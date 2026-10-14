# wp2-videoio on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `INPUT LIST` | `INPUT LIST` | Every live input with its health, its users and whether it is on air | low | no |
| `INPUT DISCOVER` | `INPUT DISCOVER [NDI\|OMT\|SPOUT\|ALL] [WAIT <0-5000 ms>]` | The NDI, OMT and Spout sources this node sees | low | no |
| `INPUT ADD` | `INPUT ADD <uri> [<name>] [PROXY] [COLD]` | Adds a live input (ndi:HOST (Name), omt:HOST (Name), omt://host:port, spout:Name) to the library, received once however many surfaces show it; answers its id | review | yes |
| `INPUT FALLBACK` | `INPUT FALLBACK <input> HOLD\|BLACK\|<source>` | What an input shows when its feed is lost | review | yes |
| `INPUT BACKUP` | `INPUT BACKUP <input> <uri>\|OFF` | A second feed the input takes over on loss | review | yes |
| `INPUT SWITCH` | `INPUT SWITCH <input> PRIMARY\|BACKUP` | Switches an input between its feeds (answered at the first frame) | high | yes |
| `INPUT RECONNECT` | `INPUT RECONNECT <input>\|ALL` | Reconnects a stale or lost input (refused while it delivers) | high | yes |
| `SEND LIST` | `SEND LIST` | Every send with its tap, format, receivers, tally and drops | low | no |
| `SEND ADD` | `SEND ADD NDI\|OMT\|SPOUT <name> <tap> [<w>x<h>] [@<fps>] [UYVY\|UYVA\|P216\|PA16\|BGRA8\|RGBA8\|RGBA16F] [QUALITY LOW\|MEDIUM\|HIGH] [ALWAYS]` | Sends a picture to the network or to other apps on this machine: tap output:<id>, prewarp:<id>, canvas:<id> or source:<ref>; answers its id | review | yes |
| `SEND ENABLE` | `SEND ENABLE <send> ON\|OFF` | Starts or stops a send | high | yes |
| `SEND REMOVE` | `SEND REMOVE <send>` | Removes a send (its receivers lose the picture) | high | yes |
| `VIDEOIO STATUS` | `VIDEOIO STATUS` | The NDI, OMT and Spout runtimes on this node: loaded, versions, discovery, bandwidth | low | no |
| `VIDEOIO CHECK` | `VIDEOIO CHECK` | Video I/O readiness rows with the lines that fix them | low | no |
| `VIDEOIO SET` | `VIDEOIO SET NDI ON\|OFF \| NDI-GROUPS <groups> \| NDI-DISCOVERY <host,…>\|OFF \| OMT ON\|OFF \| OMT-DISCOVERY <host[:port]>\|OFF \| SPOUT ON\|OFF \| MAX-SEND-MBPS <0-100000>` | Changes this node's video I/O settings | review | yes |

## STATE

top level `videoio`.

## Papers note

(What shipped, for the integration docs pass.)
