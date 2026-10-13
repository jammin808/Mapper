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

## A connection's life (TCP wire)

1. On connect: if the node has a pairing token and the peer is neither loopback (when trusted) nor in the
   trusted-address list, the first line is `NONCE <32 hex>`.
2. `HELLO <name>` → `OK`; the session becomes a **state listener**: `STATE {json}` at once, then on every change
   (trailing 200 ms throttle, identical pushes skipped) and at least every second while any playlist plays.
   `SUBSCRIBE STATE|CONTEXT|PROJECT|OFF` adds or stops pushes (`CONTEXT {json}`, `PROJECT {json}`).
3. `AUTH-PROOF <hex>` = HMAC-SHA256(token, nonce + name) → `OK paired`; wrong → `ERR wrong token — …` and a fresh
   `NONCE` line; five wrong → closed. `AUTH <token>` only from loopback. Open node: `OK open — this node asks for
   no token`.
4. Queries (STATE, CONTEXT, PING, HELP, VERBS, GET, *LIST*, *STATUS*) are open; mutating verbs need pairing:
   `ERR not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web`.
5. `PING <t1>` → `OK PONG t1 t2 t3` (Unix ms on the node's show clock; t2 at read, t3 at write).
6. Lines that start `OK` or `ERR` are a peer's replies, never commands: ignored. Patterns' Interactive fact lines
   (`BLACKOUT 1`, `LIVE 0`, `LOOK …`, `DECK p n`…) are taken as facts and never answered (`BLACKOUT 1` blacks
   the node out when *Follow Patterns' blackout* is on).
7. Limits: 64 connections, 16 per address; a started line must end within 10 s; one writer per connection,
   replies in order (≤ 256 queued, then closed), pushes latest-wins.

## STATE

Compact JSON, top-level keys in this order (new keys are added at the end of their object; tests match some
as raw substrings):

```json
{"v":1,"seq":412,"id":"a1b2c3d4","t":"playmap","name":"Stage left","version":"0.1.0","at":1760101234567,
 "light":"green","headline":"Show · Intro 0:42 left · 3 outputs green · synced to FOH (0.2 ms)",
 "air":{"blackout":false,"frozen":false,"pattern":null,"identify":false},
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
 "media":{"items":12,"missing":0,"ready":true,"transfers":[]},
 "stage":{"surfaces":6,"meshes":2,"projectors":[{"id":"pj…","name":"Left","solved":true,"residualPx":0.6}],"scans":1},
 "scan":null,
 "sync":{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.35,"driftPpm":3.2,"groups":["show"]},
 "links":{"patterns":{"state":"live","host":"10.0.0.5:9697","paired":true,"clip":"VT 2:28"},
   "stagewatch":{"state":"live","host":"10.0.0.6:9924","timer":{"state":"running","text":"12:30","seconds":750,
   "phase":"warning","color":"#F59E0B","title":"Keynote","message":""}}},
 "peers":[{"t":"patterns","id":"…","name":"FOH","light":"green","live":true}],
 "machine":{"renderer":"OpenGL 4.6 NVIDIA RTX A4000","cpuPct":12,"memMb":840,"gcPauseMsMax":0.4},
 "project":{"id":"pr…","name":"Show","revision":118,"path":"D:/Shows/show.playmap","dirty":false}}
```

- `video` is Patterns' row for the **focused** playlist's current item (null when nothing is loaded), so
  Stagewatch's VT link and countdown work unchanged; `seq` moves on every discontinuity.
- `seq` (top level) rises with every published change; a smaller `seq` after a reconnect means the node
  restarted.

## HTTP (9940)

`GET /api/state` (`?since=<seq>` long-polls ≤ 25 s), `GET /api/context`, `GET /api/project`, `GET /api/verbs`,
`POST /api/cmd` (body = one line; `X-Request-Id` → receipt JSON), `GET /api/media/<sha256>` (range requests,
between nodes), `GET /api/media` (manifest), `GET /ws` (state frames `{"type":"state",…}`, beats every 2 s,
`{"cmd":"…","id":"…"}` commands with receipts, `{"cmd":"ping","t":…}` → pong), `GET /tls`, `GET /tls.cer`,
`/mcp` (Model Context Protocol). The token rides in `X-PlayMap-Token` or `Authorization: Bearer`, never in a URL.

## Previews (VIEW)

The desk's pictures of the node come through the same wire: `VIEW <view> SHOW <what> [<w>x<h>]`, where
`<what>` is `editor`, `output:<id>`, `projector:<id>` (the stage as that projector lights it, at its raster),
`camera:<id>` or `scan:<id>:smart|confidence`; `VIEW <view> CAMERA {"intrinsics":…,"pose":…}` moves an editor
view's eye (the project's own JSON; optional `"content":false`, `"selected":[ids]`); `VIEW <view> HIDE` lets it go.
Pictures are BGRA in the shared-memory ring `PreviewRing.PathFor(nodeId, view)`, fitted inside the size asked
(and 1920×1200) at the picture's own aspect: an output at most 10 times a second, the editor and a projector
at most 30. A view keeps its ring while it lives (a reader opens it once). View ids are file names: letters,
digits, `-`, `_`, `.`, at most 64. VIEW is a query (never journalled) taken from this machine or a paired peer;
a view the node cannot draw (no GPU, no camera feed) is refused with the reason, and the desk draws its own
stand-in. The desk's ids: `editor`, `pj-<projector>`, `op-<output>`, `scan-camera`, `scan-<scan>-smart|confidence`.
