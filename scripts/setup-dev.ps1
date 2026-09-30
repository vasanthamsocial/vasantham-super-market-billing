# One-time developer setup: checks tools, creates .env with random secrets, restores .NET and npm packages.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-dev.ps1
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)

Write-Step 'Checking required tools'
foreach ($tool in 'dotnet', 'node', 'npm', 'docker', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required tool '$tool' was not found on PATH. See docs\development.md."
    }
}
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^10\.')) { throw '.NET 10 SDK is required (dotnet --list-sdks).' }
$nodeMajor = [int]((& node --version).TrimStart('v').Split('.')[0])
if ($nodeMajor -lt 20) { throw "Node.js 20 or later is required (found $(& node --version))." }
Write-Host 'dotnet, node, npm, docker and git found.'

$envPath = Join-Path (Get-RepoRoot) '.env'
if (Test-Path -LiteralPath $envPath) {
    Write-Step '.env already exists - leaving it unchanged'
}
else {
    Write-Step 'Creating .env with newly generated random passwords'
    $superPw = New-RandomSecret
    $migratorPw = New-RandomSecret
    $appPw = New-RandomSecret
    $lines = foreach ($line in Get-Content -LiteralPath (Join-Path (Get-RepoRoot) '.env.example')) {
        if ($line -like 'SB_DB_SUPERUSER_PASSWORD=*') { "SB_DB_SUPERUSER_PASSWORD=$superPw" }
        elseif ($line -like 'SB_DB_MIGRATOR_PASSWORD=*') { "SB_DB_MIGRATOR_PASSWORD=$migratorPw" }
        elseif ($line -like 'SB_DB_APP_PASSWORD=*') { "SB_DB_APP_PASSWORD=$appPw" }
        elseif ($line -like 'SB_BACKUP_PASSPHRASE=*') { "SB_BACKUP_PASSPHRASE=$(New-RandomSecret 40)" }
        elseif ($line -like '*Username=sb_migrator;Password=CHANGE_ME*') { $line.Replace('Password=CHANGE_ME', "Password=$migratorPw") }
        elseif ($line -like '*Username=sb_app;Password=CHANGE_ME*') { $line.Replace('Password=CHANGE_ME', "Password=$appPw") }
        else { $line }
    }
    # UTF-8 without BOM so Docker Compose reads the first key correctly.
    [System.IO.File]::WriteAllLines($envPath, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '.env created. Passwords were generated locally and are not displayed.'
}

# Add settings introduced after .env was first created, without touching existing values.
$existingKeys = @{}
foreach ($line in Get-Content -LiteralPath $envPath) {
    $index = $line.IndexOf('=')
    if ($index -gt 0 -and -not $line.TrimStart().StartsWith('#')) { $existingKeys[$line.Substring(0, $index).Trim()] = $true }
}
$additions = @()
foreach ($line in Get-Content -LiteralPath (Join-Path (Get-RepoRoot) '.env.example')) {
    $index = $line.IndexOf('=')
    if ($index -le 0 -or $line.TrimStart().StartsWith('#')) { continue }
    $key = $line.Substring(0, $index).Trim()
    if ($existingKeys.ContainsKey($key)) { continue }
    if ($key -eq 'SB_BACKUP_PASSPHRASE') { $additions += "SB_BACKUP_PASSPHRASE=$(New-RandomSecret 40)" }
    elseif ($line.Contains('CHANGE_ME')) { throw "New secret setting '$key' needs a value; add it to .env manually." }
    else { $additions += $line }
}
if ($additions.Count -gt 0) {
    Write-Step "Adding $($additions.Count) new setting(s) to .env"
    [System.IO.File]::AppendAllText($envPath, "`n" + ($additions -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $additions | ForEach-Object { Write-Host ("  + " + $_.Substring(0, $_.IndexOf('='))) }
}

Write-Step 'Restoring .NET tools and packages'
Invoke-Native dotnet @('tool', 'restore')
Invoke-Native dotnet @('restore', 'SupermarketBilling.slnx')

Write-Step 'Installing npm workspace packages'
Invoke-Native npm @('install', '--no-fund')

Write-Host ''
Write-Host 'Setup complete. Next: scripts\db-up.ps1, then scripts\db-migrate.ps1.' -ForegroundColor Green
