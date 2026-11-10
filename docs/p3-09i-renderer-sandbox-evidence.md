# P3-09I: renderer evidence research

This isolated experiment starts at `dae16d8e6155eac8eb83ca2e4c90e28429079ce4`
(PR #54). The CEF `154.0.34+g14c5a08+chromium-154.0.8037.98` runtime,
CefSharp `154.0.340`, CEF source `14c5a089e8452874cb1a556dc3fde26ed2d87723`,
and previously verified CEF archive pin remain unchanged. See
[P3-09H](p3-09h-native-runtime-completion.md) for the prior native lifecycle.

## Evidence boundaries

The experiment runs on an ephemeral test host and does not claim resistance to
malicious administrators or hostile software in that host's security context.
A child-process role, pipe message, PID, image path, lineage, or token in
isolation cannot establish a renderer identity. Test-client observations and
OS queries are reported separately; process handles and creation times are
needed to avoid equating a recycled PID with the original process. Missing,
ambiguous, or stale evidence remains inconclusive. A named-pipe client PID is
obtained from the operating system as described by [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid),
but this API does not authenticate the client's claimed CEF role.
The API's PID is obtained before the process handle is opened; if the original
client exits and its PID is reused in that interval, a later handle cannot by
itself prove it refers to the original pipe client. The same-user, live parent,
creation-time and image checks reduce ambiguity but do not eliminate that race.
The supervisor owns the parent handle only through its process run; an
asynchronous candidate check that finishes after the parent handle is closed
fails conservatively and can lose late evidence. This is not a completion
signal or proof that no child existed.

CEF's [pinned render-process handler](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_render_process_handler.h)
documents that render callbacks run on the renderer main thread and that
`OnContextCreated` follows creation of a frame's V8 context. That contract
alone does not establish when all Chromium sandbox lockdown steps have taken
effect. A role claim made from this callback cannot by itself prove process
role or the timing of all effective restrictions.

## Policy review

The [pinned Chromium Windows sandbox source](https://github.com/chromium/chromium/blob/154.0.8037.98/sandbox/policy/win/sandbox_win.cc)
configures a low initial integrity level, delayed untrusted integrity, and
restricted/lockdown token levels in its default sandbox policy. The same
source makes that default conditional on the delegate, requests a renderer
Win32k lockdown policy, and handles AppContainer based on sandbox type and
features. Those source settings are expectations to investigate, not evidence
of an individual process's effective policy. AppContainer is not assumed to be
mandatory for this renderer. Mitigations may vary by platform or feature.
Microsoft documents that [mitigation inspection](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessmitigationpolicy)
requires `PROCESS_QUERY_INFORMATION`; unavailable access is not a pass.

An external Job Object is not automatically added to Chromium. Windows job
assignment is irreversible and inheritance has exceptions; an additional job
can also change the sandbox configuration being studied. See the [Windows Job
Object contract](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
Without a source of complete, independently verified process-creation and exit
events, parent exit or a partial child list cannot establish complete cleanup.

## Initial integrated observations

The experimental client advertises a bounded, per-invocation pipe to renderer
launch requests and sends a fixed role claim from its renderer-side V8 context
callback. The server rejects remote connections, checks the connected user's
identity, obtains the client PID from Windows, then checks retained-handle
creation time, image path and direct-parent relationship before querying its
token. The pipe uses a random name and a restricted local security descriptor;
the name and claimed role are not authentication or sandbox evidence. A
three-second delayed CEF UI task gives the observer time without waiting on
the browser UI thread. Failure to connect does not authorize forced close.

On the Windows ARM64 development host with x64 emulation, a disposable
desktop run reported fixture completion, close callbacks and `CefShutdown`,
and the retained parent exited 0 without termination. The observer reported
`OBSERVATION_TIMEOUT_OR_PARENT_EXIT`: no connected client or renderer token
was observed. Its bounded record is at
`%TEMP%\aegis-p309i-bundle-84a14e665c1e46328eb1440499fe20a8\supervisor-output-desktop.jsonl`.
A separate run inside the restricted command environment reported renderer
termination, exit 15 and no token. The two results are distinct; neither
establishes sandbox enforcement or descendant cleanup. The cause of the
missing pipe connection was not established by those traces.

A subsequent diagnostic desktop run reported that the renderer-side callback
was reached, followed by `PIPE_OPEN_FAILED` with Windows error 5 (access
denied). The fixture still loaded, the client reported normal close and
`CefShutdown`, and the retained parent exited 0. The error does not by itself
identify which Windows access check or sandbox rule denied access. No token
was observed. The bounded record is at
`%TEMP%\aegis-p309i-diag-bundle-51940cd5fa0e4d15904e3b4b3602a100\supervisor-output.jsonl`.

The fallback sends a bounded PID claim through a CEF process message and the
browser's captured standard output. The server checks a process snapshot for
the expected parent before opening a candidate, then verifies live handles,
creation times, image paths and direct-parent relationship before observing a
token. The original claim and inherited standard output are not authenticated;
the snapshot-to-handle interval also permits PID reuse. Duplicate or malformed
claims invalidate the observation. An accepted candidate handle is retained
to observe that candidate's exit and then closed, but one candidate is not a
complete descendant inventory. Competing or late observations can remain
inconclusive. No channel supplies a verified renderer role or a complete
sandbox-policy verdict.

One local desktop run with the PID fallback loaded the fixture and observed
normal native parent exit 0. The CEF client claimed PID 12040; the observer
retained a live direct-child handle and sampled its token. The snapshot reported
integrity RID 0, one restricting SID, zero privileges, job membership and
Win32k system-call disablement. The retained candidate later exited 0. These
are point-in-time observations about the handle, not independently verified
renderer role or a complete policy/cleanup verdict. The pipe still reported
access denied. The record is at
`%TEMP%\aegis-p309i-pid-bundle-80e1bc68c9764bcd858b2267c5c906ae\supervisor-native-i-output.jsonl`.

On hosted Windows Server 2022 x64, [research run 42746046714](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42746046714)
at `cf9a2a825ab950715ed2c36eb1f0aa8eaa55e21d` reported fixture completion,
normal close and parent exit 0. The CEF client claimed candidate PID 776; the
observer recorded a live direct-child token with integrity RID 0, one
restricting SID, zero privileges, job membership and Win32k system-call
disablement, then observed candidate exit 0. The pipe open failed with access
denied. Role, sandbox enforcement and full descendant accounting remained
inconclusive. This is one hosted execution, not a general security proof.

## Security boundary

This research does not alter production CefSharp, profiles, network routing,
proxy enforcement or sandbox settings. The production owner-release contract
remains unsatisfied; popup terminal delivery and production binding approval
are separate questions. A successful CEF fixture or a read-only token snapshot
does not establish behavioral sandbox enforcement or network containment.
