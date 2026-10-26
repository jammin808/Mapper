# PlayMap on the Show Bus — how it talks to Patterns, Stagewatch and an AI

PlayMap implements Show Bus 1.3 as the node kind `playmap`. The contract is [SHOWBUS.md](SHOWBUS.md), this tree's copy of
Stagewatch's master, byte for byte, with its scripted peer in `tools/showbus/peerlink-peer.mjs`; `ShowBusSpecTests` holds both to
the hashes the master pins, and its §8.3 is PlayMap's row (scopes, the doors that are not PeerLinks, the named exemption and what
PlayMap does not keep yet). This page is what PlayMap does on the bus today, what works with Patterns and Stagewatch as they are,
and how the additions this page asked of 1.3 were answered (§5).

## 1. Being seen

Every 2 s a hello to UDP 9928 (broadcast and loopback; never on Patterns' own beacon, 9700), from a socket bound to the node's
bind address, and none at all from a node bound to this machine only:

```json
{"t":"playmap","v":1,"proto":3,"id":"a1b2c3d4","name":"Stage left","host":"RENDER-01","web":9940,"wire":9941,
 "ports":{"http":9940,"https":9940,"sync":9942,"wire":9941,"ws":9940},
 "caps":["video","video.clock","video.transport","video.seek","video.end","state","context","receipts","clock","mcp",
         "playlist","media","mapping","scan","eye","show"],
 "clock":1760101234567,"inst":"0badc0de","tls":"sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04",
 "build":"0.2.0+17.abc1234"}
```

- `t` is neither `patterns` (Patterns hides other Patterns) nor `main` (Patterns would dial it as Stagewatch).
- It is Show Bus 1.3's hello (§2, §14): `proto` 3; at most 1200 bytes as sent, a name or host of escaped characters cut until it
  fits; name and host at most 64 characters, with no control characters; 16 caps, each a §14 `cap` row that names `playmap`;
  every door a §14 `port` row, and only doors that are open; `inst` new at every start of the process. `HelloTests` hold the
  builder to the contract's rows and this example to what it builds for these inputs.
- Top-level `web` and `wire` ride beside `ports` for older readers.
- `build` is the build the node runs, its whole identity in SemVer (`0.2.0+17.abc1234` from CI, `0.2.0-dev+abc1234` from a
  local build), the same string as HEALTH's `build` and MCP's server info, so a peer tells builds apart before it connects.
  A reader that knows no `build` ignores it; PlayMap's reader keeps at most 64 characters of SemVer's alphabet and drops any
  other value, keeping the rest of the hello.
- Stagewatch 2.19 names it "PlayMap" on its Show network (§14's `kind playmap PlayMap`); Patterns lists it as an app heard on
  the network and draws it unlit until it reads PlayMap's context ([FLEET.md](FLEET.md), P1–P3).

PlayMap listens on 9928 too: it lists every node, follows the Patterns desk and the Stagewatch it is pointed at
(or the first one heard), and finds other PlayMap nodes for sync groups and media transfer. It hears by 1.3 §2:

- A hello keeps its first 32 caps of §2's shape (others are words no reader knows), at most 8 doors, and its name and host whole
  up to 64 characters; it is read up to 4096 bytes. A datagram that is no hello — a `v` that is no whole number, a string that is a
  lone surrogate or not UTF-8 — is dropped and counted, and the next is heard.
- At most 128 ids: a hello from a new id past that is dropped (counted with the datagrams dropped) until one is forgotten.
- One id, one peer: every address it was heard from within 6 s is kept (at most 4; a fifth takes the place of the one heard
  longest ago, never of the one dialled). The address dialled stays while it is still heard and moves only once it goes stale,
  so a node heard on the network and on this machine never flips between them; an address change alone is not a change of the
  peer. The peer's hello is the one last heard at the address dialled, so the certificate a link pins is that address's.
- Two nodes, one id: an `inst` heard within 6 s that comes back after another (A, B, A), or, from nodes that send no `inst`,
  two hosts within 6 s, marks the id in conflict. STATE's `peers` shows it amber with both machines named ("two machines say they
  are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7"); the context lists no peer row for it (§6). The mark is the two
  machines, not their addresses: it keeps its words while both are heard, whatever address each is heard at and whatever other copy
  takes turns among them. A Stagewatch so marked is not followed (nor matched to a typed address) while the mark stands, a link it
  already has keeps running, and the mark clears 60 s after the last alternation, when a Stagewatch is followed again. A sync
  group, a media fetch, the fleet board and a backup still find a PlayMap id in conflict by its hello: that is not kept from them yet
  (an open fix, round 2's fix-showbus-1.3). A restart (A, then only B) is never one. Another process saying this node's own id
  (settings copied to a second machine) is marked at once, an amber row in STATE's `peers` ("two machines say they are a1b2c3d4:
  this node and RACK-2 at 10.0.0.7") and, in the context, an amber `bus:hello` item with the same words rather than a peer row. The
  context lists at most 32 peers (`context.peers`).

## 2. Being driven — what works with Patterns and Stagewatch as they are

| From | How | What it can do |
|---|---|---|
| **Stagewatch** | Settings → Integrations → Patterns: type PlayMap's host and port 9941 | Stagewatch's VT countdown follows PlayMap's focused playlist (STATE's `video` row is Patterns' own shape), and its `patterns end/restart/play/pause/stop/seek` commands drive it — PlayMap's VIDEO verbs mean exactly what Patterns' do, refusals included. The link pairs with PlayMap's pairing token by proof: `AUTH-PROOF` to the node's 32-hex `NONCE`, plain `AUTH` only on one machine, nothing taken before `OK paired` (Stagewatch 2.18). Stagewatch's playout links (2.20, planned) find PlayMap by its hello, `t:"playmap"`. |
| **Stagewatch** | Node link: PlayMap dials `/ws?node=<its id>&t=playmap&cid=<cid>&page=/playmap&detail=1` | A 2.18 Stagewatch lists PlayMap as a show app on its Show network, never a screen, and sends it no screen command; its first message is the verdict on PlayMap's key, read before anything keyed is sent (§3). From an older Stagewatch, which reads `cid` and `page` as a screen's, a `redirect` picks which Stagewatch view PlayMap shows as its *timer* source, `identify` and `reload` are one journal row each (nothing flashes on the outputs), and a `disconnect` holds the link 30 s. |
| **Stagewatch** | Triggers (HTTP POST or OSC) | A trigger calls `POST /api/cmd` on the node's web port with a verb line (`PLAYLIST GO show #3`) and the node's pairing token in `X-PlayMap-Token`: over `https://` (the same port speaks both) to the certificate the node's hello names, or from an address the node trusts (this machine by default). A node with a token refuses a call without it; its token sent on plain HTTP from another machine is still taken, and marked "seen in clear" in preflight (`links:token`, `PAIRING NEW`). Never put the token in a URL. Stagewatch's triggers send a secret only to this machine or a pinned HTTPS server from 2.20 (planned). |
| **Stagewatch approvals (§11)** | PlayMap posts its context and collects approved proposals aimed at its id, once Stagewatch's verdict pairs the link | An AI anywhere on the bus proposes a PlayMap verb; the show caller approves it on Stagewatch; PlayMap runs it through its own checks and reports the result. |
| **Patterns** | Interactive area → a *Lines* device at PlayMap's host:9941 | Every cue step, the wire's `DEVICE PlayMap …`, OSC and Companion can send PlayMap verbs; Patterns' fact lines (`BLACKOUT 1`, `LIVE 0`…) are taken in silently and **PlayMap follows Patterns' blackout** by default. Patterns' Lines devices do not pair by proof today: they send lines with no `NONCE` or `AUTH-PROOF`, so PlayMap takes them only from an address in its trusted list (`NodeSettings.TrustedAddresses`, an address, not an identity) or on a node that asks no token. Pairing by proof waits for Patterns' Show Bus node devices (§5, item 2). |
| **Patterns' AI / any AI** | `CONTEXT` on the wire, `GET /api/context` (behind the node's token), or MCP at `/mcp` | Reads everything PlayMap is doing (lights, headline, playlists, outputs, projectors, links, recent journal) and proposes verbs with their risk. TCP 9941 sends `STATE` on `HELLO` and answers `STATE` and `CONTEXT` before pairing: Show Bus 1.3 §8.3's named exemption, which ends once Patterns' node devices pair by proof. The direction (docs/round3/PLAN.md D13): the guides (two projectors on a flat screen, a film mapped onto a wall so it reads undistorted from the audience, an object with one or two projectors) become steps in STATE and the context, and an AI proposes a step's lines through the same gate as a person's lines, which decides by the line's risk, the operator's settings and Show Mode what waits for a person. |
| **Companion / any controller** | Companion: the PlayMap module (`integrations/companion-module-playmap`) on the web port's line channel (`/ws`, `playmap.lines.v1`), paired by proof; any controller: TCP 9941 lines, that channel, or HTTP | The full vocabulary; the module builds its keys from `VERBS`, STATE and every section of `INDEX` (docs/COMPANION.md). |

Never paste a Stagewatch window's address (`/w/<token>`), or any address that carries a pass, into PlayMap: `MEDIA IMPORT` refuses
a page, an embed and an address carrying a pass, and scrubs one a project kept. A window granted to another node's app travels
only as Show Bus 1.3 §12 says, which PlayMap does not hold or draw yet.

## 3. Following others

- **Patterns** (`LINK PATTERNS host[:9697] [token]`): PlayMap dials Patterns' wire like Stagewatch does
  (`HELLO PlayMap`, `AUTH-PROOF` from the pairing token, `PING` every 2 s for the clock offset, `CONTEXT` every
  5 s), reads Patterns' `video` row and blackout, and shows the link's light on its own context. It proves only to a `NONCE` of
  32 lowercase hex, sends plain `AUTH` only to this machine, takes nothing the desk says before `OK paired`, and asks the desk's
  STATE once paired.
- **Stagewatch** (`LINK STAGEWATCH host[:9924] [key]`): `/ws` state frames become a *timer* source PlayMap can map
  onto any surface (countdown, colour by phase, messages with urgency and flash, holding images, watermark);
  `{"type":"beat","live":false}` turns the timer to "?" instead of freezing a wrong time. The link is a node link (§2). Each dial
  tries TLS first and pins the certificate; a Stagewatch whose certificate is pinned or named by its hello is TLS or nothing. The
  key goes over plain ws only to this machine; off it the timer is read without the key (no context posted, no approvals
  collected). Stagewatch's verdict on the key is read first and nothing keyed goes before it; `links.stagewatch.paired` is that
  verdict (or a keyed 2xx from a Stagewatch that sends none). A refused key is held 60 s, or the `retry` Stagewatch gives (at most
  an hour), before any keyed call or dial, STATE's `links.stagewatch.state` `held` meanwhile; a 429's `Retry-After` is waited.

## 4. PlayMap to PlayMap

- **Sync groups**: a playlist with `syncGroup` follows that group's leader. Followers keep the leader's clock with
  a nanosecond NTP-style exchange on UDP 9942 (min-RTT filtering plus drift); the leader sends each transport
  change as a timeline anchor on its anchor feed (`SUBSCRIBE ANCHORS` on its wire), so every node computes the same
  frame for the same instant. Commands can carry a start instant (`PLAYLIST GO show AT +250`) so a group starts at one
  show instant. The leader's `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER` ride the same feed, each answered with a
  member's receipt, and members report their vblanks to the leader's board (docs/SYNC.md).
- **Media**: library items are identified by SHA-256; a node that lacks a file pulls it from any peer that has it
  (`MEDIA FETCH ALL`), in resumable 8 MiB ranges, verified by its hash before it lands. On the exe, which composes the
  fleet's part, the pull goes with the peer's proof over `/mirror/media` and is not throttled while a show plays (an open
  fix, docs/ROADMAP.md); a node without that part pulls over the peer's `/api/media`, throttled while a show plays
  (docs/WIRE.md). STATE says which files are present, verified and decodable on each node.

## 5. Asked of Show Bus 1.3, and answered

1. **Node kind `playmap`** — adopted: §14's `kind playmap PlayMap`, every cap PlayMap sends a `cap` row that names it (`playlist`,
   `media`, `mapping`, `scan`, `eye` and `show` joined the vocabulary), and §8.3, PlayMap's row.
2. **Patterns: hello → device** — Patterns' own work, not the contract's. Patterns' device links do not speak `NONCE` /
   `AUTH-PROOF` today, so PlayMap takes their lines by address only (§2). Patterns' Show Bus node devices (planned) add a heard PlayMap
   as a device that pairs by proof; with them §8.3's named exemption ends, and 9941's `STATE` and `CONTEXT` wait for pairing on a
   node with a token.
3. **Patterns: read third-party contexts** — Patterns' own work, with item 2: a PlayMap node's context read over its line wire
   (`SUBSCRIBE CONTEXT`), since its `/api/context` stays behind the node's token; not built yet ([FLEET.md](FLEET.md)'s P2).
4. **Patterns: a destination** — not the contract's: a screen in Patterns' plan whose picture a PlayMap node makes is Patterns'
   plan to make.
5. **Stagewatch: find PlayMap for VT** — Stagewatch's playout links (2.20, planned) find `t:"playmap"` hellos; until then its
   Patterns integration reaches PlayMap at a typed host and 9941 (§2).
6. **Timeline anchors on the bus** — reserved: §14's `reserved video.anchor playmap`, which becomes a `cap` row once a second kind
   reads it; the `ANCHOR` line waits for a second reader.
7. **The Eye's command cap** — adopted: §6's `context.commands` 256, the least a reader that keeps commands keeps, and §6's trimming
   order, which PlayMap's own order (the panic, air, transport and Show Mode rows first) already follows. Patterns' reader keeps 256
   since its round 91 (docs/round3/PLAN.md §9 decision 18).
