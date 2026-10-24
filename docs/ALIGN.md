# Camera auto-align of flat screens

One camera on a tripod, any number of projectors on one flat screen: PlayMap shows each projector its patterns, watches them, fits
the screen, and gives every output a measured warp and a measured blend so the outputs make one picture of one canvas. Nothing
reaches the air until a person accepts the result, and every accepted result goes into the calibration ledger with the correction
it replaced kept as its fallback (`docs/CALIBRATION.md`). The verbs are in `docs/wire/align.md`.

Round 3 puts this inside a guide for projectors blended or stacked on a flat screen, each camera step beside its hand step, and keeps
the measured layer under the hand's; *Not yet*, at the end, says how.

## The flow

1. `ALIGN NEW Wall cm1 ALL` groups the outputs that make one picture, the camera that sees them and the canvas they share (the
   canvas they show already, or a new one wide enough for all of them; an output that showed something else is put on it).
2. `ALIGN CORNERS <group> x1 y1 … x4 y4` (optional) gives the screen's corners as clicked on the camera picture; the canvas is
   then exactly the screen (`FIT CORNERS`). Without corners the canvas is the largest rectangle of its aspect the outputs cover
   (`FIT INSIDE`).
3. `ALIGN RUN <group> [COLOR WHITE] [ONLY <output>…]` measures and stages: progress, the step and the time left are in `STATE.align.run`. The run never writes
   the project. Its files go under `align/<run>/` beside the project and are never written again.
4. `ALIGN ACCEPT <run>` applies every output's correction as one edit (one undo step); `ALIGN REVERT <group>` goes back to each
   output's fallback, drawing the blend γ the output drew when that result was kept; `ALIGN CLEAR` takes a correction off.

## Identify and scan (`Core/Align/AlignSequence.cs`)

The camera's side runs one output at a time: every output black; then each output white alone (its **footprint**: where its
light falls: the white − black difference split by Otsu's threshold, closed, its largest part) and its grey ramp (its **photometry**: black, white and its
transfer γ fitted to the ramp in camera-linear light, the camera taken as a 2.2 encoder); then its structured-light plan alone —
phase-shifted sinusoids over Gray-code periods, predistorted through the γ just measured — decoded to a projector point per camera
pixel. Grouping outputs whose footprints do not touch into one pass is next round.

The same sequence runs against the simulator (`MultiProjectorSim`, deterministic through `Pcg32`: a camera rig of kind
`Simulated` whose source names a layout such as `flat-screen:tile3x1:o20`, `bulged-screen:…`, `curved-screen:…` or `…:stack2`)
and against a real display and camera (`DisplayShooter`: the render host's scan display and any `IScanCamera`, the latency
measured with flashes first, each held as a scan holds its flashes — for its hold and until four camera pictures stamped after
it have come, docs/SCAN-MULTIVIEW.md — and each picture taken only once the camera has settled). The camera is opened through
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
vignetting and a gain screen's angle bias what it measures). Each output's γ from its grey ramp is its measured γ
(`Measured.Color.Gamma`), and ACCEPT also writes it into `Blend.Gamma`, as CALIBRATION RECALL and ALIGN REVERT write the γ their
result recorded. `Blend.Gamma` is the γ the blend's fades are drawn through when above 0 — a person's, or the γ the output's current
calibration result recorded; 0 draws the output's measured γ where it has one, else 2.2, and every renderer, the 3D blend, the audit
and the MPCDI export read it so (`Output.DrawnBlendGamma`). A person's γ is told from a result's in one place, `Output.HandGamma`: it
is `Blend.Gamma` unless that is 0 or the γ the current result recorded. A project file of schema 1 is read by that rule: a stored 2.2,
or a γ that is not a person's, reads as 0 wherever 0 draws the same γ, any other γ stays the person's, and PROJECT OPEN's answer and
journal row name each output it moved. The file writes the drawn γ as `blend.gamma`, which every earlier build reads, and
`Blend.Gamma` beside it as `blend.gammaOverride`, so a round-2 backup mirroring this node, or a round-2 build opening its file, draws
the same fades.

## On the air (`Render/Passes/MeasuredStage.cs`, the `output` program)

The output stage draws the measured grid as the first warp layer (the operator's warp then moves the corrected picture) and it
replaces the output's region of the canvas, since the grid holds the canvas's own uv. With a measured blend the picture goes to light
(its 2.2, times the gains), takes ped + (1 − ped) · weight · L, and goes back through the output's measured γ. With gains of one, a
γ of 2.2 and no blend the picture passes bit for bit. The files are read off the lane and off the render thread
(`MeasuredLibrary`, each checked against its hash and only inside the project's folder); the render thread uploads a set when it
changes and nothing on a frame where it is the same. An output whose file is missing or changed runs without its correction, and
`outputs[].measured.files` says which file.

## Rescanning one output (`ONLY`)

Every run keeps what the camera saw of each output beside it (`capture-<output>.bin`: the decode, the footprint, the white picture
and the photometry; `black.bin`). `ALIGN RUN <group> ONLY op2` loads the captures of the run the group's outputs stand on, shows
patterns on op2 alone, and solves with the earlier captures as the reference: the screen, the rectification and the canvas come
from them exactly, so op2's new warp lands on the very canvas the others already show. The others keep their warp files as they
are; every blend is made again from the new black, the kept whites and op2's new one (`AlignOnlyTests`: P2 knocked 3 px, the
seams back from 3.3 px to 0.05 px, op1's file unchanged byte for byte, op1 shown nothing). Each run also writes `basis.txt` (the run
whose captures made its canvas) and `canvas.txt` (the fit, aspect, margin and corners it was made with): chained ONLY runs keep
the one canvas, and ONLY refuses with words when those settings or the camera's raster changed since.

## Colour and level (`COLOR WHITE`, `Core/Align/ColourMatch.cs`)

The colour step shows each output's black, red, green, blue and white through the display's colour path, each output alone, and
measures each over its own footprint (eroded off its edge) in camera-linear RGB (the camera taken as linear sRGB through its 2.2). Each output's primaries make a
3 × 3 matrix M; the common white is the mean of the outputs' white chromaticities at the largest level every output reaches with
gains no more than one, and each output's gains are M⁻¹ · white: diagonal gains in linear light, staged as its measured colour and
drawn by the output stage; a white an output cannot reach refuses colour with words. First a stability probe shows the first
output's white alone and again with the other outputs lit (a lone output, or a stack whose outputs share every pixel: on the left
of its raster only): where only it reaches, a camera that holds its exposure reads
the same; one that sets its own refuses colour with words, and the run's geometry is staged without it (`ColourMatchTests`: a
6 500 K output at full level and a 7 500 K one at 0.8 land within Δu′v′ 0.0002 and 0.5 % of each other).

