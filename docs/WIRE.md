# PlayMap's wire, STATE and pushes

One vocabulary (`src/PlayMap.Core/Wire/VerbCatalog.cs`) on every transport: the TCP line wire (9941), HTTP
`POST /api/cmd` (9940), the WebSocket `/ws`, MCP tools at `/mcp`, approved Show Bus proposals, and the desk.

## Lines

Patterns' grammar: one UTF-8 line per command (≤ 64 KiB, `\n`, a trailing `\r` stripped); optional request id
`@a1 ` ([A-Za-z0-9_-]{1,32}); words split on spaces, `"double quotes"` keep a phrase, a word starting `{` or `[`
is JSON to the end of the line. Replies: `OK`, `OK <payload>`, `ERR <reason>`, the id after the first word
(`OK @a1 …`). A refused verb says why; a verb that would change nothing is refused ("already playing").

Receipts (Show Bus §3) ride the reply: `OK @a1` for applied; an accepted-but-not-yet-applied verb (a scan, a
transfer) answers `OK @a1 {"stage":"accepted","effect":"…"}`.

### Answered by the transport, refused off the wire

- Every transport answers a bare `STATE` (and its alias `STATUS`), `CONTEXT` and `VERBS`, and `PING`, itself, from
  the node's facts and the catalogue — before the line reaches the engine's router (`CommandRunner`). `STATE` with
  more words after it is the router's, like every other verb.
- `HELLO`, `NONCE`, `AUTH`, `AUTH-PROOF` and `SUBSCRIBE` belong to the line wire. HTTP `POST /api/cmd`, the
  WebSocket, MCP's `run_command` and lines approved on a Stagewatch desk refuse them before anything runs: `ERR HELLO,
  NONCE, AUTH, AUTH-PROOF and SUBSCRIBE belong to the line wire — on the web the token rides in X-PlayMap-Token`.

### Who sent a line (`SessionInfo.Transport`)

The router journals who did what and the transports fence by it. The transport is inferred, never claimed:

| Transport | When |
|---|---|
| `desk` | a line-wire session that said `HELLO PlayMapDesk` (the desk's engine link) |
| `stagewatch` | a line-wire session that said `HELLO Stagewatch` (Stagewatch's VT link) |
| `patterns-device` | a line-wire session that never said HELLO from an address on the trusted list (a Patterns "Lines" device) |
| `wire` | any other line-wire session |
| `http`, `ws`, `mcp` | `POST /api/cmd`, the WebSocket, MCP's tools (each past the web's token gate, so paired) |
| `approval` | a line approved for this node on a Stagewatch approval desk (§11), run through the node's own checks |
| `patterns`, `stagewatch` | the links this node dials itself: the Patterns desk's blackout fact, a Stagewatch operator's screen command |

## A connection's life (TCP wire)

1. On connect: if the node has a pairing token and the peer is neither loopback (when trusted) nor in the
   trusted-address list, the first line is `NONCE <32 hex>`.
2. `HELLO <name>` → `OK`; the session becomes a **state listener**: `STATE {json}` at once, then on every change
   (trailing 200 ms throttle, identical pushes skipped) and at least every second while any playlist plays.
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
   only a paired or trusted peer's facts reach the air.
7. Limits: 64 connections, 16 per address; a started line must end within 10 s; one writer per connection,
   replies in order (≤ 256 queued, then closed), pushes latest-wins.

## STATE

Compact JSON, top-level keys in this order (new keys are added at the end of their object; tests match some
as raw substrings):

```json
{"v":1,"seq":412,"id":"a1b2c3d4","t":"playmap","name":"Stage left","version":"0.1.0","at":1760101234567,
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
   "syncGroup":null,"leader":null,"seq":7,"next":{"id":"it…","label":"Main"}}],
 "outputs":[{"id":"op_1","name":"Projector 1","mode":"projector","display":"DISPLAY2","width":1920,"height":1080,
   "refreshHz":60,"open":true,"fps":60.0,"dropped":0,"frameMsP95":4.1,"pattern":null,"light":"green"}],
 "media":{"items":12,"missing":0,"ready":true,"transfers":[{"media":"md…","name":"Intro","progress":0.42,
   "from":"Stage right","state":"pulling","hash":"5e3f…","bytesDone":220200960,"size":524288000}]},
 "stage":{"surfaces":6,"meshes":2,"projectors":[{"id":"pj…","name":"Left","solved":true,"residualPx":0.6}],"scans":1},
 "scan":{"id":"sc1a2b3c4d","state":"running","stage":"scanning","pattern":31,"patterns":60,"progress":0.44,"quality":0.97,
   "qualityWords":"","message":"Left: pattern 31 of 30 (rows bit 3)","projectors":[{"id":"pj…","name":"Left","progress":1,
   "state":"done"},{"id":"pj…","name":"Right","progress":0.03,"state":"scanning"}],"regions":[],"projector":"pj…"},
 "sync":{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.35,"driftPpm":3.2,"groups":["show"]},
 "links":{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":true,"detail":"VT 2:28","clip":"VT 2:28"},
   "stagewatch":{"state":"live","host":"10.0.0.6:9924","paired":true,"detail":"following Stagewatch at 10.0.0.6:9924",
   "timer":{"state":"running","text":"12:30","seconds":750,"phase":"warning","color":"#F59E0B","title":"Keynote",
   "message":"","trusted":true,"why":""}}},
 "peers":[{"t":"patterns","id":"…","name":"FOH","light":"green","headline":"Programme: VT1","live":true}],
 "machine":{"renderer":"OpenGL 4.6 NVIDIA RTX A4000","cpuPct":12,"memMb":840,"gcPauseMsMax":0.4},
 "project":{"id":"pr…","name":"Show","revision":118,"path":"D:/Shows/show.playmap","dirty":false},
 "faults":[],
 "ai":{"mcp":true,"clients":1,"proposals":0},
 "devices":[{"id":"dv…","name":"Left box","protocol":"pjlink","host":"10.0.0.21","port":0,"projector":"pj…","output":"op_1",
   "light":"green","headline":"on · shutter open · input 31 · 1234 h","liveness":"live","online":true,"power":"on",
   "shutter":"open","input":"31","frozen":false,"lampHours":[1234],"errors":[],"faultsRead":true,"model":"PT-RZ120",
   "serial":"SH1234567","problem":"","credential":true,"last":{"command":"power on","stage":"observed","words":"…","at":1760101234000}}]}
