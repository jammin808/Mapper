using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Media;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Hap;
using PlayMap.Media.Sources;
using PlayMap.Net;

using LibraryImport = PlayMap.Media.MediaImport;

namespace PlayMap.Engine.Host;

/// <summary>The media edge over PlayMap.Media: imports hash and probe a file (the hash remembered per path, size and
/// write time in the node's folder, so a clip is read once per machine); sources open through MediaSources; readiness
/// is checked in the background and answered from what the last check found, so STATE never waits on a disk or a
/// decoder. Beside it, the node's hash index: every verified file in the node's media store, the project's folder and
/// the library's own paths, by SHA-256 — what PathForHash answers, what the web port serves to peers (Net's
/// <see cref="IMediaStore"/>) and what a fetched file lands in. Without FFmpeg the node still imports (hash, size,
/// a kind from the name) and says why nothing decodes; it never stops for it.
/// <para>HAP plays on the GPU: an item whose codec is one of the HAP family opens a <see cref="HapVideoSource"/> when the
/// renderer samples its block family (<see cref="UseRenderer"/>), else FFmpeg's CPU decoder for the five codecs it reads (with
/// the words why), and its readiness says when this node cannot show it at all (HAP R and HAP HDR without BPTC). Every clip
/// decoder opened here is listed with its facts (<see cref="OpenDecoders"/>) until it is disposed.</para></summary>
public sealed class MediaBackend : IMediaBackend, IMediaStore, IAsyncDisposable
{
    public const string HashCacheFile = "hash-cache.json";

    /// <summary>What readiness says of an item whose first check is still running.</summary>
    public const string Checking = "checking";

    /// <summary>How often the library's files are looked at again (present, changed) and the index revalidated.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(3);

