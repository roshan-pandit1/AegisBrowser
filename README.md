# Aegis Browser

Windows x64 privacy and diagnostics browser built with C# / .NET 8, WinForms and CefSharp Chromium. See [build instructions, behavior and verification](AegisBrowser/README.md).

From this directory:

```powershell
dotnet restore AegisBrowser/AegisBrowser.csproj
dotnet build AegisBrowser/AegisBrowser.csproj -c Release --no-restore
dotnet run --project AegisBrowser.Tests -c Release
dotnet run --project AegisBrowser -c Release -- --profile=Personal
```
