# End-to-end interaction audit — not yet complete

The published feature branch remains `07da0a7b4f563bf56bc19c6f22a602c7ec8b6211`, based on main `98a8835575c22268a456a64a5033b65e9d2c7840`. Local feature HEAD matched the remote before edits. The original dirty release/windows-rc workspace remains untouched. Existing patches were not reapplied. Open GitHub items returned by the issues endpoint were PRs #1, #2, #3, #6 and #7, not separately filed issues. Their existence does not establish a new defect or authorize merging; broader lab PR #6 remains outside this mock-Allow scope.

## Prioritized implementation and verification checklist

1. **Blocked:** retrieve authenticated logs and artifact for the candidate; verify actual archive bytes, inner checksum, layout, dependencies and VERSION.txt.
2. **Implemented, unverified:** extend the existing native MockAllowLab scenario with actual protected requests from an ordinary profile and a second lab tab, localStorage separation at the same origin, lab-tab disposal without harming its siblings, and ordinary search while a lab grant remains active. Context-object comparison alone did not establish these interactions.
3. **Blocked:** build and run these added checks in the full Windows workflow, then verify the resulting exact-SHA artifact. Do not reuse the earlier green run as evidence for this new coverage.
4. **Pending human acceptance:** only after automated criteria and artifact verification are complete, use the retained full acceptance checklist. No request to begin human testing is made now.

## Whole-browser coverage map

| Area | Existing automation inspected | Evidence/limit |
| --- | --- | --- |
| Startup/publish/extraction | Windows smoke script and portable round-trip script | Reported successful at 07da0a7; log/ZIP retrieval blocked |
| Navigation/search/history | TabsAndRenderer, AddressTests, settings restart harness | Prior local 49 address/search checks passed; Windows job success reported |
| Tabs/popups/renderer/native cleanup | TabsAndRenderer, CloseWeb, repeated storage processes | Windows step success reported; exact output inaccessible |
| Privacy modes/rules/settings/CORS | FilteringTests and restart phases | Windows step success reported; no claim of full filter-syntax support |
| Persistent profiles/cookies/localStorage | Storage write + three read processes; policy/profile runner | Windows step success reported |
| Mock authorization/reset/errors/redaction | SecurityDecisionTests, LifecycleChecks, CancellationChecks | Windows console step success reported |
| UI retry/export failure/active closure | LabLifecycle | Windows browser step success reported |
| Lab/ordinary-tab interactions | New MockAllowLab assertions | Not built or executed successfully yet |
| Save-dialog cancellation, focus/DPI/hardware permission UX | Existing UI branches and manual checklist | Not automated or human-verified in this audit |

No newly reproduced production defect was established in this pass; the code change is focused missing integration coverage. Existing Chromium security, mock-only scope and application architecture are unchanged. Full EasyList compatibility, automatic engine updates, real-provider integrations and hardware/real-site acceptance are not silently claimed as implemented by this work; existing documentation describes these limitations.

## Executed checks and external blockers

`git diff --check` passed. A fresh Windows native-harness build was attempted with .NET SDK 8.0.426, Release, win-x64 and SelfContained=true. Restore failed with six NU1301 errors because api.nuget.org was inaccessible; compilation and tests did not execute. Do not describe this as a passing build despite its zero-warning summary.

Run 40111338731 was queried again and remains completed/success for 07da0a7. This is **reported execution evidence from workflow metadata**, not independently read test output. Logs returned HTTP 403; artifact 12566329324 download returned HTTP 401. No checksum was calculated and no new artifact is claimed. GitHub integration discovery reports the connector is not installed; no authenticated download capability was available. A normal push dry run failed with `Could not resolve host: github.com`. Read-only API/ref access still works.

The new coverage requires publication and Windows CI. The older artifact does not contain it. Status documentation alone cannot satisfy that requirement. No rerun of the unchanged older revision was requested. PR #7 remains draft and unmerged. The definition of done before human acceptance remains unmet.

---

# PR #7 published lifecycle verification — 2026-10-24

**Implemented:** feature/security-lab-mock-allow is published at `07da0a7b4f563bf56bc19c6f22a602c7ec8b6211`. The remote ref, PR head and CI head agree. PR #7 targets main and remains draft and unmerged. The isolated feature checkout contained this commit and its lifecycle patch already; no patch was reapplied and no implementation changes were necessary in this verification pass. Unrelated release/windows-rc edits and saved patches were preserved.

