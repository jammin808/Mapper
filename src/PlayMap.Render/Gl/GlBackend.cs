using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PlayMap.Core.Gpu;
using PlayMap.Core.Effects;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Gl;

/// <summary>The GL device behind the render hardware interface: OpenGL 3.3 core or ES 3.0 on one context, drawing immediately.
/// Resources are the device's GL objects; the frame's list is one immediate <see cref="ICommandList"/>, reused every frame; fences are
/// values over GL sync objects (<see cref="FenceRing"/>), one timeline for the frame's submits (the direct queue) and one for uploads
/// (the copy queue), both on the one context's stream; the pixel-unpack and pixel-pack rings stand behind <see cref="Uploads"/> and
/// <see cref="Readbacks"/>; timestamp queries behind <see cref="Timers"/>; the dmabuf and ANGLE importers behind
/// <see cref="Importer"/>; user programs and warm-ups are linked on the compile lane, a user program reflected into its layout;
/// transients are rented from a resource pool. It shares nothing through NT handles (<see cref="Exporter"/> is null) and is never rebuilt
/// (<see cref="Epoch"/> 1).
///
/// One backend belongs to one <see cref="GlDevice"/> (<see cref="GlDevice.Backend"/>), which disposes it.
///
/// Threading: the device's thread (the render thread) for everything but <see cref="IsComplete"/>, <see cref="CreateProgramAsync"/>,
/// <see cref="ReleaseProgram"/>, the pipeline cache's warm-up and its counts, which any thread may call. A user program and a
/// warm-up are made at the frames' starts (<see cref="BeginFrame"/>): a GL context is its thread's.</summary>
public sealed class GlBackend : IGpuDevice, IProgramLane
{
    private static readonly string[] SoftwareRenderers =
        ["llvmpipe", "softpipe", "swrast", "SwiftShader", "lavapipe", "Microsoft Basic Render", "WARP"];

    private sealed class Make(string name, string vertex, string fragment, CancellationToken ct)
    {
        public string Name { get; } = name;
        public string Vertex { get; } = vertex;
        public string Fragment { get; } = fragment;
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource<ProgramMade> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FxMaking? Lane { get; set; }
    }

    private readonly GlDevice _device;
    private readonly ProgramTable _table = new();
    private readonly GlPipelineCache _pipelines;
    private readonly GlCommandList _list;
    private readonly FenceRing _direct;
    private readonly FenceRing _copy;
    private readonly ResourcePool _pool;
    private readonly GlUploadRing _uploads;
    private readonly PixelPackRing _readbacks;
    private readonly GlTimers _timers;
    private readonly GlTextureImporter _importer;
    private readonly ConcurrentQueue<Make> _makes = new();
    private readonly List<Make> _making = [];
    private readonly ConcurrentQueue<ProgramId> _releases = new();
    private readonly List<ProgramId> _releasing = [];
    private FxCompileLane? _lane;
    private bool _laneShared = true;
    private Action<string>? _beforeMake;
    private bool _disposed;

    internal GlBackend(GlDevice device)
    {
        _device = device;
        _pipelines = new GlPipelineCache(device, _table, () => _lane ??= MakeLane());
        _list = new GlCommandList(this, device, _table.ProductCount == 0 ? 0 : ProgramTable.Product.Max(p => p.ConstantBytes));
        _direct = new FenceRing(QueueKind.Direct, device);
        _copy = new FenceRing(QueueKind.Copy, device);
        // The binding loads a GL entry point on its first call: the ring's own are called here once, so its first wrap, a ring's
        // length of frames into the show, loads nothing on the frame path.
        var probe = device.InsertFence();
        _ = device.IsSignaled(probe);
        device.DeleteFence(probe);
        _pool = new ResourcePool(this);
        _uploads = new GlUploadRing(device, _copy);
        _readbacks = new PixelPackRing(device);
        _timers = new GlTimers(device);
        _importer = new GlTextureImporter(device);
        Adapter = new GpuAdapterInfo(0, VendorOf(device.Vendor + " " + device.Renderer), device.Renderer, device.Version,
            string.Create(CultureInfo.InvariantCulture, $"OpenGL {device.GlVersion / 10}.{device.GlVersion % 10} {GlDialects.Word(device.Dialect)}"),
            SoftwareRenderers.Any(r => device.Renderer.Contains(r, StringComparison.OrdinalIgnoreCase)));
        Features = FeaturesOf(device, _timers.Measures);
    }

