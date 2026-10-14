# wp2-fleet on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `HEALTH` | `HEALTH` | This machine's health in one record: outputs, render, CPU, memory, disk, GPU, network, sync, devices, media (unknown is −1) | low | no |
| `FLEET` | `FLEET [STATUS]` | Every PlayMap machine this node hears as one board in Patterns' Eye shape: lights by evidence, headline, problems | low | no |
| `FLEET FOCUS` | `FLEET FOCUS <machine\|output\|id\|label>` | Focuses the board on a thing and its neighbours (a view, not journalled) | low | no |
| `FLEET LENS` | `FLEET LENS ALL\|OUTPUTS\|PROJECTORS\|SYNC\|BACKUP\|PROBLEMS` | Shows one kind of thing on the board | low | no |
| `FLEET NEXT` | `FLEET NEXT` | Focuses the next problem (reds, then ambers) | low | no |
| `FLEET PREV` | `FLEET PREV` | Focuses the previous problem | low | no |
| `FLEET RESET` | `FLEET RESET` | The board's view from before the focus | low | no |

## STATE

top level `fleet`, `eye`; `sync` extras (`epoch` by wp2-backup).

## Papers note

(What shipped, for the integration docs pass.)
