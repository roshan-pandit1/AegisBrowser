# Research-only receiver for a pre-provisioned, isolated two-VM network.
# This script installs no firewall rules and must not be used on a production network.
param(
    [Parameter(Mandatory = $true)][string] $BindAddress,
    [Parameter(Mandatory = $true)][string] $ExpectedClientAddress,
    [Parameter(Mandatory = $true)][ValidateRange(1024,65535)][int] $Port,
    [Parameter(Mandatory = $true)][guid] $RunId,
    [Parameter(Mandatory = $true)][guid] $NormalNonce,
    [Parameter(Mandatory = $true)][guid] $ContainedNonce,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [ValidateRange(10,600)][int] $TimeoutSeconds = 300,
    [switch] $AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') {
    throw 'Explicit disposable isolated-VM acknowledgement and AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND are required.'
}
function ConvertTo-PrivateIPv4([string] $InputAddress) {
    $address = $null
    if (-not [Net.IPAddress]::TryParse($InputAddress, [ref] $address) -or
        $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        $address.ToString() -cne $InputAddress) { throw 'A canonical private IPv4 literal is required.' }
    $bytes = $address.GetAddressBytes()
    if (-not ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) {
        throw 'Only RFC1918 IPv4 destinations are accepted.'
    }
    return $address
}
$bind = ConvertTo-PrivateIPv4 $BindAddress
$expectedClient = ConvertTo-PrivateIPv4 $ExpectedClientAddress
if ($bind.Equals($expectedClient) -or $RunId -eq [guid]::Empty -or $NormalNonce -eq [guid]::Empty -or $ContainedNonce -eq [guid]::Empty -or $NormalNonce -eq $ContainedNonce -or $RunId -eq $NormalNonce -or $RunId -eq $ContainedNonce) {
    throw 'Two distinct VM addresses and three distinct nonempty run identifiers are required.'
}
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidenceRoot) { throw 'Evidence directory must be new.' }
$parent = Get-Item -LiteralPath (Split-Path -Parent $evidenceRoot) -Force
$cursor = $parent
while ($null -ne $cursor) {
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked evidence path rejected.' }
    $cursor = $cursor.Parent
}
$null = New-Item -ItemType Directory -Path $evidenceRoot
$listener = New-Object Net.Sockets.TcpListener($bind, $Port)
$seen = @{}
$normal = $NormalNonce.ToString('N')
$contained = $ContainedNonce.ToString('N')
$pendingRecord = $null
$pendingUntil = [DateTime]::MinValue
try {
    $listener.Start()
    Write-Output ("READY: independent receiver {0}:{1}; expected source {2}; no WFP or attribution instrumentation" -f $bind, $Port, $expectedClient)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (($seen.Count -lt 2 -or $null -ne $pendingRecord) -and [DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $pendingRecord -and [DateTime]::UtcNow -ge $pendingUntil -and -not $listener.Pending()) {
            $name = if ($pendingRecord.transactionId -ceq $normal) { 'ordinary-observation.json' } else { 'appcontainer-observation.json' }
            $path = Join-Path $evidenceRoot $name
            $encoded = [Text.Encoding]::UTF8.GetBytes(($pendingRecord | ConvertTo-Json -Compress))
            $file = New-Object IO.FileStream($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try { $file.Write($encoded, 0, $encoded.Length); $file.Flush($true) } finally { $file.Dispose() }
            Write-Output ("OBSERVED: {0}; one matching transaction in the bounded observation window; process attribution remains unverified." -f $name)
            $pendingRecord = $null
            continue
        }
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout = 3000
            $remote = [Net.IPEndPoint] $client.Client.RemoteEndPoint
            if (-not $remote.Address.Equals($expectedClient)) { throw 'Unexpected client address.' }
            $stream = $client.GetStream()
            $bytes = New-Object byte[] 33
            $count = 0
            while ($count -lt $bytes.Length) {
                $n = $stream.Read($bytes, $count, $bytes.Length - $count)
                if ($n -eq 0) { break }
                $count += $n
            }
            if ($count -ne 33 -or $bytes[32] -ne 10 -or $stream.ReadByte() -ne -1) { throw 'Malformed transaction.' }
            $nonce = [Text.Encoding]::ASCII.GetString($bytes, 0, 32)
            if (($nonce -cne $normal -and $nonce -cne $contained) -or $seen.ContainsKey($nonce)) { throw 'Unexpected or repeated transaction.' }
            if ($null -ne $pendingRecord) { throw 'Next transaction arrived before the previous observation window completed.' }
            if ($nonce -ceq $contained -and -not $seen.ContainsKey($normal)) { throw 'Contained attempt preceded the ordinary positive control.' }
            $seen[$nonce] = $true
            $pendingRecord = [ordered]@{
                formatVersion = 2; runId = $RunId.ToString('N'); overallStatus = 'INCONCLUSIVE'; classification = 'ALLOWED_AND_INDEPENDENTLY_OBSERVED';
                transactionId = $nonce; receiverAddress = $bind.ToString(); receiverPort = $Port;
                remoteAddress = $remote.Address.ToString(); remotePort = $remote.Port;
                observedUtc = [DateTime]::UtcNow.ToString('O'); transport = 'IPv4/TCP';
                observedMatches = 1; duplicateObservationWindowSeconds = 2;
                duplicateCoverage = 'bounded-window-only'; processAttribution = 'unverified'; wfpDecision = 'not-observed'
            }
            $pendingUntil = [DateTime]::UtcNow.AddSeconds(2)
        } finally { $client.Dispose() }
    }
    if ($null -ne $pendingRecord) { throw 'Receiver timed out before the bounded observation window completed.' }
    if (-not $seen.ContainsKey($normal)) { throw 'Ordinary positive control missing: environment unavailable or inconclusive.' }
    if (-not $seen.ContainsKey($contained)) { Write-Output 'INCONCLUSIVE: no AppContainer transaction was received before timeout.' }
} finally { $listener.Stop() }
