# Aegis Browser

A Windows x64 browser for ordinary browsing and local privacy diagnostics. Includes tabs, back/forward, reload/stop, an address bar, persistent profiles and Chromium DevTools. No identity spoofing, fingerprint modifications or fraud-detection bypasses.

## Build on Windows

Install Windows 10/11 x64, the .NET 8 SDK and the Microsoft Visual C++ 2015–2022 x64 Redistributable. Internet access is required for NuGet restore. Visual Studio 2022 with the .NET desktop workload is optional.

Run from the repository root:

```powershell
dotnet restore AegisBrowser/AegisBrowser.csproj -r win-x64 -p:SelfContained=true
dotnet build AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 -p:SelfContained=true --no-restore
dotnet restore AegisBrowser.Tests/AegisBrowser.Tests.csproj -r win-x64
dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64 -p:PlatformTarget=x64 --no-restore
dotnet run --project AegisBrowser -c Release -- --profile=Personal
dotnet publish AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
.\artifacts\win-x64\AegisBrowser.exe --profile=Personal
```

Distribute the entire publish directory, including the subprocess, native DLLs, locale and resource files. Trimming and single-file publishing are disabled. CefSharp.WinForms.NETCore is pinned to 151.3.240; the original Framework package and wildcard reference have been replaced. Update the pinned engine regularly after reviewing upstream security fixes and compatibility changes. The Windows GitHub Actions workflow is configured to use `windows-latest` and `actions/setup-dotnet@v4` with SDK `8.0.x`. It explicitly restores and builds for `win-x64`, runs the console tests as Windows x64, publishes the same self-contained Release build without rebuilding, checks the native payload and bundled .NET runtime, and uploads the entire publish directory as `AegisBrowser-win-x64`. Later steps run only after earlier steps succeed. The first Windows CI run restored successfully but failed compilation; local API fixes await upload and a new CI run. The workflow does not perform GUI acceptance tests. The root `global.json` selects SDK 8.0.4xx (latest installed patch), so newer preinstalled major SDKs do not take precedence. See [actual verification status](../BUILD-STATUS.md).

## Profiles

`--profile=Personal` and `--profile=Research` can run simultaneously as separate processes. Names are case insensitive, with 1–40 ASCII letters, digits, hyphens or underscores. Invalid names and unknown arguments fail explicitly. No application arguments are forwarded to Chromium.

Data lives at `%LOCALAPPDATA%\AegisBrowser\Profiles\profile-<name>`, with a separate CEF root and cache directory for each profile. Tabs share the process profile. An exclusive OS file handle prevents two processes opening the same profile; a stale lock filename after a crash is harmless. Cookies with persistent expiry and origin storage survive restarts; session cookies retain Chromium's normal session lifetime. Close the profile before removing its directory to erase local data. Existing starter directories are not migrated automatically.

Profiles isolate stored browsing state, not OS users, IP addresses or hardware characteristics. Local files and Chromium logs are not an encrypted vault.

## Controls and policy

- Ctrl+L focuses the address bar; bare hosts default to HTTPS. Explicit HTTP is supported. Embedded URL credentials and unsupported top-level schemes are rejected.
- Ctrl+T / Ctrl+W open and close tabs; Ctrl+Tab cycles tabs. The last closed tab is replaced by diagnostics.
- Alt+Left / Alt+Right navigate; F5 reloads; Escape stops. F12 or the DevTools button opens Chromium tools.
- User-initiated HTTP(S) popup targets open in independent tabs. Automatic popups are blocked. Opener-dependent login flows and script-created blank popup windows may not work.
- All CEF permission prompts and media requests are denied immediately and reported in the status bar. There are no remembered grants or allow controls in this version. Camera, microphone, screen capture and location dependent sites will not work.
- Downloads are disabled. Invalid certificates are rejected without an override. No remote debugging port, privileged JavaScript binding, user-agent override, certificate bypass or web-security disabling switch is configured.

The application preserves CefSharp/CEF defaults for Chromium security mechanisms. CefSharp is an embedded engine and does not provide all security services or sandbox guarantees of Google Chrome; this app does not claim Chrome-equivalent hardening or Safe Browsing. No additional sandbox-disabling flags are added. It has no automatic engine updater.

## Local diagnostics

The startup page, Diagnostics button and `aegis:diagnostics` address show an embedded resource served by an HTTPS scheme handler for `aegis-diagnostics.invalid`. No local HTTP server or internet page is involved. Reload works normally. Its CSP denies network connections, frames, images, forms and external scripts. The report uses textContent and observes native browser values without changing them: user agent, languages, platform, hardware concurrency, memory when exposed, screen, viewport, timezone, touch and WebGL capabilities. Unsupported APIs display an unavailable marker. It performs no uploads, persistent report storage or permission requests. Other browsing tabs can still make network requests.

## Verification

The dependency-free .NET console test runner throws on failure and exits nonzero. It checks address validation, profile argument rejection, case normalization, Windows reserved-name handling, separate storage, exclusive leases and persistence/reopening. Run it with `dotnet run --project AegisBrowser.Tests -c Release`; it is not a `dotnet test` project.

On a Windows machine, also verify:

1. Launch two different profiles; set persistent cookies/localStorage in DevTools on the same test origin. Confirm data differs and survives a restart. A second process for the same profile must fail clearly.
2. Browse two pages and use back/forward, reload/stop, close tabs during loading and close the window with multiple tabs. Exercise shortcuts while web content has focus.
3. Request geolocation and camera from a secure test page: both must reject. Test a notification prompt, automatic popup, user-clicked web popup and a download.
4. Visit a controlled invalid-certificate endpoint; navigation must fail. Confirm ordinary valid HTTPS still loads.
5. Disconnect networking and reload diagnostics; check the DevTools network panel for no outbound diagnostics requests.
6. Run the published executable on a clean Windows x64 machine with the VC++ runtime, and verify subprocess startup and DevTools.

Implementation environment: macOS ARM64. `dotnet` is absent, including common installation paths. Restore, build and tests cannot run here. Shell HTTPS fetching also failed DNS resolution. The first remote Windows CI run also failed compilation; corrections are prepared locally, but the GitHub integration rejected writes with HTTP 403. No release artifact has been produced. See BUILD-STATUS.md for the run link, compiler diagnostics and pending fixes.

Upstream references: [CefSharp .NET example](https://github.com/cefsharp/CefSharp.MinimalExample), [pinned NuGet package](https://www.nuget.org/packages/CefSharp.WinForms.NETCore/151.3.240), [CefSharp project](https://github.com/cefsharp/CefSharp).

## Privacy filtering

The **Privacy** toolbar button opens Standard/Strict/Custom settings, editable domain and ABP-style network rules, per-site exceptions, a temporary pause, and a local request inspector. Settings persist separately in each profile's `privacy.json`. The inspector records only origins in memory and displays matching rules and allowed/blocked decision counts.

The bundled tracking list is intentionally small. Import standard UTF-8 privacy lists to extend it; unsupported syntax is reported. This is a documented network-syntax subset, not a full EasyList/uBlock implementation. Strict mode can break third-party dependencies and embedded authentication; explicit exceptions restore compatibility without changing Chromium security settings.

See [privacy filtering documentation](../docs/privacy.md) for syntax, precedence, persistence, limitations and Windows acceptance cases. Real Chromium filtering tests are built and run by the Windows workflow before publishing. Consult [BUILD-STATUS.md](../BUILD-STATUS.md) for verified results; a compiled binary alone does not establish GUI acceptance.
