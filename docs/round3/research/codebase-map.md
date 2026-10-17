# The tree against the Windows-native move: a map

*What in PlayMap's tree the move to an installed, Windows-only, Direct3D 12 node touches: each area's files, types and line
counts, what the move changes there, what stays, the risks, and what CI can prove. Read against `main` at `23f162f` (16 Oct
2026) in the round-3 planning worktree. Line counts are `wc -l` (blank and comment lines included). "Here" is the Linux build
machine (Mesa llvmpipe under Xvfb); "Windows CI" is the `windows` job of `.github/workflows/build.yml` (windows-latest, no GPU,
no audio endpoint). The direction it serves is `windows-gpu-direction.md` beside it; §11 lists where the tree corrects that
note.*

*Since `23f162f`, `main` has taken 36 commits (the desk's merge among them). They leave `src/PlayMap.Render`, the render host,
`Presentation/`, Core's media contracts, Media, Net and CI untouched; on the desk, `Controls/PreviewSource.cs` grows to 186 lines
(a ring replaced under the reader is reopened) with the same copy path as §2.15, and the desk embeds the STATE card descriptors from
`tests/fixtures/state/*.cards.json`; ten state fixtures change by a few lines and the folder gains a README.*

---

## 0. The tree in numbers

| Module | `.cs` files | Lines | GPU-relevant share |
|---|---|---|---|
| `src/PlayMap.Core` | 296 | 56,256 | contracts only: `Media/GpuSurfaces.cs`, `UploadArena.cs`, `GpuDeviceLock.cs`, `PreviewRing.cs`, `Sync/*`, `Effects/*` |
| `src/PlayMap.Render` | 89 | 21,160 | all of it (plus 10 ISF files, 227 lines, and two embedded fonts) |
| `src/PlayMap.Engine` | 240 | 59,799 | `Rendering/` 25 files 4,852 lines, `Presentation/` 4 files 769, `Sync/`, `Decode/`, `VideoIo/`, `Optimise/` |
| `src/PlayMap.Media` | 56 | 13,727 | `Hw/`, `Hap/`, `Video/Spout/`, `Audio/Windows/` |
| `src/PlayMap.Net` | 54 | 13,705 | `Sync/VblankReport.cs`, `SyncBoard.cs`, `ClockSync.cs` |
| `src/PlayMap.App` | 51 | 11,884 | `Controls/PreviewSource.cs`, `PreviewSurface.cs`, `Link/EngineSupervisor.cs` |
| `src/PlayMap.Devices` | 27 | 5,688 | `Midi/WinMmBackend.cs` |
| `src/PlayMap.Vision`, `src/PlayMap.Assistant` | 35 | 8,690 | none |
| `src/ThirdParty` (libomtnet, libvmx) | 44 | 10,905 | OMT's codec is native, loaded at run time |

| Test project | Files | Lines | Test methods | GPU use |
|---|---|---|---|---|
| `tests/PlayMap.Render.Tests` | 54 | 9,597 | 258 | 213 in the `gl` collection (a real GL context) |
| `tests/PlayMap.Engine.Tests` | 237 | 41,736 | 852 | about 190, in about 50 files, start a real render host |
| `tests/PlayMap.Media.Tests` | 34 | 5,946 | 178 | 3 Windows facts on WARP (D3D11VA, Spout) |
| `tests/PlayMap.Core.Tests` | 120 | 19,178 | 736 | none |
| `tests/PlayMap.App.Tests` | 14 | 1,556 | 43 | headless Avalonia, no GPU |
| Net, Devices, Vision, Assistant | 72 | 12,854 | — | none |

---

## 1. What the map finds

1. **GL is concentrated, its types are not.** Ten files (2,603 lines) call OpenGL directly; 25 more name `GlDevice` (7,163
   lines); 15 more hold GL-backed resources (`GpuTexture`, `RenderTarget`, `RenderContext`) without naming the device (4,281
   lines). 39 Render files (7,113 lines) touch no GPU type, but 11 of them are the GLSL library (2,179 lines) that must be ported.
   In the engine, 2 files name `GlDevice` and 10 import `PlayMap.Render.Gpu` or `.Platform`. Media and Net import nothing from
   Render (the module rule holds).
2. **There is no declarative render graph.** `RenderEngine.Render` is a fixed-order, immediate-mode frame with `IFramePass` hooks.
   No pass declares what it reads or writes; no lifetime or barrier is computed (GL needs none). "Compile the graph on show change"
   is new work, not a port.
3. **The device seam is GL-shaped.** Uniforms are set by name (166 distinct names in the library, no uniform blocks), programs are
   looked up by string, blend/depth/cull are loose state, fences are GL sync objects (`nint`), and there is no resource-state
   tracking. A Direct3D 12 backend needs pipeline state objects, root signatures, descriptor heaps, command lists and fence values
   under the same callers.
4. **The presenter seam fits; two of its types do not.** `IOutputPresenter` (present, result with a present count, observe after
   every output presented) already carries a DXGI flip presenter. But `IGpuPlatform.Context` is a GL `RenderContext`,
   `IOutputPresenter.Present` takes a GL `GpuTexture`, and Core's `GpuApi` enum has no Direct3D 12 word.
5. **The DXGI path is real but thin.** `Platform/Windows/` (8 files, 1,768 lines) presents through ANGLE on a Direct3D 11 device
   with a waitable flip-model swap chain and frame statistics, proven on WARP in CI. It is experimental, explicit, behind a fuse;
   `PresentMode.IndependentFlip` and `ModeEvidence.Proven` are words nothing sets; the NVAPI tier reads the board and refuses
   every join.
6. **Device loss stops the drawing.** The render loop journals a fault and, after 30 faulting frames in a row
   (`RenderHost.FaultLimit`), stops; the engine plays on with its outputs closed. There is no rebuild path.
7. **On the DXGI path effects compile on the render thread.** `FxCompileLane` needs a window to share a GL context from; ANGLE's
   surfaceless context has none, so the lane falls back to one compile a frame on the render thread.
8. **No Windows scheduling code exists.** No MMCSS, no power-throttling opt-out, no timer resolution, no CPU sets, no GPU
   priority, no GC latency mode (the banned-symbols list names "the show GC mode", which no code sets). The render, control, LED
   and audio threads are `ThreadPriority.AboveNormal`.
9. **Previews are four CPU copies.** GPU → pixel-pack buffer → shared-memory ring → `byte[]` → `WriteableBitmap`. The desk has
   no GPU interop at all.
10. **Packaging is a folder.** CI publishes a self-contained `win-x64` folder: no installer, no signing, no FFmpeg, no libvmx. The
    version is `0.1.0` in four places on `main`; the `versioning` branch replaces them, with a scheme an MSI cannot order build by
    build (§6.3).
11. **The tests are written against `GlDevice`.** 213 pixel tests drive a `GlFixture` that owns a GL context; they run on Linux
    (llvmpipe) and on Windows CI (Mesa through WGL). To run the same scenes on Direct3D 12 on WARP they need a backend fixture first.
12. **A new present path is a wire change.** PMVB vblank reports carry a path byte the reader refuses above 2 (fail closed), and
    five STATE fixtures plus the Companion module's fixtures pin GL words (`glx`, `core330`, `OpenGL 4.5 llvmpipe`).

---

## 2. The renderer (`src/PlayMap.Render`)

`PlayMap.Render.csproj` references Core, `Silk.NET.OpenGL` 2.23.0, `Silk.NET.Windowing` 2.23.0 (GLFW), `SkiaSharp` 3.119.4 and
`SkiaSharp.NativeAssets.Linux`; it embeds Inter and the ten built-in ISF files. Its target is `net10.0`; Windows code is marked
`[SupportedOSPlatform("windows")]`, so the project builds on Linux.

### 2.1 How coupled each file is

| Band | Files | Lines | Files |
|---|---|---|---|
| Calls GL (`using Silk.NET.OpenGL`) | 10 | 2,603 | `Gpu/GlDevice.cs` 667, `GlDevice.Resources.cs` 460, `GlDevice.Decode.cs` 215, `GlDevice.Content.cs` 120, `GlDevice.Fx.cs` 76, `GlDevice.VideoIo.cs` 19, `Gpu/GlDialect.cs` 233, `Platform/RenderContext.cs` 264, `Effects/FxCompileLane.cs` 358, `Encode/GlHapEncoder.cs` 191 |
| Names `GlDevice`, no GL calls | 25 | 7,163 | `Engine/RenderEngine.cs` 787, `FramePasses.cs` 116; `Gpu/GpuTypes.cs` 335, `ShaderProgram.cs` 122, `ResourcePool.cs` 133, `PixelBuffers.cs` 169, `AngleImporter.cs` 312, `DmaBufImporter.cs` 505; `Decode/GlUploadArena.cs` 179; `Hap/HapPass.cs` 154; `Video/VideoTextureSet.cs` 478, `PackedSend.cs` 429, `PackedUnpack.cs` 136; `Passes/SendStage.cs` 401, `OverlayPass.cs` 395, `MeasuredStage.cs` 111; `Led/LedPass.cs` 365; `Projection/GpuCaches.cs` 281; `Effects/FxChainRunner.cs` 300, `IsfRenderer.cs` 384, `FxTimer.cs` 80; `Generators/GeneratorRenderer.cs` 342, `FlowGenerator.cs` 308, `ParticleGenerator.cs` 145; `Timing/FramePacer.cs` 196 |
| Holds GL-backed types only | 15 | 4,281 | `Projection/StageRenderer.cs` 567, `Engine/SourceResolver.cs` 489, `Platform/Windows/DxgiOutputWindow.cs` 448, `AnglePlatform.cs` 425, `Generators/GeneratorHost.cs` 405, `ShaderGenerators.cs` 391, `Effects/FxPass.cs` 301, `Platform/GpuPlatform.cs` 260, `Generators/ContentFrame.cs` 240, `OutputWindow.cs` 157, `SkiaPatternRenderer.cs` 154, `RaymarchGenerator.cs` 134, `EffectGenerator.cs` 130, `Passes/OutputStage.cs` 107, `Platform/IOutputPresenter.cs` 73 |
| No GPU type | 39 | 7,113 | GLSL text: `Shaders/ShaderLibrary*.cs` (11 files, 2,179). Native Windows/EGL layer: `Platform/DxgiStart.cs`, `Egl.cs`, `Windows/AngleEgl.cs`, `ComSlots.cs`, `D3D11Adapter.cs`, `DxgiNative.cs`, `NvApi.cs`, `NvFrameLock.cs` (8 files, 1,283). Neutral logic: `Timing/VblankClock.cs`, `PresentClock.cs`, `PresentLedger.cs`, `LatencyPlan.cs` (1,020); `Sync/*` (3 files, 304); `Generators/Skia/*` (8 files, 1,715); `Engine/Contracts.cs`, `Effects/FxCatalog.cs`, `FxStats.cs`, `Video/YuvConversion.cs`, `Decode/DecodeContext.cs` (612) |

Outside Render: `src/PlayMap.Engine/Rendering/RenderHost.Align.cs` (75) and `RenderHost.Decode.cs` (107) name `GlDevice`; 36 engine
files (11,084 lines) import some `PlayMap.Render` namespace; 15 test files name `GlDevice`.

### 2.2 The device seam: `GlDevice` and its resource types

- **Exists.** `GlDevice` (sealed partial, 6 files, 1,557 lines) owns one GL context on the thread that made it. It caches bound
  state (program, vertex array, framebuffer, viewport, blend, depth, cull, wireframe, 16 texture units) and skips redundant calls;
  tracks every live object; reads `GlCaps` (`Gpu/GpuTypes.cs`: DXT1/5, RGTC, BPTC, norm16, float blend, float colour buffers) and
  the dialect from `GL_VERSION`; compiles programs by name on first use (`Program(name)` → `ShaderLibrary.Source(name)`, the
  dialect's header prepended by `CreateProgram`); draws immediately (`DrawFullscreen`, `DrawGeneratedQuads`, `Draw`, `DrawIndexed`,
  `DrawInstanced`, `DrawPresent`); exposes fences as GL sync objects (`InsertFence`/`IsSignaled`/`DeleteFence`, `nint`), timer
  queries, pixel-buffer map/unmap, persistent unpack buffers, compressed uploads and `AdoptExternal`/`BindImage` for EGLImages.
  Two files bypass it through `RenderContext.Gl`: the compile lane and the HAP encoder. Resource types (`Gpu/GpuTypes.cs`, 335): `GpuTexture` (internal GL id, a
  `Version` counter), `RenderTarget` (framebuffer, colour, optional depth), `GpuBuffer`, `VertexArray`, the format and state enums,
  `GlException`. `ShaderProgram` (122) sets uniforms by name (`Set(string, …)`, `SetInt`) with locations and last values cached.
  `ResourcePool` (133) reuses targets by size, format and depth, aged by frame. The class's own comment says "a Direct3D 11 device
  can stand in its place behind the same shapes".
- **The move.** The RHI will be cut here: resources, pipelines, binding sets, command lists, queues, fences, presenters and
  importers as backend-neutral shapes, GL 3.3 as the reference implementation, Direct3D 12 beside it.
- **Stays.** The format and state vocabulary, the `Version` change counter, the one-recording-thread rule, the pool's ageing.
- **Risks.** Every caller in bands 2–3 (40 files) sets loose state and named uniforms; Direct3D 12 bakes blend, depth, cull,
  topology and target formats into a PSO and binds constants through a root signature. Either the callers change, or the RHI
  emulates GL state with a PSO cache keyed at draw time, which is the first-use compile stutter the direction forbids. Nothing
  tracks resource states: every target sampled after it is drawn needs a barrier the frame code never names. The five fence users
  (`GlUploadArena`, `DmaBufImporter`, `PixelBuffers`, `LedPass`, `PackedSend`) map cleanly once the RHI returns a fence value.
- **CI.** `DeviceTests` (66), `Spine/GlCapsTests` (92), `Spine/TextureFormatTests` (46) pin the device on llvmpipe (Linux) and Mesa
  WGL (Windows CI). A Direct3D 12 device is testable on WARP on Windows CI only.

### 2.3 The frame: `RenderEngine`, the passes and "the render graph"

- **Exists.** `Engine/RenderEngine.cs` (787). `Render(in FrameInputs)` runs one order: caches, sources and stage begin the frame;
  per enabled output, blackout clears first, a raw scan frame skips everything, a held output keeps its picture, else `DrawOutput`
  (safe look, pattern, projector through `StageRenderer.RenderProjector`, canvas or source), every pass's `Picture`, `OutputStage.Draw`
  (warp, mask, blend, lens, colour, rotation, dither seeded by the show frame, the air's gain, the measured stage), the identify
  card, every pass's `Final`; then every pass's `FrameEnd` (LED, sends); then the delay rings; presents in pace order; `Observe` per
  presenter; offscreen clocks; the pool's trim. `WarmUp()` compiles every library program at start. `Preview` and `CollectPreviews`
  draw and read back previews after the frame. Per output it keeps `OutputState`: `FramePacer`, `PresentClock`,
  `DelayRing<RenderTarget>`, the final and projector targets, a `GpuTimer`. `Engine/FramePasses.cs` (116): `IFramePass` (`Item`,
  `Layer`, `Surface`, `Picture`, `Final`, `FrameEnd`, `Order`), `PassFrame`, `IAirLevels`, `IMeasuredStages`. `Engine/Contracts.cs`
  (203): `ISourceProvider`, `IRawOutputs`, `IStageResources`, `FrameInputs`. `Engine/SourceResolver.cs` (489),
  `Projection/StageRenderer.cs` (567), `Projection/GpuCaches.cs` (281: meshes, warps, coverage, blend and lens lookups uploaded once
  per instance).
- **What "render graph" means today.** `docs/ARCHITECTURE.md` §2 lists a render graph in Render; it is this ordered method and the
  pass hooks. The topology changes only with the project but is walked afresh every frame, and a hook may return a new texture.
- **The move.** A compiler on the lane will turn outputs × surfaces × passes into an immutable plan (passes with declared reads and
  writes, transient lifetimes, barriers, queue placement) that the render thread walks. Delay rings, ISF persistent buffers and the
  particle field's state targets are imported resources, never aliased.
- **Stays.** The frame's order and the doctrine in it (blackout before content code, raw frames skip passes, a warm frame allocates
  nothing; the render host counts `GC.GetAllocatedBytesForCurrentThread` around every frame).
- **Risks.** Five passes hook the frame through `IFramePass` — `Effects/FxPass.cs`, `Led/LedPass.cs`, `Passes/SendStage.cs`,
  `Passes/OverlayPass.cs` and the engine's `AirEnd` (`Rendering/RenderHost.VideoIo.cs`); each must declare its resources for a
  compiled plan, and align's measured stage rides `OutputStage`'s inputs. Transient aliasing is unsafe for anything that lives
  across frames.
- **CI.** Golden-pixel tests on GL: `EngineTests` (237), `CanvasAndStageTests` (254), `OutputStageTests` (168), `OverrideTests`
  (120), `Spine/AirPlumbingTests` (127), `Stacks/StackRenderTests` (179), `Align/AlignRenderTests` (208). The same scenes on
  Direct3D 12 on WARP are the parity evidence; a run with aliasing off and on is a cheap missing-barrier check.

### 2.4 Shaders and their GLSL sources

- **Exists.** `Shaders/ShaderLibrary.cs` (923) and ten package partials (1,256): 2,179 lines of GLSL 3.30 in C# raw strings. 40
  named programs: 25 in the library (`present`, `blit`, `solid`, `quad`, `mix`, `yuv`, `stage`, `output`, `sprite`, `lines`,
  `checker`, `motion`, `colorcycle`, `syncmarks`, five `fractal.*`, six `reactive.*`) and 15 from packages (`content.pstep`,
  `content.psprite`, `content.raymarch`, `hap`, `hapenc.bc1|bc3|bc4|ycocg`, `fxmix`, `led_sample`, `led_patch`, `video.pack`,
  `video.unpack`, `pixelsurface`, `overlay.shapes`; align, output and show add none). 46 stage bodies (6 vertex and 25 fragment in
  the library, 15 in the packages). No source carries `#version`: `GlDialects.Header` (`Gpu/GlDialect.cs`, 233, which also holds
  `GlFormatTable` and the extension-alias loader) writes `#version 330 core` or `#version 300 es` with precision lines. The
  conventions: targets top row first, row-vector matrices, UV v down, geometry from `gl_VertexID`, exact reads with `texelFetch`,
  and only the present into a window flips. The library's comment already promises "A Direct3D backend supplies the same names in
  HLSL with the same uniforms". `Spine/ShaderConventionTests` (65) compiles every program on core 3.3 and keeps names distinct.
- **The move.** Engine programs will be written once in HLSL, compiled by DXC at build time to DXIL with PDBs, and made into PSOs
  before the show; the GLSL stays for the GL backend until parity.
- **Stays.** Program names, the meaning of every uniform, the conventions; Direct3D's top-left texture origin matches the
  top-row-first rule, so only the GL present's flip goes.
- **Risks.** Two sources per program drift unless one pixel-parity test per program runs on both backends. `video.pack` and the
  `hapenc.*` programs depend on exact integer arithmetic (`Video/PackUnpackTests` asserts byte-exact packing, 800 lines). Every
  `sampler2D` becomes a texture and a sampler binding. Whether DXC on Linux emits signed DXIL is to be checked; if not, shader
  build steps run on Windows only.
- **CI.** DXC at build on the Windows job; every PSO created on WARP.

### 2.5 Generators

- **Exists.** `Generators/` 9 files 2,249 lines and `Generators/Skia/` 8 files 1,715 lines. `RenderServices` (internal, in
  `GeneratorRenderer.cs`) carries the device, the pool and black to every generator. The particle field (`FlowGenerator.cs`) steps
  in float state targets with two colour attachments (`StateTarget`, `GlDevice.Content.cs`); the raymarched fractals and the
  reactive scenes are fragment programs; Skia draws text-heavy patterns on the CPU and uploads them once.
- **The move.** Generators become RHI users; the particle step keeps its two-attachment targets.
- **Stays.** Skia on the CPU, the time rules (a generator's time from its origin on the show clock, the 1/120 s particle grid).
- **Risks.** `Content/ParticleDeterminismTests` (290) hold frame-exact determinism on one backend; floating-point results across
  backends and drivers can differ in the last bits, so "the same picture on every node" holds per backend and driver, which the node
  identity record has to carry.
- **CI.** `FractalTests`, `ReactiveTests`, `ParticleDeterminismTests`, `ContentTimeTests`, `StageEffect*Tests` (1,109 lines) on WARP.

### 2.6 The platform and the presenter seam

- **Exists.** `Platform/GpuPlatform.cs` (260): `IGpuPlatform` (`Name` gl | gl-surfaceless | dxgi, `Api`, `Words`, `Context`, `Path`,
  `Monitors`, `CreatePresenter`, `PumpEvents`, `Share`, `DeviceLock`, `Adapter`), `PlatformRequest` (`Path`, `Dialect`,
  `Surfaceless`, `Warp`, `GlApi`, `Words`), `GpuPlatforms.Create` (DXGI only when asked, GL with words otherwise; AUTO is GL on every
  platform), `GlPlatform`. `Platform/IOutputPresenter.cs` (73): `PresentPath {Offscreen, GlWindow, DxgiFlip}`, `PresentProblem
  {None, Held, Occluded, DeviceLost, Failed}`, `PresentResult` (with `PresentCount`), `IOutputPresenter` (`Present(GpuTexture)`,
  `PresentBlack`, `SwapInterval`, `LastSwapNs`, `Observe(PresentClock)`). `Platform/OutputWindow.cs` (157): the GLFW window.
  `Platform/RenderContext.cs` (264): a hidden GLFW context (GLX, EGL, WGL), a surfaceless EGL context (`Egl.cs`, 226), or an ANGLE
  context adopted (`Adopt`); `MonitorInfo`. `Platform/DxgiStart.cs` (162): `DxgiStartStage`, `GpuPlatformStartException`,
  `AngleRequirements`, `QpcTime`, `BackBufferCache<T>`. Engine: `Rendering/OutputWindows.cs` (228) opens a presenter per output by
  display key; `Presentation/PresentFuse.cs` (135) makes a DXGI start that never reached its first frames come back as GL.
- **The move.** A Direct3D 12 platform (a device on an adapter chosen by LUID, a direct queue and copy queues, a flip presenter per
  output) will stand behind `IGpuPlatform`; `PresentPath` gains its word; AUTO's meaning on Windows changes once the ledger proves
  parity; the ANGLE platform retires.
- **Stays.** One presenter per output, `Present` → `PresentResult`, `Observe` after every output presented, the fuse, words on every
  fallback, `machine.present` in STATE.
- **Risks.** GL types sit in the seam's signatures (`RenderContext Context`, `Present(GpuTexture)`). `IGpuShare`
  (`src/PlayMap.Core/Media/GpuSurfaces.cs`, 97) exposes `D3D11Device` as an `nint`, and `GpuApi` is `{None, OpenGl, D3D11, DmaBuf,
  IoSurface, Cuda}`. `RenderContext.Monitors` comes from GLFW; the DXGI side already enumerates displays its own way (`DisplayList`
  in `AnglePlatform.cs`: `EnumDisplayMonitors` and `QueryDisplayConfig`).
- **CI.** `Presentation/PresenterSeamTests` (240), `GpuPlatformFaultTests` (87), `GlApiTests` (105); engine `PresentPathTests`
  (146), `PresentFuseTests` (140), `PresentStateTests` (242).

### 2.7 The Direct3D 11 / ANGLE / DXGI path

- **Exists.** `Platform/Windows/` (8 files, 1,768 lines): `AnglePlatform.cs` (425: `AngleContext`, `AnglePlatform`, `DisplayList`,
  `DwmNudge`), `DxgiOutputWindow.cs` (448: a borderless top-most popup, a waitable `FLIP_DISCARD` swap chain of three B8G8R8A8
  buffers at frame latency 1, buffer 0 wrapped once as an ANGLE pbuffer, `Present1(1, DO_NOT_WAIT)`, the pacing output waiting on
  its waitable for at most three periods with the device lock let go, `GetFrameStatistics` fed to the output's clock, `D3D11Readback`
  for tests), `D3D11Adapter.cs` (166: the factory, the adapter of the primary display or WARP, a device with BGRA and video support
  and multithread protection, `RemovedReason()`), `DxgiNative.cs` (296: hand-declared structures and error codes), `ComSlots.cs`
  (91: COM called through vtable slots, no interop library), `AngleEgl.cs` (149: `av_libglesv2` loaded at run time), `NvApi.cs`
  (75), `NvFrameLock.cs` (118). With them: `Gpu/AngleImporter.cs` (312) and Core's `Media/GpuDeviceLock.cs` (103: one recursive lock
  around the device's immediate context, the frame waits at most 4 ms). The path is explicit (`PRESENT PATH DXGI`), experimental and
  fused; AUTO is GL.
- **The move.** Direct3D 12 presenters will inherit `DxgiOutputWindow`'s discipline without ANGLE; ANGLE (`AngleEgl`, `AngleContext`,
  `AngleImporter`, the pbuffer wrap) retires once Direct3D 12 presents; `GpuDeviceLock` loses its reason for Direct3D 12 (queues are
  free-threaded and decode hands frames over by fences) and stays only around a Direct3D 11 side device.
- **Stays.** The staged start with a word per stage, `QpcTime`, `DisplayList`, `DwmNudge` (`DwmFlush` on its own thread), the
  statistics-to-clock code, the layout goldens' method.
- **Risks.** Hand-written COM for Direct3D 12 (command lists, descriptor heaps, PSO descriptions, the debug and DRED interfaces) is
  a far larger surface than the 31 methods `ComSlots` declares (plus 4 in `AngleImporter` and 14 in Spout's own device); a maintained binding (Vortice.Windows, TerraFX) changes the
  house approach and adds a dependency. ANGLE does not leave the exe: the desk's Avalonia carries `Avalonia.Angle.Windows.Natives`.
- **CI.** `Presentation/DxgiWarpPresenterTests` (101; the Render suite's one Windows fact) brings the platform up on WARP,
  translates every program, presents 120 frames, reads statistics or says why not, and reads the frame back. `AngleAssetTests` (180)
  check ANGLE's file and its exports; `DxgiLayoutTests` (162) pin every structure and vtable slot against the mingw-w64 headers.

### 2.8 Timing: vblank clock, present clock and ledger, pacer, latency plan, delay ring

- **Exists.** `Timing/VblankClock.cs` (393: a least-squares fit of `t = a + b·k`, long and short windows, settled at 30 samples),
  `PresentClock.cs` (303: `PresentMode {Unknown, Gl, Composed, IndependentFlip, Offscreen}`, `ModeEvidence {None, Hint, Proven}`,
  `PresentSnapshot`, the output's light), `PresentLedger.cs` (175: the refresh each present was meant for against the one that
  showed it, wrap-safe), `FramePacer.cs` (196: period from swaps, the next vblank; `GpuTimer` on GL timer queries; `OutputStats`),
  `LatencyPlan.cs` (149: `OutputDelay`, `LatencyPlan`, `DelayRing<T>` of `d + 1` pictures, at most six frames). Only `GpuTimer`
  touches GL. Nothing sets `IndependentFlip` or `Proven`. The render host predicts each frame's vblank from the pacing output's pacer
  and draws for leader time at that vblank plus the air latency (`RenderHost.Frame`).
- **The move.** Direct3D 12 presenters will feed the same clock and ledger; Independent Flip evidence (statistics continuity inside,
  PresentMon outside) fills `ModeEvidence`; a per-frame block ring (show time, target vblank, stage stamps) and frames in flight per
  output are new.
- **Stays.** All of `Timing/` but `GpuTimer`, which moves to timestamp queries.
- **CI.** `Presentation/VblankClockTests` (374), `LatencyRingTests` (158), `TimingTests` (54): pure arithmetic, they run anywhere.

### 2.9 The frame-lock tiers (Render side)

- **Exists.** `Sync/FrameLockDrivers.cs` (209: `FrameLockBoard`, `IFrameLockDriver` {`Board`, `MaxSwapGroups`, `Join(outputId,
  presenter, group, barrier)`, `Leave`, `BarrierBound`, `TryFrameCount`, `WaitTimedOut`}, `NoFrameLock`, `FakeFrameLockDriver`),
  `FrameLockTiers.cs` (10), `NvApiLayout.cs` (85: GSync structures; the D3D1x swap-group function ids are declared and unused).
  `Platform/Windows/NvFrameLock.cs` reads the board's facts (id, firmware, synced, signal, house sync, a cable into a switch,
  configured); `Join` refuses ("this build joins no DXGI swap chain to a swap group yet"), `TryFrameCount` answers false,
  `MaxSwapGroups` is 0.
- **The move.** The NVIDIA tier will join outputs through the Direct3D 12 present barrier on RTX PRO with Sync and report its sync
  mode and in-sync counts as evidence; without it, outputs stay "free" with measured phase.
- **Risks.** `Join` is shaped for swap groups and barriers; the present barrier is a client per swap chain with its back buffers
  registered, so the driver interface changes. `LockCounter` (Core) makes content time from the board's 24-bit frame count; whether a
  present-barrier client gives every node the same count is a lab question.
- **CI.** `NvapiLayoutTests` (72) against NVIDIA's header; the fake driver drives every lock test on both jobs; the real barrier is
  lab only.

### 2.10 Decode on the GPU: upload arena, HAP, YUV, dmabuf/VAAPI and D3D11VA imports

- **Exists.** Core: `Media/GpuSurfaces.cs` (97: `GpuApi`, `GpuSurfaceFormat`, `DecodePath`, `GpuSurface` with `Retain`/`Release`,
  `IGpuSurfaceOwner`, `IGpuShare`, `BlockFormats`), `Media/UploadArena.cs` (191: `IUploadArena` and its allocator),
  `Media/Decode/DecodeLadder.cs` (207: policy and demotion triggers, `DeviceLost` among them), `Media/Hap/` (6 files, 1,027).
  Render: `Decode/GlUploadArena.cs` (179: a persistently mapped, coherent pixel-unpack buffer, 256 MB by default, fenced batches,
  epochs; only where the context offers buffer storage), `Decode/DecodeContext.cs` (32), `Hap/HapPass.cs` (154,
  `CompressedUploadRing`), `Gpu/GlDevice.Decode.cs` (BC textures, `RefuseBlockFormat`), `Video/VideoTextureSet.cs` (478: planes to
  textures, the `yuv` and `hap` programs into RGBA16F, D3D11 frames through `AngleImporter`, dmabufs through `DmaBufImporter`),
  `Video/YuvConversion.cs` (88), `Gpu/DmaBufImporter.cs` (505: `DmaBufShare`, an EGLImage per plane, two slots, fences),
  `Gpu/AngleImporter.cs` (312: the decoder's slice copied into one of three pooled NV12/P010 textures, or imported in place on the
  direct rung), `Gpu/PixelBuffers.cs` (169: `PixelUnpackRing`, `PixelPackRing`). Media: `Hw/HwDecode.cs` (174: VAAPI on the render
  node; `D3d11vaOnShare` makes FFmpeg's D3D11VA device on the share's `ID3D11Device` with lock callbacks), `Hw/SurfaceLadder.cs`
  (135), `Hw/DrmFrames.cs` (174), `Ffmpeg/MediaInput.cs` (591: D3D11VA first on Windows), `Sources/FfmpegVideoSource.cs` (814),
  `Hap/HapVideoSource.cs` (875), `Hap/HapWorkers.cs` (170), `Hap/SnappyBlock.cs` (150). Engine: `Rendering/RenderHost.Decode.cs`
  (107), `Decode/DecodeFacts.cs` (221), `DecodePathCheck.cs` (92), `HapLayersCheck.cs` (68), `EngineCore.Decode.cs` (462),
  `Host/MediaBackend.cs` (718).
- **Facts.** On Windows, zero-copy exists only on the experimental DXGI platform (a WGL context imports nothing). D3D11VA is
  compile-only plus a WARP open test (WARP has no video decoder); `IGpuShare.DirectProven` is false on D3D11. The FFmpeg binding in
  use (FFmpeg.AutoGen 9.0.1.1) declares `AV_HWDEVICE_TYPE_D3D12VA` (checked in the package here).
- **The move.** D3D12VA on the renderer's device, frames sampled in place or copied once on a copy queue; the arena becomes an
  upload heap (a GPU upload heap where ReBAR is on) behind the same `IUploadArena`; HAP blocks upload on the copy queue; surfaces are
  held to a fence value instead of a GL sync.
- **Stays.** The ladder and its words, the surface holds, the HAP parser and second stage on the CPU, the arena's allocator, the
  HAP HDR rules. BC formats are mandatory on Direct3D 12, so `RefuseBlockFormat` mostly answers yes.
- **Risks.** `DmaBufImporter` and VAAPI are Linux-only and live only while GL nodes on Linux live. FFmpeg's D3D12VA frame handoff
  (resource states, fences) is version-specific, tied to the pinned 9.0.2 build. CI has no hardware decoder.
- **CI.** `Hap/HapRenderTests` (376), `Decode/GlUploadArenaTests` (178), `PaddedSurfaceTests` (115), `VideoTests` (173) on WARP;
  Media's `Hw/D3d11vaWarpTests` (118) gets a D3D12VA twin that opens or says why; `SurfaceLadderTests` (171) and `GpuSlotTests`
  (237) are pure. Linux keeps `Decode/DmaBufImportTests` (341) only while GL stays.

### 2.11 Video I/O on the GPU: sends, unpack, Spout, NDI, OMT

- **Exists.** Render: `Passes/SendStage.cs` (401), `Video/PackedSend.cs` (429: `SendReadbackRing`, four pack buffers behind fences,
  frames lent to the sender without a copy), `Video/PackedUnpack.cs` (136, `video.unpack`), `Gpu/GlDevice.VideoIo.cs` (19). Media:
  `Video/SendSink.cs` (378), `LiveFramePool.cs` (307), `LiveSource.cs` (265), `Ndi/` (4 files, 1,055; the user's runtime loaded at
  run time), `Omt/` (2 files, 471; libvmx loaded at run time), `Spout/` (10 files, 1,932: `SpoutProvider` 452, `D3D11SpoutGpu` 356 —
  Spout's own Direct3D 11 device on the default adapter or WARP with legacy share handles — `SpoutSender` 288, `SpoutReceiver` 240,
  `SpoutLayout` 219, `Win32SpoutKernel` 128). Core: `Media/LiveVideo.cs` (343, `IGpuSendTarget`). Engine: `VideoIo/VideoIoHub.cs`
  (1,263), `EngineCore.VideoIo.cs` (1,205).
- **Facts.** NDI and OMT are CPU paths after the GPU pack. Spout receives by `CopyResource` into staging textures mapped into the
  input's frame pool (a CPU copy) and sends through the packed CPU path; the GPU send target exists but the send stage does not
  drive it.
- **The move.** Read-back rings become readback heaps on a copy queue with fence values; Spout reaches a Direct3D 12 renderer through
  a Direct3D 11 side device with a shared NT handle and a shared fence.
- **Stays.** Send semantics (leader-time grid, following the air, one black frame on stop), the NDI and OMT senders, Spout's protocol
  and kernel seam.
- **Risks.** Spout publishes legacy (non-NT) share handles, which a Direct3D 12 device does not open directly, so a Direct3D 11
  bridge stays (to be confirmed in the lab).
- **CI.** `Video/PackUnpackTests` (800, byte-exact) on WARP; Media's `Spout/SpoutWarpTests` (146, two Windows facts) round-trips a
  chart through two WARP devices; engine `SpoutProviderTests` (170), `OmtLoopbackTests` (196).

### 2.12 Effects: the ISF translator and the compile lane

- **Exists.** Core `Effects/` (6 files, 1,225: `IsfDocument` 360, `IsfExpression` 283, `IsfTranslator` 275 — GLSL 3.30 core and ES
  3.00, `#line 1 1` to map errors back, Mesa/NVIDIA/ANGLE logs read — `IsfEsFixups` 101, `FxKind` 178, `FxTime` 28). Render
  `Effects/` (7 files, 1,712: `FxCompileLane` 358 — a second GL context sharing the render context's objects, made through GLFW, on a
  thread of its own; one compile a frame on the render thread when the context has no window, which is the surfaceless EGL context
  and the ANGLE platform — `IsfRenderer` 384, `FxChainRunner` 300, `FxPass` 301, `FxCatalog` 199, `FxStats` 90, `FxTimer` 80) and
  `Effects/Library/*.fs` (10 files, 227). Engine: `EngineCore.Fx.cs` (609), `Fx/` (4 files, 469). `docs/EFFECTS.md` records 324 of
  327 public ISF files compiling on Mesa.
- **The move.** User ISF will go ISF → GLSL 4.50 → SPIR-V (glslang, relaxed) → DXIL (spirv_to_dxil) on the lane, with SPIR-V
  validation, robust buffer access and a per-shader GPU-time guard; on Direct3D 12 the lane needs no second context.
- **Stays.** The parser, the expression evaluator, the time rules, chain semantics, the FX verbs; STATE's `fx.compile.lane` word
  changes.
- **Risks.** Two more native compilers in the install; error lines mapped through two more stages; the 324/327 figure must be
  measured again; a user shader that hangs the GPU is a device removal, which has no rebuild path today (§3.1).
- **CI.** `Effects/IsfTranslatorTests` (149), `FxChainTests` (489), `FxAttachTests` (454), `FxLibraryTests` (150),
  `FxLaneContextTests` (49).

### 2.13 LED sampling, overlay, measured stage, output stage

- **Exists.** `Led/LedPass.cs` (365: samples in linear light, DMX slots made on the GPU, read back behind fences, never waited on),
  `Passes/OverlayPass.cs` (395), `Passes/MeasuredStage.cs` (111), `Passes/OutputStage.cs` (107); programs `led_sample`, `led_patch`,
  `overlay.shapes`, `pixelsurface`, `output`.
- **The move.** Ports to the RHI; the maths stays. **CI.** `Led/LedSamplingTests` (450), `Wall/OverlayTests` (180),
  `Wall/PixelSurfaceRenderTests` (161), `Align/AlignRenderTests` (208), `OutputStageTests` (168).

### 2.14 The GPU HAP encoder

- **Exists.** `Encode/GlHapEncoder.cs` (191) and `Shaders/ShaderLibrary.Optimise.cs` (250: `hapenc.*`, one texel per 4×4 block) run
  in the optimise worker (`PlayMap optimise-worker`, `src/PlayMap.Engine/Optimise/OptimiseWorker.cs` 245), which makes its own GL
  platform (surfaceless EGL on Linux with no display) and lowers itself to `BelowNormal`. Core's `Media/Optimise/Recipe.cs` names the
  encoder `hapenc/1`.
- **The move.** It may stay on GL (it is off the air, in its own process) or move to Direct3D 12 compute; a new encoder is a new
  `EncoderVersion` and new PSNR evidence.
- **Risks.** Keeping it on GL keeps a GL dependency on show machines after the renderer leaves GL.
- **CI.** Engine `Optimise/HapEncoderTests` (101), `OptimiseWorkerEndTests`, `OptimiseEngineTests`.

### 2.15 Previews: views, rings and the desk's reader

- **Exists.** Engine: `Rendering/PreviewViews.cs` (458: `VIEW … SHOW` parsed, pictures fitted inside 1920 × 1200),
  `RenderHost.Previews.cs` (233: an output view is a blit at most ten times a second, an editor or projector view a stage pass at most
  thirty), `RenderEngine.Preview`/`CollectPreviews` (a `PixelPackRing` read-back, never waited on). Core: `Media/PreviewRing.cs` (217:
  a file-backed memory map with three BGRA slots and a seqlock per slot, in `$XDG_RUNTIME_DIR/playmap` or
  `%LOCALAPPDATA%\PlayMap\run`). Desk: `src/PlayMap.App/Controls/PreviewSource.cs` (118: `IPreviewSource.Poll()` returns a
  `WriteableBitmap`; `PreviewFeeds.Resolve` lets an integrator add remote JPEG feeds; `RingPreviewSource` copies the newest slot into
  a `byte[]`, then row by row into the bitmap), `PreviewSurface.cs` (173: a 33 ms `DispatcherTimer` at render priority). The desk has
  no GPU interop: no `ICompositionGpuInterop`, no `OpenGlControlBase`, no custom draw operation.
- **Cost today.** Four copies per preview frame (pack buffer → ring → `byte[]` → bitmap → Avalonia's upload). At the caps, a
  1920 × 1200 stage view at 30 Hz moves about 9.2 MB a frame through each copy (about 1.1 GB/s); a 480 × 270 output view at 10 Hz,
  about 5 MB/s.
- **The move.** Previews will be drawn into two or three shared textures with a shared fence per view and imported once by the desk;
  the ring stays as the fallback (another adapter, a remote node). `IPreviewSource`'s return type changes from a bitmap to a
  composition surface.
- **Stays.** `VIEW` (the desk has no private API), the caps, the ring, the remote feeds.
- **Risks.** Avalonia on Windows renders through its own ANGLE/D3D11 by default (`UsePlatformDetect()`, no `Win32PlatformOptions`
  set); importing a Direct3D 12 texture there needs a D3D11-openable NT handle and a fence type the backend advertises (unverified).
- **CI.** Engine `Rendering/ViewVerbTests` (243); the desk's interop cannot be proven headless (`AvaloniaFact`); Windows CI with a
  real Avalonia window on WARP is untried.

---

## 3. The engine's render host and its facts

### 3.1 `RenderHost` and its partials (`src/PlayMap.Engine/Rendering/`)

- **Exists.** 25 files, 4,852 lines: `RenderHost.cs` (890) and twelve partials (`.Output` 222, `.Show` 235, `.Previews` 233,
  `.Wall` 165, `.Decode` 107, `.VideoIo` 82, `.Align` 75, `.Content` 73, `.Led` 63, `.Fx` 60, `.Audio` 33, `.Site` 23);
  `EngineSources.cs` (501), `StageResources.cs` (498), `PreviewViews.cs` (458), `ScanFrames.cs` (257), `OutputWindows.cs` (228),
  `TimerView.cs` (158), `StageMeshes.cs` (139), `SourceLeases.cs` (116), `BlendInputs.cs` (108), `HeadlessPacing.cs` (61),
  `FactsSignal.cs` (60: the render thread releases a semaphore so the engine republishes its facts without allocating there).
  `RenderHost.TryCreate` starts a thread named `render` at `ThreadPriority.AboveNormal` (line 209). `Main`: `GpuPlatforms.Create`,
  `RenderEngine`, `WarmUp`, the packages' `OnContext*` hooks, `OutputWindows`. `Loop`: take the platform's device lock (bounded),
  `Frame` (snapshot, project change, display poll, window pump, sources, scan, leader time from the lock anchor's count or the
  leader clock plus the time to the pacer's next vblank, the packages' `Compose*` hooks, render, previews), count the frame's
  allocations, journal the first fault, stop after `FaultLimit` = 30 faulting frames (line 128); then `Pace` (a window's vsync paces;
  headless, a deadline advanced by whole periods with `AutoResetEvent.WaitOne`, 2 ms or 0.5 ms margins). It implements the
  engine's seams `IRenderHost : IOutputBackend, IScanDisplay` (`src/PlayMap.Engine/Seams.cs`, 138), `IPresentBackend` and
  `IFrameLockBackend` (`Presentation/PresentSeams.cs`, 106), which name no GL type.
- **The move.** A device-loss rebuild that runs the start-up path again (device, presenters, PSOs, imports, arena), DRED
  breadcrumbs in the journal, and the time to the first good frame measured; MMCSS, QoS and timers on the render thread through a
  `PlayMap.Platform.Windows` seam; pre-roll before the node says it is ready; PSOs compiled at arm with "all compiled" in preflight;
  the watchdog heartbeat taken from the ledger's shown-frame counter.
- **Stays.** The engine-facing seams, the leader-time-at-vblank rule, the partial-hook layout, the allocation count.
- **Risks.** The renderer's name in STATE is composed here as "OpenGL x.y <renderer>"; headless pacing on Windows waits with
  millisecond granularity at the default timer resolution, which is a measured cause waiting to be measured.
- **CI.** About 50 engine test files (about 190 of 852 methods) start a real render host through `RenderHost.TryCreate`
  (`Rendering/HostRig.cs` 219, `Host/HostRig.cs`, `Presentation/PresentRig.cs` 205, `Optimise/OptimiseRig.cs` 145,
  `Sync/SyncRig.cs` 106, `Fleet/BackupRig.cs`): Mesa llvmpipe on both jobs. `Rendering/RenderHostTests` (405),
  `DeviceLockHostTests` (171), `FactsSignalTests` (134), `BlindRenderTests` (150), `PanicRenderTests` (62).

### 3.2 Presentation, sync and the board

- **Exists.** `Presentation/FrameLockHost.cs` (391: a `FrameLockMachine` per output stepped once a frame after the presents,
  re-anchoring every 60 s), `OutputChecks.cs` (137: preflight's present-path, present and lock rows; INDEX's `present` section),
  `PresentFuse.cs` (135), `PresentSeams.cs` (106). Core `Sync/FrameLockMachine.cs` (423: eight `LockState`s, each locked state needing
  its evidence) and `Sync/LockCounter.cs` (161). Engine `Sync/SyncNode.cs` (1,033), `VblankSource.cs` (57), `LockRecord.cs` (81),
  `EngineCore.Sync.cs` (622). Net `Sync/VblankReport.cs` (176: the 64-byte PMVB packet, path byte 0 offscreen, 1 gl, 2 dxgi-flip;
  `Path <= PathDxgiFlip` checked at line 94), `VblankReporter.cs` (59), `SyncBoard.cs` (352: phase, slip, ppm and a verdict of
  `same vblank`, `free-running`, `mixed rate` or `unknown`), `ClockSync.cs` (500).
- **The move.** The board's verdict already refuses to call free outputs locked; a present-barrier tier will feed it real evidence.
- **Risks.** A Direct3D 12 path word in PMVB is refused by today's readers: a group mixing builds needs the wire versioned
  (`docs/ROADMAP.md` R4.6) or the word mapped onto `dxgi-flip`.
- **CI.** `Sync/*Tests` (35 methods) and `Presentation/SyncVerbTests` (374) run on the fake driver on both jobs.

### 3.3 STATE, context, verbs and settings that carry GPU words

- **STATE.** `machine.present` (`EngineCore.Output.cs` lines 478–486: `api`, `adapter`, `path`, `dialect`, `context`, with `asked`,
  `kept`, `fuse`, `airLatencyMs`, `latencyWords`), `machine.renderer` (`StateWriter.cs` line 136), `machine.gl`, `outputs[].present`
  (`path` offscreen|gl|dxgi-flip, `mode`, `evidence`, counters), `outputs[].lock`, `sync.lock`, `sync.outputs[].path`,
  `decode.platform` (`EngineCore.Decode.cs` lines 280–283: `api`, `gl`, `blocks`, `staging`, arena), `decode.sources[].path`,
  `content.renderer`, `fx.compile.lane`, `media.optimise.worker.gl` (`EngineCore.Optimise.cs` line 318; `Optimise/WorkerProtocol.cs`
  line 64).
- **Fixtures.** `tests/fixtures/state/` holds 37 files; `main.json`, `output.json` (`api "gl"`, `dialect "core330"`, `context
  "glx"`, `renderer "OpenGL 4.5 llvmpipe"`), `decode.json`, `sync.json` and `optimise.json` carry GL words, and
  `Rendering/HostRig.DecodeBindingAsFixture` maps a Windows WGL run onto the fixtures' GLX words.
  `integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json` copy STATE too.
- **Verbs** (`src/PlayMap.Core/Wire/VerbCatalog.cs`, 894): `VIEW` (line 193), `OUTPUT STATS` (321), `PRESENT RESET` (322), `SYNC
  APPLY` (323), `SYNC REJOIN` (325), `PRESENT PATH AUTO|DXGI|GL` (328), `DECODE STATUS` (337), `DECODE POLICY` (338).
- **Settings.** `src/PlayMap.Core/Bus/NodeSettings.Output.cs` (`PresentSettings`: `Path` auto, `Dialect` auto, `FrameLock` auto,
  `SwapGroup` 1, `SwapBarrier` 1), `NodeSettings.Decode.cs` (`glApi`, `uploadArenaMb`, HAP read-ahead), `NodeSettings.Fx.cs`
  (`budgetMs`).
- **The move.** New words (a Direct3D 12 api and path, `proven` evidence), a node identity record (build, GPU driver, Agility SDK,
  FFmpeg build, sync firmware) and a capability snapshot by adapter LUID, each followed through STATE, the context, MCP, INDEX, the
  desk and the papers; every fixture above regenerated.

### 3.4 Process model, supervision, crash handling

- **Exists.** One exe: `src/PlayMap.App/Program.cs` (24) hands `engine`, `engine headless` and `optimise-worker` to
  `src/PlayMap.Engine/Host/EngineEntry.TryDispatch` (221) before Avalonia starts. The desk restarts a local engine that stops
  (`App/Link/EngineSupervisor.cs`, 170: 0.5 s doubling to 10 s); the engine restarts its optimise worker
  (`Optimise/OptimiseSupervisor.cs`, 330). An unhandled exception is journalled (`EngineEntry.cs` lines 117–151); no dump is
  configured. The node's home is `%LOCALAPPDATA%\PlayMap` (`NodeHome.cs`, 211), per user. A headless node has no supervisor in the
  tree.
- **The move.** A per-machine install; the engine in an auto-logged-on show user's session, supervised by a small service or a
  scheduled task (never session 0); WER LocalDumps in a PlayMap folder; privileges granted at install; a minimal monitor outside the
  engine that reads the shown-frame counter.
- **Risks.** Settings and dumps are per user today; a machine-level home or dump folder changes `NodeHome` and the preview ring's
  folder.

---

## 4. Windows-specific code already in the tree

| What | Where | Lines | State |
|---|---|---|---|
| DXGI flip model on Direct3D 11 through ANGLE | `src/PlayMap.Render/Platform/Windows/` (8 files) | 1,768 | experimental, explicit, fused; WARP in CI |
| D3D11 decoder frames into GL | `src/PlayMap.Render/Gpu/AngleImporter.cs` | 312 | compile-only (no hardware decoder in CI) |
| NVAPI frame-lock tier | `Platform/Windows/NvApi.cs`, `NvFrameLock.cs`, `Sync/NvApiLayout.cs` | 278 | board facts read; joins refused |
| D3D11VA decode on the renderer's device | `src/PlayMap.Media/Hw/HwDecode.cs` (`D3d11vaOnShare`), `Ffmpeg/MediaInput.cs` | 174, 591 | only on the DXGI platform; WARP open test |
| Spout 2 | `src/PlayMap.Media/Video/Spout/` (10 files) | 1,932 | own D3D11 device, legacy handles; WARP round trip in CI |
| WASAPI capture (devices, system loopback, one process) | `src/PlayMap.Media/Audio/Windows/WasapiAudioInputs.cs` (NAudio.Wasapi 3.1.0) | 215 | capture only; sound out is OpenAL Soft (`Audio/AudioOut.cs`, 547) |
| WinMM MIDI | `src/PlayMap.Devices/Midi/WinMmBackend.cs` | 193 | in use |
| Machine counters (`GetSystemTimes`, `GlobalMemoryStatusEx`, PDH 3D-engine utilisation) | `src/PlayMap.Engine/Fleet/MachineSampler.Windows.cs` | 220 | no GPU temperature |
| DPAPI for the TLS key and device credentials | `src/PlayMap.Net/Tls/TlsIdentity.cs`, `src/PlayMap.Engine/Devices/NodeCredentialStore.cs` | 269, 161 | in use |
| Desk manifest (Windows 10/11, per-monitor DPI v2) | `src/PlayMap.App/app.manifest` | 15 | `assemblyIdentity` 1.0.0.0 |

**Absent:** MMCSS, `SetProcessInformation` power throttling, timer resolution and high-resolution waitable timers, CPU sets, GPU
scheduling priority, `SetThreadExecutionState`, a GC latency mode, WER and dump settings, ETW events. The
`PlayMap.Platform.Windows` seam does not exist. Every Windows call sits in a `net10.0` project behind `[SupportedOSPlatform]` or
`OperatingSystem.IsWindows()`, so the solution builds and its pure tests run on Linux.

---

## 5. The desk (`src/PlayMap.App`)

- **Exists.** Avalonia 12.1.4 (`Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter`); `UsePlatformDetect()`; every view drawn by
  Avalonia (`Controls/MapViewport.cs`, `StageDrawing.cs`, `ProjectorCanvas.cs`, `PlanCanvas.cs`, `Scene/` 4 files 487 lines); previews
  as bitmaps (§2.15). The desk shows no present path today (only `Link/FakeEngineLink.State.cs` writes those words).
  `tests/PlayMap.App.Tests`: 43 methods, 27 of them headless Avalonia facts.
- **The move.** Shared-texture previews with an age shown and grey when stale; the consoles' safety patterns; the build's version
  (the `versioning` branch adds `ViewModels/DeskAbout.cs` and the help overlay's line).
- **Stays.** Avalonia, the verbs-only link to the engine, the ring fallback.

---

## 6. Build, CI, packaging and the version

### 6.1 CI today (`.github/workflows/build.yml`, 184 lines)

- **`test` (ubuntu-latest).** Xvfb `:99` with Mesa llvmpipe; FFmpeg n9.0.2 shared libraries pinned by hash; a check that the
  runner has no libva and no system OpenAL; `dotnet build -c Release` with the analyzers; every `tests/*/*.csproj` to its end with a
  5-minute hang blame; the Companion module (Node 22.20.0: `npm ci`, `npm test` with its e2e against `tools/PlayMap.Node`, `npm run
  check`).
- **`windows` (windows-latest).** FFmpeg n9.0.2 for win64 pinned by hash; Mesa 25.2.5's `opengl32.dll` and `libgallium_wgl.dll`
  (mesa-dist-win, pinned) copied beside every test host and `dotnet.exe` with `GALLIUM_DRIVER=llvmpipe`, so the GL suites run on
  llvmpipe through WGL; every test project, each exit code and results file checked; failed tests listed at the end;
  `dotnet publish src/PlayMap.App -c Release -r win-x64 --self-contained true -o publish/PlayMap`; artifact `PlayMap-win-x64`. The
  DXGI, D3D11VA and Spout tests run on WARP; ANGLE reaches the Render tests through their `Avalonia.Angle.Windows.Natives` reference.
- **Not a step.** The ES leg (`PLAYMAP_GL_DIALECT=es300`).

### 6.2 Packaging today

- A self-contained folder. FFmpeg is not in it (`src/PlayMap.Media/Ffmpeg/FfmpegLoader.cs`, 252: `PLAYMAP_FFMPEG_DIR`, then
  `ffmpeg/` beside the exe, then the exe's folder); libvmx is not in it (`Video/Omt/OmtSetup.cs`: `PLAYMAP_VMX_DIR`, beside the exe,
  `runtimes/<rid>/native`); NDI is the user's install; ANGLE (`av_libglesv2.dll`), OpenAL Soft and SkiaSharp's natives come with
  their packages.
- `Directory.Build.props` turns on `TieredPGO`; there is no ReadyToRun, NativeAOT, single-file, or GC setting in any project.
- No installer, no signing, no firewall rules, no WER settings, no Agility SDK, no shader compiler binaries.

### 6.3 The version

- **On `main`.** `<Version>0.1.0</Version>` in `Directory.Build.props`; `StateWriter.Version = "0.1.0"` (`src/PlayMap.Engine/
  StateWriter.cs` line 15, STATE's `version`); `HttpHost.Version = "0.1.0"` (`src/PlayMap.Net/Http/HttpHost.cs` line 59, the MCP
  server's info); `FakeEngineLink.State.cs` line 156; `app.manifest`'s `1.0.0.0`; `"version": "0.1.0"` in `main.json` and
  `output.json`.
- **On the `versioning` branch** (three commits past `main`, 35 files): `Version.props` (53) holds `PlayMapVersion` 0.2.0;
  `AssemblyVersion` is MAJOR.MINOR.0.0, `FileVersion` MAJOR.MINOR.PATCH.BUILD, the informational version MAJOR.MINOR.PATCH+BUILD.SHA
  from CI and MAJOR.MINOR.PATCH-dev+SHA locally; BUILD is `github.run_number`, refused past 65535; every part is checked ≤ 65535.
  `src/PlayMap.Core/BuildInfo.cs` (75) reads it back; CI passes `-p:PlayMapBuild` and `-p:PlayMapCommit`, checks the stamped exe,
  and names the artifact `PlayMap-<FileVersion>-win-x64`; the desk, STATE and the fleet show it.
- **What an installer adds.** An MSI's `ProductVersion` reads only the first three fields (major and minor ≤ 255, build ≤ 65535) and
  ignores the fourth. Under the branch's scheme every CI build of 0.2.0 reads as 0.2.0 to Windows Installer, so one build cannot
  upgrade another of the same patch; the MSI needs its own mapping (for example MAJOR.MINOR.BUILD), or every shipped build moves
  PATCH. `Version.props` allows major and minor up to 65535, past MSI's 255. A run number starts again at 1 under a new workflow
  file, which an installer reads as older (the branch's own error text says so). The direction note recommended git height; the branch
  uses the run number.

---

## 7. Tests that pin the GL path

| Project | Pins GL how | Linux job | Windows job | After the move |
|---|---|---|---|---|
| Render.Tests, 33 files / 213 methods in `[Collection("gl")]` | `GlFixture.cs` (116) owns one hidden GL context on a dedicated thread; `Pixels.Read(GlDevice, RenderTarget)`; `PLAYMAP_GL_DIALECT=es300` switches the whole suite to ES | llvmpipe | Mesa WGL | a backend fixture runs each scene on GL and on Direct3D 12 (WARP), same tolerances |
| Render.Tests, GL by name | `GlCapsTests`, `ShaderConventionTests`, `GlDialectTests` (272), `GlApiTests` | ✓ | ✓ | stay GL-only while GL stays |
| Render.Tests, Linux-only (8 `LinuxFact`, 1 `LinuxTheory`) | EGL, dmabuf, GLX (`Decode/DmaBufImportTests` 341, `EglFixture` 83, `Presentation/EsFixture` 83) | ✓ | skipped with words | Linux GL only |
| Render.Tests, pure (`VblankClockTests`, `TimingTests`, `LatencyRingTests`, `DxgiLayoutTests`, `NvapiLayoutTests`, `AngleAssetTests`, `GpuPlatformFaultTests`, `TextureFormatTests`) | none or layout goldens | ✓ | ✓ | stay; ANGLE's go with ANGLE |
| Render.Tests, the Windows fact | `DxgiWarpPresenterTests` | skipped | WARP | a Direct3D 12 twin |
| Engine.Tests, about 50 files | a real render host on GL (`RenderHost.TryCreate`) | ✓ | ✓ | run on the node's backend; STATE fixtures regenerated |
| Media.Tests, Windows facts | `Hw/D3d11vaWarpTests`, `Video/Spout/SpoutWarpTests` | skipped | WARP | D3D12VA and Spout-bridge twins |
| Core, Net, Devices, Vision, Assistant, App | no GPU | ✓ | ✓ | unchanged |

The agent brief forbids skipping, disabling or quarantining a test to get green: GL-only tests stay while GL stays, and leave with
the code they test when GL retires, recorded in that change.

---

## 8. The papers that describe the GPU path

| Paper | Lines | What it says that the move changes |
|---|---|---|
| `README.md` | 194 | "one portable exe"; decode paths per platform |
| the agent brief | 69 | "one portable .NET 10 exe", "OpenGL render", the loop's Xvfb/Mesa lines |
| `docs/ARCHITECTURE.md` | 320 | line 3 "one portable exe"; §2's Render row (OpenGL 3.3/ES, GLFW, ANGLE); §5 Rendering and its backends paragraph |
| `docs/OUTPUT.md` | 202 | the platform, the DXGI path, latency, vblank truth, the frame lock, "verified here, compile-only, next" |
| `docs/DECODE.md` | 187 | the ladder, hardware imports, the upload arena |
| `docs/VIDEO.md` | 189 | sends and Spout's own D3D11 device |
| `docs/EFFECTS.md` | 159 | ISF dialects, the compile lane |
| `docs/SYNC.md` | 163 | PMVB and the board |
| `docs/OPTIMISE.md`, `docs/CONTENT.md`, `docs/FLEET.md`, `docs/UX.md` | 129, 124, 211, 389 | the GL worker, generators, the machine sampler, latency budgets |
| `docs/ROADMAP.md` | 229 | the CI line; R4.2 (NVAPI swap groups), R4.6 (versioned wire, LTS). Its "Round 3: Plays with others" is the roadmap's own numbering, not this build round |
| `docs/COMPETITIVE.md`, `docs/RESEARCH.md`, `docs/WIRE.md` | 271, 110, 369 | portable exe, no signed installer; research behind GL; the wire |
| `docs/wire/d3d11.md`, `output.md`, `zerocopy.md`, `decode.md`, `sync.md`, `videoio.md`, `fx.md`, `optimise.md` | 66, 94, 66, 87, 123, 186, 102, 71 | the verbs and STATE keys of §3.3 |
| `THIRD-PARTY-NOTICES.md` | 159 | ANGLE under wp2-d3d11; the binding, DXC, glslang, spirv_to_dxil, the Agility SDK and WiX will each need an entry |

The round-2 designs (`docs/round2/design/present.md`, `framelock.md`, `texshare.md`, `zerocopy.md`, `hap.md`, `effects.md`) are
history and are not edited.

---

## 9. Shared files the move edits

Files more than one round-3 package will touch, so the plan gives each a hunk owner:

- **The RHI cut:** `src/PlayMap.Render/Gpu/GlDevice*.cs`, `GpuTypes.cs`, `ShaderProgram.cs`, `ResourcePool.cs`, `PixelBuffers.cs`.
- **The frame:** `src/PlayMap.Render/Engine/RenderEngine.cs`, `FramePasses.cs`, `Contracts.cs`.
- **The platform seam:** `src/PlayMap.Render/Platform/GpuPlatform.cs`, `IOutputPresenter.cs`, `RenderContext.cs`.
- **Shaders:** `src/PlayMap.Render/Shaders/ShaderLibrary*.cs`.
- **Core contracts:** `src/PlayMap.Core/Media/GpuSurfaces.cs` (`GpuApi`, `IGpuShare`), `UploadArena.cs`, `GpuDeviceLock.cs`,
  `PreviewRing.cs`.
- **The host:** `src/PlayMap.Engine/Rendering/RenderHost.cs` and its partials, `Presentation/*`, `EngineCore.Output.cs`,
  `EngineCore.Decode.cs`, `StateWriter.cs`.
- **The wire and settings:** `src/PlayMap.Core/Wire/VerbCatalog.cs`, `src/PlayMap.Core/Bus/NodeSettings.Output.cs`,
  `NodeSettings.Decode.cs`, `src/PlayMap.Net/Sync/VblankReport.cs`.
- **Build:** `src/PlayMap.Render/PlayMap.Render.csproj`, `Directory.Build.props` (and `Version.props` once the branch lands),
  `.github/workflows/build.yml`.
- **Tests and fixtures:** `tests/PlayMap.Render.Tests/GlFixture.cs`, `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs`,
  `tests/fixtures/state/*.json`, the Companion module's fixtures.
- **Papers:** `docs/ARCHITECTURE.md` §1, §2, §5; `docs/OUTPUT.md`; `docs/DECODE.md`; `README.md`; the agent brief.

---

## 10. What CI can prove after the move, and what stays manual

| Evidence | Linux job | Windows job (WARP) | Lab |
|---|---|---|---|
| Core, Net, Devices, Vision, Assistant, App suites | ✓ | ✓ | — |
| GL pixel tests (213) and the GL render host | ✓ llvmpipe | ✓ Mesa WGL | — |
| Direct3D 12 pixel parity on the same scenes | — | ✓ | real GPUs |
| Direct3D 12 flip presenter: device, PSOs, 120 presents, read-back | — | ✓ (statistics may be absent on WARP) | real vblank |
| Independent Flip proven (statistics continuity, PresentMon) | — | — | ✓ |
| Present barrier, RTX PRO Sync, house sync | fake driver | fake driver, layout goldens | ✓ |
| D3D12VA decode | — | opens or says why (WARP's video support to be checked) | NVIDIA, AMD, Intel |
| Spout through a D3D11 side device and a shared fence | — | ✓ two WARP devices | Resolume, TouchDesigner |
| Forced device removal and time to the first good frame | — | ✓ if WARP honours a forced removal (to be checked) | ✓ a real TDR |
| HLSL build, ISF → SPIR-V → DXIL | glslang ✓; DXC to be checked | ✓ | — |
| MSI build, install, upgrade, uninstall | — | ✓ on the runner | Windows 11 IoT Enterprise LTSC 2024 |
| `PlayMap.Platform.Windows` (QoS, MMCSS, timers, CPU sets) | pure logic | calls answer | measured p99.9 frame time |
| Allocation-free warm frames | ✓ GL | ✓ both backends | — |
| Desk shared-texture previews | — | untried | ✓ |

Linux keeps every suite that needs no Direct3D: Core, Net, Devices, Vision, Assistant, App, Media's CPU and fake-runtime tests, the
pure Render tests, the Companion module, and the GL render host and pixel tests for as long as GL is the reference backend. The
development loop runs on Linux, so every Direct3D 12 slice's evidence comes from the Windows job: logic that can be pure should be,
with the Direct3D calls thin.

---

## 11. Where the tree corrects the direction note

1. "Direct GL calls sit in about 10 of the 89 files": confirmed (10 files, 2,603 lines). "A `GlDevice` wrapper that about 40 files
   use": 36 source files name it (34 in Render, 2 in the engine), and 15 more Render files hold its resource types.
2. "A render graph … already exist[s]": what exists is an ordered immediate-mode frame with pass hooks, with no declared resources,
   lifetimes or barriers. The compiled graph is new work.
3. "About 20 GLSL `#version` sources" (`graphics_api.md`): no source carries `#version`; there are 40 programs and 46 stage bodies in
   11 library files, plus 10 ISF library files and the translator's vertex prelude.
4. "About 2 GB/s per 1080p60 view" for previews: the tree caps output views at 10 Hz and stage views at 30 Hz, at most 1920 × 1200;
   the four copies are real.
5. "The existing pixel tests running on WARP": the pixel tests are written against `GlDevice` and `RenderContext`; they need a
   backend fixture before they can run on Direct3D 12.
6. "A thread can tick while the picture is frozen": the engine has no heartbeat of its own; the desk's `EngineSupervisor` restarts a
   process that exits, and a headless node has no supervisor in the tree.
7. Versioning: the note recommends git height; the `versioning` branch uses the CI run number in the fourth part, which an MSI
   ignores (§6.3).

---

## 12. Questions only the lab answers

- The present ledger's latency on Direct3D 12 against GL on the same show hardware (the parity gate before GL retires).
- Whether Avalonia's Windows backend imports a texture and fence made on a Direct3D 12 device, at run time.
- The composition swap chain with Direct3D 12 buffers.
- The present barrier with several swap chains on a Sync wall, and whether it yields a frame count `LockCounter` can share.
- Spout's legacy share handles through a Direct3D 11 side device on a Direct3D 12 renderer.
- D3D12VA through the pinned FFmpeg 9.0.2 on NVIDIA, AMD and Intel.
- WARP: Direct3D 12 video decode and forced device removal.
