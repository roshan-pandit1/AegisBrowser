[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verifier = Join-Path $PSScriptRoot 'verify-dotnet-notices.ps1'
$notices = Join-Path $PSScriptRoot '../../third-party-licenses'
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$root = Join-Path $tempRoot ('aegis-dotnet-notice-controls-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$wrongCore = Join-Path $root 'coreclr.dll'
$noticeFile = Join-Path $root 'DOTNET-RUNTIME-LICENSE.TXT'
[IO.File]::WriteAllText($wrongCore, 'not a runtime binary')
[IO.File]::WriteAllText($noticeFile, 'not a runtime notice')
try {
    & $verifier -PublishDirectory $PublishDirectory -NoticeDirectory $notices
    foreach ($case in @(
        @{ publish=$root; notices=$notices; expected='DOTNET_NOTICES_MISMATCH: coreclr.dll' },
        @{ publish=$PublishDirectory; notices=$root; expected='DOTNET_NOTICES_MISMATCH: DOTNET-RUNTIME-LICENSE.TXT' }
    )) {
        $rejected = $false
        try { & $verifier -PublishDirectory $case.publish -NoticeDirectory $case.notices }
        catch {
            if ($_.Exception.Message -ceq $case.expected) { $rejected = $true } else { throw }
        }
        if (-not $rejected) { throw 'Invalid .NET notice control unexpectedly passed.' }
    }
    [IO.File]::Delete($noticeFile)
    $rejected = $false
    try { & $verifier -PublishDirectory $PublishDirectory -NoticeDirectory $root }
    catch {
        if ($_.Exception.Message -ceq 'DOTNET_NOTICES_MISSING: DOTNET-RUNTIME-LICENSE.TXT') { $rejected = $true } else { throw }
    }
    if (-not $rejected) { throw 'Missing .NET notice control unexpectedly passed.' }
    Write-Output 'PASS: matching .NET runtime notices accepted; wrong runtime, wrong notice and missing notice rejected'
} finally {
    foreach ($file in @($wrongCore, $noticeFile)) {
        if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $false) }
}
