# PlayMap's wire, STATE and pushes

One vocabulary (`src/PlayMap.Core/Wire/VerbCatalog.cs`) on every transport: the TCP line wire (9941) and the same line
wire over the web port's WebSocket (`/ws` with the subprotocol `playmap.lines.v1`), HTTP `POST /api/cmd` (9940), the screens'
WebSocket `/ws`, MCP tools at `/mcp`, approved Show Bus proposals, OSC (`/playmap/line` on UDP 9943), the lines control
bindings fire (MIDI, OSC, Art-Net, sACN; docs/CONTROL.md), the lines a sync group's leader sends its members
(docs/SYNC.md), and the desk, which adds no verb and sends only catalogue lines (docs/wire/desk.md). The direction for the desk
(docs/round3/PLAN.md D11): every row it may send is in its menu bar and, where the row names an object, in that object's
right-click menu, never only behind typing; its console types any line through the same gate and refuses a row that carries
a secret ("type secrets in Network ▸ Links"); such a row is a menu entry that opens its form.

## Lines

Patterns' grammar: one UTF-8 line per command (≤ 64 KiB, `\n`, a trailing `\r` stripped); optional request id
`@a1 ` ([A-Za-z0-9_-]{1,32}); words split on spaces, `"double quotes"` keep a phrase, a word starting `{` or `[`
is JSON to the end of the line (so a word that starts so anywhere else is quoted: `DEVICE ADD "[L]" pjlink 10.0.0.20`). Replies: `OK`, `OK <payload>`, `ERR <reason>`, the id after the first word
(`OK @a1 …`). A line that carries a request id is always answered: a verb that answers nothing otherwise (`CURSOR`, streamed on
every move) answers `OK @a1`. A refused verb says why; a verb that would change nothing is refused ("already playing").

