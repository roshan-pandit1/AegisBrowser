[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
. (Join-Path $PSScriptRoot 'archive-entry-policy.ps1')

$resolvedRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$root = $resolvedRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($root -ceq [IO.Path]::GetPathRoot($resolvedRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw 'PACKAGE_ZIP_INPUT_INVALID: a volume root cannot be packaged.'
}
function Assert-OrdinaryAncestors([string]$Path) {
    $current = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $current) {
        $item = Get-Item -LiteralPath $current.FullName -Force
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'PACKAGE_ZIP_INPUT_INVALID: reparse point in a source or output ancestor.'
        }
        $current = $current.Parent
    }
}
Assert-OrdinaryAncestors $root
$rootItem = Get-Item -LiteralPath $root -Force
if (-not $rootItem.PSIsContainer -or
    ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'PACKAGE_ZIP_INPUT_INVALID: the source must be a non-reparse directory.'
}
$target = [IO.Path]::GetFullPath($ArchivePath)
$targetParent = [IO.Path]::GetDirectoryName($target)
if ([string]::IsNullOrEmpty($targetParent)) {
    throw 'PACKAGE_ZIP_OUTPUT_INVALID: an existing output directory is required.'
}
Assert-OrdinaryAncestors $targetParent
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
if ($target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $target.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PACKAGE_ZIP_OUTPUT_INVALID: the archive must be outside the source directory.'
}

$rootName = [IO.Path]::GetFileName($root)
if ([string]::IsNullOrWhiteSpace($rootName)) {
    throw 'PACKAGE_ZIP_INPUT_INVALID: source directory name is empty.'
}
$entries = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
$unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $item.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PACKAGE_ZIP_INPUT_INVALID: reparse point or path outside the source.'
    }
    $relative = $item.FullName.Substring($rootPrefix.Length).Replace('\', '/')
    $entryName = $rootName + '/' + $relative
    if ($item.PSIsContainer) { $entryName += '/' }
    Assert-AegisArchiveEntry -EntryName $entryName -IsDirectory ([bool]$item.PSIsContainer)
    # Compare without the directory suffix so a case-sensitive source cannot
    # introduce a file/directory collision when extracted on Windows.
    $collisionKey = if ($item.PSIsContainer) { $entryName.Substring(0, $entryName.Length - 1) } else { $entryName }
    if (-not $unique.Add($collisionKey)) {
        throw 'PACKAGE_ZIP_INPUT_INVALID: duplicate case-insensitive entry.'
    }
    $entries.Add($entryName, $(if ($item.PSIsContainer) { '' } else { $item.FullName }))
}

$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $fixedTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        foreach ($pair in $entries.GetEnumerator()) {
            $entry = $zip.CreateEntry($pair.Key, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTime
            $entry.ExternalAttributes = 0
            if (-not [string]::IsNullOrEmpty($pair.Value)) {
                $inputStream = [IO.File]::Open($pair.Value, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
