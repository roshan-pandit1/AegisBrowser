# Current state: automated candidate and artifact verification complete

Feature SHA: 75fbf6a59b4565fc38f16bf63a73938cb68edb4e. Successful run: 40163306372. Actual PR checkout/artifact revision: d156032f4b874d5f14ac78bad4cfa00a2563a6a8. Its Git tree equals the feature tree a1cc73b4ef97a7fb9c709e8275d6ef977e04f9c9. PR #7 remains draft and unmerged.

Authenticated downloads are present at /Users/roshan/aegis-ci.log and /Users/roshan/aegis-40163306372/. Logs were read: all nine new assertions and the other suites passed. Inner ZIP SHA-256 was computed locally and matches both sidecar and CI: 30f13afe906161ed5900654ca6b5f510754e35aa29bb643e3ef565fdbb89a10d. ZIP integrity, all 852 extracted files, dependency payload and VERSION.txt were independently verified. See BUILD-STATUS.md and verified-artifact-40163306372.json for exact results and warnings.

No publication retry, patch reapplication, new code commit or CI rerun is necessary for this unchanged candidate. The former 401/403 blockers are resolved by the downloaded evidence. Do not confuse the branch commit, synthetic PR merge commit, outer artifact digest and inner ZIP checksum.

The automated continuation is complete; human acceptance is still pending. The checklist is in FINAL-ACCEPTANCE.md. Do not mark ready or merge without explicit authorization. Do not claim universal browser compatibility from these tests. The original dirty release/windows-rc workspace and historical patches remain preserved. Documentation updates here are local records, not a new remote commit.

---

## Historical recovery instructions (superseded; do not rerun automatically)

# Current continuation — publication and Windows CI succeeded

Local and remote: 75fbf6a59b4565fc38f16bf63a73938cb68edb4e. Run 40163306372 completed successfully on that exact SHA; all steps report success. Do not reapply saved patches, repush the same commit or rerun the unchanged passing revision merely to resolve download authentication. PR #7 remains draft and unmerged.

Remaining blockers are HTTP 403 for job logs and HTTP 401 for artifact retrieval. Use an authenticated GitHub CLI session with repository/Actions read access (do not send credentials in chat):

```sh
gh run view 40163306372 --repo roshan-pandit1/AegisBrowser --log > aegis-40163306372.log
gh run download 40163306372 --repo roshan-pandit1/AegisBrowser --name AegisBrowser-portable-win-x64 --dir aegis-40163306372
```

Inspect the actual native-suite output and counts; the new assertions are on its write-phase code path, but their log output has not been read here. Verify the downloaded inner ZIP against its .sha256 sidecar, check archive integrity, extract all files and confirm VERSION.txt identifies 75fbf6a59b4565fc38f16bf63a73938cb68edb4e. Confirm executable, CEF subprocess, libcef.dll, coreclr.dll and supporting runtime/resources are present. Do not use GitHub's outer artifact digest as the inner ZIP checksum.

The artifact is https://github.com/roshan-pandit1/AegisBrowser/actions/runs/40163306372/artifacts/12587591846 . Update BUILD-STATUS.md with actual retrieved evidence. The nine assertions' containing suite reports success, but direct log/ZIP verification remains incomplete. Manual acceptance is deferred until the complete readiness criteria are met. No production or security changes are needed to resolve this authentication blocker.

The older instructions below are historical and must not be followed as current publication instructions.

---

# Recovery attempt — 2026-10-25

**Verified:** the former temporary checkout had been removed. It was recovered at /private/tmp/aegis-pr7-git using fetched remote history and the preserved browser-lab-coexistence.bundle. HEAD is exactly `75fbf6a59b4565fc38f16bf63a73938cb68edb4e`, branch feature/security-lab-mock-allow, clean working tree. Reverse-patch validation passed. No duplicate commit was created and the unrelated dirty release/windows-rc workspace was preserved.

