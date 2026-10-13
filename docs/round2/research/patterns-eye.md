# Research: Patterns' God's Eye, its show safety, and how a node joins Patterns' picture

Date of research: 13 October 2026. Local study only (no web). Legend: **[V-local]** read in the named file this
session; **[I]** inferred from the code, not run. Repositories (read-only): Patterns `/home/user/patterns-svc`,
Stagewatch `/home/user/jammin808/stagewatch`, PlayMap `/home/user/mapper` (main, c12b186).

Files read: Patterns `src/Patterns.Core/Services/Eye.cs`, `EyeJson.cs`, `EyeReplay.cs`, `ShowBus.cs`,
`ShowBusNetwork.cs`, `ShowContext.cs`, `StagewatchFollow.cs` (`ShowBusView`), `Twin.cs`, `LivePolicy.cs`,
`Commissioning.cs`, `SuperCheck.cs`, `HealthDashboard.cs`, `Administration.cs` (`MetricSample`), the EYE case in
`ControlProtocol.cs`, `src/Patterns.App/Services/StagewatchLinkService.cs`; docs `EYE.md`, `SHOW-MACHINE.md`,
`DRILL.md`, `PLAN.md` §56, §64, §83. Stagewatch `docs/SHOWBUS.md` §6–§11, `docs/FEATURES.md`, `docs/ECOSYSTEM.md`,
`src/Timer.Remote/WebServer.cs`. PlayMap `docs/SHOWBUS-NODE.md`, `src/PlayMap.Engine/ContextBuilder.cs`,
`src/PlayMap.Core/Bus/ShowContext.cs`, `src/PlayMap.Net/Bus/PeerTable.cs`, `src/PlayMap.Net/Http/HttpHost.cs`.

---

## 1. The Eye's data model

### 1.1 Facts in, graph out

- The desk gathers **`EyeFacts`** once a second (hashed; rebuilt only when the hash moves) and hands them to the pure
  core: `EyeGraph.Build(EyeFacts)`. The core never reaches for a service. [V-local] `Eye.cs` l.66–135, `docs/EYE.md` §3–§4
- **Things** (`EyeNode`): `Id` (stable), `Kind` (21 kinds: Desk, Source, Screen, Display, NdiSend, FarEnd, Device,
  Deck, Companion, Peers, Osc, Stack, Assistant, Twin, Node, AudioSource, AudioOut, Room, Stream, **ShowApp**,
  **ShowAppItem**), `Plane`, `Tier`, `Label`, `Sub` (the state line), `Light`, `Words` (facts as words), `Route`
  (the rail page that shows it), `MenuKind`/`MenuSubject` (whose right-click menu it gets). [V-local] `Eye.cs` l.20–42, l.243–252
- **Links** (`EyeEdge`): `From`, `To`, `Kind` — exactly 11 verbs: **feeds, shows, drives, carries, controls, mirrors,
  follows, hears, routes, serves, sends** — `Light` and `Words`. [V-local] `Eye.cs` l.45–58, l.254–259
- **Lights** (`CheckLight`): grey, green, amber, red. **The evidence rule:** a light is the worst thing *known*,
  never a guess; *unverified is grey* ("not verified" is not a failure and not a pass); a thing that stopped being
  heard is red. [V-local] `docs/EYE.md` §3

### 1.2 Planes, tiers, layout

- Four **planes** (bands, top to bottom): **Control** (decks, Companions, wire/web clients, OSC, the cue stack, the
  assistant, the twin, other nodes and show apps), **Video** (sources → desk → screens → displays/NDI → far end),
  **Audio** (sources → outputs), **Room** (phones, stream). **Tiers** are columns left to right in the direction the
  signal travels. [V-local] `docs/EYE.md` §3
- Layout is a **deterministic grid** by (plane, tier), O(n), stable order — "the operator wants the projector where it
  was a minute ago"; a force layout was rejected. [V-local] `docs/EYE.md` §3, §6

### 1.3 Focus, lenses, the headline, problems

- **Focus**: selecting a thing fits the camera to it and its neighbours; emphasis by hop distance — **1.0 at 0 and 1
  hop, 0.55 at 2, floor 0.25** — smoothed **300 ms attack, 600 ms release**; hops do not travel through the desk hub.
  The camera is a critically damped spring on offset and log-scale. [V-local] `docs/EYE.md` §3, §6