Receipts (Show Bus §3) ride the reply: `OK @a1` for applied; an accepted-but-not-yet-applied verb (a scan, a
transfer, a running optimise job's pause or resume) answers `OK @a1 {"stage":"accepted","effect":"…"}`, with the verb's own answer as a
JSON string in `payload` when it has one; STATE says when it is a fact.

A node that has begun to stop takes no more lines, from any source — every transport here (MCP's tools before they
would queue a line for a person), OSC, a control binding, the assistant, an approval: each is answered `ERR the node
is stopping — it takes no more lines` and journalled as any refusal of its verb, and a line still waiting for the
lane is refused the same way (a group's `SHOW ARM|DISARM ALL` then sends the group nothing), and so is an edit that a line
answered off the lane still has waiting for it (a device's `ADD` or `REMOVE`, the calibration ledger's accept or fallback):
nothing it would change lands, and a re-solve that landed before the stop keeps its new pose and, when a re-solved projector blends
automatically on the live stage, says its blend was not solved again. The node's own lines
still run — a panic's shutters and effects, sent at the panic's instant while the projector links are open (they
close last), since a panic put back after a restart takes its shutters as closed — and its last work (the recovery
record, the journal) is not a line and still happens.

### Answered by the transport, refused off the wire

- Every transport answers a bare `STATE` (and its alias `STATUS`), `CONTEXT` and `VERBS`, and `PING`, itself, from
  the node's facts and the catalogue — before the line reaches the engine's router (`CommandRunner`). `STATE` with
  more words after it is the router's, like every other verb.
- `HELLO`, `NONCE`, `AUTH`, `AUTH-PROOF` and `SUBSCRIBE` belong to the line wire: TCP 9941, and the line channel on `/ws`
  (subprotocol `playmap.lines.v1`), which refuses plain `AUTH` and takes proofs only (docs/wire/companion.md). HTTP
  `POST /api/cmd`, the screens' WebSocket, MCP's `run_command` and lines approved on a Stagewatch desk refuse them before
  anything runs: `ERR HELLO, NONCE, AUTH, AUTH-PROOF and SUBSCRIBE belong to the line wire — on the web the token rides in
  X-PlayMap-Token`. The screens' WebSocket takes one form: `SUBSCRIBE <topic>` opens a package's topic and `SUBSCRIBE OFF`
  ends them (Round 2 below); `STATE`, `CONTEXT` and `PROJECT` stay the line wire's.

### Who sent a line (`SessionInfo.Transport`)

The router journals who did what and the transports fence by it. The transport is inferred, never claimed:

| Transport | When |
|---|---|
| `desk` | a TCP line-wire session that said `HELLO PlayMapDesk` **and** proved the node's token on that connection, from this machine or an address the node lists as a desk (`Companion.DeskAddresses`); on a node that asks no token, only this machine's. The name alone makes no desk |
| `stagewatch` | a line-wire session that said `HELLO Stagewatch` (Stagewatch's VT link) |
| `companion` | a line-wire session that said `HELLO Companion` or `HELLO Companion:<label>` (a controller; the Companion module, `integrations/companion-module-playmap`, says `HELLO Companion:<connection label> module=<version>` on the `/ws` line channel, proves the token and numbers every line `@c<n>`, docs/wire/companion.md) |
| `patterns-device` | a line-wire session that never said HELLO from an address on the trusted list (a Patterns "Lines" device) |
| `wire` | any other session on TCP 9941 |
| `ws` | any other session on the `/ws` line channel, and the screens' WebSocket |
| `http`, `mcp` | `POST /api/cmd`, MCP's tools (each past the web's token gate, so paired) |
| `osc` | a line that arrived as OSC on UDP 9943 |
| `binding` | a line a control binding fired, or its learn |
| `assistant` | a line the node's own assistant ran (`ASK`) |
| `approval` | a line a person approved, on this node (`APPROVE`) or on a Stagewatch approval desk (§11), run through the node's own checks |
| `patterns`, `stagewatch` | the links this node dials itself: the Patterns desk's blackout fact, a Stagewatch operator's screen command |
| `fleet` | a line a hot backup takes from its primary's mirror link (`MIRROR …` only; the primary's own gate decided it, docs/wire/backup.md) |
| `sync` | a line a sync group's member runs from its leader's anchor feed: the leader's anchors (`PLAYLIST ANCHOR`) and air lines (`PANIC`, `PANIC CLEAR`, `BLACKOUT`, `MASTER`, `SHOW ARM`, `SHOW DISARM`, `PREFLIGHT`; any other is refused), a peer to the show's gate |
| `system` | the node's own work: a panic's shutter and effects lines, a scan's record |

## A connection's life (TCP wire)

1. On connect: if the node has a pairing token and the peer is neither loopback (when trusted) nor in the
   trusted-address list, the first line is `NONCE <32 hex>`.
2. `HELLO <name>` → `OK`; the session becomes a **state listener**: `STATE {json}` at once, then on every change
   (trailing 200 ms throttle, identical pushes skipped) and at least every second while any playlist plays or
   anything else STATE shows moves on its own (a rolling timecode with nothing playing).
   `SUBSCRIBE STATE|CONTEXT|PROJECT|OFF` adds or stops pushes (`CONTEXT {json}`, and
   `PROJECT {"revision":N,"project":{…}}` — the revision rides beside the document, which does not carry it; a
   reader compares revisions, never contents). Each subscription answers `OK subscribed …` and pushes at once.
3. `AUTH-PROOF <hex>` = HMAC-SHA256(token, nonce + name) → `OK paired`; wrong → `ERR wrong token — …` and a fresh
   `NONCE` line; five wrong → closed. `AUTH <token>` only from loopback. Open node: `OK open — this node asks for
   no token`.
4. Queries (STATE, CONTEXT, PING, HELP, VERBS, GET, *LIST*, *STATUS*) are open; mutating verbs need pairing:
   `ERR not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web`.
5. `PING <t1>` → `OK PONG t1 t2 t3` (Unix ms on the node's show clock; t2 at read, t3 at write).
6. Lines that start `OK` or `ERR` are a peer's replies, never commands: ignored. Patterns' Interactive fact lines
   (`BLACKOUT 1`, `LIVE 0`, `LOOK …`, `DECK p n`…; no request id; `BLACKOUT` only as `0` or `1`) are taken as facts
   and never answered (`BLACKOUT 1` blacks the node out when *Follow Patterns' blackout* is on —
   `LINK PATTERNS FOLLOW-BLACKOUT ON|OFF`). A fact line from a session that is not paired is dropped, unanswered:
   only a paired or trusted peer's facts reach the air. A line with a request id is no fact: it is read as a verb and
   answered (`@a1 LIVE 0` is refused as an unknown verb).
7. Limits: 64 connections, 16 per address; a started line must end within 10 s; one writer per connection,
   replies in order (≤ 256 queued, then closed), pushes latest-wins.

## STATE

Compact JSON, top-level keys in this order (new keys are added at the end of their object; tests match some
as raw substrings):

```json
{"v":1,"seq":412,"id":"a1b2c3d4","t":"playmap","name":"Stage left","version":"0.2.0","at":1760101234567,
 "light":"green","headline":"Show · Intro 0:42 left · 3 outputs green · synced to FOH (0.2 ms)",
 "air":{"blackout":false,"frozen":false,"pattern":null,"identify":false,"patternsBlackout":false,
   "followPatternsBlackout":true},
 "video":{"file":"intro.mp4","role":"playlist","tag":"Show","position":62,"length":210,"remaining":148,
   "positionText":"1:02","lengthText":"3:30","remainingText":"2:28","text":"Show intro.mp4 · 1:02 / 3:30 · 2:28 left",
   "chip":"Show 2:28","playing":true,"ended":false,"loops":false,"out":false,"call":"",
   "key":"md1a2b3c4d","positionMs":62000,"lengthMs":210000,"remainingMs":148000,"paused":false,"rate":1,
   "seekable":true,"seq":7},
 "playlists":[{"id":"pl…","name":"Show","focused":true,"state":"playing","index":0,"item":{"id":"it…","label":"Intro",
   "source":"media:md…"},"positionMs":62000,"lengthMs":210000,"remainingMs":148000,"rate":1,"loop":false,
   "syncGroup":null,"seq":7,"next":{"id":"it…","label":"Main"},"items":2}],
 "outputs":[{"id":"op_1","name":"Projector 1","mode":"projector","kind":"display","display":"DISPLAY2","width":1920,
   "height":1080,"refreshHz":60,"enabled":true,"open":true,"fps":60.0,"dropped":0,"frameMsP95":4.1,"pattern":null,
   "projector":"pj…","light":"green"}],
 "media":{"items":12,"missing":0,"ready":true,"transfers":[{"media":"md…","name":"Intro","progress":0.42,
   "from":"Stage right","at":"10.0.0.12:9940","state":"pulling","stage":"receiving","since":1760000000000,"note":"",
   "hash":"5e3f…","bytesDone":220200960,"size":524288000}]},
 "stage":{"surfaces":6,"meshes":2,"projectors":[{"id":"pj…","name":"Left","solved":true,"residualPx":0.6,"pins":12}],"scans":1},
 "scan":{"id":"sc1a2b3c4d","state":"running","stage":"scanning","pattern":31,"patterns":60,"progress":0.44,"quality":0.97,
   "qualityWords":"","message":"Left: pattern 31 of 30 (rows bit 3)","projectors":[{"id":"pj…","name":"Left","progress":1,
   "state":"done"},{"id":"pj…","name":"Right","progress":0.03,"state":"scanning"}],"regions":[],"projector":"pj…","pairing":null,
   "view":"v1","views":[],"coverage":[],"suggestions":[]},
 "sync":{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.35,"driftPpm":3.2,"groups":["show"]},
 "links":{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":true,"detail":"VT 2:28","clip":"VT 2:28"},
   "stagewatch":{"state":"live","host":"10.0.0.6:9924","paired":true,"detail":"following Stagewatch at 10.0.0.6:9924",
   "timer":{"state":"running","text":"12:30","seconds":750,"phase":"warning","color":"#F59E0B","title":"Keynote",
   "message":"","trusted":true,"why":""}}},
 "peers":[{"t":"patterns","id":"…","name":"FOH","light":"green","headline":"Programme: VT1","live":true}],
 "machine":{"renderer":"OpenGL 4.6 NVIDIA RTX A4000","memMb":840,"gcPauseMsTotal":12.4},
 "project":{"id":"pr…","name":"Show","revision":118,"path":"D:/Shows/show.playmap","dirty":false},
 "faults":[],
 "ai":{"mcp":true,"clients":1,"proposals":0,"assistant":{"enabled":true,"provider":"…","model":"…","effort":"medium",
   "baseUrl":"","runLowWithoutAsking":true,"runReviewWithoutAsking":false,"keySet":true,"kept":true}},
 "devices":[{"id":"dv…","name":"Left box","protocol":"pjlink","host":"10.0.0.21","port":0,"projector":"pj…","output":"op_1",
   "light":"green","headline":"on · shutter open · input 31 · 1234 h","liveness":"live","online":true,"power":"on",
   "shutter":"open","input":"31","frozen":false,"lampHours":[1234],"errors":[],"faultsRead":true,"model":"PT-RZ120",
   "serial":"SH1234567","problem":"","credential":true,"last":{"command":"power on","stage":"observed","words":"…","at":1760101234000}}],
 "solve":null,"cameras":[]}
```

These are the keys every node writes; the packages' keys follow at the end of their objects (Round 2 below), and
`tests/fixtures/state/main.json` is the whole STATE a node writes.

- `video` is Patterns' row for the **focused** playlist's current item (null when nothing is loaded), so
  Stagewatch's VT link and countdown work unchanged; `seq` moves on every discontinuity.
- A playlist's item may play another playlist (`PLAYLIST ADD <playlist> playlist:<other>`): it shows that playlist's
  current picture, its transitions included. An edit that would make a playlist play itself, directly or through
  others, is refused with the reason; a loop already in a project draws black there and its playlists' rows carry
  `"problem":"plays itself (A → B → A) — black there"`.
- `outputs[].dropped` is that output's late presents in the last 10 s once they pass 0.5 % of its refreshes (0 within
  that budget), what lights the output amber; the totals are in `outputs[].present` (docs/OUTPUT.md). `fps` and
  `frameMsP95` are measured against the output's own target: a windowed output's display refresh, a headless output's
  configured `refreshHz` (the headless loop runs at the fastest enabled output's refresh, not the show's frame rate).
- `version` is the product version, SemVer `MAJOR.MINOR.PATCH` (README, "Versions"): the same from every build of a release,
  so a STATE written by CI's build and by a local one differ in nothing. The build itself — its number and its commit — is
  HEALTH's `build` (docs/wire/fleet.md), which the context's machine item carries too.
- `seq` (top level) rises with every published change; a smaller `seq` after a reconnect means the node
  restarted.
- `sync.offsetMs` is how far this node's reading of its leader's clock can be off (half the best exchange's round
  trip): the raw difference between two machines' monotonic clocks says nothing about the show and is not shown.
  A leader shows its own id as `leader`; a node that neither leads nor follows is `solo`. The sync part adds the group,
  its feed (`live`, `connecting`, `lost`, `off`), whether the clock is slewing, its steps and free-wheel seconds, the
  board's outputs and pairs, and its verdict and words; `sync.light`, last, is the group's light read with that verdict (a
  leader's from its board, a follower's from its feed and clock, grey in no group), the context item's light (docs/wire/sync.md).
- `links.patterns.clip` is the desk's clip as this node counts it on ("VT 2:28"), or why there is none;
  `links.stagewatch.timer.text` is `?` whenever the time is not a fact (no frame, the link quiet, Stagewatch stuck).
- `media.transfers` lists the pulls of a node's own `MEDIA FETCH` (over a peer's `/api/media`, on a node without the fleet's part)
  on their way (`queued`, `pulling`) and the ones that failed (`failed: …`, until the next fetch of that file); a verified pull
  leaves the list and its item turns ready. On a node with the fleet's part, which the exe composes, `MEDIA FETCH` pulls with the
  peer's proof instead (docs/FLEET.md) and writes no row here (an open fix, docs/ROADMAP.md). `at` is the peer's address the pull
  asks, `stage` where a pull on its way stands (`asking` while a peer's list is read, the state still `queued` until a peer's pull
  of the file has started; `receiving` while the bytes come; `verifying` while the whole file is hashed and put in place; `""`
  before a peer is asked and once failed), `since` when the state, the stage or the peer asked last changed (Unix ms), and `note`
  the last failed try's words while the pull tries again. `bytesDone` is the file's bytes in place: a peer's pull says it as it
  starts (the chunks the part file keeps whole), as each 1 MiB block lands with bytes no try of its chunk had, at each chunk's
  end and once every byte is in, so it steps back only when the pull moves on to another peer, to those kept chunks; a failed
  row says 0. A chunk's body that sends nothing for 10 s on the node's clock (a chosen bound) fails that try; the peer's list and
  each answer's headers wait as long as the node's HTTP client allows (60 s). A chunk has three tries; a pull journals its first
  failed try at each peer and its end, the tries between are its `note` and the log's, and the last try's words are the refused
  row's. A fetch journals at most 4 of its pulls' rows and, when it had more, one row at its end that counts how it ended (five
  rows, a quarter of the 20 recent rows the context carries); the rest are in the node's log. A peer's list is asked once a
  fetch; when it could not be read, each file of the fetch that no other peer gives ends with its words.
- `faults` lists what the node is running without, each `{"part","words"}`: a door whose port was refused (`wire`,
  `web`, `hello`, `sync`), a control input or the announcement that could not open (`osc`, `artnet`, `sacn`, `midi`,
  `mdns`), no certificate (`tls`), the render host, the scanner, an extension, the node's settings (`settings`; `settings-flush`
  while the settings file holds a change whose folder the disk did not flush, until a later write flushes whole), the Patterns or
  Stagewatch link, the project or the recovery record. Each is also a red item in the context. A node starts while at least one of the wire and the web
  port listens.
