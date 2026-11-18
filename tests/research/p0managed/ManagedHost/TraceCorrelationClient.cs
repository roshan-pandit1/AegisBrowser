using System.IO.Pipes;
using System.Text;

namespace P0ManagedHost;

// Test-only client. A successful exchange does not prove renderer identity;
// the supervisor must independently authenticate this browser connection and
// check a previously retained child handle and its lifetime.
internal sealed class TraceCorrelationClient : IDisposable
{
    private const string Prefix = @"\\.\pipe\aegis-p0trace-";
    private static readonly byte[] Hello = Encoding.ASCII.GetBytes("P0TRACE1 HELLO\n");
    private static readonly byte[] Ack = Encoding.ASCII.GetBytes("P0TRACE1 ACK\n");
    private static readonly byte[] Received = Encoding.ASCII.GetBytes("P0TRACE1 RECEIVED\n");
    private readonly NamedPipeClientStream pipe;
    private TraceCorrelationClient(NamedPipeClientStream pipe) => this.pipe = pipe;

    internal static async Task<TraceCorrelationClient> ConnectAsync()
    {
        string? path = Environment.GetEnvironmentVariable("AEGIS_P0_TRACE_PIPE");
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal) ||
            path.Length != Prefix.Length + 32 || !path.AsSpan(Prefix.Length).ToString().All(IsLowerHex))
            throw new InvalidDataException("TRACE_PIPE_NAME_INVALID");
        var pipe = new NamedPipeClientStream(".", path.Substring(@"\\.\pipe\".Length),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(Hello, timeout.Token);
            await ReadExpectedAsync(pipe, Ack, timeout.Token);
            return new TraceCorrelationClient(pipe);
        }
        catch { pipe.Dispose(); throw; }
    }

    internal async Task SendAsync(int pid, string frame)
    {
        byte[] payload = Encode(pid, frame);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WriteAsync(payload, timeout.Token);
        await ReadExpectedAsync(pipe, Received, timeout.Token);
    }

    internal static byte[] Encode(int pid, string frame)
    {
        if (pid <= 0 || frame is null || frame.Length != 32 || !frame.All(IsUpperHex))
            throw new InvalidDataException("TRACE_CORRELATION_PAYLOAD_INVALID");
        return Encoding.ASCII.GetBytes("P0TRACE1 " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + frame + "\n");
    }

    private static async Task ReadExpectedAsync(Stream stream, byte[] expected, CancellationToken cancellation)
    {
        var received = new byte[expected.Length];
        int offset = 0;
        while (offset < received.Length)
        {
            int count = await stream.ReadAsync(received.AsMemory(offset), cancellation);
            if (count == 0) throw new EndOfStreamException("TRACE_PIPE_CLOSED");
            offset += count;
        }
        if (!received.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("TRACE_PIPE_REPLY_INVALID");
    }

    private static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool IsUpperHex(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'F';
    public void Dispose() => pipe.Dispose();
}
