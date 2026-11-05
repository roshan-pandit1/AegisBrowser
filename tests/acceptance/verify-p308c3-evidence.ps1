# Offline verifier for operator-approved P3-08 manifests and detached signed records.
# The two manifests must reside in an independently trusted operator-controlled location.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$TrustManifest,
 [Parameter(Mandatory=$true)][string]$TrustManifestSha256,
 [Parameter(Mandatory=$true)][string]$RunManifest,
 [Parameter(Mandatory=$true)][string]$RunManifestSha256,
 [Parameter(Mandatory=$true)][string]$OrdinaryClientPayload,
 [Parameter(Mandatory=$true)][string]$OrdinaryClientSignature,
 [Parameter(Mandatory=$true)][string]$OrdinaryReceiverPayload,
 [Parameter(Mandatory=$true)][string]$OrdinaryReceiverSignature,
 [Parameter(Mandatory=$true)][string]$AppClientPayload,
 [Parameter(Mandatory=$true)][string]$AppClientSignature,
 [Parameter(Mandatory=$true)][string]$AppReceiverPayload,
 [Parameter(Mandatory=$true)][string]$AppReceiverSignature,
 [Parameter(Mandatory=$true)][string]$ReceiverCompletionPayload,
 [Parameter(Mandatory=$true)][string]$ReceiverCompletionSignature,
 [Parameter(Mandatory=$true)][string]$LedgerPath,
 [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-file-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1')
. (Join-Path $PSScriptRoot 'p308c2-correlation-lib.ps1')

. (Join-Path $PSScriptRoot 'p308c3-operator-lib.ps1')

# Reserve a never-existing report before touching the replay ledger.
$reportFull=[IO.Path]::GetFullPath($ReportPath)
if (-not [IO.Path]::IsPathRooted($ReportPath) -or $ReportPath -notmatch '^[A-Za-z]:\\' -or $ReportPath.Substring(2).Contains(':')) { throw 'REPORT_PATH_INVALID' }
$null=Assert-P308c2UnlinkedPath $reportFull $false
$reportStream=New-Object IO.FileStream($reportFull,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$keyA=$null; $keyB=$null; $status='INCONCLUSIVE'; $runId='unavailable'; $exitCode=2
$normalId='unavailable'; $appId='unavailable'; $client='unavailable'; $receiver='unavailable'; $port=$null; $fingerprintA='unavailable'; $fingerprintB='unavailable'
try {
 $trust=Read-OperatorManifest $TrustManifest $TrustManifestSha256 @('protocol','version','roleA','statusA','algorithmA','fingerprintA','modulusA','exponentA','roleB','statusB','algorithmB','fingerprintB','modulusB','exponentB')
 if ($trust.protocol -cne 'AegisOperatorTrust' -or $trust.version -cne '1' -or $trust.roleA -cne 'VmA' -or $trust.roleB -cne 'VmB' -or $trust.statusA -cne 'active' -or $trust.statusB -cne 'active' -or $trust.algorithmA -cne 'RSA-PKCS1-SHA256' -or $trust.algorithmB -cne 'RSA-PKCS1-SHA256') { throw 'UNTRUSTED_KEY' }
 $keyA=Import-ApprovedPublicKey $trust.modulusA $trust.exponentA
 $keyB=Import-ApprovedPublicKey $trust.modulusB $trust.exponentB
 Assert-P308c2Trust -KeyA $keyA -FingerprintA $trust.fingerprintA -KeyB $keyB -FingerprintB $trust.fingerprintB
 $fingerprintA=$trust.fingerprintA; $fingerprintB=$trust.fingerprintB
 $run=Read-OperatorManifest $RunManifest $RunManifestSha256 @('protocol','version','run','ordinaryTransaction','appTransaction','client','receiver','port','transport','windowStart','windowEnd','expectedPackageSid')
 if ($run.protocol -cne 'AegisApprovedRun' -or $run.version -cne '2' -or $run.transport -cne 'IPv4/TCP' -or $run.port -cnotmatch '^[1-9][0-9]{0,4}$' -or [int]$run.port -lt 1024 -or [int]$run.port -gt 65535) { throw 'SCHEMA_INVALID' }
 if ($run.expectedPackageSid -cnotmatch '^S-1-15-2(-[0-9]+)+$') { throw 'SCHEMA_INVALID' }
 $runGuid=Get-CanonicalGuid $run.run; $normal=Get-CanonicalGuid $run.ordinaryTransaction; $app=Get-CanonicalGuid $run.appTransaction
 $start=Get-CanonicalTime $run.windowStart; $end=Get-CanonicalTime $run.windowEnd
 $runId=$runGuid.ToString('N'); $normalId=$normal.ToString('N'); $appId=$app.ToString('N')
 $client=$run.client; $receiver=$run.receiver; $port=[int]$run.port
 $oc=Import-P308c2FileRecord $OrdinaryClientPayload $OrdinaryClientSignature
 $or=Import-P308c2FileRecord $OrdinaryReceiverPayload $OrdinaryReceiverSignature
 $ac=Import-P308c2FileRecord $AppClientPayload $AppClientSignature
 $ar=Import-P308c2FileRecord $AppReceiverPayload $AppReceiverSignature
 $complete=Import-P308c2FileRecord $ReceiverCompletionPayload $ReceiverCompletionSignature
 # The legacy signed-v2 format lacks capability and full receiver-window evidence.
 foreach ($record in @($oc,$or,$ac,$ar,$complete)) {
  if ((ConvertFrom-P308c2Payload $record.Payload).version -cne '3') { throw 'SCHEMA_INVALID' }
 }
 $status=Invoke-P308c2OfflineCorrelation -OrdinaryClient $oc -OrdinaryReceiver $or -AppClient $ac -AppReceiver $ar -ReceiverCompletion $complete -KeyA $keyA -PinA $trust.fingerprintA -KeyB $keyB -PinB $trust.fingerprintB -Run $runGuid -OrdinaryTransaction $normal -AppTransaction $app -Client $run.client -Receiver $run.receiver -Port ([int]$run.port) -WindowStart $start -WindowEnd $end -LedgerPath $LedgerPath -ExpectedPackageSid $run.expectedPackageSid -RequireVersion3
 if ($status -cne 'AUTHENTICATED_AND_CORRELATED') { throw 'INCONCLUSIVE' }
 $exitCode=0
} catch {
 $known=@('AUTHENTICATION_FAILED','UNTRUSTED_KEY','SCHEMA_INVALID','EVIDENCE_MISMATCH','CLIENT_FAILED','RECEIVER_INCOMPLETE','REPLAY_DETECTED','LEDGER_INVALID')
 if ($known -ccontains $_.Exception.Message) { $status=$_.Exception.Message } else { $status='INCONCLUSIVE' }
} finally {
 if ($null -ne $keyA) { $keyA.Dispose() }; if ($null -ne $keyB) { $keyB.Dispose() }
 $correlated = ($status -ceq 'AUTHENTICATED_AND_CORRELATED')
 $report=[ordered]@{
  schemaVersion=2; result=$status; disposition=if ($correlated) {'OFFLINE_VALIDATION_PASSED'} else {'INCONCLUSIVE'}
  sourceCommit='NOT_ATTESTED'; approvedPackageSid=if ($null -ne $run) {$run.expectedPackageSid} else {'unavailable'}; evidenceProtocol='AegisCrossVmEvidence/3'; verifiedUtc=[DateTime]::UtcNow.ToString('O')
  roles=[ordered]@{client='VmA';receiver='VmB'}; fingerprints=[ordered]@{vmA=$fingerprintA;vmB=$fingerprintB}
  runId=$runId; ordinaryTransaction=$normalId; appTransaction=$appId
  approvedEndpoints=[ordered]@{client=$client;receiver=$receiver;port=$port;transport='IPv4/TCP'}
  signatures=if ($correlated) {'VALIDATED'} else {'NOT_FULLY_VALIDATED'}
  ordinaryControl=if ($correlated) {'SIGNED_CLAIMS_CORRELATED'} else {'NOT_FULLY_VALIDATED'}
  appContainerControl=if ($correlated) {'SIGNED_CLAIMS_CORRELATED'} else {'NOT_FULLY_VALIDATED'}
  receiverCompletion=if ($correlated) {'SIGNED_CLAIM_CORRELATED'} else {'NOT_FULLY_VALIDATED'}
  replayLedger=if ($correlated) {'CONSUMED'} elseif ($status -ceq 'REPLAY_DETECTED') {'REPLAY_DETECTED'} else {'NOT_COMMITTED_OR_UNKNOWN'}
  vmPreflight='NOT_EVALUATED'; independentTopologyApproval='NOT_EVALUATED'; wfpAttribution='NOT_OBSERVED'; liveValidation='LIVE_VALIDATION_NOT_RUN'
  meaning='Offline verification of signed producer claims only; no packet attribution, WFP decision, or containment established.'
 }
 try {
  $bytes=[Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Compress)+[Environment]::NewLine)
  $reportStream.Write($bytes,0,$bytes.Length);$reportStream.Flush($true)
 } catch { $exitCode=3; $status='REPORT_WRITE_FAILED' } finally { $reportStream.Dispose() }
}
Write-Output $status
exit $exitCode
