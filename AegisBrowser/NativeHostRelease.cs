namespace AegisBrowser;

// The caller supplies the native evidence and must invoke this on the WinForms
// thread. A missing callback remains pending and never authorizes disposal.
internal static class NativeHostRelease
{
    internal static async Task RunAsync(Task closeReady, Action destroyHostHandle, Task ownerClosed)
    {
        await closeReady;
        destroyHostHandle();
        await ownerClosed;
    }
}
