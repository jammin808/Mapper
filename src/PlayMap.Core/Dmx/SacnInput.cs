using PlayMap.Core.Control;

namespace PlayMap.Core.Dmx;

/// <summary>What one received sACN packet was.</summary>
public enum SacnInputResult { Ignored, Malformed, Dmx, OutOfOrder, Held, Sync, Preview, Terminated }

/// <summary>A source heard on a universe, for STATE: its CID, name, address (IPv4 as on the wire), priority and when it was last heard.</summary>
public readonly record struct SacnSourceView(Guid Cid, string Name, uint Address, int Priority, long LastNs);

/// <summary>sACN (ANSI E1.31) control input as a pure state machine, merging every source of a universe the way the standard asks:
/// the highest priority wins; sources of equal priority merge highest-takes-precedence per channel (a 16-bit pair as one value).
/// A source's sequence is checked (§6.7.2: a packet whose sequence is 0 to 19 behind the last is late and dropped); a source not
/// heard for 2.5 s, or that says its stream terminated, is gone (§6.7.1) and the merge falls to the sources left (a backup console
/// takes over); when none is left the channels hold. Preview data is ignored unless it is accepted. A data packet with a
/// synchronization address waits for the synchronization packet of that address once one has been seen, and applies at once again
/// 2.5 s after the last (§6.2.4). Only the channels bindings read become <see cref="InputEvent"/>s (8-bit /255, 16-bit /65535),
/// each named with the surface of the source that sets it; while learning, every channel of a heard universe that changes does.
/// Not thread-safe: one reader.</summary>
public sealed class SacnDmxInput
{
    public const long LossNs = 2_500_000_000;

    public const long SyncLossNs = 2_500_000_000;

    private readonly Dictionary<int, Universe> _universes = [];
    private readonly Dictionary<int, long> _syncs = [];
    private HashSet<DmxChannel> _wanted = [];
    private readonly HashSet<DmxChannel> _fresh = [];
    private HashSet<int> _wantedUniverses = [];

    /// <summary>Every changed channel of every heard universe becomes an event (learn); otherwise only the wanted ones.</summary>
    public bool LearnAll { get; set; }

    /// <summary>Preview data (options bit 7) is merged like live data.</summary>
    public bool AcceptPreview { get; set; }

    /// <summary>The surface a source is (its IPv4 address as on the wire, its CID); "" = none.</summary>
    public Func<uint, Guid, string>? SurfaceOf { get; set; }

    public long Frames { get; private set; }

    public long Dropped { get; private set; }

    public long Malformed { get; private set; }

    public long Previews { get; private set; }

    /// <summary>The universes bindings read (the receiver joins their multicast groups).</summary>
    public IReadOnlyCollection<int> Universes => _wantedUniverses;

