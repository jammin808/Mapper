# PlayMap

**A playback node and a 3D projection-mapping suite, in one portable exe — and a full member of the Patterns /
Stagewatch show network.**

Today that exe draws through a rendering hardware interface with OpenGL behind it. PlayMap is becoming an installed,
Windows-native show node on every edition of Windows 11, rendering on Direct3D 12, with a desk of guided workflows (**Next**,
below).

PlayMap plays anything FFmpeg can decode, the whole HAP family on the GPU, stills, image sequences, network streams, NDI and
OMT sources, capture devices, test patterns, fractals, particle fields and sound-reactive scenes, through chains of ISF
effects, and sends any of it back out over NDI and OMT. It runs any number of independent playlists, and a playlist can
chase timecode. PlayMap nodes in a sync group follow one leader's show clock, its transports and its air, so each draws the
frame for the same show instant, and the leader measures how close their vblanks are. It maps all of that onto walls,
objects, rooms and buildings: a corner-pinned quad in four clicks, structured-light scans with a camera from as many places
as it takes, click an object to make it a surface, automatic blends across projectors, a camera that measures the warp,
blend and white of projectors on a flat screen, and a camera that finds the projector that was knocked and re-solves it
alone. LED fixtures sample the same pictures. Show Mode, locks, blind edits, a panic and a grand master keep a running show
safe. Patterns, Stagewatch, Companion and an AI (via MCP) can see everything it is doing and drive everything it can do; OSC,
MIDI, Art-Net and sACN controllers drive it through bindings.

```
PlayMap                     the desk: Play · Map · Scan, a show strip, and Outputs, Inputs, Health and Network drawers
PlayMap engine              the node (separate process: a desk that closes never blanks a projector)
PlayMap engine headless     a rack render node driven by Patterns, with no desk at all
```

## What it does

- **Plays.** FFmpeg with hardware decode, every decoder saying the path its frames come by and why not a better one — on Linux
  VAAPI frames sampled in place through the GL's dmabuf imports (an EGL context, `decode.glApi egl`), on Windows D3D11VA on the
  renderer's own Direct3D 11 device with one GPU copy a frame, wherever the renderer can import them; else copied back or
  decoded in software, stepping down and never up while a clip plays (`DECODE STATUS`, `DECODE POLICY`); all seven HAP codecs
  block-compressed on the GPU, HAP R and HAP HDR included, their frames written straight into the renderer's upload arena where
  the GL maps one;
  Patterns' test patterns, 2D and raymarched 3D fractals, a GPU particle field of up to 1 M particles, sound-reactive
  scenes, stings and thirty curated looks. Every generator counts its own time on the show clock, so a late joiner, a backup
  or a chase draws the same picture. A clip that costs this node CPU decode (ProRes, DNxHD and the like, or long-GOP at 1080p
  and above) gets a HAP Q version in the background, HAP Q Alpha when it has alpha — on import under `MEDIA RULE MODE AUTO`,
  or when `MEDIA OPTIMISE` asks — made by PlayMap's own GPU encoder in a worker process; the source stays, and the version
  plays from the item's next open. An item cuts, fades or dips in, at a scheduled switch or on a GO by hand (in a sync group a
  GO cuts). The on-air item's sound plays through the machine's output, slaved to its transport and faded by the master and a
  panic with the picture. A sync group's followers take the leader's show clock — a small correction slewed, a large one
  stepped with every playlist kept in place, the last estimate held while the leader is quiet — and every transport change of
  the group's playlists.
- **Shapes.** Ordered chains of ISF effects on outputs, layers, surfaces and playlist items, a show's own ISF files beside the
  ten built in. A number on a layer, a surface, an effect or a generator can move with the sound, the beat clock or an LFO.
- **Maps.** `LIGHT WALL` and a pixel quad from four clicks on the wall, with the edit overlay drawn through the output's warp;
  structured-light scans with a capture camera, a phone (the node serves the camera page itself, joined by a one-time invite)
  or the simulator; click-to-surface, pins, masks, lattices and 3D blends; one explainable score for every calibration, kept
  in a ledger with a fallback. A projector that moved is re-solved alone: from its pins, or from a calibrated camera's look at
  its dots, which tells a moved projector from a moved camera, checks the new pose on the wall and re-seats its pins and pixel
  quads so each quad lights the spots it lit; an automatic blend is solved again, and no other projector moves. Camera
  auto-align of projectors on a flat screen matches every output's white and level (`COLOR WHITE`), rescans only the outputs
  it is given onto the canvas the others show (`ALIGN RUN … ONLY`), checks every run with a verify pass and scores it
  (`ALIGN SCORE`); a projector whose own geometry or edge blend would correct the picture twice (read live on Panasonic) is
  named, with the line that turns it off.
- **Scans from many places.** A scan takes another view from wherever the camera sees the light (`SCAN START … INTO`), and
  every view of every projector is solved together — re-solved holding the projectors that did not move, without a view that
  disagrees, carried by a move of the site frame — into a depth map per projector, one stage mesh and regions joined across
  the projectors that light them. A scan keeps its views on disk and works after a restart. Its measured regions are
  suggested as surfaces, each a line on the node's own list that a person approves (an assistant asked to name them sees
  numbers only). The camera can be a capture device, a phone, or a folder a tethered camera or a person shoots into.