## CI-verified execution status

[Windows run 40111338731](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40111338731), [job 120923525010](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40111338731/job/120923525010), is completed with conclusion **success** on the exact SHA above. Every returned step has conclusion success; none is failed, skipped or canceled. This includes Windows x64 Release restore/build, automated console tests, browser integration build and storage/OAuth/tab/renderer tests, Chromium filtering build/tests, privacy settings restart, self-contained publish, release-payload checks, published-app smoke, publish-directory upload, portable ZIP verification and portable artifact upload.

This is verified run/job/step metadata. Detailed job-log download returned **HTTP 403**. Exact assertion counts, compiler-warning totals and printed checksum have not been read from this run's execution output, so they are deliberately not reported as verified. The old run's counts are not substituted.

## Artifacts associated with this exact SHA

- [AegisBrowser-portable-win-x64](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40111338731/artifacts/12566329324), artifact ID 12566329324, 274,255,928 bytes, unexpired at inspection.
- [AegisBrowser-win-x64 publish directory](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40111338731/artifacts/12566364032), artifact ID 12566364032, 273,417,384 bytes, unexpired at inspection.

Both artifact records identify run 40111338731 and head SHA `07da0a7b4f563bf56bc19c6f22a602c7ec8b6211`. GitHub reports the portable artifact digest as `sha256:495585c3278921dbe0a6690a33d100efd1b6829532041a407dc5e400daef4a4a`. **This is API metadata for the outer Actions artifact, not a locally verified hash and not the checksum of the inner portable ZIP.**

The packaging source specifies `AegisBrowser-v0.1.0-rc1-win-x64-portable.zip` and its `.sha256` sidecar. It verifies extracted file counts and hashes and invokes the extracted-app smoke test. Payload checks require AegisBrowser.exe, CefSharp.BrowserSubprocess.exe, libcef.dll and coreclr.dll. These workflow steps report success. However, direct artifact retrieval returned **HTTP 401**: its files, VERSION.txt and inner ZIP checksum have not been independently downloaded/read here. The user must not mistake the API digest for that missing inner checksum.

Target: net8.0-windows, self-contained Windows x64, CefSharp 154.0.340. Follow docs/INSTALL-WINDOWS.md: Windows 10/11 x64, complete extraction, potentially Microsoft Visual C++ x64 Redistributable; no separate .NET SDK/runtime installation required. Launch extracted AegisBrowser/AegisBrowser.exe only after completing the remaining artifact verification.

## Automated-tested locally and remaining blockers

Prior local execution of this implementation compiled the portable projects with 0 warnings/errors and printed 56 privacy and 49 address/search passes. Security Lab execution was blocked by loopback socket permission denial; later policy/profile checks were not reached. Local Windows restore failed NU1301. These local outcomes remain separate from successful Windows job metadata. No redundant local execution was performed in this evidence-only pass.

**Blocked / unverified:** authenticated log access, artifact retrieval and independent inner ZIP checksum/content verification. No new code defect was identified and no additional implementation commit is needed on the available evidence. The definition of done is not yet fully met because these requested evidence checks remain outstanding. See review-deliverables/pr7/CONTINUE-INTEGRATION.md in the preserved workspace for authenticated retrieval commands.

**Not yet manually tested:** all Security Lab human-operated acceptance remains pending. The existing checklist is retained for the final stage after artifact verification; automation is not manual acceptance. No PR readiness transition or merge was performed. This status update is local documentation only; it does not change the published or CI-verified SHA.

---

# End-to-end integration follow-up — verification incomplete

This follow-up continues PR #7 from `796acfb857e1b8d40b487bd2cad6d04d8ed5a5ea`. GitHub API and remote checks reconfirmed that head and draft status before editing. The previously prepared CI documentation was already present locally and was preserved rather than applied twice. Unrelated changes in the original release/windows-rc workspace remain untouched.

## Implemented in this follow-up

- Server shutdown now cancels pending provider work before draining Kestrel, rejects incoming work during shutdown, checks cancellation before committing an authorization, and shares one disposal task across repeated callers.
- The UI discards a failed startup task so Run Test can retry, clears stale decision/ID labels on errors, and does not publish a completed report after closure begins.
- Report writing uses a testable production method; save failures retain the report for retry and do not display a dialog after the window begins closing.
- Existing console and Windows browser runners gained active-cancellation/late-provider, active-shutdown, repeated-disposal, startup-retry, repeated-run, report-write/failure, and close-during-startup/evaluation checks. Existing security assertions and timeouts were not weakened.

