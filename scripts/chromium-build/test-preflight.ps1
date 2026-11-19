$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'preflight-lib.ps1')
$script:checksPassed = 0
function Assert-Check([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $script:checksPassed++
    Write-Output ('PASS: ' + $Name)
}
function New-Snapshot {
    return @{
        WindowsX64=$true; ProcessX64=$true; MemoryBytes=[long](64GB); CpuCount=16; FreeBytes=[long](500GB);
        LocalNtfs=$true; PathHasNoSpaces=$true; VsReady=$true; SdkReady=$true; GitReady=$true;
        WindowsLongPaths=$true; GitLongPaths=$true; PythonReady=$true; DepotToolsFiles=$true;
        DepotToolsOrigin=$true; DepotToolsRevisionMatches=$true; DepotToolsClean=$true;
        ChromiumRevisionMatches=$true; CefRevisionMatches=$true; ChromiumClean=$true; CefClean=$true
    }
}
function Qualified($Snapshot, [string] $Stage) {
    return Test-AegisChromiumQualification -Checks (Get-AegisChromiumChecks -Observed $Snapshot -Stage $Stage)
}
$s = New-Snapshot
Assert-Check (Qualified $s 'Host') 'adequate synthetic host qualifies'
Assert-Check (Qualified $s 'Toolchain') 'adequate synthetic toolchain qualifies'
Assert-Check (Qualified $s 'Build') 'adequate synthetic clean checkout qualifies'
foreach ($field in @('WindowsX64','ProcessX64','LocalNtfs','PathHasNoSpaces','VsReady','SdkReady','GitReady')) {
    $s = New-Snapshot; $s[$field] = $false
    Assert-Check (-not (Qualified $s 'Host')) ("host fails closed: $field")
}
foreach ($field in @('WindowsX64','MemoryBytes','FreeBytes','VsReady','SdkReady')) {
    $s = New-Snapshot; $s[$field] = $null
    Assert-Check (-not (Qualified $s 'Host')) ("unknown required evidence fails closed: $field")
}
$s = New-Snapshot; $s.MemoryBytes = 8GB - 1
Assert-Check (-not (Qualified $s 'Host')) 'below minimum RAM rejected'
$s = New-Snapshot; $s.FreeBytes = 100GB - 1
Assert-Check (-not (Qualified $s 'Host')) 'below minimum disk rejected'
$s = New-Snapshot; $s.MemoryBytes = 8GB; $s.FreeBytes = 100GB; $s.CpuCount = 2; $s.WindowsLongPaths = $null; $s.GitLongPaths = $false
Assert-Check (Qualified $s 'Host') 'minimum capacity qualifies despite advisory preferences'
foreach ($field in @('PythonReady','DepotToolsFiles','DepotToolsOrigin','DepotToolsRevisionMatches','DepotToolsClean')) {
    $s = New-Snapshot; $s[$field] = $false
    Assert-Check (-not (Qualified $s 'Toolchain')) ("toolchain fails closed: $field")
}
foreach ($field in @('ChromiumRevisionMatches','CefRevisionMatches','CefClean')) {
    $s = New-Snapshot; $s[$field] = $false
    Assert-Check (-not (Qualified $s 'Build')) ("build fails closed: $field")
}
$s = New-Snapshot; $s.ChromiumClean = $null
$checks = @(Get-AegisChromiumChecks -Observed $s -Stage 'Build')
$provenance = @($checks | Where-Object { $_.name -ceq 'chromium_checkout_provenance' })
Assert-Check ($provenance.Count -eq 1 -and $provenance[0].status -ceq 'INCONCLUSIVE' -and -not (Test-AegisChromiumQualification -Checks $checks)) 'unverified CEF patch provenance is inconclusive and blocks build'
Write-Output ("PASS: $script:checksPassed synthetic preflight checks; no native build performed")
