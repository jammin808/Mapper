using System.Collections.Frozen;
using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Fleet;

/// <summary>A node's place in a hot-backup pair, as its settings keep it.</summary>
public static class FleetRoles
{
    public const string Solo = "solo";
    public const string Primary = "primary";
    public const string Backup = "backup";
    public const string Fenced = "fenced";

    public static bool IsKnown(string role) => role is Solo or Primary or Backup or Fenced;

    /// <summary>What a node in <paramref name="role"/> may put on the network beside its own outputs.</summary>
    public static NetworkAir AirOf(string role) => role switch
    {
        Backup => NetworkAir.Silent,
        Fenced => NetworkAir.Held,
        _ => NetworkAir.Live,
    };
}

/// <summary>What a node may put on the network beside its own outputs — its LED rig's data and its video sends — as its place in a
/// hot-backup pair decides. The rig's nodes and universes are shared by every machine that drives them, and a send is received by
/// name, so only the node that has the show sends; its own outputs (windows routed by the projectors' inputs) are the fleet's air
/// gain's to decide.</summary>
public enum NetworkAir
{
    /// <summary>The node has the show (solo or primary): its LED data and sends go out.</summary>
    Live,

    /// <summary>A hot backup: nothing goes out (its primary's picture is on the rig and the network; the backup's would fight it), and
    /// nothing goes black as it begins (the primary's frames are the rig's). FAILOVER makes it <see cref="Live"/>.</summary>
    Silent,

    /// <summary>Fenced: held black — what it sent goes black once, as a stopped LED output or send does, then nothing goes out (the
    /// new primary drives the rig and the network).</summary>
    Held,
}

/// <summary>The takeover's rules, pure: the epoch a takeover commits, and when a node that hears another must stand aside.</summary>
public static class Takeover
{
    /// <summary>The epoch a takeover (or a promotion) commits: one past every epoch this node holds or has heard, so the node
    /// that took the show last always carries the highest.</summary>
    public static long NextEpoch(long own, IEnumerable<long> heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var max = Math.Max(0, own);
        foreach (var e in heard)
            max = Math.Max(max, e);
        return max + 1;
    }

    /// <summary>True when a primary hears a peer that took the show from
    /// it under a higher epoch: the peer is a primary, names this node as the one it took over from, and its epoch is above this
    /// node's. Two nodes can then never both serve one show for longer than it takes the higher epoch to be heard.</summary>
    /// <param name="peerKnown">The peer joined this node's mirror as a backup (proved by its token) and is heard over its pinned
    /// certificate: a word in HEALTH from any other machine fences nothing.</param>
    public static bool FencedBy(string selfId, string selfRole, long selfEpoch, string peerRole, string peerOf, long peerEpoch, bool peerKnown) =>
        peerKnown
        && selfRole == FleetRoles.Primary
        && peerRole == FleetRoles.Primary
        && string.Equals(peerOf, selfId, StringComparison.Ordinal)
        && peerEpoch > selfEpoch;

    /// <summary>True when a follower whose leader went quiet should take its clock from a peer instead: the peer leads the same
    /// group under a higher epoch than the leader this node followed (a promotion or a takeover moved the group's lead to it).</summary>
    public static bool Retargets(string group, string selfLeader, long leaderEpoch, string peerId, string peerSyncRole, string peerGroup, long peerEpoch) =>
        group.Length > 0
        && peerSyncRole == "leader"
        && string.Equals(peerGroup, group, StringComparison.Ordinal)
        && !string.Equals(peerId, selfLeader, StringComparison.Ordinal)
        && peerEpoch > leaderEpoch;
}

