# Reproduce Actions' LASTEXITCODE epilogue and test the isolated runner contract.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('aegis-ci-exit-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root -ErrorAction Stop
$hostExe = (Get-Process -Id $PID).Path
$runner = Join-Path $PSScriptRoot 'invoke-isolated-powershell-test.ps1'
$expected = Join-Path $root 'expected-negative.ps1'
$expectedContent = @'
$ErrorActionPreference = 'Stop'
$exe = (Get-Process -Id $PID).Path
& $exe -NoProfile -NonInteractive -Command 'exit 7'
if ($LASTEXITCODE -ne 7) { throw 'Expected native rejection did not occur.' }
Write-Output 'PASS: expected rejection checked'
'@
[IO.File]::WriteAllText($expected, $expectedContent)
$action = Join-Path $root 'actions-wrapper.ps1'
$quoted = $expected.Replace("'", "''")
[IO.File]::WriteAllText($action, "& '$quoted'`nif (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit `$LASTEXITCODE }`n")
$failure = Join-Path $root 'unexpected-failure.ps1'
[IO.File]::WriteAllText($failure, "throw 'INTENTIONAL_TEST_FAILURE'`n")
$nativeFailure = Join-Path $root 'native-failure.ps1'
[IO.File]::WriteAllText($nativeFailure, "exit 9`n")
$stderrSuccess = Join-Path $root 'stderr-success.ps1'
[IO.File]::WriteAllText($stderrSuccess, "[Console]::Error.WriteLine('intentional stderr'); Write-Output 'PASS'`n")
function Start-TestProcess([string] $script, [string] $label, [string] $testPath) {
    $stdout = Join-Path $root ($label + '.out'); $stderr = Join-Path $root ($label + '.err')
    $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $script))
    if ($testPath) { $arguments += @('-TestScript', ('"{0}"' -f $testPath)) }
    $process = Start-Process -FilePath $hostExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    return $process.ExitCode
}
if ((Start-TestProcess $action 'leaked' '') -ne 7) { throw 'Failed to reproduce stale native exit code.' }
if ((Start-TestProcess $runner 'expected' $expected) -ne 0) { throw 'Checked native rejection leaked from isolated test.' }
if ((Start-TestProcess $runner 'throw' $failure) -eq 0) { throw 'Unexpected exception was suppressed.' }
if ((Start-TestProcess $runner 'exit' $nativeFailure) -eq 0) { throw 'Unexpected native failure was suppressed.' }
if ((Start-TestProcess $runner 'stderr' $stderrSuccess) -ne 0) { throw 'Successful stderr-only test was rejected.' }
Write-Output "PASS: 5 CI process-exit checks; logs retained at $root"
