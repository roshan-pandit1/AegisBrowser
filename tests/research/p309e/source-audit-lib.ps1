Set-StrictMode -Version Latest

# These are exact source revisions, not claims about the provenance of an
# installed binary. Read Git objects at these revisions; do not trust mutable
# working-tree files or silently accept a newer checkout.
$script:CefSharpRevision = 'ec1027aa21e1af1217681a349104c15d29bbc21a'
$script:CefRevision = '14c5a089e8452874cb1a556dc3fde26ed2d87723'
$script:MaximumSourceBytes = 1048576

function Assert-AuditRevision {
    param([string] $Actual, [string] $Expected)
    if ($Actual -cne $Expected) { throw 'SOURCE_REVISION_MISMATCH' }
}

function Get-AuditRepository {
    param([string] $Path, [string] $ExpectedRevision)
    if (-not [IO.Path]::IsPathRooted($Path)) { throw 'SOURCE_PATH_MUST_BE_ABSOLUTE' }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $item = Get-Item -LiteralPath $root -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) { throw 'SOURCE_PATH_NOT_DIRECTORY' }
    # Defense in depth only: this does not protect against a hostile local
    # administrator or concurrent replacement of a checked path.
    $current = $item
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SOURCE_REPARSE_POINT' }
        $current = $current.Parent
    }
    $inside = @(& git --no-replace-objects -C $root rev-parse --is-inside-work-tree 2>&1)
    if ($LASTEXITCODE -ne 0 -or $inside.Count -ne 1 -or $inside[0] -cne 'true') { throw 'SOURCE_NOT_GIT_WORKTREE' }
    $top = @(& git --no-replace-objects -C $root rev-parse --show-toplevel 2>&1)
    if ($LASTEXITCODE -ne 0 -or $top.Count -ne 1) { throw 'SOURCE_GIT_ROOT_UNAVAILABLE' }
    $actualRoot = [IO.Path]::GetFullPath([string]$top[0]).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($root, $actualRoot)) { throw 'SOURCE_ROOT_MISMATCH' }
    $revision = @(& git --no-replace-objects -C $root rev-parse --verify 'HEAD^{commit}' 2>&1)
    if ($LASTEXITCODE -ne 0 -or $revision.Count -ne 1) { throw 'SOURCE_HEAD_UNAVAILABLE' }
    Assert-AuditRevision ([string]$revision[0]) $ExpectedRevision
    return $root
}

function Read-AuditGitBlob {
    param([string] $Root, [string] $Revision, [string] $RelativePath)
    # RelativePath is supplied only by the fixed manifest below, never by a
    # caller. Reject non-regular tree entries and bound the blob before read.
    $entry = @(& git --no-replace-objects -C $Root ls-tree $Revision -- $RelativePath 2>&1)
    if ($LASTEXITCODE -ne 0 -or $entry.Count -ne 1 -or [string]$entry[0] -notmatch '^100(?:644|755) blob [0-9a-f]{40}\t') { throw 'SOURCE_BLOB_NOT_REGULAR' }
    $objectName = '{0}:{1}' -f $Revision, $RelativePath
    $sizeText = @(& git --no-replace-objects -C $Root cat-file -s $objectName 2>&1)
    $size = 0L
    if ($LASTEXITCODE -ne 0 -or $sizeText.Count -ne 1 -or -not [long]::TryParse([string]$sizeText[0], [ref]$size) -or $size -lt 1 -or $size -gt $script:MaximumSourceBytes) { throw 'SOURCE_BLOB_SIZE_INVALID' }
    $lines = @(& git --no-replace-objects -C $Root cat-file blob $objectName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'SOURCE_BLOB_UNREADABLE' }
    return ($lines -join "`n")
}

function Get-PopupSourceLandmarks {
    param([System.Collections.IDictionary] $Source)
    foreach ($key in @('cef_header','cef_manager','cef_host','managed_handler','adapter_header','adapter_cpp','frame_wrapper','winforms_control')) {
        if (-not $Source.Contains($key) -or $Source[$key] -isnot [string] -or $Source[$key].Length -eq 0) { throw "SOURCE_MISSING:$key" }
    }
    $approval = [regex]::Match($Source.adapter_cpp, '(?s)handler->OnBeforePopup\((.*?)\);')
    $abortRoute = [regex]::Match($Source.cef_manager, '(?s)pending_popup->aborted_callback\s*=\s*base::BindOnce\((.*?)browser->GetIdentifier\(\),\s*popup_id\);')
    $checks = [ordered]@{
        native_approval_and_abort_ids = ($Source.cef_header -match '(?s)OnBeforePopup\([^;{]*\bpopup_id\b' -and $Source.cef_header -match '(?s)OnBeforePopupAborted\([^;{]*\bpopup_id\b')
        native_creation_without_attempt_id = ($Source.cef_header -match 'OnAfterCreated\(CefRefPtr<CefBrowser>\s+browser\)')
        managed_abort_and_id_not_exposed = ($Source.managed_handler -match 'OnBeforePopup\(' -and $Source.managed_handler -notmatch '\bOnBeforePopupAborted\s*\(' -and $Source.managed_handler -notmatch '\bpopup_id\b')
        adapter_receives_id_but_no_abort_override = ($Source.adapter_header -match '(?s)OnBeforePopup\([^;{]*\bpopup_id\b' -and $Source.adapter_header -notmatch '\bOnBeforePopupAborted\s*\(')
        managed_approval_call_does_not_forward_id = ($approval.Success -and $approval.Groups[1].Value -notmatch '\bpopup_id\b')
        created_popup_keyed_by_browser_id = ($Source.adapter_cpp -match '_popupBrowsers->Add\(browser->GetIdentifier\(\)')
        frame_wrapper_downcasts_client = ($Source.frame_wrapper -match 'static_cast<ClientAdapter\*>\(host->GetClient\(\)\.get\(\)\)')
        managed_handler_can_be_cleared = ($Source.winforms_control -match 'LifeSpanHandler\s*=\s*null\s*;')
        native_abort_requires_opener_relookup = ($abortRoute.Success -and $abortRoute.Groups[1].Value -match 'GetBrowserForBrowserId\(browser_id\)' -and $abortRoute.Groups[1].Value -match 'GetLifeSpanHandler\(\)' -and $abortRoute.Groups[1].Value -match 'OnBeforePopupAborted\(browser\.get\(\),\s*popup_id\)')
        native_success_uses_returned_client = ($Source.cef_manager -match 'client\s*=\s*pending_popup->client\s*;' -and $Source.cef_host -match '(?s)CefBrowserHostBase::OnAfterCreated\(\).*?client_->GetLifeSpanHandler\(\).*?handler->OnAfterCreated\(this\)')
    }
    foreach ($key in $checks.Keys) { if (-not $checks[$key]) { throw "SOURCE_LANDMARK_MISMATCH:$key" } }
    return $checks
}
