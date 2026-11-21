# Synthetic local validation; creates no sockets and modifies no policy.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'private-network-evidence.ps1')
$runId = [guid]'11111111-1111-1111-1111-111111111111'
$nonce = [guid]'22222222-2222-2222-2222-222222222222'
$now = [DateTimeOffset]::Parse('2026-11-20T12:00:00+00:00')
$created = $now.AddSeconds(-10)
$completed = $now.AddSeconds(-5)
$checks = 0
function Test-Case([string] $Name, [string] $Observed, [string] $Created, [string] $Completed,
                   [int] $Skew, [int] $Age, [bool] $ShouldPass) {
    $evidence = [pscustomobject]@{
        formatVersion = 2; runId = $runId.ToString('N'); overallStatus = 'INCONCLUSIVE';
        classification = 'ALLOWED_AND_INDEPENDENTLY_OBSERVED'; transactionId = $nonce.ToString('N');
        receiverAddress = '10.9.8.2'; receiverPort = 47123; remoteAddress = '10.9.8.1'; remotePort = 50000;
        observedUtc = $Observed; transport = 'IPv4/TCP'; observedMatches = 1;
        duplicateObservationWindowSeconds = 2; duplicateCoverage = 'bounded-window-only';
        processAttribution = 'unverified'; wfpDecision = 'not-observed'
    }
    $ordinary = [pscustomobject]@{ processCreatedUtc = $Created; completedUtc = $Completed }
    $accepted = $false
    try {
        Assert-OrdinaryReceiverEvidence -Evidence $evidence -RunId $runId -Nonce $nonce `
            -ReceiverAddress '10.9.8.2' -ClientAddress '10.9.8.1' -Port 47123 `
            -OrdinaryRecord $ordinary -NowUtc $now -MaxClockSkewSeconds $Skew -MaxEvidenceAgeSeconds $Age
        $accepted = $true
    } catch {
        if ($_.Exception.Message -ne 'Ordinary control evidence does not match this run.') { throw }
    }
    if ($accepted -ne $ShouldPass) { throw ('FAIL: ' + $Name) }
    $script:checks++
    Write-Output ('PASS: ' + $Name)
}
$start = $created.ToString('O')
$end = $completed.ToString('O')
Test-Case 'observation within client lifetime' ($now.AddSeconds(-8).ToString('O')) $start $end 2 60 $true
Test-Case 'lower skew boundary accepted' ($created.AddSeconds(-2).ToString('O')) $start $end 2 60 $true
Test-Case 'outside lower skew rejected' ($created.AddSeconds(-2).AddMilliseconds(-1).ToString('O')) $start $end 2 60 $false
Test-Case 'upper skew boundary accepted' ($completed.AddSeconds(2).ToString('O')) $start $end 2 60 $true
Test-Case 'outside upper skew rejected' ($completed.AddSeconds(2).AddMilliseconds(1).ToString('O')) $start $end 2 60 $false
Test-Case 'UTC DateTime Z format accepted' ($now.AddSeconds(-8).UtcDateTime.ToString('O')) ($created.UtcDateTime.ToString('O')) ($completed.UtcDateTime.ToString('O')) 2 60 $true
Test-Case 'stale receiver observation rejected' '2020-01-01T00:00:00.0000000+00:00' $start $end 2 60 $false
Test-Case 'future receiver observation rejected' ($now.AddSeconds(1).ToString('O')) $start $end 2 60 $false
Test-Case 'client completion before creation rejected' ($now.AddSeconds(-8).ToString('O')) $end $start 2 60 $false
Test-Case 'future same-host client completion rejected' ($now.ToString('O')) $start ($now.AddSeconds(1).ToString('O')) 2 60 $false
Test-Case 'expired client record rejected' ($now.AddSeconds(-65).ToString('O')) ($now.AddSeconds(-70).ToString('O')) ($now.AddSeconds(-61).ToString('O')) 2 60 $false
Test-Case 'age boundary accepted' ($now.AddSeconds(-65).ToString('O')) ($now.AddSeconds(-70).ToString('O')) ($now.AddSeconds(-60).ToString('O')) 2 60 $true
Test-Case 'non-UTC receiver timestamp rejected' '2026-11-20T13:00:00.0000000+01:00' $start $end 2 60 $false
Test-Case 'malformed client creation time rejected' ($now.AddSeconds(-8).ToString('O')) 'invalid' $end 2 60 $false
Test-Case 'missing client completion time rejected' ($now.AddSeconds(-8).ToString('O')) $start '' 2 60 $false
Test-Case 'non-UTC client timestamp rejected' ($now.AddSeconds(-8).ToString('O')) '2026-11-20T12:59:50.0000000+01:00' $end 2 60 $false
Test-Case 'negative clock bound rejected' ($now.AddSeconds(-8).ToString('O')) $start $end -1 60 $false
Test-Case 'excessive clock bound rejected' ($now.AddSeconds(-8).ToString('O')) $start $end 301 60 $false
Test-Case 'zero evidence age rejected' ($now.AddSeconds(-8).ToString('O')) $start $end 2 0 $false
Test-Case 'excessive evidence age rejected' ($now.AddSeconds(-8).ToString('O')) $start $end 2 3601 $false
$parsedDateTime = ConvertTo-EvidenceUtcInstant $now.UtcDateTime
$parsedOffset = ConvertTo-EvidenceUtcInstant $now
if ($parsedDateTime -ne $now -or $parsedOffset -ne $now) { throw 'FAIL: typed UTC evidence values' }
$checks++; Write-Output 'PASS: typed UTC DateTime and DateTimeOffset accepted'
$unspecifiedRejected = $false
try { $null = ConvertTo-EvidenceUtcInstant ([DateTime]::SpecifyKind($now.UtcDateTime, [DateTimeKind]::Unspecified)) }
catch { $unspecifiedRejected = $true }
if (-not $unspecifiedRejected) { throw 'FAIL: unspecified time kind accepted' }
$checks++; Write-Output 'PASS: unspecified time kind rejected'
$localRejected = $false
try { $null = ConvertTo-EvidenceUtcInstant ([DateTime]::SpecifyKind($now.UtcDateTime, [DateTimeKind]::Local)) }
catch { $localRejected = $true }
if (-not $localRejected) { throw 'FAIL: local time kind accepted' }
$checks++; Write-Output 'PASS: local time kind rejected'
# The receiver uses DateTime.UtcNow.ToString('O'), which ends in Z. Check the
# actual ConvertFrom-Json representation on both supported PowerShell hosts.
$roundtrip = (@{ observedUtc = $now.UtcDateTime.ToString('O') } | ConvertTo-Json -Compress | ConvertFrom-Json)
if ((ConvertTo-EvidenceUtcInstant $roundtrip.observedUtc) -ne $now) { throw 'FAIL: UTC Z receiver JSON round trip' }
$checks++; Write-Output 'PASS: UTC Z receiver JSON round trip'
function Test-ClientRecord([string] $Name, $PidValue, [bool] $ShouldPass, [string] $Change = '') {
    $record = [pscustomobject]@{
        formatVersion = 2; runId = $runId.ToString('N'); overallStatus = 'INCONCLUSIVE'; stage = 'Ordinary';
        transactionId = $nonce.ToString('N'); receiverAddress = '10.9.8.2'; receiverPort = 47123;
        clientAddress = '10.9.8.1'; processId = $PidValue; exitCode = 0; timedOut = $false; clientResult = 'sent';
        receiverObservation = 'not-evaluated'; processAttribution = 'unverified'; wfpDecision = 'not-observed'
    }
    switch ($Change) {
        'missingPid' { $record.PSObject.Properties.Remove('processId') }
        'wrongRun' { $record.runId = '00000000000000000000000000000000' }
        'arrayStatus' { $record.overallStatus = @('INCONCLUSIVE') }
        'wrongAttribution' { $record.processAttribution = 'verified' }
        'wrongDecision' { $record.wfpDecision = 'allowed' }
        'timeout' { $record.timedOut = $true }
    }
    $accepted = $false
    try {
        Assert-OrdinaryClientEvidence -Record $record -RunId $runId -Nonce $nonce `
            -ReceiverAddress '10.9.8.2' -ClientAddress '10.9.8.1' -Port 47123
        $accepted = $true
    } catch {
        if ($_.Exception.Message -ne 'Ordinary client did not complete successfully for this run.') { throw }
    }
    if ($accepted -ne $ShouldPass) { throw ('FAIL: ' + $Name) }
    $script:checks++; Write-Output ('PASS: ' + $Name)
}
Test-ClientRecord 'ordinary client record accepted' 1234 $true
Test-ClientRecord 'zero process ID rejected' 0 $false
Test-ClientRecord 'negative process ID rejected' -1 $false
Test-ClientRecord 'missing process ID rejected' 1 $false 'missingPid'
Test-ClientRecord 'string process ID rejected' '1234' $false
Test-ClientRecord 'fractional process ID rejected' 1.5 $false
Test-ClientRecord 'out-of-range process ID rejected' 4294967296L $false
Test-ClientRecord 'wrong run rejected' 1234 $false 'wrongRun'
Test-ClientRecord 'array status rejected' 1234 $false 'arrayStatus'
Test-ClientRecord 'unverified attribution cannot be promoted' 1234 $false 'wrongAttribution'
Test-ClientRecord 'unobserved WFP decision cannot be promoted' 1234 $false 'wrongDecision'
Test-ClientRecord 'timed-out client rejected' 1234 $false 'timeout'
Write-Output ("PASS: $checks synthetic chronology checks; no sockets, WFP or CEF tests executed.")
