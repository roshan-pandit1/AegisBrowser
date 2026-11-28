# Two-control deterministic scenario observation v2

**RUNTIME NOT RUN; EXECUTION NOT AUTHORIZED.** This is a test-only observation
fixture. It is not a v4 corrective build, an implementation of the prototype's
strict lease contract, or a browser release. A matching URI, header and candidate
navigation ID do not authenticate the originating host call.

The `--scenario` argument is mandatory and must be exactly one of `Normal`,
`OnePending` or `BothPending`. Missing, duplicate and invalid values are rejected.
The requested scenario is recorded and displayed; an observed scenario mismatch
blocks execution. The fixture initializes separate A and B controls and profile
folders, automatically starts normal synthetic navigation in both, then posts
the requested scenario to the owning STA after the required observed successes.
No manual close-path selection is needed. The two responses use zero-byte bodies,
so blank WebView panes are expected; whether they complete is a runtime question.

- `Normal`: after both normal observations, request normal window close.
- `OnePending`: dispatch a second navigation only in A, then request close twice.
- `BothPending`: dispatch a second navigation in A and B, then request close twice.

Use a fresh process for each scenario. Prioritize Normal and OnePending. The v1
BothPending observation already passed three times; repeat it only if necessary
to qualify the changed v2 implementation. A race precondition must be observed
at the first actual close; a missed precondition is INCONCLUSIVE, even with a
zero process exit. Track navigation IDs within their control slot; the same
numeric ID may occur in both. A close or cancellation request does not itself
release observed pending work, and an absent terminal event remains unresolved.

The operator approved a fixture-only observation contract: attempt sequential
STA disposal after matching candidate terminal callbacks and observed handler
bodies reach their recorded tail. This does not authenticate ownership or prove
that no native work or callback remains queued. ResourceComplete marks the
managed handler body's tail immediately before return, not proof of native
quiescence. DisposeCheckpoint is flushed before the final gate recheck;
DisposeEnter is recorded after recheck immediately before Dispose but may not
flush if synchronous disposal hangs. Neither a zero gate count nor a passing
single run establishes safety for arbitrary browsing or production. The v4
prototype contract has not been changed.

The bounded DiagnosticJournal records sanitized fixed events, scenario, slot,
navigation ID, observed active count, sequence, monotonic ticks/frequency,
thread, run ID and validated runtime version. It does not intentionally record
raw URLs, headers, cookies, credentials, page contents or personal profile data.
Queue or writer failure can leave a partial log. `ProcessOutcome` is exit intent;
capture the actual process exit code separately. A missing event is not proof an
event did not occur. Synthetic replay, stale/competing IDs, redirects, missing
terminal and other fault-injection checks are not actual WebView2 observations.

## Before execution

Obtain fresh authorization for the exact artifact and hashes, disposable VM and
standard account, profile/log writes, WebView2 subprocesses, network scope and
evidence transfer. Independently verify VM network isolation before launch.
`.invalid`, navigation cancellation and request interception do not provide
containment: background networking or request fallthrough can occur. Do not run
on the development host, change OS policy or certificates, delete profiles, or
terminate unrelated processes. Previous v1/v3 artifacts and profiles are retained.
The approved VM is reported as Windows 11 ARM64 with x64 emulation, x64 .NET and
Windows Desktop 10.0.15, and WebView2 Runtime 158.0.4370.45; confirm those facts
and the `RuntimeSelected` records for the actual run. ARM64 emulation is not
native-x64 production acceptance.

Verify the independently communicated ZIP hash **before** extraction to a new
approved directory; then verify the separately communicated manifest and
assembly hashes and every inventory file's path, length and SHA-256. Stop on a
hash mismatch, duplicate, extra file, reparse point or existing destination.
Never overwrite prior bundles. The build is framework-dependent and has no
`.deps.json`; runtime discovery/loading is not established by compilation.

For example, replace the three placeholders below with the exact hashes received
through the trusted handoff channel. The archive stays open for both verification
and extraction, and destination files are created without overwriting:

