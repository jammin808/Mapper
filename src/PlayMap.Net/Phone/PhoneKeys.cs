using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Storage;

namespace PlayMap.Net.Phone;

/// <summary>A phone this node knows: its node-local id, the name it joined under, the SHA-256 of its key (never the key), when it
/// joined and was last seen, and the camera rig it is in the project.</summary>
public sealed record PhoneRecord(string Id, string Name, string KeySha256, long CreatedUnixMs, long LastSeenUnixMs, string Rig);

/// <summary>
/// The phones a node knows, kept in its own folder (phones.json, never in the project): each phone's key is held only as
/// "sha256:&lt;hex&gt;", so the file lets no one in. Written whole to a temporary file and moved over the old one, readable by
/// this user alone where the platform has modes. A file that does not read (it does not parse, or this account may not read it) is
/// moved aside to "phones.json.unreadable" and the node starts with no phones; one that does not parse holds nothing to keep and
/// replaces an earlier ".unreadable". A record this account may not read is never written over and never moved over another: where
/// ".unreadable" is already taken, or its move aside is refused, it stays where it is, and the next save moves it aside first or the
/// save throws, writing nothing.
/// </summary>
public sealed class PhoneKeys
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<PhoneRecord> _phones = [];

    /// <summary>True while the record at the path is one this account may not read and was not moved aside (the name aside taken, or
    /// the move refused): no save is made over it until it has been moved aside.</summary>
    private bool _unreadHere;

    public PhoneKeys(string path)
        : this(path, null)
    {
    }

    /// <summary>The keys at <paramref name="path"/>, the record read by <paramref name="read"/> (null in the product: the durable
    /// read; a test stands for a record this account may not read with it).</summary>
    internal PhoneKeys(string path, Func<string, byte[]>? read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load(read ?? DurableFile.ReadAllBytes);
    }

    public string Path => _path;

    public IReadOnlyList<PhoneRecord> All
    {
        get
        {
            lock (_gate)
                return [.. _phones];
        }
    }

    public static string Digest(string key) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The phone whose key this is, compared digest to digest in fixed time.</summary>
    public PhoneRecord? FindByKey(string key)
    {
        var presented = Encoding.ASCII.GetBytes(Digest(key));
        PhoneRecord? found = null;
        lock (_gate)
        {
            foreach (var p in _phones)
            {
                if (CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(p.KeySha256)))
                    found = p;
            }
        }
        return found;
    }

    public PhoneRecord? Find(string id)
    {
        lock (_gate)
            return _phones.Find(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    public void Put(PhoneRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var i = _phones.FindIndex(p => string.Equals(p.Id, record.Id, StringComparison.Ordinal));
            if (i >= 0)
                _phones[i] = record;
            else
                _phones.Add(record);
            Save();
        }
    }

    /// <summary>Updates a phone that is still known (when it was last seen, its rig) and answers false for one that was forgotten,
    /// so a join in flight never writes a forgotten key back.</summary>
    public bool Touch(string id, long lastSeenUnixMs, string? rig = null)
    {
        lock (_gate)
        {
            var i = _phones.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (i < 0)
                return false;
            _phones[i] = _phones[i] with { LastSeenUnixMs = lastSeenUnixMs, Rig = rig ?? _phones[i].Rig };
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _phones.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    private void Load(Func<string, byte[]> read)
    {
        if (!File.Exists(_path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(read(_path));
            foreach (var p in doc.RootElement.GetProperty("phones").EnumerateArray())
            {
                var digest = p.GetProperty("keySha256").GetString() ?? "";
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
                    continue;
                _phones.Add(new PhoneRecord(p.GetProperty("id").GetString() ?? "", p.GetProperty("name").GetString() ?? "", digest,
                    p.GetProperty("createdUnixMs").GetInt64(), p.GetProperty("lastSeenUnixMs").GetInt64(),
                    p.TryGetProperty("rig", out var rig) ? rig.GetString() ?? "" : ""));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException
            or UnauthorizedAccessException)
        {
            _phones.Clear();
            if (ex is UnauthorizedAccessException && AsideTaken())
            {
                // An earlier ".unreadable" may be another account's record: neither is moved over the other.
                _unreadHere = true;
                return;
            }
            try
            {
                // A rename reads none of the file's bytes and keeps its owner and permissions: a record another account made is kept
                // whole beside the path.
                DurableFile.Replace(_path, _path + ".unreadable");
            }
            catch (Exception moved) when (moved is IOException or UnauthorizedAccessException)
            {
                // Left where it is (a move refused: access denied, a handle that shares no delete, a folder in the way), and the node
                // starts with no phones all the same. A record that does not parse holds nothing to keep, and the next save replaces
                // it; one this account may not read may hold another account's phones, and no save is made over it.
                _unreadHere = ex is UnauthorizedAccessException;
            }
        }
    }

    private void Save()
    {
        if (_unreadHere)
        {
            // Moved aside before anything is written: a name aside still taken, or a move still refused, throws out of the save,
            // which writes nothing.
            if (File.Exists(_path))
            {
                if (AsideTaken())
                    throw new IOException(
                        $"'{_path}' could not be read by this account and '{_path}.unreadable' is taken: nothing is saved over either.");
                DurableFile.Replace(_path, _path + ".unreadable");
            }
            _unreadHere = false;
        }
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteStartArray("phones");
            foreach (var p in _phones)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("name", p.Name);
                w.WriteString("keySha256", p.KeySha256);
                w.WriteNumber("createdUnixMs", p.CreatedUnixMs);
                w.WriteNumber("lastSeenUnixMs", p.LastSeenUnixMs);
                w.WriteString("rig", p.Rig);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        DurableFile.MoveIntoPlace(temp, _path);
    }

    private bool AsideTaken() => File.Exists(_path + ".unreadable") || Directory.Exists(_path + ".unreadable");
}
