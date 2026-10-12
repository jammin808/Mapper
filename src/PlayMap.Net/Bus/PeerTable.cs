using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Net.Bus;

/// <summary>Another node heard on the hello: what it says it is, the address its datagram came from, when it was
/// first and last heard (monotonic ns) and how live it is now. Nothing about the show — a hello carries none.</summary>
public sealed record ShowBusPeer(ShowBusHello Hello, string Address, long FirstHeardNs, long HeardNs, PeerLiveness Liveness)
{
    public string Id => Hello.Id;

    public string Kind => Hello.Kind;

    public string Name => Hello.Name;

    public bool Has(string cap) => Hello.Caps.Contains(cap, StringComparer.Ordinal);
}

public enum PeerChangeKind
{
    /// <summary>First heard (or heard again after it was forgotten).</summary>
    Added,
    /// <summary>Its hello says something new: a name, a host, an address, a port, a capability, a certificate.</summary>
    Changed,
    /// <summary>Live, stale or gone moved.</summary>
    Liveness,
    /// <summary>Silent past the forget time: off the table.</summary>
    Forgotten,
}

public sealed record PeerChange(PeerChangeKind Kind, ShowBusPeer Peer);

/// <summary>Every node heard on the show network, keyed by id (stable per install, so a renamed node is the same
/// node). Liveness is the silence since the last hello on the monotonic clock: live under 6 s, stale to 12 s, gone
/// after, forgotten at 60 s (Show Bus §2/§4). <see cref="Hear"/> and <see cref="Sweep"/> raise the changes, outside
/// the table's lock, on the caller's thread.</summary>
public sealed class PeerTable(IMonoClock clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShowBusPeer> _peers = new(StringComparer.Ordinal);

    public event Action<PeerChange>? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
                return _peers.Count;
        }
    }

    /// <summary>Takes in one hello from <paramref name="address"/>.</summary>
    public void Hear(ShowBusHello hello, string address)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var now = clock.NowNs;
        PeerChange? change;
        lock (_gate)
        {
            if (_peers.TryGetValue(hello.Id, out var known))
            {
                var updated = known with { Hello = hello, Address = address, HeardNs = now, Liveness = PeerLiveness.Live };
                _peers[hello.Id] = updated;
                change = !Same(known.Hello, hello) || known.Address != address ? new PeerChange(PeerChangeKind.Changed, updated)
                    : known.Liveness != PeerLiveness.Live ? new PeerChange(PeerChangeKind.Liveness, updated)
                    : null;
            }
            else
            {
                var peer = new ShowBusPeer(hello, address, now, now, PeerLiveness.Live);
                _peers[hello.Id] = peer;
                change = new PeerChange(PeerChangeKind.Added, peer);
            }
        }
        if (change is not null)
            Changed?.Invoke(change);
    }

    /// <summary>Ages every peer by the clock: liveness moves, the long-silent are forgotten.</summary>
    public void Sweep()
    {
        var now = clock.NowNs;
        List<PeerChange>? changes = null;
        lock (_gate)
        {
            foreach (var peer in _peers.Values.ToList())
            {
                var silentMs = (now - peer.HeardNs) / Durations.NsPerMs;
                if (silentMs >= ShowBusConstants.ForgetMs)
                {
                    _peers.Remove(peer.Id);
                    (changes ??= []).Add(new PeerChange(PeerChangeKind.Forgotten, peer with { Liveness = PeerLiveness.Gone }));
                    continue;
                }
                var liveness = Liveness.Of(silentMs);
                if (liveness != peer.Liveness)
                {
                    var aged = peer with { Liveness = liveness };
                    _peers[peer.Id] = aged;
                    (changes ??= []).Add(new PeerChange(PeerChangeKind.Liveness, aged));
                }
            }
        }
        if (changes is null)
            return;
        foreach (var c in changes)
            Changed?.Invoke(c);
    }

    /// <summary>Every peer not yet forgotten, with its liveness as of now, by kind then name.</summary>
    public IReadOnlyList<ShowBusPeer> Snapshot()
    {
        var now = clock.NowNs;
        lock (_gate)
        {
            return _peers.Values
                .Where(p => (now - p.HeardNs) / Durations.NsPerMs < ShowBusConstants.ForgetMs)
                .Select(p => p with { Liveness = Liveness.Of((now - p.HeardNs) / Durations.NsPerMs) })
                .OrderBy(p => p.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public ShowBusPeer? Find(string id) => Snapshot().FirstOrDefault(p => p.Id == id);

    /// <summary>The live peers of a kind ("main" is Stagewatch, "patterns" a Patterns desk), first heard first.</summary>
    public IReadOnlyList<ShowBusPeer> Live(string kind) =>
        Snapshot().Where(p => p.Kind == kind && p.Liveness == PeerLiveness.Live).OrderBy(p => p.FirstHeardNs).ToList();

    private static bool Same(ShowBusHello a, ShowBusHello b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.Host == b.Host && a.Proto == b.Proto && a.TlsFingerprint == b.TlsFingerprint
        && a.Caps.SequenceEqual(b.Caps, StringComparer.Ordinal)
        && a.Ports.Count == b.Ports.Count && a.Ports.All(p => b.Ports.TryGetValue(p.Key, out var v) && v == p.Value);
}
