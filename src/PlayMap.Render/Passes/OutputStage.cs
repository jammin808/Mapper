using System.Numerics;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Render.Projection;

namespace PlayMap.Render.Passes;

/// <summary>What the output stage applies after the picture: its corners in the pre-warp picture's texture
/// space, the warp, the output mask, the blend map (weight and pedestal), the lens lookup, the trim, the
/// display rotation and the dither amplitude.</summary>
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
    long Frame);

/// <summary>The last pass of an output: the pre-warp picture drawn through the warp mesh into the output's
/// 8-bit picture. In float, in order: the lens lookup (when there is one), the picture (black outside it),
/// × the output mask × the blend weight, + the black pedestal (masked), then the trim — gamma as an exponent,
/// gains × brightness, contrast about mid-grey, lift — then a dither under half a code. A neutral trim and an
/// identity warp give back the picture's own codes exactly. Rotation turns the result onto the display's
/// raster by quarter turns, clockwise.</summary>
internal sealed class OutputStage
{
    private readonly RenderServices _services;

    public OutputStage(RenderServices services)
    {
        _services = services;
    }

    /// <summary>The rotation in quarter turns (0–3) for an output's degrees (anything not a multiple of 90
    /// rounds to the nearest).</summary>
    public static int Quarters(int degrees) => (int)(((Math.Round(degrees / 90.0) % 4) + 4) % 4);

    public void Draw(RenderTarget target, in OutputStageInputs input)
    {
        ArgumentNullException.ThrowIfNull(target);
        var d = _services.Device;
        d.BindTarget(target);
        d.SetBlend(BlendMode.Opaque);
        d.SetDepth(false, false);
        d.SetCull(CullMode.None);
        d.Clear(0, 0, 0, 1);
        var p = d.Program("output");
        p.SetInt("uRotation", input.RotationQuarters);
        p.Set("uSrc", input.SourceRect);
        d.BindSampler(p, "uPicture", 0, input.Picture);
        p.SetInt("uHasLens", input.Lens is null ? 0 : 1);
        d.BindSampler(p, "uLens", 1, input.Lens ?? _services.White);
        p.SetInt("uHasMask", input.Mask is null ? 0 : 1);
        d.BindSampler(p, "uMask", 2, input.Mask ?? _services.White);
        p.SetInt("uHasBlend", input.Blend is null ? 0 : 1);
        d.BindSampler(p, "uBlend", 3, input.Blend ?? _services.White);
        var t = input.Trim;
        var b = (float)t.Brightness;
        p.Set("uGain", (float)t.GainR * b, (float)t.GainG * b, (float)t.GainB * b, 1);
        p.Set("uTone", (float)t.Contrast, (float)t.Gamma, (float)t.Lift, input.Dither);
        p.Set("uFrame", input.Frame % 4096);
        d.DrawIndexed(p, input.Warp.Vao, PrimitiveKind.Triangles, input.Warp.IndexCount);
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
