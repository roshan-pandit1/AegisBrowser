# Portable candidate status

This package is an experimental Windows x64 candidate, **not an approved production release**. The source checkout commit and intended self-contained .NET runtime are recorded in `VERSION.txt`. Verify the ZIP's own SHA-256 against its sidecar and the corresponding CI run; a checksum is not a signature. A source commit alone does not establish that a locally built working tree was clean or that an artifact has been reviewed.

The candidate keeps required-proxy profiles blocked. Its Chromium renderer sandbox has not been independently verified, the engine's current security-fix coverage is unresolved, and manual desktop, installation, upgrade and rollback acceptance for this exact artifact are not established. Complete and review the dependency notices and licensing inventory before distribution. Do not interpret a passing package smoke test as approval for production or proxy-protected browsing.

`release-rc1.md`, `production-release.md` and `BUILD-STATUS.md` contain historical release assessments and test results. Their .NET 8 runtime, artifact hash and manual acceptance claims apply to their named earlier revisions, not automatically to this candidate. Follow `INSTALL-WINDOWS.md` for extraction and profile location, using this package's own identity and current release decision.
