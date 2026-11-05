# Execute one suite in its own process so an expected native failure cannot leak
# into the Actions shell. A nonzero suite process exit always fails this step.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $TestScript)
$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$hostExecutable = (Get-Process -Id $PID).Path
& $hostExecutable -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $TestScript
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) { throw "Offline test process failed with exit code $testExitCode`: $TestScript" }
