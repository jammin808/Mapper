# Work package `showsafety`: Show Mode, blind edit, panic outcomes, locks, preflight

Research: `research/safety-industry.md` (cited **[S§n]**), `research/patterns-eye.md` (**[E§n]**). Code read on main
(c12b186): `Core/Wire/VerbCatalog.cs`, `Core/Wire/Endpoints.cs`, `Core/Model/{Project,ProjectStore,ProjectJson,
OutputModel,StageModel}.cs`, `Core/Bus/{Pairing,ShowContext}.cs`, `Engine/{EngineCore,EngineCore.Router,
EngineCore.Content,EngineCore.Sync,EngineCore.Ops,StateWriter,ContextBuilder,Journal,ControlLane,NodeHome,Seams}.cs`,
`Engine/Host/{EngineParts,EngineExtension,ApprovalHook}.cs`, `Engine/Rendering/RenderHost.cs`,
`Render/Engine/Contracts.cs`, `Render/Passes/OutputStage.cs`, `Render/Shaders/ShaderLibrary.cs`,
`Engine/Assistant/StagewatchProposalSink.cs`, `Net/Mcp/McpTools.cs`, `Devices/Drivers/IProjectorDriver.cs`.
Other packages' specs read for their expectations of this one: `companion.md` (`IShowGate`, `STATE.show`, events,
`PANIC <outcome>`), `control.md` (armed table for `binding`/`osc`, MSC ALL_OFF → PANIC, `air.master`), `effects.md`
(`FX BYPASS ALL ON` as a panic outcome), `facade.md` (`IOutputGains`, stack failover), `framelock.md` (preflight sync
checks S4, refusals while armed), `led.md` (`LED LEVEL`, machine master).

Carry-over items folded in: **per-argument risk** (`RiskOf(line)`); **the Stagewatch approval desk decision**
(`StagewatchProposalSink` on, `CollectApproved=false`); **MCP approvals show `line.Raw`** (redact the display).

---

## 1. Goal and the bar

PlayMap must be safe to put in front of an audience, run by one tired operator, a Companion deck, Patterns, Stagewatch
and an AI at once — and the safety must be **a fact the node enforces at its one door**, not a convention of the desk.

