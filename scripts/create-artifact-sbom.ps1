[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactZip,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedZipSha256,
    [Parameter(Mandatory)][string]$PackageEvidence,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pr90ZipSha256 = '8A95CE41A2FD16684810376A80D2C4C1FED8E02FBAFEA15630FBBD2B84D7263B'
if ($ExpectedZipSha256 -ine $pr90ZipSha256) {
    throw 'SBOM_SCOPE_MISMATCH: this generator is scoped to the recorded PR #90 ZIP.'
}
$zipPath = (Resolve-Path -LiteralPath $ArtifactZip).Path
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ine $ExpectedZipSha256) {
    throw 'SBOM_INPUT_MISMATCH: artifact ZIP differs from the expected hash.'
}
$packages = Get-Content -LiteralPath $PackageEvidence -Raw | ConvertFrom-Json
if ($null -eq $packages -or @($packages).Count -eq 0) { throw 'SBOM_PACKAGE_EVIDENCE_MISSING' }
$files = [Collections.Generic.List[object]]::new()
$byName = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-EntrySha256([IO.Compression.ZipArchiveEntry]$Entry) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = $Entry.Open()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $archive.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
        if (-not $entry.FullName.StartsWith('AegisBrowser/', [StringComparison]::Ordinal) -or
            $entry.FullName -match '(^|/)\.\.(/|$)' -or -not $seen.Add($entry.FullName)) {
            throw 'SBOM_INPUT_INVALID: unsafe or duplicate archive entry.'
        }
        $file = [pscustomobject]@{
            path = $entry.FullName
            name = $entry.Name
            sha256 = Get-EntrySha256 $entry
            matches = [Collections.Generic.List[object]]::new()
        }
        $files.Add($file)
        if (-not $byName.ContainsKey($entry.Name)) { $byName.Add($entry.Name, [Collections.Generic.List[object]]::new()) }
        $byName[$entry.Name].Add($file)
    }
    $versionEntry = $archive.GetEntry('AegisBrowser/VERSION.txt')
    $depsEntry = $archive.GetEntry('AegisBrowser/AegisBrowser.deps.json')
    if ($null -eq $versionEntry -or $null -eq $depsEntry) { throw 'SBOM_IDENTITY_MISSING' }
    $reader = [IO.StreamReader]::new($versionEntry.Open())
    try { $versionText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($versionText -notmatch '(?m)^Aegis Browser DEVELOPMENT PREVIEW' -or
        $versionText -notmatch '(?m)^Source checkout commit: 1ee1dd284d2f6ad3ccb66593ce2984d56843063a\r?$') {
        throw 'SBOM_IDENTITY_MISMATCH: package source self-report differs from PR #90 evidence.'
    }
    $reader = [IO.StreamReader]::new($depsEntry.Open())
    try { $deps = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if (@($deps.libraries.PSObject.Properties.Name) -cnotcontains 'AegisBrowser/1.0.0') {
        throw 'SBOM_IDENTITY_MISMATCH: app version metadata differs from PR #90 evidence.'
    }
}
finally { $archive.Dispose() }

$packageComponents = [Collections.Generic.List[object]]::new()
$packageDependencies = [Collections.Generic.List[object]]::new()
foreach ($pkg in $packages) {
    $id = ([string]$pkg.id).ToLowerInvariant()
    $version = [string]$pkg.version
    if ($id -notmatch '^[a-z0-9._-]+$' -or $version -notmatch '^[a-z0-9.+-]+$' -or
        $pkg.packageSha256 -cnotmatch '^[A-F0-9]{64}$') {
        throw 'SBOM_PACKAGE_EVIDENCE_INVALID'
    }
    $packagePath = Join-Path (Join-Path (Join-Path $env:USERPROFILE '.nuget/packages') $id) ($version + '/' + $id + '.' + $version + '.nupkg')
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -cne $pkg.packageSha256) {
        throw "SBOM_PACKAGE_MISMATCH: $id $version"
    }
    $packageRef = 'pkg:nuget/' + $id + '@' + $version
    $memberRefs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $packageArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        foreach ($entry in $packageArchive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name) -or -not $byName.ContainsKey($entry.Name)) { continue }
            $hash = Get-EntrySha256 $entry
            foreach ($file in $byName[$entry.Name]) {
                if ($file.sha256 -ceq $hash) {
                    $file.matches.Add([pscustomobject]@{ package = $packageRef; entry = $entry.FullName; version = $version })
                    [void]$memberRefs.Add('file:' + $file.path)
                }
            }
        }
    }
    finally { $packageArchive.Dispose() }
    $packageComponents.Add([ordered]@{
        type = 'library'
        name = [string]$pkg.id
        version = $version
        'bom-ref' = $packageRef
        purl = $packageRef
        hashes = @(@{ alg = 'SHA-256'; content = [string]$pkg.packageSha256 })
        externalReferences = @(@{ type = 'distribution'; url = [string]$pkg.contentUrl })
        properties = @(
            @{ name = 'aegis:catalog'; value = [string]$pkg.catalogHash }
            @{ name = 'aegis:signature'; value = [string]$pkg.signature }
            @{ name = 'aegis:license'; value = [string]$pkg.license }
            @{ name = 'aegis:advisory'; value = [string]$pkg.advisory }
            @{ name = 'aegis:advisory-source'; value = [string]$pkg.advisorySource }
            @{ name = 'aegis:advisory-query-utc'; value = [string]$pkg.advisoryQueryUtc }
            @{ name = 'aegis:shipped-files'; value = [string]$memberRefs.Count }
        )
    })
    $packageDependencies.Add(@{ ref = $packageRef; dependsOn = @($memberRefs | Sort-Object) })
}

