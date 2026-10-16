# Control input: OSC, MIDI, Art-Net and bindings

A node takes control from the outside world four ways — OSC over UDP, MIDI from the machine's ports, and DMX over Art-Net or sACN —
and turns every move into a wire line. The show names its controllers (**surfaces**, `ct…`) and its **bindings** (`bd…`): an input
bound to a line. A binding's line goes through the same router and the same show gate as a line typed on the desk, Patterns' or
an assistant's: there is no private path from a fader to the show. The verbs are in `docs/wire/control.md`.

## Surfaces and bindings

A surface is a name the bindings use (`APC40`), its kind (MIDI, OSC, Art-Net, sACN) and a match: a port-name glob for MIDI, a sender
address for OSC, a source address for Art-Net, a source address or CID for sACN. Which port a surface is on *this machine* is the node's, not the show's:
`CONTROL ALIAS APC40 "USB MIDI 2"` keeps it in the node's settings, so the same show runs on two machines whose ports are named
differently. A binding names one input in words and one target line:

```
CONTROL SURFACE ADD APC40 MIDI "APC40*"
BIND ADD MIDI APC40 CC 1 48 TO SET sf_lobby opacity *
BIND ADD MIDI APC40 NOTE 1 53 TO PLAYLIST GO Show
BIND ADD OSC /fader/1 TO SET sf_stage opacity *
BIND ADD ARTNET 0 101+102 TO VIDEO RATE * ON Show
BIND ADD MIDI MSC 127 GO TO PLAYLIST NEXT Show
```

The value word rides as a whole word: `*` the shaped value, `*%` a percentage, `#*` an item number. A target that cannot be bound
is refused with the reason — a query (it would change nothing), a session or approval verb, anything that carries a secret,
another binding verb. A binding to a high-risk line is itself high risk: an assistant's `BIND ADD … TO BLACKOUT ON` needs a
person; so does a line that carries more lines than are worth reading (256 at every depth together), however low each of them is.

`BIND ADD` keeps any other binding on the same input and says so ("also bound to bd…"), so one fader may drive several targets on
purpose. `BIND LEARN [FROM <surface>|MIDI|OSC|ARTNET|SACN] <target>` binds the next control that moves, and **replaces** what that input
was bound to, saying what it replaced ("MIDI CC 1 7 → Lobby opacity (was Stage opacity)"). Learn never binds a release, a fader's
jitter (three steps from its first reading) or a DMX channel that does not move; a continuous control on a target with a number
makes it `*`, a pad keeps the number. It runs for 30 s, or until `BIND LEARN OFF`; STATE says what it waits for. Once the node has
begun to stop, learn binds nothing: a control it hears then is refused with the stopping words ("the node is stopping — it takes
no more lines", in `control.learnt` and the journal's "learn refused" row), and the show is left as it was.

## Shaping

Every move goes behaviour → in-range clip → invert → curve → out-range → takeover → format (`BindingMath`, one table test per
rule):

- **Behaviour** (auto by default): a fader or knob on a value target is absolute; an encoder is relative (two's complement, binary
  offset, signed bit, or NRPN increment/decrement, with acceleration); a button triggers, toggles (from the node's own fact —
  `BLACKOUT TOGGLE` is sent as is; `SURFACE SHOW … ON` reads the surface), is momentary (ON on press, OFF on release), sends a
  value or its velocity. A fader on a button target presses rising through 56 % and releases falling through 44 %.
- **Curves**: linear, exponential, logarithmic, S-curve, steps. **Ranges**: the target's own (`opacity` 0–1, an output's colour
  trim 0–2, gamma 0.2–5 in log space, a rate 0.05–20 in log space, a generator parameter from its catalogue, a verb's own `<lo-hi>`
  word, a playlist's item numbers); a binding's `outMin`/`outMax` override them.
- **Takeover**: a MIDI fader without motors picks up — it moves nothing until it crosses the target's value, and BIND LIST shows
  which way to move it; value scaling meets the target at the fader's end; OSC and DMX write at once (the console's fader is the
  truth). A target moved by anyone else re-arms the pick-up.
- **Debounce** (30 ms) and **hold-to-fire** (`holdMs`).

## Lines, the journal and undo

A binding fires as session `binding` with the authority of the person who made it; the show's gate decides what an armed show lets
through. A press is journalled as its line, with the binding as who. A fader's sweep is journalled **once**, closed 400 ms after
the last move ("OSC /fader → Lobby opacity 0.1 → 0.5 over 0.2 s (5 edits)"), and is one undo step (edits of one field coalesce);
on a sync group's leader each line of a sweep on the group's air (a fader on `MASTER`) still reaches every member as it runs here
(SYNC.md). `CONTROL ENABLE OFF` pauses every binding (kept on the node); `BIND ENABLE` turns a binding or a surface off. `CONTROL
INJECT <input> <value>` fires an input's bindings as if it moved — a person's test, never an assistant's.

## OSC

