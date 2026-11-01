using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Synthetic process only: this mode does not initialize CEF or contact non-loopback destinations.
    private static int PacketControlProbe(string root)
    {
        root = Path.GetFullPath(root);
        try
        {
            var lines = File.ReadAllLines(Path.Combine(root, "packet-control.txt"));
            if (lines.Length != 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                lines[1].Length != 32 || !Guid.TryParseExact(lines[1], "N", out _))
                throw new InvalidDataException("Invalid synthetic packet control.");
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            var data = Encoding.ASCII.GetBytes(lines[1] + "\n");
            socket.GetStream().Write(data);
            File.WriteAllText(Path.Combine(root, "packet-result.txt"), "sent");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException)
        {
            var socketError = ex as SocketException ?? ex.InnerException as SocketException;
            var result = socketError is null
                ? "unavailable:" + ex.GetType().Name
                : "socket-error:" + (int)socketError.SocketErrorCode;
            try { File.WriteAllText(Path.Combine(root, "packet-result.txt"), result); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            return 1;
        }
    }
}
