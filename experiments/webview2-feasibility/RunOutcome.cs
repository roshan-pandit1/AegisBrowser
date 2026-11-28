using System;

namespace AegisBrowser.WebView2Feasibility;

// A timeout is recorded but does not terminate a process or native operation.
// These codes become observable only when Main and the message loop return.
internal enum PrototypeExitCode
{
    Normal = 0,
    InvalidArguments = 2,
    InvalidProfileRoot = 3,
    InvalidShutdownTimeout = 4,
    InitializationFailure = 5,
    NavigationTimeout = 6,
    ShutdownQuiescenceTimeout = 7,
    DisposalFailure = 8,
    InternalFailure = 9,
    DiagnosticsIncomplete = 10
}

internal sealed class RunOutcome
{
    private PrototypeExitCode code;
    internal PrototypeExitCode Code => code;
    internal void Record(PrototypeExitCode value)
    {
        if (value < PrototypeExitCode.Normal || value > PrototypeExitCode.DiagnosticsIncomplete)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (value > code) code = value;
    }

    internal static PrototypeExitCode ForConfigurationReason(string reason) => reason switch
    {
        "PROFILE_ROOT_NOT_ALLOWED" or "PROFILE_ROOT_UNAVAILABLE" or "PROFILE_ROOT_UNSAFE" => PrototypeExitCode.InvalidProfileRoot,
        "INVALID_SHUTDOWN_TIMEOUT" => PrototypeExitCode.InvalidShutdownTimeout,
        _ => PrototypeExitCode.InvalidArguments
    };
}
