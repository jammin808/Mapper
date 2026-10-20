# Design: `wp3-shaders` and `-2` — one HLSL source for PlayMap's programs, user ISF out of process

*How the 40 product programs will reach Direct3D 12, how a user's ISF effect will become DXIL without a compiler ever loading
into the engine and then draw on the Direct3D 12 device, and what happens to a shader that brings the GPU down. Package sections:
`PLAN.md` §3.4 (wp3-shaders, W1) and §3.16 (wp3-shaders-2, W3: user effects on Direct3D 12, §11 here). The library as it stands
is the map's §2.4; the device that runs the result is `d3d12.md`.*

**The decision in one paragraph.** PlayMap's own programs will be written once more, in HLSL, with the conventions the GLSL library
already keeps, compiled by DXC at build time into DXIL with PDBs, and checked against the RHI's program layouts by reflection; the
GLSL stays only for the GL backend and leaves with it. A user's ISF source takes a longer road — PlayMap's translator to GLSL
4.50, glslang's relaxed mode to SPIR-V, `spirv-val`, SPIRV-Cross to HLSL, DXC to validated DXIL — inside `PlayMap shader-worker`,
a process of its own that the engine starts and watches as it does the optimise worker. A user shader that the GPU-time guard or
a device removal names is quarantined: its slots draw bypassed, with words, until a person releases it.

---

## 1. What exists

