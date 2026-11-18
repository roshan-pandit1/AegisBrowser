param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'result-lib.ps1')
$checks = 0
$hash = 'A' * 64

function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}
function New-Identity {
    [pscustomobject]@{ Pid=123; CreationFileTimeUtc=[long]13370001; ImagePath='C:\isolated\bootstrapc.exe'; ImageSha256=('A' * 64) }
}
function New-Result {
    $lines = @(
        'EVENT: CONTEXT_INITIALIZED',
        'EVENT: CEF_INITIALIZE_SUCCEEDED',
        'SANDBOX_CONFIGURATION: REQUESTED; ENFORCEMENT: INCONCLUSIVE',
        'EVENT: BROWSER_CREATED',
        'EVENT: MAIN_FRAME_LOAD_STARTED',
        'EVENT: MAIN_FRAME_LOAD_END',
        'EVENT: CLOSE_REQUESTED',
        'EVENT: DO_CLOSE',
        'EVENT: BEFORE_CLOSE',
        'EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE',
        'OWNER_RELEASE_CONTRACT: UNSATISFIED'
    )
    [pscustomobject]@{
        ProcessExit='NORMAL'; NativeExitCode=[uint32]0; ProcessIdentity=(New-Identity);
        ParentTerminationRequested=$false; ParentCleanup='NOT_NEEDED'; OutputComplete=$true;
        Stdout=[pscustomobject]@{ Text=($lines -join "`n"); Truncated=$false; ValidUtf8=$true };
        Stderr=[pscustomobject]@{ Text=''; Truncated=$false; ValidUtf8=$true };
        SubprocessCleanup='INCONCLUSIVE'; SandboxEnforcement='INCONCLUSIVE'; Error=''
    }
}
function Evaluate([object]$Result, [object]$Identity, [object]$ExpectedHash = $hash) {
    Get-P309gSupervisedResult -SupervisorResult $Result -EmittedIdentity $Identity -ExpectedImageSha256 $ExpectedHash
}

