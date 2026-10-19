using System.Numerics;
using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Passes;

/// <summary>
/// An output's camera-measured correction as the output stage draws it (wp2-align). The warp grid holds canvas uv at a regular grid
/// of output positions — node (i, k) at (i/(C−1), k/(R−1)) — as (u, v, valid, 0) in a float texture read with texel fetches; it is the
/// first warp layer (the operator's warp moves the corrected picture) and replaces the output's region of the canvas (the uv is the
/// canvas's own). The blend holds (weight, pedestal, uniformity gain) in light at a quarter of the raster. The content's light (its
/// 2.2 times the gains) goes out through the output's own <see cref="Gamma"/>: with gains of one and a γ of 2.2 and no blend the
/// picture passes as it is, bit for bit. Made on the render thread, which owns and disposes its textures.
/// </summary>
public sealed class MeasuredStage : IDisposable
{
    /// <summary>The transfer the content is drawn for.</summary>
    public const float ContentGamma = 2.2f;

    /// <summary>The measured warp grid (null = the identity).</summary>
    public GpuTexture? WarpGrid { get; init; }

    /// <summary>The measured blend, weight and pedestal in light (null = none: the output's own blend applies).</summary>
    public GpuTexture? Blend { get; init; }

    /// <summary>Per-channel gains in linear light (r, g, b, and a master in w); one leaves the picture as it is.</summary>
    public Vector4 Gains { get; init; } = Vector4.One;

    /// <summary>The output's own γ (its signal to its light); 2.2 leaves the picture as it is.</summary>
    public float Gamma { get; init; } = ContentGamma;

    /// <summary>How much of the blend's pedestal is drawn (1 as measured, 0 none).</summary>
    public float Pedestal { get; init; } = 1f;

    /// <summary>The content's transfer turned into the output's: gains other than one, or a γ other than the content's.</summary>
    public bool Match => Gains != Vector4.One || MathF.Abs(Gamma - ContentGamma) > 1e-4f;

    /// <summary>A stage from float data: the grid (C × R nodes of u, v, valid) and the blend (W × H of weight, pedestal, gain), each
    /// three floats a texel, row-major; either may be null. Its textures are filled in a frame of their own: made between the
    /// renderer's frames.</summary>
    public static MeasuredStage Upload(IGpuDevice device, (int Columns, int Rows, float[] Values)? grid, (int Width, int Height, float[] Values)? blend,
        Vector4 gains, float gamma, float pedestal)
    {
        ArgumentNullException.ThrowIfNull(device);
        GpuTexture? g = null;
        GpuTexture? b = null;
        var list = device.BeginFrame(0);
        try
        {
            if (grid is { } gr)
                g = Texture(device, list, gr.Columns, gr.Rows, gr.Values);
            if (blend is { } bl)
                b = Texture(device, list, bl.Width, bl.Height, bl.Values);
            var stage = new MeasuredStage { WarpGrid = g, Blend = b, Gains = gains, Gamma = gamma > 0 ? gamma : ContentGamma, Pedestal = pedestal };
            g = null;
            b = null;
            return stage;
        }
        finally
        {
            device.Submit(list);
            g?.Dispose();
            b?.Dispose();
        }
    }

    private static GpuTexture Texture(IGpuDevice device, ICommandList list, int width, int height, float[] rgb)
    {
        if (width < 1 || height < 1 || rgb.Length != width * height * 3)
            throw new ArgumentException("three floats a texel");
        var rgba = new float[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[4 * i] = rgb[3 * i];
            rgba[4 * i + 1] = rgb[3 * i + 1];
            rgba[4 * i + 2] = rgb[3 * i + 2];
        }
        var tex = device.CreateTexture(new TextureDesc(width, height, TextureFormat.Rgba32F, FrameLists.Uploaded, TextureFilter.Nearest));
        var uploaded = false;
        try
        {
            list.Upload(tex, System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(rgba), UploadFormat.Rgba32F);
            uploaded = true;
            return tex;
        }
        finally
        {
            if (!uploaded)
                tex.Dispose();
        }
    }

    /// <summary>The measured path on the CPU for one channel, as the shader draws it before the trim: a content signal, the blend's
    /// weight and pedestal (null: no blend), the gain, the output's γ.</summary>
    public static float Reference(float content, (float Weight, float Pedestal)? blend, float gain, float gamma, float pedestal = 1f)
    {
        var c = MathF.Max(content, 0);
        if (blend is { } b)
        {
            var lin = MathF.Pow(c, ContentGamma) * gain;
            var ped = b.Pedestal * pedestal;
            lin = ped + (1 - ped) * b.Weight * lin;
            return MathF.Pow(MathF.Max(lin, 0), 1 / gamma);
        }
        if (gain == 1f && MathF.Abs(gamma - ContentGamma) <= 1e-4f)
            return c;
        return MathF.Pow(MathF.Pow(c, ContentGamma) * gain, 1 / gamma);
    }

    public void Dispose()
    {
        WarpGrid?.Dispose();
        Blend?.Dispose();
    }
}
