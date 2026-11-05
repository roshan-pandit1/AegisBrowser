# Pure synthetic snapshot checks: no local topology inspection or key creation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c4-readiness-lib.ps1')
$snapshot=[pscustomobject]@{schemaVersion=2;role='VmA';localAddress='10.9.8.1';peerAddress='10.9.8.2';port=47123;observedUtc='2026-11-04T10:00:00.0000000Z';status='REVIEW_REQUIRED';blockers=@();host=[pscustomobject]@{osVersion='10.0.26300';osArchitecture='X64';processArchitecture='X64'};network=[pscustomobject]@{interfaceAlias='Lab';localAddressMatches=1;defaultIPv4RouteCount=0;defaultIPv6RouteCount=0;nonLinkLocalIPv6AddressCount=0;otherActiveAdapterCount=0;gatewayRouteCount=0;adapterUp=$true;addressPreferred=$true;allFirewallProfilesEnabled=$true;networkCategory='Private'}}
$args=@{Snapshot=$snapshot;Role='VmA';Client='10.9.8.1';Receiver='10.9.8.2';Port=47123;ExpectedAdapter='Lab';ExpectedCommit=('a'*40);ActualCommit=('a'*40);CleanTree=$true;ExpectedFingerprint=('b'*64);ActualFingerprint=('b'*64)}
$checks=0
if ((Get-P308c4SnapshotAssessment @args).status -cne 'REVIEW_REQUIRED') { throw 'Valid snapshot was not review required.' };$checks++
foreach ($case in @(
 @{where='snapshot';field='role';value='VmB'},@{where='snapshot';field='localAddress';value='10.9.8.3'},@{where='snapshot';field='schemaVersion';value='2'},
 @{where='network';field='interfaceAlias';value='Wrong'},@{where='network';field='gatewayRouteCount';value=1},@{where='network';field='defaultIPv6RouteCount';value=1},
 @{where='network';field='otherActiveAdapterCount';value=1},@{where='network';field='allFirewallProfilesEnabled';value=$false},@{where='network';field='adapterUp';value=$false},
 @{where='host';field='osArchitecture';value='Arm64'},@{where='host';field='processArchitecture';value='X86'},@{where='snapshot';field='observedUtc';value='invalid'}
)) {
 $copy=$snapshot | ConvertTo-Json -Depth 8 | ConvertFrom-Json
 $target=if($case.where -ceq 'snapshot'){$copy}else{$copy.($case.where)}
 $target.($case.field)=$case.value
 $params=$args.Clone();$params.Snapshot=$copy
 if ((Get-P308c4SnapshotAssessment @params).status -cne 'BLOCKED_PRECHECK') { throw "Unsafe case accepted: $($case.field)" };$checks++
}
foreach ($case in @(@{field='ActualCommit';value=('c'*40)},@{field='CleanTree';value=$false},@{field='ActualFingerprint';value=('c'*64)},@{field='Client';value='8.8.8.8'},@{field='Port';value=80})) {
 $params=$args.Clone();$params.($case.field)=$case.value
 if((Get-P308c4SnapshotAssessment @params).status -cne 'BLOCKED_PRECHECK'){throw "Unsafe input accepted: $($case.field)"};$checks++
}
Assert-P308c4PackageSid 'S-1-15-2-123';$checks++
foreach($sid in @('S-1-15-2-00123','S-1-15-3-3','S-1-15-2-4294967296')) {
 $rejected=$false;try{Assert-P308c4PackageSid $sid}catch{$rejected=$true};if(-not $rejected){throw 'Invalid package SID accepted.'};$checks++
}
$fake=Join-Path $env:TEMP ('p308c4-guard-'+[guid]::NewGuid().ToString('N'))
$rejected=$false
try { & (Join-Path $PSScriptRoot 'inspect-p308c4-lab-readiness.ps1') -Role VmA -ExpectedAdapter Lab -ExpectedCommit ('a'*40) -SigningIdentity synthetic -TrustManifest missing -TrustManifestSha256 ('a'*64) -RunManifest missing -RunManifestSha256 ('a'*64) -EvidenceDirectory $fake } catch { if ($_.Exception.Message -cne 'DISPOSABLE_VM_ACKNOWLEDGEMENT_REQUIRED') { throw }; $rejected=$true }
if (-not $rejected -or (Test-Path -LiteralPath $fake)) { throw 'Disposable VM guard did not refuse before inspection.' };$checks++
Write-Output "PASS: $checks offline readiness assessment checks; no network or CNG operations."
