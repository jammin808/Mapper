using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Effects;

/// <summary>Effect chains in the frame. An output's chain runs on its picture before the output stage (warp, blend, trims), never
/// after it; a layer's on its picture before its canvas is drawn into; a surface's on its content once a frame, which every
/// projector samples; a playlist item's on its picture while its playlist shows it, its time counting from the item's start.
/// What the air shows instead of the show — a test pattern, a panic's safe look — never passes through a chain; blackout, raw
/// scan frames and the identify card never reach a pass at all.
///
/// A chain whose owner the frame did not draw says why in its slots. An output's says so when the frame ends; a layer's, a
/// surface's and an item's once the frame is wholly drawn (when the next begins, so a picture a later pass taps in its FrameEnd
/// counts). While the air keeps a chain off it (black, a hold, a test pattern or safe look over every output that would show the
/// show) the chain keeps its programs and buffers and draws on the first frame back; a chain whose owner is out of the picture (a
/// layer or surface that is not visible, a canvas nothing shows, an item its playlist is not showing) lets them go once unused
/// for <see cref="FxChainRunner.IdleFrames"/> frames. The pass owns the compile lane and the chain runner; it is made, drawn and
/// disposed on the render thread.</summary>
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

    private readonly FxChainRunner _runner;
    private PassFrame _drawing;
    private long _frame = -1;
    private int _shown;
    private int _covered;
    private bool _disposed;

    /// <param name="context">The render context, current on the calling thread.</param>
    /// <param name="sharedContextLane">False compiles on the render thread (the lane's fallback).</param>
    public FxPass(RenderContext context, bool sharedContextLane = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        Lane = new FxCompileLane(context, sharedContextLane);
        _runner = new FxChainRunner(context.Device, Lane);
    }

    public string Name => "fx";

    /// <summary>After the packages that draw the picture, before overlays that draw on it.</summary>
    public int Order => 100;

    public FxCompileLane Lane { get; }

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
        return item.Fx.IsEmpty ? picture : _runner.Apply(item.Fx, item.Id, picture, Frame(in frame, startNs));
    }

    public GpuTexture Layer(in PassFrame frame, Layer layer, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Begin(in frame);
        return layer.Fx.IsEmpty ? picture : _runner.Apply(layer.Fx, layer.Id, picture, Frame(in frame, 0));
    }

    public GpuTexture Surface(in PassFrame frame, Surface surface, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Begin(in frame);
        return surface.Fx.IsEmpty ? picture : _runner.Apply(surface.Fx, surface.Id, picture, Frame(in frame, 0));
    }

    public GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(output);
        Begin(in frame);
        if (frame.Covered(output.Id))
        {
            _covered++;
            _runner.Skip(output.Fx, output.Id, CoveredWords);
            return picture;
        }
        _shown++;
        return output.Fx.IsEmpty ? picture : _runner.Apply(output.Fx, output.Id, picture, Frame(in frame, 0));
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
        _shown = 0;
        _covered = 0;
        Lane.Collect(frame.Frame);
        _runner.BeginFrame(frame.Frame, frame.Project);
    }

    /// <summary>The layer, surface and item chains a wholly drawn frame did not reach, each with why: out of the picture (said and
    /// let go), or kept off the air by the air itself (said and kept). Allocates nothing once the slots are known.</summary>
    private void ReportUnmet(in PassFrame frame)
    {
        var project = frame.Project;
        // The air kept the show off every output: black, frozen, or covered (or held) wherever it would have shown.
        var byAir = frame.Blackout || frame.Freeze || (_shown == 0 && (_covered > 0 || AnyHeld(in frame)));
        var airWords = frame.Blackout ? BlackWords : frame.Freeze || _covered == 0 ? null : CoveredWords;
        foreach (var canvas in project.Canvases)
        {
            foreach (var layer in canvas.Layers)
            {
                if (layer.Fx.IsEmpty || _runner.Met(layer.Id))
                    continue;
                if (!layer.Visible || layer.Opacity <= 0 || layer.Rect.IsEmpty)
                    _runner.Report(layer.Fx, LayerHiddenWords);
                else if (byAir)
                    _runner.Skip(layer.Fx, layer.Id, airWords);
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
            else if (surface.Content.Source.Kind == SourceKind.Generator && project.FindGenerator(surface.Content.Source.Id) is { } g
                     && EffectUniforms.IsEffect(g.Kind))
                _runner.Report(surface.Fx, SurfaceEffectWords);
            else if (byAir)
                _runner.Skip(surface.Fx, surface.Id, airWords);
            else
                _runner.Report(surface.Fx, SurfaceUnshownWords);
        }
        foreach (var playlist in project.Playlists)
        {
            foreach (var item in playlist.Items)
            {
                if (item.Fx.IsEmpty || _runner.Met(item.Id))
                    continue;
                if (byAir)
                    _runner.Skip(item.Fx, item.Id, airWords);
                else
                    _runner.Report(item.Fx, ItemUnshownWords);
            }
        }
    }

    private static bool AnyHeld(in PassFrame frame)
    {
        foreach (var o in frame.Project.Outputs)
            if (o.Enabled && frame.Held(o.Id))
                return true;
        return false;
    }

    /// <summary>Stops the lane and deletes every target and program; render thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runner.Dispose();
        Lane.Dispose();
    }
}
