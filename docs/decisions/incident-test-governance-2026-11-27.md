# Incident review: test safety and evidence handling

Date: 2026-11-27. Status: **FAIL [NONCOMPLIANT-EVIDENCE]** for the
historical operations described below; the current evidence is not an
independent security review. This record supplements and does not alter the
earlier [baseline decision](baseline-and-ci-events-2026-11-26.md),
[baseline summary](../security/evidence/phase0-baseline-1ee1dd2.txt), or
[threat model](../security/threat-model-2026-11-26.md).

## What happened and affected resources

| Operation | Recorded observation | Classification and status |
| --- | --- | --- |
| Prior core-suite baseline | Two invocations on the development host terminated four test-owned child processes and created and removed six test-owned junctions in temporary fixtures. | **FAIL [NONCOMPLIANT-EVIDENCE]:** process termination and junction operations require the explicitly authorized isolated environment; fixture ownership was not authorization. |
| Prior browser baseline | The write fixture deliberately crashed one test renderer on the development host. | **FAIL [NONCOMPLIANT-EVIDENCE]:** renderer-crash testing requires the explicitly authorized isolated environment. |
| Prior script execution | A ZIP fixture and an SBOM generator were run with two process-scoped PowerShell execution-policy overrides. | **FAIL [NONCOMPLIANT-EVIDENCE]:** the current task prohibits execution-policy overrides in any form. The earlier record reports no machine or user policy change; this is not an exhaustive policy audit. |
| Prior source search | Tool output displayed existing synthetic credential-like fixture literals. The literals are not repeated here. | **FAIL [NONCOMPLIANT-EVIDENCE]:** a synthetic value was displayed despite the redaction requirement. No real credential or token is known to have been exposed; that is not proof that the complete transcript contains none. |
| Current workflow audit | A read-only source-content display included one additional pre-existing synthetic URL-userinfo fixture literal. The literal is not repeated here. | **FAIL [DEVELOPER-VERIFIED]:** source content was displayed without sufficient output redaction. Content-display operations were stopped after the issue was reported. Whether any real credential or token was exposed is not established by this observation. |
| Current backup preparation | Empty-list parsing led to an unintended read-only recursive scan of the root worktree, with reparse traversal disabled. | **PARTIAL [DEVELOPER-VERIFIED] [UNVERIFIED]:** no filesystem writes were made by that scan; its completion is not independently established. It was not an authorization to execute a test or follow reparse points. |

The earlier record says the two metadata suites reached completion after their
junction cleanup blocks; source inspection confirms those blocks are in
`AegisBrowser.Tests/ProfileRepositoryTests.cs`. This supports only those
reported cleanup paths. It does not establish an exhaustive inventory of
processes, temporary data, audit logs, or other host effects.
[SOURCE-VERIFIED] [NONCOMPLIANT-EVIDENCE] [UNVERIFIED]

## Root causes and incorrect assumptions

1. The broad baseline suites were launched before their transitive execution
   paths were inspected. Their entry point calls multiple suites, including
   deliberate child termination, junction operations and renderer-crash
   testing. A suite name, temporary fixture, or successful test result was
   incorrectly treated as sufficient evidence of host safety.
   [SOURCE-VERIFIED] [NONCOMPLIANT-EVIDENCE]
2. The lack of machine-wide policy changes was incorrectly treated as enough
   to justify a process-scoped execution-policy override. The current
   instruction forbids that override even when no permanent setting changes.
   [NONCOMPLIANT-EVIDENCE]
3. Source-search and content-display output was not constrained to redacted
   metadata before execution. Synthetic values were incorrectly treated as
   harmless to display, although the instruction covers credential-like
   fixture literals as well as real credentials.
   [NONCOMPLIANT-EVIDENCE] [DEVELOPER-VERIFIED]
4. Backup enumeration did not robustly distinguish an empty list from a root
   path. The resulting read-only scan was broader than intended; its
   completion remains unverified. [DEVELOPER-VERIFIED] [UNVERIFIED]
5. At the reviewed baseline, environment flags and runner identity checks
   existed for some experimental paths, but those checks did not establish
   human authorization, VM disposal, isolation, or scope. A documented
   preflight is preparation, not permission. [SOURCE-VERIFIED] [STATIC-ANALYSIS]

