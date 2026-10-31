namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Used only by the concurrent native fixture; ordinary probe runs do not wait.
    private static async Task AwaitConcurrentProxyProbe(string root)
    {
        var barrier = Environment.GetEnvironmentVariable("AEGIS_TEST_PROXY_BARRIER");
        if (string.IsNullOrEmpty(barrier)) return;
        var role = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var peer = role switch { "cef-a" => "cef-b", "cef-b" => "cef-a", _ => throw new IOException("Invalid concurrent proxy fixture role.") };
        var directory = Path.GetFullPath(barrier);
        if (!Directory.Exists(directory)) throw new IOException("Concurrent proxy fixture barrier is missing.");
        var own = Path.Combine(directory, role + ".ready");
        using (var marker = new FileStream(own, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { marker.WriteByte(1); marker.Flush(true); }
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(Path.Combine(directory, peer + ".ready")))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The second CEF proxy fixture did not become ready.");
            await Task.Delay(100);
        }
        Console.WriteLine("PASS: both independent CEF proxy fixtures initialized before routing tests");
    }
}
