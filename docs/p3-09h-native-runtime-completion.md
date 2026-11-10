# P3-09H: isolated CEF runtime investigation

This is research infrastructure, not production qualification. The starting
revision is `f04a86366bf92e448e1874c82a8e4c6b5c69941f` (PR #53). It retains
the CEF `154.0.34+g14c5a08+chromium-154.0.8037.98` and CefSharp `154.0.340`
pins documented in [P3-09G](p3-09g-native-sandbox-lifecycle.md). The CEF
minimal Windows x64 distribution is downloaded from the official CEF build
host and pinned to SHA-256
`C81C34D048D87276F4A18F0B000206710A1709BB67DD658948B87100E1A538DA`.
The pin was computed after comparison with the upstream index checksum; it is
not an independent publisher signature or a guarantee about source provenance.

## Archive preparation

The [P3-09G hosted run](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42654795771)
verified the archive hash, started `tar.exe` on `Release` and `Resources`, and
hit the eight-minute step limit. Those logs do not distinguish slow extraction
from a hang or identify its root cause; native execution was never reached.

`tests/research/p309g/prepare_cef_archive.py` hashes and streams the same open
archive handle. It checks the archive root, rejects ambiguous Windows paths,
duplicate names, links and non-file entries, bounds entries and uncompressed
sizes, and writes selected files to a newly created destination. Required
bootstrap, libcef and chrome_elf hashes must match. Failed or interrupted
destinations are not reused. The CI preparation step is bounded separately
from native execution. This assumes the runner-owned directory is not being
modified by a hostile local process; retaining a handle does not prevent an
in-place writer. The outer CI step supplies the timeout, not the Python
extractor itself. Staging permits only the known release-file names and does
not stage the unused `bootstrap.exe` or import library. Resources and locales
are retained; their complete runtime necessity has not been independently
proven.

On the Windows ARM64 development host, the pinned 172,752,666-byte archive
produced 1,001 inspected entries and 236 selected files. The revised extractor
reported 11.705 seconds for extraction and 13.737 seconds through the three
native hash checks, exit 0. That local timing does not establish hosted x64
performance. The workflow reports stage outcomes and durations separately;
hosted results must be read from the exact final-head run.

## Lifecycle and renderer evidence

The standalone research client records a bounded, allowlisted child-launch
role and whether two forbidden command switches are present. These are CEF
launch-request observations, not independent process identity or sandbox
evidence. It distinguishes an aborted fixture navigation from an unrelated
preliminary cancellation and requests normal close on a fixture failure; no
timeout is interpreted as native closure. Renderer termination is recorded
without logging CEF's free-form error text. Neither a GPU-process error nor a
navigation cancellation alone identifies the sandbox failure mechanism.

The handle-bound lineage helper checks a caller-supplied live direct child
against a retained parent handle, creation times, image paths and the Windows
parent relationship before invoking the existing read-only token observer.
Its tests include a live sibling rejection. It does **not** discover a
renderer, prove a CEF process role, authenticate a mapped image's hash, prevent
an adversary spoofing parentage, or enumerate all descendants. It is not wired
into the native supervisor. Token snapshots, subprocess cleanup and sandbox
enforcement therefore remain inconclusive. Chromium's renderer security is a
layered policy; a requested sandbox setting, low integrity alone, or the
presence of a bootstrap sandbox pointer cannot verify enforcement.

The previous local supervised run observed initialization and browser creation,
an aborted navigation and an unusable GPU-process message, then timed out.
Only its retained test-owned parent was terminated; there was no fixture-load
completion, independently observed renderer token, natural process-exit code,
or verified descendant cleanup. Those observations do not determine why the
GPU process failed. A new run must report its own evidence independently.

A subsequent x64-emulated local run of the instrumented client retained the
bootstrap process handle and observed process exit code 15 (supervisor exit 1;
no timeout termination). The client reported initialization, browser creation,
renderer launch requests, `RENDERER_TERMINATED status=2 code=10`, a close
request, `DoClose`, `OnBeforeClose`, and `CefShutdown` returning. It did not
report fixture completion. Code 10 coincides with the test client's opt-in
gate, but that coincidence does not establish the renderer's cause of exit.
The child-launch diagnostics reported neither forbidden switch; those
diagnostics do not establish enforcement. The bounded JSONL record is at
`%TEMP%\aegis-p309h-fixture-run-785206212db143a4871b7b48e3e5957e\supervisor-output.jsonl`.
No renderer token or complete child-process accounting was obtained.

The research browser now forwards an explicit, spoofable test-intent marker
to recognized CEF child roles. The browser itself still requires the original
environment opt-in, and every child still requires sandbox information and
rejects the forbidden sandbox switches. This is not an authentication mechanism
or evidence of sandbox enforcement. After this change, a separate local
x64-emulated run reported initialization, browser creation, fixture load start
and completion, close request, `DoClose`, `OnBeforeClose`, and `CefShutdown`
return. The supervisor retained the parent handle and observed native exit 0,
without forced termination. The record is at
`%TEMP%\aegis-p309h-child-run-cec5dfdae52e4f1092aa0f6d8893e299\supervisor-output.jsonl`.
The earlier code 10 followed by this success supports an opt-in propagation
hypothesis but does not prove that the renderer's environment was the cause.
One local run is not hosted Windows x64 verification or a guarantee of
repeatable shutdown; child-process accounting and renderer tokens were not
observed.

## Acceptance and boundaries

The new archive and lineage tests are synthetic. Native compilation, a green
diagnostic workflow, or a client-reported lifecycle sequence cannot by itself
establish a renderer sandbox or the popup owner-release contract. The
diagnostic workflow explicitly retains `SANDBOX_ENFORCEMENT: INCONCLUSIVE`,
`SUBPROCESS_CLEANUP: INCONCLUSIVE` and `OWNER_RELEASE_CONTRACT: UNSATISFIED`.
Popup terminal delivery and production binding readiness remain unverified.
Production CefSharp binaries, proxy startup enforcement, browser profiles and
host networking are not changed by this research.

The next acceptance step is a hosted x64 execution that completes the offline
fixture and normal native shutdown, followed by independently discovered,
handle-bound live renderer evidence and complete test-owned process
accounting. If those cannot be established, acceptance remains incomplete.
