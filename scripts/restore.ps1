# Restores a backup as a database. The backup is restored and verified in a staging database first.
# If the target already exists it is RENAMED (kept as <name>_pre_restore_<time>), never deleted.
# Stop the API before replacing the live database.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore.ps1 -File <backup.sbbak> -Target <new_database>
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore.ps1 -File <backup.sbbak> -Target supermarketbilling -Replace -ConfirmDatabase supermarketbilling
param(
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string]$Target,
    [switch]$Replace,
    [string]$ConfirmDatabase
)
. "$PSScriptRoot\lib\Common.ps1"
. "$PSScriptRoot\lib\Backup.ps1"
Set-Location (Get-RepoRoot)
Import-DotEnv

$backup = Resolve-BackupFile $File
$arguments = @('restore', '--file', $backup, '--target', $Target)
if ($Replace) {
    if ($ConfirmDatabase -ne $Target) {
        throw "Replacing '$Target' requires -ConfirmDatabase $Target."
    }
    $arguments += @('--replace', '--confirm', $ConfirmDatabase)
}

Write-Step "Restoring $backup as '$Target'"
Invoke-BackupTool $arguments
