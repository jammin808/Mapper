using System.Text.Json;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Fleet;

/// <summary>INDEX's fleet section (wp2-backup): the keys a deck shows for this node's place in a hot-backup pair, each row's id the line
/// its key sends — FAILOVER on a backup (lit by its mirror: green identical, amber joining or resyncing, red lost), FAILBACK CONFIRM on a
/// node that took a show over, FLEET PROMOTE on a sync follower (red while its leader is silent), and each backup of a primary with its
/// state. Written from published state only; a node without the backup part lists the section with no rows.</summary>
internal sealed class BackupIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "fleet";

    public IndexPresetHint Hint { get; } = new("Fleet", "takeover",
    [
        new IndexKey("$(label)", "$(id)", "$(light)", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        if (engine.Services.Backup is { } backup)
        {
            var role = backup.Settings;
            if (role.Role == FleetRoles.Backup)
            {
                var state = backup.Mirror()?.State ?? "joining";
                // A primary gone silent turns the key red with its words, whatever the mirror said last.
                var gone = engine.Services.Clock.IsHolding;
                Row(w, "FAILOVER", "takeover", "FAILOVER", gone ? "red" : state switch { "identical" => "green", "lost" => "red", _ => "amber" },
                    gone ? "primary gone" : state);
            }
            if (role.Role == FleetRoles.Primary && role.Of.Length > 0)
                Row(w, "FAILBACK CONFIRM", "takeover", "FAILBACK", backup.Feed.Members.Any(m => m.Node == role.Of) ? "green" : "grey", "to " + role.Of);
            if (engine.Services.GroupSync is { Role: "follower" } sync && role.Role != FleetRoles.Backup)
                Row(w, "FLEET PROMOTE", "takeover", "PROMOTE", engine.Services.Clock.IsHolding ? "red" : "grey",
                    engine.Services.Clock.IsHolding ? "leader gone" : "following " + sync.Settings.Leader);
            foreach (var m in backup.Feed.Members)
            {
                var facts = engine.Services.FleetPeers?.Peers().FirstOrDefault(p => p.Id == m.Node)?.Health?.Facts;
                var state = facts?.Mirror is { Length: > 0 } s ? s : "joining";
                Row(w, "FLEET FOCUS " + FleetGraph.MachineId(m.Node), "backup", facts?.Name ?? m.Name, state == "identical" ? "green" : "amber", state);
            }
        }
        w.WriteEndArray();
    }

    private static void Row(Utf8JsonWriter w, string id, string kind, string label, string light, string state)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("kind", kind);
        w.WriteString("label", label);
        w.WriteString("parent", "fleet");
        w.WriteString("light", light);
        w.WriteString("state", state);
        w.WriteEndObject();
    }
}
