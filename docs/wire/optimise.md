# wp2-optimise on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `MEDIA OPTIMISE` | `MEDIA OPTIMISE <media>…\|ALL\|SUGGESTED [TO hap\|hap-alpha\|hap-q\|hap-q-alpha\|rechunk] [QUALITY FAST\|SHOW] \| MEDIA OPTIMISE CANCEL\|PAUSE\|RESUME [<job>\|ALL]` | Makes a version of clips that plays on the GPU here, in the background, never disturbing playback and never touching the source; CANCEL, PAUSE and RESUME steer jobs | review | yes |
| `MEDIA JOBS` | `MEDIA JOBS` | Every optimise job and the governor's state | low | no |
| `MEDIA ESTIMATE` | `MEDIA ESTIMATE <media>…\|ALL\|SUGGESTED [TO <target>] [QUALITY FAST\|SHOW]` | What optimising would do, how long and how much disk, before any work | low | no |
| `MEDIA VERSIONS` | `MEDIA VERSIONS <media>` | A clip's versions, which plays on this node and why | low | no |
| `MEDIA USE` | `MEDIA USE <media> AUTO\|SOURCE\|<version>` | Which file a clip plays (from its next open, never mid-air) | review | yes |
| `MEDIA VERSION` | `MEDIA VERSION REMOVE <version> [FILES]` | Forgets a version (FILES also deletes this node's derived file when nothing else names it) | review | yes |
| `MEDIA RULES` | `MEDIA RULES` | The optimise mode, this show's rules, then the built-in table | low | no |
| `MEDIA RULE` | `MEDIA RULE MODE AUTO\|ASK\|NEVER \| MEDIA RULE ADD <json> [AT <n>] \| MEDIA RULE REMOVE <n>\|<name>` | Changes the optimise mode or this show's rules | review | yes |

## STATE

`media.optimise`.

## Papers note

(What shipped, for the integration docs pass.)
