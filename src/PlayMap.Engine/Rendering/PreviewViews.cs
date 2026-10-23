using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Core.Time;
using PlayMap.Render.Projection;

namespace PlayMap.Engine.Rendering;

/// <summary>What a view shows: the editor's free 3D view, an output's finished picture, the stage from a
/// projector's pose at its raster, a camera's live picture, or a scan's smart or confidence image.</summary>
public enum ViewKind
{
    Editor,
    Output,
    Projector,
    Camera,
    Scan,
}

/// <summary>One VIEW SHOW, parsed: the view id (its ring is <see cref="PreviewViews.RingOf"/> the view), what
/// it shows, the entity it is of (an output, projector, camera or scan id; null for the editor), the variant
/// ("smart" or "confidence" for a scan) and the size the viewer asked for.</summary>
public sealed record ViewRequest(string ViewId, ViewKind Kind, string? TargetId, string? Variant, int Width, int Height)
{
    /// <summary>An editor view of the blind draft instead of the air ("editor DRAFT"): drawn by the show's draft feed, never on an
    /// output (wp2-blind).</summary>
    public bool Draft { get; init; }

    /// <summary>Drawn by the render host's own views (the editor of the air, outputs, projectors); every other view is a feed's.</summary>
    public bool Rendered => (Kind is ViewKind.Editor or ViewKind.Output or ViewKind.Projector) && !Draft;

    /// <summary>The largest picture any view is drawn at (a bigger ask is fitted inside, keeping its aspect).</summary>
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1200;

    /// <summary>The "what" word as the desk writes it.</summary>
    public string What => Kind switch
    {
        ViewKind.Editor => Draft ? "editor DRAFT" : "editor",
        ViewKind.Scan => $"scan:{TargetId}:{Variant}",
        _ => $"{Kind.ToString().ToLowerInvariant()}:{TargetId}",
    };

    /// <summary>A view id is also a file name: letters, digits, '-', '_' and '.', at most 64, no "..".</summary>
    public static bool IsViewId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return false;
        return true;
    }

    /// <summary>Parses "&lt;what&gt; [&lt;w&gt;x&lt;h&gt;]" (null with the reason when it cannot).</summary>
    public static ViewRequest? Parse(string viewId, string what, string? size, out string why)
    {
        why = "";
        ViewKind kind;
        string? target = null, variant = null;
        var colon = what.IndexOf(':', StringComparison.Ordinal);
        var head = colon < 0 ? what : what[..colon];
        var rest = colon < 0 ? "" : what[(colon + 1)..];
        switch (head.ToLowerInvariant())
        {
            case "editor" when rest.Length == 0:
                kind = ViewKind.Editor;
                break;
            case "output" or "projector" or "camera" when rest.Length > 0:
                kind = head.ToLowerInvariant() switch { "output" => ViewKind.Output, "projector" => ViewKind.Projector, _ => ViewKind.Camera };
                target = rest;
                break;
            case "scan" when rest.Split(':') is [{ Length: > 0 } scan, var v] && v.ToLowerInvariant() is "smart" or "confidence":
                kind = ViewKind.Scan;
                target = scan;
                variant = v.ToLowerInvariant();
                break;
            default:
                why = $"a view shows editor, output:<id>, projector:<id>, camera:<id> or scan:<id>:smart|confidence — not {what}";
                return null;
        }
        var (w, h) = kind switch
        {
            ViewKind.Output => (480, 270),
            ViewKind.Camera => (960, 540),
            _ => (1280, 720),
        };
        var draft = kind == ViewKind.Editor && size is not null && size.Equals("DRAFT", StringComparison.OrdinalIgnoreCase);
        if (draft)
            size = null;
        if (size is not null)
        {
            var x = size.IndexOf('x', StringComparison.OrdinalIgnoreCase);
            if (x <= 0 || !int.TryParse(size.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out w)
                || !int.TryParse(size.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out h) || w < 16 || h < 16 || w > 8192 || h > 8192)
            {
                why = $"a view's size is <w>x<h>, 16 to 8192 each — not {size}";
                return null;
            }
        }
        return new ViewRequest(viewId, kind, target, variant, w, h) { Draft = draft };
    }

    /// <summary>The largest size of the asked aspect that fits both the ask and the cap.</summary>
    public static (int Width, int Height) Fit(int width, int height, double aspect)
    {
        var boxW = Math.Min(width, MaxWidth);
        var boxH = Math.Min(height, MaxHeight);
        aspect = aspect > 0 && double.IsFinite(aspect) ? aspect : boxW / (double)boxH;
        var w = boxW;
        var h = (int)Math.Round(w / aspect);
        if (h > boxH)
        {
            h = boxH;
            w = (int)Math.Round(h * aspect);
        }
        return (Math.Max(1, w), Math.Max(1, h));
    }
}

