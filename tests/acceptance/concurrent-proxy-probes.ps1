param(
    [Parameter(Mandatory = $true)] [string] $BrowserTestsExe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'concurrent-proxy-cleanup.ps1')
$testExecutable = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw 'Browser test executable is missing.' }
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required for disposable test profiles.' }
$runDirectory = Join-Path $env:RUNNER_TEMP ('aegis-concurrent-proxy-' + [guid]::NewGuid().ToString('N'))
$barrierDirectory = Join-Path $runDirectory 'barrier'
New-Item -ItemType Directory -Path $barrierDirectory -Force | Out-Null
$previousBarrier = $env:AEGIS_TEST_PROXY_BARRIER
$processes = @()
try {
    $env:AEGIS_TEST_PROXY_BARRIER = $barrierDirectory
    foreach ($role in @('cef-a', 'cef-b')) {
        $profile = Join-Path $runDirectory $role
        $output = Join-Path $runDirectory ($role + '.out.log')
        $errors = Join-Path $runDirectory ($role + '.err.log')
        $arguments = @('proxy-probe', ('"{0}"' -f $profile))
        $process = Start-Process -FilePath $testExecutable -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $testExecutable) `
            -RedirectStandardOutput $output -RedirectStandardError $errors -WindowStyle Hidden -PassThru
        $processes += [pscustomobject]@{ Role = $role; Process = $process; Output = $output; Errors = $errors }
        # Retain the test-owned process handle before it exits. In Windows
        # PowerShell 5.1, ExitCode can otherwise be unavailable after Refresh.
        [void]$process.Handle
    }
    if ($processes[0].Process.Id -eq $processes[1].Process.Id) { throw 'Concurrent probes did not receive distinct process IDs.' }
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($entry in $processes) { $entry.Process.Refresh() }
        if (($processes | Where-Object { -not $_.Process.HasExited }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    $failedRoles = [Collections.Generic.List[string]]::new()
    foreach ($entry in $processes) {
        $entry.Process.Refresh()
        if (-not $entry.Process.HasExited) {
            Write-Output "Concurrent proxy fixture $($entry.Role) did not exit within three minutes; available output follows:"
            foreach ($path in @($entry.Output, $entry.Errors)) {
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    try { Get-Content -LiteralPath $path -Tail 256 -ErrorAction Stop | Write-Output }
                    catch { Write-Output "P405_PROCESS_EVENT: role=$($entry.Role) state=OUTPUT_UNAVAILABLE" }
                }
            }
            Write-Output "P405_PROCESS_EVENT: role=$($entry.Role) state=OUTPUT_TAIL_ONLY"
            Write-Output "P405_PROCESS_EVENT: role=$($entry.Role) state=TIMEOUT exit=unavailable"
            $failedRoles.Add($entry.Role)
            if (Stop-TestOwnedProxyProcess -Process $entry.Process) {
                Write-Output "P409_PROCESS_EVENT: role=$($entry.Role) state=TIMED_OUT_ROOT_TERMINATED"
            }
            else {
                Write-Output "P409_PROCESS_EVENT: role=$($entry.Role) state=TIMED_OUT_ROOT_TERMINATION_FAILED"
            }
            continue
        }
        $entry.Process.WaitForExit()
        $result = (Get-Content -LiteralPath $entry.Output -Raw) + "`n" + (Get-Content -LiteralPath $entry.Errors -Raw)
        Write-Output "Concurrent proxy fixture $($entry.Role):"
        Write-Output $result
        $exitStatus = if ($null -eq $entry.Process.ExitCode) { 'unavailable' } else { [string]$entry.Process.ExitCode }
        Write-Output "P404_PROCESS_EVENT: role=$($entry.Role) exit=$exitStatus"
        if ($null -eq $entry.Process.ExitCode -or $entry.Process.ExitCode -ne 0 -or $result -match 'Check failed:' -or
            $result -notmatch 'PASS: both independent CEF proxy fixtures initialized before routing tests' -or
            $result -notmatch 'PASS: synthetic missing SOCKS callback remains a timeout' -or
            $result -notmatch 'PASS: synthetic successful offline navigation is rejected' -or
            $result -notmatch 'PASS: synthetic SOCKS trace reports dropped events' -or
            $result -notmatch "PASS: explicit SOCKS rejection produces Chromium's SOCKS connection failure" -or
            $result -notmatch 'PASS: explicit SOCKS rejection did not reach the independently reachable origin' -or
            $result -notmatch 'kind=SOCKS_REJECTION_SENT' -or
            $result -notmatch 'PASS: \d+ CEF proxy probe checks' -or
            $result -notmatch 'PASS: CEF proxy probe shutdown completed') {
            $failedRoles.Add($entry.Role)
        }
    }
    if ($failedRoles.Count -ne 0) { throw "Concurrent proxy fixtures failed or did not complete native shutdown: $($failedRoles -join ', ')." }
    Write-Output 'PASS: two independent CEF proxy fixture processes completed concurrently'
}
finally {
    $env:AEGIS_TEST_PROXY_BARRIER = $previousBarrier
    # Includes a peer that was already started if launching the other peer
    # failed. A forced stop is cleanup only, never a successful probe result.
    $cleanupFailed = $false
    foreach ($entry in $processes) {
        if (-not (Stop-TestOwnedProxyProcess -Process $entry.Process)) {
            Write-Output "P409_PROCESS_EVENT: role=$($entry.Role) state=FINAL_CLEANUP_FAILED"
            $cleanupFailed = $true
        }
    }
    if ($cleanupFailed) { throw 'A test-owned proxy probe process could not be terminated.' }
}
