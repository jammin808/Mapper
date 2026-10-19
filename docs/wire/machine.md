# wp3-platform on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `MACHINE` | `MACHINE [STATUS]` | This machine's identity, edition and support tier, the claims PlayMap made of Windows with what Windows answered, and any drift from the locked driver set, in words | low | no | wp3-platform |
| `MACHINE SET` | `MACHINE SET QOS HIGH\|AUTO \| TIMER HIGH\|DEFAULT \| MMCSS ON\|OFF \| PROCESS HIGH\|NORMAL \| CPUSETS AUTO\|OFF \| GPU-PRIORITY NORMAL\|HIGH\|REALTIME \| GC SHOW\|DEFAULT \| AWAKE ON\|OFF` | Changes one of this machine's claims (power throttling, the timer, MMCSS, the process priority, CPU sets, the GPU scheduling class, the GC's show mode, keep-awake), kept on the node and applied now where Windows allows, else at the next start, saying which; REALTIME needs the privilege the installer grants | review | yes | wp3-platform |
| `MACHINE LOCK` | `MACHINE LOCK [CLEAR]` | Keeps this machine's driver set (adapters, the Direct3D runtime, FFmpeg, the sync board's firmware, the OS build, PlayMap's build) as the one its shows run on, so preflight names any drift; CLEAR forgets it (kept on the node, never in the project) | review | yes | wp3-platform |

## Forms added to existing rows

None.

## STATE

`machine.identity`, `machine.capabilities`, `machine.platform`, `machine.locked`; the context's machine item.

## Papers note

(What shipped, for the integration docs pass.)
