# Privacy filtering

The filtering engine uses the existing .NET 8 core library and CefSharp resource interception. It does not change Chromium certificate validation, sandboxing, web security, permissions, identity, or authentication headers.

## Network rule format

Import UTF-8 Adblock Plus / EasyPrivacy-style network lists. Supported syntax: plain URL patterns, `*`, separator `^`, start/end `|`, hostname anchor `||`, `@@` exceptions, `third-party` / `~third-party`, `domain=example.com|~excluded.example.com`, `match-case`, and positive/negative resource types (`script`, `image`, `stylesheet`, `font`, `media`, `xmlhttprequest`, `subdocument`, `ping`, `other`). Resource type `websocket` parses but CEF interception of WebSocket traffic is not guaranteed.

This is a network filtering subset, not full EasyList/uBlock/AdGuard compatibility. Cosmetic filters, scriptlets, regex literals, redirects, header/cookie/CSP rewrites, procedural rules and unknown options are rejected as whole rules, and reported. Lists never execute code. Missing support cannot turn an exception with an unsupported option into an unrestricted exception. Limits: 8 million characters, 100,000 rules, 2,048 characters per rule, bounded regex matching.

First/third-party comparison uses registrable domains, including the Public Suffix List PRIVATE section, wildcard and exception rules, IDN normalization and exact IP hosts. Scheme and port do not affect this classification. It is separate from Chromium's origin and schemeful SameSite cookie policies. Unknown document origins are reported as unknown.

Top-level navigation always proceeds through the existing navigation/TLS policy. Non-HTTP(S) resources and the reserved diagnostics hostname are excluded from filtering. Domain allow rules take precedence over domain blocks, and list exceptions take precedence over list blocks. Filtering cancels resource loads synchronously; it never disposes or retains the request callback.

## Source attribution

`AegisBrowser.Core/public_suffix_list.dat` is a snapshot fetched 2026-10-21 from the official publicsuffix/list repository. The source header and MPL 2.0 notice are preserved. Maintainers should update it from https://publicsuffix.org/list/public_suffix_list.dat periodically; classification of newly delegated public/private suffixes depends on that snapshot.
