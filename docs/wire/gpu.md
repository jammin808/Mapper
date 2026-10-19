# wp3-d3d12-core on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `GPU` | `GPU [STATUS]` | The render device: its API, adapter, feature level and shader model, how its start went stage by stage, its epoch and its removals | low | no | wp3-d3d12-core |

## Forms added to existing rows

None.

## STATE

top level `render`; `machine.present.api` reads "d3d12" on this backend.

## Papers note

(What shipped, for the integration docs pass.)
