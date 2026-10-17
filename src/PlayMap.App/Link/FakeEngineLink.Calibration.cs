using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>Calibration's history in the demo, in the engine's shape: CALIBRATION HISTORY &lt;projector&gt;|&lt;output&gt; [&lt;n&gt;] lists the
/// target's kept results newest first with which is current, fallback and staged (how far each moved the picture is the engine's to
/// measure: −1 here). A test may hand the demo a real engine's answer to a query line (<see cref="Answer"/>), which it then gives
/// as the node did.</summary>
public sealed partial class FakeEngineLink
{
    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);

    /// <summary>The answer a node gave to a query line, given again whenever the desk sends that line (a real engine's, in a test).</summary>
    public void Answer(string line, string reply)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(reply);
        lock (_gate)
            _answers[line] = reply;
    }

    /// <summary>A line a test answered, or CALIBRATION HISTORY from the demo's own results; null for any other line (asked once the
    /// node's doors let the line through).</summary>
    private WireReply? Answered(WireLine line)
    {
        if (_answers.TryGetValue(line.Raw, out var given))
            return Ok(given);
        if (line.Verb != "CALIBRATION" || line.WordUpper(1) != "HISTORY")
            return null;
        if (line.Count is < 3 or > 4)
            return Err("CALIBRATION HISTORY <projector>|<output> [<n>]");
        var n = 20;
        if (line.Count == 4 && (!int.TryParse(line.Word(3), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n) || n < 1))
            return Err("CALIBRATION HISTORY <projector>|<output> [<n>]");
        var p = _store.Project;
        var word = line.Word(2);
        var (id, name, ids) = p.FindProjector(word) is { } pj ? (pj.Id, pj.Name, (pj.CalibrationId, pj.FallbackId, pj.StagedId))
            : p.FindOutput(word) is { } o ? (o.Id, o.Name, (o.CalibrationId, o.FallbackId, o.StagedId))
            : ("", "", ((string?)null, (string?)null, (string?)null));
        if (id.Length == 0)
            return Err("no projector or output " + word);
        var all = p.Stage.Calibrations.Select((c, i) => (c, i)).Where(x => x.c.TargetId == id)
            .OrderByDescending(x => x.c.CreatedUnixMs).ThenByDescending(x => x.i).Select(x => x.c).ToList();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("target", id);
            w.WriteString("name", name);
            w.WriteNumber("count", all.Count);
            w.WriteStartArray("snapshots");
            foreach (var s in all.Take(n))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteNumber("at", s.CreatedUnixMs);
                w.WriteString("source", s.Source.ToString().ToLowerInvariant());
                w.WriteNumber("score", s.Score.Value);
                w.WriteString("grade", s.Score.Grade);
                w.WriteString("words", s.Score.Words);
                w.WriteNumber("verifiedP95", s.VerifiedP95Px > 0 ? s.VerifiedP95Px : -1);
                w.WriteString("who", s.Who);
                Nullable(w, "parent", s.ParentId);
                Nullable(w, "scan", s.ScanId);
                w.WriteStartObject("delta");
                w.WriteNumber("mm", -1);
                w.WriteNumber("deg", -1);
                w.WriteNumber("wallPx", -1);
                w.WriteEndObject();
                w.WriteBoolean("current", s.Id == ids.Item1);
                w.WriteBoolean("fallback", s.Id == ids.Item2);
                w.WriteBoolean("staged", s.Id == ids.Item3);
                w.WriteBoolean("pinned", s.Pinned);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Ok(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void Nullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }
}
