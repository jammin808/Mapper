using System.Collections.Immutable;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>What a frame's list was asked, in order: a wait for a value, or a draw and the textures it sampled; and, beside the lists,
/// what the device's upload ring and imports answered: a texture's copy or import and the value it is ready at.</summary>
internal readonly record struct Recorded(string Kind, FenceValue Value, GpuTexture? Texture, ImmutableArray<GpuTexture> Sampled);

/// <summary>A device as it is, its frame lists, upload ring and imports recorded (<see cref="Log"/>): which value each ring copy or
/// import came back with, for which texture, and in order each wait and each draw with the textures it sampled. Test only: it
/// allocates per call.</summary>
internal sealed class RecordingDevice(IGpuDevice inner) : IGpuDevice
{
    private RecordingUploads? _uploads;
    private RecordingImporter? _importer;

    public List<Recorded> Log { get; } = [];

    /// <summary>Whether every ring copy and import the log holds was waited for by a list before the first draw that sampled its
    /// texture; the words name the first that was not.</summary>
    public bool EverySampledValueWaitedFor(out string words)
    {
        for (var i = 0; i < Log.Count; i++)
        {
            var made = Log[i];
            if (made.Kind is not ("upload" or "import"))
                continue;
            var waited = false;
            for (var k = i + 1; k < Log.Count; k++)
            {
                var e = Log[k];
                if (e.Kind == "wait" && e.Value == made.Value)
                    waited = true;
                else if (e.Kind == "draw" && e.Sampled.Contains(made.Texture!) && !waited)
                {
                    words = $"a draw sampled the texture of {made.Kind} {made.Value} before any wait for it";
                    return false;
                }
            }
        }
        words = "";
        return true;
    }

    public GpuBackendKind Kind => inner.Kind;
    public GpuAdapterInfo Adapter => inner.Adapter;
    public GpuFeatures Features => inner.Features;
    public long Epoch => inner.Epoch;
    public IProgramLibrary Programs => inner.Programs;
    public IPipelineCache Pipelines => inner.Pipelines;
    public IUploadRing Uploads => _uploads ??= new RecordingUploads(this, inner.Uploads);
    public IReadbackRing Readbacks => inner.Readbacks;
    public IGpuTimers Timers => inner.Timers;
    public ITextureImporter Importer => _importer ??= new RecordingImporter(this, inner.Importer);
    public ITextureExporter? Exporter => inner.Exporter;

    public GpuTexture CreateTexture(in TextureDesc desc) => inner.CreateTexture(desc);
    public RenderTarget CreateTarget(in TargetDesc desc) => inner.CreateTarget(desc);
    public GpuBuffer CreateBuffer(in BufferDesc desc) => inner.CreateBuffer(desc);
    public void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged => inner.SetBufferData(buffer, data);

