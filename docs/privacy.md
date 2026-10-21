# Privacy filtering

The filtering engine uses the existing .NET 8 core library and CefSharp resource interception. It does not change Chromium certificate validation, sandboxing, web security, permissions, identity, or authentication headers.

## Network rule format

Import UTF-8 Adblock Plus / EasyPrivacy-style network lists. Supported syntax: plain URL patterns, `*`, separator `^`, start/end `|`, hostname anchor `||`, `@@` exceptions, `third-party` / `~third-party`, `domain=example.com|~excluded.example.com`, `match-case`, and positive/negative resource types (`script`, `image`, `stylesheet`, `font`, `media`, `xmlhttprequest`, `subdocument`, `ping`, `other`). Resource type `websocket` parses but CEF interception of WebSocket traffic is not guaranteed.

This is a network filtering subset, not full EasyList/uBlock/AdGuard compatibility. Cosmetic filters, scriptlets, regex literals, redirects, header/cookie/CSP rewrites, procedural rules and unknown options are rejected as whole rules, and reported. Lists never execute code. Missing support cannot turn an exception with an unsupported option into an unrestricted exception. Limits: 8 million characters, 100,000 rules, 2,048 characters per rule, bounded regex matching.

First/third-party comparison uses registrable domains, including the Public Suffix List PRIVATE section, wildcard and exception rules, IDN normalization and exact IP hosts. Scheme and port do not affect this classification. It is separate from Chromium's origin and schemeful SameSite cookie policies. Unknown document origins are reported as unknown.

Top-level navigation always proceeds through the existing navigation/TLS policy. Non-HTTP(S) resources and the reserved diagnostics hostname are excluded from filtering. Domain allow rules take precedence over domain blocks, and list exceptions take precedence over list blocks. Filtering cancels resource loads synchronously; it never disposes or retains the request callback.

## Source attribution

`AegisBrowser.Core/public_suffix_list.dat` is a snapshot fetched 2026-10-21 from the official publicsuffix/list repository. The source header and MPL 2.0 notice are preserved. Maintainers should update it from https://publicsuffix.org/list/public_suffix_list.dat periodically; classification of newly delegated public/private suffixes depends on that snapshot.

## Privacy modes and precedence

- **Standard** enables the small bundled tracking list (or the user's replacement list), user network rules and domain rules.
- **Strict** adds a default block for known third-party subresources. Unknown document sites fail open and are labelled as unknown; this is not a complete network firewall.
- **Custom** applies user rules and domain rules, with a separate option to include the tracking list.

Decision order: internal resources and top-level navigation; temporary pause; per-site exception; resource-domain allow; ABP exception; resource-domain block; matching network block; Strict third-party fallback; allow. Site/domain exceptions include subdomains and use hostname-label boundaries. They do not override certificate errors or the browser's unsupported-scheme policy. CNAME cloaking, DNS/IP reputation, WebRTC traffic and DNS requests are outside this engine's scope.

The starter list is a deliberately limited set of common analytics hosts. It is not a complete copy of EasyPrivacy. Filter lists are user-controlled snapshots, not silently downloaded subscriptions. Large lists may include unsupported syntax or compatibility-breaking rules. Review the unsupported-rule report after importing and after switching modes.

## Using the Privacy window

Open **Privacy** on the toolbar. Choose a mode; edit one resource hostname or site-exception hostname per line. To remove an exception or rule, delete its line. User network rules use the syntax above. The Tracking list tab imports/replaces a UTF-8 list, restores the starter list or clears it. **Apply settings** validates and publishes the configuration for new requests; reload pages to retry previously blocked resources. Unsupported active list rules appear in their own tab.

The current site's hostname can be added to site exceptions. **Pause filtering** applies across this profile's tabs until resumed or browser exit. It never persists across restarts. Neither action weakens Chromium security protections.

The Network inspector shows the last 500 decisions with first/third-party classification, allowed/blocked result and matching rule. Counts include decisions since startup or Clear, not just visible rows, and do not imply successful HTTP responses. Choose **Third-party only** to narrow the view. Select a row to add its resource hostname to block/allow settings, then Apply. Inspecting is local and in-memory: origins only, without URL paths/queries/fragments, headers, cookies or bodies. Matching rule text may itself contain user-supplied patterns. Close/reopen the Privacy window to use a different current-site shortcut.
