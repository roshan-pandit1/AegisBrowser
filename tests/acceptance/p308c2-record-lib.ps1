# P3-08C2 research-only record primitives. No files, networking, or trust-store mutation.
# Callers must obtain trusted fingerprints independently of the records.
function Get-P308c2Fingerprint {
    param([Parameter(Mandatory=$true)][Security.Cryptography.RSA] $Key)
    if ($Key.KeySize -lt 2048) { throw 'UNTRUSTED_KEY' }
    $p = $Key.ExportParameters($false)
    if ($null -eq $p.Modulus -or $null -eq $p.Exponent -or $p.Modulus.Length -lt 256) { throw 'UNTRUSTED_KEY' }
    # Canonical public representation is ASCII with LF and final LF, unsigned big-endian integer bytes.
    $body = "Aegis-RSA-Public-v1`nmodulus=$([Convert]::ToBase64String($p.Modulus))`nexponent=$([Convert]::ToBase64String($p.Exponent))`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($body))).Replace('-','').ToLowerInvariant()) } finally { $sha.Dispose() }
}
function Assert-P308c2Trust {
    param([Security.Cryptography.RSA] $KeyA, [string] $FingerprintA, [Security.Cryptography.RSA] $KeyB, [string] $FingerprintB)
    if ($FingerprintA -cnotmatch '^[0-9a-f]{64}$' -or $FingerprintB -cnotmatch '^[0-9a-f]{64}$') { throw 'UNTRUSTED_KEY' }
    $actualA = Get-P308c2Fingerprint $KeyA; $actualB = Get-P308c2Fingerprint $KeyB
    if ($actualA -ceq $actualB -or $actualA -cne $FingerprintA -or $actualB -cne $FingerprintB) { throw 'UNTRUSTED_KEY' }
}
function ConvertFrom-P308c2Payload {
    param([Parameter(Mandatory=$true)][byte[]] $Payload)
    if ($Payload.Length -lt 1 -or $Payload.Length -gt 4096) { throw 'SCHEMA_INVALID' }
    foreach ($b in $Payload) { if (($b -lt 32 -or $b -gt 126) -and $b -ne 10) { throw 'SCHEMA_INVALID' } }
    $keys = @('protocol','version','algorithm','role','type','stage','run','transaction','client','receiver','port','transport','observedUtc','pid','createdUtc','completedUtc','exitCode','clientResult','tokenVerifiedBeforeResume','packageSid','resumed','timedOut','observationCount','sourceAddress','sourcePort','duplicateWindowSeconds','duplicateStatus','receiverExit','receiverError')
    $parts = [Text.Encoding]::ASCII.GetString($Payload).Split([char]10)
    if ($parts.Length -ne ($keys.Count + 1) -or $parts[-1] -cne '') { throw 'SCHEMA_INVALID' }
    $v = [ordered]@{}
    for ($i=0; $i -lt $keys.Count; $i++) {
        $prefix = $keys[$i] + '='
        if (-not $parts[$i].StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'SCHEMA_INVALID' }
        $value = $parts[$i].Substring($prefix.Length)
        if ($value -cnotmatch '^[A-Za-z0-9.:/-]+$') { throw 'SCHEMA_INVALID' }
        $v[$keys[$i]] = $value
    }
    if ($v.protocol -cne 'AegisCrossVmEvidence' -or $v.version -cne '2' -or $v.algorithm -cne 'RSA-PKCS1-SHA256' -or $v.transport -cne 'IPv4/TCP') { throw 'SCHEMA_INVALID' }
    if (($v.role -ceq 'VmA' -and $v.type -cne 'client') -or ($v.role -ceq 'VmB' -and $v.type -cnotin @('receiver','completion')) -or $v.role -cnotin @('VmA','VmB')) { throw 'SCHEMA_INVALID' }
    if (($v.type -ceq 'completion' -and $v.stage -cne 'Complete') -or ($v.type -cne 'completion' -and $v.stage -cnotin @('Ordinary','AppContainer'))) { throw 'SCHEMA_INVALID' }
    if ($v.run -cnotmatch '^[0-9a-f]{32}$' -or $v.run -ceq ('0' * 32) -or ($v.transaction -cnotmatch '^[0-9a-f]{32}$' -and $v.transaction -cne '-') -or $v.transaction -ceq ('0' * 32)) { throw 'SCHEMA_INVALID' }
    foreach ($name in @('client','receiver')) {
        $ip = $null
        if (-not [Net.IPAddress]::TryParse($v[$name], [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -cne $v[$name]) { throw 'SCHEMA_INVALID' }
        $b = $ip.GetAddressBytes()
        if (-not ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))) { throw 'SCHEMA_INVALID' }
    }
    if ($v.client -ceq $v.receiver -or $v.port -cnotmatch '^[1-9][0-9]{0,4}$' -or [int]$v.port -lt 1024 -or [int]$v.port -gt 65535) { throw 'SCHEMA_INVALID' }
    foreach ($name in @('observedUtc','createdUtc','completedUtc')) {
        if ($name -ne 'observedUtc' -and $v[$name] -ceq '-') { continue }
        $parsed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact($v[$name], "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed) -or $parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture) -cne $v[$name]) { throw 'SCHEMA_INVALID' }
    }
    foreach ($name in @('pid','exitCode','observationCount','sourcePort','duplicateWindowSeconds','receiverExit')) {
        if ($v[$name] -cne '-' -and $v[$name] -cnotmatch '^(0|[1-9][0-9]{0,9})$') { throw 'SCHEMA_INVALID' }
    }
    foreach ($name in @('tokenVerifiedBeforeResume','resumed','timedOut')) { if ($v[$name] -cnotin @('-','true','false')) { throw 'SCHEMA_INVALID' } }
    if ($v.sourceAddress -cne '-' -and $v.sourceAddress -cne $v.client) { throw 'SCHEMA_INVALID' }
    if ($v.packageSid -cne '-' -and $v.packageSid -cnotmatch '^S-1-15-2(-[0-9]+)+$') { throw 'SCHEMA_INVALID' }
    if ($v.clientResult -cnotin @('-','sent','failed') -or $v.duplicateStatus -cnotin @('-','none','duplicate') -or $v.receiverError -cnotin @('-','none','timeout','error')) { throw 'SCHEMA_INVALID' }
    return $v
}
function Read-P308c2SignedRecord {
    param([Parameter(Mandatory=$true)][byte[]] $Payload, [Parameter(Mandatory=$true)][byte[]] $Signature, [Parameter(Mandatory=$true)][Security.Cryptography.RSA] $Key, [Parameter(Mandatory=$true)][string] $ExpectedFingerprint)
    if ($ExpectedFingerprint -cnotmatch '^[0-9a-f]{64}$' -or (Get-P308c2Fingerprint $Key) -cne $ExpectedFingerprint) { throw 'UNTRUSTED_KEY' }
    if ($Payload.Length -gt 4096 -or $Signature.Length -ne ($Key.KeySize / 8)) { throw 'SCHEMA_INVALID' }
    try { $valid = $Key.VerifyData($Payload,$Signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1) } catch { throw 'AUTHENTICATION_FAILED' }
    if (-not $valid) { throw 'AUTHENTICATION_FAILED' }
    return ConvertFrom-P308c2Payload $Payload
}
function New-P308c2Signature {
    param([Parameter(Mandatory=$true)][byte[]] $Payload, [Parameter(Mandatory=$true)][Security.Cryptography.RSA] $Key)
    if ($Key.KeySize -lt 2048) { throw 'UNTRUSTED_KEY' }
    $null = ConvertFrom-P308c2Payload $Payload
    return ,$Key.SignData($Payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
}

