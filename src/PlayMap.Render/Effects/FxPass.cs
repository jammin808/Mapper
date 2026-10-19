using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;

namespace PlayMap.Render.Effects;

/// <summary>Effect chains in the frame. An output's chain runs on its picture before the output stage (warp, blend, trims), never
/// after it; a layer's on its picture before its canvas is drawn into; a surface's on its content once a frame, which every
/// projector samples; a playlist item's on its picture while its playlist shows it, its time counting from the item's start.
/// What the air shows instead of the show — a test pattern, a panic's safe look — never passes through a chain; blackout, raw
/// scan frames and the identify card never reach a pass at all.
///
/// A chain whose owner the frame did not draw says why in its slots. An output's says so when the frame ends; a layer's, a
/// surface's and an item's once the frame is wholly drawn (when the next begins, so a picture a later pass taps in its FrameEnd
/// counts). While the air keeps a chain off it — an output that would show its owner is black, held, covered by a test pattern
/// or safe look, or shows a scan frame (for an item, the one its playlist showed when the air took it) — the chain keeps its
/// programs and buffers, says the air's reason and draws on the first frame back; a chain whose owner is out of the picture (a
/// layer or surface that is not visible, a canvas nothing shows, an item its playlist is not showing) lets them go once unused for
/// <see cref="FxChainRunner.IdleFrames"/> frames. Which outputs would show an owner is read from the show: an output's canvas,
/// source or projector's surfaces, through canvases' layers and every item of a playlist. The pass owns the effects' lane (the
/// programs it asked of the device) and the chain runner, and draws into the frame's list; it is made, drawn and disposed on the
/// render thread.</summary>
public sealed class FxPass : IFramePass
{
    internal const string CoveredWords = "not drawn: a test pattern or safe look covers its output";
    internal const string BlackWords = "not drawn: the air is black";
    internal const string OtherWords = "not drawn: its output shows a scan frame or has no size";
    internal const string LayerHiddenWords = "not drawn: the layer is not visible";
    internal const string LayerUnshownWords = "not drawn: no output shows its canvas";
    internal const string SurfaceHiddenWords = "not drawn: the surface is not visible";
    internal const string SurfaceUnshownWords = "not drawn: no projector shows it";
    internal const string SurfaceEffectWords = "not drawn: its content is a surface-aware effect the stage draws itself";
    internal const string QuadCardWords = "not drawn: the quad has no source and shows its test card";
    internal const string ItemUnshownWords = "not drawn: its playlist is not showing it";

    /// <summary>Why the air kept an output off the show this frame, weakest first (a stronger reason wins where outputs meet).</summary>
    private enum OffAir
    {
        Held,
        Other,
        Covered,
        Black,
    }

    /// <summary>How deep the walk from an output follows canvases and playlists into each other (a loop stops at its first turn).</summary>
    private const int MaxReach = 8;

    private readonly FxChainRunner _runner;
    private readonly HashSet<string> _drawn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OffAir> _offCanvases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OffAir> _offPlaylists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OffAir> _offSurfaces = new(StringComparer.Ordinal);
    private PassFrame _drawing;
    private long _frame = -1;
    private bool _disposed;

    /// <param name="device">The device the frame draws on (render thread).</param>
    /// <param name="sharedContextLane">False makes the effects' programs on the render thread (the lane's fallback).</param>
    public FxPass(IGpuDevice device, bool sharedContextLane = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        Lane = new FxLane(device, sharedContextLane);
        _runner = new FxChainRunner(device, Lane);
    }

    public string Name => "fx";

    /// <summary>After the packages that draw the picture, before overlays that draw on it.</summary>
    public int Order => 100;

    public FxLane Lane { get; }

    /// <summary>What each slot did on the renderer, for STATE (any thread).</summary>
    public FxStats Stats => _runner.Stats;

    /// <summary>The kinds the last frame's show could use.</summary>
    public FxCatalog Catalog => _runner.Catalog;

    /// <summary>The frame's leader instant as wall time, for DATE; the host sets it each frame.</summary>
    public long UnixMs { get; set; }

