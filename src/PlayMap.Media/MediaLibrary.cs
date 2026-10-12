using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Sources;

namespace PlayMap.Media;

/// <summary>Where an import is: "probe", then "hash" (with the fraction of the bytes read), then "done".</summary>
public readonly record struct ImportProgress(string Stage, double Fraction);

/// <summary>What importing a path found: enough to make the library item, and the problem in words when the
/// file cannot be played here.</summary>
public sealed record MediaImport(
    string Location,
    string Name,
    MediaKind Kind,
    MediaInfo Info,
    string ContentHash,
    long SizeBytes,
    bool Decodable,
    MediaProblem Problem,
    string Message)
{
    public bool Ok => Problem == MediaProblem.None;

    /// <summary>The library item for this import.</summary>
    public MediaItem ToItem(string id, ImmutableArray<string> tags = default) =>
        new(id, Name, Kind, Location, ContentHash, SizeBytes, Info, tags.IsDefault ? [] : tags);
}

/// <summary>A node's readiness for one item (Show Bus §7): the file is here, its bytes are the hash the
/// project names, a decoder made a picture of it, and the first frame is ready (a loaded source has the cue
/// frame decoded; without one, the probe decoded the first frame). <see cref="Problem"/> names the first thing
/// missing.</summary>
public sealed record MediaReadiness(string ItemId, bool Present, bool HashVerified, bool Decodable, bool FirstFrameReady, string Problem)
{
    public bool Ready => Present && HashVerified && Decodable && FirstFrameReady;
}

/// <summary>The library's file work without the engine: import a path (kind, facts, hash, with progress) and
/// report an item's readiness. An image sequence's hash covers its frames in order (each frame's name and
/// hash), so the same sequence in another folder is the same media.</summary>
public static class MediaLibrary
{
    /// <summary>Probes and hashes a path. A file that cannot be played still imports (its problem says why); a
    /// path that is not there throws <see cref="FileNotFoundException"/>.</summary>
    public static async Task<MediaImport> ImportAsync(string path, ContentHasher hasher, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        var full = Path.GetFullPath(path);
        var sequence = ImageSequence.IsSequenceLocation(full);
        if (!sequence && !File.Exists(full))
            throw new FileNotFoundException("no such file", full);
        progress?.Report(new ImportProgress("probe", 0));
        var probe = await MediaProbe.ProbeAsync(full, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ImportProgress("probe", 1));
        var hashProgress = progress is null ? null : new Relay(f => progress.Report(new ImportProgress("hash", f)));
        string hash;
        long size;
        if (sequence)
            (hash, size) = await HashSequenceAsync(full, hasher, hashProgress, cancellationToken).ConfigureAwait(false);
        else
        {
            hash = await hasher.HashAsync(full, hashProgress, cancellationToken).ConfigureAwait(false);
            size = new FileInfo(full).Length;
        }
        progress?.Report(new ImportProgress("done", 1));
        return new MediaImport(full, NameOf(full, sequence), probe.Kind, probe.Info, hash, size, probe.Decodable, probe.Problem, probe.Message);
    }

