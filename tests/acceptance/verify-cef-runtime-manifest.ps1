[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$manifestPath = Join-Path $PSScriptRoot '../../third-party-licenses/CEF-RUNTIME-HASHES.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.cefSharp -cne '154.0.340' -or
    $manifest.cef -cne '154.0.34+g14c5a08+chromium-154.0.8037.98' -or @($manifest.files).Count -ne 240) {
    throw 'CEF_COMPONENT_MANIFEST_INVALID: unexpected engine identity or file count.'
}
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $manifest.files) {
    $relative = [string]$item.path
    if ($relative -notmatch '^(?:locales/)?[A-Za-z0-9._-]+$' -or
        $item.sha256 -cnotmatch '^[A-F0-9]{64}$' -or -not $expected.Add($relative)) {
        throw 'CEF_COMPONENT_MANIFEST_INVALID: unsafe or duplicate file entry.'
    }
    [void]$expectedNames.Add([IO.Path]::GetFileName($relative))
    $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "CEF_COMPONENT_MISSING: $relative"
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $item.sha256) {
        throw "CEF_COMPONENT_MISMATCH: $relative"
    }
}

# Reject duplicate engine components and unexpected CefSharp binaries or locales.
# A matching filename elsewhere in the package is not an alternate search path.
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    if (-not $file.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CEF_COMPONENT_MANIFEST_INVALID: enumerated file outside the package.'
    }
    $relative = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
    $isEngineBinary = $file.Name -match '^(?i:CefSharp.*\.(dll|exe)|libcef\.dll)$'
    $isLocale = $relative -match '(?i)(^|/)locales/.*\.pak$'
    if (($isEngineBinary -or $isLocale -or $expectedNames.Contains($file.Name)) -and -not $expected.Contains($relative)) {
        throw "CEF_COMPONENT_UNEXPECTED: $relative"
    }
}

$cefVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'libcef.dll')).FileVersion
if ($cefVersion -cne $manifest.cef) { throw 'CEF_COMPONENT_VERSION_MISMATCH: libcef.dll' }
& (Join-Path $PSScriptRoot 'verify-published-executables.ps1') -PublishDirectory $root
Write-Output "PASS: 240 CEF/CefSharp x64 binaries, resources and locales match the recorded signed-package hashes; libcef=$cefVersion"