```powershell
$ErrorActionPreference = 'Stop'
$zip = 'C:\AegisLab\Input\webview2-two-control-observation-v2.zip'
$dest = 'C:\AegisLab\Build\webview2-two-control-observation-v2'
$expectedZip = '<approved-zip-sha256>'
$expectedManifest = '<approved-manifest-sha256>'
$expectedAssembly = '<approved-assembly-sha256>'
if (Test-Path -LiteralPath $dest) { throw 'Destination exists' }
$parent = Get-Item -LiteralPath (Split-Path -Parent $dest)
for ($item = $parent; $null -ne $item; $item = $item.Parent) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Extraction parent or ancestor is a reparse point'
    }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inputFile = [IO.File]::Open($zip, [IO.FileMode]::Open,
    [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    if ((Get-FileHash -InputStream $inputFile -Algorithm SHA256).Hash -ne $expectedZip) {
        throw 'ZIP mismatch'
    }
    $inputFile.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($inputFile,
        [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name -notmatch '^[A-Za-z0-9._/-]+$' -or $name.StartsWith('/') -or
                $name.EndsWith('/') -or $name.Contains('//') -or
                $name -match '(^|/)\.\.?(/|$)' -or $entries.ContainsKey($name)) {
                throw 'Unsafe or duplicate archive path'
            }
            $entries[$name] = $entry
        }
        if (-not $entries.ContainsKey('bundle-manifest.json')) { throw 'Manifest missing' }
        $stream = $entries['bundle-manifest.json'].Open()
        try { $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
        if ($hash -ne $expectedManifest) { throw 'Manifest mismatch' }
        $stream = $entries['bundle-manifest.json'].Open()
        try {
            $reader = [IO.StreamReader]::new($stream)
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
        if ($entries.Count -ne ($manifest.files.Count + 1)) { throw 'Unexpected archive entry count' }
        $seen = @{}
        foreach ($item in $manifest.files) {
            $name = [string]$item.path
            if ($name -eq 'bundle-manifest.json' -or $seen.ContainsKey($name) -or
                -not $entries.ContainsKey($name)) { throw 'Manifest path mismatch' }
            $seen[$name] = $true
            $entry = $entries[$name]
            if ($entry.Length -ne [long]$item.bytes) { throw "Length mismatch: $name" }
            $stream = $entry.Open()
            try { $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if ($hash -ne [string]$item.sha256) { throw "Hash mismatch: $name" }
        }
        $fixture = @($manifest.files | Where-Object { $_.path -ceq 'TwoControlFixture.dll' })
        if ($fixture.Count -ne 1 -or $fixture[0].sha256 -ne $expectedAssembly) {
            throw 'Assembly mismatch'
        }
        [void][IO.Directory]::CreateDirectory($dest)
        foreach ($entry in $archive.Entries) {
            $target = Join-Path $dest ($entry.FullName.Replace('/', '\'))
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            $from = $entry.Open()
            try {
                $to = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try { $from.CopyTo($to) } finally { $to.Dispose() }
            } finally { $from.Dispose() }
        }
        foreach ($item in $manifest.files) {
            $path = Join-Path $dest ($item.path.Replace('/', '\'))
            $file = Get-Item -LiteralPath $path
            if ($file.Length -ne [long]$item.bytes -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) {
                throw "Extracted file mismatch: $($item.path)"
            }
        }
        if ((Get-FileHash -LiteralPath (Join-Path $dest 'bundle-manifest.json') -Algorithm SHA256).Hash -ne $expectedManifest) {
            throw 'Extracted manifest mismatch'
        }
        Write-Host 'PASS: archive, manifest, assembly and all inventoried files verified.'
    } finally { $archive.Dispose() }
} finally { $inputFile.Dispose() }
```

If a check fails, preserve the partial directory for review; do not run from it.
These checks do not eliminate malicious same-user filesystem replacement races.

## Operator launch, only after separate authorization

The same wrapper is used for each run. Unlike v1, it forwards the scenario to
the executable. It reserves a fresh log with CreateNew, copies stdout and stderr
to it, and captures the actual native process exit code. The labels are not sufficient
evidence on their own; compare the requested and actual scenario records.

