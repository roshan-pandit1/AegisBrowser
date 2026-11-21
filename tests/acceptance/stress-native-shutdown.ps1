param(
    [Parameter(Mandatory = $true)][string] $BrowserTestsExe,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [ValidateRange(1, 20)][int] $Runs = 5
)
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetFileName($exe) -ine 'AegisBrowser.BrowserTests.exe') {
    throw 'The built browser integration test executable is required.'
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new disposable evidence directory.' }
$null = New-Item -ItemType Directory -Path $evidence
$results = [Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $root = Join-Path $evidence ('profile-' + $run)
    $stdout = Join-Path $evidence ('stdout-' + $run + '.txt')
    $stderr = Join-Path $evidence ('stderr-' + $run + '.txt')
    $process = Start-Process -FilePath $exe -ArgumentList @('hwnd-repro', ('"{0}"' -f $root)) `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $timedOut = -not $process.WaitForExit(120000)
    if ($timedOut) {
        # This is an owned disposable fixture process on a disposable CI runner.
        # Timeout remains a failure; killing the fixture is not successful shutdown.
        try { $process.Kill() } catch [InvalidOperationException] { }
        if (-not $process.WaitForExit(5000)) { throw 'Timed-out test process could not be stopped.' }
    }
    $process.Refresh()
    $exitCode = $process.ExitCode
    $output = ''
    if (Test-Path -LiteralPath $stdout) {
        if ((Get-Item -LiteralPath $stdout).Length -gt 4000000) { throw 'Fixture stdout exceeded the evidence limit.' }
        $output = [IO.File]::ReadAllText($stdout)
    }
    if ((Test-Path -LiteralPath $stderr) -and (Get-Item -LiteralPath $stderr).Length -gt 1000000) { throw 'Fixture stderr exceeded the evidence limit.' }
    $cycleMatches = @(1..5 | ForEach-Object {
        [regex]::Matches($output, ('(?m)^PASS: native window candidate reproduction cycle {0} closed normally\s*$' -f $_)).Count
    })
    $cycles = @($cycleMatches | Where-Object { $_ -eq 1 }).Count
    $shutdown = $output -match '(?m)^PASS: native window candidate reproduction completed and CEF shut down\s*$'
    $dropped = $output -match 'kind=TEST_MAIN_RETURN detail=result=0,dropped=0'
    $passed = -not $timedOut -and $exitCode -eq 0 -and $cycles -eq 5 -and $shutdown -and $dropped
    $record = [ordered]@{ run = $run; exitCode = $exitCode; timedOut = $timedOut; completedCycles = $cycles; cefShutdownMarker = $shutdown; traceComplete = $dropped; status = $(if ($passed) { 'PASS' } else { 'FAILED' }) }
    $results.Add([pscustomobject]$record)
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'results.json') -Encoding UTF8
    Write-Output ($record | ConvertTo-Json -Compress)
    if (-not $passed) {
        # Logs belong to controlled fixture profiles. Replace the machine's
        # temporary root before they are published as a failure artifact.
        foreach ($file in @($stdout, $stderr)) {
            if (Test-Path -LiteralPath $file) {
                $text = [IO.File]::ReadAllText($file).Replace($evidence, '<TEST_ROOT>')
                foreach ($sensitivePath in @($env:RUNNER_TEMP, $env:USERPROFILE)) {
                    if (-not [string]::IsNullOrWhiteSpace($sensitivePath)) { $text = $text.Replace($sensitivePath, '<LOCAL_PATH>') }
                }
                [IO.File]::WriteAllText((Join-Path $evidence ('sanitized-' + [IO.Path]::GetFileName($file))), $text)
            }
        }
        throw "Native shutdown stress run $run failed; results and sanitized fixture output retained."
    }
    # Retain only structured results on successful iterations, not profile data.
    Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue
}
Write-Output "PASS: $Runs independent native shutdown processes completed without retry."
