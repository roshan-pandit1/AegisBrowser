# Engine decision for the core preview

Decision date: 2026-11-25 UTC. Source baseline:
`36748360cb4c38a56fa300755b3295ec03ae1b46`.

**WP-A: BLOCKED for a security-qualified engine.** Retain the existing
CefSharp/CEF integration only for a clearly labelled **DEVELOPMENT PREVIEW**.
Do not migrate to WebView2 or replace individual CEF binaries. Required-proxy
profiles remain blocked before CEF starts. This decision does not establish a
renderer sandbox, network containment, or production readiness.

The existing app uses CefSharp 154.0.340, CEF 154.0.34 and Chromium
154.0.8037.98. The four installed NuGet packages matched the official NuGet
catalog SHA-512 and passed `dotnet nuget verify --all`; the built libcef,
managed binding, native wrapper and subprocess files matched those packages.
That verifies artifact identity, not current security-fix coverage.

The published CefSharp 156.0.300 package declares CEF 156.0.30 and Chromium
156.0.8078.96. Its WinForms, Common, CEF runtime and win-x64 runtime packages
matched the official NuGet catalog hashes and passed NuGet repository-signature
verification. The signed NuGet libcef SHA-256 matched the version-matched
official CEF archive. However, Google's subsequent Chromium 156.0.8078.100
release lists critical security fixes, and the CEF builds index lists
156.0.32/Chromium 156.0.8078.109. The official NuGet index inspected on this
date did not list a matching CEF 156.0.32 runtime or a later CefSharp binding.
No evidence here establishes that the later fixes were backported into the
156.0.30 package, or that each listed issue affects CEF. Therefore the 156
candidate is **not security-qualified** for this milestone. Its presence on
NuGet and matching hashes are not enough to close the engine gate.

The existing `feature/chromium156-engine-upgrade` branch points at the baseline
and contains no upgrade to reuse. A future upgrade needs a published matched
binding/runtime with current security-fix coverage, or a separately qualified
rebuild with verified source, ABI, provenance and native regression evidence.
Do not combine the 156.0.300 binding with a different CEF runtime by overriding
its exact dependency.

WebView2 Evergreen would update independently of Aegis; that is potentially
acceptable only with runtime-version reporting, update/restart handling and
compatibility testing. Fixed Version gives Aegis control but makes Aegis
responsible for distributing security updates. Neither a WebView2 integration
nor parity for the current proxy, privacy, profile and shutdown behavior has
been established. The observed local runtime executable was version
`158.0.4370.33`, had a valid Microsoft Authenticode signature and SHA-256
`B9845539AFFBB9C8346BF56FCC28F57FCC5A9E8F7DDC67B20FAD6EBCC663418E`;
it was inspected, not integrated or used to claim application behavior.

## Requirement matrix

Cells use SUPPORTED, PARTIAL, UNSUPPORTED or UNKNOWN. "Fixture" means the
specified controlled test, not production containment or release acceptance.

