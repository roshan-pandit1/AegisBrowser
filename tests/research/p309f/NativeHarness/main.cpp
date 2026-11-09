// Isolated research only. This DLL is loaded by the matching CEF bootstrapc.
// It never supplies a no-sandbox fallback and does not touch browser profiles.
#include <windows.h>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_frame.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_version_info.h"
#include "include/cef_version.h"

#if !defined(_M_X64)
#error The research harness requires an x64 target.
#endif
#if CEF_VERSION_MAJOR != 154 || CEF_VERSION_MINOR != 0 || CEF_VERSION_PATCH != 34 || CHROME_VERSION_BUILD != 8037 || CHROME_VERSION_PATCH != 98
#error The research harness requires the exact pinned CEF and Chromium headers.
#endif

namespace {
constexpr char kFixtureUrl[] =
    "data:text/html,%3Ctitle%3EAegis%20CEF%20research%3C/title%3E%3Cp%3EOffline%20fixture%3C/p%3E";
struct State {
  bool created = false;
  bool loaded = false;
  bool close_started = false;
  bool closed = false;
  bool failed = false;
  bool lifetime_fault = false;
};

class Client final : public CefClient, public CefLifeSpanHandler, public CefLoadHandler {
 public:
  explicit Client(State* state) : state_(state) {}
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  // This baseline uses a single static fixture with no popup behavior. Reject
  // any unexpected popup and record the lifetime as uncertain; this policy
  // belongs only to this standalone test, not to AegisBrowser.
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int,
                     const CefString&, const CefString&, WindowOpenDisposition,
                     bool, const CefPopupFeatures&, CefWindowInfo&,
                     CefRefPtr<CefClient>&, CefBrowserSettings&,
                     CefRefPtr<CefDictionaryValue>&, bool*) override {
    state_->failed = true;
    state_->lifetime_fault = true;
    std::puts("EVENT: UNEXPECTED_POPUP_REJECTED");
    return true;
  }
  void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
    if (state_->created || browser->IsPopup()) {
      state_->failed = true;
      state_->lifetime_fault = true;
      return;
    }
    state_->created = true;
    std::puts("EVENT: BROWSER_CREATED");
  }
  void OnLoadEnd(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame, int) override {
    if (!frame->IsMain() || browser->IsPopup() || state_->close_started) return;
    if (frame->GetURL().ToString() != kFixtureUrl) {
      std::puts("EVENT: OTHER_MAIN_FRAME_LOAD_END");
      return;
    }
    state_->loaded = true;
    state_->close_started = true;
    std::puts("EVENT: MAIN_FRAME_LOAD_END");
    std::puts("EVENT: CLOSE_REQUESTED");
    browser->GetHost()->CloseBrowser(false);
  }
  void OnLoadError(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                   ErrorCode error, const CefString&, const CefString&) override {
    if (!frame->IsMain() || browser->IsPopup() || state_->close_started) return;
    // CEF can report a cancelled navigation while another load is pending.
    // A cancellation alone is neither fixture success nor native closure.
    if (error == ERR_ABORTED) {
      std::puts("EVENT: MAIN_FRAME_NAVIGATION_ABORTED");
      return;
    }
    state_->failed = true;
    state_->close_started = true;
    std::printf("EVENT: MAIN_FRAME_LOAD_ERROR code=%d\n", static_cast<int>(error));
    std::puts("EVENT: CLOSE_REQUESTED_AFTER_LOAD_ERROR");
    browser->GetHost()->CloseBrowser(false);
  }
  bool DoClose(CefRefPtr<CefBrowser>) override {
    std::puts("EVENT: DO_CLOSE");
    return false;
  }
  void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
    if (browser->IsPopup() || state_->closed || !state_->created) {
      state_->failed = true;
      state_->lifetime_fault = true;
      return;
    }
    state_->closed = true;
    std::puts("EVENT: BEFORE_CLOSE");
    CefQuitMessageLoop();
  }
 private:
  State* const state_;
  IMPLEMENT_REFCOUNTING(Client);
};

class App final : public CefApp, public CefBrowserProcessHandler {
 public:
  explicit App(State* state) : state_(state) {}
  CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
  void OnContextInitialized() override {
    std::puts("EVENT: CONTEXT_INITIALIZED");
    CefWindowInfo window;
    window.SetAsPopup(nullptr, L"AegisBrowser P3-09F research");
    window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    CefBrowserSettings settings;
    CefRefPtr<Client> client = new Client(state_);
    if (!CefBrowserHost::CreateBrowser(window, client, kFixtureUrl, settings, nullptr, nullptr)) {
      state_->failed = true;
      std::puts("EVENT: CREATE_BROWSER_FAILED");
      CefQuitMessageLoop();
    }
  }
 private:
  State* const state_;
  IMPLEMENT_REFCOUNTING(App);
};

