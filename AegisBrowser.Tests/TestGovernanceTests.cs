using AegisBrowser.Testing;
using static AegisBrowser.Testing.TestGovernance;

// Pure, in-memory checks of decisions. No executable, profile, file or
// authorization artifact is opened, and no environment variable is read.
internal static class TestGovernanceTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value)
        {
            if (!value) throw new InvalidOperationException("Governance refusal self-test failed.");
            checks++;
        }
        static Decision Decide(ProgramKind program, params string[] args) => Evaluate(program, args);

        Check(Decide(ProgramKind.Core).Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Browser).Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Filtering).Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Core, "--governance-run", "A", "governance-self-test").Disposition == Disposition.RunSelfTest);
        Check(Decide(ProgramKind.Core, "--governance-dry-run", "A", "governance-self-test").Disposition == Disposition.DryRun);
        Check(Decide(ProgramKind.Core, "--governance-run", "B", "core-suite").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Browser, "--governance-run", "B", "browser-suite").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Filtering, "--governance-run", "B", "filtering-suite").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Browser, "--governance-run", "C", "network-containment").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Browser, "--governance-dry-run", "C", "network-containment").Disposition == Disposition.DryRun);
        Check(Decide(ProgramKind.Core, "--governance-run", "A", "core-suite").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Core, "--governance-run", "B", "governance-self-test").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Core, "--governance-run", "A", "not-in-the-catalog").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Core, "--governance-run", "B", "core-suite", "extra").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Core, "--probe-lock", "ignored").Disposition == Disposition.Refuse);
        Check(Decide(ProgramKind.Browser, "--governance-run", "A", "governance-self-test").Disposition == Disposition.Refuse);
        Check(!AllowLegacyExecution(new Decision(Disposition.RunSelfTest, null, "SYNTHETIC")));
        var unknown = Decide(ProgramKind.Core, "--governance-run", "A", "not-in-the-catalog");
        Check(unknown.Plan is null && unknown.Reason == "UNCLASSIFIED_OR_PROFILE_MISMATCH");

        Console.WriteLine($"PASS [SYNTHETIC]: {checks} in-memory governance decision checks; no legacy test started.");
        return 0;
    }
}
