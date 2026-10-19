using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Core.Effects;

/// <summary>Why a kind is quarantined: its pass held the GPU past the output's period (the GPU-time guard), the GPU stopped inside
/// it (a device removal's breadcrumbs), its source stopped the shader worker three times, or a reason this build does not know (a
/// record written by a newer build: the entry stays quarantined and its word is kept).</summary>
public enum QuarantineReason
{
    GpuTime,
    DeviceRemoved,
    CompilerStopped,
    Unknown,
}

/// <summary>A quarantined kind: its sources' hash (<c>FxSource.Hash</c>, the kind's content identity), its name when it was
/// quarantined, why, the words that say what happened, when (Unix ms) and on which build.</summary>
public sealed record QuarantineEntry(string KindHash, string Kind, QuarantineReason Reason, string Words, long AtUnixMs, string Build)
{
    /// <summary>The word the record held for a reason this build does not know (<see cref="QuarantineReason.Unknown"/>), kept as it
    /// was; null for a reason this build knows.</summary>
    public string? UnknownWord { get; init; }

    /// <summary>The reason as STATE and the record spell it: this build's word, or the record's own word for one it does not know.</summary>
    public string ReasonWord => UnknownWord ?? ShaderQuarantine.Word(Reason);
}

/// <summary>What a change to the quarantine did: applied (written to the home, then held; its words name a write whose durability
/// is in doubt), unchanged (it would change nothing: the kind is already held, or not held), or not written (the home refused the
/// record: nothing changed, in memory or on disk).</summary>
public enum QuarantineOutcome
{
    Applied,
    Unchanged,
    NotWritten,
}

/// <summary>A change's outcome, the entries it added or released, and its words.</summary>
public sealed record QuarantineChange(QuarantineOutcome Outcome, ImmutableArray<QuarantineEntry> Entries, string Words)
{
    public bool Applied => Outcome == QuarantineOutcome.Applied;
}

