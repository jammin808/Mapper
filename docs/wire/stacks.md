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
| `STACK ALIGN` | `STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]` | JSON `{stack, scan, master, members:[{projector, output, master, residualPx, p95Px, beforePx, pairs, heldOut, snapshot, fallback}], residualPx, staged, words}`. Each member but the master gets a residual lattice (GRID, 33x19 by default, 2–65 a side) fitted to the master's picture from the scan's decodes; `residualPx`/`p95Px` are the held-out misfit after, `beforePx` before, in the member's pixels. Applied through the calibration ledger as one edit (source `stack`; each output's correction as it stood kept as its fallback); the lattice is the output's `measured.lattice`, drawn as the warp's first layer. Refused: no such scan; a member the scan did not see, or whose view it no longer holds (scan again: the line is given); a member with no output in Projector mode, or scanned at another raster than its output's; members at different poses with no model to carry the master's picture; a fit that would change nothing | review | yes |
| `STACK MATCH` | `STACK MATCH <stack> FROM <scan>` | JSON `{stack, scan, members:[{projector, output, gain:[r,g,b], duv}], staged, words}`: per-channel gains (each ≤ 1, the largest 1) bringing each member's white to the master's chromaticity, from the scan's colour white frames (`duv` is Δu′v′ from the master before). Written as the output's `measured.color` through the ledger as one edit; level is left alone. Refused: a scan without a colour white frame for a member ("scan the stack with a colour camera"), and as `STACK ALIGN` | review | yes |

`REMOVE <sk…>` removes a stack; `ADD stack <json>` and `SET <sk…> …` edit one (members must exist, a projector is in one stack only,
the master is a member). A projector in a stack cannot be removed while the stack names it.

**Show Mode, blind edit, locks, the group.** `SITE GUIDE` and `STACK LIST` are queries. `STACK NEW`, `STACK ALIGN` and `STACK MATCH` are
mapping: refused while armed for a person, proposed by an assistant, held by `LOCK AREA mapping` and, for ALIGN and MATCH, by a
lock on a member's output (the lock's words); a lock on the stack holds edits of the stack itself (`SET`, `REMOVE`). From the session that holds the blind draft they run into the draft (`staged` true,
words "staged in the blind draft:"), the live stage, STATE and the air unchanged until `BLIND COMMIT`. A stopping node refuses them.
A sync group shares none of them: each node keeps its own stacks.

## STATE

`stage.site` (when a site is set) gains `"next": {"step", "state", "words", "line"} | null` — the guide's next step on the live stage.

`stage` gains, after `surveys`:

```json
"stacks": [{"id": "sk…", "name": "S1", "members": ["pj1", "pj2"], "master": "pj1", "aligned": true, "matched": true,
            "residualPx": 0.03, "failover": "hold", "degraded": [], "gain": 1}]
```

`aligned`: every member but the master carries this stack's lattice; `residualPx`: the worst member's held-out residual.
`stage.projectors[]` gains `"stack": "sk…" | null`. `outputs[].measured` (align's key) carries a member's lattice and gains.

## Context

`stack:<id>` (kind `stack`): amber "not aligned — STACK ALIGN <id> FROM <scan>" until aligned, amber when a member lands more than
0.5 px from the master, green with the residual otherwise.

## Preflight

Section `site`, one row per stack: green when every member is aligned within 0.5 px; amber otherwise, with the fix `STACK ALIGN <id>
FROM <scan>` (the newest scan of all its members), or `SCAN START <camera> <members>` when none has scanned them.

## INDEX

Section `site` (preset "Site"): per stack an `ALIGN <name>` row (`STACK ALIGN <id> FROM <scan>`, lit by its alignment) and a
`MATCH <name>` row (`STACK MATCH …`), and a `NEXT` row (`SITE GUIDE`) whose state is the guide's next step.

## Papers note

Stacks (STACK NEW, LIST, ALIGN, MATCH; the blend collapse; the measured lattice as the warp's first layer), the facade guide (SITE
GUIDE, `stage.site.next`), geodesy (Krüger transverse Mercator, UTM, ECEF/ENU, EPSG UTM families, WKT sniffing; sites named by
their system) shipped. WIRE.md wants the five verbs and the STATE keys above; ARCHITECTURE wants the stack collapse and the measured
lattice layer; ROADMAP M23 moves stacks and the guide to done.
