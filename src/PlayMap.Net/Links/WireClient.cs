using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Links;

/// <summary>How a dialled line-wire link runs; the defaults are the Show Bus's (§4).</summary>
public sealed record WireClientOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>What this end says in HELLO (and what its pairing proof covers).</summary>
    public required string ClientName { get; init; }

    /// <summary>The peer's pairing token ("" = none): proved against its NONCE, never sent in clear to another machine.</summary>
    public string Token { get; init; } = "";

    /// <summary>Request ids this link makes start with this (ids are unique per link).</summary>
    public string IdPrefix { get; init; } = "c";

    public TimeSpan PingEvery { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.BeatMs);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs);

    public TimeSpan DropAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.GoneMs);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for a NONCE before taking the peer as open (or trusting this machine).</summary>
    public TimeSpan NonceWait { get; init; } = TimeSpan.FromMilliseconds(1500);

    public int MaxLineBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan LineSeconds { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Runs after each connection's HELLO, beside the reader (so its replies are read): a link's own
    /// greeting, such as SUBSCRIBE, sent through <see cref="WireClient.SendAsync"/>.</summary>
    public Func<CancellationToken, Task>? OnConnected { get; init; }
}

/// <summary>Where a link's pairing stands.</summary>
public enum PairState
{
    /// <summary>Connected, nothing said about pairing yet.</summary>
    Unknown,
    /// <summary>The peer asked no proof (open, or it trusts this address).</summary>
    NotNeeded,
    Paired,
    /// <summary>The peer asked for a proof and no token is set here.</summary>
    NeedsToken,
    /// <summary>The proof was refused: this link does not try again until it redials.</summary>
    Refused,
}

/// <summary>A dialled line-wire link in Patterns' grammar — the core of the Patterns link and the desk's engine link.
/// It says HELLO, proves the token against the peer's NONCE (plain AUTH only to this machine, and only when the peer
/// never asked for a proof), pings every 2 s (the clock exchange, and the beat the peer's silence is measured
/// against), goes stale after 6 s of silence and drops and redials after 12 s with the contract's backoff. A line
/// sent through it carries a request id and its reply is matched by that id; nothing is queued while the link is
/// down — a send then fails at once with the reason.</summary>
public sealed class WireClient : IAsyncDisposable
{
    private readonly WireClientOptions _o;
    private readonly ShowWallClock _wall;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireReply>> _pending = new(StringComparer.Ordinal);
    private readonly ClockOffsetEstimator _offset = new(ShowBusConstants.OffsetWindow);
    private readonly object _clockGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile NetworkStream? _stream;
    private volatile Socket? _socket;
    private Task? _loop;
    private long _next;
    private long _lastHeard;
    private int _status = (int)LinkStatus.Down;
    private int _pair = (int)PairState.Unknown;
    private int _disposed;
    private long _connections;

