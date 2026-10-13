using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Render.Video;

namespace PlayMap.Render.Engine;

/// <summary>Turns a <see cref="SourceRef"/> into a picture on the GPU for this frame: a flat colour, a
/// generator at the size its consumer needs, a composed canvas, or a video lease from the
/// <see cref="ISourceProvider"/> (asked once per source per frame, uploaded unless its generation is already
/// on the GPU, disposed at once). A source with nothing to show is black; a video that has nothing new holds
/// its last picture. Canvases are composited once a frame, layers bottom to top, nested canvases included
/// (a canvas that contains itself draws black there). A playlist in a transition is mixed once a frame from
/// the two sides its provider names, in linear light (<see cref="SourceMix"/>); a playlist may play another, and one
/// that comes back to itself draws black there.</summary>
internal sealed class SourceResolver : IDisposable
{
    public const int IdleFrames = 120;
    private const int MaxCanvasDepth = 4;

    /// <summary>How deep playlists may play playlists before the innermost draws black.</summary>
    public const int MaxPlaylistDepth = 8;

    private sealed class VideoEntry : IDisposable
    {
        public required VideoTextureSet Set { get; init; }
        public long LastAcquired = -1;
        public long LastUsed;

        public void Dispose()
        {
            Set.Dispose();
        }
    }

    private sealed class CanvasEntry : IDisposable
    {
        public required RenderTarget Target { get; init; }
        public long LastRendered = -1;
        public long LastUsed;

        public void Dispose()
        {
            Target.Dispose();
        }
    }

    private readonly RenderServices _services;
    private readonly GeneratorHost _generators;
    private readonly bool _streaming;
    private readonly Dictionary<SourceRef, VideoEntry> _videos = [];
    private readonly Dictionary<string, CanvasEntry> _canvases = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceRef, CanvasEntry> _mixes = [];
    private readonly List<SourceRef> _staleMixes = [];
    private readonly Dictionary<string, GpuTexture> _colors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _composing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _playlists = new(StringComparer.Ordinal);
    private readonly List<SourceRef> _staleVideos = [];
    private readonly List<string> _staleCanvases = [];
    private Project? _project;
    private ISourceProvider _provider = NoSources.Instance;
    private long _leaderNs;
    private long _frame;
    private GeneratorFrame _generatorFrame;

    public SourceResolver(RenderServices services, GeneratorHost generators, bool streaming = true)
    {
        _services = services;
        _generators = generators;
        _streaming = streaming;
    }

    /// <summary>Video uploads done since the resolver was made (all sources).</summary>
    public int VideoUploads { get; private set; }

    public void BeginFrame(Project project, ISourceProvider provider, long leaderNs, long frame, in GeneratorFrame generatorFrame)
    {
        _project = project;
        _provider = provider;
        _leaderNs = leaderNs;
        _frame = frame;
        _generatorFrame = generatorFrame;
        _generators.BeginFrame(frame);
    }

    /// <summary>The picture of a source for a consumer of the given size (generators draw at it; videos and
    /// canvases have their own). Never null.</summary>
    public GpuTexture Resolve(SourceRef source, int width, int height, GeneratorContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (source.Kind)
        {
            case SourceKind.None:
                return _services.Black;
            case SourceKind.Color:
                return Color(source.Id);
            case SourceKind.Generator:
                var g = _project?.FindGenerator(source.Id);
                return g is null ? _services.Black : _generators.Render(g, width, height, context, in _generatorFrame);
            case SourceKind.Canvas:
                return Canvas(source.Id) ?? _services.Black;
            case SourceKind.Playlist:
                return Playlist(source, width, height, context);
            default:
                return Video(source) ?? _services.Black;
        }
    }

    /// <summary>A playlist's picture: its mix while it is between two items, the picture its item routes to (one
    /// the renderer draws, or another playlist's), else its frames. A playlist already being drawn further out — a
    /// loop of playlists playing each other — or one nested deeper than <see cref="MaxPlaylistDepth"/> is black there.</summary>
    private GpuTexture Playlist(SourceRef source, int width, int height, GeneratorContext context)
    {
        if (_playlists.Count >= MaxPlaylistDepth || !_playlists.Add(source.Id))
            return _services.Black;
        try
        {
            if (_provider.Mix(source, _leaderNs) is { } mix)
                return Mixed(source, mix, width, height, context);
            if (_provider.Route(source, _leaderNs) is { } routed)
                return Resolve(routed, width, height, context);
            return Video(source) ?? _services.Black;
        }
        finally
        {
            _playlists.Remove(source.Id);
        }
    }

