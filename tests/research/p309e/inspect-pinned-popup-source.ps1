[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CefSharpSource,
    [Parameter(Mandatory = $true)][string] $CefSource
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-audit-lib.ps1')

$previousNoLazyFetch = [Environment]::GetEnvironmentVariable('GIT_NO_LAZY_FETCH', 'Process')
try {
    # Partial clones must fail closed if a blob is absent; inspection is
    # offline and must never fetch an object as a side effect.
    $env:GIT_NO_LAZY_FETCH = '1'
    $sharp = Get-AuditRepository $CefSharpSource $script:CefSharpRevision
    $cef = Get-AuditRepository $CefSource $script:CefRevision
    # Fixed paths and Git commit objects are intentional. Local edits to the
    # worktree are not read, and this audit never executes downloaded content.
    $source = @{
        cef_header = Read-AuditGitBlob $cef $script:CefRevision 'include/cef_life_span_handler.h'
        cef_manager = Read-AuditGitBlob $cef $script:CefRevision 'libcef/browser/browser_info_manager.cc'
        cef_host = Read-AuditGitBlob $cef $script:CefRevision 'libcef/browser/browser_host_base.cc'
        managed_handler = Read-AuditGitBlob $sharp $script:CefSharpRevision 'CefSharp/Handler/ILifeSpanHandler.cs'
        adapter_header = Read-AuditGitBlob $sharp $script:CefSharpRevision 'CefSharp.Core.Runtime/Internals/ClientAdapter.h'
        adapter_cpp = Read-AuditGitBlob $sharp $script:CefSharpRevision 'CefSharp.Core.Runtime/Internals/ClientAdapter.cpp'
        frame_wrapper = Read-AuditGitBlob $sharp $script:CefSharpRevision 'CefSharp.Core.Runtime/Internals/CefFrameWrapper.cpp'
        winforms_control = Read-AuditGitBlob $sharp $script:CefSharpRevision 'CefSharp.WinForms/ChromiumWebBrowser.cs'
    }
    $landmarks = Get-PopupSourceLandmarks $source
    foreach ($key in $landmarks.Keys) { Write-Output "SOURCE_LANDMARK_MATCH: $key" }
    Write-Output "PINNED_SOURCE_LANDMARKS_MATCHED: $($landmarks.Count)"
    Write-Output 'NATIVE_EXECUTION: NOT_RUN'
    Write-Output 'OWNER_RELEASE_CONTRACT: UNSATISFIED'
    Write-Output 'Source text matching is not evidence of runtime behavior, ABI compatibility, binary provenance, or safe release.'
    $resultCode = 0
}
catch {
    [Console]::Error.WriteLine('SOURCE_AUDIT_INCOMPLETE: ' + $_.Exception.Message)
    $resultCode = 1
}
finally {
    [Environment]::SetEnvironmentVariable('GIT_NO_LAZY_FETCH', $previousNoLazyFetch, 'Process')
}
exit $resultCode
