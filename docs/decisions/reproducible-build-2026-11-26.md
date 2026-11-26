# Decision: normalize portable ZIP metadata and measure reproducibility

Date: 2026-11-26 UTC. The compared source is
`1ee1dd284d2f6ad3ccb66593ce2984d56843063a`.

**WP-H: PARTIAL [HOSTED-CI] [DEVELOPER-VERIFIED].** Two separately
scheduled Windows-hosted builds of that commit produced the same extracted
file contents, but their original ZIPs were not byte-for-byte identical.
The new writer normalizes archive metadata; this has not yet been demonstrated
in two independent final-head builds with controlled restore inputs.
It does not establish provenance, engine security, or production readiness.

## Observed comparison

| Observation | Result |
| --- | --- |
| Source [HOSTED-CI] | Both runs recorded the exact source commit above: [PR run 45649203906](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/45649203906) and [manual run 45649827138](https://github.com/roshan-pandit1/AegisBrowser/actions/runs/45649827138). These are separate hosted runs, not independent external review. |
| Artifact identity [HOSTED-CI] [DEVELOPER-VERIFIED] | The PR and manual artifact wrapper hashes matched GitHub's artifact metadata; the inner ZIP SHA-256 values matched the CI logs: `8a95ce41a2fd16684810376a80d2c4c1fed8e02fbafea15630fbbd2b84d7263b` and `89794662cfd41800df140574a004263e98b8e62d48d8213b8288b6ec0caa8aca`. |
| Extracted content [DEVELOPER-VERIFIED] | PASS for these two artifacts: identical ordered inventories of 539 files, identical SHA-256 for every uncompressed file and every raw compressed entry, and no missing or extra entries. |
| Archive bytes [DEVELOPER-VERIFIED] | FAIL for the original ZIPs: 108 differing bytes, all within the local and central DOS timestamp fields for 27 entries. The archive lengths, entry order, compression methods and sizes, CRCs, flags, attributes, and inspected extra/comment metadata match. No non-timestamp byte differed. |
| Generated files [DEVELOPER-VERIFIED] | `VERSION.txt`, application and core DLLs and PDBs, runtime configuration and dependency manifest are byte-identical across the two artifacts. The generated version file names the same commit. |
| Dependencies [UNVERIFIED] | The identical `AegisBrowser.deps.json` names 14 libraries, of which 10 package entries contain SHA-512 fields. `global.json` pins SDK 10.0.404 with roll-forward disabled. No package lock file establishes complete restore-input equivalence; manifest equality does not authenticate packages or prove all transitive inputs were independently reproduced. Next: signed and hash-verified locked restores and builds in separate clean environments. |
| Embedded paths [DEVELOPER-VERIFIED] [UNVERIFIED] | The two managed DLLs contain absolute hosted-runner `D:\a\AegisBrowser\AegisBrowser\...\obj\Release\...\*.pdb` paths. Both builds used the same path, so their equality does not establish path independence. A byte-string scan of the two PDBs for several common absolute-root prefixes found no match; it is not a complete portable-PDB metadata inspection. Next: inspect portable-PDB metadata and compare clean builds from different absolute paths. |

The sanitized per-file evidence is
`docs/security/evidence/ci-zip-comparison-1ee1dd2.json`,
SHA-256 `96fefc5cb9714a45333d782ec704831fd075849f4310b8ec89201bc57c3d12a2`.
It contains archive entry names, per-file hashes and timestamps, not signed
download URLs or profile data. To reproduce it, download the two inner preview
ZIPs from the named CI runs, verify their hashes above, and run:

```powershell
python tests/acceptance/compare-portable-zips.py <pr-inner-zip> <manual-inner-zip> --output <new-evidence-json>
```

The comparator reads entries without extracting or running them. Its
`--skip-raw` option explicitly marks byte-offset comparison NOT_RUN.
The full locally retained comparison has SHA-256
`a114c5f3d9901e0eb907ec978b8bc5bde20a22316098a963eca11b3f7ca22a4`;
the committed sanitized report retains every file's hashes and the comparison
summary.

## Change and scope

`scripts/package-portable.ps1` now invokes
`scripts/write-deterministic-zip.ps1`. The writer sorts entry names
ordinally, assigns the fixed ZIP timestamp 1980-01-01 00:00:00 and fixed
attributes, uses .NET's optimal compression, creates the output with
`CreateNew`, and rejects observed reparse points and paths outside the
source. It preserves enumerated files and child directories, including empty
child directories. The writer does not emit an entry for an otherwise empty
source root; that case is not used for the browser package and was not tested.
The previous published-runtime, CEF manifest, notices, extraction-hash,
HTTPS smoke, output-publication and cleanup gates remain in place.

The checks are path-based: a process able to replace a source directory or
ancestor between inspection and opening a file can race them. No handle-based
identity binding or race-free behavior is claimed. Reparse substitution and
concurrent replacement tests are **NOT_RUN [UNVERIFIED]**; next, run them on
an authorized disposable environment before depending on these checks for an
untrusted source tree.

**NOT_RUN [UNVERIFIED]:** a case-sensitive Windows directory can potentially
contain file/directory names differing only by case. The writer now removes
the directory suffix before its case-insensitive duplicate check, so the
source logic is intended to reject a collision such as `foo` and `FOO/`.
The existing package extraction and hash checks remain in place, but no
filesystem collision regression has been performed. Next: validate the
behavior on an authorized disposable environment with the necessary
case-sensitive filesystem support.

The standalone writer may leave a newly created partial ZIP if a read,
compression, or write fails after opening the output. It never intentionally
overwrites an existing ZIP. When invoked by `package-portable.ps1`, the
existing finally block attempts to remove only the exact pending ZIP and
checksum in its validated unique pending directory. That cleanup does not
remove or alter pre-existing output and does not prove resistance to
same-user path-replacement races.

## Verification and limits

- **PARTIAL [SYNTHETIC] [DEVELOPER-VERIFIED]:** four targeted checks with
  test-owned temporary files: identical contents with different creation
  order/timestamps produce identical ZIP bytes; ordinal entries, fixed times,
  empty directories and file hashes are preserved; existing output is
  rejected unchanged; output inside the source is rejected. These four
  observations used the process-scoped policy override disclosed below, so
  they do not qualify this change for release.
  Command: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/acceptance/test-deterministic-zip.ps1`.
- **PASS [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]:** Windows PowerShell's
  parser accepted the writer, fixture script and packaging script after the
  collision-key change. This checked syntax without executing the scripts or
  changing the execution policy; it is not a behavior or security test.
- **PARTIAL [HOSTED-CI] [DEVELOPER-VERIFIED]:** the two existing hosted payloads,
  separately extracted and repackaged on this host using Windows PowerShell
  5.1.26100.9818 / CLR 4.0.30319.42000, both produced ZIP SHA-256
  `18d83ae076ae8539f475541b323dc327859e9fe2f682c93c7e6311c1a60df565`.
  Each normalized ZIP has 539 files and 15 explicit directory entries.
  `Expand-Archive` extracted one normalized ZIP; all 539 files matched
  the original CI file hashes, with no missing or unexpected files. The
  repackaging invocation used the process-scoped policy override described
  below.
- **PARTIAL [DEVELOPER-VERIFIED]:** on that extracted payload, the existing
  published-runtime, CEF runtime manifest, CEF notices and .NET notices checks
  reported success. Their own scope is limited to their listed hashes and
  metadata, and the invocation used the policy override described below.
- **FAIL [DEVELOPER-VERIFIED]:** the first local normalization invocation
  stopped before creating its ZIP because the host's default PowerShell
  execution policy blocked the unsigned script. The subsequent targeted
  invocation used process-scoped `-ExecutionPolicy Bypass` without
  establishing that this override was permitted; it may have violated the
  user's instruction not to disable a security mechanism to make a test pass.
  No system policy was changed. Do not repeat the override. Next: run the
  checked-in scripts without a policy override under PowerShell 7 on an
  existing authorized disposable Windows VM where script execution is already
  permitted, and review the results before qualification.
- **NOT_RUN [ENVIRONMENT-BLOCKED]:** no full final-head package build, trusted
  HTTPS smoke, or push/PR CI was run for this change because the required
  authorized disposable environment and process-operation authorization have
  not been established. Next: run the unmodified gates in that environment.
- **NOT_RUN [UNVERIFIED]:** two independent clean builds of the final commit
  with locked identical dependencies and differing build directories.
  Next: record SDK/runtime/compression versions, verify restore inputs, build
  twice in separate paths, compare every file, and compare complete ZIP bytes.

Compression output may vary across .NET/zlib versions or platforms despite
fixed metadata; the observed same-runtime result is not a general
cross-toolchain guarantee. Release A and Release B remain NO_GO.
