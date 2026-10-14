using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Effects;

/// <summary>The chain call points for a layer's and a surface's picture (an output's is <see cref="IFramePass.Picture"/>): a
/// renderer that composes layers or textures surfaces calls these with the picture it made and draws what comes back.</summary>
public interface IFxAttachPoints
{
    GpuTexture Layer(in PassFrame frame, Layer layer, GpuTexture picture);

    GpuTexture Surface(in PassFrame frame, Surface surface, GpuTexture picture);
}

/// <summary>Effect chains in the frame: an output's chain runs on its picture before the output stage (warp, blend, trims),
/// never after it. What the air shows instead of the show — a test pattern, a panic's safe look — never passes through a chain;
/// blackout, raw scan frames and the identify card never reach a pass at all. The pass owns the compile lane and the chain
/// runner; it is made, drawn and disposed on the render thread.</summary>
public sealed class FxPass : IFramePass, IFxAttachPoints
{
    internal const string CoveredWords = "not drawn: a test pattern or safe look covers its output";
    internal const string BlackWords = "not drawn: the air is black";
    internal const string OtherWords = "not drawn: its output shows a scan frame or has no size";

    private readonly FxChainRunner _runner;
    private FrameInputs _air;
    private long _frame = -1;
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

    /// <summary>The frame's air as the renderer will draw it (its pattern override and levels): call before each Render.</summary>
    public void Prepare(in FrameInputs inputs) => _air = inputs;

    public GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(output);
        Begin(in frame);
        if (output.Fx.IsEmpty)
            return picture;
        if (_air.PatternCovers(output.Id) || _air.Levels?.Override(output.Id, frame.LeaderNs) is { IsNone: false })
        {
            _runner.Skip(output.Fx, output.Id, CoveredWords);
            return picture;
        }
        return _runner.Apply(output.Fx, output.Id, picture, Frame(in frame, 0));
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
            var held = frame.Freeze || (_air.Levels?.Hold(o.Id, frame.LeaderNs) ?? false);
            _runner.Skip(o.Fx, o.Id, frame.Blackout ? BlackWords : held ? null : OtherWords);
        }
        _runner.EndFrame();
    }

    private FxFrame Frame(in PassFrame frame, long ownerOriginNs) =>
        new(frame.Frame, frame.LeaderNs, frame.Generator.FrameRate, UnixMs, ownerOriginNs);

    private void Begin(in PassFrame frame)
    {
        if (frame.Frame == _frame)
            return;
        _frame = frame.Frame;
        Lane.Collect(frame.Frame);
        _runner.BeginFrame(frame.Frame, frame.Project);
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
