#Requires -Version 5.1
# Experimental CI only: this script builds the pinned unmodified source in a
# fresh runner-owned temporary directory. It does not replace browser packages.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$File, [Parameter(Mandatory)][string[]]$Arguments)
    Write-Host ("Executing: {0} {1}" -f $File, ($Arguments -join ' '))
    & $File @Arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Command failed with exit code ${code}: $File" }
}

if (-not $env:RUNNER_TEMP -or -not [IO.Path]::IsPathRooted($env:RUNNER_TEMP) -or
    -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'A valid GitHub runner temporary directory is required.'
}
if (-not [Environment]::Is64BitProcess) { throw 'The baseline requires an x64 process.' }

$sourceRevision = 'ec1027aa21e1af1217681a349104c15d29bbc21a'
$sdkVersion = '154.0.34'
$sdkSha512 = '92616C6776D5A4E445CCD8C4147C4F60B05BDD7802D8495CED42C700CD1D4C3A18C8B13CF9A5B2C48D92A2B753626B03DFEEB822C53E1550A92488156CE6266B'
$root = Join-Path $env:RUNNER_TEMP ('aegis-p309e-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$packages = Join-Path $source 'packages'
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$env:NUGET_PACKAGES = Join-Path $root 'nuget-global'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root 'nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$git = (Get-Command git.exe -ErrorAction Stop).Source
$nuget = (Get-Command nuget.exe -ErrorAction Stop).Source
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'vswhere is unavailable.' }
$vs = @(& $vswhere -version '[17.0,18.0)' -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CLI.Support -property installationPath)
if ($LASTEXITCODE -ne 0 -or $vs.Count -ne 1 -or -not $vs[0]) { throw 'Visual Studio 2022 C++/CLI component not found.' }
$msbuild = Join-Path $vs[0] 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) { throw 'MSBuild is unavailable.' }
$msvcRoot = Join-Path $vs[0] 'VC\Tools\MSVC'
$toolsets = @(Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction Stop | Sort-Object { [Version]$_.Name } -Descending)
if ($toolsets.Count -eq 0) { throw 'The MSVC toolset is unavailable.' }
$compiler = Join-Path $toolsets[0].FullName 'bin\Hostx64\x64\cl.exe'
$linker = Join-Path $toolsets[0].FullName 'bin\Hostx64\x64\link.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf) -or -not (Test-Path -LiteralPath $linker -PathType Leaf)) {
    throw 'The x64 MSVC compiler or linker is unavailable.'
}
$windowsSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include'
$windowsSdks = @(Get-ChildItem -LiteralPath $windowsSdkRoot -Directory -ErrorAction Stop | Select-Object -ExpandProperty Name)
if ($windowsSdks.Count -eq 0) { throw 'No Windows SDK include directories found.' }

Write-Host "EXPERIMENTAL_BASELINE_ROOT=$root"
Write-Host "SOURCE_REVISION=$sourceRevision CEF_SDK_VERSION=$sdkVersion"
Write-Host "VISUAL_STUDIO=$($vs[0])"
Write-Host "OS=$([Environment]::OSVersion.VersionString) PROCESS_ARCHITECTURE=$env:PROCESSOR_ARCHITECTURE"
Write-Host "MSVC_COMPILER=$compiler VERSION=$((Get-Item -LiteralPath $compiler).VersionInfo.FileVersion)"
Write-Host "MSVC_LINKER=$linker VERSION=$((Get-Item -LiteralPath $linker).VersionInfo.FileVersion)"
Write-Host "WINDOWS_SDK_INCLUDE_VERSIONS=$($windowsSdks -join ',')"
Invoke-Checked $dotnet @('--info')
Invoke-Checked $msbuild @('-version', '-nologo')
Invoke-Checked $nuget @('help')
$sdkList = @(& $dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdkList -match '^6\.0\.428\s')) { throw '.NET SDK 6.0.428 not found.' }

Invoke-Checked $git @('init', $source)
Invoke-Checked $git @('-C', $source, 'remote', 'add', 'origin', 'https://github.com/cefsharp/CefSharp.git')
Invoke-Checked $git @('-C', $source, 'fetch', '--depth', '1', 'origin', $sourceRevision)
Invoke-Checked $git @('-C', $source, 'checkout', '--detach', 'FETCH_HEAD')
$actualRevision = (& $git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualRevision -cne $sourceRevision) { throw 'Unexpected CefSharp source revision.' }

