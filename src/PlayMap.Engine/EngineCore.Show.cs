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
        routes.Lane("SHOW", _ => CommandReply.Success(JsonOut(WriteShow)));
        routes.Lane("SHOW ARM", ShowArm);
        routes.Lane("SHOW DISARM", ShowDisarm);
        routes.Lane("SHOW SET", ShowSet);
        routes.Lane("PANIC", Panic);
        routes.Lane("PANIC CLEAR", PanicClear);
        routes.Lane("PANIC SET", PanicSet);
        routes.Lane("MASTER", Master);
        routes.Lane("LOCK", Lock);
        routes.Lane("UNLOCK", Unlock);
        routes.Lane("LOCKS", _ => CommandReply.Success(JsonOut(WriteLocks)));
        routes.OffLane("PREFLIGHT", PreflightAsync);
        routes.Lane("PREFLIGHT REPORT", PreflightReport);
        routes.Index(new ShowIndex(this));
    }

    partial void TickShow(long leaderNs) => TickPanic(leaderNs);

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
        refused = ShowGateLine(spec, line, session);

    partial void SnapshotShow(ref EngineSnapshot snapshot)
    {
        // The blind draft and a commit waiting for its boundary are wp2-blind's (show's continuation); without them the
        // snapshot is the air's alone.
    }

    private partial ProjectStore? BlindStoreFor(SessionInfo session) =>
        null; // wp2-blind (show's continuation): the session's draft.
}
