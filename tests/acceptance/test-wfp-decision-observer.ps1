param([switch] $BuildOnly, [switch] $SelfTest)
$ErrorActionPreference = 'Stop'
if ($BuildOnly -eq $SelfTest) { throw 'Choose exactly one of -BuildOnly or -SelfTest.' }
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'MSVC x64 and the Windows SDK are required.' }
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
if ([string]::IsNullOrWhiteSpace($base)) { throw 'A temporary build directory is required.' }
$build = Join-Path $base ('aegis-wfp-observer-build-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $build) { throw 'Synthetic build directory already exists.' }
$null = New-Item -ItemType Directory -Path $build
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../containment/WfpDecisionObserver.cpp'))
$exe = Join-Path $build 'WfpDecisionObserver.exe'
$object = Join-Path $build 'WfpDecisionObserver.obj'
& cl.exe /nologo /EHsc /W4 /std:c++17 /D_WIN32_WINNT=0x0A00 "/Fe:$exe" "/Fo:$object" $source
if ($LASTEXITCODE -ne 0) { throw "WFP observer failed to compile: $LASTEXITCODE" }
Write-Output ('OBSERVED: test-only observer compiled at ' + $exe)
if ($SelfTest) {
    & $exe --self-test
    if ($LASTEXITCODE -ne 0) { throw "WFP observer synthetic self-test failed: $LASTEXITCODE" }
}
