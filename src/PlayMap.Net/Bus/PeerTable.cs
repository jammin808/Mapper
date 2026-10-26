using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Net.Bus;

/// <summary>Another node heard on the hello: what it says it is — the hello last heard at <see cref="Address"/>, the address to dial —
/// when it was first and last heard (monotonic ns), how live it is now, every address it was heard from (<see cref="Addresses"/>) and the
/// conflict mark while two running nodes say its id (<see cref="Conflict"/>). Nothing about the show — a hello carries none.</summary>
public sealed record ShowBusPeer(ShowBusHello Hello, string Address, long FirstHeardNs, long HeardNs, PeerLiveness Liveness)
{
    public string Id => Hello.Id;

    public string Kind => Hello.Kind;

    public string Name => Hello.Name;

    /// <summary>Every address a hello of this id came from within §14's <c>peer.stale.ms</c>, newest first, at most
    /// <c>peer.addresses</c>; the address to dial alone when none is that fresh.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Two running nodes saying this id (Show Bus §2) while the mark stands, else null. No new link is dialled to an id in
    /// conflict (<see cref="PeerTable.Live"/>); a link it already has keeps running.</summary>
    public PeerConflict? Conflict { get; init; }

    public bool Has(string cap) => Hello.Caps.Contains(cap, StringComparer.Ordinal);
}

/// <summary>Two running nodes heard saying one id (Show Bus §2): the one heard before the alternation and the one heard at it, each by
/// its host and the address its hello came from, and by who sent it (<see cref="SenderA"/>, <see cref="SenderB"/>). A null
/// <see cref="HostA"/> and <see cref="SenderA"/> is this node itself, whose id another process says.</summary>
public sealed record PeerConflict(string Id, string? HostA, string AddressA, string HostB, string AddressB)
{
    /// <summary>Who sent the first side's hello: its <c>inst</c>, or "host:" and its host when it sends none; null for this node.</summary>
    public string? SenderA { get; init; }

    /// <summary>Who sent the second side's hello, as <see cref="SenderA"/>.</summary>
    public string SenderB { get; init; } = "";

    /// <summary>The mark as people read it: "two machines say they are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7".</summary>
    public string Words => $"two machines say they are {Id}: {Who(HostA, AddressA)} and {Who(HostB, AddressB)}";

    /// <summary>The same two senders, named in either order, whichever addresses each was heard at: a node heard at two addresses is
    /// one node (§2), so its second address never makes a mark of its own.</summary>
    public bool SameAs(PeerConflict? other) =>
        other is not null && other.Id == Id
        && ((SenderA, SenderB) == (other.SenderA, other.SenderB) || (SenderA, SenderB) == (other.SenderB, other.SenderA));

    private static string Who(string? host, string address) =>
        host is null ? "this node" : host.Length == 0 ? address : address.Length == 0 ? host : $"{host} at {address}";
}

public enum PeerChangeKind
{
    /// <summary>First heard (or heard again after it was forgotten).</summary>
    Added,
    /// <summary>Its hello says something new — a name, a host, a port, a capability, a certificate — or its conflict mark stood,
    /// named other machines or cleared.</summary>
    Changed,
    /// <summary>Live, stale or gone moved.</summary>
    Liveness,
    /// <summary>Silent past the forget time: off the table.</summary>
    Forgotten,
    /// <summary>The address to dial moved: the one dialled went unheard past §14's <c>peer.stale.ms</c> while another is heard. An
    /// address change alone is not a change of the peer (Show Bus §2).</summary>
    Moved,
}

public sealed record PeerChange(PeerChangeKind Kind, ShowBusPeer Peer);

/// <summary>Every node heard on the show network, keyed by id (stable per install, so a renamed node is the same node), by Show Bus 1.3
/// §2's hearing rules. Liveness is the silence since the last hello on the monotonic clock: live under 6 s, stale to 12 s, gone after,
/// forgotten at 60 s. At most <see cref="ShowBusConstants.PeersMax"/> ids: a hello from a new id past that is dropped. Per id, every
/// address heard within the stale time is kept (at most <see cref="ShowBusConstants.PeerAddresses"/>; a new one past that takes the
/// place of the one heard longest ago, never of the one dialled), and the address to dial stays while it is still heard, moving only once
/// it goes stale, so a node heard from two addresses never flips between them. One id said by two running nodes is marked in conflict:
/// an <c>inst</c> heard within the stale time comes back after another (A, B, A — and A, B, C, A), or, from nodes that send no
/// <c>inst</c>, a second host within that time; a hello of this node's own id from another process marks it at once. The mark is the
/// two senders, never their addresses, and keeps its words while both are heard; it clears after
/// <see cref="ShowBusConstants.ConflictClearMs"/> without another; a restart (A, then only B) is never one. <see cref="Hear"/> and
/// <see cref="Sweep"/> raise the changes, outside the table's lock, on the caller's thread.</summary>
public sealed class PeerTable(IMonoClock clock, string? selfId = null)
{
    /// <summary>The senders kept per id: one per running node, as many as the addresses kept, so N copies taking turns are seen.</summary>
    private const int KeptSenders = ShowBusConstants.PeerAddresses;

