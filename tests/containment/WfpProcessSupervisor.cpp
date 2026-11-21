// Experimental disposable-VM-only supervisor. Never used by production.
// A persistent test block is intentionally left in place on uncertain child
// termination; this does not establish BFE-restart or crash-safe containment.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <fwpmu.h>
#include <userenv.h>
#include <rpc.h>
#include <sddl.h>
#include <bcrypt.h>
#include <atomic>
#include <climits>
#include <cstring>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <memory>
#include <string>
#include <vector>
#include "WfpTestProcess.hpp"
#include "WfpSupervisorPolicy.hpp"
#include "WfpSupervisorGate.hpp"

#pragma comment(lib, "fwpuclnt.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "rpcrt4.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "advapi32.lib")

// Reuse the separately tested, bounded observer without exposing its CLI.
namespace AegisTestObserver {
#define wmain ObserverUnusedMain
#include "WfpDecisionObserver.cpp"
#undef wmain
}

static GUID NewGuid()
{
    GUID value{}; const auto status = UuidCreate(&value);
    if (status != RPC_S_OK && status != RPC_S_UUID_LOCAL_ONLY) throw std::runtime_error("Cannot create test GUID.");
    return value;
}

static std::wstring GuidText(const GUID& value)
{
    RPC_WSTR text = nullptr;
    if (UuidToStringW(const_cast<GUID*>(&value), &text) != RPC_S_OK) throw std::runtime_error("Cannot format test GUID.");
    std::wstring result(reinterpret_cast<wchar_t*>(text)); RpcStringFreeW(&text); return result;
}

static bool ParseGuid(const wchar_t* text, GUID& value)
{
    RPC_STATUS check = RPC_S_OK;
    return UuidFromStringW(reinterpret_cast<RPC_WSTR>(const_cast<wchar_t*>(text)), &value) == RPC_S_OK &&
        !UuidIsNil(&value, &check) && check == RPC_S_OK;
}

static bool ParseEndpoint(const wchar_t* text, const wchar_t* portText, UINT32& host, UINT16& port)
{
    IN_ADDR address{}; wchar_t canonical[INET_ADDRSTRLEN]{};
    if (InetPtonW(AF_INET, text, &address) != 1 || !InetNtopW(AF_INET, &address, canonical, ARRAYSIZE(canonical)) ||
        std::wstring(text) != canonical) return false;
    host = ntohl(address.S_un.S_addr);
    if (!((host >> 24) == 10 || (host >> 20) == 0xAC1 || (host >> 16) == 0xC0A8)) return false;
    unsigned long parsed = 0;
    if (!portText || !*portText) return false;
    for (const wchar_t* p = portText; *p; ++p) {
        if (*p < L'0' || *p > L'9' || parsed > 6553) return false;
        parsed = parsed * 10 + static_cast<unsigned>(*p - L'0');
    }
    if (parsed < 1024 || parsed > 65535) return false;
    port = static_cast<UINT16>(parsed); return true;
}

static bool ValidNonce(const wchar_t* text)
{
    if (!text || wcslen(text) != 32) return false;
    for (const wchar_t* p = text; *p; ++p)
        if (!((*p >= L'0' && *p <= L'9') || (*p >= L'a' && *p <= L'f'))) return false;
    return true;
}

static bool OptedIn()
{
    wchar_t ack[32]{};
    return GetEnvironmentVariableW(L"AEGIS_DISPOSABLE_VM_NETWORK_TEST", ack, ARRAYSIZE(ack)) &&
        std::wstring(ack) == L"I_UNDERSTAND";
}

