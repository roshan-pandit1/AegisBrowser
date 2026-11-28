using System;
using AegisBrowser.WebView2Feasibility;

// Pure argument checks; never creates or inspects a directory.
internal static class ProfileRootPolicyTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value)
        {
            count++;
            if (!value) throw new InvalidOperationException("Profile configuration regression " + count);
        }
        bool Valid(params string[] args) => ProfileRootPolicy.TryParse(args, out _, out _);
        string[] Pair(string profile, string timeout) => new[] { "--profile-root", profile, "--shutdown-timeout-ms", timeout };
        const string root = @"C:\AegisLab\Profiles";

        Check(!ProfileRootPolicy.TryParse(null, out _, out _));
        Check(!Valid());
        Check(!Valid("--profile-root", root));
        Check(Valid(Pair(root, "5000")));
        Check(Valid("--shutdown-timeout-ms", "1", "--profile-root", root));
        Check(Valid(Pair(@"c:\aegislab\profiles", "2147483647")));
        Check(!Valid("--profile-root", root, "--profile-root", root));
        Check(!Valid("--profile-root", root, "--other", "1"));
        Check(!Valid(Pair(@"\\host\share", "10")));
        Check(!Valid(Pair(@"\\?\C:\AegisLab\Profiles", "10")));
        Check(!Valid(Pair(@"C:AegisLab\Profiles", "10")));
        Check(!Valid(Pair(@"C:\AegisLab\Profiles:stream", "10")));
        Check(!Valid(Pair(@"C:\AegisLab\other\..\Profiles", "10")));
        Check(!Valid(Pair(@"C:\Users\someone\Edge", "10")));
        Check(!Valid(Pair(root + " ", "10")));
        Check(!Valid(Pair("", "10")));
        Check(!Valid(Pair(root, "0")));
        Check(!Valid(Pair(root, "-1")));
        Check(!Valid(Pair(root, "+1")));
        Check(!Valid(Pair(root, " 1")));
        Check(!Valid(Pair(root, "2147483648")));
        Check(!Valid(Pair(root, "text")));
        Check(!Valid("--profile-root", root, "--shutdown-timeout-ms", "1", "extra"));
        Check(ProfileRootPolicy.TryParse(Pair(root, "5000"), out var config, out _) &&
            config.ProfileRoot == root && config.ShutdownTimeoutMilliseconds == 5000);
        Check(!ProfileRootPolicy.TryParse(Pair(@"C:\AegisLab\Other", "5000"), out _, out var rootReason) && rootReason == "PROFILE_ROOT_NOT_ALLOWED");
        Check(!ProfileRootPolicy.TryParse(Pair(root, "0"), out _, out var timeoutReason) && timeoutReason == "INVALID_SHUTDOWN_TIMEOUT");
        Check(!ProfileRootPolicy.TryParse(Array.Empty<string>(), out _, out var argsReason) && argsReason == "INVALID_ARGUMENTS");
        return count;
    }
}
