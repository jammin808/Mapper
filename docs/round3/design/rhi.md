# Design: `wp3-rhi` — the cut at `GlDevice`

*The interface every renderer file will draw through, so a Direct3D 12 device can stand where the GL context stands today.
Written against `main` at `23f162f` and the map's §2 (cited **[map §n]**). Package section: `PLAN.md` §3.2. The Direct3D 12 side
of every contract is in `d3d12.md`; the render graph that later drives the same contracts is in `graph.md`.*

**The decision in one paragraph.** The RHI's contracts will live in Core (`PlayMap.Core.Gpu`), so both backends are edges that
reference Core and nothing else of PlayMap's. Everything a backend implements or feeds moves to Core with them: the platform and
presenter seams (`IGpuPlatform`, `IOutputPresenter`), the program table (each product program's name and layout, as data read by
the GL backend, the Direct3D 12 backend and the HLSL build alike), and — moved by the spine — the vblank clock, the present
ledger and the present words (`PlayMap.Core.Presentation`). They keep the resource names callers already use (`GpuTexture`, `RenderTarget`,
`GpuBuffer`) as abstract classes, so most call sites change only where they set state or draw. A draw names a program by id and
its fixed state as one `DrawState`; constants are set by name through the program's layout without allocating; textures bind by
the layout's slot names; fences are values on a queue's timeline. A user's effect is a program made at run time
(`CreateProgramAsync`), from GLSL on GL and from the shader worker's DXIL on Direct3D 12, and answers the same `ProgramId` and
`ProgramLayout` a product program does. The render graph's transient memory, aliasing barriers and discards are in the contract
from the start, so the graph's package adds no contract mid-wave. The GL backend implements all of it immediately, as it draws
today. The cut's one acceptance is that every golden image stays byte for byte what it is.

The contracts as built add to these shapes and differ from them in places; `../notes/rhi.md` §2 lists each, with its file and
caller, and §3 where the code proves this design and its neighbours wrong.

---

## 1. The bar

| Must | How it is proved |
|---|---|
| GL pixels unchanged | every Render and Engine pixel test passes with no golden file edited (llvmpipe here, Mesa WGL on the Windows job) |
| No GL type outside the GL backend | `RhiFenceTests` scan the assemblies' types and the sources |
| A D3D12 device can implement every contract without a caller changing | the contracts take no GL concept: no bound-state machine, no uniform locations, no sync object handles, no texture units |
| Nothing allocates on a warm frame | the render host's existing `GC.GetAllocatedBytesForCurrentThread` count stays 0 |
| No compile on the render thread is required by the contracts | `IPipelineCache.TryGet` never compiles; warm-up is a separate, asynchronous call |

## 2. Where things live

| Project | Holds | References |
|---|---|---|
| `PlayMap.Core` (`Core/Gpu/`) | the contracts below, the format vocabulary (moved from `Render/Gpu/GpuTypes.cs`, same names and values), `ProgramLayout` and `ProgramId`, `DrawState`, `PipelineKey`, `FenceValue`, `GpuFeatures`; `IGpuPlatform`, `IOutputPresenter`, `OutputPresenterOptions` and `MonitorInfo` (moved from `Render/Platform`); the program table (`Core/Gpu/Programs/programs.layout.json`, embedded, and `ProgramTable : IProgramLibrary` reading it) | the runtime |
| `PlayMap.Core` (`Core/Presentation/`, moved by the spine) | `VblankClock`, `PresentLedger`, `PresentClock` with `PresentSnapshot`, `PresentMode` and `ModeEvidence`, `LatencyPlan`, `PresentPath`, `PresentProblem`, `PresentResult` — same names, pure | the runtime |
| `PlayMap.Render` | passes, generators, projection, video, LED, sends, overlay, effects, `ResourcePool` (now over `IGpuDevice`), the GLSL library (`ShaderLibrary`, text only); `Render/Gl/**` — the GL backend (`GlBackend`, `GlDevice` and its partials, `GlDialect`, `RenderContext`, the GL platforms and windows, the compile lane, the dmabuf and ANGLE importers, the GL HAP encoder) | Core, Silk.NET (in `Gl/` only), SkiaSharp |
| `PlayMap.Render.D3D12` | the Direct3D 12 backend (`d3d12.md`): its device, its platform and presenters (wp3-present), its barrier tier (wp3-framelock) | Core, TerraFX |
| `PlayMap.Engine` | composes a backend through `IGpuPlatform`, handing each the program table | all |

