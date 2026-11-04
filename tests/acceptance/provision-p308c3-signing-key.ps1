# Creates one new, non-exportable, user-scoped research key ONLY in an authorized disposable VM.
# Never runs as part of CI or normal workstation validation.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('VmA','VmB')][string] $Role,
    [Parameter(Mandatory=$true)][string] $Identity,
    [Parameter(Mandatory=$true)][string] $EnrollmentPath,
    [switch] $AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference='Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') { throw 'DISPOSABLE_VM_ACKNOWLEDGEMENT_REQUIRED' }
if ($env:OS -cne 'Windows_NT') { throw 'WINDOWS_REQUIRED' }
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-key-lib.ps1')
$name = Get-P308c3KeyName -Role $Role -Identity $Identity
if (-not [IO.Path]::IsPathRooted($EnrollmentPath) -or $EnrollmentPath -notmatch '^[A-Za-z]:\\' -or $EnrollmentPath.Substring(2).Contains(':')) { throw 'UNSAFE_ENROLLMENT_PATH' }
$path = [IO.Path]::GetFullPath($EnrollmentPath)
$cursor = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($path)) -Force -ErrorAction Stop
while ($null -ne $cursor) {
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'UNSAFE_ENROLLMENT_PATH' }
    $cursor = $cursor.Parent
}
# Reserve the public output exclusively before creating the key. A failed run leaves
# the output reserved for investigation and never silently replaces either object.
$output = New-Object IO.FileStream($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    $provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $options = New-Object Security.Cryptography.CngKeyCreationParameters
    $options.Provider = $provider
    $options.KeyCreationOptions = [Security.Cryptography.CngKeyCreationOptions]::None
    $options.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $options.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $length = New-Object Security.Cryptography.CngProperty('Length',[BitConverter]::GetBytes([int]3072),[Security.Cryptography.CngPropertyOptions]::None)
    $options.Parameters.Add($length)
    # No overwrite flag: an existing name is a hard error.
    $created = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,$name,$options)
    $created.Dispose()
    $key = Open-P308c3SigningKey -Role $Role -Identity $Identity
    try {
        Test-P308c3SigningChallenge $key
        $public = Get-P308c3PublicEnrollment -Key $key -Role $Role -Identity $Identity
        $encoded = [Text.Encoding]::UTF8.GetBytes(($public | ConvertTo-Json -Compress) + "`n")
        $output.Write($encoded,0,$encoded.Length); $output.Flush($true)
        Write-Output "CREATED: $name; public fingerprint $($public.fingerprint); enrollment $path"
    } finally { $key.Dispose() }
} finally { $output.Dispose() }
