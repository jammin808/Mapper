using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayMap.Media;

/// <summary>A file's content hash, "sha256:" and 64 lowercase hex digits — the identity of a library item on
/// every node. The file is read once, sequentially, in 4 MiB blocks with the next read in flight while the last
/// is hashed; the hash is cancellable and reports progress. With a <see cref="HashCache"/>, a file whose path,
/// size and last-write time are unchanged is not read again (a 20 GB clip is hashed once per machine).</summary>
public sealed class ContentHasher
{
    public const string Prefix = "sha256:";
    private const int BlockBytes = 4 << 20;

    public ContentHasher(HashCache? cache = null) => Cache = cache;

    public HashCache? Cache { get; }

    /// <summary>The file's hash, from the cache when its size and last-write time match, else computed (and
    /// remembered).</summary>
    public async Task<string> HashAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var file = new FileInfo(full);
        if (!file.Exists)
            throw new FileNotFoundException("no such file", full);
        if (Cache is not null && Cache.TryGet(full, file.Length, file.LastWriteTimeUtc, out var cached))
        {
            progress?.Report(1);
            return cached;
        }
        var hash = await ComputeAsync(full, progress, cancellationToken).ConfigureAwait(false);
        if (Cache is not null)
        {
            file.Refresh();
            Cache.Put(full, file.Length, file.LastWriteTimeUtc, hash);
            await Cache.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        return hash;
    }

    /// <summary>Reads and hashes the whole file (no cache).</summary>
    public static async Task<string> ComputeAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var a = ArrayPool<byte>.Shared.Rent(BlockBytes);
        var b = ArrayPool<byte>.Shared.Rent(BlockBytes);
        try
        {
            long done = 0;
            var lastReport = -1.0;
            var read = await stream.ReadAsync(a.AsMemory(0, BlockBytes), cancellationToken).ConfigureAwait(false);
            while (read > 0)
            {
                // The next block is read while this one is hashed.
                var next = stream.ReadAsync(b.AsMemory(0, BlockBytes), cancellationToken);
                sha.AppendData(a, 0, read);
                done += read;
                if (progress is not null && length > 0)
                {
                    var fraction = (double)done / length;
                    if (fraction - lastReport >= 0.01 || done == length)
                    {
                        progress.Report(fraction);
                        lastReport = fraction;
                    }
                }
                read = await next.ConfigureAwait(false);
                (a, b) = (b, a);
            }
            if (length == 0)
                progress?.Report(1);
            return Prefix + Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>True for "sha256:" and 64 lowercase hex digits.</summary>
    public static bool IsValid(string? hash)
    {
        if (hash is null || hash.Length != Prefix.Length + 64 || !hash.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        foreach (var c in hash.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}

/// <summary>Remembered hashes keyed by (full path, size, last-write time), persisted to a small JSON file
/// (written whole to a temp file beside it and moved over it, so a crash never leaves half a file). A change to
/// any of the three is a different key: an edited file is hashed again. Thread-safe.</summary>
public sealed class HashCache : IDisposable
{
    private const int MaxEntries = 20_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(PathComparer);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private long _hits;
    private long _misses;

    /// <summary>A cache persisted to a file (loaded now if it exists), or kept in memory only when the file is
    /// null.</summary>
    public HashCache(string? file)
    {
        File = file;
        if (file is null || !System.IO.File.Exists(file))
            return;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(System.IO.File.ReadAllText(file), Json);
            if (doc?.Entries is null)
                return;
            foreach (var e in doc.Entries)
            {
                if (!string.IsNullOrEmpty(e.Path) && ContentHasher.IsValid(e.Hash))
                    _entries[e.Path] = e;
            }
        }
        catch (JsonException)
        {
            // A damaged cache is only lost time: it starts empty and is rewritten on the next hash.
            _entries.Clear();
        }
        catch (IOException)
        {
            _entries.Clear();
        }
    }

    public string? File { get; }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The default file: the user's local application data, PlayMap/hash-cache.json.</summary>
    public static string DefaultFile() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "PlayMap", "hash-cache.json");

    public bool TryGet(string fullPath, long size, DateTime lastWriteUtc, out string hash)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fullPath, out var e) && e.Size == size && e.LastWriteTicks == lastWriteUtc.Ticks)
            {
                Interlocked.Increment(ref _hits);
                hash = e.Hash;
                return true;
            }
        }
        Interlocked.Increment(ref _misses);
        hash = "";
        return false;
    }

    public void Put(string fullPath, long size, DateTime lastWriteUtc, string hash)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(fullPath))
            {
                // Full: the oldest-written files go first.
                foreach (var old in _entries.Values.OrderBy(v => v.LastWriteTicks).Take(MaxEntries / 10).ToList())
                    _entries.Remove(old.Path);
            }
            _entries[fullPath] = new Entry(fullPath, size, lastWriteUtc.Ticks, hash);
        }
    }

    /// <summary>Writes the cache to its file (no-op for an in-memory cache).</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (File is null)
            return;
        Document doc;
        lock (_gate)
            doc = new Document(1, [.. _entries.Values]);
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var temp = File + ".tmp";
            await System.IO.File.WriteAllTextAsync(temp, JsonSerializer.Serialize(doc, Json), cancellationToken).ConfigureAwait(false);
            System.IO.File.Move(temp, File, overwrite: true);
        }
        finally
        {
            _saving.Release();
        }
    }

    public void Dispose() => _saving.Dispose();

    private sealed record Entry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtime")] long LastWriteTicks,
        [property: JsonPropertyName("hash")] string Hash);

    private sealed record Document(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("entries")] List<Entry> Entries);
}
