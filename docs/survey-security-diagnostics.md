# Investigating survey security results

## Assessment — 2026-10-26

**Cause unresolved.** The user reports a Security result after a provider redirect and says disabling filtering does not resolve it. No authorized reproduction, response trace, provider decision explanation or affected-session logs were supplied or collected. This does not establish Verisoul involvement or which signal caused a rejection. A business-level rejection can arrive in a successfully loaded HTTP 200 document. HTTP success is not survey acceptance.

Current checkout: `release/windows-rc`, HEAD `9fd457276d3e741ade1041ea37655115b7beea4c`, with pre-existing edits. Application target is net8.0-windows / win-x64, CefSharp.WinForms.NETCore 154.0.340. This task does not reconcile or replace saved later PR work.

## Source trace and confirmed diagnostic gap

- `Program.cs`: owns Cef initialization and profile context; no new startup switches.
- `SafeRequests.OnBeforeBrowse`: main-frame URL policy and navigation-site snapshot. `GetResourceRequestHandler` creates the existing FilteringResourceHandler for each request. New optional diagnostics reuse this path.
- `FilteringResourceHandler.OnBeforeResourceLoad`: existing PrivacySession decision alone determines Cancel versus Continue. New callbacks observe resource completion and redirects without modifying response bytes, MIME type, redirect destination or handling flags.
- `RequestFilter.EvaluateCore`: top-level documents pass, supported list exceptions precede block rules, Strict adds third-party blocking; existing domain/site exceptions can include subdomains. None were added for any provider. Exact URL/type ABP exception behavior is tested without changing configuration or adding automatic exceptions.
- `MainForm.NewTab`: existing load errors and HTTP status UI lacked a correlated opt-in trace. New BrowserDiagnosticAttachment records completion/load/console events and unsubscribes on retirement/disposal.
- `SafeRequests.OnCertificateError`: still disposes the callback and returns false. Cookie storage, authentication, popup policy, response handling and profile isolation were not changed.

No browser-side filtering defect causing this particular rejection has been demonstrated. The confirmed gap repaired here is missing normal-browser diagnostic evidence, not the provider result itself.

## Using the local diagnostic facility (after Windows validation)

Open **Local trace** in the status strip and explicitly enable collection before an authorized navigation. It is off by default and shared by ordinary tabs in this window. The separate mock Lab retains its own diagnostics. Disabling collection clears memory; Clear also deletes entries. Closing the panel does not disable collection; its checkbox controls that state. Closing the browser clears it. No preference persists consent across restarts.

The panel displays up to 500 events, each with an ordered sequence/time, browser/request correlation identifiers where available, event kind, sanitized location/redirect destination, numeric CEF resource type, HTTP status, numeric load error/outcome and fixed filter category. Request IDs are CEF-local correlation numbers, never provider/participant IDs. Completion events describe transport loading only; load errors can represent a later failure or cancellation. Concurrent tabs interleave, so use browser/request IDs rather than assuming adjacent rows form a chain. Cache/worker/contextless traffic and popup event coverage are not comprehensive.

Only HTTP(S) locations are accepted; query, fragment and user information are discarded. Paths retain only a small structural vocabulary, with other segments replaced and depth capped. IP literals, unusual numeric/long host labels and opaque subdomains are redacted. Registered hostnames remain useful but can identify visited services; review before sharing. Each URL field is at most 512 characters. Arbitrary script names/paths are intentionally lost. Raw console text, source code, page contents, rule literals, cookies, request/response headers, bodies, credentials and exception messages are not accepted by the record schema. Console errors retain only occurrence and sanitized source, not their potentially sensitive text. No disk writes occur until an explicit export; JSON serialization and writing run off the UI thread. Export failures show a generic local error, not a path or exception dump. Stop and export atomically captures a private snapshot, stops collection and clears live entries before the save dialog opens. Cancelling discards that snapshot; it does not restart collection. Export is capped at 4 MiB of UTF-8 JSON. A failed write returns a generic failure; a partial destination file may remain and must not be shared. An already requested export may finish after the panel closes.

