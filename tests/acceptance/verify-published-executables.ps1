[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'EXECUTABLE_ROOT_INVALID' }
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('AegisBrowser.exe', 'CefSharp.BrowserSubprocess.exe', 'createdump.exe')) {
    [void]$expected.Add($name)
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.exe') {
    if (-not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'EXECUTABLE_PATH_INVALID'
    }
    $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
    if (-not $expected.Contains($relative) -or -not $seen.Add($relative)) {
        throw "EXECUTABLE_UNEXPECTED: $relative"
    }
}
foreach ($name in $expected) {
    if (-not $seen.Contains($name)) { throw "EXECUTABLE_MISSING: $name" }
}

# The source-controlled CEF manifest is derived from the signed, catalog-matched
# NuGet package. This check does not assert the identity of the application exe.
$manifestPath = Join-Path $PSScriptRoot '../../third-party-licenses/CEF-RUNTIME-HASHES.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$subprocess = @($manifest.files | Where-Object { $_.path -ceq 'CefSharp.BrowserSubprocess.exe' })
if ($subprocess.Count -ne 1 -or $subprocess[0].sha256 -cnotmatch '^[A-F0-9]{64}$') {
    throw 'EXECUTABLE_MANIFEST_INVALID'
}
$hash = (Get-FileHash -LiteralPath (Join-Path $root 'CefSharp.BrowserSubprocess.exe') -Algorithm SHA256).Hash
if ($hash -cne $subprocess[0].sha256) { throw 'EXECUTABLE_MISMATCH: CefSharp.BrowserSubprocess.exe' }
# SHA-256 of runtimes/win-x64/native/createdump.exe in the catalog-matched
# Microsoft.NETCore.App.Runtime.win-x64 10.0.15 package. Signature details were
# observed; a complete retained trust decision remains unverified.
$dumpHash = (Get-FileHash -LiteralPath (Join-Path $root 'createdump.exe') -Algorithm SHA256).Hash
if ($dumpHash -cne '5161F24CCBE3AF9FF224CAB8D667F87920A58136DD045B054999E19AC29004F3') {
    throw 'EXECUTABLE_MISMATCH: createdump.exe'
}
Write-Output 'PASS: expected executable names, subprocess hash and runtime dump hash verified.'
Write-Output 'UNVERIFIED: executable inventory alone does not verify AegisBrowser.exe identity.'
