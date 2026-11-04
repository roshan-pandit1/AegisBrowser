# A rigid ordered ASCII document avoids JSON duplicate-key and numeric ambiguities.
function Read-OperatorManifest([string]$Path,[string]$ExpectedSha256,[string[]]$Names) {
 if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'SCHEMA_INVALID' }
 $data=Read-P308c2EvidenceFile -Path $Path -MaximumBytes 16384
 $sha=[Security.Cryptography.SHA256]::Create()
 try { $actual=([BitConverter]::ToString($sha.ComputeHash($data))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
 if ($actual -cne $ExpectedSha256) { throw 'UNTRUSTED_KEY' }
 foreach($byte in $data) { if (($byte -lt 32 -or $byte -gt 126) -and $byte -ne 10) { throw 'SCHEMA_INVALID' } }
 $lines=[Text.Encoding]::ASCII.GetString($data).Split([char]10)
 if ($lines.Length -ne ($Names.Count+1) -or $lines[-1] -cne '') { throw 'SCHEMA_INVALID' }
 $map=[ordered]@{}
 for($i=0;$i -lt $Names.Count;$i++) {
  $prefix=$Names[$i]+'='
  if (-not $lines[$i].StartsWith($prefix,[StringComparison]::Ordinal)) { throw 'SCHEMA_INVALID' }
  $value=$lines[$i].Substring($prefix.Length)
  if ([string]::IsNullOrEmpty($value)) { throw 'SCHEMA_INVALID' }
  $map[$Names[$i]]=$value
 }
 return $map
}
function Import-ApprovedPublicKey([string]$Modulus,[string]$Exponent) {
 try {
  if ($Modulus -cnotmatch '^[A-Za-z0-9+/]+={0,2}$' -or $Exponent -cnotmatch '^[A-Za-z0-9+/]+={0,2}$') { throw 'UNTRUSTED_KEY' }
  $m=[Convert]::FromBase64String($Modulus); $e=[Convert]::FromBase64String($Exponent)
  if ([Convert]::ToBase64String($m) -cne $Modulus -or [Convert]::ToBase64String($e) -cne $Exponent -or $m.Length -lt 256 -or $m.Length -gt 1024 -or $m[0] -eq 0 -or $e.Length -ne 3 -or $e[0] -ne 1 -or $e[1] -ne 0 -or $e[2] -ne 1) { throw 'UNTRUSTED_KEY' }
  $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
  $rsa.PersistKeyInCsp=$false
  $parameters=New-Object Security.Cryptography.RSAParameters
  $parameters.Modulus=$m; $parameters.Exponent=$e
  $rsa.ImportParameters($parameters)
  return $rsa
 } catch { if ($null -ne $rsa) { $rsa.Dispose() }; throw 'UNTRUSTED_KEY' }
}
function Get-CanonicalGuid([string]$Text) {
 $value=[guid]::Empty
 if ($Text -cnotmatch '^[0-9a-f]{32}$' -or -not [guid]::TryParseExact($Text,'N',[ref]$value) -or $value -eq [guid]::Empty) { throw 'SCHEMA_INVALID' }
 return $value
}
function Get-CanonicalTime([string]$Text) {
 $value=[DateTimeOffset]::MinValue
 $format="yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
 if (-not [DateTimeOffset]::TryParseExact($Text,$format,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$value) -or $value.UtcDateTime.ToString($format,[Globalization.CultureInfo]::InvariantCulture) -cne $Text) { throw 'SCHEMA_INVALID' }
 return $value
}
