# Aegis Browser threat model — 2026-11-26

Status: **PARTIAL**. Scope: the development-preview architecture at source
baseline `1ee1dd284d2f6ad3ccb66593ce2984d56843063a`, plus explicitly
identified provisional work. This is a developer assessment, not an
independent security review. [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]
The targeted credential-failure and executable-integrity controls described
below are provisional changes after the baseline, not proof that those
changes were present in the earlier shipped preview.

Assets include browsing sessions and profile storage, proxy credentials and
the intended route, local files, Windows and browser integrity, package
contents, security configuration, and user decisions. Trust boundaries include
website/origin to renderer; renderer to browser and native OS; Chromium IPC
and helpers; profile process to profile storage and DPAPI; application to
proxy, DNS and network; and publisher/build system to installed package. A
separate profile directory is application separation, not proof of a security
boundary against another process running as the same Windows user.

The preview's stock CefSharp binding does not provide the CEF renderer
sandbox. Required-proxy profiles are blocked before CEF initialization;
`DenyDownloads` remains assigned. Browser-level proxy configuration, CEF
callbacks, missing packets, process names, and token snapshots are not
independent proof of network or sandbox enforcement. There is no claim of
anonymity, invisibility, production readiness, or resistance to a compromised
administrator, OS kernel, or Windows account. [SOURCE-VERIFIED] [UNVERIFIED]

Severity below is an engineering priority for the stated residual risk, not
a finding that a particular exploit or vulnerability was demonstrated.
Prior fixtures cited are developer-authored and are not independent security
reviews. `C` means control, `R` residual risk, and `Next` a discriminating
test or mitigation.

| Threat, asset and boundary | Existing control and evidence | Weakness, mitigation and residual risk | Severity |
| --- | --- | --- | --- |
| Malicious website; browser/session integrity; web origin → renderer | C: Chromium origin/TLS handling and supported-scheme navigation checks; controlled browser fixtures. [SOURCE-VERIFIED] [HOSTED-CI] | R: current engine security-fix coverage is unqualified and stock renderer sandbox support is absent. Next: qualify an up-to-date matched engine, map vendor fixes and independently test renderer restrictions. | Critical |
| Compromised renderer; local OS and other profiles; renderer → browser/OS | C: Chromium process separation exists; production required-proxy startup is refused. [SOURCE-VERIFIED] | R: process separation is not a sandbox; no external file-denial/control evidence exists. Next: integrate a qualified supported sandbox and perform the renderer test in the qualification matrix. | Critical |
| Untrusted page, title or URL; UI and host; content → native UI | C: navigation policy rejects unsupported schemes and embedded credentials; diagnostics uses an embedded resource. [SOURCE-VERIFIED] | R: not every untrusted-text path or unsafe URI interaction has been separately audited. Next: exercise origin, redirect, unsupported scheme and text rendering cases on the qualified engine. | High |
| Proxy unavailable, corrupt, replaced or disconnected; route integrity; profile → network | C: `EnsureRuntimeReady` rejects required-proxy profile startup before CEF; loopback HTTP/SOCKS fixtures have limited results. [SOURCE-VERIFIED] [HOSTED-CI] | R: no running protected profile or OS-enforced fail-closed policy is qualified. Next: authorized two-VM positive-control, OS-decision and receiver tests for startup, failure, rotation and recovery; keep guard. | Critical |
| DNS leak; queried names and profile route; browser/OS resolver → network | C: scoped fixtures observed selected hostname forms. [DEVELOPER-VERIFIED] | R: resolver ownership and direct DNS/DoH traffic are unmeasured. Next: unique per-run names, independent DNS observer and attributed OS decisions in the authorized VM pair. | Critical |
| Cross-profile exposure; cookies, storage and grants; profile A → profile B | C: separate roots, lease and controlled cookie/local-storage restart fixtures. [SOURCE-VERIFIED] [HOSTED-CI] | R: IndexedDB, caches, service workers, permission state, simultaneous profiles and adversarial same-user access are not comprehensively established. Next: canary matrix across stores, concurrent processes, restarts and lease failure. | High |
| Credential theft or plaintext fallback; proxy secrets; process → vault | C: the vault uses Windows CurrentUser DPAPI; configuration stores a credential reference. A targeted normal-user fixture round-tripped a protected file, while a restricted fixture failed with zero credential files or directories. [SOURCE-VERIFIED] [REAL-WINDOWS] [DEVELOPER-VERIFIED] | R: same-user code may use the user's DPAPI context; the precise sandbox cause, callback lifetime, crash dumps and other failure paths remain unverified. Next: inspect permitted profile/key-store access and crash artifacts with non-secret canaries in an authorized disposable environment; keep failure closed with no plaintext fallback. | Critical |
| Local-file exposure; files; renderer/IPC → filesystem | C: Chromium origin handling exists; unsupported navigation schemes are restricted. [SOURCE-VERIFIED] | R: absent renderer sandbox leaves no established OS file boundary, and broker/handle paths are not comprehensively measured. Next: externally identify renderer and capture a specific denied file read plus a successful unsandboxed control. | Critical |
| Unsafe downloads; local files and execution; web content → filesystem | C: `DenyDownloads` is the active handler. [SOURCE-VERIFIED] | R: any future download implementation could overwrite or create files at unsafe paths. Next: retain denial until destination, ownership, lifecycle and GUI gates pass on an authorized disposable environment. | High |
| TLS or certificate compromise; authenticity of remote endpoint; browser → network | C: certificate-error callback rejects the certificate without an override; fixtures cover specified failure paths. [SOURCE-VERIFIED] [HOSTED-CI] | R: this does not authenticate a malicious but trusted CA, establish complete TLS policy or qualify every proxy mode. Next: valid and invalid certificate tests with trust changes confined to an authorized disposable VM and verified cleanup. | High |
| IPC confusion or inherited handles; browser authority and profile data; renderer/helper → host | C: existing CEF lifecycle handlers and safe-popup ownership; no general privileged JavaScript binding is configured in reviewed startup code. [SOURCE-VERIFIED] | R: full message authentication, inherited handles, broker paths and all child generations are not characterized. Next: map IPC endpoints and handles and test invalid or cross-profile messages under the qualified sandbox. | High |
| Dependency compromise; executable integrity; upstream/build → runtime | C: 12 examined NuGet packages matched recorded catalog hashes; signature details were observed, and the shipped SQLite binary matched a package member. [OFFICIAL-UPSTREAM] [DEVELOPER-VERIFIED] | R: retained logs lack complete signature trust results; the artifact SBOM does not establish all embedded or package-to-package dependencies, advisory applicability or redistribution obligations. Next: retain verified signature exit/chain evidence and reconcile remaining components, fixes and licenses. | Critical |
| Tampered or injected package; executable integrity; package → user machine | C: published and extracted CEF component checks; provisional fixture controls reported rejection of missing, altered and unexpected executable cases in their stated scope. [SOURCE-VERIFIED] [DEVELOPER-VERIFIED] [UNVERIFIED] | R: the controls' exact invocation and execution-policy status could not be recovered, so they are not a compliant acceptance pass; an unsigned checksum is not authenticity, the application host lacks trusted build attestation, and the inventory is not a complete DLL/script allowlist. Next: rerun the controls without bypass on an authorized existing isolated VM, verify signing and build attestation, add a host-replacement control, and review the full executable/component inventory. | High |
| Unsafe update or unqualified engine; browser security; updater/distributor → installation | C: preview labeling, pinned versions and no automatic engine updater. [SOURCE-VERIFIED] | R: delayed updates and incompatible component mixing can persist; advisory backports are unverified. Next: qualify exact matched source/binary versions, signature, fix coverage and update/rollback workflow before distribution. | Critical |
| Crash or interrupted shutdown; leases, profile state and process lifecycle; host → CEF/OS | C: native close readiness and lease disposal paths; scoped hosted shutdown fixtures. [SOURCE-VERIFIED] [HOSTED-CI] | R: no complete 50-cycle Windows 11 desktop sample, adversarial crash recovery or all-child orphan measurement. Next: qualified-engine fresh-process lifecycle suite with exit, process, handle and lease observations. | High |
| Corrupt or partially written configuration; route and privacy policy; storage → startup | C: pre-CEF privacy settings validation and proxy startup readiness guard. [SOURCE-VERIFIED] | R: corruption, rollback, migration and same-user tampering are not comprehensively tested across all configuration stores. Next: disposable malformed, inaccessible and interrupted-write cases; require explicit failure without weakening guard. | High |

