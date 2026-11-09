[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BootstrapPath,
    [Parameter(Mandatory)][string]$ClientPath,
    [Parameter(Mandatory)][string]$LibCefPath,
    [Parameter(Mandatory)][string]$BootstrapSha256,
    [Parameter(Mandatory)][string]$ClientSha256,
    [Parameter(Mandatory)][string]$LibCefSha256
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sandbox-preflight-lib.ps1')
try {
    $records = @(
        Get-P309fFileEvidence $BootstrapPath 'Bootstrap'
        Get-P309fFileEvidence $ClientPath 'Client'
        Get-P309fFileEvidence $LibCefPath 'LibCef'
    )
    $result = Test-P309fEvidenceSet $records @{ Bootstrap = $BootstrapSha256; Client = $ClientSha256; LibCef = $LibCefSha256 }
    $result | ConvertTo-Json -Compress
    # REVIEW_REQUIRED is not sandbox qualification and must not become a
    # successful gate merely because the files and caller-provided pins match.
    exit 2
}
catch {
    [pscustomobject]@{ Outcome = 'BLOCKED_PRECHECK'; Reason = $_.Exception.Message; SandboxEnforcement = 'NOT_RUN'; CefStartup = 'NOT_RUN' } | ConvertTo-Json -Compress
    exit 1
}
