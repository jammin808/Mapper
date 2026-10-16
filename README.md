# PlayMap

**A playback node and a 3D projection-mapping suite, in one portable exe — and a full member of the Patterns /
Stagewatch show network.**

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
PlayMap                     the desk: Play · Map · Scan, plus Outputs and Network drawers
PlayMap engine              the node (separate process: a desk that closes never blanks a projector)
PlayMap engine headless     a rack render node driven by Patterns, with no desk at all
```

## What it does

- **Plays.** FFmpeg with hardware decode; all seven HAP codecs block-compressed on the GPU, HAP R and HAP HDR included;
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
  solve says how far the picture could be off on the wall.
- **Drives LEDs.** Fixtures laid on a canvas, an output, a surface or a source sample its picture in linear light every frame
  and leave as Art-Net 4, sACN or DDP, latched with the projection; patching with an overlap check and a bandwidth guard, test
  patterns and locate, all by verb. The rig follows the air: dark under blackout, black and terminated when the node stops,
  and dark where an output shows a scan or alignment pattern.
- **Takes and sends network video.** OMT (vendored, on every node) and NDI 6 (through the runtime the user installs) in and out:
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
  frame-lock machine for sync boards, whose NVAPI tier is compile-only and joins no swap group while outputs present through
  GL; OpenGL 3.3 core or OpenGL ES 3.00 (EGL, or surfaceless with no window system). A sync group's members report each
  output's vblank to the leader, whose board gives each output's phase, the next slip and one verdict, and they join and leave
  the leader's frame lock as it applies and releases it.
- **Takes control.** OSC, MIDI, Art-Net and sACN bound to wire lines, with curves, ranges, pick-up and feedback; a playlist
  chases LTC, MTC, Art-Net timecode or an internal generator; Companion and other controllers get the full line wire over the
  web port, the show's index, its events and key pictures; the node announces itself over mDNS.
- **Listens.** One sound input analysed into levels, bands, a spectrum and onsets; a beat clock by tap, by number or by
  detection.
- **Watches the fleet.** Each machine's health with lights by evidence, and a board of every PlayMap machine in the shape of
  Patterns' Eye.

Each runs by verbs on every door — the line wire, HTTP, MCP, the controllers' WebSocket and the bindings — and STATE and the
context report it.

**In progress:** the Companion module; Spout; and the desk's controls for show safety, locks,
blind edit, the sync board, effects and the rest, which today run over the wire.

## What it is made of

| Module | What it does |
|---|---|
| `PlayMap.Core` | The immutable project model and its single writer (undo, coalesced drags). Also: the monotonic clock, the leader clock (slewed, stepped on the engine's lane, held while the leader is quiet) and its drift estimate; timeline anchors (frame = a pure function of show time); Patterns' clip semantics; the one verb catalogue; Show Bus hello/context/pairing; the solvers (SVD, Levenberg–Marquardt); mesh, ray caster and camera model. Mapping maths (warp lattices, pixel quads, Bézier masks, 3D blend), scan maths (structured light, calibration, point clouds, the multi-view solve), align's fits and the calibration score sit here too, with the show policy, preflight and the blind draft's diff, the ISF parser, audio analysis, the beat clock and the modulation bus, OSC/MIDI/DMX and binding maths, LTC/MTC and the chase, the frame-lock machine, the generator catalogue and its looks, LED layouts, patching and packets, fleet health, and optimise's planner, rules, governor, HAP packer and version choice. The site frame and the survey readers (rebased in double; chunked LAS, a managed E57 reader), decimation and voxel thinning, control-point files and the site check, and the long-throw solve with its priors sit here too. |
| `PlayMap.Engine` | The node: control lane, command router (every verb), the show gate (Show Mode, locks, panic, master, the GO fence), the blind draft and its commit at a show boundary, preflight, frame-exact playlist lanes with pre-scheduled gapless switches and fades on a GO by hand, the air's sound, timecode chase, control bindings, the video I/O hub (each live input received once and lent to every consumer, each send's sender, the loss policy and tally), the calibration ledger, align runs, the scan runner (a scan's views solved together, kept on disk, its surface suggestions and the folder camera), survey import (read, thinned and decimated on its own thread, applied as one edit), the optimise queue and its worker (the same exe as `PlayMap optimise-worker`, a process of its own the engine starts and watches), STATE and context, journal, recovery record; the sync part: the role kept, the leader's air fanned out to its members, and a member taking the leader's anchors and air through its own verbs (answered with receipts) and its frame lock |
| `PlayMap.Render` | One GL context on a platform seam (OpenGL 3.3 core or ES 3.00; GLFW windows, EGL or surfaceless), a presenter per output with its vblank clock and present ledger, the frame-lock tiers; the projector pass through the 3D stage and pixel quads, warp/mask/blend/black level/colour, align's measured warp and blend, the air's gain last; ISF effect chains; the HAP pass; generators (Patterns' test patterns, fractals, the GPU particle field, raymarched fractals, reactive scenes, surface-aware effects); YUV video upload and the unpack of packed network video; the edit overlay; LED sampling; the send stage (network video packed on the GPU and read back without a stall); previews; the GPU HAP encoder |
| `PlayMap.Media` | FFmpeg 9.0: probe, hardware decode, frame-accurate seek, preroll, gapless loop, capture devices, content hashing; OMT (libomtnet and libvmx, vendored) and NDI 6 (the user's runtime, loaded at run time) receivers and senders over a lock-free frame pool; HAP's own parser with its Snappy stage on a worker pool; stills (JPEG, PNG, TIFF at 16 bits) read as grey for a folder camera; the HAP transcode (decode, encode, mux with the source's audio, read back); sound input (libavdevice; WASAPI with loopback on Windows); sound out through OpenAL Soft, slaved to a transport |
| `PlayMap.Net` | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; also on the web port's `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS + MCP (9940) with `/health` and `/fleet`, pairing and TLS pinning, fine clock sync and the members' vblank reports (UDP 9942), the sync group's anchor feed and the leader's sync board, OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) input, the LED sender (Art-Net, sACN, DDP), the mDNS announcement (`_playmap._tcp`), the fleet monitor, media transfer, the Patterns and Stagewatch links |
| `PlayMap.Devices` | Projectors: PJLink and the manufacturers' own protocols (Christie, Panasonic, Barco, Sony, Epson, NEC). Covers power, shutter, lens, health, resetting internal geometry before scans, and reading a Panasonic's own geometry and edge blend while PlayMap corrects its picture. Also the machine's MIDI ports (ALSA sequencer on Linux, WinMM on Windows). |
| `PlayMap.Vision` | Local AI, offline: click-to-surface segmentation and suggested regions on the smart scan, by a classical segmenter or an optional ONNX segmentation model. Monocular depth previs, pin suggestions and drift watch are in the library; no verb reaches them yet. |
| `PlayMap.Assistant` | Plain words → PlayMap verbs, with a hosted or a local model. High-risk verbs always wait for a person on the node's own proposal list. |
| `PlayMap.App` | The desk (Avalonia 12): starts and watches this machine's engine or attaches to a node on the network; simple workspaces, right-click menus on everything, edits only through the verbs every peer uses |

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
- `docs/VIDEO.md` — video in and out over OMT and NDI: live inputs, sends, the loss policy
- `docs/MAPPING.md` — the stage, the site frame and solving a projector from pins and priors
- `docs/CALIBRATION.md` — the quad on the wall, the edit overlay, the score, the calibration ledger and re-solving the
  projector that moved
