# Work package "effects": ISF and FFGL hosts, effect chains, a built-in library

Spec for round 2, written 13 October 2026.

- Research: `r2/research/effects.md`, cited below as **[R §n]**.
- Related research: `present.md` (D3D11/ANGLE) and `companion.md`.
- Code read: `main` at c12b186.

**Naming.** In this package an image effect is called **FX**: `FxSlot`, `FxChain`, the `FX …` verbs. The word "effect" is already taken in the tree three times:
- the surface-aware *generators* `effect.sweep|outline|relight` (`GeneratorCatalog`, `EffectGenerator`);
- the stings `EffectImpulse` / `EffectSurge`;
- the `effect:` field of a `CommandReply`.

Those keep their names.

---

## 1. Goal and the bar

PlayMap gains three things:
- **an ISF v2 host**, for generators, filters and transitions;
- **an FFGL 2.x host**, for native plugins;
- **effect chains** on playlist items, layers, canvases, surfaces and outputs.

A **built-in library written for PlayMap** comes with them. Every parameter can be set by one verb, bound to modulation, pressed on a Stream Deck and proposed by an AI.

| What the best do (research) | How PlayMap matches or beats it |
|---|---|
| **Resolume.** Effects on clip → layer/group → composition, in that order. An effect clip acts as an adjustment layer. Every effect has Opacity + Blend (dry/wet). B-bypass is animatable. Presets are applied once. Copy Effects. Quick search. Preview before apply. Transform is an effect [R §3] | **Match:** chains on **item → layer → canvas → output**, plus **surface**, which Resolume has no 3D equivalent for. Every slot has `mix` and `blend`. `FX BYPASS` is bindable to modulation like any parameter. One-shot presets. `FX PASTE … FROM`. An effect-only playlist item is the adjustment item. **Beat:** audition on the desk's preview without touching the air. Linked "looks" as well as one-shot presets. Per-slot GPU cost in STATE |
| **Resolume** scans and runs FFGL plugins in-process (V: an "Extra Effects" folder, loaded at startup) | **Beat:** each plugin is **probed in a child process** (a crashing DLL is quarantined with its reason; the node still starts). Untrusted plugins run **isolated in an `fxhost` process**: if it crashes, the slot passes its input through and the air survives. Trusted plugins may run in-process |
| **MadMapper 6** materials and surface FX are an ISF *dialect*: no multi-pass, their own entry function, `GENERATORS` (time_base, damper, adsr), audio FFT smoothing, 2D/3D/Line FX families [R §1.6] | **Match:** standard ISF *with* multi-pass, persistent, float and imported images (MadMapper refuses multi-pass). **Beat:** *geometry-aware ISF in standard files*. Reserved image inputs `PM_POSITION`, `PM_NORMAL` and `PM_EDGE` are bound to a surface's UV-space G-buffer. A world-space noise or sweep is therefore **continuous across several projectors and curved surfaces**, and the file still opens in VDMX or Resolume. MadMapper's `time_base` jump problem is solved by **anchored phases** that stay a pure function of show time (the doctrine) |
| **Millumin.** ISF effects and medias, a per-shader quality (render scale), an `enabled` property animatable per column. An ISF image input can take only image files and live inputs; "layers… not planned" (forum, April 2025) [R §1.6] | **Match:** a per-slot `scale` (quality) and `bypass`. **Beat:** **any ISF image input can take any PlayMap source**: another canvas, a playlist, a live input, media. That gives dynamic mattes and displacement maps, with recursion refused |
| **disguise.** Effect layers (Blur, Lut, OCIO, Kaleidoscope, PixelMap…), piped by layer arrows. Layer Stacks on live inputs. Keyframed parameters [R §3] | **Match:** the built-in library covers the same families (colour, LUT, blur, kaleidoscope, edge, pixelate, noise, transitions). Chains on live inputs come through the layer or the item. Parameters are timed through `AT` scheduling today and through modulation. A timeline is out of scope here |
| **Pixera.** GLSL and DFX effects stacked on timeline layers. MultiFX gives fixed DMX IDs (4/8/14 parameters, 16-bit). Its performance tips say: bake, use a reduced-resolution layer reference, watch GPU above 80 % [R §3] | **Match:** a stable `kind` id per effect, and (with the control package) a DMX personality per chain. **Beat:** each slot reports its *measured* GPU ms (timer queries) and its output's chain cost against a budget, in STATE, CONTEXT, the desk and Companion. Pixera tells you to guess from total GPU load |
| **VDMX / ISFMSLKit.** ">99 %" of ISF shaders work unmodified [R §1.3] | **Match on desktop GL, measured here:** a small managed translator compiled **324/327 (99.1 %)** of the public corpus as GLSL 330 core on Mesa [R §1.5]. **On ANGLE (ES 3.00)** the plain translator gets 91 % and the fix-ups 93 %. The fix-up tokenizer this round targets ≥ 97 % (§3.3) |
| Nobody documents deterministic feedback effects across machines, crash-safe native plugins, or effects reachable through the same verb grammar by a desk, Patterns, an AI and Companion | **Beat:** TIME, FRAMEINDEX and DATE come from leader show time with an origin stored in the model, so every node draws the same frame. History-dependent effects (persistent buffers, live audio) carry a badge. `FX CHECK` gives an AI a compile loop. The assistant's imports go through approval |

---

## 2. Scope this round

### Must

M1. **Model.**
- `FxSlot` / `FxChain` on `PlaylistItem`, `Layer`, `Canvas`, `Surface` and `Output`.
- `FxAsset` (ISF files carried with the project, content-addressed) and `FxPreset`.
- `ParamBinding`.
- All of it is backwards compatible: an old file reads with empty chains, and a file round-trips byte-identical when no FX is used.
- *Check:* `ModelTests` load main's sample project, unchanged, and a project with every attach point populated; both round-trip.

M2. **ISF v2 document parser and translator in Core.** It covers:
- every key in [R §1.1–1.4]: v1 `PERSISTENT_BUFFERS`, both `IMG_THIS_NORM_PIXEL` spellings, `IMG_*` with bias, WIDTH/HEIGHT expressions;
- the dialects **GLSL 330 core** and **GLSL ES 3.00**.

*Check:*
- unit tests on parsing and translation;
- **opt-in corpus test** (`PLAYMAP_ISF_CORPUS` → a checkout of Vidvox/ISF-Files): ≥ 324/327 compile and link at 330 core, and ≥ 305/327 at ES 3.00 on Mesa.

M3. **ISF renderer in Render.** It covers:
- passes, temporary and persistent targets, `FLOAT`, imported images, `audio`/`audioFFT` textures;
- orientation handling and the built-ins from show time (§3.4);
- a compile lane that never stalls a frame.

*Check:* GL tests on Xvfb/llvmpipe with pixel assertions (§7).

M4. **Chain runner** at the five attach points. Each slot has:
- `bypass`, `mix` (0–1) and `blend` (normal, add, multiply, screen, difference);
- `scale` (0.25–1) and per-slot GPU timing.

Test patterns, raw scan frames, blackout and the identify card **never** pass through chains.

*Check:*
- bypass and `mix = 0` are bit-exact pass-through;
- `PATTERN` over an output with an output chain shows exact pattern codes;
- the frame path stays allocation-free once warm (the existing `FramePathAllocatedBytes` assertion).

M5. **Built-in library v1.** 36 effects written for PlayMap as ISF files (§3.8), embedded, MIT. They include five geometry-aware effects (`PM_POSITION`/`PM_NORMAL`/`PM_EDGE`) and six transitions.
- *Check:* each has a pixel test at 64×36 or 128×72.

M6. **Verbs, STATE and CONTEXT** (§4).
- `FX KINDS / LIST / ADD / REMOVE / MOVE / SET [BY] / BYPASS / RESET / IMAGE / BIND / UNBIND / PASTE / SAVE / RECALL / PRESETS / IMPORT / CHECK / EDIT / RETIME / PLUGINS / TRUST / AUDITION`. Every verb is two words, because `VerbCatalog.Match` reads at most two.
- Every mutating verb refuses a no-op with the reason. Receipts say accepted → applied → observed ("compiling" → "ok" or "faulted").
- *Check:* `EngineTests` assert fields and published STATE, never words alone.

M7. **Modulation bindings v1.**
- `ParamBinding` evaluated per frame on the render thread through `IModulationBus`.
- Built-in deterministic sources: `lfo:*`, `ramp:*` and `sting`.
- The other packages register `audio:*`, `midi:*`, `osc:*`, `dmx:*` and `beat`.
- Modulated values never write the project. They show in `STATE.fx.slots[].live`.
- *Check:* an LFO-bound mix gives identical pixels on two engines at the same leader instants.

M8. **FFGL 2.x host, in-process, desktop GL.** It covers:
- the ABI (§3.6) and the default-state contract with a validator;
- parameter mapping, `FF_SET_TIME`, beat info, FFT buffers, thumbnails and plugin events, with observed values kept out of the project;
- a request for a **4.1 core** context when FFGL is enabled.

*Check (Linux):* the SDK's own **AddSubtract** (effect) and **Gradients** (source) samples are built in test setup with g++ against PlayMap's GLEW shim, loaded, run and pixel-checked [R §2.4].

