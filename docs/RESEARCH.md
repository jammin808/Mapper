# Research behind PlayMap (October 2026)

What the field does, what users complain about, and the choices PlayMap made because of it. Sources are linked
inline; claims marked *estimate* are engineering judgement, not measurements.

## 1. The market

| Tier | Products | Mapping approach | Camera calibration | Weak spots users name |
|---|---|---|---|---|
| Media servers | disguise, Pixera, Watchout 7, Modulo Kinetic, Smode, LightAct | 3D model + projector pose (point picking: QuickCal ≥ 6 points, 10–15 recommended); warp layers on top | disguise OmniCal (structured-light blobs, < 1 min capture, one-click re-solve when only projectors moved, an "OmniCal score" since r32.4); Pixera via an embedded VIOSO module; Watchout 7 NDI camera streams + HTTP API; Modulo "Autocalib 3D" | cost, hardware/dongle lock-in, planning effort; wrong lens-shift values ruin solves (Watchout forum) |
| Calibration specialists | VIOSO 7, Domeprojection (now VIOSO), Scalable Display (Desktop, Atlas), Christie Mystique, Panasonic Geometry Manager | camera-computed warp/blend, model-based 3D (Atlas imports CAD, solves projector positions, blends across shadows) | the product | separate tool, expert setup, per-channel licences |
| Creative / prosumer | MadMapper 6, Resolume Arena, HeavyM, Millumin 5, TouchDesigner (kantanMapper, camSchnappr), VPT 8, MapMap | mostly 2D slices + Bézier/mesh warp; MadMapper's 6-point object calibration; camSchnappr runs OpenCV calibrateCamera from ≥ 6 dragged points | MadMapper's Space Scanner (projector-as-scanner, mainly a mask aid) | "Resolume works in 2D", no camera auto-blend on 3D, steep learning curves |
| One-box / AI | Lightform LF1/LF2 (closed Aug 2022); 2026: Panasonic + Illumibot IllumiBeast, Appotronics AIMapper (InfoComm 2026 Best of Show), Artemis (UC Irvine spinout) | scan → projector-view "smart scan" → select surfaces → procedural effects that follow edges/normals | built in | Lightform: small scan area (~6–12 ft), low brightness, cloud dependency removed at shutdown |

**The opening PlayMap takes:** professional-grade automatic calibration with a webcam or phone and no special
hardware, a surface-anchored workflow so recalibration never destroys work, and full membership of the
Patterns/Stagewatch show network — local-first, no cloud dependency. PlayMap is becoming an installed, Windows-native
show node: one that uses what the hardware gives (Direct3D 12, the flip model, RTX and Radeon features, the hardware lock
where a board allows one), claims nothing it has not measured, and opens on guided workflows for the common jobs (§8).

## 2. Problems people have mapping 3D surfaces — and PlayMap's answer

| Problem | What PlayMap does |
|---|---|
| Re-work after recalibration (the top professional pain) | masks and warps are stored in **surface UV space**, not projector pixels; a rescan deforms toward the new data and keeps the content mesh's UVs |
| Projector-internal keystone/warp silently ruining a scan | PlayMap.Devices resets the projector's internal geometry (where the protocol allows) and says so when it can't, before every scan |
| Wrong lens-shift/zoom values | pose solves lock measured shift/focal (SolveLocks); projector lens positions are read over the control protocol where documented |
| Alignment drift (warm-up: run 15–20 min before aligning; outdoor wind; bumps) | drift watch: a fixed camera compares against the reference capture and offers a one-click re-solve of the projector that moved |
| Blending on 3D geometry (self-shadowing, uneven overlaps) | per-pixel visibility by ray casting, weights = edge falloff × incidence × distance, normalised in linear light, black-level pedestal to the deepest overlap |
| Brightness falloff with angle and distance | intensity compensation from the calibrated model (cos θ / d², vignetting) with a "uniformity vs brightness" slider |
| Depth of field across deep objects | scan modulation shows focus quality per region; blend weights can prefer the sharper projector (roadmap) |
| Webcam latency (C930e median 112 ms) and missed frames during scans | latency auto-measured with a white flash; settle detection; pipelined capture schedule |
| Glossy, dark, translucent materials | Gray code with inverse patterns (per-pixel comparison), phase shifting with modulation as confidence, a "difficult materials" mode with high-frequency codes, confidence maps and a quality score |
| Building scale (a camera pixel bigger than a projector pixel) | phase interpolation, multiple camera views, LiDAR/photogrammetry models registered by structured-light bridging |

