param(
    [Parameter(Mandatory = $true)] [string] $BrowserTestsExe
)

$ErrorActionPreference = 'Stop'
$testExecutable = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw 'Browser test executable is missing.' }
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required for disposable test profiles.' }
$runDirectory = Join-Path $env:RUNNER_TEMP ('aegis-concurrent-proxy-' + [guid]::NewGuid().ToString('N'))
$barrierDirectory = Join-Path $runDirectory 'barrier'
New-Item -ItemType Directory -Path $barrierDirectory -Force | Out-Null
$previousBarrier = $env:AEGIS_TEST_PROXY_BARRIER
try {
    $env:AEGIS_TEST_PROXY_BARRIER = $barrierDirectory
    $processes = @()
    foreach ($role in @('cef-a', 'cef-b')) {
        $profile = Join-Path $runDirectory $role
        $output = Join-Path $runDirectory ($role + '.out.log')
        $errors = Join-Path $runDirectory ($role + '.err.log')
        $arguments = @('proxy-probe', ('"{0}"' -f $profile))
        $process = Start-Process -FilePath $testExecutable -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $testExecutable) `
            -RedirectStandardOutput $output -RedirectStandardError $errors -WindowStyle Hidden -PassThru
        $processes += [pscustomobject]@{ Role = $role; Process = $process; Output = $output; Errors = $errors }
    }
    if ($processes[0].Process.Id -eq $processes[1].Process.Id) { throw 'Concurrent probes did not receive distinct process IDs.' }
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($entry in $processes) { $entry.Process.Refresh() }
        if (($processes | Where-Object { -not $_.Process.HasExited }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    foreach ($entry in $processes) {
        $entry.Process.Refresh()
        if (-not $entry.Process.HasExited) { throw "Concurrent $($entry.Role) fixture did not exit within three minutes." }
        $entry.Process.WaitForExit()
        $result = (Get-Content -LiteralPath $entry.Output -Raw) + "`n" + (Get-Content -LiteralPath $entry.Errors -Raw)
        Write-Output "Concurrent proxy fixture $($entry.Role):"
        Write-Output $result
        if ($entry.Process.ExitCode -ne 0 -or $result -match 'Check failed:' -or
            $result -notmatch 'PASS: both independent CEF proxy fixtures initialized before routing tests' -or
            $result -notmatch 'PASS: \d+ CEF proxy probe checks' -or
            $result -notmatch 'PASS: CEF proxy probe shutdown completed') {
            throw "Concurrent $($entry.Role) fixture failed or did not complete native shutdown."
        }
    }
    Write-Output 'PASS: two independent CEF proxy fixture processes completed concurrently'
}
finally {
    $env:AEGIS_TEST_PROXY_BARRIER = $previousBarrier
}