M9. **FFGL probe in a child process** (`PlayMap fxprobe <path>`).
- It enumerates info, params and the thumbnail, and caches the result by file SHA-256.
- A plugin that crashes or hangs (5 s) is quarantined with words.
- *Check:* a PlayMap-written test plugin that aborts in `FF_GET_INFO` is quarantined, and the node starts.

M10. **Desk.**
- An Effects drawer (library with thumbnails, search, categories, cost, determinism badges).
- An "Effects" section in every target inspector (slots, bypass, mix, blend, scale, parameter editors shared with generators, bind popover, presets).
- Right-click menus on targets and slots.
- An ISF code editor with inline compile errors.
- **Audition** on a preview view.
- *Check:* App.Tests headless: each new `DeskAction` sends a catalogue verb, using the existing CI rule.

M11. **Papers.**
- A new `docs/EFFECTS.md`.
- `WIRE.md` (FX verbs and STATE `fx`), `ARCHITECTURE.md` (the FX pipeline), `ROADMAP.md` (M7 and M25 done, M8 in progress) and `THIRD-PARTY-NOTICES` (FFGL BSD-3).

### Should

S1. **FFGL isolated host** (`PlayMap fxhost`).
- One child process per node runs untrusted plugins on its own GL context.
- Transport:
  - **Linux:** a shared-memory frame ring, the same format as the VIEW preview rings.
  - **Windows:** D3D11 shared NT handles + `WGL_NV_DX_interop2`.
- One frame of latency. If the host dies, the slot passes through and restarts with backoff.
- `FX TRUST <plugin> IN-PROCESS|ISOLATED|OFF` (High, this machine only).
- *Check:* the PlayMap test plugin that segfaults on a parameter value takes down only `fxhost`. The output's frame counter keeps advancing, and the slot shows `faulted: plugin host restarted`.

S2. **Windows GL island for FFGL on the ANGLE/D3D11 present path**, from the present package. Compile-only here.

S3. **ES fix-up tokenizer.** It hoists non-constant global initialisers into an `isf_init()` called at the top of `main`, and makes implicit int→float conversions explicit in binary operators.
- *Check:* corpus ES ≥ 317/327 (97 %).

S4. **History pre-roll.** A slot with persistent buffers that is created while its target is on air renders K = 60 frames of past show time on the compile lane's shared context before it goes visible. Feedback trails then converge on a late node.
- *Check:* two engines started 3 s apart, with `fx.feedback` (decay 0.9), differ by < 1 code after the pre-roll.

S5. **ISF generators as generator kinds.** An ISF generator asset appears in `GENERATOR KINDS` as `isf:<asset>` and works in `GENERATOR NEW`.

S6. **ISF transitions for playlist transitions.** `PlaylistItem.Transition` takes `isf:<asset>` or a built-in `fx.t.*`, once the content package renders fades (a carryover gap).

S7. **Linked looks.** An `FxPreset` applied with `LINK` keeps following the preset. Editing the preset updates every linked slot in one undo step.

### Later

- MadMapper material import: wrap `materialColorForPixel` / `fxColorForPixel` and map `GENERATORS` onto anchored phases. Each file's licence is the user's to check.
- macOS FFGL bundles.
- Out-of-process ISF for untrusted shaders: GPU-hang containment.
- Baking a chain to a HAP file (with the codecs package).
- Automatic quality governor (it needs a measured cause and a deterministic test first).
- Timeline keyframes.
- A DMX personality per chain (with the control package).
- OCIO.

---

## 3. Architecture

### 3.1 Data flow (one frame, render thread)

```
FrameInputs (Project snapshot, LeaderNs, Modulation, Audio)
   │
   ├─ blackout? → black (no FX code runs)          ← unchanged order
   ├─ raw scan frame? → raw (no FX)
   ├─ pattern override? → pattern (no FX)
   └─ show:
        SourceResolver.Resolve(source)
           playlist item picture ──► item chain        (TIME origin = item start)
           layer source         ──► layer chain        (at layer rect size)
        Canvas compose ──────────► canvas chain        (at canvas size; cached per frame)
        Surface texture ─────────► surface chain       (once per frame for all projectors; G-buffer inputs)
        Output picture (pre-warp)► output chain        (at output size)
        OutputStage (warp, mask, blend, lens, trim, dither)   ← chains never run after the warp
```

**Why there is no post-warp chain (E).** An effect after warp and blend would break the blend weights and the black pedestal. A "post" effect is refused, with the reason.

### 3.2 Components

**Core** (pure; no GL):
- **`IsfDocument`**: the parsed header.
  - Records: `IsfInput(Name, Type, Label, Default, Min, Max, Identity, Values, Labels, MaxSamples)`, `IsfPass(Target, Persistent, Float, WidthExpr, HeightExpr)` and `IsfImported(Name, Path)`.
  - Also the categories, credit, description, `Vsn` and `IsfVsn`, the role (`Filter`, `Transition` or `Generator`), the PlayMap extensions (`PM_PHASES`, the reserved `PM_*` images) and `Issues`: warnings with line numbers.
- **`IsfExpression`**: a small evaluator for WIDTH/HEIGHT.
  - Grammar: numbers, `$WIDTH`, `$HEIGHT`, `$<input>`, `+ − * / ^ ( )` and `floor ceil round min max pow sqrt abs`.
  - Invariant culture; NaN, ∞ or ≤ 0 → 1 with an issue.
- **`IsfTranslator`**: `Translate(IsfDocument doc, string fs, string? vs, ShaderDialect dialect) → IsfProgramSource(Vertex, Fragment, Uniforms, LineMap)`. It is our own code; the reference VVISF was only read [R §1.3].
- **`IsfEsFixups`**: a tokenizer pass for ES: builtin-name renames (in M2), plus initialiser hoisting and conversions (S3).
- **`FxKind`**: what a slot can be.
  - `FxKind(string Id, FxFormat Format, string Name, string Family, IReadOnlyList<string> Categories, FxRole Role, IReadOnlyList<ParamSpec> Params, IReadOnlyList<string> ImageInputs, FxTraits Traits)`.
  - `FxTraits`: `HistoryDependent`, `ReadsAudio`, `GeometryAware`, `NativeCode`, `MayFlash`.
  - **The params are `ParamSpec`**, the same type generators use, so the inspector, `TryNormalise`, HELP and MCP schemas are shared.
- **`FxCatalog`**: the built-in kinds (static, from the embedded headers) merged with the project's ISF assets and this node's FFGL descriptors. `FxCatalog.Resolve(Project, FfglDescriptors) → FxCatalogSnapshot` is immutable and rebuilt on change.
- **`FxTime`**: TIME, TIMEDELTA, FRAMEINDEX and DATE from `(leaderNs, originNs, frameRate, wall)`, plus anchored phases (§3.4).
- **`Modulation`**: `ParamBinding` evaluation and the built-in sources (§3.7).
- **`FfglAbi`**: constants and blittable struct layouts transcribed from `FFGL.h` (BSD-3, credited) [R §2.2].

**Render:**
- **`FxHost`**: like `GeneratorHost`.
  - One renderer per (slot id, owner key, width, height), made on first use, reconfigured when the slot record changes (reference equality, then `SameParams`), and dropped after 120 unused frames.
  - A renderer that faults shows **pass-through** (not black) and stays faulted until its slot changes.
  - Nothing throws through a frame.
- **`FxChainRunner.Apply(FxChain chain, GpuTexture input, int w, int h, in FxFrame f, FxOwner owner) → GpuTexture`**.
  - It walks the slots, skipping bypassed, `mix = 0`, compiling and faulted ones.
  - Ping-pong targets come from `ResourcePool`.
  - It caches per (owner, frame), so a surface seen by three projectors runs its chain once.
- **`IsfRenderer : IFxRenderer`**: the program, pass targets, persistent targets (owned; resized and cleared on size change), imported textures (decoded with SkiaSharp, cached by hash) and uniforms through `ShaderProgram`'s value cache.
- **`FxMixPass`** (shader `fxmix`): `out = mix(input, blend(input, fx), mix)`.
  - It upsamples a scaled effect and normalises orientation to top-row-first.
  - It is one full-screen pass per active slot, skipped when the next consumer is an ISF slot and `mix = 1`, `blend = normal`, `scale = 1` (then the orientation flag travels instead).
- **`FxCompileLane`**: a second GL context **shared** with the render context, on its own thread.
  - It compiles and links with `KHR_parallel_shader_compile` where present, and caches program binaries (`ARB_get_program_binary`) keyed by (dialect, renderer string, SHA-256 of the source).
  - It hands a program over with a fence.
  - Pre-roll (S4) and `FX CHECK` probation renders run here.
- **`AudioTextures`**: one R32F texture per (samples, channels) per frame from `IAudioFrames`.
  - Waveform centred at 0.5, as ISF requires.
  - Silence when no audio package is present, with STATE `fx.audio: "none"`.