- **Lenses**: ALL, VIDEO, CONTROL, AUDIO, ROOM, **PROBLEMS** (every red and amber thing with its neighbours). [V-local] `Eye.cs` l.61–69
- **Headline**: one line from the lights — the count of things, reds and ambers, the worst thing named with its
  words. **Problems**: reds then ambers in picture order; `EYE NEXT` / `EYE PREV` walk it. [V-local] `docs/EYE.md` §3
- **View as words**: `EYE FOCUS <id|"screen 2"|label>`, `EYE LENS <name>`, `EYE RESET`, `EYE NEXT|PREV`; desk-only
  (never from a running order); RESET restores the view from before the focus. [V-local] `ControlProtocol.cs` l.1186–1206

### 1.4 Replay

- `EYE REPLAY [ON|OFF|<time>]` opens the record on the page; `EYE AT <time>` answers the picture at an instant as
  JSON without moving the page. The record is the journal's rows plus the **metrics file's one-second samples**
  (`MetricSample`, unknown = −1); the **structure is the Eye of now**, relit from the record: a thing shows the outcome
  of rows that named it in the **30 s window** before the instant, a sample older than **90 s** no longer speaks, and a
  thing the record does not mention is **grey and says so** ("attempts are not facts"). [V-local] `EyeReplay.cs` l.1–135; `Administration.cs` l.148–180

### 1.5 On the wire

- `EYE` / `EYE STATUS` → `EyeJson`: `headline, worst, worstLight, worstWords, counts{green,amber,red,grey,things,links},
  focus, lens, problems[], nodes[{id,kind,plane,tier,label,sub,light,x,y,w,h,words,page,item,wire:"EYE FOCUS <id>"}],
  edges[{from,to,kind,light,words}], replay, replayAt, moment`. STATE carries an `eye` row (headline, worst light,
  counts). The Companion module 3.7.0 has an Eye page: actions, an `eye_worst` feedback, `eye_headline`, `eye_worst`,
  `eye_problems` variables. [V-local] `EyeJson.cs`; `docs/EYE.md` §3, §6
- Hello caps: `video video.clock video.end video.seek video.transport state eye context receipts clock`. [V-local] `ShowBus.cs` l.25

## 2. How the Eye learns about bus peers

| Path | What Patterns does | What it draws | Source |
|---|---|---|---|
| **Show Bus hello** (UDP 9928) | Every heard peer whose kind is not `patterns` becomes an `EyeShowApp` (`ShowBusView.Apps`) | A **ShowApp** thing on the **Control plane, tier 2**, label `"{KindWords} {Name}"`, sub "heard · host · address · first 6 caps"; light **grey** when heard and not linked, **amber** stale, **red** gone; a grey `hears` link to the desk | [V-local] `StagewatchFollow.cs` l.196–245; `Eye.cs` l.771–796 |
| **The linked Stagewatch's §6 context** | `StagewatchLinkService` fetches `GET /api/context` **every 5 s** with its `swk_` key, and posts Patterns' own context to `/api/peers/context` | The ShowApp takes **the context's light and headline**; up to **12 of its items** (reds first, then ambers, then the context's order) become **ShowAppItem** things on the **Control plane, tier 3**, ids `app:<id>:<item id>`; links are drawn **only between two drawn items** and **only when the link kind parses as one of the 11 Eye verbs**; items left unlinked get a grey `serves` link from the app | [V-local] `StagewatchLinkService.cs` l.347–352; `Eye.cs` l.797–819 |
| **Patterns nodes over the twin** | A caller, timer or arcade node sends `CONTEXT` on the twin link every 5 s | Each is an app "Patterns node" with its context drawn as above | [V-local] `Twin.cs` l.49–50; `StagewatchFollow.cs` l.229–236 |
| **The beacon** (UDP 9700) | Other Patterns instances | `EyeNodeHeard` things (Control, tier 2), green linked / grey heard / red gone quiet | [V-local] `Eye.cs` l.757–766 |
| **Interactive devices** | A box the desk drives (incl. a *Lines* device) | `device:<id>` (Control, tier 2, `desk drives device`), or **FarEnd** on the Video plane (tier 4) with a `carries` link from a screen's display when the device has an `InputScreen` | [V-local] `Eye.cs` l.654–676 |
| **Decks** (HELLO on the wire), **Companion** (mDNS) | Controllers | Deck (green paired / amber not paired — "its keys do nothing"), Companion (grey heard) | [V-local] `Eye.cs` l.678–700 |

**Context bounds** (`ShowContext.Parse`): ≤ 512 KB; items ≤ 200; links ≤ 400; recent last 20; **commands ≤ 64**;
peers ≤ 32; unknown lights are grey. [V-local] `ShowContext.cs` (Patterns) l.86–107

