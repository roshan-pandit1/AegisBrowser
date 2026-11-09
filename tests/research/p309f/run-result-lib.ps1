Set-StrictMode -Version Latest

# Classifies only an already-collected harness transcript. This function does
# not start or stop a process, authenticate a transcript, inspect renderer
# tokens, or establish subprocess cleanup. Its matching markers are producer
# claims, not independent proof of Chromium sandbox enforcement.
function Get-P309fRunResult {
    param(
        [Parameter(Mandatory)][bool]$TimedOut,
        [AllowNull()][object]$ExitCode,
        [AllowEmptyCollection()][string[]]$StandardOutput = @(),
        [AllowEmptyCollection()][string[]]$StandardError = @()
    )

    function New-P309fResult([string]$Disposition, [string]$Reason) {
        return [pscustomobject]@{
            Disposition = $Disposition
            Reason = $Reason
            EvidenceSource = 'UNAUTHENTICATED_HARNESS_TRANSCRIPT'
            SandboxEnforcement = 'INCONCLUSIVE'
            SubprocessCleanup = 'INCONCLUSIVE'
            OwnerReleaseContract = 'UNSATISFIED'
        }
    }

    if ($TimedOut) { return New-P309fResult 'INCOMPLETE' 'TIMEOUT' }
    # In particular, PowerShell's null ExitCode must never be converted to 0.
    if ($null -eq $ExitCode -or $ExitCode -isnot [int]) {
        return New-P309fResult 'INCOMPLETE' 'EXIT_CODE_UNAVAILABLE'
    }
    if ($ExitCode -ne 0) { return New-P309fResult 'FAILED' 'PROCESS_EXIT_NONZERO' }
    if ($null -eq $StandardOutput -or $null -eq $StandardError -or
        $StandardOutput.Count -gt 10000 -or $StandardError.Count -gt 10000) {
        return New-P309fResult 'INCOMPLETE' 'TRACE_INVALID_OR_OVERSIZED'
    }
    foreach ($line in @($StandardOutput) + @($StandardError)) {
        if ($null -eq $line -or $line.Length -gt 8192) {
            return New-P309fResult 'INCOMPLETE' 'TRACE_INVALID_OR_OVERSIZED'
        }
        if ($line -match '^(?:BLOCKED:|CEF_STARTUP: FAIL|CEF_SHUTDOWN: INCOMPLETE|FIXTURE_RESULT: FAILED|EVENT: (?:CREATE_BROWSER_FAILED|MAIN_FRAME_LOAD_ERROR|CLOSE_REQUESTED_AFTER_LOAD_ERROR))') {
            return New-P309fResult 'INCOMPLETE' 'CONTRADICTORY_FAILURE_MARKER'
        }
    }

    $required = @(
        'EVENT: CONTEXT_INITIALIZED',
        'EVENT: CEF_INITIALIZE_SUCCEEDED',
        'SANDBOX_CONFIGURATION: REQUESTED; ENFORCEMENT: INCONCLUSIVE',
        'EVENT: BROWSER_CREATED',
        'EVENT: MAIN_FRAME_LOAD_END',
        'EVENT: CLOSE_REQUESTED',
        'EVENT: BEFORE_CLOSE',
        'EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE',
        'OWNER_RELEASE_CONTRACT: UNSATISFIED'
    )
    # The client can report an aborted preliminary navigation or another
    # main-frame load before the fixed fixture completes. These events alone
    # never replace the required fixture-load marker.
    $optional = @('EVENT: MAIN_FRAME_NAVIGATION_ABORTED', 'EVENT: OTHER_MAIN_FRAME_LOAD_END')
    foreach ($line in $StandardOutput) {
        if ($line -match '^(?:EVENT:|SANDBOX_CONFIGURATION:|SANDBOX_ENFORCEMENT:|OWNER_RELEASE_CONTRACT:)' -and
            $line -cne 'EVENT: DO_CLOSE' -and $required -cnotcontains $line -and $optional -cnotcontains $line) {
            return New-P309fResult 'INCOMPLETE' 'UNKNOWN_CRITICAL_MARKER'
        }
    }
    foreach ($line in $StandardError) {
        if ($line -match '^(?:EVENT:|SANDBOX_CONFIGURATION:|SANDBOX_ENFORCEMENT:|OWNER_RELEASE_CONTRACT:)') {
            return New-P309fResult 'INCOMPLETE' 'UNEXPECTED_STDERR_MARKER'
        }
    }
    $positions = @{}
    foreach ($marker in $required) {
        $indices = @(for ($i = 0; $i -lt $StandardOutput.Count; $i++) {
            if ($StandardOutput[$i] -ceq $marker) { $i }
        })
        if ($indices.Count -ne 1) { return New-P309fResult 'INCOMPLETE' 'MISSING_OR_DUPLICATE_MARKER' }
        $positions[$marker] = $indices[0]
    }
    foreach ($marker in $optional) {
        $indices = @(for ($i = 0; $i -lt $StandardOutput.Count; $i++) {
            if ($StandardOutput[$i] -ceq $marker) { $i }
        })
        if ($indices.Count -gt 8) { return New-P309fResult 'INCOMPLETE' 'OPTIONAL_EVENT_LIMIT_EXCEEDED' }
        foreach ($index in $indices) {
            if ($index -ge $positions['EVENT: MAIN_FRAME_LOAD_END']) {
                return New-P309fResult 'INCOMPLETE' 'OPTIONAL_EVENT_ORDER_CONTRADICTORY'
            }
        }
    }
    # Context is needed before creation; these five events are emitted in
    # order by the client. CefInitialize can complete before or after its
    # context callback, so no unsupported ordering is imposed on that marker.
    $ordered = @('EVENT: CONTEXT_INITIALIZED', 'EVENT: BROWSER_CREATED',
        'EVENT: MAIN_FRAME_LOAD_END', 'EVENT: CLOSE_REQUESTED',
        'EVENT: BEFORE_CLOSE', 'EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE',
        'OWNER_RELEASE_CONTRACT: UNSATISFIED')
    for ($i = 1; $i -lt $ordered.Count; $i++) {
        if ($positions[$ordered[$i - 1]] -ge $positions[$ordered[$i]]) {
            return New-P309fResult 'INCOMPLETE' 'EVENT_ORDER_CONTRADICTORY'
        }
    }
    $doClose = @(for ($i = 0; $i -lt $StandardOutput.Count; $i++) {
        if ($StandardOutput[$i] -ceq 'EVENT: DO_CLOSE') { $i }
    })
    if ($doClose.Count -gt 1 -or ($doClose.Count -eq 1 -and
        ($doClose[0] -le $positions['EVENT: CLOSE_REQUESTED'] -or $doClose[0] -ge $positions['EVENT: BEFORE_CLOSE']))) {
        return New-P309fResult 'INCOMPLETE' 'DO_CLOSE_ORDER_CONTRADICTORY'
    }
    return New-P309fResult 'OBSERVED_CLIENT_SEQUENCE' 'ZERO_EXIT_AND_EXPECTED_MARKERS'
}