The program table is one file for three readers: `ProgramTable` (Core) serves both backends' `IGpuDevice.Programs`; the HLSL build
generates each program's slot header from it (`shaders.md` §3); `ProgramLayoutTests` hold it equal to the GLSL declarations. It is
generated once from the GLSL library by a test-time tool and committed; a program's layout changes only with its sources.

`RhiFenceTests` keep the line: no type from `Silk.NET.OpenGL` and no `Gl*` backend type is named outside `Render/Gl/**`; Core's
`Gpu` and `Presentation` namespaces name no Render type; `PassFrame` and every `IFramePass` signature carry RHI types only. The
spine's `ModuleRuleTests` hold the project line: `Render.D3D12` references Core and its native packages, never `Render`.

## 3. The contracts

### 3.1 Resources

```csharp
namespace PlayMap.Core.Gpu;

[Flags]
public enum TextureUsage
{
    Sampled = 1, RenderTarget = 2, DepthStencil = 4, Storage = 8, CopySource = 16, CopyDest = 32,
    /// <summary>Opened by another device or process through an NT handle (previews, Spout's bridge).</summary>
    Shared = 64,
}

public readonly record struct TextureDesc(int Width, int Height, TextureFormat Format, TextureUsage Usage,
    TextureFilter Filter = TextureFilter.Linear, TextureWrap Wrap = TextureWrap.Clamp, string Name = "");

/// <summary>A target's attachments: one or two colour textures (the particle step writes two) and an optional depth.</summary>
public readonly record struct TargetDesc(int Width, int Height, TextureFormat Color0, TextureFormat? Color1 = null,
    bool Depth = false, TextureFilter Filter = TextureFilter.Linear, string Name = "");

public readonly record struct BufferDesc(long Bytes, BufferUsage Usage, string Name = "");

[Flags] public enum BufferUsage { Vertex = 1, Index = 2, Instance = 4, Constant = 8, Structured = 16, Indirect = 32 }

/// <summary>Anything a device made. Disposal on any thread: the backend releases it only once the GPU has finished with it
/// (GL at once, the driver keeping it alive; Direct3D 12 when the fence value of its last use completes).</summary>
public abstract class GpuResource : IDisposable
{
    public string Name { get; }
    /// <summary>The device epoch it belongs to: a resource of an older epoch is gone (the device was rebuilt).</summary>
    public long Epoch { get; }
    public bool IsDisposed { get; }
    public abstract void Dispose();
}

public abstract class GpuTexture : GpuResource
{
    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }
    public TextureFilter Filter { get; }
    public TextureWrap Wrap { get; }
    public TextureUsage Usage { get; }
    /// <summary>Bumped by whoever draws into it (as today): caches compare it to know a picture changed.</summary>
    public long Version { get; set; }
}

public abstract class RenderTarget : GpuResource
{
    public GpuTexture Color { get; }
    public GpuTexture? Color1 { get; }
    public bool HasDepth { get; }
    public int Width => Color.Width;
    public int Height => Color.Height;
}

public abstract class GpuBuffer : GpuResource
{
    public long Bytes { get; }
    public BufferUsage Usage { get; }
}

/// <summary>Vertex, index and instance buffers with their attribute layout (today's VertexArray).</summary>
public abstract class GpuMesh : GpuResource
{
    public GpuBuffer? Vertices { get; }
    public GpuBuffer? Indices { get; }
    public GpuBuffer? Instances { get; }
    public ImmutableArray<VertexAttribute> Attributes { get; }
}
```

