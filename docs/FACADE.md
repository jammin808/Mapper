# Facades: sites, surveys, control points, long throws

PlayMap takes a building from a survey to calibrated projectors at 50–150 m. The verbs are in `docs/wire/facade.md` and `docs/wire/stacks.md`; the design is
`docs/round2/design/facade.md`.

## The site frame

A project has one site frame (`Stage.Site`): a coordinate system (`EPSG:<code>` or `LOCAL`), a grid origin (E, N, H in metres, double),
a rotation, a grid scale and the venue's mains frequency. Stage coordinates are the grid's rebased: +X east turned by the rotation,
+Y up, +Z grid south. Every reader takes `GeometryReadOptions` (unit, Z-up or Y-up, the frame) and rebases each point in double before
narrowing to float, so a facade 5.4 million metres north of its grid's origin keeps its millimetres (a float there steps 0.5 m). A site
is at most 2 km across. Earth curvature (0.8 mm at 100 m) is ignored.

The first georeferenced import, or the first control points, set the frame at their centre rounded to 10 m; `SITE ORIGIN` sets it by
hand. Moving the frame moves the stage with it in one edit (surfaces, projectors, cameras, viewpoints, pins, priors, the calibration
ledger's poses, LED fixtures placed in the world), so grid positions and every pin's reprojection are kept; scans taken before keep their own frame's points (the
words say so) until a view `INTO` one, or `SCAN SOLVE` or `SCAN DROP` of it, carries its views into the new frame (`docs/MAPPING.md`). Setting the first frame moves nothing: the stage's coordinates are taken as relative to it. Moving a site from one
EPSG system to another is refused (that is a reprojection).

## Survey import

`SURVEY IMPORT` answers at once and works on its own thread below the lane's priority; the lane is touched twice (to accept and to
apply), each slice timed into the import's view by two measures. One is the work the lane thread did in it (`ThreadTime`: the
thread's own processor time, never the time a loaded machine gave others; the view's `LaneMaxMs`, the longest slice by it) beside
the step that clock moves in (`LaneResolutionMs`). Linux and macOS read the thread's CPU clock, precise in the step it states (1 ns
on Linux; `clock_getres` on macOS). Windows reads the thread's kernel and user time (`GetThreadTimes`), which it charges in whole
clock ticks (15.625 ms by default), a tick to the thread a clock interrupt finds running, so a stretch the thread runs unbroken
reads within a tick of its work; it is not precise, and `Basis` says so and why the thread's cycles are not converted: Windows
leaves interrupt time out of them, so their rate against the stopwatch moves with the machine's load (by more than a tenth on a
Hyper-V guest), and no rate measured once converts them. The other is the stopwatch (`LaneMaxWallMs`): the slice's span, which holds
its work and whatever else the machine did meanwhile, and never reads below the work. Each bounds a slice's work from above
(Windows' ticks to a tick for each stretch the thread ran the slice in), so a slice held the lane for no more than a budget where
its thread time plus the step, or its stopwatch time, is within it. The slice budget's tests prove their budgets so, and a slice
proven by neither fails with both readings, the step and `Basis`: on Linux the CPU clock proves them; on Windows a slice read as no
tick or one, plus a tick, is within the regular test's 4 × 10 ms, and the Perf twin's 10 ms, shorter than a tick, is proven by the
stopwatch, which in a test run alone reads the slice's work and little else. How long the lane answers meanwhile is measured as such
(SITE's latency).
The result lands as one edit, so one UNDO takes the whole import back.

- **Meshes** (OBJ, PLY, glTF): rebased as read, then decimated to the triangle budget by quadric edge collapse. A collapse keeps one of
  its edge's own vertices, so no vertex moves; boundary, seam and non-manifold vertices are never removed; a collapse that would make
  the mesh non-manifold, turn a face by more than 60°, or give a vertex more than 16 neighbours is refused. Copies of a vertex
  with the same attributes are one vertex, so an unindexed or flat-shaded file decimates like an indexed one. Collapses stop at the
  budget or when the next one's quadric error passes the deviation limit (5 mm). The deviation reported is measured afterwards — at
  every vertex of the survey and of the result and at every face centre of the result, within 10 µm — and the words say when it
  passes the limit or the budget was not met.
- **Clouds** (LAS, E57, XYZ/PTS/CSV, PLY or OBJ without faces): read in chunks (LAS streams; memory follows the occupied voxels) and
  thinned to one centroid per voxel. The thinned cloud is kept in grid coordinates as a double-precision PLY (`clouds/<sv>.ply`), so it
  stays exact whatever the frame becomes; SITE CHECK uses it when a survey has no mesh.
- **E57** is read in managed code (the whole file, scan after scan, before thinning) from the format libE57Format writes: every 1024-byte page's CRC-32C is checked (a damaged page is
  named), the XML is read with no DTD and no resolver, and each scan's compressed vector is decoded with the bit-pack codec (Float
  single and double, ScaledInteger, Integer; cartesian or spherical; invalid states; colour; row and column indices). Each scan is
  moved into the file's frame by its pose. A structured scan is meshed on its own grid, one station at a time. Images are not read.
