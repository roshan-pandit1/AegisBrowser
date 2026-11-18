[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientPath,
    [switch]$PrepareOnly,
    [string]$PreparedRoot
)

# Hosted-runner diagnostic only. The fixed archive pin was calculated from an
# official CEF distribution that matched its published index checksum; this
# is not an independent publisher attestation. Never use production profiles.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$expectedArchive = 'C81C34D048D87276F4A18F0B000206710A1709BB67DD658948B87100E1A538DA'
$expectedBootstrap = 'BD0D6DCF683171305C02BCEE6B6F5DAD2ABDD2F90FF8EF0593BE585E78E164C9'
$expectedCef = 'E3DA6707A840A8F33F565E011542169FD51AAA5218603EB7A89039FB16D79AE7'
$expectedElf = 'F26656BFE38E1B78E42F292B6A8BE1B5F0B5DB15395AC571D6E20A1DEDF698FB'
$name = 'cef_binary_154.0.34+g14c5a08+chromium-154.0.8037.98_windows64_minimal'
$url = 'https://cef-builds.spotifycdn.com/cef_binary_154.0.34%2Bg14c5a08%2Bchromium-154.0.8037.98_windows64_minimal.tar.bz2'
if (-not $env:RUNNER_TEMP -or -not [IO.Path]::IsPathRooted($env:RUNNER_TEMP)) { throw 'Runner temporary directory required.' }
$runner = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\','/') + '\'
$client = [IO.Path]::GetFullPath($ClientPath)
if (-not $client.StartsWith($runner, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $client -PathType Leaf)) { throw 'Client must be a runner-owned isolated build output.' }
if ($PrepareOnly -and $PreparedRoot) { throw 'PrepareOnly and PreparedRoot are mutually exclusive.' }
$root = if ($PreparedRoot) { [IO.Path]::GetFullPath($PreparedRoot) } else { Join-Path $runner ('aegis-p309h-run-' + [Guid]::NewGuid().ToString('N')) }
if (-not $root.StartsWith($runner, [StringComparison]::OrdinalIgnoreCase)) { throw 'Research root must be under runner temporary directory.' }
$bundle = Join-Path $root 'bundle'
$unpack = Join-Path $root 'unpack'
$archive = Join-Path $root 'cef.tar.bz2'
if (-not $PreparedRoot) {
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
Write-Output 'DIAGNOSTIC_STAGE: DOWNLOAD_PINNED_CEF'
$timer = [Diagnostics.Stopwatch]::StartNew()
Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 180
Write-Output "DOWNLOAD_SECONDS: $($timer.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))"
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $expectedArchive) { throw 'Pinned CEF archive hash mismatch.' }
Write-Output 'DIAGNOSTIC_STAGE: ARCHIVE_HASH_VERIFIED'

# Python's streaming reader validates paths and types while extracting in one
# sequential pass. The exact archive pin is checked again by the extractor.
# CI provides the hard timeout; partial output is never reused as a prepared root.
Write-Output 'DIAGNOSTIC_STAGE: EXTRACT_PINNED_CEF'
& python (Join-Path $PSScriptRoot 'prepare_cef_archive.py') $archive $unpack
if ($LASTEXITCODE -ne 0) { throw 'CEF archive inspection or extraction failed.' }
if ($PrepareOnly) {
    if (-not $env:GITHUB_OUTPUT) { throw 'GITHUB_OUTPUT required for separated preparation.' }
    "prepared_root=$root" >> $env:GITHUB_OUTPUT
    Write-Output 'DIAGNOSTIC_STAGE: PREPARATION_COMPLETE'
    exit 0
}
}
foreach ($path in @($root, $unpack)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe prepared research directory.' }
}
New-Item -ItemType Directory -Path $bundle -ErrorAction Stop | Out-Null
$distribution = Join-Path $unpack $name
$release = Join-Path $distribution 'Release'
$resources = Join-Path $distribution 'Resources'
if (-not (Test-Path -LiteralPath $release -PathType Container) -or -not (Test-Path -LiteralPath $resources -PathType Container)) { throw 'Pinned CEF distribution is incomplete.' }

