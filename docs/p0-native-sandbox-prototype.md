# P0 native sandbox prototype

This experiment starts from `2cb06545f196e8900c651fefb147e934b6e68864` on
`release/sandbox-remediation`. It does not change the production application,
its CefSharp packages, profile handling, or the required-proxy guard. Production
remains **NO_GO** until the managed browser and renderer sandbox are independently
verified on native Windows x64.

The pinned runtime is CefSharp 154.0.340, CEF 154.0.34, and Chromium
154.0.8037.98. The CEF source revision is
`14c5a089e8452874cb1a556dc3fde26ed2d87723`; the experimental CefSharp patch
targets `ec1027aa21e1af1217681a349104c15d29bbc21a`. See
`docs/production-sandbox-feasibility.md` for the preceding assessment and its
limitations.

## What is implemented

The isolated `tests/research/p0native` client exports `RunWinMain` for the
pinned GUI bootstrap. It requires a non-null bootstrap context, passes it to
`CefExecuteProcess` and `CefInitialize`, requests the same executable for child
processes, and rejects sandbox-disabling arguments. It emits presence markers,
never raw pointer values. The hosted workflow uses a pinned CEF archive and a
bounded process supervisor. It is a native CEF test client, **not** the Aegis
WinForms application.

The source patch under `research/cefsharp-sandbox-bridge` sketches a fail-closed
binding interface carrying the bootstrap instance and sandbox context. It is
not a shipped binary or a working managed host. The independent test component
under `tests/research/p0isolation` exercises process-identity correlation and
restricted-token file-access controls using test-owned processes and files.
Neither the presence of a sandbox pointer nor the synthetic controls prove
Chromium renderer identity or enforcement.

The upstream [CEF sandbox setup for the pinned revision](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/docs/sandbox_setup.md)
documents the M138+ bootstrap, same-executable, and sandbox-info requirements.

## Native Windows x64 evidence

On commit `29e9c00979ac255c3fa64de3a1391affdfb75471`, the
[hosted Windows x64 experiment](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44073119021)
compiled the GUI client and both patched CefSharp C++/CLI projects. This is
compile/link evidence for the patched bindings, not a loaded or functional
managed bridge. The patch SHA-256 recorded by that job is
`b6d89f56fe47aedd85d10c615aac045cdfe5c03bb16be6e344f43b971e1e5c55`.
The GUI bootstrap from the pinned archive had SHA-256
`0b6c80b6c81615f8c2b6c051d698cb9ff93d626fb82016ce61eefd62d8cc4fec`.

The native test logged `RunWinMain`, non-null sandbox-info markers for the
browser's `CefExecuteProcess` and `CefInitialize`, successful initialization,
fixture load, browser `OnBeforeClose`, `CefShutdown` return, and parent exit
code zero. It reported a renderer launch request and an in-process renderer
claim, but the renderer's observation-pipe open failed with access denied.
The cause is not established. Renderer identity, enforcement, and complete
subprocess cleanup remain **INCONCLUSIVE**. The fixture is a synthetic native
page, not HTTPS or Aegis.

The same workflow passed 44 synthetic native gate checks and eight synthetic
process/token controls. The [existing Windows regression workflow](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44073118925)
also passed on this commit. Neither workflow establishes a sandboxed managed
browser or production readiness.

## Remaining gates

The binding patch still requires a managed bootstrap/CLR lifecycle and a real
WinForms browser. CefSharp's renderer uses `SubProcessApp`; the native sample's
CEF app is not an established substitute. Starting the CLR inside a sandboxed
child before `CefExecuteProcess` has not been shown safe. A bounded next
experiment is browser-process-only CLR hosting after native subprocess dispatch,
with validated dependencies and a disposable profile; basic rendering would
still not establish CefSharp renderer callback compatibility. Renderer role
must be independently corroborated and tied to a retained process handle;
token restrictions, policy and controlled native access denial must then be
measured in that renderer. A synthetic restricted token or a process argument
alone is not sufficient. Controlled HTTPS navigation, disposable profiles,
the existing proxy refusal, browser closure and complete CEF subprocess cleanup
also remain required.

Previous P3-09J evidence remains separate: its test-owned native sample did not
establish renderer identity or sandbox enforcement. Its observed named-pipe
access denial has no established cause. No result from that sample establishes
that Aegis is sandboxed.
