# Authorized mock security lab — verified 2026-10-23

[Draft PR #6](https://github.com/roshan-pandit1/AegisBrowser/pull/6), `feature/authorized-testing-lab`, is based on main `98a8835575c22268a456a64a5033b65e9d2c7840` (merged Smart Search). It remains draft and unmerged. The user explicitly confirmed mock-only scope: no real provider, credentials, live adapter or external detection-service calls. `ISecurityTestAdapter` remains an extension interface; only the mock is implemented.

## Actual Windows verification

[Run 39939721741](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39939721741), [job 120367883691](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39939721741/job/120367883691), **SUCCESS** for implementation `2a8476e1fbd4b9f5e1c567f32098c7da71b13982`. Completed logs and artifact metadata were inspected. This status-only commit follows that verified implementation; its own CI result is reported in PR #6.

| Check | Actual result |
| --- | --- |
| Windows restore/build | PASS; windows-latest, .NET 8, win-x64 Release; zero compiler warnings/errors |
| Lab server/model | PASS: 23 checks (loopback, containment, redirects, session separation, allow/challenge/deny/unavailable enforcement, invalid/canceled decisions, redaction, bounds and lab profile paths) |
| Existing suites | PASS: 56 privacy + 49 address/search + 50 policy/profile checks |
| Native browser suite | PASS: 54 checks, including all four lab profiles, baseline versus Strict/Custom third-party resources, mock protected-access decisions, redirect instrumentation, TLS rejection, connection failure and lab shutdown |
| Persistent browser storage | PASS: 8 checks in each of three fresh read processes; existing cookie/localStorage isolation and persistence retained |
| Native shutdown | PASS: completion markers and zero exit in all four browser suite processes; assertion gate retained |
| Chromium filtering / CORS | PASS: 20 checks |
| Settings restart | PASS: 3 write + 4 read checks |
| Self-contained publish | PASS; complete publish directory uploaded |
| Published/extracted application smoke | PASS for both ordinary browser and --lab=baseline, including cache creation and normal close |
| Portable archive | PASS: 852 file hashes verified; portable ZIP and checksum uploaded |

[Portable Windows x64 test artifact](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39939721741/artifacts/12495776550), 274,245,542 bytes. Inner ZIP SHA-256: `e6cd75efc3a927b57aeabcae6729a807a76bf37eecf89a8d0bf5da69ef5862f7`. [Complete publish directory](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39939721741/artifacts/12495581854). The self-contained build includes the ASP.NET Core framework needed by the fixtures; no additional NuGet library was introduced. See [lab instructions](docs/authorized-lab.md) and [installation](docs/INSTALL-WINDOWS.md).

## Failure corrected

[Initial run 39938808440](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39938808440) compiled but failed waiting for the baseline third-party fixture. Diagnostic [run 39939397693](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39939397693) identified `SyntaxError: Unexpected end of input`: the fixture's JavaScript loop lacked its closing brace. The script was corrected. The original assertions and timeouts remain intact; the subsequent verified run passes.

## Boundaries and remaining manual acceptance

No lab GUI acceptance was performed manually by this agent or reported by the user. Previous user-reported Smart Search acceptance below does not cover the lab. Remaining: all scenario controls and profile launch UX; keyboard/accessibility and 100/150/200% scaling; rule editing; JSON save/cancel/unwritable destination; simultaneous lab/normal profiles; close during initialization/traffic; DNS behavior under the user's OS resolver/proxy; and the historical real-site/permission/DevTools/offline diagnostics gates.

TLS and native connection failure were exercised in CI. The explicit reserved .invalid DNS scenario is implemented but not asserted in CI; resolver/proxy outcomes vary and are not a claimed pass. Default fixtures use loopback, not a real provider. The lab request allowlist and CSP are not an OS network sandbox. Chromium background traffic and OS DNS are not comprehensively contained. Existing CefSharp 154, certificate validation, permissions, web security and normal privacy/Smart Search behavior remain enabled; no security-bypassing switches were added.

Lab profiles use a separate LabProfiles root. Server authorization is deliberately memory-only and expires when the server stops. Random fixture ports can change localStorage origins between launches; CI's persistent-storage checks use the existing dedicated browser fixtures. Reports exclude raw URLs/paths, credentials, headers, cookies and bodies; they retain bounded typed events, status codes, sanitized destinations, request IDs and timing. No actual provider certification or stable-release certification is claimed.

Local `dotnet --info` fails (SDK not installed on this macOS host); Windows results come from CI. Local `git diff --check` passes. Local working changes are preserved; commits were published through the authenticated GitHub connector.

---

## Previous Smart Search and browser verification (historical)

# PR #5 review and user-reported acceptance — 2026-10-23

PR #5 is prepared for human review against `main`. Main at `023a5709ff3af0dbc3edf319e4ebe8b827d79cf0` contains PR #4's merge and the original privacy base `8a78d77`; retargeting does not require reintroducing those changes. No automatic merge is authorized or performed.

## User-reported manual results

The user reported the following working in manual testing. These are **user-reported PASS**, not tests performed or independently witnessed by this agent and not CI results. The report did not specify the exact binary revision, Windows version, selected providers or display scaling.

| Case | User-reported result |
| --- | --- |
| Single-word search | PASS |
| Direct domain navigation | PASS |
| Search-engine switching | PASS |
| Search settings persist after restart | PASS |
| Per-profile search settings | PASS |

These confirmations supersede the blanket “manual acceptance not run” statement for these five cases only. No failures were reported in these cases. Full release acceptance is still incomplete.

## Automated verification and regression review

Before this documentation update, the latest Windows CI [run 39932369988](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39932369988) passed on `71ead1580710258138eb0d3c5f1d1eaf1b9890dc`. It verified 49 address/search, 56 privacy, 50 policy/profile, 38 browser write checks, 8 storage checks in each of three read processes, 20 filtering checks and 3 write + 4 read settings checks. Restore/build/publish, TLS rejection, CORS, native shutdown, published-app smoke and 712-file portable extraction checks passed. The prior sections preserve the detailed implementation evidence. CI for this subsequent documentation commit is reported on PR #5; this document does not pre-claim its result.

Review of the 12-file feature diff found no blocking regression. The resolver separates URLs from encoded queries, permits only the supported internal addresses and HTTP(S) navigation, and does no network work while typing. Engine choices are fixed HTTPS templates. Search settings use profile-local atomic replacement and preserve unreadable files; the active engine changes only after saving succeeds. MainForm continues to use Chromium Load/Back/Forward and existing lifecycle handlers. No CefSharp version, TLS validation, permission policy, privacy engine, popup handler or native disposal code is changed by this feature. The existing browser tests retain those regression gates. This is a code/CI review, not a claim of complete GUI validation.

## Remaining acceptance

Not confirmed by the user's report: all four real providers individually; multiword/Unicode query UX; explicit URL, localhost and IPv6 GUI behavior; Enter/Ctrl+L with web content focused; multi-tab interaction and Back/Forward UX; keyboard-only settings; 100/150/200% scaling/accessibility; simultaneous profile processes; corrupt/unwritable settings error UX; real OAuth, permission/device prompts, DevTools, offline diagnostics and the other historical release gates. Automated coverage of some of these cases does not establish a manual pass. No stable-release certification is implied.

---

## Earlier implementation verification (historical)

# Smart address bar verification — 2026-10-23

[Draft PR #5](https://github.com/roshan-pandit1/AegisBrowser/pull/5) targets `feature/privacy-filtering` from `feature/smart-address-search`, based on `8a78d77262e9bfb59ebc6191951209dda0accb5a`. GitHub reports PR #4 already merged as of this verification; this task did not merge or modify it. PR #5 remains draft with the requested base branch. CefSharp 154.0.340, .NET 8, privacy filtering and Chromium security settings are unchanged.

## Actual Windows results

[Windows run 39931668616](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39931668616), [job 120341176849](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39931668616/job/120341176849), **SUCCESS** for implementation `f6204a3ade0c96a993ac7b7a028a7b1263480f2a`. Completed logs and artifact metadata were inspected. This subsequent status commit documents that tested revision.

| Check | Actual result |
| --- | --- |
| Workflow | windows-latest; setup-dotnet installs .NET 8; global.json pins 8.0.4xx |
| Restore / Release win-x64 application and browser harness builds | PASS; zero compiler warnings/errors |
| Address/search resolution and settings | PASS: 49 checks |
| Existing privacy / policy/profile suites | PASS: 56 + 50 checks |
| Native browser write suite | PASS: 38 checks, including submitted searches, no search while typing, Unicode query decoding, engine changes, current-tab navigation/history and existing popup/renderer/shutdown cases |
| Persistent browser storage | PASS: 8 checks in each of three fresh read processes |
| Native shutdown | PASS: all four browser processes report CEF shutdown; zero exits; native assertion gate enabled |
| Chromium filtering / CORS | PASS: 20 checks |
| Settings process restart | PASS: 3 write + 4 read checks, including search selection through production settings UI |
| TLS | PASS: untrusted certificate rejected with CertAuthorityInvalid |
| Self-contained publish, payload and published-app smoke | PASS |
| Portable archive | PASS: 712 file hashes verified, extracted executable startup/normal-close smoke passed |
| Publish directory and portable ZIP uploads | PASS |

[Portable Windows x64 test artifact](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39931668616/artifacts/12491409149) (262,467,120 bytes) includes the inner ZIP, checksum, installation instructions and SEARCH.md. Inner ZIP SHA-256: `895c4f9311609189bf1120ec246d34c4b6d56267c2db47a9453b3862e5c6e661`. [Complete publish directory](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39931668616/artifacts/12492570314). See [installation](docs/INSTALL-WINDOWS.md) and [search behavior/settings](docs/search.md).

## Defect corrected and limits

Initial [run 39716359894](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39716359894) failed `navigate [::1]:8080`: .NET's IPAddress parser accepted the bracketed authority, which the bare-IPv6 path incorrectly wrapped again. The resolver now reserves that path for unbracketed literals, with explicit bracketed loopback and non-loopback port regressions. No timeout or assertion was relaxed. The subsequent Windows run above passes all suites.

Manual GUI acceptance is **NOT RUN**. Remaining cases: all four real search providers; Enter/Ctrl+L and keyboard-only settings with web content focused; multi-tab interaction; 100/150/200% display scaling; simultaneous named profiles; unwritable/corrupt settings UX; and all previous manual release gates below. Automated searches use local HTTPS fixtures, not real-provider availability or authentication tests. No CAPTCHA, identity, TLS or anti-fraud circumvention is added. This is a test candidate, not a stable-release certification.

Local `dotnet --info` fails with command not found on this macOS ARM64 environment; executable results above are from Windows CI. Local `git diff --check` passes. Local working files are retained; remote commits were published through the authenticated GitHub connector because local Git metadata is restricted.

---

## Previous Windows release review (historical)

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
