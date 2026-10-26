# Browser compatibility and security review

Architecture retained: .NET 8, WinForms, CefSharp 151, one persistent global profile per application process. No new identity/fingerprint behavior, external debugging listener, certificate exception or web-security disabling flag.

| Area | Finding and change |
| --- | --- |
| Program.cs | Profile lease, separate root/cache paths, disabled application-to-Chromium command-line forwarding, request-context scheme registration and UI-thread shutdown retained. Request-context/window disposal precedes Cef.Shutdown. |
| MainForm.cs | Asynchronous callbacks could overwrite an address being typed, errors were transient and renderer termination was invisible. Preserve address focus, store per-tab messages, report HTTP/network/renderer failures and provide normal reload recovery. |
| Popups | Canceling and recreating a popup as a new browser loses opener, POST data and window context. User-initiated web/blank windows now use native CEF creation. CEF retains redirect, opener, postMessage, SOP and COOP semantics. Track popup lifetimes and close them when their owner tab is disposed. Unsolicited/unsupported popups remain blocked. |
| Permissions | Blanket denial prevented legitimate sites. Supported requests delegate to native Chromium consent only for matching secure origins (HTTPS or loopback). No application-generated grants or permission cache. Chromium owns prompt identity, lifecycle and origin/profile-scoped persistence. Cross-origin, opaque, insecure, diagnostics, unknown permissions and screen capture remain denied. |
| BrowserPolicy.cs | Added parsed-origin comparisons, default-port normalization and safe origin-only diagnostic display. Host suffixes, other ports and embedded credentials are not accepted as same origin. |
| diagnostics.html | Retained embedded local resource, restrictive CSP, textContent output and native observations. Updated policy text; no network APIs or fingerprint overrides added. |
| Tests / CI | Prior tests exercised helpers and a visible window, not CEF storage or renderers. Added a Windows WinForms integration executable with production handlers, separate persistent contexts, process restart phases, OAuth-style local provider redirect/postMessage, rapid tab disposal and controlled renderer failure/recovery. |

## Security boundaries and limitations

- Certificate errors still return false; there is no bypass button. No changes to CORS, sandbox, site isolation, user-agent or challenge handling.
- Native popup windows are deliberate for OAuth compatibility; they are not tab clones. Cross-origin opener access remains constrained by Chromium. Some providers intentionally reject embedded browsers and will continue to do so.
- Native consent UI must still be manually checked for correct rendering, grant/deny behavior, prompt cancellation and device access on supported Windows systems. Policy tests do not substitute for a human approving or denying a device prompt.
- Persisted consent is Chromium-managed within the profile, not a new application-side grant store. Deleting a closed disposable profile resets its consent and browsing data; an application-level per-origin settings editor is not implemented.
- Screen capture stays blocked because a source-selection flow has not been implemented. Do not grant screen capture flags without an explicit picker.
- Crash recovery is a user reload, not transparent replay of POST requests or automatic resubmission. Same-origin tabs may share a renderer; the integration test allows them to reload after the same crash.
- Integration fixtures are served in memory by CEF scheme handlers. They validate actual Chromium storage and browsing-context semantics but do not validate real provider credentials, TLS transport, or every OAuth flow.
- Test-only CDP calls simulate activation and crash a renderer inside the test process. They are not exposed by the shipped application, and do not disable security checks.

API behavior was checked against the pinned upstream [life-span handler](https://github.com/cefsharp/CefSharp/blob/v151.3.240/CefSharp/Handler/LifeSpanHandler.cs), [permission handler](https://github.com/cefsharp/CefSharp/blob/v151.3.240/CefSharp/Handler/PermissionHandler.cs), and request-context/cookie-manager interfaces. See BUILD-STATUS.md for actual run IDs, failures and manual gaps.
