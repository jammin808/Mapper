using PlayMap.Core.Control;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Dmx;

/// <summary>A DMX channel a binding reads: its universe, channel and fine partner (0 = 8-bit).</summary>
public readonly record struct DmxChannel(int Universe, int Channel, int Fine);

/// <summary>What one received packet was.</summary>
public enum ArtNetInputResult { Ignored, Malformed, Dmx, OutOfOrder, Held, Sync, Poll, TimeCode }

/// <summary>A poll reply due: to whom (an IPv4 address as written on the wire) and when (mono ns).</summary>
public readonly record struct PollDue(uint To, long DueNs);

/// <summary>Art-Net 4 control input as a pure state machine. ArtDmx: the sequence check per universe and source (0 disables it;
/// a packet whose (sequence − last) mod 256 is 128–255 is late and dropped), LTP between sources (the newest packet wins);
/// once an ArtSync is seen, frames are held and applied together at the next ArtSync, and 4 s without one returns to applying
/// at once. ArtPoll: a unicast ArtPollReply is due after a random delay of up to 1 s (from a seeded <see cref="Pcg32"/>).
/// ArtTimeCode is handed out as is. The bound channels that changed become <see cref="InputEvent"/>s (8-bit /255, 16-bit
/// coarse+fine /65535); while learning, every changed channel does. Not thread-safe: one reader.</summary>
public sealed class ArtNetDmxInput
{
    public const long SyncTimeoutNs = 4_000_000_000;

    public const long MaxPollDelayNs = 1_000_000_000;

    private readonly Dictionary<int, Universe> _universes = [];
    private readonly List<PollDue> _polls = [];
    private Pcg32 _random;
    private HashSet<DmxChannel> _wanted = [];
    private readonly HashSet<DmxChannel> _fresh = [];
    private long _lastSyncNs = long.MinValue;

    public ArtNetDmxInput(ulong seed)
    {
        _random = new Pcg32(seed);
    }

    /// <summary>Every changed channel of every universe becomes an event (learn); otherwise only the wanted ones.</summary>
    public bool LearnAll { get; set; }

    public long Frames { get; private set; }

    public long Dropped { get; private set; }

    public long Malformed { get; private set; }

    public long Polls { get; private set; }

