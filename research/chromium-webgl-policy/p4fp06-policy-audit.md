# P4-FP-06: policy ownership and transfer audit

This is a source audit of the **uncompiled** P4-FP-05 candidate, not a tested
engine policy. The manifest pins Chromium
`b859317bf11f6be47f9b7799ec690a0a42a1fb33` and CEF
`14c5a089e8452874cb1a556dc3fde26ed2d87723`. Its five-file source subset
and patch applicability are separate evidence from a complete checkout. No
patched executable, Blink test, CEF integration, or mixed-policy experiment was
run for this audit.

## Findings requiring a policy decision

* The candidate adds two booleans initialized to `false` to
  `WorkerSettings`. The `Settings*` constructor copies the document values
  when present, and `Copy()` copies the snapshot. The existing alternative
  constructor does not take WebGL values. Therefore an absent or unspecified
  policy and an explicit restriction have the same representation when the
  experiment is enabled. See the pinned [WorkerSettings implementation](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/third_party/blink/renderer/core/workers/worker_settings.cc)
  and the [candidate patch](0001-prototype-offscreen-context-settings.patch).
  The candidate's comments explicitly describe denial for an uninitialized
  snapshot; they do not establish whether that is the correct behavior for
  every constructor and owner. Do not replace `false` with `true`: either
  default could silently assign the wrong policy. An explicit ownership and
  unknown-state contract needs review before changing this behavior.
* The pinned [dedicated worker creation path](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/third_party/blink/renderer/core/workers/dedicated_worker.cc)
  snapshots a document's settings or copies a parent `WorkerGlobalScope`'s
  settings. The candidate's worker gate only handles a
  `DedicatedWorkerGlobalScope`; other context types retain the upstream
  permissive host behavior. The possible construction paths, reachability and
  owners of alternative settings need to be traced and tested before claiming
  nested-worker isolation or coverage of shared/service workers.
* The pinned [OffscreenCanvas deserializer](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/third_party/blink/renderer/bindings/core/v8/serialization/v8_script_value_deserializer.cc#L748-L765)
  reconstructs the canvas using the receiving script state. The candidate
  consults the receiving context and adds no originating restriction to the
  transfer representation. A restrictive receiver is intended to deny new
  contexts; a restrictive sender's protection is not preserved by any new
  transfer metadata in this patch. Those are source-level conclusions, not
  observed runtime results. Whether and how sender restrictions should survive
  transfer remains a design and compatibility question.
* The proposed Blink test covers enabled and disabled snapshots, nested
  workers and a transfer where sender and receiver share one setting. It does
  **not** exercise alternative constructors, absent policy, independently
  configured owners, policy changes across worker lifetimes, or mixed-policy
  transfers. The proposed generated setting setters, test discovery, GPU
  positive controls and runtime effects have not been verified by a build.

## Proposed executable tests — NOT_RUN

The exact test targets and harness must be discovered on the pinned full
checkout. Test cases below are requirements, not assertions that a suitable
test harness or executable is currently available. Each WebGL 1 and 2 case
needs a rendering-positive control in the same environment; missing GPU/API
support is inconclusive. Record the feature state, both policy states, context
types, creation and rendering results, process exit code, and patch hash.

| Case | Required evidence |
| --- | --- |
| Feature disabled | Stock document and dedicated-worker behavior is unchanged, including absent settings and both alternative-constructor paths where reachable. |
| Settings snapshot | Independently exercise enabled, denied, absent and alternative-constructor ownership; verify the reviewed unknown-state rule, rather than treating the current boolean default as an explicit policy. |
| Nested workers | Parent and nested dedicated workers preserve an explicitly assigned snapshot for WebGL 1 and 2; lifecycle and restart do not reuse stale owners. |
| Transfer, allow → allow | Both endpoints have independently established unrestricted policies; context creation and rendering succeed after transfer. |
| Transfer, deny → deny and allow → deny | Restricted receiver rejects new WebGL 1 and 2 contexts; Canvas 2D remains functional. |
| Transfer, deny → allow | Measure separately against the approved sender/receiver contract; do not declare success merely because receiver policy permits creation. Include transferred canvases after sender destruction. |
| Existing context and transfer | Check supported transfer semantics for initialized canvases; report a standards-required rejection as such, not as WebGL policy enforcement. |
| Owner isolation | Run simultaneous distinct-policy contexts, worker recreation and independent profiles/processes; prove the policy identity used for each receiver. |
| Other worker types | Determine actual API availability, multi-client or background ownership, and behavior for shared/service workers. Unavailable APIs are not proof of policy enforcement. |
| 2D and WebGPU | Verify 2D drawing/readback and independently observe WebGPU under both policy states; do not infer either outcome from WebGL. |

Do not wire the prototype into production or claim a security boundary until
the ownership and transfer contracts are reviewed and actual patched-engine
tests pass. A future CEF/CefSharp build and its policy transport require their
own evidence.

## Incremental unknown-state candidate — NOT COMPILED

The separate [0002 candidate](0002-prototype-worker-settings-presence.patch)
must follow the unchanged 0001 patch. It adds an explicit known bit, set when
both values are copied from non-null document settings and copied into nested
snapshots. Null and alternative-constructor snapshots remain unknown. The
dedicated-worker gate checks the bit and, as in 0001, denies an unknown
snapshot when the experimental feature is on. With the feature off, the
candidate still returns the stock behavior. This is the intended source-level
equivalence, not an observed runtime result. The candidate does not determine
policy ownership or fix transfers, shared/service workers, dynamic changes,
isolation, or CEF activation.

The 0002 SHA-256 is
`70cccc5506d1ffb95f183240dd090a61ca8f102ee78d44b2d753f6445f8a59d7`.
`verify_source_subset.py` passed against the independently retained original
five-file subset (exit 0). In a fresh isolated copy of that subset, these
commands then each returned exit 0, with `0001` actually applied only to the
disposable copy:

```text
git apply --check --whitespace=error-all <absolute-0001-patch>
git apply --whitespace=error-all <absolute-0001-patch>
git apply --check --whitespace=error-all <absolute-0002-patch>
```

No focused C++ test is included: the exact owning GN target and registration
have not been established from a complete pinned checkout. Adding an
unregistered test would not demonstrate coverage. Test implementation,
registration, discovery, compilation, and execution all remain **NOT_RUN**.
Before relying on this change, implement the cases above in the discovered
upstream target, verify default-off and missing-settings behavior, and run them
on a qualified pinned builder.
