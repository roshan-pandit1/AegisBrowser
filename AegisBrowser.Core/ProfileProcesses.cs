using System.Diagnostics;

namespace AegisBrowser.Core;

public enum ProfileActivity { Stopped, Starting, Running, InUse, Unknown, Failed, Missing }
public sealed record ProfileStatus(ProfileActivity Activity, string Detail = "")
{
    public bool IsActive => Activity is ProfileActivity.Starting or ProfileActivity.Running or ProfileActivity.InUse;
}

public interface IProfileLeaseProbe { ProfileStatus Inspect(string slug); }
public interface IProfileChild : IDisposable { bool HasExited { get; } int ExitCode { get; } }
public interface IProfileChildStarter { IProfileChild Start(string slug); }

/// <summary>Read-only observation of the existing OS lease; never creates or removes a lock.</summary>
public sealed class ProfileLeaseProbe(string root) : IProfileLeaseProbe
{
    public ProfileStatus Inspect(string slug)
    {
        try
        {
            slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
            ProfileRepository.CheckSafeProfilePath(root, slug);
            var directory = BrowserPolicy.ProfilePath(root, slug);
            if (!Directory.Exists(directory)) return new(ProfileActivity.Missing, "Profile directory does not exist.");
            var path = Path.Combine(directory, ".profile.lock");
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return new(ProfileActivity.Unknown, "The profile lock path is linked; inspect it before launching.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return new(ProfileActivity.Stopped);
            }
            catch (FileNotFoundException) { return new(ProfileActivity.Stopped); }
            catch (DirectoryNotFoundException) { return new(ProfileActivity.Missing, "Profile directory does not exist."); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { return new(ProfileActivity.InUse, "The profile lease is held by another process."); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(ProfileActivity.Unknown, "Could not inspect the profile: " + ex.Message); }
    }
}

/// <summary>Starts the application with only its validated profile argument.</summary>
public sealed class ProfileChildStarter(string executable, string applicationAssembly, string workingDirectory) : IProfileChildStarter
{
    public IProfileChild Start(string slug)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(applicationAssembly);
        start.ArgumentList.Add("--profile=" + slug);
        return new Child(Process.Start(start) ?? throw new IOException("Could not launch the browser process."));
    }

    private sealed class Child(Process process) : IProfileChild
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Dispose() => process.Dispose();
    }
}

/// <summary>Observes owned children and the shared profile lease. It never terminates a process.</summary>
public sealed class ProfileProcessSupervisor(IProfileLeaseProbe probe, IProfileChildStarter starter) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, IProfileChild> children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public ProfileStatus GetStatus(string slug)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var alive = false;
            if (children.TryGetValue(slug, out var child))
            {
                try
                {
                    if (child.HasExited)
                    {
                        var code = child.ExitCode;
                        children.Remove(slug);
                        child.Dispose();
                        if (code != 0) failures[slug] = code;
                    }
                    else alive = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                { return new(ProfileActivity.Unknown, "Could not inspect the launched process."); }
            }
            var lease = probe.Inspect(slug);
            if (alive)
                return lease.Activity switch
                {
                    ProfileActivity.InUse => new(ProfileActivity.Running, "A launched process is active and the profile lease is held; lease ownership is not independently verified."),
                    ProfileActivity.Unknown => lease,
                    _ => new(ProfileActivity.Starting, "Waiting for the profile lease. Check the browser for a startup error.")
                };
            if (lease.Activity is ProfileActivity.InUse or ProfileActivity.Unknown or ProfileActivity.Missing) return lease;
            if (failures.TryGetValue(slug, out var exitCode)) return new(ProfileActivity.Failed, "The launched process exited with code " + exitCode + ".");
            return lease;
        }
    }

    public void Launch(string slug)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        lock (gate)
        {
            var status = GetStatus(slug);
            if (status.IsActive) throw new IOException("This profile is already running or starting.");
            if (status.Activity == ProfileActivity.Unknown) throw new IOException(status.Detail);
            var child = starter.Start(slug);
            failures.Remove(slug);
            children.Add(slug, child);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var child in children.Values) child.Dispose();
            children.Clear();
        }
    }
}
