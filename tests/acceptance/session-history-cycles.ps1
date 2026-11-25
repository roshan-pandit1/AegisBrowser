param(
    [Parameter(Mandatory=$true)][string]$BrowserTestsExe,
    [ValidateRange(20, 100)][int]$Cycles = 20
)
$ErrorActionPreference = 'Stop'
$cycleRoot = Join-Path ([IO.Path]::GetTempPath()) ('aegis-history-cycles-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $cycleRoot | Out-Null
$oldMode = $env:AEGIS_HISTORY_LIFECYCLE_ONLY
$oldScreenshot = $env:AEGIS_HISTORY_SCREENSHOT
$passed = 0
try {
    $env:AEGIS_HISTORY_LIFECYCLE_ONLY = '1'
    $env:AEGIS_HISTORY_SCREENSHOT = '0'
    for ($i = 1; $i -le $Cycles; $i++) {
        $profileRoot = Join-Path $cycleRoot ('cycle-' + $i.ToString('D3'))
        $output = & $BrowserTestsExe history-ui $profileRoot 2>&1
        $code = $LASTEXITCODE
        $text = $output -join "`n"
        if ($code -ne 0 -or $text -match 'Check failed:|FATAL:|ASSERTION FAILED' -or
            $text -notmatch 'PASS: history fixture lease released' -or
            $text -notmatch 'PASS: CEF shutdown completed \(history\)') {
            $output | Where-Object { $_ -match '^FAIL:|TimeoutException:|Check failed:|FATAL:|ASSERTION FAILED' } |
                ForEach-Object { [regex]::Replace([string]$_, 'https?://\S+', '[redacted]') }
            throw "History lifecycle cycle $i failed; exit code $code."
        }
        $passed++
        Write-Output "PASS: lifecycle cycle $i of $Cycles"
    }
    Write-Output "PASS: $passed fresh-process history lifecycle cycles; fixture roots: $cycleRoot"
} finally {
    $env:AEGIS_HISTORY_LIFECYCLE_ONLY = $oldMode
    $env:AEGIS_HISTORY_SCREENSHOT = $oldScreenshot
}
