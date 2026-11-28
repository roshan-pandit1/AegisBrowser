# Single-control close-before-start observation fixture

**NOT RUN.** This is a distinct, test-only fixture, not the v4 corrective build
and not proof of dispatch ownership. Do not launch it on the development host.
It is derived from the previous single-control correlation fixture; that fixture,
the v3 bundle and all existing profile directories remain unchanged.
This fixture uses `NavigateWithWebResourceRequest`; the original v3 prototype
used `NavigateToString`. Their runtime callbacks must not be assumed identical.

After the operator presses the single dispatch button, the fixture calls
`NavigateWithWebResourceRequest` and, immediately after it returns on the same
STA and before deliberately yielding the message loop, requests window close.
The first close is refused while the observed navigation is unresolved. The
first matching observed start is associated for observation before cancellation.
An independently matched terminal completion is required before another close
is posted. Ambiguous, duplicate or unmatched events, an uncertain dispatch
exception, missing terminal completion, or incomplete initialization keep
disposal blocked. Cancellation itself and deadlines do not release anything.

The decisive prerequisite is `PRECONDITION_MET_CLOSE_BEFORE_START` after the
first `CLOSE_REQUEST`: dispatch returned and no start was observed at the
actual close event. If a start has already been observed, including reentrant
delivery during dispatch, the fixture logs `PRECONDITION_MISSED_INCONCLUSIVE`;
do not infer that the requested race occurred. If the matching start arrives
after close, expect `CANDIDATE_START_CANCELLED`, followed by a matching
`NAVIGATION_COMPLETED` with `detail=OPERATION_CANCELED`, and then
`RACE_OBSERVATION_MATCHED`. That marker means only that this particular
callback sequence was observed; it is not overall PASS. Confirm disposal
enter/return, form closed, message-loop
return and actual process exit code separately. Any missing or contradictory
evidence is inconclusive; an incorrect error outcome is a failure of the
observation expectation. A zero process exit by itself is never a passing race
observation. No diagnostic marker is proof of native-operation
ownership or general lifecycle safety.

As in the original fixture, `NAVIGATION_COMPLETED` logs active-ID-found in the
`uri` field and candidate-ID-match in the `header` field; these are not repeated
URI/header comparisons. The journal contains only fixed markers, navigation
IDs, comparison booleans, bounded exception categories and a validated runtime
version. It uses a bounded queue and background writer; missing output does not
prove an event did not occur.

This fixture accepts the existing required `--profile-root` and
`--shutdown-timeout-ms` arguments, but **does not enforce the timeout**. The
profile root must satisfy the existing policy. It creates a fresh
`C:\AegisLab\Profiles\close-race-<guid>` directory containing `close-race.log`
and a `profile` directory. Do not delete or reuse existing profiles. STA control
disposal can synchronously hang. Do not force cleanup or treat interruption as
successful shutdown.

Before any execution, independently verify the exact new artifact and all
dependencies and obtain fresh authorization for its hashes, a disposable VM,
account, new profile and diagnostic writes, subprocesses, network scope and
evidence transfer. The `.invalid` name, cancellation and `WebResourceRequested`
do not enforce isolation. WebView2 can send a request during a start callback
and can perform background networking; a failed interception can fall through.
Require independently verified OS/network isolation before execution. Never
alter a workstation's security settings or certificates to run this test.

This one scenario does not exercise two controls, competing requests, replay,
redirects, actual production shutdown, sandboxing or proxy protection. A single
run on ARM64 with x64 emulation is not native-x64 production acceptance. It
cannot retrospectively establish the cause of the prior v3 shutdown failure.

Only after separate authorization, complete hash verification and confirmation
of VM isolation, a proposed command from a fresh extracted bundle is:

```powershell
& 'C:\Program Files\dotnet\x64\dotnet.exe' exec '<verified-extraction-directory>\CloseRaceFixture.dll' --profile-root 'C:\AegisLab\Profiles' --shutdown-timeout-ms 30000
```

Press the dispatch button once. The first close request is automatic; do not
press close repeatedly to force a result. Observe externally for 60 seconds;
this is an operator observation window, not an enforced program deadline. On
an unresolved state preserve the log and consult the VM owner. Do not force
termination, delete profiles, or classify an interruption as graceful shutdown.
