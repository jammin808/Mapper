# Show Bus 1.3 — the contract between Stagewatch, Patterns, PlayMap, AGMessage and their micro-apps

*Version 1.3, 2026-10-26 (1.1: 2026-10-10; 1.2: 2026-10-11, §10 encrypted connections, §11 the shared approval queue, hello `tls`
and caps `approvals`). 1.3 adds, in §1–§11: the `playmap` kind, `proto` as the revision whose shapes a node speaks, the hello's
numbers, where a hello is sent from and how one id is heard (§2); reads after pairing and each node's public view (§1 rule 4, §7.2);
a receipt's last fields (§3); the context's sizes, the order a sender trims it in, the command notation and the `peers` fields (§6);
one key for one node, and a node on another node's `/ws` (§7, §7.1); each node's row — its public view, its scopes, its show writer,
its doors and named exemptions, and what it does not keep yet (§8); the certificate's whole span (§10); and the approval queue's
lines, risk, ids and shape (§11). §6 gains the rules for the Eye: what every node publishes so any Eye can draw it (P1–P8), the link
ends that name another node, posting a context — on a `line` link too, `CONTEXT POST` — and the kind table. §12 Windows says what a
window is, its view passes and its link, how a node grants a window to another node's app and how that app holds it, with §1 rule
5's words for view passes and the two carriers they ride; §13 PeerLink, one authenticated link between two nodes, with the vectors
every tree checks itself against; and §14 The numbers, every number the contract names, with its node kinds, doors and caps, in one
block each tree's test reads. The scripted peer §13.20 names is in this tree, honest and as an attacker, and §14's
`peerlink.peer.sha256` is its hash; this tree runs S1–S15, S17–S19, S21 and S22 with it against its PeerLink core — its connector on
`line`, `playmap.lines.v1` and `ws`, and its acceptor's parsers, proofs, session check and receipt store behind the kit's stand-in.
S16 and S20 run only the peer's attack, as do S15's slow reader and S12's emitting half, until its `/ws` acceptor speaks LINK and
takes the acceptor's rows over; the connector's readers come with its playout links on the core. A node keeps 1.2 until a release of
its own carries 1.3, and offers LINK only once its tree passes §13.20. §8's Stagewatch row says what Stagewatch 2.19, which carries
1.3, does not keep of it, and this tree already keeps every rule of 1.3 that 2.19 brings. No node holds or draws a granted window
yet. This page is the contract. `docs/ECOSYSTEM.md` is the why and the roadmap; each app's own remote documentation (Stagewatch
`docs/REMOTE.md`, Patterns `docs/REMOTE.md`, PlayMap `docs/SHOWBUS-NODE.md`, AGMessage `docs/NODES.md`) shows how its endpoints
implement this. This one is the master. Patterns and AGMessage keep copies of 1.2, and PlayMap none, until each tree's copy of 1.3
lands and the Landed line below names it. The body — from `## 1. Rules every node follows` to the end, UTF-8 without a BOM, LF line
ends and one final LF — hashes to the SHA-256 the Body line below prints, which Stagewatch's own test pins, so every edit to it is
deliberate. A copy holds the same body, prints the same Body line and copies the scripted peer byte for byte, its own tree's test
holding both hashes; `tools/showbus-copies.sh` in this tree holds the trees the Landed line names to this master (§14.5).*

*Body SHA-256 (§14): 9ca83889a3735eba9dd948a53be3045b9cd1855c68a3682d5e734adf8f901fd5.*

*Landed (§14): Stagewatch.*

Everything here works **on the local network with no internet and no AI**. The only parts that need the internet are
the language models (drafting, summaries, answering questions) and AGMessage's optional Cloudflare tunnel for people
off-site. See §9.

## 1. Rules every node follows

1. **Attempts are not facts.** A command is *sent → accepted → applied*. A node answers every command it was given an id for with a
   receipt that says which (§3). "OK" from a socket write is never success.
2. **Durations, not wall clocks, for anything that counts.** Remaining time travels as a duration with a counter that moves on every
   discontinuity; a receiver counts down from the frame it holds and re-syncs when the counter moves (§4).
3. **Latest wins for state, nothing is dropped for replies.** A slow peer gets the newest state, never a backlog; a reply is never
   dropped but a peer that stops reading is cut (one writer per connection); and a command is never applied twice (13.12.6).
4. **Discovery says what a node is, never what the show is doing; show data goes only where the node knows the reader.** The one show
   fact a hello carries is a Slides agent's `presenting` (§2). A node's *public view* — what any screen in the room may show, named for
   each node in §8 — is open by design. Everything else (its state beyond that view, its context, its journal lines, the answers to its
   queries) travels only on an authenticated link (a key, a proof, a pairing: §7), to this machine where the node trusts it, or from a
   node whose operator left it open (no PIN or token), which a connector says (13.6.7, 13.16.2). A node that still serves more names it in
   its §8 row, with what ends it.
5. **Secrets ride in no URL, no journal line, no frame and no beacon.** Keys, tokens and PINs go in a header or a proof (§7, 13.6);
   the doors that still take one in an address are named in 13.18. The one exception is a *view pass* (§12.2) — a window's address,
   a viewing code, a crew link: it views (a crew link also answers its department's cues and keeps its department's notes), is
   revocable and is counted — which a browser can be given only as an address. Beyond the node that issues it, which shows it only
   to its own operator, a view pass rides as an address in exactly two carriers: a browser's address bar, and the one verified
   PeerLink (or key-verified node link) to the grant's own app (12.5). Everywhere else it is a secret: never in STATE, a context, a
   frame, a journal line, a log line, a report, a show file, a proposal or any other URL. A node that holds another node's view pass
   keeps it as its own secret, which travels only where that node's secrets may (13.18's twin, under its switch); a grant's address
   travels nowhere else (12.5, 12.8).
6. **The picture is computed, the model only reads it.** Lights and headlines come from facts with rules, so they are
   the same with or without AI (§6, §9).

## 2. Discovery — one channel: the hello on UDP 9928

Every node says hello every `hello.every.ms` on UDP `hello.port` (9928), and **every node listens** on it (a shared port,
`SO_REUSEADDR`). Nothing else is needed to find a peer.

**Where a hello goes.** A node whose doors the network can reach broadcasts its hello (255.255.255.255), sent from a socket bound to the
address its doors are bound to — every interface, or the one address — so the address a hello comes from is one its doors answer on. It
may also send a copy to 127.0.0.1; each send is on its own, so a broadcast that fails never stops the copy. **No reader relies on the
copy**: on a shared port a datagram sent to one address reaches one listener of several. A node whose doors are bound to this machine
only says no hello. `ports` names only doors every receiver of that hello can open. 1.3 names no carrier that finds nodes on one machine
with no network: there a typed address (127.0.0.1 and the port) links them, in each app's link settings.

```json
{"t":"main","v":1,"proto":3,"id":"3f9c2a1b7d0e","inst":"c3d4e5f6","name":"Hall A","host":"FOH-PC",
 "ports":{"http":9924,"https":9924,"ws":9924},
 "caps":["timer","cues","messages","context","receipts","clock","screens","windows","aux","slides-in","copilot","approvals","mcp"],
 "clock":1760101234567,"tls":"sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04"}
```

| field | meaning |
|---|---|
| `t` | the node's kind: a §14 `kind` row — `main` (Stagewatch), `agent` (Stagewatch Slides on a presenter PC), `patterns` (Patterns), `playmap` (PlayMap), `messenger` (AGMessage) — or another word of letters, digits, `-` and `_`, at most `hello.kind.chars` |
| `v` | the hello's format: 1. A reader drops a hello whose `v` it does not know |
| `proto` | the Show Bus revision whose shapes the node speaks: §14's `proto` for that revision, a whole number that only grows (1.1 and 1.2 nodes sent 1). What a node does not keep yet of its revision is in its §8 row. A reader never refuses a hello for it, and never decides a safety question by it |
| `id` | stable per install and random — at least 32 bits, never derived from a secret, a serial or a licence — so a renamed node is still the same node: `hello.id.min` to `hello.id.max` letters, digits, `-` and `_` |
| `inst` | `hello.inst.chars` lowercase hex, new at every start of the process |
| `name` / `host` | the node's name and its machine's, each at most `hello.name.chars` characters |
| `ports` | where to connect, only the doors it serves, at most `hello.ports` of §14's `port` words: `http`, `https`, `ws`, `wire` (a line wire: Patterns 9697, PlayMap 9941), `osc`, `companion` (Stagewatch's Companion TCP), `sync` (PlayMap's clock exchange) |
| `caps` | what it can do: at most `hello.caps` words of lowercase letters, digits, `.` and `-`, each at most `hello.cap.chars` — §14's `cap` rows for its kind (§14.6 says what each means), and words its own node page declares. A dotted word is a finer ability of the word before its dot. A reader ignores a word it does not know |
| `clock` | the sender's Unix time in ms (a first, rough offset; §5 is the real one) |
| `tls` | `sha256:` and 64 lowercase hex of its certificate's DER, beside `ports.https` (§10) |
| `presenting` | a Slides agent only: whether it is showing a slide — the one show fact a hello carries (§1 rule 4) |
| anything else | ignored by a reader that does not know it (`build`, `role`, and the older top-level `web`, `wire` and `machine`) |

A hello is at most `hello.bytes` as sent, one datagram that needs no fragmenting; a reader takes one of at least `hello.read.bytes`,
keeps at least `hello.caps` of its caps, and keeps its `name` and `host` whole.

**Hearing.** A reader lists every kind that passes these rules and acts only on kinds it knows. A peer is **live** while hellos keep
coming, **stale** after `peer.stale.ms` without one, **gone** after `peer.gone.ms`; a reader forgets it after `peer.forget.ms`, and keeps
at most `peers.max` peers: a new id beyond that is dropped.

**One id, one peer.** A reader keeps, per id, every address it heard within `peer.stale.ms`, at most `peer.addresses` of them: a new
address past that takes the place of the one heard longest ago, never of the one its link uses. It dials the address its link already
uses while that address is still heard (13.15.4); an address change alone is not a change of the peer. A peer heard from 127.0.0.1, or
from one of the reader's own addresses, is on the reader's machine: the reader may dial 127.0.0.1 first and, if refused, the heard
address. A host name alike on two machines proves nothing.

**Two nodes, one id.** Ids are not authenticated and travel with copied settings. A reader that hears one id with `inst` A, then B,
then A again, each within `peer.stale.ms` of the last — or, from a node that sends no `inst`, two different `host` values within
`peer.stale.ms` — marks the id **in conflict**: it shows it amber, naming both hosts and addresses ("two machines say they are
3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7"), keeps the link it had, dials no new link to that id while the mark stands
(13.4.4, 13.15.3), and clears the mark after `peer.conflict.clear.ms` without the alternation; a connector then dials it again. A
restart (A, then only B) is not a conflict; one `inst` from two addresses is one node with two addresses.

The older beacons (Patterns 9700, Stagewatch rooms 9927) remain for their own features (Patterns' twin and standby, Stagewatch's
multi-room dashboard) and are not used for discovery between apps.

## 3. Commands and receipts

Any command may carry a **request id**: 1 to `request.id.chars` letters, digits, `-` and `_`. A node that sees an id answers with a
receipt for it.

| transport | request | receipt |
|---|---|---|
| Patterns wire (TCP 9697) | `@a1 VIDEO END 10` | `OK @a1 <json?>` · `ERR @a1 <reason>` |
| PlayMap wire (TCP 9941) | `@a1 PLAYLIST GO show #4` | `OK @a1 <json?>` · `ERR @a1 <reason>` |
| Stagewatch Companion TCP (9923) | `@a1 START` | `OK @a1 <stage> <effect>` · `ERROR @a1 <reason>` |
| Stagewatch HTTP (`/api/...`) | header `X-Request-Id: a1` | JSON body (below) |
| Stagewatch WS (`/ws`) | `{"cmd":"start","args":[],"id":"a1"}` | `{"type":"receipt", ...}` (below) |
| AGMessage HTTP / Socket.io | `id` field / ack callback | same JSON |

Receipt JSON (HTTP and WS):

```json
{"type":"receipt","id":"a1","ok":true,"stage":"applied","effect":"Message on the speaker screen","error":null,"refs":{"messageId":12}}
```

`stage` is `accepted` (taken; the node could not confirm it within its deadline, `receipt.deadline.ms`) or `applied` (run by its show
writer; `effect` says what changed). `ok:false` carries `error`. `refs` names what the command made, by number — a message's signal id
(so the sender can follow its acknowledgement), an AI agent's proposal waiting for the operator. A command without an id behaves exactly
as before.

A receipt may end with four more fields, in this order, each only where it applies:

- `final`: `true` on the one follow-up of a receipt that said `accepted` while the command waited on another node — on WS and line
  transports, with the same id, once, within `receipt.final.ms` (on a line, the word `final` after the id: 13.12.4);
- `repeat`: `true` when the receipt is answered from the node's record of an id it already ran, which it does not run again (13.12.6);
- `lateMs`: how late the command ran, when it ran more than `receipt.late.ms` after it was read (13.12.5);
- `frame`: the counter (§4) of the first state frame published after it ran (13.12.2).

A reader ignores a field it does not know. How a connector reads every answer — a line, a JSON receipt or an HTTP status — into one of
five outcomes is 13.12.3's table.

## 4. State frames

Every state stream carries its frame format version and a **frame counter** that rises with every published frame, so a receiver that
sees a smaller one after a reconnect knows the sender restarted: Stagewatch's frames `v` and `seq`, PlayMap's STATE `seq`, Patterns'
STATE `rev`. The node a stream belongs to is named by how it was found (its hello) or by the first message of its link (§7.1); a frame
need not repeat it. Counting values travel as durations (`seconds`, `remainingMs`, `positionMs`) with a counter that moves on every
discontinuity — a seek, a pause, a nudge, a loop, an item change: a **discontinuity counter** inside the object it counts (`video.seq` in
Patterns' STATE, `clip.seq` in PlayMap's), which never moves for the playhead running on, or the frame counter itself where a node sends
a frame on every change (Stagewatch's `seq`).

Liveness, the same everywhere, is 13.10's. A state stream sends a frame or a beat at least every `link.beat.ms`, and a link pings every
`ping.min.ms` to `ping.max.ms` (§5). A link silent past `link.stale.ms` is stale — amber, its counting values shown as "?" (13.16.1) —
and past `link.drop.ms` it is down: dropped and redialled on 13.15's waits, from `link.redial.first.ms` doubling to
`link.redial.max.ms`, with jitter. Nothing is queued while a link is down (13.15.5): a command to a link that is not live is `not-sent`
at once, with a receipt that says so (13.12.3).

## 5. Clock offset

Links that can ping carry timestamps, NTP-style: the client sends its time `t1`, the server answers with its receive time `t2` and send
time `t3`, the client notes `t4` on arrival.

| transport | ping | pong |
|---|---|---|
| Patterns and PlayMap wires | `PING 1760101234567` | `OK PONG 1760101234567 <t2> <t3>` |
| Stagewatch WS | `{"cmd":"ping","t":1760101234567}` | `{"type":"pong","t":...,"t2":...,"t3":...}` |
| Stagewatch Companion | `PING 1760101234567` | `PONG 1760101234567 <t2> <t3>` (after the existing words) |

`offset = ((t2 − t1) + (t3 − t4)) / 2`, `rtt = (t4 − t1) − (t3 − t2)`; keep the sample with the smallest rtt of the last
`clock.window`. A node shows **CLOCKS APART** when a peer's offset, less that sample's error, exceeds `clock.apart.ms` — timers on two
machines then disagree on what time "14:30" is, even though durations stay right. Which samples a link refuses, a sample's error, and
when a clock is fresh and fit for a timed command — its quality — are 13.11's.

## 6. Context — what an AI (or the Eye) needs to understand a node

A node with cap `context` answers **`GET /api/context`** (HTTP, an authenticated read), and a `line` acceptor — Patterns, PlayMap —
also **`CONTEXT`** on its wire, with one shape:

```json
{"v":1,"node":{"t":"main","id":"a1b2c3d4","name":"Hall A"},"at":1760101234567,
 "light":"amber","headline":"Keynote — Ana · 12:30 left · 2:10 behind · Lighting, Sound standing by",
 "items":[
   {"id":"cue:lighting","kind":"department","label":"Lighting","light":"amber","state":"standing by: LX 12"},
   {"id":"timer","kind":"timer","label":"Keynote — Ana","light":"green","state":"running, 12:30 left"},
   {"id":"link:patterns","kind":"link","label":"Patterns FOH","light":"green","state":"Patterns FOH · pinned 5a7e 91c0 · key 7c1d… · scope drive · PeerLink 1 · +4 ms ±2"}],
 "links":[{"from":"link:patterns","to":"@9f8e7d6c","kind":"controls","light":"green"}],
 "recent":[{"at":1760101230000,"text":"GO Lighting: LX 11 (done by Sam)"}],
 "commands":[{"verb":"signal go all","risk":"high","words":"GO every department standing by"}],
 "peers":[{"t":"patterns","id":"9f8e7d6c","name":"Patterns FOH","light":"green","headline":"Programme: VT1 · 3 screens green",
   "heard":"live","link":"line","verified":"key"}]}
```

- `light` is the worst light of the node's items: `red` (wrong now: overtime, curfew missed, unanswered cue, link
  down that the show needs), `amber` (needs attention: behind schedule, standing by, not paired), `green`, `grey`
  (unknown / not verified). Unknown stays unknown: never guessed.
- `headline` is one line computed from the facts — no model. It is what a Stream Deck key, the Eye's node card or a
  dashboard shows, and what an AI reads first.
- `items` and `links` use the Eye's vocabulary.
  - An item is one of the node's own things (P1): its `id`, its `kind` — a word of the kind table below where one fits (P6) — its
    `label`, its `light` and its `state` in words.
  - A link joins two ends with a `kind` — `feeds`, `shows`, `drives`, `carries`, `controls`, `mirrors`, `follows`, `hears`, `routes`,
    `serves` or `sends` — and a `light`. Each end is one of the node's own item ids, or a link end that names another node.

  An Eye draws a node's items as that node's things, each on the plane the kind table gives its kind, and its links as edges.
- **Link ends.** A link's end names another node, one hop:
  - `@<node id>/<item id>` names that node's item (12.10's placement claim is one);
  - `@<node id>` names the node itself.

  The node id is a §2 id, `hello.id.min` to `hello.id.max` letters, digits, `-` and `_`; the item id is at most
  `context.item.id.chars`, and the whole end at most `context.link.end.chars`. A reader refuses a link whose end breaks these, reads
  `@<its own id>` as itself, and places an end on the item it names where it draws that item, else on that node's card with the
  item in its words; a link whose end names a node the reader does not show is left out and counted. A link with an end on another
  node is the publishing node's claim, never a fact about that node, and a reader words it as one ("PlayMap Stage left says: drawing
  it").
- `recent` is the last `context.recent` journal lines in plain words, each at most `context.recent.chars`. A context posted to, or read
  by, a desk may appear line by line in that desk's show report and its operator's AI summary: a node puts nothing in `recent` that the
  desk's readers may not see (P8).
- `commands` are the verbs a peer (or an AI) may propose, each `{"verb","risk","words"}`: its form in the notation below, its risk
  (`low`, `review`, `high` — high is never pre-ticked by any assistant) and what it does, in words. A sender lists its panic, air and
  transport commands first.
  - **The notation.** A literal word matches whatever its case. `A|B` is one word, either of those; `[…]` is optional and nests; a bare
    `…` is any further words. ` | ` outside brackets separates whole forms, and a form that does not start with the verb's first word
    carries on from the first form's leading literal words, all but the last. A `<…>` value is exactly one word, but for a form's last
    element and a value named `words`, `text` or `line`, or ending in ` words` or ` line`, each of which takes one word or more. A line's
    words are read as the line wires read them: a quoted phrase, with `\"` inside it, is one word, and a word that opens with `{` or `[`
    runs to the end of the line.
  - **A risk that depends on a word** is a row of its own: the form again with that word written in, and its own risk.
  - **Secrets.** A value whose whole name is `token`, `key`, `password`, `passcode` or `secret`, in any case, is a secret placeholder,
    and so is every word from the n-th on of a row that carries `"secret": n`. A line that fills one with anything but `‹secret›` is
    never sent or queued (§11).
- `peers` is one hop only: the peers this node hears, with their light and headline (never their peers), so a node
  that can reach only one other still sees the room. A row is `{"t","id","name","light","headline","heard","link","verified"}`:
  - `heard`: `live`, `stale` or `gone`, by §2's ages;
  - `link`: `ws` while a node link on this node's `/ws` proves the id (§7.1: `verified` `key` or `operator`), or the carrier of a link
    LINK verified to or from that node (13.1, 13.16.4); else empty;
  - `verified`: what proves the row's light and headline now — `key`, `operator` or `none` — or empty when only a hello named the node.
    A LINK verdict's `verified` (13.6.6) maps onto it: `key` and `link` are `key`, and `code`, the operator's credential (13.5.1), is
    `operator`.

  A proved link with no hello or context still has a row; a claim (§7.1), an id in conflict (§2) and a key that names no node add none.
  A node lists at most `context.peers` rows, those a hello or a context gives first. A sender may append fields to a row after these; a
  reader ignores a field it does not know.
- No secrets, no private content (P8): AGMessage never puts message text or documents in its context — only counts, the
  room's state and action prompts' titles.
- **Sizes.** A context is at most `context.bytes` as sent, counted in UTF-8 bytes, with at most `context.items` items, `context.links`
  links, `context.recent` recent lines, `context.commands` commands and `context.peers` peers. Its `headline` is at most
  `context.headline.chars` characters; an item's `id`, `kind`, `label` and `state` at most `context.item.id.chars`,
  `context.item.kind.chars`, `context.item.label.chars` and `context.item.state.chars`; a recent line `context.recent.chars`; a command's
  `verb` `context.verb.chars` and its `words` `context.words.chars`. A reader accepts at least these. It keeps a `verb` whole or drops it,
  and counts it, and keeps at least `context.words.read.chars` characters of `words`. A reader that keeps a node's commands keeps at
  least `context.commands` of them; a view of them — a prompt's brief, a drawn card — may show fewer, panic, air and transport first, and
  says how many it left out.
- **Trimming.** A sender over `context.bytes` leaves out, in this order and only until it fits: the oldest `recent` lines; grey links,
  then the other links; commands other than panic, air and transport, from the end of its list; grey items, then green items. Only
  while what is left is still over does it touch a red or amber item, or a panic, air or transport command: it cuts the `state` of each
  amber item, then of each red item, ending each cut with `…`; then it leaves out amber items, then red items, then panic, air and
  transport commands, each from the end of its list. It then adds one grey item that says what it left out:
  `{"id":"context:trimmed","kind":"health","label":"Context","light":"grey","state":"left out: 12 recent, 40 links, 30 commands, 5 items"}`.
- **Posting.** A node that gives its context to another (a desk) posts it on change and at least every `context.post.max.ms`
  while linked, never more often than once a `context.post.min.ms`. The node it posts to keeps the latest it took for each node id
  for `context.kept.ms` after it came, then drops it. It keeps contexts for at least `context.peers` node ids and at most `peers.max`,
  refusing one for an id past its own bound with words, and answers a context over `context.bytes` with `413` and its size. A post is a
  node's own act: no node lists it among its `commands`, and no assistant proposes it.
  - Over HTTP, and on a `ws` link as a side call (13.7): `POST /api/peers/context`.
  - On a `line` link: the line `CONTEXT POST <json>`. It is taken only on a connection LINK verified (13.6) with a key bound to one
    node — made for it or enrolled (13.5.2, 13.5.7) — of scope `post` or above (13.5.3), and only for the node that key speaks for:
    the context's `node.t` and `node.id` are the key's bound ones. A pairing code speaks for no one node (13.5.2), so a connection a
    code proved posts no context until it enrols a key. The whole line is within `link.command.bytes` (13.14), so a sender leaves out
    what it would leave out of an over-long context until the line fits. A `line` acceptor that takes posted contexts says so in its §8
    row; one that does not refuses the verb as it refuses any word it does not know.

```
→ @c7 CONTEXT POST {"v":1,"node":{"t":"messenger","id":"77aa77aa","name":"AGMessage"},…}
← OK @c7 kept
← ERR @c7 this key speaks for messenger 77aa77aa — not for messenger 5e6f7a8b
← ERR @c7 a pairing code speaks for no one node — posting a context needs a key made or enrolled for this node
← ERR @c7 this link's key is scope follow — Patterns FOH's operator can raise it on its Nodes page
← ERR @c7 posting a context needs PeerLink — this connection did not prove its id
← ERR @c7 not a context — {"v","node":{"t","id"}} is the least it carries
```

The first refusal answers a context that names a node other than the key's, and the second a connection a code proved: neither is
counted (13.5.6). The third answers a key below `post` (13.5.3); the fourth a connection that did not run LINK, level 0 included.

**What every node publishes, so any Eye can draw it.** P1–P8 hold for every context. Each node adopts them on its own and none waits
for another: a reader draws a node that has not as before — its own things, with no edge to another node — and a reader that predates
them leaves out a link whose end it cannot place.

1. **P1 — Items are the node's own.** A context's items are the node's own things: its parts, its links and its health. A thing of
   another node is never an item: a link end names it, or a `peers` row does. No item takes a peer's light, so a peer's fault never
   shows as the node's own. The one exception is Patterns' twin (13.18): a twin node names its desk in one item of kind `twin`,
   whose light and words are the twin link's own.
2. **P2 — Every PeerLink is one `link` item and one row.**
   - **The item** is of kind `link`, its id `link:` followed by a name of its choosing, the key's scope among its words ("scope
     post"). A node publishes one:
     - as connector, for each PeerLink it holds, with its 13.16.3 record's light and state words;
     - as acceptor, for each link it accepted that LINK verified (13.6), or, until that link's connector speaks LINK, that a key
       bound to the connector's node proves (13.5.2; on Stagewatch's `/ws`, `verified:"key"`), with its 13.16.5 row's light and
       words.

     Each of an acceptor's items stands on a credential its operator gave out. The links it took that neither proves — a level-0
     link a pairing code proved, a `keyless` one — it folds into one item of kind `link`, id `link:unproved`, whose words count and
     name them ("3 connections not proved: PlayMap Stage left, …"), with no row: an id nothing proved draws no edge from the node
     that took it.
   - **The row** runs from the connector's end to the acceptor's: a connector publishes
     `{"from":"<its link item>","to":"@<peer id>","kind":K}`, an acceptor `{"from":"@<peer id>","to":"<its link item>","kind":K}`.
     A link has this one row, whatever else it carries.
   - **K** is the connector's primary use of the link: `controls` when it runs the acceptor's show verbs (a playout link's clip
     end, a GO), else `follows` when it reads the acceptor's state or context, else `sends` when it only gives the acceptor its own
     context or proposals. An acceptor names K by the same order from what the connector has done on the link within its key's scope.
   - **Both ends.** A reader that holds both ends' items for one link draws one edge, in the worse of their lights, with the
     connector's K.
   - **A link not yet proved** that a connector holds — `legacy` (level 0) or `keyless` — names the id its hello or its answer gave,
     and its words say "not proved".

   This is the row to the peer node that 13.16.4 and 13.16.5 publish, with its shape fixed.
