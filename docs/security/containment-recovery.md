# Containment recovery and operational boundary

Status: research only. The production application installs **no** containment service, WFP filters, AppContainer identity or driver. A separate opt-in research test can register uniquely named synthetic AppContainer identities on a disposable GitHub-hosted runner, as described in the [architecture decision](profile-isolation-architecture.md); it does not delete identities. There is no supported production installation, removal or repair command for containment. Do not invent or execute system-wide firewall reset commands as recovery.

For the existing proxy configuration, preserve the proxy database, its journal/WAL/SHM files, profile directory, `.aegis-proxy-required` marker and credential files. Follow [proxy configuration recovery](../proxy-configuration.md); do not delete a marker or open an older build to bypass the startup block. Do not delete or overwrite browser profiles or private storage. Explicitly selecting Direct is a user decision that removes the required policy; it is not a repair of containment and is not appropriate when a proxy remains required.

A future privileged component needs a separately reviewed installation, owner-authenticated management endpoint, narrow WFP object ACLs, versioned ownership inventory and uninstall/repair process. A durable default block may survive a crash and prevent a test profile from connecting; a dynamic-only policy may vanish and leave a running profile exposed. Both require actual BFE/service-crash and restart testing before any deployment. Recovery must identify the exact profile identity and owned provider/filter keys, preserve evidence, avoid blanket deletions or changes to unrelated firewall policies, and prove all relevant profile processes have stopped before removing a block. If ownership or process state is uncertain, leave the restriction in place and ask an authorized administrator to investigate. A retained block is not permission to delete browser data.

Only exercise future policy installation and rollback inside disposable, explicitly designated test infrastructure. Before testing, establish independent console access and a VM snapshot; document the exact OS build, permissions, policy keys and a scoped recovery procedure. Validate that service installation/removal and failure cleanup affect no unrelated profile or app. Such a procedure is not yet implemented or verified.

## WFP research fixture

The [scoped object fixture](wfp-scoped-object-research.md) is opt-in on a disposable hosted runner and creates no persistent policy. Ordinary CI compiles it without installing filters. Its dynamic objects and cleanup must not be confused with a production recovery mechanism or a fail-closed lifetime guarantee.

## Private-network control follow-up

A suspended synthetic AppContainer's package SID and sole enabled private-network capability were verified in [Windows run 41314237304](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41314237304). No network was attempted by that token test. The opt-in [isolated cross-VM control](cross-vm-network-test.md) has not been executed; ordinary and AppContainer cross-VM delivery, WFP packet enforcement and attribution remain unverified. Production required-proxy startup remains blocked.
