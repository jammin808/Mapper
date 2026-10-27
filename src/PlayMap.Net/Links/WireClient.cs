using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Links;

/// <summary>How a dialled line-wire link runs; the defaults are the Show Bus's (§4, §13).</summary>
public sealed record WireClientOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>What this end says in HELLO (and what its pairing proof covers).</summary>
    public required string ClientName { get; init; }

    /// <summary>The peer's pairing token ("" = none): proved against its NONCE, never sent in clear to another machine.</summary>
    public string Token { get; init; } = "";

    /// <summary>Whether a connected socket's remote address is this machine, the only place plain AUTH goes. It is asked of the
    /// address the socket reached, never of a host name; a test plays another machine from loopback by answering false.</summary>
    public Func<IPAddress, bool> IsLocal { get; init; } = NetAddress.IsLoopback;

    /// <summary>Request ids this link makes start with this, 1–4 letters: each id is <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c>, a counter from 1
    /// and <see cref="Inst"/> (Show Bus 1.3 §13.12.1). The counter is the process's for the prefix and inst, never the link's: a link
    /// made again, or two side by side, never repeats an id.</summary>
    public string IdPrefix { get; init; } = "c";

    /// <summary>The inst in this link's request ids: this process's own (<see cref="PeerLink.ProcessInst"/>), so an id is unique for the
    /// process and never repeated by a later one.</summary>
    public string Inst { get; init; } = PeerLink.ProcessInst;

    /// <summary>The clock the beat and the peer's silence are counted on: the process's monotonic clock unless the owner hands its own
    /// (a node's, so a hand-moved clock decides when the link pings, goes stale and drops, whatever real time does).</summary>
    public IMonoClock Clock { get; init; } = MonoClock.Instance;

    public TimeSpan PingEvery { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.BeatMs);

    /// <summary>Live while the peer's silence is within this (<c>link.stale.ms</c>), stale past it.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.StaleMs);

    /// <summary>Past this silence (<c>link.drop.ms</c>) the connection is dropped and dialled again.</summary>
    public TimeSpan DropAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.DropMs);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(PeerLink.ConnectTimeoutMs);

    /// <summary>How long a written command waits for its answer, counted on <see cref="Clock"/> from its write (<c>receipt.final.ms</c>):
    /// an answer at the limit still counts; past it the command is unconfirmed — it may have run.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromMilliseconds(PeerLink.ReceiptFinalMs);

    /// <summary>How long a connection stays up once its pairing settled before the redial's backoff starts over
    /// (<c>peerlink.stable.ms</c>), counted on <see cref="Clock"/>: a connect, a reply or a refusal alone resets nothing.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.StableMs);

    /// <summary>How long to wait for a NONCE before taking the peer as open (or trusting this machine).</summary>
    public TimeSpan NonceWait { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>The clock estimate holds only the exchanges of the connection up now: each new connection starts it empty. A link that
    /// takes its peer's word only once the peer proves itself on a connection takes its clock the same way, so nothing an earlier,
    /// unproved answer at the peer's address said counts once a later connection pairs (false: the estimate spans connections).</summary>
    public bool ClockPerConnection { get; init; }

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
    /// <summary>The peer asked for a proof with a NONCE that is not 32 lowercase hex: nothing is proved for it, and nothing goes in
    /// plain AUTH on this connection. A later NONCE of the right shape is proved.</summary>
    Unprovable,
    /// <summary>The peer asked for a proof and the token held here holds an invisible character §7's 1.1 desks read apart (Show Bus 1.3
    /// §13.6.8): nothing is proved, and the words say to retype it.</summary>
    TokenUnprovable,
}

/// <summary>What became of one command sent on a line link (Show Bus 1.3 §13.12.2): its request id (null when none was made), its
/// outcome by 13.12.3's table, its words, and the reply as the wire's readers take it. Only an answer applies, accepts or refuses; a
/// command written with no answer within <see cref="WireClientOptions.ReplyTimeout"/>, or whose connection ended first, is unconfirmed —
/// it may have run — and one never written is not-sent.</summary>
public sealed record WireOutcome(string? Id, PeerOutcome Outcome, string Words, WireReply Reply)
{
    /// <summary>The words an unconfirmed command's reply opens with, so a reader that knows only OK and ERR still reads the outcome.</summary>
    public const string UnconfirmedWord = "unconfirmed: ";

