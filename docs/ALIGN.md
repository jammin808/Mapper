# Camera auto-align of flat screens

One camera on a tripod, any number of projectors on one flat screen: PlayMap shows each projector its patterns, watches them, fits
the screen, and gives every output a measured warp and a measured blend so the outputs make one picture of one canvas. Nothing
reaches the air until a person accepts the result, and every accepted result goes into the calibration ledger with the correction
it replaced kept as its fallback (`docs/CALIBRATION.md`). The verbs are in `docs/wire/align.md`.

## The flow

1. `ALIGN NEW Wall cm1 ALL` groups the outputs that make one picture, the camera that sees them and the canvas they share (the
   canvas they show already, or a new one wide enough for all of them; an output that showed something else is put on it).
2. `ALIGN CORNERS <group> x1 y1 … x4 y4` (optional) gives the screen's corners as clicked on the camera picture; the canvas is
   then exactly the screen (`FIT CORNERS`). Without corners the canvas is the largest rectangle of its aspect the outputs cover
   (`FIT INSIDE`).
3. `ALIGN RUN <group>` measures and stages: progress, the step and the time left are in `STATE.align.run`. The run never writes
   the project. Its files go under `align/<run>/` beside the project and are never written again.
4. `ALIGN ACCEPT <run>` applies every output's correction as one edit (one undo step); `ALIGN REVERT <group>` goes back to each
   output's fallback; `ALIGN CLEAR` takes a correction off.

## Identify and scan (`Core/Align/AlignSequence.cs`)

The camera's side runs one output at a time: every output black; then each output white alone (its **footprint**: where its
light falls: the white − black difference split by Otsu's threshold, closed, its largest part) and its grey ramp (its **photometry**: black, white and its
transfer γ fitted to the ramp in camera-linear light, the camera taken as a 2.2 encoder); then its structured-light plan alone —
phase-shifted sinusoids over Gray-code periods, predistorted through the γ just measured — decoded to a projector point per camera
pixel. Grouping outputs whose footprints do not touch into one pass is next round.

The same sequence runs against the simulator (`MultiProjectorSim`, deterministic through `Pcg32`: a camera rig of kind
`Simulated` whose source names a layout such as `flat-screen:tile3x1:o20`, `bulged-screen:…`, `curved-screen:…` or `…:stack2`)
and against a real display and camera (`DisplayShooter`: the render host's scan display and any `IScanCamera`, the latency
measured with flashes first and each picture taken only once the camera has settled). The camera is opened through
`AlignRunnerOptions.OpenCamera`, so a phone camera plugs in as a seam.

## The flat fit (`PlanarFit`, `PlaneRectify`)

A plane seen through a lens is a homography only once the lens is undone, and every output's homography shares the one lens. The
fit is joint: the camera's radial distortion (k1, k2, about the picture's centre) and one homography per output, Levenberg–Marquardt
from each output's robust DLT, re-weighted by Huber (δ 1 px). A screen whose pairs a plane cannot explain to 0.5 output px RMS
(with 90 % of the pairs within 3 px) is **not flat**: the run fails with words and stages nothing; curved screens are next round.

The plane is then rectified to metric: from the projectors' homographies and what may be assumed of their lenses (square pixels;
a projector's principal point and focal length when the output shows a described projector, ±5 %, else a centred throw-1.5 lens,
±30 %), or from the clicked corners.

## The canvas and the measured warp (`CanvasTarget`, `MeasuredWarpFit`)

The canvas target is a rectangle on the rectified plane. Each output's dense pairs (raster point → canvas uv) fit a **measured warp
grid** (33 × 19 nodes by default, `AlignOptions.GridColumns/GridRows`): bilinear per cell, least squares with a graph-Laplacian
smoothness and Huber weights, every tenth pair held out to measure the residual. A node the camera did not see keeps the smooth extension of its neighbours and is
marked invalid; a point is shown only where the validity is at least one half. Every member of a stack fits the same target, so a
stack converges.

## The measured blend (`MeasuredBlend2D`, `Photometry`)

Per output, at a quarter of its raster, three floats a pixel: the **weight** (from the distance to the raster's edge and to the edge
of where it reaches on the canvas, over `EdgeWidth` 0.15 of the raster side, through the cosine curve, normalised so the outputs'
light sums to one), the **black pedestal** (the black lifted to the deepest overlap's floor, shared by weight, as a share of each
output's own white) and the **uniformity gain** (the white evened out toward the dimmest by `Uniformity`, 0.6 by default: a camera's
vignetting and a gain screen's angle bias what it measures). Each output's γ from its grey ramp is written to its `Blend.Gamma`.

## On the air (`Render/Passes/MeasuredStage.cs`, the `output` program)

The output stage draws the measured grid as the first warp layer (the operator's warp then moves the corrected picture) and it
replaces the output's region of the canvas, since the grid holds the canvas's own uv. With a measured blend the picture goes to light
(its 2.2, times the gains), takes ped + (1 − ped) · weight · L, and goes back through the output's measured γ. With gains of one, a
γ of 2.2 and no blend the picture passes bit for bit. The files are read off the lane and off the render thread
(`MeasuredLibrary`, each checked against its hash and only inside the project's folder); the render thread uploads a set when it
changes and nothing on a frame where it is the same. An output whose file is missing or changed runs without its correction, and
`outputs[].measured.files` says which file.

## The score

A staged run carries a points score (`CalibrationScoring.ForAlign`): coverage of the canvas, seams (how far neighbours disagree in
their overlap, p95) and uniformity (1 − white p5/p95 after the blend), graded A/B/C/F. Every fix a term carries is a line the node
carries out (the uniformity term's raises the group's `options.uniformity` to 1). The accepted score is the ledger result's.

## Not yet

Grouped capture, curved screens (cylinder, then dome), the colour match (`STEPS COLOR`, `COLOR WHITE`), the projectors' own
processing (snapshot, neutral and restore), the shutter knock, the free-form hand-over to the scan solver, auditing a vendor's
built-in result, the warm-up gate, two cameras, camera control, `ALIGN PLAN` (the dry run), `ALIGN SCORE`, `ONLY`, `DEVICE NATIVE`
and the desk's Align tab.
