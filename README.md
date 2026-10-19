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
history strip's Recall; MAP's quad tool (Q, four clicks, Enter; the arrows nudge a corner in wall pixels) with the overlay on the
wall (O); and a first-light guide from LIGHT WALL to the first clip (docs/DESK.md). The rest runs over the wire, HTTP, MCP,
Companion and the bindings, and shows on the desk as generic cards and generated right-click entries: the effects browser,
modulation, tempo, bindings and learn, timecode, LED placement and patching, optimise jobs, live inputs and sends, the phone scan
path, align, the facade guide, the sync tab, the present path chips, the per-output panic plans, Companion decks and the content
library.

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
included (`docs/round3/PLAN.md`). It will install per machine from an installer built with Inno Setup this round (an MSI is
revisited next round); builds will stay unsigned for now, named as unsigned, each with a SHA-256 list, and the installer will
show a licence page for the Direct3D 12 runtime it carries, as Microsoft's terms for shipping that runtime ask. A supervising
service will start the engine in the show user's session, never as administrator and never in session 0. The engine will render
on Direct3D 12 behind the rendering hardware interface it draws through today, and OpenGL stays the reference backend until the
pixel and timing evidence says the two agree. Outputs will lock in hardware where the hardware allows it (NVIDIA's present
barrier on Direct3D 12, its swap group on OpenGL) and run the software lock everywhere else: the same frame, its phase measured.
The desk will open on guided workflows — two projectors blended or stacked on a flat screen, a film mapped onto a wall so it
reads undistorted from the audience, an object mapped with one or two projectors — beside advanced panes and a typed console,
with every command and feature in the menu bar and the right-click menus. Every mapping step will be doable by hand or with the
assistant, and capture cards will pass through to the air. With no lab this round, what only hardware proves stays on the lab's
list (above). Twenty-one packages build the direction in four waves, each of the plan's decisions answered in its §9, on the
round's spine, which is built: the catalogue's rows, Core's contracts, the engine's seams, the model's fields, the new projects
as placeholders and the CI legs. Of the packages, the rendering hardware interface is built on it: every frame draws through
Core's `IGpuDevice` with the GL backend behind it, its pixels unchanged (`docs/ROADMAP.md`, round 3).

## What it is made of

