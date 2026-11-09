Set-StrictMode -Version Latest

# Read-only input qualification, not a sandbox or publisher verification.
# The expected hashes must be approved out of band. No files are executed.
$script:P309fExpectedCefVersion = '154.0.34+g14c5a08+chromium-154.0.8037.98'
$script:P309fMaximumFileBytes = 1073741824

function Get-P309fFileEvidence {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Role)
    # UNC, device paths, drive-relative names and alternate streams are not
    # accepted. In particular, do not probe a remote share during preflight.
    if ($Path -cnotmatch '^[A-Za-z]:\\') { throw 'PATH_NOT_ABSOLUTE_LOCAL' }
    if ($Path.Substring(2).Contains(':')) { throw 'ALTERNATE_STREAM_REJECTED' }
    if ($Role -notin @('Bootstrap', 'Client', 'LibCef')) { throw 'INVALID_ROLE' }
    $full = [IO.Path]::GetFullPath($Path)
    $drive = [IO.DriveInfo]::new($full.Substring(0, 3))
    if ($drive.DriveType -notin @([IO.DriveType]::Fixed, [IO.DriveType]::Ram)) { throw 'NONLOCAL_DRIVE_REJECTED' }
    $extension = [IO.Path]::GetExtension($full)
    $expectedExtension = if ($Role -eq 'Bootstrap') { '.exe' } else { '.dll' }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($extension, $expectedExtension)) { throw 'INVALID_EXTENSION' }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($item.PSIsContainer) { throw 'NOT_A_FILE' }
    # Reject reparse points in every visible path component. A malicious local
    # administrator or concurrent path replacement is outside this check.
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'REPARSE_POINT' }
    $current = $item.Directory
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'REPARSE_POINT' }
        $current = $current.Parent
    }
    $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 90 -or $stream.Length -gt $script:P309fMaximumFileBytes) { throw 'FILE_SIZE_INVALID' }
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'PE_DOS_SIGNATURE_INVALID' }
        $stream.Position = 0x3C
        $offset = $reader.ReadUInt32()
        if ($offset -lt 64 -or $offset -gt 2097152 -or ([long]$offset + 26) -gt $stream.Length) { throw 'PE_OFFSET_INVALID' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'PE_SIGNATURE_INVALID' }
        if ($reader.ReadUInt16() -ne 0x8664) { throw 'PE_MACHINE_NOT_X64' }
        $stream.Position = [long]$offset + 20
        $optionalSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if ($optionalSize -lt 2 -or ([long]$offset + 24 + $optionalSize) -gt $stream.Length) { throw 'PE_OPTIONAL_HEADER_INVALID' }
        if ($reader.ReadUInt16() -ne 0x20B) { throw 'PE_NOT_PE32PLUS' }
        $isDll = ($characteristics -band 0x2000) -ne 0
        if (($Role -eq 'Bootstrap' -and $isDll) -or ($Role -ne 'Bootstrap' -and -not $isDll)) { throw 'PE_ROLE_MISMATCH' }
        $stream.Position = 0
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '') }
        finally { $hasher.Dispose() }
        $version = if ($Role -eq 'LibCef') { [Diagnostics.FileVersionInfo]::GetVersionInfo($full).FileVersion } else { $null }
        return [pscustomobject]@{ Role = $Role; Path = $full; Sha256 = $hash; FileVersion = $version }
    }
    finally { $stream.Dispose() }
}

function Test-P309fEvidenceSet {
    param([object[]]$Evidence, [System.Collections.IDictionary]$ExpectedHashes)
    $roles = @('Bootstrap', 'Client', 'LibCef')
    if ($Evidence.Count -ne 3) { throw 'INCOMPLETE_EVIDENCE' }
    $seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($role in $roles) {
        $selected = @($Evidence | Where-Object { $_.Role -ceq $role })
        if ($selected.Count -ne 1) { throw 'INVALID_ROLE_SET' }
        if (-not $ExpectedHashes.Contains($role) -or [string]$ExpectedHashes[$role] -cnotmatch '^[A-Fa-f0-9]{64}$') { throw 'INVALID_PIN' }
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals([string]$selected[0].Sha256, [string]$ExpectedHashes[$role])) { throw 'HASH_MISMATCH' }
        if (-not $seenPaths.Add([string]$selected[0].Path)) { throw 'DUPLICATE_INPUT' }
        if ($role -eq 'LibCef' -and $selected[0].FileVersion -cne $script:P309fExpectedCefVersion) { throw 'CEF_VERSION_MISMATCH' }
    }
    return [pscustomobject]@{
        Outcome = 'REVIEW_REQUIRED'
        SandboxInfo = 'NOT_OBSERVED'
        SandboxEnforcement = 'NOT_RUN'
        CefStartup = 'NOT_RUN'
        CefShutdown = 'NOT_RUN'
        Note = 'Matching caller-supplied pins do not authenticate publisher, prove bootstrap usage, or verify sandbox enforcement.'
    }
}