- **Damaged and hostile files** are held to what they hold: an E57 scan's record count to the bits its section can carry (a scan
  whose fields are all constant carries none), its offsets and XML to the file's length, and a structured grid to its records; a
  LAS header shorter than its own fields, a count past what the file holds, or extended records past its end are refused or
  passed over with words before anything is allocated for them.
- **Coordinate systems** come from the argument, else the file (LAS's WKT or GeoKey projection record; E57's coordinate metadata),
  else LOCAL. A file in another EPSG system than the site's, or whose points are more than 2 km from the site's origin, is
  refused: reprojection is not done here. LAS points flagged withheld are left out.

A stopping node starts no import, and one running stops at its next read or step, unapplied; the node's disposal waits for it to
have ended, so no import outlives the node. The import's model lands on the live stage of the project it was asked for, in the
folder it was asked in (another project opened, or the project saved elsewhere, while it ran fails it with words): a lock that holds
the stage refuses it before the file is read, and so does a session that holds a blind draft (heavy work is never staged). An import
that fails for any reason is shown failed with its words, and the files it wrote are removed; file names are never reused.

## Control points

`SITE POINTS` reads total-station files (PENZD, PNEZD, ENH, NEH; any separator, and decimal commas in a semicolon- or tab-separated
file; header sniffing; m, cm, mm, ft, US survey ft) into
control points (`cp…`, σ 1 cm). A pin may take its world point from a control point (`CalibrationPin.ControlId`), and a projector's
position prior may sit on one; both are re-derived whenever SITE POINTS or the site changes them, in the same edit. A bound pin set
away from its point is refused with the world point it must have; a bound point moved by hand is refused (SITE POINTS moves it with
what is bound to it); and PROJECTOR SOLVE refuses a bound pin that is not at its point, with the line that puts it there.

`SITE CHECK` measures the model against the points: each point's distance to the model's surface (green within max(2σ, 2 cm), amber
within 5 cm, red beyond) and the similarity that best moves the model onto them — "shifted 20 cm", "turned 0.3°", or a scale beyond the
grid's own named as grid against ground metres. Preflight's `site` section runs the same measure.

## Long-throw solves

At 100 m a projector's pins cannot tell zoom from distance: on a flat facade seen head-on not at all, and with 2 m of relief only to
±3.7 % in zoom and ±3.7 m along the axis while the picture is still within 1.5 px. `PROJECTOR PRIOR` gives the solve what it cannot
see: the lens's throw ratio (±2 % by default) and the projector's position (a control point or coordinates, ±5 cm by default). They
are residual rows beside the pins' (`PosePriors`, consumed by the refinement only), so a solve without priors is bit for bit the solve
it always was.