`StateTarget` (the particle field's two float attachments) becomes a `RenderTarget` with `Color1`.

### 3.2 Programs, state and pipelines

```csharp
/// <summary>A program of the library by index: resolved once, at a pass's construction, from its name.</summary>
public readonly record struct ProgramId(int Value);

public enum ConstantType { Float, Float2, Float3, Float4, Int, Int2, Int4, Float3x3, Float4x4 }

public readonly record struct ConstantSlot(string Name, ConstantType Type, int Offset, int Count = 1);

/// <summary>A texture the program reads: its name, its slot (0–15), and whether it is sampled or only loaded.</summary>
public readonly record struct TextureSlot(string Name, int Slot, bool Sampled);

/// <summary>What a program takes, the same on every backend: its constants packed in one block (16-byte rules, as an HLSL
/// cbuffer packs them), its textures by slot. The GL backend sets the block's members as uniforms of the same names.</summary>
public sealed record ProgramLayout(string Name, ImmutableArray<ConstantSlot> Constants, ImmutableArray<TextureSlot> Textures,
    int ConstantBytes)
{
    /// <summary>The slot of a constant or texture by name, from a frozen table built once: no allocation on look-up.</summary>
    public bool TryConstant(string name, out ConstantSlot slot);
    public bool TryTexture(string name, out TextureSlot slot);
}

public interface IProgramLibrary
{
    ProgramId Id(string name);                 // a product program; throws with words for an unknown name (at construction, never mid-frame)
    ProgramLayout Layout(ProgramId id);        // product and user programs alike
    IReadOnlyList<string> Names { get; }       // the product programs, in the table's order
}

/// <summary>A program made at run time — a user's ISF effect, one entry per pass. GL takes GLSL text; Direct3D 12 takes the shader
/// worker's DXIL and its reflection blob. The kind's hash keys the quarantine and the pipeline cache.</summary>
public sealed record ProgramSource(string Name, string KindHash, ProgramCode Code, ReadOnlyMemory<byte> Vertex,
    ReadOnlyMemory<byte> Pixel, ReadOnlyMemory<byte> Reflection);

public enum ProgramCode { Glsl, Dxil }

/// <summary>What a make answered: the program and its reflected layout, or the stage that refused and its words.</summary>
public sealed record ProgramMade(bool Ok, ProgramId Id, ProgramLayout? Layout, string Stage, string Words);

public enum DepthMode { Off, TestWrite, TestOnly }
public enum Topology { Triangles, TriangleStrip, Lines, LineStrip, Points }

/// <summary>Every piece of fixed-function state a draw sets, as one value (GL's loose state, D3D12's pipeline state).</summary>
public readonly record struct DrawState(BlendMode Blend, DepthMode Depth, CullMode Cull, Topology Topology, bool Wireframe = false)
{
    public static readonly DrawState Opaque = new(BlendMode.Opaque, DepthMode.Off, CullMode.None, Topology.Triangles);
}

/// <summary>A pipeline's identity: the program, the state and the formats of the target it draws into.</summary>
public readonly record struct PipelineKey(ProgramId Program, DrawState State, TextureFormat Color0, TextureFormat? Color1,
    bool Depth);

public abstract class GpuPipeline : GpuResource
{
    public PipelineKey Key { get; }
}

public interface IPipelineCache
{
    /// <summary>"adapter LUID · driver version · build" — a different key means every pipeline is made again.</summary>
    string Key { get; }
    int Count { get; }
    /// <summary>Pipelines made on the render thread because no warm-up had made them (D3D12 counts; GL's are cheap and counted too).</summary>
    long RenderThreadMakes { get; }
    /// <summary>The pipeline, if made. Never makes one: the render thread calls only this.</summary>
    bool TryGet(in PipelineKey key, [NotNullWhen(true)] out GpuPipeline? pipeline);
    /// <summary>Makes the pipelines off the render thread (GL: on the compile lane's shared context where there is one, else one a
    /// frame on the render thread, counted, as today's surfaceless path does).</summary>
    Task<PipelineWarmResult> WarmAsync(IReadOnlyCollection<PipelineKey> keys, CancellationToken ct);
}

public sealed record PipelineWarmResult(int Asked, int Made, int FromCache, ImmutableArray<string> Failed);
```

Until wp3-d3d12-passes-2 makes every key before the show, a `TryGet` miss inside a `Draw` makes the pipeline there and counts it
(`RenderThreadMakes`) — the honest interim, visible in STATE from wp3-d3d12-core on.

Reflection makes a user program's layout: GL reads its active uniforms and samplers after linking (as `IsfRenderer` does today);
Direct3D 12 reads the worker's reflection blob. Its constants and textures are then set by name through that layout, exactly as a
product program's.

### 3.3 Commands

```csharp
public enum LoadOp { Load, Clear, DontCare }

public readonly record struct PassDesc(LoadOp Load, Vector4 Clear, float ClearDepth = 1, int X = 0, int Y = 0, int Width = 0,
    int Height = 0)   // a zero size is the whole target
{
    public static readonly PassDesc ClearBlack = new(LoadOp.Clear, new Vector4(0, 0, 0, 1));
    public static readonly PassDesc Keep = new(LoadOp.Load, default);
}

/// <summary>How a resource is about to be used: the state a barrier moves it to. Both backends take the call; GL ignores it.</summary>
public enum ResourceUse { Sampled, RenderTarget, DepthWrite, CopySource, CopyDest, Present, Storage, ShaderRead }

/// <summary>One queue's recording for one frame. Recorded by one thread (the render thread for the direct queue; an upload lane
/// for its copy list); submitted once. Every method is allocation-free.</summary>
public interface ICommandList
{
    QueueKind Queue { get; }
    void BeginPass(RenderTarget target, in PassDesc pass);
    void EndPass();
    /// <summary>Starts a draw of a program with a state into the current pass's target.</summary>
    DrawBuilder Draw(ProgramId program, in DrawState state);
    void Copy(GpuTexture source, GpuTexture dest);
    /// <summary>Pixels into a texture through the frame's upload ring (GL: straight into the texture).</summary>
    void Upload(GpuTexture dest, ReadOnlySpan<byte> pixels, UploadFormat format, int rowPixels = 0);
    void UploadCompressed(GpuTexture dest, nint blocks, int bytes);
    /// <summary>An explicit transition. A backend that tracks states itself takes it as a hint; the render graph's plans
    /// issue every one they need.</summary>
    void Barrier(GpuResource resource, ResourceUse use);
    /// <summary>The GPU waits for another queue's value before what follows (GL: nothing to wait for).</summary>
    void Wait(in FenceValue value);
    /// <summary>The memory under <paramref name="after"/> held <paramref name="before"/> until now (two transients aliased by the
    /// render graph): an aliasing barrier on Direct3D 12; nothing on GL, whose transients are pooled, never aliased.</summary>
    void Alias(GpuResource? before, GpuResource after);
    /// <summary>The resource's old content is not needed (a transient's first use in a frame): <c>DiscardResource</c> on Direct3D 12,
    /// nothing on GL.</summary>
    void Discard(GpuResource resource);
    /// <summary>A marker for PIX and DRED's breadcrumbs: a pass's name, a literal (never built per frame).</summary>
    void Marker(string pass);
    void Timestamp(int slot);
}

/// <summary>One draw being built: constants by name through the program's layout, textures by slot name, then the draw itself.
/// A ref struct over the list's constant-ring page: nothing escapes the frame, nothing allocates.</summary>
public ref struct DrawBuilder
{
    public DrawBuilder Set(string name, float x);
    public DrawBuilder Set(string name, float x, float y);
    public DrawBuilder Set(string name, float x, float y, float z, float w);
    public DrawBuilder Set(string name, Vector4 v);
    public DrawBuilder Set(string name, in Matrix4x4 m);
    public DrawBuilder Set(string name, in Matrix3x3 m);
    public DrawBuilder SetInt(string name, int value);
    /// <summary>The texture's own filter and wrap choose its sampler.</summary>
    public DrawBuilder Texture(string name, GpuTexture texture);
    public void Fullscreen();
    public void GeneratedQuads(int instances = 1);
    public void Vertices(GpuMesh mesh, int vertexCount, int first = 0);
    public void Indexed(GpuMesh mesh, int indexCount);
    public void Instanced(GpuMesh mesh, int vertexCount, int instances);
}
```

**The render graph's memory.** Transients live in memory the plan places them in, and the backend's own state tracking steps aside
for the resources a plan manages:

```csharp
/// <summary>One block of memory a plan places transient textures in at the offsets its aliasing chose (wp3-d3d12-passes). GL
/// rents each placement from <c>ResourcePool</c> and ignores the offset; Direct3D 12 places resources in one heap.</summary>
public interface ITransientMemory : IDisposable
{
    long Bytes { get; }
    /// <summary>The bytes and alignment a texture of this shape takes here, so the plan can lay transients out.</summary>
    (long Bytes, long Align) SizeOf(in TextureDesc desc);
    GpuTexture Place(in TextureDesc desc, long offset);
}

// on IGpuDevice
ITransientMemory CreateTransientMemory(long bytes, string name);
/// <summary>A resource a plan manages: the backend's automatic barriers stop for it and the plan's own take over (GL: no effect).</summary>
void SetPlanManaged(GpuResource resource, bool planManaged);
```

Indirect draws and structured-buffer bindings (the bindless surface records of `graph.md` §8) join the contract in W3 with
wp3-d3d12-passes-2, behind `GpuFeatures.Indirect`; GL 3.3 has neither and keeps its draw per surface.

A constant the layout does not hold is a programming error caught by `ProgramLayoutTests` (every `Set` and `Texture` name in the
source checked against its program's layout), not a run-time look-up failure. Every constant a draw's layout holds and the draw did
not set is zero on both backends — the house rule the output stage's comments already follow ("a uniform left unset reads 0").

### 3.4 Queues, fences, rings

```csharp
public enum QueueKind { Direct, Copy }

/// <summary>A point on one queue's timeline.</summary>
public readonly record struct FenceValue(QueueKind Queue, ulong Value)
{
    public static readonly FenceValue None = new(QueueKind.Direct, 0);
}

/// <summary>Pixels to the GPU off the render thread: a lane records copies into its own list and submits them; the render list
/// waits on the returned value (on the GPU). Replaces the pixel-unpack ring and the upload arena's GL half.</summary>
public interface IUploadRing
{
    bool TryUpload(GpuTexture dest, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format, out FenceValue done);
}

/// <summary>Pictures back to the CPU, never waited on: a request after a pass, collected frames later when its value completed.
/// Replaces <c>PixelPackRing</c>; the send stage, LED sampling and previews use it.</summary>
public interface IReadbackRing
{
    bool Request(RenderTarget target, long stamp);
    bool TryCollect<TState>(TState state, ReadbackConsumer<TState> consume);
    int Pending { get; }
}

public interface IGpuTimers
{
    int Slots { get; }
    /// <summary>The elapsed GPU time between two timestamps of a completed frame, µs; false until it completed.</summary>
    bool TryElapsedUs(long frame, int fromSlot, int toSlot, out double microseconds);
}
```

### 3.5 Imports and exports

```csharp
/// <summary>A decoder's or another device's surface as a texture the passes sample (dmabuf EGLImages and ANGLE's D3D11 slices on
/// GL; D3D12VA resources and shared NT handles on Direct3D 12). The surface is held until the returned value completes.</summary>
public interface ITextureImporter
{
    bool CanImport(in GpuSurface surface, out string? words);
    bool TryImport(GpuSurface surface, out ImportedPlanes planes, out string? words);
}

public readonly record struct ImportedPlanes(GpuTexture Luma, GpuTexture? Chroma, FenceValue Ready, int CropWidth, int CropHeight);

/// <summary>A texture and a fence opened by another process or device (previews, Spout's bridge). Null on a backend that shares
/// nothing (GL).</summary>
public interface ITextureExporter
{
    SharedTexture Export(GpuTexture texture);
    SharedFence ExportFence();
}

public sealed record SharedTexture(nint NtHandle, long AdapterLuid, int Width, int Height, TextureFormat Format);
public sealed record SharedFence(nint NtHandle, ulong Value);
```

### 3.6 The device

```csharp
public enum GpuBackendKind { Gl, D3D12 }

public sealed record GpuAdapterInfo(long Luid, int VendorId, string Name, string Driver, string Api, bool Software);

/// <summary>What the device can do, asked once at creation: per format, sampled, target, blendable; the block formats; timers;
/// shared handles; the shader model on D3D12. A format the device lacks is refused with words, never silently replaced.</summary>
public sealed record GpuFeatures(FrozenDictionary<TextureFormat, FormatSupport> Formats, BlockFormats Blocks, bool Timers,
    bool SharedHandles, string ShaderModel, ImmutableArray<string> Words);

public readonly record struct FormatSupport(bool Sampled, bool Target, bool Blend);

public interface IGpuDevice : IDisposable
{
    GpuBackendKind Kind { get; }
    GpuAdapterInfo Adapter { get; }
    GpuFeatures Features { get; }
    /// <summary>Bumped by every rebuild (wp3-stability-2); every resource carries the epoch it was made in.</summary>
    long Epoch { get; }
    IProgramLibrary Programs { get; }
    IPipelineCache Pipelines { get; }

    GpuTexture CreateTexture(in TextureDesc desc);
    RenderTarget CreateTarget(in TargetDesc desc);
    GpuBuffer CreateBuffer(in BufferDesc desc);
    void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged;
    GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
        GpuBuffer? instances = null);

    /// <summary>The frame's direct list (render thread).</summary>
    ICommandList BeginFrame(long frame);
    FenceValue Submit(ICommandList list);
    bool IsComplete(in FenceValue value);
    /// <summary>Blocks until the value completes: start-up, scans and tests only — the render loop never calls it mid-show.</summary>
    void WaitFor(in FenceValue value);

    /// <summary>A user program, made off the render thread: GLSL on the compile lane's shared context (GL); a pipeline state from
    /// DXIL and reflection (Direct3D 12). The id is valid on this device's epoch only.</summary>
    Task<ProgramMade> CreateProgramAsync(ProgramSource source, CancellationToken ct);
    /// <summary>Releases a user program after the last frame that drew with it completes.</summary>
    void ReleaseProgram(ProgramId id);

    ITransientMemory CreateTransientMemory(long bytes, string name);
    void SetPlanManaged(GpuResource resource, bool planManaged);

    IUploadRing Uploads { get; }
    IReadbackRing Readbacks { get; }
    IGpuTimers Timers { get; }
    ITextureImporter Importer { get; }
    ITextureExporter? Exporter { get; }

    /// <summary>A target's pixels now, waiting for the GPU: tests and snapshots only (<c>ReadOutput</c>, <c>ReadPresented</c>).</summary>
    void ReadNow(RenderTarget target, Span<byte> bgra);
    void ReadNowFloat(RenderTarget target, int attachment, Span<float> rgba);
}
```

### 3.7 The platform seam

`IGpuPlatform` and `IOutputPresenter` move from `src/PlayMap.Render/Platform` to `src/PlayMap.Core/Gpu` (with
`OutputPresenterOptions` and `MonitorInfo`), so a Direct3D 12 platform in `Render.D3D12` implements them without referencing Render.
`IGpuPlatform.Context` (a GL `RenderContext`) becomes `IGpuPlatform.Device` (an `IGpuDevice`); its other members stay (its `Share`
and `DeviceLock` are Core's types already). The GL platforms (GLFW, EGL, ANGLE) move under `Render/Gl/`. The presenter splits its
present in two, so the copy into a swap chain's buffer is recorded in the frame's list and the present follows the submit:

```csharp
namespace PlayMap.Core.Gpu;

public interface IOutputPresenter : IDisposable
{
    // Width, Height, IsClosing, Path, SwapInterval, SetSwapInterval, LastSwapNs, Observe(PresentClock) — as today, the
    // present types now in PlayMap.Core.Presentation.

    /// <summary>Records the picture into the presenter's next buffer in the frame's list (GL: the window's default framebuffer,
    /// the only flip; Direct3D 12: the swap chain's current back buffer).</summary>
    void Record(ICommandList list, GpuTexture picture);

    void RecordBlack(ICommandList list);

    /// <summary>After the frame's list is submitted: queues the buffer for display. Never blocks longer than its wait for the
    /// vertical blank when it is the pacing output.</summary>
    PresentResult Present();

    /// <summary>The frame-latency waitable where the platform has one (DXGI), else null: the loop waits on it before sampling time.</summary>
    WaitHandle? FrameLatencyWaitable => null;
}
```

## 4. Threading

| What | Thread |
|---|---|
| `BeginFrame`, the direct list, `Submit`, presents | the render thread, as today |
| A copy list (`IUploadRing`) | the upload lane that owns it (one per lane); its `Submit` from the same lane |
| `CreateTexture`, `CreateTarget`, `CreateBuffer` | the render thread on GL (a GL context is one thread's); any thread on Direct3D 12 — so callers keep making resources on the render thread, and only new Direct3D 12 lanes use the freedom |
| `Dispose` of any resource | any thread; the backend defers the release to the resource's last fence value |
| `IPipelineCache.WarmAsync`, `CreateProgramAsync` | any thread; GL runs both on the compile lane's shared context |
| `IsComplete` | any thread; lock-free |

One recording thread per list is the rule GL already keeps (one context, one thread); the contracts never require a lock on the
render thread.

## 5. How the GL backend implements it

| Contract | GL |
|---|---|
| `BeginPass` | bind the framebuffer, set the viewport, clear when `LoadOp.Clear` |
| `Draw(program, state)` → `DrawBuilder` | the state cache applies blend, depth, cull, polygon mode only where they differ (today's skipping, kept); `glUseProgram`; constants set as uniforms through cached locations, last values compared as `ShaderProgram` does today; textures bound to the unit equal to the layout's slot |
| `PipelineKey` | a program plus a state set: "making" one is compiling the program if not yet linked |
| `FenceValue` | a ring of GL sync objects keyed by value; `IsComplete` polls `glClientWaitSync` with a zero timeout |
| `Barrier`, `Wait` | nothing (GL orders work itself) |
| `IUploadRing` | today's `PixelUnpackRing` and persistent upload arena, behind the interface |
| `IReadbackRing` | today's `PixelPackRing`, behind the interface |
| `IGpuTimers` | timer queries, where the context has them |
| `ITextureImporter` | today's `DmaBufImporter` and `AngleImporter` |
| `ITextureExporter` | null |
| `CreateProgramAsync` | today's `FxCompileLane` compile and link of the translated GLSL, its uniforms and samplers read back into a `ProgramLayout` |
| `ITransientMemory`, `Alias`, `Discard`, `SetPlanManaged` | placements rented from `ResourcePool`; the rest nothing |
| `Epoch` | 1, until wp3-stability-2's rebuild on GL |

## 6. How callers move: the recipe

Every caller of bands 2–3 [map §2.1] follows the same five steps; the output stage shows them.

```csharp
// before (Passes/OutputStage.cs)
d.BindTarget(target);
d.SetBlend(BlendMode.Opaque); d.SetDepth(false, false); d.SetCull(CullMode.None);
d.Clear(0, 0, 0, 1);
var p = d.Program("output");
p.SetInt("uRotation", input.RotationQuarters);
d.BindSampler(p, "uPicture", 0, input.Picture);
// … the other constants and samplers …
p.Set("uAir", input.Gain ?? 1f);
d.DrawIndexed(p, input.Warp.Vao, PrimitiveKind.Triangles, input.Warp.IndexCount);
target.Color.Version++;

// after
private static readonly ProgramId Output = Programs.Id("output");          // once, at construction
list.BeginPass(target, PassDesc.ClearBlack);
list.Draw(Output, DrawState.Opaque)
    .SetInt("uRotation", input.RotationQuarters)
    .Texture("uPicture", input.Picture)
    // … the other constants and textures, unchanged names …
    .Set("uAir", input.Gain ?? 1f)
    .Indexed(input.Warp.Mesh, input.Warp.IndexCount);
list.EndPass();
target.Color.Version++;
```

1. The device a pass held becomes the frame's `ICommandList` (from `PassFrame.List`) plus the `IGpuDevice` for resources.
2. `BindTarget`, `SetViewport` and `Clear` become one `BeginPass`.
3. `SetBlend`, `SetDepth`, `SetCull`, `SetWireframe` become one `DrawState` value (a `static readonly` per call site).
4. `Program(name)` becomes a `ProgramId` resolved at construction; `p.Set` and `BindSampler` become the builder's calls with the same
   names; the explicit texture units go (the layout's slot is the unit).
5. `DrawFullscreen`, `DrawGeneratedQuads`, `Draw`, `DrawIndexed`, `DrawInstanced` become the builder's terminal call.

The compile lane and the HAP encoder, the two files that call GL through `RenderContext.Gl` [map §2.2], move inside the GL backend
whole: the lane serves the GL `IPipelineCache.WarmAsync` and `CreateProgramAsync`; the encoder stays a GL user in the optimise
worker this round. The effect code that compiled user GLSL through `GlDevice` (`IsfRenderer`, `FxChainRunner`, `EffectGenerator`)
asks `CreateProgramAsync` instead and draws the answer through the builder like any program; on Direct3D 12 the same code receives
the worker's DXIL (wp3-shaders-2).

## 7. Facts and verbs

None. The cut changes no STATE key, no word and no verb; every fixture is regenerated byte for byte as part of the acceptance. The
backend's name reaches STATE when wp3-d3d12-core adds `render.backend`.

## 8. Tests

| Test | What it holds |
|---|---|
| every existing Render and Engine test | unchanged results; no golden file edited |
| `RhiFenceTests` | §2's lines, by reflection over the assemblies and a scan of the sources |
| `DrawStateTests` | each `DrawState` maps to exactly one `PipelineKey` for a target; a scripted frame's GL state read back after each draw equals what the old calls set, draw by draw |
| `ProgramLayoutTests` | each layout lists exactly the uniforms and samplers its GLSL declares (name, type); every `Set`/`Texture` name in Render's sources exists in its program's layout; layouts pack as an HLSL cbuffer does (offsets checked against the 16-byte rules) |
| `FenceValueTests` | values grow per queue; `IsComplete` monotonic; a ring of 64 GL syncs wraps without losing a value |
| `UserProgramTests` | a translated ISF source through `CreateProgramAsync` on GL → a layout equal to its declared uniforms and samplers; a compile error → the stage and the ISF line; the program released after its last frame; the FX pixel tests unchanged through it |
| `TransientContractTests` | on GL: placements come from the pool, `Alias` and `Discard` change no pixel, `SetPlanManaged` has no effect |
| `BackendFixture` | runs each scene on every backend the job has (GL today; Direct3D 12 on WARP from wp3-d3d12-core); `Pixels.Read(IGpuDevice, RenderTarget)` |
| `AllocationTests` | warm frames allocate nothing through the builder (it is a ref struct over a ring) |

## 9. Risks and open questions

- **The size of the cut** (`PLAN.md` R1). Mitigated by keeping the resource names and the uniform names, so most of the fifty
  files change only in the five places §6 lists.
- **Uniform names through a look-up per call.** A frozen dictionary per program costs a hash per `Set`. If the hottest passes show
  it in the frame time, the stretch's generated ids replace names there; the contract does not change.
- **Sampler state on the texture.** GL keeps filter and wrap on the texture object; Direct3D 12 samples through separate samplers.
  The contract keeps filter and wrap on the texture and lets the backend pick one of four static samplers per slot (`d3d12.md`
  §4), which every program here needs — no program samples one texture two ways.
- **Implicit GL behaviour the callers rely on** (a read of a target just drawn needs no barrier; deleting a texture in flight is
  safe). The contracts make both explicit (`Barrier`, deferred release); GL ignores the first and already does the second.
