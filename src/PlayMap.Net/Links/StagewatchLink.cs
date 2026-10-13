using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
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
    /// in Authorization: Bearer — never in the URL.</summary>
    public string Key { get; init; } = "";

    /// <summary>Over TLS (wss/https), pinned — never trusted by a name or an authority.</summary>
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

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs);

    public TimeSpan DropAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.GoneMs);

    /// <summary>After the operator's "disconnect" the link waits this long before it dials again.</summary>
    public TimeSpan DisconnectHold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A Stagewatch heard on the hello: its http (or https) port, TLS when it names a certificate, the
    /// approval desk when it has the cap.</summary>
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

/// <summary>PlayMap as a screen of a Stagewatch (Show Bus §3–§5, §10, §11): its <c>/ws</c> as
/// <c>?cid=&lt;this node&gt;&amp;page=/playmap&amp;detail=1</c> with the key in the Authorization header, state frames
/// taken latest-wins on seq and counted on locally from their arrival, <c>{"type":"beat","live":false}</c> read as
/// "the time is not a fact" ("?", never a frozen time), the operator's screen commands raised as events, a ping
/// every 2 s (the clock offset), this node's context posted every 5 s, and — when the Stagewatch is an approval desk —
/// the lines approved for this node collected every 2 s, run through the node's own command path and answered.
/// Stale after 6 s of silence, dropped and redialled after 12 s. Over TLS the certificate is checked against the pin.</summary>
public sealed class StagewatchLink : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly StagewatchLinkOptions _o;
    private readonly IWireEndpoint _endpoint;
    private readonly ShowWallClock _wall;
    private readonly PinStore _pins;
    private readonly IMonoClock _mono;
    private readonly ILogger _log;
    private readonly CommandRunner _runner;
    private readonly ClockOffsetEstimator _offset = new(ShowBusConstants.OffsetWindow);
    private readonly object _gate = new();
    private readonly HashSet<string> _ran = new(StringComparer.Ordinal);
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
    private long _approvalsRun;
    private long _connections;

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

    /// <summary>What the pin made of the certificate last presented (null over plain).</summary>
    public PinVerdict? Pin { get; private set; }

    /// <summary>A certificate refused as CERTIFICATE CHANGED, waiting for the operator (<see cref="AcceptPendingCertificate"/>).</summary>
    public string PendingFingerprint { get; private set; } = "";

    public long ApprovalsRun => Interlocked.Read(ref _approvalsRun);

    public long Connections => Interlocked.Read(ref _connections);

    public event Action<LinkStatus>? StatusChanged;

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
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(LinkStatus.Connecting);
            var hold = TimeSpan.Zero;
            try
            {
                hold = await ConnectionAsync(ct);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or System.Security.Authentication.AuthenticationException)
            {
                _words = PendingFingerprint.Length > 0 ? _words : "Stagewatch at " + Address + ": " + Brief(ex);
                _log.LogDebug(ex, "The Stagewatch link to {Address} ended.", Address);
                attempt++;
            }
            SetStatus(LinkStatus.Down);
            if (!await Waits.DelayAsync(hold > TimeSpan.Zero ? hold : Jitter.Shared.Redial(attempt), ct))
                break;
        }
        SetStatus(LinkStatus.Down);
    }

    private static string Brief(Exception ex) =>
        ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal)
            ? "refused — check the Stagewatch key"
            : ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;

    private RemoteCertificateValidationCallback Validator() => _pins.Validator(_o.PinKey, _o.Fingerprint, (verdict, presented) =>
    {
        Pin = verdict;
        _presented = presented;
        _words = PinStore.Words(verdict, "Stagewatch", presented);
        if (verdict == PinVerdict.Changed)
            PendingFingerprint = presented;
    });

    /// <summary>One connection; answers how long to wait before the next dial (the operator's disconnect asks for a
    /// longer wait than the backoff).</summary>
    private async Task<TimeSpan> ConnectionAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = linked.Token;
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;   // the Show Bus beat is the keep-alive
        if (_o.Key.Length > 0)
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _o.Key);
        if (_o.Tls)
            ws.Options.RemoteCertificateValidationCallback = Validator();
        var host = _o.Host.Contains(':', StringComparison.Ordinal) ? "[" + _o.Host + "]" : _o.Host;
        var cid = _o.SelfId.Length is >= 6 and <= 32 && _o.SelfId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? _o.SelfId : "playmap-" + _o.SelfId;
        var uri = new Uri($"{(_o.Tls ? "wss" : "ws")}://{host}:{_o.Port.ToString(CultureInfo.InvariantCulture)}/ws?cid={Uri.EscapeDataString(cid)}&page=/playmap&detail=1");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(uri, timeout.Token);
        }
        Interlocked.Increment(ref _connections);
        lock (_gate)
            _lastSeq = -1;
        Interlocked.Exchange(ref _lastHeardNs, _mono.NowNs);
        SetStatus(LinkStatus.Live);
        _words = "following Stagewatch at " + Address + (_o.Tls ? " over TLS" : "");
        if (_o.Tls && _o.Key.Length == 0 && Pin == PinVerdict.FirstUse)
        {
            // No key: the WebSocket's answer is the first trusted contact. The link says so before the store holds it,
            // so whoever sees the pin stored sees the link's verdict too.
            Pin = PinVerdict.Pinned;
            _pins.Pin(_o.PinKey, _presented);
        }

        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) };
        if (_o.Tls)
            handler.SslOptions.RemoteCertificateValidationCallback = Validator();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4), BaseAddress = new Uri($"{(_o.Tls ? "https" : "http")}://{host}:{_o.Port.ToString(CultureInfo.InvariantCulture)}") };
        using var send = new SemaphoreSlim(1, 1);
        var beat = Task.Run(() => BeatAsync(ws, send, http, ct), CancellationToken.None);
        TimeSpan hold;
        try
        {
            hold = await ReceiveAsync(ws, ct);
        }
        finally
        {
            await linked.CancelAsync();
            try
            {
                await beat;
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or HttpRequestException or IOException)
            {
                // The beat ends with the link.
                _log.LogDebug(ex, "The Stagewatch beat ended with its link.");
            }
        }
        return hold;
    }

    private async Task<TimeSpan> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>(16 * 1024);
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                return TimeSpan.Zero;
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
                continue;   // not JSON: not a frame, and not the link's end
            }
            message.ResetWrittenCount();
            using (doc)
            {
                if (Take(doc.RootElement, now) is { } hold)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnected by the operator", CancellationToken.None);
                    return hold;
                }
            }
        }
        return TimeSpan.Zero;
    }

    /// <summary>One message from Stagewatch; a non-null answer ends the connection and holds the redial that long.</summary>
    private TimeSpan? Take(JsonElement r, long now)
    {
        if (r.ValueKind != JsonValueKind.Object)
            return null;
        var type = JsonText.Str(r, "type");
        switch (type)
        {
            case "beat":
                _stuck = JsonText.Bool(r, "live", true) is false;
                return null;
            case "pong":
                var t1 = JsonText.Long(r, "t");
                var t2 = JsonText.Long(r, "t2");
                var t3 = JsonText.Long(r, "t3");
                if (t1 > 0 && t2 > 0 && t3 > 0)
                {
                    lock (_gate)
                        _offset.Add(new ClockSample(t1, t2, t3, _wall.UnixMs));
                }
                return null;
            case "redirect" or "identify" or "reload" or "disconnect":
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
                return type == "disconnect" ? _o.DisconnectHold : null;
            case "auth" when !JsonText.Bool(r, "ok", true):
            case "error":
                _words = "Stagewatch says: " + JsonText.Str(r, "error");
                return null;
            case "state" or "":
                if (StagewatchTimer.Parse(r) is not { } frame)
                    return null;
                lock (_gate)
                {
                    if (frame.Seq > 0 && _lastSeq > 0 && frame.Seq < _lastSeq)
                        return null;   // an older frame overtaken by a newer one: latest wins
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
                return null;
            default:
                return null;
        }
    }

    /// <summary>Every beat: the ping (clock exchange), the liveness check, the approval queue; every five seconds this
    /// node's context.</summary>
    private async Task BeatAsync(ClientWebSocket ws, SemaphoreSlim send, HttpClient http, CancellationToken ct)
    {
        var lastPing = long.MinValue;
        var lastContext = long.MinValue;
        var lastApprovals = long.MinValue;
        var step = TimeSpan.FromMilliseconds(Math.Clamp(_o.StaleAfter.TotalMilliseconds / 8, 20, 250));
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
            var now = Stopwatch.GetTimestamp();
            if (Due(ref lastPing, now, _o.BeatEvery))
            {
                var ping = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"cmd\":\"ping\",\"t\":{_wall.UnixMs}}}"));
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
            if (_o.Key.Length > 0 && Due(ref lastContext, now, _o.ContextEvery))
                await PostContextAsync(http, ct);
            if (_o.Approvals && _o.Key.Length > 0 && Due(ref lastApprovals, now, _o.ApprovalsEvery))
                await ApprovalsAsync(http, ct);
        }
        while (await Waits.DelayAsync(step, ct));
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
        if (_o.Key.Length > 0)
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _o.Key);
        if (json is not null)
            r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>This node's context (§6) to Stagewatch's show network (and the key bound to this node's id for the
    /// approval queue). The first authenticated answer over TLS pins the certificate.</summary>
    private async Task PostContextAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Post, "/api/peers/context", _endpoint.Context());
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode && _o.Tls && Pin == PinVerdict.FirstUse && _presented.Length > 0)
            {
                // The verdict first, then the store (whose lock publishes both): whoever sees the pin stored sees Pinned.
                Pin = PinVerdict.Pinned;
                _pins.Pin(_o.PinKey, _presented);
                _words = $"Stagewatch's certificate pinned ({TlsFingerprint.Short(_presented)})";
            }
            else if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                _words = "the Stagewatch key may not post this node's context — make a node key on the Stagewatch";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // Said again in five seconds; the frames are what the screen reads.
            _log.LogDebug(ex, "Posting the context to Stagewatch failed.");
        }
    }

    /// <summary>§11: collect what was approved for this node, run each once through the node's own command path (its
    /// fences still apply) and answer the result.</summary>
    private async Task ApprovalsAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var ask = Request(HttpMethod.Get, "/api/proposals?target=" + Uri.EscapeDataString(_o.SelfId) + "&state=approved");
            using var list = await http.SendAsync(ask, ct);
            if (!list.IsSuccessStatusCode)
                return;
            foreach (var (id, target, line) in Proposals(await list.Content.ReadAsStringAsync(ct)))
            {
                if (target.Length > 0 && target != _o.SelfId)
                    continue;
                lock (_gate)
                {
                    if (!_ran.Add(id))
                        continue;
                    if (_ran.Count > 1000)
                        _ran.Clear();
                }
                var (ok, words) = await RunApprovedAsync(id, line, ct);
                var result = JsonText.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("ok", ok);
                    w.WriteString("words", words.Length > 256 ? words[..256] : words);
                    w.WriteEndObject();
                });
                using var answer = Request(HttpMethod.Post, "/api/proposals/" + Uri.EscapeDataString(id) + "/result", result);
                using var _ = await http.SendAsync(answer, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException && !ct.IsCancellationRequested)
        {
            // The queue is asked again in two seconds; nothing ran twice (the ids that ran are kept).
            _log.LogDebug(ex, "The approval queue on Stagewatch could not be read this time.");
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

    /// <summary>A list reply: a bare array or an object with "proposals"; each with its id, target id and line.</summary>
    private static List<(string Id, string Target, string Line)> Proposals(string json)
    {
        var list = new List<(string, string, string)>();
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
            list.Add((id, JsonText.Str(JsonText.Obj(e, "target"), "id"), JsonText.Str(e, "line")));
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
