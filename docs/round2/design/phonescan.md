# Work package "phonescan": phone scan, fully local

*Design for round 2, 13 October 2026. The research behind it is `r2/research/phonescan.md`, cited below as R§n. The
browser measurements come from `r2/bench/phonescan/`: headless Chromium 141, fake cameras, a self-signed HTTPS origin on
a LAN address, all run in this container. The code read is `main` at c12b186. Numbers marked "estimate" have not been
measured.*

Any phone with a browser becomes PlayMap's scan camera. The operator scans a QR code on the desk; the node serves the
camera page itself, over its own certificate, on the show network. The phone locks what its platform lets it lock. It
reads a small **tag** that the node draws into every pattern, so a picture is paired with its pattern by observation
rather than by guessing at Wi-Fi timing. It sends only the settled picture, losslessly. The operator can move the phone
to more places; each place is one more **view**, and the node solves every projector and every view together, anchored
on the projectors, into one 3D frame with curved geometry. Surfaces are suggested from measured geometry and a local
segmenter. An assistant may name and rank them from text alone, and a person approves each one on the phone or the
desk. Nothing leaves the building.

This package also closes the carry-over items in its area (CARRYOVER.md, ops integration):

- Phone **and Folder** cameras are refused today.
- Scan pictures from before a restart cannot be used.
- Regions and the smart scan refer to the first projector only.
- A real scan without a known distance is not metric.
- A camera that fails to open leaves a blank view with no words.

The Folder camera (tethered DSLR and manual "next") is the dependency `facade.md` §6 names. The phone's exposure and
white-balance lock is the one `autoalign.md` §6 names.

---

## 1. Goal and the bar

| What the best do (R§) | PlayMap's answer | Match or beat |
|---|---|---|
| **Ghost Arcade 2.0.17**: an **iOS app** paired to the desktop. Stripes "about fifteen seconds per projector"; phone "near the projector… keep it still". It finds **flat** surfaces only ("Curved… Not yet") and returns 4-corner layers. "Not been tried with two projectors at once" (R§1, 9 Oct 2026) | **No install**: a link on Android Chrome and iPhone Safari. Target **≤ 15 s per projector per view** (M4). **Curved** surfaces from a dense per-projector depth map (M6). **Many projectors from many views** in one solve (M6). The phone can stand anywhere that sees the light, not only beside the lens | **Beat** on install, curved and multi-projector; **match** on time |
| **Illumibot**: phone "2–3 inches above the projector lens", patterns photographed "on a stand": "**Allow up to 10 minutes**". 2D alignment (photo, markers, corner pins). AI content in the cloud behind a **subscription and tokens** (R§1) | Minutes become seconds. Geometry is 3D (pose and lens of every projector), so content moves between surfaces and projectors without re-alignment. **No cloud, no subscription**: PlayMap is MIT and runs on the node | **Beat** |
| **Appotronics AIMapper** (InfoComm 2026 Best of Show): structured patterns through "cameras or **mobile devices**", 3D reconstruction, "sub-pixel auto alignment and edge blending", curved and domes, AI content. Where it runs is not stated (cloud **U**) (R§1) | Same chain (patterns → 3D → projectors → blend). Fully local. Any projector maker. A **score that explains itself** (per-view and per-projector RMS, coverage, pairing facts). AI that **proposes and never acts** | **Match** on geometry and blend; **beat** on locality, openness and safety |
| **Artemis** (Summit Tech Lab): "a few cameras… push of a button", SaaS licence, scheduled recalibration (R§1) | Fixed cameras already work (webcams, NDI, streams). The phone adds a **zero-hardware** path. Drift watch and scheduled re-scan are the autoalign and score packages' | **Match** with fixed cameras; **beat** on cost of entry |
| **Lightform** (closed 2022): built-in 4K camera, Gray code, one projector, 1–3 m, cloud services ended with the company (R§1) | Every byte local, an open protocol (`playmap.camera.v1`), any camera | **Beat**: nothing to switch off |
| **Every phone-scan product**: a native app on the phone | iOS Safari **cannot lock exposure or focus** from the web (WebKit main, R§2.2). PlayMap's **AE-steady plan** keeps auto-exposure still instead: every coded frame is at 50 % mean, references come from the coarsest Gray pair, and the gain is normalised per frame. STATE says which path ran. A native client is scoped for later (L1) | **Match** on iPhone (estimate: to be measured in the R0.5 lab); **beat** on Android, which locks exposure, focus and white balance explicitly |
| **Category gap**: "safe AI" | Suggestions are **measured** regions (planarity, depth edges, segmentation agreement, which projectors light them). An AI only **names and ranks** them from text descriptors (no pixels leave the node). The engine queues each as a `SCAN SURFACE` **proposal** that a person approves. **No wall preview while the node is armed** (show safety). Every applied surface is one undo step | **Beat**: nothing in the category documents any of this |
| disguise (from the brief): "a calibration score that explains itself" | Per view and projector: RMS (px), track count, coverage seen once and twice, pairing ("tag" observed versus "timing" inferred), retakes, rolling-shutter rejects, exposure path. Each red item carries a fix line ("add a view that sees the red area", "use a stand") | **Match**; the score package composes it |
| Scalable and VYV (from the brief): "re-solve only the projector that moved" | `SCAN SOLVE <scan> HOLD <projector>…` re-solves with the held projectors fixed at their stage poses. One new view that sees the moved projector and a held one is enough (§3.10) | **Match** |

**The three things the category does not do, which this package does:**

