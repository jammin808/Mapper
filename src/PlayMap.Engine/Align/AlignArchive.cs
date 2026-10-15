using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Align;

/// <summary>
/// A run's folder beside the project ("align/&lt;run&gt;"): run.json (the record) and each output's warp-&lt;op&gt;.pfm and
/// blend-&lt;op&gt;.pfm. A folder is written beside under a hidden name and moved into place whole; one that exists is never written
/// again, so undo, REVERT and the ledger's history always find the files they name. Paths come only from a valid run id, and a file
/// a project names is opened only inside the project's folder.
/// </summary>
public static partial class AlignArchive
{
    public const string Folder = "align";

    [GeneratedRegex("^ar[0-9a-f]{8}$")]
    private static partial Regex RunIdPattern();

    /// <summary>A run id this node makes ("ar" and eight hex digits): the only kind a path is made from.</summary>
    public static bool IsRunId(string? id) => id is not null && RunIdPattern().IsMatch(id);

    /// <summary>The run's folder relative to the project ("align/ar1a2b3c4d").</summary>
    public static string DataPath(string runId) => IsRunId(runId) ? Folder + "/" + runId : throw new ArgumentException($"{runId} is not a run id", nameof(runId));

    /// <summary>Writes the run's folder whole; throws when it exists already.</summary>
    public static void Save(string projectFolder, AlignRunRecord record, IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(files);
        var final = Path.Combine(projectFolder, Folder, record.Id);
        _ = DataPath(record.Id);
        if (Directory.Exists(final))
            throw new IOException($"{DataPath(record.Id)} exists — a run's folder is never written again");
        var temp = Path.Combine(projectFolder, Folder, "." + record.Id + ".partial");
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(temp, Path.GetFileName(name)), bytes);
        File.WriteAllText(Path.Combine(temp, "run.json"), Json(record), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Directory.Move(temp, final);
    }

