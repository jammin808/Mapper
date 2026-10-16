# Multi-view scanning: many projectors, many views, curved surfaces

A single-view scan is one camera place and one solve per projector. Multi-view scanning lets the operator move the camera (a
phone, a webcam on a stand) to more places; each place is one more **view**, and every projector, every view and the
camera's own optics are solved **together**, in one frame, anchored on the projectors. From all views together each projector
gets a **dense depth map** on its own raster, so curved plaster, a column or a sculpture needs nothing special; the
projectors' meshes are joined into one stage mesh, and the scan reports how much of each projector's light the views saw.

The Core of it (`src/PlayMap.Core/Scan/MultiView/`) is pure computation. The engine drives it (`src/PlayMap.Engine/Scanning/`,
"In the engine" below): `SCAN START … INTO <scan>` adds a view, and `SCAN SOLVE`, `SCAN VIEWS`, `SCAN DROP`, `SCAN SURFACE … ON`
and `SCAN SUGGEST` work on the views a scan keeps.

## Why anchor on the projectors

A projector does not move between views, and every decoded camera pixel names the projector pixel that lit it, exactly.
So **a projector pixel is a surface point's name across views**: projector pixel u seen from view A and from view B is
the same 3D point. No feature matching and no texture are needed; a white wall and curved plaster work as well as a
painted set.

## The pipeline

### 1. Inverting each view's decode (`InverseCorrespondence`)

