namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test-only synchronization: the external observer never influences production startup.
    private static async Task AwaitProcessObservation()
    {
        var barrier = Environment.GetEnvironmentVariable("AEGIS_TEST_PROCESS_OBSERVATION_BARRIER");
        if (string.IsNullOrEmpty(barrier)) return;
        var directory = Path.GetFullPath(barrier);
        if (!Directory.Exists(directory)) throw new IOException("Process observation barrier is missing.");
        using (var ready = new FileStream(Path.Combine(directory, "ready"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { ready.WriteByte(1); ready.Flush(true); }
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(Path.Combine(directory, "release")))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The independent process observer did not release the fixture.");
            await Task.Delay(100);
        }
    }
}
