# Authorized testing lab (mock-only)

Run the portable Windows executable with one of:

```powershell
.\AegisBrowser.exe --lab=baseline
.\AegisBrowser.exe --lab=strict
.\AegisBrowser.exe --lab=custom
.\AegisBrowser.exe --lab=failure
```

The dashboard is separate from ordinary browsing. No live security provider, credentials or detection-service call is configured or permitted by this implementation. `ISecurityTestAdapter` is an extension boundary for future reviewed work; only `MockSecurityAdapter` is implemented. A future integration requires explicit authorization and official sandbox documentation, endpoint restrictions and tests proving unknown/failure responses cannot grant access.

## Fixtures and isolation

ASP.NET Core Kestrel binds only to **127.0.0.1**, on dynamically allocated HTTP and HTTPS ports. Host headers are restricted to localhost/127.0.0.1, mutation requests with a foreign Origin are rejected, request logging is disabled, and fixture CSP restricts resources to the loopback server. The HTTPS endpoint uses a temporary self-signed certificate: it is deliberately untrusted and never installed in a trust store. This uses the supported [Kestrel endpoint configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0).

Profiles live under `%LOCALAPPDATA%\AegisBrowser\LabProfiles\<profile>`, separate from normal `Profiles\profile-*` data, with the existing exclusive profile lease. Baseline uses Standard filtering; StrictPrivacy uses Strict; CustomRules uses Custom plus the explicit editable rule field (initial `||localhost^`); FailureTesting starts with the unavailable scenario selected. The browser uses its normal Chromium properties. These are storage boundaries, not identity isolation.

The server creates synthetic, random, HttpOnly session cookies and keeps authorization state in memory. There is no password input. Server shutdown invalidates sessions. Dynamic HTTP ports intentionally mean localStorage origins may change between lab launches; persisted Chromium storage is not a promise that a lab session or origin survives a new fixture server. Existing browser integration tests separately verify persistent cookies/localStorage across processes and isolated contexts. Do not enter real credentials or personal data into this lab.

## Run scenarios

Choose a scenario and press **Run local scenario**. Nothing contacts a detection provider. The initial browser page is blank.

- **Navigation:** HTML and JavaScript fixtures; same-origin script and a third-party `localhost` script from the same loopback server. Baseline allows the latter, Strict and the default Custom rule block it. Cookies/localStorage remain subject to normal Chromium behavior.
- **Redirect:** two HTTP 302 hops back to the fixture page.
- **Allow / Challenge / Deny / Unavailable:** JavaScript creates a mock session, asks `/security`, then independently requests `/protected`. Only explicit Allow grants access (200). Challenge and Deny return 403; Unavailable returns 503 for the check and 403 for protected access. Challenges are simulated outcomes and are never solved or bypassed. Changing decisions invalidates previous grants; another successful check is required.
- **Disconnect:** the server aborts the response, producing a native network failure.
- **TLS rejection:** navigation to the self-signed endpoint must fail. No override exists.
- **DNS failure:** explicitly navigates to reserved `aegis-lab-dns.invalid`. This may contact the configured DNS resolver; it does not contact a real detection service. Resolver/proxy behavior can change the exact failure. It runs only when selected and submitted, not by default.

Lab requests outside the fixture endpoints (and the explicitly selected reserved DNS test) are canceled. All lab popups and downloads are blocked. Normal browser popup behavior is unchanged. This is a controlled test harness, not an OS network sandbox: Chromium background services, OS DNS or system proxies are not comprehensively contained. No Chromium sandbox, certificate-validation or web-security protection is disabled.

## Instrumentation and report limits

The dashboard displays profile/filter mode, allowed/blocked counts, network failure counts, configured mock decision and a bounded event table. Authentication, security outcomes and execution start/finish appear as distinct events. Native resource callbacks record navigation/subresource decisions, HTTP codes when available, redirect events, request IDs and elapsed milliseconds; CEF load/certificate errors record enum outcomes. A finished navigation means execution completed, not that access succeeded. Inspect HTTP status and security outcome.

Export writes a versioned JSON report with at most 1,000 events. Only fixed labels, numeric statuses/timings/request IDs and sanitized destinations are serialized. Paths, queries, fragments, userinfo, arbitrary hostnames, headers, cookies, bodies, raw exception strings and custom rule text are excluded. Redirect hops can be followed by sequence/request ID/status; the report deliberately omits full URLs. Unknown error enums are shown as Unknown rather than copied as arbitrary text. Timings are diagnostic durations, not a performance benchmark. Counters describe the retained event window. Chromium's own profile log is separate and is not included in export.

## Automated and manual verification

The existing Windows workflow now runs `LabTests` in the console suite and native lab scenarios in the browser suite before publishing. Tests cover mock decisions and protected-resource enforcement, redaction, bounded history, profile path separation, HTTP redirects, browser third-party filtering, real TLS rejection and dashboard shutdown. Existing suites retain cookie/storage persistence, real profile-context separation, Smart Search, privacy modes, OAuth-style popups, renderer recovery and native teardown tests. No live provider tests run.

Manual acceptance remains **NOT RUN** until explicitly recorded in BUILD-STATUS.md:

1. Launch all four lab profiles and a normal profile; inspect distinct cache roots and profile lease behavior. Confirm ordinary privacy and Smart Search behavior is unchanged.
2. Run every scenario. Verify challenge/deny/unavailable never show protected access 200; changing back to Allow requires another successful check. Check no session survives a server restart as authorized.
3. Confirm baseline versus Strict/Custom third-party results, edit/remove Custom rules, and inspect request/redirect/timing/error rows.
4. Verify TLS rejection, connection closure and DNS-failure diagnostics on the actual Windows network; record proxy/DNS-dependent differences honestly.
5. Export JSON and inspect for absence of cookies, bodies, raw URLs and credentials. Check cancel, unwritable destination, keyboard use and 100/150/200% scaling.
6. Close during requests/initialization, restart profiles, and confirm clean process termination and no native assertion. Use a disposable lab profile for crash testing.

Installation: see INSTALL-WINDOWS.md. Actual CI results and remaining failures: BUILD-STATUS.md. This is not a stable-release certification or a fraud-provider certification.
