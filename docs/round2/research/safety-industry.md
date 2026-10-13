# Research: show safety in the industry — Show Mode, blind edit, panic, locks, preflight, redundancy, fleet health

Date of research: 13 October 2026. Legend: **[V]** verified this session in the cited source (URL; the page's own
date where it shows one, else "undated, read 2026-10-13"); **[V-local]** verified in a local repository (path given);
**[R]** recalled from earlier PlayMap papers and not re-read this session; **[U]** could not open or could not find —
treat as unverified.

Sources read: QLab 5 documentation (workspace, workspace settings, keyboard shortcuts, OSC dictionary), Mitti help
(Playback, Aug 2026), Millumin help (Dashboard, Tips, Change Log) and forum, Dataton WATCHOUT 7 user guide (network
overview, architectural differences, going online, connecting devices), PIXERA help (manager backup, client backup and
usage presets, hybrid mode backup 25.2, 26.1 changelog, Hub, logs, commissioning guide), disguise user guide (failover,
d3Net setup, d3Net overview, advanced machine settings) and developer API (monitoring), Green Hippo Hippotizer V4
manual, Pandoras Box help file (multi-user), 7thSense Delta and StackSignage guides, Panasonic projector operating
instructions (BACKUP INPUT SETTING), broadcast automatic-changeover products (Evertz, Cobalt, Vidovation/openGear,
SkyLark). Local: Patterns (`/home/user/patterns-svc`) for the sister app's own safety features.

---

## 1. QLab 5 (Figure 53) — Show Mode, panic, double-GO protection

