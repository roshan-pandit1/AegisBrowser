# Session-only history

The history service holds at most 1,000
entries in process memory and has no storage or logging API. It is not private
browsing and does not clear Chromium caches or cookies, or hide activity from
websites, the network or the operating system. Clearing drops service references;
the .NET runtime does not guarantee secure memory erasure. Chromium uses its own
profile cache and may persist history independently of this feature.

Only successful main-frame HTTP(S) loads below 400 are candidates. Redirects
use the final committed URL. Failed, aborted and HTTP error loads are excluded.
Reload does not create or update an entry; back and forward each create a visit.
Same-document changes, including hash and pushState, are not recorded. Userinfo,
query strings and fragments are removed; revisiting a saved entry therefore
does not restore its query. Entries use a hostname when no trustworthy title
is available. Repeated completion for one navigation generation is ignored.
Pausing invalidates in-flight recordings and resuming never backfills them.
Closing a tab keeps its entries; opening them uses the current tab or a new tab.
The service serializes state changes with a lock, takes snapshots under that
lock, and calls change observers only after releasing it. An empty or
sanitized-empty title displays the saved URL's hostname. When a visit completes,
the tracker queries the current document title even if CEF delivered the title
event before the visit was recorded; a result from an older navigation is ignored.
Titles are cleaned of controls and bidi formatting characters, at most the first
2,048 UTF-16 code units are examined, and the result is limited to 256 code
units without splitting a surrogate pair. URLs over 2,048 code units are rejected.
The oldest visit is evicted when the entry limit is reached. One History window
is maintained per browser window; opening it makes no title or favicon request.

The pinned CefSharp 154.0.340 documentation says frame-load callbacks occur on
the CEF UI thread, `IsMain` distinguishes the main frame, and frame-load-end can
occur after failure. Its multi-threaded message loop is enabled by default and
the application does not override it. The loopback observation fixture records
event counts and thread classifications, not URLs or titles. In a Windows 11
x64 run the observed address, navigation, load and title callbacks ran on the
CEF UI thread rather than the WinForms thread. A redirected main frame
reported a final 200, 404 and 500 pages reported their HTTP status, a reset reported
`EmptyResponse` then end status 0, and reload and back/forward had distinct
transition flags. A pushState address event appeared without an additional
frame-load start; subframe callbacks and an aborted load were also observed.
A title notification sometimes arrived after loading became
false and after the next fixture action began. These are fixture observations,
not guarantees about every callback order. The tracker uses per-browser
navigation generations and queries the current document title rather than using
the payload of an unidentifiable late title event. A query result is applied
only to the still-current generation; otherwise the hostname remains the title.
The native fixture covers popup ownership and a pushState call during loading.
Additional callback races and same-document cases remain unverified.

Existing Ctrl+L, Ctrl+T, Ctrl+W and Ctrl+Tab are handled both by the form and
the CefSharp keyboard handler, which is intended to handle the key before page
handling. Actual shortcut behavior depends on a focused interactive window and
must be reported as NOT_RUN when that focus is unavailable. Popup
windows are CEF-owned through `SafePopups`; explicit load/display handlers observe
their callbacks without taking ownership. A loopback fixture observed a popup
visit. The existing WinForms address update leaves
focused text alone. A previous disposable native CEF test created files named
`History` in its cache directories. In a disposable CEF fixture configured with
the same root/cache pattern as the app, a unique canary also appeared in
`Default/History`, `Default/Cache/Cache_Data/data_1`, and
`segmentation_platform/ukm_db`. The scan did not find it in fixture stdout,
stderr or unclassified files under the isolated profile and temporary roots.
This is an observation of that fixture, not a claim that other locations,
crash dumps or network transmission contain no browsing data.
