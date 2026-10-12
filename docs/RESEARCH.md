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
Patterns/Stagewatch show network — local-first, no cloud dependency.

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
  D3D11VA on Windows (covers NVIDIA/Intel/AMD), VAAPI/CUDA/Vulkan on Linux, VideoToolbox on macOS; YUV→RGB on
  the GPU honouring matrix/range/transfer; HAP decoded by uploading its BC blocks directly (FFmpeg's CPU HAP
  decoder defeats the purpose). Frame-accurate seek = seek backward, flush, decode-and-discard to the target.
  libVLC (no PTS out, its own clock) and libmpv (render API GL/software only) don't fit a frame-accurate
  multi-output engine.
- **GPU and presentation:** on Windows the lowest-latency path is engine-owned borderless windows with DXGI
  flip-model waitable swap chains (independent flip, one frame of latency), `GetFrameStatistics` to map vblanks;
  PlayMap starts on OpenGL 3.3 (testable headless on Mesa) behind a device seam, with D3D11 next (ROADMAP).
- **Sync:** Watchout 7 (NTP via w32time, "millisecond-level"; frame-exact needs NVIDIA sync boards), Pixera
  (sync cards + "RX Frame Sync"), disguise (genlock + QTP/VSync messages), Resolume/Millumin (OSC/LTC mirror —
  users report drift). PlayMap: own UDP exchange on the monotonic clock with min-RTT filtering and drift
  regression (LAN: tens to hundreds of µs, *estimate*), and **stateless frame selection** — the frame is a pure
  function of leader time at the predicted vblank plus the display's measured latency — so nodes never
  accumulate drift; ±1 refresh without genlock, same vblank with NVIDIA present barrier (roadmap).
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