Initialization, address/search handling, profile/settings persistence, native tab/popup closure, TLS denial, privacy handler integration and packaging scripts were reviewed. No unrelated browser rewrite, provider integration, security-disabling switch or dependency upgrade was introduced. CefSharp remains 154.0.340; the application target remains net8.0-windows / win-x64.

## Actual local checks

An isolated .NET SDK 8.0.426 (macOS ARM64) was installed under /private/tmp. Dependency-free console restore succeeded using an empty local package source. Release compilation of Core, SecurityLab and Tests succeeded with **0 warnings and 0 errors** using single-node MSBuild and shared compilation disabled. Multi-node restore did not complete in the restricted environment; single-node restore resolved that execution problem.

Execution printed **PASS: 56 privacy checks** and **PASS: 49 address and search checks**. The Security Lab suite then failed to start Kestrel with `System.Net.Sockets.SocketException (13): Permission denied` while binding loopback. The remainder of that runner, including policy/profile checks, was not reached. These environmental restrictions were not worked around by relaxing tests or widening the server binding.

Windows application restore was attempted with `-r win-x64 -p:SelfContained=true` and failed with **NU1301**, unable to load `https://api.nuget.org/v3/index.json`. Consequently, the changed WinForms/native harness has not compiled or executed locally. No new Windows publish, native smoke test or portable artifact is claimed. A push dry run failed with `Could not resolve host: github.com`; read-only GitHub API requests still worked.

## Required next verification

Push the follow-up commit only to feature/security-lab-mock-allow, then run the existing Windows workflow and inspect its complete logs. It already invokes both amended test runners before publish and ZIP verification. Any compiler errors, failing assertions or native lifecycle failures must be fixed and the workflow rerun on the final code revision. The successful run/artifact recorded below belongs to **796acfb only** and does not verify this follow-up.

Manual acceptance is the final phase and is not a blocker to these automated checks. No manual UI test is claimed. The existing checklist in docs/security-lab-mock-allow.md remains pending: normal startup/navigation/search; keyboard and scaling; denial/Allow/reset; repeated runs/status; report export and failure recovery; close during active work; and compatibility with ordinary browsing/profiles/privacy. **Do not mark this follow-up complete or release-ready until Windows verification passes.** PR #7 remains draft and must not be merged automatically.

---

# PR #7 recovery verification — 2026-10-24

The user successfully pushed review commit `796acfb857e1b8d40b487bd2cad6d04d8ed5a5ea`. The remote branch SHA was independently checked with `git ls-remote`; PR #7 remains draft and unmerged at that exact head. The original dirty release/windows-rc workspace was preserved. The isolated review checkout already contained the patch; it was not applied twice. Parent SHA, four-file scope, reverse-patch applicability and `git diff --check` were verified.

## Windows CI on the recovered commit

[Run 40109058867](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40109058867), [job 120916744523](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40109058867/job/120916744523), completed **SUCCESS** for `796acfb857e1b8d40b487bd2cad6d04d8ed5a5ea`. The GitHub API reports every job step successful:

- .NET 8 setup, Windows x64 restore and Release build.
- Existing console suites, including the new expiration-boundary, overlapping-evaluation and reset-during-evaluation coverage.
- Native storage, OAuth, tabs and renderer tests, with the existing shutdown/assertion gates.
- Chromium filtering/CORS and settings process-restart checks.
- Self-contained win-x64 publish, payload validation and published-application smoke.
- Portable ZIP generation/verification and both artifact uploads.

[Portable artifact 12566046409](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40109058867/artifacts/12566046409): 274,252,045 bytes, listed as unexpired. [Complete publish directory 12565678692](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40109058867/artifacts/12565678692): 273,416,606 bytes, listed as unexpired.

Evidence limitation: detailed job-log retrieval returned HTTP 403. Run conclusion, individual step conclusions and artifact metadata were verified, but assertion counts, compiler-warning totals, inner ZIP checksum and payload file counts were not independently re-read from this run's logs. No artifact download/extraction was performed locally. Local dotnet remains unavailable (`command not found`); no local test pass is claimed.

