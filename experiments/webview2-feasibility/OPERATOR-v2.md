# WebView2 feasibility: proposed authorized smoke test

**NOT_RUN.** This procedure is preparation for an existing, explicitly
authorized disposable Windows x64 environment. Proposed paths and executable
hashes are not evidence that such an environment exists. This document, a
successful preflight, a path or a runner label does not grant authorization.

1. Have the environment owner approve the exact prototype, managed references,
   x64 loader, manifest digest, intended Windows identity, profile writes,
   subprocesses, evidence transfer and background-network scope. Verify the
   actual Windows edition/build and x64 architecture, standard-user identity,
   isolation, .NET and Windows Desktop runtimes, and WebView2 Runtime before
   scheduling a launch. Do not provision a machine or change security settings
   to satisfy this procedure.
2. Transfer the full reviewed bundle to a new writable folder, for example
   `C:\AegisLab\Input\WebView2Prototype`, using the approved transfer path.
   Obtain the independently reviewed manifest SHA-256 through a separate trusted
   channel. From the destination folder, calculate:

   ```powershell
   Get-FileHash -LiteralPath .\bundle-manifest.json -Algorithm SHA256
   ```

   Stop unless the result matches the approved digest; a manifest cannot
   authenticate itself. Then verify each manifest entry's relative path,
   length and SHA-256 with `Get-FileHash`. The manifest itself is deliberately
   excluded from its file list. Stop on any other extra entry, or on a missing,
   altered or reparse-point entry. Do not follow links or delete pre-existing
   objects.
3. Confirm that `C:\AegisLab\Profiles` is the exact approved, existing profile
   root, separate from existing Edge, Chromium and personal profile data. The
   prototype uses `C:\AegisLab\Profiles\aegis-webview2-<new-guid>\a` and
   `C:\AegisLab\Profiles\aegis-webview2-<new-guid>\b`. The operator must
   authorize both fresh child directories under that root; no
   automatic fallback or pre-existing profile reuse is allowed. Source-level
   path checks do not eliminate same-user or filesystem replacement races.
4. Confirm that the required `Microsoft.NETCore.App` and
   `Microsoft.WindowsDesktop.App` 10.0.15-compatible runtimes and an approved
   WebView2 Evergreen Runtime are installed. The runtime is selected at launch,
   so record the actual version and stop if it is outside the approved scope.
   The bundle does not include a WebView2 Runtime. If there is no `.deps.json`,
   managed dependency resolution and native-loader discovery remain unverified
   until a real launch; do not add guessed metadata or substitute DLLs.
5. After explicit authorization, choose a positive shutdown deadline in
   milliseconds within the reviewed test scope. From the bundle folder, use the
   installed .NET host and an operator-supplied deadline:

   ```powershell
   & 'C:\Program Files\dotnet\dotnet.exe' exec '.\Aegis.WebView2Feasibility.dll' --profile-root 'C:\AegisLab\Profiles' --shutdown-timeout-ms <approved-positive-milliseconds>
   ```

   Replace the placeholder with the approved integer; do not invent a default.
   The same interval reports an unconfirmed navigation timeout. A deadline
   does not cancel native work, and controls remain alive while work is pending.
   Synchronous control disposal occurs after quiescence and can itself hang;
   the pending-operation deadline cannot interrupt it.
   This is an operator instruction, not a command executed by this document.
6. Observe startup, actual runtime identity, the local synthetic page, both
   independent environments and normal window close. WebView2 may start
   browser, renderer, GPU and utility processes and can perform background
   network activity before navigation handlers are installed. Do not visit
   external sites, use personal data, activate real devices or perform crash,
   kill, firewall, certificate-trust or containment experiments.
7. On timeout, initialization error, unexpected process or unauthorized network
   activity, stop dependent checks, retain sanitized evidence and contact the
   environment owner. A timeout is incomplete shutdown, not proof that native
   work was cancelled or disposed. Do not forcibly terminate a process to make
   the result pass. Record only test IDs, timestamps, outcome, relevant process
   identities, exception categories and error codes; never record credentials,
   cookies, page contents or sensitive URL parameters.
8. Retrieve evidence through the approved channel. Record remaining processes
   and profile directories. The prototype does not authorize deleting existing
   data; cleanup and eventual disposal must be separately approved and limited
   to resources the authorized run created.

No result here establishes renderer sandbox enforcement, OS profile isolation,
network containment, security-fix coverage or production readiness.
