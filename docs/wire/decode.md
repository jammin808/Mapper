# wp2-decode on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `MEDIA INSPECT` | `MEDIA INSPECT <media>` | What a clip is to the decoder: codec and texture, chunks, second stage, MB per frame and per second, HDR reading, odd size, and the lines that fix what is slow | low | no |

## STATE

top level `decode`; `machine.gl`.

## Papers note

(What shipped, for the integration docs pass.)
