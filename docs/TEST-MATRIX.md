# Audit verification and manual acceptance matrix — 2026-10-26

Status legend: **PASS** executed successfully; **FAIL** executed and failed; **BLOCKED** cannot execute in this environment; **NOT RUN** not attempted. No historic CI run verifies these edits. Windows GUI/native cases are blocked on this Mac; no manual acceptance is requested yet.

## Automated evidence

| Case / suite | Status | Evidence |
| --- | --- | --- |
| Source policy, CSP hashes, dependency pins and audit config | PASS | `python3 scripts/security_audit.py`: 7 static checks |
| Gate/payload/inventory regression tests | PASS | `python3 -m unittest discover -s scripts -p test_security_audit.py -v`: 5 tests, synthetic payloads only |
| Whitespace diff check | PASS | `git diff --check` |
| Restore/build/console C# suites | BLOCKED | `dotnet: command not found` before execution |
| New audit regressions | BLOCKED | Added to existing console runner; diagnostics boundaries, malformed rules/IDN, coverage counters, server expiry/reset/cancellation/capacity tests not executed |
| Existing policy/profile/Smart Search/privacy/Lab suites | BLOCKED | No SDK; no test count claimed |
| TLS rejection, native CORS and forbidden diagnostics access | BLOCKED | Existing native runners extended; Windows required |
| Native downloads | BLOCKED | Added local attachment-denial check |
| Tabs/popups/renderer/startup/shutdown | BLOCKED | Existing native suite retained; retiring-tab navigation check added |
| Persistent profiles and process restart | BLOCKED | Existing write/read/read/read and settings write/read phases retained |
| Native permission grants and revocation | BLOCKED | Windows desktop and test devices needed; policy-level origin regression added |
| Service-worker/cache/contextless coverage characterization | NOT RUN | Known blind spots documented; no comprehensive interception claim |
| Large-rule performance benchmark | BLOCKED | `dotnet run --project AegisBrowser.Tests -c Release -- --benchmark`; no timing results |
| NuGet advisory / scheduled audit / Dependabot operation | BLOCKED | Configuration added; no SDK/feed/remote execution evidence |
| Published payload, real SBOM, ZIP/hash/extraction smoke | BLOCKED | No Windows publish, PowerShell or candidate build; inventory tests used fake files |
| Effective native sandbox / signing verification | BLOCKED | Requires exact shipped Windows binary and infrastructure |

## Human acceptance (pending, not a request to begin now)

| Scenario | Status | Procedure / expected result |
| --- | --- | --- |
| Fresh Windows 10/11 x64 installation | BLOCKED | Disposable standard account; verify VC++ runtime and full self-contained payload; no dev tools assumed. |
| First launch and packaged startup | BLOCKED | Open diagnostics, verify rendered result and no unexpected error; cold offline reload works. |
| Multiple profiles | BLOCKED | Different cookie/localStorage data persists separately; same-profile concurrent process refuses. |
| Multiple tabs / history / navigation | BLOCKED | Back/forward, reload/stop, search provider, typing, keyboard shortcuts; close loading tabs and reject navigation into retiring tabs. No standalone history manager is claimed. |
| Popups / owner closure | BLOCKED | Local user-gesture OAuth fixture preserves opener/postMessage; automatic popups blocked; owner close ends popups. |
| Downloads | BLOCKED | Local attachment is refused and no file is written; no execution offered. |
| Valid HTTPS / invalid certificates | BLOCKED | Valid controlled HTTPS loads; self-signed/expired/mismatched certificate fixtures fail without override. |
| Real OAuth compatibility | NOT RUN | Separately authorized provider test account; consent, redirect and close; respect embedded-browser restrictions. |
| Payment-related compatibility | NOT RUN | Official separately authorized test/sandbox only; no real charges, fraud-signal manipulation or bypass. |
| Media/location/notification permissions | BLOCKED | Same-origin prompt names origin; deny/allow/revoke with test hardware; cross-origin and unsupported capture deny; navigate with pending prompt. |
| Standard/Strict/Custom filtering | BLOCKED | Deterministic fixtures verify precedence, exceptions, pause and counters; unknown/budget labels must not claim total coverage. |
| Filter import/settings persistence | BLOCKED | Supported/unsupported, Unicode and large rules; cancel closing settings during preparation; malformed file remains preserved. |
| Product-page rendering report | NOT RUN | Affected URL/mode/list not supplied; compare authorized reproduction with filtering enabled/paused. No speculative fix claimed. |
| Browser/lab shutdown | BLOCKED | Repeated Close, initializing tabs/popups, pending callbacks; timeout feedback visible and retry retains native ownership. |
| Crash recovery | BLOCKED | Disposable profiles; renderer reload and abrupt process restart; lock releases and profile data remains coherent. |
| Mock lab authorization | BLOCKED | Fresh denial, Allow, other-session denial, reset, expiry and changed decision; no real provider involved. |
| Display scaling | BLOCKED | 100%, 150%, 200%, small/maximized window, readable address area, settings and consent dialogs. |
| Accessibility | BLOCKED | Keyboard-only traversal, screen-reader labels, focus and contrast; preserved existing shortcuts. |
| Report/log handling | BLOCKED | Lab JSON contains typed sanitized data; cancelled/unwritable export handled; do not upload raw Chromium logs. |
| Extracted ZIP / process cleanup | BLOCKED | Compare all extracted hashes; launch/close ordinary and Lab modes on disposable account; no observed descendants remain. |

Record tester, exact binary SHA/source commit, Windows build, scaling, scenario and evidence when a case is eventually executed. Never replace BLOCKED/NOT RUN with PASS merely because code or documentation exists.
