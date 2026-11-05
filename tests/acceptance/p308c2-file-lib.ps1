# Bounded, explicit file reads for operator-transferred evidence. No directory traversal.
# Path checks are defense in depth, not a defense against hostile concurrent replacement.
function Read-P308c2EvidenceFile {
    param([Parameter(Mandatory=$true)][string] $Path, [Parameter(Mandatory=$true)][ValidateRange(1,65536)][int] $MaximumBytes)
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path -notmatch '^[A-Za-z]:\\' -or $Path.Substring(2).Contains(':')) { throw 'SCHEMA_INVALID' }
    $full = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($item.PSIsContainer) { throw 'SCHEMA_INVALID' }
    $cursor = $item
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SCHEMA_INVALID' }
        if ($cursor -is [IO.FileInfo]) { $cursor = $cursor.Directory } else { $cursor = $cursor.Parent }
    }
    $stream = [IO.File]::Open($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $MaximumBytes) { throw 'SCHEMA_INVALID' }
        $buffer = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $buffer.Length) {
            $count = $stream.Read($buffer,$offset,$buffer.Length-$offset)
            if ($count -le 0) { throw 'SCHEMA_INVALID' }
            $offset += $count
        }
        if ($stream.ReadByte() -ne -1) { throw 'SCHEMA_INVALID' }
        return ,$buffer
    } finally { $stream.Dispose() }
}
function Import-P308c2FileRecord {
    param([Parameter(Mandatory=$true)][string] $PayloadPath, [Parameter(Mandatory=$true)][string] $SignaturePath)
    $payload = Read-P308c2EvidenceFile -Path $PayloadPath -MaximumBytes 4096
    $signature = Read-P308c2EvidenceFile -Path $SignaturePath -MaximumBytes 1024
    return [pscustomobject]@{ Payload = $payload; Signature = $signature }
}
