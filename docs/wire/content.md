# wp2-content on the wire

Particles, fractals and reactive scenes (`docs/CONTENT.md`). A `<generator>` is a generator's id (`gn…`) or its name. `AT` takes
`+<ms>` from now or a Unix ms on this node's show clock (the transport's rule) and schedules on the leader clock.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `GENERATOR RETIME` | `GENERATOR RETIME <generator>\|ALL [AT +<ms>\|<unix ms>]` | JSON `{originNs, atUnixMs, generators:[id…]}`; the generator's own time counts from that instant (a particle field settles anew from there); the words say when a playlist routes it ("while Show plays it, its item's time drives it"); the sync marks are refused (they keep the leader's grid) and ALL leaves them be | review | yes |
| `GENERATOR INPUT` | `GENERATOR INPUT <generator> source <source>\|NONE` | effect words: what it reads now | review | yes |
| `GENERATOR RESET` | `GENERATOR RESET <generator> [<param>\|ALL]` | effect words | review | yes |
| `GENERATOR PRESETS` | `GENERATOR PRESETS [<kind>\|<family>]` | JSON `[{id: "gp…"\|null, name, kind, pack, curated}]`: the curated looks (thirty for the round-2 kinds; the particle presets and fractal scenes for the older kinds) and the looks the show keeps | low | no |
| `GENERATOR RECALL` | `GENERATOR RECALL <generator> <preset>` | effect words "Sparks → Galaxy swirl"; the look's parameters written whole, one undo step (a kind with a `preset` parameter takes the look's name there); a saved look by id or name first, then a curated one | review | yes |
| `GENERATOR SAVE` | `GENERATOR SAVE <generator> <name>` | the look's id (`gp…`): its parameters kept whole in the show (as the operator set them, for a kind with a `preset` parameter) | low | yes |
| `GENERATOR GET` | `GENERATOR GET <generator>` | JSON `{id, name, kind, params, effective, bindings, inputs, originNs, morph:null, traits:{time, periodSeconds, maxLifeSeconds, readsAudio, mayFlash, drawsBindings}, state, progress, time, timeS, gpuMs, live, words}` | low | no |
| `STING` | `STING <preset> [<100-5000 ms>] [AT +<ms>\|<unix ms>]` | JSON `{preset, atUnixMs, lengthMs, progress, drawn}`; applied when the channel every generator reads holds it; on a node with no renderer accepted, not applied ("accepted: no renderer on this node draws it") | review | no (the air; journalled) |
| `STING CLEAR` | `STING CLEAR` | effect words | low | no (the air) |
| `STING LIST` | `STING LIST` | JSON `[{name, kind:"pulse\|score", channels:[…], defaultMs}]` | low | no |

