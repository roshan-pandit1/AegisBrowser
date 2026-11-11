# P4-05: Cross-origin Storage Access input qualification

The research branch starts at PR #61 commit
`5d8f03beed6983d7416d2fd5c2af19edaa74f517`. The target is Windows x64,
.NET 8, CefSharp 154.0.340, CEF `154.0.34+g14c5a08`, and Chromium
`154.0.8037.98`. The production browser, package pins, certificate policy,
sandbox, proxy guard and permission handler are unchanged.

## Input boundary and feasibility

P4-04 observed that a visible cross-origin control did not receive input on
the hosted runner, even when Windows accepted the test input. The cause was
not established. A successful dispatch response is not evidence that the
intended frame handled a click.

The pinned [Chromium input handler](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/content/browser/devtools/protocol/input_handler.cc)
routes browser-level DevTools mouse events through an asynchronous widget
hit test. It requests a compositor visual-state callback before dispatching a
wheel event, but not before ordinary mouse events. The pinned
[input protocol](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/third_party/blink/public/devtools_protocol/domains/Input.pdl)
uses main-frame viewport CSS coordinates. This identifies a supported
**browser-level** route to test; it does not prove that a stale compositor
state caused the earlier failure.

The test-only adapter sends a zero-delta wheel as a visual-state barrier,
then mouse move, press and release through the existing CEF DevTools client.
It opens no remote debugging port and does not use a JavaScript `userGesture`
override or a permission override. It checks the controlled iframe's source,
visibility, viewport geometry and parent-document hit point. It also obtains
the uniquely named child from CEF, checks its URL, main-frame parent and stable
CEF frame identifier before and after input, and correlates the iframe's
`postMessage` source and origin. A root DevTools frame-tree lookup is reported
separately; a CEF frame identifier is not assumed to be a DevTools target ID.
If a matching iframe target is exposed, the adapter rejects ambiguous targets
or a conflicting reported parent and rechecks that target's ID before input.
This protocol relies on the controlled test pages and runner; it is not a
hostile-administrator-resistant identity system.