/// <summary>The render side of the views (the render host): the editor, outputs and projectors. Each call
/// answers null when done, or the words why not.</summary>
public interface IViewRenderer
{
    string? Show(ViewRequest request);

    /// <summary>The file of the ring a shown view's pictures go to, or null when it shows none.</summary>
    string? RingOf(string viewId);

    string? Camera(string viewId, EditorCamera camera);

    bool Hide(string viewId);
}

/// <summary>Draws the views the render host does not: a camera's live picture, a scan's smart or confidence
/// image. Registered on <see cref="PreviewViews"/> by kind ("camera", "scan"); it is handed a sink whose ring
/// the desk already reads, and feeds it until the handle it returned is disposed (HIDE, another SHOW of the
/// view, or the node stopping). Start is called under the registry's lock: it starts the feed and returns at
/// once, and never calls back into the registry.</summary>
public interface IPreviewFeed
{
    /// <summary>Starts feeding the view; null with the reason when it cannot ("no camera cm_9").</summary>
    IDisposable? Start(ViewRequest request, PreviewSink sink, out string why);
}

/// <summary>Where a feed's pictures go: the view's ring, sized at most as the viewer asked. Frames larger than
/// the ring are thinned to fit (nearest pixel, whole steps); grey and RGBA frames become BGRA. Thread-safe:
/// one frame at a time; a write never waits for the reader.</summary>
public sealed class PreviewSink : IDisposable
{
    private readonly Lock _gate = new();
    private readonly PreviewRingWriter _ring;
    private byte[] _scratch = [];
    private bool _disposed;

    internal PreviewSink(PreviewRingWriter ring)
    {
        _ring = ring;
    }

    public int MaxWidth => _ring.MaxWidth;

    public int MaxHeight => _ring.MaxHeight;

    /// <summary>The ring's file.</summary>
    public string Path => _ring.Path;

    /// <summary>Frames written so far.</summary>
    public long Frames { get; private set; }

    public bool WriteGray(ReadOnlySpan<byte> gray, int width, int height, int stride, long stampNs) =>
        Write(gray, width, height, stride, 1, stampNs);

    public bool WriteRgba(ReadOnlySpan<byte> rgba, int width, int height, int stride, long stampNs) =>
        Write(rgba, width, height, stride, 4, stampNs);

    public bool WriteBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride, long stampNs) =>
        Write(bgra, width, height, stride, -4, stampNs);

    /// <param name="bytesPerPixel">1 grey, 4 RGBA, −4 BGRA.</param>
    private bool Write(ReadOnlySpan<byte> src, int width, int height, int stride, int bytesPerPixel, long stampNs)
    {
        if (width < 1 || height < 1 || src.Length < (long)stride * (height - 1) + width * Math.Abs(bytesPerPixel))
            return false;
        var step = Math.Max(1, Math.Max((width + _ring.MaxWidth - 1) / _ring.MaxWidth, (height + _ring.MaxHeight - 1) / _ring.MaxHeight));
        var w = width / step;
        var h = height / step;
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (bytesPerPixel == -4 && step == 1)
            {
                Frames++;
                return _ring.Write(src, width, height, stride, stampNs);
            }
            if (_scratch.Length < w * h * 4)
                _scratch = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                var row = src.Slice(y * step * stride);
                for (var x = 0; x < w; x++)
                {
                    var o = (y * w + x) * 4;
                    var sx = x * step;
                    switch (bytesPerPixel)
                    {
                        case 1:
                            _scratch[o] = _scratch[o + 1] = _scratch[o + 2] = row[sx];
                            break;
                        case 4:
                            _scratch[o] = row[sx * 4 + 2];
                            _scratch[o + 1] = row[sx * 4 + 1];
                            _scratch[o + 2] = row[sx * 4];
                            break;
                        default:
                            _scratch[o] = row[sx * 4];
                            _scratch[o + 1] = row[sx * 4 + 1];
                            _scratch[o + 2] = row[sx * 4 + 2];
                            break;
                    }
                    _scratch[o + 3] = 255;
                }
            }
            Frames++;
            return _ring.Write(_scratch.AsSpan(0, w * h * 4), w, h, w * 4, stampNs);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ring.Dispose();
        }
    }
}

