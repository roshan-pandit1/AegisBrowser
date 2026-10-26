# AegisBrowser threat model

Audit context: the local `release/windows-rc` working tree over `9fd457276d3e741ade1041ea37655115b7beea4c`, including pre-existing uncommitted Smart Search and earlier `AegisBrowser.Lab` work. This is not the PR #7/redesign checkout saved elsewhere. No remote reconciliation or release verification succeeded in this environment.

## Assets and trust boundaries

| Boundary | Assets / adversary | Current controls and limitations |
| --- | --- | --- |
| Web content → Chromium/OS | Malicious scripts, renderer exploits, cross-origin data | Chromium TLS/SOP/CORS defaults retained; no privileged JS bridge. CefSharp native sandbox assurance is absent; see limitations. Browser profile users are not OS security principals. |
| Web content → local diagnostics | Embedded page data / unexpected internal URL methods or paths | Exact HTTPS/root/default-port GET main-document gate, no embedding or CORS grants, CSP hashes, no network/report upload. No privileged internal actions exist. |
| Page → permission prompt | Cross-origin or insecure capture requests | Same normalized origin and secure/loopback gate; supported requests reach Chromium consent, others deny. Native revocation/persistence UX still needs Windows acceptance. |
| Requests → privacy engine | Malformed URLs, oversized inputs, ambiguous filter syntax, expensive matching | IDN normalization, bounded input/list sizes, non-backtracking patterns and cooperative budget; explicit coverage counters. Fail-open paths and unobserved traffic remain possible. |
| Local settings → active policy | Corrupt edits, interrupted writes | Validation and existing same-directory atomic replacement; previous policy retained on save failure. UI filter compilation moves off-thread; cancellation before publication. No automatic remote list replacement. |
| Process → profile | Concurrent writers, path traversal, crashes | Restricted names, separate roots and exclusive OS handle; no lock-file deletion. Same-user file tampering/junctions and full-disk encryption are outside the app's boundary. |
| Web/local clients → mock lab | Forged decisions, stale grants, reset races, resource exhaustion | Loopback Kestrel, Host/Origin gates, random session IDs, server expiry, bounded registry, versioned decision snapshots, reset removal and shutdown revocation. Only synthetic data; no real provider trust. |
| Build → distribution | Dependency compromise, stale binaries, profile leakage | Consistent pins, proposed updates, transitive audit gates, package/file SBOM, payload exclusions, clean checkout requirement, ZIP hashes and existing smoke workflow. No signed release was produced. |

## Security invariants

- A client-supplied decision string cannot authorize the mock protected endpoint.
- Revoked/expired sessions cannot be recreated by an in-flight evaluation; a server closing to new work cannot issue sessions.
- TLS errors remain rejection paths; diagnostics response hardening does not alter normal response bodies or certificates.
- Existing profile and privacy settings are preserved. There is no identity spoofing, stealth patch, CAPTCHA solving or anti-fraud bypass.
- Diagnostics and lab reports must not record request bodies, headers, credentials or raw query strings. Native Chromium logs are not covered by application redaction guarantees.
- Native browser owners stay alive until CEF's existing close callbacks permit adapter disposal; exceptions are not treated as successful shutdown.

## Out of scope / residual threats

A compromised OS account, native Chromium/CEF vulnerabilities, malware, kernel/network attacks, compromised build infrastructure, malicious developer tools, real-provider acceptance and OS sandbox equivalence are not solved by this patch. Filtering is not an egress firewall or authorization mechanism. Sites may use normal first-party requests, service workers/cache, WebRTC or unobserved network paths. Mock lab outcomes are not real security certifications.
