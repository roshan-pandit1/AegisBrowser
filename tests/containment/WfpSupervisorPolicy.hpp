#pragma once

// Isolated, test-owned persistent WFP objects only. Persistence prevents a
// controller-session close from intentionally deleting the rule while a child
// is alive. It does NOT establish uninterrupted enforcement across BFE or OS
// restart. Never use this type for production browsing.
#include <winsock2.h>
#include <windows.h>
#include <fwpmu.h>
#include <winerror.h>
#include <stdexcept>

namespace AegisTestPolicy {

inline void Check(DWORD error, const char* message)
{
    if (error != ERROR_SUCCESS) throw std::runtime_error(message);
}

struct Keys { GUID provider; GUID sublayer; GUID filter; };

// Scope comparison is deliberately exact; no allow rule or wildcard is
// accepted. A valid filter is still not evidence that it blocked a packet.
inline bool ExactScope(const FWPM_FILTER0* f, const Keys& keys, PSID sid,
                       UINT32 remoteAddress, UINT16 remotePort, UINT64 runtimeId)
{
    if (!f || !sid || !IsValidSid(sid) || !f->providerKey || !f->filterCondition ||
        !IsEqualGUID(f->filterKey, keys.filter) || !IsEqualGUID(*f->providerKey, keys.provider) ||
        !IsEqualGUID(f->subLayerKey, keys.sublayer) || !IsEqualGUID(f->layerKey, FWPM_LAYER_ALE_AUTH_CONNECT_V4) ||
        f->filterId != runtimeId || f->action.type != FWP_ACTION_BLOCK || f->numFilterConditions != 4 ||
        !(f->flags & FWPM_FILTER_FLAG_PERSISTENT) ||
        (f->flags & ~(FWPM_FILTER_FLAG_PERSISTENT | FWPM_FILTER_FLAG_INDEXED)) != 0) return false;
    bool identity = false, address = false, port = false, protocol = false;
    for (UINT32 i = 0; i < f->numFilterConditions; ++i) {
        const auto& c = f->filterCondition[i];
        if (c.matchType != FWP_MATCH_EQUAL) return false;
        if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_ALE_PACKAGE_ID)) {
            if (identity || c.conditionValue.type != FWP_SID || !c.conditionValue.sid ||
                !IsValidSid(c.conditionValue.sid) || !EqualSid(c.conditionValue.sid, sid)) return false;
            identity = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_ADDRESS)) {
            if (address || c.conditionValue.type != FWP_UINT32 || c.conditionValue.uint32 != remoteAddress) return false;
            address = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_REMOTE_PORT)) {
            if (port || c.conditionValue.type != FWP_UINT16 || c.conditionValue.uint16 != remotePort) return false;
            port = true;
        } else if (IsEqualGUID(c.fieldKey, FWPM_CONDITION_IP_PROTOCOL)) {
            if (protocol || c.conditionValue.type != FWP_UINT8 || c.conditionValue.uint8 != IPPROTO_TCP) return false;
            protocol = true;
        } else return false;
    }
    return identity && address && port && protocol;
}

inline bool ExactProvider(const FWPM_PROVIDER0* provider, const Keys& keys)
{
    return provider && IsEqualGUID(provider->providerKey, keys.provider) &&
        provider->flags == FWPM_PROVIDER_FLAG_PERSISTENT && provider->serviceName == nullptr;
}

inline bool ExactSublayer(const FWPM_SUBLAYER0* sublayer, const Keys& keys)
{
    return sublayer && sublayer->providerKey && IsEqualGUID(sublayer->subLayerKey, keys.sublayer) &&
        IsEqualGUID(*sublayer->providerKey, keys.provider) &&
        sublayer->flags == FWPM_SUBLAYER_FLAG_PERSISTENT && sublayer->weight == 0x100;
}

