# Facades: sites, surveys, control points, long throws

PlayMap takes a building from a survey to calibrated projectors at 50–150 m. The verbs are in `docs/wire/facade.md`; the design is
`docs/round2/design/facade.md`.

## The site frame

A project has one site frame (`Stage.Site`): a coordinate system (`EPSG:<code>` or `LOCAL`), a grid origin (E, N, H in metres, double),
a rotation, a grid scale and the venue's mains frequency. Stage coordinates are the grid's rebased: +X east turned by the rotation,
+Y up, +Z grid south. Every reader takes `GeometryReadOptions` (unit, Z-up or Y-up, the frame) and rebases each point in double before
narrowing to float, so a facade 5.4 million metres north of its grid's origin keeps its millimetres (a float there steps 0.5 m). A site
is at most 2 km across. Earth curvature (0.8 mm at 100 m) is ignored.

The first georeferenced import, or the first control points, set the frame at their centre rounded to 10 m; `SITE ORIGIN` sets it by
hand. Moving the frame moves the stage with it in one edit (surfaces, projectors, cameras, viewpoints, pins, priors, the calibration
ledger's poses), so grid positions and every pin's reprojection are kept. Setting the first frame moves nothing: the stage's
coordinates are taken as relative to it.

## Survey import

`SURVEY IMPORT` answers at once and works on its own thread below the lane's priority; the lane is touched twice (to accept and to
apply), each slice timed into the import's view. The result lands as one edit, so one UNDO takes the whole import back.

- **Meshes** (OBJ, PLY, glTF): rebased as read, then decimated to the triangle budget by quadric edge collapse. A collapse keeps one of
  its edge's own vertices, so no vertex moves; boundary, seam and non-manifold vertices are never removed; a collapse that would make
  the mesh non-manifold, turn a face by more than 60°, or give a vertex more than 16 neighbours is refused. Collapses stop at the
  budget or at the deviation limit (5 mm). The deviation reported is measured afterwards and is an upper bound of the Hausdorff
  distance between the survey and the result at their vertices (within 10 µm).
- **Clouds** (LAS, E57, XYZ/PTS/CSV, PLY or OBJ without faces): read in chunks (LAS streams; memory follows the occupied voxels) and
  thinned to one centroid per voxel. The thinned cloud is kept in grid coordinates as a double-precision PLY (`clouds/<sv>.ply`), so it
  stays exact whatever the frame becomes; SITE CHECK uses it when a survey has no mesh.
- **E57** is read in managed code from the format libE57Format writes: every 1024-byte page's CRC-32C is checked (a damaged page is
  named), the XML is read with no DTD and no resolver, and each scan's compressed vector is decoded with the bit-pack codec (Float
  single and double, ScaledInteger, Integer; cartesian or spherical; invalid states; colour; row and column indices). Each scan is
  moved into the file's frame by its pose. A structured scan is meshed on its own grid, one station at a time. Images are not read.
- **Coordinate systems** come from the argument, else the file (LAS's WKT or GeoKey projection record; E57's coordinate metadata),
  else LOCAL. A file in another EPSG system than the site's is refused: reprojection is not done here.

A stopping node starts no import, and one running stops at its next step, unapplied.

## Control points

`SITE POINTS` reads total-station files (PENZD, PNEZD, ENH, NEH; any separator; header sniffing; m, cm, mm, ft, US survey ft) into
control points (`cp…`, σ 1 cm). A pin may take its world point from a control point (`CalibrationPin.ControlId`), and a projector's
position prior may sit on one; both are re-derived whenever the point or the site changes, in the edit that changes them.

`SITE CHECK` measures the model against the points: each point's distance to the model's surface (green within max(2σ, 2 cm), amber
within 5 cm, red beyond) and the similarity that best moves the model onto them — "shifted 20 cm", "turned 0.3°", or a scale beyond the
grid's own named as grid against ground metres. Preflight's `site` section runs the same measure.

## Long-throw solves

At 100 m a projector's pins cannot tell zoom from distance: on a flat facade seen head-on not at all, and with 2 m of relief only to
±3.7 % in zoom and ±3.7 m along the axis while the picture is still within 1.5 px. `PROJECTOR PRIOR` gives the solve what it cannot
see: the lens's throw ratio (±2 % by default) and the projector's position (a control point or coordinates, ±5 cm by default). They
are residual rows beside the pins' (`PosePriors`, consumed by the refinement only), so a solve without priors is bit for bit the solve
it always was.

`PROJECTOR SOLVE` first solves from the pins (and any position prior). When the weakest direction of its normalised information matrix
lies mostly along zoom and the along-axis distance and the implied zoom σ passes 10 %, the zoom is locked at the lens prior ("zoom
locked from the lens: the pins cannot tell zoom from distance") and the solve runs again; with no lens prior the solve is kept and
named weak, with the lines that fix it. Otherwise the lens prior joins the solve as a row. The reply and STATE carry the uncertainty
from the solve's own information matrix with the priors' added: on the wall first (px), then along the axis and across it (m), and
the zoom (%).

The recommended path at a facade: the lens prior from the datasheet → 8–15 pins on control points → solve, wall px read first.

## Scans bridge into the stage as placed

A scan with a calibrated camera bridges into the stage's model as it stands: every visible surface on an imported mesh, through its
placement — never a mesh file's raw coordinates.

## Not here yet

Geodesy (UTM, geographic coordinates, CRS conversion), the facade guide, stacks and the light plan are wp2-stacks'. LAZ, the night
capture profile, measured photometry, glass masks, camera solves from photos and the desk's facade drawer are next round.
