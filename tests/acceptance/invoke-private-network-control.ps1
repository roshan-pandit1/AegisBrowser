# Research only: manually run on VM A in a pre-provisioned isolated two-VM network.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Ordinary','AppContainer')][string] $Stage,
    [Parameter(Mandatory = $true)][guid] $RunId,
    [Parameter(Mandatory = $true)][guid] $NormalNonce,
    [Parameter(Mandatory = $true)][guid] $ContainedNonce,
    [Parameter(Mandatory = $true)][string] $ReceiverAddress,
    [Parameter(Mandatory = $true)][string] $ClientAddress,
    [Parameter(Mandatory = $true)][ValidateRange(1024,65535)][int] $Port,
    [Parameter(Mandatory = $true)][string] $ParentDirectory,
    [string] $BrowserTestsExe,
    [string] $OrdinaryObservation,
    [switch] $AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') {
    throw 'Explicit disposable isolated-VM acknowledgement and AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND are required.'
}
function Assert-NoLink([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked path rejected.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
function Assert-PrivateIPv4([string] $Value) {
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref] $ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -cne $Value) {
        throw 'A canonical RFC1918 IPv4 address is required.'
    }
    $b = $ip.GetAddressBytes()
    if (-not ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))) {
        throw 'A canonical RFC1918 IPv4 address is required.'
    }
}
function Write-NewFile([string] $Path, [string] $Value) {
    $data = [Text.Encoding]::UTF8.GetBytes($Value)
    $file = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $file.Write($data, 0, $data.Length); $file.Flush($true) } finally { $file.Dispose() }
}
Assert-PrivateIPv4 $ReceiverAddress
Assert-PrivateIPv4 $ClientAddress
if ($ReceiverAddress -ceq $ClientAddress -or $RunId -eq [guid]::Empty -or $NormalNonce -eq [guid]::Empty -or $ContainedNonce -eq [guid]::Empty -or $NormalNonce -eq $ContainedNonce) {
    throw 'Distinct VM addresses, nonempty run ID and distinct nonempty nonces are required.'
}
$parent = [IO.Path]::GetFullPath($ParentDirectory)
Assert-NoLink $parent
$id = $RunId.ToString('N')
$root = Join-Path $parent ('aegis-private-' + $id)
$fixture = Join-Path $root 'fixture'
$normalRoot = Join-Path $root 'normal'
$clientRoot = Join-Path $root ('client-' + $id)
$exe = Join-Path $fixture 'AegisBrowser.BrowserTests.exe'
$run = [ordered]@{ runId = $id; receiverAddress = $ReceiverAddress; clientAddress = $ClientAddress; port = $Port; normalNonce = $NormalNonce.ToString('N'); containedNonce = $ContainedNonce.ToString('N') }
$expectedRun = $run | ConvertTo-Json -Compress
if ($Stage -eq 'Ordinary') {
    if (Test-Path -LiteralPath $root) { throw 'The synthetic run root already exists.' }
    if ([string]::IsNullOrWhiteSpace($BrowserTestsExe)) { throw 'BrowserTestsExe is required for the ordinary stage.' }
    $source = [IO.Path]::GetFullPath($BrowserTestsExe)
    if ([IO.Path]::GetFileName($source) -ine 'AegisBrowser.BrowserTests.exe') { throw 'A fixed synthetic executable is required.' }
    Assert-NoLink $source
    $sourceDir = Split-Path -Parent $source
    foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force -Recurse) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked build output rejected.' }
    }
    $null = New-Item -ItemType Directory -Path $root
    foreach ($path in @($fixture,$normalRoot,$clientRoot)) { $null = New-Item -ItemType Directory -Path $path }
    foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $fixture -Recurse }
    Write-NewFile (Join-Path $root 'run.json') $expectedRun
    Write-NewFile (Join-Path $normalRoot 'private-network-control.txt') ($ReceiverAddress + "`n" + $Port + "`n" + $NormalNonce.ToString('N') + "`n")
    $process = Start-Process -FilePath $exe -ArgumentList @('private-network-control-client', ('"{0}"' -f $normalRoot)) -WorkingDirectory $fixture -WindowStyle Hidden -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Owned synthetic ordinary client did not terminate.' }
        throw 'Ordinary client timed out. Do not proceed until receiver evidence exists.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw 'Ordinary client failed. Inspect only the synthetic result and receiver evidence.' }
    Write-Output ("ORDINARY_SENT: pid={0}; waiting for independent receiver evidence before AppContainer stage." -f $process.Id)
    Write-Output ("SYNTHETIC_ROOT: {0}" -f $root)
    return
}
Assert-NoLink $root
Assert-NoLink $exe
Assert-NoLink $clientRoot
$runFile = Join-Path $root 'run.json'
Assert-NoLink $runFile
if ((Get-Item -LiteralPath $runFile).Length -gt 4096 -or [IO.File]::ReadAllText($runFile) -cne $expectedRun) { throw 'Run configuration differs from the ordinary stage.' }
if ([string]::IsNullOrWhiteSpace($OrdinaryObservation)) { throw 'Independent ordinary-observation.json is required before this stage.' }
Assert-NoLink $OrdinaryObservation
if ((Get-Item -LiteralPath $OrdinaryObservation).Length -gt 4096) { throw 'Receiver evidence is oversized.' }
$observation = Get-Content -LiteralPath $OrdinaryObservation -Raw | ConvertFrom-Json
if ($observation.formatVersion -ne 1 -or $observation.classification -cne 'ALLOWED_AND_INDEPENDENTLY_OBSERVED' -or
    $observation.transactionId -cne $NormalNonce.ToString('N') -or $observation.receiverAddress -cne $ReceiverAddress -or
    $observation.receiverPort -ne $Port -or $observation.remoteAddress -cne $ClientAddress -or $observation.transport -cne 'IPv4/TCP' -or
    $observation.observedMatches -ne 1 -or $observation.duplicateObservationWindowSeconds -ne 2 -or $observation.duplicateCoverage -cne 'bounded-window-only' -or
    $observation.processAttribution -cne 'unverified' -or $observation.wfpDecision -cne 'not-observed') {
    throw 'Ordinary control evidence does not match this run.'
}
Write-NewFile (Join-Path $clientRoot 'private-network-control.txt') ($ReceiverAddress + "`n" + $Port + "`n" + $ContainedNonce.ToString('N') + "`n")
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/AppContainerLaunchResearch.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ContainedCefLauncher.cs')
$name = 'AegisPrivateNetworkResearch.' + $id
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
Grant-TestAccess $root ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
Grant-TestAccess $clientRoot ([System.Security.AccessControl.FileSystemRights]::Modify)
$child = $null
try {
    $child = [AegisContainmentResearch.ContainedCefLauncher]::StartPrivateNetworkControl($name, $exe, $clientRoot)
    $token = $child.Verify($sid)
    Write-Output ("VERIFIED: suspended synthetic process pid={0} created={1} package={2} and requested capability verified" -f $child.Id, $child.CreationUtc(), $token)
    $child.Resume()
    if (-not $child.Wait(15000)) { $child.StopTimedOutTest(); throw 'AppContainer client timed out; receiver absence is inconclusive.' }
    if ($child.ExitCode() -ne 0) { throw 'AppContainer client failed; receiver absence is inconclusive.' }
    Write-Output 'CLIENT_SENT: independent receiver evidence is still required; nonce correlation does not attribute the flow to a process.'
} finally { if ($null -ne $child) { $child.Dispose() } }
