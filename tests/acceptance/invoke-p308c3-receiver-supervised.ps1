# Disposable VM B only: supervises the receiver so completion includes its actual OS exit code.
param(
    [Parameter(Mandatory=$true)][string]$BindAddress,
    [Parameter(Mandatory=$true)][string]$ExpectedClientAddress,
    [Parameter(Mandatory=$true)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory=$true)][guid]$RunId,
    [Parameter(Mandatory=$true)][guid]$NormalNonce,
    [Parameter(Mandatory=$true)][guid]$ContainedNonce,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [Parameter(Mandatory=$true)][string]$SigningIdentity,
    [ValidateRange(10,600)][int]$TimeoutSeconds=300,
    [switch]$AcknowledgeDisposableIsolatedNetwork
)
$ErrorActionPreference='Stop'
if (-not $AcknowledgeDisposableIsolatedNetwork -or $env:AEGIS_DISPOSABLE_VM_NETWORK_TEST -cne 'I_UNDERSTAND') { throw 'Explicit disposable isolated-VM acknowledgement and AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND are required.' }
. (Join-Path $PSScriptRoot 'cross-vm-preflight-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c3-fixture-lib.ps1')
. (Join-Path $PSScriptRoot 'p308c2-file-lib.ps1')
Assert-CrossVmEndpoints -LocalAddress $ExpectedClientAddress -PeerAddress $BindAddress -Port $Port
if ($RunId -eq [guid]::Empty -or $NormalNonce -eq [guid]::Empty -or $ContainedNonce -eq [guid]::Empty -or @($RunId,$NormalNonce,$ContainedNonce | Select-Object -Unique).Count -ne 3) { throw 'Distinct nonempty run and transaction IDs required.' }
# Invoke a new PowerShell process and use its exit status, not an assertion made before exit.
$hostExe=(Get-Process -Id $PID).Path
$receiver=Join-Path $PSScriptRoot 'receive-private-network-control.ps1'
& $hostExe -NoProfile -ExecutionPolicy Bypass -File $receiver -BindAddress $BindAddress -ExpectedClientAddress $ExpectedClientAddress -Port $Port -RunId $RunId.ToString('D') -NormalNonce $NormalNonce.ToString('D') -ContainedNonce $ContainedNonce.ToString('D') -EvidenceDirectory $EvidenceDirectory -TimeoutSeconds $TimeoutSeconds -SigningIdentity $SigningIdentity -AcknowledgeDisposableIsolatedNetwork
$receiverExit=$LASTEXITCODE
if ($receiverExit -ne 0) { throw "RECEIVER_INCOMPLETE: owned receiver exited $receiverExit; no successful completion record signed." }
$summaryBytes=Read-P308c2EvidenceFile -Path (Join-Path $EvidenceDirectory 'receiver-local-completion.json') -MaximumBytes 2048
$summary=[Text.Encoding]::UTF8.GetString($summaryBytes) | ConvertFrom-Json
if ($summary.runId -cne $RunId.ToString('N') -or $summary.clientAddress -cne $ExpectedClientAddress -or $summary.receiverAddress -cne $BindAddress -or $summary.port -ne $Port -or $summary.observedCount -ne 2) { throw 'RECEIVER_INCOMPLETE: receiver did not report both expected observations.' }
$completed=[DateTime]::UtcNow.ToString('O')
$fields=New-P308c3FixtureFields -Role VmB -Type completion -Stage Complete -Run $RunId.ToString('N') -Transaction '-' -Client $ExpectedClientAddress -Receiver $BindAddress -Port $Port -ObservedUtc $completed
$fields.observationCount='2'; $fields.receiverExit='0'; $fields.receiverError='none'; $fields.duplicateStatus='none'
$fields.listenerStartedUtc=ConvertTo-P308c3UtcString $summary.listenerStartedUtc; $fields.windowStartUtc=$fields.listenerStartedUtc; $fields.windowEndUtc=ConvertTo-P308c3UtcString $summary.completedUtc
Write-P308c3FixtureRecord -Fields $fields -Role VmB -Identity $SigningIdentity -OutputPrefix (Join-Path $EvidenceDirectory 'receiver-completion')
Write-Output 'Receiver completed successfully and completion claim was signed; this is not WFP or OS process-to-flow attribution.'
