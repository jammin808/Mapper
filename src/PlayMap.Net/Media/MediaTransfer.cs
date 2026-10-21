using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PlayMap.Core.Time;
using PlayMap.Core.Storage;

namespace PlayMap.Net.Media;

/// <summary>A token bucket for transfer bandwidth: bytes per second, lowered while a show plays so a pull never
/// starves playback's disk or network (0 = no limit). A take may run the bucket into debt by one read; the next
/// take waits the debt out, so the long-run rate is exact whatever the read size.</summary>
public sealed class TokenBucket(long bytesPerSecond, IMonoClock? clock = null)
{
    private readonly IMonoClock _clock = clock ?? MonoClock.Instance;
    private readonly object _gate = new();
    private long _rate = Math.Max(0, bytesPerSecond);
    private double _tokens;
    private long _lastNs = (clock ?? MonoClock.Instance).NowNs;

    public long BytesPerSecond => Interlocked.Read(ref _rate);

    /// <summary>A new rate, at once (a show starting lowers it; its end raises it).</summary>
    public void SetRate(long bytesPerSecond)
    {
        lock (_gate)
        {
            Refill();
            _rate = Math.Max(0, bytesPerSecond);
            _tokens = Math.Min(_tokens, Capacity);
        }
    }

    private double Capacity => Math.Max(64 * 1024, _rate / 4.0);

    private void Refill()
    {
        var now = _clock.NowNs;
        if (_rate > 0)
            _tokens = Math.Min(Capacity, _tokens + _rate * Durations.ToSeconds(now - _lastNs));
        _lastNs = now;
    }

    public async ValueTask TakeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                if (_rate == 0)
                    return;
                Refill();
                if (_tokens > 0)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds(Math.Max(0.001, -_tokens / _rate + 0.001));
            }
            await Task.Delay(wait < TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), ct);
        }
    }
}

public sealed record MediaTransferOptions
{
    /// <summary>The ranged chunk a pull asks for (8 MiB: big enough to keep a link busy, small enough to resume cheaply).</summary>
    public int ChunkBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Chunks in flight at once (2–4).</summary>
    public int Parallelism { get; init; } = 3;

    /// <summary>Tries per chunk before the pull gives up (its finished chunks stay for the resume).</summary>
    public int ChunkAttempts { get; init; } = 3;

    /// <summary>The peer's pairing token, sent in X-PlayMap-Token ("" = none). A pull that signs (<see cref="Sign"/>) sends none.</summary>
    public string Token { get; init; } = "";

    /// <summary>Where the peer serves a file by its hash: <c>/api/media/</c> behind its token gate, or a backup's
    /// <c>/mirror/media/</c>, which takes a proof instead.</summary>
    public string MediaPath { get; init; } = "/api/media/";

    /// <summary>Signs each request with the peer's pairing proof (fresh nonce, the request's method and path); null = unsigned. When
    /// set, the token itself is never sent.</summary>
    public Action<HttpRequestMessage>? Sign { get; init; }

