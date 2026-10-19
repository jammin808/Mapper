# wp3-desk-ux on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `UNDO LIST` | `UNDO LIST [<n 1-100>]` | The undo steps, newest first, in field words with who and when | low | no | wp3-desk-ux |
| `UNDO TO` | `UNDO TO <step>` | Undoes a step and every newer one as one action (UNDO TO the newest is one UNDO) | review | yes | wp3-desk-ux |

## Forms added to existing rows

None.

## STATE

none.

## Papers note

(What shipped, for the integration docs pass.)