- `ai` is the assistant's door: `mcp` (served at `/mcp`), `clients` (MCP clients heard in the last minute and open
  WebSockets), `proposals` (waiting for a person; 0 on a node without an assistant), and `assistant` — its settings
  in force as `ASSISTANT STATUS` answers them (null on a node without one).

- `scan` is the running or last scan (null before the first): its id from SCAN START's receipt on
  (`OK {"stage":"accepted","effect":"…","scan":"sc…"}`), `state` starting / running / done / failed / cancelled,
  `stage` the step (preparing, latency, scanning, decoding, calibrating, finding surfaces), patterns shown of all,
  `progress` and `quality` 0–1, each projector's progress, and once done the `regions` it suggests as surfaces:
  `{"id":"rg1","label":"floor","rect":[x,y,w,h],"confidence":0.98}` with the rectangle 0–1 across and down the
  first projector's smart scan. `pairing` is a phone camera's frame pairing (docs/wire/phonescan.md; null for any other camera);
  `view` is the view being taken, `views` the scan's views, `coverage` each projector's, each region names its projectors, and
  `suggestions` are the surfaces waiting for a person (docs/wire/multiscan.md). A scan backend other than the node's scan runner
  writes the same keys in the same order — its state as the stage too, null where it knows nothing (an id, the pairing, the view)
  and empty lists. The smart scan, the
  confidence and the camera's live picture are preview views (`VIEW <view> SHOW scan:<id>:smart|confidence`,
  `VIEW <view> SHOW camera:<id>`; see Previews).
