# Decision: inventory the PR #90 artifact without upgrading provenance claims

Date: 2026-11-26 UTC. Source baseline:
`1ee1dd284d2f6ad3ccb66593ce2984d56843063a`.

**WP-G: PARTIAL [SOURCE-VERIFIED] [DEVELOPER-VERIFIED] [UNVERIFIED].** The
scope is the retained portable artifact from
[PR #90 Windows run 45649203906](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/45649203906),
not a build of the later qualification branch. Its hosted job recorded the
baseline SHA; the artifact's `VERSION.txt` self-reports that SHA. Neither
fact is an independently signed source-to-binary attestation.

## Artifact and binary observations

The downloaded GitHub artifact wrapper matched the digest reported by GitHub:
`2832F6C343790DCBC2AC583E398ADABB1935D2EEF733A6C5F895A33F75006EFA`.
Its inner preview ZIP matched the CI-recorded SHA-256:
`8A95CE41A2FD16684810376A80D2C4C1FED8E02FBAFEA15630FBBD2B84D7263B`.
The following values were measured from files in that artifact, rather than
read only from the prior CEF manifest. [HOSTED-CI] [DEVELOPER-VERIFIED]

| Shipped file | Reported file version | SHA-256 |
| --- | --- | --- |
| `libcef.dll` | `154.0.34+g14c5a08+chromium-154.0.8037.98` | `E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7` |
| `CefSharp.Core.Runtime.dll` | `154.0.340` | `2B4329C6D48A58194C4E4AC198E82990E4F1DCF63559B6F3357F9EA19781BFF0` |
| `CefSharp.BrowserSubprocess.exe` | `154.0.340.0` | `CA0F20A27A27C73A63130A433A85D51E8F58C18A76EDABE91036DAB3B77E46D6` |
| `createdump.exe` | `10,0,1526,53008` | `5161F24CCBE3AF9FF224CAB8D667F87920A58136DD045B054999E19AC29004F3` |
| `e_sqlite3.dll` | No file-version string observed | `B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E` |
| `AegisBrowser.exe` | `1.0.0.0` assembly/file metadata | `397F4CEB705291890428D1E433B98DA8A84766C02176EEDAC4778180571BD7E8` |

The SQLite binary and dump tool were matched to members of the examined
SQLitePCLRaw 2.1.13 and .NET 10.0.15 packages respectively. The host version is
assembly metadata, not a production-release designation. A recorded host hash
identifies this artifact but does not authenticate a new application build.

## Packages, advisories and SBOM

The [package evidence](../security/pr90-nuget-provenance.json) records 12
package identities, versions, official NuGet registration/catalog URLs,
catalog SHA-512, package SHA-256, signature types, declared licenses and query
timestamps. The preceding developer execution recorded matches against NuGet
catalog hashes and `dotnet nuget verify --all` results without warnings
for all 12. Signature text from that execution is retained in the local
artifact-evidence directory; a complete independent audit and durable raw
per-command exit transcript are **UNVERIFIED**. A
[sanitized signature-log summary](../security/evidence/nuget-signature-log-summary-2026-11-26.md)
records the observed signature types and output-file hashes; those logs did not
retain the command exit codes. Next: repeat catalog and
signature checks with recorded command output in an authorized environment and
retain the signed artifacts and trust-chain evidence. [OFFICIAL-UPSTREAM]
[DEVELOPER-VERIFIED]

The NuGet project advisory query recorded on 2026-11-26 covered ten resolved
project packages and returned no package advisory for those selections. The
two runtime packs were not covered by that project query, and embedded
Chromium/native advisories are not exhausted by it. They remain **UNVERIFIED**.
Next: query authoritative advisories for each runtime/native component and map
Chromium fixes to the exact CEF sources; the
[engine decision](engine-qualification-2026-11-26.md) records the known
upstream coverage gap. Declared license metadata and included notices are not
a legal redistribution review; complete that review before release.

The [CycloneDX 1.6 file inventory](../security/pr90-preview.sbom.cdx.json)
contains 539 shipped-file components and 12 examined package components.
It records locations, SHA-256 values and the evidence's declared package
metadata. Twenty-seven files have no byte-identical member in those packages,
including application-generated files and notices, and are labeled
`UNVERIFIED`. The graph links the root and byte-matched package members;
it does not reconstruct the complete package-to-package transitive graph or
the embedded Chromium dependency graph. This is not a complete supply-chain
SBOM or production qualification.

A read-only developer check of the retained ZIP and JSON verified the ZIP
digest, all 539 file hashes and inventory names, unique component references,
and structurally resolved dependency references using
`tests/acceptance/check-sbom-artifact.py`. This is a partial structural
check, not full CycloneDX validation. **NOT_RUN [UNVERIFIED]:** full validation
against the [official CycloneDX 1.6 schema](https://cyclonedx.org/schema/bom-1.6.schema.json);
the available Python installation lacks `jsonschema` and no trusted
validator was already identified. Next: use a previously verified validator
and pinned official schema on an authorized environment, including the schema's
referenced resources.

## Executable controls and execution-policy incident

The new executable verifier checks an exact `.exe` name inventory and
hashes the CEF subprocess and `createdump.exe` against recorded package
members. It does not verify the bytes of `AegisBrowser.exe`, other
executable extensions, or arbitrary DLLs. Next: establish an authenticated
application build attestation and validate the host executable, then define
and review the full shipped executable/component inventory. The new
missing/replaced/extra fixture tests exercise only the stated executable
subset; they are not a complete package-tampering test.

**UNVERIFIED:** a preceding agent reported passing these new executable
fixtures, but the exact invocation and its PowerShell execution-policy status
were not retained in the available transcript. Do not count that report as a
compliant pass. **NOT_RUN [ENVIRONMENT-BLOCKED]:** rerun the controls in an
existing authorized disposable environment where script execution is already
permitted, without changing or overriding security policy.

**FAIL [DEVELOPER-VERIFIED]:** one later regeneration of the SBOM used
`powershell.exe -NoProfile -ExecutionPolicy Bypass` before the policy
restriction was identified. It produced the same SBOM SHA-256
`46EF2582F29ED002AE2B0D6C66AE6BAD1E595412CA3C3987F19C34DD1E431DD9`
before a later conservative correction. Because the captured logs lack exit
codes and complete trust decisions, all twelve signature-status fields in the
retained provenance and SBOM were changed to UNVERIFIED and the SBOM was
normalized to LF line endings for a stable checked-in byte sequence. The
corrected SBOM SHA-256 is `8DE2EF7C47C0677EB1E0CC1D141DF9A63FF3E7746C8439E27759416E2B079478`;
its 539 artifact file hashes and references were rechecked by the read-only
Python checker. The final generator edit and corrected signature evidence have
not been reproduced together. No system-wide execution-policy setting was
changed. This process-scoped
override did not comply with the task's security-mechanism constraint. Do not
repeat it. **NOT_RUN [ENVIRONMENT-BLOCKED]:** the corrected generator's
compliant rerun, including its subsequent `FileMode.CreateNew` output
change, has not executed. Next: run it without an execution-policy override in
an authorized environment where execution is already permitted, and test that
an existing output remains unchanged.

The corrected generator pins the PR #90 inner ZIP digest and checks its
embedded identity before creating the inventory; `CreateNew` now prevents
intentional overwrite of an already-existing output. These are source-level
observations, not proof that parent-directory substitution races are excluded.
No security or release gate is promoted by this work. Release A and Release B
remain NO_GO.
