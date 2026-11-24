# Stage 2A and 2B containment readiness

Source reviewed: `36748360cb4c38a56fa300755b3295ec03ae1b46`.

**Environment gate: ENVIRONMENT_BLOCKED. Stage 2A: NOT_RUN. Stage 2B: NOT_RUN.** The repository and available session contain no verified handoff for an authorized disposable VM pair. The development workstation and ordinary hosted CI are outside this experiment. This review changes no production or acceptance status.

## Required authorization and evidence

| Requirement | Status | Missing evidence and next discriminator |
| --- | --- | --- |
| Two disposable, snapshot-restorable Windows x64 guests | ENVIRONMENT_BLOCKED | No VM inventory, snapshot/restore record or named owners. Obtain the operator's verified guest inventory and snapshot evidence. |
| Private virtual switch with no host LAN or internet path | ENVIRONMENT_BLOCKED | No switch configuration or guest route evidence. Confirm the switch type, uplinks and both guests' IPv4 and IPv6 paths independently. |
| Dedicated NICs and approved private addresses | ENVIRONMENT_BLOCKED | No NIC-to-switch mapping, approved subnet or VM address assignment. Reconcile these with the operator's configuration and read-only guest preflight. |
| Approved TCP port and scoped receiver | ENVIRONMENT_BLOCKED | No approved endpoint or VM B listener binding record. Obtain the approval and independently confirm the receiver's address and port. |
| Guest firewall permission | ENVIRONMENT_BLOCKED | No explicit permission or scoped rule evidence. Obtain written approval and verify the approved source, destination and port on VM B. |
| Trusted evidence transfer | ENVIRONMENT_BLOCKED | No approved out-of-band channel or measured clock bound. Verify the transfer path and retain measured guest clock skew before correlating timestamps. |
| VM-only WFP and audit authorization | ENVIRONMENT_BLOCKED | No written installation, removal or auditing approval. Obtain authorization scoped to the named guests and confirm supported event collection there. |

## Control and evidence review

| Control | What the implementation does | Existing evidence | Status and next discriminator |
| --- | --- | --- | --- |
| Guest preflight | Reads local x64, preferred IPv4, interface and active route data. It rejects out-of-scope paths while treating acknowledgements as operator inputs. | The preflight deliberately returns INCONCLUSIVE even when local checks pass. It cannot attest to switch isolation, permissions, clocks or the peer. | NOT_RUN. Compare outputs from both approved guests against verified operator records. |
| Ordinary TCP | The sender uses a run-specific nonce. VM B binds only the approved private address and port and validates source, message order and nonce before writing a bounded observation. | No authorized paired sender and receiver records. A clean receiver exit can still mean the second observation was missing. | NOT_RUN. Require the ordinary client record and matching independent VM B receipt. |
| Suspended AppContainer positive control | The launcher retains the process, checks its package SID and enabled private-network capability while suspended, then resumes. The sender verifies the transferred ordinary record and chronology before this step. | Historical hosted observations establish suspended identity primitives only. No independent cross-VM AppContainer receipt was available. | NOT_RUN. Correlate the retained PID and creation time, package SID, destination, nonce, measured time bound, exit status and VM B receipt. |
| WFP installation and ownership | The supervisor flushes a ledger containing exact provider, sublayer and filter keys before installation. It installs and checks one IPv4/TCP AppContainer SID and endpoint block, then rechecks identity before resume. | Synthetic scope and sequencing checks exercise the matcher. They provide no live installation evidence. | NOT_RUN. On an authorized guest, collect actual readback and verify exact ledger identity and installation order. |
| OS decision and receiver-side enforcement | The observer matches a bounded WFP drop event to filter ID, layer, image, SID, destination and port. It requires event collection to be enabled and does not enable it. | The existing observer cannot independently attribute the event to the retained process. No paired receiver absence and unaffected positive control exist. | NOT_RUN. Correlate supported OS evidence to the exact attempt and require both receiver absence and contemporaneous receipt for an unaffected identity. |
| Policy lifetime and cleanup | The supervisor revalidates ownership inside the deletion transaction and requires confirmed child exit and an empty job. A fresh BFE session checks each recorded key for its exact not-found result. | No post-run enumeration, recovery exercise or post-cleanup positive control from this stage. Persistent objects alone provide no BFE restart or crash guarantee. | NOT_RUN. Retain the exact-key checks, collect an independent key enumeration and repeat the receiver-backed positive control after cleanup. |

The [cross-VM procedure](cross-vm-network-test.md) and [WFP supervisor procedure](wfp-supervisor-test.md) correctly describe research controls. Their scopes differ from the requested Stage 2B sequence in three ways:

- The supervisor creates its test child suspended before installing policy. Readback precedes resume and socket activity. The stricter pre-process-creation condition remains **BLOCKED** until the ordering is reviewed and an authorized trace demonstrates readback before creation.
- The Stage 2A receiver accepts exactly two ordered positive observations. It does not produce a concurrent unaffected-identity control or a post-cleanup control for Stage 2B. That evidence is **NOT_RUN** until an approved run plan supplies independent VM B observations for both windows.
- The supervisor's fresh-session exact-key checks are narrower than a separate post-cleanup inventory. Enumeration is **NOT_RUN**. Before VM reuse, compare an independently collected inventory with the owned ledger keys or restore the approved snapshot.

## Production boundary and remaining Stage 2C work

No sockets, privilege changes, WFP writes, audit changes or native fixtures were run for this review. No policy was installed, so there are no experiment keys to enumerate and no cleanup result to report. The [acceptance record](proxy-containment-acceptance.json) retains its existing evidence.

Required-proxy production startup remains **BLOCKED**. Stage 2C still requires verified renderer sandbox integration and Chromium child identities, IPv4 and IPv6 containment, TCP and UDP enforcement, DNS and WebRTC egress tests, process and service failure recovery, and a complete proxy authentication lifecycle. See the [threat model](network-containment-threat-model.md) and [profile isolation architecture](profile-isolation-architecture.md).
