# wp2-wall on the wire

The quad on the wall, the edit overlay and the calibration ledger (`docs/CALIBRATION.md`). Unless a verb says otherwise, numbers
on the wall are **wall pixels**: the output's own final pixels, after its warp and its quarter turns. `RASTER` gives the
projector raster's pixels instead (before the output's warp). A quad's corners are kept raster-normalised (0–1 of the projector's
picture), so a quad stays on its feature when the output's warp or raster changes. A `<snapshot>` is a calibration result's id
(`cs…`).

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `LIGHT WALL` | `LIGHT WALL <display>\|#<n>\|<output> [AS <name>] [THROW <ratio>]` | JSON `{output, projector, display, raster, next, words}`: the output full screen on that display in projector mode (one already there, else the first free display output, else a new one), its projector (the output's, else a new one at a nominal pose, throw 1.5 or `THROW`); the overlay on for that output with a lease, the identify card for 3 s. Lit already with the overlay on: refused with words | high | yes |
| `SURFACE QUAD` | `SURFACE QUAD <projector>\|<output> <x1> <y1> … <x4> <y4> [PX\|RASTER] [AS <name>] [SOURCE <source>]` | JSON `{id, projector, corners:{tl,tr,br,bl} (raster px), areaPx, words}`; four points in any order, ordered top-left first and clockwise; crossed corners or under 64 px² refused with the reason; a point off the warped picture refused | review | yes |
| `SURFACE CORNER` | `SURFACE CORNER <surface> TL\|TR\|BR\|BL\|0-3 TO <x> <y> [PX\|RASTER]`, `… <corner> BY <dx> <dy> [PX\|RASTER]` | JSON `{id, corner, raster, wall, words}`; `TO` is wall-normalised (0–1) by default, `BY` wall pixels; a burst on one corner is one undo step; a move to where it is, a crossing shape or a locked quad refused with words | review | yes |
| `SURFACE LATTICE` | `SURFACE LATTICE <surface> <c>x<r>\|OFF [LINEAR\|SMOOTH\|BEZIER]` | JSON `{id, lattice, shapeChangePx, words}`: a lattice on top of the corners (at rest it changes nothing; a resize samples the old offsets at the new nodes) | review | yes |
| `SURFACE NODE` | `SURFACE NODE <surface> <col> <row> TO\|BY <x> <y> [PX\|RASTER]` | as `SURFACE CORNER`, with `node:[col,row]` | review | yes |
| `HANDLE SELECT` | `HANDLE SELECT <surface> TL\|TR\|BR\|BL\|<col>,<row>`, `HANDLE SELECT NONE` | JSON `{handle, words}`; the air's, not the document's (no undo, no journal) | low | yes |
| `HANDLE NEXT` / `HANDLE PREV` | `HANDLE NEXT` | the next (previous) handle on the wall: each visible quad's corners, then its nodes, wrapping across quads | low | yes |
| `HANDLE NUDGE` | `HANDLE NUDGE <dx> <dy> [PX\|RASTER]` | as `SURFACE CORNER … BY` on the selected handle | review | yes |
| `OVERLAY` | `OVERLAY ON\|OFF\|TOGGLE\|KEEP [ON <output>\|ALL] [LAYERS <cursor,outlines,handles,lattice>\|EDIT\|ALL]` | JSON `{on, outputs, layers, words}`; `KEEP` renews the lease silently (it lapses `NodeSettings.Wall.OverlayLeaseMs`, 15 s, after the last); `names` and `pins` refused with words (not drawn yet) | review | yes |
| `CURSOR` | `CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb]`, `CURSOR OFF` | silent OK (a line with a request id is answered `OK @id`); 0–1 across the output, up to 8 trail points; one cursor per session, at most `MaxCursors` (8), each lapsing `CursorLeaseMs` (3 s) after its last line; no journal, no undo | low | yes |
| `CALIBRATION STATUS` | `CALIBRATION STATUS [<projector>\|<output>\|ALL]` | JSON `{task, targets:[…]}`, each target as STATE | low | no |
| `CALIBRATION HISTORY` | `CALIBRATION HISTORY <projector>\|<output> [<n>]` | JSON `{target, name, count, snapshots:[{id, name, at, source, score, grade, words, verifiedP95, who, parent, scan, delta:{mm, deg, wallPx}, current, fallback, staged, pinned}]}`, newest first; the delta is from the result before (−1 for the oldest) | low | no |
| `CALIBRATION SAVE` | `CALIBRATION SAVE <projector>\|<output>\|ALL [AS <name>]` | JSON `{snapshots, words}`: the calibration as it stands kept as a pinned result (never pruned) | review | yes |
| `CALIBRATION RECALL` | `CALIBRATION RECALL <snapshot>` | JSON `{target, applied, recorded, deltaWallPx, words}`: the result put back exactly, recorded as a new result (source `revert`) | review | yes |
| `CALIBRATION ACCEPT` | `CALIBRATION ACCEPT <projector>\|<output>\|ALL\|<snapshot>` | JSON `{fallbacks:[{target, fallback}], words}`: a staged result applied and made the fallback, else the current one made the fallback (a projector with none records itself as it stands first) | review | yes |
| `CALIBRATION FALLBACK` | `CALIBRATION FALLBACK <projector>\|<output>\|ALL` | as `RECALL` of the fallback (ALL: `{recorded, words}`) | review | yes |
| `CALIBRATION FORGET` | `CALIBRATION FORGET <snapshot>` | JSON `{removed, words}`; the current, fallback or staged result refused | review | yes |
| `CALIBRATION RESOLVE` | `CALIBRATION RESOLVE <projector>…\|ALL [CAMERA <camera>] [STAGE]` | JSON `{task, projectors:[{id, snapshot, applied, staged, why, score:{value, grade, rmsPx, words}, delta:{mm, deg, wallPx}, verifiedP50, verifiedP95, priorsUsed, locked}], blend, words}`; answered off the lane. Without `CAMERA` each named projector is re-solved from its pins as they stand, as `PROJECTOR SOLVE` solves with no LOCK word: its priors as rows, the zoom locked from the lens when the pins cannot carry it (`locked:["focal"]`, the words say so), a bound pin away from its control point refused with the `SET` line (docs/wire/facade.md). With `CAMERA` (a calibrated camera of the stage) every projector black, then each named projector's dots alone: each judged unmoved, moved or unseen (and every one moved together: the camera moved), only the movers re-solved from their dots (optics kept; a position prior as rows beside them) and checked once more on the wall; the reply adds `camera`, `attribution:[{id, name, verdict, dots, matched, shiftP50, shiftP95, words}]`, `frames`, `patternSeconds` (frames × the node's `wall.resolveFrameMs`) and `captureSeconds`; nothing moved, the camera moved or nothing seen is refused with the attribution's words. Applied when its score is within `max(calibrationGate, 1.25 × the fallback's)` and not F, otherwise staged (also with `STAGE`, and always for an assistant over MCP). Applied with `CAMERA`, the projector's pins take the new pose's pixels and its pixel quads are re-seated to light the wall spots they lit before. A projector that blends automatically has its blend solved again off the lane (`blend` its receipt); in a blind draft, `blend` says it is solved when the draft lands. Refused: no such camera, a camera not calibrated, a stage without a surface, a scan or an alignment showing patterns, a task running | high | yes |
| `CALIBRATION CANCEL` | `CALIBRATION CANCEL` | stops a running re-solve before it applies anything (its task `cancelled`, the re-solve answers "cancelled — nothing was applied"); once its result is being applied, refused with the way back (`CALIBRATION FALLBACK`) | low | yes |

Changed rows: `PROJECTOR SOLVE` and `SCAN APPLY` record a scored result in the same edit; their replies gain `snapshot` and
`score {value, grade, words}` (`SCAN APPLY`: per projector).

Every wall verb that changes something is refused while the show is armed, with the show gate's own words.

## STATE

```jsonc
"air": { …, "overlay": {"on":true,"outputs":["op…"],"layers":["cursor","outlines","handles","lattice"],
                        "handle":{"surface":"sf…","kind":"corner","index":0}|{"surface":"sf…","kind":"node","col":1,"row":1}|null,
                        "grid":false,"cursors":1,"cursorP95Ms":16.7|-1,"armed":false} },
