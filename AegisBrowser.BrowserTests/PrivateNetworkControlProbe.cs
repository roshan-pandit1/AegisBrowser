using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test-only client. Its destination must be an operator-authorized RFC1918
    // literal for a pre-provisioned isolated VM network; no DNS or forwarding.
    private static int PrivateNetworkControlProbe(string root)
    {
        root = Path.GetFullPath(root);
        try
        {
            var control = Path.Combine(root, "private-network-control.txt");
            var info = new FileInfo(control);
            if (!info.Exists || info.Length > 256 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid synthetic control file.");
            var lines = File.ReadAllLines(control);
            if (lines.Length != 3 || !IPAddress.TryParse(lines[0], out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != lines[0] ||
                !IsPrivateTestAddress(address) || !ushort.TryParse(lines[1], out var port) || port < 1024 ||
                port.ToString() != lines[1] || !Guid.TryParseExact(lines[2], "N", out var nonce) ||
                nonce.ToString("N") != lines[2])
                throw new InvalidDataException("Invalid synthetic private endpoint or nonce.");

            using var socket = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            socket.ConnectAsync(address, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.GetStream().Write(Encoding.ASCII.GetBytes(lines[2] + "\n"));
            File.WriteAllText(Path.Combine(root, "private-network-result.txt"), "sent");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or UnauthorizedAccessException or OperationCanceledException)
        {
            var socketError = ex as SocketException ?? ex.InnerException as SocketException;
            var result = socketError is null ? "unavailable:" + ex.GetType().Name : "socket-error:" + (int)socketError.SocketErrorCode;
            try { File.WriteAllText(Path.Combine(root, "private-network-result.txt"), result); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            return 1;
        }
    }

    private static bool IsPrivateTestAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }
}
