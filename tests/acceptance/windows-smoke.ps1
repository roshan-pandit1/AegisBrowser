param([string]$PublishDirectory = "artifacts/win-x64")
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This test requires PowerShell 7 on Windows.' }
$exe = Join-Path (Resolve-Path $PublishDirectory) 'AegisBrowser.exe'
$profile = 'smoke-' + [guid]::NewGuid().ToString('N')
$profilePath = Join-Path $env:LOCALAPPDATA "AegisBrowser/Profiles/profile-$profile"
$process = $null
$listener = $null
$idlePeer = $null
$httpsHelper = $null
$httpsControl = $null
$httpsUrl = $null
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AegisSmokeInput {
    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendTextTimeout(IntPtr hwnd, uint msg, IntPtr wparam, string text, uint flags, uint timeout, out IntPtr result);

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

    // Require exactly one matching descendant of the test-owned browser.
    public static IntPtr FindUniqueChild(IntPtr parent, int expectedProcessId, string classPrefix, string expectedCaption) {
        if (!IsOwnedBy(parent, expectedProcessId)) return IntPtr.Zero;
        IntPtr match = IntPtr.Zero;
        int count = 0;
        EnumChildWindows(parent, (hwnd, parameter) => {
            if (!IsOwnedBy(hwnd, expectedProcessId) || !IsChild(parent, hwnd)) return true;
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (!className.ToString().StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (expectedCaption != null) {
                var caption = new StringBuilder(512);
                GetWindowText(hwnd, caption, caption.Capacity);
                if (!String.Equals(caption.ToString(), expectedCaption, StringComparison.Ordinal)) return true;
            }
            match = hwnd;
            count++;
            return true;
        }, IntPtr.Zero);
        if (count > 1) throw new InvalidOperationException("More than one matching browser control was found.");
        return match;
    }
}
'@
    if ($env:P403_PACKAGE_TRUST_DIR) {
        if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CI -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
            throw 'The HTTPS package fixture requires a disposable GitHub-hosted runner.'
        }
        $httpsControl = Join-Path $env:RUNNER_TEMP ('aegis-rc1-https-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $httpsControl -ErrorAction Stop | Out-Null
        $helperExe = (Resolve-Path './AegisBrowser.BrowserTests/bin/Release/net10.0-windows/win-x64/AegisBrowser.BrowserTests.exe').Path
        $helperOut = Join-Path $httpsControl 'helper.out.log'
        $helperErr = Join-Path $httpsControl 'helper.err.log'
        $httpsHelper = Start-Process -FilePath $helperExe -ArgumentList @('https-package-server', ('"' + $env:P403_PACKAGE_TRUST_DIR + '"'), ('"' + $httpsControl + '"')) -WindowStyle Hidden -RedirectStandardOutput $helperOut -RedirectStandardError $helperErr -PassThru
        $readyFile = Join-Path $httpsControl 'ready.txt'
        $readyWatch = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $readyFile) -and $readyWatch.ElapsedMilliseconds -lt 45000) {
            $httpsHelper.Refresh()
            if ($httpsHelper.HasExited) { throw "Controlled HTTPS helper exited before readiness (exit $($httpsHelper.ExitCode))." }
            Start-Sleep -Milliseconds 100
        }
        if (!(Test-Path -LiteralPath $readyFile)) { throw 'Controlled HTTPS helper did not become ready.' }
        $httpsUrl = (Get-Content -LiteralPath $readyFile -Raw).Trim()
        $parsedUrl = [uri]$httpsUrl
        if ($parsedUrl.Scheme -cne 'https' -or $parsedUrl.Host -cne '127.0.0.1' -or
            $parsedUrl.AbsolutePath -cne '/rc1-package-https' -or $parsedUrl.Query -ne '' -or $parsedUrl.Fragment -ne '') {
            throw 'Unexpected controlled HTTPS fixture address.'
        }
    }
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
    # A test-owned connection that sends no data deterministically exercises
    # the idle-socket path. It cannot satisfy the HTTP navigation assertion.
    $idlePeer = [Net.Sockets.TcpClient]::new()
    if (!$idlePeer.ConnectAsync([Net.IPAddress]::Loopback, $port).Wait(2000)) { throw 'Controlled idle connection could not start.' }
    $idlePort = ([Net.IPEndPoint]$idlePeer.Client.LocalEndPoint).Port
    if (![AegisSmokeInput]::IsOwnedBy($browserHandle, $process.Id)) { throw 'Browser window no longer belongs to the test-owned process.' }
    $addressHandle = [AegisSmokeInput]::FindUniqueChild($browserHandle, $process.Id, 'WindowsForms10.EDIT.', $null)
    if ($addressHandle -eq [IntPtr]::Zero) { throw 'Unique browser address control was not found.' }
    $messageResult = [IntPtr]::Zero
    # WM_SETTEXT is delivered only to the selected test-owned address control.
    if ([AegisSmokeInput]::SendTextTimeout($addressHandle, 0x000C, [IntPtr]::Zero,
            "http://127.0.0.1:$port/aegis-rc1-smoke", 2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero -or
            $messageResult -eq [IntPtr]::Zero) { throw 'Browser address control did not accept the controlled URL.' }
    $messageResult = [IntPtr]::Zero
    # WM_KEYDOWN/WM_KEYUP directed only to this browser's address control;
    # no global keyboard input or foreground-window change is used.
    foreach ($message in @(0x0100, 0x0101)) {
        if ([AegisSmokeInput]::SendMessageTimeout($addressHandle, $message, [IntPtr]13, [IntPtr]0,
                2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero) { throw 'Address control did not accept navigation input.' }
    }
    # Chromium can open a connection before sending a request. Keep a bounded
    # set of test-owned sockets and observe their request lines independently;
    # an idle socket must not prevent another socket from delivering the page.
    $clients = [Collections.Generic.List[object]]::new()
    $accepted = 0
    $responded = $false
    $idleObserved = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (!$responded -and $watch.ElapsedMilliseconds -lt 15000) {
            while ($listener.Pending()) {
                if (++$accepted -gt 16) { throw 'Too many controlled loopback connections.' }
                $client = $listener.AcceptTcpClient()
                try {
                    if (([Net.IPEndPoint]$client.Client.RemoteEndPoint).Port -eq $idlePort) { $idleObserved = $true }
                    $client.SendTimeout = 2000
                    $stream = $client.GetStream()
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
                    $clients.Add([pscustomobject]@{ Client = $client; Stream = $stream; Reader = $reader; Line = $reader.ReadLineAsync() })
                } catch { $client.Dispose(); throw }
            }
            for ($i = $clients.Count - 1; $i -ge 0; $i--) {
                $connection = $clients[$i]
                if (!$connection.Line.IsCompleted) { continue }
                $requestLine = $connection.Line.GetAwaiter().GetResult()
                if ([string]::IsNullOrEmpty($requestLine)) {
                    $connection.Client.Dispose()
                    $connection.Reader.Dispose()
                    $clients.RemoveAt($i)
                    continue
                }
                if ($requestLine -notmatch '^GET /aegis-rc1-smoke HTTP/1\.[01]$') { throw 'Unexpected controlled navigation request.' }
                $headerCount = 0
                do {
                    if (++$headerCount -gt 100) { throw 'Too many controlled request headers.' }
                    $lineTask = $connection.Reader.ReadLineAsync()
                    $remaining = 15000 - [int]$watch.ElapsedMilliseconds
                    if ($remaining -le 0 -or !$lineTask.Wait($remaining)) { throw 'Controlled request headers timed out.' }
                    $line = $lineTask.GetAwaiter().GetResult()
                    if ($null -eq $line) { throw 'Controlled request ended before its headers.' }
                } while ($line.Length -gt 0)
                $body = '<!doctype html><title>Aegis RC1 fixture</title><p>Controlled local page</p>'
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $headers = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
                $connection.Stream.Write($headers, 0, $headers.Length)
                $connection.Stream.Write($bytes, 0, $bytes.Length)
                $connection.Stream.Flush()
                $responded = $true
                break
            }
            if (!$responded) { Start-Sleep -Milliseconds 50 }
        }
        if (!$responded) { throw "Controlled loopback navigation did not deliver a request within 15 seconds (connections: $accepted)." }
        if (!$idleObserved) { throw 'Controlled idle connection was not observed.' }
    } finally {
        foreach ($connection in $clients) {
            $connection.Client.Dispose()
            $connection.Reader.Dispose()
        }
    }
    Write-Output 'PASS: a controlled idle loopback connection did not prevent navigation.'
    Write-Output 'PASS: launched browser requested the controlled loopback HTTP page.'

    if ($httpsUrl) {
        if (![AegisSmokeInput]::IsOwnedBy($addressHandle, $process.Id)) { throw 'Browser address control no longer belongs to the test process.' }
        $messageResult = [IntPtr]::Zero
        if ([AegisSmokeInput]::SendTextTimeout($addressHandle, 0x000C, [IntPtr]::Zero, $httpsUrl,
                2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero -or $messageResult -eq [IntPtr]::Zero) {
            throw 'Browser address control did not accept the controlled HTTPS URL.'
        }
        foreach ($message in @(0x0100, 0x0101)) {
            if ([AegisSmokeInput]::SendMessageTimeout($addressHandle, $message, [IntPtr]13, [IntPtr]0,
                    2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero) { throw 'HTTPS navigation input was not accepted.' }
        }
        $observedFile = Join-Path $httpsControl 'observed.txt'
        $observedWatch = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $observedFile) -and $observedWatch.ElapsedMilliseconds -lt 25000) {
            $httpsHelper.Refresh()
            if ($httpsHelper.HasExited) { throw "Controlled HTTPS helper exited before the request (exit $($httpsHelper.ExitCode))." }
            Start-Sleep -Milliseconds 100
        }
        if (!(Test-Path -LiteralPath $observedFile) -or (Get-Content -LiteralPath $observedFile -Raw).Trim() -cne 'https-request-observed') {
            throw 'The launched browser did not deliver the controlled HTTPS request.'
        }
        Write-Output 'PASS: launched browser delivered controlled HTTPS page and same-origin subresource requests under normal certificate validation.'
    }

    $privacyButton = [AegisSmokeInput]::FindUniqueChild($browserHandle, $process.Id, 'WindowsForms10.BUTTON.', 'Privacy')
    if ($privacyButton -eq [IntPtr]::Zero) { throw 'Unique Privacy toolbar action was not found.' }
    $messageResult = [IntPtr]::Zero
    # BM_CLICK invokes the selected button without global input or focus changes.
    if ([AegisSmokeInput]::SendMessageTimeout($privacyButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero,
            2, 2000, [ref]$messageResult) -eq [IntPtr]::Zero) { throw 'Privacy toolbar action did not accept the click.' }
    $privacyHandle = [IntPtr]::Zero
    $privacyTitle = 'Privacy settings'
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
    if (!$httpsUrl) { Write-Output 'NOT TESTED: HTTPS/TLS (the hosted-runner fixture was not enabled).' }
    Write-Output 'NOT TESTED: permissions, cookie behavior or visual correctness.'
    if ($httpsHelper) {
        Set-Content -LiteralPath (Join-Path $httpsControl 'stop.txt') -Value 'stop'
        if (!$httpsHelper.WaitForExit(15000)) { throw 'Controlled HTTPS helper did not shut down.' }
        if ($httpsHelper.ExitCode -ne 0) { throw "Controlled HTTPS helper failed (exit $($httpsHelper.ExitCode))." }
        $helperLog = Get-Content -LiteralPath $helperOut -Raw
        if ($helperLog -notmatch 'PASS: controlled HTTPS server observed the package navigation' -or
            $helperLog -notmatch 'P403_TRUST: temporary user root removed') {
            throw 'Controlled HTTPS helper did not confirm its request and root removal.'
        }
        Write-Output 'PASS: controlled HTTPS helper removed its exact test root.'
    }
}
finally {
    if ($idlePeer) { $idlePeer.Dispose() }
    if ($listener) { $listener.Stop() }
    if ($process) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
    if ($httpsHelper) {
        if (!$httpsHelper.HasExited) {
            try { Set-Content -LiteralPath (Join-Path $httpsControl 'stop.txt') -Value 'stop' } catch { }
            if (!$httpsHelper.WaitForExit(15000)) { $httpsHelper.Kill(); $httpsHelper.WaitForExit() }
        }
        $httpsHelper.Dispose()
    }
    # Keep the isolated profile/logs for failure investigation. Do not delete any user profile.
    Write-Output "Smoke-test profile/logs: $profilePath"
}
