param([string]$PublishDirectory = 'artifacts/win-x64', [string]$OutputDirectory = 'artifacts/portable',
    [ValidateRange(1, 20)][int]$SmokeRuns = 1)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Packaging verification requires Windows.' }
if ($SmokeRuns -gt 1 -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or !$env:P403_PACKAGE_TRUST_DIR)) {
    throw 'Repeated HTTPS smoke runs require the disposable hosted runner and trust fixture.'
}
if ($env:GITHUB_ACTIONS -eq 'true' -and [string]::IsNullOrWhiteSpace($env:P403_PACKAGE_TRUST_DIR)) {
    throw 'Hosted packaging requires the disposable HTTPS package fixture.'
}
$source = (Resolve-Path $PublishDirectory).Path
./tests/acceptance/verify-cef-notices.ps1 -PublishDirectory $source -CreditsFile 'third-party-licenses/CEF-CREDITS.html'
./tests/acceptance/verify-dotnet-notices.ps1 -PublishDirectory $source -NoticeDirectory 'third-party-licenses'
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$outputParent = [IO.Path]::GetDirectoryName($output)
if ([string]::IsNullOrEmpty($outputParent) -or [string]::IsNullOrEmpty([IO.Path]::GetFileName($output))) {
    throw 'PACKAGE_OUTPUT_INVALID: a new output directory is required.'
}
if (Test-Path -LiteralPath $output) { throw 'PACKAGE_OUTPUT_EXISTS: choose a new output directory; existing files are not overwritten.' }
$sourceRoot = [IO.Path]::GetFullPath($source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($output.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PACKAGE_OUTPUT_INVALID: output must be outside the publish directory.'
}
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
$pendingName = '.aegis-package-' + [guid]::NewGuid().ToString('N')
$pending = Join-Path $outputParent $pendingName
New-Item -ItemType Directory -Path $pending -ErrorAction Stop | Out-Null
$zip = Join-Path $pending 'AegisBrowser-DEVELOPMENT-PREVIEW-win-x64.zip'
$sidecar = "$zip.sha256"
$published = $false
try {
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$stage = Join-Path $tempRoot ('aegis-portable-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'AegisBrowser'
New-Item -ItemType Directory -Force $bundle | Out-Null
Copy-Item "$source/*" $bundle -Recurse
Copy-Item docs/INSTALL-WINDOWS.md (Join-Path $bundle 'INSTALL-WINDOWS.md')
Copy-Item docs/portable-candidate.md (Join-Path $bundle 'PORTABLE-CANDIDATE.md')
Copy-Item docs/decisions/engine-selection.md (Join-Path $bundle 'ENGINE-SELECTION.md')
Copy-Item docs/core-mvp-baseline.md (Join-Path $bundle 'CORE-MVP-BASELINE.md')
Copy-Item docs/privacy.md (Join-Path $bundle 'PRIVACY.md')
Copy-Item docs/release-rc1.md (Join-Path $bundle 'release-rc1.md')
Copy-Item docs/production-release.md (Join-Path $bundle 'production-release.md')
Copy-Item docs/search.md (Join-Path $bundle 'SEARCH.md')
Copy-Item docs/profile-manager.md (Join-Path $bundle 'PROFILE-MANAGER.md')
Copy-Item BUILD-STATUS.md (Join-Path $bundle 'BUILD-STATUS.md')
# Include the unmodified notices for the pinned CEF and CefSharp packages.
# Keep the accompanying attribution file next to the distributed notices.
Copy-Item -LiteralPath 'third-party-licenses' -Destination (Join-Path $bundle 'third-party-licenses') -Recurse
$revision = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not record source revision' }
@("Aegis Browser DEVELOPMENT PREVIEW - production NO_GO", "Source checkout commit: $revision", "Target: win-x64, self-contained .NET 10.0.15", "Chromium security-fix coverage is unqualified; stock CefSharp does not support the CEF renderer sandbox.", "Required-proxy profiles remain blocked; network containment is not established.", "Read PORTABLE-CANDIDATE.md and ENGINE-SELECTION.md. Manual release acceptance remains required.") | Set-Content (Join-Path $bundle 'VERSION.txt')
./scripts/write-deterministic-zip.ps1 -SourceDirectory $bundle -ArchivePath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$extract = Join-Path $stage 'extracted'
Expand-Archive $zip -DestinationPath $extract
$unpacked = Join-Path $extract 'AegisBrowser'
$expected = @(Get-ChildItem $bundle -Recurse -File)
$actual = @(Get-ChildItem $unpacked -Recurse -File)
if ($expected.Count -ne $actual.Count) { throw 'ZIP file count mismatch' }
foreach ($file in $expected) {
    $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName)
    $restored = Join-Path $unpacked $relative
    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $restored).Hash) { throw "ZIP content mismatch: $relative" }
}
./tests/acceptance/verify-published-runtime.ps1 -PublishDirectory $unpacked -ExpectedRuntimeVersion '10.0.15'
./tests/acceptance/verify-cef-runtime-manifest.ps1 -PublishDirectory $unpacked
if ((Get-FileHash -LiteralPath (Join-Path $unpacked 'third-party-licenses/CEF-RUNTIME-HASHES.json') -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath 'third-party-licenses/CEF-RUNTIME-HASHES.json' -Algorithm SHA256).Hash) {
    throw 'CEF_COMPONENT_MANIFEST_MISMATCH: packaged manifest differs from the source manifest.'
}
./tests/acceptance/verify-cef-notices.ps1 -PublishDirectory $unpacked -CreditsFile (Join-Path $unpacked 'third-party-licenses/CEF-CREDITS.html')
./tests/acceptance/verify-dotnet-notices.ps1 -PublishDirectory $unpacked -NoticeDirectory (Join-Path $unpacked 'third-party-licenses')
$smokeFailures = 0
for ($run = 1; $run -le $SmokeRuns; $run++) {
    try {
        Write-Output "PACKAGE_HTTPS_RUN: run=$run total=$SmokeRuns"
        ./tests/acceptance/windows-smoke.ps1 -PublishDirectory $unpacked
        Write-Output "PACKAGE_HTTPS_RUN_RESULT: run=$run status=PASS"
    }
    catch {
        $smokeFailures++
        Write-Output "PACKAGE_HTTPS_RUN_RESULT: run=$run status=FAIL reason=$($_.Exception.Message)"
        if ($SmokeRuns -eq 1) { throw }
    }
    finally {
        if ($SmokeRuns -gt 1) {
            ./tests/acceptance/p403-cleanup-test-ca.ps1 -TrustDirectory $env:P403_PACKAGE_TRUST_DIR
            if ($run -lt $SmokeRuns) {
                New-Item -ItemType Directory -Path $env:P403_PACKAGE_TRUST_DIR -ErrorAction Stop | Out-Null
            }
        }
    }
}
if ($smokeFailures) { throw "PACKAGE_HTTPS_SUMMARY: passed=$($SmokeRuns - $smokeFailures) failed=$smokeFailures" }
Write-Output "PACKAGE_HTTPS_SUMMARY: passed=$SmokeRuns failed=0"
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath $sidecar -ErrorAction Stop
# A same-parent directory rename publishes both validated files together. The
# destination must still be absent: Directory.Move never overwrites an existing
# directory or file, including one created after the initial check.
[IO.Directory]::Move($pending, $output)
$published = $true
Write-Output "PASS: portable ZIP round trip verified ($($expected.Count) files), extracted application smoke passed"
Write-Output "Portable ZIP SHA256: $hash"
} finally {
    if (-not $published -and (Test-Path -LiteralPath $pending -PathType Container)) {
        # Delete only the two paths created by this run, and only under the
        # exact unique sibling directory. Do not recursively remove anything.
        $item = Get-Item -LiteralPath $pending -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            [IO.Path]::GetFileName($item.FullName) -cne $pendingName -or
            -not [string]::Equals([IO.Path]::GetDirectoryName($item.FullName), $outputParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'PACKAGE_CLEANUP_FAILED: staged output identity changed.'
        }
        foreach ($file in @($zip, $sidecar)) {
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        [IO.Directory]::Delete($pending, $false)
    }
}