inline bool ReadbackMatches(HANDLE engine, const Keys& keys, PSID sid,
                            UINT32 remoteAddress, UINT16 remotePort, UINT64 runtimeId)
{
    if (!engine || !runtimeId) return false;
    FWPM_FILTER0* filter = nullptr;
    const DWORD result = FwpmFilterGetByKey0(engine, &keys.filter, &filter);
    bool exact = result == ERROR_SUCCESS && ExactScope(filter, keys, sid, remoteAddress, remotePort, runtimeId);
    if (filter) FwpmFreeMemory0(reinterpret_cast<void**>(&filter));
    FWPM_PROVIDER0* provider = nullptr;
    const DWORD providerResult = FwpmProviderGetByKey0(engine, &keys.provider, &provider);
    exact = exact && providerResult == ERROR_SUCCESS && ExactProvider(provider, keys);
    if (provider) FwpmFreeMemory0(reinterpret_cast<void**>(&provider));
    FWPM_SUBLAYER0* sublayer = nullptr;
    const DWORD sublayerResult = FwpmSubLayerGetByKey0(engine, &keys.sublayer, &sublayer);
    exact = exact && sublayerResult == ERROR_SUCCESS && ExactSublayer(sublayer, keys);
    if (sublayer) FwpmFreeMemory0(reinterpret_cast<void**>(&sublayer));
    return exact;
}

class PersistentTestPolicy {
    HANDLE engine_ = nullptr;
    bool transaction_ = false;
    bool committed_ = false;
    Keys keys_{};
    UINT64 runtimeId_ = 0;
public:
    PersistentTestPolicy() = default;
    PersistentTestPolicy(const PersistentTestPolicy&) = delete;
    PersistentTestPolicy& operator=(const PersistentTestPolicy&) = delete;
    ~PersistentTestPolicy()
    {
        // Never delete persistent objects from a destructor. An abnormal exit
        // leaves their pre-recorded identifiers for disposable-VM recovery.
        if (engine_ && transaction_) FwpmTransactionAbort0(engine_);
        if (engine_) FwpmEngineClose0(engine_);
    }

    HANDLE Engine() const { return engine_; }
    UINT64 RuntimeId() const { return runtimeId_; }
    bool Committed() const { return committed_; }

