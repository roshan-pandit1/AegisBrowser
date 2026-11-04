# Offline P3-08 regression checks. No network commands, elevation, WFP, or firewall changes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
$checks = 0
function Assert-Rejected([scriptblock] $Action, [string] $Case) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw ('Unsafe input accepted: ' + $Case) }
    $script:checks++
}
foreach ($address in @('10.0.0.1','172.16.0.1','172.31.255.254','192.168.30.4')) {
    Assert-CrossVmPrivateIPv4 $address
    $checks++
}
foreach ($address in @('8.8.8.8','127.0.0.1','169.254.1.2','224.0.0.1','172.32.1.1','192.0.2.1','::1','10.2','10.0.0.01','10.0.0.256','192.168.1.0/24')) {
    $candidate = $address
    Assert-Rejected { Assert-CrossVmPrivateIPv4 $candidate } $candidate
}
Assert-CrossVmEndpoints -LocalAddress '10.9.8.1' -PeerAddress '10.9.8.2' -Port 47123
$checks++
Assert-Rejected { Assert-CrossVmEndpoints -LocalAddress '10.9.8.1' -PeerAddress '10.9.8.1' -Port 47123 } 'same endpoints'
Assert-Rejected { Assert-CrossVmEndpoints -LocalAddress '10.9.8.1' -PeerAddress '8.8.8.8' -Port 47123 } 'public peer'
Assert-Rejected { Assert-CrossVmEndpoints -LocalAddress '10.9.8.1' -PeerAddress '10.9.8.2' -Port 80 } 'privileged port'
Assert-Rejected { Assert-CrossVmEndpoints -LocalAddress '10.9.8.1' -PeerAddress '10.9.8.2' -Port 65536 } 'out of range port'

$valid = @{
    LocalAddressMatches = 1
    NetworkCategory = 'Private'
    DefaultIPv4Routes = 0
    DefaultIPv6Routes = 0
    NonLinkLocalIPv6Addresses = 0
    FirewallEnabled = $true
    AdapterUp = $true
    AddressPreferred = $true
    OtherActiveAdapters = 0
    GatewayRoutes = 0
}
$issues = @(Get-CrossVmPreflightBlockers @valid)
if ($issues.Count -ne 0) { throw 'Minimal local snapshot rejected unexpectedly.' }
$checks++
$badCases = @(
    @{ key = 'LocalAddressMatches'; value = 0; expected = 'LOCAL_ADDRESS_NOT_UNIQUE' }
    @{ key = 'LocalAddressMatches'; value = 2; expected = 'LOCAL_ADDRESS_NOT_UNIQUE' }
    @{ key = 'NetworkCategory'; value = 'Public'; expected = 'PRIVATE_NETWORK_CATEGORY_NOT_CONFIRMED' }
    @{ key = 'NetworkCategory'; value = 'DomainAuthenticated'; expected = 'PRIVATE_NETWORK_CATEGORY_NOT_CONFIRMED' }
    @{ key = 'DefaultIPv4Routes'; value = 1; expected = 'IPV4_DEFAULT_ROUTE_PRESENT' }
    @{ key = 'DefaultIPv6Routes'; value = 1; expected = 'IPV6_DEFAULT_ROUTE_PRESENT' }
    @{ key = 'NonLinkLocalIPv6Addresses'; value = 1; expected = 'NON_LINK_LOCAL_IPV6_PRESENT' }
    @{ key = 'FirewallEnabled'; value = $false; expected = 'FIREWALL_PROFILE_DISABLED_OR_UNAVAILABLE' }
    @{ key = 'AdapterUp'; value = $false; expected = 'LOCAL_ADAPTER_NOT_UP' }
    @{ key = 'AddressPreferred'; value = $false; expected = 'LOCAL_ADDRESS_NOT_PREFERRED' }
    @{ key = 'OtherActiveAdapters'; value = 1; expected = 'OTHER_ACTIVE_ADAPTER_PRESENT' }
    @{ key = 'GatewayRoutes'; value = 1; expected = 'GATEWAY_ROUTE_PRESENT' }
)
foreach ($case in $badCases) {
    $argsCopy = @{}
    foreach ($key in $valid.Keys) { $argsCopy[$key] = $valid[$key] }
    $argsCopy[$case.key] = $case.value
    $actual = @(Get-CrossVmPreflightBlockers @argsCopy)
    if ($actual -cnotcontains $case.expected) { throw ('Risk not blocked: ' + $case.expected) }
    $checks++
}
# Guard rejection occurs before inspection, directory creation, or any socket use.
$fakeRoot = Join-Path $PSScriptRoot 'must-not-create-cross-vm-evidence'
if (Test-Path -LiteralPath $fakeRoot) { throw 'Refusing to test against an existing evidence directory.' }
$scriptPath = Join-Path $PSScriptRoot 'inspect-cross-vm-preflight.ps1'
$guarded = $false
try {
    & $scriptPath -Role VmA -LocalAddress '10.9.8.1' -PeerAddress '10.9.8.2' -Port 47123 -EvidenceDirectory $fakeRoot
} catch {
    if ($_.Exception.Message -notlike '*Explicit disposable isolated-VM acknowledgement*') { throw }
    $guarded = $true
}
if (-not $guarded -or (Test-Path -LiteralPath $fakeRoot)) { throw 'Unacknowledged read-only inspection was not rejected.' }
$checks++
Write-Output ('PASS: ' + $checks + ' offline P3-08 preflight checks; no network or WFP operations.')
