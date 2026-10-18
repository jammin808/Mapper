using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Core.Machine;

/// <summary>The engine's pid record: its process id, the line wire's port (0 while the wire is closed) and when it started (Unix ms).
/// The engine writes it as it comes up into the supervisor's folder, where that folder exists (the installer makes it; the engine
/// never does), and removes it on a clean stop while it still names this process. The supervisor reads it to adopt a running engine,
/// the installer's armed check to ask that engine on loopback. It holds no secret, as the hello holds none.</summary>
public sealed record EnginePidRecord(int Pid, int WirePort, long StartedUnixMs)
{
    /// <summary>The record's file name in the supervisor's folder.</summary>
    public const string FileName = "engine.pid";

    /// <summary>The largest record read; anything longer is not one.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The supervisor's folder on this machine: <c>%ProgramData%\PlayMap\supervisor</c> on Windows (the machine-wide
    /// application data folder elsewhere, where no installer makes it).</summary>
    public static string MachineFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap", "supervisor");

    /// <summary>The record as its file holds it: <c>{"pid":…,"port":…,"started":…}</c>.</summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", Pid);
            w.WriteNumber("port", WirePort);
            w.WriteNumber("started", StartedUnixMs);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A record read back, or null when the text is not one: too long, not an object, a pid that is not positive, a port
    /// outside 0–65535 or a negative start.</summary>
    public static EnginePidRecord? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !Int(r, "pid", out var pid) || pid <= 0 || pid > int.MaxValue
                || !Int(r, "port", out var port) || port is < 0 or > 65535
                || !Int(r, "started", out var started) || started < 0)
                return null;
            return new EnginePidRecord((int)pid, (int)port, started);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the record into <paramref name="folder"/> (replacing the file whole) when that folder exists; false, with
    /// nothing written and no folder made, when it does not.</summary>
    public bool TryWrite(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
            return false;
        DurableFile.WriteAllText(Path.Combine(folder, FileName), ToJson());
        return true;
    }

    /// <summary>Removes the record in <paramref name="folder"/> when it still names this record's process; true when it did.</summary>
    public bool TryRemove(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path) || Parse(DurableFile.ReadAllText(path)) is not { } there || there.Pid != Pid || there.StartedUnixMs != StartedUnixMs)
            return false;
        File.Delete(path);
        return true;
    }

    private static bool Int(JsonElement e, string name, out long value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }
}
