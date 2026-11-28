# Two-control lifecycle observation fixture

**NOT RUN.** This separate test-only fixture is not the v4 corrective build,
not an implementation of the prototype lease contract, and not a browser
release. A recorded candidate is not authenticated navigation ownership.

Two WebView2 controls are initialized with separate A and B user-data folders.
The operator first selects **Navigate both**. Both must produce an observed
synthetic document response and a successful matching terminal event before the
next controls are enabled. Responses have zero-byte bodies; blank views are
expected. Whether a zero-byte response completes is a runtime question.

Choose exactly one next scenario per run:

* **Normal close:** after both normal navigations complete, close the window.
* **Close with A pending:** dispatch A then request close twice on the same STA.
* **Close with A and B pending:** dispatch both then request close twice on the
  same STA.

RacePreconditionMet is recorded only at the first actual close handler when
the intended dispatches are pending and no corresponding start has been
observed. Otherwise close-before-start is INCONCLUSIVE even with a zero exit.
Starts are recorded per slot before cancellation. Matching
NavigationOperationCanceled and NavigationComplete with the same slot and ID
are expected for each intended slot. Unrelated, replayed, redirected, duplicate
or mismatched events make the observation ambiguous. An exception or missing
terminal retains observed pending state; cancellation and deadlines do not
manufacture completion.

The gate counts only initialization, host dispatches and resource callback
bodies actually observed by the application. ObservedGateClear does not
establish absence of queued or future native callbacks, all native activity,
or an authenticated dispatch-to-ID binding. The fixture hands no app-owned
response stream to WebView2, yet browser-native request processing remains
opaque. The last observed gate check is immediately before each STA disposal;
disposal is synchronous and can hang. This restricted observation does not
prove arbitrary applications can safely dispose under these conditions.

**Approved fixture-only observation contract:** the operator approved artifact
preparation and transfer and the fixture's proposed attempt to dispose after
matching candidate terminal callbacks and observed resource-handler bodies have
reached their logged tail. The API evidence does not authenticate each candidate
as the host dispatch or prove that no other native work remains. This differs
from the prototype's strict no-unresolved-work rule. The approval does not
authorize execution, change the v4 contract or establish production safety.

Check DisposeCheckpoint, DisposeEnter and DisposeReturn for A and B independently,
followed by FormClosed, MessageLoopReturn and the actual process exit code.
DisposeCheckpoint is flushed before the final gate recheck. DisposeEnter is
recorded after that check immediately before calling Dispose, but might not be
flushed if synchronous disposal hangs; a missing enter is not proof no call
occurred. ResourceComplete is recorded at the resource-handler body's tail,
immediately before handler return; it does not independently prove return or
native work completion. ProcessOutcome
is exit intent and can precede journal flush failure. Records use the bounded
DiagnosticJournal: fixed event names, slot, navigation ID, observed active
count, sequence, monotonic ticks/frequency, thread, run ID and validated runtime
version. Redirect stdout to a newly approved log. URLs, headers, contents,
credentials and personal profile data are not intentionally logged. Queue or
writer failures can leave incomplete evidence; a missing marker proves nothing.

Each scenario needs a separate run because it ends in window close. The fixture
creates C:\AegisLab\Profiles\two-control-<guid>\a and \b; never reuse or
delete earlier profile directories. The required --shutdown-timeout-ms argument
is validated but is NOT ENFORCED. Use a 60-second external observation window
per stage; do not equate it with cancellation or force termination for a pass.
Missing events, responses, checkpoints or exit are INCONCLUSIVE; preserve the
evidence and consult the VM owner.

Fresh authorization for the exact artifact hashes, isolated disposable VM,
account, profile and diagnostic writes, subprocesses, background traffic and
evidence transfer is required before execution. A reserved hostname, navigation
cancellation and request interception do not provide containment; a failure to
set a response can allow ordinary network resolution. Independently verify the
VM OS/network isolation before starting WebView2. Do not run on the development
host or change policies or certificates.

Normal navigation and callback ordering are live-runtime observations.
ObservationGateTests are separate pure fault injection; modeled same-ID,
competing, stale, redirected and missing-terminal cases do not establish that
WebView2 produced them. URI/header matching is an observation, not a security
token or proof of call ownership. An ARM64 x64-emulation run cannot establish
v3's shutdown cause, native x64 qualification, sandbox or proxy protection.

## Operator procedure (only after separate execution authorization)

Obtain independently verified ZIP, manifest and assembly hashes through the
approved handoff channel; the manifest cannot authenticate itself. Verify the
ZIP hash before extracting into a **new** approved directory, then verify the
manifest hash and every listed file's length and SHA-256. Stop on a mismatch,
unexpected file, duplicate entry or reparse point. Do not alter prior profiles.
The application is framework-dependent and has no deps.json; loader and managed
dependency resolution are not established by compilation. Confirm the approved
Windows ARM64/x64-emulation configuration, x64 .NET Runtime and Windows Desktop
Runtime 10.0.15, approved WebView2 Runtime, standard account and independently
verified network isolation. Observe the actual RuntimeSelected records.

The handoff provides the exact ZIP, manifest and assembly hashes separately.
After receiving them through the approved channel, the following verifies and
extracts the ZIP; replace the three hash placeholders with those exact values:

