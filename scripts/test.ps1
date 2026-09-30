# Runs the automated test suites.
#   Default: .NET unit + integration tests (integration needs the database) and TypeScript type checks.
#   -E2E:    also runs Playwright browser tests (starts the API and all web apps if not already running).
#   -UnitOnly: .NET unit tests only; no database needed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1 [-E2E] [-UnitOnly]
param([switch]$E2E, [switch]$UnitOnly)
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
$env:NEXT_TELEMETRY_DISABLED = '1'

if ($UnitOnly) {
    Write-Step '.NET unit tests'
    Invoke-Native dotnet @('test', 'tests/unit/SupermarketBilling.UnitTests', '--nologo')
    return
}

Import-DotEnv

Write-Step 'Applying migrations to the test database'
& "$PSScriptRoot\db-migrate.ps1" -Target Test

Write-Step '.NET unit and integration tests'
Invoke-Native dotnet @('test', 'SupermarketBilling.slnx', '--nologo')

Write-Step 'Database verification (development and test databases)'
& "$PSScriptRoot\verify-database.ps1" -Target Dev
& "$PSScriptRoot\verify-database.ps1" -Target Test

Write-Step 'TypeScript type checks'
Invoke-Native npm @('run', 'typecheck')

if ($E2E) {
    Write-Step 'Playwright end-to-end tests'
    Invoke-Native npm @('run', 'test:e2e')
}

Write-Host ''
Write-Host 'All requested test suites passed.' -ForegroundColor Green
