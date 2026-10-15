using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlayMap.Net.Phone;

/// <summary>A phone this node knows: its node-local id, the name it joined under, the SHA-256 of its key (never the key), when it
/// joined and was last seen, and the camera rig it is in the project.</summary>
public sealed record PhoneRecord(string Id, string Name, string KeySha256, long CreatedUnixMs, long LastSeenUnixMs, string Rig);

/// <summary>
/// The phones a node knows, kept in its own folder (phones.json, never in the project): each phone's key is held only as
/// "sha256:&lt;hex&gt;", so the file lets no one in. Written whole to a temporary file and moved over the old one, readable by
/// this user alone where the platform has modes. A file that does not read is kept aside and the node starts with no phones.
/// </summary>
public sealed class PhoneKeys
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<PhoneRecord> _phones = [];

    public PhoneKeys(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load();
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

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
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
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            _phones.Clear();
            try
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }
            catch (IOException)
            {
                // Left where it is: the next save replaces it.
            }
        }
    }

    private void Save()
    {
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
        File.Move(temp, _path, overwrite: true);
    }
}
