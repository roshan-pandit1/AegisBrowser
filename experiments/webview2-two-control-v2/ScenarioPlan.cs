using System;
using System.Collections.Generic;

namespace AegisBrowser.WebView2TwoControl;

internal enum FixtureScenario { Normal, OnePending, BothPending }

// Fixture-only scenario selection. It does not establish navigation ownership.
internal sealed class ScenarioPlan
{
    private readonly FixtureScenario requested;
    private bool invalidated;
    internal bool IsExecuted { get; private set; }

    internal ScenarioPlan(FixtureScenario requested)
    {
        _ = DispatchMask(requested);
        this.requested = requested;
    }

    internal static bool TryParseArguments(string[]? args, out FixtureScenario scenario,
        out string[] profileArgs, out string reason)
    {
        scenario = default;
        profileArgs = Array.Empty<string>();
        reason = "INVALID_ARGUMENTS";
        if (args is null || args.Length % 2 != 0) return false;
        bool found = false;
        var remainder = new List<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] != "--scenario")
            {
                remainder.Add(args[i]);
                remainder.Add(args[i + 1]);
                continue;
            }
            if (found) { reason = "DUPLICATE_SCENARIO"; return false; }
            found = true;
            switch (args[i + 1])
            {
                case "Normal": scenario = FixtureScenario.Normal; break;
                case "OnePending": scenario = FixtureScenario.OnePending; break;
                case "BothPending": scenario = FixtureScenario.BothPending; break;
                default: reason = "INVALID_SCENARIO"; return false;
            }
        }
        if (!found) { reason = "MISSING_SCENARIO"; return false; }
        profileArgs = remainder.ToArray();
        reason = "SCENARIO_ACCEPTED";
        return true;
    }

    internal bool TryBegin(FixtureScenario actual, bool normalComplete)
    {
        if (invalidated || IsExecuted) return false;
        if (!Enum.IsDefined(actual) || actual != requested)
        {
            invalidated = true;
            return false;
        }
        if (!normalComplete) return false;
        IsExecuted = true;
        return true;
    }

    internal static int DispatchMask(FixtureScenario scenario) => scenario switch
    {
        FixtureScenario.Normal => 0,
        FixtureScenario.OnePending => 1,
        FixtureScenario.BothPending => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
}