3. **P3 — What a node shows from another node is its claim.** When a thing a node shows comes from another node — a window it
   draws, a timer or a clip's time left it shows, a page it mounts — it publishes
   `{"from":"@<node id>/<item id>","to":"<its own item>","kind":"feeds"}`.
4. **P4 — Grants.** A node that grants a thing of its own to another node's app (a window, §12) publishes one row per holder,
   `{"from":"<its item>","to":"@<holder id>","kind":"serves"}`: green while the holder reports it shown, amber while it opens, grey
   once withdrawn.
5. **P5 — Show liveness.** A node that knows its show writer is stuck (13.1, 13.10.5) makes each of its counting items — kinds
   `timer`, `schedule`, `playlist`, `chase`, `timecode` and `clock` — red, its state words saying the show is stuck in place of a
   time. A reader takes no counting value from it as current (13.10.6).
6. **P6 — Kinds.** A node uses the kind table's words where one fits; a reader places any other word as the table's last row says.
7. **P7 — Order.** A node lists its red and amber items first, then the rest in an order of its own, with every item that has a row
   to another node (P2–P4) among its first `context.items.first` as far as its reds and ambers leave room: what an Eye draws of a
   node before it is opened then holds its problems and its chain ends.
8. **P8 — Content.** No secret (§1 rule 5, 13.19): no key, code, token, PIN, view pass or other capability URL. No private content,
   nothing a node keeps for its people alone:
   - what one person writes for another — a message's text, a speaker's message and an audience question among them, whatever
     screen shows it;
   - documents and their file names, who is in a conversation, room and hall codes.

   The show's own words are not private: a running order's titles and the people they name, a cue's words ("LX 12"), and who ran a
   verb, in `recent` ("done by Sam"). A node may keep more out than this.

**The kind table.** The words P6 asks for, and where an Eye places an item of each kind — the plane its lenses show it on, and its
column in its node's cluster. The words bind; the placement is advice to an Eye.

| kind | plane | column |
|---|---|---|
| `link`, `deck`, `client`, `desk`, `node`, `twin`, `assistant`, `ai`, `approvals`, `health`, `machine`, `preflight`, `fault`, `clock`, `sync`, `mirror`, `fleet`, `decode`, `optimise`, `scan`, `calibration`, `align`, `site`, `survey` | control | 0 — links and health |
| `timer`, `schedule`, `department`, `cue`, `message`, `stack`, `playlist`, `playlists`, `chase`, `timecode`, `show` | control | 1 — the show |
| `air`, `media`, `content`, `input`, `videoio` | video | 1 |
| `screen` (Stagewatch's windows, AGMessage's displays), `output`, `outputs`, `send`, `stage` | video | 2 — screens |
| `projector`, `surface`, `led` | video | 3 — far ends |
| `room`, `meetings`, `prompt`, `help`, `phone`, `audience` | room | 1 |
| `audio` | audio | 1 |
| anything else | control | 1 |

## 7. Keys and pairing

App links prove keys by 13.6, and a PIN, a viewing code or a view pass is never a PeerLink credential (13.5.1); a node link on
Stagewatch's `/ws` that the PIN or a control key proves (§7.1) is level 0, a door that is not a PeerLink (13.18). **One node, one
key**: a route that takes something for a node — a node link (§7.1), a context, a proposal's `from`, a collect, a result — takes it
only with the key bound to that node's id (13.5.2), or, while no key is bound to it, with the operator's credential. A key binds to the
id it first speaks for, or to the one its operator names, and is refused for any other — "this key speaks for <kind> <id>" — and that
refusal is never counted. Each node's §8 row maps its keys onto 13.5.3's scopes.

- **Stagewatch**: scoped keys (`swk_…`, 13.5.1's shape) in `Authorization: Bearer`, hashed at rest, rate-limited. Scopes: `read`,
  `control`, `mcp`, and **`node`** (1.1): read, put a message on the stage, and post its own context, proposals and their results — what
  a show app needs, nothing more. Keys are accepted wherever the PIN is; a key never counts as a wrong PIN, and an unknown or revoked key
  counts toward no lockout.
- **Patterns** and **PlayMap**: the pairing token is never sent in clear on their line wires from 1.1: `AUTH` becomes a challenge — the
  server sends `NONCE <32 hex>` on connect, the client answers `AUTH-PROOF <hex>` = HMAC-SHA256(token, nonce + client name), the token
  keyed as 13.6.8 says. Plain `AUTH <token>` is taken only from this machine. Patterns' HTTP keeps `X-Patterns-Token` (local network,
  behind the bind address; 13.18).
- **AGMessage**: holds the Stagewatch key encrypted at rest (Windows DPAPI where available, else AES with a machine key file outside
  `data/`), sends it in a header — never `?pin=` in a WebSocket address — and asks for a `node`-scope key.

### 7.1 A node on another node's `/ws`

A show app that follows a Stagewatch opens a **node link** on its `/ws`: it names itself in the address and carries its credential in
the `Authorization` header, never in the address.

```
GET /ws?node=<its id>&t=<its kind>[&detail=1] HTTP/1.1
Upgrade: websocket
Authorization: Bearer swk_…        a node key; a control key or the PIN also proves, at level 0; none makes the socket a claim
```

- `node` is a §2 id and `t` a §2 kind. With `node`, the browser's `cid` and `page` are ignored.
- **Refused before the upgrade**, with HTTP 400 and words, and never counted toward a lockout:

| request | error |
|---|---|
| `node` with `win`, or `Authorization: Window …` | a window's link is not a node link |
| `node` with `pin` in the address | a node link carries its key in the Authorization header, never in the URL |
| `node` with `role=control` | a node link reads; it does not control |
| a bad `node` | a node id is 1-40 letters, digits, - or _ |
| a bad or missing `t` | a node link names its kind (t=patterns, playmap, messenger…) |
| the node's own id | that's this Stagewatch |

- **What the socket is.** The credential is checked first, then the viewing code, then:

| credential | binding | the socket is | `verified` |
|---|---|---|---|
| a node key | bound to this id, or bound to none while the id is free (it binds now) | a node link | `key` |
| a node key | bound to another id, or the id bound to another key | a node link | `none`, with words |
| a control key or the PIN | the id bound to no key | a node link | `operator` |
| a control key or the PIN | the id bound to a key | a node link | `none`, with words |
| a read or MCP key, or none | — | a claim | `none` |
| a refused key (unknown, revoked, locked out, over its rate) | — | a claim | — |

  A node link is never listed as a screen and never hears a screen command (`redirect`, `identify`, `reload`, `disconnect`). A claim
  stays among the screens, flagged with what it claims ("patterns 9f8e7d6c"), never reached by `all`, only by its own screen id or a name
  its operator gave it. A binding refused keeps the frames: the binding governs contexts and proposals, not reading. A node key on a
  socket that names no node is a node link listed under its key, with no id asserted. Every node link here is level 0 (13.6.8) until
  its connector runs LINK (13.6.5); one the operator's credential proves is `verified` `operator`, and is never a PeerLink, which no PIN
  proves (13.5.1).
- **The verdict, first.** Before any frame a node link hears what the node made of it, and again — latest wins — whenever that changes:
  a key revoked or rebound, the PIN changed.

```
{"type":"auth","ok":true,"scope":"node","node":{"t":"patterns","id":"9f8e7d6c","verified":"key"}}
{"type":"auth","ok":true,"scope":"node","node":{"t":"patterns","id":"5e6f7a8b","verified":"none"},"words":"this key speaks for patterns 9f8e7d6c — make a key for patterns 5e6f7a8b"}
{"type":"auth","ok":false,"error":"unknown or revoked API key","retry":60}
```

  `scope` is `node`, `control`, `read`, `mcp`, `pin` or `none`; `verified` is `key`, `operator` or `none`, with `words` when it is
  `none`. A refused credential is told once, with `retry` in seconds, and the socket stays open with the public frame: no socket closes
  for a key's sake, so an app that redials never loops. When the viewing code refuses the socket — its credential does not open the
  code, and its address does not carry it — that one message carries `"view":true` and the socket closes, as a screen's does. 13.6.5
  appends LINK's fields to the verdict.

### 7.2 Reading a node that asks for a token

- **Line wires** (Patterns 9697, PlayMap 9941 and its `/ws` line channel). On a node with a token, a connection the node does not trust
  — not from this machine where it trusts loopback, not from a trusted address — hears `NONCE <32 hex>` as its first line and nothing
  of the show until it pairs. `STATE` (the greeting and the pushes) and the answers to `STATE`, `STATUS`, `CONTEXT`, `EYE` and every
  subscription begin after `OK paired`, and stop again when the node's token changes. Before pairing a connection may send what finding
  and pairing need — `HELLO`, `NONCE`, `AUTH-PROOF`, `AUTH` from this machine, `LINK` and `LINK PROOF` (13.6.4), `PING`, `HELP` and
  `VERBS` — and the queries the node's §8 row names as still open; anything else is answered
  `ERR [@id] not paired — this node shows its state only to a paired connection`. An open node (no token) greets with `STATE` at once.

```
← NONCE 3f9a0c…(32 hex)                    the first line, and nothing else of the show
→ HELLO Stagewatch
← OK
→ @s1 STATE
← ERR @s1 not paired — this node shows its state only to a paired connection
→ @pa AUTH-PROOF <64 hex>                  HMAC-SHA256(token, nonce + "Stagewatch")
← OK @pa paired
← STATE {…}                                 the greeting, now; then on every change
```

- **HTTP and WebSockets**: every read beyond the public view wants a key, the token or the PIN (§7), as in 1.2, or a verified link's
  session credential (13.7).
- **A reader pairs before it reads.** It answers `NONCE` with `AUTH-PROOF`, or runs LINK (13.6); it sends its reads only after
  `OK paired` or `OK open`, and takes `STATE` whenever it comes. With no token for a node that sent `NONCE`, it shows that link amber —
  "not paired — <node> shows its state only to a paired link; its pairing token goes in <where>" — and sends nothing but `PING`.

## 8. What each node publishes and consumes

Each node's row says what it publishes and consumes and its **public view** — what any screen in the room may show, open by design
(§1 rule 4); how its keys map onto 13.5.3's scopes; its show writer (13.1); its doors that are not PeerLinks (13.18), each with what it
leaves open; its **named exemptions** from §1's rules, each with what ends it; and what it does not keep yet of this revision, each with
what ends it. A node's own remote documentation lists its verbs by scope.

| node (`t`) | publishes | consumes | public view |
|---|---|---|---|
| **Stagewatch** (`main`) | hello; `/ws` frames (`v`, `seq`: public, public detail, operator); receipts; `/api/context`; pong with times; the approval desk (§11); windows (§12); `/tls` | Patterns' and PlayMap's STATE, context and receipts over its playout links; every node's posted context; proposals and their results; Slides agents' feeds; verified peers' `recent` → its show report | the public frame on `/ws` and `/api/state`, `detail=1`'s public detail, the viewer, role and audience pages, `/api/holding/<id>`, `/tls`; a window's page to whoever holds its address (§12) |
| **Patterns** (`patterns`) | hello; wire STATE (`rev`); receipts `@id`; `CONTEXT` and `/api/context` (the Eye's headline, problems, programme); its context and proposals to a desk; the results of the approved lines it collected | Stagewatch's frames and context (its stage timer page follows Stagewatch when one is linked; the Eye draws it); its twin's | its pages, the EDID files, the audience port |
| **PlayMap** (`playmap`) | hello (with `sync` and `build`); its line wire on TCP 9941 and its `/ws` line channel, in Patterns' grammar, with receipts; `/api/context` (its token), `/mcp`; its context to a desk; the results of the approved lines it collected | Stagewatch's `/ws` frames; Patterns' STATE and context; other PlayMap nodes (sync anchors, media) | `/health`, `/fleet`, `/tls` |
| **AGMessage** (`messenger`) | hello; `/api/context` (the room, counts, action-prompt titles — no content); its context and proposals to a desk; the results of the approved lines it collected | Stagewatch's frames (the timer, a message's acknowledgement by its id, VT), receipts and context | `/api/health` (`ok`, version, uptime), `/tls` |
| **Slides agent** (`agent`) | hello (with `presenting`); its slide feed (its own `seq` and version) | — | — |

### 8.1 Stagewatch (`main`)

- **Scopes**: `follow` is a `read` key; `post` a `node` key, which also puts a message on the stage; `operate` a `control` key or the
  PIN. No key of its is `drive`: it lists no verbs for a playout desk. An `mcp` key reads and reaches the MCP endpoint, whose changes may
  wait for a person; it is no app link's credential. Its `docs/REMOTE.md` gives each route's scope.
- **Show writer**: its show thread. `stuckMs` 3000, its shipped stall mark, not yet set from a measured stall; it says its show is stuck
  on `/ws` with `{"type":"beat","live":false}` (13.10.5). It offers neither `stuck` nor `expire` as a feature: no measurement yet.