- **`SurfaceGBuffer`**: per surface and mesh revision, the mesh rasterised in UV space (`gl_Position = vec4(uv*2−1, 0, 1)`) into:
  - `PM_POSITION`: RGBA32F world xyz plus coverage;
  - `PM_NORMAL`: RGBA16F;
  - `PM_EDGE`: R16F, the distance to a fold edge (dihedral > 30°) or a boundary edge, in UV units.

  It is rebuilt only when the mesh, transform or faces change.
- **`Ffgl/`**:
  - `FfglLibrary`: load once per process; `plugMain` as a function pointer.
  - `FfglInstance`: the lifecycle.
  - `FfglStateGuard`: the default state before a call; validate and then `GlDevice.InvalidateState()` after.
  - `FfglRenderer : IFxRenderer` (in-process) and `FfglRemoteRenderer : IFxRenderer` (isolated, S1).

**Engine:**
- **`FxLibraryService`**:
  - scans folders (built-in, project `fx/`, `NodeSettings.FxFolders`, the standard ISF paths [R §1.5]);
  - imports and edits assets;
  - stores presets;
  - runs `FX CHECK` through the render side (`IFxBackend`);
  - supervises `fxprobe` and `fxhost`.
- **`EngineCore.Fx.cs`**: the verbs.
- **`StateWriter`**: the `fx` block.
- **`ContextBuilder`**: the FX item.

**Exe:** `PlayMap fxprobe <file>` (M9) and `PlayMap fxhost --ring <name>` (S1), both added in `EngineEntry`.

### 3.3 ISF translation (dialects)

The fragment output for **GLSL 330 core**:
```glsl
#version 330 core
out vec4 isf_FragColor;
#define gl_FragColor isf_FragColor
#define texture2D texture
#define texture2DRect texture
#define varying in
in vec2 isf_FragNormCoord;                 // bottom-left origin, as ISF requires
uniform int PASSINDEX; uniform vec2 RENDERSIZE; uniform float TIME; uniform float TIMEDELTA;
uniform vec4 DATE; uniform int FRAMEINDEX;
// per input: bool|int|float|vec2|vec4 <NAME>;  per image (input, pass target, imported, PM_*):
uniform sampler2D <img>; uniform vec2 _<img>_imgSize; uniform vec4 _<img>_imgRect; uniform bool _<img>_flip;
vec4 isf_sample(sampler2D s, bool flip, vec2 uv) { return texture(s, flip ? vec2(uv.x, 1.0 - uv.y) : uv); }
vec4 isf_sample(sampler2D s, bool flip, vec2 uv, float bias) { … }   // FRAGMENT STAGE ONLY [R §1.5]
#line 1
<body with IMG_* rewritten>
```

**GLSL ES 3.00** adds:
- `precision highp float; precision highp int; precision highp sampler2D;` — in the ES dialect header that
  `GlDevice.CreateProgram` prepends (SPINE.md R11), never in the translated source, which carries no `#version` and no
  precision line in either dialect;
- the fix-ups: the built-in renames of M2 (`IsfEsFixups`) this round; hoisting and int → float conversions (S3) next round.

**Rewrites:**
- `IMG_PIXEL(i, c[, b])` → `isf_sample(i, _i_flip, (c)/_i_imgSize[, b])`
- `IMG_NORM_PIXEL(i, c[, b])` → `isf_sample(i, _i_flip, (c)[, b])`
- `IMG_THIS_PIXEL(i)`, `IMG_THIS_NORM_PIXEL(i)` and `IMG_NORM_THIS_PIXEL(i)` → `isf_sample(i, _i_flip, isf_FragNormCoord)`
- `IMG_SIZE(i)` → `(_i_imgSize)`
- `vv_*` → `isf_*`
- `#version` lines are dropped.

Arguments are parsed with balanced parentheses. A wrong argument count is an issue at the source line.

