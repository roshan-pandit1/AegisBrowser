# Repository reconciliation and safety boundary

Date: 2026-11-27 UTC.

**PARTIAL [SOURCE-VERIFIED] [STATIC-ANALYSIS]:** The local
`feature/engine-qualification` worktree was clean at
`304259f0687eb9a304d00938003ee2686567633b`; no later local commits
were present. The original session-history and browser-core-MVP worktrees were
also clean at `8304e6fe78a94a7a8ea8a8055145f0ff15866ddf` and
`1ee1dd284d2f6ad3ccb66593ce2984d56843063a` respectively. The local
repository reported four worktrees and 118 branch references. Download and
bookmark branches were not modified.

After explicit user approval for read-only Git commands to create the
preservation backup, staged and unstaged binary patches and untracked-file
lists were saved for those three worktrees under
`%TEMP%\aegis-safe-transition-backup-2fecce3025d74b37a5c2c7cbdd1f7bad`.
Each patch and list was empty. The initial empty-list handling error and
subsequent read-only scan are disclosed in the separate
[incident record](incident-test-governance-2026-11-27.md).

The implementation workspace is a separate local clone at
`C:\Users\rosha\AegisBrowser\artifacts\worktrees\safe-engine-transition`
on `feature/safe-engine-transition`, initially at the locally verified
reported commit `304259f0687eb9a304d00938003ee2686567633b`. It was
cloned without hard links and has its own Git metadata within that workspace.
Checkout preparation found no tracked symlinks or submodules, no active default
template hooks, and no tracked filter rules. This is a provisional local
starting point, not proof of the current GitHub PR state.

**BLOCKED [ENVIRONMENT-BLOCKED] [UNVERIFIED]:** The current state, draft flag,
base and head of PR #91 could not be fetched through the available permitted
network path; the public web lookup was unavailable and a credential-free
read-only remote query reported a network failure. No elevation, credential
disclosure, alternate route, or workflow dispatch was used. Next: use an
already authorized read-only GitHub connection and compare the PR head and base
with the provisional local branch before delivery.

**PASS [SOURCE-VERIFIED] [STATIC-ANALYSIS]:** The protected startup, proxy
repository, TLS/download handler, profile-lease policy, native proxy installer,
and `MainForm` paths have zero changed files between the previous preview
head `1ee1dd284d2f6ad3ccb66593ce2984d56843063a` and the local starting
commit. This source comparison does not prove runtime containment or sandbox
enforcement.

**PARTIAL [SOURCE-VERIFIED] [DEVELOPMENT-SAFE]:** A retained archive identified
by earlier evidence as the PR #90 preview had inner ZIP SHA-256
`8A95CE41A2FD16684810376A80D2C4C1FED8E02FBAFEA15630FBBD2B84D7263B`.
Direct read-only hashing of its members found `libcef.dll` SHA-256
`E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7`,
`CefSharp.Core.Runtime.dll` SHA-256
`2B4329C6D48A58194C4E4AC198E82990E4F1DCF63559B6F3357F9EA19781BFF0`,
and subprocess SHA-256
`CA0F20A27A27C73A63130A433A85D51E8F58C18A76EDABE91036DAB3B77E46D6`.
Separately retained byte-matched files report CEF
`154.0.34+g14c5a08+chromium-154.0.8037.98` and CefSharp `154.0.340`.
The archive-to-current-PR linkage and a PR #91 shipped build are unverified.
Next: verify artifact provenance through an authorized read-only GitHub
connection; do not infer a new build from this retained artifact.

**BLOCKED [AUTHORIZATION-BLOCKED]:** The existing unfiltered push/PR workflow
transitively invokes process termination, renderer crash, trust-fixture and
AppContainer operations on hosted runners. A push or PR could start that
workflow, so neither is authorized in the current state. Next: finish and
statically review fail-closed workflow separation, including direct entry
points and human authorization, before considering delivery.
