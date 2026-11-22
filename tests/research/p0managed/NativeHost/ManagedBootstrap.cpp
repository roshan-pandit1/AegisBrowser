#include "ManagedBootstrap.h"

#include <windows.h>
#include <objbase.h>
#include <shlwapi.h>
#include <cstdio>
#include <string>
#include <vector>
#include "nethost.h"
#include "hostfxr.h"
#include "coreclr_delegates.h"

namespace {
using get_hostfxr_path_fn = int (NETHOST_CALLTYPE*)(char_t*, size_t*, const get_hostfxr_parameters*);
using managed_entry_fn = int (__cdecl*)(void*, void*);
using co_initialize_ex_fn = HRESULT (WINAPI*)(LPVOID, DWORD);
using co_uninitialize_fn = void (WINAPI*)();

bool GetEnvironment(const wchar_t* name, std::wstring& result) {
  DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
  if (!needed || needed > 32768) return false;
  std::vector<wchar_t> value(needed);
  DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
  if (!written || written >= needed) return false;
  result.assign(value.data(), written);
  return true;
}

bool RegularFile(const std::wstring& path) {
  const DWORD flags = GetFileAttributesW(path.c_str());
  return flags != INVALID_FILE_ATTRIBUTES &&
      !(flags & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

bool CanonicalPath(const std::wstring& input, std::wstring& result) {
  std::vector<wchar_t> full(32768);
  const DWORD length = GetFullPathNameW(input.c_str(), static_cast<DWORD>(full.size()), full.data(), nullptr);
  if (!length || length >= full.size()) return false;
  result.assign(full.data(), length);
  return true;
}

template<class T> T Symbol(HMODULE module, const char* name) {
  return reinterpret_cast<T>(GetProcAddress(module, name));
}

int Failed(const char* stage) {
  std::fprintf(stderr, "MANAGED_HOST: FAILED stage=%s\n", stage);
  return 41;
}
}

int RunManagedBrowser(HINSTANCE instance, void* sandbox_info) {
  // This is an execution opt-in, not proof of verified bundle integrity.
  // The experiment's supervisor must verify and hold the test bundle.
  std::wstring opt_in, dotnet_root, canonical_root;
  if (!instance || !sandbox_info ||
      !GetEnvironment(L"AEGIS_P0_MANAGED_EXECUTE", opt_in) || opt_in != L"YES" ||
      !GetEnvironment(L"AEGIS_P0_MANAGED_DOTNET_ROOT", dotnet_root) ||
      PathIsRelativeW(dotnet_root.c_str()) || !CanonicalPath(dotnet_root, canonical_root) ||
      canonical_root.size() < 3 || canonical_root[1] != L':' || canonical_root[2] != L'\\' ||
      GetDriveTypeW(canonical_root.substr(0, 3).c_str()) != DRIVE_FIXED)
    return Failed("PREREQUISITES");
  while (!canonical_root.empty() && (canonical_root.back() == L'\\' || canonical_root.back() == L'/'))
    canonical_root.pop_back();
  const std::wstring root_prefix = canonical_root + L"\\";

  std::vector<wchar_t> executable(32768);
  DWORD length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
  if (!length || length >= executable.size()) return Failed("EXECUTABLE_PATH");
  std::wstring directory(executable.data(), length);
  const size_t separator = directory.find_last_of(L"\\/");
  if (separator == std::wstring::npos) return Failed("BUNDLE_PATH");
  directory.resize(separator + 1);
  const std::wstring nethost_path = directory + L"nethost.dll";
  const std::wstring assembly_path = directory + L"P0ManagedHost.dll";
  const std::wstring runtime_config = directory + L"P0ManagedHost.runtimeconfig.json";
  if (!RegularFile(nethost_path) || !RegularFile(assembly_path) || !RegularFile(runtime_config))
    return Failed("MISSING_OR_LINKED_DEPENDENCY");

  // Load COM only in the browser after CEF subprocess dispatch, rather than
  // adding another static DLL dependency to every sandbox child.
  HMODULE ole = LoadLibraryExW(L"ole32.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!ole) return Failed("COM_LIBRARY");
  const auto initialize_com = Symbol<co_initialize_ex_fn>(ole, "CoInitializeEx");
  const auto uninitialize_com = Symbol<co_uninitialize_fn>(ole, "CoUninitialize");
  if (!initialize_com || !uninitialize_com) return Failed("COM_EXPORTS");
  const HRESULT com = initialize_com(nullptr, COINIT_APARTMENTTHREADED);
  if (FAILED(com)) return Failed("STA_INITIALIZATION");
  struct ComScope { co_uninitialize_fn finish; ~ComScope() { finish(); } } com_scope{uninitialize_com};

  // The modules remain loaded until process exit. Unloading hostfxr after
  // starting the CLR is not treated as a supported runtime teardown.
  HMODULE nethost = LoadLibraryExW(nethost_path.c_str(), nullptr,
      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!nethost) return Failed("NETHOST_LOAD");
  const auto get_path = Symbol<get_hostfxr_path_fn>(nethost, "get_hostfxr_path");
  if (!get_path) return Failed("NETHOST_EXPORT");
  std::vector<char_t> hostfxr_path(32768);
  size_t hostfxr_size = hostfxr_path.size();
  get_hostfxr_parameters params{sizeof(params), nullptr, canonical_root.c_str()};
  std::wstring canonical_hostfxr;
  if (get_path(hostfxr_path.data(), &hostfxr_size, &params) != 0 ||
      hostfxr_size == 0 || hostfxr_size > hostfxr_path.size() ||
      PathIsRelativeW(hostfxr_path.data()) || !CanonicalPath(hostfxr_path.data(), canonical_hostfxr) ||
      canonical_hostfxr.size() <= root_prefix.size() ||
      _wcsnicmp(canonical_hostfxr.c_str(), root_prefix.c_str(), root_prefix.size()) != 0 ||
      !RegularFile(canonical_hostfxr))
    return Failed("HOSTFXR_RESOLUTION");
  HMODULE hostfxr = LoadLibraryExW(canonical_hostfxr.c_str(), nullptr,
      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!hostfxr) return Failed("HOSTFXR_LOAD");
  std::puts("MANAGED_HOST: HOSTFXR_LOADED role=browser");
  const auto initialize = Symbol<hostfxr_initialize_for_runtime_config_fn>(hostfxr, "hostfxr_initialize_for_runtime_config");
  const auto get_delegate = Symbol<hostfxr_get_runtime_delegate_fn>(hostfxr, "hostfxr_get_runtime_delegate");
  const auto close = Symbol<hostfxr_close_fn>(hostfxr, "hostfxr_close");
  if (!initialize || !get_delegate || !close) return Failed("HOSTFXR_EXPORTS");
  hostfxr_handle context = nullptr;
  const int init_rc = initialize(runtime_config.c_str(), nullptr, &context);
  if (init_rc != 0 || !context) {
    if (context) close(context);
    return Failed("HOSTFXR_INITIALIZE");
  }
  std::puts("MANAGED_HOST: RUNTIME_CONFIG_ACCEPTED role=browser");
  void* raw_delegate = nullptr;
  const int delegate_rc = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &raw_delegate);
  const int close_rc = close(context);
  if (delegate_rc != 0 || close_rc != 0 || !raw_delegate) return Failed("HOSTFXR_DELEGATE");
  const auto load = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(raw_delegate);
  void* raw_entry = nullptr;
  if (load(assembly_path.c_str(), L"P0ManagedHost.ManagedEntry, P0ManagedHost", L"Run",
           UNMANAGEDCALLERSONLY_METHOD, nullptr, &raw_entry) != 0 || !raw_entry)
    return Failed("MANAGED_ENTRY_RESOLUTION");
  std::puts("MANAGED_HOST: CLR_ENTRY_REQUESTED role=browser sandbox_info=present");
  const int result = reinterpret_cast<managed_entry_fn>(raw_entry)(instance, sandbox_info);
  std::printf("MANAGED_HOST: CLR_ENTRY_RETURNED code=%d\n", result);
  return result;
}