This documentation update follows the verified commit and is initially local only. It does not claim CI verification of a later documentation commit. Earlier sections below are historical, including statements that the coverage had not yet been pushed or run.

## Remaining acceptance

All Security Lab manual UI checks remain **NOT RUN**: toolbar/keyboard access, 100/150/200% scaling, status and request-ID display, repeated runs, JSON export/cancel/save failures, protected page denial/Allow/reset, closure during startup/active work, and normal browsing/privacy/profile compatibility. Follow [the checklist](docs/security-lab-mock-allow.md). Existing automation does not substitute for these human-operated checks. Active-request cancellation and shutdown-during-evaluation coverage gaps described below remain open; this successful run does not add those assertions.

Recommendation: the recovered portable artifact is ready for manual testing. Keep PR #7 draft pending manual acceptance, remaining focused lifecycle verification and full log review. No automatic merge or ready-for-review transition was performed. Chromium security protections and existing tests were not weakened.

---

# PR #7 focused review — 2026-10-24

Remote state was fetched again: PR #7 remains open, draft, unmerged and conflict-free at `4a0124cbbaf10faf28ff746a7e5d952feb07668e`, based on main `98a8835575c22268a456a64a5033b65e9d2c7840`. No reviews, inline comments or issue comments were returned. Branch-protection settings could not be read (HTTP 401), so required-check configuration is not independently confirmed.

## Exact-head CI evidence

[Windows run 39971483169](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39971483169), [job 120475026009](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39971483169/job/120475026009), is **SUCCESS** on `4a0124cbbaf10faf28ff746a7e5d952feb07668e`. The API reports every step successful: restore/build, console suites, native storage/popup/tab/renderer tests, filtering/CORS, settings restart, self-contained publish, application smoke, ZIP verification and artifact uploads. The companion push run 39971474953 also succeeded on that SHA.

Previously inspected logs for this head recorded 26 Mock Allow, 56 privacy, 49 address/search, 50 policy/profile, 49 native write, 8 storage checks in each of three read processes, 20 filtering/CORS and 3 write + 4 read settings checks; zero compiler warnings/errors; TLS rejection, clean CEF shutdown and 852-file portable verification. In this resumed session the run/job/artifact metadata was reconfirmed, but re-downloading detailed logs returned HTTP 403. These results apply to the named remote head, **not to the subsequent local coverage changes**.

[Portable artifact 12509990722](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39971483169/artifacts/12509990722) is listed as unexpired, 274,249,884 bytes. [Publish directory artifact 12509043351](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39971483169/artifacts/12509043351) is also unexpired. Artifact contents were not downloaded again in this session.

## Focused review and pending coverage

The 15-file PR diff retains the existing privacy, Smart Search, profiles and native lifecycle architecture. Program.cs and SecurityHandlers.cs are unchanged by this PR. The server binds IPv4 loopback on an OS-selected port, rejects foreign Host/Origin values, uses cryptographically random HttpOnly session cookies and checks server-side authorization for protected responses. Reset marks sessions revoked and increments their revision; overlapping evaluations can only authorize the current revision. Reports accept only validated request IDs and fixed error labels. No real integration or Chromium security-setting changes were introduced.

No new reproducible production defect was established. Additional coverage is prepared for the exact ten-minute expiration boundary, overlapping evaluations, and reset/reset-all during a pending evaluation. An optional TimeProvider defaults to TimeProvider.System so expiration can be tested without sleeping or changing production lifetimes. Tests use the existing console runner and real ephemeral-port Kestrel fixtures. No existing assertion or timeout was weakened.

**The additional coverage is NOT RUN.** Local `dotnet --info` fails because the SDK is absent on this macOS host. Windows/native execution is unavailable locally. A new Windows CI run on the final pushed commit is required before these changes can be accepted. Active-request cancellation, server shutdown during evaluation, and startup/active-work UI closure still require focused verification; the existing pre-canceled runner and completed-work owner-close tests do not prove those cases.

## Manual acceptance and recommendation

All Security Lab manual UI cases remain **NOT RUN**. Follow the explicit checklist in [milestone instructions](docs/security-lab-mock-allow.md): keyboard access; 100/150/200% scaling; status/decision/IDs; repeated runs; JSON export/cancel/save failures; page denial/Allow/access/reset; close during startup/execution; Smart Search, privacy modes, tabs and profile separation. Prior user-reported Smart Search results do not establish Security Lab acceptance.

