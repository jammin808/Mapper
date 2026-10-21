using System.Diagnostics.CodeAnalysis;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Presentation;
using PlayMap.Engine.Rendering;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>A pipeline the device refuses to make — a program its driver will not link — fails the render host's start with the
/// device's words, so a node never comes up with a pass that no frame can draw.</summary>
public sealed class WarmUpHostTests
{
    private const string Refusal = "program 7 (link): the driver refused it";

    /// <summary>A device's pipelines whose warm-up answers one key it could not make.</summary>
    private sealed class RefusingPipelines(IPipelineCache inner) : IPipelineCache
    {
        public string Key => inner.Key;
        public int Count => inner.Count;
        public long RenderThreadMakes => inner.RenderThreadMakes;

        public bool TryGet(in PipelineKey key, [NotNullWhen(true)] out GpuPipeline? pipeline) => inner.TryGet(key, out pipeline);

        public Task<PipelineWarmResult> WarmAsync(IReadOnlyCollection<PipelineKey> keys, CancellationToken ct) =>
            Task.FromResult(new PipelineWarmResult(keys.Count, keys.Count - 1, 0, [Refusal]));
    }

    /// <summary>A device as it is but for its pipelines' warm-up.</summary>
    private sealed class RefusingDevice(IGpuDevice inner) : IGpuDevice
    {
        private readonly RefusingPipelines _pipelines = new(inner.Pipelines);

        public GpuBackendKind Kind => inner.Kind;
        public GpuAdapterInfo Adapter => inner.Adapter;
        public GpuFeatures Features => inner.Features;
        public long Epoch => inner.Epoch;
        public IProgramLibrary Programs => inner.Programs;
        public IPipelineCache Pipelines => _pipelines;
        public IUploadRing Uploads => inner.Uploads;
        public IReadbackRing Readbacks => inner.Readbacks;
        public IGpuTimers Timers => inner.Timers;
        public ITextureImporter Importer => inner.Importer;
        public ITextureExporter? Exporter => inner.Exporter;

        public GpuTexture CreateTexture(in TextureDesc desc) => inner.CreateTexture(desc);
        public RenderTarget CreateTarget(in TargetDesc desc) => inner.CreateTarget(desc);
        public GpuBuffer CreateBuffer(in BufferDesc desc) => inner.CreateBuffer(desc);
        public void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged => inner.SetBufferData(buffer, data);

        public GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
            GpuBuffer? instances = null) => inner.CreateMesh(attributes, vertices, indices, instances);

        public ICommandList BeginFrame(long frame) => inner.BeginFrame(frame);
        public FenceValue Submit(ICommandList list) => inner.Submit(list);
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
            // The platform owns the device.
        }
    }

    /// <summary>This machine's GL platform with its device's warm-up refusing a key.</summary>
    private sealed class RefusingPlatform(IGpuPlatform inner) : IGpuPlatform
    {
        private readonly RefusingDevice _device = new(inner.Device);

        public string Name => inner.Name;
        public string Api => inner.Api;
        public string Words => inner.Words;
        public IGpuDevice Device => _device;
        public PresentPath Path => inner.Path;
        public IGpuShare Share => inner.Share;
        public GpuDeviceLock? DeviceLock => inner.DeviceLock;
        public string Adapter => inner.Adapter;
        public string Dialect => inner.Dialect;
        public string ImportBinding => inner.ImportBinding;

        public IReadOnlyList<MonitorInfo> Monitors() => inner.Monitors();
        public IOutputPresenter CreatePresenter(OutputPresenterOptions options) => inner.CreatePresenter(options);
        public void PumpEvents() => inner.PumpEvents();
        public IGpuShare? OpenImports() => inner.OpenImports();
        public IHapBlockEncoder? CreateHapEncoder() => inner.CreateHapEncoder();
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task APipelineTheDeviceRefusesFailsTheStartWithTheDevicesWords()
    {
        var surfaceless = OperatingSystem.IsLinux();
        var made = false;
        var options = RenderHostOptions.Default with
        {
            Windows = false,
            // The platform maker the host is handed: this machine's GL, its device's warm-up refusing one key.
            Platform = new PlatformRequest("dxgi", Surfaceless: surfaceless),
            Dxgi = o =>
            {
                made = true;
                return new RefusingPlatform(GpuPlatforms.Create(new PlatformRequest("gl", Surfaceless: surfaceless), o));
            },
        };
        var host = RenderHost.TryCreate(options, out var why);
        if (host is not null)
            await host.DisposeAsync();
        Assert.True(made);
        Assert.Null(host);
        Assert.Contains(Refusal, why, StringComparison.Ordinal);
        Assert.Contains("could not make 1 of the", why, StringComparison.Ordinal);
    }
}
