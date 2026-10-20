using System.Collections.Immutable;
using PlayMap.Core.Effects;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Effects;

/// <summary>One slot's ISF effect on the GPU: its program (made by the device through the effects' lane, <see cref="FxLane"/>), its
/// pass buffers — temporary ones and persistent ones, float when asked, owned here, made again and cleared when their size changes —
/// and its uniforms, computed once per slot change. Each frame it draws every pass into the frame's list with the built-ins of show
/// time, each constant and picture set by name through the program's layout (a name the program does not use is not set), and
/// answers its result, stored bottom row first as ISF draws. A buffer a pass draws into is read by that pass as it was before (each
/// buffer has a front and a back), so a feedback pass never reads what it writes.</summary>
internal sealed class IsfRenderer : IDisposable, IIsfVariables
{
    private const int MaxImages = 15;

    private sealed class Buffer : IDisposable
    {
        public RenderTarget? Front;
        public RenderTarget? Back;
        public bool Float;

        /// <summary>No pass reads it (the output's own buffer): one target, written in place.</summary>
        public bool Single;
        public int Width;
        public int Height;

        public void Swap() => (Front, Back) = (Back, Front);

        public void Dispose()
        {
            Front?.Dispose();
            if (!ReferenceEquals(Back, Front))
                Back?.Dispose();
            Front = Back = null;
        }
    }

    private enum ImageSource : byte
    {
        Chain,
        Unbound,
        Wave,
        Spectrum,
        Buffer,
        Imported,
    }

    private sealed record ImageBinding(string Name, string Size, string Rect, string Flip, ImageSource Source, Buffer? Buffer);

    private sealed record UniformBinding(string Name, IsfInputType Type, float[] Value);

    private readonly IGpuDevice _device;
    private readonly FxLane _lane;
    private readonly GpuTexture _black;
    private readonly GpuTexture _grey;
    private readonly Dictionary<string, double> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Buffer> _named = new(StringComparer.Ordinal);
    private readonly Buffer _output = new() { Single = true };
    private FxKind? _kind;
    private FxSlot? _slot;
    private string _key = "";
    private string _name = "";
    private IsfProgramSource? _source;
    private ImmutableSortedDictionary<string, string> _imports = ImmutableSortedDictionary<string, string>.Empty;
    private FxProgram? _entry;
    private IsfPass[] _passes = [];
    private IsfExpression[] _widths = [];
    private IsfExpression[] _heights = [];
    private Buffer[] _passBuffers = [];
    private (int W, int H)[] _sizes = [];
    private ImageBinding[] _images = [];
    private UniformBinding[] _uniforms = [];
    private int _renderW;
    private int _renderH;
    private bool _failedDrawing;

    public IsfRenderer(IGpuDevice device, FxLane lane, GpuTexture black, GpuTexture grey)
    {
        _device = device;
        _lane = lane;
        _black = black;
        _grey = grey;
    }

    public FxSlotState State { get; private set; } = FxSlotState.Compiling;

    public string Words { get; private set; } = "compiling";

    public FxKind? Kind => _kind;

    /// <summary>Takes the slot's record: other files (by content, not by which catalogue holds them) start again (buffers
    /// cleared, program asked for); the same files from another catalogue keep the program and every buffer, so a show's other
    /// edits never restart an effect on the air; new parameters only recompute the uniforms. Called when the slot's record or
    /// the catalogue is not the one it has.</summary>
    public void Configure(FxSlot slot, FxKind kind, IsfProgramSource source, FxSource files)
    {
        if (_kind is null || !string.Equals(files.Hash, _key, StringComparison.Ordinal) || !SameImages(files.Images, _imports))
            Rebuild(kind, source, files);
        _kind = kind;
        _slot = slot;
        if (_failedDrawing)
        {
            // The slot's record changed: a fault met drawing is tried again (a compile fault stays until the files change).
            _failedDrawing = false;
            State = FxSlotState.Compiling;
            Words = "compiling";
        }
        _vars.Clear();
        Span<float> v = stackalloc float[4];
        foreach (var u in _uniforms)
        {
            if (kind.Document.Input(u.Name) is { } input && FxKinds.TryUniform(input, kind, slot.Params, v))
                v.CopyTo(u.Value);
            _vars[u.Name] = u.Value[0];
        }
    }

