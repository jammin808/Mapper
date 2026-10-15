using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Show;

namespace PlayMap.Engine;

/// <summary>wp2-show's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The work itself lives in Show/.</summary>
public sealed partial class EngineCore
{
    partial void RouteShow(VerbRoutes routes)
    {
        StartShow();
        StartPreflight();
        routes.Lane("SHOW", c => c.Args.Count == 0 || (c.Args.Count == 1 && c.Args[0].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            ? CommandReply.Success(JsonOut(WriteShow)) : CommandReply.Fail("SHOW [STATUS]"));
        routes.OffLane("SHOW ARM", ShowArmAsync);
        routes.OffLane("SHOW DISARM", ShowDisarmAsync);
        routes.Lane("SHOW SET", ShowSet);
        routes.Lane("PANIC", Panic);
        routes.Lane("PANIC CLEAR", PanicClear);
        routes.Lane("PANIC SET", PanicSet);
        routes.Lane("MASTER", Master);
        routes.Lane("LOCK", Lock);
        routes.Lane("UNLOCK", Unlock);
        routes.Lane("LOCKS", c => c.Args.Count == 0 ? CommandReply.Success(JsonOut(WriteLocks)) : CommandReply.Fail("LOCKS takes no words"));
        routes.Lane("BLIND", Blind);
        routes.Lane("BLIND ON", BlindOn);
        routes.Lane("BLIND OFF", BlindOff);
        routes.Lane("BLIND DIFF", BlindDiff);
        routes.Lane("BLIND COMMIT", BlindCommitVerb);
        routes.Lane("BLIND DISCARD", BlindDiscard);
        routes.OffLane("PREFLIGHT", PreflightAsync);
        routes.OffLane("PREFLIGHT REPORT", PreflightReportAsync);
        routes.Index(new ShowIndex(this));
    }

    partial void TickShow(long leaderNs)
    {
        // STATE.show.refused counts the last minute: when a refusal leaves the window, STATE moves with it.
        if (_refusals.Expire(Services.Wall.UnixMs))
            FactsChanged();
        TickPanic(leaderNs);
        TickBlind(leaderNs);
    }

    partial void StateShow(Utf8JsonWriter w)
    {
        w.WritePropertyName("show");
        WriteShow(w);
    }

    partial void AirShow(Utf8JsonWriter w) => WriteAirShow(w);

    partial void ContextShow(List<ContextItem> items, List<ContextLink> links) => AddShowContext(items, links);

    partial void HeadlineShow(ref string headline) => headline = ShowHeadline(headline);

    partial void ValidateShow(Project p, object entity, ref string? why)
    {
        // Panic plans and show rules are checked by PANIC SET and SHOW SET themselves; no generic edit reaches them.
    }

    partial void RecoverySaveShow(IDictionary<string, string> into) => into["show"] = ShowRecoveryJson();

    partial void RecoveryLoadShow(IReadOnlyDictionary<string, string> from)
    {
        if (from.TryGetValue("show", out var json))
            LoadShowRecovery(json);
    }

    partial void GateLine(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused, ref ProjectStore? lineStore) =>
        refused = ShowGateLine(spec, line, session, out lineStore);

    partial void SnapshotShow(ref EngineSnapshot snapshot) => SnapshotBlind(ref snapshot);

    /// <summary>Off the lane, every document edit of the draft's owner goes to its draft (its gate already took the line).</summary>
    private partial ProjectStore? BlindStoreFor(SessionInfo session) => DraftStoreFor(session);
}