**`KindWords`**: `main`→"Stagewatch", `agent`→"Slides agent", `patterns`→"Patterns", `messenger`→"AGMessage", else the
kind with its first letter upper-cased — so PlayMap is drawn as **"Playmap"**. [V-local] `ShowBusNetwork.cs` l.167–175

## 3. What PlayMap publishes today, and how it shows up

- **Hello** `t:"playmap"` every 2 s with caps `video video.clock video.transport video.seek video.end state context
  receipts clock mcp playlist media mapping scan` (14 of the 16 a strict reader takes). [V-local] `docs/SHOWBUS-NODE.md` §1
- **Context** at `GET /api/context` (open read: `HttpHost.cs` l.273) and `CONTEXT` on the wire, in the §6 shape.
  Items today: `air` (only when blacked out or a pattern is up), `playlist:<id>`, `output:<id>`, `projector:<id>`,
  `media`, `link:patterns`, `link:stagewatch`, `sync` (followers only), `scan` (while running), `fault:<part>`, plus the
  ops items (devices, assistant). Links: `output → projector "drives"`, `link:patterns → air "controls"` (dropped by the
  Eye whenever `air` is absent). `commands` lists **every** mutating verb (≈ 75; Patterns keeps the first 64).
  [V-local] `ContextBuilder.cs`; `ShowContext.cs` (PlayMap) `CommandsFromCatalog`
- PlayMap's `StagewatchLink` **posts its context** to a linked Stagewatch's `/api/peers/context`. [V-local] `StagewatchLink.cs` l.497–501

**Result with Patterns unchanged:**
1. A grey ShowApp **"Playmap Stage left"**, "heard · RENDER-01 · 10.0.0.21 · video, video.clock, …" — no light of
   its own, no things inside: PlayMap is not a linked Stagewatch, so its context is never read.
2. If Patterns is linked to a Stagewatch that PlayMap also posts to, the **Stagewatch card's words** carry one line
   "hears Playmap Stage left — green: Show · playing Intro, 2:10 left · 3 outputs green" (its context's `peers`, ≤ 6
   shown). [I] from `Eye.cs` l.789–792
3. If the operator adds PlayMap as an Interactive **Lines** device (docs/SHOWBUS-NODE.md §2), a second thing
   `device:<id>` appears, lit by the device link (green answering, red closed). With its *Input screen* set to the
   Patterns screen that feeds PlayMap (an NDI send, a capture), it becomes a **FarEnd** on the Video plane with a
   `carries` link from that screen's display. [I] from `Eye.cs` l.654–676

## 4. Exactly how PlayMap should appear

### 4.1 What PlayMap changes (its own side; no Patterns change needed for any of it)

1. **Order and budget the items for a 12-item window.** Patterns draws at most 12 items (reds, then ambers, then the
   order given). PlayMap's `ContextBuilder` emits in this order, aggregating so the important greens survive:
   `show` (armed / rehearsal / panic / blind — always present, so links to it are never dropped), `machine` (this
   node: role leader/follower/backup, build, CPU/GPU, render path), `output:<id>` (≤ 6 individually; above 6 one
   `outputs` aggregate plus every non-green output individually), `projector:<id>` (with its device link's power,
   shutter, input and light hours folded into the item's words — one item per projector, not two), `playlist:<id>`
   (playing ones first; idle ones folded into one `playlists` aggregate), `mirror:<node>` / `backup` (the mirror
   relation), `sync:<group>`, `media`, `preflight`, `link:*`, `fault:*`. Every item keeps its full detail in STATE;
   the context is the Eye's window. **All items** remain in the context (Patterns' assistant brief reads up to 200).
2. **Links only in the Eye's 11 words**, and only between items that are both likely to be drawn:
   - `playlist:<id>` **feeds** `output:<id>` (the output's canvas has a layer whose source is the playlist, or a surface
     it projects shows the playlist) — derived from the project;
   - `output:<id>` **drives** `projector:<id>` (exists);
   - `show` **controls** `output:<id>` (the air: blackout, panic, master) — replaces today's `link:patterns → air`;
   - `link:patterns` **controls** `show`; `link:stagewatch` **feeds** `show` (its timer as a source);
   - `machine` **follows** `sync:<group>` (follower) or `sync:<group>` **follows** `machine` (leader);
   - `mirror:<node>` **mirrors** `output:<id>` (a backup renders this output); `machine` **mirrors** `mirror:<node>`.
   - **Correction for the framelock design** (`design/framelock.md` §4.5): its link kinds `member`, `locked` and
     `free` are not Eye verbs and Patterns drops them silently — use `follows` (member / locked, lit by state) and
     `hears` (free-running); and Patterns does **not** merge items by id across apps (ids are prefixed
     `app:<node id>:`), so the hub `sync:<group>` appears once **per node** that publishes it. Each node should publish
     its own `sync:<group>` item; the leader's carries the group verdict.
