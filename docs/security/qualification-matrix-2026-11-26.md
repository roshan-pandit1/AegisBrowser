# Security qualification matrices — 2026-11-26

Status: **PARTIAL**. This is a test and evidence matrix for the development
preview at source baseline `1ee1dd284d2f6ad3ccb66593ce2984d56843063a`,
not a production acceptance result. Existing fixture outcomes apply only to
the cited build and environment. [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]

The selected engine is not yet qualified. Any current-engine experiment is
engine-specific and provisional; native acceptance cannot pass until the
engine, browser, sandbox and provenance prerequisites pass. Developer-authored
tests do not constitute external independent review.
The DPAPI failure handling, executable controls and normalized ZIP writer
described below are provisional work after that baseline, not claims about
the earlier shipped artifact. See the
[credential diagnostic](../decisions/credential-protection-diagnostics-2026-11-26.md),
[reproducibility decision](../decisions/reproducible-build-2026-11-26.md),
and [artifact inventory](pr90-preview.sbom.cdx.json) for their exact scope.

## Profile and lifecycle coverage

Use unique, non-secret per-run canaries and disposable profiles. Compare at
least two simultaneous profile processes and multiple tabs; repeat after a
normal browser restart and after an authorized VM-only interrupted process.
Include same-profile concurrent acquisition, stale-lease recovery, create,
delete, symlink and reparse-point cases without touching unrelated data.
Retain exact source SHA, profile identifiers, canary hashes, process creation
times and result counts. Test each row in main documents, applicable frames and
workers where the storage API exists.

| Store / boundary | Evidence and status | Blocker and next discriminating test |
| --- | --- | --- |
| Persistent cookies | PARTIAL: controlled write/read fixture on CefSharp 154. [HOSTED-CI] | Complete concurrent cross-profile, cache, grant and crash combinations are not established. Run the full canary matrix on the qualified engine. |
| Local storage | PARTIAL: controlled write/read fixture on CefSharp 154. [HOSTED-CI] | The same fixture does not establish other stores or adversarial access. Test concurrent profile and restart canaries. |
| Session storage | NOT_RUN [NOT_RUN] | No complete qualified-engine cross-profile/session lifecycle result. Test tab, window and process restarts and check expected session lifetime. |
| IndexedDB | NOT_RUN [NOT_RUN] | Cross-profile and crash/recovery canaries not established. Exercise writes, restart and isolation on the qualified engine. |
| Cache Storage and service workers | NOT_RUN [NOT_RUN] | Cross-profile worker/cache scope and cleanup unproven. Register distinct workers and cache entries, restart and check both profiles. |
| HTTP cache | NOT_RUN [NOT_RUN] | No complete canary and network-observer comparison. Use distinct cacheable fixture responses and server request logs. |
| Permissions and preferences | PARTIAL: source enforces selected origin and permission handling. [SOURCE-VERIFIED] | Full persisted permission and preference isolation is unverified. Test grants, denial, restart, cross-origin frames and profile separation with controlled devices. |
| Exclusive lease / stale recovery | PARTIAL: source and developer fixtures cover selected lease handoffs. [SOURCE-VERIFIED] [HOSTED-CI] | Crash, stale-lease, same-user adversary and all child handles remain incomplete. Observe lock acquisition/release and process identities in an authorized disposable VM. |
| Symlink / reparse paths | PARTIAL: some hosted fixtures ran; everyday-host junction tests violated the test-environment instruction. [HOSTED-CI] [DEVELOPER-VERIFIED] | No authorized disposable-VM acceptance for all paths. Run all cases only there and record each owned object and cleanup. |

**NOT_RUN [REAL-WINDOWS] [DEVELOPER-VERIFIED]:** the two local symlink
fixtures skipped. The current normal-user token did not list
`SeCreateSymbolicLinkPrivilege` and the inspected Developer Mode registry
value was absent; neither observation proves all possible ways to create
links are unavailable. Next: run the unchanged cases in an existing,
authorized disposable isolated Windows VM with the required capability,
and record the actual result and cleanup.

Separate directories and a lease are application mechanisms. Do not label them
an OS-enforced confidentiality boundary against another process acting as the
same user without a separately tested access decision.

