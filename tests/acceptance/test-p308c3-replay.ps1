# Offline contention and interrupted-write checks. Uses only synthetic local files and owned jobs.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1')
$root = Join-Path $env:TEMP ('p308c3-ledger-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$path = Join-Path $root 'ledger'
Initialize-P308c2Ledger $path
$lib = Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1'
$run = [guid]::NewGuid(); $one = [guid]::NewGuid(); $two = [guid]::NewGuid()
$jobs = @()
try {
 foreach ($i in 1..2) {
  $jobs += Start-Job -ArgumentList $lib,$path,$run,$one,$two -ScriptBlock {
   param($lib,$path,$run,$one,$two)
   . $lib
   try { Add-P308c2ConsumedRun $path $run $one $two; 'COMMITTED' }
   catch { if ($_.Exception.Message -eq 'REPLAY_DETECTED') { 'REPLAY' } elseif ($_.Exception -is [IO.IOException] -or $_.Exception.InnerException -is [IO.IOException]) { 'LOCKED' } else { throw } }
  }
 }
 $null = $jobs | Wait-Job -Timeout 30
 if (@($jobs | Where-Object State -ne 'Completed').Count -ne 0) { throw 'Concurrent worker did not complete' }
 $outcomes = @($jobs | Receive-Job)
 if (@($outcomes | Where-Object { $_ -eq 'COMMITTED' }).Count -ne 1 -or @($outcomes | Where-Object { $_ -in @('REPLAY','LOCKED') }).Count -ne 1) { throw 'Unexpected concurrent outcomes' }
 $replay = $false
 try { Add-P308c2ConsumedRun $path $run $one $two } catch { if ($_.Exception.Message -eq 'REPLAY_DETECTED') { $replay = $true } else { throw } }
 if (-not $replay) { throw 'Concurrent consumption was not durable' }
 # Simulate a crash leaving a non-terminated append; verifier must refuse it, not repair it.
 $s = [IO.File]::Open($path,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try { $partial = [Text.Encoding]::ASCII.GetBytes('0123'); $s.Write($partial,0,$partial.Length); $s.Flush($true) } finally { $s.Dispose() }
 $corrupt = $false
 try { Add-P308c2ConsumedRun $path ([guid]::NewGuid()) ([guid]::NewGuid()) ([guid]::NewGuid()) } catch { if ($_.Exception.Message -eq 'LEDGER_INVALID') { $corrupt = $true } else { throw } }
 if (-not $corrupt) { throw 'Interrupted write was accepted' }
 Write-Output "PASS: 3 replay concurrency and interrupted-write checks; state retained at $root"
} finally { foreach ($job in $jobs) { if ($job.State -eq 'Running') { Stop-Job $job }; Remove-Job $job -Force -ErrorAction SilentlyContinue } }
