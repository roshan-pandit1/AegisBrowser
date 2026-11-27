# Pure policy for Windows-compatible ZIP entry names. This function performs
# no filesystem, network, process, registry, or environment operations.
function Assert-AegisArchiveEntry {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$EntryName,
        [Parameter(Mandatory)][bool]$IsDirectory
    )

    if ([string]::IsNullOrEmpty($EntryName) -or $EntryName.StartsWith('/') -or
        $EntryName -match '[\x00-\x1F<>:"\\|?*]') {
        throw 'PACKAGE_ZIP_ENTRY_INVALID'
    }
    if ($IsDirectory) {
        if (-not $EntryName.EndsWith('/')) { throw 'PACKAGE_ZIP_ENTRY_INVALID' }
        $candidate = $EntryName.Substring(0, $EntryName.Length - 1)
    }
    else {
        if ($EntryName.EndsWith('/')) { throw 'PACKAGE_ZIP_ENTRY_INVALID' }
        $candidate = $EntryName
    }

    foreach ($part in $candidate.Split([char]'/')) {
        if ([string]::IsNullOrEmpty($part) -or $part -eq '.' -or $part -eq '..' -or
            $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])(?:\..*)?$') {
            throw 'PACKAGE_ZIP_ENTRY_INVALID'
        }
    }
}