## Known consequences and unresolved questions

- During the later WebView2 SDK research, a package-download link was opened by
  the research tool before the required download approval. No local package
  file was written by that operation and no package code was executed. Whether
  the research service transferred package bytes is unverified. The lookup was
  stopped. A separate, explicitly approved local download was subsequently made.
  **FAIL [NONCOMPLIANT-EVIDENCE] [UNVERIFIED].** Next: review research-tool
  action classification and obtain transfer metadata if available; do not
  treat the earlier link access as authorization or signature evidence.

- A current official-source search returned an anti-automation redirect whose
  query included a token-like value in raw tool output. The lookup stopped and
  the value is not reproduced here. No repository credential is known to be
  involved; transcript retention and downstream exposure are unverified.
  **FAIL [NONCOMPLIANT-EVIDENCE] [UNVERIFIED].** Next: an authorized reviewer
  should assess retention of the affected output without reproducing its value;
  use sanitized metadata for subsequent source research.
- A subsequent check reopened the same redirecting public release endpoint
  despite the earlier decision to stop that lookup. Its output was filtered
  before display, but the repeat was unnecessary. The endpoint remains blocked
  for this investigation; use other official records and report gaps instead.
  **FAIL [NONCOMPLIANT-EVIDENCE].** Next: review the delegation handoff and
  ensure stopped endpoints are included in the active operation blocklist.

- The operations and output disclosures above are recorded; no credential-like
  values are reproduced in this decision. Their historical test results must
  not be converted into compliant acceptance results.
  [NONCOMPLIANT-EVIDENCE]
- The earlier report states that system trust, firewall, WFP, and persistent
  execution-policy settings were not changed. This is an engineering report,
  not an independently established exhaustive host-state comparison.
  [NONCOMPLIANT-EVIDENCE] [UNVERIFIED]
- Transcript retention, any additional sensitive values, the full set of host
  side effects, and completion of the unintended scan are **UNVERIFIED**.
  Next: an authorized reviewer should inspect retained event metadata and
  affected fixture roots read-only, using redaction and without displaying
  source literals. No destructive cleanup is authorized by this document.
  [AUTHORIZATION-BLOCKED]

## Prevention decisions and remaining actions

- Before execution, inventory each test, script, workflow, build target and
  transitive command. Unknown operations are blocked; classify process-kill,
  crash, junction, symlink, destructive cleanup, trust and policy operations
  as requiring the appropriate existing authorized isolated environment.
  This is a requirement, not a claim that enforcement is already complete.
  [SOURCE-VERIFIED] [UNVERIFIED]
- Implement and test a fail-closed governance entry point, including
  unclassified-test refusal. An environment variable, an agent-authored
  acknowledgement, a successful preflight, or a runner label is not human
  authorization. Profiles requiring isolated environments need a
  human-supplied, verifiable record of machine identity, scope and expiry.
  [AUTHORIZATION-BLOCKED]
- Audit direct test/script and workflow entry points as well as wrappers.
  Do not create or start VMs, provision runners, dispatch workflows, change
  endpoint protection or networking, terminate processes, manipulate
  reparse points, or override PowerShell execution policy on this host.
  [SOURCE-VERIFIED]
- Use filename-only or count-only searches, constrained patterns, and
  sanitized summaries for credential-adjacent code. Treat source content,
  command output, transcripts and generated artifacts as potentially
  sensitive; never reproduce fixture literals in evidence.
  [DEVELOPER-VERIFIED]
- Harden backup enumeration so an empty result cannot become a root scan;
  preserve all existing work and record backup scope without printing file
  contents. Verify the implementation with pure synthetic inputs before use.
  [UNVERIFIED]
- **NOT_RUN [ENVIRONMENT-BLOCKED]:** compliant reruns of hazardous baseline
  and native acceptance are unavailable without an existing authorized
  disposable isolated Windows environment and a human authorization record.
  Next: establish those prerequisites and an approved evidence-transfer path,
  then run only the explicitly approved scope.

G0 remains **PARTIAL** until refusal behavior and compliant operation are
demonstrated in use. The missing environment and human record remain blockers;
this decision does not grant authorization. Release A and Release B remain
NO_GO. [UNVERIFIED]
