# Experimental in-memory primitive for offline research only. No I/O or replay state.
# Never interpret a successful signature as delivery, VM isolation, or WFP enforcement.
function Test-P308cSignedRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][byte[]] $Payload,
        [Parameter(Mandatory=$true)][byte[]] $Signature,
        [Parameter(Mandatory=$true)][System.Security.Cryptography.RSA] $PinnedKey,
        [Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $ExpectedRole,
        [Parameter(Mandatory=$true)][guid] $ExpectedRun,
        [Parameter(Mandatory=$true)][guid] $ExpectedTransaction,
        [Parameter(Mandatory=$true)][string] $ExpectedClient,
        [Parameter(Mandatory=$true)][string] $ExpectedReceiver,
        [Parameter(Mandatory=$true)][int] $ExpectedPort
    )
    # These values are supplied by an independently authenticated operator, never by the payload.
    if ($PinnedKey.KeySize -lt 2048 -or $ExpectedRun -eq [guid]::Empty -or $ExpectedTransaction -eq [guid]::Empty -or $ExpectedRun -eq $ExpectedTransaction) { throw 'SCHEMA_INVALID' }
    Assert-CrossVmEndpoints -LocalAddress $ExpectedClient -PeerAddress $ExpectedReceiver -Port $ExpectedPort
    if ($Payload.Length -lt 1 -or $Payload.Length -gt 2048 -or $Signature.Length -ne ($PinnedKey.KeySize / 8)) { throw 'SCHEMA_INVALID' }
    $valid = $false
    try { $valid = $PinnedKey.VerifyData($Payload, $Signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1) } catch { throw 'AUTHENTICATION_FAILED' }
    if (-not $valid) { throw 'AUTHENTICATION_FAILED' }
    # ASCII is checked byte-for-byte; no decoding replacement, BOM, CR, JSON ambiguity or alternate ordering.
    foreach ($b in $Payload) { if (($b -lt 32 -or $b -gt 126) -and $b -ne 10) { throw 'SCHEMA_INVALID' } }
    $text = [Text.Encoding]::ASCII.GetString($Payload)
    $parts = $text.Split([char]10)
    if ($parts.Length -ne 13 -or $parts[12] -cne '') { throw 'SCHEMA_INVALID' }
    $keys = @('protocol','version','algorithm','role','type','run','transaction','client','receiver','port','transport','observedUtc')
    $values = @{}
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $prefix = $keys[$i] + '='
        if (-not $parts[$i].StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'SCHEMA_INVALID' }
        $values[$keys[$i]] = $parts[$i].Substring($prefix.Length)
    }
    if ($values.protocol -cne 'AegisCrossVmEvidence' -or $values.version -cne '1' -or $values.algorithm -cne 'RSA-PKCS1-SHA256' -or $values.transport -cne 'IPv4/TCP') { throw 'SCHEMA_INVALID' }
    if ($values.role -cne $ExpectedRole -or $values.run -cne $ExpectedRun.ToString('N') -or $values.transaction -cne $ExpectedTransaction.ToString('N') -or $values.client -cne $ExpectedClient -or $values.receiver -cne $ExpectedReceiver -or $values.port -cne $ExpectedPort.ToString([Globalization.CultureInfo]::InvariantCulture)) { throw 'EVIDENCE_MISMATCH' }
    if (($ExpectedRole -ceq 'VmA' -and $values.type -cne 'client') -or ($ExpectedRole -ceq 'VmB' -and $values.type -cne 'receiver')) { throw 'EVIDENCE_MISMATCH' }
    $time = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact($values.observedUtc, 'yyyy-MM-ddTHH:mm:ss.fffffffZ', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref] $time)) { throw 'SCHEMA_INVALID' }
    # This primitive proves only signature and basic bindings, not freshness or a successful experiment.
    return 'INCONCLUSIVE'
}
