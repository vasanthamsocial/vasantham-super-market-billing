# Production build: publishes the API and builds the four web apps as standalone Node servers,
# then writes release/SHA256SUMS.txt covering every file in the release folder.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-production.ps1
. "$PSScriptRoot\lib\Common.ps1"
$root = Get-RepoRoot
Set-Location $root
$env:NEXT_TELEMETRY_DISABLED = '1'
$release = Join-Path $root 'release'

if (Test-Path -LiteralPath $release) {
    Remove-Item -LiteralPath $release -Recurse -Force
}
New-Item -ItemType Directory -Path $release | Out-Null

Write-Step 'Publishing API (Release, framework-dependent, .NET 10)'
Invoke-Native dotnet @('publish', 'src/SupermarketBilling.Api', '-c', 'Release', '-o', (Join-Path $release 'api'), '--nologo')

Write-Step 'Publishing backup tool (sb-backup)'
Invoke-Native dotnet @('publish', 'src/SupermarketBilling.BackupTool', '-c', 'Release', '-o', (Join-Path $release 'tools\sb-backup'), '--nologo')

Write-Step 'Exporting idempotent database migration script'
Invoke-Native dotnet @('tool', 'restore')
Invoke-Native dotnet @('ef', 'migrations', 'script', '--idempotent', '--project', 'src/SupermarketBilling.Infrastructure',
    '--output', (Join-Path $release 'database\supermarketbilling-migrations.sql'))
Copy-Item -Recurse -LiteralPath (Join-Path $root 'database\verification') -Destination (Join-Path $release 'database\verification')

foreach ($app in 'billing-web', 'owner-dashboard', 'collection-app', 'owner-archive-web') {
    Write-Step "Building $app"
    Invoke-Native npm @('run', 'build', '--workspace', "apps/$app")
    $appDir = Join-Path $root "apps\$app"
    $target = Join-Path $release "web\$app"
    # Standalone output mirrors the monorepo layout; static assets must be copied next to server.js.
    Copy-Item -Recurse -LiteralPath (Join-Path $appDir '.next\standalone') -Destination $target
    $serverAppDir = Join-Path $target "apps\$app"
    Copy-Item -Recurse -LiteralPath (Join-Path $appDir '.next\static') -Destination (Join-Path $serverAppDir '.next\static')
    if (Test-Path -LiteralPath (Join-Path $appDir 'public')) {
        Copy-Item -Recurse -LiteralPath (Join-Path $appDir 'public') -Destination (Join-Path $serverAppDir 'public')
    }
}

Write-Step 'Writing SHA-256 manifest'
$manifest = Join-Path $release 'SHA256SUMS.txt'
$lines = Get-ChildItem -LiteralPath $release -Recurse -File |
    Where-Object { $_.FullName -ne $manifest } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = $_.FullName.Substring($release.Length + 1).Replace('\', '/')
        "$hash  $relative"
    }
[System.IO.File]::WriteAllLines($manifest, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Production build complete: $release ($($lines.Count) files checksummed)." -ForegroundColor Green
