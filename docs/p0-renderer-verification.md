# Renderer sandbox verification experiment

This branch starts at PR #78 commit
`51dcac57697f72a2693f35f0db8a852f6c78fc4d`. It does not modify the
shipping browser or enable production proxy routing. Production remains
**NO_GO** until the renderer and release gates have independent evidence.

The [baseline Windows x64 run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44234038645)
reported a managed browser window, local-page load, JavaScript result 42,
trusted loopback HTTPS content, browser close and CEF shutdown, with parent
exit code zero. The test root was independently removed. The renderer observer
reported `OBSERVATION_TIMEOUT_OR_PARENT_EXIT`; renderer identity, token,
sandbox enforcement and complete subprocess cleanup were inconclusive.
Several child candidates exited, but discovery and accounting were partial.

The native renderer sends bounded `P309I_*` diagnostic messages to the browser.
In the baseline, their logger belonged to the native sample client, while the
managed browser used CefSharp's separate `ClientAdapter`. Thus the absence of a
diagnostic line could not distinguish a missing switch from an access-denied
pipe open. A test-only binding patch can surface these messages as **claims**;
it cannot authenticate a PID or establish a renderer sandbox.

The native renderer IPC bridge is a limited experiment, not an implementation
of all `CefAppUnmanagedWrapper` behavior. Primitive JavaScript results,
unsupported complex values, exception handling, and lifetime events require
their own executable checks. JavaScript object binding, callbacks, promises,
custom schemes and production compatibility remain separate requirements.

The experiment also creates a disposable file with a protected current-user
DACL. A separate test-owned non-CEF process must open the same retained file
identity as a positive control. The renderer callback attempts a native read
open and reports an outcome as an untrusted claim. A different temporary path,
missing file, or an uncorrelated access-denied claim does not demonstrate
renderer enforcement. The test does not change the renderer's sandbox or the
production browser's permissions.

The dedicated Windows workflow uses the pinned CEF 154.0.34 / Chromium
154.0.8037.98 distribution and CefSharp 154.0.340 source patch. A strict gate
requires independently verified renderer identity, sandbox enforcement and
complete subprocess cleanup; a successful managed browser lifecycle alone
cannot satisfy it. Synthetic positive and negative controls are identified as
such and are not renderer enforcement evidence.

On commit `91d85ada66aafae5f954f3d1802e955eb9a9989d`, the
[native Windows x64 run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44291879882)
completed the managed browser lifecycle, primitive IPC checks and HTTPS
fixture, with parent exit 0 and independent removal of the test root. The
non-CEF positive reader opened the exact retained sentinel file. Renderer-side
messages claimed `ERROR_ACCESS_DENIED` for the sentinel and pipe opens. The
observer retained a live child handle associated with a PID claim and observed
a null-only restricting SID, integrity RID 0, job membership, and disabled
Win32k system calls. A later `AccessCheck` of that candidate's primary token
against the pipe DACL returned denied. The role, original thread token,
mandatory integrity check, exact sentinel object, and actual cause of the
earlier open failure were not independently established. The result is not
renderer identity or enforcement proof. The strict security gate failed, as
required, and [machine-readable evidence](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44291879882/artifacts/14420844912)
was retained.

The pinned Chromium `USER_LOCKDOWN` token construction uses a null restricting
SID and makes the user SID deny-only; this source path is consistent with a
restrictive access model but does not establish that the claimed candidate used
that exact policy. The pinned CEF DevTools controller attaches to the inspected
`WebContents`, while Chromium's `SystemInfo.getProcessInfo` requires a browser
session. The pinned [CEF controller](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/libcef/browser/devtools/devtools_controller.cc)
attaches to a `WebContents` target; Chromium's [frame agent](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/content/browser/devtools/render_frame_devtools_agent_host.cc),
[target handler](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/content/browser/devtools/protocol/target_handler.cc)
and [system-info handler](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/content/browser/devtools/protocol/system_info_handler.cc)
do not give that page-scoped session browser-target process enumeration. A
supported, test-only brokered or inherited
channel with kernel-bound client identity and independent Chromium role
corroboration remains necessary.