| The best of the field | Who | PlayMap matches by | PlayMap beats it by |
|---|---|---|---|
| Show Mode with no password; transport and escape stay live; edits blocked [S§1] | QLab 5 | `SHOW ARM` / `SHOW DISARM`: a toggle, a key, a Companion button; no password | Enforced **per caller role** at the node, so a Companion deck, Patterns, OSC, MCP and the desk all meet the same table. QLab's Show mode still lets OSC edit cue properties [S§1]; PlayMap's armed table applies to every transport |
| "Restrict all collaborators to view-only in Show Mode" [S§1] | QLab 5 | `ShowRules.RemoteViewOnlyWhenArmed` | Graded: remote clients stay **transport-only** by default (UX.md §7.1), view-only is one switch |
| Double-GO protection (min time between GOs; refused GO flashes) [S§1]; 300 ms lockout and a standby-id fence [S§10] | QLab, Patterns | `MinGoMs` (300 ms armed default), refused with words | `EXPECT <item>` on GO: a key rendered as "Next: Intro" refuses if the next item changed under it |
| A table of what an armed desk refuses, with words saying what lifts it; content never refused [S§10] | Patterns | `ShowPolicy`, the same "what lifts it" words | One pure table shared by the router, MCP, the assistant, Companion's `WHOAMI` and the desk's greying |
| Silent Edition: edit without the audience seeing [S§3]; Blind Mode for sub-managers [S§7]; Watchout 7 has **no** offline mode, only a warning [S§4] | Millumin, Pandoras Box, Watchout | `BLIND ON`: edits go to a draft root; previews can show the draft | `BLIND COMMIT` lands the draft as **one undo step at a show boundary** (`NEXT` item, `ON GO`, `AT`) — frame-exact because the commit is a (root, leader instant) pair, and identical on every mirror. Conflicts with live edits are named, not silently overwritten |
| Panic with defined outcomes: Fade & Pause / Fade & Rewind / Fade & Load Next [S§2]; panic fades over a duration, a second panic is the hard stop, Esc is always panic [S§1] | Mitti, QLab | `PANIC` with outcomes `BLACK`, `RUN`, `REWIND`, `NEXT`, `FREEZE`, `SAFE`, `SHUTTER`, `FX-OFF`, `HARD`; second PANIC during the fade = hard stop; Esc on the desk | Outcomes **per output** (a stage wash keeps a safe look while the screens go black); fades are pure functions of leader time, so every mirror and sync-group member fades on the same frames; a defined **safe look** (a broadcast trouble slate's idea [S§9]); `SHUTTER` closes the lens through the projector's own protocol |
| Locks that block edits but not playback or inspection [S§5] | Pixera 26.1 | `LOCK <id>` / `LOCK AREA <area>` | Enforced **once, in `ProjectStore`**, by comparing roots: no verb, no AI, no undo can change a locked thing; locks travel with the show |
| "Going online" as a verification process [S§4]; Super Check rows with FIX words, ShowReady bar, commissioning stages [S§10]; Pixera's commissioning statistics [S§5] | Watchout 7, Patterns, Pixera | `PREFLIGHT QUICK|FULL` | One verb across every node, with a **time budget**, rows that each carry **the verb that fixes them**, a decode proof at show resolution on every node ("never a silent CPU fallback"), projectors read through their own protocols, and checks other packages plug in |
| Live actions not in undo [S§5 R] | Pixera 25.2 | transport, air, panic, show and locks are not undo steps | — |

## 2. Scope this round

### Must

| # | Item | Acceptance (each is a test in this container unless marked) |
|---|---|---|
| M1 | **`ShowPolicy`** (Core, pure): roles, verb show-classes, the armed and rehearsal tables, the refusal words. **The gate** at the router's door (lane verbs) and at `RunOpsAsync` (ops verbs). `IShowGate` (companion's seam) implemented | Table test: every `VerbCatalog.All` row × every role × armed/rehearsal has a decision (no default branch); 30 named cases (e.g. remote `WARP NODE` armed → refused "Not while armed: mapping is locked. SHOW DISARM on the desk, or BLIND ON to stage it."; remote `PLAYLIST GO` armed → allowed; assistant `BLACKOUT ON` armed → proposal; desk `SCAN START` armed → refused "heavy work"). Router tests through `HandleAsync` with sessions of each transport assert the reply's `Ok` and words and the journal row's `kind` |
| M2 | **`SHOW ARM [ALL]` / `SHOW DISARM [ALL]` / `SHOW [STATUS]` / `SHOW SET <rule> <value>`**; armed state survives an engine restart (recovery record); `STATE.show`, context item `show`, journal rows, `ShowEvent` raised | ARM then restart the engine from its recovery record (existing `RestoreAsync` path): `STATE.show.armed` true, `by` kept. ARM twice → refused "already armed by Desk since 20:31:05". `ALL` without the fleet package → refused "no fleet on this node" |
| M3 | **Locks**: `LOCK`, `UNLOCK`, `LOCKS`; entity and area locks in `Project.Show.Locks`; `Surface.Locked`/`Projector.Locked` honoured as entity locks; armed adds `mapping`+`layout`; **one guard in `ProjectStore`** (also on UNDO/REDO); locks are not undo steps | Every edit verb (SET/ADD/REMOVE/MOVE, WARP NODE, PROJECTOR SOLVE, SCAN APPLY, PLAYLIST REMOVE, GENERATOR SET…) against a locked target is refused with "<Label> is locked (by Desk: 'show night') — UNLOCK <id> first"; the same verbs on an unlocked sibling succeed; UNDO of a step that touches a locked entity is refused and the undo stack is unchanged; transport on a locked playlist plays. Guard cost ≤ 50 µs p99 for 100 locks on a 5 000-entity project (BenchmarkDotNet-free stopwatch test, 1 000 runs) |
| M4 | **Panic**: outcomes, per-output plans (`PANIC SET`), `PANIC [<outcome>] [ON …] [FADE ms] [AT …]`, `PANIC CLEAR`, second-panic hard stop, fades as pure functions of leader time, safe-look dip, `SHUTTER` through device links, `FX-OFF` through the effects package when present; **`MASTER`** grand master with fade | Core: `AirFade.At` exact at endpoints and monotonic; outcome table test. Engine with a `ManualClock`: `PANIC` at t → STATE `show.panic.outputs[].level` 1.0 → 0 over 1 000 ms; a second PANIC at t+300 ms → level 0 at t+300 ms and `hard:true`; `REWIND` leaves the playlist cued at its item's top; `NEXT` leaves the next item cued; `RUN` keeps transports running. **Render (Mesa llvmpipe, Xvfb :99):** a white output read back at leader instants t, t+250, t+500, t+1000 ms gives 255, ≈191, ≈128, 0 (±2); `SAFE` shows the safe source after the dip. `SHUTTER` with a fake projector driver: `DEVICE SHUTTER … CLOSE` issued per linked device, its `CommandStage` in the reply |
| M5 | **Blind edit**: `BLIND ON [ALL]`, `BLIND DIFF`, `BLIND COMMIT [NOW|NEXT [<pl>]|ON GO [<pl>]|AT …] [FORCE]`, `BLIND DISCARD`, `BLIND [STATUS]`; draft store; `ProjectDiff` (Core) with field-level words; pending commit as (root, leader instant) in `EngineSnapshot`; render picks the root per frame; conflicts | Draft edits invisible in `PROJECT GET` and STATE until commit; `BLIND DIFF` lists "Left (output): warp node 3,2 moved; blend gamma 2.2 → 2.4"; `COMMIT NEXT pl…` while an item plays with 4 s left: `PROJECT GET` changes at the switch instant, not before (ManualClock), as **one** undo step; `COMMIT ON GO` lands with the next `PLAYLIST GO … AT +200` on the same leader instant as the item; a live edit to an entity the draft also changed → commit refused naming it, `FORCE` commits. **Render:** an output whose canvas changes colour in the draft reads back the old colour one frame before the commit instant and the new colour at it |
| M6 | **Preflight**: `PREFLIGHT [QUICK|FULL] [ALL|<node>…] [SOAK <s>]`, `PREFLIGHT REPORT [<id>]`; `IPreflightCheck` registry; the built-in checks of §3.6; per-check budgets; report JSON with fix verbs; `STATE.show.preflight`; show-ready bar; FULL refused while armed | QUICK on a node with the fake media backend and fake devices completes in < 2 s with every section present; a missing media file → red row with `fix:"MEDIA FETCH md… "`; a projector whose fake driver reports shutter closed → amber with `fix:"DEVICE SHUTTER dv… OPEN"`; a check that hangs → grey "did not finish in 5 s" and the run still completes; FULL decodes the test clip at 0 % and 50 % through the real FFmpeg backend (`PLAYMAP_FFMPEG_DIR=/opt/ffmpeg9/lib`) and reports `decoder` and `hardware:false` (honest on this box) |
| M7 | **Double-GO protection and the GO fence**: `MinGoMs` per rule set; `EXPECT <item>` on `PLAYLIST GO|NEXT|PREV` and `VIDEO PLAY|RESTART` | ManualClock: two GOs 200 ms apart armed → second refused "double GO protection: 0.2 s since the last GO (0.3 s armed)"; `PLAYLIST GO show EXPECT it_b` while the next is `it_c` → refused "the next item is now 'Outro', not 'Intro' — the key was out of date" |
| M8 | **Per-argument risk** `VerbCatalog.RiskOf(WireLine)`. *Round 2 (SPINE.md §4.1, R12): risk words only **raise** risk on Air, Transport, Panic and DevicePower lines, and PANIC is High for an assistant; lowering the safe directions below is deferred — it would let an MCP client black out the air or close every shutter with no person, and four existing tests hold BLACKOUT ON at High (PLAN.md §9 decision 11).* Proposed: safe directions lower (BLACKOUT ON, FREEZE ON, DEVICE SHUTTER CLOSE, PANIC, MASTER 0 → Review); MCP, the assistant and the context use it | Table test of 12 lines; MCP proposes `DEVICE SHUTTER dv1 OPEN` (High → approval), runs nothing for `CLOSE` without approval either (Review → still queued for an assistant), assistant settings' `RunsWithoutAsking` reads `RiskOf` |
| M9 | **Approval desk** [CO]: `StagewatchProposalSink` switched on when a node with the `approvals` cap is linked, `CollectApproved=false` (the link stays the one collector); approved lines run as role `approved` through the gate; the MCP approval card shows `VerbCatalog.Redacted(line)` | Host test with the existing Stagewatch fake: a proposal posts to the fake desk; approving it there runs it **once**; armed, an approved `WARP RESET` is refused with the armed words and the result posted back as failed with those words; an MCP proposal of `LINK STAGEWATCH host key` shows `LINK STAGEWATCH host ‹secret›` and runs with the key |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Parked edits** (Patterns' "waits for DISARM" [E§6]): an approved or remote edit refused while armed can be parked with `PARK` in its reply; `PARKED`, `PARKED DROP <id>|ALL`; parked edits land in order on DISARM, a superseded one is set aside with words | Two parked edits land on DISARM in order; one whose target changed since is set aside and journalled |
| S2 | `SHOW ARM ALL`, `PANIC … NODES ALL`, `PREFLIGHT ALL` fan out through the fleet package (`IFleetFanout`) with each node's receipt | Two-node `HostRig`: ARM ALL arms both; PANIC on one fades both with the same leader instant |
| S3 | Desk: close and quit ask while armed; Esc = PANIC (double Esc = HARD) in the PLAY workspace; the dark-room show strip | App.Tests view-model tests |
| S4 | Soft edit leases (UX §3 "soft lock … 5 s"): `HOLD <id>` taken implicitly by a drag; others see the holder | Lease expiry on ManualClock |

### Later (named so nobody builds them by accident)

| # | Item |
|---|---|
| L1 | OS show lock on Windows (notifications, sleep, Windows key, update restarts) as Patterns' `SHOW-MACHINE.md` [S§10] |
| L2 | Named users with logins and per-user rights (Pixera user management is unverified [S§5]); today's roles come from transports |
| L3 | Multiple concurrent drafts (one per session) with a merge view |
| L4 | Panic outcomes that drive audio (fade audio with the picture) once the audio seam (R4.4) exists — today `BLACK` mutes audio through the playlist transport's pause |

## 3. Architecture

### 3.1 Components and where they live

```
Core (pure, tested without an engine)
  Show/ShowPolicy.cs        roles, show classes, Decide(), the words                       M1
  Show/ShowClasses.cs       default class by category; RiskOf helpers                       M1, M8
  Show/AirFade.cs           AirFade, PanicPlan math, PanicOutcomes table                    M4
  Show/LockGuard.cs         guard(old, new, locks) → refusal words                          M3
  Model/ShowModel.cs        ShowPlan, ShowRules, OutputPanicPlan, ShowLock (document)       M2–M4
  Model/ProjectDiff.cs      entity diff, patch apply, field-level words                     M5 (fleet reuses)
  Preflight/PreflightModel.cs  PreflightRow, PreflightReport, ShowReady                     M6
Engine
  EngineCore.Show.cs        SHOW/LOCK/PANIC/MASTER/BLIND verbs; the gate; ShowState; events
  EngineCore.Blind.cs       draft store, commit scheduling, conflicts, Tick promotion
  Show/ShowGate.cs          IShowGate adapter over the engine (companion's WHOAMI)
  Show/AirLevels.cs         immutable per-output gain/override lookup for the render thread
  Preflight/PreflightRunner.cs + Preflight/Checks/*.cs
  StateWriter.Show.cs       the "show" key
Render
  OutputStage + OutputFs    uAir uniform (final multiply)
Net
  Mcp/McpTools.cs           RiskOf(line); redacted display
App
  Header (ARM, PANIC, readiness), Show strip, Blind panel, Preflight board, lock badges
```

### 3.2 The gate (M1)

**Roles** (`ShowRole`), from the session the transport built (never from words a client sends):

| Transport (`SessionInfo.Transport`) | Role | Note |
|---|---|---|
| `desk` | `Operator` | the desk on this machine or an attached remote desk |
| `wire`, `http`, `ws`, `companion`, `osc`, `binding`, `patterns-device`, `stagewatch` — paired | `Remote` | a client listed in `ShowSafetySettings.Operators` (HELLO name or address) is `Operator` |
| any — not paired | `Observer` | (already cannot mutate; the gate adds nothing) |
| `assistant`, `mcp` | `Assistant` | |
| `approval` | `Approved` | a person approved an assistant's line (node APPROVE or Stagewatch's desk) |
| `fleet`, `sync`, `system` (in-process sessions only) | `Peer` / `System` | anchors a follower adopts, a mirror's applies, recovery |

With `RemoteViewOnlyWhenArmed`, armed `Remote` becomes `Observer` (QLab [S§1]).

**Show classes** (`ShowClass`), one per verb in the catalogue — `VerbSpec.Show`, defaulted from the category and set
explicitly where the default is wrong (the catalogue stays the one table):

