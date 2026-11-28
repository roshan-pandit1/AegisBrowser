using System;
using AegisBrowser.WebView2Feasibility;

namespace AegisBrowser.WebView2TwoControl;

// Pure synthetic tests against the scenario plan and the real observation gate.
// No WebView2 runtime is launched, and these checks do not prove ownership or
// the absence of native work after a terminal callback.
internal static class ScenarioSelectionTests
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException("Scenario selection check failed: " + message);
    }

    private static string[] Args(string scenario) => new[]
    {
        "--profile-root", @"C:\AegisLab\Profiles",
        "--shutdown-timeout-ms", "30000", "--scenario", scenario
    };

    private static void VerifyParsed(string name, FixtureScenario expected)
    {
        Check(ScenarioPlan.TryParseArguments(Args(name), out var actual, out var profileArgs, out _), name + " accepted");
        Check(actual == expected, name + " selects requested mode");
        Check(profileArgs.Length == 4, name + " preserves profile argument count");
        Check(ProfileRootPolicy.TryParse(profileArgs, out var config, out _), name + " profile parser accepts forwarded arguments");
        Check(config.ProfileRoot == @"C:\AegisLab\Profiles" && config.ShutdownTimeoutMilliseconds == 30000,
            name + " preserves exact profile configuration");
    }

    private static ObservationGate Ready()
    {
        var gate = new ObservationGate();
        Check(gate.CompleteInitialization(0, true), "control A initialized");
        Check(gate.CompleteInitialization(1, true), "control B initialized");
        return gate;
    }

    internal static int Run()
    {
        checks = 0;
        Check(!ScenarioPlan.TryParseArguments(null, out _, out _, out _), "null rejected");
        Check(!ScenarioPlan.TryParseArguments(Array.Empty<string>(), out _, out _, out _), "missing scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(new[] { "--profile-root", @"C:\AegisLab\Profiles", "--shutdown-timeout-ms", "30000" }, out _, out _, out _), "missing scenario with valid profile rejected");
        Check(!ScenarioPlan.TryParseArguments(Args(""), out _, out _, out _), "empty scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(Args("normal"), out _, out _, out _), "incorrect case rejected");
        Check(!ScenarioPlan.TryParseArguments(Args("Other"), out _, out _, out _), "unknown scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(Args("0"), out _, out _, out _), "numeric scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(new[] { "--scenario" }, out _, out _, out _), "missing value rejected");
        Check(!ScenarioPlan.TryParseArguments(new[] { "--scenario", "Normal", "--scenario", "Normal" }, out _, out _, out _), "duplicate same scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(new[] { "--scenario", "Normal", "--scenario", "BothPending" }, out _, out _, out _), "duplicate differing scenario rejected");
        Check(!ScenarioPlan.TryParseArguments(new[] { "--scenario", "Normal", "--profile-root" }, out _, out _, out _), "unpaired argument rejected");

        VerifyParsed("Normal", FixtureScenario.Normal);
        VerifyParsed("OnePending", FixtureScenario.OnePending);
        VerifyParsed("BothPending", FixtureScenario.BothPending);
        Check(ScenarioPlan.TryParseArguments(new[] { "--scenario", "OnePending", "--shutdown-timeout-ms", "30000", "--profile-root", @"C:\AegisLab\Profiles" }, out var reordered, out var rest, out _), "scenario in first position accepted");
        Check(reordered == FixtureScenario.OnePending && ProfileRootPolicy.TryParse(rest, out _, out _), "remaining arguments retain their order and meaning");

        Check(ScenarioPlan.DispatchMask(FixtureScenario.Normal) == 0, "normal adds no close-race dispatch");
        Check(ScenarioPlan.DispatchMask(FixtureScenario.OnePending) == 1, "one-pending selects A alone");
        Check(ScenarioPlan.DispatchMask(FixtureScenario.BothPending) == 3, "both-pending selects A and B");
        bool invalidMaskRejected = false;
        try { _ = ScenarioPlan.DispatchMask((FixtureScenario)99); }
        catch (ArgumentOutOfRangeException) { invalidMaskRejected = true; }
        Check(invalidMaskRejected, "undefined mask rejected");

        var plan = new ScenarioPlan(FixtureScenario.OnePending);
        Check(!plan.IsExecuted, "new plan not executed");
        Check(!plan.TryBegin(FixtureScenario.OnePending, false), "normal navigation not complete prevents scenario");
        Check(!plan.IsExecuted, "failed readiness does not execute scenario");
        Check(plan.TryBegin(FixtureScenario.OnePending, true), "ready matching scenario executes");
        Check(plan.IsExecuted, "execution latched");
        Check(!plan.TryBegin(FixtureScenario.OnePending, true), "repeated execution denied");

        plan = new ScenarioPlan(FixtureScenario.Normal);
        Check(!plan.TryBegin(FixtureScenario.BothPending, true), "mismatched actual scenario rejected");
        Check(!plan.IsExecuted, "mismatched scenario did not execute");
        Check(!plan.TryBegin(FixtureScenario.Normal, true), "mismatch permanently invalidates plan");
        Check(!plan.IsExecuted, "invalidated plan remains unexecuted");

        plan = new ScenarioPlan(FixtureScenario.BothPending);
        Check(!plan.TryBegin((FixtureScenario)99, true), "undefined actual scenario rejected");
        Check(!plan.TryBegin(FixtureScenario.BothPending, true), "undefined actual invalidates plan");
        bool invalidRequestedRejected = false;
        try { _ = new ScenarioPlan((FixtureScenario)99); }
        catch (ArgumentOutOfRangeException) { invalidRequestedRejected = true; }
        Check(invalidRequestedRejected, "undefined requested scenario rejected");

        // A failed observed normal navigation keeps the gate ambiguous, and the
        // same readiness expression used by the fixture must prevent posting.
        var failed = Ready();
        Check(failed.TryBeginDispatch(0) && failed.TryBeginDispatch(1), "normal-phase dispatches started");
        Check(failed.ObserveStart(0, 101, true, true, false).Candidate, "normal phase A candidate");
        Check(!failed.ObserveCompletion(0, 101, false, false).Expected, "normal failure recognized");
        plan = new ScenarioPlan(FixtureScenario.OnePending);
        Check(!plan.TryBegin(FixtureScenario.OnePending, failed.PendingOperations == 0 && !failed.IsAmbiguous), "failed normal phase prevents selected scenario");
        failed.RequestClose();
        Check(!failed.CanAttemptObservedDisposal, "failed normal phase prevents disposal");

        // Exercise scenario masks with the actual per-control gate. These are
        // simulated observations, not evidence of WebView2 event behavior.
        var one = Ready();
        plan = new ScenarioPlan(FixtureScenario.OnePending);
        Check(plan.TryBegin(FixtureScenario.OnePending, true), "one-pending plan begins");
        int mask = ScenarioPlan.DispatchMask(FixtureScenario.OnePending);
        if ((mask & 1) != 0) Check(one.TryBeginDispatch(0), "one-pending admits A");
        if ((mask & 2) != 0) Check(one.TryBeginDispatch(1), "one-pending admits B");
        Check(one.IsDispatchPending(0) && !one.IsDispatchPending(1), "one-pending really leaves only A unresolved");
        one.RequestClose();
        one.RequestClose();
        Check(!one.CanAttemptObservedDisposal, "repeated close does not dispose one-pending work");
        Check(one.ObserveStart(0, 3, true, true, false).Cancel, "one-pending start canceled");
        Check(!one.CanAttemptObservedDisposal, "cancellation request alone cannot release A");
        Check(one.ObserveCompletion(0, 3, false, true).Expected, "one-pending matching terminal observed");
        Check(one.CanAttemptObservedDisposal, "one-pending observed state can drain");

        var both = Ready();
        plan = new ScenarioPlan(FixtureScenario.BothPending);
        Check(plan.TryBegin(FixtureScenario.BothPending, true), "both-pending plan begins");
        mask = ScenarioPlan.DispatchMask(FixtureScenario.BothPending);
        if ((mask & 1) != 0) Check(both.TryBeginDispatch(0), "both-pending admits A");
        if ((mask & 2) != 0) Check(both.TryBeginDispatch(1), "both-pending admits B");
        Check(both.IsDispatchPending(0) && both.IsDispatchPending(1), "both-pending has two unresolved dispatches");
        both.RequestClose();
        both.RequestClose();
        Check(!both.CanAttemptObservedDisposal, "repeated close does not release either pending dispatch");
        Check(both.ObserveStart(0, 4, true, true, false).Cancel && both.ObserveStart(1, 4, true, true, false).Cancel,
            "two independent starts canceled");
        Check(both.ObserveCompletion(0, 4, false, true).Expected, "A terminal observed");
        Check(!both.CanAttemptObservedDisposal, "A terminal cannot release B");
        Check(both.ObserveCompletion(1, 4, false, true).Expected, "B terminal observed");
        Check(both.CanAttemptObservedDisposal, "both observed dispatches drain independently");

        var normal = Ready();
        plan = new ScenarioPlan(FixtureScenario.Normal);
        Check(plan.TryBegin(FixtureScenario.Normal, true), "normal plan begins");
        Check(ScenarioPlan.DispatchMask(FixtureScenario.Normal) == 0, "normal scenario has no extra dispatch");
        normal.RequestClose();
        Check(normal.CanAttemptObservedDisposal, "normal observed gate permits normal close");
        return checks;
    }
}
