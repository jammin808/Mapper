# wp3-shaders, wp3-shaders-2 on the wire

*What wp3-shaders builds: the node's quarantine and its verb, the shader worker's facts in STATE, FX CHECK's stages of the
Direct3D 12 road, and the HLSL library the Direct3D 12 backend reads. **The published exe composes none of the node's parts yet**:
the spine's composition has no hook for them (the papers note's first two merge-gate items), so on the exe the quarantine is the
empty in-memory one, STATE has no `fx.compile.worker`, FX CHECK reports no Direct3D 12 stages, nothing is kept in the node's home
and no renderer bypasses a quarantined kind. Everything below holds on a node composed with `ShaderParts` (`ShadersExtension`), as
every test of this package composes it. wp3-shaders-2 adds user effects drawn on the device (`design/shaders.md` §11).*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | Lifts the quarantine on an effect kind whose shader took the GPU down, so its slots draw again (the quarantine is the node's, never a draft's) | high | yes | wp3-shaders |

`FX RELEASE` takes a kind by its hash first (each entry's own, so two entries quarantined under one name are told apart), then by
the name it was quarantined under, then by a kind of the show (`fx.invert`, `isf:fa…`, an asset id) whose sources are quarantined,
and answers `{"released": [<kind names>]}`. It is class Setup: the gate refuses it while the show is armed ("Not while armed: FX
RELEASE puts set-up pictures or settings on the air. SHOW DISARM on the desk first."), and it is never drafted — from the blind
owner it is refused armed and acts on the node's quarantine at once when disarmed, the draft staging nothing. A kind that is not
quarantined is refused with the kinds that are ("fx.blur is not quarantined on this node (quarantined: Invert)"), and `ALL` with
none is refused ("no kind is quarantined on this node"); either changes nothing. A release the node's home refuses to record is
refused with the home's words ("the quarantine record could not be written (…): nothing changed") and changes nothing, in memory or
on disk. Each release is a journal row (`fx.release`).

## Forms added to existing rows

`FX CHECK <asset>|<kind>|<json>` keeps its syntax; on a node composed with the shader worker its report carries `stages`, the
Direct3D 12 road in order — `translate`, `glslang`, `validate`, `cross`, `dxc` — each with `ok` (true, false, or null when not run),
its words and the ISF line it points at. A quarantined kind answers one failed `quarantine` stage with the words that lift it; a
worker that could not answer answers one failed `worker` stage with the host's own words. The worker's stage check answers within 8 s
of the line (the engine bounds a stage check at 10 s): a check still waiting then — for the worker, its start or its compile — is
ended ("the check ran past 8 s and was ended") and the worker that held it is ended with it. FX CHECK is a query: whatever it meets,
it changes no quarantine and nothing on the air.

## STATE

Inside wp2-fx's `fx` block (`tests/fixtures/state/shaders.json`, cards in `shaders.cards.json`):

- `fx.compile.worker` (a node composed with the shader worker): `running`, `pid` (null when none runs), `restarts`, `lastMs` (the
  last compile's own time, recorded, never a pass mark), `compiled`, `failed`, `missing` (how many of the natives its hello named
  did not load: every compile then fails at the first stage that needs one; the card is amber while it is above 0), `natives` (each
  native's `name`, `ok` and `words` from the worker's hello) and `words` (its last trouble, or how it starts).
- `fx.quarantine` (a node composed with the shader worker, or any node that holds a quarantined kind): a list of `hash`, `kind`,
  `reason` (`gpuTime`, `deviceRemoved`, `compilerStopped`, or the word a newer build wrote for a reason this one does not know,
  kept as it was), `words`, `at` (Unix ms), `build`, and `slots` — the show's slots whose kind it is.

The renderer (`FxChainRunner.Quarantine`) draws a quarantined kind's slots bypassed, their input passing unchanged, and says so in
the slot's observed state (`fx.slots[].state` "bypassed", with words) once it is handed the node's quarantine; no render host hands
it one yet (the papers note's second merge-gate item), so on a node today a quarantined kind's slots still draw. Preflight's `fx`
section is red for each quarantined kind (fix `FX RELEASE <hash>`, the kind's own entry whatever its name) and for each slot that
uses one (fix `FX REMOVE <slot>`), and red while the record kept in the node's home could not be read whole at start ("The
quarantine record could not be read", with where it was kept aside); it is one green row only when the whole record was read and
no kind is quarantined.

## The quarantine record

`playmap.quarantine.json` in the node's home, read once at start and written before every change is held, so memory and the home
never disagree. A record that cannot be read is moved aside (`playmap.quarantine.json.unreadable-<unix ms>`) before anything can
write over it, journalled (`fx.quarantine`) with that name, and preflight stays red for the run; an entry with no hash is counted,
and the whole record is copied aside while the rest is held; an entry whose reason this build does not know stays quarantined. Adds
and releases may come from any thread: a lock orders them, and readers take immutable values.

## The shader worker

`PlayMap shader-worker` is a process of its own, started by the host with the first source it is given and spoken to in JSON lines:
the engine's `{"t":"compile","id","kind","name","fragment","vertex"}` and `{"t":"stop"}`, the worker's
`{"t":"hello","pid","natives":[…]}` and `{"t":"done","id","ok","stage","words","isfLine","stages":[…],"dxil":[vs, ps],"reflection":[vs, ps],"layout":{…},"ms"}`
(DXIL and reflection as base64; the layout's constants with name, HLSL type, offset and count, its textures with name, slot and
whether sampled, and its block's size). It runs below normal priority; its main thread only reads its input and its compiles run on
a thread of their own, so its input closing (the engine went) or a stop line ends it even mid-compile.

One source is in hand at a time. A source that is not answered — past the compile's 10 s deadline, past an FX CHECK's bound, or
abandoned by its caller — ends the worker that holds it before the next source is sent, and counts nothing: running long is not
stopping the worker. A worker that stops while it compiles is started again after a backoff doubling from 1 s to 30 s and given the
source again; a source that stops it three times fails with words ("the source stopped the shader worker 3 times (…): it fails,
and is not sent again until it changes") and is answered at once from then on, without a worker. That memory is the host's own,
for the node's run: it writes no quarantine, so no kind leaves the air for a compile on the Direct3D 12 road. A node that stops
mid-compile answers "the node is stopping" and counts nothing. The compilers — glslang and SPIRV-Tools (shaderc), SPIRV-Cross, DXC
and its validator — load only in the worker, each held to its SHA-256; the engine's process holds none.

## Papers note

(For the integration pass.)

- **Merge-gate items: the seams this package needs and does not own** (it works behind its own until they land, and the exe
  composes none of the node's parts meanwhile):
  1. The spine's composition (§5.7): `static partial void AddShaderParts(List<EngineExtensionFactory>)` in `EngineParts.Standard()`
     beside the other round-3 hooks, and a `Host/EngineParts.Shaders.cs` stub for the `Shaders` hook word, which this package fills
     with `extensions.Add(setup => new ShadersExtension(setup, new ShaderWorkerOptions()))`. Then a test on an
     `EngineParts.Standard()`-composed node (`HostRig`): STATE carries `fx.compile.worker` and `fx.quarantine`, preflight carries the
     `fx.quarantine` row, an entry written survives a restart from the same home (`ShaderParts.QuarantinePath`).
  2. The renderer's quarantine: `FxPass` and `RenderHost.Fx` are wp3-rhi's, and §4 grants this package only `FxChainRunner`'s hunk,
     so the line that hands `EngineServices.Quarantine` to `FxChainRunner.Quarantine` needs a hunk named in §4 or a spine seam (the
     render host's request carrying the quarantine, say). Then a quarantined slot's `fx.slots[].state` reads "bypassed" on that node.
  3. `fx.library.quarantined` (wp2-fx's `EngineCore.Fx.cs`) is a constant 0 beside a non-empty `fx.quarantine`: a hook or the line
     that writes `Services.Quarantine.All.Length` there, with this package's fixture then carrying `fx.library`.
  4. The HLSL library's PDBs: the build writes them to `obj/` beside the DXIL, and no CI step keeps them. A package edits only its
     own named block of the workflow, and this package's block must sit before `Restore` (it fetches a native the build copies), so
     an upload after the Windows job's `Build` step (`src/PlayMap.Render.D3D12/obj/**/hlsl/**/*.pdb` as `hlsl-symbols`) is the
     spine's step to add; until then the PDBs are not kept.
  5. Design §5 and §11 give user effects four static samplers (`s0`–`s3`); the worker emits every image's sampling through one
     sampler at `s0` (linear and clamped, as the GL path samples ISF images), which those four hold.
- Design §6 states the record's invariant as "one writer (the engine's lane)". It is met as one lock that orders every add and
  release, from whichever thread makes it, each written to the home before it is held; FX RELEASE on the control lane is today's only
  writer, since the worker host writes no quarantine, and wp3-shaders-2's GPU-time guard and wp3-stability-2's removal breadcrumbs
  add through the same lock.
- `docs/EFFECTS.md`: user ISF on the Direct3D 12 road (the Vulkan target: GLSL 4.50, glslang's relaxed rules, `spirv-val`,
  SPIRV-Cross to Shader Model 6.0 HLSL with the default block at b0, the built-ins at b1, image i at t i and every image sampled
  through the one sampler at s0, any other resource refused with its name, DXC to validated, signed DXIL) in the shader worker; the
  quarantine and FX RELEASE; FX CHECK's stages. GL user effects compile as before, on the compile lane.
- `docs/ARCHITECTURE.md` §5: the HLSL library under `src/PlayMap.Render.D3D12/Shaders` (one file per family, `Common.hlsli`'s
  conventions: `PmClip` turns GL's clip space into Direct3D's so targets stay top row first, one root signature with the binding
  block read through `ResourceDescriptorHeap`), `programs.json`, the layout table and the build step that writes each program's
  `Layout.hlsli`, embeds the library and, on Windows, its DXIL (compiled again when a source, the table or `programs.json` changes).
- The assistant's FX RELEASE test sits in the Assistant suite (`tests/PlayMap.Assistant.Tests/Shaders/`) on a real node, through
  this package's `ProjectReference` to the engine there.
- What CI proves: DXC's Linux build emits validated, signed DXIL (M6 answered yes), so every program compiles and is held to the
  table by reflection on both jobs; the build embeds DXIL on Windows only, where the DXC executable is packaged, and each embedded
  entry's reflection is the sources' own. Pixel parity of the HLSL with the GLSL is wp3-d3d12-core's; nothing here was drawn on a
  device.
