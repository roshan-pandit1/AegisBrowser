[CmdletBinding()]
param()

# Creates an offline, framework-dependent diagnostic fixture. It does not run it.
# Run only after source review. All output goes to a new workspace artifacts directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$artifacts = Join-Path $repository 'artifacts'
$package = Join-Path $artifacts 'webview2-v4-inputs\microsoft.web.webview2.1.0.4370.26.nupkg'
$packageHash = '1C0DACAC84B70109B3FD42F90C53A0BA942ABC3A87590D7A4E0CCD86E4A06CF4'
$dotnet = 'C:\Program Files\dotnet\x64\dotnet.exe'
$compiler = 'C:\Program Files\dotnet\x64\sdk\10.0.404\Roslyn\bincore\csc.dll'
$coreRefs = 'C:\Program Files\dotnet\x64\packs\Microsoft.NETCore.App.Ref\10.0.15\ref\net10.0'
$desktopRefs = 'C:\Program Files\dotnet\x64\packs\Microsoft.WindowsDesktop.App.Ref\10.0.15\ref\net10.0'
$sourceFiles = @(
    (Join-Path $PSScriptRoot 'Program.cs'),
    (Join-Path $repository 'experiments\webview2-feasibility\ProfileRootPolicy.cs')
)

foreach ($required in @($package, $dotnet, $compiler, $coreRefs, $desktopRefs) + $sourceFiles) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required input is missing: $required" }
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $packageHash) {
    throw 'WebView2 package hash mismatch; no output was created.'
}
$head = (& git -C $repository rev-parse --verify HEAD)
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify source base commit.' }

$runDir = Join-Path $artifacts ('webview2-correlation-build-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runDir) { throw 'Output directory already exists.' }
$bundle = Join-Path $runDir 'bundle'
[void](New-Item -ItemType Directory -Path $bundle)
$sourceDir = Join-Path $bundle 'source'
[void](New-Item -ItemType Directory -Path $sourceDir)
$sourceSnapshots = @($sourceFiles | ForEach-Object {
    $destination = Join-Path $sourceDir ([IO.Path]::GetFileName($_))
    Copy-Item -LiteralPath $_ -Destination $destination
    $destination
})
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CorrelationFixture.csproj') -Destination (Join-Path $sourceDir 'CorrelationFixture.csproj')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $sourceDir 'build-correlation-fixture.ps1')
$env:DOTNET_CLI_HOME = Join-Path $runDir 'dotnet-home'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @(
    @{ Path = 'lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.dll'; Output = 'Microsoft.Web.WebView2.Core.dll'; Sha256 = '43319ABB99F77A74CC7E05C987E8E3DC9029BFD95BB2BBB9EEF61C4976998313' },
    @{ Path = 'lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.WinForms.dll'; Output = 'Microsoft.Web.WebView2.WinForms.dll'; Sha256 = '8419329E63A8F20C3EBD7ACB45895779D252C06C47A325EB5D21A00C78441FB5' },
    @{ Path = 'runtimes/win-x64/native/WebView2Loader.dll'; Output = 'WebView2Loader.dll'; Sha256 = '37996D81F0F3A13E93549F74F722FCE25D70B00E93E4A470C34B75D7231BFD1D' },
    @{ Path = 'LICENSE.txt'; Output = 'WebView2-LICENSE.txt'; Sha256 = '0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55' }
)
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $entries) {
        $matches = @($archive.Entries | Where-Object { $_.FullName -ceq $entry.Path })
        if ($matches.Count -ne 1) { throw "Missing or duplicate package entry: $($entry.Path)" }
        $destination = Join-Path $bundle $entry.Output
        $inputStream = $matches[0].Open()
        try {
            $outputStream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.Sha256) {
            throw "Package entry hash mismatch: $($entry.Path)"
        }
    }
} finally { $archive.Dispose() }

