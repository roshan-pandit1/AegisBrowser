#pragma once

#include <cstring>
#include <string>

namespace p309g {

constexpr char kFixtureMarker[] = "AEGIS_P309G_FIXTURE_V1";
constexpr char kFixtureUrl[] = "data:text/html,AEGIS_P309G_FIXTURE_V1";

// Only a locally addressed per-run pipe with an opaque 128-bit lowercase hex
// suffix is accepted. This is a routing constraint, not authentication.
inline bool IsObservationPipe(const std::string& name) {
  constexpr char prefix[] = "\\\\.\\pipe\\aegis-p309i-";
  constexpr size_t size = sizeof(prefix) - 1;
  if (name.size() != size + 32 || name.compare(0, size, prefix) != 0) return false;
  for (size_t i = size; i < name.size(); ++i)
    if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
  return true;
}

inline std::string ObservationPipeArgument(int argc, char* const argv[]) {
  constexpr char prefix[] = "--aegis-p309i-pipe=";
  constexpr size_t size = sizeof(prefix) - 1;
  std::string result;
  for (int i = 0; i < argc; ++i) {
    if (argv[i] && std::strncmp(argv[i], prefix, size) == 0) {
      if (!result.empty()) return {};
      result = argv[i] + size;
      if (!IsObservationPipe(result)) return {};
    }
  }
  return result;
}

// This marker is a test fixture selector, not an authentication primitive.
inline bool IsFixtureUrl(const std::string& url) {
  constexpr char prefix[] = "data:text/html";
  constexpr size_t length = sizeof(prefix) - 1;
  return url.rfind(prefix, 0) == 0 && url.size() > length &&
         (url[length] == ',' || url[length] == ';') &&
         url.find(kFixtureMarker, length + 1) != std::string::npos;
}

// An unrelated preliminary cancellation can be followed by the fixture.
// Cancellation of the fixture itself is a failed attempt, not a load or a
// reason to keep a browser alive indefinitely waiting for that attempt.
inline bool IsFixtureAbort(bool is_aborted, const std::string& failed_url) {
  return is_aborted && IsFixtureUrl(failed_url);
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

// Chromium may launch children with a filtered environment. The opted-in
// browser copies this test-only marker to its children. It is spoofable and
// expresses test intent only; it is not process-ownership or sandbox proof.
// Permit it only for one explicitly named, recognized child role.
inline bool HasChildOptIn(int argc, char* const argv[]) {
  unsigned markers = 0, roles = 0;
  bool recognized = false;
  for (int i = 0; i < argc; ++i) {
    const char* name = SwitchName(argv[i]);
    if (!name) continue;
    if (std::strcmp(name, "aegis-p309f-child-opt-in") == 0) ++markers;
    if (_stricmp(name, "type") == 0 || _strnicmp(name, "type=", 5) == 0) {
      ++roles;
      if (_strnicmp(name, "type=", 5) == 0) {
        const char* role = name + 5;
        recognized = _stricmp(role, "renderer") == 0 ||
                     _stricmp(role, "gpu-process") == 0 ||
                     _stricmp(role, "utility") == 0;
      }
    }
  }
  return markers == 1 && roles == 1 && recognized;
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
