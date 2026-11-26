# Security boundary decisions — 2026-11-26

Status: **PARTIAL**; applies to preview source baseline
`1ee1dd284d2f6ad3ccb66593ce2984d56843063a` and the explicitly provisional
qualification work. [SOURCE-VERIFIED] [DEVELOPER-VERIFIED] No decision here is
a production approval or external security review.

1. Keep `Program.cs` startup authorization and
   `ProxyRepository.EnsureRuntimeReady` unchanged. A required-proxy
   configuration blocks startup before CEF rather than silently connecting
   directly. This is a source-level startup guard, not containment of a
   running proxy browser. Changing it requires passing G4 evidence, a separate
   reviewed change and explicit human approval. [SOURCE-VERIFIED]
2. Keep `DenyDownloads` active. The reviewed production handler rejects
   CEF download requests; experimental download code is not activated or merged.
   [SOURCE-VERIFIED]
3. Preserve certificate rejection, origin restrictions and the existing sandbox
   configuration. Stock CefSharp does not provide the CEF renderer sandbox;
   no setting is changed to disguise this limitation. [SOURCE-VERIFIED]
   [UNVERIFIED]
4. Store proxy credentials only through a supported Windows protection
   facility. Failure to protect must fail closed and leave no plaintext
   fallback. A targeted normal-user DPAPI fixture succeeded; a sandboxed
   fixture returned Win32 error 2 and left zero credential files or directories.
   The cause is not proven. Same-user access, crash-output exposure and the
   full lifecycle still require canary testing in an authorized disposable
   environment. [SOURCE-VERIFIED] [REAL-WINDOWS] [DEVELOPER-VERIFIED] [UNVERIFIED]
5. Treat profile directories and leases as application separation. Do not call
   them an OS security boundary against same-user processes. Qualify every
   storage context and concurrent lifecycle separately. [SOURCE-VERIFIED]
   [UNVERIFIED]
6. Treat resource filtering, WebRTC preferences and viewport/privacy settings
   as scoped controls, not network containment or evidence of anonymity.
   [SOURCE-VERIFIED] [UNVERIFIED]
7. Only test WFP, ACL manipulation, test process termination, junction
   substitution, trust changes and other privileged or destructive experiments
   in existing authorized disposable isolated VMs. Do not create VMs or alter
   the everyday workstation's network policy. The specified VM pair, transfer
   path and written authorization are unavailable. [ENVIRONMENT-BLOCKED]
8. Require each security claim to name its source, source SHA, environment,
   observation, control and limits. Developer-authored tests are
   `[DEVELOPER-VERIFIED]`, never `[INDEPENDENTLY-VERIFIED]`; hosted Windows
   Server CI is not Windows 11 desktop acceptance. Browser API output is not
   an OS enforcement record. [SOURCE-VERIFIED]
9. Do not bypass PowerShell execution policy to run qualification scripts.
   During this task a deterministic-ZIP fixture and an SBOM-generator rerun
   each used process-scoped `-ExecutionPolicy Bypass`; neither changed
   machine policy, but this may conflict with the user's instruction not to
   disable a security mechanism. Those operations are not compliant acceptance
   evidence. An existing authorized disposable isolated VM where scripts are
   permitted under its approved policy is required for a compliant rerun.
   [DEVELOPER-VERIFIED] [ENVIRONMENT-BLOCKED]

The [threat model](../security/threat-model-2026-11-26.md) records assets,
boundaries, risks, the development-host test incident and mitigations. The
[qualification matrix](../security/qualification-matrix-2026-11-26.md)
defines pending profile, privacy, containment and renderer tests.

G10 is **BLOCKED** pending an external reviewer and human release
authorization. Release A and Release B remain NO_GO. No production release,
tag or merge is authorized by these decisions. [UNVERIFIED]
