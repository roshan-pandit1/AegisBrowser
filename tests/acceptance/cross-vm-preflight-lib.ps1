# Pure input checks for P3-08 research. Dot-source only; performs no network or OS operations.
function Assert-CrossVmPrivateIPv4 {
    param([Parameter(Mandatory = $true)][string] $Value)
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref] $ip) -or
        $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        $ip.ToString() -cne $Value) {
        throw 'A canonical RFC1918 IPv4 literal is required.'
    }
    $bytes = $ip.GetAddressBytes()
    if (-not ($bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) {
        throw 'A canonical RFC1918 IPv4 literal is required.'
    }
}
function Assert-CrossVmEndpoints {
    param(
        [Parameter(Mandatory = $true)][string] $LocalAddress,
        [Parameter(Mandatory = $true)][string] $PeerAddress,
        [Parameter(Mandatory = $true)][int] $Port
    )
    Assert-CrossVmPrivateIPv4 $LocalAddress
    Assert-CrossVmPrivateIPv4 $PeerAddress
    if ($LocalAddress -ceq $PeerAddress) { throw 'The two VM addresses must be different.' }
    if ($Port -lt 1024 -or $Port -gt 65535) { throw 'The research port must be 1024-65535.' }
}
function Get-CrossVmPreflightBlockers {
    param(
        [Parameter(Mandatory = $true)][int] $LocalAddressMatches,
        [Parameter(Mandatory = $true)][string] $NetworkCategory,
        [Parameter(Mandatory = $true)][int] $DefaultIPv4Routes,
        [Parameter(Mandatory = $true)][int] $DefaultIPv6Routes,
        [Parameter(Mandatory = $true)][int] $NonLinkLocalIPv6Addresses,
        [Parameter(Mandatory = $true)][bool] $FirewallEnabled,
        [Parameter(Mandatory = $true)][bool] $AdapterUp,
        [Parameter(Mandatory = $true)][bool] $AddressPreferred,
        [Parameter(Mandatory = $true)][int] $OtherActiveAdapters,
        [Parameter(Mandatory = $true)][int] $GatewayRoutes
    )
    $issues = New-Object 'System.Collections.Generic.List[string]'
    if ($LocalAddressMatches -ne 1) { $issues.Add('LOCAL_ADDRESS_NOT_UNIQUE') }
    if ($NetworkCategory -cne 'Private') { $issues.Add('PRIVATE_NETWORK_CATEGORY_NOT_CONFIRMED') }
    if ($DefaultIPv4Routes -ne 0) { $issues.Add('IPV4_DEFAULT_ROUTE_PRESENT') }
    if ($DefaultIPv6Routes -ne 0) { $issues.Add('IPV6_DEFAULT_ROUTE_PRESENT') }
    if ($NonLinkLocalIPv6Addresses -ne 0) { $issues.Add('NON_LINK_LOCAL_IPV6_PRESENT') }
    if (-not $FirewallEnabled) { $issues.Add('FIREWALL_PROFILE_DISABLED_OR_UNAVAILABLE') }
    if (-not $AdapterUp) { $issues.Add('LOCAL_ADAPTER_NOT_UP') }
    if (-not $AddressPreferred) { $issues.Add('LOCAL_ADDRESS_NOT_PREFERRED') }
    if ($OtherActiveAdapters -ne 0) { $issues.Add('OTHER_ACTIVE_ADAPTER_PRESENT') }
    if ($GatewayRoutes -ne 0) { $issues.Add('GATEWAY_ROUTE_PRESENT') }
    return $issues.ToArray()
}
