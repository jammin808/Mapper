using System.Collections.Frozen;
using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Show;

/// <summary>Show Mode as the node holds it: armed or rehearsal, by whom, since when and why. Runtime, not the document (the rules
/// travel in the project; who armed it does not), kept across a restart by the recovery record.</summary>
public sealed record ShowMode(bool Armed, string By, long SinceUnixMs, string Words)
{
    public static readonly ShowMode Rehearsal = new(false, "", 0, "");
}

/// <summary>The gate's refusals of the last minute, for STATE.show.refused: how many and the last one in words (its line
/// redacted). Written from the lane and from off-lane verbs, so it locks.</summary>
public sealed class ShowRefusals
{
    private const long WindowMs = 60_000;
    private readonly Lock _gate = new();
    private readonly Queue<long> _at = new();
    private string _last = "";
    private long? _lastEventMs;

    /// <summary>Notes a refusal; true when an event may be raised for it (at most one a second).</summary>
    public bool Note(long nowUnixMs, string words)
    {
        lock (_gate)
        {
            _at.Enqueue(nowUnixMs);
            Trim(nowUnixMs);
            _last = words;
            if (_lastEventMs is { } last && nowUnixMs - last < 1000)
                return false;
            _lastEventMs = nowUnixMs;
            return true;
        }
    }

    public (int Last60s, string Last) Read(long nowUnixMs)
    {
        lock (_gate)
        {
            Trim(nowUnixMs);
            return (_at.Count, _last);
        }
    }

    /// <summary>Lets refusals older than the window go; true when any did (the count STATE shows has moved).</summary>
    public bool Expire(long nowUnixMs)
    {
        lock (_gate)
            return Trim(nowUnixMs);
    }

    private bool Trim(long nowUnixMs)
    {
        var any = false;
        while (_at.Count > 0 && nowUnixMs - _at.Peek() > WindowMs)
        {
            _at.Dequeue();
            any = true;
        }
        return any;
    }
}

/// <summary>The air's levels for the render thread, built on the lane once per change of the master, a panic or a return from
/// one, and read every frame without allocating: each output's gain is master × its panic level (or its return fade) × its
/// degrade gain, every value a pure function of the frame's leader instant, so every node fades on the same frames.</summary>
public sealed class AirLevels : IAirLevels
{
    private readonly AirFade _master;
    private readonly FrozenDictionary<string, OutputPanicAir> _panic;
    private readonly FrozenDictionary<string, AirReturn> _returns;
    private readonly OutputPanicAir[] _panicAll;
    private readonly AirReturn[] _returnsAll;
    private readonly IOutputGains? _gains;

    public AirLevels(AirFade master, PanicAir? panic, ImmutableArray<AirReturn> returns, IOutputGains? gains)
    {
        _master = master;
        _panicAll = panic is null ? [] : [.. panic.Outputs];
        _returnsAll = returns.IsDefaultOrEmpty ? [] : [.. returns];
        _panic = _panicAll.ToFrozenDictionary(o => o.OutputId, StringComparer.Ordinal);
        _returns = _returnsAll.ToFrozenDictionary(r => r.OutputId, StringComparer.Ordinal);
        _gains = gains;
    }

    public float Gain(string outputId, long leaderNs)
    {
        var g = _master.At(leaderNs);
        if (_panic.TryGetValue(outputId, out var p))
            g *= PanicMath.Level(p, leaderNs);
        else if (_returns.TryGetValue(outputId, out var r))
            g *= r.At(leaderNs);
        if (_gains is not null)
            g *= _gains.GainOf(outputId);
        return Math.Clamp(g, 0f, 1f);
    }

    public SourceRef? Override(string outputId, long leaderNs)
    {
        if (_panic.TryGetValue(outputId, out var p))
            return PanicMath.Override(p, leaderNs);
        return _returns.TryGetValue(outputId, out var r) ? r.Override(leaderNs) : null;
    }

    public bool Hold(string outputId, long leaderNs) =>
        _panic.TryGetValue(outputId, out var p) && p.Outcome == PanicOutcome.Freeze && leaderNs >= p.Level.StartLeaderNs;

    /// <summary>The air's sound: master × the darkest panic (a SAFE look has no show sound; a picture-only outcome keeps it).</summary>
    public float AudioGain(long leaderNs)
    {
        var level = 1f;
        foreach (var p in _panicAll)
        {
            if (p.Outcome == PanicOutcome.Safe)
                level = Math.Min(level, p.Level.At(leaderNs));
            else if (PanicMath.Darkens(p.Outcome))
                level = Math.Min(level, PanicMath.Level(p, leaderNs));
        }
        foreach (var r in _returnsAll)
            level = Math.Min(level, r.Safe is not null && leaderNs < r.SafeUntilLeaderNs ? 0f : r.At(leaderNs));
        return Math.Clamp(_master.At(leaderNs) * level, 0f, 1f);
    }
}

/// <summary>Show Mode for every caller that holds the engine: the router's own table, read from published state.</summary>
public sealed class EngineShowGate(EngineCore engine) : IShowGate
{
    public bool Armed => engine.ShowNow.Armed;

    public string ArmedBy => engine.ShowNow.By;

    public string? BlindOwner => engine.BlindOwnerName;

    public int MinGoMs => engine.MinGoMsNow;

    public ShowRole RoleOf(SessionInfo session) => engine.ShowRoleOf(session);

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => engine.DecideShow(spec, line, session);

    /// <summary>Every row this session may run as it stands now, or null in rehearsal (every row).</summary>
    public IReadOnlyCollection<string>? Allowed(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Armed)
            return null;
        return [.. VerbCatalog.All.Where(v => !v.Internal && engine.IsAvailable(v)
            && Decide(v, new WireLine(null, v.Name.Split(' '), v.Name), session).Verdict == ShowVerdict.Allow).Select(v => v.Name)];
    }
}

/// <summary>The show's gate the services carry before the engine exists: open until the engine built from them binds its own,
/// then that engine's (the parts that hold only the services read Show Mode through it).</summary>
public sealed class LateShowGate : IShowGate
{
    private IShowGate _inner = OpenShowGate.Instance;

    /// <summary>Binds the engine's gate; false when another engine's is bound already (services copied between engines).</summary>
    public bool Bind(IShowGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return ReferenceEquals(Interlocked.CompareExchange(ref _inner, gate, OpenShowGate.Instance), OpenShowGate.Instance);
    }

    private IShowGate Inner => Volatile.Read(ref _inner);

    public bool Armed => Inner.Armed;

    public string ArmedBy => Inner.ArmedBy;

    public string? BlindOwner => Inner.BlindOwner;

    public int MinGoMs => Inner.MinGoMs;

    public ShowRole RoleOf(SessionInfo session) => Inner.RoleOf(session);

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => Inner.Decide(spec, line, session);

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => Inner.Allowed(session);
}
