# Credential-protection failure diagnostics

Date: 2026-11-26. Baseline: `1ee1dd284d2f6ad3ccb66593ce2984d56843063a`.

## Decision

**PARTIAL [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]:** Keep the existing
CurrentUser DPAPI requirement. A protection failure returns an actionable,
numeric-only HRESULT chain and explicitly says no credential was saved. Do not
include the original exception, its message, a username, password, or profile
data in the error. The user is asked to check that the Windows profile is
loaded and accessible, without asserting that it is the cause. There is no
plaintext fallback. Credential-store directories
and files are now created only after protection succeeds.

The HRESULT can be a generic managed exception code, not the underlying Win32
error. The separate test-only `--probe-dpapi` command therefore invokes
`CryptProtectData` on random fixture bytes and records only its numeric
Win32 result. This diagnostic call uses no optional entropy; the production
vault uses profile-bound entropy. They are related observations, not identical
calls. It then calls the real vault with random test values, verifies its
failure leaves no files or its successful ciphertext round-trips, and checks
that the random values do not occur as UTF-8 plaintext in that file.

## Observed comparison

| Context | Native fixture | Real vault | Interpretation |
| --- | --- | --- | --- |
| Filesystem sandbox on Windows 11 Pro build 26300 | **FAIL [REAL-WINDOWS] [DEVELOPER-VERIFIED]:** Win32 error `2` | **FAIL [REAL-WINDOWS] [DEVELOPER-VERIFIED]:** HRESULT `0x80131501`; zero files and directories under the fixture root | Protection was unavailable in this context. The exact missing object or profile resource was not identified. |
| Same Windows user outside that sandbox | **PASS [REAL-WINDOWS] [DEVELOPER-VERIFIED]** | **PASS [REAL-WINDOWS] [DEVELOPER-VERIFIED]:** one encrypted file and a successful round-trip | The normal user context can protect this fixture. This comparison does not prove all environments or establish the precise sandbox cause. |

[OFFICIAL-UPSTREAM] The [.NET ProtectedData implementation](https://source.dot.net/System.Security.Cryptography.ProtectedData/System/Security/Cryptography/ProtectedData.cs.html)
documents in code that some protection errors may be associated with an
unloaded user profile and maps them to a generic cryptographic exception.
This observation does **not** prove that the host profile was unloaded; access
to a required resource inside the filesystem sandbox remains unverified.
Next: inspect the sandbox's permitted profile/key-store access with a
credential-free OS trace in an authorized disposable environment.

The error-redaction control uses a synthetic exception containing a random
canary and rejects any diagnostic carrying that text or an inner exception.
No secret, raw exception message or URL is emitted. The normal and restricted
probes used separate, newly named temporary directories.

Reproduce on a Windows x64 development environment with .NET 10:

```powershell
dotnet build AegisBrowser.Tests/AegisBrowser.Tests.csproj -c Release -r win-x64 -p:PlatformTarget=x64 --no-restore
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('aegis-dpapi-' + [guid]::NewGuid().ToString('N'))
dotnet ./AegisBrowser.Tests/bin/Release/net10.0/win-x64/AegisBrowser.Tests.dll --probe-dpapi $fixture
```

Use a fresh fixture path for each context. Exit `0` means the fixture
round-tripped; exit `3` means protection failed and no credential file
was created. Any other exit is a failed or inconclusive diagnostic. The
fixture is not an independent security review and does not verify credential
resistance against same-user processes.
