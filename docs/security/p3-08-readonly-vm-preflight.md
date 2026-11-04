# P3-08: read-only isolated two-VM readiness preflight

**Status: RESEARCH ONLY — INFRASTRUCTURE UNAVAILABLE / NOT EXECUTED.** This change adds an inspection helper, not WFP enforcement or a browser network policy. No actual isolated two-VM network, private-address ownership, packet-attribution instrument, or fail-closed behavior has been verified. Production required-proxy profiles remain blocked before CEF initialization.

## Why this exists

The [cross-VM TCP research](cross-vm-network-test.md) requires an independently approved pair of disposable Windows x64 VMs on a truly isolated virtual switch, with neither public/corporate reachability nor a route to unrelated systems. The TCP fixture's opt-in acknowledgement and RFC1918-only endpoints do not prove the virtual topology. A read-only observation from **each** VM makes some dangerous mismatches visible before attempting any packet or policy experiment.

The preflight **does not** send packets, probe peers, install WFP filters, change firewall settings, set network categories, create AppContainer identities, run CEF, start the cross-VM receiver, or alter OS routes. Its only durable output is a new local JSON evidence file. It must not run on the normal development workstation.

## Prerequisites

An authorized operator first provisions two disposable Windows x64 VMs and independently verifies:

- Dedicated virtual switch with no uplink/NAT, outside route, public/corporate interface or unrelated tenants; document the hypervisor topology and VM disposal plan. A local routing table alone cannot prove this.
- Approved, distinct canonical RFC1918 addresses for VM A and VM B, and a reserved test-only TCP port 1024–65535. Verify network categories and existing firewall conditions without changing them to force a passing result.
- Reviewed source revision and dedicated test account. Use fresh run identifiers and synthetic profiles only, and never store credentials or browsing contents in evidence.
- An existing, unlinked, operator-owned parent directory on **each** VM for a new, never-before-created evidence directory.

The helper deliberately blocks a local snapshot with any IPv4 or IPv6 default route, non-link-local IPv6 address, unconfirmed Private network category, disabled/unavailable firewall profile, inactive adapter, non-preferred target address or non-unique address assignment. These conservative checks may block an otherwise authorized lab until its topology is reviewed. They are not a mathematical proof that routing is isolated, and they do not inspect every hypervisor setting or firewall rule.

## Run from the audited repository on each disposable VM

Set the acknowledgement in the *test-only process environment* on each VM:

~~~powershell
$env:AEGIS_DISPOSABLE_VM_NETWORK_TEST = 'I_UNDERSTAND'
~~~

On VM A, substitute the operator-reviewed private addresses, port and new evidence path:

~~~powershell
./tests/acceptance/inspect-cross-vm-preflight.ps1 -Role VmA -LocalAddress <VM-A-private-IPv4> -PeerAddress <VM-B-private-IPv4> -Port <port> -EvidenceDirectory <new-VM-A-evidence-directory> -AcknowledgeDisposableIsolatedNetwork
~~~

On VM B, use the **same endpoint pair reversed**:

~~~powershell
./tests/acceptance/inspect-cross-vm-preflight.ps1 -Role VmB -LocalAddress <VM-B-private-IPv4> -PeerAddress <VM-A-private-IPv4> -Port <port> -EvidenceDirectory <new-VM-B-evidence-directory> -AcknowledgeDisposableIsolatedNetwork
~~~

Each writes only its own vm-network-preflight.json into a newly created directory. Neither script deletes the evidence. Inspect files for environment metadata before sharing, and retain them under the lab's access policy.

- BLOCKED_PRECHECK: One or more visible local prerequisites failed. Do not proceed; investigate without modifying the host's network on behalf of the tool.
- REVIEW_REQUIRED: No *listed local heuristic* failed. This is **not** verified isolation, authorized execution, successful cross-VM networking or packet containment. The operator must independently examine both snapshots, the virtual switch, NAT/uplinks, routes, VM identities, firewall rules, off-host policy, disposal plan and reachability restrictions before authorizing the next experiment.
- Any exception or missing evidence: classify the environment as unavailable/inconclusive. Never silently substitute a normal workstation or public endpoint.