    public static WireOutcome NotSent(string? id, string words) => new(id, PeerOutcome.NotSent, words, new WireReply(false, words));

    public static WireOutcome Unconfirmed(string id, string words) =>
        new(id, PeerOutcome.Unconfirmed, words, new WireReply(false, UnconfirmedWord + words));

    /// <summary>An answer to the command <paramref name="id"/>, read by 13.12.3's table. Its reply is the line as it came, save that its
    /// Ok says the outcome: a refusal an <c>OK</c> carries (a refused command's repeat, <c>OK @id {"ok":false,…}</c>) is Ok false with the
    /// refusal's words, so a reader of the reply and a reader of the outcome never disagree.</summary>
    public static WireOutcome Answered(string id, string line)
    {
        var reply = WireReply.Parse(line);
        var reading = PeerLink.ReadLine(line)?.Reading;
        var outcome = reading?.Outcome ?? (reply.Ok ? PeerOutcome.Applied : PeerOutcome.Refused);
        var words = reading?.Words ?? reply.Text;
        var ok = outcome is PeerOutcome.Applied or PeerOutcome.Accepted;
        return new WireOutcome(id, outcome, words, ok == reply.Ok ? reply : new WireReply(ok, words));
    }
}

/// <summary>A dialled line-wire link in Patterns' grammar — the core of the Patterns link and the desk's engine link, a level-0
/// connector of Show Bus 1.3 §13 (it speaks no LINK yet). It says HELLO, proves the token against the peer's NONCE when that NONCE is 32
/// lowercase hex and the token is one §7's 1.1 desks key alike (no other is proved), sends plain AUTH only to a socket whose remote
/// address is this machine and only when the peer never sent a NONCE, pings every 2 s (the clock exchange — a pong is a sample only
/// when it answers one of the last pings — and the beat the peer's silence is measured against), is stale past 6 s of silence and drops
/// and redials past 12 s, each counted on its options' clock; a stale link is live again with the peer's next line on the same
/// connection. A redial waits 13.15.1's wait for the dials that failed since the link was last stable — a connection whose pairing
/// settled and that stayed up 10 s — so a peer that answers and closes is backed off. A command carries a request id
/// <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c>, its answer is matched by it and read by 13.12.3's table, and nothing is queued while the link is
/// down: a command never written is not-sent at once with the reason, and one written with no answer — none within the reply timeout,
/// or its connection ended first — is unconfirmed, never failed: it may have run.</summary>
public sealed class WireClient : IAsyncDisposable
{
    private readonly WireClientOptions _o;
    private readonly ShowWallClock _wall;
    private readonly ILogger _log;
    /// <summary>The request-id counters of this process, one per prefix and inst (Show Bus 1.3 §13.12.1: an id is unique for the process),
    /// shared by every link that numbers with them.</summary>
    private static readonly ConcurrentDictionary<string, StrongBox<long>> Counters = new(StringComparer.Ordinal);

    private readonly StrongBox<long> _next;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly ClockOffsetEstimator _offset = ClockOffsetEstimator.ForShowBus();
    private readonly object _clockGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>The last pings written on the connection up now, each its id and t1 (under <see cref="_clockGate"/>): a pong answers one
    /// of these or is no sample (Show Bus 1.3 §13.10.2).</summary>
    private readonly List<(string Id, long T1)> _pings = [];

    private volatile NetworkStream? _stream;
    private volatile Socket? _socket;
    private Task? _loop;
    private long _lastHeard;
    private int _status = (int)LinkStatus.Down;
    private int _pair = (int)PairState.Unknown;
    private int _disposed;
    private long _connections;
    private long _nonceWaits;
    private long _unmatched;
    private volatile bool _local;

    /// <summary>When (on the link's clock) the connection up now settled its pairing — paired, or nothing asked — or -1 while it has not.</summary>
    private long _settledNs = -1;

