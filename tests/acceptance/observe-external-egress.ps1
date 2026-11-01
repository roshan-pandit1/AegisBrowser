param(
    [string] $BrowserTestsExe,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ExternalEgressFixture.cs')
function Send-ControlRequest([int] $Port, [string] $Target, [string] $Expected = 'HTTP/1.1 200 OK') {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect([Net.IPAddress]::Loopback, $Port)
        $stream = $client.GetStream()
        $bytes = [Text.Encoding]::ASCII.GetBytes("GET $Target HTTP/1.1`r`nHost: 127.0.0.1`r`nConnection: close`r`n`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $reader = New-Object System.IO.StreamReader($stream)
        $line = $reader.ReadLine()
        if ($line -ne $Expected) { throw 'The controlled endpoint returned an unexpected response.' }
    }
    finally { $client.Dispose() }
}
function Wait-Counter([scriptblock] $Predicate) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (& $Predicate)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The controlled observer did not receive its positive control.' }
        Start-Sleep -Milliseconds 25
    }
}

$control = New-Object AegisContainmentResearch.ExternalEgressFixture
try {
    Send-ControlRequest $control.OriginPort '/control'
    Send-ControlRequest $control.ProxyPort ("http://127.0.0.1:{0}/external-target" -f $control.OriginPort)
    Send-ControlRequest $control.ProxyPort 'http://unapproved.invalid/' 'HTTP/1.1 403 Forbidden'
    $packet = New-Object byte[] 20
    $packet[4] = 0x21; $packet[5] = 0x12; $packet[6] = 0xa4; $packet[7] = 0x42
    $sender = New-Object System.Net.Sockets.UdpClient
    try { $null = $sender.Send($packet, $packet.Length, '127.0.0.1', $control.StunPort) }
    finally { $sender.Dispose() }
    Wait-Counter { $control.OriginControl -eq 1 -and $control.ProxyTarget -eq 1 -and $control.StunPackets -eq 1 }
    if ($control.Error) { throw $control.Error }
    Write-Output 'PASS: independent TCP origin, HTTP proxy and UDP listeners received positive controls and the proxy rejected an unapproved target'
}
finally { $control.Dispose() }
if ($SelfTest) { return }

if ([string]::IsNullOrWhiteSpace($BrowserTestsExe) -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'BrowserTestsExe and RUNNER_TEMP are required for the disposable fixture.'
}
$testExecutable = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) { throw 'Browser test executable is missing.' }
$runDirectory = Join-Path $env:RUNNER_TEMP ('aegis-external-egress-' + [guid]::NewGuid().ToString('N'))
$barrier = Join-Path $runDirectory 'barrier'
New-Item -ItemType Directory -Path $barrier -Force | Out-Null
$output = Join-Path $runDirectory 'browser.out.log'
$errors = Join-Path $runDirectory 'browser.err.log'
$names = @('AEGIS_TEST_EXTERNAL_PROXY_PORT', 'AEGIS_TEST_EXTERNAL_ORIGIN_PORT', 'AEGIS_TEST_EXTERNAL_STUN_PORT', 'AEGIS_TEST_EXTERNAL_EGRESS_BARRIER')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$fixture = New-Object AegisContainmentResearch.ExternalEgressFixture
$process = $null
try {
    Send-ControlRequest $fixture.OriginPort '/control'
    $env:AEGIS_TEST_EXTERNAL_PROXY_PORT = [string]$fixture.ProxyPort
    $env:AEGIS_TEST_EXTERNAL_ORIGIN_PORT = [string]$fixture.OriginPort
    $env:AEGIS_TEST_EXTERNAL_STUN_PORT = [string]$fixture.StunPort
    $env:AEGIS_TEST_EXTERNAL_EGRESS_BARRIER = $barrier
    $profile = Join-Path $runDirectory 'synthetic-profile'
    $arguments = @('external-egress-probe', ('"{0}"' -f $profile))
    $process = Start-Process -FilePath $testExecutable -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $testExecutable) `
        -RedirectStandardOutput $output -RedirectStandardError $errors -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    $proxyStopped = $false
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (-not $proxyStopped -and (Test-Path -LiteralPath (Join-Path $barrier 'stop-proxy'))) {
            $fixture.StopProxy()
            $proxyStopped = $true
            [IO.File]::WriteAllText((Join-Path $barrier 'proxy-stopped'), 'stopped')
        }
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }
    if (-not $process.HasExited) { throw 'The synthetic egress browser did not exit in time.' }
    $process.WaitForExit()
    $result = (Get-Content -LiteralPath $output -Raw) + "`n" + (Get-Content -LiteralPath $errors -Raw)
    Write-Output $result
    if ($fixture.Error) { throw "Independent observer failed: $($fixture.Error)" }
    if ($process.ExitCode -ne 0 -or $result -match 'Check failed:' -or
        $result -notmatch 'PASS: external egress CEF fixture and native shutdown completed' -or -not $proxyStopped) {
        throw 'The synthetic egress browser failed or did not complete native shutdown.'
    }
    if ($fixture.OriginControl -ne 1 -or $fixture.ProxyTarget -lt 1 -or $fixture.OriginTarget -ne 0 -or $fixture.OriginOffline -ne 0) {
        throw 'The external loopback proxy/origin observations did not match the controlled scenario.'
    }
    Write-Output ("OBSERVED: external proxy requests={0}, direct target requests={1}, direct offline requests={2}, origin controls={3}" -f `
        $fixture.ProxyTarget, $fixture.OriginTarget, $fixture.OriginOffline, $fixture.OriginControl)
    if ($fixture.StunPackets -gt 0) {
        Write-Output ("OBSERVED: external loopback STUN listener received {0} UDP packet(s)" -f $fixture.StunPackets)
    } else {
        Write-Output 'INCONCLUSIVE: external loopback STUN listener observed no STUN packet; this does not prove UDP containment.'
    }
    Write-Output 'INCONCLUSIVE: observations cover only these loopback endpoints and do not establish network isolation or fail-closed behavior.'
}
finally {
    if ($null -ne $process -and -not (Test-Path -LiteralPath (Join-Path $barrier 'proxy-stopped'))) {
        $fixture.StopProxy()
        [IO.File]::WriteAllText((Join-Path $barrier 'proxy-stopped'), 'stopped')
    }
    if ($null -ne $process -and -not $process.HasExited -and -not $process.WaitForExit(150000)) {
        Write-Warning 'Synthetic egress browser did not exit after release; it was not forcibly terminated.'
    }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $fixture.Dispose()
}
