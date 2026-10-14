# wp2-audio on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `AUDIO INPUTS` | `AUDIO INPUTS` | The sound inputs this node can listen to (devices, the system's sound, test signals) | low | no |
| `AUDIO LISTEN` | `AUDIO LISTEN <input>\|DEFAULT\|SYSTEM\|OFF [CHANNELS 1\|1+2\|3-4\|M\|S] [GAIN <dB>] [HERE] \| AUDIO LISTEN TEST CLICK\|SWEEP\|NOISE\|DRUMS [CHANNELS 1\|1+2\|3-4\|M\|S] [GAIN <dB>] [HERE]` | Chooses what the show listens to (HERE: this machine only) | review | yes |
| `AUDIO GAIN` | `AUDIO GAIN <-24…24>\|AUTO\|FIXED` | The listening gain | low | yes |
| `AUDIO STATUS` | `AUDIO STATUS` | Levels, bands, the spectrum, onsets and the input's state | low | no |
| `TEMPO STATUS` | `TEMPO STATUS` | The beat clock: BPM, beat, bar, phrase, source, lock and confidence | low | no |
| `TEMPO SET` | `TEMPO SET <20-999> [AT +<ms>\|<unix ms>\|BEAT\|BAR\|PHRASE]` | Sets the tempo from the next beat | low | yes |
| `TEMPO TAP` | `TEMPO TAP [AT <unix ms>]` | A tap; two or more set the tempo | low | yes |
| `TEMPO RESYNC` | `TEMPO RESYNC [BEAT\|BAR\|PHRASE] [AT <unix ms>]` | Puts the beat, bar or phrase on now | low | yes |
| `TEMPO NUDGE` | `TEMPO NUDGE <±1-200 ms>\|FASTER\|SLOWER\|STOP` | Moves the beat's phase, slewed over a bar | low | yes |
| `TEMPO HALF` | `TEMPO HALF` | Halves the tempo | low | yes |
| `TEMPO DOUBLE` | `TEMPO DOUBLE` | Doubles the tempo | low | yes |
| `TEMPO SOURCE` | `TEMPO SOURCE MANUAL\|DETECT` | Where the beat comes from: taps and SET, or detected from the sound | review | yes |
| `TEMPO METER` | `TEMPO METER <beats per bar 1-16> [<bars per phrase 1-64>]` | The meter the bar and phrase sources count | low | yes |
| `MOD SOURCES` | `MOD SOURCES [<prefix>]` | Every modulation source on this node (audio:, beat, lfo:, sting) with its value and whether it is pure, shared or live | low | no |
| `MOD BIND` | `MOD BIND <entity> <param> <source> [AMOUNT <-1…1>] [OFFSET <x>] [MODE ADD\|SCALE\|REPLACE\|RATE] [CURVE LINEAR\|EASE\|STEP\|EXP\|LOG\|S] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]` | Modulates a number of a generator, layer, surface or effect slot from a source, live; the show keeps the base value | review | yes |
| `MOD UNBIND` | `MOD UNBIND <entity> <param>\|ALL` | Removes modulation | review | yes |
| `MOD BYPASS` | `MOD BYPASS ON\|OFF\|TOGGLE [<entity>\|ALL]` | Holds every modulated number at its base value | review | yes |
| `MOD PEEK` | `MOD PEEK <entity>` | The live value of each modulated number of an entity | low | no |

## STATE

top level `audio`, `modulation`.

## Papers note

(What shipped, for the integration docs pass.)
