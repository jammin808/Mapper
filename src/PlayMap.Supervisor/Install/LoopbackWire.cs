using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlayMap.Supervisor.Install;

/// <summary>One line asked of the engine on this machine's line wire (loopback only), its answer read back: the payload of the
/// <c>OK</c> that carries the line's request id, or the reason of its <c>ERR</c>. Silence — no listener, a closed connection, no
/// answer within the time — is null, never an answer. Pushed lines (STATE, NONCE) and other replies are passed over.</summary>
public static class LoopbackWire
{
    /// <summary>The longest line read; a longer one is no answer.</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    /// <summary>An answer: whether it was OK and its payload or reason.</summary>
    public sealed record Answer(bool Ok, string Payload);

    public static async Task<Answer?> AskAsync(int port, string line, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);
        if (port is < 1 or > 65535)
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            const string id = "pmsup";
            var bytes = Encoding.UTF8.GetBytes("@" + id + " " + line + "\n");
            await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reader = new LineReader(stream);
            while (await reader.ReadAsync(deadline.Token).ConfigureAwait(false) is { } reply)
            {
                if (Reply(reply, "OK", id) is { } ok)
                    return new Answer(true, ok);
                if (Reply(reply, "ERR", id) is { } err)
                    return new Answer(false, err);
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No answer within the time: silence.
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // No listener, or the connection closed: silence.
            return null;
        }
    }

    /// <summary>The rest of <paramref name="reply"/> after "<paramref name="word"/> @<paramref name="id"/>", or null when it is not
    /// that reply.</summary>
    internal static string? Reply(string reply, string word, string id)
    {
        var head = word + " @" + id;
        if (!reply.StartsWith(head, StringComparison.Ordinal))
            return null;
        if (reply.Length == head.Length)
            return "";
        return reply[head.Length] == ' ' ? reply[(head.Length + 1)..] : null;
    }

    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly ArrayBufferWriter<byte> _line = new(256);
        private int _at;
        private int _end;

        public async Task<string?> ReadAsync(CancellationToken ct)
        {
            _line.Clear();
            while (true)
            {
                if (_at == _end)
                {
                    _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                    _at = 0;
                    if (_end == 0)
                        return null;
                }
                var nl = Array.IndexOf(_buffer, (byte)'\n', _at, _end - _at);
                if (nl < 0)
                {
                    _line.Write(_buffer.AsSpan(_at, _end - _at));
                    _at = _end;
                    if (_line.WrittenCount > MaxLineBytes)
                        return null;
                    continue;
                }
                _line.Write(_buffer.AsSpan(_at, nl - _at));
                _at = nl + 1;
                return Encoding.UTF8.GetString(_line.WrittenSpan).TrimEnd('\r');
            }
        }
    }
}