```

- `video` is Patterns' row for the **focused** playlist's current item (null when nothing is loaded), so
  Stagewatch's VT link and countdown work unchanged; `seq` moves on every discontinuity.
- `seq` (top level) rises with every published change; a smaller `seq` after a reconnect means the node
  restarted.
- `sync.offsetMs` is how far this node's reading of its leader's clock can be off (half the best exchange's round
  trip): the raw difference between two machines' monotonic clocks says nothing about the show and is not shown.
  A leader shows its own id as `leader`; a node that neither leads nor follows is `solo`.
- `links.patterns.clip` is the desk's clip as this node counts it on ("VT 2:28"), or why there is none;
  `links.stagewatch.timer.text` is `?` whenever the time is not a fact (no frame, the link quiet, Stagewatch stuck).
- `media.transfers` lists pulls on their way (`queued`, `pulling`) and the ones that failed (`failed: …`, until the
  next fetch of that file); a verified pull leaves the list and its item turns ready.
- `faults` lists what the node is running without, each `{"part","words"}`: a door whose port was refused (`wire`,
  `web`, `hello`, `sync`), no certificate (`tls`), the render host, the scanner, an extension, the project or the
  recovery record. Each is also a red item in the context. A node starts while at least one of the wire and the web
  port listens.
- `ai` is the assistant's door: `mcp` (served at `/mcp`), `clients` (MCP clients heard in the last minute and open
  WebSockets), `proposals` (waiting for a person; 0 on a node without an assistant).

- `scan` is the running or last scan (null before the first): its id from SCAN START's receipt on
  (`OK {"stage":"accepted","effect":"…","scan":"sc…"}`), `state` starting / running / done / failed / cancelled,
  `stage` the step (preparing, latency, scanning, decoding, calibrating, finding surfaces), patterns shown of all,
  `progress` and `quality` 0–1, each projector's progress, and once done the `regions` it suggests as surfaces:
  `{"id":"rg1","label":"floor","rect":[x,y,w,h],"confidence":0.98}` with the rectangle 0–1 across and down the
  first projector's smart scan. The smart scan, the confidence and the camera's live picture are preview views
  `scan-<id>-smart`, `scan-<id>-confidence` and `scan-camera`.
- `devices` is one row per projector link in the project's order: the link, then what its projector reports
  (power, shutter, input, light-source hours, faults with their severity) and the last command's receipt.
  `credential` says whether a secret is kept for it — never the secret.

## The node's own verbs (beside the catalogue's)

- `PAIRING NEW` (high risk) makes a new pairing token, writes it to the node's settings first, then hands it to the
  wire and the web port: a peer paired with the old one is unpaired at its next verb. The reply says where the token
  is read (the node's settings and the Network drawer) — never the token.
- `LINK PATTERNS FOLLOW-BLACKOUT ON|OFF` is the air's *Follow Patterns' blackout* switch; the desk's blackout fact
  is kept either way, so switching back on blacks out at once when the desk is blacked out.
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
  changes what leaves a lens is high risk. `DEVICE DISCOVER [SWEEP <address>]` runs the PJLink search.
- `CREDENTIAL SET <ref> [<user>] <secret>` keeps a secret in the node's credential store (a link's reference is its
  own id; the assistant's key is `assistant`), `CREDENTIAL CLEAR <ref>` forgets one, `CREDENTIAL LIST` names the
  references. Like `AUTH`, a credential line is taken from this machine only, never from an assistant, never
  journalled and never repeated in a reply.
- `ASK <words>` runs one request of the client's conversation with the node's assistant and answers with its words
  and every line it ran or queued (`{"conversation","outcome","text","status","lines":[…],"queued":["ap1"]}`).
  `PROPOSALS [ALL]` lists what waits; `APPROVE <proposal>` runs the line through the node's own path and answers
  the node's reply; `REJECT <proposal> [<why>]` drops it. Only a person approves or rejects: a line from an
  assistant, from MCP or from an approval of either is refused.
- `SCAN SURFACE <scan> <x> <y> [<name>]` makes a surface of what is at x, y on the smart scan (0–1), or
  `SCAN SURFACE <scan> REGION <region> [<name>]` of a suggested region; it answers the surface's id.

## HTTP (9940)

`GET /api/state` (`?since=<seq>` long-polls ≤ 25 s), `GET /api/context`, `GET /api/project`, `GET /api/verbs`,
`POST /api/cmd` (body = one line; `X-Request-Id` → receipt JSON), `GET /api/media/<sha256>` (range requests,
between nodes), `GET /api/media` (manifest), `GET /ws` (state frames `{"type":"state",…}`, beats every 2 s,
`{"cmd":"…","id":"…"}` commands with receipts, `{"cmd":"ping","t":…}` → pong), `GET /tls`, `GET /tls.cer`,
`/mcp` (Model Context Protocol). The token rides in `X-PlayMap-Token` or `Authorization: Bearer`, never in a URL.