These guarantees apply to this new trace, not native chromium.log, DevTools, the older privacy inspector's literal rules or mock-lab reports. Do not upload raw browser profiles or native logs. Diagnostics failures are isolated from request decisions; bounded best-effort collection can miss events and cannot prove absence of a network error.

## Interpret evidence without evasion

- FilterBlocked plus its category identifies an Aegis-owned cancellation. Correlate with the same authorized flow and configuration; existing cancellation alone does not establish that the resource was essential.
- Redirect events/statuses, network error codes, missing completion and console-error occurrence help isolate browser/network issues. No cookie values are collected; storage/authentication problems remain hypotheses until demonstrated by isolated local tests or provider-approved evidence.
- If the document loads and displays Security, keep it intact. The panel explains that AegisBrowser does not override provider decisions. No page-content scanner infers fraud decisions or fabricates a success state.
- If provider records establish intentional rejection of embedded/unsupported browsers, use a provider-supported browser or administrator-approved compatibility route. Do not retry with altered identity, manipulate status parameters or bypass verification.

## Regression coverage and actual execution

New console-suite tests cover sanitizer token/userinfo/query/fragment/path/subdomain/IP removal, schema redaction, disabled collection, entry limits, diagnostic failure isolation, existing exact URL/type exception scope, unrelated domains and independently evaluated redirect targets. New native fixtures cover blocked/allowed decisions, 302 observation, unchanged Security page rendering, console payload omission, disable, repeated attachment/disposal and normal native cleanup. Existing invalid-certificate and lifecycle suites remain unchanged.

**Executed:** seven existing static security gates and five Python audit tests passed; git diff whitespace check passed. **Not executed:** new/existing C# tests, restore/build, native fixtures, Windows smoke/publish. This macOS environment has no dotnet or Windows runtime. No live survey/provider was contacted. No manual acceptance is claimed.

## Windows validation

From a reviewed checkout with .NET 8 and Python 3.12+, run the existing Windows workflow, or its commands:

```powershell
python scripts/security_audit.py
python -m unittest discover -s scripts -p test_security_audit.py -v
dotnet restore AegisBrowser/AegisBrowser.csproj -r win-x64 -p:SelfContained=true
dotnet build AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 -p:SelfContained=true --no-restore
dotnet run --project AegisBrowser.Tests -c Release --runtime win-x64 -p:PlatformTarget=x64
dotnet build AegisBrowser.BrowserTests -c Release -r win-x64 -p:SelfContained=true
$profiles = Join-Path $env:TEMP ('aegis-diag-' + [guid]::NewGuid().ToString('N'))
foreach ($phase in @('write','read','read','read')) {
  & ./AegisBrowser.BrowserTests/bin/Release/net8.0-windows/win-x64/AegisBrowser.BrowserTests.exe $phase $profiles
  if ($LASTEXITCODE -ne 0) { throw 'Browser suite failed' }
}
dotnet build AegisBrowser.FilteringTests -c Release -r win-x64 -p:SelfContained=true
& ./AegisBrowser.FilteringTests/bin/Release/net8.0-windows/win-x64/AegisBrowser.FilteringTests.exe
# Also run the workflow's privacy write/read phases and strict native assertion gates.
dotnet publish AegisBrowser/AegisBrowser.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
./tests/acceptance/windows-smoke.ps1 -PublishDirectory artifacts/win-x64
```

The workflow checks process completion markers and native assertion output in addition to exit codes; it is the authoritative full verification path. Stop on any failure. Packaging additionally requires a clean reviewed commit and the existing package/SBOM/ZIP verification script. Do not discard local work to satisfy that gate.

