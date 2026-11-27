# WebView2 feasibility source draft

**NOT_RUN / UNVERIFIED.** This isolated source draft is disconnected from the
production browser. It has not been compiled or launched. No project file,
package restore, native loader, or executable is supplied. The downloaded
candidate SDK has not passed publisher-signature and trust verification; do
not reference its binaries or run its package build assets until qualified.

The draft sketches two separate environment folders, local synthetic content,
download and permission denial, navigation restriction, resource interception,
runtime-version display, synthetic cookie/storage calls and disposal. API signatures, callback ordering, whether `NavigateToString`
works with the interception policy, download output timing, and shutdown are
not verified. It does not implement or establish cookie or storage isolation,
renderer sandboxing, proxy containment, profile security, runtime version
reporting, or production readiness. There is no protected-proxy mode.

Next: authenticate an SDK and its dependencies, review a workspace-local build
path and disposable-profile lifecycle, then compile. Runtime tests require a
separately authorized environment. Never use real profiles or credentials.

The reviewed candidate is Microsoft.Web.WebView2 `1.0.4370.26`, obtained from
the [official NuGet package page](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4370.26).
The user-approved local download has SHA-256
`1C0DACAC84B70109B3FD42F90C53A0BA942ABC3A87590D7A4E0CCD86E4A06CF4`.
The package was not extracted or executed; its NuGet signature, certificate
chain, publisher identity, dependency closure and .NET 10 behavior are unverified.
Metadata-only archive inspection found candidate managed Core and WinForms DLLs
and an x64 loader, but this is not authenticity evidence. NuGet signature
verification can consult certificate stores, revocation services and caches;
its complete environment and authorization requirements must be reviewed first.