Do not send packets on the basis of a REVIEW_REQUIRED status alone. Two snapshots are not a signed peer handshake and do not establish separate VM identity, system-wide absence of external routes or correct WFP enforcement.

## Offline automated validation

Windows CI runs the non-network regression checks:

~~~powershell
./tests/acceptance/test-cross-vm-preflight.ps1
~~~

The test checks RFC1918 and endpoint restrictions, conservative snapshot flags and refusal to start without explicit acknowledgement. It does not request network access or administrator rights, and CI does not exercise the live VM inspector. CI green is evidence of input-validation behavior only, not of an isolated two-VM deployment.

## Follow-on gates

After an authorized operator independently signs off both disposable VM environments, run the ordinary cross-VM TCP positive control and separately confirm receiver-side observation before considering the capability-verified AppContainer control. Missing, delayed or ambiguous observations are INCONCLUSIVE. The independent policy-decision, unaffected-identity, UDP/IPv6/DNS, service/BFE failure, rollback and fail-closed tests remain outstanding. The [isolated VM checklist](isolated-vm-acceptance-checklist.md), [WFP lifetime analysis](wfp-fail-closed-lifetime.md) and [containment threat model](network-containment-threat-model.md) remain authoritative gates. Do not turn on production proxy routing.

## P3-08B topology and evidence review

VM A is a disposable Windows x64 synthetic client and token observer; VM B is a separate disposable Windows x64 receiver that does not forward traffic. Record VM identities, reviewed commit, snapshots and disposal plan. An authorized operator must independently inspect the dedicated switch, all uplinks, NAT, VPNs, routing, unrelated machines, firewall rules and the authorized port, then explicitly approve the experiment. Neither an acknowledgement nor these local records is that approval. No authorized pair is currently available: `LIVE_CROSS_VM_VALIDATION: BLOCKED ? INFRASTRUCTURE_UNAVAILABLE`.

Version 2 snapshots include adapter, address, route and firewall profile inventories. An additional active adapter or a gateway route blocks the precheck. On-link routes may reach other systems; inactive or persistent VPN configuration, virtual switch topology, hidden paths and firewall rule contents need independent review. Collection is not atomic and cannot detect all configuration changes: capture fresh separate evidence before and after the experiment, compare it, and stop on changes or uncertainty. Local metadata can be sensitive and is not authenticated. Use trusted operator-controlled storage and transfer; the path checks and exclusive file creation do not defend against concurrent malicious replacement of parent directories. Never use shared or untrusted directories.

Compare both records manually: roles must be VmA and VmB, addresses must be reversed, ports and reviewed revisions must match, timestamps must be plausible, and independent VM identity and topology must be established. Missing, stale, altered or inconsistent records require `BLOCKED_PRECHECK` or `INCONCLUSIVE`; a clean record remains `REVIEW_REQUIRED`. The snapshot has no run identifier and is not a signed peer handshake.

After independent authorization, follow [the existing TCP runbook](cross-vm-network-test.md): start VM B's receiver, run VM A's ordinary control, verify the matching receiver observation and successful client exit, then run the suspended-token-verified AppContainer control and verify the receiver completes successfully. Component records are not whole-experiment certification. Missing positive controls, failed client or receiver, duplicate or inconsistent evidence and missing packet decisions cannot establish a block. The offline receiver validator can be exercised with `./tests/acceptance/test-private-network-evidence-offline.ps1`; its synthetic fixtures test rejection only. It does not validate VM roles, full client/receiver correlation or receiver completion. Those remain manual gates and future automation work. `FIXTURE_FAILURE`, `INFRASTRUCTURE_UNAVAILABLE`, `INCONCLUSIVE`, and independently observed delivery must remain distinct; `BLOCKED_BY_INDEPENDENT_POLICY_EVIDENCE` is unavailable without independently attributed policy decisions and healthy controls.

