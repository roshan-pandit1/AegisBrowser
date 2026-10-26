# Release gate checklist

All boxes below remain open for this audit's uncommitted revision unless supported by new evidence.

- [ ] Reconcile the intended branch with current main/PR work using a clean checkout; preserve unrelated edits. Review the audit changes, including native lifetime changes. The recovery snapshot includes pre-existing work and is not an audit-only patch.
- [ ] Verify trusted CefSharp/CEF/Chromium/.NET metadata and security advisories. Test any upgrade consistently across all projects. Resolve high/critical advisories and review lower warnings.
- [ ] Obtain a clean exact source commit and successful Windows run for that commit. PR merge synthetic SHA/tree provenance must be explicit.
- [ ] Run full console, Lab HTTP, browser write/read/read/read, filtering/CORS/download/diagnostics and settings restart suites. No assertion suppression or increased timeout as a substitute for fixing failures.
- [ ] Execute indexed/generic benchmark on the same hardware before/after; separately measure startup and navigation. Record fallback counts and allocations, not guessed improvements.
- [ ] Publish self-contained win-x64; inspect real package/file SBOM scope and native dependencies. Reject profile, credential, log and debug clutter.
- [ ] Smoke-test published and extracted apps, check parent/observed-descendant exit, verify ZIP file count/hashes and SHA-256 sidecar.
- [ ] Use a disposable Windows account for the fixed baseline Lab smoke profile. Do not run destructive crash/profile tests on normal browsing data.
- [ ] Verify clean-machine prerequisites, media permission handling, native consent revocation, OAuth/payment compatibility using separately authorized test accounts/sandboxes, keyboard/screen-reader and 100/150/200% scaling.
- [ ] Resolve or explicitly accept native sandbox limitations through security review; do not advertise a Chrome-equivalent sandbox.
- [ ] Establish authorized signing infrastructure, verify Authenticode signatures and distribution integrity. No signing or update-secret configuration is performed by this patch.
- [ ] Update BUILD-STATUS with exact commit, CI jobs, counts, artifact links and checksum. Preserve manual vs automated labels.
- [ ] Obtain explicit release/merge approval. No automatic merge, release or downloaded-binary execution.

The Python inventory generator requires Python 3.12+ in CI or local packaging. It records NuGet packages from `project.assets.json` and hashes actual published files. It does not infer an exhaustive list of third-party source components statically linked into libcef. Existing inventory is verified, not rewritten, when packaging an already-inventoried directory.
