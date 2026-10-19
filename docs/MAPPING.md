# Mapping: the stage, the site and the solve

Where things sit in PlayMap and how a projector is solved into that place. The quad on the wall, the score, the ledger and
re-solving the projector that moved are in `docs/CALIBRATION.md`; surveys and control points in `docs/FACADE.md`; scanning in
`docs/ARCHITECTURE.md` §Scanning and `docs/SCAN-MULTIVIEW.md`; camera auto-align of flat screens in `docs/ALIGN.md`. The verbs are
in `docs/wire/wall.md` and `docs/wire/facade.md`.

Round 3 makes every mapping step doable by hand, from the first setup to adjusting a scan, keeps every automatic result under the
hand's edits, and gives the common jobs guides; *Not here yet*, at the end, says what that changes here.

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

A scan can take more views: `SCAN START <camera> … INTO <scan>` from wherever the camera sees the light — a capture camera, a
phone, a folder a tethered camera shoots into. Every view of every projector is then solved together, anchored on the projectors'
pixels (docs/SCAN-MULTIVIEW.md): with nothing held, the result keeps the scan's own frame and scale by its anchor view's place
(where the scan's last solve put that camera); with `SCAN SOLVE <scan> HOLD <projector>…`, the held projectors stay at their stage
poses bit for bit and the solve is in the stage's frame (metric with two held) — re-solving only the projector that moved. A
single-view scan is placed by its camera (bridged into the model when the camera is calibrated), so the views added to it inherit
that placement; `SCAN APPLY` then puts the merged mesh and every solved projector not locked for the show (naming those it
leaves) on the stage as one edit, and its score names a view that disagrees with the rest (`SCAN DROP`).

A scan's data lives in its folder, not in the project, so a site move does not reach it: a scan taken before `SITE ORIGIN` moved the
stage keeps the old frame's points. Each view of a scan keeps the site frame its place was solved in, and the scan's next view `INTO`
it, `SCAN SOLVE` or `SCAN DROP` carries its views by the move since (`StageMove.Between` the two frames) before the solve keeps the
frame by its anchor view, so the scan lands in the stage's frame now. A view placed before the site had an origin is taken as placed
in the site's frame now (the first origin moves nothing).

## Stacks

Projectors that light one picture are a stack (`StackGroup`: its members and a master). The 3D blend treats a stack as one projector
(`BlendSolver3D`): at every point a stack's weight is its master's geometry (incidence and distance), the least edge factor of its
members that see the point and their summed lumens, and every member's map gets the group's share, so stacked light adds and the
stack hands over to its neighbours as one projector.

Members placed by hand never land exactly on each other. `STACK ALIGN` fits each member's measured residual to the master's picture
from a scan that saw every member through one camera standing in one place — of a scan of many views, the view its last solve used
that saw them all, its decodes built into projector views (the smart scans of such a scan come from wherever each projector decoded
most, and camera pixels from two places do not pair) (`StackAlignment`): each member pixel the camera saw is taken to the camera,
back into the master's raster (`ViewInverse`), back through the master's own warp to the picture point its output draws there (an
operator's corner pin or lattice on the master, or a lattice it kept, is part of the picture the members land on), and from there to
where the member's render puts the same picture point (the identity when both share one model pose; through the stage's model when
their poses differ). The pairs fit a Catmull-Rom
`WarpLattice` (33 × 19 by default) — linear least squares in the node offsets, Huber-reweighted against stray decodes, a bending
penalty that leaves shifts and zooms free (a member the camera saw only in part takes the straight extension of its seen part beyond
it), conjugate gradients on the banded normal equations — and every tenth pair, held out of the fit and its reweighting, measures
the residual. A lattice whose Jacobian turns non-positive anywhere on the raster folds the picture over itself (decodes that come
back mirrored, from glass or a reflection, are consistent enough to pull it so): the fit says where (`StackFit.FoldsAt`) and
`STACK ALIGN` applies nothing. The pairs are taken back through the member's own warp, so the lattice is right under an operator's
corner pin or lattice; the fit runs off the control lane, and its result lands only while the members, their poses, optics, outputs
and warps stand as it read them. The
lattice is the member's measured correction (`MeasuredCorrection.Lattice`), drawn as the first layer
of its output's warp (`WarpField`, `WarpMeshBuilder`): before the operator's lattice and corner pin, with no shader of its own, so
the output's finished picture — what its display presents through any delay and what a send taps — carries it. It lives in the
project, so a hot backup has it with the show it mirrors. It keeps what it was fitted against (`MeasuredCorrection.FittedTo`: the
member's pose in its master's frame and both projectors' optics); `StackPoses` says when the projectors no longer stand so, and the
stack is no longer aligned until it is fitted again. A member an alignment draws through its measured warp grid is not stacked on:
that grid already registers it to its canvas. A lattice holds nothing to read from disk: align's preflight row and STATE's
`outputs[].measured` count it loaded as it stands.
`STACK MATCH` brings each member's white to the master's with per-channel gains of at most one, from the same scan's colour white
frames, through align's colour arithmetic (`StackColour`); level is left alone, because stacked light adds. Gains are never written
beside a lattice another stack fitted: the stack aligns that member first. A stack that loses a member holds, or
(`STACK FAILOVER … MATCH`) brings its blend group to the light that remains: its own members give that light already and stay as they
are, and every other output of the group dims to it (a member of another degraded stack by the light its own stack has left), through
a runtime degrade gain (`StackGains`, an `IOutputGains` composed with any other — under the hot backup's fence, so a fenced node is
black whatever it says). The gain is a fade on the leader's clock, read at the instant each frame is drawn for, and its instants move
with a step of that clock as the air's own do.

## The light plan

`LightPlan` turns the coverage analysis into light: at each sample E = derate·Φ·density²/(W·H)·U per projector (density in projector
pixels per metre on the surface, U the fall-off to the raster's corners), stacked members adding and separate projectors sharing an
overlap evenly; luminance, contrast against an ambient, pixel pitch and the lumens short of a target follow per surface.

## Coordinates on the earth

`Geodesy.cs` converts between geodetic latitude, longitude and height, ECEF and a local east-north-up frame, and projects with the
transverse Mercator by Krüger's series to sixth order in n (UTM by zone and hemisphere). `CrsInfo` knows the EPSG UTM families
(WGS 84 326zz/327zz, ETRS89 258zz, NAD83 269zz) and geographic WGS 84 and ETRS89, names a site's system ("ETRS89 / UTM zone
32N"), and reads a WKT's system from its root element's own EPSG authority (a compound system's horizontal part's) or, without one,
from a UTM name. Other systems are refused with
their words, never guessed.

## The facade guide

`SiteGuide` reads the workflow from the project: survey → control points → pins → the lens prior and the solve → stacks → blend →
score. Each step has a state (done, waiting, running, failed), words and the line that does it; the next step is the first not done.
`SITE GUIDE` answers the steps; STATE's `stage.site.next` is the live stage's next step.

## Not here yet

LAZ, the night capture profile, measured photometry and glass masks; a camera solved from its own pins; geographic
and other-zone surveys converted on import (the conversions exist; the import still refuses another system than the site's).

Round 3 adds the hand's part (`docs/round3/PLAN.md` D12, D13, §3.20; the design is `docs/round3/design/desk-workflows.md` §3, §5).
None of it is built yet. Its new rows are in the catalogue and answer "not available on this node yet".

- **A typed pose is a result** (`docs/CALIBRATION.md`'s *Not yet*). Over a pose typed by hand, `PROJECTOR SOLVE` and `SCAN APPLY`
  stage that projector's result and name the manual result it would replace.
- **A scan applied again.** `SCAN APPLY` keeps every surface's content, tags, masks and hand transforms, and every pixel quad as it
  stands, and applies every solved projector not locked for the show, but those it stages. A surface whose faces the new mesh lacks
  is kept, hidden and named in the reply, never deleted.
- **The hand, said.** `HAND <output>|<projector>|<surface>` says what a hand set over each automatic result, and `HAND CLEAR` takes
  an output's hand layer back to rest. A step that writes a value a person can also type refuses over a value typed since, naming
  each, unless the line says `KEEP` (the hand's values stay) or `REPLACE` (the reply lists what it replaced).
- **A film on a wall, seen from a seat.** Content projected from a viewpoint is built (`SurfaceMapping.Projection`, set today with
  `SET`). Round 3 adds `VIEWPOINT FIT`, which aims a viewpoint and fits its field of view to surfaces from its eye, and
  `SURFACE MAP <surface> PROJECTION FROM <viewpoint>` as the row that sets it, so the picture reads undistorted from that seat.
- **The guides.** The facade guide's shape becomes every guide's. `GUIDE` answers the flat-screen, wall, object, facade and
  first-light guides from pure planners, each step with its state, its facts and its automatic, hand and proposed lines. STATE
  `guides` lists the guides begun, and `SITE GUIDE` stays as the facade's row.

One canvas across outputs is in `docs/ALIGN.md`'s *Not yet*; every handle by one pixel, `PIN MOVE` among them, in
`docs/CALIBRATION.md`'s.
