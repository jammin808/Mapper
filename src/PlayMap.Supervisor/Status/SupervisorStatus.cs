using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;
using PlayMap.Supervisor.Policy;

namespace PlayMap.Supervisor.Status;

/// <summary>The supervisor's status file (<c>%ProgramData%\PlayMap\supervisor\status.json</c>), replaced whole at every change: its
/// state, the engine's pid (0 none), restarts within the window, the last cause, the freeze rule's words and when (Unix ms). The
/// engine reads it into STATE's <c>machine.install.supervisor</c>, the desk shows it while the engine is being started. It holds no
/// secret.</summary>
public sealed record SupervisorStatus(string State, int Pid, int Restarts, string LastCause, string FreezeRule, long AtUnixMs)
{
    /// <summary>The file's name in the supervisor's folder.</summary>
    public const string FileName = "status.json";

    /// <summary>The largest file read.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>A state's word: "waiting-for-session", "starting", "running", "stopping", "backoff", "stopped-too-many-restarts",
    /// "held".</summary>
    public static string Word(SupervisorState state) => state switch
    {
        SupervisorState.WaitingForSession => "waiting-for-session",
        SupervisorState.Starting => "starting",
        SupervisorState.Running => "running",
        SupervisorState.Stopping => "stopping",
        SupervisorState.Backoff => "backoff",
        SupervisorState.StoppedTooManyRestarts => "stopped-too-many-restarts",
        _ => "held",
    };

    /// <summary>The status of <paramref name="core"/> now.</summary>
    public static SupervisorStatus Of(SupervisorCore core, int pid, long atUnixMs)
    {
        ArgumentNullException.ThrowIfNull(core);
        return new SupervisorStatus(Word(core.State), pid, core.Restarts, core.LastCause, core.FreezeRule, atUnixMs);
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("state", State);
            w.WriteNumber("pid", Pid);
            w.WriteNumber("restarts", Restarts);
            w.WriteString("lastCause", LastCause);
            w.WriteString("freezeRule", FreezeRule);
            w.WriteNumber("at", AtUnixMs);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A status read back, or null when the text is not one.</summary>
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
                || !r.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var p) || p < 0
                || !r.TryGetProperty("restarts", out var restarts) || !restarts.TryGetInt32(out var n) || n < 0
                || !r.TryGetProperty("at", out var at) || !at.TryGetInt64(out var t))
                return null;
            return new SupervisorStatus(state.GetString() ?? "", p, n, Text(r, "lastCause"), Text(r, "freezeRule"), t);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
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

    /// <summary>The status <paramref name="folder"/> holds, or null.</summary>
    public static SupervisorStatus? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? Parse(DurableFile.ReadAllText(path)) : null;
    }

    private static string Text(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
