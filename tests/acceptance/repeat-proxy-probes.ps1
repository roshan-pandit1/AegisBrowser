param(
    [Parameter(Mandatory = $true)] [string] $BrowserTestsExe,
    [ValidateRange(0, 20)] [int] $SequentialRuns = 1,
    [ValidateRange(0, 20)] [int] $ConcurrentRuns = 0
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'concurrent-proxy-cleanup.ps1')
if ($SequentialRuns -eq 0 -and $ConcurrentRuns -eq 0) { throw 'Select at least one run.' }
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required for disposable profiles and logs.' }
$executable = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Browser test executable is missing.' }
$root = Join-Path $env:RUNNER_TEMP ('aegis-proxy-stress-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$summaryLog = Join-Path $root 'stress-summary.log'
function Write-StressEvent([string] $message) {
    Add-Content -LiteralPath $summaryLog -Value $message -Encoding UTF8 -ErrorAction Stop
    Write-Output $message
}
Write-Output "P409_STRESS: directory=$root sequential=$SequentialRuns concurrent_pairs=$ConcurrentRuns"
$oldTemp = $env:RUNNER_TEMP
$oldBarrier = $env:AEGIS_TEST_PROXY_BARRIER
$completedSingle = 0
$completedPairs = 0
try {
    # A single run must not accidentally inherit an unrelated concurrent barrier.
    $env:AEGIS_TEST_PROXY_BARRIER = $null
    for ($i = 1; $i -le $SequentialRuns; $i++) {
        $directory = Join-Path $root ('single-{0:D2}' -f $i)
        New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
        $profile = Join-Path $directory 'profile'
        $out = Join-Path $directory 'stdout.log'
        $err = Join-Path $directory 'stderr.log'
        $process = $null
        $recorded = $false
        try {
            $process = Start-Process -FilePath $executable -ArgumentList @('proxy-probe', ('"{0}"' -f $profile)) `
                -WorkingDirectory (Split-Path -Parent $executable) -RedirectStandardOutput $out -RedirectStandardError $err `
                -WindowStyle Hidden -PassThru
            [void]$process.Handle
            if (-not $process.WaitForExit(180000)) {
                Write-StressEvent "P409_STRESS_RESULT: kind=single iteration=$i outcome=TIMEOUT exit=unavailable"
                $recorded = $true
                throw "Single proxy probe $i timed out; logs are in $directory."
            }
            $process.WaitForExit()
            $body = [string](Get-Content -Raw -LiteralPath $out) + "`n" + [string](Get-Content -Raw -LiteralPath $err)
            $exitStatus = $process.ExitCode
            $required = @(
                'PASS: CEF uses an unauthenticated SOCKS5 tunnel and supplies the controlled destination hostname',
                'PASS: CEF reports a non-cancellation load error after the controlled SOCKS5 listener stops',
                'PASS: controlled SOCKS5 denial reply was sent',
                "PASS: explicit SOCKS rejection produces Chromium's SOCKS connection failure",
                'PASS: explicit SOCKS rejection did not reach the independently reachable origin',
                'PASS: a deliberately canceled SOCKS navigation is reported as Aborted',
                'PASS: CEF proxy probe shutdown completed'
            )
            $missing = @($required | Where-Object { $body -notmatch [regex]::Escape($_) })
            if ($null -eq $exitStatus -or $exitStatus -ne 0 -or $body -match 'Check failed:' -or
                $body -notmatch 'PASS: \d+ CEF proxy probe checks' -or $missing.Count -gt 0) {
                Write-StressEvent "P409_STRESS_RESULT: kind=single iteration=$i outcome=FAILED exit=$exitStatus"
                $recorded = $true
                throw "Single proxy probe $i failed or missed required evidence; logs are in $directory."
            }
            $completedSingle++
            Write-StressEvent "P409_STRESS_RESULT: kind=single iteration=$i outcome=PASS exit=$exitStatus"
            $recorded = $true
        }
        catch {
            if (-not $recorded) {
                Write-StressEvent "P409_STRESS_RESULT: kind=single iteration=$i outcome=INFRASTRUCTURE_FAILURE exit=unavailable"
            }
            throw
        }
        finally {
            if ($null -ne $process -and -not (Stop-TestOwnedProxyProcess -Process $process)) {
                throw "Could not clean up test-owned single probe $i; logs are in $directory."
            }
        }
    }

    for ($i = 1; $i -le $ConcurrentRuns; $i++) {
        $directory = Join-Path $root ('pair-{0:D2}' -f $i)
        New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
        $env:RUNNER_TEMP = $directory
        $log = Join-Path $directory 'launcher.log'
        Set-Content -LiteralPath $log -Value '' -Encoding UTF8
        try {
            # The launcher owns both retained process handles, bounds its wait,
            # checks both exit codes and performs its own failure cleanup.
            & (Join-Path $PSScriptRoot 'concurrent-proxy-probes.ps1') -BrowserTestsExe $executable *>&1 |
                Out-File -FilePath $log -Encoding UTF8
            $body = [string](Get-Content -Raw -LiteralPath $log)
            if ($body -notmatch 'PASS: two independent CEF proxy fixture processes completed concurrently') {
                throw 'Concurrent launcher returned without its success marker.'
            }
            $completedPairs++
            Write-StressEvent "P409_STRESS_RESULT: kind=pair iteration=$i outcome=PASS exit=0"
        }
        catch {
            $_ | Out-String | Add-Content -LiteralPath $log
            Write-StressEvent "P409_STRESS_RESULT: kind=pair iteration=$i outcome=FAILED exit=unavailable"
            throw "Concurrent proxy pair $i failed; logs are in $directory."
        }
    }
}
finally {
    $env:RUNNER_TEMP = $oldTemp
    $env:AEGIS_TEST_PROXY_BARRIER = $oldBarrier
    Write-StressEvent "P409_STRESS_SUMMARY: single_pass=$completedSingle pair_pass=$completedPairs logs=$root"
}
