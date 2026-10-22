# Windows release review — 2026-10-22

**Windows automation passes; manual release acceptance remains incomplete. PR #4 is draft and unmerged.** Architecture remains .NET 8 / WinForms / CefSharp. CefSharp is now pinned to **154.0.340** (Chromium 154.0.8037.98) in the app and both browser harnesses.

## Verified Windows result

[Windows run 39709234492](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39709234492), [job 119596331252](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39709234492/job/119596331252), completed successfully for implementation `487e1419cdafd5bdf9e2d13bc3e916726bbef9e8`. Completed logs and artifact metadata were inspected. This status update changes documentation after that verified implementation.

| Check | Actual result |
| --- | --- |
| Runner / SDK | windows-latest, Windows x64, .NET 8.0.426 |
| Restore / Release win-x64 app and harness builds | PASS; zero warnings and errors |
| Privacy / policy and profile checks | PASS: 56 + 50 |
| Browser integration write process | PASS: 31 checks, including popup ownership, tab/loading shutdown, renderer recovery, native close acknowledgement, sibling survival and TLS rejection (`CertAuthorityInvalid`) |
| Persistent cookie/localStorage reads | PASS: 8 checks in each of three fresh processes using the same saved profiles |
| Clean process termination | PASS: CEF-shutdown marker and zero exit in all four integration processes; native assertion gate remained enabled |
| Chromium privacy filtering / settings | PASS: 20 checks, including all modes, import/rules/exceptions/pause and enforced CORS |
| Settings process restart | PASS: 2 write + 3 read checks |
| Self-contained publish and app smoke | PASS |
| Portable ZIP round trip | PASS: 711 file hashes verified; extracted application startup/normal-close smoke passed |
| Publish directory and portable artifact uploads | PASS |

[Portable Windows x64 candidate](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39709234492/artifacts/12396835295), artifact ID `12396835295`, 262,457,683 bytes. The artifact contains the application ZIP and its checksum file. **Inner portable ZIP SHA-256:** `99265af54af447c70e6bd6b32b10c0e7b653e9687fa887e4b42449e299fc8af1`. GitHub's outer artifact digest is different: `3116c15622e4aea01cb5e900a1e92d1d9178b4f344d4875eb531792de4ee1892`.

[Complete publish directory](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39709234492/artifacts/12396033393), artifact ID `12396033393`, 261,647,790 bytes. See [installation instructions](docs/INSTALL-WINDOWS.md).

## Failures investigated and corrections

1. [Run 39616815340](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39616815340) failed with Aura assertions and unexpected `WM_CLOSE` of the storage host. `DoClose` returns true for embedded browsers, leaving host teardown to the app. Disposing the adapter at this point queues another native close while handlers are released. `BrowserView` now releases its WinForms host after close readiness, waits for `OnBeforeClose` to finish, then permits adapter disposal. Readiness also waits for owned popups. Repeated release calls share one task. Tests explicitly prove sibling browsers and their storage remain usable.
2. [Run 39619861386](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39619861386) reached the previously blocked TLS test, which failed with `ConnectionClosed`. The fixture's ephemeral key was unsuitable for .NET 8 Schannel. It now imports a temporary user key container without adding certificate trust. Certificate-specific rejection is still required; transport failure is not accepted.
3. [Run 39620364932](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39620364932) completed write and read checks but retained an intermittent Aura assertion. Direct native destruction and hide-before-destroy experiments on CEF 151 also failed, including [run 39708655361](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39708655361). The assertion maps to `Window::GetToplevelWindow()` after destruction in bundled Chromium 151.0.7922.174, including CEF-managed popup closure. Those Win32 workarounds were removed. Upgrading all harnesses and the app to stable CefSharp 154.0.340 cleared the remaining native failure with the same assertion gate and expanded tests. No individual upstream fixing commit is claimed.
4. Inspector selection now follows the request observation ID across refresh; its Windows regression passes. No timeout increases, assertion exclusions, security switches or runtime-style changes were used to obtain the passing run.

## Remaining release gates

All human-operated cases in [release review](docs/release-review.md) and [Windows acceptance plan](docs/windows-acceptance-v0.1.md) remain **NOT RUN**: clean Windows installation, full navigation/keyboard/stop UX, tab/window close races, real OAuth, origin permission consent and device handling, DevTools, cold offline diagnostics, sustained-traffic inspector actions, large imports, settings warning/upgrade/crash recovery, scaling and accessibility. Automated CEF coverage above is partial and is not a manual GUI acceptance pass.

TLS rejection and Chromium web-security protections remain enabled; no sandbox-disabling switches, identity spoofing or security-check circumvention was added. Local execution remains unavailable on this macOS host without dotnet; all Windows results above come from CI.

