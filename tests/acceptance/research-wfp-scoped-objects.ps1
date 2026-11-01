param([switch] $RunOnDisposableRunner, [switch] $BuildOnly)
$ErrorActionPreference = 'Stop'
if ($RunOnDisposableRunner -and $BuildOnly) { throw 'Choose either BuildOnly or RunOnDisposableRunner.' }
if (-not $BuildOnly -and (-not $RunOnDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Explicit opt-in on a disposable GitHub-hosted runner is required to install test filters.'
}
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'MSVC cl.exe and Windows SDK are required; use a Visual Studio developer shell.' }
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
if ([string]::IsNullOrWhiteSpace($base)) { throw 'A temporary build directory is required.' }
$build = Join-Path $base ('aegis-wfp-build-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $build) { throw 'Synthetic build directory already exists.' }
$null = New-Item -ItemType Directory -Path $build
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../containment/WfpScopedObjects.cpp'))
$exe = Join-Path $build 'WfpScopedObjects.exe'
$object = Join-Path $build 'WfpScopedObjects.obj'
& cl.exe /nologo /EHsc /W4 /std:c++17 /D_WIN32_WINNT=0x0A00 "/Fe:$exe" "/Fo:$object" $source
if ($LASTEXITCODE -ne 0) { throw "WFP research fixture failed to compile: $LASTEXITCODE" }
Write-Output ("OBSERVED: test-only executable built at {0}" -f $exe)
if ($BuildOnly) { return }
& $exe --disposable-runner
if ($LASTEXITCODE -ne 0) { throw "WFP research fixture failed or could not verify cleanup: $LASTEXITCODE" }
