$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
$a=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $a.PersistKeyInCsp=$false
$b=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $b.PersistKeyInCsp=$false
$pubA=New-Object Security.Cryptography.RSACryptoServiceProvider; $pubA.PersistKeyInCsp=$false; $pubA.ImportParameters($a.ExportParameters($false))
$pubB=New-Object Security.Cryptography.RSACryptoServiceProvider; $pubB.PersistKeyInCsp=$false; $pubB.ImportParameters($b.ExportParameters($false))
$checks=0
function Reject([scriptblock]$action,[string]$expected) { $hit=$false; try { & $action | Out-Null } catch { if ($_.Exception.Message -cne $expected) { throw }; $hit=$true }; if (-not $hit) { throw "Expected $expected" }; $script:checks++ }
try {
 $fingerA=Get-P308c2Fingerprint $pubA; $fingerB=Get-P308c2Fingerprint $pubB
 Assert-P308c2Trust $pubA $fingerA $pubB $fingerB; $checks++
 Reject { Assert-P308c2Trust $pubA $fingerA $pubA $fingerA } 'UNTRUSTED_KEY'
 Reject { Assert-P308c2Trust $pubA $fingerB $pubB $fingerA } 'UNTRUSTED_KEY'
 Reject { Assert-P308c2Trust $pubA 'bad' $pubB $fingerB } 'UNTRUSTED_KEY'
 $weak=New-Object Security.Cryptography.RSACryptoServiceProvider(1024); $weak.PersistKeyInCsp=$false
 try { Reject { Get-P308c2Fingerprint $weak } 'UNTRUSTED_KEY' } finally { $weak.Dispose() }
 $lines=@('protocol=AegisCrossVmEvidence','version=2','algorithm=RSA-PKCS1-SHA256','role=VmA','type=client','stage=Ordinary',('run='+[guid]::NewGuid().ToString('N')),('transaction='+[guid]::NewGuid().ToString('N')),'client=10.9.8.1','receiver=10.9.8.2','port=47123','transport=IPv4/TCP','observedUtc=2026-11-04T10:00:00.0000000Z','pid=100','createdUtc=2026-11-04T09:59:58.0000000Z','completedUtc=2026-11-04T09:59:59.0000000Z','exitCode=0','clientResult=sent','tokenVerifiedBeforeResume=-','packageSid=-','resumed=true','timedOut=false','observationCount=-','sourceAddress=-','sourcePort=-','duplicateWindowSeconds=-','duplicateStatus=-','receiverExit=-','receiverError=-')
 function Encode($l) { return ,([Text.Encoding]::ASCII.GetBytes(($l -join "`n")+"`n")) }
 $payload=Encode $lines; $sig=New-P308c2Signature $payload $a
 $result=Read-P308c2SignedRecord $payload $sig $pubA $fingerA
 if ($result.role -cne 'VmA' -or $result.type -cne 'client') { throw 'Record mismatch' }; $checks++
 $receiver=[string[]]$lines.Clone(); $receiver[3]='role=VmB'; $receiver[4]='type=receiver'
 $receiver[13]='pid=-'; $receiver[14]='createdUtc=-'; $receiver[15]='completedUtc=-'; $receiver[16]='exitCode=-'; $receiver[17]='clientResult=-'; $receiver[20]='resumed=-'; $receiver[21]='timedOut=-'
 $receiver[22]='observationCount=1'; $receiver[23]='sourceAddress=10.9.8.1'; $receiver[24]='sourcePort=50000'; $receiver[25]='duplicateWindowSeconds=2'; $receiver[26]='duplicateStatus=none'
 $receiverPayload=Encode $receiver; $receiverSig=New-P308c2Signature $receiverPayload $b
 $receiverResult=Read-P308c2SignedRecord $receiverPayload $receiverSig $pubB $fingerB
 if ($receiverResult.type -cne 'receiver' -or $receiverResult.role -cne 'VmB') { throw 'Receiver mismatch' }; $checks++
 Reject { Read-P308c2SignedRecord $payload $sig $pubB $fingerB } 'AUTHENTICATION_FAILED'
 Reject { Read-P308c2SignedRecord $payload $sig $pubA $fingerB } 'UNTRUSTED_KEY'
 $tampered=[byte[]]$payload.Clone(); $tampered[0]=120
 Reject { Read-P308c2SignedRecord $tampered $sig $pubA $fingerA } 'AUTHENTICATION_FAILED'
 $short=[byte[]]::new($sig.Length-1); [Array]::Copy($sig,$short,$short.Length)
 Reject { Read-P308c2SignedRecord $payload $short $pubA $fingerA } 'SCHEMA_INVALID'
 foreach ($case in @(@{i=1;v='version=1'},@{i=2;v='algorithm=none'},@{i=3;v='role=VmB'},@{i=5;v='stage=Other'},@{i=7;v='transaction=bad'},@{i=8;v='client=8.8.8.8'},@{i=10;v='port=047123'},@{i=12;v='observedUtc=bad'},@{i=13;v='pid=01'},@{i=20;v='resumed=yes'})) {
  $bad=[string[]]$lines.Clone(); $bad[$case.i]=$case.v; $bytes=Encode $bad
  Reject { ConvertFrom-P308c2Payload $bytes } 'SCHEMA_INVALID'
 }
 $bad=Encode ($lines + 'role=VmB'); Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $bad=[Text.Encoding]::ASCII.GetBytes(($lines -join "`r`n")+"`r`n"); Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $bad=Encode $lines[0..27]; Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $bad=[byte[]]::new(4097); Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $reordered=[string[]]$lines.Clone(); $reordered[3]=$lines[4]; $reordered[4]=$lines[3]
 $bad=Encode $reordered; Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $extra=[string[]]$lines.Clone(); $extra[3]='role=VmA=VmB'; $bad=Encode $extra
 Reject { New-P308c2Signature $bad $a } 'SCHEMA_INVALID'
 $bad=[byte[]]$payload.Clone(); $bad[0]=255; Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 $bad=[byte[]]::new($payload.Length - 1); [Array]::Copy($payload,$bad,$bad.Length)
 Reject { ConvertFrom-P308c2Payload $bad } 'SCHEMA_INVALID'
 Write-Output "PASS: $checks P3-08C2 record and key checks; synthetic only."
} finally { $a.Dispose(); $b.Dispose(); $pubA.Dispose(); $pubB.Dispose() }