try {
    # Synthetic objects and fixed markers exercise only the classifier. They
    # are never presented as real supervisor, CEF, token or sandbox evidence.
    $valid = Evaluate (New-Result) (New-Identity)
    Check ($valid.Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT' -and $valid.IdentityMatched) 'matching supervisor records and fixture markers correlate'
    Check ($valid.EvidenceSource -ceq 'SUPERVISOR_RECORD_AND_UNAUTHENTICATED_CLIENT_TRANSCRIPT' -and
        $valid.SandboxEnforcement -ceq 'INCONCLUSIVE' -and $valid.RendererTokenEvidence -ceq 'INCONCLUSIVE' -and
        $valid.SubprocessCleanup -ceq 'INCONCLUSIVE' -and $valid.OwnerReleaseContract -ceq 'UNSATISFIED') 'matching records do not establish sandbox or subprocess cleanup'
    # The real supervisor communicates JSON. PowerShell 5.1 and 7 can parse
    # JSON integer values into different integer types; preserve that boundary.
    $wireResult = (New-Result | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json
    $wireIdentity = (New-Identity | ConvertTo-Json -Depth 4 -Compress) | ConvertFrom-Json
    Check ((Evaluate $wireResult $wireIdentity).Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT') 'JSON supervisor records correlate without numeric coercion'
    $wireResult.ProcessIdentity.CreationFileTimeUtc = [DateTime]::UtcNow.ToFileTimeUtc()
    $wireIdentity.CreationFileTimeUtc = $wireResult.ProcessIdentity.CreationFileTimeUtc
    $wireResult = ($wireResult | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json
    $wireIdentity = ($wireIdentity | ConvertTo-Json -Depth 4 -Compress) | ConvertFrom-Json
    Check ((Evaluate $wireResult $wireIdentity).Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT') 'JSON preserves a current Windows creation FILETIME'
    $wireIdentity.CreationFileTimeUtc++
    Check ((Evaluate $wireResult $wireIdentity).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'adjacent FILETIME values are not rounded together'
    $r=New-Result; $r.NativeExitCode=$null
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'NATIVE_EXIT_UNAVAILABLE') 'missing native exit is not zero'
    $r=New-Result; $r.NativeExitCode='0'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'NATIVE_EXIT_UNAVAILABLE') 'string zero is not an integer exit'
    $r=New-Result; $r.NativeExitCode=0.0
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'NATIVE_EXIT_UNAVAILABLE') 'floating-point zero is not an integer exit'
    $r=New-Result; $r.NativeExitCode=[uint32]7; $r.ProcessExit='NONZERO'
    Check ((Evaluate $r (New-Identity)).Disposition -ceq 'FAILED') 'native nonzero exit fails'
    $r=New-Result; $r.ProcessExit='TIMEOUT'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'PROCESS_TIMEOUT') 'timeout is incomplete even with zero in the record'
    $r=New-Result; $r.ProcessExit='UNAVAILABLE'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'PROCESS_EXIT_UNCERTAIN') 'unavailable process exit is incomplete'
    $r=New-Result; $r.ParentTerminationRequested=$true
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'TERMINATION_OR_CLEANUP_UNCERTAIN') 'termination request prevents success'
    $r=New-Result; $r.ParentCleanup='PARENT_TERMINATED'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'TERMINATION_OR_CLEANUP_UNCERTAIN') 'cleanup termination cannot be normal completion'
    $r=New-Result; $r.Error='IDENTITY_DELIVERY_FAILED'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'PROCESS_EXIT_UNCERTAIN') 'supervisor identity delivery failure is incomplete'
    Check ((Evaluate (New-Result) (New-Identity) 'bad').Reason -ceq 'EXPECTED_HASH_INVALID') 'malformed expected hash is rejected'
    Check ((Evaluate (New-Result) (New-Identity) ('B'*64)).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'untrusted image hash is rejected'
    $id=New-Identity; $id.Pid=124
    Check ((Evaluate (New-Result) $id).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'different process ID is rejected'
    $id=New-Identity; $id.CreationFileTimeUtc=[long]13370002
    Check ((Evaluate (New-Result) $id).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'stale or reused process ID is rejected'
    $id=New-Identity; $id.ImagePath='C:\other\bootstrapc.exe'
    Check ((Evaluate (New-Result) $id).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'different executable path is rejected'
    $id=New-Identity; $id.ImagePath='relative.exe'
    Check ((Evaluate (New-Result) $id).Reason -ceq 'PROCESS_IDENTITY_INVALID') 'relative executable path is rejected'
    $id=New-Identity; $id.ImageSha256=('B'*64)
    Check ((Evaluate (New-Result) $id).Reason -ceq 'PROCESS_IDENTITY_MISMATCH') 'identity hash substitution is rejected'
    Check ((Evaluate (New-Result) $null).Reason -ceq 'PROCESS_IDENTITY_INVALID') 'missing emitted identity is incomplete'
    $r=New-Result; $r.ProcessIdentity=$null
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'PROCESS_IDENTITY_INVALID') 'missing result identity is incomplete'
    $r=New-Result; $r.OutputComplete=$false
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INCOMPLETE') 'incomplete output is rejected'
    $r=New-Result; $r.Stdout.Truncated=$true
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INVALID_OR_TRUNCATED') 'truncated stdout is rejected'
    $r=New-Result; $r.Stderr.Truncated=$true
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INVALID_OR_TRUNCATED') 'truncated stderr is rejected'
    $r=New-Result; $r.Stdout.ValidUtf8=$false
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INVALID_OR_TRUNCATED') 'invalid UTF-8 flag is rejected even if completeness is claimed'
    $r=New-Result; $r.Stderr.PSObject.Properties.Remove('ValidUtf8')
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INCOMPLETE') 'missing UTF-8 validation flag is rejected'
    $r=New-Result; $r.Stdout.Text += "`0"
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INVALID_OR_TRUNCATED') 'malformed control byte is rejected'
    $r=New-Result; $r.Stdout.Text += ('x'*65537)
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OUTPUT_INVALID_OR_TRUNCATED') 'oversized transcript is rejected'
    $r=New-Result; $r.Stdout.Text=$r.Stdout.Text.Replace('EVENT: MAIN_FRAME_LOAD_STARTED', 'EVENT: OTHER_MAIN_FRAME_LOAD_START')
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'FIXTURE_LOAD_START_MISSING_OR_CONTRADICTORY') 'missing fixture load-start is incomplete'
    $r=New-Result; $r.Stdout.Text=$r.Stdout.Text.Replace('EVENT: MAIN_FRAME_LOAD_STARTED', "EVENT: MAIN_FRAME_LOAD_STARTED`nEVENT: MAIN_FRAME_LOAD_STARTED")
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'FIXTURE_LOAD_START_MISSING_OR_CONTRADICTORY') 'duplicate fixture load-start is rejected'
    $r=New-Result; $r.Stdout.Text=$r.Stdout.Text.Replace("EVENT: MAIN_FRAME_LOAD_STARTED`nEVENT: MAIN_FRAME_LOAD_END", "EVENT: MAIN_FRAME_LOAD_END`nEVENT: MAIN_FRAME_LOAD_STARTED")
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'FIXTURE_LOAD_START_MISSING_OR_CONTRADICTORY') 'fixture load events in wrong order are rejected'
    $r=New-Result; $r.Stdout.Text=$r.Stdout.Text.Replace('EVENT: MAIN_FRAME_LOAD_STARTED', "EVENT: OTHER_MAIN_FRAME_LOAD_START`nEVENT: MAIN_FRAME_LOAD_STARTED")
    Check ((Evaluate $r (New-Identity)).Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT') 'bounded preliminary load-start does not replace fixture evidence'
    $r=New-Result; $r.Stdout.Text += "`nEVENT: OTHER_MAIN_FRAME_LOAD_START"
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'OTHER_LOAD_START_INVALID') 'late preliminary load-start is rejected'
    $r=New-Result; $r.Stdout.Text=$r.Stdout.Text.Replace('EVENT: BEFORE_CLOSE','EVENT: MISSING_CLOSE')
    Check ((Evaluate $r (New-Identity)).Disposition -ceq 'INCOMPLETE') 'missing browser close is incomplete'
    $r=New-Result; $r.Stderr.Text='BLOCKED: MISSING_SANDBOX_INFO'
    Check ((Evaluate $r (New-Identity)).Disposition -ceq 'INCOMPLETE') 'sandbox gate failure is incomplete'
    $r=New-Result; $r.SandboxEnforcement='VERIFIED'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'UNSUPPORTED_SECURITY_CLAIM') 'unsupported sandbox claim is rejected'
    $r=New-Result; $r.SubprocessCleanup='VERIFIED'
    Check ((Evaluate $r (New-Identity)).Reason -ceq 'UNSUPPORTED_SECURITY_CLAIM') 'unsupported subprocess-cleanup claim is rejected'
    Check ((Evaluate ([pscustomobject]@{}) (New-Identity)).Reason -ceq 'SUPERVISOR_RESULT_MALFORMED') 'malformed supervisor object is rejected'

    # Exercise the actual supervisor JSON wire format and retained-process
    # identity using its synthetic child mode. This executes no CEF binary.
    $supervisorProject = Join-Path $PSScriptRoot 'ProcessSupervisor/ProcessSupervisor.csproj'
    $supervisorExe = Join-Path $PSScriptRoot 'ProcessSupervisor/bin/Release/net8.0/ProcessSupervisor.exe'
    & $Dotnet build $supervisorProject -c Release --ignore-failed-sources
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $supervisorExe -PathType Leaf)) { throw 'SUPERVISOR_WIRE_TEST_BUILD_FAILED' }
    $wireLines = @(& $supervisorExe --fixture-wire-selftest)
    $wireExit = $LASTEXITCODE
    Check ($wireExit -eq 0) 'real supervisor synthetic wire process exits zero'
    $messages = @($wireLines | ForEach-Object { $_ | ConvertFrom-Json -ErrorAction Stop })
    $identities = @($messages | Where-Object { $_.kind -ceq 'PROCESS_IDENTITY' })
    $results = @($messages | Where-Object { $_.kind -ceq 'PROCESS_RESULT' })
    Check ($messages.Count -eq 2 -and $identities.Count -eq 1 -and $results.Count -eq 1) 'real supervisor emitted one identity and one result record'
    $wireHash = (Get-FileHash -LiteralPath $supervisorExe -Algorithm SHA256).Hash
    $wireOutcome = Evaluate $results[0].result $identities[0].identity $wireHash
    Check ($wireOutcome.Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT' -and $wireOutcome.IdentityMatched) 'real supervisor synthetic child JSON is correlated'
    Check ($results[0].DependencyCoverage -ceq 'SYNTHETIC_FIXTURE_ONLY' -and $results[0].OverallQualification -ceq 'INCONCLUSIVE' -and
        $wireOutcome.SandboxEnforcement -ceq 'INCONCLUSIVE') 'synthetic wire integration cannot establish sandbox enforcement'
    Write-Output "PASS: $checks synthetic supervised-result checks; no CEF execution or sandbox verification"
    exit 0
}
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
