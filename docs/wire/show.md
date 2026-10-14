# wp2-show on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `SHOW` | `SHOW [STATUS]` | Show Mode: armed or rehearsal, by whom and since, the rules, the blind draft, panic and locks | low | no |
| `SHOW ARM` | `SHOW ARM [ALL] [<words>]` | Arms the show with no password: edits that would move the air are refused for every caller until SHOW DISARM; transport, blackout and PANIC stay live (ALL: every node of the group, with each node's receipt) | review | yes |
| `SHOW DISARM` | `SHOW DISARM [ALL]` | Back to rehearsal (ALL: every node of the group) | review | yes |
| `SHOW SET` | `SHOW SET MINGO <ms> [ARMED\|REHEARSAL] \| REMOTE-VIEW-ONLY ON\|OFF \| DISARM-FROM PAIRED\|DESK \| EXPECT-ARMED ON\|OFF \| COMMIT-LEAD <ms> \| PANIC-LEAD <ms>` | Changes a show rule (kept in the project, one undo step) | review | yes |
| `PANIC` | `PANIC [BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD] [ON <output>…\|ALL] [FADE <ms 0-10000>] [AT +<ms>\|<unix ms>] [HERE]` | Takes each output to its planned outcome over its fade, picture and sound; a second PANIC during the fade is the hard stop; on a group's leader every follower and backup fades on the same frames unless HERE; never refused for an operator (an assistant's PANIC waits for a person) | high | yes |
| `PANIC CLEAR` | `PANIC CLEAR [ON <output>…\|ALL] [FADE <ms>] [HERE]` | Brings the air back from a panic over a fade (transports stay as the outcome left them); a leader's clear reaches its group unless HERE | high | yes |
| `PANIC SET` | `PANIC SET <output>\|ALL BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD [FADE <ms>] [SAFE <source>]` | Plans what PANIC does on an output (kept in the project) | review | yes |
| `MASTER` | `MASTER <0-100> [FADE <ms>] [HERE]` | The grand master over every output, picture and sound, faded on the show clock (not an undo step); a leader's master reaches its group unless HERE | high | yes |
| `LOCK` | `LOCK <id> [<words>] \| LOCK AREA mapping\|layout\|content\|playlists\|media\|devices\|network [<words>]` | Locks an entity or an area: no verb, undo or assistant changes it; playback and reading go on | review | yes |
| `UNLOCK` | `UNLOCK <id> \| UNLOCK AREA <area> \| UNLOCK ALL` | Lifts a lock | review | yes |
| `LOCKS` | `LOCKS` | Every lock with who set it and why, and the areas an armed show adds | low | no |
| `BLIND` | `BLIND [STATUS]` | The blind draft: whose, how many changes, when it commits | low | no |
| `BLIND ON` | `BLIND ON` | Blind editing for this session: its edits go to a draft, off the air, until BLIND COMMIT | low | yes |
| `BLIND OFF` | `BLIND OFF` | Leaves blind editing (refused while the draft holds changes: COMMIT or DISCARD them) | low | yes |
| `BLIND DIFF` | `BLIND DIFF` | What the draft changes, entity by entity, in field words, with conflicts | low | no |
| `BLIND COMMIT` | `BLIND COMMIT [NOW\|NEXT [<playlist>]\|ON GO [<playlist>]\|AT +<ms>\|<unix ms>] [FORCE]` | Lands the draft on the air as one undo step at a show boundary, frame-exact; FORCE overrides the conflicts it names | high | yes |
| `BLIND DISCARD` | `BLIND DISCARD` | Drops the draft | review | yes |
| `PREFLIGHT` | `PREFLIGHT [QUICK\|FULL] [ALL]` | Checks the node is ready for the show (FULL also decodes every clip in it; ALL every node of the group, one section per node); each row carries the line that fixes it; answers the report's id | low | yes |
| `PREFLIGHT REPORT` | `PREFLIGHT REPORT [<id>]` | The last preflight report, or one by id | low | no |

## STATE

top level `show`; `air.master`, `air.panic`.

## Papers note

(What shipped, for the integration docs pass.)
