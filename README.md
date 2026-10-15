# PlayMap

**A playback node and a 3D projection-mapping suite, in one portable exe — and a full member of the Patterns /
Stagewatch show network.**

PlayMap plays anything FFmpeg can decode, the whole HAP family on the GPU, stills, image sequences, network streams,
capture devices, test patterns, fractals, particle fields and sound-reactive scenes, through chains of ISF effects. It
runs any number of independent playlists, in frame-accurate step with other PlayMap nodes, and a playlist can chase
timecode. It maps all of that onto walls, objects, rooms and buildings: a corner-pinned quad in four clicks, structured-light
scans with a camera, click an object to make it a surface, automatic blends across projectors, and a camera that measures the
warp and blend of projectors on a flat screen. LED fixtures sample the same pictures. Show Mode, locks, a panic and a grand
master keep a running show safe. Patterns, Stagewatch, Companion and an AI (via MCP) can see everything it is doing and drive
everything it can do; OSC, MIDI, Art-Net and sACN controllers drive it through bindings.

```
PlayMap                     the desk: Play · Map · Scan, plus Outputs and Network drawers
PlayMap engine              the node (separate process: a desk that closes never blanks a projector)
PlayMap engine headless     a rack render node driven by Patterns, with no desk at all
```

## What it does

- **Plays.** FFmpeg with hardware decode; all seven HAP codecs block-compressed on the GPU, HAP R and HAP HDR included;
  Patterns' test patterns, 2D and raymarched 3D fractals, a GPU particle field of up to 1 M particles, sound-reactive
  scenes, stings and thirty curated looks. Every generator counts its own time on the show clock, so a late joiner, a backup
  or a chase draws the same picture.
- **Shapes.** Ordered chains of ISF effects on outputs, layers, surfaces and playlist items, a show's own ISF files beside the
  ten built in. A number on a layer, a surface, an effect or a generator can move with the sound, the beat clock or an LFO.
- **Maps.** `LIGHT WALL` and a pixel quad from four clicks on the wall, with the edit overlay drawn through the output's warp;
  structured-light scans with a capture camera (or the simulator), click-to-surface, pins, masks, lattices and 3D blends;
  camera auto-align of projectors on a flat screen; one explainable score for every calibration, kept in a ledger with a
  fallback. Core also solves many camera views of many projectors together into depth maps and a merged mesh.
- **Drives LEDs.** Fixtures laid on a canvas, an output, a surface or a source sample its picture in linear light every frame
  and leave as Art-Net 4, sACN or DDP, latched with the projection; patching with an overlap check and a bandwidth guard, test
  patterns and locate, all by verb.
- **Keeps the show safe.** `SHOW ARM` turns a rehearsal into a show and decides every line by who sent it; locks on finished
  work; `PANIC` takes each output to its planned outcome on the show clock and `PANIC CLEAR` brings it back; a grand master;
  a fence against the double GO; `PREFLIGHT` says what is not ready, each row with the line that fixes it.
- **Presents, and measures it.** Each output keeps a vblank clock and a ledger of late and missed presents; a per-output
  frame-lock machine for sync boards, whose NVAPI tier is compile-only and joins no swap group while outputs present through
  GL; OpenGL 3.3 core or OpenGL ES 3.00 (EGL, or surfaceless with no window system).
- **Takes control.** OSC, MIDI, Art-Net and sACN bound to wire lines, with curves, ranges, pick-up and feedback; a playlist
  chases LTC, MTC, Art-Net timecode or an internal generator; Companion and other controllers get the full line wire over the
  web port, the show's index, its events and key pictures; the node announces itself over mDNS.
- **Listens.** One sound input analysed into levels, bands, a spectrum and onsets; a beat clock by tap, by number or by
  detection.
- **Watches the fleet.** Each machine's health with lights by evidence, and a board of every PlayMap machine in the shape of
  Patterns' Eye.

Each runs by verbs on every door — the line wire, HTTP, MCP, the controllers' WebSocket and the bindings — and STATE and the
context report it; the multi-view solve is Core's alone so far.

