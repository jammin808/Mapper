using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Sync;

/// <summary>INDEX's sync section: the group this node is in, lit by its verdict (the leader's board) or by the follower's link, with
/// a key that asks for the group's status.</summary>
public sealed class SyncIndex(EngineCore engine, SyncNode sync) : IIndexContributor
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
        var group = sync.Settings.Group;
        if (group.Length > 0 && sync.Settings.Role != "solo")
        {
            var picture = sync.Picture(engine.IsAnimating);
            var leads = sync.Role == "leader";
            var light = leads ? picture.Light : sync.FeedWord == "live" ? Light.Green : sync.FeedWord == "lost" ? Light.Red : Light.Amber;
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
