# wp2-facade on the wire

Sites, surveys, control points and long-throw solves (`docs/FACADE.md`). A `<projector>` is a projector's id (`pj…`) or name; a
`<control>` is a control point's id (`cp…`) or name; a `<survey>` is a survey's id (`sv…`) or name; a `<path>` is a file on the node's
machine. Grid coordinates are easting, northing, height in metres; stage coordinates are the stage's own (+X east turned by the site's
rotation, +Y up, +Z grid south).

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `SITE` | `SITE` | JSON `{crs, crsName, origin:[e,n,h], rotationDeg, gridScale, mainsHz, set}` | low | no |
| `SITE ORIGIN` | `SITE ORIGIN <e> <n> <h> [CRS <code>\|LOCAL] [ROTATE <deg>]` | JSON `{origin, crs, rotationDeg, moved:[dx,dy,dz], staged, words}`. With no site set, the stage's coordinates are taken as relative to the new origin and nothing moves. With one set, the whole stage moves in one edit (surfaces' placements, projectors', cameras' and viewpoints' poses, pins, position priors, the calibration ledger's poses, LED fixtures placed in the world) so every grid position is kept and every pin's reprojection is unchanged; pins and priors bound to control points are re-derived in the same edit. `CRS` takes `EPSG:<code>` or `LOCAL` | review | yes |
| `SITE MAINS` | `SITE MAINS 50\|60` | JSON `{mainsHz, staged}` | low | yes |
| `SITE POINTS` | `SITE POINTS <path> [ORDER PENZD\|PNEZD\|ENH\|NEH] [CRS <code>] [UNITS m\|cm\|mm\|ft\|usft]` | JSON `{added, updated, ids:[…], skipped:[{line, why}], order, orderFrom, staged, words}`. Comma, semicolon, tab or space separated; the order from the argument, else a header ("Point, Northing, Easting, Elevation, Code"), else PENZD. A point already in the project under its name is moved (and what is bound to it follows). With no site set, the site is set at the points' centre rounded to 10 m. Off the lane; one undo step | review | yes |
| `SITE CHECK` | `SITE CHECK [<surface>\|<survey>]` | JSON `{points:[{id, name, distanceM, light, words}], fit:{shiftM, rotDeg, scalePpm}, light, words}`: each enabled point's distance to the model's surface (every visible surface by default; a survey without a mesh: its thinned cloud), green within max(2σ, 2 cm), amber within 5 cm, red beyond; the similarity that best moves the model onto the points. Off the lane | low | no |
| `SURVEY IMPORT` | `SURVEY IMPORT <path> [CRS <code>\|LOCAL] [UNITS m\|cm\|mm\|ft\|usft] [UP Z\|Y] [VOXEL <m>] [DECIMATE <triangles>\|OFF] [NAME <name>]` | accepted at once: JSON `{stage:"accepted", survey:"sv…", effect}`. A worker reads OBJ, PLY, glTF/GLB, LAS 1.0–1.4, E57, XYZ/PTS/CSV; the result lands as one edit (one UNDO takes it back): a mesh asset (`meshes/<sv>-<name>.ply`) with a surface over all of it, the survey with its provenance, and — with no site set — the site the import picked. Defaults: UP Z (glTF: Y), VOXEL 0.01, DECIMATE 400000 with a 5 mm deviation limit (the notes say when the measured deviation passes it or the budget is not met). LAZ is refused with words. A failed import is shown failed with its words and its files removed. One import at a time; a stopping node starts none; refused at once, before the file is read, when a lock holds the stage (`LOCK AREA mapping`) and from a session that holds a blind draft (heavy work is never staged); the result lands on the live stage | review | yes |
| `SURVEY LIST` | `SURVEY LIST` | JSON `{surveys:[{id, name, format, state, points, triangles, mesh, path, hash, crs, cloud, voxelM, decimationErrorM, importedAt, notes}]}` | low | no |
| `PROJECTOR PRIOR` | `PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>]`, `… AT <control> [±<m>]`, `… AT <x> <y> <z> [±<m>]`, `… CLEAR` | JSON `{priors:{throw, throwSigmaPct, position, controlId, positionSigmaM}, staged, words}`. Defaults ±2 % and ±5 cm. A throw keeps the position and a position keeps the throw | review | yes |
| `PROJECTOR SOLVE` | unchanged grammar | adds `uncertainty:{wallPx, proudPx, focalPct, axisM, lateralM, condition, weak}` (with the priors' information), `locked:["focal"]` when the zoom was locked from the lens (asked again at the next solve without a LOCK word), and `priorsUsed:["throw","position"]` (the priors that shaped the solve: under an operator's `LOCK FOCAL` the lens prior has nothing to weigh and is not named); refused when a bound pin is not at its control point, with the `SET <pin> world …` line that puts it there | review | yes |