**In progress:** blind edit and sound on the air; the sync group's leader clock and measured sync; the phone as a scan
camera; video in and out over OMT and NDI; optimise to HAP on import; the Companion module; and the desk's controls for
show safety, locks, effects and the rest, which today run over the wire.

## What it is made of

| Module | What it does |
|---|---|
| `PlayMap.Core` | The immutable project model and its single writer (undo, coalesced drags). Also: the monotonic clock, network clock and drift estimate; timeline anchors (frame = a pure function of show time); Patterns' clip semantics; the one verb catalogue; Show Bus hello/context/pairing; the solvers (SVD, Levenberg–Marquardt); mesh, ray caster and camera model. Mapping maths (warp lattices, pixel quads, Bézier masks, 3D blend), scan maths (structured light, calibration, point clouds, the multi-view solve), align's fits and the calibration score sit here too, with the show policy and preflight, the ISF parser, audio analysis, the beat clock and the modulation bus, OSC/MIDI/DMX and binding maths, LTC/MTC and the chase, the frame-lock machine, the generator catalogue and its looks, LED layouts, patching and packets, and fleet health. |
| `PlayMap.Engine` | The node: control lane, command router (every verb), the show gate (Show Mode, locks, panic, master, the GO fence), preflight, frame-exact playlist lanes with pre-scheduled gapless switches, timecode chase, control bindings, the calibration ledger, align runs, the scan runner, STATE and context, journal, recovery record, sync-group anchors |
| `PlayMap.Render` | One GL context on a platform seam (OpenGL 3.3 core or ES 3.00; GLFW windows, EGL or surfaceless), a presenter per output with its vblank clock and present ledger, the frame-lock tiers; the projector pass through the 3D stage and pixel quads, warp/mask/blend/black level/colour, align's measured warp and blend, the air's gain last; ISF effect chains; the HAP pass; generators (Patterns' test patterns, fractals, the GPU particle field, raymarched fractals, reactive scenes, surface-aware effects); YUV video upload; the edit overlay; LED sampling; previews |
| `PlayMap.Media` | FFmpeg 9.0: probe, hardware decode, frame-accurate seek, preroll, gapless loop, capture devices, content hashing; HAP's own parser with its Snappy stage on a worker pool; sound input (libavdevice; WASAPI with loopback on Windows) |
| `PlayMap.Net` | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; also on the web port's `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS + MCP (9940) with `/health` and `/fleet`, pairing and TLS pinning, fine clock sync (UDP 9942), OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) input, the LED sender (Art-Net, sACN, DDP), the mDNS announcement (`_playmap._tcp`), the fleet monitor, media transfer, the Patterns and Stagewatch links |
| `PlayMap.Devices` | Projectors: PJLink and the manufacturers' own protocols (Christie, Panasonic, Barco, Sony, Epson, NEC). Covers power, shutter, lens, health, and resetting internal geometry before scans. Also the machine's MIDI ports (ALSA sequencer on Linux, WinMM on Windows). |
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
- `docs/SHOW.md` — Show Mode, locks, panic, the master, the GO fence and preflight
- `docs/OUTPUT.md` — presenting, vblank truth and the frame lock
- `docs/DECODE.md` — the HAP family on the GPU
- `docs/CONTROL.md` — OSC, MIDI, Art-Net and sACN input, and bindings
- `docs/TIMECODE.md` — timecode chase
- `docs/COMPANION.md` — the node's side of Companion and other controllers
- `docs/EFFECTS.md` — ISF effects in chains
- `docs/AUDIO.md` — listening, the beat clock and the modulation bus
- `docs/CONTENT.md` — particles, fractals, reactive scenes, stings and looks
- `docs/LED.md` — LED pixel mapping: fixtures, sampling, patching and sending
- `docs/CALIBRATION.md` — the quad on the wall, the edit overlay, the score and the calibration ledger
- `docs/ALIGN.md` — camera auto-align of flat screens
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
