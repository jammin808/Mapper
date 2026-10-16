using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.VideoIo;

/// <summary>INDEX section "video": one row per live input (its name as the label, its state, lit as the context lights it), and a
/// deck's keys per row to reconnect it (refused while it delivers, so a press on a healthy input does nothing but say so) and to
/// switch it to its backup or back to its primary (the feed it uses is STATE's videoio.inputs[].using).</summary>
internal sealed class VideoIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "video";

    public IndexPresetHint Hint { get; } = new("Video inputs", "input",
    [
        new IndexKey("Reconnect", "INPUT RECONNECT $(id)", "videoio.inputs.state", "hold"),
        new IndexKey("Backup", "INPUT SWITCH $(id) BACKUP", "videoio.inputs.using", "hold"),
        new IndexKey("Primary", "INPUT SWITCH $(id) PRIMARY", "videoio.inputs.using", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        foreach (var input in engine.VideoIo.InputsOf(project))
        {
            var state = input.Source is { } s ? PlayMap.Media.Video.LiveSource.StateWord(s.Live.State) : input.Problem.Length > 0 ? "failed" : "connecting";
            w.WriteStartObject();
            w.WriteString("id", input.Id);
            w.WriteString("kind", "input");
            w.WriteString("label", input.Item.Name);
            w.WriteString("parent", "");
            // Lit as the context lights it: lost or failed is red only on air; an input showing black, its fallback or its backup is amber.
            var standIn = input.Loss is "black" or "fallback" || input.Using == "backup";
            w.WriteString("light", state switch
            {
                "ok" => standIn ? "amber" : "green",
                "connecting" => "grey",
                "lost" or "failed" => input.Tally == "program" ? "red" : "amber",
                _ => "amber",
            });
            w.WriteString("state", state);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>INDEX section "sends": one row per send (its name, on or off, lit green while it sends, red when its sender failed) and
/// the key that starts or stops it.</summary>
internal sealed class SendsIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "sends";

    public IndexPresetHint Hint { get; } = new("Video sends", "send",
    [
        new IndexKey("On", "SEND ENABLE $(id) ON", "videoio.sends.state", "hold"),
        new IndexKey("Off", "SEND ENABLE $(id) OFF", "videoio.sends.state", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var slots = engine.VideoIo.SendsOf(project).ToDictionary(s => s.Send.Id, StringComparer.Ordinal);
        w.WriteStartArray();
        foreach (var send in project.Sends)
        {
            slots.TryGetValue(send.Id, out var slot);
            // Lit as the context lights it, from the same state.
            var (state, _) = EngineCore.SendState(send, slot);
            w.WriteStartObject();
            w.WriteString("id", send.Id);
            w.WriteString("kind", "send");
            w.WriteString("label", send.Name);
            w.WriteString("parent", SendShape.TryTap(send.Tap, out var kind, out var target, out _, out _) && kind != SendTapKind.Source ? target : "");
            w.WriteString("light", state switch { "failed" => "red", "sending" or "idle" or "standby" => "green", _ => "grey" });
            w.WriteString("state", state);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