- **Doors that are not PeerLinks**:
  - OSC (off by default; once on, on every interface unless set to this PC): it takes show verbs with no PIN or key, so it leaves the
    show to anything that reaches its port; and its feedback, once set, sends the show's state to the host it names, unauthenticated
    and in clear;
  - Companion TCP (9923; this PC by default): no authentication, so bound to the network it leaves the show to anything that reaches
    the port;
  - browser pages and `?pin=`: on plain HTTP a PIN or a key a page sends, or an address carries, crosses the network in clear — on
    every request with Require HTTPS off (the default), and with it on in an `http://` address, which its redirect echoes (below);
  - scripts and `/mcp` clients (13.6.9's bearer keys): on plain HTTP a key they send in `Authorization: Bearer` crosses the network in
    clear;
  - node links (§7.1) the PIN or a control key proves: the operator's credential as a bearer, level 0 and never a PeerLink, in clear
    on plain HTTP;
  - window links (§12): on plain HTTP a window's address crosses the network in clear, in the browser's address bar;
  - Slides agents: a feed carries the PIN or a control key in a header, in clear on plain HTTP.
- **Named exemptions**:
  - `?pin=`: a page or a script may carry the PIN or a key in its address (bookmarks, scripts), which §1 rule 5 keeps out of every
    other address — not scheduled to end. Require HTTPS refuses a plain WebSocket from another machine and redirects a plain page to
    its `https://` address, but an `http://` address that carries the PIN or a key has crossed the network in clear by then, and the
    redirect's `Location` carries it back once more, in clear;
  - Companion TCP (9923) takes commands with no authentication when its operator binds it to the network, which §1 rule 4 leaves to an
    authenticated link — its authentication comes with 1.4's default flip.
- **Not kept yet**, by what ends it:
  - its Patterns link: `link.stale.ms` (today PatternsLink marks no link stale: a clip shows no time 3 s after its last frame, and the
    link drops after 12 s); `link.redial.first.ms` (today PatternsLink waits 250 ms before its first redial, and never jitters); 13.11's
    clock quality (today CLOCKS APART reads the offset alone, with no error, freshness or fitness); its Patterns link proves at level 0
    only (today it answers NONCE with AUTH-PROOF and runs no LINK); no playout link to PlayMap (today its Patterns link is its only line
    connector) — ends when its playout links run on the PeerLink core, and speak LINK to Patterns and PlayMap where they offer it;
  - its `/ws` speaks no LINK (today a node link proves a bearer key or the operator's credential, §7.1); 13.6.9's mark on a key seen in
    clear (today it takes a key sent in clear from another machine as it takes any other, and marks nothing); `receipt.final.ms` (today
    its receipts carry no `final`, `repeat`, `lateMs` or `frame`) — ends when its `/ws` speaks LINK, with features `final` and
    `receipts`;
  - as a reader of contexts: `context.items` (today PeerContexts keeps a context's first 64 items); `context.commands` (today
    PeerContexts keeps a context's first 64 commands); `context.verb.chars` (today PeerContexts cuts a verb at 80 characters, where a
    reader keeps a verb whole or drops it); `context.links` (today PeerContexts reads no links); `context.link.end.chars` (today
    PeerContexts reads no links); `context.words.read.chars` (today PeerContexts reads no command words); its risk by whole forms (today
    it rates a line by the longest run of a listed verb's leading words) — ends with one owner for every reader's numbers, its verb
    table;
  - as the desk: `proposal.line.chars` (today ProposalDesk takes a line of 400 characters and cuts a longer one, which can run another
    command); `proposal.collect.ms` (today the desk sends and approves for a target whatever its last collect); its proposal ids (today
    they restart with each desk run, `p<n>`, so a target pairs an id with `created`); a `from` of kind `main` or `mcp` (today the desk
    takes one from the network unless it names the desk's own id); a proposal sent with an `mcp` key (today the desk queues one for a
    node no key is bound to, proved by nothing); §11's line refusals (today the desk takes a line that holds a key, a window's address
    or a filled secret placeholder, and drops its control characters); a target's raise of its own proposal's risk (today the desk
    takes a proposal's risk from the target's table alone); its commands rows in §6's notation (today it lists `start / pause` and
    `patterns end 10`) — ends with the desk's rules; a line a person sends to another app (today it has no `peer` or `desk` verb) —
    ends with the desk's verbs; verified peers' `recent` in its report (today its report holds its own journal alone) — ends with one
    report;
  - an over-long context's `413` (today a context over `context.bytes` closes its connection with no answer) — ends with `413`, and
    its size, for an over-long body;
  - `window.grants` (today it grants no window, so no grant's address travels) — ends with grants (§12);
  - 12.2's page rule for a page opened with a viewing code or a crew link (today it serves those pages without the window page's
    no-referrer, noindex and no-framing headers); 12.2's revocation at once for a viewing code or a crew link (today a screen already
    open on a changed one keeps its link until it reconnects) — not scheduled;
  - of §6's rules for the Eye: P2 for its links (today its only rows run from its timer, its Patterns link is an item of kind
    `playout`, and no row names a peer); P2 for the node links it accepts on `/ws` (today it lists them only as `peers` rows, with
    `link` and `verified`); P3 for the clip time it shows from Patterns (today its screens show Patterns' clip time, and no row names
    Patterns' item); P5 for its schedule (today a stuck show thread turns its timer red and leaves its schedule's light as the frame
    gives it); P6 for its vMix link (today an item of kind `playout`); P7 for its order (today its timer is its first item whatever its
    light); and P8 for its messages (today a cue item carries the text of a message it sent, a speaker message sent with an ack among
    them, and `recent` carries the text of every message and audience question its journal holds); `context.items.first` (today its
    timer is its first item whatever its light, ahead of a red or amber one) — ends when it publishes by P1–P8.

### 8.2 Patterns (`patterns`)

- **Scopes**: its pairing token is `operate`. Its app keys (`ptk_`) take `follow` (STATE, CONTEXT, SUBSCRIBE, EYE), `post` (that, and
  CONTEXT POST and its own proposals and results), `drive` (that, and VIDEO transport, CUE GO and STANDBY, PLAYLIST transport and STAGE
  MESSAGE) and `operate` (everything); no OUTPUTS, TAKE or CUT, SCREEN or setting is reached below `operate`. Its `docs/REMOTE.md` lists
  the verbs.
- **Show writer**: its desk thread. `stuck` not offered: no measurement yet; nor `expire`.
- **Doors that are not PeerLinks**:
  - OSC: it takes verbs from the addresses on its OSC ALLOW list and from this machine, with no proof; a proof on the wire grants an
    address nothing;
  - Companion decks on its wire: the 1.1 proof, never plain `AUTH` off this machine; on a plain wire a recorded proof is an offline
    guessing target for the token;
  - its HTTP's `X-Patterns-Token` is the token itself: on plain HTTP from another machine it crosses in clear, and Patterns marks it seen
    in clear;
  - its twin and node relays (13.18): plain between the two desks, with device secrets — a view pass it keeps among them — only under
    their own switch, off by default and refused on a plain link to another machine.
- **Named exemptions**: once its wire shows its state only to a paired connection (below), `NAV`, `MENU`, `CUE LIST`, `ARCADE …` and
  `ASSISTANT …` stay open before pairing (§7.2), which §1 rule 4 leaves to an authenticated link — until the desk's own cross-machine
  asks pair by proof.
- **Not kept yet**, by what ends it:
  - its wire greets every connection with `STATE` before `NONCE` and answers its queries before pairing (§7.2) — ends when its wire shows
    its state only to a paired connection (its ADR-016);
  - its Stagewatch link names no node on `/ws` (§7.1) — ends when it dials as a node and reads its verdict;
  - it keeps a peer context's first 64 commands (§6) — ends when it keeps `context.commands`, each verb and its words whole;
  - `proto` 1; its hello reader keeps one address per id, marks no id in conflict and holds every id it hears; certificates of 1,826
    days (§2, §10) — ends with its hello 1.3 and certificates within `cert.days`;
  - LINK, TLS on its wire (no `STARTTLS` is offered either), app keys, `final` and `receipts` (§13) — ends with its app links;
  - CONTEXT POST (§6) — ends when another app gives it its context over a verified PeerLink.

### 8.3 PlayMap (`playmap`)

- **Scopes**: its pairing token is `operate`. Its app keys (`pmk_`) take `follow`, `post`, `drive` and `operate`, mapped onto its
  ShowPolicy roles; its own wire documentation lists the verbs.
- **Show writer**: its engine's control lane. `stuck` not offered: no measurement yet; nor `expire`.
- **Doors that are not PeerLinks**:
  - its sync group's clock exchange (9942): a follower takes the clock, the transport and the air of the leader it names, as heard on
    the show network, with no PeerLink proof;
  - its fleet traffic: pulls proved by its pairing token's HMAC, never the token (its `docs/FLEET.md`);
  - its desk↔engine link on one machine, at level 0 on loopback;
  - Companion decks on its line wire and its `/ws` line channel: the 1.1 proof, never plain `AUTH` off this machine;
  - Lines devices on trusted addresses pair with no proof — an address, not an identity.
- **Posted contexts**: it takes none on its wire: `CONTEXT POST` is a word it does not know (§6).
- **Named exemptions**: TCP 9941 sends `STATE` on `HELLO` and answers `STATE` and `CONTEXT` before pairing (§7.2), which §1 rule 4 leaves
  to an authenticated link — its owner's decision 17; it ends after Patterns' Round 96, once Patterns' node devices pair by proof, when
  9941's `STATE` and `CONTEXT` wait for pairing on a node with a token.
- **Not kept yet**, by what ends it:
  - `proto` 1, and its hello reader's limits (§2) — ends with its 1.3 copy;
  - certificates of 826 days, a day back and 825 on (§10) — not scheduled;
  - it posts its context every 5 s whether or not it changed, not on change (§6) — not scheduled;
  - its posted context is not trimmed to `context.bytes` (§6) — ends when it trims at the poster, in §6's order;
  - LINK on 9941 and its `/ws` line channel, app keys, `final` and `receipts` (§13) — ends with its PeerLink acceptor;
  - its links to Stagewatch and Patterns prove at level 0 — ends when they speak LINK.

### 8.4 AGMessage (`messenger`)

- **Scopes**: no node link reaches it with a key in 1.3 (13.3.1), so it maps none. It holds a Stagewatch `node` key (`post`).
- **Show writer**: its event loop. It accepts no node link, so it names no `stuckMs` and offers no feature.
- **Doors that are not PeerLinks**: its admin page and its board on its own web port, which keep its own rules (its `docs/NODES.md`).
- **Named exemptions**: none.
- **Not kept yet**, by what ends it:
  - it drops a hello of a kind it does not know (§2), and reads a proposal's `ok` only from an object `result` (§11) — ends with its
    0.5.1 reader;
  - `proto` 1 and no `inst` in its hello, and its hello reader's limits (§2): 20 caps, names cut at 60 characters, up to 256 peers, one
    address per id, a peer forgotten once gone, and no id marked in conflict — ends with its 0.5.1 hello and reader;
  - its certificate spans `cert.days` and an hour more (§10) — ends with its 0.5.1 certificate;
  - its links to Patterns and PlayMap carry their operator token, which reaches them only over TLS to a pinned certificate or on this
    machine, and no command (§7) — ends when it holds an app key of its own;
  - LINK (§13) — ends with its 0.6.0.

### 8.5 Slides agent (`agent`)

- **Scopes**: none; its feed carries a Stagewatch's PIN or a control key (§8.1's doors).
- **Show writer**: none it shares.
- **Doors that are not PeerLinks**, **named exemptions** and **not kept yet**: none.

## 9. With and without AI

| Works with no internet and no AI | Needs the internet |
|---|---|
| Discovery, links, commands, receipts, state, clock offset, liveness | The language model: copilot plans, the assistant's cloud advice, drafting messages and recaps, ASK in the Eye |
| Every node's context, lights and headline; the Eye drawing every node and device | AGMessage's Cloudflare tunnel (people off-site) |
| Rule-based advice (Stagewatch's advisor, Patterns' Super Check), problems queues, triggers | Time from pool.ntp.org (the LAN clock offset still works) |
| Cross-app automation: clip end → next item, ALL GO → lights, action prompt → producer | |

With AI available, each app's assistant gets its own context **plus its peers' contexts** in its brief, under the same
rule as the Eye: the facts are evidence, unknown stays unknown, verbs are proposed and a human approves.

## 10. Encrypted connections (1.2)

Show data and keys cross the local network; from 1.2 every web endpoint can be reached over TLS.

- **One certificate per install.** On first run each node makes a self-signed certificate (ECDSA P-256, SHA-256) whose whole span —
  NotBefore to NotAfter, any backdating for a skewed clock included — is at most `cert.days` (Apple devices refuse a TLS server
  certificate valid for longer, even one the user chose to trust): its NotBefore at most `cert.backdate.hours` before it was made, and
  its NotAfter counted from its NotBefore. At a start a node makes a new one when fewer than `cert.renew.days` remain, or when the one it
  has spans more than `cert.days` — a new fingerprint, so every peer that pinned it sees CERTIFICATE CHANGED once (13.8.3). Its subject
  names the node; its SANs are the host name, `localhost`, 127.0.0.1 and the machine's LAN addresses at the time. Its private key is
  protected at rest (Windows DPAPI where available) and never copied between machines.
