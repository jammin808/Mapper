using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Sync;

/// <summary>INDEX's sync section: the group this node is in, lit as STATE.sync.light lights it (the light read with the node's verdict:
/// a leader's board, a follower's feed and clock, grey for a group it is configured into and has not joined), with a key that asks for
/// the group's status; no rows on a node without the sync part or in no group.</summary>
public sealed class SyncIndex(EngineCore engine) : IIndexContributor
{
    private static readonly IndexPresetHint PresetHint = new("Sync", "sync",
    [
        new IndexKey("$(label)", "SYNC STATUS", "sync.verdict", "instant"),
    ]);

    public string Section => "sync";

    public IndexPresetHint Hint => PresetHint;

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        if (engine.Services.GroupSync is not { } sync)
        {
            w.WriteEndArray();
            return;
        }
        var group = sync.Settings.Group;
        if (group.Length > 0 && sync.Settings.Role != "solo")
        {
            var picture = sync.Picture(engine.IsAnimating);
            var leads = sync.Role == "leader";
            var light = engine.SyncLight(sync, picture);
            w.WriteStartObject();
            w.WriteString("id", "sync:" + group);
            w.WriteString("kind", "sync");
            w.WriteString("label", group);
            w.WriteNull("parent");
            w.WriteString("light", ShowContext.LightWord(light));
            w.WriteString("state", leads ? picture.Verdict : sync.FeedWord);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
