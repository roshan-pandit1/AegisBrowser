namespace AegisBrowser.Testing;

// This is a provisional, in-process guard for the three .NET test entry
// points. It is not an OS boundary and does not govern standalone scripts.
// B/C are deliberately unavailable until human authorization can be verified
// against an established trust mechanism and the actual machine identity.
internal static class TestGovernance
{
    internal enum ProgramKind { Core, Browser, Filtering }
    internal enum Disposition { Refuse, DryRun, RunSelfTest }

    internal sealed record Plan(
        string Id, string Profile, string Risk, string What, string Environment,
        string Authorization, string Filesystem, string Processes,
        string Network, string Privileges);

    internal sealed record Decision(Disposition Disposition, Plan? Plan, string Reason);

    private static readonly Plan SelfTest = new(
        "governance-self-test", "A", "SAFE_WORKSPACE", "In-memory governance decision checks only",
        "Declared development-safe context; build and runtime remain subject to separate audit",
        "No isolated-VM authorization; only the fixed in-memory self-test is selected",
        "No intentional file writes by governance logic",
        "Current test process only; no child process",
        "None by governance logic", "No elevation requested");

    private static readonly Plan CoreSuite = new(
        "core-suite", "B", "ISOLATED_ENVIRONMENT_REQUIRED", "Existing core test executable; execution currently blocked",
        "Existing authorized disposable isolated Windows machine",
        "Human authorization and trusted verification required; verifier not configured",
        "Disposable profiles, junctions and test-owned files",
        "Starts and deliberately terminates test-owned child processes",
        "Includes loopback fixtures", "Depends on selected fixtures; not established");

    private static readonly Plan BrowserSuite = new(
        "browser-suite", "B", "ISOLATED_ENVIRONMENT_REQUIRED", "Existing native browser test executable; execution currently blocked",
        "Existing authorized disposable isolated Windows machine",
        "Human authorization and trusted verification required; verifier not configured",
        "Disposable browser profiles and test-owned files",
        "Starts Chromium children and deliberately crashes a test renderer",
        "Controlled fixtures; some legacy modes require separate network review",
        "Depends on selected fixtures; not established");

    private static readonly Plan FilteringSuite = new(
        "filtering-suite", "B", "ISOLATED_ENVIRONMENT_REQUIRED", "Existing filtering test executable; execution currently blocked",
        "Existing authorized disposable isolated Windows machine",
        "Human authorization and trusted verification required; verifier not configured",
        "Disposable browser profile and fixture files",
        "Starts a real browser and Chromium children",
        "Controlled loopback fixtures", "Not established");

    private static readonly Plan NetworkSuite = new(
        "network-containment", "C", "EXPLICIT_AUTHORIZATION_REQUIRED", "Independent containment fixtures; execution currently blocked",
        "Two existing authorized isolated machines and an approved evidence path",
        "Human authorization and trusted verification required; verifier not configured",
        "Test-owned evidence and policy objects as separately reviewed",
        "Controlled browser and receiver processes",
        "Isolated test network only; independent receiver and controls required",
        "Approved WFP and audit permissions required");

    internal static Decision Evaluate(ProgramKind program, IReadOnlyList<string>? args) =>
        Evaluate(program, args, InventoryBinding.ValidateCurrent);

    // Injection exists for deterministic in-memory tests; executable entry
    // points use the overload above and cannot supply a command-line override.
    internal static Decision Evaluate(ProgramKind program, IReadOnlyList<string>? args, Func<bool> validateInventory)
    {
        // Never echo unrecognized arguments: they could contain sensitive data.
        if (args is null || args.Count != 3 ||
            (args[0] != "--governance-dry-run" && args[0] != "--governance-run"))
            return new(Disposition.Refuse, null, "UNCLASSIFIED_OR_MISSING_DECLARATION");

        var plan = FindPlan(program, args[2]);
        if (plan is null || args[1] != plan.Profile)
            return new(Disposition.Refuse, null, "UNCLASSIFIED_OR_PROFILE_MISMATCH");

        if (args[0] == "--governance-dry-run")
            return new(Disposition.DryRun, plan, "INSPECTION_ONLY");
        if (program == ProgramKind.Core && plan.Id == SelfTest.Id && plan.Profile == "A")
        {
            try
            {
                if (validateInventory is null || !validateInventory())
                    return new(Disposition.Refuse, plan, "INVENTORY_INVALID_OR_STALE");
            }
            catch (Exception)
            {
                return new(Disposition.Refuse, plan, "INVENTORY_INVALID_OR_STALE");
            }
            return new(Disposition.RunSelfTest, plan, "PROVISIONAL_PURE_SELF_TEST");
        }

        // No flag, environment variable, file existence, or self-attestation
        // can authorize B/C. No trusted human authorization verifier exists.
        return new(Disposition.Refuse, plan, "HUMAN_AUTHORIZATION_VERIFIER_UNAVAILABLE");
    }

    private static Plan? FindPlan(ProgramKind program, string id)
    {
        if (program == ProgramKind.Core && id == SelfTest.Id) return SelfTest;
        if (program == ProgramKind.Core && id == CoreSuite.Id) return CoreSuite;
        if (program == ProgramKind.Browser && id == BrowserSuite.Id) return BrowserSuite;
        if (program == ProgramKind.Filtering && id == FilteringSuite.Id) return FilteringSuite;
        if (program == ProgramKind.Browser && id == NetworkSuite.Id) return NetworkSuite;
        return null;
    }

    // This stays false for all decisions, including fabricated ones. The old
    // suite bodies remain in source, but cannot run until a reviewed verifier
    // and per-operation classification replace this explicit deny.
    internal static bool AllowLegacyExecution(Decision _) => false;

    internal static int Report(Decision decision)
    {
        if (decision.Disposition == Disposition.DryRun && decision.Plan is { } plan)
        {
            Console.WriteLine($"NOT_RUN: id={plan.Id}; profile={plan.Profile}; risk={plan.Risk}");
            Console.WriteLine($"would_run={plan.What}");
            Console.WriteLine($"environment={plan.Environment}; authorization={plan.Authorization}");
            Console.WriteLine($"filesystem={plan.Filesystem}; processes={plan.Processes}");
            Console.WriteLine($"network={plan.Network}; privileges={plan.Privileges}");
            Console.WriteLine("No test was started. A preflight or environment flag is not authorization.");
            return 0;
        }
        Console.Error.WriteLine(decision.Reason == "HUMAN_AUTHORIZATION_VERIFIER_UNAVAILABLE"
            ? "BLOCKED [AUTHORIZATION-BLOCKED]: no trusted human authorization verifier is configured; no test was started."
            : "BLOCKED [UNVERIFIED]: missing, invalid or unclassified declaration; no test was started.");
        return 2;
    }
}
