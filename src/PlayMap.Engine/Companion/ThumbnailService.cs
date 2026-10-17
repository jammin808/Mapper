using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Model;
using PlayMap.Net;
using PlayMap.Net.Companion;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Companion;

/// <summary>THUMB's pictures, cached by content and named by an ETag that moves exactly when the picture would:
/// <c>media:</c> by the file's content hash (a poster from <see cref="IPosterStore"/>, else one frame decoded at a tenth of the clip,
/// at most 3 s in, kept in memory and under the node's folder so a restart never decodes it again); <c>generator:</c> by its kind and
/// parameters (a flat field and a colour cycle drawn as their colour, any other kind as a named tile); <c>color:#RRGGBB</c>; and
/// <c>playlist:</c> as its current item's source. While the show is armed only cached posters are served: nothing is decoded on the
/// air's machine. At most two posters are made at once.</summary>
public sealed class ThumbnailService(EngineCore engine, string? folder, ILogger? log = null)
{
    public const int DefaultSize = 96;
    public const int MaxSize = 512;
    public const int MemoryEntries = 256;

    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(2);

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>Posters being made now (at most <see cref="MaxMakers"/>; a third waits its turn).</summary>
    private int _making;

    public const int MaxMakers = 2;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (byte[] Bytes, int W, int H, string Mime, long Used)> _memory = new(StringComparer.Ordinal);
    private long _use;

    /// <summary>What a reference stands for now: its ETag at a size and format, or why there is no picture of it.</summary>
    public (string? Etag, string? Why) EtagOf(string reference, int w, int h, Net.Companion.ThumbFormat format)
    {
        var target = Resolve(reference, out var why);
        return target is null ? (null, why) : (target.Etag(w, h, format), null);
    }

    public async ValueTask<ThumbResult> ThumbAsync(ThumbRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = Resolve(request.Ref, out var why);
        if (target is null)
            return ThumbResult.Refused(why!);
        var w = Math.Clamp(request.Width, 8, MaxSize);
        var h = Math.Clamp(request.Height, 8, MaxSize);
        var etag = target.Etag(w, h, request.Format);
        if (request.IfNoneMatch is { } held && string.Equals(held, etag, StringComparison.Ordinal))
            return new ThumbResult(true, etag, null, 0, 0, ReadOnlyMemory<byte>.Empty, true, null);
        if (FromMemory(etag) is { } hit)
            return new ThumbResult(true, etag, hit.Mime, hit.W, hit.H, hit.Bytes, false, null);
        switch (target)
        {
            case Drawn drawn:
            {
                var bytes = drawn.Draw(w, h);
                Remember(etag, bytes, w, h, "image/png");
                return new ThumbResult(true, etag, "image/png", w, h, bytes, false, null);
            }
            case MediaTarget media:
                return await MediaAsync(media, etag, w, h, request.Format, ct);
            default:
                return ThumbResult.Refused("no picture of it");
        }
    }

    private async ValueTask<ThumbResult> MediaAsync(MediaTarget media, string etag, int w, int h, Net.Companion.ThumbFormat format, CancellationToken ct)
    {
        var jpeg = format == Net.Companion.ThumbFormat.Jpeg;
        var mime = jpeg ? "image/jpeg" : "image/png";
        var path = DiskPath(media.Hash16, w, h, jpeg);
        if (path is not null && File.Exists(path))
        {
            var disk = await DurableFile.ReadAllBytesAsync(path, ct);
            var size = SizeOf(disk, w, h);
            Remember(etag, disk, size.W, size.H, mime);
            return new ThumbResult(true, etag, mime, size.W, size.H, disk, false, null);
        }
        if (jpeg && engine.Services.Posters is { } store && media.Item.ContentHash.Length > 0
            && store.TryGet(media.Item.ContentHash, w, h, out var poster, out var pw, out var ph))
        {
            var bytes = poster.ToArray();
            Remember(etag, bytes, pw, ph, mime);
            return new ThumbResult(true, etag, mime, pw, ph, bytes, false, null);
        }
        if (engine.Services.ShowGate.Armed)
            return ThumbResult.Refused("no picture of it yet — posters are made in rehearsal or on import");
        if (engine.Services.Media is not { } backend)
            return ThumbResult.Refused("this node decodes no media");
        while (Interlocked.Increment(ref _making) > MaxMakers)
        {
            Interlocked.Decrement(ref _making);
            if (!await Waits.DelayAsync(TimeSpan.FromMilliseconds(20), ct))
                ct.ThrowIfCancellationRequested();
        }
        try
        {
            var made = await Task.Run(() => Decode(backend, media.Item, w, h, jpeg, ct), ct);
            if (made.Why is not null)
                return ThumbResult.Refused(made.Why);
            Remember(etag, made.Bytes!, made.W, made.H, mime);
            if (path is not null)
                Save(path, made.Bytes!);
            return new ThumbResult(true, etag, mime, made.W, made.H, made.Bytes!, false, null);
        }
        finally
        {
            Interlocked.Decrement(ref _making);
        }
    }

