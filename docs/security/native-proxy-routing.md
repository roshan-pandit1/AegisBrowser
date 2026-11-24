# Native Chromium proxy routing

Base: d069f05c92642c68624399a3fab32a1c409e7345. Windows 11 x64, .NET SDK 10.0.404, CefSharp 154.0.340. These results concern disposable loopback fixtures.

| Capability | At base | Existing native evidence | Result |
| --- | --- | --- | --- |
| HTTP forwarding | IMPLEMENTED | Proxy and loopback origin record requests | VERIFIED in fixture, including script, redirect and popup |
| HTTPS destination through HTTP CONNECT | INCOMPLETE | CONNECT count and certificate rejection | PARTIAL. Chromium rejects the fixture's untrusted certificate. Successful TLS uses the separate test client with a private trust policy. Trusted Chromium HTTPS delivery remains unverified. |
| HTTPS proxy endpoint | INCOMPLETE | TLS endpoint receives connection and Chromium rejects its certificate | PARTIAL. No Chromium application request over a trusted HTTPS proxy was observed. The saved preference builder has no HTTPS proxy mode. |
| Unauthenticated SOCKS5 | INCOMPLETE | Browser requested a domain through SOCKS5 and proxy served the page itself | VERIFIED. Existing fixture now forwards the controlled path to a separate loopback destination. |
| Hostname routing and DNS | INCOMPLETE | Synthetic hostname observed in SOCKS5 and CONNECT | PARTIAL. SOCKS5 supplies its hostname and port, HTTP CONNECT supplies its authority, and Basic HTTP sends an absolute target containing its hostname. Resolver traffic was not measured. |
| Basic HTTP proxy authentication | IMPLEMENTED | Proxy counts accepted or denied credentials and checks origin challenge separation | VERIFIED for one isolated experimental attempt. General authenticated browsing remains unverified. |
| Authenticated SOCKS5 | INCOMPLETE | Configuration rejection | VERIFIED rejection by the installer. The observed ordinary greeting did not offer username/password. Native authenticated SOCKS5 capability remains unverified. |
| Proxy outage and no direct delivery | IMPLEMENTED | HTTP origin observer and independent reachable origin for SOCKS denial | VERIFIED for HTTP outage and explicit SOCKS denial. Stopped SOCKS listener refusal and Chromium error also observed. |
| Shutdown, restart and lease | INCOMPLETE | Auth fixture verifies lease revocation and reacquisition. Native probes report teardown. | PARTIAL. Fresh fixture process runs and normal browser storage restart are covered. Reopening a saved proxy-required profile as a routed production session remains blocked. |

## SOCKS timeout

**NOT REPRODUCED.** Baseline Chromium returned ProxyConnectionFailed after the stopped listener. Both updated native runs independently confirmed loopback refusal. On 2026-11-24 UTC, the navigation requested at 04:19:29.873 returned ProxyConnectionFailed at 04:19:29.923. The request at 04:19:46.412 returned the same error at 04:19:46.448. The destination recorded the preceding successful forwarded requests at 04:19:27.705 and 04:19:44.225. Run IDs were 2f41ed7783fb43e2a6767d96b06840dd and 7929ce2a63fe4ef2a0ae3dbda38dc9d6.

The accepted preference, domain-form CONNECT and separate HTTP destination establish that configuration, negotiation and fixture forwarding worked in those runs. Listener refusal preceded the matching main-frame callback. The observer remained attached through browser close, and the CEF shutdown marker followed. The synthetic timeout control still times out. No causal finding can be assigned to configuration, negotiation, DNS, callback ordering, fixture behavior, timeout handling or request-context lifetime for the historical intermittent failure.

## Production gate

Program.cs and ProxyRepository.EnsureRuntimeReady retain the original fail-closed startup path. These changes are confined to test fixtures and documentation. Production activation requires independently verified OS enforcement before CEF startup in authorized disposable VMs. Cover the browser and all network-capable subprocess generations, IPv4 and IPv6, TCP and UDP, resolver traffic and WebRTC, proxy and policy failures, lease binding, restart, recovery and cleanup. See the [containment threat model](network-containment-threat-model.md).
