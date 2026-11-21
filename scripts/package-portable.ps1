param([string]$PublishDirectory = 'artifacts/win-x64', [string]$OutputDirectory = 'artifacts/portable')
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Packaging verification requires Windows.' }
if ($env:GITHUB_ACTIONS -eq 'true' -and [string]::IsNullOrWhiteSpace($env:P403_PACKAGE_TRUST_DIR)) {
    throw 'Hosted packaging requires the disposable HTTPS package fixture.'
}
$source = (Resolve-Path $PublishDirectory).Path
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$stage = Join-Path $tempRoot ('aegis-portable-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'AegisBrowser'
New-Item -ItemType Directory -Force $bundle | Out-Null
Copy-Item "$source/*" $bundle -Recurse
Copy-Item docs/INSTALL-WINDOWS.md (Join-Path $bundle 'INSTALL-WINDOWS.md')
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
@("Aegis Browser v0.1.0 release candidate", "Source: $revision", "Target: win-x64, self-contained", "Manual release acceptance remains required. See BUILD-STATUS.md.") | Set-Content (Join-Path $bundle 'VERSION.txt')
$zip = Join-Path $output 'AegisBrowser-RC1-win-x64.zip'
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256"
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
./tests/acceptance/windows-smoke.ps1 -PublishDirectory $unpacked
Write-Output "PASS: portable ZIP round trip verified ($($expected.Count) files), extracted application smoke passed"
Write-Output "Portable ZIP SHA256: $hash"
