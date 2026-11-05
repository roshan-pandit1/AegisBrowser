# Only raw signed payloads are accepted; no claim of packet attribution or receiver honesty.
function Invoke-P308c2OfflineCorrelation {
    [CmdletBinding()]
    param(
      [Parameter(Mandatory=$true)] $OrdinaryClient,
      [Parameter(Mandatory=$true)] $OrdinaryReceiver,
      [Parameter(Mandatory=$true)] $AppClient,
      [Parameter(Mandatory=$true)] $AppReceiver,
      [Parameter(Mandatory=$true)] $ReceiverCompletion,
      [Parameter(Mandatory=$true)][Security.Cryptography.RSA] $KeyA,
      [Parameter(Mandatory=$true)][string] $PinA,
      [Parameter(Mandatory=$true)][Security.Cryptography.RSA] $KeyB,
      [Parameter(Mandatory=$true)][string] $PinB,
      [Parameter(Mandatory=$true)][guid] $Run,
      [Parameter(Mandatory=$true)][guid] $OrdinaryTransaction,
      [Parameter(Mandatory=$true)][guid] $AppTransaction,
      [Parameter(Mandatory=$true)][string] $Client,
      [Parameter(Mandatory=$true)][string] $Receiver,
      [Parameter(Mandatory=$true)][int] $Port,
      [Parameter(Mandatory=$true)][DateTimeOffset] $WindowStart,
      [Parameter(Mandatory=$true)][DateTimeOffset] $WindowEnd,
      [Parameter(Mandatory=$true)][string] $LedgerPath,
      [switch] $RequireVersion3,
      [string] $ExpectedPackageSid
    )
    Assert-P308c2Trust -KeyA $KeyA -FingerprintA $PinA -KeyB $KeyB -FingerprintB $PinB
    if ($RequireVersion3 -and -not $PSBoundParameters.ContainsKey('ExpectedPackageSid')) { throw 'SCHEMA_INVALID' }
    if ($PSBoundParameters.ContainsKey('ExpectedPackageSid')) {
      if ($ExpectedPackageSid -cnotmatch '^S-1-15-2(-[0-9]+)+$') { throw 'SCHEMA_INVALID' }
      try { $canonicalSid = New-Object Security.Principal.SecurityIdentifier($ExpectedPackageSid) } catch { throw 'SCHEMA_INVALID' }
      if ($canonicalSid.Value -cne $ExpectedPackageSid) { throw 'SCHEMA_INVALID' }
    }
    Assert-CrossVmEndpoints -LocalAddress $Client -PeerAddress $Receiver -Port $Port
    $ids = @($Run.ToString('N'), $OrdinaryTransaction.ToString('N'), $AppTransaction.ToString('N'))
    if ($Run -eq [guid]::Empty -or $OrdinaryTransaction -eq [guid]::Empty -or $AppTransaction -eq [guid]::Empty -or @($ids | Select-Object -Unique).Count -ne 3 -or $WindowStart -ge $WindowEnd) { throw 'SCHEMA_INVALID' }
    $items = @($OrdinaryClient,$OrdinaryReceiver,$AppClient,$AppReceiver,$ReceiverCompletion)
    $records = @()
    $recordVersion = $null
    for ($i=0; $i -lt 5; $i++) {
      $key = if ($i -eq 0 -or $i -eq 2) { $KeyA } else { $KeyB }
      $pin = if ($i -eq 0 -or $i -eq 2) { $PinA } else { $PinB }
      if ($items[$i].Payload -isnot [byte[]] -or $items[$i].Signature -isnot [byte[]]) { throw 'SCHEMA_INVALID' }
      $r = Read-P308c2SignedRecord -Payload $items[$i].Payload -Signature $items[$i].Signature -Key $key -ExpectedFingerprint $pin
      if ($null -eq $recordVersion) { $recordVersion = $r.version }
      elseif ($r.version -cne $recordVersion) { throw 'SCHEMA_INVALID' }
      if ($r.version -ceq '3' -and -not $PSBoundParameters.ContainsKey('ExpectedPackageSid')) { throw 'SCHEMA_INVALID' }
      if ($RequireVersion3 -and $r.version -cne '3') { throw 'SCHEMA_INVALID' }
      $expectedType = @('client','receiver','client','receiver','completion')[$i]
      $expectedStage = @('Ordinary','Ordinary','AppContainer','AppContainer','Complete')[$i]
      $expectedRole = if ($i -eq 0 -or $i -eq 2) { 'VmA' } else { 'VmB' }
      $expectedTxn = if ($i -lt 2) { $ids[1] } elseif ($i -lt 4) { $ids[2] } else { '-' }
      if ($r.role -cne $expectedRole -or $r.type -cne $expectedType -or $r.stage -cne $expectedStage -or $r.run -cne $ids[0] -or $r.transaction -cne $expectedTxn -or $r.client -cne $Client -or $r.receiver -cne $Receiver -or $r.port -cne $Port.ToString([Globalization.CultureInfo]::InvariantCulture)) { throw 'EVIDENCE_MISMATCH' }
      $observed = [DateTimeOffset]::ParseExact($r.observedUtc,"yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
      if ($observed -lt $WindowStart -or $observed -gt $WindowEnd) { throw 'EVIDENCE_MISMATCH' }
      $records += ,$r
    }
    foreach ($i in @(0,2)) {
      $r = $records[$i]
      if ($r.pid -ceq '-' -or [long]$r.pid -le 0 -or $r.createdUtc -ceq '-' -or $r.completedUtc -ceq '-') { throw 'SCHEMA_INVALID' }
      $created = [DateTimeOffset]::Parse($r.createdUtc); $completed = [DateTimeOffset]::Parse($r.completedUtc); $observed = [DateTimeOffset]::Parse($r.observedUtc)
      if ($created -lt $WindowStart -or $completed -gt $WindowEnd -or $created -gt $completed -or $completed -gt $observed) { throw 'EVIDENCE_MISMATCH' }
      if ($r.exitCode -cne '0' -or $r.clientResult -cne 'sent' -or $r.timedOut -cne 'false') { throw 'CLIENT_FAILED' }
      if ($i -eq 2 -and ($r.tokenVerifiedBeforeResume -cne 'true' -or $r.resumed -cne 'true' -or $r.packageSid -ceq '-')) { throw 'EVIDENCE_MISMATCH' }
      if ($i -eq 2 -and $PSBoundParameters.ContainsKey('ExpectedPackageSid') -and $r.packageSid -cne $ExpectedPackageSid) { throw 'EVIDENCE_MISMATCH' }
      if ($i -eq 0 -and ($r.tokenVerifiedBeforeResume -cne '-' -or $r.packageSid -cne '-' -or $r.resumed -cne '-')) { throw 'EVIDENCE_MISMATCH' }
      foreach ($name in @('observationCount','sourceAddress','sourcePort','duplicateWindowSeconds','duplicateStatus','receiverExit','receiverError')) { if ($r[$name] -cne '-') { throw 'EVIDENCE_MISMATCH' } }
    }
    foreach ($i in @(1,3)) {
      $r=$records[$i]
      if ($r.observationCount -cne '1' -or $r.sourceAddress -cne $Client -or $r.sourcePort -ceq '-' -or [long]$r.sourcePort -lt 1 -or [long]$r.sourcePort -gt 65535 -or $r.duplicateWindowSeconds -cne '2' -or $r.duplicateStatus -cne 'none' -or $r.receiverError -cne 'none') { throw 'RECEIVER_INCOMPLETE' }
      foreach ($name in @('pid','createdUtc','completedUtc','exitCode','clientResult','tokenVerifiedBeforeResume','packageSid','resumed','timedOut','receiverExit')) { if ($r[$name] -cne '-') { throw 'EVIDENCE_MISMATCH' } }
    }
    $completion = $records[4]
    if ($completion.observationCount -cne '2' -or $completion.receiverExit -cne '0' -or $completion.receiverError -cne 'none' -or $completion.duplicateStatus -cne 'none') { throw 'RECEIVER_INCOMPLETE' }
    foreach ($name in @('pid','createdUtc','completedUtc','exitCode','clientResult','tokenVerifiedBeforeResume','packageSid','resumed','timedOut','sourceAddress','sourcePort','duplicateWindowSeconds')) { if ($completion[$name] -cne '-') { throw 'EVIDENCE_MISMATCH' } }
    # Enforce semantics from the authenticated record version, regardless of caller flags.
    if ($recordVersion -ceq '3') {
      foreach ($i in @(0,2)) {
        $r=$records[$i]
        if ($r.listenerStartedUtc -cne '-' -or $r.windowStartUtc -cne '-' -or $r.windowEndUtc -cne '-') { throw 'EVIDENCE_MISMATCH' }
        if (($i -eq 0 -and $r.capabilitySid -cne '-') -or ($i -eq 2 -and $r.capabilitySid -cne 'S-1-15-3-3')) { throw 'EVIDENCE_MISMATCH' }
      }
      foreach ($i in @(1,3,4)) {
        $r=$records[$i]
        if ($r.capabilitySid -cne '-' -or $r.listenerStartedUtc -ceq '-' -or $r.windowStartUtc -ceq '-' -or $r.windowEndUtc -ceq '-') { throw 'RECEIVER_INCOMPLETE' }
        $start=[DateTimeOffset]::Parse($r.listenerStartedUtc); $wstart=[DateTimeOffset]::Parse($r.windowStartUtc); $wend=[DateTimeOffset]::Parse($r.windowEndUtc); $obs=[DateTimeOffset]::Parse($r.observedUtc)
        if ($start -lt $WindowStart -or $start -gt $wstart -or $wstart -gt $wend -or $wend -gt $WindowEnd -or ($i -ne 4 -and ($obs -lt $wstart -or $obs -gt $wend)) -or ($i -eq 4 -and $obs -lt $wend)) { throw 'EVIDENCE_MISMATCH' }
        if ($r.listenerStartedUtc -cne $records[4].listenerStartedUtc) { throw 'EVIDENCE_MISMATCH' }
        if ($i -ne 4 -and ($obs -ne $wstart -or ($wend - $wstart).TotalSeconds -lt 2 -or $wend -gt [DateTimeOffset]::Parse($records[4].windowEndUtc))) { throw 'EVIDENCE_MISMATCH' }
      }
    }
    # Compare ordering only within each VM clock, not across unsynchronized machines.
    if ([DateTimeOffset]::Parse($records[0].completedUtc) -gt [DateTimeOffset]::Parse($records[2].createdUtc) -or [DateTimeOffset]::Parse($records[1].observedUtc).AddSeconds(2) -gt [DateTimeOffset]::Parse($records[3].observedUtc) -or [DateTimeOffset]::Parse($records[3].observedUtc).AddSeconds(2) -gt [DateTimeOffset]::Parse($completion.observedUtc)) { throw 'EVIDENCE_MISMATCH' }
    # Durably consume IDs before returning success. Missing/corrupt state fails closed.
    Add-P308c2ConsumedRun -Path $LedgerPath -Run $Run -Ordinary $OrdinaryTransaction -AppContainer $AppTransaction
    return 'AUTHENTICATED_AND_CORRELATED'
}
