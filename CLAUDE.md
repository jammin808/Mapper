# PlayMap — the agent brief

Read this before any change. The papers people read are `README.md`, `docs/`; this is for whoever works the tree.

## What this is

PlayMap (repo: Mapper) is a media player / render node of the Patterns ecosystem (Patterns, Stagewatch) and a 3D
projection-mapping suite (structured-light scanning, LiDAR import, pose solving, 3D blending, pen-tool masks). Today it
is one portable .NET 10 exe that runs as the **desk** (`PlayMap`, Avalonia 12) and the **engine** (`PlayMap engine`,
the node: project, playback, FFmpeg decode, rendering through the RHI with the GL backend behind it, outputs, Show Bus,
scan); the engine starts its optimise worker and, with the first effect source, its shader worker as processes of their
own (`docs/ARCHITECTURE.md` §1). On an installed node the supervising service (`PlayMap.Supervisor`, an exe of its own)
starts the engine in the show user's session and the desk attaches to it (`docs/INSTALL.md`). `docs/ARCHITECTURE.md` is the
design; read it first. Round 3 (`docs/round3/PLAN.md`) is the plan of record for where it is going.

## The direction (the owner's)

- **An installed Windows-native show node.** Windows 11 from build 26100, every edition, Home included (a fallback for
  each edition-only feature, said in preflight). A per-machine installer built with Inno Setup this round, every
  machine-level action done by the supervisor's own commands; an MSI is revisited next round. No WiX paid service or
  official WiX binary, and WiX's EULA gesture is never passed. Unsigned for now, named `-unsigned`, with `SHA256SUMS`.
  The Agility SDK's Direct3D 12 runtime ships in the installer under a licence page that carries Microsoft's own licence
  for `D3D12Core.dll` verbatim, answering its requirement that distributors and external end users "agree to terms that
  protect it and Microsoft at least as much as this agreement"; PlayMap's own terms are the owner's to name, and none are
  named yet. The owner accepts Microsoft's distribution terms, including indemnifying Microsoft (round 3 §9 decisions 3 and
  23). A supervising service starts the engine in the show user's session; the engine never runs as administrator or in
  session 0. Every build carries its version (`Version.props`, `BuildInfo`).
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
  everyone, which decides by the line's risk, the operator's settings and Show Mode what waits for a person's approval;
  a person's Check and a secret are never the assistant's. A still of a view goes to an off-machine model only when a
  person turns it on.
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
  references Avalonia; `PlayMap.Render.D3D12` references Core and TerraFX, and DXC's package (`Microsoft.Direct3D.DXC`)
  only at build time (`ExcludeAssets="all"`, `PrivateAssets="all"`: the HLSL build runs its `dxc.exe` on Windows, found
  through `GeneratePathProperty`, and nothing of it reaches the output or a project that references this one), never
  `PlayMap.Render`, so the two backends meet only in Core's contracts. Windows-only types carry
  `[SupportedOSPlatform("windows")]`.
- **The GL line**: no source under `src/` outside `src/PlayMap.Render/Gl` names a GL type or a type of Silk.NET's GL
  side (in Render, of any of Silk.NET) but the platform factory, which names the GL platforms' maker (`RhiFenceTests`,
  `EngineFenceTests` hold it); the lab's bench (`bench/r3-present`) drives the GL platforms directly and is outside the
  line. A Render GPU test draws through the device its `BackendFixture` leg hands it (only the GL backend's own tests,
  listed by name in those fences, name a GL type or open a GL context of their own).
- **Round 3's ownership**: a package edits only the files `docs/round3/PLAN.md` §4 gives it and its hook word's stubs; a
  missing seam goes through the spine amendment lane (§5.7). Every new verb is a catalogue row; every new STATE block has
  its fixture written by a test and a card descriptor.
- **Threads and deadlines.** The render, decode, audio, control and LED threads claim their roles from the machine platform in
  one line as their work starts (`using var claim = ThreadClaims.Claim(ThreadRole.Decode);`; the render thread once the engine is
  attached, after the host points `ThreadClaims` at the platform; the LTC reader, fed on the capture thread, through a
  `DeliveryClaim`). A deadline thread (the headless pacer, the LED sender, each audio feeder) waits on the timer
  `ThreadClaims.CreateTimer(clock)` makes on it, disposed at its end: on the machine's clock the platform's (on Windows the
  high-resolution waitable timer under `TIMER HIGH`), on any other clock the runtime's wait on that clock. `MACHINE SET`'s MMCSS and
  CPUSETS reach a thread at its next claim, TIMER at its next timer (`docs/PLATFORM.md`).