- `solve` is the running or last `BLEND SOLVE` (null before the first): `{"id":"bs1","kind":"blend","state":"running",
  "progress":0.42,"words":"solving the blend for 2 outputs","outputs":[],"problems":[]}`. `state` is running, done,
  stale (the stage changed while it solved: the result was dropped, nothing installed), failed or cancelled; once
  done, `outputs` has each output's `covered`, `overlap` and `shadow` fractions, `maxOverlap`, `overlapsWith`, the map's
  size and words. `BLEND SOLVE` answers at once — `OK {"stage":"accepted","effect":"…","solve":"bs1"}` — and solves on a
  worker, one at a time (a second is refused while one runs); the outcome lands in the journal and here.
- `cameras` lists the cameras shown on views now, and any that could not give a picture: `{"id":"cm…","name":"Webcam",
  "state":"failed","views":1,"problem":"Webcam did not open: /dev/video9: No such file or directory"}`. `state` is
  live, opening, scanning (a scan holds it), simulated or failed; a failed camera's views carry a card with the same
  words, and showing a view again tries the camera again. A camera with no view left drops off the list.
- `devices` is one row per projector link in the project's order: the link, then what its projector reports
  (power, shutter, input, light-source hours, faults with their severity) and the last command's receipt.
  `credential` says whether a secret is kept for it — never the secret.

## The node's own verbs (beside the catalogue's)

