# PlayMap on the Show Bus — how it talks to Patterns, Stagewatch and an AI

PlayMap implements Show Bus 1.2 (the contract whose master copy lives in Stagewatch's `docs/SHOWBUS.md`) as a
new node kind, `playmap`. This page is what PlayMap does on the bus today, what works with Patterns and
Stagewatch **unchanged**, and the small additions to those apps (proposed as Show Bus 1.3) that would make a
PlayMap node first-class on their desks.

## 1. Being seen

Every 2 s a hello to UDP 9928 (broadcast and loopback; never on Patterns' own beacon, 9700):

```json
{"t":"playmap","v":1,"proto":1,"id":"a1b2c3d4","name":"Stage left","host":"RENDER-01","web":9940,"wire":9941,
 "ports":{"http":9940,"https":9940,"wire":9941,"ws":9940},
 "caps":["video","video.clock","video.transport","video.seek","video.end","state","context","receipts","clock","mcp",
         "playlist","media","mapping","scan"],
 "clock":1760101234567,"inst":"0badc0de","tls":"sha256:…"}
```

- `t` is neither `patterns` (Patterns hides other Patterns) nor `main` (Patterns would dial it as Stagewatch).
- Stagewatch's strict reader is honoured: ≤ 2048 bytes, `v` 1, `t` ≤ 16 and `id` ≤ 40 characters, ≤ 16 caps.
- Top-level `web` and `wire` ride beside `ports` for older readers.
- It shows up today as a grey "playmap" node on Stagewatch's Show network and on Patterns' Nodes page and Eye.

PlayMap listens on 9928 too: it lists every node, follows the Patterns desk and the Stagewatch it is pointed at
(or the first one heard), and finds other PlayMap nodes for sync groups and media transfer.

## 2. Being driven — what works with no change to Patterns or Stagewatch

| From | How | What it can do |
|---|---|---|
| **Stagewatch** | Settings → Integrations → Patterns: type PlayMap's host and port 9941 | Stagewatch's VT countdown follows PlayMap's focused playlist (STATE's `video` row is Patterns' own shape), and its `patterns end/restart/play/pause/stop/seek` commands drive it — PlayMap's VIDEO verbs mean exactly what Patterns' do, refusals included. |
| **Stagewatch** | Screens: PlayMap connects to `/ws` with `cid=<node id>&page=/playmap` | PlayMap appears in Stagewatch's Screens list; `redirect` / `identify` / `reload` arrive and pick which Stagewatch view PlayMap renders as its *timer* source. |
| **Stagewatch** | Triggers (HTTP GET/POST or OSC) | Any trigger can call `POST http://<node>:9940/api/cmd` with a verb line (`PLAYLIST GO show #3`). |
| **Stagewatch approvals (§11)** | PlayMap posts its context and collects approved proposals aimed at its id | An AI anywhere on the bus proposes a PlayMap verb; the show caller approves it on Stagewatch; PlayMap runs it through its own checks and reports the result. |
| **Patterns** | Interactive area → a *Lines* device at PlayMap's host:9941 (add PlayMap's address to its trusted list, or leave the node open) | Every cue step, the wire's `DEVICE PlayMap …`, OSC and Companion can send PlayMap verbs; Patterns' fact lines (`BLACKOUT 1`, `LIVE 0`…) are taken in silently and **PlayMap follows Patterns' blackout** by default. |
| **Patterns' AI / any AI** | `CONTEXT` on the wire, `GET /api/context`, or MCP at `/mcp` | Reads everything PlayMap is doing (lights, headline, playlists, outputs, projectors, links, recent journal) and proposes verbs with their risk. |
| **Companion / any controller** | TCP 9941 lines or HTTP | The full vocabulary. |

## 3. Following others

- **Patterns** (`LINK PATTERNS host[:9697] [token]`): PlayMap dials Patterns' wire like Stagewatch does
  (`HELLO PlayMap`, `AUTH-PROOF` from the pairing token, `PING` every 2 s for the clock offset, `CONTEXT` every
  5 s), reads Patterns' `video` row and blackout, and shows the link's light on its own context.
- **Stagewatch** (`LINK STAGEWATCH host[:9924] [key]`): `/ws` state frames become a *timer* source PlayMap can map
  onto any surface (countdown, colour by phase, messages with urgency and flash, holding images, watermark);
  `{"type":"beat","live":false}` turns the timer to "?" instead of freezing a wrong time.

## 4. PlayMap to PlayMap

- **Sync groups**: a playlist with `syncGroup` follows that group's leader. Followers keep the leader's clock with
  a nanosecond NTP-style exchange on UDP 9942 (min-RTT filtering plus drift); the leader sends each transport
  change as a timeline anchor, so every node computes the same frame for the same instant. Commands can carry a
  start instant (`PLAYLIST GO show AT +250`) so a group starts on one vblank.
- **Media**: library items are identified by SHA-256; a node that lacks a file pulls it from any peer that has it
  (`MEDIA FETCH ALL`), in resumable 8 MiB ranges, throttled while a show plays; STATE says which files are
  present, verified and decodable on each node.

## 5. Proposed for Show Bus 1.3 (changes to Patterns and Stagewatch, not made by PlayMap)

1. **Node kind `playmap`** in the contract's table, with the caps above (`playlist`, `media`, `mapping`, `scan`
   join the vocabulary).
2. **Patterns: hello → device.** A heard `playmap` node offers "Add as a device" on the Nodes page, filling the
   Lines profile with its wire port and pairing by proof (Patterns' device links speak the same `NONCE` /
   `AUTH-PROOF` the wire does).
3. **Patterns: read third-party contexts.** Fetch `GET /api/context` from every heard node with the `context`
   cap (today only Stagewatch's), so the Eye draws PlayMap's outputs, projectors and links.
4. **Patterns: a destination.** A screen in Patterns' plan whose picture is made by a PlayMap node: Patterns sends
   the content (an NDI feed it already makes, or a media id the node already has) and the node maps it; the
   node's output health comes back as that screen's light.
5. **Stagewatch: find PlayMap for VT.** Its Patterns finder accepts `t:"playmap"` hellos (it already reads a
   top-level `wire`).
6. **Timeline anchors on the bus.** A `video.anchor` cap and an `ANCHOR` push line (media time at a leader instant,
   rate, seq) so any node can follow a clip frame-accurately, not just within Patterns' 1.5 s re-sync window.
