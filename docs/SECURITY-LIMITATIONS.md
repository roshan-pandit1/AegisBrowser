# Security and compatibility limitations

## Native sandbox — high-severity residual risk

No application-owned `--no-sandbox` or web-security-disabling flag was found or added. **That does not establish a working Chromium OS sandbox.** Upstream CefSharp's native settings constructor sets `no_sandbox = true`; its public managed settings wrapper does not expose a supported switch that would safely supply missing native sandbox integration. See [upstream native constructor](https://raw.githubusercontent.com/cefsharp/CefSharp/master/CefSharp.Core.Runtime/CefSettingsBase.h) and [managed wrapper](https://raw.githubusercontent.com/cefsharp/CefSharp/master/CefSharp.Core/CefSettingsBase.cs). Those source observations are not inspection of the shipped DLL's effective process tokens. Exact-build native sandbox behavior remains unverified on Windows. Do not describe this application as Chrome-equivalent sandboxed or Safe Browsing protected. No unsupported override or custom native sandbox retrofit was attempted.

The audited application pins CefSharp `154.0.340` consistently. The [official release page](https://github.com/cefsharp/CefSharp/releases/tag/v154.0.340), inspected during the audit, labels that release latest and identifies CEF `154.0.34+g14c5a08+chromium-154.0.8037.98` / Chromium `154.0.8037.98`. Package feed access and runtime compatibility tests were unavailable; no version was upgraded and no complete CVE coverage is asserted. Recheck trusted metadata at release time.

## Filtering coverage

Standard uses a small authored list, not a complete EasyPrivacy distribution. The ABP network subset excludes cosmetic filters, scriptlets, arbitrary regex literals, response rewriting and numerous options. Conflicting/duplicate option groups are now rejected rather than broadened. Domain-anchored IDNs are canonicalized. The regex engine can reject overly complex automata with NotSupportedException; that now marks the rule unsupported instead of escaping the settings callback. [Runtime source](https://source.dot.net/System.Text.RegularExpressions/System/Text/RegularExpressions/Symbolic/SymbolicRegexRunnerFactory.cs.html). Very large generic lists still have compilation/memory costs; no measured performance improvement is claimed.

Exception and blocking scans share a cooperative 25 ms budget, checked between rules. A single regex retains its 50 ms timeout, so the scan deadline is **not a hard wall-clock limit**. Input lengths above 65,536 characters produce a visible allow fallback. Unknown top-level classification and future resource types are flagged separately; type fallback remains `other` for compatibility. Counters describe observed callbacks only and may overlap. They cannot count requests for which CEF never calls the app.

Service workers, cache-satisfied responses, worker requests without a usable browser/frame, WebSockets, WebRTC and Chromium background connections are not comprehensively intercepted by the current browser request handler. No unsupported request-context interception API was invented. Strict mode is not comprehensive network blocking. Main-frame navigation remains allowed; explicit site/domain exceptions and pause restore compatibility without changing TLS or security decisions.

No automatic filter-list updater is enabled. Offline import remains user-controlled. A trusted upstream signing/checksum/rollback contract was not supplied; adding a network updater without it was deferred. User rules are never silently replaced. Imported unsupported entries are reported, not implicitly emulated.

## Profiles, permissions and logs

Profiles isolate stored browser state, not IP/device identity or OS users. The application does not claim that all CEF cookies, cache or localStorage are encrypted. JSON privacy/search preferences are ordinary local files; no application-owned credentials are stored, so no new plaintext credential store or unnecessary DPAPI wrapper was introduced. Use OS access controls/disk encryption and closed-profile backups.

Permission decisions are owned by Chromium's native consent UI and may persist in its profile. Cross-origin/insecure/unsupported requests are denied by application policy. There is no new permission database or dedicated per-origin revocation UI; native grant/deny/revoke/navigation behavior needs Windows testing. Closing and deleting a disposable profile clears its stored data, but is not a substitute for ordinary revocation UX.

The network inspector is origin-only and memory-bounded; user-authored matching rule text may itself include sensitive literals. Application permission diagnostics now reduce rejected URLs to origins. Native `chromium.log` can still contain browsing details and is not guaranteed sanitized. Do not upload raw profiles or native logs. Downloads remain disabled; downloaded executables are never automatically launched.

## Lifecycle, UI and release

Native shutdown ordering is preserved. Faulted close waits can be retried explicitly without disposing the adapter early, and initialization cleanup releases other owners even if one component fails. Windows races, renderer/subprocess exits, accessibility and 100/150/200% DPI remain unverified for these edits. Existing callbacks and new asynchronous settings publication must pass the unchanged native assertion gates.

Portable packaging now rejects dirty source state and profile/debug material, includes a package/file CycloneDX inventory, and checks observed subprocess exits. The inventory is not a full source-component SBOM of every library embedded inside Chromium. ZIP hash verification is not code signing. No new artifact, clean-machine launch, signature, notarization or automatic-update distribution has been verified.

The `-Lab` smoke option uses a fixed baseline lab profile. Run it only on a disposable Windows test account/runner, not a normal browsing account. The main smoke path uses a unique profile. Hosted CI provides limited window automation and is not manual GUI acceptance.

## Optional survey/navigation trace

The new Local trace is separate from native logs and disabled by default. It records bounded sanitized transport observations, not provider verdict internals or authentication tokens. Registered hostnames remain visible; review exports before sharing. Raw console messages and opaque paths are omitted, which limits JavaScript diagnosis. Main-document loading is not proof of survey acceptance. Neither this implementation nor its new local native fixtures have been compiled/executed on Windows in this session. See [diagnostic scope and evidence](survey-security-diagnostics.md).