static bool OwnTokenMatches(PSID expected)
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    bool valid = false;
    try {
        DWORD app = 0, size = 0;
        AegisTestProcess::Require(GetTokenInformation(token, TokenIsAppContainer, &app, sizeof(app), &size) && app,
            "Child is not an AppContainer.");
        auto data = AegisTestProcess::TokenInfo(token, TokenAppContainerSid);
        auto* package = reinterpret_cast<TOKEN_APPCONTAINER_INFORMATION*>(data.data());
        AegisTestProcess::Require(data.size() >= sizeof(*package) && package->TokenAppContainer &&
            IsValidSid(package->TokenAppContainer) && EqualSid(package->TokenAppContainer, expected), "Child SID mismatch.");
        auto groups = AegisTestProcess::TokenInfo(token, TokenCapabilities);
        auto* caps = reinterpret_cast<TOKEN_GROUPS*>(groups.data());
        AegisTestProcess::Require(groups.size() >= sizeof(TOKEN_GROUPS) && caps->GroupCount == 1 &&
            caps->Groups[0].Sid && IsValidSid(caps->Groups[0].Sid), "Child capability mismatch.");
        alignas(DWORD) BYTE privateSid[SECURITY_MAX_SID_SIZE]{}; DWORD len = sizeof(privateSid);
        AegisTestProcess::Require(CreateWellKnownSid(static_cast<WELL_KNOWN_SID_TYPE>(87), nullptr, privateSid, &len) &&
            EqualSid(caps->Groups[0].Sid, privateSid) && (caps->Groups[0].Attributes & SE_GROUP_ENABLED) &&
            !(caps->Groups[0].Attributes & SE_GROUP_USE_FOR_DENY_ONLY), "Private capability mismatch.");
        valid = true;
    } catch (...) { valid = false; }
    CloseHandle(token); return valid;
}

// The child performs its own WFP readback before creating a socket. If BFE
// denies AppContainer read access or any field is missing, it does not attempt
// traffic. Direct --client invocation cannot bypass the matching filter check.
static int Client(int argc, wchar_t** argv)
{
    if (argc != 10 || !OptedIn() || std::wstring(argv[9]) != L"--acknowledge-isolated-test") return 2;
    UINT32 address = 0; UINT16 port = 0; AegisTestPolicy::Keys keys{};
    if (!ParseEndpoint(argv[2], argv[3], address, port) || !ValidNonce(argv[4]) ||
        !ParseGuid(argv[6], keys.provider) || !ParseGuid(argv[7], keys.sublayer) || !ParseGuid(argv[8], keys.filter)) return 2;
    PSID sid = nullptr; if (!ConvertStringSidToSidW(argv[5], &sid)) return 2;
    bool valid = OwnTokenMatches(sid);
    HANDLE engine = nullptr; FWPM_FILTER0* filter = nullptr;
    if (valid && FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &engine) == ERROR_SUCCESS &&
        FwpmFilterGetByKey0(engine, &keys.filter, &filter) == ERROR_SUCCESS && filter)
        valid = AegisTestPolicy::ReadbackMatches(engine, keys, sid, address, port, filter->filterId);
    else valid = false;
    if (filter) FwpmFreeMemory0(reinterpret_cast<void**>(&filter));
    if (engine) FwpmEngineClose0(engine);
    LocalFree(sid);
    if (!valid) return 3; // No network attempt without child-side readback.
    WSADATA data{}; if (WSAStartup(MAKEWORD(2, 2), &data)) return 4;
    SOCKET socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP); int result = 4;
    if (socket != INVALID_SOCKET) {
        u_long nonblocking = 1;
        if (ioctlsocket(socket, FIONBIO, &nonblocking) == 0) {
            sockaddr_in target{}; target.sin_family = AF_INET; target.sin_addr.s_addr = htonl(address); target.sin_port = htons(port);
            const int connectResult = connect(socket, reinterpret_cast<sockaddr*>(&target), sizeof(target));
            int error = connectResult == 0 ? 0 : WSAGetLastError();
            if (error == WSAEWOULDBLOCK || error == WSAEINPROGRESS) {
                fd_set writable{}, failed{}; FD_ZERO(&writable); FD_ZERO(&failed); FD_SET(socket, &writable); FD_SET(socket, &failed);
                timeval timeout{ 5, 0 }; const int ready = select(0, nullptr, &writable, &failed, &timeout);
                int length = sizeof(error);
                if (ready > 0 && getsockopt(socket, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) == 0) { }
                else error = WSAETIMEDOUT;
            }
            if (error == 0) {
                char payload[33]{}; for (int i = 0; i < 32; ++i) payload[i] = static_cast<char>(argv[4][i]); payload[32] = '\n';
                result = send(socket, payload, sizeof(payload), 0) == sizeof(payload) ? 0 : 4;
            } else result = error == WSAEACCES ? 5 : 4; // Not proof of a WFP decision.
        }
        closesocket(socket);
    }
    WSACleanup(); return result;
}

