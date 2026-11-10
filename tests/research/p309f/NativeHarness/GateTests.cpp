#include "FixtureGate.h"
#include <cstdio>
#include <cstring>

int main() {
  int checks = 0;
  auto check = [&](bool value, const char* name) {
    if (!value) std::fprintf(stderr, "FAIL: %s\n", name);
    else ++checks;
    return value;
  };
  char app[] = "bootstrapc";
  char renderer[] = "--type=renderer";
  char gpu[] = "--type=gpu-process";
  char utility[] = "--type=utility";
  char unknown[] = "--type=untrusted-value";
  char noSandbox[] = "--no-sandbox";
  char noSandboxValue[] = "--NO-SANDBOX=1";
  char gpuDisabled[] = "--disable-gpu-sandbox";
  char alternateNoSandbox[] = "/no-sandbox";
  char alternateGpu[] = "-disable-gpu-sandbox=1";
  char malformedType[] = "--type";
  char childOptIn[] = "--aegis-p309f-child-opt-in";
  char childOptInValue[] = "--aegis-p309f-child-opt-in=1";
  char* base[] = {app};
  char* a[] = {app, renderer};
  char* b[] = {app, gpu};
  char* c[] = {app, utility};
  char* d[] = {app, unknown};
  char* e[] = {app, renderer, noSandbox};
  char* f[] = {app, noSandboxValue};
  char* g[] = {app, gpu, gpuDisabled};
  char* h[] = {app, alternateNoSandbox};
  char* i[] = {app, alternateGpu};
  char* j[] = {app, malformedType};
  char* kr[] = {app, renderer, childOptIn};
  char* kg[] = {app, gpu, childOptIn};
  char* ku[] = {app, utility, childOptIn};
  char* kb[] = {app, childOptIn};
  char* kx[] = {app, unknown, childOptIn};
  char* km[] = {app, malformedType, childOptIn};
  char* kd[] = {app, renderer, gpu, childOptIn};
  char* kt[] = {app, renderer, childOptIn, childOptIn};
  char* kv[] = {app, renderer, childOptInValue};
  char* kn[] = {app, renderer, childOptIn, noSandbox};
  char* kp[] = {app, gpu, childOptIn, gpuDisabled};
  using p309g::GateFailure;
  bool ok = true;
  ok &= check(p309g::EvaluateGate(false, true, 1, base) == GateFailure::OptIn, "missing opt-in rejected");
  ok &= check(p309g::EvaluateGate(true, false, 1, base) == GateFailure::MissingSandboxInfo, "missing sandbox info rejected");
  ok &= check(p309g::EvaluateGate(true, true, 1, base) == GateFailure::None, "configured input permitted by gate only");
  ok &= check(p309g::EvaluateGate(true, true, 3, e) == GateFailure::NoSandbox, "renderer no-sandbox rejected");
  ok &= check(p309g::EvaluateGate(true, true, 2, f) == GateFailure::NoSandbox, "case-insensitive no-sandbox value rejected");
  ok &= check(p309g::EvaluateGate(true, true, 3, g) == GateFailure::DisableGpuSandbox, "disabled GPU sandbox rejected");
  ok &= check(p309g::EvaluateGate(true, true, 2, h) == GateFailure::NoSandbox, "Windows slash-form no-sandbox rejected");
  ok &= check(p309g::EvaluateGate(true, true, 2, i) == GateFailure::DisableGpuSandbox, "single-dash GPU disable rejected");
  ok &= check(std::strcmp(p309g::ProcessRole(1, base), "browser") == 0, "browser role");
  ok &= check(std::strcmp(p309g::ProcessRole(2, a), "renderer") == 0, "renderer role");
  ok &= check(std::strcmp(p309g::ProcessRole(2, b), "gpu") == 0, "GPU role");
  ok &= check(std::strcmp(p309g::ProcessRole(2, c), "utility") == 0, "utility role");
  ok &= check(std::strcmp(p309g::ProcessRole(2, d), "other") == 0, "unknown role redacted");
  ok &= check(std::strcmp(p309g::ProcessRole(2, j), "other") == 0, "incomplete process-type is not labeled browser");
  ok &= check(p309g::HasChildOptIn(3, kr), "renderer child opt-in");
  ok &= check(p309g::HasChildOptIn(3, kg), "GPU child opt-in");
  ok &= check(p309g::HasChildOptIn(3, ku), "utility child opt-in");
  ok &= check(!p309g::HasChildOptIn(2, kb), "marker alone cannot opt in browser");
  ok &= check(!p309g::HasChildOptIn(3, kx), "unknown child role cannot use marker");
  ok &= check(!p309g::HasChildOptIn(3, km), "malformed role cannot use marker");
  ok &= check(!p309g::HasChildOptIn(4, kd), "duplicate roles rejected");
  ok &= check(!p309g::HasChildOptIn(4, kt), "duplicate markers rejected");
  ok &= check(!p309g::HasChildOptIn(3, kv), "marker with value rejected");
  ok &= check(p309g::EvaluateGate(p309g::HasChildOptIn(3, kr), false, 3, kr) == GateFailure::MissingSandboxInfo,
              "child marker cannot bypass sandbox-info gate");
  ok &= check(p309g::EvaluateGate(p309g::HasChildOptIn(4, kn), true, 4, kn) == GateFailure::NoSandbox,
              "child marker cannot bypass no-sandbox gate");
  ok &= check(p309g::EvaluateGate(p309g::HasChildOptIn(4, kp), true, 4, kp) == GateFailure::DisableGpuSandbox,
              "child marker cannot bypass GPU sandbox gate");
  ok &= check(p309g::IsFixtureUrl(p309g::kFixtureUrl), "fixture marker accepted");
  ok &= check(p309g::IsFixtureUrl("data:text/html,%3Cp%3EAEGIS_P309G_FIXTURE_V1%3C/p%3E"), "canonicalization does not require raw URL equality");
  ok &= check(!p309g::IsFixtureUrl("https://example.invalid/AEGIS_P309G_FIXTURE_V1"), "wrong scheme rejected");
  ok &= check(!p309g::IsFixtureUrl("data:text/plain,AEGIS_P309G_FIXTURE_V1"), "wrong MIME type rejected");
  ok &= check(!p309g::IsFixtureUrl("data:text/html,other"), "missing marker rejected");
  ok &= check(p309g::IsFixtureAbort(true, p309g::kFixtureUrl), "aborted fixture identified as incomplete");
  ok &= check(!p309g::IsFixtureAbort(true, "about:blank"), "unrelated aborted navigation is not fixture failure");
  ok &= check(!p309g::IsFixtureAbort(false, p309g::kFixtureUrl), "non-abort not classified as cancellation");
  ok &= check(!p309g::IsFixtureAbort(true, ""), "missing failed URL does not invent fixture identity");
  if (!ok) return 1;
  std::printf("PASS: %d synthetic fixture and sandbox-gate checks; no CEF execution\n", checks);
  return 0;
}
