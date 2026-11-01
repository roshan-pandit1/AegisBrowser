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
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $env:RUNNER_TEMP ('aegis-private-' + $id)
if (Test-Path -LiteralPath $root) { throw 'Synthetic root already exists.' }
$null = New-Item -ItemType Directory -Path $root
$fixture = Join-Path $root 'fixture'
$client = Join-Path $root ('client-' + $id)
$null = New-Item -ItemType Directory -Path $fixture
$null = New-Item -ItemType Directory -Path $client
Assert-NoLink $root
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $fixture -Recurse }
$name = 'AegisPrivateNetworkResearch.' + $id
$sid = [AegisContainmentResearch.SuspendedSyntheticProcess]::RegisterSyntheticIdentity($name)
$identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
$acl = Get-Acl -LiteralPath $root
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute),
    ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
    [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $root -AclObject $acl
$exe = Join-Path $fixture 'AegisBrowser.BrowserTests.exe'
$child = $null
try {
    $badRootRejected = $false
    $unexpectedChild = $null
    try { $unexpectedChild = [AegisContainmentResearch.ContainedCefLauncher]::StartPrivateNetworkControl($name, $exe, $fixture) } catch { $badRootRejected = $true }
    if ($null -ne $unexpectedChild) { $unexpectedChild.Dispose() }
    if (-not $badRootRejected) { throw 'Mismatched synthetic root was accepted.' }
    $badExeRejected = $false
    $unexpectedChild = $null
    try { $unexpectedChild = [AegisContainmentResearch.ContainedCefLauncher]::StartPrivateNetworkControl($name, $source, $client) } catch { $badExeRejected = $true }
    if ($null -ne $unexpectedChild) { $unexpectedChild.Dispose() }
    if (-not $badExeRejected) { throw 'Executable outside the owned fixture was accepted.' }
    $child = [AegisContainmentResearch.ContainedCefLauncher]::StartPrivateNetworkControl($name, $exe, $client)
    if ($child.ExpectedCapabilitySid -ne 'S-1-15-3-3') { throw 'Unexpected private-network capability SID.' }
    $wrongRejected = $false
    try { $null = $child.Verify($sid + '-wrong') } catch { $wrongRejected = $true }
    if (-not $wrongRejected) { throw 'Wrong expected identity was accepted.' }
    $resumeRejected = $false
    try { $child.Resume() } catch { $resumeRejected = $true }
    if (-not $resumeRejected) { throw 'Unverified suspended process was resumed.' }
    $observed = $child.Verify($sid)
    if ($observed -ne $sid) { throw 'Observed package SID differs.' }
    Write-Output ("PASS: suspended synthetic PID={0} package SID={1} and sole enabled capability SID={2} verified" -f $child.Id, $observed, $child.ExpectedCapabilitySid)
    # Re-verification failure must revoke permission to resume.
    $wrongRejected = $false
    try { $null = $child.Verify($sid + '-wrong') } catch { $wrongRejected = $true }
    if (-not $wrongRejected) { throw 'Wrong re-verification was accepted.' }
    $resumeRejected = $false
    try { $child.Resume() } catch { $resumeRejected = $true }
    if (-not $resumeRejected) { throw 'Failed re-verification did not revoke resume permission.' }
    Write-Output 'PASS: invalid paths and identities rejected; unverified or revoked process cannot resume'
    Write-Output 'INCONCLUSIVE: token verification did not attempt a connection or prove network containment.'
} finally {
    if ($null -ne $child) { $child.Dispose() }
    Write-Output ("OBSERVATION: test-owned suspended process disposed without resume; synthetic files and identity retained until runner destruction: {0}" -f $root)
}
