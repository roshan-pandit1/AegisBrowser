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

A separate local test holds a SOCKS request until the fixture observes it,
then deliberately stops that navigation. CEF reported `Aborted`; this is
tested separately and is not accepted as the offline-proxy error. The held
fixture is bounded by cancellation when disposed. This observation does not
explain the intermittent missing offline callback.

After adding that cancellation case, another local concurrent run completed
both independent processes with exit code 0 and 19 proxy checks each. Both
observed `ProxyConnectionFailed` for the offline request and `Aborted` for the
separately held request. One process logged a GPU cache path-length error in
the deeply nested local test directory; the test completed and this does not
establish a cause for the earlier SOCKS callback timeout.

## Storage Access research

The P4-03 real TLS fixture is reused on a disposable hosted runner with its
temporary test-user CA and exact-root cleanup. The experiment sends mouse
input to visible test controls. It uses CEF's internal DevTools input client
for the top-level control. For cross-origin input it validates that the target
belongs to the test process and its browser window. The preflight posts
messages only to that window and does not move the system cursor. It does not
set a JavaScript `userGesture` override
for the Storage Access request, open a remote debugging port, or grant a
permission. The page reports `event.isTrusted` and
`navigator.userActivation.isActive` at the instant it calls the API. These are
automated input events, not evidence of a physical user's action. A local
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
none is reported. Callback labels come from the existing handler's sanitized
report; an unrecognized or numeric permission type is counted as `other`, so
the absence of a storage-named callback alone does not prove that Chromium
never exposed a related permission event.

The first hosted [push run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42857319748)
observed that a no-gesture request was rejected with blocking enabled but
resolved when cookies were already allowed. It then failed before the gesture
test: a typed DevTools client expected a different message ID after the older
popup fixture had submitted an automatically numbered command. Exact test-root
removal and independent cleanup were verified. The test now obtains IDs from
the same CefSharp browser-host wrapper for the popup command, keeping its
counter aligned with the input client. A local preflight exercised the input
both before and after that popup command. The existing popup fixture uses a
DevTools gesture flag; it is not evidence of a physical user action. The new
Storage Access request itself uses a visible button and separately observed
trusted mouse input and activation.

The [next hosted run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42859660481)
reached the visible cross-site iframe but timed out without a completed input
result; root removal was verified. A local cross-origin in-process CEF fixture
reproduced the missing DevTools input while a same-origin iframe worked. A
separate window-message experiment reached the cross-origin button locally;
the [subsequent hosted preflight](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42862556342)
also failed to deliver a cross-origin window-message click. It stopped before
installing test trust. In the [runner-only `SendInput` experiment](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42864070119),
Windows accepted the input after ownership and foreground checks, but the
cross-origin click was still not observed. That unsuccessful global-input
experiment was removed; it did not establish the cause of the missing click.

The fixture now reports a distinct `INCONCLUSIVE` record when the controlled
cross-origin click is not observed within ten seconds. It does not call the
Storage Access API on behalf of that missing click. The workflow records a
warning and a step summary, while continuing the independent HTTPS cookie,
embedded-session, restart and proxy tests. Unexpected input, parsing, TLS,
cookie, browser-close and shutdown errors still fail their tests. A completed
workflow with an inconclusive input marker is not a Storage Access grant or
denial qualification. If a click is observed, both the trusted-event and
transient-activation checks are mandatory before reporting a result.
The input preflight also checks a deliberately unchanged pending marker, which
must remain unobserved; this is a synthetic negative test, not a browser
permission decision.

The embedded session is a synthetic server-side cookie requirement, not a real
identity-provider integration. It reports whether the test cookies arrived
before and, when an API request occurs, after that request. The fixture also
prepares a sandboxed iframe without the storage-access sandbox token and
queries both top-level test sites again after navigation and restart. A
sandboxed-iframe rejection requires an observed click and API result; grant,
denial and grant-persistence outcomes remain unverified.

On hosted Windows x64 [push run 42867102027](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42867102027)
at commit `1778caf9f9fa944931af115eff150a58113fd01f`, the verified HTTPS
server received one cross-site request in each cookie scenario. With the
setting disabled, both synthetic ordinary and HttpOnly cookies arrived and the
synthetic response cookie was stored. With it enabled, neither test cookie
arrived and the response cookie was not stored. The embedded-session fixture
accordingly reported `true` without blocking and `false` with blocking. This
is a compatibility failure for that controlled embedded-cookie-dependent
flow, not evidence about every authentication provider.

Without transient activation, the enabled case reported `before=false`,
`rejected:NotAllowedError`, `after=false`; the baseline reported
`before=true`, `resolved`, `after=true`. Resolution with access already
available is not evidence of a newly granted permission. The browser requested
the visible iframe control in three gesture scenarios, but no click was
observed in the bounded window; all three were recorded as inconclusive and no
Storage Access request or permission decision was inferred from them. After
restart, both queried sites reported access unavailable in that profile; since
no grant was established, this is not a grant-persistence test. The HTTPS
write and read phases both observed normal CEF shutdown and removed the
temporary test-user root. The independent cleanup step also completed.

The same Windows run completed both concurrent proxy fixtures with exit code
0. Its [pull-request counterpart](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42867116258)
also completed successfully. These workflow conclusions include explicit
inconclusive gesture results and are not full Storage Access qualification.

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

## Acceptance at the recorded hosted run

| Area | Result and scope |
| --- | --- |
| `SOCKS_TIMEOUT_REPRODUCTION` | VERIFIED in the earlier P4-03 local and hosted failures; not reproduced in the later traced runs. |
| `SOCKS_ROOT_CAUSE` | INCONCLUSIVE; the historical failure had no P4-04 trace. |
| `SOCKS_CALLBACK_ORDERING` | VERIFIED for the observed offline and deliberate-cancellation sequences only. |
| `SOCKS_TERMINAL_NAVIGATION` | VERIFIED for the observed fixtures; no successful offline response was accepted. |
| `SOCKS_TEST_RELIABILITY` | INCOMPLETE; the intermittent failure is not explained. |
| `STORAGE_ACCESS_API_SUPPORT` | SUPPORTED for the tested HTTPS iframe API. |
| `STORAGE_ACCESS_NO_GESTURE` | VERIFIED for the controlled blocked and baseline cases described above. |
| `STORAGE_ACCESS_USER_GESTURE` | INCONCLUSIVE for real HTTPS; local trusted-input preflight is not an API outcome. |
| `STORAGE_ACCESS_PERMISSION_DENIAL` | INCONCLUSIVE; no user-mediated denial was observed. |
| `STORAGE_ACCESS_PERMISSION_GRANT` | INCONCLUSIVE; no user-mediated grant was observed. |
| `STORAGE_ACCESS_PERSISTENCE` | INCONCLUSIVE for grants; no grant was established. |
| `HTTPS_COOKIE_BEHAVIOR` | VERIFIED for the controlled loopback HTTPS cookies and selected modes. |
| `EMBEDDED_AUTH_COMPATIBILITY` | FAILED for the synthetic cookie-dependent iframe with protection enabled. |
| `OAUTH_POPUP_COMPATIBILITY` | VERIFIED for the controlled synthetic popup/redirect fixture. |
| `PROFILE_ISOLATION` | VERIFIED only for the existing disposable-profile tests. |
| `TLS_VALIDATION` | VERIFIED for the controlled trusted, untrusted and wrong-host fixtures. |
| `NETWORK_LEAK_COVERAGE` | INCOMPLETE; proxy fixtures do not establish system-wide containment. |
| `PRODUCTION_READINESS` | NOT_APPROVED. |