    /// <summary>A playlist between two pictures: both sides resolved first (a side may draw), then mixed into
    /// the playlist's own target at the larger of their sizes, once a frame.</summary>
    private GpuTexture Mixed(SourceRef playlist, SourceMix mix, int width, int height, GeneratorContext context)
    {
        if (_mixes.TryGetValue(playlist, out var entry) && entry.LastRendered == _frame)
        {
            entry.LastUsed = _frame;
            return entry.Target.Color;
        }
        var from = Side(mix.From, width, height, context);
        var to = Side(mix.To, width, height, context);
        var w = Math.Max(1, Math.Max(from.Width, to.Width));
        var h = Math.Max(1, Math.Max(from.Height, to.Height));
        if (entry is null || entry.Target.Width != w || entry.Target.Height != h)
        {
            entry?.Dispose();
            entry = new CanvasEntry { Target = _services.Device.CreateRenderTarget(w, h, TextureFormat.Rgba16F) };
            _mixes[playlist] = entry;
        }
        entry.LastUsed = _frame;
        entry.LastRendered = _frame;
        var (wf, wt) = Transitions.Weights(mix.Kind, mix.Progress);
        var d = _services.Device;
        d.BindTarget(entry.Target);
        d.SetBlend(BlendMode.Opaque);
        var p = d.Program("mix");
        p.Set("uWeights", (float)wf, (float)wt);
        p.Set("uGamma", (float)Transitions.Gamma);
        d.BindSampler(p, "uFrom", 0, from);
        d.BindSampler(p, "uTo", 1, to);
        d.DrawFullscreen(p);
        entry.Target.Color.Version++;
        return entry.Target.Color;
    }

    /// <summary>One side of a mix: a picture the renderer draws, another playlist's, or the frames of a ref the
    /// provider made for one item (which it never mixes or routes again).</summary>
    private GpuTexture Side(SourceRef side, int width, int height, GeneratorContext context) => Resolve(side, width, height, context);

    /// <summary>The generator a source names when it is a surface-aware effect (drawn by the stage shader
    /// with the surface's own geometry), else null.</summary>
    public Generator? EffectOf(SourceRef source)
    {
        if (source.Kind != SourceKind.Generator || _project?.FindGenerator(source.Id) is not { } g)
            return null;
        return EffectUniforms.IsEffect(g.Kind) ? g : null;
    }

    /// <summary>A canvas composed for this frame (its own size), or null when the project has none by that id.</summary>
    public GpuTexture? Canvas(string id)
    {
        if (_project?.FindCanvas(id) is not { } canvas || canvas.Width < 1 || canvas.Height < 1)
            return null;
        if (!_canvases.TryGetValue(id, out var entry) || entry.Target.Width != canvas.Width || entry.Target.Height != canvas.Height)
        {
            entry?.Dispose();
            entry = new CanvasEntry { Target = _services.Device.CreateRenderTarget(canvas.Width, canvas.Height, TextureFormat.Rgba16F) };
            _canvases[id] = entry;
        }
        entry.LastUsed = _frame;
        if (entry.LastRendered == _frame)
            return entry.Target.Color;
        if (_composing.Count >= MaxCanvasDepth || !_composing.Add(id))
            return _services.Black;
        try
        {
            Compose(canvas, entry.Target);
            entry.LastRendered = _frame;
        }
        finally
        {
            _composing.Remove(id);
        }
        return entry.Target.Color;
    }

    /// <summary>The layers of a canvas into its target: the background, then each visible layer's source
    /// at its rectangle with its opacity and blend.</summary>
    private void Compose(Canvas canvas, RenderTarget target)
    {
        var context = new GeneratorContext(canvas.Name, 0, "#3EC1F3", true);
        // Resolve every layer's source first: a source may itself draw (a generator, a nested canvas) and
        // must not do so in the middle of this canvas's draws.
        Span<int> order = canvas.Layers.Length <= 64 ? stackalloc int[canvas.Layers.Length] : new int[canvas.Layers.Length];
        // One scratch array per nesting depth: a nested canvas composes while this one's sources are held.
        var depth = _composing.Count - 1;
        var textures = _layerTextures[depth];
        if (textures is null || textures.Length < canvas.Layers.Length)
            textures = _layerTextures[depth] = new GpuTexture[Math.Max(16, canvas.Layers.Length)];
        var count = 0;
        for (var i = 0; i < canvas.Layers.Length; i++)
        {
            var layer = canvas.Layers[i];
            if (!layer.Visible || layer.Opacity <= 0 || layer.Rect.IsEmpty)
                continue;
            var w = Math.Max(1, (int)Math.Round(layer.Rect.W));
            var h = Math.Max(1, (int)Math.Round(layer.Rect.H));
            textures[count] = Resolve(layer.Source, w, h, context);
            order[count++] = i;
        }
        var d = _services.Device;
        d.BindTarget(target);
        // A first layer that covers the whole canvas, opaque, at full opacity, replaces the background: it is
        // drawn without blending and the clear is skipped (the common one-layer canvas costs one pass).
        var first = 0;
        if (count > 0 && CoversOpaquely(canvas, canvas.Layers[order[0]]))
        {
            d.SetBlend(BlendMode.Opaque);
            var r0 = canvas.Layers[order[0]].Rect;
            _services.DrawQuad(target, textures[0], (float)r0.X, (float)r0.Y, (float)r0.W, (float)r0.H, 0, 1);
            textures[0] = null!;
            first = 1;
        }
        else
        {
            var bg = ColorRgb.Parse(canvas.Background, ColorRgb.Black);
            d.Clear(bg.R, bg.G, bg.B, 1);
        }
        for (var k = first; k < count; k++)
        {
            var layer = canvas.Layers[order[k]];
            var (blend, mode) = layer.Blend switch
            {
                LayerBlend.Add => (BlendMode.Add, 1),
                LayerBlend.Multiply => (BlendMode.Multiply, 2),
                LayerBlend.Screen => (BlendMode.Screen, 3),
                _ => (BlendMode.Normal, 0),
            };
            d.SetBlend(blend);
            var r = layer.Rect;
            _services.DrawQuad(target, textures[k], (float)r.X, (float)r.Y, (float)r.W, (float)r.H, mode, (float)Math.Clamp(layer.Opacity, 0, 1));
            textures[k] = null!;
        }
        d.SetBlend(BlendMode.Opaque);
        target.Color.Version++;
    }