$fileComponents = [Collections.Generic.List[object]]::new()
$unmapped = 0
foreach ($file in $files) {
    $matches = @($file.matches)
    $origins = if ($matches.Count) {
        ($matches | ForEach-Object { $_.package + '!' + $_.entry } | Sort-Object -Unique) -join '; '
    } else {
        $unmapped++
        'UNVERIFIED: no byte-identical member found in the examined packages'
    }
    $versions = @($matches | ForEach-Object { $_.version } | Sort-Object -Unique)
    $version = if ($versions.Count -eq 1) { $versions[0] } else { 'UNVERIFIED' }
    $fileComponents.Add([ordered]@{
        type = 'file'
        name = $file.path
        version = $version
        'bom-ref' = 'file:' + $file.path
        hashes = @(@{ alg = 'SHA-256'; content = $file.sha256 })
        properties = @(
            @{ name = 'aegis:source'; value = $origins }
            @{ name = 'aegis:signature'; value = 'UNVERIFIED individually; see matched package evidence' }
            @{ name = 'aegis:license'; value = 'UNVERIFIED per file; see matched package metadata and notices' }
            @{ name = 'aegis:advisory'; value = 'UNVERIFIED per file; see package advisory observations' }
            @{ name = 'aegis:location'; value = $file.path }
        )
    })
}

$rootRef = 'aegis:preview:1ee1dd284d2f6ad3ccb66593ce2984d56843063a'
$rootDeps = @($packageComponents | ForEach-Object { $_['bom-ref'] }) + @($fileComponents | ForEach-Object { $_['bom-ref'] })
$bom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = @{
        component = @{
            type = 'application'
            name = 'AegisBrowser DEVELOPMENT PREVIEW'
            version = '1.0.0'
            'bom-ref' = $rootRef
            properties = @(
                @{ name = 'aegis:source-commit'; value = 'Artifact VERSION.txt self-reports 1ee1dd284d2f6ad3ccb66593ce2984d56843063a; this is not independent source attestation' }
                @{ name = 'aegis:artifact-sha256'; value = $ExpectedZipSha256.ToUpperInvariant() }
                @{ name = 'aegis:scope'; value = 'PR #90 portable ZIP; file inventory and examined packages, not a complete embedded-component or legal analysis' }
            )
        }
    }
    components = @($packageComponents) + @($fileComponents)
    dependencies = @(@{ ref = $rootRef; dependsOn = @($rootDeps | Sort-Object) }) + @($packageDependencies)
}
$out = [IO.Path]::GetFullPath($OutputFile)
# Use LF bytes so the documented evidence hash is stable across checkouts.
$json = ($bom | ConvertTo-Json -Depth 30).Replace("`r`n", "`n")
$stream = [IO.File]::Open($out, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write($json) }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
Write-Output "SBOM_FILES: $($files.Count)"
Write-Output "SBOM_PACKAGES: $($packageComponents.Count)"
Write-Output "SBOM_UNMAPPED_FILES: $unmapped"
Write-Output "SBOM_OUTPUT: $out"
