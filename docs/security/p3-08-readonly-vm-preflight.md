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