    private static bool SameImages(ImmutableSortedDictionary<string, string> a, ImmutableSortedDictionary<string, string> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        foreach (var (name, data) in a)
            if (!b.TryGetValue(name, out var other) || !string.Equals(data, other, StringComparison.Ordinal))
                return false;
        return true;
    }

    private void Rebuild(FxKind kind, IsfProgramSource source, FxSource files)
    {
        DisposeBuffers();
        _failedDrawing = false;
        _kind = kind;
        _source = source;
        _imports = files.Images;
        _entry = null;
        _name = kind.Id;
        _key = files.Hash;
        var doc = kind.Document;
        if (!doc.Valid)
        {
            Fault(doc.Issues.Length > 0 ? Line(doc.Issues[0]) : "the file does not read");
            return;
        }
        _passes = [.. doc.Passes];
        _widths = [.. _passes.Select(p => IsfExpression.Parse(p.WidthExpr))];
        _heights = [.. _passes.Select(p => IsfExpression.Parse(p.HeightExpr))];
        _sizes = new (int, int)[_passes.Length];
        _passBuffers = new Buffer[_passes.Length];
        for (var i = 0; i < _passes.Length; i++)
        {
            var p = _passes[i];
            if (p.Target.Length == 0)
            {
                _passBuffers[i] = _output;
                continue;
            }
            if (!_named.TryGetValue(p.Target, out var b))
                _named[p.Target] = b = new Buffer();
            b.Float |= p.Float;
            _passBuffers[i] = b;
        }
        var images = new List<ImageBinding>();
        foreach (var name in doc.Images)
        {
            var input = doc.Input(name);
            var (src, buffer) = input switch
            {
                { Type: IsfInputType.Audio } => (ImageSource.Wave, null),
                { Type: IsfInputType.AudioFft } => (ImageSource.Spectrum, null),
                { } when FxKinds.IsChainImage(name) => (ImageSource.Chain, null),
                { } => (ImageSource.Unbound, null),
                null when _named.TryGetValue(name, out var nb) => (ImageSource.Buffer, nb),
                null when doc.Imported.Any(m => m.Name == name) => (ImageSource.Imported, (Buffer?)null),
                _ => (ImageSource.Unbound, null),
            };
            images.Add(new ImageBinding(name, $"_{name}_imgSize", $"_{name}_imgRect", $"_{name}_flip", src, buffer));
        }
        _images = [.. images];
        _uniforms = [.. doc.Inputs.Where(i => IsfTranslator.GlslType(i.Type) is not null).Select(i => new UniformBinding(i.Name, i.Type, new float[4]))];
        if (_images.Length > MaxImages)
        {
            Fault($"{_images.Length} pictures; an effect reads at most {MaxImages}");
            return;
        }
        if (source.Issues.FirstOrDefault(i => i.Words.Contains("takes", StringComparison.Ordinal) || i.Words.Contains("never closed", StringComparison.Ordinal)) is { Words.Length: > 0 } bad)
        {
            Fault(Line(bad));
            return;
        }
        State = FxSlotState.Compiling;
        Words = "compiling";
        _lane.Request(_key, _name, source.Vertex, source.Fragment, _imports);
    }

    private static string Line(IsfIssue issue) => issue.Line > 0 ? $"line {issue.Line}: {issue.Words}" : issue.Words;

    private void Fault(string words)
    {
        State = FxSlotState.Faulted;
        Words = words;
    }

