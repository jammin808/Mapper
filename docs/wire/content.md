# wp2-content on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `GENERATOR GET` | `GENERATOR GET <generator>` | A generator with its effective values, bindings, inputs, morph, cost and state | low | no |
| `GENERATOR RESET` | `GENERATOR RESET <generator> [<param>\|ALL]` | Parameters back to their defaults | review | yes |
| `GENERATOR INPUT` | `GENERATOR INPUT <generator> source\|flow\|matte <source>\|NONE` | A picture a generator reads (particles emit from it) | review | yes |
| `GENERATOR PRESETS` | `GENERATOR PRESETS [<kind>\|<family>]` | Curated and saved looks | low | no |
| `GENERATOR RECALL` | `GENERATOR RECALL <generator> <preset>` | Applies a look (one undo step) | review | yes |
| `GENERATOR SAVE` | `GENERATOR SAVE <generator> <name>` | Saves the generator's look in the show; answers its id | low | yes |
| `GENERATOR MORPH` | `GENERATOR MORPH <generator> <preset> [OVER <0.1-600 s>] [AT +<ms>\|<unix ms>] [CURVE LINEAR\|EASE]` | Crossfades a generator to a look on the show clock | review | yes |
| `GENERATOR RETIME` | `GENERATOR RETIME <generator>\|ALL [AT +<ms>\|<unix ms>]` | Restarts a generator's own time (particles re-seed from that instant) | review | yes |
| `STING` | `STING <preset> [<100-5000 ms>] [AT +<ms>\|<unix ms>]` | Fires a sting every generator answers, on the show clock | review | yes |
| `STING CLEAR` | `STING CLEAR` | Stops the sting | low | yes |
| `STING LIST` | `STING LIST` | The sting presets | low | no |

## STATE

top level `content`.

## Papers note

(What shipped, for the integration docs pass.)