Round-one rows content answers with more (their syntax is the spine's):

- `GENERATOR NEW <kind> [<name>] [PRESET <preset>]` stamps the generator's `originNs` at the leader's now — the same instant on
  every node that applies the edit — and takes a look by name (saved or curated, as `GENERATOR RECALL` takes it), written whole.
- `GENERATOR SET <generator> <param> BY <delta>`: a number moves by `delta` hundredths of its range, a whole number likewise
  (at least one), a choice by `delta` places (wrapping); a flag, a colour or text does not step.
- `GENERATOR KINDS` adds to each kind `traits` `{time: pure|bounded|history|live, periodSeconds, maxLifeSeconds, readsAudio, mayFlash,
  drawsBindings}` and `inputs` (the pictures it reads). Its `[<family>]` filter is not read: the router hands the verb no words
  (spine amendment named in the papers note).

**Refusals** (every one leaves the show unchanged, with words): an unknown generator, kind, preset, parameter or input; a value
that would not change ("curlOctaves is already 3", "… already counts its time from that instant", "… is already at its
defaults", "… already reads canvas Main"); an input a kind does not read ("Julia reads no picture", "Sparks reads source, not
matte"); an input that would show the generator itself ("Sparks would read canvas Main, which shows Sparks: a loop"); a sting not
in the list (the answer lists them), a length outside 100–5000 ms, a malformed `AT`; `STING CLEAR` with none running; a look of
another kind ("Golden bulb is a look for Mandelbulb, not Particle flow"), an unknown look (the answer lists the kind's), a recall
that would change nothing ("Sparks already looks like Galaxy swirl"), a second kept look of one name for a kind.

**Generic edits.** `ADD generator {json}` refuses an input its kind does not read. `SET gn… originNs …`, `inputs` and `bindings` are
plain fields of the generator.

## STATE

```json
"content": {"budgetMs": 6, "governor": "advise", "light": "green|amber|red|grey", "costMs": 1.2, "renderer": "drawing|none",
            "sting": null | {"preset": "Explosion", "atUnixMs": 1760000004100, "lengthMs": 900, "progress": 0.5, "drawn": true},
            "generators": [{"id": "gn…", "kind": "particles.flow", "name": "Sparks", "state": "ok|warming|faulted|idle",
                            "progress": 0.4, "time": "pure|bounded|history|live", "horizonS": 4, "originNs": 1000000000, "timeS": 3,
                            "gpuMs": 0.9, "gpuMsP95": 1.1, "count": 65536, "morph": null, "live": {"curl": 0.71},
                            "inputs": {"source": "canvas:cv…"}, "onAir": true, "light": "amber", "words": "", "warnings": []}]}
```

Everything under `generators[]` but the record's own fields is what the renderer observed (never assumed): `state` `warming`
while a particle field settles after a seek or a change (with `progress`), `faulted` with the renderer's words, `idle` when no
frame drew it in the last 30 frames ("not drawn lately: nothing shows it") or the node has no renderer ("no renderer on this
node"); `timeS` its own time at its last draw (0 when not drawn); `gpuMs`/`gpuMsP95` its draw's GPU time (timestamp queries,
three frames deep; 0 on a context with none) and the 95th percentile of the last 120 measurements; `count` the particles it
steps (64 k on a software renderer, with words); `live` each bound number's value at its last draw. `time` is `live` when the
generator listens to this node's sound (`sound` above 0, or a binding to an `audio:` source): a mirror does not draw the same.
`time` is `history` for a GPU field that reads a picture (its births and drift follow the frames the picture had). A number its
kind reads once per change (a field's `lifeMin`/`lifeMax`, a fractal's `renderScale`) is drawn at its base when bound, and audio's
words say so. `warnings` holds "time precision: re-time at a quiet moment" past 2¹⁶ s of a non-periodic kind's own time. `horizonS` is a
bounded kind's longest life: two nodes, or one after a seek, draw the same field once it has run. `light` is red when faulted or
at twice the budget (p95), amber over the budget or settling, grey idle. `sting` is null once the sting is over; its progress
reaches STATE in tenths; `drawn` is true once the renderer drew a generator with it in the last 30 frames (observed).

A surface-aware effect a projector pass draws on a stage surface is drawn there: `ok` and on the air, `timeS` its own time (a
`RETIME` moves it on the surface as anywhere else). Its light is part of the projector pass, so its `gpuMs` is 0 unless it is
also drawn flat.

## Context

Item `content` (kind `content`) while a generator is on the air, faulted or settling, or a sting surges: its light is the STATE
light, its state e.g. "3 generators · 1.2 ms of 6 · Sparks settling 40 % · Waves faulted: … · sting Flash surging"; a generator
that listens to this node's sound adds "(breaks mirror backup)". A show whose generators nothing draws adds no item.

## INDEX

- Section `content`: one row per generator (`kind` `generator`, label "Sparks · Particle flow", light and state as STATE); key
  "Retime" → `GENERATOR RETIME $(id)`.
- Section `sting`: one row per sting preset (`id` is its name; state `surging` while it runs, else `ready`); key "Fire" →
  `STING $(id)`.

## Preflight

Section `content`: a row per generator on the air — green within the node's content budget (`NodeSettings.Content.BudgetMs`,
6 ms), amber over it and red over twice it (its p95), with the line that brings it down (`GENERATOR SET <id> count <half>` for a
particle field, `GENERATOR SET <id> renderScale <⅔>` for a raymarched fractal); amber while settling; red when faulted with
`GENERATOR RESET <id>`. One green row when nothing is on the air; grey with no renderer.

## Papers note

What shipped (for the integration docs pass):

- **Content time.** `Generator.OriginNs` is stamped by `GENERATOR NEW` and moved by `GENERATOR RETIME`; every generator draws on its
  own time (leader − origin, in double), or — while a playlist routes it — its item's clip time (`PlaylistCursor.ItemTimeNs`: held
  while cued or paused, wrapped by a loop, moved by a seek; the incoming side of a fade or a dip too). The sync marks keep the
  leader's grid. Files from before round 2 (origin 0) draw as they did.
- **Stings on the wire.** `STING`, `STING CLEAR`, `STING LIST`; the render host composes the engine's channel into every frame's
  impulse (the options' channel is used when the engine has none running).
- **`particles.flow`**: the GPU particle field (state in RGBA32F, a fragment step on a 1/120 s grid, births on an integer
  schedule with a C# twin, curl-noise flow, vortex, attractor, edges, picture emitter from any source's light, sprites drawn
  from the state). Bounded memory: re-anchored one longest life back on a seek, a jump, a change; bit-exact after one life on one
  GPU and driver. 256 k by default, up to 1 M; 64 k on llvmpipe. Bound numbers are read at each step's own leader instant, so a
  pure source keeps the field a pure function of time at any frame rate; a GPU slower than the show draws behind its time after
  30 held frames, and says so.
- **Raymarched fractals** `fractal.mandelbulb`, `fractal.mandelbox`, `fractal.kifs` on one marcher, with single-precision CPU
  twins (`Fractal3D`), an orbiting camera on the generator's time, render scale 0.25–1 filtered up.
- **Reactive**: `reactive.scene` and every fractal gain `sound` (0–1, 0 for old projects) reading `audio:level`/`audio:bass`
  through the frame's bus, the brightness swing held to `FlashGuard.MaxAudioBrightnessSwing`; with no sound they draw neutral
  and say so.
- **Bindings drawn live**: a generator's bindings (audio's `MOD BIND`/`GENERATOR BIND`) are evaluated on the render thread through
  the frame's bus for `particles.flow`, the fractals, `reactive.scene` and the moving patterns; the CPU particle field, the still
  patterns and the surface effects draw them at their base (audio's words say so per kind).
- **Stretch reached:** `particles.image` (the field born where a source is bright — by luma, alpha or edges, inverted on request —
  drifting along its contours or across them, coloured by the picture where it was born) and the looks: `GENERATOR PRESETS`,
  `RECALL`, `SAVE` with thirty curated looks (`GeneratorLooks`).
- Not built this round (row trimmed): `GENERATOR MORPH`; deep zoom (stretch 7).
- **Spine amendments wanted:** the router hands `GENERATOR KINDS` no words (its `[<family>]` filter is unread);
  `GeneratorFrame`/`FrameInputs` have no content field, so content's per-frame facts (item times, the pictures a generator reads,
  the observed stats) ride the frame's modulation as a `ContentFrame` that forwards the node's bus whole — a `FrameInputs.Content`
  would carry them plainly; `EngineCore.Edits`' refusal of `ADD generatorpreset` names `GENERATOR SAVE`, trimmed this round.
