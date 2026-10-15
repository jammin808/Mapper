namespace PlayMap.Core.Fleet;

/// <summary>How recently a machine's health was heard: live under 3 s of silence, stale to 6 s, gone after. A gone machine stays on
/// the board, red, until it is heard again — it never leaves silently.</summary>
public enum FleetHeard
{
    Live,
    Stale,
    Gone,
}

/// <summary>Another PlayMap machine as this node's fleet monitor knows it: who it is, where its hello came from, its last health
/// record (null until the first arrives), how recently that was, and words for what the link to it is doing ("subscribed over
/// TLS, certificate pinned", "CERTIFICATE CHANGED …").</summary>
public sealed record FleetPeer(string Id, string Name, string Address, HealthRecord? Health, FleetHeard Heard, string Words)
{
    /// <summary>True when its certificate was refused (changed or not the hello's): the board shows it red with the words.</summary>
    public bool PinRefused { get; init; }

    /// <summary>"pinned", "first use", "plain" (no TLS offered) or "refused" — for preflight's pin rows.</summary>
    public string Pin { get; init; } = "plain";

    /// <summary>Seconds since its last record (or since it was first heard, before any record).</summary>
    public double SilentS { get; init; }

    public static string HeardWord(FleetHeard heard) => heard switch
    {
        FleetHeard.Live => "live",
        FleetHeard.Stale => "stale",
        _ => "gone",
    };

    /// <summary>The liveness of a silence (ms): stale at 3 s, gone at 6 s.</summary>
    public static FleetHeard HeardOf(long silentMs) =>
        silentMs < StaleMs ? FleetHeard.Live : silentMs < GoneMs ? FleetHeard.Stale : FleetHeard.Gone;

    public const long StaleMs = 3_000;
    public const long GoneMs = 6_000;
}

/// <summary>The other machines this node hears (the fleet monitor), for STATE, the board, the context's peers and preflight. Read
/// from any thread; <see cref="Changed"/> is raised when what a board shows moved (a light, a headline, a liveness), not on every
/// record.</summary>
public interface IFleetPeers
{
    IReadOnlyList<FleetPeer> Peers();

    event Action? Changed;
}
