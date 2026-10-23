# Address bar and search settings

Type an address or query and press Enter. Typing alone performs no search, suggestion request or prefetch. Searches load in the current tab and use normal Chromium Back/Forward history. Ctrl+L focuses the address bar.

| Input | Result |
| --- | --- |
| `news` / `best laptops 2026` | Search with the selected engine |
| `github.com` | `https://github.com/` |
| `https://example.com/a%20b` | Direct navigation; existing URL escapes retained |
| `bücher.de` | HTTPS navigation using the IDN's ASCII hostname |
| `localhost:8080` / `127.0.0.1:8080` | HTTP loopback navigation |
| `[::1]:8080` | HTTP IPv6 loopback navigation |
| `[2001:db8::1]:8443` | HTTPS IPv6 navigation |
| `aegis:diagnostics` / `about:blank` | Supported internal page |

Bare domains and non-loopback IPs default to HTTPS. Localhost, its subdomains and loopback IPs default to HTTP for local development; explicit HTTP or HTTPS takes precedence. Brackets are required for IPv6 with a port. A bare IPv6 literal has no port. Single-label names such as `news` are searches; use an explicit URL such as `http://intranet` for a single-label server. Surrounding whitespace is removed. Unicode queries and literal query punctuation are encoded once as search terms. Existing URLs are parsed separately from search terms.

Unsupported schemes (including javascript, data, file, ftp and chrome), URL credentials, backslashes and embedded control characters are rejected. Empty input does nothing. TLS certificate validation and Chromium web-security protections remain enabled. Existing privacy rules also apply to search pages and can affect their compatibility.

## Choose an engine

Open **Search** on the toolbar, choose DuckDuckGo (default), Google, Bing or Brave Search, and click **Save settings**. The window identifies the current engine; the address-bar placeholder also reflects it. Changing the engine does not navigate or send a query.

Selection is stored atomically in `search.json` alongside the profile's `privacy.json`, under `%LOCALAPPDATA%\AegisBrowser\Profiles\profile-<name>`. Other profiles retain their own selection. Restart with the same `--profile` argument to restore it. Search templates are fixed HTTPS URLs with a `{searchTerms}` placeholder; arbitrary custom template editing is not supported.

If settings cannot be read, the app warns and uses DuckDuckGo without overwriting the original file. Close the app, repair or rename `search.json`, and restart before saving. Failed saves do not change the active engine.

## Verification and remaining manual acceptance

The console suite covers classification, encoding, unsafe schemes, all four engine templates and profile isolation/corruption. Windows browser tests exercise no navigation while typing, submitted searches in the current tab, Back/Forward, provider changes and decoded Unicode queries using local HTTPS fixtures. A separate process-restart test verifies the saved selection through the production settings UI.

Manual acceptance is **not performed** by these tests. On Windows, check all four real providers, Enter and Ctrl+L with web content focused, navigation in multiple tabs, 100/150/200% display scaling, keyboard-only settings operation, restart with two named profiles, and saving on an unwritable profile. Confirm existing privacy, diagnostics, popup and shutdown acceptance cases in `windows-acceptance-v0.1.md`. Real-provider availability, OAuth and CAPTCHA behavior are not simulated or bypassed. See `BUILD-STATUS.md` for actual CI results.
