using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Fx;

/// <summary>INDEX section "fx": one row per effect slot (its chain's owner as parent, its light and state from what the renderer
/// observed), and a preset per slot — bypass on a key, mix on an encoder (a turn is a BY step).</summary>
internal sealed class FxIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "fx";

    public IndexPresetHint Hint { get; } = new("Effects", "fx",
    [
        new IndexKey("Bypass", "FX BYPASS $(id) TOGGLE", "fx.bypass", "instant"),
        new IndexKey("Mix +", "FX SET $(id) mix BY 0.05", "fx.mix", "hold"),
        new IndexKey("Mix −", "FX SET $(id) mix BY -0.05", "fx.mix", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        foreach (var v in engine.Fx.Views(project))
        {
            w.WriteStartObject();
            w.WriteString("id", v.Slot.Id);
            w.WriteString("kind", "fx");
            w.WriteString("label", $"{v.Label} · {v.Owner.Name}");
            w.WriteString("parent", v.Owner.Id);
            w.WriteString("light", v.Light.ToString().ToLowerInvariant());
            w.WriteString("state", v.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
