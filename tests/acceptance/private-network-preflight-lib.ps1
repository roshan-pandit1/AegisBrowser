# Pure validation helpers. Passing these checks does not prove isolation,
# firewall behavior, VM ownership, measured clock skew, or WFP enforcement.
function ConvertTo-PreflightIPv4Number([string] $Value) {
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref] $ip) -or
        $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -cne $Value) {
        throw 'Expected a canonical IPv4 literal.'
    }
    [uint64] $number = 0
    foreach ($part in $ip.GetAddressBytes()) { $number = ($number * 256) + $part }
    return $number
}

function ConvertTo-PreflightRange([string] $Cidr) {
    if ($Cidr -cnotmatch '^([^/]+)/([0-9]{1,2})$') { throw 'Expected an IPv4 CIDR.' }
    $number = ConvertTo-PreflightIPv4Number $Matches[1]
    $prefix = [int] $Matches[2]
    if ($prefix -lt 0 -or $prefix -gt 32) { throw 'Invalid CIDR prefix.' }
    [uint64] $size = [uint64] [math]::Pow(2, 32 - $prefix)
    [uint64] $start = [uint64] ([math]::Floor($number / $size) * $size)
    if ($start -ne $number) { throw 'CIDR must specify its canonical network address.' }
    return [pscustomobject]@{ Start = $start; End = ($start + $size - 1); Prefix = $prefix }
}

function Test-PreflightWithin($Inner, $Outer) {
    return ($Inner.Start -ge $Outer.Start -and $Inner.End -le $Outer.End)
}

function Assert-PrivateNetworkPreflight {
    param(
        [string] $Architecture, [string] $LocalAddress, [string] $PeerAddress,
        [string] $Subnet, [int] $Port, [int] $MaxClockSkewSeconds,
        [object[]] $Addresses, [object[]] $Routes
    )
    if ($Architecture -cne 'X64') { throw 'The operating system must be native Windows x64.' }
    if ($Port -lt 1024 -or $Port -gt 65535) { throw 'An approved test port from 1024 through 65535 is required.' }
    if ($MaxClockSkewSeconds -lt 0 -or $MaxClockSkewSeconds -gt 300) { throw 'An operator-measured clock bound from 0 through 300 seconds is required.' }
    $local = ConvertTo-PreflightIPv4Number $LocalAddress
    $peer = ConvertTo-PreflightIPv4Number $PeerAddress
    $scope = ConvertTo-PreflightRange $Subnet
    if ($scope.Prefix -gt 30 -or $scope.Prefix -lt 8 -or $local -eq $peer -or
        $local -le $scope.Start -or $local -ge $scope.End -or $peer -le $scope.Start -or $peer -ge $scope.End) {
        throw 'Distinct usable VM addresses inside the authorized subnet are required.'
    }
    $private = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16') | ForEach-Object { ConvertTo-PreflightRange $_ }
    if (-not @($private | Where-Object { Test-PreflightWithin $scope $_ }).Count) { throw 'The entire subnet must be RFC1918.' }
    $assigned = @($Addresses | Where-Object { $_.Address -ceq $LocalAddress -and $_.State -ceq 'Preferred' -and $_.InterfaceState -ceq 'Up' })
    if ($assigned.Count -ne 1 -or [int] $assigned[0].InterfaceIndex -le 0) { throw 'The local address must be uniquely assigned to an active interface.' }
    $interface = [int] $assigned[0].InterfaceIndex
    if (-not $Routes -or $Routes.Count -eq 0) { throw 'Active route data is required.' }
    $hasPeerRoute = $false
    $exceptions = @('127.0.0.0/8', '224.0.0.0/4', '255.255.255.255/32') | ForEach-Object { ConvertTo-PreflightRange $_ }
    foreach ($route in $Routes) {
        if ($route.Family -ceq 'IPv6') {
            # Without an explicitly scoped IPv6 test network, no global IPv6
            # routing is accepted. Host-local and link/multicast routes do not
            # establish that the virtual network is isolated.
            if ($route.Prefix -cnotmatch '^(::1/128|fe80::/64|ff00::/8)$' -or $route.NextHop -cne '::') { throw 'Unexpected active IPv6 route.' }
            continue
        }
        if ($route.Family -cne 'IPv4') { throw 'Unknown route address family.' }
        $range = ConvertTo-PreflightRange $route.Prefix
        $localRoute = Test-PreflightWithin $range $scope
        $exception = @($exceptions | Where-Object { Test-PreflightWithin $range $_ }).Count -gt 0
        if (-not $localRoute -and -not $exception) { throw 'Unexpected active IPv4 route outside the test subnet.' }
        if ($route.NextHop -cne '0.0.0.0') { throw 'A routed gateway is outside the authorized on-link test scope.' }
        if ($localRoute) {
            if ([int] $route.InterfaceIndex -ne $interface) { throw 'Test subnet route must be on-link on the selected interface.' }
            if ($peer -ge $range.Start -and $peer -le $range.End) { $hasPeerRoute = $true }
        }
    }
    if (-not $hasPeerRoute) { throw 'No active on-link route reaches the peer through the selected interface.' }
}
