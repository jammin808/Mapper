using System.Numerics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Projection;

namespace PlayMap.Render.Passes;

/// <summary>What the output stage applies after the picture: its corners in the pre-warp picture's texture
/// space, the warp, the output mask, the blend map (weight and pedestal), the lens lookup, the trim, the
/// display rotation and the dither amplitude; then the air's gain.</summary>
internal readonly record struct OutputStageInputs(
    GpuTexture Picture,
    Vector4 SourceRect,
    GpuWarp Warp,
    GpuTexture? Mask,
    GpuTexture? Blend,
    GpuTexture? Lens,
    ColorTrim Trim,
    int RotationQuarters,
    float Dither,
    long Frame)
{
    /// <summary>The air's gain multiplied last, after the pedestal and the dither, so 0 is true black (wp2-show's levels). Null = 1.</summary>
    public float? Gain { get; init; }

    /// <summary>The output's measured correction as the stage draws it (wp2-align: grid, blend and pedestal, gains, γ). Null = none.</summary>
    public MeasuredStage? Measured { get; init; }
}

/// <summary>The last pass of an output: the pre-warp picture drawn through the warp mesh into the output's
/// 8-bit picture. In float, in order: the camera-measured warp and the lens lookup (when there are), the picture (black outside it),
/// × the output mask × the blend weight, + the black pedestal (masked) — or, with a measured blend, the same in light through the
/// output's measured γ (<see cref="MeasuredStage"/>) — then the trim — gamma as an exponent,
/// gains × brightness, contrast about mid-grey, lift — then a dither under half a code. A neutral trim and an
/// identity warp give back the picture's own codes exactly. Rotation turns the result onto the display's
/// raster by quarter turns, clockwise. Last, the air's gain multiplies every channel (1 when no levels are given), so a gain of 0
/// is black whatever the pedestal and the dither.</summary>
internal sealed class OutputStage
{
    /// <summary>The stage's state: the warp mesh's triangles written opaque, no depth, no culling.</summary>
    private static readonly DrawState Warped = DrawState.Opaque;

    /// <summary>The stage's draw: into an output's final picture, and into a projector's preview.</summary>
    internal static void Pipelines(PipelineList keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("output", Warped, TargetShape.Rgba8, TargetShape.Rgba8Depth);
    }

    private readonly RenderServices _services;
    private readonly ProgramId _output;

    public OutputStage(RenderServices services)
    {
        _services = services;
        _output = services.Device.Programs.Id("output");
    }

    /// <summary>The rotation in quarter turns (0–3) for an output's degrees (anything not a multiple of 90
    /// rounds to the nearest).</summary>
    public static int Quarters(int degrees) => (int)(((Math.Round(degrees / 90.0) % 4) + 4) % 4);

    /// <summary>Draws the stage into <paramref name="target"/> in a pass of the frame's list that clears it to black first.</summary>
    public void Draw(ICommandList list, RenderTarget target, in OutputStageInputs input)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(target);
        var measured = input.Measured;
        var t = input.Trim;
        var b = (float)t.Brightness;
        using (list.Pass(target, PassDesc.ClearBlack))
            list.Draw(_output, Warped)
                .SetInt(ProgramIds.Output.uRotation, input.RotationQuarters)
                // The measured grid holds the canvas's own uv: it replaces the output's region of the picture.
                .Set(ProgramIds.Output.uSrc, measured?.WarpGrid is null ? input.SourceRect : new Vector4(0, 0, 1, 1))
                .Texture(ProgramIds.Output.uPicture, input.Picture)
                .SetInt(ProgramIds.Output.uHasLens, input.Lens is null ? 0 : 1)
                .Texture(ProgramIds.Output.uLens, input.Lens ?? _services.White)
                .SetInt(ProgramIds.Output.uHasMask, input.Mask is null ? 0 : 1)
                .Texture(ProgramIds.Output.uMask, input.Mask ?? _services.White)
                .SetInt(ProgramIds.Output.uHasBlend, input.Blend is null ? 0 : 1)
                .Texture(ProgramIds.Output.uBlend, input.Blend ?? _services.White)
                .Set(ProgramIds.Output.uGain, (float)t.GainR * b, (float)t.GainG * b, (float)t.GainB * b, 1)
                .Set(ProgramIds.Output.uTone, (float)t.Contrast, (float)t.Gamma, (float)t.Lift, input.Dither)
                .Set(ProgramIds.Output.uFrame, input.Frame % 4096)
                .SetInt(ProgramIds.Output.uHasMeasuredGrid, measured?.WarpGrid is null ? 0 : 1)
                .Texture(ProgramIds.Output.uMeasuredGrid, measured?.WarpGrid ?? _services.White)
                .SetInt(ProgramIds.Output.uHasMeasuredBlend, measured?.Blend is null ? 0 : 1)
                .Texture(ProgramIds.Output.uMeasuredBlend, measured?.Blend ?? _services.White)
                .SetInt(ProgramIds.Output.uMatch, measured is { Match: true } ? 1 : 0)
                .Set(ProgramIds.Output.uMeasuredGains, measured?.Gains ?? Vector4.One)
                // A γ of 0 would black the output: unset, it reads 0, so it is set on every draw, as uAir is.
                .Set(ProgramIds.Output.uMeasuredGamma, measured?.Gamma ?? MeasuredStage.ContentGamma)
                .Set(ProgramIds.Output.uMeasuredPedestal, measured?.Pedestal ?? 0f)
                .Set(ProgramIds.Output.uAir, input.Gain ?? 1f)
                .Indexed(input.Warp.Mesh, input.Warp.IndexCount);
        target.Color.Version++;
    }

    /// <summary>The trim on the CPU, as the shader does it (tests, and a desk's preview of a trim).</summary>
    public static float Trim(float v, ColorTrim trim, float gain)
    {
        ArgumentNullException.ThrowIfNull(trim);
        var c = v;
        if (trim.Gamma != 1)
            c = MathF.Pow(MathF.Max(c, 0), (float)trim.Gamma);
        c *= gain * (float)trim.Brightness;
        if (trim.Contrast != 1)
            c = (c - 0.5f) * (float)trim.Contrast + 0.5f;
        if (trim.Lift != 0)
            c += (float)trim.Lift * (1 - c);
        return Math.Clamp(c, 0, 1);
    }
}
