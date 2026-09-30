# Shared helpers for SupermarketBilling scripts. Compatible with Windows PowerShell 5.1 and PowerShell 7.
# Dot-source it:  . "$PSScriptRoot\lib\Common.ps1"

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-RepoRoot { return $script:RepoRoot }

# Loads KEY=VALUE pairs from .env into the current process only. Values are never printed.
function Import-DotEnv {
    param([string]$Path = (Join-Path $script:RepoRoot '.env'))

    if (-not (Test-Path -LiteralPath $Path)) {
        throw ".env not found at '$Path'. Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-dev.ps1"
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $index = $trimmed.IndexOf('=')
        if ($index -lt 1) { continue }
        $name = $trimmed.Substring(0, $index).Trim()
        $value = $trimmed.Substring($index + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }

    if ($env:ConnectionStrings__Main -and $env:ConnectionStrings__Main.Contains('CHANGE_ME')) {
        throw ".env still contains CHANGE_ME placeholders. Delete .env and re-run scripts\setup-dev.ps1."
    }
}

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Runs a native command and throws if it fails (PowerShell 5.1 does not do this automatically).
function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @())
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function New-RandomSecret([int]$Length = 32) {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $builder = New-Object System.Text.StringBuilder
    $buffer = New-Object byte[] 1
    try {
        while ($builder.Length -lt $Length) {
            $rng.GetBytes($buffer)
            # Rejection sampling keeps the distribution uniform.
            $limit = 256 - (256 % $alphabet.Length)
            if ($buffer[0] -lt $limit) {
                [void]$builder.Append($alphabet[$buffer[0] % $alphabet.Length])
            }
        }
    }
    finally {
        $rng.Dispose()
    }
    return $builder.ToString()
}
