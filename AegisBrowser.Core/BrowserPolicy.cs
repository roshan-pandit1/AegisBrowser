namespace AegisBrowser.Core;

public static class BrowserPolicy
{
    public const string DiagnosticsUrl = "https://aegis-diagnostics.invalid/";

    public static string ReadProfile(string[] args)
    {
        if (args.Length == 0) return "default";
        if (args.Length != 1 || !args[0].StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Usage: AegisBrowser.exe [--profile=NAME]");
        var name = args[0][10..];
        if (name.Length is < 1 or > 40 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Profile names require 1–40 ASCII letters, digits, hyphens or underscores.");
        return name.ToLowerInvariant();
    }

    public static string ProfilePath(string root, string profile) =>
        Path.Combine(root, "profile-" + ReadProfile(["--profile=" + profile]));

    public static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    // Compare parsed origins, never suffixes or display strings; ports remain significant.
    public static string? PermissionOrigin(string value)
    {
        if (!IsWebUrl(value)) return null;
        var uri = new Uri(value);
        if (uri.Scheme != "https" && !uri.IsLoopback) return null;
        if (uri.Host.Equals("aegis-diagnostics.invalid", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool CanPromptPermission(string requestingUrl, string topLevelUrl)
    {
        var origin = PermissionOrigin(requestingUrl);
        return origin is not null && origin == PermissionOrigin(topLevelUrl);
    }

    public static string DisplayOrigin(string url) =>
        IsWebUrl(url) ? new Uri(url).GetLeftPart(UriPartial.Authority) : "unavailable origin";

    public static string? NormalizeAddress(string input)
    {
        input = input.Trim();
        if (input.Any(char.IsWhiteSpace) || input.Length == 0) return null;
        if (input.Equals("aegis:diagnostics", StringComparison.OrdinalIgnoreCase)) return DiagnosticsUrl;
        if (IsWebUrl(input)) return new Uri(input).AbsoluteUri;
        if (input.Contains("://") || input.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;
        return IsWebUrl("https://" + input) ? new Uri("https://" + input).AbsoluteUri : null;
    }
}

public sealed class ProfileLease : IDisposable
{
    private readonly FileStream stream;
    private readonly string profilePath;
    private readonly object sync = new();
    private readonly List<Action> revocations = [];
    private Action[]? deferredRevocations;
    private int handoffDepth;
    private bool disposed;
    public ProfileLease(string path)
    {
        profilePath = Path.GetFullPath(path);
        RejectLink(profilePath);
        Directory.CreateDirectory(profilePath);
        RejectLink(profilePath);
        var lockPath = Path.Combine(profilePath, ".profile.lock");
        RejectLink(lockPath);
        // The OS releases the exclusive handle even following a crash. Do not delete the lock file.
        // A read-only dashboard probe may briefly hold this same exclusive handle.
        // Retry short sharing conflicts; a real running browser still retains the lease.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && deadline.ElapsedMilliseconds < 500)
            { Thread.Sleep(10); RejectLink(lockPath); }
        }
    }
    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked profile and lock paths are not supported: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    internal bool IsHeldFor(string path)
    {
        lock (sync) return !disposed && string.Equals(profilePath, Path.GetFullPath(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // This is only used for the synchronous CEF auth handoff. The pinned CEF
    // callback posts its continuation to the UI thread; it does not wait for it.
    // It does not revoke credentials already queued or cached by Chromium.
    internal bool TryInvokeWhileHeld(Action action)
    {
        Action[]? deferred = null;
        Exception? handoffFailure = null;
        lock (sync)
        {
            if (disposed) return false;
            handoffDepth++;
            try { action(); }
            catch (Exception ex) { handoffFailure = ex; }
            finally
            {
                handoffDepth--;
                if (handoffDepth == 0) { deferred = deferredRevocations; deferredRevocations = null; }
            }
        }
        try { if (deferred is not null) NotifyRevocations(deferred); }
        catch (Exception ex) when (handoffFailure is not null)
        { throw new AggregateException("Credential handoff and profile revocation failed.", handoffFailure, ex); }
        if (handoffFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(handoffFailure).Throw();
        return true;
    }

    internal IDisposable RegisterRevocation(Action action)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ProfileLease));
            revocations.Add(action);
            return new Registration(this, action);
        }
    }

    private sealed class Registration(ProfileLease owner, Action action) : IDisposable
    {
        private WeakReference<ProfileLease>? ownerReference = new(owner);
        public void Dispose()
        {
            var reference = Interlocked.Exchange(ref ownerReference, null);
            if (reference is null || !reference.TryGetTarget(out var owner)) return;
            lock (owner.sync) owner.revocations.Remove(action);
        }
    }

    public void Dispose()
    {
        Action[]? pending;
        Exception? closeFailure = null;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = revocations.ToArray();
            revocations.Clear();
            try { stream.Dispose(); }
            catch (Exception ex) { closeFailure = ex; }
            // Reentrant disposal can occur from a callback already executing
            // under this gate. Defer session callbacks until the outermost
            // handoff has released it; otherwise another session could invert
            // the session/lease lock order.
            if (handoffDepth != 0) { deferredRevocations = pending; pending = null; }
        }
        // Never wait for a session gate while holding the lease gate.
        try { if (pending is not null) NotifyRevocations(pending); }
        catch (Exception ex) when (closeFailure is not null)
        { throw new AggregateException("Profile lease disposal failed.", closeFailure, ex); }
        if (closeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
    }

    private static void NotifyRevocations(Action[] pending)
    {
        List<Exception>? failures = null;
        foreach (var revoke in pending)
        {
            try { revoke(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null) throw new AggregateException("Profile lease disposal failed.", failures);
    }
}
