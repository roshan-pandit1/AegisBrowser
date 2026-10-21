# Aegis Browser v0.1.0 — Windows portable release candidate

This is a release candidate, not a stable-release certification. Check BUILD-STATUS.md and the linked CI run before distribution. Full manual GUI and real-site authentication acceptance remains required.

## Install and start

1. Use Windows 10/11 x64. The archive includes the .NET runtime; the .NET SDK is not needed to run it. CefSharp may require the Microsoft Visual C++ x64 Redistributable: https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist . Install that prerequisite using Microsoft's installer if native runtime DLLs are missing.
2. Download the portable artifact from the PR's successful Windows Actions run. The Actions artifact wrapper contains `AegisBrowser-v0.1.0-rc1-win-x64-portable.zip` and its `.sha256` file.
3. Verify the inner ZIP using PowerShell `Get-FileHash .\AegisBrowser-v0.1.0-rc1-win-x64-portable.zip -Algorithm SHA256` and compare it with the accompanying checksum and CI log.
4. Extract the ZIP completely to a writable folder. Keep every file and subfolder together. Do not run the executable inside the ZIP or copy only the EXE.
5. Open `AegisBrowser\AegisBrowser.exe`. No administrator privileges are required for the browser. This build is not code-signed; follow your organization's application trust policy.

For a separate persistent profile:

```powershell
.\AegisBrowser\AegisBrowser.exe --profile=Research
```

The first tab displays local diagnostics. Use the address bar to navigate; Privacy opens filtering settings and the request inspector. Standard enables the starter/imported list, Strict blocks third-party subresources, and Custom applies user rules with optional tracking-list support. Apply edits and reload affected pages. Explicit site/resource exceptions or temporary pause can restore third-party content. See PRIVACY.md for syntax and limitations.

## Data, updates and removal

“Portable” describes the application folder, not its data location. Profiles and privacy settings are stored under `%LOCALAPPDATA%\AegisBrowser\Profiles\profile-<name>`. Pause and inspector history reset on exit. Each profile can be opened by only one process at a time.

Close every browser window before replacing the application folder with a newly extracted version. Keep a backup of profile data before upgrades. Do not mix files from different releases. There is no automatic Chromium updater; use reviewed builds with current engine security fixes.

To remove the application, close it and delete its extracted folder. Removing profile directories separately permanently erases cookies, storage and settings; keep them if you intend to reinstall. Copying the executable folder alone does not back up profiles.

## Troubleshooting and release limits

- Keep native DLLs, CEF subprocess, locales and resource files together. For startup failures inspect the selected profile's `chromium.log` and the startup dialog.
- A malformed `privacy.json` is preserved and reported. Repair or rename it while the browser is closed, then restart; defaults become active if it is absent.
- Strict mode can break embedded login, payment, media and CDN dependencies. Use explicit exceptions; the browser does not bypass security checks or invalid certificates.
- Denied permissions and missing OS hardware access must not be mistaken for a browser crash. Screen sharing and unsupported permissions remain denied.
- Do not disable certificate validation, web security or the sandbox to work around failures. Report the source revision from VERSION.txt, Windows version, exact reproduction steps and relevant logs with secrets removed.
