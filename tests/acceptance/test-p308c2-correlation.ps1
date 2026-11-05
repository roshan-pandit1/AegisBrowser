$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1')
. (Join-Path $PSScriptRoot 'p308c2-correlation-lib.ps1')
$a=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $a.PersistKeyInCsp=$false
$b=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $b.PersistKeyInCsp=$false
$root=Join-Path $env:TEMP ('p308c2-correlation-'+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $root | Out-Null
$run=[guid]::NewGuid(); $normal=[guid]::NewGuid(); $app=[guid]::NewGuid(); $checks=0
function Base($role,$type,$stage,$tx,$time) {
 return [ordered]@{protocol='AegisCrossVmEvidence';version='2';algorithm='RSA-PKCS1-SHA256';role=$role;type=$type;stage=$stage;run=$run.ToString('N');transaction=$tx;client='10.9.8.1';receiver='10.9.8.2';port='47123';transport='IPv4/TCP';observedUtc=$time;pid='-';createdUtc='-';completedUtc='-';exitCode='-';clientResult='-';tokenVerifiedBeforeResume='-';packageSid='-';resumed='-';timedOut='-';observationCount='-';sourceAddress='-';sourcePort='-';duplicateWindowSeconds='-';duplicateStatus='-';receiverExit='-';receiverError='-'}
}
function Signed($rec,$key) {
 $lines=@(foreach($k in $rec.Keys) { "$k=$($rec[$k])" })
 $bytes=[Text.Encoding]::ASCII.GetBytes(($lines -join "`n")+"`n")
 return @{Payload=$bytes;Signature=(New-P308c2Signature $bytes $key)}
}
function Reject([scriptblock]$action,[string]$expected) { $hit=$false; try { & $action | Out-Null } catch { if ($_.Exception.Message -cne $expected) { throw }; $hit=$true }; if (-not $hit) { throw "Expected $expected" }; $script:checks++ }
try {
 $oc=Base 'VmA' 'client' 'Ordinary' $normal.ToString('N') '2026-11-04T10:00:02.0000000Z'
 $oc.pid='100';$oc.createdUtc='2026-11-04T10:00:00.0000000Z';$oc.completedUtc='2026-11-04T10:00:01.0000000Z';$oc.exitCode='0';$oc.clientResult='sent';$oc.timedOut='false'
 $or=Base 'VmB' 'receiver' 'Ordinary' $normal.ToString('N') '2026-11-04T10:00:03.0000000Z'
 $or.observationCount='1';$or.sourceAddress='10.9.8.1';$or.sourcePort='50001';$or.duplicateWindowSeconds='2';$or.duplicateStatus='none';$or.receiverError='none'
 $ac=Base 'VmA' 'client' 'AppContainer' $app.ToString('N') '2026-11-04T10:00:06.0000000Z'
 $ac.pid='101';$ac.createdUtc='2026-11-04T10:00:04.0000000Z';$ac.completedUtc='2026-11-04T10:00:05.0000000Z';$ac.exitCode='0';$ac.clientResult='sent';$ac.timedOut='false';$ac.resumed='true';$ac.tokenVerifiedBeforeResume='true';$ac.packageSid='S-1-15-2-123'
 $ar=Base 'VmB' 'receiver' 'AppContainer' $app.ToString('N') '2026-11-04T10:00:07.0000000Z'
 $ar.observationCount='1';$ar.sourceAddress='10.9.8.1';$ar.sourcePort='50002';$ar.duplicateWindowSeconds='2';$ar.duplicateStatus='none';$ar.receiverError='none'
 $done=Base 'VmB' 'completion' 'Complete' '-' '2026-11-04T10:00:09.0000000Z';$done.observationCount='2';$done.receiverExit='0';$done.receiverError='none';$done.duplicateStatus='none'
 $params=@{OrdinaryClient=(Signed $oc $a);OrdinaryReceiver=(Signed $or $b);AppClient=(Signed $ac $a);AppReceiver=(Signed $ar $b);ReceiverCompletion=(Signed $done $b);KeyA=$a;PinA=(Get-P308c2Fingerprint $a);KeyB=$b;PinB=(Get-P308c2Fingerprint $b);Run=$run;OrdinaryTransaction=$normal;AppTransaction=$app;Client='10.9.8.1';Receiver='10.9.8.2';Port=47123;WindowStart=[DateTimeOffset]::Parse('2026-11-04T09:59:00Z');WindowEnd=[DateTimeOffset]::Parse('2026-11-04T10:01:00Z');LedgerPath=(Join-Path $root 'ledger')}
 Initialize-P308c2Ledger $params.LedgerPath
 Reject { Invoke-P308c2OfflineCorrelation @params -RequireVersion3 } 'SCHEMA_INVALID'
 Reject { Invoke-P308c2OfflineCorrelation @params -ExpectedPackageSid 'S-1-15-2-456' } 'EVIDENCE_MISMATCH'
 Reject { Invoke-P308c2OfflineCorrelation @params -ExpectedPackageSid 'invalid' } 'SCHEMA_INVALID'
 Reject { Invoke-P308c2OfflineCorrelation @params -ExpectedPackageSid 'S-1-15-2-0123' } 'SCHEMA_INVALID'
 # Verify rejection does not consume the run, then accept the matching pin.
 $params.ExpectedPackageSid='S-1-15-2-123'
 if ((Invoke-P308c2OfflineCorrelation @params) -cne 'AUTHENTICATED_AND_CORRELATED') { throw 'Expected correlated synthetic case' };$checks++
 # Legacy v2 callers that did not supply a package pin remain compatible.
 $legacy=@{}; foreach($key in $params.Keys) { if ($key -ne 'ExpectedPackageSid') { $legacy[$key]=$params[$key] } }
 $legacy.LedgerPath=Join-Path $root 'legacy-ledger'; Initialize-P308c2Ledger $legacy.LedgerPath
 if ((Invoke-P308c2OfflineCorrelation @legacy) -cne 'AUTHENTICATED_AND_CORRELATED') { throw 'Legacy v2 caller rejected' };$checks++
 Reject { Invoke-P308c2OfflineCorrelation @params } 'REPLAY_DETECTED'
 foreach ($case in @(
  @{which='OrdinaryClient';field='exitCode';value='1';expected='CLIENT_FAILED'},
  @{which='AppClient';field='tokenVerifiedBeforeResume';value='false';expected='EVIDENCE_MISMATCH'},
  @{which='OrdinaryReceiver';field='observationCount';value='2';expected='RECEIVER_INCOMPLETE'},
  @{which='ReceiverCompletion';field='receiverError';value='timeout';expected='RECEIVER_INCOMPLETE'},
  @{which='AppReceiver';field='transaction';value=$normal.ToString('N');expected='EVIDENCE_MISMATCH'}
 )) {
   $maps=@{OrdinaryClient=$oc;OrdinaryReceiver=$or;AppClient=$ac;AppReceiver=$ar;ReceiverCompletion=$done}
   $clone=[ordered]@{};foreach($k in $maps[$case.which].Keys){$clone[$k]=$maps[$case.which][$k]};$clone[$case.field]=$case.value
   $copy=@{};foreach($k in $params.Keys){$copy[$k]=$params[$k]};$key=if($case.which -in @('OrdinaryClient','AppClient')){$a}else{$b};$copy[$case.which]=Signed $clone $key
   Reject { Invoke-P308c2OfflineCorrelation @copy } $case.expected
 }
 $copy=@{};foreach($k in $params.Keys){$copy[$k]=$params[$k]};$copy.AppReceiver=$params.OrdinaryReceiver
 Reject { Invoke-P308c2OfflineCorrelation @copy } 'EVIDENCE_MISMATCH'
 $copy.AppReceiver=@{Payload=[byte[]]$params.AppReceiver.Payload.Clone();Signature=$params.AppReceiver.Signature};$copy.AppReceiver.Payload[0]=120
 Reject { Invoke-P308c2OfflineCorrelation @copy } 'AUTHENTICATION_FAILED'
 Write-Output "PASS: $checks correlation cases; synthetic records only. State retained at $root"
} finally {$a.Dispose();$b.Dispose()}
