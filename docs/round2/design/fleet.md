# Work package `fleet`: redundancy, controller-death continuity, fleet health and the Patterns Eye link

Research: `research/safety-industry.md` (cited **[S§n]**), `research/patterns-eye.md` (**[E§n]**). Code read on main
(c12b186): `Core/Time/ClockOffsetEstimator.cs` (`NetworkClock`), `Core/Bus/{ShowContext,Pairing}.cs`,
`Engine/{EngineCore,EngineCore.Sync,ContextBuilder,StateWriter,NodeHome,Seams}.cs`,
`Engine/Playback/PlaybackLanes.cs` (lanes switch items locally at `SwitchAtLeaderNs`),
`Engine/Host/{NetworkHost,EngineParts,EngineExtension}.cs`, `Net/Bus/PeerTable.cs`, `Net/Sync/ClockSync.cs`,
`Net/Links/{WireClient,PatternsLink,StagewatchLink}.cs`, `Net/Http/HttpHost.cs`, `Devices/Drivers/IProjectorDriver.cs`,
`Devices/Commands/CommandResult.cs` (`CommandStage`), `Engine/Devices/DeviceService.cs`,
`tests/PlayMap.Engine.Tests/Host/HostRig.cs`. Patterns: `Eye.cs`, `EyeJson.cs`, `EyeReplay.cs`, `Twin.cs`,
`StagewatchFollow.cs`, `HealthDashboard.cs`, `docs/DRILL.md`. Other specs: `framelock.md` (F1 anchor feed and
persisted sync role, F2 slewed clock and `Stepped`), `showsafety.md` (`ProjectDiff`, `IFleetFanout`, `PanicLeadMs`,
`IPreflightCheck`, pending commits), `companion.md` (`STATE.fleet`, fleet template), `facade.md` (stack failover is
in-node; cross-node later), `effects.md` ("breaks mirror backup" badges).

Carry-over items folded in: the **follow role persisted** (owned by framelock F1; this package persists the backup
role and epoch beside it); **authenticated media pulls** (`FETCH` sends no token, plain HTTP) — a mirror pulls every
file the project names by hash, so pulls carry the peer proof and use HTTPS with the pin when offered.

---

## 1. Goal and the bar

When a machine, a cable, a desk or a controller fails in front of an audience, the picture stays up, the operator
knows within a second what failed and what the fleet did about it, and editing goes on. And Patterns' God's Eye draws
PlayMap's machines, outputs and projectors as things with lights and links.

| The best of the field | Who | PlayMap matches by | PlayMap beats it by |
|---|---|---|---|
| Producer closes or crashes, the show plays; Runners keep rendering when the Director is lost; renderers hold their last state [S§4] | Watchout 7 | The desk is a separate process (exists); followers and mirrors **free-wheel** on the leader's last offset and drift and keep switching items at the same instants (the lanes compute switches locally) | A **named succession** promotes the next node to lead **with the show clock continuing the dead leader's line** (no step), so new commands work again within seconds; Watchout's Runners only hold what they have |
| Understudy takes over on a timeout (0 = off), by a press, a transport command or an API; matrix preset sent; failed machine ignored [S§6] | disguise | `FAILOVER` (press, verb, Companion, API), `TimeoutMs` (0 = off), route steps, epoch fencing | **Editing continues after failover** — disguise stops live update for every machine when its Director fails over [S§6]; PlayMap's backup *is* a full node with the show, so it simply becomes the primary |
| Full 1:1 mirror plays along; usage presets; matrix switched by a "System Lost" action; automatic only for fixed installs [S§5] | Pixera | HOT/WARM/COLD backups; `Manual` failover by default, `Auto` opt-in | **Mirroring by construction**: the backup takes the primary's project revision, anchors, air and blind commits **each with its leader instant**, and a measured **mirror lead** makes every live change land on the same vblank on both — so blackout and panic are frame-identical too. A 4 Hz **frame key** proves it, live |
| Twin: a beat a second, five missed = gone; take-over through a wall switch whose **confirmation level** decides whether automatic takeover may proceed; never two pictures [S§10] | Patterns | Route steps answered with `CommandStage` (sent, accepted, observed); auto takeover only at the route's required level; the route is the fence | Per-output failover (only the output whose window died switches) and the projector's own seamless backup input used as a first-class route |
| Projector switches seamlessly to its backup input on loss of signal when both carry the same signal [S§8] | Panasonic | `PROJECTOR-AUTO` route step: PlayMap keeps the backup **HOT and identical**, observes the switch and reports it | Preflight checks the precondition (same raster and timing; HOT; framelock state) so the seamless switch is designed, not hoped for |
| Fleet health: Pixera Hub (CPU/GPU/RAM per server) [S§5], disguise's four health categories and per-actor fps [S§6], Patterns' HEALTH AT A GLANCE (12 tiles, unknown = −1) [E§5] | Pixera, disguise, Patterns | `HEALTH` per node, `FLEET` board on every node, the desk's fleet board and a `/fleet` web page | The board **is an Eye** (lights by evidence, problems queue, lenses, focus, replay), its JSON is **shape-compatible with Patterns' `EyeJson`**, and every PlayMap node appears in Patterns' own God's Eye |

## 2. Scope this round

### Must

