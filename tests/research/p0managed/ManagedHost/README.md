# Browser-process managed host experiment

This test-only fixture is derived from PR #78 commit `51dcac57697f72a2693f35f0db8a852f6c78fc4d`. Its managed host is retargeted to .NET 10 and explicitly requires the browser-process runtime to be Windows x64 .NET 10.0.15. The CEF distribution remains pinned to CEF 154.0.34 / Chromium 154.0.8037.98 and the experimental binding to CefSharp 154.0.340. The first Windows execution of this port is required before claiming .NET 10 host compatibility.

The [CEF Windows bootstrap](https://chromiumembedded.github.io/cef/sandbox_setup) is the shortest supported candidate for the lifecycle experiment, but this fixture's partial renderer protocol and lack of authoritative renderer identity, lockdown and exact-object enforcement evidence prevent production integration. The workflow preserves a separate fail-closed independent-security gate. The actual Aegis application is unchanged and production remains NO_GO.

This is a disposable research class library, not the Aegis application. A GUI
bootstrap host must dispatch CEF subprocesses first, then invoke `ManagedEntry.Run`
synchronously on the browser process's STA thread through .NET hosting. The
ABI is Cdecl `(HINSTANCE, sandbox_info) -> int`; pointer values are not logged.

Build the managed project and its metadata-only checks on Windows x64:

```powershell
dotnet build tests/research/p0managed/ManagedHost/P0ManagedHost.csproj -c Release -r win-x64
dotnet run --project tests/research/p0managed/ManagedHost.Tests -c Release -r win-x64
```

The output is `bin/Release/net10.0-windows/win-x64/P0ManagedHost.dll`, its
`.deps.json`, and `.runtimeconfig.json`. The project restores the exact
`CefSharp.WinForms.NETCore` 154.0.340 package. Building against stock references
does **not** supply the patched initializer. The isolated runner must replace
`CefSharp.Core.Runtime.dll` with the separately built patched binding, verify
its identity and hash, and verify all bundle inputs before launching. At runtime
the host resolves only `InitializeSandboxed`; a missing or incompatible method
fails and never falls back to the normal null-sandbox initializer.

The fixture initializes CEF, opens a visible WinForms browser at a synthetic
`data:` page, requires a JavaScript result of 42, and then requires the normal
TLS-validated HTTPS loopback URL in `AEGIS_P0_HTTPS_URL` to return HTTP 200 and
body `AEGIS_P0_HTTPS_OK`. The address must contain a literal loopback IP and
nondefault explicit port; hostnames, non-HTTPS URLs and credentials are rejected.
The server and any temporary test certificate must be test-owned. No certificate
validation override is installed. An absent HTTPS fixture is a failure after the
local and JavaScript checks, not a successful skip.

Zero return requires all fixture checks, normal browser close and a returned CEF
shutdown call. Bounded callbacks use CEF's close notifications before releasing
the WinForms HWND; if those callbacks do not arrive the external supervisor's
deadline reports failure. The `Cef.WaitForBrowsersToClose` API has no return
value, so it is not interpreted as proof that all subprocesses terminated.
Cache data goes under a fresh random test-owned temporary directory.

The native child currently uses the experimental native CEF app, not CefSharp's
`SubProcessApp`. Therefore the managed host may fail its script test and cannot
establish renderer callback compatibility. The synthetic metadata tests do not
initialize CEF or prove sandbox enforcement. Real renderer identity, native
access denial and subprocess cleanup require independent evidence.
