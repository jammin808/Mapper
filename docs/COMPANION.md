# PlayMap and Companion: the node's side of two-way control

Bitfocus Companion (and the Stream Deck, X-keys or Loupedeck it drives) is the show's hardware panel. PlayMap's module for Companion
is a separate package; this paper is the node's side it talks to. The node gives a controller five things, all on the same wire
every other client uses (the desk has no private API, and neither does a controller):

1. **A door** — the line wire over the web port's WebSocket, so a controller needs one port and works through the same TLS as the
   web.
2. **The show's structure** — `INDEX`, so a deck's dropdowns and presets are built from data, and rebuilt only when the structure
   moves.
3. **What happened** — `EVENTS`, so automations trigger on an item's end, a fault, an arm, at the instant it happened.
4. **Who it is** — `WHOAMI` and `PRESENCE`, so a key greys out what this controller may not run while the show is armed, and the
   desk sees which deck shows what.
5. **Pictures and types** — `THUMB` for a key's picture, and typed parameters with a press shape for every verb (`VERBS`), so the
   module's actions are generated rather than written by hand.

The node also announces itself as `_playmap._tcp` on the local network, so Companion's device list finds it without an address.

`docs/wire/companion.md` has every verb, push, field and close code.

## The door

`/ws` with the subprotocol `playmap.lines.v1` runs the line session TCP 9941 runs — the same grammar, request ids, receipts,
pushes, `PING` clock exchange and pairing — one text frame per line. Three things differ, all stricter:

- The web's header token is not asked at the upgrade (a WebSocket client cannot always set one), so the line session itself gates the
  connection: until it proves the token (`NONCE`, then `AUTH-PROOF` — HMAC-SHA256 of the nonce and the HELLO name keyed by the token)
  it is answered nothing but pairing, and its greeting `STATE` waits for `OK paired`. The address's wrong-token lockout and
  `RequireHttps` hold before the upgrade, and a wrong proof counts against the address like a wrong header token.
