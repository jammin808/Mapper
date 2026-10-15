# PlayMap — architecture

PlayMap is two things in one portable exe:

1. **A player / render node.** Standalone, it plays anything FFmpeg decodes (H.264/HEVC/AV1/VP9/ProRes/DNxHR/HAP/
   NotchLC…), stills, image sequences, web pages and YouTube embeds, generators (test patterns, fractals,
   particles, reactive scenes) and live inputs, from any number of independent playlists, in frame-accurate step
   with other PlayMap nodes on the network. On the Show Bus it is a node of the Patterns ecosystem: Patterns (and
   Stagewatch, and an AI through MCP) can see everything it is doing and drive everything it can do.
2. **A 3D projection-mapping suite.** Scan an object, a room or a building with structured light (any webcam, a
   phone, NDI) and/or import a LiDAR or photogrammetry model; get projector poses, a mesh and surfaces in minutes;
   click an object to make it a surface; play media through the warped 3D mesh with automatic blends; fine-tune
   with pins, Bézier pen masks and warp lattices that stay attached to surfaces when you rescan.

The UI hides the power: three workspaces (**Play**, **Map**, **Scan**) plus **Outputs** and **Network** drawers;
right-click menus on everything, in step with the desk's buttons and the wire's verbs.

## 1. Processes — one exe, two crash domains

```
PlayMap.exe                 the desk (Avalonia). Starts and watches its engine; or attaches to any node's engine
                            on the network (operate a rack render node from a laptop).
PlayMap.exe engine          the node: project, playback, decode, render, outputs, network, scan. Owns its main
                            thread (output windows + GL context live there). Headless: `engine headless`.
```

- A desk that faults or closes never blanks a projector: the engine keeps playing. The desk restarts and
  re-attaches; the engine restarts (watchdog) and puts back the recovery record (project, playlists' transports,
  outputs) and "seeks to now" — trivial, because every frame is a pure function of show time (§4).
- **The desk has no private API.** It drives the engine through the same verbs Patterns, Stagewatch and an AI
  use (docs/WIRE.md): the wire on loopback for commands, the state stream for facts, shared memory for preview
  pictures. Anything the desk can do, a peer can do; anything a peer sees, the desk sees.

## 2. Modules (the module rules are fences: `ModuleRulesTests`)

| Module | References | Holds |
|---|---|---|
| `PlayMap.Core` | the runtime only | numerics (SVD, LM, Pcg32), time (mono clock, offset estimator, network clock, timecode), the model (immutable `Project` tree, `ProjectStore` single writer with undo), playback logic (timeline anchor, clip transport, playlist cursor, frame selection), the wire grammar and verb catalogue, Show Bus models (hello, context, pairing, receipts), the generator catalogue, geometry (mesh, ray caster, camera model), **mapping maths** (warp mesh, Bézier masks, blend solver, pose solver), **scan maths** (structured light encode/decode, calibration, triangulation, point clouds, segmentation, registration), contracts (`IVideoSource`, `WarpMeshGeometry`, `BlendMap`) |
| `PlayMap.Render` | Core, Silk.NET.OpenGL/Windowing, SkiaSharp | GL device wrapper, render graph, shader library, generators (Skia-drawn test patterns → textures; shader fractals, reactive, effects; CPU particle sim → instanced sprites), video upload (YUV → RGB on the GPU), the projector pass (stage meshes from the projector's pose), warp, blend, black level, colour, preview read-back |
| `PlayMap.Media` | Core, FFmpeg.AutoGen 9.0, Silk.NET.OpenAL | probe, decode (hw: D3D11VA/VAAPI/CUDA/VideoToolbox; sw fallback), frame-accurate seek, preroll, gapless loop, stills and sequences, capture devices (avdevice), content hashing, audio out slaved to the timeline |
| `PlayMap.Net` | Core, ASP.NET Core (shared framework), ModelContextProtocol.AspNetCore | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar), HTTP/WS/HTTPS on 9940 (TLS sniffed), MCP at `/mcp`, pairing and TLS pinning, fine clock sync (UDP 9942), media transfer, the Patterns and Stagewatch links, the §11 approvals pull |
| `PlayMap.Engine` | all of the above | composition root: the command router (verbs → edits/actions), playback lanes, output windows, preview shared memory, scan runner, journal, recovery record, watchdog heartbeat |
| `PlayMap.App` | Core, Engine (for `engine` mode only), Avalonia 12 | the desk |

