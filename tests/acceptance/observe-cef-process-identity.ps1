param(
    [string] $BrowserTestsExe,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ProcessIdentityReader.cs')
$self = [AegisContainmentResearch.ProcessIdentityReader]::Observe([uint32]$PID)
if ($self.Error -or -not $self.CreationUtc -or -not $self.NativeMachine) {
    throw "Cannot read this observer's process identity: $($self.Error)"
}
$null = [AegisContainmentResearch.ProcessIdentityReader]::SnapshotDescendants([uint32]$PID)
Write-Output 'PASS: native process identity and process snapshot APIs are readable for this observer'
$invalid = [AegisContainmentResearch.ProcessIdentityReader]::Observe([uint32]::MaxValue)
if (-not $invalid.Error) { throw 'An invalid PID did not report a process identity error.' }
Write-Output 'PASS: an invalid PID is reported as unavailable'
$childProcess = New-Object System.Diagnostics.Process
$childProcess.StartInfo.FileName = $env:ComSpec
$childProcess.StartInfo.UseShellExecute = $false
$childProcess.StartInfo.CreateNoWindow = $true
$childProcess.StartInfo.RedirectStandardInput = $true
if (-not $childProcess.Start()) { throw 'Could not start the synthetic identity control process.' }
try {
    $link = @([AegisContainmentResearch.ProcessIdentityReader]::SnapshotDescendants([uint32]$PID) |
        Where-Object { $_.Id -eq $childProcess.Id -and $_.ParentId -eq $PID })
    $childIdentity = [AegisContainmentResearch.ProcessIdentityReader]::Observe([uint32]$childProcess.Id)
    if ($link.Count -ne 1 -or $childIdentity.Error -or -not $childIdentity.CreationUtc) {
        throw "Could not independently observe the synthetic child: $($childIdentity.Error)"
    }
    Write-Output 'PASS: independently observed the synthetic child process and its token'
}
finally {
    $childProcess.StandardInput.WriteLine('exit')
    $childProcess.StandardInput.Close()
    if (-not $childProcess.WaitForExit(5000)) { Write-Warning 'The synthetic control process did not exit promptly.' }
    $childProcess.Dispose()
}
if ($SelfTest) { return }

if ([string]::IsNullOrWhiteSpace($BrowserTestsExe) -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'BrowserTestsExe and RUNNER_TEMP are required.'
}
if (-not (Test-Path -LiteralPath $BrowserTestsExe -PathType Leaf)) { throw 'Browser test executable is missing.' }
$testExecutable = [IO.Path]::GetFullPath($BrowserTestsExe)
$runDirectory = Join-Path $env:RUNNER_TEMP ('aegis-identity-observation-' + [guid]::NewGuid().ToString('N'))
$barrier = Join-Path $runDirectory 'barrier'
New-Item -ItemType Directory -Path $barrier -Force | Out-Null
$output = Join-Path $runDirectory 'browser.out.log'
$errors = Join-Path $runDirectory 'browser.err.log'
$previousBarrier = $env:AEGIS_TEST_PROCESS_OBSERVATION_BARRIER
$process = $null
try {
    $env:AEGIS_TEST_PROCESS_OBSERVATION_BARRIER = $barrier
    $profile = Join-Path $runDirectory 'synthetic-profile'
    $arguments = @('proxy-probe', ('"{0}"' -f $profile))
    $process = Start-Process -FilePath $testExecutable -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $testExecutable) `
        -RedirectStandardOutput $output -RedirectStandardError $errors -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(50)
    while (-not (Test-Path -LiteralPath (Join-Path $barrier 'ready'))) {
        $process.Refresh()
        if ($process.HasExited) { throw 'The synthetic browser fixture exited before the observer barrier.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The synthetic browser fixture did not reach the observer barrier.' }
        Start-Sleep -Milliseconds 100
    }
    $root = [AegisContainmentResearch.ProcessIdentityReader]::Observe([uint32]$process.Id)
    if ($root.Error) { throw "Cannot read synthetic browser identity: $($root.Error)" }
    $children = @([AegisContainmentResearch.ProcessIdentityReader]::SnapshotDescendants([uint32]$process.Id))
    $readable = 0
    Write-Output ("OBSERVED: synthetic browser pid={0} created={1} appcontainer={2} packageSid={3} machine={4:X4}/{5:X4}" -f `
        $root.Id, $root.CreationUtc, $root.IsAppContainer, $root.PackageSid, $root.ProcessMachine, $root.NativeMachine)
    foreach ($child in $children) {
        $observation = [AegisContainmentResearch.ProcessIdentityReader]::Observe($child.Id)
        if ($observation.Error) {
            Write-Output ("INCONCLUSIVE: snapshot child pid={0} parent={1} image={2} could not be queried ({3})" -f `
                $child.Id, $child.ParentId, $child.ImageName, $observation.Error)
            continue
        }
        if ([DateTime]::Parse($observation.CreationUtc) -lt [DateTime]::Parse($root.CreationUtc)) {
            Write-Output ("INCONCLUSIVE: snapshot child pid={0} predates the observed root; the PID association may be stale" -f $child.Id)
            continue
        }
        if ($child.ImageName -ieq 'CefSharp.BrowserSubprocess.exe') { $readable++ }
        Write-Output ("OBSERVED: snapshot child pid={0} parent={1} image={2} created={3} appcontainer={4} packageSid={5} machine={6:X4}/{7:X4}" -f `
            $child.Id, $child.ParentId, $child.ImageName, $observation.CreationUtc, $observation.IsAppContainer, `
            $observation.PackageSid, $observation.ProcessMachine, $observation.NativeMachine)
    }
    if ($readable -eq 0) { throw 'No live CefSharp.BrowserSubprocess.exe identity was observable; subprocess coverage is inconclusive.' }
    Write-Output 'PASS: independent observer queried at least one synthetic CEF child identity'
    Write-Output 'INCONCLUSIVE: point-in-time parent/PID snapshots cannot prove complete subprocess coverage or network containment.'
    [IO.File]::WriteAllText((Join-Path $barrier 'release'), 'release')
    if (-not $process.WaitForExit(180000)) { throw 'Synthetic browser fixture did not exit within three minutes.' }
    $result = (Get-Content -LiteralPath $output -Raw) + "`n" + (Get-Content -LiteralPath $errors -Raw)
    Write-Output $result
    if ($process.ExitCode -ne 0 -or $result -match 'Check failed:' -or
        $result -notmatch 'PASS: \d+ CEF proxy probe checks' -or
        $result -notmatch 'PASS: CEF proxy probe shutdown completed') {
        throw 'Synthetic browser fixture failed or did not complete native shutdown.'
    }
}
finally {
    if ($null -ne $process -and -not (Test-Path -LiteralPath (Join-Path $barrier 'release'))) {
        [IO.File]::WriteAllText((Join-Path $barrier 'release'), 'release')
    }
    if ($null -ne $process -and -not $process.HasExited -and -not $process.WaitForExit(150000)) {
        Write-Warning 'Synthetic browser fixture did not exit after release; it was not forcibly terminated.'
    }
    $env:AEGIS_TEST_PROCESS_OBSERVATION_BARRIER = $previousBarrier
}
