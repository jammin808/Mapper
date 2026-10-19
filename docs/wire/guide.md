# wp3-workflows on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `GUIDE` | `GUIDE LIST \| GUIDE FLAT\|WALL\|OBJECT\|FACADE\|FIRST [<subject>]` | The guides as steps: LIST, every guide begun on this node; FLAT, WALL, OBJECT, FACADE or FIRST, one guide's steps for its subject, each with its state, words, facts and the automatic, hand and proposed lines | low | no | wp3-workflows |
| `GUIDE BEGIN` | `GUIDE BEGIN FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject>` | Records a guide begun for its subject, with who and when | review | yes | wp3-workflows |
| `GUIDE END` | `GUIDE END FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject>` | Records a begun guide ended | review | yes | wp3-workflows |
| `GUIDE CHECK` | `GUIDE CHECK FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject> <step>` | A person marks a step they judged done (corners lined up by eye, a blend that looks right): the mark keeps the step's inputs and lapses once they move; never from an assistant's session | review | yes | wp3-workflows |
| `GUIDE UNCHECK` | `GUIDE UNCHECK FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject> <step>` | Takes a person's mark off a step; never from an assistant's session | review | yes | wp3-workflows |
| `VIEWPOINT FIT` | `VIEWPOINT FIT <viewpoint> <surface>… [MARGIN <pct>] [KEEP\|REPLACE]` | Aims a viewpoint and fits its field of view to surfaces from its eye, with a margin, recorded as the fit; over an aim or field of view typed since, refused naming each unless KEEP or REPLACE | review | yes | wp3-workflows |
| `SURFACE MAP` | `SURFACE MAP <surface> UV \| SURFACE MAP <surface> CANVAS <canvas> [<x> <y> <w> <h>] \| SURFACE MAP <surface> PROJECTION FROM <viewpoint>` | How a surface's content lies on it: by its own UVs, from a rectangle of a canvas, or projected from a viewpoint | review | yes | wp3-workflows |

## Forms added to existing rows

None.

## STATE

top level `guides`; the context's `guide` items.

## Papers note

(What shipped, for the integration docs pass.)
