[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$CreditsFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# These pins bind the CREDITS.html from the pinned official CEF 154.0.34
# binary distribution to the actual libcef being packaged. They are not a
# complete third-party inventory or legal approval for redistribution.
$expectedCef = 'E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7'
$expectedCredits = 'EB5A0B6300C58BB8316A66B3E935D55B1456F8F1CF225C979B03D42AFA878C4B'
$cef = Join-Path $PublishDirectory 'libcef.dll'
foreach ($file in @($cef, $CreditsFile)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'CEF_NOTICES_MISSING: required file is absent.' }
}
if ((Get-FileHash -LiteralPath $cef -Algorithm SHA256).Hash -cne $expectedCef) {
    throw 'CEF_NOTICES_MISMATCH: packaged libcef.dll does not match the pinned CEF credits distribution.'
}
if ((Get-FileHash -LiteralPath $CreditsFile -Algorithm SHA256).Hash -cne $expectedCredits) {
    throw 'CEF_NOTICES_MISMATCH: Chromium credits bytes do not match the pinned CEF distribution.'
}
Write-Output 'CEF_NOTICES_PIN_MATCH: PASS (not a complete redistribution review)'
