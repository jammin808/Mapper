using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Links;

/// <summary>The clip a Patterns desk has on air, as its STATE's <c>video</c> row says (round 86+: milliseconds,
/// paused, rate and a seq that moves on every discontinuity). <see cref="LengthMs"/> is null until the desk knows it.</summary>
public sealed record PatternsVideo(string File, string Tag, double PositionMs, double? LengthMs, bool Playing, bool Paused, bool Ended, bool Loops, double Rate, long Seq)
{
    /// <summary>The clip runs on its own: playing, not paused, not ended.</summary>
    public bool Running => Playing && !Paused && !Ended;

    public double? RemainingMs => LengthMs is { } l && !Loops ? Math.Max(0, l - PositionMs) : null;

    /// <summary>The row, or null when there is none (no clip on air) or it says no position.</summary>
    public static PatternsVideo? Parse(JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object)
            return null;
        var position = JsonText.NumOrNull(video, "positionMs") ?? JsonText.NumOrNull(video, "position") * 1000;
        if (position is not { } pos)
            return null;
        var length = JsonText.NumOrNull(video, "lengthMs") ?? (JsonText.NumOrNull(video, "length") is { } l and > 0 ? l * 1000 : null);
        var rate = JsonText.Num(video, "rate", 1);
        return new PatternsVideo(JsonText.Str(video, "file"), JsonText.Str(video, "tag"), pos, length is > 0 ? length : null,
            JsonText.Bool(video, "playing"), JsonText.Bool(video, "paused"), JsonText.Bool(video, "ended"), JsonText.Bool(video, "loops"),
            rate is > 0 and <= 20 ? rate : 1, JsonText.Long(video, "seq", -1));
    }

    /// <summary>The clip counted on by <paramref name="elapsedMs"/> of real time at its rate (clamped to its end,
    /// wrapped when it loops).</summary>
    public PatternsVideo At(double elapsedMs)
    {
        if (!Running || elapsedMs <= 0)
            return this;
        var p = PositionMs + elapsedMs * Rate;
        if (LengthMs is { } l)
            p = Loops ? p % l : Math.Min(p, l);
        return this with { PositionMs = p };
    }
}