`PROJECTOR SOLVE` first solves from the pins (and any position prior). When the pins cannot carry the zoom — its σ from the solve's
own information passes 10 % — the zoom is locked at the lens prior and the solve runs again; the words say "the pins cannot tell zoom
from distance" when the weakest direction lies along zoom and the along-axis distance, else "the pins cannot carry zoom". With no lens
prior the solve is kept and named weak, with the lines that fix it. Otherwise the lens prior joins the solve as a row. The projector
keeps who put its focal lock there (`focalLock`: `operator` or `lens`, and the ledger puts it back with a result): a lock the lens put
there is asked again at every solve (more pins or depth free it); an operator's lock — `LOCK FOCAL` on the line, or the locks set by
hand — holds at every solve and re-solve, wherever its focal sits, the lens prior's value included. A file that does not say holds the
lock as the operator's. The reply carries the uncertainty from the solve's own information matrix with the priors' added: on the wall
first (px), then along the axis and across it (m), and the zoom (%). STATE and the calibration score carry that same analysis, made
once per solve: the pins' information with the projector's priors, a locked zoom held. The solve and its score run off the control
lane; the projector is read and its result lands on the lane, a result refused when the projector changed while it was solved.

The recommended path at a facade: the lens prior from the datasheet → 8–15 pins on control points → solve, wall px read first.

A re-solve is a solve: `CALIBRATION RESOLVE` from pins runs this same solve (priors, the lens lock asked again, the bound-pin check)
and keeps the locks it answers with the result; from a camera, the optics are held and a position prior joins the dots as rows,
weighed against their 0.3 px noise (docs/CALIBRATION.md).

## Show Mode, blind edit and the group

The site's queries (`SITE`, `SITE CHECK`, `SURVEY LIST`) run for everyone. Its edits (`SITE ORIGIN`, `SITE MAINS`, `SITE POINTS`,
`PROJECTOR PRIOR`, a generic edit of a control point or a survey) are mapping: refused while armed for a person, proposed by an
assistant, and held by `LOCK AREA mapping` and by a lock on the point or survey itself. From the session that holds the blind draft
they run into the draft and say "staged" until `BLIND COMMIT` lands them, and its `SITE` and `SITE CHECK` read the draft (a draft's
check is never taken as the stage's by the context or preflight). `SURVEY IMPORT` is heavy work: refused while armed, and never
staged. A sync group shares none of them: each node keeps its own site. A survey is no media: the show's media, preflight's
media rows and the optimiser never see its files, and `MEDIA IMPORT` refuses a survey or model file (OBJ, PLY, glTF, LAS, LAZ, E57,
XYZ, PTS) with the `SURVEY IMPORT` line that reads it.

## Scans bridge into the stage as placed

A scan with a calibrated camera bridges into the stage's model as it stands: every visible surface on an imported mesh, through its
placement — never a mesh file's raw coordinates.

## Coordinate systems

A site's system is named as the EPSG dataset names it ("ETRS89 / UTM zone 32N"); a LAS file's WKT record gives its system from the root
element's own EPSG authority (a compound system's horizontal part's; an authority nested in a unit, a datum or the base system names
that, not the system) or, when it has none, from a UTM name ("WGS_1984_UTM_Zone_33N"). The geodesy beneath (`Geodesy.cs`) is
from published specifications: geodetic ↔ ECEF ↔ local east-north-up, and the transverse Mercator by Krüger's series to sixth
order, within a millimetre of Lantmäteriet's SWEREF 99 TM control points, the Ordnance Survey's worked example and EPSG's
Luxembourg example, and within a micrometre forward and back over a UTM zone.

## Stacks

Two or more projectors lighting one picture are a stack: `STACK NEW` groups them with a master (the first named, or `MASTER`). A
member must light at least 80 % of the master's picture (where the master's raster lands on the model, or on a wall at its throw
without one) and may be in one stack only. The blend treats the stack as one projector whose light is its members' summed.

