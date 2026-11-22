# Third-party notices

The CEF and .NET license texts below are copied unmodified from the identified
distributions. The SQLitePCLRaw entry is the canonical Apache 2.0 license text,
as indicated by its NuGet license expression; it is not a package-extracted file.

| File | Package | Version |
| --- | --- | --- |
| `CefSharp-LICENSE.txt` | `CefSharp.Common.NETCore` and `CefSharp.WinForms.NETCore` | `154.0.340` |
| `CEF-LICENSE.txt` | `chromiumembeddedframework.runtime` and `chromiumembeddedframework.runtime.win-x64` | `154.0.34` |
| `CEF-CREDITS.html` | CEF Windows x64 minimal binary distribution | CEF `154.0.34`, Chromium `154.0.8037.98` |
| `DOTNET-RUNTIME-LICENSE.TXT` and `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT` | `Microsoft.NETCore.App.Runtime.win-x64` | `10.0.15` |
| `DOTNET-DESKTOP-LICENSE.TXT` | `Microsoft.WindowsDesktop.App.Runtime.win-x64` | `10.0.15` |
| `SQLITEPCLRAW-APACHE-2.0.txt` | SQLitePCLRaw 2.1.13 core, provider, bundle and native packages | `2.1.13` |

The CEF package identifies Chromium `154.0.8037.98`. Its upstream redistribution
documentation points to the CEF `CREDITS.html` / `about:credits` for Chromium
and additional third-party licensing information:

- <https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/LICENSE.txt>
- <https://github.com/cefsharp/CefSharp/blob/v154.0.340/LICENSE>
- <https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/docs/general_usage.md>

`CEF-CREDITS.html` is the byte-exact generated credits file from
`cef_binary_154.0.34+g14c5a08+chromium-154.0.8037.98_windows64_minimal.tar.bz2`.
The archive SHA-256 is `c81c34d048d87276f4a18f0b000206710a1709bb67dd658948b87100e1a538da`;
the credits SHA-256 is `eb5a0b6300c58bb8316a66b3e935d55b1456f8f1cf225c979b03d42afa878c4b`.
Packaging checks the credits and `libcef.dll` against their expected hashes.
The upstream HTML links stylesheets at `chrome://` addresses; standalone display
may omit styling. Its inclusion does not establish that notices for every other
shipped dependency are complete or that redistribution has been approved.
The complete inventory and distribution requirements remain open for review.

The .NET files were extracted without modification from the exact 10.0.15
runtime and Windows Desktop NuGet packages downloaded over the official NuGet
HTTPS feed. The package SHA-256 values were
`7423f58917342b9e4bf276fb164d5726d2c90fe2fe989c552b9e39056256a035`
and `a036c174bac57a716e21dedccf96459804e4d8d8c31a08e3a8de649bf7684497`,
respectively. The runtime and desktop nuspecs both identify dotnet/dotnet
commit `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7`. Their coreclr, CoreLib
and Windows Forms binaries match the previously validated CI payload hashes;
the package signatures have not been independently verified. Packaging checks
the actual binary and notice hashes before archiving and after extraction.
This binding covers these files only and is not a legal or security approval.

SQLitePCLRaw 2.1.13 NuGet metadata declares the Apache-2.0 license and
`Copyright 2014-2024 SourceGear, LLC`. `SQLITEPCLRAW-APACHE-2.0.txt` is the
[canonical Apache 2.0 text](https://www.apache.org/licenses/LICENSE-2.0.txt),
SHA-256 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`.
This attribution is based on package metadata; it does not verify the shipped
SQLitePCLRaw binary's exact identity. The package does not identify a source
revision for a version-matched upstream NOTICE; that NOTICE and all remaining
redistribution obligations still require review.
