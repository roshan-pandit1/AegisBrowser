# Research-only CNG identity helpers. Loading this file creates no key.
# A CNG key is scoped to the current Windows user; the same-user and administrator
# compromise are outside this trust boundary. Only public parameters are exported.
function Get-P308c3KeyName {
    param([Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $Role,
          [Parameter(Mandatory=$true)][string] $Identity)
    if ($Role -cnotin @('VmA','VmB')) { throw 'INVALID_KEY_ROLE' }
    if ($Identity -cnotmatch '^[a-z0-9][a-z0-9-]{6,38}[a-z0-9]$') { throw 'INVALID_KEY_IDENTITY' }
    return "Aegis-P308C3-$Role-$Identity"
}
function Get-P308c3PublicEnrollment {
    param([Parameter(Mandatory=$true)][Security.Cryptography.RSA] $Key,
          [Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $Role,
          [Parameter(Mandatory=$true)][string] $Identity)
    $name = Get-P308c3KeyName -Role $Role -Identity $Identity
    $p = $Key.ExportParameters($false)
    return [ordered]@{ protocol='AegisP308C3Enrollment'; version=1; role=$Role; identity=$Identity;
        keyName=$name; algorithm='RSA-PKCS1-SHA256'; fingerprint=(Get-P308c2Fingerprint $Key);
        modulus=[Convert]::ToBase64String($p.Modulus); exponent=[Convert]::ToBase64String($p.Exponent) }
}
function Test-P308c3SigningChallenge {
    param([Parameter(Mandatory=$true)][Security.Cryptography.RSA] $Key)
    if ($Key.KeySize -lt 2048) { throw 'UNTRUSTED_KEY' }
    $challenge = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($challenge) } finally { $rng.Dispose() }
    $signature = $Key.SignData($challenge,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $public = [Security.Cryptography.RSA]::Create()
    try {
        $public.ImportParameters($Key.ExportParameters($false))
        if (-not $public.VerifyData($challenge,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'SIGNING_CHALLENGE_FAILED' }
    } finally { $public.Dispose() }
}
function Open-P308c3SigningKey {
    param([Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $Role,
          [Parameter(Mandatory=$true)][string] $Identity)
    $name = Get-P308c3KeyName -Role $Role -Identity $Identity
    $provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $cng = [Security.Cryptography.CngKey]::Open($name,$provider,[Security.Cryptography.CngKeyOpenOptions]::None)
    try {
        if ($cng.IsMachineKey -or $cng.AlgorithmGroup -ne [Security.Cryptography.CngAlgorithmGroup]::Rsa -or
            $cng.ExportPolicy -ne [Security.Cryptography.CngExportPolicies]::None -or
            ($cng.KeyUsage -band [Security.Cryptography.CngKeyUsages]::Signing) -eq 0 -or $cng.KeySize -lt 2048) { throw 'UNTRUSTED_KEY' }
        # RSACng takes its own key handle; dispose the original CngKey below.
        $rsa = New-Object Security.Cryptography.RSACng($cng)
        return $rsa
    } finally { $cng.Dispose() }
}
