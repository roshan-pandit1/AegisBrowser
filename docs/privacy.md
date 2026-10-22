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

## Windows acceptance cases (not yet executed interactively)

1. Open Privacy, switch each mode, Apply and reload. Confirm Standard blocks a listed third-party tracker, Strict blocks an unlisted third-party image, and Custom obeys only enabled rules.
2. Add a domain block, inspect the matching rule, then add/remove an allow rule and verify new request decisions change. Confirm `example.com.evil.test` does not inherit an `example.com` exception.
3. Add a site exception, reload, remove it and reload. Pause/resume filtering. Check counts and third-party-only view, and confirm Clear resets history/counts.
4. Import a standard privacy list containing supported and unsupported rules. Confirm unsupported entries appear, validation errors leave the previous policy active, and large imports remain usable.
5. Exercise top-level sign-in redirects and embedded identity-provider content. Use explicit exceptions if Strict breaks embedded authentication. No automatic security-check exceptions or challenge manipulation are provided.
6. Navigate through a redirect to a blocked resource, test iframe resources and cached/service-worker requests, then close loading tabs. Confirm no crash, no unexpected dialogs and no traffic reaches the blocked test endpoint.
7. Restart the same profile and a different profile. Confirm settings persist only in the first, while pause and inspector history reset. Check malformed/read-only settings files produce a visible error without silent data loss.
8. Open local diagnostics offline and DevTools. Verify invalid TLS certificates still fail, permission policy is unchanged and no security-disabling Chromium switches were added.

These cases require an actual Windows desktop. CI startup smoke does not establish their outcomes.

## Persistence, performance and compatibility

Settings are stored in `privacy.json` next to the profile's Chromium cache. Writes use a flushed temporary file followed by replacement in the same directory. A failed save leaves the active policy unchanged. Malformed settings trigger a visible warning, activate defaults and prevent overwriting the original file until it is repaired/renamed and the browser restarted. Settings are per profile; pause and network history are not persisted. Atomic replacement protects normal interrupted writes, but is not a backup against disk failure.

Hostname-only list rules use an index. Generic patterns use .NET's non-backtracking regex engine with a 50 ms per-pattern timeout and a 25 ms scan budget (checked between rules). If a budget is exhausted, the request is allowed and the inspector states this; availability takes precedence over blocking in this exceptional case. Large generic-pattern lists may therefore provide incomplete coverage. Policy compilation occurs only when applying settings, not in CEF callbacks; large imports can briefly occupy the settings UI.

Third-party classification is relative to the top-level page. The supported `domain=` restriction likewise uses the top-level hostname; this differs from engines that scope it to a nested frame's document. Unknown top-level contexts fail open for third-party-only rules. This interception is not a system-wide firewall: service-worker/cache paths, WebSockets, WebRTC and other Chromium background traffic may not have a usable browser/frame context or pass through these callbacks. The inspector is a resource-decision inspector, not a complete packet capture.

Strict mode can break embedded sign-in, payment widgets, video, fonts and CDN dependencies. Add a narrowly scoped resource-domain exception, or temporarily pause for the site and reload. No automatic authentication/security-check detection or bypass is implemented. Top-level navigation, redirects and POST semantics are not rewritten by this engine. The current release review incorporates main's native popup/OAuth compatibility changes. Real-provider acceptance remains required.

## Automated verification

Run the existing console tests with `dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64`. On Windows, build and run `AegisBrowser.FilteringTests` with `dotnet build AegisBrowser.FilteringTests -c Release -r win-x64 -p:SelfContained=true`, then run its generated executable. This harness uses the production request handler with a loopback HTTP fixture; it tests server-side arrival of allowed requests and absence of blocked direct/redirect/iframe requests, Strict mode, exceptions, pause/resume and top-level navigation. It also opens the native Privacy form, but does not automate editing every control.

The workflow runs on `windows-latest`, installs .NET 8, requires the harness's completion marker and exit code, and only then publishes and uploads the complete self-contained win-x64 directory. See BUILD-STATUS.md for actual results and remaining manual tests.
