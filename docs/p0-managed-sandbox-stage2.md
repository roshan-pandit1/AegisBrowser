# P0 managed sandbox experiment

This experimental branch starts at PR #77 commit
`3668eaa20ddc3dadcf97bcb241fbe1d7bde3a499`. It does not modify the shipping
browser or replace its packages. Production remains **NO_GO**.

The test combines the pinned CEF 154.0.34 GUI bootstrap and Chromium
154.0.8037.98 with the CefSharp 154.0.340 source patch. The native client
forwards the supplied sandbox context to `CefExecuteProcess` first. Only the
browser dispatch result can enter hostfxr and start the CLR; subprocesses do
not initialize the CLR. The managed entry point requires a nonzero bootstrap
context and resolves the experimental binding API without falling back to the
stock null-context initialization. The supervised bundle is assembled from
the pinned CEF archive and isolated build outputs. The manifest protects that
runner-owned bundle during the test; it is not publisher authentication.

The managed fixture requests a visible WinForms window, a local synthetic page,
a JavaScript result, and a loopback HTTPS page. A test-owned CA is installed
only on a declared disposable GitHub-hosted Windows runner, with a public
manifest for independent always-run cleanup. Missing fixtures, certificates,
entry points, callback markers or normal exit are failures, not passes.
Synthetic restricted-token and pipe tests are separate from Chromium evidence.

The [first hosted attempt](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44081541723)
failed before launching the browser because the disposable CA utility timed
out at the Windows trust prompt. Independent cleanup passed. After adding the
existing exact-dialog confirmation pattern and synthetic negative controls, the
[second attempt](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44083628351)
installed and removed its test root, reached hostfxr, and reported managed CEF
initialization. It then terminated before showing a window: the parent and
observed child candidates exited `0x80000003`, with Chromium reporting an
unusable GPU process. This is a failed managed run; no rendering or shutdown
claim follows from it.

