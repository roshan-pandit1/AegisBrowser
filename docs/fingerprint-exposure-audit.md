# Local fingerprint exposure audit

Status: **diagnostic feature only**. This feature does not spoof, randomize, or rewrite the browser fingerprint and does not attempt to evade fraud-detection systems.

## Purpose

The local `aegis:diagnostics` page reports the availability of nine commonly queried browser API surfaces:

- Canvas 2D
- WebGL
- AudioContext
- WebRTC
- User-Agent Client Hints
- Hardware concurrency
- Device memory
- Screen dimensions
- Timezone

The audit reports `API available`, `Not observed`, or `Unavailable`. It intentionally does **not** render canvas pixels, synthesize an audio fingerprint, enumerate media devices, initiate WebRTC connections, request high-entropy Client Hints, generate fingerprint hashes, contact external servers, or persist an audit.

The existing separate browser-values report continues to show local diagnostic observations (including WebGL details). Refresh only reruns the checks within the embedded local page.

## Interpretation

- An available API can potentially expose device or environment characteristics, but an availability check does not show that a website has used it.
- Missing API availability does not guarantee fingerprint resistance; browsers can be identified through many combinations of rendering, network, timing, and behavior.
- The local diagnostics page's Content Security Policy denies external connections, images, frames, and forms. The feature is bundled as an embedded resource; it does not upload data.
- Strict privacy filtering can block some third-party subresources but does not cover every request path, first-party scripts, DNS, WebRTC networking, or fraud-detection mechanisms.
- Profiles separate local storage. They do not change public IP address, hardware, network identity or site-specific sign-in state.

## Verification

The CEF browser integration test checks that the audit renders and that the nine expected surface labels are present. This is a UI-functionality check, not an anti-fingerprinting effectiveness test.

Manual acceptance on Windows x64:

1. Open `aegis:diagnostics` in a disposable profile.
2. Confirm the local audit lists nine surfaces without any permission prompt.
3. Select **Refresh report**, confirm the audit rerenders.
4. With Chromium DevTools, confirm there is no outbound request from the diagnostics page.
5. Verify existing browser-values diagnostics still work and regular sites retain standard Chromium behavior.

## Future privacy work

Privacy improvements should prioritize independently measured reduction of unnecessary cross-site tracking, strict origin and permission boundaries, leak testing, and clear compatibility effects. Avoid claiming detection-system bypass or anonymity without a reproducible, appropriate threat model and evidence.
