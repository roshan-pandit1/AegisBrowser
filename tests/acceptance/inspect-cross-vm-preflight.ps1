# P3-08: read-only observations from one approved, disposable, isolated Windows VM.
# Does not send packets, install policy, alter firewall/routes, or authorize the network experiment.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('VmA','VmB')][string] $Role,
    [Parameter(Mandatory = $true)][string] $LocalAddress,
    [Parameter(Mandatory = $true)][string] $PeerAddress,
    [Parameter(Mandatory = $true)][ValidateRange(1024,65535)][int] $Port,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [switch] $AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') {
    throw 'Explicit disposable isolated-VM acknowledgement and AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND are required.'
}
if ($env:OS -cne 'Windows_NT') { throw 'This inspection requires Windows.' }
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
Assert-CrossVmEndpoints -LocalAddress $LocalAddress -PeerAddress $PeerAddress -Port $Port

# The destination must be a new directory below an existing, unlinked operator-owned parent.
$root = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $root) { throw 'Evidence directory must be new; existing evidence is never overwritten.' }
$parent = Get-Item -LiteralPath (Split-Path -Parent $root) -Force
$cursor = $parent
while ($null -ne $cursor) {
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Linked evidence parent rejected.'
    }
    $cursor = $cursor.Parent
}

# All commands below query current OS configuration. In particular, do NOT test connectivity.
$addressRows = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $LocalAddress -ErrorAction SilentlyContinue)
$interfaceIndex = $null
$interfaceAlias = $null
$adapterUp = $false
$addressPreferred = $false
$category = 'Unknown'
if ($addressRows.Count -eq 1) {
    $interfaceIndex = [int] $addressRows[0].InterfaceIndex
    $interfaceAlias = [string] $addressRows[0].InterfaceAlias
    $addressPreferred = ([string] $addressRows[0].AddressState -ceq 'Preferred')
    $adapter = Get-NetAdapter -InterfaceIndex $interfaceIndex -ErrorAction Stop
    $adapterUp = ([string] $adapter.Status -ceq 'Up')
    $profiles = @(Get-NetConnectionProfile -InterfaceIndex $interfaceIndex -ErrorAction SilentlyContinue)
    if ($profiles.Count -eq 1) { $category = [string] $profiles[0].NetworkCategory }
}
$allRoutes = @(Get-NetRoute -ErrorAction Stop)
$routes4 = @($allRoutes | Where-Object { $_.DestinationPrefix -ceq '0.0.0.0/0' })
$routes6 = @($allRoutes | Where-Object { $_.DestinationPrefix -ceq '::/0' })
$gatewayRoutes = @($allRoutes | Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' -and $_.NextHop -ne '::' })
$adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
$otherActive = @($adapters | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceIndex -ne $interfaceIndex })
$allAddresses = @(Get-NetIPAddress -ErrorAction Stop)
$otherIPv6 = @(Get-NetIPAddress -AddressFamily IPv6 -ErrorAction Stop | Where-Object {
    $_.IPAddress -ne '::1' -and $_.IPAddress -notlike 'fe80:*' -and $_.IPAddress -notlike 'fe80*'
})
$firewalls = @(Get-NetFirewallProfile -ErrorAction Stop)
$firewallEnabled = ($firewalls.Count -eq 3 -and @($firewalls | Where-Object { -not $_.Enabled }).Count -eq 0)
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop

$issues = @(Get-CrossVmPreflightBlockers -LocalAddressMatches $addressRows.Count -NetworkCategory $category -DefaultIPv4Routes $routes4.Count -DefaultIPv6Routes $routes6.Count -NonLinkLocalIPv6Addresses $otherIPv6.Count -FirewallEnabled $firewallEnabled -AdapterUp $adapterUp -AddressPreferred $addressPreferred -OtherActiveAdapters $otherActive.Count -GatewayRoutes $gatewayRoutes.Count)
$status = if ($issues.Count -gt 0) { 'BLOCKED_PRECHECK' } else { 'REVIEW_REQUIRED' }
$record = [ordered]@{
    schemaVersion = 2
    status = $status
    observedUtc = [DateTime]::UtcNow.ToString('O')
    role = $Role
    localAddress = $LocalAddress
    peerAddress = $PeerAddress
    port = $Port
    host = [ordered]@{
        osVersion = [string] $os.Version
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    network = [ordered]@{
        localAddressMatches = $addressRows.Count
        interfaceIndex = $interfaceIndex
        interfaceAlias = $interfaceAlias
        adapterUp = $adapterUp
        addressPreferred = $addressPreferred
        networkCategory = $category
        defaultIPv4RouteCount = $routes4.Count
        defaultIPv6RouteCount = $routes6.Count
        nonLinkLocalIPv6AddressCount = $otherIPv6.Count
        allFirewallProfilesEnabled = $firewallEnabled
        otherActiveAdapterCount = $otherActive.Count
        gatewayRouteCount = $gatewayRoutes.Count
        adapters = @($adapters | Select-Object Name, InterfaceDescription, InterfaceIndex, Status, MacAddress)
        addresses = @($allAddresses | Select-Object InterfaceIndex, InterfaceAlias, AddressFamily, IPAddress, PrefixLength, AddressState)
        routes = @($allRoutes | Select-Object InterfaceIndex, DestinationPrefix, NextHop, RouteMetric, PolicyStore)
        firewallProfiles = @($firewalls | Select-Object Name, Enabled)
    }
    blockers = @($issues)
    limitations = @(
        'Snapshot is not atomic; repeat and compare before and after testing. On-link routes and hidden or inactive VPN configurations require operator review.'
        'Read-only local snapshot: no reachability probe, packet capture, WFP rule or filter decision.'
        'No verification of virtual-switch isolation, hypervisor topology, firewall rule contents, external routing or independent peer identity.'
        'REVIEW_REQUIRED does not mean network isolation is verified or authorize running the TCP controls.'
        'IPv4-only controls do not establish IPv6, UDP, DNS, browser egress or fail-closed containment.'
    )
    productionPolicyChanged = $false
    networkTrafficGenerated = $false
}
$null = New-Item -ItemType Directory -Path $root -ErrorAction Stop
$file = Join-Path $root 'vm-network-preflight.json'
$bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
$stream = New-Object IO.FileStream($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output ("{0}: saved read-only local network snapshot: {1}" -f $status, $file)
Write-Output 'Independent two-VM isolation and authorization review is mandatory. No TCP or WFP test was run.'
