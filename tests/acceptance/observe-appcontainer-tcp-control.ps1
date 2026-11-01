param([Parameter(Mandatory = $true)][string] $BrowserTestsExe, [switch] $RunOnDisposableRunner)

$ErrorActionPreference = 'Stop'
if (-not $RunOnDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Explicit opt-in on a disposable GitHub-hosted runner is required.'
}
function Assert-NoLink([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked path rejected.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required.' }
Assert-NoLink $env:RUNNER_TEMP
$source = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Synthetic test executable not found.' }
Assert-NoLink $source
$sourceDir = Split-Path -Parent $source
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force -Recurse) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked build output rejected.' }
}
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/AppContainerLaunchResearch.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ContainedCefLauncher.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/IndependentTcpObserver.cs')
$testRoot = Join-Path $env:RUNNER_TEMP ('aegis-tcp-control-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $testRoot) { throw 'Synthetic root already exists.' }
$null = New-Item -ItemType Directory -Path $testRoot
$fixture = Join-Path $testRoot 'fixture'
$normalRoot = Join-Path $testRoot 'normal-control'
$profile = Join-Path $testRoot 'appcontainer-control'
foreach ($path in @($fixture,$normalRoot,$profile)) { $null = New-Item -ItemType Directory -Path $path }
Assert-NoLink $testRoot
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $fixture -Recurse }
$name = 'AegisTcpResearch.' + [guid]::NewGuid().ToString('N')
$sid = [AegisContainmentResearch.SuspendedSyntheticProcess]::RegisterSyntheticIdentity($name)
$identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
function Grant-TestAccess([string] $Path, [System.Security.AccessControl.FileSystemRights] $Rights) {
    Assert-NoLink $Path
    $acl = Get-Acl -LiteralPath $Path
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, $Rights,
        ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Grant-TestAccess $testRoot ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
Grant-TestAccess $profile ([System.Security.AccessControl.FileSystemRights]::Modify)
$exe = Join-Path $fixture 'AegisBrowser.BrowserTests.exe'
$observer = New-Object AegisContainmentResearch.IndependentTcpObserver
$child = $null
$result = [ordered]@{
    formatVersion = 1
    outcome = 'FIXTURE_FAILURE'
    profileSid = $sid
    requestedCapabilities = 'none'
    pid = $null
    creationUtc = $null
    transactionId = $null
    addressFamily = 'IPv4'
    protocol = 'TCP'
    destination = '127.0.0.1'
    port = $observer.Port
    wfpFilter = $null
    observer = 'separate loopback listener; no independent OS process-to-packet attribution'
    identityAttribution = 'unverified'
    wfpDecision = 'not-observed'
    normalPositiveControl = 'not-executed'
    normalPid = $null
    normalCreationUtc = $null
    observedConnections = 0
    clientResult = 'missing'
    exitCode = $null
    nativeError = $null
    cleanup = 'not-completed'
}
try {
    $ordinary = [guid]::NewGuid().ToString('N')
    $observer.Register($ordinary)
    [IO.File]::WriteAllLines((Join-Path $normalRoot 'packet-control.txt'), @([string]$observer.Port, $ordinary))
    $process = Start-Process -FilePath $exe -ArgumentList @('packet-control-client', ('"{0}"' -f $normalRoot)) `
        -WorkingDirectory $fixture -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $result.normalPid = $process.Id
    $result.normalCreationUtc = $process.StartTime.ToUniversalTime().ToString('O')
    if (-not $process.WaitForExit(15000)) {
        Write-Output 'FIXTURE_FAILURE: normal synthetic client timed out; stopping only its owned process.'
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Owned normal-client process did not stop.' }
        throw 'Independent normal-client positive control timed out.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw 'Independent normal-client positive control failed.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($observer.Received($ordinary) -eq 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    if ($observer.Error -or $observer.Unregistered -ne 0 -or $observer.Received($ordinary) -ne 1) { throw 'Observer positive control failed or was ambiguous.' }
    $result.normalPositiveControl = 'observed'
    Write-Output 'OBSERVED: independent receiver got one correlated TCP control from a separate normal process'

    $transaction = [guid]::NewGuid().ToString('N')
    $result.transactionId = $transaction
    $observer.Register($transaction)
    [IO.File]::WriteAllLines((Join-Path $profile 'packet-control.txt'), @([string]$observer.Port, $transaction))
    try { $child = [AegisContainmentResearch.ContainedCefLauncher]::StartPacketControl($name, $exe, $profile) }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -isnot [ComponentModel.Win32Exception]) { throw }
        $result.outcome = 'ENVIRONMENT_UNAVAILABLE'
        $result.nativeError = $cause.NativeErrorCode
        Write-Output ("ENVIRONMENT_UNAVAILABLE: suspended client creation failed, Win32 error {0}" -f $cause.NativeErrorCode)
        return
    }
    try { $token = $child.Verify($sid) }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -is [ComponentModel.Win32Exception]) { $result.nativeError = $cause.NativeErrorCode }
        $result.outcome = 'INCONCLUSIVE_IDENTITY'
        Write-Output 'INCONCLUSIVE: suspended synthetic client token could not be verified; it was not resumed.'
        return
    }
    $result.pid = $child.Id
    $result.creationUtc = $child.CreationUtc()
    Write-Output ("OBSERVED: synthetic client pid={0} created={1} verified AppContainer SID={2} before resume" -f $child.Id, $child.CreationUtc(), $token)
    $child.Resume()
    if (-not $child.Wait(15000)) {
        $result.outcome = 'NO_PACKET_OBSERVED_INCONCLUSIVE'
        Write-Output 'INCONCLUSIVE: synthetic AppContainer client timed out; terminating the owned root process.'
        $child.StopTimedOutTest()
        return
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($observer.Received($transaction) -eq 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
    if ($observer.Error) { throw ('Independent receiver failure: ' + $observer.Error) }
    if ($observer.Unregistered -ne 0) { throw 'Unexpected or malformed messages reached the synthetic receiver.' }
    $count = $observer.Received($transaction)
    $exitCode = $child.ExitCode()
    $outcome = if ($count -eq 1 -and $exitCode -eq 0) { 'ALLOWED_AND_INDEPENDENTLY_OBSERVED' }
        elseif ($count -eq 1) { 'FIXTURE_FAILURE' }
        else { 'NO_PACKET_OBSERVED_INCONCLUSIVE' }
    if ($count -gt 1) { throw 'Ambiguous duplicate transaction observed.' }
    $result.outcome = $outcome
    $result.observedConnections = $count
    $result.exitCode = $exitCode
    Write-Output ("OBSERVATION: AppContainer TCP classification={0}; independently observed matches={1}; exit={2}" -f $outcome, $count, $exitCode)
    if ($outcome -eq 'FIXTURE_FAILURE') { throw 'The AppContainer client reported failure after the receiver saw its transaction.' }
    if ($outcome -eq 'NO_PACKET_OBSERVED_INCONCLUSIVE') {
        Write-Output 'INCONCLUSIVE: AppContainer capability, loopback restrictions, fixture failure or another cause can prevent this control; no WFP decision was observed.'
    }
    Write-Output 'INCONCLUSIVE: no WFP policy, packet-to-process attribution, IPv6, UDP or network containment was tested.'
}
finally {
    $cleanupErrors = @()
    try { if ($null -ne $child) { $child.Dispose() } }
    catch { $cleanupErrors += ('synthetic process cleanup: ' + $_.Exception.Message) }
    try { $observer.Dispose() }
    catch { $cleanupErrors += ('observer cleanup: ' + $_.Exception.Message) }
    try {
        $clientResultPath = Join-Path $profile 'packet-result.txt'
        if (Test-Path -LiteralPath $clientResultPath -PathType Leaf) {
            $item = Get-Item -LiteralPath $clientResultPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -gt 128) {
                $result.clientResult = 'invalid'
            } else {
                $candidate = [IO.File]::ReadAllText($clientResultPath)
                if ($candidate -match '^(sent|unavailable:(IOException|SocketException|UnauthorizedAccessException|OperationCanceledException)|socket-error:[0-9]{1,10})$') {
                    $result.clientResult = $candidate
                } else { $result.clientResult = 'invalid' }
            }
        }
    } catch { $result.clientResult = 'unreadable' }
    if ($cleanupErrors.Count) {
        $result.cleanup = 'failed'
        $result.outcome = 'FIXTURE_FAILURE'
    } else {
        $result.cleanup = 'completed'
    }
    $json = $result | ConvertTo-Json
    $json | Set-Content -LiteralPath (Join-Path $testRoot 'packet-observation.json') -Encoding UTF8
    Write-Output 'SYNTHETIC_PACKET_OBSERVATION_JSON_BEGIN'
    Write-Output $json
    Write-Output 'SYNTHETIC_PACKET_OBSERVATION_JSON_END'
    Write-Output ("OBSERVATION: synthetic test data and identity retained until disposable runner destruction: {0}" -f $testRoot)
    if ($cleanupErrors.Count) { throw ($cleanupErrors -join '; ') }
}
