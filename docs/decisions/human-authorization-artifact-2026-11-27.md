# Human authorization artifact: proposed design

Date: 2026-11-27. **BLOCKED [STATIC-ANALYSIS] [AUTHORIZATION-BLOCKED]:**
this is a design, not an implemented verifier, configured trust anchor, runner
authorization, or permission to perform an operation. Profiles B and C must
remain denied until the verifier, trust policy and required human-provided
evidence are established and tested. An agent-authored file, environment
variable, configuration flag, runner label, successful preflight, or this
decision alone is not authorization.

Profile A may use only individually audited development-safe operations
under the current workspace restrictions. Unknown or unclassified operations
remain denied; an authorization artifact does not change their classification.
No production-security gate is passed by this proposal. G0 remains PARTIAL
until refusal behavior and compliant use are demonstrated.

## Authorization record to be reviewed and signed

A human-authorized reviewer would approve a versioned, non-secret record whose
exact signed content binds all of the following. This list is a schema
requirement for a future reviewed implementation, not an executable example.

| Required field | Bound meaning |
| --- | --- |
| Schema version and unique authorization ID | A recognized format and an identifier suitable for audit, revocation and single-use tracking. |
| Human reviewer/issuer identity and signing-key ID | The human and the specific key or authenticated approval identity recognized by a separately provisioned trust policy. The record cannot nominate its own trusted issuer or key. |
| Profile and exact operation IDs | B or C, the reviewed test-inventory revision and digest, and explicit operation IDs and sub-operations. Wildcards or a generic acknowledgement do not authorize unspecified operations. |
| Source and input identities | Exact repository source commit, relevant executable/script and dependency hashes, and an approved evidence-plan digest. A different revision or input requires a new authorization. |
| Machine identities and roles | For B, the exact existing Windows VM and role; for C, both the browser VM and independent receiver/observer VM, their roles, and the intended private-network identity. Bind identities to an independently verified asset or attested identity, not merely a hostname, MAC address, environment variable or caller-supplied label. |
| Limits and intended effects | Authorized fixture paths, endpoints and traffic classes, network-policy/auditing scope, required privileges, cleanup ownership, and emergency-stop conditions. A record does not authorize creating or provisioning machines or changing scope. |
| Time and run binding | Issue time, not-before time, expiry, and an authorized run ID or verifier-issued one-time challenge. All times are UTC; missing, malformed, expired or inconsistent values deny execution. |

Do not put credentials, private keys, bearer tokens, cookie values, or
credential-like fixture literals in the record, reports or logs. Treat asset
identifiers and authorization metadata as potentially sensitive. A retained
evidence summary should contain only the minimum reviewed identifiers,
artifact digest, scope decision, timestamps and numeric/status outcomes.

## Trust and verification requirements

1. A human must establish the trusted reviewer/signing policy and its public
   trust anchor through a channel outside the agent's control. The verifier
   must read that policy from an appropriately protected, pre-provisioned
   source. No human-provided trust anchor or approval service has been
   established for this task; therefore B and C deny by default.
   [AUTHORIZATION-BLOCKED]
2. Verify the signature using a reviewed implementation and an algorithm
   explicitly allowed by the external trust policy. Bind the signer/key ID,
   schema and algorithm to the signed content; reject unsigned records,
   untrusted signers, algorithm substitution, malformed data, ambiguous or
   duplicate fields, unsupported schema versions, and any signature failure.
   No cryptographic implementation or trust-chain result is claimed here.
3. If JSON is selected, use a specified canonical representation such as
   [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) before signing and
   verifying; reject input outside the supported schema. An alternative
   authenticated format requires its own reviewed, unambiguous encoding.
4. Compare every signed source, inventory, operation and machine binding to
   independently obtained current facts before starting. A machine claim
   supplied by the record or by a process on that machine is not independent
   proof of identity. If the binding cannot be authenticated, deny. For
   Profile C, both roles and the approved evidence-transfer path must match.
5. Verify the time window against a trustworthy clock and fail closed if time
   validity cannot be established. Check the record against a human-controlled
   revocation source and a protected single-use/run ledger; if either status
   cannot be verified when required by policy, deny. A nonce alone does not
   prevent replay without authoritative state.
6. A reviewer-approved protected workflow environment could be an approval
   channel only if its trusted approval record is independently authenticated
   and binds the same artifact digest, commit, operation, machines, scope and
   expiry. A workflow approval or self-hosted label alone is insufficient.
   There must be no fallback to a different runner or automatic trigger for
   Profiles B and C.
7. Verify the operation remains within authorization immediately before each
   hazardous step. If it expires, is revoked, changes scope or loses a required
   control, refuse new hazardous steps and follow only the previously reviewed
   emergency-stop/cleanup authority. Do not assume this document authorizes
   process termination, deletion, policy changes or network activity.

The authorization record can establish that a trusted human approved a
specific signed scope only if the verifier, trust anchor, time and revocation
checks are themselves trustworthy. It cannot prove that a machine is
disposable, that two machines are isolated, that a receiver is independent,
that a process is sandboxed, or that an OS access decision occurred. Those
facts require separate evidence and, where appropriate, human inspection or
platform attestation. A compromised user account, same-user process or
administrator able to replace the verifier or its trust policy may defeat a
workspace-level check; this is not an OS security boundary.

## Replay, revocation and evidence limitations

**UNVERIFIED:** no protected ledger, revocation channel, trusted clock,
reviewer trust anchor, machine-attestation mechanism or approval-service
binding has been supplied. A short expiry reduces exposure but cannot by itself
prevent replay or prove revocation. Offline operation makes revocation
especially dependent on a separately authenticated, current status record.
Next: a human security owner must select and provision those controls outside
the agent's authority; implement and review a verifier; then demonstrate
default-deny behavior, invalid/expired/revoked/replayed-record refusal,
wrong-machine and wrong-operation refusal, and valid-scope handling using
sanitized synthetic records in an approved environment.

Do not describe a signature check, preflight, human approval, or successful
test as independent containment or sandbox evidence. Only the separate
receiver/OS-decision and renderer file-denial/control procedures can support
those respective claims. Production readiness remains BLOCKED; Release A and
Release B remain NO_GO. [UNVERIFIED]
