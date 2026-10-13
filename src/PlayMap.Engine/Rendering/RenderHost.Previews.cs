using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Projection;

namespace PlayMap.Engine.Rendering;

/// <summary>The render-backed preview views — how the desk sees the outputs, the projectors and the stage.
///
/// <para><b>How the desk asks.</b> Through the VIEW verb, like any peer (<see cref="PreviewViews"/> routes it
/// here): <c>VIEW op-&lt;output&gt; SHOW output:&lt;output&gt; 480x270</c> for an output's finished picture,
/// <c>VIEW pj-&lt;projector&gt; SHOW projector:&lt;projector&gt; &lt;w&gt;x&lt;h&gt;</c> for the stage as that
/// projector lights it (its pose and intrinsics, content on), <c>VIEW editor SHOW editor &lt;w&gt;x&lt;h&gt;</c>
/// for the free 3D view, whose eye <c>VIEW editor CAMERA {"intrinsics":…,"pose":…}</c> moves, and
/// <c>VIEW &lt;view&gt; HIDE</c> when it no longer shows. The pictures are BGRA in the ring at
/// <see cref="PreviewRing.PathFor"/>(node id, view id), read with a <see cref="PreviewRingReader"/>.</para>
///
/// <para><b>What it costs.</b> An output view is a blit of the output's finished picture at most ten times a
/// second (<see cref="RenderHostOptions.OutputPreviewInterval"/>); an editor or projector view is a stage pass
/// at most thirty (<see cref="RenderHostOptions.StagePreviewInterval"/>); both are read back through a pixel
/// buffer ring, so the render thread never waits for them, and only while shown. A picture is fitted inside
/// the size asked (and 1920×1200), keeping the output's or the camera's aspect.</para>
///
/// <para><b>Rings.</b> A view's ring is made once and kept while the view lives: the desk opens it once. The
/// editor's ring is made at the largest size, so the desk's viewport can grow without a new ring. A hidden view
/// keeps its ring a few seconds (the readbacks in flight land, and showing it again finds it), then it goes.</para></summary>
public sealed partial class RenderHost : IViewRenderer
{
    private const int RetireFrames = 150;

    private sealed class ViewSlot(string viewId, PreviewRingWriter ring)
    {
        public string ViewId { get; } = viewId;
        public PreviewRingWriter Ring { get; } = ring;
        public volatile ViewRequest? Request;
        public volatile EditorCamera? Camera;
        public long HiddenAtFrame = -1;
        public long LastNs = long.MinValue / 2;
        public PreviewRequest? Built;
        public ViewRequest? BuiltRequest;
        public object? BuiltFrom;
    }

    private readonly Lock _viewGate = new();
    private readonly Dictionary<string, ViewSlot> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorCamera> _cameras = new(StringComparer.Ordinal);
    private int _viewsVersion;
    // Render thread.
    private readonly List<ViewSlot> _viewList = [];
    private int _viewListVersion = -1;

    /// <summary>Views being drawn now (hidden ones still draining are not counted).</summary>
    public int ViewsShown
    {
        get
        {
            lock (_viewGate)
                return _views.Values.Count(v => v.Request is not null);
        }
    }

