# WP "desk": the desk surface for round 2

*Design for the Avalonia desk (`src/PlayMap.App`) across all 21 round-2 packages, on main at `cb3cfc0` (13 Oct 2026).
The desk sends verbs and reads STATE; it has no private API. Every button, right-click entry and shortcut below names
the line it sends. Facts marked **measured** were measured in this container (§9). Time targets come from docs/UX.md
§5.0; none is measured yet.*

---

## 0. The decision in one paragraph

Round 2 adds about 300 verbs and 20 STATE sections. Giving each feature its own panel would bury the operator. So the
desk keeps **three workspaces** (PLAY, MAP, SCAN) and adds **one show strip**: ARM, BLACKOUT, PANIC and a Ready light,
in every workspace. It adds **one attention bar** that shows only what needs a person now. It adds **four right-hand
drawers**, each with tabs: Outputs, Inputs, Health and Network. The drawers replace today's two fixed drawers. Most
features add no panel at all: they add **badges** on existing rows, **sections** in the existing inspector, and
**entries** in the existing right-click menus. Five generic parts carry most of the new UI, so each feature is mostly
data:

- a status row (light, words, value, a **Fix** button that runs the fix line);
- a banner;
- a score ring;
- a hold-to-confirm button;
- one modulation socket on every number.

**Simple** and **Pro** are two presentations of the same verbs. Simple hides controls; it never disables them. Right-click
menus and the show strip are the same in both.

The work splits into a small **desk spine** (D0) and three packages with disjoint files: **D1 Show & health**, **D2
Content & media** and **D3 Map & calibration**. The spine's seams are partial-method hooks, a module view locator,
memoised STATE sections and fake-engine hooks. They were prototyped here and the desk tests stay green (§9).

---

## 1. The bar, and how the desk meets it

| Peer habit (UX.md sources) | The desk this round |
|---|---|
| QLab: Show Mode with no password; transport stays live | ARM in the strip, one press, frame turns red. Controls the gate would refuse go grey with the refusal words **before** the press (§2.8) |
| Mitti: panic with defined outcomes | PANIC never opens a dialog. Its tooltip lists each output's outcome. Right-click gives the nine outcomes, and a second press within the fade is HARD. The red banner has **Clear** with a fade |
| Millumin / Watchout: blind editing | BLIND chip; a violet inner frame round the workspace; a diff panel with field words; commit at Now / Next item / On GO / At |
| Pixera: locks, a health view of every machine | Lock/Unlock in every entity menu, padlock badges, area locks under ARM's menu; Health ▸ Fleet board in Patterns' Eye vocabulary (lenses, problems queue) |
| disguise: a score that explains itself; a fallback | Score ring on the projector card; each term has words, **Fix** (a real verb line) and **Show on wall**; a history strip with the fallback ringed |
| Scalable / VYV: re-solve the one that moved; history | A red banner "Projector 2 moved ≈ 16 px" → **Re-solve P2**, one press; staged results → **Accept / Discard** |
| Watchout: optimise on import; nodes run on when the controller dies | Media badges and a jobs strip; an ask-mode banner. On link loss the desk offers **Attach to FOH-B** (the backup it knew) and the show plays on |
| MadMapper / HeavyM: a quad, editing while looking at the wall | "Light a wall" first-run guide; Q for quad, four clicks on the wall with a live wall cursor; arrows nudge in wall pixels (Alt = 0.1 px) |
| Resolume / MadMapper: learn mode | **Bind ▸** in every right-click menu, built from that menu's own actions; one press, move a control, done. Esc cancels |
| Bitfocus Companion | Network ▸ Decks lists connected decks; every tooltip names the line a Companion key would send |

**Beyond the peers:**
- the menu, the toolbar, the key, Companion and MCP share one vocabulary, and the tooltip shows the line;
- greying when armed comes from the engine's own policy table (Core), so the desk never disagrees with the gate;
- every red row offers its fix as a verb.

---

## 2. The frame

### 2.1 Layout

```
1600 px, armed, a chase and audio present
╔══════════════════════ mode frame: 3 px red = ARMED · amber = patterns/overlay on the wall · none = rehearsal ══════════════════════╗
║ ▣ Gala ·saved ↶ ↷ │PLAY│MAP│SCAN│ Simple|Pro ║ Show ▸ Intro ⏮ ▶ ■ ⏭ 1:02 −2:28 │ TC 01:02:03:04 LTC │ ♩128.0 ●○○○ TAP ║                  ║
║                                                         ║ ● Ready 4/5 │ BLIND 7 │ ARMED · Desk │ ⊘ Blackout │ PANIC ║ ▣ ⇆ ⇄●P●S  AI·2  ? ║
╟────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╢
║ ● PANIC black since 21:04:05 by Companion: FOH deck — Left: safe look · Right: black                           [Clear ▾]          ║ ← attention bar
║ ● FOH-A silent 2.0 s — FAILOVER to FOH-B                                                                       [Failover] [Hold]  ║   (absent when empty)
╟──────────────┬───────────────────────────────────────────────────────────────────┬────────────────────────╥──────────────────────╢
║ Library /    │ Workspace (PLAY lanes · MAP 3D ⇄ projector view · SCAN steps)      │ Inspector              ║ Drawer (one open):   ║
║ Outliner /   │  ┊ violet inner frame while blind-editing                         │  object card           ║  Outputs · Inputs ·  ║
║ Guide        │                                                                   │  + sections (FX, Cal…) ║  Health · Network    ║
║              │ Blind panel (docked, only while blind)                            │                        ║  tabs; ⤢ wide        ║
╟──────────────┴───────────────────────────────────────────────────────────────────┴────────────────────────╨──────────────────────╢
║ ● Demo node · live │ last thing that happened (learn line: "Move a control for Blackout — Esc cancels 24 s") │ F1 · right-click     ║
╚════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝
```

- **Below 1366 px the top bar wraps into two rows.**
  - Row 1: identity, workspaces and the drawer buttons.
  - Row 2: transport, TC, tempo and the show cluster.
- **Compact forms, in this order as the width falls:**
  1. the transport drops its position column and the VT chip;
  2. TC drops its source word;
  3. tempo drops its beat dots;
  4. drawer pills become icons;
  5. Simple|Pro moves into the project menu.
- **The show cluster never shrinks.** It is the Ready light, BLIND, ARM, Blackout and PANIC.
- **Measured** (§9):
  - today's bar wants 1686 px and is already clipped by 47 px at the 1100 px minimum;
  - adding the show strip makes it 1883 px;
  - at 1600 px the prototype strip overlapped the transport.
- A spine test asserts that no top-bar child ends beyond the window at 1100, 1280, 1366, 1600 and 1920 px.

### 2.2 The show strip (top bar, every workspace)

