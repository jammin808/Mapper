# The desk

The desk (`src/PlayMap.App`) keeps three workspaces (PLAY, MAP, SCAN) and adds a show strip, an attention bar, four
drawers and generic parts, so most round-2 features add data, not panels. It sends verbs and reads STATE: every button,
right-click entry and key names the catalogue line it sends, the same line the wire, MCP and Companion send.

## Its parts

| Part | What the operator sees | Lines |
|---|---|---|
| **Show strip** (top bar) | Ready light ("Ready 4/5", "Checking…") · ARM ("ARMED · Desk · 20:31") · PANIC (never a dialog) · Blackout | `PREFLIGHT QUICK`, `PREFLIGHT`, `FLEET NEXT` · `SHOW ARM`/`SHOW DISARM` (Ctrl+Shift+A) · `PANIC` (F12) · `BLACKOUT ON\|OFF` |
| ARM's right-click (the Show menu) | Arm all nodes · Lock area ▸ · Unlock all · Edit blind · Rules ▸ (Min GO gap, Remote view only, Expect armed, Esc is panic) | `SHOW ARM ALL`, `LOCK AREA <a>`, `UNLOCK ALL`, `BLIND ON`, `SHOW SET MINGO <ms> ARMED`, `SHOW SET REMOTE-VIEW-ONLY ON\|OFF`, `SHOW SET EXPECT-ARMED ON\|OFF` |
| PANIC's right-click | The nine outcomes | `PANIC BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD` |
| Blackout's right-click | Master ▸ 100 / 75 / 50 / 0 % over 2 s | `MASTER <n> FADE 2000` |
| **Armed, desk-wide** | Every control asks the engine's doors in their order before the press: the fleet's (Core's `BackupGate` with STATE.fleet's role and door words), the Core show policy (`ShowPolicy.Decide`, the desk's role from `WHOAMI`, STATE.show's facts), and the locks in STATE.show with `LockGuard`'s own words. A refused control is disabled, its tooltip carries the node's words, a menu entry reads "· armed" (or "· locked"). Armed, transport, Blackout, PANIC and MASTER stay live; on a hot backup or a fenced node queries, PANIC, Show Mode and the fleet's verbs (FAILOVER) stay live and a banner says whose show it is. The window's frame is red while armed, amber while anything but the show is on a wall | — |
| **Attention bar** | Rebuilt from STATE on every push, worst first, two shown and "+n more". Panic: "PANIC black since 21:04:05 by Desk — op_1: black" with **Clear** and a fade menu; rehearsal while playing with **Arm** | `PANIC CLEAR FADE 1000` (0 / 1 / 2 / 5 s), `SHOW ARM` |
| **Locks** | Lock for the show / Unlock in every surface, projector, output, playlist and item menu and on its inspector toolbar; a padlock badge with who and why | `LOCK <id>`, `UNLOCK <id>` |
| **Drawers** | Outputs (Plan), Inputs (cards of controllers, sound, live video), Health (every card), Network (Links); one open at a time; ⤢ takes the workspace | Ctrl+Shift+O / I / H / N |
| **Generic STATE cards** | One card per descriptor in `tests/fixtures/state/*.cards.json` (built into the desk): light, title, words, six fields on its face and the rest under "more", actions; under Health, Inputs and in the inspector of the object it is about (a block a list element owns, such as a projector's solve uncertainty, sits with that element). The desk's own cards: solve, cameras, the assistant | each card's action lines |
| **Generated entries** | After a rule in every object's menu, and on its inspector toolbar: Add effect ▸, Modulate ▸, Bind ▸, Optimise ▸, Use input ▸ — built from the node's own answers; an entry whose verb the node does not run hides | `FX ADD <target> <kind>`, `MOD BIND <entity> <param> <source>`, `BIND LEARN <line>`, `MEDIA OPTIMISE <md> TO <target>`, `SURFACE SOURCE <sf> live:<in>` |
| **Esc ladder** | Esc goes to the first rung that uses it: a learn (`BIND LEARN OFF`), a draft, the overlays, PANIC (PLAY, with the desk's preference), with a 600 ms guard after a lower rung | — |
| **Simple / Pro** | A desk preference in desk.json (Ctrl+Shift+P); Simple will hide, never disable, and hides nothing yet; menus and the show strip are the same in both | — |

## The spine other desk packages build on

`DeskModule` (slots, banners, shortcuts, menu extras, badges, inspector sections, drawer tabs, the gate, an Esc rung),
composed by one partial hook per package (`MainViewModel.Spine|Show|Content|Map.cs`); views registered with `ModuleViews`;
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
1366, 1600 or 1920 px; the show cluster never shrinks.