## Verify and the score

Every run ends with a verify pass (`Core/Align/AlignVerify.cs`): a grid of canvas points about 24 output px apart, drawn as dots
through each output's new warp, each output alone; where two outputs drew the same canvas point, the distance between the two
dots the camera saw is their misregistration on the screen in output pixels (a stack's, when the outputs share one picture).
Measured rather than predicted, it sees a projector that drifted after the scan.

A staged run carries a points score (`CalibrationScoring.ForAlign`): coverage of the canvas, the verify pass's seam (or stack)
misregistration p95, and uniformity (1 − white p5/p95 after the blend), graded A/B/C/F; `ALIGN SCORE` answers it with every term.
Every fix a term carries is a line the node carries out: coverage `ALIGN RUN <group> FIT INSIDE` (or `ALIGN CORNERS <group> CLEAR`
when corners reach past what the outputs light), seam and stack `ALIGN RUN <group>`, uniformity the group's `options.uniformity`
raised to 1 (`AlignScoreTests` trips each with a simulator condition). The accepted score is the ledger result's.

A run says its pictures and its pattern time — pictures times a picture's hold (`AlignRunnerOptions.PatternHold`, 200 ms: a
present, the camera's latency, one settled picture). Four projectors in a 2 × 2 tile through every step take 167 pictures here
(33 s) and 207 at 1920 × 1080 (41 s), inside the 90 s bar (`AlignTimingTests`).

## The projectors' own processing

A projector whose own geometry or edge blend is on while PlayMap draws a measured correction warps or blends twice. For every
device linked to an output with a measured correction (by the link's output, or by the projector the output shows), the
projector's own processing is read live, off the lane, in step with the device's poll and at once after an accept
(`Devices/Native`: on Panasonic `QVX:GMMI0` and `QVX:EDBI0`, queries only; other protocols say where to look). One found on lights
its `device:<id>` context item amber with the words and the `DEVICE RAW … VXX:GMMI0=+00000` line that turns it off, adds an amber
preflight row, and `STATE.align.devices` says what was read. `DEVICE NATIVE <device>` reads one now.

## Simulator layouts

A simulated camera's source names its layout: `flat-screen|bulged-screen|curved-screen`, `tile<c>x<r>`, `stack<n>`, `o<pct>`
(overlap), `k<kelvin>,…` (each projector's colour temperature), `lvl<level>,…` (its light), `autoexposure` (a camera that sets
its own exposure, within 0.6–1.6× of its set one), `shift<n>=<px>` (projector n knocked so its picture moves that many of its
pixels) and `drift<n>=<px>` (the same, only after the scan: seen by the verify pass).

## Not yet

Grouped capture, curved screens (cylinder, then dome), FULL colour (the primaries matched, not only white), a colour step on its
own, the warm-up gate, the projectors' own processing snapshot, neutral and restore, the shutter knock, the free-form hand-over to
the scan solver, auditing a vendor's built-in result, two cameras, camera control and `ALIGN PLAN` (the dry run).

Round 3 adds the following (`docs/round3/PLAN.md` D12, §3.19, §3.20, §9 decision 22; the design is
`docs/round3/design/desk-workflows.md` §2, §3, §5.1, §8). None of it is built yet. Its new rows and forms are in the catalogue and
answer "not available on this node yet".

- **The flat guide.** Two projectors blended side by side, two stacked, or four as two stacked positions blended, on a flat screen.
  Each step is done by hand or by a camera run: the corners by `WARP CORNER` and `WARP NODE … PX`, or by `ALIGN NEW … CANVAS`,
  `ALIGN RUN` and `ALIGN ACCEPT`; the match by the output's colour trims, or by `ALIGN RUN … COLOR WHITE`.
- **One canvas in one line.** `CANVAS SPAN` makes the canvas, each output's rectangle and its edges as one edit, and `PATTERN … ON
  <canvas>` draws one test card across the whole canvas. A re-span over a rectangle a hand moved, or over an output drawing a
  measured warp, is refused naming each unless the line says `KEEP` or `REPLACE`.
- **The measured layer under the hand's.** An accept replaces the measured layer only, and its reply lists every hand edit it kept
  with the line that clears it (`HAND`, `HAND CLEAR`). It writes no `Blend.Gamma`, and neither does `ALIGN REVERT`: the measured γ
  stays in `Measured.Color.Gamma`, and `Blend.Gamma` holds only a person's γ (0 draws the measured one, else 2.2), so a re-run never
  overwrites the hand's γ (*The measured blend*, above, is today's rule).
- **On the desk.** An Align area pane in Pro lists the runs' cards with their actions, and the flat guide's camera steps sit beside
  their hand steps.
