# wp2-phonescan on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `PHONE INVITE` | `PHONE INVITE [<name>] [FOR <minutes 1-60>]` | A one-time link and QR code that makes a phone a scan camera (to this session only; never from an assistant) | high | yes |
| `PHONE LIST` | `PHONE LIST` | Phones paired with this node and what their cameras can do | low | no |
| `PHONE FORGET` | `PHONE FORGET <phone>\|ALL` | Forgets a phone's key and disconnects it | review | yes |
| `SCAN NEXT` | `SCAN NEXT` | The picture for the current pattern is taken (a folder camera in manual mode waits for this) | review | yes |
| `SCAN SUGGEST` | `SCAN SUGGEST <scan> [ASSISTANT]` | Measured surface suggestions for a scan (regions, and a pixel quad per planar region), each queued as a proposal a person approves (ASSISTANT: names from the node's assistant, from text only) | low | no |
| `SCAN SOLVE` | `SCAN SOLVE <scan>` | Solves every projector, view and the camera of a scan together (projector-anchored bundle adjustment) | review | yes |
| `SCAN VIEWS` | `SCAN VIEWS <scan>` | A scan's views with their residuals, coverage per projector and pairing facts | low | no |
| `SCAN DROP` | `SCAN DROP <scan> <view>` | Drops a view from a scan and solves again | review | yes |

## STATE

top level `phones`.

## Papers note

(What shipped, for the integration docs pass.)
