# Runs every SQL file in database/verification against the development database.
# Each file raises an exception when a check fails, so any failure stops the script with a non-zero exit code.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-database.ps1 [-Target Dev|Test]
param([ValidateSet('Dev', 'Test')][string]$Target = 'Dev')
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

$database = if ($Target -eq 'Test') { "$($env:SB_DB_NAME)_test" } else { $env:SB_DB_NAME }
$files = Get-ChildItem -LiteralPath (Join-Path (Get-RepoRoot) 'database\verification') -Filter '*.sql' | Sort-Object Name

foreach ($file in $files) {
    Write-Step "Verifying $($file.Name) on $database"
    Get-Content -LiteralPath $file.FullName -Raw |
        & docker exec -i supermarketbilling-db psql --quiet -v ON_ERROR_STOP=1 `
            -U $env:SB_DB_SUPERUSER -d $database `
            -v app_role=$env:SB_DB_APP_USER -v migrator_role=$env:SB_DB_MIGRATOR_USER -f -
    if ($LASTEXITCODE -ne 0) { throw "Database verification failed in $($file.Name)." }
}
Write-Host "All database verification checks passed on $database." -ForegroundColor Green
