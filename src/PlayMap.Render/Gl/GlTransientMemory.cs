using System.Globalization;
using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Gl;

/// <summary>A plan's memory block on GL: each placement is rented from the backend's resource pool and the offset is only checked
/// against the block (GL places nothing: two transients at one offset are two pooled textures, never aliased). A placement's size is
/// its texels' bytes (or its blocks'), aligned to nothing. Disposing the block hands every placement it still holds back to the pool,
/// where the next plan rents it again.
///
/// Threading: the render thread (the GL context's).</summary>
internal sealed class GlTransientMemory : ITransientMemory
{
    private readonly ResourcePool _pool;
    private readonly List<GpuTexture> _textures = [];
    private readonly List<RenderTarget> _targets = [];
    private bool _disposed;

    public GlTransientMemory(ResourcePool pool, long bytes, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentNullException.ThrowIfNull(name);
        _pool = pool;
        Bytes = bytes;
        Name = name;
    }

    public long Bytes { get; }

    public string Name { get; }

    /// <summary>Placements held now (textures and targets).</summary>
    public int Placed => _textures.Count + _targets.Count;

    public (long Bytes, long Align) SizeOf(in TextureDesc desc) => (TextureFormats.LevelBytes(desc.Format, desc.Width, desc.Height), 1);

    public (long Bytes, long Align) SizeOf(in TargetDesc desc)
    {
        var bytes = TextureFormats.LevelBytes(desc.Color0, desc.Width, desc.Height);
        if (desc.Color1 is { } color1)
            bytes += TextureFormats.LevelBytes(color1, desc.Width, desc.Height);
        if (desc.Depth)
            bytes += 4L * desc.Width * desc.Height;
        return (bytes, 1);
    }

    public GpuTexture Place(in TextureDesc desc, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Fits(SizeOf(desc).Bytes, offset);
        var texture = _pool.RentTexture(desc);
        _textures.Add(texture);
        return texture;
    }

    public RenderTarget PlaceTarget(in TargetDesc desc, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Fits(SizeOf(desc).Bytes, offset);
        var target = _pool.RentTarget(desc);
        _targets.Add(target);
        return target;
    }

    private void Fits(long bytes, long offset)
    {
        if (offset < 0 || offset > Bytes - bytes)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, string.Create(CultureInfo.InvariantCulture,
                $"{bytes} bytes at {offset} do not fit the {Bytes}-byte block '{Name}'."));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var t in _textures)
        {
            if (!t.IsDisposed)
                _pool.Return(t);
        }
        foreach (var t in _targets)
        {
            if (!t.IsDisposed)
                _pool.Return(t);
        }
        _textures.Clear();
        _targets.Clear();
    }
}
