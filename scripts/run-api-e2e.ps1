# Starts the API for end-to-end tests against a freshly recreated database (supermarketbilling_e2e) on port 5181.
# With -Archive: the archive server instead (archive mode, ARCHIVE_WEB_ENABLED), database supermarketbilling_archive_e2e,
# port 5182. Never touches the development database. Used by tests/end-to-end/playwright.config.ts.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-api-e2e.ps1 [-Archive]
param([switch]$Archive)
. "$PSScriptRoot\lib\Common.ps1"
$root = Get-RepoRoot
Set-Location $root
Import-DotEnv

$database = if ($Archive) { 'supermarketbilling_archive_e2e' } else { 'supermarketbilling_e2e' }
$port = if ($Archive) { 5182 } else { 5181 }
$migrator = $env:SB_DB_MIGRATOR_USER
$app = $env:SB_DB_APP_USER
$superuser = $env:SB_DB_SUPERUSER

function Invoke-AdminSql([string]$db, [string]$sql) {
    $sql | & docker exec -i supermarketbilling-db psql --quiet -v ON_ERROR_STOP=1 -U $superuser -d $db -f -
    if ($LASTEXITCODE -ne 0) { throw "SQL failed on $db." }
}

Write-Step "Recreating $database"
Invoke-AdminSql 'postgres' "DROP DATABASE IF EXISTS $database WITH (FORCE);"
Invoke-AdminSql 'postgres' "CREATE DATABASE $database OWNER `"$migrator`" ENCODING 'UTF8' TEMPLATE template0;"
Invoke-AdminSql $database @"
REVOKE ALL ON DATABASE $database FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE $database TO "$migrator";
GRANT CONNECT ON DATABASE $database TO "$app";
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO "$migrator";
GRANT USAGE ON SCHEMA public TO "$app";
ALTER DEFAULT PRIVILEGES FOR ROLE "$migrator" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "$app";
ALTER DEFAULT PRIVILEGES FOR ROLE "$migrator" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO "$app";
ALTER DEFAULT PRIVILEGES FOR ROLE "$migrator" IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO "$app";
"@

Write-Step "Migrating $database"
$migratorConnection = $env:ConnectionStrings__Migrator -replace 'Database=[^;]+', "Database=$database"
Invoke-Native dotnet @('tool', 'restore')
Invoke-Native dotnet @('ef', 'database', 'update', '--project', 'src/SupermarketBilling.Infrastructure', '--connection', $migratorConnection)

$auth = Join-Path $root 'tests\end-to-end\.auth'
New-Item -ItemType Directory -Force -Path $auth | Out-Null
$setupCodeFile = Join-Path $auth $(if ($Archive) { 'archive-setup-code.txt' } else { 'setup-code.txt' })
if (Test-Path -LiteralPath $setupCodeFile) { [System.IO.File]::Delete($setupCodeFile) }

$env:ConnectionStrings__Main = $env:ConnectionStrings__Main -replace 'Database=[^;]+', "Database=$database"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$port"
$env:Security__SetupCodeFile = $setupCodeFile
# Archive keys of the test servers live with the test files, never in the development App_Data; each run starts afresh.
$env:Archive__SigningKeyFile = Join-Path $auth 'store-signing-key.pem'
$env:Archive__RecipientKeyFile = Join-Path $auth 'archive-recipient-key.pem'
$ownKey = if ($Archive) { $env:Archive__RecipientKeyFile } else { $env:Archive__SigningKeyFile }
if (Test-Path -LiteralPath $ownKey) { [System.IO.File]::Delete($ownKey) }
if ($Archive) {
    $env:Archive__Server = 'true'
    $env:ARCHIVE_WEB_ENABLED = 'true'
}
$env:RateLimiting__AuthPermitPerMinute = '1000'
# Messages go to the simulator (no real WhatsApp or SMS), sent every few seconds.
$env:Messaging__WhatsApp__Provider = 'Simulated'
$env:Messaging__Sms__Provider = 'Simulated'
$env:Messaging__DispatchIntervalSeconds = '5'

Write-Step "Starting $(if ($Archive) { 'the archive server' } else { 'the API' }) on http://localhost:$port"
if ($Archive) {
    # The main API (started at the same time) builds the project; running a second build at once would collide.
    for ($i = 0; $i -lt 120; $i++) {
        try { if ((Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:5181/health/live' -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
        Start-Sleep -Seconds 2
    }
    Invoke-Native dotnet @('run', '--project', 'src/SupermarketBilling.Api', '--no-launch-profile', '--no-build')
} else {
    Invoke-Native dotnet @('run', '--project', 'src/SupermarketBilling.Api', '--no-launch-profile')
}
