# Runs one of the Next.js applications in development mode.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-web.ps1 -App billing-web
param(
    [Parameter(Mandatory)]
    [ValidateSet('billing-web', 'owner-dashboard', 'collection-app', 'owner-archive-web')]
    [string]$App
)
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

if ($App -eq 'owner-archive-web' -and $env:ARCHIVE_WEB_ENABLED -ne 'true') {
    Write-Host 'Owner Archive Web is a licensed optional feature and ARCHIVE_WEB_ENABLED is not "true" in .env.' -ForegroundColor Yellow
    Write-Host 'Set ARCHIVE_WEB_ENABLED=true in .env to start it.'
    exit 1
}

$env:NEXT_TELEMETRY_DISABLED = '1'
Invoke-Native npm @('run', 'dev', '--workspace', "apps/$App")
