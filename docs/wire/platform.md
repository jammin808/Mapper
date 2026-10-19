# wp3-platform on the wire

The package's verbs, STATE, context item, INDEX section and preflight rows are under its hook word, Machine:
**`docs/wire/machine.md`**. What the package does is `docs/PLATFORM.md`.

| Verb | Syntax | Risk |
|---|---|---|
| `MACHINE` | `MACHINE [STATUS]` | low |
| `MACHINE SET` | `MACHINE SET QOS HIGH\|AUTO \| TIMER HIGH\|DEFAULT \| MMCSS ON\|OFF \| PROCESS HIGH\|NORMAL \| CPUSETS AUTO\|OFF \| GPU-PRIORITY NORMAL\|HIGH\|REALTIME \| GC DEFAULT\|SHOW \| AWAKE ON\|OFF` | review (REALTIME: high) |
| `MACHINE LOCK` | `MACHINE LOCK [CLEAR]` | review |

STATE: `machine.identity`, `machine.capabilities`, `machine.platform`, `machine.locked`. Preflight: section `machine`. INDEX: section
`machine`. Context: `machine:platform` on a Windows node.
