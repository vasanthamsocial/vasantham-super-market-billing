# Starts the development PostgreSQL 16 container(s) and waits until they are healthy.
# Starts Docker Desktop first if the Docker engine is not running.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-up.ps1 [-Archive]
param([switch]$Archive)
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

function Test-DockerEngine {
    # Relax Stop locally: Windows PowerShell 5.1 turns native stderr output into terminating errors.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker info --format '{{.ServerVersion}}' *> $null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# Docker Desktop does not start with Windows on this machine; start it and wait for the engine if needed.
if (-not (Test-DockerEngine)) {
    $dockerDesktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path -LiteralPath $dockerDesktop)) { throw 'Docker engine is not running and Docker Desktop was not found.' }
    Write-Step 'Starting Docker Desktop'
    Start-Process -FilePath $dockerDesktop
    $deadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 3
        $running = Test-DockerEngine
    } while (-not $running -and (Get-Date) -lt $deadline)
    if (-not $running) { throw 'Docker engine did not start within 3 minutes.' }
    Write-Host 'Docker engine is running.' -ForegroundColor Green
}

$containers = @('supermarketbilling-db')
if ($Archive) {
    Write-Step 'Starting operational and archive databases'
    Invoke-Native docker @('compose', '--profile', 'archive', 'up', '-d', 'db', 'archive-db')
    $containers += 'supermarketbilling-archive-db'
}
else {
    Write-Step 'Starting operational database'
    Invoke-Native docker @('compose', 'up', '-d', 'db')
}

foreach ($container in $containers) {
    $deadline = (Get-Date).AddSeconds(120)
    do {
        $status = (& docker inspect --format '{{.State.Health.Status}}' $container)
        if ($status -eq 'healthy') { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if ($status -ne 'healthy') {
        & docker logs --tail 50 $container
        throw "$container did not become healthy (status: $status)."
    }
    Write-Host "$container is healthy." -ForegroundColor Green
}
