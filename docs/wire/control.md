# wp2-control on the wire

Control input — OSC, MIDI, Art-Net and sACN — bound to wire lines, and timecode chase (`docs/CONTROL.md`, `docs/TIMECODE.md`). A
`<surface>` is a control surface's id (`ct…`) or name; a `<binding>` a binding's id (`bd…`); a `<source>` a timecode source's id
(`tc…`) or name; a `<playlist>` a playlist's id or name.

**Input words** name one control: `MIDI [<surface>] NOTE|CC|CC14|NRPN|RPN|PROGRAM|POLYPRESSURE <ch 1-16> <n>`,
`MIDI [<surface>] BEND|PRESSURE <ch>`, `MIDI [<surface>] MSC|MMC <device 0-127> <command>`, `OSC [<surface>] </address> [ARG <n>]`
(the address may be an OSC pattern), `ARTNET [<surface>] <port-address 0-32767> <channel 1-512>[+<fine>]`, `SACN [<surface>] <universe 1-63999> <channel 1-512>[+<fine>]`. Patterns' bare rows
(`NOTE 1 53`, `CC 1 7 *`) read as MIDI. **A target line** is any line that changes something, with at most one value word as a
whole word — `*` (the shaped value), `*%` (0–100) or `#*` (an item number, `#3`); session, secret, approval and binding verbs are
refused as targets, and so is a path quoted with single backslashes, with the refusal the node's door gives it (`docs/WIRE.md`).

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `CONTROL STATUS` | `CONTROL STATUS` | JSON: STATE's `control` object | low | no |
| `CONTROL PORTS` | `CONTROL PORTS` | JSON `{backend, open, words, inputs:[{name, surface, open}], outputs:[], osc:{…}, artnet:{…}, sacn:{…}}` (off the lane) | low | no |
| `CONTROL MONITOR` | `CONTROL MONITOR [<n 1-64>]` | JSON `[{at, input, raw, value, binding\|null, source}]`, newest first (off the lane) | low | no |
| `CONTROL PROFILES` | `CONTROL PROFILES` | JSON `[{id:"generic", name, verified:true, words}]` (generic: values in, OSC values and lamps back; controller profiles are next round) | low | no |
| `CONTROL ENABLE` | `CONTROL ENABLE ON\|OFF\|TOGGLE` | effect "control input off — 24 bindings paused"; the same state is refused; kept in the node's settings | review | yes |
| `CONTROL SURFACE` | `CONTROL SURFACE ADD <name> MIDI\|OSC\|ARTNET\|SACN [<match>] [PROFILE <id>] \| CONTROL SURFACE REMOVE <surface>` | `{"id":"ct…"}`; REMOVE is refused while bindings name the surface | review | yes |
| `CONTROL ALIAS` | `CONTROL ALIAS <surface> <port glob>\|OFF` | effect "APC40 is 'USB MIDI 2' on this machine" (node settings; off the lane) | review | yes |
| `CONTROL FEEDBACK` | `CONTROL FEEDBACK ADD\|REMOVE <host:port>` | effect "10.0.0.9:9000 hears the bindings' feedback"; refused: not an IP address and port, already there / not there, the node's own OSC port, more than 16 (node settings; off the lane) | review | yes |
| `CONTROL DMX` | `CONTROL DMX ARTNET\|SACN ON\|OFF [<interface address>]` | effect "sACN control input on at 10.0.0.2 — STATE says what it hears", with the socket's problem when it has one; a new Art-Net interface says the shared socket moves when the node starts again (node settings; off the lane) | review | yes |
| `CONTROL INJECT` | `CONTROL INJECT <input words> <value>` | `{"fired":[{binding, line, ok, reply}]}`; refused from an assistant, MCP or approval session, and when nothing is bound (off the lane) | review | yes |
| `BIND LIST` | `BIND LIST [<surface>\|<entity id>]` | JSON `[{id, input, target, words, enabled, state:"live\|waiting\|unplugged\|off", behaviour, takeover:"up\|down"\|null, value\|null, feedback:"value → OSC /fader/1"\|"<why none>"\|null, why?}]` | low | no |
| `BIND ADD` | `BIND ADD <input words> TO <target line> \| BIND ADD <json>` | `{"id", "bindings":[…], "words":"MIDI APC40 CC 1 48 → Lobby opacity", "alsoBound":[…], "sheet"?}` | review (max with the target's) | yes |
| `BIND LEARN` | `BIND LEARN [FROM <surface>\|MIDI\|OSC\|ARTNET\|SACN] <target line> \| BIND LEARN OFF` | accepted `{"stage":"accepted","effect":"waiting for a control for Lobby opacity (30 s)","until":<unix ms>}`; the outcome is observed in STATE (`control.learning` → null, `control.learnt`) and the journal (off the lane); a control heard once the node has begun to stop binds nothing (`learnt` and a "learn refused" row say the stopping words) | review (OFF low) | yes |
| `BIND FORGET` | `BIND FORGET <target line>\|<input words>` | effect "2 bindings forgotten"; none is refused | review | yes |
| `BIND ENABLE` | `BIND ENABLE <binding>\|<surface> ON\|OFF` | effect; the same state is refused | review | yes |
| `NUDGE` | `NUDGE <id> <field.path> <±delta>` | effect "Lobby opacity 0.4 → 0.45"; refused at a bound: "Lobby opacity is already at its top (1)" | review | yes |
| `TIMECODE STATUS` | `TIMECODE STATUS [<source>\|<playlist>]` | JSON: STATE's `timecode`, or one source or chase | low | no |
| `TIMECODE DEVICES` | `TIMECODE DEVICES` | JSON `{audioIn:[{key, name, channels, backend}], midiIn:[name], artnet:{interface, open}, words?}` | low | no |
| `TIMECODE SOURCE` | `TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]] [FORMAT <fmt>] \| … MTC <port> [FORMAT <fmt>] \| … ARTNET [STREAM <n>] [FORMAT <fmt>] \| … INTERNAL [FORMAT <fmt>] \| TIMECODE SOURCE REMOVE <source> \| TIMECODE SOURCE RESET <source>` | ADD answers `{"id":"tc…"}` as accepted ("opening … — STATE says when code arrives"); REMOVE is refused while an engaged chase has it as its only source; RESET clears the counts | review (RESET low) | yes |
| `TIMECODE CHASE` | `TIMECODE CHASE <playlist> <source>[,<backup>…]\|OFF` | accepted "Main chases FOH LTC (locked), engaged — waiting for code"; refused: no source, already so, a sync follower | high | yes |
| `TIMECODE ENGAGE` | `TIMECODE ENGAGE <playlist>\|ALL ON\|OFF\|TOGGLE` | "Main engaged: takes FOH LTC at its next frame" / "Main disengaged: plays on by hand from 01:02:03:04"; refused when nothing changes | high | yes |
| `TIMECODE SET` | `TIMECODE SET <playlist> DELAY <±ms> \| … FREEWHEEL <ms> \| … ONLOSS HOLD\|STOP\|CONTINUE \| … JUMP <ms>\|AUTO \| … GAP BLACK\|HOLD` | effect "Main: picture 40 ms later than its timecode"; DELAY −2000…2000, FREEWHEEL 0…10000; JUMP 1…10000 is the chase's own jump threshold, which judges its sources' code in a tracker of its own: code further off its line is a jump, taken when the next frame agrees (a lone outlier counts invalid); less is drift the fit takes in, but a step past auto's threshold too steep for the fit to glide within its 0.85–1.15 slope bounds is a jump all the same; AUTO = max(2 frames, 5σ), the source's own judgement; a value that is not a number ("NaN") is refused, and a generic `SET` of `chase.delayNs`/`freewheelNs`/`jumpNs` is held to the same bounds | review | yes |
| `TIMECODE IN` | `TIMECODE IN <playlist> <item> <HH:MM:SS:FF>\|NOW\|OFF` | effect "Song 2 starts at 01:04:10:00 (in Main)"; a drop-frame label that does not exist is refused with the first that does | review | yes |
| `TIMECODE INTERNAL` | `TIMECODE INTERNAL <source> PLAY\|STOP \| … LOCATE <tc> \| … RATE <0.5-2>` | effect "Rehearsal rolling from 01:00:00:00" | high | yes |

`BIND ADD <json>` takes a binding (`{"input","target","shape"?,"feedback"?,"enabled"?}`), a set (`{"bindings":[…]}`, one undo
step), or the built-in DMX personality (`{"personality":"playmap","source"?:"artnet|sacn","universe":<0-32767, sACN 1-63999>,"address":<1-512>,"surface"?}`), which
answers the channel sheet too. A binding's `feedback` is `{"mode":"auto|off|value|lamp","to":"OSC [<host:port>] [<address>]",
"on","off"}` (`to` "" = the binding's own address; a `to` that does not read is refused; `when` predicates and MIDI words are kept
but send nothing in this build). An OSC surface's `feedbackTo` (`SET ct… feedbackTo 10.0.0.9:9000`) is a host:port. Manual transport on an
engaged chase — `PLAYLIST GO/CUE/NEXT/PREV` and the `VIDEO` clip verbs — is refused with "Main chases FOH LTC (locked) —
TIMECODE ENGAGE Main OFF to drive it by hand".

## OSC (UDP 9943)

- `/playmap/line ,s "<line>" [,s "<request id>"]` runs any line. `/playmap/<verb words>[/<word>…] [args]` is that verb with the
  segments and arguments as words (`/playmap/blackout ,i 1` → `BLACKOUT ON`; no argument on a verb that toggles → `TOGGLE`;
  `/playmap/playlist/go/Show ,i 3` → `PLAYLIST GO Show #3`).
