# Creates an encrypted, checksummed backup and immediately proves it restores correctly.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup.ps1 [-Database <name>] [-OutDir <dir>] [-SkipRestoreTest]
#   -OutDir can point at an encrypted pendrive, for example E:\SB-Backups (never run the live database from it).
param(
    [string]$Database,
    [string]$OutDir,
    [switch]$SkipRestoreTest
)
. "$PSScriptRoot\lib\Common.ps1"
. "$PSScriptRoot\lib\Backup.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

if (-not $Database) { $Database = $env:SB_DB_NAME }
if (-not $OutDir) { $OutDir = Get-BackupDirectory }

Write-Step "Backing up database '$Database' to $OutDir"
$arguments = @('backup', '--database', $Database, '--out', $OutDir)
if ($SkipRestoreTest) { $arguments += '--no-restore-test' }
Invoke-BackupTool $arguments
Write-Host 'Backup complete.' -ForegroundColor Green
