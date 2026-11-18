#include "FixtureGate.h"
#include <cstdio>

int main() {
  char browser[] = "bootstrap.exe";
  char renderer[] = "--type=renderer";
  char utility[] = "--type=utility";
  char unknown[] = "--type=unknown";
  char split[] = "--type";
  char* plain[] = {browser};
  char* child[] = {browser, renderer};
  char* utility_child[] = {browser, utility};
  char* unknown_child[] = {browser, unknown};
  char* split_child[] = {browser, split};
  const bool checks[] = {
      p309g::MayRunManagedHost(-1, 1, plain),
      !p309g::MayRunManagedHost(-2, 1, plain),
      !p309g::MayRunManagedHost(0, 1, plain),
      !p309g::MayRunManagedHost(-1, 2, child),
      !p309g::MayRunManagedHost(-1, 2, utility_child),
      !p309g::MayRunManagedHost(-1, 2, unknown_child),
      !p309g::MayRunManagedHost(-1, 2, split_child),
  };
  for (bool check : checks) if (!check) {
    std::fputs("FAIL: managed browser dispatch guard\n", stderr);
    return 1;
  }
  std::puts("PASS: 7 synthetic managed dispatch checks; no CEF or CLR execution");
  return 0;
}
