# Scoped WFP object feasibility experiment

Status: scoped native WFP object creation, readback and cleanup observed once on a disposable Windows x64 runner; packet enforcement remains untested. This is not a containment service and does not affect production browser startup.

The opt-in test creates two uniquely named synthetic per-user AppContainer profiles and reserves two separate loopback TCP ports. It opens a dynamic WFP session and, in a transaction, creates a test-owned provider, sublayer and two block filters. Each filter intersects exactly one fresh AppContainer package SID, TCP, IPv4 loopback destination and its own reserved port at ALE_AUTH_CONNECT_V4. It adds no permit rule, loopback exemption, callout, driver or system-wide rule. It reads each filter back and checks its layer, owner, action and all four conditions before reporting object creation. After closing the dynamic session it queries the two filter keys, provider and sublayer from another session and requires all to be absent. It leaves the synthetic identities to disposal of the test runner; it does not remove user profiles or unrelated WFP objects.

The harness validates its four in-memory conditions before staging each filter, attempts readback inside the transaction before commit, and checks both filters again after commit. On failure with a transaction open, it explicitly attempts transaction abort and reports abort or session-close errors. The test logs its own random provider, sublayer and filter GUIDs. If cleanup reports failure, retain those identifiers and discard the disposable runner; do not reset WFP or delete unrelated rules.

## Isolated readback observation

