# wp2-fx on the wire

Effects in chains (`docs/EFFECTS.md`). A `<target>` is an output (id, name or number), a layer, a canvas, a surface or a
playlist item (id or name). A `<slot>` is an effect's id (`fx…`) or `<target>#<n>`, its place in the chain counting from 1.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `FX KINDS` | `FX KINDS [<family>\|<search>]` | JSON: each kind's `id`, `format`, `name`, `family`, `categories`, `role`, `words`, `params` (as `GENERATOR KINDS`), `images`, `traits` `{history, audio, geometry, native, mayFlash}` | low | no |
| `FX LIST` | `FX LIST <target>\|ALL` | JSON: `[{owner, ownerKind, name, slots:[…as STATE…]}]` | low | no |
| `FX ADD` | `FX ADD <target> <kind> [AT <position>] [<name>]` | the slot id; the effect words say "compiling" (an output's chain), "compiling when its layer\|surface\|item is on the air", or, on a canvas, that its chain is kept, not drawn; an item's slot counts its time from the item's start (origin 0), any other's from the instant it was added | review | yes |
| `FX REMOVE` | `FX REMOVE <slot>` | effect words | review | yes |
| `FX MOVE` | `FX MOVE <slot> <position>` | effect words; the same position is refused | review | yes |
| `FX SET` | `FX SET <slot> <param>\|mix\|blend\|scale\|name <value>`, `FX SET <slot> <param>\|mix\|scale BY <delta>` | effect words; edits coalesce per slot and field; a number `BY` moves by that fraction of its range, a choice by that many steps | review | yes |
| `FX BYPASS` | `FX BYPASS <slot>\|<target>\|ALL ON\|OFF\|TOGGLE` | the number of slots changed | review | yes |
| `FX RESET` | `FX RESET <slot>` | parameters back to their defaults | review | yes |
| `FX IMPORT` | `FX IMPORT <path>\|<json>` | JSON `{id, kind, check}`: the asset id, its kind `isf:<id>` and the check report | review | yes |
| `FX CHECK` | `FX CHECK <asset>\|<kind>\|<json>` | JSON report (below) | low | no |

`FX IMPORT` reads a `.fs` file with the `.vs` beside it and its `IMPORTED` pictures relative to it (inside its folder, PNG or
JPEG files only; an imported picture is at most 4096 × 4096 when it is decoded), or the JSON form
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

`ok` is true when the file reads, translates without issues and did not fail to compile here; a dialect not compiled here is
`null` with words: "this node's context compiles the other dialect", "not compiled: the file's issues come first", or, with no
renderer, "no GL context on this node". The compile waits at most 10 s for the lane.

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
with the compiler's words at the file's line ("line 3: `nowhere' undeclared"). `idle` names why it is not drawn — kept whole while
the air keeps it off: "not drawn: the air is black", "not drawn: a test pattern or safe look covers its output", "not drawn: its
output shows a scan frame or has no size"; let go once unused while its owner is out of the picture: "not drawn: its output is
off", "not drawn: the layer is not visible", "not drawn: no output shows its canvas", "not drawn: the surface is not visible",
"not drawn: no projector shows it", "not drawn: the quad has no source and shows its test card", "not drawn: its content is a
surface-aware effect the stage draws itself", "not drawn: its playlist is not showing it"; and "not drawn: this build draws a
canvas's layers' chains, not the canvas's own", "no renderer on this node". A held picture (freeze) keeps the slot's last state.
A layer's, surface's or item's chain takes the air's words when an output that would show it is off the show (an item: the one
its playlist was showing when the air took it), else it is out of the picture. A slot the air keeps off keeps its program and
buffers, so it draws on the first frame back (`docs/EFFECTS.md`, *Where a chain runs*). A history effect is `"deterministic": false` with the words "integrates frame to frame: not frame-exact across nodes".
The fixture is `tests/fixtures/state/fx.json`; the desk's cards are `fx.cards.json`.

## Context

Item `fx` (kind `fx`): red when an output's chain costs more than twice the budget; amber when a slot is faulted or missing or a
chain is over budget; grey when nothing is drawn; words such as "4 effects · Broken faulted: line 3: … · 1 not frame-exact
across nodes (breaks mirror backup)".

## INDEX

Section `fx`: one row per slot (`parent` = its chain's owner, its light and state); preset keys **Bypass** (`FX BYPASS $(id)
TOGGLE`, instant) and **Mix ±** (`FX SET $(id) mix BY ±0.05`, hold) for an encoder.

## Preflight

Section `effects`: a row per slot on an enabled output's chain, and per slot on a layer's, surface's or item's chain that is on the
air now or of a kind this node does not have — green drawing, amber still compiling, red faulted or missing with the fix
`FX BYPASS <slot> ON` — or one green row when no effect runs on the air.

## Papers note

For the integration docs pass:

- `WIRE.md`: the ten FX rows above and STATE `fx` / `outputs[].fx`; `FX PLUGINS` and `FX TRUST` left the catalogue (FFGL is next
  round). `FX BIND`/`FX UNBIND` remain aliases of audio's `MOD BIND`/`MOD UNBIND`.
- `ARCHITECTURE.md`: the effects pass (an `IFramePass` at order 100: item, layer and surface chains where those pictures are made,
  output chains before the output stage), the compile lane on a shared context, the ISF translator in Core (`Core/Effects`), the
  built-in library embedded in Render (`Render/Effects/Library`).
- `ROADMAP.md`: ISF effects on output, layer, surface and item chains done (324/327 of the public ISF files at GLSL 3.30); a
  canvas's own chain, FFGL, bindings, geometry-aware effects and the rest of the library are next round.
- Spine amendment landed: `IFramePass.Item`/`Layer`/`Surface` (default: the picture handed back), called by `SourceResolver` and
  `StageRenderer` with the frame's ordered passes and `PassFrame`, which the engine sets once a frame; `PassFrame.Covered`/`Held`
  per output; `ISourceProvider.Item` and `SourceMix.FromItem`/`ToItem` name the item on the air and its start; a test pattern, a
  safe look and a raw frame are resolved with no pass (`SourceResolver.ResolvePlain`, canvases and mixes apart from the show's).
