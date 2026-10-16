# wp2-audio on the wire

Listening, the beat clock and the modulation bus (`docs/AUDIO.md`). An `<entity>` is a generator (id or name), a layer (id or
name), a surface (id or name) or an effect slot (id). Every mutating verb refuses a change that changes nothing, with the reason.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `AUDIO INPUTS` | `AUDIO INPUTS` | JSON `{inputs:[{key, name, kind, channels, rates, backend, default, listening}], backends:[{backend, words}]}` — an empty device list comes with its backend's words | low | no |
| `AUDIO LISTEN` | `AUDIO LISTEN <input>\|DEFAULT\|SYSTEM\|OFF [CHANNELS 1\|1+2\|3-4\|M\|S] [GAIN <dB>] [HERE]`, `AUDIO LISTEN TEST CLICK\|SWEEP\|NOISE\|DRUMS [CHANNELS …] [GAIN <dB>] [HERE]` | JSON `{input, key, name, channels, rateHz, state, words, backend}` as observed after the open; the words say what the show (or with `HERE`, this machine) listens to and what the node hears | review | yes |
| `AUDIO GAIN` | `AUDIO GAIN <-24…24>\|AUTO\|FIXED` | effect words | low | yes |
| `AUDIO STATUS` | `AUDIO STATUS` | JSON: `input` (as above), `heard`, and from the newest frame `frameAgeMs`, `level`, `bass`, `mid`, `high`, their `…N` twins, `peakDb`, `flux`, `centroid`, `silent`, `onsetAgoMs`, `bands[16]`, `spectrum[64]`; `gainDb`, `autoGain`, `gateDb` | low | no |
| `TEMPO STATUS` | `TEMPO STATUS` | JSON: `bpm` now, `settlesAt`, `beat`, `beatInBar`, `bar`, `barInPhrase`, `phrase`, `phase`, `barPhase`, `phrasePhase`, `beatsPerBar`, `barsPerPhrase`, `source`, `lock`, `leaderNs`, `anchors:[{leaderNs, beat, bpm, seq, source, pending}]` | low | no |
| `TEMPO SET` | `TEMPO SET <20-999>\|+<n>\|-<n> [AT +<ms>\|<unix ms>\|BEAT\|BAR\|PHRASE]` | JSON anchor `{leaderNs, beat, bpm, seq, source}`; `+n`/`-n` moves the tempo (an encoder) | low | yes |
| `TEMPO TAP` | `TEMPO TAP [AT <unix ms>]` | JSON `{taps}`, then `{taps, bpm, atLeaderNs}` from the second tap | low | yes |
| `TEMPO RESYNC` | `TEMPO RESYNC [BEAT\|BAR\|PHRASE] [AT <unix ms>]` | JSON anchor; the bar by default | low | yes |
| `TEMPO NUDGE` | `TEMPO NUDGE <±1-200 ms>\|FASTER\|SLOWER\|STOP` | JSON anchor; positive milliseconds land the beat earlier | low | yes |
| `TEMPO HALF`, `TEMPO DOUBLE` | | JSON anchor | low | yes |
| `TEMPO SOURCE` | `TEMPO SOURCE MANUAL\|DETECT` | effect words; detected, `TEMPO STATUS` and STATE give `lock` (searching, locked, holding, lost) and `confidence` | review | yes |
| `TEMPO METER` | `TEMPO METER <beats per bar 1-16> [<bars per phrase 1-64>]` | effect words | low | yes |
| `MOD SOURCES` | `MOD SOURCES [<prefix>]` | JSON `[{source, value, raw?, determinism, words}]`; `value` is null for a source the node lacks | low | no |
| `MOD BIND` | `MOD BIND <entity> <param> <source> [AMOUNT <-1…1>] [OFFSET <x>] [MODE ADD\|SCALE\|REPLACE] [CURVE LINEAR\|EASE\|STEP\|EXP\|LOG\|S] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]` (aliases `FX BIND`, `GENERATOR BIND`) | JSON binding `{param, source, mode, amount, offset, curve, attackMs, releaseMs, rangeMin, rangeMax, steps, invert, enabled, entity, kind}`; the words say whether the source is live (this node's sound) or pure | review | yes |
| `MOD UNBIND` | `MOD UNBIND <entity> <param>\|ALL` (aliases `FX UNBIND`, `GENERATOR UNBIND`) | effect words | review | yes |
| `MOD BYPASS` | `MOD BYPASS ON\|OFF\|TOGGLE [<entity>\|ALL]` | effect words; this node only, the show is not written | review | yes |
| `MOD PEEK` | `MOD PEEK <entity>` | JSON `{entity, kind, name, held, leaderNs, bindings:[{…binding…, base, value, sourceValue, min, max, determinism, words}]}` | low | no |

`AMOUNT` defaults to 0.5, `MODE` to ADD, `CURVE` to LINEAR; without `SMOOTH` (or a `~` suffix on the source) a binding follows its
source unsmoothed. `AUDIO LISTEN` and `AUDIO INPUTS` answer off the control lane (an open waits on a device).

**Refusals** (each leaves the show unchanged): an unknown entity, parameter or source (with the list or the form); a parameter
that is not a continuous number ("iterations is integer … it does not take modulation"); an option out of range; `MODE RATE`
(not on this node); smoothing given twice; the same binding again ("already bound … with these settings"); unbinding what is not
bound; a bypass that is already so; a tempo outside 20–999 or the one in force; `AT` sooner than one lead; a resync on the line
already; `TEMPO NUDGE STOP` with no nudge running; a meter or source that is already so; a gain outside ±24 dB or the one in force;
`AUDIO LISTEN` of what is already heard; an edit (`SET … bindings`) that writes a binding with an unknown source or parameter.

## STATE

Top level, after `fx`:

```json
"audio": {"level":0.42,"bpm":128.0,"beat":2,"bar":5,"phrase":1,"phase":0.61,"bass":0.61,"mid":0.30,"high":0.18,"peakDb":-6.1,"silent":false,
          "input":{"key":"test:drums","name":"Test drums","channels":"1+2","rateHz":48000,"state":"off|opening|listening|retrying",
                   "words":"","backend":"test","asked":"test:drums","here":false,"silentForS":0},
          "tempo":{"source":"manual|detect","lock":"manual|searching|locked|holding|lost","confidence":1,"bpm":128.0,"beatsPerBar":3,"barsPerPhrase":4,
                   "inUse":true,"epochUnixMs":1759999995000,"anchor":{"leaderNs":5040000000,"beat":10.08,"bpm":128.0,"seq":1,"source":"set"}},
          "latencyMs":{"input":-1,"analysis":10.7,"total":-1,"acoustic":0}},
"modulation": {"bindings":3,"bypassed":false,"live":1,"shared":0,"pure":2,"unbound":["audio:bass (no audio on this node)"],"held":["gn…"],"flashLimited":-1}
```

- `beat` is the beat in its bar (1-based), `bar` and `phrase` the counts since leader 0, `phase` the phase in the beat — written
  when the beat count moves, while the clock is in use (from the first TEMPO verb or a binding that reads it); `null` before.
  A reader computes the beat at any instant from `tempo.anchor`: `beat + (t − leaderNs)·bpm/60e9`.
- Levels are refreshed a few times a second while listening (when they moved by more than 0.05); `AUDIO STATUS` reads the newest
  frame.
- `latencyMs.input` is −1 (unknown) unless the node's settings give it; `total` likewise. `acoustic` is the listener distance ÷
  343 m/s.
- `tempo.confidence` is 1 under `MANUAL`, the last estimate's (0–1) when detecting, −1 before the first.
- `modulation.shared` is 0 (frames are not shared between nodes); `flashLimited` is −1 (no limiter on this node).
- `modulation.unbound` names each binding that holds its base value on the air: a source the node lacks ("audio:bass (no audio
  on this node)"), or a generator number its kind draws at its base (the CPU particle field, the sync marks, the still patterns,
  the surface effects, and a number read once per change such as a field's `lifeMin`).
- Fixture: `tests/fixtures/state/audio.json`, cards `tests/fixtures/state/audio.cards.json`.

## Context

`audio` (kind `audio`): red when bindings read the sound and nothing is heard (off or retrying); amber silent for more than 10 s,
opening, or retrying with no binding listening; green listening; grey off. `tempo` (kind `tempo`): amber while detection searches
or holds, red when it is lost, green once the clock is in use (manual or locked), else grey; words "128 BPM · 4 beats a bar ·
manual".

## INDEX

Section `tempo` (row kind `tempo`): one row `{id:"tempo", label:"128 BPM", light:<the context's tempo light>, state:<source>}`;
keys **Tap** (`TEMPO TAP`), **Resync** (`TEMPO RESYNC BAR`), **Half**, **Double**, **BPM +** / **BPM −** (`TEMPO SET +1` / `-1`,
an encoder's turn). Every key presses as the catalogue presses its verb: instant.

## Preflight

Section `audio`: one row — green "not needed" when no binding reads the sound; green "listening"; amber "silent" (more than 10 s)
or "opening" (an input that stops delivering without a fault counts as silent from a second after its last sound); red "missing"
(the device's words) or "off", both with the fix `AUDIO LISTEN DEFAULT`.

## Where it lives

- The analyser and the beat clock are Core's (`Core/Audio`), with the bus and the binding plan (`Core/Modulation`); the runtime
  and the verbs are the engine's (`Engine/Audio`); the capture backends are Media's (`Media/Audio`, WASAPI under
  `Media/Audio/Windows`).
- Bound numbers of layers, surfaces and effect slots reach the renderer through the project the render host's compose hook hands
  it. A generator keeps its numbers and its bindings, and its renderer evaluates them on the render thread through the frame's
  bus for the kinds that read their numbers every frame (`particles.flow`, `particles.image`, the fractals, `reactive.scene`, the
  moving patterns); the rest are drawn at their base and named in `modulation.unbound`.
