# .NET 10 candidate: Windows manual acceptance

**Status: NOT_RUN. Release A and Release B: NO_GO.** This is an operator procedure, not an acceptance result. The candidate's stock CefSharp initialization disables the Chromium renderer sandbox; do not rely on renderer sandbox isolation. Use only synthetic data on an authorized disposable Windows x64 machine and test account; do not enter real credentials or personal data. Required-proxy profiles must stay blocked.

## Identify the artifact

The existing PR #83 candidate was built from source commit `6329a0fffcd587ce9b076ac70afc3caa455670aa`. Its [Windows push run 44936547297](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44936547297) passed. Its portable CI artifact is `AegisBrowser-portable-win-x64`, [artifact 14720495335](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44936547297/artifacts/14720495335). The **inner** `AegisBrowser-RC1-win-x64.zip` SHA-256 printed by that job is `ae8274e9122244b4b6948e8f173166f71e01afe9ef995ab7ae2aeb499463cc3c`. The GitHub artifact download is a separate outer archive with a different digest. Artifacts can expire; if testing a new build, obtain and record that build's own source SHA, run, artifact ID and inner ZIP hash. A matching hash detects changes; it does not authenticate an unsigned publisher.

The later PR #84 candidate at commit `e749ba55edbe160cd9f1be88ed4827fc4918368c` passed [Windows push run 44987442849](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44987442849). Its [portable CI artifact 14740965338](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44987442849/artifacts/14740965338) contains an inner ZIP whose SHA-256 is `628c89cca047cd55018b83e605518aa3eeef70e950912fefdafc401efc50f825`. These identities do not apply to another commit or run. This remains a test artifact, not a production release.

Record tester, UTC date, Windows edition/build and x64 architecture, artifact/run URL, source SHA, inner ZIP SHA-256, expected runtime and actual runtime identity, clean-install state, evidence location and a separate PASS, FAIL, BLOCKED or NOT_RUN for each case. Keep logs and screenshots free of secrets. Do not substitute the earlier .NET 8 RC1's hash or manual results for this candidate.

On the disposable machine, download the selected artifact through an authorized channel. Verify the inner ZIP before extraction:

```powershell
Get-FileHash -LiteralPath '.\AegisBrowser-RC1-win-x64.zip' -Algorithm SHA256
```

Compare it to the **matching run's** output and sidecar; stop on mismatch. Extract the entire inner ZIP into a new directory without overwriting another build. Inspect `AegisBrowser\VERSION.txt`, `AegisBrowser.runtimeconfig.json`, native CEF resources and subprocess files; record their identities and the candidate's .NET runtime `10.0.15`. CI checked runtime file hashes and x64 binaries; the manual operator must verify the selected artifact rather than infer its identity from CI for a different archive.

## Isolated setup

Use a disposable Windows x64 VM or machine and a fresh standard user account whose `%LOCALAPPDATA%\AegisBrowser\Profiles` contains no real profiles. Test self-contained startup on an image without an installed .NET runtime where available. Keep the previous verified package, candidate package, copied test data and evidence in separate directories. Never overwrite application files or modify an existing user's profiles.

Prepare a controlled, normally trusted HTTPS origin and a separate test-owned invalid-certificate endpoint; validate their behavior independently. Do not disable certificate validation or install unapproved trust roots. For the repository's local cookie/localStorage fixture, from a reviewed checkout at the candidate commit run:

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory tests/acceptance
```

Keep the origin and port fixed across sessions. Fixture availability, valid and invalid HTTPS endpoints and browser prerequisites are independent preconditions. If any are unavailable, mark affected cases BLOCKED rather than interpreting a connection failure as a successful security check.

## Acceptance cases

Run the applicable cases in [Windows GUI acceptance](windows-acceptance-v0.1.md) against the exact extracted candidate, including navigation, tabs, popups, profile leases and privacy. Record these additional outcomes separately:

| Case | Procedure and required evidence |
| --- | --- |
| Startup and shutdown | Launch the extracted app as the disposable user; inspect version and diagnostics. Open several tabs, close the browser normally and restart. Record process exit, errors and whether the same profile can be reopened; parent exit alone does not prove complete Chromium subprocess cleanup. |
| HTTPS | Verify actual content from the valid controlled HTTPS origin, then verify the invalid-certificate origin produces a certificate rejection. A DNS, connection or fixture failure is not certificate-rejection evidence. |
| Isolation and persistence | In disposable profile A, set the fixture cookie and localStorage marker; verify profile B initially cannot read it. Set a different B marker, close both, reopen each and verify the separate values persist. Test IndexedDB on a controlled fixture if available; otherwise mark that part BLOCKED. |
| Lease and privacy | Keep A open and confirm a second A launch is rejected while B can open. Change supported privacy settings in A, restart and verify they persist independently of B. Exercise malformed-settings recovery only on a disposable copied profile; browsing must remain blocked until repair and restart. |
| Required proxy | Use a separate disposable profile configured to require a proxy. Verify startup is blocked and no direct browser session opens. Do not treat this as a network-containment test. |
| Upgrade and rollback | Perform the procedure below using only copied synthetic profiles. Record both package identities, backups, observed data and any incompatibility. |

## Disposable upgrade and rollback

1. Using the previous verified package in its own directory, create a disposable named profile and save known synthetic cookie, localStorage and, if a controlled fixture is available, IndexedDB markers. Set recognizable privacy and search settings. Close **all** browsers and profile managers and verify the profile lease is released.
2. Make a separate protected copy of the entire disposable `Profiles` directory, including metadata database, `metadata-backups`, profile folders and any SQLite WAL/SHM files. Verify the backup is complete and readable. This is a test copy; do not use or migrate a real profile. Preserve the old application ZIP and its independently recorded SHA-256.
3. Start the candidate from its separate extracted directory with the same disposable test account/profile. Verify expected data, metadata and settings, test profile lock exclusivity, then close and reopen. Record missing data, migration errors and any failure without repairing or discarding the original backup.
4. Close every Aegis process. Preserve the post-upgrade test tree separately. Restore the pre-upgrade copy only inside the disposable test account, then launch the old verified package from its own directory and verify the original markers and settings. Record any loss or failure. The saved pre-upgrade backup is the rollback source; do not assume the older Chromium can safely read a profile modified by a newer one.
5. Independently verify the retained backups and inspect process exit and lease release. Delete disposable data only under the test environment owner's approved cleanup procedure.

Metadata backup alone does not contain the browser profile folders. Consult [profile migration and rollback](profile-manager.md) for database and WAL/SHM details. Neither this procedure nor CI proves compatibility with all future versions.

Finish by attaching the case-by-case evidence and updating the release register only after review. Manual acceptance, renderer sandbox enforcement, engine-security coverage and production release approval remain separate gates.