| Module | What it does |
|---|---|
| `PlayMap.Core` | The immutable project model and its single writer (undo, coalesced drags). Also: the monotonic clock, the leader clock (slewed, stepped on the engine's lane, held while the leader is quiet) and its drift estimate; timeline anchors (frame = a pure function of show time); Patterns' clip semantics; the one verb catalogue; Show Bus hello/context/pairing; the solvers (SVD, Levenberg–Marquardt); mesh, ray caster and camera model. Mapping maths (warp lattices, pixel quads, Bézier masks, 3D blend), scan maths (structured light, calibration, point clouds, the multi-view solve), align's fits and the calibration score sit here too, with the show policy, preflight and the blind draft's diff, the ISF parser, audio analysis, the beat clock and the modulation bus, OSC/MIDI/DMX and binding maths, LTC/MTC and the chase, the frame-lock machine, the generator catalogue and its looks, LED layouts, patching and packets, fleet health, and optimise's planner, rules, governor, HAP packer and version choice. The site frame and the survey readers (rebased in double; chunked LAS, a managed E57 reader), decimation and voxel thinning, control-point files and the site check, and the long-throw solve with its priors sit here too. |
| `PlayMap.Engine` | The node: control lane, command router (every verb), the show gate (Show Mode, locks, panic, master, the GO fence), the blind draft and its commit at a show boundary, preflight, frame-exact playlist lanes with pre-scheduled gapless switches and fades on a GO by hand, the air's sound, timecode chase, control bindings, the video I/O hub (each live input received once and lent to every consumer, each send's sender, the loss policy and tally), the calibration ledger, align runs, the scan runner (a scan's views solved together, kept on disk, its surface suggestions and the folder camera), survey import (read, thinned and decimated on its own thread, applied as one edit), the optimise queue and its worker (the same exe as `PlayMap optimise-worker`, a process of its own the engine starts and watches), STATE and context, journal, recovery record; the sync part: the role kept, the leader's air fanned out to its members, and a member taking the leader's anchors and air through its own verbs (answered with receipts) and its frame lock |
| `PlayMap.Render` | The frame drawn through Core's render hardware interface (`IGpuDevice`, its frame list, one `DrawState` per draw), with the GL backend behind it (`Render/Gl`): one GL context on a platform seam (OpenGL 3.3 core or ES 3.00; GLFW windows, EGL or surfaceless), a presenter per output with its vblank clock and present ledger, the frame-lock tiers; the projector pass through the 3D stage and pixel quads, warp/mask/blend/black level/colour, align's measured warp and blend, the air's gain last; ISF effect chains; the HAP pass; generators (Patterns' test patterns, fractals, the GPU particle field, raymarched fractals, reactive scenes, surface-aware effects); YUV video upload and the unpack of packed network video; the edit overlay; LED sampling; the send stage (network video packed on the GPU and read back without a stall); previews; the GPU HAP encoder |
| `PlayMap.Media` | FFmpeg 9.0: probe, hardware decode, frame-accurate seek, preroll, gapless loop, capture devices, content hashing; OMT (libomtnet and libvmx, vendored) and NDI 6 (the user's runtime, loaded at run time) receivers and senders over a lock-free frame pool; HAP's own parser with its Snappy stage on a worker pool; stills (JPEG, PNG, TIFF at 16 bits) read as grey for a folder camera; the HAP transcode (decode, encode, mux with the source's audio, read back); sound input (libavdevice; WASAPI with loopback on Windows); sound out through OpenAL Soft, slaved to a transport |
| `PlayMap.Net` | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; also on the web port's `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS + MCP (9940) with `/health` and `/fleet`, pairing and TLS pinning, fine clock sync and the members' vblank reports (UDP 9942), the sync group's anchor feed and the leader's sync board, OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) input, the LED sender (Art-Net, sACN, DDP), the mDNS announcement (`_playmap._tcp`), the fleet monitor, media transfer, the Patterns and Stagewatch links |
| `PlayMap.Devices` | Projectors: PJLink and the manufacturers' own protocols (Christie, Panasonic, Barco, Sony, Epson, NEC). Covers power, shutter, lens, health, resetting internal geometry before scans, and reading a Panasonic's own geometry and edge blend while PlayMap corrects its picture. Also the machine's MIDI ports (ALSA sequencer on Linux, WinMM on Windows). |
| `PlayMap.Vision` | Local AI, offline: click-to-surface segmentation and suggested regions on the smart scan, by a classical segmenter or an optional ONNX segmentation model. Monocular depth previs, pin suggestions and drift watch are in the library; no verb reaches them yet. |
| `PlayMap.Assistant` | Plain words → PlayMap verbs, with a hosted or a local model. High-risk verbs always wait for a person on the node's own proposal list. |
| `PlayMap.Render.D3D12` | The Direct3D 12 backend (`docs/round3/design/d3d12.md`), Windows only, on TerraFX's raw bindings: one folder for each part — the device, queues, memory, descriptors, commands, pipelines, shaders, the exporter, the platform, the flip presenters, video, user effects, bindless records, the present barrier and the rebuild path — each a placeholder. Nothing renders through it yet, and neither the engine nor the desk references it; it never references `PlayMap.Render`, so the two backends meet only in Core's contracts |
| `PlayMap.Platform.Windows` | What a show node claims from Windows (`docs/round3/design/platform.md`), Windows only: a placeholder. The engine runs on Core's null machine platform, which claims nothing and says so |
| `PlayMap.Supervisor` | The supervising service's exe (`docs/round3/design/installer.md`): it does nothing yet, says so on its standard error and exits 1 |
| `PlayMap.App` | The desk (Avalonia 12): starts and watches this machine's engine or attaches to a node on the network; three workspaces, a show strip, four drawers, a generic card for every package's STATE (the packages' card descriptors built in), right-click menus on everything with entries built from the node's own answers; edits only through the verbs every peer uses, each control asking the node's doors before the press (pairing, the fleet's, the Core show policy, the locks) and greying with the node's own words |

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
- `docs/PROJECTORS.md` — projector control: PJLink and the makers' own protocols
- `docs/RESEARCH.md` — what the field does and why PlayMap chose what it chose
- `docs/COMPETITIVE.md` — where PlayMap stands against MadMapper, disguise, Pixera, Watchout and the rest, and what to learn from each
- `docs/UX.md` — the desk's principles, workflows and time targets
- `docs/DESK.md` — the desk as built: the show strip, the attention bar, drawers, generic STATE cards, generated menus and the
  spine its parts build on
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

GL tests need a display (`Xvfb :99 &` and `export DISPLAY=:99` on Linux). Media tests need FFmpeg 9.0 shared
libraries: set `PLAYMAP_FFMPEG_DIR`. `PLAYMAP_GL_DIALECT=es300` runs the Render suite on an OpenGL ES 3.00 context.

CI does all of this on every push (`.github/workflows/build.yml`), on Linux and on Windows Server 2025 (`windows-2025`). Each
job builds the whole solution in Release with the analyzers on and runs every test project to its end; on Windows that is on
Mesa llvmpipe and WARP, since the runner has no GPU. Then:

- **Linux** runs the Render suite again on ES 3.00, and the Companion module's tests.
- **Windows** runs the Direct3D 12 tests (`Backend=D3D12`) as a leg of their own, on WARP and the Agility SDK, both from NuGet at
  pinned versions and hashes (`tools/ci-d3d12.ps1`), and lists their results apart in the job's summary. It asks the runner what
  it gives (`bench/r3-probe`: the Agility runtime in a .NET apphost, MMCSS, the throttling read-back, the GPU scheduling class,
  HDR10, the session calls) and writes the answers in the summary. It builds the lab's present bench (`bench/r3-present`) without
  running it, and publishes the portable exe, named with its version (Versions, below).
- **A dispatched run** with the `d3d12` input runs only the Windows job's Direct3D 12 leg, narrowed by that test filter. It
  uploads the leg's results, diff images, info-queue log and DRED output, and gives the leg's minutes in the summary.

A test whose subject exists on one platform only (EGL, GLX, dmabufs and VA-API on Linux; DXGI, Direct3D 12, Spout and D3D11VA on
Windows) carries that platform's category (`Category=Linux`, `Category=Windows`) and is skipped on the other with the words.

On Ubuntu 24.04, `sudo tools/setup-dev.sh start` installs the .NET 10 SDK, Mesa and Xvfb, FFmpeg 9.0 and a sound stack
(PulseAudio with a virtual output whose monitor the audio tests capture from), starts the display and the sound card, and prints
the variables to export.

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
build; clamped, as the same one). CI's run numbers never start again (they count the workflow's runs), so a raised version
alone keeps the count; raising MAJOR or MINOR and setting `PlayMapRunBase` to the last run's number in the same commit starts
it again at 1. A raised PATCH never starts it again: Windows Installer reads three fields of a version, so an installer of
PlayMap orders builds by MAJOR.MINOR and the build number, and a count started again under the same MAJOR.MINOR would read to
it as an older build. The vendored projects under `src/ThirdParty` keep their upstream's version, with PlayMap's patch level after it
(libomtnet's `1.0.0.20+7d29031.playmap.1`): their file version does not move with PlayMap's builds, so whatever installs
PlayMap replaces every file of a build, not only those whose version rose. Where to read a build: the exe's file properties
(File version, Product version); the desk's title and the About line of its help (F1); the engine's log, where the first line
of every file it starts, the start line and every crash line name it, and the journal's start line; HEALTH's `build`, which the
context's machine item and the fleet's board carry (docs/wire/fleet.md); the web port's MCP server info; each CI job's summary,
written straight after its build, and the Windows artifact's name (`PlayMap-0.2.0.17-win-x64`). STATE's `version` is the
product version alone, the same for every build of a release, so the committed STATE fixtures hold on any machine.
