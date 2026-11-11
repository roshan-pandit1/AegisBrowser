# Experimental OffscreenCanvas WebGL settings patch

**Source-only prototype; not compiled, not integrated, not production-ready.**
Do not apply to a production CefSharp installation. The single patch is based
on Chromium commit `b859317bf11f6be47f9b7799ec690a0a42a1fb33`, corresponding
to the pinned CEF 154.0.34 line; see the parent research report for the revision
derivation. `source-manifest.json` records exact input blobs and hashes.

## Candidate behavior and trust boundary

The patch adds an **off-by-default** Blink runtime feature,
`AegisOffscreenWebGLSettings`. The feature only activates this prototype;
it is not itself a per-profile policy or a web-facing API. With it off, the
existing OffscreenCanvas WebGL host decisions remain unchanged. With it on:

* A document-created or received OffscreenCanvas consults its receiving
  LocalDOMWindow frame's WebGL1/2 settings.
* A dedicated worker consults WebGL1/2 values captured in the existing
  WorkerSettings when the worker starts. Nested dedicated workers copy them.
  A missing frame, missing dedicated-worker settings, or an uninitialized
  snapshot denies creation in those cases while the feature is enabled.
* Shared workers, service workers, worklets and other execution-context types
  retain upstream behavior. They are not covered by this prototype.

The feature's activation is renderer-wide; actual WebGL settings in this
prototype are per frame or worker snapshot. AegisBrowser's usual one-profile-
per-process layout is not proof of complete renderer isolation. A worker's
snapshot is fixed at creation; live updates need separate design and testing.

An OffscreenCanvas transfer is evaluated against the **receiving** execution
context; this prototype does not carry an originator's restrictive policy
across a transfer into a more permissive context. Same-policy transfer has a
proposed test, but cross-policy transfer, simultaneous differently configured
contexts, renderer reuse, OOPIFs, shared/service-worker ownership and popup
lifetime all require independent tests. Do not describe this candidate as a
complete isolation boundary.

When the feature is on, the candidate reuses CEF-derived WebGL1 and WebGL2
settings separately; it does not change Canvas 2D or WebGPU code. The proposed
web test exercises positive controls, document and dedicated/nested worker
blocking, same-policy transfer and Canvas 2D. Its use of
`internals.settings.setWebGL1Enabled` / `setWebGL2Enabled` follows the
pinned `settings.json5` names and generated-IDL template, but generated API
availability and test execution have **not** been verified by a build.

## Source-subset and patch verification

In an ignored disposable scratch directory, five files were fetched from the
exact Chromium revision and checked against their GitHub Git-blob IDs. A
separate empty Git repository rooted at the snapshot was used to avoid Git's
subdirectory path filtering. The following checks actually returned exit 0:

```powershell
git apply --check --whitespace=error-all <absolute-path-to-patch>
git apply --stat <absolute-path-to-patch>
```

The stat listed six paths: five upstream files plus the new proposed web test.
The subset is not an engine checkout. No compiler, generated Blink bindings,
GN target, browser binary or CEF artifact was tested.

The included `verify_source_subset.py <source-root>` (Python 3.11 or newer) checks the manifest's
SHA-256 values, requires the source root to be the Git repository top level,
checks the patch with `git apply --check --whitespace=error-all`,
and never applies it. It reports only subset applicability, not engine build or
enforcement. Unified-diff blank context lines are intentionally represented by
a single space; the local `.gitattributes` preserves patch LF bytes and excludes
those patch-payload lines from repository whitespace checks. The independent
`git apply` whitespace check still validates actual upstream additions.
The companion negative test verifies that modified inputs and nested roots are
rejected; it does not run Blink or CEF.

For an independent trusted checkout, first verify the exact revision and the
input hashes in the manifest, then use `git apply --check` from that checkout's
root. Do not run these commands against an existing production or user checkout.

```powershell
$src = '<disposable Chromium src directory>'
$patch = '<absolute path to 0001-prototype-offscreen-context-settings.patch>'
$manifest = Get-Content '<absolute path to source-manifest.json>' -Raw | ConvertFrom-Json
git -C $src rev-parse HEAD
foreach ($item in $manifest.sources) {
  $actual = (Get-FileHash -LiteralPath (Join-Path $src $item.path) -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $item.sha256) { throw "Source hash mismatch: $($item.path)" }
}
if ((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.patch_sha256) { throw 'Patch hash mismatch' }
git -C $src apply --check --whitespace=error-all $patch
```

## Proposed isolated build and test

Use the [pinned Chromium Windows build instructions](https://github.com/chromium/chromium/blob/b859317bf11f6be47f9b7799ec690a0a42a1fb33/docs/windows_build_instructions.md)
and a supported Windows x64 build environment. Obtain the exact revision and
its DEPS via official depot_tools, verify `git rev-parse HEAD` and ensure
`gclient sync` used that revision before applying the patch. Install no
experimental artifacts into AegisBrowser. The commands below are a proposed
research sequence, **not executed**:

```powershell
git -C $src apply --check --whitespace=error-all $patch
git -C $src apply $patch
Set-Location $src
gn gen out/AegisWebGLResearch --args='is_debug=false is_component_build=false target_cpu="x64"'
autoninja -C out/AegisWebGLResearch content_shell
python3 third_party/blink/tools/run_web_tests.py -t AegisWebGLResearch fast/canvas/aegis-offscreen-webgl-settings.html
```

Confirm the generated setter names, feature registration, build output and
test runner configuration on the exact checkout. Positive WebGL1 and WebGL2
controls must actually render; unavailable GPU support is not a successful
restriction. Repeat in independent processes and differently configured browser
contexts, and specifically test cross-policy transfer and worker types before
considering any CEF binding. CEF/CefSharp compatibility, process isolation,
sandboxed runtime behavior and binary provenance remain unverified.