$packageConfigs = @(
    'CefSharp.Core.Runtime\packages.CefSharp.Core.Runtime.netcore.config',
    'CefSharp.BrowserSubprocess.Core\packages.CefSharp.BrowserSubprocess.Core.netcore.config'
)
foreach ($relative in $packageConfigs) {
    $config = Join-Path $source $relative
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw "Missing package configuration: $relative" }
    [xml]$xml = Get-Content -LiteralPath $config -Raw
    $cefSdk = @($xml.packages.package | Where-Object { $_.id -ceq 'cef.sdk' -and $_.version -ceq $sdkVersion })
    if ($cefSdk.Count -ne 1) { throw "Pinned cef.sdk $sdkVersion absent or duplicated in $relative" }
    Invoke-Checked $nuget @('restore', $config, '-PackagesDirectory', $packages, '-Source', 'https://api.nuget.org/v3/index.json', '-NoCache', '-NonInteractive')
}

$sdkDir = Join-Path $packages "cef.sdk.$sdkVersion"
$props = Join-Path $sdkDir 'build\cef.sdk.props'
if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { throw 'Pinned CEF SDK props are missing.' }
foreach ($relative in @('CEF\include\cef_life_span_handler.h', 'CEF\x64\Release\libcef.lib', 'CEF\x64\Release\VS2022\libcef_dll_wrapper.lib')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sdkDir $relative) -PathType Leaf)) { throw "Pinned CEF SDK input is missing: $relative" }
}
$sdkArchive = Join-Path $sdkDir "cef.sdk.$sdkVersion.nupkg"
if (-not (Test-Path -LiteralPath $sdkArchive -PathType Leaf)) { throw 'Pinned CEF SDK package archive is missing; hash verification cannot run.' }
$actualSdkSha512 = (Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash
if ($actualSdkSha512 -cne $sdkSha512) { throw 'Pinned CEF SDK package SHA-512 mismatch.' }
Write-Host "CEF_SDK_PACKAGE_SHA512=$actualSdkSha512"
Write-Host ("CEF_SDK_PACKAGE_SHA256={0}" -f (Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA256).Hash)
Write-Host 'The SHA-512 pin was compared with official NuGet metadata; this does not independently authenticate publisher identity.'

$managedProject = Join-Path $source 'CefSharp\CefSharp.netcore.csproj'
$nativeProject = Join-Path $source 'CefSharp.Core.Runtime\CefSharp.Core.Runtime.netcore.vcxproj'
foreach ($project in @($managedProject, $nativeProject)) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Pinned project is missing: $project" }
}
# Build only the managed reference and native binding. The upstream solution
# also contains a ref-assembly project that depends on Microsoft.DotNet.GenAPI
# from a separate feed and is not required for this baseline. Force MSBuild to
# use the same pinned SDK as dotnet rather than silently selecting another SDK.
$dotnetRoot = Split-Path -Parent $dotnet
$sdkPath = Join-Path $dotnetRoot 'sdk\6.0.428\Sdks'
if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) { throw 'Pinned .NET SDK resolver path is unavailable.' }
$env:DOTNET_ROOT = $dotnetRoot
$env:MSBuildSDKsPath = $sdkPath
$env:MSBuildEnableWorkloadResolver = 'false'
$solutionDir = $source.TrimEnd('\', '/') + '\'
$common = @('/p:Platform=x64', '/p:Configuration=Release', '/p:VisualStudioVersion=17.0', '/nologo', '/v:minimal')
Push-Location $source
try {
    Invoke-Checked $dotnet @('restore', $managedProject, '--source', 'https://api.nuget.org/v3/index.json')
    Invoke-Checked $dotnet @('build', $managedProject, '--no-restore', '-c', 'Release', '-p:Platform=x64', '-p:VisualStudioVersion=17.0')
    $managedOutput = Join-Path $source 'CefSharp\bin.netcore\x64\Release\net6.0\CefSharp.dll'
    if (-not (Test-Path -LiteralPath $managedOutput -PathType Leaf)) { throw 'Expected managed reference build output is missing.' }
    Write-Host ("MANAGED_REFERENCE_SHA256={0}" -f (Get-FileHash -LiteralPath $managedOutput -Algorithm SHA256).Hash)
    Invoke-Checked $msbuild (@($nativeProject, '/t:Build', '/p:BuildProjectReferences=false', "/p:SolutionDir=$solutionDir", '/m:1') + $common)
} finally { Pop-Location }

$runtime = Join-Path $source 'CefSharp.Core.Runtime\bin.netcore\x64\Release\CefSharp.Core.Runtime.dll'
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) { throw 'Expected native runtime output is missing.' }
Write-Host ("EXPERIMENTAL_RUNTIME_SHA256={0}" -f (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash)
Write-Host 'UNMODIFIED_NATIVE_BUILD: PASS (compile/link only; native load, CEF startup, callback safety and ABI are not established)'
Write-Host 'EXPERIMENTAL_BRIDGE_BUILD: NOT_RUN'
Write-Host 'OWNER_RELEASE_CONTRACT: UNSATISFIED'
