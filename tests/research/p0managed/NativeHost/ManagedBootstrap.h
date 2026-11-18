#pragma once

#include <windows.h>

// Browser process only, called after CefExecuteProcess has returned -1.
// This experimental framework-dependent host is not a production loader.
int RunManagedBrowser(HINSTANCE instance, void* sandbox_info);