The pinned CEF source [clears browser command-line switches](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/libcef/common/chrome/chrome_main_delegate_cef.cc#L154-L170)
when `command_line_args_disabled` is enabled, [propagates `--module` only when
present](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/libcef/browser/chrome/chrome_content_browser_client_cef.cc#L215-L223),
and [uses the executable basename](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/libcef_dll/bootstrap/bootstrap_win.cc#L820-L829)
as a sandboxed child's module fallback. The failing run named its executable
`bootstrap.exe` and client `p309f_client.dll`. The next controlled comparison
uses CEF's documented matching-executable-and-DLL naming convention without
changing the sandbox or enabling external arguments. The source chain explains
a plausible module-selection failure; it does not independently establish the
cause of the observed GPU crash.

The [first matching-name run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44227739249)
failed in the synthetic supervisor's zero-exit control before CEF started.
Its original assertion did not record the underlying process outcome. A
bounded diagnostic now records that outcome on failure; the assertion and its
deadline are unchanged. The bootstrap-name hypothesis was not tested by that
run.

The [matching-name comparison](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44228364247)
reached the managed WinForms `Shown` callback and returned from CEF shutdown,
but its local-page load timed out and the parent exited 1. The bounded process
observer recorded many direct child candidates exiting 10, without establishing
their Chromium roles or complete cleanup. Exit 10 is also the test client's
missing-opt-in result; filtered child environment or missing propagation is a
plausible cause, not an independently established attribution. The test-only
CefSharp source patch now appends the existing fixed child-intent marker from
its browser-process launch callback. Child sandbox-context and forbidden-switch
checks are unchanged. The marker is not identity or sandbox evidence.

The [child-marker run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44229769353)
reached the WinForms `Shown` callback and reported local-page load. CefSharp's
browser-level JavaScript evaluation then rejected the request because its
renderer-context signal was absent; the process returned 1 after the browser
close and CEF shutdown callbacks. The pinned CefSharp
[`CefAppUnmanagedWrapper`](https://github.com/cefsharp/CefSharp/blob/ec1027aa21e1af1217681a349104c15d29bbc21a/CefSharp.BrowserSubprocess.Core/CefAppUnmanagedWrapper.cpp)
provides the renderer context notification and handles evaluation messages; the
experiment's native renderer app is not that wrapper. This source difference
is a concrete compatibility blocker, although the run did not by itself prove
why the signal was absent. The next bounded check uses CefSharp's documented
frame-level evaluation API and measures HTTPS navigation independently; it
still requires a real renderer response and cannot turn a failure into a pass.

On source commit `0c312ba7171c5efd6fdeca0abf7404f8e222a36b`, the
[frame-level comparison](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44230746664)
reported a visible WinForms `Shown` callback, local-page load, and a controlled
HTTPS main-frame HTTP 200 navigation. The loopback TLS server independently
reported serving the synthetic response using a temporary trusted test CA. The
frame-level JavaScript request was canceled after its bounded deadline; the
expected JavaScript result and HTTPS page-content assertion were not obtained.
The browser `OnBeforeClose` and CEF shutdown calls returned, but the parent
exited 1 and subprocess discovery remained partial. The test root was removed
and independent cleanup passed. [Machine-readable evidence](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44230746664/artifacts/14394753859)
is tied to that source SHA. The later observation does not prove visual
rendering, renderer identity, all-child termination, or sandbox enforcement.

The production CefSharp renderer uses `CefAppUnmanagedWrapper`, derived from
`SubProcessApp`, to send context-created notifications and handle evaluation
messages. It holds managed objects and callbacks; the native-child sample does
not instantiate it. Supplying that wrapper requires a separately validated
native/managed subprocess lifecycle or a maintained native equivalent. CLR
startup in a sandboxed child before `CefExecuteProcess` has not been shown safe,
and the experiment does not select or ship either solution.

Run `.github/workflows/p0-managed-sandbox.yml` on the experiment branch. The
workflow calls `tests/research/p0native/build-patched-binding.ps1`, builds the
native and managed projects in `tests/research/p0managed/`, prepares the
hash-pinned CEF archive with the existing P0 driver, and invokes
`tests/research/p0managed/invoke-managed-ci.ps1`. A successful lifecycle is
still not proof of renderer sandbox enforcement or complete subprocess cleanup.

## Architecture decision

| Option | Assessment |
| --- | --- |
| Native CEF bootstrap plus maintained CefSharp fork | Candidate experiment. The fork must preserve CefSharp's renderer callbacks and schemes, carry sandbox context correctly, and be maintained for every CEF update. A native child `CefApp` is not yet a proven substitute for `SubProcessApp`. |
| Native CEF browser with separate managed UI | Plausible isolation boundary, but requires a designed IPC protocol and a significant rewrite of existing CefSharp browser controls, lifecycle, permissions, and profile integration. No compatibility claim is made. |
| Alternative supported embedding framework | Requires a separate feature and security review for profile isolation, settings, popup ownership, request handling, and update cadence. No specific migration is selected without evidence. |

The current experiment deliberately avoids CLR initialization inside a
sandboxed child before native CEF dispatch. How to retain CefSharp's actual
subprocess behavior is unresolved. A browser-only managed result would not
resolve that compatibility question. Renderer role, token and sandbox policy
must be independently bound to retained OS handles; pipe access-denied and
self-reported process IDs are not enforcement evidence. Actual renderer native
access denial, an unsandboxed control, full descendant accounting, Aegis
profile and HTTPS/TLS regressions, and forced-termination behavior remain
separate mandatory gates.

## Gate status for the tested source commit

| Gate | Result | Scope |
| --- | --- | --- |
| Native bootstrap | PASS | Browser-process entry and context forwarding observed; child policy not proven. |
| Managed CLR hosting | PASS | Browser-only hostfxr and managed CEF initializer executed. |
| CefSharp subprocess compatibility | FAIL | Browser-level and frame-level JavaScript evaluation did not complete. |
| Managed WinForms rendering | INCONCLUSIVE | Window and page-load callbacks occurred; visual content was not independently verified. |
| Controlled HTTPS navigation | PASS | Loopback server response and main-frame HTTP 200 observed with temporary test-root cleanup. |
| Independent renderer identification | INCONCLUSIVE | Process roles were not independently established. |
| Sandbox policy observation | INCONCLUSIVE | No verified renderer policy was observed. |
| Renderer native access denial | BLOCKED | No identified renderer executed the controlled access probe. |
| Unsandboxed positive control | BLOCKED | Synthetic restricted-token controls are not a paired renderer experiment. |
| Complete subprocess lifecycle | INCONCLUSIVE | Some retained candidates exited; enumeration and cleanup were partial. |
| Disposable Aegis compatibility | BLOCKED | The production Aegis app did not run through this host. |

The full milestone and production readiness remain **NO_GO**.
