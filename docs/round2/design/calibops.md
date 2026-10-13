# Work package "calibops": an explainable calibration score, saved fallbacks, history, and re-solving only the projector that moved

*Design for round 2, 13 October 2026. Research: `r2/research/lessons.md` (cited as L§n). Benches: `r2/bench/lessons/`
(`blobcheck.py`, run here; outputs beside it). Code read: `main` at c12b186. Implements ROADMAP R1.2 (snapshots,
fallback, per-projector re-solve), R1.4 (explainable score and confidence map) and most of R4.8 (calibration as a
service: check, resolve, accept, revert, history), with M2's content-based drift watch left for later. Meets docs/UX.md
§4.4, §4.8.5–6, §5.3 and the "Re-align after a bump ≤ 60 s, ≤ 10 s of patterns, no mask rework" target.*

Three things are built. First, every calibration result becomes an **immutable named snapshot** with a **score that explains
itself**: each penalty comes with words, a place and a fix you can press. Second, each projector keeps a **history** and a
**fallback**. Third, a **one-second dot check** tells which projector, if any, moved and whether the camera moved. Then
only the projector that moved is re-solved. Masks and lattices stay as they are, 2D quads are re-seated, and the result
is verified on the wall.

---

## 1. Goal and the bar

| What the best do (L§) | PlayMap's answer | Match or beat |
|---|---|---|
| **OmniCal Score**: rounded-up aggregate projector RMS plus penalties (high RMS, z-clipping, mirrored/backwards, non-square pixels, unrealistic focal/shift/distortion); 0 best, ≤ 1 ideal; a report lists reasons at the end; colours green→red (L§3.1–3.3) | **Score** = inlier RMS rounded up to 0.1 px + penalties in pixel-equivalents, on OmniCal's scale (grade A ≤ 1.0, B ≤ 1.5, C ≤ 4, F above or on a hard failure). OmniCal's penalties are included, plus **outlier pins by name, point spread, depth spread (zoom↔distance), covariance-predicted wall error, lens moved since the solve (device readback), mesh changed since the solve, decode quality, focus, seam misregistration with each neighbour**. **Every term has words, a place and a fix line** | **Beat**: reasons on the object card, on Companion and **on the wall** (numbered areas), each with a button, not in a report window |
| OmniCal colours points (ProjectorReprojError green/yellow/red) in the 3D visualiser (L§3.4) | A **confidence map** per projector (predicted error per 32 px cell: measured where there are pairs, propagated from the solve's covariance elsewhere, hatched where unmeasured), **projected onto the object** through the edit overlay with numbered problem areas that match the score's terms | **Beat**: on the real object, not in a visualiser |
| RigCheck **Results** are immutable folders; a **Fallback Result** the operator sets after a visual check; Revert to Fallback; retention keeps current, fallback and a keep-list; an API (`executeplan`, `checkresult`, `acceptresult`, `revertresult`, `deleteoldresults`) (L§3.6–3.11) | `CalibrationSnapshot` entities (`cs…`) in the project, artefacts in `calibration/<cs>/`. `CALIBRATION ACCEPT` (= acceptresult), `FALLBACK` (= revertresult), `RECALL` any snapshot, `SAVE` a pinned one; retention keeps current, fallback, staged, pinned and the last N per projector. Every verb is on the wire, HTTP, MCP, Companion and the Show Bus | **Match**, per projector instead of per rig, and undoable |
| disguise: "compare … only the omniCalScore, since RMS … may fluctuate" (L§3.11) | The **gate** that applies a re-solve automatically compares scores: ≤ max(1.5, 1.25 × the fallback's score), and the wall verification must agree. A worse result is kept as a snapshot but **staged**, never applied silently | **Match**, and the gate is a project setting |
| OmniCal: one click **only if only projectors moved**; a moved camera or surface needs an operator to adjust alignment; "2D warps or manual blend masks … will likely need adjusting" (L§3.9–3.10) | The check **attributes** the move: nothing / projector k / camera / camera and some projectors / the surface. **A moved camera is re-solved from the projectors that agree**, then the moved projectors (bench: 0.29 px p95 after a camera **and** projector bump, L§9.2). Masks live in UV and do not move. **2D quads (and, on 2D rigs, output warps) are re-seated** by the measured displacement field | **Beat**: two cases disguise hands to an expert are handled, and 2D work is carried over |
| VYV: per-projector recalibration leaving good ones untouched; error < 0.5; **last 100 calibrations saved**; Calibration Lock (L§4.1, 4.4, 4.5) | Per-projector everything; history of 100 reachable (`CalibrationKeep` up to 100; default 20); `Projector.Locked` refuses RESOLVE/RECALL/FALLBACK and allows CHECK | **Match** |
| Scalable: a **30-second** per-projector recalibration "without affecting any other channel"; manual, scheduled or API; ten automatic backups (L§5.1–5.3) | QUICK path: **≈ 0.5 s of patterns per projector** to check (N+1 frames) and ≈ 1.5 s to re-solve and verify. Dense coded escalation ≈ 1.5–2 s more; a full robust bridged scan of one projector as the last resort. Triggered by a button, Companion, Patterns, a device's lens readback, the camera drift monitor or (later) the scheduler | **Beat** on time per projector (simulation; to be measured on hardware) |
| Everyone: a calibration is "good" or "bad" | **Verified**: after applying, a dot check measures the wall error (sent → applied → **observed**) and records `verifiedPx` in the snapshot | **Beat** (doctrine: attempts are not facts) |

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **`CalibrationScoring`**: score, grade, RMS, p95, confidence, terms with code/px/words/where/fix/hint | Unit tests with injected faults, each asserting its term's code and px: a mirrored pose → `geometry` and grade F; fx/fy = 1.02 → `aspect` ≈ 1.5 px; one pin 14 px off → `outliers` naming that pin and the fix `SET pn… enabled false`; 8 coplanar pins with focal free → `depth` with fix `PROJECTOR SOLVE pj… LOCK FOCAL`; pins in one quarter → `spread` with where `quadrant:tl,br`; lens readback zoom 1234 → 1240 → `lens-moved`; mesh hash changed → `mesh-changed`. **Every non-empty fix parses with `VerbCatalog.Match`** (test over all terms) |
| M2 | **`ConfidenceMap`**: 32 px cells, measured / predicted / unmeasured, numbered problem areas | A simulated solve with pins only in the left half: right-half cells are "predicted" with errors rising toward the edge; problem area 1 is in the right half; the map's p95 agrees with a Monte Carlo of the solve (Pcg32, 200 trials) within ±30 % |
| M3 | **Snapshots**: `CalibrationSnapshot`, `Stage.Calibrations`, `Projector.CalibrationId/FallbackId/StagedId`; every `PROJECTOR SOLVE`, `SCAN APPLY`, `CALIBRATION RESOLVE/RECALL/FALLBACK` records one **in the same edit** | `PROJECTOR SOLVE` then `GET pj…` shows `calibrationId`; `GET cs…` shows optics, pose, pins, score, source `pins`. UNDO removes both the solve and its snapshot. `SCAN APPLY` makes one snapshot per applied projector with `source:"scan"` and `scanId` |
| M4 | **Fallback, accept, recall, save, forget, retention** | The first snapshot of a projector becomes its fallback. `CALIBRATION ACCEPT pj…` moves the fallback to the current one. `CALIBRATION FALLBACK pj…` restores the fallback's optics, pose and pins exactly (bit-equal doubles) and records a `revert` snapshot whose parent is the replaced one. With `CalibrationKeep` 3, a fourth solve drops the oldest unpinned, non-current, non-fallback snapshot in the same edit, and the reply says so |
| M5 | **Dot check** (`CALIBRATION CHECK`, QUICK): N+1 frames (all black, then each projector's jittered dots alone) through the scan runner's display and capture pacing; detection, consensus matching, focus ratio; the CODED escalation (dot IDs in ⌈log₂(N+1)⌉ bit frames) when consensus fails | Simulator (floor-and-box, wall-and-cylinder): with the true projector bumped 0.8° / 2 cm, ≥ 90 % of visible dots are matched and the median shift agrees with the truth within 0.3 px. With a 1.5° / 5 cm bump (127 px, beyond the QUICK capture range) the check escalates to CODED by itself and still matches ≥ 90 %. Blur σ 1.5 camera px → `focus` ratio > 1.4 and a "projector 2 looks out of focus" term |
| M6 | **Attribution** (`MotionAttribution`): unmoved / moved / lens changed / unseen per projector; camera moved; surface suspect | Simulator cases from the bench: only P2 bumped → P2 `moved`, others `unmoved`, camera not moved. Camera bumped → camera `moved`, every projector `unmoved` after the camera re-solve. Camera + P1 → camera re-solved from P2, P1 `moved`. Zoom changed 2 % → P1 `lens`. A box moved 3 cm on the table → `surfaceSuspect:true`, no automatic proposal, words "the object moved — rescan it (SCAN START …)" |
| M7 | **Targeted re-solve** (`CALIBRATION RESOLVE`): the ladder QUICK (pose only) → LENS (focal and shift free) → CODED → DENSE (bridged robust scan of that projector only); verification check; gate; apply or stage | Simulator: after a 0.8° / 2 cm bump, `CALIBRATION RESOLVE MOVED` re-solves only P2. Wall error vs truth ≤ **0.3 px p95**; P1's snapshot id unchanged; `verifiedPx.p95` ≤ 0.5. Patterns time (frames × frame time) ≤ **2 s**. A result whose score fails the gate is **staged**: `pj.stagedId` set, pose unchanged, and the reply names `CALIBRATION ACCEPT cs…` |
| M8 | **What a re-solve leaves alone, and what it re-seats**: surface masks and lattices untouched; pins follow the pose (pixel := projection of their world point); pixel quads (firstfive) re-seated by the displacement field; output warps re-seated on 2D rigs only | After a re-solve the project diff contains only `pj…` optics/pose/pins and calibration fields, the snapshot, and the quads' corners. A pixel quad re-seated after a 16 px bump puts its corners within **0.5 px** of the wall spots they lit before (simulator). Masks unchanged: an equality test |
| M9 | **History**: `CALIBRATION HISTORY` with Δmm, Δdeg and Δwall px between consecutive snapshots, score and source | Three solves give three rows newest first; Δwall px between a snapshot and its bumped successor equals the simulated bump's p95 within 0.5 px |
| M10 | **Artefacts on disk** under `calibration/<cs>/` (confidence, residuals, references); written at once when the project has a folder, else at the first `PROJECT SAVE`; a janitor at save removes folders no live or undo root references | After a node restart, `CALIBRATION CHECK` compares against the reference written before the restart (carry-over: work from before a restart is not refused). Removing a snapshot and saving deletes its folder unless an undo root still holds it |
| M11 | **Off-lane tasks** with progress: CHECK and RESOLVE run on a worker; results land as one edit through the lane; one task at a time; mutually exclusive with SCAN; `CALIBRATION CANCEL` | The control lane answers `PING` within 50 ms during a RESOLVE. `STATE.calibration.task` moves through `capturing → matching → solving → verifying → done`. A camera that fails to open fails the task with its words in STATE (carry-over) |
| M12 | **Blends follow**: a re-solved projector in an auto-blend triggers the blend solve **off the lane**, applied through `BlendSolved` (carry-over: BLEND SOLVE blocks the lane) | The lane stays responsive (as M11) while the blend of a 3-projector stage re-solves; the maps installed match those `BLEND SOLVE` would make |
| M13 | **Show safety**: CHECK and RESOLVE refused while armed; RECALL/FALLBACK/ACCEPT refused while armed unless in blind edit (per the show-safety gate); an assistant's RESOLVE is always staged | With a fake gate saying armed: all four refuse with the gate's words; STATUS and HISTORY still answer |
| M14 | **STATE, context, events, VERBS, WIRE.md, UX.md §5.3, ROADMAP** | `STATE.calibration` shape asserted field by field; a `calibration:<pj>` context item per projector with green/amber/red; the companion package's `calibration.moved` event fires when `moved[]` changes |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Device triggers**: a lens readback differing from the snapshot's (`LensPositions`) marks the projector stale and moved with verdict `lens (readback)`; a power cycle or light-source change recommends a check | A fake device reporting zoom +6 steps → `stale:["zoom 1234→1240"]`, amber, and the proposal `CALIBRATION RESOLVE pj… LENS` |
| S2 | **Camera drift monitor** (`DriftMonitor`): `DriftWatch` at 1 Hz on a fixed camera's live frames, restricted to the regions no projector lights (from the check's frames); allowed while armed (observation only) | Simulated camera frames shifted 3 px → `camera.state:"drifting"` after 2 confirmations; dark scene → `"unjudged"` with DriftWatch's own words |
| S3 | **Before/after on the wall**: `CALIBRATION PREVIEW <cs> [SPLIT <0–1>]\|OFF` renders the projector's output with the staged snapshot on one side of a split | llvmpipe read-back: left of the split matches the current pose's render, right the staged one's (each ± 1 code) |
| S4 | **Seam term**: misregistration p95 between overlapping projectors, from the dots both check runs see in their overlap (3D points from each) | Two simulated projectors, one with a 1 px pose error in the overlap: `seam` ≈ 1 px on both, naming the other projector |
| S5 | `CALIBRATION REPORT`: a plain-text and JSON report per projector (score, terms, history tail, last check) for the show report | Golden-file test on the simulator |

### Later

Content-based passive drift watch during the show (M2: predicted camera view vs live, LightGlue-class matcher), imperceptible
continuous alignment (L3), multi-camera checks with seam-aware bundle adjustment (M3), retargeting masks when the mesh
changes (R4.7), the scheduler's "check before doors" (R4.5 package), colour/brightness drift (M22).

---

## 3. Architecture

### 3.1 Components and threads

```
                  ┌────────────── control lane (single writer) ─────────────┐
 verbs ─────────▶ │ EngineCore.Calibration.cs: STATUS/HISTORY (read),        │
                  │ SAVE/RECALL/ACCEPT/FALLBACK/FORGET (edits),              │
                  │ PROJECTOR SOLVE & SCAN APPLY hunks → CalibrationLedger   │──▶ ProjectStore.Apply (one edit: optics/pose/pins
                  │ CHECK/RESOLVE → CalibrationTasks.Start(…)                 │                         + snapshot + pruning)
                  └───────────────────────────────▲─────────────────────────┘
                                                  │ result edit (lane)
 ┌──────────── CalibrationTasks worker (one at a time; exclusive with SCAN) ┴──────────────────────────────────┐
 │ CheckRunner ── IDotCheckBackend (ScanRunner: display + camera + latency + settle) ── frames                  │
 │   DotPattern.Make → DotDetector.Find → DotMatcher.Match (+ coded IDs) → DotObservations per projector         │
 │ MotionAttribution.Decide(model, observations, references) → verdicts, camera re-solve                        │
 │ Resolver ladder: PoseSolver (pose-only → lens) → CODED → DENSE (ScanRunner bridged, one projector)           │
 │ CalibrationScoring + ConfidenceMap + Reseat2D → result → lane; then verification check → reference files    │
 │ Blend re-solve off-lane (BlendInputs.Solve) → BlendSolved via the lane                                       │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
 DriftMonitor (1 Hz, low priority, per fixed camera) ─ DriftWatch on unlit regions ─▶ CalibrationRuntime (STATE)
 DeviceService facts ─ lens readback vs snapshot ─────────────────────────────────▶ CalibrationRuntime (stale)
 CalibrationFiles: calibration/<cs>/… written by the worker (or buffered until the project has a folder); janitor at save
```

`CalibrationRuntime` (engine memory, not the model): per projector the last check (time, verdict, shift, words), stale
reasons, task progress, camera drift. It feeds STATE, the context and the overlay's heat map (`IOverlayHeat`).

### 3.2 The score (`PlayMap.Core/Calibration/CalibrationScoring.cs`)

```
Score = ceil₀.₁(RMS_inliers) + Σ penalty_k        (px-equivalents, ≥ 0; hard failure → 99.9 and grade F)
Grade = A ≤ 1.0 < B ≤ 1.5 < C ≤ 4.0 < F           (OmniCal's "≤ 1 ideal, ~1.5 acceptable"; the companion package's rule)
Confidence = share of the projector's lit raster cells whose predicted error ≤ 1 px (ConfidenceMap)
```

Inputs (`ScoreInputs`): the projector before and after, the `PoseSolution` (residuals, inliers), the pairs (3D ↔ px), the
raster, the locks, the lens envelope (datasheet or device `LensPrior`, optional), `SolveUncertainty` (the facade package's
`SolveUncertaintyAnalysis.Of`, which **this package (wp2-wall, W1) creates** with a minimal body: Σ = σ̂²(JᵀJ)⁻¹, wall px p95
over ≤ 2 000 visible samples; wp2-facade (W2) extends it with the priors' rows through a hunk named in `PLAN.md` §4), the decode's `ScanQualityReport` (dense only),
the dot focus ratio (check only), seam measures, the stage's mesh hashes now, the device lens now, and the camera's RMS.

| Code | Measure | Penalty (px) | Words (example) | Where | Fix (a verb line) / hint |
|---|---|---|---|---|---|
| `rms` | inlier RMS | ceil₀.₁(rms) (the base, not a penalty) | "fits its 12 pins to 0.42 px" | — | — |
| `outliers` | outlier share s; pins named | pins: 0.5 per outlier pin; dense: 4·max(0, s − 0.10) | "pin 4 disagrees by 14 px" | `pins:pn…` | `SET pn… enabled false` / "or drag pin 4 onto its feature" |
| `spread` | convex-hull area of inlier pixels ÷ raster area a | 2·max(0, 0.35 − a)/0.35 | "the pins cover 21 % of the picture; the top-left extrapolates" | `quadrant:tl` | `OVERLAY HEAT pj…` / "add pins in the empty quarters" |
| `depth` | relative depth spread of inliers along the axis, with focal free | 1.0 when the weakest eigenvector is zoom↔distance (facade's rule) | "points nearly flat: zoom cannot be told from distance" | — | `PROJECTOR SOLVE pj… LOCK FOCAL` / "or add a pin off this plane" |
| `uncertainty` | covariance-predicted wall px p95 | max(0, wall − 0.5) | "could be 1.8 px off at the edges" | `edges` | `OVERLAY HEAT pj…` |
| `aspect` | \|fx/fy − 1\| | 100·max(0, \|fx/fy − 1\| − 0.005) | "pixels not square (2 %): a scaling mode in the projector or a wrong raster" | — | `DEVICE GET dv…` / "turn the projector's aspect or scaling off" |
| `shift` | principal point vs the lens's shift range (envelope), else \|c − centre\| > 0.6·size | 1.0 + 2·excess fraction | "lens shift 70 % up — beyond this lens's 50 %" | — | `PROJECTOR SOLVE pj… LOCK SHIFT` |
| `focal` | throw ratio vs the envelope (or readback prior ± 3 %) | 1.0 + 10·relative excess | "throw 0.9, the lens is 1.5–2.0" | — | `PROJECTOR SOLVE pj… LOCK FOCAL` |
| `distortion` | \|k1\| > 0.3, or the radial map not monotonic inside the raster | 1.0 | "lens distortion unrealistic for this lens" | — | `PROJECTOR SOLVE pj… LOCK DISTORTION` |
| `geometry` | points behind the projector, mirrored (det R < 0), stage inside the near plane | hard failure | "solved pointing backwards" | — | `CALIBRATION FALLBACK pj…` |
| `lens-moved` | device lens readback ≠ the snapshot's (> 2 steps) | 2.0 (stale) | "zoom moved 1234 → 1240 since this solve" | — | `CALIBRATION RESOLVE pj… LENS` |
| `mesh-changed` | the stage's mesh hashes ≠ the snapshot's | 1.0 (stale) | "solved against an older mesh" | — | `CALIBRATION RESOLVE pj… DENSE` |
| `decode` | dense: `ScanQualityReport.Score` < 85 | (85 − score)/20 | the report's own words | the report's worst region | the report's advice (as hint) |
| `focus` | dot FWHM ÷ expected FWHM (check) | max(0, ratio − 1.3) | "Projector 2 looks out of focus (dots 1.6× wider than expected)" | — | `DEVICE LENS dv… FOCUS BY …` / "focus the lens" |
| `seam` (S4) | p95 misregistration with each overlapping neighbour | max(0, seam − 0.5) | "P1 and P2 disagree by 1.4 px in their overlap" | `seam:pj…` | `CALIBRATION RESOLVE pj…` (the worse one) |
| `camera` | the camera's own RMS | max(0, rms − 0.7) | "the camera's calibration is weak (1.2 px)" | — | "re-calibrate the camera" |

`Words` for the whole score: the grade, the score, then the two largest terms ("B 1.3 — spread (add pins top-left), pin
4 off by 14 px"). `Worst` is the largest term's words (the companion package's `worst`).

### 3.3 The confidence map (`PlayMap.Core/Calibration/ConfidenceMap.cs`)

Cells of 32 raster px (60 × 34 at 1080p). For each cell: if ≥ 3 inlier pairs fall in it, `measured` with the median
residual. Otherwise, if a ray through the cell's centre hits the stage (`MeshRaycaster`), `predicted` with
√λmax(J_x Σ J_xᵀ) at that wall point plus the nearest measured cell's residual. Otherwise `unmeasured`. Problem areas are
4-connected components of cells > 1 px covering ≥ 2 % of the raster, numbered by descending peak. Their centroids are the
overlay's labels, and the score's `where` refers to them (`area:2`). Budget ≤ 30 ms (2 040 rays, cached raycaster). It is
stored as `confidence.f32` (header `PMCF`, version 1, columns, rows, cell px; float32 error; uint8 state) and a
`confidence.png` preview. `IOverlayHeat.For(pj)` serves the current snapshot's map to firstfive's `OVERLAY HEAT`.

### 3.4 Snapshots, fallback and the gate (`PlayMap.Engine/Calibration/CalibrationLedger.cs`)

`CalibrationLedger.Record(Project p, string projectorId, PoseSolution s, CalibrationSource source, RecordContext ctx) →
(Project next, CalibrationSnapshot snap)`: a pure function used **inside** the caller's `Store.Apply` lambda, so the solve
and its snapshot are one edit and one undo step. It:
1. builds the snapshot (optics, lens, pose, locks, pins, the score, provenance: `ScanId`, `ParentId` = the projector's
   current `CalibrationId`, `Who` = the session's client name, `LensReadback` = the device's last reading, `MeshHash` =
   the sorted, joined content hashes of the meshes of visible surfaces, `DataPath` = `calibration/<cs>/`);
2. applies it (unless staged): optics, pose, pins (with their pixels re-projected under a pose-only re-solve),
   `CalibrationId`, `SolveResidualPx` (kept for compatibility, = RMS); the first snapshot of a projector also becomes its
   `FallbackId`;
3. prunes: per projector, sort by `CreatedUnixMs` descending; keep current, fallback, staged and pinned, plus the newest
   until `ProjectSettings.CalibrationKeep` (default 20, 1–100) are kept; drop the rest. The reply names how many were
   dropped.

**Gate** (`RESOLVE` only; manual solves and recalls always apply because the operator asked): apply when
`score ≤ max(CalibrationGate, 1.25 × fallback.Score.Score)` and the verification check's p95 ≤ max(1.0, 2 ×
fallback.VerifiedP95). Otherwise **stage**: the snapshot is recorded with `pj.StagedId` set and the projector unchanged.
`CALIBRATION ACCEPT cs…` (or `RECALL`) applies it. An assistant-initiated RESOLVE always stages; approval is a person's
word (Show Bus §11).

**Recall/fallback** apply a snapshot's optics, lens, pose, locks and pins, and record a new snapshot with `source:"revert"`,
`ParentId` = the replaced one and the recalled score (no re-scoring of old data). The history stays append-only in
meaning.

### 3.5 The dot check (`PlayMap.Core/Scan/DotCheck.cs`, `PlayMap.Engine/Calibration/CheckRunner.cs`)

**Pattern** (`DotPattern.Make(raster, seed, spacing = 64, jitter = 0.25)`): a grid at `spacing` with each dot displaced
by up to ±jitter·spacing (Pcg32 seeded by the check id), so the pattern is **not periodic** and a large shift cannot
alias onto the next dot. Radius r = max(3, round(0.0035·H)) px (4 px at 1080p), full white. Dots are kept only where the
projector lights a visible, unmasked surface (ray into the stage), or anywhere on a 2D rig.

**Frames** (QUICK, N projectors): frame 0 every checked output black; frames 1…N one projector's dots, the others black.
Every unchecked output keeps the show (the `IScanDisplay` contract). CODED adds, per projector, B = ⌈log₂(n_dots + 1)⌉
frames in which dot i is lit when bit b of (i + 1) is set (10 frames for 510 dots). Capture uses the scan runner's
measured latency and settle detector (frame-ID tags, exposure as a multiple of the refresh period). The camera's
exposure is locked from the reference.

**Detection** (`DotDetector.Find(gray8 dots, gray8 black, predictedRadiusPx)`): d = dots − black (int16); threshold
max(12, 0.35 × the local maximum in a 64-px tile); 4-connected components; area between π(0.5·r_c)² and π(3·r_c)², where
r_c is the dot radius predicted in camera px from the local projector→camera scale; intensity-weighted centroid; FWHM
from second moments; peak over background. ≤ 15 ms per 1080p frame, single pass, pooled buffers.

**Matching** (`DotMatcher.Match`): predicted camera positions ĉ_i (3D rigs: projector ray → stage → camera; 2D rigs: the
reference's observed positions). A coarse global offset is found by one phase correlation between a rendered image of the
predicted dots and the observed difference image (256×256 downsample; the jitter makes the peak unique). Candidates lie
within R = 0.45 × spacing × local scale of ĉ_i + offset. Consensus is a RANSAC homography in camera space (500
iterations, 2 camera px inliers). If the consensus holds < 60 % of the visible predicted dots, the check **escalates to
CODED** for that projector, and identities come from the bits (each bit thresholded at half the dot's own peak in the
all-dots frame). Output: `DotObservation(int Id, Vec2d ProjectorPx, Vec2d CameraPx, double Fwhm, double Peak)` per projector.

**Timing** (estimate, to be measured on hardware): QUICK ≈ (N + 1) frames × (camera frame time + settle). At 30 fps with
measured latency, about 0.3–0.5 s per projector. CODED adds about 1.5 s per projector. Detection and matching ≤ 25 ms per
projector.

### 3.6 Who moved (`PlayMap.Core/Calibration/MotionAttribution.cs`)

Per projector k with ≥ 60 matched dots (else `unseen`):

```
d_k  = median ‖c_obs − c_ref‖ over its dots          (camera px; c_ref from the stored reference check, else the model's prediction)
τ_u  = max(0.6, 3 σ̂_c)                               (σ̂_c: the reference's own verification scatter; bench: unmoved ≈ 0.24 at σ 0.2)
moved_k = d_k > τ_u
```

1. No projector moved → `stable` (camera drift, if any, is below τ_u).
2. ≥ 2 seen projectors, all moved → fit the **camera** pose with every projector fixed (3D points from projector rays ∩
   stage, observed camera px). RMS ≤ 1.0 camera px → `cameraMoved`, every projector `unmoved` after the camera's
   re-solve. (Bench: camera bump → 0.29 px, against 2.1–4.5 px for per-projector fits.)
3. Otherwise, if ≥ 2 moved and ≥ 1 did not, or the camera fit failed, try RANSAC over projector subsets: the largest set
   S whose joint camera fit has RMS ≤ 1.0 px with ≥ 150 dots → camera re-solved from S; projectors outside S are `moved`.
   (Bench: camera + P1 → camera from P2, 0.29 px, 1.2 mm.)
4. Each moved projector: a pose-only fit (optics locked). RMS ≤ 1.0 projector px → `moved` (pose). Else focal and shift
   free: RMS ≤ 1.0 and at least 2× better → `lens`. Else `surfaceSuspect`: words "the object or a surface moved (or
   something blocks the view) — rescan it", and **no automatic proposal**.
5. With one projector and a moved image, camera vs projector is ambiguous: the verdict is `moved (camera or projector)`
   unless S2's drift monitor or a second camera settles it. The proposal is the projector re-solve, with the words saying
   so.

A 2D rig (no stage mesh or unsolved projector) uses steps 1, 2 (coherent motion of all projectors → camera moved: "only
re-reference needed") and the displacement field. No pose fits.

### 3.7 The re-solve ladder (`PlayMap.Engine/Calibration/Resolver.cs`)

For each projector to re-solve (named, or `MOVED` from the last check ≤ 120 s old with nothing changed since; otherwise
a fresh QUICK check first):

| Step | Data | Unknowns | Accept when | Else |
|---|---|---|---|---|
| QUICK | the check's dots: X = camera ray ∩ stage, ↔ projector px | pose (6) | fit RMS ≤ 1.0 px and ≥ 150 dots | LENS |
| LENS (or `LENS` flag, or a lens readback change) | same | pose + focal + shift | RMS ≤ 1.0, the envelope respected | CODED |
| CODED | a CODED check of that projector (dots by ID) | as above | as above | DENSE |
| DENSE (or the `DENSE` flag) | `SCAN START <camera> <projector> MODE ROBUST`, bridged into the stage mesh (`ScanSolver` with `Model`) | pose + intrinsics per locks | score gate | staged, with words |

Then: score (§3.2), confidence map (§3.3), re-seat (§3.8), **verification**: one QUICK check of the projector with the new
pose. Wall error = projector-px distance between each dot and the projection, under the new pose, of the stage point
where its camera ray lands (p50/p95 → `VerifiedPx`). The verification frames become the new **reference**. Gate (§3.4).
One edit through the lane (projector fields, snapshot, quads), then the off-lane blend re-solve when the projector blends
automatically (M12).

Bench (L§9.2, simulation, identities given): pose-only from 510 dots at σ 0.15–0.3 camera px gives **0.04–0.17 px RMS,
≤ 0.27 px p95** on the wall, from bumps of 14–127 px. The 96 px grid with σ 0.3 is the worst case (0.27 px p95). Hence the
64 px default.

### 3.8 Re-seating raster objects (`PlayMap.Core/Calibration/Reseat2D.cs`)

A pose cannot carry objects defined in projector pixels: firstfive's pixel quads and, on a 2D rig, output corner pins
and lattices. With the camera fixed, the reference check gives `ref: projector px → camera px` and the new check gives
`new` (thin-plate splines over the matched dots, `ThinPlateSpline` exists, regularised λ = 1e-3). A raster point p moves
to `p' = new⁻¹(ref(p))`: Newton on the TPS, seeded by p plus the nearest dot's displacement, converged at 1e-4 px. Each
corner or node is moved; the reply gives the largest move and the TPS's leave-one-out residual as the uncertainty. Rules:
- pixel quads: always re-seated with their projector;
- output warps (corner pin, lattice): re-seated only on a 2D rig (the projector has no solved pose). On a 3D rig they
  are hand touch-ups and stay; the reply names them ("Output 2 has a hand lattice; check it with OVERLAY GRID");
- surface masks (UV) and 3D surfaces: never touched;
- a camera that moved: no re-seat (the reference is invalid); the words ask for `CALIBRATION ACCEPT` to re-reference.

### 3.9 Triggers and policy

| Trigger | What happens |
|---|---|
| Operator, Companion, Patterns cue, MCP | `CALIBRATION CHECK …` / `RESOLVE …` |
| Device lens readback ≠ snapshot (S1) | stale term, projector in `moved[]` with verdict `lens (readback)`, proposal `CALIBRATION RESOLVE pj… LENS` |
| Camera drift monitor (S2) | `camera.state` drifting/bumped → proposal `CALIBRATION CHECK ALL` |
| Scheduler (later, R4.5 package) | a CHECK between shows; RESOLVE only with an operator's standing approval (a policy flag), never while armed |

Detect and propose; a person approves anything that changes the air while a show might be running (ROADMAP §6's patent
note on automatic content-based correction). In rehearsal, RESOLVE's own gate applies the result. While armed: CHECK and
RESOLVE are refused, and drift monitoring only observes.

### 3.10 History

`CALIBRATION HISTORY pj… [n]`: snapshots newest first, each with source, score, grade, verified p95, who, and the delta
from the previous one: Δposition (mm), Δrotation (degrees, angle of Rₐᵀ R_b) and **Δwall px** (p95 over ≤ 2 000 visible
stage samples of ‖π_b(X) − π_a(X)‖: how far the picture moved). The desk draws it as a strip: score over time, with bumps
as spikes.

### 3.11 Files and persistence (`PlayMap.Engine/Calibration/CalibrationFiles.cs`)

`<project folder>/calibration/<cs…>/`: `snapshot.json` (the record, for recovery), `confidence.f32`, `confidence.png`,
`residuals.json` (per pin, or a histogram and the worst 200 pairs), `reference.<cm…>.json` (`[{"id","proj":[x,y],"cam":[x,y],"fwhm","peak"}]`
plus exposure, latency, time, seed), `frames.<cm…>.png` (the black frame and the all-dots composite, for drift and audit).
Written by the worker as `*.tmp`, then moved over. With no project folder, they are kept in memory and written at the
first `PROJECT SAVE`. The janitor, after each save, deletes `calibration/cs*/` folders that neither the saved project nor
any root in the undo/redo stack references (a small `ProjectStore.Roots()` enumeration hunk). Size: ≈ 60 KB per snapshot
(1080p), so 100 snapshots × 8 projectors ≈ 48 MB, which the reply and the desk show.

---

## 4. Spine needs

### 4.1 Model (Core; init-only; an older file reads absent fields as null/empty/default)

```csharp
// src/PlayMap.Core/Model/CalibrationModel.cs (new)
public enum CalibrationSource { Pins, Scan, Check, Revert, Import, Manual }

/// <summary>One reason in a score: its code, its share in px-equivalents, the words, where on the projector it is,
/// a verb line that fixes it ("" when only hands can) and what the hands do.</summary>
public sealed record ScoreTerm(string Code, double Px, string Words, string Where, string Fix, string Hint);

public sealed record CalibrationScore(double Score, string Grade, double RmsPx, double P95Px, double Confidence,
    ImmutableArray<ScoreTerm> Terms, string Words)
{ public static readonly CalibrationScore None = new(0, "", 0, 0, 0, [], ""); }

/// <summary>A calibration result, never edited after it is made: the projector's optics, pose, locks and pins as solved,
/// the score that explains them, and where they came from. Files beside it hold its maps and references.</summary>
public sealed record CalibrationSnapshot(
    string Id,                       // "cs…"
    string Name,                     // "Projector 2 · re-solve · 13 Oct 21:04"
    string ProjectorId,
    long CreatedUnixMs,
    CalibrationSource Source,
    string? ScanId,
    string? ParentId,
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    SolveLocks Locks,
    ImmutableArray<CalibrationPin> Pins,
    CalibrationScore Score,
    double VerifiedP50Px,            // 0 = not verified on the wall
    double VerifiedP95Px,
    string MeshHash,
    string LensReadback,             // "zoom=1234;shiftH=-20;shiftV=310;focus=88" or ""
    string Who,
    string DataPath,                 // "calibration/cs…/"
    bool Pinned) : IEntity;
```

| Record | Property (json) | Default |
|---|---|---|
| `Stage` | `Calibrations` (calibrations) | [] |
| `Projector` | `CalibrationId` (calibrationId), `FallbackId` (fallbackId), `StagedId` (stagedId) | null, null, null |
| `CameraRig` | `Fixed` (fixed): the camera is mounted and may be used for checks and drift | false |
| `ProjectSettings` | `CalibrationKeep` (calibrationKeep) 1–100; `CalibrationGate` (calibrationGate) | 20; 1.5 |
| `Ids` | `Calibration = "cs"` | — |

`Project.AllIds` includes snapshots. Generic `GET cs…` works; `SET cs… name|pinned` is allowed and any other field is
refused ("a snapshot is never edited — CALIBRATION RECALL makes a new one"); `REMOVE cs…` = `CALIBRATION FORGET`
(refused for current, fallback or staged). `ADD calibration` is refused ("snapshots are made by solves").

### 4.2 Verbs (new `VerbCategory.Calibration`, appended to the enum; all mutating ones need pairing)

| Verb | Grammar | Risk | Mutates | Reply |
|---|---|---|---|---|
| `CALIBRATION STATUS` | `CALIBRATION STATUS [<projector>\|ALL]` | Low | no | `{"projectors":[{id,score{…},snapshot,fallback,staged,check{…},stale[],history}],"camera":{…},"task":{…}}` |
| `CALIBRATION HISTORY` | `CALIBRATION HISTORY <projector> [<n> (default 20)]` | Low | no | `{"projector","snapshots":[{id,name,at,source,score,grade,verifiedP95,who,delta:{mm,deg,wallPx},current,fallback,pinned}]}` |
| `CALIBRATION SAVE` | `CALIBRATION SAVE <projector>\|ALL [AS <name>]` | Review | yes | `{"snapshots":[…]}` (pinned, source `manual`, current state re-scored) |
| `CALIBRATION RECALL` | `CALIBRATION RECALL <snapshot>` | Review | yes | `{"projector","applied":"cs…","recorded":"cs…","deltaWallPx"}` |
| `CALIBRATION ACCEPT` | `CALIBRATION ACCEPT <projector>\|ALL\|<snapshot>` (a snapshot: apply if staged, then make it the fallback) | Review | yes | `{"projector","fallback":"cs…","reference":"written\|kept"}` |
| `CALIBRATION FALLBACK` | `CALIBRATION FALLBACK <projector>\|ALL` | Review | yes | as RECALL |
| `CALIBRATION FORGET` | `CALIBRATION FORGET <snapshot>` | Review | yes | `{"removed":"cs…"}` |
| `CALIBRATION CHECK` | `CALIBRATION CHECK [<projector>…\|ALL] [CAMERA <camera>] [MODE QUICK\|CODED] [REFERENCE]` (REFERENCE stores this check as the reference without judging) | **High** (patterns on the air) | yes (air only; no document change) | accepted at once; the result in `STATE.calibration` and as the task's final reply: `{"camera":{…moved,shiftPx},"projectors":[{id,verdict,dots,matched,shiftPx:{p50,p95},fitPx,focus,words,fix}],"ms","words"}` |
| `CALIBRATION RESOLVE` | `CALIBRATION RESOLVE <projector>…\|MOVED [LENS] [DENSE] [STAGE]` | **High** | yes | `{"projectors":[{id,snapshot,path:"quick\|lens\|coded\|dense",applied,staged,score{…},verifiedPx:{p50,p95},delta:{mm,deg,wallPx},reseated:{quads,warps,maxPx}}],"camera":{"resolved":bool,"rmsPx"},"words"}` |
| `CALIBRATION CANCEL` | `CALIBRATION CANCEL` | Low | yes | `OK` with words |
| `CALIBRATION PREVIEW` (S3) | `CALIBRATION PREVIEW <snapshot> [SPLIT <0–1>] \| CALIBRATION PREVIEW OFF` | Review | yes (air) | `{"snapshot","split"}` |
| `CALIBRATION REPORT` (S5) | `CALIBRATION REPORT [<projector>\|ALL]` | Low | no | text, or JSON with `?json` on HTTP |

Long verbs (`CHECK`, `RESOLVE`) answer `Accepted("calibration task ct… started")` (receipt stage *accepted*). The task's end
is published on STATE and as a second reply line on transports that support it, and the journal row carries the final
words (receipt stage *observed*: the verification check). With the show-safety package's per-argument risk
(`RiskOf(line)`), `RESOLVE … STAGE` drops to Review because it changes nothing on the air until accepted.

Hunks in existing verbs: `PROJECTOR SOLVE` and `SCAN APPLY` record snapshots (§3.4); their replies gain `"snapshot"`
and `"score"`.

### 4.3 STATE, context and events

Top level, after the last key:

```json
"calibration": {
  "moved": ["pj…"],
  "task": {"id":"ct…","kind":"check|resolve","state":"capturing|matching|solving|verifying|done|failed|cancelled",
           "projector":"pj…","progress":0.4,"words":"…"} ,
  "camera": {"id":"cm…","state":"stable|drifting|bumped|unjudged","shiftPx":0.3,"words":"…"},
  "projectors": [
    {"id":"pj…","name":"Projector 2","score":0.6,"grade":"A","words":"A 0.6 — fits 471 dots to 0.31 px",
     "worst":"","confidence":0.94,"snapshot":"cs…","fallback":"cs…","staged":null,"history":7,"verifiedP95":0.31,
     "check":{"at":1760…,"verdict":"stable|moved|lens|camera|unseen|surface","shiftPx":0.2,"words":"…"},
     "stale":[],
     "terms":[{"code":"rms","px":0.4,"words":"…","where":"","fix":"","hint":""}]}
  ]
}
```

These are the companion package's keys (`score`, `grade`, `words`, `worst`, `moved`, `history`) under the names it
expects. `stage.projectors[]` rows gain `"grade"`. Context: one item per projector, `calibration:<pj>` (kind
`calibration`; light green for A/B and not moved, amber for C, stale, staged or drifting, red for F, moved or camera
moved; words = the score's words, or the check's). The headline names a moved projector first. The companion package's
event source emits `calibration.moved` when `moved[]` changes. The Show Bus context carries the items to Patterns' Eye
and Stagewatch unchanged.

### 4.4 Seams (C#)

```csharp
// PlayMap.Engine — implemented by ScanRunner (it owns the display, the cameras, latency and settle)
public interface IDotCheckBackend
{
    /// <summary>Shows each frame on its output (others keep the show), captures the first settled camera frame after
    /// each present + latency, and answers the gray8 frames in order. One capture at a time, exclusive with a scan.</summary>
    Task<DotCapture> CaptureAsync(DotCaptureRequest request, IProgress<double>? progress, CancellationToken ct);
}
public sealed record DotCaptureRequest(string CameraId, IReadOnlyList<DotFrame> Frames);
public sealed record DotFrame(IReadOnlyList<(string OutputId, ReadOnlyMemory<byte> Gray8, int Width, int Height)> Show);  // outputs not named show black if in the check, the show otherwise
public sealed record DotCapture(IReadOnlyList<ImageGray8> Frames, double LatencyMs, string Exposure, string Problem);

// PlayMap.Engine — facts the ledger reads (DeviceService implements it; null when no devices extension)
public interface ILensFacts { string? LensReadback(string projectorId); }

// PlayMap.Render (declared by firstfive) — served by CalibrationService
public interface IOverlayHeat { HeatGrid? For(string projectorId); }

// PlayMap.Vision — a DriftWatch on chosen regions (new constructor overload; the old one unchanged)
public DriftWatch(ImageGray8 reference, IReadOnlyList<RectD> regions, DriftWatchOptions? options = null);
// and the global offset for matching:
public static class PhaseCorrelation { public static (Vec2d Shift, double Confidence) Of(ImageGray8 a, ImageGray8 b, int size = 256); }
```

`EngineServices` gains `IDotCheckBackend? DotCheck`, `ILensFacts? LensFacts` (init properties in this package's own
partial file `EngineServices.Calibration.cs`, the ops pattern). `ScanSimulator` gains `RenderFrame(ReadOnlySpan<byte>
projectorGray8, int projector)` (any frame through the simulated light transport), and `SimulatedRig` gains
`Bump(int projector, Vec3d rotation, Vec3d translation)` and `BumpCamera(…)`, for the tests **and the demo** ("knock
Projector 2").

### 4.5 NodeSettings

`CalibrationDriftHz` (1.0; 0 turns the monitor off), `CalibrationCheckSpacingPx` (64), `CalibrationTaskTimeoutS` (120).

---

## 5. Files

**New**
- `src/PlayMap.Core/Model/CalibrationModel.cs`
- `src/PlayMap.Core/Calibration/CalibrationScoring.cs`, `ConfidenceMap.cs`, `MotionAttribution.cs`, `Reseat2D.cs`,
  `SnapshotDelta.cs`; `SolveUncertaintyAnalysis.cs` (created here; facade extends it by a hunk)
- `src/PlayMap.Core/Scan/DotCheck.cs` (`DotPattern`, `DotDetector`, `DotMatcher`, `DotObservation`, the coded IDs)
- `src/PlayMap.Engine/EngineCore.Calibration.cs` (router, read verbs, edit verbs)
- `src/PlayMap.Engine/EngineServices.Calibration.cs`
- `src/PlayMap.Engine/Calibration/CalibrationLedger.cs`, `CalibrationTasks.cs`, `CheckRunner.cs`, `Resolver.cs`,
  `CalibrationRuntime.cs`, `CalibrationFiles.cs`, `DriftMonitor.cs`, `CalibrationState.cs` (STATE/context writers)
- `src/PlayMap.Vision/Drift/PhaseCorrelation.cs`
- Tests: `tests/PlayMap.Core.Tests/Calibration/{CalibrationScoringTests,ConfidenceMapTests,MotionAttributionTests,Reseat2DTests}.cs`,
  `tests/PlayMap.Core.Tests/Scan/DotCheckTests.cs`; `tests/PlayMap.Engine.Tests/Calibration/{LedgerTests,CheckResolveSimTests,
  CalibrationVerbTests,CalibrationFilesTests,DriftMonitorTests}.cs`; `tests/PlayMap.Vision.Tests/PhaseCorrelationTests.cs`;
  `tests/PlayMap.App.Tests/CalibrationCardTests.cs`

**Touched (small hunks)**
- `src/PlayMap.Core/Model/StageModel.cs` (`Stage.Calibrations`, `Projector.*Id`, `CameraRig.Fixed`), `Project.cs`
  (`AllIds`, `ProjectSettings` two properties), `Ids.cs` (`Calibration`)
- `src/PlayMap.Core/Model/ProjectStore.cs`: `IEnumerable<Project> Roots()` (current + undo + redo) for the janitor
- `src/PlayMap.Core/Wire/VerbCatalog.cs`: the category and rows
- `src/PlayMap.Core/Scan/ScanSimulator.cs`: `RenderFrame`; `src/PlayMap.Engine/Scanning/ScanSimulation.cs`: `Bump`, `BumpCamera`
- `src/PlayMap.Engine/Scanning/ScanRunner.cs`: implements `IDotCheckBackend` (reuses latency, settle, cameras; mutual
  exclusion with scans)
- `src/PlayMap.Engine/EngineCore.Mapping.cs`: `SolveProjector` and `ApplyScan` call `CalibrationLedger.Record` inside
  their edit; `SolveBlend`'s maths callable off-lane (shared with M12)
- `src/PlayMap.Engine/EngineCore.Router.cs`, `EngineCore.Edits.cs` (snapshot SET/REMOVE rules)
- `src/PlayMap.Engine/StateWriter.cs`, `ContextBuilder.cs`
- `src/PlayMap.Engine/Devices/DeviceService.cs`: implements `ILensFacts`
- `src/PlayMap.Vision/Drift/DriftWatch.cs`: the regions overload
- `src/PlayMap.App/ViewModels/MapInspectors.cs` (projector card: score ring, terms, history), `DrawerViewModels.cs`
  (moved chip), `State/StateSnapshot.cs` (calibration keys)
- Papers: `docs/WIRE.md`, `docs/UX.md` §4.4/§4.8/§5.3 (what is built), `docs/ROADMAP.md` (R1.2, R1.4, R4.8 status),
  `docs/ARCHITECTURE.md` (calibration as a service), and a "Calibration operations" section in a new `docs/MAPPING.md`
  (CLAUDE.md points to it; it does not exist on main)

---

## 6. Dependencies

**Work packages**
- **firstfive**: `OVERLAY HEAT` and `IOverlayHeat` (the confidence map on the wall), the pins layer (residual rings),
  pixel quads (re-seated here). Without it: no wall heat map and nothing to re-seat; everything else stands.
- **facade**: `SolveUncertainty`, priors and the lens envelope feed `uncertainty`, `depth`, `focal` and `shift`. Without
  it: this package's `SolveUncertaintyAnalysis` without prior rows and datasheet-free envelopes (device prior ± 3 %).
- **show safety**: `IShowGate` (armed, blind edit), per-argument risk. Without it: never armed.
- **companion**: consumes `STATE.calibration` and `calibration.moved`; generates actions from `VERBS`.
- **phonescan**: a Phone or Folder camera cannot be "fixed". CHECK refuses them with words; a phone may still do a DENSE
  re-solve by scan.
- **blend off-lane** (carry-over): M12 implements the off-lane solve for re-solves and exposes it so `BLEND SOLVE` can use
  it. If the blend package lands first, this package calls its API instead.
- **fleet / Patterns Eye**: shows the context items per node.

**Third party**: none. Everything is managed C# on existing numerics (`PoseSolver`, `LevenbergMarquardt`, `Svd`,
`ThinPlateSpline`, `MeshRaycaster`, `Pcg32`), plus Vision's FFT.

---

## 7. Verification here

| What | How | Real or simulated |
|---|---|---|
| Score terms, grades, fix lines | Core unit tests with injected faults; every fix line parsed by `VerbCatalog.Match` | Real maths |
| Confidence map | Core tests against a seeded Monte Carlo of the solve | Real maths |
| Dot detection, matching, coded escalation | `ScanSimulator.RenderFrame` with noise, blur and ambient from `SimOptions`; scenes floor-and-box, wall-and-cylinder, stepped-facade | **Simulated** light transport (no rolling shutter, projector processing, interreflection) |
| Attribution and the ladder | `SimulatedRig.Bump/BumpCamera`; whole verbs through the engine with the simulated camera; wall error against the simulator's truth | Simulated, end to end through the real engine and scan runner |
| Patterns reach outputs | `IScanDisplay` on the **real render host, headless on llvmpipe** (`ShowAsync` completes with present stamps) | Real GL; the camera is simulated |
| Lane responsiveness, task states, cancel | Engine tests with timers (`PING` ≤ 50 ms during RESOLVE) | Real |
| Snapshots, retention, gate, undo, files, janitor, restart | Engine tests with a temp project folder; a second `EngineCore` over the saved folder | Real |
| Device lens trigger | Fake `ILensFacts` | Simulated device |
| Drift monitor | Synthetic camera frames shifted by known amounts | Simulated camera |
| Split preview (S3) | llvmpipe read-back | Real GL |
| **Not here** | A real camera's exposure lock, MJPEG artefacts, rolling shutter on dots, real projector focus and keystone processing, thermal drift. **The bench numbers (≤ 0.3 px) are simulation results and must be measured in the lab (R0.5)** | Lab |

Nothing is compile-only: no platform API is involved.

---

## 8. Desk and Companion surface

**Projector object card** (UX P15): a score ring with the grade letter and score ("A 0.6"), the worst term's words in
one line, a confidence bar, and "verified 0.31 px · 21:04". A click opens the **score panel**: every term as a row
(code icon, px bar, words, where) with its **Fix** button (the term's verb line through a `DeskAction`; the tooltip shows
the line) and **Show on wall** (`OVERLAY HEAT pj…`, numbered areas matching the rows). Below that is the **history
strip**: score over time with a dot per snapshot (source icon; the fallback ringed; staged hollow). Hovering a dot shows
Δwall px. Right-click a dot: Recall · Set as fallback (ACCEPT) · Pin · Rename (F2) · Compare with current (S3 split, with
a draggable split line on the wall) · Forget.

**Readiness area**: a red chip "Projector 2 moved ≈ 16 px" with **Re-solve P2** (`CALIBRATION RESOLVE pj…`), and
"Camera 1 moved: re-solved from P1 and P3" as an info chip. A staged result shows "P2 re-solve staged (score 1.9 > gate
1.5) — Accept · Compare · Discard". During a task the dial shows the steps and its ETA; Cancel is one key (Esc).

**Right-click on a projector** (same `DeskAction`s): Check · Re-solve · Re-solve (lens changed) · Accept as fallback ·
Revert to fallback · History… · Save snapshot · Show confidence on wall.

**Companion** (the companion package's Calibration section, fed by these keys):
- Per-projector tiles: grade ring and colour (A/B green, C amber, F red), `cal_words`; press → `CALIBRATION CHECK pj…`;
  **hold** → `CALIBRATION RESOLVE pj…`.
- Keys: **CHECK ALL**; **RE-SOLVE MOVED** (hold; disabled when `moved[]` is empty); **ACCEPT** (hold); **FALLBACK** (hold;
  a red outline while armed means refused). The **HISTORY** encoder (Stream Deck+): rotate to scroll snapshots on the
  LCD strip (score, age), press to `CALIBRATION PREVIEW` the selected one, hold to RECALL.
- Variables: `cal_<pj>_score`, `cal_<pj>_grade`, `cal_<pj>_words`, `cal_moved` (names), `cal_task` (step and progress),
  `cal_camera`.
- Feedbacks: a moved projector flashes red at 1 Hz; a running task shows a progress ring.

**AI**: the assistant reads the terms and fixes in its context and proposes the fix lines; RESOLVE from the assistant is
always staged and needs a person's ACCEPT.

**Patterns / Stagewatch**: the `calibration:<pj>` context items light Patterns' Eye per node. A Patterns cue can fire
`CALIBRATION CHECK ALL` before doors.

---

## 9. Risks and open questions

| Risk | Mitigation |
|---|---|
| Real dots under real optics (blur, MJPEG blocks, rolling shutter) detect worse than the simulator | Exposure locked to a multiple of the refresh period; settle detector; FWHM-aware area limits; CODED escalation; DENSE as the last rung. Lab measurement before claims |
| Showing dots is visible: a check cannot run mid-show | By design: refused while armed. Between items, or with the scheduler between shows. Imperceptible checks are research (L3) |
| A single-projector, single-camera rig cannot tell camera from projector motion | Said in words; drift monitor (S2) or a second camera settles it; the proposal is the projector re-solve either way (the result on the wall is the same for small moves) |
| The surface moved (an object nudged) is misread as a projector move | Step 4's pose and lens fits fail → `surfaceSuspect`, no proposal. A test covers it |
| Snapshots bloat the project file | ≈ 2 KB per record (pins included); default keep 20 per projector; artefacts in files; sizes shown |
| Pins rewritten after a pose re-solve surprise an operator | The snapshot keeps the old pins; RECALL restores them; the reply says "12 pins follow the new pose" |
| Gate too strict or too lax | A project setting; the staged path loses nothing; numbers from the lab will tune the default |
| Patent space around drift correction from reduced pattern sets (ROADMAP §6) | Detect and propose; a human approves while a show runs; the freedom-to-operate review (ROADMAP §5) before a commercial release |
| **Open**: should CHECK be allowed while armed on an output that is blacked out? | Proposed: no this round; revisit with the show-safety package |
| **Open**: the grade boundaries (A ≤ 1.0, B ≤ 1.5) follow OmniCal's guidance for dense captures; are they right for 6–12 pins? | Lab data on pin solves; per-source boundaries if needed |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Scoring, confidence map, snapshot deltas + tests | 10 |
| Dot pattern, detection, matching, coded IDs + tests | 9 |
| Attribution, re-solve ladder, re-seat + tests | 9 |
| Model, JSON, ids, catalogue rows, SET/REMOVE rules | 4 |
| Ledger, gate, retention, hunks in PROJECTOR SOLVE and SCAN APPLY | 5 |
| Tasks worker, check runner (`IDotCheckBackend` in ScanRunner), off-lane blend, files, janitor | 10 |
| Simulator hooks (`RenderFrame`, `Bump`), end-to-end simulated tests | 7 |
| Drift monitor, device lens trigger, STATE/context/events | 5 |
| Desk: card, score panel, history strip, chips, menus + tests | 9 |
| Papers | 3 |
| **Total** | **71** |
