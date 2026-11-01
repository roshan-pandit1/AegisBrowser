# Offline validation of trusted-operator-transferred synthetic receiver evidence.
# Windows PowerShell and PowerShell 7 deserialize JSON integers as Int32 and
# Int64 respectively. Accept only those integral types; never coerce strings,
# booleans or fractional numbers from untrusted evidence.
function Test-JsonInteger($Value) {
    return ($Value -is [int] -or $Value -is [long])
}

function Assert-OrdinaryReceiverEvidence {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)][guid] $RunId,
        [Parameter(Mandatory = $true)][guid] $Nonce,
        [Parameter(Mandatory = $true)][string] $ReceiverAddress,
        [Parameter(Mandatory = $true)][string] $ClientAddress,
        [Parameter(Mandatory = $true)][int] $Port
    )
    $utc = [DateTimeOffset]::MinValue
    $utcValid = $false
    if ($Evidence.observedUtc -is [string]) {
        $utcValid = [DateTimeOffset]::TryParseExact($Evidence.observedUtc, 'O', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $utc) -and $utc.Offset -eq [TimeSpan]::Zero
    } elseif ($Evidence.observedUtc -is [DateTime]) {
        # Some PowerShell versions deserialize JSON timestamps as DateTime.
        $utcValid = $Evidence.observedUtc.Kind -eq [DateTimeKind]::Utc
    } elseif ($Evidence.observedUtc -is [DateTimeOffset]) {
        $utcValid = $Evidence.observedUtc.Offset -eq [TimeSpan]::Zero
    }
    if (-not (Test-JsonInteger $Evidence.formatVersion) -or $Evidence.formatVersion -ne 2 -or
        $Evidence.runId -isnot [string] -or $Evidence.runId -cne $RunId.ToString('N') -or
        $Evidence.overallStatus -cne 'INCONCLUSIVE' -or $Evidence.classification -cne 'ALLOWED_AND_INDEPENDENTLY_OBSERVED' -or
        $Evidence.transactionId -cne $Nonce.ToString('N') -or
        $Evidence.receiverAddress -cne $ReceiverAddress -or -not (Test-JsonInteger $Evidence.receiverPort) -or $Evidence.receiverPort -ne $Port -or
        $Evidence.remoteAddress -cne $ClientAddress -or -not (Test-JsonInteger $Evidence.remotePort) -or $Evidence.remotePort -lt 1 -or $Evidence.remotePort -gt 65535 -or
        -not $utcValid -or
        $Evidence.transport -cne 'IPv4/TCP' -or -not (Test-JsonInteger $Evidence.observedMatches) -or $Evidence.observedMatches -ne 1 -or
        -not (Test-JsonInteger $Evidence.duplicateObservationWindowSeconds) -or $Evidence.duplicateObservationWindowSeconds -ne 2 -or
        $Evidence.duplicateCoverage -cne 'bounded-window-only' -or $Evidence.processAttribution -cne 'unverified' -or $Evidence.wfpDecision -cne 'not-observed') {
        throw 'Ordinary control evidence does not match this run.'
    }
}
