Set-StrictMode -Version Latest

function Test-P0IntegralRange {
    param($Value, [long] $Minimum, [long] $Maximum)
    return (($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum -and $Value -le $Maximum)
}

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
    $expected = @('Status','Reason','CandidatePid','CandidateCreationFileTimeUtc','Barrier','RendererIdentity','SandboxEnforcement','Token','TokenAfter')
    if ($names.Count -ne $expected.Count) { throw 'Invalid trace-correlation evidence shape.' }
    foreach ($name in $expected) { if ($name -cnotin $names) { throw 'Invalid trace-correlation evidence shape.' } }
    if ($trace.Status -isnot [string] -or $trace.Status -cnotin @('OBSERVED','INCONCLUSIVE') -or
        $trace.Reason -isnot [string] -or $trace.Reason -cnotmatch '^[A-Za-z0-9_]{1,100}$' -or
        $trace.RendererIdentity -isnot [string] -or $trace.RendererIdentity -cne 'INCONCLUSIVE' -or
        $trace.SandboxEnforcement -isnot [string] -or $trace.SandboxEnforcement -cne 'INCONCLUSIVE' -or
        ($trace.Barrier -isnot [int] -and $trace.Barrier -isnot [long]) -or $trace.Barrier -lt 0 -or $trace.Barrier -gt 601) {
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
        foreach ($token in @($trace.Token, $trace.TokenAfter)) {
        if ($null -eq $token -or $token -isnot [System.Management.Automation.PSCustomObject]) {
            throw 'Missing trace-correlated token evidence.'
        }
        $tokenNames = @($token.PSObject.Properties.Name)
        $tokenExpected = @('Status','Reason','ProcessId','CreationFileTimeUtc','IntegrityRid','IsAppContainer',
            'DenyOnlyGroupCount','RestrictingSidCount','HasRestrictingSids','PrivilegeCount','EnabledPrivilegeCount',
            'InAnyJob','Win32kSystemCallsDisabled','SystemCallMitigationFlags','ProcessRole','SandboxEnforcement',
            'QpcStart','QpcEnd','QpcFrequency')
        if ($tokenNames.Count -ne $tokenExpected.Count) { throw 'Invalid trace-token evidence shape.' }
        foreach ($name in $tokenExpected) { if ($name -cnotin $tokenNames) { throw 'Invalid trace-token evidence shape.' } }
        if ($token.Status -isnot [string] -or $token.Status -cnotin @('OBSERVED','INCONCLUSIVE') -or
            $token.Reason -isnot [string] -or $token.Reason -cnotmatch '^[A-Za-z0-9_]{1,100}$' -or
            $token.ProcessRole -isnot [string] -or $token.ProcessRole -cne 'NOT_ESTABLISHED' -or
            $token.SandboxEnforcement -isnot [string] -or $token.SandboxEnforcement -cne 'INCONCLUSIVE') {
            throw 'Unexpected trace-token classification.'
        }
        $dataNames = @('ProcessId','CreationFileTimeUtc','IntegrityRid','IsAppContainer','DenyOnlyGroupCount',
            'RestrictingSidCount','HasRestrictingSids','PrivilegeCount','EnabledPrivilegeCount','InAnyJob',
            'Win32kSystemCallsDisabled','SystemCallMitigationFlags','QpcStart','QpcEnd','QpcFrequency')
        if ($token.Status -ceq 'OBSERVED') {
            if ($token.Reason -cne 'HANDLE_IDENTITY_AND_TOKEN_SNAPSHOT' -or
                -not (Test-P0IntegralRange $token.ProcessId 1 ([int]::MaxValue)) -or $token.ProcessId -ne $trace.CandidatePid -or
                -not (Test-P0IntegralRange $token.CreationFileTimeUtc 1 ([long]::MaxValue)) -or
                $token.CreationFileTimeUtc -ne $trace.CandidateCreationFileTimeUtc) {
                throw 'Trace-token process identity does not match retained candidate.'
            }
            if (-not (Test-P0IntegralRange $token.QpcStart 1 ([long]::MaxValue)) -or
                -not (Test-P0IntegralRange $token.QpcEnd 1 ([long]::MaxValue)) -or
                -not (Test-P0IntegralRange $token.QpcFrequency 1 ([long]::MaxValue)) -or
                $token.QpcEnd -lt $token.QpcStart) {
                throw 'Invalid monotonic trace-token observation interval.'
            }
            foreach ($name in @('IntegrityRid','DenyOnlyGroupCount','RestrictingSidCount','PrivilegeCount','EnabledPrivilegeCount')) {
                if (-not (Test-P0IntegralRange $token.$name 0 ([uint32]::MaxValue))) { throw 'Invalid trace-token count.' }
            }
            if ($token.IsAppContainer -isnot [bool]) { throw 'Invalid trace-token Boolean.' }
            foreach ($name in @('HasRestrictingSids','InAnyJob','Win32kSystemCallsDisabled')) {
                if ($null -ne $token.$name -and $token.$name -isnot [bool]) { throw 'Invalid optional trace-token Boolean.' }
            }
            if ($null -ne $token.SystemCallMitigationFlags -and
                -not (Test-P0IntegralRange $token.SystemCallMitigationFlags 0 ([uint32]::MaxValue))) {
                throw 'Invalid trace-token mitigation flags.'
            }
        } else {
            if ($token.Reason -ceq 'HANDLE_IDENTITY_AND_TOKEN_SNAPSHOT') { throw 'Inconclusive token has observed reason.' }
            foreach ($name in $dataNames) { if ($null -ne $token.$name) { throw 'Inconclusive token contains observation data.' } }
        }
        }
        if ($trace.Token.Status -ceq 'OBSERVED' -and $trace.TokenAfter.Status -ceq 'OBSERVED' -and
            ($trace.Token.QpcFrequency -ne $trace.TokenAfter.QpcFrequency -or
             $trace.TokenAfter.QpcStart -lt $trace.Token.QpcEnd)) {
            throw 'Trace-token observations are not in monotonic order.'
        }
    } elseif ($null -ne $trace.CandidatePid -or $null -ne $trace.CandidateCreationFileTimeUtc -or $trace.Barrier -ne 0 -or
        $trace.Reason -ceq 'LIVE_HANDLE_RETAINED_BEFORE_TRACE' -or $null -ne $trace.Token -or $null -ne $trace.TokenAfter) {
        throw 'Invalid inconclusive trace-correlation identity.'
    }
    return $trace
}
