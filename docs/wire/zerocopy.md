# wp2-zerocopy on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `DECODE STATUS` | `DECODE STATUS` | This node's decode paths: zero-copy ready, direct sampling proven, and every open decoder's path, why and cost | low | no |
| `DECODE POLICY` | `DECODE POLICY AUTO\|DIRECT\|GPU-COPY\|COPY-BACK\|SOFTWARE [FORCE]` | The best path decoders opened from now on may take (open ones keep theirs); DIRECT needs a proof unless FORCE | review | yes |
| `DECODE PROBE` | `DECODE PROBE [<media>\|ALL] [DIRECT]` | Decodes clips by every path and compares the pictures; DIRECT writes the direct-sampling proof for this driver | review | yes |

## STATE

(adds to wp2-decode's keys).

## Papers note

(What shipped, for the integration docs pass.)