    public GpuTexture Item(in PassFrame frame, PlaylistItem item, long startNs, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(item);
        Begin(in frame);
        return item.Fx.IsEmpty ? picture : _runner.Apply(frame.List, item.Fx, item.Id, picture, Frame(in frame, startNs));
    }

    public GpuTexture Layer(in PassFrame frame, Layer layer, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Begin(in frame);
        return layer.Fx.IsEmpty ? picture : _runner.Apply(frame.List, layer.Fx, layer.Id, picture, Frame(in frame, 0));
    }

    public GpuTexture Surface(in PassFrame frame, Surface surface, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Begin(in frame);
        return surface.Fx.IsEmpty ? picture : _runner.Apply(frame.List, surface.Fx, surface.Id, picture, Frame(in frame, 0));
    }

    public GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(output);
        Begin(in frame);
        _drawn.Add(output.Id);
        if (frame.Covered(output.Id))
        {
            _runner.Skip(output.Fx, output.Id, CoveredWords);
            return picture;
        }
        return output.Fx.IsEmpty ? picture : _runner.Apply(frame.List, output.Fx, output.Id, picture, Frame(in frame, 0));
    }

    /// <summary>Every enabled output whose chain the frame did not reach (blackout, a held picture, a scan frame) says why in its
    /// slots, then the runner lets go of what has been idle long enough.</summary>
    public void FrameEnd(in PassFrame frame)
    {
        Begin(in frame);
        var outputs = frame.Project.Outputs;
        for (var i = 0; i < outputs.Length; i++)
        {
            var o = outputs[i];
            if (o.Fx.IsEmpty || !o.Enabled || _runner.Met(o.Id))
                continue;
            _runner.Skip(o.Fx, o.Id, frame.Blackout ? BlackWords : frame.Held(o.Id) ? null : OtherWords);
        }
        _runner.EndFrame();
    }

    private FxFrame Frame(in PassFrame frame, long ownerOriginNs) =>
        new(frame.Frame, frame.LeaderNs, frame.Generator.FrameRate, UnixMs, ownerOriginNs);

    private void Begin(in PassFrame frame)
    {
        if (frame.Frame == _frame)
            return;
        if (_frame >= 0)
            ReportUnmet(in _drawing);
        _frame = frame.Frame;
        _drawing = frame;
        _drawn.Clear();
        Lane.Collect(frame.Frame, frame.List);
        _runner.BeginFrame(frame.Frame, frame.Project);
    }

    /// <summary>The layer, surface and item chains a wholly drawn frame did not reach, each with why: kept off the air by the air
    /// (said and kept), or out of the picture (said and let go). Allocates nothing once the slots and the walk's tables are known.</summary>
    private void ReportUnmet(in PassFrame frame)
    {
        var project = frame.Project;
        MarkOffAir(in frame);
        foreach (var canvas in project.Canvases)
        {
            foreach (var layer in canvas.Layers)
            {
                if (layer.Fx.IsEmpty || _runner.Met(layer.Id))
                    continue;
                if (!layer.Visible || layer.Opacity <= 0 || layer.Rect.IsEmpty)
                    _runner.Report(layer.Fx, LayerHiddenWords);
                else if (_offCanvases.TryGetValue(canvas.Id, out var why))
                    _runner.Skip(layer.Fx, layer.Id, Words(why));
                else
                    _runner.Report(layer.Fx, LayerUnshownWords);
            }
        }
        foreach (var surface in project.Stage.Surfaces)
        {
            if (surface.Fx.IsEmpty || _runner.Met(surface.Id))
                continue;
            if (!surface.Visible || surface.Opacity <= 0)
                _runner.Report(surface.Fx, SurfaceHiddenWords);
            else if (surface.Pixel is not null && surface.Content.Source.IsNone)
                _runner.Report(surface.Fx, QuadCardWords);
            else if (IsSurfaceEffect(project, surface.Content.Source))
                _runner.Report(surface.Fx, SurfaceEffectWords);
            else if (_offSurfaces.TryGetValue(surface.Id, out var why))
                _runner.Skip(surface.Fx, surface.Id, Words(why));
            else
                _runner.Report(surface.Fx, SurfaceUnshownWords);
        }
        foreach (var playlist in project.Playlists)
        {
            var off = _offPlaylists.TryGetValue(playlist.Id, out var why);
            foreach (var item in playlist.Items)
            {
                if (item.Fx.IsEmpty || _runner.Met(item.Id))
                    continue;
                // Of a playlist the air keeps off, only the item it was showing when the air took it is kept.
                if (off && _runner.MetLast(item.Id))
                    _runner.Skip(item.Fx, item.Id, Words(why));
                else
                    _runner.Report(item.Fx, ItemUnshownWords);
            }
        }
    }

    private static string? Words(OffAir why) => why switch
    {
        OffAir.Black => BlackWords,
        OffAir.Covered => CoveredWords,
        OffAir.Other => OtherWords,
        _ => null,
    };

    private static bool IsSurfaceEffect(Project project, SourceRef source) =>
        source.Kind == SourceKind.Generator && project.FindGenerator(source.Id) is { } g && EffectUniforms.IsEffect(g.Kind);

    /// <summary>The canvases, playlists and surfaces an output the air kept off the show this frame would have shown (and, for a
    /// covered output, what covers it), each with the strongest reason among those outputs.</summary>
    private void MarkOffAir(in PassFrame frame)
    {
        _offCanvases.Clear();
        _offPlaylists.Clear();
        _offSurfaces.Clear();
        var project = frame.Project;
        foreach (var o in project.Outputs)
        {
            if (!o.Enabled || o.Width < 1 || o.Height < 1)
                continue;
            OffAir why;
            if (frame.Blackout)
                why = OffAir.Black;
            else if (!_drawn.Contains(o.Id))
                why = frame.Held(o.Id) ? OffAir.Held : OffAir.Other;
            else if (frame.Covered(o.Id))
                why = OffAir.Covered;
            else
                continue;
            switch (o.Mode)
            {
                case OutputMode.Projector when o.ProjectorId is { } pid && project.FindProjector(pid) is not null:
                    foreach (var s in project.Stage.Surfaces)
                    {
                        if (!s.Visible || s.Opacity <= 0 || (s.Pixel is not null && !string.Equals(s.Pixel.ProjectorId, pid, StringComparison.Ordinal)))
                            continue;
                        Mark(_offSurfaces, s.Id, why);
                        MarkSource(project, s.Content.Source, why, 0);
                    }
                    break;
                case OutputMode.Canvas:
                    MarkSource(project, SourceKind.Canvas, o.Source.Id, why, 0);
                    break;
                case OutputMode.Source:
                    MarkSource(project, o.Source, why, 0);
                    break;
            }
            if (why == OffAir.Covered)
            {
                if (frame.Levels?.Override(o.Id, frame.LeaderNs) is { IsNone: false } safe)
                    MarkSource(project, safe, why, 0);
                else if (frame.PatternOverride is { IsNone: false } pattern)
                    MarkSource(project, pattern, why, 0);
            }
        }
    }

    private void MarkSource(Project project, SourceRef source, OffAir why, int depth) => MarkSource(project, source.Kind, source.Id, why, depth);

    private void MarkSource(Project project, SourceKind kind, string id, OffAir why, int depth)
    {
        if (depth > MaxReach)
            return;
        if (kind == SourceKind.Canvas)
        {
            if (!Mark(_offCanvases, id, why) || project.FindCanvas(id) is not { } canvas)
                return;
            foreach (var layer in canvas.Layers)
                if (layer.Visible && layer.Opacity > 0)
                    MarkSource(project, layer.Source, why, depth + 1);
        }
        else if (kind == SourceKind.Playlist)
        {
            if (!Mark(_offPlaylists, id, why) || project.FindPlaylist(id) is not { } list)
                return;
            foreach (var item in list.Items)
                MarkSource(project, item.Source, why, depth + 1);
        }
    }

    /// <summary>Marks an id with a reason unless a reason as strong is there; true when it was marked (its walk goes on).</summary>
    private static bool Mark(Dictionary<string, OffAir> marks, string id, OffAir why)
    {
        if (marks.TryGetValue(id, out var had) && had >= why)
            return false;
        marks[id] = why;
        return true;
    }

    /// <summary>Lets go of every target, picture and program the pass holds; render thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runner.Dispose();
        Lane.Dispose();
    }
}