---

## Historical privacy-only verification (superseded by current review)

# Privacy filtering verification — 2026-10-21

[PR #4](https://github.com/roshan-pandit1/AegisBrowser/pull/4), branch `feature/privacy-filtering`, implements the four privacy phases on the existing .NET 8 / WinForms / CefSharp 151.3.240 architecture. It is based on `main` at `ee2f01a` and does not include or overwrite the separate unfinished browser-compatibility work in PR #3 or its local working files.

## Verified Windows result

[Windows run 39532140250](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39532140250), [job 119000279921](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39532140250/job/119000279921), passed for implementation commit `02311a01636032b0826e40a2bb3b573ee9b3dbf1`. Completed job logs and the artifact listing were inspected. This status update changes documentation only after that verified implementation.

| Check | Actual result |
| --- | --- |
| Runner and SDK | `windows-latest`, Windows x64, .NET SDK 8.0.426 selected by global.json |
| Restore and Release win-x64 app build | PASS; zero warnings/errors |
| Privacy policy and persistence tests | PASS: 56 checks |
| Existing policy/profile tests | PASS: 40 checks |
| Chromium filtering harness build | PASS; zero warnings/errors |
| Real Chromium filtering tests | PASS: 13 checks, explicit suite completion and zero exit required |
| Self-contained win-x64 publish and payload checks | PASS |
| Published-app startup/cache/normal-close smoke | PASS |
| Complete publish directory upload | PASS: 259,044,111 bytes |

[Download AegisBrowser-win-x64](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39532140250/artifacts/12316165284). Artifact ID `12316165284`; SHA-256 `977a9f225759bbd9649ab33046b0ceb0a3649161a8e37c30e888f4d3c9423af0`.

The Chromium harness used the production request handler and a loopback server on Windows. Server hit counts confirmed allowed first-party/unlisted resources, cancellation of direct/redirect/iframe blocked resources, Strict third-party blocking, site and resource exceptions, pause/resume, and preserved top-level navigation. It also opened the native Privacy form. This is **not** a full interactive GUI or real-world OAuth acceptance pass.

## Sequential phase verification and fixes

- Phase 1: [run 39529897738](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39529897738), 20 privacy checks plus 40 existing checks, Windows build/publish/smoke passed.
- Phase 2: [run 39530635531](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39530635531), 32 privacy checks plus 40 existing checks passed before UI work.
- Phase 3: [run 39531044621](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39531044621), 39 privacy checks plus 40 existing checks, Windows build/publish/smoke passed before persistence/reliability work.
- Phase 4 first live failure: [run 39531526664](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39531526664). `/blocked.png` was incorrectly rejected as a regex literal. Corrected path-pattern parsing and added a regression check. Publish/upload were skipped.
- Subsequent live failure: [run 39531685073](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39531685073), confirmed by fixture diagnostics in [run 39531914737](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39531914737). During navigation replacement, early subresources received an empty CEF main-frame URL and Strict mode failed open as unknown. The request handler now retains the accepted navigation URL as a fallback for that gap. Redirect targets are re-evaluated by CEF's resource callbacks. The final live tests passed. The harness's generated ApplicationConfiguration name warning was also removed.
- Local restore, build, test and publish attempts failed with exit 127 (`dotnet: command not found`) on macOS ARM64. Windows verification above comes from CI, not local execution.

## Remaining acceptance and limitations

See [privacy documentation and manual cases](docs/privacy.md). Interactive rule editing/import/removal, inspector selection and counts under sustained traffic, settings warnings/restart UX, real-site authentication, cross-origin nested frames, service-worker/cache behavior, rapid loading-tab closure, and display scaling remain **NOT RUN manually**. Native Privacy-window opening was automated; its complete interactive flow was not.

The filter-list parser supports a documented ABP network subset and reports unsupported rules. The starter list is small, not all of EasyPrivacy. Generic matching has a time budget and fails open with an inspector reason if exceeded. Unknown document sites and traffic outside CEF resource callbacks are not a complete firewall. Strict can break third-party content; users can add explicit exceptions. Imported lists are local snapshots without automatic refresh. Existing popup/OAuth limitations on main remain separate PR #3 work.

Settings persist per profile with atomic replacement; malformed files are preserved and reported. Temporary pause and the bounded, origin-only inspector history never persist. No identity spoofing, scriptlet injection, CAPTCHA bypass, security-check detection or anti-fraud evasion was added. TLS rejection, Chromium command-line restrictions, sandbox/web-security defaults and permission handlers remain unchanged.

---

# Historical v0.1 acceptance record

# Aegis Browser v0.1 verification — 2026-10-20

## Confirmed Windows automation

[PR #2](https://github.com/roshan-pandit1/AegisBrowser/pull/2) adds the acceptance plan, local fixture, extended policy tests and published-app smoke test. It is based on the build fixes in [PR #1](https://github.com/roshan-pandit1/AegisBrowser/pull/1); neither PR is merged by this task.

[Windows CI run 39397188354](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39397188354), [job 118542708820](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39397188354/job/118542708820), completed successfully for source head `5cf28fa82c3e12f3c011972d96fbcd0a3c603c33`. Completed job logs and the artifact API were inspected.

| Check | Actual result |
| --- | --- |
| Runner / selected SDK | Windows Server 2025 x64 (`windows-latest`), .NET SDK 8.0.426 |
| Application restore and Release build | PASS, zero compiler warnings/errors |
| Automated policy/profile checks | PASS, 40 checks including cross-process exclusive lease rejection |
| Self-contained win-x64 publish | PASS |
| Payload validation | PASS: executable, CEF subprocess, libcef and coreclr present |
| Published-app Windows smoke test | PASS: expected main window, persistent cache creation, remained alive during initial startup, normal close within timeout and exit code zero |
| Publish directory artifact upload | PASS, 258,924,406 bytes |
| Fixture JavaScript syntax | PASS locally using `node --check`; no browser fixture interaction was performed locally |

[Download artifact AegisBrowser-win-x64](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39397188354/artifacts/12252486465), ID `12252486465`, SHA-256 `391c9f9d373ac80bffab4a8a9e5c6ad6b7d896669c0fa0caf2a6e844414e0ace`. Artifact expiration reported by GitHub: 2027-01-18.

The smoke test ran on an actual Windows CI runner. It does **not** inspect rendered content or exercise navigation, tabs, cookies, CEF permission requests, DevTools or offline diagnostics. The policy tests check helpers and filesystem behavior, not Chromium cookie separation or permissions. No full GUI acceptance pass is claimed.

## Remaining manual acceptance — NOT RUN

Follow [the v0.1 Windows GUI acceptance plan](docs/windows-acceptance-v0.1.md) and its exact steps/expected results. Serve `tests/acceptance/` on loopback for reproducible navigation, persistent cookies/storage and permission requests.

| Cases | Remaining verification | Status |
| --- | --- | --- |
| S01–S03 | Rendered startup on clean Windows without .NET, invalid-argument dialogs, multi-tab/loading shutdown | NOT RUN; automated window/close check is only partial coverage |
| N01–N04 | History/buttons/keyboard navigation, reload/stop, unsupported URLs and invalid-certificate rejection | NOT RUN |
| T01–T03 | Independent tab contents/history, rapid close/switch, last-tab replacement, user/automatic popup behavior | NOT RUN |
| P01–P04 | Duplicate browser instance rejection, actual cookie/localStorage separation and persistence, crash recovery | NOT RUN; file-lock helper behavior was automated |
| Q01–Q05 | Location, camera, microphone, screen capture, notification denial and navigation/closure races | NOT RUN; hardware/OS denial must not be mistaken for browser denial |
| D01 | DevTools button/F12 with content focus and correct tab attachment | NOT RUN |
| F01–F03 | Rendered fingerprint observations, refresh/reload, cold offline startup, no external diagnostics requests | NOT RUN |
| U01 | 100%/150% display scaling, minimum-size layout and keyboard usability | NOT RUN |

No interactive Windows workstation was used in this session. No manual GUI failures have been observed because these cases have not been executed; absence of failures is not a pass. Record tester/date/OS/source artifact, actual observations, screenshots/logs and PASS/FAIL/BLOCKED per case. BLOCKED cases remain unverified. Full release acceptance is incomplete.

## Failures and known limitations

- Original Windows run [39394651876](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39394651876) failed CS0117 (`Cef.RegisterSchemeHandlerFactory`) with a ResourceHandler obsolete-overload warning. Fixed through the global request context and named MIME argument in PR #1. Subsequent CI builds passed.
- Original runner selected .NET 10 despite installation of .NET 8. `global.json` now selects 8.0.4xx; current logs confirm 8.0.426.
- Earlier GitHub HTTP 403 write failures are resolved. The source and acceptance PRs were successfully pushed.
- No failures occurred in the new automated acceptance checks. CI cannot establish device availability, rendering correctness or human interaction quality from this smoke test.
- Designed limitations: deny-all permissions/downloads, unsupported opener-dependent blank popups, no tab-session restoration, no identity/IP isolation and no automatic Chromium updater.
- Production browser code and Chromium security protections were unchanged by this acceptance work. No certificate, sandbox or web-security bypasses were introduced.
- Local host remains macOS ARM64 without dotnet; earlier local restore/build/test/publish commands failed with exit 127. Windows results above come from CI, not local execution.
