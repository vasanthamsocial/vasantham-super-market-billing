# Security audit: vulnerable .NET packages, vulnerable npm packages, and committed-secret scan.
# Exits non-zero if anything is found.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\security-audit.ps1
. "$PSScriptRoot\lib\Common.ps1"
Set-Location (Get-RepoRoot)
$failures = New-Object System.Collections.Generic.List[string]

Write-Step '.NET packages with known vulnerabilities (including transitive)'
$dotnetOutput = & dotnet list SupermarketBilling.slnx package --vulnerable --include-transitive
$dotnetOutput | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { $failures.Add('dotnet list package failed') }
elseif ($dotnetOutput -match 'has the following vulnerable packages') { $failures.Add('.NET vulnerable packages found') }

Write-Step 'npm packages with known vulnerabilities'
& npm audit --audit-level=low
if ($LASTEXITCODE -ne 0) { $failures.Add('npm audit reported vulnerabilities') }

$firebaseFunctions = Join-Path (Get-RepoRoot) 'firebase' | Join-Path -ChildPath 'functions'
if (Test-Path -LiteralPath (Join-Path $firebaseFunctions 'package-lock.json')) {
    Write-Step 'npm packages of the Firebase backend with known vulnerabilities'
    & npm --prefix $firebaseFunctions audit --audit-level=low
    if ($LASTEXITCODE -ne 0) { $failures.Add('npm audit (Firebase backend) reported vulnerabilities') }
}

Write-Step 'Scanning tracked and untracked (non-ignored) files for secrets'
$files = & git ls-files --cached --others --exclude-standard
$patterns = @(
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    # Connection-string / .env style (no spaces): Password=realvalue
    '(?i)password=(?!CHANGE_ME\b)[^\s;"''<>{}$]{8,}',
    # Code style: password = "literal"
    '(?i)password\s*=\s*"(?!CHANGE_ME")[^"]{8,}"',
    'AKIA[0-9A-Z]{16}',
    '(?i)(api[_-]?key|secret|token)\s*[:=]\s*["''][A-Za-z0-9_\-]{20,}["'']'
)
$allowList = @('scripts/security-audit.ps1')
foreach ($file in $files) {
    if ($allowList -contains $file) { continue }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    if ($file -match '\.(png|jpg|ico|pdf|zip|dll|exe)$') { continue }
    $hits = Select-String -LiteralPath $file -Pattern $patterns -ErrorAction SilentlyContinue
    foreach ($hit in $hits) {
        # Fixed credentials for throw-away test databases are allowed only under tests/ and only when marked on the same line.
        if ($file -like 'tests/*' -and $hit.Line -match 'sb-audit: test-fixture') { continue }
        $failures.Add("possible secret in ${file}:$($hit.LineNumber)")
    }
}
if (& git ls-files -- .env) { $failures.Add('.env is tracked by git') }

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host 'Security audit FAILED:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Security audit passed: no vulnerable packages and no secrets found.' -ForegroundColor Green
