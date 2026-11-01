# Actual CEF AppContainer launch experiment

Research only; results are unverified until the isolated Windows run completes. This experiment does not configure a proxy, add WFP policy, or change the production startup guard.

Run only on a disposable GitHub-hosted Windows runner with explicit -RunOnDisposableRunner. It creates a random per-user AppContainer identity and an owned temporary root, rejects linked paths, copies the built native test fixture, and grants that SID read/execute access to the synthetic root and modify access to its profile subdirectory. No real profile or existing build output ACL is changed. It adds no network capabilities and supplies a minimal environment without the runner's environment or credentials. The temporary identity and files are retained for destruction with the disposable runner; the script deletes no profile or other resources.

The launcher holds the newly created process suspended and verifies TokenIsAppContainer and TokenAppContainerSid against the requested SID using the retained process handle. A deliberate wrong-SID check and an attempted unverified resume must fail. Only a verified process is resumed. Failed verification or a timeout terminates only the owned synthetic test process; no browser belonging to a user is targeted.

The actual CefSharp fixture initializes CEF with a fresh synthetic cache, loads the application-owned diagnostics scheme handler, closes the native browser and shuts down CEF. Stage markers and the synthetic log distinguish initialization, browser readiness, navigation and shutdown. An access failure, unavailable marker, crash or timeout is explicitly inconclusive and cannot be converted into evidence of containment. A package SID ACL can still be blocked by mandatory integrity control; the experiment does not change integrity labels or assume the cause of an access denial.

This is a single-process compatibility experiment. It does not establish child-process token propagation, two-profile isolation, WFP enforcement, DNS or UDP containment, service-crash behavior, ARM64 support, or absence of unrestricted egress. No production proxy activation is authorized by its result.

The runner has no independently enforced per-profile network boundary. The AppContainer requests no network capabilities, but that request alone does not establish an egress guarantee. Only a disposable isolated runner is acceptable. If a fixture is terminated or crashes, Chromium descendants may survive until the runner is discarded; this harness does not claim complete descendant cleanup or terminate unknown processes.

To reproduce, build the Windows x64 browser tests and run on a disposable GitHub-hosted runner:

    ./tests/acceptance/observe-appcontainer-cef.ps1 -BrowserTestsExe ./AegisBrowser.BrowserTests/bin/Release/net8.0-windows/win-x64/AegisBrowser.BrowserTests.exe -RunOnDisposableRunner

No kernel driver, firewall modification, persistent service, loopback exemption or host networking change is installed. Recovery is to discard the disposable runner; do not delete real browser profiles or reset firewall settings.

## Initial Windows x64 observation

In [Windows run 41265475417](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41265475417), commit `540beb61936ecf2c9d0a22c248750a85c3616d78`, the actual suspended CEF test executable had the requested AppContainer SID. Wrong-SID verification and resume without a current successful verification were rejected. After resume, the process initialized CEF, created a browser, loaded the owned diagnostic handler and reached its shutdown marker with exit code 0. This is one controlled compatibility observation, not evidence of child-token coverage, policy enforcement or network isolation. The test requests no network capabilities but does not independently enforce or observe all egress.
