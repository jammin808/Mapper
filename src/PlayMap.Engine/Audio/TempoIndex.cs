using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Audio;

/// <summary>INDEX section "tempo": one row, the beat clock (its tempo as the label, its source as the state), and a deck's keys for
/// it — TAP, RESYNC (the bar), HALF, DOUBLE, and the BPM on an encoder (a turn is ±1 BPM, scheduled like any change).</summary>
internal sealed class TempoIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "tempo";

    public IndexPresetHint Hint { get; } = new("Tempo", "tempo",
    [
        new IndexKey("Tap", "TEMPO TAP", "audio.tempo.bpm", "instant"),
        new IndexKey("Resync", "TEMPO RESYNC BAR", "audio.beat", "instant"),
        new IndexKey("Half", "TEMPO HALF", "audio.tempo.bpm", "instant"),
        new IndexKey("Double", "TEMPO DOUBLE", "audio.tempo.bpm", "instant"),
        new IndexKey("BPM +", "TEMPO SET +1", "audio.tempo.bpm", "hold"),
        new IndexKey("BPM −", "TEMPO SET -1", "audio.tempo.bpm", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var clock = engine.AudioRt.Tempo;
        w.WriteStartArray();
        w.WriteStartObject();
        w.WriteString("id", "tempo");
        w.WriteString("kind", "tempo");
        w.WriteString("label", clock.Bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM");
        w.WriteString("parent", "");
        w.WriteString("light", "green");
        w.WriteString("state", project.Tempo.Source);
        w.WriteEndObject();
        w.WriteEndArray();
    }
}
