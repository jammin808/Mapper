# wp3-desk-map on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `PIN MOVE` | `PIN MOVE <pin> TO\|BY <x> <y> [PX]` | Moves a pin's picture point to or by x, y (0–1 across its projector's picture; PX: its pixels) | review | yes | wp3-desk-map |
| `WARP CORNER` | `WARP CORNER <output> TL\|TR\|BR\|BL TO\|BY <x> <y> [PX\|NORM]` | Moves a corner of an output's corner pin to or by x, y (PX: the output's pixels; NORM: 0–1 across it) | review | yes | wp3-desk-map |

## Forms added to existing rows

- `WARP NODE <output> <c> <r> TO|BY <x> <y> PX`
- `HANDLE SELECT <pin>`, `… WARP …` and `… MASK …`

## STATE

`outputs[].showing`, `media.shownOn`, `air.overlay.handleP95Ms`; the context's output items carry the showing words.

## Papers note

(What shipped, for the integration docs pass.)
