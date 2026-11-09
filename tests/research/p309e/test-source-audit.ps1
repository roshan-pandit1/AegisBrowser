$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-audit-lib.ps1')
$checks = 0

function Check([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
}
function Reject([scriptblock] $Action, [string] $Expected) {
    try { & $Action | Out-Null; return $false }
    catch { return $_.Exception.Message -like "*$Expected*" }
}

try {
    # Small synthetic text fragments deliberately exercise only the audit's
    # failure paths. They are not a source checkout or native event evidence.
    $fixture = @{
        cef_header = 'OnBeforePopup(CefRefPtr<CefBrowser> browser, int popup_id) {} OnBeforePopupAborted(CefRefPtr<CefBrowser> browser, int popup_id) {} OnAfterCreated(CefRefPtr<CefBrowser> browser) {}'
        cef_manager = 'pending_popup->aborted_callback = base::BindOnce([](int browser_id,int popup_id) { GetBrowserForBrowserId(browser_id); GetLifeSpanHandler(); OnBeforePopupAborted(browser.get(), popup_id); }, browser->GetIdentifier(), popup_id); client = pending_popup->client;'
        cef_host = 'void CefBrowserHostBase::OnAfterCreated() { auto h = client_->GetLifeSpanHandler(); handler->OnAfterCreated(this); }'
        managed_handler = 'bool OnBeforePopup(IWebBrowser b); void OnAfterCreated(IBrowser b);'
        adapter_header = 'bool OnBeforePopup(CefRefPtr<CefBrowser> browser, int popup_id);'
        adapter_cpp = 'handler->OnBeforePopup(_browserControl, browserWrapper); _popupBrowsers->Add(browser->GetIdentifier(), browserWrapper);'
        frame_wrapper = 'auto client = static_cast<ClientAdapter*>(host->GetClient().get());'
        winforms_control = 'LifeSpanHandler = null;'
    }
    $result = Get-PopupSourceLandmarks $fixture
    Check ($result.Count -eq 10) 'synthetic fixture exercises all landmark predicates'
    Assert-AuditRevision $script:CefRevision $script:CefRevision
    Check $true 'identical revision accepted'
    Check (Reject { Assert-AuditRevision ('0' * 40) $script:CefRevision } 'SOURCE_REVISION_MISMATCH') 'wrong revision rejected'
    Check (Reject { Get-AuditRepository 'relative-source-path' $script:CefRevision } 'SOURCE_PATH_MUST_BE_ABSOLUTE') 'relative path rejected'
    $projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    Check (Reject { Get-AuditRepository $projectRoot $script:CefRevision } 'SOURCE_REVISION_MISMATCH') 'wrong source checkout rejected'

    $cases = @(
        @('cef_header','popup_id','missing_id','native_approval_and_abort_ids'),
        @('cef_header','OnAfterCreated','DifferentCallback','native_creation_without_attempt_id'),
        @('managed_handler','OnBeforePopup','DifferentCallback','managed_abort_and_id_not_exposed'),
        @('adapter_header','popup_id','missing_id','adapter_receives_id_but_no_abort_override'),
        @('adapter_cpp','browserWrapper);','popup_id);','managed_approval_call_does_not_forward_id'),
        @('adapter_cpp','_popupBrowsers->Add','_popupBrowsers->Remove','created_popup_keyed_by_browser_id'),
        @('frame_wrapper','static_cast<ClientAdapter*>','different_cast','frame_wrapper_downcasts_client'),
        @('winforms_control','LifeSpanHandler = null','LifeSpanHandler = other','managed_handler_can_be_cleared'),
        @('cef_manager','GetBrowserForBrowserId','DifferentLookup','native_abort_requires_opener_relookup'),
        @('cef_host','OnAfterCreated(this)','Other(this)','native_success_uses_returned_client')
    )
    foreach ($case in $cases) {
        $changed = @{}; foreach ($key in $fixture.Keys) { $changed[$key] = $fixture[$key] }
        $changed[$case[0]] = $changed[$case[0]].Replace($case[1], $case[2])
        Check (Reject { Get-PopupSourceLandmarks $changed } "SOURCE_LANDMARK_MISMATCH:$($case[3])") "landmark change rejected: $($case[3])"
    }
    $missing = @{}; foreach ($key in $fixture.Keys) { if ($key -ne 'cef_host') { $missing[$key] = $fixture[$key] } }
    Check (Reject { Get-PopupSourceLandmarks $missing } 'SOURCE_MISSING:cef_host') 'missing source rejected'
    $managedAbort = @{}; foreach ($key in $fixture.Keys) { $managedAbort[$key] = $fixture[$key] }
    $managedAbort.managed_handler += ' void OnBeforePopupAborted();'
    Check (Reject { Get-PopupSourceLandmarks $managedAbort } 'managed_abort_and_id_not_exposed') 'new managed abort surface requires review'
    $nativeAbort = @{}; foreach ($key in $fixture.Keys) { $nativeAbort[$key] = $fixture[$key] }
    $nativeAbort.adapter_header += ' void OnBeforePopupAborted();'
    Check (Reject { Get-PopupSourceLandmarks $nativeAbort } 'adapter_receives_id_but_no_abort_override') 'new native override requires review'

    Write-Output "PASS: $checks synthetic source-audit checks; fixtures are not native evidence"
    exit 0
}
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
