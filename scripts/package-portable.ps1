param([string]$PublishDirectory = 'artifacts/win-x64', [string]$OutputDirectory = 'artifacts/portable')
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Packaging verification requires Windows.' }
$dirty = git status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source state' }
if ($dirty) { throw 'Portable release packaging requires a clean, reviewed checkout; preserve unrelated work in a separate checkout.' }
$source = (Resolve-Path $PublishDirectory).Path
$sbom = Join-Path $source 'aegis-sbom.cdx.json'
$verify = @()
if (Test-Path $sbom) { $verify = @('--verify-inventory') }
python scripts/security_audit.py --assets AegisBrowser/obj/project.assets.json --publish $source --sbom $sbom @verify
if ($LASTEXITCODE -ne 0) { throw 'Source/payload/SBOM validation failed' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$stage = Join-Path $tempRoot ('aegis-portable-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'AegisBrowser'
New-Item -ItemType Directory -Force $bundle | Out-Null
Copy-Item "$source/*" $bundle -Recurse
Copy-Item docs/INSTALL-WINDOWS.md (Join-Path $bundle 'INSTALL-WINDOWS.md')
Copy-Item docs/privacy.md (Join-Path $bundle 'PRIVACY.md')
Copy-Item docs/search.md (Join-Path $bundle 'SEARCH.md')
Copy-Item docs/authorized-lab.md (Join-Path $bundle 'AUTHORIZED-LAB.md')
Copy-Item BUILD-STATUS.md (Join-Path $bundle 'BUILD-STATUS.md')
Copy-Item SECURITY.md (Join-Path $bundle 'SECURITY.md')
Copy-Item docs/SECURITY-LIMITATIONS.md (Join-Path $bundle 'SECURITY-LIMITATIONS.md')
$revision = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not record source revision' }
@("Aegis Browser v0.1.0 release candidate", "Source: $revision", "Target: win-x64, self-contained", "Manual release acceptance remains required. See BUILD-STATUS.md.") | Set-Content (Join-Path $bundle 'VERSION.txt')
$zip = Join-Path $output 'AegisBrowser-v0.1.0-rc1-win-x64-portable.zip'
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
./tests/acceptance/windows-smoke.ps1 -PublishDirectory $unpacked
./tests/acceptance/windows-smoke.ps1 -PublishDirectory $unpacked -Lab
Write-Output "PASS: portable ZIP round trip verified ($($expected.Count) files), extracted application smoke passed"
Write-Output "Portable ZIP SHA256: $hash"
