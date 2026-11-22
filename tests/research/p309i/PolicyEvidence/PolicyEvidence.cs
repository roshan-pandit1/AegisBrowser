using AegisBrowser.P309G;

namespace P309i;

// Attribute comparisons are deliberately not a sandbox verdict. The pinned
// default Chromium policy is conditional on its delegate and this experiment
// cannot independently establish role, lockdown timing or complete policy.
public sealed record PolicyAssessment(string RendererSandboxPolicy, string SandboxEnforcement,
    string IntegrityComparison, string RestrictedSidComparison, string Win32kComparison,
    string Reason);

public static class PolicyEvidence
{
    public static PolicyAssessment Assess(TokenObservation? token)
    {
        if (token is null || token.Status != "OBSERVED")
            return new("INCONCLUSIVE", "INCONCLUSIVE", "UNKNOWN", "UNKNOWN", "UNKNOWN", "TOKEN_NOT_OBSERVED");
        string integrity = token.IntegrityRid switch
        {
            null => "UNKNOWN",
            <= 0x1000 => "LOW_OR_LOWER_OBSERVED",
            _ => "ABOVE_LOW_OBSERVED"
        };
        string restricted = token.RestrictingSidCount switch
        {
            null => "UNKNOWN",
            > 0 => "RESTRICTING_SIDS_OBSERVED",
            _ => "NO_RESTRICTING_SIDS_OBSERVED"
        };
        string win32k = token.Win32kSystemCallsDisabled switch
        {
            null => "UNKNOWN",
            true => "DISABLED_OBSERVED",
            false => "NOT_DISABLED_OBSERVED"
        };
        return new("INCONCLUSIVE", "INCONCLUSIVE", integrity, restricted, win32k,
            "ROLE_LOCKDOWN_TIMING_AND_COMPLETE_POLICY_NOT_INDEPENDENTLY_ESTABLISHED");
    }
}
