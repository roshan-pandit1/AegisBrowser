[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DownloadRoot,
    [Parameter(Mandatory)][string]$DepotToolsPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedDepotToolsRevision,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$AcknowledgeSourceBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This wrapper builds only the pinned, uninstrumented baseline. A successful
# compiler exit is not a native smoke test, sandbox proof, or release approval.
# No source reset, clean, retry, distribution publication, or production binary
# replacement is performed by this wrapper.
$cefCommit = '14c5a089e8452874cb1a556dc3fde26ed2d87723'
$chromiumCommit = 'b859317bf11f6be47f9b7799ec690a0a42a1fb33'
$cefBranch = '8037'
$chromiumTag = 'refs/tags/154.0.8037.98'

if (-not $AcknowledgeSourceBuild) {
    throw 'BUILD_NOT_STARTED: pass -AcknowledgeSourceBuild only on an authorized disposable x64 builder.'
}
foreach ($item in @($DownloadRoot, $DepotToolsPath)) {
    if ($item.Length -gt 120 -or $item -notmatch '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$' -or
        @($item.Substring(3).Split('\\') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw 'BUILD_NOT_STARTED: source and depot_tools paths must be short, absolute, ASCII and shell-safe.'
    }
    if (-not (Test-Path -LiteralPath $item -PathType Container)) {
        throw 'BUILD_NOT_STARTED: source and depot_tools paths must already exist.'
    }
}
if (-not [IO.Path]::IsPathRooted($EvidenceDirectory)) { throw 'BUILD_NOT_STARTED: evidence path must be absolute.' }
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'BUILD_NOT_STARTED: evidence directory must not already exist.' }

$root = (Resolve-Path -LiteralPath $DownloadRoot).Path.TrimEnd('\')
$depot = (Resolve-Path -LiteralPath $DepotToolsPath).Path.TrimEnd('\')
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
if ($evidence.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $root.StartsWith($evidence + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.StartsWith($depot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $depot.StartsWith($evidence + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.Equals($depot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BUILD_NOT_STARTED: evidence, source and depot_tools paths must be separate.'
}
$src = Join-Path $root 'chromium\src'
$cef = Join-Path $root 'cef'
$cefInSrc = Join-Path $src 'cef'
$automate = Join-Path $cef 'tools\automate\automate-git.py'
$preflight = Join-Path $PSScriptRoot 'preflight.ps1'
foreach ($required in @($src, $cef, $cefInSrc, $automate, $preflight, (Join-Path $depot 'python3.bat'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw 'BUILD_NOT_STARTED: required prepared source, tool or preflight is missing.' }
}

# Require the stricter source stage. In particular, a modified Chromium tree
# cannot be called a stock baseline merely because its HEAD matches the pin.
$preflightJson = (& $preflight -BuildRoot $root -Stage Build -DepotToolsPath $depot `
    -ExpectedDepotToolsRevision $ExpectedDepotToolsRevision -ChromiumSourcePath $src -CefSourcePath $cef | Out-String)
$preflightExit = $LASTEXITCODE
if ($preflightExit -ne 0) { throw ('BUILD_NOT_STARTED: preflight was not qualified: ' + $preflightJson) }
$preflightResult = $preflightJson | ConvertFrom-Json
if ($preflightResult.qualified -ne $true) { throw 'BUILD_NOT_STARTED: preflight did not report qualified.' }

function Assert-Revision([string]$path, [string]$expected, [string]$label) {
    $actual = (& git -C $path rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $expected) { throw "BUILD_NOT_STARTED: $label revision mismatch." }
}
Assert-Revision $src $chromiumCommit 'Chromium'
Assert-Revision $cef $cefCommit 'CEF'
Assert-Revision $cefInSrc $cefCommit 'CEF-in-Chromium'
Assert-Revision $depot ($ExpectedDepotToolsRevision.ToLowerInvariant()) 'depot_tools'

# The upstream automator may relocate an existing out directory if its branch
# marker differs. Never invoke it with a prior output tree or unknown marker.
$out = Join-Path $src 'out'
if (Test-Path -LiteralPath $out -PathType Container) {
    $children = @(Get-ChildItem -LiteralPath $out -Force)
    if (@($children | Where-Object { $_.Name -cne 'cef.branch' }).Count -ne 0) {
        throw 'BUILD_NOT_STARTED: existing build output requires a new isolated source root.'
    }
    $branchFile = Join-Path $out 'cef.branch'
    if (Test-Path -LiteralPath $branchFile) {
        $branchContents = Get-Content -LiteralPath $branchFile -Raw
        if ($branchContents -notmatch "(?m)^\s*'branch':\s*'8037',?\s*$") {
            throw 'BUILD_NOT_STARTED: existing CEF output branch marker is unexpected.'
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $root 'out_8037')) {
    throw 'BUILD_NOT_STARTED: existing relocated output requires a new isolated source root.'
}

$null = New-Item -ItemType Directory -Path $evidence -ErrorAction Stop
$log = Join-Path $evidence 'baseline-automate.log'
$manifestPath = Join-Path $evidence 'baseline-build.json'
$startUtc = [DateTime]::UtcNow.ToString('o')
$manifest = [ordered]@{
    schema = 1; kind = 'CEF_BASELINE_BUILD'; chromium_commit = $chromiumCommit;
    chromium_tag = $chromiumTag; cef_commit = $cefCommit; cef_branch = $cefBranch;
    depot_tools_commit = $ExpectedDepotToolsRevision.ToLowerInvariant();
    started_utc = $startUtc; finished_utc = $null; build = 'NOT_RUN'; exit_code = $null;
    source_patch_provenance = 'INCONCLUSIVE'; native_smoke = 'NOT_RUN';
    renderer_identity = 'NOT_RUN'; sandbox_enforcement = 'NOT_RUN';
    production_readiness = 'NO_GO'; artifacts = @()
}
function Save-Manifest { $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8 }
function Get-PeMachine([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'OUTPUT_NOT_PE' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 6)) { throw 'OUTPUT_PE_HEADER_INVALID' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'OUTPUT_PE_SIGNATURE_INVALID' }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose() }
}
Save-Manifest

# Pinned CEF's automator prints all GN_/CEF_/GCLIENT_/GYP_/DEPOT_TOOLS_
# variables. Remove inherited values so unrelated credentials are not emitted.
$saved = @{}
Get-ChildItem Env: | Where-Object { $_.Name -match '^(CEF_|GCLIENT_|GN_|GYP_|DEPOT_TOOLS_)' } | ForEach-Object {
    $saved[$_.Name] = $_.Value
    Remove-Item -LiteralPath ('Env:' + $_.Name)
}
$oldPath = $env:PATH
try {
    $env:PATH = $depot + ';' + $oldPath
    $env:DEPOT_TOOLS_UPDATE = '0'
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
    $env:GN_DEFINES = 'is_official_build=false is_component_build=false symbol_level=1'
    $argsList = @($automate, "--download-dir=$root", "--depot-tools-dir=$depot", "--branch=$cefBranch",
        "--checkout=$cefCommit", "--chromium-checkout=$chromiumTag", '--no-update', '--force-build',
        '--no-debug-build', '--no-distrib', '--x64-build', '--build-target=cefsimple', '--build-log-file')
    $manifest.build = 'IN_PROGRESS'
    Save-Manifest
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & (Join-Path $depot 'python3.bat') @argsList 2>&1 | Tee-Object -FilePath $log
        $buildExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    $manifest.exit_code = $buildExit
    $manifest.build = 'FAILED'
    if (Test-Path -LiteralPath $log -PathType Leaf) {
        $manifest.artifacts += [ordered]@{ name = 'baseline-automate.log'; sha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $config = Join-Path $src 'out\Release_GN_x64\args.gn'
    if (Test-Path -LiteralPath $config -PathType Leaf) {
        Copy-Item -LiteralPath $config -Destination (Join-Path $evidence 'args.gn')
        $manifest.artifacts += [ordered]@{ name = 'Release_GN_x64/args.gn'; sha256 = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $nativeLog = Join-Path $root 'build-8037-release.log'
    if (Test-Path -LiteralPath $nativeLog -PathType Leaf) {
        Copy-Item -LiteralPath $nativeLog -Destination (Join-Path $evidence 'build-8037-release.log')
        $manifest.artifacts += [ordered]@{ name = 'build-8037-release.log'; sha256 = (Get-FileHash -LiteralPath $nativeLog -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    if ($buildExit -ne 0) { throw "CEF_BUILD_FAILED: exit $buildExit. See baseline-automate.log." }
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw 'CEF_BUILD_OUTPUT_MISSING: args.gn' }
    foreach ($name in @('libcef.dll', 'bootstrap.exe', 'cefsimple.exe')) {
        $file = Join-Path (Join-Path $src 'out\Release_GN_x64') $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "CEF_BUILD_OUTPUT_MISSING: $name" }
        if ((Get-PeMachine $file) -ne 0x8664) { throw "CEF_BUILD_OUTPUT_NOT_X64: $name" }
        $manifest.artifacts += [ordered]@{ name = $name; machine = 'AMD64'; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest.build = 'COMPILED_SOURCE_PROVENANCE_INCONCLUSIVE'
} catch {
    if ($manifest.build -eq 'IN_PROGRESS') { $manifest.build = 'FAILED' }
    throw
} finally {
    $manifest.finished_utc = [DateTime]::UtcNow.ToString('o')
    Save-Manifest
    Get-ChildItem Env: | Where-Object { $_.Name -match '^(CEF_|GCLIENT_|GN_|GYP_|DEPOT_TOOLS_)' } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    foreach ($name in $saved.Keys) { Set-Item -LiteralPath ('Env:' + $name) -Value $saved[$name] }
    $env:PATH = $oldPath
}

Write-Output ('BUILD_RESULT=' + $manifest.build)
Write-Output 'NATIVE_SMOKE=NOT_RUN; SANDBOX_ENFORCEMENT=NOT_RUN; PRODUCTION_READINESS=NO_GO'
