# Shared helpers for the backup scripts. Dot-source after Common.ps1.

function Invoke-BackupTool {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $root = Get-RepoRoot
    $project = Join-Path $root 'src\SupermarketBilling.BackupTool'
    Invoke-Native dotnet @('build', $project, '-c', 'Release', '--nologo', '-v', 'q')
    $dll = Join-Path $project 'bin\Release\net10.0\sb-backup.dll'
    $verification = Join-Path $root 'database\verification'
    Invoke-Native dotnet (@($dll) + $Arguments + @('--verification-dir', $verification))
}

function Get-BackupDirectory {
    $dir = $env:SB_BACKUP_DIR
    if (-not $dir) { $dir = 'backups' }
    if (-not [System.IO.Path]::IsPathRooted($dir)) { $dir = Join-Path (Get-RepoRoot) $dir }
    return $dir
}

function Resolve-BackupFile {
    param([string]$File)
    if ($File) { return (Resolve-Path -LiteralPath $File).Path }
    $dir = Get-BackupDirectory
    $latest = Get-ChildItem -LiteralPath $dir -Filter '*.sbbak' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $latest) { throw "No backups found in $dir. Create one with scripts\backup.ps1." }
    return $latest.FullName
}