**Blocked publication:** the remote ref was read as `07da0a7b4f563bf56bc19c6f22a602c7ec8b6211`. One normal push of the exact intended SHA was attempted and exited 128 with `Could not resolve host: github.com`. No retries, force pushes, resets or rebases were performed. Read-only fetch succeeded; push connectivity remains unavailable.

**CI/artifact:** the exact-SHA Actions query returned no runs. The nine new assertions have not been verified executing. No current-commit artifact, archive integrity result or SHA-256 is available. Older CI/artifacts remain inapplicable.

**NuGet:** the saved restore log was inspected; its eight NU1301 errors target https://api.nuget.org/v3/index.json. Previous diagnostics established direct DNS failure while tool-mediated curl could retrieve the standard feed index and pinned package metadata. There is no new evidence of a missing package version. Restore was not retried in this attempt: publication remains unresolved and the former temporary SDK is gone. No source, dependency or security setting changed.

**Next action:** use a terminal with working GitHub push connectivity to execute the exact command below. The isolated checkout has been restored, so this path is valid again:

```sh
git -C /private/tmp/aegis-pr7-git push origin 75fbf6a59b4565fc38f16bf63a73938cb68edb4e:refs/heads/feature/security-lab-mock-allow
```

Then verify the remote SHA and exact-revision Windows run, retrieve its logs/artifact with authenticated Actions read access, and verify the inner ZIP checksum. Manual acceptance remains deferred. No PR readiness or merge action was taken.

---

# Current blocker and precise resume commands

No new commit was made. Local: 75fbf6a59b4565fc38f16bf63a73938cb68edb4e; remote: 07da0a7b4f563bf56bc19c6f22a602c7ec8b6211. No Actions run exists for 75fbf6a. The latest restore produced 8 NU1301 errors against the standard nuget.org index; direct DNS fails although tool-mediated curl can retrieve the index and pinned CefSharp metadata. See BUILD-STATUS.md and latest-restore.log. The code/patch remains preserved.

Run these in a terminal with working GitHub connectivity (no force):

```sh
git -C /private/tmp/aegis-pr7-git push origin 75fbf6a59b4565fc38f16bf63a73938cb68edb4e:refs/heads/feature/security-lab-mock-allow
git -C /private/tmp/aegis-pr7-git ls-remote origin refs/heads/feature/security-lab-mock-allow
gh run list --repo roshan-pandit1/AegisBrowser --commit 75fbf6a59b4565fc38f16bf63a73938cb68edb4e
```

With GitHub CLI authenticated for this repository and Actions read access, substitute the actual new run ID:

```sh
gh run view NEW_RUN_ID --repo roshan-pandit1/AegisBrowser --log > new-run.log
gh run download NEW_RUN_ID --repo roshan-pandit1/AegisBrowser --name AegisBrowser-portable-win-x64 --dir new-artifact
```

Do not provide credentials in chat. If using local Windows verification after network access is restored, use the existing workflow commands: dotnet restore AegisBrowser/AegisBrowser.csproj -r win-x64 -p:SelfContained=true, then the workflow's builds, console/browser/filtering tests and packaging steps. Preserve package versions and TLS validation. Follow the archive verification commands in the historical section below against the NEW artifact directory and NEW expected revision. Keep PR #7 draft and unmerged. Manual acceptance stays deferred.

---

# Latest continuation: whole-browser interaction coverage

Local branch: feature/security-lab-mock-allow. New local commit: `75fbf6a59b4565fc38f16bf63a73938cb68edb4e`; parent and current remote: `07da0a7b4f563bf56bc19c6f22a602c7ec8b6211`.

New changes add nine native browser assertions for actual cross-context authorization/storage separation and ordinary search/tab survival while the lab is active. No production behavior changed. The commit incorporates the previously uncommitted published-CI status update: do not separately apply 0004. The 0005 patch and browser-lab-coexistence.bundle preserve this commit. The isolated checkout is clean; unrelated original workspace edits remain untouched.