- `PAIRING NEW` (high risk) makes a new pairing token, writes it to the node's settings first, then hands it to the
  wire and the web port: a peer paired with the old one is unpaired at its next verb. The reply says where the token
  is read (the node's settings and the Network drawer) — never the token.
- `LINK PATTERNS FOLLOW-BLACKOUT ON|OFF` is the air's *Follow Patterns' blackout* switch, kept in the node's settings
  and on the air again before the first transport opens at the next start; the desk's blackout fact is kept either way,
  so switching back on blacks out at once when the desk is blacked out.
- `LINK PATTERNS <host[:port]> [<token>]` and `LINK STAGEWATCH <host[:port]>|"" [<key>]` (`""`: the first
  Stagewatch heard) are accepted at once and dial beside the engine; the link is kept in the node's settings, its
  token or key with it, and comes back at the next start. `OFF` forgets both.
- `MEDIA LIST` rows carry `state` — `ready`, `missing` (no file, or not the bytes the project names), `fetching`,
  `unplayable` (the right bytes, nothing here decodes them) or `unknown` (not hashed or not checked yet) — and
  `progress` (0–1, a fetch's fraction).

## Projector control, credentials and the assistant

- `DEVICE ADD <name> <protocol> <host[:port]> [PROJECTOR <projector>] [OUTPUT <output>]` links a projector box (the
  link is a project entity, `dv…`; `ADD device {json}`, `SET dv… <field> <json>` and `REMOVE` work on it too).
  `DEVICE POWER|SHUTTER|INPUT|FREEZE|PATTERN|LENS|RAW <device> …`, `DEVICE PREPARE <device>` and
  `DEVICE IDENTIFY <device>` answer with the projector's own receipt: refused, unreached or rejected is `ERR` with
  the words; sent or accepted is `OK` not yet applied; observed (a read-back confirmed it) is applied. Anything that
  changes what leaves a lens is high risk. `DEVICE DISCOVER [SWEEP <address>]` runs the PJLink search. `DEVICE NATIVE <device>`
  reads the projector's own geometry and edge blend now, queries only (Panasonic; other protocols answer where to look), with
  the `DEVICE RAW` line that turns off what fights a measured correction (docs/ALIGN.md).
- `CREDENTIAL SET <ref> [<user>] <secret>` keeps a secret in the node's credential store (a link's reference is its
  own id; the assistant's key is `assistant`), `CREDENTIAL CLEAR <ref>` forgets one, `CREDENTIAL LIST` names the
  references. Like `AUTH`, a credential line is taken from this machine only, never from an assistant, never
  journalled and never repeated in a reply.
- The assistant's settings are the node's own, in `playmap.assistant.json` beside its settings (provider, model,
  effort, the low- and review-risk switches); with no file the assistant is off. Its key is never in that file.
  `ASSISTANT STATUS` answers them with `keySet` (whether a key is kept — never the key) and `kept` (whether a change
  is written to the file). `ASSISTANT SET enabled|provider|model|effort|baseUrl|runLowWithoutAsking|runReviewWithoutAsking
  <value>` changes one: high risk for `enabled` and `provider`, review for the rest; from this machine or its paired
  desk only, never from an assistant, MCP or an approval; written to the file first, then in force at once (the
  provider is made again and conversations start over). A value that changes nothing is refused. The key goes only
  through `CREDENTIAL SET assistant …`.
- `ASK <words>` runs one request of the client's conversation with the node's assistant and answers with its words
  and every line it ran or queued (`{"conversation","outcome","text","status","lines":[…],"queued":["ap1"]}`).
  `PROPOSALS [ALL]` lists what waits; `APPROVE <proposal>` runs the line through the node's own path and answers
  the node's reply; `REJECT <proposal> [<why>]` drops it. Only a person approves or rejects: a line from an
  assistant, from MCP or from an approval of either is refused. An MCP assistant's high-risk line (while "AI may
  act" is off) waits on the same list — `run_command` answers `{"stage":"queued","proposal":"ap…"}` — except a
  person's own verbs (`APPROVE`, `REJECT`, `ASK`, `CREDENTIAL …`), which are refused, not queued.
- A proposal is shown without its verb's secret words (`LINK PATTERNS 10.0.0.5 ‹secret›`): in `PROPOSALS`, the
  replies, the journal, the queued receipt MCP answers and the copy a Stagewatch approval desk is offered. The node
  keeps the line whole and an approval runs that (a proposal approved on the desk runs from the node's own record,
  never the desk's copy). A line an assistant writes with a secret in it — over MCP or through `ASK` — never runs on
  its word: it waits for a person whatever the risk switches say. A shown copy (its secret words the marker) is
  refused wherever it arrives. A verb's secret words are counted from where its name ends, as its arguments are: a
  two-word name quoted as one word (`"LINK PATTERNS" 10.0.0.5 <token>`) hides its token as the plain spelling does. A line
  carried inside another is shown the same way wherever the node writes the outer one down, however deep it rides — a binding's
  target after any `TO` or in `BIND LEARN`'s words, every string literal of a JSON argument (read as the node's JSON reader reads
  it: comments skipped, escapes decoded, a key given twice read each time, one cut short read to the end), a word that reads as
  a line whatever white space or quote parts its words (`SET bd… target "…"`) — so a refused `BIND ADD … TO LINK PATTERNS
  10.0.0.5 <token>` is journalled as `BIND ADD … TO LINK PATTERNS 10.0.0.5 ‹secret›`, everything from the first secret word on
  kept back. A line is read so in one pass over its words, the longest the wire reads at once. A binding's target refused as no
  verb names its first word only when it is a bare word, never a quoted phrase or JSON that may hold a token.
- `SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>]` makes a surface of what is at x, y on the smart scan (0–1), or
  `SCAN SURFACE <scan> REGION <region> [ON <projector>] [<name>]` of a suggested region (`ON`: what that projector lights of
  it); it answers the surface's id. `SCAN SUGGEST <scan>` queues a scan's measured regions as such lines on the node's own
  list, each run only by a person's `APPROVE` (docs/wire/multiscan.md).

## HTTP (9940)

`GET /api/state` (`?since=<seq>` long-polls ≤ 25 s; a poll the node's own stop cuts is answered 503 with the reason, never an
empty 200), `GET /api/context`, `GET /api/project`, `GET /api/verbs`,
`POST /api/cmd` (body = one line; `X-Request-Id` → receipt JSON), `GET /api/media/<sha256>` (range requests,
between nodes), `GET /api/media` (manifest), `GET /api/thumb?ref=<media:…|generator:…|playlist:…|color:#RRGGBB>&w=&h=&fmt=jpeg|png`
(a key's picture, with an ETag), `GET /ws` (state frames `{"type":"state",…}`, beats every 2 s, `{"cmd":"…","id":"…"}`
commands with receipts, `{"cmd":"ping","t":…}` → pong, and `{"type":"push","topic":…,"line":…}` frames for a package's
topic; with the subprotocol `playmap.lines.v1` it is the line wire instead, docs/wire/companion.md), `GET /tls`,
`GET /tls.cer`, `/mcp` (Model Context Protocol), `/mirror` (a hot backup's pulls of its primary's show and files, let in by the
pairing proof in place of the token, docs/wire/backup.md). The token rides in `X-PlayMap-Token` or `Authorization: Bearer`, never in
a URL. Outside `/api`, two reads need no token: `GET /health` (this machine's health record; as a WebSocket, every record as
it is published) and `GET /fleet` (the fleet board), docs/wire/fleet.md.

A page in a browser never drives the node from another site. A request whose `Origin` is not the node's own (another site,
or `null`) is refused with 403 on every node route (`/api`, `/ws`, `/mcp`) before it runs; controllers, scripts and other
nodes send no `Origin`, and the node's own pages keep theirs. A request let in **without** the token (a trusted address, this
machine by default, or a node that asks none) must also name the node by an address, exactly `localhost`, one of this
machine's own names (its OS name and its DNS host name, bare, with its domain, as its mDNS label or in `.local`), or a name
listed in the node's settings under `web.hosts` (a venue DNS name); otherwise, where the node asks a token, it is let in
only by the token (a wrong one is counted toward the address's lockout), and else refused with 403 whose words name the host
and the fix. That closes a page pointing its own name at this machine (DNS rebinding). The line channel reads no header
token at its upgrade, so there a host that is not the node's is refused.

## Previews (VIEW)

The desk's pictures of the node come through the same wire: `VIEW <view> SHOW <what> [<w>x<h>]`, where
`<what>` is `editor`, `output:<id>`, `projector:<id>` (what the projector puts on the wall: the stage as it lights it, at its
raster's aspect, through the lens lookup of the first enabled output that drives it, before that output's warp, mask, blend
and trim, black where it lights nothing),
`camera:<id>` or `scan:<id>:smart|confidence`; `VIEW <view> CAMERA {"intrinsics":…,"pose":…}` moves an editor
view's eye (the project's own JSON; optional `"content":false`, `"selected":[ids]`); `VIEW <view> KEEP` renews it;
`VIEW <view> HIDE` lets it go; `VIEW <view> SHOW editor DRAFT` is the editor's view of the blind draft (the air when no
draft is open), on a view id of its own (docs/SHOW.md). A shown view is leased: SHOW, CAMERA and KEEP renew it, and a
view not renewed for 15 s is hidden and its ring let go (journalled), so a viewer that vanished without HIDE costs the
node nothing for long. KEEP of a view that is not shown is refused with the reason — the viewer then shows it again. The desk renews
every view it shows every 5 s.
Pictures are BGRA in a shared-memory ring whose file the SHOW reply names — `{"view":"editor","ring":"<path>","seq":<n>}`, `seq`
the STATE seq from which STATE shows the view as the reply names it (every STATE written before the SHOW ran counts below it) — and
STATE `machine.views[]` lists for every view shown (`id`, `what`, `ring`), fitted inside the size asked
(and 1920×1200) at the picture's own aspect: an output at most 10 times a second, the editor and a projector
at most 30. A view keeps its ring while it lives (a reader opens it once). Each ring the node makes is a new file, one generation
of the view's (`playmap-<node>-<view>~<generation>.preview`): a view shown again after its ring went, or a draft view shown again
while its last ring retires, writes a ring no reader has mapped, so a reader that still maps the old one keeps its last picture and
never holds the new one up; generations count up from the engine's start (its wall-clock milliseconds × 1000), so a ring made after a
restart never takes a name a reader opened before it. `ring` is empty for a view the node writes no ring for. A reader opens the
file the reply or STATE names, and opens it again when that name changes. The desk reads that file only when it is one of the
linked node's rings for that view in its own preview folder (`playmap-<node>-<view>~<digits>.preview`, directly in the folder,
decided from the words before the disk is touched), and no other file: any other name opens nothing, and a named file that is not a
whole ring (empty, short, a pipe or a device) is not mapped; the stand-in says why. It takes each view's ring from the newer of the
reply and STATE: a STATE whose `seq` is below the reply's changes nothing of that view, a STATE as new names it as it stands, and a
STATE counting below the last one is a node that started again. A new name closes and disposes the reader of the old ring before the
new one opens, also while the view is off screen, and a view STATE lists no more is let go. A reply that comes back on a link the
desk has let go names nothing, and a new link is asked for every view at once. View ids are file names: letters,
digits, `-`, `_`, `.`, at most 64. VIEW is a query (never journalled) taken from this machine or a paired peer;
a view the node cannot draw (no GPU, no camera feed) is refused with the reason, and the desk draws its own
stand-in with those words — as it does while no ring is named, and with the words why while the named ring does not open. The
desk's ids: `editor`, `pj-<projector>`, `op-<output>`, `scan-camera`, `scan-<scan>-smart|confidence`.

