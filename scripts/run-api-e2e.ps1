# Starts the API for end-to-end tests against a freshly recreated database (supermarketbilling_e2e) on port 5181.
# Never touches the development database. Used by tests/end-to-end/playwright.config.ts.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-api-e2e.ps1
. "$PSScriptRoot\lib\Common.ps1"
$root = Get-RepoRoot
Set-Location $root
Import-DotEnv

$database = 'supermarketbilling_e2e'
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

$setupCodeFile = Join-Path $root 'tests\end-to-end\.auth\setup-code.txt'
if (Test-Path -LiteralPath $setupCodeFile) { [System.IO.File]::Delete($setupCodeFile) }

$env:ConnectionStrings__Main = $env:ConnectionStrings__Main -replace 'Database=[^;]+', "Database=$database"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5181'
$env:Security__SetupCodeFile = $setupCodeFile
$env:RateLimiting__AuthPermitPerMinute = '1000'
# Messages go to the simulator (no real WhatsApp or SMS), sent every few seconds.
$env:Messaging__WhatsApp__Provider = 'Simulated'
$env:Messaging__Sms__Provider = 'Simulated'
$env:Messaging__DispatchIntervalSeconds = '5'

Write-Step 'Starting API on http://localhost:5181'
Invoke-Native dotnet @('run', '--project', 'src/SupermarketBilling.Api', '--no-launch-profile')
