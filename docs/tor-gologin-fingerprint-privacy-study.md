# Tor- and GoLogin-informed browser fingerprint privacy research

This note records a **clean-room, public-behavior comparison**, not extraction
of proprietary GoLogin/Orbita internals, a Tor Browser port, or an anti-fraud
bypass. AegisBrowser continues to use .NET 8, WinForms, CefSharp 154.0.340,
and its existing browser security defaults.

## Source and method

- User-provided reverse-engineering index:
  https://github.com/AIshihzong/awesome-reverse-engineer-anything
  This is a curated REA case/resource list, **not** GoLogin source code,
  a GoLogin fingerprint engine, or an executable decompiler session.
- Tor Browser's published anti-fingerprinting defenses:
  https://support.torproject.org/tor-browser/features/fingerprinting-protections/
- GoLogin/Orbita public feature descriptions:
  https://support.gologin.com/en/articles/14853789-orbita-browser
  https://support.gologin.com/en/articles/14810056-profile-fingerprint-settings
- AegisBrowser's source and tests on the parent branch, PR #62.

| Area | Tor Browser public design | GoLogin public design | AegisBrowser status / design |
| --- | --- | --- | --- |
| Fingerprint strategy | Reduce distinguishable buckets and restrict surfaces | Profile-specific customized identity | No fabricated identities; incremental privacy controls |
| Viewport | Letterboxing, about 200-by-100 size buckets | Configured profile screen metrics | **New opt-in WinForms viewport bucketing** (below) |
| Fonts | Restrict font enumeration/fallback | Profile font characteristics | No font normalization engine |
| Canvas/WebGL/audio | Some access restrictions or normalization in browser engine | Customized rendering-related characteristics | Diagnostics only; no changes to API outputs |
| Tracking/storage | First-party isolation and tracking defenses | Separate profile/browser state | Tested Chromium cookie protection and storage isolation in scoped fixtures |
| IP/WebRTC | Tor network path plus defenses | Profile proxies/WebRTC controls | Production required-proxy guard still blocks startup; no proven network containment |
| Browser engine | Firefox-derived Tor Browser | Customized Chromium/Orbita | Pinned stock CefSharp/CEF, not a custom engine |

The comparison is based on public documentation. It does not reverse-engineer
GoLogin's proprietary binaries or prove equivalent protections.

## Implemented slice: optional viewport letterboxing

The profile-local `LetterboxWebContent` setting defaults to `false`.
When enabled and the profile is restarted, each standard Chromium tab is
centered within its available WinForms TabPage display rectangle:

- Width is rounded **down** to a multiple of 200 control units.
- Height is rounded **down** to a multiple of 100 control units.
- Margins on opposing sides center the content.
- If a dimension is smaller than one step, that dimension is left unchanged
  so tiny panes remain usable.
- Browser tabs remain normal native CefSharp browser controls; navigation,
  filters, permissions, Chromium cookies and popup lifetimes are unchanged.
- The value is profile-local, saved through the existing atomic privacy
  settings store, and applied on profile startup. Changing it in Privacy
  requires restarting that profile. Existing profiles default to disabled.

This design is **inspired by Tor**, not an equivalent implementation.
WinForms control dimensions are not guaranteed to equal the CSS-pixel viewport
at non-100% Windows DPI or with browser zoom. Other exposed metrics
(`screen.width`, `screen.availWidth`, `outerWidth`, device pixel ratio,
Canvas, WebGL, fonts, audio) remain unchanged. Popups and DevTools are not
letterboxed. Browser detection, IP leakage, and cross-site fingerprint
correlation remain possible. Applying a unique option can itself affect
fingerprint distinctiveness. No anti-fraud evasion or browser identity
spoofing has been added.

## Implementation boundaries

The code does not:

- Override JavaScript APIs or HTTP headers.
- Add random noise or synthetic hardware identity values.
- Change CefSharp packages, command-line flags, sandbox/TLS/permission policy.
- Change the required-proxy pre-start guard.
- Inject scripts into visited pages, create hashes or contact external sites.
- Copy GoLogin's proprietary assets or code.

This is a narrow attempt to reduce one high-entropy signal while preserving
normal browsing by default. Different-sized viewports may break responsive
layouts, and changing the option requires a restart to avoid mixed states.

## Verification and acceptance

The core console tests check the 200-by-100 bucketing algorithm, host bounds,
small-pane behavior and invalid dimensions. Privacy tests check default-off,
per-profile persistence and backward compatibility with older JSON settings.

The following require **actual Windows GUI/CEF acceptance** and must not be
reported as verified merely because the core tests pass:

1. Check real `window.innerWidth`/`innerHeight` alongside control bounds at
   100%, 125%, 150%, and 200% display scaling and different browser zoom.
2. Resize the main window, maximize, minimize, restore, move across monitors
   and switch between tabs. Confirm no stalled native render or unnecessary
   browser creation.
3. Confirm window-size bucketing after first tab initialization and navigation,
   plus layout changes while DevTools are open.
4. Verify ordinary browsing, forms, videos, popup/OAuth, profile restart,
   setting toggles and tab/native close behavior.
5. Confirm that changing this setting does not alter permissions, TLS
   rejection, third-party cookie policy or required-proxy startup refusal.
6. Measure distribution/entropy independently before claiming reduced
   fingerprintability. Letterboxing without a shared user population may not
   improve unlinkability.

Acceptance gates:

| Area | Status at implementation |
| --- | --- |
| Per-profile configuration and opt-in default | Implemented; automated settings tests |
| Deterministic viewport-bucket calculations | Implemented; core unit tests |
| Real Chromium CSS viewport at multiple DPI scales | UNVERIFIED |
| Complete fingerprint resistance | NOT ESTABLISHED |
| Network leak containment | INCOMPLETE |
| Site/GUI compatibility and production readiness | NOT APPROVED |

Additional feature candidates should be separately evaluated for supported
engine-level controls and compatibility. Tor's Firefox-specific mechanisms
cannot be pasted into Chromium/CEF; GoLogin's Orbita changes would require
independent engine work rather than superficial JavaScript overrides.