    private readonly GpuTexture[]?[] _layerTextures = new GpuTexture[MaxCanvasDepth][];

    /// <summary>True when a layer hides everything under it: a normal layer at full opacity over the whole
    /// canvas whose source has no transparency (a generator, a colour or a canvas — pictures here are opaque).</summary>
    private static bool CoversOpaquely(Canvas canvas, Layer layer) =>
        layer.Blend == LayerBlend.Normal && layer.Opacity >= 1
        && layer.Source.Kind is SourceKind.Generator or SourceKind.Color or SourceKind.Canvas
        && layer.Rect.X <= 0 && layer.Rect.Y <= 0 && layer.Rect.Right >= canvas.Width && layer.Rect.Bottom >= canvas.Height;

    private GpuTexture? Video(SourceRef source)
    {
        if (!_videos.TryGetValue(source, out var entry))
        {
            entry = new VideoEntry { Set = new VideoTextureSet(_services.Device, _streaming) };
            _videos[source] = entry;
        }
        entry.LastUsed = _frame;
        if (entry.LastAcquired != _frame)
        {
            entry.LastAcquired = _frame;
            using var lease = _provider.Acquire(source, _leaderNs);
            if (lease is not null && entry.Set.Update(lease))
                VideoUploads++;
        }
        return entry.Set.Picture;
    }

    /// <summary>The upload count of one video source (tests: a repeated generation is not uploaded).</summary>
    public int UploadsOf(SourceRef source) => _videos.TryGetValue(source, out var e) ? e.Set.Uploads : 0;

    private GpuTexture Color(string hex)
    {
        if (_colors.TryGetValue(hex, out var tex))
            return tex;
        var c = ColorRgb.Parse(hex, ColorRgb.Black);
        tex = _services.Device.CreateTexture(1, 1, TextureFormat.Rgba8, TextureFilter.Nearest);
        _services.Device.Upload(tex, [c.R8, c.G8, c.B8, 255], UploadFormat.Rgba8);
        _colors[hex] = tex;
        return tex;
    }

    /// <summary>Lets go of sources no frame has used for a while.</summary>
    public void EndFrame()
    {
        _generators.EndFrame();
        foreach (var (key, e) in _videos)
            if (_frame - e.LastUsed > IdleFrames)
                _staleVideos.Add(key);
        foreach (var key in _staleVideos)
        {
            _videos[key].Dispose();
            _videos.Remove(key);
        }
        _staleVideos.Clear();
        foreach (var (key, e) in _canvases)
            if (_frame - e.LastUsed > IdleFrames)
                _staleCanvases.Add(key);
        foreach (var key in _staleCanvases)
        {
            _canvases[key].Dispose();
            _canvases.Remove(key);
        }
        _staleCanvases.Clear();
        foreach (var (key, e) in _mixes)
            if (_frame - e.LastUsed > IdleFrames)
                _staleMixes.Add(key);
        foreach (var key in _staleMixes)
        {
            _mixes[key].Dispose();
            _mixes.Remove(key);
        }
        _staleMixes.Clear();
    }

    public void Dispose()
    {
        foreach (var e in _videos.Values)
            e.Dispose();
        foreach (var e in _canvases.Values)
            e.Dispose();
        foreach (var e in _mixes.Values)
            e.Dispose();
        foreach (var t in _colors.Values)
            t.Dispose();
        _videos.Clear();
        _canvases.Clear();
        _mixes.Clear();
        _colors.Clear();
    }
}
