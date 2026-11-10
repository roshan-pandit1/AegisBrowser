using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Confirms only the Windows Root-store dialog belonging to the test-owned
    // certutil process and naming the exact just-generated test certificate.
    // This is used only after RequireHostedRunner and never on user machines.
    private static class RunnerTrustConfirmation
    {
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int maxCount);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr SendMessageTimeoutW(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr SendTextMessage(IntPtr window, uint message, IntPtr wParam,
            StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

        private const uint ButtonClick = 0x00F5;
        private const uint GetText = 0x000D;
        private const uint AbortIfHung = 0x0002;
        private const int YesButtonId = 6;

        private static string WindowText(IntPtr window)
        {
            var text = new StringBuilder(4096);
            _ = GetWindowTextW(window, text, text.Capacity);
            if (text.Length == 0)
                _ = SendTextMessage(window, GetText, new IntPtr(text.Capacity), text, AbortIfHung, 1000, out _);
            return text.ToString();
        }

        private static string WindowClass(IntPtr window)
        {
            var name = new StringBuilder(128);
            _ = GetClassNameW(window, name, name.Capacity);
            return name.ToString();
        }

        internal static bool IsExactPrompt(uint owner, uint expectedOwner, string windowClass, string title,
            string content, string subject, string thumbprint, bool yesPresent)
        {
            if (owner != expectedOwner || windowClass != "#32770" || title != "Security Warning" || !yesPresent
                || !content.Contains(subject, StringComparison.Ordinal)) return false;
            var hex = new string(content.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
            return hex.Contains(thumbprint, StringComparison.Ordinal);
        }

        internal static bool WaitForInstaller(Process process, X509Certificate2 certificate)
        {
            DisposableRunnerTrust.RequireHostedRunner();
            var stopwatch = Stopwatch.StartNew();
            var sawOwnedDialog = false; var sawSubject = false; var sawThumbprint = false; var sawYes = false;
            var confirmed = false;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(20) && !process.HasExited)
            {
                EnumWindow visit = (window, ignored) =>
                {
                    _ = GetWindowThreadProcessId(window, out var owner);
                    if (owner != (uint)process.Id || WindowClass(window) != "#32770"
                        || WindowText(window) != "Security Warning") return true;
                    sawOwnedDialog = true;
                    var text = new StringBuilder(WindowText(window));
                    var yes = IntPtr.Zero;
                    EnumWindow child = (control, ignoredChild) =>
                    {
                        _ = GetWindowThreadProcessId(control, out var childOwner);
                        if (childOwner != (uint)process.Id) return true;
                        text.Append(' ').Append(WindowText(control));
                        if (GetDlgCtrlID(control) == YesButtonId && WindowClass(control) == "Button"
                            && WindowText(control).Replace("&", "", StringComparison.Ordinal).Trim() == "Yes")
                            yes = control;
                        return true;
                    };
                    EnumChildWindows(window, child, IntPtr.Zero);
                    var content = text.ToString();
                    var subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    var subjectMatches = content.Contains(subject, StringComparison.Ordinal);
                    var hex = new string(content.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
                    var fingerprintMatches = hex.Contains(certificate.Thumbprint, StringComparison.Ordinal);
                    sawSubject |= subjectMatches; sawThumbprint |= fingerprintMatches; sawYes |= yes != IntPtr.Zero;
                    if (!IsExactPrompt(owner, (uint)process.Id, WindowClass(window), WindowText(window),
                        content, subject, certificate.Thumbprint, yes != IntPtr.Zero)) return true;
                    if (SendMessageTimeoutW(yes, ButtonClick, IntPtr.Zero, IntPtr.Zero, AbortIfHung, 3000, out _) != IntPtr.Zero)
                    {
                        confirmed = true;
                        return false;
                    }
                    return true;
                };
                EnumWindows(visit, IntPtr.Zero);
                if (confirmed) break;
                Thread.Sleep(100);
            }
            Console.WriteLine($"P403_TRUST_DIALOG: owned={sawOwnedDialog} subject={sawSubject} fingerprint={sawThumbprint} yes={sawYes} confirmed={confirmed}");
            return confirmed;
        }
    }
}