static void WriteLedger(const std::wstring& image, const GUID& run, const AegisTestPolicy::Keys& keys,
                        const std::wstring& profile, const wchar_t* endpoint, UINT16 port, const wchar_t* hash,
                        PSID sid, const AegisTestProcess::Child& child)
{
    const auto slash = image.find_last_of(L'\\');
    if (slash == std::wstring::npos) throw std::runtime_error("Cannot locate test ledger directory.");
    const std::wstring path = image.substr(0, slash) + L"\\wfp-supervisor-" + GuidText(run) + L".txt";
    LPWSTR sidText = nullptr;
    if (!ConvertSidToStringSidW(sid, &sidText)) throw std::runtime_error("Cannot record test identity.");
    const std::wstring sidValue(sidText); LocalFree(sidText);
    ULARGE_INTEGER created{}; created.LowPart = child.creation.dwLowDateTime; created.HighPart = child.creation.dwHighDateTime;
    std::wstring record = L"run=" + GuidText(run) + L"\r\nprovider=" + GuidText(keys.provider) +
        L"\r\nsublayer=" + GuidText(keys.sublayer) + L"\r\nfilter=" + GuidText(keys.filter) +
        L"\r\nprofile=" + profile + L"\r\nendpoint=" + endpoint + L":" + std::to_wstring(port) +
        L"\r\npackageSid=" + sidValue + L"\r\nchildPid=" + std::to_wstring(child.pid) +
        L"\r\nchildCreationFileTime=" + std::to_wstring(created.QuadPart) +
        L"\r\nimageSha256=" + hash + L"\r\nreview=operator-asserted\r\n";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create unique policy recovery ledger.");
    DWORD written = 0; const DWORD bytes = static_cast<DWORD>(record.size() * sizeof(wchar_t));
    const bool ok = WriteFile(file, record.data(), bytes, &written, nullptr) && written == bytes && FlushFileBuffers(file);
    const bool closed = CloseHandle(file) != FALSE;
    if (!ok || !closed) throw std::runtime_error("Cannot durably record test policy recovery identifiers.");
    std::wprintf(L"RECOVERY_LEDGER: %ls\n", path.c_str()); std::fflush(stdout);
}

