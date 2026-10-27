using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Bus;
using PlayMap.Net.Commands;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Links;

public sealed record StagewatchLinkOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = ShowBusConstants.StagewatchHttpPort;

    /// <summary>Stagewatch's key (swk_…; a node key lets this node post its context and collect approvals). It rides
    /// in Authorization: Bearer — never in the URL — over TLS, or over plain ws only to this machine; off this machine a Stagewatch
    /// that offers no TLS is read without it.</summary>
    public string Key { get; init; } = "";

    /// <summary>The Stagewatch's hello names a certificate and an https port: the link is TLS or nothing (a hello raises the guard and
    /// never lowers it). Without it the link still tries TLS first, as it does for a pinned certificate, which is TLS or nothing too.</summary>
    public bool Tls { get; init; }

    /// <summary>The certificate fingerprint the Stagewatch's hello names (null for a typed address).</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Whose pin this is: node:&lt;id&gt; for a node heard on the hello, addr:&lt;host&gt;:&lt;port&gt; for a typed address.</summary>
    public string PinKey { get; init; } = "";

    /// <summary>The Stagewatch is an approval desk (its hello has the "approvals" cap): approved lines for this node are
    /// collected and run (Show Bus §11).</summary>
    public bool Approvals { get; init; }

    /// <summary>This node's id (the screen's cid, the approvals' target) and name.</summary>
    public required string SelfId { get; init; }

    public string SelfName { get; init; } = "PlayMap";

    public TimeSpan BeatEvery { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.BeatMs);

    public TimeSpan ContextEvery { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ApprovalsEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Live while Stagewatch's silence is within this (<c>link.stale.ms</c>), stale past it.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.StaleMs);

    /// <summary>Past this silence (<c>link.drop.ms</c>) the connection is dropped and dialled again.</summary>
    public TimeSpan DropAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.DropMs);

    /// <summary>After the operator's "disconnect" the link waits this long before it dials again (<c>peerlink.hold.disconnect.ms</c>).</summary>
    public TimeSpan DisconnectHold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a connection stays up once Stagewatch settled its pairing before the redial's backoff starts over
    /// (<c>peerlink.stable.ms</c>), counted on the link's clock: a connect or a reply alone resets nothing.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMilliseconds(PeerLink.StableMs);

    /// <summary>How long a TLS dial waits, from its socket's connect, for the far end's certificate. A far end that shows none by then
    /// does not speak TLS on that port and is met as one that offers no TLS. It stays well under the 5 s a Stagewatch with no
    /// certificate waits for a request's head (it reads the TLS greeting as one and says nothing until then), so the link's own
    /// deadline, not the far end's, ends the attempt.</summary>
    public TimeSpan TlsAnswerWithin { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether the address a dial reaches is this machine, the only place the key goes over plain ws. It is asked of the address
    /// the socket dials (the host resolved once per dial, and that address dialled), never of a host name; a test plays another machine
    /// from loopback by answering false.</summary>
    public Func<IPAddress, bool> IsLocal { get; init; } = NetAddress.IsLoopback;

    /// <summary>A Stagewatch heard on the hello: its https port and TLS or nothing when it names a certificate, else its ws (or http)
    /// port, tried over TLS first; the approval desk when it has the cap.</summary>
    public static StagewatchLinkOptions FromPeer(ShowBusPeer peer, string key, string selfId, string selfName)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var tls = TlsFingerprint.Normal(peer.Hello.TlsFingerprint) is { Length: > 0 } && peer.Hello.Port("https") > 0;
        var port = tls ? peer.Hello.Port("https") : peer.Hello.Port("ws") > 0 ? peer.Hello.Port("ws") : peer.Hello.Port("http");
        return new StagewatchLinkOptions
        {
            Host = peer.Address,
            Port = port > 0 ? port : ShowBusConstants.StagewatchHttpPort,
            Key = key,
            Tls = tls,
            Fingerprint = peer.Hello.TlsFingerprint,
            PinKey = PinStore.NodeKey(peer.Id),
            Approvals = peer.Has("approvals"),
            SelfId = selfId,
            SelfName = selfName,
        };
    }
}

/// <summary>A result owed to Stagewatch for an approved proposal (Show Bus 1.3 §13.12.9): the proposal's id, the result's JSON once its line
/// ran (null while it runs), since when it is owed (on the link's clock), the tries made and when the next is due.</summary>
internal sealed record OwedResult(string Id, string? Json, long SinceNs, int Tries, long NextNs);

/// <summary>A Stagewatch link's side calls (Show Bus 1.3 §13.7.2), each an item of its own: a fault of one is counted on it, and never
/// moves the link's liveness or stops the others.</summary>
public enum StagewatchSideCall
{
    /// <summary>This node's context, posted every five seconds.</summary>
    Context,
    /// <summary>The results owed for the approved lines this node ran.</summary>
    Results,
    /// <summary>The lines an approval desk approved for this node, collected and run.</summary>
    Approvals,
}

