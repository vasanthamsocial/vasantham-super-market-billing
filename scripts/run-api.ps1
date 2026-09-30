# Runs the ASP.NET Core API on http://localhost:5080 with settings from .env.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-api.ps1 [-Watch]
param([switch]$Watch)
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

$arguments = @('--project', 'src/SupermarketBilling.Api', '--launch-profile', 'http')
if ($Watch) {
    Invoke-Native dotnet (@('watch', 'run') + $arguments)
}
else {
    Invoke-Native dotnet (@('run') + $arguments)
}
