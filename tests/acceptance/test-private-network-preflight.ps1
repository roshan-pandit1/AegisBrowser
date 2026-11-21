# Pure synthetic validation: no networking or WFP operations.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'private-network-preflight-lib.ps1')
$addresses = @([pscustomobject]@{ Address = '10.9.8.1'; State = 'Preferred'; InterfaceState = 'Up'; InterfaceIndex = 4 })
$routes = @(
    [pscustomobject]@{ Family = 'IPv4'; Prefix = '10.9.8.0/24'; InterfaceIndex = 4; NextHop = '0.0.0.0' },
    [pscustomobject]@{ Family = 'IPv4'; Prefix = '127.0.0.0/8'; InterfaceIndex = 1; NextHop = '0.0.0.0' },
    [pscustomobject]@{ Family = 'IPv6'; Prefix = '::1/128'; InterfaceIndex = 1; NextHop = '::' }
)
$checks = 0
function Test-Preflight([string] $Name, [hashtable] $Overrides, [bool] $Expected) {
    $arguments = @{
        Architecture = 'X64'; LocalAddress = '10.9.8.1'; PeerAddress = '10.9.8.2'; Subnet = '10.9.8.0/24';
        Port = 47123; MaxClockSkewSeconds = 2; Addresses = $addresses; Routes = $routes
    }
    foreach ($key in $Overrides.Keys) { $arguments[$key] = $Overrides[$key] }
    $passed = $false
    try { Assert-PrivateNetworkPreflight @arguments; $passed = $true } catch { }
    if ($passed -ne $Expected) { throw ('FAIL: ' + $Name) }
    $script:checks++
    Write-Output ('PASS: ' + $Name)
}
Test-Preflight 'valid synthetic topology accepted as local checks only' @{} $true
Test-Preflight 'ARM64 rejected despite potential emulation' @{ Architecture = 'Arm64' } $false
Test-Preflight 'identical VM addresses rejected' @{ PeerAddress = '10.9.8.1' } $false
Test-Preflight 'peer outside subnet rejected' @{ PeerAddress = '10.9.9.1' } $false
Test-Preflight 'public subnet rejected' @{ Subnet = '8.0.0.0/8'; LocalAddress = '8.1.1.1'; PeerAddress = '8.1.1.2' } $false
Test-Preflight 'noncanonical subnet rejected' @{ Subnet = '10.9.8.1/24' } $false
Test-Preflight 'low port rejected' @{ Port = 53 } $false
Test-Preflight 'missing measured clock bound rejected' @{ MaxClockSkewSeconds = -1 } $false
Test-Preflight 'nonassigned local address rejected' @{ LocalAddress = '10.9.8.3' } $false
Test-Preflight 'duplicate local address rejected' @{ Addresses = @($addresses[0], $addresses[0]) } $false
Test-Preflight 'down interface rejected' @{ Addresses = @([pscustomobject]@{ Address = '10.9.8.1'; State = 'Preferred'; InterfaceState = 'Down'; InterfaceIndex = 4 }) } $false
Test-Preflight 'empty route data rejected' @{ Routes = @() } $false
Test-Preflight 'IPv4 default route rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv4'; Prefix = '0.0.0.0/0'; InterfaceIndex = 4; NextHop = '10.9.8.254' }) } $false
Test-Preflight 'public more-specific route rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv4'; Prefix = '8.8.8.0/24'; InterfaceIndex = 4; NextHop = '0.0.0.0' }) } $false
Test-Preflight 'unrelated private route rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv4'; Prefix = '10.10.0.0/16'; InterfaceIndex = 4; NextHop = '0.0.0.0' }) } $false
Test-Preflight 'IPv6 default route rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv6'; Prefix = '::/0'; InterfaceIndex = 4; NextHop = '::' }) } $false
Test-Preflight 'unexpected IPv6 route rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv6'; Prefix = '2001:db8::/32'; InterfaceIndex = 4; NextHop = '::' }) } $false
Test-Preflight 'IPv4 multicast gateway rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv4'; Prefix = '224.0.0.0/4'; InterfaceIndex = 4; NextHop = '10.9.8.254' }) } $false
Test-Preflight 'IPv6 multicast gateway rejected' @{ Routes = @($routes + [pscustomobject]@{ Family = 'IPv6'; Prefix = 'ff00::/8'; InterfaceIndex = 4; NextHop = 'fe80::1' }) } $false
Test-Preflight 'peer route through gateway rejected' @{ Routes = @([pscustomobject]@{ Family = 'IPv4'; Prefix = '10.9.8.0/24'; InterfaceIndex = 4; NextHop = '10.9.8.254' }) } $false
Test-Preflight 'peer route on wrong interface rejected' @{ Routes = @([pscustomobject]@{ Family = 'IPv4'; Prefix = '10.9.8.0/24'; InterfaceIndex = 7; NextHop = '0.0.0.0' }) } $false
Test-Preflight 'no peer route rejected' @{ Routes = @($routes[1], $routes[2]) } $false
Write-Output ("PASS: $checks synthetic preflight checks; no sockets, firewall, VM or WFP checks executed.")
