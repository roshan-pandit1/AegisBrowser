using AegisBrowser.Core;

// Synthetic lease callbacks; these do not execute CEF or prove network revocation.
internal static class ProfileLeaseRevocationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aegis-lease-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lease = new ProfileLease(root);
            var notified = 0;
            using var first = lease.RegisterRevocation(() => throw new InvalidOperationException("Synthetic revocation failure."));
            using var second = lease.RegisterRevocation(() => notified++);
            AggregateException? result = null;
            try
            {
                lease.TryInvokeWhileHeld(() =>
                {
                    lease.Dispose(); // reentrant disposal defers notifications
                    throw new ApplicationException("Synthetic handoff failure.");
                });
            }
            catch (AggregateException ex) { result = ex.Flatten(); }
            if (notified != 1 || result is null ||
                !result.InnerExceptions.Any(x => x is InvalidOperationException) ||
                !result.InnerExceptions.Any(x => x is ApplicationException))
                throw new Exception("Profile lease must notify all registrations and retain both errors.");
            using var reopened = new ProfileLease(root);
            Console.WriteLine("PASS: profile lease preserves handoff and revocation failures and releases its lock");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