The [first verification-branch run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44288296225)
failed before CEF started: the test-owned zero-exit helper completed with exit
code zero before the supervisor retained a process identity. The synthetic
helper now waits for a named event released by the identity callback. The
supervisor still requires a retained identity and retains its original timeout;
this change does not affect Chromium process classification.

In the [next native Windows x64 run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44289775737),
the managed browser and primitive IPC controls passed. The separate non-CEF
reader opened the same retained sentinel file identity. Two renderer-side
messages each claimed error 5 for the sentinel open and for the observation
pipe open. These claims were not authenticated against the pipe or exact
sentinel identity, so they do not establish renderer sandbox enforcement.
The observer discarded its candidate when two distinct PID claims arrived
during navigation. It now counts a bounded number of additional distinct
claims without treating them as verified; duplicate or malformed claims still
invalidate the observation. The strict security gate remained failed and the
test root was independently removed.

No runtime migration, browser-engine upgrade, signing, packaging or production
integration is authorized by this experiment's success alone. Those release
requirements remain unverified and require separate evidence.

| Production acceptance area | Result |
| --- | --- |
| Independent renderer identification and sandbox policy | INCONCLUSIVE |
| Renderer native access denial | INCONCLUSIVE: uncorrelated claim |
| Test-owned non-CEF positive read | PASS: exact retained file identity |
| Primitive JavaScript, trusted loopback HTTPS, browser close | PASS within the experimental fixture |
| Full CefSharp IPC, Aegis profiles, tabs, popups and privacy | BLOCKED |
| Required-proxy refusal under the new host | BLOCKED |
| Renderer crash recovery and complete subprocess cleanup | INCONCLUSIVE |
| Supported runtime and patched engine migration | BLOCKED pending sandbox architecture |
| Signing, notices, SBOM, package, upgrade and rollback | BLOCKED |
| Production release | NO_GO |

## Scoped observation-pipe ACL investigation (November 18, 2026)

The final-head 2146e7a Windows evidence includes a live child **primary token**
with exactly one NULL restricting SID (`S-1-0-0`) and a later
`PipeDaclProbe` result of `DACL_DENIED`. The observer's pipe DACL allowed
current-user, RC and AC access, but not the NULL restricting SID. For a
restricted token, Windows DACL access checks include the restricting SID pass;
this offers a specific, testable explanation for the reported pipe
`ERROR_ACCESS_DENIED`. It does not prove the claimed process was a renderer,
that its effective thread token matched the primary token, or the original
failure's cause.

The isolated observation pipe now adds a **read/write ACE for the NULL SID** in
addition to the current-user ACE and untrusted mandatory label. This does not
change Chromium sandbox configuration, production pipe/file ACLs, test-sentinel
ACL, or browser process privileges. A research-only synthetic access-check and
impersonated `CreateFile` test covers the NULL-restricted positive and a
WORLD-only restricted negative. All independent identity, enforcement,
paired-object and descendant-cleanup release gates remain unchanged.

If the observer can connect, the kernel-provided named-pipe client PID can be
checked against retained OS process handles. A successful pipe connection
**alone** is not an independently corroborated Chromium renderer role or a
sandbox enforcement verdict. Evidence must retain the actual run commit and
original errors. No production release is authorized by this adjustment.

## Brokered observation follow-up

The PR #79 head `e0dd75742c5fa760f889fdafce80a7ff9182c674` was exercised in
[Windows x64 run 44298542524](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44298542524).
Its managed browser loaded the controlled HTTPS page, evaluated JavaScript,
and shut down normally. The browser-owned broker relayed a claimed renderer
PID; a retained child handle showed a restricted token and Win32k mitigation.
The final security gate **failed**: the renderer role, exact-object denial and
complete subprocess cleanup remain inconclusive. The non-CEF reader opened
the retained sentinel in that run; that does not authenticate the separate
renderer denial claim.

