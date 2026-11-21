#pragma once
// Test-only suspended-process primitives. No WFP, CEF, or network operations.
// A live Child is never resumed or silently closed by these helpers.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>
#include <cstdint>
#include <cwchar>
#include <initializer_list>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>
#include <utility>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ws2_32.lib")

namespace AegisTestProcess {
inline void Require(bool value, const char* what) { if (!value) throw std::runtime_error(what); }
inline void Close(HANDLE& h) { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); h = nullptr; }

struct VerifiedImage {
    std::wstring path;
    HANDLE file = nullptr;
    std::vector<HANDLE> directories;
    std::vector<unsigned char> digest;
    VerifiedImage() = default;
    VerifiedImage(const VerifiedImage&) = delete;
    VerifiedImage& operator=(const VerifiedImage&) = delete;
    VerifiedImage(VerifiedImage&& other) noexcept : path(std::move(other.path)), file(other.file), directories(std::move(other.directories)), digest(std::move(other.digest)) { other.file = nullptr; other.directories.clear(); }
    ~VerifiedImage() { Close(file); for (auto& h : directories) Close(h); }
};

inline bool IsRegularNonReparse(HANDLE h, bool directory) {
    FILE_ATTRIBUTE_TAG_INFO tag{};
    if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, &tag, sizeof(tag))) return false;
    return !(tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) &&
        (((tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory);
}

inline std::vector<unsigned char> Sha256(HANDLE file) {
    BCRYPT_ALG_HANDLE alg = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD length = 0, written = 0; std::vector<unsigned char> object, digest(32);
    auto cleanup = [&]() { if (hash) BCryptDestroyHash(hash); if (alg) BCryptCloseAlgorithmProvider(alg, 0); };
    try {
        Require(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "SHA-256 provider unavailable");
        Require(BCryptGetProperty(alg, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&length), sizeof(length), &written, 0) >= 0 &&
            written == sizeof(length) && length > 0 && length <= 1024 * 1024, "SHA-256 object size invalid");
        object.resize(length);
        Require(BCryptCreateHash(alg, &hash, object.data(), length, nullptr, 0, 0) >= 0, "SHA-256 initialization failed");
        LARGE_INTEGER size{}; Require(GetFileSizeEx(file, &size) && size.QuadPart>0 && size.QuadPart<=256LL*1024*1024, "synthetic image size outside allowed bound");
        LARGE_INTEGER start{}; Require(SetFilePointerEx(file, start, nullptr, FILE_BEGIN) != FALSE, "image seek failed");
        unsigned char buffer[65536]; LONGLONG total=0;
        for (;;) { DWORD count = 0; Require(ReadFile(file, buffer, sizeof(buffer), &count, nullptr) != FALSE, "image read failed");
            if (!count) break; total+=count; Require(total<=size.QuadPart,"synthetic image changed during hashing");
            Require(BCryptHashData(hash, buffer, count, 0) >= 0, "image hash failed"); }
        Require(total==size.QuadPart, "synthetic image changed during hashing");
        Require(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0, "image hash finalization failed");
        Require(SetFilePointerEx(file, start, nullptr, FILE_BEGIN) != FALSE, "image rewind failed");
        cleanup(); return digest;
    } catch (...) { cleanup(); throw; }
}

