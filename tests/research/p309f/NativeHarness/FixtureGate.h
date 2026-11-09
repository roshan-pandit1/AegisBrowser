#pragma once

#include <cstring>
#include <string>

namespace p309g {

constexpr char kFixtureMarker[] = "AEGIS_P309G_FIXTURE_V1";
constexpr char kFixtureUrl[] = "data:text/html,AEGIS_P309G_FIXTURE_V1";

// This marker is a test fixture selector, not an authentication primitive.
inline bool IsFixtureUrl(const std::string& url) {
  constexpr char prefix[] = "data:text/html";
  constexpr size_t length = sizeof(prefix) - 1;
  return url.rfind(prefix, 0) == 0 && url.size() > length &&
         (url[length] == ',' || url[length] == ';') &&
         url.find(kFixtureMarker, length + 1) != std::string::npos;
}

// Only allowlisted process-type labels enter the diagnostics; raw arguments
// can contain unrelated data and are intentionally not logged.
inline const char* SwitchName(const char* arg) {
  if (!arg) return nullptr;
  if (arg[0] == '-' && arg[1] == '-') return arg + 2;
  if (arg[0] == '-' || arg[0] == '/') return arg + 1;
  return nullptr;
}

inline const char* ProcessRole(int argc, char* const argv[]) {
  for (int i = 0; i < argc; ++i) {
    const char* name = SwitchName(argv[i]);
    if (!name) continue;
    if (_stricmp(name, "type") == 0) return "other";
    if (_strnicmp(name, "type=", 5) != 0) continue;
    const char* type = name + 5;
    if (_stricmp(type, "renderer") == 0) return "renderer";
    if (_stricmp(type, "gpu-process") == 0) return "gpu";
    if (_stricmp(type, "utility") == 0) return "utility";
    return "other";
  }
  return "browser";
}

enum class GateFailure { None, OptIn, MissingSandboxInfo, NoSandbox, DisableGpuSandbox };

inline GateFailure EvaluateGate(bool opted_in, bool has_sandbox_info,
                                int argc, char* const argv[]) {
  if (!opted_in) return GateFailure::OptIn;
  if (!has_sandbox_info) return GateFailure::MissingSandboxInfo;
  for (int i = 0; i < argc; ++i) {
    const char* name = SwitchName(argv[i]);
    if (!name) continue;
    if (_stricmp(name, "no-sandbox") == 0 ||
        _strnicmp(name, "no-sandbox=", 11) == 0) return GateFailure::NoSandbox;
    if (_stricmp(name, "disable-gpu-sandbox") == 0 ||
        _strnicmp(name, "disable-gpu-sandbox=", 20) == 0) return GateFailure::DisableGpuSandbox;
  }
  return GateFailure::None;
}

}  // namespace p309g
