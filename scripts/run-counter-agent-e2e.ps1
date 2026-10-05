# Starts the counter agent for end-to-end tests: receipts and the customer display go to files, the scale is
# simulated, and a fresh pairing token is written for the test to read. Used by tests/end-to-end/playwright.config.ts.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-counter-agent-e2e.ps1
. "$PSScriptRoot\lib\Common.ps1"
$root = Get-RepoRoot
Set-Location $root

$auth = Join-Path $root 'tests\end-to-end\.auth'
New-Item -ItemType Directory -Force -Path $auth | Out-Null
$receipts = Join-Path $auth 'receipts.bin'
$display = Join-Path $auth 'display.bin'
foreach ($file in @($receipts, $display)) {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
}
# Offline bills and price list (D-039): a fresh, test-only folder each run.
$offline = Join-Path $auth 'offline'
if (Test-Path -LiteralPath $offline) { Remove-Item -LiteralPath $offline -Recurse -Force }

$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
Set-Content -LiteralPath (Join-Path $auth 'agent-token.txt') -Value $token -NoNewline -Encoding ascii

$config = @{
    Agent = @{
        Url = 'http://127.0.0.1:47800'
        AllowedOrigins = @('http://localhost:3100')
        PairingToken = $token
        Printer = @{ Transport = 'File'; FilePath = $receipts; Columns = 48 }
        DrawerEnabled = $true
        Scale = @{ Transport = 'Simulated'; SimulatedWeightKg = 0.750 }
        Display = @{ Transport = 'File'; FilePath = $display; Columns = 20 }
        OfflineDirectory = $offline
    }
}
$configPath = Join-Path $auth 'counter-agent.json'
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8

$env:SB_COUNTER_AGENT_CONFIG = $configPath
Write-Step 'Starting the counter agent (end-to-end) on http://127.0.0.1:47800'
& dotnet run --project src/SupermarketBilling.CounterAgent --no-launch-profile
