using System.Globalization;

namespace PlayMap.Core.Sync;

/// <summary>The leader's mapping from the board's frame count to show time: from count <see cref="EffectiveAt24"/> on, the vblank
/// of count c shows <c>LeaderNs + ((c − Count24) mod 2²⁴) × PeriodPs / 1000</c>. Published again every 60 s, taking effect two
/// seconds of refreshes later, so every node switches on the same vblank.</summary>
public readonly record struct LockAnchor(uint Count24, long LeaderNs, long PeriodPs, uint EffectiveAt24, long Seq);

/// <summary>Content time under the lock: the board's frame count (24 bits on the boards this reads, wrapping in 3 d 5 h at 60 Hz,
/// 1 d 14 h at 120 Hz) unwrapped into a 64-bit count, and the leader's anchors turned into the show time of any count — the same
/// integers on every node, so every node picks the same content frame for the same vblank.
///
/// <list type="bullet">
/// <item><b>Unwrap.</b> Each read adds <c>(c − c_prev) mod 2²⁴</c>. A delta over 2²³ cannot happen within one frame: it is a fault
/// (counted), not a wrap. A value ≥ 2²⁴ switches the counter to 32 bits for good.</item>
/// <item><b>Anchors.</b> A newer anchor (by <see cref="LockAnchor.Seq"/>) waits until its effective count, decided from the count
/// asked about, never from when this node read it — so a node that heard it late still agrees.</item>
/// <item><b>Stale.</b> More than 2²³ counts past the anchor (38.8 h at 60 Hz) with no newer one: no answer (the caller falls back
/// to clock time and says so).</item>
/// </list>
/// Pure; one thread.</summary>
public sealed class LockCounter
{
    public const uint Mask24 = 0xFFFFFF;
    public const uint Half24 = 1u << 23;

    /// <summary>The re-anchor lead: a new anchor takes effect this many refreshes after the count it was made at.</summary>
    public const uint DefaultLead = 120;

    private ulong _modulus = 1UL << 24;
    private bool _hasRead;
    private uint _last;
    private LockAnchor? _active;
    private LockAnchor? _pending;

    /// <summary>The unwrapped count (refreshes since the first read).</summary>
    public ulong Count64 { get; private set; }

    /// <summary>The last count read.</summary>
    public uint Last => _last;

    /// <summary>Reads that jumped more than half the counter's range (impossible within a frame).</summary>
    public long Faults { get; private set; }

    /// <summary>The board's counter proved wider than 24 bits; arithmetic is 32-bit from then on.</summary>
    public bool Wide => _modulus > 1UL << 24;

    public LockAnchor? Active => _active;

    public LockAnchor? Pending => _pending;

    /// <summary>Reads the board's count.</summary>
    public void Read(uint count)
    {
        if (!Wide && count > Mask24)
            _modulus = 1UL << 32;
        if (!_hasRead)
        {
            _hasRead = true;
            _last = count;
            return;
        }
        var delta = Delta(count, _last);
        if (delta > _modulus / 2)
        {
            Faults++;
            _last = count;
            return;
        }
        Count64 += delta;
        _last = count;
        Promote(count);
    }

    /// <summary>Offers an anchor; a newer one than the active and pending ones waits for its effective count, an older one is dropped.</summary>
    public void Offer(LockAnchor anchor)
    {
        if (_active is { } a && anchor.Seq <= a.Seq)
            return;
        if (_pending is { } p && anchor.Seq <= p.Seq)
            return;
        if (_active is null)
        {
            _active = anchor;
            return;
        }
        // A pending anchor whose count has passed takes effect before a newer one waits behind it.
        if (_hasRead)
            Promote(_last);
        _pending = anchor;
    }

    /// <summary>The show time (leader ns) of the vblank of <paramref name="count"/>; false with no anchor, or a stale one.</summary>
    public bool TryLeaderNsAt(uint count, out long leaderNs)
    {
        leaderNs = 0;
        // The anchor in effect at that count — decided by the count alone, so every node answers alike.
        var chosen = _pending is { } p && Delta(count, p.EffectiveAt24) <= _modulus / 2 ? p : _active;
        if (chosen is not { } a)
            return false;
        var d = Delta(count, a.Count24);
        if (d > _modulus / 2)
            return false;
        leaderNs = a.LeaderNs + (long)(d * (ulong)a.PeriodPs / 1000);
        return true;
    }

    /// <summary>Whether a new anchor is due: the active one was made 60 s of refreshes before <paramref name="count"/> (or none yet).</summary>
    public bool DueForReanchor(uint count, double refreshHz, double everySeconds = 60)
    {
        if (_active is not { } a)
            return true;
        if (_pending is not null)
            return false;
        return Delta(count, a.Count24) >= (ulong)Math.Max(1, Math.Round(refreshHz * everySeconds));
    }

    /// <summary>An anchor made on the leader at the vblank of <paramref name="count"/> (its fitted show time
    /// <paramref name="vblankLeaderNs"/>, its period in ps), taking effect <paramref name="lead"/> refreshes later.</summary>
    public static LockAnchor Make(uint count, long vblankLeaderNs, long periodPs, long seq, uint lead = DefaultLead) =>
        new(count, vblankLeaderNs, periodPs, (count + lead) & Mask24, seq);

    /// <summary>The anchor's record for the group's lock feed (stable keys).</summary>
    public static string Json(LockAnchor a) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"count24\":{a.Count24},\"leaderNs\":{a.LeaderNs},\"periodPs\":{a.PeriodPs},\"effectiveAt24\":{a.EffectiveAt24},\"seq\":{a.Seq}}}");

    private void Promote(uint count)
    {
        if (_pending is { } p && Delta(count, p.EffectiveAt24) <= _modulus / 2)
        {
            _active = p;
            _pending = null;
        }
    }

    /// <summary>(a − b) mod the counter's range.</summary>
    private ulong Delta(uint a, uint b) => ((ulong)a - b) & (_modulus - 1);
}

/// <summary>Sync boards by the id their driver reports.</summary>
public static class SyncBoards
{
    /// <summary>The board's name: 856 Quadro G-Sync (P358), 8288 Quadro Sync (P2060), 8289 Quadro Sync II (P2061); any other id
    /// "sync board 0x…" (its capabilities unknown).</summary>
    public static string Name(int boardId) => boardId switch
    {
        856 => "Quadro G-Sync (P358)",
        8288 => "Quadro Sync (P2060)",
        8289 => "Quadro Sync II (P2061)",
        _ => string.Create(CultureInfo.InvariantCulture, $"sync board 0x{boardId:x}"),
    };

    /// <summary>A board whose capabilities are known (one of the three named).</summary>
    public static bool Known(int boardId) => boardId is 856 or 8288 or 8289;
}
