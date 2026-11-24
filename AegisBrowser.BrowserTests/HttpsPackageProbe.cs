using System.Diagnostics;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // This helper is a disposable, hosted-runner-only server for the actual
    // published/extracted application. It uses the same narrowly scoped root
    // manifest and exact-certificate cleanup as the existing HTTPS fixture.
    private static int HttpsPackageServer(string trustDirectory, string controlDirectory)
    {
        try
        {
            DisposableRunnerTrust.RequireHostedRunner();
            var runner = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
            var control = Path.GetFullPath(controlDirectory);
            if (!control.StartsWith(Path.TrimEndingDirectorySeparator(runner) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !Directory.Exists(control) ||
                (File.GetAttributes(runner) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(control) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The HTTPS package control directory must be an existing non-linked directory under RUNNER_TEMP.");

            using var certificates = new PrivacyCertificates();
            using var trust = new DisposableRunnerTrust(certificates.Root, trustDirectory, certificates.Id);
            var remaining = 96;
            void Trace(string stage, string detail = "none")
            {
                if (Interlocked.Decrement(ref remaining) >= 0)
                    Console.WriteLine($"PACKAGE_HTTPS qpc={Stopwatch.GetTimestamp()} source=helper stage={stage} detail={detail}");
            }
            using var server = new PrivacyHttpsServer(certificates.Server, trace: Trace);
            Trace("listener_ready", server.Port.ToString());
            // Publish the URL only after the test root and listener are ready.
            PublishMarker(control, "ready.txt", server.TopA + "rc1-package-https");
            var watch = Stopwatch.StartNew();
            var observed = false;
            var pageObserved = false;
            var subresourceObserved = false;
            while (watch.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (!pageObserved && server.Take("/rc1-package-https").Length > 0)
                {
                    pageObserved = true;
                    Trace("main_observed");
                }
                if (!subresourceObserved && server.Take("/rc1-package-rendered").Length > 0)
                {
                    subresourceObserved = true;
                    Trace("image_observed");
                }
                if (!observed && pageObserved && subresourceObserved)
                {
                    observed = true;
                    PublishMarker(control, "observed.txt", "https-request-observed");
                    Trace("marker_published");
                }
                if (File.Exists(Path.Combine(control, "stop.txt")))
                {
                    Trace("shutdown_requested");
                    if (!observed) throw new InvalidOperationException("The controlled HTTPS request was not observed.");
                    Console.WriteLine("PASS: controlled HTTPS server observed the package navigation");
                    return 0;
                }
                Thread.Sleep(50);
            }
            throw new TimeoutException("The controlled HTTPS package server timed out.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void PublishMarker(string directory, string name, string content)
    {
        var target = Path.Combine(directory, name);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, target);
    }
}
