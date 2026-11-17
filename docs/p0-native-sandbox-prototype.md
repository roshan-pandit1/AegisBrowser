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

## Evidence and remaining gates

The GUI host must be compiled and run on native Windows x64 before its native
execution can be classified. The binding patch requires an exact-source build,
a managed bootstrap/CLR lifecycle, and a real WinForms browser. Renderer role
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
