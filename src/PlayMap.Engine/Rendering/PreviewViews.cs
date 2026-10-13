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

/// <summary>One VIEW SHOW, parsed: the view id (its ring is <see cref="PreviewRing.PathFor"/>(node, view)), what
/// it shows, the entity it is of (an output, projector, camera or scan id; null for the editor), the variant
/// ("smart" or "confidence" for a scan) and the size the viewer asked for.</summary>
public sealed record ViewRequest(string ViewId, ViewKind Kind, string? TargetId, string? Variant, int Width, int Height)
{
    /// <summary>The largest picture any view is drawn at (a bigger ask is fitted inside, keeping its aspect).</summary>
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1200;

    /// <summary>The "what" word as the desk writes it.</summary>
    public string What => Kind switch
    {
        ViewKind.Editor => "editor",
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
        return new ViewRequest(viewId, kind, target, variant, w, h);
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
/// a peer that dropped) costs the node nothing for long. Thread-safe.</para></summary>
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
    private readonly IMonoClock _clock;
    private IViewRenderer? _renderer;

    public PreviewViews(string nodeId, IMonoClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        _nodeId = nodeId;
        _clock = clock ?? MonoClock.Instance;
    }

    /// <summary>The ring file a view's pictures go to.</summary>
    public string RingPath(string viewId) => PreviewRing.PathFor(_nodeId, viewId);

    /// <summary>The views shown now, by id.</summary>
    public IReadOnlyDictionary<string, ViewRequest> Shown()
    {
        lock (_gate)
            return new Dictionary<string, ViewRequest>(_shown, StringComparer.Ordinal);
    }

    /// <summary>The render host's side (null detaches it: render-backed views are refused again).</summary>
    public void AttachRenderer(IViewRenderer? renderer)
    {
        lock (_gate)
        {
            _renderer = renderer;
            if (renderer is null)
                foreach (var id in _shown.Where(kv => kv.Value.Kind is ViewKind.Editor or ViewKind.Output or ViewKind.Projector).Select(kv => kv.Key).ToList())
                {
                    _shown.Remove(id);
                    _renewed.Remove(id);
                }
        }
    }

    /// <summary>A feed for a kind of view: "camera" or "scan" (another registration replaces it).</summary>
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
        lock (_gate)
        {
            if (_shown.TryGetValue(request.ViewId, out var had) && had == request)
            {
                _renewed[request.ViewId] = _clock.NowNs;
                return null;
            }
            switch (request.Kind)
            {
                case ViewKind.Editor or ViewKind.Output or ViewKind.Projector:
                {
                    if (_renderer is null)
                        return "this node draws no pictures (no OpenGL here) — the view is the desk's own";
                    StopFeed(request.ViewId);
                    if (_renderer.Show(request) is { } refused)
                        return refused;
                    break;
                }
                default:
                {
                    var kind = request.Kind == ViewKind.Camera ? "camera" : "scan";
                    if (!_feeds.TryGetValue(kind, out var feed))
                        return $"no {kind} pictures on this node yet";
                    if (had is not null && had.Kind is ViewKind.Editor or ViewKind.Output or ViewKind.Projector)
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
                            sink = new PreviewSink(new PreviewRingWriter(RingPath(request.ViewId), size.Width, size.Height));
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
        lock (_gate)
        {
            var now = _clock.NowNs;
            List<string>? gone = null;
            foreach (var (id, at) in _renewed)
                if (now - at > LeaseNs && _shown.ContainsKey(id))
                    (gone ??= []).Add(id);
            foreach (var id in gone ?? [])
                Hide(id);
            return gone ?? (IReadOnlyList<string>)[];
        }
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
        lock (_gate)
        {
            _renewed.Remove(viewId);
            if (!_shown.Remove(viewId, out var had))
                return $"no view {viewId} is shown";
            if (had.Kind is ViewKind.Editor or ViewKind.Output or ViewKind.Projector)
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
            foreach (var id in _fed.Keys.ToList())
                StopFeed(id);
            _shown.Clear();
            _renewed.Clear();
        }
    }
}
