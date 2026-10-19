# wp3-stability, wp3-stability-2 on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| `WATERMARK` | `WATERMARK <output>\|ALL ON\|OFF [TOP\|BOTTOM\|BOTH] [AT <x> <y>] [SIZE <px 4-32>]` | A frame code on an output's picture at an edge or a place, at a block size, that an observer reads back to prove which frame was shown (one undo step) | review | yes | wp3-stability |
| `OBSERVE` | `OBSERVE <output> FROM <capture uri> \| OBSERVE <output> OFF` | Watches an output's picture through a capture input (capture:<provider>/<device>[/<input>]) and reads its frame code: each frame shown, stale or lost; OFF stops watching (kept on the node) | review | yes | wp3-stability |
| `OBSERVE STATUS` | `OBSERVE STATUS [<output>]` | Each observed output's verdict, the frame seen and how late, in words | low | no | wp3-stability |
| `LATENCY STEP` | `LATENCY STEP <output> [VIA <camera>\|OBSERVER] [STEPS <3-60>]` | Measures glass to glass: steps the frame code's region (VIA a camera, the whole output) to white and times when the observer or the camera sees each step, with each stage's share (it puts the steps on the air: refused while armed) | high | yes | wp3-stability |
| `GPU REHEARSE` | `GPU REHEARSE [REMOVE]` | Rehearses a device loss: removes the render device on purpose and answers when the first good frame is back, with the time (the outputs black for its length: refused while armed) | high | yes | wp3-stability-2 |

## Forms added to existing rows

None.

## STATE

top level `observe`; `render.removals` (stability-2).

## Papers note

(What shipped, for the integration docs pass.)