`ShaderLibrary.cs` and ten package partials hold 40 named programs in GLSL 3.30, 46 stage bodies, with no `#version` line (the
dialect's header is prepended) [map §2.4]. Their conventions: targets top row first, row-vector matrices, UV v down, geometry from
`gl_VertexID`, exact reads with `texelFetch`, and only the present into a window flips. The library's own comment promises "a
Direct3D backend supplies the same names in HLSL with the same uniforms". User ISF goes through `IsfTranslator` (Core) to GLSL
3.30 core or ES 3.00, compiled on the compile lane; 324 of 327 public ISF files compile on Mesa [map §2.12].

## 2. The HLSL library

- **Files.** `src/PlayMap.Render.D3D12/Shaders/<family>.hlsl` (`core`, `video`, `stage`, `output`, `content`, `fractal`,
  `reactive`, `hap`, `hapenc`, `fx`, `led`, `send`, `overlay`, `wall`), each program a vertex and a pixel entry named for the
  program (`OutputVS`, `OutputPS`), listed in `Shaders/programs.json` (program name → file, entries) so the build and the tests
  read one table.
- **Conventions**, translated once:

| GLSL today | HLSL |
|---|---|
| geometry from `gl_VertexID` | `SV_VertexID` |
| `texelFetch(t, p, 0)` (exact reads) | `t.Load(int3(p, 0))` |
| `texture(t, uv)` | `t.Sample(s, uv)` with the slot's sampler |
| a `mat4` constant: a row-vector transform (`m * v` of the block as set computes v·M) | `row_major float4x4`, `mul(v, M)` |
| a `mat3` constant (the normal matrix N = R·S⁻¹): a column-vector transform (`m * v` computes M·v) | `row_major float3x3`, `mul(M, v)` |
| targets top row first, UV v down | the texture origin agrees (top-left, v down): sampling changes nothing, and the GL present's flip does not exist here |
| the RHI's clip space (`ClipSpace`, Core): clip y = −w is the target's top row, z from −w to w is depth 0 to 1, back faces clockwise in the raster seen top row first | Direct3D's rasteriser differs (clip y = +w is its top row, z < 0 is clipped): every vertex entry ends with `pos.y = -pos.y; pos.z = (pos.z + pos.w) * 0.5;` (`ClipSpace.ToDirect3D`), and pipelines cull with `FrontCounterClockwise = TRUE` and `CULL_MODE_BACK`; without it every offscreen pass draws upside down |
| integer-exact maths (`video.pack`, `hapenc.*`) | `uint` arithmetic and `asuint`/`asfloat`, never float round trips |
| a uniform left unset reads 0 | the constant ring is zeroed per draw before the builder writes |

- **Bindings.** Constants in `cbuffer Program : register(b0)` with the GLSL uniforms' names, types and order, so the RHI's
  `ProgramLayout` is one table for both backends. Textures and samplers through the binding block (`d3d12.md` §4):

```hlsl
// Common.hlsli
cbuffer Bindings : register(b1) { uint4 pmTex[4]; uint4 pmSmp[4]; }   // 16 texture and 16 sampler indices
#define PM_TEX(slot) ((Texture2D<float4>)ResourceDescriptorHeap[pmTex[(slot) >> 2][(slot) & 3]])
#define PM_SMP(slot) ((SamplerState)SamplerDescriptorHeap[pmSmp[(slot) >> 2][(slot) & 3]])

// output.hlsl, the slot numbers from the program's layout (generated into OutputSlots.hlsli by the build)
float4 OutputPS(VsOut i) : SV_Target
{
    float4 picture = PM_TEX(SLOT_uPicture).Sample(PM_SMP(SLOT_uPicture), i.uv);
    // … the same steps as the GLSL, in the same order …
    return float4(c * uAir, 1);
}
```

## 3. Build

- An MSBuild target in `PlayMap.Render.D3D12.csproj` runs DXC (`Microsoft.Direct3D.DXC`) for each entry of `programs.json`:
  `-T vs_6_6` / `-T ps_6_6`, `-HV 2021`, `-Zi` with the PDB written beside, reflection kept in a separate blob, the DXIL validated
  and signed by `dxil.dll`. The DXIL and reflection blobs are embedded resources; the PDBs go to the CI's symbols artifact, never
  into the installer. Only changed files compile again.
- The slot headers (`<Program>Slots.hlsli`) are generated before DXC runs from the program table's file
  (`src/PlayMap.Core/Gpu/Programs/programs.layout.json`, wp3-rhi's), read as data by an MSBuild step, so a texture's slot has one
  source and `Render.D3D12` never references `Render`. Before wp3-rhi merges, this package works against a parse of the GLSL
  declarations in the same shape.
- **On Linux** the target runs if M6 finds DXC there emits validated, signed DXIL; otherwise it is skipped, the assembly embeds no
  DXIL, and `HlslProgramSetTests` (Linux) hold `programs.json`'s names equal to `ShaderLibrary.Names`. Direct3D 12 never runs on
  Linux, so nothing there needs the blobs.

## 4. The layout check

`ShaderLayoutTests` read each program's reflection (DXC's `IDxcUtils::CreateReflection`) and compare it with the RHI's
`ProgramLayout`: every constant's name, type and offset; every texture slot. Before wp3-rhi merges, the GLSL side is a parse of the
library's uniform declarations. The integer-exact programs that only `Load` (`video.pack`, `video.unpack`) are listed for wp3-d3d12-core's byte-exact parity
tests. The four HAP encoder programs (`hapenc.*`) are written, compiled, reflected and drawn once on WARP like the rest, but their
blocks are not a parity test this round: the encoder stays a GL user in the optimise worker, and its Direct3D 12 version (wp3-video's
stretch) carries its own evidence.

## 5. The shader worker

```
PlayMap.exe shader-worker     a process the engine starts and watches; one JSON object a line on standard input and output
```

**Request and reply.**

```json
{ "id": 17, "kind": "sha256:…", "source": "/*{ \"ISFVSN\": \"2\", … }*/ …", "passes": 2 }
{ "id": 17, "ok": true, "stage": "done", "dxil": ["<base64 vs>", "<base64 ps>"], "layout": { … }, "ms": 84 }
{ "id": 18, "ok": false, "stage": "glslang", "words": "line 12: 'foo' : undeclared identifier", "isfLine": 12 }
```

**The stages**, each failure in its own words with the line mapped back to the ISF source through the translator's `#line`
directives:

| # | Stage | Native | What it checks |
|---|---|---|---|
| 1 | `translate` | — (Core) | `IsfTranslator`'s new target `Glsl450Vulkan`: `#version 450`, explicit `layout(location)` ins and outs, images at fixed set and binding, ISF's inputs as loose uniforms, its built-ins (`TIME`, `RENDERSIZE`, `PASSINDEX`, `FRAMEINDEX`, `DATE`) as a block |
| 2 | `glslang` | glslang (C interface), relaxed rules (`-R`) | compiles existing OpenGL GLSL without edits; loose uniforms into `gl_DefaultUniformBlock` at the fixed set and binding |
| 3 | `validate` | SPIRV-Tools (`spvValidate`) | the SPIR-V is valid before anything trusts it |
| 4 | `cross` | SPIRV-Cross (C API) | HLSL Shader Model 6.0, combined image samplers split, registers remapped to user effects' root signature (below) |
| 5 | `dxc` | DXC | validated, signed DXIL and its reflection |

**User effects' root signature.** User HLSL from SPIRV-Cross binds the classic way, so user effects get a root signature of their
own: the default uniform block at `b0`, ISF's built-ins as root constants at `b1`, a descriptor table of `t0`–`t15` and one of
`s0`–`s3`. The effect pass copies its textures' descriptors into the frame's ring range per draw (`CopyDescriptorsSimple`,
allocation-free). Product programs keep the bindless root signature. This package fixes the remap the worker emits; the root
signature, the copies and the pipelines on the device are wp3-shaders-2's (§11).

**Supervision.** Started and watched as the optimise worker is: restarted with backoff (1 s doubling to 30 s); a compile that runs
past 10 s is ended and fails with words; a source that stops the worker three times fails its kind with words and is never sent
again until its source changes. The engine never loads glslang, SPIRV-Tools, SPIRV-Cross or DXC; `ShaderWorkerTests` assert it
from the engine process's module list.

**On GL.** User effects on the GL backend compile as today on the compile lane, through `IGpuDevice.CreateProgramAsync` (wp3-rhi);
the worker serves Direct3D 12 only.

**The alternative.** Mesa's spirv_to_dxil (stretch) would replace stages 4–5 with one; it has no package and is built from pinned
Mesa source if measured worth it on the corpus.

## 6. Robustness and quarantine

- **What protects the GPU.** A validated module; Direct3D 12's defined out-of-bounds reads (an SRV read outside a texture returns
  zero); the GPU-time guard per user slot measured by timestamps (wp3-shaders-2's must, §11); and, as the last line, the device's
  timeout, whose removal wp3-stability-2 rebuilds from. No loop counter is
  injected into user code: it would change what the shader means.
- **The record.**

```csharp
namespace PlayMap.Core.Effects;

public sealed record QuarantineEntry(string KindHash, string Kind, QuarantineReason Reason, string Words, long AtUnixMs,
    string Build);

public enum QuarantineReason { GpuTime, DeviceRemoved, CompilerStopped }

// Kept in the node's home, never in the project: a quarantine is this machine's fact about its GPU, so a blind draft never
// carries it and a hot backup does not mirror it.

/// <summary>Kept in the node's home; read at start; one writer (the engine's lane).</summary>
public sealed class ShaderQuarantine
{
    public bool IsQuarantined(string kindHash, [NotNullWhen(true)] out QuarantineEntry? entry);
    public void Add(QuarantineEntry entry);
    public bool Release(string kindHash);
    public ImmutableArray<QuarantineEntry> All { get; }
}
```

- **Its effect.** A quarantined kind's slots draw bypassed: the picture passes through unchanged (± 0 codes), on every backend,
  with words in the slot's STATE; the rest of the chain draws.

## 7. Facts in STATE

```json
"fx": {
  "compile": { "lane": "shader-worker", "worker": { "running": true, "restarts": 0, "lastMs": 84 } },
  "quarantine": [ { "kind": "Wobble Pro", "reason": "deviceRemoved",
                    "words": "the GPU stopped inside this effect's pass on op_2 (DRED: last completed 'fx op_2 #3')",
                    "at": 1760600000000, "build": "0.2.0+17.abc1234" } ]
}
```

## 8. Verbs

- `FX RELEASE <kind>|ALL` (High, **Setup**): lifts a quarantine; refused while armed by the gate's Setup class ("a shader that
  stopped the GPU waits for a rehearsal"), and never drafted blind (Setup is not draftable, and the quarantine is the node's, not the
  project's); refused for a kind not quarantined, and `ALL` refused when nothing is ("no kind is quarantined"); answers the kinds
  released.
- `FX CHECK <asset>|<kind>|<json>` (existing row, words extended): reports every stage of the Direct3D 12 road beside the GL
  dialects', each with its words and the ISF line.

## 9. Tests

| Test | Job | Holds |
|---|---|---|
| `HlslBuildTests` | Windows | every program compiles to validated DXIL with a PDB; the set equals `ShaderLibrary.Names` |
| `HlslProgramSetTests` | both | `programs.json` names every library program once |
| `ShaderLayoutTests` | Windows (GLSL side both) | reflection equals the layouts |
| `IsfVulkanTargetTests` | both | the built-in ISF files and the test corpus translate, compile under `-R` and validate; a translator error keeps its ISF line |
| `ShaderWorkerTests` | both (to DXIL on Windows) | a good source → DXIL and a layout; an error → its stage and ISF line; a fake compiler that aborts → restarts, the third stop fails the kind; a 10 s compile is ended; the engine holds no compiler module |
| `QuarantineTests` | both | a quarantined slot passes its input through (± 0 on GL); STATE and preflight carry it; `FX RELEASE` armed → the gate's verdict is a refusal by class and the quarantine is unchanged; from the blind owner never drafted — armed refused, disarmed acting on the node's quarantine at once, the draft holding no quarantine either way; a kind not quarantined and `ALL` with none refused with words; otherwise the slot draws again |

## 10. Risks

- **Two sources while GL lives** (`PLAN.md` R6): layouts by reflection, pixels by `PixelParityTests` per program, and GL's retirement
  next round.
- **SPIRV-Cross's HLSL and bindings** (Godot found the reflection route brittle): ISF's binding model is small — one uniform block,
  images, built-ins — and the remap is fixed; spirv_to_dxil stays measured behind the same stage.
- **The corpus number** must be measured again on this road; wp3-shaders-2 records it against 324 of 327 above its cut line, and
  M1's AUTO record cites it.

---

## 11. User effects on Direct3D 12 (`wp3-shaders-2`, W3)

After wp3-d3d12-core, the device draws every product program; this continuation makes user ISF effects draw there too, so a show
with effects runs end to end on `PRESENT PATH D3D12`.

- **The request.** On show change (and on `FX ADD`, `FX SET` of a kind), the engine sends each user kind the plan reaches to the
  worker once, keyed by its hash; the reply's DXIL and reflection go to `IGpuDevice.CreateProgramAsync`
  (`ProgramCode.Dxil`), whose answer — a `ProgramId` and a `ProgramLayout` — the effect pass draws through the builder, as on GL. A
  kind whose DXIL has not come back draws bypassed with words ("compiling on the shader worker"), counted.
- **The root signature** of §5 and the per-draw descriptor copies into the frame's ring (`Render.D3D12/Effects/`); ISF's built-ins
  as root constants; samplers from the four static ones.
- **ISF on the device.** Multi-pass with sizes, persistent buffers (`ImportKind.IsfPersistent` in the plan: never aliased),
  imported pictures, `EffectGenerator` (an effect drawn as a source), feedback through the persistent buffers — each as the GL
  path draws it today.
- **Pipelines at arm.** The effect pass declares its user programs' keys (`GraphBuilder.Draws`), so wp3-d3d12-passes-2's warm-up
  makes them before ARM answers.
- **The GPU-time guard** (the measured cause: a frame whose GPU time passes the output's period misses its vblank, which the present
  ledger counts). Each user slot is bracketed by timestamps. A slot is the cause of a frame when the frame's GPU time (from
  `IGpuTimers`) is over the output's period (from its mode) and would be under it without the slot's own time; a slot that is the
  cause on 30 consecutive frames is bypassed and quarantined (`QuarantineReason.GpuTime`) with both numbers in its words.
  `GpuTimeGuardTests` drive it with a fake timer: 29 frames → nothing; 30 → quarantined; a frame over the period that would still be
  over without the slot → nothing for that slot.
- **The corpus.** The Windows job clones the public ISF files at a pinned commit and runs each through the worker to DXIL and one
  draw on WARP; the count goes to the job's summary and to `fx.compile.corpus` in the fixture, against 324 of 327 on GL, and M1's
  AUTO record cites it.

| Test | Job | Holds |
|---|---|---|
| `UserEffectD3D12Tests` | Windows | the FX pixel scenes on WARP within their rows of the tolerance table; multi-pass and persistent buffers; an effect generator; a kind compiling → bypassed with words, then drawn |
| `EffectRootSignatureTests` | Windows | the descriptor copies per draw allocate nothing; the debug layer quiet |
| `UserPipelineWarmTests` | Windows (at the merge with wp3-d3d12-passes-2) | an armed show's user programs made before ARM answers |
| `GpuTimeGuardTests` | both | the 30-frame rule with a fake timer, as above |
| `IsfCorpusD3D12Tests` | Windows | the pinned corpus to DXIL and one draw each; the count recorded |
