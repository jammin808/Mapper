using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Fleet;

/// <summary>INDEX's eye section: the keys of Patterns' Eye page on a deck — NEXT and PREV PROBLEM lit by the worst light, the PROBLEMS and
/// ALL lenses, RESET — and one key per machine on the board with its light (a press focuses it; this node's own is "FLEET FOCUS self",
/// the same on every node). Each row's id is the line its key sends. Written from published state only.</summary>
internal sealed class FleetIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "eye";

    public IndexPresetHint Hint { get; } = new("Eye", "machine",
    [
        new IndexKey("$(label)", "$(id)", "$(light)", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var board = engine.Board();
        var (focus, lens) = engine.Fleet.View;
        var worst = ShowContext.LightWord(board.WorstLight);
        var problems = board.Problems.Length;
        w.WriteStartArray();
        Row(w, "FLEET NEXT", "problem", "NEXT PROBLEM", problems > 0 ? worst : "grey", problems.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(w, "FLEET PREV", "problem", "PREV PROBLEM", problems > 0 ? worst : "grey", problems.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(w, "FLEET LENS PROBLEMS", "lens", "PROBLEMS", lens == FleetLens.Problems ? "amber" : "grey", FleetGraph.LensWord(lens));
        Row(w, "FLEET LENS ALL", "lens", "ALL", lens == FleetLens.All ? "green" : "grey", FleetGraph.LensWord(lens));
        Row(w, "FLEET RESET", "view", "RESET", focus.Length > 0 ? "amber" : "grey", focus);
        foreach (var m in board.Machines)
        {
            var (light, headline) = FleetGraph.MachineLight(m);
            var id = FleetGraph.MachineId(m.Id);
            Row(w, "FLEET FOCUS " + (m.Self ? FleetGraph.SelfWord : id), "machine", m.Name, ShowContext.LightWord(light), focus == id ? "focused · " + headline : headline);
        }
        w.WriteEndArray();
    }

    private static void Row(Utf8JsonWriter w, string id, string kind, string label, string light, string state)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("kind", kind);
        w.WriteString("label", label);
        w.WriteString("parent", "eye");
        w.WriteString("light", light);
        w.WriteString("state", state);
        w.WriteEndObject();
    }
}