- A web page from another site never reaches it: an upgrade whose `Origin` is not this node's own is refused (403) before it opens,
  since on a trusted address it would be paired at once. Controllers (Companion's module, scripts) send no `Origin`; pages the node
  serves itself keep their origin and are let in. Where it would be paired at once, the upgrade must also name the node by an
  address or one of its own names (`docs/WIRE.md`, HTTP), so a page that points its own name at this machine is refused too.
- Plain `AUTH <token>` is never taken there.

The sessions behind each door are their own (`LineSessions`): either door serves lines when the other could not open.

## Who a client is

A HELLO name is a claim, and a claim only narrows what a session may do. `Companion:<label>` makes a session a controller
(transport `companion`, Show Mode's remote role). The desk is the operator, so its role is proved, never claimed: `PlayMapDesk` is the
desk only on the TCP wire, with the node's token proved on that very connection, from this machine — the desk reads the token
from the node's own settings file — or from an address the node lists in `Companion.DeskAddresses` for a desk on another machine.
A client that is only trusted by address, or that says the desk's name on the WebSocket, is a remote client; a trusted one that says the
desk's name is sent a `NONCE` at once, so the desk's link proves the token without waiting.

## INDEX

INDEX lists playlists with their items, surfaces, projectors, outputs, devices, generators with typed parameters, canvases and the
media count, then one section per package that adds something a controller acts on (effect slots, LED fixtures, inputs and sends,
calibration targets, peers, proposals…). A package contributes its section through `IIndexContributor`: rows from published state,
and a preset hint — a title, a row kind, and key templates (a label, the line a press sends with `$(id)` for the row, the feedback
that lights it, the press shape). The module builds a preset section per hint, so a new package reaches the deck without module code.

Two signatures keep a deck quiet: `sig` moves only when the structure does (ids and kinds, an item's source kind, a generator's
parameter names, a section's rows and hint), `labels` only when names do. STATE carries `project.index` (the `sig`), so a controller
that does not subscribe still knows when to ask. `SUBSCRIBE INDEX` pushes when either moves, when a section row's light or state
moves with the show, or when anything else INDEX lists moves — an item's or a surface's source changed for another of its kind, a
projector's output or device, an output's enabled, a loop, a length (both signatures kept, so a deck relights its keys or repaints
their pictures and rebuilds nothing), latest wins; a burst of edits is one push.

## EVENTS

Packages raise events through `RaiseEvent` with the show-clock instant they happened at (an item's end is its scheduled instant, not
when the lane noticed). The node keeps the last 256 with a sequence number; `EVENTS SINCE <seq>` replays and says how many the ring
no longer holds; `SUBSCRIBE EVENTS` pushes each in order. A gap is never silent: a session that falls behind, or is sent more than 64
in a second, is closed with an overflow line naming the seq to replay from. The node's own faults become `fault.raised` and
`fault.cleared`.

## WHOAMI and PRESENCE

`WHOAMI` answers the session as the node sees it — transport and channel, its name, paired, its Show Mode role, whether the show is
armed, the double-GO window, and the verbs it may run now (`"*"` when nothing is gated). A controller greys out the rest instead of
learning by refusal. `PRESENCE` is a controller's short note (its module version, the page it shows, whether it records, how many
pictures it holds); the desk and the context show it. STATE lists every controller under `clients`; the context has an item per
Companion — green paired and live, amber connected but not paired.

## Pictures

`THUMB <ref> [<w>x<h>] [JPEG|PNG] [IF <etag>]` answers a small picture and its ETag, or "same" when the asker holds the ETag. A clip's
picture comes from the node's poster store when there is one, else one frame decoded at a tenth of the clip (at most 3 s in),
box-filtered on the CPU and encoded as JPEG at the highest quality that fits; it is kept in memory and under the node's folder, so a
restart never decodes it again. Flat-field and colour-cycle generators are drawn as their colour, other generators as a named tile,
`color:#RRGGBB` as itself, and `playlist:<id>` as its current item. ETags follow content: a clip's hash, a generator's parameters, a
colour. **An armed node never decodes for a key**: only a poster made in rehearsal or on import is served, anything else is refused
with words. `SUBSCRIBE THUMBS <ref>…` pushes the watched set's ETags when one moves; `GET /api/thumb` serves the same bytes over HTTP
with `ETag` and 304.

## Typed parameters and press shapes

`VerbParams` reads every catalogue row's forms through `VerbSyntax` into typed parameters — an entity with its id prefix, a literal
choice, a number with its range, a time, a source, a line, JSON, a keyword and its value, another form it accepts (`#<n>` for an item
by position), whether it repeats — with overrides by row name where the syntax says too little. A placeholder is a line or JSON only
when it says `line` or `json` as a word of its own (`<target line>`, `<json>`; `<cursor,outlines,…>` is a word of text), and nothing
follows one, since it takes the rest of the line. A row's press shape: `PLAYLIST GO`, `NEXT` and `PREV`, `VIDEO PLAY` and `PAUSE`,
`BLACKOUT`, `FREEZE` and `PANIC` fire at once, always; a projector's power, a project opened or made, a scan started and a proposal
approved are pressed twice; every other high-risk row (`PANIC CLEAR`, `VIDEO STOP`, `VIDEO SEEK`, `MASTER`, `LED OUTPUT` among them),
and an identify that puts a picture over the air (`OUTPUT IDENTIFY`, `DEVICE IDENTIFY`), is held to confirm; the rest fire at once.
`VERBS` and `/api/verbs` carry `params`, `formParams` and `press`, so the module generates its actions.

## Announcement

A minimal mDNS / DNS-SD responder in `PlayMap.Net/Discovery` answers for `_playmap._tcp.local` on UDP 5353 (sharing the port with
Avahi and Bonjour): PTR, SRV to the web port, TXT (`id`, `name`, `proto`, `ws`, `wire`) and A, plus the service list. It probes before
it announces, renames itself `"<name> (2)"` when another host holds the name, answers legacy unicast queries, and says goodbye on
stop. It binds the node's bind address. A bind the machine refuses is a fault in STATE and a red preflight row — never a stopped node.
`Companion.Mdns` turns it off.

## Preflight

The controllers section has a row per connected Companion: green paired and live, amber connected but not paired (enter the node's
pairing token in Companion's PlayMap connection — nothing on the node pairs it), and red when the announcement could not open.

## MCP and Show Mode

MCP's `run_command` asks the node's Show Mode before it runs or queues a line: what Show Mode refuses is answered with its words and
neither runs nor waits for a person; what it holds for a person goes to the node, whose gate keeps it on the node's own list.

## The module

`integrations/companion-module-playmap` is the Companion 5.1 connection (`jammin808-playmap`, TypeScript on `@companion-module/base`
2.2.1, Node 22). It is the one part of this repository with a licence of its own, the owner's decision for it alone: its source is MIT
(Bitfocus requires that of a module's source) and its packaged form GPL-3.0-only, the most restrictive Companion's tools accept.
`companion/HELP.md` is the operator's page; this is how it works.

**The link.** It opens `/ws` with `playmap.lines.v1` on the node's web port (or wss, keeping the certificate's fingerprint on the first
link and refusing another — a refusal's status and words included — and never falling back to plain TCP), says `HELLO Companion:<connection label> module=<version>` and answers a `NONCE` with `AUTH-PROOF`: the token
is a Companion secret and never rides a line. Nothing the node says before the pairing is published: what it pushes then (the TCP
wire's greeting `STATE`) is held, the latest of each, and taken when it pairs. A node whose web port has no line channel (an older node: its `/ws` takes no subprotocol, or the
upgrade is answered 400, 401 or 404) is reached on the TCP wire. An upgrade the node refuses (a foreign `Origin` — the module sends none — a host name the node does not answer to, HTTPS
required) shows the node's own words from the body. Every line carries `@c<n>`; replies are matched by it, never by order. `PING` every
2 s is the watchdog and the clock (the round trip with the least delay of the last eight gives the node's offset). No line for 4 s is
**stale**: the status says so and every key dims with a ⚠. 10 s (or the node closing) drops the link, the status saying in words
that the node is not there and when the link tries again: what the node said is emptied, every feedback is checked
again (a dead key never stays green; a retry that fails is a drop too; a press's receipt, the deck's own fact and not the node's,
stays on its key for its time, red 2 s or amber at most 10 s, through the drop and every failed retry), and the link opens again
after 1, 2, 4, 8, then 15 s, each ±20 %. A wrong token, a locked-out
address (in a line, by close 4401, or at the upgrade: HTTP 429), or a token changed on the node (its lines answered `not paired`)
halts the link until the token, the address or the connection's label changes (the HELLO names the label, so a rename relinks
under the new name) — an edit to anything else keeps the link and its halt — so a deck never spends the address's wrong tokens.
Any other refusal of the greeting is the node's passing fault and is tried again. An address is read as typed (`host`, `host:port`, `[v6]:port`, a scheme
left off); one no link could open says so in words. Lines the module sends are held to the
node's 64 KiB; what the node sends is not (VERBS and INDEX are long), but JSON nested past 64 levels is dropped, every VERBS field is
type-checked (a vocabulary that builds no actions is not taken; a row with no typed forms or no press, from a node older than them,
keeps the bundled row of its name and takes only whether the node answers it, and one the bundle does not know is left out), and
nothing a push holds can throw into Companion's process.

**Once paired** it says `PRESENCE` (its version, its Live source and page, how many pictures it holds), asks `WHOAMI` (again when Show
Mode or the controllers move) and `VERBS`, subscribes to `INDEX` and `EVENTS`, asks `EVENTS SINCE <seq>` for what a drop missed, and
watches the pictures its keys show with `SUBSCRIBE THUMBS`. After every drop it replays, from a node that speaks events (an older
node, whose STATE has no `events` and which refuses `EVENTS`, raises none and is asked no replay), from the last event it took (or,
having taken none, from the node's `events.seq` when it first heard it, 0 included — a later greeting's seq, which counts what was
raised while the link was down, never moves it); events pushed while the replay is in flight wait for it — at most the node's ring of 256: one more
lets go of what waits and asks the replay again from the last seq taken — a replay the node does not answer is asked again (three
tries, 2 s apart) and one the link drops leaves them untaken, so the last seq never passes a gap; a node whose newest seq is below it
started again and is replayed from its first, and from its first again after a replay the link lost (no later STATE's seq moves
that). An `EVENTS overflow` line replays the same way. An event is kept bounded whatever a push
holds: its kind and each text at most 1 KiB, plain values only, at most 32 fields.

**STATE and INDEX.** STATE is read through one table of paths (`src/state/paths.ts`); the node's contract test finds every one of them in
a STATE the engine wrote. Variables are computed from it as a pure function of STATE, the module's own facts and the node's clock, diffed,
and sent at most 25 times a second; feedback types are checked only when a slice of STATE they read moved. Playing clocks run between
pushes at 10 Hz from `positionMs`, `rate` and STATE's `at`, in whole seconds and progress steps of 0.005. A new INDEX `sig` rebuilds the
variable definitions, the actions' and feedbacks' dropdowns and the preset structure at once; a rename (`labels` only) re-sends the
structure after 3 s, while keys relabel at once through value feedbacks (a playlist's name, its item's and its next one's labels are
STATE's, so its keys relabel with the STATE that carries them, which the node pushes after INDEX); a `project.index` that moved with
no INDEX push following asks `INDEX`.

**Actions** are generated from `VERBS` (`v_<name>`, one per row the module does not say itself, that carries no secret and that the node answers — `CURSOR` gets no line back and its lease lapses unless sent again, so it is no key), with typed
options from `params` — entity dropdowns from INDEX, literal choices, keyword parameters, a form chooser for a row with several — and the
line written by the node's grammar. Every press returns its receipt as the action's result (`ok`, `stage`, `effect`, `error`) on the node's
first reply — `stage` is `applied` or `accepted` (the node's OK), `refused` (its ERR), `unanswered` (the line left the module and no
answer came: none in 5 s, or the link dropped after it was written; the node may have run it, so its error says to check STATE before
sending it again) or `not-sent` (it never left: the fence, a guard, no link, too long) — and lights a corner dot on its key: amber while
sent or accepted — an accepted line until STATE's entries for the ids it names move (or, naming none, until STATE moves past it), at
most 10 s — red for 2 s on `ERR` with the words in `last_refusal`, and red for 2 s when unanswered (`last_receipt` reads
`unanswered: <VERB> — …`; `last_refusal` holds only what the node refused). Smart actions: GO, NEXT and PREV on the focused playlist with `EXPECT` the next item; PANIC per outcome;
blackout and freeze toggles; a section key; the Live page; `raw` (any line of the vocabulary) and `ask_node` (a question, its
answer as the result; a line that is no question, or that the fence refuses, is refused naming only its verb). Every key's line — generated, raw, a section key or Live slot from INDEX — passes one fence, which reads it as the
node does (its words split by the node's grammar, its verb by name or alias — Patterns' `EYE`, `VT`, `CLIP`, `TC` are their rows): it
must start with a verb of the node, never the session's words, a picture's, a secret's or pairing's, nor carry a secret's or pairing's
(`AUTH`, `AUTH-PROOF`, `PAIRING NEW`, `CREDENTIAL SET`, `LINK PATTERNS`, `LINK STAGEWATCH`) where the node may read a line: for a row
whose forms take a line or JSON (`SET`, `ADD`, `BIND ADD`, `BIND LEARN`, `BIND FORGET`, `PLAYLIST ANCHOR`, `DEVICE RAW`, `FX IMPORT`,
`FX CHECK`, read from the bundled catalogue and from the node's own), every run of its words to the end, every word, and every string
literal of the line as the node's JSON reader reads one — comments skipped whatever quote marks they hold, escapes decoded, a key given
twice read both times, a string cut short read to the end — is read as a line too, and so on inside each (`BIND ADD … TO AUTH …`, a
binding's JSON `target`, `SET <binding> target /*…*/"\u004cINK …"`), while a word inside that names any other row is the operator's
own (a label `"Record"`, `SET <item> index 3`, `DEVICE RAW <device> STATUS`); a refusal names only the outer verb,
never the operator's words. Encoders turn `$(this:delta)` into `MASTER`, `VIDEO RATE`, `VIDEO SEEK` (absolute, from the STATE
value plus the steps sent and not yet confirmed, clamped to the range; a seek's target is its playlist's and item's, and since a
playhead moves by itself it steers only until the first STATE written after it) or `GENERATOR SET … BY`, `DEVICE LENS … BY` and a
surface's opacity by `NUDGE` (relative); turns within 30 ms are one line. A preset's press stores its receipt in the key's local
variable `result`, for an *if* block on the key; a part of a Live slot's press that does nothing for its slot (the release or 800 ms of
an instant one, the down of a held one) answers the key's last result unchanged, so `result` stays the press's answer.

**Press shapes** come from each row's `press`, with its per-argument risk (`riskWhen`: `DECODE POLICY … FORCE` is held): GO, NEXT,
PREV, PLAY, PAUSE, BLACKOUT, FREEZE and PANIC fire on the down, never held; every other high-risk row (`PANIC CLEAR`, a stop, a seek,
`MASTER`) and an identify are held 0.8 s; a projector's power, a project opened or made, a scan started and a proposal approved are
two held presses within 3 s of the same line (the module keeps the first, armed for its key and line, each line its own 3 s — a
preset cannot reset a button's step — so a key whose step holds two such actions sends both on its second press, and a press of a
line not armed on the key, the Live page turned under it, arms that line and sends nothing). A section key and a Live
slot are pressed as the stronger of their hint's press and their line's; a Live slot sends an instant line on the down, a held one at
800 ms, and says HOLD (or SURE?) otherwise. GO, NEXT and PREV expect the item the node will play for them — the cued one, the one
after the current in list order, the first when idle — not STATE's `next`, which follows the item's end action; `next` says the same
item. The double-GO guard holds a second GO of a playlist within `min_go_ms`; the node keeps its own minimum too.

**Feedbacks** are booleans for lights and trigger conditions (Patterns' colours by default; `section_light` and `slot_light` are a
section key's and a Live slot's light word) and typed values a layered key binds through
`$(this:value)` style overrides: label, light, state word, progress, time left, picture, score, any STATE path, a Live slot, a section
key's value, the focused playlist. A score (and `cal_score`, the worst target's) is 0-100 points, higher better: a calibration
target's `unit` says its scale, and a `px` score (lower better), like a solve's residual, reads piecewise through the node's grade
boundaries — 0 px is 100, A's 1 px is 85, B's 1.5 px is 70, C's 4 px is 50, 8 px and past is 0. `event_pulse` is true for `hold_ms`
after an `EVENT` of its kind, on the node's clock. A projector's power and shutter are its box's
(`STATE.devices`, linked by projector); a scan runs while its state is not an end; an entity STATE no longer lists but INDEX does keeps
its name, dimmed, and reads REMOVED only when INDEX drops it too. `variable` binds a key to one of the connection's variables (Show
Mode's ARMED or REHEARSAL, the master's 0-100). `verb_locked`
reads WHOAMI's `may`.

**Presets** are fixed for a config and never change with the show; only the structure does. Template groups are keyed by entity id
(`pl.<id>` lists a playlist's items), so a linked key follows its item through renames and reorders. Every preset is an `alternatives`
pair — a layered key, then a simple one for a host that cannot draw it, which wears the layered key's light as its ground through
boolean feedbacks (an item on the air green, next or cued amber; a playlist playing green, paused or cued amber; an output's,
projector's, machine's, section key's or Live slot's light word in its colour) — and passes the host's sanitiser unchanged. Keys breathe with
no traffic, in Companion's own expressions: Show Mode's frame on `oscillate(2000)` while armed, the headline's mark on `blink(500)` while
a fault stands, the Beat key on `oscillate(60000 / bpm, 'square')`, a projector's ground in `colorMix` from red to green by its score. A package's INDEX
section becomes a preset section from its hint: for each key, a template group per press over the rows pressed at it — a row at
the stronger of the hint's press and its own line's, in the order instant, hold, double, the first group `sec.<section>.<i>`, the
others `sec.<section>.<i>.<press>` — so the show's PANIC rows stay instant while PANIC CLEAR and MASTER 0 and 100 are held; lit by the
hint's feedback when it is a STATE path to a light (`outputs.$(id).present.light`), else by the row's light. An `encoder` row (the
show's MASTER) is turned with its value, never pressed: it is no key of the section's preset, its Live slot only shows, and
`section_press` refuses it unsent — the Master encoder preset turns it. The **Live page** is
one template group of 32 slots whose source is the page variable `pm_live`; the module lays the slots out from INDEX and STATE and, while
the show is armed, keeps each slot on its entity (lights and labels still move; a gone one reads REMOVED) until RE-LAY or disarming.

**Pictures** are asked with `THUMB <ref> <size>x<size>` only for keys that show them, kept by ref and size (256 at most), and asked again
with `IF <etag>` when a `THUMBS` push says one moved; one the node refused while the show was armed (only posters made before are
served then) is asked again when STATE's `show.armed` goes false. A key on an item shows its source as INDEX has it now. A picture of
the air (`playlist:`, `output:`, `projector:`) goes when the link drops, as every fact the node said does; a poster of media, a colour
or a generator stays.

**Tests.** `npm test` (Node 22) type-checks, lints and runs the module through `@companion-module/host`'s `InstanceWrapper` — Companion's own
host layer — against a fake node in Node, and against `tools/PlayMap.Node engine --headless` itself (`e2e-engine.test.ts`, which kills the
engine it started on every path, and fails a start that exits, says a port is closed or is not up within 60 s with the engine's stderr).
The node holds its side in `CompanionContractTests`: `verbs.json` is VERBS, every line in `test/lines.txt` and every section key and Live slot line in
`test/section-lines.txt` (filled by the module from the INDEX fixture, a sample row for a section with none) parses and fits its row,
the module's secret rows are every row that carries a secret or pairs, every path in `test/state-paths.txt` is in a STATE the engine wrote, one version, one line protocol, Patterns' palette byte for byte.
`CompanionModuleFixtureTests` writes the module's STATE and INDEX fixtures from the engine (`PLAYMAP_WRITE_FIXTURES=1`), as the verbs
test writes `verbs.json`.

## Next round

Key pages authored on the desk (`KEYS …`) and `COMPANION PAGE`/`COMPANION ADD` (REST provisioning); the node → Companion link
(`COMPANION PRESS`, `COMPANION VAR`, `COMPANION BRIGHTNESS`, the REST key in the credential store); `RECORD` (the operator's lines as
`ACTION` pushes) and with it the module's typed recorder (a port of `VerbCatalog.Match` turning `ACTION <line>` into typed actions) and
`SELECT` for learn; live output and projector thumbnails from preview views with leases; streamed `ASK` replies; Show Bus hello discovery
in the module; the desk's Decks drawer; browsing (OSCQuery, WLED) on the same responder; the TLS fingerprint in the TXT record; the
back-port of id-keyed template groups, value feedbacks and the secret token to Patterns' module; Live slots that press projectors
(identify, shutter on hold), surfaces (show or hide, once STATE says a surface's shown state) and faults (`EYE FOCUS`), and the
`devices` and `proposals` Live rules; `OUTPUT ENABLE … OFF` pressed twice (a press per argument, where today the row's is one hold);
a node that raises `scan.*`, `proposal.*`, `peer.*` and `device.*` events (the design lists them; recipes watch variables until then).
