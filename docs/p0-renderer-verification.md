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

The [first verification-branch run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44288296225)
failed before CEF started: the test-owned zero-exit helper completed with exit
code zero before the supervisor retained a process identity. The synthetic
helper now waits for a named event released by the identity callback. The
supervisor still requires a retained identity and retains its original timeout;
this change does not affect Chromium process classification.

No runtime migration, browser-engine upgrade, signing, packaging or production
integration is authorized by this experiment's success alone. Those release
requirements remain unverified and require separate evidence.