    public bool TryGet(string name, out double value)
    {
        switch (name)
        {
            case "WIDTH":
                value = _renderW;
                return true;
            case "HEIGHT":
                value = _renderH;
                return true;
            default:
                return _vars.TryGetValue(name, out value);
        }
    }

    /// <summary>Keeps the slot's program on the lane through a frame it is not drawn in (its owner shows something else).</summary>
    public void Keep(long frame)
    {
        if (_entry is { } entry)
            entry.LastUsed = frame;
    }

    /// <summary>The effect's result for this frame at a size, drawn into <paramref name="list"/>, or null when it has none to give
    /// (compiling or faulted: the chain passes its input on). The texture is stored bottom row first and stays this renderer's.</summary>
    public GpuTexture? Apply(ICommandList list, long frame, in FxClock clock, GpuTexture input, int width, int height)
    {
        if (_kind is null || _slot is null || State == FxSlotState.Faulted)
            return null;
        if (_entry is null)
        {
            _entry = _lane.Get(_key, frame);
            if (_entry is null)
            {
                _lane.Request(_key, _name, _source!.Vertex, _source.Fragment, _imports);
                State = FxSlotState.Compiling;
                Words = _lane.Mode == "render-thread" ? "compiling (on the render thread: one effect a frame)" : "compiling";
                return null;
            }
            if (_entry.Layout is null)
            {
                var issues = _source!.LineMap.Read(_entry.Error, vertex: _entry.Stage == "vertex");
                Fault(issues.Length > 0 ? Line(issues[0]) : _entry.Error ?? "the program did not build");
                return null;
            }
        }
        var entry = _entry;
        entry.LastUsed = frame;
        var layout = entry.Layout!;
        _renderW = width;
        _renderH = height;
        for (var i = 0; i < _passes.Length; i++)
        {
            var w = _widths[i].Evaluate(this, width, out _);
            var h = _heights[i].Evaluate(this, height, out _);
            _sizes[i] = (w, h);
            Ensure(list, _passBuffers[i], w, h);
        }
        for (var i = 0; i < _passes.Length; i++)
        {
            var buffer = _passBuffers[i];
            var (w, h) = _sizes[i];
            using (list.Pass(buffer.Back!, PassDesc.Keep))
            {
                var draw = list.Draw(entry.Id, Drawn);
                SetInt(draw, layout, "PASSINDEX", i);
                Set(draw, layout, "RENDERSIZE", w, h);
                Set(draw, layout, "TIME", clock.Time);
                Set(draw, layout, "TIMEDELTA", clock.TimeDelta);
                SetInt(draw, layout, "FRAMEINDEX", clock.FrameIndex);
                Set(draw, layout, "DATE", clock.Year, clock.Month, clock.Day, clock.DaySeconds);
                foreach (var image in _images)
                    BindImage(draw, layout, entry, image, input);
                foreach (var u in _uniforms)
                    SetUniform(draw, layout, u);
                draw.Fullscreen();
            }
            buffer.Swap();
        }
        State = FxSlotState.Ok;
        Words = "";
        return _passBuffers[^1].Front!.Color;
    }

    /// <summary>Every pass writes its buffer whole, opaque.</summary>
    private static readonly DrawState Drawn = DrawState.Opaque;

    private static void Set(DrawBuilder draw, ProgramLayout layout, string name, float x)
    {
        if (layout.TryConstant(name, out _))
            draw.Set(name, x);
    }

    private static void Set(DrawBuilder draw, ProgramLayout layout, string name, float x, float y)
    {
        if (layout.TryConstant(name, out _))
            draw.Set(name, x, y);
    }

    private static void Set(DrawBuilder draw, ProgramLayout layout, string name, float x, float y, float z, float w)
    {
        if (layout.TryConstant(name, out _))
            draw.Set(name, x, y, z, w);
    }

    private static void SetInt(DrawBuilder draw, ProgramLayout layout, string name, int value)
    {
        if (layout.TryConstant(name, out _))
            draw.SetInt(name, value);
    }

