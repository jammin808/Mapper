# PlayMap

**A playback node and a 3D projection-mapping suite, in one portable exe — and a full member of the Patterns /
Stagewatch show network.**

PlayMap plays anything FFmpeg can decode, stills, image sequences, web pages and YouTube embeds, test patterns,
fractals, particles and live inputs. It runs any number of independent playlists, in frame-accurate step with
other PlayMap nodes. It maps all of that onto real objects, rooms and buildings: scan with any webcam or phone,
import a LiDAR model, click an object to make it a surface, and get automatic blends across projectors.
Patterns, Stagewatch and an AI (via MCP) can see everything it is doing and drive everything it can do.

```
PlayMap                     the desk: Play · Map · Scan, plus Outputs and Network drawers
PlayMap engine              the node (separate process: a desk that closes never blanks a projector)
PlayMap engine headless     a rack render node driven by Patterns, with no desk at all
```

## What it is made of

| Module | What it does |
|---|---|
| `PlayMap.Core` | The immutable project model and its single writer (undo, coalesced drags). Also: the monotonic clock, network clock and drift estimate; timeline anchors (frame = a pure function of show time); Patterns' clip semantics; the one verb catalogue; Show Bus hello/context/pairing; the solvers (SVD, Levenberg–Marquardt); mesh, ray caster and camera model. Mapping maths (warp lattices, Bézier masks, 3D blend) and scan maths (structured light, calibration, point clouds) sit here too. |
| `PlayMap.Engine` | The node: control lane, command router (every verb), frame-exact playlist lanes with pre-scheduled gapless switches, STATE and context, journal, recovery record, sync-group anchors |
| `PlayMap.Render` | OpenGL 3.3 renderer: the projector pass through the 3D stage, warp/mask/blend/black level/colour, generators (Patterns' test patterns, fractals, particles, reactive scenes, surface-aware effects), YUV video upload, previews |
| `PlayMap.Media` | FFmpeg 9.0: probe, hardware decode, frame-accurate seek, preroll, gapless loop, capture devices, content hashing, audio slaved to the timeline |
| `PlayMap.Net` | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar), HTTP/WS/HTTPS + MCP (9940), pairing and TLS pinning, fine clock sync (UDP 9942), media transfer, the Patterns and Stagewatch links |
| `PlayMap.Devices` | Projectors: PJLink and the manufacturers' own protocols (Christie, Panasonic, Barco, Sony, Epson, NEC). Covers power, shutter, lens, health, and resetting internal geometry before scans. |
| `PlayMap.Vision` | Local AI, offline: click- and type-to-surface segmentation, monocular depth previs, pin suggestions, drift watch. Each has a classical fallback. |
| `PlayMap.Assistant` | Plain words → PlayMap verbs, with Claude or a local model. High-risk verbs always wait for a human, here or on Stagewatch's approval desk. |
| `PlayMap.App` | The desk (Avalonia 12): simple workspaces, right-click menus on everything, edits only through the verbs every peer uses |

## Talking to Patterns and Stagewatch

See `docs/SHOWBUS-NODE.md`. With no change to either app:

- Stagewatch's VT countdown and video buttons drive PlayMap — point its Patterns link at PlayMap's port 9941.
- Patterns' Interactive area drives PlayMap as a *Lines* device, and PlayMap follows Patterns' blackout.
- Any AI on the bus reads PlayMap's context, and proposes verbs through Stagewatch's approval desk.

## Papers

- `docs/ARCHITECTURE.md` — the design
- `docs/WIRE.md` — lines, STATE, pushes, HTTP
- `docs/SHOWBUS-NODE.md` — the ecosystem
- `docs/RESEARCH.md` — what the field does and why PlayMap chose what it chose
- `docs/COMPETITIVE.md` — where PlayMap stands against MadMapper, disguise, Pixera, Watchout and the rest, and what to learn from each
- `docs/UX.md` — the desk's principles, workflows and time targets
- `docs/XR.md` — phones, smart glasses and headsets: look at a surface and control it
- `docs/ROADMAP.md` — the prioritised rounds, from first light on a real projector onwards
- `docs/PROJECTORS.md` — projector control: PJLink and the makers' own protocols
- `CLAUDE.md` — the brief for whoever works the tree

## Build

```
dotnet build PlayMap.sln -c Debug
dotnet test tests/PlayMap.Core.Tests -c Debug --no-build
```

GL tests need a display (`Xvfb :99 &` and `export DISPLAY=:99` on Linux). Media tests need FFmpeg 9.0 shared
libraries: set `PLAYMAP_FFMPEG_DIR`. CI does both (`.github/workflows/build.yml`).

MIT licence. FFmpeg is LGPL/GPL depending on the build shipped beside the exe; see the licences of the models
the vision module downloads before using them commercially.