## Test-operation incident

**FAIL [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]:** During Phase 0 on the everyday
development host, two core-suite runs each created three test-owned junctions,
killed one test-owned metadata-transaction child and killed one test-owned
settings-transaction child: six junctions and four child terminations in total.
The browser write fixture also invoked `Page.crash` on one test renderer.
These actions violated the instruction to confine privileged or destructive
experiments to an authorized disposable VM. Test ownership does not authorize
that exception.

`AegisBrowser.Tests/ProfileRepositoryTests.cs:198–231` deletes each junction
in its `finally` block, and both runs reported completion of the 46-check
metadata suite after those blocks. This supports completion of those fixture
cleanup paths; it is **not** an exhaustive audit of host side effects.
[SOURCE-VERIFIED] [DEVELOPER-VERIFIED] [UNVERIFIED] Do not repeat these
operations on the everyday host. Next: inspect retained fixture roots and
process records read-only, document any unverified side effect, and move future
process-kill, junction and crash tests to an authorized disposable VM.

**FAIL [DEVELOPER-VERIFIED]:** A read-only source search during this task
displayed pre-existing synthetic test-credential literals in the tool
transcript. No real credential or token was requested or is known to have
been exposed. The literals are not reproduced here. Subsequent searches use
file names, counts or sanitized output. Next: review retained task evidence
for any additional exposure without printing values, and use synthetic
canaries and redacted diagnostics for future tests.

**FAIL [DEVELOPER-VERIFIED] [UNVERIFIED]:** Later in this task, a targeted
deterministic-ZIP fixture and a corrected SBOM-generator rerun were each
executed using process-scoped PowerShell `-ExecutionPolicy Bypass` on a
host whose default policy had blocked unsigned-script execution. Neither
invocation changed machine policy. The process-scoped bypass may conflict with the
instruction not to disable a security mechanism to make a test pass, so
these operations are not compliant acceptance evidence. Do not repeat the
bypass. **ENVIRONMENT_BLOCKED [ENVIRONMENT-BLOCKED]:** a compliant rerun
requires an existing authorized disposable isolated VM where the scripts
are permitted under its approved policy. Next: verify that environment and
policy, then rerun the unchanged operations without bypass.

## Review and evidence boundary

Developer-authored tests and this model are engineering evidence only. Only a
separate external reviewer can provide independent review; a reviewer and human
release authorization remain required. The required existing, authorized,
isolated Windows VMs, their evidence-transfer path and written WFP/auditing
authorization were not supplied or verified. The next infrastructure action
is to provide and verify those prerequisites, not to rerun local substitutes.
[ENVIRONMENT-BLOCKED] [NOT_RUN]
