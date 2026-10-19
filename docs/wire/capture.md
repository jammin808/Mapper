# wp3-capture on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `OUTPUT PASSTHROUGH` | `OUTPUT PASSTHROUGH <output> ON\|OFF` | Runs an output as its capture input's pass-through: one frame in flight, outside the delay ring, sampled as late as its measured render time allows | review | yes | wp3-capture |

## Forms added to existing rows

- `INPUT DISCOVER CAPTURE`
- `INPUT ADD` of a `capture:` URI with `MODE <w>x<h>@<rate>` and `FORMAT UYVY|V210|NV12|AUTO`

## STATE

`outputs[].passThrough`, `videoio.inputs[].capture`, `videoio.inputs[].latency`.

## Papers note

(What shipped, for the integration docs pass.)