The scan side feeds the camera and scan views. A camera view (`camera:<id>`, or `camera:sim` for the bare
simulator) shows the frames a running scan takes through that camera; between scans, a capture camera is opened
for its views (a scan takes it over and gives it back; it closes with its last view) and the simulated camera shows
its scene under white light — at most 25 pictures a second. A scan view draws the first projector's smart scan or
its confidence (red unsure, green sure) at the projector's raster when shown, or when the scan running now
finishes; a scan's pictures are kept in memory until the node stops, so a scan from before a restart is refused
with the reason.

## Round 2

Round 2's packages add their verbs, topics and STATE keys through the engine's hooks. Every key above keeps its place;
`outputs[].dropped` counts late presents (above).

- **Rows.** Each package keeps its block of the catalogue to what it builds. A node answers every first-round row and each
  round-two row a package routes; any other row answers `ERR <VERB> is not available on this node yet`. `VERBS` lists every
  row but the node-to-node ones, each with `"available"`; MCP's `list_verbs`, the assistant's prompt and `list_verbs`, and
  the context's `commands` list only the rows the node answers. An assistant's line naming a row the node does not answer,
  or a node-to-node verb, is refused with these words by `propose_commands` and MCP's `run_command` before anything runs or
  waits for a person.
- **The door.** A verb that changes something, or a paired-only query (`PRESENCE`, `THUMB`, the board's focus and lens),
  needs pairing. A node-to-node verb (`MIRROR`, `FLEET FENCE`) is refused from every transport but a fleet peer's and the
  node's own: `ERR <VERB>: this line is node-to-node only`. Then the fleet's door (a hot backup or a fenced node refuses a
  client's line that would change the show it mirrors, with the way out, the words `fleet.door` carries in STATE — docs/wire/backup.md), then the show's gate (Show Mode
  and the GO fence, docs/SHOW.md), then the handler; locks are checked as an edit applies. An ephemeral verb (a cursor, a focus) is never journalled; a
  gesture (a drag, a nudge) is journalled once per gesture by its owner. A verb's row is written once its handler has answered,
  and the work the handler or the show's gate leaves running — a fetch's pulls (the node's own, whose row of what it queued comes
  before any of theirs, and the fleet's), a scan's run, a survey's import, a blend solve (`BLEND SOLVE`, and the one a calibration
  accept, fallback or re-solve runs), a preflight's run, an optimise job, what a learn comes to, a panic's shutter and effect lines,
  a link or sync change and the follow-blackout switch's write, a media import (what it finds and how it ends), the hooks a
  leader's `SHOW ARM|DISARM` runs behind its answer and a hook any arm leaves running past its deadline, a mirrored show's file
  pulls, a hand-back's takeover, an armed assistant's proposal offered to an approval desk — begins, or journals, only after that
  row and before the line is answered, so in the journal and the context's recent rows every row of that work follows the row of
  the verb that began it, however soon the work ends; a refused or faulted line's work begins after its refused or fault row.
  Rows the handler writes while it answers (a `PREFLIGHT WAIT`'s run, the lines a `CONTROL INJECT` fires) come before its row. A
  part that sees the verb's change on a turn of its own (the lane's ticks, the video I/O hub, the Stagewatch, sync and backup
  links, a backup's mirror lines, the projector links, the optimise service's automatic queue, the render host) journals what it sees
  when it sees it (an open fix, docs/ROADMAP.md). `CURSOR` runs off the control lane: its line goes on
  the wall's cursor board as it arrives, stamped then, never queued behind edits.
- **Topics.** `SUBSCRIBE <TOPIC> [<words>…]` opens a package's push topic (INDEX, EVENTS, THUMBS — companion; HEALTH —
  fleet; MIRROR <node> — backup, a primary's show to its hot backup, paired only; ANCHORS — sync, the group's feed, whose members answer each air line with `SYNC RECEIPT` on the same connection,
  the only session a receipt is taken from) beside STATE, CONTEXT and PROJECT: `OK subscribed <topic>`, then the topic's
  lines (what it says while it opens comes after the OK). An unknown topic is refused with the list this node serves.
  In-order lines are bounded per session; a reader too slow for a topic gets `<TOPIC> overflow — …` once and the topic ends
  (the connection and its own replies stay). Latest-wins lines keep only the newest per key. `SUBSCRIBE OFF` ends every push. On the screens' WebSocket,
  `{"cmd":"SUBSCRIBE INDEX"}` answers its receipt, then `{"type":"push","topic":"INDEX","line":"…"}` frames.
- **Events.** Show events (`item.start`, `item.end`, `air.blackout`, `air.freeze`, and each package's) carry the show-clock
  instant they happened at; companion's `EVENTS` topic serves them.
- **STATE.** New keys are appended at the end of their object, in this order:
  top level after `cameras` — `show`, `fleet`, `eye`, `decode`, `videoio`, `control`, `timecode`, `clients`, `events`, `fx`, `audio`,
  `modulation`, `content`, `led`, `calibration`, `onboarding`, `align`, `phones`; `air` — `master`, `panic`, `overlay`; `media` —
  `optimise`; `outputs[]` — `latencyMs`, `present`, `lock`, `fx`, `measured`; `playlists[]` — `chase`; `sync` — `group`, `feed`,
  `slewing`, `steps`, `freewheelS`, `outputs`, `pairs`, `verdict`, `words` (the sync part's), `groupLatencyMs`, `latencyMs`, `lock`, `epoch`, `light`; `show` — `sound`
  (after `preflight`), and `commitLeadMs` at the end of `show.rules`; `show.blind` is the open draft, or null;
  `stage` — `site`, `controls`, `surveys`, `stacks`; `stage.site` — `next` (after `mainsHz`); `stage.projectors[]` — `uncertainty`,
  `priors`, `stack`, `grade`, `quads`; `scan` — `pairing`, `view`,
  `views`, `coverage`, `suggestions`; `scan.regions[]` — `projector`, `projectors`; `machine` — `present`, `views` (each shown view's `id`, `what` and `ring`), `gl` (once the renderer has reported); `machine.present` — after
  output's keys, `asked`, `kept`, `fuse` (`state` clear, armed or blown, `attempt`, `at`), `airLatencyMs`, `latencyWords`; `decode.platform` —
  after decode's keys, `api`, `describe`, `gl`, `zeroCopy`, `direct`, `why`; `decode.platform.blocks` — `arenaWords`; `decode.sources[]` — `device`, `gpuCopies`, `width`, `height`,
  `surfaces`, `vramMb`, `importMsP95`; `decode` — `policy`, `forced`; `project` — `index`.
  Inside the packages' own objects: `air.overlay.cursorP95Ms` is how late a cursor move is drawn (p95 over the last 300 moves,
  −1 before the first), `onboarding.contentAt` is stamped when the render host draws a frame of a quad's content (on a node
  without one, when the project first puts content on a quad an output shows), and `align.devices` is each guarded
  device's last read of its projector's own geometry and blend, with its light, words and fix (docs/wire/resolve.md).
  `stage.stacks` is each stack with its alignment, colour match and failover (what it lost and the light it has left) and its context
  row's light and words,
  `stage.site.next` the facade guide's next step on the live stage (null once through), `stage.projectors[].stack` the stack a
  projector is in, and a stack member's correction is its output's `measured` (docs/wire/stacks.md).
  `null` means the node has none; an unknown number is `-1` and grey, never `0`. `tests/fixtures/state/main.json` is the
  whole STATE; each package's fixture is `tests/fixtures/state/<package>.json`, with its card descriptor beside it.
- **Each package's rows and keys**: [show](wire/show.md) · [blind](wire/blind.md) · [sync](wire/sync.md) ·
  [output](wire/output.md) · [decode](wire/decode.md) · [optimise](wire/optimise.md) · [videoio](wire/videoio.md) ·
  [control and timecode](wire/control.md) ·
  [companion](wire/companion.md) · [fx](wire/fx.md) ·
  [audio](wire/audio.md) · [content](wire/content.md) · [led](wire/led.md) · [wall](wire/wall.md) · [align](wire/align.md) ·
  [resolve](wire/resolve.md) (its forms of wall's and align's rows) · [fleet](wire/fleet.md) · [backup](wire/backup.md) ·
  [phonescan](wire/phonescan.md) ·
  [facade](wire/facade.md) · [stacks](wire/stacks.md) (`STACK NEW|LIST|ALIGN|MATCH|FAILOVER`, `PLAN LIGHT`, `SITE GUIDE` and
  `FACADE GUIDE`; `stage.stacks`, `stage.site.next`, each projector's `stack`, a member's lattice and gains in its output's
  `measured`) · [multiscan](wire/multiscan.md) (views INTO a scan, the joint solve, suggestions, the folder
  camera) · [multiview](wire/multiview.md) (Core's solver behind multiscan's rows: no verb, no STATE of its own) ·
  [desk](wire/desk.md) (no rows: what the desk asks and the STATE it reads) ·
  [d3d11](wire/d3d11.md) (`PRESENT PATH AUTO|DXGI|GL` from the render host's next start, DXGI explicit and experimental, the
  present fuse that `PRESENT RESET` clears; Spout's lines through videoio's rows; each output's `latencyMs` and the node's air
  latency) · [zerocopy](wire/zerocopy.md) (`DECODE STATUS`, the `decode` object STATE carries with each source's path — the path its
  latest frame came by — and why; `DECODE POLICY AUTO|DIRECT|GPU-COPY|COPY-BACK|SOFTWARE [FORCE]` for decoders opened from now on,
  kept in the node's settings; `PRESENT PATH` and `DECODE POLICY` are set-up lines, refused while the show is armed).

## Round 3

Round 3's rows are in the catalogue in one block per package (docs/round3/PLAN.md §3.1). They carry the round's direction: an
installed Windows node (`machine`, `install`), Direct3D 12 behind the RHI with OpenGL as the reference (`gpu`, `plan`, `present`,
`shaders`), proof of what the audience saw (`observe`), undo to a point (`undo`), previews without a CPU copy (`preview`), every
handle typed and nudged by one pixel (`handles`), one picture across several outputs (`canvas`), the guided workflows (`guide`)
and capture cards passed through (`capture`). Once routed, a row reaches `VERBS`, MCP, the assistant's prompt and the context
with no package code. No node answers one until its package routes it. Until then:

- **The rows** answer `ERR <VERB> is not available on this node yet` and change nothing, as round 2's unrouted rows do.
  `VERBS` lists each with `"available": false`. MCP's `list_verbs`, the assistant's prompt and the context's `commands` leave
  them out.
- **The new forms of earlier rows** are refused by that row's handler with the forms that work, and change nothing:
  `VIEW … SHARED <pid>`, `PRESENT PATH D3D12`, `PATTERN … ON <canvas>`, `WARP NODE … TO|BY … PX`, `HANDLE SELECT` of a pin,
  a warp corner or node, or a mask node, `INPUT DISCOVER CAPTURE`, and `INPUT ADD` with a `capture:` URI, `MODE` or `FORMAT`.
  Their words read `<form> is not available on this node yet — <the forms that work>`. A `capture:` URI the grammar refuses
  gets the grammar's words instead, and no line or journal row repeats any of it (docs/wire/videoio.md).
- **Round 3's STATE keys** have their places, and no node writes one while its package's hook is empty: an absent key means
  "not built here", never "none".
- **Each package's page** lists its rows, its forms and its keys: [machine](wire/machine.md) · [install](wire/install.md) ·
  [shaders](wire/shaders.md) · [gpu](wire/gpu.md) · [plan](wire/plan.md) · [observe](wire/observe.md) ·
  [present](wire/present.md) · [undo](wire/undo.md) · [preview](wire/preview.md) · [handles](wire/handles.md) ·
  [canvas](wire/canvas.md) · [guide](wire/guide.md) · [capture](wire/capture.md).
- **Routed:** wp3-shaders' row. Every node answers `FX RELEASE` (a Setup line: refused while armed, never drafted) and counts its
  quarantine in `fx.library.quarantined`; every node the exe composes writes `fx.compile.worker` and `fx.quarantine` in STATE and
  carries the Direct3D 12 road's `stages` in `FX CHECK`'s and `FX IMPORT`'s reports ([shaders](wire/shaders.md)).
- **Routed:** wp3-platform's rows. Every node answers `MACHINE [STATUS]`, `MACHINE SET` and `MACHINE LOCK [CLEAR]` (both changes
  Setup lines, refused while armed and on a node with no node home to keep them in; `GPU-PRIORITY REALTIME` high risk); every node the
  exe composes writes `machine.identity`, `machine.capabilities`, `machine.platform` and `machine.locked` in STATE, preflight's
  `machine` section and INDEX's `machine` section, and a Windows node adds the context's `machine:platform` item. A node that is not
  Windows answers through Core's null platform, which claims nothing and says "the platform seam is Windows only"
  ([machine](wire/machine.md)).
- **Routed:** wp3-desk-ux's rows. Every node answers `UNDO LIST [<n 1-100>]` (low risk: the undo steps of the store the line's edits
  go to, newest first, in field words with who and when) and `UNDO TO <step>` (a step and every newer one taken back as one action,
  gated as `UNDO`: drafted for the blind owner, the operator's while armed, a remote's refused while armed) ([undo](wire/undo.md)).
  `STATE.show.lastGo` names each playlist's last accepted GO with its leader time, its wall time and its session, so every client
  sees a GO another made ([show](wire/show.md)).
