# wp2-backup on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `FLEET BACKUP` | `FLEET BACKUP <primary node> [HOT] [TIMEOUT <ms 0-60000>]` | This node becomes a hot backup of a primary: it mirrors the primary's project, air and transports on its own outputs; FAILOVER makes it the primary | high | yes |
| `FLEET SOLO` | `FLEET SOLO` | This node stops backing up and plays on alone | high | yes |
| `FLEET SUCCESSION` | `FLEET SUCCESSION <node> [<node>…] \| OFF` | Who leads the sync group, in order, when its leader goes quiet | review | yes |
| `FLEET PROMOTE` | `FLEET PROMOTE [<group>]` | This node leads the group now, continuing the old leader's show clock without a step | high | yes |
| `FAILOVER` | `FAILOVER [<primary node>] [HOLD]` | The backup takes the show from its primary and fences the old primary by epoch; HOLD cancels a pending takeover | high | yes |
| `FAILBACK` | `FAILBACK CONFIRM` | Hands the show back to the original primary (a person's press only) | high | yes |
| `FLEET FENCE` | `FLEET FENCE <epoch> <node>` | A peer that took the show tells this node: hold the outputs | high | yes |
| `MIRROR` | `MIRROR PROJECT\|DELTA\|AIR\|ANCHOR\|FOCUS\|BEAT\|RESYNC <json>` | A primary's show, line by line, to its backup | high | yes |
| `FLEET ROUTE` | `FLEET ROUTE <output> DEVICE <device> <input> <backup input> \| FLEET ROUTE <output> NONE \| FLEET ROUTE TEST` | What FAILOVER switches so the audience sees the backup: the projector's input, confirmed by its own answer; TEST reads reachability and the current input | review | yes |

## STATE

`sync.epoch` (with wp2-fleet's file).

## Papers note

(What shipped, for the integration docs pass.)
