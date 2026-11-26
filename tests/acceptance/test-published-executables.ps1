[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verifier = Join-Path $PSScriptRoot 'verify-published-executables.ps1'
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
& $verifier -PublishDirectory $source

# Every file below is created in this unique fixture and cleaned individually.
# A dummy host tests its name only. The subprocess and dump tool are genuine inputs.
$root = Join-Path ([IO.Path]::GetTempPath()) ('aegis-executable-control-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$hostFile = Join-Path $root 'AegisBrowser.exe'
$dumpFile = Join-Path $root 'createdump.exe'
$subprocess = Join-Path $root 'CefSharp.BrowserSubprocess.exe'
$unexpected = Join-Path $root 'unexpected.exe'
try {
    [IO.File]::WriteAllText($hostFile, 'fixture host')
    Copy-Item -LiteralPath (Join-Path $source 'createdump.exe') -Destination $dumpFile
    Copy-Item -LiteralPath (Join-Path $source 'CefSharp.BrowserSubprocess.exe') -Destination $subprocess
    & $verifier -PublishDirectory $root

    [IO.File]::Delete($subprocess)
    $rejected = $false
    try { & $verifier -PublishDirectory $root }
    catch {
        if ($_.Exception.Message -ceq 'EXECUTABLE_MISSING: CefSharp.BrowserSubprocess.exe') { $rejected = $true }
        else { throw }
    }
    if (-not $rejected) { throw 'Missing executable was not rejected.' }
    Write-Output 'PASS: missing subprocess executable is rejected'

    [IO.File]::WriteAllText($subprocess, 'modified fixture subprocess')
    $rejected = $false
    try { & $verifier -PublishDirectory $root }
    catch {
        if ($_.Exception.Message -ceq 'EXECUTABLE_MISMATCH: CefSharp.BrowserSubprocess.exe') { $rejected = $true }
        else { throw }
    }
    if (-not $rejected) { throw 'Replaced executable was not rejected.' }
    Write-Output 'PASS: replaced subprocess executable is rejected'

    [IO.File]::Delete($subprocess)
    Copy-Item -LiteralPath (Join-Path $source 'CefSharp.BrowserSubprocess.exe') -Destination $subprocess
    [IO.File]::WriteAllText($dumpFile, 'modified fixture dump tool')
    $rejected = $false
    try { & $verifier -PublishDirectory $root }
    catch {
        if ($_.Exception.Message -ceq 'EXECUTABLE_MISMATCH: createdump.exe') { $rejected = $true }
        else { throw }
    }
    if (-not $rejected) { throw 'Replaced dump executable was not rejected.' }
    Write-Output 'PASS: replaced runtime dump executable is rejected'

    [IO.File]::Delete($dumpFile)
    Copy-Item -LiteralPath (Join-Path $source 'createdump.exe') -Destination $dumpFile
    [IO.File]::WriteAllText($unexpected, 'unexpected fixture executable')
    $rejected = $false
    try { & $verifier -PublishDirectory $root }
    catch {
        if ($_.Exception.Message -ceq 'EXECUTABLE_UNEXPECTED: unexpected.exe') { $rejected = $true }
        else { throw }
    }
    if (-not $rejected) { throw 'Unexpected executable was not rejected.' }
    Write-Output 'PASS: unexpected executable is rejected'
}
finally {
    foreach ($file in @($hostFile, $dumpFile, $subprocess, $unexpected)) {
        if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $false) }
}
