# PlayMap — architecture

PlayMap is two things in one portable exe:

1. **A player / render node.** Standalone, it plays anything FFmpeg decodes (H.264/HEVC/AV1/VP9/ProRes/DNxHR/
   NotchLC…), the HAP family block-compressed on the GPU, stills, image sequences, network streams, generators (test
   patterns, 2D and raymarched 3D fractals, a GPU particle field, reactive scenes) and live inputs, through chains of
   ISF effects, from any number of independent playlists, in frame-accurate step with other PlayMap nodes on the
   network, a playlist chasing timecode if asked, and makes HAP versions of clips it decodes on the CPU in the
   background. On the Show Bus it is a node of the Patterns ecosystem: Patterns (and Stagewatch, controllers, and an
   AI through MCP) can see everything it is doing and drive everything it can do.
2. **A 3D projection-mapping suite.** Scan an object, a room or a building with structured light (a capture camera, or
   the scan simulator) and/or put a LiDAR or photogrammetry model on the stage; get projector poses, a mesh and surfaces
   in minutes; click an object to make it a surface; play media through the warped 3D mesh with automatic blends;
   fine-tune with pins, Bézier pen masks and warp lattices that stay attached to surfaces when you rescan. On a plain
   wall, a pixel quad from four clicks; on a flat screen, one camera measures each projector's warp and blend.

The UI hides the power: three workspaces (**Play**, **Map**, **Scan**) plus **Outputs** and **Network** drawers;
right-click menus on everything, in step with the desk's buttons and the wire's verbs.

## 1. Processes — one exe, three roles

```
PlayMap.exe                 the desk (Avalonia). Starts and watches its engine; or attaches to any node's engine
                            on the network (operate a rack render node from a laptop).
PlayMap.exe engine          the node: project, playback, decode, render, outputs, network, scan. Owns its main
                            thread (output windows + GL context live there). Headless: `engine headless`.
PlayMap.exe optimise-worker the node's HAP encoder for optimise on import. The engine starts and watches it; it lowers
                            its own CPU priority, makes its own GL context (hidden, or EGL surfaceless with no
                            display) and speaks one JSON object a line on its standard input and output.
```

- A desk that faults or closes never blanks a projector: the engine keeps playing. The desk restarts and
  re-attaches; the engine restarts (watchdog) and puts back the recovery record (project, playlists' transports,
  outputs) and "seeks to now" — trivial, because every frame is a pure function of show time (§4).
- A node that stops takes no more lines from the moment it begins to: a line from any source (the wire, the web,
  OSC, MIDI and DMX bindings, the assistant, an approval) is refused with the words, so no source that closes late
  starts work on a part already closed; the node's own lines (a panic's shutters and effects) still run. Then the
  doors and parts close in reverse, the recovery record is written and the journal flushed.
- A worker that stops never touches the air: the engine starts it again after a backoff (1 s, doubling to 30 s) and its
  job starts again from its first frame; a clip that stops it three times fails with the words. It ends when its input
  closes, and after two minutes with nothing to do (docs/OPTIMISE.md).
- **The desk has no private API.** It drives the engine through the same verbs Patterns, Stagewatch and an AI
  use (docs/WIRE.md): the wire on loopback for commands, the state stream for facts, shared memory for preview
  pictures. Anything the desk can do, a peer can do; anything a peer sees, the desk sees.

## 2. Modules

