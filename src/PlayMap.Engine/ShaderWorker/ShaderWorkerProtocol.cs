using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>A source the engine hands the worker: its id (one per request), the kind's hash, the kind's name, the ISF fragment file
/// and its vertex file or null.</summary>
public sealed record ShaderRequest(long Id, string Kind, string Name, string Fragment, string? Vertex);

/// <summary>A native as the worker loaded it: its name, the file it is (its pin's on the worker's platform; "" where PlayMap carries
/// none), whether it loaded, and the words when it did not.</summary>
public readonly record struct NativeState(string Name, string File, bool Ok, string Words);

/// <summary>The worker's first line: its process id and its natives.</summary>
public sealed record ShaderHello(int Pid, ImmutableArray<NativeState> Natives);

/// <summary>The worker's answer to a request: each stage, the stage that failed ("done" when none did) with its words and ISF line,
/// and when every stage passed the DXIL and reflection of each entry (vertex, then pixel) and the layout; how long it took.</summary>
public sealed record ShaderReply(long Id, string Stage, string Words, int IsfLine, ImmutableArray<FxStage> Stages, ImmutableArray<byte[]> Dxil,
    ImmutableArray<byte[]> Reflection, ShaderLayout Layout, long Ms)
{
    public bool Ok => Stage == ShaderRoad.Done;

    /// <summary>A reply the engine makes itself when the worker could not answer (a stop, the deadline, a quarantine): the stage it
    /// names failed with these words, every stage of the road beside it not run.</summary>
    public static ShaderReply Refused(long id, string stage, string words) =>
        new(id, stage, words, 0, [new FxStage(stage, false, words, 0), .. ShaderRoad.StageNames.Select(s => new FxStage(s, null, "not run: " + words, 0))],
            [], [], ShaderLayout.Empty, 0);
}

/// <summary>The JSON lines between the engine and its shader worker, one object a line, "t" its kind: the engine's compile and
/// stop; the worker's hello and done. DXIL and reflection ride as base64. No secret rides them: nothing the worker does needs one.
/// A line that is not one of these is ignored (fail closed: it changes nothing).</summary>
public static class ShaderWorkerProtocol
{
    public static string Compile(ShaderRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return Line(w =>
        {
            w.WriteString("t", "compile");
            w.WriteNumber("id", r.Id);
            w.WriteString("kind", r.Kind);
            w.WriteString("name", r.Name);
            w.WriteString("fragment", r.Fragment);
            if (r.Vertex is null)
                w.WriteNull("vertex");
            else
                w.WriteString("vertex", r.Vertex);
        });
    }

    public const string Stop = "{\"t\":\"stop\"}";

    public static string Hello(int pid, IEnumerable<NativeState> natives)
    {
        ArgumentNullException.ThrowIfNull(natives);
        return Line(w =>
        {
            w.WriteString("t", "hello");
            w.WriteNumber("pid", pid);
            w.WriteStartArray("natives");
            foreach (var n in natives)
            {
                w.WriteStartObject();
                w.WriteString("name", n.Name);
                w.WriteString("file", n.File);
                w.WriteBoolean("ok", n.Ok);
                w.WriteString("words", n.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    public static string Done(long id, RoadResult result, long ms)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Line(w =>
        {
            w.WriteString("t", "done");
            w.WriteNumber("id", id);
            w.WriteBoolean("ok", result.Ok);
            w.WriteString("stage", result.Stage);
            w.WriteString("words", result.Words);
            w.WriteNumber("isfLine", result.IsfLine);
            w.WriteStartArray("stages");
            foreach (var s in result.Stages)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                if (s.Ok is { } ok)
                    w.WriteBoolean("ok", ok);
                else
                    w.WriteNull("ok");
                w.WriteString("words", s.Words);
                w.WriteNumber("line", s.Line);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("dxil");
            foreach (var d in result.Dxil)
                w.WriteBase64StringValue(d);
            w.WriteEndArray();
            w.WriteStartArray("reflection");
            foreach (var r in result.Reflection)
                w.WriteBase64StringValue(r);
            w.WriteEndArray();
            w.WritePropertyName("layout");
            result.Layout.Write(w);
            w.WriteNumber("ms", ms);
        });
    }

    /// <summary>The request a compile line carries; null when the line is not one.</summary>
    public static ShaderRequest? ParseCompile(JsonElement r)
    {
        try
        {
            var vertex = r.GetProperty("vertex");
            return new ShaderRequest(r.GetProperty("id").GetInt64(), Str(r, "kind"), Str(r, "name"),
                r.GetProperty("fragment").GetString() ?? "", vertex.ValueKind == JsonValueKind.String ? vertex.GetString() : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A line from the worker: a hello, a reply, or null when it is neither.</summary>
    public static object? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("t", out var t))
                return null;
            return t.GetString() switch
            {
                "hello" => new ShaderHello(r.GetProperty("pid").GetInt32(), [.. r.GetProperty("natives").EnumerateArray()
                    .Select(n => new NativeState(Str(n, "name"), Str(n, "file"), n.GetProperty("ok").GetBoolean(), Str(n, "words")))]),
                "done" => Reply(r),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static ShaderReply Reply(JsonElement r)
    {
        var stages = r.GetProperty("stages").EnumerateArray().Select(s => new FxStage(Str(s, "name"),
            s.GetProperty("ok").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null },
            Str(s, "words"), s.GetProperty("line").GetInt32())).ToImmutableArray();
        return new ShaderReply(r.GetProperty("id").GetInt64(), Str(r, "stage"), Str(r, "words"), r.GetProperty("isfLine").GetInt32(), stages,
            [.. r.GetProperty("dxil").EnumerateArray().Select(d => d.GetBytesFromBase64())],
            [.. r.GetProperty("reflection").EnumerateArray().Select(d => d.GetBytesFromBase64())],
            ShaderLayout.Read(r.GetProperty("layout")) ?? ShaderLayout.Empty, r.GetProperty("ms").GetInt64());
    }

    private static string Str(JsonElement r, string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Line(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
