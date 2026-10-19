# wp3-desk-ux on the wire

*The package's rows, as the catalogue holds them and the node answers them.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `UNDO LIST` | `UNDO LIST [<n 1-100>]` | The undo steps, newest first, in field words with who and when | low | no | wp3-desk-ux |
| `UNDO TO` | `UNDO TO <step>` | Undoes a step and every newer one as one action (UNDO TO the newest is one UNDO) | review | yes | wp3-desk-ux |

`UNDO LIST [<n>]` answers the newest *n* (20 unless given, 1–100) undo steps of the store the line's edits go to — the blind
owner's draft for the session that holds it, the live project for every other — as
`{"draft","count","canRedo","steps":[{"step","label","who","at","words","next"}]}`: `step` the step's own number (kept while the step
is, never another's), `label` the edit's field words, `who` the session that made it ("system" for the node's own work), `at` Unix ms
on the show's wall clock, `words` "set output Left width — Desk at 20:31:05", `next` true on the step UNDO takes back next. A count
outside 1–100 is refused with the syntax.

`UNDO TO <step>` takes that step and every newer one back through `ProjectStore.UndoTo`: one published root (the one before the
step), one revision, one journal row; each step taken back goes to the redo list, so REDO brings them back one at a time, oldest
first, and `UNDO TO` the newest step is one `UNDO`. Refused, with nothing changed: "nothing to undo" on an empty list; "no undo step
999 — they are 4 to 8 (UNDO LIST lists them)" for a step not in the list; LockGuard's words ("Wall (surface) is locked by Desk (…")
across a lock; "UNDO TO <step> — UNDO LIST names the steps" for a word that is no number. The show's gate treats it as `UNDO`:
drafted for the blind owner, the operator's while armed, a remote's refused while armed with the gate's words.

## Forms added to existing rows

None.

## STATE

None. The desk's GO guard reads `show.minGoMs` (the window the node's fence holds now) and the reply to each GO it sends; STATE
names no time of the last GO, so a GO from another client draws no guard on this desk until the node publishes one.

## Papers note

What shipped, for the integration pass: the Oops history (UNDO LIST, UNDO TO) on the node and the desk's Oops panel; the GO guard;
blind in grey with BLIND on every draft pane and the blind frame; the dark room and daylight themes with a desk brightness; every
light's shape and word; preview age; the confirmation before closing an armed desk; the four-click quad kept as a draft per
projector (desk.json) with its corners dragged, typed and nudged and a pin typed and nudged. `docs/DESK.md` describes each.
