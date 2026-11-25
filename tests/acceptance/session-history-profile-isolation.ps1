param([Parameter(Mandatory=$true)][string]$BrowserTestsExe)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('aegis-history-pair-' + [guid]::NewGuid().ToString('N'))
$scratch = Join-Path $root 'temp'
New-Item -ItemType Directory -Path $scratch | Out-Null
$exe = (Resolve-Path -LiteralPath $BrowserTestsExe).Path
$id = [guid]::NewGuid().ToString('N')
$markerA = [guid]::NewGuid().ToString('N'); $markerB = [guid]::NewGuid().ToString('N')
$readyAName = 'Local\AegisHistory-' + $id + '-a'
$readyBName = 'Local\AegisHistory-' + $id + '-b'
$releaseName = 'Local\AegisHistory-' + $id + '-release'
$readyA = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $readyAName)
$readyB = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $readyBName)
$release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $releaseName)
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
$processes = @()
try {
    $env:TEMP = $scratch; $env:TMP = $scratch
    foreach ($spec in @(@('a',$markerA,$markerB,$readyAName), @('b',$markerB,$markerA,$readyBName))) {
        $profile = Join-Path $root ('profile-' + $spec[0])
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $exe
        $start.Arguments = 'history-privacy-pair "{0}" {1} {2} "{3}" "{4}"' -f $profile,$spec[1],$spec[2],$spec[3],$releaseName
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Could not start a profile fixture.' }
        $processes += [pscustomobject]@{ Process=$process; Out=$process.StandardOutput.ReadToEndAsync(); Err=$process.StandardError.ReadToEndAsync() }
    }
    if (-not $readyA.WaitOne(30000) -or -not $readyB.WaitOne(30000)) { throw 'Both profile fixtures did not reach the barrier.' }
    $release.Set() | Out-Null
    foreach ($item in $processes) {
        if (-not $item.Process.WaitForExit(30000)) { throw 'A profile fixture did not shut down.' }
        $output = $item.Out.GetAwaiter().GetResult() + $item.Err.GetAwaiter().GetResult()
        if ($output.Contains($markerA) -or $output.Contains($markerB)) { throw 'A canary appeared in fixture output.' }
        if ($item.Process.ExitCode -ne 0 -or $output -match 'Check failed:|FATAL:|ASSERTION FAILED' -or
            $output -notmatch 'PASS: simultaneous profile history remains isolated' -or
            $output -notmatch 'PASS: history privacy fixture shutdown history-privacy-pair') {
            $output -split "`n" | Where-Object { $_ -match '^FAIL:|History privacy fixture failed:|Check failed:|FATAL:|ASSERTION FAILED' } |
                ForEach-Object { [regex]::Replace(([string]$_).Replace($markerA, '[redacted]').Replace($markerB, '[redacted]'), 'https?://\S+', '[redacted]') }
            throw "A profile fixture failed; exit code $($item.Process.ExitCode)."
        }
    }
    Write-Output 'PASS: two simultaneously running disposable profile processes observed only their own history'
    Write-Output "Disposable profile-pair root: $root"
} finally {
    $release.Set() | Out-Null
    foreach ($item in $processes) { if (-not $item.Process.HasExited) { $item.Process.WaitForExit(35000) | Out-Null }; $item.Process.Dispose() }
    $readyA.Dispose(); $readyB.Dispose(); $release.Dispose()
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp
}
