# Read-only network qualification on an independently authorized disposable VM only.
# Creates new evidence files and signs an in-memory key challenge; makes no connectivity probe.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string]$Role,
 [Parameter(Mandatory=$true)][string]$ExpectedAdapter,
 [Parameter(Mandatory=$true)][string]$ExpectedCommit,
 [Parameter(Mandatory=$true)][string]$SigningIdentity,
 [Parameter(Mandatory=$true)][string]$TrustManifest,
 [Parameter(Mandatory=$true)][string]$TrustManifestSha256,
 [Parameter(Mandatory=$true)][string]$RunManifest,
 [Parameter(Mandatory=$true)][string]$RunManifestSha256,
 [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
 [switch]$AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference='Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') { throw 'DISPOSABLE_VM_ACKNOWLEDGEMENT_REQUIRED' }
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-file-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-key-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-operator-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c4-readiness-lib.ps1')
if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($ExpectedAdapter)) { throw 'INVALID_EXPECTATION' }
$trust=Read-OperatorManifest $TrustManifest $TrustManifestSha256 @('protocol','version','roleA','statusA','algorithmA','fingerprintA','modulusA','exponentA','roleB','statusB','algorithmB','fingerprintB','modulusB','exponentB')
if ($trust.protocol -cne 'AegisOperatorTrust' -or $trust.version -cne '1' -or $trust.roleA -cne 'VmA' -or $trust.roleB -cne 'VmB' -or $trust.statusA -cne 'active' -or $trust.statusB -cne 'active' -or $trust.algorithmA -cne 'RSA-PKCS1-SHA256' -or $trust.algorithmB -cne 'RSA-PKCS1-SHA256') { throw 'UNTRUSTED_KEY' }
$a=$null;$b=$null;$local=$null
try {
 $a=Import-ApprovedPublicKey $trust.modulusA $trust.exponentA
 $b=Import-ApprovedPublicKey $trust.modulusB $trust.exponentB
 Assert-P308c2Trust -KeyA $a -FingerprintA $trust.fingerprintA -KeyB $b -FingerprintB $trust.fingerprintB
 $expectedPin=if ($Role -ceq 'VmA') { $trust.fingerprintA } else { $trust.fingerprintB }
 $local=Open-P308c3SigningKey -Role $Role -Identity $SigningIdentity
 $actualPin=Get-P308c2Fingerprint $local
 if ($actualPin -cne $expectedPin) { throw 'UNTRUSTED_KEY' }
 Test-P308c3SigningChallenge $local
} finally { if($local){$local.Dispose()};if($a){$a.Dispose()};if($b){$b.Dispose()} }
$run=Read-OperatorManifest $RunManifest $RunManifestSha256 @('protocol','version','run','ordinaryTransaction','appTransaction','client','receiver','port','transport','windowStart','windowEnd','expectedPackageSid')
if ($run.protocol -cne 'AegisApprovedRun' -or $run.version -cne '2' -or $run.expectedPackageSid -cnotmatch '^S-1-15-2(-[0-9]+)+$' -or $run.transport -cne 'IPv4/TCP' -or $run.port -cnotmatch '^[1-9][0-9]{0,4}$' -or [int]$run.port -lt 1024 -or [int]$run.port -gt 65535) { throw 'SCHEMA_INVALID' }
Assert-P308c4PackageSid $run.expectedPackageSid
$ids=@((Get-CanonicalGuid $run.run),(Get-CanonicalGuid $run.ordinaryTransaction),(Get-CanonicalGuid $run.appTransaction))
$start=Get-CanonicalTime $run.windowStart; $end=Get-CanonicalTime $run.windowEnd
if (@($ids | Select-Object -Unique).Count -ne 3 -or $start -ge $end -or $end -le [DateTimeOffset]::UtcNow) { throw 'SCHEMA_INVALID' }
Assert-CrossVmEndpoints -LocalAddress $run.client -PeerAddress $run.receiver -Port ([int]$run.port)
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$revision=(& git -C $repo rev-parse HEAD); if ($LASTEXITCODE -ne 0 -or @($revision).Count -ne 1) { throw 'SOURCE_NOT_CONFIRMED' }
$changed=@(& git -C $repo status --porcelain --untracked-files=all); if ($LASTEXITCODE -ne 0) { throw 'SOURCE_NOT_CONFIRMED' }
$localAddress=if($Role -ceq 'VmA'){$run.client}else{$run.receiver}
$peerAddress=if($Role -ceq 'VmA'){$run.receiver}else{$run.client}
& (Join-Path $PSScriptRoot 'inspect-cross-vm-preflight.ps1') -Role $Role -LocalAddress $localAddress -PeerAddress $peerAddress -Port ([int]$run.port) -EvidenceDirectory $EvidenceDirectory -AcknowledgeDisposableIsolatedNetwork | Write-Output
$bytes=Read-P308c2EvidenceFile -Path (Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) 'vm-network-preflight.json') -MaximumBytes 65536
$snapshot=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
$assessment=Get-P308c4SnapshotAssessment -Snapshot $snapshot -Role $Role -Client $run.client -Receiver $run.receiver -Port ([int]$run.port) -ExpectedAdapter $ExpectedAdapter -ExpectedCommit $ExpectedCommit -ActualCommit $revision -CleanTree ($changed.Count -eq 0) -ExpectedFingerprint $expectedPin -ActualFingerprint $actualPin
$report=[ordered]@{schemaVersion=1;status=$assessment.status;observedUtc=[DateTime]::UtcNow.ToString('O');role=$Role;runId=$run.run;expectedPackageSid=$run.expectedPackageSid;sourceCommit=$revision;sourceClean=($changed.Count -eq 0);signingFingerprint=$actualPin;adapter=$ExpectedAdapter;blockers=$assessment.blockers;limitations=$assessment.limitations}
$path=Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) 'lab-readiness.json'
$stream=New-Object IO.FileStream($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $data=[Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 6)+"`n");$stream.Write($data,0,$data.Length);$stream.Flush($true) } finally {$stream.Dispose()}
Write-Output "$($assessment.status): $path; independent operator isolation and authorization review remains required."