    public GpuBackendKind Kind => GpuBackendKind.Gl;

    public GpuAdapterInfo Adapter { get; }

    public GpuFeatures Features { get; }

    /// <summary>1: the GL backend is never rebuilt.</summary>
    public long Epoch => GlTexture.GlEpoch;

    public IProgramLibrary Programs => _table;

    public IPipelineCache Pipelines => _pipelines;

    public IUploadRing Uploads => _uploads;

    public IReadbackRing Readbacks => _readbacks;

    /// <summary>A pixel-pack ring of its own: each request reads the target into the next of its buffers with a fence behind it.</summary>
    public IReadbackRing CreateReadbackRing(int slots = 3)
    {
        ThrowIfDisposed();
        return new PixelPackRing(_device, slots);
    }

    /// <summary>A ring of pixel-pack buffers the reader holds mapped until it lets go of each read.</summary>
    public IHeldReadbackRing CreateHeldReadbackRing(int slots = 4)
    {
        ThrowIfDisposed();
        return new GlHeldReadbackRing(_device, slots);
    }

    public IGpuTimers Timers => _timers;

    public ITextureImporter Importer => _importer;

    /// <summary>The importer as the GL backend holds it (its held surfaces).</summary>
    internal GlTextureImporter GlImporter => _importer;

    /// <summary>The upload ring as the GL backend holds it (its arena).</summary>
    internal GlUploadRing GlUploads => _uploads;

    /// <summary>Null: GL shares no texture or fence through an NT handle.</summary>
    public ITextureExporter? Exporter => null;

    /// <summary>The pool transients are rented from (<see cref="CreateTransientMemory"/>).</summary>
    public ResourcePool TransientPool => _pool;

    internal ProgramTable ProgramTable => _table;

    /// <summary>The GL device under the backend.</summary>
    internal GlDevice Device => _device;

    internal GlPipelineCache PipelineCache => _pipelines;

    internal GlTimers GlTimers => _timers;

    /// <summary>Names a draw set that its program's layout does not hold, or values of the wrong kind for their constants, since the
    /// backend was made (each wrote nothing); the last one's program and name.</summary>
    internal long ConstantMisses { get; private set; }

    internal (ProgramId Program, string Name) LastMiss { get; private set; }

    internal void Missed(ProgramId program, string name)
    {
        ConstantMisses++;
        LastMiss = (program, name);
    }

    /// <summary>The direct queue's value the frame being recorded will take at its submit.</summary>
    internal ulong FrameValue => _direct.Last + 1;

    /// <summary>The device's compile lane, made the first time a user program or a warm-up's link is asked for (a second context
    /// sharing the device's, or the render thread where none can be made); null until then.</summary>
    internal FxCompileLane? CompileLane => _lane;

    // ---- resources ----

    /// <summary>A texture of the shape asked, a grey one through the texture's swizzle. A block format is made at whole 4×4 blocks
    /// and sampled nearest, filled only through <see cref="ICommandList.UploadCompressed"/>; a depth, storage or shared texture is
    /// refused with words (GL 3.3 makes a target's depth with the target, and shares nothing).</summary>
    public GpuTexture CreateTexture(in TextureDesc desc)
    {
        ThrowIfDisposed();
        if ((desc.Usage & (TextureUsage.DepthStencil | TextureUsage.Storage | TextureUsage.Shared)) != 0)
            throw new NotSupportedException($"The GL backend makes no {desc.Usage & (TextureUsage.DepthStencil | TextureUsage.Storage | TextureUsage.Shared)} texture: a target's depth is made with the target, and GL shares no texture.");
        if (TextureFormats.IsBlockCompressed(desc.Format))
            return _device.CreateCompressedTexture(desc.Width, desc.Height, desc.Format);
        var texture = _device.CreateTexture(desc.Width, desc.Height, desc.Format, desc.Filter, desc.Wrap, desc.Usage, desc.Name ?? "");
        if (desc.Swizzle == TextureSwizzle.Gray)
            _device.SetGraySwizzle(texture);
        return texture;
    }

    /// <summary>A target of the shape asked; one the context refuses (an incomplete framebuffer) throws with words, the device's state
    /// cache forgotten first (the make bound what it made).</summary>
    public RenderTarget CreateTarget(in TargetDesc desc)
    {
        ThrowIfDisposed();
        try
        {
            return _device.CreateTarget(desc);
        }
        catch (Exception)
        {
            _device.InvalidateState();
            throw;
        }
    }

