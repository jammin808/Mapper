# wp2-companion on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `INDEX` | `INDEX` | The show's structure for controllers (playlists and items, surfaces, projectors, outputs, devices, generators, and every package's section with its preset hint) with structure and label signatures | low | no |
| `EVENTS` | `EVENTS [SINCE <seq>]` | The node's recent events (item start and end, armed, panic, faults, calibration moved…) for automations | low | no |
| `RECORD` | `RECORD ON\|OFF` | This session hears every line an operator performs on the desk as ACTION lines (never a secret) | low | no |
| `PRESENCE` | `PRESENCE <json>` | A controller says what it is and what it shows (kind, module version, page) | low | no |
| `WHOAMI` | `WHOAMI` | This session as the node sees it: transport, role, paired, armed, and the verbs it may run now | low | no |
| `SELECT` | `SELECT <id>…\|NONE` | What the desk has selected, for controllers that learn from it | low | no |
| `THUMB` | `THUMB <media:<id>\|output:<id>\|playlist:<id>> [<w>x<h>] [JPEG\|PNG] [IF <etag>]` | A small picture for a key or a list, cached by content and ETag | low | no |
| `COMPANION PRESS` | `COMPANION PRESS <page>/<row>/<column> [ON <companion>]` | Presses a button on a Companion this node links to (its HTTP API) | review | yes |
| `COMPANION VAR` | `COMPANION VAR <name> <value> [ON <companion>]` | Sets a Companion custom variable | review | yes |
| `COMPANION BRIGHTNESS` | `COMPANION BRIGHTNESS <0-100> [<surface>] [ON <companion>]` | Dims Companion's surfaces (REST v2; the key from the credential store) | review | yes |

## STATE

top level `clients`, `events`, `selection`; `project.index`.

## Papers note

(What shipped, for the integration docs pass.)
