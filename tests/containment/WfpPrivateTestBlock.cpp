// Isolated test only. Installs one dynamic WFP block against a freshly-created
// AppContainer identity and one RFC1918 IPv4 TCP endpoint. This is not browser
// containment: no process is launched, no WFP decision is observed, and the
// rule disappears when this process or its WFP session ends.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <fwpmu.h>
#include <winerror.h>
#include <userenv.h>
#include <rpc.h>
#include <sddl.h>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <stdexcept>
#include <string>

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
    const RPC_STATUS status = UuidCreate(&result);
    if (status != RPC_S_OK && status != RPC_S_UUID_LOCAL_ONLY) Check(status, "UuidCreate");
    return result;
}

static std::wstring GuidText(const GUID& guid)
{
    RPC_WSTR value = nullptr;
    Check(UuidToStringW(const_cast<GUID*>(&guid), &value), "UuidToStringW");
    std::wstring result(reinterpret_cast<wchar_t*>(value));
    Check(RpcStringFreeW(&value), "RpcStringFreeW");
    return result;
}

static bool IsPrivateV4(UINT32 host)
{
    return (host >> 24) == 10 || (host >> 20) == 0xAC1 || (host >> 16) == 0xC0A8;
}

static bool IsAdministrator()
{
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
    PSID admins = nullptr;
    if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID,
        DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &admins)) return false;
    BOOL member = FALSE;
    const BOOL ok = CheckTokenMembership(nullptr, admins, &member);
    FreeSid(admins);
    return ok && member;
}

static bool ExactScope(const FWPM_FILTER0* filter, const GUID& key, const GUID& provider,
                       const GUID& sublayer, PSID sid, UINT32 address, UINT16 port)
{
    if (!filter || !sid || !IsValidSid(sid) || !filter->providerKey || !filter->filterCondition ||
        !IsEqualGUID(filter->filterKey, key) || !IsEqualGUID(*filter->providerKey, provider) ||
        !IsEqualGUID(filter->subLayerKey, sublayer) || !IsEqualGUID(filter->layerKey, FWPM_LAYER_ALE_AUTH_CONNECT_V4) ||
        filter->action.type != FWP_ACTION_BLOCK || filter->numFilterConditions != 4 ||
        (filter->flags & ~static_cast<UINT32>(FWPM_FILTER_FLAG_INDEXED)) != 0) return false;
    bool identity = false, remote = false, destinationPort = false, protocol = false;
    for (UINT32 i = 0; i < filter->numFilterConditions; ++i) {
        const auto& c = filter->filterCondition[i];
        if (c.matchType != FWP_MATCH_EQUAL) return false;
        if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_ALE_PACKAGE_ID)) {
            if (identity || c.conditionValue.type != FWP_SID || !c.conditionValue.sid ||
                !IsValidSid(c.conditionValue.sid) || !EqualSid(c.conditionValue.sid, sid)) return false;
            identity = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_ADDRESS)) {
            if (remote || c.conditionValue.type != FWP_UINT32 || c.conditionValue.uint32 != address) return false;
            remote = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_PORT)) {
            if (destinationPort || c.conditionValue.type != FWP_UINT16 || c.conditionValue.uint16 != port) return false;
            destinationPort = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_PROTOCOL)) {
            if (protocol || c.conditionValue.type != FWP_UINT8 || c.conditionValue.uint8 != IPPROTO_TCP) return false;
            protocol = true;
        } else return false;
    }
    return identity && remote && destinationPort && protocol;
}

static bool MatchesRuntimeFilter(const FWPM_FILTER0* filter, const GUID& key, const GUID& provider,
                                 const GUID& sublayer, PSID sid, UINT32 address, UINT16 port, UINT64 runtimeId)
{
    return ExactScope(filter, key, provider, sublayer, sid, address, port) && filter->filterId == runtimeId;
}