| Requirement | Existing CefSharp 154 | Published CefSharp 156.0.300 | WebView2 |
| --- | --- | --- | --- |
| Version and updates | PARTIAL: pinned and identified; Aegis must ship updates and current security-fix coverage is unqualified. Next: qualify a current matching release. | PARTIAL: signed, matched 156.0.30/.96 artifacts; later Chromium fixes are not mapped. Next: obtain a matching binding with verified current fixes. | PARTIAL: Evergreen updates independently and Fixed Version is app-managed; update timing and Aegis restart behavior were not observed. Next: test runtime update and restart in a disposable app. |
| Renderer sandbox | UNSUPPORTED: the stock CefSharp package says CEF sandboxing is unsupported; no enforcement evidence. Next: a qualified supported host/binding plus independent renderer access test. | UNSUPPORTED: the signed 156 package carries the same sandbox-support notice. Next: the same qualified-host and independent access test. | PARTIAL: Microsoft documents LowIL and possible AppContainer renderers; Aegis has no integration or independent file-denial evidence. Next: identify the real renderer and test token, access denial and control. |
| Separate profiles, cookies and storage | PARTIAL: separate profile processes, leases and disposable write/read fixtures passed; broad migration and rollback remain unqualified. Next: test disposable upgrades and simultaneous isolated profiles at the final build. | UNKNOWN: no Aegis 156 runtime/profile test has run. Next: test disposable copies and simultaneous profiles after a qualified upgrade. | PARTIAL: Microsoft documents profiles and distinct user-data folders, but Aegis has no integration. Next: test two simultaneous UDFs, cookies, storage, lease and restart. |
| HTTP proxy and HTTPS destination through HTTP CONNECT | PARTIAL: controlled loopback routing passed; required-proxy production startup remains blocked and no external containment is proven. Next: independent two-VM routing and policy evidence. | UNKNOWN: the Aegis fixture has not run with 156. Next: run the same matched-runtime proxy suite. | PARTIAL: documented environment browser arguments can configure a proxy for HTTP(S) requests, but Microsoft describes flags as diagnostic and Aegis has no supported per-profile adapter. Next: establish a supported configuration and test separate UDFs without fallback. |
| HTTPS proxy endpoint | PARTIAL: certificate rejection was observed, but the saved preference builder has no HTTPS proxy mode. Next: qualify a supported configuration and trusted endpoint in a disposable environment. | UNKNOWN: not tested with 156. Next: test a supported HTTPS proxy endpoint with validation enabled. | UNKNOWN: the documented flag does not establish Aegis HTTPS-proxy support. Next: obtain supported API guidance and run a trusted endpoint fixture. |
| SOCKS5 | PARTIAL: unauthenticated forwarding passed a loopback fixture; authenticated SOCKS is rejected and production startup is blocked. Next: test supported combinations and independent containment. | UNKNOWN: no Aegis 156 SOCKS test. Next: run the pinned-version fixture and rejection cases. | UNKNOWN: the documented proxy flag alone does not establish supported SOCKS5 behavior or per-profile isolation. Next: obtain vendor guidance and test an isolated proxy fixture. |
| Proxy authentication and credentials | PARTIAL: DPAPI storage and a scoped experimental HTTP authentication fixture passed; lifecycle and production routing remain unverified. Next: test challenge isolation, revocation and crash output under containment. | UNKNOWN: no 156 authentication lifecycle test. Next: run the same disposable credential-canary tests. | PARTIAL: Microsoft documents an authentication event and proxy URI, but Aegis has no implementation or credential-isolation evidence. Next: test proxy versus origin challenges and leak scans. |
| WebRTC, DNS, QUIC and UDP | PARTIAL: the existing WebRTC preference is requested/read back; resolver behavior and enforcement are unverified. Next: independent DNS and UDP/STUN controls with OS filtering records on two authorized VMs. | UNKNOWN: no 156 Aegis behavior or containment test. Next: repeat those controls after a qualified integration. | UNKNOWN: Edge browser policy is not automatically a WebView2 app control; DNS, QUIC and WebRTC behavior have not been measured. Next: obtain supported WebView2 controls and run the same independent tests. |
| Viewport and display resolution | PARTIAL: opt-in viewport bucketing exists but does not reduce exposed screen metrics. Next: compare viewport and screen values across profiles and restarts. | UNKNOWN: no 156 Aegis viewport or display test. Next: run the same controlled geometry and screen-metric fixtures. | UNKNOWN: no Aegis implementation or display-entropy test. Next: test a native window with controlled screen and viewport measurements. |
| User agent and Client Hints | UNKNOWN: no per-profile coherent identity control is established. Next: compare navigator values, request headers and user-agent data across contexts and restart. | UNKNOWN: no 156 Aegis identity-consistency test. Next: perform the same header and script comparison. | PARTIAL: a documented user-agent setting may clear Client Hints; consistency is untested. Next: compare headers, navigator values, workers and service workers without inconsistent overrides. |
| Locale, language and timezone | UNKNOWN: no supported per-profile consistency mechanism is established. Next: compare request headers, Intl and timezone APIs across profiles and restart. | UNKNOWN: no 156 Aegis locale/timezone test. Next: run the same cross-context consistency test. | UNKNOWN: per-profile Aegis configuration and worker consistency are untested. Next: establish supported APIs and compare headers and Intl values. |
| WebGL, Canvas and remote fonts | PARTIAL: per-browser WebGL and remote-font restrictions exist; worker/OffscreenCanvas coverage is incomplete and no Canvas protection is claimed. Next: test main, frame, popup and worker contexts. | UNKNOWN: no 156 Aegis surface test. Next: run the existing native surface suites and compare worker results. | UNKNOWN: no equivalent Aegis controls or compatibility evidence. Next: identify supported APIs and measure all contexts without injected spoofing. |
| Media-device exposure and permission | PARTIAL: origin-scoped permission handling exists; complete device-entropy reduction is not established. Next: test device enumeration before and after permission across profiles. | UNKNOWN: no 156 permission or device-enumeration test. Next: run the same native permission fixtures. | PARTIAL: WebView2 exposes permission events, but no Aegis implementation or device-enumeration evidence exists. Next: test permission and enumeration in a disposable implementation. |
| Third-party cookies and tracking | PARTIAL: profile settings and resource filtering exist, with documented interception gaps. Next: test worker, cache, WebSocket and restart behavior at the final build. | UNKNOWN: no 156 Aegis cookie/filter parity test. Next: run controlled storage and filtering suites. | PARTIAL: profile tracking-prevention APIs exist, but equivalent Aegis rules and cookie behavior are unverified. Next: compare controlled requests, cookies and restarts. |
| Popups, OAuth, TLS, crash and shutdown | PARTIAL: the 154 baseline passed controlled browser, popup, TLS-rejection and shutdown fixtures; real-provider and full Windows 11 acceptance remain open. Next: run the requested desktop and lifecycle matrix. | UNKNOWN: no Aegis 156 native compatibility run. Next: run the complete existing regression suite and fresh-process cycles. | PARTIAL: WebView2 exposes browser-process and navigation APIs, but no Aegis migration or lifecycle parity has been tested. Next: implement a separate reviewed prototype and run the same fixtures. |
| External containment identity | PARTIAL: traffic can involve the host and CefSharp subprocesses; experimental AppContainer observations do not prove complete child coverage. Next: prove identity before traffic and correlate WFP decisions and receiver evidence. | UNKNOWN: final 156 process identities and coverage have not been observed. Next: repeat independent identity and two-VM flow tests after qualification. | PARTIAL: `msedgewebview2.exe` runs a process collection per UDF; an image-path filter can affect unrelated apps using the shared runtime. WFP supports an AppContainer SID condition, but a unique Aegis/profile identity and complete coverage are unproven. Next: independently inspect all network-capable processes and test a scoped identity on two authorized VMs. |