/// <summary>The node's preview views, as the VIEW verb drives them: SHOW, CAMERA, KEEP and HIDE by view id. The
/// render host attaches itself for the editor, outputs and projectors; the scan side registers feeds for
/// cameras and scans (<see cref="Register"/>). A view of a kind nobody draws is refused with words (the desk
/// then draws its own stand-in): with no GPU on the node, every render-backed view is.
///
/// <para>A shown view is leased: SHOW, CAMERA and KEEP renew it, and one not renewed for <see cref="LeaseNs"/> is
/// hidden by <see cref="Expire"/> and its ring let go — a viewer that went away without HIDE (a desk that crashed,
/// a peer that dropped) costs the node nothing for long.</para>
///
/// <para>Each ring the node makes has a file of its own, named with a generation (<see cref="NewRing"/>): a view shown again after
/// its ring went, or a draft view's private ring made while the one before it retires, is a new file, so a new writer never meets
/// a reader still mapping an old ring (on Windows such a reader keeps the old file; on Linux a writer opening it would truncate it
/// under the reader's map). The VIEW reply and STATE (<c>machine.views</c>) name the file a view's reader opens. Thread-safe.</para></summary>
public sealed class PreviewViews : IDisposable
{
    /// <summary>How long a view lives without SHOW, CAMERA or KEEP.</summary>
    public const long LeaseNs = 15_000_000_000;

    private sealed record FeedView(ViewRequest Request, PreviewSink Sink, IDisposable Running);

    private readonly string _nodeId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IPreviewFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FeedView> _fed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewRequest> _shown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _renewed = new(StringComparer.Ordinal);

    /// <summary>View ids the render host has drawn into: their rings stay mapped a while after they are hidden, so a draft view never
    /// takes one of these ids (its ring would be a second writer on the same file).</summary>
    private readonly HashSet<string> _renderedIds = new(StringComparer.Ordinal);
    private readonly IMonoClock _clock;
    private IViewRenderer? _renderer;
    private long _generation;
    private bool _disposed;

    /// <param name="firstGeneration">The generation the first ring made takes, counted up from there: the engine gives its start's
    /// wall-clock milliseconds × 1000, so a ring made after a restart never takes the name of one a reader opened before it.</param>
    public PreviewViews(string nodeId, IMonoClock? clock = null, long firstGeneration = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(firstGeneration);
        _nodeId = nodeId;
        _clock = clock ?? MonoClock.Instance;
        _generation = firstGeneration - 1;
    }

    /// <summary>The file a shown view's pictures go to (the one its reader opens), or null when it is not shown.</summary>
    public string? RingOf(string viewId)
    {
        lock (_gate)
        {
            if (!_shown.TryGetValue(viewId, out var request))
                return null;
            return request.Rendered ? _renderer?.RingOf(viewId) : _fed.TryGetValue(viewId, out var fed) ? fed.Sink.Path : null;
        }
    }

    /// <summary>The file a shown view's pictures go to; an <see cref="InvalidOperationException"/> when it is not shown.</summary>
    public string RingPath(string viewId) => RingOf(viewId) ?? throw new InvalidOperationException($"no view {viewId} is shown");

