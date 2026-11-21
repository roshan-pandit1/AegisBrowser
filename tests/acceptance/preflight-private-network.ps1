# Read-only local preflight for a manually authorized disposable VM. This does
# not create routes, firewall rules, sockets, WFP objects, or evidence files.
param(
    [Parameter(Mandatory = $true)][string] $LocalAddress,
    [Parameter(Mandatory = $true)][string] $PeerAddress,
    [Parameter(Mandatory = $true)][string] $AuthorizedSubnet,
    [Parameter(Mandatory = $true)][int] $Port,
    [Parameter(Mandatory = $true)][int] $MeasuredClockSkewSeconds,
    [switch] $AcknowledgeDisposableVm,
    [switch] $AcknowledgeIsolatedNetwork,
    [switch] $AcknowledgeRoutingAndFirewallPermission,
    [switch] $AcknowledgeTrustedEvidenceTransfer
)
$ErrorActionPreference = 'Stop'
if (-not ($AcknowledgeDisposableVm -and $AcknowledgeIsolatedNetwork -and
           $AcknowledgeRoutingAndFirewallPermission -and $AcknowledgeTrustedEvidenceTransfer)) {
    throw 'Explicit operator acknowledgement of each disposable VM, isolation, permission, and evidence-transfer requirement is required.'
}
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'This read-only preflight requires Windows.'
}
foreach ($command in @('Get-NetIPAddress', 'Get-NetIPInterface', 'Get-NetRoute')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw 'Required Windows networking inspection commands are unavailable.' }
}
. (Join-Path $PSScriptRoot 'private-network-preflight-lib.ps1')
$connected = @(Get-NetIPInterface -PolicyStore ActiveStore | Where-Object { $_.ConnectionState -eq 'Connected' })
$indices = @($connected | ForEach-Object { [int] $_.InterfaceIndex } | Select-Object -Unique)
if ($indices.Count -eq 0) { throw 'No connected interfaces were available for route inspection.' }
$addresses = @(Get-NetIPAddress -PolicyStore ActiveStore -AddressFamily IPv4 | ForEach-Object {
    [pscustomobject]@{ Address = $_.IPAddress; State = [string] $_.AddressState; InterfaceIndex = [int] $_.InterfaceIndex;
        InterfaceState = $(if ($indices -contains [int] $_.InterfaceIndex) { 'Up' } else { 'Down' }) }
})
$routes = @(Get-NetRoute -PolicyStore ActiveStore | Where-Object { $indices -contains [int] $_.InterfaceIndex } | ForEach-Object {
    [pscustomobject]@{ Family = [string] $_.AddressFamily; Prefix = $_.DestinationPrefix;
        InterfaceIndex = [int] $_.InterfaceIndex; NextHop = $_.NextHop }
})
Assert-PrivateNetworkPreflight -Architecture ([string] [Runtime.InteropServices.RuntimeInformation]::OSArchitecture) `
    -LocalAddress $LocalAddress -PeerAddress $PeerAddress -Subnet $AuthorizedSubnet -Port $Port `
    -MaxClockSkewSeconds $MeasuredClockSkewSeconds -Addresses $addresses -Routes $routes
[ordered]@{
    formatVersion = 1
    status = 'INCONCLUSIVE'
    observedUtc = [DateTime]::UtcNow.ToString('O')
    localAddress = $LocalAddress
    peerAddress = $PeerAddress
    authorizedSubnet = $AuthorizedSubnet
    port = $Port
    operatorReportedClockBoundSeconds = $MeasuredClockSkewSeconds
    localChecks = 'passed'
    disposableVm = 'operator-asserted-not-verified'
    networkIsolation = 'operator-asserted-not-verified'
    permissions = 'operator-asserted-not-verified'
    clockMeasurement = 'operator-reported-not-independently-verified'
    peerConfiguration = 'not-observed'
    flowOrWfpEnforcement = 'not-tested'
} | ConvertTo-Json -Compress
