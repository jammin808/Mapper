# wp2-fx on the wire

Effects in chains (`docs/EFFECTS.md`). A `<target>` is an output (id, name or number), a layer, a canvas, a surface or a
playlist item (id or name). A `<slot>` is an effect's id (`fx…`) or `<target>#<n>`, its place in the chain counting from 1.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `FX KINDS` | `FX KINDS [<family>\|<search>]` | JSON: each kind's `id`, `format`, `name`, `family`, `categories`, `role`, `words`, `params` (as `GENERATOR KINDS`), `images`, `traits` `{history, audio, geometry, native, mayFlash}` | low | no |
| `FX LIST` | `FX LIST <target>\|ALL` | JSON: `[{owner, ownerKind, name, slots:[…as STATE…]}]` | low | no |
| `FX ADD` | `FX ADD <target> <kind> [AT <position>] [<name>]` | the slot id; the effect words say "compiling" (an output's chain) or that the chain is kept, not drawn | review | yes |
| `FX REMOVE` | `FX REMOVE <slot>` | effect words | review | yes |
| `FX MOVE` | `FX MOVE <slot> <position>` | effect words; the same position is refused | review | yes |
| `FX SET` | `FX SET <slot> <param>\|mix\|blend\|scale\|name <value>`, `FX SET <slot> <param>\|mix\|scale BY <delta>` | effect words; edits coalesce per slot and field; a number `BY` moves by that fraction of its range, a choice by that many steps | review | yes |
| `FX BYPASS` | `FX BYPASS <slot>\|<target>\|ALL ON\|OFF\|TOGGLE` | the number of slots changed | review | yes |
| `FX RESET` | `FX RESET <slot>` | parameters back to their defaults | review | yes |
| `FX IMPORT` | `FX IMPORT <path>\|<json>` | JSON `{id, kind, check}`: the asset id, its kind `isf:<id>` and the check report | review | yes |
| `FX CHECK` | `FX CHECK <asset>\|<kind>\|<json>` | JSON report (below) | low | no |

`FX IMPORT` reads a `.fs` file with the `.vs` beside it and its `IMPORTED` pictures relative to it, or the JSON form
`{"name","fs","vs"?,"images"?:{name: base64}}`. The show carries the file whole (an `fxAssets` entry with its sources and pictures,
content hash SHA-256); the same file twice is refused with the asset it already is. `FX IMPORT` and `FX CHECK` answer off the
control lane.

**Refusals** (every one leaves the show unchanged): an unknown target, slot or kind; a position out of range; a value that would
not change ("radius is already 12.5", "… is already bypassed", "… already at their defaults"); a parameter the kind does not have
(the answer lists those it has); a file that does not read as ISF (with its line); a file whose imported pictures are not given.

**Generic edits.** `ADD fx <owner> {json}` keeps a slot of a kind this node does not have (it shows `missing`) and refuses a
parameter its kind does not have.

### The check report

```json
{"ok": false, "role": "filter", "inputs": ["inputImage", "gain"], "passes": 1, "persistent": [],
 "issues": [{"line": 3, "col": 0, "words": "IMG_PIXEL takes 2 or 3 values, here 1"}],
 "dialects": {"glsl330": {"ok": false, "words": "…"}, "es300": {"ok": null, "words": "this node's context compiles the other dialect"}}}
```

`ok` is true when the file reads, translates without issues and did not fail to compile here; a dialect this node cannot compile
is `null` with words (with no renderer: "no GL context on this node").

## STATE

```json
"fx": {"budgetMs": 4, "library": {"builtin": 10, "isf": 1, "ffgl": 0, "quarantined": 0},
       "compile": {"lane": "shared-context|render-thread|none", "pending": 0, "words": ""},
       "slots": [{"id": "fx…", "owner": "op_1", "ownerKind": "output|layer|canvas|surface|item", "index": 0, "kind": "fx.blur",
                  "name": "Blur", "state": "ok|compiling|faulted|missing|bypassed|idle", "gpuMs": 0.31, "bypass": false, "mix": 1,
                  "blend": "normal", "scale": 1, "deterministic": true, "history": false, "live": {}, "words": ""}],
       "plugins": {"enabled": false, "inProcess": 0, "words": "FFGL plugins are not hosted by this build"}},
"outputs": [{…, "fx": null | {"slots": 2, "costMs": 1.4, "light": "green|amber|red|grey"}}]
```

A slot's `state` is what the renderer observed: `compiling` from `FX ADD` until the renderer draws it, then `ok`, or `faulted`
with the compiler's words at the file's line ("line 3: `nowhere' undeclared"). `idle` names why it is not drawn ("not drawn: its
output is off", "not drawn: this build draws effects on outputs only, not on a layer", "no renderer on this node"). A history
effect is `"deterministic": false` with the words "integrates frame to frame: not frame-exact across nodes". The fixture is
`tests/fixtures/state/fx.json`; the desk's cards are `fx.cards.json`.

## Context

Item `fx` (kind `fx`): red when an output's chain costs more than twice the budget; amber when a slot is faulted or missing or a
chain is over budget; grey when nothing is drawn; words such as "4 effects · Broken faulted: line 3: … · 1 not frame-exact
across nodes (breaks mirror backup)".

## INDEX

Section `fx`: one row per slot (`parent` = its chain's owner, its light and state); preset keys **Bypass** (`FX BYPASS $(id)
TOGGLE`, instant) and **Mix ±** (`FX SET $(id) mix BY ±0.05`, hold) for an encoder.

## Preflight

Section `effects`: a row per slot on an enabled output's chain — green drawing, amber still compiling, red faulted or missing
with the fix `FX BYPASS <slot> ON` — or one green row when no effect runs on an output.

## Papers note

For the integration docs pass:

- `WIRE.md`: the ten FX rows above and STATE `fx` / `outputs[].fx`; `FX PLUGINS` and `FX TRUST` left the catalogue (FFGL is next
  round). `FX BIND`/`FX UNBIND` remain aliases of audio's `MOD BIND`/`MOD UNBIND`.
- `ARCHITECTURE.md`: the effects pass (an `IFramePass` at order 100: output chains before the output stage), the compile lane on
  a shared context, the ISF translator in Core (`Core/Effects`), the built-in library embedded in Render (`Render/Effects/Library`).
- `ROADMAP.md`: ISF effects on output chains done (324/327 of the public ISF files at GLSL 3.30); layer, surface and item chains
  wait for the renderer's call points; FFGL, bindings, geometry-aware effects and the rest of the library are next round.
- Spine amendment asked for: `IFramePass` (or `SourceResolver`/`StageRenderer`) call points for a layer's and a surface's picture,
  and the playlist item's start for item chains; `PassFrame` carrying the frame's pattern/safe-look override per output (the pass
  reads it today from `FxPass.Prepare(in FrameInputs)`, called by the render host's compose hook).
