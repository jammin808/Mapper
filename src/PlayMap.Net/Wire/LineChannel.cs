using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace PlayMap.Net.Wire;

/// <summary>One connection's lines, whatever carries them: TCP (a line ends at "\n") or a WebSocket (one text frame is one line).
/// The session reads lines from it one at a time; the session's writer is the only writer of <see cref="Output"/>, and it writes
/// whole lines, each ended by "\n".</summary>
internal interface ILineChannel : IAsyncDisposable
{
    /// <summary>"tcp" or "ws".</summary>
    string Kind { get; }

    IPAddress? Remote { get; }

    /// <summary>Where the session's writer writes whole lines, each ended by "\n".</summary>
    Stream Output { get; }

    /// <summary>The next line; null at the end (the peer closed, or the channel closed itself with its code). A line past the limit
    /// or one that never ends throws <see cref="InvalidDataException"/> with the words the peer is told before the close.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Ends the conversation with a close code where the channel has one (a WebSocket's close frame); on TCP the close
    /// follows the session's end.</summary>
    Task CloseAsync(int code, string words);

    /// <summary>Cuts the connection now, so a pending read wakes.</summary>
    void Abort();
}

/// <summary>The line wire's TCP door: "\n"-ended lines through <see cref="LineReader"/>, written to the socket's stream.</summary>
internal sealed class TcpLineChannel(Socket socket, NetworkStream stream, IPAddress? remote, WireLimits limits) : ILineChannel
{
    private readonly LineReader _reader = new(stream, limits.MaxLineBytes, TimeSpan.FromSeconds(limits.LineSeconds));

    public string Kind => "tcp";

    public IPAddress? Remote => remote;

    public Stream Output => stream;

    public ValueTask<string?> ReadLineAsync(CancellationToken ct) => _reader.ReadLineAsync(ct);

    /// <summary>TCP has no close words: the connection closes gracefully when the session ends, after its last line.</summary>
    public Task CloseAsync(int code, string words) => Task.CompletedTask;

    public void Abort()
    {
        try
        {
            socket.Close(0);
        }
        catch (ObjectDisposedException)
        {
            // Closed already.
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

/// <summary>The line wire over a WebSocket (subprotocol <c>playmap.lines.v1</c>): one UTF-8 text frame is one line of at most 64 KiB
/// (a trailing "\n" is tolerated). A binary frame closes with 1003 and a frame past the limit with 1009 — neither is a command.
/// Every frame the node sends is one line. One sender at a time: the writer's lines and the close frame share one gate.</summary>
internal sealed class WebSocketLineChannel : ILineChannel
{
    public const string SubProtocol = "playmap.lines.v1";
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>The close code for five wrong proofs on one connection.</summary>
    public const int WrongProofsClose = 4401;

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly byte[] _buffer = new byte[MaxLineBytes + 1];
    private int _closing;

    public WebSocketLineChannel(WebSocket socket, IPAddress? remote)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        Remote = remote;
        Output = new FrameStream(this);
    }

    public string Kind => "ws";

    public IPAddress? Remote { get; }

    public Stream Output { get; }

    /// <summary>The close code the channel ended with (0 while open); for the tests and the log.</summary>
    public int ClosedWith { get; private set; }

    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var count = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (count >= _buffer.Length)
                {
                    await CloseAsync((int)WebSocketCloseStatus.MessageTooBig, "a line past 64 KiB is not a command");
                    return null;
                }
                r = await _socket.ReceiveAsync(_buffer.AsMemory(count), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync((int)WebSocketCloseStatus.NormalClosure, "bye");
                    return null;
                }
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    await CloseAsync((int)WebSocketCloseStatus.InvalidMessageType, "lines are text frames");
                    return null;
                }
                count += r.Count;
            }
            while (!r.EndOfMessage);
            if (count > MaxLineBytes)
            {
                await CloseAsync((int)WebSocketCloseStatus.MessageTooBig, "a line past 64 KiB is not a command");
                return null;
            }
            var length = count;
            if (length > 0 && _buffer[length - 1] == (byte)'\n')
                length--;
            if (length > 0 && _buffer[length - 1] == (byte)'\r')
                length--;
            if (length == 0)
                continue;   // an empty frame is no line
            return Encoding.UTF8.GetString(_buffer, 0, length);
        }
    }

    public async Task CloseAsync(int code, string words)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
            return;
        ClosedWith = code;
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var deadline = new CancellationTokenSource(CloseWait);
        try
        {
            await _send.WaitAsync(deadline.Token);
            try
            {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)code, words, deadline.Token);
            }
            finally
            {
                _send.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The peer went away first: there is nobody to tell.
            Abort();
        }
    }

    public void Abort()
    {
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // Gone already.
        }
    }

    /// <summary>Closes normally if nothing closed it yet; the socket itself is its host's.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync((int)WebSocketCloseStatus.NormalClosure, "bye");
        _send.Dispose();
    }

    private async ValueTask SendLinesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try
        {
            while (bytes.Length > 0)
            {
                var nl = bytes.Span.IndexOf((byte)'\n');
                var line = nl < 0 ? bytes : bytes[..nl];
                bytes = nl < 0 ? ReadOnlyMemory<byte>.Empty : bytes[(nl + 1)..];
                if (Volatile.Read(ref _closing) != 0 || _socket.State != WebSocketState.Open)
                    throw new IOException("the WebSocket is closing");
                await _socket.SendAsync(line, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The writer's side: every "\n"-ended line it writes leaves as one text frame.</summary>
    private sealed class FrameStream(WebSocketLineChannel channel) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Each line left as its own frame already.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("the line channel writes asynchronously");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            channel.SendLinesAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            channel.SendLinesAsync(buffer, cancellationToken);
    }
}