- **The desk's lights and its own state.** Every light the desk draws carries its shape beside its colour, and a light in a
  template its word too (`StatusMark`, `StatusShapes`: ✓ ok, △ warn, ✕ fault, ■ armed, ○ off, i information, a grey that is
  information lit `info`); ok, warn and fault each 3:1 or more against every surface a light is drawn on in every theme; red only in
  the air's styles and a fault's light; nothing repeats faster than 2 Hz (`StatusRuleTests`). The desk's own preferences and each
  unfinished quad live in the user's `desk.json`, never in the project. Map anything outlines a control only where the node's
  `BIND LEARN` learns its line (Core's `TargetLine`; `DeskVerbRuleTests`).
- **Native libraries the user installs** (NDI, NVIDIA's Aftermath, vendor SDKs) are never shipped and load at run time.
  A new loader finds its library by absolute path only, never by a bare-name search, which on Windows takes in the
  current directory and `PATH` (Aftermath: round 3 §9 decision 7; NDI's and libvmx's loaders still end with one, an open
  fix in `docs/ROADMAP.md`). FFmpeg ships only as the LGPL build's replaceable shared libraries. No GPL code is copied.
  DXC ships only as its LLVM-licensed compiler, dxcompiler (`dxcompiler.dll` from Microsoft.Direct3D.DXC, which the
  engine downloads and never references; `libdxcompiler.so` from DXC's pinned Linux release), whose built-in validator
  signs DXIL; no `dxil.dll` or `libdxil.so` is used or shipped (round 3 §9 decision 24). Every package the Windows publish
  carries, and by its file name every native library no package lists, is named in `THIRD-PARTY-NOTICES.md`, each package's licence
  and notice files under `licenses/<id>/`: `installer/notices.ps1` stops the Windows and installer jobs otherwise, and
  `LicenceTests` hold the portable folder and the installer's payload to it.

## The loop

```
dotnet build PlayMap.sln -c Debug -v q
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
# GL tests need a display: Xvfb :99 & export DISPLAY=:99 (Mesa llvmpipe is enough)
# The Render suite's ES 3.00 leg: PLAYMAP_GL_DIALECT=es300
# FFmpeg tests need FFmpeg 9.0 shared libraries: PLAYMAP_FFMPEG_DIR=/path/to/lib
# The shader worker's SPIR-V validator on Windows: PLAYMAP_SPIRV_TOOLS_DIR=<folder with SPIRV-Tools-shared.dll>
#   (the Windows job's pinned download, README's Build); without it every FX CHECK fails at validate
# DXC on Linux, set before the build: PLAYMAP_DXC_DIR=<absolute folder with libdxcompiler.so from the pinned DXC release>
#   (the Linux job's pinned download, README's Build); without it the worker's dxc stage and the HLSL tests say so
# Generated files (STATE fixtures, the Companion module's fixtures): regenerate with PLAYMAP_WRITE_FIXTURES=1
# The installer's tests sit outside PlayMap.sln (installer/tests/PlayMap.Installer.Tests): their life cycle runs elevated on the
#   installer job only; LicenceTests read the folder PLAYMAP_SHIPPED_DIR names
```

CI runs three jobs on every push: Linux (with the ES 3.00 leg); Windows on `windows-2025`, where Direct3D 12 tests
(`[WindowsFact]`, trait `Backend=D3D12`) run on WARP with the Agility SDK as a leg of their own and cannot skip
(`PLAYMAP_D3D12_REQUIRED=1`); and the installer on `windows-2025`, elevated, which builds the unsigned Inno Setup installer and
the one numbered before it and proves install, upgrade, a downgrade refused and uninstall, a hard gate (round 3 §5.4 item 5).
Windows-only acceptance is read from the two Windows jobs.

## Where things live

| What | Where |
|---|---|
| The design | `docs/ARCHITECTURE.md` |
| Where it is going: round 3's plan, designs and research | `docs/round3/PLAN.md`, `docs/round3/design/`, `docs/round3/research/` |
| The render hardware interface | `src/PlayMap.Core/Gpu`, `docs/round3/design/rhi.md`, the contracts as built in `docs/round3/notes/rhi.md` §2 |
| The HLSL library, the shader worker, the effect quarantine | `src/PlayMap.Render.D3D12/Shaders`, `src/PlayMap.Engine/ShaderWorker`, `src/PlayMap.Core/Effects/ShaderQuarantine.cs`, `docs/round3/design/shaders.md`, `docs/wire/shaders.md` |
| The machine platform: what a node claims from Windows, its identity, the driver lock, machine preflight | `src/PlayMap.Core/Machine`, `src/PlayMap.Platform.Windows`, `src/PlayMap.Engine/Machine`, `docs/PLATFORM.md`, `docs/wire/machine.md` |
| The installer, the supervisor, STATE `machine.install` and preflight's `install` section | `installer/`, `src/PlayMap.Supervisor`, `src/PlayMap.Engine/Install`, `docs/INSTALL.md`, `docs/wire/install.md` |
| The wire, STATE, the verbs | `docs/WIRE.md`, `src/PlayMap.Core/Wire/VerbCatalog.cs` (the one table) |
| The Show Bus node (Patterns/Stagewatch interop) | `docs/SHOWBUS-NODE.md` |
| Mapping and scanning | `docs/MAPPING.md` |
| Playback, sync, media distribution | `docs/SYNC.md`, `docs/DECODE.md`, `docs/VIDEO.md`, `docs/OPTIMISE.md`, `docs/FLEET.md` |
| The desk | `docs/DESK.md`, `docs/UX.md` |
| Research behind the choices | `docs/RESEARCH.md` |
| What is built, what is next, the lab's list | `docs/ROADMAP.md` |
| The owner's calls and their answers, open fixes in detail | `docs/round2/CARRYOVER.md`, `docs/round3/PLAN.md` §9 |

## House words

**the air** — what the audience sees; **a lane** — a single-writer path; **a seam** — an interface where a platform
or heavy implementation is swapped; **STATE** — the node's JSON; **the context** — Show Bus §6; **a surface** — a
mappable mesh face set with content; **pins** — 3D↔2D calibration pairs; **the smart scan** — the camera image
seen from the projector's point of view; **the RHI** — the rendering hardware interface in `Core/Gpu` every backend
implements; **a lab line** — a check only real hardware runs (`docs/round3/PLAN.md` §7).
