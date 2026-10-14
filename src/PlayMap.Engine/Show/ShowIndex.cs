using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Show;

namespace PlayMap.Engine.Show;

/// <summary>INDEX's show section: the keys a show caller needs on a deck — ARM, DISARM, a PANIC per outcome this show plans (and
/// the default), PANIC CLEAR, the master's ends and the master on an encoder. Each row's id is the line its key sends; the
/// encoder row's key sends MASTER with the encoder's value. Written from published state only.</summary>
public sealed class ShowIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "show";

    public IndexPresetHint Hint { get; } = new("Show", "show",
    [
        new IndexKey("$(label)", "$(id)", "$(light)", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(w);
        var mode = engine.ShowNow;
        var panic = snapshot.Air.Panic;
        w.WriteStartArray();
        Row(w, "SHOW ARM", "show", "ARM", mode.Armed ? "green" : "grey", mode.Armed ? "armed" : "rehearsal");
        Row(w, "SHOW DISARM", "show", "DISARM", mode.Armed ? "grey" : "green", mode.Armed ? "armed" : "rehearsal");
        var outcomes = project.Show.Panic.Select(p => p.Outcome).Append(project.Show.Rules.DefaultOutcome).Append(PanicOutcome.Hard)
            .Where(o => o != PanicOutcome.Run).Distinct().OrderBy(o => o);
        Row(w, "PANIC", "panic", "PANIC", panic is null ? "grey" : "red", panic is null ? "ready" : PanicMath.Json(panic.Outcome));
        foreach (var o in outcomes)
            Row(w, "PANIC " + PanicMath.Word(o), "panic", "PANIC " + PanicMath.Word(o), panic?.Outcome == o ? "red" : "grey", PanicMath.Json(o));
        Row(w, "PANIC CLEAR", "panic", "CLEAR", panic is null ? "grey" : "amber", panic is null ? "not in panic" : "in panic");
        var level = (int)Math.Round(snapshot.Air.Master.To * 100);
        Row(w, "MASTER 0", "master", "MASTER 0", level == 0 ? "amber" : "grey", level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(w, "MASTER 100", "master", "MASTER 100", level == 100 ? "green" : "grey", level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(w, "MASTER", "encoder", "MASTER", level < 100 ? "amber" : "green", level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteEndArray();
    }

    private static void Row(Utf8JsonWriter w, string id, string kind, string label, string light, string state)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("kind", kind);
        w.WriteString("label", label);
        w.WriteString("parent", "show");
        w.WriteString("light", light);
        w.WriteString("state", state);
        w.WriteEndObject();
    }
}
