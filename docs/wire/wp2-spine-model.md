# Round 2 spine: the model and the wire's table

What the first half of the round-2 spine adds to the project file, to Core's shared types and to the verb table. Nothing here
changes what a node does on the air: every round-2 verb answers "… is not available on this node yet" until the package that
owns it routes it, and no STATE key is written.

## The project file

- Round-2 fields are new keys on today's objects, all with defaults, plus new top-level lists and records (`show`, `fleet`,
  `control`, `timecode`, `sends`, `fxAssets`, `generatorPresets`, `audio`, `tempo`, `leds`, `optimise`; `stage.site`,
  `stage.controls`, `stage.surveys`, `stage.stacks`, `stage.aligns`, `stage.calibrations`; `fx` chains on items, layers,
  canvases, surfaces and outputs; the calibration ledger's `calibrationId`, `fallbackId` and `stagedId` on projectors and
  outputs). `schema` stays 1.
- A file written before round 2 loads unchanged and re-saves with every new key (`tests/fixtures/projects/round1.playmap.json`
  is such a file, written by the first round's own code).
- A `null` read where a nested record belongs reads as that record's `Default`, `Empty`, `None` or `Auto`; a `null` for a number,
  a flag, an enum or a non-nullable string that is set after construction reads as its declared default. A field declared
  nullable keeps its `null`.
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
projector, an LED type or node, a chased timecode source, a surveyed control point). A new send and a new LED node start off.

## The verb table

- Every row has a show class (`VERBS` field `show`), the round it belongs to, and the flags `ephemeral`, `gesture`,
  `pairedOnly` and `internal`. Internal (node-to-node) rows are left out of `VERBS`, HELP and the context's commands.
- A row's syntax is one or more forms separated by a top-level ` | `; each form starts with the verb's name (`VERBS` field
  `forms`). The assistant's gate passes a line that fits any form and, when none fits, says what the closest form lacks.
- A line's risk: its row's, set by the first matching per-word rule (`riskWhen`), raised to high by `HighFor`, and at least the
  risk of a line the verb carries (`BIND ADD … TO <line>`, `BIND LEARN [FROM x] <line>`, or a JSON `"target"`). A per-word rule
  never lowers a line of class air, transport, panic or device power, and every air or panic row is high. A line no verb names
  is high.
- `available` is true for the first round's rows; a round-2 row becomes available when its owner routes it.
- `TC` reads as `TIMECODE`, as `VT` and `CLIP` read as `VIDEO`.
- The context's commands put panic, air, transport and Show Mode verbs first (the Eye keeps 64). Its links use only the Eye's
  eleven verbs (`feeds`, `shows`, `drives`, `carries`, `controls`, `mirrors`, `follows`, `hears`, `routes`, `serves`, `sends`).

## Papers note

For the integration docs pass: `WIRE.md` gains the `VERBS` fields above and the risk rule; `ARCHITECTURE.md` the file rule for
`null`; the round-2 rows whose sub-forms were joined by a bare `|` are written as separate forms (`TIMECODE SET`, `TIMECODE
SOURCE`, `TIMECODE INTERNAL`, `AUDIO LISTEN`, `PROJECTOR PRIOR … AT`, `SUBSCRIBE THUMBS`), and a quad corner reads as
`TL|TR|BR|BL|0|1|2|3`.