"stage": { "projectors": [{ …, "grade":"A"|"", "quads":1 }] },
"calibration": {"moved":["pj…"],
                "task":null|{"id":"kt…","kind":"resolve","state":"capturing|matching|solving|verifying|done|failed|cancelled","projector":"pj…",
                             "progress":1,"words":"…"},
                "targets":[{"id":"pj…","kind":"projector|output","name":"…","score":0.2|-1,"unit":"px|points","grade":"A|B|C|F|",
                            "light":"green|amber|red|grey","source":"pins|scan|resolve|manual|align|stack","words":"A 0.2 — …","worst":"",
                            "confidence":1,"snapshot":"cs…"|null,"fallback":"cs…"|null,"staged":"cs…"|null,"history":1,"verifiedP95":-1,
                            "terms":[{"code":"rms","words":"…","value":0.13,"unit":"px","penalty":0.2,"light":"green","where":"",
                                      "fix":"CALIBRATION RESOLVE pj…","hint":"…"}]}]},
"onboarding": {"stage":"none|lit|quad|content","startedAt":…|null,"litAt":…|null,"quadAt":…|null,"contentAt":…|null,"seconds":84.2|-1}
```

`targets` lists every projector, and every output with a result. `moved` names the projectors the last camera look found moved
that still stand where they were found (a staged re-solve leaves its projector there). `cursorP95Ms` is the render host's measure
of how late a cursor move is drawn: frames from its line's arrival to the frame that first draws it, times the output's frame
period, p95 over the last 300 moves (−1 before the first). Unknown numbers are −1. The fixture is
`tests/fixtures/state/wall.json`, its cards `wall.cards.json`.

## Context, events, preflight, INDEX

- Context: `calibration:<target>` (kind `calibration`): red F, amber C or a staged result waiting, green A/B; `overlay` (kind
  `overlay`, amber) whenever the overlay is up.
- Events: `calibration.staged` (a re-solve or a ledger record staged a result), `calibration.recalled`.
- Preflight section `calibration`: each projector an output shows — red F (fix: `CALIBRATION FALLBACK`), amber C, a staged result
  (`CALIBRATION ACCEPT <snapshot>`) or no fallback (`CALIBRATION ACCEPT <projector>`), green otherwise.
- INDEX section `calibration`: a row per projector (light from its grade, state "A 0.2"); keys Re-solve (held, as its row is), Fallback (hold), History.

## Papers note

See `docs/wire/resolve.md` for what wp2-resolve added (the camera re-solve, re-seating, the cursor's latency, the first content
witnessed by the render host, the projector VIEW).
