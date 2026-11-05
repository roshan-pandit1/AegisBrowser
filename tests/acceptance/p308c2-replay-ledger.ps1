# Research-only replay ledger. Caller must validate all evidence before consuming IDs.
# State directory must be operator-owned; path checks do not defeat malicious replacement.
function Assert-P308c2UnlinkedPath([string] $Path, [bool] $MustExist) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path -notmatch '^[A-Za-z]:\\' -or $Path.Substring(2).Contains(':')) { throw 'STATE_PATH_INVALID' }
    $full = [IO.Path]::GetFullPath($Path)
    $current = if ($MustExist) { Get-Item -LiteralPath $full -Force -ErrorAction Stop } else { Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($full)) -Force -ErrorAction Stop }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'STATE_PATH_INVALID' }
        $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
    return $full
}
function Initialize-P308c2Ledger([string] $Path) {
    $full = Assert-P308c2UnlinkedPath $Path $false
    $stream = New-Object IO.FileStream($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $header = [Text.Encoding]::ASCII.GetBytes("AEGIS-P308C2-LEDGER-1`n")
        $stream.Write($header, 0, $header.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
}
function Add-P308c2ConsumedRun([string] $Path, [guid] $Run, [guid] $Ordinary, [guid] $AppContainer) {
    $ids = @($Run.ToString('N'), $Ordinary.ToString('N'), $AppContainer.ToString('N'))
    if ($Run -eq [guid]::Empty -or $Ordinary -eq [guid]::Empty -or $AppContainer -eq [guid]::Empty -or @($ids | Select-Object -Unique).Count -ne 3) { throw 'SCHEMA_INVALID' }
    $full = Assert-P308c2UnlinkedPath $Path $true
    # Exclusive open serializes cooperating verifiers. Contention fails closed; retry from validation.
    $stream = New-Object IO.FileStream($full, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if ($stream.Length -gt 1048576 -or $stream.Length -lt 22) { throw 'LEDGER_INVALID' }
        $bytes = New-Object byte[] ([int] $stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) { $n = $stream.Read($bytes,$offset,$bytes.Length-$offset); if ($n -eq 0) { throw 'LEDGER_INVALID' }; $offset += $n }
        foreach ($b in $bytes) { if (($b -lt 32 -or $b -gt 126) -and $b -ne 10) { throw 'LEDGER_INVALID' } }
        $lines = [Text.Encoding]::ASCII.GetString($bytes).Split([char]10)
        if ($lines[0] -cne 'AEGIS-P308C2-LEDGER-1' -or $lines[$lines.Length-1] -cne '') { throw 'LEDGER_INVALID' }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        for ($i = 1; $i -lt $lines.Length-1; $i++) {
            if ($lines[$i] -cnotmatch '^[0-9a-f]{32} [0-9a-f]{32} [0-9a-f]{32}$') { throw 'LEDGER_INVALID' }
            foreach ($id in $lines[$i].Split(' ')) {
                if ($id -ceq '00000000000000000000000000000000' -or -not $seen.Add($id)) { throw 'LEDGER_INVALID' }
            }
        }
        foreach ($id in $ids) { if ($seen.Contains($id)) { throw 'REPLAY_DETECTED' } }
        $line = [Text.Encoding]::ASCII.GetBytes(($ids -join ' ') + "`n")
        if ($stream.Length + $line.Length -gt 1048576) { throw 'LEDGER_INVALID' }
        $stream.Seek(0, [IO.SeekOrigin]::End) | Out-Null
        $stream.Write($line,0,$line.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}