A decode answers "which projector pixel lit this camera pixel". The solve needs the other direction: "where did this view
see projector pixel u". For any u, the decoded camera pixels whose projector coordinate lies within 3 projector pixels of u
are fitted with a local affine map projector ← camera (weighted by decode confidence; the noise is in the decoded
coordinate, not in the camera pixel's place), and the map is inverted at u. An answer is refused — never guessed — when:

| Rule | What it catches |
|---|---|
| fewer than 6 pixels, or they lie on a line | too little seen |
| the fit's residual, in camera pixels, is over 0.5 | a neighbourhood spanning a depth edge (two surfaces seen in two places) |
| no decoded pixels on some side of u | the edge of what the view saw: u would be extrapolated |
| the pixels fill less than half the disc the fit's own scale predicts | a rim seen edge-on |
| the nearest decoded pixel is more than 1.5 projector pixels from u | a fold the view did not see (a cylinder's top seen from below: the wall above and the face below meet in the camera) |
| the 2 × 2 camera pixels around the answer did not themselves decode near u | an answer just past a silhouette |

On the test scene every answer lands within 0.04–0.06 px RMS of where the camera truly saw that projector pixel, 99 % within
0.25 px, and no projector pixel hidden from the camera is ever answered.

### 2. Tracks (`TrackSet`)

On each projector's raster a grid at a 16-pixel stride (nodes at ((i + ½)·16, (j + ½)·16)); every node any view placed is a
**track**, with one observation per view that placed it. Tracks are ordered by projector id, then row and column — the same
tracks in the same order whatever order the views arrive in. `TrackSet.Without(view)` drops a view's observations.

### 3. The start

- Each view is self-calibrated on its own (`SelfCalibration.Solve`, from its dense pairs: shared camera pixels tie two
  projectors' scales within a view).
- The views are chained along a **maximum spanning tree** whose edges are shared projectors, weighted by shared tracks,
  from the view that solved the most projectors. Each link is a similarity fitted to the shared tracks' points in the two
  views' frames (Umeyama, refitted without the pairs past three times the median distance).
- A view that shares no projector with the tree is left out with words: *"view v3 shares no projector with the others;
  include part of a projector the other views saw"*. A projector no chained view solved is named too.
- Held projectors carry the chain onto the stage (below). Each track starts at the median of its triangulated depths.

### 4. The joint adjustment (`MultiViewBundle`)

| Unknown | Count | Prior (1σ) |
|---|---|---|
| each view's pose (rotation, centre) | 6 per view | — |
| each camera rig: f (fy = f·aspect), cx, cy, k1, k2 | 5 per rig, none when `Calibrated` | f ±10 %, cx/cy ±2 % of the width, k ±0.05 |
| each projector: pose, f, cx, cy | 6 + 1 + 2; f held under `SolveLocks.Focal`, cx/cy under `SolveLocks.Shift` | throw ±5 %, cx/cy ±2 % of the width |
| each track: its depth along its projector's ray | 1 per track | — |

- **Residual**: the camera reprojection π(K_c, D_c, T_v, X_t) − x_{t,v}, with X_t = C_p + d_t·R_p·K_p⁻¹[u, 1]ᵀ. The
  projector's own observation is exact by construction — that is the anchor. Huber loss, δ = 1.5 px.
- **Solver**: Levenberg–Marquardt; the depth block is diagonal and is eliminated by the Schur complement, so each step
  solves a dense system the size of the poses and optics (about 35 unknowns for 3 views, 2 projectors and a phone). After the
  first adjustment, observations more than 2 px out are dropped and it runs again. Sums run in a fixed order: the same input
  gives the same result on any machine.
- **Gauge**: with no projector held, the root view is fixed and the projector it sees most keeps its distance from it (the
  scale; lengths are then in that baseline, `Metric` false, until the engine scales them by a known distance or registers the
  scan). With **one** held projector, it fixes the frame and the root view keeps its distance from it; the scale comes from
  the stage's distances between the held projector and the others (the words say so: hold two for a metric solve — one held
  projector cannot fix scale, since scaling the scene about it changes no observation). With **two or more** held, the stage
  fixes everything and the result is metric.
- **HOLD**: a held projector is never a parameter. Its result *is* its stage pose and prior optics, bit for bit. This is "re-solve
  only the projector that moved": one new view that sees the moved projector and two held ones is enough.
- **Projector lenses** are held as given (taken as distortion-free or measured); camera rigs solve k1, k2.

**Outputs** (`MultiViewResult`): per view its pose, RMS, observations, inliers and RMS per projector; per projector its pose,
optics, RMS, tracks, inliers and the 1σ of its position along its axis (from the reduced system with the priors, scaled by the
residual's own variance — a formal σ: on the simulator it is smaller than the true error, which comes from the edges, not the
noise); per rig its optics and focal σ; per track its 3D point, RMS and views; the views left out and why.

### 5. A bad view: `Influence` and `DropView`

A view that disagrees with the rest (the camera moved between two projectors' runs) does not show up as one bad view: a
joint solve spreads its error over every view (on the test scene a 2 px shift of one view's run gives 0.19–0.35 px on every
view and projector alike). So the scan names it by **influence**: `MultiViewCalibration.Influence` re-solves without each view
in turn (warm, from the current result) and reports each view's RMS with and without it. The view whose removal lowers the
residual most is the one to drop (what `SCAN DROP` is for).

`DropView(input, previous, view)` removes the view's pairs and observations (tracks no other view saw go too) and re-solves
**warm** from the previous result: the root view stays where it was and keeps its distance to its projector, so the frame and
its scale do not jump (`Metric` is kept). The warm start is taken only when the previous solve held every projector the input
holds, at the same stage pose and optics; otherwise the previous frame is not the stage's, and the drop starts afresh in the
stage frame. Dropping a view the scan does not have, or its only view, is refused with the reason.

### 6. Dense depth per projector (`ProjectorDepth`)

For every projector pixel on a 2-pixel grid (1 for a difficult surface), each view's camera position comes from its inverted
decode at that pixel. The projector's ray is intersected with each camera's by moving along the projector's ray to the depth
that camera sees best (a few Gauss–Newton steps from the rays' closest approach). Views combine by inverse variance: a view's
variance is its pixel uncertainty (confidence, fit residual) over how fast its pixel moves with depth, so a grazing or
short-baseline view weighs little. Views more than 2 px out of the combined point are dropped and the rest combined again.
The result is a depth, a σ and a view count per cell, and an organised cloud with normals toward the projector — `Mesh()` is
its `GridMesher` mesh, whose UVs are the projector's own raster.

### 7. The stage mesh (`ScanMerge`)

The projectors' meshes are joined so each surface appears **once**: projectors are taken steadiest first (median σ); a later
triangle near kept triangles (1.5 local grid steps) is dropped when any of them measured that place as well or better, and
when every one of them measured it worse, they are dropped instead and it is kept. Each vertex carries its projector and
depth-map cell (for click-to-surface).

### 8. Coverage (`CoverageMap`)

Per projector, on cells of 4 × 4 projector pixels: how many views decoded each cell (0, 1, or 2 for two and more), and the
shares of the lit raster seen at least once (`Seen`) and twice (`Twice`). The lit raster is the scanned region by default, or
a mask the caller knows better. "Aim at the red" is the lit cells still at 0.

### 9. Regions per projector (`ProjectorRegions`)

Each projector's depth-map cloud is split into regions on its own raster (region growing, flattest seeds first; a region
carries on only where the surface varies less than 0.01, so a column of 25 cm radius sampled every centimetre grows whole while
a wall-to-floor crease stops it), so click-to-surface works on that projector's own smart scan. Regions of different projectors
that lie on one surface are then joined: half of the smaller region's 1 cm voxels must have a voxel of the larger at or next to
them. The joined region lists every projector that lights it, with one part per projector (its cells on that projector's
depth grid), its centroid, its best plane and whether it is planar (RMS within 5 mm). Ids `rg1`, `rg2`, … go largest first.

The join measures the overlap of the **smaller** region, not the union's: a wall lit by two projectors from two sides is lit on
different but overlapping stretches, and their intersection over union can fall under a half while it is plainly one wall.
Comparing a voxel with its neighbours too makes two samplings of one surface (each projector's own grid) agree.

### 10. The simulator (`MultiViewSim`)

Multi-view scans with no hardware, over the existing `ScanSimulator`: each view of each projector is its own
single-projector scene (`MultiViewScene.For`), traced, rendered frame by frame (noise seeded per capture with `Pcg32`) and
decoded as a real scan would be. `MultiViewScenes.WallAndCylinder()` is the wall and cylinder of `SimScenes` lit by two
projectors (throw 1.2 from the left, 1.0 from the right, 512 × 320) and seen by a phone (64° field of view, 480 × 360, a little
residual distortion) from three places: left, right and low in the middle. The simulator also answers the truth: the point a
projector pixel lights and the lit cells of a projector's raster.

## Measured (simulator, `tests/PlayMap.Core.Tests/Scan/MultiView/`)

Wall and cylinder, 2 projectors, 3 views, the phone's focal prior 8 % off, the throw priors 5 % off, nothing held; the solve
registered to the world on its track points (as registering a scan to a surveyed model does):

| What | Bar | Measured |
|---|---|---|
| projector centre | 5 mm | 1.16 mm (pj1), 1.44 mm (pj2) |
| projector orientation | 0.05° | 0.0042°, 0.0077° |
| projector throw | 0.2 % | 0.033 %, 0.050 % |
| phone focal length | 0.5 % | 0.004 % |
| residual | — | 0.01 px RMS over 1 244 tracks; 8 iterations |
| time (4 cores, alone) | reported | the solve 4.5 s, almost all of it each view's start on its dense pairs (the adjustment itself about 0.2 s, a warm drop 0.17 s); dense depth of both projectors about 2 s |
| dense surface to the truth mesh | ≤ 3 mm expected; **tightened to 1 mm** | 0.26 mm and 0.27 mm RMS (99 % within 0.9 mm; the worst, 6 mm, on the floor at grazing incidence, where its σ says 4 mm) |
| coverage of the lit raster | ≥ 95 % with 3 views; ≤ 80 % for one | 99.9 %; one view alone 70.8–72.0 % (at most 78 % of either projector) |
| a view shifted 2 px | raises RMS; dropping it < 0.5 px | 0.009 → 0.29 px; without it 0.009 px (without either good view 0.10 and 0.24 px) |
| HOLD | bit-identical | every held field bit for bit, and every solved point of a held projector's tracks on its stage ray (back onto its own pixel within 10⁻⁶ px); with both held, every view within 0.06 mm and 0.001° |
| regions | each surface once, every projector that lights it | the wall one region [pj1, pj2] (0.1 mm RMS, planar); the cylinder's side one curved region [pj1, pj2] holding all 5 317 and 4 065 of the two projectors' side cells; the floor two pieces (the cylinder's shadow cuts the lit strip), each [pj1, pj2] |

## In the engine

- **A view per run.** Every scan keeps each run as a view: the camera and its optics as they were, each projector's decode, how
  its pictures were paired. `SCAN START <camera> … INTO <scan>` takes another view of a scan this node holds and solves every view
  together (`MultiViewScan`): the projectors' priors from the model at the raster the patterns were shown at (their locks honoured),
  one rig per camera, each view's dense pairs to start from, the tracks.
- **The frame.** With nothing held, the joint solve is carried onto the scan's own frame by its first view: that camera keeps the
  place the scan's last solve gave it, and its mean distance to its projectors (so the scale is the first solve's — metric when it
  was). Adding a view therefore moves nothing already placed beyond what the new view corrects. `SCAN SOLVE <scan> HOLD <projector>…`
  holds those projectors at their stage poses instead: the solve is then in the stage's frame (metric with two held). Each view
  keeps the site frame its place is in: after `SITE ORIGIN` moved the stage, the anchor's place (or a warm drop's frame) is carried
  by that move first, so the next view, solve or drop lands in the stage's frame now (`docs/MAPPING.md`).
- **Latency.** A view through a streaming camera keeps the latency its own white flashes measured for each projector (`SCAN VIEWS`,
  STATE); the scan's latency for a projector is its newest view's.
- **A bad view.** Every joint solve measures each view's influence; the view whose removal halves the residual (when it is at least
  0.1 px) is named in `SCAN VIEWS` (`disagreeing`), in the solve's words, and in `SCAN APPLY`'s score: the dense fit's fix is
  `SCAN DROP <scan> <view>`. `SCAN DROP` solves the rest warm from the last solve; the view stays in the folder, marked dropped.
- **What SCAN APPLY reads.** The merged mesh (each surface once), every projector at the model's raster, the first view's camera
  (the rig's solved optics there), and the planar regions as plane suggestions.
- **Regions.** Per projector on its own raster, joined across projectors; STATE lists each with `projector` (whose smart scan places
  it) and `projectors` (all that light it). `SCAN SURFACE … REGION <region> ON <projector>` keeps the merged mesh's triangles of
  the region that projector lights (their centre on a cell of its part whose own depth agrees). A single-view scan's regions list
  the solved projectors whose picture holds a fifth of their points.
- **On disk.** `scans/<id>/views/<view>/` (view.json and one `.pmcm` decode per projector), `multiview.json`, `depth/<projector>.bin`.
  After a restart the scan reads back whole: INTO, SOLVE, DROP, SURFACE, SUGGEST and APPLY work on it as before.

Measured through the engine (simulator, `tests/PlayMap.Engine.Tests/Phone/MultiViewScanTests.cs`): the same scene, priors and three
views as Core's, each view a `SCAN START … INTO` — every projector within 5 mm, 0.05° and 0.2 % of throw after registering the track
points to the scene (as Core's bar is measured), the phone's focal length within 0.5 %, residual under 0.5 px, each projector seen
over 90 %. A fourth view taken with the phone knocked 2 px between its projectors' runs raises the residual tenfold and is named;
dropping it brings it back under half a pixel.

## Next

Metric scale (a tape between two points, a phone's depth), TSDF or Poisson meshing of the per-projector depth maps, two phones
in one view, and solving projector lens distortion in the joint adjustment.
