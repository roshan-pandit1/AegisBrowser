[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientPath,
    [Parameter(Mandatory)][string]$ManagedOutput,
    [Parameter(Mandatory)][string]$PatchedRuntime,
    [Parameter(Mandatory)][string]$NethostPath,
    [Parameter(Mandatory)][string]$DotnetRoot,
    [Parameter(Mandatory)][string]$FixtureDll,
    [Parameter(Mandatory)][string]$TrustDirectory,
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
& python (Join-Path $PSScriptRoot '../p309g/prepare_cef_archive.py') $archive $unpack
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
# The extracted GUI executable is bound to the previously verified archive.
$expectedBootstrap = (Get-FileHash -LiteralPath (Join-Path $release 'bootstrap.exe') -Algorithm SHA256).Hash
Write-Output "GUI_BOOTSTRAP_SHA256: $expectedBootstrap"

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
    if ($file.Name -cin @('bootstrapc.exe','libcef.lib')) { continue }
    $destinationName = if ($file.Name -ceq 'bootstrap.exe') { 'p309f_client.exe' } else { $file.Name }
    Copy-BundleFile $file (Join-Path $bundle $destinationName)
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
$managedRoot = [IO.Path]::GetFullPath($ManagedOutput)
$patched = [IO.Path]::GetFullPath($PatchedRuntime)
if (-not $patched.StartsWith($runner, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $patched -PathType Leaf)) { throw 'Patched runtime must be a runner-owned build output.' }
foreach ($name in @('P0ManagedHost.dll','P0ManagedHost.deps.json','P0ManagedHost.runtimeconfig.json',
        'CefSharp.dll','CefSharp.Core.dll','CefSharp.WinForms.dll','Ijwhost.dll')) {
    Copy-BundleFile (Get-Item -LiteralPath (Join-Path $managedRoot $name)) (Join-Path $bundle $name)
}
Copy-BundleFile (Get-Item -LiteralPath $patched) (Join-Path $bundle 'CefSharp.Core.Runtime.dll')
Copy-BundleFile (Get-Item -LiteralPath $NethostPath) (Join-Path $bundle 'nethost.dll')
if ((Get-FileHash -LiteralPath $patched -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath (Join-Path $bundle 'CefSharp.Core.Runtime.dll') -Algorithm SHA256).Hash) { throw 'Patched binding copy mismatch.' }
Write-Output "PATCHED_BINDING_SHA256: $((Get-FileHash -LiteralPath $patched -Algorithm SHA256).Hash)"
foreach ($pair in @(@('p309f_client.exe',$expectedBootstrap), @('libcef.dll',$expectedCef), @('chrome_elf.dll',$expectedElf))) {
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
$supervisor = Join-Path $PSScriptRoot '../p0native/GuiSupervisor/bin/Release/net8.0/ProcessSupervisor.exe'
if (-not (Test-Path -LiteralPath $supervisor -PathType Leaf)) { throw 'Built supervisor is missing.' }
$evidencePath = Join-Path $runner 'aegis-p0-managed-evidence.json'
$initial = [ordered]@{ source_sha = $env:GITHUB_SHA; phase = 'FIXTURE_OR_STARTUP_INCOMPLETE';
    managed_browser = 'BLOCKED'; renderer_identity = 'INCONCLUSIVE';
    sandbox_enforcement = 'INCONCLUSIVE'; subprocess_cleanup = 'INCONCLUSIVE' }
[IO.File]::WriteAllText($evidencePath, ($initial | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
$fixtureDllPath = [IO.Path]::GetFullPath($FixtureDll)
$trust = [IO.Path]::GetFullPath($TrustDirectory)
if (-not $trust.StartsWith($runner, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $trust -PathType Container) -or
    -not (Test-Path -LiteralPath $fixtureDllPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'Fixture inputs are unavailable.' }
$ready = Join-Path $runner ('aegis-p0-https-ready-' + [Guid]::NewGuid().ToString('N') + '.txt')
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $dotnet
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($arg in @($fixtureDllPath, 'serve', $trust, $ready)) { $start.ArgumentList.Add($arg) }
$fixture = [Diagnostics.Process]::Start($start)
if (-not $fixture) { throw 'Could not start the test-owned HTTPS fixture.' }
$fixtureOut = $fixture.StandardOutput.ReadToEndAsync()
$fixtureErr = $fixture.StandardError.ReadToEndAsync()
$previous = $ErrorActionPreference
try {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ready -PathType Leaf)) {
        if ($fixture.HasExited -or $deadline.Elapsed.TotalSeconds -ge 35) { throw 'HTTPS fixture did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $env:AEGIS_P0_HTTPS_URL = (Get-Content -LiteralPath $ready -Raw).Trim()
    if ($env:AEGIS_P0_HTTPS_URL -notmatch '^https://127\.0\.0\.1:[0-9]{1,5}/$') { throw 'Invalid fixture URL.' }
    $env:AEGIS_P0_MANAGED_EXECUTE = 'YES'
    $env:AEGIS_P0_MANAGED_DOTNET_ROOT = [IO.Path]::GetFullPath($DotnetRoot)
    $ErrorActionPreference = 'Continue'
    $output = @(& $supervisor 'supervise' (Join-Path $bundle 'p309f_client.exe') $manifest $manifestHash '90000' 2>&1)
    $supervisorExit = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previous
    if (-not $fixture.HasExited) {
        $fixture.StandardInput.WriteLine('stop')
        $fixture.StandardInput.Close()
        if (-not $fixture.WaitForExit(25000)) { $fixture.Kill(); $fixture.WaitForExit(); Write-Output 'HTTPS_FIXTURE_STOP_TIMEOUT' }
    }
    $fixtureExit = $fixture.ExitCode
    Write-Output ("HTTPS_FIXTURE_EXIT: {0}" -f $fixtureExit)
    Write-Output ($fixtureOut.GetAwaiter().GetResult())
    Write-Output ($fixtureErr.GetAwaiter().GetResult())
    if (Test-Path -LiteralPath $ready -PathType Leaf) { Remove-Item -LiteralPath $ready }
    $fixture.Dispose()
}
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
. (Join-Path $PSScriptRoot '../p309g/result-lib.ps1')
$identity = if ($identities.Count -eq 1) { $identities[0].identity } else { $null }
if ($null -ne $identity -and $identity.ImageSha256 -ceq $expectedBootstrap) {
    Write-Output "PROCESS_IDENTITY: PID=$($identity.Pid) CREATION_FILETIME_UTC=$($identity.CreationFileTimeUtc) IMAGE_SHA256=$($identity.ImageSha256)"
} else { Write-Output 'PROCESS_IDENTITY: UNVERIFIED' }
Write-Output "PROCESS_EXIT: $($record.result.ProcessExit)"
if ($null -eq $record.result.NativeExitCode) { Write-Output 'NATIVE_EXIT_CODE: UNAVAILABLE' }
else { Write-Output "NATIVE_EXIT_CODE: $($record.result.NativeExitCode)" }
Write-Output "PARENT_TERMINATION_REQUESTED: $($record.result.ParentTerminationRequested)"
Write-Output "OUTPUT_COMPLETE: $($record.result.OutputComplete)"
foreach ($line in @(($record.result.Stdout.Text + "`n" + $record.result.Stderr.Text) -split "`r?`n" | Select-Object -First 200)) { Write-Output $line }
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
    Write-Output "PIPE_RESTRICTING_SID_CLASS: $($renderer.PipeDaclDiagnostic.RestrictingSidClass)"
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
$markers = @('NATIVE_ENTRY: RunWinMain role=browser sandbox_info=present',
    'CEF_ENTRY: ExecuteProcess role=browser sandbox_info=present',
    'MANAGED_HOST: BROWSER_NATIVE_DISPATCH_COMPLETE', 'MANAGED_HOST: HOSTFXR_LOADED role=browser',
    'MANAGED_HOST: RUNTIME_CONFIG_ACCEPTED role=browser', 'MANAGED_HOST: CLR_ENTRY_REQUESTED role=browser sandbox_info=present',
    'MANAGED_EVENT: CEF_INITIALIZED_SANDBOX_CONTEXT_PRESENT', 'MANAGED_EVENT: WINDOW_SHOWN',
    'MANAGED_EVENT: LOCAL_PAGE_LOADED', 'MANAGED_EVENT: JAVASCRIPT_RESULT_VERIFIED',
    'MANAGED_EVENT: HTTPS_PAGE_VERIFIED', 'MANAGED_EVENT: BROWSER_BEFORE_CLOSE',
    'MANAGED_EVENT: WINFORMS_LOOP_RETURNED', 'MANAGED_EVENT: CEF_SHUTDOWN_RETURNED',
    'MANAGED_HOST: CLR_ENTRY_RETURNED code=0')
$missing = @($markers | Where-Object { -not $record.result.Stdout.Text.Contains($_) })
$evidence = [ordered]@{ source_sha = $env:GITHUB_SHA; process_exit = $record.result.ProcessExit;
    native_exit_code = $record.result.NativeExitCode; output_complete = $record.result.OutputComplete;
    missing_markers = $missing; renderer_identity = 'INCONCLUSIVE'; sandbox_enforcement = 'INCONCLUSIVE';
    subprocess_cleanup = 'INCONCLUSIVE'; https_fixture_exit = $fixtureExit }
[IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output ('MANAGED_EVIDENCE_JSON: ' + ($evidence | ConvertTo-Json -Depth 5 -Compress))
if ($fixtureExit -ne 0 -or $supervisorExit -ne 0 -or $record.result.NativeExitCode -ne 0 -or
    $record.result.ProcessExit -cne 'NORMAL' -or -not $record.result.OutputComplete -or
    $null -eq $identity -or $identity.ImageSha256 -cne $expectedBootstrap -or $missing.Count -ne 0) {
    throw ("Managed browser lifecycle did not satisfy its gates. Missing markers: " + ($missing -join ', '))
}
Write-Output 'MANAGED_BROWSER_LIFECYCLE: OBSERVED; SANDBOX_ENFORCEMENT: INCONCLUSIVE'
