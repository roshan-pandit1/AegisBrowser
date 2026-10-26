param([string]$PublishDirectory = "artifacts/win-x64", [switch]$Lab)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This test requires PowerShell 7 on Windows.' }
$exe = Join-Path (Resolve-Path $PublishDirectory) 'AegisBrowser.exe'
$profile = 'smoke-' + [guid]::NewGuid().ToString('N')
$profilePath = Join-Path $env:LOCALAPPDATA "AegisBrowser/Profiles/profile-$profile"
$launchArgument = "--profile=$profile"
$expectedTitle = "Aegis Browser — $profile"
if ($Lab) {
    $launchArgument = '--lab=baseline'
    $profilePath = Join-Path $env:LOCALAPPDATA 'AegisBrowser/LabProfiles/Baseline'
    $expectedTitle = 'Aegis authorized testing lab — Baseline'
}
$process = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList $launchArgument -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ready = $false
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Browser exited during startup: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq $expectedTitle) {
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
    # Capture this application's descendants only; do not inspect command lines or other profiles.
    $snapshot = @(Get-CimInstance Win32_Process)
    $owned = [Collections.Generic.HashSet[int]]::new()
    [void]$owned.Add($process.Id)
    do {
        $added = $false
        foreach ($child in $snapshot) {
            if ($owned.Contains([int]$child.ParentProcessId) -and $owned.Add([int]$child.ProcessId)) { $added = $true }
        }
    } while ($added)
    $descendants = @($snapshot | Where-Object { $_.ProcessId -ne $process.Id -and $owned.Contains([int]$_.ProcessId) })
    if (!$process.CloseMainWindow()) { throw 'Could not request a normal window close.' }
    if (!$process.WaitForExit(15000)) { throw 'Browser did not exit within 15 seconds of normal close.' }
    if ($process.ExitCode -ne 0) { throw "Browser exited with code $($process.ExitCode)" }
    foreach ($child in $descendants) {
        $remaining = Get-CimInstance Win32_Process -Filter "ProcessId = $($child.ProcessId)"
        if ($remaining -and $remaining.CreationDate -eq $child.CreationDate) {
            throw "Browser descendant remained after shutdown: $($child.Name) ($($child.ProcessId))"
        }
    }
    Write-Output "PASS: observed application descendants exited ($($descendants.Count))"
    Write-Output "PASS: Windows published-app window startup, profile cache creation and normal close. Lab=$Lab"
    Write-Output 'NOT TESTED: rendered content, navigation, permissions, cookie behavior or visual correctness.'
}
finally {
    if ($process) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
    # Keep the isolated profile/logs for failure investigation. Do not delete any user profile.
    Write-Output "Smoke-test profile/logs: $profilePath"
}
