$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sandbox-preflight-lib.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null; return $false }
    catch { return $_.Exception.Message -like "*$Expected*" }
}
function Write-SyntheticPe([string]$Path, [int]$Machine, [bool]$Dll) {
    # A non-executable synthetic header, solely to exercise the parser.
    $bytes = New-Object byte[] 160
    [BitConverter]::GetBytes([uint16]0x5A4D).CopyTo($bytes, 0)
    [BitConverter]::GetBytes([uint32]64).CopyTo($bytes, 0x3C)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes, 64)
    [BitConverter]::GetBytes([uint16]$Machine).CopyTo($bytes, 68)
    [BitConverter]::GetBytes([uint16]2).CopyTo($bytes, 84)
    if ($Dll) { [BitConverter]::GetBytes([uint16]0x2000).CopyTo($bytes, 86) }
    [BitConverter]::GetBytes([uint16]0x20B).CopyTo($bytes, 88)
    [IO.File]::WriteAllBytes($Path, $bytes)
}
try {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('aegis-p309f-preflight-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $boot = Join-Path $root 'bootstrap.exe'
    $client = Join-Path $root 'client.dll'
    $cef = Join-Path $root 'libcef.dll'
    Write-SyntheticPe $boot 0x8664 $false
    Write-SyntheticPe $client 0x8664 $true
    Write-SyntheticPe $cef 0x8664 $true
    $bootEvidence = Get-P309fFileEvidence $boot 'Bootstrap'
    $clientEvidence = Get-P309fFileEvidence $client 'Client'
    $cefEvidence = Get-P309fFileEvidence $cef 'LibCef'
    Check ($bootEvidence.Sha256 -match '^[A-F0-9]{64}$') 'synthetic bootstrap SHA-256 calculated'
    Check ($clientEvidence.Role -ceq 'Client') 'synthetic x64 client parsed'
    Check ($cefEvidence.Role -ceq 'LibCef') 'synthetic x64 libcef parsed'
    # The fake image has no authentic Windows version resource. Assigning this
    # value is solely a unit fixture and is never treated as native evidence.
    $cefEvidence.FileVersion = $script:P309fExpectedCefVersion
    $records = @($bootEvidence, $clientEvidence, $cefEvidence)
    $pins = @{ Bootstrap=$bootEvidence.Sha256; Client=$clientEvidence.Sha256; LibCef=$cefEvidence.Sha256 }
    $result = Test-P309fEvidenceSet $records $pins
    Check ($result.Outcome -ceq 'REVIEW_REQUIRED') 'matching synthetic inputs do not verify isolation or sandbox'
    Check ($result.SandboxInfo -ceq 'NOT_OBSERVED' -and $result.SandboxEnforcement -ceq 'NOT_RUN') 'sandbox data remains unobserved'
    Check ($result.CefStartup -ceq 'NOT_RUN' -and $result.CefShutdown -ceq 'NOT_RUN') 'CEF is never started'
    $badPins = @{ Bootstrap=('0'*64); Client=$clientEvidence.Sha256; LibCef=$cefEvidence.Sha256 }
    Check (Reject { Test-P309fEvidenceSet $records $badPins } 'HASH_MISMATCH') 'wrong trusted hash rejected'
    $badPins.Bootstrap = 'nope'
    Check (Reject { Test-P309fEvidenceSet $records $badPins } 'INVALID_PIN') 'malformed trusted hash rejected'
    $wrongVersion = [pscustomobject]@{ Role='LibCef'; Path=$cef; Sha256=$cefEvidence.Sha256; FileVersion='154.0.1' }
    Check (Reject { Test-P309fEvidenceSet @($bootEvidence,$clientEvidence,$wrongVersion) $pins } 'CEF_VERSION_MISMATCH') 'wrong CEF runtime version rejected'
    Check (Reject { Test-P309fEvidenceSet @($bootEvidence,$clientEvidence) $pins } 'INCOMPLETE_EVIDENCE') 'missing role rejected'
    Check (Reject { Test-P309fEvidenceSet @($bootEvidence,$clientEvidence,$clientEvidence) $pins } 'INVALID_ROLE_SET') 'duplicate role rejected'
    $samePath = [pscustomobject]@{ Role='LibCef'; Path=$client; Sha256=$cefEvidence.Sha256; FileVersion=$script:P309fExpectedCefVersion }
    Check (Reject { Test-P309fEvidenceSet @($bootEvidence,$clientEvidence,$samePath) $pins } 'DUPLICATE_INPUT') 'same input path rejected'
    Check (Reject { Get-P309fFileEvidence 'relative.exe' 'Bootstrap' } 'PATH_NOT_ABSOLUTE_LOCAL') 'relative path rejected'
    Check (Reject { Get-P309fFileEvidence 'C:relative.exe' 'Bootstrap' } 'PATH_NOT_ABSOLUTE_LOCAL') 'drive-relative path rejected'
    Check (Reject { Get-P309fFileEvidence '\\server\share\bootstrap.exe' 'Bootstrap' } 'PATH_NOT_ABSOLUTE_LOCAL') 'remote UNC path rejected without opening it'
    Check (Reject { Get-P309fFileEvidence 'C:\bootstrap.exe:stream.exe' 'Bootstrap' } 'ALTERNATE_STREAM_REJECTED') 'alternate data stream rejected'
    Check (Reject { Get-P309fFileEvidence (Join-Path $root 'missing.exe') 'Bootstrap' } 'Cannot find path') 'missing file rejected'
    Check (Reject { Get-P309fFileEvidence $client 'Bootstrap' } 'INVALID_EXTENSION') 'wrong file extension rejected'
    Check (Reject { Get-P309fFileEvidence $boot 'Client' } 'INVALID_EXTENSION') 'executable cannot stand in for client DLL'
    $wrongCpu = Join-Path $root 'wrongcpu.exe'; Write-SyntheticPe $wrongCpu 0x014c $false
    Check (Reject { Get-P309fFileEvidence $wrongCpu 'Bootstrap' } 'PE_MACHINE_NOT_X64') 'wrong CPU architecture rejected'
    $wrongRole = Join-Path $root 'wrongrole.exe'; Write-SyntheticPe $wrongRole 0x8664 $true
    Check (Reject { Get-P309fFileEvidence $wrongRole 'Bootstrap' } 'PE_ROLE_MISMATCH') 'DLL header cannot stand in for bootstrap executable'
    $tiny = Join-Path $root 'tiny.exe'; [IO.File]::WriteAllBytes($tiny, [byte[]]@(1,2,3))
    Check (Reject { Get-P309fFileEvidence $tiny 'Bootstrap' } 'FILE_SIZE_INVALID') 'truncated file rejected'
    $badDos = Join-Path $root 'baddos.exe'; Write-SyntheticPe $badDos 0x8664 $false
    $bytes = [IO.File]::ReadAllBytes($badDos); $bytes[0] = 0; [IO.File]::WriteAllBytes($badDos, $bytes)
    Check (Reject { Get-P309fFileEvidence $badDos 'Bootstrap' } 'PE_DOS_SIGNATURE_INVALID') 'malformed DOS signature rejected'
    $badPe = Join-Path $root 'badpe.exe'; Write-SyntheticPe $badPe 0x8664 $false
    $bytes = [IO.File]::ReadAllBytes($badPe); $bytes[64] = 0; [IO.File]::WriteAllBytes($badPe, $bytes)
    Check (Reject { Get-P309fFileEvidence $badPe 'Bootstrap' } 'PE_SIGNATURE_INVALID') 'malformed PE signature rejected'
    $badMagic = Join-Path $root 'badmagic.exe'; Write-SyntheticPe $badMagic 0x8664 $false
    $bytes = [IO.File]::ReadAllBytes($badMagic); $bytes[88] = 0; [IO.File]::WriteAllBytes($badMagic, $bytes)
    Check (Reject { Get-P309fFileEvidence $badMagic 'Bootstrap' } 'PE_NOT_PE32PLUS') 'non-PE32+ header rejected'
    Write-Output "PASS: $checks sandbox preflight checks (synthetic; no CEF execution or sandbox verification)"
    Write-Output "SYNTHETIC_TEMP_DIRECTORY=$root"
    exit 0
}
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
