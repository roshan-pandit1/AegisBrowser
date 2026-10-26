# Security audit recovery — 2026-10-26

Branch: `release/windows-rc`; HEAD: `9fd457276d3e741ade1041ea37655115b7beea4c`.
The working tree is uncommitted. No push, release or PR state change was performed.

## Contents and provenance

`browser-source-snapshot.zip` contains 71 current browser source/configuration/documentation files, including **pre-existing user work**. It excludes .git, generated bin/obj/cache directories, the separate Python project and older recovery archives. It is **not a Windows application artifact** and **not an audit-only patch**.

Archive CRC and every file's SHA-256 were independently checked after creation. Archive SHA-256: `62a3b762f790c4bd1b3d5c01a9b15466e6049007537da4395aacfc7f95d6ef88`. `source-manifest.json` records each source file digest. `tracked-working-tree.patch` is the entire tracked delta against HEAD, also including pre-existing work; untracked browser source is preserved in the ZIP. The temporary pre-audit baseline is no longer available, so task-only attribution cannot safely be reconstructed. Do not apply either over a dirty checkout.

## Resume safely

1. Preserve the existing tree and review `git-status.txt`. Extract the snapshot into a separate directory for comparison, not over user work.
2. Reconcile the intended branch and prior PR work. This checkout uses AegisBrowser.Lab, not the later PR #7 SecurityLab implementation. Do not treat historical CI as evidence for this source.
3. On a compatible .NET 8 host run the repository restore/build/console commands. On Windows run the existing `.github/workflows/windows.yml` suites, including native browser phases, filtering, privacy restart, publish and packaging. Packaging requires a clean reviewed source commit; never clean/reset to satisfy that requirement.
4. Record exact revision/run/artifact evidence only after execution. Leave manual acceptance pending and do not merge automatically.

## Actual verification

Seven static security gates, five Python audit tests, seven project/props XML parses, diagnostics JavaScript syntax and git whitespace checks passed. The .NET restore/build/tests could not start: SDK missing. Native tests, publish, SBOM for a real payload, ZIP application packaging and Windows smoke checks remain blocked. Remote query failed with `Could not resolve host: github.com`. No release readiness is claimed.

See `docs/REMEDIATION-REPORT.md`, `docs/TEST-MATRIX.md`, `docs/SECURITY-LIMITATIONS.md` and `BUILD-STATUS.md` in the snapshot for issue scope, residual risks and exact commands.
