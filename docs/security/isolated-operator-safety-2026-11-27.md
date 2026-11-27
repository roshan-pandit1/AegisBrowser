# Isolated acceptance: operator safety supplement

**Status: NOT_RUN [ENVIRONMENT-BLOCKED] [AUTHORIZATION-BLOCKED].** The
existing [two-VM checklist](isolated-vm-acceptance-checklist.md) and
[containment procedure](cross-vm-network-test.md) describe the technical
fixtures. This supplement adds the authorization and execution boundaries for
the present work. It does not authorize or launch a test.

1. A human must supply an approval record for the exact source commit, operation
   identifiers, machine identities, scope and expiry, as described in
   [the authorization design](../decisions/human-authorization-artifact-2026-11-27.md).
   Establish the human-controlled trust anchor and a reviewed verifier first.
   Approval flags, runner labels and a successful preflight are not authorization.
2. Verify by an independent administrative channel that the Windows x64 machines
   already exist, are disposable, and match the approved identities. Record OS
   version, architecture, installed software and tool versions, workspace and
   output locations, isolation, disposal procedure, and any required privileges.
   For network containment, separately verify the isolated receiver, topology,
   controlled endpoints, WFP/audit scope and evidence-transfer path. Missing or
   unverifiable items stop the dependent operation.
3. Review the inventory and the exact source of each intended test and its
   transitive execution paths. The inventory contains UNKNOWN entries and does not
   itself intercept direct script execution. An operator must not execute an
   unknown operation; a separate reviewer must approve its classification. Record
   expected process operations, filesystem writes, network effects, cleanup and
   privileges for each operation. Do not perform these tests on the development
   host.
4. Before any acceptance operation, verify the commit and dependency hashes using
   independently authenticated inputs and save sanitized evidence in the approved
   evidence location. A hash comparison is not publisher authentication or build
   attestation. Do not capture private values, page contents or authentication
   material in logs.
5. Read-only preflight consists of checking the signed approval and its scope,
   viewing machine identity and environment metadata, and reviewing source and
   inventory without running fixtures, changing settings, starting listeners or
   mutating files. No automatic hazardous operation follows it. There is no
   qualified automated authorization verifier in this branch; this preflight is a
   manual procedure and cannot qualify a B or C run by itself.
6. An explicitly authorized operator must separately approve and invoke each
   reviewed acceptance operation. Preserve a contemporaneous operation record,
   source SHA, environment and sanitized outputs. Sandbox evidence needs an
   independently identified renderer, its external token inspection, and an
   independently recorded file-access denial with a working control. Containment
   evidence needs a working positive control, OS filtering decision and independent
   receiver observation per traffic class. Do not replace these with a browser
   callback, timeout or absent packet.
7. Stop immediately if a machine, endpoint, source hash, approval, test behavior or
   cleanup action differs from the reviewed scope. Preserve sanitized evidence and
   ask the human owner to decide the next step. Clean up only objects the approved
   operation created, using its reviewed ownership procedure; record and verify
   cleanup before environment disposal. Never delete unrelated objects.

This supplement prepares an operator workflow; it is not evidence that a machine
is isolated, a person approved an operation, or any security gate passed.
