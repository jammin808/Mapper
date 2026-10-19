# wp3-shaders, wp3-shaders-2 on the wire

*What wp3-shaders ships: the node's quarantine and its verb, the shader worker's facts in STATE, FX CHECK's stages of the Direct3D 12
road, and the HLSL library the Direct3D 12 backend reads. wp3-shaders-2 adds user effects drawn on the device (`design/shaders.md`
§11).*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | Lifts the quarantine on an effect kind whose shader took the GPU down, so its slots draw again (the quarantine is the node's, never a draft's) | high | yes | wp3-shaders |

`FX RELEASE` takes a kind by the name it was quarantined under, by its hash, or by a kind of the show (`fx.invert`, `isf:fa…`, an
asset id) whose sources are quarantined, and answers `{"released": [<kind names>]}`. It is class Setup: the gate refuses it while
the show is armed ("Not while armed: FX RELEASE puts set-up pictures or settings on the air. SHOW DISARM on the desk first."), and
it is never drafted — from the blind owner it is refused armed and acts on the node's quarantine at once when disarmed, the draft
staging nothing. A kind that is not quarantined is refused with the kinds that are ("fx.blur is not quarantined on this node
(quarantined: Invert)"), and `ALL` with none is refused ("no kind is quarantined on this node"); either changes nothing. Each
release is a journal row (`fx.release`).

## Forms added to existing rows

`FX CHECK <asset>|<kind>|<json>` keeps its syntax; on a node composed with the shader worker its report carries `stages`, the
Direct3D 12 road in order — `translate`, `glslang`, `validate`, `cross`, `dxc` — each with `ok` (true, false, or null when not run),
its words and the ISF line it points at. A quarantined kind answers one failed `quarantine` stage with the words that lift it; a
worker that could not answer (a stop, the 10 s deadline) answers one failed `worker` stage.

## STATE

Inside wp2-fx's `fx` block (`tests/fixtures/state/shaders.json`, cards in `shaders.cards.json`):

- `fx.compile.worker` (a node composed with the shader worker): `running`, `pid` (null when none runs), `restarts`, `lastMs` (the
  last compile's own time, recorded, never a pass mark), `compiled`, `failed`, `natives` (each native's `name`, `ok` and `words`
  from the worker's hello) and `words` (its last trouble, or how it starts).
- `fx.quarantine` (a node composed with the shader worker, or any node that holds a quarantined kind): a list of `hash`, `kind`,
  `reason` (`gpuTime`, `deviceRemoved`, `compilerStopped`), `words`, `at` (Unix ms), `build`, and `slots` — the show's slots whose
  kind it is.

The renderer (`FxChainRunner.Quarantine`) draws a quarantined kind's slots bypassed, their input passing unchanged, and says so in
the slot's observed state (`fx.slots[].state` "bypassed", with words); the render host hands the runner the node's quarantine once
the seam named in the papers note lands. Preflight's `fx` section is red for each quarantined kind (fix `FX RELEASE <kind>`) and for
each slot that uses one (fix `FX REMOVE <slot>`), and one green row when none is quarantined.

## The shader worker

`PlayMap shader-worker` is a process of its own, started by the engine with the first source and spoken to in JSON lines:
the engine's `{"t":"compile","id","kind","name","fragment","vertex"}` and `{"t":"stop"}`, the worker's
`{"t":"hello","pid","natives":[…]}` and `{"t":"done","id","ok","stage","words","isfLine","stages":[…],"dxil":[vs, ps],"reflection":[vs, ps],"layout":{…},"ms"}`
(DXIL and reflection as base64; the layout's constants with name, HLSL type, offset and count, its textures with name, slot and
whether sampled, and its block's size). A compile past 10 s is ended and the worker killed; a worker that stops while it compiles
is started again after a backoff doubling from 1 s to 30 s and given the source again; a source that stops it three times fails
with words and its kind is quarantined (`compilerStopped`). The compilers — glslang and SPIRV-Tools (shaderc), SPIRV-Cross, DXC
and its validator — load only in the worker, each held to its SHA-256; the engine's process holds none.

## Papers note

(For the integration pass.)

- `docs/EFFECTS.md`: user ISF on the Direct3D 12 road (the Vulkan target: GLSL 4.50, glslang's relaxed rules, `spirv-val`,
  SPIRV-Cross to Shader Model 6.0 HLSL with the default block at b0, the built-ins at b1 and image i at t i and s i, DXC to
  validated, signed DXIL) in the shader worker; the quarantine and FX RELEASE; FX CHECK's stages. GL user effects compile as
  before, on the compile lane.
- `docs/ARCHITECTURE.md` §5: the HLSL library under `src/PlayMap.Render.D3D12/Shaders` (one file per family, `Common.hlsli`'s
  conventions: `PmClip` turns GL's clip space into Direct3D's so targets stay top row first, one root signature with the binding
  block read through `ResourceDescriptorHeap`), `programs.json`, the layout table and the build step that writes each program's
  `Layout.hlsli`, embeds the library and, on Windows, its DXIL.
- Seams this package needs from the spine and from wp3-rhi's files (it works behind its own until they land): an
  `AddShaderParts` hook in `EngineParts.Standard` so the exe composes `ShadersExtension` (the worker, the home-kept quarantine, FX
  CHECK's stages, the preflight check); one line each in `FxPass` and `RenderHost.Fx` handing the node's quarantine to
  `FxChainRunner.Quarantine`; and `fx.library.quarantined` (wp2-fx's `EngineCore.Fx.cs`) reading the quarantine's count.
- What CI proves: DXC's Linux build emits validated, signed DXIL (M6 answered yes), so every program compiles and is held to the
  table by reflection on both jobs; the build embeds DXIL on Windows only, where the DXC executable is packaged. Pixel parity of
  the HLSL with the GLSL is wp3-d3d12-core's; nothing here was drawn on a device.
