Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../p309f/run-result-lib.ps1')

# This consumes the trusted, in-memory result and identity records emitted by
# one supervisor invocation. It does not authenticate JSON or arbitrary log
# files; the caller must establish that both records came from that invocation.
# The supervisor's pathname/file hash is not proof of a mapped-image hash in
# the face of hostile concurrent filesystem replacement or an administrator.
function Get-P309gSupervisedResult {
    param(
        [AllowNull()][object]$SupervisorResult,
        [AllowNull()][object]$EmittedIdentity,
        [AllowNull()][object]$ExpectedImageSha256
    )

    function New-P309gDisposition([string]$Disposition, [string]$Reason, [bool]$IdentityMatched) {
        [pscustomobject]@{
            Disposition = $Disposition
            Reason = $Reason
            IdentityMatched = $IdentityMatched
            EvidenceSource = 'SUPERVISOR_RECORD_AND_UNAUTHENTICATED_CLIENT_TRANSCRIPT'
            RendererTokenEvidence = 'INCONCLUSIVE'
            SandboxEnforcement = 'INCONCLUSIVE'
            SubprocessCleanup = 'INCONCLUSIVE'
            OwnerReleaseContract = 'UNSATISFIED'
        }
    }
    function Has-Fields([object]$Object, [string[]]$Names) {
        if ($null -eq $Object) { return $false }
        foreach ($name in $Names) {
            if (@($Object.PSObject.Properties | Where-Object { $_.Name -ceq $name }).Count -ne 1) { return $false }
        }
        return $true
    }
    function Is-Integer([object]$Value) {
        return $null -ne $Value -and ($Value -is [int] -or $Value -is [long] -or
            $Value -is [uint32] -or $Value -is [uint64] -or $Value -is [int16] -or $Value -is [uint16] -or $Value -is [byte])
    }
    function Valid-Identity([object]$Identity) {
        if (-not (Has-Fields $Identity @('Pid','CreationFileTimeUtc','ImagePath','ImageSha256'))) { return $false }
        if (-not (Is-Integer $Identity.Pid) -or -not (Is-Integer $Identity.CreationFileTimeUtc)) { return $false }
        if ([decimal]$Identity.Pid -le 0 -or [decimal]$Identity.Pid -gt [int]::MaxValue -or
            [decimal]$Identity.CreationFileTimeUtc -le 0 -or [decimal]$Identity.CreationFileTimeUtc -gt [long]::MaxValue) { return $false }
        if ($Identity.ImagePath -isnot [string] -or $Identity.ImageSha256 -isnot [string] -or
            $Identity.ImageSha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or
            $Identity.ImagePath -notmatch '^[A-Za-z]:[\\/]') { return $false }
        try { $null = [IO.Path]::GetFullPath($Identity.ImagePath) } catch { return $false }
        return $true
    }

    if ($ExpectedImageSha256 -isnot [string] -or $ExpectedImageSha256 -cnotmatch '^[0-9A-Fa-f]{64}$') {
        return New-P309gDisposition 'INCOMPLETE' 'EXPECTED_HASH_INVALID' $false
    }
    if (-not (Has-Fields $SupervisorResult @('ProcessExit','NativeExitCode','ProcessIdentity',
        'ParentTerminationRequested','ParentCleanup','OutputComplete','Stdout','Stderr',
        'SubprocessCleanup','SandboxEnforcement','Error'))) {
        return New-P309gDisposition 'INCOMPLETE' 'SUPERVISOR_RESULT_MALFORMED' $false
    }
    if ($SupervisorResult.ProcessExit -isnot [string]) { return New-P309gDisposition 'INCOMPLETE' 'PROCESS_EXIT_INVALID' $false }
    if ($SupervisorResult.ProcessExit -ceq 'TIMEOUT') { return New-P309gDisposition 'INCOMPLETE' 'PROCESS_TIMEOUT' $false }
    if ($SupervisorResult.ParentTerminationRequested -isnot [bool] -or $SupervisorResult.ParentTerminationRequested -or
        $SupervisorResult.ParentCleanup -isnot [string] -or $SupervisorResult.ParentCleanup -cne 'NOT_NEEDED') {
        return New-P309gDisposition 'INCOMPLETE' 'TERMINATION_OR_CLEANUP_UNCERTAIN' $false
    }
    if (-not (Is-Integer $SupervisorResult.NativeExitCode)) { return New-P309gDisposition 'INCOMPLETE' 'NATIVE_EXIT_UNAVAILABLE' $false }
    if ([decimal]$SupervisorResult.NativeExitCode -ne 0 -or $SupervisorResult.ProcessExit -ceq 'NONZERO') {
        return New-P309gDisposition 'FAILED' 'NATIVE_EXIT_NONZERO' $false
    }
    if ($SupervisorResult.ProcessExit -cne 'NORMAL' -or $SupervisorResult.Error -isnot [string] -or $SupervisorResult.Error -cne '') {
        return New-P309gDisposition 'INCOMPLETE' 'PROCESS_EXIT_UNCERTAIN' $false
    }
    if ($SupervisorResult.SubprocessCleanup -cne 'INCONCLUSIVE' -or $SupervisorResult.SandboxEnforcement -cne 'INCONCLUSIVE') {
        return New-P309gDisposition 'INCOMPLETE' 'UNSUPPORTED_SECURITY_CLAIM' $false
    }
    if (-not (Valid-Identity $SupervisorResult.ProcessIdentity) -or -not (Valid-Identity $EmittedIdentity)) {
        return New-P309gDisposition 'INCOMPLETE' 'PROCESS_IDENTITY_INVALID' $false
    }
    $a = $SupervisorResult.ProcessIdentity
    $b = $EmittedIdentity
    if ([long]$a.Pid -ne [long]$b.Pid -or [long]$a.CreationFileTimeUtc -ne [long]$b.CreationFileTimeUtc -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($a.ImagePath), [IO.Path]::GetFullPath($b.ImagePath)) -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals($a.ImageSha256, $b.ImageSha256) -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals($a.ImageSha256, $ExpectedImageSha256)) {
        return New-P309gDisposition 'INCOMPLETE' 'PROCESS_IDENTITY_MISMATCH' $false
    }
    if ($SupervisorResult.OutputComplete -isnot [bool] -or -not $SupervisorResult.OutputComplete -or
        -not (Has-Fields $SupervisorResult.Stdout @('Text','Truncated','ValidUtf8')) -or
        -not (Has-Fields $SupervisorResult.Stderr @('Text','Truncated','ValidUtf8'))) {
        return New-P309gDisposition 'INCOMPLETE' 'OUTPUT_INCOMPLETE' $true
    }
    foreach ($capture in @($SupervisorResult.Stdout,$SupervisorResult.Stderr)) {
        if ($capture.Truncated -isnot [bool] -or $capture.Truncated -or
            $capture.ValidUtf8 -isnot [bool] -or -not $capture.ValidUtf8 -or $capture.Text -isnot [string] -or
            $capture.Text.Length -gt 65536 -or $capture.Text -match '[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFD]') {
            return New-P309gDisposition 'INCOMPLETE' 'OUTPUT_INVALID_OR_TRUNCATED' $true
        }
    }
    $stdout = @($SupervisorResult.Stdout.Text -split "`r?`n")
    $stderr = @($SupervisorResult.Stderr.Text -split "`r?`n")
    if ($stdout.Count -gt 10000 -or $stderr.Count -gt 10000) { return New-P309gDisposition 'INCOMPLETE' 'OUTPUT_INVALID_OR_TRUNCATED' $true }
    $starts = @(for ($i = 0; $i -lt $stdout.Count; $i++) { if ($stdout[$i] -ceq 'EVENT: MAIN_FRAME_LOAD_STARTED') { $i } })
    $created = @(for ($i = 0; $i -lt $stdout.Count; $i++) { if ($stdout[$i] -ceq 'EVENT: BROWSER_CREATED') { $i } })
    $ends = @(for ($i = 0; $i -lt $stdout.Count; $i++) { if ($stdout[$i] -ceq 'EVENT: MAIN_FRAME_LOAD_END') { $i } })
    if ($starts.Count -ne 1 -or $created.Count -ne 1 -or $ends.Count -ne 1 -or
        $created[0] -ge $starts[0] -or $starts[0] -ge $ends[0]) {
        return New-P309gDisposition 'INCOMPLETE' 'FIXTURE_LOAD_START_MISSING_OR_CONTRADICTORY' $true
    }
    $filtered = [Collections.Generic.List[string]]::new()
    $otherStarts = 0
    for ($i = 0; $i -lt $stdout.Count; $i++) {
        if ($stdout[$i] -ceq 'EVENT: MAIN_FRAME_LOAD_STARTED') { continue }
        if ($stdout[$i] -ceq 'EVENT: OTHER_MAIN_FRAME_LOAD_START') {
            $otherStarts++
            if ($otherStarts -gt 8 -or $i -ge $ends[0]) { return New-P309gDisposition 'INCOMPLETE' 'OTHER_LOAD_START_INVALID' $true }
            continue
        }
        $filtered.Add($stdout[$i])
    }
    # P3-09F deliberately accepts only an Int32 exit. Convert only after the
    # supervisor's original code has been validated as an integral zero.
    $client = Get-P309fRunResult -TimedOut $false -ExitCode ([int]0) -StandardOutput @($filtered.ToArray()) -StandardError $stderr
    if ($client.Disposition -cne 'OBSERVED_CLIENT_SEQUENCE') {
        return New-P309gDisposition 'INCOMPLETE' ('CLIENT_TRACE_' + $client.Reason) $true
    }
    return New-P309gDisposition 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT' 'MATCHED_ZERO_EXIT_AND_CLIENT_MARKERS' $true
}
