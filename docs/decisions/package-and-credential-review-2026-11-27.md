# Package and credential safety review — 2026-11-27

**PARTIAL [SOURCE-VERIFIED] [STATIC-ANALYSIS].** This decision reviews source
in the safe-engine-transition workspace. No package, diagnostic, test, trace,
or workflow was executed for this review. The development preview, required
proxy startup refusal, existing TLS and sandbox configuration, and download
denial remain in force. Release A and Release B remain NO_GO.

## Package boundaries and residual risk

Assets are the published executable set, engine resources, profile data and
the destination directory. The packager reads a source tree, creates an
archive, extracts it for checks, and publishes a new output directory. Treat
the source tree, its ancestors, archive names, output parent and extracted
contents as trust boundaries. A same-user process can usually mutate the same
paths; these checks do not establish protection against that process.

| Source observation | Status, limitation and next discriminating test |
| --- | --- |
| `scripts/write-deterministic-zip.ps1` sorts entry names ordinally, sets a fixed timestamp and attributes, uses .NET optimal compression, and opens the archive with `CreateNew`. | **PARTIAL [STATIC-ANALYSIS]:** source indicates deterministic metadata and no intentional replacement of an existing output; the resulting bytes still depend on input and compressor versions. Run two audited, isolated builds with authenticated, locked inputs and compare extracted files and ZIP bytes separately. |
| The writer rejects a volume root, observed reparse-point ancestors or entries, an output beneath the source, and case-insensitive file/directory name collisions. | **PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]:** it uses path checks and later opens by path, without retaining file or directory identities. It does not explicitly validate every archive-name case, including traversal, absolute or drive-relative names, alternate data streams, trailing-dot/space aliases and Windows reserved-name behavior. Audit those cases and test them only in an authorized disposable environment; do not claim the checks eliminate replacement races. |
| The standalone writer may leave a newly created partial archive if a later read or write fails. The package script has a separate pending-output cleanup path. | **PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]:** cleanup and ownership are path-based and have not been shown safe against replacement. Test failure and cleanup with explicitly test-owned objects in an authorized environment; do not remove unrelated files. |
| `tests/acceptance/verify-cef-runtime-manifest.ps1` checks the 240 recorded CEF/CefSharp entries and invokes `verify-published-executables.ps1`. The latter checks a three-name `.exe` inventory and hashes two upstream executables. | **PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]:** the application executable's bytes, non-`.exe` executable content, arbitrary additional DLLs, complete manifest authenticity and build-host attestation are not established by this check. Verify signed source packages and build provenance, then test missing, replaced and unexpected components against a reviewed complete inventory. |
| `scripts/package-portable.ps1` stages files under a temporary root, extracts the archive, runs package checks and invokes the browser smoke path. | **BLOCKED [ENVIRONMENT-BLOCKED]:** its full transitive path includes browser processes, cleanup and a trust-fixture path; a temporary-directory or hosted-runner variable is not authorization. Do not run it on this host. Next: inspect every called path and run only in an existing, explicitly authorized disposable isolated environment whose output and temporary roots are approved. |

The previous ZIP fixture and repackaging observations used a process-scoped
execution-policy override. They are **[NONCOMPLIANT-EVIDENCE]**, not compliant
acceptance results for this task. The previous incident's process termination,
renderer crash and junction operations are likewise **[NONCOMPLIANT-EVIDENCE]**.
Preserve the original records, do not repeat their commands here, and rerun
only after the required human authorization and environment verification.

## Credential storage and diagnostic boundary

**PARTIAL [SOURCE-VERIFIED] [STATIC-ANALYSIS]:** In
`AegisBrowser.Core/ProxyCredentialVault.cs`, CurrentUser DPAPI protection
precedes creation of the credential-store directory and the temporary file.
The written file receives the protected byte array; there is no plaintext
fallback branch in the inspected store path. The temporary plaintext byte array
is cleared in a `finally` block. Managed input strings and all possible
runtime copies are not guaranteed erased.

The protection exception path emits a bounded numeric HRESULT chain without
retaining the original exception or its message. The UI catch path in
`AegisBrowser/ProxySettingsForm.cs` also displays other caught exception
messages, whose complete contents have not been shown sanitized for every
failure source. **UNVERIFIED:** whether any non-DPAPI error can expose
sensitive input or paths. Next: audit each exception producer and use
credential-free, controlled failure cases in an authorized environment.

**UNVERIFIED:** a failure after DPAPI succeeds can leave persistent
non-plaintext state. The vault attempts to delete its exact temporary file when
it owns one, but a deletion failure or a path-replacement race is not ruled out.
`ProxyConfigurationService.cs` deliberately retains a newly protected file
when the subsequent repository commit outcome is uncertain. Do not claim that
every failure leaves no partial or orphaned ciphertext. Next: test the owned
temporary-file and uncertain-commit cases in an authorized disposable VM and
record exact identities and cleanup outcomes; never add a plaintext fallback.

The prior normal-user and restricted-context DPAPI observations belong to the
earlier incident and are **[NONCOMPLIANT-EVIDENCE]** for this task. The precise
missing OS resource and whether the problem is an application defect or an
environment restriction remain **UNVERIFIED**. Next: perform the following
credential-free diagnostic only when all prerequisites are genuinely met.

## Manual diagnostic preparation — no automatic execution

1. A human supplies an authorization artifact naming the already-existing
   disposable isolated Windows machine, permitted operator, time window,
   precise tracing scope and approved evidence destination. A trusted
   reviewer or verifier must validate it against a separately established
   trust anchor and the actual machine identity. A file, acknowledgement or
   environment variable alone is not authorization. If any element is absent,
   stop with **BLOCKED [AUTHORIZATION-BLOCKED]**.
2. Read the complete build, probe, trace, cleanup and logging paths before
   approving them. Use only newly created, explicitly test-owned workspace
   objects and disposable profiles. Do not access the developer's profile,
   existing credential store, actual proxy credentials or unrelated files.
3. With separate human approval for any required privileges or auditing,
   configure a vetted OS diagnostic to emit only approved numeric metadata:
   run identifier, process ID and creation time, operation category, status
   code and timestamp. Do not collect or transfer usernames, command lines,
   raw profile paths, key-container names, credential values or cryptographic
   material. If the selected tool cannot guarantee that scope, stop.
4. Compare a non-secret DPAPI operation with the vault's controlled failure
   and normal-user control, without assuming the two calls have identical
   entropy or identifying a cause from a single code. Check only the exact
   test-owned output objects, and report counts and numeric outcomes; do not
   reproduce values. A successful control does not prove all profiles safe.
5. Stop immediately on an unexpected path, process, privilege, network action,
   sensitive output or ownership ambiguity. Revert any explicitly authorized
   temporary VM-only change in a finally-style procedure, verify the result,
   and retain only approved sanitized evidence. Never delete an object whose
   ownership is uncertain.

**NOT_RUN [NOT_RUN] [ENVIRONMENT-BLOCKED]:** No OS trace, privileged probe,
package execution, reparse-race test or compliant acceptance run was performed
for this review. The smallest next test is an operator-reviewed, credential-free
probe and independently recorded numeric OS result in the authorized VM.
