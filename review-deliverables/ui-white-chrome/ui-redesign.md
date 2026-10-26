# AegisBrowser native UI redesign

Branch: feature/ui-white-chrome, based on Security Lab commit 75fbf6a59b4565fc38f16bf63a73938cb68edb4e. This is a real WinForms implementation, not an HTML mockup. CefSharp remains 154.0.340 and the target is .NET 8 Windows x64.

## Screens and behavior

White surfaces, pale-blue selected tabs, navy text, blue actions, a restrained red/blue vector shield and Segoe UI typography are shared by the browser, homepage, saved-site editor, privacy/search settings and Security Lab. The shield is drawn locally with GDI+; no fonts, favicons or images are downloaded. Standard native borders and controls are retained where custom decoration would impair reliability/accessibility. The homepage search has a rounded, focus-highlighted border. PerMonitorV2 DPI mode is explicit; actual visual scaling still needs Windows verification.

- The tab strip has selected/focus states, ellipsized titles, full-title tooltips and per-tab close hit targets. Ctrl+W remains the keyboard close action. The toolbar also retains new/close-tab actions.
- Back, Forward and Reload/Stop retain Chromium navigation. The wide address field uses the existing AddressInputResolver and selected engine. The star opens a real bookmark editor. The privacy indicator opens the existing settings/inspector. The compact menu exposes Search, Privacy, Bookmarks, Shortcuts, Security Lab, Diagnostics, DevTools and pause/resume.
- The sidebar has Home/New Tab, Bookmarks and Settings. There is no fake History button: history is not implemented. Profile name is shown read-only; process/profile switching remains the existing --profile=NAME launch mechanism.
- The native homepage is hosted over an ordinary about:blank browser within the same tab. Search or shortcut submission uses the existing resolver and current-tab navigation; no privileged JavaScript binding or scheme bypass is added. Back to that tab's about:blank page restores the homepage. The last-tab replacement is now Home; diagnostics are still available from the menu and aegis:diagnostics.
- Homepage shortcut tiles are empty until configured. Add via the homepage, edit/remove via the Shortcuts menu. Bookmark the current page with the star; edit/remove/open via Bookmarks. Editors are modal to prevent simultaneous stale editors overwriting each other's collection.
- sites.json persists explicit shortcuts/bookmarks per profile. Only HTTP(S) URLs without embedded credentials are accepted. Collections are bounded, writes use temporary-file replacement, and invalid original files are preserved until repaired. No automatic browsing-history recording is introduced. Explicit saved URLs may contain query parameters: users should avoid saving sensitive links. They are local settings, not an encrypted vault.
- Privacy labels use the actual PrivacySession mode/Paused state; tooltip counts come from Snapshot. The settings pause checkbox follows changes made through the menu. Labels do not claim that all traffic is safe or that a Custom configuration necessarily has blocking rules.

Native timers, menus/tooltips, home controls and tab contexts follow disposal. Existing CEF host-close acknowledgement and popup/renderer shutdown logic is retained. Security Lab server-side authorization, mock-only scope and JSON sanitization are unchanged.

## Product-page rendering investigation

No affected public URL, reproduction steps or active list/mode have been supplied. The consumer-site failure therefore has **not been reproduced or fixed**. SafeRequests/FilteringResourceHandler were inspected: they cancel matching network requests but do not supply response bodies, response filters or rewritten MIME types. Main-frame navigation is exempt from content blocking. This does not prove that blocked resources cannot break a real site's client-side routing.

A deterministic Windows regression was added: click a product link, follow a redirect, and require an actual styled H1 DOM element and text/html document while filtering is enabled, then while paused. The product URL deliberately matches a blocking rule; an unrelated third-party image is also present. This test checks the main-document/rendering boundary and does not replace reproducing the reported site. No blanket allowlists or filtering/TLS/security-policy changes were made.

## Verification and build commands

The repository has project-based builds rather than a solution. Use the existing Windows workflow for full execution and packaging:

```powershell
dotnet restore AegisBrowser/AegisBrowser.csproj -r win-x64 -p:SelfContained=true
dotnet build AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 -p:SelfContained=true --no-restore
dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64
dotnet build AegisBrowser.BrowserTests -c Release -r win-x64 -p:SelfContained=true
dotnet build AegisBrowser.FilteringTests -c Release -r win-x64 -p:SelfContained=true
# Execute the workflow's native write/read, filtering and settings-restart phases.
dotnet publish AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 --self-contained true --no-build --no-restore -o artifacts/win-x64
./tests/acceptance/windows-smoke.ps1 -PublishDirectory artifacts/win-x64
./scripts/package-portable.ps1
```

Actual local results are in BUILD-STATUS.md. Cross-compilation and file presence do not establish successful Windows execution or visual acceptance. Verify the new branch's full Windows CI before accepting it. Existing assertions remain; navigation tests now explicitly open Diagnostics when testing its content, and settings tests invoke the same action delegates as the new menus.

Remaining Windows checks: 100/150/200% scaling, minimum-window layout, keyboard/tab close targets, modal saved-site edits and cancellation, live privacy pause/configuration states, all native regression suites, startup/shutdown, and the reported consumer URL once supplied. No UI screenshot or manual GUI pass is claimed.
