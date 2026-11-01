// Research-only WFP object lifetime test. No CEF or packet enforcement claim.
// The only filters are dynamic blocks intersecting an owned synthetic SID,
// one owned loopback destination/port, and TCP. No global settings are changed.
#include <winsock2.h>
#include <windows.h>
#include <fwpmu.h>
#include <winerror.h>
#include <userenv.h>
#include <rpc.h>
#include <sddl.h>
#include <cstdio>
#include <cwchar>
#include <stdexcept>
#include <string>
#include <initializer_list>

#pragma comment(lib, "fwpuclnt.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "rpcrt4.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "advapi32.lib")

static void Check(DWORD status, const char* operation)
{
    if (status != ERROR_SUCCESS) {
        std::fprintf(stderr, "FAIL: %s returned 0x%08lX\n", operation, status);
        throw std::runtime_error(operation);
    }
}

static GUID NewGuid()
{
    GUID result{};
    RPC_STATUS status = UuidCreate(&result);
    if (status != RPC_S_OK && status != RPC_S_UUID_LOCAL_ONLY) Check(status, "UuidCreate");
    return result;
}

static void PrintKey(const wchar_t* label, const GUID& key)
{
    RPC_WSTR value = nullptr;
    Check(UuidToStringW(const_cast<GUID*>(&key), &value), "UuidToStringW");
    std::printf("OBSERVED: %ls=%ls\n", label, reinterpret_cast<wchar_t*>(value));
    Check(RpcStringFreeW(&value), "RpcStringFreeW");
}

struct Fixture
{
    PSID sid = nullptr;
    SOCKET socket = INVALID_SOCKET;
    UINT16 port = 0;
    GUID filter = NewGuid();
    ~Fixture() { if (socket != INVALID_SOCKET) closesocket(socket); if (sid) FreeSid(sid); }
    void Create()
    {
        GUID id = NewGuid();
        RPC_WSTR stringId = nullptr;
        Check(UuidToStringW(&id, &stringId), "UuidToStringW");
        std::wstring name = L"AegisWfpResearch.";
        name += reinterpret_cast<wchar_t*>(stringId);
        Check(RpcStringFreeW(&stringId), "RpcStringFreeW");
        HRESULT hr = CreateAppContainerProfile(name.c_str(), name.c_str(), L"Disposable Aegis WFP object research", nullptr, 0, &sid);
        if (FAILED(hr)) Check(static_cast<DWORD>(hr), "CreateAppContainerProfile");
        if (!sid) throw std::runtime_error("AppContainer SID missing");
        socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (socket == INVALID_SOCKET) Check(WSAGetLastError(), "socket");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (bind(socket, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) Check(WSAGetLastError(), "bind");
        int length = sizeof(address);
        if (getsockname(socket, reinterpret_cast<sockaddr*>(&address), &length) == SOCKET_ERROR) Check(WSAGetLastError(), "getsockname");
        port = ntohs(address.sin_port);
        if (port == 0) throw std::runtime_error("Assigned port is zero");
        std::printf("OBSERVED: owned synthetic identity %ls with reserved IPv4 loopback port %u\n", name.c_str(), port);
    }
};

struct Engine
{
    HANDLE handle = nullptr;
    bool transaction = false;
    ~Engine()
    {
        if (handle && transaction) { DWORD code = FwpmTransactionAbort0(handle); if (code) std::fprintf(stderr, "FAIL: cleanup FwpmTransactionAbort0 0x%08lX\n", code); }
        if (handle) { DWORD code = FwpmEngineClose0(handle); if (code) std::fprintf(stderr, "FAIL: cleanup FwpmEngineClose0 0x%08lX\n", code); }
    }
    void Open(bool dynamic)
    {
        FWPM_SESSION0 session{};
        if (dynamic) session.flags = FWPM_SESSION_FLAG_DYNAMIC;
        Check(FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session, &handle), "FwpmEngineOpen0");
    }
    void Begin() { Check(FwpmTransactionBegin0(handle, 0), "FwpmTransactionBegin0"); transaction = true; }
    void Commit() { Check(FwpmTransactionCommit0(handle), "FwpmTransactionCommit0"); transaction = false; }
    void Close() { Check(FwpmEngineClose0(handle), "FwpmEngineClose0"); handle = nullptr; }
};

static bool ExactScope(const FWPM_FILTER0* filter, const GUID& provider, const GUID& sublayer, const Fixture& fixture);