// Self-test verifies exact persistent-filter matching without opening BFE.
static int SelfTest()
{
    PSID sid = nullptr;
    if (!ConvertStringSidToSidW(L"S-1-15-2-123456", &sid)) return 1;
    AegisTestPolicy::Keys keys{ NewGuid(), NewGuid(), NewGuid() };
    FWPM_FILTER_CONDITION0 c[4]{};
    c[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID; c[0].matchType = FWP_MATCH_EQUAL; c[0].conditionValue.type = FWP_SID; c[0].conditionValue.sid = static_cast<SID*>(sid);
    c[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c[1].matchType = FWP_MATCH_EQUAL; c[1].conditionValue.type = FWP_UINT32; c[1].conditionValue.uint32 = 0x0a010203;
    c[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c[2].matchType = FWP_MATCH_EQUAL; c[2].conditionValue.type = FWP_UINT16; c[2].conditionValue.uint16 = 42000;
    c[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; c[3].matchType = FWP_MATCH_EQUAL; c[3].conditionValue.type = FWP_UINT8; c[3].conditionValue.uint8 = IPPROTO_TCP;
    FWPM_FILTER0 f{}; f.filterKey = keys.filter; f.providerKey = &keys.provider; f.subLayerKey = keys.sublayer;
    f.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4; f.flags = FWPM_FILTER_FLAG_PERSISTENT; f.filterId = 7;
    f.action.type = FWP_ACTION_BLOCK; f.numFilterConditions = ARRAYSIZE(c); f.filterCondition = c;
    bool ok = AegisTestPolicy::ExactScope(&f, keys, sid, 0x0a010203, 42000, 7);
    f.filterId++; ok = ok && !AegisTestPolicy::ExactScope(&f, keys, sid, 0x0a010203, 42000, 7); f.filterId--;
    f.flags = 0; ok = ok && !AegisTestPolicy::ExactScope(&f, keys, sid, 0x0a010203, 42000, 7);
    FWPM_PROVIDER0 provider{}; provider.providerKey = keys.provider; provider.flags = FWPM_PROVIDER_FLAG_PERSISTENT;
    FWPM_SUBLAYER0 sublayer{}; sublayer.subLayerKey = keys.sublayer; sublayer.providerKey = &keys.provider;
    sublayer.flags = FWPM_SUBLAYER_FLAG_PERSISTENT; sublayer.weight = 0x100;
    ok = ok && AegisTestPolicy::ExactProvider(&provider, keys) && AegisTestPolicy::ExactSublayer(&sublayer, keys);
    provider.flags |= FWPM_PROVIDER_FLAG_DISABLED; ok = ok && !AegisTestPolicy::ExactProvider(&provider, keys);
    sublayer.weight++; ok = ok && !AegisTestPolicy::ExactSublayer(&sublayer, keys);
    LocalFree(sid);
    using AegisTestSupervisor::Gate;
    { Gate gate; bool rejected = false; try { gate.Resumed(); } catch (const std::logic_error&) { rejected = true; } ok = ok && rejected; }
    { Gate gate; bool rejected = false; try { gate.ChildVerified(); gate.ObserverArmed(); } catch (const std::logic_error&) { rejected = true; } ok = ok && rejected; }
    { Gate gate; bool rejected = false; try { gate.ChildVerified(); gate.PolicyReadBack(); gate.ObserverArmed(); gate.Cleaned(); } catch (const std::logic_error&) { rejected = true; } ok = ok && rejected; }
    { Gate gate; gate.ChildVerified(); gate.PolicyReadBack(); gate.ObserverArmed(); gate.Resumed(); gate.Exited(); gate.Cleaned();
      ok = ok && gate.State() == AegisTestSupervisor::Phase::Cleaned; }
    UINT32 endpoint = 0; UINT16 port = 0;
    ok = ok && ParseEndpoint(L"10.1.2.3", L"42000", endpoint, port) && endpoint == 0x0a010203 && port == 42000;
    ok = ok && !ParseEndpoint(L"127.0.0.1", L"42000", endpoint, port) && !ParseEndpoint(L"10.1.2.3", L"80", endpoint, port);
    ok = ok && ValidNonce(L"0123456789abcdef0123456789abcdef") && !ValidNonce(L"invalid") && Client(0, nullptr) == 2;
    ok = ok && AegisTestProcess::IsNonzeroGuidText(L"11111111-1111-1111-1111-111111111111") &&
        !AegisTestProcess::IsNonzeroGuidText(L"00000000-0000-0000-0000-000000000000") &&
        !AegisTestProcess::IsNonzeroGuidText(L"not-a-guid");
    if (!ok) return 1;
    std::puts("PASS: persistent test policy scope synthetic checks; no WFP, process or socket operations."); return 0;
}

class TestObservation {
    HANDLE engine_ = nullptr, subscription_ = nullptr;
    FWP_BYTE_BLOB* appId_ = nullptr;
    FWPM_LAYER0* layer_ = nullptr;
    AegisTestObserver::Expected* expected_ = nullptr;
    struct CallbackContext { AegisTestObserver::Expected* expected; HANDLE signal; };
    CallbackContext* context_ = nullptr;
    bool stopped_ = true;
    static void CALLBACK OnEvent(void* context, const FWPM_NET_EVENT5* event)
    {
        auto* state = static_cast<CallbackContext*>(context);
        AegisTestObserver::Observe(state->expected, event);
        if (AegisTestObserver::Matches(event, *state->expected)) SetEvent(state->signal);
    }
public:
    TestObservation() = default;
    TestObservation(const TestObservation&) = delete;
    TestObservation& operator=(const TestObservation&) = delete;
    ~TestObservation()
    {
        if (subscription_ && engine_) stopped_ = FwpmNetEventUnsubscribe0(engine_, subscription_) == ERROR_SUCCESS;
        if (stopped_) {
            if (context_) { if (context_->signal) CloseHandle(context_->signal); delete context_; }
            if (appId_) FwpmFreeMemory0(reinterpret_cast<void**>(&appId_));
            delete expected_;
        }
        // Failed unsubscribe does not prove quiescence. Keep context and app
        // ID alive until process exit rather than freeing under a callback.
        if (layer_) FwpmFreeMemory0(reinterpret_cast<void**>(&layer_));
    }

    void Arm(HANDLE engine, const std::wstring& image, PSID sid, UINT32 address, UINT16 port, UINT64 filterId)
    {
        if (!engine || !sid || !filterId) throw std::runtime_error("Invalid observer scope.");
        engine_ = engine;
        FWP_VALUE0* option = nullptr;
        const DWORD optionStatus = FwpmEngineGetOption0(engine_, FWPM_ENGINE_COLLECT_NET_EVENTS, &option);
        const bool enabled = optionStatus == ERROR_SUCCESS && option && option->type == FWP_UINT32 && option->uint32 == 1;
        if (option) FwpmFreeMemory0(reinterpret_cast<void**>(&option));
        if (!enabled) throw std::runtime_error("WFP event collection is unavailable; settings were not changed.");
        AegisTestPolicy::Check(FwpmGetAppIdFromFileName0(image.c_str(), &appId_), "Cannot identify test image for WFP.");
        AegisTestPolicy::Check(FwpmLayerGetByKey0(engine_, &FWPM_LAYER_ALE_AUTH_CONNECT_V4, &layer_), "Cannot identify WFP layer.");
        if (!appId_ || !layer_) throw std::runtime_error("WFP observer identity unavailable.");
        FILETIME now{}; GetSystemTimeAsFileTime(&now);
        ULARGE_INTEGER begin{}; begin.LowPart = now.dwLowDateTime; begin.HighPart = now.dwHighDateTime;
        const UINT64 end = begin.QuadPart + 300000000ULL; // Bounded 30-second event window.
        expected_ = new AegisTestObserver::Expected{ filterId, layer_->layerId, address, port, appId_, sid, begin.QuadPart, end };
        context_ = new CallbackContext{ expected_, CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        if (!context_->signal) throw std::runtime_error("Cannot create test observer synchronization event.");
        FWPM_FILTER_CONDITION0 c[5]{};
        c[0].fieldKey = FWPM_CONDITION_ALE_APP_ID; c[0].matchType = FWP_MATCH_EQUAL; c[0].conditionValue.type = FWP_BYTE_BLOB_TYPE; c[0].conditionValue.byteBlob = appId_;
        c[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c[1].matchType = FWP_MATCH_EQUAL; c[1].conditionValue.type = FWP_UINT32; c[1].conditionValue.uint32 = address;
        c[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c[2].matchType = FWP_MATCH_EQUAL; c[2].conditionValue.type = FWP_UINT16; c[2].conditionValue.uint16 = port;
        c[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; c[3].matchType = FWP_MATCH_EQUAL; c[3].conditionValue.type = FWP_UINT8; c[3].conditionValue.uint8 = IPPROTO_TCP;
        c[4].fieldKey = FWPM_CONDITION_NET_EVENT_TYPE; c[4].matchType = FWP_MATCH_EQUAL; c[4].conditionValue.type = FWP_UINT32; c[4].conditionValue.uint32 = FWPM_NET_EVENT_TYPE_CLASSIFY_DROP;
        FWPM_NET_EVENT_ENUM_TEMPLATE0 filter{}; filter.startTime = now;
        ULARGE_INTEGER until{}; until.QuadPart = end; filter.endTime.dwLowDateTime = until.LowPart; filter.endTime.dwHighDateTime = until.HighPart;
        filter.numFilterConditions = ARRAYSIZE(c); filter.filterCondition = c;
        FWPM_NET_EVENT_SUBSCRIPTION0 subscription{}; subscription.enumTemplate = &filter;
        AegisTestPolicy::Check(FwpmNetEventSubscribe4(engine_, &subscription, OnEvent, context_, &subscription_),
            "Cannot arm WFP decision observer.");
        stopped_ = false;
    }

    bool Stop()
    {
        if (!subscription_) return stopped_;
        const DWORD result = FwpmNetEventUnsubscribe0(engine_, subscription_);
        if (result != ERROR_SUCCESS) return false;
        subscription_ = nullptr; stopped_ = true; return true;
    }
    void WaitForDecision(DWORD milliseconds)
    {
        if (context_ && context_->signal) (void)WaitForSingleObject(context_->signal, milliseconds);
    }
    unsigned Matches() const { return expected_ ? expected_->matches.load() : 0; }
    unsigned Conflicts() const { return expected_ ? expected_->conflicts.load() : 0; }
};

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return SelfTest();
    if (argc >= 2 && std::wstring(argv[1]) == L"--client") return Client(argc, argv);
    SYSTEM_INFO system{}; GetNativeSystemInfo(&system);
    if (argc != 7 || std::wstring(argv[1]) != L"--execute" || !OptedIn() ||
        std::wstring(argv[6]) != L"--acknowledge-persistent-disposable-test-policy" ||
        system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64 || !AegisTestObserver::IsAdministrator()) {
        std::fputs("BLOCKED: approved disposable Windows x64 VM, administrator and explicit persistent-policy opt-in required.\n", stderr);
        return 2;
    }
    UINT32 address = 0; UINT16 port = 0;
    if (!ParseEndpoint(argv[2], argv[3], address, port) || !ValidNonce(argv[4])) return 2;
    const GUID run = NewGuid();
    AegisTestPolicy::Keys keys{ NewGuid(), NewGuid(), NewGuid() };
    const std::wstring profile = L"AegisSupervisedTest." + GuidText(run);
    PSID sid = nullptr; bool profileCreated = false, childCreated = false, childExited = false, policyAbsent = false;
    bool observerStopped = true, profileRemoved = false, unexpectedSend = false; int exitCode = 1; DWORD childCode = STILL_ACTIVE;
    std::unique_ptr<AegisTestProcess::VerifiedImage> image;
    std::unique_ptr<AegisTestProcess::Child> child;
    AegisTestPolicy::PersistentTestPolicy policy;
    TestObservation observation; // Destroy before policy closes its engine.
    AegisTestSupervisor::Gate gate;
    try {
        image = std::make_unique<AegisTestProcess::VerifiedImage>(AegisTestProcess::VerifyAndHoldSelf(argv[5]));
        const HRESULT hr = CreateAppContainerProfile(profile.c_str(), profile.c_str(), L"Disposable Aegis supervised test", nullptr, 0, &sid);
        if (FAILED(hr)) throw std::runtime_error("Cannot create a fresh test AppContainer profile.");
        profileCreated = true;
        if (!sid) throw std::runtime_error("Fresh test AppContainer profile has no SID.");
        child = std::make_unique<AegisTestProcess::Child>();
        AegisTestProcess::CreateSuspended(*child, *image, sid, argv[2], port, argv[4],
            GuidText(keys.provider), GuidText(keys.sublayer), GuidText(keys.filter));
        childCreated = true;
        if (!AegisTestProcess::VerifySuspended(*child, *image, sid)) throw std::runtime_error("Suspended test child identity could not be verified.");
        gate.ChildVerified();
        WriteLedger(image->path, run, keys, profile, argv[2], port, argv[5], sid, *child);
        policy.Install(keys, sid, address, port);
        gate.PolicyReadBack();
        observation.Arm(policy.Engine(), image->path, sid, address, port, policy.RuntimeId());
        gate.ObserverArmed();
        policy.Revalidate(sid, address, port);
        if (!AegisTestProcess::VerifySuspended(*child, *image, sid)) throw std::runtime_error("Child identity changed before resume.");
        LPWSTR sidText = nullptr; if (!ConvertSidToStringSidW(sid, &sidText)) throw std::runtime_error("Cannot format test identity.");
        ULARGE_INTEGER created{}; created.LowPart = child->creation.dwLowDateTime; created.HighPart = child->creation.dwHighDateTime;
        std::wprintf(L"READY: run=%ls pid=%lu creationFileTime=%llu sid=%ls filterId=%llu observerArmed=true\n",
            GuidText(run).c_str(), child->pid, created.QuadPart, sidText, policy.RuntimeId());
        LocalFree(sidText); std::fflush(stdout);
        if (ResumeThread(child->thread) != 1) throw std::runtime_error("Unable to resume the single suspended child.");
        gate.Resumed();
        DWORD wait = WaitForSingleObject(child->process, 15000);
        if (wait != WAIT_OBJECT_0 && !AegisTestProcess::TerminateAndWait(*child, 5000))
            throw std::runtime_error("Child termination could not be confirmed; persistent policy retained.");
        childExited = WaitForSingleObject(child->process, 0) == WAIT_OBJECT_0;
        if (!childExited || !GetExitCodeProcess(child->process, &childCode)) throw std::runtime_error("Cannot confirm test child exit.");
        unexpectedSend = childCode == 0;
        gate.Exited();
        // Wait only for the observer's actual callback signal, bounded; a
        // timeout remains inconclusive and is never interpreted as a block.
        observation.WaitForDecision(3000);
        observerStopped = observation.Stop();
        if (!observerStopped) throw std::runtime_error("WFP observer teardown could not be confirmed.");
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if (!QueryInformationJobObject(child->job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) ||
            accounting.ActiveProcesses != 0) throw std::runtime_error("Test job may still contain a process; persistent policy retained.");
        policy.DeleteAfterChildExit(child->process, child->job, sid, address, port);
        policyAbsent = policy.VerifyAbsentIndependently();
        if (!policyAbsent) throw std::runtime_error("Test policy cleanup could not be independently verified.");
        gate.Cleaned();
        // A child-confirmed send to the denied endpoint is a failed test even
        // without receiver evidence. No other result establishes enforcement.
        const char* status = childCode == 0 ? "FAILED" : "INCONCLUSIVE";
        std::printf("{\"status\":\"%s\",\"childExitCode\":%lu,\"matchingDropEvents\":%u,\"conflictingEvents\":%u,\"pidFlowAttribution\":\"NOT_ESTABLISHED\",\"bfeRestartContinuity\":\"BLOCKED\"}\n",
            status, childCode, observation.Matches(), observation.Conflicts());
        exitCode = childCode == 0 ? 1 : 2; // No WFP packet verification from this observer alone.
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "INCONCLUSIVE: %s\n", ex.what());
    }
    // Stop and confirm the retained test child before intentionally deleting
    // its persistent policy; uncertain termination leaves policy and ledger.
    // CreateSuspended can throw after process creation; its out-parameter keeps
    // the handle available even when cleanup was not confirmed internally.
    if (child && child->process) childCreated = true;
    if (childCreated && !childExited) childExited = AegisTestProcess::TerminateAndWait(*child, 5000);
    if (childCreated && childExited && policy.Committed()) {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if (QueryInformationJobObject(child->job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) && accounting.ActiveProcesses == 0) {
            try { policy.DeleteAfterChildExit(child->process, child->job, sid, address, port); policyAbsent = policy.VerifyAbsentIndependently(); }
            catch (...) { policyAbsent = false; }
        }
    }
    if (!observation.Stop()) observerStopped = false;
    if (childCreated && childExited && !AegisTestProcess::CloseAfterExit(*child)) exitCode = 1;
    if (profileCreated && (!childCreated || childExited) && (!policy.Committed() && (policyAbsent || !childCreated))) {
        profileRemoved = SUCCEEDED(DeleteAppContainerProfile(profile.c_str()));
        if (!profileRemoved) exitCode = 1;
    }
    if (!childExited && childCreated) std::fputs("BLOCKED: child exit unconfirmed; policy state may be incomplete. Preserve the VM and any recovery ledger.\n", stderr);
    if (policy.Committed() || (childCreated && !policyAbsent)) std::fputs("INCONCLUSIVE: owned policy cleanup unverified; preserve recovery ledger and discard VM.\n", stderr);
    if (!observerStopped) std::fputs("INCONCLUSIVE: observer teardown not confirmed.\n", stderr);
    std::printf("{\"finalStatus\":\"%s\",\"runId\":\"%ls\",\"childCreated\":%s,\"childExitConfirmed\":%s,\"ownedPolicyRemovalVerified\":%s,\"observerStopped\":%s,\"profileRemoved\":%s,\"pidFlowAttribution\":\"NOT_ESTABLISHED\",\"bfeRestartContinuity\":\"BLOCKED\"}\n",
        unexpectedSend ? "FAILED" : "INCONCLUSIVE", GuidText(run).c_str(), childCreated ? "true" : "false",
        childExited ? "true" : "false", policyAbsent ? "true" : "false", observerStopped ? "true" : "false",
        profileRemoved ? "true" : "false");
    // Observer may still reference the SID after unsubscribe failure. Retain it
    // until process exit instead of freeing under a possible callback.
    if (sid && observerStopped) FreeSid(sid);
    return exitCode;
}
