# Engine transition: qualification update

**BLOCKED [OFFICIAL-UPSTREAM] [STATIC-ANALYSIS] [UNVERIFIED].** Retain the
existing engine only for the restricted development preview. No alternative is
qualified for migration and no engine is approved for production. Stock CefSharp
is not a credible path to the mandatory renderer-sandbox gate without a supported
change in that upstream capability. Investigate supported WebView2 and native CEF
in isolated prototypes before choosing; neither documentation nor a launch alone
establishes the app's sandbox or profile-scoped containment.

## Dated observations

On 2026-11-27 UTC, the official
[NuGet package page](https://www.nuget.org/packages/CefSharp.WinForms.NETCore)
listed CefSharp.WinForms.NETCore `156.0.300`, updated 2026-11-17, with an exact
dependency on CefSharp.Common.NETCore `156.0.300`; it lists `net10.0-windows`
as computed compatibility, not as the package's included target. Package metadata
also states that CefSharp does not currently support CEF sandboxing. This is not a
download, signature check, proof of tested .NET 10 behavior, or a determination
of all upstream security fixes. [OFFICIAL-UPSTREAM]

The official [WebView2 Runtime release notes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/runtime/)
list Runtime `158.0.4370.33` dated 2026-11-21. The
[Edge Stable notes](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-relnote-stable-channel)
list Stable `158.0.4370.45` dated 2026-11-25. Edge and WebView2 versions are
distinct observations and their fix sets cannot be assumed identical.
[OFFICIAL-UPSTREAM]

The [CEF build index](https://cef-builds.spotifycdn.com/index.html) was reachable
but its dynamically populated build list was not available in this observation;
its JSON index was unavailable through the source tool. The official Chrome
release landing page redirected to an inaccessible anti-automation endpoint.
Consequently current Chrome stable, latest CEF build, exact fixed-CVE lists,
backports and patch latencies are **UNVERIFIED**. Next: retrieve authenticated
official release and advisory metadata without following an unsafe redirect, map
every fixed issue to each exact candidate and obtain documented backport evidence.
Do not infer security parity from a major version. [OFFICIAL-UPSTREAM] [UNVERIFIED]

The retained prior PR #90 archive's `libcef.dll` matches SHA-256
`E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7` and a
byte-matched retained file reports CEF `154.0.34` and Chromium `154.0.8037.98`.
This does not prove what the current PR #91 ships, because remote head and build
custody were not verified. See [reconciliation](reconciliation-2026-11-27.md).
[DEVELOPER-VERIFIED] [UNVERIFIED]

## Candidate decision matrix

These are capability assessments, not runtime acceptance. UNKNOWN requires the
indicated discriminating test before a candidate can pass a mandatory gate.

| Candidate | Security and sandbox | Profiles, proxy, interception and lifecycle | Updates, licensing and containment |
| --- | --- | --- | --- |
| Existing CefSharp 154 | **UNSUPPORTED** for upstream-supported CEF sandbox; security-fix coverage **UNKNOWN**. Compare exact advisory fix commits and test a real renderer denial. | **PARTIAL**: existing app implements these paths, but cross-store isolation and lifecycle acceptance remain incomplete. Run the qualified-engine profile matrix. | **PARTIAL**: app ships its engine; package authenticity and process-scoped containment remain **UNKNOWN**. Authenticate artifacts and independently trace all network processes. |
| CefSharp 156.0.300 | **UNSUPPORTED** for upstream-supported CEF sandbox; fix coverage **UNKNOWN**. Obtain exact build and advisory mapping. | **UNKNOWN** in this app. Restore authenticated dependencies and run the isolated native compatibility matrix. | **UNKNOWN** for tested update, license and containment behavior. Verify packages, notices and all network processes. |
| WebView2 Evergreen | **UNKNOWN** for independently observed sandbox in this app. Identify the real renderer and record a denial with a control. | **UNKNOWN** for simultaneous profile separation, proxy authentication, interception, storage, permissions and shutdown under this application. Run isolated proof-of-concept tests. | **PARTIAL**: runtime update cadence differs from an app-pinned engine. **UNKNOWN** whether process identity safely scopes per-profile filters without affecting other apps; establish process identity and external WFP controls. Verify redistribution terms. |
| WebView2 Fixed Version | **UNKNOWN** for sandbox and patch coverage. Run the same renderer and advisory tests. | **UNKNOWN**: run the same profile, policy and lifecycle matrix. | **UNKNOWN**: verify supported distribution, servicing responsibility, licensing and distinct process scope; execute the independent containment protocol. |
| Native CEF sandbox host | **UNKNOWN** until a matching authenticated host, wrapper, sandbox library and subprocess are built and tested. Obtain upstream source/build provenance and independent denial evidence. | **UNKNOWN** for app compatibility and proxy/privacy integration. Build a reviewed isolated prototype and exercise the required matrix. | **UNKNOWN**: establish reproducible build, update and redistribution process and independently test per-profile process scope. |

No weighted convenience score can override an unmet security gate. The present
evidence does not determine whether WebView2 or a supported native CEF host is
the smaller maintainable secure design; the decisive facts are their independently
verified renderer restrictions, per-profile proxy/containment scope, profile
lifecycle and current security-fix provenance.

## Migration scope

An exact text search for `CefSharp` in production C# source finds 11 files; this
is a lower-bound text count, not a dependency graph. Coupled boundaries include
browser creation and shutdown (`Program.cs`, `BrowserView.cs`, `MainForm.cs`),
request interception and permissions (`FilteringResourceHandler.cs`,
`SecurityHandlers.cs`), proxy installation and authentication, profile and
storage lifecycle, and fingerprint/WebRTC policies. A future abstraction must
keep download denial, TLS handling, proxy fail-closed behavior and lifecycle
ownership explicit. No abstraction or prototype runtime result is claimed here:
the engine choice and authorized execution environment remain unresolved.
