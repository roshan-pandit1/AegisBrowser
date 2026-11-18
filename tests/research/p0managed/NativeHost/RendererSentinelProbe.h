#pragma once

#include <windows.h>
#include <string>
#include "FixtureGate.h"

// Test-only read-open of the resource created by the independent supervisor.
// The nonce must be the existing scoped observation-pipe suffix. A result is
// an unauthenticated renderer claim; even ERROR_ACCESS_DENIED is not proof of
// identity, the exact object, or sandbox enforcement.
namespace p0sentinel {
inline DWORD ReadOpenClaim(const std::string& pipe) {
  if (!p309g::IsObservationPipe(pipe)) return ERROR_INVALID_PARAMETER;
  wchar_t temp[MAX_PATH] = {};
  const DWORD length = GetTempPathW(MAX_PATH, temp);
  if (!length || length >= MAX_PATH) return ERROR_INVALID_NAME;
  std::wstring path(temp, length);
  path += L"aegis-p0-sentinel-";
  const std::string nonce = pipe.substr(pipe.size() - 32);
  for (char ch : nonce) path.push_back(static_cast<wchar_t>(ch));
  path += L".dat";
  HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
      OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (file == INVALID_HANDLE_VALUE) return GetLastError();
  CloseHandle(file);
  return ERROR_SUCCESS;
}
}  // namespace p0sentinel
