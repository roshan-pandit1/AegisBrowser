# P4-FP-03: Cross-context WebRTC privacy

This research starts from PR #66, commit `64fe2a341680db3b6efa2bf6d42f2b7af1f47e1a`. The pinned runtime is .NET 8, CefSharp 154.0.340, CEF 154.0.34 and Chromium 154.0.8037.98. The earlier [push](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42977962661) and [PR](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42977969348) Windows runs passed.

## Context and trust boundaries

The application leases one profile per process, checks `ProxyRepository.EnsureRuntimeReady` before CEF initialization, and applies the opt-in `webrtc.ip_handling_policy` preference to the global request context before opening a tab. Preference readback alone does not prove effective behavior in every execution context. The [pinned CEF popup contract](https://raw.githubusercontent.com/chromiumembedded/cef/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_life_span_handler.h) initializes popup settings and client from the opener; the [request-context contract](https://raw.githubusercontent.com/chromiumembedded/cef/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_request_context.h) describes shared contexts for window-opened browsers. Those source contracts are distinct from observed native behavior and do not prove popup owner-release safety.

## Observation limits

Only disposable test profiles and loopback-controlled endpoints are used. The harness records categories and counts, not user IP addresses, ICE candidate strings, credentials or packet payloads. A listener records traffic that arrives there, not all host traffic. An empty listener does not establish complete UDP, TCP, DNS, IPv6, WebRTC or proxy containment. A baseline positive control, successful operation initiation and bounded browser state are necessary to interpret a missing packet. DNS requires independent resolver instrumentation and is not inferred from using `localhost`.

The controlled origins use loopback HTTP unless a specific test says otherwise. Loopback is treated as trustworthy by Chromium, but it is not evidence of cross-origin HTTPS, public-site behavior or an out-of-process iframe. A frame name is not an OS renderer identity. Popup, worker and network results must be reported separately rather than inherited from the main frame.

## Security and compatibility

No CEF package, Chromium sandbox, TLS policy, origin permission policy, native popup ownership or required-proxy guard is changed by this research. The WebRTC option remains opt-in and can affect call connectivity. It is not a firewall and does not guarantee all TCP, DNS or other traffic is proxied. Canvas, audio, local font metrics, screen characteristics and other fingerprint surfaces remain observable. Neither Tor Browser equivalence nor full fingerprint resistance is established.

## Results

On Windows ARM64 with x64 emulation, independent baseline and restricted CEF processes ran a loopback HTTP fixture. For each main, same-origin iframe, cross-origin `localhost` iframe and native same-origin popup scenario, the baseline produced IPv4 and IPv6 STUN packets and IPv4 and IPv6 TCP connections at separate test-owned listeners. With the preference requested and read back, neither UDP listener observed a packet in the bounded window, but both TCP listeners observed connections in every context. In the restricted cases the browser reported an applied offer and ICE gathering `complete` with zero candidates; the baseline was still gathering when all four listeners had observed activity. The popup was created and closed through `SafePopups`, and its opener remained usable. The two processes reported normal CEF shutdown. These are narrow per-listener observations, not comprehensive network containment, call success, or OOPIF attribution.

The dedicated worker, shared worker and service worker each reported `RTCPeerConnection` unavailable in this fixture. That is an API availability observation, not proof about other worker network paths. The service worker used a local script and disposable profile. The native popup surface fixture passed 78 checks and normal CEF shutdown: WebGL was available in the baseline and unavailable with the existing option; the popup's independent remote-font request counter was one in the baseline and zero with the option.

The controlled main, same-origin and cross-origin frame measurements also observed WebGL context availability in the baseline and no context with the per-browser option enabled. **A dedicated worker still created an OffscreenCanvas WebGL context with that option enabled.** The current browser setting therefore does not establish worker-wide WebGL restriction. No supported per-worker CEF setting has been established here, and no API override or global GPU switch was added. The iframe observations use test-owned scheme handlers, not public HTTPS network responses. Remote-font behavior in iframes remains incomplete.

The initial [hosted Windows x64 run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/43015537953) passed at `0268276988ff296dc9ab220261a23944cf61ba02`. It observed the same loopback UDP/TCP results, worker WebGL gap and popup font counts, with 78 checks per fingerprint phase and CEF shutdown. That run predates the timer, ICE-classification and cleanup review fixes; final-head CI must be evaluated separately.

Cross-origin HTTPS, OOPIF process identity, DNS, media tracks, call establishment and owner-close races remain incomplete. The historical intermittent SOCKS callback timeout remains unresolved without root-cause evidence. Local builds used cached packages; NuGet vulnerability metadata was unavailable.
