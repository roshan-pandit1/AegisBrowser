# Offline end-to-end operator CLI tests. Ephemeral keys; no sockets or persistent identities.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1')
. (Join-Path $PSScriptRoot 'p308c3-fixture-lib.ps1')
$root=Join-Path $env:TEMP ('aegis-p308c3-operator-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$a=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $a.PersistKeyInCsp=$false
$b=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $b.PersistKeyInCsp=$false
$run=[guid]::NewGuid().ToString('N'); $normal=[guid]::NewGuid().ToString('N'); $app=[guid]::NewGuid().ToString('N')
$positive=0; $negative=0
function HashFile([string]$path) {
 $h=[Security.Cryptography.SHA256]::Create();try { return ([BitConverter]::ToString($h.ComputeHash([IO.File]::ReadAllBytes($path)))).Replace('-','').ToLowerInvariant() } finally {$h.Dispose()}
}
function SaveText([string]$path,[string[]]$lines) { [IO.File]::WriteAllText($path,($lines -join "`n")+"`n",[Text.Encoding]::ASCII) }
function SaveRecord([string]$prefix,$fields,[Security.Cryptography.RSA]$key) {
 $record=New-P308c3SignedFixtureRecord -Fields $fields -Role $fields.role -Key $key
 [IO.File]::WriteAllBytes($prefix+'.payload',$record.Payload); [IO.File]::WriteAllBytes($prefix+'.sig',$record.Signature)
}
function RunCli([string]$report,[string]$trustHash,[string]$runHash,[string]$donePayload=(Join-Path $root 'done.payload')) {
 $cli=Join-Path $PSScriptRoot 'verify-p308c3-evidence.ps1'
 $output=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $cli -TrustManifest (Join-Path $root 'trust') -TrustManifestSha256 $trustHash -RunManifest (Join-Path $root 'run') -RunManifestSha256 $runHash -OrdinaryClientPayload (Join-Path $root 'oc.payload') -OrdinaryClientSignature (Join-Path $root 'oc.sig') -OrdinaryReceiverPayload (Join-Path $root 'or.payload') -OrdinaryReceiverSignature (Join-Path $root 'or.sig') -AppClientPayload (Join-Path $root 'ac.payload') -AppClientSignature (Join-Path $root 'ac.sig') -AppReceiverPayload (Join-Path $root 'ar.payload') -AppReceiverSignature (Join-Path $root 'ar.sig') -ReceiverCompletionPayload $donePayload -ReceiverCompletionSignature (Join-Path $root 'done.sig') -LedgerPath (Join-Path $root 'ledger') -ReportPath $report
 return @{code=$LASTEXITCODE;out=($output -join "`n"); report=(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json)}
}
try {
 $pa=$a.ExportParameters($false); $pb=$b.ExportParameters($false)
 $trustPath=Join-Path $root 'trust'; $runPath=Join-Path $root 'run'
 SaveText $trustPath @('protocol=AegisOperatorTrust','version=1','roleA=VmA','statusA=active','algorithmA=RSA-PKCS1-SHA256',('fingerprintA='+(Get-P308c2Fingerprint $a)),('modulusA='+[Convert]::ToBase64String($pa.Modulus)),('exponentA='+[Convert]::ToBase64String($pa.Exponent)),'roleB=VmB','statusB=active','algorithmB=RSA-PKCS1-SHA256',('fingerprintB='+(Get-P308c2Fingerprint $b)),('modulusB='+[Convert]::ToBase64String($pb.Modulus)),('exponentB='+[Convert]::ToBase64String($pb.Exponent)))
 SaveText $runPath @('protocol=AegisApprovedRun','version=1',('run='+$run),('ordinaryTransaction='+$normal),('appTransaction='+$app),'client=10.9.8.1','receiver=10.9.8.2','port=47123','transport=IPv4/TCP','windowStart=2026-11-04T09:59:00.0000000Z','windowEnd=2026-11-04T10:01:00.0000000Z')
 $trustHash=HashFile $trustPath; $runHash=HashFile $runPath
 $oc=ConvertTo-P308c3ClientFields ([ordered]@{stage='Ordinary';runId=$run;transactionId=$normal;clientAddress='10.9.8.1';receiverAddress='10.9.8.2';receiverPort=47123;processId=100;processCreatedUtc='2026-11-04T10:00:00.0000000Z';completedUtc='2026-11-04T10:00:01.0000000Z';exitCode=0;clientResult='sent';timedOut=$false})
 $or=ConvertTo-P308c3ReceiverFields ([ordered]@{runId=$run;transactionId=$normal;remoteAddress='10.9.8.1';receiverAddress='10.9.8.2';receiverPort=47123;observedUtc='2026-11-04T10:00:03.0000000Z';observedMatches=1;remotePort=50001;duplicateObservationWindowSeconds=2}) 'Ordinary' '2026-11-04T10:00:00.0000000Z' '2026-11-04T10:00:03.0000000Z' '2026-11-04T10:00:05.0000000Z'
 $ac=ConvertTo-P308c3ClientFields ([ordered]@{stage='AppContainer';runId=$run;transactionId=$app;clientAddress='10.9.8.1';receiverAddress='10.9.8.2';receiverPort=47123;processId=101;processCreatedUtc='2026-11-04T10:00:04.0000000Z';completedUtc='2026-11-04T10:00:05.0000000Z';exitCode=0;clientResult='sent';timedOut=$false;resumed=$true;tokenVerifiedBeforeResume=$true;packageSid='S-1-15-2-123';capabilitySid='S-1-15-3-3'})
 $ar=ConvertTo-P308c3ReceiverFields ([ordered]@{runId=$run;transactionId=$app;remoteAddress='10.9.8.1';receiverAddress='10.9.8.2';receiverPort=47123;observedUtc='2026-11-04T10:00:07.0000000Z';observedMatches=1;remotePort=50002;duplicateObservationWindowSeconds=2}) 'AppContainer' '2026-11-04T10:00:00.0000000Z' '2026-11-04T10:00:07.0000000Z' '2026-11-04T10:00:09.0000000Z'
 $done=New-P308c3FixtureFields 'VmB' 'completion' 'Complete' $run '-' '10.9.8.1' '10.9.8.2' 47123 '2026-11-04T10:00:10.0000000Z'
 $done.observationCount='2';$done.receiverExit='0';$done.receiverError='none';$done.duplicateStatus='none';$done.listenerStartedUtc='2026-11-04T10:00:00.0000000Z';$done.windowStartUtc='2026-11-04T10:00:00.0000000Z';$done.windowEndUtc='2026-11-04T10:00:09.0000000Z'
 foreach($item in @(@('oc',$oc,$a),@('or',$or,$b),@('ac',$ac,$a),@('ar',$ar,$b),@('done',$done,$b))) { SaveRecord (Join-Path $root $item[0]) $item[1] $item[2] }
 Initialize-P308c2Ledger (Join-Path $root 'ledger')
 $result=RunCli (Join-Path $root 'success.json') $trustHash $runHash
 if ($result.code -ne 0 -or $result.report.result -cne 'AUTHENTICATED_AND_CORRELATED') { throw "Valid synthetic flow failed: $($result.out) / $($result.report.result)" }; $positive++
 $result=RunCli (Join-Path $root 'replay.json') $trustHash $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'REPLAY_DETECTED') { throw 'Replay accepted' }; $negative++
 $result=RunCli (Join-Path $root 'bad-pin.json') ('0'*64) $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'UNTRUSTED_KEY') { throw 'Manifest substitution accepted' }; $negative++
 $tampered=[IO.File]::ReadAllBytes((Join-Path $root 'oc.payload'));$tampered[0]=120;[IO.File]::WriteAllBytes((Join-Path $root 'oc.payload'),$tampered)
 $result=RunCli (Join-Path $root 'tampered.json') $trustHash $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'SCHEMA_INVALID') { throw 'Tampered record accepted' }; $negative++
 SaveRecord (Join-Path $root 'oc') $oc $a
 $payload=[IO.File]::ReadAllBytes((Join-Path $root 'oc.payload'))
 $payloadText=[Text.Encoding]::ASCII.GetString($payload).Replace('pid=100','pid=102')
 [IO.File]::WriteAllBytes((Join-Path $root 'oc.payload'),[Text.Encoding]::ASCII.GetBytes($payloadText))
 $result=RunCli (Join-Path $root 'bad-signature.json') $trustHash $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'AUTHENTICATION_FAILED') { throw 'Modified signed bytes accepted' }; $negative++
 SaveRecord (Join-Path $root 'oc') $oc $a
 $cases=@(
  @{name='bad-exit';field='exitCode';value='1';record=$oc;prefix='oc';key=$a;expected='CLIENT_FAILED'},
  @{name='missing-capability';field='capabilitySid';value='-';record=$ac;prefix='ac';key=$a;expected='EVIDENCE_MISMATCH'},
  @{name='wrong-capability';field='capabilitySid';value='S-1-15-3-2';record=$ac;prefix='ac';key=$a;expected='EVIDENCE_MISMATCH'},
  @{name='bad-token';field='tokenVerifiedBeforeResume';value='false';record=$ac;prefix='ac';key=$a;expected='EVIDENCE_MISMATCH'},
  @{name='wrong-transaction';field='transaction';value=$normal;record=$ar;prefix='ar';key=$b;expected='EVIDENCE_MISMATCH'},
  @{name='duplicate';field='observationCount';value='2';record=$or;prefix='or';key=$b;expected='RECEIVER_INCOMPLETE'},
  @{name='timeout';field='receiverError';value='timeout';record=$done;prefix='done';key=$b;expected='RECEIVER_INCOMPLETE'},
  @{name='late-observation';field='observedUtc';value='2026-11-04T10:02:00.0000000Z';record=$ar;prefix='ar';key=$b;expected='EVIDENCE_MISMATCH'},
  @{name='wrong-run';field='run';value=([guid]::NewGuid().ToString('N'));record=$oc;prefix='oc';key=$a;expected='EVIDENCE_MISMATCH'},
  @{name='wrong-endpoint';field='receiver';value='10.9.8.3';record=$or;prefix='or';key=$b;expected='EVIDENCE_MISMATCH'}
 )
 foreach($case in $cases) {
  $clone=[ordered]@{}; foreach($name in $case.record.Keys) { $clone[$name]=$case.record[$name] };$clone[$case.field]=$case.value
  SaveRecord (Join-Path $root $case.prefix) $clone $case.key
  $result=RunCli (Join-Path $root ($case.name+'.json')) $trustHash $runHash
  if ($result.code -eq 0 -or $result.report.result -cne $case.expected) { throw ("$($case.name) did not reject correctly: $($result.report.result)") }; $negative++
  SaveRecord (Join-Path $root $case.prefix) $case.record $case.key
 }
 $result=RunCli (Join-Path $root 'missing-completion.json') $trustHash $runHash (Join-Path $root 'does-not-exist')
 if ($result.code -eq 0 -or $result.report.result -cne 'INCONCLUSIVE') { throw 'Missing completion accepted' }; $negative++
 $originalTrust=[IO.File]::ReadAllBytes($trustPath)
 $modified=[Text.Encoding]::ASCII.GetString($originalTrust).Replace('statusB=active','statusB=revoked')
 [IO.File]::WriteAllText($trustPath,$modified,[Text.Encoding]::ASCII)
 $result=RunCli (Join-Path $root 'revoked.json') (HashFile $trustPath) $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'UNTRUSTED_KEY') { throw 'Revoked identity accepted' }; $negative++
 [IO.File]::WriteAllBytes($trustPath,$originalTrust)
 foreach($case in @(
  @{name='swapped-role';old='roleA=VmA';new='roleA=VmB'},
  @{name='unsupported-algorithm';old='algorithmA=RSA-PKCS1-SHA256';new='algorithmA=none'},
  @{name='fingerprint';old=('fingerprintA='+(Get-P308c2Fingerprint $a));new=('fingerprintA='+('0'*64))}
 )) {
  $modified=[Text.Encoding]::ASCII.GetString($originalTrust).Replace($case.old,$case.new)
  [IO.File]::WriteAllText($trustPath,$modified,[Text.Encoding]::ASCII)
  $result=RunCli (Join-Path $root ($case.name+'.json')) (HashFile $trustPath) $runHash
  if ($result.code -eq 0 -or $result.report.result -cne 'UNTRUSTED_KEY') { throw "$($case.name) accepted" }; $negative++
 }
 $same=[Text.Encoding]::ASCII.GetString($originalTrust).Replace(('fingerprintB='+(Get-P308c2Fingerprint $b)),('fingerprintB='+(Get-P308c2Fingerprint $a))).Replace(('modulusB='+[Convert]::ToBase64String($pb.Modulus)),('modulusB='+[Convert]::ToBase64String($pa.Modulus)))
 [IO.File]::WriteAllText($trustPath,$same,[Text.Encoding]::ASCII)
 $result=RunCli (Join-Path $root 'same-key.json') (HashFile $trustPath) $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'UNTRUSTED_KEY') { throw 'Same key accepted for both roles' }; $negative++
 [IO.File]::WriteAllBytes($trustPath,$originalTrust)
 SaveRecord (Join-Path $root 'oc') $oc $b
 $result=RunCli (Join-Path $root 'wrong-signing-key.json') $trustHash $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'AUTHENTICATION_FAILED') { throw 'Wrong signer accepted' }; $negative++
 SaveRecord (Join-Path $root 'oc') $oc $a
 [IO.File]::AppendAllText((Join-Path $root 'ledger'),'truncated')
 $result=RunCli (Join-Path $root 'corrupt-ledger.json') $trustHash $runHash
 if ($result.code -eq 0 -or $result.report.result -cne 'LEDGER_INVALID') { throw 'Corrupt ledger accepted' }; $negative++
 Write-Output "PASS: $positive positive and $negative negative operator CLI checks; synthetic only; files retained at $root"
} finally { $a.Dispose();$b.Dispose() }