**The vertex shader:**
- The default is a full-screen triangle from `gl_VertexID` that sets `isf_FragNormCoord`. It needs no VBO, like the library's `FullscreenVs`.
- A custom `.vs` (38 in the corpus) gets its own **vertex prelude**, not the fragment one: `#define varying out`,
  `#define attribute in`, `#define texture2D texture`, `out vec2 isf_FragNormCoord;`, and an `isf_vertShaderInit()` that
  writes `gl_Position` from `gl_VertexID` (the full-screen triangle) and sets `isf_FragNormCoord`; no fragment output. The
  same prelude serves both dialects (the research probe's, `ext/isfprobe/Program.cs` l.184–189). An `IsfTranslatorTests`
  case compiles a `.vs` that passes a varying to its `.fs`.

**Line map.** `#line 1` plus a per-dialect offset table means compile errors report **the user's file lines**. `FX CHECK` returns `{line, column, words}`.

**HLSL.** There is no HLSL path this round. On Windows the present package's ANGLE path runs the ES dialect. A native D3D11 backend, if it is ever gated in (present.md §10), would compile ISF through glslang → SPIR-V → SPIRV-Cross at import time (Apache-2.0 / BSD-3). It would reuse the `IsfProgramSource` uniform table as cbuffer reflection.

### 3.4 Time, determinism and anchored phases

Per slot and per frame:
- `TIME = (float)((leaderNs − originNs) / 1e9)`, where `originNs` is:
  - the item's start for an item chain;
  - otherwise `FxSlot.OriginNs`, which `FX ADD` stamps with `LeaderNowNs` and `FX RETIME` re-anchors.
- `TIMEDELTA = 1 / frameRate` (the show's rate, not a measured delta). It is 0 on FRAMEINDEX 0.
- `FRAMEINDEX = floor(TIME · frameRate + 1e-6)`.
- `DATE = (year, month, day, secondsOfDay)` of `ShowWallClock.UnixMsAt(leader→mono)`, in UTC unless `ProjectSettings.TimeZone` is set later.
- `PASSINDEX` and `RENDERSIZE` per pass.

**Precision rule.** float32 TIME has an 8 ms step at 65 536 s. When TIME > 65 536, or when TIME < 0 (the leader rebooted, so its monotonic clock restarted), STATE flags `timePrecision: "re-anchor"`. The leader re-anchors at the next moment the slot is not on air, or on `FX RETIME`.

**Fold-in fix.** The shader generators pass `(float)frame.Seconds` of the leader's monotonic clock (`ShaderGenerators.cs` `uTime`). After days of uptime that steps visibly. They move to the same origin rule: a `GeneratorFrame.ShaderSeconds` relative to a project-level `ShaderEpochNs`.

**Anchored phases (PlayMap extension, valid ISF).** A header may declare:
```json
"PM_PHASES": [ { "NAME": "pm_spin", "RATE": "speed", "WRAP": 6.283185 } ]
```
- The translator declares `uniform float pm_spin;`.
- The engine keeps `FxSlot.Phases[name] = (anchorNs, phase0)`.
- On every `FX SET` of `speed` it writes a new anchor in the same edit: `phase0 = phase(now)`, `anchorNs = now`.
- Then `phase(t) = phase0 + speed·(t − anchorNs)`, mod WRAP. That is a pure function of show time; MadMapper's `time_base` has to integrate. When `speed` is bound to a live source, the render thread integrates it per frame and the slot is badged live.

**Persistent buffers** make a slot history-dependent: `FxTraits.HistoryDependent` → a badge in STATE and on the desk ("breaks mirror backup", COMPETITIVE §5c). Pre-roll (S4) bounds the divergence for decaying feedback.

**Live inputs** also make a slot non-deterministic: `audio`/`audioFFT` inputs, bindings to `audio:`/`midi:`/`osc:`/`dmx:`, and FFGL plugins unless `FX TRUST … DETERMINISTIC`.

### 3.5 Orientation

- PlayMap's offscreen targets are **top row first** (`ShaderLibrary` conventions).
- ISF draws with `gl_FragCoord`/`isf_FragNormCoord` **bottom-left**. An ISF pass target is therefore stored bottom-up.

**Rule:** each texture handed between slots carries `bool BottomUp`.
- A PlayMap texture fed to ISF gets `_img_flip = true`.
- ISF → ISF passes `_img_flip = false`.
- The chain's last output is normalised by `fxmix`, which samples with the flag, so every consumer downstream sees top-row-first.

**FFGL:**
- `FF_ENABLE_PLUGIN_CAP(FF_CAP_TOP_LEFT_TEXTURE_ORIENTATION)` when `FF_GET_PLUGIN_CAPS` reports it [R §2.2]. No flips are needed then.
- Otherwise the input is flipped by one blit into a pooled texture, and the output is marked `BottomUp`.

*Test:* an ISF generator writing `isf_FragNormCoord.y` must read 1.0 at the **top** row of PlayMap's output picture.

### 3.6 FFGL host

**Context.** When `NodeSettings.FfglEnabled` is set:
- `RenderContext.CreateHidden` asks for **4.1 core** (forward-compatible), falling back to 3.3 with words: "FFGL needs OpenGL 4.1".
- macOS caps at 4.1 core, which is enough. Mesa gives 4.5.

**Loading.**
- Windows: `NativeLibrary.Load(path)` and `GetExport("plugMain")`, called as `delegate* unmanaged[Stdcall]<uint, nint, nint, nint>`.
- Linux:
  - `dlopen("libGL.so.1", RTLD_GLOBAL)`;
  - `dlopen("libplaymap_ffglshim.so", RTLD_GLOBAL)` and `pm_ffgl_shim_init(getProcAddress)`, with the context current;
  - then `dlopen(plugin, RTLD_NOW)`, as `[Cdecl]`.

  The shim fills the `__glew*` pointers [R §2.4].
- `SetLogCallback` is hooked into `NodeLog` with rate limiting.

**Lifecycle per DLL:** `FF_GET_INFO` → `FF_INITIALISE_V2` → (instances) → `FF_DEINITIALISE` at unload.

**Lifecycle per instance:**
- `FF_INSTANTIATE_GL(&{0,0,w,h})`
- `FF_SET_HOSTINFO(&{"PlayMap", version})`
- `FF_SET_SAMPLERATE(48000)`
- `FF_ENABLE_PLUGIN_CAP(TOP_LEFT)` when supported
- `FF_RESIZE` on a size change
- `FF_DEINSTANTIATE_GL`

**Per frame:**
1. `FF_SET_TIME(&seconds)` when `FF_CAP_SET_TIME` (TIME as in §3.4, as a double).
2. `FF_SET_BEATINFO(&{bpm, barPhase})` from `beat`, when a tempo source exists.
3. Changed parameters only:
   - `FF_SET_PARAMETER(&{index, bits})`; floats as their bits, text as a pointer to a pinned UTF-8 buffer kept alive until the next set;
   - for `FF_USAGE_FFT` buffers, `FF_SET_PARAMETER_ELEMENT_VALUE` per element (2048 by default) from `IAudioFrames.TrySpectrum`.
4. `FfglStateGuard.Enter(target)`:
   - bind the target FBO and set the viewport;
   - program 0; texture unit 0 active; every unit unbound for 2D, rect and cube; VAO, array, element and UBO 0;
   - blend off, ONE/ZERO, ADD; depth off, LESS, mask TRUE; cull off; CCW; fill; colour mask on.
5. `FF_PROCESS_OPENGL(&{n, inputs, HostFBO = target.Fbo})`, with inputs `{W, H, HardwareW = W, HardwareH = H, Handle}`.
6. `FfglStateGuard.Exit()`:
   - in debug builds, and in the probe's test run, validate the default state and record leaks as `fx.slots[].warnings`;
   - always `GlDevice.InvalidateState()` and the program value caches (`ShaderProgram.ForgetValues()`).
7. Every 6th frame, `FF_GET_PARAMETER_EVENTS`. Value, name and visibility changes become **observed** values in STATE (`live`, `display`, `hidden`). They are never project edits; there is one writer.

**Parameter mapping** (FFGL type → `ParamSpec`):
- STANDARD → Number, range from `FF_GET_RANGE`, else 0..1.
- INTEGER → Integer.
- BOOLEAN → Bool.
- EVENT → Bool with `momentary` (FX SET … ON pulses one frame).
- OPTION → Choice: element names, values from `FF_GET_PARAMETER_ELEMENT_VALUE`.
- RED/GREEN/BLUE/ALPHA and HUE/SAT/BRI (consecutive) → kept as separate Numbers, grouped by `FF_GET_PARAM_GROUP` on the desk.
- XPOS/YPOS → Number.
- TEXT/FILE → Text; a FILE value must resolve inside the project folder or an FX folder, or it is refused.
- BUFFER/FFT → not editable; it shows as "audio spectrum".

Parameter names come from `FF_GET_PARAMETER_NAME`, sanitised to `[a-z0-9_]`, with duplicates suffixed. Display names come from `FF_GET_PARAM_DISPLAY_NAME`.

**Kind id:** `ffgl:<PluginUniqueID>/<slug of PluginName>`, for example `ffgl:RM01/add-example`. Two DLLs that claim the same id are both quarantined with words.

**Threads.**
- In-process plugins run on the render thread only.
- One DLL is loaded once per process.
- A plugin that takes more than 20 ms in one `PROCESS_OPENGL` three frames in a row is reported. No governor acts, by doctrine.

**Isolated host (S1).**
- `fxhost` owns a GL context (4.1+), the plugin DLLs and a ring per slot.
  - Linux: the CPU frame ring.
  - Windows: a D3D11 texture pair per slot, shared by NT handle and opened in `fxhost` through `wglDXRegisterObjectNV`.
- The render thread posts frame N's input and takes frame N−1's output: **one frame of latency**, shown in STATE.
- A heartbeat runs every frame. If it is missing for more than 250 ms, the slot passes through. The supervisor restarts `fxhost` with backoff 1, 2, 4… up to 30 s.

**Probe (M9).**
- `fxprobe` runs `FF_GET_INFO`, `INITIALISE`, the parameters and their elements, `FF_GET_THUMBNAIL` (size, then pixels), and one instantiate-process-deinstantiate at 64×36 on a hidden context, with the state validator on.
- It writes JSON to stdout and exits.
- The supervisor kills it after 5 s.
- The cache key is `(path, size, mtime, SHA-256)`.

**Folders.**
- `NodeSettings.FfglFolders`. The default is `%ProgramData%\PlayMap\FFGL`, or `~/.local/share/PlayMap/FFGL`.
- **Resolume's "Extra Effects" is never scanned automatically**: plugins bought for Resolume may be licensed to that host [R §4.13].

### 3.7 Modulation bindings

```csharp
public sealed record ParamBinding(string Param, string Source, double Amount, double Offset, BindingCurve Curve, double SmoothSeconds);
```

**Evaluation** on the render thread, per frame, for each bound parameter of an active slot:
1. `s = bus.TryRead(Source, leaderNs, out v) ? v : 0.5` (v is in 0..1).
2. `u = curve(s − Offset)`.
3. Then by parameter type:
   - Number/Integer: `value = clamp(base + Amount · u · (Max − Min), Min, Max)`.
   - Bool: `u ≥ 0.5`.
   - Choice: `index = floor(u · n)`.
   - Colour: hue rotation by `Amount · u` turns.

`bypass` and `mix` are bindable too: a beat-synced bypass is Resolume's "animate bypass".

**Built-in sources** (deterministic; Core):
- `lfo:<sine|triangle|saw|square|random>/<period seconds>[/<phase 0–1>]`, on show time. `random` is `Pcg32` seeded by the slot id and the period index.
- `ramp:<seconds>`: 0→1 from the origin, then held.
- `sting`: the `EffectImpulseChannel` surge peak, already on `FrameInputs.Impulse`.

**Sources from other packages** (registered at run time; the names are reserved here):
- `audio:level|bass|mid|high|onset|fft/<bin>`;
- `beat` (bar phase) and `bpm`;
- `midi:<port>/<ch>/cc/<n>|note/<n>`;
- `osc:<address>`;
- `dmx:<universe>/<channel>[/16]`;
- `var:<name>`, a node variable set by `VAR SET` from Companion or Patterns.

A source this node does not have reads 0.5. STATE says `"unbound source audio:bass (no audio analysis on this node)"`.

**Smoothing.** `SmoothSeconds` is a one-pole filter applied **only to live sources**. Deterministic sources are never smoothed, which keeps them pure functions of time.

### 3.8 The built-in library (written for PlayMap, embedded ISF, MIT)

All of these are `fx.*` kinds. Each is an ISF v2 file under `src/PlayMap.Render/Fx/Library/` and embedded as a resource. Users can **duplicate and edit** one (it becomes a project asset). Files may use the `PM_*` extensions, and stay valid ISF.

| Family | Effects (must, 36) |
|---|---|
| Colour (7) | `fx.levels` (in/out black, white, gamma per channel) · `fx.hsl` · `fx.balance` (lift, gamma, gain) · `fx.lut` (`.cube` 17–65³ imported as a 2D-packed LUT, trilinear) · `fx.invert` · `fx.threshold` · `fx.duotone` |
| Blur and light (6) | `fx.blur` (separable Gaussian, PASSES at `$WIDTH/2` and `/4` for radius > 8) · `fx.directional` · `fx.zoomblur` · `fx.sharpen` · `fx.bloom` (threshold, 3-level down/up chain) · `fx.edges` (Sobel, optional glow) |
| Geometry (8) | `fx.mirror` · `fx.kaleido` (n-fold, rotation) · `fx.tile` · `fx.polar` · `fx.twirl` · `fx.ripple` · `fx.displace` (displacement map: **any source** through image input `map`) · `fx.pixelate` |
| Look (6) | `fx.rgbsplit` · `fx.halftone` · `fx.scanlines` · `fx.grain` (Pcg-hash on FRAMEINDEX, deterministic) · `fx.vignette` · `fx.posterize` |
| Time (3) | `fx.feedback` (PERSISTENT, FLOAT; decay, zoom, rotate, hue drift) · `fx.echo` (4-tap frame echo, PERSISTENT ring) · `fx.strobe` (**obeys `ProjectSettings.FlashGuard`**, ≤ 3 flashes a second, through `FlashGuard`) |
| Key and matte (3) | `fx.lumakey` · `fx.chromakey` · `fx.matte` (alpha or luma of **any source** through image input `matte`) |
| **Geometry-aware (5)** | `fx.worldnoise` (3D simplex in world metres: continuous across projectors) · `fx.sweep3d` (a band through world space over video) · `fx.relight` (normal shading of the content by a moving light) · `fx.outline3d` (fold and boundary edges glow over the content) · `fx.depthfade` (fade by distance from a point or the audience viewpoint) |

**Transitions** (S6) use the ISF `startImage`/`endImage`/`progress` convention: `fx.t.crossfade`, `fx.t.dip`, `fx.t.wipe`, `fx.t.noise`, `fx.t.zoom` and `fx.t.slide`.

**Geometry-aware effects off a surface** (on a layer or an output) fall back to a flat plane: position = (uv.x·aspect, 1−uv.y, 0), normal = +z. This is the rule `EffectGenerator` already uses, so they always draw something.

**Shared include.** `#include "pm.glsl"` is resolved by the translator from an embedded file with `pm_hash`, `pm_simplex3`, `pm_hsv2rgb`, `pm_rgb2hsv` and `pm_luma`. Our own include syntax keeps the files portable once inlined: `FX EXPORT` writes them with includes expanded (later).

### 3.9 Performance budgets

| Item | Budget | How it is held |
|---|---|---|
| CPU per active slot (uniforms, binds, mix) | ≤ 20 µs; **0 bytes allocated** once warm | `ShaderProgram` value cache; pooled targets; precomputed uniform locations; no LINQ in the frame path |
| GPU: built-in single-pass effect at 1920×1080 on a mid-range discrete GPU | ≤ 0.25 ms (**U**: cannot be measured here) | full-screen triangle; RGBA8 unless FLOAT; blur at reduced size |
| Mix pass | 1 texture read ×2 + 1 write per pixel | skipped when ISF→ISF at mix 1 / normal / scale 1 |
| Per-output chain | `NodeSettings.FxBudgetMs` = 4.0 at 60 Hz | GPU timer ring per slot (3 queries deep, never waits). STATE amber when p95 > budget for 2 s; red when > 2 × budget. **No automatic action** (measured, then governed) |
| Compile | never on the render thread | compile lane + program binary cache. Measured 0.8–19 ms on llvmpipe [R §1.5] |
| Memory | `FxMemoryCapMb` = 1024 per node | persistent and FLOAT targets counted. `FX ADD` beyond the cap is refused with the number |
| FFGL FFT upload | 2048 `plugMain` calls per slot per frame | about 20 µs (E); only for plugins with an FFT buffer |

---

## 4. Spine needs

### 4.1 Model (Core/Model, new `FxModel.cs`; all JSON camel-case)

```csharp
public enum FxBlend { Normal, Add, Multiply, Screen, Difference }
public enum BindingCurve { Linear, Ease, Step }
public enum FxFormat { Builtin, Isf, Ffgl }

/// <summary>One effect in a chain: a kind from the FX catalogue with its parameters, how its result mixes over
/// its input, at what render scale, and the show-time origin its TIME counts from.</summary>
public sealed record FxSlot(
    string Id,              // "fx" + 8 hex (Ids.Fx)
    string Kind,            // "fx.blur" | "isf:fa1b2c3d4e" | "ffgl:RM01/add-example"
    string Name,            // "" = the kind's name
    ParamSet Params,        // as generators; normalised by the kind's ParamSpec
    bool Bypass,            // default false
    double Mix,             // 0..1, default 1
    FxBlend Blend,          // default Normal
    double Scale,           // 0.25..1, default 1
    long OriginNs) : IEntity        // 0 = the owner's origin (item start), else leader ns
{
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];
    /// <summary>Extra ISF image inputs (not inputImage/startImage/endImage) → any source.</summary>
    public ImmutableSortedDictionary<string, SourceRef> Images { get; init; } = ImmutableSortedDictionary<string, SourceRef>.Empty;
    /// <summary>PM_PHASES anchors: name → (anchor leader ns, phase at the anchor).</summary>
    public ImmutableSortedDictionary<string, PhaseAnchor> Phases { get; init; } = ImmutableSortedDictionary<string, PhaseAnchor>.Empty;
    /// <summary>A linked look (S7): the preset this slot follows, or null.</summary>
    public string? PresetId { get; init; }
}
public readonly record struct PhaseAnchor(long AnchorNs, double Phase);
public sealed record ParamBinding(string Param, string Source, double Amount, double Offset, BindingCurve Curve, double SmoothSeconds);
public sealed record FxChain(ImmutableArray<FxSlot> Slots) { public static readonly FxChain Empty = new([]); public bool IsEmpty => Slots.IsDefaultOrEmpty; }

/// <summary>An ISF file carried with the project: content-addressed, distributed by hash like media.</summary>
public sealed record FxAsset(string Id, string Name, FxFormat Format, string Path, string ContentHash,
    string VertexPath, string VertexHash, ImmutableSortedDictionary<string, string> Imported /* name → sha256 */) : IEntity;

public sealed record FxPreset(string Id, string Name, string Kind, ParamSet Params, double Mix, FxBlend Blend, double Scale) : IEntity;
```

**Attach points.** These are init properties, so the positional constructors and every existing call site stay unchanged, and an older file reads them as empty, like `Stage.Devices`:

| Record | Property | JSON |
|---|---|---|
| `PlaylistItem` | `public FxChain Fx { get; init; } = FxChain.Empty;` | `"fx": {"slots": []}` |
| `Layer` | same | same |
| `Canvas` | same | same |
| `Surface` | same | same |
| `Output` | same (pre-warp) | same |
| `Project` | `public ImmutableArray<FxAsset> FxAssets { get; init; } = [];` and `public ImmutableArray<FxPreset> FxPresets { get; init; } = [];` | `"fxAssets"`, `"fxPresets"` |

**Ids:** `Ids.Fx = "fx"`, `Ids.FxAsset = "fa"`, `Ids.FxPreset = "fp"`. `Project.AllIds()` yields slot, asset and preset ids.

**JSON.**
- `ProjectJson.Sanitize` maps a null `FxChain` to `FxChain.Empty`, and a null dictionary to Empty. This is a small hunk with a test.
- A file with no FX writes `"fx": {"slots": []}` per entity. Byte-identical round-trip of old files holds for *reading* only; files grow by those keys on save (agreed rule: "every list written").

**Generic edits work at once:**
- `SET ly_base fx.slots.0.mix 0.5`
- `SET op_1 fx.slots.1.bypass true`
- `GET fx1a2b3c4d`, which needs `Project.Find` over slots (a small helper `Project.FindFx(id) → (FxSlot, ownerId, index)`).

**Ownership.** A slot id is unique project-wide. `REMOVE fx…` removes it from its owner.

### 4.2 Verbs (`VerbCatalog`, new category `VerbCategory.Effects`)

Targets:
- `<target>` is any id or name of a layer, canvas, surface, output or playlist item (`ly…`, `cv…`, `sf…`, `op…`, `it…`). Names resolve as `ResolveOutput` does.
- `<slot>` is an `fx…` id, or `<target>#<n>` (1-based).

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `FX KINDS` | `FX KINDS [<family>|<search>]` | Low | no | JSON: `[{id, format, name, family, categories, role, params:[ParamSpec json as GENERATOR KINDS], images, traits:{history, audio, geometry, native, mayFlash}, costMs256}]` |
| `FX LIST` | `FX LIST <target>|ALL` | Low | no | JSON chain(s) with each slot's live state (as STATE) |
| `FX ADD` | `FX ADD <target> <kind> [AT <index>] [<name>]` | Review | yes | `OK <slotId>`, effect `"added Blur to layer Test card — compiling"` |
| `FX REMOVE` | `FX REMOVE <slot>` | Review | yes | effect words |
| `FX MOVE` | `FX MOVE <slot> <index>` | Review | yes | refuses the same index |
| `FX SET` | `FX SET <slot> <param>|mix|blend|scale|name <value> \| FX SET <slot> <param> BY <delta>` | Review | yes | coalesced per slot and parameter (as `GENERATOR SET`). `BY` adds `delta × (max − min)` for numbers and steps choices. Refuses an unchanged value |
| `FX BYPASS` | `FX BYPASS <slot>|<target>|ALL ON|OFF|TOGGLE` | Review (`ALL ON` is a defined **panic outcome**: "effects off") | yes | the number of slots changed |
| `FX RESET` | `FX RESET <slot>` | Review | yes | parameters to defaults; refuses when already at defaults |
| `FX IMAGE` | `FX IMAGE <slot> <input> <source>|NONE` | Review | yes | refuses an unknown input, a recursion (a canvas into its own chain) or an unknown source |
| `FX BIND` | `FX BIND <slot> <param> <source> [AMOUNT <a>] [OFFSET <o>] [CURVE LINEAR|EASE|STEP] [SMOOTH <s>]` | Review | yes | refuses an unknown source family; answers words about whether the source exists on this node |
| `FX UNBIND` | `FX UNBIND <slot> <param>|ALL` | Review | yes | |
| `FX PASTE` | `FX PASTE <target> FROM <target>|<json> [REPLACE]` | Review | yes | the new slot ids (copies get new ids and origins) |
| `FX SAVE` | `FX SAVE <slot> <preset name>` | Low | yes | `OK <presetId>` (a preset in `Project.FxPresets`) |
| `FX RECALL` | `FX RECALL <slot> <preset> [LINK]` | Review | yes | refuses when the slot already has those values; `LINK` makes a linked look (S7) |
| `FX PRESETS` | `FX PRESETS [<kind>]` | Low | no | JSON `[{id, name, kind, linkedSlots}]` |
| `FX IMPORT` | `FX IMPORT <path|url|json>` | Review | yes | `OK <assetId>` + check report. JSON form: `{"name","fs","vs"?,"images"?:{name:base64png}}` |
| `FX CHECK` | `FX CHECK <asset>|<json>` | Low | no | JSON `{ok, role, inputs, passes, persistent, issues:[{line, col, words}], dialects:{glsl330:{ok, errors[]}, es300:{ok, errors[]}}, costMs256}` |
| `FX EDIT` | `FX EDIT <asset> <json>` | Review | yes | a new hash; dependent slots recompile; UNDO restores the old source |
| `FX RETIME` | `FX RETIME <slot>|<target>|ALL [AT +<ms>|<unix ms>]` | Review | yes | the new origin |
| `FX PLUGINS` | `FX PLUGINS [RESCAN]` | Low | no (RESCAN does probe work, but is no project edit) | JSON `[{kind, path, name, type, params, trust, state:"ok|quarantined|missing", words}]` |
| `FX TRUST` | `FX TRUST <plugin> IN-PROCESS|ISOLATED|OFF [DETERMINISTIC]` | **High**, this machine only, never from an assistant (the `CREDENTIAL` rule) | no (node setting) | the new trust |
| `FX AUDITION` | `FX AUDITION <view> <target> <kind>|OFF` | Low | no | an ephemeral slot on one preview VIEW only; the air is untouched |

**Per-argument risk.** When the spine adds `RiskOf(line)` (a carryover), `FX SET`/`FX BYPASS` on a target **not on air** drops to Low.

**Approval display.** The approval display for `FX IMPORT <json>` shows a summary ("ISF 'Glow', 2.1 KB, 3 inputs, 1 persistent pass"), not the raw line (the MCP carryover).

**Receipts and doctrine.**
- `FX ADD` / `FX IMPORT` / `FX EDIT` answer when the edit is **applied**.
- STATE then moves `state: compiling → ok|faulted`, which is **observed**.
- `FX CHECK` waits for the compile lane, with a 10 s timeout, and runs off the control lane (the lesson of the `BLEND SOLVE` carryover). It answers `accepted` at once and pushes the report.

### 4.3 STATE and CONTEXT

`STATE.fx` is written by a new `StateWriter` hunk that calls `FxStateWriter.Write(w, …)` in its own file:

```json
"fx": {
  "budgetMs": 4.0,
  "audio": "none|analysis",
  "library": { "builtin": 36, "isf": 4, "ffgl": 2, "quarantined": 1 },
  "outputs": { "op_1": { "costMs": 1.4, "costMsP95": 1.9, "light": "green" } },
  "slots": [ {
      "id": "fx1a2b3c4d", "owner": "ly_base", "ownerKind": "layer", "index": 0, "kind": "fx.blur", "name": "Blur",
      "state": "ok|compiling|warming|faulted|missing|bypassed|isolated-restarting",
      "gpuMs": 0.31, "scale": 1.0, "deterministic": true, "history": false, "latencyFrames": 0,
      "live": { "radius": 12.5, "mix": 0.8 },          // only bound or plugin-moved params
      "timePrecision": "ok|re-anchor",
      "words": "", "warnings": []
  } ],
  "plugins": { "host": "in-process|isolated|off", "isolatedAlive": true, "restarts": 0 }
}
```

**`CONTEXT`.**
- Item `fx`: light amber when a slot is faulted or an output is over budget, red when an output's chain is at > 2 × budget. Headline example: "12 effects · Glow faulted: line 14 `pm_blur` undeclared".
- Commands: the FX verbs with their risks.
- **Determinism:** the context lists the non-deterministic slots under "breaks mirror backup".

### 4.4 Seams

```csharp
// Core — read on the render thread; must not block or allocate.
public interface IModulationBus
{
    bool TryRead(string source, long leaderNs, out double value01);
    bool IsLive(string source);              // live = non-deterministic (audio, midi, osc, dmx, var)
}
public interface IAudioFrames
{
    int Channels { get; }
    bool TryWave(long leaderNs, int samples, Span<float> interleaved);     // −1..1 (the renderer maps to 0.5-centred)
    bool TrySpectrum(long leaderNs, int bins, Span<float> magnitudes01);
}

// Render
public readonly record struct FxFrame(long LeaderNs, double FrameRate, long UnixMs, IModulationBus Modulation, IAudioFrames? Audio, bool FlashGuard);
internal interface IFxRenderer : IDisposable
{
    FxRenderState State { get; }             // Compiling, Warming, Ok, Faulted(words)
    void Configure(FxSlot slot, FxKind kind);
    /// <summary>The slot's result for this frame (owned by the renderer or the pool until the frame ends).</summary>
    (GpuTexture Texture, bool BottomUp) Apply(in FxFrame frame, GpuTexture input, bool inputBottomUp, int width, int height, FxOwnerContext owner);
}
public sealed record FxOwnerContext(string OwnerId, long OriginNs, SurfaceGBufferTextures? GBuffer, Func<SourceRef, GpuTexture> ResolveImage);
public sealed record FxSlotStats(string SlotId, string State, double GpuMs, string Words, IReadOnlyDictionary<string, double> Live);

// FrameInputs gains (init, defaults keep every existing caller):
public IModulationBus Modulation { get; init; }   // default: NoModulation (built-ins only)
public IAudioFrames? Audio { get; init; }
// RenderEngine gains: IReadOnlyList<FxSlotStats> FxStats(); FxCheckResult CheckIsf(IsfSourceBundle) via the compile lane.

// Engine (Seams.cs)
public interface IFxBackend
{
    FxCatalogSnapshot Catalog { get; }
    Task<FxCheckReport> CheckAsync(IsfSourceBundle source, CancellationToken ct);
    Task<FfglScanReport> ScanPluginsAsync(bool rescan, CancellationToken ct);
    IReadOnlyList<FxSlotStats> Stats();
}
// IRenderHost : … , IFxBackend   (the render host implements it; a headless node answers with a backend
//                                   that checks on a pbuffer/surfaceless context or refuses with words)
```

**`ISourceProvider` gains** `PlaylistItemAt(SourceRef playlist, long leaderNs) → (string ItemId, long StartLeaderNs)?`, with a default of `null`. The resolver then applies the current item's chain with the item-relative origin. During a transition (content package) each side's chain applies to its own picture.

### 4.5 NodeSettings additions

These are init properties with defaults, so existing files read unchanged:

```csharp
public ImmutableArray<string> FxFolders { get; init; } = [];
public ImmutableArray<string> FfglFolders { get; init; } = [];
public bool FfglEnabled { get; init; } = false;
public ImmutableSortedDictionary<string, FfglTrust> FfglTrust { get; init; } = …Empty;   // kind → InProcess|Isolated|Off (+Deterministic flag)
public double FxBudgetMs { get; init; } = 4.0;
public int FxMemoryCapMb { get; init; } = 1024;
```

`FfglTrust` is a record `(FfglTrustMode Mode, bool Deterministic)`. Plugins default to `Isolated` once S1 lands; until then the default is `Off` (listed, not runnable) and `FX TRUST … IN-PROCESS` enables one.

---

## 5. Files

**New. Core:**
- `src/PlayMap.Core/Model/FxModel.cs`
- `src/PlayMap.Core/Content/Fx/FxCatalog.cs`, `FxKind.cs`, `FxTime.cs`, `Modulation.cs` (`ParamBinding` evaluation, `IModulationBus`, `NoModulation`, `LfoSources`)
- `src/PlayMap.Core/Content/Fx/IsfDocument.cs`, `IsfExpression.cs`, `IsfTranslator.cs`, `IsfEsFixups.cs`
- `src/PlayMap.Core/Content/Fx/FfglAbi.cs` (constants and structs; BSD-3 credit)

**New. Render:**
- `src/PlayMap.Render/Fx/FxHost.cs`, `FxChainRunner.cs`, `FxMixPass.cs`, `IsfRenderer.cs`, `FxCompileLane.cs`, `AudioTextures.cs`, `SurfaceGBuffer.cs`, `FxTimers.cs`
- `src/PlayMap.Render/Fx/Ffgl/FfglLibrary.cs`, `FfglInstance.cs`, `FfglStateGuard.cs`, `FfglRenderer.cs`, `FfglRemoteRenderer.cs` (S1), `FfglInterop.Windows.cs` (S2, compile-only)
- `src/PlayMap.Render/Fx/Ffgl/shim/playmap_ffglshim.c` (MIT; built by CI for Linux packages and by the tests)
- `src/PlayMap.Render/Fx/Library/*.fs` (36 + 6 transitions + `pm.glsl`), embedded via `<EmbeddedResource>`

**New. Engine:**
- `src/PlayMap.Engine/EngineCore.Fx.cs`
- `src/PlayMap.Engine/Fx/FxLibraryService.cs`, `FfglProbeRunner.cs`, `FxHostSupervisor.cs`, `FxStateWriter.cs`
- `src/PlayMap.Engine/Host/FxEntry.cs` (the `fxprobe` and `fxhost` subcommands)

**New. App:**
- `src/PlayMap.App/ViewModels/FxDrawer.cs`, `FxInspector.cs`, `FxBindPopover.cs`, `IsfEditorViewModel.cs`
- `src/PlayMap.App/Views/FxDrawerView.axaml`, `FxSectionView.axaml`, `IsfEditorView.axaml`

**New. Tests:**
- `tests/PlayMap.Core.Tests/Content/IsfDocumentTests.cs`, `IsfTranslatorTests.cs`, `IsfExpressionTests.cs`, `FxTimeTests.cs`, `ModulationTests.cs`, `FxModelJsonTests.cs`
- `tests/PlayMap.Render.Tests/FxChainTests.cs`, `IsfRenderTests.cs`, `FxLibraryTests.cs`, `IsfCorpusTests.cs` (opt-in), `FfglHostTests.cs` (opt-in on `PLAYMAP_FFGL_SDK` + g++), `SurfaceGBufferTests.cs`
- `tests/PlayMap.Engine.Tests/FxVerbTests.cs`, `FfglProbeTests.cs`
- `tests/PlayMap.App.Tests/FxDeskTests.cs`
- `tests/fixtures/ffgl/pm_crash_plugin.cpp` (our test plugin, MIT)

**Touched (small hunks):**
- `Core/Model/Content.cs` (Layer, Canvas `Fx`), `StageModel.cs` (Surface `Fx`), `OutputModel.cs` (Output `Fx`), `PlaylistModel.cs` (PlaylistItem `Fx`), `Project.cs` (assets, presets, `AllIds`, `FindFx`), `Ids.cs`, `ProjectJson.cs` (null chain → Empty)
- `Core/Wire/VerbCatalog.cs` (category and 22 verbs)
- `Core/Bus/Pairing.cs` (`NodeSettings` init properties)
- `Render/Engine/Contracts.cs` (`FrameInputs.Modulation/Audio`, `ISourceProvider.PlaylistItemAt` default)
- `Render/Engine/SourceResolver.cs` (item, layer and canvas chain calls)
- `Render/Projection/StageRenderer.cs` (surface chain, cached per frame)
- `Render/Engine/RenderEngine.cs` (output chain before `OutputStage`, `FxStats`, `CheckIsf`)
- `Render/Gpu/GlDevice.cs` (`ShaderDialect`; `InvalidateState` already exists; also drop the cached depth mask)
- `Render/Gpu/GpuTypes.cs`: no change. `RenderTarget.Framebuffer` and the texture handle are `internal`, and the FFGL host lives in the same assembly; they become `HostFBO` and `FFGLTextureStruct.Handle`
- `Render/Platform/RenderContext.cs` (4.1 core when FFGL is on; shared context for the compile lane)
- `Render/Shaders/ShaderLibrary.cs` (`fxmix`, `gbuffer`)
- `Render/Generators/ShaderGenerators.cs` (the `ShaderSeconds` fold-in)
- `Engine/EngineCore.Router.cs` (dispatch), `StateWriter.cs` (one call), `ContextBuilder.cs` (the fx item), `Seams.cs` (`IFxBackend`), `Rendering/RenderHost.cs` (pass modulation, audio and stats; implement `IFxBackend`), `Rendering/EngineSources.cs` (`PlaylistItemAt`), `Host/EngineEntry.cs` (subcommands)
- `App/ViewModels/GeneratorInspector.cs` (`ParamEditor` owner generalised to `IParamOwner`), `Inspectors.cs`, `MapInspectors.cs`, `DrawerViewModels.cs`, `Controls/DeskMenu.cs` (actions)
- Docs.

---

## 6. Dependencies

**Packages:**
- **present**: `IGpuDevice` extraction, the ES dialect switch, the ANGLE device and the interop helpers for S2. The effects package runs on today's `GlDevice` and needs only `ShaderDialect` from it.
- **audio/modulation** (audio analysis, tempo, Link): registers `audio:*`, `beat` and `bpm` in `IModulationBus` and implements `IAudioFrames`. Until it lands, the inputs are silence and the built-in sources remain.
- **control** (OSC, MIDI, Art-Net, timecode): registers `midi:*`, `osc:*` and `dmx:*` sources, and later the chain DMX personality.
- **companion**: consumes `STATE.fx`, the `VERBS` schemas and the `BY` deltas (§8).
- **content** (stings, transitions): the `STING` verb feeds `EffectImpulseChannel` (the `sting` source). The fade/dip carryover is the host for ISF transitions (S6).
- **showsafety**: Show Mode, blind editing and panic. `FX BYPASS ALL ON` is a panic outcome. In Show Mode the role table can lock `FX IMPORT`, `FX EDIT` and `FX TRUST`. Blind edits stage chains off-air.
- **fleet**: distributes the project and FX assets by hash (`MEDIA HAS`/FETCH generalised to any content hash). Its health view shows "plugin X missing on node B".

**Third-party:**
- **FFGL SDK headers.** BSD-3, transcribed constants. Test-time build of the SDK's samples from a user-supplied checkout (`PLAYMAP_FFGL_SDK`); never shipped.
- **GLEW.** Not shipped; PlayMap's own shim replaces it (MIT). If a user has `libGLEW.so.2.x`, it is used instead.
- **SkiaSharp**, already a dependency, decodes imported images.
- **Optional, Windows build-time only, S3 alternative:** glslang (BSD-3/Apache-2.0) + SPIRV-Cross (Apache-2.0) as native DLLs, *only if* the managed fix-ups miss the 97 % target.
- **No GPL/LGPL code.** The ISF-Files corpus (MIT, mixed credits, 6 Shadertoy ports) is **not** shipped; it is an opt-in test input.

---

## 7. Verification here (4-core Linux, Mesa llvmpipe on Xvfb :99, no GPU, no Windows)

**Real, in this container:**
- **Core unit tests (no GL):**
  - parse every ISF construct, including v1 files, both `THIS_NORM` spellings, bias arguments, errors with lines;
  - WIDTH/HEIGHT expressions;
  - golden translations for 330 and ES;
  - `FxTime`: origin, precision flag, FRAMEINDEX at 59.94;
  - anchored phases: continuity across a speed edit, identical on two clocks;
  - modulation curves, ranges and the step choice;
  - `Pcg32` random LFO.
- **Opt-in corpus test** (`PLAYMAP_ISF_CORPUS`):
  - ≥ 324/327 at 330 core and ≥ 305/327 at ES 3.00 (≥ 317 after S3);
  - for every compiled filter, a 64×36 render with a test image and finite pixels (no NaN).

  The baseline was measured in this research: 324 and 305 [R §1.5].
- **GL render tests** (the existing `GlFixture`; 64×36 to 256×144 to keep llvmpipe fast):
  - each built-in's pixel property, for example:
    - `fx.invert` of 0x40 → 0xBF;
    - `fx.mirror` symmetric;
    - `fx.blur` keeps the mean within 1 code and lowers the variance;
    - `fx.lut` with an identity `.cube` is bit-exact;
    - `fx.feedback` carries frame N−1;
    - `fx.strobe` never exceeds 3 flashes a second with the guard on;
  - orientation: an ISF generator writing `isf_FragNormCoord.y` gives the top row ≈ 1;
  - mix and blend maths against a CPU reference;
  - bypass and mix 0 bit-exact;
  - chains at all five attach points;
  - pattern override and raw frames untouched by chains;
  - a surface chain runs once for three projectors (a counter);
  - G-buffer: a known plane's `PM_POSITION` and `PM_NORMAL` within 1e-3;
  - `fx.worldnoise` continuous across two projector outputs that share a seam (the same world point gives the same value within 1/255);
  - determinism: two `RenderEngine`s, same project and leader instants → identical bytes for deterministic chains;
  - compile lane: a slot shows pass-through while compiling, and the frame time does not include the compile;
  - allocation-free frame path with 10 active slots.
- **FFGL on Linux (opt-in `PLAYMAP_FFGL_SDK` + g++)**, which research shows is feasible [R §2.4]:
  - the test setup builds the SDK's `AddSubtract` and `Gradients` samples as `.so` (8 s each) and `playmap_ffglshim.so`;
  - the host loads them on a 4.1+ core Mesa context and runs the full lifecycle;
  - assertions:
    - parameter enumeration (names, types, ranges);
    - `Gradients` output pixels vary with its parameters;
    - `AddSubtract` adds or subtracts its parameter's value to input codes;
    - the GL default state is restored (validator);
    - `FF_SET_TIME` is called with show time.
- **Probe:**
  - the PlayMap-written `pm_crash_plugin` (`abort()` in `FF_GET_INFO`) → quarantined with "the plugin crashed while describing itself (signal 6)";
  - a hang plugin (sleeps 10 s) → killed at 5 s and quarantined;
  - the node starts in both cases.
- **Isolated host (S1, Linux ring):** the test plugin segfaults on a parameter value → `fxhost` dies; the render loop's frame counter keeps advancing; the slot passes through, then recovers after restart; STATE shows `restarts: 1`.
- **Engine verb tests:** every FX verb's refusals (no-op, unknown, recursion, unknown source family, `FX TRUST` from a non-local session or an assistant). Published STATE fields after each verb. `FX CHECK` JSON on a broken shader reports the user's line number.
- **Desk (Avalonia headless):** drawer drag → `FX ADD`; slot bypass toggle → `FX BYPASS`; encoder-style `FX SET … BY`; every new `DeskAction` maps to a catalogue verb (the existing CI rule).

**Simulated:**
- **Audio:** a fake `IAudioFrames` (a sine and a pink-noise spectrum) for `audio`/`audioFFT` inputs and FFGL FFT buffers.
- **Modulation:** a fake bus with `midi:`/`osc:` values.
- **GPU timing:** timer-query plumbing is real on Mesa, but **its numbers are not representative**. Tests assert the plumbing (non-negative, published, budget light changes with a fake timer), never a millisecond target.

**Compile-only (stand-ins named):**
- Windows `stdcall` loading of `.dll` plugins: the code path shares `FfglInstance` with Linux; only the loader differs.
- `WGL_NV_DX_interop2` island and D3D11 shared-handle transport (S1, S2): behind `IFfglTransport`; the Linux ring implementation stands in.
- ANGLE running the ES dialect: Mesa's strict ES compiler (`ARB_ES3_compatibility`) stands in, and that is what the corpus numbers are measured on.
- macOS bundles: later.
- **Real GPU numbers** (the 0.25 ms per effect at 1080p, driver compile times) and **TDR behaviour** cannot be verified here. They are on the hardware-lab list.

---

## 8. Desk and Companion surface

**Desk:**
- **Effects drawer.**
  - Built-in families, project ISF, FFGL plugins (with trust state) and quarantined plugins (with words).
  - Search; a thumbnail of each effect over the selected target's picture (rendered at 160×90 on the compile lane, cached).
  - Badges: cost (measured, ms at 1080p scaled from the last run), *history*, *live*, *native*, *may flash*.
  - **Drag** onto a layer, surface, output, canvas or playlist item → `FX ADD`. **Alt-drag** adds at mix 0 (Resolume's habit).
- **Simple mode** (UX P14, "quad, drop, effect"): drop on the quad. The inspector shows the effect's **Amount** (mix) and its first two parameters, large.
- **Inspector "Effects" section** on every target:
  - the ordered slots (drag to reorder → `FX MOVE`);
  - per slot: bypass toggle, mix slider, blend menu, scale, parameter editors (the generator `ParamEditor`s, shared), a **bind** icon per parameter (popover: source picker from STATE's known sources, amount, curve, smooth), a presets menu, a cost readout and a state chip (compiling, faulted with the first error, isolated-restarting).
- **Right-click on a target:** Effects ▸ Add… (search) / Paste effects / Copy effects / Bypass all / Remove all / Retime.
- **Right-click on a slot:** Bypass, Reset, Duplicate, Move up/down, Save preset…, Apply preset ▸, Bind ▸, Edit shader (ISF; a built-in is duplicated into the project first), Show cost, Remove.
- These are `DeskAction`s, so toolbar, menu and shortcut send the same line, and the tooltip shows it.
- **Audition.** Holding **A** over a library item, or "Audition" in the menu, sends `FX AUDITION <view> <target> <kind>`. The desk's preview of that output shows the effect; the air does not. Releasing sends `OFF`.
- **ISF editor.** Text with JSON-header validation. On every pause in typing it runs `FX CHECK` and shows errors inline at the user's lines for both dialects, with a live 256×144 preview from the compile lane. **Save** sends `FX EDIT` (Review), and appears as one undo step.
- **AI.**
  - "Make an effect…" opens the assistant with the FX vocabulary.
  - The assistant drafts ISF, runs `FX CHECK` (Low) until it is clean, then proposes `FX IMPORT` + `FX ADD` (Review) as one staged diff the operator approves.
  - The assistant never calls `FX TRUST`.

**Companion** (consistent with `companion.md` §8):
- **Variables:** `fx_count`, `fx_faults`, `fx_cost_ms` (worst output), `fx_over_budget`.
- **Feedbacks (boolean):** `fx_bypassed {slot}`, `fx_state_is {slot, state}`, `fx_over_budget {output}`.
- **Value feedbacks:** `fx_param {slot, param}` → 0–1 plus text, live with modulation, for rings and gauges.
- **Actions:**
  - `FX BYPASS` (on, off, toggle);
  - `FX SET … BY $(this:delta)` for encoders (absolute verbs with a delta, as §8.6 asks);
  - `FX RECALL`;
  - `FX ADD` with a kind dropdown fed by `FX KINDS`;
  - `FX RETIME`;
  - **Panic: effects off** = `FX BYPASS ALL ON`.
- **Live preset group "Effects"**, auto-created per target with a chain: a bypass button with its feedback for each slot, a mix encoder, and encoders for the first three Number parameters, labelled from the ParamSpec words. It is re-created on structural change only.
- **Learn:** slot actions learn the desk's current **selection** (`STATE.selection`, companion package).

---

## 9. Risks and open questions

1. **GPU hangs from user shaders.** An unbounded loop causes a TDR on Windows and can take the air down.
   - Mitigations:
     - probation before air: `FX CHECK` / import render 30 frames at 256×144 on the compile lane with timer queries;
     - a static heuristic flags loops whose bound is not a constant or an input with MAX;
     - imports by an assistant are Review;
     - Show Mode can lock imports.
   - True containment (ISF out of process) is "later". TDR cannot be tested here.
2. **ANGLE ES compatibility** is 93 % today and 97 % with S3 (target). If S3 falls short, use the glslang + SPIRV-Cross import-time path (native DLLs, Windows build only). **Open:** which of the two the present package prefers for its own shader ports.
3. **FFGL in practice.**
   - Commercial plugins may be licensed to Resolume only. That is legal territory, so PlayMap never auto-scans Resolume's folders, and the drawer shows the plugin's "About" text.
   - Plugins compiled for GL 4.1 on macOS assume the core profile; some Windows plugins may assume a compatibility profile (**U**). The probe records failures with the driver log.
   - Release-built plugins leak GL state. `InvalidateState()` after every call is mandatory; validation is debug and probe only.
4. **FFGL GL version.** Today's render context is 3.3 core. Requesting 4.1 when `FfglEnabled` is set changes the context for everything. **Open:** whether to always request the highest core version (4.5 on Mesa), so tests and production match.
5. **Determinism.** Persistent buffers, live sources and plugins break frame identity. Badges plus pre-roll (S4) reduce the harm but cannot remove it. Backup mirroring guarantees stay limited to deterministic chains.
6. **TIME origin after a leader reboot.** The leader's monotonic clock restarts, TIME goes negative and re-anchoring kicks in, but content jumps exactly as generators do today. The real fix is a leader *show epoch* in the sync protocol (the fleet package). **Open.**
7. **Memory.** FLOAT persistent targets at 4K RGBA32F are 133 MB each. The cap (1 GB) refuses with the number. **Open:** honour `FLOAT` as RGBA16F when the cap would be exceeded, with a warning, instead of refusing.
8. **Surface chain resolution.** A surface is sampled by several projectors at different resolutions. The chain runs once at the **largest consumer's** size, cached per frame. **Open:** use UV-texel density instead.
9. **Cost of a mix pass per slot** on low-end iGPUs. It is skipped where it can be. The numbers need the hardware lab.
10. **The name "effect" clash** with `effect.*` generators. Keep both, and explain in EFFECTS.md: those are surface-aware *generators*, these are FX on chains. **Open:** rename the generators to `surface.*` in a later round, with aliases.

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Model, JSON, ids, `FindFx`, Sanitize (M1) | 6 |
| ISF parser, expression evaluator, translator 330/ES, builtin renames, line map (M2) | 12 |
| ISF renderer: passes, persistent, float, imported, audio textures, orientation, compile lane, program cache (M3) | 16 |
| Chain runner, mix pass, five attach points, per-slot timers, pattern/raw exclusion, allocation-free (M4) | 12 |
| Built-in library: 36 effects + 6 transitions + `pm.glsl` + G-buffer + pixel tests (M5) | 20 |
| Verbs, router, STATE, CONTEXT, presets, import/check/edit, retime (M6) | 12 |
| Modulation bindings and built-in sources (M7) | 6 |
| FFGL in-process host, ABI, state guard, parameter mapping, events, FFT, Linux shim, SDK sample tests (M8) | 16 |
| FFGL probe child process and quarantine, test plugin (M9) | 5 |
| Desk: drawer, inspector section, menus, audition, bind popover, ISF editor (M10) | 18 |
| Papers (M11) | 4 |
| **Must subtotal** | **127** |
| S1 isolated host (Linux ring real, Windows compile-only) | 14 |
| S2 Windows GL island (compile-only) | 6 |
| S3 ES fix-up tokenizer | 6 |
| S4 history pre-roll | 4 |
| S5 ISF generators as generator kinds | 3 |
| S6 ISF transitions hook (after the content fades) | 3 |
| S7 linked looks | 3 |
| **Total with shoulds** | **166** |