/// <summary>What a backup or a fenced node lets a client do, pure: a mirror's show is its primary's, so a line that would change it
/// here is refused with the way out; queries, panic, Show Mode, the node's own administration and the fleet's own verbs stay.</summary>
public static class BackupGate
{
    /// <summary>The transports whose lines the fleet's own parts run (the mirror, the sync feed, the node itself): never refused here.</summary>
    private static readonly FrozenSet<string> Inside = new[] { "fleet", "sync", "system" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Classes a client may still use on a mirror: they read, stop the picture, arm, or administer the node.</summary>
    private static readonly FrozenSet<ShowClass> Kept = new[] { ShowClass.Query, ShowClass.Panic, ShowClass.Show, ShowClass.Admin, ShowClass.Assistant, ShowClass.Fleet }.ToFrozenSet();

    /// <summary>The words that refuse the line here, or null when it may run. <paramref name="role"/> is the node's fleet role;
    /// <paramref name="words"/> what a refusal says (the mirror's or the fence's way out).</summary>
    public static string? Refused(VerbSpec spec, WireLine line, string transport, string role, string words)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        if (role is not (FleetRoles.Backup or FleetRoles.Fenced) || Inside.Contains(transport) || !spec.Mutates)
            return null;
        // A route is part of the show's plan, edited where the show is; its TEST reads and switches nothing.
        if (spec.Name == "FLEET ROUTE")
            return line.Words.Skip(2).FirstOrDefault()?.Equals("TEST", StringComparison.OrdinalIgnoreCase) == true ? null : words;
        // The fleet's own verbs, and a fetch of a file by its hash (it edits no show: the mirror's missing file is fetched this way).
        if (spec.Category == VerbCategory.Fleet || spec.Name == "MEDIA FETCH")
            return null;
        // A draft lands over the show it was made on: a mirror's show is its primary's, so no draft is begun or landed here (one left
        // from before may still be let go).
        if (spec.Name is "BLIND ON" or "BLIND COMMIT")
            return words;
        return Kept.Contains(ShowPolicy.ClassOf(spec, line)) ? null : words;
    }
}

/// <summary>The proof a peer's pull carries over HTTP(S) (fleet.md §3.3): the HMAC of the node's pairing token over a fresh nonce and
/// the request ("GET https /mirror/project"), in the same scheme as the wire's AUTH-PROOF, so the token itself never crosses the network.
/// The nonce is "&lt;unix ms&gt;-&lt;32 hex&gt;": the server takes it once, within <see cref="WindowMs"/> of its own wall clock.</summary>
public static class PullProof
{
    public const string NonceHeader = "X-PlayMap-Nonce";
    public const string ProofHeader = "X-PlayMap-Proof";

    /// <summary>How far a nonce's time may be from the server's wall clock.</summary>
    public const long WindowMs = 60_000;

    public static string NewNonce(long unixMs) => unixMs.ToString(CultureInfo.InvariantCulture) + "-" + Pairing.NewNonce();

    /// <param name="scheme">"https" or "http": a proof made for one is refused on the other, so a request signed for a plain
    /// connection (to whoever answered it) cannot be relayed to the node over HTTPS.</param>
    public static string Sign(string token, string nonce, string method, string scheme, string path) =>
        Pairing.Proof(token, nonce, Message(method, scheme, path));

    public static bool Verify(string token, string nonce, string method, string scheme, string path, string? proof) =>
        Pairing.Verify(token, nonce, Message(method, scheme, path), proof);

    /// <summary>The nonce's time, when it is a nonce of this form.</summary>
    public static bool TryTime(string? nonce, out long unixMs)
    {
        unixMs = 0;
        if (nonce is null || nonce.Length > 64)
            return false;
        var dash = nonce.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && nonce.Length - dash - 1 == 32
            && nonce.AsSpan(dash + 1).ContainsOnlyHexLower()
            && long.TryParse(nonce.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out unixMs);
    }

    private static string Message(string method, string scheme, string path) =>
        method.ToUpperInvariant() + " " + scheme.ToLowerInvariant() + " " + path;

    private static bool ContainsOnlyHexLower(this ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}

/// <summary>Which of a primary's own lines its backup takes through the mirror's air feed: the air verbs (panic and its clear,
/// blackout, master, freeze, pattern) that the sync group's feed does not already carry to it — a line the group's fan-out sends
/// reaches the backup there, once.</summary>
public static class MirrorAir
{
    private static readonly FrozenSet<string> Verbs =
        new[] { "PANIC", "PANIC CLEAR", "BLACKOUT", "MASTER", "FREEZE", "PATTERN" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The kind a backup that joins later is handed the latest of, or null when the line is not the mirror's air.</summary>
    public static string? KindOf(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (VerbCatalog.Match(line) is not { } m || !Verbs.Contains(m.Spec.Name))
            return null;
        return m.Spec.Name switch
        {
            "PANIC" or "PANIC CLEAR" => "panic",
            var n => n.ToLowerInvariant(),
        };
    }
}
