# P4-FP-01: Browser-native fingerprint-surface reduction

## Goal and threat-model boundary

AegisBrowser now offers optional per-profile browser-native restrictions for
WebGL and remote web fonts. This is a small privacy hardening step, **not full
fingerprint resistance**, a fingerprint spoofing engine, anonymity, or
unlinkability. No browser can promise that a site will not recognize its
combination of capabilities, platform characteristics or browsing behavior.

The parent is PR #64 at `3f1a66d8349c732b7524d6df7dd469983d2dedd2`,
which remains the base of this stacked draft. All production package pins,
sandbox, TLS policy, permission handler, third-party cookie preference,
popup owner lifecycle and `ProxyRepository.EnsureRuntimeReady` are unchanged.

## Verified source capability

The CEF 154 browser settings structure and CefSharp's `IBrowserSettings`
expose `WebGl` and `RemoteFonts` fields. These are per-browser initialization
settings. This change sets `CefState.Disabled` only when explicitly selected
and otherwise preserves the CEF defaults:

- https://cef-builds.spotifycdn.com/docs/154.0/structcef__browser__settings__t.html
- https://github.com/cefsharp/CefSharp/blob/master/CefSharp/IBrowserSettings.cs
- https://github.com/cefsharp/CefSharp/blob/master/CefSharp.WinForms/ChromiumWebBrowser.cs

Tor Browser's anti-fingerprinting design depends on multiple Firefox/Gecko
engine-level protections and a shared population with similar behavior.
These two CefSharp settings do not recreate that architecture:

- https://support.torproject.org/tor-browser/features/fingerprinting-protections/

GoLogin/Orbita's publicly described profile characteristics are not copied;
no hardware identifiers, navigator data, user agent, WebGL strings or canvas
pixels are replaced with fabricated values.

## User-visible behavior

Both settings are off by default and saved in the existing atomic,
per-profile `privacy.json`:

| Setting | Backend | Compatibility |
| --- | --- | --- |
| `DisableWebGl` | `BrowserSettings.WebGl = CefState.Disabled` | 3D graphics, WebGL canvas and some interactive sites may stop working |
| `DisableRemoteFonts` | `BrowserSettings.RemoteFonts = CefState.Disabled` | Web fonts may fall back to local fonts; text layout can change |

The Privacy dialog describes the settings, effective startup values and
restart requirement. `MainForm` attaches `BrowserSettings` before creating
each ordinary tab's native CEF browser. Older profiles with no fields use
the default false values. No settings are applied retroactively to existing
CEF instances. Unprotected mode does not create custom `BrowserSettings`.

This is not a global WebGL ban on the GPU process. It does not establish
native popup behavior; that needs separate testing.

## Native controlled fixture

The new `fingerprint-write` and `fingerprint-read` commands launch CEF
in a disposable profile and reopen profile settings across a process restart.

They exercise a local in-process HTTPS scheme fixture with no public network
traffic:

1. Confirm the exact CEF settings values for each independent combination.
2. Load an ordinary Chromium tab with the setting off and record whether a
   page obtains a real WebGL rendering context. The baseline may lack GPU
   support on a particular runner; it is not assumed to be available.
3. Load the restricted tab and require
   `canvas.getContext('webgl') === null`.
4. Use an authored CSS `@font-face` definition with a unique invalid
   synthetic font payload. Record whether the browser requests the font
   resource. Require a baseline request and no restricted-mode request.
   The fixture tests attempted font fetching, not visual font correctness.
5. Verify ordinary HTML remains readable, both native browser windows
   close normally and the CEF subprocess shutdown completes.
6. Reopen the same settings in a separate read process to establish
   persistence. Store and inspect synthetic booleans and request counters,
   not personally identifying values.

These are empirical, narrowly scoped tests. A source-defined property
alone is not proof that the engine applied a restriction. If a native test
fails, the feature remains unverified and must not be promoted as ready.

## Existing protections and remaining risks

Other layers of AegisBrowser already include the profile-specific cookie
preference, network rule filtering, local privacy diagnostics and optional
viewport control-unit letterboxing. These layers do **not** establish
complete defense against fingerprinting. In particular:

- Canvas 2D readback is unchanged.
- Browser and OS features, font enumeration/metrics, audio, screen, timezone,
  CPU count and device memory remain observable.
- WebGPU and other graphics APIs are not independently restricted.
- WebRTC, DNS, proxy routing and broader network-leak coverage remain
  unverified; required-proxy production startup remains fail closed.
- Browser versions and client hints remain observable.
- Disabled capabilities may make this browser more distinctive.
- Popup browser inheritance is not verified.
- Public HTTPS site compatibility and assistive-technology impact are
  unreviewed; remote fonts can affect accessibility or localization.
- It is incorrect to describe the opt-in settings as "full fingerprint
  resistance", untraceability, Tor-equivalence or evasion of anti-fraud tools.

## Acceptance matrix

| Area | Status before final-head CI |
| --- | --- |
| Default-off opt-in settings | Implemented, core tests added |
| Profile persistence and legacy compatibility | Core tests added |
| CEF WebGL initialization setting | Source supported; native test required |
| WebGL rendering context suppressed | Native test required |
| CEF remote-font initialization setting | Source supported; native test required |
| Remote font request suppressed | Native test required |
| Ordinary browser HTML | Native test required |
| Native CEF shutdown | Native test required |
| Cookie/TLS/proxy regressions | Existing Windows CI required |
| Popups inherit same restrictions | INCONCLUSIVE |
| Local installed font enumeration | NOT PROTECTED |
| Canvas/audio/WebGPU/screen/network identity | NOT PROTECTED |
| Global fingerprint unlinkability | NOT ESTABLISHED |
| Production readiness | NOT_APPROVED |

Document exact final-head CI results and any failed runs in the PR. Do not
interpret a successful local build as native Chromium verification.
