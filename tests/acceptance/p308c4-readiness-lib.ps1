# Pure snapshot assessment for an explicitly authorized disposable Windows x64 lab.
# Local metadata is not authenticated evidence of hypervisor isolation or remote identity.
function Get-P308c4SnapshotAssessment {
    param(
        [Parameter(Mandatory=$true)] $Snapshot,
        [Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $Role,
        [Parameter(Mandatory=$true)][string] $Client,
        [Parameter(Mandatory=$true)][string] $Receiver,
        [Parameter(Mandatory=$true)][int] $Port,
        [Parameter(Mandatory=$true)][string] $ExpectedAdapter,
        [Parameter(Mandatory=$true)][string] $ExpectedCommit,
        [Parameter(Mandatory=$true)][string] $ActualCommit,
        [Parameter(Mandatory=$true)][bool] $CleanTree,
        [Parameter(Mandatory=$true)][string] $ExpectedFingerprint,
        [Parameter(Mandatory=$true)][string] $ActualFingerprint
    )
    $issues=New-Object 'System.Collections.Generic.List[string]'
    try { Assert-CrossVmEndpoints -LocalAddress $Client -PeerAddress $Receiver -Port $Port } catch { $issues.Add('INVALID_ENDPOINTS') }
    if ($Role -cnotin @('VmA','VmB')) { $issues.Add('ROLE_INVALID') }
    $local=if ($Role -ceq 'VmA') { $Client } else { $Receiver }
    $peer=if ($Role -ceq 'VmA') { $Receiver } else { $Client }
    if ($Snapshot.schemaVersion -isnot [int] -and $Snapshot.schemaVersion -isnot [long]) { $issues.Add('SNAPSHOT_SCHEMA_INVALID') }
    elseif ($Snapshot.schemaVersion -ne 2) { $issues.Add('SNAPSHOT_SCHEMA_INVALID') }
    if ($Snapshot.role -cne $Role -or $Snapshot.localAddress -cne $local -or $Snapshot.peerAddress -cne $peer -or ($Snapshot.port -isnot [int] -and $Snapshot.port -isnot [long]) -or $Snapshot.port -ne $Port) { $issues.Add('SNAPSHOT_ENDPOINT_MISMATCH') }
    if ($Snapshot.host.osVersion -isnot [string] -or $Snapshot.host.osVersion -cnotmatch '^10\.0\.[0-9]+$' -or $Snapshot.host.osArchitecture -cne 'X64' -or $Snapshot.host.processArchitecture -cne 'X64') { $issues.Add('WINDOWS_X64_NOT_CONFIRMED') }
    if ($ExpectedAdapter -notmatch '\S' -or $Snapshot.network.interfaceAlias -cne $ExpectedAdapter) { $issues.Add('ADAPTER_MISMATCH') }
    if ($Snapshot.status -cne 'REVIEW_REQUIRED' -or @($Snapshot.blockers).Count -ne 0) { $issues.Add('SNAPSHOT_BLOCKED_OR_INCOMPLETE') }
    $n=$Snapshot.network
    foreach ($name in @('localAddressMatches','defaultIPv4RouteCount','defaultIPv6RouteCount','nonLinkLocalIPv6AddressCount','otherActiveAdapterCount','gatewayRouteCount')) {
        if (($n.$name -isnot [int] -and $n.$name -isnot [long]) -or $n.$name -ne $(if ($name -ceq 'localAddressMatches') { 1 } else { 0 })) { $issues.Add('NETWORK_' + $name) }
    }
    foreach ($name in @('adapterUp','addressPreferred','allFirewallProfilesEnabled')) { if ($n.$name -isnot [bool] -or -not $n.$name) { $issues.Add('NETWORK_' + $name) } }
    if ($n.networkCategory -cne 'Private') { $issues.Add('NETWORK_CATEGORY') }
    if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or $ActualCommit -cne $ExpectedCommit -or -not $CleanTree) { $issues.Add('SOURCE_NOT_CONFIRMED') }
    if ($ExpectedFingerprint -cnotmatch '^[0-9a-f]{64}$' -or $ActualFingerprint -cne $ExpectedFingerprint) { $issues.Add('SIGNING_IDENTITY_NOT_CONFIRMED') }
    $timestamp=[DateTimeOffset]::MinValue
    if ($Snapshot.observedUtc -is [DateTime]) { if ($Snapshot.observedUtc.Kind -ne [DateTimeKind]::Utc) { $issues.Add('TIMESTAMP_INVALID') } }
    elseif ($Snapshot.observedUtc -is [DateTimeOffset]) { if ($Snapshot.observedUtc.Offset -ne [TimeSpan]::Zero) { $issues.Add('TIMESTAMP_INVALID') } }
    elseif ($Snapshot.observedUtc -is [string]) { if (-not [DateTimeOffset]::TryParse($Snapshot.observedUtc,[ref]$timestamp) -or $timestamp.Offset -ne [TimeSpan]::Zero) { $issues.Add('TIMESTAMP_INVALID') } }
    else { $issues.Add('TIMESTAMP_INVALID') }
    $status=if ($issues.Count) { 'BLOCKED_PRECHECK' } else { 'REVIEW_REQUIRED' }
    return [pscustomobject]@{status=$status;blockers=@($issues.ToArray()); limitations=@('Operator must independently verify VM identities, network isolation and authorization.','Snapshot is local, unsigned and non-atomic; timestamps do not prove freshness.','Private-key ACLs and hypervisor topology were not verified; readiness does not establish connectivity or packet enforcement.')}
}

function Assert-P308c4PackageSid([string] $Value) {
    if ($Value -cnotmatch '^S-1-15-2(-[0-9]+)+$') { throw 'SCHEMA_INVALID' }
    try { $sid=New-Object Security.Principal.SecurityIdentifier($Value); if ($sid.Value -cne $Value) { throw 'SCHEMA_INVALID' } } catch { throw 'SCHEMA_INVALID' }
}
