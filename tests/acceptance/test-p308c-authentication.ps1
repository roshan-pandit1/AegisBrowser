# Synthetic ephemeral keys and records only; no network, persistent keys, or policy changes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c-authentication-lib.ps1')
$run = [guid]::NewGuid(); $txn = [guid]::NewGuid()
$keyA = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$keyB = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$keyA.PersistKeyInCsp = $false; $keyB.PersistKeyInCsp = $false
$hash = [Security.Cryptography.HashAlgorithmName]::SHA256
$padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
$checks = 0
try {
    $lines = @('protocol=AegisCrossVmEvidence','version=1','algorithm=RSA-PKCS1-SHA256','role=VmA','type=client',('run=' + $run.ToString('N')),('transaction=' + $txn.ToString('N')),'client=10.9.8.1','receiver=10.9.8.2','port=47123','transport=IPv4/TCP','observedUtc=2026-11-04T10:00:00.0000000Z')
    function Bytes($rows) { return ,([Text.Encoding]::ASCII.GetBytes(($rows -join "`n") + "`n")) }
    function Verify($data, $sig, $key, $role, $expectedRun, $expectedTxn) {
        Test-P308cSignedRecord -Payload $data -Signature $sig -PinnedKey $key -ExpectedRole $role -ExpectedRun $expectedRun -ExpectedTransaction $expectedTxn -ExpectedClient '10.9.8.1' -ExpectedReceiver '10.9.8.2' -ExpectedPort 47123
    }
    function Reject([scriptblock] $action, [string] $expected) {
        $caught = $false
        try { & $action | Out-Null } catch { if ($_.Exception.Message -cne $expected) { throw }; $caught = $true }
        if (-not $caught) { throw "Expected $expected" }
        $script:checks++
    }
    $data = Bytes $lines; $sig = $keyA.SignData($data, $hash, $padding)
    if ((Verify $data $sig $keyA 'VmA' $run $txn) -cne 'INCONCLUSIVE') { throw 'Unsafe success classification.' }; $checks++
    Reject { Verify $data $sig $keyB 'VmA' $run $txn } 'AUTHENTICATION_FAILED'
    Reject { Verify $data $sig $keyA 'VmB' $run $txn } 'EVIDENCE_MISMATCH'
    Reject { Verify $data $sig $keyA 'VmA' ([guid]::NewGuid()) $txn } 'EVIDENCE_MISMATCH'
    Reject { Verify $data $sig $keyA 'VmA' $run ([guid]::NewGuid()) } 'EVIDENCE_MISMATCH'
    $changed = [byte[]] $data.Clone(); $changed[0] = 120
    Reject { Verify $changed $sig $keyA 'VmA' $run $txn } 'AUTHENTICATION_FAILED'
    $short = [byte[]]::new($sig.Length - 1); [Array]::Copy($sig,$short,$short.Length)
    Reject { Verify $data $short $keyA 'VmA' $run $txn } 'SCHEMA_INVALID'
    foreach ($case in @(
        @{index=0; value='protocol=Other'; expected='SCHEMA_INVALID'},
        @{index=1; value='version=2'; expected='SCHEMA_INVALID'},
        @{index=2; value='algorithm=none'; expected='SCHEMA_INVALID'},
        @{index=4; value='type=receiver'; expected='EVIDENCE_MISMATCH'},
        @{index=7; value='client=10.9.8.3'; expected='EVIDENCE_MISMATCH'},
        @{index=8; value='receiver=10.9.8.3'; expected='EVIDENCE_MISMATCH'},
        @{index=9; value='port=047123'; expected='EVIDENCE_MISMATCH'},
        @{index=10; value='transport=IPv6/TCP'; expected='SCHEMA_INVALID'},
        @{index=11; value='observedUtc=not-a-time'; expected='SCHEMA_INVALID'}
    )) {
        $copy = [string[]] $lines.Clone(); $copy[$case.index] = $case.value
        $bad = Bytes $copy; $badSig = $keyA.SignData($bad, $hash, $padding)
        Reject { Verify $bad $badSig $keyA 'VmA' $run $txn } $case.expected
    }
    $bad = Bytes ($lines + 'role=VmB'); $badSig = $keyA.SignData($bad,$hash,$padding)
    Reject { Verify $bad $badSig $keyA 'VmA' $run $txn } 'SCHEMA_INVALID'
    $bad = Bytes $lines[0..10]; $badSig = $keyA.SignData($bad,$hash,$padding)
    Reject { Verify $bad $badSig $keyA 'VmA' $run $txn } 'SCHEMA_INVALID'
    $bad = [byte[]]::new(2049)
    Reject { Verify $bad $sig $keyA 'VmA' $run $txn } 'SCHEMA_INVALID'
    Reject { Test-P308cSignedRecord -Payload $data -Signature $sig -PinnedKey $keyA -ExpectedRole VmA -ExpectedRun $run -ExpectedTransaction $txn -ExpectedClient '8.8.8.8' -ExpectedReceiver '10.9.8.2' -ExpectedPort 47123 } 'A canonical RFC1918 IPv4 literal is required.'
    $bad = [byte[]] $data.Clone(); $bad[0] = 255; $badSig = $keyA.SignData($bad,$hash,$padding)
    Reject { Verify $bad $badSig $keyA 'VmA' $run $txn } 'SCHEMA_INVALID'
    $bLines = [string[]] $lines.Clone(); $bLines[3] = 'role=VmB'; $bLines[4] = 'type=receiver'
    $bData = Bytes $bLines; $bSig = $keyB.SignData($bData,$hash,$padding)
    if ((Verify $bData $bSig $keyB 'VmB' $run $txn) -cne 'INCONCLUSIVE') { throw 'Unsafe VM B classification.' }; $checks++
    Reject { Verify $bData $bSig $keyA 'VmB' $run $txn } 'AUTHENTICATION_FAILED'
    Write-Output "PASS: $checks offline signed-record checks; replay and cross-VM completion remain unverified."
} finally { $keyA.Dispose(); $keyB.Dispose() }
