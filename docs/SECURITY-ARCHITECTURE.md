# Security architecture map

| Component | Ownership / boundary |
| --- | --- |
| Program | Parse application-only arguments; take profile lease; optionally start loopback mock Lab; initialize CEF once; register diagnostics; run one WinForms root; shut down CEF/Lab/lease independently. |
| BrowserPolicy / AddressInputResolver | Exact supported navigation/internal URLs; smart search only on submission; canonical origin consent gates; profile path validation. |
| MainForm / BrowserView / SafePopups | UI-thread tab ownership; native popup association; retain native owner through close readiness and OnBeforeClose before adapter disposal. No navigation to retiring tabs; explicit timeout retry retains the owner. |
| SafeRequests / FilteringResourceHandler | Main-frame navigation checks, certificate rejection, renderer failure notification and synchronous filter decisions. Browser-specific early-navigation snapshots removed on native close. No response body rewriting. |
| PrivacySession / RequestFilter / SiteClassifier | Immutable policy publication, PSL-backed registrable-site classification, precedence, bounded matching and observation queue; structured partial-coverage flags. |
| PrivacyForm / stores | UI reads edits; background compile then UI-context publication after persistence; atomic profile-local JSON. No remote update service. |
| OriginPermissions / DenyDownloads | Native origin-labelled consent only for supported secure same-origin requests; other permissions deny; all downloads deny. |
| DiagnosticsFactory / diagnostics.html | Exact local main-document request; explicit headers and CSP hashes; static assembly resource; property readings via textContent; no native JS bindings. |
| LabServer / LabSessionRegistry / MockSecurityAdapter | Loopback fixtures; host/origin policy; unpredictable session IDs; server expiry/revocation/versioned mock decisions; no real adapter or credentials. |
| LabReport / inspector | Typed bounded events and reduced URLs; raw native Chromium logs remain outside the sanitizer guarantee. |
| CI / scripts | Windows builds and native suites; NuGet advisory gates; static policy checks; package/file inventory; complete publish/portable ZIP and extraction smoke checks. |

The current checkout's earlier multi-scenario Lab and newer PR #7 Mock Allow code in saved deliverables are separate histories. This audit did not pretend to merge or replace them. The UI redesign remains a separate preserved patch. Reconcile only intended changes in a clean integration branch before building a release candidate.
