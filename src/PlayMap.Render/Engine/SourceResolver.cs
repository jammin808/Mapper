using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Render.Generators;
using PlayMap.Render.Video;

namespace PlayMap.Render.Engine;

/// <summary>Turns a <see cref="SourceRef"/> into a picture on the GPU for this frame: a flat colour, a
/// generator at the size its consumer needs, a composed canvas, or a video lease from the
/// <see cref="ISourceProvider"/> (asked once per source per frame, uploaded unless its generation is already
/// on the GPU, disposed at once). A source with nothing to show is black; a video that has nothing new holds
/// its last picture. Canvases are composited once a frame, layers bottom to top, nested canvases included
/// (a canvas that contains itself draws black there). A playlist in a transition is mixed once a frame from
/// the two sides its provider names, in linear light (<see cref="SourceMix"/>); a playlist may play another, and one
/// that comes back to itself draws black there.
///
/// The frame's passes (<see cref="SetPasses"/>) see a layer's picture before its canvas is drawn into, an item's picture
/// (each side of a transition on its own) before it is shown or mixed, and — through <see cref="SurfacePicture"/> — a stage
/// surface's; each at most once a frame. Once the frame ends the passes are let go, so what is resolved after it (a preview)
/// shows what the frame made, or the picture without its passes. What the air shows instead of the show
/// (<see cref="ResolvePlain"/>) meets no pass: its canvases are composed and its playlists mixed apart from the show's.</summary>
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

    /// <summary>A playlist's item picture through the frame's passes, kept for the frame's later consumers.</summary>
    private sealed class ItemEntry
    {
        public GpuTexture? Picture;
        public long Frame = -1;
        public long LastUsed;
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

    /// <summary>A mix's state: the mixed picture written whole, opaque.</summary>
    private static readonly DrawState Mixing = DrawState.Opaque;

    private readonly RenderServices _services;
    private readonly GeneratorHost _generators;
    private readonly bool _streaming;
    private readonly ProgramId _mix;
    private readonly Dictionary<SourceRef, VideoEntry> _videos = [];
    private readonly Dictionary<string, CanvasEntry> _canvases = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceRef, CanvasEntry> _mixes = [];
    private readonly List<SourceRef> _staleMixes = [];
    private readonly Dictionary<string, GpuTexture> _colors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _composing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _playlists = new(StringComparer.Ordinal);
    private readonly List<SourceRef> _staleVideos = [];
    private readonly List<string> _staleCanvases = [];
    private readonly Dictionary<SourceRef, ItemEntry> _items = [];
    private readonly List<SourceRef> _staleItems = [];
    private readonly Dictionary<string, CanvasEntry> _plainCanvases = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceRef, CanvasEntry> _plainMixes = [];
    private IFramePass[] _passes = [];
    private int _plain;
    private PassFrame _passFrame;
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
        _mix = services.Device.Programs.Id("mix");
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

    /// <summary>The frame's passes in their order and what they are told about the frame: set once a frame, after
    /// <see cref="BeginFrame"/> and before anything is resolved; <see cref="EndFrame"/> lets them go.</summary>
    public void SetPasses(IFramePass[] passes, in PassFrame frame)
    {
        _passes = passes;
        _passFrame = frame;
    }

    /// <summary>True while the frame's passes run (between <see cref="SetPasses"/> and <see cref="EndFrame"/>).</summary>
    public bool RunsPasses => _passes.Length > 0;

    /// <summary>The passes a picture resolved now meets: none outside the frame, none for what the air shows instead of the show.</summary>
    private ReadOnlySpan<IFramePass> Passes => _plain > 0 ? default : _passes;

    /// <summary>The frame being drawn.</summary>
    public long FrameNumber => _frame;

    /// <summary>A stage surface's content through the frame's passes, in order; the caller keeps what comes back for the frame.</summary>
    public GpuTexture SurfacePicture(Surface surface, GpuTexture picture)
    {
        foreach (var pass in Passes)
            picture = pass.Surface(in _passFrame, surface, picture);
        return picture;
    }

    /// <summary>What the air shows instead of the show — a test pattern, a panic's safe look, a raw scan frame — as its source is,
    /// through no pass: a canvas in it is composed, and a playlist in it mixed, into targets of their own (a canvas the show also
    /// shows is composed twice, once with its layers' chains and once without), and a playlist's item is drawn without its
    /// chain. Never null.</summary>
    public GpuTexture ResolvePlain(SourceRef source, int width, int height, GeneratorContext context)
    {
        _plain++;
        try
        {
            return Resolve(source, width, height, context);
        }
        finally
        {
            _plain--;
        }
    }

    /// <summary>The picture of a source for a consumer of the given size (generators draw at it; videos and
    /// canvases have their own). Never null.</summary>
    public GpuTexture Resolve(SourceRef source, int width, int height, GeneratorContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Inside the engine's frame its list; asked outside one (a pass's tap between frames, a test), a frame of its own.
        using var scope = _services.Frame();
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
            case SourceKind.Media or SourceKind.Live when _provider.Route(source, _leaderNs) is { } routed:
                // A live input's loss policy (black, or a fallback that is never live, so this does not recurse).
                return routed.Kind is SourceKind.Media or SourceKind.Live ? Video(routed) ?? _services.Black : Resolve(routed, width, height, context);
            default:
                return Video(source) ?? _services.Black;
        }
    }

    /// <summary>A playlist's picture: its mix while it is between two items, the picture its item routes to (one
    /// the renderer draws, or another playlist's), else its frames. A playlist already being drawn further out — a
    /// loop of playlists playing each other — or one nested deeper than <see cref="MaxPlaylistDepth"/> is black there.
    /// An item whose effect chain draws passes through the frame's passes once a frame, at the size its first consumer asks
    /// for; the frame's later consumers share that picture.</summary>
    private GpuTexture Playlist(SourceRef source, int width, int height, GeneratorContext context)
    {
        if (_playlists.Count >= MaxPlaylistDepth || !_playlists.Add(source.Id))
            return _services.Black;
        try
        {
            if (_provider.Mix(source, _leaderNs) is { } mix)
                return Mixed(source, mix, width, height, context);
            if (_plain == 0 && _items.TryGetValue(source, out var shown) && shown.Frame == _frame)
            {
                shown.LastUsed = _frame;
                return shown.Picture!;
            }
            var item = !Passes.IsEmpty ? _provider.Item(source, _leaderNs) : null;
            GpuTexture picture;
            if (_provider.Route(source, _leaderNs) is { } routed)
                picture = Resolve(routed, width, height, context);
            else if (Video(source) is { } frames)
                picture = frames;
            else
                return _services.Black;
            if (item is not { } on || !FxChains.Draws(on.Item.Fx))
                return picture;
            picture = ItemPicture(on, picture);
            if (!_items.TryGetValue(source, out var entry))
                _items[source] = entry = new ItemEntry();
            entry.Picture = picture;
            entry.Frame = _frame;
            entry.LastUsed = _frame;
            return picture;
        }
        finally
        {
            _playlists.Remove(source.Id);
        }
    }

    /// <summary>An item's picture through the frame's passes, in order.</summary>
    private GpuTexture ItemPicture(in ItemOnAir item, GpuTexture picture)
    {
        foreach (var pass in Passes)
            picture = pass.Item(in _passFrame, item.Item, item.StartNs, picture);
        return picture;
    }

    /// <summary>A playlist between two pictures: both sides resolved first (a side may draw), then mixed into
    /// the playlist's own target at the larger of their sizes, once a frame (once more, apart, for the air's own picture).</summary>
    private GpuTexture Mixed(SourceRef playlist, SourceMix mix, int width, int height, GeneratorContext context)
    {
        var mixes = _plain > 0 ? _plainMixes : _mixes;
        if (mixes.TryGetValue(playlist, out var entry) && entry.LastRendered == _frame)
        {
            entry.LastUsed = _frame;
            return entry.Target.Color;
        }
        var from = Side(mix.From, mix.FromItem, width, height, context);
        var to = Side(mix.To, mix.ToItem, width, height, context);
        var w = Math.Max(1, Math.Max(from.Width, to.Width));
        var h = Math.Max(1, Math.Max(from.Height, to.Height));
        if (entry is null || entry.Target.Width != w || entry.Target.Height != h)
        {
            entry?.Dispose();
            entry = new CanvasEntry { Target = _services.Device.CreateTarget(new TargetDesc(w, h, TextureFormat.Rgba16F)) };
            mixes[playlist] = entry;
        }
        entry.LastUsed = _frame;
        entry.LastRendered = _frame;
        var (wf, wt) = Transitions.Weights(mix.Kind, mix.Progress);
        var list = _services.List;
        using (list.Pass(entry.Target, PassDesc.Keep))
            list.Draw(_mix, Mixing)
                .Set("uWeights", (float)wf, (float)wt)
                .Set("uGamma", (float)Transitions.Gamma)
                .Texture("uFrom", from)
                .Texture("uTo", to)
                .Fullscreen();
        entry.Target.Color.Version++;
        return entry.Target.Color;
    }

    /// <summary>One side of a mix: a picture the renderer draws, another playlist's, or the frames of a ref the
    /// provider made for one item (which it never mixes or routes again) — through the frame's passes when its item has a
    /// chain, before the two are mixed.</summary>
    private GpuTexture Side(SourceRef side, ItemOnAir? item, int width, int height, GeneratorContext context)
    {
        var picture = Resolve(side, width, height, context);
        return !Passes.IsEmpty && item is { } on && FxChains.Draws(on.Item.Fx) ? ItemPicture(on, picture) : picture;
    }

    /// <summary>The generator a source names when it is a surface-aware effect of a kind this node has (drawn by the stage shader
    /// with the surface's own geometry), else null: an effect kind it does not have is resolved as any generator, so its surface
    /// shows the host's error card and no other surface of the pass is touched.</summary>
    public Generator? EffectOf(SourceRef source)
    {
        if (source.Kind != SourceKind.Generator || _project?.FindGenerator(source.Id) is not { } g)
            return null;
        return EffectUniforms.IsEffect(g.Kind) && GeneratorCatalog.Find(g.Kind) is not null ? g : null;
    }

    /// <summary>The frame a surface-aware effect the stage draws is drawn at: on its own clock, as it draws anywhere else, with the
    /// draw reported to the frame's stats (<see cref="GeneratorHost.OnStage"/>).</summary>
    public GeneratorFrame EffectFrame(Generator effect, in GeneratorFrame frame) => _generators.OnStage(effect, in frame);

    /// <summary>A canvas composed for this frame (its own size), or null when the project has none by that id.</summary>
    public GpuTexture? Canvas(string id)
    {
        if (_project?.FindCanvas(id) is not { } canvas || canvas.Width < 1 || canvas.Height < 1)
            return null;
        using var scope = _services.Frame();
        var canvases = _plain > 0 ? _plainCanvases : _canvases;
        if (!canvases.TryGetValue(id, out var entry) || entry.Target.Width != canvas.Width || entry.Target.Height != canvas.Height)
        {
            entry?.Dispose();
            entry = new CanvasEntry { Target = _services.Device.CreateTarget(new TargetDesc(canvas.Width, canvas.Height, TextureFormat.Rgba16F)) };
            canvases[id] = entry;
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
    /// at its rectangle with its opacity and blend, as the frame's passes hand it back.</summary>
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
        var firstPassed = false;
        for (var i = 0; i < canvas.Layers.Length; i++)
        {
            var layer = canvas.Layers[i];
            if (!layer.Visible || layer.Opacity <= 0 || layer.Rect.IsEmpty)
                continue;
            var w = Math.Max(1, (int)Math.Round(layer.Rect.W));
            var h = Math.Max(1, (int)Math.Round(layer.Rect.H));
            var picture = Resolve(layer.Source, w, h, context);
            var drawn = picture;
            foreach (var pass in Passes)
                drawn = pass.Layer(in _passFrame, layer, drawn);
            if (count == 0)
                firstPassed = !ReferenceEquals(drawn, picture);
            textures[count] = drawn;
            order[count++] = i;
        }
        // A first layer that covers the whole canvas, opaque, at full opacity, replaces the background: it is
        // drawn without blending and the clear is skipped (the common one-layer canvas costs one pass). A layer a pass
        // handed another picture is not known opaque (an effect may write alpha), so the background goes under it.
        var covered = count > 0 && !firstPassed && CoversOpaquely(canvas, canvas.Layers[order[0]]);
        var list = _services.List;
        using (list.Pass(target, covered ? PassDesc.Keep : Background(canvas)))
        {
            var first = 0;
            if (covered)
            {
                var r0 = canvas.Layers[order[0]].Rect;
                _services.DrawQuad(list, target, textures[0], (float)r0.X, (float)r0.Y, (float)r0.W, (float)r0.H, 0, 1, in Covering);
                textures[0] = null!;
                first = 1;
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
                var r = layer.Rect;
                _services.DrawQuad(list, target, textures[k], (float)r.X, (float)r.Y, (float)r.W, (float)r.H, mode,
                    (float)Math.Clamp(layer.Opacity, 0, 1), FrameLists.Quads(blend));
                textures[k] = null!;
            }
        }
        target.Color.Version++;
    }

    /// <summary>A covering first layer's state: drawn without blending.</summary>
    private static readonly DrawState Covering = FrameLists.Quads(BlendMode.Opaque);

    /// <summary>The canvases' and playlists' draws, into half-float pictures: a playlist's mix, and each layer's quad in its blend (a
    /// covering first layer opaque).</summary>
    internal static void Pipelines(PipelineList keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("mix", Mixing, TargetShape.Rgba16F);
        keys.Add("quad", Covering, TargetShape.Rgba16F);
        foreach (var blend in (ReadOnlySpan<BlendMode>)[BlendMode.Normal, BlendMode.Add, BlendMode.Multiply, BlendMode.Screen])
            keys.Add("quad", FrameLists.Quads(blend), TargetShape.Rgba16F);
    }

    /// <summary>A pass that clears the canvas to its background first.</summary>
    private static PassDesc Background(Canvas canvas)
    {
        var bg = ColorRgb.Parse(canvas.Background, ColorRgb.Black);
        return FrameLists.Clear(bg.R, bg.G, bg.B, 1);
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
            if (lease is not null && entry.Set.Update(lease, _services.List))
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
        tex = _services.Device.CreateTexture(new TextureDesc(1, 1, TextureFormat.Rgba8, FrameLists.Uploaded, TextureFilter.Nearest));
        _services.List.Upload(tex, [c.R8, c.G8, c.B8, 255], UploadFormat.Rgba8);
        _colors[hex] = tex;
        return tex;
    }

    /// <summary>Lets go of the frame's passes, and of sources no frame has used for a while.</summary>
    public void EndFrame()
    {
        _passes = [];
        _passFrame = default;
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
        Sweep(_canvases, _staleCanvases);
        Sweep(_plainCanvases, _staleCanvases);
        Sweep(_mixes, _staleMixes);
        Sweep(_plainMixes, _staleMixes);
        foreach (var (key, e) in _items)
            if (_frame - e.LastUsed > IdleFrames)
                _staleItems.Add(key);
        foreach (var key in _staleItems)
            _items.Remove(key);
        _staleItems.Clear();
    }

    /// <summary>Lets go of the targets no frame has used for <see cref="IdleFrames"/> frames.</summary>
    private void Sweep<TKey>(Dictionary<TKey, CanvasEntry> entries, List<TKey> stale)
        where TKey : notnull
    {
        foreach (var (key, e) in entries)
            if (_frame - e.LastUsed > IdleFrames)
                stale.Add(key);
        foreach (var key in stale)
        {
            entries[key].Dispose();
            entries.Remove(key);
        }
        stale.Clear();
    }

    public void Dispose()
    {
        foreach (var e in _videos.Values)
            e.Dispose();
        foreach (var e in _canvases.Values)
            e.Dispose();
        foreach (var e in _plainCanvases.Values)
            e.Dispose();
        foreach (var e in _mixes.Values)
            e.Dispose();
        foreach (var e in _plainMixes.Values)
            e.Dispose();
        foreach (var t in _colors.Values)
            t.Dispose();
        _videos.Clear();
        _canvases.Clear();
        _plainCanvases.Clear();
        _mixes.Clear();
        _plainMixes.Clear();
        _colors.Clear();
        _items.Clear();
    }
}