- **Its fingerprint is public.** The hello carries `"tls":"sha256:<64 hex>"` (SHA-256 of the certificate's DER) and
  `ports.https`. Stagewatch serves HTTP and HTTPS **on the same port** (9924: a connection whose first byte is a TLS
  handshake is TLS); Patterns and AGMessage may use the same trick or a second port, as `ports.https` says.
- **App-to-app: pinned, not trusted by name.** A client that connects to another node over TLS checks the server's certificate
  fingerprint, never its name or a certificate authority. A fingerprint is pinned per node id only after a proof verifies over it, or
  when a person confirms it (13.8.2), and a later change is refused with CERTIFICATE CHANGED until the operator accepts it (13.8.3), as
  SSH does. The hello is unauthenticated, so the pin — not the hello — is the trust (13.4.2).
- **Browsers.** A self-signed certificate shows a browser warning. Each node serves `GET /tls` (a page with the
  fingerprint, how to trust it on Windows, macOS, iOS and Android) and `GET /tls.cer` (the certificate, DER), so a show
  laptop's screens can trust it once. Plain HTTP keeps working unless the operator turns on **Require HTTPS**, which
  refuses plain HTTP and plain WebSockets from anything but this machine (loopback).
- **Line protocols.** A line wire serves TLS on the port it already uses, told from plain by its first byte, as HTTP is (13.8.5); 1.3
  has no `STARTTLS`. Pairing by proof (§7, 13.6) keeps tokens and keys off the wire; TLS adds privacy for show data.

## 11. The shared approval queue (1.2)

Every app's AI proposes; a human approves — in **one place**. When a node with the `approvals` cap is on the bus (a
Stagewatch: the show caller's desk), the other apps send their AI's proposals there instead of keeping their own queues.
With no desk on the bus, each app approves its own (as in 1.1).

- **A proposal** names who proposes and which node is to act, in that node's own grammar:
  `POST /api/proposals` (a `node` or `control` key, or the PIN)
  `{"from":{"t":"patterns","id":"9f8e7d6c","name":"Patterns FOH"},"target":{"t":"patterns","id":"9f8e7d6c"},"line":"EYE FOCUS screen 2",
  "reason":"Projector 1 is on the wrong mode","risk":"review"}` → `201 {"ok":true,"id":"p17","state":"pending","risk":"review"}`.
  Target `{"t":"main"}` (or the desk's own id) means the desk itself. Its `from` is spoken for as §7 says: a node key speaks only for
  the id it is bound to. A `from` of kind `main` or `mcp`, or with the desk's own id, is the desk's own, and is refused from the
  network: an AI on the desk's own MCP endpoint proposes through `/mcp`, as the desk's own, and an `mcp` key posts no proposal here.
- **The line** is at most `proposal.line.chars` characters, refused — never cut — when longer, and refused when it holds a control
  character, a key (13.5.1's shape), a window's address, or a secret placeholder (§6) filled with anything but `‹secret›`. A proposer
  that keeps a secret itself sends `‹secret›` in its place and runs its own record.
- **Risk is the target's call.** The desk rates a line by the most specific whole form it matches in the target's verified table — the
  commands of a context posted with the key bound to the target's id, or read by the desk over its own link (§6): most literal words
  wins, an exact tie goes higher, a line carried in a `line` value raises the risk to that line's, and a line no form matches is
  `high`. A table that no proof stands behind rates every line `high`. A target may raise the risk of its own proposal, never lower it.
  The desk's own verbs use its own rules; anything unknown is `high` — never pre-ticked, always shown in red.
- **States:** `pending` → `approved` / `rejected` (by whom, when) → for an approved one: `done` / `failed` (with the target's words) or
  `undelivered` — not collected within `proposal.undelivered.ms` of its approval, or collected with no result within
  `proposal.result.ms`.
- **Ids** are opaque, and unique across a desk's runs: `p<n>-<run>`, the run 4 random base-32 characters drawn at each start; `p<n>`
  alone resolves within the current run.
- **The proposal** reads
  `{"id","from":{"t","id","name"},"target":{"t","id","name"},"line","reason","risk","state","created","decided","decidedBy","result","ok","words"}`:
  `created` and `decided` in Unix ms, `ok` `true` once done, `false` once failed or undelivered and `null` before, `words` the
  target's words (`result` the same, as 1.2 sent it); a desk that records collects appends `collected` (Unix ms, or `null`). A desk
  may append more fields after these, to a proposal and to its `201` answer; a reader ignores a field it does not know.
  `GET /api/proposals/<id>` reads one; `GET /api/proposals?from=<node id>` the proposer's own; `GET /api/proposals` the whole queue.
- **Delivery is by the target, pulled:** each node polls `GET /api/proposals?target=<its id>&state=approved` with a credential that
  speaks for its id (§7), which claims what it collects; it runs each line through its own command path — its own checks and fences
  still apply — and reports `POST /api/proposals/<id>/result {"ok":true,"words":"…"}`. The desk records each target's last collect, and
  neither sends nor approves a line for a target that has not asked within `proposal.collect.ms`; it says why. A result is a report owed
  (13.12.9): retried until the desk takes it, and answered `409` once the proposal is settled, which counts as delivered. A target that
  lost a collect's answer finds its claimed, approved proposals in `GET /api/proposals`, and runs only those it never ran, keeping what
  it ran across restarts. The desk never holds another app's credentials. The desk runs proposals aimed at itself directly, with a
  receipt.
- **A person's line.** A line a person sends to another app from the desk (`peer <app> <line>`) must match a form of that app's verified
  table, under the line rules above; it is born approved, `decidedBy` a role word (`operator`, `deck`, `wire` or `trigger`).
- **Everything is journalled** on the desk (asked, approved or rejected by whom, the result) and each target journals
  what it ran, so the report shows what each AI suggested, who decided and what happened.
- **Offline:** the queue is plain LAN traffic; it works with no internet (proposals made by a person's own rules, or
  by an AI earlier, still queue and run).

## 12. Windows (1.3)

A **window** is a browser that a node controls through an address of its own. 12.1–12.3 say what a window is, how its address is kept
and the link it holds to its node. 12.4–12.10 say how a node grants a window to another node's app — one that shows the window's page
in a browser of its own, or draws the window's content itself — and how that app holds it. A node grants windows only with cap
`windows`, and an app holds one only with cap `pages` or `window-draw` (14.6).

### 12.1 What a window is

- A window's address is `/w/<token>` on its node's web port: `window.token.chars` symbols of `window.token.alphabet`, each five random
  bits from the platform's CSPRNG. A node keeps at most `window.max` windows.
- The address is the window's identity and its pass: whoever opens it sees what the node puts on that window, and controls nothing.
  Identity lives in the node, not in the browser: two browser sources of one program are two windows, and a browser that clears its
  storage is still the same window.
- A window shows its `content`: a page of its node that needs no credential, `holding` (its holding page) or `black`. A page that needs
  the PIN, and another site, are never a window's content. The window draws its holding page and black itself, so they show with no
  page and no network.
- When its link goes stale (§4) or drops, a window shows its own loss state, its `onLoss`: `hold`, the holding page, or `warn`, dimmed
  with NOT CONNECTED. Never a confident stale time.
- Windows stay with the machine, never in a show file.

### 12.2 View passes

A window's address — like a viewing code and a crew link — is a **view pass**: a pass a browser can be given only as an address. A
node that issues one:

- makes it view-only — a crew link also answers its own department's cues and reads and writes its department's notes — and
  revocable at once: `window revoke` gives the window a new address, the old one stops, and a screen on it says so;
- serves every page opened with one so that nothing caches or indexes it, no referrer leaves it and no other site frames it;
- counts wrong view passes per device in a count of their own, apart from wrong PINs and keys: `view.wrong.per.minute` in a minute
  lock that device out of view passes for the minute, and a right PIN or key never clears that count. A locked device is never
  told its address is gone: a page it asks for says to wait, and a window's link is closed and tries again. An unknown or revoked
  key counts toward no lockout;
- shows a window's address only to its own operator (the PIN or a control key; never a read or node key), to hand out.

A view pass is never a PeerLink credential (13.5.1). Where else it may ride is §1 rule 5's, and 12.5's for a grant.

### 12.3 The window link

- A window holds one WebSocket to its node: `/ws?win=<token>&detail=1`. A page can set no header on a WebSocket, and its own address
  already holds the token. `win` with `node` is refused before the upgrade: a window's link is not a node link (§7.1).
- Before any frame, and again on each change, it hears what to show:
  `{"type":"window","id","name","content","hold","holdText","holdImage","onLoss","fadeMs","rev"}`, where `rev` rises with every change
  the window must act on. Then public detail frames and beats (§4). It sends only what any public page may.
- When its operator asks: `{"type":"identify","name","id"}` (its name on the screen for a few seconds) and `{"type":"reload"}`.
- When its address stops working — revoked or deleted — `{"type":"window","revoked":true}`: the window says so in words and stops
  trying. A page grant's address ends black instead (12.7).
- After each swap, hold or failure it reports `{"type":"shown","rev","content","hold","ms","error"}`.
  - Its node shows a window green only when it reports the revision it was sent;
  - amber while a change is sent and not shown, when it could not show a page, or when its link is lost;
  - grey until it is opened.

  A window's light never turns its node red.

### 12.4 Grants

- A node with cap `windows` MAY grant a window to another node's app: `window grant <window> <app>`, the app named by its id (§2) or by
  a name no other app on the show network has. `window withdraw <window|all> [<app>]` ends grants. A grant is one window to one app,
  and a window has at most `window.grants`.
- **A grant is a person's act.** The node takes one only from a door that authenticates its operator: its desk, the PIN, a control key.
  A door that authenticates nobody refuses it with the reason, or queues it as a proposal (§11) for a person to decide: OSC, and
  Companion TCP (9923), which authenticates nobody in 1.3, loopback included (13.18). An assistant or a peer only proposes one
  (§11), and a person decides.
- **An app asks only for itself**, with the §11 proposal `window grant <window> <its own id>`. A proposal under an app's key that names
  another app is refused: "a show app may ask a window only for itself".
- **How the app holds it**, `how`, fixed when the grant is made:
  - `page`: the app opens the window's page in a browser of its own (cap `pages`);
  - `draw`: the app draws the window's content itself (cap `window-draw`) and holds no address.

  It is read from what the app's link offers (12.6's `can=`) when the app is linked, else from its hello's caps, and then worded as a
  claim: "it says it shows pages — checked when it links". An app that offers neither is refused: "<app> can't show a window: it offers
  neither pages nor window-draw".
- **A page grant has an address of its own**, `/w/<grant token>`, made as 12.1 makes a window's. Withdrawing one app stops only that
  app's address, and the node knows which link is that app's by the address it came in on. A draw grant has no address.
- `window revoke` gives the window and each of its page grants a new address. Deleting a window ends its grants.
- **Grants outlive the link.** The granting node keeps them with the machine, never in a show file. An app drops a grant only when a
  `grants` message (12.6) leaves it out, or a `window-end` ends it: a dropped link, or a restart of either node, drops nothing.
- A standby is a node of its own, with its own id: it needs a grant of its own, and no grant's address rides a twin's link.

### 12.5 Where a grant's address travels

A page grant's address, its `path`, is sent only on a link that is all of these:

- **the grant's own node's**, proved by a key bound to that node (13.5.2): verified by LINK (13.6), or, until that node speaks LINK, a
  node link its key proves (§7.1, `verified:"key"`) — never a link the operator's PIN or a control key proves;
- **pinned TLS, or local**: TLS to the certificate the app pinned — on a verified link the proofs bind it (13.6.1), and a level-0
  key is sent only inside it (13.6.8) — or local (13.1): a loopback address, never one the node owns, so an app on the same machine
  dials loopback (13.15.4) or TLS;
- **on a key never seen in clear** (13.6.9). A key seen in clear gets no grant at all — no address and no draw assignment — until its
  node is made a new key;
- **offering `pages`** in its current `can=` (12.6).

Otherwise the grant's row says `cannot`, with the reason. A grant's address rides nowhere else: not in a hello, a frame, STATE, a
context, the node's window list (for any credential, the PIN included), a journal line, a log line, a report or a proposal.

### 12.6 Messages on a node link

- **What the app offers.** A connecting app lists the abilities it offers on this link in its upgrade's query: `can=pages`,
  `can=window-draw`, or both, comma-separated. Each is a cap word whose row (14.6) names the app's kind. The node reads the first
  `hello.caps` words and ignores the rest, a word it does not know and a word whose cap row does not name the app's kind: an
  ability it ignores is not offered. These are the app's abilities, not 13.1's features, which the verdict's `can` lists.
- **The grants**, node → app, on every link of 12.5's first point, whether or not that node holds a grant: after the verdict and
  before any frame or other window message, then whole whenever they change (latest wins). A node that holds none hears an empty
  list, so a grant withdrawn while its app was away ends when that app links again.

```json
{"type":"grants","from":"3f9c2a1b7d0e","rev":3,"link":"tls","grants":[
 {"window":"wk3m9x2","name":"lobby","how":"page","path":"/w/k7m2…","cannot":""},
 {"window":"wq8r4t1","name":"foyer","how":"page","path":"","cannot":"this link is not encrypted — link over wss://"},
 {"window":"wz5n6p7","name":"stage","how":"draw","path":"","cannot":""}]}
```

  - `link` is `tls`, `local` or `plain`. `path` is set only as 12.5 allows; `cannot` is empty, or says why not.
  - `rev` rises whenever the list changes, and restarts with the node: a receiver takes the first `grants` on each new link whatever
    its `rev`, and later ones only with a higher `rev`.
- **A draw grant's window**, node → app, only on a link of 12.5's first point, on a key never seen in clear (13.6.9), that offers
  `window-draw`:
  - `{"type":"window-assign","window","name","content","hold","holdText","holdImage","onLoss","fadeMs","rev"}` — 12.3's window
    message for that window — on connect and on each change;
  - `{"type":"window-end","window","why"}`, `why` `withdrawn` or `deleted`;
  - `{"type":"window-identify","window","name"}`.
- **The app → the node**, each within `link.ws.in.bytes`:
  - `{"type":"grants-seen","rev","cannot"}`, where `cannot` says why the app shows no page at all, or is empty;
  - `{"type":"window-shown","window","rev","content","hold","ms","error"}` — 12.3's report — only for a draw grant of that window
    held by this link's node. Any other is answered
    `{"type":"error","error":"only a show app a window is granted to draw may say what it draws"}`.
- A node link hears no screen message (identify, reload, redirect, disconnect), and no window message but these, for its own grants.
  `window reload` of a window that only apps draw is refused: it has no page to reload.

### 12.7 The black end

- A page grant's address opens a window link as 12.3's does: `/ws?win=<grant token>&detail=1`.
- When that address stops working — withdrawn, deleted or revoked — its link hears
  `{"type":"window","revoked":true,"black":true}` and is let go. With `black`, the window draws plain black with no words, sets
  `data-link` to `gone` (12.9) and stops trying.
- A window's own address keeps 12.3's end, in words: a person put it there, and a person reads it.

### 12.8 What a holder publishes

A node that holds another node's view pass keeps it as a secret (§1 rule 5). Wherever it would name that address — its STATE, context,
journal, logs, reports, show file, library — it names a held form that carries no pass:

- `stagewatch://<granting node's id>/<window id>`: a grant;
- `kept://<host[:port]>/<id>`: a pass its operator kept, the id of its own random making and never a function of the address;
- `unkept://<host[:port]>`: a pass that reached it without being kept. It opens nothing and draws black.

Only its browser, as it navigates, turns a held form back into the address. A twin carries a kept pass only as 13.18 lets it carry a
secret, and never a grant's address.

### 12.9 Drawing a held window

- A browser that hosts a window's page MAY read the window's state to decide when to draw it:
  - the root element's `data-rev` (0 until the node has said what the window shows), `data-link` (`ok`, `lost` or `gone`),
    `data-showing`, `data-hold` (`1` or `0`) and `data-error`;
  - `SWShell.state()`, whose `loading` names a page still on its way.
- It draws the page only once its navigation succeeded, `data-rev` is above 0 and `data-link` is not `gone`. Otherwise — no page yet,
  a failed navigation, gone — it draws **black, never words or white**, and says why on its own desk, STATE and context.
- An app that draws a window draws:
  - its content, from the last `window-assign` for it on its current link to the granting node;
  - the window's own loss state, its `onLoss`, while that link is down, or is up but has not assigned it;
  - black for a window it was never granted, or whose grant ended: a `window-end`, or a `grants` that leaves it out, a new link's
    first included.

  It never falls back to another source, and says in `window-shown`'s `error` what it could not draw.

### 12.10 Placement claims and the granting node's readers

- An app says where it shows a granted window in its own context (§6), with a link whose `from` names the granting node's item,
  `window:` and the window's name in lower case:
  `{"from":"@3f9c2a1b7d0e/window:lobby","to":"source:web:stagewatch://3f9c2a1b7d0e/wk3m9x2","kind":"feeds"}`.
- The granting node takes a placement only from a context that app's key proves (12.5's first point), from an app that holds a grant
  of that window, and only while that grant has an open page or a `window-shown` report. It is always the app's claim: "Patterns FOH
  says: Screen 2 — LED wall".
- Its context has one item per window, `window:` and the window's name in lower case (kind `screen`; no two of a node's windows'
  names differ only in case). Its light is 12.3's over every viewer the window has — its own address, each page grant's address,
  each draw grant's report — the worst of those opened, grey while none has been opened. Its words name the grants and the claims.
- Its operator frame (§4) MAY list its windows — name, light, what each shows, to whom it is granted, the claims — never with a
  token or a path. Its public frames name no window.

## 13. PeerLink — one authenticated link between two nodes (1.3)

Discovery (§2) finds a node and says where its doors are. A **PeerLink** is one connection between two nodes that have proved to each
other who they are, together with the HTTP calls that ride beside it to the same node.

- Commands, receipts, state, context, proposals and windows (§3–§6, §11, §12) travel on it unchanged.
- This section owns only *how* they travel: identity, keys, TLS, liveness, the clock, request ids and the envelope of a receipt,
  order, bounds, redial, and the link's state, which every reader takes from one record (13.16).
- Every app-to-app link in this contract is a PeerLink:
  - Stagewatch's playout links and the node links on its `/ws`;
  - Patterns' Stagewatch link and its Show Bus node devices;
  - PlayMap's Stagewatch and Patterns links;
  - AGMessage's Stagewatch link and its links to Patterns and PlayMap.
- Doors that are not PeerLinks keep their own rules and are named in 13.18.

MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119. The *connector* dials; the *acceptor* answers. A number named in
`code` (`link.stale.ms`) is a §14 row. Times are on the node's monotonic clock unless called wall time. *Within* a limit includes the
limit (6000 ms of silence is within `link.stale.ms`); a hold or a wait is over at its end (a hold to 60000 lets a dial at 60000).

### 13.1 Words

- **Carrier.**
  - `line`: §3's line grammar — Patterns' wire (TCP 9697), PlayMap's wire (TCP 9941), and PlayMap's `/ws` with subprotocol
    `playmap.lines.v1`, one text frame per line.
  - `ws`: a node link on Stagewatch's `/ws` (§7.1).
  - A **side call** is an HTTP request to the acceptor of a live `ws` link, on that link's scheme, host, port and pin.
- **Local**: the socket's remote address is a loopback address — never because of a host name, the hello's `host`, or an address the
  node owns.
- **Verified**: both proofs of 13.6 checked, each by the other end. A link that is up without them carries the flag `legacy`, `open`
  or `keyless` (13.16.2).
- **Level**: 1 on a link that ran LINK; 0 on a link to an acceptor that does not speak it (13.6.8).
- **Feature**: a word in the verdict's `can` list: `final`, `receipts`, `stuck`, `expire`, `enrol`. An unknown word is ignored.
- **Show writer**: a node's single writer of the show. These are Stagewatch's show thread, Patterns' desk thread, PlayMap's engine
  control lane and AGMessage's event loop.
  - Its **stuckMs** is how long it may go without turning over before the node says its show is stuck.
  - Each node's §8 row names both, with the measurement behind the number.
- **The show needs a link** when the link is configured to be followed, is a playout link, or carries a grant (§12). A link only heard
  never turns red.
- **Link record**: what a connector publishes about each link (13.16.3).

### 13.2 What the venue network can and cannot do

Anything on the show network — the venue's Wi-Fi included, where every client may read every other's traffic — is assumed hostile.

| anyone on the show network can | under this section they cannot |
|---|---|
| hear every hello: names, ids, hosts, ports, caps, fingerprints (public by design) | learn a key, code, PIN or view pass from a PeerLink: none crosses it in clear. Keys and codes are proved, not sent (a level-0 bearer key goes only inside TLS to a certificate a proof or a person pinned, 13.6.8). The one derived credential, S, and a view pass on the one link §1 rule 5 lets it ride, travel only inside pinned TLS or on a local link (13.1, 13.6, 13.7, 13.19). The browser doors (`?pin=`, a window's address on plain HTTP) are named in 13.18 with what they leave open |
| forge hellos for any id, address or fingerprint | make a link plain, change or confirm a pin, choose the key that is proved, mark a link verified, or refuse a link (13.4.2) |
| answer at a node's address (ARP, a stolen IP, a forged hello) | complete LINK without the key, or relay a proof between two nodes, because proofs are mutual and bound to the certificate each end saw (13.6.3); nor, once that node has proved itself to a connector (its floor, 13.9.3), be followed by it for a fact. Before a floor, a level-0 or `open` answer is followed as in 1.2, amber, and says so (13.6.7, 13.6.8) |
| intercept a first contact with its own certificate | have that certificate pinned on a keyed link: the proofs fail and nothing secret was sent (13.8.2); a certificate pinned from the public view (`first`) is sent no key (13.6.8) |
| replay anything it recorded | replay a proof (fresh nonces), use S off TLS (refused), or run a command twice (the receipt store, 13.12.6) |
| strip TLS or LINK from a link it sits in | downgrade a node that proved itself before: floors (13.9.3) |
| read and alter a plain `line` link off this machine | use it to carry a secret, enrol or schedule a timed command; the link carries the flag `plain` (13.3.3, 13.11.6) |
| record a code's proof made on a plain carrier | — a recorded code proof is an offline guessing target (about 2^59 for a made Patterns token, trivial for a typed short one); an issued key's proof is not; the link says so (13.5.1, 13.16.2) |
| send wrong guesses from its own or a spoofed address | lock out a PIN, a browser or another key: wrong proofs count per (address, kid) in a table of their own, and unknown kids count nowhere (13.5.6) |
| drop, delay or flood traffic; jam the Wi-Fi; see who talks to whom | — not prevented; every link says what it lost (13.16) |

### 13.3 Carriers

1. Where each node is dialled, and what it dials:

| node | dialled at (acceptor) | dials (connector) |
|---|---|---|
| Stagewatch (`main`) | `ws` and its side calls on the web port (9924), TLS sniffed on the same port | Patterns 9697 (`line`); PlayMap 9941, or `playmap.lines.v1` over TLS on PlayMap's `https` door (`line`) |
| Patterns (`patterns`) | `line` on 9697, TLS sniffed on the same port (13.8.5) | Stagewatch (`ws`); PlayMap 9941 or `playmap.lines.v1` (`line`), as a Show Bus node device |
| PlayMap (`playmap`) | `line` on 9941 (TLS sniffed) and `playmap.lines.v1` on its `https` door | Stagewatch (`ws`); Patterns 9697 (`line`) |
| AGMessage (`messenger`) | — (no node links to it with a key in 1.3) | Stagewatch (`ws`); Patterns and PlayMap (`line`) |

2. **Off this machine a `ws` link and its side calls MUST be TLS.**
   - A connector MUST NOT send a credential on a plain `ws` link or side call to another machine, whatever a hello, a typed `http://`
     address or a setting says.
   - With no TLS on offer, it reads the acceptor's public view keyless and says why: "<name> offers no TLS — the key stays on this
     machine".
3. A `line` link MUST be TLS when the acceptor offers TLS on that port. Where it offers none, a `line` link MAY run plain off this
   machine only with the flag `plain` (13.16.2):
   - verified by LINK;
   - carrying no secret;
   - shown amber;
   - its clock never scheduling a timed command (13.11.6);
   - its lines understood to be readable and alterable on the path.
4. A local link MAY be plain.
   - A node that forwards outside traffic to one of its ports through loopback (a tunnel, a reverse proxy) MUST tell that traffic apart
     and MUST NOT treat it as local.
   - Before any listener grants anything to loopback, it MUST check that the request names the node by one of its own hosts (`Host`)
     and comes from no foreign page (`Origin` absent or the node's own).
5. Every listener a PeerLink uses MUST honour the node's bind address.
6. **HTTP polling alone is not a PeerLink.** A node that only polls another's context is a *reader*: it shows the context's age, grey
   at best, never a green link, and sends no command through it.

### 13.4 Identity and the hello

1. A node is its hello `id` and `t`, together with its certificate (§10) and the key it proves.
   - A connector keeps one link record per node id: the pin, the floor (13.9.3), the key held for that node, the clock and the state.
   - An address is where to dial, never who.
2. **A hello may raise a link's guard and MUST NOT lower it.**
   - It may add a peer to dial, an address to try, and the hints `tls` and cap `peerlink` (try TLS first, try LINK first).
   - It MUST NOT make a link plain, change or confirm a pin, choose which key is proved or where a key goes, mark anything verified, or
     refuse a link.
   - **For an id with a pin, the pin alone decides.** The connector dials TLS whatever the hello says.
   - A hello that contradicts the pin (no `tls`, another fingerprint, another address only) is shown as a note on the link ("the hello
     heard for Hall A names no TLS — a stale or forged hello; dialling TLS as before"), or as §2's conflict, never as a refusal.
   - A typed address follows the same rule.
3. A typed address is recorded as `addr:<host>:<port>` until the first verified link names the acceptor's id.
   - The record then becomes `node:<id>`, with that pin, and the key typed for it is bound to that id.
   - A key bound to one id MUST NOT be proved to, or sent toward, another.
   - An acceptor that answers LINK with an id other than the one the connector meant is refused, naming both ids ("10.0.0.5:9697 now
     answers as patterns 7a7a… — not Patterns FOH 9f8e…; accept it on the Nodes page"). Nothing it says is followed until the operator
     accepts the new id.
4. An id in conflict (§2) gets no new link; a link it already has keeps running (13.15.3).

### 13.5 Keys

1. **Classes.**
   - *key* — machine-made, with at least `peerlink.key.bits.min` random bits from the platform's CSPRNG, shown once: `<prefix>_` and 32
     symbols of `abcdefghijkmnpqrstuvwxyz23456789` (160 bits).
     - Stagewatch's `swk_…` are such keys; Patterns issues `ptk_…` and PlayMap `pmk_…` for app links.
     - `K = SHA-256(UTF-8(key))`, which is what Stagewatch already stores for a key.
   - *code* — a pairing code a person reads and types: Patterns' and PlayMap's pairing token.
     - Its canonical form removes `-` and every one of U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029,
       U+202F, U+205F, U+3000 and U+FEFF, and upper-cases `a`–`z`.
     - The result MUST be `[A-Z0-9]{1,64}`. A code that is not is never proved by LINK, and where it is typed the words say so
       ("PeerLink pairs a token of letters and digits — this one reaches only an older build"). The legacy proof keys a token its
       own way (13.6.8).
     - `K = SHA-256(UTF-8("showbus-peerlink-1 code\n" + canonical))`.
     - A code is the operator's credential: scope `operate`. Proved over a plain carrier off this machine, it is an offline guessing
       target, and the link says so (flag `code-plain`).
   - *link* — a key both ends derive at enrolment (13.5.7) and never send. `K` is its 32 bytes.
   - A key's `kid` is the lowercase hex of the first `peerlink.kid.bytes` bytes of SHA-256(UTF-8("showbus-peerlink-1 kid\n") ‖ K). A
     code's kid is the word `code`. A kid is not a secret.
   - A PIN, a viewing code and a view pass (§12.2) are never PeerLink credentials.
2. **One key, one pair.**
   - An acceptor binds each key to the one connector `t:id` it was made for — named when the operator makes it, or at its first
     verified use — and refuses it for any other: "this key speaks for <kind> <id>". That refusal is never counted.
   - A connector holds each key for one node id and its pin (13.4.3).
   - A code belongs to its node and may be proved by several connectors, each named by its own id.
3. **Scope.** Every key has one scope on this ladder, which each node's §8 row maps onto its own verbs:
   - `follow`: state, context, subscriptions;
   - `post`: the above, plus its own context, proposals and their results — Stagewatch's `node` scope;
   - `drive`: the above, plus the verbs the node lists for a playout desk — clip and playlist transport, cue GO and STANDBY;
   - `operate`: every verb, as the operator — a code, or a control key.

   The acceptor enforces the scope. A refusal names it: "this link's key is scope follow — Patterns FOH's operator can raise it on its
   Nodes page". A connector's own fence is extra.
4. **At rest.**
   - An acceptor keeps only what verifies: `K`, or the hash it already keeps.
   - A connector keeps its keys and codes protected — Windows DPAPI for the user, an owner-only file elsewhere — and never in a show
     file, a project, a backup, an export or a twin's traffic (13.18).
5. **Revocation reaches live links at once.**
   - Revoking or re-binding a key, or a new code, MUST refuse every verified link that used it (13.16) and end its session
     credentials (13.7).
   - A new code (Patterns' NEW TOKEN) also revokes every key enrolled from the old one.
   - The refusal reaches the link on its carrier. On `line` the acceptor writes `ERR LINK refused — <words>`, with no id, and ends
     the connection; on `ws` it sends the refused verdict (`{"type":"auth","ok":false,…,"class":"refused","retry":60}`), and the
     socket stays a public viewer (13.6.5). The connector takes either as a refused verdict: the key is held (13.15.3).
6. **Counting.**
   - Unknown kids and binding refusals are never counted toward any lockout.
   - Wrong proofs are counted in a table of their own: per (address, kid), and per address for `code`.
   - `peerlink.wrong.proofs.per.minute` in a minute lock that pair for the minute; a locked pair is refused before its proof is checked.
   - That table never locks out a PIN, a viewing code, a view pass, another key or a browser.
7. **Enrolment** (feature `enrol`; an acceptor MAY offer it). Over TLS, a link proved with a code enrols:
   - both ends derive `K_link = HMAC-SHA256(K_code, "enrol\n" ‖ T)` (T of 13.6.1);
   - the acceptor stores it bound to the connector's id, with the scope its operator set for that kind (`follow` unless set), journals
     it, and names it in the verdict (`"enrolled":"<kid>"`);
   - the connector proves with that kid from then on.

   A code proved over a plain carrier MUST NOT enrol. An acceptor lists each enrolled key with the connector's name, id, scope and when,
   so an enrolment nobody expected is seen.

### 13.6 The handshake: LINK

1. **The transcript.** T is these lines in UTF-8, joined by LF, with no final LF:

```
showbus-peerlink-1
carrier=<line|ws>
offer=<the connector's highest PeerLink version>
speaks=<the version the acceptor chose>
server=<acceptor t>:<acceptor id>
client=<connector t>:<connector id>
kid=<kid>
snonce=<32 lowercase hex: 16 bytes from the acceptor's CSPRNG>
cnonce=<32 lowercase hex: 16 bytes from the connector's CSPRNG>
bind=<tls:sha256:<64 lowercase hex> | plain>
```

   - `bind` is the certificate this connection showed (on the connector's side, the one it was shown; on the acceptor's side, its own),
     or `plain`.
   - Each field is checked against its shape before T is built, so no LF can enter it: `t` `[a-z0-9_-]{1,16}`, `id`
     `[A-Za-z0-9_-]{6,40}` (§2), kid 16 lowercase hex or `code`.
2. From T and K:
   - client proof = lowercase hex of HMAC-SHA256(K, "client\n" ‖ T);
   - server proof = lowercase hex of HMAC-SHA256(K, "server\n" ‖ T);
   - `lid`, the link's id (not a secret) = lowercase hex of the first `peerlink.lid.bytes` bytes of
     SHA-256("showbus-peerlink-1 lid\n" ‖ T);
   - on `ws`, the session credential S = lowercase hex of HMAC-SHA256(K, "session\n" ‖ T) (13.7).
3. **Order.**
   - The connector proves first. The acceptor verifies, then answers with its own proof.
   - The connector MUST NOT take state, context, a receipt, a clock sample, a pin or a fact from the connection before the acceptor's
     proof verifies; anything that arrives earlier is discarded and counted.
   - Each nonce answers one proof, right or wrong. One proof per connection: a wrong one ends the connection on `line`; on `ws` it ends
     the socket's standing as a node, and the socket stays a public viewer (§7.1).
   - An acceptor MUST treat an unknown kid exactly like a known one until the proof, and compare in constant time. It refuses both with
     the same words: "the proof did not verify: a wrong key, or another machine between us" (its own journal MAY name the kid).
   - The connector proves only to the id it meant (13.4.3) and over the certificate it was shown: an answer to LINK whose `bind` is
     not that certificate gets no proof. A verdict that comes before its proof, or whose proof does not verify, ends the connection
     with those same words, as a failed dial (13.15.1); nothing of it is taken, pinned or floored.
   - A connection that has not finished LINK within `peerlink.handshake.ms` is closed. An acceptor holds at most
     `peerlink.unpaired.per.address` unfinished connections per address.
4. **`line`.** Two verbs join the line grammar. Before LINK completes, an acceptor answers only §7.2's list — what finding and pairing
   need, `LINK`, and the queries its §8 row names as still open — and nothing else:

```
← NONCE 7c1e…                                   a 1.2 greeting; a LINK connector ignores it
→ HELLO Stagewatch
← OK
→ @l1 LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code
← OK @l1 LINK 1 patterns 9f8e7d6c 00112233445566778899aabbccddeeff tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04
→ @l2 LINK PROOF a0622441e11e24be7577abcff6599b5b8c5031a06739a2774e94307d70b5c09f
← OK @l2 paired {"v":1,"verified":"code","scope":"operate","proof":"a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02","lid":"c1bb791379aa366c","level":1,"can":["final","receipts","enrol"],"inst":"a41f09c2","beatMs":2000,"enrolled":"936086ee6bc93cef"}
← STATE {…}                                     the greeting, now; then on every change
```

   - The forms: `LINK <offer> <t> <id> <cnonce> <kid>`; its answer `OK LINK <speaks> <t> <id> <snonce> <bind>`; `LINK PROOF <64 hex>`.
   - The verdict is `OK paired <json>`, or `ERR LINK <class> — <words>`, with class `malformed`, `version`, `refused`, `locked`,
     `binding`, `scope`, `busy` or `open`.
   - The connector records a refusing class in the link record's `refusal` (13.16.3) as the same word, but `refused` (a wrong proof,
     or an unknown or revoked key) as `key`. `open` refuses nothing (13.6.7).
   - A word on a LINK line that the acceptor does not know refuses the line. A malformed proof is a wrong proof.
5. **`ws`.**
   - The connector opens `wss://<acceptor>/ws?node=<its id>&t=<its kind>&link=<offer>` with no `Authorization` header.
   - The acceptor's first message: `{"type":"link","v":1,"speaks":1,"server":{"t","id"},"snonce","bind"}`.
   - The connector's: `{"type":"link","client":{"t","id"},"cnonce","kid","proof"}`, whose `client` MUST equal the query's `node` and
     `t`.
   - The verdict is §7.1's, with fields appended: `{"type":"auth","ok":true,"scope","node":{"t","id","verified":"key"},"proof","lid","level":1,"can":[…],"inst","beatMs"}`,
     or `{"type":"auth","ok":false,"error","class","retry":60}`.
   - No frame is sent to a `link=` socket before its verdict. After `ok:false` the socket stays a public viewer.
6. **The verdict** carries:
   - `verified` (`key`, `code` or `link`) and `scope`;
   - the acceptor's `proof` and the `lid`;
   - `level`;
   - the features the acceptor will use on this link (`can`);
   - its `inst` (§2) and `beatMs`;
   - `stuckMs` when it lists `stuck`, `expireMs` when it lists `expire`, `enrolled` when it enrolled.
7. **An open acceptor** (no code, no keys) answers `ERR @l1 LINK open — this node asks for no key`.
   - A node id with a floor (13.9.3) is refused (13.16.1).
   - Otherwise the connector links unverified, with the flag `open` (13.16.2). Facts it publishes are followed as in 1.2, and the
     words say so — amber while this connector holds a key for it: "Hall A asks for no key now — anything at its address could
     answer".
8. **Level 0: an acceptor that does not speak LINK.** The connector knows it this way:
   - on `line`: any `ERR` to `LINK` whose words do not begin with `LINK` (Patterns' `unknown command`, PlayMap's `not paired` on its
     WebSocket channel), or no answer in `receipt.deadline.ms`;
   - on `ws`: a first message that is not `link`.

   Then:
   - **Floors first.** A node id with a floor (13.9.3) is refused (13.16.1); nothing more is sent.
   - **`line`, the legacy proof.**
     - The connector MAY answer §7's `NONCE` with 1.1's `AUTH-PROOF` (HMAC over the nonce and the HELLO name), and only a `NONCE` of
       exactly 32 lowercase hex.
     - The proof's key is the token as §7's 1.1 desks compare it, not 13.5.1's canonical form: `-`, space, tab, CR and LF removed,
       every other character kept and upper-cased alone, one UTF-16 unit at a time, by its simple mapping (`a` → `A`, `ä` → `Ä`,
       `ß` stays `ß`). 13.5.1's shape does not apply to it.
     - A token that, after that removal, still holds a character 13.5.1's canonical form removes (another space, a line or paragraph
       separator, U+FEFF) is not proved — 1.1 desks differ on those — and the words say so: "this token holds an invisible
       character — retype it".
     - It MUST NOT send plain `AUTH <secret>` to another machine, nor on a connection that heard a `NONCE` or an `OK` to its `NONCE` ask.
     - The link is `legacy`, amber: "<node> cannot prove itself yet — an older build; anything at its address could answer".
     - Facts it publishes are followed as in 1.2, and the words say so.
   - **`ws`, the legacy bearer.**
     - The connector sends its key in `Authorization: Bearer` only over TLS, and only to a certificate pinned for that id by a proof
       that verified over it (13.8.2) or by a person on this desk — typed, or confirmed with one press (*Trust certificate*, beside
       the short fingerprint).
     - A `first` pin (13.8.2), made from the public view with no proof, never carries a key, a code or S.
     - Otherwise it reads the public view keyless: "Hall A cannot prove itself yet (an older Stagewatch) — compare its fingerprint 03e9
       b152 ca42 518c and press Trust to send the key".
     - Side calls carry the bearer key on the same terms. The link is `legacy`, amber.
9. **Bearer keys** remain for tools and scripts.
   - A connector that speaks LINK MUST NOT send a key, code or token to an acceptor that speaks LINK.
   - An acceptor that receives a key or token in clear from another machine MUST mark it *seen in clear* — on its key row and in its
     context, amber, with when and from where: "replace this key".
   - It gives that key no window grant (§12), and from 1.4 refuses it.

### 13.7 Side calls

1. On a verified `ws` link, every side call carries `Authorization: ShowBus lid=<16 hex>, s=<64 hex>` (S of 13.6.2).
   - It goes only over TLS, to the certificate that link pinned, or to loopback; never in a URL.
   - S is valid while its link's socket is open and verified. The socket's end, a revocation or a refused verdict ends it, answered
     `401 {"ok":false,"error":…,"relink":true}`.
   - An S that verifies for its lid, received in clear from another machine (no TLS, and a remote address that is not loopback), is
     refused — 401 with `relink` — and that link ended: anyone on the path read it. A wrong S in clear is the same 401 and ends
     nothing: a lid is not a secret.
2. Side calls run on a lane of their own:
   - never in the beat's path (13.10.3);
   - at most `link.sidecall.inflight` per kind at a time, each within `link.sidecall.timeout.ms`;
   - a newer post replaces one not yet sent;
   - their failures are counted on their own item and never move the link's liveness; a 401 that asks for a relink ends the link
     (below).
3. **One door for refusals.**
   - A 401 with `"relink":true` ends S: the connector closes the link and relinks on 13.15.1's wait, with no hold. The relink's own
     verdict decides whether the key is held.
   - A 401 without it starts the key's hold (13.15.3).
   - A 429 waits its `Retry-After`.
   - A 403 says its words and holds nothing.
   - A timeout or a 5xx is retried on the call's own schedule for a read, a context post or an owed report (13.12.9), and never for a
     command (13.12.7).
4. A side call that carries a command carries its id — `X-Request-Id` on Stagewatch and PlayMap, the line's `@id` in the body on
   Patterns' `/api/cmd` — and its answer is read by 13.12.3.

### 13.8 TLS and pinning

1. TLS 1.2 or 1.3, with ephemeral key exchange; the certificate is §10's. A connector checks a certificate by its fingerprint
   (`sha256:` + lowercase hex of SHA-256 of the DER) and by nothing else — never a name, a chain or an authority.
2. **First use.**
   - With no pin, any certificate is accepted for the handshake.
   - It is **pinned only after the acceptor's proof verifies over it**.
   - On a link that reads only the public view, it is pinned after the first frame and shown `first`: "pinned 03e9 b152 ca42 518c at
     20:01 — compare it on Hall A's screen".
   - A `first` pin is kept for comparison, not trust: no key, code or S goes to it (13.6.8). A proof that verifies over it, or a
     person who confirms it on this desk, makes it pinned.
   - A certificate that differs from the hello's `tls` is a note, never a refusal; on a keyed link the proof decides.
3. **Changed.**
   - A pinned node showing another certificate gets nothing of the connector's — no key, code, proof, S or command — until the operator
     accepts the new one, typing at least 8 hex of it: CERTIFICATE CHANGED, refused (13.16.1).
   - Meanwhile the connector MAY read the public view keyless over that certificate, and MAY probe every `peerlink.cert.probe.ms` to
     show the current fingerprint.
4. A pin store that cannot be read is said, amber: "pins lost — every node is new here; compare fingerprints". Floors are lost with it.
5. An acceptor SHOULD serve TLS on every port a PeerLink uses, telling TLS from plain by the first byte (0x16), as §10 does for HTTP.
   A connector speaks first on every carrier — its `HELLO`, its upgrade or its TLS handshake — and never waits for a greeting before
   it, so an acceptor that sniffs greets once that byte has come; a connection that sends none is closed at `peerlink.handshake.ms`
   (13.6.3).

### 13.9 Versions, features and floors

1. The PeerLink version is chosen in LINK (`offer`, `speaks`). Both are inside T, so a changed offer fails the proof. The hello's
   `proto` decides nothing here.
2. **Features.** The verdict lists the features the acceptor will use on this link. A version-1 connector reads every version-1 shape —
   a fifth PONG word, an `OK @id final …` line, a `repeat` field — whether or not it uses the feature, and ignores what it does not use.
3. **Floors.**
   - After a verified link, a connector records for that node id what it reached: `{tls, level, proof}`.
   - A later link to that id that offers less MUST be refused (13.16.1): plain where TLS was, LINK refused as an unknown word, a lower
     `speaks`, or an `open` answer where a key verified.
   - The words: "<node> used to prove itself with PeerLink 1 over TLS; it now offers less — not linking". The connector keeps only what
     the acceptor shows any reader (§8's public view), where the carrier has one.
   - The operator lowers a floor with one press, journalled.
   - Floors come only from verified links, never from a hello.

### 13.10 Liveness — the link and the show

1. **Beats.**
   - The connector pings every `ping.min.ms`–`ping.max.ms` (§5).
   - The acceptor answers each ping at once, and never through its show writer.
   - A `ws` acceptor also sends a frame or `{"type":"beat"}` at least every `link.beat.ms`.
2. **Matched pongs.** Any complete message from the verified acceptor counts as heard. A pong is a clock sample only when it answers one
   of the connector's last `peerlink.pings.matched` pings (its `t1`).
3. **The link.** With s the silence since the last complete message:
   - **live** while s ≤ `link.stale.ms`;
   - **stale** while s ≤ `link.drop.ms`;
   - then **down**: the connector closes and redials (13.15), and the acceptor closes.

   So 6000 ms is live, 6001 stale, 12000 stale, 12001 down. The state is evaluated at least every `link.check.max.ms`, on a timer of its
   own that never waits behind a side call or a write.
4. **Its own gap.** When the evaluator's consecutive runs are more than `link.selfgap.ms` apart — this node was paused, asleep or
   starved — no link moves to down on that run, nor until `link.selfgap.ms` after it (a link past `link.drop.ms` shows stale
   meanwhile). The evaluator pings every link at once, decides again `link.selfgap.ms` later, and logs one line naming its own pause.
5. **The show.** Show liveness — `live`, `stuck` or `unknown` — is kept apart from link liveness.
   - `ws`: `{"type":"beat","live":false}` makes it `stuck`. Only the next state frame makes it `live`; a plain beat or a pong never
     clears it.
   - `line`, feature `stuck`:
     - the acceptor answers a ping `OK [@id] PONG <t1> <t2> <t3> STUCK` while its show writer has not turned over within its `stuckMs`;
     - on such a link, a pong without the word makes show liveness `live`;
     - the flag is read from a stamp the writer sets each turn, and written off the writer;
     - an acceptor lists `stuck` only once its `stuckMs` (between `show.stuck.min.ms` and `show.stuck.max.ms`) comes from a measured
       honest stall of its own show writer, named in its §8 row.
   - Otherwise show liveness is `unknown`, and the link claims nothing.
6. While a peer's show is stuck, or its link is stale or down, no counting value taken from it is shown as current: it shows `?` with
   the reason. The link stays live while only its show is stuck.

### 13.11 Clock offset and its quality

1. §5's exchange runs on every link. t1 and t4 come from the connector's wall clock; t2 is the acceptor's as it read the ping; t3 is as
   late as its writer allows, and never before t2.
2. A sample is refused when any stamp is ≤ 0, t4 < t1, t3 < t2, rtt < 0 or rtt > `clock.rtt.max.ms`, or when its pong is not matched
   (13.10.2).
3. The last `clock.window` samples are kept, and the one with the smallest rtt is believed. A link publishes:
   - `offsetMs`;
   - `errMs` = ⌊(rtt + 1) / 2⌋ + 1 of that sample;
   - `ageMs` since its t4;
   - `samples`.
4. **Quality.**
   - `unknown` with no sample;
   - **fresh** while `ageMs` ≤ `clock.fresh.ms`;
   - **fit for** a tolerance while fresh and `errMs` ≤ that tolerance.
5. **CLOCKS APART** when |`offsetMs`| − `errMs` > `clock.apart.ms`.
6. A timed command (`AT`) uses an offset only when it is fit for that verb's tolerance and was measured over TLS or a local link;
   otherwise the command falls back and says so. A clock measured over a plain link off this machine MAY be shown and MUST NOT schedule.
7. The window is emptied when the peer's `inst` changes, and when this node sees its own wall clock step.

### 13.12 Commands — request ids, receipts and outcomes

1. **Ids.** Every command a connector sends on a PeerLink carries a request id `<prefix><n>-<inst>`:
   - a prefix of 1–4 letters, a counter from 1, and its hello's `inst`;
   - unique for the process and never repeated by a later one;
   - at most `request.id.chars` characters (§3).

   An acceptor treats an id as opaque. The link's own lines are not commands: LINK and LINK PROOF carry `l<n>` and a `RECEIPTS` ask
   `rq<n>`, each counted from 1 on its connection.
2. **Outcomes.** Each command ends, at the connector, in one of:
   - `not-sent`: never written; it did not run;
   - `refused`: the acceptor said no; it did not run;
   - `accepted`: the acceptor took it and has not run it yet;
   - `applied`: it ran; `effect` says what changed;
   - `unconfirmed`: written, and no final answer came; it may have run ("unknown — <node> did not answer; look at its state").

   An outcome only moves forward: `accepted` → `applied` or `refused` by a final receipt; `unconfirmed` → `applied` or `refused` by a
   final receipt or a `RECEIPTS` answer (13.12.6). Whether an effect was later seen in the peer's state is a reader's (layer 3–4) fact,
   journalled as *seen*. A receipt MAY carry `frame`, the counter (§4) of the first state frame published after it ran.
3. **Reading answers.** Every connector reads by this table:

| the answer | outcome | words |
|---|---|---|
| line `OK @id`, or `OK @id <words>` that is not JSON | applied | the words |
| line `OK @id {…}` with `"ok":false` (a refused command's repeat, 13.12.6) | refused | `error` |
| line `OK @id {…}` with `"stage":"accepted"` (PlayMap) | accepted | `effect` |
| line `OK @id {…}` with `"outcome":"Requested"` (Patterns `CUE GO`) | accepted | Patterns' words |
| line `OK @id {…}` with `"outcome":"Confirm"` | accepted | Patterns' words + "asks for a second GO — a person's" |
| line `OK @id {…}` with `"stage":"applied"`, `"outcome":"Done"`, or neither, and no `"ok":false` | applied | `effect`, or the words |
| line `ERR @id <words>` | refused | the words |
| line `OK @id final {…}` / `ERR @id final <words>` | as the rows above, final | |
| JSON receipt `"ok":true,"stage":"applied"` | applied | `effect` |
| JSON receipt `"ok":true,"stage":"accepted"` | accepted (a `final` may follow) | `effect` |
| JSON receipt `"ok":false` | refused | `error` |
| any receipt with `"repeat":true` | as its other fields | + "answered from its record — not run again" |
| HTTP 2xx `{"ok":true}` with neither `stage` nor `msg` | accepted | "took it — this node does not say whether it ran" |
| HTTP 2xx `{"ok":true,"msg":"OK …"}` (Patterns `/api/cmd`) | applied | `msg` after `OK` and any `@id` |
| HTTP 2xx or 4xx `{"ok":false,"msg":"ERR …"}` or `{"ok":false,"error":"…"}` | refused | `msg` after `ERR` and any `@id`, else `error` |
| HTTP 401 with `"relink":true` | refused; the link relinks on 13.15.1's wait, with no hold (13.7.3) | the body's `error` |
| HTTP 401 without it | refused, and the key's hold (13.15.3) | the body's `error` |
| HTTP 403, 404, 409, 413 | refused | the body's words |
| HTTP 429 | refused | "rate limited — again in N s" (`Retry-After`) |
| HTTP 408, a 5xx, or no response after the request was written | unconfirmed | |
| no connection, a hold, `link.inflight.max` reached, a link that is not live, or a peer whose show is stuck | not-sent | why |
| written, then no final answer within `receipt.final.ms`, or the link dropped first | unconfirmed | "no answer in 30 s — it may have run" / "the link dropped before <node> answered" |
| an acceptor older than 1.1, which echoes no id | unconfirmed | "sent — this node gives no receipts; look at its state" |

4. **Deadlines.**
   - An acceptor answers within `receipt.deadline.ms` with applied, refused or accepted.
   - With feature `final`, it sends one follow-up within `receipt.final.ms` once it knows: `OK @<id> final <json>` or
     `ERR @<id> final <words>` on `line`, `{"type":"receipt",…,"final":true}` on `ws`.
   - A connector shows "waiting for <node>'s answer" after `receipt.deadline.ms`. A command written with no final answer within
     `receipt.final.ms` — an answer at 30000 ms still counts — or whose link dropped first, is `unconfirmed`.
5. **Late commands.**
   - An acceptor that runs a command more than `receipt.late.ms` after it read it says so: `"lateMs"` in the receipt, and a journal row.
   - Feature `expire` is offered only by an acceptor whose §8 row names an `expireMs` (≤ `cmd.expire.max.ms`) set by a measured stall
     of its show writer. With it, the acceptor does not run a command later than its `expireMs`, and answers refused "expired — read at
     20:14:03.120, not run".
6. **Receipts kept** (feature `receipts`).
   - The acceptor keeps, per verified connector id, the final receipts of its last `receipts.kept` ids for `receipts.kept.ms`.
   - A command whose id it holds MUST NOT run again: it is answered with the receipt it had and `"repeat":true` appended (or `accepted`
     while it still runs). On `line` that answer is the receipt as JSON: `OK @<id> {"ok":true,"stage":"applied",…,"repeat":true}`, a
     refused one's `"ok":false` with its `error`.
   - After a relink, the connector MAY ask `@rq1 RECEIPTS <id> …` (at most `receipts.ask.max` ids) or
     `{"cmd":"receipts","args":[…],"id":"rq1"}`, for its own ids only. The answer, from an acceptor whose `inst` is `a41f09c2`, to a
     connector whose `inst` is `11aa22bb`: `OK @rq1 {"inst":"a41f09c2","receipts":{"s17-11aa22bb":{…},"s18-11aa22bb":"unknown"}}`, or
     on `ws` that object's fields in a receipt to the ask, `{"type":"receipt","id":"rq1","ok":true,"inst":"a41f09c2","receipts":{…}}`.
   - The answer gives each id its final receipt, `{"ok":true,"stage":"accepted",…}` for one that still runs, or "unknown" for one the
     record does not hold: from the same `inst`, an id the acceptor never read or no longer holds; from another, one it cannot know.
   - Only a final receipt moves an `unconfirmed` command (13.12.2): `accepted`, and "unknown" from any `inst`, leave it `unconfirmed`,
     and neither is a reason to send it again (rule 7).
7. **No blind resends.** A connector MUST NOT send a command again once any byte of it may have reached the acceptor. Sending it again
   is a person's act, with the outcome in view and a new id.
8. **In flight.** At most `link.inflight.max` commands wait for answers on one link; the next is `not-sent` ("32 commands are waiting on
   <node>'s answers"). Nothing waiting is ever evicted, and nothing is queued for a link that is not live (§4).
9. **Reports owed** (a §11 result, an acknowledgement):
   - are idempotent at their receiver: a second one is answered 409 "settled", which counts as delivered;
   - are retried by their sender from `report.retry.first.ms`, doubling to `report.retry.max.ms`, ±20 %, for `report.retry.for.ms`;
   - are recorded before the act they report.
10. One journal row per command that reached the socket, when its outcome first settles; one more when an `unconfirmed` one is resolved
    or its effect is seen.

### 13.13 Order and latest wins

1. One writer per connection. Replies leave in the order their lines came, and receipts are matched by id, never by order. Commands,
   replies and receipts are never coalesced or dropped.
2. State is latest-wins.
   - A sender keeps one pending push per kind and replaces it.
   - A reader keeps the newest frame per stream, and on one connection takes a frame only when its counter (§4) is above the last it
     took.
   - The first frame on a new connection is taken whatever its counter.
   - A counter lower than the last connection's, or a new `inst`, means the peer restarted: every anchor and held value taken from it is
     dropped, save 13.17's held facts.
3. An event stream (every item matters) is ordered and counted. A reader that falls behind is cut, with words saying where to resume.

### 13.14 Bounds and back-pressure

| bound | value |
|---|---|
| a command line an acceptor reads | ≤ `link.command.bytes`; longer: `ERR … — closed` |
| a message a connector reads | at least `link.read.min.bytes`. A longer one is counted and shown amber with its size (`oversize`), read only for the fields it can reach, and never taken as whole; the link stays up |
| a message a connector sends to Stagewatch's `/ws` | ≤ `link.ws.in.bytes` |
| a posted context | ≤ `context.bytes` (§6) |
| replies waiting at an acceptor, per connection | ≤ `link.replies.max`, plus one pending push per kind; past that the connection is cut: "not reading its answers" |
| one write | within `link.write.deadline.max.ms`, or the connection is cut |
| commands in flight per link | ≤ `link.inflight.max` (13.12.8) |
| side calls | 13.7.2 |
| connections an acceptor takes | its own ceiling, said in words before it closes an extra one (a line of words, or `429` with `Retry-After`); a connector takes `Retry-After` as a hold |

The show writer never writes to, reads from or waits on a socket.

### 13.15 Reconnect and holds

1. **The wait before redial n.** n counts failed dials since the link was last stable, from 0.
   - base = min(`link.redial.max.ms`, `link.redial.first.ms` × 2ⁿ);
   - wait = round(base × (0.8 + 0.4 j)), with j uniform in [0, 1) from a generator seeded once per process from the CSPRNG (a test
     injects j).

   A first redial waits 400–600 ms, and none waits more than 6000 ms.
2. **n returns to 0 only after a link that was verified and stayed up — never down — for `peerlink.stable.ms`.** A connect, an upgrade,
   a reply or a refusal alone resets nothing, so an acceptor that accepts and closes is backed off.
3. **Holds replace the wait.**
   - A refused key — unknown, revoked, bound to another, a wrong proof, locked out, out of scope, or a side call's 401 that asks for no
     relink (13.7.3) — is held for max(`peerlink.hold.refused.ms`, the acceptor's `retry` or `Retry-After`).
     - The hold is recorded before any further I/O, and persisted with its end in wall time, so it outlasts a restart.
     - The connector proves that key again only when the hold ends, the key changes, or the operator presses *Link again now*.
     - A `ws` link keeps its public frames meanwhile, and proves the key again on a fresh `link=` socket.
   - The acceptor operator's `disconnect` holds `peerlink.hold.disconnect.ms`.
   - An id in conflict (§2) is held while §2's mark stands: a link it has keeps running (13.4.4), and nothing new is dialled to it —
     state `held`, refusal `conflict` — until the mark clears, when the connector dials again at once.
   - CERTIFICATE CHANGED, a floor and another id link nothing keyed until the operator acts.
4. **Where.** The connector dials the address its link uses while the hello still names it (§2), and a peer on its own machine at
   127.0.0.1 first.
5. **Nothing crosses a drop** (§4). A command not yet written becomes `not-sent`; a written one becomes `unconfirmed`.

### 13.16 States, and what each side shows

1. A connector keeps one state per link, from this closed set. Every light carries its shape beside its colour.

| state | when | light | the connector says (example) | commands | counting values |
|---|---|---|---|---|---|
| `off` | not configured | ○ grey | "link off" | not-sent | none |
| `finding` | no address, and no live hello of the kind | ○ grey | "listening for a Stagewatch on the show network" | not-sent | none |
| `connecting` | dialling, TLS, LINK | ○ grey; △ amber after `peerlink.handshake.ms` | "dialling Hall A at 10.0.0.5:9924 over TLS (attempt 3)" | not-sent | `?` |
| `live` | heard within `link.stale.ms`; verified, or up with `legacy`, `open` or `keyless` | ✓ green; △ amber with an amber flag | "Hall A · pinned 03e9 b152 · key e63c… · scope post · PeerLink 1 · +12 ms ±3" | sent | shown while its show is live |
| `stale` | silent past `link.stale.ms`, up to `link.drop.ms` | △ amber | "Hall A quiet for 8 s" | sent; may end unconfirmed | `?` |
| `down` | silent past `link.drop.ms`, dropped or unreachable; redialling | ✕ red when the show needs it, else △ amber | "Hall A not answering — dialling again in 2.4 s; nothing is queued" | not-sent | none |
| `held` | a key refused, or an id in conflict (§2); its hold runs | ✕ red when the show needs it, else △ amber | "Hall A refused this desk's key (revoked) — next try 20:15:05; make a node key on Hall A" | not-sent | public view only |
| `refused` | CERTIFICATE CHANGED, a floor, another id: a person must act | ✕ red when the show needs it, else △ amber | "CERTIFICATE CHANGED: Hall A now shows ab12 … — nothing sent; accept it on the Nodes page" | not-sent | public view only, where the carrier has one |

   Beside the state is the **show**:
   - `stuck`: the link △ amber "Hall A says its show is stuck"; every value from it ✕ "no time shown"; commands not-sent;
   - `unknown`: an i note, no claim.
2. **Flags.** Each flag has its words; it is amber △ unless marked i.
   - `plain`: a `line` link off this machine with no TLS on offer — "proved, not private — its wire offers no TLS yet; timed GO off".
     It is i on a local link.
   - `keyless`: a key is set but held back. It is i "public view" when no key is set.
   - `legacy`: level 0 (13.6.8).
   - `open`: the acceptor asks for no key. Amber when this connector holds a key for it, i otherwise.
   - `code-plain`: proved with a pairing code over plain — "a recording of this proof can be guessed offline — make an app key on
     <node>".
   - `scope`: the key's scope is below what the link is used for.
   - `first` (i): pinned at first use; compare the fingerprint.
   - `note` (i): a hello contradicts the record.
   - `apart`: CLOCKS APART.
   - `unfit` (i): the clock is not fit for timed commands.
   - `oversize`, and `malformed` (i, a count).
   - A link's light is the worse of its state's and its flags', and red only when the show needs the link, a refused one included.
3. **The link record.** Every reader takes this one record. It is appended at the end of each row that carries a link, and never
   carries a key, code, `K`, S, proof or nonce:

```
{"peer":{"t","id","name"},"carrier":"line|ws","level":0|1,"tls":"pinned|first|none|local","pin":"<16 hex>",
 "proof":"mutual|legacy|open|none","kid":"<16 hex>|code","scope":"follow|post|drive|operate|none",
 "state":"…","refusal":"key|binding|scope|locked|version|certificate|floor|other-id|conflict|busy|malformed|","show":"live|stuck|unknown",
 "flags":[…],"light":"green|amber|red|grey","mark":"✓|△|✕|○|i","words":"…","since":<wall ms>,"heardMs":…,"attempt":n,"nextMs":…,
 "clock":{"offsetMs","errMs","ageMs","samples","apart"},"counts":{"unconfirmed","late","replayed","oversize","malformed","unmatched"},
 "floor":{"tls","level","proof"}}
```

   `refusal` is empty, a LINK verdict's class as 13.6.4 records it, `conflict` while §2's mark holds the link (13.15.3), or why the
   state is `refused`: `certificate`, `floor` or `other-id`.
4. **Every reader.** The connector publishes the record in:
   - its STATE;
   - its context item `link:<name>` (kind `link`, the light and the state's words) with its one `links` row to the peer node (§6's
     P2), and the `peers` row's `link` and `verified` (§6);
   - the Eye's edge, its desk's node card and Run strip, and its right-click menus;
   - its Companion variables and feedbacks;
   - its AI facts, its preflight and its report.

   No reader derives a link fact of its own. The Eye draws the links a node does not hold one hop out, from other nodes' contexts: an
   app's `link:` items and `links` rows become edges between that app and its peer, and a node known only from a context's `peers` is
   grey "heard, not linked".
5. **The acceptor's side.** An acceptor lists, on its operator surface and in its context (one `link` item and its row per peer
   LINK or a bound key proves, and one item that counts the rest, §6's P2):
   - each peer it accepted: its id, kind and name, carrier, level, TLS, kid or key name, scope, since when, and its light with its
     mark beside it:
     - ✓ green when it is verified (13.1), or when it is `keyless`, with i "public view" beside it (no key: it reads what this
       acceptor shows anyone, as its connector's light says, 13.16.2);
     - △ amber, over green, when it is `legacy` (level 0: the proof shows it knows the token, never who dialled) or `plain` (from
       another machine with no TLS; i on a local link), or its key was seen in clear (13.6.9);
     - ○ grey once its link has ended, for `peer.forget.ms`, and then it leaves the list; never red, since an acceptor does not
       redial;
   - each refusal with its words and its count this minute ("PlayMap Stage left (10.0.0.12): key e63c… refused (revoked) ×3 this
     minute");
   - each key with its bound connector, scope, last use, how it was made (issued or enrolled) and whether it was *seen in clear*.
6. **Operator actions.** These are on every surface, in step: the desk's buttons, right-click menus, wire verbs and Companion.
   - Connector: *Link again now*; *Trust certificate* (a `first` pin, one press beside its short fingerprint, 13.6.8); *Accept
     certificate* (at least 8 hex of the new one); *Lower floor*; *Accept new id*; *Release held fact* (13.17).
   - Acceptor: *Make key for…*; *Let this key speak for <node>*; *Revoke key*.

   Each is journalled. None is proposed by an assistant: a trust decision is a person's.
7. One journal row and one log line per state change — from, to, reason, attempt — never per beat (13.19).

### 13.17 Safety on loss

- A fact a node follows for safety — a blackout, a hold — is **held** when its link goes stale or down, or its show stuck.
  - It is shown with its age: "holding Patterns' blackout since 20:14:03 — link lost".
  - It is released only by the peer's live word or by a person, never by silence.
- A counting value is never held (13.10.6).
- What a link feeds to air goes to that output's own loss state — a window's `onLoss`, whether a browser shows it or an app draws it
  (12.9) — never to the last picture of a live count.
- No command is sent to a peer whose show is stuck (13.12.3).

### 13.18 Doors that are not PeerLinks

These doors keep their own rules:

- OSC;
- Stagewatch's Companion TCP (9923);
- Companion decks on the line wires: they keep §7's legacy proof until their modules adopt LINK, and never send plain `AUTH` off this
  machine;
- browser pages and `?pin=`: on plain HTTP a PIN crosses the network in clear — with Require HTTPS off, or in an `http://` address
  that Require HTTPS then redirects;
- Stagewatch's node links (§7.1) that the PIN or a control key proves: the operator's credential as a bearer, level 0;
- window links (§12): on plain HTTP a window's address crosses the network in clear, in the browser's address bar;
- Slides agents;
- PlayMap's sync (9942) and fleet traffic;
- PlayMap's desk↔engine link on one machine;
- Patterns' twin and its node relays. These keep failover's own carrier, beat (1 s), silence (5 s) and dialling, and:
  - MUST honour the bind address;
  - name a twin node's desk in its context by one item of kind `twin`, with the twin link's own light and words: the one item §6's P1
    lets name another node, and never a PeerLink's `link` item (P2);
  - send device secrets — a view pass the node keeps among them (12.8) — only under their own switch, which is off by default and
    refused on a plain link to another machine, and never a grant's address (12.4);
  - adopt 13.12's outcomes and 13.19 in a later round of their node.

Each door is named in its node's §8 row with what it does without a proof, and each node's health lights say what these doors leave
open. **A PeerLink proof grants nothing to an address:** it proves a key on one connection, and no OSC, trusted-address or loopback trust
follows from it.

### 13.19 Secrets and logging

1. A PeerLink sends none of these off this machine: a key, a code, `K`, `K_link`, a PIN. Keys and codes are only proved (13.6).
   The exceptions are S, inside pinned TLS (13.7), and a level-0 bearer key, only inside TLS to a certificate a proof or a person
   pinned (13.6.8). None of them, S included, rides in:
   - a URL, a hello, a frame, STATE, a context or a receipt;
   - a journal row, a log line, a report, a CSV or a support bundle;
   - an error or exception message;
   - a Companion variable, an AI brief or a proposal.

   A view pass (§12.2) is a page address that is itself a pass. It rides only the carriers §1 rule 5 names for it, and every
   other place above treats it as these secrets.
2. Proofs, nonces and transcripts are not logged either: a proof made with a code is a guessing target. A log names:
   - a key by its kid, or by its name and last four characters;
   - a certificate by its short fingerprint;
   - a peer by kind and id.
3. A line that may carry a secret — a legacy `AUTH`, a verb with a token argument, an `Authorization` header — is redacted where it is
   read, before any logger, journal or fault path sees it.
   - A fault answer never echoes the line.
   - Kept lines read `AUTH (token)`, `AUTH-PROOF (proof)`, `LINK PROOF (proof)`.
4. One log line per change of state: the link, the peer's kind and id, the address, from → to, the reason and the attempt. Never per
   beat.
   - While a state repeats, one summary every `log.repeat.summary.ms`: "still dialling Stage left: 11 attempts, last: refused".
   - Counters are published (13.16.3), not only logged.
5. Each tree runs 13.20's rows and its scripted-peer scenarios with canary secrets. It fails when a canary — or a proof or credential
   made with one — appears in anything it captured.

### 13.20 Conformance

A node sends cap `peerlink`, and offers LINK, only when its tree passes the block below and the scripted peer's scenarios for every
carrier it uses, in each role it plays. Its §8 row lists anything it does not keep yet, and what ends it.

- **The scripted peer** is `tools/showbus/peerlink-peer.mjs`: one dependency-free Node 22 file, byte-identical in every tree. Its SHA-256
  is §14's `peerlink.peer.sha256`.
  - It plays either end of a link, the connector or the acceptor, honestly or as an attacker: on `line` (TCP, TLS told from plain by
    its first byte, or `playmap.lines.v1`), on `ws` and on side calls.
  - Its acceptor keeps the features 13.1 names when a test lists them, binds and revokes keys (13.5.2, 13.5.5), enrols (13.5.7), and
    answers commands as the test sets it: applied, accepted with a final to follow, refused, late, unanswered or dropped.
  - Either end attacks when a command names an `attack`: a LINK replayed as it was sent (its cnonce, kid and proof), a forged kid, a
    server proof as a client's, a second proof under one nonce; a reused snonce, a replayed or reflected verdict, a NONCE that is not
    32 hex; an unmatched, forged or backwards pong and a lying clock; S in clear, under another lid or after its link; a flood of
    connections that never link; a reader that stops reading; a foreign `Origin` or `Host`. Each end reports what it did and what
    came back.
  - A tree's test drives it over its stdin and stdout, one JSON object a line: `listen`, `dial`, `send`, `call`, `set`, `expect`,
    `silence`, `close` and `quit`, each answered by one event, with an event for what it reads, writes and decides on each
    connection. The head of the file is its grammar. A command or a field it does not know, or a value of a shape its field does not
    take, is refused, never defaulted.
  - It takes no arguments. Keys, codes and certificates come from files their owner alone can read (mode 0600), named in a command.
    On Windows, where no mode is read, the file's own ACL keeps it — a file in the user's profile — and the peer does not check it:
    a named exemption.
  - It has no clock of its own: the link under test keeps its injected clock, and the times the peer stamps are the ones the test
    gives it. An `expect` or a `silence` waits at most 2 s of real time.
  - Each §14 number it keeps names its row on its own line (`// 14: <name>`), and a tree's test holds each to the block.
  - It never writes a key, a code, `K` or S of its own — from its files, or made from them — to its stdout. What crossed a wire, its
    own or the other end's, it reports as it was, so a test reads what went in clear.
- **The scenarios.** A tree runs these with the scripted peer against its own links, on each carrier each link uses:

| id | scenario |
|---|---|
| S1 | an honest link, each carrier and role: verified both ways, one lid at both ends, pinned over TLS, a command applied, a matched pong a clock sample |
| S2 | a wrong key → 13.6.3's words; `line` ended by the acceptor, `ws` a public viewer; held `peerlink.hold.refused.ms`; the attempt not reset |
| S3 | an unknown kid → S2's words, byte for byte, never counted (13.5.6) |
| S4 | another certificate at first use, a machine between → no proof made over it, or one that fails; nothing pinned; no key, code or S on the wire |
| S5 | a pinned id and a forged hello with no `tls`, another certificate or another address → TLS dialled anyway; a note; no refusal |
| S6 | CERTIFICATE CHANGED → nothing of the connector's sent; public view only; a probe at most every `peerlink.cert.probe.ms`; accepted only with 8 hex of the new one typed |
| S7 | LINK stripped (`unknown command` on `line`, a first frame on `ws`) on a floored id → floor-refused; never AUTH-PROOF, AUTH or a bearer sent |
| S8 | a LINK replayed as it was sent (its cnonce, kid and client proof), a reused nonce or a forged kid → refused |
| S9 | a server proof offered as a client proof → refused |
| S10 | S replayed in clear from another machine → refused and the link ended; S after the link closed → 401 relink; a wrong lid → 401 |
| S11 | silence: stale past `link.stale.ms`, down past `link.drop.ms`; each redial within 13.15.1's wait; the self-gap |
| S12 | stuck: the `ws` beat; the `line` `PONG … STUCK` with the feature; a pong never clears it on `ws` |
| S13 | a forged or unmatched pong → no sample; a clock over a plain link never schedules `AT` |
| S14 | receipts: applied, accepted→final, refused, a drop→unconfirmed, `receipt.final.ms`→unconfirmed, one past `link.inflight.max` not-sent; RECEIPTS with the same inst and a new one; a repeated id answered `repeat` and run once |
| S15 | an oversize message → counted, the link up; a slow reader → one push per kind, cut past `link.replies.max` |
| S16 | an unpaired flood → at most `peerlink.unpaired.per.address` per address, each closed at `peerlink.handshake.ms` |
| S17 | a key revoked while live → refused at once, its session ended |
| S18 | a key bound to another connector → a binding refusal, not counted |
| S19 | canary secrets: none of the key, code, K, S, proofs or nonces in any captured log, journal, STATE, context, report or error (13.19.5) |
| S20 | loopback trust through a local forwarder or a foreign `Origin` → not trusted |
| S21 | enrolment over TLS where offered; a code over plain → no enrolment; a new code revokes the keys enrolled from the old one |
| S22 | a level-0 acceptor with Patterns 88.1's grammar (STATE, then NONCE) → `legacy` amber, AUTH-PROOF only on a 32-hex nonce, no plain AUTH off the machine, nothing taken before `OK paired` |

- **Who passes what**, on each carrier its link uses:
  - every connector: S1, S2, S4–S7, S11–S14, S17–S19 and S22, and S21 where it enrols;
  - every acceptor: S1–S3, S8, S9, S12 (emitting), S14 (for the features it offers), S15–S20, S10 where it takes side calls, and S21
    where it offers `enrol`;
  - Patterns' twin (13.18): the `clock` rows and S19.
- **The block** holds four kinds of rows:
  - crypto rows: `canon`, `key`, `proof`, `refuse`, `legacy`;
  - parse rows;
  - pure rows: `liveness`, `redial`, `errms`, `clock`, `outcome`;
  - machine rows: events on injected time → the link record and what the node did.
- An `http` parse row is a side call's `Authorization` checked against the first `ws` `proof` row's live link: `clear` true when it
  came in clear from another machine, and `ends` the lid whose link the acceptor ends (null: none). A row that names no `ends`
  carries a header 13.7.1 does not take.
- `refuse` rows reuse the first `proof` row with the one named field changed; `role` offers that row's server proof as its client
  proof. `legacy` rows are §7's AUTH-PROOF, keyed as 13.6.8 says; one with `prove` false is a nonce or a token the connector does
  not prove. Hex is lowercase. `K` is the 32-byte key as hex.
- The block is ASCII: any other character in a row is a JSON `\u` escape, so no editor can drop or change an invisible one.
- **Machine rows.** A row plays the connector unless its `role` is `acceptor`. It starts from a fresh record — no failed dial
  counted, no hold — with its `config` applied. Its times are one injected clock in ms, standing for both the monotonic and the wall
  clock. Its `events` run in order, and at each `expect` time, after every event at or before it, each named field holds.
  - Only an `evaluate` event is a run of the evaluator (13.10.3, 13.10.4); the first has no gap before it. An `expect` time is no
    run: it reads 13.10.3 at that time, under any self-gap still pending.
  - `sent` and `credentials` count from the row's last `connected` or `verified` event, or from its start.
  - Every word below is the whole set: a row that uses another is malformed.

| config | what it sets |
|---|---|
| `carrier` | `line` or `ws`; every machine row names it |
| `can` | the features the verdict lists |
| `key` | the kid, or `code`, of what the connector holds for the node; none when absent |
| `pin` | `node:<id>`: the first `proof` row's certificate (the hex of its `bind`) pinned for that id by a verified link |
| `floor` | the floor recorded for the node (13.9.3) |
| `needs` | `true`: the show needs the link (13.1); otherwise it does not |

| event | what happened |
|---|---|
| `dial` | the connector dials, on its own schedule |
| `connected` | a connection is up, nothing verified on it |
| `answer` | the acceptor's answer to a dial: `tls` false (no TLS on offer), `cert` (the certificate shown, 64 hex), `line` (the line answering LINK), or `first` `frame` (`ws`'s first message is a state frame, not `link`) |
| `verified` | LINK completed, both proofs checked; `inst` is the acceptor's |
| `refused` | a LINK verdict refused the key: its `class` (13.6.4) and `retry` in seconds |
| `closed` | the connection ended |
| `heard` | a complete message from the acceptor, and nothing more |
| `frame` | a state frame |
| `beat` | `{"type":"beat"}` |
| `beat-stuck` | `{"type":"beat","live":false}` |
| `ping` | the connector wrote a ping with this `t1` |
| `pong` | a pong without `STUCK`: its `t1`, `t2` and `t3` when given, else it answers the last ping |
| `pong-stuck` | a pong with `STUCK` |
| `evaluate` | one run of the evaluator |
| `hello` | a hello heard with this `id`, `tls` (null: none) and, where given, `inst` |
| `send` | `count` commands asked for, numbered from 1 in `outcome` |
| `written` | the command with this `id` was written |
| `receipts` | a `RECEIPTS` answer: each id's receipt, or `unknown` |
| `side-call` | a side call's answer, in an `outcome` row's `answer` shape |
| `restart` | the connector's process restarts; only what it persists remains |
| `command` | the acceptor reads a command with this `id` on a verified link, and runs it to its end |
| `running` | the acceptor reads a command with this `id` on a verified link, and it is still running |
| `ask` | the acceptor reads a `RECEIPTS` ask for these `ids` |

| field | holds when |
|---|---|
| `state`, `light`, `show`, `refusal`, `tls` | the record's field is this value (13.16.3) |
| `flags` | each named flag is among the record's flags |
| `samples` | the record's clock holds this many samples |
| `counts` | each named count is this number |
| `waitMs` | the wait before the next redial lies in this range, ends included (13.15.1) |
| `heldUntil` | the hold ends at this wall time |
| `dial` | the connector dials now: `tls` or `plain`; null when it does not |
| `pinged` | the evaluator pinged every link at this time |
| `follows` | the connector takes the link's facts — state, context — or, when false, none |
| `sent` | the verbs of the lines the connector wrote (on `ws`, each message's `type` or `cmd`), pings left out, in order; `[]` for none |
| `credentials` | what the connector put on the wire, as a set: `proof`, `auth-proof`, `bearer`, `session`; `[]` for none |
| `inFlight` | how many commands wait for answers |
| `outcome` | each command's outcome (13.12.2), by its id or its `send` number |
| `runs` | how many times the acceptor ran the command |
| `answer` | the acceptor's last answer — a receipt's fields, or a `RECEIPTS` answer — holds these fields |

```peerlink
{"case":"canon","code":"k7qm-3xwd p9ra","canon":"K7QM3XWDP9RA","K":"5155f2dfc471eeab6498c03b18e3e3df5f8c9f7a2e5f85c8ed4526aaad49e5c8","kid":"code"}
{"case":"canon","code":"k7qm\u00a03xwd\u2009p9ra","canon":"K7QM3XWDP9RA"}
{"case":"canon","code":"\ufeffK7QM-3XWD-P9RA\u0085","canon":"K7QM3XWDP9RA"}
{"case":"canon","code":"k7qm\u20113xwd","canon":null}
{"case":"canon","code":"K7QM-3XWD-P9R\u00c4","canon":null}
{"case":"canon","code":"","canon":null}
{"case":"key","key":"swk_abcdefghijkmnpqrstuvwxyz23456789","K":"9e20155b4f2bf01d2f2db22477f4d206f8dbd3da98d1aba6518b12c61218c0ee","kid":"e63c317763b14546"}
{"case":"key","key":"ptk_3vq8xk2m9tz7w4hc6r5n2b8d7f4g9j3k","K":"9ea1b4fe90fa985d2b50fc444ef8941d6db25d55e41b09b8eea99a8efc84a039","kid":"bb3f97d01c274e15"}
{"case":"proof","carrier":"line","offer":1,"speaks":1,"server":"patterns:9f8e7d6c","client":"main:3f9c2a1b7d0e","kid":"code","snonce":"00112233445566778899aabbccddeeff","cnonce":"ffeeddccbbaa99887766554433221100","bind":"tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04","K":"5155f2dfc471eeab6498c03b18e3e3df5f8c9f7a2e5f85c8ed4526aaad49e5c8","Tsha256":"0a7706734a78a999335af163a788f9ace1ac831a53d999bc45b46c72ce7986c9","clientProof":"a0622441e11e24be7577abcff6599b5b8c5031a06739a2774e94307d70b5c09f","serverProof":"a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02","lid":"c1bb791379aa366c","enrolK":"fb7d7f2a95b74ba83c4dd61e4382fad70056f92f7267d5921471df4905c5dd44","enrolKid":"936086ee6bc93cef"}
{"case":"refuse","why":"bind","bind":"tls:sha256:8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc","clientProof":"b1117233650616c95cb2d776c67e24aacd286f08c8a7361bbaa31c5b0fe628b6","expect":"refused"}
{"case":"refuse","why":"offer","offer":0,"clientProof":"4ac3e7c904c4e6ada67f1e283bf8eac7f21c7a797b992e43e6143fac5b262571","expect":"refused"}
{"case":"refuse","why":"role","clientProof":"a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02","expect":"refused"}
{"case":"proof","carrier":"ws","offer":1,"speaks":1,"server":"main:3f9c2a1b7d0e","client":"patterns:9f8e7d6c","kid":"e63c317763b14546","snonce":"00112233445566778899aabbccddeeff","cnonce":"ffeeddccbbaa99887766554433221100","bind":"tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04","K":"9e20155b4f2bf01d2f2db22477f4d206f8dbd3da98d1aba6518b12c61218c0ee","clientProof":"469ead1078e9a7bc81fe31537c9f7dfaee0047ad55f4d7da3fd195ddbb267d43","serverProof":"5899ecdd0e97543dc8add2e68bfcc7878556fd5850d7e3c3378b93cbb38b3dbf","lid":"618b61b446eefa81","session":"c08920c546466a5a3ead976d6fbc50c240e5f071599439e8da889414aa350741"}
{"case":"proof","carrier":"line","offer":1,"speaks":1,"server":"patterns:9f8e7d6c","client":"main:3f9c2a1b7d0e","kid":"bb3f97d01c274e15","snonce":"00112233445566778899aabbccddeeff","cnonce":"ffeeddccbbaa99887766554433221100","bind":"plain","K":"9ea1b4fe90fa985d2b50fc444ef8941d6db25d55e41b09b8eea99a8efc84a039","clientProof":"d64f0760b67d30c1ae6e98e337a6990767a5382a851950063e3735ad88797ba6","serverProof":"386af6ffb6c7e13e94481111eb56fde13df8993bdeadcd96411900a82ffd012d","lid":"e7bb3c6328d0766c","flags":["plain"]}
{"case":"proof","carrier":"line","offer":1,"speaks":1,"server":"playmap:0a1b2c3d","client":"patterns:9f8e7d6c","kid":"code","code":"ABCD-EFGH-JKMN","snonce":"00112233445566778899aabbccddeeff","cnonce":"ffeeddccbbaa99887766554433221100","bind":"plain","K":"2ffce7e6490a662038d6fa71cd307b0ba41f4aec0068ffd9621ac8930a08e9a4","clientProof":"228994337068a5a5c3ca67af80275b72d2480c6b7d393d9cbe94806f06b377f4","serverProof":"4bff96d7f1602241b677249e2457aefc4b1b0a9b3e9049c11725ea4cccc587f2","lid":"a512c1c5f3a901cd","enrol":false,"flags":["plain","code-plain"]}
{"case":"legacy","code":"k7qm-3xwd p9ra","nonce":"3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e","name":"Stagewatch","proof":"f4196515d05ce2e593ac57af719ef450f4390826f1c20cce7079ac8b7ba75a13"}
{"case":"legacy","code":"abcd-efgh-ijkl","nonce":"3f9a0c1d2e4b5a69788796a5b4c3d2e1","name":"Stagewatch","proof":"7490a3de9065f4040b38fb07c1ffc489947348137fe81dca8d47f2fabd4f7f38"}
{"case":"legacy","code":"B\u00fchne-7_a!","nonce":"3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e","name":"Stagewatch","proof":"0783527db3e665748ccf3f49531ce48dd3f5de45053aab9abe3732d1d2c5604e"}
{"case":"legacy","code":"k7qm\u00a03xwd-p9ra","nonce":"3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e","prove":false}
{"case":"legacy","code":"\ufeffK7QM-3XWD-P9RA","nonce":"3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e","prove":false}
{"case":"legacy","nonce":"3F9A0C5E7B2D4F6A8C1E3B5D7F9A0C2E","prove":false}
{"case":"legacy","nonce":"3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2","prove":false}
{"case":"parse","carrier":"line","line":"LINK 1 main 3f9c2a1b7d0e FFEEDDCCBBAA99887766554433221100 code","expect":"ERR LINK malformed"}
{"case":"parse","carrier":"line","line":"LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa9988776655443322110 code","expect":"ERR LINK malformed"}
{"case":"parse","carrier":"line","line":"LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 e63c317763b1454","expect":"ERR LINK malformed"}
{"case":"parse","carrier":"line","line":"LINK 1 Main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code","expect":"ERR LINK malformed"}
{"case":"parse","carrier":"line","line":"LINK 1 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code extra","expect":"ERR LINK malformed"}
{"case":"parse","carrier":"line","line":"LINK 0 main 3f9c2a1b7d0e ffeeddccbbaa99887766554433221100 code","expect":"ERR LINK version"}
{"case":"parse","carrier":"line","line":"LINK PROOF a0622441e11e24be7577abcff6599b5b8c5031a06739a2774e94307d70b5c09","expect":"ERR LINK refused"}
{"case":"parse","carrier":"ws","query":"node=7a7a7a7a&t=patterns&link=1","message":{"type":"link","client":{"t":"patterns","id":"9f8e7d6c"}},"expect":{"ok":false,"class":"malformed"}}
{"case":"parse","carrier":"http","authorization":"ShowBus lid=618b61b446eefa81","expect":{"status":401,"relink":true}}
{"case":"parse","carrier":"http","authorization":"ShowBus lid=618B61B446EEFA81, s=c08920c546466a5a3ead976d6fbc50c240e5f071599439e8da889414aa350741","expect":{"status":401,"relink":true}}
{"case":"parse","carrier":"http","authorization":"ShowBus lid=618b61b446eefa81, s=c08920c546466a5a3ead976d6fbc50c240e5f071599439e8da889414aa350741","clear":false,"expect":{"status":200,"relink":false,"ends":null}}
{"case":"parse","carrier":"http","authorization":"ShowBus lid=618b61b446eefa81, s=c08920c546466a5a3ead976d6fbc50c240e5f071599439e8da889414aa350741","clear":true,"expect":{"status":401,"relink":true,"ends":"618b61b446eefa81"}}
{"case":"parse","carrier":"http","authorization":"ShowBus lid=618b61b446eefa81, s=0000000000000000000000000000000000000000000000000000000000000000","clear":true,"expect":{"status":401,"relink":true,"ends":null}}
{"case":"liveness","silenceMs":6000,"state":"live"}
{"case":"liveness","silenceMs":6001,"state":"stale"}
{"case":"liveness","silenceMs":12000,"state":"stale"}
{"case":"liveness","silenceMs":12001,"state":"down"}
{"case":"redial","n":0,"j":0,"ms":400,"tol":1}
{"case":"redial","n":0,"j":0.5,"ms":500,"tol":1}
{"case":"redial","n":0,"j":0.999,"ms":600,"tol":1}
{"case":"redial","n":1,"j":0.25,"ms":900,"tol":1}
{"case":"redial","n":3,"j":0.25,"ms":3600,"tol":1}
{"case":"redial","n":4,"j":0.25,"ms":4500,"tol":1}
{"case":"redial","n":9,"j":0.999,"ms":5998,"tol":1}
{"case":"errms","rttMs":0,"errMs":1}
{"case":"errms","rttMs":1,"errMs":2}
{"case":"errms","rttMs":11,"errMs":7}
{"case":"errms","rttMs":29,"errMs":16}
{"case":"errms","rttMs":250,"errMs":126}
{"case":"clock","samples":[[1000,1505,1506,1012]],"offsetMs":499.5,"rttMs":11,"errMs":7,"apart":false,"tol":1}
{"case":"clock","samples":[[1000,1520,1521,1050],[3000,3530,3531,3100],[5000,5512,5513,5030],[7000,7600,7601,7300]],"offsetMs":497.5,"rttMs":29,"errMs":16,"apart":false,"tol":1}
{"case":"clock","samples":[[1000,1505,1504,1012]],"refused":1}
{"case":"clock","samples":[[1000,1505,1506,999]],"refused":1}
{"case":"clock","samples":[[1000,1505,1506,62000]],"refused":1}
{"case":"clock","samples":[[0,1505,1506,1012]],"refused":1}
{"case":"clock","best":{"offsetMs":2020,"errMs":16},"apart":true}
{"case":"clock","best":{"offsetMs":2010,"errMs":16},"apart":false}
{"case":"clock","best":{"offsetMs":-2017,"errMs":16},"apart":true}
{"case":"clock","best":{"offsetMs":499,"errMs":16,"ageMs":10001},"fresh":false,"fitFor300":false}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb"},"outcome":"applied"}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb {\"stage\":\"accepted\",\"effect\":\"queued\"}"},"outcome":"accepted","words":"queued"}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb {\"outcome\":\"Requested\"}"},"outcome":"accepted"}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb {\"outcome\":\"Done\"}"},"outcome":"applied"}
{"case":"outcome","answer":{"line":"ERR @s1-11aa22bb standby moved"},"outcome":"refused","words":"standby moved"}
{"case":"outcome","answer":{"http":200,"body":{"ok":true,"msg":"OK @a1-5e6f7a8b clip ends in 10 s","id":"a1-5e6f7a8b"}},"outcome":"applied","words":"clip ends in 10 s"}
{"case":"outcome","answer":{"http":403,"body":{"ok":false,"msg":"ERR not paired \u2014 this desk asks for its pairing token first"}},"outcome":"refused","words":"not paired \u2014 this desk asks for its pairing token first"}
{"case":"outcome","answer":{"http":401,"body":{"ok":false,"error":"the link ended","relink":true}},"outcome":"refused","words":"the link ended"}
{"case":"outcome","answer":{"http":401,"body":{"ok":false,"error":"unknown key"}},"outcome":"refused","words":"unknown key"}
{"case":"outcome","answer":{"http":200,"body":{"ok":true}},"outcome":"accepted"}
{"case":"outcome","answer":{"http":502},"outcome":"unconfirmed"}
{"case":"outcome","answer":{"ws":{"type":"receipt","id":"p1-0a1b2c3d","ok":true,"stage":"applied","repeat":true}},"outcome":"applied"}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb {\"ok\":true,\"stage\":\"applied\",\"effect\":\"cut\",\"repeat\":true}"},"outcome":"applied","words":"cut \u2014 answered from its record \u2014 not run again"}
{"case":"outcome","answer":{"line":"OK @s1-11aa22bb {\"ok\":false,\"error\":\"standby moved\",\"repeat\":true}"},"outcome":"refused","words":"standby moved \u2014 answered from its record \u2014 not run again"}
{"case":"outcome","answer":{"written":true,"silenceMs":30001},"outcome":"unconfirmed"}
{"case":"outcome","answer":{"written":false,"link":"down"},"outcome":"not-sent"}
{"case":"machine","name":"silence","config":{"carrier":"line"},"events":[[0,"verified"]],"expect":[[6000,{"state":"live"}],[6001,{"state":"stale","light":"amber"}],[12000,{"state":"stale"}],[12001,{"state":"down"}]]}
{"case":"machine","name":"self-gap","config":{"carrier":"line"},"events":[[0,"verified"],[5000,"evaluate"],[14000,"evaluate"],[16000,"evaluate"]],"expect":[[14000,{"state":"stale","pinged":true}],[16000,{"state":"down"}]]}
{"case":"machine","name":"no reset on a bare connect","config":{"carrier":"line"},"events":[[0,"connected"],[100,"closed"],[700,"connected"],[800,"closed"]],"expect":[[100,{"state":"down","waitMs":[400,600]}],[800,{"waitMs":[800,1200]}]]}
{"case":"machine","name":"reset only when stable","config":{"carrier":"line"},"events":[[0,"connected"],[100,"closed"],[600,"verified"],[9000,"closed"],[10000,"verified"],[12000,"heard"],[16000,"heard"],[20000,"heard"],[20001,"closed"]],"expect":[[100,{"waitMs":[400,600]}],[9000,{"waitMs":[800,1200]}],[20001,{"waitMs":[400,600]}]]}
{"case":"machine","name":"hold survives a restart","config":{"carrier":"ws","key":"e63c317763b14546"},"events":[[0,"refused",{"class":"refused","retry":30}],[1000,"restart"]],"expect":[[1000,{"state":"held","refusal":"key","heldUntil":60000}],[59999,{"dial":null}],[60000,{"dial":"tls"}]]}
{"case":"machine","name":"ws stuck","config":{"carrier":"ws"},"events":[[0,"verified"],[1000,"frame"],[2000,"beat-stuck"],[2010,"pong"],[4000,"beat"],[9000,"frame"]],"expect":[[2010,{"state":"live","show":"stuck"}],[4000,{"show":"stuck"}],[9000,{"show":"live"}]]}
{"case":"machine","name":"line stuck","config":{"carrier":"line","can":["stuck"]},"events":[[0,"verified"],[2000,"pong-stuck"],[4000,"pong"]],"expect":[[2000,{"show":"stuck"}],[4000,{"show":"live"}]]}
{"case":"machine","name":"line without stuck","config":{"carrier":"line","can":[]},"events":[[0,"verified"],[2000,"pong"]],"expect":[[2000,{"show":"unknown"}]]}
{"case":"machine","name":"the pin decides","config":{"carrier":"line","pin":"node:9f8e7d6c"},"events":[[0,"hello",{"id":"9f8e7d6c","tls":null}],[0,"dial"]],"expect":[[0,{"dial":"tls","flags":["note"],"state":"connecting"}]]}
{"case":"machine","name":"a conflict holds until its mark clears","config":{"carrier":"line","pin":"node:9f8e7d6c"},"events":[[0,"verified"],[1000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[3000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"b7c3d9e1"}],[5000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[6000,"closed"],[9000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[13000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[17000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[21000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[25000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[29000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[33000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[37000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[41000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[45000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[49000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[53000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[57000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[61000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}],[65000,"hello",{"id":"9f8e7d6c","tls":null,"inst":"a41f09c2"}]],"expect":[[5000,{"state":"live"}],[6000,{"state":"held","refusal":"conflict","dial":null}],[64999,{"state":"held","dial":null}],[65001,{"dial":"tls"}]]}
{"case":"machine","name":"floor","config":{"carrier":"line","floor":{"tls":true,"level":1,"proof":"mutual"}},"events":[[0,"answer",{"tls":false}]],"expect":[[0,{"state":"refused","refusal":"floor","sent":[],"light":"amber"}]]}
{"case":"machine","name":"floor on a stripped LINK","config":{"carrier":"line","floor":{"tls":false,"level":1,"proof":"mutual"}},"events":[[0,"answer",{"line":"ERR @l1 unknown command 'LINK'"}]],"expect":[[0,{"state":"refused","refusal":"floor","sent":["HELLO","LINK"]}]]}
{"case":"machine","name":"a refusal the show needs is red","config":{"carrier":"line","needs":true,"floor":{"tls":true,"level":1,"proof":"mutual"}},"events":[[0,"answer",{"tls":false}]],"expect":[[0,{"state":"refused","refusal":"floor","light":"red"}]]}
{"case":"machine","name":"33rd command","config":{"carrier":"line"},"events":[[0,"verified"],[1,"send",{"count":33}]],"expect":[[1,{"inFlight":32,"outcome":{"33":"not-sent"}}]]}
{"case":"machine","name":"drop after write, same acceptor","config":{"carrier":"line","can":["receipts"]},"events":[[0,"verified",{"inst":"a41f09c2"}],[100,"written",{"id":"s1-11aa22bb"}],[200,"closed"],[800,"verified",{"inst":"a41f09c2"}],[810,"receipts",{"s1-11aa22bb":{"ok":true,"stage":"applied"}}]],"expect":[[200,{"outcome":{"s1-11aa22bb":"unconfirmed"}}],[810,{"outcome":{"s1-11aa22bb":"applied"}}]]}
{"case":"machine","name":"drop after write, acceptor restarted","config":{"carrier":"line","can":["receipts"]},"events":[[0,"verified",{"inst":"a41f09c2"}],[100,"written",{"id":"s1-11aa22bb"}],[200,"closed"],[800,"verified",{"inst":"b7c3d9e1"}],[810,"receipts",{"s1-11aa22bb":"unknown"}]],"expect":[[810,{"outcome":{"s1-11aa22bb":"unconfirmed"},"sent":["RECEIPTS"]}]]}
{"case":"machine","name":"drop after write, still running","config":{"carrier":"line","can":["receipts"]},"events":[[0,"verified",{"inst":"a41f09c2"}],[100,"written",{"id":"s1-11aa22bb"}],[200,"closed"],[800,"verified",{"inst":"a41f09c2"}],[810,"receipts",{"s1-11aa22bb":{"ok":true,"stage":"accepted","effect":""}}]],"expect":[[810,{"outcome":{"s1-11aa22bb":"unconfirmed"},"sent":["RECEIPTS"]}]]}
{"case":"machine","name":"drop after write, no receipts feature","config":{"carrier":"line"},"events":[[0,"verified",{"inst":"a41f09c2"}],[100,"written",{"id":"s1-11aa22bb"}],[200,"closed"],[800,"verified",{"inst":"a41f09c2"}]],"expect":[[800,{"outcome":{"s1-11aa22bb":"unconfirmed"},"sent":[]}]]}
{"case":"machine","name":"repeated id at the acceptor","role":"acceptor","config":{"carrier":"line","can":["receipts"]},"events":[[0,"verified"],[10,"command",{"id":"s1-11aa22bb"}],[20,"command",{"id":"s1-11aa22bb"}]],"expect":[[20,{"runs":1,"answer":{"repeat":true}}]]}
{"case":"machine","name":"an id that still runs","role":"acceptor","config":{"carrier":"line","can":["receipts"]},"events":[[0,"verified"],[10,"running",{"id":"s1-11aa22bb"}],[20,"command",{"id":"s1-11aa22bb"}],[30,"ask",{"ids":["s1-11aa22bb","s2-11aa22bb"]}]],"expect":[[20,{"runs":1,"answer":{"stage":"accepted","repeat":true}}],[30,{"runs":1,"answer":{"receipts":{"s1-11aa22bb":{"ok":true,"stage":"accepted","effect":""},"s2-11aa22bb":"unknown"}}}]]}
{"case":"machine","name":"unmatched pong","config":{"carrier":"line"},"events":[[0,"verified"],[0,"ping",{"t1":1000}],[50,"pong",{"t1":999,"t2":1505,"t3":1506}]],"expect":[[50,{"state":"live","samples":0,"counts":{"unmatched":1}}]]}
{"case":"machine","name":"stuck show refuses commands","config":{"carrier":"ws"},"events":[[0,"verified"],[1000,"frame"],[2000,"beat-stuck"],[2100,"send",{"count":1}]],"expect":[[2100,{"outcome":{"1":"not-sent"}}]]}
{"case":"machine","name":"a first pin carries no key","config":{"carrier":"ws","key":"e63c317763b14546"},"events":[[0,"answer",{"cert":"8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc","first":"frame"}],[100,"closed"],[700,"answer",{"cert":"8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc","first":"frame"}]],"expect":[[0,{"tls":"first","flags":["first","keyless","legacy"],"credentials":[]}],[700,{"tls":"first","flags":["first","keyless","legacy"],"credentials":[]}]]}
{"case":"machine","name":"open before a floor","config":{"carrier":"line","key":"code"},"events":[[0,"answer",{"line":"ERR @l1 LINK open \u2014 this node asks for no key"}]],"expect":[[0,{"state":"live","flags":["open"],"light":"amber","follows":true}]]}
{"case":"machine","name":"open after a floor","config":{"carrier":"line","key":"code","floor":{"tls":false,"level":1,"proof":"mutual"}},"events":[[0,"answer",{"line":"ERR @l1 LINK open \u2014 this node asks for no key"}]],"expect":[[0,{"state":"refused","refusal":"floor","follows":false}]]}
{"case":"machine","name":"a 401 that asks for a relink holds nothing","config":{"carrier":"ws","key":"e63c317763b14546"},"events":[[0,"verified"],[100,"closed"],[150,"side-call",{"http":401,"body":{"ok":false,"error":"the link ended","relink":true}}]],"expect":[[150,{"state":"down","waitMs":[400,600]}]]}
{"case":"machine","name":"a 401 without a relink holds the key","config":{"carrier":"ws","key":"e63c317763b14546"},"events":[[0,"verified"],[1000,"side-call",{"http":401,"body":{"ok":false,"error":"unknown key"}}]],"expect":[[1000,{"state":"held","refusal":"key","heldUntil":61000}]]}
{"case":"machine","name":"another id","config":{"carrier":"line","key":"code","pin":"node:9f8e7d6c"},"events":[[0,"answer",{"cert":"03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04","line":"OK @l1 LINK 1 patterns 7a7a7a7a 00112233445566778899aabbccddeeff tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04"}]],"expect":[[0,{"state":"refused","refusal":"other-id","sent":["HELLO","LINK"],"credentials":[],"follows":false}]]}
{"case":"machine","name":"a verdict before the proof","config":{"carrier":"line","key":"code"},"events":[[0,"answer",{"cert":"8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc","line":"OK @l1 paired {\"v\":1,\"verified\":\"code\",\"scope\":\"operate\",\"proof\":\"a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02\",\"lid\":\"c1bb791379aa366c\",\"level\":1,\"inst\":\"a41f09c2\"}"}]],"expect":[[0,{"state":"down","tls":"none","follows":false,"sent":["HELLO","LINK"],"credentials":[]}]]}
{"case":"machine","name":"no link within the handshake's time","config":{"carrier":"line"},"events":[[0,"connected"]],"expect":[[10000,{"state":"connecting"}],[10001,{"state":"down","follows":false}]]}
{"case":"machine","name":"a floor on silence to LINK","config":{"carrier":"line","floor":{"tls":false,"level":1,"proof":"mutual"}},"events":[[0,"connected"]],"expect":[[3000,{"state":"connecting"}],[3001,{"state":"refused","refusal":"floor","sent":["HELLO","LINK"]}]]}
{"case":"machine","name":"a ws key held proves again at its end","config":{"carrier":"ws","key":"e63c317763b14546"},"events":[[0,"refused",{"class":"refused","retry":0}],[30000,"frame"]],"expect":[[30000,{"state":"held","follows":true,"heldUntil":60000}],[59999,{"dial":null}],[60000,{"dial":"tls"}]]}
```

## 14. The numbers (1.3)

Every number this contract names in `code` (`link.stale.ms`) is a row of the block below, and so are its node kinds, the doors a
hello may name and its cap words. The block is inside the hashed body: a copy that holds the body holds every number. Each tree's own
test reads the block from its copy and holds its code to every row that code implements; what a node does not keep yet is said in
its §8 row, with what ends it.

1. **Rows.** One row per line, its words separated by one space, the first word naming the row. The block is ASCII and has no empty
   line. A name stands on one row, but for `kind`, `port`, `cap`, `reserved` and `peerlink.feature`, which take a row per word.

| row | what it says |
|---|---|
| `revision <major>.<minor>` | this contract's revision |
| `proto <n>` | the hello's `proto` for this revision (§2): the revision's minor number |
| `<name> <value>` | a number: lowercase words joined by dots, and a whole number — but `peerlink.label`, a word; `window.token.alphabet`, its symbols; and `peerlink.peer.sha256`, 64 lowercase hex |
| `kind <t> <name>` | a node kind (§2's `t`) and the name a reader shows for it |
| `port <word>` | a door a hello's `ports` may name (§2) |
| `cap <word> <t> …` | a cap word (§2) and the kinds that may send it; a dotted word is a finer ability of the word before its dot |
| `reserved <word> <t>` | a word one kind names on its own node page; it becomes a `cap` row when a second kind reads it |
| `peerlink.feature <word>` | a feature a verdict's `can` may list (13.1) |

2. **Units.** A name's unit is the one unit word in it: `ms` milliseconds, `hours` and `days` wall-clock hours and days, `bytes`
   bytes (text as UTF-8), `chars` characters, `bits` bits, `pct` per cent; a name ending in `.per.minute` counts in a minute and holds
   no unit word. `hello.id.min` and `hello.id.max` bound an id's characters, `hello.port` is a UDP port and `peerlink.version` a
   version; `peerlink.label`, `window.token.alphabet` and `peerlink.peer.sha256` are the words 14.1 names. Every other name without a
   unit word is a count.
3. **Limits.** A limit on what a node sends — a hello's bytes, caps and names, a context's sizes, a line, a request id — is the
   sender's maximum, and every reader accepts at least it; where a byte limit and a count both apply, the byte limit binds first.
   `hello.read.bytes` and `link.read.min.bytes` are what a reader takes at least; a reader accepts a command's `words` up to
   `context.words.chars` and keeps at least `context.words.read.chars` of them. Every other row is its own section's: a time, a
   table's bound, a count kept.
4. **Whose rows.** `proto`, `kind`, `port`, `cap`, `reserved`, `hello.*`, `peers.*` and `peer.*` are §2's; `context.*` §6's;
   `cert.*` §10's; `proposal.*` §11's; `window.*` and `view.*` §12's; the rest are §3–§5's and §13's. §13 names every number of its
   own in `code` but these, whose meaning is here:
   - `peerlink.version`: the PeerLink version this contract defines (13.9.1);
   - `peerlink.label`: the transcript's first line, and the start of the code, kid and lid labels (13.5.1, 13.6.1, 13.6.2);
   - `peerlink.can.words`: the most words a verdict's `can` lists (13.6.6);
   - `peerlink.nonce.bytes`: a nonce's bytes (13.6.1);
   - `peerlink.redial.jitter.pct`: how far 13.15.1's jitter moves a wait either way;
   - `link.connect.timeout.ms`: how long a dial waits for its connection before it counts as a failed dial (13.15.1).
5. **Copies.** A tree that copies this body copies the scripted peer with it, byte for byte. Every tree's header — the master's
   and each copy's — prints the body's SHA-256 once, on a line of its own, `*Body SHA-256 (§14): <hash>.*`, where `<hash>` is its 64
   lowercase hex and a full stop follows it; the tree's own test holds its body to that line and its scripted peer to
   `peerlink.peer.sha256`, so the body's hash is in the header and the peer's in this section. The master's header names, on its one
   line `*Landed (§14): <trees>.*`, the trees whose copies the hashes hold — the body's and `peerlink.peer.sha256` —
   by repository name, from (Stagewatch, Patterns, Mapper, AGMessage), each once, separated by a comma and a space, Stagewatch's own
   first. A tree joins once its copy passes its own test against both, and the master's next release names it. Both lines are
   outside the body, so neither changes the hash. A comparison of the trees, Stagewatch's `tools/showbus-copies.sh`, holds only the
   trees the Landed line names to this master — each one's body hashes to the master's, its header prints that hash, and its
   scripted peer hashes to `peerlink.peer.sha256` — and names each that does not as stale; a tree it does not name, it does not
   compare.
6. **Caps.** What each cap word says a node does:

| cap | the node |
|---|---|
| `timer` | runs a show timer and sends its frames (§4) |
| `cues` | calls cues: departments standing by, and their GO |
| `messages` | carries messages for people to read |
| `context` | answers §6's context |
| `receipts` | answers a command's request id with a receipt (§3) |
| `clock` | answers §5's ping with its times |
| `peerlink` | accepts LINK (13.6), its tree having passed 13.20: a hint to a connector to try LINK first (13.4.2) |
| `screens` | drives browsers that show its pages |
| `windows` | controls windows at their own addresses (§12) |
| `aux` | runs extra timers beside the show's |
| `slides-in` | follows Slides agents' feeds |
| `slides` | sends a slide feed (a Slides agent) |
| `copilot` | proposes plans of its own verbs for a person to approve |
| `approvals` | keeps §11's approval queue |
| `mcp` | serves an MCP endpoint |
| `ai` | drafts with a language model |
| `video` | plays clips to air |
| `video.clock` | publishes the clip's time left, with a counter that moves on every discontinuity (§4) |
| `video.transport` | takes play, pause, stop and restart |
| `video.seek` | takes a seek to a time in the clip |
| `video.end` | takes END: the clip jumps to its last seconds |
| `state` | pushes STATE on its line wire |
| `eye` | draws the room's nodes and links: the Eye |
| `pages` | opens a page granted to it in its own browser (§12) |
| `window-draw` | draws a granted window's content itself (§12) |
| `action-prompts` | asks people action prompts, with their titles in its context |
| `documents` | keeps documents for its people, never in its context (§6) |
| `proposals` | sends its AI's proposals to a desk (§11) |
| `playlist` | plays playlists |
| `media` | keeps a media library, each item known by its hash |
| `mapping` | maps content onto surfaces |
| `scan` | scans a surface with structured light |
| `show` | publishes Show Mode's facts in its STATE and context |
| `video.anchor` (reserved) | sends timeline anchors for a sync group |

```showbus
revision 1.3
proto 3
hello.port 9928
hello.every.ms 2000
hello.bytes 1200
hello.read.bytes 2048
hello.caps 32
hello.cap.chars 24
hello.ports 8
hello.name.chars 64
hello.kind.chars 16
hello.id.min 6
hello.id.max 40
hello.inst.chars 8
peers.max 128
peer.addresses 4
peer.stale.ms 6000
peer.gone.ms 12000
peer.forget.ms 60000
peer.conflict.clear.ms 60000
link.beat.ms 2000
link.stale.ms 6000
link.drop.ms 12000
link.redial.first.ms 500
link.redial.max.ms 5000
ping.min.ms 1000
ping.max.ms 2000
clock.apart.ms 2000
receipt.deadline.ms 3000
request.id.chars 32
context.bytes 65536
context.items 200
context.items.first 12
context.links 400
context.link.end.chars 120
context.recent 20
context.commands 256
context.peers 32
context.headline.chars 200
context.item.id.chars 60
context.item.kind.chars 30
context.item.label.chars 80
context.item.state.chars 160
context.recent.chars 200
context.verb.chars 512
context.words.chars 512
context.words.read.chars 200
context.post.min.ms 1000
context.post.max.ms 10000
context.kept.ms 30000
proposal.line.chars 2048
proposal.undelivered.ms 60000
proposal.result.ms 300000
proposal.collect.ms 15000
cert.days 825
cert.backdate.hours 24
cert.renew.days 30
window.token.chars 26
window.token.alphabet abcdefghijkmnpqrstuvwxyz23456789
window.max 64
view.wrong.per.minute 5
window.grants 4
kind main Stagewatch
kind agent Stagewatch Slides
kind patterns Patterns
kind playmap PlayMap
kind messenger AGMessage
port http
port https
port ws
port wire
port osc
port companion
port sync
cap timer main
cap cues main
cap messages main messenger
cap context main patterns playmap messenger
cap receipts main patterns playmap messenger
cap clock main patterns playmap
cap peerlink main patterns playmap
cap screens main
cap windows main
cap aux main
cap slides-in main
cap slides agent
cap copilot main
cap approvals main
cap mcp main playmap
cap ai messenger
cap video patterns playmap
cap video.clock patterns playmap
cap video.transport patterns playmap
cap video.seek patterns playmap
cap video.end patterns playmap
cap state patterns playmap
cap eye patterns playmap
cap pages patterns
cap window-draw playmap
cap action-prompts messenger
cap documents messenger
cap proposals messenger
cap playlist playmap
cap media playmap
cap mapping playmap
cap scan playmap
cap show playmap
reserved video.anchor playmap
peerlink.version 1
peerlink.label showbus-peerlink-1
peerlink.feature final
peerlink.feature receipts
peerlink.feature stuck
peerlink.feature expire
peerlink.feature enrol
peerlink.can.words 8
peerlink.nonce.bytes 16
peerlink.kid.bytes 8
peerlink.lid.bytes 8
peerlink.key.bits.min 128
peerlink.handshake.ms 10000
peerlink.unpaired.per.address 4
peerlink.wrong.proofs.per.minute 5
peerlink.hold.refused.ms 60000
peerlink.hold.disconnect.ms 30000
peerlink.cert.probe.ms 60000
peerlink.stable.ms 10000
peerlink.redial.jitter.pct 20
peerlink.pings.matched 8
peerlink.peer.sha256 f36847cfebeee05e9e76fa4f2071a4c205a544fddb0231c247c6975893dabc28
link.check.max.ms 1000
link.selfgap.ms 2000
link.connect.timeout.ms 5000
link.inflight.max 32
link.replies.max 256
link.write.deadline.max.ms 10000
link.command.bytes 65536
link.read.min.bytes 262144
link.ws.in.bytes 4096
link.sidecall.inflight 1
link.sidecall.timeout.ms 4000
clock.window 8
clock.rtt.max.ms 60000
clock.fresh.ms 10000
show.stuck.min.ms 1000
show.stuck.max.ms 10000
receipt.final.ms 30000
receipt.late.ms 1000
receipts.kept 256
receipts.kept.ms 600000
receipts.ask.max 32
cmd.expire.max.ms 10000
report.retry.first.ms 3000
report.retry.max.ms 30000
report.retry.for.ms 1800000
log.repeat.summary.ms 60000
```
