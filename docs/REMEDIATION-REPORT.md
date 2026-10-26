# Security/reliability remediation report — 2026-10-26

## Exact scope and evidence

Local branch: `release/windows-rc`. HEAD: `9fd457276d3e741ade1041ea37655115b7beea4c`. This branch already contained extensive working-tree changes and untracked browser/lab/test files before this audit. The current source uses **AegisBrowser.Lab**, not the newer **AegisBrowser.SecurityLab** from PR #7. The separate redesign and Python project were preserved. No reset, checkout replacement, commit, push, PR-state change or release occurred.

All C# files in AegisBrowser/Core and relevant Lab/test projects, project pins, startup, handlers, forms, persistence, diagnostics, workflows and packaging were inspected. An earlier session recorded a temporary pre-audit baseline, but that directory is no longer present. Therefore an audit-only patch cannot be reconstructed reliably. The recovery deliverable preserves the current browser source snapshot and tracked diff, explicitly including pre-existing user work; do not apply it as an audit-only patch.

`git ls-remote origin ...` failed: `Could not resolve host: github.com`. `dotnet restore AegisBrowser/AegisBrowser.csproj`, `dotnet build AegisBrowser/AegisBrowser.csproj -c Release --no-restore`, and `dotnet run --project AegisBrowser.Tests -c Release` could not start: `dotnet: command not found`. Common SDK paths were absent. PowerShell is also unavailable. The version-specific package index could not be retrieved; the official CefSharp release page and upstream API source were inspected through the available web tool. Historical CI evidence elsewhere in BUILD-STATUS.md does **not** verify this dirty revision.

## Issue inventory

All “implemented” entries below still require C# compilation/Windows execution unless explicitly marked static-tested. Severity reflects this application's threat model; mock-lab issues do not imply real-provider compromise.

| ID / severity | Status, component and evidence / root cause | Remediation, tests and remaining risk |
| --- | --- | --- |
| SEC-01 High | Partially mitigated / native sandbox. No app disabling switch, but upstream native constructor disables CEF sandbox; effective shipped behavior unavailable. | Accurate limitations and source gate; no unsupported native flag change. Windows/native engineering remains open. |
| SEC-02 High | Unverifiable / release baseline. Dirty older checkout, no SDK/remote/Windows. | Baseline preserved; incremental patch and exact evidence. No release-ready claim or old artifact reuse. All C# runtime work blocked. |
| SEC-03 Medium | Confirmed / DiagnosticsFactory accepted all methods, paths and frame contexts under registered host; response headers delegated to a helper. Meta CSP allowed unrestricted inline script/style and cannot enforce frame ancestors. | Main-document GET/root gate, explicit no-CORS response, no-store/nosniff/CORP/XFO, hashed CSP and LF checkout. Pure policy and native cross-origin tests added. CSP hashes/static hooks checked locally; CEF behavior unverified. No demonstrated secret exfiltration is claimed. |
| SEC-04 Medium | Confirmed / NetworkFilters accepted double-negated resource options and contradictory/duplicate groups. IDN host+path patterns were not canonicalized like indexed domains. | Reject ambiguous options; normalize anchored IDN hosts; catch NonBacktracking automaton-size rejection as unsupported syntax. New malformed/IDN regressions; runtime blocked. Supported syntax remains a subset. |
| SEC-05 Medium | Confirmed / PrivacySession exposed only allowed/blocked totals; timeout reason was a string; unknown CEF types silently became other. | Structured coverage flags and aggregate counters, unknown-type mapping, oversized-input fallback, shared scan budget. Tests for unknown site/type, input cap and clear. Native/unobserved paths remain limitations. |
| SEC-06 Medium | Confirmed / SiteClassifier.Host could propagate IDN conversion exceptions from untrusted URL strings. Permission origin comparison lacked canonical IDN equivalence. | Nonthrowing bounded host classification, control/backslash navigation refusal, canonical permission origin. New malformed URL and origin regressions; runtime blocked. |
| SEC-07 Medium | Confirmed / earlier LabServer used cookie MaxAge without server expiry; sessions lived until shutdown and capacity never reclaimed. | Clock-driven bounded registry, server expiry and pruning, session rotation. Synthetic clock/capacity tests added; HTTP tests blocked. |
| SEC-08 Medium | Confirmed / mock decision and version were separately published; dictionary assignment could recreate a session after removal. | Atomic decision snapshots, existing-session-only grant publication, reset endpoint, shutdown closure and version checks. Reset/cancel/stale-state/isolation tests added; runtime blocked. Only mock providers retained. |
| SEC-09 Medium | Confirmed / cleanup exceptions could prevent subsequent owner cleanup; LabDashboard async close exception escaped; BrowserView permanently cached timeout failure. | Independent finally ownership, guarded async close, explicit retry of failed wait without early disposal or longer timeout. Navigation into retiring tabs refused. Existing lifecycle assertions retained plus navigation-during-close assertion. Windows blocked. |
| SEC-10 Medium | Confirmed / privacy list compilation ran synchronously in Apply UI handler. | Background immutable policy preparation; UI-context publication, cancellation before publish, previous policy retained on save failure. Existing sync API/harness preserved. No latency measurements available; benchmark added. Startup parsing/large text rendering still costs time. |
| SEC-11 Medium | Confirmed / no explicit transitive advisory gate or dependency update configuration. | NuGetAudit all; high/critical and unavailable-audit errors; grouped Dependabot and scheduled read-only audit evidence. Python static configuration checks pass. Actual NuGet vulnerability findings and repo alert settings are unverified. No dependency version changed. |
| SEC-12 Medium | Confirmed / packaging relied on source HEAD even if working files differed; no explicit profile/debug payload exclusion or SBOM. | Clean source gate, release debug-symbol exclusion, payload denylist and real-assets/package/file hash inventory. Five Python regression tests pass using labelled fake payloads. Actual Windows payload/SBOM/ZIP not generated. |
| SEC-13 Medium | Partially mitigated / smoke checked parent exit but not observed descendants. | Capture own descendants and require their exit; existing close timeout/assertions unchanged. Windows execution blocked; not an OS-wide orphan guarantee. |
| SEC-14 Low | Confirmed / some symbol buttons had glyph-only accessible names; DPI mode not explicit; toolbar fixed widths cramped address area. | Meaningful names, explicit DPI scaling, smaller non-address widths and scroll fallback. Screen-reader/visual results blocked. |
| SEC-15 Low | Confirmed / rejected permission strings used raw incoming origin text; popup navigation-site snapshots were not removed on close. | Origin-only status text and per-browser snapshot removal. Existing redaction tests retained; runtime blocked. Native logs still need manual sanitization. |
| SEC-16 Medium | Partially mitigated / service workers, cache, WebSocket and contextless traffic are not comprehensively visible. | Explicit limitations and coverage counters; no invented interception or security bypass. Native coverage characterization remains open. |
| SEC-18 Low | Confirmed / local import checked FileInfo length then performed unbounded ReadAllText on the UI thread. | Bounded stream reads, strict UTF-8/BOM handling and content SHA-256, background import with cancellation. Import regressions added; runtime blocked. Digest is not publisher authentication. |
| SEC-17 Medium | Unverifiable / consumer product page source-like rendering with filtering enabled. | No reproducible URL/mode/list supplied, no Windows runtime. No speculative response rewrite, blanket exception or filtering disable was made. |