    private const string HostKey = "host:";
    private const long StaleNs = ShowBusConstants.StaleMs * Durations.NsPerMs;
    private const long ForgetNs = ShowBusConstants.ForgetMs * Durations.NsPerMs;
    private const long ClearNs = ShowBusConstants.ConflictClearMs * Durations.NsPerMs;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);

    public event Action<PeerChange>? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
                return _peers.Count;
        }
    }

    /// <summary>Takes in one hello from <paramref name="address"/>. False when it is from a new id while the table holds
    /// <see cref="ShowBusConstants.PeersMax"/> ids none of which is past the forget time: it is dropped.</summary>
    public bool Hear(ShowBusHello hello, string address)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(address);
        var now = clock.NowNs;
        var changes = new List<PeerChange>(1);
        lock (_gate)
        {
            var added = false;
            if (!_peers.TryGetValue(hello.Id, out var e))
            {
                if (_peers.Count >= ShowBusConstants.PeersMax)
                    Forget(now, changes);
                if (_peers.Count >= ShowBusConstants.PeersMax)
                {
                    Raise(changes);
                    return false;
                }
                e = new Entry(now);
                _peers[hello.Id] = e;
                added = true;
            }
            e.HeardNs = now;
            Address(e, hello, address, now);
            var who = hello.Instance ?? HostKey + hello.Host;
            e.Prune(now);
            if (hello.Id == selfId)
            {
                e.Record(who, now);
                e.Mark(new PeerConflict(hello.Id, null, "", hello.Host, address) { SenderB = who }, now);
            }
            else
                Alternation(e, hello, who, address, now);
            var next = e.View(now);
            if (added)
                changes.Add(new PeerChange(PeerChangeKind.Added, next));
            else if (Compare(e.Told!, next) is { } kind)
                changes.Add(new PeerChange(kind, next));
            e.Told = next;
        }
        Raise(changes);
        return true;
    }

    /// <summary>Ages every peer by the clock: liveness moves, a conflict mark past its clear time clears, the long-silent are forgotten.</summary>
    public void Sweep()
    {
        var now = clock.NowNs;
        var changes = new List<PeerChange>();
        lock (_gate)
        {
            Forget(now, changes);
            foreach (var e in _peers.Values)
            {
                var next = e.View(now);
                if (Compare(e.Told!, next) is { } kind)
                {
                    changes.Add(new PeerChange(kind, next));
                    e.Told = next;
                }
            }
        }
        Raise(changes);
    }

    /// <summary>Every peer not yet forgotten, with its liveness and conflict mark as of now, by kind then name.</summary>
    public IReadOnlyList<ShowBusPeer> Snapshot()
    {
        var now = clock.NowNs;
        lock (_gate)
        {
            return _peers.Values
                .Where(e => now - e.HeardNs < ForgetNs)
                .Select(e => e.View(now))
                .OrderBy(p => p.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public ShowBusPeer? Find(string id) => Snapshot().FirstOrDefault(p => p.Id == id);

    /// <summary>The live peers of a kind ("main" is Stagewatch, "patterns" a Patterns desk) that a new link may dial — none whose id is
    /// in conflict (§2) — first heard first.</summary>
    public IReadOnlyList<ShowBusPeer> Live(string kind) =>
        Snapshot().Where(p => p.Kind == kind && p.Liveness == PeerLiveness.Live && p.Conflict is null).OrderBy(p => p.FirstHeardNs).ToList();

    /// <summary>Under the gate: takes every id silent past the forget time off the table.</summary>
    private void Forget(long now, List<PeerChange> changes)
    {
        foreach (var (id, e) in _peers.Where(p => now - p.Value.HeardNs >= ForgetNs).ToList())
        {
            _peers.Remove(id);
            changes.Add(new PeerChange(PeerChangeKind.Forgotten, e.View(now) with { Liveness = PeerLiveness.Gone }));
        }
    }

    private void Raise(List<PeerChange> changes)
    {
        foreach (var c in changes)
            Changed?.Invoke(c);
    }

    /// <summary>What moved between the peer as last told and as it is now, the most telling first: what it says (or its conflict mark),
    /// then its liveness, then only the address to dial; null when nothing did.</summary>
    private static PeerChangeKind? Compare(ShowBusPeer told, ShowBusPeer now) =>
        !Same(told.Hello, now.Hello) || !SameMark(told.Conflict, now.Conflict) ? PeerChangeKind.Changed
        : told.Liveness != now.Liveness ? PeerChangeKind.Liveness
        : told.Address != now.Address ? PeerChangeKind.Moved
        : null;

    private static bool SameMark(PeerConflict? a, PeerConflict? b) => a is null ? b is null : a.SameAs(b);

    /// <summary>Every address heard within the stale time, at most the kept number, the one dialled kept while it is heard; the address
    /// to dial moves to this one only once the one dialled has gone stale.</summary>
    private static void Address(Entry e, ShowBusHello hello, string address, long now)
    {
        foreach (var stale in e.Addresses.Where(a => now - a.Value.Ns > StaleNs).Select(a => a.Key).ToList())
            e.Addresses.Remove(stale);
        e.Addresses[address] = (now, hello);
        while (e.Addresses.Count > ShowBusConstants.PeerAddresses)
        {
            var oldest = e.Addresses.Where(a => a.Key != e.Dial && a.Key != address)
                .OrderBy(a => a.Value.Ns).ThenBy(a => a.Key, StringComparer.Ordinal).First().Key;
            e.Addresses.Remove(oldest);
        }
        if (!e.Addresses.ContainsKey(e.Dial))
            e.Dial = address;
    }

    /// <summary>§2's two nodes with one id: a sender (<paramref name="who"/>) heard within the stale time comes back after another, however
    /// many take turns; or, with no inst, a second host within that time.</summary>
    private static void Alternation(Entry e, ShowBusHello hello, string who, string address, long now)
    {
        if (e.Who.Length > 0 && who != e.Who)
        {
            var back = e.Senders.ContainsKey(who);
            var twoHosts = who.StartsWith(HostKey, StringComparison.Ordinal) && e.Who.StartsWith(HostKey, StringComparison.Ordinal) && now - e.WhoNs <= StaleNs;
            if (back || twoHosts)
                e.Mark(new PeerConflict(hello.Id, e.WhoHost, e.WhoAddress, hello.Host, address) { SenderA = e.Who, SenderB = who }, now);
        }
        e.Record(who, now);
        e.Who = who;
        e.WhoNs = now;
        e.WhoHost = hello.Host;
        e.WhoAddress = address;
    }

    private static bool Same(ShowBusHello a, ShowBusHello b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.Host == b.Host && a.Proto == b.Proto && a.TlsFingerprint == b.TlsFingerprint
        && a.Caps.SequenceEqual(b.Caps, StringComparer.Ordinal)
        && a.Ports.Count == b.Ports.Count && a.Ports.All(p => b.Ports.TryGetValue(p.Key, out var v) && v == p.Value);

    /// <summary>One id's hearing, under the table's gate.</summary>
    private sealed class Entry(long firstNs)
    {
        // Each address heard within the stale time: when last, and the hello last heard there.
        public readonly Dictionary<string, (long Ns, ShowBusHello Hello)> Addresses = new(StringComparer.Ordinal);

        // Who sent each hello (its inst, or "host:" and its host when it sends none) heard within the stale time, and when last.
        public readonly Dictionary<string, long> Senders = new(StringComparer.Ordinal);

        public long HeardNs;
        public string Dial = "";
        public string Who = "";
        public long WhoNs;
        public string WhoHost = "";
        public string WhoAddress = "";
        public PeerConflict? Conflict;
        public long ConflictNs;

        /// <summary>The peer as the listeners were last told of it.</summary>
        public ShowBusPeer? Told;

        /// <summary>Forgets every sender not heard within the stale time.</summary>
        public void Prune(long now)
        {
            foreach (var stale in Senders.Where(s => now - s.Value > StaleNs).Select(s => s.Key).ToList())
                Senders.Remove(stale);
        }

        /// <summary>Keeps <paramref name="who"/> as heard now: one already kept, or a new one while fewer than the kept number are.</summary>
        public void Record(string who, long now)
        {
            if (Senders.ContainsKey(who) || Senders.Count < KeptSenders)
                Senders[who] = now;
        }

        /// <summary>Sets the mark, its clear time running from now. A standing mark whose two senders are both still heard keeps the words it
        /// first said — the same machines taking turns from another of their addresses, or a third copy among them, change nothing a reader
        /// is told — and takes the new words only once one of its own goes unheard.</summary>
        public void Mark(PeerConflict conflict, long now)
        {
            var standing = Conflict is { } mark && now - ConflictNs <= ClearNs
                && (mark.SenderA is null || Senders.ContainsKey(mark.SenderA)) && Senders.ContainsKey(mark.SenderB);
            if (!standing)
                Conflict = conflict;
            ConflictNs = now;
        }

        /// <summary>The peer as of <paramref name="now"/>: the hello last heard at the address to dial, every address heard within the
        /// stale time newest first, its liveness, and its conflict mark while it stands.</summary>
        public ShowBusPeer View(long now)
        {
            var heard = Addresses.Where(a => now - a.Value.Ns <= StaleNs)
                .OrderByDescending(a => a.Value.Ns).ThenBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key).ToList();
            if (heard.Count == 0)
                heard.Add(Dial);
            return new ShowBusPeer(Addresses[Dial].Hello, Dial, firstNs, HeardNs, Liveness.Of((now - HeardNs) / Durations.NsPerMs))
            {
                Addresses = heard,
                Conflict = Conflict is not null && now - ConflictNs <= ClearNs ? Conflict : null,
            };
        }
    }
}