Future WFP research requires scoped policy identity, independent decision and flow attribution, unaffected controls, IPv4/IPv6, TCP/UDP and DNS/delegated services, proxy interruption, session and BFE loss, browser/broker crash, cleanup and recovery. A dynamic session removes its filters on closure and alone cannot establish fail-closed behavior. No filters, live controls or fail-closed experiments were executed in P3-08B.

## P3-08C protocol review (before implementation)

Existing version 2 sender and receiver JSON records share run and transaction IDs and endpoints; the AppContainer stage validates an ordinary sender completion and receiver observation, then verifies the suspended token before resume. Receiver duplicate detection is bounded to its listening window. Receiver completion is currently an operator-observed process outcome, not an authenticated record. Records have no cryptographic source authentication, role-bound key, durable replay ledger, or end-to-end verifier. The snapshot role label is an assertion, not proof of a separate VM. Version 1 lacks a run ID and is rejected by the existing stage gate; version 2 remains unauthenticated and is never silently upgraded. PR #46's existing controls remain necessary.

VM A, VM B and the verifier are distinct trust domains. Independent key pinning can authenticate bytes to a key, but does not prove a VM is uncompromised, a receiver is honest, a network flow belongs to a process, or WFP enforced a policy. A nonce or a checksum next to a file authenticates neither its producer nor freshness. A reliable replay ledger requires trusted, durable, concurrency-safe verifier state and a recovery procedure; timestamps and signatures alone do not supply that state.

### P3-08C authenticated-record prototype — incomplete acceptance

`tests/acceptance/p308c-authentication-lib.ps1` contains an **in-memory verification primitive**, not a finished evidence workflow. It neither reads files nor exports a command-line verifier, signs fixture output, creates a completion record, correlates the two stages, stores replay state, or produces an allowed/blocked classification. Every successfully authenticated record returns `INCONCLUSIVE`. Do not use it to approve a live experiment. Existing v2 JSON is unchanged and remains unauthenticated.

The experimental payload is ASCII, exactly twelve ordered `name=value` lines each terminated by LF (including the last), with no BOM, CR, JSON, extensions, duplicate fields or alternate encodings: `protocol`, `version`, `algorithm`, `role`, `type`, `run`, `transaction`, `client`, `receiver`, `port`, `transport`, `observedUtc`. Fixed values are `AegisCrossVmEvidence`, `1`, `RSA-PKCS1-SHA256`, and `IPv4/TCP`; GUIDs use lowercase 32-character N format; timestamps use UTC `yyyy-MM-ddTHH:mm:ss.fffffffZ`; addresses are canonical RFC1918 IPv4 and port is 1024–65535. A detached raw RSA PKCS#1 v1.5 SHA-256 signature authenticates the exact payload bytes. The function requires a caller-supplied RSA key of at least 2048 bits and separately supplied expected role, run, transaction and endpoints. These expectations are not read from the record. Malformed or mismatched fields and invalid signatures are rejected. The format contains no process completion, receiver completion, token result or trustworthy freshness information, so it is intentionally insufficient for evidence correlation.

A future integration must independently provision and pin distinct VM A and VM B public keys through an authenticated operator channel; it must not accept a bundle-provided key as trust. Protect private keys within each disposable VM and never export them with evidence. The prototype does not manage certificates, verify key fingerprints, detect two roles using the same key, provision signing identities, or implement rollover/revocation. Do not invent a signing or key-distribution procedure until those controls and operational review exist. Synthetic tests generate non-persistent test keys only; no key is logged or stored as an artifact.