# Direct Roslyn compilation imports no NuGet/MSBuild package targets and does
# not start the fixture. Generated usings match the project's WinForms defaults.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$globalUsings = Join-Path $runDir 'GlobalUsings.g.cs'
[IO.File]::WriteAllText($globalUsings, @'
global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows.Forms;
'@, $utf8)
$desktopReferenceFiles = @(Get-ChildItem -LiteralPath $desktopRefs -Filter '*.dll' -File)
$desktopReferenceNames = @{}
foreach ($reference in $desktopReferenceFiles) { $desktopReferenceNames[$reference.Name] = $true }
$references = @(@(Get-ChildItem -LiteralPath $coreRefs -Filter '*.dll' -File |
    Where-Object { -not $desktopReferenceNames.ContainsKey($_.Name) }) + $desktopReferenceFiles) |
    Sort-Object FullName
$referenceHashes = @($references | ForEach-Object {
    [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$rsp = Join-Path $runDir 'compiler.rsp'
$response = @('/nostdlib+', '/target:winexe', '/platform:x64', '/nullable:enable',
    '/langversion:latest', '/deterministic+', '/optimize+',
    ('/out:"' + (Join-Path $bundle 'CorrelationFixture.dll') + '"'))
$response += @($references | ForEach-Object { '/reference:"' + $_.FullName + '"' })
$response += @($entries | Where-Object { $_.Output -like 'Microsoft.Web.WebView2.*.dll' } |
    ForEach-Object { '/reference:"' + (Join-Path $bundle $_.Output) + '"' })
$response += @($sourceSnapshots + $globalUsings | ForEach-Object { '"' + $_ + '"' })
[IO.File]::WriteAllLines($rsp, [string[]]$response, $utf8)
$compilerOutput = & $dotnet $compiler /noconfig "@$rsp" 2>&1 | Out-String
$compilerExit = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $runDir 'compiler-output.txt'), $compilerOutput, $utf8)
if ($compilerExit -ne 0) { throw "Compiler failed with exit $compilerExit; inspect compiler-output.txt in $runDir." }

$runtimeConfig = @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.15" },
      { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.15" }
    ]
  }
}
'@
[IO.File]::WriteAllText((Join-Path $bundle 'CorrelationFixture.runtimeconfig.json'), $runtimeConfig, $utf8)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $bundle 'README.md')
Copy-Item -LiteralPath (Join-Path $runDir 'compiler-output.txt') -Destination (Join-Path $bundle 'compiler-output.txt')
$inventory = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($bundle.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{
    schema = 'aegis-webview2-correlation-fixture-v1'
    source_base_commit = $head
    source_files = @($sourceSnapshots | ForEach-Object { [ordered]@{ path = 'source/' + [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
    build_script_sha256 = (Get-FileHash -LiteralPath (Join-Path $sourceDir 'build-correlation-fixture.ps1') -Algorithm SHA256).Hash
    package = [ordered]@{ name = 'Microsoft.Web.WebView2'; version = '1.0.4370.26'; sha256 = $packageHash; signature_verification = 'Previously recorded; timestamp and revocation freshness not independently established by this build.' }
    compiler = [ordered]@{ sdk = '10.0.404'; sha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash; host_sha256 = (Get-FileHash -LiteralPath $dotnet -Algorithm SHA256).Hash; reference_pack = '10.0.15'; references = $referenceHashes }
    target = 'net10.0-windows, win-x64, framework-dependent'
    dependency_limitation = 'No deps.json is generated. Managed and native dependency resolution requires independent runtime verification.'
    files = $inventory
}
$manifestPath = Join-Path $bundle 'bundle-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8)
$zip = Join-Path $runDir 'webview2-correlation-fixture.zip'
# Explicit portable ZIP entry names: .NET Framework CreateFromDirectory may
# retain Windows separators, which would disagree with the manifest paths.
$zipArchive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName)) {
        $entryName = $file.FullName.Substring($bundle.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zipArchive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zipArchive.Dispose() }
Write-Output "Bundle: $bundle"
Write-Output "Assembly SHA256: $((Get-FileHash -LiteralPath (Join-Path $bundle 'CorrelationFixture.dll') -Algorithm SHA256).Hash)"
Write-Output "Manifest SHA256: $((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash)"
Write-Output "ZIP: $zip"
Write-Output "ZIP SHA256: $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)"
Write-Output 'NOT RUN: fixture execution requires separate authorization on the approved VM.'