- `/playmap/set/<id>/<field>[/<path>…] <value>` is `SET` (one argument a JSON literal, several a JSON array; a pattern in the id
  sets every matching entity); `,s "+" <step>` / `,s "-" <step>` is `NUDGE`; `,s "?"` answers the value on the same address.
- `/playmap/surfaces|outputs|generators|playlists/<name>/…` and `/playmap/layers/<canvas>/<layer>/…` act by name (names with
  OSC's reserved characters read with `_`); `/playmap/playlists/<name>/go|next|prev|cue <n>`.
- `/playmap/ping` → `/playmap/pong ,hhh` (the node's Unix ms).
- Replies go to the sender's own port: `/playmap/reply ,ss "OK"|"ERR" "<payload>"` (`,sss` with the request id); a refusal is
  also `/playmap/error ,s "<words>"`; a malformed datagram is answered `/playmap/error ,s "malformed: …"` and counted.
- Any other address fires the bindings on it: each argument (from 0) an axis value; `T`/`F` press and release; no argument a
  trigger. A bundle's messages run in order (its timetag is read; scheduling at the instant is next round).
- A sender's changes — its lines and its messages to bound addresses alike — count as paired when the node has no token, the
  sender is loopback and the node trusts loopback, or its address is in `NodeSettings.TrustedAddresses` or
  `ControlSettings.OscAllow`; reads are open. An unpaired sender's bound message moves nothing and is answered `/playmap/error ,s
  "not paired — OSC from <address> moves no binding until this node trusts the address (TrustedAddresses or OscAllow)"`.
- **Feedback.** A client that sends to a bound address (anything outside `/playmap`) is fed for 10 minutes after it last spoke:
  each binding with a route sends `<its address> ,f <value>` (the target's value in the control's own range: the binding's shaping
  run backwards) or a lamp (`,f 1`/`,f 0`, or the binding's `on`/`off` words) to the client's own port — or only to its surface's
  `feedbackTo`, or to its `to` endpoint — and to every `CONTROL FEEDBACK` client. At most `FeedbackHz` (50) a second per control,
  only what differs from what the client shows, no value to a control within 300 ms of input from it (a lamp answers at once);
  several messages ride in bundles of at most 1400 bytes; a client that sends back what it was sent within 50 ms is an echo,
  dropped and counted. A binding on an address pattern, or a MIDI or DMX input without OSC `to` words, sends nothing.

## STATE

Top level `control` and `timecode` (after `decode`), and `playlists[].chase` (`null` or `{state, source, text, engaged}`):

```jsonc
"control": {"enabled":true,"axisHz":100,
  "osc":{"port":9943,"open":true,"senders":1,"refused":0,"malformed":0,"feedback":["10.0.0.9:9000"],"clients":1,"feedbackSent":12,
    "echoes":0,"words":""},
  "midi":{"backend":"alsa-seq","open":false,"words":"no ALSA sequencer: /dev/snd/seq is missing …","inputs":[{"name","surface":"ct…"|null,"open"}],"outputs":[]},
  "dmx":{"artnet":{"enabled":false,"open":false,"interface":"all","words"?,"frames":0,"dropped":0,"polls":0},
    "sacn":{"enabled":true,"open":true,"interface":"all","words"?,"universes":[7],"sources":[{"universe","cid","name","address","priority"}],
      "frames":0,"dropped":0,"previews":0}},
  "surfaces":[{"id","name","kind","enabled","connected","port"|null,"profile","bindings","light":"green|amber|red|grey"}],
  "bindings":{"count","enabled","live","waiting","unplugged"},
  "learning":null|{"target","words","until","from"}, "learnt":null|"MIDI CC 1 7 → Lobby opacity (was Stage opacity)",
  "last":null|{"input","value","binding"|null,"at"}},
"timecode": {"sources":[{"id","name","kind":"ltc|mtc|artnet|internal","device","state","text","df","format","detected","fpsMeasured",
    "speedPpm","jitterUs","levelDb"?,"level"?,"open","errors":{"biphase","missing","duplicate","discontinuity","invalid","badBcd","parity"},
    "userBits","ageMs","latencyMs","words"}],
  "chases":[{"playlist","sources","active","mode":"locked","engaged","state","text","item"|null,"rate","delayMs","freewheelMs",
    "freewheelLeftMs"|null,"onLoss","gap","jumpMs","jumpAuto","lastJump"|null,"words"}]}
```

A source's `state` is `none`, `unavailable`, `silent`, `acquiring`, `rolling`, `parked`, `reverse`, `dropout` or `lost`; a chase's
`disengaged`, `waiting`, `acquiring`, `locked`, `parked`, `outside`, `freewheel`, `held`, `stopped`, `free` or `reverse`. A chase's
`jumpMs` is the jump threshold in force — the one `TIMECODE SET … JUMP` set, or auto's max(2 frames, 5σ) of its code now
(`jumpAuto` true). The fixture
is `tests/fixtures/state/control.json`, the cards `control.cards.json`.

## Context

`control:<ct>` (kind `control`) per surface — green connected and bound, amber bound but not connected, red its socket or backend
refused, grey no bindings — linked `controls` to the surfaces, playlists and outputs its bindings name; `control:learning` and
`control:off` (amber). `timecode:<tc>` (kind `timecode`) and `chase:<pl>` (kind `chase`, linked `drives` to its playlist and
`follows` to its sources): green locked/rolling, amber freewheel/outside/acquiring or disengaged while code rolls, red held, stopped,
or a source a chase needs that is unavailable or lost.

## Events

`chase.locked` when a chase locks to its code and `chase.lost` when its freewheel runs out and its loss rule (hold, stop, continue)
takes over, each with `{playlist, source, state}`.

## INDEX

Section `timecode`: one row per chased playlist (`{id, kind:"chase", label, parent, light, state}`), preset "Timecode" with an
Engage key (`TIMECODE ENGAGE $(id) TOGGLE`, held: a chase engaged or let go moves the air).

## Preflight

Sections `control` (Art-Net or sACN bindings with that input off: red, fix `CONTROL DMX ARTNET ON` / `CONTROL DMX SACN ON`; control input off: red, fix
`CONTROL ENABLE ON`; a MIDI surface whose port is not here: amber, fix `CONTROL ALIAS <surface> <port glob>`; the OSC socket) and
`timecode` (a chased playlist's source rolling: green; not: amber, red when its input could not open, fix
`TIMECODE ENGAGE <playlist> OFF`; disengaged: amber, fix `TIMECODE ENGAGE <playlist> ON`).

## In short

- Control input: OSC on UDP 9943 (the `/playmap` address space above), MIDI (ALSA sequencer on Linux, WinMM on Windows, the fake
  backend in tests; CoreMIDI next round), Art-Net 4 input on the node's one Art-Net socket (`EngineServices.ArtNet`, shared with
  LED output); sACN (E1.31) input merged by priority on UDP 5568 and the universes' multicast groups; OSC value and lamp feedback
  to the clients that drive the bindings and to `CONTROL FEEDBACK` clients. MIDI feedback and controller profiles, the
  `/playmap/state` mirror and `/playmap/subscribe`, sACN discovery, OSCQuery and timetag scheduling are next round.
- Bindings (`bd…`) and surfaces (`ct…`) live in the show (`Project.Control`); which port a surface is on is the node's
  (`ControlSettings.SurfacePorts`). A binding's line goes through the router and the show's gate as session `binding`, with the
  authority of the person who made it; a fader's sweep is one journal row and one undo step.
- The built-in DMX personality "playmap" (4 channels per surface, 1 per output) is a channel block a lighting desk patches.
- Timecode chase: LTC (from the audio inputs), MTC (MIDI), Art-Net and internal sources; a chased playlist is a pure function of
  the source's fitted line at every vblank; the guard against manual transport lives in the playback lanes.
- Ports: OSC 9943 (UDP), OSCQuery 9944 (reserved), Art-Net 6454, sACN 5568 (UDP; multicast 239.255.0.0/16).
