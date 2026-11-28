# Navigation correlation: feasibility decision

This is a design assessment for the restricted synthetic-content prototype, not a
production browser or a runtime result. No WebView2 launch, network isolation, or
navigation ownership has been verified by this document. The original v3 bundle
remains frozen. The observations below use Microsoft's published API contracts;
the exact pinned SDK/runtime combination still requires direct validation.

| Candidate | Correlation and terminal events | Storage, cancellation, and security implications | Decision |
| --- | --- | --- | --- |
| A: unique URI and intercepted synthetic response | A host-generated path distinguishes expected attempts, but a matching URI alone does not prove which caller initiated it. Bind a `NavigationId` only after validating the full restricted-fixture state. Terminal completion is matched by that ID. | A stable HTTPS origin can support storage comparisons. Filter and response setup must precede dispatch; an unset response continues the request. Cancelled navigation must remain tracked until terminal completion. The reserved host is not containment. | Useful component, insufficient ownership evidence alone. |
| B: unique URI plus host-supplied request header | Adds an observable correlation signal to A; require a single outstanding dispatch, exact expected state, non-redirect initial start, and replay rejection. A header is not an authenticated request identity. | Headers are retained through redirects, including cross-origin redirects, and stored in navigation history for reload and back/forward. Deny unneeded entry points and redirects; do not treat the marker as a secret. Verify its visibility in the pinned runtime. | Best candidate for a constrained fixture when combined with D, not yet an established ownership guarantee. |
| C: virtual host mapping and serialized navigation | Mapping and serialization serve files but do not by themselves bind a host dispatch to a navigation ID, prevent competing events, or prove terminal ownership. | A mapped HTTPS origin supports origin-based storage. Mapping has frame and worker semantics and must be configured consistently where applicable. It does not prohibit unrelated networking. | Viable local-content transport, no substitute for correlation. |
| D: event-driven tracking of dispatches and started navigations | Separate the pre-dispatch lease from navigation IDs. Match a confirmed terminal event only to its associated ID; reject unrelated, stale, duplicate, and ambiguous events. Keep an unresolved dispatch outstanding if no trustworthy association or terminal event arrives. | Cancellation or deadline expiration alone cannot release a lease. A synchronous dispatch exception does not establish that native work was never queued unless the applicable API contract guarantees it. No disposal while work remains unresolved. | Necessary lifecycle structure; must be paired with independently justified association evidence. |

## Documented behavior and remaining gaps

- The [navigation event documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events) describes overlapping IDs, redirects with the same ID, same-document navigation without a new `NavigationStarting`, and separate frame events. A URI, event order, or `IsUserInitiated` does not establish a unique host invocation. The [event-args reference](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2navigationstartingeventargs) says API-initiated navigation counts as user initiated, and cancellation produces an operation-cancelled completion. It also warns that GET requests may occur while the host responds. Cancellation is not network isolation.
- [NavigateWithWebResourceRequest](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.navigatewithwebresourcerequest) accepts a constructed request and headers, but its public contract does not supply a host-generated call identifier or guarantee the exclusivity of a URI/header tuple. The documentation does not establish that a synchronous exception means native work was not queued. Treat missing callbacks and ambiguous exceptions as unresolved.
- [WebResourceRequested](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webresourcerequested) can provide a synthetic response, but if no response is assigned the original request continues. [Filter documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.addwebresourcerequestedfilter) describes normalized wildcard matching and worker/profile behavior. A filter is not an OS-backed egress boundary. The WebView2 project's reports of [missing document interception in a particular popup scenario](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5293) and [WebSocket interception limitations](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4303) are unverified for this prototype but reinforce the need for actual runtime and isolated-network evidence.
- Microsoft's [local-content guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content) describes a stable virtual-host origin and support for origin-based storage. It does not establish profile isolation or dispatch ownership. Profile isolation remains a separate live test.

## Next discriminating evidence

Prepare a separately authorized, restricted runtime correlation fixture before
claiming a v4 ownership fix. On the approved isolated VM, record only sanitized
sequence markers and booleans for expected URI/header presence, callback kind,
redirect flag, navigation ID relationship, cancellation, and completion. Exercise
normal dispatch, cancellation before interception, competing same-URI navigation,
header absence/replay, history/reload denial, redirects, duplicate/stale events,
and missing completion. A runtime observation characterizes that particular
environment; it cannot turn an undocumented exclusivity property into a general
API guarantee. Missing or contradictory evidence remains inconclusive.

The previous .NET CLI execution unexpectedly created a development certificate on
the development host. It was not authorized as part of this task; do not trust,
export, delete, or otherwise modify it without separate authorization. Future
CLI invocations use the requested process-scoped opt-out variables. No production
readiness, graceful shutdown, or network containment is established here.