function Copy-BundleFile([IO.FileInfo]$File, [string]$Destination) {
    if (($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or (Test-Path -LiteralPath $Destination)) { throw 'Unsafe or duplicate bundle input.' }
    Copy-Item -LiteralPath $File.FullName -Destination $Destination -ErrorAction Stop
}
$releaseFiles = @('bootstrap.exe','bootstrapc.exe','chrome_elf.dll','d3dcompiler_47.dll',
    'dxcompiler.dll','dxil.dll','libcef.dll','libcef.lib','v8_context_snapshot.bin',
    'vk_swiftshader.dll','vk_swiftshader_icd.json','vulkan-1.dll')
$releaseItems = @(Get-ChildItem -LiteralPath $release -Force)
if ($releaseItems.Count -ne $releaseFiles.Count) { throw 'Unexpected CEF release file count.' }
foreach ($file in $releaseItems) {
    if ($file.PSIsContainer -or $releaseFiles -cnotcontains $file.Name) { throw 'Unknown CEF release entry.' }
    if ($file.Name -cin @('bootstrap.exe','libcef.lib')) { continue }
    Copy-BundleFile $file (Join-Path $bundle $file.Name)
}
foreach ($item in @(Get-ChildItem -LiteralPath $resources -Force)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Resource link rejected.' }
    if ($item.PSIsContainer) {
        if ($item.Name -cne 'locales') { throw 'Unexpected CEF resource directory.' }
        $dest = Join-Path $bundle 'locales'
        New-Item -ItemType Directory -Path $dest -ErrorAction Stop | Out-Null
        foreach ($locale in @(Get-ChildItem -LiteralPath $item.FullName -Force)) {
            if ($locale.PSIsContainer) { throw 'Nested locale directory rejected.' }
            Copy-BundleFile $locale (Join-Path $dest $locale.Name)
        }
    } else { Copy-BundleFile $item (Join-Path $bundle $item.Name) }
}
Copy-BundleFile (Get-Item -LiteralPath $client) (Join-Path $bundle 'p309f_client.dll')
foreach ($pair in @(@('bootstrapc.exe',$expectedBootstrap), @('libcef.dll',$expectedCef), @('chrome_elf.dll',$expectedElf))) {
    $file = Join-Path $bundle $pair[0]
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $pair[1]) { throw 'Pinned native dependency mismatch.' }
}

# Construct an external manifest only after the official archive pin and
# source-built client have been checked inside this trusted ephemeral job.
# This is an execution input, not independent evidence of publisher identity.
$files = [Collections.Generic.List[object]]::new()
foreach ($item in @(Get-ChildItem -LiteralPath $bundle -Force)) {
    if ($item.PSIsContainer) {
        if ($item.Name -cne 'locales') { throw 'Unexpected bundle directory.' }
        foreach ($file in @(Get-ChildItem -LiteralPath $item.FullName -File -Force)) {
            $files.Add([ordered]@{ name = 'locales/' + $file.Name; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash })
        }
    } else { $files.Add([ordered]@{ name = $item.Name; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }) }
}
$manifest = Join-Path $root 'manifest.json'
$json = [ordered]@{ schema = 'p309g-bundle-v1'; files = @($files.ToArray()) } | ConvertTo-Json -Depth 5 -Compress
[IO.File]::WriteAllText($manifest, $json, [Text.UTF8Encoding]::new($false))
$manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
Write-Output 'DIAGNOSTIC_STAGE: SUPERVISE_PINNED_CLIENT'
$supervisor = Join-Path $PSScriptRoot 'ProcessSupervisor/bin/Release/net8.0/ProcessSupervisor.exe'
if (-not (Test-Path -LiteralPath $supervisor -PathType Leaf)) { throw 'Built supervisor is missing.' }
$previous = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $output = @(& $supervisor 'supervise' (Join-Path $bundle 'bootstrapc.exe') $manifest $manifestHash '60000' 2>&1)
    $supervisorExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $previous }
