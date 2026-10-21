# Windows release review — 2026-10-21

**Release blocked; PR #4 remains draft and unmerged.** The review branch now includes main at `90a4f69` and the privacy work, retaining .NET 8 / WinForms / CefSharp 151.3.240. Historical green runs below do not certify this combined release candidate.

## Latest completed verification

[Windows run 39570856734](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39570856734) for `76fd0a3383663c45434687370b78a56204bcd144` **failed**. Logs were inspected:

- Application restore and win-x64 Release build passed; Windows SDK selected .NET 8.0.426.
- 56 privacy and 50 policy/profile checks passed.
- Browser integration passed rendered diagnostics, back/forward navigation, preserving address edits, rapid tab closure, renderer failure/recovery and blank-popup opener checks. It then timed out closing the owner tab's native popup. A native Chromium observer assertion also occurred. The full suite did not complete; cookie restart and TLS coverage are not certified by this run.
- 20 Chromium filtering checks passed: all three modes, redirect/iframe cancellation, explicit exceptions, pause/resume, preserved navigation, import/apply/remove/invalid edits, inspector selection and enforced CORS.
- Browser settings restart checks passed in separate processes (2 write checks, 3 read checks), including normal window shutdown and reset of temporary pause/history.
- Publish and both artifact uploads were skipped because the browser suite failed. No portable ZIP from this source is available or certified.

Earlier review runs exposed a missing test namespace import (fixed), unexpected native closure of the storage fixture host, and native shutdown assertions. These are real verification failures, not passing tests. The workflow now rejects native assertions even when a process returns zero.

The latest lifecycle revision keeps the owner handler alive until popup closure completes. Revision `00210f26f7117554524a056988bef00f2c4c7cb7` is pushed, but GitHub has not created a workflow run for it as of 2026-10-21 14:25 UTC. Verification is pending; no fix success is claimed. Inspector selection retention is fixed and its Windows regression check passes. Packaging and installation instructions are implemented but await a fully passing run; see [installation instructions](docs/INSTALL-WINDOWS.md).

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