These assertions are unverified. Native Release win-x64 build failed with six NU1301 restore errors; tests never ran. Push dry run failed resolving github.com. Authenticated logs and artifacts remain unavailable (403/401). Do not begin manual UI acceptance or describe the previous artifact as containing this coverage.

From a terminal with working authenticated GitHub access:

```sh
git -C /private/tmp/aegis-pr7-git push origin 75fbf6a59b4565fc38f16bf63a73938cb68edb4e:refs/heads/feature/security-lab-mock-allow
```

Use a normal fast-forward push only. Then verify the remote SHA, inspect the new Windows run and fix any failures. Download and verify that new run's portable artifact, record actual log counts and archive checksum, and only then evaluate the complete readiness checklist. Do not rerun the old 07da0a7 CI merely to compensate for unavailable artifact access.

If the temporary checkout is gone, apply 0005-browser-lab-coexistence.patch using git am in a clean feature-branch checkout at 07da0a7. Do not apply it to the dirty release/windows-rc workspace. Keep PR #7 draft and unmerged. The historical instructions below describe retrieval of the previous candidate and remain useful for evidence comparison, not as verification of the new commit.

---

# Resume PR #7 artifact and log verification

Implementation is published at 07da0a7b4f563bf56bc19c6f22a602c7ec8b6211. Run 40111338731 completed successfully on that exact SHA; all returned steps succeeded. PR #7 remains draft and unmerged. Do not reapply 0001, 0002 or 0003 patches or repush the implementation unnecessarily. The isolated checkout is /private/tmp/aegis-pr7-git; its only new edit is evidence documentation in BUILD-STATUS.md. Original unrelated working changes remain preserved.

Remaining blockers: job logs return HTTP 403; artifact download returns HTTP 401. No actual inner portable checksum or new exact test counts were retrieved. Do not substitute older run results or the outer Actions artifact digest. This task is not yet fully complete.

From an authenticated GitHub CLI session with Actions read access:

```sh
gh run view 40111338731 --repo roshan-pandit1/AegisBrowser --log > aegis-40111338731.log
gh run download 40111338731 --repo roshan-pandit1/AegisBrowser --name AegisBrowser-portable-win-x64 --dir aegis-40111338731
```

Inspect the logs for actual suite counts, warnings, TLS rejection, native shutdown and packaging checks. In PowerShell, verify the downloaded inner archive before launching anything:

```powershell
$zip = 'aegis-40111338731/AegisBrowser-v0.1.0-rc1-win-x64-portable.zip'
$expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ine $expected) { throw 'Portable checksum mismatch' }
$destination = Join-Path $env:TEMP ('aegis-verified-' + [guid]::NewGuid().ToString('N'))
Expand-Archive $zip $destination
Get-Content "$destination/AegisBrowser/VERSION.txt"
foreach ($name in @('AegisBrowser.exe', 'CefSharp.BrowserSubprocess.exe', 'libcef.dll', 'coreclr.dll')) {
    if (!(Get-ChildItem "$destination/AegisBrowser" -Recurse -Filter $name)) { throw "Missing $name" }
}
```

Confirm VERSION.txt identifies 07da0a7b4f563bf56bc19c6f22a602c7ec8b6211. Record the actual inner SHA-256 and relevant log output in BUILD-STATUS.md, keeping automated and manual results separate. Any newly committed documentation revision requires its own CI verification; do not claim this run verifies a later SHA.

Only after the evidence checks above, begin the pending human checklist in security-lab-mock-allow.md: startup/browsing/search, keyboard/focus/scaling, repeated runs, fresh-session denial/Allow/other-session denial/reset, lab and ordinary tabs, sanitized export and save failures, active-work shutdown and recovery. Launch the fully extracted AegisBrowser/AegisBrowser.exe on Windows x64. Keep PR #7 draft and unmerged.