| Element | Idle (rehearsal) | Busy | Armed | Fault | Sends |
|---|---|---|---|---|---|
| **Ready light** `● Ready 4/5` | Worst light of: STATE light, `fleet.light`, `eye.worstLight`, `show.preflight.light`, `sync.verdict`, worst output `present.missed60s`, `calibration.moved`. Text = show-ready `done/of` | "Checking 12/44" with a spinner while `show.preflight.running` | Unchanged; the tooltip adds "armed since 20:31" | Red, with the worst words ("FOH-B: P3 shutter closed"). A missed-vblank chip `⚠ 2 missed` sits beside it, hidden at 0 | Click: Health drawer (Fleet when a fleet exists, else Preflight). Right-click: **Run preflight** `PREFLIGHT QUICK [ALL]` · **Full preflight** `PREFLIGHT FULL` · **Next problem** `FLEET NEXT` |
| **BLIND n** (only while a draft exists) | — | "commits at next item 4.2 s" while `show.blind.commit` is pending | The normal way to edit mapping while armed | Amber "1 conflict" | Click: opens the Blind panel. Ctrl+Shift+B: `BLIND ON` (or focuses the panel) |
| **ARM** | Outline pill "ARM" | — | Solid red "ARMED · Desk · 20:31" (who and since, from `show.by/since`) | Tooltip: "3 refused in the last minute: Companion: FOH deck — WARP NODE …" (`show.refused`) | `SHOW ARM` / `SHOW DISARM` (Ctrl+Shift+A). Right-click is the **Show menu**: Arm all nodes `SHOW ARM ALL` · Lock area ▸ mapping/layout/content/playlists/media/devices/network `LOCK AREA <a>` · Unlock all `UNLOCK ALL` · Edit blind `BLIND ON` · Rules ▸ (Pro) Min GO gap ▸ 0/300/1000 ms `SHOW SET MINGO <ms> ARMED` · Remote view only `SHOW SET REMOTE-VIEW-ONLY ON\|OFF` · Expect armed while playing `SHOW SET EXPECT-ARMED ON\|OFF` · Esc is panic (desk pref) |
| **Blackout** (today's button) | "Blackout" | — | Live | Solid when `air.blackout`. A thin level bar appears under it when `air.master.level < 1` | `BLACKOUT ON\|OFF` (Ctrl+B). Right-click (Pro): Master ▸ 100 / 75 / 50 / 0 % over 2 s `MASTER <n> FADE 2000` |
| **PANIC** | Red outline. The tooltip lists each output's planned outcome ("Left: safe look 2 s · Right: black 1 s") | Pulses during the fade; a second press within the fade = HARD (engine rule) | Live; never refused | Solid red while `show.panic` exists. The banner carries Clear | `PANIC` (F12; Esc in PLAY when the desk pref is on). Right-click: `PANIC BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD` · Panic plans… (Outputs ▸ Plan) |
| **TC chip** (hidden without a source or chase) | `01:02:03:04` monospace, coloured by the focused playlist's chase: green locked, grey waiting, amber freewheel (with the countdown), red held, violet disengaged. Small source word "LTC 25" | "acquiring" (amber) | Engage/Disengage greyed when the policy refuses | Red "LTC lost 3 s" | Click: Inputs ▸ Timecode. Right-click: Engage/Disengage `TIMECODE ENGAGE <pl> TOGGLE` · Reset counters `TIMECODE SOURCE RESET <src>` · Show source |
| **Tempo chip** (hidden without `audio`) | `♩ 128.0 ●○○○ 17.3`, a source chip (`DETECT ✓ 0.86` / `TAP` / `LINK 3` / `MIDI` / `TRACK`), **TAP**, and a mini level bar | "searching" in amber | TAP, RESYNC, nudge, ½ and ×2 stay live | "silent 12 s" amber; input lost red with its words | TAP `TEMPO TAP` (T in PLAY). Double-click the BPM to type, drag to change `TEMPO SET <bpm>`. Right-click: Resync ▸ beat/bar/phrase `TEMPO RESYNC BEAT\|BAR\|PHRASE` (R) · ½ `TEMPO HALF` · ×2 `TEMPO DOUBLE` · Nudge ‹ › `TEMPO NUDGE ±10` (hold: `FASTER`/`SLOWER`, then `STOP`) · Hold modulation `MOD BYPASS ON ALL` · Audio… (Inputs ▸ Audio) |

**The beat dots** are drawn on the desk from `audio.tempo.epochUnixMs` and `bpm`. They make no traffic. This is a display
only, and the node's beat stays the truth.

### 2.3 Mode frame

| Frame | When (from STATE) |
|---|---|
| None (2 px neutral border) | Rehearsal, with nothing but the show on the wall |
| **Amber 3 px** | Anything not the show is on a wall: `air.overlay.on`, `air.pattern`, `air.identify`, a running scan, align run or calibration check (patterns), `SYNC MARKS`, an LED test, `SCAN COVERAGE` |
| **Red 4 px** | `show.armed` |
| **Violet inner frame** (round the workspace only) | `show.blind` is open for this desk. It combines with red: armed and blind-editing |

### 2.4 The attention bar

One row per banner. Banners are sorted worst first; at most two are shown, with "+3 more" to expand.

- Banners are rebuilt from STATE on every push. A banner disappears when its cause clears, and none can go stale.
- Toasts stay for the result of what this desk sent; banners are for the state of the show.
- Each banner's buttons are `DeskAction`s.

| Banner (owner) | Light | Words | Buttons → verbs |
|---|---|---|---|
| Panic (D1) | red | "PANIC black since 21:04:05 by Companion: FOH deck — Left: safe look · Right: black" | **Clear ▾** (fade 0 / 1 / 2 / 5 s) `PANIC CLEAR [FADE <ms>]` |
| Failover alarm (D1) | red | `fleet.alarm.words` | **Failover** `FAILOVER` · **Hold** `FAILOVER HOLD` |
| Fenced (D1) | red | "fenced: FOH-B has the show (epoch 8)" | **Attach to FOH-B** (desk-local connect) |
| Link lost while a fleet existed (D1) | red | "FOH-A does not answer — the show plays on. Its backup is FOH-B" | **Attach to FOH-B** · **Keep trying** |
| Leader gone (D1) | red | "leader gone — FLEET PROMOTE on the node that should lead" | **Promote this node** (hold) `FLEET PROMOTE` |
| Sync fell out (D1) | red | `faults[part=sync].words` | **Rejoin** `SYNC REJOIN` (greyed while armed, unless blacked out, with words) |
| Present fuse (D1) | red | "presenting through OpenGL: DXGI did not settle (…)" | **Try DXGI again** (hold) `PRESENT PATH DXGI` |
| Rehearsal while playing (D1) | amber | "rehearsal while playing — SHOW ARM before doors" (`ExpectArmed`) | **Arm** `SHOW ARM` |
| Chase held (D1) | red | "Main held: LTC lost 3 s" | **Disengage** `TIMECODE ENGAGE <pl> OFF` |
| Learn (D1) | amber | "Move a control for Blackout — Esc cancels (24 s)" | **Cancel** `BIND LEARN OFF` |
| Live input lost on air (D2) | red | "Arena lost — showing Loop B" | **Switch to primary** `INPUT SWITCH <in> PRIMARY` · **Reconnect** `INPUT RECONNECT <in>` |
| Optimise suggestion (D2) | info | "2 clips would play better as HAP Q (est 3 min, 4.2 GB)" | **Optimise** `MEDIA OPTIMISE SUGGESTED` · **Not now** (desk-local hide until the set changes) · **Never in this project** `MEDIA RULES MODE NEVER` |
| Projector moved (D3) | red | "Projector 2 moved ≈ 16 px" (`calibration.moved`, check words) | **Re-solve P2** `CALIBRATION RESOLVE pj…` · **Check all** `CALIBRATION CHECK ALL` |
| Camera moved (D3) | info | "Camera 1 moved: re-solved from P1 and P3" | — |
| Re-solve staged (D3) | amber | "P2 re-solve staged (score 1.9 > gate 1.5)" | **Accept** `CALIBRATION ACCEPT cs…` · **Discard** `CALIBRATION FORGET cs…` |
| Align staged (D3) | amber | "Main screen staged: 91 Good" | **Accept** `ALIGN ACCEPT ar…` · **Open** (Outputs ▸ Align) |
| Double correction (D3) | amber | "P2's own geometry is on while PlayMap corrects Left — two warps fight" | **Neutral** (hold) `DEVICE NEUTRAL dv…` |
| Suggestions waiting (D3) | amber | "3 surface suggestions wait for a person" | **Review** (SCAN step 5) |

### 2.5 Drawers (right side, one open at a time, each with tabs, ⤢ to take the whole workspace)

| Drawer (key) | Tabs (owner) | Simple shows |
|---|---|---|
| **Outputs** (Ctrl+Shift+O) | **Plan** (today's drawer + chips, panic plans; D1) · **Align** (D3) · **LED** (D3) · **Sends** (D2) | Plan, Align, LED node tiles |
| **Inputs** (Ctrl+Shift+I) | **Controllers** (D1) · **Timecode** (D1) · **Audio** (D2) | All three, trimmed |
| **Health** (Ctrl+Shift+H; the Ready light) | **Fleet** (D1) · **Preflight** (D1) · **Sync** (D1) · **Calibration** (D3) | Fleet (light, name, role, headline), Preflight problems only |
| **Network** (Ctrl+Shift+N) | **Links** (today's drawer; D1) · **Decks** (D1) · **Video I/O** (next round) | Links, Decks |

Live video inputs, effects and generators are content, so they live in the PLAY **Library** (D2): Files · Generators ·
Effects · Live.

### 2.6 Keyboard: the one table

New and changed keys. Everything else is unchanged. ShortcutMap stays the only key table; modules append their rows,
and F1 lists them all.

| Keys | Where | Does | Owner |
|---|---|---|---|
| **F12** | everywhere | `PANIC`. A second press within the fade = HARD. Never a dialog | D1 |
| Ctrl+Shift+A | everywhere | `SHOW ARM` / `SHOW DISARM` | D1 |
| Ctrl+Shift+B | everywhere | `BLIND ON`, or focus the Blind panel | D1 |
| Ctrl+Shift+I / H | everywhere | Inputs / Health drawer | D0 |
| Ctrl+Shift+P | everywhere | Simple ⇄ Pro (desk-local) | D0 |
| Ctrl+Right / Ctrl+Left | everywhere | `PLAYLIST NEXT <pl> EXPECT <it>` / `PLAYLIST PREV <pl> EXPECT <it>` (the GO fence; the expected item is the one the desk shows) | D1 |
| T / Shift+T | PLAY | `TEMPO TAP` / `TEMPO RESYNC BAR` (downbeat) | D2 |
| R | PLAY | `TEMPO RESYNC BEAT` | D2 |
| A (hold) | PLAY library, over an effect | `FX AUDITION <view> <target> <kind>`; on release `FX AUDITION <view> <target> OFF` | D2 |
| **S** | MAP | Select tool (was Q) | D3 |
| **Q** | MAP | Quad tool: four clicks, on the wall | D3 |
| D | MAP | LED tool (line/grid/ring by drag) | D3 |
| G | MAP | `OVERLAY GRID <op> 2` (flash grid) | D3 |
| O | MAP | On-wall editing on/off `OVERLAY ON\|OFF ON <op>` | D3 |
| C | MAP | Wall cursor on/off | D3 |
| L / Shift+L | MAP, quad selected | `SURFACE LATTICE <sf> 3x3` / `SURFACE LATTICE <sf> OFF` | D3 |
| Arrows / Shift / **Alt** | MAP, handle selected | `SURFACE CORNER <sf> <c> BY 1 0 PX` (Shift ×10, Alt ×0.1). A lattice node uses `SURFACE NODE … BY` | D3 |
| Tab / Shift+Tab | MAP | Next / previous handle (`HANDLE SELECT …`, so the wall shows it) | D3 |
| Enter | MAP | Close the pen path / finish the quad / accept | D3 |
| P, W, B, T, V, Delete | MAP | Unchanged (pin, warp, pen, test pattern, 3D ⇄ projector view, remove) | — |

**The conflicts this table settles:**

| Key | Claims from the specs | Decision |
|---|---|---|
| B | pen (today) vs bind mode (control, audio) | Pen keeps B. Bind mode is next round, on Ctrl+M |
| L | lattice (firstfive) vs LED tool (led) | Lattice keeps L. The LED tool is D |
| T | test pattern menu (MAP, today) vs tap tempo (audio) | Both are scoped: T opens the test pattern menu in MAP and taps tempo in PLAY |
| Q | select (today) vs quad (firstfive) | Quad takes Q. Select moves to S |
| Tab | 3D ⇄ projector view (UX.md §3.1) vs handles (firstfive) | V stays the view switch; Tab cycles handles |
| Esc | close and cancel (today), PANIC (showsafety), cancel learn (control), cancel task (calibops) | The Esc ladder (§2.7) |

### 2.7 The Esc ladder

Esc goes to the first rung that uses it. A module answers `TryEscape()` in its `EscapeOrder`.

| Rung | Order | When | Does |
|---|---|---|---|
| 0 | Pattern task | A scan, align run or calibration task from this desk is putting patterns on the wall | `SCAN CANCEL` / `ALIGN CANCEL` / `CALIBRATION CANCEL`. This is the safe direction: the wall goes back to the show |
| 1 | Learn | `control.learning` | `BIND LEARN OFF` |
| 2 | Draft | A pen path, quad corners or an LED stroke is in progress | Cancel the draft (desk-local; `CURSOR OFF`) |
| 3 | Overlays | Help, a drawer or the Blind panel's picker is open | Close it |
| 4 | Panic | PLAY workspace, and the desk pref "Esc is panic" is on | `PANIC`; a second Esc within the fade = HARD |
| 5 | Selection | MAP | Deselect |

**The Esc guard:** after rungs 0–3 use a key, Esc cannot reach rung 4 for 600 ms. Two quick Escs that close a drawer
therefore never panic.

### 2.8 Armed, desk-wide

- **Greying from the same table.**
  - The desk calls the Core `ShowPolicy` (showsafety M1) with role `desk`, the line, and the facts in `STATE.show`
    (armed, blind, locks).
  - It does this on every `DeskAction` refresh, through one spine hook, `DeskAction.Gate`.
  - A refused action is disabled. It wears a padlock glyph, and its tooltip carries the gate's words: "Not while armed:
    mapping is locked. SHOW DISARM on the desk, or BLIND ON to stage it."
  - Menu entries show greyed with "· armed".
  - Entity locks come from `STATE.show.locks` and the model's `Locked` flags. Entity ids in a line are found through
    `VerbParams` (companion M9).
- **Stays live:**
  - transport (with the fence) and Blackout;
  - PANIC and MASTER;
  - TAP, RESYNC and nudge;
  - `FX BYPASS ALL ON` and `MOD BYPASS ON ALL`;
  - FAILOVER and HOLD (the Fleet class);
  - `INPUT SWITCH` (as the policy says).
- **Heavy work** (scan, align, calibration check or resolve, optimise, FULL preflight, `SYNC CERTIFY`) is greyed with
  "heavy work is refused on an armed node".
- **On-wall overlay:** the engine strips it. The O toggle greys, saying "Blind edits show their overlay on the desk only".
- **Closing the desk while armed:** close and quit ask "The show is armed on FOH-A. Closing the desk does not stop the
  show. Close?". This is the one confirmation in the desk. It is about the desk, not the air.

### 2.9 Simple and Pro

A desk-local preference, persisted in the desk's `desk.json`. The toggle sits beside the workspaces; Ctrl+Shift+P.

1. **Same verbs, same project.** Simple hides; it never disables.
2. **Never trimmed:** right-click menus and the show strip are identical in both. The whole vocabulary is always one
   right-click away.
3. **A Pro-only setting that changes the air still shows in Simple, as a chip.** Examples: "custom curve", "blend:
   add", "scale ½". Simple never hides a fact the audience sees.
4. **Every trimmed panel ends with "n more in Pro"**, which switches.
5. **In Simple:**
   - the inspector shows the object card, the first parameter group and the first two numbers of each effect slot;
   - drawers open on their first tab;
   - numbers that only an engineer reads (ppm, µs, σ) are hidden.

| Feature | Simple | Pro adds |
|---|---|---|
| Show safety | ARM, PANIC, Blackout, Ready. Blind panel: diff, "Commit at next item", Discard. Preflight: problems only | Show rules, Master, commit options (AT, FORCE), all preflight rows, node columns |
| Fleet | Machine cards: light, name, role, headline, outputs as dots; Problems toggle | Facts lines, lenses, routes, succession, lead ms |
| Sync | Verdict card, Lead / Follow, Rejoin | Per-output phase and whisker, board block, Marks, Certify, latency |
| Present | Output chip and light | Display section: bits, latency, Prove |
| Timecode | TC chip, Chase ▸ menu, TC in | Sources' speed, errors, user bits; internal generator rate |
| Control | Bind ▸, badges, surfaces and bindings | Shape editor, monitor, OSC/DMX network, OSC address |
| Effects | 12 curated "Looks" + search; slot: bypass, Amount, two parameters | All families, every parameter, blend, scale, cost, presets, plugins, the shader editor |
| Audio | Tempo chip, input picker, gain | Spectrum, tempo panel, role, distance, curve/range/steps in the socket |
| Content | Presets by family; first group of parameters; Recall | Every group, Save, Morph, sting pad, GPU ms |
| Optimise | Badges, the banner, the jobs line | Jobs rows, Use ▸, the rules mode |
| HAP / zero-copy | One chip per media row | The "On this node" facts and fixes |
| Video I/O | Live tab tiles, drag to a surface | On loss, backup, pacing; Sends tab |
| Quad / on-wall | Q, four clicks, arrows, drop a clip | Lattice sizes, Alt fine nudge (works in both), PICK later |
| Calibration | Score ring and grade, worst words, Re-solve | Terms panel, history strip, confidence on the wall |
| Align | Align screens → Run → Accept | Plan details, per-device native settings, corners, colour mode |
| Facade | The guide's steps and primary buttons | Site numbers, priors, uncertainty, plan-light figures |
| Phone scan | QR, phone card, Scan, suggestions | Views table, Drop view, ON <projector> |
| LED | Node tiles, Test, Locate, On/Off | Patch grid, DMX monitor, colour and sampling fields |

---

## 3. Show and health (D1)

### 3.1 Show Mode, blind edit, panic, locks (showsafety)

**Where:**
- the show strip (§2.2);
- the attention bar (the Panic banner);
- the **Blind panel**, docked at the bottom of PLAY and MAP while a draft exists;
- Outputs ▸ Plan: the panic plan per output;
- Health ▸ Preflight;
- Lock / Unlock in every entity menu;
- ARM's right-click Show menu.

**States:**

| State | The operator sees |
|---|---|
| Idle | Outline ARM, red-outline PANIC, Ready light |
| Busy | Ready shows "Checking 12/44". BLIND shows "commits at next item (4.2 s)" with a countdown from `show.blind.commit.effectiveLeaderNs` |
| Armed | Red frame; red ARMED pill with who and since; mapping and layout greyed with padlocks and words |
| Fault | Panic: PANIC is solid, the banner is red, and every output tile shows its level bar from `show.panic.outputs[].level`. Blind conflicts: the diff rows go amber, and **Commit** becomes **Commit anyway (FORCE)** |

**Blind panel:**
- **The diff** comes from `BLIND DIFF`, fetched on open and again when `show.blind.changes` moves. One row per change:
  - "Left (output): warp node 3,2 moved; blend gamma 2.2 → 2.4";
  - clicking a row selects the entity (desk-local).
- **The boundary picker:**
  - Now `BLIND COMMIT NOW`;
  - Next item of ▸ <playlist> `BLIND COMMIT NEXT <pl>`;
  - On next GO ▸ <playlist> `BLIND COMMIT ON GO <pl>`;
  - At… (Pro) `BLIND COMMIT AT +<ms>`.
- **Commit** sends the chosen line, with ` FORCE` added when conflicts are shown.
- **Discard** sends `BLIND DISCARD`.
- **Default in Simple:** "Commit at next item of the focused playlist".
- **Built:** the panel opens from the chip under the attention bar, in every workspace, rather than docked; it offers Commit now
  and Commit at the next item of the focused playlist (`BLIND COMMIT NEXT`), Discard, and the draft view in MAP
  (`VIEW editor-draft SHOW editor DRAFT`); ON GO, AT, the countdown, the row's selection and the violet frame are not on the
  desk (docs/DESK.md).

**Panic plans** (Outputs ▸ Plan tile chip "On panic: Safe look · 2 s"). Right-click a tile:
- On panic ▸ Black / Run / Rewind / Next / Freeze / Safe look / Shutter / Effects off → `PANIC SET <op> <OUTCOME>`;
- Fade ▸ 0.5 / 1 / 2 / 5 s → `PANIC SET <op> <outcome> FADE <ms>`;
- Safe look ▸ <playlist | generator | colour | media> → `PANIC SET <op> SAFE SAFE <source>`.

**Locks:**
- Right-click any surface, projector, output, playlist or item → Lock `LOCK <id>` / Unlock `UNLOCK <id>`.
- A padlock badge marks a locked object. Its tooltip says who and why: "by Desk: 'show night'".

**GO fence:**
- The master transport and the playlist cards send `PLAYLIST NEXT <pl> EXPECT <it>`.
- A refusal ("the next item is now 'Outro', not 'Intro' — the key was out of date") shows as a reason chip at the button,
  and in the toast.

**Next round:**
- the dark-room show strip view (night red, current/next and countdowns, S3);
- parked edits (S1, `PARKED`);
- soft edit leases and their holders (S4);
- a per-preview panic override.

### 3.2 Preflight board (showsafety M6)

**Where:** Health ▸ Preflight.

**The board:**
- **Header:** the show-ready bar "show-ready 4/5 ▰▰▰▰▱ · media: 2 missing on FOH-B", then **Quick**
  `PREFLIGHT QUICK [ALL]` and **Full** `PREFLIGHT FULL [ALL]` (Full is greyed while armed, with words), then "last run 3
  min ago · 1.8 s".
- **Body:** sections (machine, outputs, media, content, mapping, devices, sync, network, video I/O). Each is a group of
  **status rows**: light, item, value, note, and **Fix**, which runs `row.fix` (e.g. `MEDIA FETCH md…`,
  `DEVICE SHUTTER dv… OPEN`).
- **Fleet:** with a fleet, one column per node.
- **Data:** rows come from `PREFLIGHT REPORT <id>`, fetched when `show.preflight.id` changes. A grey row says "did not
  finish in 5 s".

**States:**

| State | The board shows |
|---|---|
| Idle | The last report |
| Busy | Rows fill as sections finish, with a progress line |
| Armed | Quick runs; Full is greyed |
| Fault | Red rows first; **Fix all safe** (Pro) runs only the Low and Review fixes, one at a time |

### 3.3 Fleet and health board (fleet)

**Where:**
- Health ▸ Fleet;
- the Ready light;
- the attention bar (failover, fenced, link lost, leader gone);
- cards wide in ⤢.

**Data:**
- `STATE.fleet` and `STATE.eye` arrive on every push;
- the board is `FLEET`, polled at ≤ 1 Hz while the tab is visible and on `fleet.nodes[].heard|light|rev` changes.

**Machine card:**
- light; name; role chip (PRIMARY, BACKUP HOT, LEADER, FOLLOWER, FENCED); epoch; headline;
- output tiles: fps, dropped, a lock chip, an on-air dot;
- projector tiles: power, shutter, input, hours;
- sync ring: uncertainty in ms, free-wheel seconds;
- ARMED and PANIC chips;
- mirror line: "identical · lead 50 ms, margin 48 ms";
- Pro facts line, folding in the other packages' machine facts: CPU, RAM, disk; renderer;
  `D3D11 + ANGLE · RTX A4000 · DXGI`; `Zero-copy: VAAPI → EGL · direct proven`; `Blocks: BC1 BC3 BC4 BC7 BC6H · arena
  140/256 MB`; `Optimise: 2 running · 51 fps`.

**Controls:**

| Control | Sends |
|---|---|
| Lens chips: All · Outputs · Projectors · Sync · Backup · Problems | `FLEET LENS <lens>` |
| ‹ › problem | `FLEET PREV` / `FLEET NEXT` |
| Click a card (the others dim) | `FLEET FOCUS <machine>` |
| Reset | `FLEET RESET` |

These are view verbs: not journalled, and the same words Companion's EYE keys use.

**Right-click a machine card:**
- Focus;
- **Open its desk** (desk-local attach);
- **Failover to its backup** (hold) `FAILOVER <primary>`;
- Route test `FLEET ROUTE TEST ALL`;
- **Promote** (hold) `FLEET PROMOTE`;
- Succession… `FLEET SUCCESSION <node> <node>…`.

**On this node's own card:**
- **Back up ▸ <primary> ▸ Hot / Warm / Cold** (hold) `FLEET BACKUP <primary> HOT`;
- **Stop backing up** (hold) `FLEET SOLO`;
- Lead ▸ Auto / 0 / 50 / 100 ms (Pro) `FLEET LEAD AUTO|<ms>`.

**Right-click an output tile on this node:**
- **Failover route…** opens an inline form: device (from `DEVICE LIST`), primary input, backup input and need level →
  `FLEET ROUTE <op> DEVICE <dv> <in1> <in2> NEED OBSERVED`;
- Test this route `FLEET ROUTE TEST <op>`;
- Failover this output `FAILOVER <op>`.

Fleet verbs act on the node that receives them, and round 2 has no fan-out. So role changes for other machines say
"Open its desk to change FOH-B's role" (spine need §6).

**States:**

| State | The board shows |
|---|---|
| Idle, one node | One "This machine" card and the sentence "Add a backup: run PlayMap on a second machine and choose Back up ▸ this node" |
| Busy | "joining FOH-A as a hot backup · 40 %"; route test chips filling |
| Armed | ARMED chips. FAILOVER and HOLD stay live (Fleet class); BACKUP and SOLO follow the policy |
| Fault | Stale cards amber at 3 s and red "gone" at 6 s (never silently removed); `mirror.state = different` red with its words; the alarm banner |

**Controller death:**
- the engine plays on, whatever happens to the desk;
- the desk keeps the last `fleet.backups` and `fleet.nodes` it saw;
- on link loss its banner offers **Attach to <backup>**, which reuses `ConnectionManager` with the stored address.

**Next round:**
- the `FLEET AT` replay scrubber;
- 24 h metrics sparklines;
- `FAILOVER DRILL`;
- N+1 standby;
- the Eye graph drawing;
- acting on other nodes without attaching.

### 3.4 Sync and framelock (framelock)

**Where:**
- Health ▸ Sync;
- the fell-out banner;
- output tile chips.

Lead/Follow moves here from today's Network drawer.

**The tab:**
- **Group card:**
  - the verdict, large: "same vblank", or "free-running · worst 6.1 ms · next slip 4:12";
  - "air latency 33 ms (Right's projector)";
  - the last certificate's verdict.
- **Per node:** role, feed, and (Pro) the board block: "P2061 fw 3.6 · house sync —", with a warning glyph for "cable in a
  switch".
- **Per output:** a chip (LOCK / HOUSE / GROUP / FREE ✓ / FREE ? / FELL OUT) and, in Pro, a phase bar in µs with an
  uncertainty whisker. Free-run outputs show a countdown to their next slip.

**Buttons:**

| Button | Sends |
|---|---|
| **Lead this group** | `SYNC LEAD <group>` |
| **Follow ▸ <node>** | `SYNC FOLLOW <node>` |
| **Apply framelock** (hold) | `SYNC APPLY` |
| **Leave** (hold) | `SYNC LEAVE [ALL]` |
| **Rejoin** | `SYNC REJOIN [<op>\|ALL]` |
| **Marks** | `SYNC MARKS ON\|OFF` |
| **Certify** (camera picker) | `SYNC CERTIFY <camera> 10` |
| **Measure latency** | `LATENCY MEASURE <camera> ALL` |
| Latency ▸ Auto / ms | `SYNC LATENCY AUTO\|<ms>` |

**Right-click:**
- an output: Leave lock `SYNC LEAVE <op>` · Rejoin lock `SYNC REJOIN <op>` · Measure latency
  `LATENCY MEASURE <camera> <op>` · Show frame code `PATTERN synccode ON <op>`;
- a node: Follow this node `SYNC FOLLOW <node>` · Lead from here `SYNC LEAD <group>`.

**States:**

| State | The tab shows |
|---|---|
| Idle, solo | "This node is alone: lead a group, or follow a leader" |
| Busy | "applying framelock to 3 nodes, 7 outputs" until applied; a 10 s certify bar |
| Armed | Apply, Leave, Marks and Certify greyed with words; Rejoin greyed unless blacked out |
| Fault | Fell out (red banner); mixed rate (amber); cable into a switch (amber with the fix words) |

**Next round:**
- certificate history;
- certify from a dropped phone clip (`SYNC CERTIFY FILE`);
- the pairs table.

### 3.5 Present path (present)

**Where:**
- Outputs ▸ Plan tile chips (badges);
- an output inspector section, **Display** (a section VM; it does not edit `MapInspectors.cs`);
- the machine card line;
- the fuse banner.

**Output chips:**

| Chip | When |
|---|---|
| `FLIP ✓ 59.999 Hz · 0` | green |
| `FLIP? · 2 missed` | amber |
| `COMPOSED` | red when proven |
| `GL` | grey on Linux; amber on Windows, with the fallback reason |

**Display section:**
- **Bits** 8 / 10 `SET <op> colorBits 10`, with `bitsReported` beside it;
- **Latency (ms)** `SET <op> latencyMs <n>`;
- **Measure** `LATENCY MEASURE <camera> <op>`;
- **Prove flip** `PRESENT PROVE <op> 20`, with a 20 s bar and the words.

**Right-click a tile:** Prove flip mode · Reset counters `PRESENT RESET <op>` · Set 10-bit / 8-bit.

**Armed:** PRESENT PATH is refused; PROVE follows the policy.

### 3.6 Timecode chase (timecode; the desk is S7 there, this is its lean round-2 cut)

**Where:**
- the TC chip (§2.2);
- the playlist cards: a chase chip, and the menus;
- the item rows: a TC-in label;
- Inputs ▸ Timecode.

**Playlist right-click:**
- Chase timecode ▸ <source> ▸ Locked / Triggered `TIMECODE CHASE <pl> <src>[,<backup>] LOCKED|TRIGGERED`;
- Stop chasing `TIMECODE CHASE <pl> OFF`;
- Engage / Disengage `TIMECODE ENGAGE <pl> ON|OFF`;
- Delay ▸ +1 frame / −1 frame / +10 ms / −10 ms `TIMECODE SET <pl> DELAY <ms>` (the desk computes the absolute value
  from `chases[].delayMs`);
- On loss ▸ Hold / Stop / Continue `TIMECODE SET <pl> ONLOSS …`;
- Freewheel ▸ 0.5 / 1 / 2 / 5 s `TIMECODE SET <pl> FREEWHEEL <ms>`.

**Item right-click:**
- Set TC in… (an inline field, checked by Core `Timecode.TryParse`; the refusal words are shown as typed)
  `TIMECODE IN <pl> <it> <tc>`;
- Capture TC in now `TIMECODE IN <pl> <it> NOW`;
- Clear TC in `TIMECODE IN <pl> <it> OFF`.

**Transport on a chased playlist:**
- **When engaged and locked:** its transport buttons grey, with the engine's words: "Main chases FOH LTC — Disengage
  to play by hand".
- **The card shows** the chase chip "LTC · locked 01:02:03:04" and "picture 40 ms later".

**Inputs ▸ Timecode:**
- **Sources:** name, kind, state light, TC text, detected fps and level bar; in Pro also speed (ppm), jitter, error
  counters and user bits.
- **Add ▸**
  - LTC ▸ <device:channel from `TIMECODE DEVICES`>;
  - MTC ▸ <port>;
  - Art-Net ▸ stream;
  - Internal.

  Each sends `TIMECODE SOURCE ADD <name> LTC <device>:<ch>` (or its kind).
- **Remove** `TIMECODE SOURCE REMOVE <src>`.
- **Internal generator row:** Play / Stop / Locate [TC field] / Rate → `TIMECODE INTERNAL <src> PLAY|STOP|LOCATE <tc>|RATE <r>`.

**States:**

| State | The operator sees |
|---|---|
| Idle | The chip is hidden until a source exists |
| Busy | "acquiring", amber |
| Armed | CHASE, ENGAGE and INTERNAL are High and follow the policy |
| Fault | The chase-held banner; source red "lost" with its words |

**Next round:** the TC ruler strip, TC cues, TC outputs, `TIMECODE CALIBRATE`, and the test card.

### 3.7 Bindings and learn (control)

**Where:**
- **Bind ▸** in every right-click menu (a menu-extras hook, so no existing VM is edited);
- bound badges;
- the learn banner and status line;
- Inputs ▸ Controllers.

**Learn:**
- **Bind ▸** lists the menu's own actions that have a verb, by label (Go Intro, Blackout, Shutter open). Choosing one sends
  `BIND LEARN <that line>`.
- The banner and status line read "Move a control for Blackout — Esc cancels (24 s)", counting down from
  `control.learning.until`. The menu entry reads LEARNING….
- The first qualifying input binds. `STATE.control.last` and the journal give the words for a toast: "CC 1 48 on APC40 →
  Blackout (was Stage opacity)". If the Controllers tab is open, the new row flashes there.
- Esc (rung 1) or **Cancel** sends `BIND LEARN OFF`.
- A number row's socket (D2) has **Learn a controller**, which sends `BIND LEARN SET <id> <path> *`.

**Bound to ▸** (in the same menu) lists the project's bindings whose target names this entity: "CC 1 48 on APC40 · Forget"
→ `BIND FORGET <input words>`.

**Copy OSC address** (Pro) comes from Core's OSC address map.

**Badges:** "CC48", "/fader/1" or "U7.101" on bound buttons and number rows. A badge is grey when its surface is
disconnected (`control.surfaces[].connected`).

**Inputs ▸ Controllers:**
- **Master switch:** "Control input" `CONTROL ENABLE ON|OFF`.
- **Surfaces:** light, name, kind and profile, with a verified badge.
  - Port on this machine ▸ <from `CONTROL PORTS`> `CONTROL ALIAS <surface> <port>`.
  - **Add surface** `CONTROL SURFACE ADD <name> MIDI|OSC|ARTNET|SACN <match>`.
- **Bindings:** filter; "input → target" words; an enable switch `BIND ENABLE <bd> ON|OFF`; Forget `BIND FORGET …`.
  Expanding a row shows, in Pro, the **shape editor**:
  - takeover None / Pick-up / Scale `SET <bd> shape.takeover "pickUp"`;
  - range in and out;
  - invert;
  - curve preset;
  - button behaviour;
  - hold-to-fire;
  - **Test** `CONTROL INJECT <input words> 0`, then `… 1`.
- **Monitor** (Pro): `CONTROL MONITOR 32`, polled at 10 Hz while visible.
- **Network** (Pro): the OSC port; the OSCQuery URL (Open in browser); Art-Net and sACN switches
  `CONTROL DMX ARTNET|SACN ON|OFF [<nic>]`; feedback targets `CONTROL FEEDBACK ADD|REMOVE <host:port>`.

**States:**

| State | The operator sees |
|---|---|
| Idle | Surfaces with green lights |
| Busy | Learning (banner) |
| Armed | Bindings keep firing. `BIND …` and `CONTROL SURFACE\|ALIAS\|DMX` are greyed with the gate's words |
| Fault | "bound to 12 controls, not connected" (amber); a refused port (red, with its words) |

**Next round:**
- Bind mode (Ctrl+M: every bindable control tinted; a click learns);
- the shape editor in a last-action panel;
- Pick-up arrows (a `BIND LIST` state stream);
- Identify surface;
- Import from Patterns;
- personalities.

### 3.8 Companion decks (companion S5, lean cut)

**Network ▸ Decks:** one row per entry of `STATE.clients`:
- name and address;
- channel (ws / wss / tcp);
- module version (amber "update the module" when it is older than the node's known module);
- Live source and page;
- a recording dot;
- the thumbnail count.

Right-click: **Copy connection details** (host, port, fingerprint; desk-local).

The Network pill's tooltip adds "3 decks connected".

**Next round:**
- Identify deck (needs a verb for `deck.identify`);
- Close connection (needs a verb);
- Add PlayMap to Companion `COMPANION ADD`;
- Dim decks with the show strip;
- key pages `KEYS …` with Put on keys ▸;
- `SELECT` publishing.

---

## 4. Content and media (D2)

### 4.1 Effects browser and slots (effects)

**Where:**
- Library ▸ **Effects**;
- an **Effects** section in the inspector of every target (surface, output, canvas, layer, playlist item);
- right-click extras on targets and slots;
- **A** to audition;
- the **Shader** editor (Pro, the Library opened wide).

**Browser:**
- search, with family chips (Colour, Blur, Distort, Stylise, Geometry-aware, Transition, Project ISF, FFGL);
- tiles: name, family icon, cost badge "0.3 ms @1080p", trait badges (*history*, *live*, *native*, *may flash*);
- data: `FX KINDS`, fetched on attach and when `fx.library` counts change.

**Drag and drop:**
- **Drag** a tile onto an outliner row, a preview card or a playlist item. These are spine `DeskDrop` targets,
  format `playmap/fx-kind`. The drop sends `FX ADD <target> <kind>`.
- **Alt-drag** adds it at mix 0: `FX ADD …`, then `FX SET <slot> mix 0`.

**Plugins group** (Pro):
- FFGL plugins with their trust state; quarantined ones in red with their words;
- **Rescan** `FX PLUGINS RESCAN`;
- right-click **Trust ▸ In-process / Isolated / Off** (hold; High; this machine only) `FX TRUST <plugin> IN-PROCESS|ISOLATED|OFF`;
- **Import ISF…** `FX IMPORT <path>`.

**Slot rows** in the Effects section:
- a state chip from `fx.slots[]` (compiling / ok / faulted with the first error / isolated-restarting);
- bypass `FX BYPASS <slot> TOGGLE`;
- **Amount** (mix) `FX SET <slot> mix <v>` (throttled);
- parameters, using the generators' `ParamEditor`s fed from `FX KINDS` param specs: `FX SET <slot> <param> <v>`. The
  wheel sends `FX SET <slot> <param> BY <delta>`;
- Pro: blend `FX SET <slot> blend add` · scale `FX SET <slot> scale 0.5` · cost · presets ▸ `FX RECALL <slot> <preset>`
  · Save preset… `FX SAVE <slot> <name>`;
- a modulation socket on each number (§4.2), sending `FX BIND <slot> <param> <source> …`.

**Slot right-click:**
- Bypass;
- Reset `FX RESET <slot>`;
- Move up / down `FX MOVE <slot> <i±1>`;
- Save preset…;
- Apply preset ▸;
- Bind ▸ (D1 adds it);
- Edit shader (project ISF only);
- Remove `FX REMOVE <slot>`.

**Target right-click:**
- Effects ▸ Add… (a search flyout) `FX ADD <target> <kind>`;
- Bypass all `FX BYPASS <target> TOGGLE`;
- Copy effects (desk-local);
- Paste effects `FX PASTE <target> FROM <copied target>`;
- Retime `FX RETIME <target>`.

**Audition:** hold **A** over a tile, with an output preview of the selected target visible.
- Press: `FX AUDITION <previewView> <target> <kind>`.
- Release: `FX AUDITION <previewView> <target> OFF`.
- The preview shows the effect; the air does not.

**Shader editor** (Pro, minimal):
- a monospace text box with the ISF JSON header;
- 500 ms after typing stops, it sends `FX CHECK {json}` and lists the issues per dialect (line, column, words);
- **Save** sends `FX EDIT <asset> {json}` for an existing asset, or `FX IMPORT {json}` for a new one, and is one undo
  step.

**States:**

| State | The operator sees |
|---|---|
| Idle | Tiles |
| Busy | Compiling chips; probing spinner |
| Armed | Edits follow the policy (blind for mapping-class edits). `FX BYPASS ALL ON` stays live as the "effects off" panic outcome |
| Fault | A faulted slot in red with its first error; an output over budget shows an amber chip on its tile (`fx.outputs[].light`) |

**Next round:**
- live effect thumbnails over the target;
- drag-to-reorder slots;
- linked looks;
- duplicating a built-in to edit it;
- live preview and highlighting in the editor;
- "Make an effect…" via the assistant.

### 4.2 The modulation socket and audio (audio, with effects M7 and content M10)

**One socket** (`~`) sits on every bindable number row: generator params, FX params, surface and layer numbers. It is a
spine slot, filled by D2.

**The popover:**
- **Source chips** from `MOD SOURCES`: Bass, Mid, High, Level, Kick, Snare, Hat, Onset, Beat pulse, Bar ramp, LFO ▸
  ¼ · 1 · 4 · 16 beats, Random per beat, Track ▸, named modulators, MIDI/OSC/DMX. Each has a determinism badge
  ("shared from Stage-left" / "live — breaks mirror backup"). If the node lacks `MOD SOURCES`, the effects package's
  built-ins (`lfo:*`, `ramp:*`, `sting`) are offered.
- **Amount** (bipolar), **Mode** (Add / Scale / Replace / Rate) and **Smooth** (attack/release); in Pro, Curve, Range,
  Steps and Invert.
- **Apply:**
  - `FX BIND <slot> <param> <source> AMOUNT <a> …` on an FX slot;
  - `MOD BIND <entity> <param> <source> AMOUNT <a> MODE ADD SMOOTH 30/250` elsewhere.
- **Remove:** `FX UNBIND …` / `MOD UNBIND <entity> <param>`.
- **Learn a controller:** `BIND LEARN SET <id> <path> *`.

**Bound rows:**
- a ring in the source family's colour;
- a thin live needle over the base value, read from `fx.slots[].live`, `content.generators[].live`, or `MOD PEEK
  <entity>` (polled at 10 Hz only while a bound row is visible).

**Drag a source onto a number:** meter bars, beat dots and source chips are drag sources (`playmap/mod-source`). Dropping
one on a socket sends `MOD BIND … AMOUNT 0.5 SMOOTH 30/250`. This is the five-second gesture (audio M12).

**Right-click a number row** (the socket's own menu):
- Modulate ▸ Bass · Beat pulse · LFO ▸ · Random per beat · From track ▸;
- Remove;
- Copy / Paste modulation.

**Other right-click entries:**
- on a generator, layer or surface: **Make it react ▸** Gentle · Punchy · On the beat · Off `MOD AUTO <entity>
  GENTLE|PUNCHY|BEAT|OFF`;
- on media: **Analyse sound** `AUDIO ANALYSE <md>`;
- on a playlist: **Listen to this playlist** `AUDIO LISTEN TRACK <pl>` · **Use for tempo** `TEMPO SOURCE TRACK <pl>`.

**Inputs ▸ Audio:**
- **Input picker** from `AUDIO INPUTS`: Default, System sound, Everything but PlayMap, devices, Track ▸, Test ▸
  click/sweep/noise/drums, Off.
  - It sends `AUDIO LISTEN <input> [CHANNELS 1+2]`, adding `HERE` when "this machine only" is ticked.
- **Gain** `AUDIO GAIN <dB>`, with **Auto** `AUDIO GAIN AUTO`.
- **Live bands:** Level, Bass, Mid, High.
- **Pro:**
  - a 64-bin spectrum (`AUDIO STATUS` at 10 Hz while visible);
  - the tempo panel: lock, confidence, candidates as buttons `TEMPO SET <bpm>`, source
    `TEMPO SOURCE DETECT|MANUAL|LINK|MIDI <port>|NODE <node>`, meter `TEMPO METER 4 8`;
  - role `AUDIO ROLE LISTEN|FOLLOW <node>|SOLO|AUTO`, with each follower's late counter;
  - distance `AUDIO DISTANCE <m>`.

**States:**

| State | The operator sees |
|---|---|
| Idle | Tempo chip; level bar |
| Busy | "analysing 40 %" on media rows (`audio.analysing`) |
| Armed | TAP, RESYNC, nudge, ½ and ×2 live; binding edits follow the policy |
| Fault | "Line In is not on this machine — retrying every 5 s" amber; `modulation.unbound` listed in the Audio tab ("audio:bass (no audio on this node)") |

**Next round:**
- onset lanes;
- the latency breakdown bar;
- a 2 s scope in the popover;
- the named-modulator editor (`MOD NEW|SET`);
- launch quantisation on GO (`AT BAR`);
- BPM chips and beat ticks on items.

### 4.3 Particles, fractals, reactive (content)

**Library ▸ Generators** is regrouped: families → kinds → presets, from `GENERATOR KINDS` and `GENERATOR PRESETS`.

- **Tiles:** family icon, preset name, and trait badges: time class (`pure` grey, `bounded 4 s` blue, `live` amber
  "breaks mirror"), *reads audio*, *may flash*.
- **Drag a preset:**
  - onto a surface row or a preview: `GENERATOR NEW <kind> PRESET <preset>`, then `SURFACE SOURCE <sf>
    generator:<gn>` (or `PLAYLIST ADD <pl> generator:<gn>` on a playlist);
  - two lines, so two undo steps this round (spine need §6).

**Generator inspector** (D2 owns `GeneratorInspector.cs`):
- **Groups** from `ParamSpec.Group` (Emit, Forces, Collide, Look, Camera, Quality), collapsible. Simple shows the first
  group and Look.
- **Numbers:** the wheel sends `GENERATOR SET <gn> <param> BY <delta>`.
- **Socket** on each number (§4.2).
- **Badges:** the time class; `warming 40 %` / `computing 37 %` from `content.generators[]`; GPU ms (Pro).
- **Preset bar:**
  - preset picker → **Recall** `GENERATOR RECALL <gn> <preset>`;
  - **Save…** `GENERATOR SAVE <gn> <name>` (Pro);
  - **Morph to…** (picker plus length slider) `GENERATOR MORPH <gn> <preset> OVER <s>`, with a progress line from
    `morph.progress` (Pro);
  - **Reset** `GENERATOR RESET <gn> ALL`;
  - **Retime** `GENERATOR RETIME <gn>`.

**Sting pad** (Pro, under the Generators tab):
- 12 buttons from `STING LIST`;
- a length knob, 100–5000 ms → `STING <name> <ms>`;
- **Clear** `STING CLEAR`;
- buttons light while `content.sting.progress < 1`.

**Surface right-click:**
- Particles from this surface ▸ Snow settling / Sparks from edges / Lava down the face → `GENERATOR NEW particles.stage
  PRESET "<p>"` + `SURFACE SOURCE <sf> generator:<gn>` (stage particles emit from the surfaces that show them);
- **Draw itself** (`effect.grow`);
- **Sun across it** (`effect.shadowlight`);
- **Crease glow** (`effect.crease`);
- **Fractal inside ▸** window / solid.

The last four send the same pair of lines.

**States:**

| State | The operator sees |
|---|---|
| Busy | Warming or computing rings; a bounded generator prewarms behind the previous picture |
| Armed | Recall and Morph follow the policy; STING and STING CLEAR are air verbs and follow it too |
| Fault | Faulted generator red; `content.light` amber at budget, red at 2× |

**Next round:**
- live preset thumbnails (needs a preset preview view);
- sparklines;
- Quality ▸;
- Prepare;
- copy/paste look;
- one undo step for drag-to-surface;
- "Morph at next bar".

### 4.4 Optimise on import (optimise), HAP and zero-copy facts (hap, zerocopy)

**Where:**
- media row badges;
- an ask-mode banner;
- a **jobs strip** at the foot of Library ▸ Files;
- right-click menus;
- the media inspector section **On this node**.

**Badges** on each media row:

| Badge | Meaning |
|---|---|
| `HAP Q ✓` | Plays here, on the GPU |
| `optimising 42 % · 3 min` | A job is running |
| `suggested` | The rules suggest a version |
| `stale` | A version no longer matches its source |
| `review` | Amber: a finished version waits for a person |
| `failed` | Red, with the reason on hover |

Decode chips sit beside them: `GPU · zero-copy`, `GPU · 1 copy`, `GPU · copy-back` (amber at ≥ 4K), `CPU` (amber at ≥
4K), `HAP R · cannot play here` (red).

**Jobs strip:**
- the line "Optimising 2 · 3 min left · a show plays: one stream, GPU ≤ 15 %" (the governor's words);
- **On import: Ask | Auto | Never** `MEDIA RULES MODE ASK|AUTO|NEVER`;
- **Pause all** `MEDIA OPTIMISE PAUSE ALL` / **Resume all** `MEDIA OPTIMISE RESUME ALL`;
- expanding it (Pro) shows the job rows: name, target, bar, fps, ETA and state, with Pause / Resume / Cancel per row
  `MEDIA OPTIMISE PAUSE|RESUME|CANCEL <oj>`. A finished `review` job has **Use it** `MEDIA USE <md> <mv>`.

**Media right-click:**
- **Optimise ▸**
  - As the rules say `MEDIA OPTIMISE <md>`;
  - HAP Q / HAP Q Alpha / HAP R / HAP HDR / Re-chunk (lossless) `MEDIA OPTIMISE <md> TO hap-q|…|rechunk`;
  - Cancel.

  The estimates are fetched with `MEDIA ESTIMATE <md>` when the menu opens. The entries show without an ETA until the
  reply lands (the menu is rebuilt on open).
- **Use ▸** Auto / Source / <version> `MEDIA USE <md> AUTO|SOURCE|<mv>`.
- **Inspect on this node** `MEDIA INSPECT <md>`.
- **Probe decode** `DECODE PROBE <md>`.

**On this node** (media inspector section):
- the chip `HAP Q · GPU`;
- the facts line from `MEDIA INSPECT`: "16 chunks · Snappy 96 % · 0.7 MB per frame · 43 MB/s · decode 2.1 ms · arena";
- the decode path from `decode.sources[]`, with `why` as its second line;
- warnings as status rows with **Fix**, e.g. "1 chunk: decodes on one core" → `MEDIA OPTIMISE md… TO rechunk`.

**States:**

| State | The operator sees |
|---|---|
| Armed | The strip reads "optimise waits: the show is armed"; Pause all stays live |
| Fault | Failed rows in red with their words |

**Next round:**
- the Versions… view with A/B compare (two VIEWs and a wipe);
- the rules table editor;
- HAP HDR colour editing;
- decode policy UI;
- `THUMB` posters on tiles.

### 4.5 Live video in and out (texshare, netvideo)

**Library ▸ Live:**
- **Discovered** sources (`videoio.discovered`), grouped by protocol (NDI®, OMT, Spout, Syphon) and by host. A peer
  shows its name ("Patterns · Program").
- **Added inputs** (`videoio.inputs`), each with:
  - a thumbnail (`VIEW live-<id> SHOW input:<id> 160x90`, ≤ 1 fps);
  - a protocol badge;
  - a state chip (green ok, amber stale, red lost, grey connecting);
  - `1080p59.94`;
  - a tally dot;
  - `↺ backup` when the backup is in use.
- **Runtime line:** "NDI® runtime 6.3.2 found", or "NDI® runtime not installed — ndi.link/NDIRedistV6 · ndi.video". The
  footer reads "NDI® is a registered trademark of Vizrt NDI AB" (netvideo M12: a desk test finds the link).

**Gestures:**
- **Double-click** a discovered source: `INPUT ADD <uri>`.
- **Drag** one onto a surface: `INPUT ADD <uri>`, then `SURFACE SOURCE <sf> live:<md>`.
- **Add by address…** `INPUT ADD omt://…|ndi://ip:port|spout:<name>`.

**Input right-click:**
- Show on surface ▸ `SURFACE SOURCE <sf> live:<md>`;
- Show on output ▸ `OUTPUT SOURCE <op> live:<md>`;
- On loss ▸ Hold / Black / Fallback… `INPUT FALLBACK <md> HOLD|BLACK|<source>`;
- Backup source… `INPUT BACKUP <md> <uri>`;
- Switch to backup / primary (hold) `INPUT SWITCH <md> BACKUP|PRIMARY`;
- Reconnect `INPUT RECONNECT <md>` (enabled only when not ok);
- Pacing ▸ Latest / Smooth / Locked `SET <md> live.pacing "smooth"` (Pro);
- Copy URI;
- Remove `MEDIA REMOVE <md>`.

**Outputs ▸ Sends:**
- one row per send: name, full name, connections, Mb/s, a tally lamp, dropped frames, and a state chip (`sending`,
  `idle`, `off`, `failed`);
- **Enable** `SEND ENABLE <sd> ON`; **Disable** (hold when watched; the button says "2 receivers will lose this
  picture") `SEND ENABLE <sd> OFF`.

**Output and canvas right-click:**
- Send this output ▸ NDI… / OMT… / Spout… `SEND ADD NDI|OMT|SPOUT <name> output:<op>` (in the word order the spine
  settles, §6.17);
- Send before warp `… prewarp:<op>`.

**Output tile chip:** "→ NDI · 2 watching".

**Armed:** `SEND ENABLE` is refused; `INPUT SWITCH` and `INPUT RECONNECT` follow the policy.

**Next round:**
- multiview `VIEW … multiview:auto`;
- PTZ presets;
- hover proxies in the browser;
- Network ▸ Video I/O (runtimes, mDNS, discovery servers, NIC bar, `VIDEOIO CHECK`);
- Spout format and rate controls.

---

## 5. Map and calibration (D3)

### 5.1 On-wall editing and the quad path (firstfive)

**First-run guide "Light a wall".** It is a **Guide** (§5.4) shown over PLAY when the project is the default and has no
projector. It has three steps, each read from `STATE.onboarding.stage`:

1. **Light a wall.**
   - Displays are listed by EDID name, from `DISPLAYS`. One press sends `LIGHT WALL "<name>"`.
   - The desk then switches to MAP and the projector view, with Q armed.
2. **Click the four corners of your wall — on the wall.**
   - Each click is drawn on the desk at once (UX P1), and `CURSOR <op> x y TRAIL …` makes the wall show the same rubber
     band.
   - The fourth click sends `SURFACE QUAD <pj> x1 y1 … x4 y4`.
3. **Drop a clip on it.**
   - Dropping a file from the OS onto the projector view, or **Choose a clip…**, sends `MEDIA IMPORT <path>`, then
     `SURFACE SOURCE <sf> media:<md>`.
   - The guide ends with "First map in 1:24" (`onboarding.seconds`).
- **Built:** the guide is a card in MAP (its step 1 also shows on a show with no projector); a light switches to MAP with Q armed
  on the lit projector; Enter, not the fourth click, sends the quad; step 3 offers the show's media and playlists
  (`SURFACE SOURCE <sf> media:<md>|playlist:<pl>`) rather than an import; "First map in 1:24" is a toast as the guide closes; it
  shows only on the first-light path (no projector yet, or a wall lit in this run); the cross closes it on this desk.

**On-wall editing mode (O, or the "On the wall" toggle in the MAP toolbar):**

| State | The operator sees | Sends |
|---|---|---|
| Off (default) | The projector view on the desk only | — |
| **On** | Amber frame; an "ON THE WALL" chip on the projector view; the wall shows outlines, handles, names and the desk's cursor | `OVERLAY ON ON <op>`. The cursor (C) sends `CURSOR <op> x y` through the throttle (60 Hz, leading edge); `CURSOR OFF` on leaving. `OVERLAY KEEP` every 5 s (the lease, spine need §6) |
| Quad tool | Clicks place corners. Esc cancels; Enter finishes once there are four | `SURFACE QUAD …` |
| Handle selected | The handle is large on the desk and on the wall | `HANDLE SELECT <sf> TL` (Tab cycles). Arrows send `SURFACE CORNER <sf> TL BY 1 0 PX` (Shift ×10, Alt ×0.1), each drawn optimistically. A refusal rolls the handle back over 150 ms with the reason chip |
| Grid | A fine grid flashes for 2 s | `OVERLAY GRID <op> 2` (G) |
| Armed | O is greyed: "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)". Blind edits draw their overlay on the desk only | — |
| Fault | Reason chips: "Output 1 is not in Projector mode — LIGHT WALL or SET op_1 mode projector"; "that point is outside Output 1's warped picture". The cursor p95 above two frames shows "wall cursor 48 ms behind" (`air.overlay.cursorP95Ms`) | — |

Leaving MAP or closing the desk sends `OVERLAY OFF ON <op>`. With the lease, a crashed desk's overlay expires on its own.

**Quad right-click** (the same `DeskAction`s as its inspector):
- Source ▸ `SURFACE SOURCE <sf> …`;
- Promote to lattice ▸ 3×3 / 5×5 / 9×9 `SURFACE LATTICE <sf> 5x5`;
- Lattice off `SURFACE LATTICE <sf> OFF`;
- Add mask `ADD mask {…}`;
- Flash grid `OVERLAY GRID <op> 2`;
- Lock `LOCK <sf>`;
- Duplicate;
- Move to projector ▸ `SET <sf> pixel.projectorId "<pj>"`;
- Remove `REMOVE <sf>`.

**Simple and Pro:** Simple shows Q, the four clicks, the arrows and Drop a clip. Pro adds the lattice sizes and Move to
projector.

**Next round:**
- snapping guides (S3);
- `PICK PIXEL` click-through;
- `HIGHLIGHT` from the outliner;
- pixel-quad cards in the 3D view;
- the Simple/Pro onboarding variants;
- "Make this a show machine?".

### 5.2 Calibration score, fallback and history (calibops)

**Projector object card** (a projector inspector section, plus the outliner badge "A 0.6"):
- a **score ring** with the grade letter and score;
- the worst term's words in one line;
- a confidence bar;
- "verified 0.31 px · 21:04".

**Clicking the ring opens the score panel:**
- every term from `calibration.projectors[].terms` as a status row: code icon, a px bar, the words, and where;
- **Fix** runs `term.fix` (e.g. `SET pn… enabled false`, `PROJECTOR SOLVE pj… LOCK FOCAL`);
- **Show on wall** sends `OVERLAY HEAT <pj>`, with numbered areas that match the rows.

**History strip** (Pro; Simple shows "7 snapshots · fallback 2 days ago"):
- the score over time, from `CALIBRATION HISTORY <pj>`, fetched when the section opens and when `history` changes;
- dots by source icon: current filled, fallback ringed, staged hollow;
- hover shows Δwall px, who and when;
- right-click a dot:
  - **Recall** `CALIBRATION RECALL cs…`;
  - **Set as fallback** `CALIBRATION ACCEPT cs…`;
  - **Pin** `SET cs… pinned true`;
  - **Rename** `SET cs… name "<name>"`;
  - **Forget** `CALIBRATION FORGET cs…`.

**Projector right-click:**
- Check `CALIBRATION CHECK <pj>`;
- Re-solve `CALIBRATION RESOLVE <pj>`;
- Re-solve (lens changed) `CALIBRATION RESOLVE <pj> LENS`;
- Accept as fallback `CALIBRATION ACCEPT <pj>`;
- Revert to fallback `CALIBRATION FALLBACK <pj>`;
- Save snapshot `CALIBRATION SAVE <pj>`;
- Show confidence on wall `OVERLAY HEAT <pj>`.

**Health ▸ Calibration:**
- one row per projector: grade, worst words, last check and moved state;
- **Check all** `CALIBRATION CHECK ALL`;
- **Re-solve moved** (hold) `CALIBRATION RESOLVE MOVED`.

**States:**

| State | The operator sees |
|---|---|
| Idle | Green A/B rings |
| Busy | The task dial "capturing → matching → solving → verifying" from `calibration.task`; Esc cancels (rung 0) `CALIBRATION CANCEL` |
| Armed | Check and Re-solve greyed ("heavy work"); Recall, Fallback and Accept greyed unless blind |
| Fault | A moved projector: red banner and ring; staged: amber banner Accept / Discard; camera moved: info |

After a re-solve, the desk says "Projector 2 re-solved. Masks and lattices unchanged: they live on the surfaces" (UX.md
§3.3), using the reply's `reseated` counts.

**Next round:**
- Compare (the S3 `CALIBRATION PREVIEW` split with a draggable wall line);
- `CALIBRATION REPORT`;
- drift-watch chips.

### 5.3 Auto-align wizard (autoalign)

**Where:**
- Outputs ▸ **Align**;
- output, device and canvas menus;
- the staged banner.

| Step (from STATE.align) | The operator sees | Sends |
|---|---|---|
| **Start** (no group) | "Align screens" with one sentence: "Uses Camera 1 and the 4 open outputs". Pickers for camera and outputs, and (Pro) Shape Auto / Flat / Cylinder / Dome / Free-form and Colour Off / White / Full | **Align screens** sends `ALIGN NEW <name> <camera> <op>… [SHAPE …]`, then `ALIGN PLAN <group>` |
| **Plan** | Chips: camera (resolution, colour); each projector ("Panasonic · resets 9 settings", or "switch keystone off on its menu"); groups; ETA. Warnings are status rows with **Fix** | **Run — patterns on the wall for about 90 s** sends `ALIGN RUN <group> [COLOR WHITE\|FULL]` |
| **Running** | The step strip Neutral · Identify · Scan · Solve · Colour · Verify, with progress and ETA dial (`align.run`). The camera's live view `VIEW align-cam SHOW camera:<id> 960x540`. Per-output progress chips | **Cancel** (Esc, rung 0) `ALIGN CANCEL` |
| **Staged** | Result card: score ring and grade; the items (coverage, sampling, warp, seams, stacks, canvas, ripple, uniformity, colour, level, warm-up, additivity, contrast, native) as status rows with **Fix** | **Accept** `ALIGN ACCEPT <run>` · **Revert to previous** `ALIGN REVERT <group>` · **Run again** |
| **Accepted** | Group summary: score, grade, fallback run, history count | **Re-align this projector** `ALIGN RUN <group> ONLY <op>` · **Revert** · **Clear** `ALIGN CLEAR <group>` |
| **Armed** | Run, Accept and Revert greyed with words; the summary stays | — |
| **Fault** | Failed words and fix lines. Refusals such as "the camera changes its own exposure — lock it" become a red row | — |

**Pro, per-device rows:**
- the native settings from `DEVICE NATIVE <dv>`, read when the tab opens;
- **Snapshot** `DEVICE SNAPSHOT <dv>`;
- **Neutral** (hold) `DEVICE NEUTRAL <dv>`;
- **Restore** (hold) `DEVICE RESTORE <dv>`.

**Set screen corners** (Pro): four clicks on the camera view send `ALIGN CORNERS <group> x1 y1 … x4 y4`; `CLEAR` removes
them.

**Right-click:**
- an output: Re-align this projector · Clear measured correction `ALIGN CLEAR <op>`;
- a device: Projector's own settings ▸ Read / Snapshot / Neutral / Restore.

**Output tile badge:** "Aligned 91" from `outputs[].measured`.

**Next round:**
- the before/after split slider on the camera picture;
- footprint labels and residual arrows drawn on the camera view;
- "Link these projectors" from discovery;
- Audit;
- a Keep-current verb (spine need §6).

### 5.4 Facade wizard: a Guide (facade)

**A Guide is the desk's one wizard pattern.**
- It is a non-modal column that replaces the MAP outliner while open (Guide ▸ First light / Facade).
- Steps can be done in any order, and steps already done are shown done. It survives a desk or node restart.
- Each step has:
  - a title;
  - a **done when** read from STATE or the project;
  - a state chip (not started, in progress, done, needs attention);
  - its words;
  - one primary button and (Pro) secondary ones.
- The same component runs "Light a wall" (§5.1).

| Step | Done when | Primary (and Pro) |
|---|---|---|
| 1 Site and survey | `stage.site` set and a survey `ready` | **Import survey…** `SURVEY IMPORT <path> [CRS <code>] [UNITS m]`; progress "importing 42 %". Pro: CRS chip, origin `SITE ORIGIN <e> <n> <h>`, mains `SITE MAINS 50\|60` |
| 2 Control points | `CONTROL CHECK` light green or amber | **Import points…** `CONTROL IMPORT <path>` → **Check** `CONTROL CHECK`. A table of points with distance and light; clicking a row flies the 3D view there |
| 3 Lenses | Every projector has a prior | Per projector: **Lens from the projector** (the device readback's throw), or **Set throw…** `PROJECTOR PRIOR <pj> THROW <r> ±2 %`; **At control point ▸** `PROJECTOR PRIOR <pj> AT <cp>` |
| 4 Pins | Every projector solved with wall px ≤ 1.5 | **Pin control points on <pj>** (the pin tool in projector view, the control-point list beside it) → **Solve** `PROJECTOR SOLVE <pj>`. The solve line leads with the wall: "On the wall ±1.4 px · distance ±3.7 m (zoom↔distance) · Lock zoom" `PROJECTOR SOLVE <pj> LOCK FOCAL` |
| 5 Night scan (optional) | A night scan applied, or skipped | **Scan at night** opens SCAN with the Night chip `SCAN START <cam> … PROFILE NIGHT`. Review offers **Mask glass** `MASK GLASS <scan> ALL`, with RMS before and after |
| 6 Light | `PLAN LIGHT` answered | **Plan light** `PLAN LIGHT ALL [AMBIENT <lux>] [TARGET <lux>]`. Per-surface lux p5/p50, contrast and pitch go into the surface section; the deficit sentence is shown |

**Projector section** (Pro): the lens line ("ET-D75LE40 · TR 3.02 ±2 % · from lens readback") and the uncertainty line.

**Surface right-click:** Mask glass · Plan light here `PLAN LIGHT <sf>`.

**Mesh right-click:** Check against control points `CONTROL CHECK <survey>`.

**Control point right-click:** Pin this on <projector>.

**States:**

| State | The operator sees |
|---|---|
| Busy | "importing 42 %" (`stage.surveys[].state`) |
| Armed | Every step greyed: these are mapping and heavy work |
| Fault | Survey failed in red with its words; CONTROL CHECK red "20 cm" |

**Next round:**
- stacks (S1/S2: stack chips, Align stack, Match colour, the degraded banner);
- the light heat-map legend (S4);
- the uncertainty ellipsoid in 3D;
- `SCAN AMBIENT` (S5).

### 5.5 Phone scan, fully local (phonescan)

These are changes to today's five-step SCAN wizard (D3 owns `ScanWizardViewModel.cs` and `ScanView.axaml`).

**Step 1, Camera: three tiles.**
- **This machine's camera** (today's path).
- **A phone:**
  - sends `PHONE INVITE [<name>] FOR 10`;
  - shows the QR code (drawn from the reply's `qr.modules`, so no QR library is needed), the URL, the expiry countdown,
    and the fingerprint words;
  - when the phone joins (`phones[]`), its card appears, already selected, with capability chips ("Exposure locked ✓" or
    "Brightness kept steady (iPhone can't lock exposure from a web page)", Focus, White balance, "Clock ±4 ms", "Link
    6.1 MB/s") and a live preview `VIEW scan-camera SHOW camera:<cm> 960x540`;
  - **Run check** `PHONE CHECK <ph>`.
  - The invite reply is shown only on the desk that asked. It is never copied to a toast, the log or a Companion
    variable.
- **A folder or tethered camera:** a path picker and Advance ▸ New file / Manual. These are the existing `ADD camera {…}`
  lines with kind Folder.

**Step 3, Scan:**
- progress per view and projector;
- the phone's preview;
- pairing chips "Paired by tags ✓" / "Timing only ⚠" (`scan.pairing`);
- **Next picture** `SCAN NEXT`, for a manual folder camera;
- Cancel.

**Step 4, Review:**
- a views table (`SCAN VIEWS <scan>`): camera, projectors, RMS, tracks, exposure path; right-click **Drop view**
  `SCAN DROP <scan> <view>`;
- coverage per projector;
- **Add a view** `SCAN START <cam> … INTO <scan>`;
- **Re-solve** `SCAN SOLVE <scan>`.

**Step 5, Surfaces:**
- **Suggestion cards** (`scan.suggestions[]`): name, reasons and confidence, with **✓** `APPROVE <proposal>` and **✗**
  `REJECT <proposal>`. A grey "AI only — not measured" card has no ✓.
- **Ask the assistant to name them** `SCAN SUGGEST <scan> ASSISTANT`. It is disabled, with words, when no assistant is
  set.
- Click-to-surface on any projector's smart scan `SCAN SURFACE <scan> REGION <rg> ON <pj>`.

**Right-click:**
- a phone camera rig: Invite again · Check phone `PHONE CHECK` · Forget phone `PHONE FORGET <ph>`;
- a scan: Add a view with… · Re-solve · Drop view… · Suggest surfaces `SCAN SUGGEST <scan>`.

**States:**

| State | The operator sees |
|---|---|
| Busy | The ring per projector |
| Armed | Scan and Invite greyed ("heavy work" / "not while armed") |
| Fault | "Moved: set it down and tap Retry" (red, from the phone row); "This page needs the node's secure address" when the node serves no HTTPS: the Invite reply's refusal, shown on the tile |

**Next round:** Re-solve holding…, Scale from a tape measure, Show coverage on the wall (S1), and USB tether.

### 5.6 LED pixel mapping and the patch editor (led)

**LED tool (D) in MAP:**
- dragging draws a line, a grid (rectangle) or a ring, on whatever the view shows:
  - the projector view, or a canvas, gives canvas or output fixtures;
  - a selected surface gives surface-UV fixtures;
  - the 3D view gives world fixtures bound to the clicked surface;
- the drag sends `LED NEW LINE|GRID|RING <count>[x<rows>] ON <space>`;
- the inspector then edits the count, type and direction live through `SET <lf> …`.

**Outliner:**
- an **LED** group: nodes, with their fixtures under them;
- unpatched fixtures in their own folder;
- badges: red for a conflict, amber for late, grey for disabled.

**Fixture inspector** (a new inspector VM with its own view):
- Layout;
- Patch: node, `universe.address`, and a footprint bar across universes. Changing it sends `LED PATCH <lf> <ln> <u>.<a>`;
- Colour: type, gamma, white, brightness;
- Sampling (Pro): point or area, footprint.

**Outputs ▸ LED** (node tiles):
- state light, fps, universes and lateness, and what the node says about itself ("PixLite16 MkII · 44 Hz");
- **Test ▾** `LED TEST chase|order|universe|fixture|gradient|walk|white|full|black ON <ln>`, and **Test off**
  `LED TEST OFF`;
- **Locate** `LED LOCATE <lf>`;
- **On / Off** (hold) `LED OUTPUT ON|OFF <ln>`;
- the machine master: "this machine transmits" (hold) `LED OUTPUT ON|OFF`;
- **Level** `LED LEVEL <n>` (Pro);
- the Mb/s estimate against the guard;
- **Discover** `LED DISCOVER ALL`;
- **Auto-patch** `LED AUTOPATCH ALL`.

**Patch editor** (Pro, LED tab ⤢):
- one 32 × 16 grid per universe (512 cells), with fixture colour bands from the project's patch;
- overlaps in red; hovering a cell shows fixture, pixel and channel;
- **dragging a band** to a new start address sends `LED PATCH <lf> <ln> <u>.<a>`. An overlap refusal shows its words
  with **Patch anyway**, which adds `FORCE`;
- **Live values** (a toggle) poll `LED MONITOR <ln> <u>` at 4 Hz while visible, and shade each cell by its byte.

**Import:** dropping `.mvr`, `.gdtf` or `.csv` on MAP sends `LED IMPORT <path>`, one undo step. A preview first is
next round.

**Previs:** fixtures are drawn as dots in the 3D and canvas views, in their editor colour (positions only).

**Fixture right-click:**
- Auto-patch from here `LED AUTOPATCH <lf>`;
- Locate;
- Test ▸;
- Set node ▸ `LED PATCH <lf> <ln> …`;
- Remove.

**Node right-click:**
- Discover;
- Test ▸;
- Monitor universe ▸;
- Output on / off.

**Surface right-click:** Add LED strip along this edge `LED NEW LINE <n> ON surface:<sf>`.

**States:**

| State | The operator sees |
|---|---|
| Busy | Discovering |
| Armed | `LED TEST` refused (words); master and level follow the policy |
| Fault | Unreachable node in red; refused by the bandwidth guard with the Mb/s numbers |

**Next round:**
- live previs colours;
- the import preview;
- camera auto-locate (S2);
- Reverse and Snake;
- Sample through viewpoint ▸.

---

## 6. Spine needs

**App side: the desk spine D0.** It lands with the round-2 spine, before any desk package. It is **prototyped in §9**.

1. **Modules.**
   - `DeskModule` (state, project, tick, slots, banners, shortcuts, menu extras, badges, inspector sections, drawer
     tabs, views, `TryEscape`/`EscapeOrder`).
   - `MainViewModel` becomes `partial`, with three composition hooks: `ComposeShowDesk`, `ComposeContentDesk` and
     `ComposeMapDesk`.
   - **Sonar S3251 fails the build on an unimplemented partial method (measured).** So the spine ships one stub file per
     package: `MainViewModel.Show|Content|Map.cs` and `FakeEngineLink.Show|Content|Map.cs`. Each package owns its stub.
2. **`ModuleViews` and `ModuleViewLocator`:** an explicit registry, with no reflection or naming convention. It sits in
   `Application.DataTemplates`, so a package adds a view-model and view pair and edits no shared template.
3. **`StateSnapshot.Root` and `Section<T>(key, reader)`:** memoised per snapshot (measured: read once). Each package
   reads its own section in its own file, and the positional record is not widened by three packages at once.
4. **The frame:**
   - top-bar slots (`show`, `timecode`, `tempo`);
   - the two-row rule below 1366 px;
   - the mode frame and the violet blind frame;
   - the attention bar;
   - `DrawerHost`: four drawers with tabs and ⤢. Today's Outputs and Network drawers become the Plan and Links tabs.
5. **Hooks on objects:**
   - `IMenuTarget { Kind, Id }` on every row VM and inspector;
   - `DeskMenu` appends module menu extras (they are menu-only, so toolbar ⊆ menu still holds);
   - a `Badges` collection on outliner nodes, media items, playlists, items, preview cards and plan tiles;
   - `InspectorViewModel.Sections`;
   - a `ParamEditor.Socket` slot, with the param templates moved to `Theme/ParamTemplates.axaml`;
   - `DeskDrop`: a drop-target attached property and a handler registry by data format.
6. **`DeskAction.Gate`:** a global refusal hook, giving IsEnabled false and the words in the tooltip.
   `DeskSession.Verbs` holds the names from `VERBS` at attach, so a control whose verb the node lacks hides.
7. **`EscapeLadder`:**
   - replaces the Esc row;
   - adds the 600 ms guard;
   - adds `DeskPrefs` (Simple/Pro, Esc is panic; `desk.json`);
   - adds `HoldButton` (600 ms ring), `ScoreRing` and `StatusRow` (light, title, value, words, Fix).
8. **`FakeEngineLink`:** per-package verb hooks, `SetSection(key, json)` for fixtures, and `PutSection` for hooks
   (prototyped).
9. **Icons and styles:**
   - the round-2 icon geometries go into `Tokens.axaml` once (panic, arm, blind, health, fleet, sync, tc, tempo, fx,
     modulation, bind, led, align, calibration, guide, phone, live, send, optimise);
   - three empty style files `Theme/Desk1|2|3.axaml` are included in `App.axaml`, one owned by each package.
10. **The verb-first CI rule** (`DeskVerbRuleTests`):
    - it builds the desk on the fake engine with every STATE fixture loaded;
    - it walks every `DeskAction` reachable from the top bar, drawers, menus (`DeskMenu.Fill` per target) and
      inspectors;
    - it asserts `VerbCatalog.Match` parses every line, and that no line uses a secret-bearing verb;
    - it asserts toolbar ⊆ menu.

**Engine and Core side** (asked of the other packages):

11. **Feature packages do not edit `src/PlayMap.App` or `tests/PlayMap.App.Tests`.** Each ships
    `tests/fixtures/state/<wp>.json`: its STATE section(s), written by one of its own engine tests from a real
    scenario and golden-checked there. The desk tests load these, so the desk's picture of STATE cannot drift from the
    engine's. This replaces "the desk's fake engine writes the same shape" in hap M9, zerocopy M8 and netvideo M8.
12. **`ShowPolicy` (showsafety M1) public and pure in Core:** `Decide(role, WireLine, ShowFacts)` gives Allowed,
    Refused(words) or Proposal. The desk calls it for greying. The same goes for `VerbCatalog.RiskOf(line)`.
13. **`VerbParams` (companion M9):** the desk finds entity ids in a line through it (for lock greying), and it later
    feeds the palette and the last-action panel.
14. **One meaning for a binding:**
    - `GENERATOR BIND/UNBIND` are aliases of `MOD BIND/UNBIND gn…` (content M10 and audio M6 bind the same
      `ParamBinding`);
    - `FX BIND` stays for slots;
    - `MOD SOURCES` lists every family, including effects' built-ins, when audio is absent.
15. **An overlay lease:** an `OVERLAY ON` from a desk session ends when that session closes, or 15 s without
    `OVERLAY KEEP`. This is the view-lease pattern main already has (`VIEW … KEEP`, f466600). Without it, a crashed desk
    leaves outlines on the audience's wall.
16. **Missing verbs the round-2 desk needs, or works around:**
    - `ALIGN DISCARD <run>` (Keep current);
    - fleet fan-out `… ON <node>` (fleet S6), so one desk can change another node's role. Round 2 attaches instead.
17. **Words to settle in the catalogue:**
    - facade's `CONTROL IMPORT` / `CONTROL CHECK` (survey control points) sit in the same first word as the control
      package's `CONTROL STATUS|PORTS|ENABLE…` (controller input). Match tells them apart by the second word, but an
      operator will not. Proposal: `SITE POINTS <path>` and `SITE CHECK`.
    - `SEND ADD` has two word orders: texshare's `SEND ADD <name> SPOUT|… <tap>` and netvideo's `SEND ADD NDI|OMT <name>
      <tap>`. Their STATE keys differ as well: texshare has top-level `inputs`/`sends`/`videoio`, netvideo has
      `videoio.inputs`/`videoio.sends`.
    - Whichever package lands the shared video-IO spine fixes one form. The desk reads `videoio.inputs|sends|discovered`
      and sends the protocol word first.
18. **One undo step** for "drop a preset or live input on a surface": either the engine coalesces `GENERATOR NEW` +
    `SURFACE SOURCE`, or `SURFACE SOURCE <sf> new:<kind> PRESET <p>`. This is next round; round 2 accepts two steps.

---

## 7. The packages

The desk spine D0 lands first, with the round-2 spine. D1, D2 and D3 then run in parallel, each after its feature
packages land. **Their files are disjoint.** After D0, each existing hot file has exactly one owner:

| Existing file (after D0) | Owner |
|---|---|
| `Views/MainWindow.axaml(.cs)`, `Views/PlayView.axaml`, `ViewModels/PlaylistViewModel.cs`, `ViewModels/TransportViewModel.cs`, `ViewModels/Inspectors.cs`, `ViewModels/DrawerViewModels.cs`, `Views/NetworkDrawer.axaml(.cs)`, `Views/OutputsDrawer.axaml(.cs)`, `Controls/PlanCanvas.cs`, `ViewModels/ConnectViewModel.cs` | **D1** |
| `Views/LibraryPanel.axaml(.cs)` (extracted by D0), `ViewModels/PlayViewModel.cs`, `ViewModels/GeneratorInspector.cs`, `Views/InspectorView.axaml`, `Theme/ParamTemplates.axaml` | **D2** |
| `Views/MapView.axaml(.cs)`, `ViewModels/MapViewModel.cs`, `ViewModels/MapInspectors.cs`, `Controls/ProjectorCanvas.cs`, `Controls/MapViewport.cs`, `Controls/StageDrawing.cs`, `Scene/*`, `Views/ScanView.axaml(.cs)`, `ViewModels/ScanWizardViewModel.cs`, `ViewModels/ShortcutMap.cs` | **D3** |
| `App.axaml`, `ViewModels/MainViewModel.cs`, `ViewModels/DeskAction.cs`, `Controls/DeskMenu.cs`, `State/StateSnapshot.cs`, `Theme/Tokens.axaml`, `Theme/Templates.axaml`, `Theme/DeskStyles.axaml`, `Link/FakeEngineLink.cs`, `Link/FakeEngineLink.State.cs`, `Link/DeskSession.cs`, `Infrastructure/*` | **nobody** (D0 only) |

**Tests:** each package adds its own test files. It edits an existing test file only if it owns the file under test:
- D1 owns `DrawerTests.cs`, `TransportAndMenuTests.cs` and `MainWindowTests.cs`;
- D2 owns `InspectorTests.cs` and `PlayDragTests.cs`;
- D3 owns `MapTests.cs` and `ScanWizardTests.cs`.

`DeskVerbRuleTests` (D0) must stay green in all three.

### D0: desk spine (with the round-2 spine), ≈ 10 agent-hours

- **New:**
  - `Infrastructure/ModuleViews.cs`, `Infrastructure/DeskPrefs.cs`, `Infrastructure/DeskDrop.cs`, `Infrastructure/EscapeLadder.cs`;
  - `ViewModels/DeskModule.cs` (`DeskModule`, `Banner`, `ObjectBadge`, `IMenuTarget`, `DrawerTab`), `ViewModels/StatusRow.cs`;
  - `Controls/HoldButton.cs`, `Controls/ScoreRing.cs`;
  - `Views/DrawerHost.axaml(.cs)`, `ViewModels/DrawerHostViewModel.cs`, `Views/LibraryPanel.axaml(.cs)`;
  - `Theme/ParamTemplates.axaml`, `Theme/Status.axaml`, `Theme/Desk1.axaml`, `Theme/Desk2.axaml`, `Theme/Desk3.axaml`;
  - the stubs `ViewModels/MainViewModel.{Show,Content,Map}.cs` and `Link/FakeEngineLink.{Show,Content,Map}.cs`;
  - `tests/PlayMap.App.Tests/DeskSpineTests.cs`, `DeskVerbRuleTests.cs`, `StateFixtures.cs`, and `tests/fixtures/state/README.md`.
- **Touched:**
  - `App.axaml`, `Views/MainWindow.axaml(.cs)`, `ViewModels/MainViewModel.cs`, `ViewModels/ShortcutMap.cs`,
    `ViewModels/DeskAction.cs`, `Controls/DeskMenu.cs`, `State/StateSnapshot.cs`;
  - `ViewModels/Inspectors.cs`, `Views/InspectorView.axaml`, `ViewModels/GeneratorInspector.cs` (the socket slot only);
  - `ViewModels/PlayViewModel.cs`, `ViewModels/PlaylistViewModel.cs`, `ViewModels/MapViewModel.cs`,
    `ViewModels/DrawerViewModels.cs` (`IMenuTarget` and `Badges` only);
  - `Views/PlayView.axaml` (bin → LibraryPanel; badge and drop slots), `Views/MapView.axaml` and
    `Views/OutputsDrawer.axaml` (badge and drop slots);
  - `Theme/Tokens.axaml`, `Theme/Templates.axaml`, `Theme/DeskStyles.axaml`;
  - `Link/FakeEngineLink.cs`, `Link/FakeEngineLink.State.cs`, `Link/DeskSession.cs`.
- **Tests:**
  - modules compose through hooks;
  - the locator builds a registered view;
  - a section is read once;
  - banners come and go with STATE;
  - the top bar fits at 1100, 1280, 1366, 1600 and 1920 px;
  - the Esc ladder order and the 600 ms guard;
  - badges and sections render;
  - the verb rule.
- **The prototype patch is the starting point:** `r2/bench/desk/desk-spine-proto.patch` (486 lines added over 18 files).

### D1: Show & health, ≈ 30 agent-hours

- **Depends on:** showsafety, fleet, framelock, present, timecode, control and companion (and the D0 spine).
- **Round-2 cut:**
  - §2.2 the show strip, minus tempo;
  - §2.3 the frame;
  - D1's banners in §2.4;
  - §3.1 blind, panic plans, locks, the GO fence and the close confirm;
  - §3.2 preflight;
  - §3.3 the fleet board, alarms and attach-to-backup;
  - §3.4 Sync;
  - §3.5 present chips and the Display section;
  - §3.6 the TC chip, chase menus, TC in and the Timecode tab;
  - §3.7 Bind ▸, Bound to ▸, badges, learn and Controllers;
  - §3.8 Decks.
- **New:**
  - `ViewModels/Show/` (ShowModule, ShowStripViewModel, ShowSection, ShowGate, BlindPanelViewModel, PanicPlanMenu, LocksMenu, PreflightViewModel);
  - `ViewModels/Health/` (FleetModule, FleetSection, FleetBoardViewModel, MachineCardViewModel, RouteForm, SyncTabViewModel, SyncSection, PresentSection, OutputDisplaySection);
  - `ViewModels/Inputs/` (ControlModule, ControlSection, BindMenu, ControllersTabViewModel, BindingRowViewModel, TimecodeModule, TimecodeSection, TimecodeChipViewModel, TimecodeTabViewModel, ChaseMenu);
  - `ViewModels/Network/DecksTabViewModel.cs`;
  - `Views/Show/*`, `Views/Health/*`, `Views/Inputs/*` (Controllers, Timecode, TimecodeChip), `Views/Network/DecksTabView.axaml`;
  - its owned stubs `ViewModels/MainViewModel.Show.cs` and `Link/FakeEngineLink.Show.cs` (SHOW, PANIC, LOCK, BLIND, PREFLIGHT, FLEET view verbs, TIMECODE, BIND LEARN in the demo); `Theme/Desk1.axaml`;
  - tests: `ShowStripTests.cs`, `BlindLocksPanicTests.cs`, `PreflightBoardTests.cs`, `FleetBoardTests.cs`, `SyncTabTests.cs`, `TimecodeDeskTests.cs`, `BindingsDeskTests.cs`, `DecksTests.cs`.
- **Touched:** the D1 rows of the ownership table.
- **Acceptance (App tests, headless, fake engine with WP fixtures):**
  - ARM sends `SHOW ARM`, and with the `show` fixture armed, mapping actions are disabled with the gate's words;
  - F12 sends `PANIC`, and the panic fixture raises the red banner, whose Clear sends `PANIC CLEAR FADE 1000`;
  - Esc in PLAY with the pref on sends `PANIC`, and Esc after closing a drawer within 600 ms does not;
  - Blind: the diff rows render from a `BLIND DIFF` reply, and "Commit at next item" sends
    `BLIND COMMIT NEXT <focused>`;
  - every preflight Fix sends its row's fix line;
  - the fleet fixture renders three cards with stale/gone colours;
  - the failover alarm banner sends `FAILOVER`, and a dropped link shows Attach to <backup>;
  - Ctrl+Right sends `PLAYLIST NEXT <pl> EXPECT <it>`;
  - Bind ▸ entries equal the menu's verbs and send `BIND LEARN <line>`, and Esc during learn sends `BIND LEARN OFF`;
  - Set TC in refuses `00:01:00;00` at 29.97 DF before sending.

### D2: Content & media, ≈ 31 agent-hours

- **Depends on:** effects, audio, content, optimise, hap, zerocopy, texshare and netvideo (and D0).
- **Round-2 cut:**
  - §2.2 the tempo chip;
  - §4.1 the effects browser, slots, menus, audition, a minimal shader editor and plugin trust;
  - §4.2 the socket, popover, drag-to-bind, Make it react and the Audio tab;
  - §4.3 generator families and presets, inspector groups, the preset bar and morph, the sting pad, and surface menus;
  - §4.4 badges, jobs strip, banner, Optimise ▸ / Use ▸, and On this node;
  - §4.5 the Live tab, input menus and the Sends tab.
- **New:**
  - `ViewModels/Content/` (EffectsModule, FxSection, EffectsBrowserViewModel, EffectTile, EffectsSectionViewModel, FxSlotViewModel, ShaderEditorViewModel, PluginRow, ModSocketViewModel, ModSources, AudioModule, AudioSection, TempoChipViewModel, AudioTabViewModel, ContentModule, ContentSection, PresetBrowserViewModel, PresetBar, StingPadViewModel);
  - `ViewModels/Media/` (OptimiseModule, OptimiseSection, JobsStripViewModel, MediaNodeSection, VideoIoModule, VideoIoSection, LiveTabViewModel, LiveInputTile, SendsTabViewModel);
  - `Views/Content/*`, `Views/Media/*`;
  - its owned stubs `ViewModels/MainViewModel.Content.cs` and `Link/FakeEngineLink.Content.cs`; `Theme/Desk2.axaml`;
  - tests: `EffectsDeskTests.cs`, `ModulationDeskTests.cs`, `TempoDeskTests.cs`, `ContentDeskTests.cs`, `OptimiseDeskTests.cs`, `MediaNodeDeskTests.cs`, `VideoIoDeskTests.cs`.
- **Touched:** the D2 rows of the ownership table.
- **Acceptance:**
  - dropping an effect tile on an outliner row sends `FX ADD <sf> <kind>`, and Alt-drop adds `FX SET <slot> mix 0`;
  - slot bypass, Amount and a parameter send `FX BYPASS|SET`;
  - holding A over a tile sends `FX AUDITION <view> <target> <kind>`, and release sends `… OFF`;
  - a shader-editor pause sends `FX CHECK`, and the issues render at their lines;
  - dropping a Bass chip on a generator's speed socket sends
    `MOD BIND <gn> speed audio:bass AMOUNT 0.5 SMOOTH 30/250` (the five-second gesture, timed manually);
  - T in PLAY sends `TEMPO TAP`, and T in MAP still opens the test pattern menu;
  - Recall and Morph send `GENERATOR RECALL|MORPH`;
  - the ask-mode banner's three buttons send their lines, and the jobs strip renders the governor's words from the
    fixture;
  - the Live tab renders the NDI link and trademark line (netvideo M12);
  - dragging an input onto a surface sends `INPUT ADD` then `SURFACE SOURCE <sf> live:<md>`.

### D3: Map & calibration, ≈ 32 agent-hours

- **Depends on:** firstfive, calibops, autoalign, facade, phonescan and led (and D0).
- **Round-2 cut:**
  - §5.1 the first-light guide, Q quad, S select, on-wall mode, nudges, Tab, G, O, C, L and the quad menu;
  - §5.2 the score card, terms panel, history strip, Health ▸ Calibration, banners and menus;
  - §5.3 the Align tab steps, per-device natives, corners and menus;
  - §5.4 the Guide component with the facade guide and projector lines;
  - §5.5 the phone tile, QR, phone card, views, coverage and suggestion cards;
  - §5.6 the LED tool, outliner group, fixture inspector, LED tab, patch editor and import drop.
- **New:**
  - `ViewModels/Map/` (MapModule, QuadTool, OnWallMode, GuideViewModel, GuideStep, FirstLightGuide, FacadeGuide, SiteSection, ControlPointRow, ProjectorSolveSection, CalibrationModule, CalibrationSection, CalibrationHistoryViewModel, CalibrationTabViewModel);
  - `ViewModels/Outputs/` (AlignModule, AlignTabViewModel, AlignScoreItem, DeviceNativeRow);
  - `ViewModels/Led/` (LedModule, LedSection, LedTool, LedTabViewModel, LedNodeTile, LedPatchGridViewModel, LedFixtureInspector);
  - `ViewModels/Scan/` (PhoneJoinViewModel, ScanViewsViewModel, SuggestionCard);
  - `Controls/QrCodeView.cs`, `Controls/HistoryStrip.cs`, `Controls/DmxGrid.cs`, `Controls/CornerPicker.cs`;
  - `Views/Map/*`, `Views/Outputs/AlignTabView.axaml`, `Views/Led/*`, `Views/Scan/*`;
  - its owned stubs `ViewModels/MainViewModel.Map.cs` and `Link/FakeEngineLink.Map.cs`; `Theme/Desk3.axaml`;
  - tests: `QuadToolTests.cs`, `OnWallTests.cs`, `FirstLightTests.cs`, `CalibrationDeskTests.cs`, `AlignTabTests.cs`, `FacadeGuideTests.cs`, `PhoneScanDeskTests.cs`, `LedDeskTests.cs`.
- **Touched:** the D3 rows of the ownership table.
- **Acceptance** (through the real input path, as `MapTests` does today):
  - Q, four clicks, Enter emit `CURSOR … TRAIL …` lines and then exactly one `SURFACE QUAD`;
  - Alt+Right sends `SURFACE CORNER <sf> <c> BY 0.1 0 PX`;
  - Ctrl+Left still sends the transport's previous-item line in MAP;
  - O sends `OVERLAY ON ON <op>`, and leaving MAP sends `OVERLAY OFF ON <op>`;
  - with the armed fixture, O is disabled with the gate's words;
  - a term's Fix sends the term's fix line, and a history dot's Recall sends `CALIBRATION RECALL cs…`;
  - the moved banner's Re-solve sends `CALIBRATION RESOLVE <pj>`;
  - Align Start → Plan → Run → staged (fixtures) → Accept sends the five lines in order;
  - the phone tile sends `PHONE INVITE`, and its QR renders `size²` modules from the fixture;
  - a suggestion ✓ sends `APPROVE ap…`;
  - dragging a band in the patch grid sends `LED PATCH`;
  - an overlap refusal offers Patch anyway (`… FORCE`).

### Next round, in one list

**Show and health:** the dark-room show strip; parked edits; soft leases; Bind mode (Ctrl+M) and the last-action panel;
the Ctrl+K palette over the typed verbs; F2 rename; the night-red theme and UI brightness; `FLEET AT` replay and
metrics; fleet fan-out; certificate history; TC ruler, cues and outputs; Companion Identify/Add/Dim, key pages and
`SELECT`.

**Content and media:** live thumbnails for effects and presets; drag-reorder slots; ISF live preview; named
modulators; onset lanes; launch quantisation; the Versions/A-B view; the rules editor; multiview; PTZ; Video I/O
network tab.

**Map and calibration:** snapping; `PICK PIXEL`; `HIGHLIGHT`; calibration Compare split; align before/after; stacks;
light legend; the uncertainty ellipsoid; Scale from a tape measure; LED live previs; the import preview; LED camera
locate; "Bring my setup" importers.

---

## 8. Companion and the desk, in step

The desk and the Companion module (companion WP) are two faces of one vocabulary:

- **Same words.** Every desk tooltip shows the line a Companion key would send.
- **Same gating.**
  - The desk greys a control from Core `ShowPolicy`.
  - Companion greys a key from `WHOAMI may`.
  - Both come from the same table.
- **Same holds.** High-risk verbs that touch many nodes are hold-to-confirm on the desk (§2.2 and §3) and hold presses in
  the module.
  - GO, PANIC and BLACKOUT are never confirmed on either.
- **What the desk adds:**
  - the places where words need room: diffs, score terms, preflight rows;
  - the gestures: quads, drags, learn.
- **What Companion adds:** physical keys, encoders and auto-created pages.
- **One handover:** the desk's Decks tab lists the decks, so the operator sees who can press GO.

---

## 9. Verification here

**Measured or run in this container** (4 vCPU, load average 5–8 from other sessions):

- **Read for this design:**
  - all 21 round-2 specs: §2 (Must, Should, Later), §4.2 verbs, §4.3 STATE and §8 desk/Companion;
  - docs/UX.md;
  - CARRYOVER.md;
  - every desk source file on main.
- **Main moved during this work.** It went from `c12b186` to `cb3cfc0`, adding view leases (`VIEW … KEEP`). The overlay
  lease in §6 reuses that pattern.
- **Baseline:** `dotnet build tests/PlayMap.App.Tests` took 1 min 40 s on a cold clone and 10–25 s incremental. The 43
  desk tests pass in 27 s.
- **Spine prototype** in a scratch clone of `cb3cfc0` (`r2/bench/desk/mapper`; patch `r2/bench/desk/desk-spine-proto.patch`,
  18 files, +486 lines):
  - `DeskModule`, `ModuleViews` / `ModuleViewLocator`, `StateSnapshot.Root` and `Section<T>`, the attention bar;
  - `FakeEngineLink` hooks and sections;
  - the show strip as a module (ARM, PANIC, the panic banner with Clear);
  - **All 46 desk tests green** (43 existing + 3 new) in 37 s, with every analyzer fence on.
  - The headless test clicks ARM → `SHOW ARM` → "ARMED · Desk" from the fixture section, then PANIC → red banner →
    Clear → `PANIC CLEAR` → banner gone.
  - A section reader runs once per snapshot.
- **Found:** Sonar S3251 is a build error for an unimplemented partial method. Hence the per-package stub files (§6.1).
  Verified by the failing and then passing build.
- **Top bar, measured:**
  - today it wants **1686 px**; at the **1100 px** minimum its help button ends at **1147 px**, so it is clipped today;
  - with a 197 px show strip it wants **1883 px** (1344 px at 1100);
  - at 1600 px the strip overlapped the master transport (screenshot `r2/bench/desk/screens/proto-show-strip.png`).

  Hence the two-row rule and the compact order (§2.1).

**Simulated:** every round-2 feature's STATE, through the fake engine's sections and the WPs' fixtures (§6.11).

**Not verifiable here, and how it is verified:**
- real engines;
- hardware facts (DXGI, NVAPI boards, NDI runtimes, MIDI devices, phones, LED nodes);
- the UX.md time targets ("bind a MIDI control ≤ 3 s", "map a box ≤ 3 min", "five-second modulation gesture").

These are covered by:
- each WP's own tests;
- the filmed storyboards and telemetry of UX.md §12;
- the manual timings listed in each package's acceptance.

---

## 10. Risks and open questions

1. **Top-bar density.** Even with the two-row rule, a 1100 px desk with a chase, audio, blind and a fleet is busy.
   - Mitigation: Simple hides TC and tempo unless active.
   - Check: a monthly usability session (UX.md §12).
2. **Esc as PANIC.** Esc is off by default; the guard and the ladder stop accidental panics; F12 is the dedicated key.
   - Open: should "Esc is panic" become a show rule (`SHOW SET ESC-PANIC`) rather than a desk preference, so every desk
     on the show behaves the same? The proposal keeps it a preference: the key is the desk's, not the show's.
3. **Policy drift.** Greying from Core `ShowPolicy` assumes the engine gates with the same table and the same facts.
   - A desk test runs the policy over fixtures.
   - The engine's router tests cover the gate.
   - A mismatch still fails safe: the engine refuses, and the desk shows the words.
4. **Polling cost.** `FLEET` at 1 Hz; `MOD PEEK`, `CONTROL MONITOR` and `AUDIO STATUS` at 10 Hz; `LED MONITOR` at 4 Hz.
   - All of it runs only while visible, and all of it is quiet sends (no toast).
   - Budget: under 20 lines a second per desk. To measure in D1–D3 with the fake engine's sent-line counter.
5. **STATE growth.** With `fx.slots`, `timecode`, `control`, `audio` and `fleet`, STATE grows, and the desk parses it
   once per push.
   - `Section<T>` memo keeps each section to one parse.
   - Open: a per-key revision in STATE would let the desk skip unchanged sections.
6. **Two undo steps** for drop-to-surface (content, live inputs) until §6.18 lands. Operators may undo half a gesture.
   The toast says "2 changes".
7. **Attach-to-backup** relies on the last fleet roster the desk saw, and a desk started after the death has none. It
   then falls back to the Connect overlay, which lists peers heard on the Show Bus.
8. **Simple/Pro discoverability.** "n more in Pro" links and identical right-click menus keep power findable (UX.md
   anti-pattern 9). The palette (next round) completes it.
9. **The fixture contract (§6.11) is new for the feature WPs.** If a WP skips it, the desk package hand-writes a fixture
   from the spec's STATE example and marks the test "fixture from spec, not from engine".

---

## 11. Effort (agent-hours)

| Package | Hours | Of which tests |
|---|---|---|
| D0 desk spine (with the round-2 spine) | 10 | 3 |
| D1 Show & health | 30 | 7 |
| D2 Content & media | 31 | 7 |
| D3 Map & calibration | 32 | 7 |
| **Total** | **103** | 24 |

For scale, the specs' own §10 desk lines sum to about 150 h. This cut keeps the controls each feature's Must needs. It
reaches them through five shared parts and the module hooks, and moves the rest to the next round.
