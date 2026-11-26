# Core MVP baseline and preserved work

Baseline: `36748360cb4c38a56fa300755b3295ec03ae1b46`, also the GitHub head of
`feature/proxy-native-routing` when checked on 2026-11-25 UTC. The MVP branch
was created at that commit in a separate worktree. The original
`feature/session-history` worktree was clean and its empty staged/unstaged
patches were saved outside the repository. The download and bookmark branches
were not modified.

## Baseline checks

| Check | Result and scope |
| --- | --- |
| Build | PASS: Windows x64 .NET 10 browser test build; five existing certificate-constructor warnings. |
| Core/privacy | PASS: 88 privacy, 24,051 letterboxing, 48 zoom and 49 address checks, plus profile/proxy suites. |
| Profiles and proxy model | PASS: 14 catalog, 46 metadata, 38 dashboard, 21 operations, 12 bulk, 20 transfer, 21 backup, 13 settings, 90 proxy configuration, 16 startup-safety and 32 proxy diagnostics checks. |
| Native browser | PASS: 101 write and 10 read checks with CEF shutdown, using disposable profiles. |
| Native viewport | PASS: 125 write and 81 read checks on the Windows 11 development host. |
| Native fingerprint | PASS: 128 write and 128 read checks on the Windows 11 development host. |
| Native filtering | PASS: 29, 3 and 6 checks with disposable profiles. |
| Native proxy fixture | PASS: 62 loopback routing and 40 experimental authentication checks. These are not containment evidence. |
| Local symlink cases | NOT_RUN: two proxy-preset symlink fixtures could not create links on this host. Next: run the same baseline on a disposable Windows host with symlink support. |
| Sandbox-restricted core run | FAILED: Windows DPAPI could not protect the fixture credential inside the filesystem sandbox. A separate run outside that sandbox passed the 90 proxy checks. Next: repeat under a sandbox that exposes the same user DPAPI context to distinguish its restriction. |
| Full hosted baseline | PASS: [Windows push CI](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/45641727214) checked out the exact baseline SHA; it reported 170/122 viewport checks, 128/128 fingerprint checks, 66 proxy and 40 authentication checks, 29/3/6 filtering checks, and a 536-file portable ZIP round trip with extracted HTTPS smoke. Hosted Windows Server is not Windows 11 desktop acceptance. |

The development host reported Windows 11 Pro `10.0.26300`, x64. It was not
authorized for system trust or network-policy changes. No two-VM WFP or
cross-network test was run there.

## Existing work inventory

| Work | Head SHA | Evidence and classification |
| --- | --- | --- |
| [Proxy routing #85](https://github.com/roshan-pandit1/AegisBrowser/pull/85) | `36748360cb4c38a56fa300755b3295ec03ae1b46` | PASS for scoped loopback fixtures; production routing and containment BLOCKED. Open draft, not production merge-ready. |
| [Native sandbox #77](https://github.com/roshan-pandit1/AegisBrowser/pull/77) | `3668eaa20ddc3dadcf97bcb241fbe1d7bde3a499` | Native bootstrap experiment; application integration and independent enforcement BLOCKED. Draft experimental. |
| [Managed sandbox #78](https://github.com/roshan-pandit1/AegisBrowser/pull/78) | `51dcac57697f72a2693f35f0db8a852f6c78fc4d` | Managed IPC fixture; renderer enforcement INCONCLUSIVE. Draft experimental. |
| [Renderer IPC #79](https://github.com/roshan-pandit1/AegisBrowser/pull/79) | `e0dd75742c5fa760f889fdafce80a7ff9182c674` | Child correlation is supporting evidence only; independent enforcement INCONCLUSIVE. Draft experimental. |
| [Renderer identity #80](https://github.com/roshan-pandit1/AegisBrowser/pull/80) | `1db1aa9bf3e21e44d734e6523de22fa1a907ecea` | Tracing/identity research; final application enforcement INCONCLUSIVE. Draft experimental. |
| [CEF builder #81](https://github.com/roshan-pandit1/AegisBrowser/pull/81) | `e74da65138a7ad6e91e32db4d8e506dda4de02d6` | Source-builder preparation; qualified application binding and sandbox proof BLOCKED. Draft experimental. |
| [Containment #86](https://github.com/roshan-pandit1/AegisBrowser/pull/86) | `eafae346388f1b787c0bd22040ef1568b11e81cb` | Two-VM WFP and receiver evidence ENVIRONMENT_BLOCKED; policy and production activation NOT_RUN. Draft research. |
| Local Chromium 156 branch | `36748360cb4c38a56fa300755b3295ec03ae1b46` | No upgrade commit. Published 156 candidate and later security-fix gap are assessed in the engine decision. |
| [Fingerprint #66](https://github.com/roshan-pandit1/AegisBrowser/pull/66) | `64fe2a341680db3b6efa2bf6d42f2b7af1f47e1a` | Experimental cross-surface controls; production coverage is INCONCLUSIVE. Draft experimental. |
| [Fingerprint contexts #67](https://github.com/roshan-pandit1/AegisBrowser/pull/67) | `8203b7b32d6b140614c5cc048e7a64a17aedb95f` | Cross-context measurements, not an invisibility or containment proof. Draft experimental. |
| [Viewport work #71](https://github.com/roshan-pandit1/AegisBrowser/pull/71) | `96264aaaf1a72c790ee7e07018b5d2ad24853dd2` | Controlled viewport evidence; current base has letterboxing, with remaining zoom-race limits in `release-blockers.json`. Draft. |
| [Letterboxing #63](https://github.com/roshan-pandit1/AegisBrowser/pull/63) | `041cc7fed719e5df7da1799a00d6b2134fe65480` | Optional viewport bucketing; it does not change screen metrics. Draft. |

No branch in this inventory is merged by this milestone.

WP-E is **ENVIRONMENT_BLOCKED**: the user confirmed that two existing isolated
VMs, access/topology, trusted evidence transfer and written WFP/auditing
authorization are not currently available. The next discriminating test is the
documented paired-VM positive control and process-attributed filtering record
after those prerequisites are supplied. Release A and Release B remain NO_GO.
