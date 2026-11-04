# P3-07 stability and Windows ARM64 acceptance

This is a draft acceptance record, not release certification. The shutdown candidate is already in main through PR #47; the cause of the earlier native window failure remains unproven. Do not weaken native assertions or change close ordering merely to obtain a passing run.

## Local evidence, 2026-11-04

Source base `6f31d48fe346cfd391510f8b0fccb3e661dac268`, Windows build 26300 ARM64, x64 .NET SDK 8.0.427 and `win-x64` CEF 154.0.340 under emulation. Release app, browser harness and filtering harness builds passed with zero warnings and errors. The full core regression suite passed outside the sandbox, including 83 proxy configuration and credential checks and 50 policy/profile checks. A sandboxed attempt failed at Windows credential protection; it is not a passing suite.

Three stress attempts outside the sandbox passed: 15 window cycles and three integration write plus nine read processes. Each process exited zero and reported its required completion and shutdown markers; the runner found no native assertion or fatal log. The synthetic logs remain in the local temporary evidence directory printed by the runner. An earlier sandboxed window run passed five cycles and exited zero but reported unavailable Chromium encryption. An initial runner attempt reached its completion marker but could not capture the exit code and was rejected; process-handle tracking was corrected before the three passing attempts. This is limited emulation evidence, not a root-cause proof, native ARM64 support, or manual release acceptance.

The separate Chromium filtering harness passed 20 checks, followed by 3 settings-write and 4 settings-read checks in fresh processes, with zero exits. The integration runs reported 93 write checks and 10 checks per read process. These automated results do not replace the manual gates below.

## Repeatable native stress

Build `AegisBrowser.BrowserTests` in Release for `win-x64`, then on a disposable Windows test account run:

```powershell
./tests/acceptance/native-shutdown-stress.ps1 -BrowserTestsExe ./AegisBrowser.BrowserTests/bin/Release/net8.0-windows/win-x64/AegisBrowser.BrowserTests.exe -Attempts 3
```

Each attempt uses a fresh synthetic root: five rapid window/last-tab replacement cycles, then a full integration write and three read processes against a separate shared synthetic root. The runner requires the suite and CEF shutdown markers, zero process exit, and no `Check failed:` or `FATAL` in captured output or profile logs. It retains the evidence root and reports its location. Inspect it for sensitive information before sharing; do not point this test at existing profiles. A timeout kills the test process and is a failure, not evidence of clean shutdown. Investigate any remaining child processes on an isolated test machine.

The Windows CI workflow runs three attempts on x64 and uploads synthetic `.log` files. A green run shows only the exercised sequences on that runner; it cannot prove the absence of an intermittent defect. Record the commit, run URL, operating system build, architecture, attempt count, markers, exits, assertion/fatal output and failures. Preserve failed logs and investigate rather than retrying away a failure.

## Architecture and release gates

The application and harnesses explicitly target `win-x64` and CefSharp 154.0.340. An ARM64 Windows run exercises x64 emulation; it is not a native ARM64 build. Do not change the RID to `win-arm64` or claim native ARM64 support without compatible native CEF assets and separate build, packaging and runtime verification. Test on both a clean Windows x64 workstation and Windows 11 ARM64 with x64 emulation, using the same identified release artifact. Record Windows build, processor architecture, artifact hash, runtime prerequisites and whether the full extracted payload is present.

Manual acceptance remains **NOT RUN** until a tester records each applicable result with date, tester, artifact SHA, exact procedure, observed behavior and sanitized evidence. Use only new disposable profiles and synthetic credentials; do not alter existing profiles or credentials. Record PASS, FAIL or BLOCKED and the prerequisite for each block.

| Gate | Required evidence |
| --- | --- |
| Clean installation | Launch the complete x64 portable artifact on each target architecture, verify startup and normal exit; check missing prerequisites without disabling OS or Chromium security. |
| Navigation and tabs | Run the navigation, loading, rapid tab close, last-tab replacement, popup and multi-window cases in [Windows GUI acceptance](windows-acceptance-v0.1.md), including keyboard and display scaling. |
| Shutdown and restart | Close loaded and loading tabs, owned popups and multiple windows repeatedly; record exit codes, shutdown behavior, native errors and orphan processes. Reopen the same disposable profile and verify persistent cookie and localStorage separately. |
| Recovery and isolation | Test renderer failure and restart, duplicate-profile lease rejection, concurrent disposable profiles and isolation; record any hangs, native crash or storage corruption. |
| Security regression | Verify certificate rejection and permission behavior using the documented fixtures; exercise privacy filtering and proxy startup guard. A required-proxy profile must remain blocked before CEF initialization. Do not change the guard or use production credentials. |
| ARM64 compatibility | Repeat all applicable cases under Windows ARM64 x64 emulation and explicitly distinguish results from native ARM64 support. Verify credential protection and storage persistence under the actual interactive account; encryption errors make those cases blocked. |
| Release package | Build, publish, package and verify payload/hash and clean-machine smoke; retain exact CI run and artifact links. |

The detailed manual steps and remaining security/release gates are in [Windows GUI acceptance](windows-acceptance-v0.1.md), [release review](release-review.md), and [proxy configuration](proxy-configuration.md). Production proxy-required startup remains blocked pending the independent containment gates described there.
