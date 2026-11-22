[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $env:RUNNER_TEMP -or -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'A dedicated runner temporary directory is required.'
}
if ((& dotnet --version).Trim() -cne '10.0.404' -or $LASTEXITCODE -ne 0) { throw 'Unexpected SDK.' }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $PSScriptRoot 'DependencyCompat/DependencyCompat.csproj'
[xml]$core = Get-Content -LiteralPath (Join-Path $repo 'AegisBrowser.Core/AegisBrowser.Core.csproj') -Raw
$references = @($core.Project.ItemGroup.PackageReference)
foreach ($name in @('Microsoft.Data.Sqlite', 'System.Security.Cryptography.ProtectedData')) {
    $matches = @($references | Where-Object { $_.Include -ceq $name -and $_.Version -ceq '10.0.15' })
    if ($matches.Count -ne 1) { throw 'Current core package pins differ from this compatibility fixture.' }
}

$runner = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\','/')
if (((Get-Item -LiteralPath $runner -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Linked runner temporary root rejected.'
}
$runId = [guid]::NewGuid().ToString('N')
$root = Join-Path $runner ('aegis-dependency-compat-' + $runId)
if (Test-Path -LiteralPath $root) { throw 'Fixture root already exists.' }
$null = New-Item -ItemType Directory -Path $root

function Build-Generation([string]$generation, [string]$sqlite, [string]$protectedData) {
    $out = Join-Path $root ($generation + '-out')
    $obj = (Join-Path $root ($generation + '-obj')) + '/'
    & dotnet build $project -c Release -o $out -p:SelfContained=false "-p:CompatGeneration=$generation" `
        "-p:BaseIntermediateOutputPath=$obj" "-p:MSBuildProjectExtensionsPath=$obj" --nologo | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Compatibility build failed: $generation" }
    $assets = Get-Content -LiteralPath (Join-Path $obj 'project.assets.json') -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    foreach ($pin in @("Microsoft.Data.Sqlite/$sqlite", "Microsoft.Data.Sqlite.Core/$sqlite", "System.Security.Cryptography.ProtectedData/$protectedData")) {
        if ($libraries -cnotcontains $pin) { throw "Resolved compatibility package mismatch: $pin" }
    }
    $exe = Join-Path $out 'DependencyCompat.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Compatibility executable missing.' }
    Write-Host "COMPAT_BUILD: generation=$generation sqlite=$sqlite protected_data=$protectedData"
    return $exe
}

function Write-SafeFailure([string]$phase, [string]$errorText) {
    $detail = $errorText.Trim()
    if ($detail -cmatch '^COMPAT_FAILURE=([A-Z_]+|[A-Za-z][A-Za-z0-9]+Exception)$') {
        Write-Host ("COMPAT_PHASE_FAILURE: phase=$phase detail=" + $Matches[1])
    } else { Write-Host "COMPAT_PHASE_FAILURE: phase=$phase detail=UNEXPECTED_OUTPUT" }
}

function Invoke-Phase([string]$exe, [string]$generation, [string]$phase) {
    $process = [Diagnostics.Process]::new()
    $started = $false
    try {
        $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
        $process.StartInfo.FileName = $exe
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardInput = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        foreach ($arg in @($phase, $root, $runId)) { $process.StartInfo.ArgumentList.Add($arg) }
        if (-not $process.Start()) { throw 'Could not launch compatibility process.' }
        $started = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        $readyTask = $process.StandardOutput.ReadLineAsync()
        if (-not $readyTask.Wait([TimeSpan]::FromSeconds(30))) { throw 'Compatibility process did not reach the identity barrier.' }
        if ($null -eq $readyTask.Result) {
            if (-not $process.WaitForExit(5000)) { throw 'Compatibility process closed output without exiting.' }
            Write-SafeFailure $phase ($stderr.GetAwaiter().GetResult())
            throw "Compatibility phase failed before identity barrier: $phase (exit $($process.ExitCode))."
        }
        $ready = $readyTask.Result -split '\|'
        if ($ready.Count -ne 4 -or $ready[0] -cne 'READY' -or $ready[1] -cne $generation -or
            $ready[2] -cne $process.Id.ToString() -or $ready[3] -cne $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() -or
            -not [string]::Equals([IO.Path]::GetFullPath($process.MainModule.FileName), [IO.Path]::GetFullPath($exe), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Compatibility process identity did not match the retained process.'
        }
        $process.StandardInput.WriteLine($runId)
        $process.StandardInput.Close()
        $tail = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { throw 'Compatibility phase timed out.' }
        $stdout = $tail.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $stdout.Trim() -cne "PASS_COMPAT_PHASE=$phase" -or $errorText.Length -ne 0) {
            Write-SafeFailure $phase $errorText
            throw "Compatibility phase failed: $phase (exit $($process.ExitCode))."
        }
        Write-Host "PASS_COMPAT_PHASE=$phase process_id=$($process.Id)"
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'Compatibility child termination was not confirmed.' }
        }
        $process.Dispose()
    }
}

try {
    $old = Build-Generation 'old' '8.0.33' '8.0.0'
    $new = Build-Generation 'new' '10.0.15' '10.0.15'
    if ([string]::Equals($old, $new, [StringComparison]::OrdinalIgnoreCase)) { throw 'Compatibility outputs must be distinct.' }
    Invoke-Phase $old 'old' 'old-write'
    Invoke-Phase $new 'new' 'new-update'
    Invoke-Phase $old 'old' 'old-check'
    Invoke-Phase $new 'new' 'new-check'
    Write-Host 'DEPENDENCY_COMPATIBILITY: PASS (synthetic profile metadata and CurrentUser DPAPI; no browser cache assertion)'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith($runner + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cne ('aegis-dependency-compat-' + $runId) -or
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Fixture cleanup identity check failed.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