```powershell
function Invoke-AegisTwoControlV2 {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Normal', 'OnePending', 'BothPending')]
        [string] $Scenario
    )
    if ($Scenario -cnotin @('Normal', 'OnePending', 'BothPending')) {
        throw 'Scenario must use the exact casing shown in the approved list.'
    }
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $fixture = 'C:\AegisLab\Build\webview2-two-control-observation-v2\TwoControlFixture.dll'
    $log = Join-Path 'C:\AegisLab\Profiles' (
        'two-control-v2-' + $Scenario + '-' + [guid]::NewGuid().ToString('N') + '.log'
    )
    $stream = [IO.File]::Open($log, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $fixtureExitCode = $null
    try {
        # Avoid reporting a stale exit code if the host fails to start.
        $global:LASTEXITCODE = $null
        & 'C:\Program Files\dotnet\x64\dotnet.exe' exec $fixture `
            --profile-root 'C:\AegisLab\Profiles' `
            --shutdown-timeout-ms 30000 --scenario $Scenario 2>&1 |
            ForEach-Object {
                try { $writer.WriteLine([string]$_); $writer.Flush() }
                catch { throw }
            }
        $fixtureExitCode = $global:LASTEXITCODE
    } catch {
        $fixtureExitCode = $null
        try { $writer.WriteLine('WRAPPER_LAUNCH_OR_CAPTURE_FAILURE'); $writer.Flush() } catch {}
        Write-Warning 'Launch or log capture failed; actual process exit code is unknown.'
    } finally { $writer.Dispose() }
    Write-Host "Scenario: $Scenario"
    Write-Host "Diagnostic log: $log"
    if ($null -eq $fixtureExitCode) { Write-Host 'Actual process exit code: UNKNOWN' }
    else { Write-Host "Actual process exit code: $fixtureExitCode" }
}
```

Run the following separately only after the prerequisites are satisfied:

```powershell
Invoke-AegisTwoControlV2 -Scenario Normal
Invoke-AegisTwoControlV2 -Scenario OnePending
# Only if needed to qualify v2:
Invoke-AegisTwoControlV2 -Scenario BothPending
```

The fixture makes a fresh `C:\AegisLab\Profiles\two-control-<guid>` with
independent `a` and `b` folders for each process. The separate `two-control-v2-`
log path is printed after exit and is reserved without overwriting. The window
title is `Two-control lifecycle observation: <name>` and its label is
`Selected scenario: <name>`; no click between close
paths is required. An early manual close before the scenario starts records
`ScenarioMismatch`, marks the observation ambiguous, and blocks closure; it is
not a qualifying normal-close observation.
Actual process exit must be captured in addition to ProcessOutcome in the log.
The fixture journal intentionally uses only sanitized fixed fields, but host or
loader stderr may contain filesystem paths or other environmental details.
Keep the combined log in the approved VM; review it for sensitive data before
transferring evidence. Do not publish unrestricted stderr automatically.

Use 60 seconds per initialization, normal navigation, scenario and closing
stage as an **external observation window**. The required 30000 ms argument is
validated but is **not enforced** by this fixture and cannot interrupt a hung
STA Dispose. On a missing callback or deadline, retain the last flushed log and
profiles, mark INCONCLUSIVE and consult the VM owner. Do not forcibly terminate
or treat interruption as graceful shutdown; any intervention needs separate
authorization.

## Narrow results

For any scenario, require both initialization and runtime-version records,
matching normal start and terminal events and a synthetic response for each
slot, matching requested/actual scenario markers, sequential DisposeCheckpoint,
DisposeEnter and DisposeReturn for A then B, FormClosed, MessageLoopReturn and
actual exit code zero. Correlate IDs only within their own slot.

The requested scenario markers are `RequestedNormal`, `RequestedOnePending`
and `RequestedBothPending`; the corresponding executed markers are
`ScenarioNormal`, `ScenarioOnePending` and `ScenarioBothPending`. Confirm the
corresponding requested and executed markers appear exactly once. A
`ScenarioMismatch` cannot be treated as a pass.

For `OnePending`, require only A to have a second dispatch and matching
cancelled-start and `OperationCanceled` terminal; B must not have a second
dispatch. For `BothPending`, require those records for both slots. Both require
a met race precondition and repeated-close record. `Normal` must have neither
second dispatch. The actual scenario and requested scenario must agree.

Mark a definite wrong outcome or explicit rejected mismatch FAIL. A missing
marker, lost diagnostics, missed precondition, unresolved work, timeout or
unknown actual process exit is INCONCLUSIVE. A passing observation applies only
to this particular fixture and environment. Renderer sandbox, network containment,
storage isolation, native-x64 qualification, the original v3 hang and general
navigation ownership remain separate and unresolved.
