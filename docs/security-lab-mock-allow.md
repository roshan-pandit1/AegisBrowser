# Security Lab milestone 1 — Mock Allow

This is a local simulation, using the `AegisMock` provider. No real detection provider, credential, fingerprint change or external security-service call is configured. The milestone starts from main independently of the broader draft lab PR #6.

## Use the Windows build

Extract the complete portable ZIP, follow INSTALL-WINDOWS.md, launch `AegisBrowser.exe`, and select **Security Lab** on the toolbar. The window shows **Scenario: Security Decisions**, **Mock response: Allow** and **Not Run**.

- **Run Test** asynchronously starts/reuses the server, resets old grants, and creates a new in-memory HTTP cookie container. Status becomes Running, then Passed only when all nine assertions succeed. Failure, cancellation, timeout or an unexpected response produces Failed. The decision and generated request ID are shown.
- **Open Test Page** opens the local fixture using the browser's normal tab and navigation code, with a new independent in-memory CefSharp request context. Its synthetic session is separate from the WinForms runner and normal profile storage. Use the page's buttons to evaluate Allow, request protected content and reset that page's session.
- **Reset Session** revokes all grants held by this lab server, including open fixture tabs, clears the prior result and returns the window to Not Run. It is disabled during a run; closing the window cancels running work and stops the server.
- **Export JSON Report** saves the completed report. It contains scenario, provider, validated decision/request ID, UTC start/end timestamps, individual assertions, overall pass/fail and a fixed error code when needed. No cookies, session identifiers, credentials, URLs or arbitrary provider/exception text are included.

Run Test also verifies reset revocation as its final assertion, so its evaluated session is no longer authorized after completion. Passed describes the assertions performed, not an ongoing grant or a real provider approval.

## Server contract and authorization

ASP.NET Core Kestrel binds only to `127.0.0.1` on port 0 (an available port chosen by the OS). Foreign Host and Origin headers are rejected. Request logging is disabled. Fixture content is deterministic and uses a restrictive CSP; no external resources or network calls are needed.

| Route | Behavior |
| --- | --- |
| `GET /lab/security-decisions` | Accessible simulation page |
| `POST /api/mock-security/evaluate` | Generates a session as needed, evaluates AegisMock, returns provider/scenario/decision/requestId/reason |
| `GET /api/mock-security/protected` | 200 with protected fixture only when this session is authorized; otherwise 403 |
| `POST /api/mock-security/reset` | Revokes this session server-side and deletes its cookie; 204 |

`ISecurityDecisionProvider` separates evaluation from transport. `MockSecurityDecisionProvider` returns `provider=AegisMock`, `scenario=security-decisions`, `decision=allow`, a new GUID request ID and `reason=configured-test-fixture`. The result is deterministic except for request ID. Challenge/Deny can be added later; they are not enabled in this milestone. The server accepts only the complete valid Allow contract. Errors and malformed results revoke the in-flight session grant and return 503.

Session IDs contain 32 cryptographically random bytes. They are stored only in memory and sent in HttpOnly, SameSite=Strict session cookies. HTTP is used only on loopback, so these cookies do not carry the Secure attribute. Authorization is held in the server's session object, never inferred from query strings, JSON supplied by a client or JavaScript. Reset revokes replayed cookies and invalidates an in-flight evaluation. Grants expire after ten minutes; the session store and sanitized evaluation event buffer are bounded. Server shutdown revokes all sessions. No session or authorization is persisted.

Browser lab tabs use [CefSharp's empty CachePath request context](https://cefsharp.github.io/api/95.7.x/html/P_CefSharp_RequestContextSettings_CachePath.htm) for in-memory storage. They retain normal privacy filtering and browser security. Normal tabs continue using the selected browser profile. This is not an OS network sandbox or an identity-isolation feature.

## Verification

The existing Windows workflow runs the new `SecurityDecisionTests` as part of the dependency-free console runner and native lab UI/tab tests as part of `AegisBrowser.BrowserTests`, before publish. Endpoint checks use a real Kestrel loopback listener with an automatically assigned port, exercising actual cookie and HTTP behavior. They cover unique request IDs, forged Allow, reset/replay, unrelated sessions, server errors, invalid responses, cancellation and report redaction. Existing privacy, Smart Search, storage, TLS and shutdown regressions remain in CI.

Local commands on Windows:

```powershell
dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64
dotnet build AegisBrowser.BrowserTests -c Release -r win-x64 -p:SelfContained=true
# Follow the README write/read harness commands with a fresh temporary root.
```

The portable build includes the ASP.NET Core runtime required by the fixture server. No standalone server installation is required. See BUILD-STATUS.md for actual CI results.

## Remaining manual acceptance

Manual UI verification is **NOT RUN** until a user runs this portable build and reports it. Check:

1. Toolbar entry and keyboard accessibility; 100/150/200% scaling and minimum window size.
2. Not Run → Running → Passed, displayed decision/ID, repeated runs with new IDs, and report export/save-error UX.
3. Open Test Page, denial before evaluation, Allow response, protected fixture after evaluation, and Reset Session revocation in the open tab.
4. Close the lab/owner during startup or a running test; verify responsive UI and normal process termination.
5. Normal Smart Search, existing profile data, privacy modes and navigation remain functional with lab tabs open.

Do not treat automation, an Allow simulation or prior Smart Search acceptance as manual acceptance of this milestone.
