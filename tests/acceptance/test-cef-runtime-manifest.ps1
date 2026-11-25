[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verifier = Join-Path $PSScriptRoot 'verify-cef-runtime-manifest.ps1'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../third-party-licenses/CEF-RUNTIME-HASHES.json') -Raw | ConvertFrom-Json
& $verifier -PublishDirectory $PublishDirectory

$root = Join-Path ([IO.Path]::GetTempPath()) ('aegis-cef-component-control-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$first = [string]$manifest.files[0].path
if ($first -match '[/\\]') { throw 'The first component control must be a root file.' }
$wrong = Join-Path $root $first
try {
    [IO.File]::WriteAllText($wrong, 'not the signed package component')
    $rejected = $false
    try { & $verifier -PublishDirectory $root }
    catch {
        if ($_.Exception.Message -like 'CEF_COMPONENT_MISMATCH:*') { $rejected = $true }
        else { throw }
    }
    if (-not $rejected) { throw 'Tampered CEF component control unexpectedly passed.' }
    Write-Output 'PASS: tampered CEF component is rejected'
}
finally {
    if ([IO.File]::Exists($wrong)) { [IO.File]::Delete($wrong) }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $false) }
}
