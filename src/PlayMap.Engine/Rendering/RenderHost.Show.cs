using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Projection;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-show's hooks into the render host: each frame takes the air's levels the lane built last (the master, a panic,
/// a return from one); null leaves every output at full. wp2-blind (its continuation) adds two things. A blind commit waiting for
/// its boundary is drawn from that frame on. And the editor's draft view (<c>VIEW &lt;v&gt; SHOW editor DRAFT</c>) is drawn by a
/// second renderer on this thread's context that never has an output: each frame it is handed the draft with no outputs, so its
/// canvases and generators are the draft's while the air's renderer never sees the draft at all; its editor picture lands in a
/// ring of its own and is copied into the view's ring. The second renderer exists only while a draft view is shown.</summary>
public sealed partial class RenderHost
{
    /// <summary>A shown draft view: its request, the view's ring (the desk reads it), and on the render thread the second
    /// renderer's own ring for it with a reader.</summary>
    private sealed class DraftView(ViewRequest request, PreviewSink sink) : IDisposable
    {
        public ViewRequest Request { get; } = request;
        public PreviewSink Sink { get; } = sink;
        public volatile bool Stopped;
        public PreviewRingWriter? Ring;
        public PreviewRingReader? Reader;
        public byte[] Buffer = [];
        public long Have = -1;
        public long LastNs = long.MinValue / 2;

        public void Dispose() => Stopped = true;

        /// <summary>Render thread: lets go of the private ring.</summary>
        public void Release()
        {
            Reader?.Dispose();
            Reader = null;
            Ring?.Dispose();
            Ring = null;
        }
    }

    private sealed class DraftFeed(RenderHost host) : IPreviewFeed
    {
        public IDisposable? Start(ViewRequest request, PreviewSink sink, out string why)
        {
            why = "";
            var view = new DraftView(request, sink);
            lock (host._draftGate)
            {
                host._draftViews.Add(view);
                host._draftVersion++;
            }
            return view;
        }
    }

    private readonly Lock _draftGate = new();
    private readonly List<DraftView> _draftViews = [];
    private int _draftVersion;
    // Render thread.
    private readonly List<DraftView> _draftList = [];
    private int _draftListVersion = -1;
    private RenderEngine? _draftRenderer;
    private bool _draftFeedRegistered;
    private Project? _draftFrom;
    private Project? _draftBare;

    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (engine.AirLevelsNow is { } levels)
            inputs = inputs with { Levels = levels };
        // A blind commit is drawn from its boundary's frame on: the root is picked by the frame's leader instant, so the switch is
        // exact to the frame and the same on every node that holds the commit, before the lane makes it the document. A test
        // pattern on the air stays over it.
        if (snap.Pending is { } pending && inputs.LeaderNs >= pending.EffectiveLeaderNs && ReferenceEquals(inputs.Project, snap.Project.Project))
            inputs = inputs with { Project = pending.Root };
        if (!_draftFeedRegistered)
        {
            engine.Previews.Register("draft", new DraftFeed(this));
            _draftFeedRegistered = true;
        }
        DrawDraftViews(engine, snap, inputs.LeaderNs);
    }

    partial void OnContextShow(RenderEngine renderer)
    {
        // The show's levels ride on uAir and the pattern-override path; the draft's renderer is made when a draft view first shows.
    }

    partial void OnContextLostShow()
    {
        if (_draftFeedRegistered)
            Volatile.Read(ref _engine)?.Previews.Register("draft", null);
        _draftFeedRegistered = false;
        foreach (var view in _draftList)
            view.Release();
        _draftList.Clear();
        _draftRenderer?.Dispose();
        _draftRenderer = null;
    }

    /// <summary>Render thread: the draft views that are due, drawn by the draft's renderer from the draft (or the air's project when
    /// no draft is open), and every finished picture copied to its view's ring. Nothing runs while no draft view is shown.</summary>
    private void DrawDraftViews(EngineCore engine, EngineSnapshot snap, long leaderNs)
    {
        var version = Volatile.Read(ref _draftVersion);
        if (version != _draftListVersion)
        {
            lock (_draftGate)
            {
                _draftViews.RemoveAll(v => v.Stopped);
                foreach (var gone in _draftList.Where(v => !_draftViews.Contains(v)))
                    gone.Release();
                _draftList.Clear();
                _draftList.AddRange(_draftViews);
                _draftListVersion = _draftVersion;
            }
        }
        if (_draftList.Count == 0)
        {
            if (_draftRenderer is not null)
            {
                _draftRenderer.Dispose();
                _draftRenderer = null;
                _draftFrom = _draftBare = null;
            }
            return;
        }
        var any = false;
        foreach (var view in _draftList)
        {
            if (view.Stopped)
            {
                lock (_draftGate)
                    _draftVersion++;
                continue;
            }
            any = true;
        }
        if (!any)
            return;
        var draft = snap.Draft?.Project ?? snap.Project.Project;
        if (!ReferenceEquals(draft, _draftFrom))
        {
            _draftFrom = draft;
            _draftBare = draft with { Outputs = [] };
        }
        var renderer = _draftRenderer ??= new RenderEngine(_context!, _options.Engine with { GpuTiming = false });
        renderer.Render(new FrameInputs { Project = _draftBare!, LeaderNs = leaderNs, Sources = _sources!, Stage = _stage! });
        var monoNow = Core.Time.MonoClock.Instance.NowNs;
        foreach (var view in _draftList)
        {
            if (view.Stopped)
                continue;
            if (view.Ring is null)
            {
                var path = engine.Previews.RingPath(view.Request.ViewId + "~draft");
                view.Ring = new PreviewRingWriter(path, view.Sink.MaxWidth, view.Sink.MaxHeight);
                view.Reader = new PreviewRingReader(path);
                view.Buffer = new byte[view.Sink.MaxWidth * view.Sink.MaxHeight * 4];
            }
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
            if (view.Reader!.TryRead(view.Buffer, view.Have, out var info))
            {
                view.Have = info.Sequence;
                view.Sink.WriteBgra(view.Buffer, info.Width, info.Height, info.Stride, info.StampNs);
            }
        }
    }
}