| Fact | Status | Source |
|---|---|---|
| A workspace is in **Edit mode** (default) or **Show mode**, switched by two buttons in the footer, ⇧⌘[ / ⇧⌘], or OSC `/workspace/{id}/showMode {bool}` and `/toggleEditShowMode` | [V] | https://qlab.app/docs/v5/fundamentals/workspace/ ; https://qlab.app/docs/v5/general/keyboard-shortcuts/ ; https://qlab.app/docs/v5/scripting/osc-dictionary-v5/ (undated, read 2026-10-13) |
| Show mode **disables** the toolbox, deleting and moving cues, pasting cues, and the inspector ("The inspector is disabled when the workspace is in Show Mode") | [V] | workspace page; https://qlab.app/docs/v5/fundamentals/inspector/ |
| Show mode does **not** disable: quitting QLab, opening/closing/saving workspaces, the escape key, editing cue properties by AppleScript and OSC, viewing and changing workspace settings | [V] | workspace page |
| No password guards Show mode (it is a two-button toggle) | [V] (by the toggle's description); the phrase "a safety mechanism… not a security mechanism" quoted in docs/UX.md was **not** re-found this session | workspace page; [R] docs/UX.md §7.2 |
| Workspace setting **"When in Show Mode, restrict all collaborators to view-only"**: remote collaborators cannot edit, move the playhead, start or stop cues while in Show Mode, even if otherwise allowed | [V] | https://qlab.app/docs/v5/fundamentals/workspace-settings/ |
| **Panic** = fade and stop all cues over the **panic duration** (Workspace Settings → General). A second panic while panicking stops everything still fading **immediately** | [V] | workspace-settings page |
| Escape is **always** Panic All; double-tap escape is **always** Hard Stop; "This shortcut cannot be changed" — "in an emergency, any person familiar with QLab can confidently use the escape key to stop all playback from any QLab system" | [V] | workspace-settings page; keyboard-shortcuts page |
| OSC: `/workspace/{id}/panic`, `/panicInTime {number}`, `/hardStop`; MSC `ALL_OFF` = Panic | [V] | OSC dictionary; workspace-settings MSC table |
| **Double-GO protection**: "Minimum time required between each GO" (any duration incl. 0); a red border around GO while it holds; a GO received meanwhile flashes red and is **disregarded** | [V] | workspace-settings page |
| Reset button: hard stops all cues, resets temporary properties, returns each list's playhead to its first cue | [V] | workspace page |
| Broken cues show a red X; warnings listed in Workspace Status → Warnings (a pre-show check surface) | [V] | workspace page |

**Operated:** a toggle and a fixed key. **On failure:** panic is a fade with a defined duration; panic-while-panicking
is the hard stop. **Limits:** one machine; QLab has no network redundancy of its own (redundancy is "two Macs fed the
same GO", by OSC or MSC — [R], not re-verified).

## 2. Mitti (imimot) — panic with defined outcomes

| Fact | Status | Source |
|---|---|---|
| **Panic/Kill** (Playlist menu or ⌘Esc) "kills the outgoing signal"; Playlist Preferences choose its outcome: **Fade out & Pause**, **Fade out & Rewind** (rewinds the cue and pauses), **Fade out & Load Next** | [V] | https://imimot.com/help/mitti/getting-started/playback (published 2026-08-09) |
| During playback new cues cannot be created from the File menu or removed (drag & drop still adds) | [V] | same |
| JUMP behaviour is a preference too: **Trigger** (jump plays) or **Load** (jump waits on the first frame) | [V] | same |
| Mitti backup/failover between two machines (follower Mitti over the network) | [U] | not found this session |

**Lesson:** the outcome of panic is a **named, pre-set choice**, not a single "stop". Mitti offers it per playlist;
nobody offers it **per output**.

## 3. Millumin — Silent Edition, frozen outputs, locks

| Fact | Status | Source |
|---|---|---|
| **Silent Edition** (dashboard button): "any changes on your project cannot be seen on the outputs, only on the workspace, until you deactivate the silent-edition and come back to where your show was"; the board or timeline on the outputs is highlighted orange | [V] | https://help.millumin.com/docs/playback/dashboard/ (undated); https://help.millumin.com/docs/general/change-log/ |
| 5.14.f adds OSC feedback when Silent Edition changes (a user asked for a warning light) | [V] | https://forum.millumin.com/discussion/2512/feature-request-silent-edition-osc-status (2026-03-10) |
| **Freeze outputs** (Output menu): stop rendering the outputs while previewing changes; **video master** to black out; **Disable workspace**: no clicks in the workspace, rendered twice a second | [V] | https://help.millumin.com/docs/general/tips/ |
| Locked layers can be hidden or soloed in the dashboard; the preview grid can be locked | [V] | tips page; https://help.millumin.com/docs/playback/workspace/ |
| A Millumin panic button with defined outcomes | [U] | not found |

**Lesson:** blind editing is **global** in Millumin (everything you change is silent until you leave), and it ends by
"coming back to where your show was" — i.e. the commit is the moment you switch off, not a boundary of the show.

## 4. Dataton WATCHOUT 7 — Producer, Director, Runner; what happens when the controller dies

| Fact | Status | Source |
|---|---|---|
| Four services: **Producer** (authoring UI), **Director** (playback coordinator; holds the authoritative running show; NTP time server; hosts the Operative for external control), **Runner** (render engine per display host), **Asset Manager** (stores, optimises and distributes media) | [V] | https://docs.dataton.com/watchout-7-new/watchout/network-setup/network-overview.html ; https://docs.dataton.com/watchout-7/.architecture.html |
| "Producer can close and reopen without affecting the show… Closing Producer, intentionally or due to a crash, does not interrupt playback" | [V] | https://docs.dataton.com/guide/watchout/getting-started/architectural-differences-watchout-6-vs-7.html |
| "**Runners continue playback if the Director is lost** — once a Runner has received the show data, it keeps rendering even if the network connection to the Director drops" | [V] | same |
| "**Renderers maintain output if the Runner disconnects** — the visual and audio renderers hold their last state, avoiding black screens during transient failures" | [V] | same |
| A **Process Manager** on every node discovers nodes (multicast **UDP 3011 and 3012**) and restarts crashed services | [V] | same |
| **Installation (headless)** layout: Director on a server with no Producer; Runners play autonomously, controlled by external systems via the Operative | [V] | same |
| Redundancy by **host alias**: two machines with the same alias are a redundant pair; "If the active machine goes offline, the system automatically switches to the backup machine within seconds"; both must have identical hardware for a seamless switch | [V] | network-overview; https://docs.dataton.com/watchout-7-new/watchout/network-setup/connecting-devices.html |
| …but another version of the same guide calls the same-name swap a **cold spare**: "The two machines must not be powered on at the same time; this is a manual swap, not automatic failover" | [V] (the two pages contradict each other) | https://docs.dataton.com/guide/watchout/getting-started/architectural-differences-watchout-6-vs-7.html |
| **No offline mode** in 7: "the moment Producer connects to a Director, any playback commands you issue are broadcast to all connected Runners… there is no 'safe' editing mode"; protection is the **Blind Edit Warning** and timeline lock states (**Hands Off** mode) | [V] | https://docs.dataton.com/watchout-7-new/watchout/playback/going-online.html |
| "Going online" is a **verification process** (connections, asset distribution, output check), not a mode | [V] | same |
| Asset Manager **optimises media on import** and distributes it in the background; dynamic asset versioning without stopping the show | [V] | architectural-differences page |

**Lesson:** the strongest "controller dies" story in the field. Its limits: a Director that dies takes the *control*
with it (Runners free-wheel on what they have); redundancy is by name collision, with the docs disagreeing about
whether it is automatic; edits are live by default.

## 5. PIXERA (AV Stumpfl) — locks, multi-user backup, Hub monitoring

| Fact | Status | Source |
|---|---|---|
| **26.1 Clip / Timeline / Layer Lock**: "lock elements… so that they cannot be accidentally changed… This can also be locked during playback **without affecting the playback functionality**"; a locked timeline stays fully inspectable (effects, keyframes, parameters open, not editable); no creating/deleting layers, cues or clips; a locked layer allows no keyframe edits; a locked clip cannot be selected or edited but follows layer/timeline changes | [V] | https://help.pixera.one/changelogs-version-overviews/pixera-261-overview-changelog (undated) |
| In multi-user, a lock is **not transferred immediately**: only when the first user pushes the timeline | [V] | same |
| **Manager Backup**: Full Backup (the backup mirrors the live system's playback; multi-user since 2.0) vs Partial Backup (outputs not active; takes over dynamically). Settings: **Treat Closed Members As Lost**, **Automatically Activate Local Outputs When Member Lost**, **Automatically Take Over Clients From Lost Members**; manual **Take Over All Clients** | [V] | https://help.pixera.one/manager-backup ; https://help.pixera.one/multiuser-and-backup/manager-backup |
| "We do not advise activating the automatic settings in live control environments. These features are designed for fixed installation situations where no technical person might be on the location" | [V] | manager-backup page |
| **Client backup** by **Usage Presets** (the client's output config, mappings, aggregates) copied to the backup live system; transfer modes Copy / Applied Preset / Update then Apply / Only Control Action; **System Lost** and **System Available** actions (e.g. `BackupTrigger.MatrixSwitch`) drive the matrix; manual "Switch to Backup" in a Control UI element; automatic switch when the playout client goes offline — recommended for fixed installs only | [V] | https://help.pixera.one/multiuser-and-backup/client-backup |
| **Full 1:1 redundancy**: a mirrored backup system with the same naming plays along (incremental update + follow timeline); on failure "simply switch the output matrix" | [V] | same |
| **Hybrid Mode Backup** (25.2): one server backs up either the Manager or a Client | [V] | https://help.pixera.one/hybrid-mode-backup |
| Multi-user has **no lead→follow**: once a reference is declared, directors are equal; changes pushed/pulled or incremental in real time; "Never deactivate Outputs at Backup" option | [V] | https://help.pixera.one/multiuser-and-backup/multi-user-setup |
| **PIXERA Hub**: every server on the network, CPU/GPU/RAM dashboards (Performance Monitoring), remote access (VNC), support bundle with logs and dumps, version distribution | [V] | https://pixera.helpjuice.com/en_US/pixera-hub/pixera-hub-overview |
| Render engine health words: **"WARNING display sync timed out"** (display not responding > 1 s; RX shuts down after 3 min if it persists), **"FATAL DISPLAY SYNC DEADLOCKED"** (30 s; RX closes and reopens hung displays), **"FATAL WATCHDOG TIMEOUT"** (render thread blocked 3 min → dump/restart) | [V] | https://help.pixera.one/troubleshooting/pixera-logs-and-their-meanings |
| Commissioning guide: on-screen statistics (refresh rate, incomplete and dropped frames), output identification, test patterns, performance monitoring | [V] | https://pixera.helpjuice.com/pixera-system-commissioning-guide |
| Pixera user rights / role-based permissions | [U] | not found this session |
| 25.2 fixed engine start/stop creating undo entries ("live actions not in undo") | [R] | docs/UX.md §7.2 cites https://help.pixera.one/pixera-252-overview-changelog |

**Lesson:** locks that **block edits, never playback or inspection**; backup by named "usage"; and the vendor's own
advice that automatic takeover is for unattended installs, manual for live shows.

## 6. disguise (Designer) — Director / Actor / Understudy, failover, monitoring

| Fact | Status | Source |
|---|---|---|
| Roles: **Director** (one per network; controls the session), **Actor** (outputs its Feed scene from the Director's timeline), **Understudy** (takes over from a Director or an Actor), **Editor** (no output role). Roles need disguise hardware (Starter/Full on non-disguise hardware: Editor only) | [V] | https://help.disguise.one/designer/networking/setup-d3net ; https://help.disguise.one/designer/networking/d3net-overview |
| Failover is triggered **automatically on timeout**, by a **remote transport command**, by the **Session widget's replace button**, or via the **Failover API** | [V] | https://help.disguise.one/designer/networking/failover (last updated Aug 19, 2026) |
| **Failover Timeout**: seconds until the Understudy takes over a non-responsive machine; **0 disables** | [V] | setup-d3net page |
| On replace, the failed machine is ignored ("No further communication from the failed machine is accepted"); it is told to stop sending network data "but… Designer cannot guarantee that message will be received"; the Understudy takes the role and feeds and **sends the matrix preset** | [V] | failover page |
| A failed-over **Director** disconnects **live update for every machine**: "All edits made on any machine will no longer be transferred… until the session is restarted"; an Actor's failover keeps live update for the Understudy | [V] | failover page |
| Restore is a press; disguise **does not recommend restoring mid-show** | [V] | failover page |
| Automatic matrix preset switching only with one Understudy per target | [V] | setup-d3net page |
| On a network failure "all Actor machines… will continue to output from their current position on the playhead"; external triggers (timecode, DMX for the Director) must also reach the Director's Understudy | [V] | d3net-overview page |
| Monitoring: the Director shows each Actor's **fps** (60 green, < 60 red, "–" when not running); the API's health has four categories **Connection, Output, Genlock, Devices** with severities **ok, warning, error, busy, offline**; `GET /api/session/status/notifications`; Live Update WebSocket subscriptions such as `object.hasTakenOverFailedMachine()` | [V] | setup-d3net page; https://developer.disguise.one/api/guides/monitoring/ |
| `sessionSyncTimeout`: actors/understudies sync from the Director before it carries on (0 = 5 min default); `copyIPVfcMulticastOnFailOver` | [V] | https://help.disguise.one/designer/d3manager/advanced-machine-settings |
| OmniCal: one-click recalibration if only projectors moved; a calibration score; out-of-focus detection (r32.4) | [R] | docs/COMPETITIVE.md §2 |

**Lesson:** failover is a **role transfer plus a route switch**, the failed machine is **fenced by being ignored**,
and the price of a Director failover is that **editing stops**. PlayMap can beat that (§10).

## 7. Green Hippo Hippotizer, Pandoras Box, 7thSense

| Fact | Status | Source |
|---|---|---|
| Hippotizer System Recovery keeps three OS backups (Factory, Rental with a password, User) of the C: drive only | [V] | https://www.manula.com/manuals/green-hippo/hippotizer-v4/4.6.2/en/topic/v4-system-recovery |
| Hippotizer **Watchfolders** keep primary and backup media in step by watching the same folder; **Auto Sync to Media Managers**; Advanced Sync sends media, the media map, names, notes and tags | [V] | https://www.manula.com/manuals/green-hippo/hippotizer-v4/4.3.3/en/topic/watchfolders ; …/4.9.3/en/topic/advanced-sync |
| Hippotizer live hot-backup mirroring over HippoNet (a "backup" engine following a master's pins) | [U] | not confirmed this session; common practice is two engines fed the same console DMX/timecode [R] |
| **Pandoras Box Multi-User**: a **Main Manager** (connected to the clients, hosts the sync session, can be clock leader) and **Sub Managers**; "Take Over All Sites in Domain" makes a Sub Manager the Main; DMX/TCP/widget outputs "manifest" only on the Main, so a takeover re-establishes them without doubles; **Blind Mode** exists for Sub Managers ("the Main Manager… cannot enter the Blind Mode") | [V] | https://pandorasboxhelpfile.com/home/multi-user.htm |
| Pandoras Box: "Full redundant or partial backup setups are possible with direct IP switching or live re-patching" | [V] (distributor copy, no detail) | https://tsm-int.com/product/pandoras-box-server/ |
| **7thSense StackSignage**: a backup Scheduler PC; backup media servers "switched in as a fail-safe" by **setting the display switcher to the backup**; live/backup roles reassigned **by hand** (for logging and billing) | [V] | https://portal.7thsense.one/user-guides/pdf-library/Delta%20web%20interface%20apps/M727-1%20StackSignage%202.2%20User%20Guide.pdf |
| 7thSense Delta: OS clone and Stack Backup & Restore of configurations (geometry, blends, colour, shows, sequences) | [V] | https://portal.7thsense.one/user-guides/M285-delta-server-hardware-operation/hw_backup_and_recovery.html ; https://portal.7thsense.one/user-guides/MC255-managing-servers/sf_backup_restore_restore.html |

**Lesson:** across the whole field, the backup **route switch is external** (a matrix, a display switcher, a
projector input) and the **operator decides** in live shows; only fixed installs automate.

## 8. Projector dual-input failover (the switch that is already in the truck)

| Fact | Status | Source |
|---|---|---|
| Panasonic **[BACKUP INPUT SETTING]**: [BACKUP INPUT MODE] (OFF or an input pair), [PRIMARY INPUT]/[SECONDARY INPUT], **[AUTOMATIC SWITCHING] ENABLE/DISABLE** | [V] | Panasonic PT-RZ16K operating instructions p.127 (https://www.manualslib.com/manual/2665078/Panasonic-Pt-Rz16ku.html?page=127); PT-SRZ34K p.186; PT-RZ14K p.142 |
| Ready only when: mode not OFF, **the same signal on the primary and secondary inputs**, and the primary or secondary is displayed; **[BACKUP INPUT STATUS]** reads **ACTIVE** when a switch is possible, else **INACTIVE** | [V] | same |
| With ACTIVE, switching between the pair is **seamless**; with AUTOMATIC SWITCHING it switches to the backup input "if a disruption occurs in the input signal"; afterwards status is INACTIVE until the original returns, and the **current input is kept** (no automatic switch-back) | [V] | same |
| Panasonic markets it as "switching to the backup input signal immediately even when the input signal is discontinued" (PT-MZ20K, PT-RZ12K, PT-RZ31K manuals; RZ31K manual dated 2025-08) | [V] | https://eu.connect.panasonic.com/sites/default/files/media/document/2025-08/rz31k_manual_en_0.pdf |
| The status is shown on the projector's STATUS screen and input guide | [V] | PT-RZ14K p.142 |
| A network query for [BACKUP INPUT STATUS] (Panasonic protocol command) | [U] | not found; PlayMap would read it with `DEVICE RAW` once a command is confirmed |
| Christie, Barco and Epson equivalents (input failover / "backup source") | [U] | not verified this session |
| PJLink class 1/2 can set and read the input (`INPT`), and class 2 notifies input changes (`%2INPT=`) | [V-local] | `src/PlayMap.Devices/Drivers/PjLink/PjLinkDriver.cs`, `PjLinkProtocol.cs` |

**Lesson:** for a **hot mirror** whose signal is identical (same raster, same timing, same frames), a Panasonic
projector switches **by itself, seamlessly**, on loss of signal — the fastest failover there is, with no PC in the
decision. It needs the two signals to be the same, which is exactly what a frame-identical mirror produces.

## 9. Broadcast practice — automatic changeover, black / freeze / safe

| Fact | Status | Source |
|---|---|---|
| Automatic changeover (ACO) cards switch main → backup on **loss of signal (LOS), black detect, freeze detect** (and audio silence); thresholds and persistence ("hold-off") are user-configurable | [V] | Evertz 7700ACO2-HD https://evertz.com/products/7700ACO2-HD ; Cobalt 9940-ACO https://cobaltdigital.com/products/9940-aco/ |
| **Bypass relay**: the main program output is relay-protected, so a powered-off changeover still passes the main (or latches the selected path) | [V] | Evertz; Cobalt ("latching relay bypass retains manual or failover input-to-output routing even in the event of power failure") |
| **Clean switching** needs both inputs timed: Evertz SoftSwitch "clean video and popless embedded audio switching"; frame-sync ACO cards switch "seamlessly using multiple input frame sync" | [V] | Evertz; Vidovation PKA-5210 https://www.vidovation.com/product/pka-5210-automatic-changeover-protection-switch-with-framesync-opengear/ |
| When both inputs fail: insert a **test signal with a character ID** or an uploaded **trouble slate** — a defined safe look, not black | [V] | PKA-5210; Cobalt 9940-ACO (+T-SLATE) |
| Software changeover: best-source selection by number and severity of faults, **hysteresis** against flapping, manual override, router API / GPO switching, bypass pass-through | [V] | SkyLark SL NEO Changeover https://skylark.tv/products/software-new/transmission-telecom-operators/sl-neo-changeover/ |
| A Blackmagic Videohub crosspoint switch is not frame-synchronised (a visible glitch on switch) | [R] | docs/COMPETITIVE.md §5c |

**Black vs freeze vs safe.** Broadcast treats them as three different faults and three different outputs: black and
freeze are *detected* conditions that trigger a changeover, and the *output* when everything is lost is a safe look (a
slate, a test signal with an ID) — never an unexplained black. For a projection show the equivalents are: **black**
(true black, shutter or zero signal — the default for a sight-line hazard), **freeze** (hold the last frame — hides a
transient fault, wrong for long faults), **safe look** (a logo, a dim wash, a holding image — the audience sees a
designed moment).

## 10. Patterns (sister app) — what the ecosystem already does

| Fact | Status | Source |
|---|---|---|
| **The armed desk**: arm (operator policy) + outputs live = a pure table (`LivePolicy`) of verbs that do not start: calibration run/demo/apply/undo, update, restart — each refusal says what lifts it ("DISARM first (or OUTPUTS OFF)"). Content is never refused. There is "no fourth thing called Show Mode" | [V-local] | `src/Patterns.Core/Services/LivePolicy.cs`; `docs/PLAN.md` §64 |
| A caller's plan that would replace the running stack **waits for DISARM**; notes land at once | [V-local] | `docs/PLAN.md` §64.2 |
| The executor's one gate: armed, hold, blackout, executing, **standby-id fence, 300 ms lockout**, confirm window; STOP ALL two-press on the phone remote; a relaunch restores the caller's place **disarmed** | [V-local] | `docs/PLAN.md` §1 table rows 4, 5 and the remote page |
| **Show lock**: notifications, system sounds, other apps' audio, Sticky Keys shortcuts, sleep, the Windows key held off; Windows Update read only; a crash leaves a receipt that puts everything back | [V-local] | `docs/SHOW-MACHINE.md` |
| **Twin**: MAIN and STANDBY, the show and its sections and the air record sent on a proved-key link, a beat a second (five missed = gone), TAKE OVER / TAKE BACK through a **wall switch whose confirmation level** (sent, delivered, accepted, observed) decides whether an automatic takeover may proceed; an ownership record fences two pictures | [V-local] | `src/Patterns.Core/Services/Twin.cs`; `docs/DRILL.md`; `docs/PLAN.md` §48–§63, §61.3 |
| **The redundancy drill**: 22 filmed scenarios; "No scenario may show two pictures on the wall"; dark no longer than the switcher's own switching time plus the first frame | [V-local] | `docs/DRILL.md` |
| **Super Check**, **Commissioning** (7 stages: Discover, Assign, Contract, Capability, OutputTest, Verify, KnownGood), **ShowReady** bar (5 steps), Rig Day games | [V-local] | `SuperCheck.cs`, `Commissioning.cs`, `docs/PLAN.md` §56, §83 |

(The Eye and how a peer appears in it are in `research/patterns-eye.md`.)

---

## 11. The best-in-class combined design for PlayMap

The field's best ideas, combined, and the places where PlayMap's architecture (content = f(leader time), one writer,
the verbs) lets it go further.

### 11.1 Show Mode without a password (QLab + Patterns' table)

- `SHOW ARM` / `SHOW DISARM`: a two-word verb, a header toggle and a Companion key; **no password** (QLab). Arming is
  a node fact in STATE and the context, journalled with who and when, kept across an engine restart (the restriction
  is the safe side).
- Armed is **a table, not a mode** (Patterns' `LivePolicy`): one pure function `ShowPolicy.Decide(verb, line, role,
  facts)` gives Allow, Refuse (with what lifts it), Propose (an assistant's line goes to the queue) or Stage (into a
  blind draft). Roles come from the caller: the desk operator, a remote client, an observer, an assistant, an approved
  proposal. The table is UX.md §7.1: the operator keeps transport, blackout, PANIC and blind edit; mapping and layout
  are locked; remote clients are transport-only; an assistant can only propose.
- QLab's "restrict all collaborators to view-only" is one switch (`RemoteViewOnlyWhenArmed`).
- QLab's double-GO protection is `MinGoMs` (on when armed by default, 300 ms like Patterns' lockout) and a
  refused GO says so.
- Heavy work (scan, solve, transcode, segmentation, local LLM, full preflight decode) is refused while armed (UX §7.2).

### 11.2 Blind edit committed at a boundary (Millumin + Watchout + Pandoras Box, then better)

- Millumin's Silent Edition is global and commits when you switch it off; Watchout 7 has no offline mode at all;
  Pandoras Box's Blind Mode is for Sub Managers. PlayMap: `BLIND ON` forks a **draft root** for the session (or `ALL`
  sessions), edits land in the draft, previews can show the draft, `BLIND DIFF` reads as a list of field changes,
  and `BLIND COMMIT` lands it as **one undo step at a show boundary**: `NOW`, `AT +ms|<unix ms>`, `NEXT [<playlist>]`
  (the current item's end), `ON GO [<playlist>]` (with the next GO — the new content and the new item start on the
  same vblank).
- Because a frame is f(project root, anchors, leader time), the commit is a **(root, leader instant)** pair: the render
  thread picks the pending root by leader time, so the switch is frame-exact and identical on every mirror.
- Conflicts with live edits made since the fork are listed by entity and refused unless `FORCE`.

### 11.3 Panic with defined outcomes — per output (Mitti, QLab, broadcast)

- Outcomes (named like Mitti's, extended per output like an ACO's safe slate): `black` (fade out and hold;
  transports pause — Mitti's Fade out & Pause), `black-run` (fade out, transports run on — lifting returns to the show
  as it is now), `rewind` (fade out, rewind and pause), `next` (fade out, cue the next item), `freeze` (hold the frame),
  `safe` (dip through black to the output's safe look), `shutter` (close the projector's shutter through its device
  link, picture held), `effects-off` (bypass all effects).
- Each output has its own defined outcome and fade; a bare `PANIC` applies every output's own; QLab's rule holds:
  **a second PANIC during the fade is the hard stop** (instant black). `PANIC CLEAR [FADE ms]` lifts it.
- Panic is **never** refused for a paired operator or remote, armed or not, and **never** opens a dialog; on a mirrored
  node it is scheduled a few frames ahead so every mirror fades on the same frames.

### 11.4 Locks (Pixera 26.1)

- `LOCK <id>|<area>` / `UNLOCK`: entity locks (playlist, surface, projector, output, canvas, layer, generator, device)
  and area locks (`mapping`, `layout`, `content`, `playlists`, `media`, `devices`, `network`). **A lock blocks edits,
  never playback or inspection** (Pixera). Locks live in the project (they travel with the show), are not in undo,
  and are enforced **once, at the store**: a pure guard compares the old and new roots and refuses any edit that
  changes a locked entity — reference equality on immutable records makes it O(locks), whatever verb asked.
- Armed adds the `mapping` and `layout` area locks implicitly.

### 11.5 Preflight (Watchout's "going online" as a verb, Patterns' Super Check, Pixera's commissioning)

- `PREFLIGHT [QUICK|FULL] [ALL|<node>…]`: QUICK ≤ 10 s per node, facts only (allowed armed); FULL ≤ 90 s for 4 nodes
  and 8 outputs (UX.md budget), opens every scheduled asset's decoder at show resolution on each node, decodes the
  first frame and one mid frame, proves the hardware path (never a silent CPU fallback), soaks presentation for up to
  60 s, reads every projector (power, shutter, input, light hours, faults, Panasonic backup input ACTIVE), checks sync
  (offset, uncertainty, lock verdict), mirrors (project hash, media present, frame match), links, panic outcomes and
  safe looks, and lists every non-deterministic source ("breaks mirror backup"). Each row: section, item, light, value,
  note, the node, and **the verb that fixes it**. A five-step show-ready bar as Patterns' `ShowReady`.

### 11.6 Redundancy (Watchout + Pixera + disguise + Patterns' twin + broadcast ACO + Panasonic backup input)

- **Mirroring by construction.** A backup node adopts the primary's project revision, its anchors and its air, each
  with its leader instant; with a small **mirror lead** (live changes scheduled a few frames ahead on a mirrored
  primary) the backup renders **the same frame on the same vblank**, blackout and panic included. With framelock the
  two signals are timing-identical, which is what a Panasonic projector's seamless backup input needs.
- **Triggers:** silence past a timeout (disguise's Failover Timeout; 0 = off), output health (a window lost, a device
  lost), the projector's own LOS switch (observed, reported), and the operator's press. Automatic takeover is **off by
  default for live shows** and on for installs (Pixera's advice), and never switches back by itself (Panasonic,
  disguise).
- **Route:** projector input switch through PJLink and native protocols, a router crosspoint, a generic line, or the
  projector's own automatic switching; each step answered with its stage (refused, unreached, sent, rejected,
  accepted, observed) and an automatic takeover requires the configured level (Patterns' wall-switch confirmation).
- **Fencing:** an epoch number per takeover; the old primary, when it is heard again, sees a higher epoch and holds
  its outputs; the route is the fence (Patterns' drill: never two pictures).
- **Controller death:** followers and mirrors free-wheel on the leader's last offset and drift and keep playing their
  playlists (Watchout's "Runners continue"); a named succession list picks the next leader deterministically, whose
  show clock continues the old leader's line; **editing continues after failover** (beats disguise's Director failover).

### 11.7 A health view of every machine (Pixera Hub, disguise monitoring, Patterns' Eye)

- `HEALTH` per node (outputs fps/dropped/path/lock, projectors, sync, media, machine CPU/GPU/RAM/disk/temperatures,
  build, project revision, armed/panic, mirror state), `SUBSCRIBE HEALTH` at 1 Hz, `GET /api/fleet` on any node for
  every PlayMap node it hears, disguise's four categories (connection, output, genlock, devices) as the board's
  columns, a 24 h ring of one-minute aggregates and a replay (Patterns' `EYE AT`).
- The desk's Network drawer becomes the fleet board; `/fleet` serves the same board to any browser and phone; the
  context carries PlayMap's things and links in the Eye's vocabulary so Patterns' God's Eye draws them
  (`research/patterns-eye.md`).

## 12. Design implications for PlayMap (summary)

1. Show Mode is a **policy table at the one door** (the router), keyed by the caller's role; no password; it persists
   across an engine restart; it never blocks transport, blackout or panic for the operator.
2. Blind edit is a **draft root** committed as **(root, leader instant)**: frame-exact, one undo step, identical on mirrors.
3. Panic is **per output** with named outcomes and fades that are **pure functions of leader time**; the second panic
   is the hard stop; panic is never refused for a paired operator and never opens a dialog.
4. Locks block edits only, are enforced by **one guard in `ProjectStore`** by reference comparison, travel with the
   project and are not in undo.
5. Preflight is a verb with a time budget, rows with fix verbs, and pluggable checks other packages contribute.
6. Redundancy rides on what PlayMap already is: **mirror by construction** (project revision + anchors + air, each at a
   leader instant, with a measured mirror lead), route switching with **confirmation levels**, **epoch fencing**,
   **manual by default** for live shows, **no automatic switch-back**, **editing continues after failover**.
7. A Panasonic projector's **seamless backup input** turns a frame-identical hot mirror into a no-PC, no-gap failover;
   preflight must check **BACKUP INPUT STATUS = ACTIVE** (command [U]; read via `DEVICE RAW` once confirmed).
8. The fleet board borrows disguise's health categories, Pixera Hub's per-machine dashboard and Patterns' Eye (lights by
   evidence, a problems queue, lenses, replay).
