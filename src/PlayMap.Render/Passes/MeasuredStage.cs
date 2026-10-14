using System.Numerics;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Passes;

/// <summary>An output's measured correction as the output stage draws it (wp2-align): the camera-measured warp grid, the blend and
/// pedestal map, the per-channel gains and the γ. Built on the lane, read on the render thread; the textures are the render
/// thread's own.</summary>
public sealed class MeasuredStage
{
    /// <summary>Where each output pixel samples the picture, as a grid texture (null = the identity).</summary>
    public GpuTexture? WarpGrid { get; init; }

    /// <summary>The measured blend weight and pedestal per pixel (null = weight one, no pedestal).</summary>
    public GpuTexture? Blend { get; init; }

    /// <summary>Per-channel gains (r, g, b, and a master in w); one leaves the picture as it is.</summary>
    public Vector4 Gains { get; init; } = Vector4.One;

    /// <summary>The measured γ to correct for; one leaves the picture as it is.</summary>
    public float Gamma { get; init; } = 1f;

    /// <summary>The black pedestal raised under the picture (0 = none).</summary>
    public float Pedestal { get; init; }
}