/// <summary>PlayMap as a show app on a Stagewatch's <c>/ws</c> (Show Bus §3–§5, §7.1, §10, §11): a node link,
/// <c>?node=&lt;this node&gt;&amp;t=playmap&amp;cid=&lt;its cid&gt;&amp;page=/playmap&amp;detail=1</c> (today's <c>?cid=…</c> alone for an id
/// that is no Show Bus id), with the key in the Authorization header. Every dial tries TLS first (the
/// certificate checked against the pin, never a name or an authority); a Stagewatch whose certificate is pinned, or whose hello names
/// one, is TLS or nothing, and any other falls back to plain ws only when it answers without TLS (no certificate within
/// <see cref="StagewatchLinkOptions.TlsAnswerWithin"/> of the socket's connect, or a handshake that fails on what came instead). Over
/// plain ws the key goes only to this machine: off it, the timer is read without the key, and no context is posted and no approval
/// collected. <see cref="Pairing"/> is what Stagewatch made of the key on the connection up now: its verdict, or for a Stagewatch that
/// sends none the answer to a keyed side call; nothing keyed is sent before the connection's first message, so a verdict is read
/// before any side call, and a refusal holds keyed calls and the next dial until its hold ends (a rate limit holds them too, and
/// pairs nothing and refuses nothing). State frames
/// taken latest-wins on seq and counted on locally from their arrival, <c>{"type":"beat","live":false}</c> read as
/// "the time is not a fact" ("?", never a frozen time), the operator's screen commands raised as events, a ping
/// every 2 s (the clock offset), this node's context posted every 5 s, and — when the Stagewatch is an approval desk —
/// the lines approved for this node collected every 2 s, run through the node's own command path and answered.
/// Stale after 6 s of silence, dropped and redialled after 12 s. Over TLS the certificate is checked against the pin.</summary>
public sealed class StagewatchLink : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    /// <summary>The budget of one WebSocket upgrade (a TLS dial's is cut to its options' TLS answer deadline until a certificate comes).</summary>
    private static readonly TimeSpan UpgradeWithin = TimeSpan.FromSeconds(5);

    /// <summary>How long a dial waits for its host's name to resolve.</summary>
    private static readonly TimeSpan ResolveWithin = TimeSpan.FromSeconds(4);

    /// <summary>The least a refused key is held (Show Bus 1.3 §13.15's <c>peerlink.hold.refused.ms</c>, Stagewatch's own
    /// <c>retry</c>): no keyed call and no dial before it ends, unless Stagewatch takes the key again on the open socket.</summary>
    public static readonly TimeSpan RefusedHold = TimeSpan.FromSeconds(60);

    /// <summary>The most a refusal's <c>retry</c> or <c>Retry-After</c>, or a rate limit's, holds the link: a far end's number is bounded
    /// here.</summary>
    public static readonly TimeSpan MaxRefusedHold = TimeSpan.FromHours(1);

    /// <summary>How often the wait before a redial reads the holds again on the link's clock, which recorded them: a hold that ends
    /// on that clock ends the wait within this, whatever the runtime's timer was told when the wait began.</summary>
    private static readonly TimeSpan HoldReadEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>What <see cref="Refusal"/> opens with when Stagewatch refused this node's key; the far end's words follow.</summary>
    public const string KeyRefused = "key refused — ";

    /// <summary>Stagewatch's words kept in a refusal, at most this many characters.</summary>
    private const int MaxRefusalChars = 240;

    /// <summary>Each side call's own deadline (Show Bus 1.3 §13.7.2, §14 <c>link.sidecall.timeout.ms</c>): the side-call lane runs one call
    /// at a time (<c>link.sidecall.inflight</c> 1), apart from the beat, so a slow call never holds a ping or the liveness check.</summary>
    public static readonly TimeSpan SideCallWithin = TimeSpan.FromMilliseconds(4000);

    /// <summary>The most approved proposals the link remembers it ran (each by its desk, id and creation): one still listed is never run
    /// twice while it is remembered; past this the oldest whose result is not owed is let go.</summary>
    public const int RanKept = 1024;

    private readonly StagewatchLinkOptions _o;
    private readonly IWireEndpoint _endpoint;
    private readonly ShowWallClock _wall;
    private readonly PinStore _pins;
    private readonly IMonoClock _mono;
    private readonly ILogger _log;
    private readonly CommandRunner _runner;
    private readonly ClockOffsetEstimator _offset = ClockOffsetEstimator.ForShowBus();
    private readonly object _gate = new();

    /// <summary>The approved proposals run, by <see cref="RanKey"/>, oldest first (under <see cref="_gate"/>): one still listed is never run
    /// again while it is here.</summary>
    private readonly Dictionary<string, long> _ran = new(StringComparer.Ordinal);
    private readonly Queue<string> _ranOrder = new();

    /// <summary>The results owed to Stagewatch (Show Bus 1.3 §13.12.9), by <see cref="RanKey"/> (under <see cref="_gate"/>): each recorded
    /// before its line runs, posted once it ran, and posted again on 13.12.9's schedule until Stagewatch takes it or it has been owed
    /// <c>report.retry.for.ms</c>.</summary>
    private readonly Dictionary<string, OwedResult> _owed = new(StringComparer.Ordinal);

    /// <summary>The last pings' t1 on the connection up now (under <see cref="_gate"/>): a pong answers one of these or is no sample
    /// (Show Bus 1.3 §13.10.2).</summary>
    private readonly List<long> _pings = [];

    private long _unmatched;
    private long _resultsDelivered;

    /// <summary>The side calls that faulted, by <see cref="StagewatchSideCall"/>.</summary>
    private readonly long[] _sideCallFaults = new long[Enum.GetValues<StagewatchSideCall>().Length];

    /// <summary>When (on the link's clock) Stagewatch settled the pairing of the connection up now — paired, or nothing asked — or -1.</summary>
    private long _settledNs = -1;

    /// <summary>The connection up now settled its pairing and stayed up <see cref="StagewatchLinkOptions.StableAfter"/>.</summary>
    private volatile bool _stable;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private StagewatchTimer? _timer;
    private long _frameAtNs;
    private long _lastHeardNs;
    private long _lastSeq = -1;
    private volatile bool _stuck;
    private int _status = (int)LinkStatus.Down;
    private string _presented = "";
    private volatile string _words = "";
    private volatile bool _overTls;
    private volatile bool _keyed;
    private volatile bool _keyHeld;
    private string _lastMode = "";
    private long _approvalsRun;
    private long _connections;

    /// <summary>A <see cref="PairState"/>: what Stagewatch made of the key on the connection up now, written under <see cref="_gate"/>.</summary>
    private int _pairing = (int)PairState.Unknown;

    /// <summary>Counts every verdict and refusal (under <see cref="_gate"/>): a side call's answer settles the pairing only while no
    /// verdict or refusal came after the call left, so a late 2xx never undoes a newer refusal.</summary>
    private long _verdicts;

    /// <summary>Until when (on <see cref="_mono"/>, ns; 0 for none) a refused key holds keyed calls and the next dial, written under
    /// <see cref="_gate"/>.</summary>
    private long _refusedUntilNs;

    /// <summary>Until when (on <see cref="_mono"/>, ns; 0 for none) Stagewatch's rate limit holds keyed calls and the next dial.</summary>
    private long _busyUntilNs;

    /// <summary>Until when (on <see cref="_mono"/>, ns; 0 for none) the operator's disconnect holds the next dial.</summary>
    private long _heldUntilNs;

    private volatile string _refusal = "";

    /// <summary>The connection up now has heard its first message: a verdict, which Stagewatch sends before anything, is read.</summary>
    private volatile bool _heard;

    /// <summary>The words the connection up now came up with, said again once a refusal on it is lifted.</summary>
    private volatile string _upWords = "";

    public StagewatchLink(StagewatchLinkOptions options, IWireEndpoint endpoint, ShowWallClock wall, PinStore pins, IMonoClock? mono = null, ILogger? log = null)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _mono = mono ?? MonoClock.Instance;
        _log = log ?? NullLogger.Instance;
        _runner = new CommandRunner(endpoint, wall, _log);
    }

    public LinkStatus Status => (LinkStatus)Volatile.Read(ref _status);

    public string Address => _o.Host + ":" + _o.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stagewatch says its show thread is stuck: its time is not a fact until a frame comes again.</summary>
    public bool Stuck => _stuck;

    /// <summary>The link's last words (a refusal, the TLS verdict, an approval desk's answer).</summary>
    public string Words => _words;

    /// <summary>The connection up now runs over TLS.</summary>
    public bool OverTls => _overTls;

    /// <summary>A key is set and the connection up now does not carry it: the Stagewatch offers no TLS and is not this machine.</summary>
    public bool KeyHeldBack => _keyHeld;

    /// <summary>What Stagewatch made of this node's key on the connection up now: <see cref="PairState.Paired"/> when its verdict says
    /// the key (or the operator's credential) speaks for this node, or — from a Stagewatch that sends no verdict — when a keyed side
    /// call is answered 2xx; <see cref="PairState.Refused"/> when it refused the key, let it speak for no node, or refused a keyed side
    /// call; <see cref="PairState.NotNeeded"/> when no key is set and the connection's first message brought no refusal; else
    /// <see cref="PairState.Unknown"/> (no answer yet, or the key held back). Each is set before the link does anything else, and a
    /// refusal stands, with its words, until a later verdict, the next connection, or — with no connection up — the end of the wait
    /// its hold causes.</summary>
    public PairState Pairing => (PairState)Volatile.Read(ref _pairing);

    /// <summary>The refusal standing, as people read it: <see cref="KeyRefused"/> and Stagewatch's words for a refused key, else what
    /// it refused and its words; "" when nothing is refused.</summary>
    public string Refusal => _refusal;

    /// <summary>The pairing, the refusal standing and how much longer its hold runs, read as one: a reader never sees a refusal without
    /// its words, or the words of one that was lifted.</summary>
    public (PairState State, string Refusal, TimeSpan RefusedFor) PairingNow
    {
        get
        {
            lock (_gate)
                return ((PairState)Volatile.Read(ref _pairing), _refusal, Remaining(Volatile.Read(ref _refusedUntilNs)));
        }
    }

    /// <summary>How much longer a refused key holds keyed calls and the next dial (zero when none is held).</summary>
    public TimeSpan RefusedFor => Remaining(Volatile.Read(ref _refusedUntilNs));

    /// <summary>How much longer Stagewatch's rate limit (a 429's <c>Retry-After</c>) holds keyed calls and the next dial (zero when none
    /// is held). It pairs nothing and refuses nothing: the key is good, and asked too often.</summary>
    public TimeSpan BusyFor => Remaining(Volatile.Read(ref _busyUntilNs));

    /// <summary>How much longer the operator's disconnect holds the next dial (zero when none is held).</summary>
    public TimeSpan HeldFor => Remaining(Volatile.Read(ref _heldUntilNs));

    /// <summary>What the pin made of the certificate last presented (null over plain).</summary>
    public PinVerdict? Pin { get; private set; }

    /// <summary>A certificate refused as CERTIFICATE CHANGED, waiting for the operator (<see cref="AcceptPendingCertificate"/>).</summary>
    public string PendingFingerprint { get; private set; } = "";

    public long ApprovalsRun => Interlocked.Read(ref _approvalsRun);

    /// <summary>Pongs that answered none of the last pings (forged, late or another's): no clock sample was taken from them.</summary>
    public long UnmatchedPongs => Interlocked.Read(ref _unmatched);

    /// <summary>The connection up now settled its pairing and has stayed up long enough that, when it ends, the backoff starts over.</summary>
    internal bool Stable => _stable;

    /// <summary>Each wait before a redial as the link computes it, before it waits: 13.15.1's n (the dials that failed since the link was
    /// last stable) and its backoff (a hold standing makes the wait longer). Raised on the link's loop.</summary>
    internal event Action<int, TimeSpan>? Redialling;

    /// <summary>Results Stagewatch took (or had settled already) for the approved lines this node ran.</summary>
    public long ResultsDelivered => Interlocked.Read(ref _resultsDelivered);

    /// <summary>Side calls of <paramref name="call"/>'s kind that faulted since start — an exception none of the call's own answers
    /// names: each was logged and let go, and the lane went on with the next call.</summary>
    public long SideCallFaults(StagewatchSideCall call) => Interlocked.Read(ref _sideCallFaults[(int)call]);

    /// <summary>Results still owed to Stagewatch: each posted again on Show Bus 1.3 §13.12.9's schedule until it takes it.</summary>
    public int ResultsOwed
    {
        get
        {
            lock (_gate)
                return _owed.Count;
        }
    }

    public long Connections => Interlocked.Read(ref _connections);

    public event Action<LinkStatus>? StatusChanged;

    /// <summary>The pairing or its refusal moved (<see cref="Pairing"/>, <see cref="Refusal"/>); raised on the link's reader or beat.</summary>
    public event Action<PairState>? PairingChanged;

    /// <summary>The operator's screen command (redirect, identify, reload, disconnect); raised on the link's reader.</summary>
    public event Action<StagewatchScreenCommand>? ScreenCommand;

    /// <summary>A state frame landed (latest wins); raised on the link's reader.</summary>
    public event Action<StagewatchTimer>? FrameReceived;

    /// <summary>The last frame as it came (null before the first on this link).</summary>
    public StagewatchTimer? LastFrame
    {
        get
        {
            lock (_gate)
                return _timer;
        }
    }

    /// <summary>The timer now: counted on from the frame's arrival, or "?" when it is not a fact.</summary>
    public StagewatchReading Reading
    {
        get
        {
            StagewatchTimer? t;
            long at;
            lock (_gate)
            {
                t = _timer;
                at = _frameAtNs;
            }
            if (t is null)
                return StagewatchReading.Unknown;
            if (Status != LinkStatus.Live)
                return new StagewatchReading(t, false, "?", Status == LinkStatus.Stale ? "Stagewatch has gone quiet" : "Stagewatch is not answering");
            if (_stuck)
                return new StagewatchReading(t, false, "?", "Stagewatch says its show is stuck — the time is not shown");
            var now = t.CountedOn(Durations.ToSeconds(_mono.NowNs - at));
            return new StagewatchReading(now, true, now.Text, "");
        }
    }

    /// <summary>Stagewatch's clock minus this machine's (ms) and the round trip, once a ping has closed.</summary>
    public (double OffsetMs, double RoundTripMs, int Samples) Clock
    {
        get
        {
            lock (_gate)
                return _offset.HasEstimate ? (_offset.Offset, _offset.RoundTrip, _offset.Samples) : (0, 0, 0);
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);

    /// <summary>The operator accepts a changed certificate: it becomes the pin; the next dial uses it.</summary>
    public string AcceptPendingCertificate()
    {
        var f = PendingFingerprint;
        if (f.Length == 0 || _o.PinKey.Length == 0)
            return "No changed certificate is waiting.";
        _pins.Pin(_o.PinKey, f);
        PendingFingerprint = "";
        return $"Accepted Stagewatch's new certificate {TlsFingerprint.Short(f)}.";
    }

    private void SetStatus(LinkStatus s)
    {
        if (Interlocked.Exchange(ref _status, (int)s) == (int)s)
            return;
        try
        {
            StatusChanged?.Invoke(s);
        }
        catch (Exception ex)
        {
            // A listener that faults is the listener's: the link goes on.
            _log.LogWarning(ex, "A listener to the Stagewatch link faulted.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // The dials that failed since the link was last stable (Show Bus 1.3 §13.15.1), from 0. Only a connection on which Stagewatch
        // settled the pairing (its verdict for the key, or nothing asked of a link with none) and that stayed up for StableAfter starts
        // it over: a connect, a socket that answers and closes, or a refusal alone resets nothing.
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
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or SocketException or OperationCanceledException or InvalidOperationException or AuthenticationException)
            {
                // A refusal's words stand while its hold runs; with none running, what the dial met is the news.
                var (pairing, _, refusedFor) = PairingNow;
                _words = ex is DialEndedException refused ? refused.Message
                    : PendingFingerprint.Length > 0 || (pairing == PairState.Refused && refusedFor > TimeSpan.Zero) ? _words
                    : "Stagewatch at " + Address + ": " + Brief(ex);
                _log.LogDebug(ex, "The Stagewatch link to {Address} ended.", Address);
            }
            _overTls = false;
            _keyed = false;
            _keyHeld = false;
            _heard = false;
            SetStatus(LinkStatus.Down);
            if (_stable)
                failed = 0;
            // A hold was recorded before anything that could throw, so a connection that ends in an exception (a close that races the
            // far end's abort) waits it as a clean end does.
            if (!await BeforeRedialAsync(failed, ct))
                break;
            failed = Math.Min(failed + 1, 16);
            HoldsSpent();
        }
        SetStatus(LinkStatus.Down);
    }

    /// <summary>The wait before the next dial: the backoff (on the runtime's timer), or longer while the operator's disconnect, a refused
    /// key or a rate limit holds it (each read again on the link's clock as the wait goes). False when the link stops.</summary>
    private async Task<bool> BeforeRedialAsync(int attempt, CancellationToken ct)
    {
        var backoff = Jitter.Shared.Redial(attempt);
        Redialling?.Invoke(attempt, backoff);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var wait = Longest(backoff - Stopwatch.GetElapsedTime(started), Longest(HeldFor, Longest(RefusedFor, BusyFor)));
            if (wait <= TimeSpan.Zero)
                return true;
            if (!await Waits.DelayAsync(wait < HoldReadEvery ? wait : HoldReadEvery, ct))
                return false;
        }
    }

    /// <summary>The wait before a dial outlasted every hold, and no connection was up to bring another: they are spent, and a refusal
    /// they held stands no longer (the next dial asks again; until it is answered the pairing is unknown).</summary>
    private void HoldsSpent()
    {
        bool lifted;
        lock (_gate)
        {
            Volatile.Write(ref _heldUntilNs, 0);
            Volatile.Write(ref _refusedUntilNs, 0);
            Volatile.Write(ref _busyUntilNs, 0);
            lifted = Volatile.Read(ref _pairing) == (int)PairState.Refused;
            if (lifted)
            {
                _verdicts++;
                _refusal = "";
                Volatile.Write(ref _pairing, (int)PairState.Unknown);
            }
        }
        if (lifted)
            RaisePairing(PairState.Unknown);
    }

    private static TimeSpan Longest(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>How long until <paramref name="untilNs"/> on the link's clock (zero once it has passed, or for 0, none).</summary>
    private TimeSpan Remaining(long untilNs)
    {
        var now = _mono.NowNs;
        return untilNs > now ? TimeSpan.FromTicks((untilNs - now) / 100) : TimeSpan.Zero;
    }

    private static string Brief(Exception ex) =>
        ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal)
            ? "refused — check the Stagewatch key"
            : ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;

    private RemoteCertificateValidationCallback Validator(DialProbe probe) => _pins.Validator(_o.PinKey, _o.Fingerprint, (verdict, presented) =>
    {
        probe.Certificate();
        Pin = verdict;
        _presented = presented;
        _words = PinStore.Words(verdict, "Stagewatch", presented);
        if (verdict == PinVerdict.Changed)
            PendingFingerprint = presented;
    });

    /// <summary>What one dial saw, and how long its upgrade may take. The upgrade spends a budget of <see cref="UpgradeWithin"/>; on a
    /// TLS dial its socket reaching the far end cuts that budget to <see cref="StagewatchLinkOptions.TlsAnswerWithin"/>, and a
    /// certificate gives it back whole. A far end that showed no certificate by the time the budget ran out is <see cref="Silent"/>: it
    /// does not speak TLS on that port. The side calls' sockets, opened after the upgrade, change no budget.</summary>
    private sealed class DialProbe(bool tls, TimeSpan tlsWithin)
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _budget;
        private volatile bool _reached;
        private volatile bool _answered;
        private volatile bool _silent;

        /// <summary>A socket of this dial reached the far end.</summary>
        public bool Reached => _reached;

        /// <summary>A TLS handshake of this dial got as far as a certificate.</summary>
        public bool Answered => _answered;

        /// <summary>The TLS answer's deadline passed with the socket connected and no certificate shown.</summary>
        public bool Silent => _silent;

        /// <summary>The upgrade under way spends <paramref name="budget"/>; null once it has ended (before the budget is disposed).</summary>
        public void Arm(CancellationTokenSource? budget)
        {
            lock (_gate)
                _budget = budget;
        }

        public void Connected()
        {
            _reached = true;
            if (!tls)
                return;
            lock (_gate)
                _budget?.CancelAfter(tlsWithin);
        }

        public void Certificate()
        {
            _answered = true;
            lock (_gate)
                _budget?.CancelAfter(UpgradeWithin);
        }

        /// <summary>The upgrade's budget ran out (not the link's own stop).</summary>
        public void Expired()
        {
            if (tls && _reached && !_answered)
                _silent = true;
        }
    }

    /// <summary>A dial that ended before anything of this node's was sent: its words are the link's.</summary>
    private sealed class DialEndedException(string words) : IOException(words);

    /// <summary>The address this dial reaches: the host as typed when it is an address, else resolved once (IPv4 first) within
    /// <see cref="ResolveWithin"/>. The socket dials this address and no other, so whether it is this machine is decided by what is
    /// dialled.</summary>
    private async Task<IPAddress> DialAddressAsync(CancellationToken ct)
    {
        if (IPAddress.TryParse(_o.Host, out var literal))
            return literal;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ResolveWithin);
        IPAddress[] found;
        try
        {
            // The wait, not only the lookup, is bounded: a resolver that does not take the cancel is left to finish on its own.
            found = await Dns.GetHostAddressesAsync(_o.Host, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DialEndedException(string.Create(CultureInfo.InvariantCulture,
                $"Stagewatch at {Address}: the name {_o.Host} did not resolve within {ResolveWithin.TotalSeconds:0} s — nothing sent"));
        }
        return Array.Find(found, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (found.Length > 0 ? found[0] : throw new SocketException((int)SocketError.HostNotFound));
    }

    /// <summary>The WebSocket and side calls of one dial: every socket it opens goes to <paramref name="address"/>, over TLS checked
    /// against the pin when <paramref name="tls"/>.</summary>
    private SocketsHttpHandler Handler(IPAddress address, bool tls, DialProbe probe)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    socket.Dispose();
                    throw;
                }
                probe.Connected();
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        if (tls)
            handler.SslOptions.RemoteCertificateValidationCallback = Validator(probe);
        return handler;
    }

    private Uri Base(bool tls, bool socket)
    {
        var host = _o.Host.Contains(':', StringComparison.Ordinal) ? "[" + _o.Host + "]" : _o.Host;
        var scheme = (socket, tls) switch
        {
            (true, true) => "wss",
            (true, false) => "ws",
            (false, true) => "https",
            _ => "http",
        };
        return new Uri($"{scheme}://{host}:{_o.Port.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>The socket this node asks for: a node link (<c>node=&lt;id&gt;&amp;t=playmap</c>, Stagewatch 2.18) when its id is a Show Bus
    /// id (1–40 letters, digits, <c>-</c> or <c>_</c>, as Stagewatch reads one), with the <c>cid</c> and <c>page</c> an older Stagewatch reads
    /// (which ignores <c>node</c>); else today's screen query alone. The <c>cid</c> is the id when Stagewatch keeps it as a screen's id
    /// (6–32 of those characters), else <c>playmap-</c> and the id.</summary>
    public static string SocketPath(string selfId)
    {
        ArgumentNullException.ThrowIfNull(selfId);
        var cid = selfId.Length is >= 6 and <= 32 && selfId.All(IdChar) ? selfId : "playmap-" + selfId;
        var screen = $"cid={Uri.EscapeDataString(cid)}&page=/playmap&detail=1";
        return DialsAsNode(selfId)
            ? $"/ws?node={selfId}&t={ShowBusConstants.KindPlayMap}&{screen}"
            : "/ws?" + screen;
    }

    /// <summary>A node with this id dials a node link (<see cref="SocketPath"/>): its id is a Show Bus id as Stagewatch reads one.</summary>
    public static bool DialsAsNode(string selfId) => selfId is { Length: > 0 and <= 40 } && selfId.All(IdChar);

    private static bool IdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    /// <summary>One upgrade through <paramref name="invoker"/>, the key in its header when <paramref name="keyed"/>, on the budget
    /// <paramref name="probe"/> keeps.</summary>
    private async Task<ClientWebSocket> UpgradeAsync(HttpMessageInvoker invoker, DialProbe probe, bool tls, bool keyed, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.Zero;   // the Show Bus beat is the keep-alive
            if (keyed)
                ws.Options.SetRequestHeader("Authorization", "Bearer " + _o.Key);
            var uri = new Uri(Base(tls, socket: true), SocketPath(_o.SelfId));
            budget.CancelAfter(UpgradeWithin);
            probe.Arm(budget);
            await ws.ConnectAsync(uri, invoker, budget.Token);
            return ws;
        }
        catch
        {
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
                probe.Expired();
            ws.Dispose();
            throw;
        }
        finally
        {
            probe.Arm(null);
        }
    }

    /// <summary>The far end answered a TLS dial without TLS: the socket reached it and no certificate came, and either the TLS answer's
    /// deadline passed (a server that reads the greeting as a request's head and waits) or the handshake failed on what came instead
    /// (a server that answers the greeting as a bad request). A cancel of the link's own is never an answer.</summary>
    private static bool AnsweredWithoutTls(Exception ex, DialProbe probe)
    {
        if (!probe.Reached || probe.Answered)
            return false;
        if (probe.Silent)
            return true;
        var tlsFailed = false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException)
                return false;
            tlsFailed |= e is AuthenticationException or IOException;
        }
        return tlsFailed;
    }

    /// <summary>One connection, until it ends; a hold it met (the operator's disconnect, a refused key) is recorded in the link's
    /// fields before anything else is done, so the next dial waits it however the connection ends.</summary>
    private async Task ConnectionAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = linked.Token;
        // A dial is stable only once this connection settles and stays up: one that fails anywhere — the name, the socket, TLS, the
        // pin, the upgrade — counts toward the backoff whatever the connection before it was.
        _stable = false;
        Interlocked.Exchange(ref _settledNs, -1);
        var address = await DialAddressAsync(ct);
        var local = _o.IsLocal(NetAddress.Normal(address));
        var pinned = _o.PinKey.Length > 0 && TlsFingerprint.IsValid(_pins.Get(_o.PinKey));
        var tls = true;
        var keyed = _o.Key.Length > 0;
        var probe = new DialProbe(tls: true, _o.TlsAnswerWithin);
        var handler = Handler(address, tls: true, probe);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        ClientWebSocket ws;
        try
        {
            ws = await UpgradeAsync(invoker, probe, tls: true, keyed, ct);
        }
        catch (Exception ex) when (AnsweredWithoutTls(ex, probe) || (probe.Answered && Pin is PinVerdict.Changed or PinVerdict.Mismatch))
        {
            invoker.Dispose();
            handler.Dispose();
            if (probe.Answered)
                throw new DialEndedException(_words);   // the certificate was refused: nothing more goes to that machine
            if (pinned || _o.Tls)
            {
                // A pinned certificate, or one the hello names, is TLS or nothing: a plain answer is not that Stagewatch.
                throw new DialEndedException(pinned
                    ? $"Stagewatch at {Address} answered without its pinned certificate — refused, nothing sent (another machine at its address?)"
                    : $"Stagewatch at {Address} answered without TLS although its hello names a certificate — refused, nothing sent");
            }
            // It offers no TLS: plain, and the key goes only to this machine.
            tls = false;
            keyed = _o.Key.Length > 0 && local;
            probe = new DialProbe(tls: false, _o.TlsAnswerWithin);
            handler = Handler(address, tls: false, probe);
            invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            try
            {
                ws = await UpgradeAsync(invoker, probe, tls: false, keyed, ct);
            }
            catch
            {
                invoker.Dispose();
                handler.Dispose();
                throw;
            }
        }
        catch
        {
            invoker.Dispose();
            handler.Dispose();
            throw;
        }
        using var owned = handler;
        using var upgrade = invoker;
        using var socket = ws;
        Interlocked.Increment(ref _connections);
        // A new connection asks again: its pairing is unknown until Stagewatch answers for the key it carries, or — with no key set —
        // until its first message is read and brings no refusal; nothing keyed goes until that message is read.
        lock (_gate)
        {
            _lastSeq = -1;
            _verdicts++;
            _refusal = "";
            Volatile.Write(ref _pairing, (int)PairState.Unknown);
            _pings.Clear();
        }
        _heard = false;
        Interlocked.Exchange(ref _lastHeardNs, _mono.NowNs);
        _overTls = tls;
        _keyed = keyed;
        _keyHeld = _o.Key.Length > 0 && !keyed;
        _upWords = tls ? $"following Stagewatch at {Address} over TLS"
            : _keyHeld ? $"Stagewatch at {Address} offers no TLS — the key stays on this machine, so its timer is read without it (no context posted, no approvals collected)"
            : local ? $"following Stagewatch at {Address}"
            : $"following Stagewatch at {Address} over plain ws — it offers no TLS";
        _words = _upWords;
        SetStatus(LinkStatus.Live);
        var mode = tls ? "tls" : _keyHeld ? "keyless" : "plain";
        if (!string.Equals(mode, _lastMode, StringComparison.Ordinal))
        {
            _lastMode = mode;
            _log.LogInformation("The Stagewatch link to {Address} is {Mode}: {Words}", Address, mode, _words);
        }
        if (tls && _o.Key.Length == 0 && Pin == PinVerdict.FirstUse && _o.PinKey.Length > 0 && TlsFingerprint.Normal(_o.Fingerprint).Length > 0)
        {
            // No key, and the certificate is the one the hello names: the WebSocket's answer is the first trusted contact. A typed
            // address, or an id whose hello names none, is pinned only after an authenticated answer, which with no key never comes.
            // The link says so before the store holds it, so whoever sees the pin stored sees the link's verdict too.
            Pin = PinVerdict.Pinned;
            _pins.Pin(_o.PinKey, _presented);
        }

        // Every answer is buffered whole before it is read, and bounded as a message is.
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = SideCallWithin,
            BaseAddress = Base(tls, socket: false),
            MaxResponseContentBufferSize = MaxMessageBytes,
        };
        using var send = new SemaphoreSlim(1, 1);
        // The beat (the ping and the liveness check) and the side calls (the context, the approvals, the results owed) run apart: a side
        // call that waits never holds a ping or the drop (Show Bus 1.3 §13.7.2).
        var beat = Task.Run(() => BeatAsync(socket, send, ct), CancellationToken.None);
        var sideCalls = Task.Run(() => SideCallsAsync(http, ct), CancellationToken.None);
        try
        {
            await ReceiveAsync(socket, ct);
        }
        finally
        {
            await linked.CancelAsync();
            await Ended(beat, "beat");
            await Ended(sideCalls, "side calls");
        }
    }

    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>(16 * 1024);
        var asked = Asked();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                return;
            message.Write(buffer.AsSpan(0, r.Count));
            if (message.WrittenCount > MaxMessageBytes)
                throw new InvalidOperationException("a Stagewatch message past 1 MB is not a frame");
            if (!r.EndOfMessage)
                continue;
            var now = _mono.NowNs;
            Interlocked.Exchange(ref _lastHeardNs, now);
            if (Status == LinkStatus.Stale)
                SetStatus(LinkStatus.Live);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(message.WrittenMemory);
            }
            catch (JsonException)
            {
                message.ResetWrittenCount();
                Heard(asked);
                continue;   // not JSON: not a frame, and not the link's end
            }
            message.ResetWrittenCount();
            bool end;
            using (doc)
                end = Take(doc.RootElement, now);
            // Whatever the message was, it was read whole first: a verdict among it is the pairing before a keyed call can go.
            Heard(asked);
            if (end)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnected by the operator", CancellationToken.None);
                return;
            }
        }
    }

    /// <summary>The connection's first message is read (<paramref name="asked"/>: the count of verdicts and refusals as it came up). With
    /// no key set, one that brought no refusal or verdict of its own settles the pairing as needing none; the pairing is set before
    /// keyed calls may go.</summary>
    private void Heard(long asked)
    {
        if (_heard)
            return;
        if (_o.Key.Length == 0)
            Pair(PairState.NotNeeded, asked);
        _heard = true;
    }

    /// <summary>One message from Stagewatch; true ends the connection (the operator's disconnect, whose hold is recorded first).</summary>
    private bool Take(JsonElement r, long now)
    {
        if (r.ValueKind != JsonValueKind.Object)
            return false;
        var type = JsonText.Str(r, "type");
        switch (type)
        {
            case "beat":
                // A beat that says the show is not live makes it stuck; a plain beat, or a pong, never clears it — only the next state
                // frame does (Show Bus 1.3 §13.10.5).
                if (JsonText.Bool(r, "live", true) is false)
                    _stuck = true;
                return false;
            case "pong":
                Pong(JsonText.Long(r, "t"), JsonText.Long(r, "t2"), JsonText.Long(r, "t3"));
                return false;
            case "redirect" or "identify" or "reload" or "disconnect":
                if (type == "disconnect")
                    Volatile.Write(ref _heldUntilNs, now + _o.DisconnectHold.Ticks * 100);
                var command = new StagewatchScreenCommand(type, JsonText.Str(r, "to"), JsonText.Str(r, "name"), JsonText.Str(r, "id"));
                try
                {
                    ScreenCommand?.Invoke(command);
                }
                catch (Exception ex)
                {
                    // A listener that faults is the listener's: the link goes on.
                    _log.LogWarning(ex, "A listener to Stagewatch's screen commands faulted.");
                }
                return type == "disconnect";
            case "auth":
                Verdict(r);
                return false;
            case "error":
                _words = "Stagewatch says: " + Bounded(JsonText.Str(r, "error"));
                return false;
            case "state" or "":
                if (StagewatchTimer.Parse(r) is not { } frame)
                    return false;
                lock (_gate)
                {
                    if (frame.Seq > 0 && _lastSeq > 0 && frame.Seq < _lastSeq)
                        return false;   // an older frame overtaken by a newer one: latest wins
                    _lastSeq = frame.Seq;
                    _timer = frame;
                    _frameAtNs = now;
                }
                _stuck = false;
                try
                {
                    FrameReceived?.Invoke(frame);
                }
                catch (Exception ex)
                {
                    // A listener that faults is the listener's: the link goes on.
                    _log.LogWarning(ex, "A listener to Stagewatch's frames faulted.");
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>Stagewatch's verdict on this node's link (§7.1, 2.18), the first message a node link hears and sent again when the
    /// key, its binding or the PIN changes, latest wins. <c>ok:false</c> refuses the key: held for its <c>retry</c> (at least
    /// <see cref="RefusedHold"/>), the socket kept for the public frame. <c>ok:true</c> naming this node pairs the link when the key
    /// (or the operator's credential) speaks for it; one that speaks for none refuses the key with Stagewatch's words when the link
    /// carries it, and asks nothing of a link with no key set. A verdict naming another node, or none (a control page's), is not this
    /// link's.</summary>
    private void Verdict(JsonElement r)
    {
        if (!JsonText.Bool(r, "ok", true))
        {
            var error = Bounded(JsonText.Str(r, "error"));
            var refused = Bounded(JsonText.Str(r, "refused"));
            var retry = JsonText.Long(r, "retry");
            if (JsonText.Bool(r, "view") && refused.Length == 0 && !_keyed)
            {
                // The viewing code, asked of a link that brings no key: nothing of this node's was refused, and it is said so. Stagewatch
                // closes the socket after it, so it is held as every refused admission is, or each dial would be let in and shut again.
                Refuse("viewing code asked — " + error,
                    $"Stagewatch at {Address} lets in only what gives its viewing code or a key — {error}", HoldFor(retry));
                return;
            }
            var words = refused.Length > 0 ? refused : error.Length > 0 ? error : "refused";
            Refuse(KeyRefused + words, $"Stagewatch at {Address} refused this node's key — {words}", HoldFor(retry));
            return;
        }
        var node = JsonText.Obj(r, "node");
        if (node.ValueKind != JsonValueKind.Object || !string.Equals(JsonText.Str(node, "id"), _o.SelfId, StringComparison.Ordinal))
            return;
        var verified = JsonText.Str(node, "verified");
        if (!_keyed)
        {
            // No key went on this socket: with none set there is nothing to prove; a key held back is not proved by a verdict on none.
            if (_o.Key.Length == 0)
                Pair(PairState.NotNeeded);
            return;
        }
        if (verified is "key" or "operator")
        {
            Pair(PairState.Paired);
            return;
        }
        var why = Bounded(JsonText.Str(r, "words"));
        if (why.Length == 0)
            why = $"Stagewatch lets this key speak for no node {_o.SelfId}";
        Refuse(KeyRefused + why, $"Stagewatch at {Address} does not take this node's key for it — {why}", TimeSpan.Zero);
    }

    /// <summary>The pairing settles as <paramref name="state"/>, the refusal and its hold lifted. A verdict (<paramref name="asked"/> −1) is
    /// the latest word; a side call's answer (<paramref name="asked"/>: the count when it left) settles only while no verdict or refusal
    /// came after it. Answers whether it settled.</summary>
    private bool Pair(PairState state, long asked = -1)
    {
        bool lifted;
        bool moved;
        lock (_gate)
        {
            if (asked >= 0 && asked != _verdicts)
                return false;
            if (asked < 0)
                _verdicts++;
            lifted = _refusal.Length > 0;
            moved = Volatile.Read(ref _pairing) != (int)state;
            _refusal = "";
            Volatile.Write(ref _refusedUntilNs, 0);
            Volatile.Write(ref _pairing, (int)state);
            // Paired, or nothing asked, settles the connection up now: from then it counts toward the backoff's start over.
            if (state is PairState.Paired or PairState.NotNeeded)
                Interlocked.CompareExchange(ref _settledNs, _mono.NowNs, -1);
        }
        if (lifted)
        {
            _words = _upWords;
            _log.LogInformation("Stagewatch at {Address} takes this node's key again.", Address);
        }
        if (moved || lifted)
            RaisePairing(state);
        return true;
    }

    private void RaisePairing(PairState state)
    {
        try
        {
            PairingChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            // A listener that faults is the listener's: the link goes on.
            _log.LogWarning(ex, "A listener to the Stagewatch link's pairing faulted.");
        }
    }

    /// <summary>The pairing is <see cref="PairState.Refused"/> with <paramref name="refusal"/>, and keyed calls and the next dial are
    /// held <paramref name="hold"/> (a hold standing is never shortened), all before the link does anything else. A side call's answer
    /// (<paramref name="asked"/>: the count when it left) refuses only while no verdict came after it.</summary>
    private void Refuse(string refusal, string words, TimeSpan hold, long asked = -1)
    {
        bool changed;
        lock (_gate)
        {
            if (asked >= 0 && asked != _verdicts)
                return;
            _verdicts++;
            if (hold > TimeSpan.Zero)
            {
                var until = _mono.NowNs + hold.Ticks * 100;
                if (until > Volatile.Read(ref _refusedUntilNs))
                    Volatile.Write(ref _refusedUntilNs, until);
            }
            changed = !string.Equals(_refusal, refusal, StringComparison.Ordinal) || Volatile.Read(ref _pairing) != (int)PairState.Refused;
            _refusal = refusal;
            Volatile.Write(ref _pairing, (int)PairState.Refused);
            // A refused connection is not a stable one, however long it stays up as a public viewer.
            Interlocked.Exchange(ref _settledNs, -1);
            _stable = false;
        }
        _words = words;
        if (!changed)
            return;
        _log.LogWarning("Stagewatch at {Address} refused this node: {Refusal}", Address, refusal);
        RaisePairing(PairState.Refused);
    }

    /// <summary>A refusal's hold: its <c>retry</c> or <c>Retry-After</c> in seconds, at least <see cref="RefusedHold"/> and at most
    /// <see cref="MaxRefusedHold"/>.</summary>
    private static TimeSpan HoldFor(long seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, (long)RefusedHold.TotalSeconds, (long)MaxRefusedHold.TotalSeconds));

    /// <summary>A far end's words, cut to <see cref="MaxRefusalChars"/>.</summary>
    private static string Bounded(string words) => words.Length > MaxRefusalChars ? words[..MaxRefusalChars] : words;

    /// <summary>Keyed side calls may go: the connection carries the key, its first message (a verdict among it) is read, and neither a
    /// refusal nor a rate limit holds the key.</summary>
    private bool KeyedCallsOpen() => _keyed && _heard && RefusedFor == TimeSpan.Zero && BusyFor == TimeSpan.Zero;

    /// <summary>Every beat: the ping (clock exchange) and the liveness check, nothing else, so no side call ever waits in its path.</summary>
    private async Task BeatAsync(ClientWebSocket ws, SemaphoreSlim send, CancellationToken ct)
    {
        var lastPing = long.MinValue;
        var step = Step;
        do
        {
            var silent = TimeSpan.FromTicks((_mono.NowNs - Interlocked.Read(ref _lastHeardNs)) / 100);
            if (silent > _o.DropAfter)
            {
                _words = string.Create(CultureInfo.InvariantCulture, $"Stagewatch at {Address} silent for {silent.TotalSeconds:0.#} s — dropped, dialling again");
                ws.Abort();
                return;
            }
            if (silent > _o.StaleAfter && Status == LinkStatus.Live)
                SetStatus(LinkStatus.Stale);
            if (Interlocked.Read(ref _settledNs) is >= 0 and var settled && _mono.NowNs - settled >= _o.StableAfter.Ticks * 100)
                _stable = true;
            var now = Stopwatch.GetTimestamp();
            if (Due(ref lastPing, now, _o.BeatEvery))
            {
                var t1 = _wall.UnixMs;
                lock (_gate)
                {
                    _pings.Add(t1);
                    if (_pings.Count > PeerLink.PingsMatched)
                        _pings.RemoveAt(0);
                }
                var ping = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"cmd\":\"ping\",\"t\":{t1}}}"));
                await send.WaitAsync(ct);
                try
                {
                    await ws.SendAsync(ping, WebSocketMessageType.Text, true, ct);
                }
                finally
                {
                    send.Release();
                }
            }
        }
        while (await Waits.DelayAsync(step, ct));
    }

    /// <summary>The step the beat and the side-call lane look again on: an eighth of the stale mark, between 20 and 250 ms.</summary>
    private TimeSpan Step => TimeSpan.FromMilliseconds(Math.Clamp(_o.StaleAfter.TotalMilliseconds / 8, 20, 250));

    /// <summary>The side-call lane (Show Bus 1.3 §13.7.2): one call at a time, each within <see cref="SideCallWithin"/> — this node's
    /// context every five seconds, the results owed when each is due, and, when the Stagewatch is an approval desk, the lines approved
    /// for this node. They carry the key, so they run only on a connection that carries it, once its verdict could have been read, and
    /// never while a refusal or a rate limit holds the key. Approved lines run as a paired session's, so they are collected only once
    /// Stagewatch took the key for this node (its verdict, or a keyed 2xx from one that sends none, which the context post asks for
    /// first). A call's failure is its own: it never moves the link's liveness.</summary>
    private async Task SideCallsAsync(HttpClient http, CancellationToken ct)
    {
        var lastContext = long.MinValue;
        var lastApprovals = long.MinValue;
        var said = new bool[_sideCallFaults.Length];
        var step = Step;
        do
        {
            var now = Stopwatch.GetTimestamp();
            if (KeyedCallsOpen() && Due(ref lastContext, now, _o.ContextEvery))
                await SideCallAsync(StagewatchSideCall.Context, () => PostContextAsync(http, ct), said, ct);
            if (KeyedCallsOpen())
                await SideCallAsync(StagewatchSideCall.Results, () => PostOwedAsync(http, ct), said, ct);
            if (_o.Approvals && KeyedCallsOpen() && Pairing == PairState.Paired && Due(ref lastApprovals, now, _o.ApprovalsEvery))
                await SideCallAsync(StagewatchSideCall.Approvals, () => ApprovalsAsync(http, ct), said, ct);
        }
        while (await Waits.DelayAsync(step, ct));
    }

    /// <summary>One call on the side-call lane. A fault none of the call's own answers names (a reader of this node's that throws, a
    /// bug) is the call's own (Show Bus 1.3 §13.7.2): counted on its item, logged once a connection (<paramref name="said"/>), and the
    /// lane goes on with the next call; only the connection's end stops it.</summary>
    private async Task SideCallAsync(StagewatchSideCall call, Func<Task> run, bool[] said, CancellationToken ct)
    {
        try
        {
            await run();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sideCallFaults[(int)call]);
            if (said[(int)call])
            {
                _log.LogDebug(ex, "The Stagewatch link's {Call} side call faulted again.", call);
                return;
            }
            said[(int)call] = true;
            _log.LogWarning(ex, "The Stagewatch link's {Call} side call faulted: it is counted, and the other side calls go on.", call);
        }
    }

    /// <summary>Waits for one of the connection's helpers to end with it. A helper's own fault ends with the connection too: the link
    /// dials again, never stops for good.</summary>
    private async Task Ended(Task helper, string what)
    {
        try
        {
            await helper;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or HttpRequestException or IOException)
        {
            // The connection's helpers end with it.
            _log.LogDebug(ex, "The Stagewatch {Helper} ended with its link.", what);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The Stagewatch {Helper} faulted; the link dials again.", what);
        }
    }

    /// <summary>A pong: a clock sample only when its t1 is one of the last <see cref="PeerLink.PingsMatched"/> pings this connection
    /// wrote (Show Bus 1.3 §13.10.2), and only one 13.11.2 admits; any other is counted as unmatched. A pong never clears a stuck show.</summary>
    private void Pong(long t1, long t2, long t3)
    {
        lock (_gate)
        {
            if (!_pings.Remove(t1))
            {
                Interlocked.Increment(ref _unmatched);
                return;
            }
            _offset.Add(new ClockSample(t1, t2, t3, _wall.UnixMs));
        }
    }

    private static bool Due(ref long last, long now, TimeSpan every)
    {
        if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < every)
            return false;
        last = now;
        return true;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? json = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (_keyed)
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _o.Key);
        if (json is not null)
            r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>The count of verdicts and refusals now, which a side call carries out to its answer.</summary>
    private long Asked()
    {
        lock (_gate)
            return _verdicts;
    }

    /// <summary>A keyed side call answered other than 2xx (its body is read already: the client buffers it before it answers).
    /// A 401 or 403 refuses the key with Stagewatch's words, held for its <c>Retry-After</c> (at least <see cref="RefusedHold"/>). A 429
    /// is Stagewatch's rate limit on a good key: keyed calls and the next dial wait its <c>Retry-After</c>, and the pairing stands. Any
    /// other answer is said.</summary>
    private async Task RefusedAsync(HttpResponseMessage response, string call, long asked, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        var error = "";
        using (var doc = JsonText.TryParse(await response.Content.ReadAsStringAsync(ct), MaxMessageBytes))
        {
            if (doc is not null)
                error = Bounded(JsonText.Str(doc.RootElement, "error"));
        }
        var words = error.Length > 0 ? error : string.Create(CultureInfo.InvariantCulture, $"HTTP {code}");
        var after = response.Headers.RetryAfter?.Delta is { } delta ? (long)Math.Ceiling(delta.TotalSeconds) : 0;
        if (code == 429)
        {
            Busy(after, words, asked);
            return;
        }
        if (code is 401 or 403)
        {
            Refuse(KeyRefused + words, $"Stagewatch at {Address} refused this node's {call} — {words}", HoldFor(after), asked);
            return;
        }
        _words = $"Stagewatch at {Address} refused this node's {call} — {words}";
    }

    /// <summary>Stagewatch's rate limit: keyed calls and the next dial wait <paramref name="seconds"/> (its <c>Retry-After</c>; without
    /// one, <see cref="RefusedHold"/>), at most <see cref="MaxRefusedHold"/>; a hold standing is never shortened. The key is good, so the
    /// pairing is left as it stands. An answer a later verdict or refusal overtook (<paramref name="asked"/>) says nothing.</summary>
    private void Busy(long seconds, string words, long asked)
    {
        var wait = seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, (long)MaxRefusedHold.TotalSeconds)) : RefusedHold;
        lock (_gate)
        {
            if (asked != _verdicts)
                return;
            var until = _mono.NowNs + wait.Ticks * 100;
            if (until > Volatile.Read(ref _busyUntilNs))
                Volatile.Write(ref _busyUntilNs, until);
        }
        _words = string.Create(CultureInfo.InvariantCulture,
            $"Stagewatch at {Address} rate-limits this node's key — next try in {wait.TotalSeconds:0} s ({words})");
        _log.LogWarning("Stagewatch at {Address} rate-limits this node's key for {Seconds} s.", Address, (long)wait.TotalSeconds);
    }

    /// <summary>This node's context (§6) to Stagewatch's show network (and the key bound to this node's id for the
    /// approval queue). Its 2xx pairs the link, unless Stagewatch keeps the context unverified (the key speaks for no node); a refusal
    /// is <see cref="RefusedAsync"/>'s. The first authenticated answer over TLS pins the certificate.</summary>
    private async Task PostContextAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var asked = Asked();
            using var request = Request(HttpMethod.Post, "/api/peers/context", _endpoint.Context());
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RefusedAsync(response, "context", asked, ct);
                return;
            }
            using (var doc = JsonText.TryParse(await response.Content.ReadAsStringAsync(ct), MaxMessageBytes))
            {
                if (doc is not null && JsonText.Str(doc.RootElement, "verified") == "none")
                {
                    const string unverified = "Stagewatch keeps this node's context unverified — only a node key made for this node speaks for it";
                    Refuse(KeyRefused + unverified, $"Stagewatch at {Address}: {unverified}", TimeSpan.Zero, asked);
                    return;
                }
            }
            // The pairing first, then the pin: an answer a later verdict overtook pairs nothing and pins nothing.
            if (Pair(PairState.Paired, asked) && _overTls && Pin == PinVerdict.FirstUse && _presented.Length > 0 && _o.PinKey.Length > 0)
            {
                // The verdict first, then the store (whose lock publishes both): whoever sees the pin stored sees Pinned.
                Pin = PinVerdict.Pinned;
                _pins.Pin(_o.PinKey, _presented);
                _words = $"Stagewatch's certificate pinned ({TlsFingerprint.Short(_presented)})";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // Said again in five seconds; the frames are what the screen reads.
            _log.LogDebug(ex, "Posting the context to Stagewatch failed.");
        }
    }

    /// <summary>§11: collect what was approved for this node and run each once through the node's own command path (its fences still
    /// apply). Each is remembered by Stagewatch's identity, its id and when it was made (<see cref="RanKey"/>), and its result is recorded
    /// as owed before its line runs (Show Bus 1.3 §13.12.9), so a proposal still listed is never run twice and a result is never lost to
    /// a failed post: <see cref="PostOwedAsync"/> posts it, now and again until Stagewatch takes it.</summary>
    private async Task ApprovalsAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var asked = Asked();
            using var ask = Request(HttpMethod.Get, "/api/proposals?target=" + Uri.EscapeDataString(_o.SelfId) + "&state=approved");
            using var list = await http.SendAsync(ask, ct);
            if (!list.IsSuccessStatusCode)
            {
                await RefusedAsync(list, "collect", asked, ct);
                return;
            }
            foreach (var (id, target, line, created) in Proposals(await list.Content.ReadAsStringAsync(ct)))
            {
                if (target.Length > 0 && target != _o.SelfId)
                    continue;
                var key = RanKey(id, created);
                if (!Ran(key, id))
                    continue;
                (bool Ok, string Words) ran;
                try
                {
                    ran = await RunApprovedAsync(id, line, ct);
                }
                catch (Exception ex)
                {
                    // The line may have run before its path faulted or the connection ended: its result says so, owed as any other.
                    var ended = ct.IsCancellationRequested;
                    Owe(key, false, ended ? EndedAsItRan : FaultedAsItRan);
                    if (ended)
                        throw;
                    _log.LogWarning(ex, "An approved line from Stagewatch ({Proposal}) faulted as it ran: its result says it may have run.", id);
                    continue;
                }
                Owe(key, ran.Ok, ran.Words);
            }
            await PostOwedAsync(http, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException && !ct.IsCancellationRequested)
        {
            // The queue is asked again in two seconds; nothing ran twice (the proposals that ran are remembered) and every result is owed.
            _log.LogDebug(ex, "The approval queue on Stagewatch could not be read this time.");
        }
    }

    /// <summary>The result of a line whose command path faulted as it ran: it may have run.</summary>
    internal const string FaultedAsItRan = "this node's command path faulted as the line ran — it may have run; look at this node's state";

    /// <summary>The result of a line whose connection ended as it ran: it may have run.</summary>
    internal const string EndedAsItRan = "the link to Stagewatch ended as the line ran — it may have run; look at this node's state";

    /// <summary>The proposal <paramref name="key"/>'s line is done: its result (<paramref name="ok"/>, <paramref name="words"/> cut to
    /// 256 characters) is owed from now, posted on the lane's next turn.</summary>
    private void Owe(string key, bool ok, string words)
    {
        var result = JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            w.WriteString("words", words.Length > 256 ? words[..256] : words);
            w.WriteEndObject();
        });
        lock (_gate)
        {
            if (_owed.TryGetValue(key, out var owed))
                _owed[key] = owed with { Json = result, NextNs = _mono.NowNs };
        }
    }

    /// <summary>A proposal as this link remembers it: the Stagewatch it came from (its pin key, or its address), its id and when it was
    /// made, so a proposal another desk or a restarted one numbers alike is its own.</summary>
    private string RanKey(string id, long created) =>
        (_o.PinKey.Length > 0 ? _o.PinKey : "addr:" + Address) + "/" + id + "/" + created.ToString(CultureInfo.InvariantCulture);

    /// <summary>Records the proposal <paramref name="key"/> as run and its result as owed — before its line runs — and answers whether it
    /// is new: false for one remembered, which is never run again. Past <see cref="RanKept"/> the oldest remembered whose result is not
    /// owed is let go.</summary>
    private bool Ran(string key, string id)
    {
        lock (_gate)
        {
            if (_ran.ContainsKey(key))
                return false;
            var now = _mono.NowNs;
            _ran[key] = now;
            _ranOrder.Enqueue(key);
            _owed[key] = new OwedResult(id, null, now, 0, long.MaxValue);
            for (var n = _ranOrder.Count; n > 0 && _ran.Count > RanKept; n--)
            {
                var oldest = _ranOrder.Dequeue();
                if (_owed.ContainsKey(oldest))
                    _ranOrder.Enqueue(oldest);
                else
                    _ran.Remove(oldest);
            }
            return true;
        }
    }

    /// <summary>Posts each owed result that is due (Show Bus 1.3 §13.12.9): a 2xx, or a 409 (already settled), delivers it; a 404 or
    /// another refusal of the result itself lets it go, said once; a 401 or 403 refuses the key (its hold stops every keyed call) and a
    /// 429 waits its Retry-After, the result kept; anything else — no answer within the call's deadline, a 408, a 5xx — tries again from
    /// <c>report.retry.first.ms</c>, doubling to <c>report.retry.max.ms</c>, ±20 %, until it has been owed <c>report.retry.for.ms</c>.</summary>
    private async Task PostOwedAsync(HttpClient http, CancellationToken ct)
    {
        List<(string Key, OwedResult Owed)> due;
        var now = _mono.NowNs;
        lock (_gate)
        {
            foreach (var key in _owed.Where(o => !PeerLink.ReportStillOwed((now - o.Value.SinceNs) / Durations.NsPerMs)).Select(o => o.Key).ToList())
            {
                // A result whose line is still running past it is let go here too: it may have run, so the proposal stays remembered
                // and is never run again.
                _log.LogWarning("Stagewatch at {Address} did not take the result of proposal {Id} in {Minutes} min: it is no longer posted.", Address,
                    _owed[key].Id, PeerLink.ReportRetryForMs / 60_000);
                _owed.Remove(key);
            }
            due = _owed.Where(o => o.Value.Json is not null && o.Value.NextNs <= now).Select(o => (o.Key, o.Value)).ToList();
        }
        foreach (var (key, owed) in due)
        {
            if (!KeyedCallsOpen())
                return;
            var asked = Asked();
            int? code;
            try
            {
                using var answer = Request(HttpMethod.Post, "/api/proposals/" + Uri.EscapeDataString(owed.Id) + "/result", owed.Json);
                using var response = await http.SendAsync(answer, ct);
                code = (int)response.StatusCode;
                if (code is 401 or 403 or 429)
                    await RefusedAsync(response, "result", asked, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Posting a result to Stagewatch failed; it is owed still.");
                code = null;
            }
            Posted(key, owed, code);
        }
    }

    /// <summary>What one post of an owed result came to: <paramref name="code"/> its status, null when no answer came.</summary>
    private void Posted(string key, OwedResult owed, int? code)
    {
        lock (_gate)
        {
            if (!_owed.ContainsKey(key))
                return;
            switch (code)
            {
                case >= 200 and < 300 or 409:
                    _owed.Remove(key);
                    Interlocked.Increment(ref _resultsDelivered);
                    return;
                case 401 or 403 or 429:
                    // The key's hold or the rate limit stops keyed calls; the result waits for them to open again.
                    _owed[key] = owed with { NextNs = _mono.NowNs };
                    return;
                case >= 400 and < 500 and not 408:
                    // A 408 is a timeout, read with the 5xx (13.12.3): it is tried again below.
                    _owed.Remove(key);
                    _log.LogWarning("Stagewatch at {Address} would not take the result of proposal {Id} (HTTP {Code}): it is no longer posted.", Address,
                        owed.Id, code.Value);
                    return;
                default:
                    var wait = PeerLink.ReportRetryWait(owed.Tries, Jitter.Shared.Next01());
                    _owed[key] = owed with { Tries = owed.Tries + 1, NextNs = _mono.NowNs + wait * Durations.NsPerMs };
                    return;
            }
        }
    }

    private async Task<(bool Ok, string Words)> RunApprovedAsync(string id, string text, CancellationToken ct)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return (false, error!.Reason);
        if (line.Verb is "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE")
            return (false, WireWords.WireOnly);
        var session = new SessionInfo("a" + id, "approval", _o.Host, false, "Stagewatch approval desk", Paired: true);
        var reply = await _runner.RunAsync(line, session, ct);
        Interlocked.Increment(ref _approvalsRun);
        _log.LogInformation("Ran an approved line from Stagewatch ({Proposal}): {Outcome}.", id, reply.Ok ? "done" : "refused");
        return reply.Ok
            ? (true, reply.Effect.Length > 0 ? reply.Effect : reply.Payload ?? "done")
            : (false, reply.Payload ?? "refused");
    }

    /// <summary>A list reply: a bare array or an object with "proposals"; each with its id, target id, line and when it was made (0 when
    /// it says not).</summary>
    private static List<(string Id, string Target, string Line, long Created)> Proposals(string json)
    {
        var list = new List<(string, string, string, long)>();
        using var doc = JsonText.TryParse(json, 1024 * 1024);
        if (doc is null)
            return list;
        var r = doc.RootElement;
        var array = r.ValueKind == JsonValueKind.Array ? r
            : r.ValueKind == JsonValueKind.Object && r.TryGetProperty("proposals", out var p) && p.ValueKind == JsonValueKind.Array ? p
            : default;
        if (array.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in array.EnumerateArray().Take(200))
        {
            var id = JsonText.Str(e, "id");
            var state = JsonText.Str(e, "state");
            if (id.Length is 0 or > 64 || (state.Length > 0 && state != "approved"))
                continue;
            list.Add((id, JsonText.Str(JsonText.Obj(e, "target"), "id"), JsonText.Str(e, "line"), JsonText.Long(e, "created")));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException ex)
            {
                // The loop ends on the stop it was given.
                _log.LogDebug(ex, "The Stagewatch link stopped.");
            }
        }
        _stop.Dispose();
    }
}
