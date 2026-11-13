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
using System.Text;
public static class AegisSmokeInput {
    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);

    public static bool IsOwnedBy(IntPtr hwnd, int expectedProcessId) {
        uint actualProcessId;
        return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out actualProcessId) != 0 && actualProcessId == (uint)expectedProcessId;
    }

    // Process.MainWindowHandle ignores hidden windows. Limit discovery to the
    // test-owned process and the unique expected title, even when hidden.
    public static IntPtr FindWindow(int expectedProcessId, string expectedTitle) {
        IntPtr match = IntPtr.Zero;
        EnumWindows((hwnd, parameter) => {
            if (!IsOwnedBy(hwnd, expectedProcessId)) return true;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (String.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal)) {
                match = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }
}
'@
    $process = Start-Process -FilePath $exe -ArgumentList "--profile=$profile" -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $browserHandle = [IntPtr]::Zero
    $expectedTitle = 'Aegis Browser ' + [char]0x2014 + ' ' + $profile
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Browser exited during startup: $($process.ExitCode)" }
        $browserHandle = [AegisSmokeInput]::FindWindow($process.Id, $expectedTitle)
        if ($browserHandle -ne [IntPtr]::Zero) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($browserHandle -eq [IntPtr]::Zero) { throw 'Expected browser window was not found for the test-owned process.' }
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
    if (![AegisSmokeInput]::IsOwnedBy($browserHandle, $process.Id)) { throw 'Browser window no longer belongs to the test-owned process.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($browserHandle)
    if (!$window) { throw 'Browser UI Automation root is unavailable.' }
    $addressCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Search or enter address')
    $addressElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $addressCondition)
    if (!$addressElement) { throw 'Browser address control was not found.' }
    $value = $addressElement.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue("http://127.0.0.1:$port/aegis-rc1-smoke")
    $addressHandle = [IntPtr]::new($addressElement.Current.NativeWindowHandle)
    if (![AegisSmokeInput]::IsOwnedBy($addressHandle, $process.Id)) { throw 'Browser address control is not owned by the test process.' }
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
    $privacyHandle = [IntPtr]::Zero
    $privacyTitle = 'Aegis Privacy ' + [char]0x2014 + ' profile settings and network inspector'
    $privacyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $privacyHandle = [AegisSmokeInput]::FindWindow($process.Id, $privacyTitle)
        if ($privacyHandle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 }
    } while ($privacyHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $privacyDeadline)
    if ($privacyHandle -eq [IntPtr]::Zero) { throw 'Privacy window did not open for the launched browser.' }
    Write-Output 'PASS: launched browser opened its Privacy window.'
    if (![AegisSmokeInput]::IsOwnedBy($browserHandle, $process.Id)) { throw 'Browser window no longer belongs to the test-owned process.' }
    # WM_CLOSE follows the normal form close path; it targets only the retained
    # test-owned browser window and does not terminate a Chromium process.
    $messageResult = [IntPtr]::Zero
    if ([AegisSmokeInput]::SendMessageTimeout($browserHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero,
            2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero) { throw 'Could not request a normal window close.' }
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
