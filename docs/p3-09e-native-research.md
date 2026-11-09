# P3-09E: isolated CefSharp native baseline research

This is an experimental build and source audit, not a replacement browser runtime. It is stacked on `fix/browser-late-native-close` (draft PR #50). The existing popup owner-release contract remains **UNSATISFIED**. No source-level match, successful compile, or passing synthetic test demonstrates safe native release.

## Pinned inputs and trust

The browser references CefSharp.WinForms.NETCore 154.0.340. Its common package identifies [CefSharp source revision `ec1027aa21e1af1217681a349104c15d29bbc21a`](https://github.com/cefsharp/CefSharp/tree/ec1027aa21e1af1217681a349104c15d29bbc21a) and runtime 154.0.34. The installed x64 `libcef.dll` reports `154.0.34+g14c5a08+chromium-154.0.8037.98`, corresponding to [CEF revision `14c5a089e8452874cb1a556dc3fde26ed2d87723`](https://github.com/chromiumembedded/cef/commit/14c5a089e8452874cb1a556dc3fde26ed2d87723). Package metadata and PE version information identify intended versions; they do not establish binary provenance or ABI equivalence.

The pinned baseline requires `cef.sdk` 154.0.34 and its matching headers, import library and wrapper, .NET 6 build dependencies, MSVC C++/CLI, and a compatible Windows SDK. The [official NuGet package](https://www.nuget.org/packages/cef.sdk/154.0.34) was compared with official package metadata. Its recorded SHA-512 is `92616C6776D5A4E445CCD8C4147C4F60B05BDD7802D8495CED42C700CD1D4C3A18C8B13CF9A5B2C48D92A2B753626B03DFEEB822C53E1550A92488156CE6266B`. The pin detects later byte differences; metadata fetched from the same source is not an independent publisher identity check. .NET 6 is out of support and is confined to the isolated build environment.

## Baseline workflow

The separate `.github/workflows/p309e-native-baseline.yml` runs only on the research branch or by dispatch. It uses a bounded Windows 2022 job, explicitly installs SDKs 6.0.428 and 8.0.427 into the ephemeral runner, checks for VS 2022 C++/CLI, fetches the exact CefSharp revision into a fresh runner temporary directory, restores the pinned upstream package configurations from NuGet.org, verifies the CEF SDK archive hash and required inputs, and attempts the unmodified x64 Release solution build. It checks process exit codes and reports the runtime hash if compilation and linking succeed. It does not install a build on the development workstation, upload generated runtime binaries, or change production packaging. The workflow is a reproducible *procedure*, not a claim of byte-identical output, native loading, CEF startup or ABI compatibility.

The entry point `tests/research/p309e-native-baseline.ps1` intentionally requires a runner-owned absolute `RUNNER_TEMP`, VS 2022 and the exact SDK. It creates a new GUID-named child directory and leaves it for runner disposal; it does not delete existing directories. Invoke it in a comparable disposable environment only. .NET first-run certificate generation is disabled in the research workflow. Always inspect the actual workflow run and exit status before treating the baseline as built.

## Offline source audit

The audit reads only bounded Git blobs from explicitly supplied repository roots at the two exact revisions. It disables lazy object fetching, refuses mismatched revisions and reports incomplete if required objects are unavailable. It does not execute upstream content. Given locally available complete pinned checkouts, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\research\p309e\test-source-audit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\research\p309e\inspect-pinned-popup-source.ps1 `
  -CefSharpSource 'C:\path\to\pinned\CefSharp' -CefSource 'C:\path\to\pinned\cef'
```

The first command uses synthetic text fixtures to test rejection paths; it is not native evidence. On the development host it passed 18 checks. The second matched all 10 source landmarks against the two pinned checkouts, but checks source text rather than provenance or execution. The temporary checkouts were created under another Windows identity, so that run required process-scoped Git `safe.directory` entries; no global Git configuration was changed. A missing object must be obtained separately from a trusted source; the audit itself does not fetch it. File-system path checks are defense in depth, not protection from a hostile administrator or concurrent replacement.

## Bridge decision and remaining proof obligations

An approved popup's native ID is scoped to its opener. An abort carries that ID, but successful `OnAfterCreated` carries a browser identity instead. An ID cannot safely be inferred from FIFO order, URL or timing. The existing binding does not expose abort events; the native abort route first re-finds the opener, so callback delivery cannot be guaranteed merely by retaining the old managed handler. No timeout or failed lookup is an abort.

The source contains a further compatibility constraint: [`CefFrameWrapper::EvaluateScriptAsync`](https://github.com/cefsharp/CefSharp/blob/ec1027aa21e1af1217681a349104c15d29bbc21a/CefSharp.Core.Runtime/Internals/CefFrameWrapper.cpp#L227-L245) casts the browser client to `ClientAdapter`. A generic forwarding client could invalidate that assumption even if it forwarded every handler. Any candidate must address this cast, nested and DevTools popups, handler identity, ownership and OAuth behavior. An opener-lookup failure occurs inside `libcef`; an adapter built against the unmodified SDK cannot directly observe that internal failure. Source-level instrumentation of the lookup requires a separately reviewed CEF-core build or a validated equivalent.

The model in the parent branch is only conditional: if every relevant terminal outcome reaches a correctly scoped lifetime target, aggregate accounting can withhold release until all outcomes are terminal. That delivery and the owner-host safety predicate remain unproved. Missing events, unknown IDs, target loss, timeout and uncertain task dispatch must remain unresolved; created browsers must independently complete closure. No native bridge is included, and no production migration, experimental binding readiness, OAuth compatibility or native lifecycle guarantee is claimed. Before any bridge is attempted, the unmodified pinned baseline must build and pass isolated load/start/close checks; afterward the bridge still needs native interleavings, ownership, ABI, packaging, licensing and security review.
