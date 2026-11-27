# Retained preview artifact: additional source matches

**PARTIAL [SOURCE-VERIFIED] [DEVELOPER-VERIFIED] [UNVERIFIED].** The prior
[SBOM](../security/pr90-preview.sbom.cdx.json) remains unchanged as historical
evidence. Its 539 file entries included 27 with unresolved origins. On this date,
read-only SHA-256 comparisons matched 12 of those entries both to a file in this
checkout and directly to the corresponding member of the retained preview ZIP.
This reduces the set without a demonstrated repository-byte match to 15; it does
not establish publisher authenticity, license correctness, artifact custody,
signature-chain trust, advisory status, or build provenance for any of them.
The archive's relationship to the current GitHub head is also unverified.

The retained ZIP SHA-256 is
`8A95CE41A2FD16684810376A80D2C4C1FED8E02FBAFEA15630FBBD2B84D7263B`.
The following SHA-256 values were compared separately against the SBOM, local
source file, and archive member. Paths under `third-party-licenses/` are the
same in the repository and underneath the archive's `AegisBrowser/` directory.

| Archive member | Repository source | SHA-256 |
| --- | --- | --- |
| `AegisBrowser/INSTALL-WINDOWS.md` | `docs/INSTALL-WINDOWS.md` | `DA0ADD1ABC0549511EF730D3DEA04A1E99173C7542B624E13D0F3074BD5106CE` |
| `AegisBrowser/production-release.md` | `docs/production-release.md` | `EDF6FCE156D67F722D67EE1B063A98FD84D5ED62A5A43BC156C3BE00EC7742C0` |
| `AegisBrowser/release-rc1.md` | `docs/release-rc1.md` | `3FD97B577CFB3AA9A24B293D270E113AF7180856B3A0B9923E16FF3E71ECCBB7` |
| `AegisBrowser/third-party-licenses/CEF-CREDITS.html` | `third-party-licenses/CEF-CREDITS.html` | `EB5A0B6300C58BB8316A66B3E935D55B1456F8F1CF225C979B03D42AFA878C4B` |
| `AegisBrowser/third-party-licenses/CEF-LICENSE.txt` | `third-party-licenses/CEF-LICENSE.txt` | `2E69C36A7EAA4FA153426EAB635C607EA0356CBC7A68A70F42A49E8AB8EB8106` |
| `AegisBrowser/third-party-licenses/CEF-RUNTIME-HASHES.json` | `third-party-licenses/CEF-RUNTIME-HASHES.json` | `F127D86B7328E194406942AD0281508B182E170357B2212F4239C5291AFE288A` |
| `AegisBrowser/third-party-licenses/CefSharp-LICENSE.txt` | `third-party-licenses/CefSharp-LICENSE.txt` | `5587B0CD1D5BB1011F1D33E9D2A255F09A735F835354BDEA478005531D6D3891` |
| `AegisBrowser/third-party-licenses/DOTNET-DESKTOP-LICENSE.TXT` | `third-party-licenses/DOTNET-DESKTOP-LICENSE.TXT` | `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB` |
| `AegisBrowser/third-party-licenses/DOTNET-RUNTIME-LICENSE.TXT` | `third-party-licenses/DOTNET-RUNTIME-LICENSE.TXT` | `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B` |
| `AegisBrowser/third-party-licenses/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT` | `third-party-licenses/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT` | `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21` |
| `AegisBrowser/third-party-licenses/README.md` | `third-party-licenses/README.md` | `99724176A8DEDA433901789B54DE23C279B7DBDFD636FA117C3D8CAC728CE430` |
| `AegisBrowser/third-party-licenses/SQLITEPCLRAW-APACHE-2.0.txt` | `third-party-licenses/SQLITEPCLRAW-APACHE-2.0.txt` | `CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30` |

The local comparison enumerated repository filenames, compared SHA-256 values
with `Get-FileHash`, then read the named entries from the existing ZIP using
`System.IO.Compression.ZipFile.OpenRead` and SHA-256 streams. It did not extract
or execute the archive. The first archive-read attempt used an unavailable .NET
conversion method and failed; the successful comparison used
`BitConverter.ToString`. Both attempts disposed the archive and streams.

## Further read-only matches

Six further archive members match both their SBOM digest and the source files
listed below. Four were missed by the earlier case-sensitive filename search;
the other two match files in the preserved PR #90 worktree, not this checkout.
These are byte matches, not authenticated build origins. **PARTIAL
[SOURCE-VERIFIED] [DEVELOPMENT-SAFE].** Of the original 27, 18 now have
repository-byte matches and nine do not; no additional publisher or build-host
provenance was authenticated.

| Archive member | Matching repository file | SHA-256 |
| --- | --- | --- |
| `PORTABLE-CANDIDATE.md` | `docs/portable-candidate.md` | `36BD2ED054F405F137623DE6723C1FA5396513C94EEAB39985E00331DA08042D` |
| `CORE-MVP-BASELINE.md` | `docs/core-mvp-baseline.md` | `1DA52ABEE57DC24F6117BADACA6600D787CFE5D9C7004C6256FA73BB045AC193` |
| `PROFILE-MANAGER.md` | `docs/profile-manager.md` | `733A9A5AC14CF85D2B5FD98166913F18FA4332EA09961B2A40F1E6563C73458A` |
| `SEARCH.md` | `docs/search.md` | `0D65BBDDDF19D505A969C54AD1DB7B082A921A47705976B62C3D2AFF127A8AF6` |
| `BUILD-STATUS.md` | Historical PR #90 worktree `BUILD-STATUS.md` | `2CCB6DD053376CA22ED3742E1FEDB9F6211562A51811D31E1D546E77A43D8E77` |
| `PRIVACY.md` | Historical PR #90 worktree `docs/privacy.md` | `126CBEBAEF4ACDC142431A53328A82AC52F7C04564908F94721DB845EBCD2437` |

The remaining nine comprise seven application or generated build files,
`ENGINE-SELECTION.md` and `VERSION.txt`. Some build scripts describe how these
files are generated, but the retained archive's embedded claims are not a build
attestation. Next: compare them with authenticated outputs and build-service
attestation for the exact source commit.

**NOT_RUN [NOT_RUN]:** CycloneDX schema validation and new authenticated package
verification. The validator and transitive execution paths were not qualified in
this environment. Next: independently authenticate exact artifacts, validate their
signatures and trust chain, establish build attestation, and re-run schema
validation with an audited validator. Do not infer any of these from a hash match.
