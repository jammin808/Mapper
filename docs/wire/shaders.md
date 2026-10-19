# wp3-shaders, wp3-shaders-2 on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | Lifts the quarantine on an effect kind whose shader took the GPU down, so its slots draw again (the quarantine is the node's, never a draft's) | high | yes | wp3-shaders |

## Forms added to existing rows

None.

## STATE

`fx.compile` and `fx.quarantine`, inside wp2-fx's `fx` block.

## Papers note

(What shipped, for the integration docs pass.)
