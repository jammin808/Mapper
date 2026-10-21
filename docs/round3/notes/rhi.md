# The render hardware interface (`wp3-rhi`): papers note

*For the integration pass (`PLAN.md` §4, §5.6). The package's text for `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`
and the agent brief stands in them (§1 says where). This note holds the contracts as they are built where they differ from
`design/rhi.md`, where the code proves a design wrong, the hunks outside the package's Owns in the order they merge, and what waits
for the Windows job and the lab.*

## 1. Where the papers carry the package

- `README.md`: the opening's line on what the exe draws through, the round-3 paragraph's lines on the interface and the module
  table's `PlayMap.Render` row.
- `docs/ARCHITECTURE.md`: the opening's direction paragraph; the module table's `PlayMap.Core` and `PlayMap.Render` rows; §5's
  first line, its paragraph on the render hardware interface ("Every pass draws through …") and the direction paragraph after it.
- `docs/ROADMAP.md`: the opening, round 3's "Today", the section "Built: the render hardware interface" and the item's row in the
  table after it.
- `CLAUDE.md` (the agent brief): the engine's description, the GL line in "Constraints that bite" and the render hardware
  interface's row in "Where things live".
- `docs/round3/README.md` (the round's opening and its index row for `notes/`), `docs/COMPETITIVE.md` (the direction paragraph and
  the Render row) and `docs/RESEARCH.md` (the GPU and presentation line): the RHI built, the rest of round 3 not.

## 2. The contracts as built

Where `src/PlayMap.Core/Gpu` differs from `design/rhi.md` §3, each with its file and the callers that need it. wp3-d3d12-core
implements these, not the design's shapes where the two differ.

