# Offline fixture data exercises the existing receiver validator, not packet enforcement.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'private-network-evidence.ps1')
$run = [guid]::NewGuid(); $nonce = [guid]::NewGuid()
$record = [ordered]@{
    formatVersion = 2; runId = $run.ToString('N'); overallStatus = 'INCONCLUSIVE'
    classification = 'ALLOWED_AND_INDEPENDENTLY_OBSERVED'; transactionId = $nonce.ToString('N')
    receiverAddress = '10.9.8.2'; receiverPort = 47123; remoteAddress = '10.9.8.1'; remotePort = 50000
    observedUtc = [DateTime]::UtcNow.ToString('O'); transport = 'IPv4/TCP'; observedMatches = 1
    duplicateObservationWindowSeconds = 2; duplicateCoverage = 'bounded-window-only'
    processAttribution = 'unverified'; wfpDecision = 'not-observed'
}
function Validate($item) {
    Assert-OrdinaryReceiverEvidence -Evidence $item -RunId $run -Nonce $nonce -ReceiverAddress '10.9.8.2' -ClientAddress '10.9.8.1' -Port 47123
}
Validate ($record | ConvertTo-Json | ConvertFrom-Json)
$cases = @(
    @{field='formatVersion'; value=1}, @{field='runId'; value=[guid]::NewGuid().ToString('N')},
    @{field='transactionId'; value=[guid]::NewGuid().ToString('N')}, @{field='receiverAddress'; value='10.9.8.3'},
    @{field='remoteAddress'; value='10.9.8.3'}, @{field='receiverPort'; value=47124},
    @{field='observedUtc'; value='invalid'}, @{field='observedMatches'; value=2},
    @{field='transport'; value='IPv6/TCP'}, @{field='wfpDecision'; value='blocked'},
    @{field='wfpDecision'; value=$null}, @{field='classification'; value='BLOCKED_BY_INDEPENDENT_POLICY_EVIDENCE'},
    @{field='observedMatches'; value=$true}, @{field='remotePort'; value='50000'},
    @{field='duplicateCoverage'; value='complete'}, @{field='overallStatus'; value='VERIFIED_ALLOWED'}
)
foreach ($case in $cases) {
    $item = $record | ConvertTo-Json | ConvertFrom-Json
    $item.($case.field) = $case.value
    $rejected = $false
    try { Validate $item } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted inconsistent evidence: $($case.field)" }
}
Write-Output "PASS: 1 matching and $($cases.Count) rejected offline receiver evidence cases; no packet decision inferred."
