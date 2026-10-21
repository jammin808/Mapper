# PlayMap — architecture

PlayMap is two things in one portable exe:

1. **A player / render node.** Standalone, it plays anything FFmpeg decodes (H.264/HEVC/AV1/VP9/ProRes/DNxHR/
   NotchLC…), the HAP family block-compressed on the GPU, stills, image sequences, network streams, generators (test
   patterns, 2D and raymarched 3D fractals, a GPU particle field, reactive scenes) and live inputs over NDI and OMT,
   through chains of ISF effects, from any number of independent playlists, a playlist chasing timecode if asked; it sends
   any output, canvas or source back out over NDI and OMT; in a sync group it follows one leader's show clock, transports
   and air, so every node draws the frame for the same show instant; and it makes HAP versions of clips it decodes on the
   CPU in the background. On the Show Bus it is a node of the Patterns ecosystem: Patterns (and Stagewatch, controllers,
   and an AI through MCP) can see everything it is doing and drive everything it can do.
2. **A 3D projection-mapping suite.** Scan an object, a room or a building with structured light (a capture camera, or
   the scan simulator) and/or put a LiDAR or photogrammetry model on the stage; get projector poses, a mesh and surfaces
   in minutes; click an object to make it a surface; play media through the warped 3D mesh with automatic blends;
   fine-tune with pins, Bézier pen masks and warp lattices that stay attached to surfaces when you rescan. On a plain
   wall, a pixel quad from four clicks; on a flat screen, one camera measures each projector's warp, blend and white; and a
   calibrated camera finds the projector that moved and re-solves it alone.

The UI hides the power: three workspaces (**Play**, **Map**, **Scan**), a show strip, and **Outputs**, **Inputs**,
**Health** and **Network** drawers; right-click menus on everything, in step with the desk's buttons and the wire's verbs.
The desk as built is docs/DESK.md.

Where the design is going is round 3's plan (docs/round3/PLAN.md). PlayMap is becoming an installed, Windows-native show node on
every edition of Windows 11 from build 26100, Home included: a per-machine installer, and a supervising service that starts the
engine in the show user's session (§1). Its renderer, which draws through a rendering hardware interface (RHI) with OpenGL behind
it, is moving to Direct3D 12 behind that interface, OpenGL kept as the reference (§5). Its outputs are to lock in hardware where
the hardware allows it and say how far apart they are everywhere else (§4). Its desk is to open on guided workflows beside
advanced panes and the typed console, every command reachable from the menu bar, and from an object's right-click menu where it
acts on one, its panes docking, floating and collapsing (docs/round3/design/desk-workflows.md). Until then the node is the
portable exe on OpenGL this paper describes.

## 1. Processes — one exe, four roles

```
PlayMap.exe                 the desk (Avalonia). Starts and watches its engine; or attaches to any node's engine
                            on the network (operate a rack render node from a laptop).
PlayMap.exe engine          the node: project, playback, decode, render, outputs, network, scan. Owns its main
                            thread (output windows + GL context live there). Headless: `engine headless`.
PlayMap.exe optimise-worker the node's HAP encoder for optimise on import. The engine starts and watches it; it lowers
                            its own CPU priority, makes its own GL context (hidden, or EGL surfaceless with no
                            display) and speaks one JSON object a line on its standard input and output.
PlayMap.exe shader-worker   the compilers' process: a user's ISF effect to validated, signed DXIL for the Direct3D 12
                            stages of FX CHECK and FX IMPORT (docs/round3/design/shaders.md). The engine starts it
                            with the first source and watches it; it runs below normal priority, loads the pinned
                            compilers the engine never loads, speaks one JSON object a line and ends when its input
                            closes.
```

Round 3 adds a service above the engine and the fourth role beside it (docs/round3/design/installer.md,
docs/round3/design/shaders.md). On an installed node `PlayMap.Supervisor`, a LocalSystem service, is to start
`PlayMap.exe engine --supervised` in the show user's session, restart it, and stop one whose shown frames have stopped while
HEALTH says they should move. The engine never runs as administrator and never in session 0. User ISF effects compile for the
Direct3D 12 road in `PlayMap.exe shader-worker`, so no compiler loads into the engine; no device draws that DXIL yet
(wp3-shaders-2). Today the supervisor's exe does nothing and says so (§2), and the user effects the frame draws compile inside the
engine, on a shared second context (§5).

- A desk that faults or closes never blanks a projector: the engine keeps playing. The desk restarts and
  re-attaches; the engine restarts (watchdog) and puts back the recovery record (project, playlists' transports,
  outputs) and "seeks to now" — trivial, because every frame is a pure function of show time (§4).
