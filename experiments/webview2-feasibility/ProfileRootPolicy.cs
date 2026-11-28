using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;

namespace AegisBrowser.WebView2Feasibility;

internal readonly record struct ProfileLaunchConfig(string ProfileRoot, int ShutdownTimeoutMilliseconds);

// Restricts configuration to one proposed lab location. This is a path policy,
// not proof that an environment or runtime operation has human authorization.
internal static class ProfileRootPolicy
{
    internal const string ProposedLabRoot = @"C:\AegisLab\Profiles";

    internal static bool TryParse(string[]? args, out ProfileLaunchConfig config, out string reason)
    {
        config = default;
        reason = "INVALID_ARGUMENTS";
        if (args is null || args.Length != 4) return false;

        string? root = null;
        string? timeout = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] == "--profile-root" && root is null) root = args[i + 1];
            else if (args[i] == "--shutdown-timeout-ms" && timeout is null) timeout = args[i + 1];
            else return false;
        }

        if (!string.Equals(root, ProposedLabRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "PROFILE_ROOT_NOT_ALLOWED";
            return false;
        }
        if (!int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
        {
            reason = "INVALID_SHUTDOWN_TIMEOUT";
            return false;
        }
        config = new ProfileLaunchConfig(ProposedLabRoot, milliseconds);
        reason = "CONFIGURATION_ACCEPTED";
        return true;
    }

    // Intended only during an explicitly authorized run. Checks are
    // non-atomic: ancestor replacement and same-user races remain possible.
    internal static bool ValidateExistingRoot(ProfileLaunchConfig config, out string reason)
    {
        reason = "PROFILE_ROOT_UNAVAILABLE";
        if (!string.Equals(config.ProfileRoot, ProposedLabRoot, StringComparison.OrdinalIgnoreCase) ||
            config.ShutdownTimeoutMilliseconds <= 0) return false;
        try
        {
            var root = Path.GetPathRoot(config.ProfileRoot);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var ancestors = new Stack<DirectoryInfo>();
            for (DirectoryInfo? current = new DirectoryInfo(config.ProfileRoot); current is not null; current = current.Parent)
                ancestors.Push(current);
            while (ancestors.Count > 0)
            {
                var current = ancestors.Pop();
                var attributes = File.GetAttributes(current.FullName);
                if ((attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "PROFILE_ROOT_UNSAFE";
                    return false;
                }
            }
            reason = "PROFILE_ROOT_CHECKED";
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
