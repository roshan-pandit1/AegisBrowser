$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'run-result-lib.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}
function Result([bool]$Timeout, [AllowNull()][object]$Code, [string[]]$Out, [string[]]$Err = @()) {
    Get-P309fRunResult -TimedOut $Timeout -ExitCode $Code -StandardOutput $Out -StandardError $Err
}

try {
    # Synthetic marker strings only. This is not an actual native CEF trace.
    $valid = @(
        'EVENT: CONTEXT_INITIALIZED',
        'EVENT: CEF_INITIALIZE_SUCCEEDED',
        'SANDBOX_CONFIGURATION: REQUESTED; ENFORCEMENT: INCONCLUSIVE',
        'EVENT: BROWSER_CREATED',
        'EVENT: MAIN_FRAME_LOAD_END',
        'EVENT: CLOSE_REQUESTED',
        'EVENT: DO_CLOSE',
        'EVENT: BEFORE_CLOSE',
        'EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE',
        'OWNER_RELEASE_CONTRACT: UNSATISFIED'
    )
    $r = Result $false 0 $valid
    Check ($r.Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'complete zero-exit synthetic transcript classified as producer claims'
    Check ($r.EvidenceSource -ceq 'UNAUTHENTICATED_HARNESS_TRANSCRIPT' -and
        $r.SandboxEnforcement -ceq 'INCONCLUSIVE' -and $r.SubprocessCleanup -ceq 'INCONCLUSIVE' -and
        $r.OwnerReleaseContract -ceq 'UNSATISFIED') 'matching markers never establish sandbox, cleanup or owner safety'
    $withoutDoClose = @($valid | Where-Object { $_ -cne 'EVENT: DO_CLOSE' })
    Check ((Result $false 0 $withoutDoClose).Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'DoClose is not required when skipped by native CEF'
    $contextLater = @($valid[1], $valid[0]) + $valid[2..9]
    Check ((Result $false 0 $contextLater).Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'initialization and context markers need no invented relative order'
    $preliminary = @($valid[0..3]) + @('EVENT: MAIN_FRAME_NAVIGATION_ABORTED', 'EVENT: OTHER_MAIN_FRAME_LOAD_END') + $valid[4..9]
    Check ((Result $false 0 $preliminary).Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'bounded preliminary navigation events can precede fixture load'
    $repeatedPreliminary = @($valid[0..3]) + (@('EVENT: MAIN_FRAME_NAVIGATION_ABORTED') * 8) + $valid[4..9]
    Check ((Result $false 0 $repeatedPreliminary).Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'up to eight preliminary events of one kind are bounded'
    $excessPreliminary = @($valid[0..3]) + (@('EVENT: MAIN_FRAME_NAVIGATION_ABORTED') * 9) + $valid[4..9]
    Check ((Result $false 0 $excessPreliminary).Reason -ceq 'OPTIONAL_EVENT_LIMIT_EXCEEDED') 'excessive preliminary events are rejected'
    $latePreliminary = @($valid[0..4]) + @('EVENT: OTHER_MAIN_FRAME_LOAD_END') + $valid[5..9]
    Check ((Result $false 0 $latePreliminary).Reason -ceq 'OPTIONAL_EVENT_ORDER_CONTRADICTORY') 'preliminary event after fixture load is rejected'
    Check ((Result $false 0 @($preliminary | Where-Object { $_ -cne 'EVENT: MAIN_FRAME_LOAD_END' })).Reason -ceq 'MISSING_OR_DUPLICATE_MARKER') 'preliminary events cannot replace fixture completion'
    Check ((Result $true 0 $valid).Reason -ceq 'TIMEOUT') 'timeout cannot become success even with zero exit and complete markers'
    Check ((Result $false $null $valid).Reason -ceq 'EXIT_CODE_UNAVAILABLE') 'null process exit does not become zero'
    Check ((Result $false '0' $valid).Reason -ceq 'EXIT_CODE_UNAVAILABLE') 'string exit code is not trusted as an integer'
    Check ((Result $false 15 $valid).Reason -ceq 'PROCESS_EXIT_NONZERO') 'nonzero native process exit fails'
    Check ((Result $false 0 @($valid | Where-Object { $_ -cne 'EVENT: BEFORE_CLOSE' })).Reason -ceq 'MISSING_OR_DUPLICATE_MARKER') 'missing final browser close remains incomplete'
    Check ((Result $false 0 ($valid + @('EVENT: BROWSER_CREATED'))).Reason -ceq 'MISSING_OR_DUPLICATE_MARKER') 'duplicate creation remains incomplete'
    $wrongOrder = @($valid[0..3]) + @($valid[5], $valid[4]) + $valid[6..9]
    Check ((Result $false 0 $wrongOrder).Reason -ceq 'EVENT_ORDER_CONTRADICTORY') 'contradictory close ordering is rejected'
    $lateConfig = @($valid[0..1]) + @($valid[3..8]) + @($valid[2], $valid[9])
    Check ((Result $false 0 $lateConfig).Reason -ceq 'EVENT_ORDER_CONTRADICTORY') 'configuration reported after shutdown is rejected'
    $earlyConfig = @($valid[0], $valid[2], $valid[1]) + $valid[3..9]
    Check ((Result $false 0 $earlyConfig).Reason -ceq 'EVENT_ORDER_CONTRADICTORY') 'configuration reported before initialization return is rejected'
    Check ((Result $false 0 $valid @('FIXTURE_RESULT: FAILED')).Reason -ceq 'CONTRADICTORY_FAILURE_MARKER') 'fixture failure contradicts zero exit'
    Check ((Result $false 0 $valid @('CEF_SHUTDOWN: INCOMPLETE')).Reason -ceq 'CONTRADICTORY_FAILURE_MARKER') 'incomplete shutdown marker prevents success'
    Check ((Result $false 0 ($valid + @('EVENT: MAIN_FRAME_LOAD_ERROR code=-3'))).Reason -ceq 'CONTRADICTORY_FAILURE_MARKER') 'load error prevents success'
    Check ((Result $false 0 ($valid + @('EVENT: DO_CLOSE'))).Reason -ceq 'DO_CLOSE_ORDER_CONTRADICTORY') 'duplicate DoClose is rejected'
    $earlyDoClose = @($valid[0..4]) + @($valid[6], $valid[5]) + $valid[7..9]
    Check ((Result $false 0 $earlyDoClose).Reason -ceq 'DO_CLOSE_ORDER_CONTRADICTORY') 'DoClose before close request is rejected'
    Check ((Result $false 0 ($valid + @('SANDBOX_ENFORCEMENT: VERIFIED'))).Reason -ceq 'UNKNOWN_CRITICAL_MARKER') 'unsupported sandbox claim is rejected'
    Check ((Result $false 0 ($valid + @('EVENT: UNRECOGNIZED'))).Reason -ceq 'UNKNOWN_CRITICAL_MARKER') 'unknown native event is not silently accepted'
    Check ((Result $false 0 $valid @('EVENT: BROWSER_CREATED')).Reason -ceq 'UNEXPECTED_STDERR_MARKER') 'security event on unexpected stream is rejected'
    Check ((Result $false 0 ($valid + @(('x' * 8193)))).Reason -ceq 'TRACE_INVALID_OR_OVERSIZED') 'oversized trace line is rejected'
    Check ((Result $false 0 (@('x') * 10001)).Reason -ceq 'TRACE_INVALID_OR_OVERSIZED') 'oversized trace count is rejected'
    Check ((Result $false 0 $valid @('unrelated bounded diagnostic')).Disposition -ceq 'OBSERVED_CLIENT_SEQUENCE') 'noncritical bounded diagnostics do not imply failure'
    Write-Output "PASS: $checks synthetic native result checks; no CEF execution or sandbox verification"
    exit 0
}
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