| # | Item | Acceptance (tests in this container unless marked) |
|---|---|---|
| M1 | **Health**: `HealthRecord` (Core), `MachineSampler` (Linux real; Windows compile-only), `HEALTH` verb, `GET /api/health`, `SUBSCRIBE HEALTH` (1 Hz + on light change), unknown = −1 and grey | A node's HEALTH has every category; CPU/memory/disk non-negative on Linux; GPU and temperature −1 and grey here; a red output in STATE makes `output.light` red within one push |
| M2 | **Fleet monitor**: subscribes to every live PlayMap peer's HEALTH (open read; TLS pinned when offered), ≤ 32 peers, latest-wins, stale 3 s / gone 6 s; `FLEET` verb, `GET /api/fleet`, STATE `fleet`; context `peers` with real lights | Three `HostRig` nodes: each `FLEET` lists 3 machines with their outputs; disposing one → its card `stale` within 3.5 s and red `gone` within 6.5 s; it never vanishes silently |
| M3 | **`FleetGraph`** (Core, pure): things, links in the Eye's 11 verbs, deterministic grid, headline, problems queue, lenses; `FLEET FOCUS/LENS/NEXT/PREV/RESET` (view verbs); STATE `eye` row in Patterns' shape; `EYE` aliases | Golden JSON for a 3-node fixture; the same facts → the same ids and places; PROBLEMS lens holds reds and ambers and their neighbours; `EYE STATUS` parses with a reader written against Patterns' `EyeJson` field names |
| M4 | **Metrics ring**: 1-minute aggregates per node for 24 h (own and every monitored peer), `fleet.metrics.jsonl` rotated daily (7 days) | ManualClock: 1 440 samples kept, the 1 441st drops the oldest; the file round-trips |
| M5 | **The context for Patterns' Eye** [E§4.1]: items ordered and budgeted for a 12-thing window (`show` and `machine` always first, greens aggregated, devices folded into projectors), links only in the Eye's 11 verbs and only between likely-drawn items, `commands` trimmed to 64 (transport, air, panic first), `peers` with real lights and headlines | A 20-output fixture: the first 12 items in Patterns' order (reds, ambers, then given order) contain `show`, `machine`, every non-green output, the playing playlists; every link kind ∈ the 11 verbs; a test parses the context with a copy of Patterns' `ShowContext.Parse` bounds |
| M6 | **Mirror by construction**: `FLEET BACKUP <primary> [HOT\|WARM\|COLD] [AUTO\|MANUAL] [TIMEOUT <ms>]`, `FLEET SOLO`; `SUBSCRIBE MIRROR` feed (project + deltas from `ProjectDiff`, air with instants, every playlist's anchor, focus, pending blind commits, 4 Hz beat with frame keys); the backup follows the primary's clock; **mirror lead** (live changes scheduled ahead on a primary with a HOT backup); local edits refused on a mirror; display remap; files fetched by hash with the peer proof | Two `HostRig` nodes: after `FLEET BACKUP`, the backup's `PROJECT GET` equals the primary's (canonical JSON hash) within 1 s of each edit; a `PLAYLIST GO`, `BLACKOUT ON` and `PANIC` on the primary give **equal frame keys on both for every beat over 30 s** (ManualClock-free, real loopback); `mirror.state` "identical"; an edit sent to the backup is refused "FOH-B mirrors FOH-A — send it to FOH-A (10.0.0.21:9941) or FAILOVER"; **render (Xvfb)**: two render hosts read back byte-equal outputs at 10 chosen leader instants across a blackout and a panic fade |
| M7 | **Failover**: the pure `FailoverMachine`; triggers silence (`TimeoutMs`, 0 = off; both the beat and the clock exchange silent), output health, projector auto-switch observed, manual; policy `Manual` (alarm + one press) by default, `Auto` opt-in; `FAILOVER [HOLD]`, `FAILBACK CONFIRM`; route steps with confirm levels; **epoch fencing**; the takeover trail in the journal | ≥ 30 scripted transitions on the pure machine (incl. Patterns' drill cases: silence then return during takeover, route NAK, route accepted-not-observed, both channels vs one channel silent, repeated presses). Host: primary disposed → backup (Manual) raises the alarm and does nothing until `FAILOVER`; (Auto, fake device route at Observed) → trail `route requested → route confirmed (2/2 observed) → epoch 8 committed → serving` within `TimeoutMs` + 500 ms; route unreached → auto refused with words, manual proceeds saying so; the old primary restarted reads epoch 8 in the hello and comes up **fenced** (outputs held), and never switches back by itself; **editing on the new primary works** (`SET` succeeds, a mirror re-joined to it follows) |
| M8 | **Routes**: `FLEET ROUTE <output> DEVICE <dv> <primary input> <backup input> [NEED …] \| PROJECTOR-AUTO <dv> \| LINE <host:port> "<to backup>" "<to primary>" \| NONE`; `FLEET ROUTE TEST` (reachability and current input read, nothing switched) | Fake PJLink driver: TEST reads `INPT ?` and reports "P1 on 31 (primary) · reachable"; takeover sends `INPT 32` and reads back observed; a `LINE` step against a loopback TCP listener sees the exact line |
| M9 | **Controller death and leader succession**: free-wheel (`NetworkClock.Hold`) on leader loss, followers keep switching items; `FLEET SUCCESSION <node>…`, auto promotion with **`NetworkClock.Promote()`** continuing the leader's time line; `FLEET PROMOTE`; Patterns link loss keeps the air; desk loss keeps playing (regression tests) | Three `HostRig` nodes in a group: leader disposed mid-item; both followers switch to the next item at the same leader instant they computed before (read in STATE); follower 1 leads within `TimeoutMs` + 1 s; follower 2 follows it; leader time read on follower 2 is continuous within 1 ms across the change and never runs backwards; with no succession list nobody promotes and the context says "leader gone — FLEET PROMOTE on the node that should lead" |
| M10 | **Desk fleet board and `/fleet` web page**; the readiness light in the header includes the fleet's worst light | App.Tests view-model tests on fixtures; `/fleet` served and its WS frames carry the board JSON (HTTP test); the page renders 3 cards in headless Chromium (Playwright script, run here, not in CI) |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Router route step**: Blackmagic Videohub Ethernet protocol (TCP 9990, `VIDEO OUTPUT ROUTING:` blocks) | A loopback fake hub: routing block sent, the echoed block read back → Observed |
| S2 | **Picture fingerprint**: 8×8 luma of each output from its last mip level at 4 Hz, compared with the primary's for the same show frame | Render test: identical outputs → distance 0; a one-colour difference flagged |
| S3 | **N+1 cold standby**: one COLD node backs up several primaries (project and anchors only, no render) and takes over one | Two primaries, one standby: takeover of either loads its show and seeks to now |
| S4 | **`FLEET AT <time>`** replay from the metrics ring and journals (Patterns' `EYE AT` [E§1.4]) | Fixture record: the board at an instant relit from the record; unknown things grey |
| S5 | **`FAILOVER DRILL`**: in rehearsal, simulate the primary's death (its outputs held, its beat stopped) and run the real takeover and a `FAILBACK` | Host test of the drill trail |
| S6 | Fan-out (`IFleetFanout`) for showsafety's `SHOW ARM ALL`, `PANIC … NODES ALL`, `PREFLIGHT ALL`, and fleet preflight rows | Two nodes: ARM ALL arms both |
| S7 | Webhooks and syslog for fleet events (ROADMAP R4.3) | a local HTTP listener receives `fleet.takeover` JSON |

### Later

| # | Item |
|---|---|
| L1 | Cross-node projector stacks failover (facade L-item) |
| L2 | NMOS IS-04/05 routing as a route step; ST 2110 |
| L3 | GPU temperatures and fan speeds via NVML (runtime-loaded, proprietary) and ADLX |
| L4 | One-click diagnostics bundle across the fleet |
| L5 | Byte-exact pixel comparison of mirrors through a camera (the framelock certificate's camera path) |

## 3. Architecture

### 3.1 Components, threads, data flow

```
                         ┌───────────── Show Bus hello (UDP 9928): role + epoch ─────────────┐
 Primary FOH-A                                                                   Backup FOH-B
 ┌──────────────────────┐   SUBSCRIBE MIRROR (paired wire, TLS pinned)   ┌──────────────────────────┐
 │ EngineCore           │ ─ MIRROR HELLO/PROJECT/DELTA/AIR/ANCHOR/ ────▶ │ MirrorClient (Net)       │
 │  Store, Playback,    │   FOCUS/PENDING/BEAT                           │  → MIRROR lines on the   │
 │  Air, Pending        │                                                │    lane (fleet session)  │
 │ MirrorFeed (Net)     │ ◀─ UDP 9942 clock exchange (B follows A) ───── │ ClockSyncClient          │
 │ FleetService (Engine)│                                                │ FleetService:            │
 │  mirror lead         │ ◀─ FLEET FENCE <epoch> (on takeover) ───────── │  FailoverMachine (Core)  │
 └──────────────────────┘                                                │  RouteRunner → DEVICE …  │
          ▲  ▲                                                           └──────────────────────────┘
          │  └── SUBSCRIBE HEALTH (open read) ── FleetMonitor on every node ── FleetGraph ── FLEET / eye / desk / /fleet
          └──── GET /api/context ── Patterns' Eye (with its P2 change) [E§4.2]
```

| Component | Module | Thread | Job |
|---|---|---|---|
| `HealthRecord`, `FleetGraph`, `FailoverMachine`, `FrameKey`, `MirrorProtocol`, `MetricsRing`, `FleetModel` | Core | pure | the model, the decisions, the codec |
| `MachineSampler` | Engine | its own 1 s timer | CPU, memory, disk, GC, GPU where available |
| `HealthPublisher` | Engine | lane snapshot + sampler | builds this node's `HealthRecord` on change, ≤ 1 Hz unless a light moved |
| `FleetMonitor` | Net | one `WireClient` per peer (pool I/O) | HEALTH subscriptions, liveness, latest-wins table |
| `MirrorFeed` | Net (server side of `SUBSCRIBE MIRROR`) | the wire session's writer | latest-wins per key, initial burst |
| `MirrorClient` | Net | pool I/O | the backup's subscription; hands lines to `HandleAsync` with the in-process `fleet` session |
| `FleetService` | Engine extension | its own 50 ms timer + lane posts | triggers, the failover machine, takeovers, fencing, succession, persistence, frame-key comparison |
| `RouteRunner` | Engine | pool, 2 s deadline per step | runs route steps through the node's own verbs (`DEVICE INPUT …`, a `LINE` socket, a router client) |

### 3.2 Roles

| Role (STATE `fleet.role`) | Holds the show | Outputs | Takes commands | Clock |
|---|---|---|---|---|
| `solo` | its own | its own | yes | own or group |
| `leader` / `follower` (sync group, framelock F1) | its own project; group playlists follow anchors | its own | yes | leads / follows |
| `primary` | its own; has ≥ 1 backup | on air | yes; live changes scheduled `+lead` | leads the backups (and its group) |
| `backup` HOT | the primary's (mirrored) | open, rendering identical frames | **no** (mirror lines only; queries; PANIC forwarded) | follows the primary |
| `backup` WARM | mirrored | open, master 0 (black) until takeover | no | follows |
| `backup` COLD | mirrored (project, anchors, air; files fetched) | closed | no | follows |
| `fenced` | stale | held (master 0) or closed (`FencedOutputs`) | queries only; `FLEET BACKUP <new primary>` | follows the new primary when it re-joins |

The **plan** that names roles and routes travels with the show (`Project.Fleet`, edited on the primary, mirrored to
backups); what this machine is doing (role, the primary it backs, its epoch, display remap) is machine-local
(`NodeSettings.Fleet`) so a restart re-enters it. A backup joins the primary (Patterns' twin model: the standby joins
the main [E§6]); the primary records the member in its plan as a `system` edit ("FOH-B joined as hot backup").

### 3.3 Mirror by construction (M6)

A frame is a pure function of **(project root, playlist anchors, air, pending commit, leader instant)**. The backup
receives each of those exactly, each with the leader instant it takes effect, and follows the primary's clock, so it
computes the same frame for the same instant.

**The feed** (on the primary's wire; paired only, because it carries the whole show; the backup proves the primary's
token from its credential store, ref `node:<primary id>`):

| Line | When | Payload |
|---|---|---|
| `MIRROR HELLO <json>` | on subscribe | `{"proto":1,"node":"9f8e7d6c","epoch":7,"rev":1234,"hash":"sha256:…","leadMs":50,"frameNs":16666667}` |
| `MIRROR PROJECT <rev> <json>` | on subscribe; when a delta cannot be formed (open/new) | the whole project (compact `ProjectJson`) |
| `MIRROR DELTA <rev> <baseRev> <json>` | every project change, coalesced to ≤ 20 Hz, latest-wins chain | `ProjectDiff` changes with full entity JSON: `{"changes":[{"op":"changed","id":"op_1","path":"outputs","entity":{…}}],"settings":null,"show":null,"fleet":null,"hash":"sha256:…"}` |
| `MIRROR AIR <seq> <json>` | every air change | `AirState` with instants: blackout since, freeze since, pattern, identify until, master fade, panic (per-output fades) |
| `MIRROR ANCHOR <playlist> <json>` | every transport change of **any** playlist | the `AnchorJson` shape (exists) + `switchAtLeaderNs` |
| `MIRROR FOCUS <playlist>` | focus change | |
| `MIRROR PENDING <json>\|NONE` | a blind commit scheduled / landed / withdrawn | `{"rev":1235,"effectiveLeaderNs":…,"patch":{…}}` (showsafety §3.6) |
| `MIRROR BEAT <seq> <leaderNs> <json>` | 4 Hz, aligned to a show frame | the primary's frame key at that instant: `{"rev":1234,"air":88,"outputs":{"op_1":"a1b2c3d4…"}}` |

The feed reuses framelock's `AnchorFeed` structure (latest value per key, the current set first on subscribe); keys
here are playlist ids, not groups. Lines are bounded by the client's `MaxLineBytes` (64 MiB for this client).

**Applying on the backup.** `MirrorClient` hands each line to `HandleAsync` with an in-process session (transport
`fleet`, role `Peer`), so the journal, the receipts and the lane order are the same as for any verb. The `MIRROR …`
verbs are marked `Internal` in the catalogue (not in MCP tools, the context's commands or HELP's list) and are refused
from any other session: "MIRROR lines come only from this node's own mirror link". On the lane:
- `PROJECT`: `Store.Replace(project, "mirrored FOH-A rev 1234")` (history cleared; a mirror has no undo of its own);
- `DELTA`: `Store.Apply` of the patch — checked: `baseRev` must equal the last applied, else the client asks for a
  full `PROJECT` (`MIRROR RESYNC`); the hash after applying must equal the line's hash, else resync and a journal row;
- `AIR`: `SetAir` with the primary's instants (the backup does not re-stamp them);
- `ANCHOR`: `Playback.Adopt` for any playlist (the sync-group condition of `PLAYLIST ANCHOR` is lifted for the `fleet`
  session only);
- `PENDING`: published as `EngineSnapshot.Pending` with the primary's effective instant;
- `BEAT`: handed to `FleetService` for the frame-key comparison and as the liveness beat.

**The mirror lead.** On a primary with at least one live HOT or WARM backup, every live change without an explicit
`AT` is scheduled at `now + lead` (transport verbs through `TakeAt`'s default; `SetAir` changes carry
`SinceLeaderNs = now + lead`; panic uses showsafety's `PanicLeadMs = lead`). `lead = clamp(2 frames + 1.5 × p99
one-way delivery of MIRROR lines over the last 60 s, 2 frames, 200 ms)`; the one-way delivery is measured from the
beat's leader stamp to the backup's receipt on the backup's leader clock and reported back on its `HEALTH`. STATE shows
`fleet.leadMs` and `fleet.leadMarginMs` (lead − the worst delivery seen); a margin under one frame turns `fleet` amber
"mirror lead too short: a change may land one frame late on FOH-B". With no backup the lead is 0 and nothing changes for
a solo node. Rationale (doctrine: measured, then governed): a change applied at an instant the backup has not yet
heard cannot be identical; 2 frames is the floor that covers the render's one-frame look-ahead.

**Air with instants.** Today `BLACKOUT` and `FREEZE` take effect at the next rendered frame. `AirState` gains
`BlackoutSinceLeaderNs` and `FreezeSinceLeaderNs`, and the render applies them when `leaderNs >= since` — a pure
function of leader time, as panic fades already are (showsafety §3.5). On a solo node `since = now`, so nothing a user
sees changes.

**Determinism caveats** (stated in STATE and preflight, never hidden): live captures, NDI/Spout/OMT inputs, web pages,
the Stagewatch timer source, audio-reactive parameters and history-dependent effects are **non-deterministic**; the
frame key marks outputs whose sources include one (`"op_2":"nondeterministic: NDI Cam 1"`) and the mirror state reads
"identical except op_2 (live input)". The output dither is seeded by the show frame number (showsafety §3.5), so it does
not break identity.

**Frame key** (`Core/Fleet/FrameKey.cs`, pure): for an output at a leader instant, a 64-bit FNV-1a hash (our own few
lines in Core — Core references only the runtime, and `HashCode` is randomised per process, so it cannot agree across
machines) over a stack buffer, no allocation, of: the project root's content hash; for each source the output draws — the playlist
lane's `(itemId, media frame index at the instant for media, generator id + show frame number for generators)`; the
air's level for that output quantised to 1/255, blackout/freeze/pattern/override at the instant. Both nodes compute it
for the beat's instant on the lane from their own snapshots; the backup compares. A mismatch names the first differing
component in words ("Show is on 'Intro' frame 1203 here, 1204 on FOH-A"). Three consecutive mismatches → `mirror.state`
"different" amber; identical again for 2 s → "identical".

**Display remap.** A backup's outputs keep the primary's ids; `NodeSettings.Fleet.Displays` maps an output id to a
local `DisplayKey` (default: the same key). `RenderHost` resolves display keys through `IDisplayRemap`. Preflight
checks each mirrored output found a local display of the same raster and refresh.

**Files.** The backup pulls every file the project names by content hash (media, meshes, scan data, FX assets) with
the existing range transfer, generalised from media items to any `ContentHash` the project holds. Pulls carry the
pairing proof (`X-PlayMap-Proof: <hmac of the path and a nonce>` over HTTP, the node token from the credential store)
and use HTTPS with the pinned fingerprint when the peer offers TLS [CO].

**Local commands on a backup.** Refused from every session but `fleet`/`system`, with the words "FOH-B mirrors FOH-A —
send it to FOH-A (10.0.0.21:9941), or FAILOVER to make this node the show". Allowed: queries, `VIEW`, `HEALTH`,
`FLEET …` view verbs, `FAILOVER`, `FLEET SOLO`, `PREFLIGHT QUICK`. `PANIC` on a backup is **forwarded** to the primary
over the paired link (so both fade on the same frames); if the primary cannot be reached within 100 ms it panics
locally and says so.

### 3.4 Failover (M7, M8)

**The machine** (`Core/Fleet/FailoverMachine.cs`, pure; inputs are facts with monotonic times, outputs are actions):

```
Joining ──hello+project──▶ Mirroring ──beats equal──▶ Identical
   ▲                         │  ▲                        │
   │                 suspect │  │ heard again            │ suspect (silence ≥ T/2 on BOTH channels,
   │                         ▼  │                        ▼          or a primary output red ≥ OutputMs)
   │                       Suspect ── T reached, Manual ──▶ Alarm (waits for FAILOVER or recovery)
   │                         │
   │        T reached, Auto, routes ready, no HOLD
   │                         ▼
   │                     TakingOver: RouteRequested → RouteConfirmed (level ≥ Need) → EpochCommitted → Serving
   │                         │ route below Need (Auto)            │
   │                         ▼                                    ▼
   │                     Refused (alarm, keeps mirroring)       Primary (this node has the show)
   └──── FLEET BACKUP <new primary> ◀── Fenced ◀── a higher epoch heard / FLEET FENCE received (on the old primary)
```

- **Silence** needs both channels silent — the mirror beat (4 Hz) and the clock exchange replies (every 250 ms) — for
  `TimeoutMs` (default 2 000; 0 disables automatic triggers). One channel silent is "primary link degraded" (amber), never
  a takeover (Patterns' drill: one cable is not a dead machine [S§10]).
- **Output health**: the primary's HEALTH reports an output red (window lost, device lost) for `OutputMs` (default
  1 000) → an **output-level** takeover: only that output's routes switch; the backup is already rendering it (HOT).
- **Projector auto-switch observed**: a device linked to a mirrored output reports its input changed to the route's
  backup input (PJLink class 2 `%2INPT=` notice, or the poll) → recorded as an output-level takeover that the
  projector did by itself; the backup marks that output "on air from FOH-B" and keeps rendering. No command is sent.
- **Manual**: `FAILOVER` on any fleet node (forwarded over the paired link to the backup that should act).
- **Policy**: `Manual` (default; Pixera and disguise advise manual for live shows [S§5–6]) raises the alarm — a STATE
  fault, a red context item, `fleet.suspect` event, the desk banner "FOH-A silent 2.0 s — FAILOVER to FOH-B?" with one
  press — and does nothing else. `Auto` proceeds after `TimeoutMs` unless `FAILOVER HOLD` was pressed.
- **The route as the fence.** An automatic takeover proceeds only when every route step of the affected outputs
  reached its `Need` level (default `Accepted`; `Observed` when the protocol can read back, as PJLink `INPT ?` can).
  Below it: "takeover refused: P1's route was not confirmed (DEVICE INPUT dv1 32 → unreached)" and the alarm stays. A
  pressed `FAILOVER` goes ahead and says the route was not confirmed — switch by hand (Patterns' drill §5–7 [S§10]).
- **Epochs.** Each takeover commits `epoch = max(heard) + 1`, persisted in `NodeSettings.Fleet.Epoch` before the node
  serves, carried in the hello (`epoch`) and in HEALTH. A node that hears a higher epoch for a show it thinks it holds,
  or receives `FLEET FENCE <epoch>`, becomes **fenced**: outputs held (master 0) within one frame, mutating verbs
  refused "fenced: FOH-B has the show since 21:04:05 (epoch 8) — FLEET BACKUP FOH-B to mirror it". A fenced or
  restarted old primary never switches back by itself (Panasonic and disguise [S§6, §8]).
- **Takeover by mode**: HOT — outputs already identical; the route switches. WARM — the master fades 0 → 1 at the
  serving instant (one frame). COLD — outputs open, the show seeks to now from the last anchors on the free-wheeling
  clock, preroll, then the route switches (seconds; stated as such).
- **Serving**: the backup becomes `primary` (role persisted), accepts every verb under the armed table it mirrored,
  leads the clock with `Promote()` (§3.5), leads its playlists' groups, announces role and epoch in its hello, and
  sends `FLEET FENCE <epoch>` to the old primary if reachable. **Editing continues** — the new primary is a full node
  with the show; a re-joined backup mirrors it.
- **FAILBACK** (`FAILBACK CONFIRM`, press only, refused armed without `CONFIRM`): needs the old primary mirroring the
  new one and identical for 10 s; the routes switch back, the epoch moves on, roles swap. The journal trail mirrors
  the takeover's.
- **The trail** (journal kind `fleet`, one id per takeover): `takeover 8 of FOH-A by FOH-B (silence 2.0 s) — route
  requested → P1 32 observed, P2 32 observed → route confirmed (2/2 observed) → epoch 8 committed → serving → complete
  in 1.4 s`. Every refusal names its step.

**Route steps** (`FleetModel.RouteStep`), run by `RouteRunner` in parallel with a 2 s deadline each, through the node's
own verbs and drivers:

| Kind | To backup | Back | Confirm |
|---|---|---|---|
| `DeviceInput` | `DEVICE INPUT <dv> <backup input>` (PJLink `INPT 32`, Panasonic, Barco Pulse, Christie, Epson, NEC, Sony — the existing drivers) | `DEVICE INPUT <dv> <primary input>` | the driver's `CommandStage` (Observed when it reads back) |
| `ProjectorAuto` | nothing (the projector switches itself on loss of signal) | nothing | Observed when the device's input reads the backup input; preflight requires a HOT, identical backup |
| `Router` (S1) | crosspoint `<out> <backup in>` | `<out> <primary in>` | Observed when the router echoes the routing block |
| `Line` | the operator's line to `host:port` (TCP, newline) | the other line | Sent (Accepted if an `OK`/`ACK` comes back within 500 ms) |
| `None` | — | — | Sent (the operator switches by hand) |

`FLEET ROUTE TEST` reads each step's target (reachability, current input) without switching, and records the level it
can reach; preflight shows it.

### 3.5 Controller death, free-wheel and succession (M9)

**What keeps playing when something dies:**

| Dies | What happens | Mechanism |
|---|---|---|
| the desk process or the desk's machine | nothing on the air | the engine is its own process (exists) |
| Patterns (the link) | the air is unchanged; Patterns' last blackout fact holds; the link light goes amber then red with "following its last blackout (off)" | `PatternsLink` never changes `PatternsBlackout` on link loss; a test pins it |
| Stagewatch | timer source shows "?" (exists) | |
| Companion, OSC, MIDI clients | nothing | |
| a sync-group **leader** | followers **free-wheel**: `NetworkClock.Hold()` freezes the last offset and drift (no new samples; the mapping stays continuous), every lane keeps switching at its `SwitchAtLeaderNs`; after `TimeoutMs` the succession promotes | below |
| a **primary** with a backup | failover (§3.4) | |
| a node's engine | the watchdog restarts it, the recovery record puts the show back at "now"; a backup re-joins as a backup and resyncs (`MIRROR PROJECT`) | `NodeSettings.Fleet` |
| the network | every node keeps playing its own frames on its free-wheeling clock | |

**Free-wheel error budget.** The drift estimate's residual is typically ≤ 1 ppm (ClockSyncClient regression over 2 s
windows), i.e. ≤ 1 ms of divergence per ~17 minutes of free-wheel; STATE shows `sync.freewheelS` and an uncertainty that
grows at the estimate's residual rate.

**Succession.** `Project.Fleet.Succession` is an ordered list of node ids (cached by every member from the leader's
HEALTH, in `NodeSettings.Fleet.PlanCache`, so it survives the leader's death). When the leader has been silent on both
channels for `TimeoutMs` (default 3 000 for leaders), the first live node in the list:
1. commits `epoch + 1`;
2. calls **`NetworkClock.Promote()`**: the current mapping (offset at now, drift) becomes the node's own fixed mapping —
   its show clock continues the dead leader's line with no step — and starts answering the clock exchange (the
   responder already stamps `NetworkClock.NowNs` — `Net/Sync/ClockSync.cs` — so followers receive the same time line);
3. leads every group it was following (anchor feed, framelock F1);
4. announces role `leader` and the epoch in its hello.
Other followers re-target to it (`SYNC FOLLOW` internally); their estimators converge on a time line that differs from
their held one only by estimation error (< 1 ms), which framelock F2 slews. If the old leader returns and hears the
higher epoch it steps down and follows. With **no** succession list nobody promotes; the context goes red "leader gone —
FLEET PROMOTE on the node that should lead". Promotion never changes the air; it is allowed armed.

### 3.6 Health and the fleet board (M1–M4, M10)

**`HealthRecord`** (Core; JSON, ≤ 16 KB typical, bounded to 64 KB):

```json
{"v":1,"id":"0badc0de","name":"FOH-A","at":1760101234567,"build":"0.2.0+c12b186","os":"Windows 11 24H2",
 "role":"primary","epoch":7,"light":"amber","headline":"FOH-A · ARMED · Show 2:10 left · 1 amber: P3 shutter closed",
 "connection":{"light":"green","peers":4,"clients":3,"uptimeS":86400,"tls":true},
 "output":{"light":"green","outputs":[{"id":"op_1","name":"Left","light":"green","fps":60.0,"refreshHz":60.0,
   "dropped60s":0,"p95Ms":4.1,"path":"dxgi-flip","lock":"framelocked","onAir":true}]},
 "sync":{"light":"green","role":"leader","group":"show","uncertaintyMs":0.21,"freewheelS":0,"feed":"live","verdict":"same vblank"},
 "devices":{"light":"amber","devices":[{"id":"dv1a2b3c4d","name":"P3","light":"amber","power":"on","shutter":"closed",
   "input":"31","hours":1203,"faults":[]}]},
 "media":{"light":"green","items":42,"missing":0,"transfers":0},
 "machine":{"light":"green","cpuPct":23.0,"cpuAppPct":11.2,"ramPct":41.0,"memMb":1400,"gpuPct":-1,"vramPct":-1,
   "diskFreeGb":812.0,"tempC":-1,"gcPauseMs":12.3,"renderer":"NVIDIA RTX A4000/PCIe","power":"ac"},
 "show":{"armed":true,"panic":false,"blind":false,"preflight":"green"},
 "project":{"id":"pr1a2b3c4d","name":"Gala","rev":1234,"hash":"sha256:…"},
 "fleet":{"id":"fl…","succession":["9f8e7d6c"],"backups":[{"id":"9f8e7d6c","mode":"hot","state":"identical"}]},
 "mirror":null}
```

Categories are disguise's (connection, output, sync ≈ genlock, devices) [S§6] plus media, machine and show; lights by
the evidence rule (−1 = unknown = grey; silence = red) [E§1.1]. No secret, no content beyond names and counts — it is an
open read like STATE.

**`MachineSampler`**: Linux `/proc/stat` (system CPU), `/proc/self/stat` (process CPU), `/proc/meminfo`, `DriveInfo`
for the media and node-home volumes, `GC.GetTotalPauseDuration`; Windows `GetSystemTimes`, `GetProcessTimes`,
`GlobalMemoryStatusEx` (P/Invoke, compile-only here), GPU utilisation from the present package's DXGI device
(`IDXGIAdapter3.QueryVideoMemoryInfo`) and PDH "GPU Engine" counters (compile-only), power from
`GetSystemPowerStatus`. Temperatures −1 (L3).

**`FleetMonitor`**: for every live peer of kind `playmap` (PeerTable), one `WireClient` (`HELLO PlayMap <name>
module=fleet`, `SUBSCRIBE HEALTH`), TLS pinned when the hello offers it, ≤ 32 peers, reconnect backoff 0.5–8 s. Fleet
liveness is tighter than the hello's: stale after 3 s without a record, gone after 6 s. Non-PlayMap apps (Patterns,
Stagewatch, Companion) appear from the hello and the existing links' contexts.

**`FleetGraph`** (Core, pure; Patterns' Eye method [E§5]):
- things: `machine:<id>` (Video plane, tier 1), `output:<node>:<id>` (tier 2), `projector:<node>:<id>` (tier 3, the
  device's facts folded in), `sync:<group>` and `app:<id>` (Control plane), `route:<output>` when a route exists;
- links (the 11 verbs): machine **shows** output, output **drives** projector, backup machine **mirrors** primary machine,
  follower **follows** leader, app **controls** machine (a linked Patterns), machine **hears** app (heard only);
- lights by evidence; headline "4 machines · 18 outputs · 1 amber — FOH-B: P3 shutter closed"; problems queue (reds,
  then ambers); lenses ALL, OUTPUTS, PROJECTORS, SYNC, BACKUP, PROBLEMS; focus emphasis 1.0/0.55/0.25 by hops;
- layout: a deterministic grid — one row per machine in (role, name) order, columns in signal order; a machine keeps
  its row while others come and go.

`FLEET` / `EYE STATUS` answer the graph as JSON with Patterns' `EyeJson` field names (`headline, worst, worstLight,
worstWords, counts, focus, lens, problems, nodes[{id,kind,plane,tier,label,sub,light,x,y,w,h,words,wire}], edges[…]`)
plus `machines` (the health records). STATE carries `eye` (`headline, worst, worstLight, worstWords, counts, problems,
focus, lens`). The view verbs move the desk's board and are not journalled; a Companion key can recall a view ("EYE
FOCUS FOH-B").

**Metrics ring**: per node (own and monitored peers), one record per minute: min fps per output, dropped, max frame
p95, max CPU, min disk, worst light, events — 1 440 per node in memory; appended to `fleet.metrics.jsonl` (rotated
daily, 7 days). S4's `FLEET AT <time>` relights the board from it and the journal (30 s window, 90 s stale — Patterns'
replay numbers [E§1.4]).

**`/fleet`** (served by every node's HTTP host; one static page, no framework, embedded resource): cards per machine
(light, role chip, headline, output tiles with fps/dropped/lock, projector tiles with power/shutter/input/hours, sync
uncertainty ring, armed/panic chips, mirror state), the problems list, lenses; WS frames `{"t":"fleet",…}` at 1 Hz;
phone-sized dark layout (UX §8's show strip idea); read-only (no buttons send verbs without pairing; a paired browser
gets FAILOVER and PANIC with hold-to-confirm).

### 3.7 PlayMap in Patterns' God's Eye (M5)

This package owns the **order and shape** of `ContextBuilder`'s output (showsafety contributes `show` and `preflight`
items; framelock contributes `sync:*`):

1. `show`, `machine` (role, epoch, build, render path, CPU) — always present;
2. outputs: ≤ 6 individually; beyond 6 one `outputs` aggregate ("14 outputs green") plus every non-green output;
3. projectors (one item each, device facts folded into the words: "calibrated 0.8 px · on · shutter open · 31 ·
   1 203 h");
4. playing playlists individually, idle ones as one `playlists` aggregate;
5. `mirror:<node>` / `backup:<node>` ("FOH-B hot · identical"), `sync:<group>` (this node's own; never `member`,
   `locked`, `free` — those are not Eye verbs [E§4.1]);
6. `media`, `preflight`, `link:*`, `fault:*`.

Links (only the 11 Eye verbs, only between items in the first dozen where possible): `playlist feeds output`
(derived: the output's canvas has a layer showing the playlist, or a surface it projects shows it), `output drives
projector`, `show controls output`, `link:patterns controls show`, `machine mirrors mirror:<node>`, `mirror:<node>
mirrors output`, `machine follows sync:<group>` / `sync:<group> follows machine`. `commands` lists the 64 most useful
verbs, transport, air and PANIC first (full list at `/api/verbs`). `peers` carry each PlayMap node's light and headline
from its HEALTH (so a Patterns that reads one PlayMap's context sees the fleet as words).

**Patterns unchanged** [E§3]: a grey ShowApp "Playmap <name>" with host and caps, and — through a linked Stagewatch
PlayMap posts to — word lines with PlayMap's light and headline. **With Patterns' P2** (read the context of every peer
with the `context` cap; listed separately in `research/patterns-eye.md` §4.2 with P1, P3, P4, P5): each PlayMap node is
a ShowApp with its light, headline, up to 12 things and their links, and its reds and ambers join Patterns' problems
queue.

### 3.8 Performance budgets

| Path | Budget |
|---|---|
| `HealthRecord` build | ≤ 1 ms on the lane snapshot, ≤ 1 Hz (+ on light change) |
| Fleet monitor | ≤ 32 subscriptions; ≤ 64 KB per record; ≤ 2 MB/min total at 32 nodes |
| Mirror feed | DELTA coalesced ≤ 20 Hz; a 2 000-entity project delta ≤ 20 ms to form (ProjectDiff), full PROJECT on subscribe only |
| Frame key | ≤ 20 µs per output per beat (4 Hz), no allocation |
| Takeover (HOT, PJLink observed) | route ≤ 2 s deadline; target: serving within `TimeoutMs` + 1.5 s |
| Promotion | ≤ `TimeoutMs` + 1 s; leader-time continuity ≤ 1 ms |
| `FleetGraph.Build` | ≤ 5 ms for 32 nodes × 16 outputs; rebuilt only when a record's hash moves |

## 4. Spine needs

### 4.1 Model (Core; backwards compatible — old files read as `FleetPlan.Default`)

```csharp
// Core/Fleet/FleetModel.cs
public enum FleetRole { Primary, Backup, Follower }
public enum BackupMode { Hot, Warm, Cold }
public enum FailoverMode { Manual, Auto }
public enum ConfirmLevel { Sent, Accepted, Observed }
public enum RouteKind { DeviceInput, ProjectorAuto, Router, Line, None }

public sealed record FleetMember(string Id, string NodeId, string Name, FleetRole Role, string? BackupOf,
    BackupMode Mode, FailoverMode Failover, int TimeoutMs) : IEntity;                     // id prefix "fm"
public sealed record RouteStep(RouteKind Kind, string Target, string ToBackup, string ToPrimary);
public sealed record FailoverRoute(string Id, string Name, string OutputId, ImmutableArray<RouteStep> Steps,
    ConfirmLevel Need) : IEntity;                                                         // id prefix "fr"
public sealed record RouterLink(string Id, string Name, string Protocol, string Host, int Port) : IEntity; // "rt" (S1)
public sealed record FleetPlan(ImmutableArray<FleetMember> Members, ImmutableArray<FailoverRoute> Routes,
    ImmutableArray<RouterLink> Routers, ImmutableArray<string> Succession)
{ public static readonly FleetPlan Default = new([], [], [], []); }

// Core/Model/Project.cs (one hunk, beside showsafety's)
public FleetPlan Fleet { get; init; } = FleetPlan.Default;     // JSON "fleet"
// Ids: FleetMember = "fm", FailoverRoute = "fr", Router = "rt"; Project.AllIds() walks them
```

JSON: `"fleet":{"members":[{"id":"fm…","nodeId":"9f8e7d6c","name":"FOH-B","role":"backup","backupOf":"0badc0de","mode":"hot","failover":"manual","timeoutMs":2000}],"routes":[{"id":"fr…","name":"P1","outputId":"op_1","steps":[{"kind":"deviceInput","target":"dv1a2b3c4d","toBackup":"32","toPrimary":"31"}],"need":"observed"}],"routers":[],"succession":["9f8e7d6c"]}`.

Engine runtime: `AirState` gains `BlackoutSinceLeaderNs`, `FreezeSinceLeaderNs` (defaults 0 = since forever, today's
behaviour). `ShowBusHello` gains optional `Role` (`"solo|leader|follower|primary|backup|fenced"`) and `Epoch` (long)
— the hello stays ≤ 2 048 bytes and its caps unchanged (no new cap; the 16-cap budget is kept for LED's 15th).

`NetworkClock` gains (coordinated with framelock F2's `ClockState`): `Hold()` (stop taking new estimates; keep the last
mapping), `Promote()` (the current mapping becomes this node's own fixed mapping; `IsFollowing` false; `NowNs`
continuous), `FreewheelNs` (time since the last accepted estimate).

### 4.2 Verbs (`VerbCatalog`, category `Network` unless noted; new `Fleet` show class from showsafety)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `HEALTH` | `HEALTH` | Low | no | `OK {HealthRecord}` |
| `FLEET` | `FLEET [STATUS]` (aliases `EYE`, `EYE STATUS`) | Low | no | `OK {EyeJson-shaped board + machines}` |
| `FLEET FOCUS` / `LENS` / `NEXT` / `PREV` / `RESET` | `FLEET FOCUS <machine\|output\|id\|label>`, `FLEET LENS ALL\|OUTPUTS\|PROJECTORS\|SYNC\|BACKUP\|PROBLEMS`, … (aliases `EYE …`) | Low | no (paired only, not journalled, like VIEW) | `OK {"focus":"…","lens":"…"}` |
| `FLEET AT` (S4) | `FLEET AT <time>` (alias `EYE AT`) | Low | no | the board at that instant |
| `FLEET BACKUP` | `FLEET BACKUP <primary node> [HOT\|WARM\|COLD] [AUTO\|MANUAL] [TIMEOUT <ms 0-60000>]` | High | yes | `accepted` "joining FOH-A as a hot backup", then `applied` when identical |
| `FLEET SOLO` | `FLEET SOLO` | High | yes | `OK` "FOH-B no longer backs up FOH-A" |
| `FLEET ROUTE` | `FLEET ROUTE <output> DEVICE <device> <primary input> <backup input> [NEED SENT\|ACCEPTED\|OBSERVED] \| PROJECTOR-AUTO <device> \| ROUTER <router> <out> <primary in> <backup in> \| LINE <host:port> "<to backup>" "<to primary>" \| NONE` | Review | yes (plan edit on the primary) | the route JSON |
| `FLEET ROUTE TEST` | `FLEET ROUTE TEST [<output>\|ALL]` | Low | yes (journalled; switches nothing) | `[{"output","step","target","reached":true,"current":"31","level":"observed"}]` |
| `FLEET SUCCESSION` | `FLEET SUCCESSION <node> [<node>…] \| OFF` | Review | yes (plan edit) | the list |
| `FLEET PROMOTE` | `FLEET PROMOTE [<group>]` | High | yes | "leading show; clock continuous (±0.3 ms)" |
| `FLEET LEAD` | `FLEET LEAD AUTO\|<ms 0-200>` | Review | yes (node setting) | `{"leadMs":50,"auto":true,"marginMs":48.7}`; `0` warns "transitions may differ by a frame on the backups" |
| `FAILOVER` | `FAILOVER [<primary node>\|<output>…] [HOLD]` (class Fleet: never refused for an operator or remote, armed or not) | High | yes | `accepted` with the takeover id, then the trail as the receipt; `HOLD` cancels a pending automatic takeover |
| `FAILBACK` | `FAILBACK [CONFIRM]` | High | yes | the trail |
| `FLEET FENCE` | `FLEET FENCE <epoch>` | High, **Internal** | yes | from a peer's paired fleet session only |
| `MIRROR …` | `MIRROR PROJECT\|DELTA\|AIR\|ANCHOR\|FOCUS\|PENDING\|BEAT\|RESYNC …` | High, **Internal** | yes | from this node's own mirror link only |
| `SUBSCRIBE` (widened) | `SUBSCRIBE STATE\|CONTEXT\|PROJECT\|ANCHORS\|HEALTH\|MIRROR\|… \|OFF` | Low | no (`MIRROR` paired only) | the pushes |
| `NODES` (extended) | unchanged syntax | | | rows gain `role`, `epoch`, `light`, `headline` from HEALTH |

`VerbSpec` gains `public bool Internal { get; init; }` (kept out of MCP tools, the context's commands and HELP's list;
refused unless the session's transport is `fleet`/`system`).

### 4.3 STATE and context keys (new keys at the end of their objects)

```json
"fleet":{"role":"primary","epoch":7,"state":"serving","of":null,"mode":null,"failover":"manual","timeoutMs":2000,
  "leadMs":50,"leadMarginMs":48.7,"backups":[{"node":"9f8e7d6c","name":"FOH-B","mode":"hot","state":"identical","light":"green","words":"same frame at 21:04:05.250"}],
  "mirror":null|{"of":"0badc0de","state":"identical|different|resyncing|joining","words":"…","behindRev":0,"deliveryP99Ms":1.3},
  "succession":["9f8e7d6c"],"routes":[{"output":"op_1","steps":1,"tested":"observed","at":1760101234567}],
  "alarm":null|{"kind":"silence","since":…,"words":"FOH-A silent 2.0 s — FAILOVER to FOH-B?"},
  "last":null|{"kind":"takeover","id":"to_…","at":…,"words":"…complete in 1.4 s"},
  "nodes":[{"id","name","role","light","headline","heard":"live|stale|gone","build","rev"}],
  "light":"green","headline":"4 machines · 18 outputs green · FOH-B identical"},
"eye":{"headline":"…","worst":"output:9f8e7d6c:op_3","worstLight":"amber","worstWords":"P3: shutter closed",
  "counts":{"green":30,"amber":1,"red":0,"grey":2,"things":33,"links":41},"problems":["…"],"focus":"","lens":"all"}
```

`sync` gains `freewheelS` and `epoch`; `air` gains `blackoutSince`, `freezeSince` (leader ns). Faults: `fleet`
("FOH-A silent — FAILOVER to FOH-B?", "fenced: FOH-B has the show (epoch 8)", "mirror different: …"). Events (for
companion's automations): `fleet.suspect`, `fleet.takeover` (with the trail), `fleet.fenced`, `fleet.identical`,
`fleet.different`, `fleet.node.gone`, `fleet.node.back`, `sync.promoted`.

Context: §3.7.

### 4.4 Seams (C#)

```csharp
// Core/Fleet/FailoverMachine.cs — pure
public enum FailoverState { Solo, Joining, Mirroring, Identical, Suspect, Alarm, TakingOver, Refused, Serving, Fenced }
public abstract record FailoverInput(long MonoNs);
public sealed record BeatHeard(long MonoNs, ImmutableArray<ulong> Keys, bool Equal) : FailoverInput(MonoNs);
public sealed record ClockReply(long MonoNs) : FailoverInput(MonoNs);
public sealed record PrimaryOutputs(long MonoNs, IReadOnlyList<(string Id, Light Light)> Outputs) : FailoverInput(MonoNs);
public sealed record DeviceInputSeen(long MonoNs, string DeviceId, string Input) : FailoverInput(MonoNs);
public sealed record Pressed(long MonoNs, string Verb, string By) : FailoverInput(MonoNs);   // FAILOVER, HOLD, FAILBACK
public sealed record RouteResult(long MonoNs, string StepId, ConfirmLevel? Reached, string Words) : FailoverInput(MonoNs);
public sealed record EpochHeard(long MonoNs, string NodeId, long Epoch) : FailoverInput(MonoNs);
public sealed record TimePassed(long MonoNs) : FailoverInput(MonoNs);
public abstract record FailoverAction;
public sealed record RaiseAlarm(string Words) : FailoverAction;
public sealed record RunRoutes(IReadOnlyList<string> OutputIds, bool ToBackup) : FailoverAction;
public sealed record CommitEpoch(long Epoch) : FailoverAction;
public sealed record Serve(BackupMode From) : FailoverAction;
public sealed record Fence(long Epoch, string By) : FailoverAction;
public sealed record Note(string Words) : FailoverAction;                 // a journal row (kind "fleet")
public static class FailoverMachine
{
    public static (FailoverSnapshot Next, IReadOnlyList<FailoverAction> Actions) Step(FailoverSnapshot s, FailoverInput input, FailoverPolicy p);
}

// Core/Fleet/FrameKey.cs — pure
public static class FrameKey { public static ulong Of(in FrameKeyInputs inputs, string outputId, long leaderNs); }

// Net — the transport the fleet's links use (a fake in tests injects partitions and delays)
public interface IFleetTransport
{
    IAsyncDisposable SubscribeHealth(ShowBusPeer peer, Action<HealthRecord> onRecord, Action<string> onState);
    IAsyncDisposable SubscribeMirror(ShowBusPeer primary, string proofToken, Func<string, ValueTask> onLine, Action<string> onState);
    Task<WireReply> SendAsync(ShowBusPeer peer, string line, string? proofToken, CancellationToken ct);
}

// Engine
public interface IDisplayRemap { string Resolve(string outputId, string displayKey); }      // RenderHost reads it
public interface IHealthSource { HealthRecord Current { get; } event Action<HealthRecord>? Changed; }
// IFleetFanout (showsafety §4.4) — implemented by FleetService
// IPreflightCheck (showsafety §3.7) — FleetPreflight: nodes heard, builds, mirror identical, routes tested,
//   PROJECTOR-AUTO preconditions (HOT, identical, same raster/refresh), credentials present for devices on backups
```

`EngineParts` gains one partial hook, `static partial void AddFleet(List<EngineExtensionFactory> extensions);`,
implemented in `Engine/Host/EngineParts.Fleet.cs` (the existing `AddExtensions` stays the devices side's).

### 4.5 NodeSettings additions (`NodeSettings` partial — showsafety makes it partial)

```csharp
// Core/Bus/FleetSettings.cs
public FleetSettings Fleet { get; init; } = FleetSettings.Default;
public sealed record DisplayRemap(string OutputId, string DisplayKey);
public sealed record FleetSettings(
    string Role = "solo",                    // solo | backup | primary | fenced — re-entered at start
    string Of = "",                          // the primary this node backs up
    string Mode = "hot",
    string Failover = "manual",
    int TimeoutMs = 2000,
    int LeaderTimeoutMs = 3000,
    int OutputMs = 1000,
    long Epoch = 0,
    string FencedOutputs = "black",          // black | close
    string PlanCache = "",                   // the last fleet plan (succession) heard from the leader's HEALTH
    bool Monitor = true,
    int MonitorMax = 32)
{
    public ImmutableArray<DisplayRemap> Displays { get; init; } = [];
    public static readonly FleetSettings Default = new();
}
```

Peer tokens live in the credential store (`node:<node id>`), never in settings or lines.

## 5. Files

**New (Core):** `src/PlayMap.Core/Fleet/{FleetModel,HealthRecord,FleetGraph,FleetLayout,FailoverMachine,FrameKey,
MirrorProtocol,MetricsRing}.cs`; `src/PlayMap.Core/Bus/FleetSettings.cs`.
**New (Net):** `src/PlayMap.Net/Fleet/{FleetMonitor,MirrorFeed,MirrorClient,FleetTransport,VideohubClient}.cs`;
`src/PlayMap.Net/Http/FleetEndpoints.cs`; `src/PlayMap.Net/Http/Pages/fleet.html` (embedded).
**New (Engine):** `src/PlayMap.Engine/EngineCore.Fleet.cs` (verbs), `EngineCore.Mirror.cs` (MIRROR apply, the backup's
refusals, lead scheduling), `StateWriter.Fleet.cs`, `Fleet/{FleetService,MachineSampler,MachineSampler.Linux,
MachineSampler.Windows,HealthPublisher,RouteRunner,FleetPreflight,FleetFanout,DisplayRemap}.cs`,
`Host/EngineParts.Fleet.cs`.
**New (App):** `ViewModels/FleetViewModel.cs`, `Views/FleetBoard.axaml(.cs)`, `Controls/FleetCanvas.cs`.
**New (tests):** Core `Fleet/{FleetGraphTests,FailoverMachineTests,FrameKeyTests,HealthRecordTests,
MirrorProtocolTests (round trip + 100k Pcg32 fuzz),MetricsRingTests,PromoteClockTests}.cs`; Engine
`Host/{FleetMonitorHostTests,MirrorHostTests,FailoverHostTests,SuccessionHostTests,ControllerDeathTests,
FleetContextTests}.cs`; Render `MirrorIdentityTests.cs`; App `FleetViewModelTests.cs`; `tests/web/fleet.spec.mjs`
(Playwright, run manually here).

**Existing files touched (small hunks):**

| File | Hunk |
|---|---|
| `Core/Model/Project.cs`, `Core/Model/Ids.cs` | `Fleet` property; `fm`, `fr`, `rt` prefixes; `AllIds` walks them |
| `Core/Time/ClockOffsetEstimator.cs` | `NetworkClock.Hold()`, `Promote()`, `FreewheelNs` (on framelock's `ClockState`) |
| `Core/Bus/ShowBus.cs` | `ShowBusHello.Role`, `Epoch` (optional) |
| `Core/Wire/VerbCatalog.cs` | the rows; `VerbSpec.Internal`; `EYE` aliases; SUBSCRIBE syntax |
| `Engine/EngineCore.cs` | `AirState.BlackoutSinceLeaderNs/FreezeSinceLeaderNs`; mirror-lead hook in `SetAir` |
| `Engine/EngineCore.Router.cs` | dispatch rows; `TakeAt` default `+lead` on a primary with backups; the backup's refusal |
| `Engine/EngineCore.Sync.cs` | `PLAYLIST ANCHOR` accepts any playlist from the `fleet` session |
| `Engine/ContextBuilder.cs` | order, aggregation, links, commands (§3.7) |
| `Engine/StateWriter.cs` | call `WriteFleetState`; `sync.freewheelS`, `air.blackoutSince/freezeSince` |
| `Engine/Host/NetworkHost.cs` | hello role/epoch; expose `PeerTable` and `WireClient` factory to the fleet (the clock responder already stamps `NetworkClock.NowNs`, `Net/Sync/ClockSync.cs`, so a promoted node answers with the continued show time) |
| `Engine/Host/EngineParts.cs` | `AddFleet` partial |
| `Engine/Rendering/RenderHost.cs` | `IDisplayRemap`; blackout/freeze by instant |
| `Net/Links/PatternsLink.cs` | pin "link loss never changes the blackout fact" (test only if it already holds) |
| `Net/Media/*` (transfer) | fetch any project `ContentHash`; proof header; HTTPS with pin |
| `App/Views/NetworkDrawer.axaml`, `ViewModels/DrawerViewModels.cs`, `ViewModels/MainViewModel.cs` | Fleet tab; header readiness includes the fleet |
| Papers | `docs/WIRE.md`, `docs/SHOWBUS-NODE.md` (§3.7 here; hello role/epoch; Patterns P1–P5), `docs/ARCHITECTURE.md` §4 (free-wheel, promote) and a new §7 "Fleet", `docs/PLAYBACK.md` (mirror lead), `docs/ROADMAP.md` R4.1, R4.3 |

## 6. Dependencies

**Work packages.**
- **framelock** (F1, F2): the anchor feed and persisted sync role, the slewed clock and `Stepped`; `Hold`/`Promote`
  build on its `ClockState`. Without F1, succession still promotes but followers of non-mirrored groups need
  `SYNC FOLLOW` by hand — the fleet does it internally once F1 exists.
- **showsafety**: `ProjectDiff` (mirror deltas), pending commits, `PanicLeadMs`, `IFleetFanout`, `IPreflightCheck`,
  the `Fleet` show class (FAILOVER never refused for an operator), dither from the show frame (identity).
- **present**: output paths and per-output latency in HEALTH; the DXGI device for GPU numbers.
- **companion**: consumes `STATE.fleet`, `eye`, the events; the fleet template over `nodes`.
- **effects / videoio / control**: their non-deterministic badges feed the frame key's "nondeterministic" marks.
- **facade**: stack failover stays in-node; cross-node is L1.

**Third party.** None. The Videohub protocol (S1) is implemented from Blackmagic's published developer document
(TCP 9990) — the document was not re-read this session [R]; the client is tested against a fake and a real hub is a
lab test. No GPL/LGPL code.

## 7. Verification here

| What | How | Grade |
|---|---|---|
| Health records, sampler on Linux | Engine tests; `/proc` read here | **Real** (Linux); Windows sampler **compile-only** |
| Fleet monitor, liveness, board, eye row, EyeJson compatibility | 3–4 `HostRig` nodes on loopback in one process | **Real** |
| Mirror: project/delta/air/anchor/pending/beat; lead; refusals; resync on a broken chain | two `HostRig` nodes; fault injection through a fake `IFleetTransport` (drops, delays, reorders) | **Real** + **simulated network faults** |
| Frame identity of pixels | two render hosts on Mesa llvmpipe (Xvfb :99) reading back the same leader instants (deterministic generator and a test clip) | **Real (software GL)** |
| Failover machine | ≥ 30 scripted transitions incl. the Patterns drill cases | **Real** |
| Takeover with routes | fake projector drivers (Devices tests' fakes) at each `CommandStage`; a loopback TCP listener for `LINE`; a fake Videohub | **Simulated devices** |
| Fencing, restart into fenced, editing after failover | `HostRig` restart from `NodeHome` and recovery record | **Real** |
| Succession, free-wheel, clock continuity | 3 `HostRig` nodes; leader disposed; leader time sampled on followers | **Real** (loopback; ns clocks are the container's) |
| Controller death matrix | `HostRig` + fake Patterns link | **Real** |
| `/fleet` page | HTTP tests; Playwright with Chromium at `/opt/pw-browsers` (Node 22) run manually | **Real** (manual run) |
| Real projector seamless backup input, real routers, real two-machine drill on hardware with cameras on the wall | — | **Lab** (the drill of `docs/DRILL.md`, adapted: no scenario may show two pictures; dark no longer than the projector's or router's switch + one frame) |
| GPU utilisation, Windows counters | — | **Compile-only** |

## 8. Desk and Companion surface

**Desk — Network drawer → Fleet** (the board): one card per machine — light, name, role chip (PRIMARY, BACKUP HOT,
LEADER, FOLLOWER, FENCED), epoch, headline, output tiles (fps, dropped, lock, on-air), projector tiles (power,
shutter, input, hours), sync ring (uncertainty, free-wheel seconds), ARMED/PANIC chips, mirror state ("identical ·
lead 50 ms, margin 48 ms"). Lenses as chips; NEXT/PREV PROBLEM; focus by click (others dim); right-click a machine:
Focus, Open its desk (attach), FAILOVER to its backup, Make backup of…, Route test, Promote, Health…; right-click an
output: its routes, Route test, Failover this output. The **header readiness light** wears the fleet's worst light.
The **alarm banner** (Manual policy): "FOH-A silent 2.0 s — FAILOVER to FOH-B" with one press (no dialog) and HOLD.
A desk attached to a node that becomes fenced shows "FOH-B has the show — attach to FOH-B" and attaches with one press.

**Web `/fleet`**: the same board read-only on any phone or browser; a paired browser gets FAILOVER and PANIC as
hold-to-confirm.

**Companion** (the companion package builds the module; these are the facts and verbs it uses): a **fleet template**
(one key per machine: light, name, role, headline; press = focus), a **FAILOVER** key per primary (red, hold-to-press,
feedback `fleet.alarm`), **HOLD**, an **EYE** key lit by `eye.worstLight` with NEXT/PREV PROBLEM (Patterns' Eye page
habits), and automations on `fleet.takeover`, `fleet.node.gone`, `fleet.different`.

## 9. Risks and open questions

1. **Mirror lead adds latency** (2–3 frames) to every live action on a primary with a HOT backup. It is the price of
   identical frames; shown in STATE; `0` is allowed (`FLEET LEAD 0`, documented as "transitions may differ by a frame").
2. **Split brain under partition**: both nodes may believe they hold the show. The route is the fence (the projector
   shows one input); epochs settle it when the network heals; automatic takeover needs the route confirmed. A
   partition that also cuts the backup from the projectors leaves the backup refusing (Patterns' drill §3).
3. **Panasonic backup-input status over the network** is unverified [S§8]; until a command is confirmed, preflight
   reads the active input only and says "backup input readiness not readable — check the projector's STATUS screen".
4. **COLD takeover takes seconds** (windows, preroll); stated in STATE and preflight, never sold as seamless.
5. **Patterns' P2 is not ours to ship**; until it lands PlayMap is grey on Patterns' Eye (words only via Stagewatch).
6. **Non-deterministic sources** break identity; the frame key names them; the fix offered is "feed both nodes from
   one router" or "capture to a file" (COMPETITIVE §5c).
7. **Two DeviceServices polling the same projectors** (primary and backup): PJLink tolerates it; some native
   protocols allow one connection — the backup polls at a lower rate and never commands until serving; open: an
   opt-out per device.

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| HealthRecord, sampler (Linux real, Windows compile-only), HEALTH, SUBSCRIBE HEALTH | 5 |
| Fleet monitor, liveness, STATE fleet, context peers | 5 |
| FleetGraph, layout, eye row, view verbs, EyeJson compatibility | 6 |
| Metrics ring | 3 |
| Context reshaping for Patterns' Eye | 3 |
| Mirror feed, client, MIRROR verbs, apply, lead, refusals, remap, file pulls with proof/TLS | 12 |
| Frame key and comparison | 3 |
| Failover machine, FleetService, triggers, takeover, fencing, persistence | 10 |
| Routes (device, projector-auto, line), ROUTE TEST | 5 |
| Hold/Promote clock, succession | 6 |
| Controller-death tests | 3 |
| Desk fleet board | 9 |
| `/fleet` page | 5 |
| Fleet preflight rows, fan-out | 2 |
| Papers | 2 |
| **Must total** | **79** |
| Should: Videohub 3, picture fingerprint 3, N+1 cold 5, replay 3, drill 2, webhooks 2 | 18 |