// No BFE session, profile creation, policy mutation or network request.
static int SelfTest()
{
    PSID sid = nullptr;
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
    if (!AllocateAndInitializeSid(&authority, 1, 1234, 0, 0, 0, 0, 0, 0, 0, &sid)) return 1;
    int result = 1;
    try {
        const GUID key = NewGuid(), provider = NewGuid(), sublayer = NewGuid();
        constexpr UINT32 address = 0x0a010203;
        constexpr UINT16 port = 42000;
        FWPM_FILTER_CONDITION0 c[4]{};
        c[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID; c[0].matchType = FWP_MATCH_EQUAL;
        c[0].conditionValue.type = FWP_SID; c[0].conditionValue.sid = static_cast<SID*>(sid);
        c[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c[1].matchType = FWP_MATCH_EQUAL;
        c[1].conditionValue.type = FWP_UINT32; c[1].conditionValue.uint32 = address;
        c[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c[2].matchType = FWP_MATCH_EQUAL;
        c[2].conditionValue.type = FWP_UINT16; c[2].conditionValue.uint16 = port;
        c[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; c[3].matchType = FWP_MATCH_EQUAL;
        c[3].conditionValue.type = FWP_UINT8; c[3].conditionValue.uint8 = IPPROTO_TCP;
        FWPM_FILTER0 filter{}; filter.filterKey = key; filter.providerKey = const_cast<GUID*>(&provider);
        filter.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4; filter.subLayerKey = sublayer;
        filter.action.type = FWP_ACTION_BLOCK; filter.numFilterConditions = ARRAYSIZE(c); filter.filterCondition = c;
        filter.filterId = 9;
        if (!IsPrivateV4(address) || IsPrivateV4(0x7f000001) || IsPrivateV4(0x08080808) ||
            !MatchesRuntimeFilter(&filter, key, provider, sublayer, sid, address, port, 9)) throw std::runtime_error("Valid scope rejected.");
        if (MatchesRuntimeFilter(nullptr, key, provider, sublayer, sid, address, port, 9)) throw std::runtime_error("Missing readback accepted.");
        if (MatchesRuntimeFilter(&filter, key, provider, sublayer, sid, address, port, 10)) throw std::runtime_error("Stale filter identity accepted.");
        c[2].conditionValue.uint16++;
        if (ExactScope(&filter, key, provider, sublayer, sid, address, port)) throw std::runtime_error("Wrong port accepted.");
        c[2].conditionValue.uint16 = port;
        c[1].conditionValue.uint32++;
        if (ExactScope(&filter, key, provider, sublayer, sid, address, port)) throw std::runtime_error("Wrong address accepted.");
        c[1].conditionValue.uint32 = address;
        c[3].conditionValue.uint8 = IPPROTO_UDP;
        if (ExactScope(&filter, key, provider, sublayer, sid, address, port)) throw std::runtime_error("Wrong protocol accepted.");
        c[3].conditionValue.uint8 = IPPROTO_TCP;
        filter.action.type = FWP_ACTION_PERMIT;
        if (ExactScope(&filter, key, provider, sublayer, sid, address, port)) throw std::runtime_error("Permit action accepted.");
        filter.action.type = FWP_ACTION_BLOCK;
        c[3].fieldKey = c[2].fieldKey;
        if (ExactScope(&filter, key, provider, sublayer, sid, address, port)) throw std::runtime_error("Duplicate field accepted.");
        std::puts("PASS: private WFP scope synthetic self-test; no BFE, policy, profile or network operation performed.");
        result = 0;
    } catch (const std::exception& ex) { std::fprintf(stderr, "FAIL: %s\n", ex.what()); }
    FreeSid(sid);
    return result;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return SelfTest();
    wchar_t ack[64]{};
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    if (argc != 5 || std::wstring(argv[1]) != L"--install-private-disposable" ||
        std::wstring(argv[4]) != L"--acknowledge-dynamic-policy-is-not-containment" ||
        !GetEnvironmentVariableW(L"AEGIS_DISPOSABLE_VM_NETWORK_TEST", ack, ARRAYSIZE(ack)) ||
        std::wstring(ack) != L"I_UNDERSTAND" || system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64 || !IsAdministrator()) {
        std::fprintf(stderr, "Explicit disposable-VM acknowledgement, native Windows x64 and administrator privileges are required.\n");
        return 2;
    }
    IN_ADDR parsed{};
    if (InetPtonW(AF_INET, argv[2], &parsed) != 1 || !IsPrivateV4(ntohl(parsed.S_un.S_addr))) {
        std::fprintf(stderr, "Only an RFC1918 IPv4 destination is accepted.\n"); return 2;
    }
    wchar_t canonical[INET_ADDRSTRLEN]{};
    if (!InetNtopW(AF_INET, &parsed, canonical, ARRAYSIZE(canonical)) || std::wstring(canonical) != argv[2]) {
        std::fprintf(stderr, "Destination must be canonical IPv4.\n"); return 2;
    }
    wchar_t* end = nullptr;
    const unsigned long parsedPort = wcstoul(argv[3], &end, 10);
    if (!end || *end || end == argv[3] || parsedPort < 1024 || parsedPort > 65535) {
        std::fprintf(stderr, "Port must be 1024..65535.\n"); return 2;
    }
    HANDLE engine = nullptr, stop = nullptr;
    PSID sid = nullptr;
    bool profileCreated = false, transaction = false, committed = false, closed = false;
    int result = 1;
    std::wstring name;
    GUID providerKey{}, sublayerKey{}, filterKey{};
    try {
        const GUID run = NewGuid();
        providerKey = NewGuid(); sublayerKey = NewGuid(); filterKey = NewGuid();
        name = L"AegisPrivateWfpTest." + GuidText(run);
        const HRESULT hr = CreateAppContainerProfile(name.c_str(), name.c_str(), L"Disposable Aegis WFP test identity", nullptr, 0, &sid);
        if (FAILED(hr) || !sid) Check(FAILED(hr) ? static_cast<DWORD>(hr) : ERROR_INVALID_SID, "CreateAppContainerProfile");
        profileCreated = true;
        FWPM_SESSION0 session{}; session.flags = FWPM_SESSION_FLAG_DYNAMIC;
        Check(FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session, &engine), "FwpmEngineOpen0");
        Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0"); transaction = true;
        FWPM_PROVIDER0 provider{}; provider.providerKey = providerKey;
        provider.displayData.name = const_cast<wchar_t*>(L"Aegis disposable private endpoint test provider");
        Check(FwpmProviderAdd0(engine, &provider, nullptr), "FwpmProviderAdd0");
        FWPM_SUBLAYER0 sublayer{}; sublayer.subLayerKey = sublayerKey; sublayer.providerKey = const_cast<GUID*>(&providerKey);
        sublayer.displayData.name = const_cast<wchar_t*>(L"Aegis disposable private endpoint test sublayer"); sublayer.weight = 0x100;
        Check(FwpmSubLayerAdd0(engine, &sublayer, nullptr), "FwpmSubLayerAdd0");
        FWPM_FILTER_CONDITION0 conditions[4]{};
        conditions[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID; conditions[0].matchType = FWP_MATCH_EQUAL;
        conditions[0].conditionValue.type = FWP_SID; conditions[0].conditionValue.sid = static_cast<SID*>(sid);
        conditions[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; conditions[1].matchType = FWP_MATCH_EQUAL;
        conditions[1].conditionValue.type = FWP_UINT32; conditions[1].conditionValue.uint32 = ntohl(parsed.S_un.S_addr);
        conditions[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; conditions[2].matchType = FWP_MATCH_EQUAL;
        conditions[2].conditionValue.type = FWP_UINT16; conditions[2].conditionValue.uint16 = static_cast<UINT16>(parsedPort);
        conditions[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; conditions[3].matchType = FWP_MATCH_EQUAL;
        conditions[3].conditionValue.type = FWP_UINT8; conditions[3].conditionValue.uint8 = IPPROTO_TCP;
        FWPM_FILTER0 filter{}; filter.filterKey = filterKey; filter.providerKey = const_cast<GUID*>(&providerKey);
        filter.displayData.name = const_cast<wchar_t*>(L"Aegis disposable private IPv4 TCP test block");
        filter.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4; filter.subLayerKey = sublayerKey;
        filter.weight.type = FWP_EMPTY; filter.numFilterConditions = ARRAYSIZE(conditions);
        filter.filterCondition = conditions; filter.action.type = FWP_ACTION_BLOCK;
        if (!ExactScope(&filter, filterKey, providerKey, sublayerKey, sid, ntohl(parsed.S_un.S_addr), static_cast<UINT16>(parsedPort)))
            throw std::runtime_error("Refusing unexpected filter scope.");
        UINT64 runtimeFilterId = 0;
        Check(FwpmFilterAdd0(engine, &filter, nullptr, &runtimeFilterId), "FwpmFilterAdd0");
        Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0"); transaction = false; committed = true;
        FWPM_FILTER0* readback = nullptr;
        Check(FwpmFilterGetByKey0(engine, &filterKey, &readback), "FwpmFilterGetByKey0");
        const bool valid = MatchesRuntimeFilter(readback, filterKey, providerKey, sublayerKey, sid,
            ntohl(parsed.S_un.S_addr), static_cast<UINT16>(parsedPort), runtimeFilterId);
        if (readback) FwpmFreeMemory0(reinterpret_cast<void**>(&readback));
        if (!valid) throw std::runtime_error("Committed filter did not match the requested test scope.");
        const std::wstring eventName = L"Local\\AegisWfpTestStop." + GuidText(run);
        stop = CreateEventW(nullptr, TRUE, FALSE, eventName.c_str());
        if (!stop || GetLastError() == ERROR_ALREADY_EXISTS) throw std::runtime_error("Cannot create fresh stop event.");
        const std::wstring providerText = GuidText(providerKey), sublayerText = GuidText(sublayerKey), filterText = GuidText(filterKey);
        // This helper deliberately never publishes the generated profile name or
        // SID, and never launches a child. It cannot yet run a restricted-flow
        // test; adding one requires retained-child lifecycle integration first.
        std::printf("ACTIVE_UNVERIFIED: provider=%ls sublayer=%ls filter=%ls filterId=%llu stopEvent=%ls deadlineSeconds=120\n",
            providerText.c_str(), sublayerText.c_str(), filterText.c_str(),
            static_cast<unsigned long long>(runtimeFilterId), eventName.c_str());
        std::fflush(stdout);
        // Readback detects a missing, replaced or mutated filter while this
        // isolated helper waits. Polling cannot prove uninterrupted enforcement
        // between checks and never authorizes a browser or a protected process.
        const ULONGLONG deadline = GetTickCount64() + 120000;
        for (;;) {
            const ULONGLONG now = GetTickCount64();
            if (now >= deadline) throw std::runtime_error("Test controller did not signal stop within the bounded interval.");
            const ULONGLONG remaining = deadline - now;
            const DWORD wait = WaitForSingleObject(stop, static_cast<DWORD>(remaining < 1000 ? remaining : 1000));
            if (wait == WAIT_OBJECT_0) break;
            if (wait != WAIT_TIMEOUT) throw std::runtime_error("Test stop event wait failed.");
            FWPM_FILTER0* current = nullptr;
            const DWORD status = FwpmFilterGetByKey0(engine, &filterKey, &current);
            const bool matches = status == ERROR_SUCCESS && MatchesRuntimeFilter(current, filterKey, providerKey,
                sublayerKey, sid, ntohl(parsed.S_un.S_addr), static_cast<UINT16>(parsedPort), runtimeFilterId);
            if (current) FwpmFreeMemory0(reinterpret_cast<void**>(&current));
            if (!matches) {
                std::fprintf(stderr, "FAILED_OR_LOST: owned filter readback missing or mismatched (0x%08lX).\n", status);
                throw std::runtime_error("Test policy could not be revalidated.");
            }
        }
        std::puts("INCONCLUSIVE: no process attribution or WFP packet decision was observed.");
        result = 0;
    } catch (const std::exception& ex) { std::fprintf(stderr, "FAIL: %s\n", ex.what()); }
    if (engine && transaction) { const DWORD e = FwpmTransactionAbort0(engine); if (e) { std::fprintf(stderr, "FAIL: transaction abort 0x%08lX\n", e); result = 1; } }
    if (engine) { const DWORD e = FwpmEngineClose0(engine); if (e) { std::fprintf(stderr, "FAIL: engine close 0x%08lX\n", e); result = 1; } else closed = true; }
    if (committed && closed) {
        HANDLE reader = nullptr;
        const DWORD open = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &reader);
        if (open) { std::fprintf(stderr, "FAIL: independent cleanup reader 0x%08lX\n", open); result = 1; }
        else {
            FWPM_FILTER0* f = nullptr; FWPM_SUBLAYER0* s = nullptr; FWPM_PROVIDER0* p = nullptr;
            const DWORD fc = FwpmFilterGetByKey0(reader, &filterKey, &f);
            const DWORD sc = FwpmSubLayerGetByKey0(reader, &sublayerKey, &s);
            const DWORD pc = FwpmProviderGetByKey0(reader, &providerKey, &p);
            if (f) FwpmFreeMemory0(reinterpret_cast<void**>(&f));
            if (s) FwpmFreeMemory0(reinterpret_cast<void**>(&s));
            if (p) FwpmFreeMemory0(reinterpret_cast<void**>(&p));
            if (fc != static_cast<DWORD>(FWP_E_FILTER_NOT_FOUND) || sc != static_cast<DWORD>(FWP_E_SUBLAYER_NOT_FOUND) ||
                pc != static_cast<DWORD>(FWP_E_PROVIDER_NOT_FOUND)) {
                std::fprintf(stderr, "FAIL: owned WFP object cleanup not verified (filter=0x%08lX sublayer=0x%08lX provider=0x%08lX)\n", fc, sc, pc);
                result = 1;
            } else std::puts("OBSERVED: owned dynamic WFP objects absent from independent reader.");
            const DWORD e = FwpmEngineClose0(reader);
            if (e) { std::fprintf(stderr, "FAIL: cleanup reader close 0x%08lX\n", e); result = 1; }
        }
    }
    if (stop) CloseHandle(stop);
    if (profileCreated) { const HRESULT e = DeleteAppContainerProfile(name.c_str()); if (FAILED(e)) { std::fprintf(stderr, "FAIL: test profile removal 0x%08lX\n", static_cast<unsigned long>(e)); result = 1; } }
    if (sid) FreeSid(sid);
    return result;
}
