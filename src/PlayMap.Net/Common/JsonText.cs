using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PlayMap.Net;

/// <summary>Small JSON helpers: compact writing with <see cref="Utf8JsonWriter"/> (no serializer options to build
/// per call) and tolerant reading of a peer's fields (a missing or mistyped field reads as its default, never a
/// throw).</summary>
public static class JsonText
{
    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    public static string Write(Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Compact))
            body(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static byte[] WriteBytes(Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Compact))
            body(w);
        return buffer.WrittenSpan.ToArray();
    }

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static double Num(JsonElement e, string name, double fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : fallback;

    public static double? NumOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public static long Long(JsonElement e, string name, long fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : fallback;

    public static bool Bool(JsonElement e, string name, bool fallback = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    public static bool? BoolOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    /// <summary>Parses a peer's JSON; null when it is not JSON (the caller treats that as "not a frame").</summary>
    public static JsonDocument? TryParse(string? json, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > maxChars)
            return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
