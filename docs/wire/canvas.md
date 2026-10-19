# wp3-canvas on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `CANVAS SPAN` | `CANVAS SPAN <canvas>\|NEW <output> <output>… [SIDE <c>x<r>] [STACK <n 2-4>] [OVERLAP <px>\|<pct>%] [KEEP\|REPLACE]` | Spans a canvas across outputs as one edit: the outputs position by position (SIDE columns x rows, STACK outputs a position), the overlap between positions, each output's rectangle and edges, recorded as the span; over a rectangle a hand moved or a measured warp, refused naming each unless KEEP or REPLACE | review | yes | wp3-canvas |
| `CANVAS SOURCE` | `CANVAS SOURCE <canvas> <source> [FIT\|FILL\|STRETCH]` | What a canvas shows as its base layer, fitted, filled or stretched | high | yes | wp3-canvas |
| `HAND` | `HAND <output>\|<projector>\|<surface>` | What a hand set over the automatic results: warp, blend, colour, masks and γ, each beside the measured value under it and the run that measured it | low | no | wp3-canvas |
| `HAND CLEAR` | `HAND CLEAR <output> WARP\|BLEND\|COLOR\|MASKS\|ALL` | Clears a hand's layer of an output, the measured layer under it staying (one undo step) | review | yes | wp3-canvas |

## Forms added to existing rows

- `PATTERN <kind>|OFF ON <canvas>`

## STATE

`outputs[].hand`.

## Papers note

(What shipped, for the integration docs pass.)
