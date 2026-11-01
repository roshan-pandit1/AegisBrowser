# Unprivileged validation only: every client input is rejected before socket creation.
param([Parameter(Mandatory = $true)][string] $BrowserTestsExe)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Run validation only on a disposable GitHub-hosted runner.'
}
$files = @('receive-private-network-control.ps1', 'invoke-private-network-control.ps1')
foreach ($file in $files) {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref] $tokens, [ref] $errors) | Out-Null
    if ($errors.Count -ne 0) { throw ('Script parser rejected ' + $file) }
}
$exe = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetFileName($exe) -ine 'AegisBrowser.BrowserTests.exe') { throw 'Fixed synthetic test executable required.' }
$root = Join-Path $env:RUNNER_TEMP ('aegis-private-validation-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Synthetic test root already exists.' }
$null = New-Item -ItemType Directory -Path $root
$nonce = [guid]::NewGuid().ToString('N')
$cases = @(
    @{ name = 'loopback'; address = '127.0.0.1'; port = '47123'; nonce = $nonce },
    @{ name = 'public'; address = '8.8.8.8'; port = '47123'; nonce = $nonce },
    @{ name = 'multicast'; address = '224.0.0.1'; port = '47123'; nonce = $nonce },
    @{ name = 'ipv6'; address = '::1'; port = '47123'; nonce = $nonce },
    @{ name = 'noncanonical'; address = '10.1'; port = '47123'; nonce = $nonce },
    @{ name = 'privileged-port'; address = '10.1.2.3'; port = '80'; nonce = $nonce },
    @{ name = 'malformed-nonce'; address = '10.1.2.3'; port = '47123'; nonce = 'bad' }
)
foreach ($case in $cases) {
    $dir = Join-Path $root $case.name
    $null = New-Item -ItemType Directory -Path $dir
    [IO.File]::WriteAllLines((Join-Path $dir 'private-network-control.txt'), @($case.address, $case.port, $case.nonce))
    $process = Start-Process -FilePath $exe -ArgumentList @('private-network-control-client', ('"{0}"' -f $dir)) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Owned invalid-input client did not terminate.' }
        throw ('Invalid-input client timed out: ' + $case.name)
    }
    $process.Refresh()
    $result = Join-Path $dir 'private-network-result.txt'
    if ($process.ExitCode -ne 1 -or -not (Test-Path -LiteralPath $result) -or [IO.File]::ReadAllText($result) -cne 'unavailable:InvalidDataException') {
        throw ('Invalid input was not rejected by the synthetic client: ' + $case.name)
    }
    Write-Output ('PASS: rejected synthetic input before socket creation: ' + $case.name)
}
Write-Output ('PASS: parsed both scripts and rejected ' + $cases.Count + ' invalid client inputs without network access.')
Write-Output ('Synthetic validation files retained until disposable runner destruction: ' + $root)
