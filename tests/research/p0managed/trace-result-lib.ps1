Set-StrictMode -Version Latest

function Get-P0TraceCorrelation {
    param([Parameter(Mandatory)][object[]] $Decoded)

    $allowedKinds = @('PROCESS_IDENTITY','PROCESS_RESULT','RENDERER_OBSERVATION','PROCESS_DISCOVERY','SENTINEL_CONTROL','TRACE_PROCESS_CORRELATION')
    foreach ($record in $Decoded) {
        if ($null -eq $record -or $record -isnot [System.Management.Automation.PSCustomObject] -or
            'kind' -cnotin @($record.PSObject.Properties.Name) -or
            $record.kind -isnot [string] -or $record.kind -cnotin $allowedKinds) {
            throw 'Supervisor returned an unknown record kind.'
        }
    }
    $matches = @($Decoded | Where-Object { $_.kind -ceq 'TRACE_PROCESS_CORRELATION' })
    if ($matches.Count -ne 1) { throw 'Supervisor did not produce exactly one trace-correlation record.' }
    $record = $matches[0]
    $recordNames = @($record.PSObject.Properties.Name)
    if ($recordNames.Count -ne 2 -or 'kind' -cnotin $recordNames -or 'traceEvidence' -cnotin $recordNames -or
        $null -eq $record.traceEvidence -or $record.traceEvidence -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'Invalid trace-correlation record shape.'
    }
    $trace = $record.traceEvidence
    $names = @($trace.PSObject.Properties.Name)
    $expected = @('Status','Reason','CandidatePid','CandidateCreationFileTimeUtc','Barrier','RendererIdentity','SandboxEnforcement')
    if ($names.Count -ne $expected.Count) { throw 'Invalid trace-correlation evidence shape.' }
    foreach ($name in $expected) { if ($name -cnotin $names) { throw 'Invalid trace-correlation evidence shape.' } }
    if ($trace.Status -isnot [string] -or $trace.Status -cnotin @('OBSERVED','INCONCLUSIVE') -or
        $trace.Reason -isnot [string] -or $trace.Reason -cnotmatch '^[A-Za-z0-9_]{1,100}$' -or
        $trace.RendererIdentity -cne 'INCONCLUSIVE' -or $trace.SandboxEnforcement -cne 'INCONCLUSIVE' -or
        $trace.Barrier -isnot [int] -or $trace.Barrier -lt 0 -or $trace.Barrier -gt 601) {
        throw 'Unexpected trace-correlation classification.'
    }
    if ($trace.Status -ceq 'OBSERVED') {
        if ($trace.Reason -cne 'LIVE_HANDLE_RETAINED_BEFORE_TRACE' -or
            ($trace.CandidatePid -isnot [int] -and $trace.CandidatePid -isnot [long]) -or
            $trace.CandidatePid -le 0 -or $trace.CandidatePid -gt [int]::MaxValue -or
            ($trace.CandidateCreationFileTimeUtc -isnot [int] -and $trace.CandidateCreationFileTimeUtc -isnot [long]) -or
            $trace.CandidateCreationFileTimeUtc -le 0 -or $trace.Barrier -le 0) {
            throw 'Invalid observed trace-correlation identity.'
        }
    } elseif ($null -ne $trace.CandidatePid -or $null -ne $trace.CandidateCreationFileTimeUtc -or $trace.Barrier -ne 0 -or
        $trace.Reason -ceq 'LIVE_HANDLE_RETAINED_BEFORE_TRACE') {
        throw 'Invalid inconclusive trace-correlation identity.'
    }
    return $trace
}
