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
