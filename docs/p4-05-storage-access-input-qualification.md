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
API and controlled test design. It is not an assertion that hosted delivery
or user-mediated Storage Access permission has been verified.

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

## Verification and acceptance

The local browser-test project builds and the source-derived input route has
been exercised in the CEF-only fixture. Hosted Windows x64 real-HTTPS results,
permission decisions and final-head CI must be read from the research PR and
its logs. Missing gesture, denial, grant or persistence evidence remains
inconclusive; a green infrastructure workflow is not permission qualification.
No result approves production deployment.
