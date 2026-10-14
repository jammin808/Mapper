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
/// once warm. Blackout wins before any pass; raw scan frames skip <see cref="Picture"/> and <see cref="Final"/>.</summary>
public interface IFramePass : IDisposable
{
    string Name { get; }

    int Order { get; }

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

/// <summary>What a pass is told about the frame: its number, its leader instant, the project it draws, the generators' frame, the
/// device, and whether the air is black or frozen.</summary>
public readonly record struct PassFrame(long Frame, long LeaderNs, Project Project, GeneratorFrame Generator, GlDevice Device,
    bool Blackout, bool Freeze);