| Module | References | Holds |
|---|---|---|
| `PlayMap.Core` | the runtime only | numerics (SVD, LM, Pcg32), time (mono clock, offset estimator, network clock, timecode: LTC, MTC and the chase), the model (immutable `Project` tree, `ProjectStore` single writer with undo), playback logic (timeline anchor, clip transport, playlist cursor, frame selection), the wire grammar and verb catalogue, Show Bus models (hello, context, pairing, receipts), the show policy, locks and panic maths, preflight, the generator catalogue, its looks and the particle schedule, geometry (mesh, ray caster, camera model), **mapping maths** (warp mesh, pixel quads, Bézier masks, blend solver, pose solver), **scan maths** (structured light encode/decode, calibration, triangulation, point clouds, segmentation, registration, the multi-view solve), align's fits, the calibration score, the ISF parser and translator, audio analysis, the beat clock, the modulation bus and plan, OSC/MIDI/DMX and binding maths, the frame-lock machine, LED layouts, patching and packets, fleet health and the Eye's graph, optimise's planner, recipe, governor, HAP packer (PlayMap's own Snappy compressor) and version choice, contracts (`IVideoSource`, `WarpMeshGeometry`, `BlendMap`, `IHapBlockEncoder`) |
| `PlayMap.Render` | Core, Silk.NET.OpenGL/Windowing, SkiaSharp | the GPU platform (OpenGL 3.3 core or ES 3.00; GLFW windows, EGL, or EGL surfaceless) with a presenter per output, its vblank clock and present ledger, the frame-lock tiers; GL device wrapper, render graph, shader library, generators (Skia-drawn test patterns → textures; shader fractals, raymarched fractals, the GPU particle field, reactive, effects), video upload (YUV → RGB on the GPU; HAP blocks through the `hap` pass), ISF effect chains, the projector pass (stage meshes from the projector's pose, then pixel quads), warp, blend, black level, colour, align's measured stage, the edit overlay, LED sampling, preview read-back, the GPU HAP encoder (`GlHapEncoder`: fragment programs that write one texel per 4×4 block) |
| `PlayMap.Media` | Core, FFmpeg.AutoGen 9.0, Silk.NET.OpenAL, NAudio.Wasapi | probe, decode (hw: D3D11VA/VAAPI/CUDA/VideoToolbox; sw fallback), the HAP parser and its Snappy stage on a worker pool, frame-accurate seek, preroll, gapless loop, stills and sequences, capture devices (avdevice), sound input (avdevice; WASAPI with loopback on Windows), content hashing, the HAP transcode (decode, encode through a frame encoder it is handed, mux with the source's audio stream-copied, read back) |
| `PlayMap.Net` | Core, ASP.NET Core (shared framework), ModelContextProtocol.AspNetCore | Show Bus hello (UDP 9928), the line wire (TCP 9941, Patterns' grammar; and over `/ws` as `playmap.lines.v1`), HTTP/WS/HTTPS on 9940 (TLS sniffed), MCP at `/mcp`, pairing and TLS pinning, fine clock sync (UDP 9942), OSC (UDP 9943), Art-Net (UDP 6454) and sACN (UDP 5568) sockets, the LED sender (Art-Net, sACN, DDP), the mDNS responder, the fleet monitor and the `/health` and `/fleet` feeds, media transfer, the Patterns and Stagewatch links, the §11 approvals pull |
| `PlayMap.Devices` | Core | projector control (PJLink and the makers' own protocols; docs/PROJECTORS.md), the machine's MIDI ports (ALSA sequencer, WinMM) |
| `PlayMap.Vision` | Core, ONNX Runtime | segmentation (classical, and an optional ONNX model) for surfaces and regions on the smart scan; depth previs, pin suggestions and drift watch as library code |
| `PlayMap.Assistant` | Core, a provider's client library | plain words → verbs, the proposals list, secret redaction |
| `PlayMap.Engine` | all of the above | composition root: the command router (verbs → edits/actions) and the packages' hooks, the show gate, playback lanes, timecode chase, control bindings, output windows, preview shared memory, scan runner, align runs, the calibration ledger, the optimise service (its queue, governor and worker process), journal, recovery record, watchdog heartbeat |
| `PlayMap.App` | Core, Engine (for `engine` mode only), Avalonia 12 | the desk |

Core references only the runtime; each edge references Core and its own native library, and never another edge except
through Core's contracts (Render does not know FFmpeg; Media does not know GL). Only App references Avalonia.

## 3. The document and the air

- `Project` is an immutable record tree (`System.Collections.Immutable`). An edit is `Func<Project, Project>`
  applied by `ProjectStore` — **one writer**, in the engine. The new root shares every untouched branch; undo is a
  list of roots; drags coalesce into one undo step. Readers (render thread, network, state writer) take the
  current root through a volatile reference and never lock.
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

## 4. Time and sync — content is a pure function of show time

- Every node has a monotonic clock (`MonoClock`, ns). A sync group has a **leader**; followers estimate the
  leader's clock with an NTP-style exchange on UDP 9942 every 250 ms (min-RTT filtering + drift regression,
  `ClockOffsetEstimator`; slewed, never stepped). Show Bus links use the same estimator on `PING t1` lines (ms).
- A playlist's transport is a `TimelineAnchor` (media time M at leader time L, rate, running). Commands carry an
  optional start instant (`AT +200`): every node in the group gets the same anchor and starts on the same vblank.
- The render loop predicts its next vblank, maps it to leader time (plus a host-wide display-latency setting, 0 by
  default) and asks `FrameSelector` which frame to show; while an output is frame-locked (`SYNC APPLY`) the instant is the
  lock anchor's time of the pacer's next count, the same integers on every node that holds the anchor. No node integrates a
  frame delta, so none drifts; a dropped render frame heals itself. Without genlock the honest ceiling is ±1 refresh between
  machines; a sync board makes it the same vblank once outputs join a swap group, which this build's NVAPI tier
  (compile-only) does not do while they present through GL (docs/OUTPUT.md).
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
  same instant. A GO taken by hand cuts.
- No engine part plays sound yet: clips play silent. The air's sound gain (master × panic) is computed from the same
  fades as the picture for the part that will. Sound comes in: one capture per input feeds the analyser and the LTC
  reader.

## 5. Rendering

Per frame, on the engine's render thread (one GL context, all outputs). Blackout wins before any pass:

1. **Sources**: upload the frame each visible video source has for its media time (planes → textures,
   YUV→RGB in the shader with the frame's matrix/range/transfer; HAP blocks as BC textures through the `hap` pass);
   render generators whose parameters or time changed; Stagewatch timer views. A playlist item's picture goes through
   its effect chain while its playlist shows it, each side of a transition before the mix.
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
5. **Output stage**: align's measured warp grid first, then the 2D warp (corner pin + Catmull-Rom/Bézier lattice
   tessellated by `WarpMeshBuilder`), output masks (feathered Bézier paths), blend map × signal + black-level pedestal
   (linear light, through the inverse gamma — or align's measured blend and pedestal through the output's measured γ),
   colour trim, dither, and last the air's gain (master × panic), so 0 is black. Then the identify card and the edit
   overlay (outlines, handles, lattices, cursors, through the output's warp; nothing while armed or on a black air).
6. **LEDs**: once every output is drawn, each LED fixture samples its canvas, output, surface or source in linear light and
   its DMX slots are made on the GPU, read back behind fences, never waited on (docs/LED.md).
7. **Present** through the output's presenter; its vblank clock and present ledger record which refresh showed each
   picture (missed vblanks, held and late presents; docs/OUTPUT.md).
8. **Previews**: requested views (desk viewports, output thumbnails) are rendered to targets and read back
   asynchronously (PBO ring) into the preview shared memory.

A warm frame with no modulated layer, surface or effect slot allocates nothing (tests hold it for static frames, a test
pattern, a projector at another raster, effect chains and an unchanged measured set). The render thread reads the air's
levels and the cursor board lock-free; effect compiles (on a shared second context), measured-file reads, frame-lock board
polls and machine sampling run off it.

Backends: a platform seam (`IGpuPlatform`, one `IOutputPresenter` per output) over OpenGL 3.3 core or OpenGL ES 3.00
through Silk.NET — GLFW windows, EGL, or EGL surfaceless with no window system (testable headless on Mesa). A
Direct3D 11 flip-model presenter (waitable swap chains, `GetFrameStatistics`, D3D11VA zero-copy) is the next path on
Windows; asked for today (`dxgi`), the node runs GL and says so (docs/ROADMAP.md).

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
- **LiDAR / model import**: OBJ, PLY (mesh and points), XYZ/PTS, LAS as a mesh on the stage; Core registers by three
  picked points (Umeyama) then point-to-plane ICP, with no verb on it yet; or bridge with structured light: decoded
  camera pixels ray-cast into the model give thousands of 2D↔3D pairs per projector → `PoseSolver`.
- **Surfaces**: RANSAC planes and click-to-select region growing (depth- and normal-aware) on the scan; an
  optional ONNX segmenter (SAM-class) refines masks. A surface is a mesh face set with UVs; masks and warps are
  stored in surface UV space so a rescan keeps the work.
- **Pins**: 3D↔2D pairs per projector; DLT (≥ 6) or PnP with locked shift/focal + LM refinement; RANSAC flags
  a bad pick; per-pin residuals shown live while dragging.
- **Blend**: per output pixel, which projectors see the point (visibility by ray casting), weights by distance to
  the projector's frame edge × incidence angle × distance falloff, normalised to one in light, through the gamma;
  black-level pedestal to the deepest overlap's floor.
- **The quad on the wall**: `LIGHT WALL` puts an output full screen with a nominal projector; `SURFACE QUAD` makes a
  pixel quad from four wall clicks, taken back through the output's warp, and its handles move by verb with one undo
  step per burst (docs/CALIBRATION.md).
- **Score and ledger**: every calibration result carries one explainable score (terms, grade, fix line). Results are
  immutable snapshots recorded in the same edit as the change; the first applied is the fallback. `CALIBRATION RECALL`
  puts a kept one back exactly, `FALLBACK` the fallback, `ACCEPT` applies a staged one; `CALIBRATION RESOLVE` re-solves
  only the named projectors from their pins, applying within the gate or staging the result (docs/CALIBRATION.md).
- **Camera auto-align of flat screens** (`ALIGN`): off the lane, against the simulator or a real display and camera —
  footprints, photometry and structured light per output, a joint lens-plus-homography fit that refuses a screen that is
  not flat, a measured warp grid and blend per output; `ALIGN ACCEPT` applies them as one edit through the ledger
  (docs/ALIGN.md).
- **Many views**: Core solves many camera views of many projectors together, anchored on the projectors' pixels, into a
  dense depth map per projector and a merged mesh; no verb drives it yet (docs/SCAN-MULTIVIEW.md).

## 7. Network — a node of the Show Bus (docs/SHOWBUS-NODE.md)

- **Hello** every 2 s on UDP 9928, `t:"playmap"`, caps `video video.clock video.transport video.seek video.end
  state context receipts clock mcp playlist media mapping scan eye show`, top-level `web`/`wire` for older readers.
- **Wire** on TCP 9941 in Patterns' grammar: `STATE` then `NONCE` on connect, `HELLO`, `AUTH-PROOF`, `@id`
  receipts, `PING t1` → `OK PONG t1 t2 t3`, `CONTEXT`, the VIDEO verbs with Patterns' meaning, and the rest of
  the vocabulary. STATE carries Patterns' `video` row (positionMs, lengthMs, rate, seq…) so **Stagewatch's VT link
  drives PlayMap by pointing it at PlayMap's host and port**. Patterns' Interactive "Lines" devices can drive it
  too: their `KEY VALUE` fact lines (BLACKOUT 1, LIVE 0…) are read as facts, never answered.
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
  from any peer that has it, resumable, throttled while a show runs; each node publishes readiness (present,
  verified, decodable, first frame ready).
- **Media versions**: an item may carry versions (`MediaVersion`, `mv…`): HAP files the optimise worker made from its
  source, each hashed into this node's media store and recorded on the item as one edit. At each open the media backend
  asks `VersionChoice` which file to play — under `use: auto` a version this GPU samples whose file is here, the best
  measured first, else the source; `MEDIA USE` pins the source or a version — so a change reaches the air at the item's
  next open (docs/OPTIMISE.md).

## 8. Reliability rules (inherited from Patterns, enforced by the analyzers and tests)

Attempts are not facts (receipts say accepted/applied and the effect); fail closed on the wire; one writer per
lane; immutable snapshots for readers; pools and rings that skip rather than wait; no allocation on a warm frame
(a frame whose layers, surfaces or effect slots are modulated still builds its drawn project afresh); a faulting
generator draws an error card and is disabled until its settings change; blackout is checked before any content code;
FlashGuard limits flashes to three a second.
