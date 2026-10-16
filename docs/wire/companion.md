# wp2-companion on the wire

The node's side of two-way control: the line wire over the web port's WebSocket, INDEX, EVENTS, WHOAMI, PRESENCE, THUMB, typed
parameters in VERBS, and the network announcement. `docs/COMPANION.md` says how the pieces fit.

## The line channel (`/ws`, subprotocol `playmap.lines.v1`)

- A WebSocket upgrade on `/ws` that offers `playmap.lines.v1` runs the same line session as TCP 9941: one UTF-8 text frame is one
  line of at most 64 KiB (a trailing `\n` is tolerated), and every frame the node sends is one line. Without the subprotocol `/ws`
  is the screens' JSON socket, unchanged.
- The upgrade skips the web's header token; a browser page from another site (an `Origin` header that is not this node's own,
  403), `RequireHttps` (403) and the address's wrong-token lockout (429) still hold before it. Controllers send no `Origin`.
  Where it would be paired at once (a trusted address, or no token asked) it must name the node by an address or one of its own
  names, or a name in `web.hosts` (403 otherwise).
- Until the session pairs it answers only `NONCE`, `HELLO`, `AUTH-PROOF`, `AUTH` (refused: proofs only) and `PING`; anything else is
  `ERR not paired …`. The greeting `STATE` waits for `OK paired`. A node that asks no token, or trusts the address, is paired at once.
- Wrong proofs count against the address in the web port's ledger (the same five a minute as a wrong header token).
- Close codes: 1000 normal; 1003 a binary frame; 1009 a frame past 64 KiB; **4401** five wrong proofs (or a locked-out address);
  1001 the node stopping.