The explicit disposable-runner [diagnostic run 41291189281](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41291189281) built the fixture, opened a dynamic session, staged the objects and read back the first filter. Its four expected SID, IPv4 loopback, port and TCP conditions matched, but BFE returned filter flags `0x00000040`. This is `FWPM_FILTER_FLAG_INDEXED`, documented by Microsoft as an index for faster classification lookup ([FWPM_FILTER0](https://learn.microsoft.com/en-us/windows/win32/api/fwpmtypes/ns-fwpmtypes-fwpm_filter0)). The existing strict zero-flags check caused that run to abort rather than commit; post-failure object absence was not independently verified. The comparison now accepts only the documented INDEXED bit and still rejects DISABLED and every other unexpected flag.

The earlier failed run also had the zero-flags check but did not log the differing field. Its precise mismatch remains unknown; the later observation does not establish its cause. Neither failure establishes packet enforcement. For recovery records, the diagnostic run's owned provider was `1cdda14d-2d26-4e49-a9c8-d5fca570a2d4`, sublayer `c1e2841c-717f-48d2-8f27-45f155d8c5fc`, filter A `f62d07ee-bc14-426e-8ce5-9c2e7b89cab9`, and filter B `3e78f26d-f41e-405d-972a-6b94a300d104`. These identifiers belong to the disposable runner; do not remove unrelated objects.

After the INDEXED-only correction in source commit `4997dc1b1bb36a08f1a74a8980fc9604730ed49c`, [disposable run 41291369737](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41291369737) succeeded. It built and executed the fixture, verified exact scope for both dynamic block filters before and after commit, closed the owning session, and independently queried the two filter keys, sublayer and provider as absent. The opt-in workflow ran at commit `3292acbf0dcfa0237e2ea72a0059eca07922ffbf`, which checks the reviewed source parent. The run's owned provider was `8466215f-0a8b-47ce-8efc-ad26c3c52307`, sublayer `1280a5c2-886a-49a0-a40f-416fcd69dd16`, filter A `666abea8-31dc-455b-b733-05a406f6d925`, and filter B `671e30db-0ea0-404c-bc49-d27de9eacfaf`. No packets were sent and no enforcement, service-failure or production-containment claim follows from this object test.

The test's executable and script both reject actual execution unless explicitly requested on a disposable GitHub-hosted runner. Build-only mode performs no WFP API calls. On an isolated runner with Visual Studio's x64 developer environment, the explicit command is:

    ./tests/acceptance/research-wfp-scoped-objects.ps1 -RunOnDisposableRunner

To compile without executing the privileged test, use -BuildOnly. Adding or querying filters requires the BFE access rights documented by Microsoft; this may require an elevated isolated runner. Missing permissions, unavailable SDK, an installation failure or cleanup verification failure are failures or unavailable results, never evidence of network isolation.

The fixture now encapsulates its existing transactional installation, exact filter readback, and dynamic-session closure in a test-only lifecycle object. It labels an installed and reread policy `ActiveUnverified`; failed readback and closure do not authorize use. The privileged disposable-runner test checks that revalidation after session closure is rejected. This is not a production controller: it does not set up a proxy allow-list, protect Chromium subprocesses, cover IPv6 or UDP, or survive an owner crash. Normal Windows CI builds the fixture and uses `-SelfTest` for in-memory scope and uninstalled-lifecycle checks only; this mode makes no WFP policy calls, creates no AppContainer profile and sends no packets. Installation, policy-loss and enforcement outcomes remain untested until separately authorized native execution.

`WfpPrivateTestBlock.cpp` is a separate opt-in **object test**, not a two-VM blocked-flow test. On an explicitly authorized disposable native Windows x64 administrator machine, it creates a fresh, randomly named AppContainer profile, installs one dynamic `ALE_AUTH_CONNECT_V4` TCP block scoped to that SID, one canonical RFC1918 destination and one port, and checks the filter's exact conditions and runtime ID. While waiting for a stop event it rechecks the filter at intervals of at most one second; a missing, replaced or unreadable filter is a failure. This periodic observation does not establish uninterrupted protection. On normal completion it closes the session and independently checks that its filter, sublayer and provider are absent. It does not send packets, launch a process, or disclose the fresh profile name or SID. It cannot be used with the existing two-VM AppContainer client or observer until a supervisor ties the **same** identity to a retained child handle and ensures the child exits before policy removal.

After an operator has independently confirmed that machine disposal, elevation, private destination and port are authorized, build in an x64 Visual Studio developer shell:

```powershell
./tests/acceptance/build-wfp-private-test.ps1 -BuildOnly
# Set $testExe to the executable path printed by the build command.
$env:AEGIS_DISPOSABLE_VM_NETWORK_TEST = 'I_UNDERSTAND'
& $testExe --install-private-disposable $confirmedPrivateIPv4 $confirmedTcpPort --acknowledge-dynamic-policy-is-not-containment
```

The command outputs its test-owned stop-event name and waits at most 120 seconds. Once the operator has copied that exact printed name into `$stopEventName`, signal it from a separate process in the same user session:

```powershell
$event = [Threading.EventWaitHandle]::OpenExisting($stopEventName)
try { $null = $event.Set() } finally { $event.Dispose() }
```

Use `-SelfTest` on the build script for an in-memory check without WFP, profile or network operations. The private-endpoint object test has **not been executed**. Its dynamic policy disappears if the helper/session exits; do not use it for Chromium, real profiles, persistent policy, an attributed negative control, or production containment. Failure or timeout is not evidence that traffic was blocked. No two-VM positive or negative observation follows from it.

This deliberately narrow experiment does not launch CEF, classify traffic, test a permit rule, prove filter arbitration, or observe packet delivery. It does not test IPv6, UDP, DNS, WebRTC, cross-profile traffic, proxy outages, service failure or BFE restart. A successful object readback is not proof that any packet was blocked. Dynamic session objects are removed when the session ends; they cannot by themselves enforce fail-closed behavior if a browser outlives that session. Any future egress experiment needs independent positive and negative controls, a demonstrated enforceable identity for network-capable descendants, lifecycle review and a separately authorized disposable environment.

References: [WFP object management](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management), [available layer conditions](https://learn.microsoft.com/en-us/windows/win32/fwp/filtering-conditions-available-at-each-filtering-layer), [WFP access control](https://learn.microsoft.com/en-us/windows/win32/fwp/access-control) and [filter arbitration](https://learn.microsoft.com/en-us/windows/win32/fwp/filter-arbitration).
