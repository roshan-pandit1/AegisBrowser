# Independent egress verification plan

Status: planned research, not a passed containment test. Baseline `88c4f1821e6f4217bca151033f464e14e02b4136`. Use a disposable Windows VM or dedicated test machine, synthetic profiles and local controlled fixtures; never real proxy credentials, browser sessions or production profile roots. Do not alter the host's global firewall, DNS or routes for routine tests.

## Instrumentation and evidence

Run the observer outside the browser process. Controlled proxy, origin, DNS and UDP/STUN sinks record bounded synthetic connection metadata and counters, not request headers, credentials, packet payloads or candidate addresses. When permitted, correlate WFP audit/ETW or an independent packet capture with the synthetic identity and process handles. Collect only minimum metadata, secure artifacts and remove only test-owned temporary resources. Record test ID, binary revision, OS/architecture, policy identity/revision, process role, PID plus creation time, token/package SID, intended endpoint, observed endpoint, protocol, event source, filter result where available, time window and outcome.

First prove observers with known positive traffic and a known blocking control; verify they remained healthy for the whole window. A missing packet, a browser error, a missing WFP event, unsupported IPv6 or a timeout alone is **inconclusive**. A block needs a trustworthy independent deny observation and positive control; absence at one receiver cannot rule out a different route. A real negative observation overrides a passing application-level assertion. Distinguish observed direct/proxied/blocked from no observation and inconclusive.

## Matrix

Repeat with direct browser defaults, HTTP and unauthenticated SOCKS5 fixtures, and available/unavailable proxy, with and without a candidate policy. Add authenticated HTTP only with synthetic credentials. Run WebRTC preference both unset and `disable_non_proxied_udp`; no preference constitutes containment. The baseline suite's listeners and browser tests are useful controls, but are not an independent system-wide capture.

| Test | Independent observation and essential control |
| --- | --- |
| IPv4/IPv6 TCP, redirects, subresources and native popup | Proxy and origin record the route; known direct requests reach a separate trap without policy. Separate mapped IPv4-in-IPv6 behavior. |
| UDP and WebRTC STUN | Controlled IPv4/IPv6 sinks plus independent capture; positive STUN/UDP control in the same environment. Include connected and unconnected datagrams. |
| DNS and DNS-over-HTTPS | Controlled resolver/query endpoint and packet or WFP observations, including Windows resolver service identity. Check SOCKS hostname resolution separately. Do not change system DNS on the user's host. |
| Loopback, local services, QUIC/HTTP3 | Explicit per-endpoint and UDP observations; test whether local proxy bypass and service delegation enable unintended routes. |
| Startup, proxy refuse/kill, disconnect/reconnect | Attempt direct destination traffic before, during and after failure. Observe both policy and network independently. |
| Child processes and concurrent profiles | Observe actual network/GPU/renderer/utility/popup identity and all network-capable subprocesses throughout startup and browsing; prove two synthetic profiles have distinct policies and do not affect a third direct/default profile. |
| Service/BFE/browser crash, restart, PID reuse | Observe from before policy installation through complete shutdown; inject only in an isolated environment. Verify rule state, default-deny continuity, owned cleanup and profile lease release. |
| IPv6 enablement, interface/VPN change and sleep/wake | Test actual network transitions on disposable infrastructure. Ensure ongoing and newly created sockets remain under policy. |

Add isolated, explicitly opt-in administrator tests for filter creation and cleanup. Report them separately from ordinary CI and treat unavailable privilege/capture as **not run**, not passed. Automated unit tests can validate policy model and cleanup ownership but cannot prove WFP, child inheritance or egress.

## Current observations and limitations

At the baseline, Windows x64 CI observed a direct loopback STUN packet while its controlled HTTP request used a proxy ([run 41057825104](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41057825104)). A later test accepted a CEF WebRTC setting and did not observe a packet once; that is inconclusive. A SOCKS fixture observed a synthetic hostname, not every DNS path. Existing proxy probes and stopped-proxy errors are not system-wide capture. No WFP/AppContainer containment, DNS/IPv6 coverage, subprocess policy, service-crash fail-closed behavior or real proxy provider has been verified.

Manual release acceptance: repeat the approved matrix with authorized HTTP/SOCKS proxies, controlled DNS and STUN endpoints, public and private network transitions, real IPv4/IPv6, sleep/wake, proxy restart, Windows x64 and Windows 11 ARM64 under x64 emulation. Record environment and evidence, keeping credentials and session content out of artifacts. Do not enable production proxy routing until all activation gates in the [threat model](network-containment-threat-model.md) pass.
