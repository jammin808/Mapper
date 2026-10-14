# wp2-wall on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `LIGHT WALL` | `LIGHT WALL <display>\|#<n>\|<output> [AS <name>] [THROW <ratio>]` | First light in one line: an output full screen on that display as a projector, with the edit overlay and the wall cursor on | high | yes |
| `SURFACE QUAD` | `SURFACE QUAD <projector>\|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX\|RASTER] [AS <name>] [SOURCE <source>]` | Draws a surface as four corners in a projector's picture, clicked on the wall in any order; answers its id | review | yes |
| `SURFACE CORNER` | `SURFACE CORNER <surface> TL\|TR\|BR\|BL\|0\|1\|2\|3 TO <x> <y> [PX\|RASTER] \| SURFACE CORNER <surface> <corner> BY <dx> <dy> [PX\|RASTER]` | Moves a quad's corner (BY: wall pixels by default) | review | yes |
| `SURFACE LATTICE` | `SURFACE LATTICE <surface> <c>x<r>\|OFF [LINEAR\|SMOOTH\|BEZIER]` | Gives a quad a lattice on top of its corners (its shape does not move) | review | yes |
| `SURFACE NODE` | `SURFACE NODE <surface> <col> <row> TO\|BY <x> <y> [PX\|RASTER]` | Moves a quad's lattice node | review | yes |
| `HANDLE SELECT` | `HANDLE SELECT <surface> TL\|TR\|BR\|BL\|<col>,<row> \| HANDLE SELECT NONE` | The handle the wall shows enlarged and the arrows move | low | yes |
| `HANDLE NEXT` | `HANDLE NEXT` | The next handle | low | yes |
| `HANDLE PREV` | `HANDLE PREV` | The previous handle | low | yes |
| `HANDLE NUDGE` | `HANDLE NUDGE <dx> <dy> [PX\|RASTER]` | Moves the selected handle (encoders and dials) | review | yes |
| `OVERLAY` | `OVERLAY ON\|OFF\|TOGGLE\|KEEP [ON <output>\|ALL] [LAYERS <cursor,outlines,names,handles,lattice,pins>\|EDIT\|ALL]` | The edit overlay on the wall, drawn through each output's warp; KEEP renews this session's lease (it lapses 15 s after the last) | review | yes |
| `OVERLAY GRID` | `OVERLAY GRID [<output>\|ALL] [<seconds 0.5-30>]` | Flashes a fine grid through the warp to judge alignment | review | yes |
| `CURSOR` | `CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb] \| CURSOR OFF` | This session's cursor on the wall (0–1 across the output) | low | yes |
| `CALIBRATION STATUS` | `CALIBRATION STATUS [<projector>\|<output>\|ALL]` | Each projector's and output's score with its terms, current, fallback and staged results, and the running task | low | no |
| `CALIBRATION HISTORY` | `CALIBRATION HISTORY <projector>\|<output> [<n>]` | Its results, newest first, with how far each moved the picture | low | no |
| `CALIBRATION SAVE` | `CALIBRATION SAVE <projector>\|<output>\|ALL [AS <name>]` | Keeps the current calibration as a pinned result | review | yes |
| `CALIBRATION RECALL` | `CALIBRATION RECALL <snapshot>` | Puts a kept result back (recorded as a new result) | review | yes |
| `CALIBRATION ACCEPT` | `CALIBRATION ACCEPT <projector>\|<output>\|ALL\|<snapshot>` | Accepts the current (or a staged) result: it becomes the fallback | review | yes |
| `CALIBRATION FALLBACK` | `CALIBRATION FALLBACK <projector>\|<output>\|ALL` | Back to the fallback result, exactly | review | yes |
| `CALIBRATION FORGET` | `CALIBRATION FORGET <snapshot>` | Forgets a kept result (never the current, fallback or staged one) | review | yes |
| `CALIBRATION RESOLVE` | `CALIBRATION RESOLVE <projector>… [CAMERA <camera>] [STAGE]` | Re-solves only the projectors named from a fresh capture of each against the stage, scores and applies it (or stages it when it fails the gate); the others are not touched | high | yes |
| `CALIBRATION CANCEL` | `CALIBRATION CANCEL` | Stops a calibration task and gives the outputs back | low | yes |

## STATE

top level `calibration`, `onboarding`; `air.overlay`; `stage.projectors[].grade`, `quads`.

## Papers note

(What shipped, for the integration docs pass.)