/// <summary>The kinds this node will not draw: a quarantined kind's slots draw bypassed (their input passes unchanged) until a person
/// releases it. Kept in the node's home, never in the project: a quarantine is this machine's fact about its GPU, so a blind draft
/// never carries it and a hot backup does not mirror it. Read once at start: a record that cannot be read is kept aside under a name
/// of its own before anything can write over it, and says so (<see cref="LoadWords"/>); an entry whose reason this build does not
/// know stays quarantined. Every change is written to the home first and held only once written, so what memory holds is what a
/// restart reads. Adds and releases may come from any thread: a lock orders them, and readers take <see cref="All"/> or
/// <see cref="Hashes"/>, immutable values, from any thread.</summary>
public sealed class ShaderQuarantine
{
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly string _keptAsideFailed = "";
    private Held _held = new([], ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    /// <summary>The entries and their hashes, replaced whole on every change.</summary>
    private sealed record Held(ImmutableArray<QuarantineEntry> All, ImmutableHashSet<string> Hashes);

    /// <summary>A quarantine kept at <paramref name="path"/> (read now when the file is there), or in memory only when null.
    /// <paramref name="time"/> names a record kept aside (the system's clock when null).</summary>
    public ShaderQuarantine(string? path = null, TimeProvider? time = null)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        var name = Path.GetFileName(path);
        string text;
        ImmutableArray<QuarantineEntry> entries;
        int unreadable;
        try
        {
            text = DurableFile.ReadAllText(path);
            entries = Parse(text, out unreadable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            // Nothing in it can be held: it is moved aside, so no later write can destroy what it holds.
            var aside = KeepAside(path, time ?? TimeProvider.System, move: true, out var why);
            if (aside is null)
                _keptAsideFailed = why;
            LoadWords = aside is null
                ? $"the quarantine record {name} could not be read ({ex.Message}) and could not be kept aside ({why}): no kind is quarantined from it, and it is not written over"
                : $"the quarantine record {name} could not be read ({ex.Message}): it is kept as {Path.GetFileName(aside)}, and no kind is quarantined from it";
            return;
        }
        Set(entries);
        if (unreadable == 0)
            return;
        // What could be read is held and stays in place; a copy of the whole record keeps what could not.
        var copy = KeepAside(path, time ?? TimeProvider.System, move: false, out var copyWhy);
        if (copy is null)
            _keptAsideFailed = copyWhy;
        var some = $"{unreadable} entr{(unreadable == 1 ? "y" : "ies")} of the quarantine record {name} could not be read";
        LoadWords = copy is null
            ? $"{some}, and the record could not be kept aside ({copyWhy}): it is not written over"
            : $"{some}: the record is kept as {Path.GetFileName(copy)}, and {entries.Length} kind{(entries.Length == 1 ? " is" : "s are")} quarantined from it";
    }

    /// <summary>Why the record kept at start could not be read whole, and where it was kept aside; empty when it was read or there was
    /// none. While it is set, the node does not know every kind its record held, and preflight says so.</summary>
    public string LoadWords { get; } = "";

    /// <summary>Raised after every applied change, outside the lock.</summary>
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

    /// <summary>Quarantines a kind: unchanged when it is already quarantined; not written (nothing changes) when the home refuses
    /// the record.</summary>
    public QuarantineChange Add(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        QuarantineChange change;
        lock (_gate)
        {
            change = _held.Hashes.Contains(entry.KindHash)
                ? new QuarantineChange(QuarantineOutcome.Unchanged, [], $"{entry.Kind} is quarantined already")
                : Commit(_held.All.Add(entry), [entry]);
        }
        Raise(change);
        return change;
    }

    /// <summary>Lifts a kind's quarantine: unchanged when it is not quarantined; not written (nothing changes) when the home refuses
    /// the record.</summary>
    public QuarantineChange Release(string kindHash)
    {
        ArgumentNullException.ThrowIfNull(kindHash);
        QuarantineChange change;
        lock (_gate)
        {
            var released = _held.All.Where(e => string.Equals(e.KindHash, kindHash, StringComparison.Ordinal)).ToImmutableArray();
            change = released.IsEmpty
                ? new QuarantineChange(QuarantineOutcome.Unchanged, [], $"{kindHash} is not quarantined")
                : Commit(_held.All.RemoveRange(released), released);
        }
        Raise(change);
        return change;
    }

    /// <summary>Lifts every quarantine, answering the entries released: unchanged when none is held; not written (nothing changes)
    /// when the home refuses the record.</summary>
    public QuarantineChange ReleaseAll()
    {
        QuarantineChange change;
        lock (_gate)
        {
            change = _held.All.IsEmpty
                ? new QuarantineChange(QuarantineOutcome.Unchanged, [], "no kind is quarantined")
                : Commit([], _held.All);
        }
        Raise(change);
        return change;
    }

    /// <summary>Writes <paramref name="all"/> to the home, then holds it (under the lock). A write the home refuses changes nothing; a
    /// write that landed but whose folder could not be flushed is applied, with the words why its durability is in doubt.</summary>
    private QuarantineChange Commit(ImmutableArray<QuarantineEntry> all, ImmutableArray<QuarantineEntry> touched)
    {
        var words = "";
        try
        {
            Save(all);
        }
        catch (NotFlushedException ex)
        {
            words = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new QuarantineChange(QuarantineOutcome.NotWritten, [], $"the quarantine record could not be written ({ex.Message}): nothing changed");
        }
        Set(all);
        return new QuarantineChange(QuarantineOutcome.Applied, touched, words);
    }

    private void Raise(QuarantineChange change)
    {
        if (change.Applied)
            Changed?.Invoke();
    }

    private void Set(ImmutableArray<QuarantineEntry> all) =>
        Volatile.Write(ref _held, new Held(all, all.Select(e => e.KindHash).ToImmutableHashSet(StringComparer.Ordinal)));

    private void Save(ImmutableArray<QuarantineEntry> all)
    {
        if (_path is null)
            return;
        if (_keptAsideFailed.Length > 0)
            throw new IOException($"the record read at start could not be kept aside ({_keptAsideFailed}), so it is not written over");
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        DurableFile.WriteAllText(_path, Json(all));
    }

    /// <summary>Moves (or copies) the record beside itself under a name of its own (<c>…unreadable-&lt;unix ms&gt;</c>); the new path,
    /// or null with the words why it could not be.</summary>
    private static string? KeepAside(string path, TimeProvider time, bool move, out string why)
    {
        why = "";
        var stamp = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var aside = $"{path}.unreadable-{stamp}";
        for (var n = 2; File.Exists(aside) || Directory.Exists(aside); n++)
            aside = $"{path}.unreadable-{stamp}-{n.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            if (move)
                File.Move(path, aside);
            else
                File.Copy(path, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return null;
        }
    }

    /// <summary>The reason as STATE and the record spell it.</summary>
    public static string Word(QuarantineReason reason) => reason switch
    {
        QuarantineReason.GpuTime => "gpuTime",
        QuarantineReason.DeviceRemoved => "deviceRemoved",
        QuarantineReason.CompilerStopped => "compilerStopped",
        _ => "unknown",
    };

    private static QuarantineReason ReasonOf(string word) => word switch
    {
        "gpuTime" => QuarantineReason.GpuTime,
        "deviceRemoved" => QuarantineReason.DeviceRemoved,
        "compilerStopped" => QuarantineReason.CompilerStopped,
        _ => QuarantineReason.Unknown,
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
                w.WriteString("reason", e.ReasonWord);
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
    public static ImmutableArray<QuarantineEntry> Parse(string json) => Parse(json, out _);

    /// <summary>Reads a record entry by entry: an entry with no hash cannot be held and is counted in <paramref name="unreadable"/>;
    /// a field it lacks reads empty; a reason this build does not know is kept with its word. Throws when the text is not a record at
    /// all.</summary>
    public static ImmutableArray<QuarantineEntry> Parse(string json, out int unreadable)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("quarantine", out var list)
            || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("it holds no list of quarantined kinds");
        var entries = ImmutableArray.CreateBuilder<QuarantineEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        unreadable = 0;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || Text(e, "hash") is not { Length: > 0 } hash)
            {
                unreadable++;
                continue;
            }
            var word = Text(e, "reason") ?? "";
            var reason = ReasonOf(word);
            var at = e.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var ms) ? ms : 0;
            var entry = new QuarantineEntry(hash, Text(e, "kind") ?? "", reason, Text(e, "words") ?? "", at, Text(e, "build") ?? "")
            {
                UnknownWord = reason == QuarantineReason.Unknown ? word : null,
            };
            if (seen.Add(hash))
                entries.Add(entry);
        }
        return entries.ToImmutable();

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