    /// <summary>The longest a peer may send nothing (no answer to a request, no byte of a body it is sending) before the try is given
    /// up as stalled, with words that say so. The bandwidth bucket's waits and the disk's are not the peer's silence.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a pull says how far it is while its blocks land; every chunk's end and the verifying are said whatever this.</summary>
    public TimeSpan ProgressEvery { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>One file a peer offers: <c>GET /api/media</c>'s rows.</summary>
public sealed record MediaManifestEntry(string Hash, long Size, string Name);

/// <summary>Where a pull stands: its bytes coming from the peer, or every byte in and the whole file being hashed and put in place.</summary>
public enum MediaPullStage
{
    Receiving,
    Verifying,
}

/// <summary>How far a pull is: the bytes in place (whole chunks and the blocks of chunks still coming), its chunks, and its stage.</summary>
public sealed record MediaProgress(string Hash, long BytesDone, long Size, int ChunksDone, int Chunks, int ChunksResumed, MediaPullStage Stage = MediaPullStage.Receiving);

/// <summary>One try at a chunk that failed: what the peer did instead, in words, and which try of how many. The pull tries again
/// while tries are left; the last try's words are the failed pull's.</summary>
public sealed record MediaTrouble(string Hash, string Words, int Attempt, int Attempts);

public enum MediaPullOutcome
{
    /// <summary>Pulled, hashed, matched, in place.</summary>
    Verified,
    /// <summary>The file was here already, verified.</summary>
    AlreadyHere,
    /// <summary>Every chunk came but the whole file's hash is not the one asked for: nothing was kept.</summary>
    HashMismatch,
    /// <summary>A chunk could not be had: the finished chunks stay for the resume.</summary>
    Failed,
}

public sealed record MediaPullResult(MediaPullOutcome Outcome, string Words, long BytesFetched, int ChunksFetched, int ChunksResumed);

/// <summary>Content-addressed pulls between nodes (docs/ARCHITECTURE.md §7): a file is asked for by its SHA-256 from a
/// peer's <c>/api/media/&lt;hash&gt;</c> in ranged chunks, 2–4 in flight, through a bandwidth bucket. Each chunk's body is read and
/// written in blocks of <see cref="BlockBytes"/> (a pull's cost in thread-pool turns follows its blocks), and a peer that sends
/// nothing for <see cref="MediaTransferOptions.Quiet"/> ends that try with words that say so (<see cref="Trouble"/>); a chunk is
/// tried <see cref="MediaTransferOptions.ChunkAttempts"/> times. Chunks land in "&lt;file&gt;.part" at their offsets and a sidecar
/// bitmap ("&lt;file&gt;.part.map", rewritten atomically after each chunk) says which are done, so an interrupted pull resumes where
/// it stopped. The whole file is hashed at the end; only a match is renamed into place (atomically) — a mismatch keeps nothing.</summary>
public sealed class MediaTransfer(HttpClient http, TokenBucket bandwidth, MediaTransferOptions? options = null, ILogger? log = null, IMonoClock? clock = null)
{
    /// <summary>The most of a chunk's body read from the peer before it is written to the part file.</summary>
    public const int BlockBytes = 1024 * 1024;

    private const string MapMagic = "PMMAP1";
    private readonly MediaTransferOptions _o = options ?? new MediaTransferOptions();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly IMonoClock _clock = clock ?? MonoClock.Instance;

    public TokenBucket Bandwidth => bandwidth;

    /// <summary>Raised as a pull's blocks land (at most every <see cref="MediaTransferOptions.ProgressEvery"/>), after each chunk, and
    /// once every byte is in, before the whole file is hashed (on a worker; a handler keeps it short).</summary>
    public event Action<MediaProgress>? Progress;

    /// <summary>Raised for each try at a chunk that failed, with what the peer did instead (on a worker; a handler keeps it short).</summary>
    public event Action<MediaTrouble>? Trouble;

    /// <summary>The files a peer offers (<c>GET /api/media</c>); a peer that does not answer the whole list within
    /// <see cref="MediaTransferOptions.Quiet"/> is a <see cref="TimeoutException"/> that says so.</summary>
    public async Task<IReadOnlyList<MediaManifestEntry>> ManifestAsync(Uri peer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        string text;
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            quiet.CancelAfter(_o.Quiet);
            try
            {
                using var request = Request(new Uri(peer, "/api/media"));
                using var response = await http.SendAsync(request, quiet.Token);
                response.EnsureSuccessStatusCode();
                text = await response.Content.ReadAsStringAsync(quiet.Token);
            }
            catch (OperationCanceledException) when (quiet.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{peer.Authority} did not answer its media list in {Seconds(_o.Quiet)}");
            }
        }
        using var doc = JsonDocument.Parse(text);
        var list = new List<MediaManifestEntry>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var hash = Normal(JsonText.Str(e, "hash"));
            var size = JsonText.Long(e, "size", -1);
            if (hash.Length == 64 && size >= 0)
                list.Add(new MediaManifestEntry(hash, size, JsonText.Str(e, "name")));
        }
        return list;
    }