    /// <summary>An item's path on this machine: absolute as given, or relative to the project folder.</summary>
    public static string ResolvePath(MediaItem item, string? projectFolder)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Path.IsPathRooted(item.Location) || string.IsNullOrEmpty(projectFolder))
            return item.Location;
        return Path.GetFullPath(Path.Combine(projectFolder, item.Location));
    }

    /// <summary>Checks an item's readiness. The hash is verified through the hasher (its cache spares a re-read
    /// of an unchanged file). With a loaded source, "first frame ready" is that source's preroll at
    /// <paramref name="cueNs"/>.</summary>
    public static async Task<MediaReadiness> CheckAsync(MediaItem item, string? projectFolder, ContentHasher hasher,
        IVideoSource? loaded = null, long cueNs = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(hasher);
        if (!item.IsFile)
        {
            // Pages, embeds and streams have no bytes to verify; only a loaded source can say it has a picture.
            var live = loaded?.IsReadyAt(cueNs) ?? false;
            return new MediaReadiness(item.Id, true, true, true, live, live ? "" : "not loaded");
        }
        var path = ResolvePath(item, projectFolder);
        var sequence = item.Kind == MediaKind.ImageSequence;
        var present = sequence ? ImageSequence.Resolve(path).Count > 0 : File.Exists(path);
        if (!present)
            return new MediaReadiness(item.Id, false, false, false, false, "missing: " + path);

        bool verified;
        string problem = "";
        if (!ContentHasher.IsValid(item.ContentHash))
        {
            verified = false;
            problem = "not hashed";
        }
        else
        {
            var hash = sequence
                ? (await HashSequenceAsync(path, hasher, null, cancellationToken).ConfigureAwait(false)).Hash
                : await hasher.HashAsync(path, null, cancellationToken).ConfigureAwait(false);
            verified = string.Equals(hash, item.ContentHash, StringComparison.Ordinal);
            if (!verified)
                problem = "the file's bytes are not the ones the project names";
        }

        var probe = await MediaProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!probe.Decodable && problem.Length == 0)
            problem = probe.Message;
        var firstFrame = loaded is not null ? loaded.IsReadyAt(cueNs) : probe.Decodable && item.Kind != MediaKind.Audio;
        if (item.Kind == MediaKind.Audio)
            firstFrame = probe.Decodable;
        if (!firstFrame && problem.Length == 0)
            problem = "first frame not decoded yet";
        return new MediaReadiness(item.Id, true, verified, probe.Decodable, firstFrame, problem);
    }

    private static string NameOf(string full, bool sequence)
    {
        if (!sequence)
            return Path.GetFileNameWithoutExtension(full);
        if (Directory.Exists(full))
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        var name = Path.GetFileNameWithoutExtension(full);
        var cut = name.IndexOfAny(['%', '#']);
        var stem = (cut > 0 ? name[..cut] : name).TrimEnd('_', '-', '.', ' ');
        return stem.Length > 0 ? stem : Path.GetFileName(Path.GetDirectoryName(full)) ?? name;
    }

    // A sequence's identity: SHA-256 over "<file name> <file hash>\n" for each frame in order.
    private static async Task<(string Hash, long Size)> HashSequenceAsync(string location, ContentHasher hasher, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var files = ImageSequence.Resolve(location);
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var h = await hasher.HashAsync(files[i], null, cancellationToken).ConfigureAwait(false);
            manifest.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(files[i]) + " " + h + "\n"));
            size += new FileInfo(files[i]).Length;
            progress?.Report((i + 1) / (double)files.Count);
        }
        return (ContentHasher.Prefix + Convert.ToHexStringLower(manifest.GetHashAndReset()), size);
    }

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>Options for <see cref="MediaSources.Open"/>.</summary>
public sealed record MediaSourceOptions
{
    public VideoSourceOptions Video { get; init; } = VideoSourceOptions.Default;
    public ImageSequenceOptions Sequence { get; init; } = ImageSequenceOptions.Default;

    /// <summary>The rate a sequence plays at when its item does not say (its MediaInfo.FrameRate).</summary>
    public FrameRate SequenceRate { get; init; } = MediaProbe.DefaultSequenceRate;

    public static readonly MediaSourceOptions Default = new();
}

/// <summary>Opens the picture source for a library item: a clip, a still, a sequence (at the item's frame rate),
/// or a network stream (read as a live source, newest frame wins). Audio, pages and embeds have no FFmpeg
/// picture source.</summary>
public static class MediaSources
{
    public static IVideoSource Open(MediaItem item, string? projectFolder = null, MediaSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        options ??= MediaSourceOptions.Default;
        var path = MediaLibrary.ResolvePath(item, projectFolder);
        return item.Kind switch
        {
            MediaKind.Video => new FfmpegVideoSource(path, options.Video, item.Id),
            MediaKind.Image => new ImageSource(path, item.Id),
            MediaKind.ImageSequence => new ImageSequenceSource(path,
                FrameRate.TryParse(item.Info.FrameRate, out var rate) && rate.IsValid ? rate : options.SequenceRate, options.Sequence, item.Id),
            MediaKind.Stream => new CaptureSource(new CaptureOptions { Format = "", Device = item.Location }, item.Id),
            _ => throw new NotSupportedException($"a {item.Kind} item has no picture source here"),
        };
    }
}
