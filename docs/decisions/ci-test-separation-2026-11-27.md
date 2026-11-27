# Decision: separate test discovery from authorized security acceptance

Date: 2026-11-27. Status: **PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]**.
This is a source-only design, not an active workflow, runner authorization,
test execution, or security acceptance.

## Existing trigger and execution risk

**BLOCKED [SOURCE-VERIFIED] [STATIC-ANALYSIS]:** The inherited
`.github/workflows/windows.yml` declares unfiltered `push`,
`pull_request` and `workflow_dispatch` events on `windows-latest`.
Its full core suite reaches deliberate fixture-child termination and junction
creation; the native browser write phase deliberately crashes a test renderer.
Its HTTPS fixture can add and remove current-user certificate trust, and other
paths can register AppContainer state, create processes or terminate timed-out
fixtures. Relevant source paths include:

- `AegisBrowser.Tests/Program.cs`, `ProfileRepositoryTests.cs`,
  `ProfileSettingsTransactionTests.cs` and the preset tests.
- `AegisBrowser.BrowserTests/Program.cs` and `HttpsPrivacyFixture.cs`.
- `tests/acceptance/windows-smoke.ps1`,
  `research-appcontainer-launch.ps1` and `scripts/package-portable.ps1`.

The other six workflows are branch-filtered or manual, but several also run
the same core/browser suites, process-lifecycle scripts, package controls or a
WFP experiment. Branch names, environment variables, hosted-runner markers,
preflight results and a successful prior run are **not** authorization.
No current workflow declares a protected environment; all inspected action
references use mutable tags rather than verified commit SHAs.
[SOURCE-VERIFIED] [STATIC-ANALYSIS]

| Workflow | Declared events |
| --- | --- |
| `windows.yml` | push, pull request, manual dispatch |
| `core-dependency-compat.yml` | push, pull request |
| `native-shutdown-stress.yml` | push, manual dispatch |
| `net10-migration-focused.yml` | push, manual dispatch |
| `package-publication-focused.yml` | push, pull request |
| `viewport-restart-focused.yml` | push, manual dispatch |
| `wfp-isolated-once.yml` | push |

Event names alone do not establish whether repository settings suppress or
permit any run; branch filters and server-side policy must also be evaluated.

**Next:** do not push a branch or open a PR if it would trigger the inherited
workflow. Review the complete trigger graph, including any repository-side
rules not visible in source, before delivery. Do not dispatch a workflow or
provision a runner under this task's current constraints.

## Proposed separation

1. **Profile A — development-safe.** A future automatic workflow may run
   only source inspection, formatting, script syntax parsing without script
   execution, artifact/manifest consistency checks, and individually audited
   pure tests or compilation. It must invoke the governance entry point and
   fail when a discovered source/test/script is new, changed, missing,
   unclassified or has unaudited execution paths. Restore, SDK and package
   imports, network access, write locations, compiler tasks, and interpreter
   startup hooks must be audited before compilation is admitted. Existing
   direct legacy script invocation is not intercepted by the provisional
   inventory and cannot be called compliant merely because a catalog exists.
2. **Profile B — isolated Windows acceptance.** A future workflow is manual
   only, targets a dedicated self-hosted Windows x64 label controlled by the
   operator, and uses a protected environment requiring human reviewer
   approval. It may run classified lifecycle, renderer-crash, junction,
   symlink, credential, native-browser and desktop checks only after an
   independently verified human authorization record names the existing
   machine, test scope, allowed operations and expiry.
3. **Profile C — independent containment.** A separate future manual-only
   workflow targets an explicitly authorized pre-existing browser machine and
   a distinct receiver/observer machine. It requires separate human approval
   and documented network, WFP, auditing and evidence-transfer scope. It must
   not provision, start or reconfigure machines and must not fall back to a
   hosted or general-purpose runner if labels are absent.

Protected-environment approvals, runner labels and an authorization record are
separate controls. A label is only a scheduling selector; a YAML declaration
cannot prove GitHub protection rules, runner identity, isolation or approval.
An authorization artifact is not self-validating: its issuer, integrity,
machine binding, scope and expiry need human-controlled verification. Until
that verifier and actual repository settings exist, B and C remain
**BLOCKED [AUTHORIZATION-BLOCKED]**, and the proposed workflows
must refuse to run.

## Configuration requirements and remaining work

- Pin each external action to a reviewed full commit SHA; record upstream source
  and verification date. Current verified SHAs were not obtained here.
  **NOT_RUN [NOT_RUN]:** action-provenance verification. Next: obtain and review
  the exact upstream action commits without executing them.
- Use job-level minimum permissions and disable persisted checkout credentials
  unless a reviewed operation needs them. Do not put secrets or credential-like
  fixtures in workflow text or logs.
- Give B and C only `workflow_dispatch` triggers, dedicated protected
  environments and explicit self-hosted labels; prohibit automatic triggers
  and fallback runners. Verify the actual environment reviewers, branch rules,
  label ownership and machine identity outside YAML before enabling anything.
- Preserve the current assertions and their source; separate or gate execution
  without rewriting failures as skips or passes.
- Keep a human-readable operation plan, allowed filesystem paths, process and
  network behavior, and sanitized evidence for every selected test. Missing
  classification, authorization or runner evidence must fail closed.

**NOT_RUN [NOT_RUN] [ENVIRONMENT-BLOCKED]:** no separated workflow has been
created, statically linted by an audited linter, dispatched or executed; no
protected environment or self-hosted runner has been inspected. Next: complete
the inventory and enforcement implementation, verify action commit provenance
and repository protection settings, and have a human authorize existing
isolated machines before preparing an active workflow. G0 remains PARTIAL.
