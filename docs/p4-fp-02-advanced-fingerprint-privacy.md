# P4-FP-02: Advanced fingerprint privacy

## Scope and evidence

This research starts from PR #65, branch
`feature/p4-fingerprint-surface-reduction`, commit
`5126831335ffe868f146021f46fc95c3a2c5024b`. The pinned stack is .NET 8,
CefSharp.WinForms.NETCore `154.0.340`, CEF
`154.0.34+g14c5a08+chromium-154.0.8037.98`, and Windows x64. CEF source is
`14c5a089e8452874cb1a556dc3fde26ed2d87723`; CefSharp source is
`ec1027aa21e1af1217681a349104c15d29bbc21a`.

This document separates upstream source capabilities, prior parent-branch
observations, and new measurements. A setting's existence or readback is not
proof that every renderer or network path enforces it. Disabling an API can
itself make a browser more distinctive. No anonymity, unlinkability, complete
fingerprint resistance, or Tor Browser equivalence is asserted.

## Browser-native capability audit

| Surface | Capability and scope | Assessment |
| --- | --- | --- |
| Canvas 2D and OffscreenCanvas | The pinned CEF browser-settings structure exposes no dedicated readback permission or restriction. Blocking GPU acceleration is not equivalent to blocking readback. | No supported control established; engine work would be required for a targeted restriction. Canvas rendering and readback remain exposed. |
| AudioContext and OfflineAudioContext | The examined CEF settings provide no targeted restriction of rendered audio characteristics. Microphone permission controls govern device capture, not offline rendering. | No supported control established; output is not modified. |
| WebGPU | Pinned Chromium enables the GPU service feature by default on supported Windows builds. The reviewed per-browser CEF settings have no independently configurable WebGPU field. | A process feature is not a demonstrated per-profile control; effects on adapters, workers and popups remain unmeasured. |
| Local Font Access | Chromium registers a desktop `LOCAL_FONTS` content setting with ASK, ALLOW and BLOCK; CefSharp exposes `ContentSettingTypes.LocalFonts` and request-context content-setting APIs. | Supported with limitations. The existing AegisBrowser prompt handler denies permissions other than its explicitly supported types, but real API behavior and retained site grants require independent testing. CSS fallback and font metrics remain exposed. |
| WebRTC IP handling | Chromium describes `disable_non_proxied_udp` as a WebRTC policy; CEF preference availability, writability, and actual effect must be verified in the selected request context. | Supported candidate with limitations, not evidence of complete network containment. |
| WebGL and remote web fonts | CEF has per-browser `WebGl` and `RemoteFonts` settings. | Parent PR implements opt-in restrictions; popup, worker and other context behavior must not be inferred from a tab result. |

Relevant upstream sources:

- [CEF 154 browser settings and permission types at the pinned revision](https://raw.githubusercontent.com/chromiumembedded/cef/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/internal/cef_types.h).
- [CEF permission callback contract at the pinned revision](https://raw.githubusercontent.com/chromiumembedded/cef/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_permission_handler.h). Some permission types are platform- or Chrome-style-specific; absence of a prompt is not proof of a denial.
- [Chromium 154 content-setting registry](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/components/content_settings/core/browser/content_settings_registry.cc), including the `LOCAL_FONTS` entry and its secure-origin exception rules.
- [Chromium 154 GPU features](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/gpu/config/gpu_finch_features.cc), including `kWebGPUService`.
- [Chromium WebRTC IP handling policy](https://chromium.googlesource.com/chromium/src/+/376fc41e87a058f7a7b300b0ec3a4982b4ec0960/components/policy/resources/templates/policy_definitions/Miscellaneous/WebRtcIPHandling.yaml). This is upstream policy documentation, not proof of a particular CEF embedding's behavior.
- [Tor Browser fingerprinting protections](https://support.torproject.org/tor-browser/features/fingerprinting-protections/). Tor's coordinated browser-engine defenses and user population are not reproduced by individual CEF settings.

The pinned CefSharp package's `IRequestContext` documentation describes
`GetContentSetting` and `SetContentSetting`; empty URLs address the default
setting in a non-incognito context. A default BLOCK does not automatically
establish how a more specific stored exception is handled. An application must
not silently erase existing grants to manufacture a passing result.

## WebRTC restriction and limits

The opt-in profile setting requests Chromium's
`webrtc.ip_handling_policy = disable_non_proxied_udp` before browsing, with
application and readback checked and profile restart required. Legacy profiles
preserve their existing preference; an enabled policy that cannot be applied
and read back prevents tab creation. Disabling the option requests removal of
the override; it does not prove the effective policy in the presence of other
settings.

The intended scope is the affected CEF request context and its associated
browser processes; popup, frame, and worker coverage requires separate
verification. The policy may affect peer-to-peer calls and media connectivity.
Users must be able to disable it and restart the profile. A restriction on
non-proxied UDP does not prohibit all direct TCP, DNS, STUN or other traffic,
does not hide the public IP address, and does not prove proxy containment.
Absence of one packet in one local listener is not a proof of network isolation.
`ProxyRepository.EnsureRuntimeReady` must continue blocking required-proxy
startup while containment is unverified.

Separate disposable CEF processes apply the production policy before browser
creation and then attempt STUN on a loopback UDP listener for eight seconds.
On the development host (Windows ARM64, x64 emulation), one baseline run sent
a STUN packet; the restricted run read back the requested preference and no
packet arrived at that listener. Both native processes exited normally after
CEF shutdown. The restricted absence is bounded evidence for that listener,
not proof of an ICE completion boundary, behavior on other interfaces, or
network containment. A separate existing proxy probe changes the preference
after browser creation and is not evidence of startup ordering.

## Existing evidence and remaining context matrix

The parent PR's controlled fixture tests a tab's WebGL context availability,
remote-font request counts, ordinary HTML, and profile setting persistence;
see [its detailed test methodology](fingerprint-surface-reduction.md). The
parent's final-head [push](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42971274896)
and [PR](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42971342899)
Windows workflows passed. That prior evidence is scoped to
the parent fixture; it does not establish this branch's results or validate
native popups, cross-origin frames, shared workers, service workers, or WebGPU.

| Context | Evidence still required for the new option |
| --- | --- |
| Main tab | Preference established before navigation and an actual controlled network observation. |
| Same-origin and cross-origin iframe | Actual WebRTC behavior under the inherited or applicable context. |
| Dedicated/shared/service workers | Browser support and actual policy behavior; do not assume each API is available. |
| Native popup | Separate observation using the production popup and request-context lifecycle. |
| Restart and multiple profiles | Persistence, isolation and startup readback in independently launched processes. |

The local `fingerprint-write` CEF run completed 60 checks and normal shutdown.
In its controlled main frame, same-origin frame and cross-origin frame, Canvas
readback and an offline audio render were functional, `AudioContext` and the
Local Font Access API were present, and WebGPU adapter requests were
inconclusive. The dedicated worker could read an OffscreenCanvas; audio and
Local Font APIs were unavailable there and WebGPU was inconclusive. These are
API observations, not local-font permission decisions. The main-frame WebGL
test observed baseline availability and restricted unavailability; the
controlled remote-font request counts were one and zero respectively. The
fixture used local scheme handlers, not public websites.

Do not collect or persist device fingerprints, ICE addresses, raw cookies, or
credentials. Controlled test fixtures should record only bounded status and
packet-presence observations. Canvas, audio, local font metrics, screen, device
and CPU characteristics, browser version, language and timezone remain possible
signals. WebGL and remote-font restrictions can affect graphics, localization,
accessibility, and website layout; WebRTC controls may interrupt calls.

## Verification and decisions

- Hosted Windows x64 final-head workflow and security-regression results are
  **pending** until their actual run results are inspected.
- Canvas and audio protection: no supported targeted mechanism established.
- WebGPU protection: no independently verified per-profile CEF mechanism.
- Local Font Access: browser content-setting capability established from source;
  actual API enforcement and remaining stored grants are inconclusive.
- WebRTC IP privacy: **INCOMPLETE** beyond the bounded local observations;
  complete network leak coverage remains **INCOMPLETE**.
- Cross-origin iframe, worker, and popup consistency: **INCOMPLETE** until
  each context has its own evidence.
- Full fingerprint resistance: **NOT_ESTABLISHED**. Production readiness:
  **NOT_APPROVED** pending review and applicable tests.

Do not promote a successful build, a synthetic test, or preference readback to
an end-to-end protection claim. The smallest next step is to measure the
selected WebRTC setting using disposable profiles and controlled packet
endpoints with explicit before/after and first-navigation coverage.