    /// <summary>The connection up now settled its pairing and stayed up <see cref="WireClientOptions.StableAfter"/>: the backoff starts over.</summary>
    private volatile bool _stable;

    /// <summary>One command waiting for its answer: whether any byte of it may have left, from when (on the link's clock), and its
    /// outcome once settled. Its lock orders a write starting against the connection's end settling it, so a command is never written
    /// after it was said not-sent, and never said not-sent once its write began.</summary>
    private sealed class Pending
    {
        private readonly Lock _gate = new();
        private bool _written;
        private long _writtenNs;

        public TaskCompletionSource<WireOutcome> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Its write begins now; false when it was settled already and nothing may be written.</summary>
        public bool Begin(long nowNs)
        {
            lock (_gate)
            {
                if (Outcome.Task.IsCompleted)
                    return false;
                _written = true;
                _writtenNs = nowNs;
                return true;
            }
        }

        /// <summary>When its write began, or null while it has not.</summary>
        public long? WrittenNs
        {
            get
            {
                lock (_gate)
                    return _written ? _writtenNs : null;
            }
        }

        /// <summary>Settles it with what <paramref name="outcome"/> makes of whether its write began; a later word changes nothing.</summary>
        public void Settle(Func<bool, WireOutcome> outcome)
        {
            lock (_gate)
                Outcome.TrySetResult(outcome(_written));
        }
    }

    /// <summary>What one write did: nothing left (no connection up, or the command was settled first), all of it left, or the connection
    /// broke while it was being written — some of it may have reached the peer.</summary>
    private enum Wrote
    {
        NotUp,
        Written,
        Broken,
    }

