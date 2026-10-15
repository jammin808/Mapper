# wp2-content on the wire

Particles, fractals and reactive scenes (`docs/CONTENT.md`). A `<generator>` is a generator's id (`gn…`) or its name. `AT` takes
`+<ms>` from now or a Unix ms on this node's show clock (the transport's rule) and schedules on the leader clock.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `GENERATOR RETIME` | `GENERATOR RETIME <generator>\|ALL [AT +<ms>\|<unix ms>]` | JSON `{originNs, atUnixMs, generators:[id…]}`; the generator's own time counts from that instant (a particle field settles anew from there) | review | yes |
| `GENERATOR INPUT` | `GENERATOR INPUT <generator> source <source>\|NONE` | effect words: what it reads now | review | yes |
| `GENERATOR RESET` | `GENERATOR RESET <generator> [<param>\|ALL]` | effect words | review | yes |
| `GENERATOR GET` | `GENERATOR GET <generator>` | JSON `{id, name, kind, params, effective, bindings, inputs, originNs, morph:null, traits:{time, periodSeconds, maxLifeSeconds, readsAudio, mayFlash, drawsBindings}, state, progress, time, timeS, gpuMs, live, words}` | low | no |
| `STING` | `STING <preset> [<100-5000 ms>] [AT +<ms>\|<unix ms>]` | JSON `{preset, atUnixMs, lengthMs, progress}`; applied when the channel every generator reads holds it | review | no (the air; journalled) |
| `STING CLEAR` | `STING CLEAR` | effect words | low | no (the air) |
| `STING LIST` | `STING LIST` | JSON `[{name, kind:"pulse\|score", channels:[…], defaultMs}]` | low | no |

Round-one rows content answers with more (their syntax is the spine's):

- `GENERATOR NEW <kind> [<name>] [PRESET <preset>]` stamps the generator's `originNs` at the leader's now — the same instant on
  every node that applies the edit — and takes a curated look by name for a kind that has presets (`particles.field`: the
  particle presets; the 2D fractals: the fractal scenes).
- `GENERATOR SET <generator> <param> BY <delta>`: a number moves by `delta` hundredths of its range, a whole number likewise
  (at least one), a choice by `delta` places (wrapping); a flag, a colour or text does not step.
- `GENERATOR KINDS` adds to each kind `traits` `{time: pure|bounded|history|live, periodSeconds, maxLifeSeconds, readsAudio, mayFlash,
  drawsBindings}` and `inputs` (the pictures it reads). Its `[<family>]` filter is not read: the router hands the verb no words
  (spine amendment named in the papers note).

**Refusals** (every one leaves the show unchanged, with words): an unknown generator, kind, preset, parameter or input; a value
that would not change ("curlOctaves is already 3", "… already counts its time from that instant", "… is already at its
defaults", "… already reads canvas Main"); an input a kind does not read ("Julia reads no picture", "Sparks reads source, not
matte"); an input that would show the generator itself ("Sparks would read canvas Main, which shows Sparks: a loop"); a sting not
in the list (the answer lists them), a length outside 100–5000 ms, a malformed `AT`; `STING CLEAR` with none running.

**Generic edits.** `ADD generator {json}` refuses an input its kind does not read. `SET gn… originNs …`, `inputs` and `bindings` are
plain fields of the generator.

## STATE

```json
"content": {"budgetMs": 6, "governor": "advise", "light": "green|amber|red|grey", "costMs": 1.2, "renderer": "drawing|none",
            "sting": null | {"preset": "Explosion", "atUnixMs": 1760000004100, "lengthMs": 900, "progress": 0.5},
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
`warnings` holds "time precision: re-time at a quiet moment" past 2¹⁶ s of a non-periodic kind's own time. `horizonS` is a
bounded kind's longest life: two nodes, or one after a seek, draw the same field once it has run. `light` is red when faulted or
at twice the budget (p95), amber over the budget or settling, grey idle. `sting` is null once the sting is over; its progress
reaches STATE in tenths.

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
  GPU and driver. 256 k by default, up to 4 M; 64 k on llvmpipe.
- **Raymarched fractals** `fractal.mandelbulb`, `fractal.mandelbox`, `fractal.kifs` on one marcher, with single-precision CPU
  twins (`Fractal3D`), an orbiting camera on the generator's time, render scale 0.25–1 filtered up.
- **Reactive**: `reactive.scene` and every fractal gain `sound` (0–1, 0 for old projects) reading `audio:level`/`audio:bass`
  through the frame's bus, the brightness swing held to `FlashGuard.MaxAudioBrightnessSwing`; with no sound they draw neutral
  and say so.
- **Bindings drawn live**: a generator's bindings (audio's `MOD BIND`/`GENERATOR BIND`) are evaluated on the render thread through
  the frame's bus for `particles.flow`, the fractals, `reactive.scene` and the moving patterns; the CPU particle field, the still
  patterns and the surface effects draw them at their base (audio's words say so per kind).
- Not built this round (rows trimmed): `GENERATOR PRESETS/RECALL/SAVE/MORPH` (looks and morphs), `particles.image`, deep zoom.
- **Spine amendments wanted:** the router hands `GENERATOR KINDS` no words (its `[<family>]` filter is unread);
  `GeneratorFrame`/`FrameInputs` have no content field, so content's per-frame facts (item times, the pictures a generator reads,
  the observed stats) ride the frame's modulation as a `ContentFrame` that forwards the node's bus whole — a `FrameInputs.Content`
  would carry them plainly; `EngineCore.Edits`' refusal of `ADD generatorpreset` names `GENERATOR SAVE`, trimmed this round.
