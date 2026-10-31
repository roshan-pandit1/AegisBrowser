# Profile network containment threat model

Status: research decision, 2026-10-31. Baseline: `88c4f1821e6f4217bca151033f464e14e02b4136` (draft PR #32 and its unmerged parents). This document describes a **target**, not an implemented security boundary. Required-proxy profiles remain blocked before CEF initialization. A saved Direct setting leaves the existing Chromium/system defaults in effect; it is not a verified direct-only policy.

## Assets, actors and boundaries

Protect the intended per-profile network route, separation between profiles, profile-local browsing state and account sessions, proxy configuration, and proxy credentials. A local browser and its renderer, GPU, utility, network-service and popup processes can handle untrusted page content. The manager, metadata stores, credential vault, an eventual broker/service, the Windows kernel and BFE, the configured proxy, and destination servers are separate trust boundaries. DNS may be issued by another Windows service and thus have a different process/token identity. A proxy can observe traffic addressed to it; TLS still matters.

The target is to prevent unintended network paths from a proxy-required profile, including network-capable subprocesses, within the documented OS and policy boundary. It is not a claim of anonymity, undetectability, protection from a malicious proxy, a compromised Windows account, administrator, kernel, or a process capable of changing WFP policy or the trusted broker. A compromised sandboxed subprocess is in scope only to the extent that its actual token, inherited handles, IPC and permitted network paths remain contained; this has not been demonstrated. Current production behavior provides **startup refusal**, not containment of a running proxy profile.

## Paths and adverse events

| Path or event | Intended property and open question |
| --- | --- |
| TCP, HTTP, HTTPS, proxy authentication | Permit only the approved proxy endpoint and necessary protocol; protect credentials and preserve normal certificate validation. Prove redirects, subresources and popups use the same boundary. |
| UDP, WebRTC ICE/STUN, QUIC/HTTP3 | Deny direct UDP unless an explicitly justified transport is verified. A preference is not a boundary. Test connected and unconnected datagrams and real network-service traffic. |
| DNS, DNS-over-HTTPS and SOCKS hostname handling | Account for browser and Windows resolver traffic, encrypted DNS and the proxy's resolution behavior. Restricting a browser token alone may not confine a shared resolver service. |
| IPv4, IPv6, mapped addresses, interface or VPN changes | Independently test all families and route changes. An IPv4-only rule cannot establish an IPv6 guarantee. |
| Loopback and local IPC | Treat loopback and local services as potential egress paths, not universally safe exceptions. Authorize only a specific, tested endpoint and preserve browser origin protections. |
| Background traffic, native popups, renderer/network/GPU and other children | Establish the identity at first instruction and verify every relevant process and alternate brokered process. Parent PID or path alone is insufficient. |
| Proxy offline at start, refuses/auth fails, or stops mid-session | Continue blocking direct destination traffic, including retries and new sockets; do not infer this from an error page. |
| Profile starts before policy is active; invalid/missing credentials | Fail startup without CEF networking. Do not silently choose Direct. |
| Child gets a different token or launches via another broker | Stop activation; investigate and enforce its actual network identity before any traffic. Enumeration after the fact does not close the race. |
| Service, BFE, browser or OS crashes; filters are absent or stale | A running browser must not become unrestricted. Dynamic filters disappear when their WFP session ends, so dynamic-session cleanup by itself is unsafe. Persistent rules require scoped ownership and recovery. BFE restart behavior remains to be tested. |
| PID reuse, profile restart, concurrent profiles | Keep authoritative process handles plus creation identity and verify tokens; PID is an observation, not durable policy identity. Two profiles must have distinct verified policies. |
| Network policy conflicts or elevated local attacker | Test rule arbitration and enterprise policy interactions. Do not claim resistance to an administrator/kernel compromise. Never modify unrelated apps' policies. |

## Evidence and activation

At the baseline, a controlled local test observed STUN UDP at a direct loopback listener while HTTP used a proxy ([run 41057825104](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/41057825104)). It does not measure public traffic or all WebRTC behavior. A later accepted WebRTC preference with no single observed packet was inconclusive. The baseline also has separate-process HTTP fixtures and a stopped-proxy load error, not independent proof of complete egress control. See [test plan](network-egress-test-plan.md).

Production activation requires independently verified profile identity and all relevant subprocesses, policy active before execution, correct TCP/UDP/IPv4/IPv6/DNS/WebRTC behavior, proxy outage and service/BFE failure behavior, crash recovery, safe cleanup, and supported platform coverage. Missing, skipped or inconclusive security tests block activation. There is no production service, driver, WFP rule or filter installed by this research.
