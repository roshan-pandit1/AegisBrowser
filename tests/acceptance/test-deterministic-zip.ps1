$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Join-Path ([IO.Path]::GetTempPath()) ('aegis-deterministic-zip-' + [guid]::NewGuid().ToString('N'))
$left = Join-Path $root 'left/AegisBrowser'
$right = Join-Path $root 'right/AegisBrowser'
$leftNested = Join-Path $left 'nested'
$rightNested = Join-Path $right 'nested'
$leftEmpty = Join-Path $left 'empty'
$rightEmpty = Join-Path $right 'empty'
$name = 'caf' + [char]0xE9 + '.bin'
$leftA = Join-Path $left 'a.bin'
$leftB = Join-Path $leftNested $name
$rightA = Join-Path $right 'a.bin'
$rightB = Join-Path $rightNested $name
$archiveA = Join-Path $root 'a.zip'
$archiveB = Join-Path $root 'b.zip'
$inside = Join-Path $left 'inside.zip'
$writer = Join-Path $PSScriptRoot '../../scripts/write-deterministic-zip.ps1'

# Refuse any existing fixture root before creating or deleting test-owned files.
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
try {
    foreach ($directory in @($leftNested, $rightNested, $leftEmpty, $rightEmpty)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    # Different creation order and filesystem times, with identical content.
    [IO.File]::WriteAllBytes($leftB, [byte[]](0, 1, 2, 127, 255))
    [IO.File]::WriteAllText($leftA, 'same content')
    [IO.File]::WriteAllText($rightA, 'same content')
    [IO.File]::WriteAllBytes($rightB, [byte[]](0, 1, 2, 127, 255))
    foreach ($file in @($leftA, $leftB)) {
        [IO.File]::SetLastWriteTimeUtc($file, [DateTime]::new(2021, 1, 2, 3, 4, 6, [DateTimeKind]::Utc))
    }
    foreach ($file in @($rightA, $rightB)) {
        [IO.File]::SetLastWriteTimeUtc($file, [DateTime]::new(2025, 6, 7, 8, 9, 10, [DateTimeKind]::Utc))
    }
    & $writer -SourceDirectory $left -ArchivePath $archiveA
    & $writer -SourceDirectory $right -ArchivePath $archiveB
    $firstHash = (Get-FileHash -LiteralPath $archiveA -Algorithm SHA256).Hash
    if ($firstHash -cne (Get-FileHash -LiteralPath $archiveB -Algorithm SHA256).Hash) {
        throw 'Identical source bytes and names did not produce identical ZIP bytes.'
    }
    Write-Output 'PASS: same inputs with different source times and creation order yield identical ZIP bytes'

    $zip = [IO.Compression.ZipFile]::OpenRead($archiveA)
    try {
        $actualNames = @($zip.Entries | ForEach-Object { $_.FullName })
        $expectedNames = [string[]]@('AegisBrowser/a.bin', 'AegisBrowser/empty/', 'AegisBrowser/nested/',
            ('AegisBrowser/nested/' + $name))
        [Array]::Sort($expectedNames, [StringComparer]::Ordinal)
        if (($actualNames -join [char]0) -cne ($expectedNames -join [char]0)) {
            throw 'ZIP entry inventory or ordinal order differs.'
        }
        foreach ($entry in $zip.Entries) {
            if ($entry.LastWriteTime.Year -ne 1980 -or $entry.LastWriteTime.Month -ne 1 -or
                $entry.LastWriteTime.Day -ne 1 -or $entry.LastWriteTime.Hour -ne 0 -or
                $entry.LastWriteTime.Minute -ne 0 -or $entry.LastWriteTime.Second -ne 0) {
                throw 'ZIP timestamp was not normalized.'
            }
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Substring('AegisBrowser/'.Length).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $expected = (Get-FileHash -LiteralPath (Join-Path $left $relative) -Algorithm SHA256).Hash
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $entryStream = $entry.Open()
                try { $actual = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '') }
                finally { $entryStream.Dispose() }
            } finally { $sha.Dispose() }
            if ($actual -cne $expected) { throw 'ZIP entry content differs from the source.' }
        }
    } finally { $zip.Dispose() }
    Write-Output 'PASS: ZIP entry order, timestamp, empty directories and file hashes match the expected inputs'

    $rejected = $false
    try { & $writer -SourceDirectory $left -ArchivePath $archiveA }
    catch [IO.IOException] { $rejected = $true }
    if (-not $rejected -or (Get-FileHash -LiteralPath $archiveA -Algorithm SHA256).Hash -cne $firstHash) {
        throw 'An existing ZIP was not rejected and preserved.'
    }
    Write-Output 'PASS: an existing output archive is rejected without modification'

    $rejected = $false
    try { & $writer -SourceDirectory $left -ArchivePath $inside }
    catch { if ($_.Exception.Message -like 'PACKAGE_ZIP_OUTPUT_INVALID:*') { $rejected = $true } else { throw } }
    if (-not $rejected -or [IO.File]::Exists($inside)) { throw 'An output inside the source was not rejected.' }
    Write-Output 'PASS: an output path inside the source tree is rejected'
} finally {
    # These exact files and empty directories were created by this test.
    foreach ($file in @($archiveA, $archiveB, $leftA, $leftB, $rightA, $rightB)) {
        if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    }
    foreach ($directory in @($leftNested, $rightNested, $leftEmpty, $rightEmpty, $left, $right,
            (Join-Path $root 'left'), (Join-Path $root 'right'), $root)) {
        if ([IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $false) }
    }
}