`STACK ALIGN <stack> FROM <scan>` fits each member to the master's picture — as the master's output draws it, through its own corner
pin or lattice — from a scan that saw every member through one camera.
Its pairs compare camera pixels, so they come from one camera place: of a scan of many views, the view its last solve used that saw
every member (the reply names it); a scan with no such view is refused with the line that adds one (`SCAN START … INTO`). A site
moved between the scan and the alignment fits the same lattice. The master is the target and its output is left as it is. The fit is a residual lattice per member, taken through the member's own
warp (so a corner pin or lattice on it keeps the registration), written to the member's output as its measured correction through the calibration ledger
(source "stack", the correction it had kept as its fallback; one edit, one undo step), and drawn as the first layer of the output's
warp. A fit that would fold a member's picture over itself (decodes that come back mirrored, from glass or a reflection) is refused
with where, and so is one whose members, poses, optics, outputs or warps changed while it ran: nothing is applied. The reply gives each member's held-out residual before and after, in pixels; a member farther than 0.5 px from the master lights
the stack amber. `STACK MATCH <stack> FROM <scan>` gives each member per-channel gains of at most one that bring its white to the
master's, from the scan's colour white frames over the area every member lights, written beside the lattice — never beside a
lattice another stack fitted (the stack aligns that member first). The white frames carry the
ambient light as well, the same for every member: a dark scene matches best. The measured lattice is drawn on the air; the wall's
overlay, the click mapping and MPCDI export follow the operator's warp alone; what a send or an LED map taps and what a delayed
display presents carry it, being the output's finished picture. A projector's own geometry or blend left on beside a measured
correction is named by align's double-correction guard, and a member that an alignment already draws through its measured warp grid
is refused (a lattice on it would register it twice): `ALIGN CLEAR` takes that warp off, or `ALIGN RUN` registers the stack on its
canvas.

A lattice is the measured light less the modelled pictures, so it holds only while the member stands against its master as it was
fitted (the member's pose in the master's frame and both projectors' optics are kept with it). Once either is solved again to
another pose or other optics — a re-solve, a scan applied, a result recalled, a pose set by hand — the stack is no longer aligned:
STATE, the context, preflight and the guide say so with the line that fits it again (a scan taken since, if it moved), and the solve's
reply names the stack it left. A move of the whole site, or a result recorded as it stands, leaves it aligned.

A scan keeps each view's decodes in its folder, so a stack aligns and matches from it after a restart, or on a hot backup that
mirrors the show, as on the node that scanned.

`STACK FAILOVER <stack> HOLD|MATCH` says what happens when a member fails — its projector in standby or cooling down, or reporting an
error, or its display output not open (a closed shutter is no loss: shutters are closed on purpose). HOLD (the default) tells it: the stack is red
in STATE, the context and preflight, and the journal names the cause. MATCH also dims the rest of the stack's blend group to the light
that remains (Σ remaining lumens ÷ Σ lumens) — the stack's own members give that light already and stay as they are — so the
building stays even: a runtime gain like BLACKOUT, never an edit, fading down in 0.5 s and back over 2 s on the leader's clock when the
member returns (read at the instant each frame is drawn for, and moved with a step of that clock), multiplied with any degrade gain
another part sets.
A hot backup's fence is one: the node composes the fleet's fence over the stacks' gains, so a fenced node is black whatever its stacks
say, and the stacks' gains hold once the fence lifts (docs/FLEET.md).

## The light plan

`PLAN LIGHT` predicts each surface's light from the projectors as placed: at every coverage sample, each projector's lumens spread
over its raster, scaled by its pixel density there (so a grazing or far surface gets less), falling off to the raster's corners;
stacked members add, separate projectors share an overlap. It answers lux at p5/p50/p95, nits (albedo 0.5 by default), contrast
against an ambient, pixel pitch and the lumens short of a target. One 30 000 lm 4K projector at throw ratio 3 from 100 m, head-on,
gives 48 lux and an 8.7 mm pixel; a double stack 96.

## The facade guide

`SITE GUIDE` (or `FACADE GUIDE`) lists the facade's steps — survey, control points, pins, the lens prior and the solve, stacks, blend,
score — each with its state and the line that does it, and names the next. STATE's `stage.site.next` carries the next step, so the
desk and Companion can drive the workflow with the verbs everyone uses.

The guide asks for attention only once the facade work has begun: a site set, a survey (importing, imported or failed) or a control
point. Projectors, pins, solve priors, an imported model, scans, blends and stacks serve every rig and do not begin it: any rig's
weak solve asks for the lens's throw prior, a position prior at a control point needs the control point, and a stack's own keys light
what it needs. INDEX's `NEXT` key is grey with the next step named until then — the survey or the control points, since every later
step needs three control points — so a playback or mapping node's deck shows no standing amber; amber while a step is left; green
through. STATE's `next` is there only with a site, which begins the work.

## Not here yet

Measured photometry against the light plan, and a heat map of it in the editor view; LAZ, the night capture profile, glass masks,
camera solves from photos and the desk's facade drawer; geographic or other-zone surveys converted on import.
