// Isolated research only. This DLL is loaded by the matching CEF bootstrapc.
// It never supplies a no-sandbox fallback and does not touch browser profiles.
#include <windows.h>
#include <atomic>
#include <climits>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <utility>
#include "FixtureGate.h"

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_command_line.h"
#include "include/cef_frame.h"
#include "include/cef_process_message.h"
#include "include/cef_request_handler.h"
#include "include/cef_render_process_handler.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_task.h"
#include "include/cef_version_info.h"
#include "include/cef_version.h"

#if !defined(_M_X64)
#error The research harness requires an x64 target.
#endif
#if CEF_VERSION_MAJOR != 154 || CEF_VERSION_MINOR != 0 || CEF_VERSION_PATCH != 34 || CHROME_VERSION_BUILD != 8037 || CHROME_VERSION_PATCH != 98
#error The research harness requires the exact pinned CEF and Chromium headers.
#endif

namespace {
struct State {
  bool created = false;
  bool load_started = false;
  bool loaded = false;
  bool close_started = false;
  bool closed = false;
  bool failed = false;
  bool lifetime_fault = false;
  std::string fixture_frame_id;
};

bool IsFixtureFrame(CefRefPtr<CefFrame> frame) {
  return p309g::IsFixtureUrl(frame->GetURL().ToString());
}

// Runs on the CEF browser UI thread. It never waits for the observer: an
// expired observation window permits normal close but cannot imply evidence.
class FinishObservation final : public CefTask {
 public:
  FinishObservation(State* state, CefRefPtr<CefBrowser> browser)
      : state_(state), browser_(browser) {}
  void Execute() override {
    if (state_->close_started || state_->closed) return;
    state_->close_started = true;
    std::puts("EVENT: CLOSE_REQUESTED");
    browser_->GetHost()->CloseBrowser(false);
  }
 private:
  State* const state_;
  CefRefPtr<CefBrowser> browser_;
  IMPLEMENT_REFCOUNTING(FinishObservation);
};

class Client final : public CefClient, public CefLifeSpanHandler, public CefLoadHandler,
                     public CefRequestHandler {
 public:
  explicit Client(State* state, bool observe) : state_(state), observe_(observe) {}
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
  bool OnProcessMessageReceived(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                                CefProcessId source, CefRefPtr<CefProcessMessage> message) override {
    // Renderer messages are debugging claims only, can be lost and are not
    // evidence of process identity, pipe ownership or sandbox enforcement.
    if (source != PID_RENDERER || !frame || !frame->IsMain() || !message) return false;
    const std::string name = message->GetName().ToString();
    const char* stage = name == "P309I_PID" ? "PID" :
                        name == "P309I_CONTEXT" ? "CONTEXT" :
                        name == "P309I_PIPE_MISSING" ? "PIPE_MISSING" :
                        name == "P309I_PIPE_OPEN_FAILED" ? "PIPE_OPEN_FAILED" :
                        name == "P309I_PIPE_MODE_FAILED" ? "PIPE_MODE_FAILED" :
                        name == "P309I_PIPE_WRITE_FAILED" ? "PIPE_WRITE_FAILED" :
                        name == "P309I_PIPE_SENT" ? "PIPE_SENT" : nullptr;
    if (!stage || ipc_diagnostics_ >= 32) return false;
    CefRefPtr<CefListValue> args = message->GetArgumentList();
    if (!args || args->GetSize() != 1 || args->GetType(0) != VTYPE_INT) return false;
    if (name == "P309I_PID") {
      const int pid = args->GetInt(0);
      if (pid <= 0) return false;
      ++ipc_diagnostics_;
      // This is an unauthenticated renderer-side PID claim, not a process
      // handle, lineage verification or an authorization to inspect a PID.
      std::printf("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=%d\n", pid);
      return true;
    }
    ++ipc_diagnostics_;
    std::printf("NATIVE_DIAGNOSTIC: RENDERER_IPC stage=%s code=%d\n", stage, args->GetInt(0));
    return true;
  }
  // This baseline uses a single static fixture with no popup behavior. Reject
  // any unexpected popup and record the lifetime as uncertain; this policy
  // belongs only to this standalone test, not to AegisBrowser.
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int,
                     const CefString&, const CefString&, CefLifeSpanHandler::WindowOpenDisposition,
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
  void OnLoadStart(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                   TransitionType) override {
    if (!frame->IsMain() || browser->IsPopup() || state_->close_started) return;
    if (!IsFixtureFrame(frame)) {
      std::puts("EVENT: OTHER_MAIN_FRAME_LOAD_START");
      return;
    }
    const auto identifier = frame->GetIdentifier().ToString();
    if (identifier.empty()) {
      state_->failed = true;
      state_->close_started = true;
      std::puts("EVENT: FIXTURE_FRAME_ID_MISSING");
      browser->GetHost()->CloseBrowser(false);
      return;
    }
    if (!state_->load_started) {
      state_->load_started = true;
      state_->fixture_frame_id = identifier;
      std::puts("EVENT: MAIN_FRAME_LOAD_STARTED");
    } else if (state_->fixture_frame_id != identifier) {
      state_->failed = true;
      state_->close_started = true;
      std::puts("EVENT: FIXTURE_FRAME_ID_CHANGED");
      browser->GetHost()->CloseBrowser(false);
    }
  }
  void OnLoadEnd(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame, int) override {
    if (!frame->IsMain() || browser->IsPopup() || state_->close_started) return;
    if (!IsFixtureFrame(frame)) {
      std::puts("EVENT: OTHER_MAIN_FRAME_LOAD_END");
      return;
    }
    if (!state_->load_started || frame->GetIdentifier().ToString() != state_->fixture_frame_id) {
      state_->failed = true;
      state_->close_started = true;
      std::puts("EVENT: FIXTURE_LOAD_START_OR_FRAME_MISMATCH");
      browser->GetHost()->CloseBrowser(false);
      return;
    }
    state_->loaded = true;
    std::puts("EVENT: MAIN_FRAME_LOAD_END");
    // Keep a live page long enough for the independent observer to run. A
    // delayed task does not block CEF's UI thread; scheduling failure is a
    // failed observation, never native-closure evidence.
    if (observe_ && CefPostDelayedTask(TID_UI, new FinishObservation(state_, browser), 3000)) {
      std::puts("NATIVE_DIAGNOSTIC: OBSERVATION_WINDOW_STARTED");
    } else {
      if (observe_) { state_->failed = true; std::puts("NATIVE_DIAGNOSTIC: OBSERVATION_WINDOW_SCHEDULING_FAILED"); }
      state_->close_started = true;
      std::puts("EVENT: CLOSE_REQUESTED");
      browser->GetHost()->CloseBrowser(false);
    }
  }
  void OnLoadError(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                   ErrorCode error, const CefString&, const CefString& failed_url) override {
    if (!frame->IsMain() || browser->IsPopup() || state_->close_started) return;
    // CEF can report a cancelled navigation while another load is pending.
    // A cancellation alone is neither fixture success nor native closure.
    if (error == ERR_ABORTED) {
      if (p309g::IsFixtureAbort(true, failed_url.ToString())) {
        state_->failed = true;
        state_->close_started = true;
        std::puts("EVENT: FIXTURE_NAVIGATION_ABORTED");
        std::puts("EVENT: CLOSE_REQUESTED_AFTER_LOAD_ERROR");
        browser->GetHost()->CloseBrowser(false);
        return;
      }
      std::puts("EVENT: MAIN_FRAME_NAVIGATION_ABORTED");
      return;
    }
    state_->failed = true;
    state_->close_started = true;
    std::printf("EVENT: MAIN_FRAME_LOAD_ERROR code=%d\n", static_cast<int>(error));
    std::puts("EVENT: CLOSE_REQUESTED_AFTER_LOAD_ERROR");
    browser->GetHost()->CloseBrowser(false);
  }
  void OnRenderProcessTerminated(CefRefPtr<CefBrowser> browser,
                                 TerminationStatus status, int error_code,
                                 const CefString&) override {
    // This callback is an observation, not proof of the process role, token,
    // or cause of termination. Do not log CEF's free-form error string.
    std::printf("EVENT: RENDERER_TERMINATED status=%d code=%d\n",
                static_cast<int>(status), error_code);
    state_->failed = true;
    if (!state_->close_started && !state_->closed) {
      state_->close_started = true;
      std::puts("EVENT: CLOSE_REQUESTED_AFTER_LOAD_ERROR");
      browser->GetHost()->CloseBrowser(false);
    }
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
  const bool observe_;
  unsigned int ipc_diagnostics_ = 0;  // browser UI thread only
  IMPLEMENT_REFCOUNTING(Client);
};

class App final : public CefApp, public CefBrowserProcessHandler, public CefRenderProcessHandler {
 public:
  App(State* state, std::string pipe) : state_(state), pipe_(std::move(pipe)) {}
  ~App() override { if (pipe_handle_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_handle_); }
  CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
  CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override { return this; }
  void OnContextCreated(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                        CefRefPtr<CefV8Context>) override {
    // CEF documents this callback on the renderer main thread after V8
    // context creation. It does not prove that sandbox lockdown is complete.
    if (!frame || !frame->IsMain() || attempted_) return;
    attempted_ = true;
    auto diagnostic = [frame](const char* name, DWORD code) {
      CefRefPtr<CefProcessMessage> message = CefProcessMessage::Create(name);
      if (!message) return;
      CefRefPtr<CefListValue> args = message->GetArgumentList();
      if (!args || !args->SetInt(0, static_cast<int>(code))) return;
      frame->SendProcessMessage(PID_BROWSER, message);
    };
    diagnostic("P309I_CONTEXT", 0);
    const DWORD pid = GetCurrentProcessId();
    if (pid > 0 && pid <= INT_MAX) diagnostic("P309I_PID", pid);
    if (!p309g::IsObservationPipe(pipe_)) { diagnostic("P309I_PIPE_MISSING", 0); return; }
    HANDLE pipe = CreateFileA(pipe_.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) { diagnostic("P309I_PIPE_OPEN_FAILED", GetLastError()); return; }
    DWORD mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
    if (!SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr)) {
      DWORD error = GetLastError(); CloseHandle(pipe); diagnostic("P309I_PIPE_MODE_FAILED", error); return;
    }
    constexpr char marker[] = "P309I_RENDERER_V1\n";
    DWORD written = 0;
    const BOOL ok = WriteFile(pipe, marker, sizeof(marker) - 1, &written, nullptr);
    if (!ok || written != sizeof(marker) - 1) {
      DWORD error = ok ? ERROR_WRITE_FAULT : GetLastError(); CloseHandle(pipe); diagnostic("P309I_PIPE_WRITE_FAILED", error); return;
    }
    // The observer obtains a kernel-supplied client PID and retains its own
    // process handle. The marker/pipe name alone never authenticates a role.
    pipe_handle_ = pipe;
    diagnostic("P309I_PIPE_SENT", 0);
  }
  void OnBeforeChildProcessLaunch(CefRefPtr<CefCommandLine> command_line) override {
    // The initial browser passed the explicit environment opt-in before CEF
    // was initialized. Propagate test intent if Chromium filters child
    // environments; the marker is spoofable and is never security evidence.
    // Every child still passes the sandbox-info and forbidden-switch gates.
    command_line->AppendSwitch("aegis-p309f-child-opt-in");
    if (p309g::IsObservationPipe(pipe_) && command_line->GetSwitchValue("type").ToString() == "renderer")
      command_line->AppendSwitchWithValue("aegis-p309i-pipe", pipe_);
    // CEF may invoke this on UI or IO threads. Bound output without retaining
    // the command line or emitting its potentially sensitive arguments. This
    // records a launch request, not a process identity or sandbox proof.
    if (launch_diagnostics_.fetch_add(1, std::memory_order_relaxed) >= 32) return;
    const std::string type = command_line->GetSwitchValue("type").ToString();
    const char* role = type == "renderer" ? "renderer" :
                       type == "gpu-process" ? "gpu" :
                       type == "utility" ? "utility" : "other";
    std::printf("NATIVE_DIAGNOSTIC: CHILD_LAUNCH role=%s no_sandbox=%d disable_gpu_sandbox=%d\n",
                role, command_line->HasSwitch("no-sandbox") ? 1 : 0,
                command_line->HasSwitch("disable-gpu-sandbox") ? 1 : 0);
  }
  void OnContextInitialized() override {
    std::puts("EVENT: CONTEXT_INITIALIZED");
    CefWindowInfo window;
    window.SetAsPopup(nullptr, L"AegisBrowser P3-09F research");
    window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    CefBrowserSettings settings;
    CefRefPtr<Client> client = new Client(state_, p309g::IsObservationPipe(pipe_));
    if (!CefBrowserHost::CreateBrowser(window, client, p309g::kFixtureUrl, settings, nullptr, nullptr)) {
      state_->failed = true;
      std::puts("EVENT: CREATE_BROWSER_FAILED");
      CefQuitMessageLoop();
    }
  }
 private:
  State* const state_;
  const std::string pipe_;
  bool attempted_ = false;  // renderer-main-thread only
  HANDLE pipe_handle_ = INVALID_HANDLE_VALUE;
  std::atomic<unsigned int> launch_diagnostics_{0};
  IMPLEMENT_REFCOUNTING(App);
};

bool ExplicitOptIn() {
  wchar_t value[8] = {};
  return GetEnvironmentVariableW(L"AEGIS_P309F_EXECUTE_SANDBOX_TEST", value, 8) == 3 &&
         std::wcscmp(value, L"YES") == 0;
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
  const bool opted_in = ExplicitOptIn() || p309g::HasChildOptIn(argc, argv);
  switch (p309g::EvaluateGate(opted_in, sandbox_info != nullptr, argc, argv)) {
    case p309g::GateFailure::OptIn:
      std::fprintf(stderr, "BLOCKED: OPT_IN role=%s\n", p309g::ProcessRole(argc, argv)); return 10;
    case p309g::GateFailure::MissingSandboxInfo:
      std::fprintf(stderr, "BLOCKED: MISSING_SANDBOX_INFO role=%s\n", p309g::ProcessRole(argc, argv)); return 16;
    case p309g::GateFailure::NoSandbox:
      std::fprintf(stderr, "BLOCKED: DISALLOWED_SANDBOX_SWITCH role=%s switch=no-sandbox\n", p309g::ProcessRole(argc, argv)); return 17;
    case p309g::GateFailure::DisableGpuSandbox:
      std::fprintf(stderr, "BLOCKED: DISALLOWED_SANDBOX_SWITCH role=%s switch=disable-gpu-sandbox\n", p309g::ProcessRole(argc, argv)); return 17;
    case p309g::GateFailure::None: break;
  }
  if (!bootstrap_version || bootstrap_version->size <
          offsetof(cef_version_info_t, chrome_version_patch) + sizeof(bootstrap_version->chrome_version_patch) ||
      bootstrap_version->cef_version_major != 154 ||
      bootstrap_version->cef_version_minor != 0 || bootstrap_version->cef_version_patch != 34 ||
      bootstrap_version->chrome_version_build != 8037 || bootstrap_version->chrome_version_patch != 98) {
    std::fprintf(stderr, "BLOCKED: BOOTSTRAP_VERSION role=%s\n", p309g::ProcessRole(argc, argv));
    return 11;
  }
  if (cef_version_info(0) != 154 || cef_version_info(1) != 0 || cef_version_info(2) != 34 ||
      cef_version_info(6) != 8037 || cef_version_info(7) != 98) {
    std::fprintf(stderr, "BLOCKED: CEF_RUNTIME_VERSION role=%s\n", p309g::ProcessRole(argc, argv));
    return 12;
  }
  std::string pipe = p309g::ObservationPipeArgument(argc, argv);
  if (std::strcmp(p309g::ProcessRole(argc, argv), "browser") == 0) {
    char value[128] = {};
    DWORD length = GetEnvironmentVariableA("AEGIS_P309I_PIPE", value, sizeof(value));
    pipe = length > 0 && length < sizeof(value) && p309g::IsObservationPipe(value) ? value : "";
  }
  State state;
  CefRefPtr<App> app = new App(&state, pipe);
  CefMainArgs main_args(GetModuleHandleW(nullptr));
  const int subprocess = CefExecuteProcess(main_args, app, sandbox_info);
  if (subprocess >= 0) return subprocess;
  std::wstring cache;
  if (!CreateTemporaryCache(cache)) { std::fputs("BLOCKED: TEMP_CACHE\n", stderr); return 13; }
  CefSettings settings;
  settings.no_sandbox = false;
  settings.log_severity = LOGSEVERITY_DISABLE;
  CefString(&settings.root_cache_path) = cache;
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
