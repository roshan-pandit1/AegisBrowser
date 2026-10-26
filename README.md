# Aegis Browser

Windows x64 privacy and diagnostics browser built with C# / .NET 8, WinForms and CefSharp Chromium. See [build instructions, behavior and verification](AegisBrowser/README.md).

From this directory:

```powershell
dotnet restore AegisBrowser/AegisBrowser.csproj
dotnet build AegisBrowser/AegisBrowser.csproj -c Release --no-restore
dotnet run --project AegisBrowser.Tests -c Release
dotnet run --project AegisBrowser -c Release -- --profile=Personal
```

## Security audit and release status

This checkout contains uncommitted audit improvements; it is **not a verified release candidate**. See [current evidence](BUILD-STATUS.md), [security policy](SECURITY.md), [architecture](docs/SECURITY-ARCHITECTURE.md), [limitations](docs/SECURITY-LIMITATIONS.md) and [acceptance matrix](docs/TEST-MATRIX.md). Historical artifacts do not validate new source changes.

Additional source checks (Python 3.12+) and portable .NET tests:

```sh
python scripts/security_audit.py
python -m unittest discover -s scripts -p test_security_audit.py -v
dotnet run --project AegisBrowser.Tests -c Release -- --core-only
dotnet run --project AegisBrowser.Tests -c Release -- --benchmark
```

`--core-only` deliberately omits the loopback HTTP lab suite; the default console command and Windows workflow still run it. No benchmark timings are currently verified. Restore audits direct/transitive dependencies; high/critical advisories or missing audit data fail instead of being silently suppressed.

For exact Windows browser/filtering/publish commands, use [build instructions](AegisBrowser/README.md) and the existing [Windows workflow](.github/workflows/windows.yml). Portable packaging requires a clean reviewed checkout, Python 3.12+, PowerShell 7 and Windows. It includes a package/file SBOM, checks payload exclusions, verifies ZIP extraction and runs published/extracted smoke checks. See [release checklist](docs/RELEASE-CHECKLIST.md). Nothing is pushed, signed or published automatically by local audit work.
