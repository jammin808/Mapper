# wp2-led on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `LED LIST` | `LED LIST` | The LED rig: nodes, fixtures, pixels, universes, conflicts and bandwidth | low | no |
| `LED NEW` | `LED NEW LINE\|GRID\|RING <count>[x<rows>] [ON canvas:<id>\|output:<id>\|surface:<id>\|source:<ref>\|world] [TYPE <type>] [<name>]` | Adds a fixture laid out on a space; answers its id | low | yes |
| `LED IMPORT` | `LED IMPORT <path> [AS CSV] [ON <space>] [SCALE <x>] [NODE <node>]` | Imports pixel positions and patches from a CSV (one undo step) | review | yes |
| `LED PATCH` | `LED PATCH <fixture> <node> <universe>[.<address>] [FORCE]` | Patches a fixture to a node's universe and address (FORCE allows an overlap) | review | yes |
| `LED AUTOPATCH` | `LED AUTOPATCH [<fixture>\|#<tag>\|ALL] [NODE <node>…] [FROM <universe>[.<address>]] [ALIGN ON\|OFF] [EACH ON\|OFF]` | Patches fixtures in order within each node's capacity | review | yes |
| `LED DISCOVER` | `LED DISCOVER [ARTNET\|ALL] [<seconds 1-10>]` | Polls the network for Art-Net nodes | low | yes |
| `LED OUTPUT` | `LED OUTPUT ON\|OFF [<node>\|ALL] [HOLD]` | This machine sends pixel data, or stops (one black frame first unless HOLD) | high | yes |
| `LED TEST` | `LED TEST CHASE\|ORDER\|UNIVERSE\|FIXTURE\|GRADIENT\|WALK\|WHITE\|FULL\|BLACK\|OFF [ON <node>\|<fixture>\|#<tag>\|ALL]` | A test pattern on the LEDs instead of the show | high | yes |
| `LED LOCATE` | `LED LOCATE <fixture> [<seconds 1-60>]` | Blinks one fixture | review | yes |
| `LED LEVEL` | `LED LEVEL <0-100> [FADE <ms>]` | The LED rig's master level (live, not an undo step) | high | yes |
| `LED MONITOR` | `LED MONITOR <node> <universe>` | The bytes last sent to a universe, with the fixtures in it | low | no |

## STATE

top level `led`.

## Papers note

(What shipped, for the integration docs pass.)
