using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Core.Effects;

/// <summary>Why a kind is quarantined: its pass held the GPU past the output's period (the GPU-time guard), the GPU stopped inside
/// it (a device removal's breadcrumbs), or its source stopped the shader worker three times.</summary>
public enum QuarantineReason
{
    GpuTime,
    DeviceRemoved,
    CompilerStopped,
}

/// <summary>A quarantined kind: its sources' hash (<c>FxSource.Hash</c>, the kind's content identity), its name when it was
/// quarantined, why, the words that say what happened, when (Unix ms) and on which build.</summary>
public sealed record QuarantineEntry(string KindHash, string Kind, QuarantineReason Reason, string Words, long AtUnixMs, string Build);

/// <summary>The kinds this node will not draw: a quarantined kind's slots draw bypassed (their input passes unchanged) until a person
/// releases it. Kept in the node's home, never in the project: a quarantine is this machine's fact about its GPU, so a blind draft
/// never carries it and a hot backup does not mirror it. Read once at start; every change is written through at once (atomically,
/// beside the journal). One writer: its gate orders every add and release; readers take <see cref="All"/> or <see cref="Hashes"/>,
/// immutable values, from any thread.</summary>
public sealed class ShaderQuarantine
{
    private readonly Lock _gate = new();
    private readonly string? _path;
    private Held _held = new([], ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    /// <summary>The entries and their hashes, replaced whole on every change.</summary>
    private sealed record Held(ImmutableArray<QuarantineEntry> All, ImmutableHashSet<string> Hashes);

    /// <summary>A quarantine kept at <paramref name="path"/> (read now when the file is there), or in memory only when null.</summary>
    public ShaderQuarantine(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        try
        {
            Set(Parse(DurableFile.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            // A record that cannot be read quarantines nothing: every kind draws, and the words say why the record was set aside.
            LoadWords = $"the quarantine record {Path.GetFileName(path)} could not be read ({ex.Message}): no kind is quarantined";
        }
    }

    /// <summary>Why the record kept at start could not be read; empty when it was read or there was none.</summary>
    public string LoadWords { get; } = "";

    /// <summary>Raised after every change, outside the gate.</summary>
    public event Action? Changed;

    /// <summary>Every quarantined kind, oldest first.</summary>
    public ImmutableArray<QuarantineEntry> All => Volatile.Read(ref _held).All;

    /// <summary>The quarantined kinds' hashes: what a renderer checks a slot's kind against.</summary>
    public ImmutableHashSet<string> Hashes => Volatile.Read(ref _held).Hashes;

    public bool IsQuarantined(string kindHash, [NotNullWhen(true)] out QuarantineEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(kindHash);
        foreach (var e in All)
        {
            if (string.Equals(e.KindHash, kindHash, StringComparison.Ordinal))
            {
                entry = e;
                return true;
            }
        }
        entry = null;
        return false;
    }

    /// <summary>Quarantines a kind; false (nothing changes) when it is already quarantined.</summary>
    public bool Add(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_held.Hashes.Contains(entry.KindHash))
                return false;
            Set(_held.All.Add(entry));
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Lifts a kind's quarantine; false (nothing changes) when it is not quarantined.</summary>
    public bool Release(string kindHash)
    {
        ArgumentNullException.ThrowIfNull(kindHash);
        lock (_gate)
        {
            if (!_held.Hashes.Contains(kindHash))
                return false;
            Set(_held.All.RemoveAll(e => string.Equals(e.KindHash, kindHash, StringComparison.Ordinal)));
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Lifts every quarantine; answers the entries released (none: nothing changes).</summary>
    public ImmutableArray<QuarantineEntry> ReleaseAll()
    {
        ImmutableArray<QuarantineEntry> released;
        lock (_gate)
        {
            released = _held.All;
            if (released.IsEmpty)
                return released;
            Set([]);
            Save();
        }
        Changed?.Invoke();
        return released;
    }

    private void Set(ImmutableArray<QuarantineEntry> all) =>
        Volatile.Write(ref _held, new Held(all, all.Select(e => e.KindHash).ToImmutableHashSet(StringComparer.Ordinal)));

    private void Save()
    {
        if (_path is null)
            return;
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        DurableFile.WriteAllText(_path, Json(_held.All));
    }

    /// <summary>The reason as STATE and the record spell it.</summary>
    public static string Word(QuarantineReason reason) => reason switch
    {
        QuarantineReason.GpuTime => "gpuTime",
        QuarantineReason.DeviceRemoved => "deviceRemoved",
        _ => "compilerStopped",
    };

    private static QuarantineReason ReasonOf(string? word) => word switch
    {
        "gpuTime" => QuarantineReason.GpuTime,
        "deviceRemoved" => QuarantineReason.DeviceRemoved,
        "compilerStopped" => QuarantineReason.CompilerStopped,
        _ => throw new FormatException($"'{word}' is not a quarantine reason"),
    };

    /// <summary>The record as the node's home keeps it: one object with the entries in order.</summary>
    public static string Json(ImmutableArray<QuarantineEntry> entries)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("quarantine");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString("hash", e.KindHash);
                w.WriteString("kind", e.Kind);
                w.WriteString("reason", Word(e.Reason));
                w.WriteString("words", e.Words);
                w.WriteNumber("at", e.AtUnixMs);
                w.WriteString("build", e.Build);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>Reads a record written by <see cref="Json"/>; throws when it is not one.</summary>
    public static ImmutableArray<QuarantineEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = ImmutableArray.CreateBuilder<QuarantineEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.GetProperty("quarantine").EnumerateArray())
        {
            var entry = new QuarantineEntry(e.GetProperty("hash").GetString() ?? throw new FormatException("an entry has no hash"),
                e.GetProperty("kind").GetString() ?? "", ReasonOf(e.GetProperty("reason").GetString()), e.GetProperty("words").GetString() ?? "",
                e.GetProperty("at").GetInt64(), e.GetProperty("build").GetString() ?? "");
            if (seen.Add(entry.KindHash))
                list.Add(entry);
        }
        return list.ToImmutable();
    }
}