    private void BindImage(DrawBuilder draw, ProgramLayout layout, FxProgram entry, ImageBinding image, GpuTexture input)
    {
        GpuTexture texture;
        var flip = false;
        switch (image.Source)
        {
            case ImageSource.Chain:
                texture = input;
                flip = true;
                break;
            case ImageSource.Wave:
                // Silence: a waveform is centred at 0.5 in ISF.
                texture = _grey;
                break;
            case ImageSource.Buffer:
                texture = image.Buffer!.Front?.Color ?? _black;
                break;
            case ImageSource.Imported when entry.Images.TryGetValue(image.Name, out var imported):
                texture = imported;
                flip = true;
                break;
            default:
                texture = _black;
                break;
        }
        if (layout.TryTexture(image.Name, out _))
            draw.Texture(image.Name, texture);
        Set(draw, layout, image.Size, texture.Width, texture.Height);
        Set(draw, layout, image.Rect, 0, 0, 1, 1);
        SetInt(draw, layout, image.Flip, flip ? 1 : 0);
    }

    private static void SetUniform(DrawBuilder draw, ProgramLayout layout, UniformBinding u)
    {
        var v = u.Value;
        switch (u.Type)
        {
            case IsfInputType.Float:
                Set(draw, layout, u.Name, v[0]);
                break;
            case IsfInputType.Point2D:
                Set(draw, layout, u.Name, v[0], v[1]);
                break;
            case IsfInputType.Color:
                Set(draw, layout, u.Name, v[0], v[1], v[2], v[3]);
                break;
            default:
                SetInt(draw, layout, u.Name, (int)v[0]);
                break;
        }
    }

    /// <summary>A buffer at a size: made (both sides, cleared) when it has none or its size changed — a persistent buffer starts
    /// again from transparent black.</summary>
    private void Ensure(ICommandList list, Buffer b, int w, int h)
    {
        if (b.Front is not null && b.Width == w && b.Height == h)
            return;
        b.Dispose();
        var format = BufferFormat(_device.Features, b.Float);
        b.Front = _device.CreateTarget(new TargetDesc(w, h, format));
        b.Back = b.Single ? b.Front : _device.CreateTarget(new TargetDesc(w, h, format));
        b.Width = w;
        b.Height = h;
        Clear(list, b.Front);
        if (!ReferenceEquals(b.Back, b.Front))
            Clear(list, b.Back);
    }

    private static void Clear(ICommandList list, RenderTarget target)
    {
        using (list.Pass(target, Transparent))
        {
            // The pass's clear is the whole of it.
        }
    }

    private static readonly PassDesc Transparent = FrameLists.Clear(0, 0, 0, 0);

    /// <summary>A pass buffer's format: 32-bit float when the file asks for FLOAT, else 16-bit float, each where this device can
    /// render to it (an ES context may not), else 8-bit.</summary>
    internal static TextureFormat BufferFormat(GpuFeatures features, bool wantsFloat)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (wantsFloat && Renders(features, TextureFormat.Rgba32F))
            return TextureFormat.Rgba32F;
        return Renders(features, TextureFormat.Rgba16F) ? TextureFormat.Rgba16F : TextureFormat.Rgba8;
    }

    internal static bool Renders(GpuFeatures features, TextureFormat format) =>
        features.Formats.TryGetValue(format, out var f) && f.Target;

    /// <summary>A fault met while drawing (a target this context cannot make, a driver's refusal): the slot shows its input with
    /// the words and is not drawn again until its record changes, so a failing draw is never retried every frame.</summary>
    public void Fail(string words)
    {
        Fault(words);
        _failedDrawing = true;
    }

    private void DisposeBuffers()
    {
        _output.Dispose();
        foreach (var b in _named.Values)
            b.Dispose();
        _named.Clear();
    }

    public void Dispose() => DisposeBuffers();
}
