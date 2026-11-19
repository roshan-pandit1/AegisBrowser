Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'build-baseline.ps1'
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'BASELINE_SCRIPT_PARSE_FAILED' }
$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$missing = Join-Path $root ('guard-test-' + [Guid]::NewGuid().ToString('N'))
$revision = '0123456789012345678901234567890123456789'
$passed = 0
function Reject([scriptblock]$action, [string]$message) {
    try { & $action | Out-Null } catch { $script:passed++; Write-Output "PASS: $message"; return }
    throw "FAIL: $message"
}
Reject { & $script -DownloadRoot $root -DepotToolsPath $root -ExpectedDepotToolsRevision $revision -EvidenceDirectory $missing } 'build requires explicit opt-in'
Reject { & $script -DownloadRoot 'relative' -DepotToolsPath $root -ExpectedDepotToolsRevision $revision -EvidenceDirectory $missing -AcknowledgeSourceBuild } 'relative source root rejected'
Reject { & $script -DownloadRoot 'C:\build&danger' -DepotToolsPath $root -ExpectedDepotToolsRevision $revision -EvidenceDirectory $missing -AcknowledgeSourceBuild } 'shell metacharacter in source path rejected'
Reject { & $script -DownloadRoot 'C:\build\..\other' -DepotToolsPath $root -ExpectedDepotToolsRevision $revision -EvidenceDirectory $missing -AcknowledgeSourceBuild } 'parent traversal in source path rejected'
Reject { & $script -DownloadRoot $root -DepotToolsPath $root -ExpectedDepotToolsRevision $revision -EvidenceDirectory $root -AcknowledgeSourceBuild } 'existing evidence path rejected'
Reject { & $script -DownloadRoot $root -DepotToolsPath $root -ExpectedDepotToolsRevision 'not-a-commit' -EvidenceDirectory $missing -AcknowledgeSourceBuild } 'invalid depot_tools revision rejected'
if (Test-Path -LiteralPath $missing) { throw 'FAIL: rejected build created an evidence directory' }
$passed++; Write-Output 'PASS: rejected builds did not create outputs'
Write-Output "PASS: $passed baseline build guard checks; no source build or native CEF test ran"