CEF's [pinned DevTools interface](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/include/cef_browser.h)
exposes browser-host protocol commands. Chromium's
[Target protocol](https://raw.githubusercontent.com/chromium/chromium/154.0.8037.98/third_party/blink/public/devtools_protocol/domains/Target.pdl)
also defines target sessions, but this experiment does not claim to attach an
out-of-process iframe session: the browser-level input handler performs the
widget hit test. CEF frame-tree evidence alone does not prove which renderer
process ultimately handled input. The controlled frame's received click,
`isTrusted` and transient activation are recorded separately from command
acknowledgment and from any permission result.

The feasibility finding is `SUPPORTED_INPUT_ROUTE_IDENTIFIED` for the pinned
API and controlled test design. It is not a user-approved Storage Access grant.

## Local observations and negative checks

On the Windows ARM64 development host under x64 emulation, the in-process CEF
fixture identified the expected child frame and observed a trusted automated
click with transient activation. The root DevTools frame-tree query did not
list a direct child, while target discovery returned one matching iframe target
whose reported parent matched the root frame. The test also used CEF's frame
registry and the controlled message source. This fixture does not exercise real HTTPS sockets and
is not evidence of a Storage Access grant or a physical user's action.

The preflight checks that a script-generated iframe click is untrusted and
that a wrong URL, hidden iframe, offscreen coordinates and destroyed iframe do
not receive the test adapter's input. The existing missing-click check remains. Each attempt emits
a bounded trace with a dropped-event count. A missing frame, timeout, missing
click or changed frame identity cannot produce a successful input result.
Other requested cases, including a permission prompt interrupted by closure,
are not established by these checks.

One local x64-emulated run logged CEF `Timeout of new browser info response`
messages during the inherited storage fixture despite completing its checks
and shutdown. Their cause is not established by this experiment.

## HTTPS, permissions and authentication

The existing real-HTTPS fixture keeps normal certificate validation and uses
temporary, exact-root trust only on a disposable hosted runner. The controlled
iframe calls `requestStorageAccess` only in its visible button handler after
observing both trusted input and transient activation. Its reported API
result, the production `OriginPermissions` reports, and server-observed
synthetic cookie booleans are separate evidence. An API resolution while
cookies were already available is not a newly granted permission. No automatic
grant, origin exception or permission override is introduced.

On Windows x64, the initial P4-05 [push run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42954142021)
and [PR run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42955052461)
at commit `664249125fc7f5d7ac02db9c709b57beba990058` both observed the
controlled real-HTTPS iframe click as trusted with transient activation. In
the enabled case, the existing production `OriginPermissions` handler
reported a `StorageAccess` permission as blocked; the API rejected with
`NotAllowedError`, `hasStorageAccess` remained false, and the server received
neither the ordinary nor the HttpOnly synthetic cookie. The embedded synthetic
session remained unavailable. This is an application-handler denial, not a
user's explicit choice in a prompt.

With blocking disabled, access was already true before the request; the API
resolved and both synthetic cookies reached the server. No new grant follows
from that resolution. The sandboxed iframe without the storage-access sandbox
token rejected its request with `NotAllowedError`; no storage-named handler
callback was reported for that case. After navigation and process restart,
the enabled fixture still reported access unavailable. Because no grant was
established, this is not grant-persistence evidence. Both runs observed CEF
shutdown and the independent temporary-root cleanup.

P4-04 measured a synthetic embedded-cookie-dependent login that worked with
blocking disabled and failed with blocking enabled; its synthetic popup flow
worked. Those results are scoped to the fixtures. A user may turn off the
opt-in third-party-cookie setting for a profile and restart that profile if an
embedded feature requires it, with the privacy tradeoff described by the UI.
That is not a site-specific exception or evidence of universal compatibility.

## SOCKS observation

The existing offline assertion still requires a non-`Aborted` load error and
rejects successful navigation. P4-05 emits the bounded trace immediately on a
missing-error timeout before attempting browser teardown. The concurrent
supervisor also prints bounded available output from a timed-out child and
continues reporting the other child; a missing exit remains a failure. It does
not force native browser disposal or establish system-wide proxy containment.
The historical intermittent SOCKS root cause remains inconclusive.
Separate synthetic checks exercise the missing-callback timeout, rejection of
an unexpected successful navigation and trace overflow accounting. Their
records are explicitly labeled `SYNTHETIC`; they are not CEF observations.

## Verification and acceptance

The local browser-test project builds and the source-derived input route has
been exercised in the CEF-only fixture. The initial hosted runs supplied the
controlled real-HTTPS evidence above; later final-head results must be read
from the research PR and its logs. No result approves production deployment.

| Area | Result and tested scope |
| --- | --- |
| `CROSS_ORIGIN_INPUT_ROUTE` | SUPPORTED for the pinned browser-level test API. |
| `FRAME_TARGET_ATTRIBUTION` | VERIFIED for the controlled named iframe using CEF, target metadata and fixture messages. |
| `TRUSTED_CLICK_DELIVERY` | VERIFIED for automated input to the controlled iframe. |
| `TRANSIENT_USER_ACTIVATION` | VERIFIED at the controlled automated click handler. |
| `STORAGE_ACCESS_NO_GESTURE` | VERIFIED for the controlled enabled and baseline HTTPS cases. |
| `STORAGE_ACCESS_WITH_GESTURE` | VERIFIED for API execution and the observed handler-denied result. |
| `STORAGE_PERMISSION_DENIAL` | VERIFIED for the production handler's denial, not a user's prompt choice. |
| `STORAGE_PERMISSION_GRANT` | INCONCLUSIVE; no user-approved grant was observed. |
| `GRANT_PERSISTENCE` | INCONCLUSIVE; no grant was established. |
| `HTTPS_COOKIE_TRANSMISSION` | VERIFIED for synthetic ordinary and HttpOnly loopback cookies. |
| `EMBEDDED_LOGIN_COMPATIBILITY` | FAILED for the synthetic cookie-dependent iframe with blocking enabled. |
| `OAUTH_POPUP_COMPATIBILITY` | VERIFIED for the controlled synthetic popup. |
| `SOCKS_CALLBACK_ORDERING` | VERIFIED for the observed offline and cancellation cases. |
| `SOCKS_ROOT_CAUSE` | INCONCLUSIVE. |
| `SOCKS_TEST_RELIABILITY` | INCOMPLETE. |
| `TLS_VALIDATION` | VERIFIED for the controlled certificate fixtures. |
| `PROFILE_ISOLATION` | VERIFIED within disposable-profile tests. |
| `NETWORK_LEAK_COVERAGE` | INCOMPLETE. |
| `PRODUCTION_READINESS` | NOT_APPROVED. |
