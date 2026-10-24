# wp3-shaders, wp3-shaders-2 on the wire

*What wp3-shaders builds: the node's quarantine and its verb, the shader worker's facts in STATE, FX CHECK's stages of the
Direct3D 12 road, and the HLSL library the Direct3D 12 backend reads. The exe composes the node's part (`EngineParts.Standard()`'s
`Shaders` hook adds `ShadersExtension.Factory`, the shader worker at its default options), so every node it runs keeps its
quarantine in its home, says `fx.compile.worker` and `fx.quarantine` in STATE, answers FX CHECK's Direct3D 12 stages through the
worker and carries the quarantine's preflight rows; a node composed without the part holds an empty quarantine in memory. Its
render host hands the renderer the node's quarantine, so a quarantined kind's slots draw bypassed. wp3-shaders-2 adds user effects
drawn on the device (`design/shaders.md` §11).*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | Lifts the quarantine on an effect kind whose shader took the GPU down, so its slots draw again (the quarantine is the node's, never a draft's) | high | yes | wp3-shaders |

`FX RELEASE` takes a kind by its hash first (each entry's own, so two entries quarantined under one name are told apart), then by
the name it was quarantined under, then by a kind of the show (`fx.invert`, `isf:fa…`, an asset id) whose sources are quarantined,
and answers `{"released": [<kind names>]}`, its words naming what it released and counting the show's slots that use it, read
before the release ("released Invert from this node's quarantine; 1 slot of this show uses it"); whether a slot draws is its
`fx.slots[].state`'s. It is class Setup: the gate refuses it while the show is armed ("Not while armed: FX RELEASE puts set-up
pictures or settings on the air. SHOW DISARM on the desk first."), and it is never drafted — from the blind owner it is refused
armed and acts on the node's quarantine at once when disarmed, the draft staging nothing. A kind that is not quarantined is refused
with the kinds that are ("fx.blur is not quarantined on this node (quarantined: Invert)"), and `ALL` with none is refused ("no kind
is quarantined on this node"); either changes nothing. A release the node's home refuses to record is refused with the home's words
("the quarantine record could not be written (…): nothing changed") and changes nothing, in memory or on disk. Each release is a
journal row (`fx.release`).

## Forms added to existing rows

`FX CHECK <asset>|<kind>|<json>` keeps its syntax; on a node composed with the shader worker its report carries `stages`, the
Direct3D 12 road in order — `translate`, `glslang`, `validate`, `cross`, `dxc` — each with `ok` (true, false, or null when not
run), its words and the ISF line it points at, and a failed stage makes the report's `ok` false (`docs/wire/fx.md`'s check report).
A quarantined kind answers one failed `quarantine` stage with the words that lift it; a worker that could not answer answers one
failed `worker` stage with the host's own words. The worker's stage check answers within 8 s of the line (the engine bounds a stage
check at 10 s): a check still waiting then — for the worker, its start or its compile — is ended ("the check ran past 8 s and was
ended") and the worker that held it is ended with it. FX CHECK is a query: whatever it meets, it changes no quarantine and nothing
on the air.

## STATE

Inside wp2-fx's `fx` block (`tests/fixtures/state/shaders.json`, cards in `shaders.cards.json`):

- `fx.library.quarantined` (wp2-fx's library counts): how many kinds the node's quarantine holds, the entries `fx.quarantine`
  lists; 0 on a node that quarantines nothing.
- `fx.compile.worker` (a node composed with the shader worker): `running`, `pid` (null when none runs), `restarts`, `lastMs` (the
  last compile's own time, recorded, never a pass mark), `compiled`, `failed`, `missing` (how many of the natives its hello named
  did not load: every compile then fails at the first stage that needs one; the card is amber while it is above 0), `natives` (each
  native's `name`, `file` — the file its pin names on the worker's platform, `dxcompiler.dll` or `libdxcompiler.so` for `dxc`,
  empty where PlayMap carries none — `ok` and `words`, from the worker's hello) and `words` (its last trouble, or how it starts).
- `fx.quarantine` (a node composed with the shader worker, or any node that holds a quarantined kind): a list of `hash`, `kind`,
  `reason` (`gpuTime`, `deviceRemoved`, `compilerStopped`, or the word a newer build wrote for a reason this one does not know,
  kept as it was), `words`, `at` (Unix ms), `build`, and `slots` — the show's slots whose kind it is.

The renderer draws a quarantined kind's slots bypassed, their input passing unchanged and nothing of them drawn: the render host
hands each effects pass the node's quarantine (`FxPass.Quarantine`, its chain runner's) before the engine reads the pass, a pass
made again after a context loss included. The slot's observed state says so: `fx.slots[].state` "bypassed" with the words
"quarantined on this node (FX RELEASE lifts it): its input passes unchanged", `gpuMs` 0 and `bypass` false (the show's own bypass
is not set), on every frame the quarantine holds the kind: a slot that drew before lets go of its renderer at once, and the
renderer's idle sweep never forgets its state. Once released, the slot draws its effect again, `compiling` and then `ok`.

Preflight's `fx` section is red for each quarantined kind (fix `FX RELEASE <hash>`, the kind's own entry whatever its name) and
for each slot that uses one (fix `FX REMOVE <slot>`), and red while the record kept in the node's home could not be read whole at
start ("The quarantine record could not be read", with where it was kept aside); it is one green row only when the whole record
was read and no kind is quarantined.

## The quarantine record

`playmap.quarantine.json` in the node's home, read once at start and written before every change is held, so memory and the home
never disagree. A record that cannot be read is moved aside (`playmap.quarantine.json.unreadable-<unix ms>`) before anything can
write over it, journalled (`fx.quarantine`) with that name, and preflight stays red for the run; an entry with no hash is counted,
and the whole record is copied aside while the rest is held; an entry whose reason this build does not know stays quarantined. Adds
and releases may come from any thread: a lock orders them, and readers take immutable values.

## The shader worker

`PlayMap shader-worker` is a process of its own, started by the host with the first source it is given and spoken to in JSON lines:
the engine's `{"t":"compile","id","kind","name","fragment","vertex"}` and `{"t":"stop"}`, the worker's
`{"t":"hello","pid","natives":[…]}` (each native's `name`, `file`, `ok` and `words`) and
`{"t":"done","id","ok","stage","words","isfLine","stages":[…],"dxil":[vs, ps],"reflection":[vs, ps],"layout":{…},"ms"}` (DXIL and
reflection as base64; the layout's constants with name, HLSL type, offset and count, its textures with name, slot and whether
sampled, and its block's size). It runs below normal priority; its main thread only reads its input and its compiles run on a
thread of their own, so its input closing (the engine went) or a stop line ends it even mid-compile.

One source is in hand at a time. A source that is not answered — past the compile's 10 s deadline, past an FX CHECK's bound, or
abandoned by its caller — ends the worker that holds it before the next source is sent, and counts nothing: running long is not
stopping the worker. A worker that stops while it compiles is started again after a backoff doubling from 1 s to 30 s and given the
source again; a source that stops it three times fails with words ("the source stopped the shader worker 3 times (…): it fails, and
is not sent again until it changes") and is answered at once from then on, without a worker. That memory is the host's own, for the
node's run: it writes no quarantine, so no kind leaves the air for a compile on the Direct3D 12 road. A node that stops mid-compile
answers "the node is stopping" and counts nothing. The compilers — glslang (shaderc), SPIRV-Tools (inside shaderc on Linux, its own
library on Windows), SPIRV-Cross and DXC's compiler alone (dxcompiler, whose built-in validator validates the DXIL and writes its
retail hash: `dxcompiler.dll` from Microsoft.Direct3D.DXC on Windows, `libdxcompiler.so` from DXC v1.9.2609's Linux release on
Linux, and no `dxil.dll` or `libdxil.so`; `docs/round3/PLAN.md` §9 decision 24) — load only in the worker, each held to its
SHA-256; the engine's process holds none. A vertex file's varyings meet its fragment file's by name: `cross` gives each pixel-entry
input the location, and so the `TEXCOORD` semantic, of the vertex output of its name, and refuses one no vertex output of its name
and type writes ("c: the fragment shader reads a varying the vertex shader does not write").

## Papers note

(For the integration pass.)

- **Merge-gate items: the seams this package needs and does not own, each landed on this branch** (§5.7: each a `Spine:` commit
  of its own, reaching `main` with the package's merge; PLAN §2.3's `Shaders` row names the stub). Items 1 to 4 and 6 are spine
  code, each committed before the fill that uses it; item 7 is spine words, committed after the fill, bringing `FxStage`'s summary
  in line with the stage names the worker and the FX CHECK row carry; item 5 is a design fact and lands in the design:
  1. The composition's `Shaders` hook, in `src/PlayMap.Engine/Host/EngineParts.cs`, `tools/gen_stubs3.py` and the stub
     `src/PlayMap.Engine/Host/EngineParts.Shaders.cs`: `EngineParts.Standard()` calls `AddShadersParts` between Observe's and
     Preview's (§2.3's hook-word order), `tools/gen_stubs3.py`'s `ENGINE_PARTS` table names the word, and its stub
     `Host/EngineParts.Shaders.cs` adds `ShadersExtension.Factory` (the worker at its default options: this exe in its
     `shader-worker` role, started with the first source). `ShadersExtensionTests` take that factory from `EngineParts.Standard()`
     by reference and start a node (`HostRig`) with it: STATE carries `fx.compile.worker` and `fx.quarantine`, preflight its
     `fx.quarantine` row, and an entry the node quarantined is read again, red with its `FX RELEASE`, by a node started from the
     same home (`ShaderParts.QuarantinePath`).
  2. The renderer's quarantine, in `src/PlayMap.Render/Effects/FxPass.cs` and `FxStats.cs`,
     `src/PlayMap.Engine/Rendering/RenderHost.Fx.cs` and `src/PlayMap.Engine/Fx/FxBoard.cs`: `FxPass.Quarantine` passes through to
     its chain runner's, and `RenderHost.Fx`'s `ComposeFx` hands it `EngineServices.Quarantine` the first time each pass meets the
     engine (the render host's request is made before any extension configures, so it cannot carry the quarantine).
     `FxSlotState.Quarantined` is the renderer's word for a held slot, and `FxBoard` (wp2-fx's) reads it as "bypassed", while a
     renderer's `Bypassed` (the show's own bypass or mix 0) stays read from the project. This package's `FxChainRunner` hunk reports
     a held slot `Quarantined` at no GPU time. `QuarantineRenderTests` start a node with the exe's render host and shaders part: the
     quarantined slot's output shows its chain's input and its `fx.slots[].state` reads "bypassed" with those words, and after
     `FX RELEASE` it draws its effect and reads "ok".
  3. `fx.library.quarantined`, in wp2-fx's `src/PlayMap.Engine/EngineCore.Fx.cs`: it is the count of `EngineServices.Quarantine`, so
     it and `fx.quarantine` agree. This package's fixture carries `fx.library` beside `fx.compile` and `fx.quarantine`, and
     `ShadersFixtureTests` hold its count to the one entry the fixture's node quarantines; every other committed fixture comes from
     a node that quarantines nothing and reads 0.
  4. The HLSL library's PDBs, in `.github/workflows/build.yml`: the build writes each entry's beside its DXIL in `obj/` where DXC's
     executable runs (Windows), and the Windows job uploads them after its build and version check
     (`src/PlayMap.Render.D3D12/obj/**/hlsl/**/*.pdb` as `hlsl-symbols`, `if-no-files-found: error`), a spine step outside this
     package's named block, which sits before `Restore`. `HlslBuildTests` read the workflow: the upload follows the Windows job's
     `Build` with no condition of its own and that path, the Linux job has none, and on Windows the glob holds both entries' PDBs of
     every program.
  5. The user effects' samplers, a design fact with no code, in `docs/round3/design/shaders.md` §5 and §11 (a `Papers:` commit):
     design §5 and §11 give user effects a table of the four static samplers (`s0`–`s3`), and the worker gives every image one
     sampler, declared once at `s0` (`SpirvCross.ShareSampler`; `ShaderRoadTests` read one sampler, register 0, space 0, from DXC's
     reflection), so the table's `s0` is the linear, clamped one, as the pictures an effect reads are on the GL path. Design §5 and
     §11 say so for wp3-shaders-2, whose root signature it is.
  6. The program table's `sampled` flag, in wp3-rhi's generator (`tests/PlayMap.Render.Tests/Rhi/GlslDeclarations.cs`) and the
     table it writes (`src/PlayMap.Core/Gpu/Programs/programs.layout.json`): a sampler handed to a helper a stage defines counts as
     loaded where that helper's own sampler parameter is only the first argument of `texelFetch`, `textureSize` or another such
     helper, so the output program's `uMeasuredGrid` and `uMeasuredBlend` are loaded, as the GLSL reads them and the HLSL `Load`s
     them; no other program's flag, constant, slot or input changes, and `ProgramLayoutTests` hold the rule.
  7. The stage words, in `src/PlayMap.Engine/Seams.Round3.cs` (doc comments only): `FxStage`'s summary names the stages FX CHECK
     carries in the road's order (`translate`, `glslang`, `validate`, `cross`, `dxc`) and the single stages that answer for the
     whole road (`quarantine`, `worker`, `stages`), and `IFxStageCheck`'s names what this package's worker checks and what
     wp3-shaders-2 adds.
- Design §6 states the record's invariant as the code meets it: one lock that orders every add and release, from whichever thread
  makes it, each written to the home before it is held, rather than one writer on the engine's lane; FX RELEASE on the control lane
  is today's only writer, since the worker host writes no quarantine, and wp3-shaders-2's GPU-time guard and wp3-stability-2's
  removal breadcrumbs add through the same lock.
- `docs/EFFECTS.md`: user ISF on the Direct3D 12 road (the Vulkan target: GLSL 4.50, glslang's relaxed rules, `spirv-val`,
  SPIRV-Cross to Shader Model 6.0 HLSL with the default block at b0, the built-ins at b1, image i at t i and every image sampled
  through the one sampler at s0, the vertex entry ending with the render hardware interface's conversion to Direct3D's clip space
  (`ClipSpace.ToDirect3D`), each varying matched by name across the entries, any other resource refused with its name, DXC to
  validated, signed DXIL) in the shader worker; the quarantine and FX RELEASE; FX CHECK's stages. GL user effects compile on the
  device's compile lane.
- `docs/ARCHITECTURE.md` §5: the HLSL library under `src/PlayMap.Render.D3D12/Shaders` (one file per family, `Common.hlsli`'s
  conventions: positions in the render hardware interface's clip space and every vertex entry ending with `PmClip`, which is
  `ClipSpace.ToDirect3D`, so a draw lands on the GL backend's rows and depths; a `float4x4` constant read as `mul(v, M)` and the
  normal matrix's `float3x3` as `mul(M, v)`; the present drawing the picture unflipped into a back buffer that runs top row first;
  one root signature with the binding block read through `ResourceDescriptorHeap`), `programs.json`, and the build
  step that writes each program's `Layout.hlsli` from the render hardware interface's program table
  (`src/PlayMap.Core/Gpu/Programs/programs.layout.json`, read as data, the one table both backends and the HLSL build read), embeds
  the library and, on Windows, its DXIL (compiled again when a source, the table or `programs.json` changes).
- The assistant's FX RELEASE test sits in the Assistant suite (`tests/PlayMap.Assistant.Tests/Shaders/`) on a real node, through
  this package's `ProjectReference` to the engine there.
- What CI proves: DXC's Linux build emits validated, signed DXIL (M6 answered yes; `libdxcompiler.so` alone, its built-in validator
  writing each container's retail hash, which `HlslBuildTests` recompute), so every program compiles on both jobs and
  `ShaderLayoutTests` hold its reflection to its `ProgramLayout` in the program table there: the same programs on both sides, the
  block at b0 with the table's constants, offsets, counts and size and its matrices `row_major`, the table's texture slots with
  sampling exactly where the table says sampled, and the vertex entry's input signature as the input layout the table's inputs make
  (`ATTR<location>`, a float format of each input's float count, buffer 0 per vertex and buffer 1 per instance);
  `HlslConventionTests` hold the clip conversion, the matrices' order and the vertex inputs in the source there too; the build
  embeds DXIL on Windows only, where the DXC executable is packaged, and each embedded entry's reflection is the sources' own.
  Pixel parity of the HLSL with the GLSL is wp3-d3d12-core's (the present held to GL's on the window, `programs.json`'s `window`
  class); CI draws none of it on a device.
