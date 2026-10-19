using PlayMap.Core.Gpu;

namespace PlayMap.Render.Engine;

/// <summary>Render targets and textures kept for reuse, keyed by their shape (size, formats, depth, filter, wrap and uses): a pass
/// takes one for a frame and gives it back, so the frame path makes no GPU objects once the show is warm. Objects idle for more than
/// <see cref="IdleFramesBeforeRelease"/> frames are deleted by <see cref="Trim"/>. It makes what it lends through the device it is
/// given, so the same pool serves every backend; the device's own thread rules apply (GL: the render thread).</summary>
public sealed class ResourcePool : IDisposable
{
    public const int IdleFramesBeforeRelease = 120;

    /// <summary>What a texture rented by size and format alone is made for: sampled, copied from and into.</summary>
    public const TextureUsage TextureUses = TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest;

    private readonly record struct Key(int Width, int Height, TextureFormat Format, TextureFormat? Color1, bool Depth, TextureFilter Filter,
        TextureWrap Wrap, TextureUsage Usage);

    private sealed class Shelf<T>
    {
        public readonly Stack<(T Item, long Frame)> Free = new();
    }

    private readonly IGpuDevice _device;
    private readonly Dictionary<Key, Shelf<RenderTarget>> _targets = [];
    private readonly Dictionary<Key, Shelf<GpuTexture>> _textures = [];
    private readonly HashSet<RenderTarget> _targetsOut = [];
    private readonly HashSet<GpuTexture> _texturesOut = [];
    private long _frame;
    private bool _disposed;

    public ResourcePool(IGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>Objects lent out now.</summary>
    public int Lent => _targetsOut.Count + _texturesOut.Count;

    /// <summary>Objects resting on the shelves.</summary>
    public int Resting
    {
        get
        {
            var n = 0;
            foreach (var s in _targets.Values)
                n += s.Free.Count;
            foreach (var s in _textures.Values)
                n += s.Free.Count;
            return n;
        }
    }

    public RenderTarget RentTarget(int width, int height, TextureFormat format, bool depth = false) =>
        RentTarget(new TargetDesc(width, height, format, Depth: depth));

    /// <summary>A target of the shape asked: one resting on its shelf, else a new one.</summary>
    public RenderTarget RentTarget(in TargetDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new Key(desc.Width, desc.Height, desc.Color0, desc.Color1, desc.Depth, desc.Filter, TextureWrap.Clamp, TextureUsage.None);
        RenderTarget target;
        if (_targets.TryGetValue(key, out var shelf) && shelf.Free.Count > 0)
            target = shelf.Free.Pop().Item;
        else
            target = _device.CreateTarget(desc);
        _targetsOut.Add(target);
        return target;
    }

    public void Return(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_disposed || !_targetsOut.Remove(target))
            return;
        var key = new Key(target.Width, target.Height, target.Format, target.Color1?.Format, target.HasDepth, target.Color.Filter,
            TextureWrap.Clamp, TextureUsage.None);
        if (!_targets.TryGetValue(key, out var shelf))
            _targets[key] = shelf = new Shelf<RenderTarget>();
        shelf.Free.Push((target, _frame));
    }

    public GpuTexture RentTexture(int width, int height, TextureFormat format) =>
        RentTexture(new TextureDesc(width, height, format, TextureUses));

    /// <summary>A texture of the shape asked: one resting on its shelf, else a new one.</summary>
    public GpuTexture RentTexture(in TextureDesc desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new Key(desc.Width, desc.Height, desc.Format, null, false, desc.Filter, desc.Wrap, desc.Usage);
        GpuTexture texture;
        if (_textures.TryGetValue(key, out var shelf) && shelf.Free.Count > 0)
            texture = shelf.Free.Pop().Item;
        else
            texture = _device.CreateTexture(desc);
        _texturesOut.Add(texture);
        return texture;
    }

    public void Return(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_disposed || !_texturesOut.Remove(texture))
            return;
        var key = new Key(texture.Width, texture.Height, texture.Format, null, false, texture.Filter, texture.Wrap, texture.Usage);
        if (!_textures.TryGetValue(key, out var shelf))
            _textures[key] = shelf = new Shelf<GpuTexture>();
        shelf.Free.Push((texture, _frame));
    }

    /// <summary>Advances the pool's frame and deletes what rested too long.</summary>
    public void Trim()
    {
        _frame++;
        TrimShelves(_targets);
        TrimShelves(_textures);
    }

    private void TrimShelves<T>(Dictionary<Key, Shelf<T>> shelves) where T : IDisposable
    {
        foreach (var shelf in shelves.Values)
        {
            if (shelf.Free.Count == 0 || _frame - shelf.Free.Peek().Frame <= IdleFramesBeforeRelease)
                continue;
            // The stack's top is the most recently returned; when even it is stale, everything is.
            while (shelf.Free.Count > 0)
                shelf.Free.Pop().Item.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var s in _targets.Values)
            while (s.Free.Count > 0)
                s.Free.Pop().Item.Dispose();
        foreach (var s in _textures.Values)
            while (s.Free.Count > 0)
                s.Free.Pop().Item.Dispose();
        foreach (var t in _targetsOut)
            t.Dispose();
        foreach (var t in _texturesOut)
            t.Dispose();
        _targetsOut.Clear();
        _texturesOut.Clear();
        _disposed = true;
    }
}
