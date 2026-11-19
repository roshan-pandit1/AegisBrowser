using System.Diagnostics;

namespace P0ManagedHost;

// Bounded browser-side observation times, not Chromium commit timestamps or
// authenticated renderer identity. The controlled fixture never logs URLs.
internal sealed class ProbeTimeline
{
    internal enum Stage { FrameLoadStart, FrameLoadEnd, FrameLoadError, HttpsRequested, TraceBegin, TraceEnd }
    private readonly object sync = new();
    private int count;
    private const int Limit = 64;

    internal void Record(Stage stage, int browserId, string? frameId)
    {
        lock (sync)
        {
            if (count >= Limit) return;
            ++count;
            string frame = IsSafeFrameId(frameId) ? frameId! : "UNAVAILABLE";
            Console.WriteLine($"MANAGED_TIMELINE: seq={count} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} browser={browserId} frame={frame} stage={stage}");
        }
    }

    private static bool IsSafeFrameId(string? id) => id is { Length: > 0 and <= 96 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
