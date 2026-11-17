# P0 isolation controls

This standalone Windows-only test uses .NET 8 and operating-system APIs. It
has no Chromium, CEF, or additional NuGet dependency. Run on a disposable
Windows x64 runner from the repository root:

```powershell
dotnet restore tests/research/p0isolation/P0Isolation.csproj -r win-x64
dotnet run --project tests/research/p0isolation/P0Isolation.csproj -c Release -r win-x64 --no-restore
```

The first control creates an owned, user-only temporary file. It requires an
unrestricted open, an `ERROR_ACCESS_DENIED` direct open while impersonating a
synthetic restricted token, and another unrestricted open after reverting.
It removes only its own file and directory. Some host confinement policies may
prevent creating the synthetic token, in which case the test fails; it must not
be interpreted as a CEF result.

The second control retains handles for a test-owned parent and synthetic child
and compares liveness, identity, parent and bounded command-line evidence.
It checks wrong markers, wrong parents, and exited handles. Raw arguments and
native pointer values are not logged. It does not enumerate arbitrary processes.
The arguments are claims controlled by a test process; a match is **not** proof
that a process is a renderer, that it services a particular frame, or that a CEF
sandbox is active. Independently corroborated renderer role, actual renderer
token and direct-operation evidence are still required.

For process-role controls alone, append `-- --role-only` to the run command.
That mode deliberately omits the file access control.