Signatures provide integrity and key authentication only within that trust model. A compromised producer can sign false observations, and a valid old signature can be replayed. `observedUtc` is syntax-checked only, not proof of clock synchronization or freshness. Run-scoped IDs alone do not prevent replay. There is no trusted durable replay ledger or safe recovery/concurrency protocol, and there is no operator-owned path or reparse-point handling in this primitive. File-level input, size, link and permissions checks, concurrent state updates, rollback detection, receiver process completion, cross-VM role separation and positive-control correlation remain open implementation gates. Use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/acceptance/test-p308c-authentication.ps1` to run only the offline synthetic primitive tests. The process-scoped execution-policy option does not change system policy.

No result from this prototype represents a WFP decision or independently attributed flow. Historical controls remain subject to the manual gates in the cross-VM runbook. `LIVE_CROSS_VM_VALIDATION: BLOCKED — INFRASTRUCTURE_UNAVAILABLE`.

### P3-08C2 offline research libraries

The P3-08C2 research libraries define a new, separate version 2 **signed ASCII protocol**, distinct from the historical unsigned JSON version 2. Historical JSON is not accepted as signed evidence. `p308c2-record-lib.ps1` checks a fixed sequence of LF-terminated ASCII fields and RSA PKCS#1 v1.5 SHA-256 signatures. It supplies signing and verification functions accepting RSA objects; keys and signatures are not embedded in the payload. Fingerprints are lowercase SHA-256 of ASCII `Aegis-RSA-Public-v1` followed by LF, `modulus=<base64>` followed by LF and `exponent=<base64>` followed by LF, using public RSA parameter bytes in unsigned big-endian form. The verifier requires separate operator-supplied RSA public keys and independently authenticated fingerprints and rejects identical keys, weak keys, and mismatched pins. The operator must authenticate each fingerprint outside the evidence transfer channel and protect the private keys on their respective VMs. Key rotation or revocation requires a new independently authenticated operator configuration; never derive trust from a bundle or automatically replace a key.

`p308c2-file-lib.ps1` provides bounded explicit reads for payload and raw signature files; `Import-P308c2FileRecord` yields the pair expected by the correlation library. It rejects reparse points found along the path, but a hostile process able to replace paths concurrently remains outside its trust boundary. Use operator-owned directories and a trusted OS. Private keys must not be written to evidence directories. No existing network fixture has been changed to sign or export these records, and there is no complete production-ready provisioning or command-line operator workflow. Do not treat existing unsigned fixture records as authenticated.

`Invoke-P308c2OfflineCorrelation` requires five authenticated claims (ordinary client and receiver, AppContainer client and receiver, receiver completion), explicit endpoints, run and two distinct transactions, keys, pins, declared time window and an already initialized operator-owned replay ledger. It verifies role, stage, endpoint and identity consistency, successful client and receiver claims, bounded duplicate claims and a signed token-verification-before-resume assertion. It checks ordering on each producer's clock; clocks and timestamps are not proof of freshness or causality across VMs. The signed schema does not establish the capability SID or receiver startup/window boundaries, and the assertion is not an independent token observation. The current record format, signing integration and runbook must be extended before live acceptance.

`Initialize-P308c2Ledger` creates a new ledger without overwriting an existing file. The correlation function checks and appends all three IDs with exclusive access and flushes before returning `AUTHENTICATED_AND_CORRELATED`. It refuses missing, locked, malformed, torn or duplicate state; it does not repair state. If a crash occurs after the durable append and before the result is received, the run is consumed: preserve the ledger and investigate manually. A privileged attacker or restored snapshot can roll the ledger back or truncate it on a complete-record boundary; external tamper-evident storage and independently controlled backups are required to detect this. Protect the operator-owned state and its directory; signatures cannot protect against a dishonest or compromised producer.

The correlation result means only that authenticated assertions satisfy this offline contract and the ledger consumed their identifiers. It is not proof that network traffic occurred, that a receiver was honest, or of process-to-packet attribution or a WFP decision. This work does not establish fail-closed policy or authorize live traffic. `LIVE_CROSS_VM_VALIDATION: BLOCKED — INFRASTRUCTURE_UNAVAILABLE`.

Signed payload fields appear in this exact order: `protocol`, `version`, `algorithm`, `role`, `type`, `stage`, `run`, `transaction`, `client`, `receiver`, `port`, `transport`, `observedUtc`, `pid`, `createdUtc`, `completedUtc`, `exitCode`, `clientResult`, `tokenVerifiedBeforeResume`, `packageSid`, `resumed`, `timedOut`, `observationCount`, `sourceAddress`, `sourcePort`, `duplicateWindowSeconds`, `duplicateStatus`, `receiverExit`, `receiverError`. Fields are `name=value` ASCII lines ending in LF including the final line; `-` means not applicable. The implementation and offline tests define the allowed values and field applicability. The role is an authenticated key-holder assertion, not proof of an independent VM. Sender process IDs are accompanied by creation time, but neither establishes flow attribution.

Run the new offline suites in Windows PowerShell with process-scoped execution-policy bypass if required:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/acceptance/test-p308c2-record.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/acceptance/test-p308c2-files.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/acceptance/test-p308c2-replay-ledger.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/acceptance/test-p308c2-correlation.ps1
```