public sealed record PatternsLinkOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = ShowBusConstants.PatternsWirePort;

    /// <summary>The desk's pairing token ("" = none).</summary>
    public string Token { get; init; } = "";

    public string ClientName { get; init; } = "PlayMap";

    public TimeSpan ContextEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A running clip with no fresh STATE for this long is not trusted (an old countdown is worse than none).</summary>
    public TimeSpan VideoStaleAfter { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs);

    public TimeSpan DropAfter { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.GoneMs);

    /// <summary>"host", "host:port" or "[v6]:port" as an operator types it (the port defaults to the wire's).</summary>
    public static (string Host, int Port)? ParseAddress(string? typed, int defaultPort = ShowBusConstants.PatternsWirePort)
    {
        var t = (typed ?? "").Trim();
        if (t.Length is 0 or > 255 || t.Contains('/', StringComparison.Ordinal) || t.Contains('@', StringComparison.Ordinal) || t.Contains(' ', StringComparison.Ordinal))
            return null;
        if (t.StartsWith('['))
            return IPEndPoint.TryParse(t, out var ep) && t.Contains("]:", StringComparison.Ordinal) ? (ep.Address.ToString(), ep.Port) : null;
        var colons = t.Count(c => c == ':');
        if (colons > 1)
            return IPAddress.TryParse(t, out var v6) ? (v6.ToString(), defaultPort) : null;
        if (colons == 1)
        {
            var i = t.IndexOf(':', StringComparison.Ordinal);
            return int.TryParse(t[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 && Uri.CheckHostName(t[..i]) != UriHostNameType.Unknown
                ? (t[..i], port) : null;
        }
        return Uri.CheckHostName(t) != UriHostNameType.Unknown ? (t, defaultPort) : null;
    }
}

/// <summary>PlayMap following a Patterns desk over its line wire (TCP 9697): STATE then NONCE on connect, HELLO
/// PlayMap, AUTH-PROOF with the desk's token, PING every 2 s (the desk's clock offset), CONTEXT every 5 s. The desk's
/// clip clock (STATE.video) is held as an anchor and counted on locally at its rate — re-anchored when its seq moves
/// (a seek, a pause, a new clip) or when a frame strays from the count — and the desk's blackout is followed. Stale
/// after 6 s of silence, dropped and redialled after 12 s; nothing is queued while the link is down.</summary>
public sealed class PatternsLink : IAsyncDisposable
{
    private const double ResyncMs = 250;

    private readonly PatternsLinkOptions _o;
    private readonly WireClient _wire;
    private readonly IMonoClock _mono;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private PatternsVideo? _anchor;
    private long _anchorAtNs;
    private long _frameAtNs;
    private bool? _blackout;
    private string? _state;
    private string? _context;
    private Task? _contextLoop;

    public PatternsLink(PatternsLinkOptions options, ShowWallClock wall, IMonoClock? mono = null, ILogger? log = null)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _mono = mono ?? MonoClock.Instance;
        _log = log;
        _wire = new WireClient(new WireClientOptions
        {
            Host = options.Host,
            Port = options.Port,
            ClientName = options.ClientName,
            Token = options.Token,
            IdPrefix = "pm",
            StaleAfter = options.StaleAfter,
            DropAfter = options.DropAfter,
            MaxLineBytes = 1024 * 1024,
        }, wall, log);
        _wire.Line += OnLine;
        _wire.StatusChanged += s => StatusChanged?.Invoke(s);
    }

    public LinkStatus Status => _wire.Status;

    /// <summary>Paired by proof, or the desk asked none.</summary>
    public bool Paired => _wire.Pairing is PairState.Paired or PairState.NotNeeded;

    public PairState Pairing => _wire.Pairing;

    public string Address => _wire.Address;

    public long Connections => _wire.Connections;

    public event Action<LinkStatus>? StatusChanged;

    /// <summary>The clip changed discontinuously (new clip, seek, pause, play, rate) or went off air (null).</summary>
    public event Action<PatternsVideo?>? VideoChanged;

    /// <summary>The desk's blackout moved.</summary>
    public event Action<bool>? BlackoutChanged;

    /// <summary>The desk's latest STATE (null before the first).</summary>
    public string? StateJson
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>The desk's Show Bus context, refreshed every 5 s (null when the link is down).</summary>
    public string? ContextJson
    {
        get
        {
            lock (_gate)
                return Status is LinkStatus.Live or LinkStatus.Stale ? _context : null;
        }
    }

    public bool? Blackout
    {
        get
        {
            lock (_gate)
                return Status is LinkStatus.Live or LinkStatus.Stale ? _blackout : null;
        }
    }

    /// <summary>The desk's clip counted on to now, or null: no clip, the link is not live, or a running clip whose
    /// STATE has not come for <see cref="PatternsLinkOptions.VideoStaleAfter"/>.</summary>
    public PatternsVideo? Video
    {
        get
        {
            lock (_gate)
            {
                if (_anchor is not { } v || Status != LinkStatus.Live)
                    return null;
                var now = _mono.NowNs;
                if (v.Running && Durations.ToMs(now - _frameAtNs) > _o.VideoStaleAfter.TotalMilliseconds)
                    return null;
                return v.At(Durations.ToMs(now - _anchorAtNs));
            }
        }
    }

    /// <summary>The desk's clock minus this machine's (ms), with the round trip, once a PING exchange closed.</summary>
    public (double OffsetMs, double RoundTripMs, int Samples) Clock => _wire.Clock;

    public void Start()
    {
        _wire.Start();
        _contextLoop ??= Task.Run(() => ContextAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Sends a line to the desk (a request id is added; the reply is matched by it). Fails at once while the
    /// link is down.</summary>
    public Task<WireReply> SendAsync(string line, CancellationToken ct = default) => _wire.SendAsync(line, ct);

    private void OnLine(string line)
    {
        if (!line.StartsWith("STATE ", StringComparison.Ordinal))
            return;
        var json = line[6..];
        using var doc = JsonText.TryParse(json, 1024 * 1024);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return;
        var root = doc.RootElement;
        var video = root.TryGetProperty("video", out var v) ? PatternsVideo.Parse(v) : null;
        var blackout = JsonText.BoolOrNull(root, "blackout") ?? JsonText.BoolOrNull(JsonText.Obj(root, "air"), "blackout");
        var now = _mono.NowNs;
        bool videoMoved;
        bool blackoutMoved;
        PatternsVideo? announced;
        lock (_gate)
        {
            _state = json;
            _frameAtNs = now;
            videoMoved = Anchor(video, now);
            announced = _anchor;
            blackoutMoved = blackout is { } b && _blackout != b;
            if (blackout is not null)
                _blackout = blackout;
        }
        if (videoMoved)
            VideoChanged?.Invoke(announced);
        if (blackoutMoved)
            BlackoutChanged?.Invoke(blackout!.Value);
    }

    /// <summary>Re-anchors on a discontinuity; keeps counting through frames that agree with the count. True when the
    /// clip moved discontinuously.</summary>
    private bool Anchor(PatternsVideo? video, long now)
    {
        var old = _anchor;
        if (video is null)
        {
            _anchor = null;
            return old is not null;
        }
        if (old is not null && old.Seq == video.Seq && old.File == video.File && old.Running == video.Running && Math.Abs(old.Rate - video.Rate) < 0.001)
        {
            var counted = old.At(Durations.ToMs(now - _anchorAtNs));
            if (Math.Abs(counted.PositionMs - video.PositionMs) <= ResyncMs)
            {
                _anchor = old with { LengthMs = video.LengthMs, Ended = video.Ended, Loops = video.Loops };
                return false;
            }
        }
        _anchor = video;
        _anchorAtNs = now;
        return old is null || old.Seq != video.Seq || old.File != video.File || old.Running != video.Running || Math.Abs(old.Rate - video.Rate) >= 0.001;
    }

    private async Task ContextAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(_o.ContextEvery, ct))
        {
            if (Status != LinkStatus.Live)
                continue;
            var reply = await _wire.SendAsync("CONTEXT", ct);
            if (reply.Ok && ShowContext.Parse(reply.Text) is not null)
            {
                lock (_gate)
                    _context = reply.Text;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_contextLoop is not null)
        {
            try
            {
                await _contextLoop;
            }
            catch (OperationCanceledException ex)
            {
                // The loop ends on the stop it was given.
                _log?.LogDebug(ex, "The Patterns link's context loop stopped.");
            }
        }
        await _wire.DisposeAsync();
        _stop.Dispose();
    }
}