- **Maps buildings.** A site frame in the survey's coordinate system keeps millimetres millions of metres from its grid's
  origin. Surveyed models and point clouds (OBJ, PLY, glTF, LAS, E57, XYZ) come in off the control lane, decimated or thinned,
  as one undo step; total-station control points check the model and give pins their exact points. At 50–150 m the lens and
  position priors carry what pins cannot: the zoom is locked from the lens when the pins cannot tell it from distance, and every
  solve says how far the picture could be off on the wall. A site is named by its EPSG system (from a LAS's GeoKeys or WKT).
  Projectors stacked on one picture are aligned to their master's picture and colour-matched from one scan, and blended as one
  projector; when a member fails, the rest of the building dims to the light the stack has left. A light plan predicts each
  surface's lux and pixel pitch, and the facade guide names the next step.
- **Drives LEDs.** Fixtures laid on a canvas, an output, a surface or a source sample its picture in linear light every frame
  and leave as Art-Net 4, sACN or DDP, latched with the projection; patching with an overlap check and a bandwidth guard, test
  patterns and locate, all by verb. The rig follows the air: dark under blackout, black and terminated when the node stops,
  and dark where an output shows a scan or alignment pattern.
- **Takes and sends network video.** OMT (vendored, on every node), NDI 6 (through the runtime the user installs) and Spout (on
  Windows) in and out:
  a live input is a library item received once however many surfaces, outputs and previews show it, with its health, tally, a
  backup feed and a loss policy (hold, black or a fallback); a send taps an output's final picture, a canvas or a source, packs it
  on the GPU on the show clock's grid and costs nothing until a receiver connects. Sends follow the air as the outputs do
  (blackout, a freeze, the master, a panic's safe look); an output showing a scan or alignment pattern sends black, and a send
  or a node that stops sends one black frame.
- **Keeps the show safe.** `SHOW ARM` turns a rehearsal into a show and decides every line by who sent it; locks on finished
  work; `PANIC` takes each output to its planned outcome on the show clock, picture and sound, and `PANIC CLEAR` brings it
  back; a grand master; a fence against the double GO; `PREFLIGHT` says what is not ready, each row with the line that fixes
  it. `BLIND ON` stages edits in a draft off the air, seen in its own editor view; `BLIND DIFF` lists them in field words, and
  `BLIND COMMIT` lands them as one undo step at a show boundary: now, at an instant, at the next switch or on the next GO. A
  sync group's leader takes the group with it: its `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER` reach every member, their
  fades running from the leader's instant, each member answering with a receipt, and `SHOW ARM ALL`, `SHOW DISARM ALL` and
  `PREFLIGHT ALL` cover the whole group.
- **Presents, and measures it.** Each output keeps a vblank clock and a ledger of late and missed presents; a per-output
  frame-lock machine for sync boards, whose NVAPI tier is compile-only and locks no output yet, so every output presents free on its
  own vblank, measured once its clock settles (where the lock is going: **Next**, below); OpenGL 3.3 core or OpenGL ES 3.00 (EGL, or surfaceless with no window
  system), and on Windows the Direct3D 11 flip model through ANGLE (`PRESENT PATH DXGI`, experimental; AUTO is OpenGL) with
  frame statistics as vblank truth and a fuse that falls back to OpenGL after a start that never reached its first frames. Each
  output names its display's input lag: the node draws every frame for the instant its light leaves the slowest display (its air
  latency, in STATE), and each faster output holds its picture back whole frames so all of them light the same picture together.
  A sync group's members report each output's vblank to the leader, whose board gives each output's phase, the next slip and one
  verdict, and they join and leave the leader's frame lock as it applies and releases it.
- **Takes control.** OSC, MIDI, Art-Net and sACN bound to wire lines, with curves, ranges, pick-up and feedback; a playlist
  chases LTC, MTC, Art-Net timecode or an internal generator; Companion and other controllers get the full line wire over the
  web port, the show's index, its events and key pictures; the node announces itself over mDNS.
- **Puts the show on a Stream Deck.** The Companion 5.1 module (`integrations/companion-module-playmap`) pairs over the
  node's own line wire by proof, the token never on the network, and builds its buttons from the node's verbs, its
  feedbacks and variables from STATE and its presets from every section of the node's index, so a new package reaches the
  deck without a new module. A risky line's key is held or pressed twice, never fired at a touch, and a key lights because
  the node says so, never because it was pressed.
- **Listens.** One sound input analysed into levels, bands, a spectrum and onsets; a beat clock by tap, by number or by
  detection.
- **Watches the fleet, and stands by.** Each machine's health with lights by evidence, and a board of every PlayMap machine in
  the shape of Patterns' Eye. A hot backup mirrors its primary's show, files, clock and air and draws its frame to the byte;
  `FAILOVER` takes the air over by epoch with no step in show time and switches the projectors' inputs, and the old primary hears
  the epoch and fences itself; only the node with the show drives the LED rig, the network sends and the sound.

Each runs by verbs on every door — the line wire, HTTP, MCP, the controllers' WebSocket and the bindings — and STATE and the
context report it. The desk is one more client of those verbs: a show strip (the Ready light, ARM, PANIC and Blackout, the air's
controls sending every press at once), every control greyed with the node's own words before a press any of its doors would
refuse (pairing, the fleet, the show's gate with the desk's own blind draft, a lock), Lock for the show in every object's menu, a
card for every part of the node's STATE whose buttons take their values from it, Add effect, Modulate, Bind, Optimise and Use
input entries built from the node's own answers, and an Esc ladder that backs out before it panics. A line a Companion key holds
to confirm is held on every desk button that sends it and pressed twice in a menu (the first press arms the entry, a second within
3 s sends the line), except the transport's Stop, the panic banner's Clear and the master, which go at every press. The desk has
views of its own for getting a show ready and keeping it right: a readiness light (the worst of the show's preflight, the fleet
and the sync group, each lit as the node lights it); the blind chip, whose panel shows the draft's diff and commits it now or at
the next item, discards it or draws it in MAP's 3D view; Health ▸ Fleet with each machine's card and FAILOVER, held to confirm;
Health ▸ Preflight with a Fix button on every row; Health ▸ Calibration with each target's score card, a Fix per term and the
history strip's Recall; MAP's quad tool (Q, four clicks, Enter; a half-drawn quad kept as its projector's draft when the person
looks away; a quad's corners dragged, typed in wall pixels and nudged by the arrows, and a pin's pixel typed and nudged) with the
overlay on the wall (O); a first-light guide from LIGHT WALL to the first clip; and the lighting consoles' safety patterns — a red
border on the GO controls while the node's min-GO window after a GO from any client is open, the whole desk in grey with BLIND
while it edits its own blind draft, the Oops history (`UNDO LIST`, `UNDO TO`), the dark room and daylight looks with a desk
brightness and every light's shape beside its colour, each preview's age, a question before closing an armed desk, and Map anything, where
a touch on an outlined control learns its line (docs/DESK.md). The rest runs over the wire, HTTP, MCP, Companion and the bindings,
and shows on the desk as generic cards and generated right-click entries: the effects browser, modulation, tempo, bindings and
learn, timecode, LED placement and patching, optimise jobs, live inputs and sends, the phone scan path, align, the facade guide, the
sync tab, the present path chips, the per-output panic plans, Companion decks, the content library, the machine platform's claims
and driver lock, and the effect quarantine.

**Tested, and what is left for the lab.** Every push builds PlayMap and runs every test on Linux and on Windows (Build, below):
the GPU work on software renderers (Mesa llvmpipe, in desktop GL and in ES 3.00; WARP for the Direct3D 11 path, Spout and
D3D11VA on Windows, and for Direct3D 12 from NuGet beside the Agility SDK), cameras,
projectors and sync boards as simulators and fakes, NDI against a stand-in for its runtime, OMT for real over loopback, and a sync
group's nodes in one process. What only real hardware or a real show proves — the Direct3D 11 path, Spout and hardware decode on
real GPUs, a real NDI runtime and sync board, real projectors, cameras, phones and LED nodes, HAP R and HAP HDR files from other
encoders, surveys from real scanners and total stations, and a group or a backup across machines — is the lab's list in
`docs/ROADMAP.md` ("Lab only"), with the fixes still open. No lab exists this round, so every line on that list is reported not
run, never passed, and nothing claims a hardware fact it has not measured.

**Next:** PlayMap is becoming an installed, Windows-native show node on every edition of Windows 11 from build 26100, Home
included (`docs/round3/PLAN.md`). It installs per machine from an installer built with Inno Setup this round (an MSI is
revisited next round); builds stay unsigned for now, named as unsigned, each with a SHA-256 list, and the installer shows a
licence page for the Direct3D 12 runtime it carries, as Microsoft's terms for shipping that runtime ask. A supervising service
starts the engine in the show user's session, never in session 0 and never needing administrator rights: the engine runs as the
user of the console session — the account `/SHOWUSER=` names (meant to be a standard account), or, on an install that names none,
whoever is signed in at the console — with an administrator's rights only where that account's token is one Windows does not filter
(docs/INSTALL.md). The engine will render
on Direct3D 12 behind the rendering hardware interface it draws through today, and OpenGL stays the reference backend until the
pixel and timing evidence says the two agree. Outputs will lock in hardware where the hardware allows it (NVIDIA's present
barrier on Direct3D 12, its swap group on OpenGL) and run the software lock everywhere else: the same frame, its phase measured.
The desk will open on guided workflows — two projectors blended or stacked on a flat screen, a film mapped onto a wall so it
reads undistorted from the audience, an object mapped with one or two projectors — beside advanced panes and a typed console,
with every command and feature in the menu bar and the right-click menus. Every mapping step will be doable by hand or with the
assistant, and capture cards will pass through to the air. With no lab this round, what only hardware proves stays on the lab's
list (above). Twenty-one packages build the direction in four waves, every decision of the plan's answered in its §9, on the
round's spine, which is built: the catalogue's rows, Core's contracts, the engine's seams, the model's fields, the new projects as
placeholders and the CI legs. Of the packages, the rendering hardware interface is built on it: every frame draws through Core's
`IGpuDevice` with the GL backend behind it, its pixel goldens unedited and holding within their tolerances. So is the shaders
package: PlayMap's programs written once more in HLSL, compiled on CI to validated, signed DXIL and held to the program table by
reflection; a user's ISF effect compiled to DXIL in a worker process of its own, so none of that road's compilers (glslang,
SPIRV-Tools, SPIRV-Cross, DXC) loads into the engine, with each stage of that road in `FX CHECK`'s report; what PlayMap ships and
loads of DXC is its LLVM-licensed compiler alone, dxcompiler, whose built-in validator signs the DXIL, and no `dxil.dll` or
`libdxil.so` is used or shipped (the plan's §9 decision 24); and a quarantine, kept in the node's home, whose kinds' slots draw
bypassed until `FX RELEASE`. No device draws that DXIL yet. The machine platform is built too: on Windows the engine claims what a
real-time process needs (high priority, HighQoS with its timer requests honoured, MMCSS and CPU sets for its render, decode, audio,
timecode, control and LED threads by their roles, a high-resolution timer for every deadline thread, keep-awake), and each claim is
in STATE with what Windows answered and what reading it back shows; the node's identity (its build, the edition and its support
tier, every GPU with its driver) and the driver set its shows run on (`MACHINE LOCK`) are in STATE, and machine preflight says what
it reads of the machine, each row with words. A default is on only where its cause is documented, and none of it is measured on show
hardware yet (`docs/ROADMAP.md`, round 3). The desk's part of the first wave is built as well: the safety patterns and the quad
that stays (above), proven headless on both CI jobs and not yet tried by operators in a dark room (L19 is not run). So is the
installer: a per-machine installer built with Inno Setup, unsigned and named `-unsigned` with its `SHA256SUMS`, whose licence page
carries a section for PlayMap's own terms (none named yet) and Microsoft's licence for `D3D12Core.dll` verbatim, a quiet install
laying nothing down without `/ACCEPTTERMS=yes`; the supervising service, which starts the engine, restarts it after an exit and
stops it on its stop event; every machine-level action done by the supervisor's own commands; an install, upgrade or uninstall
refused while the node answers armed or while a live engine does not answer; and STATE's `machine.install` with preflight's
`install` section — proven elevated on the Windows runner and on no clean machine (L11, L18, L21 and L23's install part are not
run; `docs/INSTALL.md`).

## What it is made of

| Module | What it does |
|---|---|
| `PlayMap.Core` | The immutable project model and its single writer (undo, coalesced drags). Also: the monotonic clock, the leader clock (slewed, stepped on the engine's lane, held while the leader is quiet) and its drift estimate; timeline anchors (frame = a pure function of show time); Patterns' clip semantics; the one verb catalogue; Show Bus hello/context/pairing; the solvers (SVD, Levenberg–Marquardt); mesh, ray caster and camera model. Mapping maths (warp lattices, pixel quads, Bézier masks, 3D blend), scan maths (structured light, calibration, point clouds, the multi-view solve), align's fits and the calibration score sit here too, with the show policy, preflight and the blind draft's diff, the ISF parser, audio analysis, the beat clock and the modulation bus, OSC/MIDI/DMX and binding maths, LTC/MTC and the chase, the frame-lock machine, the generator catalogue and its looks, LED layouts, patching and packets, fleet health, and optimise's planner, rules, governor, HAP packer and version choice. The site frame and the survey readers (rebased in double; chunked LAS, a managed E57 reader), decimation and voxel thinning, control-point files and the site check, and the long-throw solve with its priors sit here too. |
| `PlayMap.Engine` | The node: control lane, command router (every verb), the show gate (Show Mode, locks, panic, master, the GO fence), the blind draft and its commit at a show boundary, preflight, frame-exact playlist lanes with pre-scheduled gapless switches and fades on a GO by hand, the air's sound, timecode chase, control bindings, the video I/O hub (each live input received once and lent to every consumer, each send's sender, the loss policy and tally), the calibration ledger, align runs, the scan runner (a scan's views solved together, kept on disk, its surface suggestions and the folder camera), survey import (read, thinned and decimated on its own thread, applied as one edit), the optimise queue and its worker (the same exe as `PlayMap optimise-worker`, a process of its own the engine starts and watches), the shader worker (the same exe as `PlayMap shader-worker`, started with the first source: a user's ISF effect through glslang, SPIRV-Tools, SPIRV-Cross and DXC to signed DXIL, the compilers pinned by hash and loaded only there) and the effect quarantine kept in the node's home (`FX RELEASE`), the machine part (the machine platform composed for this machine, its process claims made as it composes and each thread's role claimed through it, keep-awake, the machine's settings and driver lock, machine preflight, the GC's show mode while armed), the install part (STATE `machine.install`, read at most every two seconds from the install's registry key, Winlogon's auto-logon user and the supervisor's status file; preflight's `install` section; under `--supervised`, the supervisor's stop event, waited on for the engine's life), STATE and context, journal, recovery record; the sync part: the role kept, the leader's air fanned out to its members, and a member taking the leader's anchors and air through its own verbs (answered with receipts) and its frame lock |
| `PlayMap.Render` | The frame drawn through Core's render hardware interface (`IGpuDevice`, its frame list, one `DrawState` per draw), with the GL backend behind it (`Render/Gl`): one GL context on a platform seam (OpenGL 3.3 core or ES 3.00; GLFW windows, EGL or surfaceless), a presenter per output with its vblank clock and present ledger, the frame-lock tiers; the projector pass through the 3D stage and pixel quads, warp/mask/blend/black level/colour, align's measured warp and blend, the air's gain last; ISF effect chains; the HAP pass; generators (Patterns' test patterns, fractals, the GPU particle field, raymarched fractals, reactive scenes, surface-aware effects); YUV video upload and the unpack of packed network video; the edit overlay; LED sampling; the send stage (network video packed on the GPU and read back without a stall); previews; the GPU HAP encoder |
| `PlayMap.Media` | FFmpeg 9.0: probe, hardware decode, frame-accurate seek, preroll, gapless loop, capture devices, content hashing; OMT (libomtnet and libvmx, vendored) and NDI 6 (the user's runtime, loaded at run time) receivers and senders over a lock-free frame pool; HAP's own parser with its Snappy stage on a worker pool; stills (JPEG, PNG, TIFF at 16 bits) read as grey for a folder camera; the HAP transcode (decode, encode, mux with the source's audio, read back); sound input (libavdevice; WASAPI with loopback on Windows); sound out through OpenAL Soft, slaved to a transport |
| `PlayMap.Net` | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; also on the web port's `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS + MCP (9940) with `/health` and `/fleet`, pairing and TLS pinning, fine clock sync and the members' vblank reports (UDP 9942), the sync group's anchor feed and the leader's sync board, OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) input, the LED sender (Art-Net, sACN, DDP), the mDNS announcement (`_playmap._tcp`), the fleet monitor, media transfer, the Patterns and Stagewatch links |
| `PlayMap.Devices` | Projectors: PJLink and the manufacturers' own protocols (Christie, Panasonic, Barco, Sony, Epson, NEC). Covers power, shutter, lens, health, resetting internal geometry before scans, and reading a Panasonic's own geometry and edge blend while PlayMap corrects its picture. Also the machine's MIDI ports (ALSA sequencer on Linux, WinMM on Windows). |
| `PlayMap.Vision` | Local AI, offline: click-to-surface segmentation and suggested regions on the smart scan, by a classical segmenter or an optional ONNX segmentation model. Monocular depth previs, pin suggestions and drift watch are in the library; no verb reaches them yet. |
| `PlayMap.Assistant` | Plain words → PlayMap verbs, with a hosted or a local model. High-risk verbs always wait for a person on the node's own proposal list. |
| `PlayMap.Render.D3D12` | The Direct3D 12 backend (`docs/round3/design/d3d12.md`), Windows only, on TerraFX's raw bindings: one folder for each part — the device, queues, memory, descriptors, commands, pipelines, shaders, the exporter, the platform, the flip presenters, video, user effects, bindless records, the present barrier and the rebuild path — each a placeholder but the shaders folder, which holds the HLSL library: PlayMap's 40 programs in HLSL with the render hardware interface's conventions, each program's header written by the build from Core's program table, embedded with, on Windows, the DXIL DXC's executable compiles at build time (`docs/round3/design/shaders.md`). Nothing renders through it yet, and neither the engine nor the desk references it; it never references `PlayMap.Render`, so the two backends meet only in Core's contracts |
| `PlayMap.Platform.Windows` | What a show node claims from Windows (`docs/PLATFORM.md`), Windows only, an edge of Core alone: the process's claims and each thread's by its role (priority, HighQoS with its timer requests honoured, MMCSS, CPU sets; the policy's EcoQoS for background pools, which no node thread claims yet), each read back; the high-resolution deadline timer; keep-awake as a power request; the node's identity (the edition and its support tier, every GPU by LUID with its driver and vendor tier) and capabilities; and the machine's facts for preflight, through Win32 calls and its own DXGI and NVAPI declarations. The engine composes it on Windows, and elsewhere Core's null machine platform, which claims nothing and says so |
| `PlayMap.Supervisor` | The supervising service's exe (`docs/INSTALL.md`, `docs/round3/design/installer.md`), on Core: as the `PlayMapSupervisor` service (LocalSystem) it starts `PlayMap.exe engine --supervised` in the show user's console session, never session 0, restarts it after an exit with backoff (1 s doubling to 30 s) and stops trying after five restarts in ten minutes, its freeze rule waiting for HEALTH's shown frames, which no engine reports yet; every stop sets the engine's stop event (made for each start under a name drawn for it, `Global\PlayMap-Engine-Stop-` and 32 random hex digits, and named on the engine's command line), on which the engine stops as any asked stop does, and terminates it 5 s later only if it still runs; its commands — `run`, `status`, `stop`, `resume`, `preinstall-check`, `install`, `uninstall`, `upgrade-stop`, `grant`, `revoke`, `rights`, `version-check`, `previous-build`, `session-probe` — are the installer's armed check and every machine-level action of the install (`docs/INSTALL.md`'s table) |
| `PlayMap.App` | The desk (Avalonia 12): starts and watches this machine's engine — on a supervised machine (`Supervised` under `HKLM\SOFTWARE\PlayMap`) it starts none and attaches to the engine the supervisor keeps — or attaches to a node on the network; three workspaces, a show strip, four drawers, a generic card for every package's STATE (the packages' card descriptors built in), right-click menus on everything with entries built from the node's own answers; edits only through the verbs every peer uses, each control asking the node's doors before the press (pairing, the fleet's, the Core show policy, the locks) and greying with the node's own words; its own preferences (Simple or Pro, the look, each unfinished quad) in the user's desk.json, never in the show |

## Talking to Patterns and Stagewatch

See `docs/SHOWBUS-NODE.md`. With no change to either app:

- Stagewatch's VT countdown and video buttons drive PlayMap — point its Patterns link at PlayMap's port 9941.
- Patterns' Interactive area drives PlayMap as a *Lines* device, and PlayMap follows Patterns' blackout.
- Any AI on the bus reads PlayMap's context; a line it proposes waits for a person on PlayMap's own proposal list
  (forwarding proposals to Stagewatch's approval desk is off).

PlayMap's context is ordered for the twelve things Patterns' Eye draws of an app, and its fleet board (`FLEET`) answers in the
Eye's own field names. Patterns draws PlayMap lit once it takes three small changes on its side, prepared as a patch with
tests (`docs/FLEET.md`); until then it draws PlayMap as a grey app heard on the network.

## Papers

- `docs/ARCHITECTURE.md` — the design
- `docs/WIRE.md` — lines, STATE, pushes, HTTP; `docs/wire/` — each area's verbs, replies and STATE keys
- `docs/SHOWBUS-NODE.md` — the ecosystem
- `docs/SHOW.md` — Show Mode, locks, panic, the master, blind edit, sound on the air, the GO fence and preflight
- `docs/SYNC.md` — the sync group: one show clock, anchors, the group's air and the measured vblank board
- `docs/OUTPUT.md` — presenting, vblank truth and the frame lock
- `docs/DECODE.md` — the HAP family on the GPU
- `docs/OPTIMISE.md` — optimise on import: HAP versions made in the background
- `docs/CONTROL.md` — OSC, MIDI, Art-Net and sACN input, and bindings
- `docs/TIMECODE.md` — timecode chase
- `docs/COMPANION.md` — the node's side of Companion and other controllers
- `docs/EFFECTS.md` — ISF effects in chains
- `docs/AUDIO.md` — listening, the beat clock and the modulation bus
- `docs/CONTENT.md` — particles, fractals, reactive scenes, stings and looks
- `docs/LED.md` — LED pixel mapping: fixtures, sampling, patching and sending
- `docs/VIDEO.md` — video in and out over OMT, NDI and Spout: live inputs, sends, the loss policy
- `docs/MAPPING.md` — the stage, the site frame and solving a projector from pins and priors
- `docs/CALIBRATION.md` — the quad on the wall, the edit overlay, the score, the calibration ledger and re-solving the
  projector that moved
- `docs/FACADE.md` — buildings: the site frame, survey import, control points, long-throw solves, geodesy, stacks, the light
  plan and the facade guide
- `docs/ALIGN.md` — camera auto-align of flat screens: warp, blend, white, rescanning one output, the verify pass and the score
- `docs/PHONESCAN.md` — the phone as a scan camera, fully local
- `docs/SCAN-MULTIVIEW.md` — many views of many projectors, solved together
- `docs/FLEET.md` — machine health, the fleet board and PlayMap on Patterns' Eye
- `docs/PLATFORM.md` — the machine platform: what a show node claims from Windows, its identity, the driver lock and machine
  preflight
- `docs/INSTALL.md` — installing a show node: the installer, the supervisor, unsigned builds, the show machine and its support
  tiers
- `docs/PROJECTORS.md` — projector control: PJLink and the makers' own protocols
- `docs/RESEARCH.md` — what the field does and why PlayMap chose what it chose
- `docs/COMPETITIVE.md` — where PlayMap stands against MadMapper, disguise, Pixera, Watchout and the rest, and what to learn from each
- `docs/UX.md` — the desk's principles, workflows and time targets
- `docs/DESK.md` — the desk as built: the show strip, the attention bar, drawers, generic STATE cards, generated menus, the safety
  patterns, the quad that stays and the spine its parts build on
- `docs/XR.md` — phones, smart glasses and headsets: look at a surface and control it
- `docs/ROADMAP.md` — what is built, what only the lab can prove, the fixes still open, and the prioritised rounds
- `docs/round2/CARRYOVER.md` — the open fixes in detail, and the owner's calls with their answers in one place
- `docs/round3/README.md` — where PlayMap is going: the round's index, its plan (`PLAN.md`: the installed, Windows-native node
  and the desk's workflows, with the owner's answers), the designs that go beyond the plan (`design/`) and the research behind
  its choices (`research/`)
- `THIRD-PARTY-NOTICES.md` — what PlayMap ships or loads from others, with their licences

## Build

```
dotnet build PlayMap.sln -c Debug
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
```

GL tests need a display (`Xvfb :99 &` and `export DISPLAY=:99` on Linux). Media tests need FFmpeg 9.0 shared libraries: set
`PLAYMAP_FFMPEG_DIR` to their folder, an absolute path (a relative one is refused). `PLAYMAP_GL_DIALECT=es300` runs the Render suite on an OpenGL ES 3.00 context.

The shader worker's DXC is its LLVM-licensed compiler alone, dxcompiler, whose built-in validator signs the DXIL; no `dxil.dll` or
`libdxil.so` is used (`docs/round3/PLAN.md` §9 decision 24). On Windows `dxcompiler.dll` comes from Microsoft.Direct3D.DXC, which
the restore downloads. On Linux it is `libdxcompiler.so` from DXC v1.9.2609's Linux release (the URL and both hashes are the Linux
job's `DXC for the shader worker (pinned)` step): set `PLAYMAP_DXC_DIR` to the absolute path of the folder holding it before the
build, and the engine's build copies it beside every build of the engine. Wherever the build copies dxcompiler it lays down
`licenses/DXC/` beside it, the text DXC's LLVM licence asks to travel with the binary: the package's `LICENSE-LLVM.txt` and DXC's
own `LICENSE.TXT` and `ThirdPartyNotices.txt` (`THIRD-PARTY-NOTICES.md`). A Linux build without `PLAYMAP_DXC_DIR` has no DXC: the
worker's `dxc` stage fails on every source ("libdxcompiler.so is not beside the worker (it comes from DXC 1.9.2609's Linux release
…)"), so every `FX CHECK` and `FX IMPORT` report is `ok` false, `fx.compile.worker`'s `missing` reads 1 once the worker runs, and
the tests that compile through DXC (`HlslBuildTests`, `ShaderLayoutTests`, `ShaderRoadTests` and `ShaderWorkerTests` among them)
fail with the loader's words; none is skipped.

On Windows the shader worker's SPIR-V validator is SPIRV-Tools' own library, `SPIRV-Tools-shared.dll` from Google's Visual Studio
2022 release build (the URL and both hashes are the Windows job's `SPIRV-Tools for the shader worker` step): set
`PLAYMAP_SPIRV_TOOLS_DIR` to the folder holding it, and the engine's build copies it beside every build of the engine. A Windows
build without it has no validator: the worker's `validate` stage fails on every source ("SPIRV-Tools-shared.dll is not beside the
worker"), so every `FX CHECK` and `FX IMPORT` report is `ok` false, `fx.compile.worker`'s `missing` reads 1 once the worker runs,
and the road's tests (`ShaderRoadTests`, `IsfVulkanTargetTests`) fail. On Linux SPIRV-Tools is inside shaderc's library.

CI does all of this on every push (`.github/workflows/build.yml`), on Linux and on Windows Server 2025 (`windows-2025`), and a
third job builds and proves the installer (below). The Linux and Windows jobs each build the whole solution in Release with the
analyzers on and run every test project to its end; on Windows that is on Mesa llvmpipe and WARP, since the runner has no GPU. Both jobs compile every program of the HLSL library through the shader worker's
pinned DXC, hold its reflection to the program table (`HlslBuildTests`, `ShaderLayoutTests`) and each container's digest to its
retail hash. Then:

- **Linux** fetches, before its restore, FFmpeg 9.0.2's LGPL shared libraries (FFmpeg-Builds' `lgpl-shared`, the build the
  installer carries), which every decode test loads, with its GPL command-line build on the path only to encode test media, and DXC
  v1.9.2609's Linux release, each held to its SHA-256; of the release it keeps `libdxcompiler.so` alone, held to the worker's pin and cached by it, in the folder it names in
  `PLAYMAP_DXC_DIR` for the build and the suites. It runs the Render suite again on ES 3.00, and the Companion module's tests.
- **Windows** fetches, before its build, the same FFmpeg 9.0.2 LGPL shared libraries (its GPL command-line build on the path only
  to encode test media) and Mesa's llvmpipe at their pinned hashes, and SPIRV-Tools' library at its pinned hash for the shader worker (`PLAYMAP_SPIRV_TOOLS_DIR`); its restore downloads Microsoft.Direct3D.DXC
  1.9.2609.5 from NuGet, whose `dxcompiler.dll` alone the engine's build copies beside the engine. It runs the Direct3D 12 tests
  (`Backend=D3D12`) as a leg of their own, on WARP and the Agility SDK, both from NuGet at pinned versions and hashes
  (`tools/ci-d3d12.ps1`), and lists their results apart in the job's summary. It asks the runner what it gives (`bench/r3-probe`:
  the Agility runtime in a .NET apphost, MMCSS, the throttling read-back, the GPU scheduling class, HDR10, the session calls) and
  writes the answers in the summary. Its machine platform suite makes every claim on the runner (`PlatformCallsTests`), and a step
  after the suites puts what Windows answered each claim, the identity and facts it read and both waits' lateness in the log and
  the summary, failing when the suite wrote none. Its build compiles the HLSL library with that package's `dxc.exe` into the DXIL
  `PlayMap.Render.D3D12` embeds, started without `DXC_DXIL_DLL_PATH` (the job names a path with nothing there in it, so a `dxc.exe`
  that saw it would fail the build) so that dxcompiler's built-in validator signs it, and it uploads every entry's PDB as
  `hlsl-symbols` (failing when there is none). It builds the lab's present bench (`bench/r3-present`) without running it, and
  publishes the portable exe, which `DxcNativesTests` read again before it is named with its version and `-unsigned` (Versions,
  below); the folder carries its notices (`installer/notices.ps1`) and its `SHA256SUMS` and no file of the Agility SDK,
  `LicenceTests` hold it to DXC's compiler alone and to notices naming every package and native library it carries (their results
  uploaded on their own, pass or fail), and it is uploaded.
- **The installer job** (`windows-2025`, elevated) publishes the exe with ReadyToRun and the supervisor, fetches the same pinned LGPL
  FFmpeg, the Agility SDK's runtime and SPIRV-Tools, builds libvmx from its vendored source with its upstream Windows command, and
  builds this build's installer and the one numbered before it with
  Inno Setup 7.1.0 (pinned by its hash, the release's digest and its publisher's signature checked). It checks the release order
  (`PlayMap.Supervisor.exe version-check`), runs `InstallerTests`, `UnsignedArtifactTests` and `LicenceTests` on the installer and its
  payload (the licence page, a quiet install's `/ACCEPTTERMS=yes`, install, upgrade from the build before, a downgrade refused, the
  armed and silent refusals, uninstall: `docs/INSTALL.md`, "What is proven, and where"), probes mDNS beside Windows' own responder
  and M9's session calls, and uploads `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned-installer`: the installer, its
  `SHA256SUMS` and `SHA256SUMS.payload`. It is a hard gate for every merge (`docs/round3/PLAN.md` §5.4 item 5).
- **A dispatched run** with the `d3d12` input runs only the Windows job's Direct3D 12 leg, narrowed by that test filter. It
  uploads the leg's results, diff images, info-queue log and DRED output, and gives the leg's minutes in the summary.

A test whose subject exists on one platform only (EGL, GLX, dmabufs and VA-API on Linux; DXGI, Direct3D 12, Spout and D3D11VA on
Windows) carries that platform's category (`Category=Linux`, `Category=Windows`) and is skipped on the other with the words.

On Ubuntu 24.04, `sudo tools/setup-dev.sh start` installs the .NET 10 SDK, Mesa and Xvfb, FFmpeg 9.0, DXC's `libdxcompiler.so`
in `/opt/dxc` (the Linux job's release, archive and file held to the same hashes) and a sound stack (PulseAudio with a virtual output
whose monitor the audio tests capture from), starts the display and the sound card, and prints the variables to export, among them
`PLAYMAP_DXC_DIR`.

Third-party software, and the licences it comes under, is listed in `THIRD-PARTY-NOTICES.md`.

## Versions

The product version is SemVer, `MAJOR.MINOR.PATCH`, written once, as `PlayMapVersion` in `Version.props`. Every build carries
a build number and its commit: CI passes its run number and commit (`-p:PlayMapRun=<n> -p:PlayMapCommit=<sha>`), and the
build number is the run number less `PlayMapRunBase`, also in `Version.props`; a build given neither is a local build,
numbered 0 and marked `-dev`, with the commit its checkout is at (none outside a git checkout, which does not fail the build).
Every assembly of PlayMap's own source carries the same stamp — here, for version 0.2.0, CI's build 17 of commit abc1234 and a
local build:

| | CI build | Local build |
|---|---|---|
| AssemblyVersion | `0.2.0.0` | `0.2.0.0` |
| FileVersion (Windows' four parts) | `0.2.0.17` | `0.2.0.0` |
| InformationalVersion (Windows' ProductVersion) | `0.2.0+17.abc1234` | `0.2.0-dev+abc1234` |
| In words | PlayMap 0.2.0 (build 17, abc1234) | PlayMap 0.2.0-dev (local build, abc1234) |

The assembly version moves only with MAJOR.MINOR, so binding does not churn between builds. A part of a Windows file version
holds at most 65535: a build number past it fails the build with words (wrapped, it would read to an installer as an older
build; clamped, as the same one). A MAJOR or MINOR above 255 fails every build with words: the installer's version is
MAJOR.MINOR.BUILD (`BuildInfo.MsiVersion`; `docs/INSTALL.md`, "The two version numbers"), and an MSI's version holds at most 255
in each of its first two fields. CI's run numbers never start again (they count the workflow's runs), so a raised version
alone keeps the count; raising MAJOR or MINOR and setting `PlayMapRunBase` to the last run's number in the same commit starts
it again at 1. A raised PATCH never starts it again: Windows Installer reads three fields of a version, so an installer of
PlayMap orders builds by MAJOR.MINOR and the build number, and a count started again under the same MAJOR.MINOR would read to
it as an older build. The vendored projects under `src/ThirdParty` keep their upstream's version, with PlayMap's patch level after it
(libomtnet's `1.0.0.20+7d29031.playmap.1`): their file version does not move with PlayMap's builds, so whatever installs
PlayMap replaces every file of a build, not only those whose version rose. Where to read a build: the exe's file properties
(File version, Product version); the desk's title and the About line of its help (F1); the engine's log, where the first line
of every file it starts, the start line and every crash line name it, and the journal's start line; HEALTH's `build`, which the
context's machine item and the fleet's board carry (docs/wire/fleet.md); the web port's MCP server info; each CI job's summary,
written straight after its build, and the artifacts' names (the Windows job's portable `PlayMap-0.2.0.17-win-x64-unsigned`; the
installer job's `PlayMap-0.2.0.17-win-x64-unsigned-installer`, holding `PlayMap-0.2.0.17-win-x64-unsigned.exe`). STATE's `version` is the
product version alone, the same for every build of a release, so the committed STATE fixtures hold on any machine.
