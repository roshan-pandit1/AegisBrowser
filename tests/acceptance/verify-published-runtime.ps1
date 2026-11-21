param(
    [Parameter(Mandatory = $true)][string] $PublishDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^10\.0\.[0-9]+$')][string] $ExpectedRuntimeVersion
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Publish directory is unavailable.' }

function Get-RequiredFile([string] $Name) {
    $path = Join-Path $root $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required publish file is missing: $Name" }
    return $path
}

function Assert-X64Pe([string] $Name) {
    $path = Get-RequiredFile $Name
    $stream = [IO.File]::OpenRead($path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid DOS header: $Name" }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt ($stream.Length - 24)) { throw "Invalid PE offset: $Name" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Publish file is not an AMD64 PE: $Name"
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-RuntimeComponent([string] $Name, [string] $Framework) {
    $path = Get-RequiredFile $Name
    $dotnetRoot = $env:DOTNET_ROOT
    if ([string]::IsNullOrWhiteSpace($dotnetRoot)) {
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
        if ($null -eq $command) { throw 'The pinned x64 SDK is required to identify the reference runtime.' }
        $dotnetRoot = Split-Path -Parent $command.Source
    }
    $reference = Join-Path $dotnetRoot (Join-Path 'shared' (Join-Path $Framework (Join-Path $ExpectedRuntimeVersion $Name)))
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "Installed reference runtime $Framework $ExpectedRuntimeVersion does not contain $Name."
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $expectedHash = (Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash
    if ($actualHash -cne $expectedHash) { throw "Bundled $Name differs from the installed $Framework $ExpectedRuntimeVersion reference." }
    $product = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    Write-Output "PUBLISHED_RUNTIME_COMPONENT: $Name product=$product sha256=$actualHash reference=$Framework/$ExpectedRuntimeVersion"
}

foreach ($file in @('AegisBrowser.exe', 'CefSharp.BrowserSubprocess.exe', 'libcef.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
    Assert-X64Pe $file
}
$null = Get-RequiredFile 'CefSharp.BrowserSubprocess.Core.dll'
$null = Get-RequiredFile 'AegisBrowser.deps.json'

$configPath = Get-RequiredFile 'AegisBrowser.runtimeconfig.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.runtimeOptions.tfm -cne 'net10.0' -or $config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) {
    throw 'The app runtime configuration is not the expected self-contained .NET 10 configuration.'
}
$included = @($config.runtimeOptions.includedFrameworks)
foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $match = @($included | Where-Object { $_.name -ceq $name })
    if ($match.Count -ne 1 -or $match[0].version -cne $ExpectedRuntimeVersion) {
        throw "The self-contained runtime configuration does not pin $name to $ExpectedRuntimeVersion."
    }
}
if ($included.Count -ne 2) { throw 'Unexpected additional bundled framework in the app runtime configuration.' }

# CefSharp 154's self-contained package targets intentionally exclude the
# subprocess runtimeconfig so the supplied subprocess uses the bundled runtime.
# A changed subprocess runtimeconfig is rejected rather than guessed compatible.
$subConfig = Join-Path $root 'CefSharp.BrowserSubprocess.runtimeconfig.json'
if (Test-Path -LiteralPath $subConfig) {
    throw 'Unexpected subprocess runtime configuration; its runtime binding requires independent verification.'
}

Assert-RuntimeComponent 'coreclr.dll' 'Microsoft.NETCore.App'
Assert-RuntimeComponent 'System.Private.CoreLib.dll' 'Microsoft.NETCore.App'
Assert-RuntimeComponent 'System.Windows.Forms.dll' 'Microsoft.WindowsDesktop.App'
Write-Output "PASS: self-contained .NET $ExpectedRuntimeVersion metadata, required native files and AMD64 PE headers verified."
Write-Output 'INCONCLUSIVE: file metadata alone does not establish subprocess runtime loading, native CEF behavior, navigation, or shutdown.'
