# Ephemeral test keys only; provisioning command is checked only before its opt-in gate.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p308c2-record-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-key-lib.ps1')
$checks=0
function Reject([scriptblock] $action,[string] $message) {
    $failed=$false
    try { & $action | Out-Null } catch { if ($_.Exception.Message -notlike "*$message*") { throw }; $failed=$true }
    if (-not $failed) { throw "Expected rejection: $message" }
    $script:checks++
}
$key=New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$key.PersistKeyInCsp=$false
try {
    if ((Get-P308c3KeyName -Role VmA -Identity 'test-vm-a') -cne 'Aegis-P308C3-VmA-test-vm-a') { throw 'Incorrect role name' }; $checks++
    Reject { Get-P308c3KeyName -Role VmA -Identity '../escape' } 'INVALID_KEY_IDENTITY'
    Reject { Get-P308c3KeyName -Role VmA -Identity 'UPPERCASE' } 'INVALID_KEY_IDENTITY'
    Reject { Get-P308c3KeyName -Role vma -Identity 'test-vm-a' } 'INVALID_KEY_ROLE'
    Test-P308c3SigningChallenge $key; $checks++
    $record=Get-P308c3PublicEnrollment -Key $key -Role VmA -Identity 'test-vm-a'
    if ($record.role -cne 'VmA' -or $record.fingerprint -cne (Get-P308c2Fingerprint $key) -or $record.modulus -ne [Convert]::ToBase64String($key.ExportParameters($false).Modulus) -or $record.Contains('private')) { throw 'Bad enrollment' }; $checks++
    $script=Join-Path $PSScriptRoot 'provision-p308c3-signing-key.ps1'
    $missing=Join-Path $PSScriptRoot 'must-not-create-p308c3-enrollment.json'
    if (Test-Path -LiteralPath $missing) { throw 'Refusing to use existing evidence path' }
    Reject { & $script -Role VmA -Identity 'test-vm-a' -EnrollmentPath $missing } 'DISPOSABLE_VM_ACKNOWLEDGEMENT_REQUIRED'
    if (Test-Path -LiteralPath $missing) { throw 'Unexpected output' }; $checks++
    # Unnamed CNG keys are ephemeral. Exercise the same RSA-CNG settings without
    # provisioning a persistent identity on this workstation.
    $options=New-Object Security.Cryptography.CngKeyCreationParameters
    $options.Provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $options.ExportPolicy=[Security.Cryptography.CngExportPolicies]::None
    $options.KeyUsage=[Security.Cryptography.CngKeyUsages]::Signing
    $length=New-Object Security.Cryptography.CngProperty('Length',[BitConverter]::GetBytes([int]3072),[Security.Cryptography.CngPropertyOptions]::None)
    $options.Parameters.Add($length)
    $cng=[Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,[System.Management.Automation.Language.NullString]::Value,$options)
    try {
        if (-not $cng.IsEphemeral -or $cng.KeySize -ne 3072 -or $cng.ExportPolicy -ne [Security.Cryptography.CngExportPolicies]::None -or $cng.IsMachineKey) { throw 'Unsafe ephemeral CNG settings' }
        $rsa=New-Object Security.Cryptography.RSACng($cng)
        try { Test-P308c3SigningChallenge $rsa } finally { $rsa.Dispose() }
        $checks++
    } finally { $cng.Dispose() }
    Write-Output "PASS: $checks offline P3-08C3 key checks; no persistent key created."
} finally { $key.Dispose() }
