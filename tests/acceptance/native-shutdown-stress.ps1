param(
    [Parameter(Mandatory = $true)][string]$BrowserTestsExe,
    [ValidateRange(1, 100)][int]$Attempts = 3,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180,
    [string]$EvidenceRoot = ''
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $BrowserTestsExe).Path
$root = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('aegis-shutdown-stress-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $root) { throw 'Evidence root must be a new, disposable directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
Write-Output "Synthetic evidence root: $root"

function Invoke-Phase([string]$phase, [string]$profileRoot, [string]$label, [string]$marker) {
    $stdout = Join-Path $root "$label.stdout.log"
    $stderr = Join-Path $root "$label.stderr.log"
    $process = Start-Process -FilePath $exe -ArgumentList @($phase, ('"{0}"' -f $profileRoot)) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        # Retain the process handle so Windows PowerShell can read the exit code after exit.
        $null = $process.Handle
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "$label timed out after $TimeoutSeconds seconds; inspect $root"
        }
        $code = $process.ExitCode
        $output = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
        $nativeLogs = @(Get-ChildItem -LiteralPath $profileRoot -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue)
        foreach ($log in $nativeLogs) { $output += "`n" + (Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue) }
        if ($code -ne 0 -or $output -notmatch $marker -or $output -match 'Check failed:|\bFATAL\b') {
            throw "$label failed: exit=$code, completion marker=$($output -match $marker); inspect $root"
        }
        Write-Output "PASS: $label exited 0 and reported completion; no native assertion or fatal log found"
    } finally { $process.Dispose() }
}

for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    $windowRoot = Join-Path $root "window-$attempt"
    Invoke-Phase 'hwnd-repro' $windowRoot "window-$attempt" 'PASS: native window candidate reproduction completed and CEF shut down'
    $browserRoot = Join-Path $root "browser-$attempt"
    foreach ($phase in @('write', 'read', 'read', 'read')) {
        $label = "browser-$attempt-$phase-" + [guid]::NewGuid().ToString('N')
        Invoke-Phase $phase $browserRoot $label "PASS: CEF shutdown completed \($phase\)"
        $suiteOutput = Get-Content -LiteralPath (Join-Path $root "$label.stdout.log") -Raw
        if ($suiteOutput -notmatch "PASS: \d+ browser integration checks \($phase\)") { throw "$label did not complete the integration suite; inspect $root" }
    }
}
Write-Output "PASS: $Attempts stress attempts completed; logs retained at $root"