    /// <summary>A buffer of the uses asked (an index buffer binds as GL's element array, every other as an array buffer), its store
    /// made at <see cref="BufferDesc.Bytes"/> when that is above 0.</summary>
    public GpuBuffer CreateBuffer(in BufferDesc desc)
    {
        ThrowIfDisposed();
        var buffer = _device.CreateBuffer((desc.Usage & BufferUsage.Index) != 0 ? BufferKind.Index : BufferKind.Vertex, desc.Usage,
            desc.Name ?? "");
        _device.ReserveBuffer(buffer, desc.Bytes);
        return buffer;
    }

    public void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        ThrowIfDisposed();
        _device.SetBufferData(buffer, data);
    }

    public GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
        GpuBuffer? instances = null)
    {
        ThrowIfDisposed();
        return _device.CreateVertexArray(attributes, vertices, indices, instances);
    }

    // ---- frames and fences ----

    /// <summary>Opens the frame's list. First it lets go of imported surfaces and user programs whose last reads completed, takes
    /// the user programs the compile lane finished, and runs the warm-up asked for.</summary>
    public ICommandList BeginFrame(long frame)
    {
        ThrowIfDisposed();
        if (_list.IsOpen)
            throw new InvalidOperationException("The last frame's list was not submitted: every BeginFrame ends with its Submit.");
        _importer.Collect(frame);
        PumpMakes(frame);
        SweepReleases();
        _pipelines.Pump();
        _pool.Trim();
        _list.Open(frame);
        return _list;
    }

    /// <summary>Closes the frame's list, signals the direct queue's next value after everything it issued, and hands the commands to
    /// the GPU (a flush, so the value completes without a later call).</summary>
    public FenceValue Submit(ICommandList list)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(list, _list))
            throw new ArgumentException("This list is not this device's frame list.", nameof(list));
        _list.Close();
        var value = _direct.Signal();
        _device.Flush();
        _importer.Submitted();
        _uploads.EndFrame();
        return value;
    }

    public bool IsComplete(in FenceValue value) => value.Queue == QueueKind.Copy ? _copy.IsComplete(value) : _direct.IsComplete(value);

    /// <summary>Waits for the GPU to finish everything issued, then answers the value: start-up, scans and tests only. A value not yet
    /// signalled is refused.</summary>
    public void WaitFor(in FenceValue value)
    {
        ThrowIfDisposed();
        if (IsComplete(value))
            return;
        var ring = value.Queue == QueueKind.Copy ? _copy : _direct;
        if (value.Value > ring.Last)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{value.Queue} value {value.Value} was never signalled (the last is {ring.Last}): nothing would complete it."));
        _device.Finish();
        if (!IsComplete(value))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{value.Queue} value {value.Value} did not complete after the GPU finished."));
    }

    // ---- user programs ----

    /// <summary>A user program from GLSL (UTF-8, dialect-free: the device writes the dialect's header): linked on the compile lane,
    /// its active uniforms and samplers read back into its layout, adopted at the start of a later frame. A refusal names its stage:
    /// "code" (not GLSL, or not UTF-8), "source" (a header in the source), "vertex", "fragment", "link", "layout" (a uniform no layout
    /// holds), "lane" (the lane failed or stopped).</summary>
    public Task<ProgramMade> CreateProgramAsync(ProgramSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Code != ProgramCode.Glsl)
            return Task.FromResult(new ProgramMade(false, default, null, "code", $"the GL backend makes programs from GLSL; '{source.Name}' is {source.Code}"));
        string vertex, fragment;
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            vertex = utf8.GetString(source.Vertex.Span);
            fragment = utf8.GetString(source.Pixel.Span);
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(new ProgramMade(false, default, null, "code", $"'{source.Name}' is not UTF-8 GLSL text"));
        }
        var make = new Make(source.Name, vertex, fragment, ct);
        _makes.Enqueue(make);
        return make.Result.Task;
    }

    /// <summary>Releases a user program once the last frame that drew with it completed (looked at as each frame begins). A product
    /// program is never released.</summary>
    public void ReleaseProgram(ProgramId id)
    {
        if (!_table.IsUser(id))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Program {id.Value} is a product program: it is never released."), nameof(id));
        _releases.Enqueue(id);
    }

    private void PumpMakes(long frame)
    {
        while (_makes.TryDequeue(out var make))
        {
            if (make.Ct.IsCancellationRequested)
            {
                make.Result.TrySetCanceled(make.Ct);
                continue;
            }
            _lane ??= MakeLane();
            make.Lane = _lane.Make(make.Name, make.Vertex, make.Fragment);
            _making.Add(make);
        }
        if (_lane is null)
            return;
        _lane.Collect();
        for (var i = _making.Count - 1; i >= 0; i--)
        {
            var make = _making[i];
            if (make.Lane?.Made is not { } made)
                continue;
            _making.RemoveAt(i);
            Adopt(make, made);
        }
    }

    private FxCompileLane MakeLane() =>
        new(_device.Context ?? throw new InvalidOperationException("This GL device has no render context to compile user programs beside."),
            _laneShared)
        {
            BeforeCompile = _beforeMake,
        };

    // ---- the lane, as the effects report it ----

    string IProgramLane.LaneMode => (_lane ??= MakeLane()).Mode;

    string IProgramLane.LaneWords => (_lane ??= MakeLane()).Words;

    /// <summary>Desktop GLSL 3.30 on a core context; any other is an ES one.</summary>
    IsfDialect IProgramLane.Dialect => _device.Dialect == GlDialect.Core330 ? IsfDialect.Glsl330 : IsfDialect.Es300;

    Action<string>? IProgramLane.BeforeMake
    {
        get => _beforeMake;
        set
        {
            _beforeMake = value;
            if (_lane is not null)
                _lane.BeforeCompile = value;
        }
    }

    /// <summary>The lane of the kind asked: made now if there is none; one of the other kind made again while nothing is in flight on
    /// it (a context that cannot make a second context runs the lane on the render thread whatever is asked).</summary>
    void IProgramLane.UseLane(bool sharedContext)
    {
        ThrowIfDisposed();
        if (_lane is not null && (_laneShared == sharedContext || !_makes.IsEmpty || _making.Count > 0 || _pipelines.Linking))
            return;
        _lane?.Dispose();
        _laneShared = sharedContext;
        _lane = MakeLane();
    }

    private void Adopt(Make make, FxMade made)
    {
        if (made.ProgramId == 0 || made.Layout is null)
        {
            make.Result.TrySetResult(new ProgramMade(false, default, null, made.Stage, made.Words));
            return;
        }
        var program = _device.AdoptProgram(made.ProgramId, made.Layout.Name);
        if (make.Ct.IsCancellationRequested)
        {
            program.Dispose();
            make.Result.TrySetCanceled(make.Ct);
            return;
        }
        var id = _table.AddUser(made.Layout);
        _pipelines.Register(new GlProgram(id, program, made.Layout));
        _list.EnsureConstants(made.Layout.ConstantBytes);
        if (!make.Result.TrySetResult(new ProgramMade(true, id, made.Layout, "", "")))
            Release(id);
    }

    private void SweepReleases()
    {
        while (_releases.TryDequeue(out var id))
            _releasing.Add(id);
        for (var i = _releasing.Count - 1; i >= 0; i--)
        {
            var id = _releasing[i];
            ulong last;
            try
            {
                last = _pipelines.ProgramOf(id).LastUse;
            }
            catch (ArgumentException)
            {
                // Released already (asked twice): nothing is left to let go.
                _releasing.RemoveAt(i);
                continue;
            }
            if (!_direct.IsComplete(new FenceValue(QueueKind.Direct, last)))
                continue;
            _releasing.RemoveAt(i);
            Release(id);
        }
    }

    private void Release(ProgramId id)
    {
        var program = _pipelines.ProgramOf(id);
        _pipelines.Unregister(id);
        program.Program.Dispose();
        _table.ReleaseUser(id);
    }

    // ---- transients ----

    /// <summary>A block a plan places transients in. On GL each placement is rented from <see cref="TransientPool"/> and the offset
    /// only checked against the block; disposing the block hands every placement back to the pool.</summary>
    public ITransientMemory CreateTransientMemory(long bytes, string name)
    {
        ThrowIfDisposed();
        return new GlTransientMemory(_pool, bytes, name);
    }

    /// <summary>No effect on GL: the backend tracks no resource states.</summary>
    public void SetPlanManaged(GpuResource resource, bool planManaged) => ArgumentNullException.ThrowIfNull(resource);

    // ---- reads ----

    public void ReadNow(RenderTarget target, Span<byte> bgra)
    {
        ThrowIfDisposed();
        _device.ReadPixels(target, bgra);
    }

    public void ReadNowFloat(RenderTarget target, int attachment, Span<float> rgba)
    {
        ThrowIfDisposed();
        _device.ReadAttachmentFloat(target, attachment, rgba);
    }

    // ---- what the device is ----

    /// <summary>The PCI vendor a GL names in its vendor or renderer string (ANGLE puts the GPU's in the renderer's); 0 for none
    /// (Mesa's software rasteriser).</summary>
    internal static int VendorOf(string words)
    {
        if (words.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return 0x10DE;
        if (words.Contains("AMD", StringComparison.Ordinal) || words.Contains("ATI ", StringComparison.Ordinal)
            || words.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return 0x1002;
        if (words.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return 0x8086;
        if (words.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return 0x1414;
        if (words.Contains("Apple", StringComparison.OrdinalIgnoreCase))
            return 0x106B;
        return 0;
    }

    /// <summary>What a context of these caps does with each format: sampled, target, blendable.</summary>
    internal static FrozenDictionary<TextureFormat, FormatSupport> FormatsOf(GlCaps caps) =>
        Enum.GetValues<TextureFormat>().ToFrozenDictionary(f => f,
            f => new FormatSupport(caps.Supports(f), caps.SupportsRenderTarget(f), caps.SupportsRenderTarget(f, blended: true)));

    /// <summary>The words that refuse each block format a context of these caps does not sample.</summary>
    internal static FrozenDictionary<TextureFormat, string> RefusalsOf(GlCaps caps) =>
        Enum.GetValues<TextureFormat>().Where(TextureFormats.IsBlockCompressed)
            .Select(f => (Format: f, Words: GlDevice.RefuseBlockFormat(caps, f)))
            .Where(r => r.Words is not null)
            .ToFrozenDictionary(r => r.Format, r => r.Words!);

    /// <summary>A device's features as a context of other caps would have them (its formats and their refusals): what a test stands
    /// in for a context that lacks a family.</summary>
    internal static GpuFeatures WithCaps(GpuFeatures features, GlCaps caps) =>
        features with { Formats = FormatsOf(caps), Refusals = RefusalsOf(caps) };

    private static GpuFeatures FeaturesOf(GlDevice device, bool timers)
    {
        var formats = FormatsOf(device.Caps);
        var words = ImmutableArray.CreateBuilder<string>();
        if (!timers)
            words.Add("no timestamp queries on this context (ES 3.0): GPU times read nothing");
        if (!device.HasReadBgra)
            words.Add("no BGRA read-back (EXT_read_format_bgra): streamed read-backs are off");
        if (!device.HasPolygonMode)
            words.Add("no polygon mode (ANGLE_polygon_mode): triangles asked for as a wireframe are not drawn");
        var model = device.Dialect == GlDialect.Es300 ? "GLSL ES 3.00" : "GLSL 3.30";
        return new GpuFeatures(formats, device.BlockFormats, timers, SharedHandles: false, model, words.ToImmutable())
        {
            MaxTextureSize = device.MaxTextureSize,
            Readback = device.HasReadBgra,
            Refusals = RefusalsOf(device.Caps),
        };
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Lets go of everything the backend made for itself (its rings, queries, pool, user programs, lane and fences) and
    /// answers every program and warm-up still waiting. The device's context is current on its thread. The device stays.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pipelines.Abandon();
        while (_makes.TryDequeue(out var make))
            make.Result.TrySetResult(new ProgramMade(false, default, null, "lane", "the renderer stopped"));
        _lane?.Dispose();
        foreach (var make in _making)
        {
            if (make.Lane?.Made is { ProgramId: not 0 } made)
                _device.AdoptProgram(made.ProgramId, made.Layout?.Name ?? make.Name).Dispose();
            make.Result.TrySetResult(new ProgramMade(false, default, null, "lane", "the renderer stopped"));
        }
        _making.Clear();
        _pipelines.DisposeUserPrograms();
        _importer.Dispose();
        _uploads.Dispose();
        _readbacks.Dispose();
        _timers.Dispose();
        _pool.Dispose();
        _direct.Dispose();
        _copy.Dispose();
    }
}
