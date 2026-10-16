# Roadmap

*A prioritised roadmap for PlayMap. Effort and impact are **estimates**. Evidence for each item is in docs/COMPETITIVE.md, docs/UX.md and docs/XR.md. Time targets live in one place: docs/UX.md §5.0.*

## 0. Where we are, and the rule that governs everything

**Merged and tested:**
- The ten modules — assistant, calibration, network, mapping maths, media, scanning, projector control, render, vision and the desk.
- The engine host: `PlayMap engine [headless]` composes the real media, render, network and scan backends; the scan runner, projector control and the assistant run inside it. The desk starts and watches this machine's engine, or attaches to a node on the network over the line wire; its demo engine stays for offline use.
- The round-2 work, built on one spine (the model, the catalogue's rows, the engine's hooks, topics and STATE keys): show safety — Show Mode, locks, panic, the master, the GO fence and preflight (docs/SHOW.md); outputs — the presenter seam, vblank truth and the frame lock (docs/OUTPUT.md); the HAP family on the GPU (docs/DECODE.md); OSC, MIDI, Art-Net and sACN input with bindings (docs/CONTROL.md) and timecode chase (docs/TIMECODE.md); the node's side of Companion (docs/COMPANION.md); ISF effect chains (docs/EFFECTS.md); audio analysis, the beat clock and the modulation bus (docs/AUDIO.md); content — the GPU particle field, raymarched fractals, reactive scenes, stings and looks (docs/CONTENT.md); the wall — pixel quads, the edit overlay, the score and the calibration ledger (docs/CALIBRATION.md); camera auto-align of flat screens (docs/ALIGN.md); fleet health and the board (docs/FLEET.md); LED pixel mapping (docs/LED.md); optimise on import — HAP versions of a clip made by the show's rules with PlayMap's own GPU encoder in a worker process, kept beside the source (docs/OPTIMISE.md); and the multi-view solve, in Core only (docs/SCAN-MULTIVIEW.md).

That work spans items from Rounds 1–4 and the mid term; a **Built** note on an item below says what main has of it. Camera auto-align and the content package have no item of their own.

