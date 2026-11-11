# CSS viewport letterboxing verification

This document records the scope and evidence for the viewport letterboxing research. The starting revision is `041cc7fed719e5df7da1799a00d6b2134fe65480`, from `feature/tor-viewport-letterboxing` (PR #63). The pinned production stack is .NET 8, WinForms, CefSharp 154.0.340 and Chromium 154.0.8037.98. This research does not establish Tor Browser equivalence, anonymity or cross-site unlinkability.

## Parent implementation and units

At the starting revision, `ViewportLetterboxing.Calculate` rounds the available WinForms container width and height down to 200-by-100 **control-unit** steps, centers the result, and preserves an available dimension below its step. `MainForm` applies the calculated bounds to each letterboxed browser on client-size and visibility changes. It does not recreate browsers during ordinary resize. Letterboxing is profile-local, off by default, and a setting change takes effect after restarting the profile. Native popup windows are outside this layout. Geometry and persistence unit tests establish the behavior of the calculation and setting; they are not measurements of a Chromium viewport.

The following values must not be treated as interchangeable:

| Quantity | Meaning and limitation |
| --- | --- |
| WinForms container and browser bounds | Coordinates used for the browser control layout; their relation to physical pixels depends on Windows DPI awareness and scaling. |
| Physical pixels | Display pixels or backing pixels; Windows may virtualize coordinates or scale a process depending on its DPI-awareness context. |
| Device-independent pixels | Windows logical units used by DPI-aware layout; they are not, by themselves, proof of the CSS viewport dimensions. |
| `innerWidth`, `innerHeight` | Page-observed layout viewport dimensions in CSS pixels. |
| `documentElement.clientWidth`, `clientHeight` | Root element client dimensions in CSS pixels; scrollbar and document behavior can make them differ from `innerWidth` or `innerHeight`. |
| `visualViewport` dimensions | Visible viewport in CSS pixels, where supported; distinct zoom and scrolling behavior can make it differ from the layout viewport. |
| `devicePixelRatio` | The page-observed ratio between physical and CSS pixels. Page zoom can affect it; it is not a measurement of OS display scaling alone. |
| `outerWidth`, `outerHeight`, `screen` dimensions | Browser/window and screen observations, not a guarantee that content is bucketed or that screen information is concealed. |

Neither a 200-by-100 control-unit calculation nor a chosen `devicePixelRatio` proves 200-by-100 CSS-pixel bucketing. Windows DPI virtualization, Chromium's rendering scale, page zoom, rounding and lifecycle timing must be measured together. The starting project does not explicitly set `ApplicationHighDpiMode`; WinForms documents `SystemAware` as its default. A system-aware process can be scaled by Windows when moved to a monitor with different scaling. This is not evidence of per-monitor-DPI behavior. See Microsoft's [WinForms high-DPI documentation](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms) and [DPI and device-independent pixels](https://learn.microsoft.com/en-us/windows/win32/learnwin32/dpi-and-device-independent-pixels), and MDN's [`devicePixelRatio` explanation](https://developer.mozilla.org/en-US/docs/Web/API/Window/devicePixelRatio).

## Measurement contract

Use only a controlled local page and a disposable profile. Record the browser control bounds, container bounds and effective DPI alongside `innerWidth`, `innerHeight`, `document.documentElement.clientWidth`, `document.documentElement.clientHeight`, `devicePixelRatio`, `outerWidth`, `outerHeight`, `screen.width`, `screen.height`, `screen.availWidth`, `screen.availHeight`, and `visualViewport.width` and `.height` when available. Record actual configured zoom and wait for layout to settle before reading. Keep the observations bounded and local; do not compute a persistent identifier or collect values from unrelated websites. Page APIs are read normally, with no spoofing or overridden device characteristics.

The result should distinguish a successful DOM read from a verified bucket. A bucket assertion must identify the enabled setting, actual available dimensions, zoom, effective DPI and measured CSS dimensions. Record any rounding tolerance and cases where the content area is smaller than a full bucket. A failed or unavailable measurement must remain failed or inconclusive, respectively; a WinForms unit test must not stand in for a native-browser measurement.

Exercise disabled and enabled modes, initial load, navigation, new tabs, switching, resize, maximize/restore, minimize/restore, zoom and profile restart where the test environment supports each operation. A controlled responsive page should check scrolling, forms, fixed elements, dialogs, media and embedded frames separately. A passing native test also needs observed normal browser closure and CEF shutdown; popup compatibility remains a separate claim.

### Local observations

On the Windows ARM64 development host running x64 CEF under emulation, the form reported `DeviceDpi=96` and the fixture reported `devicePixelRatio=1` at zoom level 0. This is an observed effective DPI, not independent verification of the system display-scaling configuration. The controlled in-process scheme fixture does not access unrelated sites.

| Scenario | Actual observation |
| --- | --- |
| Disabled versus enabled, initial | Available/control dimensions were 1226×721 and CSS was 1226×721 disabled; enabled control and CSS were 1200×700. |
| Resize and tabs | At zoom 0, enabled 800×500 and 1000×600 controls produced matching CSS viewports; a new tab and tab switch also reported 1000×600. |
| Maximize and restore | The maximized 1800×800 control reported 1800×800 CSS; restore returned 1000×600. |
| Zoom and reset | At unchanged 1000×600 control bounds, level 1 reported 833×500 CSS and DPR 1.2; resetting to level 0 restored 1000×600. CSS bucketing across zoom therefore fails for this case. |
| Profile restart | A fresh read process loaded the enabled setting, measured the viewport and completed CEF shutdown. |
| Responsive content | A controlled scrollable page exercised form editing, a dialog, a fixed element, a media control, an embedded frame and responsive CSS checks. It is not a compatibility survey of real websites. |
| Native shutdown | Both local write and read processes reported normal CEF shutdown and exit code 0. |
| Windows x64 CI | At commit `3f34c85a0d11a0d33f9e5c7118ccd3f9ad8bd0b2`, the [push](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42967212181) and [PR](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42967216673) workflows passed; later final-head results belong to the research PR. A passing workflow is not a multi-DPI qualification. |

For a controlled privacy comparison, collect the same finite set of window sizes with the same display, OS scaling and zoom in each mode and report the distinct observed CSS width/height pairs. A smaller count in that sample would describe only that sample; it would not establish user anonymity, unlinkability or a population-wide benefit. Also record whether enabled mode introduces distinctive dimensions. Do not extrapolate across untested displays, zoom levels or web content.

With the later scrollable local fixture at zoom 0, a 1000×600 control reported `innerWidth=1000` but `documentElement.clientWidth=985` and `visualViewport.width=985`; at zoom 1 it reported 833, 821 and approximately 820.83 respectively. The test's `cssBucketed` field describes `innerWidth`/`innerHeight` only. Scrollbar-sensitive measurements must not be represented as bucketed merely because that field is true. No population-wide privacy benefit has been measured.

For four selected window sizes on that same local display at zoom 0, the disabled `innerWidth`/`innerHeight` pairs were 1226×721, 999×584, 1065×622 and 1075×632 (four distinct pairs). Enabled pairs were 1200×700, 800×500, 1000×600 and 1000×600 (three distinct pairs). This deliberately small sample demonstrates a collision for two selected sizes; it does not establish anonymity or a population-wide reduction in identifiability.

The hosted PR run reported effective DPI 96 and DPR 1 at zoom 0. Its smaller screen constrained the requested window sizes: the four disabled pairs were 1020×659, 999×584, 1020×622 and 1020×632; the enabled pairs were 1000×600, 800×500, 1000×600 and 1000×600. Both write (62 checks) and read (44 checks) processes reported CEF shutdown. This run also observed the 833×500 zoomed `innerWidth`/`innerHeight` and the 985-pixel scrollbar-sensitive client width at zoom 0. These are controlled runner observations, not independent verification of its Windows scaling setting.

## DPI qualification and bounded manual procedure

No independently verified 100%, 125%, 150%, 200% or mixed-monitor display-scaling result is established by these tests. A calculation that substitutes a scale value is a useful unit test but is not a real Windows display transition. Unless separately recorded from a real run, each of those scenarios remains **inconclusive**.

When a disposable, interactive Windows test machine with the required displays is available, the following is a reproducible acceptance procedure, not a record of completed testing:

1. Record the test build, Windows version, display layout, process DPI-awareness mode, starting scaling, effective DPI and zoom. Use a disposable profile and only the controlled local fixture.
2. In Windows display settings on that disposable machine, test actual 100%, 125%, 150% and 200% scaling where supported. At each setting, restart as required by the OS/application, verify the applied value independently, and record enabled/disabled page and control measurements at several fixed container sizes and zoom levels.
3. If two differently scaled monitors are available, move the window between them and record the actual monitor, effective DPI, browser bounds and CSS measurements before and after the move. Distinguish OS bitmap scaling from process layout changes.
4. Exercise resize, maximize/restore, minimize/restore, tab creation and normal shutdown. Record unsupported settings, missed events and failures rather than substituting synthetic transitions. Restore the disposable environment's original settings.

No production profile, network policy, TLS setting or browser sandbox setting is needed for this procedure. Hosted CI results must identify its actual scaling and must not be presented as coverage of configurations that runner did not exercise.

## Fingerprint privacy and capability comparison

[Tor Browser's public documentation](https://support.torproject.org/tor-browser/features/fingerprinting-protections/) describes letterboxing as one part of a broader fingerprinting-resistance design. [GoLogin's public Orbita description](https://support.gologin.com/en/articles/14853789-orbita-browser) describes a customized browser architecture. These are contextual references, not evidence of equivalent AegisBrowser behavior.

| Technique | Classification | Scope |
| --- | --- | --- |
| Optional control-surface letterboxing | `ALREADY_IMPLEMENTED` | Parent implementation buckets control units; CSS-pixel and DPI behavior require the measurements above. |
| Profile-local privacy settings and resource filtering | `ALREADY_IMPLEMENTED` | Applicable configured filtering rules are not complete fingerprint or network-path coverage. |
| Third-party cookie preference | `ALREADY_IMPLEMENTED` | Opt-in Chromium setting; behavior and compatibility remain bounded by the existing controlled tests. It does not suppress all storage or fingerprinting. |
| Browser-native font enumeration restrictions | `REQUIRES_CHROMIUM_ENGINE_WORK` | No supported per-profile restriction was established in the pinned binding audit; this classification is a research assessment, not proof that no upstream API can ever exist. |
| Browser-native Canvas fingerprinting permission model | `REQUIRES_CHROMIUM_ENGINE_WORK` | No verified pinned-CefSharp equivalent was established; JavaScript overrides are not a substitute. |
| Complete WebRTC/system-wide network containment | `UNSUPPORTED` | Existing network filtering is not a system-wide containment claim; proxy-required startup remains fail closed. |
| Synthetic hardware, Canvas, GPU, audio or navigator identities | `NOT_RECOMMENDED` | Out of scope; do not impersonate devices or implement detection evasion. |

Other fingerprint surfaces, screen information, fonts, Canvas, GPU, audio, network address and site behavior can remain observable. Letterboxing can have compatibility costs and can itself be recognizable. The next focused step is to gather actual CSS viewport and real multi-DPI/zoom observations before proposing a broader browser-native reduction in exposed information.

## Security and remaining checks

This research must preserve the existing Chromium sandbox, TLS validation, permission policy, third-party cookie policy, popup ownership and `ProxyRepository.EnsureRuntimeReady`. Do not add remote debugging ports, inject spoofing scripts, alter user profiles, or change firewall, WFP, DNS or routing. Small and temporarily zero-sized client areas, rapid resize, tab closure, failed setting writes, popup lifecycle and shutdown need independently reported outcomes; their presence in a test plan is not evidence they passed. CI links and actual pass/fail/skip results belong with the final-head evidence above.
