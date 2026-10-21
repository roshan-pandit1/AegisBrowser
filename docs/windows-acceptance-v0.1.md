# Aegis Browser v0.1 — Windows GUI acceptance

Status: manual cases below are **NOT RUN**. CI automation is evidence only for its explicitly listed checks. Record tester, date, Windows version, artifact/run URL, commit SHA, result (PASS/FAIL/BLOCKED), exact observed behavior and screenshot/log references for every case. Never infer GUI success from compilation, a visible top-level window, or the policy unit tests.

## Setup and evidence

Use a standard Windows account, Windows 11 x64, and a supported Windows 10 x64 test image where available. Test at 100% and 150% display scaling. Install the VC++ x64 runtime, extract the **whole** self-contained artifact, and keep its native DLLs/subprocess/resources together. First test on a clean machine without an installed .NET runtime to verify self-contained deployment. Do not add Chromium flags, bypass certificate errors, disable browser/OS security controls, or install test trust roots.

Run two isolated disposable profiles from PowerShell in the extracted directory:

```powershell
.\AegisBrowser.exe --profile=acceptance-a
.\AegisBrowser.exe --profile=acceptance-b
```

Use fresh names if these already contain data. Profiles live under `%LOCALAPPDATA%\AegisBrowser\Profiles\profile-acceptance-a` and `profile-acceptance-b`. Save `chromium.log` when investigating a failure; redact unrelated browsing data. Delete only disposable test profiles after all their processes exit.

For deterministic local navigation/storage/permission requests, serve the repository fixture (requires Python 3 on the test workstation):

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory tests/acceptance
```

Visit `http://localhost:8765/index.html` in both profiles. Confirm the fixture reports `secureContext: true`; loopback is used so permissions can be requested without weakening HTTPS. If false, mark permission cases BLOCKED, not PASS. Use available camera/microphone devices and permit desktop applications in Windows privacy settings so OS denial/missing hardware cannot masquerade as browser denial. Never select or approve a device if an unexpected picker appears; record FAIL and cancel.

## Cases

