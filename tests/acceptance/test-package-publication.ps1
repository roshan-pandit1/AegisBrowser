[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The package publication check requires Windows.' }
$runner = if ($env:RUNNER_TEMP) { [IO.Path]::GetFullPath($env:RUNNER_TEMP) } else { [IO.Path]::GetFullPath([IO.Path]::GetTempPath()) }
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $runner ('aegis-package-publication-' + $id)
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$originalRunnerTemp = $env:RUNNER_TEMP
$originalTrust = $env:P403_PACKAGE_TRUST_DIR
try {
    $source = Join-Path $root 'incomplete-publish'
    New-Item -ItemType Directory -Path $source -ErrorAction Stop | Out-Null
    # Supply the genuine pinned CEF and runtime inputs, but omit the app exe.
    # The package passes the preflight notice checks and must fail after extraction.
    foreach ($name in @('libcef.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'System.Windows.Forms.dll')) {
        Copy-Item -LiteralPath (Join-Path $PublishDirectory $name) -Destination (Join-Path $source $name) -ErrorAction Stop
    }
    $env:RUNNER_TEMP = $root
    if ($env:GITHUB_ACTIONS -eq 'true') { $env:P403_PACKAGE_TRUST_DIR = Join-Path $root 'unused-trust' }
    $output = Join-Path $root 'failed-output'
    $failed = $false
    try { & ./scripts/package-portable.ps1 -PublishDirectory $source -OutputDirectory $output }
    catch {
        if ($_.Exception.Message -notlike '*Required publish file is missing: AegisBrowser.exe*') { throw }
        $failed = $true
    }
    if (-not $failed -or (Test-Path -LiteralPath $output)) { throw 'Late validation failure published an output.' }
    if (@(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object { $_.Name -like '.aegis-package-*' }).Count -ne 0) {
        throw 'Late validation failure retained a pending publication directory.'
    }
    Write-Output 'PASS: late package validation failure leaves no published ZIP or checksum'

    $existing = Join-Path $root 'existing-output'
    New-Item -ItemType Directory -Path $existing -ErrorAction Stop | Out-Null
    $oldZip = Join-Path $existing 'AegisBrowser-RC1-win-x64.zip'
    $oldChecksum = "$oldZip.sha256"
    [IO.File]::WriteAllText($oldZip, 'existing ZIP sentinel')
    [IO.File]::WriteAllText($oldChecksum, 'existing checksum sentinel')
    $beforeZip = (Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash
    $beforeChecksum = (Get-FileHash -LiteralPath $oldChecksum -Algorithm SHA256).Hash
    $rejected = $false
    try { & ./scripts/package-portable.ps1 -PublishDirectory $source -OutputDirectory $existing }
    catch {
        if ($_.Exception.Message -notlike 'PACKAGE_OUTPUT_EXISTS:*') { throw }
        $rejected = $true
    }
    if (-not $rejected -or (Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash -cne $beforeZip -or
        (Get-FileHash -LiteralPath $oldChecksum -Algorithm SHA256).Hash -cne $beforeChecksum) {
        throw 'Existing package was not preserved.'
    }
    Write-Output 'PASS: existing output directory and package are preserved'
} finally {
    $env:RUNNER_TEMP = $originalRunnerTemp
    $env:P403_PACKAGE_TRUST_DIR = $originalTrust
    $resolved = [IO.Path]::GetFullPath($root)
    $runnerRoot = $runner.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $resolved.StartsWith($runnerRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cne ('aegis-package-publication-' + $id) -or
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'PACKAGE_TEST_CLEANUP_FAILED: fixture root identity changed.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
}
