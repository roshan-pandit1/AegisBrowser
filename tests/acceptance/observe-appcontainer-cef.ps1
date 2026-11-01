param(
    [Parameter(Mandatory = $true)] [string] $BrowserTestsExe,
    [switch] $RunOnDisposableRunner
)
$ErrorActionPreference = 'Stop'
if (-not $RunOnDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This experiment requires explicit opt-in on a disposable GitHub-hosted runner.'
}
function Assert-NoLink([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path rejected: $($item.FullName)" }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required.' }
Assert-NoLink $env:RUNNER_TEMP
$source = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Built browser test executable is missing.' }
Assert-NoLink $source
$sourceDir = Split-Path -Parent $source
$testRoot = Join-Path $env:RUNNER_TEMP ('aegis-cef-container-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $testRoot) { throw 'Synthetic root already exists.' }
$null = New-Item -ItemType Directory -Path $testRoot
$fixtureDir = Join-Path $testRoot 'fixture'
$profile = Join-Path $testRoot 'profile'
$null = New-Item -ItemType Directory -Path $fixtureDir
$null = New-Item -ItemType Directory -Path $profile
Assert-NoLink $testRoot
# Reject source links before copying; never change ACLs on the original build output.
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force -Recurse) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked build output is not accepted.' }
}
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force) {
    Copy-Item -LiteralPath $entry.FullName -Destination $fixtureDir -Recurse
}
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/AppContainerLaunchResearch.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ContainedCefLauncher.cs')
$name = 'AegisCefResearch.' + [guid]::NewGuid().ToString('N')
$sid = [AegisContainmentResearch.SuspendedSyntheticProcess]::RegisterSyntheticIdentity($name)
Write-Output ("OBSERVED: disposable AppContainer identity {0} SID={1}" -f $name, $sid)
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
# Read/execute only for the freshly copied fixture; write access only to its new profile.
Grant-TestAccess $testRoot ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
Grant-TestAccess $profile ([System.Security.AccessControl.FileSystemRights]::Modify)
$exe = Join-Path $fixtureDir 'AegisBrowser.BrowserTests.exe'
$child = $null
try {
    try { $child = [AegisContainmentResearch.ContainedCefLauncher]::Start($name, $exe, $profile) }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -isnot [ComponentModel.Win32Exception]) { throw }
        Write-Output ("INCONCLUSIVE: suspended CEF creation failed: {0}; Win32 error {1}" -f $cause.Message, $cause.NativeErrorCode)
        return
    }
    $wrongRejected = $false
    try { $null = $child.Verify($sid + '-wrong') } catch { $wrongRejected = $true }
    if (-not $wrongRejected) { throw 'Wrong expected SID was accepted.' }
    $resumeRejected = $false
    try { $child.Resume() } catch { $resumeRejected = $true }
    if (-not $resumeRejected) { throw 'An unverified CEF fixture was resumed.' }
    Write-Output 'PASS: wrong SID and unverified resume were rejected'
    $observed = $child.Verify($sid)
    $revoked = $false
    try { $null = $child.Verify($sid + '-wrong') } catch { $revoked = $true }
    if (-not $revoked) { throw 'Invalid re-verification did not fail.' }
    $resumeRejected = $false
    try { $child.Resume() } catch { $resumeRejected = $true }
    if (-not $resumeRejected) { throw 'Failed re-verification did not revoke resume permission.' }
    $observed = $child.Verify($sid)
    Write-Output 'PASS: failed re-verification revokes resume permission'
    Write-Output ("OBSERVED: suspended CEF fixture PID={0} created={1} token SID={2}; verification completed before resume" -f $child.Id, $child.CreationUtc(), $observed)
    $child.Resume()
    if (-not $child.Wait(90000)) {
        Write-Output 'INCONCLUSIVE: CEF fixture timed out; terminating only the owned synthetic test process.'
        $child.StopTimedOutTest()
        return
    }
    $code = $child.ExitCode()
    Write-Output ("OBSERVED: synthetic CEF process exit code={0}" -f $code)
    $allStages = $true
    foreach ($phase in @('started', 'cef-initialized', 'browser-ready', 'navigated', 'shutdown')) {
        $present = Test-Path -LiteralPath (Join-Path $profile $phase)
        if (-not $present) { $allStages = $false }
        Write-Output ("OBSERVED: phase {0}={1}" -f $phase, $present)
    }
    $failure = Join-Path $profile 'failure.txt'
    if (Test-Path -LiteralPath $failure) { Write-Output ('OBSERVED: failure: ' + (Get-Content -LiteralPath $failure -Raw)) }
    if ($code -eq 0 -and $allStages) {
        Write-Output 'PASS: actual CEF fixture initialized, navigated the synthetic handler and shut down under the verified identity'
    } else {
        if ($code -ne 0) { Write-Output 'OBSERVED FAIL: the CEF fixture exited unsuccessfully in this exact test configuration; cause is unverified.' }
        else { Write-Output 'INCONCLUSIVE: the process exited without all required stage markers.' }
        $cefLog = Join-Path $profile 'cef-appcontainer.log'
        if (Test-Path -LiteralPath $cefLog -PathType Leaf) {
            Write-Output 'Synthetic CEF log tail:'
            Get-Content -LiteralPath $cefLog -Tail 60 | Write-Output
        } else { Write-Output 'INCONCLUSIVE: synthetic CEF log unavailable.' }
    }
}
finally {
    if ($null -ne $child) { $child.Dispose() }
    Write-Output ("OBSERVATION: synthetic fixture and per-user identity retained on disposable runner: {0}" -f $testRoot)
}