$decoded = @($output | ForEach-Object {
    try { $_.ToString() | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Supervisor returned malformed or unexpected output.' }
})
if (@($decoded | Where-Object { $_.kind -notin @('PROCESS_IDENTITY','PROCESS_RESULT','RENDERER_OBSERVATION','PROCESS_DISCOVERY') }).Count -ne 0) { throw 'Supervisor returned an unknown record kind.' }
$records = @($decoded | Where-Object { $_.kind -ceq 'PROCESS_RESULT' })
$identities = @($decoded | Where-Object { $_.kind -ceq 'PROCESS_IDENTITY' })
$renderers = @($decoded | Where-Object { $_.kind -ceq 'RENDERER_OBSERVATION' })
$discovery = @($decoded | Where-Object { $_.kind -ceq 'PROCESS_DISCOVERY' })
if ($records.Count -ne 1 -or $identities.Count -gt 1 -or $renderers.Count -ne 1 -or $discovery.Count -ne 1 -or $supervisorExit -notin @(0,1)) { throw 'Supervisor did not produce an unambiguous diagnostic result.' }
$observedTree = $discovery[0]
if ($observedTree.SubprocessCleanup -cne 'INCONCLUSIVE' -or ($null -ne $observedTree.report -and
    ($observedTree.report.Discovery -notin @('PARTIAL','INCONCLUSIVE') -or $observedTree.report.Accounting -notin @('PARTIAL','INCONCLUSIVE') -or
     $observedTree.report.Cleanup -cne 'INCONCLUSIVE'))) { throw 'Unexpected process-discovery classification.' }
Write-Output ('PROCESS_DISCOVERY_JSON: ' + ($observedTree | ConvertTo-Json -Depth 8 -Compress))
$record = $records[0]
if ($record.OverallQualification -cne 'INCONCLUSIVE' -or $record.result.SubprocessCleanup -cne 'INCONCLUSIVE' -or
    $record.result.SandboxEnforcement -cne 'INCONCLUSIVE' -or $record.result.ProcessExit -notin @('NORMAL','NONZERO','TIMEOUT','UNAVAILABLE')) { throw 'Unexpected supervisor classification.' }
. (Join-Path $PSScriptRoot 'result-lib.ps1')
$identity = if ($identities.Count -eq 1) { $identities[0].identity } else { $null }
$correlated = Get-P309gSupervisedResult -SupervisorResult $record.result -EmittedIdentity $identity -ExpectedImageSha256 $expectedBootstrap
if ($supervisorExit -ne 0 -and $correlated.Disposition -ceq 'CLIENT_REPORTED_SEQUENCE_AND_PROCESS_EXIT') { throw 'Supervisor exit contradicts correlated result.' }
if ($correlated.IdentityMatched) {
    Write-Output "PROCESS_IDENTITY: PID=$($identity.Pid) CREATION_FILETIME_UTC=$($identity.CreationFileTimeUtc) IMAGE_SHA256=$($identity.ImageSha256)"
} else { Write-Output 'PROCESS_IDENTITY: UNVERIFIED' }
Write-Output "PROCESS_EXIT: $($record.result.ProcessExit)"
if ($null -eq $record.result.NativeExitCode) { Write-Output 'NATIVE_EXIT_CODE: UNAVAILABLE' }
else { Write-Output "NATIVE_EXIT_CODE: $($record.result.NativeExitCode)" }
Write-Output "PARENT_TERMINATION_REQUESTED: $($record.result.ParentTerminationRequested)"
Write-Output "OUTPUT_COMPLETE: $($record.result.OutputComplete)"
Write-Output "SUPERVISED_RESULT: $($correlated.Disposition) ($($correlated.Reason))"
foreach ($line in @(($record.result.Stdout.Text + "`n" + $record.result.Stderr.Text) -split "`r?`n" | Where-Object { $_ -match '^(EVENT:|NATIVE_DIAGNOSTIC:|BLOCKED:|CEF_|SANDBOX_|FIXTURE_|OWNER_)' } | Select-Object -First 100)) { Write-Output $line }
$renderer = $renderers[0].evidence
$policy = $renderers[0].policy
if ($renderer.RendererIdentity -cne 'INCONCLUSIVE' -or $renderer.SandboxEnforcement -cne 'INCONCLUSIVE' -or
    $renderer.SubprocessAccounting -cne 'INCONCLUSIVE' -or $renderer.SubprocessCleanup -cne 'INCONCLUSIVE' -or
    $renderer.Status -notin @('OBSERVED','INCONCLUSIVE') -or $policy.RendererSandboxPolicy -cne 'INCONCLUSIVE' -or
    $policy.SandboxEnforcement -cne 'INCONCLUSIVE') { throw 'Unexpected renderer-evidence classification.' }
Write-Output "RENDERER_OBSERVATION: $($renderer.Status) ($($renderer.Reason))"
Write-Output "RENDERER_ROLE_EVIDENCE: $($renderer.RoleEvidence)"
if ($null -ne $renderer.PipeDaclDiagnostic) {
    if ($renderer.PipeDaclDiagnostic.ActualOpenCause -cne 'INCONCLUSIVE' -or
        $renderer.PipeDaclDiagnostic.SandboxCompatibility -cne 'INCONCLUSIVE' -or
        $renderer.PipeDaclDiagnostic.Status -notin @('DACL_ALLOWED','DACL_DENIED','UNAVAILABLE')) { throw 'Unexpected pipe DACL classification.' }
    Write-Output "PIPE_DACL_DIAGNOSTIC: $($renderer.PipeDaclDiagnostic.Status) STAGE=$($renderer.PipeDaclDiagnostic.Stage) ERROR=$($renderer.PipeDaclDiagnostic.Error)"
}
Write-Output 'PIPE_ACCESS_DENIAL_CAUSE: INCONCLUSIVE'
Write-Output 'PIPE_SANDBOX_COMPATIBILITY: INCONCLUSIVE'
Write-Output ('RENDERER_EVIDENCE_JSON: ' + ([ordered]@{ observation = $renderer; policy = $policy } | ConvertTo-Json -Depth 8 -Compress))
if ($renderer.Status -ceq 'OBSERVED' -and $null -ne $renderer.Token -and $renderer.Token.Status -ceq 'OBSERVED') {
    Write-Output "CANDIDATE_IDENTITY: PID=$($renderer.CandidatePid) CREATION_FILETIME_UTC=$($renderer.CandidateCreationFileTimeUtc)"
    Write-Output "CANDIDATE_TOKEN: INTEGRITY_RID=$($renderer.Token.IntegrityRid) RESTRICTING_SIDS=$($renderer.Token.RestrictingSidCount) HAS_RESTRICTING_SIDS=$($renderer.Token.HasRestrictingSids) APPCONTAINER=$($renderer.Token.IsAppContainer) IN_JOB=$($renderer.Token.InAnyJob) WIN32K_DISABLED=$($renderer.Token.Win32kSystemCallsDisabled)"
    Write-Output 'RENDERER_TOKEN_EVIDENCE: INCONCLUSIVE (live direct-child token; renderer role remains a client claim)'
} else { Write-Output 'RENDERER_TOKEN_EVIDENCE: NOT_OBSERVED' }
Write-Output 'RENDERER_IDENTITY: INCONCLUSIVE'
Write-Output 'SUBPROCESS_ACCOUNTING: INCONCLUSIVE'
Write-Output 'SUBPROCESS_CLEANUP: INCONCLUSIVE'
Write-Output 'SANDBOX_ENFORCEMENT: INCONCLUSIVE'
Write-Output 'NATIVE_LIFECYCLE_ACCEPTANCE: INCOMPLETE'
Write-Output 'OWNER_RELEASE_CONTRACT: UNSATISFIED'
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "P3-09G native diagnostic completed. Process: $($record.result.ProcessExit). Lifecycle acceptance, renderer-token evidence and sandbox enforcement remain incomplete/inconclusive." }
# Any expected supervisor nonzero exit was interpreted as a diagnostic above;
# it is never converted into native acceptance.
$global:LASTEXITCODE = 0
