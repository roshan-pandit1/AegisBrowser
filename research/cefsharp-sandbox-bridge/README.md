# Experimental CefSharp sandbox bridge patch

This **uncompiled source prototype** targets CefSharp commit
`ec1027aa21e1af1217681a349104c15d29bbc21a` (154.0.340). It is not a
replacement package, production integration, or evidence of sandbox enforcement.

The patch adds explicit, nonzero bootstrap `HINSTANCE` and sandbox-info arguments
to initialization and subprocess entry points. Legacy entry points throw instead
of silently passing null. The experimental initialization requires an empty
subprocess path, disabled external arguments, and no custom CEF arguments. It
rejects CefSharp's dependency check until a same-executable-aware check exists;
the experimental host must validate its dependencies separately. It removes the
empty-path `File.Exists` rejection in the initialization path.

The native host must supply the bootstrap arguments synchronously in each process,
choose the appropriate CefSharp subprocess entry point and retain the bootstrap
context for the required lifetime. This patch does not implement that host,
prove compatibility with CefSharp's renderer callbacks, or verify the CLR can be
loaded safely at that lifecycle point. It must not be used in production.

Apply and compile only in a disposable full checkout of the pinned source:

```powershell
git rev-parse HEAD
git apply --check path/to/0001-experimental-sandbox-info.patch
git apply path/to/0001-experimental-sandbox-info.patch
```

The exact runtime and BrowserSubprocess.Core projects and native host must all be
built against compatible CEF binaries. The existing P3-09E baseline does not do
that automatically. Compilation, native sandbox enforcement, process cleanup,
and managed WinForms rendering remain unverified. Preserve the original production
CefSharp packages and the application's proxy guard.