## Privacy and cross-context coverage

Use local controlled pages and preserve measured values only from synthetic
profiles. Observe main document, same-origin and cross-origin frames, dedicated,
shared and service workers, OffscreenCanvas, tabs, navigation and restart.
Results demonstrate configuration consistency, not anonymity or resistance
to fingerprinting. Unsupported or mismatched contexts are findings.

| Surface / control | Mechanism and existing evidence | Status, limitation and next test |
| --- | --- | --- |
| User agent and Client Hints | No per-profile spoofing in reviewed app setup. [SOURCE-VERIFIED] | NOT_RUN [NOT_RUN]: no full agreement test across HTTP headers, navigator and worker APIs. Capture each surface before/after restart and across profiles. |
| Locale, language and timezone | No qualified coherent per-profile identity override. [SOURCE-VERIFIED] | NOT_RUN [NOT_RUN]: compare Accept-Language, navigator and Intl/timezone in all contexts and after restart. |
| Canvas | No general canvas spoofing or protection is claimed. [SOURCE-VERIFIED] | NOT_RUN [NOT_RUN]: measure canvas and OffscreenCanvas values across contexts; do not label unsupported protection as present. |
| WebGL | Optional per-browser restriction; worker/OffscreenCanvas coverage has known limits. [SOURCE-VERIFIED] [HOSTED-CI] | PARTIAL: do not generalize main-document results. Compare main, cross-origin frame, popup and all worker contexts using the qualified engine. |
| Font enumeration / remote fonts | Optional remote-font restriction; it does not hide installed fonts. [SOURCE-VERIFIED] | PARTIAL: request blocking and enumeration are different. Measure allowed/blocked font requests and enumeration in each context. |
| Viewport and display | Optional zoom-aware viewport letterboxing. [SOURCE-VERIFIED] [HOSTED-CI] | PARTIAL: screen metrics, rounding and DPI can differ. Measure CSS viewport, screen values and geometry across scaling and restart. |
| WebRTC | Optional non-proxied UDP preference is requested/read back. [SOURCE-VERIFIED] | PARTIAL: preference acceptance is not OS enforcement. Use positive-control STUN and independent OS/receiver observation over IPv4 and IPv6. |
| Permissions and media devices | Origin-scoped request handling, with unsupported requests denied. [SOURCE-VERIFIED] | PARTIAL: device enumeration and all worker/frame cases are unverified. Test before/after permission with controlled hardware and profiles. |
| Third-party cookies and tracking | Profile preference and CEF request filtering; top-level, cache, worker, WebSocket, WebRTC and unknown-context limitations apply. [SOURCE-VERIFIED] | PARTIAL: neither mechanism is a firewall or complete partitioning proof. Run cross-site, worker/cache and restart request-observer fixtures. |

## Proxy behavior and production gate

The normal configuration can use browser defaults; a required-proxy profile
must not start at all under the current guard. HTTP, HTTPS via HTTP CONNECT,
unauthenticated SOCKS5 and experimental HTTP authentication have scoped
loopback fixtures; they are not OS-enforced containment. Authenticated SOCKS5
is rejected, and the saved preference builder has no qualified HTTPS-proxy
endpoint mode. [SOURCE-VERIFIED] [HOSTED-CI]

**BLOCKED [ENVIRONMENT-BLOCKED]:** no production protected-proxy routing,
fail-closed outage or DNS guarantee can be accepted without a qualified
engine and the independent receiver/OS observations below. Next: test proxy
offline at startup, invalid/corrupt configuration, invalid credentials,
rotation/replacement, proxy disconnection, concurrent profiles, restart and
all process generations; a browser error or an absent packet alone is not
proof. Keep authentication experimental until its challenge isolation and
credential lifecycle are verified. Store credentials with Windows protection;
no plaintext fallback is permitted.

## Authorized two-VM network procedure

**ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]:** the two existing authorized,
disposable, isolated Windows x64 VMs, written authorization for WFP and OS
auditing, trusted out-of-band evidence transfer, and verified private topology
were not supplied. Do not create VMs, run the following on the everyday
workstation, contact public systems, or assume acknowledgements prove
authorization. The older `cross-vm-network-test.md` includes provisioning
examples; its VM-creation section is not authorized for this task.

