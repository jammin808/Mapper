# The quad on the wall, the score and the calibration ledger

What a person does in the first five minutes with a projector and a wall, and how PlayMap keeps, explains and returns to every
calibration it makes. The verbs are in `docs/wire/wall.md`.

The desk keeps a half-drawn quad when a person clicks away, and drags, types and nudges a quad's corners and a pin by one pixel
(round 3's desk-ux). Round 3 goes on to move every handle by one pixel and to keep a re-solve from overwriting a pose typed by hand;
*Not yet*, at the end, says how.

## First light and a quad

`LIGHT WALL #2` puts an output full screen on the second display in projector mode, gives it a projector (a nominal pose, throw
1.5 or `THROW`), shows its identify card for three seconds and turns the edit overlay on for it. The reply names the next step.

`SURFACE QUAD <projector> x1 y1 … x4 y4` takes four points clicked on the wall in any order (wall pixels: the output's own final
pixels, after its warp) and makes a **pixel quad**: a surface drawn in that projector's raster rather than on a mesh. The clicks
are taken back through the output's warp (`WarpField.Invert`), ordered top-left first and clockwise, and checked: the corners must
make a strictly convex quad of at least 64 px². A new quad shows the UV checker until `SURFACE SOURCE` gives it a picture.

### The maths (`Core/Mapping/PixelQuadMath.cs`)

A content point (u, v) of the unit square lands on the raster at p(u, v) = H(u, v) + L(u, v): H is the closed-form homography of
the square onto the four corners (`QuadHomography`), L the lattice's offset field (zero at rest). A lattice given to a quad
therefore keeps its shape exactly; a node dragged to a raster point gets the offset that lands it there (every interpolation
passes through its nodes); moving a corner moves H and the lattice rides on it. A raster point inverts through H⁻¹, then Newton on
the lattice. Corners and offsets are raster-normalised, so a quad stays put when the output's raster or warp changes.

The projector pass draws each visible quad of its projector after the 3D surfaces, depth test off (2D on top). Vertices carry
(u·q, v·q, q) with q = 1/w of the homography, interpolated linearly and divided per fragment: the picture is projectively exact
across the diagonal, where two affine triangles would put a seam (`PixelSurfaceRenderTests` measures both).

### Moving it

`SURFACE CORNER`, `SURFACE NODE` and `HANDLE NUDGE` move one handle, `TO` a point (wall-normalised by default) or `BY` a move
(wall pixels by default; the warp's local inverse turns it into a raster move). A burst of moves of one handle is one undo step.
`HANDLE SELECT/NEXT/PREV` choose the handle the wall shows enlarged — the air's, not the document's.

### The edit overlay

`OverlayPass` draws after the output stage, straight into the output's final raster: each quad's outline (in its colour, on a
dark edge), its corner handles (the selected one larger, ringed in white), its lattice, and every session's cursor with the
clicks so far. Every point is taken through the output's own warp and quarter turns, so the lines sit on the content they edit;
the blend, the mask, the trim and the air's gain never touch them. While the show is armed, on a black air, on an output the
overlay does not name, or when it is off, nothing is drawn — the output is byte for byte what it is without the overlay
(`OverlayTests`). The overlay holds a lease its session renews (`OVERLAY KEEP`); an armed show turns it off.

Cursors (`CURSOR`) live on a board the control lane writes and the render thread reads lock-free; they never touch the project
or the journal, and lapse three seconds after their last line. The board keeps when each cursor's line arrived (the node's
monotonic clock); the render host counts the frames from that arrival to the frame that first draws the move — one when it
arrived after the last frame began — times the output's frame period, and publishes the p95 over the last 300 moves as
`air.overlay.cursorP95Ms` through its facts signal. Nothing on the frame path allocates for it (`CursorTests`: two cursors moving
300 times are drawn within two frames at p95, with no byte allocated).

### The first map

`STATE.onboarding` stamps the first light (`LIGHT WALL`), the first quad and the first content on a quad. The last is the render
host's word: the frame it finds a picture of a quad's source ready to draw (a node without a render host stamps it when the
project first puts content on a quad an output shows). `FirstMapTests` runs the scripted first map on a real headless node — LIGHT
WALL, SURFACE QUAD, eight nudges, MEDIA IMPORT of a 2 s clip, SURFACE SOURCE — and reads the clip inside the quad and black around
it; the machine time is recorded (about a second here; a Perf twin holds the 5 s bar).

### The projector's view

`VIEW … SHOW projector:<id>` shows what the projector puts on the wall: the stage as it lights it, through the lens lookup of the
first enabled output that drives it, before that output's warp, mask, blend and trim — equal to the output's picture before its
trim within one code, and black wherever the projector lights nothing (`ProjectorViewTests`).

## The score (`Core/Calibration/Scoring`)

Every calibration result carries one explainable `QualityScore`, in one vocabulary (`ScoreTermCodes`, worded by
`ScoreVocabulary`): each term has its code, words, value and unit, its penalty, a light, where on the picture it is, a fix line
(a line of the catalogue) and a hint for hands.

- **Pins** (px-equivalents, lower is better): the inlier RMS rounded up to 0.1 px is the base; p95 of the inliers, outlier pins
  (0.5 each, the worst named, its fix disables it), the inliers' spread (convex hull share of the picture), coverage (cells with a
  pin), conditioning (the solve's own uncertainty on the wall, the projector's priors' information included, and 1 px more when
  zoom cannot be told from distance) and lens
  plausibility (square pixels, a principal point within reach, a throw a lens has, mild distortion) add penalties. A pin behind
  the projector, too few inliers or no optics is a hard failure: 99.9 and F.
