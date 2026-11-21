# .NET 10 migration compatibility preflight

Base: `2d99e9c32905f736f10dee73210388485c8ecbe1`. This branch changes the managed runtime only. It retains CefSharp `154.0.340` (CEF `154.0.34`, Chromium `154.0.8037.98`) and does not establish a renderer sandbox or production proxy containment. Release A and Release B remain **NO_GO**.

Microsoft lists .NET 8 as out of support and .NET 10 as LTS. SDK `10.0.404` includes runtime and Windows Desktop `10.0.15`; the SDK is pinned exactly. Sources: [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [Microsoft .NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [CefSharp 154 release](https://github.com/cefsharp/CefSharp/releases/tag/v154.0.340).

| Area | Existing / proposed | Preflight status and acceptance |
|---|---|---|
| CefSharp managed assemblies | 154.0.340; retain package | NuGet advertises computed .NET 10 compatibility; restore/build required. It does not verify native CEF. |
| CEF subprocess | Pinned native subprocess and managed bootstrap; retain | Unverified on .NET 10. Require a real CEF process, navigation and shutdown. |
| WinForms and browser lifetime | `net8.0-windows` to `net10.0-windows` | Behavioral compatibility unverified; execute tab/popup and shutdown fixtures. |
| Self-contained x64 publishing | Preserve `win-x64`, no trimming or single-file publish | Verify published runtime files, PE architecture and extracted executable smoke. |
| SQLite | Microsoft.Data.Sqlite `8.0.33`; retain initially | Restore compatibility and profile metadata/backup tests required. No schema changes intended. |
| DPAPI | ProtectedData `8.0.0`; retain initially | Windows credential-vault and isolation tests required; package compatibility alone is insufficient. |
| Profiles and site storage | Preserve formats, directories and CEF cache settings | Verify write/read and restart in disposable roots. |
| Viewport and privacy | Preserve assertions, settings and 154 engine | Run `viewport-write`, `viewport-read`, filtering and privacy tests. |
| CI/test paths | Output paths include framework name | Update all active .NET test binary paths, including disposable HTTPS trust cleanup; run x64 workflows. |
| Artifact identity | Current payload verifies round-trip and smoke | Verify bundled runtime and x64 PE identities on the published and extracted files. |

The local development host is Windows ARM64 without the SDK; native Windows x64 tests and final release acceptance must be performed by the Windows workflows. A green managed restore/build is not native CEF evidence. Source and runtime migration tests are recorded independently from final artifact/manual acceptance.

The first [focused candidate](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44935349262) restored but failed compilation with `WFO1000` on `MainForm.ViewportZoomTrace`. The diagnostic callback is not designer state, so the migration explicitly marks that property `DesignerSerializationVisibility.Hidden`; no analyzer or security check is suppressed. Subsequent gates must be run on the corrected commit.

At commit `4bfa3cc575e56c2ec6dc8077505f791913a7e9a1`, the [focused Windows x64 run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/44935772438) passed restore, compilation, core tests, CEF profile write/read, viewport write/read, native window shutdown and self-contained publish validation. The publish validation matched the bundled .NET and Windows Desktop `10.0.15` files to the installed runtime and checked x64 native binaries. These fixture results do not replace full Windows CI, extracted-package acceptance, or a renderer sandbox verification.

The SQLite and ProtectedData package versions are retained to avoid bundling an unrelated dependency change into the runtime migration. Their compatibility and support status require a separate dependency and security review; passing this migration's tests does not establish that those packages are maintained.