- `docs/FACADE.md` — buildings: the site frame, survey import, control points and long-throw solves
- `docs/ALIGN.md` — camera auto-align of flat screens: warp, blend, white, rescanning one output, the verify pass and the score
- `docs/PHONESCAN.md` — the phone as a scan camera, fully local
- `docs/SCAN-MULTIVIEW.md` — many views of many projectors, solved together
- `docs/FLEET.md` — machine health, the fleet board and PlayMap on Patterns' Eye
- `docs/PROJECTORS.md` — projector control: PJLink and the makers' own protocols
- `docs/RESEARCH.md` — what the field does and why PlayMap chose what it chose
- `docs/COMPETITIVE.md` — where PlayMap stands against MadMapper, disguise, Pixera, Watchout and the rest, and what to learn from each
- `docs/UX.md` — the desk's principles, workflows and time targets
- `docs/XR.md` — phones, smart glasses and headsets: look at a surface and control it
- `docs/ROADMAP.md` — what is built, what is in progress, and the prioritised rounds
- `THIRD-PARTY-NOTICES.md` — what PlayMap ships or loads from others, with their licences

## Build

```
dotnet build PlayMap.sln -c Debug
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
```

GL tests need a display (`Xvfb :99 &` and `export DISPLAY=:99` on Linux). Media tests need FFmpeg 9.0 shared
libraries: set `PLAYMAP_FFMPEG_DIR`. CI does both (`.github/workflows/build.yml`). On Ubuntu 24.04,
`sudo tools/setup-dev.sh start` installs the .NET 10 SDK, Mesa and Xvfb, FFmpeg 9.0 and a sound stack (PulseAudio with a
virtual output whose monitor the audio tests capture from), starts the display and the sound card, and prints the
variables to export.

Third-party software, and the licences it comes under, is listed in `THIRD-PARTY-NOTICES.md`.
