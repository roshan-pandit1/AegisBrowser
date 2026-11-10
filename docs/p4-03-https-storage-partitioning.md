# P4-03: real HTTPS and cross-site storage

This research starts at PR #59 commit
`9934b89a04fac4fa0346b70e3028b08bf11814b9`. The pinned runtime is
CefSharp 154.0.340, CEF `154.0.34+g14c5a08+chromium-154.0.8037.98`,
Windows x64 and .NET 8. Production package references, TLS validation, sandbox
settings and required-proxy startup checks are unchanged.

## Fixture and trust boundary

The test server binds only IPv4 and IPv6 loopback sockets and uses real TLS.
Its certificate contains SANs for `localhost`, `127.0.0.1` and `::1`.
The sites are distinct by host, not just port. The test server records only
booleans for fixed synthetic cookie markers; it does not retain cookie headers.

The process generates a short-lived CA and server keys for the fixture. Windows
Schannel imports temporary user-key containers for the test server; no key is
added to a certificate trust store. The normal
local preflight uses an explicitly scoped .NET custom-root chain and confirms
that the matching hostname succeeds while an untrusted root and a wrong
hostname fail. It does not install a root in the user's certificate store.
The browser test refuses to install a root unless running on a declared
GitHub-hosted Windows CI runner and the manifest directory is under
`RUNNER_TEMP`. On that disposable runner it adds only the new public CA
certificate to the current test user's Root store before CEF starts. The
official Windows `certutil` user-store command runs in a bounded child
process. On the disposable runner only, the harness attempts to confirm a
Windows trust dialog only when it belongs to that exact child process and its
window class, title, generated CA subject, SHA-1 certificate thumbprint and
Yes button all match. A mismatch is not confirmed. Local synthetic negative
tests cover several mismatches. This automates the explicitly requested
temporary test trust; it does not change Windows policy or browser TLS
validation. If installation stalls, only that test-owned installer is
terminated and CEF has not started. Chromium
uses its ordinary certificate validation and the production request handler;
there is no certificate-error override.

A public-only DER manifest is written before installation. The test removes
and checks the exact root after CEF shutdown. An independent workflow cleanup
step runs after failures, validates the manifest name and certificate, removes
only byte-identical roots from the current-user store, and verifies absence.
No private key or certificate is checked into the repository. Environment
checks are an operational guard, not protection against a hostile runner
administrator.

## Measurements and limits

The write/read processes use the same disposable profile and port. The write
process compares a baseline mode with the production cookie-protection mode;
the read process applies the saved policy before the first browser and checks
the first controlled cross-site iframe request. The HTTPS server observes the
ordinary and HttpOnly synthetic cookies and a third-party response cookie.
The browser also tests first-party cookies and rejects a trusted certificate
with the wrong hostname and a certificate from an untrusted root.

Hosted execution and complete results must be taken from the PR's exact-head
CI checks. A passing local certificate preflight alone does not establish CEF
HTTPS behavior. The first hosted attempts
([run 42804594596](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42804594596)
and [run 42806422459](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42806422459))
timed out before CEF started. Streamed diagnostics on the second run identified
the stall at the managed current-user Root-store Add call; independent cleanup
reported the exact temporary root absent. Windows documents a confirmation
dialog for that operation in [dotnet/runtime #24160](https://github.com/dotnet/runtime/issues/24160).
The dialog itself was not observed on that runner, so it is a plausible cause,
not an established observation. The bounded utility is a subsequent experiment.
On [run 42808253542](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/42808253542),
the bounded utility also timed out after 20 seconds and the cleanup step
verified the test root absent. Exact-dialog confirmation is a subsequent
experiment and its result must be read from later CI.

The fixture also exercises partitioned cookies, IndexedDB, CacheStorage and
service-worker registration under two top-level sites, a Storage Access API
request without user activation, and a synthetic HTTPS popup/redirect flow.
Those cases are not established until real CEF execution reaches them.
Loopback measurements do not prove behavior for every public HTTPS site or
network path.
