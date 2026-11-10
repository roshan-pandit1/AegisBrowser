# P4-04: Storage Access and SOCKS-offline observations

This branch starts at PR #60 commit `c7f9a3510e26d22a598e48496867dcb1e4bba881`.
The test target remains Windows x64, .NET 8, CefSharp 154.0.340, CEF
`154.0.34+g14c5a08+chromium-154.0.8037.98`. No production package, proxy,
certificate, sandbox or popup-lifetime settings are changed.

## SOCKS callback tracing

The test-only SOCKS server binds loopback and accepts only the synthetic
`aegis-socks.invalid` destination. A bounded, monotonic trace records an
opaque run ID, listener and SOCKS events, main-frame callback kinds, loading
state, close and CEF shutdown. It records a dropped-event count and does not
include request headers or credentials. The trace remains subscribed through
browser closure, so a cancellation after a timeout can be distinguished from
the originally awaited offline error. It does not observe every Chromium
network event or prove that no unobserved direct traffic occurred.

The listener is stopped before the offline navigation. The test still requires
a matching load error within 25 seconds, rejects `Aborted` as a substitute for
an offline-proxy error, and rejects a successful HTTP response. It waits for a
bounded loading-state transition before accepting the observed outcome. This
does not assert that a missing callback means success or explain why it was
missing. The pinned [CEF load-handler contract](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_load_handler.h)
distinguishes loading state, frame load and failure/cancellation callbacks;
the loading state is browser-wide rather than an independent per-request ID.

Three local concurrent runs and one single-process run on the ARM64 host with
x64 emulation completed with the expected error in the traced cases. One
single-process trace showed listener stopped at 11557 ms, navigation requested
at 11557 ms, `ProxyConnectionFailed` at 11582 ms, an HTTP-status-0 load end and
loading settled at 11602 ms, then browser close and CEF shutdown. No trace
events were dropped in that run. Earlier P4-03 runs did reproduce a timeout;
these later successes do not establish its cause or eliminate it. The
[original failed hosted attempt](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42820221465/attempts/1)
remains part of the evidence.

## Storage Access research

The P4-03 real TLS fixture is reused on a disposable hosted runner with its
temporary test-user CA and exact-root cleanup. The experiment sends mouse
press/release input through CEF's internal DevTools input client to a visible
test button; it does not set a JavaScript `userGesture` override, open a remote
debugging port, or grant a permission. The page reports `event.isTrusted` and
`navigator.userActivation.isActive` at the instant it calls the API. A local
CEF-only preflight observed both flags for a top-level button and an iframe;
that preflight does not establish HTTPS or cross-site permission behavior.

The pinned [Chromium 154 Storage Access permission implementation](https://github.com/chromium/chromium/blob/154.0.8037.98/chrome/browser/storage_access_api/storage_access_grant_permission_context.cc)
can resolve early when cookie settings already permit access and otherwise
checks user activation and may check prior top-level interaction before a
prompt. Thus API resolution alone is not proof of a new grant or user consent.
The fixture exercises a visible requesting-site control before its embedded
request and separately records production `OriginPermissions` reports,
promise results, `hasStorageAccess`, and server-observed synthetic cookie
presence. The application handler denies permission types outside its existing
supported set if CEF invokes it; the experiment does not infer a callback when
none is reported.

The embedded session is a synthetic server-side cookie requirement, not a real
identity-provider integration. It reports whether the test cookies arrived
before and after the API request. The fixture also exercises a sandboxed iframe
without the storage-access sandbox token and queries both top-level test sites
again after navigation and restart. Grant, denial and persistence outcomes
remain unverified until the hosted HTTPS run supplies those observations.

## Verification and limitations

The local x64 test project builds. The local in-process CEF input preflight
passed for the trusted-click and activation checks, and the existing local
cookie/OAuth fixture completed normal CEF shutdown. The local build reported a
NuGet vulnerability-metadata retrieval warning; cached pinned packages were
used. Hosted Windows x64 test results and their exact revision must be read
from this branch's CI, not inferred from the local preflight.

No experiment changes real user certificate trust or profiles. No synthetic
result establishes full network containment, universal embedded sign-in
compatibility, a user-approved storage grant, or production readiness.
