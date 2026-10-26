using System.Diagnostics;
using System.Text.Json;
using AegisBrowser.Core;

internal static class FilteringBenchmarks
{
    internal static void Run()
    {
        foreach (var generic in new[] { false, true })
        {
            var text = string.Join('\n', Enumerable.Range(0, 10_000).Select(i => generic ? "/tracker" + i + "/pixel" : "||tracker" + i + ".test^"));
            var started = Stopwatch.GetTimestamp();
            var filter = new RequestFilter(new NetworkFilterList(text));
            var compileMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var request = new NetworkRequest("https://tracker9999.test/pixel", "https://example.com", "image");
            for (var i = 0; i < 10; i++) filter.Evaluate(request);
            var samples = new double[100]; var fallbacks = 0;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples.Length; i++)
            {
                started = Stopwatch.GetTimestamp();
                var decision = filter.Evaluate(request);
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (decision.Fallback.HasFlag(FilterFallback.BudgetExceeded)) fallbacks++;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(samples);
            Console.WriteLine(JsonSerializer.Serialize(new { Scenario = generic ? "generic" : "hostname-indexed", Rules = 10000,
                CompileMs = compileMs, MedianMs = samples[50], P95Ms = samples[95], AllocatedBytes = allocated, BudgetFallbacks = fallbacks,
                Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.Platform.ToString() }));
        }
    }
}
