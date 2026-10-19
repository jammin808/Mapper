# wp3-present on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `PRESENT PROVE` | `PRESENT PROVE [<output>\|ALL] [FOR <seconds 2-60>]` | Measures each output's presentation over a window (10 s unless FOR) and answers its mode with the evidence: proven when the window met every vblank at the display's own rate, one frame from present to display | low | yes | wp3-present |

## Forms added to existing rows

- `PRESENT PATH D3D12` (the row's D3D12 path)

## STATE

inside the existing `outputs[].present` and `machine.present` (wp2-output's).

## Papers note

(What shipped, for the integration docs pass.)
