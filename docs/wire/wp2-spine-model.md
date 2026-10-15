# Round 2 spine: the model and the wire's table

What the round-2 spine adds to the project file, to Core's shared types and to the verb table. The spine itself changes nothing
on the air: a round-2 row answers "… is not available on this node yet" until the package that owns it routes it, and each
package writes its own STATE keys. Most rows are routed: show, output, decode, optimise, control and timecode, companion,
effects, audio, content, LED, the wall, align and fleet answer the rows they build (docs/WIRE.md lists the pages; `VERBS` marks
each row available or not).

## The project file

- Round-2 fields are new keys on the first round's objects, all with defaults, plus new top-level lists and records (`show`,
  `fleet`, `control`, `timecode`, `sends`, `fxAssets`, `generatorPresets`, `audio`, `tempo`, `leds`, `optimise`; `stage.site`,
  `stage.controls`, `stage.surveys`, `stage.stacks`, `stage.aligns`, `stage.calibrations`; `fx` chains on items, layers,
  canvases, surfaces and outputs; the calibration ledger's `calibrationId`, `fallbackId` and `stagedId` on projectors and
  outputs). `schema` stays 1.
- A file written before round 2 loads unchanged and re-saves with every new key (`tests/fixtures/projects/round1.playmap.json`
  is such a file, written by the first round's own code).
- A `null` read where a nested record belongs reads as that record's `Default`, `Empty`, `None` or `Auto`; a `null` in a file for
  a number, a flag, an enum or a non-nullable string that is set after construction reads as its declared default, while `SET`
  refuses it ("'latencyMs' does not take null"). A field declared nullable keeps its `null`. A key given twice, or in another
  case, keeps its last value, in a file, a `SET` value and an `ADD`'s JSON alike.
- A projector's solve priors name the throw ratio `throw` and its uncertainty `throwSigmaPct` (`throwRatio` is a lens's derived
  value and is never stored).
- New id prefixes: `ct` `bd` `tc` `sd` `fx` `fa` `gp` `ln` `lf` `lt` `mv` `cp` `sv` `sk` `al` `cs` `fm`; node-local runtime ids
  (never in a file): `pf` `oj` `ar` `kt` `ph` `sg` `dp` `tk`.

## Generic edits on the new kinds

`GET`, `SET`, `MOVE` and `REMOVE` reach every new entity by id, including effect slots inside any chain and versions inside a
clip. `ADD` makes `binding`, `controlsurface`, `timecodesource`, `send`, `lednode`, `fixturetype`, `fixture`, `controlpoint`,
`stack`, and `fx` (owned: `ADD fx <owner> {json}` or `"parent"` in the JSON). It refuses the kinds only their own verbs make, with
the verb: `fxasset` "use FX IMPORT", `generatorpreset` "use GENERATOR SAVE", `survey` "use SURVEY IMPORT", `align` "use ALIGN NEW",
`calibration` "calibration results are made by solves", `version` "use MEDIA OPTIMISE", `fleetmember` "use FLEET BACKUP on the
backup". A calibration result takes `SET` of `name` and `pinned` only ("a calibration result is never edited — CALIBRATION
RECALL makes a new one"). `REMOVE` refuses an entity something still names (a ledger result, a played version, a stack's
projector, a quad's projector, an align group's camera, canvas or outputs, an LED fixture's type, node, space or viewpoint, a send's
tap, a generator's input, a live input's fallback, a panic plan's safe look, a chased timecode source, a surveyed control point). A new send and a new LED node start off.

## The verb table

- Every row has a show class (`VERBS` field `show`), per-word risk rules (`riskWhen`), the round it belongs to (read through
  `available`), and the flags `ephemeral`, `gesture`, `pairedOnly` and `internal`. Internal (node-to-node) rows are left out of
  `VERBS`, HELP's list and the context's commands.
- A row's syntax is one or more forms separated by a top-level ` | `; each form starts with the verb's name (`VERBS` field
  `forms`). The assistant's gate passes a line that fits any form and, when none fits, says what the closest form lacks.
- A line's risk: its row's, set by the first matching per-word rule (`riskWhen`), raised to high by `HighFor`, and at least the
  risk of every line the verb could be read to carry (the words after each `TO` of `BIND ADD … TO <line>`, `BIND LEARN [FROM x]
  <line>`, every JSON `"target"` in any case; a JSON argument no reader takes is high). A per-word rule
  never lowers a line of class air, transport, panic or device power, and every air or panic row is high. A line no verb names
  is high.
- `available` is true for the first round's rows; a round-2 row becomes available when its owner routes it.
- `TC` reads as `TIMECODE`, as `VT` and `CLIP` read as `VIDEO`.
- The context's commands put panic, air, transport and Show Mode verbs first (the Eye keeps 64). Its links use only the Eye's
  eleven verbs (`feeds`, `shows`, `drives`, `carries`, `controls`, `mirrors`, `follows`, `hears`, `routes`, `serves`, `sends`).

## Forms

A row with sub-forms writes each as a separate form after a top-level ` | ` (`TIMECODE SET`, `TIMECODE SOURCE`, `TIMECODE
INTERNAL`, `AUDIO LISTEN`, `PROJECTOR PRIOR … AT`, `SUBSCRIBE THUMBS`), and a quad corner reads as `TL|TR|BR|BL|0|1|2|3`.