CefSharp console event fields and severities were checked against the [154.0.340 event source](https://raw.githubusercontent.com/cefsharp/CefSharp/v154.0.340/CefSharp/Event/ConsoleMessageEventArgs.cs) and [enum](https://raw.githubusercontent.com/cefsharp/CefSharp/v154.0.340/CefSharp/Enums/LogSeverity.cs). Resource redirect/completion signatures reuse the existing LabRequests implementation. These inspections are not compilation evidence.

## Validation follow-up and controlled reproduction

**Windows validation remains blocked.** The source reviewed is release/windows-rc at HEAD 9fd457276d3e741ade1041ea37655115b7beea4c plus uncommitted changes, not a CI-verified commit. Fresh command output is preserved in [validation.log](../review-deliverables/navigation-diagnostics-validation/validation.log). dotnet --info, restore, build, console tests and filtering-test build could not start because dotnet is absent. The host is Darwin; pwsh is also absent. Seven static gates, five Python tests, seven XML parses and the whitespace check passed. These do not prove C# privacy tests, CEF API compatibility, TLS rejection, same-origin behavior or shutdown on Windows. Attempts to fetch the tagged load-event API sources were unavailable; compilation remains the API compatibility gate.

Confirmed diagnostics-only repairs in this follow-up:

- Previously, stopping by unchecking collection erased evidence before export. Stop and export now obtains a bounded snapshot and stops atomically, without changing disable-and-clear semantics.
- The GUI and console export now share one bounded serializer and asynchronous write path. Repeated export clicks are disabled while a write is active. New tests cover TEST_TOKEN_123, concurrent producers, stopping, serialized byte limits, local save and filesystem failure.
- Event removal alone does not exclude a callback already dispatched by CEF. Attachment disposal and observations now share a gate; callbacks dispatched after disposal do not read native arguments or record data. Existing native detach/re-attach coverage remains required and unexecuted.

### Consent-based reproduction — only after Windows validation

1. Use a dedicated ordinary test profile: `AegisBrowser.exe --profile=survey-diagnostics`. Close other instances of that profile. Do not use --lab: lab mode intentionally confines navigation to its local fixtures.
2. Obtain authorization for the survey and recording its sanitized host metadata. Do not place the complete survey URL in scripts, issue comments or console logs.
3. Open Local trace, review its privacy notice and enable collection. Keep unrelated tabs closed. Navigate and interact manually; no survey participation is automated.
4. Reproduce only as needed for one useful trace. Leave any SECURITY page intact; do not modify its parameters, identity or security checks.
5. Select **Stop and export JSON** immediately. This stops collection before the file dialog and preserves only the explicit export snapshot. Do not uncheck collection first: that deliberately erases history. Save locally. Cancelling discards the snapshot.
6. Inspect the JSON locally before sharing: no TEST-like or actual tokens, userinfo, query/fragment, IP literals, headers, bodies, raw console text or participant path values should appear. Registered hostnames and timestamps can still identify a visit. Share only with an authorized administrator after review. Do not include native logs or profiles.
7. Do not repeatedly re-enter the survey to evade a rejection. If evidence is sufficient, stop. This procedure is prepared, **not performed**, and is not a request for manual acceptance before the Windows gates pass.

### Administrator evidence needed

Ask the survey administrator, through their approved support channel, whether the Security result is an intentional server-side termination, what documented reason category applies, whether embedded Chromium/CefSharp is supported, and whether required script/redirect endpoints failed in their server logs. Supply only the reviewed host/status/redirect/error summary and approximate timestamp/timezone. The administrator should correlate their own session records; do not add participant IDs or raw survey URLs to this trace. Request a separately approved test session or compatibility route if necessary; do not assume reuse of a terminated survey session is authorized.

### Current classification

- **A — Confirmed AegisBrowser defect:** diagnostic stop/export and late-callback lifecycle gaps, repaired in source but runtime-unverified. Neither establishes the cause of SECURITY.
- **B — Confirmed compatibility issue:** none established.
- **C — Provider-side rejection:** possible, not established merely because a page renders or no error is visible. Provider evidence is required to identify intentional rejection or its reason.
- **D — Insufficient evidence:** current classification of the actual survey failure. Keep request decisions, cookies, filtering rules, identity and TLS unchanged.

Release remains unapproved until the current source compiles, every required Windows suite passes, and publishing/package/smoke verification is complete. No commit, push, merge or release was performed.
