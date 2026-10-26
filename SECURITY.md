# Security policy and reporting

AegisBrowser is an experimental embedded Chromium browser, not a security-certified or anonymity product. Read [security limitations](docs/SECURITY-LIMITATIONS.md), the [threat model](docs/THREAT-MODEL.md) and [current remediation evidence](docs/REMEDIATION-REPORT.md) before handling sensitive browsing data.

Report suspected vulnerabilities privately using this repository's **Security → Report a vulnerability** option if available. If private reporting is unavailable, use an established private channel to the maintainer. Repository security-alert/reporting settings have not been inspected or changed by this audit. Do not put credentials, profile databases, authentication cookies, full browsing URLs, private keys or raw Chromium logs in public issues. Use a minimal synthetic local fixture, affected source/binary revision, Windows version and redacted steps.

There is no promise of a supported stable release or response SLA. Engine updates require review and exact-revision Windows verification; an upstream release number alone is not evidence that every Chromium CVE is fixed. No automatic binary updater executes downloaded code.

## Patch and dependency review

1. Review the pinned CefSharp release, its CEF/Chromium versions and upstream security notes, .NET support/patch status, and NuGet transitive advisories.
2. Keep all CefSharp references consistent. Dependabot proposes grouped changes; it does not approve compatibility or merge automatically.
3. Restore with transitive NuGet auditing. High/critical advisories and audit-feed failures are errors; investigate all remaining warnings. Do not suppress them to obtain a green candidate.
4. Run the existing managed/native suites, repeated persistent-storage phases, invalid TLS/CORS checks, UI shutdown tests and published/extracted smoke checks on Windows for the exact reviewed commit.
5. Review the package/file SBOM, native payload and archive hashes. A NuGet audit is not a complete native Chromium vulnerability scan.
6. Review the final diff and manual acceptance matrix. Sign with an owner-controlled code-signing identity only through separately authorized infrastructure; no signing credentials are stored here. Verify signatures and authenticated distribution before a stable release.

Source policy checks catch selected explicit regressions; they do not prove native sandbox operation. **Do not disable TLS validation, web security, origin isolation, permissions or provider security checks to resolve compatibility failures.**
