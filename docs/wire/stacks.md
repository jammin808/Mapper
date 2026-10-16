# wp2-stacks on the wire

Stacks, geodesy and the facade guide (`docs/FACADE.md`, `docs/MAPPING.md`); this package continues wp2-facade and its rows sit in
facade's block of the catalogue. A `<stack>` is a stack's id (`sk…`) or name; a `<projector>` a projector's id or name; a `<scan>` a
scan's id (`sc…`) or name.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `SITE GUIDE` (alias `FACADE GUIDE`) | `SITE GUIDE` | JSON `{steps:[{step, state, words, line}], next:{…}\|null, words}`. Steps in order: `survey`, `controls`, `pins`, `solve`, `stacks`, `blend`, `score`; state `done`, `waiting`, `running` (a survey importing) or `failed`; `line` is the line that does the step (`<path>`, `<ratio>`, `<camera>`, `<scan>`, `<x>`/`<y>` where only the operator knows the value). The next step is the first not done. From the session that holds a blind draft it reads the draft (words begin "in the blind draft:") | low | no |
| `STACK NEW` | `STACK NEW <name> <projector> <projector> [<projector>…] [MASTER <projector>]` | JSON `{id, members, master, staged, words}`. The master is the first named unless `MASTER` names another member. Refused: a projector named twice, one in another stack, a master that is no member, a name in use, and a member lighting less than 80 % of the master's picture ("a stack shares one area; blend them instead") | review | yes |
| `STACK LIST` | `STACK LIST` | JSON `{stacks:[{id, name, members, master, aligned, matched, residualPx, failover, degraded, gain, gains:[[r,g,b]…]}]}` (gains per member, in member order) | low | no |
| `STACK ALIGN` | `STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]` | JSON `{stack, scan, view, master, members:[{projector, output, master, residualPx, p95Px, beforePx, pairs, heldOut, snapshot, fallback}], residualPx, staged, words}`. Each member but the master gets a residual lattice (GRID, 33x19 by default, 2–65 a side) fitted to the master's picture from the scan's decodes, through the member's own warp (the lattice is drawn under it, so registration holds with a corner pin or lattice on the member); the master's output is left as it is; `residualPx`/`p95Px` are the held-out misfit after, `beforePx` before, in the member's pixels. The pairs come from one camera place: `view` is the scan's view they came from — of a scan of many views, the one its last solve used that saw every member ("" for a scan that keeps no views). Applied through the calibration ledger as one edit (source `stack`; each output's correction as it stood kept as its fallback); the lattice is the output's `measured.lattice`, drawn as the warp's first layer, with `measured.fittedTo` (the member's pose in the master's frame and both projectors' optics as they stood). Refused: no such scan; a member the scan did not see, or whose view it no longer holds (scan again: the line is given); a scan of many views none of which saw every member, or whose only such view was dropped (`SCAN START <camera> <members> INTO <scan>` adds one); a member with no output in Projector mode, or scanned at another raster than its output's; a member an alignment draws through its measured warp grid (a lattice on it would register it twice: `ALIGN CLEAR <output>`); members at different poses with no model to carry the master's picture; a fit that would change nothing | review | yes |
| `STACK FAILOVER` | `STACK FAILOVER <stack> HOLD\|MATCH` | JSON `{stack, failover, staged, words}`. What the stack does when a member fails (a measured fact: the member's projector in standby or cooling down, or reporting an error; or its display output not open — a closed shutter is no loss, since PlayMap and the operator close shutters on purpose). HOLD (the default) tells it and changes nothing; MATCH dims every other output of the stack's blend group (its members' outputs and those the solved blend overlaps with them) to Σ remaining lumens ÷ Σ lumens, a runtime degrade gain (not an edit, not in undo) that fades down in 0.5 s and back up over 2 s on the leader's clock when the member returns, composed with any degrade gain another part sets. Each loss and return is journalled (kind `stack`) with its cause. Refused when it would change nothing | review | yes |
| `PLAN LIGHT` | `PLAN LIGHT [<surface>\|ALL] [AMBIENT <lux>] [ALBEDO <0-1>] [TARGET <lux>] [DERATE <0-1>]` | JSON `{surfaces:[{id, name, areaM2, coverage, luxP5, luxP50, luxP95, nitsP50, contrastP5, pitchMmP50, pitchMmP95, stackDepth, deficitLm, words}], words}`: per surface the predicted illuminance from the projectors as placed — E = derate·Φ·density²/(W·H)·U, the pixel density (px/m on the surface) saying how large each projector's pixels land and U its fall-off to the corners (uniformity 0.85) — stack members adding, separate projectors with light sharing an overlap evenly; luminance ρE/π (albedo 0.5), contrast (E + ambient)/ambient (−1 without ambient), pitch from the finest projector, and the lumens short of TARGET at p5 (100 lux by default). Defaults DERATE 0.7, AMBIENT 0. A projector with no lumens set lights nothing (the words name it). Off the lane; reads the session's own project | low | no |
| `STACK MATCH` | `STACK MATCH <stack> FROM <scan>` | JSON `{stack, scan, view, members:[{projector, output, gain:[r,g,b], duv}], staged, words}`: per-channel gains (each ≤ 1, the largest 1) bringing each member's white to the master's chromaticity, from the scan's colour white frames over the area every member lights (`duv` is Δu′v′ from the master before). The white frames hold the ambient light too, the same for every member, so a dark scene matches best (the words say so). Written as the output's `measured.color` through the ledger as one edit; level is left alone. Refused: a scan without a colour white frame for a member ("scan the stack with a colour camera"), and as `STACK ALIGN` | review | yes |

`REMOVE <sk…>` removes a stack; `ADD stack <json>` and `SET <sk…> …` edit one (members must exist, a projector is in one stack only,
the master is a member). A projector in a stack cannot be removed while the stack names it.

**Show Mode, blind edit, locks, the group.** `SITE GUIDE`, `STACK LIST` and `PLAN LIGHT` are queries. `STACK NEW`, `STACK ALIGN`, `STACK MATCH` and `STACK FAILOVER` are
mapping: refused while armed for a person, proposed by an assistant, held by `LOCK AREA mapping` and, for ALIGN and MATCH, by a
lock on a member's output (the lock's words); a lock on the stack holds edits of the stack itself (`SET`, `REMOVE`, `STACK FAILOVER`),
its words naming it ("S1 (stack) is locked by …"). From the session that holds the blind draft they run into the draft (`staged` true,
words "staged in the blind draft:"), the live stage, STATE and the air unchanged until `BLIND COMMIT`. A stopping node refuses them.
A sync group shares none of them: each node keeps its own stacks.

## STATE

`stage.site` (when a site is set) gains `"next": {"step", "state", "words", "line"} | null` — the guide's next step on the live stage.

`stage` gains, after `surveys`:

```json
"stacks": [{"id": "sk…", "name": "S1", "members": ["pj1", "pj2"], "master": "pj1", "aligned": true, "matched": true,
            "residualPx": 0.03, "failover": "hold", "degraded": [], "gain": 1}]
```

`aligned`: every member but the master carries this stack's lattice, fitted to the poses and optics they stand at now (a member or
master solved again since, recalled or set by hand to another pose leaves it false; a move of the whole site does not);
`residualPx`: the worst member's held-out residual; `degraded`: the members lost now; `gain`: the degrade gain its blend group heads to
(1 under HOLD or with nothing lost). A solve or re-solve that leaves a stack so says it in its words ("S1 was aligned with P2 where it
stood — STACK ALIGN … fits it again").
`stage.projectors[]` gains `"stack": "sk…" | null`. `outputs[].measured` (align's key) carries a member's lattice and gains.

## Context

`stack:<id>` (kind `stack`): red while a member is lost ("S1 lost P3 — matched: the building at 67 %", or "— held"); amber "P2 or P1
moved since S1 was aligned — STACK ALIGN <id> FROM <scan>" when its lattice was fitted to poses that are gone; amber "not aligned —
STACK ALIGN <id> FROM <scan>" until aligned, amber when a member lands more than 0.5 px from the master, green with the residual
otherwise.

## Preflight

Section `site`, one row per stack (`stack:<id>`): red while a member is lost (fix `STACK FAILOVER <id> MATCH` under HOLD); green when
every member is aligned within 0.5 px; amber otherwise — "moved since aligned" when its lattice was fitted to poses that are gone,
"not aligned", or the residual — with the fix `STACK ALIGN <id> FROM <scan>` (the newest scan of all its members), or
`SCAN START <camera> <members>` when none has scanned them.

## INDEX

Section `site` (preset "Site"): per stack an `ALIGN <name>` row (`STACK ALIGN <id> FROM <scan>`, lit by its alignment, its state the
residual, "moved since aligned" or "not aligned") and a `MATCH <name>` row (`STACK MATCH …`), and a `NEXT` row (`SITE GUIDE`) whose
state is the guide's next step.