Keep PR #7 draft. The existing verified artifact is available for manual testing, but completion of this review is blocked pending publication and Windows verification of the new coverage, remaining lifecycle checks, and user acceptance. Do not mark ready or merge automatically.

---

# Security Lab milestone 1 — Mock Allow verification (2026-10-23)

[Draft PR #7](https://github.com/roshan-pandit1/AegisBrowser/pull/7), branch `feature/security-lab-mock-allow`, starts from main `98a8835575c22268a456a64a5033b65e9d2c7840`. It is independent of the broader draft PR #6. It remains draft and unmerged.

## Actual Windows results

[Windows run 39970819828](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970819828), [job 120472697977](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970819828/job/120472697977), **SUCCESS** for implementation `7b662472b3ca3dc67fb2d24f628ba8d5ab692310`. Completed job logs and artifact metadata were inspected. This subsequent documentation commit records that tested implementation; its own CI result is reported on PR #7.

| Check | Actual result |
| --- | --- |
| Restore/build | PASS: windows-latest, .NET 8, win-x64 Release; zero compiler warnings/errors |
| Mock Allow endpoint/workflow suite | PASS: 26 checks, including loopback, provider/decision contract, unique IDs, forged Allow, session separation, reset/replay, provider errors, cancellation and redaction |
| Existing privacy / search / profile suites | PASS: 56 + 49 + 50 checks |
| Native browser write suite | PASS: 49 checks, including Security Lab status/workflow, local page rendering, separate memory request context, browser Allow/protected flow, reset revocation and owner shutdown |
| Persistent cookie/localStorage reads | PASS: 8 checks in each of three fresh read processes |
| Chromium filtering / CORS | PASS: 20 checks |
| Settings restart | PASS: 3 write + 4 read checks |
| Existing TLS / popup / renderer / clean shutdown regressions | PASS; native assertion gate retained |
| Self-contained win-x64 publish and complete directory upload | PASS |
| Published/extracted application startup and normal-close smoke | PASS |
| Portable archive | PASS: 852 file hashes verified; ZIP and checksum uploaded |

[Portable Windows x64 test artifact](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970819828/artifacts/12508009706), 274,249,352 bytes. Inner ZIP SHA-256: `448ee2a7cdad1e8bff0c5daca146eb0dc4d69af99744ccbee304ec14e5e81d31`. [Complete publish directory](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970819828/artifacts/12508204238). The archive includes installation instructions and SECURITY-LAB-MOCK-ALLOW.md. The ASP.NET Core runtime is bundled with the self-contained build.

## Failures diagnosed and corrected

1. [Run 39970367638](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970367638) failed JSON validation: ASP.NET selected the RequestDelegate overload and discarded the evaluation method's IResult. The endpoint is now explicitly registered as a typed route-handler delegate. ASP0016 is treated as an error to prevent recurrence. No assertion was weakened.
2. [Run 39970705716](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39970705716) passed all endpoint checks but the browser harness failed while comparing against the normal browser's nullable request-context property. The test now compares the lab context against the explicit global context and still requires separate in-memory storage. The final run passes.

## Scope and remaining manual acceptance

**Manual UI acceptance is NOT RUN.** A user must run this milestone's portable build before any manual pass is recorded. Remaining checks: Security Lab toolbar and keyboard access; 100/150/200% scaling; status and ID display; repeated runs; open-page buttons; reset UX; JSON export/cancel/save-error behavior; closing during startup or active work; normal profile, privacy and Smart Search behavior with lab tabs open. Automated native controls and HTTP checks above are not a human visual acceptance claim.

Run Test uses a fresh isolated HTTP session and verifies nine assertions, ending with reset/revocation. Open Test Page uses its own temporary Chromium context, separate from both the runner and ordinary profile storage. Reset Session revokes all sessions on this lab server. The report contains public request IDs, timestamps, fixed error codes and assertions, never cookies or session secrets. No real providers, external security-service calls, fingerprint modifications or security bypasses are implemented. TLS validation, Chromium protections and existing handlers remain unchanged.

Local compilation is unavailable because this macOS environment lacks dotnet; executable verification is from Windows CI. To preserve earlier local work, milestone files were prepared separately under `/private/tmp/aegis-mock-allow` and committed through the GitHub connector. See [milestone instructions](docs/security-lab-mock-allow.md).

---

## Earlier Smart Search verification (historical)

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
