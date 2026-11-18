$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'trace-result-lib.ps1')
$checks = 0
function Assert-Rejected([scriptblock] $operation, [string] $name) {
    try { & $operation | Out-Null }
    catch { $script:checks++; Write-Output "PASS: $name"; return }
    throw "FAIL: $name"
}
function Parse([string] $json) { return ($json | ConvertFrom-Json -ErrorAction Stop) }
$observed = Parse '{"kind":"TRACE_PROCESS_CORRELATION","traceEvidence":{"Status":"OBSERVED","Reason":"LIVE_HANDLE_RETAINED_BEFORE_TRACE","CandidatePid":123,"CandidateCreationFileTimeUtc":134394786155818129,"Barrier":3,"RendererIdentity":"INCONCLUSIVE","SandboxEnforcement":"INCONCLUSIVE"}}'
$inconclusive = Parse '{"kind":"TRACE_PROCESS_CORRELATION","traceEvidence":{"Status":"INCONCLUSIVE","Reason":"CANDIDATE_NOT_RETAINED","CandidatePid":null,"CandidateCreationFileTimeUtc":null,"Barrier":0,"RendererIdentity":"INCONCLUSIVE","SandboxEnforcement":"INCONCLUSIVE"}}'
$other = [pscustomobject]@{kind='PROCESS_RESULT'}
if ((Get-P0TraceCorrelation -Decoded @($other,$observed)).Status -cne 'OBSERVED') { throw 'Observed record not accepted.' }
$checks++; Write-Output 'PASS: bounded observed trace record accepted without identity promotion'
if ((Get-P0TraceCorrelation -Decoded @($inconclusive)).Status -cne 'INCONCLUSIVE') { throw 'Inconclusive record not accepted.' }
$checks++; Write-Output 'PASS: inconclusive trace record accepted'
foreach ($numericType in @([int], [long])) {
    $typed = Parse ($observed | ConvertTo-Json -Depth 4 -Compress)
    $typed.traceEvidence.Barrier = [Convert]::ChangeType(3, $numericType)
    $typed.traceEvidence.CandidatePid = [Convert]::ChangeType(123, $numericType)
    if ((Get-P0TraceCorrelation -Decoded @($typed)).Status -cne 'OBSERVED') { throw "Integral type $numericType rejected." }
    $checks++; Write-Output "PASS: integral $numericType trace values accepted"
}
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($other) } 'missing trace record rejected'
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($observed,$inconclusive) } 'duplicate trace records rejected'
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($observed,[pscustomobject]@{kind='OTHER'}) } 'unknown record kind rejected'
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($observed,[pscustomobject]@{kind='process_result'}) } 'case-mismatched record kind rejected'
foreach ($field in @('RendererIdentity','SandboxEnforcement')) {
    $fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress)
    $fake.traceEvidence.$field = 'VERIFIED'
    Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } "$field promotion rejected"
}
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.CandidatePid = -1
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'invalid PID rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.CandidateCreationFileTimeUtc = 0
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'invalid creation time rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.Barrier = 602
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'out-of-range barrier rejected'
foreach ($badValue in @([double]3.0, [decimal]3, '3', $true, $null)) {
    $fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.Barrier = $badValue
    Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'non-integral or null barrier rejected'
}
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.CandidatePid = [double]123.0
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'floating-point PID rejected'
$fake = Parse ($inconclusive | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.CandidatePid = 123
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'inconclusive identity rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence | Add-Member -NotePropertyName Unexpected -NotePropertyValue 1
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'extra evidence field rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.PSObject.Properties.Remove('Barrier')
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'missing evidence field rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 4 -Compress); $fake.traceEvidence.Reason = ('x' * 101)
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'oversized reason rejected'
Write-Output "PASS: $checks synthetic trace-result checks; renderer identity and sandbox remain inconclusive"