    /// <summary>The universes heard, with their sources (IPv4 as on the wire) and the last time each was heard.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<uint, long>> Sources =>
        _universes.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<uint, long>)kv.Value.Sources.ToDictionary(s => s.Key, s => s.Value.LastNs));

    /// <summary>Whether frames wait for ArtSync now.</summary>
    public bool Synced(long nowNs) => _lastSyncNs != long.MinValue && nowNs - _lastSyncNs < SyncTimeoutNs;

    /// <summary>The channels bindings read. A channel newly wanted is sent with the next frame of its universe even when it did not
    /// change (a binding made after the console's last move takes the console's level at its next refresh).</summary>
    public void Want(IEnumerable<DmxChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        HashSet<DmxChannel> next = [.. channels];
        foreach (var c in next)
            if (!_wanted.Contains(c))
                _fresh.Add(c);
        _fresh.IntersectWith(next);
        _wanted = next;
    }

    /// <summary>One packet from a source: events for the channels that changed go into <paramref name="events"/>; a poll schedules
    /// its reply; a timecode packet comes back in <paramref name="tc"/>.</summary>
    public ArtNetInputResult Receive(ReadOnlySpan<byte> packet, uint from, long nowNs, List<InputEvent> events, out ArtTimeCode tc)
    {
        ArgumentNullException.ThrowIfNull(events);
        tc = default;
        var op = ArtNet.OpOf(packet);
        switch (op)
        {
            case null:
                return ArtNetInputResult.Ignored;
            case ArtOp.Dmx:
                if (!ArtNet.TryReadDmx(packet, out var header, out var data))
                {
                    Malformed++;
                    return ArtNetInputResult.Malformed;
                }
                return Dmx(header, data, from, nowNs, events);
            case ArtOp.Sync:
                if (!ArtNet.IsSync(packet))
                {
                    Malformed++;
                    return ArtNetInputResult.Malformed;
                }
                _lastSyncNs = nowNs;
                foreach (var (number, u) in _universes)
                {
                    if (u.Pending is null)
                        continue;
                    Apply(number, u, u.Pending, nowNs, events);
                    u.Pending = null;
                }
                return ArtNetInputResult.Sync;
            case ArtOp.Poll:
                if (!ArtNet.IsPoll(packet))
                {
                    Malformed++;
                    return ArtNetInputResult.Malformed;
                }
                Polls++;
                _polls.Add(new PollDue(from, nowNs + (long)(_random.NextDouble() * MaxPollDelayNs)));
                return ArtNetInputResult.Poll;
            case ArtOp.TimeCode:
                if (!ArtNet.TryReadTimeCode(packet, out tc))
                {
                    Malformed++;
                    return ArtNetInputResult.Malformed;
                }
                return ArtNetInputResult.TimeCode;
            default:
                return ArtNetInputResult.Ignored;
        }
    }

    /// <summary>The poll replies due at this instant (each taken once).</summary>
    public IReadOnlyList<PollDue> TakeDuePolls(long nowNs)
    {
        if (_polls.Count == 0)
            return [];
        var due = _polls.Where(p => p.DueNs <= nowNs).ToList();
        _polls.RemoveAll(p => p.DueNs <= nowNs);
        return due;
    }

    /// <summary>After 4 s without ArtSync, held frames apply at once.</summary>
    public void Tick(long nowNs, List<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (_lastSyncNs == long.MinValue || nowNs - _lastSyncNs < SyncTimeoutNs)
            return;
        _lastSyncNs = long.MinValue;
        foreach (var (number, u) in _universes)
        {
            if (u.Pending is null)
                continue;
            Apply(number, u, u.Pending, nowNs, events);
            u.Pending = null;
        }
    }

    private ArtNetInputResult Dmx(ArtDmxHeader h, ReadOnlySpan<byte> data, uint from, long nowNs, List<InputEvent> events)
    {
        if (!_universes.TryGetValue(h.PortAddress, out var u))
            _universes[h.PortAddress] = u = new Universe();
        if (!u.Sources.TryGetValue(from, out var source))
            u.Sources[from] = source = new Source();
        if (h.Sequence != 0 && source.Sequence != 0)
        {
            var step = (h.Sequence - source.Sequence) & 0xFF;
            if (step >= 128)
            {
                Dropped++;
                return ArtNetInputResult.OutOfOrder;
            }
        }
        source.Sequence = h.Sequence;
        source.LastNs = nowNs;
        Frames++;
        var frame = new byte[512];
        data.CopyTo(frame);
        if (Synced(nowNs))
        {
            u.Pending = frame;
            return ArtNetInputResult.Held;
        }
        Apply(h.PortAddress, u, frame, nowNs, events);
        return ArtNetInputResult.Dmx;
    }

    private void Apply(int number, Universe u, byte[] frame, long nowNs, List<InputEvent> events)
    {
        var first = u.Values is null;
        var before = u.Values ?? new byte[512];
        u.Values = frame;
        if (LearnAll)
        {
            for (var ch = 1; ch <= 512; ch++)
                if (first || before[ch - 1] != frame[ch - 1])
                    events.Add(new InputEvent(InputAddress.Dmx(InputSource.ArtNet, number, ch), InputKind.Axis, frame[ch - 1] / 255.0, frame[ch - 1], 8, nowNs));
            return;
        }
        foreach (var w in _wanted)
        {
            if (w.Universe != number)
                continue;
            var c = w.Channel - 1;
            var f = w.Fine - 1;
            var fresh = _fresh.Remove(w);
            var changed = first || fresh || before[c] != frame[c] || (w.Fine > 0 && before[f] != frame[f]);
            if (!changed)
                continue;
            var raw = w.Fine > 0 ? (frame[c] << 8) | frame[f] : frame[c];
            var bits = (byte)(w.Fine > 0 ? 16 : 8);
            events.Add(new InputEvent(InputAddress.Dmx(InputSource.ArtNet, number, w.Channel, w.Fine), InputKind.Axis,
                raw / (w.Fine > 0 ? 65535.0 : 255.0), raw, bits, nowNs));
        }
    }

    private sealed class Universe
    {
        public Dictionary<uint, Source> Sources { get; } = [];
        public byte[]? Values { get; set; }
        public byte[]? Pending { get; set; }
    }

    private sealed class Source
    {
        public byte Sequence { get; set; }
        public long LastNs { get; set; }
    }
}
