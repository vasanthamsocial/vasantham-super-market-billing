# Starts the Firebase backend for end-to-end tests: the Functions and Firestore emulators with an empty database and
# fresh test secrets (setup code, data key). Nothing reaches the real Firebase project. Used by
# tests/end-to-end/playwright.config.ts when SB_BACKEND=firebase.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-firebase-e2e.ps1
. "$PSScriptRoot\lib\Common.ps1"
$root = Get-RepoRoot
$firebase = Join-Path $root 'firebase'
$functions = Join-Path $firebase 'functions'
Set-Location $firebase

# The Firestore emulator needs Java.
$jdk = Get-ChildItem 'C:\Program Files\Microsoft' -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($jdk) {
    $env:JAVA_HOME = $jdk.FullName
    $env:Path = "$($jdk.FullName)\bin;$env:Path"
}

# Test-only secrets, written where the emulator reads them (git-ignored). The setup code is also left for the tests.
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 16
$random.GetBytes($bytes)
$code = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
$setupCode = ($code.Substring(0, 4), $code.Substring(4, 4), $code.Substring(8, 4), $code.Substring(12, 4)) -join '-'
$key = New-Object byte[] 32
$random.GetBytes($key)
Set-Content -LiteralPath (Join-Path $functions '.secret.local') -Encoding ascii -Value @(
    "SB_SETUP_CODE=$setupCode",
    "SB_DATA_KEY=$([Convert]::ToBase64String($key))"
)
Set-Content -LiteralPath (Join-Path $functions '.env.local') -Encoding ascii -Value @(
    'SB_SECURE_COOKIES=false',
    'SB_AUTH_RATE_LIMIT_PER_MINUTE=1000',
    'SB_MAX_BUSINESSES=3'
)
$auth = Join-Path $root 'tests\end-to-end\.auth'
New-Item -ItemType Directory -Force -Path $auth | Out-Null
Set-Content -LiteralPath (Join-Path $auth 'setup-code.txt') -Value $setupCode -NoNewline -Encoding ascii

Write-Step 'Building the functions'
Invoke-Native npm @('--prefix', $functions, 'run', 'build')

Write-Step 'Starting the Firebase emulators (Functions :5001, Firestore :8085), empty database'
Invoke-Native firebase.cmd @('emulators:start', '--only', 'functions,firestore', '--project', 'vasantham-billings')
