# PlayMap — the agent brief

Read this before any change. The papers people read are `README.md`, `docs/`; this is for whoever works the tree.

## What this is

PlayMap (repo: Mapper) is a media player / render node of the Patterns ecosystem (Patterns, Stagewatch) and a 3D
projection-mapping suite (structured-light scanning, LiDAR import, pose solving, 3D blending, pen-tool masks). Today it
is one portable .NET 10 exe with two processes: the **desk** (`PlayMap`, Avalonia 12) and the **engine** (`PlayMap
engine`, the node: project, playback, FFmpeg decode, OpenGL render, outputs, Show Bus, scan). `docs/ARCHITECTURE.md` is
the design; read it first. Round 3 (`docs/round3/PLAN.md`) is the plan of record for where it is going.

## The direction (the owner's)

- **An installed Windows-native show node.** Windows 11 from build 26100, every edition, Home included (a fallback for
  each edition-only feature, said in preflight). A per-machine MSI from the WiX toolset built from its MS-RL source in CI:
  no WiX paid service, no official WiX binary, the EULA gesture never passed. Unsigned for now, named `-unsigned`, with
  `SHA256SUMS`. A supervising service starts the engine in the show user's session; the engine never runs as
  administrator or in session 0. Every build carries its version (`Version.props`, `BuildInfo`).
- **Direct3D 12 behind an RHI** (`PlayMap.Core/Gpu`), deep Windows, CPU and GPU features (RTX, Radeon) where measured.
  OpenGL stays the reference backend with unchanged pixels until parity is measured; AUTO stays OpenGL until a lab record
  says otherwise. No Vulkan.
- **Frame lock, no tearing, ultra-low latency.** Hardware lock where the hardware allows (NVIDIA's present barrier on
  Direct3D 12, the WGL swap group on OpenGL), the honest software lock elsewhere; nothing is called locked without its
  own evidence. Gaming and medical lessons: nothing allocated, compiled or waited on during the show; the air observed
  by an independent reader; every hazard traced to a fault test; servers and stores bounded and flood-tested.
- **The desk.** Simple guided workflows (two projectors blended or stacked on a flat screen; a 2D film mapped onto a 3D
  surface so it reads undistorted from the audience, blendable to more projectors; an object mapped with one or two
  projectors) beside advanced panes and the typed console. Every command and feature is reachable from the menu bar and
  right-click menus, never only by typing. Panels dock, float and collapse; colour marks status, identity and workflow
  stage, never the only cue. The four-click quad stays when a person clicks away; every corner, pin and handle is
  dragged, typed and nudged by one pixel. Each screen says what it shows. One pattern, media or generator spans
  several outputs as one canvas. Every mapping step can be done by hand, and every automatic result is a layer the
  hand's edits sit over. Capture cards pass through to the air with one frame in flight.
- **AI integration throughout.** Guides are data the assistant reads; it proposes lines through the same gate as
  everyone, and a person approves. A still of a view goes to an off-machine model only when a person turns it on.
- **No lab this round.** Every lab line keeps its script and pass mark and is reported "not run"; nothing claims a
  hardware fact it has not measured.

## The standing brief

Stability, resilience, efficiency, user experience, modularity, performance across machines, rapid show
construction. Every selection change or view update is instant. Simplicity hides power: few workspaces, the
right-click menu on everything, in step with the desk's buttons and the wire's verbs. AI integration at every step
(STATE, CONTEXT, MCP). Anything done is followed down every chain it affects — Core, engine, wire, STATE, context,
MCP, desk, menus and right-click menus, papers.

## The doctrine (Patterns')

- **Attempts are not facts.** Sent → accepted → applied → observed. A verb that would change nothing is refused
  with the reason. Tests assert fields and published state, never words alone.
- **Content is a pure function of show time.** No node integrates frame deltas; frame = f(leader time at vblank).
- **One writer.** The engine's `ProjectStore` is the only writer of the project; readers take immutable snapshots.
- **The desk has no private API.** It uses the verbs Patterns and an AI use.
- **Fail closed on the wire.** Unknown words are refused; secrets ride in no line, no journal row, no hello.
- **Measured, then governed.** No governor without a measured cause and a deterministic test.

