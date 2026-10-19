# Design: `wp3-d3d12-passes` and `-2` — the render graph, compiled when the show changes

*How the frame will stop being walked afresh every frame and become an immutable plan compiled off the render thread: what each
pass declares, what the compiler computes, how the render thread walks a plan, which plans exist before the show, and how
pipelines, bindless records and the allocation fence close the "never compile, allocate or wait on the render thread" rule.
Package sections: `PLAN.md` §3.8 and §3.13. The map's correction stands: today there is no declarative graph, only
`RenderEngine.Render`'s fixed order with five `IFramePass` hooks [map §2.3, §11.2].*

**The decision in one paragraph.** Every stage of the frame and every pass will declare the textures it reads and writes through
a `GraphBuilder`. A pure compiler in Core will turn the frame's topology and those declarations into a `FramePlan`: steps in the
frame's documented order, unused steps culled, transient textures with their lifetimes and aliasing, a barrier before every use
that needs one, copy-queue work with its fence handshake, and the pipeline keys the plan draws with. Plans are compiled on the
render host's plan thread when the topology changes — not when time, levels or modulated numbers do — published by volatile
reference and walked by the render thread from its next frame. When the show is armed, the plans of every state it can reach
without an edit are compiled, and their pipelines made, before ARM answers — or ARM's answer names each one still missing, with
preflight red. ARM itself is never refused for them: armed is the safe side, as for every other red preflight row.

---

## 1. The bar

| Must | Proved by |
|---|---|
| The frame's order and doctrine unchanged: blackout before content code, raw scan frames skip every picture call, a held output keeps its picture, a warm frame allocates nothing | the goldens through the plan equal the immediate frame's, byte for byte on GL |
| The topology compiled once per change, never per frame | `PlanSwapTests`: a frame with an unchanged topology does not compile; a change compiles once |
| Transients alias safely; anything that lives across frames never aliases | `GraphCompilerTests`; `AliasingParityTests` on WARP |
| Every barrier the Direct3D 12 debug layer would ask for, and none twice | `AliasingParityTests` with barrier validation on, no message |
| No pipeline made, no allocation, no wait on the render thread during an armed show | `PipelineWarmTests`, `ZeroAllocTests` (wp3-d3d12-passes-2) |
| The graph uses the RHI's own transient contract and adds none in W2 | `ITransientMemory`, `Alias`, `Discard` and `SetPlanManaged` are wp3-rhi's contract and wp3-d3d12-core's primitives (`rhi.md` §3.3, `d3d12.md` §6–§7); this package only calls them |

## 2. What a frame declares

```csharp
namespace PlayMap.Core.Graph;

public readonly record struct GraphTexture(int Index);

public readonly record struct TextureShape(int Width, int Height, TextureFormat Format, TextureFormat? Format1 = null,
    bool Depth = false);

public enum GraphAccess { Sampled, RenderTarget, CopySource, CopyDest, Readback, Present }

/// <summary>Why an imported texture lives outside one frame — it is never aliased.</summary>
public enum ImportKind { DelayRing, History, ParticleState, IsfPersistent, PreviewShared, BackBuffer, External }

/// <summary>What one node of the frame does to which textures. Filled by the frame's own stages and by every pass's
/// <c>Declare</c>; a ref struct over the compiler's scratch, so declaring allocates nothing the plan keeps.</summary>
public ref struct GraphBuilder
{
    public GraphTexture Import(string name, ImportKind kind, in TextureShape shape);
    public GraphTexture Create(string name, in TextureShape shape);
    public void Read(GraphTexture texture, GraphAccess access = GraphAccess.Sampled);
    public GraphTexture Write(GraphTexture texture, GraphAccess access = GraphAccess.RenderTarget);
    /// <summary>The pipelines this node may draw with (its program × state × the formats it writes).</summary>
    public void Draws(ReadOnlySpan<PipelineKey> keys);
    public void OnQueue(QueueKind queue);
    /// <summary>Work whose result leaves the GPU (LED samples, a send's readback): never culled.</summary>
    public void LeavesTheGpu();
}
```

`IFramePass` gains one method beside its existing calls, with a default that declares nothing:

```csharp
public interface IFramePass : IDisposable
{
    // Name, Order, Item, Layer, Surface, Picture, Final, FrameEnd — as today.

    /// <summary>What this pass reads and writes for one node of the frame (an item, a layer, a surface, an output's picture,
    /// an output's final raster, or the frame's end). Called on the plan thread when the topology changes, never per frame.</summary>
    void Declare(ref GraphBuilder graph, in PassNode node)
    {
        // A pass that draws nothing of its own declares nothing.
    }
}

public readonly record struct PassNode(PassNodeKind Kind, string Id, GraphTexture Input, TextureShape Shape);
public enum PassNodeKind { Item, Layer, Surface, Picture, Final, FrameEnd }
```

The frame's own stages declare in the order `ARCHITECTURE.md` §5 gives: sources (video planes and their `yuv` conversion, HAP
blocks, generators, live inputs), canvases (each layer's picture, its chain, the composite), chained surfaces at the largest
projector raster, each output's picture by mode (projector, canvas, source), its `Picture` passes, its output stage (warp, masks,
blend, black level, trim, dither, the air's gain, the watermark), its identify card and `Final` passes, the delay ring (imported),
the present (the presenter's back buffer, imported), then the frame's end (LED sampling, sends — `LeavesTheGpu`), then previews
(after the frame, never moving the air's present).

## 3. The topology

```csharp
/// <summary>Everything the plan's shape depends on, and nothing that changes per frame. Its hash decides whether a plan is
/// compiled: time, the air's levels, modulated numbers, a held output and the blackout flag are not in it.</summary>
public sealed record FrameTopology(
    ImmutableArray<OutputShape> Outputs,       // id, mode, raster, final format, delay frames, frames in flight, presenter kind
    ImmutableArray<CanvasShape> Canvases,      // size; layers with their chain's slot kinds
    ImmutableArray<SurfaceShape> Surfaces,     // chained surfaces at the largest projector raster
    ImmutableArray<SourceShape> Sources,       // videos, HAP, generators, live inputs, Stagewatch views: their texture shapes
    ImmutableArray<SendShape> Sends,           // taps and packed formats
    ImmutableArray<LedShape> Leds,             // sampling targets
    ImmutableArray<PreviewShape> Previews,     // requested views and their transport (ring or shared)
    AirRouting Air)                            // which outputs the air's pattern or a safe look covers
{
    public long Hash { get; }
}
```

The render host builds the topology on its plan thread from the project snapshot, the open outputs and the air's routing, the
same inputs `RenderEngine` reads today, and compares its hash with the current plan's.

## 4. The compiler

```csharp
public sealed class FramePlan
{
    public long TopologyHash { get; }
    public ImmutableArray<PlanStep> Steps { get; }
    public ImmutableArray<PlanTexture> Textures { get; }
    public ImmutableArray<PipelineKey> Pipelines { get; }
    public int Transients { get; }
    public long AliasedBytes { get; }
    public double CompileMs { get; }
}

public readonly record struct PlanStep(int Node, QueueKind Queue, ImmutableArray<PlanBarrier> Before, FenceValue? WaitFor,
    bool SignalAfter);
public readonly record struct PlanBarrier(int Texture, GraphAccess From, GraphAccess To);
public readonly record struct PlanTexture(string Name, TextureShape Shape, ImportKind? Imported, int FirstStep, int LastStep,
    int MemorySlot);

public static class GraphCompiler
{
    /// <summary>Pure: the same topology and declarations give the same plan, by value and by hash.</summary>
    public static FramePlan Compile(FrameTopology topology, ReadOnlySpan<NodeDeclaration> nodes, in GraphOptions options);
}

public readonly record struct GraphOptions(bool Alias, bool CopyQueue);
```

Its steps:

1. **Order.** Nodes in the frame's documented order (§2); a node's position never moves another's.
2. **Cull.** A node whose writes reach no present, no readback, no import and no `LeavesTheGpu` is dropped (an output shown nowhere
   and previewed by no one; a layer under an opaque one is not culled — it is drawn, as today, because its effect slots may keep
   history).
3. **Lifetimes.** Each transient's first and last step.
4. **Aliasing.** Transients with disjoint lifetimes share a memory slot when their shapes match exactly (first fit, by size then
   format); an imported texture never aliases. With `Alias` off every transient has its own slot — the missing-barrier check.
5. **Barriers.** Before each step, a transition for each texture whose access changes; none where it does not; a texture's first
   use in a frame starts from where the plan left it at the previous frame's end.
6. **Queues.** Copy work (uploads, readbacks) placed on the copy queue with a signal after and a `WaitFor` on the direct step that
   reads it; with `CopyQueue` off, everything runs on the direct queue (GL's case, and the fallback).
7. **Pipelines.** The union of the nodes' `Draws` keys, resolved against the target formats the plan assigned.

## 5. Publication and walking

- **The plan thread.** The render host's plan thread (a dedicated thread at normal priority, not the thread pool, its claim the
  platform's `Background` role) compiles when the topology's hash changes, materialises the plan's memory slots through
  `IGpuDevice.CreateTransientMemory` and `ITransientMemory.Place` on Direct3D 12 (placed resources are free-threaded there), marks
  each plan resource `SetPlanManaged`, and publishes the plan with a volatile write. On GL, `Place` rents from `ResourcePool` on the
  render thread at the plan's first frame, as targets are today.
- **The walk.** At the start of each frame the render thread reads the newest plan once; `RenderEngine`'s frame becomes the plan's
  walker — for each step it issues the step's barriers (an `Alias` where a transient takes over a memory slot, a `Discard` on its
  first use), waits (on the GPU) for its copy value, and calls the same stage code as
  today with the textures the plan assigned. The doctrine's checks stay where they are: blackout clears before any content step,
  a raw scan frame skips the picture steps, a held output keeps its last picture and skips its own steps.
- **The swap.** A plan replaced mid-run is walked from the next frame; the old one is released when the frames that walked it have
  completed. The swap allocates nothing on the render thread.

## 6. Plans before the show (wp3-d3d12-passes, item 5)

When the show is armed (through the spine's `IShowArmHook`: asynchronous, cancellable, with a deadline, run by `SHOW ARM` before
it answers), the plan thread compiles the plans of every topology the show can reach without an edit: each playlist
item's chains and transitions (both sides of each fade), each panic outcome's safe look on its outputs, the air's test patterns,
and the blind draft's editor view. They are kept by hash, so reaching one swaps a pointer.

## 7. Pipelines at arm, pre-roll (wp3-d3d12-passes-2, item 1)

- On `SHOW ARM` (its `IShowArmHook`) and on `GPU WARM`, the union of every kept plan's pipeline keys — user effects' included,
  which their passes declare (wp3-shaders-2) — goes to `IPipelineCache.WarmAsync`. ARM waits for them up to the hook's deadline and
  answers once they are made. If any is still missing, or failed, ARM still arms, as it does with any red preflight row (armed is
  the safe side, and SHOW ARM is never refused for a fact), and its answer names each missing key with the compile's words;
  preflight's row `pipelines 410/412` is red (fix `GPU WARM`) and `render.pipelines.missingForShow` counts them.
- A key still missing at air time holds its step: the step's target keeps its previous picture; a step that has never drawn passes
  its input through, as a quarantined effect does, and a source step with no input shows black. The hold is counted in STATE
  (`heldSteps`), named in preflight, and nothing is compiled on the render thread.
- The cache is keyed by adapter LUID, driver version and build (`d3d12.md` §8); after a driver update the next arm warms
  everything again and says why.
- **Pre-roll.** Before the node says it is ready (STATE `ready`), one frame of each kept plan is recorded and discarded, so every
  per-frame path has run its first, unoptimised call before the air depends on it. The call sits in the render host's start, which
  wp3-present owns in W3: a hunk of a few lines there, landing first in W3's merge order so present rebases on it (`PLAN.md` §4,
  §5.3); the pre-roll itself is in this package's `RenderHost.Plan.cs`. With ReadyToRun in the installer's publish (wp3-installer),
  this is M13's default.

## 8. Bindless records (wp3-d3d12-passes-2, item 2)

The stage pass draws each projector's surfaces today with a draw per surface, binding its content, mask and blend. On Direct3D 12
each surface becomes a record in a structured buffer:

```hlsl
struct SurfaceRecord
{
    uint mesh;        // index of the surface's vertex and index ranges in the stage's mesh buffer
    uint content;     // descriptor index of its content picture
    uint mask;        // descriptor index of its UV-space mask (or the white texture)
    uint blend;       // descriptor index of the projector's blend map
    float4 uvRegion;  // canvas region or projection parameters
    uint mode;        // UV, canvas region or projection from a viewpoint
};
```

A projector's surfaces are then drawn by a handful of indirect draws (`ExecuteIndirect` with one argument per surface range), the
pixel shader reading its record by the draw's id. This package adds the two contracts that takes to `Core/Gpu` (its owner in W3):
`DrawBuilder.Buffer(string name, GpuBuffer buffer)` for a structured buffer by the layout's slot, and
`ICommandList.DrawIndirect(GpuBuffer arguments, int count, GpuBuffer? countBuffer)`, both behind `GpuFeatures.Indirect`, which GL
3.3 reports false. Descriptor indices are freed only after the fence value of the last frame that
used them. The GL backend keeps today's draw per surface; the pixels must be equal either way (`BindlessTests`).

## 9. Zero allocation on Direct3D 12 (wp3-d3d12-passes-2, item 3)

The allocation fence (`GC.GetAllocatedBytesForCurrentThread` equal across N warm frames) runs on WARP for every pixel scene. The
one known gap is round 2's: a frame whose layers, surfaces or effect slots are modulated builds a drawn copy of the project each
frame. The modulation bus will write its drawn numbers into a per-frame table the passes read (fixed size, made at the topology's
compile) instead of a new `Project`; the project's base values stay in the store. Those files are round 2's and no package owns
them in W3: this package changes them through named hunks — `src/PlayMap.Core/Modulation/{ModulationBus, ModulationPlan}.cs`
(the table and its writer) and `src/PlayMap.Engine/Audio/EngineCore.Mod.cs` (the drawn project's builder gives way to the table) —
with `ModulationTests` unchanged.

## 10. Facts in STATE

```json
"render": {
  "graph": { "passes": 37, "culled": 4, "transients": 12, "aliasedBytes": 66355200, "compileMs": 3.2, "plansKept": 18,
             "compiledAt": 1760600000000 },
  "pipelines": { "count": 412, "missingForShow": 0, "renderThreadMakes": 0, "heldSteps": 0 },
  "passes": [ { "name": "stage op_1", "gpuUsP50": 812, "gpuUsP95": 905 } ],
  "cpuMsP95": 2.4
}
```

## 11. Verbs

`GPU WARM [WAIT]` (Low, Perform): makes every kept plan's pipelines now; `WAIT` answers when done, with made, from cache and
failed counts. Refused with words when there is nothing to make ("all 412 pipelines are made").

## 12. Tests

| Test | Job | Holds |
|---|---|---|
| `GraphCompilerTests` | both | two outputs × three surfaces × two chains → the documented order; culling; aliasing of disjoint lifetimes only; imports never aliased; a barrier before each changed use, none twice; equal plans by value and hash |
| `TopologyHashTests` | both | time, levels, modulated values, a hold and the blackout flag leave the hash; an output, a chain, a canvas size, a send or a preview change it |
| `PlanWalkTests` | both | every golden scene through the plan equals the immediate frame byte for byte on GL; within tolerance on WARP |
| `AliasingParityTests` | Windows | aliasing on and off draw the same pixels; no debug-layer message with barrier validation |
| `PlanSwapTests` | both | an edit swaps the plan from the next frame, allocation-free; the old plan released after its frames complete |
| `ArmedPlansTests` | both | arming compiles the reachable plans; a transition's plan exists before its window opens |
| `PipelineWarmTests` | Windows | ARM answers after every key is made; a key forced missing → ARM's answer names it, `show.armed` true, preflight's `pipelines` row red, the step held and counted at air time, no render-thread make; a failed key the same, with the compile's words |
| `BindlessTests` | Windows | 1, 10, 100 surfaces: the same pixels as per-surface draws; draws per output bounded; no descriptor reused before its fence |
| `ZeroAllocTests` | both | every scene, modulated layers included |
| `PassTimingTests` | Windows | each step's GPU time in STATE |

## 13. Risks

- **Declarations that lie.** A pass that writes what it did not declare corrupts an alias. Caught by the aliasing-off comparison
  and by the debug layer's barrier validation, both hard gates on the Windows job.
- **Too many reachable plans.** A show with hundreds of items keeps hundreds of plans; plans are small (steps and indices), and
  their memory slots are materialised only when a plan is about to be walked. Measured at the integration pass.
- **History in effects.** ISF persistent buffers and the particle field's state are imports; an effect slot added mid-show starts
  its history at the frame its plan is first walked, as today.
