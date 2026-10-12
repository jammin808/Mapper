using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.App.Infrastructure;

/// <summary>Builds the lines the desk sends. Every number is written in the invariant culture (a German desk
/// sends 0.5, never 0,5), every free word is quoted when it needs it, and a JSON argument is always the
/// last word (the grammar reads JSON to the end of the line).</summary>
public static class VerbText
{
    /// <summary>A number as the wire writes it: invariant, shortest round-trip, no exponent for the sizes a
    /// desk sends.</summary>
    public static string Num(double value)
    {
        if (!double.IsFinite(value))
            return "0";
        var r = Math.Round(value, 9);
        return r.ToString("0.#########", CultureInfo.InvariantCulture);
    }

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A free word (a name, a path, a label) quoted when it holds a space or a quote.</summary>
    public static string Word(string word) => WireGrammar.Quote(word);

    /// <summary>A value as compact JSON in the project's own shape (camel case, enums as words).</summary>
    public static string Json<T>(T value) => ProjectJson.SerializeEntity(value);

    public static string JsonString(string value) => JsonSerializer.Serialize(value, ProjectJson.Compact);

    public static string JsonBool(bool value) => value ? "true" : "false";

    /// <summary>"SET &lt;id&gt; &lt;path&gt; &lt;json&gt;" — the generic field edit.</summary>
    public static string Set(string id, string path, string json) => $"SET {id} {path} {json}";

    public static string SetNum(string id, string path, double value) => Set(id, path, Num(value));

    public static string SetBool(string id, string path, bool value) => Set(id, path, JsonBool(value));

    public static string SetText(string id, string path, string value) => Set(id, path, JsonString(value));

    public static string SetJson<T>(string id, string path, T value) => Set(id, path, Json(value));

    /// <summary>An enum value as the project file writes it ("canvasRegion", "next").</summary>
    public static string EnumWord<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value, ProjectJson.Compact).Trim('"');

    public static string SetEnum<T>(string id, string path, T value) where T : struct, Enum => Set(id, path, Json(value));

    /// <summary>Nanoseconds from seconds typed by an operator ("12.5", "1:02.5"); null when unreadable.</summary>
    public static long? ParseSeconds(string? text)
    {
        if (PlayMap.Core.Playback.ClipTransport.TryParsePoint(text, out var ns))
            return ns;
        return null;
    }

    /// <summary>"1:02" / "1:02:03" from milliseconds, for chips and counters (whole seconds, rounded down
    /// while counting up and up while counting down, the way a countdown reads).</summary>
    public static string Clock(long ms, bool countdown = false)
    {
        if (ms < 0)
            ms = 0;
        var totalSeconds = countdown ? (ms + 999) / 1000 : ms / 1000;
        var h = totalSeconds / 3600;
        var m = totalSeconds / 60 % 60;
        var s = totalSeconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }
}