    public WireClient(WireClientOptions options, ShowWallClock wall, ILogger? log = null)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _log = log ?? NullLogger.Instance;
    }

    public LinkStatus Status => (LinkStatus)Volatile.Read(ref _status);

    public PairState Pairing => (PairState)Volatile.Read(ref _pair);

    /// <summary>Connections made since start (a redial counts again).</summary>
    public long Connections => Interlocked.Read(ref _connections);

    public string Address => _o.Host + ":" + _o.Port.ToString(CultureInfo.InvariantCulture);

    public event Action<LinkStatus>? StatusChanged;

    public event Action<PairState>? PairingChanged;

    /// <summary>A line from the peer that is not a reply to one of ours (STATE, PROJECT, CONTEXT pushes…). Raised on
    /// the link's reader: a handler keeps it short.</summary>
    public event Action<string>? Line;

    /// <summary>The peer's clock minus this machine's (ms) and the round trip, once a PING exchange has closed.</summary>
    public (double OffsetMs, double RoundTripMs, int Samples) Clock
    {
        get
        {
            lock (_clockGate)
                return _offset.HasEstimate ? (_offset.Offset, _offset.RoundTrip, _offset.Samples) : (0, 0, 0);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Cuts the connection now (it redials): an operator's "reconnect", a link that moved address.</summary>
    public void Drop() => Abort();

    /// <summary>Sends one line with a request id and returns its reply. A dead link fails at once (nothing is
    /// queued); a reply that does not come within the reply timeout is a failure that says so.</summary>
    public async Task<WireReply> SendAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('@') || text.Contains('\n', StringComparison.Ordinal))
            return new WireReply(false, "a line to send is one command without a request id — the link adds the id");
        if (Status is not (LinkStatus.Live or LinkStatus.Stale) || _stream is null)
            return new WireReply(false, $"the link to {Address} is down — nothing is queued; send it again when the link is back");
        var id = _o.IdPrefix + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource<WireReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            if (!await WriteAsync("@" + id + " " + text, ct))
                return new WireReply(false, $"the line did not reach {Address} — the link dropped");
            return await reply.Task.WaitAsync(_o.ReplyTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new WireReply(false, string.Create(CultureInfo.InvariantCulture, $"no answer from {Address} within {_o.ReplyTimeout.TotalSeconds:0.#} s"));
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Writes one raw line (no id), with the write deadline; false when the link is not up to take it.</summary>
    public async Task<bool> WriteAsync(string line, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null)
            return false;
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _write.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.WriteAsync(bytes, deadline.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException && !ct.IsCancellationRequested)
        {
            Abort();
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    private void SetStatus(LinkStatus status)
    {
        if (Interlocked.Exchange(ref _status, (int)status) != (int)status)
            Raise(() => StatusChanged?.Invoke(status));
    }

    private void SetPairing(PairState state)
    {
        if (Interlocked.Exchange(ref _pair, (int)state) != (int)state)
            Raise(() => PairingChanged?.Invoke(state));
    }

    private void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            // A listener that faults is the listener's: the link goes on.
            _log.LogWarning(ex, "A listener to the link to {Address} faulted.", Address);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(LinkStatus.Connecting);
            var heard = false;
            try
            {
                heard = await ConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                _log.LogDebug(ex, "The link to {Address} ended.", Address);
            }
            finally
            {
                Close();
            }
            SetStatus(LinkStatus.Down);
            attempt = heard ? 0 : attempt + 1;
            if (!await Waits.DelayAsync(Jitter.Shared.Redial(attempt), ct))
                break;
        }
        SetStatus(LinkStatus.Down);
    }

    /// <summary>One connection, from the dial to its end; true when the peer said anything (the redial backoff
    /// starts over then).</summary>
    private async Task<bool> ConnectionAsync(CancellationToken outer)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = connection.Token;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket = socket;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_o.ConnectTimeout);
            await socket.ConnectAsync(_o.Host, _o.Port, timeout.Token);
        }
        var stream = new NetworkStream(socket, ownsSocket: true);
        _stream = stream;
        Interlocked.Increment(ref _connections);
        Interlocked.Exchange(ref _lastHeard, Stopwatch.GetTimestamp());
        SetPairing(PairState.Unknown);
        var reader = new LineReader(stream, _o.MaxLineBytes, _o.LineSeconds);
        var heard = false;
        var proofTried = false;
        var nonceSeen = false;
        if (!await WriteAsync("HELLO " + _o.ClientName, ct))
            return false;
        SetStatus(LinkStatus.Live);
        var beat = Task.Run(() => BeatAsync(ct), CancellationToken.None);
        var nonceWait = Task.Run(async () =>
        {
            if (!await Waits.DelayAsync(_o.NonceWait, ct) || Volatile.Read(ref nonceSeen))
                return;
            // No NONCE: the peer is open or trusts this address. A desk older than proofs pairs by plain AUTH — only
            // when it is this machine, so the token never crosses the network.
            if (_o.Token.Length > 0 && IsLoopbackHost())
                await WriteAsync("@pair AUTH " + _o.Token, ct);
            else if (Pairing == PairState.Unknown)
                SetPairing(PairState.NotNeeded);
        }, CancellationToken.None);
        var greeted = _o.OnConnected is { } greet ? Task.Run(() => greet(ct), CancellationToken.None) : Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                heard = true;
                Interlocked.Exchange(ref _lastHeard, Stopwatch.GetTimestamp());
                if (Status == LinkStatus.Stale)
                    SetStatus(LinkStatus.Live);
                if (line.StartsWith("NONCE ", StringComparison.Ordinal))
                {
                    Volatile.Write(ref nonceSeen, true);
                    if (Pairing == PairState.Paired || proofTried)
                        continue;   // a fresh nonce after a refused proof is not tried again on this connection
                    if (_o.Token.Length == 0)
                    {
                        SetPairing(PairState.NeedsToken);
                        continue;
                    }
                    proofTried = true;
                    await WriteAsync("@pair AUTH-PROOF " + Core.Bus.Pairing.Proof(_o.Token, line[6..].Trim(), _o.ClientName), ct);
                    continue;
                }
                if (IsReply(line))
                {
                    TakeReply(line);
                    continue;
                }
                var handler = Line;
                if (handler is not null)
                    Raise(() => handler(line));
            }
        }
        finally
        {
            await connection.CancelAsync();
            Abort();
            await Task.WhenAll(Quiet(beat), Quiet(nonceWait), Quiet(greeted));
            FailPending($"the link to {Address} dropped before the answer came");
        }
        return heard;
    }

    private async Task Quiet(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The connection's helpers end with it.
            _log.LogDebug(ex, "A helper of the link to {Address} ended with it.", Address);
        }
    }

    private bool IsLoopbackHost() =>
        string.Equals(_o.Host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(_o.Host, out var a) && IPAddress.IsLoopback(a));

    private static bool IsReply(string line) =>
        line == "OK" || line == "ERR" || line.StartsWith("OK ", StringComparison.Ordinal) || line.StartsWith("ERR ", StringComparison.Ordinal);

    /// <summary>A reply: matched by its id (the word after OK/ERR); a reply with no id (HELLO's OK) is the link's own.</summary>
    private void TakeReply(string line)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length < 2 || !parts[1].StartsWith('@'))
            return;
        var id = parts[1][1..];
        var reply = WireReply.Parse(line);
        if (id == "pair")
        {
            SetPairing(reply.Ok ? PairState.Paired : PairState.Refused);
            if (!reply.Ok)
                _log.LogWarning("The link to {Address} refused the pairing token: {Words}", Address, reply.Text);
            return;
        }
        if (id.StartsWith('k') && reply.Ok)
        {
            Pong(reply.Text);
            return;
        }
        if (_pending.TryRemove(id, out var waiting))
            waiting.TrySetResult(reply);
    }

    /// <summary>"PONG t1 t2 t3" closes a clock exchange (t4 is now).</summary>
    private void Pong(string text)
    {
        var p = text.Split(' ');
        if (p.Length != 4 || p[0] != "PONG"
            || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t1)
            || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t2)
            || !long.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out var t3))
            return;
        lock (_clockGate)
            _offset.Add(new ClockSample(t1, t2, t3, _wall.UnixMs));
    }

    /// <summary>Every ping beat: the clock exchange, the liveness check (stale at 6 s, dropped at 12 s).</summary>
    private async Task BeatAsync(CancellationToken ct)
    {
        var lastPing = Stopwatch.GetTimestamp();
        var ping = 0L;
        await WriteAsync("@k" + (++ping).ToString(CultureInfo.InvariantCulture) + " PING " + _wall.UnixMs.ToString(CultureInfo.InvariantCulture), ct);
        var step = TimeSpan.FromMilliseconds(Math.Clamp(_o.StaleAfter.TotalMilliseconds / 8, 20, 250));
        while (await Waits.DelayAsync(step, ct))
        {
            var silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeard));
            if (silent > _o.DropAfter)
            {
                _log.LogInformation("The link to {Address} was silent for {Seconds:0} s: dropped, dialling again.", Address, silent.TotalSeconds);
                Abort();
                return;
            }
            if (silent > _o.StaleAfter && Status == LinkStatus.Live)
                SetStatus(LinkStatus.Stale);
            if (Stopwatch.GetElapsedTime(lastPing) >= _o.PingEvery)
            {
                lastPing = Stopwatch.GetTimestamp();
                await WriteAsync("@k" + (++ping).ToString(CultureInfo.InvariantCulture) + " PING " + _wall.UnixMs.ToString(CultureInfo.InvariantCulture), ct);
            }
        }
    }

    private void FailPending(string words)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiting))
                waiting.TrySetResult(new WireReply(false, words));
    }

    private void Abort()
    {
        try
        {
            _socket?.Close(0);
        }
        catch (ObjectDisposedException)
        {
            // Closed already.
        }
    }

    private void Close()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
        _socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        Abort();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // The loop ends on the stop it was given.
            }
        }
        FailPending("the link was closed");
        _stop.Dispose();
        _write.Dispose();
    }
}
