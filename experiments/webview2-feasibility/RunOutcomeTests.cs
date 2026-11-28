using System;
using AegisBrowser.WebView2Feasibility;

internal static class RunOutcomeTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Outcome regression " + (count + 1)); count++; }
        Check(RunOutcome.ForConfigurationReason("INVALID_ARGUMENTS") == PrototypeExitCode.InvalidArguments);
        Check(RunOutcome.ForConfigurationReason("PROFILE_ROOT_NOT_ALLOWED") == PrototypeExitCode.InvalidProfileRoot);
        Check(RunOutcome.ForConfigurationReason("PROFILE_ROOT_UNAVAILABLE") == PrototypeExitCode.InvalidProfileRoot);
        Check(RunOutcome.ForConfigurationReason("PROFILE_ROOT_UNSAFE") == PrototypeExitCode.InvalidProfileRoot);
        Check(RunOutcome.ForConfigurationReason("INVALID_SHUTDOWN_TIMEOUT") == PrototypeExitCode.InvalidShutdownTimeout);
        var outcome = new RunOutcome();
        Check(outcome.Code == PrototypeExitCode.Normal);
        outcome.Record(PrototypeExitCode.NavigationTimeout);
        Check(outcome.Code == PrototypeExitCode.NavigationTimeout);
        outcome.Record(PrototypeExitCode.Normal);
        Check(outcome.Code == PrototypeExitCode.NavigationTimeout);
        outcome.Record(PrototypeExitCode.DisposalFailure);
        Check(outcome.Code == PrototypeExitCode.DisposalFailure);
        return count;
    }
}