- **Scans**: the dense fit is the base; a poorly decoded picture and a single view add.
- **Alignments** (points, higher is better): 100 less coverage, seam, stack, uniformity and colour deductions.

Grades: px A ≤ 1.0, B ≤ 1.5, C ≤ 4, else F; points A ≥ 85, B ≥ 70, C ≥ 50, else F. The words give the grade, the value and the two
terms that cost most ("C 2.8 — 55 % of the projector's picture decoded; 1 view: depth from one camera position").

`SolveUncertaintyAnalysis` gives the conditioning term: Σ = σ̂²(JᵀJ)⁻¹ over the parameters the solve was free to move (the pose
in the device's axes, the focal and the lens shift unless locked), propagated to a grid of the raster at the pins' median depth —
the wall error's p95 and worst, the axis and lateral position, the focal, the condition number and the weakest direction in words.

## The ledger

A `CalibrationSnapshot` is never edited after it is made. `PROJECTOR SOLVE` and `SCAN APPLY` record one (scored) in the same edit
as the change, so one undo takes both back. The first result applied to a projector is its **fallback**. A target keeps the
project's `CalibrationKeep` (20) newest loose results besides its current, fallback, staged and pinned ones.

- `CALIBRATION RECALL <snapshot>` and `CALIBRATION FALLBACK <projector>` put a result back exactly, recorded as a new result
  (source `revert`, its parent the replaced current, its score the recalled one's).
- `CALIBRATION ACCEPT` applies a staged result and makes it the fallback (or makes the current one the fallback).
- `CALIBRATION SAVE` keeps the calibration as it stands as a pinned result; `CALIBRATION FORGET` drops a loose one.
- `CALIBRATION HISTORY` gives each result's move from the one before: millimetres, degrees and wall pixels (p95 over a grid of the
  raster at the pins' median depth).
- `ICalibrationLedger` (`EngineServices.Ledger`) is how align and facade record, accept and fall back output results.

## Re-solving the projector that moved

`CALIBRATION RESOLVE <projector>…` re-solves only the named projectors and scores each result; it applies it in one edit when its
score is within `max(calibrationGate, 1.25 × the fallback's)` and not F. Otherwise, with `STAGE`, or when an assistant asked, the
result is **staged**: recorded with the projector untouched and `calibration.staged` raised, until `CALIBRATION ACCEPT` applies
it. A projector with no fallback keeps its calibration from before as one. No other projector, result or surface moves.

**From the pins** (no `CAMERA`): the pins as they stand — the operator dragged them onto the wall's features (`ResolveTests`: a
projector knocked 2 cm comes back within half a pixel RMS on the wall). It is `PROJECTOR SOLVE`'s solve with no LOCK word
(docs/FACADE.md): the projector's priors (`PROJECTOR PRIOR`) are rows beside the pins, the zoom is locked from the lens when the pins
cannot carry it (and a lock the lens put there is asked again), the locks it answers go with the result, and a pin bound to a control
point it is not at refuses the re-solve with the `SET` line that puts it there. Each reply row names `priorsUsed` and `locked`.

**From a camera** (`CAMERA <camera>`, calibops M5–M8 at their QUICK rung): a calibrated camera of the stage takes a fresh look at
only the named projectors (`ALL`: every projector). One projector named alone is looked at with another the stage shows, as a
reference that is never re-solved, so a moved camera is not taken for a moved projector; with no other, the words say the two
read the same. A real camera's look holds every other projector output black while the dots are up.

1. *Dots* (`Core/Scan/DotCheck.cs`): every projector black, then each named projector alone showing a grid of dots 64 projector px
   apart, each moved by a seeded jitter of a quarter of the spacing so the pattern never passes for itself shifted, drawn as
   Gaussian spots. The camera's picture less the black one gives the spots: connected pixels over a quarter of the brightest
   difference, each centred by its levels in a window sized to it, twice.
2. *Matching*: the project says where each dot lands — its stored pose's ray onto the stage (every visible 3D surface, placed),
   then into the camera, which must see that very point. Every expected dot votes for the shift to each spot within four
   spacings; the most voted shift starts the matching (so a move of more than half a spacing is found), then an affine fit and
   each dot's neighbours' residual refine it; a spot is one dot's at most.
3. *Who moved* (`Core/Calibration/MotionAttribution.cs`): a projector with fewer than 60 matched dots is *unseen*; one whose
   matched dots land a median of more than 0.6 camera px from where the project says has *moved*; every seen projector moved
   together means the camera moved, and nothing is re-solved ("place the camera again"). Nothing moved, the camera, or nothing
   seen is refused with these words.
4. *Solve*: each mover's pose alone (its optics kept) from its dots — the camera's ray through each spot onto the stage, paired
   with the dot's projector pixel — through `PoseSolver`, robust. A position prior (`PROJECTOR PRIOR … AT`) joins the dots as rows
   weighed against their noise (0.3 px); the lens prior has nothing to weigh with the optics held.
5. *Verify*: the mover's dots looked at once more, matched through the new pose; the wall error of the new pose against them
   (p50, p95) is the result's `verifiedP95`.
6. *Score*: the dot fit scored as a pin solve (each dot a pin), a fix that would name a dot being the re-solve again.
7. *Apply*: in the one edit, the projector's pins keep their world points and take the new pose's pixels, and its pixel quads are
   re-seated (`Core/Calibration/Reseat2D.cs`): each corner and lattice node out along the old pose's ray onto the stage and back in
   through the new pose, so it lights the wall spot it lit before. Masks, 3D surfaces and output warps are not touched. A
   projector that blends automatically has its blend solved again off the lane (BLEND SOLVE's receipt is the reply's `blend`, or
   why it was not). A projector changed while it was being re-solved (dragged pins, a nudged pose) refuses the whole edit with
   words. A staged camera result is marked as one (its `runId` is `camera:<camera>`): when a person accepts it, its quads are
   re-seated from the pose it replaces and the blend is solved again, as if it had been applied at once. Accepted in a blind
   draft, the live blend is left alone and the reply says so: the render host's automatic blend solves it for the new pose once
   the draft lands on the air.

The task's state moves through `capturing`, `matching`, `solving`, `verifying` to `done`; `CALIBRATION CANCEL` stops it before it
applies. A simulated camera (a rig of kind Simulated) is its own display: `StageSimulator` traces the stage from the camera and
lights it from each projector where `SimulatedStage` says it truly stands (knocked or as the project says). A real camera shows
the dots on the projectors' outputs through the align display shooter (latency measured first, each picture taken once settled);
it opens as a scan opens it (`ScanCameras`): a phone through the node's phone hub, a still a picture, else a capture device or a
stream.
The pattern time is the frames times `wall.resolveFrameMs` (100 ms). `ResolveCameraTests`: of three projectors, P2 knocked 0.8°
and 2 cm is found moved and the others unmoved, P2 alone is re-solved to 0.004 px p95 on the wall against the truth, in five
frames (0.5 s of patterns); `ReseatTests`: a quad on a projector knocked 16 px lights its old wall spots again within 0.001 px.

## Not yet

The LENS, CODED and DENSE rungs of the ladder (a mover whose pose alone does not fit), re-solving the camera when it moved, a
stored reference look (the project's prediction stands in for it), the drift monitor, lens read-back triggers, before/after on
the wall, the seam term from the check, the report, `OVERLAY GRID`, overlay names and pins, `HIGHLIGHT`, `PICK PIXEL`, snapping,
quads on the 3D view and warp handles on the wall.

Round 3 adds the following (`docs/round3/PLAN.md` D12, §3.6 item 6, §3.18, §3.20 item 2; the design is
`docs/round3/design/desk-workflows.md` §3, §6). Of it, the quad that stays, a quad's corners and a pin's pixel are built (desk-ux;
docs/DESK.md, MAP's quad tool); the rest is not built yet. Its new rows and forms are in the catalogue and answer "not available on
this node yet".

- **The quad that stays** (built). An unfinished quad is a draft on the desk for each projector, kept in the user's `desk.json` by
  node and projector until Enter or Discard. A tool change, another projector, leaving MAP, a menu entry, the first-light guide
  closed, a desk restart and a reattach to the same node keep it. Its points drag and nudge before Enter.
- **Every handle by one pixel.** A quad's corners are dragged (`SURFACE CORNER … TO … RASTER`), typed in wall pixels and nudged by
  the arrows, and a pin's pixel is typed in its row and nudged by the arrows through the line its drag sends (built, desk-ux).
  `PIN MOVE` moves a pin, and `WARP CORNER` and `WARP NODE … PX` move an output's warp. `HANDLE SELECT` takes every kind of handle,
  so `HANDLE NUDGE` moves a pin as it moves a corner. A move that would change nothing is refused with the words.
- **What the wall shows.** The overlay draws the selected handle of any kind with a one-pixel crosshair at its exact point, and the
  pins as rings in the projector's identity colour, each with a line to where the solve predicts it. The projector view zooms, and a
  loupe magnifies 8×.
- **A typed pose is a result.** A pose or lens typed or nudged by hand is recorded in the ledger as a result of source `manual`.
  That is one more cause to stage, beside those of *Re-solving the projector that moved*: over a current result of source
  `manual`, a solve, a re-solve or a scan's result is staged and names the manual result. `CALIBRATION ACCEPT` applies it, and the
  history shows the manual result as its parent.
