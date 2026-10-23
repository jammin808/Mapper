using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Core.Machine;

/// <summary>The engine's pid record: its process id, the line wire's port (0 while the wire is closed) and when it started (Unix ms).
/// The engine the supervisor started writes it as it comes up into the engine's folder (<see cref="MachineFolder"/>), where that
/// folder exists (the installer makes it; the engine never does), never over a record that names another PlayMap engine still running
/// by the identity rule every reader of the record holds (<see cref="EngineIdentity"/>), and removes it on a clean stop while it still
/// names this process. The supervisor reads it to adopt a running engine, the installer's armed check to ask that engine on loopback;
/// neither writes, renames or deletes a file in its folder. It holds no secret, as the hello holds none.</summary>
public sealed record EnginePidRecord(int Pid, int WirePort, long StartedUnixMs)
{
    /// <summary>The record's file name in the engine's folder.</summary>
    public const string FileName = "engine.pid";

    /// <summary>The largest record read; anything longer is not one. A file in the engine's folder is held to it by its length before
    /// any of it is read, and no more than one byte past it is ever read (<see cref="Named"/>).</summary>
    public const int MaxBytes = 4096;

    /// <summary>The engine's folder on this machine, inside the supervisor's (<see cref="SupervisorStatus.MachineFolder"/>):
    /// <c>%ProgramData%\PlayMap\supervisor\engine</c> on Windows (under the machine-wide application data folder elsewhere, where no
    /// installer makes it). It is the one folder there the show user may write, and it holds the record alone: the supervisor, which
    /// runs as SYSTEM, only reads it, so no file operation of SYSTEM's acts on a path the show user controls.</summary>
    public static string MachineFolder() => Path.Combine(SupervisorStatus.MachineFolder(), "engine");

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

    /// <summary>The record <paramref name="folder"/> holds now, or null when it holds none (no file, or a file that is not one). The
    /// folder is one the show user may write, and every reader of it reads through here — the supervisor at each step, the armed check,
    /// the elevated stop, the engine's own write — so a file longer than <see cref="MaxBytes"/> is refused by its length, read through
    /// the handle the read goes through, before any of it is read, and at most one byte past that is read of a file that grows
    /// meanwhile: a file grown to any length costs a reader no more than a record does.</summary>
    public static EnginePidRecord? Named(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return Parse(ReadBounded(Path.Combine(folder, FileName)));
    }

    /// <summary>The text of the record's file (UTF-8, a byte-order mark skipped), or null when there is no file or it is longer than
    /// <see cref="MaxBytes"/>.</summary>
    private static string? ReadBounded(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var file = DurableFile.OpenRead(path, bufferSize: 1);
            if (file.Length > MaxBytes)
                return null;
            var bytes = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < bytes.Length && (n = file.Read(bytes, read, bytes.Length - read)) > 0)
                read += n;
            if (read > MaxBytes)
                return null;
            var text = bytes.AsSpan(0, read);
            return Encoding.UTF8.GetString(text.StartsWith(Encoding.UTF8.Preamble) ? text[Encoding.UTF8.Preamble.Length..] : text);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Taken away between the look and the open: no record.
            return null;
        }
    }

    /// <summary>Writes the record into <paramref name="folder"/> (replacing the file whole) when that folder exists and its record
    /// names no other PlayMap engine still running; false, with nothing written and no folder made, otherwise.</summary>
    public bool TryWrite(string folder) => TryWrite(folder, out _);

    /// <summary>As <see cref="TryWrite(string)"/>, with the record that kept its place: <paramref name="holder"/> is the record of
    /// another engine still running (its file is left as it is), null when the folder does not exist or the record was written.
    /// The record's process is held to <see cref="EngineIdentity"/> by what this OS says of it now (<see cref="ProcessReader"/>): a
    /// record whose process is gone, whose pid another process now holds, or whose process cannot be read as PlayMap's engine, and a
    /// file that is not a record, are replaced.</summary>
    public bool TryWrite(string folder, out EnginePidRecord? holder) => TryWrite(folder, ProcessReader.Read, out holder);

    /// <summary>As <see cref="TryWrite(string, out EnginePidRecord?)"/>, with a process's facts read by <paramref name="read"/>.</summary>
    internal bool TryWrite(string folder, Func<int, ProcessFacts> read, out EnginePidRecord? holder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(read);
        holder = null;
        if (!Directory.Exists(folder))
            return false;
        if (Named(folder) is { } there && there.Pid != Pid && EngineIdentity.NamesRunningEngine(there, read))
        {
            holder = there;
            return false;
        }
        DurableFile.WriteAllText(Path.Combine(folder, FileName), ToJson());
        return true;
    }

    /// <summary>Removes the record in <paramref name="folder"/> when it still names this record's process; true when it did.</summary>
    public bool TryRemove(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var path = Path.Combine(folder, FileName);
        if (Parse(ReadBounded(path)) is not { } there || there.Pid != Pid || there.StartedUnixMs != StartedUnixMs)
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
