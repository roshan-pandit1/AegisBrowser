# P3-09J: renderer identity and process accounting

This isolated research begins at `b9977bc8a8919394e1f320cc23118fd2c638b2dc`
(PR #55). It retains CEF `154.0.34+g14c5a08+chromium-154.0.8037.98`, CEF
source `14c5a089e8452874cb1a556dc3fde26ed2d87723`, CefSharp source
`ec1027aa21e1af1217681a349104c15d29bbc21a` and package `154.0.340`.
The inherited pinned archive, bootstrap and runtime verification remains in
place. See [P3-09I](p3-09i-renderer-sandbox-evidence.md) for the previous
candidate-token observation.

## Pipe denial

The test renderer's `CreateFileA` of the per-run local pipe returned Windows
error 5. This establishes that the open was denied, not which access check,
thread token, mandatory policy or Chromium interception caused the denial.
The pipe descriptor and renderer sandbox settings are unchanged. Windows
performs an additional restricting-SID access check for restricted tokens;
both checks must grant access ([Microsoft restricted-token documentation](https://learn.microsoft.com/en-us/windows/win32/secauthz/restricted-tokens)).

The test-only `PipeDaclProbe` reads the actual server object's owner, group
and DACL and compares them with an impersonation copy of the live candidate's
primary token using `AccessCheck` and mapped file-write access. It neither
changes nor impersonates the process token. This is a later DACL comparison,
not a replay of the renderer's actual open: it omits mandatory-label checks,
the effective thread token, Chromium interception and potential state changes.
Even a DACL denial does not identify the original failure's cause. Synthetic
tests exercise same-user allow, empty-DACL denial and a restricted-token
two-check denial. They do not establish renderer behavior.

In one Windows ARM64/x64-emulated desktop run, the CEF client reported the
renderer-side pipe open failed with error 5. The separately retained,
CEF-claimed direct child's later primary-token/DACL comparison returned
`DACL_DENIED`. This is consistent with a DACL contribution but does not
establish the original failure's precise cause or compatibility with every
sandbox configuration. The same run loaded the fixture, observed normal
close callbacks and `CefShutdown`, and recorded parent exit 0. Its bounded
record is at `%TEMP%\aegis-p309i-pid-bundle-80e1bc68c9764bcd858b2267c5c906ae\supervisor-native-j-output.jsonl`.

## Identity and accounting

CEF's pinned [browser-process callback](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_browser_process_handler.h)
provides a child-launch command line, not an OS process handle. The inherited
renderer-side callback and CEF message provide a producer claim. A snapshot,
image path, parent PID and subsequent retained-handle checks add OS evidence
but cannot eliminate a snapshot-to-open PID reuse interval, authenticate the
claimed role, or resist a hostile local administrator/same-user spoofing.
Consequently renderer identity remains inconclusive.

The passive tracker periodically takes bounded Toolhelp snapshots, filters by
the verified live parent and expected image, and retains handles only after
rechecking process lifetime, parent and image. It records observed exit states
and supports nested descendants when their parents are observed alive. It
does not subscribe to creation events, can miss short-lived, pre-scan and late
processes, cannot prove mapped image hashes or infer roles, and never reports
complete accounting or verified cleanup. No external job is imposed on CEF;
job assignment can change the behavior under study and [Windows documents
inheritance exceptions](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
No global ETW session or privileged host configuration is enabled.

In that local run, 14 snapshots retained five same-image direct-child handles
and observed exit code 0 for each. One matched the CEF-claimed PID and had a
handle-bound token snapshot; the tracker itself did not identify process
roles. This is partial coverage, not proof that no other descendants existed.

The pinned [Chromium policy](https://github.com/chromium/chromium/blob/154.0.8037.98/sandbox/policy/win/sandbox_win.cc)
configures token, integrity, job and Win32k restrictions conditionally. A
candidate token snapshot is not independently verified renderer identity or
proof of complete effective policy and behavioral enforcement. AppContainer is
not assumed mandatory for every renderer configuration.

## Boundaries

Native CEF research uses disposable profiles and pinned test binaries. The
production browser, CefSharp packages, sandbox settings, proxy gate, network
configuration and user profiles are unchanged. Popup terminal delivery remains
inconclusive, production binding is not approved and the owner-release
contract remains unsatisfied. A diagnostic workflow may complete while its
native or security verdict remains incomplete; its logs must be examined.
