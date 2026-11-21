# Offline validation of trusted-operator-transferred synthetic receiver evidence.
# Windows PowerShell and PowerShell 7 deserialize JSON integers as Int32 and
# Int64 respectively. Accept only those integral types; never coerce strings,
# booleans or fractional numbers from untrusted evidence.
function Test-JsonInteger($Value) {
    return ($Value -is [int] -or $Value -is [long])
}

# This verifies the shape and run binding of the ordinary client record only.
# A syntactically valid PID does not establish process attribution of a flow.
function Assert-OrdinaryClientEvidence {
    param(
        [Parameter(Mandatory = $true)] $Record,
        [Parameter(Mandatory = $true)][guid] $RunId,
        [Parameter(Mandatory = $true)][guid] $Nonce,
        [Parameter(Mandatory = $true)][string] $ReceiverAddress,
        [Parameter(Mandatory = $true)][string] $ClientAddress,
        [Parameter(Mandatory = $true)][int] $Port
    )
    if (-not (Test-JsonInteger $Record.formatVersion) -or $Record.formatVersion -ne 2 -or
        $Record.runId -isnot [string] -or $Record.runId -cne $RunId.ToString('N') -or
        $Record.overallStatus -isnot [string] -or $Record.overallStatus -cne 'INCONCLUSIVE' -or
        $Record.stage -isnot [string] -or $Record.stage -cne 'Ordinary' -or
        $Record.transactionId -isnot [string] -or $Record.transactionId -cne $Nonce.ToString('N') -or
        $Record.receiverAddress -isnot [string] -or $Record.receiverAddress -cne $ReceiverAddress -or
        -not (Test-JsonInteger $Record.receiverPort) -or $Record.receiverPort -ne $Port -or
        $Record.clientAddress -isnot [string] -or $Record.clientAddress -cne $ClientAddress -or
        -not (Test-JsonInteger $Record.processId) -or $Record.processId -le 0 -or $Record.processId -gt [uint32]::MaxValue -or
        -not (Test-JsonInteger $Record.exitCode) -or $Record.exitCode -ne 0 -or
        $Record.clientResult -isnot [string] -or $Record.clientResult -cne 'sent' -or
        $Record.timedOut -isnot [bool] -or $Record.timedOut -or
        $Record.receiverObservation -isnot [string] -or $Record.receiverObservation -cne 'not-evaluated' -or
        $Record.processAttribution -isnot [string] -or $Record.processAttribution -cne 'unverified' -or
        $Record.wfpDecision -isnot [string] -or $Record.wfpDecision -cne 'not-observed') {
        throw 'Ordinary client did not complete successfully for this run.'
    }
}

function ConvertTo-EvidenceUtcInstant($Value) {
    $instant = [DateTimeOffset]::MinValue
    if ($Value -is [string]) {
        if (-not [DateTimeOffset]::TryParseExact($Value, 'O', [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref] $instant)) {
            throw 'Ordinary control evidence does not match this run.'
        }
    } elseif ($Value -is [DateTime]) {
        if ($Value.Kind -ne [DateTimeKind]::Utc) { throw 'Ordinary control evidence does not match this run.' }
        $instant = [DateTimeOffset]::new($Value)
    } elseif ($Value -is [DateTimeOffset]) {
        $instant = $Value
    } else { throw 'Ordinary control evidence does not match this run.' }
    if ($instant.Offset -ne [TimeSpan]::Zero) { throw 'Ordinary control evidence does not match this run.' }
    return $instant
}

function Assert-OrdinaryReceiverEvidence {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)][guid] $RunId,
        [Parameter(Mandatory = $true)][guid] $Nonce,
        [Parameter(Mandatory = $true)][string] $ReceiverAddress,
        [Parameter(Mandatory = $true)][string] $ClientAddress,
        [Parameter(Mandatory = $true)][int] $Port,
        [Parameter(Mandatory = $true)] $OrdinaryRecord,
        [Parameter(Mandatory = $true)][DateTimeOffset] $NowUtc,
        [Parameter(Mandatory = $true)][int] $MaxClockSkewSeconds,
        [Parameter(Mandatory = $true)][int] $MaxEvidenceAgeSeconds
    )
    if ($MaxClockSkewSeconds -lt 0 -or $MaxClockSkewSeconds -gt 300 -or
        $MaxEvidenceAgeSeconds -lt 1 -or $MaxEvidenceAgeSeconds -gt 3600 -or $NowUtc.Offset -ne [TimeSpan]::Zero) {
        throw 'Ordinary control evidence does not match this run.'
    }
    $utc = ConvertTo-EvidenceUtcInstant $Evidence.observedUtc
    $created = ConvertTo-EvidenceUtcInstant $OrdinaryRecord.processCreatedUtc
    $completed = ConvertTo-EvidenceUtcInstant $OrdinaryRecord.completedUtc
    try {
        $chronologyValid = $created -le $completed -and $completed -le $NowUtc -and
            $completed -ge $NowUtc.AddSeconds(-$MaxEvidenceAgeSeconds) -and
            $utc -ge $created.AddSeconds(-$MaxClockSkewSeconds) -and
            $utc -le $completed.AddSeconds($MaxClockSkewSeconds)
    } catch { $chronologyValid = $false }
    if (-not (Test-JsonInteger $Evidence.formatVersion) -or $Evidence.formatVersion -ne 2 -or
        $Evidence.runId -isnot [string] -or $Evidence.runId -cne $RunId.ToString('N') -or
        $Evidence.overallStatus -cne 'INCONCLUSIVE' -or $Evidence.classification -cne 'ALLOWED_AND_INDEPENDENTLY_OBSERVED' -or
        $Evidence.transactionId -cne $Nonce.ToString('N') -or
        $Evidence.receiverAddress -cne $ReceiverAddress -or -not (Test-JsonInteger $Evidence.receiverPort) -or $Evidence.receiverPort -ne $Port -or
        $Evidence.remoteAddress -cne $ClientAddress -or -not (Test-JsonInteger $Evidence.remotePort) -or $Evidence.remotePort -lt 1 -or $Evidence.remotePort -gt 65535 -or
        -not $chronologyValid -or
        $Evidence.transport -cne 'IPv4/TCP' -or -not (Test-JsonInteger $Evidence.observedMatches) -or $Evidence.observedMatches -ne 1 -or
        -not (Test-JsonInteger $Evidence.duplicateObservationWindowSeconds) -or $Evidence.duplicateObservationWindowSeconds -ne 2 -or
        $Evidence.duplicateCoverage -cne 'bounded-window-only' -or $Evidence.processAttribution -cne 'unverified' -or $Evidence.wfpDecision -cne 'not-observed') {
        throw 'Ordinary control evidence does not match this run.'
    }
}
