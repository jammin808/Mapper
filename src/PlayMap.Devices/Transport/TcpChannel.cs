using System.Net.Sockets;
using System.Text;

namespace PlayMap.Devices.Transport;

public enum DeviceIoKind
{
    /// <summary>The connection could not be opened (refused, unreachable, timed out connecting).</summary>
    Connect,

    /// <summary>The projector said nothing for the reply timeout.</summary>
    Timeout,

    /// <summary>The projector closed the connection before a whole reply.</summary>
    Closed,

    /// <summary>The projector answered something that is not this protocol.</summary>
    Protocol,
}

/// <summary>A device conversation failed. The message is words for the desk and never carries a secret: it is
/// built from the host, the port and the failure, nothing the driver was sent with.</summary>
public sealed class DeviceIoException(DeviceIoKind kind, string message) : Exception(message)
{
    public DeviceIoKind Kind { get; } = kind;
}

/// <summary>
/// One TCP conversation with a projector: connect with a timeout, write, and read frames (to a delimiter, an exact
/// count, or one balanced JSON object) with a timeout each. Owns its socket; disposing closes it. Not thread-safe —
/// the manager runs one conversation per projector at a time.
/// </summary>
public sealed class TcpChannel : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _endpoint;
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    private TcpChannel(TcpClient client, string endpoint)
    {
        _client = client;
        _stream = client.GetStream();
        _endpoint = endpoint;
    }

    /// <summary>The bytes of a command (not a handshake) were written: a later timeout means "sent, no answer",
    /// not "unreached".</summary>
    public bool CommandWritten { get; private set; }

    public string Endpoint => _endpoint;

    public static async Task<TcpChannel> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var endpoint = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return new TcpChannel(client, endpoint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new DeviceIoException(DeviceIoKind.Connect, $"no connection to {endpoint} within {Seconds(timeout)}");
        }
        catch (SocketException e)
        {
            client.Dispose();
            throw new DeviceIoException(DeviceIoKind.Connect, $"no connection to {endpoint} — {SocketWords(e.SocketErrorCode)}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Writes handshake bytes (a greeting answer, a digest, a CONNECT). Never logged by anyone.</summary>
    public Task WriteHandshakeAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => WriteAsync(bytes, false, ct);

    /// <summary>Writes a command's bytes.</summary>
    public Task WriteCommandAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => WriteAsync(bytes, true, ct);

    public Task WriteCommandAsync(string ascii, CancellationToken ct) => WriteAsync(Encoding.ASCII.GetBytes(ascii), true, ct);

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, bool command, CancellationToken ct)
    {
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            if (command)
                CommandWritten = true;
        }
        catch (IOException)
        {
            throw new DeviceIoException(DeviceIoKind.Closed, $"{_endpoint} closed the connection while we wrote");
        }
    }

    /// <summary>The text up to (not including) <paramref name="terminator"/>, read as Latin-1 so no byte is lost.</summary>
    public async Task<string> ReadUntilAsync(byte terminator, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = Deadline(timeout, ct);
        while (true)
        {
            var i = Array.IndexOf(_buffer, terminator, _start, _end - _start);
            if (i >= 0)
            {
                var text = Encoding.Latin1.GetString(_buffer, _start, i - _start);
                _start = i + 1;
                return text;
            }
            await FillAsync(timeout, cts.Token, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Exactly <paramref name="count"/> bytes.</summary>
    public async Task<byte[]> ReadExactlyAsync(int count, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = Deadline(timeout, ct);
        while (_end - _start < count)
            await FillAsync(timeout, cts.Token, ct).ConfigureAwait(false);
        var bytes = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        return bytes;
    }

    /// <summary>One balanced JSON object ("{…}", braces inside strings ignored): JSON-RPC over TCP with no
    /// delimiter between messages, as Barco's Pulse sends it.</summary>
    public async Task<string> ReadJsonObjectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = Deadline(timeout, ct);
        while (true)
        {
            // Rescanned from the buffer's start after each read: a fill may compact the buffer, and messages are
            // small enough that a rescan costs nothing.
            var depth = 0;
            var inString = false;
            var escaped = false;
            var begin = -1;
            for (var i = _start; i < _end; i++)
            {
                var c = (char)_buffer[i];
                if (begin < 0)
                {
                    if (c == '{')
                    {
                        begin = i;
                        depth = 1;
                    }
                    continue;
                }
                if (inString)
                {
                    if (escaped)
                        escaped = false;
                    else if (c == '\\')
                        escaped = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                if (c == '"')
                    inString = true;
                else if (c == '{')
                    depth++;
                else if (c == '}' && --depth == 0)
                {
                    var text = Encoding.UTF8.GetString(_buffer, begin, i - begin + 1);
                    _start = i + 1;
                    return text;
                }
            }
            await FillAsync(timeout, cts.Token, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whatever arrives within <paramref name="quiet"/> (a protocol that answers a good set with silence
    /// and a bad one with an error); "" when nothing came.</summary>
    public async Task<string> ReadWhatArrivesAsync(TimeSpan quiet, CancellationToken ct)
    {
        if (_end > _start)
            return TakeBuffered();
        using var cts = Deadline(quiet, ct);
        try
        {
            await FillAsync(quiet, cts.Token, ct).ConfigureAwait(false);
        }
        catch (DeviceIoException e) when (e.Kind == DeviceIoKind.Timeout)
        {
            return "";
        }
        return TakeBuffered();
    }

    private string TakeBuffered()
    {
        var text = Encoding.Latin1.GetString(_buffer, _start, _end - _start);
        _start = _end;
        return text;
    }

    private async Task FillAsync(TimeSpan timeout, CancellationToken deadline, CancellationToken caller)
    {
        if (_start > 0 && _start == _end)
            _start = _end = 0;
        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                if (_buffer.Length >= 1 << 20)
                    throw new DeviceIoException(DeviceIoKind.Protocol, $"{_endpoint} sent more than 1 MiB without a frame end");
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }
        int n;
        try
        {
            n = await _stream.ReadAsync(_buffer.AsMemory(_end), deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new DeviceIoException(DeviceIoKind.Timeout, $"no answer from {_endpoint} within {Seconds(timeout)}");
        }
        catch (IOException)
        {
            throw new DeviceIoException(DeviceIoKind.Closed, $"{_endpoint} closed the connection");
        }
        if (n == 0)
            throw new DeviceIoException(DeviceIoKind.Closed, $"{_endpoint} closed the connection");
        _end += n;
    }

    private static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static string Seconds(TimeSpan t) =>
        t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";

    private static string SocketWords(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => "connection refused (is network control on, and the port right?)",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "host unreachable",
        SocketError.TimedOut => "timed out",
        SocketError.HostNotFound => "host not found",
        _ => e.ToString(),
    };

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