inline VerifiedImage VerifyAndHoldSelf(const std::wstring& expectedSha256) {
    Require(expectedSha256.size() == 64, "reviewed SHA-256 must contain 64 hexadecimal digits");
    std::vector<unsigned char> expected; expected.reserve(32);
    auto hex = [](wchar_t c) -> int { if (c >= L'0' && c <= L'9') return c-L'0'; if (c >= L'a' && c <= L'f') return c-L'a'+10; if (c >= L'A' && c <= L'F') return c-L'A'+10; return -1; };
    for (size_t i=0; i<64; i+=2) { int a=hex(expectedSha256[i]), b=hex(expectedSha256[i+1]); Require(a>=0 && b>=0, "invalid reviewed SHA-256"); expected.push_back(static_cast<unsigned char>(a*16+b)); }
    wchar_t path[32768]{}; DWORD length = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    Require(length > 3 && length < std::size(path) && path[1] == L':' && path[2] == L'\\', "unsupported image path");
    VerifiedImage result; result.path.assign(path, length);
    const auto last = result.path.find_last_of(L'\\');
    Require(last != std::wstring::npos && _wcsicmp(result.path.c_str()+last+1, L"WfpProcessSupervisor.exe") == 0, "unexpected synthetic executable");
    Require(result.path.find(L'/', 0) == std::wstring::npos && result.path.find(L':', 2) == std::wstring::npos, "unsupported image path");
    // Hold every ancestor without delete sharing. Reparse points are rejected on
    // the opened handles, not just by a prior pathname attribute query.
    for (size_t pos=2; pos<last; ) {
        size_t next = result.path.find(L'\\', pos+1); if (next == std::wstring::npos || next > last) break;
        std::wstring directory = result.path.substr(0, next); if (directory.size()==2) directory += L'\\';
        HANDLE h = CreateFileW(directory.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ|FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        Require(h != INVALID_HANDLE_VALUE, "cannot hold image ancestor directory");
        if (!IsRegularNonReparse(h, true)) { Close(h); throw std::runtime_error("image ancestor is unavailable or a reparse point"); }
        result.directories.push_back(h); pos = next;
    }
    result.file = CreateFileW(result.path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    Require(result.file != INVALID_HANDLE_VALUE, "cannot hold reviewed synthetic image");
    Require(IsRegularNonReparse(result.file, false), "synthetic image is not a regular non-reparse file");
    IMAGE_DOS_HEADER dos{}; DWORD got=0; Require(ReadFile(result.file, &dos, sizeof(dos), &got, nullptr) && got==sizeof(dos) && dos.e_magic==IMAGE_DOS_SIGNATURE && dos.e_lfanew>=0 && dos.e_lfanew<1024*1024, "invalid synthetic image DOS header");
    LARGE_INTEGER offset{}; offset.QuadPart=dos.e_lfanew; Require(SetFilePointerEx(result.file, offset, nullptr, FILE_BEGIN), "invalid PE offset");
    DWORD signature=0; IMAGE_FILE_HEADER pe{};
    Require(ReadFile(result.file, &signature, sizeof(signature), &got, nullptr) && got==sizeof(signature) && signature==IMAGE_NT_SIGNATURE &&
        ReadFile(result.file, &pe, sizeof(pe), &got, nullptr) && got==sizeof(pe) && pe.Machine==IMAGE_FILE_MACHINE_AMD64, "synthetic image must be x64 PE");
    Require(Sha256(result.file)==expected, "synthetic executable SHA-256 mismatch");
    result.digest=std::move(expected);
    return result;
}

struct Child {
    HANDLE process=nullptr, thread=nullptr, job=nullptr;
    DWORD pid=0; FILETIME creation{}; bool jobAssigned=false;
    Child()=default; Child(const Child&)=delete; Child& operator=(const Child&)=delete;
    Child(Child&& x) noexcept : process(x.process),thread(x.thread),job(x.job),pid(x.pid),creation(x.creation),jobAssigned(x.jobAssigned) { x.process=x.thread=x.job=nullptr; x.jobAssigned=false; }
    // Deliberately do not silently kill/close a live child. The owning supervisor
    // must terminate and confirm process exit before releasing its WFP policy.
};

inline bool TerminateAndWait(Child& c, DWORD milliseconds) {
    if (!c.process) return false;
    DWORD state=WaitForSingleObject(c.process,0); if (state==WAIT_OBJECT_0) return true;
    if (state!=WAIT_TIMEOUT) return false;
    if (c.job && c.jobAssigned && TerminateJobObject(c.job,1)) return WaitForSingleObject(c.process,milliseconds)==WAIT_OBJECT_0;
    if (!TerminateProcess(c.process,1) && WaitForSingleObject(c.process,0)!=WAIT_OBJECT_0) return false;
    return WaitForSingleObject(c.process,milliseconds)==WAIT_OBJECT_0;
}
inline bool CloseAfterExit(Child& c) {
    if (!c.process || WaitForSingleObject(c.process,0)!=WAIT_OBJECT_0) return false;
    bool ok=true;
    for (HANDLE* h : { &c.thread, &c.process, &c.job }) {
        if (*h && *h != INVALID_HANDLE_VALUE) { if (!CloseHandle(*h)) ok=false; else *h=nullptr; }
    }
    return ok;
}

inline std::vector<unsigned char> TokenInfo(HANDLE token, TOKEN_INFORMATION_CLASS type) {
    DWORD length=0; GetTokenInformation(token,type,nullptr,0,&length);
    Require(GetLastError()==ERROR_INSUFFICIENT_BUFFER && length>0 && length<=65536,"cannot size token information");
    std::vector<unsigned char> bytes(length); Require(GetTokenInformation(token,type,bytes.data(),length,&length)!=FALSE,"cannot read token information"); return bytes;
}
inline bool VerifySuspended(const Child& c, const VerifiedImage& image, PSID expectedSid) {
    if (!c.process || !c.thread || !c.job || !expectedSid || !IsValidSid(expectedSid) || GetProcessId(c.process)!=c.pid || WaitForSingleObject(c.process,0)!=WAIT_TIMEOUT) return false;
    try { if (image.digest.size()!=32 || Sha256(image.file)!=image.digest) return false; } catch (...) { return false; }
    FILETIME created{},exited{},kernel{},user{}; if (!GetProcessTimes(c.process,&created,&exited,&kernel,&user) || CompareFileTime(&created,&c.creation)!=0) return false;
    wchar_t path[32768]{}; DWORD len=static_cast<DWORD>(std::size(path)); if (!QueryFullProcessImageNameW(c.process,0,path,&len) || _wcsicmp(path,image.path.c_str())!=0) return false;
    BOOL inJob=FALSE; if (!c.jobAssigned || !IsProcessInJob(c.process,c.job,&inJob) || !inJob) return false;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    if (!QueryInformationJobObject(c.job,JobObjectExtendedLimitInformation,&limits,sizeof(limits),nullptr) ||
        (limits.BasicLimitInformation.LimitFlags & (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_ACTIVE_PROCESS)) !=
            (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_ACTIVE_PROCESS) ||
        limits.BasicLimitInformation.ActiveProcessLimit!=1) return false;
    HANDLE token=nullptr; if (!OpenProcessToken(c.process,TOKEN_QUERY,&token)) return false;
    bool valid=false;
    try {
        DWORD app=0,actual=0; Require(GetTokenInformation(token,TokenIsAppContainer,&app,sizeof(app),&actual) && actual==sizeof(app) && app, "not an AppContainer");
        auto info=TokenInfo(token,TokenAppContainerSid); Require(info.size()>=sizeof(TOKEN_APPCONTAINER_INFORMATION),"invalid package token data");
        auto* package=reinterpret_cast<TOKEN_APPCONTAINER_INFORMATION*>(info.data());
        Require(package->TokenAppContainer && IsValidSid(package->TokenAppContainer) && EqualSid(package->TokenAppContainer,expectedSid),"package SID mismatch");
        auto groups=TokenInfo(token,TokenCapabilities); Require(groups.size()>=sizeof(TOKEN_GROUPS),"invalid capability token data");
        auto* caps=reinterpret_cast<TOKEN_GROUPS*>(groups.data()); Require(caps->GroupCount==1 && caps->Groups[0].Sid && IsValidSid(caps->Groups[0].Sid),"unexpected capabilities");
        alignas(DWORD) BYTE capability[SECURITY_MAX_SID_SIZE]{}; DWORD size=sizeof(capability);
        Require(CreateWellKnownSid(static_cast<WELL_KNOWN_SID_TYPE>(87),nullptr,capability,&size) && EqualSid(caps->Groups[0].Sid,capability) &&
            (caps->Groups[0].Attributes & SE_GROUP_ENABLED) && !(caps->Groups[0].Attributes & SE_GROUP_USE_FOR_DENY_ONLY),"private network capability missing");
        valid=true;
    } catch (...) { valid=false; }
    Close(token); return valid;
}

inline bool IsNonzeroGuidText(const std::wstring& value) {
    if (value.size()!=36) return false;
    bool nonzero=false;
    for (size_t i=0; i<value.size(); ++i) {
        const wchar_t c=value[i];
        if (i==8 || i==13 || i==18 || i==23) { if (c!=L'-') return false; }
        else { if (!((c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f')||(c>=L'A'&&c<=L'F'))) return false; if (c!=L'0') nonzero=true; }
    }
    return nonzero;
}

inline void CreateSuspended(Child& child, const VerifiedImage& image, PSID sid, const std::wstring& ipv4, unsigned short port, const std::wstring& nonce,
    const std::wstring& providerKey, const std::wstring& sublayerKey, const std::wstring& filterKey) {
    Require(!child.process && !child.thread && !child.job,"synthetic child state is not empty");
    Require(image.file && sid && IsValidSid(sid) && port>=1024 && nonce.size()==32,"invalid synthetic process scope");
    Require(IsNonzeroGuidText(providerKey) && IsNonzeroGuidText(sublayerKey) && IsNonzeroGuidText(filterKey) &&
        _wcsicmp(providerKey.c_str(),sublayerKey.c_str())!=0 && _wcsicmp(providerKey.c_str(),filterKey.c_str())!=0 &&
        _wcsicmp(sublayerKey.c_str(),filterKey.c_str())!=0,"invalid synthetic WFP identities");
    for (wchar_t c : nonce) Require((c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f'),"invalid synthetic nonce");
    IN_ADDR addr{}; wchar_t text[INET_ADDRSTRLEN]{}; Require(InetPtonW(AF_INET,ipv4.c_str(),&addr)==1 && InetNtopW(AF_INET,&addr,text,INET_ADDRSTRLEN) && ipv4==text,"invalid IPv4 endpoint");
    UINT32 a=ntohl(addr.S_un.S_addr); Require((a>>24)==10 || (a>>20)==0xAC1 || (a>>16)==0xC0A8,"endpoint is not RFC1918");
    LPWSTR sidText=nullptr; Require(ConvertSidToStringSidW(sid,&sidText)!=FALSE,"cannot format package SID");
    std::wstring command=L"\""+image.path+L"\" --client "+ipv4+L" "+std::to_wstring(port)+L" "+nonce+L" "+sidText+L" "+
        providerKey+L" "+sublayerKey+L" "+filterKey+L" --acknowledge-isolated-test"; LocalFree(sidText);
    alignas(DWORD) BYTE capability[SECURITY_MAX_SID_SIZE]{}; DWORD sidSize=sizeof(capability);
    Require(CreateWellKnownSid(static_cast<WELL_KNOWN_SID_TYPE>(87),nullptr,capability,&sidSize)!=FALSE,"private network capability unavailable");
    SID_AND_ATTRIBUTES entry{capability,SE_GROUP_ENABLED}; SECURITY_CAPABILITIES security{sid,&entry,1,0};
    SIZE_T attributeSize=0; InitializeProcThreadAttributeList(nullptr,1,0,&attributeSize); Require(attributeSize>0,"cannot size process attribute list");
    std::vector<unsigned char> attributeBytes(attributeSize); auto* attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeBytes.data());
    Require(InitializeProcThreadAttributeList(attributes,1,0,&attributeSize)!=FALSE,"cannot initialize process attribute list");
    try {
        Require(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,&security,sizeof(security),nullptr,nullptr)!=FALSE,"cannot set AppContainer capabilities");
        child.job=CreateJobObjectW(nullptr,nullptr); Require(child.job!=nullptr,"cannot create synthetic job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{}; limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_ACTIVE_PROCESS; limits.BasicLimitInformation.ActiveProcessLimit=1;
        Require(SetInformationJobObject(child.job,JobObjectExtendedLimitInformation,&limits,sizeof(limits))!=FALSE,"cannot scope synthetic job");
        wchar_t windows[MAX_PATH]{}; DWORD n=GetWindowsDirectoryW(windows,MAX_PATH); Require(n>0 && n<MAX_PATH,"cannot locate Windows directory");
        std::wstring env=L"AEGIS_DISPOSABLE_VM_NETWORK_TEST=I_UNDERSTAND"; env.push_back(L'\0'); env+=L"SystemRoot="; env+=windows; env.push_back(L'\0'); env+=L"WINDIR="; env+=windows; env.push_back(L'\0'); env.push_back(L'\0');
        STARTUPINFOEXW startup{}; startup.StartupInfo.cb=sizeof(startup); startup.lpAttributeList=attributes; PROCESS_INFORMATION process{};
        Require(CreateProcessW(image.path.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_SUSPENDED|CREATE_NO_WINDOW|EXTENDED_STARTUPINFO_PRESENT|CREATE_UNICODE_ENVIRONMENT,env.data(),nullptr,&startup.StartupInfo,&process)!=FALSE,"cannot create suspended synthetic client");
        child.process=process.hProcess; child.thread=process.hThread; child.pid=process.dwProcessId;
        Require(AssignProcessToJobObject(child.job,child.process)!=FALSE,"cannot assign synthetic child to job");
        child.jobAssigned=true;
        FILETIME e{},k{},u{}; Require(GetProcessTimes(child.process,&child.creation,&e,&k,&u)!=FALSE,"cannot identify synthetic child creation time");
        DeleteProcThreadAttributeList(attributes); return;
    } catch (...) {
        DeleteProcThreadAttributeList(attributes);
        if (child.process) {
            if (!TerminateAndWait(child,15000) || !CloseAfterExit(child))
                throw std::runtime_error("suspended synthetic child cleanup could not be confirmed");
        }
        else Close(child.job);
        throw;
    }
}
} // namespace AegisTestProcess
