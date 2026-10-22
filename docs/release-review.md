# Windows release review

PR #4 is the review branch. Current main's browser compatibility changes are included for verification; PR #4 must not be merged or advertised as a stable release until release acceptance is complete.

## Inspection scope

Reviewed Program/MainForm initialization and disposal, request and popup handlers, origin permission gates, filter parsing/classification, immutable policy publication, profile-local JSON storage, native Privacy UI, diagnostics CSP, existing unit/browser suites and Windows workflow. .NET 8 and WinForms are retained. CefSharp is upgraded from 151.3.240 to stable 154.0.340 to resolve the remaining native Aura teardown failure. Production has no remote debugging port or security-disabling switches. Certificate errors return rejection, and no web-security/sandbox override was introduced.

## Defects and verification work

- Inspector refresh replaced the data source and reset the current row. A click could therefore act on a different host than the selected request. Selection now follows the stable observation number, clearing it if the request has left the bounded history. A native-control regression test exercises this behavior.
- The current main already protects address-bar edits during loading. Added navigation/history and reload regression coverage while retaining that fix.
- Managed/native close ordering: disposing the CefSharp adapter at `DoClose` queues another close before native destruction, allowing a delayed `WM_CLOSE` to reach the shared host after handlers are cleared. `BrowserView` releases the WinForms host on its owning thread after close readiness, awaits `OnBeforeClose`, and only then allows adapter disposal. Owner readiness waits for native popups; release is idempotent.
- Engine lifecycle: CEF 151 retained `Window::GetToplevelWindow()` assertions after destruction, including native popup closure. Direct Win32 destruction and hiding did not solve it and were removed. Stable CefSharp 154.0.340 passes the unchanged native-assertion gate with the managed ordering fix. No runtime-style switch or individual upstream fixing commit is claimed.
- Regression checks assert native closure before adapter disposal, repeated close/release calls, sibling renderer/storage survival, owner popup closure, rapid loading tabs, window closure with initializing tabs, separate-process cookie/localStorage persistence, and a marker emitted only after CEF shutdown returns. CI retains assertion and exit-code checks.
- The first Windows run of this sequence passed native lifecycle/storage tests with no Aura assertions, then exposed a separate TLS fixture problem: an ephemeral private key is unsuitable for .NET 8 Schannel. The test imports a temporary user key container without adding certificate trust. It still requires a certificate-specific Chromium error; transport failure is not accepted.
- Added Standard-mode network coverage, list-import/apply/remove and invalid-edit checks, browser settings write/read in separate processes, and a cross-origin response test without CORS authorization.
- Packaging creates a self-contained release-candidate ZIP, source revision record and SHA-256 file. CI extracts it, compares every file hash with the staged payload and smoke-tests the extracted executable.

## Manual release gates

No human-operated Windows desktop was available in this session. Automated native controls and CEF execution are not a substitute for these checks:

| Area | Remaining manual work |
| --- | --- |
| Clean installation | Windows 10 and 11 x64 without development tools; prerequisite installation, unsigned-build handling and standard-user permissions |
| Navigation/tabs | Toolbar and keyboard focus, stop/reload during slow real pages, unsaved forms and beforeunload, many tabs, window close while tabs/popups initialize |
| Privacy UI | Native file picker, large and malformed imports, all add/remove buttons, selected-host actions under sustained traffic, visible counters/filtering, warning dialogs |
| Persistence | Human restart/upgrade flow, malformed/read-only settings warning recovery, abrupt OS/process termination and profile backup/restore |
| Authentication | Real OAuth providers, bank/payment flows and third-party embeds under all modes; explicit exceptions only |
| Permissions | Origin-labelled consent UX, denial/allow/revoke for supported permissions, hardware/OS restrictions, unsupported permissions and screen capture remain denied |
| DevTools/diagnostics | F12 with content focus, correct tab attachment, cold offline diagnostics, no outbound diagnostics traffic |
| Accessibility/display | 100/150/200% scaling, small displays, keyboard-only interaction, screen reader labels and contrast |
| Network coverage | Cross-origin nested frames, service workers/cache/WebSockets and large imported-list coverage; documented limitations still apply |

The parser supports a documented network-rule subset. Its small starter list, fail-open budget/unknown-context behavior and lack of automatic engine/list updates remain product limitations. The unsigned portable candidate stores profiles in LocalAppData rather than alongside the executable. No security-check circumvention is a proposed remedy for compatibility failures.