Two test-harness defects are corrected in this follow-up. Previously a stdout
claim and the broker competed for one observation slot, so the untrusted
stdout claim could prevent the broker observation. Each path now gets at most
one retained candidate; conflicting process identities remain inconclusive,
and both handles are closed. The passive tracker previously stopped after 128
snapshots (about 25.6 seconds) even when the supervisor allowed 90 seconds.
Its scan budget now follows the validated supervisor deadline, capped at 601
snapshots. Sampling can still miss short-lived processes, and it never proves
complete descendant accounting or cleanup.

Local Windows ARM64/x64-emulation tests passed 45 synthetic pipe-observer
checks and 38 passive-process checks. These are not Chromium renderer tests.
The local sentinel self-test failed with
`POSITIVE_CONTROL_POSITIVE_TOKEN_NOT_ESTABLISHED`; no result from that run is
treated as an unrestricted positive control or sandbox evidence. The hosted
Windows x64 test must be assessed separately.

The pinned CEF internal frame implementation exposes its render-frame host,
but the public CEF frame API does not expose the corresponding OS process
handle. Pinned Chromium's browser-side tracing emits a frame/process mapping.
The experimental managed fixture now tries a bounded page-target tracing
probe through CefSharp's in-process DevTools API, with no remote debugging
port. It accepts only a matching browser PID and DevTools main-frame ID,
limits data and rejects ambiguous observations; unconfirmed tracing shutdown
fails the fixture. Five synthetic parsing checks pass locally. Actual CEF
tracing, process-handle binding, liveness and navigation races are not yet
verified. This diagnostic does not establish authoritative renderer identity.

The relevant upstream implementations are pinned
[Chromium tracing_handler.cc](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/content/browser/devtools/protocol/tracing_handler.cc)
and [CEF frame_host_impl.cc](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/libcef/browser/frame_host_impl.cc).
Renderer identity, real enforcement, full CefSharp compatibility and
complete cleanup remain release blockers.

The first native run of the tracing probe, on commit `ef23b7b`, is
[run 44303868493](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44303868493).
It reported one current-main-frame trace candidate and a different earlier
broker candidate; a process with the trace PID also appeared in passive
tracking. The managed HTTPS and shutdown fixture succeeded, but the security
gate failed: shared output and independently sampled PIDs did not authenticate
the trace or bind it to a live handle at the trace time.

The subsequent test-only handoff uses a separate supervisor pipe, verifies the
kernel-reported browser client PID against the retained browser handle and
creation time, and establishes a process-tracker scan barrier before tracing.
The supervisor accepts only a live process handle retained before that barrier.
Malformed, duplicate, stale and wrong-peer messages remain inconclusive. Local
synthetic controls passed, but this handoff has not yet proved that the frame
remained associated with the same renderer throughout navigation or that the
sentinel access targeted the held file. It does not promote renderer identity,
sandbox enforcement or descendant cleanup to verified.

The handoff's initial hosted run
[44306036881](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44306036881)
failed its synthetic exited-process assertion before starting the native CEF
fixture. Its output did not include the actual status or reason. A local
reproduction observed a still-live retained handle after the fixture's
managed exit check. The test now waits for the owned Windows process handle
to signal and confirms that the tracker rejects it before submitting the
stale PID. Fifty bounded local synthetic runs passed after this change; that
does not establish the exact cause of the hosted failure or prove that the
failure cannot recur. Native CEF and final security gates require a new run.

[Run 44307646689](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44307646689)
passed the corrected synthetic controls but failed while decoding the native
fixture's output: the strict record allowlist did not include the new trace
correlation record. The decoder now requires exactly one record with bounded,
explicit fields and still rejects unknown kinds and any attempt to promote
identity or enforcement. The native correlation result from that failed run
was not reported and must not be inferred.

[Run 44309510925](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44309510925)
failed the decoder's synthetic positive control before launching CEF. The
fixture assumed JSON integers deserialize as `Int32`; the hosted PowerShell 7
deserializes them as `Int64`. The validator now accepts either integral type
within the existing bounds and rejects floats, decimals, strings and forged
security statuses. The corrected synthetic tests passed in local PowerShell
7.4.22; a new Windows native run is required.
