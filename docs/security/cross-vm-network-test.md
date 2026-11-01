# Cross-VM AppContainer TCP control (research only)

**Status: NOT EXECUTED.** This fixture does not create WFP policy or prove packet enforcement. The previous loopback experiment requested no network capabilities and timed out; that does not identify its cause. This experiment needs two separately provisioned disposable Windows VMs on an explicitly authorized, isolated private virtual network. Ordinary GitHub-hosted CI does not provide that topology. Do not substitute a public endpoint or run this against a real profile, production network or normal workstation.

VM B runs an independent TCP receiver bound to one operator-supplied RFC1918 address and port. It accepts only two random run-specific nonces from the configured VM A source address, records receipt on VM B, and does not forward traffic. VM A's fixed synthetic client accepts only a canonical RFC1918 IPv4 literal, an explicit port and one nonce from its owned control file; no hostname or DNS is used. The AppContainer launcher requests `privateNetworkClientServer` only and verifies the package SID and enabled capability on its suspended process before resume. This capability is broader than one endpoint: isolation depends on the pre-provisioned VM network, not the capability itself. Microsoft describes the capability in terms of private/home/work network access, so confirm the test network's Windows network category as well as the address; do not change the category or firewall automatically. See [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations) and [implementing an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer).

## Prerequisites and controlled run

Use review-approved binaries from the same commit on VM A and this repository's receiver script on VM B. Pre-provision the isolated virtual network and a specific listener port, ensuring no routes to public, corporate or unrelated networks. Independently check any required firewall rules before testing; the scripts never create exceptions. Use an existing, unlinked and empty operator-owned parent directory on VM A, and an existing unlinked parent for a **new** evidence directory on VM B. Both VMs must be disposable. Set `AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND` in each process environment and pass `-AcknowledgeDisposableIsolatedNetwork`; these are acknowledgements, not proof of network isolation.

Choose and securely transfer three distinct fresh GUIDs (`RunId`, `NormalNonce`, `ContainedNonce`) and the authorized private IPv4 addresses and port between VMs through a trusted operator channel. The following examples show parameter relationships; replace every placeholder with the verified test-network values. Never use real account or browsing data.

On VM B, start the receiver first:

```powershell
./tests/acceptance/receive-private-network-control.ps1 -BindAddress <VM-B-private-IPv4> -ExpectedClientAddress <VM-A-private-IPv4> -Port <port> -NormalNonce <normal-guid> -ContainedNonce <contained-guid> -EvidenceDirectory <new-VM-B-evidence-directory> -AcknowledgeDisposableIsolatedNetwork
```

On VM A, execute the ordinary control with the exact same addresses, port and GUIDs:

```powershell
./tests/acceptance/invoke-private-network-control.ps1 -Stage Ordinary -RunId <run-guid> -NormalNonce <normal-guid> -ContainedNonce <contained-guid> -ReceiverAddress <VM-B-private-IPv4> -ClientAddress <VM-A-private-IPv4> -Port <port> -ParentDirectory <VM-A-owned-parent> -BrowserTestsExe <published-BrowserTests-exe> -AcknowledgeDisposableIsolatedNetwork
```

The sender saying `ORDINARY_SENT` is insufficient. Wait for VM B's `ordinary-observation.json` and independently inspect its nonce, source/destination, timestamp and one-time receipt in the receiver's bounded observation window. Transfer that file to VM A over the trusted operator channel; it is not cryptographically authenticated. If ordinary receipt is missing or ambiguous, stop: the environment is unavailable or inconclusive. The AppContainer stage refuses to proceed without a matching ordinary observation.

```powershell
./tests/acceptance/invoke-private-network-control.ps1 -Stage AppContainer -RunId <run-guid> -NormalNonce <normal-guid> -ContainedNonce <contained-guid> -ReceiverAddress <VM-B-private-IPv4> -ClientAddress <VM-A-private-IPv4> -Port <port> -ParentDirectory <VM-A-owned-parent> -OrdinaryObservation <trusted-copy-of-ordinary-observation.json> -AcknowledgeDisposableIsolatedNetwork
```

For a positive result, require both a successfully verified suspended token and a successful client exit on VM A, plus one matching `appcontainer-observation.json` on VM B. The receiver delays each record until a two-second duplicate-check window has elapsed; it detects repeats received during that bounded window and while the other phase remains active. This cannot prove that no duplicate arrives after the listener stops. An absent or late observation is **inconclusive**, not a WFP block. The JSON records endpoint and transaction metadata only. Nonce correlation does not provide independent OS process-to-flow attribution; a real enforcement experiment also needs supported policy-decision instrumentation and unaffected-identity controls. This fixture does not implement those requirements or alter WFP state.

The receiver exits after two valid distinct messages or its bounded timeout; an unexpected, malformed, duplicate or out-of-order message is a failure. It never removes files. The client writes only to a new run-specific synthetic root and terminates only its retained synthetic process on timeout. Preserve the roots and generated identity until the disposable VMs are destroyed; never recursively clean unknown paths or change host firewall settings. Receiver JSON is manually transferable and must not be trusted from an untrusted source. A local user able to tamper with the fixture or its evidence is outside its research trust boundary.

IPv6, UDP, DNS, WebRTC, actual CEF egress, service crashes and WFP packet decisions are not tested here. Production required-proxy startup remains blocked. After a genuine positive control, separately review a narrowly scoped, attributed WFP test and a dynamic-session failure experiment; a browser watchdog alone cannot prove the absence of a gap after enforcement disappears.