static void AddFilter(HANDLE engine, const GUID& provider, const GUID& sublayer, const Fixture& fixture)
{
    FWPM_FILTER_CONDITION0 conditions[4]{};
    conditions[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID;
    conditions[0].matchType = FWP_MATCH_EQUAL;
    conditions[0].conditionValue.type = FWP_SID;
    conditions[0].conditionValue.sid = static_cast<SID*>(fixture.sid);
    conditions[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
    conditions[1].matchType = FWP_MATCH_EQUAL;
    conditions[1].conditionValue.type = FWP_UINT32;
    conditions[1].conditionValue.uint32 = 0x7F000001; // IPv4 host byte order.
    conditions[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
    conditions[2].matchType = FWP_MATCH_EQUAL;
    conditions[2].conditionValue.type = FWP_UINT16;
    conditions[2].conditionValue.uint16 = fixture.port;
    conditions[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
    conditions[3].matchType = FWP_MATCH_EQUAL;
    conditions[3].conditionValue.type = FWP_UINT8;
    conditions[3].conditionValue.uint8 = IPPROTO_TCP;
    FWPM_FILTER0 filter{};
    filter.filterKey = fixture.filter;
    filter.displayData.name = const_cast<wchar_t*>(L"Aegis disposable SID/loopback/TCP research block");
    filter.providerKey = const_cast<GUID*>(&provider);
    filter.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4;
    filter.subLayerKey = sublayer;
    filter.weight.type = FWP_EMPTY;
    filter.numFilterConditions = ARRAYSIZE(conditions);
    filter.filterCondition = conditions;
    filter.action.type = FWP_ACTION_BLOCK;
    if (!ExactScope(&filter, provider, sublayer, fixture)) throw std::runtime_error("Refusing to stage a filter with unexpected scope.");
    Check(FwpmFilterAdd0(engine, &filter, nullptr, nullptr), "FwpmFilterAdd0");
}

static bool ExactScope(const FWPM_FILTER0* filter, const GUID& provider, const GUID& sublayer, const Fixture& fixture)
{
    bool sid = false, address = false, port = false, protocol = false;
    bool header = filter && IsEqualGUID(filter->filterKey, fixture.filter) && filter->flags == 0 &&
        filter->providerKey && IsEqualGUID(*filter->providerKey, provider) &&
        IsEqualGUID(filter->subLayerKey, sublayer) && IsEqualGUID(filter->layerKey, FWPM_LAYER_ALE_AUTH_CONNECT_V4) &&
        filter->action.type == FWP_ACTION_BLOCK && filter->numFilterConditions == 4;
    if (header) for (UINT32 i = 0; i < filter->numFilterConditions; ++i) {
        const auto& c = filter->filterCondition[i];
        if (c.matchType != FWP_MATCH_EQUAL) { header = false; break; }
        if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_ALE_PACKAGE_ID))
            sid = c.conditionValue.type == FWP_SID && c.conditionValue.sid && EqualSid(c.conditionValue.sid, fixture.sid);
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_ADDRESS))
            address = c.conditionValue.type == FWP_UINT32 && c.conditionValue.uint32 == 0x7F000001;
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_PORT))
            port = c.conditionValue.type == FWP_UINT16 && c.conditionValue.uint16 == fixture.port;
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_PROTOCOL))
            protocol = c.conditionValue.type == FWP_UINT8 && c.conditionValue.uint8 == IPPROTO_TCP;
        else { header = false; break; }
    }
    return header && sid && address && port && protocol;
}

static void VerifyFilter(HANDLE engine, const GUID& provider, const GUID& sublayer, const Fixture& fixture)
{
    FWPM_FILTER0* filter = nullptr;
    Check(FwpmFilterGetByKey0(engine, &fixture.filter, &filter), "FwpmFilterGetByKey0");
    bool valid = ExactScope(filter, provider, sublayer, fixture);
    if (filter) FwpmFreeMemory0(reinterpret_cast<void**>(&filter));
    if (!valid) throw std::runtime_error("Returned filter scope does not exactly match owned fixture.");
}

