# wp2-output on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `OUTPUT STATS` | `OUTPUT STATS [<output>\|ALL]` | Each output's presentation from the display's side: path, measured refresh, missed vblanks, held frames, latency, lock | low | no |
| `PRESENT RESET` | `PRESENT RESET [<output>\|ALL]` | Zeroes presentation counters (the old totals go to the journal) | low | yes |
| `SYNC APPLY` | `SYNC APPLY [<group>]` | The leader joins the group's outputs to the hardware framelock and answers each output's observed lock | high | yes |
| `SYNC LEAVE` | `SYNC LEAVE [<output>\|ALL]` | Outputs leave the framelock and present free-running, measured | high | yes |
| `SYNC REJOIN` | `SYNC REJOIN [<output>\|ALL]` | Outputs that fell out of the lock join it again (armed: only while blacked out) | high | yes |

## STATE

`outputs[].latencyMs`, `present`, `lock`; `sync.latencyMs`, `sync.lock`; `machine.present`.

## Papers note

(What shipped, for the integration docs pass.)
