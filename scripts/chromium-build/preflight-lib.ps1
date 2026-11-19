Set-StrictMode -Version Latest

# Pure evaluation only. Unknown observations never satisfy a required check.
function Get-AegisChromiumChecks {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary] $Observed,
        [Parameter(Mandatory)][ValidateSet('Host','Toolchain','Build')][string] $Stage
    )
    $checks = [System.Collections.Generic.List[object]]::new()
    function Add-Check([string] $Name, $Value, [string] $Requirement, [bool] $Required) {
        $status = if ($null -eq $Value) { 'INCONCLUSIVE' } elseif ($Value -eq $true) { 'PASS' } else { 'FAIL' }
        $checks.Add([pscustomobject]@{ name = $Name; status = $status; required = $Required; requirement = $Requirement })
    }
    Add-Check 'windows_x64_os' $Observed.WindowsX64 'Native Windows x64 OS' $true
    Add-Check 'x64_process' $Observed.ProcessX64 'x64 PowerShell process' $true
    Add-Check 'memory_minimum' $(if ($null -eq $Observed.MemoryBytes) { $null } else { $Observed.MemoryBytes -ge 8GB }) 'At least 8 GiB physical RAM' $true
    Add-Check 'disk_minimum' $(if ($null -eq $Observed.FreeBytes) { $null } else { $Observed.FreeBytes -ge 100GB }) 'At least 100 GiB free' $true
    Add-Check 'local_ntfs' $Observed.LocalNtfs 'Existing local NTFS build root' $true
    Add-Check 'build_path_no_spaces' $Observed.PathHasNoSpaces 'Build root path contains no spaces' $true
    Add-Check 'visual_studio_components' $Observed.VsReady 'VS 2026 >=18.0 complete, launchable, x64 C++ desktop and ATL/MFC components' $true
    Add-Check 'windows_sdk' $Observed.SdkReady 'Windows SDK 10.0.28000.2270 plus x64 headers, libraries and debugger' $true
    Add-Check 'git' $Observed.GitReady 'Runnable Git' $true
    Add-Check 'preferred_cpu' $(if ($null -eq $Observed.CpuCount) { $null } else { $Observed.CpuCount -ge 16 }) '16 or more logical CPUs preferred' $false
    Add-Check 'preferred_memory' $(if ($null -eq $Observed.MemoryBytes) { $null } else { $Observed.MemoryBytes -ge 64GB }) '64 GiB physical RAM preferred' $false
    Add-Check 'preferred_disk' $(if ($null -eq $Observed.FreeBytes) { $null } else { $Observed.FreeBytes -ge 500GB }) '500 GiB free preferred' $false
    Add-Check 'windows_long_paths' $Observed.WindowsLongPaths 'Windows long paths enabled (advisory)' $false
    Add-Check 'git_long_paths' $Observed.GitLongPaths 'Git core.longpaths true (advisory)' $false
    if ($Stage -in @('Toolchain','Build')) {
        Add-Check 'python3' $Observed.PythonReady 'Runnable Python 3' $true
        Add-Check 'depot_tools_files' $Observed.DepotToolsFiles 'gclient, fetch and autoninja scripts present' $true
        Add-Check 'depot_tools_origin' $Observed.DepotToolsOrigin 'Official depot_tools origin' $true
        Add-Check 'depot_tools_revision' $Observed.DepotToolsRevisionMatches 'Exact operator-specified depot_tools commit' $true
        Add-Check 'depot_tools_clean' $Observed.DepotToolsClean 'No uncommitted tracked or untracked depot_tools files' $true
    }
    if ($Stage -eq 'Build') {
        Add-Check 'chromium_revision' $Observed.ChromiumRevisionMatches 'Chromium b859317bf11f6be47f9b7799ec690a0a42a1fb33' $true
        Add-Check 'cef_revision' $Observed.CefRevisionMatches 'CEF 14c5a089e8452874cb1a556dc3fde26ed2d87723' $true
        Add-Check 'chromium_checkout_provenance' $Observed.ChromiumClean 'Clean source, or separately verified expected CEF patch provenance' $true
        Add-Check 'cef_checkout_clean' $Observed.CefClean 'No unrelated CEF working-tree changes' $true
    }
    return ,$checks.ToArray()
}

function Test-AegisChromiumQualification {
    param([Parameter(Mandatory)][object[]] $Checks)
    return (@($Checks | Where-Object { $_.required -and $_.status -cne 'PASS' }).Count -eq 0)
}
