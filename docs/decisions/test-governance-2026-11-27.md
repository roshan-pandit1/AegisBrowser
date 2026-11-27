# Provisional test-governance decision

Date: 2026-11-27. Starting source:
`304259f0687eb9a304d00938003ee2686567633b`.

**PARTIAL [SOURCE-VERIFIED] [STATIC-ANALYSIS] [UNVERIFIED]:** The three
existing .NET test entry points now call a shared, fixed-allowlist decision
layer before their explicit legacy test or browser setup. Missing, unknown and
mismatched declarations are refused without echoing the input. Existing test
bodies and assertions remain in source; the legacy execution path is
unconditionally denied until a reviewed authorization verifier and
per-operation classifications exist.

`--governance-dry-run` reports a fixed plan for a known profile and test
identifier without starting that test. The only provisional Profile A run
selector is the in-memory governance self-test. Profile B and C requests
remain blocked: no trusted human authorization verifier, trust anchor, machine
binding or artifact has been configured. An environment variable, file
existence, runner label, preflight result or agent-written document cannot
authorize them.

The new code has **not** been compiled or executed. The self-test and actual
refusal behavior are **NOT_RUN [NOT_RUN]**: generated SDK/NuGet imports,
runtime and package initialization, and all build/cache/temp writes have not
been fully classified. Next: inspect the complete resolved build graph and
prove its writes remain in the designated workspace, then run the fixed
in-memory selector under a human-approved development-safe configuration.
Do not use another language or interpreter to bypass a blocked build or
execution restriction.

Source inspection found no explicit module initializer, assembly-resolution
hook or static `Program` constructor in the inspected application and test
sources, and no explicit side effect before the proposed guard. This does not
establish behavior of compiler-generated code, .NET runtime/JIT, or referenced
native and managed dependencies. [STATIC-ANALYSIS] [UNVERIFIED]

Standalone PowerShell, Python, native binaries and direct filesystem or OS
operations are **not** intercepted by these .NET guards. A catalog, wrapper or
workflow alone cannot make a directly invoked legacy script safe.
**BLOCKED [AUTHORIZATION-BLOCKED]:** direct entry points need their own
reviewed fail-closed enforcement, and human authorization must be verifiable
against a human-provided trust anchor and bound to machine, scope and expiry.
Next: review each direct entry point and establish the authorization-verification
mechanism before enabling Profiles B or C.

The historical baseline observations remain
**[NONCOMPLIANT-EVIDENCE]**; they do not demonstrate compliance of this layer.
G0 stays **PARTIAL** until actual refusal, complete entry-point coverage,
authorized environments, sanitized output and execution records are
demonstrated. Release A and Release B remain NO_GO.
# Inventory portability limitation

## Inventory-bound decision test

The fixed Profile A decision-test path now reads the inventory from the current
workspace and compares SHA-256 identities for its six fixed C# source inputs.
The operation, profile and dependency closure are fixed in code; inventory
entries cannot grant Profiles B or C. Duplicate JSON keys, paths and operation
IDs, incomplete selected records, stale identities and an expanded closure fail
the decision. The Python checker remains separately implemented and deliberately
refuses overall success until operation-validation parity is implemented and
tested. A mutable same-user workspace is not an independent trust boundary.

Two local compiler invocations with identical inputs and deterministic mode
produced the same test-assembly SHA-256:
`E3A7E11824A5A633377C526F6A1E84D148CB9A0745E896D63AED90A975675123`.
One run of that assembly reported 21 decision and 44 in-memory inventory
assertions passed. A lightweight file-backed probe accepted a complete fixture
and refused stale, missing, malformed, blank-field and expanded-scope fixtures.
The runner checks the decision before running its normal test path. These are not executions
of the three full test executables. The executing assembly is not bound to the
current source hashes: an older build may accept a newly updated source tree and
inventory. Only leaf reparse points are checked; ancestor substitution, mapped
drives and races remain unverified. Runtime dependency closure and independent
authorization are not established. [EXECUTED-TEST] [UNVERIFIED]

To reproduce locally after separately reviewing the inputs and environment,
use the signed `C:\Program Files\dotnet\dotnet.exe` host and SDK 10.0.404
`Roslyn\bincore\csc.dll`, and the 10.0.15 `Microsoft.NETCore.App.Ref`
reference pack. Run from the workspace root with a new ordinary output
directory under `artifacts/governance-inventory-run`. Set `DOTNET_CLI_HOME`,
`TEMP` and `TMP` to that directory and opt out of CLI telemetry in the child
environment. Refuse unexpected CLR hook or profiler configuration. Compile
with `dotnet exec <csc.dll> -noconfig -nostdlib+ -target:exe -debug-
-nullable:enable -deterministic+ -out:<output>/InventoryDecisionTests.dll`,
passing explicit `-r:` paths to `System.Runtime.dll`, `System.Console.dll`,
`System.Collections.dll`, `System.Text.Json.dll`,
`System.Security.Cryptography.dll`, `System.Memory.dll`,
`System.Text.Encoding.Extensions.dll`, `System.IO.FileSystem.dll` and
`System.Linq.dll` from the reference pack. The six inputs are the paths listed
in `InventoryBinding.DecisionSources`. Create a sibling runtimeconfig selecting
`Microsoft.NETCore.App` version `10.0.15`, then use `dotnet exec` on the test
assembly from the workspace root. Expected compiler and test exit codes are zero;
the test reports 21 and 44 assertions. Hash the assembly after each independent
build. Do not restore packages, use `/shared`, or execute browser fixtures.
The earlier hashes describe two builds on one machine, not independent-host or
full browser reproducibility.

