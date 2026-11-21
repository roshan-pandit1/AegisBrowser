// Disposable-VM-only WFP drop observer. No policy or engine option is changed.
// An observed WFP event is NOT process attribution or proof of containment.
#include <winsock2.h>
#include <windows.h>
#include <fwpmu.h>
#include <rpc.h>
#include <sddl.h>
#include <ws2tcpip.h>
#include <atomic>
#include <climits>
#include <cstring>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <string>

#pragma comment(lib, "fwpuclnt.lib")
#pragma comment(lib, "rpcrt4.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "advapi32.lib")

struct Expected
{
    UINT64 filterId;
    UINT16 layerId;
    UINT32 remoteAddress; // WFP UINT32 addresses use host byte order.
    UINT16 remotePort;
    const FWP_BYTE_BLOB* appId;
    PSID packageSid;
    UINT64 startFileTime;
    UINT64 endFileTime;
    std::atomic<unsigned> matches{0};
    std::atomic<unsigned> conflicts{0};
    std::atomic<UINT64> firstMatchFileTime{0};
};

static bool Matches(const FWPM_NET_EVENT5* event, const Expected& e)
{
    const UINT32 required = FWPM_NET_EVENT_FLAG_IP_VERSION_SET | FWPM_NET_EVENT_FLAG_IP_PROTOCOL_SET |
        FWPM_NET_EVENT_FLAG_REMOTE_ADDR_SET | FWPM_NET_EVENT_FLAG_REMOTE_PORT_SET |
        FWPM_NET_EVENT_FLAG_APP_ID_SET | FWPM_NET_EVENT_FLAG_PACKAGE_ID_SET;
    if (!event || event->type != FWPM_NET_EVENT_TYPE_CLASSIFY_DROP || !event->classifyDrop ||
        (event->header.flags & required) != required || event->header.ipVersion != FWP_IP_VERSION_V4 ||
        event->header.ipProtocol != IPPROTO_TCP || event->header.remoteAddrV4 != e.remoteAddress ||
        event->header.remotePort != e.remotePort || !event->header.packageSid || !e.packageSid ||
        !IsValidSid(event->header.packageSid) || !IsValidSid(e.packageSid) ||
        !EqualSid(event->header.packageSid, e.packageSid) || !e.appId ||
        !event->header.appId.data || !e.appId->data || event->header.appId.size != e.appId->size ||
        std::memcmp(event->header.appId.data, e.appId->data, e.appId->size) != 0) return false;
    ULARGE_INTEGER timestamp{};
    timestamp.LowPart = event->header.timeStamp.dwLowDateTime;
    timestamp.HighPart = event->header.timeStamp.dwHighDateTime;
    if (timestamp.QuadPart < e.startFileTime || timestamp.QuadPart > e.endFileTime) return false;
    // FWPM_NET_EVENT_CLASSIFY_DROP2::msFwpDirection uses FWP_DIRECTION_OUT.
    return event->classifyDrop->filterId == e.filterId && event->classifyDrop->layerId == e.layerId &&
        event->classifyDrop->msFwpDirection == 0x00003901u;
}

static void CALLBACK Observe(void* context, const FWPM_NET_EVENT5* event)
{
    auto* expected = static_cast<Expected*>(context);
    if (Matches(event, *expected)) {
        ULARGE_INTEGER timestamp{};
        timestamp.LowPart = event->header.timeStamp.dwLowDateTime;
        timestamp.HighPart = event->header.timeStamp.dwHighDateTime;
        UINT64 empty = 0;
        expected->firstMatchFileTime.compare_exchange_strong(empty, timestamp.QuadPart);
        ++expected->matches;
    }
    else ++expected->conflicts;
}

static bool ExactFilter(const FWPM_FILTER0* filter, const GUID& key, const Expected& expected);