A failed verification throws a conservative status or an operating-system error; no negative receiver result is a block. The signed test fixtures are synthetic and do not establish actual delivery. Provisioning and OS-protected persistent-key handling, real signed fixture completion and an end-user verifier remain incomplete. The existing v1 in-memory prototype remains unchanged and cannot be silently reinterpreted as the signed v2 protocol.

## P3-08C3 signing and offline review (research only)

**Execution status:** persistent key provisioning, supervised receiver, signed live fixture output and cross-VM execution have not been run in an independently authorized lab. Do not use the workstation or CI as a substitute. The signed v3 protocol is separate from historical unsigned JSON v2 and signed prototype v2; the operator verifier requires v3. `AUTHENTICATED_AND_CORRELATED` validates signed producer assertions, not a packet, trustworthy VM, Windows flow attribution or WFP enforcement. A compromised signer, local user or administrator can make false statements; a timestamp is not proof of freshness or cross-VM causality.

### Disposable lab identity enrollment

An authorized operator independently confirms the topology, VM identities, routes, firewall state, lack of uplinks and disposal plan before any live fixture. On each **disposable VM only**, create one new role-specific identity under the current Windows user. The Microsoft Software KSP key is RSA-3072, user-scoped and non-exportable; default user key ACLs were not independently audited. The key can be used by that user and administrators. Acknowledgement is not proof of isolation. Do not create persistent research keys on a normal workstation.

```powershell
$env:AEGIS_DISPOSABLE_VM_NETWORK_TEST = 'I_UNDERSTAND'
.\tests\acceptance\provision-p308c3-signing-key.ps1 -Role VmA -Identity <new-lowercase-identity> -EnrollmentPath <new-absolute-public-json> -AcknowledgeDisposableIsolatedNetwork
# On the separate VM B, use -Role VmB and a different identity and enrollment path.
```

Each role's public enrollment contains its fingerprint and public parameters only. Independently compare fingerprints through an authenticated operator-controlled channel before creating the trust manifest; do not accept an evidence-bundled key as a trust root. Use distinct VM keys. Rotation requires a new key name, separately approved fingerprint and a new trusted manifest; revoke by removing the old identity or marking it inactive in a newly approved manifest. Preserve historical trust configurations for verification of retained old evidence, and never silently replace or delete keys. A failed provisioning attempt can leave a reserved empty file or a key for manual investigation. No persisted key provisioning was tested on this development host.

### Signed fixture and operator verification

The existing [cross-VM procedure](cross-vm-network-test.md) remains the source of endpoint, GUID, authorization and startup requirements. In an approved lab, add `-SigningIdentity <VM-A-identity>` to the existing ordinary and AppContainer client commands. On VM B, use `invoke-p308c3-receiver-supervised.ps1` with the existing receiver parameters plus `-SigningIdentity <VM-B-identity>`; the supervisor signs a completion record only after its receiver process exits successfully and reports both transactions. Signed payload and detached `.sig` files are written with create-new semantics. Failures can leave partial outputs for investigation and must not be interpreted as success. In signed mode, the AppContainer stage additionally requires `-TrustManifest <approved-trust-manifest> -TrustManifestSha256 <independently-approved-sha256> -OrdinarySignedPayload <vm-b-ordinary.payload> -OrdinarySignedSignature <vm-b-ordinary.sig>`; it validates the VM B signature and ordinary run bindings before launch. The legacy unsigned JSON check is also retained. This signed-mode gate does not independently check a run-window or replay ledger before launch; use separately approved fresh run identifiers. The legacy mode remains available without signing and yields unauthenticated evidence. This gate and the live fixture remain untested in a real authorized VM pair.

