[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BuildRoot,
    [ValidateSet('Host','Toolchain','Build')][string] $Stage = 'Host',
    [string] $DepotToolsPath,
    [string] $ExpectedDepotToolsRevision,
    [string] $ChromiumSourcePath,
    [string] $CefSourcePath,
    [string] $ProvenanceReportPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'preflight-lib.ps1')

# No installation, system configuration or source-tree changes are made here.
# Output contains neither local paths nor remote URLs or credentials.
$o = @{
    WindowsX64=$null; ProcessX64=$null; MemoryBytes=$null; CpuCount=$null; FreeBytes=$null;
    LocalNtfs=$null; PathHasNoSpaces=$null; VsReady=$null; VsVersion=$null; SdkReady=$null;
    SdkVersion=$null; GitReady=$null; PythonReady=$null; WindowsLongPaths=$null; GitLongPaths=$null;
    DepotToolsFiles=$null; DepotToolsOrigin=$null; DepotToolsRevisionMatches=$null; DepotToolsClean=$null;
    DepotToolsRevision=$null; ChromiumRevisionMatches=$null; CefRevisionMatches=$null;
    ChromiumProvenance=$null; CefClean=$null; ChromiumRevision=$null; CefRevision=$null
}
try {
    $o.WindowsX64 = ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ceq 'X64')
    $o.ProcessX64 = ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -ceq 'X64')
    $o.CpuCount = [Environment]::ProcessorCount
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        try {
            if (-not ('AegisChromiumPreflightMemory' -as [type])) {
                Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AegisChromiumPreflightMemory {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)]
    public struct Status {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GlobalMemoryStatusEx(ref Status value);
}
'@
            }
            $memory = New-Object AegisChromiumPreflightMemory+Status
            $memory.Length = [Runtime.InteropServices.Marshal]::SizeOf($memory)
            if ([AegisChromiumPreflightMemory]::GlobalMemoryStatusEx([ref]$memory)) { $o.MemoryBytes = [long]$memory.TotalPhysical }
        } catch { }
    }
} catch { }

try {
    $root = [IO.Path]::GetFullPath($BuildRoot)
    $o.PathHasNoSpaces = -not $root.Contains(' ')
    if (Test-Path -LiteralPath $root -PathType Container) {
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
        $o.LocalNtfs = ($drive.DriveType -eq [IO.DriveType]::Fixed -and $drive.DriveFormat -ceq 'NTFS')
        $o.FreeBytes = $drive.AvailableFreeSpace
    } else { $o.LocalNtfs = $false }
} catch { }

try {
    $git = Get-Command git.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $gitVersion = & $git.Source --version 2>$null
    $o.GitReady = ($LASTEXITCODE -eq 0 -and ($gitVersion -join ' ') -match '^git version [0-9]')
    $long = & $git.Source config --get --bool core.longpaths 2>$null
    if ($LASTEXITCODE -eq 0) { $o.GitLongPaths = (($long -join '').Trim() -ceq 'true') }
} catch { $git = $null; $o.GitReady = $false }
try {
    $value = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop
    $o.WindowsLongPaths = ($value.LongPathsEnabled -eq 1)
} catch { }
try {
    $python = Get-Command python3.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $major = & $python.Source -I -c 'import sys; print(sys.version_info.major)' 2>$null
    $o.PythonReady = ($LASTEXITCODE -eq 0 -and ($major -join '').Trim() -ceq '3')
} catch { $o.PythonReady = $false }

try {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not $programFilesX86) { throw 'VSWHERE_ROOT_UNKNOWN' }
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { $o.VsReady = $false }
    else {
        $json = & $vswhere -all -products '*' -requires Microsoft.VisualStudio.Workload.NativeDesktop Microsoft.VisualStudio.Component.VC.ATLMFC Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json -utf8 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'VSWHERE_FAILED' }
        $instances = @(($json -join "`n") | ConvertFrom-Json)
        $ready = @($instances | Where-Object {
            $version = $null
            $_.isComplete -eq $true -and $_.isLaunchable -eq $true -and
                [version]::TryParse([string]$_.installationVersion, [ref]$version) -and $version -ge [version]'18.0'
        } | Sort-Object { [version]$_.installationVersion } -Descending)
        $o.VsReady = ($ready.Count -gt 0)
        if ($ready.Count -gt 0) { $o.VsVersion = [string]$ready[0].installationVersion }
    }
} catch { }

try {
    # The directory name alone is not proof of the required serviced SDK.
    $kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10 -ErrorAction Stop
    $kitRoot = [string]$kits.KitsRoot10
    $include = Test-Path -LiteralPath (Join-Path $kitRoot 'Include\10.0.28000.0\ucrt\stdio.h') -PathType Leaf
    $lib = Test-Path -LiteralPath (Join-Path $kitRoot 'Lib\10.0.28000.0\um\x64\kernel32.lib') -PathType Leaf
    $cdb = Join-Path $kitRoot 'Debuggers\x64\cdb.exe'
    $debugger = $false
    if (Test-Path -LiteralPath $cdb -PathType Leaf) {
        $debugVersion = $null
        $debugger = [version]::TryParse([string](Get-Item -LiteralPath $cdb).VersionInfo.FileVersion, [ref]$debugVersion) -and
            $debugVersion -ge [version]'10.0.26100.3323'
    }
    $products = @(Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match '^Windows (Software Development Kit|SDK)' -and $_.DisplayVersion })
    $versions = @($products | ForEach-Object {
        $v = $null
        if ([version]::TryParse([string]$_.DisplayVersion, [ref]$v) -and $v -ge [version]'10.0.28000.2270') { $v }
    } | Sort-Object -Descending)
    if ($versions.Count -gt 0) { $o.SdkVersion = $versions[0].ToString(); $o.SdkReady = ($include -and $lib -and $debugger) }
    elseif (-not $include -or -not $lib -or -not $debugger) { $o.SdkReady = $false }
    # Missing package-version evidence is inconclusive even if header paths exist.
} catch { }