Edges never reference each other except through Core's contracts (Render does not know FFmpeg; Media does not
know GL). Only App references Avalonia.

## 3. The document and the air

- `Project` is an immutable record tree (`System.Collections.Immutable`). An edit is `Func<Project, Project>`
  applied by `ProjectStore` — **one writer**, in the engine. The new root shares every untouched branch; undo is a
  list of roots; drags coalesce into one undo step. Readers (render thread, network, state writer) take the
  current root through a volatile reference and never lock.
- Runtime state (transports, links, outputs' health, availability) is not in the document: each lane publishes
  its own immutable snapshot; STATE composes them.
- Generic edits (`GET/SET/ADD/REMOVE/MOVE` by id and dotted field path) are how the desk edits anything and why an
  AI can too; specific verbs exist where an edit is a verb (VIDEO PLAY, SCAN START, PROJECTOR SOLVE).

## 4. Time and sync — content is a pure function of show time

- Every node has a monotonic clock (`MonoClock`, ns). A sync group has a **leader**; followers estimate the
  leader's clock with an NTP-style exchange on UDP 9942 every 250 ms (min-RTT filtering + drift regression,
  `ClockOffsetEstimator`; slewed, never stepped). Show Bus links use the same estimator on `PING t1` lines (ms).
- A playlist's transport is a `TimelineAnchor` (media time M at leader time L, rate, running). Commands carry an
  optional start instant (`AT +200`): every node in the group gets the same anchor and starts on the same vblank.
- The render loop predicts its next vblank, adds the output's measured display latency, maps it to leader time
  and asks `FrameSelector` which frame to show. No node integrates a frame delta, so none drifts; a dropped
  render frame heals itself. Without genlock the honest ceiling is ±1 refresh between machines; with NVIDIA
  sync hardware (roadmap) it is the same vblank.
- An item that comes in with a Fade or a Dip of T overlaps the item it follows: its window is the outgoing item's
  last T before the switch (cut to what both can give), the incoming item is anchored to start in it (at its
  opening for a fade, its middle for a dip) and prepared ahead, and the renderer mixes the two pictures in linear
  light (`Transitions`). The window is a function of the two anchors, so every node mixes the same pictures at the
  same instant. A GO taken by hand cuts.
- Audio is slaved to the timeline by resampling (±0.5 %). It plays through the OpenAL Soft that ships beside PlayMap,
  found in the app's own native folders before the system's (`Media/Audio/OpenAlLibrary`); a node where no OpenAL loads
  plays on the null output, whose status says why.

## 5. Rendering

Per frame, on the engine's render thread (one GL context, all outputs):

1. **Sources**: upload the frame each visible video source has for its media time (planes → textures,
   YUV→RGB in the shader with the frame's matrix/range/transfer); render generators whose parameters or time
   changed; Stagewatch timer views.
2. **Canvases**: composite layers into canvas render targets (normal/add/multiply/screen, opacity).
3. **Per output**, by mode:
   - *Projector*: render the stage's surfaces from the projector's pose (intrinsics → off-axis projection,
     `CameraModel.GlMatrices`), each surface textured by its content (UV, canvas region, or projection from a
     viewpoint), surface masks applied in UV space; lens distortion as a pre-distortion pass.
   - *Canvas*: the canvas rectangle. *Source*: one source full-frame.
4. **Output stage**: 2D warp (corner pin + Catmull-Rom/Bézier lattice tessellated by `WarpMeshBuilder`), output
   masks (feathered Bézier paths), blend map × signal + black-level pedestal (linear light, through the inverse
   gamma), colour trim, then present (vsync).
5. **Previews**: requested views (desk viewports, output thumbnails) are rendered to targets and read back
   asynchronously (PBO ring) into the preview shared memory.

Backends: OpenGL 3.3 core via Silk.NET now (testable headless on Mesa); the device is behind a small seam so a
Direct3D 11 flip-model backend (waitable swap chains, `GetFrameStatistics`, D3D11VA zero-copy) slots in on
Windows (roadmap, docs/ROADMAP.md).

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
  object have nearly meeting optical axes and two views cannot then tell focal length from depth. SCAN APPLY puts
  the scan on the stage as one undoable edit; SCAN SURFACE makes a surface of a click on the smart scan. The camera
  "sim" runs the same pipeline on the scan simulator — demo mode and the tests scan with no hardware. The node
  composes it through the host's hooks (`EngineParts.Ops.cs` → `OpsParts`): the runner is the scan backend over the
  render host's display, and projector control, the assistant (its queue the host's MCP approvals) and the runner's
  binding are extensions; the runner feeds the camera and scan preview views on the render host's `PreviewViews`.
