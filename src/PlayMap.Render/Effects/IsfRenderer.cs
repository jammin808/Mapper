using System.Collections.Immutable;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Effects;

/// <summary>One slot's ISF effect on the GPU: its program (from the compile lane), its pass buffers — temporary ones and
/// persistent ones, float when asked, owned here, made again and cleared when their size changes — and its uniforms, computed
/// once per slot change. Each frame it draws every pass with the built-ins of show time and answers its result, stored bottom row
/// first as ISF draws. A buffer a pass draws into is read by that pass as it was before (each buffer has a front and a back), so
/// a feedback pass never reads what it writes.</summary>
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

    private readonly GlDevice _device;
    private readonly FxCompileLane _lane;
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
#pragma warning disable CA2213 // The lane owns its programs; a slot borrows one and never disposes it.
    private FxProgramEntry? _entry;
#pragma warning restore CA2213
    private IsfPass[] _passes = [];
    private IsfExpression[] _widths = [];
    private IsfExpression[] _heights = [];
    private Buffer[] _passBuffers = [];
    private (int W, int H)[] _sizes = [];
    private ImageBinding[] _images = [];
    private UniformBinding[] _uniforms = [];
    private int _renderW;
    private int _renderH;

    public IsfRenderer(GlDevice device, FxCompileLane lane, GpuTexture black, GpuTexture grey)
    {
        _device = device;
        _lane = lane;
        _black = black;
        _grey = grey;
    }

    public FxSlotState State { get; private set; } = FxSlotState.Compiling;

    public string Words { get; private set; } = "compiling";

    public FxKind? Kind => _kind;

    /// <summary>Takes the slot's record: a new kind or program starts again (buffers cleared, program asked for); new parameters
    /// only recompute the uniforms. Called when the slot's record is not the one it has.</summary>
    public void Configure(FxSlot slot, FxKind kind, IsfProgramSource source, ImmutableSortedDictionary<string, string> imports)
    {
        if (!ReferenceEquals(kind, _kind) || !ReferenceEquals(source, _source))
            Rebuild(kind, source, imports);
        _slot = slot;
        _vars.Clear();
        Span<float> v = stackalloc float[4];
        foreach (var u in _uniforms)
        {
            if (kind.Document.Input(u.Name) is { } input && FxKinds.TryUniform(input, kind, slot.Params, v))
                v.CopyTo(u.Value);
            _vars[u.Name] = u.Value[0];
        }
    }

    private void Rebuild(FxKind kind, IsfProgramSource source, ImmutableSortedDictionary<string, string> imports)
    {
        DisposeBuffers();
        _kind = kind;
        _source = source;
        _imports = imports;
        _entry = null;
        _name = kind.Id;
        _key = FxCatalog.ContentHash(source.Fragment, source.Vertex);
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
        _lane.Request(_key, _name, source.Vertex, source.Fragment, imports);
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

    /// <summary>The effect's result for this frame at a size, or null when it has none to give (compiling or faulted: the chain
    /// passes its input on). The texture is stored bottom row first and stays this renderer's.</summary>
    public GpuTexture? Apply(long frame, in FxClock clock, GpuTexture input, int width, int height)
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
            if (_entry.Program is null)
            {
                var issues = _source!.LineMap.Read(_entry.Error, vertex: _entry.Error?.Contains("(vertex)", StringComparison.Ordinal) ?? false);
                Fault(issues.Length > 0 ? Line(issues[0]) : _entry.Error ?? "the program did not build");
                return null;
            }
        }
        var entry = _entry;
        entry.LastUsed = frame;
        var program = entry.Program!;
        _renderW = width;
        _renderH = height;
        for (var i = 0; i < _passes.Length; i++)
        {
            var w = _widths[i].Evaluate(this, width, out _);
            var h = _heights[i].Evaluate(this, height, out _);
            _sizes[i] = (w, h);
            Ensure(_passBuffers[i], w, h);
        }
        _device.SetBlend(BlendMode.Opaque);
        for (var i = 0; i < _passes.Length; i++)
        {
            var buffer = _passBuffers[i];
            var (w, h) = _sizes[i];
            _device.BindTarget(buffer.Back!);
            program.SetInt("PASSINDEX", i);
            program.Set("RENDERSIZE", w, h);
            program.Set("TIME", clock.Time);
            program.Set("TIMEDELTA", clock.TimeDelta);
            program.SetInt("FRAMEINDEX", clock.FrameIndex);
            program.Set("DATE", clock.Year, clock.Month, clock.Day, clock.DaySeconds);
            for (var k = 0; k < _images.Length; k++)
                BindImage(program, entry, k, _images[k], input);
            foreach (var u in _uniforms)
                SetUniform(program, u);
            _device.DrawFullscreen(program);
            buffer.Swap();
        }
        State = FxSlotState.Ok;
        Words = "";
        return _passBuffers[^1].Front!.Color;
    }

    private void BindImage(ShaderProgram program, FxProgramEntry entry, int unit, ImageBinding image, GpuTexture input)
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
        _device.BindSampler(program, image.Name, unit, texture);
        program.Set(image.Size, texture.Width, texture.Height);
        program.Set(image.Rect, 0, 0, 1, 1);
        program.SetInt(image.Flip, flip ? 1 : 0);
    }

    private static void SetUniform(ShaderProgram program, UniformBinding u)
    {
        var v = u.Value;
        switch (u.Type)
        {
            case IsfInputType.Float:
                program.Set(u.Name, v[0]);
                break;
            case IsfInputType.Point2D:
                program.Set(u.Name, v[0], v[1]);
                break;
            case IsfInputType.Color:
                program.Set(u.Name, v[0], v[1], v[2], v[3]);
                break;
            default:
                program.SetInt(u.Name, (int)v[0]);
                break;
        }
    }

    /// <summary>A buffer at a size: made (both sides, cleared) when it has none or its size changed — a persistent buffer starts
    /// again from transparent black.</summary>
    private void Ensure(Buffer b, int w, int h)
    {
        if (b.Front is not null && b.Width == w && b.Height == h)
            return;
        b.Dispose();
        var format = b.Float ? TextureFormat.Rgba32F : TextureFormat.Rgba16F;
        b.Front = _device.CreateRenderTarget(w, h, format);
        b.Back = b.Single ? b.Front : _device.CreateRenderTarget(w, h, format);
        b.Width = w;
        b.Height = h;
        _device.BindTarget(b.Front);
        _device.Clear(0, 0, 0, 0);
        _device.BindTarget(b.Back);
        _device.Clear(0, 0, 0, 0);
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
