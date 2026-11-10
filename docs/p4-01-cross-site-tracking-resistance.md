# P4-01: cross-site tracking resistance

This work starts from PR #57 at `2ffa10d692a127d03a7354d5171dee3611593604`.
It does not change Chromium identity, browser APIs, TLS validation, sandbox
configuration or required-proxy startup enforcement.

## Baseline capability matrix

| Capability | Baseline and scope |
| --- | --- |
| Network rules | Standard uses a small user-editable tracking list; Strict additionally blocks known third-party subresources; Custom uses selected user/list rules. Unsupported syntax is reported. |
| Site classification | Registrable domains use a bundled public-suffix snapshot, including private entries, plus normalized IDNs and exact IP addresses. Scheme and port are ignored. |
| Unknown top-level site | Third-party-qualified rules and Strict's third-party fallback do not match an unknown classification by default. |
| Exceptions | Site exceptions, allowed resource domains and supported list exceptions precede blocks. Top-level navigation is not filtered. |
| Storage and permissions | Chromium contexts and profiles separate local data; existing permission and certificate handlers remain in force. This work does not add cookie partitioning or new permission controls. |
| Fingerprint diagnostics | The local page reports API availability and selected values; it does not block or spoof those APIs. |
| Network coverage | Resource interception is not a packet firewall. DNS, WebRTC, some WebSocket, cache, service-worker or background paths may not be intercepted or have a top-level site. |

The threat addressed here is collection by matching network resources when
the current top-level document cannot be identified. Other threats include
third-party collection on visited sites, persistent state, permission abuse,
high-entropy APIs and cross-profile storage leakage; the current changes do
not independently solve all of them. This is not anonymity against a network
operator, IP rotation, device impersonation, fraud-detection bypass or
hostile-administrator resistance.

## New control

The profile-local **Apply third-party rules when site is unknown** option is
off by default. When enabled, the filter evaluates supported third-party
qualified list and user rules as third-party for an unknown top-level site.
The result remains labeled **Unknown** in the inspector: the option does not
prove which site initiated the request. Explicit resource-domain allows and
matching list exceptions retain their existing precedence. Document-domain
restrictions cannot match without a known document host. Top-level navigation
and the Strict fallback for unlisted unknown-site resources are unchanged.

This is an opt-in compatibility tradeoff. A matching rule can block legitimate
content, including an embedded sign-in resource. Disable the option or use a
narrowly scoped allow rule if necessary. It does not intercept credentials,
rewrite authentication messages, hide browser APIs, or cover paths that do not
reach the resource handler.

## Verification

The x64 browser filtering harness uses a disposable profile and loopback-only
HTTP fixture. Its unknown-site scenario creates a fresh `about:blank` browser,
requests the same listed image with the option off and on, checks the server's
arrival count and checks that the inspector still reports Unknown. In [Windows
PR run 42787868248](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42787868248)
at `7380c6e078157fd54cff99ef7e1ba398e3802432`, the server observed **1**
request with the option off and **0** with it on; the filter reported the latter
blocked and Unknown. The same run passed 23 filtering checks, including known
first-party and third-party resources, redirects, site exceptions and
pause/resume. This small controlled measurement is not a real-world tracking
or anonymity score.

Core tests cover default compatibility, known first-party behavior, list and
domain exceptions, Custom list opt-in, unknown document-domain restrictions,
classification and profile settings persistence. The browser restart test
also checks that the new setting persists. That PR run passed 69 privacy checks,
the browser settings restart, controlled cross-profile cookie/localStorage
isolation, a synthetic OAuth-style popup/redirect/postMessage case, untrusted
TLS rejection and concurrent proxy probes. These are scoped fixture results.
Broader authentication-provider, permission, storage-clearing and network-leak
acceptance remain separate.

The baseline push [run 42783182924](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42783182924)
timed out waiting for the controlled SOCKS-offline load error in concurrent
proxy fixtures. The [PR run on the same head](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42783239290)
passed. This is an intermittent failure in the available evidence; its root
cause is not established and the proxy assertions were not weakened.

API availability is not proof of disclosure or of a site's use of an API.
Network filtering and the local exposure report do not guarantee anonymity,
prevent all fingerprinting or establish network containment.
