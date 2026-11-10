using P309i;
using AegisBrowser.P309G;
using System.Diagnostics;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

var missing = PolicyEvidence.Assess(null);
Check(missing.RendererSandboxPolicy == "INCONCLUSIVE", "missing observation is not a policy match");
Check(missing.SandboxEnforcement == "INCONCLUSIVE", "missing observation is not enforcement");
Check(missing.IntegrityComparison == "UNKNOWN", "missing integrity remains unknown");
var invalid = TokenObserver.Observe(null, 0, 0, null);
var rejected = PolicyEvidence.Assess(invalid);
Check(rejected.Reason == "TOKEN_NOT_OBSERVED", "invalid handle is not promoted");
Check(rejected.RestrictedSidComparison == "UNKNOWN", "failed token query does not become unrestricted");

if (OperatingSystem.IsWindows())
{
    using var process = Process.GetCurrentProcess();
    long creation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
    var observed = TokenObserver.Observe(process.SafeHandle, (uint)process.Id, creation, process.MainModule!.FileName);
    Check(observed.Status == "OBSERVED", "owned helper token observed");
    var assessment = PolicyEvidence.Assess(observed);
    Check(assessment.SandboxEnforcement == "INCONCLUSIVE", "real helper token does not prove renderer enforcement");
    Check(assessment.RendererSandboxPolicy == "INCONCLUSIVE", "real helper token does not prove renderer policy");
    Check(assessment.IntegrityComparison != "UNKNOWN", "available integrity is recorded as an attribute");
    Check(assessment.RestrictedSidComparison != "UNKNOWN", "available restricting SID count is recorded as an attribute");
    Check(assessment.Reason.Contains("NOT_INDEPENDENTLY_ESTABLISHED"), "remaining evidence gap is explicit");
}
Console.WriteLine($"PASS: {checks} conservative policy evidence checks; no renderer sandbox acceptance");