    /// <summary>Files in the project folder the index hashes: pictures and sound, never a project or a sidecar.</summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".mxf", ".mpg", ".mpeg", ".ts", ".m2ts", ".wmv", ".flv", ".gif",
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr", ".dpx", ".webp", ".bmp", ".tga", ".jxl", ".hdr",
        ".wav", ".mp3", ".aac", ".flac", ".ogg", ".m4a", ".aif", ".aiff", ".opus",
    };

    private const int FolderDepth = 3;

    private readonly NodeHome _home;
    private readonly ILogger _log;
    private readonly HashCache _cache;
    private readonly ContentHasher _hasher;
    private readonly ConcurrentDictionary<string, Check> _checks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Indexed> _index = new(StringComparer.Ordinal);
    private readonly Channel<Func<CancellationToken, Task>> _work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private Task _sweeper = Task.CompletedTask;
    private volatile EngineCore? _engine;
    private volatile string? _indexedFolder;
    private int _reindexQueued;
    private int _saidFfmpeg;
    private volatile int _formats = (int)(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4);
    private volatile bool _rendererSaid;
    private readonly Lock _openGate = new();
    private readonly List<OpenDecoder> _open = [];

    public MediaBackend(NodeHome home, ILogger log)
    {
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _cache = new HashCache(Path.Combine(home.Folder, HashCacheFile));
        _hasher = new ContentHasher(_cache);
        Directory.CreateDirectory(home.MediaStore);
        _worker = Task.Run(() => WorkAsync(_stop.Token), CancellationToken.None);
        QueueReindex();
    }

    /// <summary>A check finished, the index moved or a file came or went: STATE's media facts may have changed.</summary>
    public event Action? Changed;

    /// <summary>Words worth the journal (FFmpeg missing, a file that will not decode), said once each.</summary>
    public event Action<string>? Said;

    /// <summary>Files the index holds verified now.</summary>
    public int IndexedFiles => _index.Count;

    /// <summary>Starts reading the library from a running engine: its project's folder, its items and whether a show
    /// plays (the project folder is not hashed while one does — the disk is the show's).</summary>
    public void Attach(EngineCore engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _sweeper = Task.Run(() => SweepAsync(_stop.Token), CancellationToken.None);
        QueueReindex();
    }

    /// <summary>Whether the FFmpeg libraries load, and the words when they do not.</summary>
    public static bool FfmpegReady(out string words)
    {
        if (FfmpegLoader.TryLoad(out var problem))
        {
            words = "";
            return true;
        }
        words = FfmpegWords(problem);
        return false;
    }

    /// <summary>The loader's reason in the operator's words: what is missing and every folder searched.</summary>
    public static string FfmpegWords(string loaderProblem) =>
        loaderProblem.Contains("were not found", StringComparison.Ordinal)
            ? "FFmpeg 9 libraries not found: searched " + string.Join(", ", FfmpegLoader.CandidateFolders())
              + $" (and their lib and bin folders) — set {FfmpegLoader.EnvironmentVariable} to the folder that holds them"
            : "FFmpeg 9 libraries could not be used: " + loaderProblem;

    public async Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var relay = progress is null ? null : new ImportRelay(progress);
        var found = await MediaLibrary.ImportAsync(location, _hasher, relay, ct);
        if (found.Problem == MediaProblem.LibrariesMissing)
            SayFfmpeg(found.Message);
        else if (!found.Ok)
            Said?.Invoke($"{found.Name} imported, but it will not play here: {found.Message}");
        if (ContentHasher.IsValid(found.ContentHash) && File.Exists(found.Location))
            AddVerified(found.ContentHash, found.Location, Path.GetFileName(found.Location));
        Changed?.Invoke();
        return Adapt(found);
    }

    /// <summary>The engine's import facts from the library's (the problem rides in readiness and the journal).</summary>
    private static MediaImport Adapt(LibraryImport found) =>
        new(found.Kind, found.Info, found.ContentHash, found.SizeBytes, found.Name);

    public IVideoSource? Open(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is MediaKind.Audio or MediaKind.Web or MediaKind.Embed)
            return null;   // no FFmpeg picture: audio is heard, pages and embeds are another renderer's
        if (!FfmpegReady(out var words))
        {
            SayFfmpeg(words);
            return null;
        }
        var path = Resolve(item);
        if (path is null)
            return null;
        try
        {
            var located = item with { Location = path };
            var why = "";
            if (item.Kind == MediaKind.Video && HapRoute(item, out why) is { } hap)
                return Track(item, new HapVideoSource(path, hap, item.Id), "");
            var source = MediaSources.Open(located, null, MediaSourceOptions.Default);
            return source is FfmpegVideoSource clip ? Track(item, clip, why) : source;
        }
        catch (Exception ex) when (ex is NotSupportedException or FfmpegUnavailableException or IOException or ArgumentException)
        {
            _log.LogWarning(ex, "{Item} would not open.", item.Name);
            return null;
        }
    }

    /// <summary>The renderer's block families and upload arena (null when its context goes: then the families every desktop GL
    /// has are assumed until it says again). Readiness of HAP items is looked at again.</summary>
    public void UseRenderer(BlockFormats formats, IUploadArena? arena)
    {
        var known = formats != BlockFormats.None;
        _formats = (int)(known ? formats : BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4);
        _rendererSaid = known;
        foreach (var (id, check) in _checks)
            if (check.Key.Contains("|hap", StringComparison.Ordinal))
                _checks.TryRemove(new KeyValuePair<string, Check>(id, check));
        Changed?.Invoke();
    }

    /// <summary>The block families HAP sources open for (the renderer's, once it has said).</summary>
    public BlockFormats Formats => (BlockFormats)_formats;

    /// <summary>True once a render context has told this backend what it samples.</summary>
    public bool RendererKnown => _rendererSaid;

    /// <summary>The clip decoders open now, oldest first, with the item each plays and why it is not on a better path.</summary>
    public IReadOnlyList<OpenDecoder> OpenDecoders()
    {
        lock (_openGate)
        {
            _open.RemoveAll(d => d.Source is FfmpegVideoSource { IsDisposed: true } or HapVideoSource { IsDisposed: true });
            return [.. _open];
        }
    }

    private IVideoSource Track(MediaItem item, IVideoSource source, string why)
    {
        lock (_openGate)
        {
            _open.RemoveAll(d => d.Source is FfmpegVideoSource { IsDisposed: true } or HapVideoSource { IsDisposed: true });
            _open.Add(new OpenDecoder(item.Id, item.Name, item.Info.VideoCodec, source, why));
        }
        return source;
    }

    // HAP options for an item this node plays on the GPU, or null with the words why FFmpeg decodes it instead (or "").
    private HapSourceOptions? HapRoute(MediaItem item, out string why)
    {
        why = "";
        var generic = string.Equals(item.Info.VideoCodec, "hap", StringComparison.Ordinal);
        if (!HapCodecs.TryFromName(item.Info.VideoCodec, out var codec) && !generic)
            return null;
        var settings = _engine?.Services.Settings.Decode ?? DecodeSettings.Default;
        var formats = Formats;
        var missing = generic ? "" : HapCodecs.Missing(codec, formats);
        var ffmpegCan = generic || HapCodecs.FfmpegDecodes(codec);
        if (ffmpegCan && (missing.Length > 0 || string.Equals(settings.Hap, "ffmpeg", StringComparison.OrdinalIgnoreCase)))
        {
            why = missing.Length > 0 ? HapVideoSource.NotOnThisGpu(codec, missing) : "decode.hap is ffmpeg: FFmpeg decodes HAP on the CPU";
            return null;
        }
        // HAP R and HAP HDR have no CPU decoder: the source opens and refuses with the words when the GPU lacks BPTC.
        return new HapSourceOptions
        {
            Formats = formats,
            QueueFrames = Math.Clamp(settings.HapQueueFrames, 2, 64),
            ReadAheadMs = Math.Clamp(settings.HapReadAheadMs, 1, 10_000),
            ReadAheadMb = Math.Clamp(settings.HapReadAheadMb, 1, 4096),
            Workers = Math.Max(0, settings.HapWorkers),
            // A library item shown directly loops on the leader's clock: the decoder reads across the loop point.
            Loop = true,
            Color = item.Color,
        };
    }

    /// <summary>The words that say this node cannot show a HAP item at all (HAP R or HAP HDR without BPTC), or null.</summary>
    public string? HapRefusal(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!HapCodecs.TryFromName(item.Info.VideoCodec, out var codec) || HapCodecs.FfmpegDecodes(codec))
            return null;
        var missing = HapCodecs.Missing(codec, Formats);
        return missing.Length > 0 ? HapVideoSource.NotOnThisGpu(codec, missing) : null;
    }

    /// <summary>What the last check found for this item, or — the first time it is asked, or when the item changed — what
    /// a look at the disk says now ("checking"), with the full check queued.</summary>
    public MediaReadiness Readiness(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsFile)
            return new MediaReadiness(true, true, item.Kind == MediaKind.Stream, false, "a URL — read from the network when it plays");
        var key = Key(item);
        if (_checks.TryGetValue(item.Id, out var known) && known.Key == key)
            return known.Readiness;
        var path = Resolve(item);
        var interim = new MediaReadiness(path is not null, false, false, false, path is null ? "missing: " + item.Location : Checking);
        _checks[item.Id] = new Check(key, interim, path, null, Pending: true);
        Queue(ct => CheckAsync(item, key, ct));
        return interim;
    }

    public string? PathForHash(string contentHash) => Lookup(contentHash)?.Path;

    /// <summary>The verified file with this hash ("sha256:…" or the bare hex), or null. A file that moved, changed or
    /// went since it was indexed is dropped from the index, never served.</summary>
    public MediaFile? Lookup(string sha256)
    {
        var hex = Hex(sha256);
        if (hex.Length == 0 || !_index.TryGetValue(hex, out var entry))
            return null;
        if (!Still(entry.Path, entry.Stat))
        {
            _index.TryRemove(new KeyValuePair<string, Indexed>(hex, entry));
            return null;
        }
        return new MediaFile(hex, entry.Path, entry.Stat.Length, entry.Name);
    }

    public IReadOnlyList<MediaFile> Manifest() =>
        _index.Keys.Select(Lookup).OfType<MediaFile>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Where a fetched file with this hash lands: the node's media store, named by its hash and keeping the
    /// extension a person knows it by.</summary>
    public string StorePath(string sha256, string name)
    {
        var ext = Path.GetExtension(name);
        if (ext.Length is 0 or > 8 || ext.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.'))
            ext = ".bin";
        return Path.Combine(_home.MediaStore, Hex(sha256) + ext.ToLowerInvariant());
    }

    /// <summary>A file whose bytes were checked against the hash (a verified pull, an import): indexed at once and
    /// its hash remembered for the path, so it is never read again to learn what it is.</summary>
    public void AddVerified(string sha256, string path, string name)
    {
        var hex = Hex(sha256);
        var full = Path.GetFullPath(path);
        if (hex.Length == 0 || Stat(full) is not { } stat)
            return;
        _index[hex] = new Indexed(full, stat, name);
        _cache.Put(full, stat.Length, stat.WrittenUtc, ContentHasher.Prefix + hex);
        Queue(ct => _cache.SaveAsync(ct));
        // Items that were missing are looked at again by their next reader: this file may be the one they name.
        foreach (var (id, check) in _checks)
            if (check.Path is null)
                _checks.TryRemove(new KeyValuePair<string, Check>(id, check));
        Changed?.Invoke();
    }

    private static string Key(MediaItem item) => item.Kind + "|" + item.ContentHash + "|" + item.Location + "|" + item.Info.VideoCodec;

    /// <summary>The bytes this item names on this machine: the indexed file with its hash (verified bytes), else the
    /// item's own location when something is there; null when neither.</summary>
    private string? Resolve(MediaItem item)
    {
        if (!item.IsFile)
            return item.Location;
        if (ContentHasher.IsValid(item.ContentHash) && Lookup(item.ContentHash) is { } indexed)
            return indexed.Path;
        var full = MediaLibrary.ResolvePath(item, ProjectFolder());
        try
        {
            if (item.Kind == MediaKind.ImageSequence)
                return ImageSequence.Resolve(full).Count > 0 ? full : null;
            return File.Exists(full) ? Path.GetFullPath(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string? ProjectFolder() =>
        _engine?.ProjectPath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;

    private async Task CheckAsync(MediaItem item, string key, CancellationToken ct)
    {
        var path = Resolve(item);
        MediaReadiness readiness;
        FileStat? stat = null;
        if (path is null)
        {
            readiness = new MediaReadiness(false, false, false, false, "missing: " + item.Location);
        }
        else
        {
            stat = Stat(path);
            var found = await MediaLibrary.CheckAsync(item with { Location = path }, null, _hasher, null, 0, ct);
            var words = found.Problem;
            if (!found.Decodable && !FfmpegReady(out var ffmpeg))
            {
                words = ffmpeg;
                SayFfmpeg(ffmpeg);
            }
            var decodable = found.Decodable;
            var firstFrame = found.FirstFrameReady;
            if (HapRefusal(item) is { } refusal)
            {
                // The file decodes, but this node's GPU cannot sample its blocks and no CPU decoder exists for them.
                decodable = false;
                firstFrame = false;
                words = refusal;
            }
            readiness = new MediaReadiness(found.Present, found.HashVerified, decodable, firstFrame, words.Length == 0 ? "ready" : words);
            if (found.HashVerified && stat is not null)
                _index[Hex(item.ContentHash)] = new Indexed(path, stat.Value, Path.GetFileName(path));
        }
        if (_checks.TryGetValue(item.Id, out var current) && current.Key == key)
            _checks[item.Id] = new Check(key, readiness, path, stat, Pending: false);
        Changed?.Invoke();
    }

    /// <summary>Every few seconds: a checked file that changed or went is checked again, a missing one that appeared
    /// (a fetch landed, a drive came back) too; items no longer in the project are forgotten; the index drops what is
    /// gone and is rebuilt when the project's folder moved.</summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(SweepEvery, ct))
        {
            try
            {
                Sweep();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A sweep that faults (a file system that threw something new) is that sweep's; the next one runs.
                _log.LogWarning(ex, "The media sweep faulted.");
            }
        }
    }

    private void Sweep()
    {
        if (_engine is not { } engine)
            return;
        var moved = false;
        var media = engine.Store.Project.Media;
        var ids = media.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _checks.Keys.Where(id => !ids.Contains(id)).ToList())
            moved |= _checks.TryRemove(id, out _);
        foreach (var item in media.Where(m => m.IsFile))
        {
            if (!_checks.TryGetValue(item.Id, out var check) || check.Pending || check.Key != Key(item))
                continue;
            var stale = check.Path is null ? Resolve(item) is not null
                : check.Stat is { } s ? !Still(check.Path, s) : item.Kind != MediaKind.ImageSequence && !File.Exists(check.Path);
            if (stale)
            {
                // Forgotten: the next reader queues the full check and says "checking" until it is done.
                moved |= _checks.TryRemove(new KeyValuePair<string, Check>(item.Id, check));
            }
        }
        foreach (var (hex, entry) in _index.ToList())
            if (!Still(entry.Path, entry.Stat))
                moved |= _index.TryRemove(new KeyValuePair<string, Indexed>(hex, entry));
        if (!string.Equals(ProjectFolder(), _indexedFolder, StringComparison.Ordinal))
            QueueReindex();
        if (moved)
            Changed?.Invoke();
    }

    private void QueueReindex()
    {
        if (Interlocked.Exchange(ref _reindexQueued, 1) == 0)
            Queue(ReindexAsync);
    }

    /// <summary>Hashes (once per file, through the cache) what the node's store and the project's folder hold. The
    /// project's folder waits while a show plays; what is left is taken at the next sweep.</summary>
    private async Task ReindexAsync(CancellationToken ct)
    {
        Volatile.Write(ref _reindexQueued, 0);
        var folder = ProjectFolder();
        var added = await IndexFolderAsync(_home.MediaStore, 0, ct);
        if (folder is not null)
        {
            if (_engine?.IsAnimating == true)
                return;   // the folder stays un-indexed; the next sweep sees it has not been and asks again
            added += await IndexFolderAsync(folder, FolderDepth, ct);
        }
        _indexedFolder = folder;
        if (added > 0)
        {
            await _cache.SaveAsync(ct);
            Changed?.Invoke();
        }
    }

    private async Task<int> IndexFolderAsync(string folder, int depth, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            return 0;
        var added = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = depth > 0,
                MaxRecursionDepth = depth,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Where(f => MediaExtensions.Contains(Path.GetExtension(f)) || string.Equals(Path.GetDirectoryName(f), _home.MediaStore, StringComparison.Ordinal)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "The folder {Folder} could not be listed for the media index.", folder);
            return 0;
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(".part", StringComparison.Ordinal) || file.EndsWith(".map", StringComparison.Ordinal) || file.EndsWith(".new", StringComparison.Ordinal))
                continue;   // a pull in progress: indexed when it is verified, never before
            if (Stat(file) is not { } stat || _index.Values.Any(e => e.Path == file && e.Stat == stat))
                continue;
            try
            {
                var hash = await _hasher.HashAsync(file, null, ct);
                _index[Hex(hash)] = new Indexed(file, stat, Path.GetFileName(file));
                added++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "{File} could not be hashed for the media index.", file);
            }
        }
        return added;
    }

    private void Queue(Func<CancellationToken, Task> work) => _work.Writer.TryWrite(work);

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await work(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One check that faults is that item's: logged, and the next reader of it asks again.
                    _log.LogWarning(ex, "A media check faulted.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with work still queued: the node is going.
        }
    }

    private void SayFfmpeg(string words)
    {
        if (Interlocked.Exchange(ref _saidFfmpeg, 1) == 0)
        {
            _log.LogWarning("{Words}", words);
            Said?.Invoke(words);
        }
    }

    /// <summary>The bare lowercase hex of a "sha256:…" (or bare) hash; "" when it is not one.</summary>
    internal static string Hex(string? hash)
    {
        var h = (hash ?? "").Trim().ToLowerInvariant();
        if (h.StartsWith(ContentHasher.Prefix, StringComparison.Ordinal))
            h = h[ContentHasher.Prefix.Length..];
        return h.Length == 64 && h.All(Uri.IsHexDigit) ? h : "";
    }

    private static FileStat? Stat(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileStat(fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Still(string path, FileStat stat) => Stat(path) == stat;

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_worker, _sweeper);
        }
        catch (OperationCanceledException)
        {
            // The loops end on the stop they were given.
        }
        _stop.Dispose();
        _cache.Dispose();
    }

    private readonly record struct FileStat(long Length, DateTime WrittenUtc);

    /// <summary>A clip decoder this backend opened: the item it plays, its codec, the source, and why it is not on a better path.</summary>
    public sealed record OpenDecoder(string ItemId, string Name, string Codec, IVideoSource Source, string Why);

    private sealed record Check(string Key, MediaReadiness Readiness, string? Path, FileStat? Stat, bool Pending);

    private sealed record Indexed(string Path, FileStat Stat, string Name);

    private sealed class ImportRelay(IProgress<double> to) : IProgress<ImportProgress>
    {
        // The hash is the long part of an import: its fraction is the import's.
        public void Report(ImportProgress value) => to.Report(value.Stage == "hash" ? value.Fraction : value.Stage == "done" ? 1 : 0);
    }
}
