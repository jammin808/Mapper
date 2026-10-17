using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.App.State;
using PlayMap.Core.Bus;

namespace PlayMap.App.ViewModels.Health;

/// <summary>One part of the readiness light: its name, its light and its words, as STATE says them.</summary>
public sealed record ReadinessPart(string Name, string Light, string Words);

/// <summary>The show strip's readiness light: the worst of the show's last preflight (STATE.show.preflight, its last report's light
/// while it runs again), the fleet's light (STATE.fleet) and the sync group's light (STATE.sync), each in the node's own words. A
/// part STATE does not carry (no preflight report, no fleet monitor, no sync group) is left out; with none the light is grey. Pure:
/// the same STATE gives the same light.</summary>
public sealed record Readiness(string Light, ImmutableArray<ReadinessPart> Parts)
{
    public static readonly Readiness Unknown = new("grey", []);

    /// <summary>The parts' worst light first, the words of the worst part (the first of them when two are as bad).</summary>
    public string Words => Parts.IsDefaultOrEmpty ? "" : Parts.OrderByDescending(p => Rank(p.Light)).First().Words;

    /// <summary>One line per part: "show: red — outputs: Output 1 — this machine has no display for it".</summary>
    public string Tip => Parts.IsDefaultOrEmpty ? "" : string.Join("\n", Parts.Select(p => $"{p.Name}: {p.Light}{(p.Words.Length > 0 ? " — " + p.Words : "")}"));

    public bool Equals(Readiness? other) =>
        other is not null && Light == other.Light && Parts.AsSpan().SequenceEqual(other.Parts.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Light, Parts.Length);

    /// <summary>A light's rank: red over amber over green over grey (unknown).</summary>
    public static int Rank(string light) => ShowContext.ParseLight(light) switch
    {
        Core.Bus.Light.Red => 3,
        Core.Bus.Light.Amber => 2,
        Core.Bus.Light.Green => 1,
        _ => 0,
    };

    public static Readiness Of(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var parts = ImmutableArray.CreateBuilder<ReadinessPart>(3);
        if (Section(state, "show")?.Obj("preflight") is { } pf && pf.StrOrNull("light") is { } pl)
            parts.Add(new ReadinessPart("show", Word(pl), pf.Str("firstOpen") is { Length: > 0 } open ? open : "show-ready " + pf.Str("ready")));
        if (Section(state, "fleet") is { } fleet && fleet.StrOrNull("light") is { } fl)
            parts.Add(new ReadinessPart("fleet", Word(fl), fleet.Str("headline")));
        if (Section(state, "sync") is { } sync && SyncPart(sync, state.Playlists.Any(p => p.IsPlaying)) is { } s)
            parts.Add(s);
        if (parts.Count == 0)
            return Unknown;
        var worst = parts.Select(p => p.Light).MaxBy(Rank)!;
        return new Readiness(worst, parts.ToImmutable());
    }

    private static string Word(string light) => ShowContext.LightWord(ShowContext.ParseLight(light));

    private static JsonElement? Section(StateSnapshot s, string key) =>
        s.Root.ValueKind == JsonValueKind.Object && s.Root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    /// <summary>A free-running pair that slips sooner than this while the show plays is amber (the sync board's rule).</summary>
    private const double SlipAmberS = 600;

    /// <summary>The sync group's light as the node gives it, from STATE.sync. A follower's is its own link and clock (the verdict is
    /// the leader's): red while its clock holds (freewheelS) or the anchor feed is lost, green while the feed is live, amber otherwise.
    /// A leader's is the sync board's over the outputs it hears: grey before any is heard; red while one fell out of the lock; amber
    /// for mixed rates, or for a verdict short of one vblank while every heard output reports locked (framelocked or house-locked:
    /// one is off the others' vblank); amber when a free-running pair slips within ten minutes while a playlist plays; green on one
    /// vblank or free-running at ease. A node configured into a group it has not joined is grey. Null for a node in no sync group.</summary>
    private static ReadinessPart? SyncPart(JsonElement sync, bool playing)
    {
        if (sync.StrOrNull("verdict") is not { } verdict || sync.Str("group").Length == 0)
            return null;
        var words = sync.Str("words", verdict);
        var light = sync.Str("role") switch
        {
            "follower" => sync.Num("freewheelS", 0) > 0 || sync.Str("feed") == "lost" ? "red" : sync.Str("feed") == "live" ? "green" : "amber",
            "leader" => LeaderLight(sync, verdict, playing),
            _ => "grey",
        };
        return new ReadinessPart("sync", light, words);
    }

    private static string LeaderLight(JsonElement sync, string verdict, bool playing)
    {
        var heard = sync.Arr("outputs").Where(o => o.Bool("heard")).Select(o => o.Str("state")).ToList();
        if (heard.Count == 0)
            return "grey";
        if (heard.Contains("fell-out"))
            return "red";
        return verdict switch
        {
            "same vblank" => "green",
            "mixed rate" => "amber",
            "free-running" when heard.All(s => s is "framelocked" or "house-locked") => "amber",
            "free-running" when playing && sync.Arr("pairs").Any(p => p.Num("nextSlipS", double.PositiveInfinity) < SlipAmberS) => "amber",
            "free-running" => "green",
            _ => "grey",
        };
    }
}
