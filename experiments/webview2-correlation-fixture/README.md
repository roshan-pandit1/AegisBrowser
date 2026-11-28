# Isolated navigation-correlation fixture

This separate experiment observes one host-dispatched WebView2
navigation. It does not repair or exercise the prototype lease tracker. Its
observations cannot authenticate a navigation or prove production safety.
It is not a portable release or the diagnostic v4 build.

**Do not run on the development host.** WebResourceRequested interception is
not OS network isolation. In particular, WebView2 can issue requests while a
NavigationStarting callback runs; a handler failure can also leave a request
without the intended response. Use a separately authorized disposable VM with
independently verified network isolation, a standard test account and no real
profiles, credentials or user data. The .invalid hostname is not a firewall.

No artifact has yet been authenticated or authorized for execution. Before a
future run, authenticate the pinned SDK and complete the artifact hash inventory;
obtain explicit approval for the exact artifact hashes, runtime, profile root,
diagnostic writes, process creation, VM isolation and evidence transfer.

The fixture uses the existing profile-root and timeout argument policy. The
timeout argument is validated but is **not enforced** by this observation-only
fixture; a missing callback or synchronous disposal may leave it running. It
creates a fresh GUID-scoped profile underneath the approved existing root and
writes `correlation.log` in that new directory. It logs a sequence number,
monotonic elapsed milliseconds, managed thread ID, bounded event name, numeric
navigation ID, boolean comparisons, and a validated runtime version. It does
not log the URI, header, cookie values, page text or command line. Records use
a bounded queue and a background writer: I/O failures, a full queue, process
termination, or a blocked sink can leave an incomplete log. A bounded flush is
attempted after the message loop returns; no missing record proves an event did
not occur, and absence of a disposal record cannot establish its outcome.

The single request contains a unique URI path and a host-supplied header; the
static response has no scripts or subresources. A catch-all resource handler
provides a response for observed requests. No callback timing, headers, storage,
or normal shutdown is presumed; compare the observed ordering and matching
flags in the log. Close before completion is refused. If the documented terminal
event is missing, do not classify this fixture as passed or force cleanup as a
claim of graceful shutdown. Runtime evidence can characterize behavior in the
approved version and environment but cannot prove absent competing navigations
or establish a general API ownership guarantee.

Only a normal single dispatch is observed. The fixture does not execute a
competing-navigation, replay, redirect, cancellation, close-during-initialization,
or missing-terminal test; those remain separate unverified runtime cases.
Abnormal message-loop failure is not graceful disposal. The program does not
automatically dispose the form in its exception path; OS cleanup on termination
is not evidence that pending browser operations closed safely.

Suggested separately authorized command (replace the executable path only after
hash verification; do not use an existing run directory):

```powershell
& 'C:\Program Files\dotnet\x64\dotnet.exe' exec '<verified fixture path>\CorrelationFixture.dll' --profile-root 'C:\AegisLab\Profiles' --shutdown-timeout-ms 30000
```

Inspect and preserve the sanitized log and process outcome separately. The
normal disposal call remains synchronous on the STA and can hang. A missing
close marker is not successful shutdown. Do not delete profiles or terminate
unrelated processes. The test does not cover real browsing, storage isolation,
proxy containment or renderer sandbox enforcement.
