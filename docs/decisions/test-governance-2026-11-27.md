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

**PARTIAL [STATIC-ANALYSIS] [UNVERIFIED]:** The current inventory uses raw
working-tree SHA-256 values. Git reported that it may convert line endings on a
subsequent checkout, so a different checkout can fail closed even when the
canonical Git content is unchanged. This is not a successful cross-platform
inventory check. Next: define and review a canonical byte representation or
explicit line-ending policy, then test both checkout representations without
weakening changed-file detection. The checker and its tests have not been run.