UDP 9943 (`ControlSettings.OscPort`), bound to the node's address or `OscBind`. OSC 1.0: every type tag (`i f s b h t d S c r m T F
N I` and arrays), bundles nested to 8, address patterns. The node's address space (`/playmap/line`, `/playmap/<verb words>`,
`/playmap/set/<id>/<path>`, `/playmap/surfaces|outputs|generators|playlists/<name>/…`, `/playmap/ping`) and its replies are in the
wire notes. Interop is tested both ways against Node's `osc` package (`tests/interop/osc/`). A sender's changes — its lines and
its bound messages alike — count as paired when the node has no token, a trusted loopback, a trusted address or an OSC-allowed one;
reads are open. A malformed datagram is
answered and counted; one datagram never closes the port.

### Feedback

A control shows what it drives. Every binding sends back, over OSC, its target's **value** (a fader, a knob or an encoder on a
field: the binding's shaping run backwards — out-range, curve, invert, in-range — so a TouchOSC fader follows the desk) or a
**lamp** (a button on a switch target — `BLACKOUT`, `FREEZE`, `SURFACE SHOW`, `OUTPUT ENABLE` — lit from the node's own fact).
`feedback.mode` forces value, lamp or off; `feedback.on`/`off` replace the lamp's 1 and 0. It goes to every client that has sent the
node a bound message in the last 10 minutes (its reply address), or only to the surface's `feedbackTo`, or to the binding's own
`feedback.to` words (`OSC 10.0.0.9:9000 /led/1`, so a MIDI pad can light an OSC lamp) — and to the node's feedback clients
(`CONTROL FEEDBACK ADD 10.0.0.9:9000`, kept on the node, for a display that never speaks first). Each client and address is sent only
what differs from what it shows, at most 50 times a second (`FeedbackHz`); a value is not sent to a control within 300 ms of input
from it (the hand on the fader is not fought, then the control is set right); a client that echoes what it was sent within 50 ms is
dropped. `BIND LIST` says each binding's route or why it has none.

## MIDI

One parser per port (MIDI 1.0: running status, real-time bytes anywhere, Note On velocity 0 as a release, 14-bit CC pairs with a
5 ms wait for the LSB, NRPN/RPN with data entry and increment, bend with an exact centre, SysEx to 4 KiB). MIDI Show Control and
MIDI Machine Control commands bind as `MIDI MSC <device> GO`; device 127 (all-call) reaches every device's bindings. MTC (quarter
frames, full frames and user bits, any device id) goes to the timecode sources. Backends: the ALSA sequencer on Linux (loaded at
run time; without `/dev/snd/seq` it says so and lists no ports), WinMM on Windows (compile-only here, run by the Windows CI job),
and an in-memory backend for tests. MIDI output, controller profiles, CoreMIDI and Windows MIDI Services are next round.

## Art-Net

The node has **one** Art-Net socket (UDP 6454, `EngineServices.ArtNet`), bound to `ControlSettings.ArtNetInterface`, opened the
first time a consumer needs it — Art-Net control input, an Art-Net timecode source, or LED output — and shared by all of them.
Sends use a cached address per destination and allocate nothing. `CONTROL DMX ARTNET ON` turns DMX → bindings on (off by default):
ArtDmx with the sequence check, LTP between sources, ArtSync (frames held until the next sync; 4 s without one applies them at
once), and only the channels bindings read become events. An ArtPoll is answered with a **unicast** ArtPollReply ("PlayMap") after a
random delay of up to 1 s, naming the universes the bindings read. "Art-Net™ Designed by and Copyright Artistic Licence Holdings
Ltd."

## sACN

`CONTROL DMX SACN ON [<interface address>]` turns sACN (ANSI E1.31, written from the standard) → bindings on (off by default). The
receiver listens on UDP 5568 at the interface and joins the multicast group of every universe a binding reads (239.255.u/256.u%256);
on Linux each group has a socket bound to the group's own address. The sources of a universe merge as the standard asks: the
highest priority wins, equal priorities merge highest-takes-precedence per channel (a 16-bit pair as one value), a late packet is
dropped (§6.7.2), a source that terminates or is silent for 2.5 s gives way to those left — a backup console at a lower priority
takes over — and when none is left the channels hold. Preview data is ignored unless an sACN surface accepts it; synchronized data
waits for its synchronization packet once one has been seen. A surface matched by a console's CID or address hears the channels
that console sets; `ControlSettings.DmxAllow` (addresses or CIDs) keeps every other source out of the merge.

### The built-in DMX personality

`BIND ADD {"personality":"playmap","universe":3,"address":101}` (`"source":"sacn"` for sACN) installs a channel block in one undo step and answers the channel
sheet for the lighting designer. Per surface, in the show's order, four channels: **intensity** (opacity), **source index** (the
item of the playlist it shows; 0 = no change), **speed** (that playlist's rate, 50 % = 1×, 0.25×–4×), **shown** (above 56 % on,
below 44 % off); then per output one channel, its **master** (brightness 0–1). A surface that shows no playlist keeps its index and
speed channels, unbound, so the sheet never shifts.

## STATE, context, preflight, INDEX

`STATE.control` holds the OSC socket (with its feedback clients and counts), the MIDI backend and its ports, the Art-Net socket and the
sACN receiver (its groups and sources) with their counts, each surface with its
port and light, the bindings' counts (live, waiting, unplugged), learn and the last input (moving STATE at most five times a
second; `CONTROL MONITOR` shows the last 64 inputs). The context has an item per surface linked `controls` to what its bindings
act on, and items while learning or with input off. Preflight's `control` section checks every bound source can be heard, with
the line that fixes it. Fixture `tests/fixtures/state/control.json`, cards `control.cards.json`.

## Not in this build

Controller profiles and MIDI feedback (lamps, motor faders, receipts that flash while accepted), feedback from a STATE predicate
(`feedback.when`), the `/playmap/state` mirror and `/playmap/subscribe`, OSCQuery (9944), timetagged bundles run at their instant,
sACN discovery, per-channel priority (START code 0xDD), a surface's LTP merge and release on loss, MIDI out, CoreMIDI, Windows MIDI
Services, input forwarding, MIDI clock — the next round.
