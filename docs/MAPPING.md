# Mapping: the stage, the site and the solve

Where things sit in PlayMap and how a projector is solved into that place. The quad on the wall, the score, the ledger and
re-solving the projector that moved are in `docs/CALIBRATION.md`; surveys and control points in `docs/FACADE.md`; scanning in
`docs/ARCHITECTURE.md` §Scanning and `docs/SCAN-MULTIVIEW.md`; camera auto-align of flat screens in `docs/ALIGN.md`. The verbs are
in `docs/wire/wall.md` and `docs/wire/facade.md`.

## The stage

The stage is the project's 3D world: metres, right-handed, +Y up. Surfaces place their meshes with a transform (position, rotation
vector, scale); projectors, cameras and viewpoints have a pose (optical centre and rotation) and their optics. Mesh files are never
rewritten by a placement: a surface carries it. A pixel quad is drawn in its projector's raster rather than on a mesh, so no
stage move touches it.

## The site frame

A surveyed site puts the stage in a coordinate system (`Stage.Site`): `EPSG:<code>` or `LOCAL`, a grid origin (E, N, H in metres,
held in double), a rotation, a grid scale and the venue's mains frequency. A grid point becomes a stage point by

```
dE = E − E0, dN = N − N0, a = rotation, k = grid scale
x = (cos a·dE + sin a·dN) / k      y = H − H0      z = −(−sin a·dE + cos a·dN) / k
```

so +X is grid east turned by the rotation, +Y up and +Z grid south, and `SiteFrame.ToGrid` is the exact inverse. Every geometry
reader takes `GeometryReadOptions` (the file's unit, Z-up or Y-up, the frame) and rebases each point in double before narrowing it
to float: OBJ and PLY as they parse, glTF, LAS and XYZ once read (clouds stay in double). A facade 5.4 million metres north of its
grid's origin therefore keeps its millimetres, where a float there steps 0.5 m. A site is at most 2 km across, and a file whose
points lie more than 2 km from the site's origin is refused (it is in another system, or local). Earth curvature (0.8 mm at
100 m) is ignored.

The first georeferenced import, or the first control points, set the origin at their centre rounded to 10 m; `SITE ORIGIN` sets it
by hand. The first origin moves nothing: the stage's coordinates are taken as relative to it. Moving it later is one edit that
moves everything placed in stage coordinates by the same rigid move (`StageMove`) — surfaces, projectors, cameras, viewpoints,
pins, position priors, the calibration ledger's poses and LED fixtures placed in the world — so every grid position and every
pin's reprojection is kept. Control points and the thinned clouds of surveys are kept in grid coordinates, so they stay exact
whatever the frame becomes; pins and priors bound to a control point are re-derived from it in the same edit.

## Solving a projector from pins

A pin pairs a stage point with the projector pixel that lights it. `PROJECTOR SOLVE` fits the pose and the unlocked optics
(`PoseSolver`): closed forms (DLT with six or more pins, PnP with four when the optics are locked), RANSAC to flag a bad pick, then
Levenberg–Marquardt on the inliers. Locks hold what the pins cannot carry: `LOCK SHIFT|FOCAL|DISTORTION|NONE` for this solve and
after it. Pins on one plane hold the lens shift and the distortion; distortion is fitted from 10 pins. Each pin's residual and
inlier flag come back in pin order, and the calibration ledger records the result with its score (docs/CALIBRATION.md).

A pin may be bound to a surveyed control point (its `controlId`) instead of taking its stage point from a click on a model: the
point is then exactly where the surveyor shot it, re-derived whenever the site or the point moves. Setting a bound pin away from
its point is refused, and a solve refuses a bound pin that is not at its point, with the `SET` line that puts it there.

## The long throw

At 50–150 m a projector's pins cannot tell zoom from distance: on a flat facade seen head-on not at all, and with 2 m of relief
only to a few percent in zoom and metres along the axis while the picture still lands within 1.5 px. `PROJECTOR PRIOR` gives the
solve what it cannot see:

- the lens: `THROW <ratio> [±<pct>]` (±2 % by default), so the focal length f₀ = throw × raster width;
- the position: `AT <control point>` or `AT <x> <y> <z>` (±5 cm by default).

A prior is a residual row beside the pins' (`PosePriors`): r_f = σ_px·(f − f₀)/σ_f and r_C = σ_px·(C − C₀)/σ_C, with σ_px the noise
the pins are weighed against (1 px for hand pins, 0.3 px for a camera's dots). Only the refinement sees them, so a solve with no
prior is bit for bit the solve it always was.

The path a facade takes is:

1. **The lens prior** from the projector's datasheet (`PROJECTOR PRIOR <projector> THROW 3.02 ±2`), and the position prior when
   the projector's lens was surveyed.
2. **Pins on control points**: 8 to 15, spread over the picture and over the facade's depth.
3. **The solve.** `PROJECTOR SOLVE` (`LongThrowSolve`) first solves from the pins and the position prior. When its own information
   says the pins cannot carry the zoom (the focal's σ above 10 %), the zoom is locked at the lens prior and the solve runs again;
   the words say "the pins cannot tell zoom from distance" when the weakest direction lies along zoom and the along-axis distance
   (its share above 0.7), else "the pins cannot carry zoom". Without a lens prior the solve is kept and named weak, with the lines
   that fix it. Otherwise the lens prior joins the solve as a row. A lock the lens put there is asked again at the next solve
   without a `LOCK` word (more pins or more depth free it); an operator's `LOCK FOCAL` keeps the projector's own focal, and the
   lens prior then has nothing to weigh.
4. **Read the wall first.** The reply's `uncertainty` comes from the solve's own information matrix with the priors' added:
   `wallPx` (how far the picture could be off on the wall, p95 over the raster at the pins' depth), then `axisM` and `lateralM`
   (the projector's position along its axis and across it), `focalPct`, the condition and the weakest direction. STATE and the
   calibration score carry the same uncertainty. `locked` names the focal the lens locked; `priorsUsed` names the priors that
   shaped the solve.

`CALIBRATION RESOLVE` from pins runs this same solve; from a camera, the optics are held and a position prior joins the camera's
dots as rows, weighed against their 0.3 px.

## Scans placed on the stage

A structured-light scan through a calibrated camera bridges into the stage's model as it is placed: every visible surface on an
imported mesh, through its transform (`BridgeModel`), never a mesh file's raw coordinates. Decoded camera pixels ray-cast into that
model give thousands of 2D↔3D pairs per projector for `PoseSolver`. With no such model, or an uncalibrated camera, the scan
self-calibrates in the camera's frame and is carried into the stage by the camera rig's pose.

## Not here yet

Geodesy (UTM and geographic coordinates, CRS conversion), the facade guide, projector stacks and the light plan; LAZ, the night
capture profile, measured photometry and glass masks; a camera solved from its own pins.