    string? IViewRenderer.Show(ViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ViewKind.Editor or ViewKind.Output or ViewKind.Projector))
            return $"the render host draws editor, output and projector views — not {request.What}";
        if (_stopping || Volatile.Read(ref _engine) is not { } engine)
            return "the render host is not running";
        lock (_viewGate)
        {
            if (!_views.TryGetValue(request.ViewId, out var slot))
            {
                var (w, h) = request.Kind == ViewKind.Editor
                    ? (ViewRequest.MaxWidth, ViewRequest.MaxHeight)
                    : (Math.Min(request.Width, ViewRequest.MaxWidth), Math.Min(request.Height, ViewRequest.MaxHeight));
                slot = new ViewSlot(request.ViewId, new PreviewRingWriter(engine.Previews.RingPath(request.ViewId), w, h));
                _views[request.ViewId] = slot;
            }
            if (_cameras.TryGetValue(request.ViewId, out var camera))
                slot.Camera = camera;
            slot.Request = request;
            Interlocked.Increment(ref _viewsVersion);
        }
        return null;
    }

    string? IViewRenderer.Camera(string viewId, EditorCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lock (_viewGate)
        {
            if (_views.TryGetValue(viewId, out var slot) && slot.Request is { Kind: not ViewKind.Editor } shown)
                return $"{viewId} shows {shown.What} — only an editor view has an eye to move";
            _cameras[viewId] = camera;
            if (slot is not null)
                slot.Camera = camera;
        }
        return null;
    }

    bool IViewRenderer.Hide(string viewId)
    {
        lock (_viewGate)
        {
            if (!_views.TryGetValue(viewId, out var slot) || slot.Request is null)
                return false;
            slot.Request = null;
            Interlocked.Increment(ref _viewsVersion);
            return true;
        }
    }

    /// <summary>On the render thread after the frame: draws the views that are due and lets go of rings
    /// hidden long enough for their last readbacks to have landed.</summary>
    private void RenderPreviews(RenderEngine renderer, Project project, long monoNow)
    {
        var version = Volatile.Read(ref _viewsVersion);
        if (version != _viewListVersion)
        {
            lock (_viewGate)
            {
                _viewList.Clear();
                foreach (var slot in _views.Values)
                {
                    if (slot.Request is null && slot.HiddenAtFrame < 0)
                        slot.HiddenAtFrame = renderer.Frame;
                    else if (slot.Request is not null)
                        slot.HiddenAtFrame = -1;
                    _viewList.Add(slot);
                }
                _viewListVersion = version;
            }
        }
        if (_viewList.Count == 0)
            return;
        var retire = false;
        foreach (var slot in _viewList)
        {
            if (slot.Request is not { } request)
            {
                retire |= slot.HiddenAtFrame >= 0 && renderer.Frame - slot.HiddenAtFrame > RetireFrames;
                continue;
            }
            var interval = request.Kind == ViewKind.Output ? _options.OutputPreviewInterval : _options.StagePreviewInterval;
            if (monoNow - slot.LastNs < (long)interval.TotalNanoseconds)
                continue;
            if (Build(slot, request, project) is not { } preview)
                continue;
            renderer.Preview(preview);
            slot.LastNs = monoNow;
        }
        if (retire)
            RetireViews(renderer.Frame);
    }

    /// <summary>The renderer's request for a view, made again only when what it is made from changed.</summary>
    private PreviewRequest? Build(ViewSlot slot, ViewRequest request, Project project)
    {
        object? from;
        double aspect;
        switch (request.Kind)
        {
            case ViewKind.Output when project.FindOutput(request.TargetId) is { Width: > 0, Height: > 0 } output:
                from = output;
                aspect = output.Width / (double)output.Height;
                break;
            case ViewKind.Projector when project.FindProjector(request.TargetId) is { } projector:
                from = projector;
                aspect = projector.Intrinsics.Width / (double)Math.Max(1, projector.Intrinsics.Height);
                break;
            case ViewKind.Editor:
                var camera = slot.Camera ?? DefaultEditorCamera(request);
                from = camera;
                aspect = camera.Intrinsics.Width / (double)Math.Max(1, camera.Intrinsics.Height);
                break;
            default:
                // Its output or projector is gone: the ring keeps its last picture.
                return null;
        }
        if (slot.Built is not null && ReferenceEquals(slot.BuiltRequest, request) && ReferenceEquals(slot.BuiltFrom, from))
            return slot.Built;
        var (w, h) = ViewRequest.Fit(Math.Min(request.Width, slot.Ring.MaxWidth), Math.Min(request.Height, slot.Ring.MaxHeight), aspect);
        slot.Built = request.Kind switch
        {
            ViewKind.Output => new PreviewRequest(slot.ViewId, slot.Ring, w, h, OutputId: request.TargetId),
            ViewKind.Projector => new PreviewRequest(slot.ViewId, slot.Ring, w, h,
                Editor: new EditorCamera(((Projector)from).Intrinsics, ((Projector)from).Pose, ShowContent: true, ShowFrusta: false, ShowFloor: false)),
            _ => new PreviewRequest(slot.ViewId, slot.Ring, w, h, Editor: (EditorCamera)from),
        };
        slot.BuiltRequest = request;
        slot.BuiltFrom = from;
        return slot.Built;
    }

    /// <summary>Before the desk's first CAMERA: the stage from the front, a little above, 60° across.</summary>
    private static EditorCamera DefaultEditorCamera(ViewRequest request) =>
        new(Intrinsics.FromFieldOfView(request.Width, request.Height, 60), Pose.LookAt(new Vec3d(4, 2.5, 6), new Vec3d(0, 1, 0), Vec3d.UnitY));

    private void RetireViews(long frame)
    {
        lock (_viewGate)
        {
            foreach (var slot in _viewList)
            {
                if (slot.Request is not null || slot.HiddenAtFrame < 0 || frame - slot.HiddenAtFrame <= RetireFrames)
                    continue;
                _views.Remove(slot.ViewId);
                slot.Ring.Dispose();
            }
            Interlocked.Increment(ref _viewsVersion);
        }
    }

    private void DisposePreviews()
    {
        lock (_viewGate)
        {
            foreach (var slot in _views.Values)
                slot.Ring.Dispose();
            _views.Clear();
            _viewList.Clear();
        }
    }
}