| Contract | As built | File | Needed by |
|---|---|---|---|
| `TextureDesc.Swizzle` | `TextureSwizzle.None` or `Gray`: a one-channel texture sampled as grey (red in red, green and blue, alpha one) | `GpuResources.cs` | `VideoTextureSet` (Gray8 pictures: stills, the folder camera) |
| `BufferUsage.Dynamic` | 64: data set again every frame or so, kept where the CPU writes cheaply (GL a streaming store, Direct3D 12 upload memory); without it a buffer is static (GL a static store, Direct3D 12 device memory) | `GpuResources.cs` | dynamic: `ParticleGenerator`, `OverlayPass`'s cursors, `StageRenderer`'s lines; static: `GpuCaches`, `StageRenderer`'s meshes, `EffectGenerator`, `OverlayPass`'s shapes |
| Buffers' content | a desc of 0 bytes makes no store until the first set; a set replaces the content for the draws recorded after it, and the draws recorded or submitted before it keep theirs (the backend renames the store and releases the old one at the last fence that read it); `GpuBuffer.Bytes` is the content's size | `GpuDevice.cs`, `GpuResources.cs` | every buffer maker (each makes at 0 bytes and sets) |
| `ProgramLayout.Inputs` | each product program's vertex inputs (location, float count, offset, stride, buffer 0 or 1, divisor), in the table as `"inputs"`; empty for a program that makes its geometry from the vertex index and for a user program | `Programs.cs`, `Programs/ProgramTable.cs`, `Programs/programs.layout.json` | a pipeline's input layout before any mesh is at hand (`d3d12.md` §8); `ProgramLayoutTests` hold every mesh a pass draws to it |
| `ConstantId`, `TextureId`, `ProgramIds` | a constant or texture by its place in a product program's layout, generated with the table (`Programs/ProgramIds.g.cs`, named `ProgramIds` because `Ids` is `PlayMap.Core.Model.Ids`); `DrawBuilder.Set(ConstantId, …)`, `SetInt(ConstantId, …)`, `Texture(TextureId, …)` | `Programs.cs`, `Commands.cs` | five passes: the particle step (`FlowGenerator`), the canvas quad (`GeneratorRenderer`), the raymarcher, `OutputStage`, `StageRenderer` |
| `ConstantTypes` | sizes, words and the block's packing rules (a scalar or vector does not cross a register; a matrix row and an array element start one) | `Programs.cs` | `ProgramTable`, both backends' constant blocks, the HLSL build's slot headers |
| The matrices | a `Float4x4` is a row-vector transform (the shader computes v·M: HLSL `row_major`, `mul(v, M)`); a `Float3x3` a column-vector one (M·v: `row_major`, `mul(M, v)`); both lay one row a register | `Programs.cs`, `Commands.cs` | the HLSL library (`shaders.md`), the normal matrix of `StageRenderer` |
| `ClipSpace` | clip y = −w is the target's top row, z from −w to w is depth 0 to 1, `CullMode.Back` culls what is clockwise in the raster seen top row first; Direct3D 12 converts as the last step of each vertex stage (`ToDirect3D`: y negated, z to (z + w) / 2) and culls with `FrontCounterClockwise = TRUE`, `CULL_MODE_BACK` | `GpuTypes.cs` | every HLSL vertex entry, d3d12-core's rasteriser state |
| `BlendMode` | every blended mode leaves the target's alpha as it was; `Opaque` writes all four channels | `GpuTypes.cs` | d3d12-core's blend descriptions (no write mask on `Opaque`) |
| `DrawBuilder` | a `readonly ref struct` a list builds over its `IDrawSink` (`Record(in DrawCall, constants, textures)`, `Missed(program, name)`), with `DrawCall` and `DrawShape`; the list hands it a zeroed block; a name the layout does not hold, or a value of the wrong kind, writes nothing and is told to `Missed`; `Set(name, x, y, z)` besides the design's overloads | `Commands.cs` | every backend's list |
| `ICommandList.Wait` | a list waits, outside a pass, for every other-queue value it samples (a ring copy, a block plane, an import) before the pass that samples it; a backend whose queues wait only between submissions holds the whole list until each value completes | `Commands.cs` | `VideoTextureSet`, `PackedUnpack` |
| `ITransientMemory` | adds `SizeOf(in TargetDesc)` and `PlaceTarget(in TargetDesc, long)` (what a pass draws into: a placed texture is only sampled or copied); a placement lives until the memory is disposed; one that does not fit at its offset is refused | `Commands.cs` | the render graph's plans (wp3-d3d12-passes); `TransientContractTests` |
| `PipelineKey.For` | the one key a draw of a program with a state into a target takes | `Pipelines.cs` | the lists, `FramePipelines` |
| `IUploadRing` | adds `TryUploadCompressed` and the upload arena (`Arena`, `OpenArena`, `CloseArena`, `InArena`, `TryUploadStaged`): GPU-visible host memory HAP's decode threads write pictures into, read where written | `Queues.cs` | `VideoTextureSet` (HAP blocks), `RenderHost.Decode` (opens the arena) |
| Read-backs | `IReadbackRing : IDisposable`; `IGpuDevice.CreateReadbackRing(slots)`, a ring per reader (reads come back in its own order); `IHeldReadbackRing` with `HeldReadback` and `CreateHeldReadbackRing(slots)`, reads kept in place until the reader lets go | `Queues.cs`, `GpuDevice.cs` | `RenderEngine` (previews), `LedPass`, `PackedSend` (a send's frames read by its sender's thread) |
| `IGpuTimers` | adds `Holds(frame)` (a frame's timestamps still kept) and `Reserve(slots)` (grows the slots between frames); `Slots` is 0 where nothing is measured; `TryElapsedUs` is false for a pair not measured | `Queues.cs` | `GpuSpans` (`GpuTimeSlots`, `GpuSpanTimer`): every span a frame asks for is measured |
| Imports | `CanImport(GpuSurface, out string?)` takes no `in`; `OpenImport()` gives a reader its own `ITextureImport` (`TryImport`, `Dispose`): a surface is held from its import, while its lease is alive, until the list it was imported in has completed; false with no words means every slot of the reader is in flight | `Imports.cs` | `VideoTextureSet` (one import per set) |
| `ImportedPlanes` | adds `CodedWidth`, `CodedHeight` (the planes' size, padding included; 0 is the crop) | `Imports.cs` | `VideoTextureSet`'s conversion |
| `GpuFeatures` | adds `MaxTextureSize` (16384 where a device says nothing), `Readback`, `Refusals` and `RefusalOf(format)` | `GpuDevice.cs` | `GeneratorHost`, `SendStage` (sizes), `LedPass` (read-backs), `VideoTextureSet` and the desk (refusal words) |
| `GpuException` | a device's refusal or failure in its own words, the one kind frame code catches | `GpuDevice.cs` | `RenderHost`, `RenderEngine.WarmUp`, `OutputWindows`, `FxChainRunner`, `OptimiseWorker` |
| `IGpuDevice.BeginFrame` | one frame number opens several lists in turn (the frame's own, a list per preview drawn after it, a resolve or an upload between frames, those outside the frames numbered 0); the number is a label, and a backend fences a list's resources by the value its `Submit` returns | `GpuDevice.cs` | `d3d12.md` §3 |
| `IGpuPlatform` | adds the default members `Dialect`, `ImportBinding`, `OpenImports()` and `CreateHapEncoder()` | `GpuPlatform.cs` | `RenderHost` (STATE's dialect, the decoders' share), `OptimiseWorker` (the HAP encoder) |

Beside the contracts, in Render: `RenderEngine.WarmUp` makes the pipelines the passes list (`FramePipelines`) and no other,
returns the device's `PipelineWarmResult` (null once `WarmUpBound` passed, the render host journalling a fault), and throws a
`GpuException` naming each pipeline the device could not make, which fails the render host's start with those words.

## 3. Where the code proves a design wrong

- **`d3d12.md` §8, the input layout.** A key carries no mesh, and `TryGet` and `WarmAsync` take only keys, so the mesh's attributes
  cannot give the input layout of a pipeline warmed before the show; `sprite`'s instance inputs (buffer 1, divisor 1) cannot be
  inferred from DXIL at all. The input layout comes from the program table (`ProgramLayout.Inputs`). `d3d12.md` §8 says so.
- **`d3d12.md` §3, frames in flight.** One frame number opens several direct lists (the frame's, each preview's, resolves, uploads),
  so frame resources keyed by frame number or held one per displayed frame either reset an allocator still running or run out of
  slots. Lists take their resources from pools fenced per submission. `d3d12.md` §3 says so, and its summary, its `Queues/` folder,
  its start's `rings` stage, its `b0` row and §6's constants and uploads follow it.
- **`shaders.md`, the conventions.** The two matrix types are read opposite ways round (`Float4x4` with `mul(v, M)`, `Float3x3`
  with `mul(M, v)`), and the texture origin agrees with Direct3D's while the rasteriser's does not (clip y = +w is Direct3D's top
  row, and it clips z < 0): every vertex entry ends with the clip-space conversion. `shaders.md`'s table says so; wp3-shaders,
  writing the HLSL in W1, is told.
- **`rhi.md` §3.5, one importer.** An importer per device shares its slots and its holds between readers of one source (a layer and
  a draft view's renderer), so each reader opens its own import (`OpenImport`), and a hold is taken at the import, not at the
  submit after the lease has gone.
- **`rhi.md` §3.4, the timers.** A fixed table of timestamp slots measures at most half its size in spans a frame, and the lease
  order is the same each frame, so the same spans go unmeasured every frame; the timers grow (`Reserve`) and say which frames they
  still hold (`Holds`). An ES context with `EXT_disjoint_timer_query` measures: by timestamps where its counter has bits, by one
  elapsed-time query per output where it has none.
- **`rhi.md` §2, the fence.** The platform factory (`Render/Platform/GpuPlatform.cs`) names exactly one GL type, the GL platforms'
  maker (`Gl.GlPlatforms`); `RhiFenceTests` allow that one name, in the source scan and the IL walk, and nothing else outside
  `Render/Gl`.
- **The warm-up's keys.** Warming every product program with the opaque state into an 8-bit target asks Direct3D 12 for pipelines
  no pass draws, some of them invalid; the warm-up makes the passes' lists only.

## 4. The hunks outside the Owns, in merge order

W1 merges **rhi → shaders → platform → desk-ux → installer** (`PLAN.md` §5.3), so these land on `main` first and the others rebase
on them.

| File | Hunk | Why the cut forces it |
|---|---|---|
| `src/PlayMap.Engine/Optimise/OptimiseWorker.cs` | the HAP encoder from `IGpuPlatform.CreateHapEncoder()`, the hello's words from `Device.Adapter`, `GpuException` in the catch and no GL type: `GlPlatforms.Create` rethrows a `GlfwException` as a `PlatformNotSupportedException`, which the catch takes | the engine names no GL type (`EngineFenceTests`), and `GlHapEncoder` lives in `Render/Gl` |
| `tests/PlayMap.Core.Tests/Threading/ComputeTests.cs` | one allow-list line for `Render/Effects/FxLane.cs` | FxLane's one `Compute` call: a program made after its lane went is let go when its make lands |
| `THIRD-PARTY-NOTICES.md` | the Direct3D 11, DXGI, user32 and display-configuration layouts' path, `src/PlayMap.Render/Gl/Windows` | the layouts live with the ANGLE platform in the GL backend's folder |
| `bench/r3-present/PlayMap.Bench.Present.csproj`, `PlayMapPath.cs`, `Program.cs` | a `wp3-rhi` using group for `PlayMap.Core.Gpu`, the GL types' namespaces (`PlayMap.Render.Gl`, `PlayMap.Render.Gl.Windows`), the presenter's record then present | the presenter seam is Core's and presents in two steps |
| `src/PlayMap.Core/PlayMap.Core.csproj` | a `wp3-rhi` item group embedding `programs.layout.json` | the program table is data in Core |
| `src/PlayMap.Engine/PlayMap.Engine.csproj` | a `wp3-rhi` using group for `PlayMap.Core.Gpu` | the engine's render files name the contracts |
| `src/PlayMap.Core/Led/LedFrames.cs`, `tests/PlayMap.Core.Tests/Led/LedReferenceTests.cs` | the LED frame pool sizes a frame to the plan's high water when it is rented or returned, off the warm path, and two tests hold it | Render's `LedSamplingTests.AWarmFrameAllocatesNothing` measures the pool as a show uses it, without sizing it first |
| `tests/Shared/GlFence.cs` (new), `tests/PlayMap.Engine.Tests/PlayMap.Engine.Tests.csproj` | the GL fence's source, project-file and IL scan, linked into the Render and Engine suites by a `wp3-rhi` item group | one line around the GL backend, held from both suites |
| `tests/PlayMap.Engine.Tests/Rendering/EngineFenceTests.cs`, `WarmUpHostTests.cs` (new) | the engine's side of the fence; the start failing on a pipeline the device refuses | the render host's checks, in its own area |
| `tests/PlayMap.Engine.Tests/Content/ReactiveRealBusTests.cs`, `Decode/DecodeGlApiTests.cs`, `Optimise/HapEncoderTests.cs`, `Optimise/OptimiseClips.cs`, `Presentation/SyncVerbTests.cs`, `Rendering/DeviceLockHostTests.cs` | the GL types' namespace `Render.Gl`, a renderer over the context's backend, a scripted presenter's `Record` | the GL types live in `Render.Gl`; these are the engine tests that name GL, listed in `EngineFenceTests`, and they leave with GL |
| `tests/PlayMap.Core.Tests/Gpu/**` (new) | the contracts' tests (`ProgramTableTests`, `DrawBuilderTests`, `ClipSpaceTests`) | the contracts live in Core |
| `docs/OUTPUT.md`, `docs/EFFECTS.md`, `docs/OPTIMISE.md` | the presenter's record and present, the backend fixture, GPU times per context, the effects' programs on the device, what a warm frame with a modulated slot allocates, the encoder's path | the papers of the areas the package changed |
| `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, `CLAUDE.md` (the agent brief), `docs/COMPETITIVE.md`, `docs/RESEARCH.md`, `docs/round3/README.md` | the RHI built and every frame drawn through it with GL behind it: the Render rows, the RHI paragraph of `ARCHITECTURE.md` §5, the GL line, the roadmap's "Built" section and its row (§1 says where) | the merge applies the package's papers text; `PLAN.md` §4 gives all seven to the integrator at the package's merge (§5.4 item 7), never to the package |
| `docs/round3/design/d3d12.md` §3 and §8 and the lines that follow §3 (the summary, the `Queues/` folder, the `rings` stage, the `b0` row, §6's constants and uploads), `docs/round3/design/shaders.md`'s conventions, `docs/round3/design/rhi.md`'s opening and its `DrawBuilder` summary | the corrections of §3 above, and a line pointing here | the code proves the designs wrong there |

## 5. What waits

- **The Windows job.** The Render suite on Mesa WGL (the GL backend behind the contracts on Windows); the ANGLE importer behind a
  reader's `ITextureImport`, built here and run only there; the elapsed-time GPU timers on a counter with no bits (ANGLE's
  Direct3D 11 back end), proved here only with the mode forced; `EngineFenceTests`' and `RhiFenceTests`' walk of the Windows
  build's types; `bench/r3-present`'s build.
- **The lab.** L20, the GL backend behind the interface on NVIDIA, AMD and Intel Windows drivers, is not run: the GL backend runs on
  llvmpipe here (core 3.30 and ES 3.00) and, on the Windows job, Mesa WGL only.
