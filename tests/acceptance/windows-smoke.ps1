param([string]$PublishDirectory = "artifacts/win-x64")
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This test requires PowerShell 7 on Windows.' }
$exe = Join-Path (Resolve-Path $PublishDirectory) 'AegisBrowser.exe'
$profile = 'smoke-' + [guid]::NewGuid().ToString('N')
$profilePath = Join-Path $env:LOCALAPPDATA "AegisBrowser/Profiles/profile-$profile"
$process = $null
$listener = $null
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AegisSmokeInput {
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
}
'@
    $process = Start-Process -FilePath $exe -ArgumentList "--profile=$profile" -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ready = $false
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Browser exited during startup: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq "Aegis Browser — $profile") {
            $ready = $true
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$ready) { throw "Expected browser window was not found. Last title: $($process.MainWindowTitle)" }
    # Let asynchronous Chromium startup progress; an early window alone is insufficient.
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw 'Browser exited immediately after showing its window.' }
    if (!(Test-Path (Join-Path $profilePath 'Cache'))) { throw 'Persistent Chromium cache directory was not created.' }

    # Exercise the launched executable itself with a test-owned loopback HTTP
    # endpoint. This is independent evidence of a navigation; it is not a TLS test.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $accept = $listener.AcceptTcpClientAsync()
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if (!$window) { throw 'Browser UI Automation root is unavailable.' }
    $addressCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Search or enter address')
    $addressElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $addressCondition)
    if (!$addressElement) { throw 'Browser address control was not found.' }
    $value = $addressElement.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue("http://127.0.0.1:$port/aegis-rc1-smoke")
    $addressHandle = [IntPtr]::new($addressElement.Current.NativeWindowHandle)
    if ($addressHandle -eq [IntPtr]::Zero) { throw 'Browser address control has no native handle.' }
    $messageResult = [IntPtr]::Zero
    # WM_KEYDOWN/WM_KEYUP directed only to this browser's address control;
    # no global keyboard input or foreground-window change is used.
    foreach ($message in @(0x0100, 0x0101)) {
        if ([AegisSmokeInput]::SendMessageTimeout($addressHandle, $message, [IntPtr]13, [IntPtr]0,
                2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero) { throw 'Address control did not accept navigation input.' }
    }
    if (!$accept.Wait(15000)) { throw 'Controlled loopback navigation did not reach the listener.' }
    $client = $accept.GetAwaiter().GetResult()
    try {
        $client.ReceiveTimeout = 5000
        $stream = $client.GetStream()
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
        $requestLine = $reader.ReadLine()
        if ($requestLine -notmatch '^GET /aegis-rc1-smoke HTTP/1\.[01]$') { throw 'Unexpected controlled navigation request.' }
        while (($line = $reader.ReadLine()) -and $line.Length -gt 0) { }
        $body = '<!doctype html><title>Aegis RC1 fixture</title><p>Controlled local page</p>'
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $headers = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
        $stream.Write($headers, 0, $headers.Length)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    } finally { $client.Dispose() }
    Write-Output 'PASS: launched browser requested the controlled loopback HTTP page.'

    $privacyCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Privacy')
    $privacyButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $privacyCondition)
    if (!$privacyButton) { throw 'Privacy toolbar action was not found.' }
    $invoke = $privacyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $ownerCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,
            ('Aegis Privacy ' + [char]0x2014 + ' profile settings and network inspector')))
    $privacyWindow = $null
    $privacyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $privacyWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $ownerCondition)
        if (!$privacyWindow) { Start-Sleep -Milliseconds 100 }
    } while (!$privacyWindow -and [DateTime]::UtcNow -lt $privacyDeadline)
    if (!$privacyWindow) { throw 'Privacy window did not open for the launched browser.' }
    Write-Output 'PASS: launched browser opened its Privacy window.'
    if (!$process.CloseMainWindow()) { throw 'Could not request a normal window close.' }
    if (!$process.WaitForExit(15000)) { throw 'Browser did not exit within 15 seconds of normal close.' }
    if ($process.ExitCode -ne 0) { throw "Browser exited with code $($process.ExitCode)" }
    Write-Output 'PASS: Windows published-app window startup, profile cache creation and normal close.'
    Write-Output 'NOT TESTED: HTTPS/TLS, rendered content, permissions, cookie behavior or visual correctness.'
}
finally {
    if ($listener) { $listener.Stop() }
    if ($process) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
    # Keep the isolated profile/logs for failure investigation. Do not delete any user profile.
    Write-Output "Smoke-test profile/logs: $profilePath"
}
