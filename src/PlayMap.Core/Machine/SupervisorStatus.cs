using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Core.Machine;

/// <summary>The supervisor's status file (<c>%ProgramData%\PlayMap\supervisor\status.json</c>, in <see cref="MachineFolder"/>, above the
/// engine's folder that holds <see cref="EnginePidRecord"/>), replaced whole at every change by PlayMap.Supervisor, its one writer: its
/// state, the engine's pid (0 none), restarts within the window, the last cause, the freeze rule's words and when (Unix ms). The
/// engine reads it into STATE's <c>machine.install.supervisor</c>; the desk's Connect says what it says when this machine's supervisor
/// keeps an engine that does not answer. It holds no secret.</summary>
public sealed record SupervisorStatus(string State, int Pid, int Restarts, string LastCause, string FreezeRule, long AtUnixMs)
{
    /// <summary>The file's name in the supervisor's folder (<see cref="MachineFolder"/>).</summary>
    public const string FileName = "status.json";

    /// <summary>The supervisor's folder on this machine: <c>%ProgramData%\PlayMap\supervisor</c> on Windows (the machine-wide application
    /// data folder's elsewhere, where no installer makes it). It holds the status file and the supervisor's log, which SYSTEM and
    /// administrators alone may write and every account may read, and the engine's folder (<see cref="EnginePidRecord.MachineFolder"/>),
    /// the one folder inside it the show user may write.</summary>
    public static string MachineFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap", "supervisor");

    /// <summary>The largest file read.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The status as one JSON object, its keys in the file's order: <c>state</c>, <c>pid</c>, <c>restarts</c>,
    /// <c>lastCause</c>, <c>freezeRule</c>, <c>at</c>. The file and STATE's <c>machine.install.supervisor</c> are written by it.</summary>
    public void WriteTo(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteString("state", State);
        w.WriteNumber("pid", Pid);
        w.WriteNumber("restarts", Restarts);
        w.WriteString("lastCause", LastCause);
        w.WriteString("freezeRule", FreezeRule);
        w.WriteNumber("at", AtUnixMs);
        w.WriteEndObject();
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
            WriteTo(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A status read back, or null when the text is not one: too long, not an object, no state, a pid or restart count
    /// that is missing or negative, no time.</summary>
    public static SupervisorStatus? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var p) || p < 0
                || !r.TryGetProperty("restarts", out var restarts) || restarts.ValueKind != JsonValueKind.Number || !restarts.TryGetInt32(out var n) || n < 0
                || !r.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var t))
                return null;
            return new SupervisorStatus(state.GetString() ?? "", p, n, Text(r, "lastCause"), Text(r, "freezeRule"), t);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the file into <paramref name="folder"/>, replacing it whole.</summary>
    public void Write(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        DurableFile.WriteAllText(Path.Combine(folder, FileName), ToJson());
    }

    /// <summary>This machine's status (in <see cref="MachineFolder"/>), or null: off Windows, where no supervisor runs, and when the
    /// folder holds none this process may read.</summary>
    public static SupervisorStatus? ReadThisMachine()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Read(MachineFolder());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file being replaced, or not this account's to read: the caller says no status was read.
            return null;
        }
    }

    /// <summary>The status <paramref name="folder"/> holds, or null when it holds none (no file, or a file that is not one).</summary>
    public static SupervisorStatus? Read(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? Parse(DurableFile.ReadAllText(path)) : null;
    }

    private static string Text(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
