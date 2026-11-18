$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'trace-result-lib.ps1')
$checks = 0
function Assert-Rejected([scriptblock] $operation, [string] $name) {
    try { & $operation | Out-Null }
    catch { $script:checks++; Write-Output "PASS: $name"; return }
    throw "FAIL: $name"
}
function Parse([string] $json) { return ($json | ConvertFrom-Json -ErrorAction Stop) }
$observed = Parse '{"kind":"TRACE_PROCESS_CORRELATION","traceEvidence":{"Status":"OBSERVED","Reason":"LIVE_HANDLE_RETAINED_BEFORE_TRACE","CandidatePid":123,"CandidateCreationFileTimeUtc":134394786155818129,"Barrier":3,"RendererIdentity":"INCONCLUSIVE","SandboxEnforcement":"INCONCLUSIVE","Token":{"Status":"OBSERVED","Reason":"HANDLE_IDENTITY_AND_TOKEN_SNAPSHOT","ProcessId":123,"CreationFileTimeUtc":134394786155818129,"IntegrityRid":0,"IsAppContainer":false,"DenyOnlyGroupCount":12,"RestrictingSidCount":1,"HasRestrictingSids":true,"PrivilegeCount":0,"EnabledPrivilegeCount":0,"InAnyJob":true,"Win32kSystemCallsDisabled":true,"SystemCallMitigationFlags":1,"ProcessRole":"NOT_ESTABLISHED","SandboxEnforcement":"INCONCLUSIVE"}}}'
$inconclusive = Parse '{"kind":"TRACE_PROCESS_CORRELATION","traceEvidence":{"Status":"INCONCLUSIVE","Reason":"CANDIDATE_NOT_RETAINED","CandidatePid":null,"CandidateCreationFileTimeUtc":null,"Barrier":0,"RendererIdentity":"INCONCLUSIVE","SandboxEnforcement":"INCONCLUSIVE","Token":null}}'
$other = [pscustomobject]@{kind='PROCESS_RESULT'}
if ((Get-P0TraceCorrelation -Decoded @($other,$observed)).Status -cne 'OBSERVED') { throw 'Observed record not accepted.' }
$checks++; Write-Output 'PASS: bounded observed trace record accepted without identity promotion'
if ((Get-P0TraceCorrelation -Decoded @($inconclusive)).Status -cne 'INCONCLUSIVE') { throw 'Inconclusive record not accepted.' }
$checks++; Write-Output 'PASS: inconclusive trace record accepted'
$unknownRestriction = Parse ($observed | ConvertTo-Json -Depth 5 -Compress)
$unknownRestriction.traceEvidence.Token.HasRestrictingSids = $null
if ($null -ne (Get-P0TraceCorrelation -Decoded @($unknownRestriction)).Token.HasRestrictingSids) { throw 'Unknown restriction was coerced.' }
$checks++; Write-Output 'PASS: failed restricting-SID API query remains unknown'
$inconclusiveToken = Parse ($observed | ConvertTo-Json -Depth 5 -Compress)
$inconclusiveToken.traceEvidence.Token = Parse '{"Status":"INCONCLUSIVE","Reason":"WINDOWS_API_ERROR_5","ProcessId":null,"CreationFileTimeUtc":null,"IntegrityRid":null,"IsAppContainer":null,"DenyOnlyGroupCount":null,"RestrictingSidCount":null,"HasRestrictingSids":null,"PrivilegeCount":null,"EnabledPrivilegeCount":null,"InAnyJob":null,"Win32kSystemCallsDisabled":null,"SystemCallMitigationFlags":null,"ProcessRole":"NOT_ESTABLISHED","SandboxEnforcement":"INCONCLUSIVE"}'
if ((Get-P0TraceCorrelation -Decoded @($inconclusiveToken)).Token.Status -cne 'INCONCLUSIVE') { throw 'Inconclusive token not accepted.' }
$checks++; Write-Output 'PASS: unavailable token remains inconclusive without observation data'
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
    $fake.traceEvidence.$field = $true
    Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } "$field Boolean rejected without string coercion"
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
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token = $null
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'missing token evidence rejected'
$fake = Parse ($inconclusive | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token = $observed.traceEvidence.Token
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'token on inconclusive candidate rejected'
foreach ($field in @('ProcessRole','SandboxEnforcement')) {
    $fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.$field = 'VERIFIED'
    Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } "token $field promotion rejected"
    $fake.traceEvidence.Token.$field = $true
    Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } "token $field Boolean rejected without string coercion"
}
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.ProcessId = 124
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'token PID mismatch rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.CreationFileTimeUtc = [long]134394786155818130
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'token creation time mismatch rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.IntegrityRid = [double]0.0
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'floating token count rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.IsAppContainer = 'false'
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'string token Boolean rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.InAnyJob = 1
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'invalid optional token Boolean rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.SystemCallMitigationFlags = -1
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'invalid token mitigation flags rejected'
$fake = Parse ($inconclusiveToken | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.IntegrityRid = 0
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'inconclusive token data rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token | Add-Member -NotePropertyName Unexpected -NotePropertyValue 1
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'extra token field rejected'
$fake = Parse ($observed | ConvertTo-Json -Depth 5 -Compress); $fake.traceEvidence.Token.PSObject.Properties.Remove('IntegrityRid')
Assert-Rejected { Get-P0TraceCorrelation -Decoded @($fake) } 'missing token field rejected'
Write-Output "PASS: $checks synthetic trace-result checks; renderer identity and sandbox remain inconclusive"
