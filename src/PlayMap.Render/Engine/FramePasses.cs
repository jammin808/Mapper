using System.Collections.Immutable;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Render.Passes;

namespace PlayMap.Render.Engine;

/// <summary>Per-output air the render thread reads every frame: built on the lane once per air change, allocation-free to read.</summary>
public interface IAirLevels
{
    /// <summary>Master × panic level × degrade gain at the frame's leader instant (1 = untouched).</summary>
    float Gain(string outputId, long leaderNs);

    /// <summary>The safe look while a SAFE outcome shows it, else null.</summary>
    SourceRef? Override(string outputId, long leaderNs);

    /// <summary>Hold this output's last picture (the FREEZE outcome).</summary>
    bool Hold(string outputId, long leaderNs);

    /// <summary>The air's sound gain (master × panic) at the leader instant, from the same fades as the picture.</summary>
    float AudioGain(long leaderNs) => 1f;
}

/// <summary>Per output, the measured correction the output stage draws (wp2-align fills it; built on the lane, read lock-free).</summary>
public interface IMeasuredStages
{
    MeasuredStage? For(string outputId);
}

/// <summary>A pass a package adds to the frame. Every method runs on the render thread, must not block, and allocates nothing
/// once warm. Blackout wins before any pass; raw scan frames skip every picture call and <see cref="Final"/>.
///
/// The picture calls come in the order the frame makes its pictures — an item's, a layer's and a surface's while the sources
/// are resolved, an output's last — and each pass is handed the last one's result, in <see cref="Order"/>. A layer, a surface
/// and an item are each called at most once a frame however many outputs show them (a canvas is composed once a frame, a
/// chained surface's and an item's pictures are made once a frame), so a pass that integrates from frame to frame steps once a
/// frame. Previews drawn after the frame call no pass: they show what the frame made, or the picture without its passes.
/// What the air shows instead of the show (a test pattern, a safe look) is resolved apart from the show with no pass — a canvas
/// or playlist in it is composed or mixed without its layers' and items' calls — so no picture call meets it but
/// <see cref="Picture"/>, whose <see cref="PassFrame.Covered"/> says so.</summary>
public interface IFramePass : IDisposable
{
    string Name { get; }

    int Order { get; }

    /// <summary>A playlist item's picture while its playlist shows it (each side of a transition on its own, before the mix): may
    /// return another picture (an effect chain's result). <paramref name="startNs"/> is the leader instant the item started (its
    /// in point on its clip's anchor), the same on every node.</summary>
    GpuTexture Item(in PassFrame frame, PlaylistItem item, long startNs, GpuTexture picture) => picture;

    /// <summary>A canvas layer's picture, its source resolved at the layer's rectangle, before anything is drawn into the canvas:
    /// may return another picture (an effect chain's result), drawn at the layer's rectangle with its opacity and blend.</summary>
    GpuTexture Layer(in PassFrame frame, Layer layer, GpuTexture picture) => picture;

    /// <summary>A stage surface's content picture, made at one size for the frame (the largest projector raster) before any
    /// projector pass draws it: may return another picture (an effect chain's result), which every projector samples through the
    /// surface's UVs.</summary>
    GpuTexture Surface(in PassFrame frame, Surface surface, GpuTexture picture) => picture;

    /// <summary>After an output's picture is made, before its output stage (warp, blend, trim): may return another picture
    /// (an effect chain's result), drawn on <paramref name="picture"/>'s own scale; the overlay's pre-warp layer draws here.</summary>
    GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture) => picture;

    /// <summary>After the output stage drew the output's final raster (and its identify card): the post-warp overlay, final taps.</summary>
    void Final(in PassFrame frame, Output output, RenderTarget final)
    {
        // A pass with nothing to draw over the final raster.
    }

    /// <summary>After every output is drawn, before any present: LED sampling, send packing and readback.</summary>
    void FrameEnd(in PassFrame frame)
    {
        // A pass with nothing to do once every output is drawn.
    }
}

/// <summary>Whether an effect chain changes a picture.</summary>
internal static class FxChains
{
    /// <summary>True when a slot of the chain draws: one not bypassed and mixed above 0. A chain with none hands every picture on
    /// bit for bit, so its owner's picture is made exactly as if it had no chain.</summary>
    public static bool Draws(FxChain chain)
    {
        if (chain.IsEmpty)
            return false;
        foreach (var slot in chain.Slots)
            if (!slot.Bypass && slot.Mix > 0)
                return true;
        return false;
    }
}

/// <summary>What a pass is told about the frame: its number, its leader instant, the project it draws, the generators' frame, the
/// device, whether the air is black or frozen, and per output whether the air shows something other than the show
/// (<see cref="Covered"/>) or holds its last picture (<see cref="Held"/>). A value; reading it allocates nothing.</summary>
public readonly record struct PassFrame(long Frame, long LeaderNs, Project Project, GeneratorFrame Generator, GlDevice Device,
    bool Blackout, bool Freeze)
{
    /// <summary>The air's test pattern, or null when none is up.</summary>
    public SourceRef? PatternOverride { get; init; }

    /// <summary>The outputs <see cref="PatternOverride"/> covers; empty = every output.</summary>
    public ImmutableArray<string> PatternOutputs { get; init; }

    /// <summary>Per-output gain, safe look and hold (null = none).</summary>
    public IAirLevels? Levels { get; init; }

    /// <summary>True when the output shows the air's test pattern or a panic's safe look this frame instead of the show.</summary>
    public bool Covered(string outputId) =>
        FrameInputs.PatternCovers(PatternOverride, PatternOutputs, outputId) || Levels?.Override(outputId, LeaderNs) is { IsNone: false };

    /// <summary>True when the output holds its last picture this frame (FREEZE, or a FREEZE outcome on it).</summary>
    public bool Held(string outputId) => Freeze || (Levels?.Hold(outputId, LeaderNs) ?? false);
}