| ID | Procedure | Required result / evidence |
| --- | --- | --- |
| S01 | Start extracted executable as standard user on clean Windows without .NET installed. | Main window and local diagnostics render without startup dialog; correct profile in title/status. Record screenshot and process exit behavior. |
| S02 | Launch with `--profile=../escape`, then an unknown flag such as `--disable-web-security`. | Clear startup rejection and nonzero exit; no alternate profile created and no browser running with the flag. Dismiss modal error manually. |
| S03 | Close normally with several loaded tabs; reopen same profile. Repeat during page loading. | No crash/hang or orphan browser session preventing restart. Note: unsaved page edits may be lost; session/tab restoration is not implemented. |
| N01 | Load fixture, follow page-two link, use back/forward buttons and Alt+Left/Right while web content has focus. | Correct content, title and address each time; buttons reflect available history. |
| N02 | Enter bare `example.com`, then explicit `https://example.com`. Reload with button and F5; use Escape/Stop while a controlled slow page loads. | HTTPS normalization, successful navigation, reload and responsive stop. Lack of a slow endpoint makes only stop timing BLOCKED. |
| N03 | Type `javascript:alert(1)`, `file:///C:/Windows/win.ini`, `data:text/html,test`, and `https://user:pass@example.com`; click fixture's data link. | No script execution, local file display, credential navigation or unsupported top-level navigation; explanatory status where implemented. |
| N04 | Visit a controlled HTTPS endpoint with an expired/untrusted certificate, without modifying machine trust. | Navigation rejected, certificate error status, no bypass option. Confirm valid HTTPS still works. Network failure alone is not certificate-denial evidence. |
| T01 | Ctrl+T, plus button, load different fixture pages; Ctrl+Tab; switch tabs while one loads. | Independent contents/history; address/title reflect selected tab; inactive-tab events do not replace active address. |
| T02 | Ctrl+W and close button; close last tab; rapidly open/close during loading. | Only selected tab closes, last tab replaced by diagnostics, no disposed-control exception/crash. |
| T03 | Click fixture user popup link; click delayed automatic-popup button and wait >6 seconds without further interaction. | User HTTP link opens a native popup window with its original opener; delayed popup adds no window/tab. Also test user-initiated about:blank followed by a redirect and a callback using postMessage; closing the owner tab must close its popup. |
| P01 | Keep profile A open and launch `--profile=ACCEPTANCE-A`. | Duplicate instance rejects clearly, original still works. Different profile B opens concurrently. |
| P02 | In profile A fixture set marker A; open same URL in another A tab. In B read before saving, then save B. Read A again. | Same-profile tabs share marker A; B initially lacks A cookie/storage; after B save, A remains A and B is B. Record cookie and localStorage separately. |
| P03 | Close both normally, restart each and visit exact same fixture origin/port. | Persistent cookie (`Max-Age=86400`) and localStorage retain each profile's separate marker. This does not test session-cookie persistence. |
| P04 | Force-close only a disposable browser instance via Task Manager; reopen that profile. | OS lease releases after process exit; restart succeeds even with a lock filename present. Crash persistence of the last unflushed write is not guaranteed. |
| Q01 | Click Geolocation on fixture in A, then B. | Native consent prompt identifies the exact requesting origin. Deny/dismiss gives error code 1 and no coordinates; separately test explicit allow on a disposable profile. Cross-origin/insecure requests must reject without a grant. Unsupported/insecure-origin errors are BLOCKED, not proof of denial. |
| Q02 | Click Camera and Microphone separately with devices available; repeat in a new tab and after restart. | Native prompt identifies the origin; deny/dismiss rejects without active tracks. Separately allow on a disposable test profile and verify only requested devices activate. Cross-origin/insecure requests must reject. NotFoundError/OS-only denial is inconclusive. |
| Q03 | Click Screen capture directly (user activation required). | Rejects without granting/capturing a screen. Unexpected picker is FAIL; cancel it. |
| Q04 | Click Notifications; repeat after navigation and in the other profile. | Native prompt identifies the origin. Deny/dismiss does not grant; separately test explicit allow, persistence within the profile and separation from the other profile. Unsupported API is BLOCKED. |
| Q05 | Trigger media/location, immediately close tab or navigate, then open a fresh fixture tab. | No crash, delayed grant, unexpected prompt or capture. No stale grant occurs; any previously explicit Chromium grant remains scoped to its origin/profile. Cross-origin/unsupported requests remain denied. |
| D01 | Click DevTools button and F12 while native web content has focus. Inspect DOM and console, close tools, repeat in another tab. | Tools attach to intended tab and close without closing browser; no remote debugging port configured. |
| F01 | Open diagnostics via startup, button and `aegis:diagnostics`; Refresh report and browser reload. | Local report renders with native UA/languages/screen/timezone/WebGL observations; resize then refresh updates viewport. No values are spoofed. Missing APIs show unavailable markers. |
| F02 | Close other tabs; open diagnostics DevTools Network, disable cache and clear log. Disconnect network, reload diagnostics and refresh report, then launch a fresh process offline. | Report renders offline. Local HTTPS handler document may appear in Network; there must be no external subresource/fetch/WebSocket requests from this page. This does not assert zero Chromium background traffic. |
| F03 | Compare selected report values to direct console reads (`navigator.userAgent`, `navigator.languages`, `devicePixelRatio`, timezone). | Values match native observations. Do not interpret equality across profiles as a failure: profiles separate storage, not fingerprints. |
| U01 | Repeat toolbar, address entry, shortcuts, tab switching and status inspection at 100%/150% scaling and minimum window size. | Controls usable/readable, no clipped critical controls, keyboard focus works. Record any accessibility/layout defects. |

## Automated evidence and limits

`dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64 -p:PlatformTarget=x64` covers address policy, argument rejection, profile paths, file persistence and both same-process and cross-process lease rejection. It does **not** exercise CEF itself. The additional AegisBrowser.BrowserTests Windows suite exercises actual CEF storage, popup messaging, tab lifecycle and renderer recovery; interactive permission decisions still require these manual cases.

`pwsh -File tests/acceptance/windows-smoke.ps1 -PublishDirectory artifacts/win-x64` launches the actual published app on Windows with a unique profile, checks the expected top-level window and cache directory, then requests normal close and checks exit code. CI runs it after publish/payload checks. It does **not** inspect page pixels/DOM or prove Chromium permissions, tab behavior or rendered diagnostics. Failures must be investigated; do not add security-disabling switches to make the smoke test pass. A runner without a usable desktop can block GUI smoke testing and does not invalidate a manual workstation test.

Release acceptance requires all applicable manual cases passing with evidence, plus successful Windows build/tests/publish. Any FAIL is a release blocker pending triage; BLOCKED cases need a named missing prerequisite and remain unverified. Maintain current results and known failures in `BUILD-STATUS.md`.
