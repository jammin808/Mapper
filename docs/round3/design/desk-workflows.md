# Design: the desk's workflows — `wp3-desk-shell`, `wp3-desk-map`, `wp3-canvas`, `wp3-workflows` (and desk-ux's quad)

*How the desk will give a person a simple path through the three jobs most shows start with — two projectors on a flat screen, a
film on a building seen from the audience, an object mapped with one or two projectors — while every advanced tool stays one
click away; how every mapping step will be doable by hand from the first setup, and every automatic result adjustable by hand
afterwards without a re-run ever overwriting it in silence; how the assistant will take part in every step through the same
verbs, STATE and context the desk uses; how the four-click quad will stay put and every corner and pin will move by one pixel; how
each screen will say what it shows; how one picture will span several outputs; and how the desk's panels, menus and colours will
carry all of it. Package sections: `PLAN.md` §3.6 (desk-ux's item 6, the quad), §3.17 (desk-shell), §3.18 (desk-map), §3.19
(canvas) and §3.20 (workflows). Today's desk is `research/desk-today.md`, cited **[today §x]**; the field's workflows, panels,
colour and latency lessons are `research/workflows_ux.md`, cited **[ux §n]**.*

**The decision in one paragraph.** The three workflows are **guides**: engine-side step lists, pure functions of the project and the
node's measured facts, the shape `SITE GUIDE` already has for facades (`Core/Site/SiteGuide.cs`). Each step names its state, its words,
the facts it rests on, the line that does it automatically, the line that does it by hand, and the line the assistant would propose, so
the desk, Companion, Patterns and an assistant drive a guide with the verbs everyone uses and read it from STATE and the context. Every
result an automatic step makes is a layer the hand's edits sit on, or a value it records having written: a re-run replaces its own
layer and nothing else, lists the hand edits it kept, and stages or refuses rather than overwrite a value a person typed since. On the
desk the guides are Simple's landing and a docked step rail in Pro, beside the live view and the full inspector; Pro adds the advanced
panes (effects chains, modulation and bindings, an area pane per STATE block) and a console; the panes dock, float inside the desk
window and collapse to rails (Dock for Avalonia, MIT); every feature is reachable from the menu bar, the Ctrl+K palette and a
right-click menu generated from the catalogue, tested against it; and colour carries three separate meanings — status, identity, stage
— none of them alone.

---

## 1. The owner's requirements, and where each is met

| Requirement (the owner's words, shortened) | Met by | Section |
|---|---|---|
| The four-click quad stays for a user to come back to | desk-ux item 6 (W1) | §6.1 |
| Each corner and pin editable and nudged by a single pixel; corners also dragged | desk-ux item 6 (the quad's draft points and corners dragged, its corners typed and nudged, a pin typed and nudged), desk-map items 1–3 (every handle typed, nudged, dragged, at one-pixel precision) | §6 |
| The media allocated to a screen shown in the UI | desk-map item 4 | §7 |
| Simple workflow areas, advanced and programming areas | workflows items 3–4, desk-map item 5 (the advanced panes), desk-shell item 2 (the console) and stretch 6 (scripts) | §2 |
| A two-projector blend, a double stack, or a double-stacked blend on a flat screen, intuitive | canvas items 1, 2 and 4, workflows items 1 and 3 | §5.1 |
| A 2D picture mapped onto a 3D surface so it reads undistorted, blendable to more projectors | workflows, the existing projection mapping | §5.2 |
| A simple workflow to map 3D surfaces with one or two projectors | workflows, the existing pins and solve | §5.3 |
| Several outputs using one pattern, media or generator as one canvas | canvas items 1–2 | §8 |
| Every feature from a menu item or a right-click menu | desk-shell items 2–3 | §9 |
| Panels draggable and collapsible | desk-shell item 1 | §10 |
| Subtle colour for areas and workflow stages | desk-shell item 4 | §11 |
| Games and medical UI and code architecture; database and server use | desk-gpu item 6, desk-map item 2, desk-shell item 3, stability items 6–7, capture item 6 | §12, §13 |
| All mapping can also be manual, from initial setup to adjusting the auto scan and setup | canvas items 1 and 3, workflows items 2–3, desk-ux item 6, desk-map | §3 |
| AI integration throughout | desk-shell items 2, 3 and 5; workflows items 1 and 5; desk-ux item 6's assistant test; every new row and STATE block | §4 |

## 2. Simple areas, advanced areas, and a programming area

The desk keeps its three workspaces (PLAY, MAP, SCAN), its show strip, attention bar and drawers. What changes is how much of it a
person sees at once: progressive disclosure, never hidden power. NN/g's distinction holds [ux §3.3]: a guide is staged disclosure,
right for tasks with distinct steps, wrong when steps alternate, so every guide step keeps the live view and the step's full
inspector one click away.

| Area | Who it is for | What it holds | How it is reached |
|---|---|---|---|
| **Workflows home** (Simple's landing; a pane in Pro) | a person setting up a standard job | a card per guide — Blend two projectors, Stack two projectors, Stack and blend four, Map a video onto a wall, Map an object, First light, Facade — each with its state from STATE `guides` ("Front screen · corners · 4 of 7"), Resume and Start (`GUIDE BEGIN <kind> <subject>`) | the Guides menu, the empty view's right-click, the palette, Simple's start |
| **A guide** | the same | the step rail docked at the left, the live view in the centre (the projector view, the 3D view or the wall's overlay), the step's object's inspector at the right showing only the fields the step uses, each step with **Do it**, **By hand**, **Ask**, **Explain** and, for a step a person judges, **Check** | the Guides menu (each guide, and each of its steps, acting on the selection), a card, the object's right-click (each step is re-entered from the object it is about), the palette |
| **Pro** | an operator who knows the tools | every pane, the outliner, the full inspectors, every card and drawer, the advanced panes (below), the generated entries | Ctrl+Shift+P, the View menu |
| **The advanced panes** (Pro) | an operator who programs a show's looks and controls | the effects chain editor, the modulation and bindings pane, and an area pane per advanced STATE block (below) | the Window menu, the object's right-click, the palette |
| **The console** (the programming area, Pro) | a programmer | a line editor with completion from `VerbSyntax`, each reply under its line, the history, "Copy line" from any menu entry or control; the journal beside it (`EVENTS`); scripts — named lists of lines run through the gate — as desk-shell's first stretch | the Window menu, Ctrl+` |

**The advanced panes** (desk-map item 5). Every advanced STATE block already has card descriptors (`tests/fixtures/state/*.cards.json`)
whose actions are catalogue lines, so an area pane per block costs little: effects, modulation and tempo, bindings and learn, timecode,
LED, optimise, live inputs and sends, align, the facade and sync each get a dockable pane listing that block's cards with their actions
and the rows' generated entries. Two areas get editors of their own, because their work is fine-grained: the **effects chain** of a
target (layer, canvas, surface, output or playlist item: kinds from `FX KINDS`, `FX ADD`, reorder by drag sending `FX MOVE`, `FX
BYPASS`, each parameter scrubbed and typed through `FX SET`, `FX IMPORT` and `FX CHECK`'s report by line) and **modulation and
bindings** (a parameter's `MOD BIND` with its source, amount, curve and smoothing; `BIND LEARN` from the parameter; the bindings list
with `BIND ENABLE` and `BIND FORGET`). What the round does not build — engine-side macros a Companion key or Patterns could run by name,
an ISF source editor, cue programming on a timeline — is not planned: the owner keeps the console and asks that every command and
feature be reachable from the UI or the menus (`PLAN.md` §9 decision 22, §9.4 here).

**Simple hides; it never disables.** Simple hides Pro fields and panes; the show strip (Ready, the blind chip, ARM, PANIC,
Blackout), the attention bar, the mode frame and every control of the air are the same in both, as `docs/DESK.md` already rules,
and menus are the same in both. "Open in Pro" on any guide step shows the whole inspector of its object.

**The console is a door like any other.** A typed line goes through the desk's gate in the node's order (pairing, the fleet, the
blind draft, Show Mode, the locks) and then to the node as the same line a button sends; the desk has no private API, so nothing
typed there can do what a button cannot. A row that carries a secret (`CREDENTIAL SET`, `LINK PATTERNS` with a token, `PAIRING`)
is refused in the console and in the palette with "type secrets in Network ▸ Links", and nothing of what was typed is kept in the
history. Typing a line by hand is new on the desk (`docs/DESK.md` says "the desk has no line of its own to type"): the owner keeps the console
(`PLAN.md` D11, §9 decision 22), and nothing is reached only through it.

| Test (App, headless) | Holds |
|---|---|
| `SimpleProTests` | for every pane and inspector field marked Pro, hidden in Simple and shown in Pro; no control of the show strip, the attention bar or the air hidden or disabled in either; every right-click menu equal in both |
| `WorkflowsHomeTests` | each guide card's words and state from a fixture's STATE `guides`; Resume opens the guide at its next step |
| `ConsoleTests` | a typed line is the line sent (byte for byte), its reply shown under it; a line the gate refuses is refused with the door's words and not sent; a secret-bearing row is refused and absent from the history; completion lists the rows `VerbSyntax` allows at the caret |
| `AdvancedPaneTests` (desk-map) | each advanced block's pane lists its fixture's cards with their actions; every action's line parses and is the card's |
| `FxChainEditorTests` (desk-map) | a drag to position 2 sends `FX MOVE <slot> 2`; a scrubbed parameter sends `FX SET` through the throttle, the newest per window; an import's compile report shown by line |
| `ModBindPaneTests` (desk-map) | a bound parameter shows its source and curve from STATE; Learn sends `BIND LEARN <target line>`; Forget sends `BIND FORGET` |

## 3. Every map by hand; every automatic result adjustable by hand

### 3.1 The rule

Every result an automatic step makes is one of two things, and the step says which:

1. **A layer under the hand's.** The automatic result has fields of its own that no person types, and the hand's edits are drawn on
   top of it. A camera alignment's measured warp grid, blend, γ and colour gains (`Output.Measured`, drawn as the first warp layer:
   "the operator's warp then moves the corrected picture", `docs/ALIGN.md`), a stack member's measured lattice (drawn before the
   operator's lattice and corner pin, `docs/MAPPING.md`) and the 3D blend maps `BLEND SOLVE` computes are layers today. A re-run
   replaces its own layer only; the hand's layer is drawn on top exactly as before.
2. **A value it records having written.** The automatic step writes a field a person can also type — a projector's pose and lens,
   a canvas's output rectangles and edges, a viewpoint's aim and field of view. It records what it wrote. A re-run that finds the
   field changed by hand since never overwrites it on its own: a projector's solve — `PROJECTOR SOLVE`, `CALIBRATION RESOLVE` or
   `SCAN APPLY` — stages that projector's result over a pose or lens typed or nudged since (a calibration result of the existing
   source `manual`), through the ledger's existing path, and `CALIBRATION ACCEPT` applies it and names the manual result it replaces;
   every other step refuses with the words naming each changed field unless the line says `KEEP` (the hand's values stay, the rest is
   written) or `REPLACE` (the automatic values are written and the reply lists what they replaced).

A re-run's reply, and the card of a staged result, list every hand edit it kept, with the line that clears it. Every hand edit and
every automatic result is an edit of the project through `ProjectStore`, the one writer, so each is one undo step (a burst of one
handle's moves is one), `UNDO TO` (desk-ux) takes a run of them back as one action, and nothing is ever written behind the store.

### 3.2 By result

| Automatic step | What it writes | What a person edits on top or beside it | What a re-run does | Today | Package |
|---|---|---|---|---|---|
| `ALIGN RUN` → `ALIGN ACCEPT` (camera, flat screen) | the measured layer: warp grid (which, holding the canvas's own uv, sets the output's region of the canvas while it is on), blend weight and pedestal, uniformity gains, colour gains, **γ** (`Measured.Color.Gamma`) | the output's corner pin and lattice, the blend's curve, a γ of the hand's own (`Blend.Gamma`, 0 meaning "the measured γ"), black level, the colour trim, masks | stages; ACCEPT replaces the measured layer only; the reply lists the hand edits kept on top | ACCEPT also writes the measured γ into `Blend.Gamma`, a field a person types (round 2's open note) — the one place a run overwrites the hand | canvas item 3: the accept writes no `Blend.Gamma`, and the blend draws the hand's γ where one is set, else the measured |
| `STACK ALIGN`, `STACK MATCH` | a member's measured lattice; its measured colour gains | both members' operator warps (the fit is made under them already), the colour trim | replaces the member's measured layer | as today | canvas item 3 (the reply's list) |
| `BLEND SOLVE` (3D) | the blend maps | the curve, γ trim, black level, intensity compensation, masks | recomputes the maps; trims and masks stay | as today | canvas item 3 (the reply's list) |
| `PROJECTOR SOLVE`, `CALIBRATION RESOLVE` | the pose and the unlocked optics, recorded as a ledger result | the pins (stage points and pixels), the priors, the locks; the pixel quads (a camera re-solve, whose cause is a projector that moved, re-seats them so each corner — a hand-moved one included — lights its wall spot, naming each; a pin solve leaves them); masks, 3D surfaces and output warps are never touched | over a current result of source `manual` (a pose or lens typed or nudged) it stages and names that result; otherwise as today (applied within the gate's score, else staged) | a typed pose is not a ledger result of its own | workflows item 2 |
| `SCAN SOLVE`, `SCAN APPLY` | the scan's mesh and solved projectors, one edit | every surface's content, tags, masks (the pen's), hand transforms; pixel quads; pins; a projector's pose and lens | keeps each surface; a surface whose faces the new mesh no longer has is kept, hidden, and named in the reply — never deleted; over a projector whose current result is `manual` it stages that projector's result and names it, the others applied as today; every pixel quad left as it stands (its corners are raster pixels, so a hand-moved corner lights the same spot whatever pose is written); `SCAN SOLVE … HOLD <projector>` (today's) keeps a projector at its stage pose through the solve itself | `SCAN APPLY` writes every solved projector's pose, a typed one included | workflows item 2 |
| `SCAN SUGGEST` | nothing: proposals a person's `APPROVE` makes | the made surface is the person's from then | new proposals; nothing made is changed | as today | — |
| `CANVAS SPAN` (new) | the canvas's size, each output's mode, rectangle, edges and plan rectangle, recorded as the span | an output's rectangle, edges, warp, trims | refuses over a rectangle or edge changed by hand since, or over an output drawing a measured warp, naming each, unless `KEEP` (those left as they are) or `REPLACE` (written, and the measured layer taken off each output whose region changed, its run named) | — | canvas item 1 |
| `VIEWPOINT FIT` (new) | the viewpoint's aim and field of view, fitted to the named surfaces from its eye, recorded in `Viewpoint.Fit` | the eye's position (always the person's), a typed aim or field of view | refuses over an aim or field of view typed since, naming each, unless `KEEP` (the typed values stay, the rest written) or `REPLACE` | — | workflows item 2 |

`HAND <output>|<projector>|<surface>` answers both layers in words ("op_2: measured warp from run al_7; by hand: corner TR +3.0,
−1.0 px, γ 2.40 by hand over the measured 2.31"). `HAND CLEAR <output> WARP|BLEND|COLOR|MASKS|ALL` takes an output's hand layer back
to rest (refused when it is at rest already, with the words), the automatic layer under it untouched; a projector's manual result goes
back through the ledger (`CALIBRATION FALLBACK`, `CALIBRATION RECALL`).

**Precedence on a flat screen.** A Canvas-mode output shows the region of the canvas its rectangle names — the span's recorded value,
or a rectangle typed by hand (`SET <output> sourceRect`) — until it draws a measured warp, whose grid holds the canvas's own uv and so
sets the region while it is on (`docs/ALIGN.md`). Under a measured warp the hand moves the picture with the warp's corners and lattice
on top, and a typed rectangle is refused with "op_2's canvas region comes from its measured warp (run al_7): move the picture with
`WARP CORNER`, or `ALIGN REVERT` the group". A re-span over such an output is refused naming the run unless `KEEP` (the output left as
it is) or `REPLACE` (the rectangles written and that output's measured layer taken off, the run named, a fresh `ALIGN RUN` to measure
again).

STATE carries the split: `outputs[].hand` (`warp`, `blend`, `color`, `masks` — each true when not at rest — `gamma` with its measured
and hand values, and the run the measured layer under it came from), derived from the project at each revision, so nothing beside the
store counts edits; a typed or nudged pose is a calibration result of the existing source `manual`; each guide step's facts say
whether its result is automatic, by hand or both.

### 3.3 By hand from the first setup

No step of a map needs a camera, a scan or the assistant. Each has a hand path on the desk and a line on the wire:

| Setup step | By hand on the desk | The line |
|---|---|---|
| Outputs on their displays | Outputs drawer: Add output; the display picker | `ADD output {…}`, `SET <output> displayKey "<key>"` |
| A projector | the outliner's +, or Light a wall | `ADD projector {…}`, `LIGHT WALL <display>` |
| Its pose and lens | the 3D gizmo; typed position, aim and throw in its inspector | `SET <projector> pose …`, `PROJECTOR PRIOR <projector> THROW <ratio>` |
| A flat picture on a wall | Q and four clicks, each placed point dragged and nudged before Enter (§6.1) | `SURFACE QUAD <projector> x1 y1 … x4 y4 PX` |
| Its corners and lattice | drag, type, nudge (§6; a quad's corners from desk-ux on) | `SURFACE CORNER`, `SURFACE LATTICE`, `SURFACE NODE` |
| An output's keystone and lattice | drag, type, nudge (§6) | `WARP CORNER` (new), `WARP NODE … PX` (extended) |
| A canvas across outputs | the canvas editor in the flat guide's Span step (positions and stacks, overlap, each output's rectangle typed and dragged on a diagram of the canvas; workflows item 3) | `CANVAS SPAN` (new), or `ADD canvas` and `SET <output> sourceRect …` |
| A surface on geometry | the outliner's primitives; a typed transform; a model from a file | `ADD mesh`, `ADD surface`, `SET <surface> transform …`, `SURVEY IMPORT <file>` |
| Pins | P, a click on a surface, then drag, type, nudge (§6; typed and nudged from desk-ux on, through the line a drag sends) | `ADD pin <projector> {…}`, `SET <pin> pixel …`, `PIN MOVE` (new) |
| A viewpoint | "Viewpoint from this view", then its typed eye, aim and field of view | `ADD viewpoint {…}`, `SET <viewpoint> …` |
| How a surface takes its picture | the surface inspector's Mapping | `SURFACE MAP` (new) |
| A blend | the output inspector's edges, curve, γ, black level | `SET <output> blend.…` |
| Masks | the pen | `ADD mask`, `SET <output> masks.<i>.path …` |
| Colour and level | the output inspector's trims | `SET <output> color.…` |
| A stack member registered on its master | the member's warp corners and lattice against the master's picture | `WARP CORNER`, `WARP NODE … PX` |

Each automatic step in the guides (§5) has its hand step beside it in the same row of the rail, and a person can switch between them
at any step: an automatic result adjusted by hand is §3.1's layer or recorded value, and a hand result is never replaced by an
automatic one without the person's line.

| Test | Package | Holds |
|---|---|---|
| `HandOverAutoTests` (Engine) | canvas | an output with a hand corner pin, a hand γ and colour trim: `ALIGN ACCEPT` of a new run replaces `Measured` and leaves `Warp`, `Blend` and `Color` byte-equal; its reply lists the three with their `HAND CLEAR` lines; `Blend.Gamma` is never written by an align; `BLEND SOLVE` and `STACK ALIGN` the same; `UNDO` after the accept restores the previous measured layer and keeps the hand layer |
| `HandGammaTests` (Render, GL) | canvas | `Blend.Gamma` 0 → the blend drawn through the measured γ, and a second accept with another γ changes the drawn pixels; `Blend.Gamma` 2.4 → drawn through 2.4 whatever the accept measured; a round-2 project whose `Blend.Gamma` equals its current result's γ reads as 0 |
| `HandClearTests` | canvas | `HAND CLEAR op WARP` on a hand warp → at rest, the measured layer unchanged; on a warp at rest → refused "op's warp is at rest already"; `HAND CLEAR op MASKS` → no mask, the measured layer unchanged |
| `SpanKeepTests` | canvas | a span, then op_2's rectangle typed by hand: `CANVAS SPAN` again → refused naming op_2's rectangle; `KEEP` → op_2's kept, the rest written; `REPLACE` → written, the reply names what it replaced |
| `SpanMeasuredTests` | canvas | an accepted align on op_2: a typed rectangle on op_2 → refused with the words; a re-span → refused naming the run; `KEEP` → op_2 untouched; `REPLACE` → written and op_2's measured layer taken off, the run named; `UNDO` → both back |
| `HandPoseTests` | workflows | a pose typed after a solve → a ledger result of source `manual`; `PROJECTOR SOLVE` then stages, `calibration.staged` raised, the words name the manual result; `CALIBRATION ACCEPT` applies and the history shows the manual result as its parent |
| `ScanKeepsHandTests` | workflows | a surface with content, tags and a pen mask, a projector with a pose typed since its last solve, a pixel quad with a hand-moved corner; `SCAN APPLY` of a new scan keeps the three surface edits, stages that projector's result and names it while applying the others, and leaves the quad byte-equal; a surface whose faces are gone is hidden, named in the reply, still in the project; `SCAN SOLVE … HOLD` on that projector leaves its pose as typed |
| `ManualSetupTests` (Engine, headless) | workflows | the three guides' end states reached by hand lines alone on the stage simulator, with no camera, no scan and no assistant |

## 4. The assistant in every workflow, panel and step

### 4.1 What the node gives an assistant today

The assistant (`src/PlayMap.Assistant`) is one session per conversation with a small typed toolbox: `get_state`, `get_context`,
`get_project`, `get_entity`, `list_verbs` (the catalogue, with each verb's risk), `plan_recipe` (pure planners: `fill_tagged`,
`blend_outputs`, `test_pattern`, `patterns_off`), `resolve_time` and `propose_commands`, the only tool that changes anything. Every
proposed line goes through `CommandGate` and the node's own command path; a low line runs when the operator's `runLowWithoutAsking` is
on and a review line when `runReviewWithoutAsking` is, else each waits, and a high-risk line always waits for a person on the node's
proposal list (`PROPOSALS`, `APPROVE`, `REJECT`); the lines of one call are a batch approved in its order, high risk is never
pre-ticked and has no bulk approval, a proposal expires after 15 minutes, and while armed the assistant only queues. MCP at `/mcp`
serves the same STATE, context and project as tools and resources, and `run_command` runs one line through the node's command path with
its own rule for high risk (`SwitchApprovalPolicy`): a high-risk line runs only while the node's "AI may act" switch is on, and while
it is off is queued for a person, or refused with the reason where no queue is set. `StateExplainer` says what STATE means in plain
words with no model. `SCAN SUGGEST … ASSISTANT` lets the assistant name and rank measured surface suggestions from text. The desk shows
an "AI · n" pill with the proposals waiting and the assistant's settings card; it has no conversation, no proposals list and no way to
type a question.

### 4.2 Guides as data

The guides are the assistant's way into every workflow, as they are the desk's:

- `GUIDE LIST` answers every guide begun on the node and its state; `GUIDE <FLAT|WALL|OBJECT|FACADE|FIRST> [<subject>]` answers one
  guide's steps (`FACADE` is `SITE GUIDE`'s list, kept under its old row too). The subject is what the guide is about: a canvas
  for FLAT, a viewpoint for WALL, a projector or a pair for OBJECT; with none, the first step says which line makes one. `GUIDE BEGIN
  <kind> <subject>` (a card's Start, from any session, an assistant's included) records the guide as begun and `GUIDE END <kind>
  <subject>` as ended, in `Project.Guides` with who and when. (The catalogue names a row by a line's first two words, so the second
  word is the action: `GUIDE FLAT cv BEGIN` would be read as the `GUIDE` query.)
- Each step: `step` (its key), `state` (`done`, `waiting`, `running`, `failed`, as `SiteGuide`'s), `words`, `facts` (the measured
  numbers it rests on: residuals, coverage, the seam's misregistration, a score's terms), `line` (what does it now), `auto` (the
  automatic way, if any), `hand` (the hand way, with `<…>` where only a person knows the value), and `checked` for a step a person
  judges.
- A step a person judges — corners lined up by eye, a blend that looks right — is marked by `GUIDE CHECK <kind> <subject> <step>`
  (and `GUIDE UNCHECK <kind> <subject> <step>`): the mark records the step's inputs, and the step reads `done` while they are
  unchanged and "changed since you checked it" once they move. `CHECK` is a person's mark: an assistant's session is refused with
  "a person checks a step", as only a person approves.
- STATE `guides` lists every guide begun (kind, subject, the next step, how many of how many, its light); the context carries one
  item per guide (kind `guide`, "Flat blend · Front: corners, 4 of 7"), so Patterns' Eye and a peer's assistant see where a
  setup stands. The Eye keeps the first 64 of a context's commands today, which are the air's, so it sees a guide's state but
  cannot propose its lines until Patterns keeps at least 256 of a context's commands, the owner's figure, or no cap, either of
  which holds every command PlayMap lists (`PLAN.md` §9 decision 18); an assistant on the node, or over MCP, reaches them all.

The planners are pure functions of the project and the node's facts (`Core/Workflows`), so the same project gives the same steps on
the desk, over MCP and in a test; they write nothing, and the guide's progress is the project itself, with the begun guides and the
checked steps' inputs in `Project.Guides`.

### 4.3 Propose, approve, carry out

- **Ask.** The desk's assistant pane (desk-shell item 5) holds the conversation; the palette's text that matches no verb becomes
  `ASK` (`docs/UX.md` P4); every object's right-click has "Ask about this…", which opens the pane with the object named; each guide
  step's **Ask** sends "help me with <step> of <guide>".
- **Propose a step.** `plan_recipe` gains `guide_step` (`guide`, `subject`, `step`): the step's lines from the planner, with
  placeholders the planner cannot fill named as questions for the operator; the prompt gains the rule "for a workflow, read the guide,
  say which step is next and why from its facts, and propose that step's lines". The lines go through `propose_commands` and the gate
  like any.
- **Approve.** The pane shows each proposed line as a row with **Show me**, **Approve** and **Reject**. Approve sends `APPROVE <id>` —
  one press, as a card's APPROVE is (the owner's call 8). **Approve step** sends the step's low and review lines' approvals in their
  batch order; a high-risk line in the step waits for its own press. **Show me** rings, on the desk, the control or menu entry that
  sends that same line (matched by the action's line), so the person can do it themselves (`docs/UX.md` §10).
- **Carry out.** An approved line runs through the node's own path; its reply is the proposal's result; STATE says what happened. A
  step's staged results (an align run, a re-solve) stay staged for a person's accept, and a judged step ends "waiting for a person's
  Check", since `CHECK` is refused from an assistant's session. Armed, the assistant only queues.

### 4.4 Explain what it sees

What the assistant sees is facts, not guesses: STATE, the context, the project, each guide step's `facts` and `words`, each score
card's terms with their fix lines, the hand and automatic layers (`HAND`), and the journal's recent lines. "Explain" on a step asks
the assistant to say, from those, why the step stands as it does ("P2's top-right corner is 3.2 px off the canvas corner the camera
found; the seam's misregistration is 0.8 px at p95; the blend is automatic from the 288 px overlap"); with the assistant off, the
desk shows the step's own words and `StateExplainer`'s, which need no model. No picture is sent to a model in the must; a still of a
view for a model that reads pictures, on the machine or off it (`PLAN.md` §9 decision 19), is workflows' stretch: off by default,
turned on by a person on the node or its paired desk with `ASSISTANT SET stills on` (high; refused from an assistant, an MCP client
or an approval, as every `ASSISTANT SET`), the provider it goes to named in `ASSISTANT STATUS`; one view's picture (an output, the stage, a
projector or a scan view, never the desk's window, so no form, token or invite is in it) at the preview's size; each still journalled
by view, size, time and provider, never its pixels (`AssistantStillTests`).

### 4.5 In every panel

| Where | The assistant's part |
|---|---|
| The palette and the console | unmatched text → `ASK`; a typed line is the person's, never the assistant's |
| Every right-click menu | "Ask about this…" with the object named |
| A guide step | Ask, Explain, the step proposed as a batch |
| A card (score, align run, stack, staged result) | its fix lines and actions are catalogue lines, so the assistant proposes them by name |
| The assistant pane | the conversation, the proposals with Show me, Approve, Reject, Approve step; the "AI · n" pill opens it |
| STATE, the context, MCP | every new block and item (`guides`, `outputs[].showing`, `outputs[].hand`, the capture inputs) reaches `get_state`, MCP's state resource and the context's items with no code of the package's own |

### 4.6 Tests

| Test | Package | Holds |
|---|---|---|
| `GuideVerbTests` (Engine) | workflows | `GUIDE FLAT cv` on each fixture project → the steps, states, words and lines the planner gives; `BEGIN` → `GUIDE LIST` lists it, `END` → not; `CHECK` from an assistant's session refused with its words; `CHECK` then a moved corner → "changed since you checked it" |
| `GuidePlannerTests` (Core, pure) | workflows | each guide's steps as functions of the project: the same project the same steps; every `line`, `auto` and `hand` parses (`VerbSyntax`) |
| `AssistantGuideTests` (Assistant) | workflows | a `ScriptedProvider` reads `GUIDE` and proposes each step of the five guides (FLAT, WALL, OBJECT, FACADE, FIRST) through `plan_recipe guide_step` and `propose_commands`: low and review lines run or wait by the operator's two settings, high lines wait for a person, every line queued while armed; after approval STATE shows each automatic step done, and each judged step "waiting for a person's Check" until the test sends `CHECK` from a person's session |
| `McpGuideTests` (Net) | workflows | MCP's `list_verbs` lists the GUIDE rows; `run_command GUIDE FLAT cv` answers the steps; `get_state` carries `guides`; a high-risk guide line through `run_command` runs with "AI may act" on and is queued (or refused with the reason with no queue) with it off |
| `AssistantPaneTests` (App) | desk-shell | proposals from a fixture: Approve sends `APPROVE <id>` once; Approve step sends the batch's low and review approvals in order and none of its high lines; Show me rings the control whose action sends the line; Reject sends `REJECT <id>` |
| `AskAboutThisTests` (App) | desk-shell | every object kind's menu has the entry; it opens the pane with the object's id |

## 5. The three workflows, step by step

Each table is the step rail a person sees, top to bottom. "Today" says what exists on `main`; "New" what the packages add.

### 5.1 Projectors on a flat screen: an edge blend, a double stack, or a double-stacked blend

The shape every tool shares — one canvas, the outputs identified, the overlap stated, a test card across the whole canvas, each
projector's four corners, the blend from the overlap, then gamma and black level, brightness matched to the dimmest, locked
[ux §1.1]. A stack is the same with full overlap and projector 1 as the reference. A double-stacked blend — standard corporate AV work,
in the owner's words — is both: two positions blended side by side, each position two projectors stacked on its first.

| Step | What the person sees and does | Automatic | By hand | Today / new |
|---|---|---|---|---|
| 1. Rig | "Side by side", "Stack" or "Stack and blend"; the outputs, their displays, the overlap (15 % by default, 10–20 % hinted) | — | `CANVAS SPAN NEW op_1 op_2 SIDE 2x1 OVERLAP 15%` (or `STACK 2`, or `NEW op_1 op_2 op_3 op_4 SIDE 2x1 STACK 2 OVERLAP 15%`, the outputs position by position): one edit making the canvas (two 1920 × 1080 positions at 15 % → a 3552 × 1080 canvas), every output in Canvas mode with its position's rectangle, the inner edges at 288 px, a stack's members on one rectangle with no edge between them, the plan | new (`CANVAS SPAN`); today the drawer's blend grid sends four `SET`s per output [today §d1] |
| 2. Identify | each output shows its name and number in its identity colour on the wall | — | `OUTPUT IDENTIFY op_1`, `op_2` | today |
| 3. Test card | one grid across the whole canvas: each output's label, number and resolution, its rectangle's border, the overlap's start and width, circles and diagonals across the seam | — | `PATTERN grid ON cv_front` | new (spanning `PATTERN`, §8); today a pattern is drawn per output at its own raster |
| 4. Corners | per output its four corner handles on the projector view and on the wall; drag, type, nudge by one pixel; a lattice only if needed; for a stack, each member to its position's first output's picture | `ALIGN NEW Front cm1 op_1 op_2 CANVAS cv_front`, `ALIGN RUN Front`, `ALIGN ACCEPT <run>` (a camera on a tripod; the measured layer, which then sets each output's region of the canvas), `STACK ALIGN` for a stack's members | `WARP CORNER op_2 TR BY 1 0 PX`, `WARP NODE op_2 3 2 BY 0 1 PX` (on top of a measured layer, never in its place) | `WARP CORNER` and the PX forms new; today a Canvas-mode output's keystone is not reachable on the desk [today §d1] |
| 5. Blend | the blend from the overlap (a stack: none — the light adds); then the curve, γ trim and black level, seen on the test card | from step 1's edges, or the align's measured blend | `SET op_1 blend.gamma 2.4`, `SET op_1 blend.blackLevel 0.02`, `SET op_1 blend.curve "cosine"` | today |
| 6. Match | brightness and colour to the dimmest output | `ALIGN RUN Front COLOR WHITE` | `SET op_2 color.brightness 0.92` and the gains | today |
| 7. Content and lock | the canvas's picture; Lock for the show | — | `CANVAS SOURCE cv_front playlist:pl_show` (new), `LOCK op_1`, `LOCK op_2` | `CANVAS SOURCE` new |

Steps 4–6 are judged by a person when no camera ran: **Check** marks them; with a camera, the align's score (A or B) marks them done.
What the guide needs that is new: `CANVAS SPAN` with positions and stacks, `CANVAS SOURCE`, the spanning test card, `WARP CORNER` and
the PX forms (desk-map), the Span step's canvas editor, the guide itself. The double-stacked blend is the same planner with two
outputs per position (the must); an n × m wall beyond two positions is workflows' stretch. Under a measured layer the span's rectangles
give way to the camera's region and a re-span asks for `KEEP` or `REPLACE` (§3.2).

### 5.2 A 2D picture on a 3D surface, undistorted from a viewer position

A flat wall needs no viewpoint: the four-click quad's homography puts the picture on the wall metrically, so it reads as a flat
poster from every seat [ux §1.2]. The viewpoint matters for walls that are not flat — bays, corners, cornices — and for an illusion
seen from one place. The engine already projects content from a viewpoint (`SurfaceMapping.Projection`, `StageModel.cs`;
`StageRenderer`), and the 3D blend already joins more projectors on one surface.

| Step | What the person sees and does | Automatic | By hand | Today / new |
|---|---|---|---|---|
| 0. Which wall | "It is flat" → the quad (First light or §5.1's corners); "It has depth" → on | — | — | new (the choice) |
| 1. The surface | the wall's geometry in the 3D view | a scan: `SCAN START`, `SCAN SOLVE`, `SCAN APPLY`, then `SCAN SURFACE` or `SCAN SUGGEST` | a primitive placed and typed (`ADD mesh`, `ADD surface`), or a model (`SURVEY IMPORT <file>`) | today |
| 2. The projector | solved onto the surface | the scan's solve | pins and `PROJECTOR SOLVE` (§5.3's steps 3–4) | today |
| 3. The viewer | the audience's eye: "Viewpoint from this view", or typed (a seat 1.7 m above the floor by default), then aimed at the surfaces | `VIEWPOINT FIT vp_seat sf_wall` (aim and field of view from the eye; over a typed aim or field of view, refused unless `KEEP` or `REPLACE`) | the viewpoint's typed eye, aim and field of view; dragged in 3D | `VIEWPOINT FIT` and the viewpoint's editable fields new; today a viewpoint cannot be edited after it is added [today §d2] |
| 4. Mapping | the surface takes its picture from the viewpoint | — | `SURFACE MAP sf_wall PROJECTION FROM vp_seat` (new) | the mapping exists; the row is new (today a generic `SET` from the inspector) |
| 5. Look through | the 3D view from the viewpoint, so the person sees what the audience will | — | the desk's view camera at the viewpoint (`VIEW editor CAMERA …`) | new (the desk's button) |
| 6. Content | the film | — | `SURFACE SOURCE sf_wall media:md_film` | today |
| 7. More projectors | each added and solved (steps 2), then the blend across them | `BLEND SOLVE ALL` | the blend's trims (§5.1 step 5) | today |
| 8. Check and lock | the test card from the viewpoint, the seams, Lock | — | `PATTERN grid ON …`, `LOCK …` | today |

Because the content is defined once, in the viewpoint's space, every projector samples the same picture and the overlap blends in
that space; adding a projector changes no content. A camera standing at the seat is the simplest path when one can [ux §1.2]:
the scan already holds projector-to-camera correspondences, so laying content out "as the camera sees it" is a content mode, not a
new calibration — canvas's stretch (`SurfaceMapping.Camera`), with the viewpoint path as the must, which works when no camera can
stand at the seat.

### 5.3 A 3D map with one or two projectors

The shape disguise QuickCal, Watchout 7, Pixera and MadMapper share: rough pose and lens first, at least six points spread in depth
and to the picture's corners, a live error while lining up, the second projector reusing the same 3D points [ux §1.3].

| Step | What the person sees and does | Automatic | By hand | Today / new |
|---|---|---|---|---|
| 1. The object | its model in the 3D view | a scan (`SCAN START` … `SCAN APPLY`) | a model (`SURVEY IMPORT <file>`) or a primitive | today |
| 2. Rough projector | lens and throw, a drag in 3D, so the overlay lands near the object | — | `LIGHT WALL #2 AS P1 THROW 1.5`, the gizmo, `PROJECTOR PRIOR P1 THROW 1.5` | today |
| 3. Pins | a click on the model for each point, then its handle dragged, typed or nudged onto the real feature; a **coverage meter** (the picture's four quadrants and the depth spread) and "add a pin in the lower right, nearer the camera" | — | `ADD pin P1 {…}`, `PIN MOVE pn_4 BY 1 0 PX` | pins today; the meter's facts and `PIN MOVE` new |
| 4. Solve | the residual per pin live, then the solve and its score card (grade, terms, fixes) | `PROJECTOR SOLVE P1` from the pins (the pins are the hand's inputs) | the pose and lens typed (a `manual` result, §3.2) | today; the typed pose as a result new |
| 5. Second projector | the first projector's pin points that P2's rough pose sees, offered as pins to place | — | `ADD pin P2 {"world":…}` per point, then step 3 | the offer new |
| 6. Blend | the blend on the model | `BLEND SOLVE ALL` | trims | today |
| 7. Content | each surface's picture | `plan_recipe fill_tagged` | `SURFACE SOURCE` | today |

The guide reads `PoseSolver`'s needs — six pins for the closed form, ten for distortion, spread over the picture and in depth — as the
coverage meter's facts, so it says what is missing before a solve fails.

| Test | Package | Holds |
|---|---|---|
| `FlatGuideTests` (Engine, on the align simulator `flat-screen:tile2x1:o15`, `…:stack2` and `…:tile2x1:stack2:o15`) | workflows | by hand: the seven steps' lines in order reach a spanned, blended canvas with content on each of the three layouts, every step `done` once checked; automatic: steps 4–6 by `ALIGN` (and `STACK ALIGN` for the stacks) reach `done` from the score; switching a step from automatic to hand keeps the measured layer under the hand's; span, accept, a typed rectangle (refused under the measured warp), a corner nudge (kept), a re-span (refused naming the run; `REPLACE` clears it) |
| `WallGuideTests` (Engine, stage simulator) | workflows | the eight steps reach a surface mapped from the viewpoint; the picture rendered from the viewpoint matches the source within the projection test's tolerance; adding a second projector and `BLEND SOLVE` changes no content |
| `ObjectGuideTests` (Engine, stage simulator) | workflows | six pins in one quadrant → the coverage facts name three empty quadrants; spread → `done`; P2 offered P1's points its rough pose sees |
| `GuideRailTests` (App) | workflows | each of the five guides' rails from a fixture, FACADE and FIRST among them: the step's buttons, the inspector showing the step's fields, Open in Pro showing all |
| `SpanEditorTests` (App) | workflows | the Span step's diagram from a fixture canvas: positions, stacks and the overlap typed send one `CANVAS SPAN`; an output's rectangle dragged sends `SET <output> sourceRect` through the throttle, refused with the words under a measured warp |

## 6. The four-click quad that stays, and every handle by one pixel

### 6.1 The quad, its corners and a pin by one pixel (desk-ux item 6, W1)

desk-ux builds this section (`docs/DESK.md`, MAP's quad tool) against the reading of the tree at `6f24310`, where the quad works and
a half-drawn one is lost the moment the person looks away: the clicks are dropped on a tool change, on showing another projector, on
leaving MAP and at Esc; after Enter the tool stays Q, so the next click starts a new quad instead of selecting the old one; a
reselected quad resets to its top-left corner without telling the wall, and the desk never sends `HANDLE SELECT NONE`; picking a
quad in the outliner does not show its projector; the first-light guide's × closes it for good; corners are neither dragged nor
typed, and a pin's pixel is read-only [today §a].

- **A draft per projector.** The clicks of an unfinished quad stay on the desk (never in the project, so the one-writer rule is
  untouched) until Enter sends `SURFACE QUAD` or the person discards it — the draft chip's ✕, or Esc while the view has the keyboard
  and shows that draft. A tool change, another projector, leaving MAP and coming back, a menu entry chosen, the first-light guide
  closed and a push keep it, and so do a desk restart and a reattach to the same node: the draft is kept in the user's `desk.json` by
  node and projector (a draft whose projector is gone is dropped with a toast). The chip "Quad 2/4 · Resume · Discard" sits in the
  view's corner and under Surfaces in the outliner; the wall's rubber band (`CURSOR … TRAIL`, an ephemeral line that changes nothing
  in the project) is off while the person is elsewhere and back on return.
- **The draft's points move.** Before Enter each placed point drags with the pointer and nudges with the arrows (1 px, Shift 10, Alt
  0.1, Tab to the next point), on the desk alone; Enter sends the four as they stand.
- **Select after Enter.** Enter leaves the new quad selected with the Select tool; "Keep quad tool" (a toggle on the tool strip) draws
  several in a row.
- **The wall told.** Selecting a quad restores the corner last selected on it and sends `HANDLE SELECT <sf> <c>`; deselecting sends
  `HANDLE SELECT NONE`.
- **The view follows.** Picking a quad in the outliner shows its projector, so the arrows never move a quad the view does not show.
- **Corners dragged, typed and nudged.** A corner handle in the projector view drags, sending `SURFACE CORNER <sf> <c> TO <x> <y>
  RASTER` through the desk's throttle (the newest in each 16 ms, as warp corners drag today); a burst is one undo step. The quad's
  inspector shows its four corners as x, y in wall pixels, in place of the mesh fields a quad does not use, and a typed value sends
  `SURFACE CORNER … TO … PX`. Tab and the arrows stay.
- **A pin by one pixel.** A selected pin's pixel is typed in its row and nudged with the arrows, through the line its drag sends today
  (`SET <pin> pixel`); desk-map adds `PIN MOVE` and `HANDLE NUDGE` of a pin so an encoder or an assistant moves it the same way.
- **First light again.** The guide reopens from the view's right-click and from the help overlay (and from the Guides menu once
  desk-shell lands).

### 6.2 Every handle: selected, dragged, typed, nudged (desk-map, W3)

desk-ux (§6.1) drags, types and nudges a quad's corners and types and nudges a pin; output warp corners move by drag alone, and the
projector view has no zoom, so at 1600 px about two raster pixels sit under each screen pixel and a one-pixel drag is out of reach
[today §a]. desk-map gives every handle the same, with the engine rows an encoder, a dial and an assistant use.

| Handle | Selected by | Dragged | Typed (inspector) | Nudged | The line |
|---|---|---|---|---|---|
| A quad's corner | a click; Tab | yes (§6.1) | x, y in wall px (§6.1) | arrows | `SURFACE CORNER <sf> <c> TO\|BY <x> <y> PX` |
| A quad's lattice node | a click; Tab | yes | x, y | arrows | `SURFACE NODE <sf> <col> <row> TO\|BY <x> <y> PX` |
| A pin | a click (Pin tool or Select) | yes (today) | its pixel x, y (raster px; §6.1); its stage point | arrows (§6.1) | `SET <pin> pixel …` (§6.1), `PIN MOVE <pin> TO\|BY <x> <y> [PX]` (new) |
| An output's warp corner | a click; Tab | yes (today) | x, y in output px | arrows | `WARP CORNER <output> TL\|TR\|BR\|BL TO\|BY <x> <y> [PX\|NORM]` (new) |
| An output's lattice node | a click; Tab | yes (today) | x, y | arrows | `WARP NODE <output> <c> <r> TO\|BY <x> <y> PX` (the PX form new; the normalised form kept) |
| A mask's node | a click; Tab | yes (today) | x, y | arrows | `SET <output> masks.<i>.path …` (the path) |
| A viewpoint | the outliner, the 3D view | the 3D gizmo | eye x, y, z; aim; field of view | — | `SET <viewpoint> …` |

`HANDLE SELECT` grows to every kind (`<pin>`, `<output> WARP TL|…|<c>,<r>`, `<output> MASK <i> <node>`) and `HANDLE NUDGE` moves
whichever handle is selected — so an encoder, a Companion dial or an assistant nudges a pin as it nudges a corner. A move that would
change nothing (`BY 0 0`, `TO` where it is) is refused with the words; a move of a locked entity is refused with LockGuard's.

### 6.3 Keys, steps and what the overlay shows

- **Keys.** The arrows move the selected handle by 1 px; Shift by 10 (a desk preference, 2–100, as Figma's big nudge [ux §3.4]); Alt
  by 0.1; Tab and Shift+Tab step the object's handles; Enter in a typed field sends `TO`. The unit is the handle's: wall pixels (the
  output's final pixels, after its warp) for quads and warps, the projector's raster for pins.
- **The status bar** names the mode and the step: "Quad 'Wall' · corner TR · 1 px wall · Shift 10 · Alt 0.1".
- **The overlay on the wall** (`OVERLAY`, through each output's warp) draws, beside today's outlines, corner handles and lattice, the
  selected handle of any kind enlarged with a one-pixel crosshair at its exact point, and the pins as rings in the projector's identity
  colour with a line to where the solve predicts them; the projector view draws the same. Nothing of it reaches the air while armed
  (`OverlayTests`).
- **Precision.** The projector view zooms (the wheel, 1:1, Fit) and a loupe at the pointer magnifies 8× (L), so one raster pixel is one
  move at any window size.

### 6.4 Tests

| Test | Package | Holds |
|---|---|---|
| `QuadDraftTests` (App, headless) | desk-ux | two clicks, then a tool change, another projector, PLAY and back, a menu entry chosen from the bar and from a right-click, the first-light guide closed, and a desk restart (the draft read back from `desk.json`): the draft's two points and the chip each time; a placed point dragged and nudged moves on the desk alone; Enter sends one `SURFACE QUAD` with all four as they stand; Discard and Esc (view focused, draft shown) drop it; no `SURFACE QUAD` and no line that changes the project before Enter, the ephemeral `CURSOR … TRAIL` only while the draft is shown |
| `QuadSelectTests` | desk-ux | after Enter the tool is Select and the quad selected; reselect restores its last corner and sends `HANDLE SELECT`; deselect sends `HANDLE SELECT NONE`; an outliner pick shows the quad's projector |
| `QuadCornerDragTests` | desk-ux | a drag sends `SURFACE CORNER … TO … RASTER`, at most one line per 16 ms window of the desk's clock, the newest; release commits |
| `QuadCornerEditTests` | desk-ux | the quad's inspector shows its four corners in wall pixels and no mesh field; a typed corner sends `SURFACE CORNER <sf> <c> TO <x> <y> PX`; an arrow sends `BY 1 0 PX` |
| `PinNudgeTests` | desk-ux | a selected pin's typed pixel and each arrow send `SET <pin> pixel` at the pixel ± 1 |
| `AssistantQuadTests` (Assistant) | desk-ux | a `ScriptedProvider` proposes `SURFACE QUAD … PX` and `SURFACE CORNER <sf> TR BY 1 0 PX`, each decided by the gate as its class and the settings say, queued while armed; the quad and the corner read back from STATE |
| `HandleVerbTests` (Engine) | desk-map | each new form moves exactly its handle (`PIN MOVE … BY 1 0 PX` → the pin's pixel +1.0; `WARP CORNER op TR BY 1 0 PX` → one output pixel); `BY 0 0` and `TO` its place refused; a locked entity refused; a burst one undo step |
| `HandleSelectTests` (Engine) | desk-map | `HANDLE SELECT` of each kind, then `HANDLE NUDGE 1 0` moves that handle by one pixel |
| `HandleEditTests` (App) | desk-map | for every handle kind: typed x, y sends `TO`; each arrow, Shift and Alt sends `BY` with 1, the big step and 0.1; the status bar's words |
| `OverlayHandleTests` (Render) | desk-map | the selected pin's crosshair at its raster point to the pixel on GL (and WARP at the merge); armed, nothing drawn |
| `LoupeTests` (App) | desk-map | at 8× a one-pixel pointer move is one raster pixel |

## 7. What each screen shows

Today a surface row names its source ("quad on P1 · clip"), but no output row, plan tile or PLAY card names what it shows, STATE's
outputs carry no source, a Source-mode output has no source picker, and a surface's Content ▸ menu is built once, so playlists and
generators added later never appear in it and it has no media [today §b].

**The fact, engine-side.** STATE `outputs[].showing` is resolved by the node at each STATE revision from the project, the transports
and the air: `{"what":"canvas","id":"cv_front","name":"Front","now":[{"layer":"ly_1","source":"playlist:pl_show","item":"it_3",
"media":"md_intro","name":"Intro.mov"}]}` for a Canvas output; for a projector output its surfaces with their sources and what plays
on each; for a Source output its source; and `over` when the air covers it (`"pattern grid"`, `"blackout"`, `"panic black"`,
`"freeze"`, `"held"`). STATE's media rows gain `shownOn` (the outputs and surfaces that show each now). The context's output items
carry the same in their words, so Patterns' Eye and an assistant read "op_2: Front — Intro.mov via Show" without the project.

**Everywhere a screen is shown.** PLAY's output cards and the Outputs plan's tiles carry the name and a thumbnail of what plays;
the outliner's output rows and the projector view's corner say it; a media or playlist row carries "on 2 surfaces · 1 output"
(MadMapper's count [ux §1.4]); the output's inspector shows the chain (output → canvas → layer → playlist → item → media) with each
link a click; a Source-mode output gets its source picker. The surface's Content ▸ menu is made again on every push, with Media
entries. An enabled output on a display that shows nothing is amber in preflight ("op_2 shows nothing: its canvas has no visible
layer").

| Test | Package | Holds |
|---|---|---|
| `ShowingTests` (Engine) | desk-map | each output mode in a fixture → `showing` names the chain to the media now playing; a GO moves it at the item change; `PATTERN`, blackout, panic and freeze set `over`; `media[].shownOn` counts |
| `ShowingDeskTests` (App) | desk-map | every view of an output (PLAY card, plan tile, outliner row, projector view, inspector) shows `showing`'s name from the fixture |
| `ContentMenuTests` (App) | desk-map | a playlist added after the surface's menu was first built appears in its Content ▸ on the next push; media entries listed |
| `NothingShownRowTests` | desk-map | the amber row on an output with no visible source; none on a blacked-out output (blackout is the person's) |

## 8. One pattern, media or generator across several outputs

**What exists.** A canvas is a 2D composition with layers; a Canvas-mode output shows a rectangle of it (`SourceRect`), with manual
edges and a plan rectangle; surfaces can sample a canvas region; a new project is one canvas shown by one output; `ALIGN NEW …
CANVAS` makes one shared canvas [today §e]. Media and generators placed on a canvas already span every output that shows it. What
does not: `PATTERN … ON ALL` draws a separate pattern on each output at its own raster, so a grid does not cross the seam; and the desk
has no canvas or layer editor (the flat guide's Span step adds the canvas's, workflows item 3).

**What is added (canvas).**

- `CANVAS SPAN <canvas>|NEW <output> <output>… [SIDE <c>x<r>] [STACK <n 2-4>] [OVERLAP <px>|<pct>%] [KEEP|REPLACE]` — one edit, the
  outputs listed position by position with a position's stacked outputs together, its first the stack's reference: the canvas's size
  from the positions' rasters less the overlaps, each output in Canvas mode with its position's rectangle, the inner edges at the
  overlap, the plan rectangles; a stack's members share their rectangle with no edge between them, so `STACK 2` alone gives two outputs
  the whole canvas and `SIDE 2x1 STACK 2` four outputs two blended positions. It records what it wrote (`Canvas.Span`, §3.1), and
  gives way to a measured warp as §3.2 says.
- `CANVAS SOURCE <canvas> <source> [FIT|FILL|STRETCH]` — what the whole canvas shows: its base layer, one line (a drop of a media or a
  playlist onto a canvas tile sends it).
- `PATTERN <kind>|OFF ON <canvas>` — the pattern drawn once at the canvas's size, over the canvas wherever it is shown (its Canvas
  outputs and the surfaces that sample it); with it, the span card: each output's label, number and resolution in its identity colour,
  its rectangle's border, the overlap's start and width lines (Panasonic's projected markers [ux §1.5]), circles and diagonals across
  every seam. Drawn through the RHI on both backends.
- On a node without NVIDIA Mosaic or Warp and Blend (every consumer GPU, every Home machine), spanning and blending are PlayMap's
  own, as they already are.

| Test | Package | Holds |
|---|---|---|
| `CanvasSpanTests` (Engine) | canvas | two 1920 × 1080 outputs, `SIDE 2x1 OVERLAP 15%` → a 3552 × 1080 canvas, rectangles (0, 0) and (1632, 0), edges 288 px, one undo step; `STACK 2` → equal rectangles, no edges; four outputs `SIDE 2x1 STACK 2 OVERLAP 15%` → op_1 and op_2 on (0, 0), op_3 and op_4 on (1632, 0), the inner edges on all four, none between a stack's members; a count that does not fill the positions refused naming it; a span that would change nothing refused |
| `SpanPatternTests` (Render, GL; WARP at the merge) | canvas | `PATTERN grid ON cv` → the two outputs' rasters, read back, join into one grid across the seam to the pixel (the overlap's pixels equal on both); each output's label drawn in its rectangle; `PATTERN OFF ON cv` → the show back |
| `CanvasSourceTests` | canvas | `CANVAS SOURCE cv playlist:pl` → the base layer; again with the same source refused "already shows it" |

## 9. Every feature from a menu item or a right-click menu

### 9.1 One action table, three doors

The desk's actions (`DeskAction`, each naming its line) are one table, reached three ways: the **menu bar** (File, Edit, View, Map,
Output, Show, Guides, Window, Help; each at most 15 entries at its top level, the catalogue's rows in submenus by category, acting on
the selection where a row names an object; Window ▸ Cards ▸ <block> opens each STATE block's cards), the **Ctrl+K palette** (deterministic ranking, each entry with its shortcut, its menu path and its
line [UX P4]; text that matches no verb becomes `ASK`), and the **right-click menu** of the object it is about. The gate's door words
and the held rules are the action's, wherever it is pressed: a line a Companion key holds is held to confirm on the desk, except
`VIDEO STOP`, `PANIC CLEAR` and `MASTER`, which the desk sends at every press with the air's controls (the owner's call 7).

### 9.2 Generated from the catalogue

Each object's right-click menu is built from the catalogue: every row whose syntax names the object's kind as a placeholder
(`<surface>`, `<output>`, `<projector>`, `<pin>`, `<playlist>`, `<canvas>`, `<viewpoint>`, `<input>` …) is an entry for that kind, its
other values filled from the object, offered as a submenu where they are enumerable (a syntax's alternatives, kinds the node lists,
ids in the project), or, where they are free, taken in the entry's own field — the inline scrub-and-type row for a number (§9.3), a file
picker for a file, a name field for a name — and sent as its line; the palette offers the same entry, its line written up to the
first free value. Hand-built entries keep their order and words where they read better; the catalogue is the coverage. Microsoft's rules
hold [ux §3.2]: only the object's commands, the default first, at most 15 at the top level (the rest grouped by category), an entry
that never applies to the kind hidden — and one refused now shown greyed with the node's words, which tells the truth about the
attempt. Empty 3D space and empty plan space get menus (add a surface, a projector, an output; paste; the view's options).

### 9.3 Interactive menus

A long menu has a filter field at its top; a number's entry is an inline row that scrubs as it is dragged and takes a typed value,
sending its line as it changes (as an inspector field does); recent entries lead; each entry shows its shortcut and, in its tooltip,
its line; every entry has **Copy line** (for Companion, Patterns or the console) and every object **Ask about this…**. A held-key
radial menu in MAP (at most eight entries: quad, select, pin, lattice, mask, on the wall, identify, test card) is desk-shell's stretch
[ux §3.2].

### 9.4 The rule's test

`MenuCoverageTests` (App, headless, every fixture loaded): every catalogue row the desk may send — not internal, not node-to-node —
is reached without typing a line (the owner: "all commands and features should be available through the UI or menu system",
`PLAN.md` §9 decision 22): from the menu bar, acting on the selection where the row names an object, and, where its first
placeholder names an entity, from the right-click menu of that kind (a row with no entity placeholder is reached from the menu bar),
the palette searching the same entries; a free value is taken in the entry's own field (§9.2), never by finishing a line in the
palette; a secret-bearing row is reached as a menu entry that opens its form (the Window menu's Network ▸ Links, the credential
form), never as a line, and the test asserts that door for each; every right-click entry, every `DeskAction` and every shortcut of `ShortcutMap`
(the loupe's L, zoom, Keep quad tool, Reset layout, Simple/Pro, dim among them) is also in the menu bar, by Microsoft's
rule ("never only in a context menu" [ux §3.2]), nor only on a key, in the palette or in the console; every STATE block's cards open from a menu entry (Window ▸ its
pane once desk-map's area panes exist, Window ▸ Cards ▸ <block> before, or its object's right-click); no menu, of the bar or a
right-click, over 15 entries at its top level, the rest in submenus by catalogue category; every entry's line parses (`VerbSyntax`) and passes
`DeskVerbRuleTests`' rules; a held row is held wherever it is pressed but for the three the owner's call keeps. It lets off no row: a
row missing from the menu bar, or from its object's right-click menu where it names one, fails the merge that adds it, unless the owner signs it off by name in `docs/DESK.md`'s list, which the test reads
and which starts empty.

`InteractiveMenuTests` (App, headless): a long menu's filter narrows it to the entries whose words match; a number's inline row sends
its line as it is scrubbed (through the throttle, the newest per window) and once more on a typed value; an entry used rises into the
recent group; Copy line puts exactly the entry's line on the clipboard; "Ask about this…" opens the assistant's pane with the object's
id.

## 10. Panels: draggable, dockable, collapsible

**The library.** Dock for Avalonia (`Dock.Avalonia`, MIT), its Avalonia-12 line (12.1.0.x, which needs Avalonia 12.1.4 or later; the
desk pins 12.1.4) [ux §3.1]: document and tool panes, split layouts with drag handles, panes that pin, auto-hide or float, docking
targets, and layout persistence through a source-generated `System.Text.Json` serializer. It is pinned to one exact release at least
two weeks old when desk-shell starts, and wrapped behind a desk seam (`IDeskLayout`), so a replacement is one folder; Actipro
Docking is the commercial alternative and is not used without the owner's decision. Its first day answers M18 (`PLAN.md` §1.5): if
Dock does not run on the desk's Avalonia headless, keep floating panes inside the window and round-trip a layout, the desk builds its
own splitters, collapsible rails and drag-to-dock — a pane's header dragged to any of the four edges or onto a rail — behind the same
seam; only floating is lost, and the owner is told.

**The rules.**

- The outliner, the inspector, the media bin, the cards, the drawers, the assistant, the console, the journal and a guide's step rail
  are panes: dragged to an edge or onto another pane, split by splitters, pinned to collapse into a rail along the edge that keeps the
  pane's title and its worst light (a collapsed Health rail is red when a row is red).
- Floating panes are **managed**: they stay inside the desk's window and cannot cross monitors, so no pane can ever land on a projector
  output — the air — or break its Independent Flip. On Windows 11 Home, where every output is a desktop monitor, this matters most.
- The show strip, the attention bar, PANIC, Blackout and the mode frame are not panes: fixed, always visible.
- **Layouts persist per user**, in `desk.json` (`%APPDATA%\PlayMap\desk.json`, the Windows user's own, beside the desk's Simple/Pro and
  Esc preferences): one layout per workspace and per guide, with a version; Reset layout per workspace; an unreadable or older layout
  is the default with a toast, never an error. Today's columns (MAP 290 | * | 360, PLAY 300 | * | 340) are the default layouts.

| Test | Package | Holds |
|---|---|---|
| `DockLayoutTests` (App, headless) | desk-shell | a layout changed (a pane moved, split, collapsed) → desk.json written once; read back → the same tree; an unreadable file → the defaults and the toast; Reset → the default; a floating pane's bounds stay inside the window at every window size of `docs/DESK.md`'s list |
| `FixedPartsTests` | desk-shell | the show strip, the attention bar, PANIC and Blackout are in no dock and visible in every layout |
| `CollapsedLightTests` | desk-shell | a collapsed pane's rail shows its worst light and its title |

## 11. Colour: status, identity and stage

Three channels, each with one job; none is ever the only cue (ISA-101, IEC 60601-1-8, WCAG 1.4.1 [ux §4]); a theme (dark room,
daylight, desk-ux) recolours neutrals, never a meaning.

| Channel | Its job | Tokens | Rules |
|---|---|---|---|
| **Status** (desk-ux's rules) | the air and the node's condition | red (fault, the air at risk, armed) `#E5484D`, amber (attention) `#D29922`, green (ready, sparingly) `#3FB950` — today's `LightRed`, `LightAmber`, `LightGreen` — and grey `#6F7882` (`LightGrey`) for off, unknown and information | reserved for this job alone; every light a shape and a word (✓ △ ✕ ■, and ○ or **i** on grey); ok, warn and fault each 3:1 or more against every theme's panels (`PLAN.md` §3.6 item 4: three lights 3:1 apart cannot all be 3:1 from a panel as dark as `#191C20`); a flash only for an unacknowledged red or amber, once per event, at most 2 Hz, never text; red, amber and green at chroma C* ≥ 40; grey is the channel's achromatic token, exempt from that rule and never the only cue — its shape and word carry it; the edition's information rows are grey with **i**; the controllers rows (the owner's call 9) keep their own lights |
| **Identity** (new) | which projector, output or client | dark themes: P1 azure `#3C9AF2`, P2 lavender `#D6C3FA`, P3 magenta `#CC64C3`; daylight: P1 `#1F79CC`, P2 `#524A98`, P3 `#B5479E`; then repeating — always with the number badge (P1, P2 …) | never a status hue (amber, orange, yellow, red, green) and never a grey: today's projector `#FFB74D` collides with amber and goes for new projectors; a stored colour within ΔE₀₀ 20 of a status token stays the project's and is drawn in its projector's identity token, the inspector saying so ("stored #FFB74D reads as amber: shown as P1's azure"); outlines, pins, cameras, the wall's line-up overlay and Identify use it |
| **Stage** (new) | where in the workflow | low-chroma tints: Setup and Outputs `#AAC4D9` (steel), MAP `#9DCAC3` (teal), SCAN `#C2C3A8` (sage), PLAY and the show none | a 2–4 % tint over the panel background and a 2 px bar on the workspace tab, pane headers and a guide's step headers; red never marks a stage; the armed frame and the blind grey override every tint |

Computed from the tokens above (CIEDE2000; colour-vision simulation by Machado's matrices at full severity; contrast as WCAG's):

| Token | Least ΔE₀₀ to a status token | Least ΔE₀₀ under protanopia, deuteranopia, tritanopia | C* | Contrast on the dark panel `#191C20` |
|---|---|---|---|---|
| P1 `#3C9AF2` | 20.9 (grey) | 11.1 | 52 | 5.8 : 1 |
| P2 `#D6C3FA` | 30.2 (grey) | 24.8 | 30 | 10.6 : 1 |
| P3 `#CC64C3` | 22.4 (red) | 12.7 | 62 | 5.0 : 1 |
| Setup `#AAC4D9` | 23.9 (grey) | 16.2 | 14 | — |
| MAP `#9DCAC3` | 24.4 (green) | 13.1 | 16 | — |
| SCAN `#C2C3A8` | 23.2 (green) | 15.3 | 14 | — |

The identity tokens are at least ΔE₀₀ 23 from one another (P1–P2 28.6, P1–P3 38.5, P2–P3 23.9); the daylight set has 4.1, 6.8 and 4.4 :
1 on a `#F3F4F6` panel, and its distances are computed against desk-ux's daylight status tokens (`DeskPalette.Daylight`: green
`#1A7F37`, amber `#9A6700`, red `#CF222E`, grey `#5F6873`). Today's tokens fail these rules (stage `#7F8C9A` is 7.6 from the status
grey, SCAN `#8E86B5` has C* 27, the pale grey identity reads as the status grey), which is why they are not used.

`AreaColourTests` (App, from each theme's own tokens): every identity and stage token at least ΔE₀₀ 20 from every status token, and at
least 10 under simulated protanopia, deuteranopia and tritanopia; identity tokens at least ΔE₀₀ 20 from one another and at C* ≥ 20;
stage tokens at C* ≤ 20; red, amber and green at C* ≥ 40, grey exempt; every identity token 3:1 against its theme's panel; no stage
token used by a status style and no status token by a stage or identity style; a stored projector colour within ΔE₀₀ 20 of a status
token drawn in its identity token; desk-ux's `StatusRuleTests` unchanged.

## 12. Lessons from games and medical devices, applied to the desk

| Lesson | The rule on the desk | Test |
|---|---|---|
| **A frame budget** (games; Holzmann's no allocation after initialisation [ux §6.3]) | a STATE push is one diff pass on the UI thread (`StateSnapshot`, read once), and an action raises only what moved (as today); a selection change is local, never a round trip, so it is instant | `PushBudgetTests` (desk-gpu item 6): the property changes per push equal the fields that moved; a push that moved nothing raises nothing; a selection sends no line; the push's UI-thread time recorded, never asserted on CI (`PLAN.md` §2.1) |
| **Input-to-photon** (NVIDIA's PC latency, PresentMon's click-to-photon [ux §6.1]) | a drag's line leaves within one throttle window (16 ms, the newest); the node draws a handle move within two frames of its arrival, as cursors are measured today (`air.overlay.cursorP95Ms`), and publishes `air.overlay.handleP95Ms` | `QuadCornerDragTests` (one line per window, by count); `HandleLatencyTests` (Render): 300 handle moves drawn within two frames at p95, counted in frames, no byte allocated |
| **Persistent tool modes** (Blender's tool per workspace [ux §3.5]) | the tool and the selected handle are remembered per workspace and per object; the mode is always on screen (the status bar, the mode frame) | `ToolMemoryTests` (desk-map item 2): PLAY and back restores MAP's tool and handle; another object's handle restored when it is reselected; the status bar names the mode in every case |
| **Undo** (Epson's nudge undo; medical "recover from a use error") | every hand edit, nudge and automatic result is one undo step through the store; `UNDO TO` takes a run back; Oops (desk-ux) lists them | `HandOverAutoTests`, `UndoListTests` |
| **Watchdogs** (AAMI's detect → notify → mitigate → record [ux §6.4]) | the air never depends on the desk (desk-gpu's desk-kill test); the desk's UI thread is watched by a background counter, and a stall over 250 ms is one desk journal row and one toast, never a flash and never a line to the node | `UiStallTests` (desk-gpu item 6: a blocked dispatcher on a manual clock → one row, one toast) |
| **Servers off the frame path** (games' and devices' bounded queues [ux §6.5]) | the line wire, MCP and HTTP run on their own threads with bounded queues; a flood is refused at the queue with words, never felt by the render | `ServerFloodTests` (wp3-stability item 7: a burst on every server while the render draws on a manual clock → its frame and present counts advance one per tick) |
| **Hazard-related use scenarios** (IEC 62366-1 [ux §5]) | each hazard is held by a test that asserts published state: editing a live surface while armed (the gate); a pane dropped over an output (managed floating); the blend left on the wrong output (Identify, identity colours, `showing`); a stale preview taken for live (preview age, desk-ux); a hand edit lost to a re-run (§3); an assistant's line run unasked (the gate) | the tests named in §3, §4, §7, §10 and desk-ux's |

## 13. What the node stores, how, and why no server database is on the frame path

The node keeps files, each with one writer: the project as JSON written whole through a temporary file and a rename
(`ProjectStore` the only writer, readers on immutable snapshots); the node's settings (one writer, the network host, `docs/round2/
CARRYOVER.md`); the recovery record; the journal as appended JSON lines; media named by their SHA-256; keys under DPAPI; the optimise
queue; the align and scan run folders beside the project, written once. The desk keeps `desk.json` per user. Nothing on the render,
present or capture threads reads or writes a file; the HTTP, MCP and line-wire servers run off the frame lanes with bounded queues and
back-pressure, and wp3-stability's `ServerFloodTests` hold that a flood on them leaves the render's counts unchanged; the journal is a
bounded channel, and wp3-stability makes its drop under a burst a counted fact in STATE instead of a silent one (today it drops its
oldest rows unseen past a 4 096-row burst [today §j]).

No database is used, and none sits on the frame path: an append-only log and immutable snapshots are the game and medical pattern for a
real-time core (deterministic replay, a crash-safe atomic replace, no lock held across a frame, nothing to administer on a Home
machine) [ux §6.5]. If an index is ever needed — a media library of thousands of files, a search of calibration history — it is SQLite
in WAL mode on a lane of its own, never on the render, present or capture threads, and not this round.

## 14. Rows and STATE

| Row | Syntax | Package | Risk / class |
|---|---|---|---|
| `GUIDE` | `GUIDE LIST \| GUIDE FLAT\|WALL\|OBJECT\|FACADE\|FIRST [<subject>]` | workflows | Low / Query |
| `GUIDE BEGIN`, `GUIDE END` | `GUIDE BEGIN\|END FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject>` | workflows | Review / Project |
| `GUIDE CHECK`, `GUIDE UNCHECK` | `GUIDE CHECK\|UNCHECK FLAT\|WALL\|OBJECT\|FACADE\|FIRST <subject> <step>` | workflows | Review / Project (refused from an assistant's session) |
| `CANVAS SPAN` | `CANVAS SPAN <canvas>\|NEW <output> <output>… [SIDE <c>x<r>] [STACK <n 2-4>] [OVERLAP <px>\|<pct>%] [KEEP\|REPLACE]` | canvas | Review / Layout |
| `CANVAS SOURCE` | `CANVAS SOURCE <canvas> <source> [FIT\|FILL\|STRETCH]` | canvas | High / Content |
| `PATTERN` (extended) | `PATTERN <kind>\|OFF [ON <output>\|<canvas>\|ALL]` | canvas | as `PATTERN` |
| `HAND` | `HAND <output>\|<projector>\|<surface> \| HAND CLEAR <output> WARP\|BLEND\|COLOR\|MASKS\|ALL` | canvas | Low / Query; CLEAR Review / Layout |
| `PIN MOVE` | `PIN MOVE <pin> TO\|BY <x> <y> [PX]` | desk-map | Review / Target (a gesture) |
| `WARP CORNER` | `WARP CORNER <output> TL\|TR\|BR\|BL TO\|BY <x> <y> [PX\|NORM]` | desk-map | Review / Layout (a gesture) |
| `WARP NODE` (extended) | `… \| WARP NODE <output> <c> <r> TO\|BY <x> <y> PX` | desk-map | as `WARP NODE` |
| `HANDLE SELECT` (extended) | `… \| HANDLE SELECT <pin> \| HANDLE SELECT <output> WARP TL\|TR\|BR\|BL\|<c>,<r> \| HANDLE SELECT <output> MASK <i> <node>` | desk-map | as `HANDLE SELECT` |
| `VIEWPOINT FIT` | `VIEWPOINT FIT <viewpoint> <surface>… [MARGIN <pct>] [KEEP\|REPLACE]` | workflows | Review / Target |
| `SURFACE MAP` | `SURFACE MAP <surface> UV \| CANVAS <canvas> [<x> <y> <w> <h>] \| PROJECTION FROM <viewpoint>` | workflows | Review / Content |

STATE: `guides` (top level, workflows); `outputs[].showing` and `media[].shownOn` (desk-map); `outputs[].hand` (canvas); a typed pose
as a calibration result of the existing source `manual` (workflows); `air.overlay.handleP95Ms` (desk-map). The spine reserves each in
its place and lands the model's fields: `Canvas.Span` (positions, stacks and what a span wrote), `Viewpoint.Fit`, `Project.Guides`, and
`Blend.Gamma` as the hand's override (0: the measured γ already kept in `Measured.Color.Gamma`), with the reader's γ rule
(`PLAN.md` §3.1 item 4). Each new block has its fixture and card descriptor; the context carries the guides' items and, through the
spine's `ShowingWords` hook, the outputs' `showing` words.

## 15. Risks

- **Scope.** Five packages beside the sixteen, which the owner added to the round (`PLAN.md` §9, decision 1): about 122 hours, each at
  its cut line with its stretch below it.
- **Dock for Avalonia** has essentially one maintainer and frequent releases [ux §3.1]: pinned exactly, behind a seam, M18 on the first
  day, the desk's own splitters, rails and drag-to-dock as the measured fallback, only floating lost.
- **Generated menus grow long.** The 15-entry cap and category groups are tested; hand-built entries keep their order where they read
  better.
- **Two layers confuse a person** ("why does my corner sit 3 px off what the camera found?"): `HAND` says both in words, the card lists
  what a re-run kept, and `HAND CLEAR` is one line.
- **A guide's judged steps are only as good as the eye.** Check records the inputs, and a moved input un-checks the step; a camera's
  score replaces the eye where a camera exists.
- **No lab** (the owner's answer 5): the three guides are proven on the simulators and headless; L24 (`PLAN.md` §7) holds the real
  two-projector blend and stack and the four-projector stack and blend, and L19 the operators' session, both reported not run until a
  lab exists.