    private static async Task<(byte[]? Bytes, int W, int H, string? Why)> Decode(IMediaBackend backend, MediaItem item, int w, int h, bool jpeg, CancellationToken ct)
    {
        using var source = backend.Open(item);
        if (source is null)
            return (null, 0, 0, $"{item.Name} does not open on this node");
        var duration = source.Info.DurationNs > 0 ? source.Info.DurationNs : item.Info.DurationNs;
        var at = Math.Min(duration > 0 ? duration / 10 : 0, 3_000_000_000L);
        source.Prepare(at, playing: false, rate: 1);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!source.IsReadyAt(at) && waited.Elapsed < ReadyWait)
        {
            if (!await Waits.DelayAsync(TimeSpan.FromMilliseconds(10), ct))
                ct.ThrowIfCancellationRequested();
        }
        using var frame = source.Acquire(at);
        if (frame is null)
            return (null, 0, 0, $"{item.Name} gave no picture within {ReadyWait.TotalSeconds:0} s");
        if (!Posters.CanRead(frame.Layout))
            return (null, 0, 0, $"{item.Name}'s pictures ({frame.Layout}) make no poster on this node yet");
        var (fw, fh) = Posters.Fit(frame.Width, frame.Height, w, h);
        // The picture's conversion and its encode are CPU work: on the compute threads, never the pool the sockets answer on.
        var bytes = await Core.Threading.Compute.RunAsync(() => Posters.Encode(Posters.ToBgra(frame, fw, fh), fw, fh, jpeg), ct).ConfigureAwait(false);
        return (bytes, fw, fh, null);
    }

    private string? DiskPath(string hash16, int w, int h, bool jpeg) =>
        folder is null ? null : Path.Combine(folder, hash16, string.Create(CultureInfo.InvariantCulture, $"{w}x{h}.{(jpeg ? "jpg" : "png")}"));

    private void Save(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            DurableFile.MoveIntoPlace(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The poster still serves from memory; only the restart's cache misses it.
            log?.LogDebug(ex, "A poster could not be kept on disk.");
        }
    }

    /// <summary>The size a stored picture was made at, read from its header (JPEG SOF or PNG IHDR); the box when unreadable.</summary>
    private static (int W, int H) SizeOf(byte[] bytes, int w, int h)
    {
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
        return codec is null ? (w, h) : (codec.Info.Width, codec.Info.Height);
    }

    private (byte[] Bytes, int W, int H, string Mime)? FromMemory(string etag)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(etag, out var e))
                return null;
            _memory[etag] = e with { Used = ++_use };
            return (e.Bytes, e.W, e.H, e.Mime);
        }
    }

    private void Remember(string etag, byte[] bytes, int w, int h, string mime)
    {
        lock (_gate)
        {
            _memory[etag] = (bytes, w, h, mime, ++_use);
            if (_memory.Count <= MemoryEntries)
                return;
            var oldest = _memory.MinBy(p => p.Value.Used).Key;
            _memory.Remove(oldest);
        }
    }

    /// <summary>What a reference names now, from published state only (a playlist's current item is its source).</summary>
    private ITarget? Resolve(string reference, out string? why)
    {
        why = null;
        if (reference.StartsWith("output:", StringComparison.OrdinalIgnoreCase) || reference.StartsWith("projector:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("camera:", StringComparison.OrdinalIgnoreCase))
        {
            why = Live(reference);
            return null;
        }
        if (!SourceRef.TryParse(reference, out var source) || source.IsNone)
        {
            why = Unknown(reference);
            return null;
        }
        var snapshot = engine.Snapshot;
        var project = snapshot.Project.Project;
        if (source.Kind == SourceKind.Playlist)
        {
            var list = project.Playlists.FirstOrDefault(p => p.Id == source.Id);
            if (list is null)
            {
                why = $"no playlist {source.Id}";
                return null;
            }
            if (!snapshot.Playback.Playlists.TryGetValue(list.Id, out var lane) || lane.IsIdle || lane.Source.IsNone)
            {
                why = $"{list.Name} is idle — no item on it";
                return null;
            }
            if (lane.Source.Kind == SourceKind.Playlist)
            {
                why = $"{list.Name}'s item is another playlist — ask THUMB of that playlist";
                return null;
            }
            source = lane.Source;
        }
        switch (source.Kind)
        {
            case SourceKind.Media:
            {
                var item = project.Media.FirstOrDefault(m => m.Id == source.Id);
                if (item is null)
                {
                    why = $"no media {source.Id}";
                    return null;
                }
                var hash = item.ContentHash.StartsWith("sha256:", StringComparison.Ordinal) ? item.ContentHash[7..] : item.ContentHash;
                // The hash names the poster's folder, so only hex is ever taken (a project file's own words never make a path).
                if (hash.Length < 16 || hash.AsSpan(0, 16).ContainsAnyExcept(HexDigits))
                {
                    why = $"{item.Name} is not hashed yet — its poster follows its hash";
                    return null;
                }
                if (item.Kind is not (MediaKind.Video or MediaKind.Image or MediaKind.ImageSequence))
                {
                    why = $"{item.Name} is {item.Kind.ToString().ToLowerInvariant()}: it has no picture";
                    return null;
                }
                return new MediaTarget(item, hash[..16].ToLowerInvariant());
            }
            case SourceKind.Generator:
            {
                var g = project.Generators.FirstOrDefault(x => x.Id == source.Id);
                if (g is null)
                {
                    why = $"no generator {source.Id}";
                    return null;
                }
                return GeneratorTarget(g);
            }
            case SourceKind.Color:
            {
                var (r, gr, b) = Posters.ParseHex(source.Id);
                var hex = string.Create(CultureInfo.InvariantCulture, $"{r:X2}{gr:X2}{b:X2}");
                return new Drawn("color-" + hex, (w, h) => Posters.Flat(hex, 1, w, h));
            }
            default:
                why = Live(reference);
                return null;
        }
    }

    private static string Live(string reference) =>
        $"{reference}: live pictures (outputs, projectors, canvases, inputs) are not served yet — THUMB takes media:, generator:, playlist: and color:";

    private static string Unknown(string reference) =>
        $"'{reference}' is not a picture reference — media:<id>, generator:<id>, playlist:<id> or color:#RRGGBB";

    private static Drawn GeneratorTarget(Generator g)
    {
        var parameters = string.Join(";", g.Params.Values.Select(p => p.Key + "=" + p.Value));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(g.Kind + "|" + g.Name + "|" + parameters)))[..16];
        return g.Kind switch
        {
            "pattern.flat" => new Drawn("generator-" + hash, (w, h) =>
                Posters.Flat(g.Params.Get("color") ?? "#FFFFFF", Math.Clamp(g.Params.GetDouble("level", 100), 0, 100) / 100, w, h)),
            "pattern.colorcycle" => new Drawn("generator-" + hash, (w, h) =>
                Posters.Flat((g.Params.Get("colors") ?? "#FF0000").Split(',')[0], 1, w, h)),
            _ => new Drawn("generator-" + hash, (w, h) => Posters.Tile(g.Kind, g.Name, w, h)),
        };
    }

    private interface ITarget
    {
        string Etag(int w, int h, Net.Companion.ThumbFormat format);
    }

    /// <summary>A file's picture: its ETag is its content hash, the box and the format.</summary>
    private sealed record MediaTarget(MediaItem Item, string Hash16) : ITarget
    {
        public string Etag(int w, int h, Net.Companion.ThumbFormat format) =>
            string.Create(CultureInfo.InvariantCulture, $"media-{Hash16}-{w}x{h}-{(format == Net.Companion.ThumbFormat.Jpeg ? 'j' : 'p')}");
    }

    /// <summary>A drawn picture (always PNG): its ETag is what it is drawn from and the box.</summary>
    private sealed record Drawn(string Key, Func<int, int, byte[]> Draw) : ITarget
    {
        public string Etag(int w, int h, Net.Companion.ThumbFormat format) =>
            string.Create(CultureInfo.InvariantCulture, $"{Key}-{w}x{h}-p");
    }
}
