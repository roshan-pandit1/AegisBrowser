# Third-party notices

The accompanying license texts are copied unmodified from the exact packages
used by this application:

| File | Package | Version |
| --- | --- | --- |
| `CefSharp-LICENSE.txt` | `CefSharp.Common.NETCore` and `CefSharp.WinForms.NETCore` | `154.0.340` |
| `CEF-LICENSE.txt` | `chromiumembeddedframework.runtime` and `chromiumembeddedframework.runtime.win-x64` | `154.0.34` |
| `CEF-CREDITS.html` | CEF Windows x64 minimal binary distribution | CEF `154.0.34`, Chromium `154.0.8037.98` |

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
