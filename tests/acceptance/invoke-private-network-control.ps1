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
    [string] $SigningIdentity,
    [string] $TrustManifest,
    [string] $TrustManifestSha256,
    [string] $OrdinarySignedPayload,
    [string] $OrdinarySignedSignature,
    [switch] $AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') {
    throw 'Explicit disposable isolated-VM acknowledgement and AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND are required.'
}
. (Join-Path $PSScriptRoot 'private-network-evidence.ps1')
if (-not [string]::IsNullOrWhiteSpace($SigningIdentity)) {
    . (Join-Path $PSScriptRoot 'p308c3-fixture-lib.ps1')
    $signingKey=Open-P308c3SigningKey -Role VmA -Identity $SigningIdentity
    try { Test-P308c3SigningChallenge $signingKey } finally { $signingKey.Dispose() }
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
function Get-SyntheticClientResult([string] $Root) {
    $path = Join-Path $Root 'private-network-result.txt'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return 'missing' }
    try {
        Assert-NoLink $path
        if ((Get-Item -LiteralPath $path).Length -gt 128) { return 'invalid' }
        $result = [IO.File]::ReadAllText($path)
        if ($result -cmatch '^(sent|unavailable:(InvalidDataException|IOException|SocketException|UnauthorizedAccessException|OperationCanceledException)|socket-error:[0-9]{1,10})$') { return $result }
        return 'invalid'
    } catch { return 'unreadable' }
}
Assert-PrivateIPv4 $ReceiverAddress
Assert-PrivateIPv4 $ClientAddress
if ($ReceiverAddress -ceq $ClientAddress -or $RunId -eq [guid]::Empty -or $NormalNonce -eq [guid]::Empty -or $ContainedNonce -eq [guid]::Empty -or $NormalNonce -eq $ContainedNonce -or $RunId -eq $NormalNonce -or $RunId -eq $ContainedNonce) {
    throw 'Distinct VM addresses and three distinct nonempty run identifiers are required.'
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
    $processCreated = $process.StartTime.ToUniversalTime().ToString('O')
    $timedOut = $false
    if (-not $process.WaitForExit(15000)) {
        $timedOut = $true
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Owned synthetic ordinary client did not terminate.' }
    }
    $process.Refresh()
    $clientRecord = [ordered]@{ formatVersion = 2; runId = $id; overallStatus = 'INCONCLUSIVE'; stage = 'Ordinary'; transactionId = $NormalNonce.ToString('N');
        receiverAddress = $ReceiverAddress; receiverPort = $Port; clientAddress = $ClientAddress; processId = $process.Id;
        processCreatedUtc = $processCreated; completedUtc = [DateTime]::UtcNow.ToString('O'); exitCode = $process.ExitCode;
        timedOut = $timedOut; clientResult = (Get-SyntheticClientResult $normalRoot); receiverObservation = 'not-evaluated'; processAttribution = 'unverified'; wfpDecision = 'not-observed' }
    Write-NewFile (Join-Path $root 'ordinary-client.json') ($clientRecord | ConvertTo-Json -Compress)
    if ($SigningIdentity) {
        $fields=ConvertTo-P308c3ClientFields -Record $clientRecord
        Write-P308c3FixtureRecord -Fields $fields -Role VmA -Identity $SigningIdentity -OutputPrefix (Join-Path $root 'ordinary-client')
    }
    if ($timedOut) { throw 'Ordinary client timed out. Do not proceed until receiver evidence exists.' }
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
Assert-OrdinaryReceiverEvidence -Evidence $observation -RunId $RunId -Nonce $NormalNonce -ReceiverAddress $ReceiverAddress -ClientAddress $ClientAddress -Port $Port
$ordinaryClient = Join-Path $root 'ordinary-client.json'
Assert-NoLink $ordinaryClient
if ((Get-Item -LiteralPath $ordinaryClient).Length -gt 4096) { throw 'Ordinary client evidence is oversized.' }
$ordinaryRecord = Get-Content -LiteralPath $ordinaryClient -Raw | ConvertFrom-Json
if (-not (Test-JsonInteger $ordinaryRecord.formatVersion) -or $ordinaryRecord.formatVersion -ne 2 -or $ordinaryRecord.runId -cne $id -or $ordinaryRecord.overallStatus -cne 'INCONCLUSIVE' -or $ordinaryRecord.stage -cne 'Ordinary' -or $ordinaryRecord.transactionId -cne $NormalNonce.ToString('N') -or
    $ordinaryRecord.receiverAddress -cne $ReceiverAddress -or -not (Test-JsonInteger $ordinaryRecord.receiverPort) -or $ordinaryRecord.receiverPort -ne $Port -or $ordinaryRecord.clientAddress -cne $ClientAddress -or
    -not (Test-JsonInteger $ordinaryRecord.exitCode) -or $ordinaryRecord.exitCode -ne 0 -or $ordinaryRecord.clientResult -cne 'sent' -or $ordinaryRecord.timedOut -isnot [bool] -or $ordinaryRecord.timedOut) {
    throw 'Ordinary client did not complete successfully for this run.'
}
if ($SigningIdentity) {
    if (-not $TrustManifest -or -not $TrustManifestSha256 -or -not $OrdinarySignedPayload -or -not $OrdinarySignedSignature) { throw 'Signed ordinary receiver evidence and independently pinned trust manifest are required before AppContainer execution.' }
    . (Join-Path $PSScriptRoot 'p308c2-file-lib.ps1')
    . (Join-Path $PSScriptRoot 'p308c3-operator-lib.ps1')
    $trust=Read-OperatorManifest $TrustManifest $TrustManifestSha256 @('protocol','version','roleA','statusA','algorithmA','fingerprintA','modulusA','exponentA','roleB','statusB','algorithmB','fingerprintB','modulusB','exponentB')
    if ($trust.protocol -cne 'AegisOperatorTrust' -or $trust.version -cne '1' -or $trust.roleA -cne 'VmA' -or $trust.roleB -cne 'VmB' -or $trust.statusA -cne 'active' -or $trust.statusB -cne 'active' -or $trust.algorithmA -cne 'RSA-PKCS1-SHA256' -or $trust.algorithmB -cne 'RSA-PKCS1-SHA256') { throw 'UNTRUSTED_KEY' }
    $localKey=$null; $keyA=$null; $keyB=$null
    try {
        $localKey=Open-P308c3SigningKey -Identity $SigningIdentity -Role VmA
        $keyA=Import-ApprovedPublicKey $trust.modulusA $trust.exponentA
        $keyB=Import-ApprovedPublicKey $trust.modulusB $trust.exponentB
        if ((Get-P308c2Fingerprint $localKey) -cne $trust.fingerprintA) { throw 'UNTRUSTED_KEY' }
        $signed=Import-P308c2FileRecord $OrdinarySignedPayload $OrdinarySignedSignature
        Assert-P308c3PriorOrdinary -Payload $signed.Payload -Signature $signed.Signature -KeyA $keyA -PinA $trust.fingerprintA -KeyB $keyB -PinB $trust.fingerprintB -Run $RunId -Transaction $NormalNonce -Client $ClientAddress -Receiver $ReceiverAddress -Port $Port
    } finally { if ($localKey) { $localKey.Dispose() }; if ($keyA) { $keyA.Dispose() }; if ($keyB) { $keyB.Dispose() } }
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
    $created = $child.CreationUtc()
    $child.Resume()
    $timedOut = -not $child.Wait(15000)
    if ($timedOut) { $child.StopTimedOutTest() }
    $exitCode = $child.ExitCode()
    $clientRecord = [ordered]@{ formatVersion = 2; runId = $id; overallStatus = 'INCONCLUSIVE'; stage = 'AppContainer'; transactionId = $ContainedNonce.ToString('N');
        receiverAddress = $ReceiverAddress; receiverPort = $Port; clientAddress = $ClientAddress; processId = $child.Id;
        processCreatedUtc = $created; completedUtc = [DateTime]::UtcNow.ToString('O'); exitCode = $exitCode;
        timedOut = $timedOut; clientResult = (Get-SyntheticClientResult $clientRoot); packageSid = $token; capabilitySid = $child.ExpectedCapabilitySid;
        tokenVerifiedBeforeResume = $true; resumed = $true; receiverObservation = 'not-evaluated'; processAttribution = 'unverified'; wfpDecision = 'not-observed' }
    Write-NewFile (Join-Path $root 'appcontainer-client.json') ($clientRecord | ConvertTo-Json -Compress)
    if ($SigningIdentity) {
        $fields=ConvertTo-P308c3ClientFields -Record $clientRecord
        Write-P308c3FixtureRecord -Fields $fields -Role VmA -Identity $SigningIdentity -OutputPrefix (Join-Path $root 'appcontainer-client')
    }
    if ($timedOut) { throw 'AppContainer client timed out; receiver absence is inconclusive.' }
    if ($exitCode -ne 0) { throw 'AppContainer client failed; receiver absence is inconclusive.' }
    Write-Output 'CLIENT_SENT: independent receiver evidence is still required; nonce correlation does not attribute the flow to a process.'
} finally { if ($null -ne $child) { $child.Dispose() } }
