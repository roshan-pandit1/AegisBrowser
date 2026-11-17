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
