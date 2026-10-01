# Runs the counter agent on this counter PC (receipt printer, cash drawer, scale, customer display; D-018).
# First run creates %LOCALAPPDATA%\SupermarketBilling\counter-agent.json with a pairing token and every device off:
# edit it to set the printer, drawer, scale and display, add the billing address to AllowedOrigins, then enter the
# token in the POS (F11, Counter hardware). An installable Windows service comes with the Stage 17 release package.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-counter-agent.ps1
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Write-Step 'Starting the counter agent on http://127.0.0.1:47800'
& dotnet run --project src/SupermarketBilling.CounterAgent --no-launch-profile
