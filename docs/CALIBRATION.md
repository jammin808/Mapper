# The quad on the wall, the score and the calibration ledger

What a person does in the first five minutes with a projector and a wall, and how PlayMap keeps, explains and returns to every
calibration it makes. The verbs are in `docs/wire/wall.md`.

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
or the journal, and lapse three seconds after their last line.

## The score (`Core/Calibration/Scoring`)

Every calibration result carries one explainable `QualityScore`, in one vocabulary (`ScoreTermCodes`, worded by
`ScoreVocabulary`): each term has its code, words, value and unit, its penalty, a light, where on the picture it is, a fix line
(a line of the catalogue) and a hint for hands.

- **Pins** (px-equivalents, lower is better): the inlier RMS rounded up to 0.1 px is the base; p95 of the inliers, outlier pins
  (0.5 each, the worst named, its fix disables it), the inliers' spread (convex hull share of the picture), coverage (cells with a
  pin), conditioning (the solve's own uncertainty on the wall, and 1 px more when zoom cannot be told from distance) and lens
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

`CALIBRATION RESOLVE <projector>…` re-solves only the named projectors from their pins as they stand — the operator drags them
onto the wall's features — scores each result and applies it in one edit when its score is within
`max(calibrationGate, 1.25 × the fallback's)` and not F. Otherwise, with `STAGE`, or when an assistant asked, the result is
**staged**: recorded with the projector untouched and `calibration.staged` raised, until `CALIBRATION ACCEPT` applies it. A
projector with no fallback keeps its calibration from before as one. No other projector, result or surface moves
(`ResolveTests`: a projector knocked 2 cm comes back within half a pixel RMS on the wall).

## Not yet

The camera re-solve ladder (QUICK, LENS, CODED, DENSE), the dot check and the drift monitor, re-seating pixel quads by the measured
displacement after a re-solve, lens read-back triggers, before/after on the wall, the seam term, the report, `OVERLAY GRID`,
overlay names and pins, `HIGHLIGHT`, `PICK PIXEL`, snapping, quads on the 3D view and warp handles on the wall.
