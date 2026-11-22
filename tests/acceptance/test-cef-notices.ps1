[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verifier = Join-Path $PSScriptRoot 'verify-cef-notices.ps1'
$credits = Join-Path $PSScriptRoot '../../third-party-licenses/CEF-CREDITS.html'
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$root = Join-Path $tempRoot ('aegis-cef-notice-controls-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$wrongCef = Join-Path $root 'libcef.dll'
$wrongCredits = Join-Path $root 'wrong-credits.html'
[IO.File]::WriteAllText($wrongCef, 'not a CEF binary')
[IO.File]::WriteAllText($wrongCredits, 'not upstream credits')

try {
    & $verifier -PublishDirectory $PublishDirectory -CreditsFile $credits
    foreach ($case in @(
        @{ root=$root; credits=$credits; expected='CEF_NOTICES_MISMATCH' },
        @{ root=$PublishDirectory; credits=$wrongCredits; expected='CEF_NOTICES_MISMATCH' },
        @{ root=$root; credits=(Join-Path $root 'missing.html'); expected='CEF_NOTICES_MISSING' }
    )) {
        $rejected = $false
        try { & $verifier -PublishDirectory $case.root -CreditsFile $case.credits }
        catch {
            if ($_.Exception.Message -like ($case.expected + '*')) { $rejected = $true }
            else { throw }
        }
        if (-not $rejected) { throw 'Invalid CEF notices control unexpectedly passed.' }
    }
    Write-Output 'PASS: matching CEF credits accepted; wrong CEF, wrong credits and missing credits rejected'
} finally {
    # Delete only these test-created files and the empty owned directory.
    foreach ($file in @($wrongCef, $wrongCredits)) {
        if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $false) }
}