## Artifact observations

All hashes below are SHA-256. The CEF archive's publisher index supplies the
SHA-1 shown as a distribution check; the archive SHA-256 was computed locally.
NuGet packages were checked against their official catalog SHA-512 and their
repository signatures before their contents were examined.

| Artifact | Version | SHA-256 |
| --- | --- | --- |
| CefSharp.WinForms.NETCore package | 156.0.300 | `C6CF1B92AC26229A506E3E2D1D23F9C78D38DC6845DA807201CDA731360DCC28` |
| CefSharp.Common.NETCore package | 156.0.300 | `92EC26BBC441955CE533DD6005A062BE07229F41B88E89251F1DA09640A3CF52` |
| chromiumembeddedframework.runtime package | 156.0.30 | `099EE672D881BE953B548AB50F4A125A0D0505AF85EFE8E2DC4E49323A1441A6` |
| chromiumembeddedframework.runtime.win-x64 package | 156.0.30 | `BD67FFE9D60051D880204ED83918BCF218ACEB7DE71A48F6B94577EB1EE2D452` |
| Official CEF minimal archive | 156.0.30/.96 | `74FD321975E99703ACD8FC7E3C1B0859292F61B388F4052A0CD7EF1FB3B29F6E` |
| libcef in both archive and signed package | 156.0.30/.96 | `2576F947395F8846616B4F7040B03F057A5E4575996AA0606C229CB63B712FCF` |
| Official CEF credits | 156.0.30/.96 | `13CBDB584882D6F8A356E46DB986F86B660FE1A357E2AB05D4E6A37AEC437E69` |

The official archive matched the CEF index SHA-1
`1fedfc8dd0274caec9183a709ad1ed7856867b91`. The existing 154 libcef in the
baseline output matched its signed package and the existing notice pin:
`E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7`.

## Sources

- [CefSharp 156.0.300 release and CEF/Chromium identity](https://github.com/cefsharp/CefSharp/releases/tag/v156.0.300)
- [NuGet WinForms package and exact Common dependency](https://www.nuget.org/packages/CefSharp.WinForms.NETCore/156.0.300)
- [NuGet Common package and exact CEF runtime dependency](https://www.nuget.org/packages/CefSharp.Common.NETCore/156.0.300)
- [CEF automated builds index](https://cef-builds.spotifycdn.com/index.json)
- [Chrome 156.0.8078.100 security update](https://chromereleases.googleblog.com/2026/11/extended-stable-update-for-desktop_01570615041.html)
- [WebView2 Evergreen and Fixed Version distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version)
- [WebView2 process model and user-data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)
- [WebView2 sandbox and runtime security](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/measures)
- [WebView2 browser flags and their scope](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)
- [WebView2 authentication event](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2basicauthenticationrequestedeventargs)
- [WebView2 user-agent and Client Hints behavior](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2settings)
- [WFP filtering condition identifiers](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-condition-identifiers-)