3. **Kinds that read well**: item `kind` is shown in the thing's words (`"PlayMap Stage left: output"`). Use
   `show`, `machine`, `output`, `projector`, `playlist`, `media`, `sync`, `mirror`, `link`, `preflight`, `fault`.
4. **Headline from facts** (exists) — extend with the fleet: `"ARMED · Show · playing Intro 2:10 left · 6 outputs green
   · backup FOH-B hot, same frame"`.
5. **`commands` trimmed to what a peer may propose**: the 64 most useful, transport and air first (the full list stays at
   `/api/verbs` and MCP), so Patterns' cap of 64 never cuts PANIC or SHOW off.
6. **`peers` one hop**: the other PlayMap nodes this node hears with **their own** light and headline (read from
   their `HEALTH`, §5), so even a Patterns that reads only one PlayMap's context sees the whole fleet as word lines.
7. **An `eye` row in STATE in Patterns' shape** (`headline, worst, worstLight, worstWords, counts, problems`) and the
   `EYE` read verbs (`EYE`, `EYE STATUS`, `EYE AT`) answering `EyeJson`'s shape for PlayMap's own picture — so a tool
   written for Patterns' Eye JSON reads PlayMap's too. The view verbs (`FOCUS`, `LENS`, `NEXT`, `PREV`, `RESET`) move
   the PlayMap desk's fleet board.

### 4.2 The small Patterns-side changes (proposed, not made by PlayMap — Show Bus 1.3, SHOWBUS-NODE §5)