**In flight:** blind edit, sound on the air and fades on a manual GO; the sync group's leader clock, anchors and measured sync; video in and out over OMT and NDI; the Companion module; the desk's controls for the new verbs (the show strip, panic, locks, generated right-click entries, cards for every package's STATE); CI parity on Linux and Windows. Until the desk's controls land, the new verbs run over the wire, HTTP, MCP and bindings.

**Not yet run on real hardware:** the tests run on Mesa llvmpipe under Xvfb, on the scan and align simulators and on a scripted frame-lock board; the NVAPI tier, WASAPI and WinMM are compiled on Linux and run by the Windows CI job, which has no GPU. A real projector, a real camera scan, a sync board, HAP R and HAP HDR files from other encoders, real LED nodes, and a second machine are lab checks still to do.

**The rule:** **Round 0** on real hardware is the gate. Every lens agreed: it is the cheapest way to learn which design assumptions are wrong.

| Effort | Duration |
|---|---|
| S | under 1 week |
| M | 1–4 weeks |
| L | 1–3 months |
| XL | over 3 months |

| Impact | Meaning |
|---|---|
| ★★★ | Blocks real shows, or is the headline differentiator |
| ★★ | Expected by pros |
| ★ | Nice to have |

## 1. How we chose the order

1. **First light, then smoothness, then interop, then show-floor discipline.** Nothing else matters if the wall stutters.
2. **Serve 3D mapping artists, corporate AV and installs first.** Serve VJs early through interop so PlayMap can be the mapper behind Resolume or TouchDesigner; build the instrument layer mid-term.
3. **Every feature ships as verbs:** a verb with typed parameters, a STATE key, a model field and a `DeskAction`, enforced in CI.
4. **Measure before we claim.** Publish scan time, RMS, sync offset, layer counts and latency per release. Until then, every "better than" is a hypothesis.

Decisions where the lenses disagreed are in docs/COMPETITIVE.md §8. In short: D3D11 before any pro release, not before first light; no timeline engine; `PICK`/`HIGHLIGHT`/`CURSOR` in Round 1 and the phone in Round 3; risk gating depends on the caller; Spout and OMT always available, NDI as a plugin; 10-bit in Round 2, HDR signalling mid-term; macOS desk mid-term, engine long-term.

## 2. Near term: Rounds 0–4

### Round 0: First light (gate)

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R0.1 | Implement the engine seams with the real media, render, network and scan backends; `Program.Main` runs the desk and `engine [headless]`. **Built.** | ★★★ | L | — |
| R0.2 | Merge the desk; the desk talks to the real engine over `EngineLinkClient`; optimistic edits roll back with a reason chip when the engine refuses (docs/UX.md §3.5). **Built:** the desk, on the real engine over `EngineLinkClient`; not the reason-chip roll-back | ★★★ | M | R0.1 |
| R0.3 | One output window fullscreen, one corner-pinned quad, an H.264 clip and a generator, driven by verbs. **Built in code:** `LIGHT WALL`, `SURFACE QUAD`, FFmpeg clips and generators by verbs; not yet run on a projector | ★★★ | M | R0.1 |
| R0.4 | Self-hosted Windows CI runner with NVIDIA and AMD GPUs and FFmpeg; nightly soak counts missed vblanks | ★★★ | M | R0.1 |
| R0.5 | Hardware lab: three projectors from different makers (one DLP, one 3LCD or LCoS, one short-throw; at least one install model with internal warp); two nodes (RTX PRO with a sync card, GeForce); two UVC webcams (one with manual exposure, one without); an iPhone and an Android phone; a white box, a curved flat, a textured sculpture; material samples (scrim, glossy, black, glass) | ★★★ | M + purchase | — |
| R0.6 | Missed vblanks, present mode and p95 frame time per output in STATE. **Built:** `outputs[].present` (missed vblanks, `missed60s`, the late share), `outputs[].frameMsP95`, `machine.present` (path and dialect), `OUTPUT STATS`, `PRESENT RESET` | ★★ | S | R0.3 |
| R0.7 | Signed build; SmartScreen and antivirus false-positive check; one explained firewall prompt; FFmpeg as replaceable LGPL libraries with notices | ★★★ | S | R0.1 |

**Exit:** a fresh Windows PC plays a looped clip on a projector from the real desk for 24 hours with the missed-vblank count published (it need not be zero yet).

### Round 1: First real map

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R1.1 | Scan runner (`IScanBackend`): patterns and capture together, frame-ID tags, settle detection, exposure as a multiple of the refresh period; `SCAN START/APPLY` work. **Built:** `SCAN START/APPLY/SURFACE` with frame tags, the settle detector, a shutter-closed black frame and a latency stage, on a capture camera or the simulator | ★★★ | L | R0 |
| R1.2 | `PROJECTOR SOLVE` and `BLEND SOLVE` on real input; results as named immutable snapshots with a fallback; per-projector re-solve that leaves masks and lattices untouched (projector-moved case). **Built in code:** the calibration ledger (immutable snapshots, a fallback) and `CALIBRATION RESOLVE` from pins; real-camera runs are lab work | ★★★ | M | R1.1 |
| R1.3 | Projector verbs: `PROJECTOR POWER/SHUTTER/INPUT/LENS/RESETGEOM`; model link Projector → device; geometry reset before every scan; read and set low-latency mode where exposed; verify NEC and Sony drivers. **Built** as `DEVICE POWER/SHUTTER/INPUT/FREEZE/LENS/RAW/PREPARE/IDENTIFY`, `DEVICE ADD … PROJECTOR`, and the scan runner readying each projector; not the low-latency mode; the NEC and Sony checks are hardware work | ★★★ | M | R0.5 |
| R1.4 | Explainable calibration score: RMS plus penalties (decoded %, focus, coverage, overlap under 20%, lens-lock violation), each with a fix link; confidence map projectable onto the object. **Built:** one score with terms, fix lines and A/B/C/F grades in STATE (docs/CALIBRATION.md); the confidence map is a preview view, not projected | ★★★ | M | R1.2 |
| R1.5 | Edit overlay pass: video cursor, selected-surface outline, lattice and mask on the wall, surface names, flash grid; stripped when armed. **Built:** outlines, handles, lattices and cursors through the output's warp, nothing while armed (`OVERLAY`, `CURSOR`); not masks, surface names, pins or the flash grid | ★★★ | M | R0.3 |
| R1.6 | 2D quad path (4 clicks, arrow nudges, promote to lattice keeping its shape). **Built:** `SURFACE QUAD`, `HANDLE`, `SURFACE LATTICE` | ★★★ | S | R1.5 |
| R1.7 | Click-to-surface verbs (`SURFACE FROM-POINT`) on the classical segmenter plus a pinned SAM 2.1-tiny ONNX export (with the previous-mask input) and checksum; licence of the export checked. Text select (`FROM-TEXT`) is mid-term (M30). **Built in part:** `SCAN SURFACE <scan> x y` and `REGION` on the classical segmenter and an optional ONNX model; no `SURFACE FROM-POINT` row | ★★★ | M | R1.1 |
| R1.8 | Verb-first contract and CI check; verbs for pin warp, masks and pen, coverage, output layout, live inputs, audio volume, vision (select, suggest). **Built in part:** typed parameters on every row, `VERBS` marking each available or not; no CI check of a `DeskAction` per verb | ★★★ | M | R0.2 |
| R1.9 | Engine-served `/scan` phone camera page (JPEG over WebSocket, frame envelope with time, intrinsics and pose). **Built:** the node serves the page on its own certificate under `/scan`; a one-time `PHONE INVITE`; lock-step capture paced by what the phone has seen; the phone as an align camera; not yet checked on real phones (docs/PHONESCAN.md) | ★★ | M | R1.1 |
| R1.10 | Render Fade and Dip transitions in linear light. **Built:** scheduled switches mix across the incoming item's window | ★★ | S | R0.3 |
| R1.11 | `LATENCY MEASURE` with the scan camera; per-output latency stored and fed into frame selection | ★★ | S | R1.1 |
| R1.12 | `PICK RAY`, `PICK PIXEL`, `HIGHLIGHT`, `CURSOR` verbs (desk on-wall cursor and hover; XR Phase 0). **Built:** `CURSOR`; not `PICK` or `HIGHLIGHT` | ★★★ | S | R1.5, R1.2 |
| R1.13 | Scan preflight: camera capability probe (manual exposure, focus and white-balance lock, MJPEG, banding risk) and projector-settings card (dynamic contrast, iris, eco, interpolation, auto keystone); a tested camera list published | ★★★ | M | R1.1, R1.3 |
| R1.14 | Per-material fallbacks: every low-confidence surface after a partial scan keeps a manual path (pins, quad, lattice on the frozen camera image); Robust and Difficult scan modes exposed | ★★ | S | R1.1 |

**Exit:** "map a box with one projector and a webcam" (docs/UX.md §5.0) is timed on a tested webcam in an office-lit room with the RMS published, and the §5.1 storyboard is filmed.

### Round 2: Smooth playback

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R2.1 | D3D11 flip-model renderer: one swap chain per output, waitable object, `GetFrameStatistics` as vblank truth, verified independent flip, one present thread per adapter; GL 3.3 kept for Linux and development. **Built so far:** the presenter seam, vblank truth per output (`VblankClock`, `PresentLedger`) and the OpenGL ES 3.00 dialect; not the D3D11 path | ★★★ | L | R0 |
| R2.2 | D3D11VA zero-copy (shared device, NV12 slices as SRVs); "zero-copy yes/no" per source in STATE | ★★★ | M | R2.1 |
| R2.3 | HAP family on the GPU: own parser, chunked Snappy, BC1/BC3/BC4/BC7/BC6H upload; HAP R and HAP HDR, which FFmpeg does not decode. **Built** on the GL path with a BPTC check (docs/DECODE.md); files from other encoders are lab work | ★★★ | M | R2.1 (a GL path with a BPTC check can come earlier) |
| R2.4 | Optimise on import: background transcode to HAP Q / HAP R keyed by (hash, codec, params), synced like any asset, source kept; GPU BC7 encoder; runs on the desk or an idle node. **Built** for this node: HAP, HAP Alpha, HAP Q and HAP Q Alpha from PlayMap's own GPU encoder in a worker process, planned by the show's rules (`MEDIA OPTIMISE`; on import under `MEDIA RULE MODE AUTO`), keyed by source hash and recipe, verified by PSNR, kept as the item's versions beside the source and played from its next open (docs/OPTIMISE.md). Next: HAP R and HAP HDR (BC7, BC6H), each node fetching its own pick, the farm over idle peers, `ASK` with suggestions, the lossless rewriter, estimates and removing versions | ★★★ | M | R2.3, R2.11 |
| R2.5 | Linear FP16 pipeline; 10-bit outputs (R10G10B10A2). HDR signalling is separate (M21) | ★★ | M | R2.1 |
| R2.6 | `PREFLIGHT` verb: open every asset's decoder per node, disk and upload tests, 60 s presentation soak, EDID check, non-deterministic sources listed; per-node stream budget in STATE/MCP. **Built in part:** `PREFLIGHT [QUICK]` and `PREFLIGHT REPORT` with each package's section and a fix line per row (docs/SHOW.md); not the soak, the upload tests, the EDID check or the stream budget | ★★★ | M | R2.3 |
| R2.7 | Measured sync: `pattern.syncmarks` plus a camera give inter-node offset p50/p99 and a "sync certificate" in the show report | ★★★ | M | R1.11, two nodes |
| R2.8 | .NET determinism: zero allocations per frame (test-enforced), GC latency mode during shows, ReadyToRun warm-up, GC pauses correlated with missed vblanks. **Built in part:** tests hold a warm frame to zero allocations; modulated layers, surfaces and effect slots still allocate a drawn project each frame | ★★ | S | R0.6 |
| R2.9 | `HARDENING` report: power plan, updates, notifications, driver allow-list, HAGS/MPO, independent flip, TdrDelay, disk; safe fixes on approval; offered after the first frame | ★★ | M | R2.1 |
| R2.10 | Frame-rate handling: per-output refresh choice, cadence report for 23.976/25/29.97/50 fps content, VRR/G-Sync off, mixed refresh rates refused within a sync group | ★★ | M | R2.1 |
| R2.11 | GPU contention policy: inference, transcode and scan decoding prefer the desk or an idle node, run at low priority on a show node in rehearsal, and are refused on an armed node. **Built for transcode:** the optimise worker runs at a lowered CPU priority, trickles while a show plays (a rest after each frame keeps its work to 15 % of the time), pauses after a dropped frame and waits while armed, and `MEDIA OPTIMISE` is refused on an armed node; not inference or scan decoding, nor moving work to the desk or an idle node | ★★ | S | R0.1 |

**Exit:** published layer counts per GPU for HAP, HAP Q, H.264 and ProRes; a 72-hour soak across two outputs with zero missed vblanks; a measured two-node sync offset.

### Round 3: Plays with others

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R3.1 | Bindings layer: input → verb or `SET` path with range, curve and soft takeover; right-click **Bind…** learn; feedback capped at 50 Hz; encoder support for fine geometry. **Built** (`BIND`, docs/CONTROL.md) but the desk's right-click **Bind…** | ★★★ | M | R1.8 |
| R3.2 | OSC in/out with a verb-mirroring address space and OSCQuery; MIDI 1.0; Art-Net/sACN input. **Built:** OSC in and feedback out, MIDI 1.0 in, Art-Net and sACN in; not OSCQuery or MIDI out | ★★★ | M | R3.1 |
| R3.3 | Spout in/out; OMT in/out; NDI 6.3.x as an optional plugin through the user-installed runtime; NDI/OMT out per output plus multiview; NDI PTZ cameras as scan cameras | ★★★ | M | R2.1 |
| R3.4 | Companion module generated from `/api/verbs` and STATE, with connection status. **Built:** the node's side (docs/COMPANION.md); the module itself is in flight | ★★ | S | R1.8 |
| R3.5 | Show Mode (`SHOW ARM`), blind edit with commit at a boundary, panic outcomes, locks, live actions out of undo, risk gating by caller, armed permission table (docs/UX.md §7.1). **Built** (docs/SHOW.md) but blind edit, which is in flight | ★★★ | M | R0.2 |
| R3.6 | Playlist ergonomics: per-item behaviours, on-start/on-end verb actions, load vs take, countdown colours, non-deterministic badge, optional grid view | ★★ | M | R1.10 |
| R3.7 | Timecode chase as a fitted anchor (LTC on audio in, MTC, Art-Net TC, Stagewatch LTC over the Show Bus), explicit freewheel and offset sign, TC-locked playlists, per-item keyframed `SET` tracks. **Built** (docs/TIMECODE.md) but Stagewatch LTC over the Show Bus and the keyframed tracks | ★★★ | M | R3.2 |
| R3.8 | `/remote` PWA: role pages, QR pairing, roles and armed permissions, dark-room show strip, object cards | ★★★ | M | R3.5 |
| R3.9 | `FIDUCIAL FLASH`, `REGISTER`, phone point-and-tap (iPhone freeze-frame, Android WebXR), beam guard | ★★★ | M | R3.8, R1.9, R1.11, R1.12 |
| R3.10 | Laser-pointer pick through the rig camera (XR Phase 0.5) | ★★ | S | R1.12, R1.11 |
| R3.11 | Ctrl+K palette (natural language falls through to a staged assistant proposal), last-action panel generated from verb parameters, universal micro-gestures, Simple/Pro switch | ★★ | M | R1.8 |
| R3.12 | "Bring my setup": import Resolume Advanced Output XML and MPCDI | ★★ | M | R1.8 |
| R3.13 | "Send to designer": per-surface UV templates (PNG/PSD/SVG) with names, sizes and safe areas; re-import by surface ID | ★★ | S | R1.7 |
| R3.14 | Network and AI security posture: pairing required for remote verbs, MCP off until enabled, firewall profile, assistant context treats names, metadata and web content as data. **Built in part:** pairing for every verb that changes something, foreign Origins and Hosts refused on the web port, secrets kept out of lines and the journal; MCP is on by default | ★★★ | S | R3.5 |

**Exit:** PlayMap maps a Resolume Spout feed onto scanned surfaces, is driven from an APC40 and a Stream Deck, chases LTC, and a phone and a laser pointer each select a surface by pointing at it.

### Round 4: Show-floor grade

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R4.1 | Mirror redundancy **for deterministic sources**: the backup renders the identical frame; fenced leader election; frame-health auto-takeover with a timeout; `FAILOVER` verb; routing by projector input or router; no automatic switch-back mid-show; live editing continues after failover. Non-deterministic sources are flagged in preflight | ★★★ | L | R2.7, R1.3 |
| R4.2 | NVAPI swap groups and barriers; per-node chip (house-locked / framelocked / free-running); counter-wrap handling; sync-card firmware caveats in preflight. **Built in part:** the per-output lock machine, `SYNC APPLY/LEAVE/REJOIN`, wrap-safe counters; the NVAPI tier is compile-only and joins no swap group until an output presents through DXGI (R2.1; docs/OUTPUT.md) | ★★★ | M | R2.1 |
| R4.3 | Fleet view in the Network drawer with 24 h history; `/metrics`, syslog, webhooks; one-click diagnostics bundle. **Built in part:** `HEALTH`, the fleet board (`FLEET`) and the `/health` and `/fleet` feeds on the web port (docs/FLEET.md); not the drawer, the history, `/metrics`, syslog, webhooks or the bundle | ★★ | M | R2.6 |
| R4.4 | Audio device seam (WASAPI exclusive, ASIO), per-playlist channel matrix, LTC out, audio clock as master. **Built in part:** the input side, one capture per input shared by the analyser and the LTC reader (docs/AUDIO.md); no sound output yet | ★★ | M | R3.7 |
| R4.5 | Scheduler (time of day, holidays, sunset-relative, evaluated by the leader), projector power sequencing with warm-up, boot-to-show service | ★★ | S | R1.3 |
| R4.6 | Versioned wire (capabilities in HELLO) and project format with migrations; N-1 interop in CI; LTS show-safe channel with pinned FFmpeg and a driver matrix; never auto-update an armed node | ★★★ | M | — |
| R4.7 | Rescan retargeting when the mesh changes: NRICP mesh deform with crease stiffness; deviation and lattice-residual heat maps | ★★★ | L | R1.4 |
| R4.8 | Calibration as a service: `CALIB PLAN/CHECK/RESOLVE/ACCEPT/REVERT` on the Show Bus and MCP; reference gate; history of the last 100 results. **Built in large part** as `CALIBRATION` and `ALIGN` on the wire and MCP with a gate; the history keeps 20 results beyond the current, fallback, staged and pinned ones | ★★★ | M | R1.4, R4.7 |
| R4.9 | Reference hardware list (GPUs, capture cards, mini-PCs, cameras, projectors known to work) and a venue network guide (wired show network, multicast/IGMP, VLANs, Wi-Fi limits for phones and glasses); mDNS discovery. **Built:** the node announces itself as `_playmap._tcp`; not the list or the guide | ★★ | S | R2.6 |

**Exit:** a failover drill shows frame-identical output on the backup (deterministic sources, checked by camera) and a measured end-to-end switch time. On projectors with a seamless backup input the target is no visible gap; otherwise the target is the projector's own re-lock time, which we report rather than hide. A projector is knocked and re-aligned within the UX §5.0 target with no mask rework. A night-time unattended check runs.

## 3. Mid term (after Round 4)

| ID | Item | Group | Impact | Effort | Depends on |
|---|---|---|---|---|---|
| M1 | Offline planner: projector and lens library; lux, density, stretch, shadow and acuity heat maps; calibration simulation; PDF/CSV | Differentiator | ★★★ | L | R4.8 |
| M2 | Content-based drift watch: render the predicted camera view, match it (permissively licensed extractor plus LightGlue-class matcher), per-projector residual, human-approved re-solve | Differentiator | ★★★ | L | R4.8 |
| M3 | Multi-camera capture and parallel multi-projector scanning (footprint probe, non-overlapping projectors at once), seam-aware bundle adjustment. **Built in Core:** the joint solve of many views of many projectors, dense depth per projector, the merged mesh (docs/SCAN-MULTIVIEW.md); no verb yet | Parity with high end | ★★★ | L | R1.1 |
| M4 | Art-Net 4/sACN output and pixel mapping sampled in linear light. **Built:** fixtures on canvases, outputs, surfaces and sources sampled on the GPU in linear light and sent as Art-Net 4, sACN or DDP with the latch (docs/LED.md); not world sampling, GDTF/MVR import or `LED DISCOVER` | Parity | ★★ | L | R3.2 |
| M5 | LED pixels auto-located by camera (blink-coded, decoded like structured light) | Differentiator | ★★ | L | M4, R1.1 |
| M6 | Audio analysis (FFT, onset, BPM) as a modulation source broadcast with show time; Ableton Link; launch quantisation. **Built:** analysis, onsets, tempo detection, the beat clock and the modulation bus on one node (docs/AUDIO.md); not the broadcast, Link or quantisation | Parity (VJ) | ★★ | M | R4.4 |
| M7 | ISF 2.0 loader as generator and filter; per-surface effect stack; a curated shader library. **Built:** ISF filters in chains on outputs, layers, surfaces and playlist items, ten built in (docs/EFFECTS.md); not ISF generators | Parity (VJ) | ★★ | L | R3.1 |
| M8 | Geometry-aware effects as live instruments (outline chase, relight, sweep driven by beat and MIDI) | Differentiator | ★★ | M | M6, M7 |
| M9 | Export: mesh, UVs and projector cameras to glTF/FBX/USD; UV-stable re-import; finished MPCDI; deterministic `EXPORT OUTPUT` | Parity (3D artists) | ★★★ | M | R4.7 |
| M10 | Quest 3/3S client (Passthrough Camera API), posed headset scan for scale; then Galaxy XR with gaze hover and pinch-the-dots | Differentiator | ★★★ | L | R3.9 |
| M11 | Spatial show-health overlay anchored to surfaces (observer mode, safe while armed) | Differentiator | ★★ | M | M10 |
| M12 | Voice input with deixis bound to the highlighted surface | Differentiator | ★★ | M | R3.9 |
| M13 | DeckLink via COM API, SRT/RIST inputs, WHEP previews, WHIP camera ingest | Parity | ★★ | M | R3.3 |
| M14 | Equirect, cubemap and domemaster sampler modes; Gaussian-splat import as previs | Differentiator | ★★ | M | R2.1 |
| M15 | Defocus-aware blend weights; winner-takes-pixel for low-confidence regions; radiometric bake and gamut overlay | Differentiator | ★★ | M | R1.2 |
| M16 | macOS desk driving a Windows node | Parity | ★★ | M | R4.6 |
| M17 | Multi-desk roles, per-entity soft locks, presence colours, "who changed this" in the journal | Parity | ★★ | L | R3.5 |
| M18 | OpenTrackIO and PSN inputs; tracked props late-latched and predicted to vblank | Differentiator | ★★ | M | R2.1 |
| M19 | Appliance node profile (fanless mini-PC, headless boot-to-show) | Parity | ★ | S | R4.5 |
| M20 | Onboarding: "Light a wall", three demo projects, embedded offline help, night-red theme. **Built in part:** `LIGHT WALL` and `STATE.onboarding` | Parity | ★★ | S | R1.6 |
| M21 | HDR signalling: PQ/HLG, scRGB, per-output nit target, HDR10 metadata | Parity | ★ | M | R2.5 |
| M22 | Colour matching across projectors: colorimeter or spectro support, white-point and gamut matching, OCIO | Parity (install) | ★★ | L | R1.4 |
| M23 | Facade workflow: long-throw pins at 50–150 m, night scanning, projector stacking with sub-pixel alignment (docs/UX.md §5.6) | Parity (commercial) | ★★★ | L | R1.2, M1 |
| M24 | MadMapper project import (format undocumented) | Adoption | ★★ | L | R3.12 |
| M25 | FFGL plugin host | Parity (VJ) | ★★ | M | M7 |
| M26 | Laser/ILDA output; LED processors (Brompton, Novastar, Megapixel) in mixed rooms, including genlock with processors | Parity | ★ | L | R4.2 |
| M27 | Push warp and blend into projector internal engines (Panasonic, Epson, Christie, Barco) for no-PC playback and redundancy | Differentiator | ★★ | L | R1.3 |
| M28 | People-aware projection: mask presenters' faces in keynotes (camera/depth segmentation, a generalised beam guard); people tracking for museum interactives | Parity | ★★ | M | R1.7, M18 |
| M29 | Camera-glasses client: Ray-Ban/Oakley Meta via the Device Access Toolkit; head camera as pointer, temple tap, voice | Differentiator | ★★★ | M | R3.9 |
| M30 | Text-prompted click-to-surface (`SURFACE FROM-TEXT`) with a verified SAM 3-class export after licence review | Differentiator | ★★ | M | R1.7 |
| M31 | Localisation of the desk and help | Adoption | ★ | M | R0.2 |
| M32 | Public benchmark: lab scan datasets and simulator scenes so anyone can verify accuracy claims | Credibility | ★★ | M | R1.4 |

## 4. Long term and moonshots

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| L1 | **Looks-and-controls with glasses:** XREAL Aura, Vision Pro (pinch-the-dots; enterprise camera build), Android XR AI glasses; Ray-Ban Display HUD remote | ★★★ | XL | M10, M29 |
| L2 | Content-as-fiducial registration: worn cameras stay registered during shows with no flashes | ★★★ | XL | M2, M10 |
| L3 | Imperceptible continuous alignment from complementary-pair embeds at 120 Hz and above; research track | ★★ | XL | M2 |
| L4 | ST 2110 / IPMX via Rivermax or Deltacast, NMOS IS-04/05, PTP-disciplined show time | ★★ | XL | R4.2 |
| L5 | Multi-wearer crews with shared anchors; drift alerts routed to whoever is looking | ★★ | L | L1 |
| L6 | macOS engine (Metal, VideoToolbox zero-copy, Syphon) | ★★ | XL | M16 |
| L7 | Render-engine integration: frame-numbered texture sharing with the sync anchor for Unreal, Notch and TouchDesigner; NotchLC GPU decode if the SDK terms allow | ★★ | L | R3.3 |
| L8 | Geometry-conditioned AI content: scan, depth and edge maps as conditioning to a pluggable generator, local by default | ★ | L | M9 |
| L9 | Live collaborative editing in a browser (Moonshine-class) | ★★ | L | M17 |
| L10 | Splats as playable content; web pages as a loose-sync source class with "capture to HAP" (never for YouTube) | ★ | L | M14 |

## 5. Non-technical track: sustainability and adoption

These are the biggest adoption risks and no code fixes them. Rental houses and integrators buy support, training and confidence, not just software.

| Item | Why | When |
|---|---|---|
| Second maintainer and a documented release process | A single-maintainer project has a bus factor of one; MapMap shows what unmaintained looks like | Before the first pro release |
| Documentation site with workflows, the hardware list and the network guide | Bring-your-own-hardware needs a buyer's guide | Round 4 |
| Support offer (paid SLA option), training and a certification path | What rental houses and integrators actually buy | After Round 4 |
| Funding model | Pays for the lab, signing certificates and support | Decide before Round 4 |
| Freedom-to-operate review of calibration patents (Christie, Scalable, VIOSO/domeprojection and others) and model licences | "Detect and propose" is a product choice, not a legal conclusion | Before a commercial release |
| Community: forum, example projects, public benchmark (M32) | Trust comes from reproducible numbers | Ongoing |

## 6. Skip or watch

- AV2, VVC, LCEVC, ProRes RAW and 4D splats: no hardware, or a camera/OTT concern (docs/COMPETITIVE.md §9).
- Dante AV video natively: ingest through capture cards instead.
- A full timeline engine: replaced by TC-locked playlists and per-item keyframes.
- Per-frame neural compensation on show nodes: lab-grade and single-projector ([CGF survey](https://onlinelibrary.wiley.com/doi/10.1111/cgf.70288)).
- Automatic content-based correction without human approval. Christie holds patents in this area, including drift correction from reduced pattern sets ([US20200092524](https://patents.justia.com/patent/20200092524)). We detect and propose, and a human approves; whether that is clear of any patent needs the review in §5.

## 7. Summary by group

**Parity gaps that block real shows (must-have):** R0 integration and signing (R0.1–R0.7); scan runner and solves (R1.1, R1.2); projector verbs (R1.3); scan preflight and fallbacks (R1.13, R1.14); D3D11 flip-model (R2.1); GPU HAP (R2.3); preflight (R2.6); frame-rate handling (R2.10); Show Mode (R3.5); bindings and OSC/MIDI (R3.1, R3.2); Spout/OMT/NDI (R3.3); timecode chase (R3.7); security posture (R3.14); redundancy (R4.1); framelock (R4.2); format versioning (R4.6); multi-camera capture (M3) for large rigs; the facade workflow (M23) for commercial mapping. Main has R0.1, R1.1, R1.2, R1.3, R2.3, R3.1, R3.2, R3.5 and R3.7 in code (each but what its row names), and R2.6, R3.14, R4.2 and M3 in part; what they lack is proof on real hardware.

**Differentiators where PlayMap can lead:** commodity-camera calibration with an explainable score (R1.4, R4.8); the edit overlay drawn on the wall (R1.5); pick-and-highlight verbs (R1.12); click-to-surface (R1.7, M30); optimise to HAP R/HDR (R2.4); measured sync and latency (R2.7, R1.11); laser and phone pointing (R3.10, R3.9); importers and designer templates (R3.12, R3.13); live-mirror redundancy for deterministic content (R4.1); rework-free recalibration (R1.2, R4.7); drift watch (M2); LED auto-locate (M5); geometry-aware instruments (M8); staged AI on a live show (R3.5 + assistant); camera glasses and headsets (M29, M10). Main has R1.4, R1.5 and R4.8 in large part, R2.4 in part (HAP and HAP Q, not HAP R or HDR), and camera auto-align of flat screens beside them.

**Moonshots:** L1–L3. Glasses as the way to run a mapped show, and a show that keeps itself aligned.

## 8. Measures of done, per release

Each is measured against the canonical targets in docs/UX.md §5.0 and published:
- Time to first light; time to map a box with a tested webcam.
- RMS per solve on the lab objects; the public benchmark (M32) once it exists.
- Two-node sync offset p50/p99.
- Layer counts per GPU and codec.
- 72-hour soak missed vblanks.
- Failover: frame identity and end-to-end switch time.
- Bump-to-realigned time.
- Laser, phone and pinch-to-outline latency p50/p95.

Published numbers count as features. They are what a show programmer asks for first.
