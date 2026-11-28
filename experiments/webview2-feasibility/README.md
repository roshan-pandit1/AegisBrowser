# WebView2 feasibility source draft

**COMPILED / RUNTIME NOT_RUN.** This isolated experiment is disconnected from
the production browser. Direct Roslyn compilation against the verified local SDK
completed with zero errors and two CS1701 assembly-reference compatibility
warnings. Its runtime behavior has not been observed. No project file or NuGet
restore is used, and package build assets were not imported or executed.

The draft sketches two separate environment folders, local synthetic content,
download and permission denial, navigation restriction, resource interception,
runtime-version display, synthetic cookie/storage calls and disposal. API signatures, callback ordering, whether `NavigateToString`
works with the interception policy, download output timing, and shutdown are
not verified. It does not implement or establish cookie or storage isolation,
renderer sandboxing, proxy containment, profile security, runtime version
reporting, or production readiness. There is no protected-proxy mode.

Next: review the recorded build, reference-compatibility warnings, disposable
profile lifecycle and background-network behavior. Runtime tests require a
separately authorized environment. Never use real profiles or credentials.

The reviewed candidate is Microsoft.Web.WebView2 `1.0.4370.26`, obtained from
the [official NuGet package page](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4370.26).
The user-approved local download has SHA-256
`1C0DACAC84B70109B3FD42F90C53A0BA942ABC3A87590D7A4E0CCD86E4A06CF4`.
Supported `dotnet nuget verify --all` reported both Microsoft author and NuGet
repository signatures and exited zero without warnings on the authorized
network-enabled run. The output did not separately expose timestamp time or
revocation freshness. Only the two managed DLLs, x64 loader and license were
extracted; their hashes match the package entries. Dependency behavior and .NET
10 runtime compatibility remain unverified. No package code or prototype
runtime was executed.

The hardened assembly SHA-256 is
`85533DA097574DD73AD630DF36B457F5A39608627A6E2B0F3A9B1B4B22D4CE3A`.
It requires an explicit `--profile-root C:\AegisLab\Profiles` and an
operator-supplied positive `--shutdown-timeout-ms`; neither establishes human
authorization or a disposable environment. The fixed root and its existing
ancestors are checked for reparse points, but those checks are non-atomic.
Two independent local compiler processes produced matching assembly hashes.
The UI-free lifecycle and profile-policy tests passed 44 and 24 assertions,
respectively; they did not start WebView2 or inspect a real profile directory.
An outstanding-operation deadline reports incomplete shutdown without claiming
to cancel native work. Synchronous control disposal itself can still hang on
the UI thread and is not covered by that deadline.
The workspace-local compiler and build manifests record the reference hashes,
source hashes, runtime configuration and warnings. Compilation is not evidence
of sandbox enforcement, profile isolation or production readiness.