    public WireClient(WireClientOptions options, ShowWallClock wall, ILogger? log = null)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _log = log ?? NullLogger.Instance;
        // An id this link would make is checked now, so a prefix or an inst of another shape fails here and not at the first command.
        _ = PeerLink.RequestId(_o.IdPrefix, 1, _o.Inst);
        _next = Counters.GetOrAdd(_o.IdPrefix + "-" + _o.Inst, _ => new StrongBox<long>());
    }

    public LinkStatus Status => (LinkStatus)Volatile.Read(ref _status);

    public PairState Pairing => (PairState)Volatile.Read(ref _pair);

    /// <summary>Connections made since start (a redial counts again).</summary>
    public long Connections => Interlocked.Read(ref _connections);

    /// <summary>Connections whose wait for a NONCE has ended (each connection's one), whatever it decided.</summary>
    internal long NonceWaitsEnded => Interlocked.Read(ref _nonceWaits);

    /// <summary>Pongs that answered none of the last pings (forged, late or another's): no clock sample was taken from them.</summary>
    public long UnmatchedPongs => Interlocked.Read(ref _unmatched);

    /// <summary>The connection up now settled its pairing and has stayed up long enough that, when it ends, the backoff starts over.</summary>
    internal bool Stable => _stable;

    /// <summary>Each wait before a redial as the link computes it, before it waits: 13.15.1's n (the dials that failed since the link was
    /// last stable) and the wait. Raised on the link's loop.</summary>
    internal event Action<int, TimeSpan>? Redialling;

    /// <summary>How long the peer has said nothing on the connection now, on the link's clock (zero while none is up).</summary>
    public TimeSpan Silence => _stream is null ? TimeSpan.Zero : Since(Interlocked.Read(ref _lastHeard));

    public string Address => _o.Host + ":" + _o.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The connection up now reached this machine (<see cref="WireClientOptions.IsLocal"/> of its socket's remote address);
    /// false while none is up.</summary>
    public bool Local => _local;

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

    /// <summary>Sends one line with a request id and returns its reply: the answer as it came, or — for a command never written, or one
    /// written that no answer settled — Ok false with the words of <see cref="CommandAsync"/>'s outcome (an unconfirmed one's open with
    /// <see cref="WireOutcome.UnconfirmedWord"/>: it may have run).</summary>
    public async Task<WireReply> SendAsync(string line, CancellationToken ct = default) => (await CommandAsync(line, ct)).Reply;

    /// <summary>Sends one line with a request id and returns what became of it (Show Bus 1.3 §13.12.2). A dead link is not-sent at once
    /// (nothing is queued); a line written and answered is what its answer says; a line written that no answer settles within the reply
    /// timeout on the link's clock, or whose connection ended first, is unconfirmed. Nothing is sent again.</summary>
    public async Task<WireOutcome> CommandAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('@') || text.Contains('\n', StringComparison.Ordinal))
            return WireOutcome.NotSent(null, "a line to send is one command without a request id — the link adds the id");
        if (Status is not (LinkStatus.Live or LinkStatus.Stale) || _stream is null)
            return WireOutcome.NotSent(null, $"the link to {Address} is down — nothing is queued; send it again when the link is back");
        var id = PeerLink.RequestId(_o.IdPrefix, Interlocked.Increment(ref _next.Value), _o.Inst);
        var waiting = new Pending();
        _pending[id] = waiting;
        try
        {
            switch (await WriteCoreAsync("@" + id + " " + text, waiting, ct))
            {
                case Wrote.NotUp:
                    waiting.Settle(_ => WireOutcome.NotSent(id, $"the link to {Address} dropped before the line was written — nothing was sent"));
                    break;
                case Wrote.Broken:
                    waiting.Settle(_ => WireOutcome.Unconfirmed(id, $"the link to {Address} dropped as the line was written — it may have run; look at its state"));
                    break;
            }
            return await waiting.Outcome.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Writes one raw line (no id), with the write deadline; false when the link is not up to take it or the write broke.</summary>
    public async Task<bool> WriteAsync(string line, CancellationToken ct) => await WriteCoreAsync(line, null, ct) == Wrote.Written;

    /// <summary>One line on the connection up now, within the write deadline: <paramref name="command"/>'s write begins (on the link's
    /// clock) only once the writer is free and the command is still unsettled. A write that breaks ends the connection.</summary>
    private async Task<Wrote> WriteCoreAsync(string line, Pending? command, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null)
            return Wrote.NotUp;
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _write.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(stream, _stream) || (command is not null && !command.Begin(_o.Clock.NowNs)))
                return Wrote.NotUp;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.WriteAsync(bytes, deadline.Token);
            return Wrote.Written;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException && !ct.IsCancellationRequested)
        {
            Abort();
            return Wrote.Broken;
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
        // Paired — or nothing asked of a link that holds no token — settles the connection up now: from then it counts toward the
        // backoff's start over. A peer that asks no proof of a link holding a token has not shown it is the peer the token is for.
        if (state is PairState.Paired || (state is PairState.NotNeeded && _o.Token.Length == 0))
            Interlocked.CompareExchange(ref _settledNs, _o.Clock.NowNs, -1);
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
        // n: the dials that failed since the link was last stable (Show Bus 1.3 §13.15.1), from 0. Only a connection whose pairing settled
        // and that stayed up for StableAfter starts it over: at level 0 a settled pairing (OK paired, or a peer that asks no proof of a
        // link that holds no token) is the nearest the link comes to verified.
        var failed = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(LinkStatus.Connecting);
            try
            {
                await ConnectionAsync(ct);
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
            if (_stable)
                failed = 0;
            var wait = Jitter.Shared.Redial(failed);
            Redialling?.Invoke(failed, wait);
            failed = Math.Min(failed + 1, 16);
            if (!await Waits.DelayAsync(wait, ct))
                break;
        }
        SetStatus(LinkStatus.Down);
    }

    /// <summary>One connection, from the dial to its end.</summary>
    private async Task ConnectionAsync(CancellationToken outer)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = connection.Token;
        _stable = false;
        Interlocked.Exchange(ref _settledNs, -1);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket = socket;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_o.ConnectTimeout);
            await socket.ConnectAsync(_o.Host, _o.Port, timeout.Token);
        }
        _local = socket.RemoteEndPoint is IPEndPoint reached && _o.IsLocal(NetAddress.Normal(reached.Address));
        var stream = new NetworkStream(socket, ownsSocket: true);
        _stream = stream;
        lock (_clockGate)
        {
            _pings.Clear();
            if (_o.ClockPerConnection)
                _offset.Reset();
        }
        Interlocked.Increment(ref _connections);
        Interlocked.Exchange(ref _lastHeard, _o.Clock.NowNs);
        SetPairing(PairState.Unknown);
        var reader = new LineReader(stream, _o.MaxLineBytes, _o.LineSeconds);
        var proofTried = false;
        var nonceSeen = false;
        if (!await WriteAsync("HELLO " + _o.ClientName, ct))
            return;
        SetStatus(LinkStatus.Live);
        var beat = Task.Run(() => BeatAsync(ct), CancellationToken.None);
        var nonceWait = Task.Run(async () =>
        {
            try
            {
                if (!await Waits.DelayAsync(_o.NonceWait, ct) || Volatile.Read(ref nonceSeen))
                    return;
                // No NONCE: the peer is open or trusts this address. A desk older than proofs pairs by plain AUTH — only
                // when the socket reached this machine, so the token never crosses the network.
                if (_o.Token.Length > 0 && _local)
                    await WriteAsync("@pair AUTH " + _o.Token, ct);
                else if (Pairing == PairState.Unknown)
                    SetPairing(PairState.NotNeeded);
            }
            finally
            {
                Interlocked.Increment(ref _nonceWaits);
            }
        }, CancellationToken.None);
        var greeted = _o.OnConnected is { } greet ? Task.Run(() => greet(ct), CancellationToken.None) : Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                Interlocked.Exchange(ref _lastHeard, _o.Clock.NowNs);
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
                    var nonce = line[6..].Trim();
                    if (!Core.Bus.Pairing.IsNonce(nonce))
                    {
                        if (Pairing != PairState.Unprovable)
                            _log.LogWarning("The link to {Address} asked for a proof with a NONCE that is not 32 lowercase hex: none is sent.", Address);
                        SetPairing(PairState.Unprovable);
                        continue;
                    }
                    // §7's proof keys the token as 1.1 desks do (Show Bus 1.3 §13.6.8); a token they read apart is never proved.
                    if (PeerLink.LegacyProof(_o.Token, nonce, _o.ClientName) is not { } proof)
                    {
                        if (Pairing != PairState.TokenUnprovable)
                            _log.LogWarning("The link to {Address} asked for a proof, and the token held for it holds an invisible character: none is sent — retype it.", Address);
                        SetPairing(PairState.TokenUnprovable);
                        continue;
                    }
                    proofTried = true;
                    await WriteAsync("@pair AUTH-PROOF " + proof, ct);
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
            // No command starts a write on this connection from here: each one waiting settles as what it is now.
            _stream = null;
            await Task.WhenAll(Quiet(beat), Quiet(nonceWait), Quiet(greeted));
            Unanswered($"the link to {Address} dropped before it answered");
            await stream.DisposeAsync();
        }
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

    private static bool IsReply(string line) =>
        line == "OK" || line == "ERR" || line.StartsWith("OK ", StringComparison.Ordinal) || line.StartsWith("ERR ", StringComparison.Ordinal);

    /// <summary>A reply: matched by its id (the word after OK/ERR); a reply with no id (HELLO's OK) is the link's own.</summary>
    private void TakeReply(string line)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length < 2 || !parts[1].StartsWith('@'))
            return;
        var id = parts[1][1..];
        if (id == "pair")
        {
            var reply = WireReply.Parse(line);
            SetPairing(reply.Ok ? PairState.Paired : PairState.Refused);
            if (!reply.Ok)
                _log.LogWarning("The link to {Address} refused the pairing token: {Words}", Address, reply.Text);
            return;
        }
        if (id.StartsWith('k') && !id.Contains('-', StringComparison.Ordinal))
        {
            Pong(id, line);
            return;
        }
        if (_pending.TryGetValue(id, out var waiting))
            waiting.Settle(_ => WireOutcome.Answered(id, line));
    }

    /// <summary>"OK @k&lt;n&gt; PONG t1 t2 t3" closes a clock exchange (t4 is now) only when it answers one of the last
    /// <see cref="PeerLink.PingsMatched"/> pings written on this connection — its id and its t1 — and the sample is one 13.11.2 admits;
    /// any other is counted as unmatched and taken as no sample.</summary>
    private void Pong(string id, string line)
    {
        var reply = WireReply.Parse(line);
        var p = reply.Text.Split(' ');
        if (!reply.Ok || p.Length != 4 || p[0] != "PONG"
            || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t1)
            || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t2)
            || !long.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out var t3))
            return;   // not a pong: nothing to match
        lock (_clockGate)
        {
            var at = _pings.FindIndex(x => x.Id == id && x.T1 == t1);
            if (at < 0)
            {
                Interlocked.Increment(ref _unmatched);
                return;
            }
            _pings.RemoveAt(at);
            _offset.Add(new ClockSample(t1, t2, t3, _wall.UnixMs));
        }
    }

    /// <summary>One ping: its id and t1 kept among the last <see cref="PeerLink.PingsMatched"/>, then written.</summary>
    private Task PingAsync(long n, CancellationToken ct)
    {
        var id = "k" + n.ToString(CultureInfo.InvariantCulture);
        var t1 = _wall.UnixMs;
        lock (_clockGate)
        {
            _pings.Add((id, t1));
            if (_pings.Count > PeerLink.PingsMatched)
                _pings.RemoveAt(0);
        }
        return WriteAsync("@" + id + " PING " + t1.ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Every ping beat: the clock exchange, the liveness check (stale past 6 s, dropped past 12 s), the commands whose answer is
    /// overdue, and the connection's stability, all on the link's clock; the check looks every step of real time.</summary>
    private async Task BeatAsync(CancellationToken ct)
    {
        var lastPing = _o.Clock.NowNs;
        var ping = 0L;
        await PingAsync(++ping, ct);
        var step = TimeSpan.FromMilliseconds(Math.Clamp(_o.StaleAfter.TotalMilliseconds / 8, 20, 250));
        while (await Waits.DelayAsync(step, ct))
        {
            var silent = Since(Interlocked.Read(ref _lastHeard));
            if (silent > _o.DropAfter)
            {
                _log.LogInformation("The link to {Address} was silent for {Seconds:0} s: dropped, dialling again.", Address, silent.TotalSeconds);
                Abort();
                return;
            }
            if (silent > _o.StaleAfter && Status == LinkStatus.Live)
                SetStatus(LinkStatus.Stale);
            Overdue();
            if (Interlocked.Read(ref _settledNs) is >= 0 and var settled && Since(settled) >= _o.StableAfter)
                _stable = true;
            if (Since(lastPing) >= _o.PingEvery)
            {
                lastPing = _o.Clock.NowNs;
                await PingAsync(++ping, ct);
            }
        }
    }

    /// <summary>Every command written more than the reply timeout ago with no answer is unconfirmed: an answer at the limit still counts.</summary>
    private void Overdue()
    {
        var now = _o.Clock.NowNs;
        var limit = _o.ReplyTimeout.Ticks * TimeSpan.NanosecondsPerTick;
        foreach (var (id, waiting) in _pending)
        {
            if (waiting.WrittenNs is { } at && now - at > limit)
                waiting.Settle(_ => WireOutcome.Unconfirmed(id, string.Create(CultureInfo.InvariantCulture,
                    $"no answer from {Address} in {_o.ReplyTimeout.TotalSeconds:0.#} s — it may have run; look at its state")));
        }
    }

    /// <summary>The time on the link's clock since <paramref name="ns"/> (never negative).</summary>
    private TimeSpan Since(long ns) => TimeSpan.FromTicks(Math.Max(0, _o.Clock.NowNs - ns) / TimeSpan.NanosecondsPerTick);

    /// <summary>The connection ended or the link closed: every command still waiting settles — one written is unconfirmed (it may have
    /// run), one never written not-sent.</summary>
    private void Unanswered(string why)
    {
        foreach (var (id, waiting) in _pending)
            waiting.Settle(written => written
                ? WireOutcome.Unconfirmed(id, why + " — it may have run; look at its state")
                : WireOutcome.NotSent(id, why + " — nothing was sent"));
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
        _local = false;
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
        Unanswered("the link was closed before it answered");
        _stop.Dispose();
        _write.Dispose();
    }
}
