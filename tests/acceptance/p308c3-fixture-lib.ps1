# P3-08C3 fixture-only record producer. The caller must supply observed data, never synthesize missing events.
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-key-lib.ps1')
function New-P308c3FixtureFields {
    param([string]$Role,[string]$Type,[string]$Stage,[string]$Run,[string]$Transaction,[string]$Client,[string]$Receiver,[int]$Port,[string]$ObservedUtc)
    return [ordered]@{protocol='AegisCrossVmEvidence';version='3';algorithm='RSA-PKCS1-SHA256';role=$Role;type=$Type;stage=$Stage;run=$Run;transaction=$Transaction;client=$Client;receiver=$Receiver;port=$Port.ToString([Globalization.CultureInfo]::InvariantCulture);transport='IPv4/TCP';observedUtc=$ObservedUtc;pid='-';createdUtc='-';completedUtc='-';exitCode='-';clientResult='-';tokenVerifiedBeforeResume='-';packageSid='-';resumed='-';timedOut='-';observationCount='-';sourceAddress='-';sourcePort='-';duplicateWindowSeconds='-';duplicateStatus='-';receiverExit='-';receiverError='-';capabilitySid='-';listenerStartedUtc='-';windowStartUtc='-';windowEndUtc='-'}
}
function New-P308c3SignedFixtureRecord {
    param([System.Collections.IDictionary]$Fields,[string]$Role,[Security.Cryptography.RSA]$Key)
    if ($Fields.role -cne $Role) { throw 'Evidence producer role mismatch.' }
    $lines=@(foreach($name in $Fields.Keys){ '{0}={1}' -f $name,$Fields[$name] })
    $bytes=[Text.Encoding]::ASCII.GetBytes(($lines -join "`n") + "`n")
    $signature=New-P308c2Signature -Payload $bytes -Key $Key
    return [pscustomobject]@{Payload=$bytes; Signature=$signature}
}
function Write-P308c3FixtureRecord {
    param([System.Collections.IDictionary]$Fields,[string]$Role,[string]$Identity,[string]$OutputPrefix)
    $key=Open-P308c3SigningKey -Identity $Identity -Role $Role
    try { $record=New-P308c3SignedFixtureRecord -Fields $Fields -Role $Role -Key $key } finally { $key.Dispose() }
    foreach ($part in @(@{path=$OutputPrefix+'.payload';bytes=$record.Payload},@{path=$OutputPrefix+'.sig';bytes=$record.Signature})) {
        $stream=New-Object IO.FileStream($part.path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try { $stream.Write($part.bytes,0,$part.bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    }
}
function ConvertTo-P308c3ClientFields {
    param([System.Collections.IDictionary]$Record)
    if ($Record.stage -cnotin @('Ordinary','AppContainer')) { throw 'Unknown fixture stage.' }
    $v=New-P308c3FixtureFields -Role VmA -Type client -Stage $Record.stage -Run $Record.runId -Transaction $Record.transactionId -Client $Record.clientAddress -Receiver $Record.receiverAddress -Port $Record.receiverPort -ObservedUtc $Record.completedUtc
    $v.pid=[string]$Record.processId; $v.createdUtc=$Record.processCreatedUtc; $v.completedUtc=$Record.completedUtc; $v.exitCode=[string]$Record.exitCode
    $v.clientResult=if ($Record.clientResult -ceq 'sent') { 'sent' } else { 'failed' }; $v.timedOut=$Record.timedOut.ToString().ToLowerInvariant()
    if ($Record.stage -ceq 'AppContainer') {
        if ($Record.tokenVerifiedBeforeResume -isnot [bool] -or -not $Record.tokenVerifiedBeforeResume -or $Record.resumed -isnot [bool] -or -not $Record.resumed -or -not $Record.packageSid -or -not $Record.capabilitySid) { throw 'Missing verified token evidence.' }
        $v.packageSid=$Record.packageSid; $v.capabilitySid=$Record.capabilitySid; $v.tokenVerifiedBeforeResume='true'; $v.resumed='true'
    }
    return $v
}
function ConvertTo-P308c3ReceiverFields {
    param([System.Collections.IDictionary]$Record,[string]$Stage,[string]$ListenerStartedUtc,[string]$WindowStartUtc,[string]$WindowEndUtc)
    $v=New-P308c3FixtureFields -Role VmB -Type receiver -Stage $Stage -Run $Record.runId -Transaction $Record.transactionId -Client $Record.remoteAddress -Receiver $Record.receiverAddress -Port $Record.receiverPort -ObservedUtc $Record.observedUtc
    $v.observationCount=[string]$Record.observedMatches; $v.sourceAddress=$Record.remoteAddress; $v.sourcePort=[string]$Record.remotePort; $v.duplicateWindowSeconds=[string]$Record.duplicateObservationWindowSeconds
    $v.duplicateStatus='none'; $v.receiverError='none'; $v.listenerStartedUtc=$ListenerStartedUtc; $v.windowStartUtc=$WindowStartUtc; $v.windowEndUtc=$WindowEndUtc
    return $v
}
function Assert-P308c3PriorOrdinary {
    param([byte[]]$Payload,[byte[]]$Signature,[Security.Cryptography.RSA]$KeyA,[string]$PinA,[Security.Cryptography.RSA]$KeyB,[string]$PinB,[guid]$Run,[guid]$Transaction,[string]$Client,[string]$Receiver,[int]$Port)
    Assert-P308c2Trust -KeyA $KeyA -FingerprintA $PinA -KeyB $KeyB -FingerprintB $PinB
    $r=Read-P308c2SignedRecord -Payload $Payload -Signature $Signature -Key $KeyB -ExpectedFingerprint $PinB
    if ($r.version -cne '3' -or $r.role -cne 'VmB' -or $r.type -cne 'receiver' -or $r.stage -cne 'Ordinary' -or $r.run -cne $Run.ToString('N') -or $r.transaction -cne $Transaction.ToString('N') -or $r.client -cne $Client -or $r.receiver -cne $Receiver -or $r.port -cne $Port.ToString([Globalization.CultureInfo]::InvariantCulture) -or $r.sourceAddress -cne $Client -or $r.observationCount -cne '1' -or $r.duplicateWindowSeconds -cne '2' -or $r.duplicateStatus -cne 'none' -or $r.receiverError -cne 'none') { throw 'EVIDENCE_MISMATCH' }
    if ($r.sourcePort -ceq '-' -or [long]$r.sourcePort -lt 1 -or [long]$r.sourcePort -gt 65535 -or $r.listenerStartedUtc -ceq '-' -or $r.windowStartUtc -ceq '-' -or $r.windowEndUtc -ceq '-') { throw 'RECEIVER_INCOMPLETE' }
    $start=[DateTimeOffset]::Parse($r.listenerStartedUtc); $windowStart=[DateTimeOffset]::Parse($r.windowStartUtc); $end=[DateTimeOffset]::Parse($r.windowEndUtc); $observed=[DateTimeOffset]::Parse($r.observedUtc)
    if ($start -gt $windowStart -or $windowStart -ne $observed -or ($end-$windowStart).TotalSeconds -lt 2) { throw 'EVIDENCE_MISMATCH' }
}
function ConvertTo-P308c3UtcString {
    param($Value)
    if ($Value -is [datetime]) {
        if ($Value.Kind -ne [DateTimeKind]::Utc) { throw 'SCHEMA_INVALID' }
        return $Value.ToString('O',[Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -isnot [string]) { throw 'SCHEMA_INVALID' }
    $parsed=[DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact($Value,"yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$parsed) -or $parsed.UtcDateTime.ToString('O',[Globalization.CultureInfo]::InvariantCulture) -cne $Value) { throw 'SCHEMA_INVALID' }
    return $Value
}