    /// <summary>A run read back from its folder, or null when there is none (or it is not a run's).</summary>
    public static AlignRunRecord? Load(string? projectFolder, string runId)
    {
        if (string.IsNullOrEmpty(projectFolder) || !IsRunId(runId))
            return null;
        var path = Path.Combine(projectFolder, Folder, runId, "run.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>A run's capture of one output and its black picture, read back from its folder (null when missing or unreadable).</summary>
    public static Core.Align.AlignCapture? LoadCapture(string? projectFolder, string runId, string outputId, Core.Align.ProjectorPrior prior)
    {
        if (string.IsNullOrEmpty(projectFolder) || !IsRunId(runId) || outputId.IndexOfAny(['/', '\\', '.']) >= 0)
            return null;
        var path = Path.Combine(projectFolder, Folder, runId, $"capture-{outputId}.bin");
        try
        {
            return File.Exists(path) ? Core.Align.AlignCaptureFile.Read(File.ReadAllBytes(path), prior) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The full path of a file a project names (relative to its folder), or null when the name leaves the folder.</summary>
    public static string? Resolve(string? projectFolder, string relative)
    {
        if (string.IsNullOrEmpty(projectFolder) || string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
            return null;
        var root = Path.GetFullPath(projectFolder);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }

    public static string Json(AlignRunRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return EngineCore.JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("id", r.Id);
            w.WriteString("group", r.GroupId);
            w.WriteString("state", r.State);
            w.WriteNumber("startedUnixMs", r.StartedUnixMs);
            w.WriteNumber("finishedUnixMs", r.FinishedUnixMs);
            w.WriteString("camera", r.CameraId);
            w.WriteString("fit", r.Fit);
            w.WriteString("words", r.Words);
            Number(w, "seamPx", r.SeamPx);
            Number(w, "coverage", r.CoverageShare);
            Number(w, "whiteP5OverP95", r.WhiteP5OverP95);
            Number(w, "planarRmsPx", r.PlanarRmsPx);
            w.WritePropertyName("score");
            WriteScore(w, r.Score);
            w.WriteStartArray("outputs");
            foreach (var o in r.Outputs)
            {
                w.WriteStartObject();
                w.WriteString("id", o.OutputId);
                w.WriteString("name", o.Name);
                Number(w, "gamma", o.Gamma);
                Number(w, "decoded", o.DecodedShare);
                Number(w, "heldOutRmsPx", o.HeldOutRmsPx);
                Number(w, "fitRmsPx", o.FitRmsPx);
                w.WritePropertyName("measured");
                JsonSerializer.Serialize(w, o.Measured, ProjectJson.Options);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>A score whole, every number as it is: read back, it is the score the run staged, so a run accepted from its folder
    /// puts the same result in the ledger as one accepted from the runner.</summary>
    internal static void WriteScore(Utf8JsonWriter w, QualityScore s)
    {
        w.WriteStartObject();
        Exact(w, "value", s.Value);
        w.WriteString("unit", s.Unit);
        w.WriteString("grade", s.Grade);
        Exact(w, "rmsPx", s.RmsPx);
        Exact(w, "p95Px", s.P95Px);
        Exact(w, "confidence", s.Confidence);
        w.WriteString("source", s.Source);
        w.WriteString("words", s.Words);
        w.WriteStartArray("terms");
        foreach (var t in s.Terms)
        {
            w.WriteStartObject();
            w.WriteString("code", t.Code);
            w.WriteString("words", t.Words);
            Exact(w, "value", t.Value);
            w.WriteString("unit", t.Unit);
            Exact(w, "penalty", t.Penalty);
            w.WriteString("light", t.Light);
            w.WriteString("where", t.Where);
            w.WriteString("fix", t.Fix);
            w.WriteString("hint", t.Hint);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter w, string name, double value)
    {
        if (double.IsFinite(value))
            w.WriteNumber(name, Math.Round(value, 4));
        else
            w.WriteNull(name);
    }

    private static void Exact(Utf8JsonWriter w, string name, double value)
    {
        if (double.IsFinite(value))
            w.WriteNumber(name, value);
        else
            w.WriteNull(name);
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static AlignRunRecord Read(JsonElement e)
    {
        var outputs = ImmutableArray.CreateBuilder<AlignRunOutput>();
        foreach (var o in e.GetProperty("outputs").EnumerateArray())
        {
            var measured = o.GetProperty("measured").Deserialize<MeasuredCorrection>(ProjectJson.Options)
                ?? throw new InvalidOperationException("an output without its correction");
            outputs.Add(new AlignRunOutput(o.GetProperty("id").GetString()!, o.GetProperty("name").GetString() ?? "", measured, Num(o, "gamma"),
                Num(o, "decoded"), Num(o, "heldOutRmsPx"), Num(o, "fitRmsPx")));
        }
        var s = e.GetProperty("score");
        var terms = ImmutableArray.CreateBuilder<ScoreTerm>();
        foreach (var t in s.GetProperty("terms").EnumerateArray())
        {
            terms.Add(new ScoreTerm(t.GetProperty("code").GetString()!, t.GetProperty("words").GetString() ?? "", Num(t, "value"), t.GetProperty("unit").GetString() ?? "",
                Num(t, "penalty"), t.GetProperty("light").GetString() ?? "", Text(t, "where"), t.GetProperty("fix").GetString() ?? "", Text(t, "hint")));
        }
        static double Or0(double v) => double.IsNaN(v) ? 0 : v;
        var score = new QualityScore(Num(s, "value"), s.GetProperty("unit").GetString() ?? "points", s.GetProperty("grade").GetString() ?? "",
            Or0(Num(s, "rmsPx")), Or0(Num(s, "p95Px")), Or0(Num(s, "confidence")), terms.ToImmutable(), s.GetProperty("words").GetString() ?? "")
        {
            Source = s.GetProperty("source").GetString() ?? "align",
        };
        return new AlignRunRecord(e.GetProperty("id").GetString()!, e.GetProperty("group").GetString()!, e.GetProperty("state").GetString()!,
            e.GetProperty("startedUnixMs").GetInt64(), e.GetProperty("finishedUnixMs").GetInt64(), e.GetProperty("camera").GetString() ?? "",
            e.GetProperty("fit").GetString() ?? "", e.GetProperty("words").GetString() ?? "", outputs.ToImmutable(), score, Num(e, "seamPx"),
            Num(e, "coverage"), Num(e, "whiteP5OverP95"), Num(e, "planarRmsPx"));
    }
}