- **LiDAR / model import**: OBJ, PLY (mesh and points), XYZ/PTS, LAS; register by three picked points
  (Umeyama) then point-to-plane ICP; or bridge with structured light: decoded camera pixels ray-cast into the
  model give thousands of 2D↔3D pairs per projector → `PoseSolver`.
- **Surfaces**: RANSAC planes and click-to-select region growing (depth- and normal-aware) on the scan; an
  optional ONNX segmenter (SAM-class) refines masks. A surface is a mesh face set with UVs; masks and warps are
  stored in surface UV space so a rescan keeps the work.
- **Pins**: 3D↔2D pairs per projector; DLT (≥ 6) or PnP with locked shift/focal + LM refinement; RANSAC flags
  a bad pick; per-pin residuals shown live while dragging.
- **Blend**: per output pixel, which projectors see the point (visibility by ray casting), weights by distance to
  the projector's frame edge × incidence angle × distance falloff, normalised to one in light, through the gamma;
  black-level pedestal to the deepest overlap's floor.

## 7. Network — a node of the Show Bus (docs/SHOWBUS-NODE.md)

- **Hello** every 2 s on UDP 9928, `t:"playmap"`, caps `video video.clock video.transport video.seek video.end
  state context receipts clock mcp playlist media mapping scan`, top-level `web`/`wire` for older readers.
- **Wire** on TCP 9941 in Patterns' grammar: `STATE` then `NONCE` on connect, `HELLO`, `AUTH-PROOF`, `@id`
  receipts, `PING t1` → `OK PONG t1 t2 t3`, `CONTEXT`, the VIDEO verbs with Patterns' meaning, and the rest of
  the vocabulary. STATE carries Patterns' `video` row (positionMs, lengthMs, rate, seq…) so **Stagewatch's VT link
  drives PlayMap by pointing it at PlayMap's host and port**. Patterns' Interactive "Lines" devices can drive it
  too: their `KEY VALUE` fact lines (BLACKOUT 1, LIVE 0…) are read as facts, never answered.
- **HTTP/WS/HTTPS** on 9940: `/api/state`, `/api/context`, `/api/cmd`, `/api/verbs`, `/api/media/<hash>` (range
  transfers between nodes), `/ws` (state frames with v/seq/id), `/tls`, `/tls.cer`, `/mcp` (Model Context
  Protocol: resources for state/context/project, tools for every verb, high-risk tools gated).
- **Links**: follows a Patterns desk as a wire client (its clip clock, blackout); follows a Stagewatch over `/ws`
  as a screen (`cid`, `page=/playmap`), renders its timer as a source, handles `beat.live:false`, posts its
  context, collects approved proposals (§11).
- **Media sync**: library items are identified by content hash; a node missing a file pulls it in 8 MiB chunks
  from any peer that has it, resumable, throttled while a show runs; each node publishes readiness (present,
  verified, decodable, first frame ready).

## 8. Reliability rules (inherited from Patterns, enforced by the analyzers and tests)

Attempts are not facts (receipts say accepted/applied and the effect); fail closed on the wire; one writer per
lane; immutable snapshots for readers; pools and rings that skip rather than wait; no allocation on the frame
path; a faulting generator draws an error card and is disabled until its settings change; blackout is checked
before any content code; FlashGuard limits flashes to three a second.
