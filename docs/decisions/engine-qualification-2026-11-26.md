# Engine qualification: 2026-11-26

Decision status: **BLOCKED**. [OFFICIAL-UPSTREAM] [SOURCE-VERIFIED]
The current application remains a labelled **DEVELOPMENT PREVIEW** using its existing
CefSharp 154 integration. No production engine is selected or qualified by
this assessment. Do not migrate or replace individual binaries: any engine
migration requires a committed decision record, human approval, verified inputs
and the existing regression gates. Release A and B remain NO_GO.

Metadata was checked on 2026-11-26, 08:35–08:40 UTC. [OFFICIAL-UPSTREAM]
Upstream metadata changes; query again before approving an upgrade. Runtime
behavior and artifact authenticity are separate from a version listing.

## Upstream identities and measured dates

| Source and channel | Identity | Published date / observed metadata |
| --- | --- | --- |
| [Chrome desktop stable](https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop_01734282054.html) | Windows 158.0.8127.88/.89 | 2026-11-23; rolling deployment. |
| [Chrome desktop early stable](https://chromereleases.googleblog.com/search/label/Desktop%20Update) | Windows 159.0.8154.33/.34 | 2026-11-24; small early rollout, not the ordinary stable release. |
| [Chrome extended stable](https://chromereleases.googleblog.com/2026/11/) | Windows 156.0.8078.114 | 2026-11-23. The archive lists a critical fix; the individual article presented an older cached version, so recheck its final contents. |
| [CEF windows64 build index](https://cef-builds.spotifycdn.com/index.json) | Stable 158.0.6+g23f1736+chromium-158.0.8127.89 | Minimal archive `cef_binary_158.0.6+g23f1736+chromium-158.0.8127.89_windows64_minimal.tar.bz2`, index `last_modified=2026-11-25T06:13:22.503Z` and SHA-1 `2abb78fcd3b07ea0fb2014e3867a0902f19f1731`. This is index metadata, not artifact-signature or SHA-256 verification. |
| [CEF windows64 build index](https://cef-builds.spotifycdn.com/index.json) | Stable 156.0.32+g3d1f4ab+chromium-156.0.8078.109 | Index metadata `last_modified=2026-11-24T05:14:34.521Z`. It does not match CefSharp 156.0.300's exact dependency. |
| [NuGet CefSharp.WinForms.NETCore](https://www.nuget.org/packages/CefSharp.WinForms.NETCore) and [release](https://github.com/cefsharp/CefSharp/releases/tag/v156.0.300) | 156.0.300, with CEF 156.0.30+g6028375+chromium-156.0.8078.96 | 2026-11-17. The [Common dependency](https://www.nuget.org/packages/CefSharp.Common.NETCore/156.0.300) pins CEF runtime 156.0.30. NuGet's win-x64 runtime index also ended at 156.0.30 when queried. |
| [Microsoft WebView2 runtime notes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/runtime/) | Runtime 158.0.4370.33; preview 159.0.4402.13 | Both listed 2026-11-21. The notes explicitly say to check the download page for the latest runtime. [UNVERIFIED] No current download-feed identity or package hash was established here. |
| [Microsoft Edge Stable notes](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-relnote-stable-channel) | Edge 158.0.4370.45 | 2026-11-25. This is Edge browser metadata, not proof that a WebView2 .45 runtime exists. |

Reproduce metadata-only queries in a network-permitted shell:
```powershell
$cef = Invoke-RestMethod https://cef-builds.spotifycdn.com/index.json
$cef.windows64.versions | Select-Object -First 10 cef_version,chromium_version,channel
(Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/cefsharp.winforms.netcore/index.json).versions
(Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/chromiumembeddedframework.runtime.win-x64/index.json).versions
```

The CEF 158 build's Chromium version matches Google's Windows stable version,
but **version equality does not prove the artifact's patch set, signature,
source provenance, Aegis ABI, sandbox initialization or security behavior**.
The CEF 158 archive was not downloaded or executed here. [UNVERIFIED]
A reproducible qualification must verify the distribution and source/build
identity, all version-matched modules, and relevant fixes, then test the actual
integrated executable.

The current browser build was documented as CefSharp 154.0.340 / CEF 154.0.34 /
Chromium 154.0.8037.98 in the previous [artifact observation](engine-selection.md).
That report's package verification and CI apply to its named build only; this
document does not upgrade them to independent review. [DEVELOPER-VERIFIED]
On 2026-11-26, a read-only local inspection found WebView2 runtime executable
`158.0.4370.33` with a valid Authenticode status and SHA-256
`B9845539AFFBB9C8346BF56FCC28F57FCC5A9E8F7DDC67B20FAD6EBCC663418E`.
It was not launched as an Aegis proof of concept and does not establish the
latest available runtime or its security-fix coverage. [REAL-WINDOWS] [UNVERIFIED]

## Advisory and patch-latency assessment

Google's [2026-11-11 extended-stable 156.0.8078.100 bulletin](https://chromereleases.googleblog.com/2026/11/extended-stable-update-for-desktop_01570615041.html)
reports 72 fixes, including four critical CVEs. The current CefSharp 156
release published on Nov 17 still identifies Chromium 156.0.8078.96. As of
Nov 26 the NuGet index did not list an exact later 156 CEF runtime: **15 calendar
days elapsed since the .100 bulletin without a published matching newer
CefSharp/CEF runtime identified in that index**. This is an observed release
gap, not proof that any particular CVE is exploitable or absent from CEF.
[OFFICIAL-UPSTREAM] [UNVERIFIED]

Google's [Nov 17 158.0.8127.36/.37 bulletin](https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop_01699533288.html)
reports 204 fixes, including critical issues. The [Nov 23 .88/.89 bulletin](https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop_01734282054.html)
reports critical CVE-2026-134401. The CEF 158 minimal archive index timestamp
is two calendar days after that Chrome bulletin, but no individual fix or
executable was verified. [OFFICIAL-UPSTREAM] [UNVERIFIED]

Microsoft documents a [two-week WebView2 Runtime cadence](https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/runtime/),
but an individual security-fix delay and
application restart/update latency were not measured. Evergreen updates
independently; Fixed Version makes the app distributor responsible for
shipping updates. [OFFICIAL-UPSTREAM] [UNVERIFIED]

For the shipped Chromium 154 and candidate Chromium 156 builds, **all
applicability and backport determinations below remain open**. The following
are public advisory identifiers from the named bulletins, not a finding that
each vulnerability exists in Aegis. Restricted issue details, Chrome-specific
code, CEF configuration and possible documented backports must be checked
individually. The 156 bulletin lists these 72 IDs:

CVE-2026-126879, CVE-2026-126885, CVE-2026-126895, CVE-2026-126911, CVE-2026-126840, CVE-2026-126841, CVE-2026-126842, CVE-2026-126843, CVE-2026-126844, CVE-2026-126847, CVE-2026-126848, CVE-2026-126849, CVE-2026-126851, CVE-2026-126852, CVE-2026-126853, CVE-2026-126854, CVE-2026-126856, CVE-2026-126857, CVE-2026-126859, CVE-2026-126860, CVE-2026-126861, CVE-2026-126863, CVE-2026-126864, CVE-2026-126865, CVE-2026-126866, CVE-2026-126868, CVE-2026-126869, CVE-2026-126870, CVE-2026-126871, CVE-2026-126872, CVE-2026-126873, CVE-2026-126875, CVE-2026-126876, CVE-2026-126877, CVE-2026-126878, CVE-2026-126880, CVE-2026-126881, CVE-2026-126883, CVE-2026-126884, CVE-2026-126886, CVE-2026-126887, CVE-2026-126888, CVE-2026-126889, CVE-2026-126890, CVE-2026-126891, CVE-2026-126892, CVE-2026-126893, CVE-2026-126894, CVE-2026-126896, CVE-2026-126897, CVE-2026-126898, CVE-2026-126899, CVE-2026-126900, CVE-2026-126901, CVE-2026-126902, CVE-2026-126904, CVE-2026-126905, CVE-2026-126906, CVE-2026-126907, CVE-2026-126908, CVE-2026-126909, CVE-2026-126910, CVE-2026-126912, CVE-2026-126913, CVE-2026-126914, CVE-2026-126916, CVE-2026-126917, CVE-2026-126918, CVE-2026-126919, CVE-2026-126920, CVE-2026-126921, CVE-2026-126922

The 158 bulletin says 204 fixes; the following 143 IDs were extracted from
the accessible bulletin text. This is **not a complete inventory** of all fixes
since Chromium 154. The Google October/November archive requests also returned
HTTP 429 / an anti-automation response for some individual historical pages.
Next: retrieve every relevant official bulletin and Chromium/CEF patch
mapping through an approved source, classify each fix as present, not
applicable, absent, or still restricted, and record the evidence. [UNVERIFIED]

CVE-2026-130963, CVE-2026-131031, CVE-2026-131060, CVE-2026-131008, CVE-2026-131054, CVE-2026-130975, CVE-2026-130910, CVE-2026-130943, CVE-2026-130994, CVE-2026-130909, CVE-2026-131065, CVE-2026-131100, CVE-2026-130979, CVE-2026-131079, CVE-2026-130940, CVE-2026-130954, CVE-2026-130996, CVE-2026-130970, CVE-2026-131057, CVE-2026-130945, CVE-2026-131084, CVE-2026-131090, CVE-2026-130982, CVE-2026-131021, CVE-2026-131022, CVE-2026-131081, CVE-2026-130961, CVE-2026-130984, CVE-2026-130946, CVE-2026-131045, CVE-2026-131042, CVE-2026-131059, CVE-2026-130934, CVE-2026-130951, CVE-2026-131013, CVE-2026-130941, CVE-2026-131068, CVE-2026-130907, CVE-2026-130914, CVE-2026-131082, CVE-2026-130915, CVE-2026-131044, CVE-2026-131025, CVE-2026-131033, CVE-2026-130977, CVE-2026-130980, CVE-2026-131039, CVE-2026-131050, CVE-2026-131104, CVE-2026-131103, CVE-2026-131093, CVE-2026-130957, CVE-2026-131076, CVE-2026-131083, CVE-2026-131002, CVE-2026-130968, CVE-2026-131007, CVE-2026-131092, CVE-2026-130932, CVE-2026-130944, CVE-2026-131023, CVE-2026-131004, CVE-2026-130926, CVE-2026-131101, CVE-2026-131089, CVE-2026-130952, CVE-2026-131063, CVE-2026-130999, CVE-2026-131080, CVE-2026-131019, CVE-2026-131095, CVE-2026-130987, CVE-2026-130942, CVE-2026-130958, CVE-2026-131020, CVE-2026-131048, CVE-2026-131062, CVE-2026-131006, CVE-2026-130971, CVE-2026-131075, CVE-2026-131069, CVE-2026-130992, CVE-2026-131010, CVE-2026-130986, CVE-2026-130990, CVE-2026-130962, CVE-2026-131000, CVE-2026-131024, CVE-2026-130948, CVE-2026-130923, CVE-2026-131015, CVE-2026-130937, CVE-2026-131053, CVE-2026-130917, CVE-2026-131087, CVE-2026-131061, CVE-2026-130960, CVE-2026-131070, CVE-2026-130936, CVE-2026-131026, CVE-2026-131056, CVE-2026-131043, CVE-2026-130930, CVE-2026-131066, CVE-2026-131028, CVE-2026-131064, CVE-2026-131047, CVE-2026-130976, CVE-2026-131077, CVE-2026-130965, CVE-2026-131086, CVE-2026-130905, CVE-2026-130938, CVE-2026-131036, CVE-2026-131014, CVE-2026-131052, CVE-2026-130985, CVE-2026-130947, CVE-2026-130995, CVE-2026-131091, CVE-2026-130997, CVE-2026-131038, CVE-2026-131096, CVE-2026-130913, CVE-2026-131017, CVE-2026-130959, CVE-2026-130933, CVE-2026-130929, CVE-2026-130956, CVE-2026-131067, CVE-2026-131030, CVE-2026-130991, CVE-2026-131051, CVE-2026-131058, CVE-2026-131032, CVE-2026-131094, CVE-2026-131071, CVE-2026-131046, CVE-2026-130928, CVE-2026-131078, CVE-2026-130924, CVE-2026-130969, CVE-2026-131003

## Mandatory gates precede preferences

A candidate is eligible only if current fix coverage, a real renderer sandbox,
profile isolation, fail-closed proxy containment and provenance are established
for the **actual Aegis build**. Unknown evidence blocks acceptance; no weighted
preference can turn it into a pass.

| Candidate | Fix coverage | Renderer sandbox | Profile / proxy containment | Gate result |
| --- | --- | --- | --- | --- |
| Existing CefSharp 154 | UNKNOWN: later fixes unmapped | UNSUPPORTED by the stock binding | UNKNOWN beyond scoped fixtures | BLOCKED |
| Latest published CefSharp 156 | UNKNOWN: later fixes unmapped | UNSUPPORTED by the stock binding | UNKNOWN for Aegis 156 | BLOCKED |
| WebView2 Evergreen | UNKNOWN for the actually deployed runtime | PARTIAL upstream architecture; no Aegis denial/control | UNKNOWN per-profile containment identity | BLOCKED |
| WebView2 Fixed Version | UNKNOWN until exact package and updates are qualified | PARTIAL upstream architecture; no Aegis denial/control | UNKNOWN per-profile containment identity | BLOCKED |
| Native CEF 158 bootstrap | UNKNOWN beyond reported Chromium identity | PARTIAL: upstream supports bootstrap; Aegis integration and enforcement not tested | UNKNOWN process and containment coverage | BLOCKED |

Next: map fixes to exact source revisions, verify official artifacts and their
hashes and signatures, run a real renderer access-denial test with a positive
control, and complete the isolated two-VM policy/receiver matrix.
[OFFICIAL-UPSTREAM] [SOURCE-VERIFIED] [UNVERIFIED]

## Requirement matrix

Cells are SUPPORTED, PARTIAL, UNSUPPORTED or UNKNOWN. They describe a capability
or observed scope, not an acceptance pass; tests resolving each unknown follow.

| Requirement | CefSharp 154 | CefSharp 156 | WebView2 Evergreen | WebView2 Fixed | Native CEF 158 |
| --- | --- | --- | --- | --- | --- |
| Security fix coverage / latency | PARTIAL | PARTIAL | UNKNOWN | UNKNOWN | PARTIAL |
| Renderer sandbox | UNSUPPORTED | UNSUPPORTED | PARTIAL | PARTIAL | PARTIAL |
| Profile separation | PARTIAL | UNKNOWN | PARTIAL | PARTIAL | UNKNOWN |
| HTTP/SOCKS proxy and auth | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| Cookie and storage isolation | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| Privacy controls / consistency | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| Browser / popup / shutdown lifecycle | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| Windows 11 compatibility | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| Licensing / redistribution | PARTIAL | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN |
| Maintenance burden / update control | PARTIAL | PARTIAL | PARTIAL | PARTIAL | PARTIAL |
| Scoped external network identity | PARTIAL | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |

[OFFICIAL-UPSTREAM] [SOURCE-VERIFIED] [UNVERIFIED] The next discriminating tests
for UNKNOWN cells are, respectively: map advisory commits and runtime updates;
run the renderer identity/token/file-denial control; test simultaneous disposable
profiles and cookies, local/session storage, IndexedDB, Cache Storage, service
workers and leases; test each supported proxy/auth combination plus independent
DNS/receiver evidence; compare privacy values in frames and workers; run
popup/crash/shutdown parity and Windows 11 DPI/lifecycle acceptance; review
exact package terms and notices; and inspect every network-capable process and
scope WFP to an unaffected distinct identity on authorized isolated VMs.

Microsoft documents [one process collection per user-data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model).
Its shared Runtime executable can also serve other applications, so an
executable-path-only WFP rule risks affecting unrelated apps. Windows offers
[application and package filtering conditions](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-condition-identifiers-),
but a unique Aegis/profile identity and coverage have not been demonstrated.
Microsoft's [browser-flag guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)
describes proxy arguments as development flags and warns against shipping
them. A proxy flag is not a verified, supported per-profile containment API.

On Windows, [upstream CEF sandbox setup](https://github.com/chromiumembedded/cef/blob/master/docs/sandbox_setup.md)
provides a bootstrap for current distributions: the same bootstrap executable
launches browser and children, loads the client DLL and supplies the sandbox-info
pointer to both CEF process execution and initialization. Upstream also
describes building the application inside the CEF/Chromium build as an
alternative. This is an upstream architecture description, not proof that the
existing CefSharp executable can adopt either path. An exact-version bootstrap,
wrapper and client ABI, source provenance and full lifecycle require qualification.

## Weighted implementation preference, after mandatory gates

Scores are coarse developer estimates (1 = unfavorable, 5 = favorable), not
measurements or security evidence. Weights: current Aegis API continuity 30%,
profile/proxy adaptation 25%, update ownership 20%, provenance/notice readiness
15%, and integration effort 10%. Because every candidate is BLOCKED above,
**none is eligible for selection**, regardless of its illustrative score.

| Candidate | Continuity | Profile / proxy adaptation | Update ownership | Provenance readiness | Effort | Weighted estimate / 5 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CefSharp 154 | 5 | 3 | 1 | 3 | 5 | 3.40 |
| CefSharp 156 | 4 | 2 | 1 | 2 | 3 | 2.50 |
| WebView2 Evergreen | 1 | 1 | 4 | 2 | 1 | 1.75 |
| WebView2 Fixed | 1 | 1 | 2 | 2 | 1 | 1.35 |
| Native CEF 158 | 1 | 1 | 2 | 2 | 1 | 1.35 |

Rationale: 154 preserves current APIs but is not security-qualified; 156 keeps
the binding family but its changed runtime is untested; WebView2's Evergreen
distribution helps update ownership but proxy and app parity are unknown; Fixed
and native CEF put update, packaging and integration work on Aegis. Re-score
only after actual proof-of-concept data; estimates do not establish readiness.

## Coupling and proof-of-concept preparation

At the source baseline, `rg -l 'using CefSharp|CefSharp\.' AegisBrowser AegisBrowser.Core --glob '*.cs'`
identified ten application files: BrowserView, ChromiumProxyInstaller,
FilteringResourceHandler, FingerprintSurfacePolicy, MainForm, Program,
ProxyAuthenticationSession, SecurityHandlers, ThirdPartyCookiePolicy and
WebRtcPrivacyPolicy. There are 19 matching import/reference lines; a separate
search for selected browser/handler types found 52 matching lines. These are
coupling indicators, not exhaustive call counts. The core project did not
appear in that import search. [SOURCE-VERIFIED]

No speculative engine abstraction or migration is proposed until a candidate
passes its gates. If justified later, preserve tests around narrowly defined
lifecycle, tabs/navigation, profile context, proxy/authentication, permissions,
download policy, request interception and diagnostics boundaries.

**NOT_RUN [NOT_RUN]:** no new WebView2 or CEF 158 proof of concept was executed.
The WebView2 SDK package was absent from the local NuGet cache; the CEF 158
distribution has only index metadata in this assessment, and no isolated,
authorized environment has been established to measure background network
traffic. Next: authenticate exact SDK/runtime or bootstrap artifacts, then use
a disposable, network-isolated environment with loopback fixtures to measure
startup, navigation, concurrent profiles, persistence, proxy configuration,
shutdown, process identity and sandbox restrictions. A successful launch is
supporting evidence only and never proves sandbox enforcement.
