# Aegis Browser v0.1 acceptance status — 2026-10-20

## Current acceptance work

[Windows GUI acceptance plan](docs/windows-acceptance-v0.1.md) defines startup, navigation, tabs, isolated persistent profiles/cookies, deny-by-default permissions, DevTools, offline diagnostics and scaling tests. Local browser fixture: `tests/acceptance/`.

- Manual cases S01–S03, N01–N04, T01–T03, P01–P04, Q01–Q05, D01, F01–F03 and U01: **NOT RUN**. No interactive Windows workstation is available in this macOS session.
- New Windows published-app startup/cache/normal-close smoke test: **PENDING CI**. This is limited automation, not full GUI acceptance.
- Expanded policy tests including a separate-process profile lock probe: **PENDING CI**.
- No new GUI failures observed because manual GUI cases have not been executed. This is not a pass. Runtime failures found by CI will be recorded here.
- Existing limitations: deny-all permissions/downloads, unsupported opener-dependent blank popups, no tab-session restoration, and untested visual/accessibility behavior. Full acceptance remains incomplete.
- Chromium security settings and production browser code are unchanged by this acceptance work.

Earlier confirmed results and resolved failures follow; they do not establish acceptance of the new tests.

# Verification status — 2026-10-20

## Latest result: Windows CI passed

Write access succeeded on retry. [PR #1](https://github.com/roshan-pandit1/AegisBrowser/pull/1) contains only the two CefSharp API fixes and global.json, committed as `6951dab83ded9a4aa7828fb1ec59c0ed248c5cea` on `fix/cefsharp-net8-build`.

[CI run 39396193090](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39396193090) completed successfully. Its full job logs confirm SDK 8.0.426, restore success, Release build with zero warnings/errors, 36 passing policy/profile checks, self-contained win-x64 publish and payload validation. PR merge checkout: `9c91b9c5dee7667ae5b21b320023aae06f5c3279`.

[Artifact 12250919586](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39396193090/artifacts/12250919586): `AegisBrowser-win-x64`, 258,924,406 bytes, uploaded successfully and confirmed in the run artifact API. SHA-256: `7cfd08d37f14d4802471e2afd05a634038c8637ba5a7e5a0e92507dffe5fc3ff`. Expires 2027-01-18.

Windows GUI acceptance has not been performed. This local status update and earlier README edits are preserved locally, outside the narrowly scoped three-file PR. Verification results are also recorded in the PR description.

## Historical failure and access blocker (resolved)

**Windows CI ran and failed compilation. Fixes are prepared locally but cannot be uploaded because the GitHub integration returns HTTP 403. No release artifact exists for this run.**

## Actual Windows CI results

Repository: `roshan-pandit1/AegisBrowser`, branch `main`.

- Commit: `c500587fb4dd0abd0bae9e809ac138e0fcd20414`.
- [Workflow run 39394651876](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39394651876).
- [Build job 118534251494](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/39394651876/job/118534251494).
- Runner: `windows-latest`, resolved to Windows Server 2025 x64.
- `actions/setup-dotnet@v4` installed SDK 8.0.426 successfully.
- Without a global.json, `dotnet --info` selected preinstalled SDK 10.0.401.
- Windows x64 application restore: **passed**.
- Windows x64 Release build: **failed**, one error and one warning.
- Automated test restore/execution, self-contained publish, payload validation and artifact upload: **skipped** after the build failed.

The completed job logs were retrieved and inspected. Compiler diagnostics:

```text
Program.cs(33,22): error CS0117: 'Cef' does not contain a definition for 'RegisterSchemeHandlerFactory'
SecurityHandlers.cs(83,16): warning CS0618: 'ResourceHandler.FromString(string, string)' is obsolete
```

## Prepared local corrections (not yet CI-verified)

- `AegisBrowser/Program.cs`: obtain `Cef.GetGlobalRequestContext()` and register the diagnostics handler on that request context, disposing its wrapper before Chromium shutdown.
- `AegisBrowser/SecurityHandlers.cs`: select the MIME-type overload with named argument `mimeType: "text/html"`.
- `global.json`: select .NET SDK 8.0.4xx using baseline 8.0.400 and `latestPatch`, preventing an installed SDK 10 from taking precedence.
- `AegisBrowser/README.md`: document the actual failed run and SDK selection.

No Chromium security settings, permission rules, certificate handling or spoofing behavior were changed.

## Current blocker

Read access now works, and the user's source upload created `main` and triggered the run above. GitHub still reports repository visibility as public. No additional source was uploaded by this agent.

Two attempts to upload the fixes failed:

1. Git trees API: HTTP 403, `Resource not accessible by integration`.
2. Repository contents API: HTTP 403, `Resource not accessible by integration`.

Although repository metadata lists the user's push permission, the connected integration does not have effective write access for these operations. Direct Git transport also remains unavailable because the shell cannot resolve github.com.

Grant the connected GitHub integration repository contents write access, or push the prepared local changes (including global.json) through an authenticated Git client. Then inspect the resulting Windows CI logs, fix any further errors and rerun until restore/build/tests/publish and artifact upload are confirmed successful. The current failed run must not be reported as a working release.

## Local environment and workflow configuration

Host: macOS ARM64, no `dotnet` on PATH. Earlier local restore, build, test and publish attempts all exited 127 before compilation. XML parsing and diagnostics JavaScript syntax checks passed in the initial pass. Workflow YAML parsing and runner/SDK/artifact assertions passed locally.

The workflow explicitly targets win-x64, runs the console tests as x64, publishes self-contained without rebuilding, checks the application/CEF subprocess/libcef/coreclr payload, and uploads the complete publish directory as `AegisBrowser-win-x64` only after preceding steps pass. Those later steps have not yet executed successfully. Windows GUI acceptance remains separate from the automated policy tests.
