# P4-02: third-party cookies and storage

This branch starts at PR #58 commit `5aa60c8ebbe312e206b284e3e4eabab07f8608ee`.
It uses CefSharp.WinForms.NETCore 154.0.340 and CEF
`154.0.34+g14c5a08+chromium-154.0.8037.98`; it does not replace the runtime.

## Capability audit

The labels describe evidence in this project, not a claim about every Chromium
configuration.

| Capability | Status and evidence |
| --- | --- |
| Persistent first-party cookies | PARTIALLY_SUPPORTED: a Secure persistent test cookie survived a real process restart; other cookie types and shutdown failures are not covered. |
| Third-party cookies | PARTIALLY_SUPPORTED: the controlled cross-site loopback iframe sent and saved unpartitioned SameSite=None cookies by default in a fresh test profile. |
| SameSite and Secure | PARTIALLY_SUPPORTED: Lax and Strict cookies were absent on the controlled cross-site iframe request; a Secure SameSite=None cookie was accepted on localhost. Public HTTPS and all redirect cases are not established. |
| Blocking third-party cookies | PARTIALLY_SUPPORTED: the pinned CEF context accepted and enforced the tested Chromium preference for the loopback scenario below. Exceptions, custom handlers and all storage/network paths are not covered. |
| Partitioned cookies / CHIPS | UNKNOWN: not measured here; blocking unpartitioned third-party cookies does not imply partitioned cookies are blocked. |
| Cookie exceptions / Storage Access API | UNKNOWN: no new per-site cookie exception or Storage Access API UI is implemented. Chromium may apply its own rules and exceptions. |
| localStorage | PARTIALLY_SUPPORTED: one in-process HTTPS fixture observed separate data for the same embedded origin under two top-level sites. This is not a general proof for all origins. |
| sessionStorage, IndexedDB, CacheStorage and service workers | UNKNOWN: cross-site partitioning was not tested. |
| Profile isolation | PARTIALLY_SUPPORTED: existing CEF integration tests exercise distinct request contexts and persistent cookie/localStorage data for two profiles. |
| Cookie deletion and expiration | PARTIALLY_SUPPORTED: the fixture expires its own synthetic cookie before a case. General clearing and recovery are not newly tested. |

Production launches one process and one CEF cache for each leased profile.
Tabs in that process use its global request context. CEF's request-context
preference API must run on its UI thread. The pinned
[Chromium 154 cookie settings source](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/components/content_settings/core/browser/cookie_settings.h)
defines mode 0 as off, mode 1 as block third party and mode 2 as incognito-only.
[CEF's request-context API](https://cef-builds.spotifycdn.com/docs/154.0/classCefRequestContext.html)
exposes preferences and content settings; the installed CefSharp binding
exposes `HasPreference`, `CanSetPreference`, `SetPreference` and
`GetPreference`. Preference availability and actual behavior were also
checked in the fixture. The separate CEF cookie access filter only describes
resource-request callbacks and is not used to guess top-level sites or rewrite
cookie headers.

## Setting and failure behavior

The new Privacy checkbox is opt-in. A missing field in older `privacy.json`
files means no Aegis override, preserving the existing Chromium preference.
The explicitly enabled value requests mode 1. Explicitly disabling it resets
the preference to Chromium's default, rather than forcing mode 0. A setting
change is saved per profile and takes effect after a profile restart; the
window separately displays the startup result.

Before the first tab, Aegis checks the preference exists, is writable, is
accepted and reads back as mode 1. If it cannot establish that result, it
does not open tabs and shows an error; the user can disable the setting in
Privacy and restart. If restoring the default after opting out fails, Aegis
warns that Chromium's existing policy remains in effect and permits browsing;
it does not report the reset as successful. A failed settings-file write does
not publish the new configuration. Fake-preference tests cover missing, read-only, rejected and
incorrect-readback cases. The inspector continues to report network-filter
decisions only; it does not infer per-request cookie outcomes.

The readback confirms the preference value, not an independent acknowledgement
from every Chromium network or storage component. The fixture exercises
subsequent requests; it does not prove ordering for every possible request
immediately after startup or all background activity.

This preference does not promise that every third-party storage mechanism is
blocked. Chromium may allow partitioned cookies or user/browser exceptions.
The existing resource-filter exceptions and pause control do not modify cookie
settings. There is no automatic authentication-provider exception. Embedded
sign-in, payment and media widgets can stop working; the user can disable this
profile option and restart. The option does not hide the IP address or change
fingerprinting APIs.

## Controlled measurements

The Windows x64 test process uses a disposable CEF profile. A loopback-only
HTTP server serves `localhost` as the embedded site and `127.0.0.1` as the
top-level site; these are different sites, not two ports on the same site.
Chromium accepted the fixture's Secure cookies on localhost. The server records
only whether fixed synthetic cookie markers were present, never their values.
The request itself is allowed in both cases:

| Observation per case | Mode 0 baseline | Mode 1 enabled |
| --- | ---: | ---: |
| Cross-site iframe requests received | 1 | 1 |
| Requests containing the ordinary SameSite=None test cookie | 1 | 0 |
| Requests containing the HttpOnly SameSite=None test cookie | 1 | 0 |
| Requests containing the Lax / Strict test cookies | 0 / 0 | 0 / 0 |
| Third-party response cookie subsequently found first-party | yes | no |
| First-party test cookie accessible | yes | yes |

The enabled result was repeated after a process restart, with the saved setting
applied by the production policy code before browser creation. A persistent
first-party test cookie also survived that restart. A controlled OAuth-style
popup, redirect, opener and postMessage flow completed while mode 1 was active;
this does not establish compatibility with real providers or embedded login.

A second fixture supplies three distinct `https://*.invalid` origins through
CEF's in-process scheme handler. For one embedded origin, a unique localStorage
marker was initially absent on Site A, present when revisiting Site A, and
absent on Site B. The embedded document's cookie observation changed from
present to absent with mode 1. The in-process handler still saw the synthetic
cookie marker in its request headers in both modes. Its callback is not an
independent HTTPS server and cannot establish whether a real TLS request would
transmit that header. No TLS exception or operating-system trust change was
introduced. The real server measurement is HTTP loopback, not public HTTPS;
independent controlled HTTPS transmission remains unverified.

The fixtures make no public network requests and log no credentials, cookie
values or session identifiers. A request with cookies suppressed is not a
blocked network resource. These small cases are not a real-world tracking,
fingerprinting or anonymity score.

## Verification and remaining work

The Windows workflow runs the probe in separate write and read processes,
requires successful native exits and CEF shutdown markers, and retains the
existing browser, filtering, TLS and concurrent-proxy tests. The local x64
run on Windows ARM64 emulation observed the measurements above, including
normal CEF shutdown; hosted Windows x64 results are reported by the draft PR's
checks. A previous P4-01 concurrent SOCKS timeout remains an intermittent
observation whose root cause was not established; its assertions are unchanged.

Remaining work includes a controlled real HTTPS fixture without weakening
certificate verification; CHIPS and Storage Access API behavior; same-site
subdomains, redirects and fetch/subresource cases; IndexedDB, CacheStorage and
service-worker partitioning; cookie deletion; wider authentication and payment
compatibility; and broader Windows x64 repetition. The resource filter does not
establish system-wide proxy containment, DNS/WebRTC leak prevention or
anonymity. Production readiness remains subject to review and final-head CI.