`CALIBRATION RESOLVE` (docs/wire/resolve.md) solves with the same priors: from pins as `PROJECTOR SOLVE` does with no LOCK word
(the zoom locked from the lens when the pins cannot carry it, the locks it answers kept with the result; a bound pin away from its
point refused with the `SET` line), and from a camera with a position prior as rows beside the dots (the optics are held). Each
row of its reply carries `priorsUsed` and `locked`.

**Show Mode, blind edit, the group.** `SITE`, `SITE CHECK` and `SURVEY LIST` are queries; `SITE ORIGIN`, `SITE MAINS`, `SITE POINTS`,
`PROJECTOR PRIOR` and a generic edit of a `cp…` or `sv…` are mapping (refused while armed for a person, proposed by an assistant);
`SURVEY IMPORT` is heavy work (refused while armed but for the node itself). From the session that holds the blind draft the mapping
lines run into the draft: `staged` is true and the words begin "staged in the blind draft:", while STATE, `PROJECT GET` and the air
keep the live site until `BLIND COMMIT` lands it; that session's `SITE` and `SITE CHECK` read the draft (the words of a draft's check
begin "in the blind draft:", and the `site` context item and preflight never take it). A sync group shares none of them: each node
keeps its own site.

`STACK ALIGN` stays in the catalogue unrouted (the calibration score's stack term names it); `SITE GUIDE`, `PLAN LIGHT` and the other
`STACK` verbs are wp2-stacks'.

**Refusals** leave the project unchanged: a value that would change nothing ("the site is at … already", "the mains are 50 Hz
already", "all 14 points are in the project as they are", "P1's priors are that already"); a coordinate system that is neither
`EPSG:<code>` nor `LOCAL`; a file in another EPSG system than the site's ("reproject it in your survey software"); a file wider than
2 km ("split the site"); a control file naming a point twice (both lines named); no project folder ("PROJECT SAVE it first"); a
survey import from a session holding a blind draft, or under a lock that holds the stage (the lock's words); a
second import while one runs; a file in another EPSG system than the site's, or 2 km or more from its origin; SITE ORIGIN from one
EPSG system to another; a pin bound to a control point that is not there or not at it; a bound control point moved by SET.

**Generic edits.** `ADD controlpoint <json>`, `SET <cp…> enabled false` (takes a point out of SITE CHECK and preflight), `SET <pin> controlId
"<cp…>"` (the control point must exist), `REMOVE`. `ADD survey` is refused: SURVEY IMPORT makes them.

## STATE

`stage` (after today's keys):

```json
"site": null | {"crs": "EPSG:25832", "name": "", "origin": [512340, 5401230, 120], "rotationDeg": 0, "gridScale": 1, "mainsHz": 50},
"controls": 14,
"surveys": [{"id": "sv…", "name": "North front", "format": "ply", "state": "importing 42 %|ready|failed: …", "points": 1002001,
             "triangles": 2000000, "mesh": "ms…"|null}]
```

`stage.projectors[]` (before the wall's keys): `"uncertainty": null | {"wallPx": 0.78, "axisM": 0.02, "focalPct": 0, "weak": ""}` (the
calibration score's, which is the solve's: the pins' information with the projector's priors, a locked zoom held),
`"priors": "TR 3.02 ±2 % · at CP7 ±5 cm" | ""`.

## Context

`site` (when a site is set, a survey is georeferenced or there are control points): amber when a georeferenced survey has no site,
the light of the last SITE CHECK while the project has not changed since, grey (unknown) once it has. `survey:<id>`: amber while importing, red when failed.

## Preflight

Section `site`: the model measured against the enabled control points now. Green within tolerance (or no control points, or no model
yet, each said); red or amber with the worst point; the fix is `SET <cp…> enabled false` when one point alone disagrees, else
`SITE CHECK`.