## Requested areas already correct or intentionally unchanged

- **TLS/web security:** explicit certificate rejection and no app-owned security-disabling switches found. Existing TLS and cross-origin harness assertions retained. This is source inspection, not current runtime proof.
- **Profiles/concurrency:** restricted profile names, Windows reserved-name prefix, independent roots, OS-held file lease, atomic JSON writes and corrupt-file preservation already exist. No profile migration or secret encryption claim was added. Actual crash/cross-process behavior still needs this revision's tests.
- **Permissions/OAuth:** native same-origin consent and user-gesture native popups preserve opener/POST/postMessage. Unsupported capture types deny. No real provider was contacted; native revocation and real OAuth remain manual.
- **Downloads:** disabled intentionally; preserved and a local attachment-denial regression added.
- **Diagnostics:** values use textContent, no privileged bridge or upload; preserved while tightening serving policy.
- **Engine updates:** pins agree. Official v154.0.340 release metadata was available; no newer compatible version was established/tested. Upgrade deferred instead of guessed.
- **Filter updates:** offline import/manual editing retained. Optional network updater deferred without a trusted integrity/rollback contract. No custom rules overwritten.
- **Performance:** hostname index and non-backtracking engine retained. Reproducible indexed/generic benchmark added, but no before/after metrics or speed claims are available.
- **Packaging/signing:** existing payload round-trip/hash and extracted app smoke retained. Signature validation/distribution infrastructure is not implemented or claimed configured.

## Verification executed

- `python3 scripts/security_audit.py`: **PASS — seven static source/configuration gates**. These are pattern/hash/XML checks, not a compiler or native security attestation.
- `python3 -m unittest discover -s scripts -p test_security_audit.py -v`: **PASS — five tests**, including profile/credential/debug exclusion and SBOM tampering detection using explicit synthetic file payloads.
- XML parsing: **PASS — seven project/props files**. `node --check`: **PASS — diagnostics script syntax only**.
- `git diff --check`: **PASS** at the latest local verification; no existing user diff was discarded.
- .NET restore/build/console tests: **BLOCKED before execution**, SDK missing. Browser/filtering suites, NuGet advisory lookup, benchmark and PowerShell packaging: **BLOCKED**, no compatible runtime/tooling/Windows.
- Manual UI acceptance, real OAuth/payment/provider compatibility: **NOT RUN**. No provider endpoints, credentials or security probes used.

See [test matrix](TEST-MATRIX.md), [release checklist](RELEASE-CHECKLIST.md) and [architecture](SECURITY-ARCHITECTURE.md). Current recommendation: **not release-ready; require source reconciliation and exact-revision Windows verification**.

## Survey-result investigation follow-up — 2026-10-26

Confirmed observability gap: ordinary browser requests lacked correlated redirect/status/error diagnostics; the mock lab instrumentation did not cover normal browsing. Added opt-in bounded memory collection and redacted local export through the existing request-handler path, plus local console/native fixtures. The reported Security rejection itself remains unresolved; no faulty filter rule or provider-specific cause was demonstrated. Matching, cookie handling and provider responses were not changed. New C# coverage is unexecuted because the SDK/Windows runtime are absent. Seven existing static gates and five Python tests pass. See [survey investigation](survey-security-diagnostics.md).
