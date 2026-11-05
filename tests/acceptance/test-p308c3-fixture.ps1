# Offline production-path mapping/signing tests. Generated test-only keys never leave memory.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p308c3-fixture-lib.ps1')
$key=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $key.PersistKeyInCsp=$false
$testKeyParameters=$key.ExportParameters($true)
$public=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $public.PersistKeyInCsp=$false; $public.ImportParameters($key.ExportParameters($false))
function Open-P308c3SigningKey { param([string]$Identity,[string]$Role) $copy=New-Object Security.Cryptography.RSACryptoServiceProvider(2048); $copy.PersistKeyInCsp=$false; $copy.ImportParameters($testKeyParameters); return $copy }
$root=Join-Path $env:TEMP ('p308c3-fixture-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
$checks=0
try {
 $run=[guid]::NewGuid().ToString('N'); $txn=[guid]::NewGuid().ToString('N')
 $record=[ordered]@{stage='Ordinary';runId=$run;transactionId=$txn;clientAddress='10.9.8.1';receiverAddress='10.9.8.2';receiverPort=47123;processId=42;processCreatedUtc='2026-11-04T10:00:00.0000000Z';completedUtc='2026-11-04T10:00:01.0000000Z';exitCode=0;timedOut=$false;clientResult='sent'}
 $fields=ConvertTo-P308c3ClientFields $record
 Write-P308c3FixtureRecord -Fields $fields -Role VmA -Identity synthetic -OutputPrefix (Join-Path $root 'ordinary')
 $pin=Get-P308c2Fingerprint $public
 $parsed=Read-P308c2SignedRecord -Payload ([IO.File]::ReadAllBytes((Join-Path $root 'ordinary.payload'))) -Signature ([IO.File]::ReadAllBytes((Join-Path $root 'ordinary.sig'))) -Key $public -ExpectedFingerprint $pin
 if ($parsed.version -cne '3' -or $parsed.clientResult -cne 'sent' -or $parsed.exitCode -cne '0') { throw 'Ordinary fixture mapping incorrect.' };$checks++
 $record.exitCode=-1;$record.timedOut=$true;$record.clientResult='socket-error:100'
 $failure=ConvertTo-P308c3ClientFields $record
 if($failure.exitCode -cne '-1' -or $failure.timedOut -cne 'true' -or $failure.clientResult -cne 'failed'){throw 'Failure mapping incorrect.'};$checks++
 Write-P308c3FixtureRecord -Fields $failure -Role VmA -Identity synthetic -OutputPrefix (Join-Path $root 'failed')
 $failed=ConvertFrom-P308c2Payload ([IO.File]::ReadAllBytes((Join-Path $root 'failed.payload')))
 if ($failed.exitCode -cne '-1') {throw 'Failed exit code not signed.'};$checks++
 $appRecord=[ordered]@{}; foreach($name in $record.Keys){$appRecord[$name]=$record[$name]}
 $appRecord.stage='AppContainer'; $appRecord.exitCode=0; $appRecord.timedOut=$false; $appRecord.clientResult='sent'; $appRecord.tokenVerifiedBeforeResume=$true; $appRecord.resumed=$true; $appRecord.packageSid='S-1-15-2-123'; $appRecord.capabilitySid='S-1-15-3-3'
 $appFields=ConvertTo-P308c3ClientFields $appRecord
 if($appFields.resumed -cne 'true' -or $appFields.capabilitySid -cne 'S-1-15-3-3'){throw 'AppContainer mapping incorrect.'};$checks++
 $appRecord.resumed=$false;$rejected=$false;try{ConvertTo-P308c3ClientFields $appRecord | Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Unresumed client accepted.'};$checks++
 $receiver=[ordered]@{runId=$run;transactionId=$txn;remoteAddress='10.9.8.1';remotePort=50001;receiverAddress='10.9.8.2';receiverPort=47123;observedUtc='2026-11-04T10:00:03.0000000Z';observedMatches=1;duplicateObservationWindowSeconds=2}
 $fields=ConvertTo-P308c3ReceiverFields $receiver Ordinary '2026-11-04T10:00:00.0000000Z' '2026-11-04T10:00:03.0000000Z' '2026-11-04T10:00:05.0000000Z'
 Write-P308c3FixtureRecord -Fields $fields -Role VmB -Identity synthetic -OutputPrefix (Join-Path $root 'receiver')
 $parsed=Read-P308c2SignedRecord -Payload ([IO.File]::ReadAllBytes((Join-Path $root 'receiver.payload'))) -Signature ([IO.File]::ReadAllBytes((Join-Path $root 'receiver.sig'))) -Key $public -ExpectedFingerprint $pin
 if ($parsed.sourcePort -cne '50001' -or $parsed.windowStartUtc -cne $receiver.observedUtc -or $parsed.windowEndUtc -cne '2026-11-04T10:00:05.0000000Z') { throw 'Receiver mapping incorrect.' };$checks++
 $rejected=$false;try { Write-P308c3FixtureRecord -Fields $fields -Role VmB -Identity synthetic -OutputPrefix (Join-Path $root 'receiver') } catch {$rejected=$true};if(-not $rejected){throw 'Overwriting evidence accepted.'};$checks++
 $other=New-Object Security.Cryptography.RSACryptoServiceProvider(2048);$other.PersistKeyInCsp=$false
 try {
  $signed=New-P308c3SignedFixtureRecord -Fields $fields -Role VmB -Key $other
  $pinB=Get-P308c2Fingerprint $other
  Assert-P308c3PriorOrdinary -Payload $signed.Payload -Signature $signed.Signature -KeyA $public -PinA $pin -KeyB $other -PinB $pinB -Run ([guid]$run) -Transaction ([guid]$txn) -Client '10.9.8.1' -Receiver '10.9.8.2' -Port 47123; $checks++
  $rejected=$false;try { Assert-P308c3PriorOrdinary -Payload $signed.Payload -Signature $signed.Signature -KeyA $public -PinA $pin -KeyB $other -PinB $pinB -Run ([guid]$run) -Transaction ([guid]::NewGuid()) -Client '10.9.8.1' -Receiver '10.9.8.2' -Port 47123 } catch {$rejected=$true};if(-not $rejected){throw 'Wrong prior transaction accepted.'};$checks++
  $corrupt=[byte[]]$signed.Payload.Clone();$corrupt[0]=120
  $rejected=$false;try { Assert-P308c3PriorOrdinary -Payload $corrupt -Signature $signed.Signature -KeyA $public -PinA $pin -KeyB $other -PinB $pinB -Run ([guid]$run) -Transaction ([guid]$txn) -Client '10.9.8.1' -Receiver '10.9.8.2' -Port 47123 } catch {$rejected=$true};if(-not $rejected){throw 'Modified prior observation accepted.'};$checks++
  $short=ConvertTo-P308c3ReceiverFields $receiver Ordinary '2026-11-04T10:00:00.0000000Z' '2026-11-04T10:00:03.0000000Z' '2026-11-04T10:00:04.0000000Z'
  $signedShort=New-P308c3SignedFixtureRecord -Fields $short -Role VmB -Key $other
  $rejected=$false;try { Assert-P308c3PriorOrdinary -Payload $signedShort.Payload -Signature $signedShort.Signature -KeyA $public -PinA $pin -KeyB $other -PinB $pinB -Run ([guid]$run) -Transaction ([guid]$txn) -Client '10.9.8.1' -Receiver '10.9.8.2' -Port 47123 } catch {$rejected=$true};if(-not $rejected){throw 'Short duplicate window accepted.'};$checks++
 } finally {$other.Dispose()}
 $date=[datetime]::SpecifyKind([datetime]::ParseExact('2026-11-04T10:00:00.0000000','yyyy-MM-ddTHH:mm:ss.fffffff',[Globalization.CultureInfo]::InvariantCulture),[DateTimeKind]::Utc)
 if ((ConvertTo-P308c3UtcString $date) -cne '2026-11-04T10:00:00.0000000Z') { throw 'DateTime conversion incorrect.' };$checks++
 $supervisor=Join-Path $PSScriptRoot 'invoke-p308c3-receiver-supervised.ps1'
 $blockedRoot=Join-Path $root 'must-not-exist'
 $stdout=Join-Path $root 'blocked-stdout.log'; $stderr=Join-Path $root 'blocked-stderr.log'
 $hostExe=(Get-Process -Id $PID).Path
 $argv=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $supervisor),'-BindAddress','10.9.8.2','-ExpectedClientAddress','10.9.8.1','-Port','47123','-RunId',([guid]::NewGuid().ToString('D')),'-NormalNonce',([guid]::NewGuid().ToString('D')),'-ContainedNonce',([guid]::NewGuid().ToString('D')),'-EvidenceDirectory',('"{0}"' -f $blockedRoot),'-SigningIdentity','synthetic')
 $child=Start-Process -FilePath $hostExe -ArgumentList $argv -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
 $null=$child.Handle
 if (-not $child.WaitForExit(15000)) { $child.Kill(); throw 'Unacknowledged supervisor timed out.' }
 if ($child.ExitCode -ne 1 -or (Test-Path -LiteralPath $blockedRoot) -or -not ([IO.File]::ReadAllText($stderr).Contains('Explicit disposable isolated-VM acknowledgement'))) { throw "Supervisor guard failed: exit=$($child.ExitCode); stderr=$stderr" };$checks++
 Write-Output "PASS: $checks synthetic fixture mapping and signing checks; no network, retained public evidence in $root"
} finally { $key.Dispose();$public.Dispose();$testKeyParameters=$null }