    /// <summary>Pulls one file into <paramref name="destination"/>. Cancelling leaves the finished chunks for the next
    /// pull (the cancellation is rethrown).</summary>
    public async Task<MediaPullResult> PullAsync(Uri peer, string hash, long size, string destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(destination);
        hash = Normal(hash);
        if (hash.Length != 64)
            throw new ArgumentException("a media hash is 64 hex characters of SHA-256", nameof(hash));
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (File.Exists(destination) && new FileInfo(destination).Length == size && await HashOfAsync(destination, ct) == hash)
            return new MediaPullResult(MediaPullOutcome.AlreadyHere, "already here, verified", 0, 0, 0);

        var part = destination + ".part";
        var mapPath = part + ".map";
        var chunkBytes = Math.Max(64 * 1024, _o.ChunkBytes);
        var chunks = (int)((size + chunkBytes - 1) / chunkBytes);
        var done = LoadMap(mapPath, part, hash, size, chunkBytes, chunks);
        var resumed = done.Count(d => d);
        // The bytes in place of each chunk still coming (a whole chunk counts once it is marked done).
        var coming = new long[chunks];
        var fetched = 0;
        long bytes = 0;
        var failure = "";
        var mapGate = new object();
        var sayAtNs = long.MinValue;
        long ChunkLength(int k) => Math.Min(chunkBytes, size - (long)k * chunkBytes);
        MediaProgress Now(MediaPullStage stage)
        {
            long landed = 0;
            for (var k = 0; k < chunks; k++)
                landed += done[k] ? ChunkLength(k) : coming[k];
            return new MediaProgress(hash, landed, size, done.Count(d => d), chunks, resumed, stage);
        }
        // A block of chunk i landed (or its try failed and it starts again from 0): said at most every ProgressEvery.
        void Landed(int i, long inPlace)
        {
            MediaProgress? say = null;
            lock (mapGate)
            {
                coming[i] = inPlace;
                var now = _clock.NowNs;
                if (inPlace > 0 && now >= sayAtNs)
                {
                    sayAtNs = now + _o.ProgressEvery.Ticks * 100;
                    say = Now(MediaPullStage.Receiving);
                }
            }
            if (say is not null)
                Raise(say);
        }
        using (var handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            if (RandomAccess.GetLength(handle) != size)
                RandomAccess.SetLength(handle, size);
            var next = -1;
            var url = new Uri(peer, _o.MediaPath + hash);
            async Task WorkerAsync()
            {
                while (true)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= chunks || Volatile.Read(ref failure).Length > 0)
                        return;
                    if (done[i])
                        continue;
                    var start = (long)i * chunkBytes;
                    var length = (int)ChunkLength(i);
                    var chunk = string.Create(CultureInfo.InvariantCulture, $"chunk {i + 1} of {chunks} from {peer.Authority}");
                    if (await ChunkAsync(url, handle, hash, chunk, start, length, size, inPlace => Landed(i, inPlace), ct) is { } why)
                    {
                        Interlocked.CompareExchange(ref failure, $"{chunk} could not be had: {why}", "");
                        return;
                    }
                    MediaProgress progress;
                    lock (mapGate)
                    {
                        done[i] = true;
                        coming[i] = 0;
                        fetched++;
                        bytes += length;
                        SaveMap(mapPath, hash, size, chunkBytes, done);
                        progress = Now(MediaPullStage.Receiving);
                        sayAtNs = _clock.NowNs + _o.ProgressEvery.Ticks * 100;
                    }
                    Raise(progress);
                }
            }
            var workers = Enumerable.Range(0, Math.Clamp(Math.Min(_o.Parallelism, Math.Max(1, chunks)), 1, 4)).Select(_ => Task.Run(WorkerAsync, CancellationToken.None)).ToList();
            await Task.WhenAll(workers);
            ct.ThrowIfCancellationRequested();
            if (failure.Length > 0)
                return new MediaPullResult(MediaPullOutcome.Failed, failure + " — the finished chunks stay for the resume", bytes, fetched, resumed);
        }

        Raise(new MediaProgress(hash, size, size, chunks, chunks, resumed, MediaPullStage.Verifying));
        if (await HashOfAsync(part, ct) != hash)
        {
            TryDelete(part);
            TryDelete(mapPath);
            _log.LogWarning("A pulled file's hash did not match what was asked: nothing was kept.");
            return new MediaPullResult(MediaPullOutcome.HashMismatch, "the file that came is not the one asked for (its SHA-256 differs) — nothing was kept", bytes, fetched, resumed);
        }
        DurableFile.MoveIntoPlace(part, destination);
        TryDelete(mapPath);
        return new MediaPullResult(MediaPullOutcome.Verified, "pulled and verified", bytes, fetched, resumed);
    }

    /// <summary>One chunk into its place, tried again on a transient failure: null once it is in place, else what the last try met.
    /// Each failed try is said (<see cref="Trouble"/>, "chunk 1 of 3 from &lt;address&gt;: sent nothing for 10 s, …") and starts the
    /// chunk again from its first byte.</summary>
    private async Task<string?> ChunkAsync(Uri url, SafeFileHandle handle, string hash, string chunk, long start, int length, long size, Action<long> landed, CancellationToken ct)
    {
        var attempts = Math.Max(1, _o.ChunkAttempts);
        var why = "";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (await FetchAsync(url, handle, start, length, size, landed, ct) is not { } met)
                    return null;
                why = met;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                why = ex.Message;
            }
            landed(0);
            _log.LogInformation("A media chunk's try failed ({Chunk}, try {Attempt} of {Attempts}): {Why}", chunk, attempt, attempts, why);
            RaiseTrouble(new MediaTrouble(hash, $"{chunk}: {why}", attempt, attempts));
            if (attempt < attempts && !await Waits.DelayAsync(TimeSpan.FromMilliseconds(250 * attempt), ct))
                break;
        }
        ct.ThrowIfCancellationRequested();
        return why;
    }

    /// <summary>One try at one chunk: null when every byte of it is in place, else what the peer did instead ("sent nothing for
    /// 10 s, …"). The peer's answer and each read of its body are given <see cref="MediaTransferOptions.Quiet"/>; the time the
    /// bandwidth bucket and the disk take is not the peer's silence. The body is read and written in blocks of <see cref="BlockBytes"/>;
    /// <paramref name="landed"/> hears the chunk's bytes in place after each block.</summary>
    private async Task<string?> FetchAsync(Uri url, SafeFileHandle handle, long start, int length, long size, Action<long> landed, CancellationToken ct)
    {
        using var request = Request(url);
        request.Headers.Range = new RangeHeaderValue(start, start + length - 1);
        HttpResponseMessage response;
        using (var answer = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            answer.CancelAfter(_o.Quiet);
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, answer.Token);
            }
            catch (OperationCanceledException) when (answer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return $"did not answer in {Seconds(_o.Quiet)}";
            }
        }
        using (response)
        {
            var whole = response.StatusCode == HttpStatusCode.OK && start == 0 && length == size;
            if (response.StatusCode != HttpStatusCode.PartialContent && !whole)
                return string.Create(CultureInfo.InvariantCulture, $"answered {(int)response.StatusCode}");
            if (!whole)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || range.From != start || range.To != start + length - 1)
                    return "answered a range other than the one asked for";   // which is not this chunk
            }
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var blockBytes = Math.Min(BlockBytes, length);
            var block = ArrayPool<byte>.Shared.Rent(blockBytes);
            try
            {
                var written = 0;   // in place in the part file
                var held = 0;      // read into the block, not written yet
                while (written + held < length)
                {
                    int n;
                    using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        silence.CancelAfter(_o.Quiet);
                        try
                        {
                            n = await body.ReadAsync(block.AsMemory(held, Math.Min(blockBytes - held, length - written - held)), silence.Token);
                        }
                        catch (OperationCanceledException) when (silence.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            return string.Create(CultureInfo.InvariantCulture,
                                $"sent nothing for {Seconds(_o.Quiet)}, {written + held} of {length} bytes in");
                        }
                    }
                    if (n == 0)
                        return string.Create(CultureInfo.InvariantCulture, $"ended the chunk at {written + held} of {length} bytes");
                    await bandwidth.TakeAsync(n, ct);
                    held += n;
                    if (held == blockBytes || written + held == length)
                    {
                        await RandomAccess.WriteAsync(handle, block.AsMemory(0, held), start + written, ct);
                        written += held;
                        held = 0;
                        landed(written);
                    }
                }
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        }
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private HttpRequestMessage Request(Uri url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (_o.Sign is { } sign)
            sign(r);
        else if (_o.Token.Length > 0)
            r.Headers.TryAddWithoutValidation("X-PlayMap-Token", _o.Token);
        return r;
    }

    private void Raise(MediaProgress progress)
    {
        try
        {
            Progress?.Invoke(progress);
        }
        catch (Exception ex)
        {
            // A progress listener that faults is the listener's: the pull goes on.
            _log.LogWarning(ex, "A media progress listener faulted.");
        }
    }

    private void RaiseTrouble(MediaTrouble trouble)
    {
        try
        {
            Trouble?.Invoke(trouble);
        }
        catch (Exception ex)
        {
            // A listener that faults is the listener's: the pull goes on.
            _log.LogWarning(ex, "A media trouble listener faulted.");
        }
    }

    /// <summary>The bitmap of finished chunks — only when the sidecar names this file, size and chunk size and the
    /// part file is whole-sized; anything else starts over (a resume never trusts a stranger's map).</summary>
    private static bool[] LoadMap(string mapPath, string part, string hash, long size, int chunkBytes, int chunks)
    {
        var done = new bool[chunks];
        try
        {
            if (!File.Exists(mapPath) || !File.Exists(part) || new FileInfo(part).Length != size)
                return done;
            var bytes = DurableFile.ReadAllBytes(mapPath);
            var header = Header(hash, size, chunkBytes);
            if (bytes.Length != header.Length + (chunks + 7) / 8 || !bytes.AsSpan(0, header.Length).SequenceEqual(header))
                return done;
            for (var i = 0; i < chunks; i++)
                done[i] = (bytes[header.Length + i / 8] & (1 << (i % 8))) != 0;
            return done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable map resumes nothing: every chunk is fetched again.
            return new bool[chunks];
        }
    }

    private static void SaveMap(string mapPath, string hash, long size, int chunkBytes, bool[] done)
    {
        var header = Header(hash, size, chunkBytes);
        var bytes = new byte[header.Length + (done.Length + 7) / 8];
        header.CopyTo(bytes, 0);
        for (var i = 0; i < done.Length; i++)
            if (done[i])
                bytes[header.Length + i / 8] |= (byte)(1 << (i % 8));
        var temp = mapPath + ".new";
        File.WriteAllBytes(temp, bytes);
        // Not flushed: the map is a hint for the resume. The chunks it names are not flushed either, and the whole file's hash is
        // checked before it is kept, so a map that outlived its chunks costs a pull again, never a wrong file.
        File.Move(temp, mapPath, overwrite: true);
    }

    private static byte[] Header(string hash, long size, int chunkBytes) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{MapMagic} {hash} {size} {chunkBytes}\n"));

    public static async Task<string> HashOfAsync(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    private static string Normal(string hash)
    {
        var h = (hash ?? "").Trim().ToLowerInvariant();
        if (h.StartsWith("sha256:", StringComparison.Ordinal))
            h = h[7..];
        return h.Length == 64 && h.All(Uri.IsHexDigit) ? h : "";
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sidecar that cannot go now is overwritten by the next pull's.
            _log.LogDebug(ex, "A transfer's sidecar could not be removed.");
        }
    }
}