function Get-GitCheckoutState([string] $Path, [string] $ExpectedRevision) {
    if (-not $git -or -not $Path -or -not (Test-Path -LiteralPath $Path -PathType Container)) { return $null }
    $head = & $git.Source -C $Path rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or ($head -join '').Trim() -cnotmatch '^[0-9a-f]{40}$') { return $null }
    $revision = ($head -join '').Trim()
    $changes = @(& $git.Source -C $Path status --porcelain=v1 --untracked-files=normal 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    return @{ revision=$revision; matches=($revision -ceq $ExpectedRevision); clean=($changes.Count -eq 0) }
}

if ($Stage -in @('Toolchain','Build')) {
    try {
        if ($DepotToolsPath -and (Test-Path -LiteralPath $DepotToolsPath -PathType Container)) {
            $o.DepotToolsFiles = @('gclient.bat','fetch.bat','autoninja.bat' | Where-Object {
                -not (Test-Path -LiteralPath (Join-Path $DepotToolsPath $_) -PathType Leaf)
            }).Count -eq 0
            if ($git) {
                $origin = & $git.Source -C $DepotToolsPath remote get-url origin 2>$null
                if ($LASTEXITCODE -eq 0) {
                    $o.DepotToolsOrigin = (($origin -join '').Trim() -cin @(
                        'https://chromium.googlesource.com/chromium/tools/depot_tools.git',
                        'https://chromium.googlesource.com/chromium/tools/depot_tools'))
                }
            }
            if ($ExpectedDepotToolsRevision -cmatch '^[0-9a-f]{40}$') {
                $depot = Get-GitCheckoutState $DepotToolsPath $ExpectedDepotToolsRevision
                if ($null -ne $depot) {
                    $o.DepotToolsRevision=$depot.revision; $o.DepotToolsRevisionMatches=$depot.matches
                    if ($depot.clean) { $o.DepotToolsClean=$true }
                    # Generated or unexpected untracked files require independent provenance review.
                }
            }
        } else { $o.DepotToolsFiles = $false }
    } catch { }
}
if ($Stage -eq 'Build') {
    try {
        $chromium = Get-GitCheckoutState $ChromiumSourcePath 'b859317bf11f6be47f9b7799ec690a0a42a1fb33'
        if ($null -ne $chromium) {
            $o.ChromiumRevision=$chromium.revision; $o.ChromiumRevisionMatches=$chromium.matches
        }
        $cef = Get-GitCheckoutState $CefSourcePath '14c5a089e8452874cb1a556dc3fde26ed2d87723'
        if ($null -ne $cef) { $o.CefRevision=$cef.revision; $o.CefRevisionMatches=$cef.matches; $o.CefClean=$cef.clean }
    } catch { }
    # Never accept a supplied or cached report as authority. A new verifier
    # process must reconstruct and compare the current checkout in this run.
    try {
        $verifier = Join-Path $PSScriptRoot 'verify_cef_patches.py'
        if ($python -and $ChromiumSourcePath -and $CefSourcePath -and $DepotToolsPath -and
            $ExpectedDepotToolsRevision -cmatch '^[0-9a-f]{40}$' -and $ProvenanceReportPath -and
            [IO.Path]::IsPathRooted($ProvenanceReportPath) -and
            -not (Test-Path -LiteralPath $ProvenanceReportPath) -and
            (Test-Path -LiteralPath $verifier -PathType Leaf)) {
            $o.ChromiumProvenance = $false
            $null = & $python.Source -I $verifier --chromium-source $ChromiumSourcePath `
                --cef-source $CefSourcePath --depot-tools $DepotToolsPath `
                --depot-tools-commit $ExpectedDepotToolsRevision --report $ProvenanceReportPath 2>$null
            $verifierExit = $LASTEXITCODE
            if (Test-Path -LiteralPath $ProvenanceReportPath -PathType Leaf) {
                $report = Get-Content -LiteralPath $ProvenanceReportPath -Raw | ConvertFrom-Json
                if ($verifierExit -eq 0 -and $report.status -ceq 'VERIFIED') {
                    $o.ChromiumProvenance = $true
                } elseif ($report.status -cin @('INCONCLUSIVE','BLOCKED','NOT_RUN')) {
                    $o.ChromiumProvenance = $null
                }
            }
        }
    } catch { $o.ChromiumProvenance = $false }
}
$checks = Get-AegisChromiumChecks -Observed $o -Stage $Stage
$qualified = Test-AegisChromiumQualification -Checks $checks
$result = [ordered]@{
    schema='aegis-chromium-build-preflight-v1'; stage=$Stage; qualified=$qualified
    observed=[ordered]@{ os_architecture=if ($null -eq $o.WindowsX64) {'UNKNOWN'} elseif ($o.WindowsX64) {'X64_WINDOWS'} else {'OTHER'};
        logical_cpus=$o.CpuCount; physical_memory_bytes=$o.MemoryBytes; free_bytes=$o.FreeBytes;
        visual_studio_version=$o.VsVersion; windows_sdk_version=$o.SdkVersion;
        depot_tools_revision=$o.DepotToolsRevision; chromium_revision=$o.ChromiumRevision; cef_revision=$o.CefRevision }
    checks=$checks; native_source_build='NOT_RUN'
}
$result | ConvertTo-Json -Depth 8
if (-not $qualified) { exit 2 }
