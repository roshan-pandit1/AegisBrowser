namespace AegisBrowser;

// A warning is not a failed release. Keep observing the same native close until
// it actually finishes; callers must not dispose the owner before that happens.
internal static class NativeCloseWaiter
{
    internal static async Task WaitAsync(Task release, TimeSpan warningAfter, Action onDelayed)
    {
        using var timer = new CancellationTokenSource();
        var warning = Task.Delay(warningAfter, timer.Token);
        if (await Task.WhenAny(release, warning) == warning && !release.IsCompleted)
            onDelayed();
        else
            timer.Cancel();
        await release;
    }
}