## 3. Structured light and calibration (design inputs)

- **Patterns.** Gray code with inverses (46 images at 1080p, both axes, plus white/black) is the robust baseline;
  complementary Gray code fixes jump errors at code boundaries; N-step phase shifting (4 steps or more) gives
  sub-pixel coordinates plus modulation (a free confidence) and the DC term (a free albedo image); multi-frequency
  unwrapping is the most noise-robust (Zuo/Zhang reviews). Interreflection corrupts low-frequency patterns and
  subsurface scattering blurs high-frequency ones: XOR codes (Gupta et al., CVPR 2011), micro phase shifting
  (Gupta & Nayar, CVPR 2012) and embedded phase shifting (Moreno et al., CVPR 2015) stay high-frequency.
  Projector gamma makes fringes non-sinusoidal: a short gamma probe per projector fixes it.
- **Projector-space decoding** (Lightform's insight): the camera image resampled into the projector's raster is
  the "smart scan" — masks drawn on it are already in output pixels. One projector on an object needs no
  calibration at all.
- **Calibration without a checkerboard.** Dense camera↔projector correspondences on an arbitrary scene give a
  fundamental/essential matrix and, with bundle adjustment, intrinsics and extrinsics up to scale (Yamazaki,
  Mochimaru & Kanade, CVPRW 2011); scale from one known distance or a LiDAR model. Moreno & Taubin's local
  homographies when a checkerboard is used.
- **Projector lens model.** Pinhole with a large principal-point offset (lens shift) and Brown–Conrady
  distortion; applied as a pre-distortion of the output.
- **Pins.** DLT from ≥ 6 non-coplanar 2D↔3D pairs, PnP with unknown focal (known principal point), LM
  refinement, RANSAC to flag a bad pick, live re-solve while dragging (camSchnappr/mapamok style).
- **Meshing.** The decoded result is an organised cloud: triangulate grid neighbours directly ("projector-grid
  meshing"), cut at depth jumps; Poisson/ball pivoting only when merging views.

## 4. LiDAR and models

iPhone/iPad LiDAR (ARKit scene reconstruction with per-face classes; 1.4–3.9 cm indoors, flattens planes; under
5 m), Polycam/Scaniverse exports (OBJ, GLB, PLY, LAS, USDZ, SPZ splats), RealSense (spun out of Intel, 2025),
Orbbec Femto/Gemini, Livox Mid-360 for facades, terrestrial scanners and survey for precision. Registration,
strongest first: **structured light as the bridge** (decoded camera pixels ray-cast into the model give
thousands of projector-pixel↔3D pairs → pose solve, no manual pins), manual points + ICP, non-rigid fit (disguise
Mesh Deform; Christie's coarse-mesh patent). Feed-forward geometry (VGGT, Depth Anything 3 — small/base Apache-2.0,
large/giant non-commercial) gives instant rough previs from photos.

## 5. AI for mapping

SAM 2.1 (point/box prompts, ONNX exports exist; keep the previous-mask input for iterative clicks), SAM 3
(Nov 2025, text prompts return every instance — "all the windows"), EdgeTAM (on-device), Grounding DINO
(Apache-2.0); YOLO-World is GPL. Run locally through ONNX Runtime: CUDA/TensorRT first, DirectML is in
maintenance (Windows ML is its successor), CPU fallback. The click-to-surface pipeline: encoder once per scan,
decoder per click (milliseconds), snap the mask to depth discontinuities and normal creases (colour alone can't
separate same-coloured surfaces), vectorise to Bézier paths, lift to 3D with plane or mesh-region UVs.

## 6. Playback, sync and distribution

- **Decode:** FFmpeg 9.0 through FFmpeg.AutoGen 9.0.1.1 (package version tracks FFmpeg); hardware decode with
  D3D11VA on Windows (covers NVIDIA/Intel/AMD; D3D12VA on the renderer's Direct3D 12 device is the direction,
  docs/round3/PLAN.md §3.11), VAAPI/CUDA/Vulkan on Linux, VideoToolbox on macOS; YUV→RGB on
  the GPU honouring matrix/range/transfer; HAP decoded by uploading its BC blocks directly (FFmpeg's CPU HAP
  decoder defeats the purpose). Frame-accurate seek = seek backward, flush, decode-and-discard to the target.
  libVLC (no PTS out, its own clock) and libmpv (render API GL/software only) don't fit a frame-accurate
  multi-output engine.
- **GPU and presentation:** on Windows the lowest-latency path is engine-owned borderless windows with DXGI
  flip-model waitable swap chains (independent flip, one frame of latency), `GetFrameStatistics` to map vblanks.
  PlayMap draws through a rendering hardware interface (RHI) cut at `GlDevice`, with OpenGL 3.3 core or ES 3.00 behind it
  (testable headless on Mesa) and an experimental Direct3D 11 flip path through ANGLE; the direction is Direct3D 12 behind that
  interface, with OpenGL the reference backend until the pixel and timing evidence agrees (§8).
- **Sync:** Watchout 7 (NTP via w32time, "millisecond-level"; frame-exact needs NVIDIA sync boards), Pixera
  (sync cards + "RX Frame Sync"), disguise (genlock + QTP/VSync messages), Resolume/Millumin (OSC/LTC mirror —
  users report drift). PlayMap: own UDP exchange on the monotonic clock with min-RTT filtering and drift
  regression (LAN: tens to hundreds of µs, *estimate*), and **stateless frame selection** — the frame is a pure
  function of leader time at the predicted vblank plus the display's measured latency — so nodes never
  accumulate drift. Without genlock the ceiling is ±1 refresh: network time aligns which frame is drawn, not when it
  scans out. The same vblank needs sync hardware. AMD withdrew framelock and GeForce never had it, so that is an NVIDIA
  professional GPU with an RTX PRO Sync board, through the present barrier on Direct3D 12 or the `WGL_NV_swap_group` swap
  group on OpenGL. PlayMap's direction is that lock where the hardware allows it, and the software lock everywhere else: the
  same frame, its measured phase published (docs/round3/PLAN.md D5).
- **Distribution:** content-addressed storage (Watchout 7's Asset Manager pattern): chunked range pulls,
  resume, verify-then-rename, per-node readiness, throttled during shows.
- **Web/YouTube:** CEF off-screen with shared textures (CefSharp 154.x on Windows) is the lowest-latency browser
  path; YouTube only through the IFrame Player API (no downloading — ToS), synced by scheduled `playVideo` and
  threshold `seekTo` (50–200 ms across nodes, *estimate*).
- **Reliability:** process split (a desk fault never blanks a projector), single-writer lanes, immutable
  snapshots, zero-allocation frame paths, watchdog heartbeats, telemetry (missed vblanks, GPU time, clock offset).
- **AI observability:** the MCP C# SDK 2.2.0 (2026-07-28 spec, stateless HTTP); resources for state/context/
  project, tools for every verb, high-risk tools gated by a human.

## 7. Patterns and Stagewatch (the ecosystem PlayMap joins)

Read from the repositories (Patterns `stagewatch-video-control`, Stagewatch `main`): Show Bus 1.2 (hello on UDP
9928, receipts, `PING t1 → PONG t1 t2 t3`, the §6 context, pairing by HMAC proof, TLS pinned by fingerprint, the
§11 approval desk); Patterns' wire grammar and VIDEO verbs (round 86); Stagewatch's `/ws` frames, beats
(`live:false` when stuck — a case Patterns' own client misses), screens, triggers and proposals. Patterns has no
render-node concept yet (`docs/GENERALISATION.md`: postponed) and controls third-party boxes only through its
Interactive area — which PlayMap speaks. See `docs/SHOWBUS-NODE.md`.

## 8. Where PlayMap is going

The research behind the Windows-native direction is in `docs/round3/research/`: the direction note
(`windows-gpu-direction.md`) and the six notes it draws on — the graphics API (`graphics_api.md`), framelock and presentation
(`framelock_presentation.md`), vendor features (`vendor_features.md`), the Windows platform and packaging
(`windows_platform.md`), games' and medical devices' architecture (`gaming_medical_architecture.md`) and the desk's UI and UX
(`desk_ui_ux.md`) — and four beside them: Windows 11 Home (`windows11_home.md`), an installer with no paid service
(`installer_tools.md`), capture cards (`capture_cards.md`) and the workflows (`workflows_ux.md`). Round 3's plan turns them into
decisions (`docs/round3/PLAN.md` §1.2). What they found, in short:

- **Direct3D 12, behind an RHI.** New GPU features reach Windows through it first; it sits on DXGI's flip model and NVIDIA's
  present barrier; it has the deepest set of fault tools (PIX, DRED, Aftermath, Radeon GPU Detective) and GPU-less CI on WARP.
  OpenGL is in maintenance on Windows, so it stays only as the reference backend until parity is measured. Vulkan is not
  carried: its Windows presentation depends on each vendor.
- **The lock only where the hardware gives one.** Tearing between outputs is phase, and only sync hardware removes it (§6).
- **Every edition of Windows 11, Home included.** Home runs every API the engine and the desk use and lacks only the management
  layers (Assigned Access, Shell Launcher, specialized displays, the update policies), so each has a fallback and a preflight row.
- **A per-machine installer.** An MSI rolls back services and machine-level writes, and the WiX toolset built from its own source
  needs no paid service, but WiX v7.0.0's pinned source does not build on the Windows runner (M17: it needs Visual Studio's v143
  C++ tools, which the runner's Visual Studio 2026 lacks) and checks a licence gesture in three places that PlayMap never passes.
  So this round's installer is built with Inno Setup, an EXE without an MSI's rollback, repair or `msiexec`, and an MSI is
  revisited next round. The Agility SDK's licence lets its Direct3D 12 runtime ship inside the installer on its Distribution
  Requirements, among them to "require distributors and external end users to agree to terms that protect it and Microsoft at
  least as much as this agreement" and to indemnify Microsoft, with its Distribution Restrictions beside them. So the installer
  shows a licence page that carries Microsoft's own licence for `D3D12Core.dll` verbatim, PlayMap's own terms being the owner's to
  name, and the owner accepts Microsoft's distribution terms, the indemnity included (docs/round3/PLAN.md §9 decisions 3 and 23,
  which quotes the terms whole). Session 0 cannot drive displays, so the engine is to run in the show user's session under a
  supervising service.
- **What games and medical devices teach.** Nothing compiled, allocated or waited on during the show; the render graph compiled
  when the show changes; an observer, independent of the engine, of what the audience saw; device loss rehearsed; every hazard
  traced to a fault test.
- **The desk.** It keeps Avalonia; its previews are to move from CPU copies to shared textures, and it borrows the lighting
  consoles' safety patterns. Every command is to be in the menu bar, by Microsoft's rule that no command lives only in a
  context menu. Colour is to mark status, identity and stage, and never be the only cue (ISA-101 and IEC 60601-1-8 reserve
  alarm colours).
- **Capture cards.** A pass-through's floor is one input frame plus the render, so the direction is one frame in flight and
  the fewest copies the card and the path allow.