- Session ids on this door start with `l` (TCP's with `w`).

**Who a session is.** A HELLO name is a claim, and a claim only narrows: `Companion` or `Companion:<label>` is transport
`companion`; `PlayMapDesk` is transport `desk` only on the TCP wire **and** with the node's token proved on that connection
(`AUTH-PROOF`, or plain `AUTH` from this machine) **and** from this machine or an address listed in the node's
`Companion.DeskAddresses` setting. Trust of an address is not proof; a WebSocket never yields `desk` (its transport is `ws`). A
trusted session (no `NONCE` at connect) that says `HELLO PlayMapDesk` on a node with a token is sent a `NONCE` after the `OK`, so the
desk proves itself at once. A node that asks no token has no secret to prove, so there only this machine's desk is the desk.

## Verbs

| Verb | Syntax | Answer | Risk | Changes the show |
|---|---|---|---|---|
| `INDEX` | `INDEX` | `OK {INDEX json}` (below) | low | no |
| `EVENTS` | `EVENTS [SINCE <seq>]` | `OK {"seq":N,"missed":M,"events":[…]}` (≤ 256) | low | no |
| `PRESENCE` | `PRESENCE <json>` | `OK`; paired only; known keys `kind`, `module`, `live`, `page`, `recording`, `thumbs`, `where`; ≤ 2 KiB; after HELLO | low | no |
| `WHOAMI` | `WHOAMI` | `OK {"session","transport","channel","client","paired","role","armed","may","minGoMs","proto":1}`; `may` is `"*"` when nothing is gated | low | no |
| `THUMB` | `THUMB <media:<id>\|generator:<id>\|playlist:<id>\|color:#RRGGBB> [<w>x<h>] [JPEG\|PNG] [IF <etag>]` | `OK {"ref","etag","mime","w","h","data"}`, `OK {"ref","etag","same":true}`, or `ERR` with words; paired only | low | no |

`SUBSCRIBE` takes three more topics (the row's syntax lists them):

| Push | Shape | Delivery |
|---|---|---|
| `INDEX {…}` | the INDEX document, at once and whenever its `sig` or `labels` moves, or a section row's `light` or `state` | latest wins |
| `EVENT {…}` | `{"seq","at","leaderNs","kind",…fields}` | in order; a session that falls behind or is sent more than 64 in a second is closed with `EVENTS overflow — … EVENTS SINCE <seq> …` |
| `THUMBS {…}` | `SUBSCRIBE THUMBS <ref>…` (≤ 128, paired only): `{"<ref>":"<etag>"\|null,…}` for the whole watched set, at once and when any moves | latest wins |

**INDEX.** `{"v":1,"revision","sig","labels","playlists":[{"id","name","loop","syncGroup","items":[{"id","label","source","kind","lengthMs"}]}],
"surfaces":[{"id","name","tags","source"}],"projectors":[{"id","name","output","device"}],"outputs":[{"id","name","kind","projector","enabled"}],
"devices":[{"id","name","protocol","projector"}],"generators":[{"id","name","kind","params":[{"name","kind","min","max","step","choices"}]}],
"canvases":[{"id","name","layers":[{"id","name"}]}],"media":{"count"},"sections":[{"section","hint":{"title","rowKind","keys":[{"label","line","feedback","press"}]},"rows":[…]}]}`.
`sig` (16 hex) moves with the ids and kinds of every listed entity, each item's source kind, each generator's parameter names and every
section's rows and hint; `labels` with the names and labels. A rename moves only `labels`. A library is never listed (only its count).

**THUMB.** ETags: `media-<hash16>-<w>x<h>-<j|p>` (the file's content hash), `generator-<hash16>-<w>x<h>-p` (its kind and parameters),
`color-RRGGBB-<w>x<h>-p`; `playlist:<id>` answers its current item's picture and ETag. While the show is armed only a poster made
before is served: `ERR no picture of it yet — posters are made in rehearsal or on import`. Live pictures (`output:`, `projector:`)
are refused with words this round. `GET /api/thumb?ref=…&w=96&h=96&fmt=jpeg` (the web's token gate) answers the bytes with `ETag`;
`If-None-Match` with that ETag answers 304; no picture answers 404 with the words.

**Event kinds raised here.** `fault.raised`, `fault.cleared` (`{"part","words"}`, from STATE's faults). Every other kind is raised by
its package through `RaiseEvent`; the ring keeps them all.

**VERBS and `/api/verbs`.** Each row gains `params` (its first form's typed parameters: `name`, `kind` — entity, enum, number,
integer, text, json, time, ref, line, flag — and when they say something `optional`, `prefix`, `keyword`, `values`, `min`, `max`,
`alt`, `repeats`), `formParams` (every form's) and `press` (`instant` for Panic, Air and Transport rows; otherwise `double` for a
projector's power, `PROJECT OPEN`, `PROJECT NEW`, `SCAN START` and `APPROVE`; `hold` for a high-risk row and for `OUTPUT IDENTIFY`
and `DEVICE IDENTIFY`; `instant` for the rest). A keyword and the choice it introduces (`[MODE FAST|ROBUST]`) are one enum with its
`keyword`, never a flag and a choice a key could send apart. `alt` is another word the value may be (`ALL`, `#<n>`); on a text
parameter whose only alternative carries marks of its own it is the shape the value is written in (`<w>x<h>`, `@<fps>`,
`#<rrggbb>`). `repeats` is a value given several times, a word each (`<output>…`); a range or list inside one word (`<-1…1>`,
`<host,…>`) is not one. The node's contract test fits every line the module can send to one of its row's forms, word for word.

**MCP.** `run_command` asks the node's Show Mode first: a line it refuses is answered with its words and neither runs nor waits for a
person; a line it holds for a person goes to the node, whose gate keeps it on the node's own list.

## Network announcement

`_playmap._tcp.local` on UDP 5353 (shared with Avahi and Bonjour): PTR, SRV (`<host>.local:<web port>`), TXT `id`, `name`, `proto`,
`ws` (`/ws`), `wire` (the TCP line port), and A; `_services._dns-sd._udp.local` lists the type. Legacy unicast queries are answered
to the sender with the query's id and TTLs of at most 10 s. Three probes, then two announcements; a name another host holds becomes
`"<name> (2)"`; a goodbye on stop. The node's `Companion.Mdns` setting turns it off; a bind the machine refuses is a STATE fault
(`part: "mdns"`) and a red preflight row.

## STATE

Top level `clients` (`[{"session","name","kind","transport","channel","address","paired","module","since","recording","where","thumbs"}]`)
and `events` (`{"seq"}`); `project.index` (INDEX's `sig`). Context items `client:<session>`, kind `deck`, green paired, amber
connected but not paired.

## Preflight

Section `controllers`: one row per connected Companion — green paired and live, amber connected but not paired (the note says to enter
the node's pairing token in Companion's PlayMap connection; no line on the node pairs it) — and a red row when the announcement could
not open; grey "no Companion is connected" otherwise.

## Papers note

For the integration docs pass: WIRE.md gains the line channel (`/ws` + `playmap.lines.v1`, its stricter pairing, its refusal of a
foreign `Origin`, and its close codes), the desk-identity rule, the three new SUBSCRIBE topics, INDEX/EVENTS/WHOAMI/PRESENCE/THUMB,
VERBS' `params`/`formParams`/`press`, `GET /api/thumb`, and STATE's `clients`, `events`, `project.index`; ARCHITECTURE.md names
`LineSessions` (one per door) and the mDNS responder in `PlayMap.Net/Discovery`; the trimmed rows (`RECORD`, `SELECT`,
`COMPANION PRESS|VAR|BRIGHTNESS`) are next round.

## wp2-companion-module: what the module says and reads

The Companion module (`integrations/companion-module-playmap`, `docs/COMPANION.md` "The module") is a client of the lines above and
adds nothing to the node's wire.

- **Says**, every line with `@c<n>`: `HELLO Companion:<label> module=<version>`, `AUTH-PROOF <hex>` after a `NONCE`, `PING <t1>` every
  2 s, `PRESENCE {"kind":"companion","module","live","page","thumbs"}`, `WHOAMI` (again when STATE's `show.armed` or `clients` move),
  `VERBS`, `SUBSCRIBE INDEX`, `SUBSCRIBE EVENTS`, `EVENTS SINCE <seq>` after a drop, `SUBSCRIBE THUMBS <ref>…` (the refs its keys show,
  at most 128), `THUMB <ref> <n>x<n> [IF <etag>]`, `INDEX` (when `project.index` moved and no push came within 500 ms), and the lines its
  actions write. Every line it can send is in `test/lines.txt`, which `CompanionContractTests` parses with `VerbCatalog.Match` and fits to
  its row; no line it sends exceeds 64 KiB.
- **Reads**: the replies (`OK @c<n> …`, `ERR @c<n> words`, `{"stage":"accepted",…}` receipts), and the pushes `STATE`, `INDEX`, `EVENT`
  and `THUMBS`, taken once the session is paired (what comes between HELLO's `OK` and the pairing, such as the TCP wire's greeting
  `STATE`, is held, the latest of each, until then); the STATE paths it reads are `test/state-paths.txt`, each found in a STATE the
  engine wrote. Unknown pushes are ignored.
- **A line with no answer**: every line waits 5 s for its `OK`/`ERR`. A line that got none — the time ran out, or the link dropped
  after it was written — left the module, so the node may have run it: the module reports it `unanswered` (red on its key, its error
  saying to check STATE), never `not-sent`, which is kept for lines that never left (the fence, a guard, no link, past 64 KiB).
- **The web port's rules hold**: it sends no `Origin`; a refused upgrade's JSON `error` is shown as the node wrote it; a node that asks for
  a token is paired by proof, so the Host rule for tokenless admission does not apply to it.

**Papers note** (for the integration docs pass): WIRE.md's controllers section names the module as the line channel's client and its
`@c<n>` ids; ROADMAP marks R3.4 (the Companion module) built; README lists `integrations/companion-module-playmap` with `npm test`;
SHOWBUS-NODE.md's Companion row points at the module. CI wants a job for the module's `npm ci && npm test` (Node 22) after the solution
builds, since `e2e-engine.test.ts` runs `tools/PlayMap.Node`.
