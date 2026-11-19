# Disposable CEF source-build preparation

This directory prepares a **research-only** Windows x64 source-build attempt.
The scripts have not been exercised against a complete Chromium checkout or a
qualified builder. They do not implement renderer instrumentation or prove
sandbox enforcement. A source build, native launch and all security gates remain
`NOT_RUN`; production readiness remains `NO_GO`.

## Pins and builder

| Component | Intended revision |
| --- | --- |
| CEF | `14c5a089e8452874cb1a556dc3fde26ed2d87723` (branch `8037`) |
| Chromium | `b859317bf11f6be47f9b7799ec690a0a42a1fb33` (tag `154.0.8037.98`) |
| CefSharp reference | `154.0.340`, `ec1027aa21e1af1217681a349104c15d29bbc21a` |
| depot_tools | A full, independently reviewed commit supplied by the operator; the pinned CEF metadata does not pin one. |

CEF's pinned [compatibility metadata](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/CHROMIUM_BUILD_COMPATIBILITY.txt)
specifies the Chromium tag. Follow the matching
[CEF automated build instructions](https://github.com/chromiumembedded/cef/blob/14c5a089e8452874cb1a556dc3fde26ed2d87723/docs/automated_build_setup.md)
and [Chromium Windows instructions](https://chromium.googlesource.com/chromium/src/+/b859317bf11f6be47f9b7799ec690a0a42a1fb33/docs/windows_build_instructions.md).
The scripts do not prove that a selected depot_tools commit is compatible.
Document its selection and the obtained dependency revisions for each attempt.

Use an **explicitly authorized, disposable, native Windows x64 builder** with a
dedicated local NTFS workspace, Visual Studio 2026 x64 C++ tools and ATL/MFC,
the pinned instructions' serviced Windows SDK and debugger, Git, and Python 3.
The checks require at least 8 GiB RAM and 100 GiB free NTFS space; these are
minimum checks, not a sizing guarantee. The intended builder has at least 16
logical CPUs, 64 GiB RAM and 500 GiB free SSD space. Baseline and instrumented
trees require additional space if kept simultaneously. Long-path settings are
reported as advisory; obtain host-owner approval before changing them. These
scripts do not install Visual Studio, configure system security, provision a VM,
or grant cloud spending authorization.

Keep the machine isolated from real browser profiles. Do not put credentials in
the workspace or environment. An operator must review logs and dependency
snapshots for sensitive URLs, query strings and local information before sharing.
Use no unattended untrusted pull-request jobs on a credential-bearing self-hosted
runner; runner-group and environment restrictions must be configured by its owner.

### Example provisioning estimate (not an authorization)

One illustrative option is an AWS Windows `m7i.4xlarge` in US East (N. Virginia):
16 vCPUs, 64 GiB RAM, and a separately provisioned 500 GB gp3 SSD. A third-party
[price listing](https://instances.usage.ai/aws/ec2/m7i.4xlarge/windows) reports
about $1.542 per compute hour; [AWS](https://aws.amazon.com/ebs/volume-types/)
lists gp3 storage at $0.08 per GB-month in its example. At those illustrative
rates, 120 continuous compute hours cost about $185.04 and 500 GB for five days
costs about $6.67, totaling about $191.71 before tax, public IP, data transfer,
snapshots, extra throughput, licensing and other account-specific charges.
Storage can continue accruing charges after compute stops. Actual prices,
capacity, Windows/Visual Studio licensing, provider account and approval limits
must be verified by the owner before provisioning. No infrastructure was created
or billed by these scripts.

## Procedure on an approved builder

Run the commands from this repository in an x64 PowerShell session. The example
paths are disposable, local and space-free. The workspace must already exist;
`baseline` and the later evidence directory must not exist. The acquisition
script also rejects reparse points and paths with unsupported characters. Select
and record the exact depot_tools revision after reviewing the official source.

```powershell
$workspace = 'C:\cefbuild'
$depotRevision = '<reviewed-40-character-lowercase-commit>'
New-Item -ItemType Directory -Path $workspace
.\scripts\chromium-build\preflight.ps1 -BuildRoot $workspace -Stage Host
```

Stop if the JSON does not report `qualified: true` or the exit code is nonzero.
Unknown required observations fail closed. A host result does not prove that
the checkout, compiler, renderer or sandbox works.

The next commands perform network downloads and write to the disposable
workspace only when the operator explicitly supplies `--execute`:

```powershell
python3 .\scripts\chromium-build\acquire_source.py tools --workspace $workspace --depot-tools-commit $depotRevision --execute
.\scripts\chromium-build\preflight.ps1 -BuildRoot $workspace -Stage Toolchain -DepotToolsPath "$workspace\depot_tools" -ExpectedDepotToolsRevision $depotRevision
python3 .\scripts\chromium-build\acquire_source.py checkout --workspace $workspace --download-root "$workspace\baseline" --depot-tools-commit $depotRevision --execute
python3 .\scripts\chromium-build\acquire_source.py verify --workspace $workspace --download-root "$workspace\baseline" --depot-tools-commit $depotRevision
```

`tools` verifies existing matching clones instead of updating them. Source sync
uses the pinned upstream CEF automator without compiling or creating a
distribution; it disables automatic depot_tools updates. Network probes retry
at most three times, and a clone has a 30-minute deadline. A partial clone or sync is **not** retried, reset or
deleted automatically. Preserve and investigate a failure and use a separately
prepared empty root for another attempt. `verify` writes a new
`dependency-revisions.json`; it refuses to overwrite an existing snapshot.
Its structured output records source revisions and the Chromium diff hash, but
does not independently establish that the diff is the expected CEF patch set.

### Baseline build gate and current limitation

The wrapper below will fail closed if its stricter preflight cannot establish
source provenance. In particular, upstream CEF may legitimately patch Chromium,
but these scripts do not yet independently verify that patch set. Thus an
ordinary synchronized checkout can be blocked even on a qualified builder.
Do not bypass the gate, discard patches or call the checkout an unmodified
baseline. Implement and independently review patch-provenance verification
before attempting to use this automation on that state.

If all preflight checks genuinely pass, an authorized operator can attempt the
baseline build, with a new evidence path outside both checkout and depot_tools:

```powershell
New-Item -ItemType Directory -Path 'C:\cef-evidence' -ErrorAction SilentlyContinue
.\scripts\chromium-build\build-baseline.ps1 -DownloadRoot "$workspace\baseline" -DepotToolsPath "$workspace\depot_tools" -ExpectedDepotToolsRevision $depotRevision -EvidenceDirectory 'C:\cef-evidence\baseline-001' -AcknowledgeSourceBuild
```

The wrapper builds the pinned, uninstrumented target and records timestamps,
configuration, exit status and output hashes when available. Even a successful
compiler exit is labeled `COMPILED_SOURCE_PROVENANCE_INCONCLUSIVE`; inspect the
manifest and logs. It does **not** launch CEF or establish browser, renderer,
lockdown or cleanup behavior. There is no instrumented-build or security-test
implementation in this package. Do not publish output as a production runtime.
The bounded `watch-space.ps1` monitor can be run in a separate operator session:

```powershell
.\scripts\chromium-build\watch-space.ps1 -BuildRoot $workspace -MinimumFreeGiB 100 -IntervalSeconds 60 -Samples 120
```

It reports available space and exits nonzero below the threshold; it does not
stop the build. The operator must respond and manage the build process safely.
This monitor has not been exercised on a qualified builder.

## Manually dispatched workflow

`.github/workflows/p0-cef-source-build.yml` is restricted to the experimental
branch, a dedicated self-hosted Windows x64 runner label and an environment.
It does not acquire source, run instrumentation or publish evidence. Before any
use, an administrator must restrict the runner group, create the named GitHub
environment with required reviewers and restricted deployment branches, and set
the indicated environment variables. Naming an environment in YAML alone does
not provide these protections. GitHub requires a workflow-dispatch file to exist
on the default branch before it can be dispatched; this experimental branch does
not install it there. No such workflow has run for this work.

## Local checks

These checks exercise script logic only; they do not require a source checkout
and do not qualify the current machine as a builder:

```powershell
.\scripts\chromium-build\test-preflight.ps1
.\scripts\chromium-build\test-build-baseline.ps1
python3 -B -m unittest discover -s scripts/chromium-build -p test_acquire_source.py -v
```

Keep failed logs and manifests in a restricted disposable evidence location.
Review them before sharing; never upload private profiles, credentials, test
certificates or unreviewed network traces. No cleanup command in this package
deletes the workspace; follow the machine owner's disposal procedure after
preserving approved evidence.