1. Fully local, with the page served by the node.
2. Several projectors on curved surfaces, solved from several phone positions in one frame.
3. Safe AI: measured suggestions, text-only naming, a person approves, never on the air while armed.

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **Phone link and pairing.** `/scan` page served by the node (embedded files, strict CSP). `PHONE INVITE` mints a one-time ticket shown as a QR code and URL, with the ticket in the URL **fragment**. The ticket is exchanged for a per-phone key, kept **hashed** on the node in `phones.json`. The phone gets a **scoped capability**, never the node token. `PHONE LIST`, `PHONE FORGET`. STATE `phones[]` | `PhoneHubTests`: a ticket is single-use and expires (a fake `TimeProvider` at +10 min 1 s gives `ERR ticket expired`). `phones.json` contains no key bytes, only `sha256:` digests. Five wrong keys in a minute lock the address out (TokenGate's rule). Playwright: across the page's whole session **no request URL, header or body contains the ticket**; it arrives only in the first WS message. STATE, CONTEXT, the journal and the log never contain the ticket or a key (string search over all four after a full scan) |
| M2 | **Camera page core** (`src/PlayMap.Net/Phone/www`): secure-context check with words; choice of a **physical back camera** (label heuristics, reported); capability probe; `devicemotion` (with the iOS permission prompt on the Allow tap); `wakeLock`; clock exchange (`ping`/`pong` → `ClockOffsetEstimator`); low-rate preview on demand; reconnect with the stored key | Playwright, default fake device: `hello` carries `caps.exposure:["manual","continuous"]`, `exposureTime:[10,100,5]`. Y4M device: `caps.exposure:[]`. Plain HTTP on a LAN address: the page says "PlayMap's camera page needs the node's https address" and shows the https link, with no camera request (verified on the bench, R§2.1). After a socket drop the page reconnects with its key within 3 s, with no new invite |
| M3 | **Exposure, focus and white balance by platform.** *Lock profile* (Chrome Android): let auto-exposure settle on a 50 % stripe probe, read `getSettings()`, compute `exposureTime`/`iso` for a p99.5 lit level of 215, apply **explicitly** (R§2.2: Chrome keeps the last AE exposure but sets no ISO), lock focus and white balance, verify once. *Steady profile* (iOS, or any camera without the controls): no full white or black in the coded run, synthesised references, white balance locked where offered, per-frame gain normalisation, drift measured | Default fake device ends with `exposure:"locked"` and `getSettings().exposureMode=="manual"`. White balance answers "not offered by this camera". Y4M device ends with `exposure:"steady"` and words that name the platform limit. Simulator (Core): a camera whose gain random-walks 1 %/frame (AE hunting) decodes ≥ 90 % of the pixels the locked profile decodes, and the pose stays within 2× the locked thresholds |
| M4 | **Observed pairing.** A **cell-locate** sequence (12 frames) finds where each projector's candidate **tag sites** land in the camera. Every pattern then carries its tag (4×4 cells, 7 differential pairs: 6 bits and parity) at 2–3 sites spread across camera rows. **Lock-step**: the node shows pattern *i+1* when the phone reports pattern *i* **seen** (tag matches at every valid site in two consecutive frames that also agree, i.e. settled). Uploads trail, at most 6 in flight. Timing (capture time on the node clock ≥ present + measured latency) is the cross-check and, without sites, the fallback. Rolling-shutter mixes are rejected | Core: tag encode/decode round-trip; every single-bit flip is caught by parity. A frame whose top site reads *i* and bottom site *i−1* is rejected. E2E (browser, §7): 0 mispaired frames over a scan with **injected** late frames, a dropped frame and one half-and-half frame; STATE `scan.pairing = {mode:"tag", rollingRejects ≥ 1, retakes ≥ 1}`. **Time per projector ≤ 15 s** on the simulator path (30 fps camera, 120 ms injected glass-to-capture latency, Robust mode, 42 frames + 12 locate + 4 latency) |
| M5 | **Upload protocol** `playmap.camera.v1`: JSON text messages and binary `PMF1` frames (gray8/gray16, `deflate-raw`, optional ROI), on a WebSocket at `/scan/ws`. A fallback over the same origin: `POST /scan/frame` and `GET /scan/events` (server-sent events). Hard limits; **unknown message types close the socket** (fail closed); backpressure | `PhoneProtocolTests`: a 16 MiB + 1 binary → close 1009; text > 64 KiB → 1009; `{"t":"nope"}` → 1008 "unknown message". The fallback path completes a scan when Playwright's `routeWebSocket` refuses the socket. A frame's pixels arrive bit-identical (CRC32 in the header checked) |
| M6 | **Multi-view, multi-projector, curved.** `SCAN START <camera> … INTO <scan>` adds a view. Tracks are keyed by **projector pixel**: each view's decode is inverted to camera positions on a 16-px projector grid. A **projector-anchored bundle adjustment** solves every projector, every view and the phone's intrinsics. Dense **per-projector depth maps** come from all views; the meshes are merged; coverage is reported per projector. `SCAN SOLVE`, `SCAN VIEWS`, `SCAN DROP` | Simulator (Core, no browser), *wall-and-cylinder*, 2 projectors, 3 views, phone focal prior 8 % off, projector throw priors 5 % off: every projector within **5 mm, 0.05°, throw 0.2 %** (the thresholds `ScanEngineTests` already holds a single view to); phone focal within 0.5 %. Dense surface RMS to the truth mesh ≤ 3 mm (estimate; tightened after the first run). Coverage of the lit raster ≥ 95 % with 3 views against ≤ 80 % for the best single view (the cylinder hides part). `SCAN DROP` of the bad view (one with a 2 px synthetic shift) brings RMS back under 0.5 px |
| M7 | **Per-projector smart scans and regions** (carry-over): regions per projector on its own raster, merged across projectors by 3D overlap. `SCAN SURFACE … [ON <projector>]`; STATE regions carry `projector` | Sim with 2 projectors: a box face lit by both appears **once** with `projectors:[pj1,pj2]`; `SCAN SURFACE sc… REGION rg… ON pj2` makes a surface whose faces are those P2 sees |
| M8 | **Scans survive a restart** (carry-over): views (`view.json`, tracks, the white picture, the tag-shadow mask) and per-projector depth maps are written under `scans/<id>/views/` and `scans/<id>/depth/`. `INTO`, `SOLVE`, `SURFACE` and `APPLY` all work after a restart | Engine test: scan → node stop → start → `SCAN START … INTO sc…` adds a view and re-solves; `SCAN SURFACE` works; the folder is ≤ 60 MB for 2 projectors × 3 views at 1080p (estimate) |
| M9 | **Safe AI surface suggestions.** `SCAN SUGGEST <scan> [ASSISTANT]` ranks the measured regions with reasons. The **engine** queues each as a proposal (`SCAN SURFACE <scan> REGION <id> [ON <pj>] [<name>]`, Review), not the assistant's tool loop, so `RunReviewWithoutAsking` cannot apply them. With `ASSISTANT`, the node's assistant receives **text descriptors only** and returns names and ranking. A phone with approval rights approves on the phone. No wall preview while the node is armed | Test: with `RunReviewWithoutAsking=true`, `SCAN SUGGEST sc… ASSISTANT` leaves every suggestion `Pending` until `APPROVE`. The provider fake records its request: no base64 or binary payload, and only the fields in §4.4's `SuggestionDescriptor`. An AI label for a region the geometry did not find is shown grey ("AI only — not measured") and has no proposal. While armed, `SCAN COVERAGE … SHOW` and the wall preview answer `ERR armed …` |
| M10 | **Closed-loop browser E2E** in this container: render host on Xvfb (llvmpipe), `SceneCamera` lighting the simulator's scene with what the output really shows, frames pushed into the page as a **synthesised camera track** (`MediaStreamTrackGenerator`), the real page pipeline (locate, tags, settle, exposure state machine, compression, upload), the real `PhoneHub`, `ScanRunner` and solver | `PhoneScanE2ETests` (`[BrowserFact]`): an Android profile (fake capabilities, locks honoured) and an iOS profile (no capabilities, a camera AE model hunting ±3 %). Single projector: pose within `OpsRenderScanTests`' 10 mm / 0.25° / 0.6 % throw. Two views INTO one scan: both views solve, RMS ≤ 0.5 px. Each run ≤ 120 s |
| M11 | **Folder camera** (carry-over; facade's dependency). `tether`: each new image file in the folder is the picture for the current pattern; the node advances on arrival. `manual`: the node waits for `SCAN NEXT` (desk, phone, Companion key). JPEG/PNG/TIFF 8/16-bit through FFmpeg's image decoders. The tag check applies to photos too | Engine test with a fake tether that copies simulator frames into the folder after each present: the scan completes and its pose matches the simulator run. A photo of the wrong pattern (copied twice) is refused with "photo 17 shows pattern 16: re-shoot" and the scan waits. 16-bit TIFF arrives as gray16 |
| M12 | **Desk**: the scan wizard's "A phone" path (QR code, fingerprint words, capability chips, live preview), views, coverage, suggestions with approve and reject; right-click entries | `ScanWizardTests` (desk with fake engine): Phone → INVITE → QR shown; joining moves to step 2 with the phone selected; suggestion cards render reasons and send `APPROVE ap…` |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | `SCAN COVERAGE <scan> SHOW\|HIDE` puts each projector's coverage on the wall (green seen twice, amber once, red never), so "aim at the red" works with no screen | Render test: SHOW puts the coverage raster on the output; HIDE restores the show; refused while armed |
| S2 | **Metric scale** (carry-over): `SCAN SCALE <scan> <metres> <x1> <y1> <x2> <y2> [ON <pj>]` (a tape measure on the smart scan), and `SCAN SCALE <scan> PHONE <phone>` (Chrome Android WebXR depth at projected dots, 0.5–5 m, R§2.6) | Sim: a 1.000 m tape gives scale within 0.3 %. The PHONE path is checked against a recorded depth fixture (no ARCore here); compile and unit level only |
| S3 | **Node-local root CA**, name-constrained to RFC 1918, link-local, CGNAT ranges and `.local`, issuing the node's leaf, plus a `/tls` install flow for phones. Only after a security review | Unit: the constraints extension is present and critical; a leaf for `example.com` is refused by our own verifier; .NET chain building with the CA as custom trust passes |
| S4 | **USB mode** (Android): `PHONE USB` runs `adb reverse tcp:<port> tcp:<port>` when `adb` is found, so the page loads from `http://localhost:<port>/scan`: secure, installable, with a service worker | Compile and unit (command line built and parsed); hardware in the lab |
| S5 | **Frame averaging and ROI**: `FRAMES n` sums n settled frames as gray16 (facade's `FrameAverager` contract); ROI crop after locate; 4K decode on the ROI with a grid origin | Sim: noise σ 4 levels → decoded fraction with n=4 ≥ n=1 + 5 points; 4K ROI upload ≤ 35 % of the full frame's bytes |
| S6 | **Words in camera views** (carry-over): a phone or capture camera that is not delivering puts its reason in the view and in STATE (`"Pixel 8 is not connected — open the invite on the phone"`) | View test: SHOW camera:<phone rig> with no phone → the view carries the words, and STATE `views[].words` agrees |
| S7 | **Companion**: variables, feedbacks and `SCAN NEXT` / `SCAN CANCEL` actions (§8) | Companion package's test harness |
| S8 | `SCAN IMPORT <camera> <folder>`: decode an offline image set by tags alone (taken while an earlier `SCAN START … MODE … WRITE-PLAN` ran) | Sim folder with shuffled filenames decodes identically |
| S9 | **Tag-hole fill**: a second short pass with the tag sites swapped, so the ~0.9 % of the raster under sites is decoded too | Sim: decoded fraction under the sites ≥ 95 % |
| S10 | **Colour frames on request** for autoalign's photometry (I420 → RGB at the node), with exposure and white balance locked; refused on the steady profile ("colour matching needs a camera that locks exposure") | Fake-device E2E delivers `CameraFrame.Rgb`; the steady profile refuses with words |

### Later

| # | Item |
|---|---|
| L1 | A **native iOS client** (manual exposure through `AVCaptureDevice`, LiDAR `sceneDepth` for metric scale) that speaks `playmap.camera.v1` unchanged |
| L2 | WebTransport uploads (HTTP/3 on Kestrel, `serverCertificateHashes`) |
| L3 | Phone point-and-tap pick and highlight (docs/XR.md Phase 1) on the same link |
| L4 | TSDF fusion or Poisson meshing of the per-projector depth maps |
| L5 | **Two phones in one view**: the lock-step waits for both phones' `seen`, giving two views per pattern run. The protocol already allows it |

---

## 3. Architecture

### 3.1 Components and where they live

```
 phone (browser)                         node: PlayMap engine process
 ───────────────                         ─────────────────────────────────────────────────────────────────
 /scan PWA (embedded in PlayMap.Net)     PlayMap.Net
  app.js ── UI, state machine              HttpHost ─ GateAsync ─▶ IWebModule "/scan" (PhoneWebModule)
  frames.worker.js ── MSTP→Y plane,                                   │ static files (CSP, no-store)
     tag read, settle, crop, deflate                                  │ /scan/ws  (playmap.camera.v1)
  exposure.js ── lock / steady profiles                               │ /scan/frame, /scan/events (fallback)
  clock.js ── ping/pong                    PhoneHub ── tickets, keys (phones.json, hashed), sessions,
  protocol.js ── JSON + PMF1                            limits, clock (ClockOffsetEstimator), IPhoneSession
        │  wss:// same origin                  ▲
        └──────────────────────────────────────┘
                                         PlayMap.Engine
                                           Phone/PhoneExtension (IEngineExtension "phones"): makes PhoneHub,
                                              adds the web module to NetworkHost before transports open,
                                              Services.Phones, PHONE verbs, STATE.phones, context
                                           Scanning/PhoneCamera : ICuedScanCamera (adapts IPhoneSession)
                                           Scanning/FolderCamera : ICuedScanCamera (tether | manual)
                                           Scanning/ScanRunner: cued capture path, views INTO a scan,
                                              multi-view solve off the lane, one edit at the end
                                           Scanning/ScanSuggestions: ranking, descriptors, proposals
                                         PlayMap.Core
                                           Scan/Tags: FrameTag, TagSite, TagOverlay, CellLocate
                                           Scan/ReferenceSynthesis, Scan/GainNormaliser
                                           Scan/InverseCorrespondence, Scan/TrackSet
                                           Calibration/MultiViewCalibration (projector-anchored BA)
                                           Scan/ProjectorDepth (dense per projector), Scan/CoverageMap
                                           Scan/Phone/PhoneProtocol (messages, PMF1 header), Bus/QrMatrix
                                         PlayMap.Media: StillImageReader (FFmpeg image2 → gray8/gray16)
                                         PlayMap.App: scan wizard phone path, QR view, suggestion cards
```

The module rules hold:

- Core references only the runtime: protocol records, tags and maths.
- Net references Core and ASP.NET Core: the web module and the hub.
- Engine references both: the adapters and the verbs.
- App references Avalonia: the desk.

### 3.2 Threads and lanes

| Work | Where | Why |
|---|---|---|
| WS receive per phone | one Kestrel request task per socket | Same pattern as `WebSocketSession` |
| WS send per phone | a `Channel<Outgoing>` (bounded 64) drained by one task; `seen`/`cue` ahead of previews | One sender per socket, latest-wins for previews |
| Frame decompression | `Task.Run` per binary message, ≤ 2 in parallel per phone | `DeflateStream` (`System.IO.Compression`, raw deflate) costs about 10 ms/MB (estimate); kept off the receive loop |
| Cued capture | the scan's own task (`ScanRunner.RunAsync`) | Unchanged: one scan at a time |
| Locate decode, tracks, BA, dense depth | `Task.Run` off the control lane, cancellable, with progress in STATE.scan | Carry-over: no long work on the lane |
| Project edits (camera rig on join, scan record, views) | the control lane (`engine.Lane.RunAsync` → `Store.Apply`) | One writer |
| Desk previews | `ScanViews.CameraFrame` (≤ 25 Hz, existing) | Unchanged |

### 3.3 The camera page

**Files** (≈ 120 KB uncompressed; plain ES2022 modules with `// @ts-check` and JSDoc, checked by `tsc --noEmit --allowJs
--checkJs`, no bundler, no third-party code):

| File | What it does |
|---|---|
| `index.html` | One page, five screens: Join, Check, Aim, Scan, Review/Surfaces. Night-red theme |
| `app.js` | State machine (below), UI, the `cmd` buttons |
| `protocol.js` | Message builders and parsers; `PMF1` framing; CRC32 |
| `clock.js` | `ping` every 1 s (every 250 ms during a scan); the node does the estimate and returns `clock {offsetMs, uncertaintyMs}` |
| `camera.js` | Device choice, `getUserMedia`, the capabilities snapshot, the preview |
| `exposure.js` | Lock and steady profiles (§3.6) |
| `frames.worker.js` | A Dedicated Worker. Takes the transferred `MediaStreamTrack` (or `ReadableStream<VideoFrame>`) and reads the Y plane by `VideoFrame.copyTo` using `visibleRect` (the bench caught coded 640×480 against visible 640×360). Tag reading, settle check, ROI crop, gray16 accumulation, `CompressionStream('deflate-raw')` |
| `fallback.js` | rVFC plus a 2D canvas (`willReadFrequently`) when `MediaStreamTrackProcessor` is absent; reads luma from G |
| `motion.js` | `devicemotion` → still or moving (‖a − g‖ > 0.15 m/s² or ω > 3 °/s for > 100 ms, estimate); `wakeLock` |
| `manifest.webmanifest`, `icon-192.png`, `icon-512.png` | Installable (Chrome ≥ 108 needs no service worker, R§2.8) |
| `sw.js` | Registered only on a trusted origin (fails harmlessly otherwise, R§2.1): caches the shell |

**State machine**:

```
JOIN ──Allow tap (gUM + motion permission + wake lock)──▶ CHECK ──hello/welcome──▶ READY
READY ──prepare──▶ PREPARING ──prepared──▶ READY
READY ──locate cue──▶ LOCATING ──sites──▶ READY
READY ──cue──▶ WATCHING ──settled──▶ (send seen, queue upload) ──▶ READY
any ──moved──▶ HELD (banner; the node fails the view) ;  any ──socket lost──▶ RECONNECTING (key) ──▶ READY
```

**Camera choice**: `enumerateDevices()` after permission, then back-facing devices. A device whose label names one
physical lens is preferred over a "dual", "triple" or "virtual" device (iOS lens switching, **U** R§6). Zoom is 1 where
offered and the torch is off. The label, resolution and frame rate are reported, and the node refuses to share
intrinsics between views whose label or resolution differ. The request is
`{width:{ideal:1920}, height:{ideal:1080}, frameRate:{ideal:30}, resizeMode:"none"}`. Difficult mode asks for 3840×2160
and turns ROI on (S5).

### 3.4 Pairing and security

**Invite**:

1. A paired session (never `assistant`/`mcp`/`approval` transports) sends
   `PHONE INVITE [<name>] [FOR <minutes>] [CAMERA-ONLY]`.
2. `PhoneHub.Invite` mints a **ticket**: 20 random bytes in base32, 32 characters, single use, expiring after
   `NodeSettings.PhoneInviteMinutes` (default 10).
3. It answers that session only, with
   `{url:"https://<addr>:<port>/scan#t=<ticket>", qr:{size, modules:"<base64 bitset>"}, fingerprint:"1a2b 3c4d 5e6f 7a8b", expiresUnixMs}`.
   The address is the node's address on the interface the asking session used, else its first private address.
4. The journal row is `PHONE INVITE "<name>" → invite issued, expires 21:14` and never holds the URL.
5. The reply is not copied into STATE. The MCP tool result is never produced, because the verb refuses those
   transports.

**Join**:

1. The page reads `location.hash`, removes it at once with `history.replaceState`, and opens
   `wss://<same origin>/scan/ws` with subprotocol `playmap.camera.v1`.
2. Its first message is `hello` carrying the ticket (or the stored key).
3. The hub checks the ticket in constant time and marks it used. It mints a **phone key** (32 random bytes,
   base64url), stores `sha256:<hex>` in `NodeHome/phones.json`, and returns `welcome {key}` once.
4. The page keeps the key in `localStorage["playmap.phone." + nodeId]` (try/catch; without storage the phone needs a
   new invite next time, and the page says so).
5. On first join the engine adds a `CameraRig` (`SourceKind=Phone`, `Source=<phone id>`) as one undoable edit on the
   lane, or updates its `Phone` profile on a later join.

**The gate**: `HttpHost.GateAsync` hands requests under a module's prefix to `IWebModule.Admit`.

- Static files are `Open`: no data, no secret.
- `/scan/ws`, `/scan/frame` and `/scan/events` are `Capability`: the module checks the ticket or key itself.
- Over plain HTTP from a non-loopback address the module answers static files with the "needs https" page and refuses
  the socket (`getUserMedia` would fail anyway).
- `RequireHttps` keeps its meaning for every other route.

**Scoped rights.** Lines from a phone run as `SessionInfo(Transport:"phone", ClientName:"phone <name>", Paired:true)`
through `PhoneVerbFilter` before the router. The filter allows:

- `SCAN START <its own camera> …` (including `INTO`), unless the invite was `CAMERA-ONLY`;
- `SCAN CANCEL` when the running scan uses its camera;
- `SCAN STATUS`, `SCAN VIEWS`, `SCAN SUGGEST`, `SCAN NEXT`;
- `SCAN SURFACE <scan> REGION …` for scans it took part in;
- `APPROVE`/`REJECT` for proposals whose line is a `SCAN SURFACE` of such a scan (not with `CAMERA-ONLY`).

Everything else gets `ERR a phone camera may only scan through itself and approve its own scan's surfaces`. The
router's own rules still apply: armed refusals and risk.

**Headers** on every `/scan` response:

- `Content-Security-Policy: default-src 'self'; script-src 'self'; worker-src 'self'; connect-src 'self'; img-src 'self' blob: data:; style-src 'self'; frame-ancestors 'none'`
- `Referrer-Policy: no-referrer`
- `Permissions-Policy: camera=(self), accelerometer=(self), gyroscope=(self)`
- `X-Content-Type-Options: nosniff`
- `Cache-Control: no-store` on HTML, a strong ETag on the rest

### 3.5 Clocks and latency

- The phone sends `ping {t1}` (page ms). The node answers `pong {t1, t2, t3}` (node monotonic ms), and the phone echoes
  `t4` in its next ping. The hub feeds `ClockSample(T1..T4)` into a per-phone `ClockOffsetEstimator`, which keeps the
  minimum-RTT sample of a window of 8 plus drift. Output: page ms → node mono ns, ±½ RTT of the best sample. On
  Wi-Fi expect ±2–8 ms (estimate).
- **Capture time** of a frame:
  - with MSTP: `VideoFrame.timestamp` (µs, its own base). It is mapped to the page clock by a running **minimum** of
    `performance.now()·1000 − timestamp` over the last 60 frames (arrival is after capture, so the minimum is the
    tightest bound);
  - with rVFC: `metadata.captureTime` when present (bench: on the `performance.now()` clock, R§2.4), else
    `metadata.presentationTime − one frame period`.

  The page reports page ms; the node converts.
- **Latency** (glass → capture time): after locate, the node toggles the chosen sites' cells all-lit and all-dark,
  2 flashes, holding each until `seen`. The phone reports per-frame site brightness with capture times, and
  `LatencyEstimator` (existing) gives the median. Because capture times remove Wi-Fi jitter, this measures projector lag
  plus the exposure midpoint. It feeds the timing cross-check and the timing fallback; latency on its own **never
  decides** a frame's pattern while sites exist.

### 3.6 Exposure, focus and white balance

**Lock profile** (Chrome Android, or any track offering `exposureMode: manual` and `exposureTime`). Sequence in
`exposure.js`, driven by the node's `prepare`:

1. The node shows the **probe**: the coarsest Gray level of the plan (50 % stripes, tag sites dark) at `High`. Focus
   and auto-exposure run continuous.
2. The phone waits until the frame mean over the lit area is stable (±2 % over 0.5 s, max 4 s) and focus has settled
   (`getSettings().focusDistance` unchanged over 0.5 s where reported). Then it reads `exposureTime` (t₀, 100 µs
   units, R§2.2) and `iso` (s₀).
3. It measures **L99.5** over the lit stripes: pixels in the brightest 45 % of the probe's lit area.
4. Target 215 (headroom for projector hot spots): `t₁ = t₀·215/L` when L < 250. When clipped (L ≥ 250),
   `t₁ = 0.6·t₀` and it measures again (max 2).
5. **Flicker-safe**: t₁ is rounded **up** to a multiple of the output's frame period (`displayHz` from `prepare`) when
   t₁ > 8 ms, for DLP colour-wheel and PWM dithering. It is capped at 33 ms (30 fps). ISO makes up the difference:
   `s₁ = s₀·(t₀·215/L)/t₁`, clamped to the capability range.
6. Apply in one call:
   `applyConstraints({advanced:[{exposureMode:"manual", exposureTime:t₁, iso:s₁, focusMode:"manual", focusDistance:d, whiteBalanceMode:"manual"}]})`.
   Members the track does not offer are omitted. The call is retried without the failing member when it rejects with
   an `OverconstrainedError` naming one (the bench shows the names, R§2.2).
7. **Verify** three frames later: `getSettings()` echoes manual and L is in [190, 240], giving `exposure:"locked"`.
   Otherwise one more pass, then fall back to the steady profile with words.

This is one exposure, one measurement and one correction, with a clipping check. It deliberately does **not** capture
references at several exposures and pick by saturation (the Summit patent's abstract, R§6).

**Steady profile** (iOS Safari, or any track without the controls). The plan and the decoder change, not the camera:

- **No full white or black frame in the coded run.** The plan's white and black stay in the plan (the decoder's
  indices are unchanged) but are **not shown**. `ReferenceSynthesis` fills them after capture. Per pixel it takes the
  pair (P, P̄) with the largest |P − P̄| among the **three coarsest** Gray levels; white = max, black = min. Each pixel is
  far from an edge in at least one of two adjacent Gray levels, because their edges never coincide.
- **Settle auto-exposure first**: the probe is shown until the phone reports `aeSteady` (mean stable ±2 % over 0.5 s).
  It is shown again after a projector's device black frame (a shutter-closed frame makes auto-exposure ramp).
- **Locate frames on a gray background** (level 96) so their mean stays near the coded frames' (§3.7).
- **Gain normalisation** (`GainNormaliser`): every coded frame (Gray, inverse, sinusoid) has a 50 % mean over the
  region. Each frame is scaled so that its mean over the **lit mask** (from the synthesised references) equals the
  median of all frames' means. That removes slow auto-exposure drift before thresholds and phase are computed.
- **Drift as a fact**: per-frame mean against the running median. Over 4 % the node **re-cues** the pattern (at most 2
  retakes per frame). STATE `scan.pairing.aeDriftPct` is the worst seen.
- White balance is locked (`whiteBalanceMode:"manual"` where offered; WebKit maps it to
  `AVCaptureWhiteBalanceModeLocked`, R§2.2). Focus stays continuous and the words say so.

### 3.7 Cell locate and tag sites

**Candidate grid** (per projector, at its output raster W×H): cell size `c = max(6, round(W/80))` px (24 at 1920, 6 at
256). A **site** is 4×4 cells (96×96 at 1920). There are 8×5 = 40 candidate sites spread evenly inside a 4 % inset, so
640 cells in all.

**Locate sequence** (12 frames, each shown until `seen` by the settle rule; the phone uploads each as `kind:"locate"`):

| Frame | Cells | Background |
|---|---|---|
| L0 | every candidate cell lit | 96 on the steady profile, 0 on the lock profile |
| L1 | every candidate cell dark | same |
| L2…L11 | cell with id k ∈ [1, 640] lit when bit (j−2) of `Gray(k)` is 1 | same |

**Decode** (`CellLocate.Decode`, Core, off the lane):

1. Per camera pixel, contrast = L0 − L1; a pixel is lit when contrast ≥ 12.
2. Each bit is `L_j > (L0+L1)/2` with margin ≥ 20 % of the contrast, else the pixel is unsure.
3. The bits give the cell id; unsure or id 0 → none.
4. Per cell: centroid, area and second moments of its pixels.

A cell is **found** when its area is ≥ 60 % of its expected footprint (the median footprint of found cells in the same
site) and it is compact (the ratio of moment axes is within 4× of the site median).

**Site choice**: candidates with all 16 cells found and a homography fit of their cell centroids with RMS < 0.5 camera
px (locally smooth surface). Then greedily:

1. The site with the **smallest** camera row.
2. The one with the **largest** camera row.
3. Up to `MaxSites` (3), the one farthest in camera space from the chosen ones.

Two sites ≥ 30 % of the lit rows apart give **rolling-shutter coverage** of the band between them. One site works,
with "rolling-shutter check limited" in STATE. None means the **timing fallback** (`pairing.mode:"timing"`, amber,
words: "the camera sees none of the tag places: pairing by timing, keep the phone very still").

**Sent to the phone**: `sites {projector, sites:[{id, cells:[[x,y]×16], rows:[y0,y1]}], lit:[x,y,w,h]}`, all in
camera pixels of the stream's visible raster.

**The tag** (`FrameTag`). For pattern index i the value is `v = (i mod 63) + 1` (1…63; 0 is never a tag, so a dark site
reads as none). Cells in row-major order:

- cell 0 always lit (reference);
- cell 15 always dark (reference);
- cells 1…14 are 7 **differential pairs** (2k+1, 2k+2), carrying bits b0…b5 of v and an even-parity bit b6.

Pair k shows (lit, dark) for bit 1 and (dark, lit) for bit 0. `TagOverlay.Apply(raster, sites, v)` paints the sites
into the pattern raster after `ScanPlan.Render(i, …)`, at `High`/`Low` levels. The latency flashes paint every cell lit
or dark.

**Reading on the phone** (per frame, per site): sample the mean of a 3×3 neighbourhood at each cell centre (16 × 9
samples). Then:

- `contrast = cell0 − cell15`; the site is **valid** when contrast ≥ 20 levels.
- Per pair `d = A − B`; certain when |d| ≥ max(6, 0.2·contrast).
- All 7 certain and parity even → the site reads v, else the site is unsure.

The frame's tag is **v** when every valid site reads v and at least `minSites` (2, or 1 when only 1 exists) are valid.
A frame where valid sites **disagree** is a **rolling-shutter reject** (counted). One with sites unsure is skipped.

**Excluding sites from the decode**: the locate decode gives each site's camera footprint. Dilated by 2 px, it is the
view's **tag shadow** mask. Camera pixels in it are marked invalid in the `CorrespondenceMap`, and so are any whose
decoded projector coordinate falls within 2 px of a site rectangle. The shadow is ≈ 0.9 % of a 1080p raster with 3
sites. S9 fills it.

### 3.8 Cued capture: the lock-step

`ScanRunner.ShowAndCaptureAsync` gains a branch: when the opened camera is `ICuedScanCamera`, it runs `CaptureCuedAsync`.

```
prepare  : camera.PrepareAsync(profile, displayHz, settle)  → exposure path facts
locate   : for j in 0..11: show L_j; await camera.LocateAsync(j, present) → stored low-level frames
           CellLocate.Decode → sites (or none) ; camera.SetSitesAsync(sites)
latency  : 2 flashes on the sites → LatencyEstimate (capture-time based)
patterns : for i in plan:
             raster = plan.Render(i); TagOverlay.Apply(raster, sites, FrameTag.Value(i))   (steady: skip White/Black)
             present = display.ShowAsync(output, raster)
             seen = await camera.CueAsync(new PatternCue(i, tag, present, earliest = present + latency − ½ frame))
               ├─ Seen        → pixels[i] = seen.Pixels (Task, completes when the upload lands); next i
               ├─ Retake(why) → re-show the same raster (≤ 2), counted in pairing.retakes
               └─ Stuck(why)  → the view fails with the phone's words ("tag never seen", "moved", "unsettled")
             backpressure: wait while pixels in flight ≥ MaxInFlight (6)
end check: show pattern 0 again; its new picture against the stored one: mean |Δ| over the lit area > 6 levels
           → "the phone moved during the view" (the view fails; observed, not assumed)
decode   : await all pixels → (steady: ReferenceSynthesis + GainNormaliser) → StructuredLightDecoder → tag shadow
```

**On the phone**, for a cue: discard frames whose capture time (node clock) is < `earliest`. Then read the tag. When
two **consecutive** frames read the cue's tag **and** agree (the sparse grid of 48×27 samples inside `lit` differs by
mean |Δ| < `AgreeTolerance`, 3 levels, existing `SettleOptions`), the second (or the sum of the next `frames`) is the
picture. The phone sends `seen {cue, tag, capture, sites, margin, settleDiff}` **at once**, then crops, compresses and
queues the upload. If 5 s pass without it, it sends `stuck {cue, why}` with the most frequent failure: "tag not seen",
"tag shows 16", "unsettled", "moved". The node re-cues once; the cue carries `retake:1`.

**Timing cross-check**: for each `seen`, the node checks `capture ≥ present + latency − 1 frame period`. A frame that
passes the tag but fails timing by more than 2 frame periods is accepted (the tag is the observation) and counted as
`timingDisagrees`. More than 3 in a view adds an amber item: "the phone's clock or latency is off — pairing by tags
only". **Timing fallback** (no sites): accept the first settled frame whose capture time is ≥ `earliest`, where the
settle detector also requires a change from the previous accepted frame (`MinChange`, 8 levels). STATE says
`pairing.mode:"timing"`.

**Expected time per pattern** (estimate): present ≤ 16.7 ms + glass-to-capture 30–120 ms + two frames at 30 fps
(67 ms) + `seen` over Wi-Fi 5–20 ms ≈ **120–220 ms**. A Robust plan for 1920×1080 (40 coded + 2 references; the steady
profile shows 40) takes ≈ 5–9 s; locate (12) ≈ 2–3 s; latency ≈ 1 s. Total **≈ 8–13 s per projector per view**.

### 3.9 Upload format and limits

**Text messages**: JSON, one object per WS text frame, `t` names the type (§4.6). At most 64 KiB. Unknown `t` closes
with 1008. Unknown fields are ignored (forward compatible); missing required fields close with 1008.

**Binary frame `PMF1`** (little-endian):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"PMF1"` (0x31464D50) |
| 4 | 4 | header length H (uint32, ≤ 4096) |
| 8 | H | header JSON: `{kind:"pattern"\|"locate"\|"still"\|"preview"\|"reference", seq, cue?, projector?, view?, capture, w, h, roi:[x,y,w,h], format:"gray8"\|"gray16"\|"jpeg", codec:"deflate-raw"\|"none"\|"jpeg", frames, crc32}` |
| 8+H | rest | payload: rows of the ROI, top to bottom, no padding; gray16 little-endian (sum of `frames` 8-bit frames ×256/frames, facade's gray16 contract); compressed by `codec` |

- **Limits**: one binary message ≤ 16 MiB. Decompressed ≤ `w·h·2` and ≤ 64 MiB. `roi` inside `w×h`. `crc32` over
  the uncompressed payload. Any breach → close 1009 or 1007 and the view fails with words.
- **Previews**: `jpeg` at ≤ 640 px wide, ≤ 5 fps, only while a desk view of the phone camera is shown (`preview {on}`),
  latest-wins.
- **Backpressure**: the phone keeps at most `maxInFlight` (from `welcome`, default 6) unacknowledged pattern frames. The
  node answers `ack {seq}` when stored. When the WS `bufferedAmount` exceeds 8 MB the phone stops uploading previews.
- **Fallback transport** (same origin, same rights): `POST /scan/frame` with the PMF1 body, the key in an
  `X-PlayMap-Phone` header, ≤ 16 MiB per request (the route raises Kestrel's per-request limit through
  `IHttpMaxRequestBodySizeFeature`), and `GET /scan/events` (server-sent events) for node → phone messages. The page
  switches when the WebSocket fails twice. Safari's WebSocket behaviour under an untrusted certificate is **U**
  (R§6).
- **Bandwidth** (estimate): 1080p gray8 is 2.07 MB raw; deflate on camera noise maybe 1.5–2.2× (**U**), so ≈ 1–1.4 MB a
  frame and ≈ 45–60 MB per projector-view. At 100 Mbit/s that is 4–5 s, hidden behind the lock-step by pipelining. The
  STATE `link.uploadMBps` is measured.

### 3.10 Multi-view solve: projector-anchored bundle adjustment

**Why projector-anchored**:

- A projector is a camera that does not move between views, and every decoded camera pixel names a projector pixel
  exactly.
- So a **projector pixel is a surface point's name across views**: the same projector pixel seen by view A and view B
  is the same 3D point. No feature matching, no texture needed, and it works on white walls and curved plaster.

**1. Tracks** (`InverseCorrespondence`, `TrackSet`). For each (view v, projector p):

- On the projector grid with stride s = 16 px (at 1920: 120×68 = 8 160 nodes), find the camera position of each node.
- Take the decoded camera pixels whose projector coordinate lies within 3 px of the node (≥ 6 of them, confidence
  ≥ 0.3).
- Fit **camera ← projector** as a local affine map by least squares and evaluate it at the node.
- Keep the node when the fit residual is < 0.5 camera px and the affine is non-degenerate.

A node seen by ≥ 1 view is a **track** t = (p, u). Observations are x_{t,v}.

**2. Initialisation**:

- (a) Per view, run the existing `SelfCalibration.Solve` with that view's projectors (camera prior = the rig's
  intrinsics; locked when `Calibrated`). That gives projector poses in the view's frame, scale-consistent within the
  view (shared camera pixels tie the scales, as `SelfCalibration` already does).
- (b) A graph with a node per view and an edge per shared projector, weighted by shared tracks. Take the maximum
  spanning tree from the view with the most projectors (v₀).
- (c) Along each edge (v → w through projector p), the similarity that maps w's frame to v's comes from p's two poses
  (rotation from the orientations, translation and scale by Umeyama on the shared tracks' triangulated points).
- (d) Projectors seen only by later views come in through their view's transform. A view sharing **no** projector with
  the tree is left out with words: "view v3 shares no projector with the others; include part of a projector the other
  views saw".

**3. Joint adjustment** (`MultiViewCalibration`, Levenberg–Marquardt with the existing `SolverMath` primitives):

| Unknown | Count | Prior |
|---|---|---|
| per view: pose (rotation vector, centre) | 6·V (v₀ fixed: gauge) | — |
| per camera rig (phone or webcam): fx (fy = fx·aspect), cx, cy, k1, k2 | 5 per rig | fx ±10 % (none when `Calibrated`: locked); cx, cy ±2 % of width; k ±0.05 |
| per projector: pose; fx; cx, cy; lens | 6 + 1 + 2 (+5) | its `SolveLocks` (shift and focal locks honoured); throw-ratio prior ±5 % unless locked |
| per track: depth d_t along the projector's ray for u | T | — |

- **Residual**: `π(K_c, D_c, T_v, X_t) − x_{t,v}` for every observation, where `X_t = C_p + d_t·R_p·K_p⁻¹[u,1]ᵀ`. The
  projector's own observation is exact by construction, which is the anchor. Huber loss, δ = 1.5 px.
- **Gauge**: v₀'s pose is fixed, and the scale is fixed by holding ‖C_{p₀} − C_{v₀}‖ at its initial value. The metric
  step (S2, or the existing known-distance and project-baseline rules) rescales afterwards.
- **Schur complement**: the per-track depths form a diagonal block; eliminating them leaves a dense reduced system of
  6V + 5R + 9P ≈ 170 parameters for 12 views, 8 projectors and 2 rigs.
- **Size**: T ≈ 8 160 × P = 65 k tracks; ≈ 2.5 views per track → 160 k observations. ≈ 20 ms per iteration and
  ≤ 30 iterations (estimate; benchmarked in M6's test).
- **`HOLD <projector>…`**: those projectors' poses and intrinsics are fixed at their **stage** values, which also fixes
  the gauge and scale (and makes the result metric when the stage is). This is "re-solve only the projector that
  moved": one new view seeing the moved projector and one held projector is enough.
- **Outputs**:
  - per projector: pose, intrinsics, lens, RMS, inliers, a σ for position along its axis from the reduced Hessian (the
    facade spec's `SolveUncertainty` fields);
  - per view: pose, RMS, track count;
  - per rig: solved intrinsics and their relative σ. The phone rig is written back as `Calibrated` when σ_f < 0.5 % at
    SCAN APPLY.

**4. Dense geometry per projector** (`ProjectorDepth`):

- For every projector pixel (stride 2 by default; 1 in Difficult), take each view that decoded it: the camera position
  comes from the view's inverse correspondence at that pixel (bilinear in the local affine field).
- Triangulate the projector ray against each view's camera ray (existing `Triangulation`). Combine by inverse variance
  (variance from the ray angle and the decode confidence), drop views more than 2 px out, and keep σ_z.
- Result: an organised cloud **on the projector's raster** with normals (`PointCloudOps.EstimateNormals` toward the
  projector) → `GridMesher.Build` → the projector's mesh. **Curved surfaces need nothing special**: the grid follows
  whatever the projector lights.

**5. Merge** for SCAN APPLY:

- The stage mesh is the union of the projectors' meshes, with overlaps trimmed. A triangle of projector q is dropped
  when its centroid lies within 1.5 grid spacings of a triangle of an earlier-kept projector with smaller σ_z there.
  Projectors are kept in order of median σ_z.
- Vertices carry the projector and cell they came from, for click-to-surface.

**6. Coverage** (`CoverageMap`): per projector raster at ¼ resolution, the number of views that decoded each cell
(0 / 1 / ≥ 2), restricted to the lit area (L0 over every view). STATE gives `seen` (≥ 1) and `twice` (≥ 2) fractions,
plus a PNG for the phone and desk (and the wall with S1).

**7. Per-projector smart scan and regions** (M7):

- `ProjectorView.Build` (existing) per (view, projector), blended across views (best confidence wins) → the projector's
  smart scan.
- A `ScanGeometry` **per projector** with the projector as its own "camera" (its organised cloud is on its own raster),
  so `ScanSurfaces.Regions` and click-to-surface work unchanged per projector.
- Regions are merged across projectors when their 3D point sets overlap (IoU ≥ 0.5 on a 1 cm voxel grid); the merged
  region lists every projector.

### 3.11 Safe AI surface suggestions

```
SCAN SUGGEST sc… [ASSISTANT]
  1 measured candidates: per-projector regions (ScanSurfaces.Regions = SurfaceSuggestion.AutoMasks/Propose on the
    cloud's depth and crease edges), merged across projectors; the local segmenter (SegmenterRouter: classical, or SAM
    when its model is installed and its licence allows commercial use) on the smart scan; agreement = IoU of the two
  2 descriptors (text only): {id, kind (plane|cylinder|sphere|freeform), areaM2, planarityRmsMm, normalToViewDeg,
    curvatureRadiusM?, projectors:[{name, coveragePct, blendSharePct}], agreementIoU, depthEdgeSharpness, heightM,
    neighbours:[ids], segLabel?}
  3 rank = confidence (geometry and segmentation agreement, size, coverage) — deterministic, no model needed
  4 ASSISTANT: one request to the node's assistant (AssistantService) with the descriptors and the project's names;
    it answers names, ranking and a short reason per item through a tool schema (JSON), never lines. Its output is
    validated: ids must exist, names ≤ 40 chars, no verb words
  5 the ENGINE queues one ProposalDraft per suggestion: "SCAN SURFACE sc… REGION rg… [ON pj…] \"<name>\"" (Review),
    From = "scan suggestions" (or "assistant (<model>) via scan suggestions"), Batch = the suggest call
  6 STATE.scan.suggestions[] with reasons and the proposal id; the phone and the desk show them; a person approves
```

The safety properties, each enforced in code and tested (M9):

| Property | How |
|---|---|
| **Local** | Candidates and ranking need no model. The segmenter is local ONNX or classical. The assistant (if any) gets text descriptors only: `SuggestionDescriptor` has no image field, and the request builder takes only descriptors |
| **Measured first** | An assistant name for a region the geometry did not find cannot become a proposal: the id must exist. A segmenter region with no geometric match (IoU < 0.5) is shown grey, "seen in the picture, not measured", with no proposal. A person can still click it (click-to-surface) |
| **A person decides** | Proposals are queued by the engine, so `RunReviewWithoutAsking` does not apply. `SCAN SURFACE` stays Review; no bulk approval (`ProposalQueue`'s rule for High is kept for Review here too) |
| **Not on the air while armed** | Wall previews (outline on the output) and `SCAN COVERAGE` are High and refused while armed. Phone and desk previews are always allowed |
| **Reversible** | Each approved surface is one `Store.Apply` edit; `UNDO` removes it |
| **Explained** | Every suggestion carries its reasons (§8). A proposal's reason is the same text |

### 3.12 Folder camera

`FolderCamera : ICuedScanCamera`, configured by `CameraRig.Folder` (§4.1):

- **tether**: a `FileSystemWatcher` on the folder plus a 250 ms poll, because watchers miss events on network shares.
  A file counts as **new** when its name was not present at the cue and its size is stable for `SettleMs` (default
  300). The picture is decoded by `StillImageReader` (FFmpeg `image2` demuxer and the JPEG/PNG/TIFF decoders, loaded at
  run time as today). 16-bit TIFF → gray16. RGB → BT.709 luma (and RGB for S10).
- **manual**: the node shows the pattern and waits for `SCAN NEXT`. The photographer shoots and presses Next on the
  desk, the phone or a Stream Deck. The newest file after the cue is the picture.
- **Tags apply**: locate runs the same way (the photographer shoots the 12 locate frames too). A photo whose tag reads a
  different pattern is refused, with words ("photo 17 shows pattern 16: re-shoot") and a retake cue.
- RAW files are refused with words ("shoot JPEG or TIFF, or let the tethering tool convert").

### 3.13 Performance budgets

| Item | Budget | How it is checked |
|---|---|---|
| Phone: tag read and settle per frame (1080p, MSTP worker) | ≤ 4 ms (≈ 16×9 + 48×27 samples) | Playwright timing on the fake device (desktop CPU; phone **U**) |
| Phone: deflate 1080p gray8 | ≤ 60 ms in the worker, off the capture path (estimate) | Bench (synthetic); phone **U** |
| Node: decompress 1 MB | ≤ 10 ms | Benchmark test |
| Per pattern (lock-step) | 120–220 ms (estimate) | E2E STATE timing histogram |
| Per projector per view | ≤ 15 s (M4) | E2E and simulator |
| Locate decode (12 frames, 1080p) | ≤ 300 ms | Core test with stopwatch reporting (no assert on CI speed) |
| Multi-view BA (65 k tracks, 12 views) | ≤ 2 s on 4 cores (estimate) | M6 test reports it |
| Dense depth (2 projectors, stride 2, 3 views) | ≤ 3 s | M6 test reports it |
| Memory per projector-view | ≤ 100 MB (42 × 2.07 MB frames) | Released after decode |
| Phone sessions | ≤ 4 connected (`PhoneMaxConnected`) | Hub test |

---

## 4. Spine needs

### 4.1 Model (Core/Model, backwards compatible)

```csharp
// StageModel.cs (hunk): optional parts of a camera rig — an older file reads them as null.
public sealed record CameraRig(...)            // unchanged positional members
{
    /// <summary>What the phone behind a Phone rig is (null for other kinds).</summary>
    public PhoneProfile? Phone { get; init; }
    /// <summary>How a Folder rig takes its pictures (null for other kinds).</summary>
    public FolderCapture? Folder { get; init; }
}

/// <summary>A phone as it described itself when it joined: platform, model, the physical camera it chose and the raster
/// it streams. Intrinsics solved with this profile are only reused while all of it matches.</summary>
public sealed record PhoneProfile(
    string Platform,          // "android-chrome" | "ios-safari" | "other"            json "platform"
    string Model,             // "Pixel 8" (UA-CH) or "" when not told              json "model"
    string CameraLabel,       // the device label it chose                         json "cameraLabel"
    int Width, int Height,    // the stream's visible raster                        json "width","height"
    string Exposure);         // "lock" | "steady" — the profile it can run          json "exposure"

public enum FolderAdvance { NewFile, Manual }
public sealed record FolderCapture(string Path, FolderAdvance Advance, int SettleMs = 300);   // json "path","advance","settleMs"

// ScanRecord (hunk): views and coverage — an older file reads them as empty.
public sealed record ScanRecord(...)
{
    public ImmutableArray<ScanViewRecord> Views { get; init; } = [];          // json "views"
    public ImmutableArray<ScanCoverage> Coverage { get; init; } = [];         // json "coverage"
}
public sealed record ScanViewRecord(
    string Id,                // "v1", "v2"… within the scan
    string CameraId,
    ImmutableArray<string> ProjectorIds,
    Pose CameraPose,          // in the scan's (and, after APPLY, the stage's) frame
    double RmsPx,
    int Tracks,
    string Pairing,           // "tag" | "timing"
    string Exposure,          // "locked" | "steady" | "auto"
    long CreatedUnixMs);
public sealed record ScanCoverage(string ProjectorId, double Seen, double Twice);
```

`ScanKind` is unchanged: a multi-view scan is `StructuredLight` with `Views.Length > 1`. Phone ids (`ph` + 8 hex) are
node-local and never project ids. The rig's `Source` holds the phone id.

### 4.2 Verbs (`VerbCatalog`, one hunk)

| Verb | Grammar | Risk | Mutates | Reply |
|---|---|---|---|---|
| SCAN START (extended) | `SCAN START <camera> [<projector>…] [MODE FAST\|ROBUST\|DIFFICULT] [INTO <scan>] [FRAMES <1-8>]` | High (unchanged) | yes | accepted `{stage:"accepted", scan, view}`. `INTO` refuses a scan from another node or one being solved; refused while armed (show safety) |
| SCAN SOLVE | `SCAN SOLVE <scan> [HOLD <projector>…]` | Review | yes | accepted, then `STATE.scan`; refused "nothing would change" when no view changed since the last solve and no HOLD is given |
| SCAN VIEWS | `SCAN VIEWS <scan>` | Low | no | `{views:[{id, camera, projectors, rmsPx, tracks, pairing, exposure, state}], coverage:[…]}` |
| SCAN DROP | `SCAN DROP <scan> <view>` | Review | yes | accepted, then a re-solve; refused for the last view |
| SCAN SUGGEST | `SCAN SUGGEST <scan> [ASSISTANT]` | Low | no (proposals are not project edits) | `{suggestions:[{id, label, confidence, reasons, proposal}]}` |
| SCAN COVERAGE (S1) | `SCAN COVERAGE <scan> SHOW\|HIDE [<projector>\|ALL]` | High | yes | applied when the output shows it; refused while armed |
| SCAN SCALE (S2) | `SCAN SCALE <scan> <metres> <x1> <y1> <x2> <y2> [ON <projector>]` \| `SCAN SCALE <scan> PHONE <phone>` | Review | yes | `{scale, metric:true, samples, sigmaPct}` |
| SCAN NEXT | `SCAN NEXT` | Review | yes | applied when the running scan was waiting for it; refused "no scan is waiting for a picture" otherwise |
| SCAN IMPORT (S8) | `SCAN IMPORT <camera> <folder>` | Review | yes | accepted |
| SCAN SURFACE (extended) | `… [ON <projector>]` added to both forms | Review (unchanged) | yes | unchanged |
| PHONE INVITE | `PHONE INVITE [<name>] [FOR <minutes 1-60>] [CAMERA-ONLY]` | High | yes | to the asking session only: `{url, qr:{size, modules}, fingerprint, expiresUnixMs}`. Refused for assistant, mcp and approval transports, and when `PhonesEnabled` is false or the node serves no HTTPS ("turn on encrypted connections first") |
| PHONE LIST | `PHONE LIST` | Low | no | the STATE `phones` rows |
| PHONE FORGET | `PHONE FORGET <phone>\|ALL` | Review | yes | `{forgotten:[…]}`; disconnects; refused "no such phone" |
| PHONE CHECK | `PHONE CHECK <phone>` | Low | no | `{caps, link:{rttMs, uploadMBps}, words}` after a 2 MB upload test |
| PHONE USB (S4) | `PHONE USB ON\|OFF` | Review | yes | `{adb:"…", reverse:"tcp:9940"}` or words |

Every new verb is in the context's command list with its risk. The phone's own buttons run these lines through
`PhoneVerbFilter` (§3.4).

### 4.3 STATE and context

```jsonc
"phones": [{
  "id": "ph7f3a2c91", "name": "Pixel 8", "camera": "cm1a2b3c4d", "platform": "android-chrome", "model": "Pixel 8",
  "connected": true, "address": "192.168.1.44", "transport": "ws",            // "ws" | "http"
  "link":   { "rttMs": 9.8, "offsetMs": 12.3, "uncertaintyMs": 4.9, "uploadMBps": 6.1 },
  "camera": { "label": "camera2 0, facing back", "width": 1920, "height": 1080, "fps": 30,
              "exposure": "locked", "exposureMs": 16.7, "iso": 320, "focus": "locked", "whiteBalance": "locked" },
  "motion": "still", "visible": true, "lastSeenUnixMs": 1760381234567,
  "words": "Ready: exposure, focus and white balance locked" }],
"scan": { /* existing fields */,
  "view": "v2",
  "views": [{ "id": "v1", "camera": "cm1a2b3c4d", "projectors": ["pj1","pj2"], "state": "done",
              "rmsPx": 0.31, "tracks": 8120, "pairing": "tag", "exposure": "locked" }],
  "coverage": [{ "projector": "pj1", "seen": 0.93, "twice": 0.61 }],
  "pairing":  { "mode": "tag", "sites": 3, "rollingRejects": 1, "retakes": 2, "timingDisagrees": 0,
                "aeDriftPct": 0.0, "latencyMs": 74 },
  "regions":  [{ /* existing */, "projector": "pj1", "projectors": ["pj1","pj2"] }],
  "suggestions": [{ "id": "sg1", "label": "Left wall", "kind": "plane", "confidence": 0.92,
                    "reasons": ["flat: 2.1 mm RMS over 3.4 m²", "lit by Left (100 %) and Right (22 % blend)",
                                "picture and depth edges agree (IoU 0.91)"],
                    "measured": true, "proposal": "ap12", "state": "pending" }] }
```

**Context items**:

- **phone** (light, headline, commands):
  - green "Pixel 8: ready, exposure locked";
  - green "iPhone: ready, brightness kept steady (no exposure lock on iPhone)";
  - amber "Pixel 8: moved during the last view" / "link slow (1.2 MB/s)";
  - grey "Pixel 8: not connected".
- **scan suggestions** waiting: amber "3 surface suggestions wait for a person", commands `APPROVE ap12` …
- **coverage below 80 %**: amber, with the fix "SCAN START <camera> INTO sc… (aim at the red)".

None of these carries a ticket, a key or a URL with a fragment.

### 4.4 Seams (C# signatures)

```csharp
// PlayMap.Net/Http/WebModules.cs — routes added to the web port by a part of the node
public enum WebAdmission { Defer, Open, Capability, Refuse }
public interface IWebModule
{
    string Name { get; }
    /// <summary>The path prefix the module answers ("/scan"); a request under it is admitted by the module.</summary>
    string PathPrefix { get; }
    /// <summary>Open (static, no secret), Capability (the module checks its own credential in the handler), Refuse
    /// (with words), or Defer (the node token gate decides).</summary>
    WebAdmission Admit(HttpContext context, NodeSettings settings, out string words);
    void Map(IEndpointRouteBuilder routes);
}
// HttpHostOptions (hunk):   public IReadOnlyList<IWebModule> Modules { get; init; } = [];
// NetworkHost (hunk):       public void AddWebModule(IWebModule module);   // before StartAsync; throws after

// PlayMap.Net/Phone/PhoneHub.cs
public sealed record PhoneHubOptions(string KeysPath, TimeProvider Time, int MaxConnected = 4, int MaxInFlight = 6,
    long MaxBinaryBytes = 16 << 20, int MaxTextBytes = 64 << 10);
public sealed record PhoneInvite(string Url, string Ticket, DateTimeOffset Expires);       // Ticket never leaves the reply
[Flags] public enum PhoneRights { Camera = 1, Control = 2, Approve = 4, All = 7 }
public sealed record PhoneFacts(string Id, string Name, string? CameraRigId, string Platform, string Model, bool Connected,
    string Address, string Transport, double RttMs, double OffsetMs, double UncertaintyMs, double UploadMBps,
    PhoneCameraFacts Camera, string Motion, bool Visible, long LastSeenUnixMs, string Words);
public sealed record PhoneCameraFacts(string Label, int Width, int Height, double Fps, string Exposure, double ExposureMs,
    double Iso, string Focus, string WhiteBalance);
public sealed class PhoneHub : IAsyncDisposable
{
    public PhoneHub(PhoneHubOptions options, ILogger log);
    public IWebModule Module { get; }
    public PhoneInvite Invite(string name, TimeSpan life, PhoneRights rights, string invitedBy, Uri baseUri);
    public IReadOnlyList<string> Forget(string phoneIdOrAll);
    public IReadOnlyList<PhoneFacts> Phones();
    public IPhoneSession? Session(string phoneId);
    public event Action<PhoneFacts>? Changed;                 // joins, leaves, facts
    public event Action<PhoneJoined>? Joined;                 // the engine adds or updates the camera rig
    /// <summary>A phone button's line; the engine runs it through PhoneVerbFilter and the router.</summary>
    public Func<string /*phoneId*/, string /*line*/, CancellationToken, Task<CommandReply>>? Lines { get; set; }
}
public sealed record PhoneJoined(string PhoneId, string Name, PhoneProfile Profile, Intrinsics IntrinsicsPrior);

public interface IPhoneSession
{
    string PhoneId { get; }
    PhoneProfile Profile { get; }
    bool Connected { get; }
    /// <summary>Phone page ms → node monotonic ns (the clock exchange); ± uncertainty in ns.</summary>
    (long NodeNs, long UncertaintyNs) ToNode(double pageMs);
    Task<PhonePrepared> PrepareAsync(PhonePrepare request, CancellationToken ct);
    Task SetSitesAsync(PhoneSites sites, CancellationToken ct);
    Task<PhoneSeen> CueAsync(PhoneCue cue, CancellationToken ct);          // completes on seen | retake | stuck
    Task<PhonePicture> PictureAsync(long seq, CancellationToken ct);       // the upload that belongs to a seen
    Task<PhonePicture?> PreviewAsync(TimeSpan timeout, CancellationToken ct);
    Task NotifyAsync(PhoneNotice notice, CancellationToken ct);            // view progress, coverage image, suggestions
}

// PlayMap.Core/Scan/Phone/PhoneProtocol.cs — the message records (JSON camelCase), shared by tests and the hub
public sealed record PhonePrepare(string Id, string Profile /*lock|steady|auto*/, double DisplayHz, int TargetL995,
    double AgreeTolerance, int StableFrames, int Frames, string Format);
public sealed record PhonePrepared(string Id, string Exposure, double ExposureMs, double Iso, double L995, string Focus,
    string WhiteBalance, string Words);
public sealed record PhoneCue(string ProjectorId, int Index, int Tag, long PresentNs, long EarliestNs, int Retake, string Kind /*pattern|locate|latency*/);
public sealed record PhoneSeen(string Outcome /*seen|retake|stuck|moved*/, long Seq, int Tag, double CapturePageMs,
    int Sites, int Margin, double SettleDiff, string Why);
public sealed record PhonePicture(long Seq, int Width, int Height, PixelRect Roi, byte[] Pixels, bool Gray16, int Frames,
    long CaptureNodeNs);
public sealed record PhoneSites(string ProjectorId, ImmutableArray<TagSitePlacement> Sites, PixelRect Lit, int MinSites);
public readonly record struct Pmf1Header(string Kind, long Seq, int? Cue, int Width, int Height, PixelRect Roi,
    string Format, string Codec, int Frames, uint Crc32, double Capture);
public static class Pmf1 { public static bool TryParse(ReadOnlySpan<byte> message, out Pmf1Header header, out ReadOnlySpan<byte> payload, out string words); }

// PlayMap.Core/Scan/Tags/*.cs
public readonly record struct TagSite(int Id, PixelRect Rect, int Cell);                      // in projector pixels
public readonly record struct TagSitePlacement(int Id, ImmutableArray<Vec2d> Cells, int RowTop, int RowBottom);   // camera px
public static class FrameTag
{
    public static int Value(int patternIndex);                                   // (i mod 63) + 1
    public static bool Cell(int value, int cell);                                // lit?
    public static TagRead Read(ReadOnlySpan<byte> cellMeans /*16*/, int minPairDelta = 6, double relative = 0.2);
}
public readonly record struct TagRead(bool Valid, int Value, int Contrast, int Margin);
public static class TagOverlay { public static void Apply(Span<byte> raster, int width, IReadOnlyList<TagSite> sites, int value, byte high, byte low); }
public sealed class CellLocate
{
    public static IReadOnlyList<TagSite> Candidates(int width, int height, int cols = 8, int rows = 5);
    public static void RenderFrame(int j, Span<byte> raster, int width, int height, IReadOnlyList<TagSite> sites, byte background);
    public static CellLocateResult Decode(int width, int height, IReadOnlyList<byte[]> frames /*12*/, IReadOnlyList<TagSite> sites);
}
public sealed record CellLocateResult(ImmutableArray<TagSitePlacement> Chosen, bool[] Shadow, PixelRect Lit, string Words);

// PlayMap.Core/Scan/ReferenceSynthesis.cs, GainNormaliser.cs
public static class ReferenceSynthesis { public static void Fill(ScanPlan plan, byte[][] frames); }   // writes WhiteIndex/BlackIndex
public static class GainNormaliser { public static double Normalise(ScanPlan plan, byte[][] frames, bool[] lit); } // worst drift %

// PlayMap.Core/Scan/InverseCorrespondence.cs, TrackSet.cs
public sealed record ProjectorGridSample(Vec2d Projector, Vec2d Camera, float Confidence);
public static class InverseCorrespondence
{
    public static IReadOnlyList<ProjectorGridSample> Grid(CorrespondenceMap map, int projectorWidth, int projectorHeight, int stride = 16);
    public static Vec2d? CameraAt(CorrespondenceMap map, Vec2d projectorPixel);   // dense, local affine
}
public sealed record TrackObservation(string ViewId, string CameraRigId, Vec2d Camera, float Confidence);
public sealed record Track(string ProjectorId, Vec2d Projector, ImmutableArray<TrackObservation> Seen);

// PlayMap.Core/Calibration/MultiViewCalibration.cs
public sealed record MultiViewInput(
    IReadOnlyList<MultiViewCamera> Cameras,        // rig id, prior intrinsics, lens, calibrated (locked)
    IReadOnlyList<MultiViewProjector> Projectors,  // id, prior intrinsics, lens, locks, held pose (or null)
    IReadOnlyList<MultiViewView> Views,            // id, rig id, per-projector pairs (for initialisation)
    IReadOnlyList<Track> Tracks);
public sealed record MultiViewOptions(double HuberPx = 1.5, int MaxIterations = 30, double OutlierPx = 2.0,
    double CameraFocalPrior = 0.10, double ProjectorThrowPrior = 0.05);
public sealed record MultiViewResult(
    IReadOnlyList<SolvedView> Views, IReadOnlyList<SolvedProjector> Projectors, IReadOnlyList<SolvedCamera> Cameras,
    double Scale, bool Metric, IReadOnlyList<string> LeftOut, string Words);
public static class MultiViewCalibration
{
    public static MultiViewResult Solve(MultiViewInput input, MultiViewOptions options, IProgress<double>? progress, CancellationToken ct);
}

// PlayMap.Core/Scan/ProjectorDepth.cs, CoverageMap.cs
public sealed record ProjectorDepthMap(string ProjectorId, int Width, int Height, int Stride, float[] Depth, float[] Sigma, PointCloud Cloud);
public static class ProjectorDepth
{
    public static ProjectorDepthMap Build(SolvedProjector p, IReadOnlyList<(SolvedView View, SolvedCamera Camera, CorrespondenceMap Map)> seenBy, int stride, CancellationToken ct);
}
public sealed record CoverageMap(string ProjectorId, int Width, int Height, byte[] Views /* 0,1,2+ per cell */, double Seen, double Twice);

// PlayMap.Core/Bus/QrMatrix.cs — QR Model 2, byte mode, ECC M, versions 1–10, mask chosen by the standard penalty
public sealed record QrMatrix(int Size, bool[] Modules) { public static QrMatrix Encode(string text); public string ToBase64Bitset(); }

// PlayMap.Engine/Scanning/CuedCamera.cs — a camera told what is on the glass
public sealed record PatternCue(int Index, int Tag, long PresentNs, long EarliestNs, int Retake, string Kind);
public sealed record CuedPicture(string Outcome, Task<CameraFrame>? Pixels, string Why, double CaptureLagMs);
public sealed record CuedPrepare(string Profile, double DisplayHz, SettleOptions Settle, int Frames);
public sealed record CuedPrepared(string Exposure, string Focus, string WhiteBalance, bool SteadyPlan, string Words);
public interface ICuedScanCamera : IScanCamera
{
    /// <summary>Whether the camera reads tags (a phone, a folder) or only counts on timing.</summary>
    bool ReadsTags { get; }
    Task<CuedPrepared> PrepareAsync(CuedPrepare request, CancellationToken ct);
    Task<CameraFrame> LocateAsync(PatternCue cue, CancellationToken ct);
    Task SetSitesAsync(CellLocateResult located, CancellationToken ct);
    Task<CuedPicture> CueAsync(PatternCue cue, CancellationToken ct);
}
// CameraFrame (hunk): public bool Gray16 { get; init; } public ushort[]? Gray16Pixels { get; init; }  (facade's averaging contract)

// Exposure-lock seam shared with autoalign S8 (introduced by whichever package lands first, with this signature):
public sealed record CameraLockRequest(bool Exposure, bool Focus, bool WhiteBalance, double DisplayHz, int TargetL995);
public sealed record CameraLockState(string Exposure, string Focus, string WhiteBalance, string Words);
public interface IScanCameraControl
{
    CameraLockState State { get; }
    Task<CameraLockState> LockAsync(CameraLockRequest request, CancellationToken ct);
    Task UnlockAsync(CancellationToken ct);
}
// PhoneCamera implements ICuedScanCamera and IScanCameraControl; FolderCamera implements ICuedScanCamera.

// PlayMap.Engine/Phone/EngineServices.Phone.cs
public sealed partial record EngineServices { public PhoneHub? Phones { get; init; } }

// PlayMap.Media/Stills/StillImageReader.cs
public static class StillImageReader { public static StillImage Read(string path); }   // gray8 or gray16, optional RGB
public sealed record StillImage(int Width, int Height, byte[]? Gray8, ushort[]? Gray16, byte[]? Rgb, string Format);
```

### 4.5 NodeSettings additions (init properties, machine-local)

```csharp
public bool PhonesEnabled { get; init; } = true;        // serve /scan and accept invites
public int PhoneInviteMinutes { get; init; } = 10;      // 1–60
public int PhoneMaxConnected { get; init; } = 4;
```

Phone keys live in `NodeHome/phones.json` (`{phones:[{id, name, keySha256, rights, createdUnixMs, lastSeenUnixMs, rig}]}`,
file mode 600, written atomically), never in the project.

`ScanRunnerOptions` (hunks):

- `TagCellPx` (0 = auto)
- `MaxTagSites` (3)
- `MaxInFlight` (6)
- `PatternTimeout` (5 s)
- `MaxRetakes` (2)
- `MoveCheckLevels` (6)
- `LocateBackgroundSteady` (96)

### 4.6 `playmap.camera.v1` messages

| Direction | `t` | Fields |
|---|---|---|
| phone → node | `hello` | `v:1, ticket? \| key?, phone:{id?, name?}, ua:{platform, browser, model?}, camera:{label, width, height, fps, caps:{exposure[], exposureTime[min,max,step], iso[…], focus[], focusDistance[…], whiteBalance[], torch, zoom[…]}}, features:{mstp, rvfc, captureTime, deflate, motion, wakeLock, xrDepth}` |
| node → phone | `welcome` | `phone:{id, name}, key? (first join only), node:{id, name}, camera:<rig id>, rights[], limits:{maxInFlight, maxBinary}` |
| both | `ping` / `pong` | `t1, t4?` / `t1, t2, t3`; then `clock {offsetMs, uncertaintyMs}` node → phone |
| phone → node | `state` | `exposure:{mode, timeMs, iso}, focus:{mode, distance}, wb:{mode}, fps, motion, visible, battery?` (on change, ≤ 2/s) |
| node → phone | `prepare` | §4.4 `PhonePrepare` |
| phone → node | `prepared` | §4.4 `PhonePrepared` |
| node → phone | `cue` | `kind, projector, i, tag, at (node ms), earliest (node ms), retake` |
| node → phone | `sites` | §4.4 `PhoneSites` |
| phone → node | `seen` / `stuck` / `moved` / `aeSteady` | §4.4 `PhoneSeen`; `moved {at, magnitude}` |
| phone → node | binary `PMF1` | §3.9 |
| node → phone | `ack` | `seq` |
| node → phone | `preview` | `on, fps, width` |
| node → phone | `view` | `scan, view, state, progress, etaS, words, coverage:[{projector, seen, twice}]` + binary `PMI1` image (coverage PNG, smart-scan JPEG) |
| node → phone | `suggest` | `scan, items:[{id, label, outline:[[x,y]…] (0–1 on the view's white picture), confidence, reasons[], measured, proposal, state}]` |
| phone → node | `cmd` | `id, line` → node `reply {id, ok, payload}` |
| node → phone | `say` | `level, words` |
| either | `bye` | `why` |

---

## 5. Files

### New

| Path | What |
|---|---|
| `src/PlayMap.Core/Scan/Tags/FrameTag.cs`, `TagSite.cs`, `TagOverlay.cs`, `CellLocate.cs` | §3.7 |
| `src/PlayMap.Core/Scan/ReferenceSynthesis.cs`, `GainNormaliser.cs` | §3.6 |
| `src/PlayMap.Core/Scan/InverseCorrespondence.cs`, `TrackSet.cs`, `ProjectorDepth.cs`, `CoverageMap.cs` | §3.10 |
| `src/PlayMap.Core/Calibration/MultiViewCalibration.cs` (+ `MultiViewInit.cs`) | §3.10 |
| `src/PlayMap.Core/Scan/Phone/PhoneProtocol.cs`, `Pmf1.cs` | §3.9, §4.6 |
| `src/PlayMap.Core/Bus/QrMatrix.cs` | QR encoder (own code, ISO/IEC 18004 subset; no dependency) |
| `src/PlayMap.Net/Http/WebModules.cs` | `IWebModule`, `WebAdmission` |
| `src/PlayMap.Net/Phone/PhoneHub.cs`, `PhoneSession.cs`, `PhoneKeys.cs`, `PhoneWebModule.cs`, `PhoneFallback.cs` | §3.4, §3.9 |
| `src/PlayMap.Net/Phone/www/*` (embedded) | the page (§3.3) |
| `src/PlayMap.Engine/Phone/PhoneExtension.cs`, `EngineServices.Phone.cs`, `EngineCore.Phone.cs`, `PhoneVerbFilter.cs` | composition, verbs, STATE, context |
| `src/PlayMap.Engine/Scanning/CuedCamera.cs`, `PhoneCamera.cs`, `FolderCamera.cs`, `CuedCapture.cs` | §3.8, §3.12 |
| `src/PlayMap.Engine/Scanning/MultiViewScan.cs`, `ScanSuggestions.cs`, `ScanViewArchive.cs` | orchestration, §3.11, persistence |
| `src/PlayMap.Media/Stills/StillImageReader.cs` | FFmpeg image2 still reader |
| `src/PlayMap.App/ViewModels/PhoneInviteViewModel.cs`, `Views/QrView.cs`, `ViewModels/SuggestionCards.cs` | desk |
| `tests/PlayMap.Core.Tests/Scan/FrameTagTests.cs`, `CellLocateTests.cs`, `ReferenceSynthesisTests.cs`, `InverseCorrespondenceTests.cs`, `MultiViewCalibrationTests.cs`, `ProjectorDepthTests.cs`, `QrMatrixTests.cs`, `Pmf1Tests.cs` | |
| `tests/PlayMap.Net.Tests/Phone/PhoneHubTests.cs`, `PhoneProtocolTests.cs`, `WebModuleGateTests.cs` | |
| `tests/PlayMap.Engine.Tests/Phone/PhoneVerbTests.cs`, `FolderCameraTests.cs`, `MultiViewScanTests.cs`, `ScanSuggestTests.cs`, `PhoneScanE2ETests.cs` (`[BrowserFact]`), `BrowserFactAttribute.cs`, `PhoneBridge.cs` | |
| `tests/phone/package.json` (devDependencies `playwright@1.56.1`, `jsqr@1.4.0`), `tests/phone/unit/*.test.mjs` (`node --test`: tag reader, settle, PMF1, exposure planner), `tests/phone/page/*.spec.mjs` (fake-device and Y4M runs), `tests/phone/e2e/drive.mjs` (driven by the C# E2E), `tests/phone/fixtures/make_y4m.py` | |
| `tests/PlayMap.App.Tests/ScanWizardPhoneTests.cs` | |

### Existing, touched (small hunks)

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/StageModel.cs` | `CameraRig.Phone`, `CameraRig.Folder`, `ScanRecord.Views`, `ScanRecord.Coverage`, new records (≈ 40 lines) |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | the rows of §4.2 (≈ 15 lines) |
| `src/PlayMap.Core/Bus/Pairing.cs` | NodeSettings init properties (3 lines) |
| `src/PlayMap.Core/Scan/CorrespondenceMap.cs` | `Exclude(bool[] cameraMask)` and `ExcludeProjectorRects(...)` (≈ 20 lines); `Origin` for ROI decode (S5) |
| `src/PlayMap.Core/Scan/ScanSimulator.cs` | `RenderRaster(ReadOnlySpan<byte> projectorRaster)` (an arbitrary projector picture through the traced geometry) and an AE-hunting option in `SimOptions` (≈ 40 lines) |
| `src/PlayMap.Net/Http/HttpHost.cs` | `HttpHostOptions.Modules`; `GateAsync` module branch; `Map` calls each module (≈ 25 lines) |
| `src/PlayMap.Engine/Host/NetworkHost.cs` | `AddWebModule`, pass modules to `HttpHostOptions` (≈ 10 lines) |
| `src/PlayMap.Engine/Host/EngineParts.Ops.cs` / `OpsParts.cs` | add `PhoneExtension` (2 lines) |
| `src/PlayMap.Engine/Scanning/ScanRunner.cs` | the cued branch in `CaptureRealAsync`; `OpenCamera` defaults for Phone/Folder; `INTO` in `ReadSetup`; views and coverage in `WriteState`; multi-view solve call (≈ 120 lines; most logic in the new files) |
| `src/PlayMap.Engine/Scanning/ScanCamera.cs` | `CameraFrame.Gray16` (+ autoalign's `Rgb`) |
| `src/PlayMap.Engine/Scanning/ScanOutcome.cs` | archive `views/` and `depth/`; load them back (≈ 60 lines) |
| `src/PlayMap.Engine/Scanning/ScanSurfaces.cs`, `ScanViews.cs` | per-projector geometry and `ON <projector>`; words in camera views (S6) |
| `src/PlayMap.Engine/EngineCore.Router.cs`, `EngineCore.Ops.cs` | dispatch rows (PHONE → `EngineCore.Phone.cs`; new SCAN verbs → `MultiViewScan`/`ScanSuggestions`) |
| `src/PlayMap.Engine/StateWriter.cs`, `ContextBuilder.cs` | `phones`; scan fields; context items (≈ 30 lines) |
| `src/PlayMap.App/ViewModels/ScanWizardViewModel.cs`, `Views/ScanWizard.axaml`, `MapInspectors.cs` | the phone path, views, coverage, suggestions, right-click entries |
| `docs/WIRE.md`, `docs/ARCHITECTURE.md` §scan, `docs/UX.md` §4 step 7, `docs/ROADMAP.md` (R1.9 done; R3.9 builds on it), `docs/MAPPING.md` (new "Phone scan" section), `CLAUDE.md` (the browser test line in the loop) | papers |

---

## 6. Dependencies

**Work packages**

- **show safety** ("armed"): SCAN START, SCAN COVERAGE and wall previews are refused while armed. Phone and desk
  previews are not. Until it lands, this package checks nothing beyond the verbs' risk.
- **autoalign**: shares `IScanCameraControl` (S8 there) and `CameraFrame.Rgb` (S10 here). Its two-camera registration
  (its S7) can call `MultiViewCalibration`. It consumes the Phone and Folder cameras.
- **facade**: consumes the Folder camera (tether, manual) and gray16 averaging (S5); provides `ExposurePlan.FlickerSafe`,
  which §3.6 step 5 uses when present (otherwise the multiple-of-frame-period rule inline).
- **calibration score/history**: reads `SolvedProjector` σ, view RMS, coverage and pairing facts as score items. It keeps
  scans as snapshots with fallback.
- **companion**: variables, feedbacks and actions (§8). Its `/ws` line channel and this `/scan/ws` share `HttpHost` but
  not a session table.
- **present/framelock**: per-output latency improves the timing cross-check. Not required.
- **blend off-lane** (carry-over): BLEND SOLVE after a multi-projector APPLY should be off the lane; this package does not
  depend on it.

**Third-party**

| Library | Version | Licence | Use |
|---|---|---|---|
| None new in shipped code | — | — | The page is our own JavaScript. QR, deflate (`System.IO.Compression`), WebSockets (Kestrel), JSON (`System.Text.Json`) and maths (Core numerics) are already in the tree |
| FFmpeg (already loaded at run time from the user's install) | 9.x | LGPL/GPL per the user's build, **not linked or copied** | Still decoding for the Folder camera |
| `playwright` (test only) | 1.56.1 | Apache-2.0 | Page and E2E tests; Chromium 1194 at `/opt/pw-browsers` |
| `jsqr` (test only) | 1.4.0 | Apache-2.0 | Decodes `QrMatrix` output in a node test |

QRCoder (MIT) was considered and rejected: 1.8.1's net6.0 target pulls `System.Drawing.Common` (checked on NuGet,
13 Oct 2026).

---

## 7. Verification here

| What | How | Real or simulated |
|---|---|---|
| Tags, parity, rolling-shutter rule, cell locate, reference synthesis, gain normalisation, inverse correspondence, tracks | Core unit tests on `ScanSimulator` renders (+ `RenderRaster`), with noise, blur and an AE-hunting option | **Real code**, simulated optics |
| Multi-view BA, dense depth, merge, coverage, HOLD | Core tests on *wall-and-cylinder* and *stepped-facade* with 2 projectors and 3 views at 640×480 cameras (≤ 30 s) | **Real code**, simulated scene |
| PMF1, limits, tickets, keys, lockout, CSP headers, the gate | Net tests with Kestrel on loopback and a real `ClientWebSocket` | **Real** |
| QR | `QrMatrix` → PNG → `jsqr` in `node --test`; ISO/IEC 18004 Annex example matrix for version 1-M | **Real** |
| Page logic | `node --test` on the pure modules (tag reader, settle, PMF1, exposure planner with recorded `getSettings` sequences) | **Real** |
| Page on Chromium with fake cameras | Playwright: default fake device = "Android with locks" (exposure 10–100 step 5, focus manual: R§2.2, measured here); Y4M device = "camera without locks" (iOS stand-in); secure-context refusal over plain HTTP on 192.0.2.2; the ticket never in a request (request log); WS refusal → fallback (`routeWebSocket`) | **Real browser**, fake camera |
| Closed loop (M10) | `PhoneScanE2ETests`: HostRig with the render host on Xvfb `:99` (Mesa llvmpipe); a test `PhoneBridge` renders `SceneCamera` pictures (the scene lit by `RenderHost.ReadOutputAsync`) at 30 fps with injected latency and noise. Frames reach the page over a test-only WebSocket served by the **test's own** Kestrel (not the node), and the page's `getUserMedia` is replaced by `addInitScript` with a `MediaStreamTrackGenerator` track (Android profile: wrapped with fake capabilities whose `applyConstraints` drive the bridge's gain; iOS profile: no capabilities, the bridge's AE model hunts ±3 %). Injected faults: late frames, a dropped frame, a half-and-half frame (top rows previous pattern), one "moved" event | **Real node, real page, real render path**; simulated optics and phone camera. Chromium launched with `--no-proxy-server` (R intro) |
| Folder camera | Fake tether copying simulator PNG/TIFF frames into a temp folder after each present | **Real** (FFmpeg at `/opt/ffmpeg9`, `[FfmpegFact]`) |
| Desk | `PlayMap.App.Tests` with the fake engine link | **Real** view models |

**Compile-only or lab-only (honest list)**:

- **Real phones**: Chrome Android's actual AE lock behaviour, iOS Safari behaviour (WebKit is not in this container;
  Playwright's WebKit on Linux is not iOS), lens switching, rolling-shutter timing, thermals, real Wi-Fi bandwidth and
  jitter, the certificate click-through UX and how long the exception lasts, Safari WebSocket under an untrusted
  certificate. All go to the R0.5 lab with an iPhone and an Android phone (ROADMAP R0.5 lists both).
- **WebXR depth (S2 PHONE)**: unit-tested against a recorded fixture; no ARCore here.
- **`adb reverse` (S4)**: command construction only; no device.
- **DeviceMotion**: synthetic `DeviceMotionEvent`s dispatched by Playwright; real sensors are lab work.
- **Performance on a phone**: budgets in §3.13 marked (estimate) stay estimates until the lab.

`[BrowserFact]` finds `node` and the Playwright Chromium (`PLAYWRIGHT_BROWSERS_PATH`, default `/opt/pw-browsers`) and
skips **with the reason** on a machine without them, exactly as `[FfmpegFact]` does. In this container they run. The
loop line added to CLAUDE.md:
`cd tests/phone && npm ci && node --test unit/ && npx playwright test page/` (`PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers`).

---

## 8. Desk and Companion surface

**The phone page** (night-red, large type, one hand):

1. **Join**: "Stage Left wants this phone as its scan camera" → **Allow camera** (one tap: camera, motion and wake
   lock). Over plain HTTP: "This page needs the node's secure address" with the https link and the certificate's
   short fingerprint to compare.
2. **Check**: chips for "Exposure locked ✓" or "Brightness kept steady (iPhone can't lock exposure from a web page)",
   "Focus locked" or "Focus auto", "White balance locked", "Clock ±4 ms", "Link 6.1 MB/s". **Run check** (PHONE CHECK).
3. **Aim**: live preview; the lit area outlined after locate; a stillness meter; "Prop the phone or use a stand. Keep it
   still for about 10 s per projector."
4. **Scan**: one ring per projector with the pattern count and ETA. A red banner on motion ("Moved: set it down and tap
   Retry"). **Cancel** (SCAN CANCEL).
5. **Review**: the coverage picture per projector (green twice, amber once, red never), words, **Add a view** (move,
   aim at the red, tap: `SCAN START <me> INTO sc…`), **Show on the wall** (S1; disabled while armed), **Done**.
6. **Surfaces**: numbered outlines on the frozen white picture. Each card shows its name, its reasons and ✓ / ✗
   (APPROVE / REJECT). Grey "seen in the picture, not measured" cards can be **tapped to make** (SCAN SURFACE x y).

**The desk** (scan wizard, existing five steps):

- **Step 1, Camera**: three tiles: *This machine's camera*, *A phone*, *A folder or tethered camera*.
  - *A phone* → PHONE INVITE → a QR code, the URL, the expiry and the fingerprint words. When the phone joins, its card
    appears with capability chips and a live preview, already selected.
  - *Folder* → path picker, Advance: New file / Manual.
- **Step 3, Capture**: per view and projector progress, the phone's preview, pairing chips ("Paired by tags ✓",
  "Timing only ⚠"), Cancel.
- **Step 4, Review**: the views table (camera, projectors, RMS, tracks, exposure path; right-click **Drop view**), the
  coverage per projector, **Add a view**, **Re-solve**, **Re-solve holding…** (projector picker → HOLD).
- **Step 5, Surfaces**: suggestion cards (shared with the phone), Approve/Reject, "Ask the assistant to name them"
  (SCAN SUGGEST ASSISTANT; disabled with words when no assistant is configured), click-to-surface on any projector's
  smart scan (ON <projector>).
- **Right-click**:
  - a phone camera rig: *Invite again (QR)*, *Check phone*, *Forget phone*;
  - a scan: *Add a view with…*, *Re-solve*, *Re-solve holding…*, *Drop view…*, *Suggest surfaces*, *Show coverage on
    the wall*, *Scale from a tape measure…*.

Each entry sends the verb the table names; nothing is desk-only.

**Companion** (S7, through the companion package's tables):

- **Variables**: `phones_connected`, `phone_<n>_state` ("ready/locked", "ready/steady", "moved", "offline"),
  `scan_view`, `scan_progress`, `scan_eta`, `scan_coverage_<projector>`, `scan_suggestions_waiting`.
- **Feedbacks**: "Phone ready" (green), "Phone cannot lock exposure" (amber), "Phone moved" (red),
  "Suggestions waiting" (amber with count).
- **Actions**: **Next picture** (`SCAN NEXT`, the DSLR operator's key), **Cancel scan**, **Add view with phone** (High:
  per the companion package's policy).
- PHONE INVITE is **not** a Companion action: its answer is a secret for the asking desk.

---

## 9. Risks and open questions

| Risk | Mitigation |
|---|---|
| **iOS steady profile quality is unmeasured.** Auto-exposure on a 50 %-mean sequence may still hunt; focus may breathe | Gain normalisation, drift retakes and focus words. The lab measures decoded fraction and RMS against Android. L1 (native client) is the escape hatch. STATE always says which profile ran |
| **Tag sites unseen** (the camera sees the projection only on surfaces between the candidate grid) | 40 candidates; the timing fallback with explicit amber words; S9 hole fill; Difficult mode can use a denser grid (12×8) |
| **Certificate UX on phones** (click-through, exception lifetime, Safari WebSocket) | Fallback transport, S3 (local CA after a security review), S4 (USB). The lab script records each platform's behaviour |
| **Patent** (Summit US 11924556, exposure prediction from references with a saturation criterion) | §3.6 uses a single settle, measure and correct with a clip check, and never captures at several exposures to choose. A freedom-to-operate review is requested before release. Not legal advice |
| **Multi-view degeneracy**: views sharing too few tracks; all views nearly coaxial with a flat wall (focal against depth) | Spanning-tree checks with words; the focal prior from the rig; σ reported; `HOLD` with a known projector gives metric and well-posed solves; the score package turns σ into items |
| **A phone moves mid-view** | Motion events plus the end check on pattern 0 (observed); the view fails cleanly and nothing is half-applied |
| **Bandwidth on crowded venue Wi-Fi** | Lossless only where it matters (patterns); ROI (S5); `uploadMBps` in STATE with an amber item under 2 MB/s; a USB path (S4) |
| **Privacy**: a camera on a show network | The camera runs only on a tap; nothing is stored on the phone; pictures stay in the project's `scans/`; no pixels to an assistant; CSP blocks any third-party fetch |
| **Phone memory and thermals at 4K** | 1080p default; 4K only in Difficult with ROI; the page releases frames promptly (`VideoFrame.close()`) |
| **Open: phone intrinsics reuse** | Reused only when the profile (label, resolution) matches and σ_f < 0.5 %; focus breathing may still shift focal ~1 % (estimate). Kept as a prior, never locked, for phones |
| **Open: two cameras of different kinds in one scan** (phone + webcam) | Supported by design (per-rig intrinsics); test coverage is one sim case; lab to confirm |
| **Open: Chrome `MediaStreamTrack` transfer to a worker** | Not checked on Android. Fallback: MSTP on the main thread (Chrome exposes it there), or rVFC with a canvas |

---

## 10. Effort (agent-hours)

| Part | Must | Should |
|---|---|---|
| Core: tags, overlay, cell locate | 6 | |
| Core: reference synthesis, gain normaliser, simulator `RenderRaster` + AE option | 4 | |
| Core: inverse correspondence, tracks | 5 | |
| Core: multi-view initialisation and projector-anchored BA (+ HOLD) | 14 | |
| Core: dense per-projector depth, merge, coverage | 7 | |
| Core: protocol records, PMF1, QR encoder | 6 | |
| Net: web module seam and gate; PhoneHub (sessions, tickets, keys, clock, limits, fallback) | 15 | |
| Page: shell and UI, frame worker and fallback, exposure profiles, tags and settle, protocol, clock, motion and wake lock | 26 | |
| Engine: cued capture and PhoneCamera; INTO, SOLVE, VIEWS, DROP; STATE and context; persistence | 21 | |
| Engine: suggestions and proposals (safe AI) | 5 | |
| Engine/Media: Folder camera and still reader | 6 | |
| Desk: wizard phone path, QR, views, coverage, suggestions, right-click | 9 | |
| Tests: Core 7, Net 4, node and page 7, closed-loop E2E harness 10, multi-view sim 4 | 32 | |
| Papers | 3 | |
| **Must subtotal** | **159** | |
| S1 coverage on the wall · S2 scale (tape 2, WebXR 7) · S3 local CA 6 · S4 USB 3 · S5 averaging/ROI 4 · S6 view words 1 · S7 Companion 2 · S8 import 3 · S9 hole fill 3 · S10 colour frames 2 | | **36** |
| **Total** | | **195** |

The package splits cleanly if it must be staged:

1. **Wave A** (≈ 85 h): M1–M5, M10 single-view, M11, M12 minimal. A phone scans one view; the Folder camera unblocks
   facade.
2. **Wave B** (≈ 74 h): M6–M9, M10 multi-view, the rest of M12. Multi-view, per-projector regions, persistence and
   safe suggestions.