| Class | Verbs (examples) |
|---|---|
| `Query` | every non-mutating verb |
| `Transport` | VIDEO *, PLAYLIST GO/CUE/NEXT/PREV/FOCUS |
| `Air` | BLACKOUT, FREEZE, MASTER, FX BYPASS ALL (effects) |
| `Panic` | PANIC, PANIC CLEAR |
| `Show` | SHOW *, BLIND *, LOCK, UNLOCK, PREFLIGHT, PARKED |
| `Content` | SURFACE SOURCE, OUTPUT SOURCE, GENERATOR SET/NEW, SET/ADD/REMOVE on canvases, layers, generators |
| `Playlists` | PLAYLIST NEW/ADD/REMOVE/MOVE/DELETE/LOOP/SYNC, SET on playlists and items |
| `Mapping` | WARP *, SURFACE SHOW, PROJECTOR SOLVE apply, SCAN APPLY/SURFACE, SET/ADD/REMOVE on stage entities |
| `Layout` | OUTPUT ENABLE, SET on output geometry/display/warp/masks |
| `Media` | MEDIA IMPORT/REMOVE/FETCH |
| `Heavy` | SCAN START, LATENCY MEASURE, BLEND SOLVE, SYNC CERTIFY/MARKS, ASK with a local model, PREFLIGHT FULL, optimise/transcode verbs |
| `Setup` | PATTERN, OUTPUT IDENTIFY, DEVICE PATTERN/LENS/PREPARE/RAW/IDENTIFY, SYNC APPLY |
| `DeviceSafe` | DEVICE SHUTTER, DEVICE INPUT, DEVICE FREEZE (repairs an operator may need) |
| `DevicePower` | DEVICE POWER |
| `Project` | PROJECT OPEN/NEW, UNDO/REDO (the guard decides further), PROJECT SAVE |
| `Network` | LINK *, SYNC LEAD/FOLLOW, PAIRING NEW, FLEET BACKUP/SOLO |
| `Fleet` | FAILOVER, FAILBACK (fleet package) |
| `Admin` | CREDENTIAL * (this machine only, unchanged) |
| `Assistant` | ASK, APPROVE, REJECT |

Generic `SET/ADD/REMOVE/MOVE <id>` are classed **by the target's prefix** (`ShowClasses.OfTarget(id)`): `op` Layout,
`sf/pj/ms/cm/sc/mk` Mapping, `cv/ly/gn` Content, `pl/it` Playlists, `md` Media, `dv` DeviceSafe, project id
Project; unknown prefix → Mapping (the cautious side).

**The armed table** (rehearsal: everything allowed as today, the assistant still gated by risk as today, locks and
`MinGoMs` still apply):

| Class \ role | Operator | Remote | Assistant | Approved | Peer/System |
|---|---|---|---|---|---|
| Query | ✓ | ✓ | ✓ | ✓ | ✓ |
| Transport | ✓ (MinGo, EXPECT) | ✓ (MinGo, EXPECT) | propose | ✓ (as Remote: a show caller approved it) | ✓ |
| Air (towards dark) | ✓ | ✓ | propose | ✓ | ✓ |
| Air (towards light: BLACKOUT OFF, MASTER up, FREEZE OFF) | ✓ | ✓ | propose | refuse | ✓ |
| Panic | ✓ never refused | ✓ never refused | propose | ✓ | ✓ |
| Show: ARM, BLIND, LOCK, PREFLIGHT QUICK | ✓ | ARM ✓, BLIND ✗, LOCK ✓, PREFLIGHT ✓ | BLIND ✓ (assistant drafts), rest propose | refuse | — |
| Show: DISARM, UNLOCK | ✓ | `DisarmFrom=paired` → ✓, `desk` → refuse | propose | refuse | — |
| Content | ✓ (live) | refuse (+park) | propose / blind | refuse (+park) | ✓ |
| Playlists | append (`PLAYLIST ADD`) ✓; others → BLIND | refuse (+park) | propose / blind | refuse (+park) | ✓ |
| Mapping, Layout | refuse → "BLIND ON to stage it" (blind ✓) | refuse | blind ✓ / propose | refuse (+park) | ✓ (mirror) |
| Media | IMPORT, FETCH ✓ (low priority); REMOVE of an item in use ✗ | FETCH ✓ | propose | refuse | ✓ |
| Heavy, Setup | refuse | refuse | refuse | refuse | — |
| DeviceSafe | ✓ | SHUTTER CLOSE ✓, rest refuse | propose | refuse | ✓ (failover routes) |
| DevicePower | ON ✓, OFF needs `CONFIRM` | refuse | propose | refuse | — |
| Project | SAVE ✓, OPEN/NEW refuse, UNDO/REDO ✓ (guard) | refuse | propose | refuse | — |
| Network | refuse | refuse | propose | refuse | ✓ |
| Fleet | FAILOVER ✓, FAILBACK needs `CONFIRM` | FAILOVER ✓ | propose | refuse | ✓ |
| Admin | ✓ (this machine) | — | never | never | — |

"Propose" = the line is queued as a proposal (as MCP High today) and the reply says so; "blind" = allowed only into a
draft. `Approved` is a person's decision on an assistant's line, so armed it gets exactly what a `Remote` client gets —
never more: an approval cannot carry a mapping edit past the armed table (it is parked for DISARM instead, S1). The direction of an Air verb is decided against the current air (`BLACKOUT TOGGLE` while blacked out is
"towards light").

**The words** follow Patterns' shape [E§6]: `"Not while armed: <what it would do>. <What lifts it>."`, e.g.
`"Not while armed: WARP NODE moves Left's picture. SHOW DISARM on the desk, or BLIND ON to stage it."`,
`"Not while armed: this client is transport-only (Companion: FOH deck). The desk can DISARM."`,
`"Not while armed: SCAN START puts structured light on the room's screens. SHOW DISARM first."`.

**Where it runs.** `ShowPolicy.Decide(VerbSpec spec, WireLine line, ShowRole role, in ShowFacts facts) → ShowDecision`
is pure and allocation-free (`ShowFacts` is a readonly struct: armed, blind owner, current air, locks count, MinGo
state). It is called:
- in `Route` on the control lane, before the dispatch switch (exact order with ARM, which also runs on the lane);
- at the start of `RunOpsAsync` for the off-lane operations verbs (they read the published `ShowState`; a line that
  races an ARM by less than one lane hop runs under the earlier state — documented);
- by MCP (`McpTools`) before it decides between run and propose, and by the assistant's `CommandGate`, so an AI sees
  "proposal" instead of a refusal;
- by `IShowGate.Allowed(session)` for Companion's `WHOAMI` (`may` list) and by the desk to grey its buttons.

Decisions are journalled like any refusal (`kind:"refused"`, `who`, redacted line) and counted in
`STATE.show.refused` (last 60 s).

### 3.3 Show Mode state (M2)

```
ShowState (engine runtime, immutable, swapped on the lane)
  Armed, ArmedBy, ArmedSinceUnixMs, LastGoLeaderNs (per playlist), Blind?, Parked[], RefusedRecent, Revision
```

- `SHOW ARM` sets it, journals "armed by Desk", raises `ShowEvent("show.armed")`; `SHOW DISARM` likewise, then lands
  parked edits (S1). Neither is an undo step.
- **Persistence:** `RecoveryRecord` gains `Armed`, `ArmedBy` (and `Panic` — a node that crashed in panic comes back in
  panic, faded to its outcome instantly). Patterns restores *disarmed* because its arm *enables* execution; PlayMap's
  arm *restricts*, so the safe side is armed [E§6]. The papers say so.
- **Heavy compute while armed:** the class table refuses heavy verbs; background jobs that are already running when
  ARM lands (a scan, a solve) are left to finish with a STATE fault "SCAN still running while armed — SCAN CANCEL".

### 3.4 Locks (M3)