bool ExplicitOptIn() {
  wchar_t value[8] = {};
  return GetEnvironmentVariableW(L"AEGIS_P309F_EXECUTE_SANDBOX_TEST", value, 8) == 3 &&
         std::wcscmp(value, L"YES") == 0;
}

bool HasNoSandboxSwitch(int argc, char* argv[]) {
  for (int i = 0; i < argc; ++i) {
    if (_stricmp(argv[i], "--no-sandbox") == 0 ||
        _strnicmp(argv[i], "--no-sandbox=", 13) == 0 ||
        _stricmp(argv[i], "--disable-gpu-sandbox") == 0 ||
        _strnicmp(argv[i], "--disable-gpu-sandbox=", 22) == 0) return true;
  }
  return false;
}

bool CreateTemporaryCache(std::wstring& path) {
  wchar_t root[MAX_PATH] = {};
  DWORD size = GetTempPathW(MAX_PATH, root);
  if (size == 0 || size >= MAX_PATH) return false;
  path = std::wstring(root) + L"aegis-p309f-cef-" + std::to_wstring(GetCurrentProcessId()) +
         L"-" + std::to_wstring(GetTickCount64());
  return CreateDirectoryW(path.c_str(), nullptr) != 0;
}
}  // namespace

// bootstrapc calls this in every process, passing its sandbox_info object.
// Do not invoke directly or add a fallback for a missing sandbox object.
CEF_BOOTSTRAP_EXPORT int RunConsoleMain(int argc, char* argv[], void* sandbox_info,
                                        cef_version_info_t* bootstrap_version) {
  // A timeout must retain the observed event sequence even if the process is
  // still running. No buffered output is treated as a completion signal.
  std::setvbuf(stdout, nullptr, _IONBF, 0);
  std::setvbuf(stderr, nullptr, _IONBF, 0);
  if (!ExplicitOptIn()) {
    std::fputs("BLOCKED: OPT_IN\n", stderr);
    return 10;
  }
  if (!sandbox_info) {
    std::fputs("BLOCKED: MISSING_SANDBOX_INFO\n", stderr);
    return 10;
  }
  if (HasNoSandboxSwitch(argc, argv)) {
    std::fputs("BLOCKED: DISALLOWED_SANDBOX_SWITCH\n", stderr);
    return 10;
  }
  if (!bootstrap_version || bootstrap_version->size <
          offsetof(cef_version_info_t, chrome_version_patch) + sizeof(bootstrap_version->chrome_version_patch) ||
      bootstrap_version->cef_version_major != 154 ||
      bootstrap_version->cef_version_minor != 0 || bootstrap_version->cef_version_patch != 34 ||
      bootstrap_version->chrome_version_build != 8037 || bootstrap_version->chrome_version_patch != 98) {
    std::fputs("BLOCKED: BOOTSTRAP_VERSION\n", stderr);
    return 11;
  }
  if (cef_version_info(0) != 154 || cef_version_info(1) != 0 || cef_version_info(2) != 34 ||
      cef_version_info(6) != 8037 || cef_version_info(7) != 98) {
    std::fputs("BLOCKED: CEF_RUNTIME_VERSION\n", stderr);
    return 12;
  }
  CefMainArgs main_args(GetModuleHandleW(nullptr));
  const int subprocess = CefExecuteProcess(main_args, nullptr, sandbox_info);
  if (subprocess >= 0) return subprocess;
  std::wstring cache;
  if (!CreateTemporaryCache(cache)) { std::fputs("BLOCKED: TEMP_CACHE\n", stderr); return 13; }
  CefSettings settings;
  settings.no_sandbox = false;
  settings.log_severity = LOGSEVERITY_DISABLE;
  CefString(&settings.root_cache_path) = cache;
  State state;
  CefRefPtr<App> app = new App(&state);
  if (!CefInitialize(main_args, settings, app, sandbox_info)) {
    std::fputs("CEF_STARTUP: FAIL\n", stderr);
    return 14;
  }
  std::puts("EVENT: CEF_INITIALIZE_SUCCEEDED");
  std::puts("SANDBOX_CONFIGURATION: REQUESTED; ENFORCEMENT: INCONCLUSIVE");
  CefRunMessageLoop();
  app = nullptr;
  // A load failure is not itself a browser-lifetime failure. Once the only
  // observed browser has actually reached OnBeforeClose, CEF shutdown can be
  // attempted; unexpected/duplicate browser callbacks make that uncertain.
  if (!state.created || !state.closed || state.lifetime_fault) {
    std::fputs("CEF_SHUTDOWN: INCOMPLETE\n", stderr);
    return 15;
  }
  CefShutdown();
  std::puts("EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE");
  if (!state.loaded || state.failed) {
    std::fputs("FIXTURE_RESULT: FAILED\n", stderr);
    return 15;
  }
  std::puts("OWNER_RELEASE_CONTRACT: UNSATISFIED");
  return 0;
}