The offline verifier `verify-p308c3-evidence.ps1` requires explicit paths for five payload/signature pairs, a separately trusted operator manifest, an approved run manifest, their independently supplied SHA-256 pins, a pre-existing operator-owned ledger, and a new report path. A manifest hash obtained from the same untrusted bundle is not an independent trust root. Manifest bytes are strict ordered ASCII `name=value` LF lines with a final LF. Trust field order: `protocol`, `version`, `roleA`, `statusA`, `algorithmA`, `fingerprintA`, `modulusA`, `exponentA`, `roleB`, `statusB`, `algorithmB`, `fingerprintB`, `modulusB`, `exponentB`; the fixed protocol is `AegisOperatorTrust`, version `1`, roles `VmA` and `VmB`, statuses `active`, algorithm `RSA-PKCS1-SHA256`. Moduli and exponents are canonical base64 public parameters from the approved enrollments. Run field order: `protocol`, `version`, `run`, `ordinaryTransaction`, `appTransaction`, `client`, `receiver`, `port`, `transport`, `windowStart`, `windowEnd`; fixed protocol `AegisApprovedRun`, version `1`, transport `IPv4/TCP`, lowercase GUID N format and UTC timestamps `yyyy-MM-ddTHH:mm:ss.fffffffZ`.

```powershell
# On the trusted offline verifier, after independently approving exact manifest bytes:
. .\tests\acceptance\p308c2-replay-ledger.ps1
Initialize-P308c2Ledger -Path <new-absolute-operator-owned-ledger>
$trustHash = (Get-FileHash -Algorithm SHA256 -LiteralPath <approved-trust-manifest>).Hash.ToLowerInvariant()
$runHash = (Get-FileHash -Algorithm SHA256 -LiteralPath <approved-run-manifest>).Hash.ToLowerInvariant()
.\tests\acceptance\verify-p308c3-evidence.ps1 -TrustManifest <approved-trust-manifest> -TrustManifestSha256 $trustHash -RunManifest <approved-run-manifest> -RunManifestSha256 $runHash -OrdinaryClientPayload <vm-a-ordinary.payload> -OrdinaryClientSignature <vm-a-ordinary.sig> -OrdinaryReceiverPayload <vm-b-ordinary.payload> -OrdinaryReceiverSignature <vm-b-ordinary.sig> -AppClientPayload <vm-a-app.payload> -AppClientSignature <vm-a-app.sig> -AppReceiverPayload <vm-b-app.payload> -AppReceiverSignature <vm-b-app.sig> -ReceiverCompletionPayload <vm-b-completion.payload> -ReceiverCompletionSignature <vm-b-completion.sig> -LedgerPath <existing-ledger> -ReportPath <new-report.json>
```

The ledger is explicit, separate from untrusted evidence, and never automatically recreated or repaired. Concurrent verifiers serialize via exclusive file access; accepted IDs are flushed before success. A crash after commit and before a report can consume the run without a successful report; preserve the ledger and investigate rather than retrying with a fresh ledger. Torn writes fail closed. A privileged attacker or restored snapshot can roll state back; external monotonic state is required to detect this. Hostile concurrent filesystem replacement, OS compromise and malicious administrators remain outside the path-check trust boundary. Report write failure after a ledger commit also requires manual investigation. Receiver duplicate checks cover a bounded window and cannot prove all duplicates were absent. Protect report, manifests and ledger under operator access controls; do not include credentials, browsing content or unrelated traffic.

Live authorization, separate VM readiness, ordinary and AppContainer controls, WFP packet attribution and fail-closed behavior remain **NOT EXECUTED**. `LIVE_CROSS_VM_VALIDATION: BLOCKED — INFRASTRUCTURE_UNAVAILABLE`.