```powershell
$zip = 'C:\AegisLab\Input\webview2-two-control-observation-v1.zip'
$dest = 'C:\AegisLab\Build\webview2-two-control-observation-v1'
$zipHash = '<approved-zip-sha256>'
$manifestHash = '<approved-manifest-sha256>'
$assemblyHash = '<approved-assembly-sha256>'
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $zipHash) { throw 'ZIP mismatch' }
if (Test-Path -LiteralPath $dest) { throw 'Destination already exists' }
Expand-Archive -LiteralPath $zip -DestinationPath $dest
$manifestPath = Join-Path $dest 'bundle-manifest.json'
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $manifestHash) { throw 'Manifest mismatch' }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$files = @(Get-ChildItem -LiteralPath $dest -File -Recurse)
if ($files.Count -ne ($manifest.files.Count + 1)) { throw 'Unexpected file count' }
foreach ($item in $manifest.files) {
    $path = Join-Path $dest $item.path
    $file = Get-Item -LiteralPath $path
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -ne [long]$item.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) {
        throw "Inventory mismatch: $($item.path)"
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $dest 'TwoControlFixture.dll') -Algorithm SHA256).Hash -ne $assemblyHash) { throw 'Assembly mismatch' }
Write-Host 'PASS: manifest, assembly and inventory verified.'
```

Stop on a failure and retain the partial extraction for inspection. Directory
and file checks cannot eliminate a malicious same-user filesystem race.

For each scenario, open a **fresh process**, choose a fresh approved diagnostic
path and reserve it without overwriting. The fixture creates fresh GUID-scoped
profile directories on each execution. The following command is a proposed
operator command, not authorization or an instruction to run on the development
host. Select the scenario in the window; the command-line is the same for all
three scenarios. The reserved log stays open for live reading and is flushed as
lines arrive; collect the actual exit code separately.

```powershell
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$fixture = 'C:\AegisLab\Build\webview2-two-control-observation-v1\TwoControlFixture.dll'
$log = Join-Path 'C:\AegisLab\Profiles' ('two-control-log-' + [guid]::NewGuid().ToString('N') + '.txt')
$stream = [IO.File]::Open($log, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
try {
    & 'C:\Program Files\dotnet\x64\dotnet.exe' exec $fixture `
        --profile-root 'C:\AegisLab\Profiles' --shutdown-timeout-ms 30000 |
        ForEach-Object { $writer.WriteLine([string]$_); $writer.Flush() }
    $fixtureExitCode = $LASTEXITCODE
} finally { $writer.Dispose() }
Write-Host "Diagnostic log: $log"
Write-Host "Actual process exit code: $fixtureExitCode"
```

The window title is **Two-control lifecycle observation**. Wait until it says
**Ready: navigate both controls**, then select **Navigate both** once. Both
panes may remain blank because the synthetic responses have zero-byte bodies.
Wait for **Normal observations completed; choose one close scenario.** In a
normal-close run use the window close button. In separate fresh runs choose
**Close with A pending** or **Close with A and B pending** once. Those buttons
request close twice automatically; do not add repeated manual clicks.

Use 60 seconds per initialization, navigation and closing stage as an external
observation window, not a program-enforced deadline. On timeout, record the
visible state and last flushed events, preserve the log and new profiles, and
consult the VM owner. Do not force termination, delete data, or classify Ctrl+C
or another interruption as graceful shutdown. Any intervention needs separate
authorization. A blocked STA Dispose cannot be timed out by this fixture.

## Narrow acceptance and event interpretation

For every scenario require Startup; InitializationStart, RuntimeSelected and
InitializationComplete for A and B; one normal NavigationRequest, NavigationStart,
SyntheticResponseServed, ResourceComplete, NavigationSucceeded and matching
NavigationComplete per slot; ObservationMatched; then DisposeCheckpoint,
DisposeEnter and DisposeReturn for A and B in that order, ObservedGateClear,
FormClosed, MessageLoopReturn and independently captured exit code 0. Events
are ordered by sequence, not assumed inter-slot timing. Check candidate IDs only
within the same slot; numerical IDs may legitimately overlap across slots.

* Normal close additionally requires ScenarioNormal and CloseRequested after
  ObservationMatched. It is a limited normal two-control lifecycle observation.
* A-pending additionally requires ScenarioOnePending, RacePreconditionMet,
  CloseRequested, CloseRepeated and, for A's second dispatch, NavigationStart,
  NavigationCancelled, NavigationOperationCanceled and NavigationComplete with
  the same slot and ID. B has no second dispatch.
* Both-pending additionally requires ScenarioBothPending, RacePreconditionMet,
  CloseRequested, CloseRepeated and those cancellation/terminal records for the
  second dispatch of **each** slot, using that slot's own ID.

An unexpected event, failed response, initialization/disposal failure or
nonzero actual exit is FAIL for the affected expectation. A missed precondition,
missing required marker, incomplete diagnostics, unresolved state or no actual
exit is INCONCLUSIVE. ObservationIncomplete can be expected while a close is
initially refused; it is not by itself a final result. Never infer event absence
from a missing log line. `active` counts observed fixture work only. A passing
single run does not establish authenticated ownership or complete native
quiescence; synthetic fault injection remains separate evidence.
