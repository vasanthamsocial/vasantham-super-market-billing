# Applies EF Core migrations using the migrator (schema-owner) account and exports the idempotent SQL script.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-migrate.ps1 [-Target Dev|Test] [-ScriptOnly]
param(
    [ValidateSet('Dev', 'Test')][string]$Target = 'Dev',
    [switch]$ScriptOnly
)
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

$project = 'src/SupermarketBilling.Infrastructure'
Invoke-Native dotnet @('tool', 'restore')

Write-Step 'Exporting idempotent migration script to database/migrations/supermarketbilling.sql'
Invoke-Native dotnet @('ef', 'migrations', 'script', '--idempotent', '--project', $project,
    '--output', 'database/migrations/supermarketbilling.sql')

if ($ScriptOnly) { return }

$connection = if ($Target -eq 'Test') { $env:ConnectionStrings__TestMigrator } else { $env:ConnectionStrings__Migrator }
if (-not $connection) { throw "Migrator connection string for target '$Target' is not set in .env." }

Write-Step "Applying migrations to the $Target database"
Invoke-Native dotnet @('ef', 'database', 'update', '--project', $project, '--connection', $connection)
Write-Host "Migrations applied to $Target database." -ForegroundColor Green