| # | Change | Size | Why |
|---|---|---|---|
| P1 | `ShowBusPeers.KindWords("playmap") => "PlayMap"` | 1 line | the name reads right |
| P2 | **Read the context of every live peer with the `context` cap** (not only the linked Stagewatch): a small service beside `StagewatchLinkService` polls `GET http(s)://<address>:<ports.http>/api/context` every 5 s (2 s timeout, ≤ 8 peers, TLS pinned by the hello's `tls` like the Stagewatch link), and `ShowBusView.Apps` attaches it as `EyeShowApp.Context`. PlayMap's context is an open read, so no key is needed | ~120 lines + tests | PlayMap's things, lights and links appear on the Eye, and its red/amber items join the problems queue |
| P3 | (optional) **Plane by item kind**: a ShowAppItem whose kind is `output`, `projector`, `surface`, `playlist` or `media` sits on the **Video** plane (tier 3/4) instead of Control | ~10 lines | PlayMap's outputs and projectors sit beside Patterns' screens and displays |
| P4 | (optional) **Merge a Lines device with its ShowApp** when the device's address and port match a heard app's wire | ~20 lines | one PlayMap, one thing |
| P5 | (optional, already proposed) "Add as a device" from the Nodes page for a heard `playmap` (SHOWBUS-NODE §5.2) | small | pairing by proof without typing |

Nothing else is needed: the lights, the words, the links and the problems come from PlayMap's context in the
contract's shape.

## 5. What PlayMap's fleet health view should borrow

| From Patterns | In PlayMap's fleet board |
|---|---|
| **One picture, facts in, pure graph out**, rebuilt only when a hash moves; a selection is a field write | `FleetGraph.Build(FleetFacts)` in Core, pure and tested; the desk animates only |
| **The evidence rule** (unknown is grey; unverified is not a pass; silence is red) | a node not heard is red "gone quiet"; a metric not read is −1 and grey, never 0 |
| **Planes and tiers, deterministic grid** | rows by machine; columns in signal order: inputs/links → machine → outputs → projectors |
| **Focus with hop de-emphasis** (1.0 / 0.55 / 0.25, 300/600 ms) and **RESET to the previous view** | focus a machine or an output; the rest dims |
| **Lenses** incl. **PROBLEMS** | ALL, OUTPUTS, PROJECTORS, SYNC, BACKUP, PROBLEMS |
| **Headline + problems queue** with NEXT/PREV on keys and from Companion | the desk header's readiness light wears the worst light; NEXT/PREV PROBLEM keys |
| **HEALTH AT A GLANCE** — 12 tiles from the super-check facts and the one-second sample, unknown = −1 | per-machine tiles: outputs, render, CPU, memory, GPU, disk, network, sync, devices, media, watchdog, power |
| **Replay** (journal + 1 s metrics, 30 s window, 90 s stale) — `EYE AT <time>` | `FLEET AT <time>`: the board relit from each node's journal and a one-minute metrics ring |
| **Menus stay one** (a thing's right-click is its own desk menu) | a machine card's menu is the machine's verbs; an output tile's is the output's |
| disguise-style categories (from the industry report) | columns Connection, Output, Genlock(sync), Devices |

## 6. Patterns' show-safety features PlayMap should mirror (so the ecosystem feels like one product)

| Patterns | Mirror in PlayMap | Note |
|---|---|---|
| **The armed desk** — a pure table of what does not start while armed and live; words say what lifts it ("DISARM first (or OUTPUTS OFF), then again"); content is never refused | `ShowPolicy` with the same shape of words: "Not while armed: … SHOW DISARM first, or BLIND to stage it." | PlayMap's ARM is explicit (no "live" condition needed), but the words, the table-not-a-mode idea and "content is never refused for the operator" are the same |
| A plan that would replace the running stack **waits for DISARM** | An approved proposal or a remote edit refused while armed can be **parked** ("waits for DISARM") and lands on DISARM unless superseded | should |
| **300 ms lockout**, **standby-id fence** | `MinGoMs` (300 ms armed default) and `EXPECT <item>` on GO: a key that shows "Next: Intro" refuses if the next item changed under it | the fence protects Companion keys rendered a second ago |
| **STOP ALL** (two-press on the phone) | PANIC on a dedicated key; on the phone two presses (UX §8) | panic outcomes are PlayMap's addition |
| A relaunch restores the caller's place **disarmed** | PlayMap restores **armed** — PlayMap's ARM is a *restriction*, so the safe side is armed; Patterns' arm *enables* execution, so its safe side is disarmed | state the difference in the papers |
| **Show lock** (OS distractions held off; crash receipt) | not in this round's packages (Windows-specific); preflight reads the same items read-only (pending update, sleep, notifications) | later |
| **Super Check** rows with FIX words; **ShowReady** bar (5 steps); **Commissioning** (7 stages) | `PREFLIGHT` rows with **fix verbs**; a five-step show-ready bar (outputs, media, mapping, sync/backup, safety) | the same rows feed the context |
| **Rig day** (opt-in, quiet during a live show, rewards only real measures) | out of scope here | |
| **Twin**: MAIN/STANDBY, proved key, beat a second (5 missed = gone), wall switch with **confirmation level**, ownership record, **TAKE OVER / TAKE BACK**, never two pictures | fleet: backup roles, `FAILOVER` (take over) and `FAILBACK` (take back, only by a press), route steps with **confirm levels** (sent/accepted/observed), **epoch fencing**, the drill's acceptance rule | the drill's 22 scenarios are the model for PlayMap's failover tests |
| **One approval queue** on Stagewatch (§11): proposals posted with the target's own risk; delivery pulled by the target; the target's own checks still apply | turn on the Stagewatch proposal sink; **one** collector of approved lines (the link, `CollectApproved=false` on the sink); an approved line runs through the armed table as an *approved* caller | carry-over item |

## 7. Design implications for PlayMap

1. **No Patterns change is required for PlayMap to be correct; one small change (P2) makes it first-class.** Until P2
   lands, PlayMap shows as a grey ShowApp and as word lines through Stagewatch's peers.
2. PlayMap's context must be **designed for a 12-thing window**: `show` always present, reds and ambers individual,
   greens aggregated, devices folded into projectors, links only in the **11 Eye verbs** and only between items that
   will be drawn. Fix the framelock spec's `member/locked/free` link kinds.
3. The fleet board should **be an Eye**: pure graph from facts, evidence lights, deterministic grid, focus, lenses,
   problems queue, replay, menus that are the things' own — and its JSON should be **shape-compatible with `EyeJson`**.
4. PlayMap's show safety should **use Patterns' words and shapes**: the table with "what lifts it", the 300 ms lockout,
   a GO fence, a parked edit that waits for DISARM, the twin's confirmation levels and "never two pictures".
5. The approval desk decision (carry-over): enable the Stagewatch proposal sink with `CollectApproved=false`; keep the
   link as the single collector; run approved lines as role `approved` through the armed table.