static int SelfTest()
{
    BYTE app[] = { 1, 2, 3, 4 };
    FWP_BYTE_BLOB blob{ sizeof(app), app };
    PSID sid = nullptr;
    if (!ConvertStringSidToSidW(L"S-1-15-2-123456", &sid)) return 1;
    Expected expected{ 42, 7, 0x0A010203u, 45080, &blob, sid, 10, 20 };
    FWPM_NET_EVENT_CLASSIFY_DROP2 drop{};
    drop.filterId = 42; drop.layerId = 7; drop.msFwpDirection = 0x00003901u;
    FWPM_NET_EVENT5 event{};
    event.type = FWPM_NET_EVENT_TYPE_CLASSIFY_DROP;
    event.classifyDrop = &drop;
    event.header.flags = FWPM_NET_EVENT_FLAG_IP_VERSION_SET | FWPM_NET_EVENT_FLAG_IP_PROTOCOL_SET |
        FWPM_NET_EVENT_FLAG_REMOTE_ADDR_SET | FWPM_NET_EVENT_FLAG_REMOTE_PORT_SET |
        FWPM_NET_EVENT_FLAG_APP_ID_SET | FWPM_NET_EVENT_FLAG_PACKAGE_ID_SET;
    event.header.ipVersion = FWP_IP_VERSION_V4; event.header.ipProtocol = IPPROTO_TCP;
    event.header.remoteAddrV4 = expected.remoteAddress; event.header.remotePort = expected.remotePort;
    event.header.timeStamp.dwLowDateTime = 15;
    event.header.appId = blob; event.header.packageSid = sid;
    bool valid = Matches(&event, expected);
    Observe(&expected, &event); Observe(&expected, &event);
    bool duplicateRecorded = expected.matches.load() == 2 && expected.firstMatchFileTime.load() == 15;
    event.header.flags &= ~FWPM_NET_EVENT_FLAG_PACKAGE_ID_SET;
    bool rejectedMissingFlag = !Matches(&event, expected);
    event.header.flags |= FWPM_NET_EVENT_FLAG_PACKAGE_ID_SET;
    drop.filterId++;
    bool rejectedWrongFilter = !Matches(&event, expected);
    drop.filterId--;
    event.header.remotePort++;
    bool rejectedWrongPort = !Matches(&event, expected);
    event.header.remotePort--;
    event.header.timeStamp.dwLowDateTime = 21;
    bool rejectedWrongTime = !Matches(&event, expected);
    const GUID key{ 1, 2, 3, { 4, 5, 6, 7, 8, 9, 10, 11 } };
    const GUID provider{ 2, 2, 3, { 4, 5, 6, 7, 8, 9, 10, 11 } };
    FWPM_FILTER_CONDITION0 c[4]{};
    c[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID; c[0].matchType = FWP_MATCH_EQUAL;
    c[0].conditionValue.type = FWP_SID; c[0].conditionValue.sid = static_cast<SID*>(sid);
    c[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c[1].matchType = FWP_MATCH_EQUAL;
    c[1].conditionValue.type = FWP_UINT32; c[1].conditionValue.uint32 = expected.remoteAddress;
    c[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c[2].matchType = FWP_MATCH_EQUAL;
    c[2].conditionValue.type = FWP_UINT16; c[2].conditionValue.uint16 = expected.remotePort;
    c[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; c[3].matchType = FWP_MATCH_EQUAL;
    c[3].conditionValue.type = FWP_UINT8; c[3].conditionValue.uint8 = IPPROTO_TCP;
    FWPM_FILTER0 filter{}; filter.filterKey = key; filter.providerKey = const_cast<GUID*>(&provider);
    filter.filterId = 42; filter.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4; filter.action.type = FWP_ACTION_BLOCK;
    filter.numFilterConditions = ARRAYSIZE(c); filter.filterCondition = c;
    bool validFilter = ExactFilter(&filter, key, expected);
    c[2].conditionValue.uint16++;
    bool rejectedWrongScope = !ExactFilter(&filter, key, expected);
    LocalFree(sid);
    if (!(valid && duplicateRecorded && rejectedMissingFlag && rejectedWrongFilter && rejectedWrongPort &&
        rejectedWrongTime && validFilter && rejectedWrongScope)) return 1;
    std::puts("PASS: synthetic WFP decision matching; no engine, subscription, policy or network operations.");
    return 0;
}

static bool ParseUnsigned(const wchar_t* text, unsigned long long maximum, unsigned long long& value)
{
    if (!text || !*text) return false;
    value = 0;
    for (const wchar_t* p = text; *p; ++p) {
        if (*p < L'0' || *p > L'9') return false;
        const unsigned digit = static_cast<unsigned>(*p - L'0');
        if (value > (maximum - digit) / 10) return false;
        value = value * 10 + digit;
    }
    return value > 0;
}

static bool IsPrivateV4(UINT32 address)
{
    return (address >> 24) == 10 || (address >> 20) == 0xAC1 || (address >> 16) == 0xC0A8;
}

static bool IsAdministrator()
{
    SID_IDENTIFIER_AUTHORITY nt = SECURITY_NT_AUTHORITY;
    PSID admin = nullptr;
    if (!AllocateAndInitializeSid(&nt, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
        0, 0, 0, 0, 0, 0, &admin)) return false;
    BOOL member = FALSE;
    const BOOL ok = CheckTokenMembership(nullptr, admin, &member);
    FreeSid(admin);
    return ok && member;
}

static bool IsRunOwnedExecutable(const wchar_t* path, const wchar_t* runText)
{
    // Accept only the exact disposable fixture subtree for this run. Reject
    // reparse points at every path level; no implicit canonicalization or UNC.
    std::wstring full = path, run;
    if (full.find(L'/') != std::wstring::npos || full.find(L':', 2) != std::wstring::npos) return false;
    wchar_t canonical[MAX_PATH]{};
    DWORD length = GetFullPathNameW(path, ARRAYSIZE(canonical), canonical, nullptr);
    if (!length || length >= ARRAYSIZE(canonical) || _wcsicmp(canonical, path) != 0) return false;
    for (const wchar_t* p = runText; *p; ++p) if (*p != L'-') run.push_back(static_cast<wchar_t>(towlower(*p)));
    if (run.size() != 32) return false;
    const std::wstring suffix = L"\\aegis-private-" + run + L"\\fixture\\AegisBrowser.BrowserTests.exe";
    if (full.size() < suffix.size() || _wcsicmp(full.c_str() + full.size() - suffix.size(), suffix.c_str()) != 0) return false;
    for (size_t end = full.find(L'\\', 3); ; end = full.find(L'\\', end + 1)) {
        const std::wstring current = end == std::wstring::npos ? full : full.substr(0, end);
        DWORD attributes = GetFileAttributesW(current.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        if (end == std::wstring::npos) return (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) return false;
    }
}

static bool ExactFilter(const FWPM_FILTER0* filter, const GUID& key, const Expected& e)
{
    if (!filter || !IsEqualGUID(filter->filterKey, key) || filter->filterId != e.filterId ||
        !IsEqualGUID(filter->layerKey, FWPM_LAYER_ALE_AUTH_CONNECT_V4) ||
        filter->action.type != FWP_ACTION_BLOCK || !filter->providerKey ||
        (filter->flags & ~static_cast<UINT32>(FWPM_FILTER_FLAG_INDEXED)) != 0 ||
        filter->numFilterConditions != 4 || !filter->filterCondition) return false;
    bool sid = false, address = false, port = false, protocol = false;
    for (UINT32 i = 0; i < filter->numFilterConditions; ++i) {
        const auto& c = filter->filterCondition[i];
        if (c.matchType != FWP_MATCH_EQUAL) return false;
        if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_ALE_PACKAGE_ID))
            sid = !sid && c.conditionValue.type == FWP_SID && c.conditionValue.sid && IsValidSid(c.conditionValue.sid) && EqualSid(c.conditionValue.sid, e.packageSid);
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_ADDRESS))
            address = !address && c.conditionValue.type == FWP_UINT32 && c.conditionValue.uint32 == e.remoteAddress;
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_PORT))
            port = !port && c.conditionValue.type == FWP_UINT16 && c.conditionValue.uint16 == e.remotePort;
        else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_PROTOCOL))
            protocol = !protocol && c.conditionValue.type == FWP_UINT8 && c.conditionValue.uint8 == IPPROTO_TCP;
        else return false;
    }
    return sid && address && port && protocol;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return SelfTest();
    wchar_t optIn[32]{};
    SYSTEM_INFO system{}; GetNativeSystemInfo(&system);
    if (argc != 11 || std::wstring(argv[1]) != L"--observe" ||
        std::wstring(argv[10]) != L"--acknowledge-disposable-isolated-vm" ||
        GetEnvironmentVariableW(L"AEGIS_DISPOSABLE_VM_NETWORK_TEST", optIn, ARRAYSIZE(optIn)) == 0 ||
        std::wstring(optIn) != L"I_UNDERSTAND" || system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64 ||
        !IsAdministrator()) {
        std::fputs("BLOCKED: explicit isolated disposable VM authorization, native Windows x64 and administrator rights are required.\n", stderr);
        return 2;
    }
    // Arguments: run GUID, fixed disposable browser-test executable, package SID,
    // private IPv4, TCP port, runtime filter ID, filter GUID, duration seconds.
    UUID run{}, filterKey{}; RPC_STATUS uuidStatus = RPC_S_OK, filterStatus = RPC_S_OK;
    if (UuidFromStringW(reinterpret_cast<RPC_WSTR>(argv[2]), &run) != RPC_S_OK ||
        UuidIsNil(&run, &uuidStatus) || uuidStatus != RPC_S_OK ||
        UuidFromStringW(reinterpret_cast<RPC_WSTR>(argv[8]), &filterKey) != RPC_S_OK ||
        UuidIsNil(&filterKey, &filterStatus) || filterStatus != RPC_S_OK ||
        wcslen(argv[3]) < 4 || !iswalpha(argv[3][0]) ||
        argv[3][1] != L':' || (argv[3][2] != L'\\' && argv[3][2] != L'/')) return 2;
    if (!IsRunOwnedExecutable(argv[3], argv[2])) return 2;
    PSID sid = nullptr;
    if (!ConvertStringSidToSidW(argv[4], &sid)) return 2;
    const SID_IDENTIFIER_AUTHORITY packageAuthority = SECURITY_APP_PACKAGE_AUTHORITY;
    bool packageValid = IsValidSid(sid) && *GetSidSubAuthorityCount(sid) > 1 &&
        std::memcmp(GetSidIdentifierAuthority(sid), &packageAuthority, sizeof(packageAuthority)) == 0 &&
        *GetSidSubAuthority(sid, 0) == SECURITY_APP_PACKAGE_BASE_RID;
    IN_ADDR parsed{};
    unsigned long long port = 0, filterId = 0, seconds = 0;
    if (!packageValid || InetPtonW(AF_INET, argv[5], &parsed) != 1 || !IsPrivateV4(ntohl(parsed.S_un.S_addr)) ||
        !ParseUnsigned(argv[6], 65535, port) || !ParseUnsigned(argv[7], ULLONG_MAX, filterId) ||
        !ParseUnsigned(argv[9], 30, seconds)) { LocalFree(sid); return 2; }

    HANDLE engine = nullptr, subscriptionHandle = nullptr;
    FWP_BYTE_BLOB* appId = nullptr;
    FWP_VALUE0* collection = nullptr;
    FWPM_LAYER0* layer = nullptr;
    FWPM_FILTER0* installedFilter = nullptr;
    DWORD result = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &engine);
    if (result == ERROR_SUCCESS) result = FwpmEngineGetOption0(engine, FWPM_ENGINE_COLLECT_NET_EVENTS, &collection);
    if (result == ERROR_SUCCESS && (!collection || collection->type != FWP_UINT32 || collection->uint32 != 1)) result = ERROR_NOT_READY;
    if (result == ERROR_SUCCESS) result = FwpmGetAppIdFromFileName0(argv[3], &appId);
    if (result == ERROR_SUCCESS) result = FwpmLayerGetByKey0(engine, &FWPM_LAYER_ALE_AUTH_CONNECT_V4, &layer);
    if (result == ERROR_SUCCESS) result = FwpmFilterGetById0(engine, filterId, &installedFilter);
    if (result != ERROR_SUCCESS || !appId || !layer || !installedFilter) {
        std::fprintf(stderr, "INCONCLUSIVE: WFP observer prerequisites unavailable (0x%08lX). No engine options changed.\n", result);
        if (layer) FwpmFreeMemory0(reinterpret_cast<void**>(&layer));
        if (installedFilter) FwpmFreeMemory0(reinterpret_cast<void**>(&installedFilter));
        if (collection) FwpmFreeMemory0(reinterpret_cast<void**>(&collection));
        if (appId) FwpmFreeMemory0(reinterpret_cast<void**>(&appId));
        if (engine) FwpmEngineClose0(engine);
        LocalFree(sid); return 2;
    }
    FILETIME now{}; GetSystemTimeAsFileTime(&now);
    ULARGE_INTEGER begin{}; begin.LowPart = now.dwLowDateTime; begin.HighPart = now.dwHighDateTime;
    const UINT64 endTime = begin.QuadPart + (seconds + 1) * 10000000ULL;
    auto* expected = new Expected{ filterId, layer->layerId, ntohl(parsed.S_un.S_addr), static_cast<UINT16>(port), appId, sid, begin.QuadPart, endTime };
    if (!ExactFilter(installedFilter, filterKey, *expected)) {
        std::fputs("INCONCLUSIVE: expected WFP filter identity or exact scope did not match.\n", stderr);
        FwpmFreeMemory0(reinterpret_cast<void**>(&installedFilter));
        FwpmFreeMemory0(reinterpret_cast<void**>(&layer));
        FwpmFreeMemory0(reinterpret_cast<void**>(&collection));
        FwpmFreeMemory0(reinterpret_cast<void**>(&appId));
        FwpmEngineClose0(engine); LocalFree(sid); delete expected; return 2;
    }
    FWPM_FILTER_CONDITION0 conditions[5]{};
    conditions[0].fieldKey = FWPM_CONDITION_ALE_APP_ID; conditions[0].matchType = FWP_MATCH_EQUAL;
    conditions[0].conditionValue.type = FWP_BYTE_BLOB_TYPE; conditions[0].conditionValue.byteBlob = appId;
    conditions[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; conditions[1].matchType = FWP_MATCH_EQUAL;
    conditions[1].conditionValue.type = FWP_UINT32; conditions[1].conditionValue.uint32 = expected->remoteAddress;
    conditions[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; conditions[2].matchType = FWP_MATCH_EQUAL;
    conditions[2].conditionValue.type = FWP_UINT16; conditions[2].conditionValue.uint16 = expected->remotePort;
    conditions[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; conditions[3].matchType = FWP_MATCH_EQUAL;
    conditions[3].conditionValue.type = FWP_UINT8; conditions[3].conditionValue.uint8 = IPPROTO_TCP;
    conditions[4].fieldKey = FWPM_CONDITION_NET_EVENT_TYPE; conditions[4].matchType = FWP_MATCH_EQUAL;
    conditions[4].conditionValue.type = FWP_UINT32; conditions[4].conditionValue.uint32 = FWPM_NET_EVENT_TYPE_CLASSIFY_DROP;
    FWPM_NET_EVENT_ENUM_TEMPLATE0 filter{};
    ULARGE_INTEGER until{}; until.QuadPart = endTime;
    filter.startTime = now;
    filter.endTime.dwLowDateTime = until.LowPart; filter.endTime.dwHighDateTime = until.HighPart;
    filter.numFilterConditions = ARRAYSIZE(conditions); filter.filterCondition = conditions;
    FWPM_NET_EVENT_SUBSCRIPTION0 subscription{}; subscription.enumTemplate = &filter;
    result = FwpmNetEventSubscribe4(engine, &subscription, Observe, expected, &subscriptionHandle);
    bool callbacksStopped = result != ERROR_SUCCESS;
    if (result == ERROR_SUCCESS) {
        std::printf("READY: scoped WFP decision observation started; no policy changed.\n"); std::fflush(stdout);
        Sleep(static_cast<DWORD>(seconds * 1000));
        // Unsubscribe waits for running callbacks. Do not hold callback locks here.
        result = FwpmNetEventUnsubscribe0(engine, subscriptionHandle);
        callbacksStopped = result == ERROR_SUCCESS;
    }
    const DWORD closeResult = engine ? FwpmEngineClose0(engine) : ERROR_INVALID_HANDLE;
    engine = nullptr;
    std::printf("{\"status\":\"INCONCLUSIVE\",\"runId\":\"%ls\",\"remoteAddress\":\"%ls\",\"remotePort\":%u,\"protocol\":\"TCP\",\"matchingDropEvents\":%u,\"conflictingEvents\":%u,\"captureStartFileTime\":%llu,\"captureEndFileTime\":%llu,\"firstMatchFileTime\":%llu,\"filterId\":%llu,\"filterKey\":\"%ls\",\"processAttribution\":\"NOT_ESTABLISHED\",\"osResult\":%lu,\"engineCloseResult\":%lu}\n",
        argv[2], argv[5], static_cast<unsigned>(port), expected->matches.load(), expected->conflicts.load(),
        expected->startFileTime, expected->endFileTime, expected->firstMatchFileTime.load(), filterId, argv[8], result, closeResult);
    if (installedFilter) FwpmFreeMemory0(reinterpret_cast<void**>(&installedFilter));
    if (layer) FwpmFreeMemory0(reinterpret_cast<void**>(&layer));
    if (collection) FwpmFreeMemory0(reinterpret_cast<void**>(&collection));
    if (callbacksStopped) {
        if (appId) FwpmFreeMemory0(reinterpret_cast<void**>(&appId));
        LocalFree(sid);
        delete expected;
    } else {
        // Unsubscribe failure does not establish callback quiescence. Retain
        // callback context until process termination instead of freeing it.
        std::fputs("INCONCLUSIVE: callback teardown was not confirmed.\n", stderr);
    }
    return 2; // A WFP event alone never establishes a PID or containment.
}
