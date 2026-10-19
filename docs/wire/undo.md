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

None. The desk's GO guard reads `show.minGoMs` (the window the node's fence holds now) and the reply to each GO this desk sends; STATE
names no time of the last GO, so a GO from Companion, Patterns or another desk draws no guard on this desk until the node publishes one.

## Papers note

For the integration pass.

**What shipped.** The Oops history (UNDO LIST, UNDO TO) on the node and the desk's Oops panel; the GO guard; blind in grey with BLIND on
every draft pane and the blind frame; the dark room and daylight themes with a desk brightness; every light's shape and word, in the
templates and on the canvases; preview age; the confirmation before closing an armed desk; the four-click quad kept as a draft per
projector (desk.json) with its corners dragged (`RASTER`), typed and nudged and a pin typed and nudged; Map anything (a touch on an
outlined control learns its line; the air and a panic never; ended when armed and by Esc). `docs/DESK.md` describes each.

**The lab.** L19 is not run (no lab: the owner's answer 5); its script and pass mark are `docs/round3/lab/L19.md`. What CI proves
headless: `GoGuardTests`, `BlindLookTests`, `StatusRuleTests`, `PreviewAgeTests`, `CloseArmedTests`, `MapAnythingTests` and the
`Quad*Tests` with `PinNudgeTests`. What waits for L19: whether operators in a dark room read the guard, the shapes and the stale badge,
reach PANIC under pressure, and find their place and a faulted output after a desk restart.

**Where the desk meets the plan differently, and why.**
- *ok, warn and fault 3:1 apart* (§3.6 item 4). The plan's pairwise rule cannot be met with lights legible on the dark panel: with the
  design's §11 tokens green `#3FB950` against amber `#D29922` is 1.01:1, amber against red `#E5484D` 1.55:1 and green against red
  1.54:1, and three lights each 3:1 from the others cannot all be 3:1 from a panel as dark as `#191C20`. The desk meets instead each
  light at 3:1 or more against every theme's panels (on `#191C20`: green 6.73, amber 6.77, red 4.37:1; the daylight theme's darkened
  lights on its light panels), with a shape and a word beside every light (`StatusRuleTests`). The plan's item 4 needs that amendment.
- *The GO guard's "last GO"* (§3.6 item 1). STATE publishes no last GO (the engine keeps it to itself), so the guard opens from the
  replies to GOs this desk sent. A spine amendment (§5.7) is asked for: `STATE.show.lastGo` per playlist (when, and from which
  session), written in the spine's `EngineCore.ShowMode.cs`; the guard reads it once it lands, and then a GO from any client draws it.
- *Preview age* (§3.6 item 5). A picture's frame rate is read from its ring slots' stamps, and its age on the desk's clock since the
  desk read the picture: the stamps are on the node's clock, which the desk does not share, so a ring that delivers regularly but late
  is not called stale by this rule. A ring gone takes its picture away at once (the stand-in says why), so a frozen picture's reasons
  are the engine quiet or no new picture. wp3-desk-gpu, which owns `PreviewRing.cs`, may stamp slots on a clock the desk shares.

**Papers outside this package that the pass corrects.**
- `docs/CALIBRATION.md`, its round-3 list: it says "None of it is built yet" of the quad that stays, a quad's corners and a pin typed
  and nudged; desk-ux built them (`docs/DESK.md`, the quad tool's row).
- `docs/ROADMAP.md`, its "Today" sentence: "a half-drawn quad is dropped when the person looks away, only a quad's corner nudges" is no
  longer so: the draft stays, and corners and a pin are dragged, typed and nudged.
- `tests/PlayMap.Engine.Tests/Fleet/Golden/context-busy.json` (outside §4's grant for this wave) was regenerated by its own test
  (`PatternsEyeContractTests`, with `PLAYMAP_WRITE_FIXTURES=1`): its context's commands now carry `UNDO TO`. The Patterns-side patch's
  copy of that golden must be refreshed with it.
