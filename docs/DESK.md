# The desk

The desk (`src/PlayMap.App`) keeps three workspaces (PLAY, MAP, SCAN) and adds a show strip, an attention bar, four
drawers and generic parts, so most round-2 features add data, not panels. It sends verbs and reads STATE: every button,
right-click entry and key names the catalogue line it sends, the same line the wire, MCP and Companion send.

## Its parts

| Part | What the operator sees | Lines |
|---|---|---|
| **Show strip** (top bar) | Ready light ("Ready 4/5", "Checking…") · ARM ("ARMED · Desk · 20:31") · PANIC (never a dialog) · Blackout. PANIC, its outcomes, the panic banner's Clear, Blackout and the master send every press at once, never waiting for the reply before (a second PANIC within the fade is HARD however slow the link), and stay live on a stale link | `PREFLIGHT QUICK`, `PREFLIGHT`, `FLEET NEXT` · `SHOW ARM`/`SHOW DISARM` (Ctrl+Shift+A) · `PANIC` (F12) · `BLACKOUT ON\|OFF` |
| ARM's right-click (the Show menu) | Arm all nodes · Lock area ▸ · Unlock all · Edit blind · Rules ▸ (Min GO gap, Remote view only, Expect armed, Esc is panic) | `SHOW ARM ALL`, `LOCK AREA <a>`, `UNLOCK ALL`, `BLIND ON`, `SHOW SET MINGO <ms> ARMED`, `SHOW SET REMOTE-VIEW-ONLY ON\|OFF`, `SHOW SET EXPECT-ARMED ON\|OFF` |
| PANIC's right-click | The nine outcomes | `PANIC BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD` |
| Blackout's right-click | Master ▸ 100 / 75 / 50 / 0 % over 2 s | `MASTER <n> FADE 2000` |
| **The gate, desk-wide** | Every control asks the node's doors in the node's order before the press, each with the node's own words: pairing (`WHOAMI`'s `paired`: an unpaired desk's lines that change something, PANIC among them); the fleet's (Core's `BackupGate` with STATE.fleet's role and door words); an open blind draft (no `PROJECT OPEN` or `PROJECT NEW`); the Core show policy (`ShowPolicy.Decide`, the desk's role from `WHOAMI`, STATE.show's facts, this desk as the draft's owner when STATE.show.blind names its session); the network area's lock; then `LockGuard` on what a `SET`, `ADD`, `REMOVE` or `MOVE` would change in the mirrored project. A refused control is disabled, its tooltip carries the words, and a menu entry names the door: "· not paired", "· mirror", "· fenced", "· blind draft", "· armed", "· locked". Armed, transport, Blackout, PANIC and MASTER stay live; after Edit blind, this desk's document edits go to its draft, armed or not, until its commit waits for its boundary. On a hot backup or a fenced node queries, PANIC, Show Mode and the fleet's verbs (FAILOVER) stay live and a banner says whose show it is. An inspector field whose own line is refused says the words and sends nothing; a surface or projector locked by its own flag shows Unlock beside them. The window's frame is red while armed, amber while anything but the show is on a wall; it is drawn over the window's edge and moves nothing | — |
| **Attention bar** | Rebuilt from STATE on every push, worst first, two shown and "+n more". Panic: "PANIC black since 21:04:05 by Desk — op_1: black" with **Clear** and a fade menu; rehearsal while playing with **Arm** | `PANIC CLEAR FADE 1000` (0 / 1 / 2 / 5 s), `SHOW ARM` |
| **Locks** | Lock for the show / Unlock in every surface, projector, output, playlist and item menu and on its inspector toolbar; a padlock badge with who and why | `LOCK <id>`, `UNLOCK <id>` |
| **Drawers** | Outputs (Plan), Inputs (cards of controllers, sound, live video), Health (every card), Network (Links); one open at a time; ⤢ takes the workspace | Ctrl+Shift+O / I / H / N |
| **Generic STATE cards** | One card per descriptor in `tests/fixtures/state/*.cards.json` (built into the desk): light, title, words, six fields on its face and the rest under "more", actions; under Health, Inputs and in the inspector of the object it is about (a block a list element owns, such as a projector's solve uncertainty, sits with that element). An action's values come from the card's STATE through its placeholders, never from a fixture; a button its own card greys says why ("STATE has no node for this card"). A card that comes or goes leaves the others' views as they are. The desk's own cards: solve, cameras, the assistant | each card's action lines |
| **Generated entries** | After a rule in every object's menu, and on its inspector toolbar: Add effect ▸, Modulate ▸, Bind ▸, Optimise ▸, Use input ▸ — built from the node's own answers; an entry whose verb the node does not run hides. An object's entries are the same instances from push to push (an open flyout keeps its anchor); a generator's are made again when its kind changes | `FX ADD <target> <kind>`, `MOD BIND <entity> <param> <source>`, `BIND LEARN <line>`, `MEDIA OPTIMISE <md> TO <target>`, `SURFACE SOURCE <sf> live:<in>` |
| **Esc ladder** | Esc goes to the first rung that uses it: a pattern task on the wall (`SCAN CANCEL`, `ALIGN CANCEL`, `CALIBRATION CANCEL`, read in the engine's own state words, Core's `PatternTasks`), a learn (`BIND LEARN OFF`), the pen's draft, the overlays (help, a drawer), PANIC (PLAY, with the desk's preference), the selection. A cancel the gate would refuse is not sent and Esc goes on down. For 600 ms after a lower rung used Esc, or after anything closed (a drawer by any means, the help, a right-click menu, a flyout), Esc does not reach PANIC. While a text field types, Esc is the field's: it reaches the field, and only the overlays answer it too | — |
| **Simple / Pro** | A desk preference in desk.json (Ctrl+Shift+P); Simple will hide, never disable, and hides nothing yet; menus and the show strip are the same in both | — |

Not built yet: a confirmation before closing while armed, PANIC's pulse, the master's level bar, the PLAY bin as a panel of its own.
A drag the gate refuses still moves on screen; its steps are not sent and a toast gives the node's words.

## The spine other desk packages build on

`DeskModule` (slots, banners, shortcuts, menu extras, badges, inspector sections, drawer tabs, the gate, an Esc rung),
composed by one partial hook per package (`MainViewModel.Spine|Show|Content|Map.cs`), each push taken in two passes (every module
takes it, the gate's facts move, then every module re-reads its actions, so none asks the gate with the push before); an action
raises only what moved; views registered with `ModuleViews`;
`StateSnapshot.Root` and `Section<T>` (read once per snapshot); `IMenuTarget` on every row and inspector; `DeskSession.Gate`
and `DeskSession.Verbs`; `StatusRow`, `HoldButton`, `ScoreRing`, `DeskDrop`; the param templates in
`Theme/ParamTemplates.axaml` with a socket slot; `Theme/Desk1|2|3.axaml`. `DeskVerbRuleTests` walks every reachable action
with every fixture loaded and fails on a line the catalogue does not parse, a line with a secret, or a toolbar not in its
menu.

## The top bar

The bar is one row when everything fits and the window is at least 1366 px; otherwise two rows (identity, workspaces and
drawers; then transport, timecode, tempo and the show cluster). With the full show cluster one row wants about 1966 px,
so a 1920 px window shows two rows. Below 1280 px the transport drops its position column and the drawer pills are icons;
below 1200 px Simple|Pro leaves the bar (Ctrl+Shift+P still switches). No child ends beyond the window at 1100, 1280,
1366, 1600 or 1920 px; the show cluster never shrinks. The mode frame takes no room, so arming changes nothing in the bar.
