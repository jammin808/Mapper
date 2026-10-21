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

None of its own. The desk's GO guard reads `show.minGoMs` (the window the node's fence holds now), the reply to each GO this desk
sends, and `show.lastGo` (`docs/wire/show.md`), so a GO from Companion, Patterns or another desk draws the guard on this desk for what
is left of the window.

## Papers note

The papers this merge carries (plan §5.4 item 7), each sentence checked against the merged code: `docs/DESK.md` describes each part
below, its wire-only list names the machine platform's and the effect quarantine's cards the merge brings, and its verb walk fails on
a control Map anything outlines whose line the node would not learn; `docs/UX.md` §1, §3.2, §4.1 and §4.4 say what the desk built;
`docs/CALIBRATION.md`'s round-3 list; `README.md`'s desk paragraph, its Next paragraph, the desk's module row and its papers list;
`docs/ARCHITECTURE.md` §2's desk row; `docs/WIRE.md`'s round-3 section, which names this package's rows routed and `STATE.show.lastGo`;
`docs/ROADMAP.md`'s round 3 ("Built: the consoles' safety patterns and the quad that stays", the package's two rows, the Today
paragraph), its "Not on the desk" paragraph, its lab list (L19) and an open fix (the desk keeps the last project revision it read
when it attaches to another node); `docs/COMPETITIVE.md`, `docs/RESEARCH.md` §8 and `docs/round3/README.md`; the plan's §3.6 (items
1, 4, 5 and 7, its hunks and its acceptance) and §7's benches (M2's is desk-gpu's); `design/desk-workflows.md` §6.1 and §6.2 (what
desk-ux builds against the tree they read) and §11's status channel, and `design/desk-gpu.md`'s age badge; the agent brief's
constraint on the desk's lights and its own state.

**What shipped.** The Oops history (UNDO LIST, UNDO TO) on the node and the desk's Oops panel; the GO guard, from this desk's replies
and from `STATE.show.lastGo` (a GO from any client); blind in grey with BLIND on every draft pane and the blind frame; the dark room and
daylight themes with a desk brightness; every light's shape and word, in the templates and on the canvases; preview age; the
confirmation before closing an armed desk; the four-click quad kept as a draft per projector (desk.json) with its corners dragged
(`RASTER`), typed and nudged and a pin typed and nudged; Map anything (a touch on an outlined control learns its line; the air and a
panic never; ended when armed and by Esc). `docs/DESK.md` describes each.

**The spine's part.** `STATE.show.lastGo` (§3.6 item 1) reaches main with this merge as its own "Spine: " commit (plan §5.7): the
router hands the GO verbs their session (`EngineCore.Router.cs`), an accepted GO is noted with it (`EngineCore.Transport.cs`), and
`Show/EngineCore.ShowMode.cs` writes the rows, each playlist's last accepted GO, when and from which session (`docs/wire/show.md`).

**For wp3-installer.** `ConnectionManager` takes the maker of its links, the maker of this machine's engine supervisor and the clock
its waits run on (`ConnectOwnTests` drives Connect's own-engine and network paths through them), so the installer's supervised-machine
case (its F18) hands it a supervisor that starts no engine, and reads the desk's link live on the engine already on loopback.

**The lab.** L19 is not run (no lab: the owner's answer 5); its script and pass mark are `docs/round3/lab/L19.md`. What CI proves
headless: `GoGuardTests`, `BlindLookTests`, `StatusRuleTests`, `PreviewAgeTests`, `CloseArmedTests`, `MapAnythingTests` and the
`Quad*Tests` with `PinNudgeTests`. What waits for L19: whether operators in a dark room read the guard, the shapes and the stale badge,
reach PANIC under pressure, and find their place and a faulted output after a desk restart.

**Where the desk meets the plan differently, and why.** The plan's §3.6 items 4 and 5 are amended to what the code proves and say so;
its §8.2 maps the owner's sentences to the sections that answer them and keeps no amendment rows.
- *ok, warn and fault 3:1 apart* (§3.6 item 4). The plan's pairwise rule cannot be met with lights legible on the dark panel: with the
  design's §11 tokens green `#3FB950` against amber `#D29922` is 1.01:1, amber against red `#E5484D` 1.55:1 and green against red
  1.54:1, and three lights each 3:1 from the others cannot all be 3:1 from a panel as dark as `#191C20`. The desk meets instead each
  light at 3:1 or more against every theme's panels (on `#191C20`: green 6.73, amber 6.77, red 4.37:1; the least of any light on any
  theme's panel, the blind greys included, 3.87:1, red on the dark theme's raised panel in its blind grey; the daylight theme's darkened
  lights 4.08 to 5.36:1 on its light panels), with a shape and a word beside every light (`StatusRuleTests`). Red marks a fault's light
  as well as the air, as the design's §11 status channel does.
- *Preview age* (§3.6 item 5). A picture's frame rate is read from its ring slots' stamps, and its age on the desk's clock since the
  desk read the picture: the stamps are on the node's clock, which the desk does not share, so a ring that delivers regularly but late
  is not called stale by this rule. A ring gone takes its picture away at once (the stand-in says why), so a frozen picture's reasons
  are the engine quiet or no new picture. wp3-desk-gpu, which owns `PreviewRing.cs`, may stamp slots on a clock the desk shares.

**Papers and files outside this package that the merge carries.**
- `docs/CALIBRATION.md`'s round-3 list says the quad that stays, a quad's corners and a pin's pixel are built (desk-ux; `docs/DESK.md`,
  the quad tool's row), and the rest of round 3's handles are not.
- `docs/ROADMAP.md`'s "Today" sentence says the draft stays and a quad's corners and a pin are dragged, typed and nudged, and its "Not
  on the desk" paragraph lists what the desk lacks without the closing question, which the desk asks.
- `tests/PlayMap.Engine.Tests/Fleet/Golden/context-busy.json` (outside §4's grant for this wave) is written by its own test
  (`PatternsEyeContractTests`, with `PLAYMAP_WRITE_FIXTURES=1`): its context's commands carry `UNDO TO`. The Patterns-side patch's
  copy of that golden is Patterns' to refresh, in Patterns' own repository, which this repository cannot change.
- `integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json` (the integration pass's) and
  `tests/fixtures/state/main.json`, `output.json` and `show.json` with `show.cards.json` are written by their own tests with
  `PLAYMAP_WRITE_FIXTURES=1`: `show` ends with `lastGo`, and show's cards include "Last GO".
