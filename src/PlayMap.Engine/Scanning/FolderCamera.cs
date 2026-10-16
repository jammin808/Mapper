using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;
using PlayMap.Media;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Stills;

namespace PlayMap.Engine.Scanning;

/// <summary>How a folder camera watches its folder.</summary>
public sealed record FolderCameraOptions
{
    /// <summary>How often the folder is listed (a watcher misses events on network shares, so the list is the truth).</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a scan waits on one picture before it asks again (a person with a camera is slower than a phone).</summary>
    public TimeSpan Patience { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Told of each cue as the camera starts waiting for its picture (a tethering tool's trigger; tests shoot on it).</summary>
    public Action<PatternCue>? Cued { get; init; }

    /// <summary>Reads a still (null: FFmpeg's, <see cref="StillImageReader"/>).</summary>
    public Func<string, StillImage>? Read { get; init; }
}

/// <summary>
/// A folder as the scan's camera (a camera rig of kind Folder): a tethered camera or a person shooting into it. With
/// <see cref="FolderAdvance.NewFile"/> each image file that appears after a pattern goes up — a name the folder did not hold at the
/// cue, the first by name, its size unchanged for the settle time — is that pattern's picture; with <see cref="FolderAdvance.Manual"/> the node waits for
/// SCAN NEXT and takes the newest file since the cue. The tags apply to photos as to a phone's pictures: a photo whose tag reads
/// another pattern is refused with words ("photo 17 shows pattern 16: re-shoot") and the scan waits for the next. A 16-bit file
/// arrives as gray16 (its top 8 bits for the decode). The camera keeps the exposure it was set to: it runs the lock profile.
/// </summary>
public sealed class FolderCamera : ICuedScanCamera
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    private readonly CameraRig _rig;
    private readonly FolderCameraOptions _options;
    private readonly string _folder;
    private readonly FolderAdvance _advance;
    private readonly TimeSpan _settle;
    private readonly Func<string, StillImage> _read;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changed = new(0);
    private FileSystemWatcher? _watcher;
    private TaskCompletionSource? _next;
    private CellLocateResult? _located;
    private volatile string _problem = "";
    private long _generation;
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _armed;
    private volatile bool _disposed;

    public FolderCamera(CameraRig rig, FolderCameraOptions? options = null)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _options = options ?? new FolderCameraOptions();
        _folder = rig.Folder?.Path is { Length: > 0 } p ? p : rig.Source;
        _advance = rig.Folder?.Advance ?? FolderAdvance.NewFile;
        _settle = TimeSpan.FromMilliseconds(Math.Max(0, rig.Folder?.SettleMs ?? 300));
        _read = _options.Read ?? StillImageReader.Read;
    }

    public string Name => _rig.Name;

    /// <summary>The words a waiting scan shows (a refused photo, a manual camera waiting for SCAN NEXT).</summary>
    public Action<string>? Say { get; set; }

    public string Profile => "lock";

    public string Problem => _problem;

    public int Moves => 0;

    public TimeSpan? Patience => _options.Patience;

