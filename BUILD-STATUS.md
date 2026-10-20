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