1. An authorized operator identifies the two already-existing disposable VMs,
   source SHA, Windows builds, image ownership, disposal plan and review scope.
   Record the written permission for WFP, auditing and fixture traffic.
2. Read-only verify the actual private topology, adapter routes, IPv4/IPv6,
   isolated receiver and DNS endpoints, permitted ports and trusted
   evidence-transfer channel independently of the network under test.
   Record clock-skew measurement and a unique run ID. Stop if any fact is
   unavailable; do not alter policy to make preflight pass.
3. Review the existing
   [private-network preflight](../../tests/acceptance/preflight-private-network.ps1),
   [receiver](../../tests/acceptance/receive-private-network-control.ps1),
   [sender](../../tests/acceptance/invoke-private-network-control.ps1), and
   [isolated execution plan](wfp-isolated-execution.md). Run only approved
   commands with operator-supplied endpoints and paths inside the verified VMs.
   Existing fixture acknowledgements and synthetic self-tests are not proof of
   infrastructure, policy, identity or enforcement.
4. Before a deny test, record a successful deliberately permitted positive
   control using a distinct unaffected identity, the same destination and a
   functioning receiver and OS instrumentation. A failed control blocks the
   corresponding negative result.
5. Only after authorization and policy review, record owned WFP provider,
   sublayer and filter keys; establish policy before any browser networking.
   Correlate PID **and creation time**, process role/image/hash, run and flow
   identifiers, endpoint/protocol/family, time, filter ID/action and OS
   decision with the independently transferred receiver record.
6. Observe the blocked attempt and OS filtering record, then confirm no arrival
   at the receiver. Repeat across process startup, proxy outage, policy/service
   failure, browser crash and restart. Record cleanup and enumerate owned keys
   after removal; never remove objects that were not created for that run.

| Traffic class | Required positive control and negative evidence | Status / next test |
| --- | --- | --- |
| IPv4 TCP and direct HTTP/HTTPS | Receiver sees permitted identity; OS logs intended denied attempt; receiver sees none for denied flow. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: execute the three-part control on the VM pair. |
| IPv6 TCP | Equivalent IPv6 control and attributed decision, including any mapped-address path. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: enable and verify isolated IPv6 test endpoints first. |
| UDP and QUIC over IPv4/IPv6 | Receiver sees permitted datagrams; OS decision identifies denied socket/flow; no denied arrival. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: test UDP/QUIC with functioning receiver and filtering record. |
| DNS and DoH | Unique name appears at positive-control resolver; OS record identifies denied resolver flow; independent resolver sees no denied query. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: instrument both browser and shared OS resolver identities. |
| WebRTC / STUN | Positive STUN received; attributed OS denial on attempted restricted flow; no matching denied packet at receiver. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: test all supported address families and contexts. |
| Proxy outage, credentials, recovery | Approved proxy receives allowed control; direct fallback attempt has filtering record and no receiver arrival before/during/after failure. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: run outage, invalid-auth and recovery sequences. |
| Browser, network service, GPU and utility generations | Each relevant process role and creation identity is observed and covered before traffic. | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]: correlate every child generation and brokered resolver path. |

## Renderer sandbox evidence

**BLOCKED [UNVERIFIED]:** the current stock CefSharp path does not provide the
CEF renderer sandbox, and no candidate has passed engine qualification. Next:
use an authenticated matching integration in an authorized measurement
environment. For multiple fresh launches record: PID plus creation time,
parent, image path and SHA-256, command line and Chromium tab/process mapping;
externally inspect the renderer and browser tokens, integrity, enabled,
deny-only and restricted SIDs, privileges, job and mitigation policies; and
record an OS audit or ETW access-denied decision for a named test-owned file
and the renderer's exact PID/time. An unsandboxed authorized control must read
that same file close in time. Preserve original evidence and the control;
renderer-reported output and token snapshots alone cannot pass the gate.

### Read-only preparation inventory

