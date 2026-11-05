$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'p308c2-replay-ledger.ps1')
$root = Join-Path $env:TEMP ('p308c2-ledger-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root 'ledger.txt'
$checks = 0
function Reject([scriptblock] $action, [string] $expected) {
 $caught = $false
 try { & $action } catch { if ($_.Exception.Message -notlike "*$expected*") { throw }; $caught = $true }
 if (-not $caught) { throw "Expected rejection $expected" }; $script:checks++
}
$a = [guid]::NewGuid(); $b = [guid]::NewGuid(); $c = [guid]::NewGuid()
Reject { Add-P308c2ConsumedRun $path $a $b $c } 'Cannot find path'
Reject { Initialize-P308c2Ledger 'relative-ledger' } 'STATE_PATH_INVALID'
Reject { Initialize-P308c2Ledger ($path + ':alternate') } 'STATE_PATH_INVALID'
Initialize-P308c2Ledger $path; $checks++
Reject { Initialize-P308c2Ledger $path } 'already exists'
Add-P308c2ConsumedRun $path $a $b $c; $checks++
Reject { Add-P308c2ConsumedRun $path $a ([guid]::NewGuid()) ([guid]::NewGuid()) } 'REPLAY_DETECTED'
Reject { Add-P308c2ConsumedRun $path ([guid]::NewGuid()) $b ([guid]::NewGuid()) } 'REPLAY_DETECTED'
Reject { Add-P308c2ConsumedRun $path ([guid]::NewGuid()) ([guid]::NewGuid()) $c } 'REPLAY_DETECTED'
Reject { Add-P308c2ConsumedRun $path ([guid]::Empty) ([guid]::NewGuid()) ([guid]::NewGuid()) } 'SCHEMA_INVALID'
$lock = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Reject { Add-P308c2ConsumedRun $path ([guid]::NewGuid()) ([guid]::NewGuid()) ([guid]::NewGuid()) } 'being used' } finally { $lock.Dispose() }
# Simulated torn write is never repaired automatically.
[IO.File]::AppendAllText($path, '0123')
Reject { Add-P308c2ConsumedRun $path ([guid]::NewGuid()) ([guid]::NewGuid()) ([guid]::NewGuid()) } 'LEDGER_INVALID'
Write-Output "PASS: $checks replay-ledger checks; state retained at $root"
