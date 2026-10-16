using System.Collections.Frozen;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Site;

/// <summary>One output's degrade gain moving from a level to another over a time on the leader's clock (a fade of zero length is a
/// step).</summary>
internal readonly record struct GainFade(float From, float To, long StartNs, long DurationNs)
{
    public float At(long nowNs)
    {
        if (DurationNs <= 0 || nowNs >= StartNs + DurationNs)
            return To;
        if (nowNs <= StartNs)
            return From;
        var t = (float)((nowNs - StartNs) / (double)DurationNs);
        return From + (To - From) * t;
    }
}

/// <summary>
/// The stacks' degrade gains (facade §3.9, failover MATCH): each output's gain as a fade on the leader's clock (show time, the clock
/// every fade of the air runs on) — down in 0.5 s when a member is lost, back up over 2 s when it returns. Written on the control lane
/// as a whole new table, only when a target changes; read by the render thread through <see cref="IOutputGains"/> lock-free and without allocating (a frozen table swapped by reference). Runtime state like
/// BLACKOUT: never in the project, never in undo.
/// </summary>
public sealed class StackGains(NetworkClock clock) : IOutputGains
{
    public const long DownNs = 500_000_000;
    public const long UpNs = 2_000_000_000;

    private volatile FrozenDictionary<string, GainFade> _fades = FrozenDictionary<string, GainFade>.Empty;

    public float GainOf(string outputId) => _fades.TryGetValue(outputId, out var f) ? f.At(clock.NowNs) : 1f;

    /// <summary>The gain each output is heading to (1 for one not named).</summary>
    public float TargetOf(string outputId) => _fades.TryGetValue(outputId, out var f) ? f.To : 1f;

    /// <summary>Sets the gains the outputs head to: each that changes fades from where it is now (down fast, up slow); an output
    /// back at 1 and settled leaves the table. On the lane.</summary>
    public void Set(IReadOnlyDictionary<string, float> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var was = _fades;
        // Nothing held and nothing asked: the table stands (the watch's usual tick allocates nothing).
        if (targets.Count == 0 && was.Count == 0)
            return;
        var now = clock.NowNs;
        var next = new Dictionary<string, GainFade>(StringComparer.Ordinal);
        foreach (var id in was.Keys.Concat(targets.Keys).Distinct(StringComparer.Ordinal))
        {
            var to = targets.TryGetValue(id, out var t) ? Math.Clamp(t, 0f, 1f) : 1f;
            var current = was.TryGetValue(id, out var f) ? f : new GainFade(1, 1, 0, 0);
            if (current.To == to)
            {
                if (to < 1 || current.At(now) < 1)
                    next[id] = current;
                continue;
            }
            var from = current.At(now);
            next[id] = new GainFade(from, to, now, to < from ? DownNs : UpNs);
        }
        if (next.Count == was.Count && next.All(kv => was.TryGetValue(kv.Key, out var f) && f == kv.Value))
            return;
        _fades = next.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>Two degrade gains multiplied: a part that adds its own (a stack's failover, a backup's fence) composes with what was set
/// before it, never overwriting it.</summary>
public sealed class ComposedGains(IOutputGains first, IOutputGains second) : IOutputGains
{
    public IOutputGains First => first;

    public IOutputGains Second => second;

    public float GainOf(string outputId) => first.GainOf(outputId) * second.GainOf(outputId);
}
