param([string] $Dotnet = 'dotnet')
# Offline metadata-only checks. The fixture assembly is this test tool itself;
# a positive result does not establish native load, ABI or sandbox safety.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FrameworkMetadata\FrameworkMetadata.csproj'
$assembly = Join-Path $PSScriptRoot 'FrameworkMetadata\bin\Release\net8.0\FrameworkMetadata.dll'
& $Dotnet build $project -c Release --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "Metadata tool build failed: $LASTEXITCODE" }
$cases = @(
    @{ Arguments = @($assembly, '8'); Expected = 0; Name = 'net8 fixture accepted at major 8' },
    @{ Arguments = @($assembly, '6'); Expected = 1; Name = 'net8 fixture rejected at major 6' },
    @{ Arguments = @(); Expected = 2; Name = 'missing arguments rejected' },
    @{ Arguments = @($assembly, '0'); Expected = 2; Name = 'invalid maximum major rejected' },
    @{ Arguments = @((Join-Path $PSScriptRoot 'missing-assembly.dll'), '8'); Expected = 1; Name = 'missing file rejected' },
    @{ Arguments = @($project, '8'); Expected = 1; Name = 'non-PE file rejected' }
)
foreach ($case in $cases) {
    # Expected native stderr is captured without turning it into an exception;
    # the actual process exit is asserted for every test.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $Dotnet $assembly @($case.Arguments) 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne $case.Expected) { throw "FAIL: $($case.Name): expected $($case.Expected), got $code. $($output -join ' ')" }
    Write-Output "PASS: $($case.Name)"
}
Write-Output "PASS: $($cases.Count) framework metadata checks (metadata only)"
# The last native failure was an asserted negative case, not a suite failure.
$global:LASTEXITCODE = 0