**PARTIAL [SOURCE-VERIFIED] [UNVERIFIED]:** The following existing code is
research material, not an accepted production integration. It was reviewed
without running it for this assessment. Before any active test, obtain the
required existing authorized disposable environment and review the exact
source, native/managed versions and build provenance. Do not copy a patched
binding into the application based on these observations alone.

| Existing source | Reusable observation | Missing evidence and next discriminating test |
| --- | --- | --- |
| `scripts/containment/ProcessIdentityReader.cs` and `tests/acceptance/observe-cef-process-identity.ps1` | A point-in-time descendant snapshot supplies a parent PID, creation time, available image path, machine type, AppContainer flag and package SID. The script runs a synthetic CEF fixture, not the shipped application. | It does not hash the mapped image, record the full command line, prove a tab's Chromium renderer mapping, or cover every transient/reparented process. Retain a live handle, bind PID and creation time to the image/hash and renderer-type command line, and independently corroborate the real tab mapping. |
| Historical commit `1db1aa9bf3e21e44d734e6523de22fa1a907ecea`: `tests/research/p309g/TokenObserver/TokenObserver.cs` and `tests/research/p309g/TokenObserver/README.md` | A caller-owned, live handle is checked against expected PID, creation time and path. The observer collects integrity RID, AppContainer state, group/deny-only/restricting-SID **counts**, privilege counts and enabled names, job membership and selected mitigation flags. | It does not identify a renderer, hash the mapped image, list group and restricting SIDs, identify the job or its limits, capture every mitigation, or establish the effective thread token/lockdown time. Extend or replace it only after qualifying the chosen engine, then compare complete external browser and renderer observations while retaining identity. |
| Same historical commit: `tests/research/p0managed/ManagedHost/RendererTraceProbe.cs`, `tests/research/p0managed/ManagedHost/TraceCorrelationClient.cs`, and `docs/p0-renderer-verification.md` | A browser-origin, page-scoped tracing experiment and retained-handle handoff attempt to correlate frame, loader and process observations; changed navigation or missing evidence invalidates the observation. | The experimental results remain unverified: sampled epochs do not prove continuous frame ownership, and the role/lockdown may change. Independently establish Chromium's real tab/process mapping and an authenticated live handle across the actual attempt. Do not expose a remote debugging port to obtain it. |
| Same historical commit: `tests/research/p0native/GuiSupervisor/RendererSentinel.cs` and `tests/research/p0managed/NativeHost/RendererSentinelProbe.h` | The fixture holds an identified test-owned file and supports a separate positive reader of that same file. A renderer-side native open can produce an error claim. | A renderer message or later token `AccessCheck` is not an independent record of the original open, exact file identity or effective thread token. After role and lockdown are established, capture a named-file access-denied decision using authorized object auditing or file-I/O ETW, correlate it to the renderer PID **and creation time**, and obtain a close-in-time unsandboxed read of the same retained file. |
| `tests/acceptance/preflight-private-network.ps1`, `tests/acceptance/receive-private-network-control.ps1`, `tests/acceptance/invoke-private-network-control.ps1` and `tests/acceptance/private-network-evidence.ps1` | Preflight and bounded receiver/control evidence provide preparation for a private IPv4/TCP fixture. The preflight explicitly labels authorization, isolation, peer configuration and enforcement unverified. | Operator acknowledgements and syntactically valid records cannot establish VM authorization or process-to-flow attribution. Verify the VM pair and out-of-band evidence path, then establish a receiver-visible positive control, an attributed OS decision, and no denied arrival for each required class. |
| `tests/containment/WfpDecisionObserver.cpp` and `tests/containment/WfpProcessSupervisor.cpp` | Research code can inspect narrowly scoped WFP filter/event information and owns a test-child lifecycle. Its observer records an inconclusive result rather than assuming PID attribution. | The documented classify-drop event does not by itself identify the originating PID; the current experiment is bounded to a synthetic IPv4/TCP case and cannot establish complete Chromium process coverage or other traffic classes. It may change WFP state and terminate its test child: do not execute it on this host or without written VM authorization. Validate a supported process/flow correlation method, event-loss handling and owned-object cleanup before extending the matrix. |