    void Install(const Keys& keys, PSID sid, UINT32 remoteAddress, UINT16 remotePort)
    {
        if (engine_ || !sid || !IsValidSid(sid) || !remotePort) throw std::runtime_error("Invalid test policy parameters.");
        keys_ = keys;
        Check(FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &engine_), "WFP engine unavailable.");
        Check(FwpmTransactionBegin0(engine_, 0), "WFP transaction unavailable.");
        transaction_ = true;
        FWPM_PROVIDER0 provider{};
        provider.providerKey = keys.provider;
        provider.flags = FWPM_PROVIDER_FLAG_PERSISTENT;
        provider.displayData.name = const_cast<wchar_t*>(L"Aegis disposable supervised test provider");
        Check(FwpmProviderAdd0(engine_, &provider, nullptr), "WFP provider creation failed.");
        FWPM_SUBLAYER0 sublayer{};
        sublayer.subLayerKey = keys.sublayer;
        sublayer.providerKey = &keys_.provider;
        sublayer.flags = FWPM_SUBLAYER_FLAG_PERSISTENT;
        sublayer.displayData.name = const_cast<wchar_t*>(L"Aegis disposable supervised test sublayer");
        sublayer.weight = 0x100;
        Check(FwpmSubLayerAdd0(engine_, &sublayer, nullptr), "WFP sublayer creation failed.");
        FWPM_FILTER_CONDITION0 c[4]{};
        c[0].fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID; c[0].matchType = FWP_MATCH_EQUAL;
        c[0].conditionValue.type = FWP_SID; c[0].conditionValue.sid = static_cast<SID*>(sid);
        c[1].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS; c[1].matchType = FWP_MATCH_EQUAL;
        c[1].conditionValue.type = FWP_UINT32; c[1].conditionValue.uint32 = remoteAddress;
        c[2].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT; c[2].matchType = FWP_MATCH_EQUAL;
        c[2].conditionValue.type = FWP_UINT16; c[2].conditionValue.uint16 = remotePort;
        c[3].fieldKey = FWPM_CONDITION_IP_PROTOCOL; c[3].matchType = FWP_MATCH_EQUAL;
        c[3].conditionValue.type = FWP_UINT8; c[3].conditionValue.uint8 = IPPROTO_TCP;
        FWPM_FILTER0 filter{};
        filter.filterKey = keys.filter;
        filter.providerKey = &keys_.provider;
        filter.flags = FWPM_FILTER_FLAG_PERSISTENT;
        filter.displayData.name = const_cast<wchar_t*>(L"Aegis disposable supervised test TCP block");
        filter.layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4;
        filter.subLayerKey = keys.sublayer;
        filter.weight.type = FWP_EMPTY;
        filter.action.type = FWP_ACTION_BLOCK;
        filter.numFilterConditions = ARRAYSIZE(c);
        filter.filterCondition = c;
        Check(FwpmFilterAdd0(engine_, &filter, nullptr, &runtimeId_), "WFP filter creation failed.");
        Check(FwpmTransactionCommit0(engine_), "WFP transaction commit failed.");
        transaction_ = false;
        committed_ = true;
        Revalidate(sid, remoteAddress, remotePort);
    }

    void Revalidate(PSID sid, UINT32 remoteAddress, UINT16 remotePort)
    {
        if (!engine_ || !committed_) throw std::runtime_error("Test policy is not committed.");
        if (!ReadbackMatches(engine_, keys_, sid, remoteAddress, remotePort, runtimeId_))
            throw std::runtime_error("Test policy missing, disabled or mismatched.");
    }

    // Only delete test-owned objects after the retained child HANDLE is
    // signaled. Any cleanup failure leaves identifiers available for manual
    // recovery; no unrelated objects are deleted.
    void DeleteAfterChildExit(HANDLE retainedChild, HANDLE retainedJob, PSID sid, UINT32 remoteAddress, UINT16 remotePort)
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if (!engine_ || !committed_ || !retainedChild || !retainedJob ||
            WaitForSingleObject(retainedChild, 0) != WAIT_OBJECT_0 ||
            !QueryInformationJobObject(retainedJob, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) ||
            accounting.ActiveProcesses != 0)
            throw std::runtime_error("Refusing policy cleanup before confirmed child exit.");
        Check(FwpmTransactionBegin0(engine_, 0), "Cannot start test policy cleanup.");
        transaction_ = true;
        // Recheck ownership/scope inside the exclusive transaction before
        // deletion; a replacement is never treated as our test policy.
        Revalidate(sid, remoteAddress, remotePort);
        Check(FwpmFilterDeleteByKey0(engine_, &keys_.filter), "Cannot remove owned test filter.");
        Check(FwpmSubLayerDeleteByKey0(engine_, &keys_.sublayer), "Cannot remove owned test sublayer.");
        Check(FwpmProviderDeleteByKey0(engine_, &keys_.provider), "Cannot remove owned test provider.");
        Check(FwpmTransactionCommit0(engine_), "Cannot commit test policy cleanup.");
        transaction_ = false;
        committed_ = false;
    }

    bool VerifyAbsentIndependently() const
    {
        HANDLE reader = nullptr;
        if (FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, nullptr, &reader) != ERROR_SUCCESS) return false;
        FWPM_FILTER0* filter = nullptr; FWPM_SUBLAYER0* sublayer = nullptr; FWPM_PROVIDER0* provider = nullptr;
        const DWORD f = FwpmFilterGetByKey0(reader, &keys_.filter, &filter);
        const DWORD s = FwpmSubLayerGetByKey0(reader, &keys_.sublayer, &sublayer);
        const DWORD p = FwpmProviderGetByKey0(reader, &keys_.provider, &provider);
        if (filter) FwpmFreeMemory0(reinterpret_cast<void**>(&filter));
        if (sublayer) FwpmFreeMemory0(reinterpret_cast<void**>(&sublayer));
        if (provider) FwpmFreeMemory0(reinterpret_cast<void**>(&provider));
        const DWORD close = FwpmEngineClose0(reader);
        return f == static_cast<DWORD>(FWP_E_FILTER_NOT_FOUND) && s == static_cast<DWORD>(FWP_E_SUBLAYER_NOT_FOUND) &&
            p == static_cast<DWORD>(FWP_E_PROVIDER_NOT_FOUND) && close == ERROR_SUCCESS;
    }
};
} // namespace AegisTestPolicy