    public Task<bool> OpenAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
        {
            _problem = $"{_rig.Name}'s folder {_folder} does not exist";
            return Task.FromResult(false);
        }
        lock (_gate)
            _seen = Files();
        try
        {
            _watcher = new FileSystemWatcher(_folder) { EnableRaisingEvents = true, IncludeSubdirectories = false };
            _watcher.Created += (_, _) => Wake();
            _watcher.Changed += (_, _) => Wake();
            _watcher.Renamed += (_, _) => Wake();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // No watcher here: the poll alone finds new files.
            _watcher = null;
        }
        _problem = "";
        return Task.FromResult(true);
    }

    private void Wake()
    {
        if (_disposed)
            return;
        try
        {
            if (_changed.CurrentCount == 0)
                _changed.Release();
        }
        catch (ObjectDisposedException)
        {
            // The camera closed while the watcher still reported: nothing waits any more.
            _disposed = true;
        }
    }

    private HashSet<string> Files() =>
        new(Directory.EnumerateFiles(_folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant(), StringComparer.Ordinal)),
            StringComparer.Ordinal);

    /// <summary>The next new picture in the folder (a camera view or an align run reading frames), null within the timeout.</summary>
    public async Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            var (path, _) = await NewFileAsync(wait.Token).ConfigureAwait(false);
            return Frame(_read(path));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (MediaOpenException ex)
        {
            _problem = ex.Message;
            return null;
        }
    }

    public Task<CuedPrepared> PrepareAsync(CuedPrepare request, CancellationToken ct) =>
        Task.FromResult(new CuedPrepared("manual", "as set", "as set", false,
            "a folder camera keeps the exposure, focus and white balance it was set to — set them to manual before the scan"));

    public Task SetSitesAsync(string projectorId, CellLocateResult located, CancellationToken ct)
    {
        _located = located;
        return Task.CompletedTask;
    }

    public bool ByHand => _advance == FolderAdvance.Manual;

    public void Arm()
    {
        lock (_gate)
        {
            _seen = Files();
            _armed = true;
        }
    }

    /// <summary>SCAN NEXT for a manual folder camera: the picture for the pattern on the glass is the newest file since its cue. False
    /// when this camera is not waiting for one.</summary>
    public bool TakeNow()
    {
        lock (_gate)
        {
            if (_advance != FolderAdvance.Manual || _next is not { } next)
                return false;
            _next = null;
            next.TrySetResult();
            return true;
        }
    }

    public async Task<CuedPicture> CueAsync(PatternCue cue, CancellationToken ct, CancellationToken scan)
    {
        ArgumentNullException.ThrowIfNull(cue);
        // A picture for this cue is a file the folder did not hold just before its pattern went up (Arm), else when the cue came.
        lock (_gate)
        {
            if (!_armed)
                _seen = Files();
            _armed = false;
        }
        _options.Cued?.Invoke(cue);
        while (true)
        {
            string path;
            if (_advance == FolderAdvance.Manual)
            {
                var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                    _next = next;
                // Waiting before it says so: a SCAN NEXT answering these words always finds the camera waiting.
                Say?.Invoke($"{_rig.Name}: shoot {Describe(cue)}, then SCAN NEXT");
                await next.Task.WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(_settle, ct).ConfigureAwait(false);
                HashSet<string> before;
                lock (_gate)
                    before = _seen;
                var fresh = Files().Where(f => !before.Contains(f)).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
                if (fresh is null)
                {
                    Say?.Invoke($"{_rig.Name}: no new picture in {_folder} since {Describe(cue)} went up — shoot it, then SCAN NEXT");
                    continue;
                }
                path = fresh.FullName;
                lock (_gate)
                    _seen.UnionWith(Files());
            }
            else
            {
                (path, _) = await NewFileAsync(ct).ConfigureAwait(false);
            }
            StillImage still;
            try
            {
                still = _read(path);
            }
            catch (MediaOpenException ex)
            {
                Say?.Invoke($"{Path.GetFileName(path)} could not be read ({ex.Message}): re-shoot {Describe(cue)}");
                continue;
            }
            // The tags apply to photos too: a photo of another pattern is refused, never paired by its order.
            if (cue.Kind == "pattern" && cue.Tag > 0 && _located is { Chosen.Length: > 0 } located)
            {
                var read = FrameTag.ReadFrame(still.Gray8, still.Width, still.Height, located.Chosen, located.MinSites);
                if (read.Outcome == FrameTagOutcome.Read && read.Value != cue.Tag)
                {
                    Say?.Invoke(string.Create(CultureInfo.InvariantCulture, $"photo {cue.Index + 1} shows pattern {read.Value}: re-shoot"));
                    continue;
                }
            }
            var frame = Frame(still);
            return new CuedPicture("seen", Task.FromResult(frame), cue.Tag, 0, _located?.Chosen.Length ?? 0, 0, "");
        }
    }

    private static string Describe(PatternCue cue) =>
        cue.Kind == "pattern" ? string.Create(CultureInfo.InvariantCulture, $"pattern {cue.Index + 1}")
            : string.Create(CultureInfo.InvariantCulture, $"locate frame {cue.Index + 1}");

    private CameraFrame Frame(StillImage still) =>
        new(still.Gray8, still.Width, still.Height, Core.Time.MonoClock.Instance.NowNs, Interlocked.Increment(ref _generation)) { Gray16 = still.Gray16 };

    /// <summary>The next file the folder did not hold, once its size has stood still for the settle time.</summary>
    private async Task<(string Path, long Size)> NewFileAsync(CancellationToken ct)
    {
        while (true)
        {
            HashSet<string> seen;
            lock (_gate)
                seen = _seen;
            var fresh = Files().Where(f => !seen.Contains(f)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (fresh is not null)
            {
                var size = Size(fresh);
                await Task.Delay(_settle, ct).ConfigureAwait(false);
                if (size >= 0 && Size(fresh) == size)
                {
                    lock (_gate)
                        _seen.Add(fresh);
                    return (fresh, size);
                }
                continue;
            }
            await _changed.WaitAsync(_options.Poll, ct).ConfigureAwait(false);
        }
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;
        _watcher?.Dispose();
        _changed.Dispose();
    }
}
