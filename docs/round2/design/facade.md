# Work package "facade": survey import, georeference, long-throw solves, night scans

*Design for round 2, 13 October 2026. The research behind it is `r2/research/facade.md` (cited below as R§n). The
long-throw numbers come from `r2/bench/facade/longthrow_table.py`, run in this container. The code read is `main` at
c12b186.*

This package takes a building from a survey to calibrated projectors at 50–150 m: a site frame with a double-precision origin,
survey and control-point import, solves that know what pins cannot tell them, night scans that survive streetlights and
traffic, glass masks, brightness planning, and projector stacks. It implements ROADMAP M23 and UX §5.6.

---

## 1. Goal and the bar

| What the best do (R§) | PlayMap's answer | Match or beat |
|---|---|---|
| disguise asks for a laser scan (±5 mm) as an OBJ and calibrates with 10–15 QuickCal points per projector. 6+ points free lens shift and throw ratio; 4 points take them from settings. QuickCal has no lens distortion (R§1) | Survey import reads OBJ/PLY/glTF/LAS/**E57** (and LAZ through LASzip at run time) and rebases in double. Pins can take their 3D point from a **total-station control point** instead of a mesh pick. Same 4/6-point rules, plus priors and distortion | **Beat**: control points and E57 come in natively. A pin is exactly where the surveyor shot it, not where a mouse landed on a decimated mesh |
| CloudCompare's Global Shift: warns above ~10⁵ and keeps the shift as metadata (R§3) | A project-level **site frame** (CRS words, grid origin, rotation, grid scale, mains Hz). It is set from the first georeferenced import, shown on the desk and kept for export. Readers subtract the origin in double before narrowing to float | **Match**, plus one frame for every import, control point and projector |
| OmniCal: score < 1 px is good. It takes a Black Level capture and says a flat scene needs the Homography model or a temporary object for depth (R§1) | Each solve reports **uncertainty in wall pixels and metres along the axis**, from the solve's own information matrix. When pins cannot separate zoom from distance, the solve locks focal from the lens prior and says so | **Beat**: disguise tells you the scene is flat; PlayMap shows what that costs on the wall (R§7: a flat head-on facade cannot be solved for zoom; with 2 m of relief the projector is ±3.7 m along its axis while the picture is within 1.5 px) |
| Jazé: 2D autocal fails on relief and under ambient light; a 3D model 20 cm off "calibrates confidently to the wrong surface" (R§1) | **CONTROL CHECK** measures the model against control points. The night scan triangulates and shows where the model deviates (σz ≈ 2.3 cm at 100 m with a 10 m baseline, R§6). Bridged dense pairs fix the solve | **Beat**: the model is checked before anyone trusts it |
| Scalable DATALAND: stack members warped to one measured target, colour matched, mirrors masked out of both the solve and the output, one-click recalibration (R§1, R§8) | **Stacks**: one projector for the blend. Each member gets a measured residual lattice fitted from the shared camera's dense decodes (target ≤ 0.25 px) and RGB gains from the same frames. Glass and mirror masks are excluded from the solve and the output | **Match** on a building at night, with commodity cameras |
| Epson stacks up to 6 projectors with its built-in camera, but only in a dark room (≤40 lux) on a matte white flat screen (R§6). Panasonic ET-CUK10 stacks 6 per camera on screens and is discontinued Q2 2026. Christie Mystique double-stacks 2 × 3 on flat screens (R§8) | Stacking on a facade: relief, long throw, ambient light | **Beat**: the rental-truck tools do not do facades |
| No vendor documents what happens when one member of a stack fails (R§8) | **STACK FAILOVER HOLD\|MATCH**. Hold keeps the rest as they are. Match dims every output in the stack's blend group to the stack's remaining fraction, so the building stays even. The trigger is a measured device or output fault; the action is journalled and reversed with a fade | **Beat**: the outcome is defined |
| Battersea: two 4K PTZ cameras zoomed on stack edges to check alignment from a distance (R§1) | A PTZ, IP or tethered camera is the facade scan camera: a Stream camera, or a tethered folder (the dependency below). A night profile averages frames, keeps exposures flicker-safe and re-shoots what headlights spoiled | **Beat**: the inspection camera becomes the calibration camera |
| Jazé, Gilmour, HeavyM plan in lux = lumens ÷ m², with derates and lux targets (R§5) | **PLAN LIGHT** uses the planner's own pixel density per sample: E = derate·Φ·density²/(W·H), summed over stacks and blends. Albedo gives nits; ambient gives contrast. **Measured** contrast comes from the scan: white ÷ shutter-closed, the same for any albedo | **Beat**: per-point, per-stack, and checked against a measurement |

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **Site frame and rebasing.** `Stage.Site`; readers take `GeometryReadOptions` and subtract in double | An OBJ whose vertices sit at E 512 345.123 / N 5 401 234.567 imports with every vertex within **1 mm** of the double-precision truth after rebasing. The same file imported raw is off by up to 0.25 m today, and a test documents that. `SITE` answers the origin and CRS; `SITE ORIGIN` moves the whole stage in one undoable edit, and every projector's reprojection of every pin is unchanged within 1e-6 px |
| M2 | **Geodesy**: geodetic↔ECEF↔local, UTM (Krüger series), EPSG UTM codes, CRS sniffing from WKT, LAS 1.4 VLR and GeoKeys | UTM forward then inverse round trip ≤ 1 mm over the zone. Agrees with ≥ 3 published sample points (taken from the EPSG guidance note while implementing) ≤ 1 mm. A LAS 1.4 file with a WKT VLR reports `EPSG:25832` |
| M3 | **SURVEY IMPORT** off the control lane with progress. Applied as one edit: a mesh asset (a rebased binary PLY in `meshes/`), a `SurveySource` with provenance, and a thinned cloud kept for picking | A 2 M-triangle OBJ imports in ≤ 20 s and a 10 M-point LAS in ≤ 25 s on this 4-core container (benchmarked). Peak managed memory stays ≤ 2 GB for 50 M LAS points with `VOXEL 0.01`. The control lane answers `PING` within 50 ms throughout. UNDO removes the whole import |
| M4 | **E57 reader** (managed): 1024-byte CRC-32C pages, XML (no DTD), Data3D with Float/ScaledInteger fields bit-packed, per-scan pose, row/column indices. A structured scan meshes per station through `GridMesher` | A test writer produces E57 files: two stations, ScaledInteger and Float, with colour. They read back point for point within the scale. One flipped byte fails the page CRC with the page number in the words |
| M5 | **Decimation** (quadric edge collapse) on import, bounded by a triangle count and a deviation | 2 M → 300 k triangles in ≤ 15 s. Hausdorff deviation ≤ the `DecimationErrorM` the reply reports. Boundaries and UV seams are not moved |
| M6 | **Control points**: `CONTROL IMPORT` (PENZD, PNEZD, ENH, NEH, header sniffing); `CONTROL CHECK`; a pin's `ControlId` | A PNEZD file with 14 points imports 14 control points with grid coordinates exact to the file's decimals. CONTROL CHECK on a model shifted by 0.2 m names every point with "20 cm" and a red light. A pin bound to a control point moves with SITE ORIGIN |
| M7 | **CAMERA SOLVE** from camera pins against the model or control points. Intrinsics come from EXIF where present | A simulated 24 MP camera at 95 m with 8 pins solves to ≤ 0.5 px RMS and is marked calibrated. A JPEG with EXIF FocalLength and FocalPlaneXResolution seeds fx within 1 % |
| M8 | **Long-throw solves**: focal and position priors as residual rows; automatic focal lock when the data cannot carry it; `SolveUncertainty` | A seeded Monte Carlo (Pcg32, 200 trials) reproduces the bench table: 100 m, TR 3, 12 pins, 2 m relief, σ 1 px gives a focal std within ±25 % of 3.7 %. A flat head-on case with a 2 % prior locks focal and the words say "zoom locked from the lens: the pins cannot tell zoom from distance". With priors off, every existing `PoseSolver` test gives bit-identical results |
| M9 | **Night profile** for SCAN START: N frames averaged per pattern, flicker-safe exposure plan, projector-black references every K patterns, transient detection and re-shoot, gray16 frames persisted | Simulator with 100 Hz ambient flicker at 30 % and 3 simulated headlight sweeps: decoded-valid fraction ≥ 95 % of the no-disturbance run, and ≤ 2 % of pixels differ by > 1 projector px. Without the profile: ≥ 10 % wrong. The scan folder holds the averaged frames, and a node restart can still apply the scan (carry-over item) |
| M10 | **Photometry**: white, projector-black and shutter-closed maps give contrast against ambient and black lift per pixel and per surface | A simulator with 15 lux ambient and 48 lux projected: measured contrast p50 within ±5 % of (48 + 15)/15 = 4.2, at any albedo from 0.2 to 0.8 |
| M11 | **PLAN LIGHT** (predicted and measured) | One 30 000 lm 4K projector, TR 3, 100 m, head-on, derate 1: E p50 = 48 lux ± 2 %. A double stack gives 96 ± 2 %. Pixel pitch is 8.7 mm ± 1 %. Contrast with AMBIENT 15 is 4.2. Answers in ≤ 3 s for 200 k samples × 12 projectors |
| M12 | **MASK GLASS** from scan evidence and model materials, written as Subtract masks in surface UV. Glass pixels are removed from the bridged solve, which then re-solves | Sim facade with 12 glass windows: mask IoU ≥ 0.8 per window, no false mask over 0.05 m² on stone of albedo ≥ 0.15. The projector RMS after excluding glass is ≤ the RMS before |
| M13 | **Bridged into the stage as placed** (carry-over fix): the scan bridges into `StageScene` (world, transforms applied), not the first mesh file's raw coordinates | A surface placed with a 3 m offset bridges to the same projector pose as the same mesh baked at the offset (≤ 1e-6 m) |
| M14 | **STATE, context, MCP, WIRE.md, desk inspector cards** for all of the above | The verb catalogue tests cover the new verbs (syntax in HELP, risks, SecretFrom null). STATE keys are asserted by field |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Stacks**: `StackGroup`, blend collapse, `STACK ALIGN` residual lattice, `STACK MATCH` RGB gains | Two simulated projectors offset by 0.8 px with a 0.3 % zoom difference align to a held-out residual ≤ 0.25 px (master pixels). The stack's blend weights equal the single-projector weights for every member (light adds). RGB gains ≤ 1 and white points matched within Δuv 0.003 on a colour sim |
| S2 | **STACK FAILOVER** with a runtime degrade gain | A fake device reports light-source error on a member of a 3-stack under MATCH. The other outputs of the blend group read gain 0.667 within one frame of the state change; the journal names the cause; it fades back over 2 s when the member returns |
| S3 | **LAZ** via LASzip loaded at run time | Compile-only here, against a fake `ILaszip`. A missing library refuses with words naming PLAYMAP_LASZIP_DIR |
| S4 | **Light-plan heat map** in the editor VIEW (`plan:lux\|nits\|contrast\|pitch`) | Readback test on llvmpipe: colour ramp at known samples |
| S5 | **SCAN AMBIENT** preflight (shutter-closed frames over a few seconds): ambient level, flicker frequency, transient rate | Sim flicker at 100 Hz is reported as "100 Hz", and the exposure advice is a multiple of 50 ms with a 60 Hz projector on 50 Hz mains |
| S6 | Glass from model materials (OBJ `usemtl`, glTF `KHR_materials_transmission`/BLEND) | An OBJ with `usemtl Glass` faces proposes masks with no scan at all |

### Later

PROJ (MIT) at run time for arbitrary CRS transforms; E57 writing and exporting back to grid coordinates; Poisson meshing of
unorganised clouds; sway measurement and live compensation (Christie Guardian-like, ties to drift watch M2); a camera-exposure
seam (UVC, VISCA-over-IP, Panasonic AW) so night exposures are set rather than advised; vision-assisted window segmentation;
per-surface measured albedo for colour compensation; stack members on different nodes (needs the framelock package).

---

## 3. Architecture

### 3.1 The site frame

- Stage frame (unchanged): metres, right-handed, +Y up. Stage +X is grid east turned by `RotationDeg` about +Y; +Z is grid
  south (the `MeshIO.ZUpToYUp` convention: (E, N, H) ↦ (E, H, −N)).
- `SiteFrame.ToStage(grid) = Ry(rot)·(E−E0, H−H0, −(N−N0))`; `ToGrid` is the inverse. Both are in double. The origin is the
  first georeferenced import's bounding-box centre, rounded to 10 m (so it can be read out loud), unless `SITE ORIGIN` set it.
- Float precision after rebasing: 6.1e-5 m spacing at 1 km. A site wider than 2 km is refused with words ("split the site").
- Curvature (0.8 mm at 100 m) is ignored, and the papers say why (R§3).
- **Grid scale** is the UTM point scale × the elevation factor at the origin. It is stored and shown. Rebased coordinates stay
  grid metres. When `CONTROL CHECK` or a tape-measured pin pair disagrees with the model by more than `|k−1| + 100 ppm`, the
  words name the scale ("the model is in grid metres, k = 0.99962; the tape says ground metres").
- **What gets converted.** Projected metric coordinates in the site's CRS: rebased only. Geographic coordinates (EPSG:4326,
  4258, 4979): geodetic → ECEF → the site's frame. With a UTM site, they first go through UTM forward of the site's zone; with a
  LOCAL site, they go through ENU at the origin's latitude and longitude. A UTM file of another zone, or of the same zone on another
  datum family: converted through geodetic for UTM↔UTM, with a datum warning ("ETRS89 and WGS84 differ by ~0.8 m today;
  treated as one"). Anything else: refused with the CRS words and "reproject in your survey software or use the site's CRS" (PROJ
  is a later item). Mixing a file in an unknown CRS into a georeferenced site needs `CRS LOCAL` plus a three-point
  registration (the existing Umeyama + ICP), and the words say so.

### 3.2 Survey import pipeline (`SURVEY IMPORT`)

```
control lane: validate args → Store.Apply("survey <name>: importing", add SurveySource{state importing}) → accepted {survey, job}
pool thread (SurveyImportJob, one at a time per node, CancellationToken from the lane):
  open reader by extension → IPointChunkSource (LAS, E57, XYZ/PTS/CSV, LAZ via LASzip) or mesh reader (OBJ, PLY, glTF/GLB)
  hash the original (SHA-256, streamed, in parallel with parsing)
  CRS: from the argument, else the file (LAS VLR 2112 WKT / GeoKeys 34735 ProjectedCSTypeGeoKey 3072; E57 coordinateMetadata), else LOCAL
  site: if Site unset and CRS known → origin = rounded bbox centre (first pass: header bounds; E57/OBJ: streamed min/max)
  points: chunks of 1 M → (units, up-axis, CRS conversion, rebase) in double → VoxelThinner (hash of (ix,iy,iz), centroid + mean colour)
          E57 structured scans keep row/column → organised PointCloud per station → GridMesher (existing)
  meshes: reader with GeometryReadOptions (subtract in double before narrowing) → MeshData
          → MeshDecimator (QEM) to DECIMATE triangles (default 400 k) with max deviation reported
  write meshes/<sv>-<name>.ply (binary, temp + move) and clouds/<sv>.ply (thinned, for picking and CONTROL CHECK)
  progress → SurveyImportJob.Progress (STATE.stage.surveys[].state "importing 42 %")
control lane (_lane.Post): one edit "survey <name>": MeshAsset(Imported), a hidden-free Surface over all faces, SurveySource ready,
  Site (when it was unset) — or the SurveySource marked failed with words
```

- **E57 reader** (`MeshIO.E57.cs`). Header (48 bytes): "ASTM-E57", major, minor, filePhysicalLength, xmlPhysicalOffset,
  xmlLogicalLength, pageSize = 1024. Logical reads strip the 4-byte CRC-32C of every 1024-byte page and check it
  (`BitOperations.Crc32C`, compared byte-swapped as libE57Format does, R§4). The XML is read with `XmlReader` (DtdProcessing.Prohibit,
  XmlResolver null). It holds `/data3D` children (`pose` = rotation quaternion w,x,y,z + translation; `points` = CompressedVector
  with `prototype` and `codecs`), cartesian or spherical fields, `cartesianInvalidState`, colour, intensity, `rowIndex` and
  `columnIndex`. The binary section header and data packets (packetType 1, a byte-stream count, buffer lengths) carry one bit-packed
  stream per field. ScaledInteger values are `raw·scale + offset` at the declared bit width. Each scan is moved into the file frame by
  its pose. `images2D` and their DIST distortion extension are not read (later). libE57Format (BSL-1.0) is the reference
  implementation for checking edge cases; no code is copied without its notice.
- **LAS** (existing `ReadLas`) is refactored into a chunked `LasChunkSource`, so 50 M points stream without a 1.2 GB `Vec3d[]`. It
  also reads VLRs for the CRS.
- **LAZ**: `ILaszip` seam implemented by `LaszipNative`. It P/Invokes `laszip_create`, `laszip_open_reader`, `laszip_get_header_pointer`,
  `laszip_get_point_pointer`, `laszip_read_point`, `laszip_close_reader` and `laszip_destroy` (R§4), loaded with
  `NativeLibrary.TryLoad` from `PLAYMAP_LASZIP_DIR`, then the OS path (`laszip3.dll`, `liblaszip.so.8`, `liblaszip.dylib`).
- **Decimation** (`MeshDecimator`): Garland–Heckbert quadrics. A binary heap of edge costs; boundary edges get a perpendicular
  penalty plane; a vertex whose UV differs across a shared position counts as a boundary; a collapse that flips a face normal by
  more than 60° is refused; the cost stops at `maxErrorM` (default 5 mm, or 0.5 × the projector pixel pitch from the planner when
  projectors exist). It reports the largest quadric error as the deviation bound.
- **Readers keep materials** (S6): `GeometryFile.FaceMaterials` (int per triangle) and `MaterialNames`, from OBJ `usemtl` and glTF
  primitives. They feed the glass prior.

### 3.3 Control points

- `ControlPointFile.Parse(TextReader, order, units)`: comma, semicolon, tab or whitespace separated. Order PENZD (P, E, N, Z,
  description), PNEZD (the common US export), ENH or NEH. The order is guessed from a header ("Easting", "Northing", "Elev", "Code")
  or the argument. Culture-invariant numbers. Duplicate names are refused with their line numbers.
- **CONTROL CHECK**: each enabled point → its stage position → nearest distance to the stage (`StageScene` ray-casting along the
  normal, and the nearest point of the survey's thinned cloud through `KdTree3`). Lights: green ≤ max(2σ, 2 cm), amber ≤ 5 cm,
  red beyond. An overall similarity (Umeyama on point ↔ nearest-surface pairs, scale free) says whether the model is shifted,
  rotated or scaled ("the model is 0.4 % small: grid vs ground scale?").
- **Pins on control points**: `CalibrationPin.ControlId`. The pin's `World` is derived from the control point whenever the site or
  the point changes (the edit that changes them updates the pins in the same edit). On the desk a control point is a crosshair in
  the 3D view; dragging a pin's projector pixel onto the real feature is all the operator does.

### 3.4 Camera solve (the facade's scan camera)

- `CAMERA SOLVE <camera>`: `PoseSolver.Solve(camera pins, rig.Intrinsics, rig.Lens, { Locks = Shift | Distortion | (FOCAL ?
  Focal) })`. Pins are `CameraRig.Pins`: camera pixel ↔ control point or model point. On success (≥ 6 pins, or ≥ 4 with focal
  locked, and RMS ≤ 1.5 px) the rig takes the pose and optics and becomes `Calibrated`. Bridged scans need exactly that
  (`ScanSolver` bridges when `camera.Calibrated` and a model exists).
- **EXIF prior** (`ExifIntrinsics`): JPEG APP1 / TIFF IFD0 + ExifIFD tags FocalLength (0x920A), FocalPlaneXResolution (0xA20E),
  FocalPlaneResolutionUnit (0xA210; 2 inch, 3 cm), FocalLengthIn35mmFilm (0xA405), PixelXDimension (0xA002). fx = f_mm ×
  px-per-mm, else f35/36 × width. Cameras are assumed square-pixel with a centred principal point; distortion is k1 = 0 unless a
  lens profile is given.
- Pixel scale check: at 100 m a 50 mm APS-C camera sees ~7.8 mm per pixel against a 4K TR 3 projector's 8.7 mm (R§6). The scan
  setup warns when camera mm/px > 1.5 × projector mm/px ("this camera cannot see single projector pixels: zoom in or use Robust").

### 3.5 Long-throw solves

**Priors as residual rows** (`PoseSolveOptions.Priors`, consumed in `Refine` only, so closed forms and RANSAC are unchanged):

```
r_f   = σ_px · (f − f₀) / σ_f                      (1 row; f₀ = throwRatio·W, σ_f = f₀·pct/100)
r_C   = σ_px · (C − C₀) / σ_C                      (3 rows; C₀ from a control point or x y z)
σ_px  = 1.0 for hand pins, 0.3 for dense pairs (the noise the residual rows are scaled against)
```

The Jacobian rows are constant (∂r_f/∂f = σ_px/σ_f; ∂r_C/∂C = σ_px/σ_C·I). With no priors there are no extra rows and the
results are bit-identical.

**Automatic lock.** Take the normalised information matrix Ĩ = D^−½ JᵀJ D^−½ at the closed-form start. Read the eigenvector of its
smallest eigenvalue. If its focal and along-axis-centre components together exceed 0.7 and the implied σ_f/f > 10 %:

- with a focal prior → lock focal at f₀ (as `SolveLocks.Focal`) and say "zoom locked from the lens: the pins cannot tell zoom
  from distance";
- without one → solve, set `Uncertainty.Weak = "zoom↔distance"`, light amber, and offer "set the lens (PROJECTOR PRIOR … THROW)
  or add pins at different depths or a night scan".

**Uncertainty** (`SolveUncertaintyAnalysis.Of(solution, pairs, priors, scene)`):

```
σ̂     = max(rms_inliers·√(m/(m−p)), floor)         floor 0.5 px pins, 0.05 px dense
Σ     = σ̂² (JᵀJ + Λ_prior)⁻¹                        J unscaled, at the solution; Λ_prior the prior information
focal %   = √Σ_ff / f·100
axis m    = √(aᵀ Σ_CC a), a = optical axis;  lateral m = √(tr Σ_CC − axis²)
wall px   = p95 over the projector's visible stage samples x of √λmax(J_x Σ J_xᵀ)
proud px  = the same with x moved toward the projector by max(1 m, 2 % of distance)
condition = cond(Ĩ); weak = words from the two weakest eigenvectors ("zoom↔distance", "sideways↔turn", "shift↔turn")
```

Visible samples come from `CoverageAnalysis` (reach bit q), at most 2 000 per projector; the analysis takes < 10 ms. The reply,
STATE and the desk lead with **wall px**: it is what the audience sees. Metres come second, for blends and brightness. This is
what the explainable score (the calibration-score package) reads.

**Recommended facade path**, in the order the desk offers it: lens prior from the datasheet or learned lens readback
(`LensCalibration` in Devices) → 8–15 pins on control points or model corners → solve (wall px shown) → **night scan bridged into
the model** for dense pairs (σ focal 0.03 %, wall 0.01 px, R§7) → glass masks → re-solve.

### 3.6 Night capture profile (`SCAN START … PROFILE NIGHT`)

Pattern-vs-inverse comparison already cancels steady ambient (R§6). The profile handles the four things that break at night:

| Problem | Mechanism | Numbers |
|---|---|---|
| Low signal | `FrameAverager`: after the settle detector accepts a pattern, take N consecutive frames (`FRAMES`, default 4; the folder camera takes one long exposure) and accumulate into ushort sums (×256/N scaled gray16) | Noise ÷ √N. The decode uses a new `Decode(w, h, IReadOnlyList<ushort[]>)` overload (gray16 linearised through `CameraGamma`) |
| Mains flicker | `ExposurePlan.FlickerSafe(displayHz, mainsHz, wantedMs)` = the smallest k·LCM(1000/displayHz, 500/mainsHz) ≥ wantedMs. The plan is shown as advice (camera exposure control is a later seam), written into the folder plan for tethered cameras, and checked: the shutter-closed frames' mean level oscillation > 3 % p–p is reported as flicker with its frequency (from frame timestamps) | 60 Hz projector on 50 Hz mains → multiples of 50 ms; on 60 Hz mains → multiples of 16.7 ms |
| Transients (headlights, people with torches) | Every K patterns (`EVERY`, default 8) the plan's projector-black frame is shown again as a reference. `TransientDetector`: a pixel whose reference changed by > 6σ (σ from the averager's variance) between two references is marked transient for the K patterns between them. If more than 2 % of lit pixels in an interval are transient, those K patterns are shown again (at most 2 retries, then the pixels are dropped with words) | ≤ 2 % wrong pixels with 3 sweeps (M9) |
| Resolution and distance | Stream camera (PTZ/IP, 4K) or tethered folder camera. The lit ROI of each projector (from its first white and black) is cropped before storage | Memory: 56 frames × 12 MP × 2 B = 1.35 GB at most; above 12 MP the ROI crop is required and the words say so |

- **Shutter-closed frames** (S): one set at the start and one at the end of the night scan, all scanned projectors closed (the
  device link's `BlackFrameAsync`, already in `ScanRunner.PrepareDevicesAsync`), each the average of N frames. Start vs end gives
  ambient drift ("ambient rose 18 % during the scan: a shop light came on?").
- **Persistence** (carry-over): averaged frames go to `<project>/scans/<sc>/frames/NNN.g16` (a 16-byte header — magic, width,
  height, scale — then little-endian ushort samples deflated with `ZLibStream`; no new dependency), with `night.json` (plan,
  exposure advice, transients, reshoots) and the decoded maps.
  `ScanArchive` loads decoded maps after a restart, so `SCAN APPLY` and `MASK GLASS` work on yesterday night's scan.
- Expected durations (estimate; measured in M9 on the simulator only): 56 patterns × (4 × 50 ms + 300 ms settle) + 7 references
  ≈ 32 s per projector on a 4K stream camera; with a tethered DSLR at ~1.5 s per photo, ~2 min per projector. The ETA dial shows it.

### 3.7 Photometry and brightness planning

**Measured** (`Photometry.Maps(white, projBlack, shutterClosed, gamma)`), per camera pixel, linear light, unsaturated pixels only:

```
contrast vs ambient  C = W / S          (the projector's white plus ambient over ambient — albedo and camera gain cancel)
black lift           B̃ = B / S          (projector black leakage plus ambient over ambient)
saturated            W ≥ SaturationLevel (then reported, never guessed; HDR is later)
```

Camera pixels are ray-cast into the stage (`StageScene`) and gathered per surface: p5, p50 and p95 of C and B̃.

**Predicted** (`LightPlan.Analyse(scene, stage, coverage, blend?, options)`), per coverage sample x:

```
E_i(x)  = derate · Φ_i · density_i(x)² / (W_i·H_i) · U_i(x)       lux; density = projector px per metre (CoverageAnalysis)
U_i(x)  = 1 − (1 − u)·r²   r = normalised radius in the raster (0 centre, 1 corner), u = uniformity (default 0.85)
E(x)    = Σ_i w_i(x)·E_i(x)   w = the solved blend's light weight (stack members carry the group's weight, so they add); 1 if unsolved
L(x)    = ρ·E(x)/π  cd/m²;   C(x) = (E(x) + E_a)/E_a;   pitch(x) = 1000/density_best(x) mm
deficit = lumens to lift E_p5 to TARGET = (TARGET − E_p5)/E_p5 · Σ Φ of the projectors reaching the p5 area
```

Defaults: derate 0.7 (Jazé's 0.60–0.70, R§5), ρ 0.5 unless the surface is tagged `albedo:<v>`, and AMBIENT from the argument or
the last SCAN AMBIENT. TARGET defaults follow Jazé's outdoor table: 100 lux minimum and 180 comfortable in town, 60 and 100 when
`ENV RURAL`. The words give one suggestion: "Stack S2 reaches 61 lux p5; a third 30 000 lm projector gives 92."

### 3.8 Glass masks (`MASK GLASS`)

Evidence per camera pixel c of a bridged scan (camera pose known, projector solved). Weights are design values; the tests tune
them on the simulator:

| Evidence | Test | Weight |
|---|---|---|
| Low return | (W−B)(c) < 0.3 × the 15 × 15 median over the same surface | 0.45 |
| Geometric ghost | the decoded projector pixel disagrees with the model's prediction π_p(X(c)) by > max(3 px, 4σ̂), and the pair was an outlier of the solve (mirror ghosts, R§1 Scalable) | 0.35 |
| Specular hot spot | S(c) > 3 × its neighbourhood median (a reflected streetlight), or a saturated blob < 0.2 % of the area | 0.20 |
| Model material | the hit face's material matches `glass|window|glazing|verre|vitr|fenster|vidrio`, or glTF transmission > 0 or alpha BLEND | prior 0.6 |

Pipeline: probabilities splat into per-surface UV rasters (2 cm texels, at most 2048²) → threshold 0.5 → close 5 cm, open 2 cm
→ drop components < 0.05 m² → grow by `GROW` (default 5 cm: no light into rooms) → `MaskToPath.Vectorise` (Vision, called by the
engine) → `Mask("Glass n", path, Subtract, feather = 2 cm in UV units, …)`. Masks live in surface UV, so one mask serves every
projector and stack member, and a rescan keeps it. The glass pixels are then removed from the bridged pairs and the projector is
re-solved; the reply gives RMS before and after. Per projector: the smart scan, regions and the glass evidence are kept per
projector (carry-over: "first projector only").

### 3.9 Stacks (should)

- **Model**: `StackGroup(Id, Name, ProjectorIds, MasterId, Failover)`. `STACK NEW` refuses a member that reaches < 80 % of the
  master's coverage area ("a stack shares one area — use a blend"). It warns when members are driven by outputs of different
  refresh rates or on different nodes: stacked moving images need the same frame (the framelock package).
- **Blend** (`BlendSolver3D`, hunk + `StackBlend`): in each map pixel's projector set, the members of a stack collapse to the
  group. The group's weight uses the master's geometry terms, the minimum edge distance over its members, and the members' summed
  lumens. Each member's map gets the group's light weight, so stacked light adds and the stack blends with its neighbours as one
  projector. `BlendInputs` includes the stacks, so editing a stack re-solves.
- **Alignment** (`StackAlignment.Fit`): needs one scan holding ≥ 2 members through one camera.
  - Target **MODEL** (bridged camera): X(c) = camera ray ∩ stage.
  - Target **MASTER** (no model): X(c) = master ray through s_m(c) ∩ stage.
  - For each member j, sample pairs u = π_j(X(c))/raster → v = s_j(c)/raster: where the render puts X, and where the light that
    reaches c really leaves.
  - Fit a Catmull-Rom `WarpLattice` (default 33 × 19; max 65) by linear least squares in the node offsets (the basis is linear),
    with Laplacian smoothing λ and Huber IRLS (δ 0.5 px). Solved by Jacobi-preconditioned conjugate gradients, x and y
    separately, ≤ 200 iterations: < 1 s for 100 k pairs.
  - The master is fitted too (MODEL target), so every member agrees with the same measured target (R§8 Scalable).
  - Residual: RMS on 10 % held-out pairs, in master pixels; target ≤ 0.25 px.
  - Written to `Output.Measured`, which the output stage applies **before** the operator's corner pin and lattice
    (`WarpField` gains a first layer).
- **Colour** (`STACK MATCH`): from a colour camera's R, G, B and W frames per member (four extra plan frames), each member's white
  chromaticity and level over the shared area. Per-channel gains ≤ 1 bring each member to the master's white point. Level is left
  alone: light adds, and the sum's uniformity is what blend compensation already handles. Written to `Output.Measured.Gain*`.
- **Failover** (`StackFailover.Hold|Match`). Degraded is a measured fact: `DeviceService` state (`Services.Devices`: power standby, light-source or
  lamp error, shutter closed not by PlayMap), or `OutputHealth.Open == false`, for a member.
  - Hold: STATE and context go red; nothing else changes.
  - Match: the engine computes f = Σ remaining lumens ÷ Σ lumens for the stack. It sets a runtime degrade gain f on every other
    output in the stack's blend group (outputs whose blend maps overlap, transitively), as render runtime state like BLACKOUT,
    not a project edit and not in undo. The gain fades in 0.5 s and out 2 s after the member returns. It is journalled with its
    cause.
  - A member coming back is a fact too.

### 3.10 Threads and lanes

| Work | Where | Applies as |
|---|---|---|
| SURVEY IMPORT parse/thin/decimate/write | pool thread, one job at a time, cancellable | one edit on the control lane |
| CONTROL IMPORT, CONTROL CHECK | control lane (small); CHECK builds `StageScene` once and caches it by stage revision | one edit / query |
| PROJECTOR SOLVE + uncertainty | control lane (≤ 50 ms for 15 pins; dense pairs go through the scan path) | one edit |
| Night capture | `ScanRunner`'s task, as today | SCAN APPLY |
| MASK GLASS, STACK ALIGN, PLAN LIGHT | pool thread with progress; the reply waits (≤ 3 s typical) or answers accepted when > 2 s and posts the result | one edit (masks, measured warps) / query |
| Failover gain | engine reacts to device and output state changes on the control lane; the render thread reads the runtime gain snapshot | runtime state |

### 3.11 Performance budgets (this container, 4 cores)

| Operation | Budget |
|---|---|
| OBJ 2 M triangles import (parse + rebase + decimate to 400 k + write) | ≤ 20 s |
| LAS 10 M points (stream + voxel 1 cm + write) | ≤ 25 s; 50 M ≤ 2 GB peak |
| E57 10 M points, 2 stations, CRC on | ≤ 30 s |
| QEM 2 M → 300 k | ≤ 15 s |
| PROJECTOR SOLVE with uncertainty, 15 pins | ≤ 50 ms |
| PLAN LIGHT, 200 k samples × 12 projectors | ≤ 3 s |
| MASK GLASS, 12 MP camera, 4 surfaces | ≤ 5 s |
| STACK ALIGN, 2 members, 100 k pairs, 33 × 19 | ≤ 2 s |
| Night scan memory, 12 MP camera | ≤ 1.5 GB of frames |

---

## 4. Spine needs

### 4.1 Model (new file `src/PlayMap.Core/Model/SiteModel.cs`; all additions are init-only with defaults, so older files read unchanged)

```csharp
public sealed record SiteFrame(
    string Crs,          // json "crs": "EPSG:25832" | "LOCAL" | "" (not set)
    string CrsName,      // "crsName": "ETRS89 / UTM zone 32N"
    Vec3d Origin,        // "origin": grid E, N, H (metres, double)
    double RotationDeg,  // "rotationDeg": stage +X from grid east, anticlockwise from above
    double GridScale,    // "gridScale": projection × elevation scale at the origin; 1 = unknown
    int MainsHz)         // "mainsHz": 50 | 60 | 0
{
    public static readonly SiteFrame None = new("", "", Vec3d.Zero, 0, 1, 0);
    public bool IsSet => Crs.Length > 0;
    public Vec3d ToStage(Vec3d grid);
    public Vec3d ToGrid(Vec3d stage);
}

public sealed record ControlPoint(string Id, string Name, Vec3d Grid, double SigmaM, string Code, bool Enabled) : IEntity; // "cp"

public enum SurveyFormat { Obj, Ply, Gltf, Las, Laz, E57, Xyz, Pts, Csv }
public enum SurveyState { Importing, Ready, Failed }

public sealed record SurveySource(string Id, string Name, SurveyFormat Format, string SourcePath, string SourceHash, string Crs,
    long SourcePoints, long SourceTriangles, string? MeshId, string CloudPath, double VoxelM, double DecimationErrorM,
    SurveyState State, long ImportedUnixMs, string Notes) : IEntity;                                                  // "sv"

public enum StackFailover { Hold, Match }
public sealed record StackGroup(string Id, string Name, ImmutableArray<string> ProjectorIds, string MasterId, StackFailover Failover) : IEntity; // "sk"

public sealed record SolvePriors(double ThrowRatio, double ThrowRatioSigmaPct, Vec3d? Position, string ControlId, double PositionSigmaM, string Source)
{ public static readonly SolvePriors None = new(0, 0, null, "", 0, ""); }

public sealed record SolveUncertainty(double WallPx, double ProudPx, double FocalPct, double AxisM, double LateralM, double Condition, string Weak);

public sealed record MeasuredCorrection(WarpLattice? Lattice, double GainR, double GainG, double GainB, double ResidualPx,
    string ScanId, string StackId, long MeasuredUnixMs);

public sealed record ScanCaptureInfo(string Profile, int FramesPerPattern, double ExposureAdviceMs, double FlickerHz, int Transients,
    int Reshot, double AmbientDriftPct, string FramesPath);
```

Additions to existing records (init-only properties, defaults in brackets):

| Record | Property (json) | Default |
|---|---|---|
| `Stage` | `Site` (site), `Controls` (controls), `Surveys` (surveys), `Stacks` (stacks) | None, [], [], [] |
| `Projector` | `Priors` (priors), `Uncertainty` (uncertainty) | None, null |
| `CalibrationPin` | `ControlId` (controlId) | "" |
| `CameraRig` | `Pins` (pins) | [] |
| `Output` | `Measured` (measured) | null |
| `ScanRecord` | `Capture` (capture) | null |
| `Ids` | `Control = "cp"`, `Survey = "sv"`, `Stack = "sk"` | — |

`Project.AllIds` includes controls, surveys and stacks. The generic `GET/SET/ADD/REMOVE` cover them (ADD gains
`control|survey|stack`; a survey is added only by SURVEY IMPORT, so `ADD survey` is refused with "use SURVEY IMPORT").

### 4.2 Verbs (`VerbCatalog`, categories Mapping and Scan; no secrets)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| SITE | `SITE` | Low | no | `{crs,crsName,origin:[e,n,h],rotationDeg,gridScale,mainsHz,set}` |
| SITE ORIGIN | `SITE ORIGIN <e> <n> <h> [CRS <code>\|LOCAL] [ROTATE <deg>]` | Review | yes | `{origin,moved:[dx,dy,dz]}`. The whole stage moves in one edit; "would change nothing" is refused |
| SITE MAINS | `SITE MAINS 50\|60` | Low | yes | `{mainsHz}` |
| SURVEY IMPORT | `SURVEY IMPORT <path> [CRS <code>\|LOCAL] [UNITS m\|cm\|mm\|ft\|usft] [UP Z\|Y] [VOXEL <m>] [DECIMATE <triangles>\|OFF] [NAME <name>]` | Review | yes | accepted `{stage:"accepted",survey:"sv…"}`; later the edit, STATE `stage.surveys[].state` |
| SURVEY LIST | `SURVEY LIST` | Low | no | sources with provenance, state, notes |
| CONTROL IMPORT | `CONTROL IMPORT <path> [ORDER PENZD\|PNEZD\|ENH\|NEH] [CRS <code>] [UNITS …]` | Review | yes | `{added,ids:[…],skipped:[{line,why}]}` |
| CONTROL CHECK | `CONTROL CHECK [<surface>\|<survey>]` | Low | no | `{points:[{id,name,distanceM,light}],fit:{shiftM,rotDeg,scalePpm},words}` |
| CAMERA SOLVE | `CAMERA SOLVE <camera> [LOCK FOCAL\|NONE]` | Review | yes | as PROJECTOR SOLVE plus `calibrated` |
| PROJECTOR PRIOR | `PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>] \| AT <control>\|<x> <y> <z> [±<m>] \| CLEAR` | Review | yes | `{priors}` |
| PROJECTOR SOLVE | unchanged grammar | Review | yes | adds `uncertainty:{wallPx,proudPx,focalPct,axisM,lateralM,condition,weak}`, `locked:["focal"]`, `priorsUsed` |
| SCAN START | `… [MODE …] [PROFILE NIGHT [FRAMES <n>] [EVERY <k>]]` | High | yes | as today; STATE.scan.night |
| SCAN AMBIENT | `SCAN AMBIENT <camera> [<projector>…] [SECONDS <s>]` | High (closes shutters) | yes | `{level,flickerHz,transientsPerMin,driftPct,exposureAdviceMs,words}` |
| MASK GLASS | `MASK GLASS <scan> [<surface>\|ALL] [GROW <cm>] [FROM SCAN\|MODEL\|BOTH]` | Review | yes | `{masks:[{id,surface,areaM2,evidence}],resolved:[{projector,rmsBefore,rmsAfter}]}` |
| PLAN LIGHT | `PLAN LIGHT [<surface>\|ALL] [AMBIENT <lux>] [ALBEDO <0-1>] [TARGET <lux>] [DERATE <0-1>] [ENV RURAL\|URBAN] [MEASURED <scan>]` | Low | no | `{surfaces:[{id,areaM2,luxP5,luxP50,luxP95,nitsP50,contrastP5,pitchMmP50,pitchMmP95,stackDepth,coverage,deficitLm}],words}` |
| STACK NEW | `STACK NEW <name> <projector> <projector> [<projector>…] [MASTER <projector>]` | Review | yes | id |
| STACK ALIGN | `STACK ALIGN <stack> [FROM <scan>] [TARGET MODEL\|MASTER] [GRID <c>x<r>]` | Review | yes | `{members:[{projector,residualPx,pairs}],words}` |
| STACK MATCH | `STACK MATCH <stack> [FROM <scan>]` | Review | yes | `{members:[{projector,gain:[r,g,b],duv}]}` |
| STACK FAILOVER | `STACK FAILOVER <stack> HOLD\|MATCH` | Review | yes | `{failover}` |
| STACK LIST | `STACK LIST` | Low | no | stacks with members, aligned, residual, degraded |

The risks follow the catalogue's rule: anything that changes what leaves a lens is High (SCAN START, SCAN AMBIENT). Edits that
re-map, mask or re-solve are Review. Queries are Low. Edits to the air in show mode come under the show-safety package's
blind-edit rule.

### 4.3 STATE and context

```
stage.site       {crs, name, origin:[e,n,h], rotationDeg, gridScale, mainsHz}          (omitted when not set)
stage.controls   <count>
stage.surveys    [{id, name, format, state:"importing 42%"|"ready"|"failed: …", points, triangles, mesh}]
stage.projectors[] + uncertainty {wallPx, axisM, focalPct, weak}, priors "datasheet ±2 %"|"", stack "sk…"|null
stage.stacks     [{id, name, members, master, aligned, residualPx, failover, degraded:[ids], gain}]
scan.night       {framesPerPattern, exposureAdviceMs, flickerHz, transients, reshot, ambientDriftPct}  (during and after a night scan)
```

Context items (Show Bus §6), using the existing lights:

- `projector <name>`: amber when wallPx > 1.5 or weak = "zoom↔distance" ("P3: picture ±1.5 px, distance ±3.7 m — lock the
  zoom or night-scan").
- `stack <name>`: red when degraded ("S2 lost P5 — matched: the building at 67 %"), amber when residualPx > 0.5.
- `site`: amber when a georeferenced import has no site, or CONTROL CHECK is amber or red.
- `survey <name>`: amber while importing, red when failed.

The context's command list gains the new verbs with their risks, so an assistant can propose `PLAN LIGHT` (Low, runs) or
`STACK FAILOVER` (Review, queued).

### 4.4 Seams (C#)

```csharp
// Core/Geometry/PointChunks.cs
public interface IPointChunkSource : IDisposable
{
    long DeclaredCount { get; }           // −1 unknown
    string Crs { get; }                   // "EPSG:…", WKT words, or ""
    (Vec3d Min, Vec3d Max)? DeclaredBounds { get; }
    bool HasColour { get; }
    /// <summary>Fills up to points.Length points in the file's own units and frame; 0 at the end.</summary>
    int Read(Span<Vec3d> points, Span<int> colours, CancellationToken ct);
}

// Core/Geometry/GeometryReadOptions.cs
public sealed record GeometryReadOptions(Vec3d Origin, double UnitScale, bool ZUp, Func<Vec3d, Vec3d>? Convert)
{
    public static readonly GeometryReadOptions AsStored = new(Vec3d.Zero, 1, false, null);
    public Vec3d Apply(Vec3d fileUnits);   // in double: convert → scale → (Z-up → Y-up) → − origin
}

// Engine/Survey/ILaszip.cs — LASzip loaded at run time; null when the library is absent
public interface ILaszip { IPointChunkSource Open(string path); string Version { get; } }

// Core/Calibration (PoseSolveOptions additions)
public sealed record PosePriors(double? Focal, double FocalSigma, Vec3d? Centre, double CentreSigma, double PixelSigma)
{ public static readonly PosePriors None = new(null, 0, null, 0, 1); }
// PoseSolveOptions { PosePriors Priors { get; init; } = PosePriors.None; bool AutoLockFocal { get; init; } }
// PoseSolution { SolveUncertainty? Uncertainty { get; init; } }

// Engine: runtime degrade gain read by the render output stage
public interface IOutputGains { float GainOf(string outputId); }   // 1 when nothing is degraded; lock-free snapshot
```

`IScanCamera` is unchanged. Exposure control is a later seam: `IScanCameraControl { bool SetExposureMs(double); }`.

### 4.5 NodeSettings

None. LASzip is found through the environment variable `PLAYMAP_LASZIP_DIR` (the `PLAYMAP_FFMPEG_DIR` pattern) or the OS search
path. Mains frequency is a property of the venue, so it lives in the project (`Site.MainsHz`).

---

## 5. Files

**New (Core)**

- `src/PlayMap.Core/Model/SiteModel.cs`
- `src/PlayMap.Core/Geometry/Geodesy.cs`: `Ellipsoid.Wgs84/Grs80`, `Ecef`, `LocalTangent`, `TransverseMercator` (Krüger
  n⁶), `Utm`, `CrsInfo` (EPSG UTM families 326xx, 327xx, 258xx, 269xx; geographic 4326, 4258, 4979; WKT `AUTHORITY`/`ID` sniffing)
- `src/PlayMap.Core/Geometry/GeometryReadOptions.cs`
- `src/PlayMap.Core/Geometry/PointChunks.cs`: `IPointChunkSource`, `LasChunkSource`, `XyzChunkSource`, `VoxelThinner`
- `src/PlayMap.Core/Geometry/MeshIO.E57.cs`
- `src/PlayMap.Core/Geometry/MeshDecimator.cs`
- `src/PlayMap.Core/Geometry/ControlPointFile.cs`
- `src/PlayMap.Core/Geometry/ExifIntrinsics.cs`
- `src/PlayMap.Core/Calibration/PosePriors.cs`: the prior rows and the auto-lock test
- `src/PlayMap.Core/Calibration/SolveUncertaintyAnalysis.cs` — created by wp2-wall (W1) with the minimal Σ = σ̂²(JᵀJ)⁻¹; this
  package adds the priors' rows and the axis/lateral breakdown through a hunk (`PLAN.md` §4)
- `src/PlayMap.Core/Scan/NightCapture.cs`: `ExposurePlan`, `FrameAverager`, `TransientDetector`
- `src/PlayMap.Core/Scan/Photometry.cs`
- `src/PlayMap.Core/Scan/SimScenes.Facade.cs`: `LongFacade(…)`, a 40 × 20 m front with 1–2 m pilasters and cornice, 12 recessed
  glass windows, projector at 100 m TR 3, camera at 95 m offset 10 m with a 50 mm-equivalent lens
- `src/PlayMap.Core/Scan/SimDisturbances.cs`: ambient flicker, headlight sweeps and glass (mirror ray) as `SimOptions`
  extensions (records consumed by a small hunk in `ScanSimulator`)
- `src/PlayMap.Core/Mapping/LightPlan.cs`
- `src/PlayMap.Core/Mapping/GlassDetector.cs`
- `src/PlayMap.Core/Mapping/StackBlend.cs`
- `src/PlayMap.Core/Mapping/StackAlignment.cs`: lattice fit by CG, residual, colour gains

**New (Engine)**

- `src/PlayMap.Engine/EngineCore.Facade.cs`: SITE*, SURVEY*, CONTROL*, CAMERA SOLVE, PROJECTOR PRIOR, MASK GLASS, PLAN LIGHT
- `src/PlayMap.Engine/EngineCore.Stacks.cs`: STACK*, the failover policy and `IOutputGains`
- `src/PlayMap.Engine/Survey/SurveyImportJob.cs`
- `src/PlayMap.Engine/Survey/ILaszip.cs`, `src/PlayMap.Engine/Survey/LaszipNative.cs`
- `src/PlayMap.Engine/Scanning/ScanRunner.Night.cs`: the night profile (partial)
- `src/PlayMap.Engine/Scanning/ScanPhotometry.cs`, `src/PlayMap.Engine/Scanning/ScanGlass.cs`
- `src/PlayMap.Engine/StateWriter.Facade.cs`: the stage.site, surveys, stacks and scan.night writers (partial)

**New (App)**

- `src/PlayMap.App/Views/SitePanel.axaml(.cs)` and `src/PlayMap.App/ViewModels/SiteViewModel.cs`: site, surveys, control
  points table
- `src/PlayMap.App/ViewModels/StackViewModel.cs`
- `src/PlayMap.App/Scene/LightPlanLegend.cs`

**New (tests)**

- `tests/PlayMap.Core.Tests/Facade/{Geodesy,Rebase,E57,VoxelThinner,Decimator,ControlPointFile,Exif,LongThrowSolve,NightCapture,Photometry,LightPlan,GlassDetector,StackBlend,StackAlignment}Tests.cs`
- `tests/PlayMap.Core.Tests/Facade/E57TestWriter.cs`: writes minimal valid E57 files for the reader tests
- `tests/PlayMap.Engine.Tests/Facade/{SurveyImportVerb,SiteVerbs,ControlVerbs,CameraSolveVerb,NightScan,MaskGlassVerb,PlanLightVerb,StackVerbs,StackFailover,BridgedTransform,FacadeState}Tests.cs`
- `tests/PlayMap.Render.Tests/MeasuredWarpLayerTests.cs` (GL, Xvfb)

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/StageModel.cs` | init-only properties on `Stage`, `Projector`, `CalibrationPin`, `CameraRig`, `ScanRecord` (~25 lines) |
| `src/PlayMap.Core/Model/OutputModel.cs` | `Output.Measured` init-only (3 lines) |
| `src/PlayMap.Core/Model/Ids.cs`, `Project.cs` | 3 prefixes; `AllIds` loops (8 lines) |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | the verb rows (≈20 lines) |
| `src/PlayMap.Core/Geometry/MeshIO.cs`, `MeshIO.Obj.cs`, `MeshIO.Ply.cs`, `MeshIO.Gltf.cs`, `MeshIO.Points.cs` | `Load(path, GeometryReadOptions)` overloads: apply in double before narrowing; materials out (S6) |
| `src/PlayMap.Core/Calibration/PoseSolver.cs` | options and solution properties; prior rows appended in `Problem` when present; uncertainty after the final refine (≈60 lines) |
| `src/PlayMap.Core/Scan/StructuredLightDecoder.cs` | `Decode(int, int, IReadOnlyList<ushort[]>)` overload and a per-pixel transient mask input (≈50 lines) |
| `src/PlayMap.Core/Scan/ScanSimulator.cs` | consume `SimDisturbances` (≈40 lines) |
| `src/PlayMap.Core/Mapping/BlendSolver3D.cs` | `partial`; a stacks parameter routed to `StackBlend` (≈30 lines) |
| `src/PlayMap.Core/Mapping/WarpField.cs` | measured lattice as the first layer (≈15 lines) |
| `src/PlayMap.Engine/Scanning/ScanRunner.cs` | `partial`; profile switch; `StageModel` → `StageScene` (the M13 fix) |
| `src/PlayMap.Engine/Rendering/BlendInputs.cs` | stacks in the inputs' equality |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` (output stage) | read `IOutputGains`; pass `Output.Measured` to `WarpField` |
| `src/PlayMap.Engine/EngineCore.Router.cs` | dispatch rows |
| `src/PlayMap.Engine/StateWriter.cs` | `partial` + calls into the facade writers |
| `src/PlayMap.Engine/EngineCore.Mapping.cs` | PROJECTOR SOLVE: LOCK words unchanged, priors passed, uncertainty in the reply |
| `src/PlayMap.App/ViewModels/MapInspectors.cs`, `ScanWizardViewModel.cs`, `Views/MapView.axaml` | projector card (priors, uncertainty bar), night profile chips, right-click entries |
| Papers | `docs/WIRE.md` (verbs, STATE), `docs/ARCHITECTURE.md` §6 (site frame, survey import), `docs/UX.md` §5.6 (the real flow), `docs/ROADMAP.md` M23, and a new `docs/MAPPING.md` "Facades" section (CLAUDE.md points to `docs/MAPPING.md`, which does not exist on main) |

---

## 6. Dependencies

**Work packages**

- **phonescan / capture**: the Folder camera (an image set, and the tethered "new file per pattern" variant facades need for
  DSLRs) and Phone. Until it lands, the night profile is tested on the simulator and with a Stream camera (an FFmpeg `lavfi` source).
- **calibration score and history** (R1.2/R1.4/R4.8 package): it consumes `SolveUncertainty` as score terms and keeps solves
  as snapshots with fallback. Facade writes the fields; that package decides how they are kept.
- **show safety / preflight**: SCAN AMBIENT, CONTROL CHECK and stack health are preflight chips. Failover MATCH is one of its
  defined outcomes.
- **framelock / present**: stacked members must show the same frame. STACK NEW warns until framelock reports the outputs locked.
- **fleet**: stacks spanning nodes, and failover across nodes (later).
- **companion**: exposes the variables, actions and feedbacks listed in §8.
- **blend off-lane** (carry-over "BLEND SOLVE blocks the lane"): stack blends make solves heavier. Facade assumes that package
  moves BLEND SOLVE off the lane; if it has not, STACK ALIGN still runs off-lane on its own.

**Third-party**

| Library | Version | Licence | Use |
|---|---|---|---|
| LASzip | 3.4.x (C API `laszip_api.h`) | Apache-2.0 (R§4) | LAZ, loaded at run time from the user's install or shipped beside the exe; never linked at build |
| libE57Format | 3.4.1 (2026-09-20) | BSL-1.0 | reference only, for edge cases; nothing is copied without its notice |
| ProjNet | 2.1.0 | LGPL-2.1+ | **rejected** |
| laszip.net | 2.2.1 | none declared (a port of LGPL-era LASzip) | **rejected** |
| PROJ | — | MIT (unverified this pass) | later, run-time load |

No new NuGet packages: `System.Numerics.BitOperations.Crc32C`, `System.Xml`, `System.IO.Compression.ZLibStream` and
`System.Security.Cryptography.SHA256` are in the shared framework (System.IO.Hashing, a separate package, is not needed).

---

## 7. Verification here

| What | How | Real / simulated / compile-only |
|---|---|---|
| Rebasing, geodesy, E57, LAS chunks, voxel thinning, QEM, control files, EXIF | Unit tests on generated files: an E57 test writer, LAS written by the test, OBJ at UTM coordinates, JPEGs with crafted EXIF | **Real** |
| Long-throw maths | Seeded Monte Carlo against the bench table (`r2/bench/facade/longthrow_table.py` numbers as assertions with tolerances); the auto-lock words; the priors-off golden test | **Real** |
| Night profile | ScanRunner on the simulator: `LongFacade` + `SimDisturbances` (100 Hz flicker, headlight sweeps), FRAMES and EVERY variations, persisted frames, restart then SCAN APPLY | **Simulated** (camera and projectors are the simulator's) |
| Night profile through a real capture path | A Stream camera reading an FFmpeg `lavfi` testsrc (FFmpeg 9 at /opt/ffmpeg9) to exercise averaging, ROI crop and gray16 persistence end to end | **Real decode path, synthetic picture** |
| Photometry, PLAN LIGHT | Simulator scenes with known lux and albedo; the analytic 30 000 lm example | **Real maths, simulated light** |
| Glass | `LongFacade` glass windows as mirror rays (sky and ground reflections); IoU vs the scene's window polygons | **Simulated** |
| Stacks | Two or three simulated projectors with a sub-pixel pose and zoom offset. STACK ALIGN, then re-simulate the camera view of both members through their measured lattices; residual in master pixels. Blend collapse checked on `BlendSolver3D` maps | **Simulated** |
| Measured warp layer on the GPU | Render tests on Mesa llvmpipe (Xvfb :99): read back an output with a known measured lattice | **Real (software GL)** |
| Failover | `DeviceService` over a fake driver (as the Devices tests do) raising a light-source error; fake output health; assert STATE, journal, `IOutputGains` values and the fade timing on a fake clock | **Simulated devices** |
| LAZ | `LaszipNative` compiled; tests use a fake `ILaszip`. If `liblaszip` happens to be installable in the container, an opt-in test reads a LAZ written by it | **Compile-only** (honest: no LASzip here) |
| Real DSLR tether, PTZ cameras, real projectors at 100 m, real streetlights | Not here | **Hardware lab / site** (ROADMAP R0.5); the storyboard in §8 is the site acceptance |

---

## 8. Desk and Companion surface

**Map workspace**

- **Site drawer** (Map → Site): CRS chip ("ETRS89 / UTM 32N"), origin in grid metres, grid scale, mains 50/60, and an
  "Import survey…" button (OBJ, PLY, glTF, LAS, LAZ, E57, XYZ, CSV of control points). The surveys list shows provenance (file,
  hash, points, decimation error) and state. The control-points table shows each point's distance to the model with its light;
  clicking a row flies the 3D view to the point.
- **Projector card**:
  - a lens line ("ET-D75LE40 · TR 3.02 ±2 % · from lens readback"), with an "Is at control point…" picker for the position prior;
  - the solve line leads with the wall: "On the wall ±1.4 px · distance ±3.7 m (zoom↔distance) · Lock zoom / Night-scan";
  - the 3D view draws the position uncertainty as an ellipsoid (a cigar along the axis) at the projector.
- **Scan screen**: a "Night" profile chip, plus exposure advice ("1/20 s: 3 × 16.7 ms is flicker-safe here"), a flicker
  reading, a transient counter ("2 headlights: 8 patterns re-shot") and the ETA dial.
- **Scan review**: "Mask glass" proposes masks drawn on the wall in the edit overlay and in the UV view, each with its evidence;
  Accept. Then "Re-solve without glass" shows RMS before and after.
- **Light plan**: a heat-map toggle in the 3D view (lux, nits, contrast, pixel mm) with a legend. Per-surface numbers in the
  inspector; the deficit sentence offers "Add to stack…".
- **Stacks**: a stack chip on member projectors; "Align stack" (shows residual px per member); "Match colour"; Failover Hold or
  Match; a red degraded banner with the cause.
- **Right-click** (in step with the verbs):
  - projector: Solve, Lock zoom, Set lens…, Position from control point…, Add to stack…, Align stack;
  - surface: Mask glass, Plan light here, Set albedo…;
  - mesh: Check against control points, Decimate…;
  - control point: Pin this on <projector>, Pin this on <camera>.

**Companion** (the companion package builds the module; these are the facts and verbs facade offers):

- Variables: `site_crs`; `projector_<name>_wall_px`; `projector_<name>_weak`; `stack_<name>_state` (ok, degraded, unaligned);
  `stack_<name>_residual_px`; `stack_<name>_gain`; `plan_<surface>_lux_p5`; `scan_night_transients`.
- Feedbacks: stack degraded (red), stack unaligned (amber), projector weak solve (amber).
- Actions: `STACK FAILOVER <stack> HOLD|MATCH` (a toggle button per stack, auto-created from STACK LIST); `PLAN LIGHT`
  (shows lux p5 on the button); `SCAN AMBIENT` (guarded: High risk, needs a hold-to-confirm in the module).
- Auto-created buttons: one per stack (state + failover toggle), one per projector with a weak solve.

**Site acceptance storyboard** (filmed on the first real facade, as UX §5.1 does for the box):

1. Import the E57 and the control CSV; CONTROL CHECK is green.
2. Set lens priors from the device readback.
3. Pin 10 control points per projector with the video cursor; check wall px.
4. At dark, run SCAN AMBIENT, then a night scan through the PTZ camera; mask glass; re-solve.
5. Stack, align and match.
6. PLAN LIGHT measured against the target.
7. Pull a member's power: MATCH dims the building evenly.

---

## 9. Risks and open questions

1. **E57 without the standard in hand.** ASTM E2807 is paywalled. The reader follows libE57Format's behaviour; test files come
   from our writer plus, if licensing allows, the libE57 sample files. Risk: exotic files (spherical-only, images, odd codecs).
   They are refused with words; we do not guess.
2. **Grid vs ground metres.** A model in grid metres against tape-measured ground distances differs by up to ~400 ppm. We show
   k and let CONTROL CHECK say it. Scaling the model to ground is an explicit operator choice (`SITE ORIGIN … ` does not
   rescale). Open: offer `SURVEY SCALE GROUND`?
3. **Datums.** WGS84, ETRS89 and NAD83 are treated as one with a warning (~0.8 m apart for ETRS89/WGS84 in 2026: unverified figure
   from memory). This is harmless while everything comes from one survey; it matters when phone GNSS positions are mixed in.
   They then carry σ ≥ 3 m as priors.
4. **Night exposure without a control seam.** We advise exposures but cannot set them on UVC or PTZ cameras this round. If the
   operator ignores the advice, flicker detection and averaging limit the damage, and the words name the setting.
5. **Glass false positives on dark stone and false negatives on blinds.** Weights are tuned on the simulator only; the desk
   always proposes and never auto-applies. Real data is needed (R0.5 lab material samples include glass).
6. **Stack alignment needs one camera seeing all members at enough resolution.** At 100 m, a 4K PTZ zoomed in may cover only
   part of a stack. Open: tile captures with PTZ presets (pose per preset) in a later round.
7. **Failover MATCH dims the show.** Default is HOLD; MATCH is opt-in per stack. The governor has a measured cause and a
   deterministic test (doctrine). Open: should MATCH also be the show-safety panic outcome "even and dim"?
8. **Memory for > 12 MP cameras.** ROI crop is required; a streaming decoder (per-pixel bit accumulation) is the later fix.
9. **PoseSolver regression risk.** Priors default off and the golden test pins existing results bit-for-bit.
10. **Who owns the lens library** (throw-ratio ranges per lens model, e.g. Panasonic ET-D75LE40)? A small JSON table in Devices
    is proposed. The data entry is unverified per model, so an operator confirms it.
11. **`docs/MAPPING.md` is referenced by CLAUDE.md but absent on main.** This package creates it with the facade chapter. The spine
    owner should confirm it is the right home.

---

## 10. Effort (agent-hours)

| Item | Hours |
|---|---|
| Site frame, readers' rebase overloads, SITE verbs | 5 |
| Geodesy (ECEF/ENU, UTM, CRS sniffing, LAS VLRs) | 4 |
| E57 reader + test writer | 7 |
| Chunked LAS, voxel thinning, E57 station meshing | 3 |
| QEM decimation | 4 |
| SURVEY IMPORT job, provenance, STATE | 3 |
| Control points (file, CHECK, pin binding) + CAMERA SOLVE + EXIF | 5 |
| Priors, auto-lock, uncertainty analysis, PROJECTOR PRIOR/SOLVE | 6 |
| Night profile (averager, references, transients, re-shoot, gray16, persistence) + SCAN AMBIENT | 7 |
| Photometry + PLAN LIGHT | 4 |
| Glass detector + MASK GLASS + re-solve | 5 |
| Bridged-into-StageScene fix | 1 |
| Simulator: LongFacade, disturbances | 4 |
| Desk (site drawer, projector card, scan chips, light legend) | 5 |
| Papers (WIRE, ARCHITECTURE, UX, ROADMAP, MAPPING) | 2 |
| **Must subtotal** | **65** |
| Stacks: model, blend collapse, alignment, colour match | 8 |
| Failover policy + runtime gains + render hook | 3 |
| LASzip native seam | 2 |
| Light-plan heat map view | 2 |
| **Should subtotal** | **15** |
| **Total** | **80** |