    public GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
        GpuBuffer? instances = null) => inner.CreateMesh(attributes, vertices, indices, instances);

    public ICommandList BeginFrame(long frame) => new RecordingList(this, inner.BeginFrame(frame));
    public FenceValue Submit(ICommandList list) => inner.Submit(((RecordingList)list).Inner);
    public bool IsComplete(in FenceValue value) => inner.IsComplete(value);
    public void WaitFor(in FenceValue value) => inner.WaitFor(value);
    public Task<ProgramMade> CreateProgramAsync(ProgramSource source, CancellationToken ct) => inner.CreateProgramAsync(source, ct);
    public void ReleaseProgram(ProgramId id) => inner.ReleaseProgram(id);
    public ITransientMemory CreateTransientMemory(long bytes, string name) => inner.CreateTransientMemory(bytes, name);
    public void SetPlanManaged(GpuResource resource, bool planManaged) => inner.SetPlanManaged(resource, planManaged);
    public IReadbackRing CreateReadbackRing(int slots = 3) => inner.CreateReadbackRing(slots);
    public IHeldReadbackRing CreateHeldReadbackRing(int slots = 4) => inner.CreateHeldReadbackRing(slots);
    public void ReadNow(RenderTarget target, Span<byte> bgra) => inner.ReadNow(target, bgra);
    public void ReadNowFloat(RenderTarget target, int attachment, Span<float> rgba) => inner.ReadNowFloat(target, attachment, rgba);

    public void Dispose()
    {
        // The leg owns the device.
    }

    private sealed class RecordingList(RecordingDevice device, ICommandList inner) : ICommandList, IDrawSink
    {
        public ICommandList Inner => inner;
        public QueueKind Queue => inner.Queue;

        public void BeginPass(RenderTarget target, in PassDesc pass) => inner.BeginPass(target, pass);
        public void EndPass() => inner.EndPass();

        public DrawBuilder Draw(ProgramId program, in DrawState state)
        {
            // The inner list checks the pass is open; its own builder is let go, the draw recorded here and handed on.
            _ = inner.Draw(program, state);
            var layout = device.Programs.Layout(program);
            return new DrawBuilder(this, program, state, layout, new byte[layout.ConstantBytes], new GpuTexture?[layout.Textures.Length]);
        }

        public void Record(in DrawCall call, ReadOnlySpan<byte> constants, ReadOnlySpan<GpuTexture?> textures)
        {
            var sampled = ImmutableArray.CreateBuilder<GpuTexture>();
            foreach (var t in textures)
                if (t is not null)
                    sampled.Add(t);
            device.Log.Add(new Recorded("draw", FenceValue.None, null, sampled.ToImmutable()));
            ((IDrawSink)inner).Record(call, constants, textures);
        }

        public void Missed(ProgramId program, string name) => ((IDrawSink)inner).Missed(program, name);

        public void Copy(GpuTexture source, GpuTexture dest) => inner.Copy(source, dest);
        public void Upload(GpuTexture dest, ReadOnlySpan<byte> pixels, UploadFormat format, int rowPixels = 0) => inner.Upload(dest, pixels, format, rowPixels);
        public void UploadCompressed(GpuTexture dest, nint blocks, int bytes) => inner.UploadCompressed(dest, blocks, bytes);
        public void Barrier(GpuResource resource, ResourceUse use) => inner.Barrier(resource, use);

        public void Wait(in FenceValue value)
        {
            device.Log.Add(new Recorded("wait", value, null, []));
            inner.Wait(value);
        }

        public void Alias(GpuResource? before, GpuResource after) => inner.Alias(before, after);
        public void Discard(GpuResource resource) => inner.Discard(resource);
        public void Marker(string pass) => inner.Marker(pass);
        public void Timestamp(int slot) => inner.Timestamp(slot);
    }

    private sealed class RecordingUploads(RecordingDevice device, IUploadRing inner) : IUploadRing
    {
        public IUploadArena? Arena => inner.Arena;

        public bool TryUpload(GpuTexture dest, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format, out FenceValue done) =>
            Recorded(dest, inner.TryUpload(dest, source, sourceStride, rowBytes, rows, format, out done), done);

        public bool TryUploadCompressed(GpuTexture dest, nint blocks, int bytes, out FenceValue done) =>
            Recorded(dest, inner.TryUploadCompressed(dest, blocks, bytes, out done), done);

        public IUploadArena? OpenArena(int megabytes, out string words) => inner.OpenArena(megabytes, out words);
        public void CloseArena() => inner.CloseArena();
        public bool InArena(in UploadBlock block) => inner.InArena(block);

        public bool TryUploadStaged(GpuTexture dest, in UploadBlock block, nint data, int bytes, out FenceValue done) =>
            Recorded(dest, inner.TryUploadStaged(dest, block, data, bytes, out done), done);

        private bool Recorded(GpuTexture dest, bool copied, FenceValue done)
        {
            if (copied)
                device.Log.Add(new Recorded("upload", done, dest, []));
            return copied;
        }
    }

    private sealed class RecordingImporter(RecordingDevice device, ITextureImporter inner) : ITextureImporter
    {
        public bool CanImport(GpuSurface surface, out string? words) => inner.CanImport(surface, out words);

        public ITextureImport OpenImport() => new RecordingImport(device, inner.OpenImport());
    }

    private sealed class RecordingImport(RecordingDevice device, ITextureImport inner) : ITextureImport
    {
        public bool TryImport(GpuSurface surface, out ImportedPlanes planes, out string? words)
        {
            if (!inner.TryImport(surface, out planes, out words))
                return false;
            device.Log.Add(new Recorded("import", planes.Ready, planes.Luma, []));
            if (planes.Chroma is { } chroma)
                device.Log.Add(new Recorded("import", planes.Ready, chroma, []));
            return true;
        }

        public void Dispose() => inner.Dispose();
    }
}