Historical files can be inspected without changing a worktree, for example
`git show 1db1aa9bf3e21e44d734e6523de22fa1a907ecea:tests/research/p309g/TokenObserver/README.md`.
These references do not authorize execution, a sandbox change, WFP policy, or
a claim that earlier experimental binaries have verified provenance.

## Release gates

| Gate | Status and evidence | Exact blocker and next discriminating test |
| --- | --- | --- |
| G1 current engine security fixes | BLOCKED [OFFICIAL-UPSTREAM] [UNVERIFIED] | Exact Aegis engine patch/backport coverage is unmapped. Qualify matched authenticated artifacts and map advisory fixes. |
| G2 renderer sandbox | BLOCKED [SOURCE-VERIFIED] [UNVERIFIED] | Stock binding lacks CEF sandbox support; no file-denial/control evidence. Integrate and test a supported sandbox path. |
| G3 profile isolation | PARTIAL [HOSTED-CI] [UNVERIFIED] | Cookie/local-storage fixtures do not cover all stores, concurrency or OS boundaries. Run the profile matrix above. |
| G4 protected-proxy containment | ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED] | Authorized VM pair and policy/auditing evidence are unavailable. Run the two-VM positive, OS-decision and receiver matrix. |
| G5 accurate privacy capability | PARTIAL [SOURCE-VERIFIED] [UNVERIFIED] | Cross-context behavior and worker coverage remain incomplete. Run the privacy matrix and document each mismatch. |
| G6 credential protection | PARTIAL [REAL-WINDOWS] [DEVELOPER-VERIFIED] [UNVERIFIED] | A targeted normal-user fixture round-tripped one protected file; the filesystem-sandbox fixture returned Win32 error 2 and wrote no credential file or directory. The precise sandbox cause, crash-output exposure and full lifecycle remain unverified. Next: trace permitted profile/key-store access and inspect credential canaries in an authorized disposable environment; retain fail-closed behavior. |
| G7 dependency provenance | PARTIAL [OFFICIAL-UPSTREAM] [DEVELOPER-VERIFIED] [UNVERIFIED] | Twelve examined NuGet packages matched recorded official catalog SHA-512 values, and the shipped SQLite binary matched its package member. Signature details were retained, but exit codes and full trust decisions were not, so signature trust remains unverified. The SBOM is an artifact/file inventory, not a complete embedded/transitive graph or legal review, and application-executable build attestation is absent. Its generator used a process-scoped PowerShell execution-policy bypass; that operation is not compliant acceptance evidence. The exact CEF/executable fixture invocation and policy status are also unverified. Next: verify signatures with retained exit and chain evidence, rerun controls with scripts permitted in an existing authorized isolated VM, reconcile components, licenses and advisories, and verify a trusted build attestation. |
| G8 reproducible build / integrity | PARTIAL [HOSTED-CI] [DEVELOPER-VERIFIED] [UNVERIFIED] | Two prior builds had identical 539-file content and differed in ZIP timestamp fields; same-runtime normalization produced matching archives. A targeted ZIP fixture was run with a process-scoped PowerShell execution-policy bypass and is not compliant acceptance evidence. Controlled restore inputs, different-path and final-head independent builds, plus path-replacement race resistance, remain unverified. Next: rerun with scripts permitted in an existing authorized isolated VM, then compare authenticated locked builds and exercise reparse/replacement races there. |
| G9 Windows lifecycle reliability | NOT_RUN [NOT_RUN] [ENVIRONMENT-BLOCKED] | The qualified-engine Windows 11 desktop 50-cycle matrix and measurements have not run; the current hosted workflow includes destructive fixtures and does not meet the specified existing-isolated-VM requirement. Next: execute the unchanged matrix on an existing authorized disposable isolated Windows VM after prerequisites pass. |
| G10 external review / authorization | BLOCKED [UNVERIFIED] | No independent external reviewer or human release authorization has been supplied. Obtain both after technical evidence is complete. |

The release decision remains NO_GO for both Release A and Release B. Zero
failures in any finite run lowers the likelihood of a defect; it does not prove
the defect absent.
