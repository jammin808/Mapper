# wp2-control on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `CONTROL STATUS` | `CONTROL STATUS` | Control input: OSC, MIDI and DMX, surfaces, bindings, learning | low | no |
| `CONTROL PORTS` | `CONTROL PORTS` | The MIDI ports and OSC, Art-Net and sACN sockets of this machine, and which surface each is | low | no |
| `CONTROL MONITOR` | `CONTROL MONITOR [<n 1-64>]` | The last inputs heard, raw and normalised, with the binding each fired | low | no |
| `CONTROL PROFILES` | `CONTROL PROFILES` | The controller profiles (feedback, init and exit messages) and whether each is verified | low | no |
| `CONTROL ENABLE` | `CONTROL ENABLE ON\|OFF\|TOGGLE` | Every control input on or off (bindings pause; kept on the node) | review | yes |
| `CONTROL SURFACE` | `CONTROL SURFACE ADD <name> MIDI\|OSC\|ARTNET\|SACN [<match>] [PROFILE <id>] \| CONTROL SURFACE REMOVE <surface>` | Names a controller the show's bindings use; answers its id | review | yes |
| `CONTROL ALIAS` | `CONTROL ALIAS <surface> <port glob>\|OFF` | Which port a surface is on this machine (kept on the node) | review | yes |
| `CONTROL FEEDBACK` | `CONTROL FEEDBACK ADD\|REMOVE <host:port>` | An OSC client that hears the node's state | review | yes |
| `CONTROL DMX` | `CONTROL DMX ARTNET\|SACN ON\|OFF [<interface address>]` | Listens for Art-Net or sACN control input (off until turned on; kept on the node) | review | yes |
| `CONTROL INJECT` | `CONTROL INJECT <input words> <value>` | Fires the bindings of an input as if it moved (never from an assistant) | review | yes |
| `BIND LIST` | `BIND LIST [<surface>\|<entity id>]` | The show's control bindings with their words, state and takeover | low | no |
| `BIND ADD` | `BIND ADD <input words> TO <target line> \| BIND ADD <json>` | Binds a control input to a wire line ('*' carries the value: SET sf… opacity *); answers its id | review | yes |
| `BIND LEARN` | `BIND LEARN [FROM <surface>\|MIDI\|OSC\|ARTNET\|SACN] <target line> \| BIND LEARN OFF` | Binds the next control that moves to the target line (30 s); OFF stops learning | review | yes |
| `BIND FORGET` | `BIND FORGET <target line>\|<input words>` | Forgets bindings by target or by input | review | yes |
| `BIND ENABLE` | `BIND ENABLE <binding>\|<surface> ON\|OFF` | Turns a binding, or a surface's bindings, on or off | review | yes |
| `NUDGE` | `NUDGE <id> <field.path> <±delta>` | Moves a number by a step within its range (encoders, Companion dials) | review | yes |
| `TIMECODE STATUS` | `TIMECODE STATUS [<source>\|<playlist>]` | Timecode sources (rolling, format, speed, errors) and every playlist that chases one | low | no |
| `TIMECODE DEVICES` | `TIMECODE DEVICES` | The audio inputs, MIDI ports and Art-Net interfaces timecode can arrive on | low | no |
| `TIMECODE SOURCE` | `TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]] \| TIMECODE SOURCE ADD <name> MTC <port> \| TIMECODE SOURCE ADD <name> ARTNET [STREAM <n>] \| TIMECODE SOURCE ADD <name> INTERNAL [FORMAT <fmt>] \| TIMECODE SOURCE REMOVE <source> \| TIMECODE SOURCE RESET <source>` | Adds, removes or resets a timecode input; answers its id | review | yes |
| `TIMECODE CHASE` | `TIMECODE CHASE <playlist> <source>[,<backup>…]\|OFF` | A playlist follows timecode: each item at its TC in, locked to the code's fitted line, freewheeling through dropouts | high | yes |
| `TIMECODE ENGAGE` | `TIMECODE ENGAGE <playlist>\|ALL ON\|OFF\|TOGGLE` | Engages or disengages a chase (disengaged, the playlist plays by hand from where it is) | high | yes |
| `TIMECODE SET` | `TIMECODE SET <playlist> DELAY <±ms> \| TIMECODE SET <playlist> FREEWHEEL <ms> \| TIMECODE SET <playlist> ONLOSS HOLD\|STOP\|CONTINUE \| TIMECODE SET <playlist> JUMP <ms>\|AUTO \| TIMECODE SET <playlist> GAP BLACK\|HOLD` | Tunes a chase | review | yes |
| `TIMECODE IN` | `TIMECODE IN <playlist> <item> <HH:MM:SS:FF>\|NOW\|OFF` | The timecode an item starts at (NOW takes the chase's current code) | review | yes |
| `TIMECODE INTERNAL` | `TIMECODE INTERNAL <source> PLAY\|STOP \| TIMECODE INTERNAL <source> LOCATE <tc> \| TIMECODE INTERNAL <source> RATE <0.5-2>` | Runs an internal timecode source (rehearsal or master) | high | yes |

## STATE

top level `control`, `timecode`; `playlists[].chase`.

## Papers note

(What shipped, for the integration docs pass.)
