# Verifies a backup by restoring it into a temporary database, comparing every table's row count with the
# backup manifest and running database verification. The temporary database is then dropped.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-test.ps1 [-File <backup.sbbak>] [-VerifyOnly]
#   Without -File the newest backup in SB_BACKUP_DIR is used. -VerifyOnly checks checksum and encryption without restoring.
param([string]$File, [switch]$VerifyOnly)
. "$PSScriptRoot\lib\Common.ps1"
. "$PSScriptRoot\lib\Backup.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

$backup = Resolve-BackupFile $File
if ($VerifyOnly) {
    Write-Step "Verifying $backup"
    Invoke-BackupTool @('verify', '--file', $backup)
}
else {
    Write-Step "Restore-testing $backup"
    Invoke-BackupTool @('restore-test', '--file', $backup)
}
