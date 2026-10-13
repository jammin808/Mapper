# Work package "autoalign": auto-align from the truck — flat-screen blends, stacks, colour matching, built-in interop

*Design for round 2, 13 October 2026. The research behind it is `r2/research/projcal.md` (cited as P§n) and, for stacks and
facades, `r2/research/facade.md` (F§n). The code read is `main` at c12b186. Numbers marked "estimate" have not been measured.*

One press takes a rig of projectors of any brand from "lamps on, cables in" to an aligned, blended, colour-matched picture:
**discover → identify → reset the projectors' own processing → scan all → geometry and blend → colour and brightness → verify →
staged result with an explained score → accept (the previous result stays as the fallback)**. Flat screens, stacks (converged to
sub-pixel), cylinders and domes go through a measured 2D canvas path; free-form objects hand over to the existing 3D mapping path
and get the same photometric match, score and fallback. The projectors' built-in alignment is snapshotted, neutralised and
restorable, audited by PlayMap's camera when an operator prefers it, and never fought silently.

---

## 1. Goal and the bar

| What the best do (P§) | PlayMap's answer | Match or beat |
|---|---|---|
| **Panasonic** VSS 2.0 / Geometry Manager Pro + ET-CUK10: auto geometry, blend, curved, stacking (6 per camera), colour, brightness, white balance, black level; Panasonic projectors only; Windows PC; Nikon DSLR/Z or IDS GigE; 360° needs ≥ 3 cameras; correction written into the projector's processor (P§1) | Any brand, any camera PlayMap can open (UVC, phone, NDI, folder, GigE through a stream). Correction in PlayMap's output stage, so it is identical on every brand; where a protocol publishes it, the colour match is **pushed into the projector** (Panasonic's *measured* colour-matching mode `CMMS/CMTS/CMAI0=+00004`, white balance `VHR/VHG/VHB`) to keep its 10/12-bit precision | **Beat** on brand mix, camera choice, no licence key; **match** on in-projector precision where published |
| **Epson** built-in camera / ELPEC01 (2 MP, 15 fps): Simple Stacking of **2** (≤ 4 min), EPPT stacking ≤ 6 (≤ 9 min), Tiling Assist ≤ 15, colour calibration 1.5 min, screen matching; matte white flat or arc screens, ≤ 40 lux, ≥ 8:1, **same native resolution**, projector square to the screen, 2–30 min warm-up (P§2) | No resolution or model rule; arcs, cylinders, domes and objects; ambient checked by measured white:black contrast, not a lux rule; warm-up gated by **measured light drift**, not a timer; parallel capture of non-overlapping projectors | **Beat** on mix, shapes and time: target ≤ 90 s for 4 projectors, flat, with colour (estimate) against Epson's minutes |
| **Christie** Mystique: Twist-enabled Christie projectors (or Pandoras Box, "quality … cannot be guaranteed for non-Christie"); Lite: 3 flat, no stacks; Essentials: 2×3 double-stacked flat; Pro Venue ±70°; Premium/LSE domes and 3D; "<2 min per projector"; black-level blending as an add-on; Guardian live recal on five models (P§3) | Every edition's geometry in one flow, black-level blending measured and included, background (albedo) compensation through the measured uniformity map, re-align of one projector without touching the others | **Beat** on brand mix, price and per-projector time; **later** for live drift correction (ROADMAP M2) |
| **Barco** Auto-alignment (with Scalable, Jan 2026): Pulse projectors, onboard warp, basic setup up to 4 (P§4) | Same "camera guides the warp", any brand; Barco health and power through the Pulse driver already in Devices | **Beat** on mix |
| **Sharp NEC** ProAssist Auto Adjust: WB, black, gamma, uniformity for **two** projectors, Logitech C922N only; Multi Screen Tool: **flat only**, 2–4 tiles or a stack (P§5) | N projectors, any camera, curved | **Beat** |
| **Sony** DSMA-PC1: colour uniformity and matching for VPL-GTZ380 with α cameras (P§6) | Colour and uniformity for any projector | **Beat** |
| **Scalable** SDM/Atlas: any brand, mixed models, scheduled or show-control-triggered recalibration, ≥ 15 % overlap advice, "keystone off, dynamic black off" (P§7) | Same brand-agnostic method, local, no licence; every step a verb (show control and AI can trigger, plan and score); the projector settings Scalable asks a human to set are **set over the network** where published and checked where not | **Match** on method, **beat** on integration |
| **disguise** OmniCal: score, fallback, one-click recal (F§1, COMPETITIVE §6) | Each run is staged with an explained score; ACCEPT keeps the previous result as the fallback; REVERT is one press; history of runs; `ONLY <output>` re-aligns the projector that moved | **Match** |
| Nobody publishes a network word that **starts** a vendor's camera alignment (P§0) | `DEVICE NATIVE` reads, `DEVICE SNAPSHOT/NEUTRAL/RESTORE` keep and reset the projector's own processing; `ALIGN AUDIT` measures whatever is on the wall (a vendor result or ours) with PlayMap's camera and scores it; a context warning when an internal warp and PlayMap's correction would both act | **Beat**: the built-in becomes a measured, reversible option instead of a black box |

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **`ALIGN PLAN`** dry run: camera check (opens, colour?, resolution, frame rate), outputs and their displays, devices and what their protocol can reset, capture groups, steps with an ETA, warnings with fix lines | On a project with 4 Canvas outputs, a sim camera and 4 fake Panasonic links: the reply lists 2 capture groups for a 2×2 tile, every native setting per device with `verified`/`model-dependent`, and an ETA; nothing is shown on an output and no command reaches a fake projector (its `Received` queue stays empty) |
| M2 | **Identify**: per-group white/black flashes give each output's camera **footprint**; **binary shutter knock** ties devices to outputs in ⌈log₂(N+1)⌉ rounds per group (+1 parity round); EDID serial cross-check where the platform gives EDID; layout inference (stacks, neighbours, rows/columns); `APPLY` links devices and outputs in one edit | Sim 2×2 + 4 fake devices with shuffled `OutputId`s: all four pairings found; with `APPLY`, one undo step restores the old links. A fake whose shutter is ignored is reported "dv…: closing its shutter changed no light — not identified" and gets no guess. A 2-stack is reported as a stack (footprint overlap of the smaller ≥ 0.8) |
| M3 | **The projector's own processing**: `INativeProcessing` (Devices) with Panasonic's published settings (RZ570 table, model-dependent): geometry `GMMI0`, edge blend `EDBI0`, colour matching `CMAI0`, uniformity `UFMI1`, dynamic contrast `OAI`, Daylight View `DLVI0`, custom masking `MSKI1`, digital zoom `OZH/OZV/OZO/OZS`, frame response `FDYI0`; Christie's warp select stays gated; others answer with instructions. `DEVICE NATIVE/SNAPSHOT/NEUTRAL/RESTORE`; the snapshot is kept on the link in the project | Against `PanasonicSimulator` extended with these words: SNAPSHOT reads 12 values; NEUTRAL writes the neutral set and **confirms each by query** (stage Observed); RESTORE writes the snapshot back and confirms; a word the fake answers `ERR1` is listed `unsupported on this model` and the rest go on. The snapshot survives PROJECT SAVE/OPEN |
| M4 | **Double-correction guard**: an output with a measured correction whose device reports internal geometry or blend on (from the last `DEVICE NATIVE` read, or the snapshot taken and not neutralised) lights its `device:<id>` context item amber with the words | Test sets a fake's `GMMI0=+00001` after ACCEPT; the next poll's context item is amber with "P2's own geometry is on while PlayMap corrects op… — two warps fight; DEVICE NEUTRAL dv…" |
| M5 | **Grouped capture**: projectors whose footprints (dilated 2 % of the camera width) do not intersect share a group (greedy colouring of the overlap graph); one pattern sequence per group; decoded per footprint; **decoded maps persisted** (`scans/<id>/map-<projector>.pfm`, carry-over) | With a counting fake `IScanDisplay`, a 2×2 tile shows `2 × planFrames` scan frames, not `4 ×`. Per-projector maps equal (≤ 0.05 px mean difference) the maps of a sequential run on the same sim. After a node restart, `ALIGN RUN … ONLY` finds the other outputs' maps on disk |
| M6 | **Photometric frames** through a new `IScanDisplay.ShowRgbAsync`: per projector K, R, G, B, W and greys 64/128/192, captured as colour; a **camera stability probe** (the same reference frame at start, middle and end) refuses colour matching when the camera adjusts itself | Sim with a camera whose auto-exposure follows the scene mean: COLOR step refused with "the camera changes its own exposure or white balance (white moved 7 %) — lock them, or use a camera that can"; geometry still completes. Without auto-exposure, accepted |
| M7 | **Flat screens, 2D canvas path**: joint fit of the camera's radial distortion (k1, k2) and one homography per projector (planarity test); metric **rectification** of the plane from CORNERS (4 clicks on the camera view), else from the projectors (square pixels, principal point from the lens prior, throw-ratio prior as a soft constraint), else fronto-parallel with a warning; "up" from the projectors' median roll | Sim flat screen 2×1, 20 % overlap, webcam distortion k1 = −0.12: canvas edges straight within 0.5 projector px over the canvas width; seam misregistration (truth) ≤ 0.3 projector px RMS, p95 ≤ 0.6; the rectified canvas's aspect within 0.5 % of truth with priors only, 0.1 % with CORNERS |
| M8 | **Curved screens**: non-planar → `ScanSolver` (self-calibration) → RANSAC + LM **cylinder** (axis, radius) fit → arc-length parametrisation; `EYE <viewpoint>` parametrises by projection from a viewpoint instead | Sim concave cylinder r = 3 m, arc 120°, 3 projectors: radius within 2 %, arc-length uv error ≤ 0.5 % of canvas width, seams ≤ 0.3 px RMS |
| M9 | **Canvas target**: `FIT INSIDE` (largest rectangle of the canvas's aspect inside the covered region, summed-area search), `FIT CORNERS` (ALIGN CORNERS), `MARGIN <pct>` | Sim 2×1 INSIDE: the target touches the union's boundary on ≥ 2 sides and no target cell is uncovered; CORNERS reproduces a clicked quad within 0.3 px |
| M10 | **Measured warp** per output: a sampling grid (default 33 × 19, up to 65 × 65) of pre-warp texture coordinates at regular output positions, fitted from dense (projector px → uv) pairs by regularised robust least squares (bilinear basis, Laplacian λ, Huber δ = 0.5 px, Jacobi-PCG); 10 % held out for the residual | 100 k pairs → grid in ≤ 0.5 s; held-out residual ≤ 0.15 px on noise-free sim pairs with a known homography + 1.5 px barrel lens; invalid (uncovered) nodes flagged and drawn black |
| M11 | **Measured blend** (2D): per output map at ¼ raster; weights from each projector's distance to its raster edge and to its decoded coverage edge (canvas-space distance transform), through the curve, normalised to one in light, stack members collapsed (they share the group's weight); **measured black-level pedestal**; **uniformity gain** (`IntensityCompensation.Gain`, default 0.6); per-output transfer γ from the grey ramp written to `Blend.Gamma` | Sim 2×1 with projector γ 2.2 and 2.6: blended white in light within ±2 % across the overlap (truth irradiance); black floor after pedestal within ±5 % of the deepest overlap; uniformity 1.0 on a sim with 30 % corner fall-off gives white p5/p95 ≥ 0.95 |
| M12 | **Colour and brightness match**: per projector the camera-linear model K, M = [R−K, G−K, B−K], γ, additivity error; modes `WHITE` (diagonal gains to a common white and level) and `FULL` (3 × 3 to the common gamut: target primaries pulled inside every projector's triangle, target white, level = the dimmest achievable); applied in the output stage in linear light (`pow(A·pow(c, 2.2), 1/γ)`); DLP white-segment detection | Sim: P1 wide-gamut white 6 500 K level 1.0, P2 narrow gamut 7 500 K level 0.8: after FULL, white Δu′v′ ≤ 0.003 and levels within 3 %; R, G, B Δu′v′ ≤ 0.006 (camera space through the assumed sRGB primaries). A sim with W = R+G+B + 30 % is reported "P2 adds a white segment (W is 30 % above R+G+B): turn its BrilliantColor/white boost off; matching is approximate" |
| M13 | **Stacks converge**: members are fitted to the same measured canvas target (flat/curved) independently; a closed-loop verify pass (dot grid per member) adds a residual offset grid and iterates at most twice until p95 ≤ `StackTargetPx` (0.25) | Sim 2-stack: offset 0.8 px, zoom +0.3 %, roll 0.5°, camera at 1.0 camera px per projector px: held-out convergence (truth) p95 ≤ 0.25 px after the loop; at 0.5 camera px/projector px ≤ 0.4 px and the score says "camera sampling limits the stack" |
| M14 | **Verify**: per group a canvas dot grid (32 × 18) pre-warped on the CPU through each output's new correction and shown through `IScanDisplay`; centroid misregistration at seams and within stacks; a 50 % grey and a white field for seam ripple, uniformity and per-projector Δu′v′ | Verify numbers on the sim agree with the truth metric within 0.1 px and 1 % |
| M15 | **Explained score** (`ALIGN SCORE`): items with value, unit, penalty, light, words and a fix line; grade Good ≥ 85, Fair ≥ 70, Poor | Each of 12 items is triggered by a sim condition built to trip it (e.g. 60 % decoded → coverage red with "raise the camera's exposure or add a camera"); the fix lines parse with `VerbCatalog.Match` |
| M16 | **Runs, fallback, history**: a run stages its result (files in `align/<run>/`, never overwritten); `ALIGN ACCEPT` applies every output's `Measured`, the group's `AcceptedRunId` and history as **one edit**; the previous accepted run becomes the fallback; `ALIGN REVERT` goes back; a result equal to the current is refused | ACCEPT then UNDO restores every output's `Measured` bit for bit; REVERT after two accepts restores the first run's correction; history keeps the last 20 summaries |
| M17 | **`ONLY <output>`**: rescan one projector, keep the others' maps (from disk), re-fit its warp, recompute blends/pedestal/uniformity of its neighbours, keep their warps | Sim: move P2 by 3 px after an accepted run; `ALIGN RUN al… ONLY op2` shows patterns on op2 only (counting display), op1's warp grid is unchanged bit for bit, seams back ≤ 0.3 px |
| M18 | **Off the control lane**: the run, and BLEND SOLVE (carry-over), run on a pool task with progress; results applied as one edit at the end | `PING` answers ≤ 50 ms throughout a sim run and a 6-output BLEND SOLVE |
| M19 | **Free-form hand-over**: `SHAPE FREEFORM` (or Projector-mode outputs) runs identify, neutral, grouped scan, `ScanSolver`, stages SCAN APPLY + BLEND SOLVE, then photometric match and a measured uniformity gain map for the 3D outputs — the same score and ACCEPT/REVERT | Sim "floor-and-box" with 2 projectors: one ACCEPT applies mesh, poses, blends and colour as one undo step |
| M20 | **Render**: the output stage draws `Measured.Warp` as the first warp layer (texture coordinates of the tessellation), the colour matrix and γ, and the measured blend map from its file; files load off the render thread | llvmpipe readback: a measured grid equal to a known homography matches the CPU reference within 1 code (8-bit); identity matrix and γ = 2.2 is bit-exact passthrough; a known matrix within 1 code; a missing file shows the output without the correction and STATE says which file |
| M21 | **STATE, context, MCP, papers**: `align` in STATE, `align:<group>` context items, `devices[].native`, `outputs[].measured`; WIRE.md, PROJECTORS.md (native table), UX.md §5.0 row, ROADMAP | Verb catalogue tests (HELP syntax, risks, `SecretFrom` null); STATE keys asserted by field |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Push the match into Panasonic**: `INativeColorMatch` writes measured xyY (black/R/G/B/W → `CMMS0..4`) and targets (`CMTS0..6`), `CMAI0=+00004`; white balance gains `VHR/VHG/VHB`; `AT PROJECTOR\|AUTO`. Gated as unverified until the `Y` scale is confirmed on a real projector | Simulator receives the exact lines with the right ×1000 x,y encoding; refused with the gate's words without `allowUnverified` |
| S2 | **Dome** (sphere fit) with azimuthal-equidistant (domemaster) and equirectangular parametrisation | Sim hemisphere r = 4 m: radius ≤ 2 %, seams ≤ 0.3 px |
| S3 | **`FIT BORDER`**: the screen's frame detected in the white−black image (edges, 4 RANSAC lines inside the union's dilation) | Sim screen with a 5 cm black border: corners within 1 camera px |
| S4 | **Epson transports**: UDP 3629 discovery; Epson Web API (`/api/v01/control/escvp21`, Digest `EPSONWEB`) as an alternative to ESC/VP.net when EPPT holds TCP 3629. The Web Control password comes from the node's credential store under the link's `CredentialRef`, never from a line | Fake HTTP server with Digest auth: `PWR?` round trip; wrong password → `Rejected` with words; the password appears in no journal row or STATE |
| S5 | **`ALIGN AUDIT`**: verify-only on what is on the wall (a vendor's built-in result or ours), scored the same way | A sim with 1.5 px stack misregistration scores the stack item amber/red with the measured value |
| S6 | **Warm-up gate**: the COLOR step waits (or is deferred to a later `STEPS COLOR`) until device on-time ≥ `AlignWarmupMinutes` (20, Epson's guidance P§2) or measured white drift < 0.5 %/min over 60 s | Sim light ramp 3 %/min → deferred with words and a fix line; 0.2 %/min → runs |
| S7 | **Two cameras** with ≥ 20 % overlap for wide screens (per-camera solve, registration through shared projectors' pairs) | Sim 4×1 wide screen, two cameras: seams ≤ 0.4 px |
| S8 | **Camera control seam** `IScanCameraControl` (exposure, gain, white-balance lock): V4L2 controls on Linux, DirectShow `IAMCameraControl`/`IAMVideoProcAmp` on Windows; the phone page's `applyConstraints({exposureMode, whiteBalanceMode})` | Compile-only for V4L2/DirectShow (no camera here); the control→ioctl mapping is unit-tested |
| S9 | **`DEVICE COLOR`** verb for an operator's gains / match off | Simulator round trip |

### Later

Starting a vendor's own camera alignment over the network (no published word, P§0; the `INativeAlignment` seam exists and refuses);
an Epson's built-in camera as a PlayMap camera through "Remote Camera Access" (endpoint unread); warp upload into projector warp
engines (ROADMAP M27); colorimeter/spectro support (M22); content-based live drift watch (M2); PTZ zoomed captures for stacks at a
distance; MPCDI import; seam placement by content saliency; multi-camera domes with more than two cameras; estimating the camera's
radiometric response from overlap additivity (Debevec-style) instead of assuming sRGB.

---

## 3. Architecture

### 3.1 The flow

```
ALIGN RUN al…                                     (AlignRunner, a pool task; progress → STATE.align.run)
 ├─ 0 plan        camera opens? colour? devices reachable? outputs open? group colouring; ETA          ~1 s
 ├─ 1 power       only with POWER ON: DEVICE POWER ON for every link, wait until ReadStatus says On      0–90 s
 ├─ 2 neutral     per device: SNAPSHOT (if none) → NEUTRAL (confirmed by query) | instruction words     ~1 s/device, parallel
 ├─ 3 identify    per capture group: all-black, then each output white alone → footprints;
 │                binary shutter knock over the group's devices → device ↔ output; EDID check; layout   ~3–8 s
 ├─ 4 scan        per group: the structured-light plan on all group outputs at once → decode →
 │                split by footprint → per-projector CorrespondenceMap (persisted)                       ~5–8 s/group (UX §5.1 estimate)
 ├─ 5 solve       shape: flat test (distortion + homographies) | ScanSolver → cylinder/sphere | freeform
 │                rectify → canvas target → per-output measured warp → blend, pedestal, uniformity       ≤ 3 s (4 outputs, 640×480 camera)
 ├─ 6 color       per group: K R G B W 64 128 192 (+ the stability reference) → models → target → A_j     ~2–3 s/group
 ├─ 7 verify      per group: dot grid through the new correction; grey and white fields; stack loop     ~2 s/group (+1 loop for stacks)
 └─ 8 stage       files to align/<run>/; score; STATE "staged"; ACCEPT AUTO <score> applies when allowed
```

Target for 4 projectors (2 × 2, flat, 1 webcam, colour): ≤ 90 s from Run to staged (estimate; vendor references P§8). Every step
can be run alone (`STEPS`), every step answers in words why it stopped, and `ALIGN CANCEL` returns the outputs to the show and
writes back the projectors' snapshots unless the run was accepted.

### 3.2 Components

| Where | Component | Job |
|---|---|---|
| Core `Align/` | `Footprints` | white−black difference per output in camera space → mask (Otsu on the difference, close 3 px, largest component), area, centroid, hull |
| | `Layout` | overlap graph (fraction of the smaller), stacks (≥ 0.8), neighbours (> 0.02), rows/columns by centroid clustering, capture groups by greedy colouring with a 2 % dilation guard |
| | `ShutterKnock` | binary codes over devices, per-footprint dark/lit decisions (drop > 50 % of the footprint's mean white−black), parity check, words for a device that changed nothing |
| | `PlanarFit` | joint LM over camera radial distortion (k1, k2, centre fixed at the image centre unless the rig is calibrated) and per-projector homographies from dense pairs (Huber); planarity = robust RMS < 0.5 projector px |
| | `PlaneRectify` | metric rectification of the undistorted camera image of the plane: from 4 corners (homography to the target rectangle), or from the projectors: unknowns the plane's rectifying transform (4 dof beyond similarity) + each projector's focal; residuals the orthonormality of K_j⁻¹ G_j's first two columns (square pixels, principal point from `Projector.Intrinsics` shift), plus focal priors from throw ratio (σ 5 %) and the camera's intrinsics when calibrated; LM; covariance → "corners ±x %" |
| | `ScreenFit` | plane (SVD), vertical cylinder (axis seed = mean projector up; RANSAC on circle fits in the plane ⟂ axis; LM over axis direction, point, radius), sphere (4-point RANSAC + LM); RMS and coverage |
| | `ScreenParam` | X → raw (s, t): plane: in-plane axes (u = up × n); cylinder: arc length r·θ and height; dome: azimuthal equidistant; `EYE`: projection through a `Viewpoint` |
| | `CanvasTarget` | coverage raster in (s, t) (1024 wide); INSIDE: binary search on height with a summed-area table; CORNERS: param of the 4 rays → homography (flat) or bilinear (curved); MARGIN |
| | `MeasuredWarpFit` | scattered (projector px q → uv) to a grid; bilinear basis per cell; normal equations with a Laplacian (λ = 1e-3·pairs/nodes) and Huber IRLS (3 passes); Jacobi-preconditioned CG per axis, ≤ 200 iterations; 10 % held out; node validity = ≥ 4 pairs within the node's support, else filled by the Laplacian and marked invalid outside the decoded hull |
| | `CanvasInverse` | per projector, the forward grid rasterised into a canvas raster (each cell's quad split in two triangles, barycentric) → canvas cell → projector px: the inverse the blend needs |
| | `MeasuredBlend2D` | per output map pixel m (¼ raster): uv = W_j(m); covering set = projectors whose inverse is defined at uv; weight_k = Curve(min(edge distance in k's raster / (EdgeWidth·side), coverage-edge distance / band)) through the curve; stacks collapse (`StackGroup` members take the group's weight, F§3.9); normalise to 1 in light; pedestal and uniformity (§3.5); output `BlendMap` (weights and pedestal through 1/γ_j, as `BlendSolver3D` writes them) |
| | `Photometry` | per projector, robust medians over the central 50 % of its footprint outside overlaps: K, R, G, B, W, greys; camera decode (sRGB EOTF unless the rig says linear); γ by least squares on log levels; additivity error |
| | `ColorTarget` | WHITE: diagonal gains to the common white (chromaticity mean, pulled into every projector's reachable set) and the dimmest level; FULL: primaries pulled toward the white until inside every triangle (bisection per primary), level scaled so that Σ_k max(A_j[i,k], 0) ≤ 1 for every j, i |
| | `VerifyAnalysis` | dot detection (local maxima of the dot frame minus black, intensity-weighted centroid in a 7 × 7 window, sub-pixel), per dot the projector-px misregistration through the local Jacobian; seam ripple: luminance profile across each seam band vs its two sides; Δu′v′ between projectors' regions |
| | `AlignScore` | items, penalties, grade, fix lines (§4.3) |
| Core `Scan/` | `MultiProjectorSim` | the simulated world for align: several projectors (each a `SimGeometry` trace of the same scene and camera) composed in linear light from what each displays (grey or RGB), per-projector response (3 × 3 primaries in camera RGB, black, γ, white-segment boost), ambient, camera vignetting, sRGB encode, exposure, optional auto-exposure; truth (world point and projector px per camera pixel per projector) |
| | `SimScenes` + | "flat-screen" (4 × 2.25 m, optional 5 cm black border), "curved-screen" (concave cylinder r 3 m, arc 120°, h 2 m), "dome" (should); layouts `tile<c>x<r>[:o<pct>]`, `stack<n>[:j<px>]` in the simulated camera's source string ("flat-screen:tile2x2:o20") |
| Engine `Align/` | `AlignRunner` | the job (steps, cancellation, progress, words); `IAlignBackend` |
| | `AlignArchive` | `align/<run>/` read/write (written beside, moved over, as `ScanArchive`) |
| | `EngineCore.Align.cs` | the ALIGN verbs, ACCEPT/REVERT edits, STATE/context writers |
| | `AlignExtension` | attached after "scan" in `OpsParts.Extensions()` ("align") |
| Engine `Scanning/` | `PatternCapture` (extracted from `ScanRunner`) | show a frame on a set of outputs at once (await every present, keep the latest instant), keep the first settled camera frame after present + latency, grey and colour; used by SCAN START and the runner |
| Engine `Devices/` | `DeviceService` + | `NativeAsync`, `SnapshotAsync`, `NeutraliseAsync`, `RestoreAsync`, `KnockAsync` (shutter subsets with the settle rule of `BlackFrame`) |
| Devices | `INativeProcessing`, `INativeColorMatch`, `INativeAlignment` | optional driver interfaces (§4.4); Panasonic implements the first two; every driver refuses the third with the reason |
| Render | `WarpMeshBuilder.Tessellate(field, measured)`, output shader `uMatch`, `StageResources` measured blends | §3.7 |

### 3.3 Geometry, flat: why it holds for any brand and where precision comes from

- Every projector's correction is fitted to **the same measured target**: the canvas coordinate of the screen point the camera sees
  at a pixel. Two projectors lighting that point both decode to it through the same camera pixel, so their warps agree there to the
  decode's precision whatever the camera's distortion or the rectification's error — those move the whole canvas, never a seam.
  This is why the method is brand-, model- and resolution-agnostic.
- What the rectification affects is global: straight edges and the canvas's aspect. The joint distortion + homography fit removes
  the webcam's barrel (a plane seen through a distorted lens is not a homography; several projectors' homographies sharing one
  distortion pin it down). The aspect comes from CORNERS when the screen has a frame (exact), else from the projectors' priors
  (square pixels, known shift), whose uncertainty the reply states ("corners ±0.3 %").
- Decode precision: 4-step phase over a 16 px period with 2 % intensity noise gives σ ≈ 16·(0.02/√2)/2π ≈ 0.04 projector px per
  camera sample (estimate); a grid node averages hundreds of samples. The limits are systematic: camera sampling below ~0.5
  camera px per projector px blurs the phase (scored, M13), and projector lens distortion, which the residual grid absorbs.
- Interpolation: the GPU interpolates texture coordinates linearly inside the tessellation's triangles (33 × 19 grid × 8
  subdivisions → ~7 px cells at 1080p); a homography's curvature over 7 px is < 0.01 px (estimate, checked in the render test).

### 3.4 Geometry, curved and free-form

- **Shape AUTO**: the planar fit's RMS decides flat (< 0.5 px). Otherwise `ScanSolver` (self-calibration of camera and projectors;
  bridged into the stage's model when one exists) triangulates the cloud; `ScreenFit` tries cylinder then sphere and keeps the one
  whose RMS < 1 % of its radius with ≥ 80 % inliers; otherwise FREEFORM.
- **Cylinder/dome**: uv(c) = target(param(ray_c ∩ fitted surface)); the per-projector grid is fitted from (p_j(c), uv(c)) exactly as
  for flat. Shape error moves content smoothly but never opens a seam (same camera-pixel argument). The scale is only needed for
  words (radius in metres when the scan is metric; otherwise "radius ×1.00 of the camera distance").
- **Free-form** hands over: the run stages `SCAN APPLY <scan>` and an off-lane `BLEND SOLVE`, then adds per-output colour matrices
  and a measured uniformity gain map in output raster space (from the white maps through each projector's decode), stack residual
  lattices through facade's `StackAlignment` when the stage has stacks. ACCEPT applies all of it as one edit.

### 3.5 Photometry, blend floor and uniformity

Per projector j, in camera-linear RGB (sRGB EOTF on 8-bit webcams; identity for rigs marked linear):

```
L_j(s) = K_j + M_j · s^γ_j          M_j = [R_j−K_j  G_j−K_j  B_j−K_j]   (3×3, columns = primaries)
additivity_j = |W_j − K_j − (R_j+G_j+B_j−3K_j)| / |W_j − K_j|          > 5 % → "white segment / non-additive" item
γ_j: least squares of log((grey_v − K)/(W − K)) on log(v) for v ∈ {64,128,192}/255 (per channel in ROBUST)
```

- **FULL**: target primaries P* = each projector primary's chromaticity averaged, moved toward the target white until inside every
  projector's triangle (bisection); target white = mean white pulled inside every reachable set; M* = P* scaled so P*·1 = white
  at level Y*. A_j = M_j⁻¹·M*, Y* chosen as the largest level with Σ_k max(A_j[i,k], 0) ≤ 1 for every j and row i. **WHITE**:
  A_j diagonal. Output stage: `c' = pow(A_j · pow(c, 2.2), 1/γ_j)` — content's 2.2 in, each projector's own γ out, so the
  transfer curves match too (NEC's "gamma tone", P§5).
- **Black floor** (measured, M11): B(uv) = Σ_k K_k(uv) over the covering projectors (black leaks whatever the weights);
  T = p99 of B over the target;
  lift L(uv) = T − B(uv) shared by weight; pedestal_j = (w_j·L/(W_j−K_j))^(1/γ_j) — `BlendMap.Pedestal`'s meaning. The reply says
  what it costs: "black lifted to the overlap's floor: contrast 1 800:1 → 950:1 (BLACK OFF keeps it)".
- **Uniformity**: Wsum(uv) = Σ_k w_k(W_k−K_k); target = `IntensityCompensation.Gain(Wsum, p2(Wsum), Uniformity)`; the gain (≤ 1,
  low-passed σ = 2 % of the canvas width) folds into each weight in light. Default 0.6 because a camera's vignetting and a gain
  screen's angle response bias the measurement (§9).
- **Warm-up**: Epson recommends 20–30 min before colour calibration (P§2). The run measures W of one projector at the start and
  end of the colour step; drift > 1 % per minute marks the item amber and offers `ALIGN RUN … STEPS COLOR` later (S6 automates).

### 3.6 Identify: footprints and the shutter knock

1. Per capture group (from the plan's provisional grouping, all outputs in one group when nothing is known): all outputs black →
   frame B; each output white alone → frames W_o. Footprint_o = mask(W_o − B). Non-overlapping outputs of one group can be lit
   together on a second pass; the first identification of an unknown rig is sequential (N + 1 frames, ~0.2 s each).
2. Knock: every group output white. Round r closes the shutters of devices whose index has bit r set (`DeviceService.KnockAsync`,
   settle = the protocol's documented fade + margin, as `MappingWorkflows.BlackFrame`), captures, opens. Each footprint's
   dark/lit pattern across rounds is its device's index; a parity round catches a device that ignored the command. Stacks: the
   members' common footprint loses only that member's share; the knock runs per output for stack members (one white at a time).
3. EDID (where `DisplayInfo` carries it; Linux `/sys/class/drm/*/edid`, Windows registry — compile-only) is a cross-check, never
   a replacement: a contradiction is reported, not resolved silently.
4. Reply: `{outputs:[{id, footprint:{area, centroid:[x,y], bbox}, device, how:"knock"|"edid"|"pattern"|"none", confidence}],
   stacks:[[op…]], grid:{columns, rows}, lines:["SET dv… outputId \"op…\"", …]}`; `APPLY` makes the links one edit.

### 3.7 Render: where the correction goes

```
pre-warp picture (canvas region or projector render)
  → tessellation: grid vertex (s,t) ∈ [0,1]²: position = operator WarpField(s,t); texcoord = Measured.Warp(s,t) or (s,t)
  → fragment: picture(texcoord) → [uMatch] lin = pow(c, 2.2); lin = A·lin; c = pow(lin, 1/γ)
  → × mask × blend weight + pedestal (measured BlendMap when Measured.Blend, else the 3D or edge map; GainOnly multiplies)
  → trim → dither
```

- `WarpMeshBuilder.Tessellate(field, MeasuredWarp?)`: the measured grid is evaluated bilinearly at each tessellation vertex. An
  invalid node gives texcoord (−1, −1) and the shader's existing `inside` test draws black. The operator's corner pin and lattice
  stay touch-ups on top (positions), as facade's design orders them (F `Output.Measured` before the operator's warp).
- Shader: `uniform int uMatch; uniform mat3 uMatrix; uniform float uGammaOut;` — one branch before the mask/blend line. With
  `uMatch == 0` the shader is unchanged (bit-exact test).
- `StageResources`: an output whose `Measured.Blend` names a file uses that map (`Replace`) or multiplies its 3D/edge map
  (`GainOnly`); files are read and decoded on the blend loop's task, keyed by hash; a missing or corrupt file is a fault in STATE
  ("op…: align/ar…/blend-op….pfm missing — the output runs without its measured blend") and the output falls back.
- The D3D11 path of the present package needs the same three uniforms in HLSL (compile-only there).

### 3.8 Threads and lanes

| Work | Where | Applies as |
|---|---|---|
| The run | `AlignRunner` task (pool), one run per node; SCAN START and ALIGN RUN exclude each other (one camera, one display) | ACCEPT: one edit on the control lane |
| Device commands | `DeviceService` (one in flight per projector, as today); neutralise/snapshot in parallel across projectors | snapshot stored on the link: one edit per run |
| Maths | `Task.Run` with the run's token; per-output fits in parallel (4 cores) | — |
| Frames | `PatternCapture` → `IScanDisplay` (render thread draws), camera thread delivers | — |
| BLEND SOLVE (carry-over) | pool task with progress (`STATE.blend`), the result offered to the render host as today, the verb answers `accepted` when it takes > 200 ms | the maps (runtime) |
| Blend file loads | `StageResources` blend loop | runtime |

### 3.9 Formats

- `align/<run>/run.json` (System.Text.Json, ProjectJson options): `{version:1, id, group, startedUnixMs, finishedUnixMs, state,
  steps:[{step, ms, words}], camera:{id, width, height, colour, linear}, shape:{…ScreenModel}, target:{…}, outputs:[{id, projector,
  device, footprint, warp:{columns, rows, file, hash, residualPx, heldOut}, blend:{width, height, file, hash, use}, color:{matrix[9],
  gamma, white:[x,y,Y], additivityPct}, decodedPct, cameraPxPerProjectorPx}], verify:{…}, score:{…}, scan:"sc…"}`.
- Grids and maps: **PFM** (`PF\n<w> <h>\n-1.0\n` + little-endian float32 RGB rows bottom-up — the format MPCDI uses for warps):
  `warp-<op>.pfm` (u, v, valid), `blend-<op>.pfm` (weight, pedestal, gain — the BlendMap's signal values), `map-<pj>.pfm` in the
  scan folder (projector x, y, confidence; NaN for undecoded).
- `photometry.json`: per projector K, R, G, B, W, greys (camera-linear), γ, additivity, drift.
- Files are written beside and moved over; a run folder is never rewritten, so undo and REVERT always find their files.

### 3.10 Performance budgets (this container, 4 cores)

| Operation | Budget |
|---|---|
| Planar fit (distortion + 4 homographies, 4 × 50 k pairs) | ≤ 0.6 s |
| Cylinder fit (200 k points) | ≤ 1 s |
| Warp grid fit (100 k pairs → 33 × 19), per output | ≤ 0.5 s |
| Blend 2D + pedestal + uniformity, 4 outputs at 480 × 270 | ≤ 0.8 s |
| Whole solve step, 4 outputs, 640 × 480 camera | ≤ 3 s; 1920 × 1080 camera ≤ 8 s |
| Sim ALIGN RUN 2 × 2 with colour and verify, end to end (simulated display and camera) | ≤ 30 s wall |
| Control lane during a run | PING ≤ 50 ms |
| Memory for a run, 1920 × 1080 camera, 4 projectors | ≤ 1 GB (frames kept per group only until decoded) |

---

## 4. Spine needs

### 4.1 Model (new file `src/PlayMap.Core/Model/AlignModel.cs`; all additions init-only with defaults, so older files read unchanged)

```csharp
public enum ScreenShape { Auto, Flat, Cylinder, Dome, Freeform }
public enum CanvasFit { Inside, Corners, Border }
public enum ColorMatchMode { Off, White, Full }
public enum ColorApplyAt { Output, Projector, Auto }
public enum MeasuredBlendUse { Replace, GainOnly }

/// <summary>The screen as measured: json "screen".</summary>
public sealed record ScreenModel(
    ScreenShape Shape,        // "shape"
    Vec3d Origin,             // "origin": plane point, cylinder axis point, sphere centre (stage frame; camera frame scaled when not metric)
    Vec3d Axis,               // "axis": plane normal, cylinder axis, dome zenith
    Vec3d Up,                 // "up"
    double RadiusM,           // "radiusM": 0 for flat
    double ArcDeg,            // "arcDeg": cylinder arc used by the target
    double WidthM, double HeightM,  // "widthM", "heightM": the target's extent on the surface
    double RmsM,              // "rmsM": fit residual
    bool Metric);             // "metric"

/// <summary>json "target".</summary>
public sealed record AlignTarget(
    CanvasFit Fit,                          // "fit", default Inside
    double Aspect,                          // "aspect": 0 = the canvas's own
    ImmutableArray<Vec2d> CameraCorners,    // "cameraCorners": TL TR BR BL, 0–1 of the camera picture; [] when unset
    string ViewpointId,                     // "viewpointId": "" = arc/plane parametrisation
    double MarginPct)                       // "marginPct": 0
{ public static readonly AlignTarget Default = new(CanvasFit.Inside, 0, [], "", 0); }

/// <summary>json "options".</summary>
public sealed record AlignOptions(
    ColorMatchMode Color,        // "color" Full
    ColorApplyAt ApplyAt,        // "applyAt" Output
    double Uniformity,           // "uniformity" 0.6
    bool BlackLevel,             // "blackLevel" true
    BlendCurve Curve,            // "curve" Cosine
    double EdgeWidth,            // "edgeWidth" 0.15 (fraction of the raster side, as BlendSolveOptions)
    int GridColumns, int GridRows,   // "gridColumns" 33, "gridRows" 19 (2..65)
    double StackTargetPx,        // "stackTargetPx" 0.25
    bool Parallel,               // "parallel" true (grouped capture)
    string Mode)                 // "mode" "FAST" | "ROBUST"
{ public static readonly AlignOptions Default = new(ColorMatchMode.Full, ColorApplyAt.Output, 0.6, true, BlendCurve.Cosine, 0.15, 33, 19, 0.25, true, "FAST"); }

public sealed record AlignRunSummary(string Id, long StartedUnixMs, string State, int Score, string Grade, string Words);  // State: staged|accepted|fallback|reverted|failed|cancelled

/// <summary>A set of outputs aligned together through one camera onto one canvas (or a free-form stage). Id "al".</summary>
public sealed record AlignGroup(
    string Id, string Name,
    string CameraId,                         // "cameraId"
    ImmutableArray<string> OutputIds,        // "outputIds"
    string CanvasId,                         // "canvasId": "" for Freeform
    ScreenShape Shape,                       // "shape": requested (Auto by default)
    ScreenModel? Screen,                     // "screen": the last accepted measurement
    AlignTarget Target, AlignOptions Options,
    string AcceptedRunId, string FallbackRunId,          // "acceptedRunId", "fallbackRunId"
    ImmutableArray<AlignRunSummary> History) : IEntity;  // "history": newest first, ≤ AlignRunsKept

// Shared with the facade package (one record, whichever lands first adds every field):
public sealed record MeasuredWarp(int Columns, int Rows, string File, string Hash, double ResidualPx);       // texture coordinates at regular output positions, in File
public sealed record MeasuredBlend(int Width, int Height, string File, string Hash, MeasuredBlendUse Use);
public sealed record MeasuredColor(ImmutableArray<double> Matrix, double Gamma, double WhiteX, double WhiteY, double Level, bool InProjector);  // Matrix 9, row-major, linear light
public sealed record MeasuredCorrection(
    MeasuredWarp? Warp,          // "warp"   (this package)
    WarpLattice? Lattice,        // "lattice" (facade: small stack residual, offsets)
    MeasuredBlend? Blend,        // "blend"
    MeasuredColor? Color,        // "color"
    double GainR, double GainG, double GainB,   // facade's stack gains (1 = none)
    double ResidualPx,
    string ScanId, string StackId, string RunId,
    long MeasuredUnixMs);

/// <summary>A projector's own processing as read before PlayMap reset it. json "snapshot" on the link.</summary>
public sealed record NativeSnapshot(long ReadUnixMs, ImmutableSortedDictionary<string, string> Values, ImmutableArray<string> Unsupported, bool Neutral);
```

Additions to existing records:

| Record | Property (json) | Default |
|---|---|---|
| `Stage` | `Aligns` (aligns) | [] |
| `Output` | `Measured` (measured) — shared with facade | null |
| `DeviceLink` | `Snapshot` (snapshot) | null |
| `CameraRig` | `Linear` (linear): the camera delivers linear values (GigE raw) | false |
| `Ids` | `Align = "al"`, run ids `"ar"` + 8 hex (not entities) | — |

`Project.AllIds` includes the groups. Generic `GET/SET` reach every field (`SET al… options.color "WHITE"`); `ADD align` is refused
with "use ALIGN NEW"; `REMOVE al…` removes the group, never the outputs' `Measured` (that is ALIGN CLEAR's job).

### 4.2 Verbs (`VerbCatalog`, a new category `Align`, and five `Device` verbs, one of them should; none carries a secret)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| ALIGN NEW | `ALIGN NEW <name> <camera> <output>…\|ALL [CANVAS <canvas>\|NEW] [SHAPE AUTO\|FLAT\|CYLINDER\|DOME\|FREEFORM]` | Review | yes | `{id:"al…", outputs, canvas, words}`; CANVAS NEW makes a canvas sized to the outputs' summed width; Projector-mode outputs need SHAPE FREEFORM or are refused with the SET line that would change them |
| ALIGN PLAN | `ALIGN PLAN <group>\|<camera> [<output>…\|ALL]` | Low | no | `{camera:{ok,colour,width,height,fps,words}, outputs:[{id,display,open,device,protocol,native:[{name,verified,neutral}]}], groups:[[op…]], steps:[{step,etaS,words}], etaS, warnings:[{words,fix}], lines:[…]}` |
| ALIGN RUN | `ALIGN RUN <group> [STEPS <step>[,<step>…]\|ALL] [ONLY <output>…] [POWER ON] [SHAPE …] [FIT INSIDE\|CORNERS\|BORDER] [ASPECT <w>:<h>] [COLOR OFF\|WHITE\|FULL] [AT OUTPUT\|PROJECTOR\|AUTO] [MODE FAST\|ROBUST] [ACCEPT ASK\|AUTO <score>]` | High (patterns, shutters, projector settings) | yes | `{stage:"accepted", run:"ar…", etaS, steps:[…]}`; progress and the end in STATE.align.run; refused when another run or a scan is running, when the node is armed (show-safety), or when a step would change nothing |
| ALIGN STATUS | `ALIGN STATUS` | Low | no | the run object of STATE |
| ALIGN CANCEL | `ALIGN CANCEL` | Low | yes | `{restored:[dv…], words}` |
| ALIGN ACCEPT | `ALIGN ACCEPT <run>` | Review | yes | `{group, applied:[op…], fallback:"ar…", score}`; one undo step |
| ALIGN REVERT | `ALIGN REVERT <group> [TO <run>]` | Review | yes | `{group, applied, run}` |
| ALIGN CLEAR | `ALIGN CLEAR <output>\|<group>` | Review | yes | removes `Measured` from the outputs (one edit) |
| ALIGN SCORE | `ALIGN SCORE <run>\|<group>` | Low | no | `{score, grade, items:[{key,value,unit,penalty,light,words,fix}]}` |
| ALIGN HISTORY | `ALIGN HISTORY <group>` | Low | no | summaries, newest first |
| ALIGN CORNERS | `ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4>\|CLEAR` | Review | yes | `{target}`; 0–1 of the camera picture, TL TR BR BL |
| ALIGN IDENTIFY | `ALIGN IDENTIFY <camera> [<output>…\|ALL] [KNOCK ON\|OFF] [APPLY]` | High (flashes, shutters) | yes | §3.6 |
| ALIGN AUDIT | `ALIGN AUDIT <group>` (should) | High (verify frames) | yes | the score of what is on the wall |
| DEVICE NATIVE | `DEVICE NATIVE <device>` | Low | no | `{device, settings:[{name,value,neutral,verified,caveat}], unsupported, snapshot:{readUnixMs,neutral}\|null, words}` (a live read) |
| DEVICE SNAPSHOT | `DEVICE SNAPSHOT <device>` | Review | yes | the snapshot, stored on the link (one edit) |
| DEVICE NEUTRAL | `DEVICE NEUTRAL <device>` | High | yes | snapshot first when none; `{set:[…], confirmed:[…], unsupported:[…], instructions:[…]}` with the stage (Observed only when every query confirms) |
| DEVICE RESTORE | `DEVICE RESTORE <device>` | High | yes | `{written:[…], confirmed:[…]}`; refused without a snapshot |
| DEVICE COLOR | `DEVICE COLOR <device> GAINS <r> <g> <b>\|MATCH OFF\|MATCH <run>` (should) | High | yes | the driver's receipt |

Steps: `POWER, NEUTRAL, IDENTIFY, SCAN, SOLVE, COLOR, VERIFY` (ALL = all but POWER). The split DEVICE verbs keep a read Low and
every write High without waiting for per-argument risk (carry-over "RiskOf(line)"); if that lands, nothing here needs changing.
The context's command list gains these verbs with their risks: an assistant may run PLAN, SCORE, HISTORY and DEVICE NATIVE on its
word, queues NEW/ACCEPT/REVERT/CORNERS for review, and can only propose RUN, IDENTIFY, AUDIT, NEUTRAL and RESTORE.

### 4.3 STATE and context

```
align.run      {id, group, state:"running"|"staged"|"failed"|"cancelled", step, progress, etaS, words,
                outputs:[{id, name, step, progress, light, words}], score?}          (absent when no run since start)
align.groups   [{id, name, shape, outputs, canvas, accepted, fallback, staged, score, grade, light, words}]
devices[].native   {snapshot: bool, neutral: bool, internal:["geometry","blend","colour matching"…], words}
outputs[].measured {run, residualPx, colour: bool, blend:"replace"|"gain"|null, files:"ok"|"missing: …"}
blend          {state:"solving"|"done", progress, words}                              (BLEND SOLVE off the lane)
```

Score items (key — value — light thresholds — fix):

| Key | Value | Green / amber | Fix line |
|---|---|---|---|
| coverage | decoded % of each footprint (min) | ≥ 90 / ≥ 75 | `ALIGN RUN al… MODE ROBUST` |
| sampling | camera px per projector px (min) | ≥ 0.8 / ≥ 0.5 | "move the camera closer or use a higher resolution" |
| warp | held-out residual, projector px (max) | ≤ 0.3 / ≤ 0.6 | `ALIGN RUN al… STEPS SCAN,SOLVE` |
| seams | verify misregistration p95 | ≤ 0.5 / ≤ 1 | idem |
| stacks | convergence p95 | ≤ StackTargetPx / ≤ 2× | idem with MODE ROBUST |
| canvas | rectification source and ± % | corners or border / priors ≤ 0.5 % | `ALIGN CORNERS al… …` |
| ripple | seam luminance ripple % | ≤ 3 / ≤ 6 | `SET al… options.edgeWidth 0.2` |
| uniformity | white p5/p95 | ≥ 0.9 / ≥ 0.8 | `SET al… options.uniformity 0.8` |
| colour | max Δu′v′ between projectors | ≤ 0.004 / ≤ 0.008 | `ALIGN RUN al… STEPS COLOR` |
| level | max luminance mismatch % | ≤ 3 / ≤ 6 | idem |
| warmup | white drift %/min during COLOR | ≤ 0.5 / ≤ 1 | idem after warm-up |
| additivity | max %, names the projector | ≤ 5 / ≤ 15 | "turn BrilliantColor / white boost off on P…" |
| contrast | white:black on the screen (min) | ≥ 8 / ≥ 4 (Epson's 8:1, P§2) | "darken the room" |
| native | internal processing left on | none / instructions only | `DEVICE NEUTRAL dv…` |

Score = 100 − Σ penalties (amber 5, red 15, capped at 100); grade Good ≥ 85, Fair ≥ 70, else Poor.

Context items (Show Bus §6, existing lights): `align:<group>` — red when the last run failed and nothing is accepted, or a
measured file is missing; amber when the accepted score is Fair/Poor or a colour pass ran before warm-up; headline "Main screen:
91 Good — seams 0.2 px, white Δu′v′ 0.002". `device:<id>` gains the double-correction amber (M4).

### 4.4 Seams (C#)

```csharp
// Engine/Align/IAlignBackend.cs
public interface IAlignBackend
{
    AlignProgress? Current { get; }
    Task<string> StartAsync(AlignRequest request, CancellationToken ct);   // answers the run id once accepted
    bool Cancel();
}
[Flags] public enum AlignSteps { None = 0, Power = 1, Neutral = 2, Identify = 4, Scan = 8, Solve = 16, Color = 32, Verify = 64, All = 126 }
public sealed record AlignRequest(string GroupId, AlignSteps Steps, ImmutableArray<string> Only, AlignOverrides Overrides, int AutoAcceptScore);  // -1 = ask
public sealed record AlignOverrides(ScreenShape? Shape, CanvasFit? Fit, double? Aspect, ColorMatchMode? Color, ColorApplyAt? ApplyAt, string? Mode, bool PowerOn);
public sealed record AlignProgress(string RunId, string GroupId, string State, string Step, double Progress, double EtaS, string Words, ImmutableArray<AlignOutputProgress> Outputs);
public sealed record AlignOutputProgress(string OutputId, string Name, string Step, double Progress, string Light, string Words);

// Seams.cs (hunk): colour frames on the scan display
public interface IScanDisplay
{
    Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct);
    /// <summary>Shows an 8-bit RGB frame (R, G, B interleaved) at the output's raster; completes at its present (ns).</summary>
    Task<long> ShowRgbAsync(string outputId, ReadOnlyMemory<byte> rgb24, int width, int height, CancellationToken ct);
    void Release();
}

// Engine/Scanning/ScanCamera.cs (hunk)
public sealed record CameraFrame(byte[] Gray, int Width, int Height, long ArrivalNs, long Generation)
{ public byte[]? Rgb { get; init; } }                 // interleaved RGB when the camera delivers colour
public interface IScanCamera { /* … */ bool HasColour => false; }

// Engine/Scanning/PatternCapture.cs
public sealed record CaptureGroup(ImmutableArray<string> OutputIds);
public sealed record CaptureRequest(string CameraId, ImmutableArray<CaptureGroup> Groups, string Mode, bool Structured,
    ImmutableArray<PhotometricFrame> Photometric, IReadOnlyDictionary<string, bool[]>? Footprints);
public readonly record struct PhotometricFrame(string Name, byte R, byte G, byte B);   // "K","R","G","B","W","g64"…
public sealed record CaptureResult(IReadOnlyDictionary<string, CorrespondenceMap> Maps,
    IReadOnlyDictionary<(string OutputId, string Frame), float[]> LinearRgb, CaptureTiming Timing, IReadOnlyList<string> Notes);

// Devices/Commands/Native.cs
[Flags] public enum NativeKind { None = 0, Geometry = 1, EdgeBlend = 2, ColorMatch = 4, Uniformity = 8, DynamicContrast = 16, DigitalZoom = 32, Masking = 64, Daylight = 128, Latency = 256 }
public sealed record NativeSetting(string Name, NativeKind Kind, string Query, string Neutral, bool Verified, string Caveat);
public interface INativeProcessing
{
    IReadOnlyList<NativeSetting> NativeSettings { get; }
    Task<(CommandResult Result, NativeSnapshot? Snapshot)> ReadNativeAsync(CancellationToken ct);
    Task<CommandResult> NeutraliseAsync(CancellationToken ct);                 // confirms each by query
    Task<CommandResult> RestoreAsync(NativeSnapshot snapshot, CancellationToken ct);
}
public readonly record struct Xyy(double Y, double X, double YChroma);
public sealed record MeasuredPrimaries(Xyy Black, Xyy Red, Xyy Green, Xyy Blue, Xyy White);
public sealed record TargetPrimaries(Xyy Red, Xyy Green, Xyy Blue, Xyy Cyan, Xyy Magenta, Xyy Yellow, Xyy White);
public interface INativeColorMatch
{
    Task<CommandResult> WriteMeasuredMatchAsync(MeasuredPrimaries measured, TargetPrimaries target, CancellationToken ct);
    Task<CommandResult> ColorMatchOffAsync(CancellationToken ct);
}
public enum NativeAlignmentKind { Stack, Blend, ColorCalibration, ScreenMatching }
public interface INativeAlignment           // no driver implements a start today: every one answers Refused with the reason
{
    Task<CommandResult> StartAsync(NativeAlignmentKind kind, CancellationToken ct);
}

// Engine seam the render host implements (live preview of a staged correction is later; verify uses CPU pre-warp frames)
// Core/Align/AlignMath contracts are pure functions: no seam.
```

`DisplayInfo` gains `Edid` (byte[]?, init) so `EdidInfo.Matches` can pair outputs and links (Linux reads `/sys/class/drm`).

### 4.5 NodeSettings

| Property (init) | Default | Why per node |
|---|---|---|
| `AlignWarmupMinutes` | 20 | a node's policy for colour passes (Epson's guidance, P§2) |
| `AlignRunsKept` | 20 | disk on this node |
| `AlignParallelCapture` | true | some venues' concave screens inter-reflect (§9); a node can turn grouping off |

---

## 5. Files

New (Core):

- `src/PlayMap.Core/Model/AlignModel.cs` — the records above.
- `src/PlayMap.Core/Align/Footprints.cs`, `Layout.cs`, `ShutterKnock.cs`, `PlanarFit.cs`, `PlaneRectify.cs`, `ScreenFit.cs`,
  `ScreenParam.cs`, `CanvasTarget.cs`, `MeasuredWarpFit.cs`, `CanvasInverse.cs`, `MeasuredBlend2D.cs`, `Photometry.cs`,
  `ColorTarget.cs`, `VerifyAnalysis.cs`, `AlignScore.cs`, `Pfm.cs` (PFM read/write).
- `src/PlayMap.Core/Scan/MultiProjectorSim.cs`; scenes added in `SimScenes.cs` (hunk) — or `SimScenes.Screens.cs` (new partial).

New (Devices):

- `src/PlayMap.Devices/Commands/Native.cs` (interfaces, records); `src/PlayMap.Devices/Drivers/Panasonic/PanasonicNative.cs`
  (the RZ570 words, partial class of the driver); `src/PlayMap.Devices/Workflows/Knock.cs`.
- (should) `src/PlayMap.Devices/Transport/EpsonWebApi.cs`, `src/PlayMap.Devices/Manager/EpsonDiscovery.cs`.

New (Engine):

- `src/PlayMap.Engine/Align/AlignRunner.cs`, `AlignRunner.Steps.cs`, `AlignArchive.cs`, `IAlignBackend.cs`, `AlignViews.cs`
  (camera view overlay of footprints and dots on the existing VIEW `camera:<id>`).
- `src/PlayMap.Engine/EngineCore.Align.cs` (verbs), `src/PlayMap.Engine/EngineCore.BlendJob.cs` (BLEND SOLVE off the lane).
- `src/PlayMap.Engine/Scanning/PatternCapture.cs` (extracted).

New (tests):

- `tests/PlayMap.Core.Tests/Align/*Tests.cs` (one per Core component; sim-truth metrics), `tests/PlayMap.Core.Tests/Scan/MultiProjectorSimTests.cs`.
- `tests/PlayMap.Devices.Tests/NativeProcessingTests.cs`, `KnockTests.cs`.
- `tests/PlayMap.Engine.Tests/Align/AlignRunTests.cs` (end to end on the sim, counting display), `AlignVerbTests.cs`,
  `AlignArchiveTests.cs`, `BlendJobTests.cs`.
- `tests/PlayMap.Render.Tests/MeasuredOutputTests.cs` (llvmpipe readback).

Touched (small hunks):

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/StageModel.cs` | `Stage.Aligns`, `CameraRig.Linear` (init) |
| `src/PlayMap.Core/Model/OutputModel.cs` | `Output.Measured` (init; shared with facade) |
| `src/PlayMap.Core/Model/DeviceModel.cs` | `DeviceLink.Snapshot` (init) |
| `src/PlayMap.Core/Model/Ids.cs`, `Project.cs` | `Align`, `AllIds` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | category `Align`, 13 + 5 rows |
| `src/PlayMap.Core/Mapping/WarpMeshBuilder.cs` | `Tessellate(field, MeasuredWarp?, Func<…> sampler)` overload |
| `src/PlayMap.Core/Bus/Pairing.cs` | three NodeSettings init properties |
| `src/PlayMap.Devices/Drivers/Panasonic/PanasonicDriver.cs` | `partial`, implements the two interfaces |
| `src/PlayMap.Engine/Seams.cs` | `IScanDisplay.ShowRgbAsync`, `DisplayInfo.Edid` |
| `src/PlayMap.Engine/Scanning/ScanRunner.cs`, `ScanCamera.cs`, `ScanOutcome.cs` | capture through `PatternCapture`; colour frames; `map-<pj>.pfm` saved/loaded |
| `src/PlayMap.Engine/Rendering/ScanFrames.cs`, `RenderHost.cs` | RGB frames; `DisplayInfo.Edid` on Linux |
| `src/PlayMap.Engine/Rendering/StageResources.cs` | measured blend files (Replace/GainOnly) |
| `src/PlayMap.Engine/Devices/DeviceService.cs`, `EngineCore.Devices.cs` | native, snapshot, neutral, restore, knock; four verbs |
| `src/PlayMap.Engine/EngineCore.Router.cs` | the ALIGN and DEVICE rows; BLEND SOLVE → job |
| `src/PlayMap.Engine/StateWriter.cs`, `ContextBuilder.cs` | `align`, `blend`, `devices[].native`, `outputs[].measured`; items |
| `src/PlayMap.Engine/OpsParts.cs`, `EngineServices.Ops.cs` | the "align" extension, `Services.Align` |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs`, `Passes/OutputStage.cs`, `Engine/RenderEngine.cs` | `uMatch`, `uMatrix`, `uGammaOut`; inputs from `Output.Measured` |
| `src/PlayMap.App/…` (desk) | Align panel, right-click entries (§8) |
| `tests/PlayMap.Devices.Tests/Simulators/PanasonicSimulator.cs` | the RZ570 native words |
| `docs/WIRE.md`, `docs/PROJECTORS.md`, `docs/UX.md` §5.0, `docs/ROADMAP.md`, `docs/COMPETITIVE.md` | verbs, native table, time target, items, verified autocal facts |

---

## 6. Dependencies

Work packages:

- **facade** — shares `StackGroup`, `MeasuredCorrection` (one record with both packages' fields, §4.1), `StackAlignment` for 3D
  stacks, per-projector scan data and the scan-folder layout (facade writes gray16 night frames, this writes decoded maps; distinct
  names). Whichever lands first adds the shared records whole.
- **present** (D3D11/framelock) — the HLSL twin of `uMatch`; 10-bit outputs remove the banding risk of matrices (§9); per-output
  latency improves capture timing.
- **show safety** — ALIGN RUN/IDENTIFY/AUDIT refused while armed; blind edits of ACCEPT; `RiskOf(line)` (optional).
- **companion** — `VerbParams` rows for the new verbs, presets and feedbacks (§8).
- **phonescan** — the Phone camera with exposure/white-balance lock (`applyConstraints`), the Folder camera.
- **fleet** — copies `align/` and `scans/` files to backup nodes with the project, or a backup node shows outputs without their
  measured correction (STATE says so).
- **calibration score/history** (ROADMAP R1.4/R4.8) — reuses `AlignScore` items and the run/fallback shape.

Third-party: **none**. All maths is managed C# on Core's numerics (`SolverMath`, LM, SVD, `Pcg32`); PFM is written by hand.
The vendor command words come from Panasonic's published table (read, not copied code). No GPL/LGPL code.

---

## 7. Verification here

Tested for real in this container:

- Every Core component against synthetic data with known truth: planar fit (random homographies + k1/k2), rectification (from
  corners exact; from priors within the stated covariance over 200 Pcg32 trials), cylinder/sphere fits, parametrisations, target
  search, warp fit (residual, held-out, invalid nodes), inverse rasterisation, blend 2D (sum to one in light), pedestal, uniformity,
  photometry, colour target (constraints hold, Δu′v′ after match), verify analysis (dots), score items.
- End to end on `MultiProjectorSim` through the real engine (headless) with a counting fake `IScanDisplay`, and once through the
  real render host on Xvfb llvmpipe: identify, grouped capture, solve, colour, verify, stage, ACCEPT/UNDO/REVERT, ONLY, cancel
  restores the fake projectors' snapshots, STATE and context keys by field, PING ≤ 50 ms during a run, budgets in §3.10.
- Devices: `PanasonicSimulator` with the native words (snapshot, neutral confirmed, restore, ERR1 per word), the knock over the
  Christie/Panasonic/PJLink/Epson simulators' shutters; (should) a fake HTTP server with Digest auth for Epson's Web API.
- Render: llvmpipe readback of the measured warp, the colour matrix and γ, measured blend files, missing-file fallback.
- Persistence: PROJECT SAVE/OPEN of groups, snapshots and `Measured`; run folders read back after a restart.

Simulated, and how:

- Projectors and camera: `MultiProjectorSim` (per-projector primaries, black, γ, white segment, light drift; camera vignetting,
  sRGB encode, noise, auto-exposure; ambient). Flat, curved and stacked layouts; truth for every metric.
- Shutters: the device simulators' AV-mute state drives the sim's per-projector light.

Compile-only (needs hardware, Windows or a GPU):

- Real cameras' exposure/white-balance lock (V4L2 ioctls, DirectShow) — S8; real cameras' radiometric response.
- Real projectors: whether each model accepts each Panasonic word (model-dependent), the `Y` scale of measured colour matching
  (S1 stays gated), Christie warp select (gated), Epson's Web API on a real projector.
- EDID on Windows (registry); the D3D11 shader twin.
- The time target (≤ 90 s for four projectors) is a sim measurement only until the R0.5 hardware lab runs it.

---

## 8. Desk and Companion surface

**Desk** (Outputs drawer and the Map workspace; the desk uses only these verbs):

- **Align screens** — one button. With no group, it runs `ALIGN NEW` over the selected outputs (or all open ones) and the first
  camera, then `ALIGN PLAN`, and shows the plan as chips: camera (resolution, colour), each projector (brand, "resets 9 settings" or
  "switch keystone off on its menu"), groups, ETA. **Run** sends `ALIGN RUN`. Devices found by `DEVICE DISCOVER` but not linked are
  offered as one "Link these projectors" press (the DEVICE ADD lines).
- **During the run**: a step strip (Neutral · Identify · Scan · Solve · Colour · Verify) with an ETA dial; the camera view shows
  footprints labelled with output and projector names as they are identified, then the dot grid's residuals as coloured arrows.
- **Result card**: before/after split slider on the camera picture, the score ring with its items (each with its fix as a button),
  **Accept**, **Keep current**, **Revert to previous**, and per projector "Restore its own settings".
- **Right-click** an output: Re-align this projector (`ALIGN RUN al… ONLY op…`), Show measured warp (VIEW `output:<id>` with the
  grid drawn), Clear measured correction. A device: Projector's own settings → Read / Snapshot / Neutral / Restore. The canvas:
  Set screen corners (click four corners on the camera view → `ALIGN CORNERS`).
- **Warnings** inline: "P3's own keystone is on — Neutral", "colour measured 6 min after power-on — re-run colour later".

**Companion** (the module generates actions from `/api/verbs` and variables from STATE):

- Actions: Align (High: two-press confirm, as the module shapes high-risk presses), Accept, Revert, Re-align `<output>` (template
  preset per output id), Neutral/Restore `<device>`, Audit.
- Variables: `align_state`, `align_step`, `align_eta`, `align_score`, `align_grade`, `align_<group>_score`.
- Feedbacks: progress gauge on the Align key (ring on encoders), score ring coloured by grade, red on a device key when its own
  geometry is on under PlayMap's correction, amber on Accept while a run is staged.
- Live auto-built buttons: when a run stages, an "Accept 91" key and a "Revert" key appear on the align page; a "Re-align P2" key
  appears when drift watch (later) or an audit flags P2.

---

## 9. Risks and open questions

| Risk | Mitigation |
|---|---|
| **Camera radiometry**: webcams auto-adjust exposure and white balance and encode with an unknown curve | Stability probe refuses colour with words (M6); sRGB decode by default, `CameraRig.Linear` for raw cameras; exposure lock seam (S8); additivity reported; Debevec-style response from overlaps later |
| **Metamerism**: a camera's RGB is not the eye's — laser (narrow-band) and lamp projectors can match for the camera and differ for people | Say so in the colour item for mixed light sources (the model names come from device status); colorimeter path later (M22); WHITE mode as the safer default when a laser and a lamp projector are mixed (open: make this automatic?) |
| **Viewing angle and screen gain**: one camera position sees a gain screen's hot spot differently from the audience | Default uniformity 0.6; the plan advises the camera near the audience centre (Scalable's advice, P§7); two-camera averaging later |
| **Camera vignetting** biases uniformity toward darkening the centre | Partial uniformity default; vignetting of a calibrated camera measurable later (a flat-field from a white wall sweep) |
| **Planar self-calibration is degenerate**: the plane's metric shape from projectors alone needs priors | Corners/border first; priors give a stated uncertainty; fronto-parallel fallback says so |
| **Concave screens inter-reflect**: light from one projector lands in another's footprint and corrupts a shared capture group | `AlignParallelCapture` off; shape fits flag concave cylinders/domes and the plan switches to sequential for them (open: measure the inter-reflection directly with one white frame?) |
| **8-bit banding** after a matrix and γ change | The existing dither; 10-bit outputs (present); in-projector match on Panasonic (S1) |
| **Projector drift during warm-up** | Drift measured; colour deferred (S6) |
| **DLP white segment** breaks the additive model | Detected and named; matching approximate |
| **Vendor units** (Panasonic measured-mode `Y`) | Gated until one session on a real projector |
| **Two warps fight** (internal keystone + ours) | Snapshot/neutral before; double-correction guard after (M4) |
| **Files and backups**: measured corrections live in `align/` files | Fleet package copies them; STATE fault and fallback on a missing file |
| **Patents**: Christie holds structured-light and stacking-target patents (COMPETITIVE §7, F§8) | The method here is the published textbook one (homographies, phase shift, per-pixel correspondence, least-squares warps); a freedom-to-operate review before release is an open item |
| **Time claim** | Measured only in the sim until the R0.5 lab; the UX §5.0 row is a target |

Open questions: Should ACCEPT AUTO be allowed at all when a show file is open but not armed? Should groups own their canvas
(created and sized by the run) or always reference an operator's canvas? Is 33 × 19 enough for short-throw lenses with strong
distortion (65 × 65 is allowed; auto-pick by the residual)?

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Core maths (footprints, layout, knock, planar fit, rectification, screen fits and params, target, warp fit, inverse, blend 2D, pedestal, uniformity, photometry, colour target, verify, score, PFM) | 32 |
| `MultiProjectorSim`, screen scenes and layouts, truth metrics | 9 |
| Engine: `PatternCapture` extraction, colour frames, map persistence, `AlignRunner` and steps, archive, verbs, STATE/context, BLEND SOLVE job | 24 |
| Devices: native interfaces, Panasonic words, knock, simulator words | 8 |
| Render: measured tessellation, shader branch, measured blend files | 7 |
| Tests (beyond those written with each part), budgets | 10 |
| Desk panel, right-click entries, papers | 10 |
| **Must total** | **100** |
| Should (Panasonic push, dome, border, Epson transports, audit, warm-up gate, two cameras, camera control, DEVICE COLOR) | 30 |
