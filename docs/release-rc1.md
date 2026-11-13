# AegisBrowser RC1

RC1 is a Windows x64, .NET 8 and CefSharp 154 release candidate. It is not a claim of anonymity, complete fingerprint resistance or network containment.

## Included and deferred

| Feature | Origin | Decision and limits |
| --- | --- | --- |
| Profiles, leases, navigation, filtering, permissions and native popups | Existing `main` | Included; profiles remain separate and a lease prevents concurrent use of one profile. |
| Third-party cookie blocking | #59 | Included as an opt-in profile setting; embedded sign-in and other content may break. |
| WebGL and remote-font restrictions | #64 | Included as separate opt-in settings. WebGL restriction covers tested HTMLCanvas contexts, not OffscreenCanvas in pages or workers; remote-font blocking does not hide installed fonts. |
| WebRTC UDP preference | #66 | Included as an opt-in setting; it can break calls and does not provide network containment. |
| Viewport letterboxing and zoom-aware layout | #63, #71, #72 | Included as opt-in. Exact CSS buckets, scrollbars, display scaling and mixed-DPI behavior have limits; screen properties remain visible. |
| SOCKS test outcome hardening | #73 | Included as test-only coverage. The intermittent stopped-listener timeout remains unresolved. |
| Clear current-profile cookies | #74 | Deferred: safe finite shutdown with a missing CEF callback has not been established. |
| Chromium OffscreenCanvas prototype and custom engine | #69, #70 | Experimental; excluded. Production uses the pinned CefSharp packages. |

On a local Windows ARM64 host using x64 emulation, one viewport restart test failed its initial centered-geometry assertion; the actual and expected bounds were not captured, and the cause is unconfirmed. Hosted Windows x64 results must be assessed independently.

Privacy settings are profile-local and relevant Chromium settings require a profile restart. Filtering and cookie protection can affect embedded authentication; no automatic exceptions are added.

## Starting and using RC1

Extract the complete portable ZIP to a writable directory and run `AegisBrowser.exe`. Use `--profile=NAME` for a named profile or `--profiles` for the profile manager. Profiles are stored separately under `%LOCALAPPDATA%\AegisBrowser\Profiles`, not inside the portable folder. Open **Privacy** from the browser toolbar to configure the available controls.

Normal Chromium certificate validation and sandbox settings remain in effect. Profiles requiring proxy containment remain blocked by `ProxyRepository.EnsureRuntimeReady`; the test fixtures do not prove system-wide containment. WebGL workers and other graphics, Canvas, audio, font, screen and network surfaces remain observable. Compatibility with every website is not established.

## Build and verification

Build on Windows x64 with the .NET 8 SDK and the repository's pinned CefSharp dependencies. The Windows workflow publishes a self-contained application, packages it, verifies the archive contents and runs the extracted-browser smoke test. Its run records and the generated SHA-256 sidecar provide the actual final-commit package provenance; consult the release PR for the completed run URLs and hash. Package creation alone is not proof of HTTPS navigation or all privacy functionality. Manual and automated results must be reported with their actual scope.