    /// <summary>A new ring for <paramref name="name"/> (a view id, or a view's private ring) in a file no ring had before: the next
    /// generation's. A file left at that name by an earlier run is deleted when it can be, and the generation after it taken. The
    /// name's earlier generations are let go first where they can be: a file a reader still mapped when its writer let it go (it
    /// stays on Windows until the reader closes) goes once that reader has; on Linux an earlier ring still written is only unlinked,
    /// its writer and readers keep their maps.</summary>
    public PreviewRingWriter NewRing(string name, int maxWidth, int maxHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SweepGenerations(name);
        for (var attempt = 0; ; attempt++)
        {
            var path = PreviewRing.PathFor(_nodeId, name, Interlocked.Increment(ref _generation));
            try
            {
                return new PreviewRingWriter(path, maxWidth, maxHeight);
            }
            catch (IOException) when (attempt < 16 && File.Exists(path))
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>Takes away the files of this node's rings that nothing holds any more, such as those of a run that stopped without
    /// letting its rings go: every generation's file of this node in the preview folder ("playmap-&lt;node&gt;-&lt;name&gt;~&lt;digits&gt;.preview")
    /// that no writer and no reader has open. A ring that another engine of this node id still writes, or that a reader still maps,
    /// is held open, and stays; a file named without a generation is never this node's ring.</summary>
    public void SweepLeftovers()
    {
        var prefix = $"playmap-{_nodeId}-";
        string[] files;
        try
        {
            files = Directory.GetFiles(PreviewRing.DefaultFolder(), prefix + "*~*.preview");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be read now: each ring made sweeps its own name's generations still.
            return;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var tilde = name.LastIndexOf('~');
            if (tilde > prefix.Length && tilde < name.Length - 1 && !name.AsSpan(tilde + 1).ContainsAnyExceptInRange('0', '9'))
                TryDeleteUnheld(file);
        }
    }

    /// <summary>Deletes a file only when it can open it alone: a writer or a reader that has it open makes that open fail (a sharing
    /// violation on Windows, the advisory lock every open takes on Linux and macOS), and the file stays.</summary>
    private static void TryDeleteUnheld(string path)
    {
        try
        {
            var alone = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            alone.Dispose();
        }
        catch (IOException)
        {
            // Held, or gone already: it stays, or there is nothing to take.
        }
        catch (UnauthorizedAccessException)
        {
            // Not this user's to open: it stays.
        }
    }

    /// <summary>Deletes, where it can, every file of an earlier generation of <paramref name="name"/>'s ring (never another name's:
    /// the part after the name's '~' is the generation's digits only).</summary>
    private void SweepGenerations(string name)
    {
        var prefix = $"playmap-{_nodeId}-{name}~";
        string[] files;
        try
        {
            files = Directory.GetFiles(PreviewRing.DefaultFolder(), prefix + "*.preview");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be read now: the next ring made sweeps again.
            return;
        }
        foreach (var file in files)
        {
            var generation = Path.GetFileNameWithoutExtension(file).AsSpan(prefix.Length);
            if (!generation.IsEmpty && !generation.ContainsAnyExceptInRange('0', '9'))
                TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // An earlier run's reader still maps it (Windows): the next generation is taken instead.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>Raised (outside the registry's lock, on the caller's thread) when the views shown, or a view's ring, changed.</summary>
    public event Action? Changed;

    /// <summary>The views shown now, by id.</summary>
    public IReadOnlyDictionary<string, ViewRequest> Shown()
    {
        lock (_gate)
            return new Dictionary<string, ViewRequest>(_shown, StringComparer.Ordinal);
    }

    /// <summary>The render host's side (null detaches it: render-backed views are refused again).</summary>
    public void AttachRenderer(IViewRenderer? renderer)
    {
        var changed = false;
        lock (_gate)
        {
            _renderer = renderer;
            if (renderer is null)
                foreach (var id in _shown.Where(kv => kv.Value.Rendered).Select(kv => kv.Key).ToList())
                {
                    _shown.Remove(id);
                    _renewed.Remove(id);
                    changed = true;
                }
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>A feed for a kind of view: "camera", "scan" or "draft" (another registration replaces it).</summary>
    public void Register(string kind, IPreviewFeed? feed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_gate)
        {
            if (feed is null)
                _feeds.Remove(kind);
            else
                _feeds[kind] = feed;
        }
    }

    /// <summary>Shows a view (again: a new size, a new target). Null when shown, else the words why not.</summary>
    public string? Show(ViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var why = ShowLocked(request, out var changed);
        if (changed)
            Changed?.Invoke();
        return why;
    }

    private string? ShowLocked(ViewRequest request, out bool changed)
    {
        changed = false;
        lock (_gate)
        {
            if (_disposed)
                return "the node is stopping";
            if (_shown.TryGetValue(request.ViewId, out var had) && had == request)
            {
                _renewed[request.ViewId] = _clock.NowNs;
                return null;
            }
            switch (request.Rendered)
            {
                case true:
                {
                    if (_renderer is null)
                        return "this node draws no pictures (no OpenGL here) — the view is the desk's own";
                    StopFeed(request.ViewId);
                    if (_renderer.Show(request) is { } refused)
                        return refused;
                    _renderedIds.Add(request.ViewId);
                    break;
                }
                default:
                {
                    var kind = request.Kind switch { ViewKind.Camera => "camera", ViewKind.Editor => "draft", _ => "scan" };
                    if (kind == "draft" && _renderedIds.Contains(request.ViewId))
                        return $"{request.ViewId} has drawn the air — a draft view takes a view id of its own (VIEW {request.ViewId}-draft SHOW editor DRAFT)";
                    // The render host draws the draft views itself (wp2-blind): attached, it is the draft feed.
                    var feed = _feeds.GetValueOrDefault(kind) ?? (kind == "draft" ? _renderer as IPreviewFeed : null);
                    if (feed is null)
                        return kind == "draft" ? "this node draws no draft view (no OpenGL here)" : $"no {kind} pictures on this node yet";
                    if (had is not null && had.Rendered)
                        _renderer?.Hide(request.ViewId);
                    // The desk opens a view's ring once: a view shown again keeps its ring (frames are thinned to it).
                    PreviewSink sink;
                    if (_fed.Remove(request.ViewId, out var old))
                    {
                        old.Running.Dispose();
                        sink = old.Sink;
                    }
                    else
                    {
                        var size = ViewRequest.Fit(request.Width, request.Height, request.Width / (double)request.Height);
                        try
                        {
                            sink = new PreviewSink(NewRing(request.ViewId, size.Width, size.Height));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _shown.Remove(request.ViewId);
                            return $"{request.ViewId}'s ring could not be made: {ex.Message}";
                        }
                    }
                    var running = feed.Start(request, sink, out var why);
                    if (running is null)
                    {
                        sink.Dispose();
                        _shown.Remove(request.ViewId);
                        return why.Length > 0 ? why : $"the {kind} pictures did not start";
                    }
                    _fed[request.ViewId] = new FeedView(request, sink, running);
                    break;
                }
            }
            _shown[request.ViewId] = request;
            _renewed[request.ViewId] = _clock.NowNs;
            changed = true;
            return null;
        }
    }

    /// <summary>Renews a shown view's lease; null when renewed, else the words why not.</summary>
    public string? Keep(string viewId)
    {
        lock (_gate)
        {
            if (!_shown.ContainsKey(viewId))
                return $"no view {viewId} is shown — VIEW {viewId} SHOW <what> first";
            _renewed[viewId] = _clock.NowNs;
            return null;
        }
    }

    /// <summary>Hides every view whose lease ran out (no SHOW, CAMERA or KEEP for <see cref="LeaseNs"/>), letting
    /// its ring go; answers the views hidden.</summary>
    public IReadOnlyList<string> Expire()
    {
        List<string>? gone = null;
        lock (_gate)
        {
            var now = _clock.NowNs;
            foreach (var (id, at) in _renewed)
                if (now - at > LeaseNs && _shown.ContainsKey(id))
                    (gone ??= []).Add(id);
            foreach (var id in gone ?? [])
                HideLocked(id);
        }
        if (gone is not null)
            Changed?.Invoke();
        return gone ?? (IReadOnlyList<string>)[];
    }

    /// <summary>Moves a view's eye (the editor's orbit); kept for a view not shown yet.</summary>
    public string? Camera(string viewId, EditorCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lock (_gate)
        {
            if (_renderer is null)
                return "this node draws no pictures (no OpenGL here) — the view is the desk's own";
            if (_renderer.Camera(viewId, camera) is { } refused)
                return refused;
            if (_shown.ContainsKey(viewId))
                _renewed[viewId] = _clock.NowNs;
            return null;
        }
    }

    /// <summary>Lets a view go; null when it went, else the words why not.</summary>
    public string? Hide(string viewId)
    {
        var why = HideLocked(viewId);
        if (why is null)
            Changed?.Invoke();
        return why;
    }

    private string? HideLocked(string viewId)
    {
        lock (_gate)
        {
            _renewed.Remove(viewId);
            if (!_shown.Remove(viewId, out var had))
                return $"no view {viewId} is shown";
            if (had.Rendered)
                _renderer?.Hide(viewId);
            else
                StopFeed(viewId);
            return null;
        }
    }

    private void StopFeed(string viewId)
    {
        if (!_fed.Remove(viewId, out var fed))
            return;
        fed.Running.Dispose();
        fed.Sink.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var id in _fed.Keys.ToList())
                StopFeed(id);
            _shown.Clear();
            _renewed.Clear();
        }
    }
}