    /// <summary>The sources heard on each universe now.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SacnSourceView>> Sources =>
        _universes.Where(kv => kv.Value.Sources.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SacnSourceView>)kv.Value.Sources
            .Select(s => new SacnSourceView(s.Key, s.Value.Name, s.Value.Address, s.Value.Priority, s.Value.LastNs)).OrderBy(s => s.Cid).ToList());

    /// <summary>The channels bindings read. A channel newly wanted is sent with the next frame of its universe even when it did not
    /// change.</summary>
    public void Want(IEnumerable<DmxChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        HashSet<DmxChannel> next = [.. channels];
        foreach (var c in next)
            if (!_wanted.Contains(c))
                _fresh.Add(c);
        _fresh.IntersectWith(next);
        _wanted = next;
        _wantedUniverses = [.. next.Select(c => c.Universe)];
        foreach (var u in _universes.Values)
            foreach (var gone in u.Emitted.Keys.Where(k => !next.Contains(k)).ToList())
                u.Emitted.Remove(gone);
    }

    /// <summary>One packet from a source address (IPv4 as on the wire): events for the merged channels that changed.</summary>
    public SacnInputResult Receive(ReadOnlySpan<byte> packet, uint from, long nowNs, List<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        switch (Sacn.KindOf(packet))
        {
            case SacnKind.None or SacnKind.Discovery:
                return SacnInputResult.Ignored;
            case SacnKind.Sync:
                if (!Sacn.TryReadSync(packet, out var sync))
                {
                    Malformed++;
                    return SacnInputResult.Malformed;
                }
                _syncs[sync.SyncAddress] = nowNs;
                foreach (var (number, u) in _universes)
                {
                    var applied = false;
                    foreach (var s in u.Sources.Values.Where(s => s.Pending is not null && s.PendingSync == sync.SyncAddress))
                    {
                        s.Values = s.Pending!;
                        s.HasData = true;
                        s.Pending = null;
                        applied = true;
                    }
                    if (applied)
                        Merge(number, u, nowNs, events);
                }
                return SacnInputResult.Sync;
        }
        if (!Sacn.TryReadData(packet, out var h, out var slots))
        {
            Malformed++;
            return SacnInputResult.Malformed;
        }
        // Other START codes (alternate data, per-channel priority) are not levels.
        if (h.StartCode != 0)
            return SacnInputResult.Ignored;
        if (!LearnAll && !_wantedUniverses.Contains(h.Universe))
            return SacnInputResult.Ignored;
        if (h.Preview && !AcceptPreview)
        {
            Previews++;
            return SacnInputResult.Preview;
        }
        if (!_universes.TryGetValue(h.Universe, out var universe))
        {
            universe = new Universe();
            _universes[h.Universe] = universe;
        }
        if (h.Terminated)
        {
            if (universe.Sources.Remove(h.Cid))
                Merge(h.Universe, universe, nowNs, events);
            return SacnInputResult.Terminated;
        }
        if (!universe.Sources.TryGetValue(h.Cid, out var source))
        {
            source = new Source();
            universe.Sources[h.Cid] = source;
        }
        else if (source.HasSequence)
        {
            var step = (sbyte)(byte)(h.Sequence - source.Sequence);
            if (step is <= 0 and > -20)
            {
                Dropped++;
                return SacnInputResult.OutOfOrder;
            }
        }
        source.Sequence = h.Sequence;
        source.HasSequence = true;
        source.Name = h.SourceName;
        source.Address = from;
        source.Priority = h.Priority;
        source.LastNs = nowNs;
        Frames++;
        var frame = new byte[512];
        slots.CopyTo(frame);
        if (h.SyncAddress != 0 && _syncs.TryGetValue(h.SyncAddress, out var at) && nowNs - at < SyncLossNs)
        {
            source.Pending = frame;
            source.PendingSync = h.SyncAddress;
            return SacnInputResult.Held;
        }
        source.Values = frame;
        source.HasData = true;
        Merge(h.Universe, universe, nowNs, events);
        return SacnInputResult.Dmx;
    }

    /// <summary>Sources not heard for 2.5 s are gone (the merge falls to those left); held frames apply 2.5 s after the last
    /// synchronization packet of their address.</summary>
    public void Tick(long nowNs, List<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var lost in _syncs.Where(kv => nowNs - kv.Value >= SyncLossNs).Select(kv => kv.Key).ToList())
            _syncs.Remove(lost);
        foreach (var (number, u) in _universes)
        {
            var changed = false;
            foreach (var cid in u.Sources.Where(kv => nowNs - kv.Value.LastNs >= LossNs).Select(kv => kv.Key).ToList())
            {
                u.Sources.Remove(cid);
                changed = true;
            }
            foreach (var s in u.Sources.Values.Where(s => s.Pending is not null && !_syncs.ContainsKey(s.PendingSync)))
            {
                s.Values = s.Pending!;
                s.HasData = true;
                s.Pending = null;
                changed = true;
            }
            if (changed)
                Merge(number, u, nowNs, events);
        }
    }

    /// <summary>The merged value of a channel (with its fine partner, 0 = 8-bit) and the source that sets it, or null when no source
    /// of the universe has data.</summary>
    private static (int Raw, Guid Cid, Source Source)? MergedOf(Universe u, int channel, int fine)
    {
        var top = -1;
        foreach (var s in u.Sources.Values)
            if (s.HasData && s.Priority > top)
                top = s.Priority;
        if (top < 0)
            return null;
        (int Raw, Guid Cid, Source Source)? best = null;
        foreach (var (cid, s) in u.Sources)
        {
            if (!s.HasData || s.Priority != top)
                continue;
            var raw = fine > 0 ? (s.Values[channel - 1] << 8) | s.Values[fine - 1] : s.Values[channel - 1];
            if (best is not { } b || raw > b.Raw || (raw == b.Raw && cid.CompareTo(b.Cid) < 0))
                best = (raw, cid, s);
        }
        return best;
    }

    private void Merge(int number, Universe u, long nowNs, List<InputEvent> events)
    {
        if (LearnAll)
        {
            for (var ch = 1; ch <= 512; ch++)
            {
                if (MergedOf(u, ch, 0) is not { } m || u.Learnt[ch - 1] == m.Raw)
                    continue;
                u.Learnt[ch - 1] = m.Raw;
                events.Add(Event(number, ch, 0, m, nowNs));
            }
            return;
        }
        foreach (var w in _wanted)
        {
            if (w.Universe != number || MergedOf(u, w.Channel, w.Fine) is not { } m)
                continue;
            var fresh = _fresh.Remove(w);
            if (!fresh && u.Emitted.TryGetValue(w, out var was) && was == m.Raw)
                continue;
            u.Emitted[w] = m.Raw;
            events.Add(Event(number, w.Channel, w.Fine, m, nowNs));
        }
    }

    private InputEvent Event(int universe, int channel, int fine, (int Raw, Guid Cid, Source Source) m, long nowNs)
    {
        var surface = SurfaceOf?.Invoke(m.Source.Address, m.Cid) ?? "";
        var a = m.Source.Address;
        return new InputEvent(InputAddress.Dmx(InputSource.Sacn, universe, channel, fine, surface), InputKind.Axis, m.Raw / (fine > 0 ? 65535.0 : 255.0),
            m.Raw, (byte)(fine > 0 ? 16 : 8), nowNs) { Origin = $"{a >> 24}.{(a >> 16) & 0xFF}.{(a >> 8) & 0xFF}.{a & 0xFF}" };
    }

    private sealed class Universe
    {
        public Dictionary<Guid, Source> Sources { get; } = [];
        public Dictionary<DmxChannel, int> Emitted { get; } = [];
        public int[] Learnt { get; } = Enumerable.Repeat(-1, 512).ToArray();
    }

    private sealed class Source
    {
        public string Name { get; set; } = "";
        public uint Address { get; set; }
        public int Priority { get; set; }
        public long LastNs { get; set; }
        public byte Sequence { get; set; }
        public bool HasSequence { get; set; }
        public byte[] Values { get; set; } = new byte[512];
        public bool HasData { get; set; }
        public byte[]? Pending { get; set; }
        public int PendingSync { get; set; }
    }
}