Executable sources and binaries use raw byte hashes. The optional JSON-only
`json-lf-sha256-v1` helper accepts LF/CRLF equivalence after strict UTF-8 JSON
validation, rejects a BOM, bare CR and duplicate keys, and was exercised by the
in-memory checks. It is not currently used to normalize executable sources or
to bind the actual inventory file. Thus source checkout line-ending changes can
still fail closed and the overall line-ending issue is not resolved.

## Executable decision-test follow-up

On this workspace, the installed signed .NET host and SDK 10.0.404 Roslyn
compiler compiled the actual `TestGovernance.cs` and `TestGovernanceTests.cs`
with the small `tests/governance/PureDecisionRunner.cs` entry point. The direct
compiler invocation used `-noconfig -nostdlib+ -target:exe -debug-
-nullable:enable`, no `/shared`, three explicit installed `10.0.15` reference
assemblies (`System.Runtime`, `System.Console`, `System.Collections`), and a
workspace-local output. It did not invoke MSBuild or restore dependencies.
Process-scoped CLI, temporary and home paths were set inside the workspace;
telemetry was opted out. Unexpected CLR hook/profiler environment variables
were checked before the final compilation and test. The created runtimeconfig
selects the installed `Microsoft.NETCore.App` 10.0.15 runtime.

`dotnet exec` of the output exited zero and the existing self-test reported
**18 in-memory decision assertions passed**. The first compilation failed
with CS2005 due to malformed argument construction; a corrected build passed
with nullable-context warnings, and the final build enabled the project's
nullable context and reported no compiler diagnostic codes. The runtime may
use local diagnostic IPC and broader OS effects were not independently traced.
No PowerShell script was executed or execution policy changed. [SOURCE-VERIFIED]
[DEVELOPMENT-SAFE] [EXECUTED-TEST]

The tested output SHA-256 was
`220F45772A685188CCAA1E5BD8F8A5BC250E13CD25F858CFD0BE0A454C3A07DD`.
The input SHA-256 values were `431A8B2E08BEE955588DE721DED9BCE095EA7BDAE6770CC50D11C15431F395AE`
for the decision source, `733D9C83052067EDFE0AB98EAF0DBA6F66E5DF03436E47F0C7D513565655CC7F`
for the tests, and `3EC4093CA6871768EB387F794B11FCD294FFAB4DFA142ED1F0A2E1A577118408`
for the entry point. These checks do not execute the browser or full test
entry points, do not inspect inventory or human-authorization artifacts, and
do not prove operating-system enforcement. Malformed, missing and stale
inventory refusal remains **NOT_RUN** because the tested C# function has no
inventory input. [NOT_RUN]

Two formerly UNKNOWN source records were classified for this exact standalone
operation, and the new runner was added: 214 records total, 177 with UNKNOWN
category, and 198 with at least one UNKNOWN required field. The direct-script
gap and Profile B/C hard denial remain. Separately, the current in-workspace
backup contains source-like files under `artifacts`; the checker has not been
shown to exclude that ignored directory, so full discovery consistency remains
unverified and may fail closed. A safe exclusion needs review and executed tests.

**PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]:** The current inventory uses raw
working-tree SHA-256 values. Git reported that it may convert line endings on a
subsequent checkout, so a different checkout can fail closed even when the
canonical Git content is unchanged. This is not a successful cross-platform
inventory check. Next: define and review a canonical byte representation or
explicit line-ending policy, then test both checkout representations without
weakening changed-file detection. The checker and its tests have not been run.

## Controlled recovery follow-up

The count-only output policy in `tests/governance/output_safety.js` accepts
untrusted strings but returns only numeric counts. It never returns input text;
callers must still avoid separately printing raw data. Its keyword heuristics
are not a complete detector and do not govern other tools. The corresponding
in-memory JavaScript test was executed in the tool's isolated V8 evaluator:
10 cases passed, zero failed. Two initial harness invocations stopped with a
setup TypeError before executing the cases; the corrected invocation ran all
10. The evaluator read the two source files but did not run PowerShell script
files, start child processes, access the network, or write files as part of the
test. [DEVELOPMENT-SAFE] [EXECUTED-TEST]

Inventory entries rose from 209 to 213 because four new source/workflow files
were added. Records whose category is UNKNOWN fell from 181 to 179 after source
classification of the pure archive policy and its test; they remain blocked
on this host because the effective PowerShell execution policy is Restricted.
Records with at least one UNKNOWN required field remain 200, including new
hard-denied workflow placeholders. Direct invocation of other scripts still
has no complete governance guard. The checker and its refusal tests have not
been executed. [STATIC-ANALYSIS] [NOT_RUN]

The seven legacy workflow jobs are locally hard-denied and paired with a
visible failing authorization job; their test bodies are retained. Two manual
isolated-environment templates also remain hard-denied. Marker checks covered
seven legacy files and two templates, but a YAML/workflow validator and GitHub
execution were not run. Runner labels and named environments do not prove
isolation, reviewer protection or authorization. There is no operational
development-safe CI workflow yet. [STATIC-ANALYSIS] [NOT_RUN]