## Constraints that bite

- **One `dotnet build` at a time** in a tree. A `--no-build` test run is safe beside edits.
- **The analyzers are fences** (`.editorconfig`, `BannedSymbols.txt`, inherited from Patterns) and apply to tests:
  no empty catch, no dead stores, no `System.Random` (use `Pcg32`), culture-invariant parsing/formatting,
  `Thread.Sleep`/`Environment.TickCount`/`DateTime.Now`/`GC.Collect`/`Console.Write`/`Task.Wait` banned in `src/`.
  Structs implement equality (use `record struct`). Dispose what you own.
- **Comments state the invariant**, not the history. No model or tool identifiers in anything pushed.
- **Never** skip, disable or quarantine a test to get green; never rewrite pushed history.
- Module rules: Core references only the runtime; edges reference Core and their own native library; only App
  references Avalonia; `PlayMap.Render.D3D12` references Core and TerraFX, never `PlayMap.Render`, so the two backends
  meet only in Core's contracts. Windows-only types carry `[SupportedOSPlatform("windows")]`.
- **Round 3's ownership**: a package edits only the files `docs/round3/PLAN.md` §4 gives it and its hook word's stubs; a
  missing seam goes through the spine amendment lane (§5.7). Every new verb is a catalogue row; every new STATE block has
  its fixture written by a test and a card descriptor.
- **Native libraries the user installs** (NDI, NVIDIA's Aftermath, vendor SDKs) load at run time by absolute path, never
  shipped and never found by a bare-name search. FFmpeg ships only as the LGPL build's replaceable shared libraries. No
  GPL code is copied.

## The loop

```
dotnet build PlayMap.sln -c Debug -v q
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
# GL tests need a display: Xvfb :99 & export DISPLAY=:99 (Mesa llvmpipe is enough)
# The Render suite's ES 3.00 leg: PLAYMAP_GL_DIALECT=es300
# FFmpeg tests need FFmpeg 9.0 shared libraries: PLAYMAP_FFMPEG_DIR=/path/to/lib
# Generated files (STATE fixtures, the Companion module's fixtures): regenerate with PLAYMAP_WRITE_FIXTURES=1
```

CI runs both jobs on every push: Linux (with the ES 3.00 leg) and Windows on `windows-2025`, where Direct3D 12 tests
(`[WindowsFact]`, trait `Backend=D3D12`) run on WARP with the Agility SDK as a leg of their own and cannot skip
(`PLAYMAP_D3D12_REQUIRED=1`). Windows-only acceptance is read from that job.

## Where things live

| What | Where |
|---|---|
| The design | `docs/ARCHITECTURE.md` |
| Where it is going: round 3's plan, designs and research | `docs/round3/PLAN.md`, `docs/round3/design/`, `docs/round3/research/` |
| The wire, STATE, the verbs | `docs/WIRE.md`, `src/PlayMap.Core/Wire/VerbCatalog.cs` (the one table) |
| The Show Bus node (Patterns/Stagewatch interop) | `docs/SHOWBUS-NODE.md` |
| Mapping and scanning | `docs/MAPPING.md` |
| Playback, sync, media distribution | `docs/PLAYBACK.md` |
| The desk | `docs/DESK.md`, `docs/UX.md` |
| Research behind the choices | `docs/RESEARCH.md` |
| What is built, what is next, the lab's list | `docs/ROADMAP.md` |
| The owner's calls and their answers, open fixes in detail | `docs/round2/CARRYOVER.md`, `docs/round3/PLAN.md` §9 |

## House words

**the air** — what the audience sees; **a lane** — a single-writer path; **a seam** — an interface where a platform
or heavy implementation is swapped; **STATE** — the node's JSON; **the context** — Show Bus §6; **a surface** — a
mappable mesh face set with content; **pins** — 3D↔2D calibration pairs; **the smart scan** — the camera image
seen from the projector's point of view; **the RHI** — the render hardware interface in `Core/Gpu` every backend
implements; **a lab line** — a check only real hardware runs (`docs/round3/PLAN.md` §7).
