$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'concurrent-proxy-cleanup.ps1')

# A disposable child is used to test the timeout cleanup without depending on
# Chromium. It is not evidence that any CEF descendants have terminated.
$executable = (Get-Command powershell.exe -ErrorAction Stop).Source
$process = Start-Process -FilePath $executable -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', '"Start-Sleep -Seconds 60"') -WindowStyle Hidden -PassThru
[void]$process.Handle
$other = $null
try {
    $other = Start-Process -FilePath $executable -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', '"Start-Sleep -Seconds 60"') -WindowStyle Hidden -PassThru
    [void]$other.Handle
    if ($process.WaitForExit(100)) { throw 'The synthetic timeout child exited before the timeout.' }
    if (-not (Stop-TestOwnedProxyProcess -Process $process)) { throw 'Timed-out test-owned child did not terminate.' }
    if (-not $process.HasExited) { throw 'Timed-out test-owned child remains alive.' }
    $other.Refresh()
    if ($other.HasExited) { throw 'Cleanup affected the independent test child.' }
    if (-not (Stop-TestOwnedProxyProcess -Process $process)) { throw 'Cleanup of an exited child was not idempotent.' }
    Write-Output 'PASS: test-owned child cleanup leaves an independent child running'
}
finally {
    $cleanupFailed = $false
    if ($null -ne $other) {
        if (-not (Stop-TestOwnedProxyProcess -Process $other)) { $cleanupFailed = $true }
        $other.Dispose()
    }
    if (-not (Stop-TestOwnedProxyProcess -Process $process)) { $cleanupFailed = $true }
    $process.Dispose()
    if ($cleanupFailed) { throw 'Synthetic test child cleanup failed.' }
}

# A real launcher must reject processes that exit without the CEF evidence.
# Windows PowerShell is deliberately not a CEF test executable; it exits
# without the required markers. Both child processes remain test-owned.
$oldTemp = $env:RUNNER_TEMP
$env:RUNNER_TEMP = Join-Path $env:TEMP ('aegis-proxy-negative-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $env:RUNNER_TEMP -ErrorAction Stop | Out-Null
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'concurrent-proxy-probes.ps1') -BrowserTestsExe $executable *> $null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Concurrent launcher accepted unexpected early peer exits.' }
    Write-Output 'PASS: concurrent launcher rejects unexpected peer exits'
}
finally { $env:RUNNER_TEMP = $oldTemp }
