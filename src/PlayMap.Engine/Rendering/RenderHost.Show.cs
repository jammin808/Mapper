using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;
using PlayMap.Render.Projection;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-show's hooks into the render host: each frame takes the air's levels the lane built last (the master, a panic,
/// a return from one); null leaves every output at full. wp2-blind (its continuation) adds two things.
///
/// <para><b>A waiting blind commit</b> is drawn from its boundary's frame on: the frame's project is the commit's root once the
/// frame's leader instant reaches the commit's, so the switch is exact to the frame for the project's own fields (content,
/// generators, canvases, outputs) on every node holding the commit. The stage's tables (meshes, warps, masks, blends, lenses) are
/// built for the live root and follow when the lane makes the commit the document, a few ticks later.</para>
///
/// <para><b>The editor's draft view</b> (<c>VIEW &lt;v&gt; SHOW editor DRAFT</c>): the render host is the draft feed itself, so a
/// view is accepted as soon as the host runs. A second renderer on this thread's context, which never has an output, is handed the
/// draft with no outputs each time a draft view is drawn: its canvases and generators are the draft's, and the air's renderer never
/// sees the draft. It draws after the air's frame (between frames, with the host's other work), so a draft view never moves the
/// air's present. Its pictures land in a ring of the view's own (made when the view is shown, on the caller's thread) and are copied
/// into the view's ring; a stopped view's ring is kept until the draft renderer can no longer deliver a read into it. The second
/// renderer exists only while a draft view is shown.</para></summary>
public sealed partial class RenderHost : IPreviewFeed
{
    /// <summary>Draft renderer frames a stopped view's ring outlives its last request (the renderer drops an idle preview after 120).</summary>
    private const int DraftRetireFrames = 150;

    /// <summary>A shown draft view: its request, the view's ring (the desk reads it), the draft renderer's own ring for it and a
    /// reader of that ring.</summary>
    private sealed class DraftView(ViewRequest request, PreviewSink sink, PreviewRingWriter ring, PreviewRingReader reader) : IDisposable
    {
        public ViewRequest Request { get; } = request;
        public PreviewSink Sink { get; } = sink;
        public PreviewRingWriter Ring { get; } = ring;
        public PreviewRingReader Reader { get; } = reader;
        public byte[] Buffer { get; } = new byte[sink.MaxWidth * sink.MaxHeight * 4];
        public volatile bool Stopped;
        public long Have = -1;
        public long LastNs = long.MinValue / 2;
        public long StoppedAtFrame = -1;

        public void Dispose() => Stopped = true;

        /// <summary>Render thread, once the draft renderer can no longer write into it: lets go of the private ring.</summary>
        public void Release()
        {
            Reader.Dispose();
            Ring.Dispose();
        }
    }

    private readonly Lock _draftGate = new();
    private readonly List<DraftView> _draftViews = [];
    private int _draftVersion;
    // Render thread.
    private readonly List<DraftView> _draftList = [];
    private readonly List<DraftView> _draftRetiring = [];
    private int _draftListVersion = -1;
    private RenderEngine? _draftRenderer;
    private long _draftFrames;
    private Project? _draftFrom;
    private Project? _draftBare;
    private EngineSnapshot? _draftSnap;
    private long _draftLeaderNs;
    private Action? _drawDrafts;
    private bool _draftQueued;

    /// <summary>The draft feed (PreviewViews asks the attached render host for "draft" views): makes the view's private ring here,
    /// on the caller's thread, and answers why not when it cannot.</summary>
    IDisposable? IPreviewFeed.Start(ViewRequest request, PreviewSink sink, out string why)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        why = "";
        if (_stopping || Volatile.Read(ref _engine) is not { } engine)
        {
            why = "the render host is not running";
            return null;
        }
        PreviewRingWriter? ring = null;
        try
        {
            // A name no view id can spell ('~' is not a view id's character): the draft renderer's own ring for this view, in a file of
            // its own generation, so a view hidden and shown again never writes the ring its retiring predecessor still maps.
            ring = engine.Previews.NewRing(request.ViewId + "~draft", sink.MaxWidth, sink.MaxHeight);
            var view = new DraftView(request, sink, ring, new PreviewRingReader(ring.Path));
            lock (_draftGate)
            {
                _draftViews.Add(view);
                _draftVersion++;
            }
            return view;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ring?.Dispose();
            why = $"{request.ViewId}'s draft ring could not be made: {ex.Message}";
            return null;
        }
    }

    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (engine.AirLevelsNow is { } levels)
            inputs = inputs with { Levels = levels };
        if (snap.Pending is { } pending && inputs.LeaderNs >= pending.EffectiveLeaderNs && ReferenceEquals(inputs.Project, snap.Project.Project))
            inputs = inputs with { Project = pending.Root };
        // The draft views are drawn after this frame, between frames; only what to draw is noted here.
        if (_draftQueued || (Volatile.Read(ref _draftVersion) == _draftListVersion && _draftList.Count == 0 && _draftRetiring.Count == 0 && _draftRenderer is null))
            return;
        _draftSnap = snap;
        _draftLeaderNs = inputs.LeaderNs;
        _draftQueued = true;
        _work.Enqueue(_drawDrafts ??= DrawDraftViewsBetweenFrames);
    }

    partial void OnContextShow(RenderEngine renderer)
    {
        // The show's levels ride on uAir and the pattern-override path; the draft's renderer is made when a draft view first shows.
    }

    partial void OnContextLostShow()
    {
        // The renderer goes first: until it is gone it may deliver a read into a view's ring.
        _draftRenderer?.Dispose();
        _draftRenderer = null;
        foreach (var view in _draftList.Concat(_draftRetiring))
            view.Release();
        _draftList.Clear();
        _draftRetiring.Clear();
        lock (_draftGate)
            _draftViews.Clear();
        _draftSnap = null;
    }

    /// <summary>Render thread, between frames: a fault here is the draft view's, never the air's — it is journalled once and the
    /// draft views stop.</summary>
    private void DrawDraftViewsBetweenFrames()
    {
        _draftQueued = false;
        if (_stopping || _context is null || _draftSnap is not { } snap || Volatile.Read(ref _engine) is not { } engine)
            return;
        _draftSnap = null;
        try
        {
            DrawDraftViews(engine, snap, _draftLeaderNs);
        }
        catch (Exception ex) when (ex is GlException or InvalidOperationException or ArgumentException or IOException or ObjectDisposedException
            or UnauthorizedAccessException)
        {
            engine.Journal.Add("fault", $"draft view: {ex.GetType().Name}: {ex.Message} — the draft views stop", ok: false);
            _context.Device.InvalidateState();
            OnContextLostShow();
        }
    }

    private void DrawDraftViews(EngineCore engine, EngineSnapshot snap, long leaderNs)
    {
        var version = Volatile.Read(ref _draftVersion);
        if (version != _draftListVersion)
        {
            lock (_draftGate)
            {
                _draftViews.RemoveAll(v => v.Stopped);
                foreach (var gone in _draftList.Where(v => !_draftViews.Contains(v)))
                {
                    gone.StoppedAtFrame = _draftFrames;
                    _draftRetiring.Add(gone);
                }
                _draftList.Clear();
                _draftList.AddRange(_draftViews);
                _draftListVersion = version;
            }
        }
        if (_draftList.Count == 0)
        {
            // No view left: the renderer goes, and with it every read it could still deliver; then the rings.
            _draftRenderer?.Dispose();
            _draftRenderer = null;
            _draftFrom = _draftBare = null;
            foreach (var view in _draftRetiring)
                view.Release();
            _draftRetiring.Clear();
            return;
        }
        foreach (var view in _draftList)
        {
            if (view.Stopped)
            {
                lock (_draftGate)
                    _draftVersion++;
            }
        }
        var draft = snap.Draft?.Project ?? snap.Project.Project;
        if (!ReferenceEquals(draft, _draftFrom))
        {
            _draftFrom = draft;
            _draftBare = draft with { Outputs = [] };
        }
        var renderer = _draftRenderer ??= new RenderEngine(_context!, _options.Engine with { GpuTiming = false });
        renderer.Render(new FrameInputs { Project = _draftBare!, LeaderNs = leaderNs, Sources = _sources!, Stage = _stage! });
        _draftFrames++;
        for (var i = _draftRetiring.Count - 1; i >= 0; i--)
        {
            if (_draftFrames - _draftRetiring[i].StoppedAtFrame > DraftRetireFrames)
            {
                _draftRetiring[i].Release();
                _draftRetiring.RemoveAt(i);
            }
        }
        var monoNow = Core.Time.MonoClock.Instance.NowNs;
        foreach (var view in _draftList)
        {
            if (view.Stopped)
                continue;
            if (monoNow - view.LastNs >= (long)_options.StagePreviewInterval.TotalNanoseconds)
            {
                view.LastNs = monoNow;
                EditorCamera? eye;
                lock (_viewGate)
                    eye = _cameras.GetValueOrDefault(view.Request.ViewId);
                var camera = eye ?? DefaultEditorCamera(view.Request);
                var aspect = camera.Intrinsics.Width / (double)Math.Max(1, camera.Intrinsics.Height);
                var (w, h) = ViewRequest.Fit(Math.Min(view.Request.Width, view.Sink.MaxWidth), Math.Min(view.Request.Height, view.Sink.MaxHeight), aspect);
                renderer.Preview(new PreviewRequest(view.Request.ViewId, view.Ring, w, h, Editor: camera));
            }
            if (view.Reader.TryRead(view.Buffer, view.Have, out var info))
            {
                view.Have = info.Sequence;
                view.Sink.WriteBgra(view.Buffer, info.Width, info.Height, info.Stride, info.StampNs);
            }
        }
    }
}