- **Document:** `Project.Show.Locks` (`ShowLock(Target, Kind, By, AtUnixMs, Words)`). Areas: `mapping`, `layout`,
  `content`, `playlists`, `media`, `devices`, `network`. `Surface.Locked` and `Projector.Locked` (already in the model,
  used only by the desk's gizmo today) count as entity locks.
- **Not undo steps:** LOCK/UNLOCK apply through `ProjectStore.ApplyUnrecorded(label, edit)` (new: publishes a new
  root with no undo entry and leaves the stacks as they are — an UNDO afterwards restores the older root *with the
  current locks re-applied*, so undo never unlocks).
- **The guard** — one function, every path:

```csharp
// Core/Show/LockGuard.cs
public static string? Check(Project before, Project after, bool armed);
```

  For each entity lock: locate the entity in both roots (`ProjectIndex.Find(project, id)`, an id → path index built
  once per root and cached by reference in a `ConditionalWeakTable`) and refuse when the two are not the same
  reference and not equal, **except** when the only difference is its own `Locked` flag or its `Show`-plan entries.
  For each area lock (and armed's implicit `mapping`, `layout`): compare the area's projection by reference:
  `mapping` = `Stage.{Surfaces,Meshes,Projectors,Cameras,Viewpoints,Scans}` and each output's `Warp`, `Masks`,
  `Blend`, `ProjectorId`; `layout` = each output's `DisplayKey, Width, Height, RefreshHz, Mode, Plan, Rotation,
  Enabled, SourceRect`; `content` = `Canvases`, `Generators`; `playlists` = `Playlists`; `media` = `Media`;
  `devices` = `Stage.Devices`. Reference inequality is the fast path; equal-by-value is accepted (a no-op edit).
  Words: `"Left (output) is locked by Desk ('show night', 20:12) — UNLOCK op_1 first"`, `"mapping is locked while
  armed — BLIND ON to stage it"`.
- `ProjectStore` gains `public Func<Project, Project, string?>? Guard { get; set; }`, checked in `Apply`, `Undo` and
  `Redo` before publishing (refusal = `EditResult.Refused(words)` and nothing moves). `Replace` (open/new) is not
  guarded: PROJECT OPEN/NEW are refused armed by the table, and opening a show replaces its locks with the new show's.
  The blind draft store gets the same guard **without** the armed implicit areas (a draft may change mapping while
  armed; its commit is guarded against explicit locks).

### 3.5 Panic, the master and the safe look (M4)

**Outcomes** (`PanicOutcome`):

| Wire word | JSON | On the outputs | On the transports | `PANIC CLEAR` |
|---|---|---|---|---|
| `BLACK` | `black` | fade to 0 over FadeMs, hold | the playlists the output shows pause at the fade's end | fade up over the clear fade; transports stay paused (GO or VIDEO PLAY is the operator's) |
| `RUN` | `run` | fade to 0, hold | keep running | fade up into the show **as it is now** |
| `REWIND` | `rewind` | fade to 0, hold | at the fade's end: rewind the current item to its in-point and hold (cued) | fade up on the first frame, held |
| `NEXT` | `next` | fade to 0, hold | at the fade's end: cue the next item (Mitti's "load next") | fade up on its first frame, held |
| `FREEZE` | `freeze` | hold the frame at the panic instant (no fade) | keep running underneath | unfreeze at the clear instant (content jumps to now) |
| `SAFE` | `safe` | dip: fade to 0 over FadeMs/2, show the output's `SafeLook` source, fade up over FadeMs/2 | keep running | dip back to the show |
| `SHUTTER` | `shutter` | hold the picture; every device linked to the output gets `DEVICE SHUTTER <dv> CLOSE` (the projector's own fade if it has one) | keep running | `DEVICE SHUTTER … OPEN` |
| `FX-OFF` | `fxOff` | `FX BYPASS ALL ON` (effects package; refused "no effects on this node" without it) | — | `FX BYPASS ALL OFF` |
| `HARD` | `hard` | instant 0 | pause | as BLACK |

- **Per output**: `Project.Show.Panic` holds `OutputPanicPlan(OutputId, Outcome, FadeMs, SafeLook)`; an output with no
  plan uses `ShowRules.DefaultOutcome` (`BLACK`) and `DefaultFadeMs` (1 000 ms; QLab's panic is a duration [S§1]).
  A transport-level outcome applies to the playlists the output shows; when two outputs disagree about one playlist
  the most conservative wins (`HARD` > `BLACK` > `REWIND` > `NEXT` > `RUN`).
- **`PANIC` with no outcome** applies every output's own; `PANIC BLACK ON op_1 op_2` overrides; `ALL` is every output.
- **The second panic** while any fade of the first is running sets every panicked output's level to 0 at that instant
  and marks `hard:true` (QLab's rule [S§1]). A PANIC after the fades completed is accepted with "already in panic" and
  changes nothing.
- **Never refused** for a paired Operator or Remote, armed or not, in a blind draft or not, under a lock or not; it
  never opens a dialog (UX §7.2); from an assistant it is a proposal.
- **Mirrors and sync groups**: the panic is applied `AT` the leader instant now + `ShowRules.PanicLeadMs` (0 by
  default; the fleet package raises it to its measured mirror lead) so every member fades on the same frames.

**Math (Core, pure).**

```csharp
public readonly record struct AirFade(float From, float To, long StartLeaderNs, long DurationNs)
{
    public static readonly AirFade Full = new(1, 1, 0, 0);
    public float At(long leaderNs)
    {
        if (leaderNs <= StartLeaderNs)
            return From;
        if (DurationNs <= 0 || leaderNs >= StartLeaderNs + DurationNs)
            return To;
        return From + (To - From) * (float)((leaderNs - StartLeaderNs) / (double)DurationNs);
    }
    public bool Settled(long leaderNs) => leaderNs >= StartLeaderNs + DurationNs;
}
```

The fade is linear in the output's signal (display-referred) values; the safe look's dip is two half fades. A level is
a pure function of the leader instant, so a dropped render frame heals and every node agrees (doctrine: content is a
pure function of show time).

**AirState** (engine, not the document) gains:

```csharp
public AirFade Master { get; init; } = AirFade.Full;     // MASTER <0-100> [FADE ms]
public PanicAir? Panic { get; init; }
public sealed record PanicAir(PanicOutcome Outcome, string By, long SinceUnixMs, long SinceLeaderNs, bool Hard,
    ImmutableArray<OutputPanicAir> Outputs, long Seq);
public sealed record OutputPanicAir(string OutputId, PanicOutcome Outcome, AirFade Level, SourceRef? Safe,
    long SafeFromLeaderNs, long SafeUntilLeaderNs /* long.MaxValue while held */);
```

**The render thread** gets an immutable `AirLevels` built on the lane whenever `AirState` changes (one dictionary
output id → entry; nothing allocated per frame):

```csharp
// Render/Engine/Contracts.cs (FrameInputs gains)
public IAirLevels? Levels { get; init; }
public interface IAirLevels
{
    float Gain(string outputId, long leaderNs);                 // master × panic level × degrade gain (facade's IOutputGains)
    SourceRef? Override(string outputId, long leaderNs);       // the safe look while it is up
    bool Hold(string outputId, long leaderNs);                 // FREEZE outcome: hold this output's picture
}
```

`OutputStage` sets one more uniform, `uAir`, and the output fragment shader's last line becomes
`oColor = vec4(c * uAir, 1.0);` — after the blend pedestal and the dither, so 0 is true black (the pedestal too) as
blackout is. The blackout check before content stays first and unchanged. `Override` reuses the pattern-override path
per output (`PatternCovers` becomes "pattern or safe look covers"). **Dither determinism:** `uFrame` is fed from the
show frame number at the frame's leader instant (`leaderNs / framePeriod`), not the local render counter, so two nodes
dither identically (needed for mirror identity, fleet M6).

### 3.6 Blind edit (M5)

**Draft.** `BLIND ON` (owner = the calling session; `ALL` = every editing session's edit lines) creates a second
`ProjectStore` seeded with the live root (sharing every branch — free) and records `BaseRevision`. While blind:
- the router sets `_lineStore = _draft` for lines whose show class is an edit class (Content, Playlists, Mapping,
  Layout, Media metadata, Project UNDO/REDO) and whose session owns the draft; handlers write through `Edits`
  (`private ProjectStore Edits => _lineStore ?? Store;` — the 32 `Store.Apply/Undo/Redo` sites and the entity reads
  that feed them change to `Edits`; transports, air and STATE keep `Store`). `_lineStore` is set and cleared inside
  `Route` on the lane only, so no other thread ever sees it;
- `EngineSnapshot` gains `ProjectSnapshot? Draft`; `VIEW <v> SHOW editor DRAFT` renders the draft in the desk's
  editor view (never on an output); STATE shows `show.blind` with the change count;
- UNDO/REDO by the owner act on the draft's own history.

**Diff (Core, pure; fleet reuses it).**

```csharp
public sealed record EntityChange(string Id, string Kind, string Label, ChangeOp Op, IReadOnlyList<string> Fields);
public enum ChangeOp { Added, Changed, Removed }
public sealed record ProjectPatch(long BaseRevision, IReadOnlyList<EntityChange> Changes, Project Draft)
{
    public Project ApplyTo(Project live);                   // replaces/adds/removes by id in the owning collections
    public IReadOnlyList<string> ConflictsWith(Project baseRoot, Project live);   // ids changed on both sides
}
public static class ProjectDiff
{
    public static ProjectPatch Between(Project baseRoot, Project draft, long baseRevision);
    public static IReadOnlyList<string> Words(EntityChange c);  // "warp node 3,2 moved", "blend gamma 2.2 → 2.4"
}
```

It walks the known collections (media; playlists and items; generators; canvases and layers; stage surfaces, meshes,
projectors, cameras, viewpoints, scans, devices; outputs; settings; show plan): reference equality first, record
equality second, and for changed entities a field diff over their `ProjectJson` nodes (paths written the way `SET`
takes them, so a diff line is also the line that would make it).

**Commit.** `BLIND COMMIT <boundary> [FORCE]`:

| Boundary | Effective leader instant |
|---|---|
| `NOW` (default) | now + `ShowRules.CommitLeadMs` (2 frames at the project rate, ≥ 34 ms) |
| `AT +<ms>` / `AT <unix ms>` | that instant (TakeAt grammar) |
| `NEXT [<playlist>]` | the playlist's `SwitchAtLeaderNs` (the instant its next item takes over); refused "nothing ends on Show — use ON GO or AT" when none is scheduled |
| `ON GO [<playlist>]` | `long.MaxValue` until the next GO/NEXT/PREV on that playlist (any playlist when none is named); that GO's own start instant (its `AT`, else now) becomes the instant |

The commit computes `patch = ProjectDiff.Between(base, draft)`, checks conflicts (`ConflictsWith`: an id changed in
the draft **and** changed live since the base) — refused naming each, unless `FORCE` — checks the lock guard on
`patch.ApplyTo(live)`, and publishes `EngineSnapshot.Pending = new PendingCommit(root, effectiveLeaderNs, label,
patch)`. The render thread chooses per frame:

```csharp
var project = snap.Pending is { } p && leaderNs >= p.EffectiveLeaderNs ? p.Root : snap.Project.Project;
```

so the frame at the boundary is the first with the new root — exact to the frame and computed identically on every
node that holds the same pending commit (fleet mirrors). The render host warms the pending root's stage resources and
source leases as soon as it is published (commit scheduled ≥ 500 ms ahead is the recommended practice; under 2 frames
is refused "too close to the boundary — the next one"). On the lane, `Tick` promotes it when `LeaderNowNs >=
effective`: `Store.Apply("blind commit: 7 changes", _ => p.Root)` — one undo step — and clears `Pending` and the
draft. While a commit is pending, a live edit touching an id in the patch is refused ("a blind commit lands on Left at
21:04:05.200"); an unrelated live edit re-bases the pending root (`patch.ApplyTo(newLive)`, re-guarded).

`BLIND DISCARD` drops the draft (and a pending commit not yet effective). `BLIND OFF` with changes is refused
"7 changes staged — BLIND COMMIT or BLIND DISCARD"; with none it just leaves.

### 3.7 Preflight (M6)

**Model (Core).**

```csharp
public enum PreflightDepth { Quick, Full }
public sealed record PreflightRow(string Id, string Node, string Section, string Item, Light Light, string Value,
    string Note, string? Fix);
public sealed record PreflightReport(string Id, long AtUnixMs, PreflightDepth Depth, long DurationMs,
    IReadOnlyList<PreflightRow> Rows, ShowReady Ready, bool Complete);
public sealed record ShowReady(int Done, int Of, string FirstOpen);   // "show-ready 3/5 ▰▰▰▱▱ · media: 2 missing on FOH-B"
```

**Seam (Engine).**

```csharp
public sealed record PreflightContext(EngineCore Engine, PreflightDepth Depth, bool Armed, IMonoClock Mono,
    TimeSpan Soak);
public interface IPreflightCheck
{
    string Section { get; }               // "outputs", "media", "devices", "sync", …
    PreflightDepth MinDepth { get; }
    TimeSpan Budget { get; }              // per run; overrun → grey row "did not finish in 5 s"
    Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext ctx, CancellationToken ct);
}
// EngineServices.Show.cs:  public IReadOnlyList<IPreflightCheck> PreflightChecks { get; init; } = [];
```

**Runner.** `PREFLIGHT` is answered `accepted` with the report id, runs every check concurrently (bounded to 2 at
once for FULL decode work, at `ThreadPriority.BelowNormal` workers), each under its own budget, then publishes the
report (journal row "preflight QUICK by Desk: 41 green, 2 amber, 1 red in 1.8 s", `STATE.show.preflight`, context item
`preflight`, `ShowEvent("preflight.done")`). Reports are kept (last 20) as `preflight/<id>.json` in the node home.
FULL is refused while armed (Heavy); QUICK runs armed.

**Built-in checks** (each row's `Fix` is a verb line the operator, Companion or an AI can run):

| Section | Row | Quick | Full | Light rules | Fix |
|---|---|---|---|---|---|
| machine | renderer (GL vendor/renderer string; llvmpipe = amber "software rendering") | ✓ | ✓ | | — |
| machine | memory free, disk free vs the media library's size, GC pause total, uptime | ✓ | ✓ | disk < 2 × largest file red | — |
| machine | power on battery (laptop), pending OS restart (Windows: `RebootRequired` registry key, compile-only) | ✓ | ✓ | amber | — |
| outputs | each enabled output: open, on its display, raster and refresh match `DisplayInfo`, fps vs its own refresh, dropped in the last 60 s, frame p95 | ✓ | ✓ | as STATE's output light; mismatch red | `OUTPUT ENABLE op ON`, `SET op_1 displayKey …` |
| outputs | presentation soak: `SOAK <s>` seconds of missed vblanks per output | — | ✓ (opt) | > 0 missed amber | — |
| media | every item a playlist or surface uses: present, verified, decodable (readiness) | ✓ | ✓ | missing red | `MEDIA FETCH <md>` |
| media | **decode proof**: open the source (`IMediaBackend.Open`), `Prepare` at 0 % and 50 %, wait `IsReadyAt` (2 s each), read `SourceHealth.Decoder/Hardware` | — | ✓ | CPU decode of a ≥ 4K or HAP-less H.264/HEVC item amber "software decode" (never silent) | — |
| media | concurrent streams vs measured decode p95 (the sum of the playlists that play at once) | — | ✓ | over budget amber | — |
| content | generators compile (shader generators link on the render thread through `InvokeAsync`) | ✓ | ✓ | red | — |
| content | non-deterministic sources listed (live inputs, web, timer, audio-reactive, history-dependent FX) | ✓ | ✓ | grey info, amber when a mirror exists | — |
| mapping | each projector: solved, residual < 1.5 px green / < 4 amber / red; blend solved after the last stage change (revision compare) | ✓ | ✓ | | `PROJECTOR SOLVE pj`, `BLEND SOLVE ALL` |
| devices | each device link: reached, power, shutter open, input as expected, light hours vs the protocol's limit, temperatures, faults | ✓ | ✓ | unreachable red; shutter closed amber | `DEVICE POWER dv ON`, `DEVICE SHUTTER dv OPEN`, `DEVICE INPUT dv <in>` |
| sync | role, clock uncertainty (< 1 ms green), feed live, lock verdict (framelock's S4 rows when present) | ✓ | ✓ | | `SYNC FOLLOW …` |
| links | Patterns, Stagewatch, Companion links live and paired | ✓ | ✓ | live not paired amber | `LINK PATTERNS …` |
| safety | armed?, every output has a panic plan or the default, every SAFE look's source present, locks listed, MinGo set | ✓ | ✓ | not armed grey (amber when `ExpectArmed`) | `SHOW ARM`, `PANIC SET …` |
| fleet | (fleet package) nodes expected/heard, builds match, mirror identical, routes reachable | ✓ | ✓ | | `FAILOVER TEST …` |

Other packages register their own (`framelock` S4 sync checks, `zerocopy` decode path, `effects` plugin presence,
`control` CONTROL CHECK, `timecode` source present, `led` node reachability) by adding to `PreflightChecks`.

**Budgets.** QUICK: ≤ 10 s per node (target 2 s; facts only, no decoder opened). FULL: ≤ 90 s for 4 nodes and
8 outputs (UX.md §5 target) with 2 decodes in flight per node; a 60 s soak is opt-in. **Show-ready** five steps
(Patterns' ShowReady [S§10]): outputs green, media ready everywhere, mapping green, sync/backup green, safety
(armed + panic plans) — a step that does not apply counts as done and says so.

### 3.8 MinGo and the GO fence (M7)

`ShowRules.MinGoMsArmed` (300) and `MinGoMsRehearsal` (0) apply per playlist to `PLAYLIST GO|NEXT|PREV`, `VIDEO
PLAY|RESTART` from any role (the timestamp is the lane's leader time of the last accepted GO on that playlist). The
words: `"double GO protection: 0.20 s since the last GO on Show (0.30 s armed)"`. `EXPECT <item>|#<n>` (a new optional
keyword on those verbs, `TakeExpect` beside `TakeOn`/`TakeAt`) refuses when the item that would play is not the one
named: `"the next item on Show is now 'Outro', not 'Intro' — the key was out of date"`.

### 3.9 Per-argument risk (M8) and approvals (M9)

- `VerbCatalog.RiskOf(WireLine line)`: the row's `Risk`, lowered by the row's `RiskWhen` list (shared with
  companion's `VerbParams`): `BLACKOUT ON`, `FREEZE ON`, `DEVICE SHUTTER * CLOSE`, `PANIC` (any outcome except
  `CLEAR`), `MASTER 0`, `SHOW ARM`, `LOCK` → Review; `PANIC CLEAR`, `BLACKOUT OFF`, `DEVICE SHUTTER * OPEN`,
  `DEVICE POWER * ON|OFF` stay High. MCP (`McpTools` line 106), `CommandGate.RiskOf`, the context's `commands` and
  the proposal queue read `RiskOf(line)` instead of the row.
- The approval desk [CO]: `AssistantService` creates the `StagewatchProposalSink` when the Stagewatch link is live and
  its hello lists `approvals`, with `CollectApproved = false`; `StagewatchLink` keeps collecting approved lines aimed
  at this node (one collector, so a line runs once). Every approved line runs through `HandleAsync` with transport
  `approval` → role `Approved` → the armed table. MCP's approval card shows `VerbCatalog.Redacted(line)`; the queue
  keeps the real line for execution.

### 3.10 Threads and budgets

| Path | Thread | Budget |
|---|---|---|
| `ShowPolicy.Decide` | caller (lane / ops / MCP) | ≤ 2 µs, no allocation |
| Lock guard | lane, inside `Apply` | ≤ 50 µs p99 (100 locks, 5 000 entities) |
| `AirLevels` lookup | render | ≤ 0.2 µs per output per frame; built on the lane once per air change |
| `ProjectDiff.Between` | lane (DIFF, COMMIT) | ≤ 20 ms for a 2 000-entity project |
| Pending-root choice | render | one compare per frame |
| Preflight QUICK / FULL | pool, below normal | ≤ 10 s / ≤ 90 s (4 nodes, 8 outputs) |

## 4. Spine needs

### 4.1 Model (Core; backwards compatible — init properties with defaults; old files read as `ShowPlan.Default`)

```csharp
// Core/Model/ShowModel.cs
public enum PanicOutcome { Black, Run, Rewind, Next, Freeze, Safe, Shutter, FxOff, Hard }
public enum LockKind { Entity, Area }
public sealed record OutputPanicPlan(string OutputId, PanicOutcome Outcome, int FadeMs, SourceRef? SafeLook);
public sealed record ShowLock(string Target, LockKind Kind, string By, long AtUnixMs, string Words);
public sealed record ShowRules(
    int MinGoMsArmed = 300,
    int MinGoMsRehearsal = 0,
    bool RemoteViewOnlyWhenArmed = false,
    string DisarmFrom = "paired",            // "paired" | "desk"
    PanicOutcome DefaultOutcome = PanicOutcome.Black,
    int DefaultFadeMs = 1000,
    int PanicLeadMs = 0,
    int CommitLeadMs = 34,
    bool ExpectArmed = false)
{ public static readonly ShowRules Default = new(); }
public sealed record ShowPlan(ShowRules Rules, ImmutableArray<OutputPanicPlan> Panic, ImmutableArray<ShowLock> Locks)
{ public static readonly ShowPlan Default = new(ShowRules.Default, [], []); }

// Core/Model/Project.cs (one hunk; Project stays positional)
public ShowPlan Show { get; init; } = ShowPlan.Default;      // JSON "show"
```

JSON (camelCase, enums as words): `"show":{"rules":{"minGoMsArmed":300,…,"defaultOutcome":"black"},"panic":[{"outputId":
"op_1","outcome":"safe","fadeMs":2000,"safeLook":{"kind":"media","id":"md…"}}],"locks":[{"target":"mapping","kind":
"area","by":"Desk","atUnixMs":…,"words":"show night"}]}`. Old files have no `show` key and read as the default.

`ProjectStore` gains `Guard` (property) and `ApplyUnrecorded(string label, Func<Project, Project> edit)`.

`VerbSpec` gains `public ShowClass Show { get; init; }` (defaulted by `ShowClasses.Default(category, mutates)` in the
`V(...)` helper) and `public IReadOnlyList<RiskWhen>? RiskWhen { get; init; }` (shared with companion's `VerbParams`;
whichever lands first owns the record `RiskWhen(string Value, VerbRisk Risk)`).

`VerbCategory` gains `Show` (appended at the end).

### 4.2 Verbs (`VerbCatalog.All`, category `Show` unless noted)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `SHOW` | `SHOW [STATUS]` | Low | no | `OK {show JSON as STATE.show}` |
| `SHOW ARM` | `SHOW ARM [ALL] [<words>]` | Review | yes | `OK {"armed":true,"by":"Desk","since":…}`; refused "already armed by …" |
| `SHOW DISARM` | `SHOW DISARM [ALL]` | Review | yes | `OK {"armed":false,"landed":2,"setAside":0}` (parked edits that landed) |
| `SHOW SET` | `SHOW SET MINGO <ms> [ARMED\|REHEARSAL] \| REMOTE-VIEW-ONLY ON\|OFF \| DISARM-FROM PAIRED\|DESK \| EXPECT-ARMED ON\|OFF \| COMMIT-LEAD <ms> \| PANIC-LEAD <ms>` | Review | yes | applied with the rule's new value (a document edit, undoable, guarded) |
| `PANIC` | `PANIC [BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD] [ON <output>… \| ALL] [FADE <ms 0-10000>] [AT +<ms>\|<unix ms>]` | Review (RiskOf) | yes | `OK {"outcome":"black","hard":false,"since":…,"outputs":[{"id":"op_1","outcome":"safe","fadeMs":2000,"until":<leaderNs>}],"devices":[{"id":"dv…","stage":"accepted"}]}`; the second within the fade: `hard:true` |
| `PANIC CLEAR` | `PANIC CLEAR [ON <output>… \| ALL] [FADE <ms>]` (alias `PANIC OFF`) | High | yes | `OK {"cleared":["op_1"],"fadeMs":1000}`; refused "not in panic" |
| `PANIC SET` | `PANIC SET <output>\|ALL <outcome> [FADE <ms>] [SAFE <source>]` | Review | yes | the plan row (document edit) |
| `MASTER` | `MASTER <0-100> [FADE <ms>]` (category Output) | High (`0` → Review) | yes | `OK {"master":0.0,"fadeMs":2000}` (runtime, not undo) |
| `LOCK` | `LOCK <id> \| LOCK AREA <mapping\|layout\|content\|playlists\|media\|devices\|network> [<words>]` | Review | yes | `OK {"target":"op_1","kind":"entity"}` |
| `UNLOCK` | `UNLOCK <id> \| UNLOCK AREA <area> \| UNLOCK ALL` | Review | yes | `OK {"unlocked":[…]}` |
| `LOCKS` | `LOCKS` | Low | no | `OK [{"target","kind","by","at","words","implicit":false}]` (armed areas with `implicit:true`) |
| `BLIND` | `BLIND [STATUS]` | Low | no | `OK {"on":true,"owner":"Desk","changes":7,"commit":null}` |
| `BLIND ON` | `BLIND ON [ALL]` | Low | yes | `OK` + effect "blind editing: changes stay off the air until BLIND COMMIT" |
| `BLIND DIFF` | `BLIND DIFF` | Low | no | `OK {"base":1234,"changes":[{"id","kind","label","op","fields":["warp node 3,2 moved"]}],"conflicts":[]}` |
| `BLIND COMMIT` | `BLIND COMMIT [NOW \| NEXT [<playlist>] \| ON GO [<playlist>] \| AT +<ms>\|<unix ms>] [FORCE]` | High | yes | `accepted` `{"effective":<leaderNs>\|"on go","changes":7}`, then `applied` at the instant (receipt) |
| `BLIND DISCARD` | `BLIND DISCARD` | Review | yes | `OK {"discarded":7}` |
| `PREFLIGHT` | `PREFLIGHT [QUICK\|FULL] [ALL\|<node>…] [SOAK <s 0-120>]` | Low | yes (journalled, paired) | `accepted {"id":"pf_1a2b3c4d"}`; the report when done (receipt) |
| `PREFLIGHT REPORT` | `PREFLIGHT REPORT [<id>]` | Low | no | the report JSON |
| `PARKED` (S1) | `PARKED [DROP <id>\|ALL]` | Low / Review | DROP yes | `OK [{"id","line","who","at","why"}]` |
| GO fence | `PLAYLIST GO … [EXPECT <item>\|#<n>]`, same on NEXT, PREV, VIDEO PLAY, VIDEO RESTART | unchanged | | refusal words §3.8 |
| `DEVICE POWER` | `… OFF [CONFIRM]` — `CONFIRM` needed armed | unchanged | | |

Reply shapes are compact JSON after `OK`; refusals are `ERR <words>` with the receipt's `error`.

### 4.3 STATE and context keys (new keys at the end of their objects; WIRE.md rule)

`STATE.air` gains `"master":{"level":1.0,"to":1.0,"untilLeaderNs":0}` and `"panic":null|{…}` (same as
`show.panic`). New top-level key `show` (after `ai`, before the ops keys' tail — appended at the end of the object):

```json
"show":{"armed":true,"by":"Desk","since":1760101234567,"rules":{"minGoMs":300,"remoteViewOnly":false,"disarmFrom":"paired"},
  "blind":null|{"owner":"Desk","all":false,"since":…,"changes":7,"commit":null|{"boundary":"next","playlist":"pl…","effectiveLeaderNs":…}},
  "panic":null|{"outcome":"black","hard":false,"by":"Companion: FOH deck","since":…,"outputs":[{"id":"op_1","outcome":"safe","level":0.42,"settled":false}]},
  "locks":[{"target":"mapping","kind":"area","by":"Desk","at":…,"words":"","implicit":true}],
  "parked":0,"refused":{"last60s":3,"last":"Companion: FOH deck — WARP NODE … — mapping is locked"},
  "preflight":null|{"id":"pf_…","at":…,"depth":"quick","light":"amber","green":41,"amber":2,"red":0,"grey":1,"ready":"4/5","running":false},
  "minGoMs":300}
```

`minGoMs` and the boolean-ish `blind`/`panic` keep companion's tolerant reader working (`blind` and `panic` are
truthy objects or null).

**Context** (`ContextBuilder`), first items in this order (the Eye draws 12 [E§4.1]):
- `show` (kind `show`, label "Show"): red "PANIC black since 21:04:05 — PANIC CLEAR"; amber "blind editing: 7 changes
  staged" or "rehearsal while playing — SHOW ARM before doors" (only with `ExpectArmed`); green "ARMED by Desk since
  20:31"; grey "rehearsal". Always present, so the `show controls output:*` links are never dropped.
- `preflight` (kind `preflight`): the last report's light and "41 green · 2 amber · show-ready 4/5 · 3 min ago".
- Links: `show` **controls** `output:<id>` (lit by the output's panic/master state); `link:patterns` **controls**
  `show` (replacing today's `→ air`, which disappears whenever `air` is absent).

**Events** (`EngineCore.ShowEvent`, consumed by companion's `IEventSource`): `show.armed`, `show.disarmed`,
`show.blind` (`{"on":true}`), `show.commit` (`{"changes":7,"at":…}`), `panic` (`{"outcome","hard"}`), `panic.clear`,
`lock`, `preflight.done` (`{"light","id"}`), `refused` (rate-limited 1/s).

### 4.4 Seams (C#)

```csharp
// Core/Show/ShowPolicy.cs
public enum ShowRole { Operator, Remote, Observer, Assistant, Approved, Peer, System }
public enum ShowClass { Query, Transport, Air, Panic, Show, Content, Playlists, Mapping, Layout, Media, Heavy, Setup,
    DeviceSafe, DevicePower, Project, Network, Fleet, Admin, Assistant }
public enum ShowVerdict { Allow, Refuse, Propose, BlindOnly }
public readonly record struct ShowFacts(bool Armed, bool BlindOwner, bool AirDark, bool RemoteViewOnly, string DisarmFrom);
public readonly record struct ShowDecision(ShowVerdict Verdict, string? Words, bool Parkable);
public static class ShowPolicy
{
    public static ShowRole RoleOf(SessionInfo session, IReadOnlyCollection<string> operators);
    public static ShowClass ClassOf(VerbSpec spec, WireLine line);     // generic edits classed by target prefix
    public static ShowDecision Decide(VerbSpec spec, WireLine line, ShowRole role, in ShowFacts facts);
}

// Engine/Seams.cs (companion's seam, implemented here)
public interface IShowGate
{
    bool Armed { get; }
    string RoleOf(SessionInfo session);                                // "operator", "remote", …
    IReadOnlyCollection<string>? Allowed(SessionInfo session);         // null = every verb
    int MinGoMs { get; }
}

// Engine — the fleet package's fan-out (S2), consumed when present
public interface IFleetFanout
{
    Task<IReadOnlyList<(string NodeId, CommandReply Reply)>> RunOnPeersAsync(string line, IReadOnlyList<string>? nodes,
        CancellationToken ct);
}

// Engine/Seams.cs — preflight (above, §3.7): IPreflightCheck, PreflightContext
// Render/Engine/Contracts.cs — IAirLevels (above, §3.5)
```

`EngineServices.Show.cs` (init properties in the package's own file): `IReadOnlyList<IPreflightCheck>
PreflightChecks`, `IFleetFanout? Fleet`, `IReadOnlyCollection<string> Operators`.

### 4.5 NodeSettings additions

`NodeSettings` becomes `partial`; `Core/Bus/ShowSafetySettings.cs` adds one init property:

```csharp
public ShowSafetySettings ShowSafety { get; init; } = ShowSafetySettings.Default;
public sealed record ShowSafetySettings(
    ImmutableArray<string> Operators,      // HELLO names or addresses whose remote sessions count as Operator; [] = only the desk
    bool EscIsPanic = true)                // the desk's Esc key (S3)
{ public static readonly ShowSafetySettings Default = new([]); }
```

Machine-local by design (who counts as an operator is a property of the venue's clients, not the show); show rules
travel in the project.

## 5. Files

**New (Core):** `src/PlayMap.Core/Show/ShowPolicy.cs`, `ShowClasses.cs`, `AirFade.cs`, `LockGuard.cs`,
`ProjectIndex.cs`; `src/PlayMap.Core/Model/ShowModel.cs`, `ProjectDiff.cs`; `src/PlayMap.Core/Preflight/PreflightModel.cs`;
`src/PlayMap.Core/Bus/ShowSafetySettings.cs`.
**New (Engine):** `src/PlayMap.Engine/EngineCore.Show.cs`, `EngineCore.Blind.cs`, `EngineCore.Panic.cs`,
`EngineServices.Show.cs`, `StateWriter.Show.cs`, `Show/ShowGate.cs`, `Show/AirLevels.cs`,
`Preflight/PreflightRunner.cs`, `Preflight/Checks/{Machine,Outputs,Media,Decode,Content,Mapping,Devices,Sync,Links,Safety}Check.cs`.
**New (App):** `ViewModels/ShowViewModel.cs`, `ViewModels/PreflightViewModel.cs`, `ViewModels/BlindViewModel.cs`,
`Views/ShowStrip.axaml(.cs)`, `Views/PreflightBoard.axaml(.cs)`, `Views/BlindPanel.axaml(.cs)`.
**New (tests):** `tests/PlayMap.Core.Tests/Show/{ShowPolicyTableTests,AirFadeTests,LockGuardTests,ProjectDiffTests,
RiskOfTests}.cs`; `tests/PlayMap.Engine.Tests/Show/{ShowVerbTests,PanicTests,BlindCommitTests,LockVerbTests,
MinGoTests,PreflightTests,ApprovalArmedTests,ShowRecoveryTests}.cs`; `tests/PlayMap.Render.Tests/AirLevelReadbackTests.cs`;
`tests/PlayMap.App.Tests/ShowViewModelTests.cs`.

**Existing files touched (small hunks):**

| File | Hunk |
|---|---|
| `Core/Wire/VerbCatalog.cs` | `VerbCategory.Show`; `VerbSpec.Show`, `VerbSpec.RiskWhen`; the new rows; `with { Show = … }` on ~15 rows whose default class is wrong; `RiskOf(WireLine)` |
| `Core/Model/Project.cs` | `public ShowPlan Show { get; init; } = ShowPlan.Default;` |
| `Core/Model/ProjectStore.cs` | `Guard`, `ApplyUnrecorded`, guard calls in `Apply`/`Undo`/`Redo` |
| `Core/Bus/Pairing.cs` | `NodeSettings` → `partial` |
| `Engine/EngineCore.cs` | `AirState` gains `Master`, `Panic`; `EngineSnapshot` gains `Draft`, `Pending`; `Tick` promotes a pending commit and settles panic transports; `HandleAsync` unchanged |
| `Engine/EngineCore.Router.cs` | gate before the switch; `_lineStore` set/cleared; dispatch rows for the new verbs; `TakeExpect` |
| `Engine/EngineCore.{Edits,Content,Mapping,Transport,Devices}.cs`, `Scanning/ScanRunner.cs` | `Store.Apply/Undo/Redo` → `Edits.…` in edit handlers (32 sites); GO paths call `NoteGo`/`OnGoCommit` |
| `Engine/EngineCore.Ops.cs` | gate at `RunOpsAsync` entry |
| `Engine/NodeHome.cs` | `RecoveryRecord` gains `Armed`, `ArmedBy`, `PanicJson` (trailing optional) |
| `Engine/StateWriter.cs` | `air.master`, `air.panic`; call `WriteShowState` at the end |
| `Engine/ContextBuilder.cs` | `show`, `preflight` items first; links §4.3 |
| `Engine/Rendering/RenderHost.cs` | `FrameInputs.Levels`, pending-root choice, warm pending resources, `uFrame` from the show frame |
| `Render/Engine/Contracts.cs`, `Render/Engine/RenderEngine.cs`, `Render/Passes/OutputStage.cs`, `Render/Shaders/ShaderLibrary.cs` | `IAirLevels`; per-output gain, override, hold; `uAir` |
| `Net/Mcp/McpTools.cs` | `RiskOf(line)`; proposal display redacted; gate consulted for "proposal" vs "refused" |
| `Assistant/CommandGate.cs` | `RiskOf(line)` |
| `Engine/Assistant/AssistantService.cs` | create the Stagewatch sink with `CollectApproved=false` when the link is live with `approvals` |
| `App/Views/MainWindow.axaml`, `ViewModels/MainViewModel.cs`, `ViewModels/ShortcutMap.cs` | header ARM toggle, PANIC button, readiness light; Esc |
| Papers | `docs/WIRE.md` (verbs, STATE `show`, `air.master/panic`), `docs/UX.md` §7 (the table as built), `docs/ARCHITECTURE.md` §8, `docs/SHOWBUS-NODE.md` (context items), `docs/ROADMAP.md` R3.5 |

## 6. Dependencies

**Work packages.**
- **companion**: consumes `IShowGate`, `STATE.show`, the events and `PANIC <outcome>`; `VerbParams`/`RiskWhen` is
  shared (first to land owns the record).
- **control**: armed rows for transports `binding` and `osc` (Remote); MSC ALL_OFF → `PANIC`; `MASTER` is the grand
  master it asked for (R10).
- **effects**: `FX BYPASS ALL ON|OFF` is the `FX-OFF` outcome; Heavy class for FX import; `IPreflightCheck` for plugins.
- **facade**: its `IOutputGains` degrade gain is multiplied into `IAirLevels.Gain`; `STACK FAILOVER` is class Fleet.
- **framelock**: its S4 sync checks are an `IPreflightCheck`; `SYNC APPLY/MARKS/REJOIN` are Setup/Heavy (refused armed,
  as its spec already says).
- **fleet**: implements `IFleetFanout` (ARM ALL, PANIC ALL, PREFLIGHT ALL), raises `PanicLeadMs` to the mirror lead,
  reuses `ProjectDiff` for mirror deltas, mirrors `Pending` commits, adds the fleet preflight rows.
- **present / zerocopy**: decode-path and present-path rows.

**Third party.** None new. FFmpeg 9 (already loaded at run time from the user's install) for the FULL decode proof.

## 7. Verification here

| What | How | Grade |
|---|---|---|
| The armed table, roles, words, RiskOf | Core table tests over every catalogue row × role × armed | **Real** |
| Gate at the door, journal, MCP proposal vs refusal, approvals once | Engine tests through `HandleAsync` with each transport's session; the existing MCP test host; the Stagewatch fake from `StagewatchSinkTests` | **Real** |
| Locks and the guard, undo interplay, guard cost | Core + Engine tests; stopwatch budget test | **Real** |
| Panic levels, outcomes on transports, second panic, clear | Engine with `ManualClock` | **Real** |
| Panic and master on pixels, safe look, dither determinism | Render tests on Mesa llvmpipe (Xvfb :99): read back an output at chosen leader instants (the render host's test clock), two render hosts rendering the same leader instant compare byte-equal | **Real (software GL)** |
| SHUTTER outcome | `DeviceService` over the Devices tests' fake driver; the reply carries each `CommandStage` | **Simulated device** |
| Blind draft, diff, commit at NEXT / ON GO / AT, conflicts, frame-exact switch | Engine with `ManualClock`; render read-back across the instant | **Real** |
| Preflight QUICK/FULL, budgets, a hanging check, fix verbs | Fake checks and the real FFmpeg backend (`PLAYMAP_FFMPEG_DIR=/opt/ffmpeg9/lib`) on the test clip; `hardware:false` is the honest answer on this machine | **Real** (decode CPU-only) |
| Hardware decode proof, OS restart pending, real projector shutter and backup-input status | — | **Compile-only** (Windows/GPU/projector); the rows' logic is tested with fakes |
| Desk: header, Esc, strip, blind panel, preflight board | App.Tests view-model tests; the views are not pixel-tested | **View models real; views manual** |
| Companion keys for ARM/PANIC | companion package's module tests against `STATE.show` fixtures | **Simulated** |

## 8. Desk and Companion surface

**Desk header** (all workspaces): **ARM** toggle (red when armed, "ARMED · Desk · 20:31"), **PANIC** (red, never a
dialog; the tooltip lists each output's outcome; Esc in PLAY = PANIC, double Esc = HARD when `EscIsPanic`), the
**readiness light** (the worst of the last preflight and live STATE; click → preflight board), a **BLIND** chip with
the change count (amber) when a draft is open.
- **Armed:** mapping and layout controls grey with a lock badge and the refusal words as their tooltip (read from
  `IShowGate.Allowed` and `LOCKS`); right-click entries that would be refused show greyed with "armed" — the menus stay
  in step with the verbs.
- **Blind panel** (Map and Play): the diff list (each line clickable: selects the entity), boundary picker (Now / Next
  item of [playlist] / On next GO / At…), COMMIT, DISCARD; conflicts in amber with FORCE.
- **Panic plans:** Outputs drawer → each output tile → "On panic: Safe look · 2 s · logo.png"; right-click "Panic
  plan…".
- **Locks:** right-click any entity → Lock / Unlock; a padlock badge; Show menu → Lock mapping / layout / content.
- **Preflight board:** sections × rows with lights, value, note and a **Fix** button that runs the fix verb; QUICK and
  FULL buttons; the show-ready bar; per-node columns when the fleet package is present.
- **Show strip** (S3): dark-room view, focused playlists' current/next with countdowns, ARM, PANIC, readiness.

**Companion** (the companion package builds the module; this package supplies the facts): SHOW MODE toggle key
(`SHOW ARM`/`SHOW DISARM`, feedback `show_armed`), PANIC keys per outcome (`PANIC`, `PANIC BLACK`, `PANIC SAFE`,
`PANIC CLEAR` as a hold-to-confirm), BLIND chip feedback, a readiness key lit by `show.preflight.light` that runs
`PREFLIGHT QUICK`, GO keys sending `EXPECT <item>` (the fence), and `WHOAMI`'s `may` list greying keys an armed node
would refuse.

## 9. Risks and open questions

1. **Edit redirection for blind** touches 32 call sites; a missed site writes live while blind. Mitigation: a test
   that runs every edit verb blind and asserts `Store.Current.Revision` unchanged; a Roslyn-free grep test that
   `Store.Apply` appears only in transport/air/show files.
2. **Approved lines refused while armed** may surprise a show caller approving on Stagewatch; the refusal posts back
   with the words, and S1 parks it. Open: should an approved PANIC CLEAR be allowed? (Proposed: no — lifting a panic is
   the operator's press.)
3. **`ExpectArmed` nagging**: off by default; Patterns' rig-day lesson is "quiet during a live show unless asked".
4. **Panic fade in display-referred values** differs slightly from a linear-light fade; it matches what video
   switchers do. Open: a `LINEAR` option later.
5. **Commit warm-up**: a draft that adds a 4K clip on a surface needs its decoder prerolled before the boundary; the
   render warms on publish, but a commit `NOW` may show the clip's first frame late. The reply warns "⚠ new media
   needs preroll — commit at least 1 s ahead"; preflight's decode proof covers it.
6. **Roles from transports** are coarse; a venue with several operators on Companion lists them in `Operators`.
   Named users are L2.
7. **Ops verbs race ARM by one hop** (documented in §3.2); the window is one lane hop (< 1 ms).

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| `ShowPolicy`, classes, roles, gate in router/ops/MCP/assistant, words, table tests | 7 |
| SHOW verbs, ShowState, recovery, STATE/context/events | 4 |
| Locks: model, guard, `ApplyUnrecorded`, verbs, tests | 5 |
| Panic + master: model, AirFade, outcomes on transports, devices, `AirLevels`, render `uAir`, safe look, dither, tests | 8 |
| Blind: draft store, `Edits` redirection, `ProjectDiff` + words, commit boundaries, pending root in render, conflicts, tests | 11 |
| Preflight: model, runner, 10 built-in checks, budgets, reports, tests | 9 |
| MinGo + EXPECT | 1.5 |
| RiskOf, approvals sink decision, redaction | 3 |
| Desk: header, Esc, blind panel, preflight board, panic plans, locks, strip | 9 |
| Papers | 2 |
| **Total** | **59.5** |