- Once its doors are open the engine the supervisor started (`engine --supervised`) writes its pid record (`engine.pid`: its
  process, the line wire's port, when it started) into the supervisor's folder, `%ProgramData%\PlayMap\supervisor`, where that
  folder exists — no installer makes it yet (round 3's installer is to, docs/round3/design/installer.md §2), and the engine never
  does — never over a record that names another engine still running (that is a fault in STATE, with the other engine's
  pid), and takes it back on a clean stop while it still names this process. Any other run (the desk's engine, a portable exe, a test) writes none. It holds what a supervisor needs to adopt a running engine and an
  installer's armed check needs to ask it on loopback, and no secret.
- A node says it is up (its journal's row, its log's "is up: wire …" line) once its doors are open and it has written STATE and
  CONTEXT once, on its start's own thread: no reader's line pays their first build, which runs every package's writer for the
  first time (a client's first STATE took 250 to 920 ms on a Debug build on Linux; asked as the node says it is up it takes 5 to
  10 ms there). A writer that faults then leaves the node up, with the words in its journal before the row that says so.
- A node that stops takes no more lines from the moment it begins to: a line from any source (the wire, the web,
  OSC, MIDI and DMX bindings, the assistant, an approval) is refused with the words, so no source that closes late
  starts work on a part already closed, and an edit a line taken before still has waiting for the control lane is refused with
  them when its turn comes; the node's own lines (a panic's shutters and effects) still run. Then the
  doors and parts close in reverse, the recovery record is written and the journal flushed.
- A worker that stops never touches the air: the engine starts it again after a backoff (1 s, doubling to 30 s) and its
  job starts again from its first frame; a clip that stops it three times fails with the words. It ends when its input
  closes, and after two minutes with nothing to do; every wait on it is bounded, so a killed worker that a driver holds is
  journalled and left, and the node's stop goes on without it (docs/OPTIMISE.md).
  The shader worker is started again on the same backoff and given its source again, and holds one source at a time: a source it
  does not answer (past the compile's 10 s, past an FX CHECK's bound, or abandoned) ends that worker and counts nothing, and a
  source that stops it three times fails with the words and is not sent again until it changes, a memory the host keeps for the
  node's run that writes no quarantine (docs/EFFECTS.md).
- **The desk has no private API.** It drives the engine through the same verbs Patterns, Stagewatch and an AI
  use (docs/WIRE.md): the wire on loopback for commands, the state stream for facts, shared memory for preview
  pictures (the ring file each view's `VIEW … SHOW` reply and STATE `machine.views` name, read only when it is that node's ring for the
  view in the desk's own preview folder). Anything the desk can do, a peer can do; anything a peer sees, the desk sees. Before a press the desk asks the
  same Core rules the engine's door decides by (the show policy for its role from `WHOAMI`, the locks in STATE.show), so a
  control the node would refuse is greyed with the node's own words; the engine still decides. A package's STATE shows on
  the desk through its card descriptor (`tests/fixtures/state/<pkg>.cards.json`, built into the desk), so a package adds data,
  not desk code.

## 2. Modules

| Module | References | Holds |
|---|---|---|
| `PlayMap.Core` | the runtime only | numerics (SVD, LM, Pcg32), time (mono clock, offset estimator, network clock, timecode: LTC, MTC and the chase), the model (immutable `Project` tree, `ProjectStore` single writer with undo), playback logic (timeline anchor, clip transport, playlist cursor, frame selection), the wire grammar and verb catalogue, Show Bus models (hello, context, pairing, receipts), the show policy, locks and panic maths, preflight, the generator catalogue, its looks and the particle schedule, geometry (mesh, ray caster, camera model, the survey readers — OBJ, PLY, glTF, XYZ, chunked LAS and a managed E57 reader — rebasing in double into the site frame, quadric decimation and voxel thinning), the site (`SiteFrame`, the stage move, control-point files, the site check, geodesy — Krüger's transverse Mercator, UTM, ECEF and ENU, the EPSG UTM families — the facade guide and the light plan), **mapping maths** (warp mesh, pixel quads, Bézier masks, blend solver with stacks as one projector, pose solver with priors as residual rows, the long-throw solve and its uncertainty, a stack's residual lattice and colour match), **scan maths** (structured light encode/decode, calibration, triangulation, point clouds, segmentation, registration, the multi-view solve), align's fits, colour match and verify pass, the calibration score, the camera re-solve's dot check, motion attribution and re-seating, the stage simulator, the ISF parser and translator (GLSL 3.30, ES 3.00, and GLSL 4.50 for Vulkan's rules, the shader worker's first stage), the effect quarantine's record (`ShaderQuarantine`: the kinds a node will not draw, kept in its home), audio analysis, the beat clock, the modulation bus and plan, OSC/MIDI/DMX and binding maths, the frame-lock machine, the render hardware interface (`Core/Gpu`: the device, resources, programs and their layouts, pipelines, the frame's command list, fences, the upload, read-back, timer and import contracts every backend implements, the platform and presenter seams, the program table as data), the presentation words (`Core/Presentation`: the vblank clock, the present ledger and clock, the latency plan, the present path, which any backend's presenter feeds), the machine's records and policy (`Core/Machine`: the node's identity and capability probes, thread roles and `ThreadClaims` with each deadline thread's timer, the machine platform's seam with what a platform reads back and the null platform that claims nothing, the show doors, the machine's settings and locked driver set, MACHINE SET's words and each default with its cause, CPU-set placement, the edition, servicing and GPU vendor tables, the machine preflight's rows, the text Windows hands back parsed, and the engine's pid record), the capture grammar (`CaptureUri`), LED layouts, patching and packets, fleet health and the Eye's graph, optimise's planner, recipe, governor, HAP packer (PlayMap's own Snappy compressor) and version choice, contracts (`IVideoSource`, `WarpMeshGeometry`, `BlendMap`, `IHapBlockEncoder`) |
| `PlayMap.Render` | Core, Silk.NET.OpenGL/Windowing, SkiaSharp | the GPU platform (OpenGL 3.3 core or ES 3.00; GLFW windows on GLX or EGL, or EGL surfaceless; on Windows the Direct3D 11 flip model through ANGLE, loaded at run time) with a presenter per output feeding Core's vblank clock and present ledger, each output's delay ring, the frame-lock tiers; the decoder-surface importers (dmabuf EGLImages, D3D11 textures through ANGLE) and the context's upload arena; the GL backend (`Render/Gl`: `GlBackend`, the GL device behind Core's `IGpuDevice` with one immediate command list, fences over GL syncs, the pixel-pack and unpack rings, timestamp queries, user programs linked on the compile lane and transients rented from a resource pool; the GL platforms, the importers, the upload arena and the HAP encoder named here live there too), render graph (every frame's drawing goes through the device's frame list — the render engine, the canvases and playlist mixes, the generators, the video conversions, the projector pass, the effect chains, the output stage, the edit overlay, LED sampling, the send stage and the HAP pass: a pass over each target, a program and one `DrawState` per draw, its constants set through the program's layout, by name or, in five passes, by generated id (`ProgramIds`), the windows' pictures recorded into the list and presented after its submit; the GPU times of outputs, generators and effect slots are spans between two timestamps in the list, read frames later, every span a frame asks for measured (on an ES context whose timer extension counts no timestamps, one elapsed-time query per output, the spans inside it unmeasured; a context with no timer queries measures none); the warm-up makes every pipeline the passes list through the device before the first frame, within `RenderEngine.WarmUpBound` (20 s; past it the render host starts with a journalled fault and the first draws make what the warm-up had not, counted in `IPipelineCache.RenderThreadMakes`), and a pipeline the device cannot make fails the render host's start with the device's words; no source under `src/` outside `src/PlayMap.Render/Gl` names a GL type or a type of Silk.NET's GL side (in Render, of any of Silk.NET; the sound's OpenAL binding is Media's), save the platform factory naming the GL platforms' maker — `RhiFenceTests` and `EngineFenceTests` hold the line by a scan of the sources and project files and a walk of the Render and Engine assemblies' signatures and IL; the lab's bench, `bench/r3-present`, drives the GL platforms directly and is outside the line), shader library, generators (Skia-drawn test patterns → textures; shader fractals, raymarched fractals, the GPU particle field, reactive, effects), video upload (YUV → RGB on the GPU; HAP blocks through the `hap` pass), ISF effect chains (each pass a user program the device makes through `CreateProgramAsync` — on GL compiled and linked on the compile lane, its uniforms and samplers read back into its layout — and lets go of once the last frame that drew it has completed; a slot whose kind the node's quarantine holds draws bypassed, its input unchanged and nothing of it drawn), the projector pass (stage meshes from the projector's pose, then pixel quads), warp, blend, black level, colour, align's measured stage, the edit overlay, LED sampling, the send stage (network video packed on the GPU, read back through a fenced ring) and the unpack of packed network layouts, preview read-back, the GPU HAP encoder (`GlHapEncoder`: fragment programs that write one texel per 4×4 block) |
| `PlayMap.Media` | Core, FFmpeg.AutoGen 9.0, Silk.NET.OpenAL, NAudio.Wasapi, `PlayMap.ThirdParty.Omt` (libomtnet, vendored under `src/ThirdParty`, outside the analyzers' fences) | probe, decode on the decode-path ladder (VAAPI mapped to DRM PRIME on the renderer's render node, D3D11VA on the renderer's own device; copy-back from any other hardware device; software), the HAP parser and its Snappy stage on a worker pool writing into the renderer's upload arena, frame-accurate seek, preroll, gapless loop, stills and sequences, capture devices (avdevice), sound input (avdevice; WASAPI with loopback on Windows), sound out (`AudioOut`: OpenAL Soft, slaved to a timeline), content hashing, the HAP transcode (decode, encode through a frame encoder it is handed, mux with the source's audio stream-copied, read back), OMT and NDI receivers and senders (libomtnet and libvmx vendored; NDI's runtime loaded from the user's install), Spout's protocol over a kernel seam and its own Direct3D 11 device (Windows) |
| `PlayMap.Net` | Core, ASP.NET Core (shared framework), ModelContextProtocol.AspNetCore | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; and over `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS on 9940 (TLS sniffed), MCP at `/mcp`, pairing and TLS pinning, fine clock sync and the members' vblank reports (UDP 9942), the sync group's anchor feed and its follower, the sync board, OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) sockets, the LED sender (Art-Net, sACN, DDP), the mDNS responder, the fleet monitor and the `/health` and `/fleet` feeds, media transfer, the Patterns and Stagewatch links, the §11 approvals pull |
| `PlayMap.Devices` | Core | projector control (PJLink and the makers' own protocols; docs/PROJECTORS.md), a read of a projector's own geometry and edge blend (`Native`), the machine's MIDI ports (ALSA sequencer, WinMM) |
| `PlayMap.Vision` | Core, ONNX Runtime | segmentation (classical, and an optional ONNX model) for surfaces and regions on the smart scan; depth previs, pin suggestions and drift watch as library code |
| `PlayMap.Assistant` | Core, a provider's client library | plain words → verbs, the proposals list, secret redaction |
| `PlayMap.Render.D3D12` | Core, TerraFX.Interop.Windows; Microsoft.Direct3D.DXC at build time only (every asset excluded and private: the build runs its `dxc.exe` on Windows, and nothing of it reaches the output or a project that references this one) | the Direct3D 12 backend, Windows only (docs/round3/design/d3d12.md): one folder each for the device, queues, memory, descriptors, commands, pipelines, shaders, the exporter, the platform, raw interop, the flip presenters, video, user effects, bindless records, the present barrier and the rebuild path, each holding a placeholder but `Shaders/`, which holds the HLSL library of PlayMap's programs and the build step that writes their headers from Core's program table and embeds the library and, on Windows, its DXIL (§5); nothing renders through it yet, and neither the engine nor the desk references it. It never references `PlayMap.Render`: the two backends meet only in Core's contracts |
| `PlayMap.Platform.Windows` | Core | what a show node claims from Windows, Windows only (docs/PLATFORM.md, docs/round3/design/platform.md): `WindowsMachinePlatform` behind Core's `IMachinePlatform` — the process's claims and each thread's by its role, every one read back, the high-resolution deadline timer, keep-awake as a power request, the node's identity and capabilities and the machine's facts for preflight — by Win32 calls (`LibraryImport`) and its own DXGI and NVAPI declarations, each structure pinned by a layout golden, NVAPI loaded from the system directory by its absolute path; every type is Windows only. The engine composes it on Windows, and Core's null machine platform, which claims nothing and says so, elsewhere |
| `PlayMap.Supervisor` | Core | the supervising service's exe (docs/round3/design/installer.md §5): it does nothing yet, says so on its standard error and exits 1 whatever its command line |
| `PlayMap.Engine` | Core, Render, Media, Net, Devices, Vision, Assistant, Platform.Windows (not Render.D3D12 or Supervisor); the shader worker's compiler natives (Silk.NET.Shaderc.Native, Silk.NET.SPIRV.Cross.Native; DXC's compiler alone, `dxcompiler.dll` from Microsoft.Direct3D.DXC, downloaded and never referenced, on Windows and `libdxcompiler.so` from DXC's pinned Linux release through `PLAYMAP_DXC_DIR` on Linux, with its licence and DXC's third-party notices in `licenses/DXC/` beside it and no `dxil.dll` or `libdxil.so`), loaded only in the worker | composition root: the command router (verbs → edits/actions) and the packages' hooks, the show gate, the blind draft and its commit, the air's sound, playback lanes, timecode chase, the sync part (the role kept, the fan-out, a member's leader lines), control bindings, the video I/O hub (each live input received once and lent to its consumers, its tally and loss policy, each send's sender and the plan the render thread packs), output windows, preview shared memory, scan runner, survey import (read, thinned and decimated on its own thread, applied on the lane as one edit), the site's verbs, align runs and the double-correction guard, the calibration ledger and its re-solves, the optimise service (its queue, governor and worker process), the shader worker (the same exe as `PlayMap shader-worker`, started with the first source: a user's ISF effect to DXIL for FX CHECK's Direct3D 12 stages, its compilers each held to its SHA-256) and the effect quarantine kept in the node's home (`playmap.quarantine.json`, lifted by `FX RELEASE`), the machine part (`Machine/`: the machine platform composed for this machine — Windows' on Windows, Core's null platform elsewhere — its process claims made as it composes, each thread's role claimed through it once the host has pointed the thread claims at it, before it starts the render host or any part (the render thread's once the engine is attached), every saved `MACHINE SET` handed to it before anything acts on it, keep-awake held while the project has an enabled display output and `AWAKE` is on, the machine's settings and `MACHINE LOCK`'s driver set in the node's settings, the machine preflight, the GC's show mode as an arm hook), journal, recovery record, watchdog heartbeat |
| `PlayMap.App` | Core, Engine (for `engine` mode only), Avalonia 12 | the desk: the workspaces, the show strip, the attention bar, the drawers, the generic STATE cards and the generated menu entries, composed of modules over one spine (docs/DESK.md); the engine link, the demo engine (`FakeEngineLink`); the desk's own preferences (Simple or Pro, the look, each unfinished quad) in the user's desk.json, never in the project |

Core references only the runtime; each edge references Core and its own native library, and never another edge except
through Core's contracts (Render does not know FFmpeg; Media does not know GL). Only App references Avalonia.

## 3. The document and the air

- `Project` is an immutable record tree (`System.Collections.Immutable`). An edit is `Func<Project, Project>`
  applied by `ProjectStore` — **one writer**, in the engine. The new root shares every untouched branch; undo is a
  list of roots; drags coalesce into one undo step (edits of one field within 750 ms on the node's clock). Each step
  is numbered (a number is never another step's), with who made it (the line's session, else the node) and when on the
  show's wall clock; `Steps` lists them newest first, and `UndoTo(n)` takes step n and every newer one back as one action:
  one root, one revision, each step on the redo list as `Undo` puts it there. Readers (render thread, network, state
  writer) take the current root through a volatile reference and never lock. The file form is schema 2: a schema-1
  file is read by its rules (`ProjectJson.Read`), and the open names what they moved. An output's blend writes its γ twice:
  `gamma` is the γ the fades are drawn through, what every earlier build reads, so a backup or a build that predates schema 2
  draws the same fades from the file; `gammaOverride` beside it is the model's `Blend.Gamma` (0: the measured γ, else 2.2). A
  line's `blend.gamma` sets the override, as `blend.gammaOverride` does.
- Runtime state (transports, links, outputs' health, availability) is not in the document: each lane publishes
  its own immutable snapshot; STATE composes them.
- Generic edits (`GET/SET/ADD/REMOVE/MOVE` by id and dotted field path) are how the desk edits anything and why an
  AI can too; specific verbs exist where an edit is a verb (VIDEO PLAY, SCAN START, PROJECTOR SOLVE).
- Every line passes one door before its handler: pairing, then the show's gate. One table (`ShowPolicy`) decides each line
  from every transport by the caller's role (operator, remote, observer, assistant, approved, peer, system) and whether the
  show is armed (`SHOW ARM`, kept across a restart); while armed, an assistant's line that changes the show waits for a
  person on the node's proposal list.
  Locks are one guard in the store, checked as an edit applies and kept outside undo; live actions (the master, a panic,
  a lock) are not undo steps (docs/SHOW.md).
- A blind draft is a second store seeded with the live root, written on the same lane: the gate sends its owner's edits
  there, off the air. Its commit is published as a (root, leader instant) pair in the engine's snapshot, so the render
  thread draws the new root's content from the boundary's frame on, and the lane makes it the document, as one undo step, on
  the tick that reaches it (the stage's meshes and warps follow then) (docs/SHOW.md).

## 4. Time and sync — content is a pure function of show time

- Every node has a monotonic clock (`MonoClock`, ns). A sync group has a **leader**; followers estimate the
  leader's clock with an NTP-style exchange on UDP 9942 every 250 ms (min-RTT filtering + drift regression,
  `ClockOffsetEstimator`) and read it through `NetworkClock`, lock-free. A correction of at most 1 ms is slewed at
  200 ppm, so leader time stays continuous; a larger one steps, on the engine's control lane, which moves the playlists'
  anchors, scheduled starts, the air's fades and a waiting blind commit by the same Δ in the same action (a group playlist
  already on the leader's time line stays), so nothing lands early or late. A leader quiet for 3 s is held on its last
  estimate. Show Bus links use the same estimator on `PING t1` lines (ms).
- A playlist's transport is a `TimelineAnchor` (media time M at leader time L, rate, running). Commands carry an
  optional start instant (`AT +200`). A leader pushes each transport change of a group playlist on its anchor feed
  (`SUBSCRIBE ANCHORS`), and each follower takes it through its own `PLAYLIST ANCHOR` and its own gate, once its clock reads
  the leader's time line: every node in the group holds the same anchor.
- The group's air rides the same feed: a leader's `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER` (but a line with `HERE`,
  or a panic naming outputs) go to every member, which runs each through its own gate as a peer, pinned to the leader's
  instant so fades start on the same frames; `SHOW ARM|DISARM ALL` and `PREFLIGHT ALL` take the same path. Each member
  answers each line with `SYNC RECEIPT`, and the leader journals the receipts. Members report each output's vblank to the
  leader (PMVB on UDP 9942), whose board measures phase, slip and rate against a reference (docs/SYNC.md).
- A hot backup follows its primary's group as a member and takes every other playlist's anchor and the air lines the group
  does not carry through the primary's mirror, each at the primary's leader instant, so it draws the same frame for the same
  instant (docs/FLEET.md). `FAILOVER` commits the next **epoch** (one past every epoch heard), keeps it before the node serves,
  and freezes the clock's mapping into the node's own time line (`NetworkClock.Promote`): show time continues with no step and
  the node leads the group. A primary that hears its backup serve its show under a higher epoch **fences** itself: its outputs
  held black, its clients' edits refused, its clock following the new primary. Only the node that has the show sends
  LED data, video sends and its sound beside its outputs; a primary that starts with backups sends none of them until it has heard
  them, so an old primary that comes back after a takeover fences itself having sent nothing.
- The render loop predicts its next vblank, maps it to leader time plus the node's air latency (the largest input lag of its
  enabled outputs, raised to a sync group's; a host-wide display-latency setting when no output names one) and asks
  `FrameSelector` which frame to show; a faster output presents the picture drawn whole frames earlier from its delay ring, so
  every display lights the same picture together; while an output is frame-locked (`SYNC APPLY`) the instant is the
  lock anchor's time of the pacer's next count, the same integers on every node that holds the anchor. No node integrates a
  frame delta, so none drifts; a dropped render frame heals itself. Without genlock the honest ceiling is ±1 refresh between
  machines. This build's NVAPI tier (compile-only) reads a sync board and joins no output to a swap group, so every output
  presents free on its own vblank, measured once its clock settles (docs/OUTPUT.md). Round 3 brings machines to the same vblank where the hardware
  locks: NVIDIA's present barrier on Direct3D 12 and the `WGL_NV_swap_group` swap group on OpenGL, on a professional GPU with
  an RTX PRO Sync board, each output locked only by its own evidence. Everywhere else an output is to present free and say so:
  "software lock: same frame, phase +x ms" (docs/round3/PLAN.md D5, docs/round3/design/present.md).
- A generator counts its time from an origin on the show clock (stamped at `GENERATOR NEW`, moved by `GENERATOR RETIME`),
  or from its item's clip time while a playlist routes it; the particle field steps on a fixed 1/120 s grid from one
  particle life back, so a late joiner draws the same picture (docs/CONTENT.md). An effect's time is the leader's show time.
- A chased playlist's media time is a line fitted to the code's arrivals, `media(L) = In + (tc(L − delay) − S) · rate`,
  never integrated (docs/TIMECODE.md).
- The beat clock is kept as anchors on leader time, as a transport is; sound levels are live readings. The modulation bus reads
  both at the frame's leader instant and moves a drawn copy of each bound number of a layer, a surface or an effect slot, while
  the project keeps its base; a generator's renderer evaluates its own bindings through the same bus, for the kinds that read
  their numbers every frame (docs/AUDIO.md).
- An item that comes in with a Fade or a Dip of T overlaps the item it follows: its window is the outgoing item's
  last T before the switch (cut to what both can give), the incoming item is anchored to start in it (at its
  opening for a fade, its middle for a dip) and prepared ahead, and the renderer mixes the two pictures in linear
  light (`Transitions`). The window is a function of the two anchors, so every node mixes the same pictures at the
  same instant. A GO, NEXT or PREV taken by hand into such an item opens the window at the GO's instant and mixes the same
  way; a playlist in a sync group cuts at the GO, since the anchor its followers take carries no window.
- The on-air item's sound (the focused playlist's) plays through one `AudioOut` slaved to the item's anchor: its rate nudged
  by at most ±0.5 % to stay within 10 ms, resynced hard on a jump; it plays rates 0.5× to 2× and is silent at any other, as
  when paused, with `STATE.show.sound.problem` saying why. Its gain is the air's sound gain (master × the darkest panic, from
  the same fades as the picture) × the playlist's volume, read at each service step's leader instant. Each step reads the
  output's clock (the node's own) once, right after a device's position, and takes the item's position, the gain and the null
  output's playhead at that one instant; the null output's playhead is a function of its clock, and a step consumes its whole
  frames and carries the part of a frame after them (docs/SHOW.md). Sound comes in: one capture per input feeds the analyser and
  the LTC reader.
- `Media/Audio/AudioOut` plays through the OpenAL Soft that ships beside PlayMap, found in the app's own native folders before
  the system's (`OpenAlLibrary`); where no OpenAL loads, its device output is the null one and `AudioOut.Status` says why,
  which STATE carries as `show.sound.status`.
- A thread that waits for an instant — the headless render loop's pacer, the LED sender, each audio output's feeder — waits on
  the machine platform's deadline timer, made on that thread (`ThreadClaims.CreateTimer`) and disposed at its end: on the machine's
  monotonic clock the platform's (on Windows under `MACHINE SET TIMER HIGH`, the default, a high-resolution waitable timer that wakes
  near its due time without raising the system's timer resolution), on any other clock the runtime's own wait on that clock. The
  p99 lateness of their last 1 000 waits is in STATE (`machine.platform.timer`; docs/PLATFORM.md).

## 5. Rendering

Per frame, on the engine's render thread (one device, all outputs; on GL its one context). Blackout wins before any pass:

1. **Sources**: upload the frame each visible video source has for its media time (planes → textures,
   YUV→RGB in the shader with the frame's matrix/range/transfer; a hardware decoder's surface imported where the renderer can
   take it — a VAAPI dmabuf sampled in place, a D3D11 slice copied once through ANGLE — with its padding cropped; HAP blocks as
   BC textures through the `hap` pass, uploaded from the context's upload arena the decode thread wrote them into, where the GL
   maps one);
   render generators whose parameters or time changed; Stagewatch timer views; a live input's newest frame from its one
   receiver (UYVY, UYVA, P216 and PA16 unpacked by `video.unpack`), or what its loss policy shows while its feed is lost
   (docs/VIDEO.md). A playlist item's picture goes through its effect chain while its playlist shows it, each side of a
   transition before the mix.
2. **Canvases**: each layer's picture through its effect chain, composited into canvas render targets
   (normal/add/multiply/screen, opacity).
3. **Surfaces**: a chained surface's content is made once a frame at one size (the largest projector raster) through
   its chain, and every projector samples it.
4. **Per output**, by mode:
   - *Projector*: render the stage's surfaces from the projector's pose (intrinsics → off-axis projection,
     `CameraModel.GlMatrices`), each surface textured by its content (UV, canvas region, or projection from a
     viewpoint), surface masks applied in UV space, then that projector's pixel quads; lens distortion as a
     pre-distortion pass.
   - *Canvas*: the canvas rectangle. *Source*: one source full-frame.
   Then the output's own effect chain, and the overlay's pre-warp layer.
5. **Output stage**: align's measured warp grid first, then the 2D warp (a stack member's measured lattice first, then the
   corner pin + Catmull-Rom/Bézier lattice, tessellated by `WarpMeshBuilder`), output masks (feathered Bézier paths), blend
   map × signal + black-level pedestal (linear light, through the inverse gamma — or align's measured blend and pedestal
   through the output's measured γ), colour trim, dither, and last the air's gain (master × panic × the degrade gains: the
   hot backup's fence composed over the stacks' MATCH gains, so a fenced node is black), so 0 is black. Then the identify card and the edit
   overlay (outlines, handles, lattices, cursors, through the output's warp; nothing while armed or on a black air).
6. **LEDs and sends**: once every output is drawn, each LED fixture samples its canvas, output, surface or source in linear
   light and its DMX slots are made on the GPU, read back behind fences, never waited on (docs/LED.md); then each send due on
   the leader-time grid taps an output's final picture, a canvas or a source (what the air does to the show applied to a
   canvas or source), is packed on the GPU and read back behind fences into its sender, never waited on (docs/VIDEO.md).
7. **Present** through the output's presenter — a faster display's picture through its delay ring (a gap in its frames or a
   scan's raw frame starts the ring again) — and its vblank clock and present ledger record which refresh showed each picture
   (missed vblanks, held and late presents; docs/OUTPUT.md).
8. **Previews**: requested views (desk viewports, output thumbnails) are rendered to targets and read back
   asynchronously (PBO ring) into the preview shared memory. An editor view of the blind draft is drawn after the air's
   frame by a second renderer that has no outputs, so it never moves the air's present.

A warm frame with no modulated layer, surface or effect slot allocates nothing (tests hold it for static frames, a test
pattern, a projector at another raster, effect chains and an unchanged measured set). The render thread reads the air's
levels and the cursor board lock-free; effect compiles (on a shared second context), measured-file reads, frame-lock board
polls and machine sampling run off it.

Backends: a platform seam (`IGpuPlatform`, one `IOutputPresenter` per output) over OpenGL 3.3 core or OpenGL ES 3.00
through Silk.NET — GLFW windows (on GLX, or on EGL by `decode.glApi`), or EGL surfaceless with no window system (testable
headless on Mesa) — and, on Windows, the Direct3D 11 flip model (`PRESENT PATH DXGI`, experimental; AUTO is GL): ANGLE on
PlayMap's own device, waitable swap chains, `GetFrameStatistics` as vblank truth, and a fuse that runs OpenGL after a start that
never reached its first frames. Decoders share that device under its one recursive lock (`GpuDeviceLock`): the render loop holds
it around each frame, taken within 4 ms (past that the frame draws on the device's own multithread protection and is counted),
and lets go of it while a window waits for the vertical blank; a decoder or a texture sharer holds it only around its own
immediate-context work (docs/OUTPUT.md, docs/DECODE.md).

Every pass draws through the render hardware interface (Core's `IGpuDevice` and the frame's `ICommandList`), never through an API:
the frame code, the passes and the generators name no GL type, and the GL backend (`Render/Gl`) implements the contracts with its
one immediate list. Five passes (the particle step, the output stage, the quad every canvas layer is drawn with, the projector
pass's surfaces and the raymarcher) set their constants by the ids generated with the program table (`ProgramIds`: a constant's
place in its program's layout), with no look-up by name; the others set theirs by name through the program's layout. Each pass
lists the pipelines it can draw on a device (`FramePipelines`: every program it draws, in every state, into every target shape it
draws into there), and the warm-up makes them all before the first frame within `RenderEngine.WarmUpBound` (20 s), so the scripted
show's draws make none on the render thread; past the bound the render host starts with a journalled fault and the first draws make
what the warm-up had not, counted (`IPipelineCache.RenderThreadMakes`). A pipeline the device cannot make fails the start with the
device's words. The Render suite's GPU tests do the same: each but the GL backend's own runs on a leg of the backend fixture
(`BackendFixture`, one device on a thread of its own per backend the job has — GL today, a later backend's leg added to
`BackendLegs.Makers`), draws through the device the leg hands it and reads back through `Pixels.Read(IGpuDevice, RenderTarget)`;
only the GL backend's own tests, listed by name in `RhiFenceTests` (and the engine suite's in `EngineFenceTests`), name a GL type,
and some of them open GL contexts of their own. Every pixel golden is unedited and holds within its tolerance on llvmpipe in both
dialects. With no lab, L20 (the GL backend behind the interface on NVIDIA, AMD and Intel Windows drivers) is not run: the GL
backend runs on llvmpipe and, on the Windows job, Mesa WGL only.

Where the backends are going (docs/round3/PLAN.md D1–D3; docs/round3/design/rhi.md, d3d12.md, present.md): Direct3D 12 is to be
the primary backend behind the same interface, each output presenting through its own flip-model swap chain. OpenGL stays the
reference backend, its pixels unchanged, until the pixel and timing evidence says the two agree; then it retires, and ANGLE and
the Direct3D 11 present path with it. AUTO stays OpenGL until a vendor tier has its lab parity record. Vulkan is not carried.
Today `PlayMap.Render.D3D12` holds placeholders and the HLSL library (§2, below), and `PRESENT PATH D3D12` answers that it is not
available on this node yet.

The HLSL library (docs/round3/design/shaders.md) sits under `src/PlayMap.Render.D3D12/Shaders`: PlayMap's 40 programs once more in
HLSL, one file per family, listed in `programs.json` (each program's file and entries), with `Common.hlsli`'s conventions —
positions in the render hardware interface's clip space and every vertex entry ending with `PmClip`, which is
`ClipSpace.ToDirect3D`, so a draw lands on the GL backend's rows and depths; a `float4x4` constant read as `mul(v, M)` and the
normal matrix's `float3x3` as `mul(M, v)`; the present drawing the picture unflipped into a back buffer that runs top row first;
one root signature for every program, its constants at `b0` and the binding block at `b1` read through `ResourceDescriptorHeap`.
The build step writes each program's header (`Layout.hlsli`: its block and texture slots) from the render hardware interface's
program table (`src/PlayMap.Core/Gpu/Programs/programs.layout.json`, read as data, the one table both backends and the HLSL build
read) and embeds the library and, on Windows, where DXC's executable runs, its validated, signed DXIL with each entry's reflection
(compiled again when a source, the table or `programs.json` changes; each entry's PDB stays in `obj/`, and the Windows job keeps
them). DXC's Linux build emits validated, signed DXIL too (M6), so every program compiles on both jobs, and `ShaderLayoutTests`
hold each one's reflection to its `ProgramLayout` in the table there — the same programs on both sides, the block at `b0` with the
table's constants, offsets, counts and size and its matrices `row_major`, the table's texture slots sampled exactly where the
table says, and the vertex entry's input signature as the input layout the table's inputs make — while `HlslConventionTests` hold
the clip conversion, the matrices' order and the vertex inputs in the source. No device draws the library yet: pixel parity with
the GLSL is wp3-d3d12-core's. User effects reach DXIL in the shader worker (§1, docs/EFFECTS.md), and no device draws theirs yet
either; on every frame a slot whose kind the node's quarantine holds draws bypassed, its input unchanged, until `FX RELEASE`.

## 6. Mapping pipeline

- **Scan (structured light)**: projector shows Gray code (+ inverses) and multi-frequency phase shift with an
  in-pattern frame tag; the camera's frames are matched to patterns by tag (so webcam latency and dropped frames
  do not matter); latency is auto-measured with a white flash. Decode gives, per camera pixel, the projector
  column/row with a confidence. Outputs: the projector-space "smart scan" image (no calibration needed — enough
  for a single projector on an object), and dense correspondences for calibration.
- **Calibrate**: self-calibration from dense correspondences (no checkerboard): essential matrix → relative
  pose → bundle adjustment of focal lengths and poses; scale from one known distance or a LiDAR model.
  Triangulate → organised point cloud → projector-grid mesh.
- **In the engine** (`ScanRunner`, behind SCAN START): per projector, its device link readies it (shutter open, its
  own geometry off) and takes a shutter-closed black frame; the patterns go up through the render host's scan
  display, and each pattern keeps the first camera frame that arrived at or after its present plus the measured
  latency and that the settle detector accepts. All projectors are then solved at once in the camera's frame and
  carried into the world by the camera rig's pose (bridged into the stage's model instead when it has one and the
  camera is calibrated); a calibrated camera keeps its focal length, because a camera and projectors aimed at one
  object have nearly meeting optical axes and two views cannot then tell focal length from depth. A run `INTO` a scan is
  one more view of it, solved together with the scan's other views (Many views, below). SCAN APPLY puts
  the scan on the stage as one undoable edit; SCAN SURFACE makes a surface of a click on the smart scan. The camera
  "sim" runs the same pipeline on the scan simulator — demo mode and the tests scan with no hardware. The node
  composes it through the host's hooks (`EngineParts.Ops.cs` → `OpsParts`): the runner is the scan backend over the
  render host's display, and projector control, the assistant (its queue the host's MCP approvals) and the runner's
  binding are extensions; the runner feeds the camera and scan preview views on the render host's `PreviewViews`.
- **Survey and model import** (`SURVEY IMPORT`): OBJ, PLY, glTF, LAS, E57 and XYZ/PTS read on the import's own thread,
  rebased in double into the site frame, meshes decimated to a triangle budget and clouds thinned by voxel, then applied on
  the lane as one edit (docs/FACADE.md, docs/MAPPING.md). Core registers by three picked points (Umeyama) then
  point-to-plane ICP, with no verb on it yet; or bridge with structured light: decoded camera pixels ray-cast into the
  model as placed on the stage give thousands of 2D↔3D pairs per projector → `PoseSolver`.
- **Surfaces**: RANSAC planes and click-to-select region growing (depth- and normal-aware) on the scan; an
  optional ONNX segmenter (SAM-class) refines masks. A surface is a mesh face set with UVs; masks and warps are
  stored in surface UV space so a rescan keeps the work.
- **Pins**: 3D↔2D pairs per projector, a pin's point clicked on the model or bound to a surveyed control point; DLT (≥ 6)
  or PnP with locked shift/focal + LM refinement; RANSAC flags a bad pick; per-pin residuals shown live while dragging.
  A projector's lens and position priors join the refinement as residual rows, and at long throw the zoom is locked from
  the lens when the pins cannot carry it — the projector keeps who put its focal lock there, so only the lens's is asked again
  (docs/MAPPING.md). The solve and its score (one uncertainty analysis) run off the control lane; its result lands on it.
- **Blend**: per output pixel, which projectors see the point (visibility by ray casting), weights by distance to
  the projector's frame edge × incidence angle × distance falloff, normalised to one in light, through the gamma;
  black-level pedestal to the deepest overlap's floor. A stack (projectors lighting one picture) blends as one projector:
  its master's geometry, its members' least edge factor and summed lumens, every member taking the group's share.
- **Stacks** (`STACK NEW|ALIGN|MATCH|FAILOVER`): each member fitted to its master's picture, as the master's output draws it,
  from a scan's decodes through one camera place, as a residual lattice drawn as the first layer of its output's warp (and kept
  with the poses it was fitted to: a member that moves against its master is no longer aligned; a fit that would fold the
  picture is refused), its white matched with gains ≤ 1, both through the calibration ledger; a member lost dims the rest of the
  stack's blend group to the light the stack has left through a degrade gain read at each frame's leader instant
  (docs/FACADE.md, docs/MAPPING.md).
- **The quad on the wall**: `LIGHT WALL` puts an output full screen with a nominal projector; `SURFACE QUAD` makes a
  pixel quad from four wall clicks, taken back through the output's warp, and its handles move by verb with one undo
  step per burst (docs/CALIBRATION.md).
- **Score and ledger**: every calibration result carries one explainable score (terms, grade, fix line). Results are
  immutable snapshots recorded in the same edit as the change; the first applied is the fallback. `CALIBRATION RECALL`
  puts a kept one back exactly, `FALLBACK` the fallback, `ACCEPT` applies a staged one; `CALIBRATION RESOLVE` re-solves
  only the named projectors, applying within the gate or staging the result (docs/CALIBRATION.md).
- **Re-solving the projector that moved**: from its pins as they stand, or from a calibrated camera (`CAMERA`). The camera
  looks at every projector black, then at each named one's jittered dots alone (`DotCheck`), matched against where the
  project says they land on the stage; `MotionAttribution` judges each projector unmoved, moved or unseen, and when every one
  seen moved together, the camera moved and nothing is re-solved. Each mover's pose alone is solved again from its dots (`PoseSolver`, its optics kept) and its
  dots looked at once more for its wall error. In the one edit that applies it, its pins take the new pose's pixels and its
  pixel quads are re-seated (`Reseat2D`: out along the old pose's ray onto the stage, back in through the new one); an
  automatic blend is then solved again off the lane. A simulated camera is its own display (`StageSimulator`); a real one
  shows the dots through align's display shooter, every other projector output held black.
- **Camera auto-align of flat screens** (`ALIGN`): off the lane, against the simulator or a real display and camera —
  footprints, photometry and structured light per output, a joint lens-plus-homography fit that refuses a screen that is
  not flat, a measured warp grid and blend per output, and with `COLOR WHITE` each output's white and level matched
  (`ColourMatch`); every run ends with a verify pass (`AlignVerify`) whose seams its score reads, and keeps what the camera
  saw (`AlignCaptureFile`), so `ONLY` scans one output again onto the canvas the others show. `ALIGN ACCEPT` applies a run as
  one edit through the ledger. A projector whose own geometry or edge blend is on while its output carries a measured
  correction is read live (`Devices/Native`, Panasonic) and named in the context and preflight with the line that turns it
  off (docs/ALIGN.md).
- **Many views** (`MultiViewScan`, behind `SCAN START … INTO`, `SCAN SOLVE` and `SCAN DROP`): each run of a scan is a view —
  the camera and its optics as they were and each projector's decode. Every view of every projector is solved together by Core's
  multi-view solve, anchored on the projectors' pixels, off the lane and one solve per scan at a time; the result is carried onto
  the scan's frame by its anchor view's place (or the held projectors' stage poses) and by any move of the site frame since, and
  gives a dense depth map per projector, the merged mesh and regions joined across projectors. The record lands through the lane
  as one edit. The views, decodes (`CorrespondenceArchive`), the solve and the depth maps sit in the scan's folder
  (`ScanViewArchive`) and are read back after a restart; what does not read is listed, never guessed. `ScanSuggestions` turns
  the measured regions into lines queued on the node's own approval list — none runs before a person's `APPROVE` — and no
  solve or view lands on a scan while its suggestions are made. `FolderCamera` is a cued camera beside the phone: a tethered camera or a person shooting into a
  folder, its stills read through FFmpeg (`Media/Stills`) (docs/SCAN-MULTIVIEW.md, docs/wire/multiscan.md).

## 7. Network — a node of the Show Bus (docs/SHOWBUS-NODE.md)

- **Hello** every 2 s on UDP 9928, `t:"playmap"`, caps `video video.clock video.transport video.seek video.end
  state context receipts clock mcp playlist media mapping scan eye show`, top-level `web`/`wire` for older readers.
- **Wire** on TCP 9941 in Patterns' grammar: `STATE` then `NONCE` on connect, `HELLO`, `AUTH-PROOF`, `@id`
  receipts, `PING t1` → `OK PONG t1 t2 t3`, `CONTEXT`, the VIDEO verbs with Patterns' meaning, and the rest of
  the vocabulary. STATE carries Patterns' `video` row (positionMs, lengthMs, rate, seq…) so **Stagewatch's VT link
  drives PlayMap by pointing it at PlayMap's host and port**. Patterns' Interactive "Lines" devices can drive it
  too: their `KEY VALUE` fact lines (BLACKOUT 1, LIVE 0…) carry no request id and are read as facts, never answered; a
  line with a request id is a request, read as a verb and always answered (`OK @a1` from a verb that otherwise answers
  nothing).
- **HTTP/WS/HTTPS** on 9940: `/api/state`, `/api/context`, `/api/project`, `/api/cmd`, `/api/verbs`,
  `/api/media/<hash>` (range transfers between nodes), `/api/thumb`, `/ws` (state frames with v/seq/id; with the
  subprotocol `playmap.lines.v1`, the line wire itself), `/tls`, `/tls.cer`, `/mcp` (Model Context Protocol:
  resources for state/context/project, tools for every verb, high-risk tools gated), and outside `/api` the open
  reads `/health` and `/fleet`. A foreign `Origin` is refused before anything runs, and a request let in without the
  token must name the node by one of its own names (docs/WIRE.md).
- **Control and discovery**: OSC on UDP 9943, Art-Net on 6454 (one socket), sACN on 5568, MIDI from the machine's
  ports — each move a wire line through the same router and show gate (docs/CONTROL.md); LED output leaves as Art-Net
  (through the same socket), sACN or DDP, latched with the projection (docs/LED.md); the node announces itself as
  `_playmap._tcp` over mDNS; the fleet monitor subscribes to each PlayMap peer's `/health`, over TLS pinned by the hello
  when the peer offers it (docs/FLEET.md).
- **Links**: follows a Patterns desk as a wire client (its clip clock, blackout); follows a Stagewatch over `/ws`
  as a screen (`cid`, `page=/playmap`), renders its timer as a source, handles `beat.live:false`, posts its
  context, collects approved proposals (§11).
- **Media sync**: library items are identified by content hash; a node missing a file pulls it in 8 MiB chunks
  from a peer that has it, resumable, each chunk served and written in 1 MiB blocks; a chunk's body silent for 10 s on
  the node's clock fails that try with words, while the peer's list and each answer wait the HTTP client's own timeout,
  and a fetch journals at most 4 of its pulls' rows and one that counts the rest, a quarter of the context's 20 recent rows.
  `MEDIA FETCH` runs one of two pulls.
  With the fleet's part, which the exe composes, it pulls with the peer's proof over `/mirror/media` (docs/FLEET.md),
  saying each file's end in the journal and writing no transfer row. Without it, the node's own pull reads each peer's
  `/api/media` list once a fetch, is throttled while a show runs, and says each pull's stage, bytes, peer and last
  failed try in STATE's transfer row, which the desk's Network drawer and PLAY show. Carrying the fleet's pulls to that
  row and to the show's throttle is an open fix (docs/ROADMAP.md). Each node publishes readiness (present, verified,
  decodable, first frame ready).
- **Media versions**: an item may carry versions (`MediaVersion`, `mv…`): HAP files the optimise worker made from its
  source, each hashed into this node's media store and recorded on the item as one edit. At each open the media backend
  asks `VersionChoice` which file to play — under `use: auto` a version this GPU samples whose file is here, the best
  measured first, else the source; `MEDIA USE` pins the source or a version — so a change reaches the air at the item's
  next open (docs/OPTIMISE.md).

## 8. Reliability rules (inherited from Patterns, enforced by the analyzers and tests)

Attempts are not facts (receipts say accepted/applied and the effect); fail closed on the wire; one writer per
lane; immutable snapshots for readers; a file that holds state for the node's next start (its settings, the recovery record, the
project, the optimise queue, the present fuse, keys and credentials, a run's or a scan's records) is written beside its place,
reaches the disk, and is renamed into place with the rename flushed (`DurableFile`; a rename that landed but whose folder could not
be flushed after says so, `NotFlushedException`: the file is in place, only its durability in doubt); every reader of such a file
opens it shared for write and delete (`DurableFile.OpenRead`) and Windows replaces it with a POSIX-semantics rename, so no reader
makes a replace fail (a program that opens it sharing no delete still can, and the writer says so), while a record it only reports
(the journal, preflight reports) and a file it makes again (the NDI runtime's config, a pull's chunk map) are not flushed;
CPU-bound loops (`Parallel.For`, every one under `src/` held to it by a source scan) and the solves, scan decodes and simulators
of the scan, align, resolve, blend, mapping, site and stack work, the stage's automatic blend, thumbnails'
conversion and encode, `MEDIA INSPECT`'s and the decode measure's HAP decodes, the scan views' pictures and ONNX inference with its
pixel work (`Compute.RunAsync`, and `Compute.Hop` for an async method's CPU part) on Core's compute threads (`Compute`), never the
shared thread pool the lanes, sockets and timers need — every product file's pool work is held to a reviewed count by a source scan
(`ComputeTests`); what the air waits for is never queued behind a solve that holds every compute thread: a still's decode stays on
the pool, and the stage's table builder runs on a thread of its own, its loops on it and the compute threads that are free; pools
and rings that skip rather than wait; no
allocation on a warm frame (a frame whose layers, surfaces or effect slots are modulated still builds its drawn project afresh;
every suite measures allocation through one helper, `tests/Shared/Allocations.cs`, whose windows begin after a gen0 collection,
in Debug and Release); a faulting
generator draws an error card and is disabled until its settings change; blackout is checked before any content code;
FlashGuard limits flashes to three a second.
