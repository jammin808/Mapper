using PlayMap.Core.Gpu;

namespace PlayMap.Render.Engine;

/// <summary>A pass over a target on the frame's list that ends when the scope does (<c>using (list.Pass(target, desc))</c>), so a draw
/// that throws never leaves the list inside a pass. A value: opening one allocates nothing.</summary>
internal readonly ref struct PassScope
{
    private readonly ICommandList _list;

    public PassScope(ICommandList list, RenderTarget target, in PassDesc pass)
    {
        list.BeginPass(target, pass);
        _list = list;
    }

    public void Dispose() => _list.EndPass();
}

/// <summary>The frame code's words over a command list and a device: a pass that ends with its scope, the format a target is made in
/// where the device renders no other, and a quad's fixed state.</summary>
internal static class FrameLists
{
    /// <summary>The usage of a texture the frame code fills from the CPU and samples.</summary>
    public const TextureUsage Uploaded = TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest;

    /// <summary>A pass over <paramref name="target"/> that ends when the scope does.</summary>
    public static PassScope Pass(this ICommandList list, RenderTarget target, in PassDesc pass) => new(list, target, pass);

    /// <summary>A pass that clears its target to a colour first.</summary>
    public static PassDesc Clear(float r, float g, float b, float a) => new(LoadOp.Clear, new System.Numerics.Vector4(r, g, b, a));

    /// <summary>The state of a generated quad (a four-vertex strip) drawn with a blend: no depth, no culling.</summary>
    public static DrawState Quads(BlendMode blend) => new(blend, DepthMode.Off, CullMode.None, Topology.TriangleStrip);

    /// <summary>The format a target asked in <paramref name="format"/> is made in on this device: the format itself where the device
    /// renders to it, else its fallback — 16-bit unsigned to 16-bit float where float targets render, a float or 16-bit target to
    /// 8-bit of the same channels, anything else to RGBA8.</summary>
    public static TextureFormat TargetFormat(this IGpuDevice device, TextureFormat format)
    {
        var formats = device.Features.Formats;
        if (formats.TryGetValue(format, out var asked) && asked.Target)
            return format;
        var floats = formats.TryGetValue(TextureFormat.Rgba16F, out var half) && half.Target;
        return format switch
        {
            TextureFormat.R16 when floats => TextureFormat.R16F,
            TextureFormat.Rg16 when floats => TextureFormat.Rg16F,
            TextureFormat.Rgba16 when floats => TextureFormat.Rgba16F,
            TextureFormat.R16F or TextureFormat.R32F or TextureFormat.R16 => TextureFormat.R8,
            TextureFormat.Rg16F or TextureFormat.Rg32F or TextureFormat.Rg16 => TextureFormat.Rg8,
            _ => TextureFormat.Rgba8,
        };
    }
}