int wmain(int argc, wchar_t** argv)
{
    wchar_t github[16]{}, environment[32]{};
    if (argc != 2 || std::wstring(argv[1]) != L"--disposable-runner" ||
        !GetEnvironmentVariableW(L"GITHUB_ACTIONS", github, ARRAYSIZE(github)) ||
        !GetEnvironmentVariableW(L"RUNNER_ENVIRONMENT", environment, ARRAYSIZE(environment)) ||
        std::wstring(github) != L"true" || std::wstring(environment) != L"github-hosted") {
        std::fprintf(stderr, "Explicit opt-in on a disposable GitHub-hosted runner is required.\n");
        return 2;
    }
    WSADATA data{};
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) { std::fprintf(stderr, "FAIL: WSAStartup\n"); return 1; }
    int result = 1;
    try {
        Fixture a, b;
        a.Create(); b.Create();
        if (EqualSid(a.sid, b.sid) || a.port == b.port) throw std::runtime_error("Fixture identities or ports are not distinct.");
        GUID providerKey = NewGuid(), sublayerKey = NewGuid();
        PrintKey(L"owned-provider", providerKey);
        PrintKey(L"owned-sublayer", sublayerKey);
        PrintKey(L"owned-filter-A", a.filter);
        PrintKey(L"owned-filter-B", b.filter);
        Engine dynamic, reader;
        dynamic.Open(true);
        dynamic.Begin();
        FWPM_PROVIDER0 provider{};
        provider.providerKey = providerKey;
        provider.displayData.name = const_cast<wchar_t*>(L"Aegis disposable WFP research provider");
        Check(FwpmProviderAdd0(dynamic.handle, &provider, nullptr), "FwpmProviderAdd0");
        FWPM_SUBLAYER0 sublayer{};
        sublayer.subLayerKey = sublayerKey;
        sublayer.providerKey = &providerKey;
        sublayer.displayData.name = const_cast<wchar_t*>(L"Aegis disposable WFP research sublayer");
        sublayer.weight = 0x100;
        Check(FwpmSubLayerAdd0(dynamic.handle, &sublayer, nullptr), "FwpmSubLayerAdd0");
        AddFilter(dynamic.handle, providerKey, sublayerKey, a);
        AddFilter(dynamic.handle, providerKey, sublayerKey, b);
        VerifyFilter(dynamic.handle, providerKey, sublayerKey, a);
        VerifyFilter(dynamic.handle, providerKey, sublayerKey, b);
        dynamic.Commit();
        VerifyFilter(dynamic.handle, providerKey, sublayerKey, a);
        VerifyFilter(dynamic.handle, providerKey, sublayerKey, b);
        std::puts("PASS: two dynamic WFP block objects read back with the exact owned SID, TCP and loopback endpoint conditions.");
        reader.Open(false);
        dynamic.Close();
        for (const Fixture* f : { &a, &b }) {
            FWPM_FILTER0* found = nullptr;
            DWORD code = FwpmFilterGetByKey0(reader.handle, &f->filter, &found);
            if (found) FwpmFreeMemory0(reinterpret_cast<void**>(&found));
            if (code != static_cast<DWORD>(FWP_E_FILTER_NOT_FOUND)) Check(code == ERROR_SUCCESS ? ERROR_ALREADY_EXISTS : code, "dynamic filter removal");
        }
        FWPM_SUBLAYER0* foundSublayer = nullptr;
        DWORD subCode = FwpmSubLayerGetByKey0(reader.handle, &sublayerKey, &foundSublayer);
        if (foundSublayer) FwpmFreeMemory0(reinterpret_cast<void**>(&foundSublayer));
        if (subCode != static_cast<DWORD>(FWP_E_SUBLAYER_NOT_FOUND)) Check(subCode == ERROR_SUCCESS ? ERROR_ALREADY_EXISTS : subCode, "dynamic sublayer removal");
        FWPM_PROVIDER0* foundProvider = nullptr;
        DWORD providerCode = FwpmProviderGetByKey0(reader.handle, &providerKey, &foundProvider);
        if (foundProvider) FwpmFreeMemory0(reinterpret_cast<void**>(&foundProvider));
        if (providerCode != static_cast<DWORD>(FWP_E_PROVIDER_NOT_FOUND)) Check(providerCode == ERROR_SUCCESS ? ERROR_ALREADY_EXISTS : providerCode, "dynamic provider removal");
        reader.Close();
        std::puts("PASS: the owned dynamic filters, sublayer and provider disappeared after their session closed.");
        std::puts("INCONCLUSIVE: no CEF traffic or packet enforcement was tested; dynamic session loss is not fail-closed.");
        result = 0;
    } catch (const std::exception& ex) { std::fprintf(stderr, "FAIL: %s\n", ex.what()); }
    WSACleanup();
    return result;
}
