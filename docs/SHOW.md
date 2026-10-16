# Show Mode, locks, panic, blind edit and preflight

What keeps a running show safe from the people and the machines that drive it: one switch that turns a rehearsal into a show
(`SHOW ARM`), locks on what is finished, a panic that takes the air down on the show clock — picture and sound — and brings it
back, a grand master, a fence against the double GO, blind edits that land at a show boundary, and a preflight that says what is
not ready with the line that fixes it. The verbs, replies and STATE are in docs/wire/show.md (wp2-show) and docs/wire/blind.md
(wp2-blind, its continuation).

## Show Mode

Rehearsal is today's node: every paired caller may change anything. `SHOW ARM [<why>]` arms the show, with no password: from then
on the table below decides every line, after pairing and before its handler, for every caller — the desk, Patterns, Companion,
OSC and MIDI bindings, the wire, MCP and the assistant. `SHOW DISARM` goes back to rehearsal. Neither is an undo step.

**Armed is the safe side**, so it survives a restart: the recovery record keeps who armed it, when and why, and a node that comes
back comes back armed (a panic in force comes back too, at its outcome at once).

**Roles** come from the session's transport, never from words a client sends: the desk is the **operator**; any other paired
client (wire, HTTP, WebSocket, Companion, OSC, bindings, Patterns, Stagewatch) is a **remote** — an **operator** when the node's
settings list it (`show.operators`) *and* it proved the node's token on its own connection (a session paired only because its
address is trusted proved nothing; on a node that asks no token only this machine counts). The session carries no "proved" mark
of its own yet, so a session from a trusted address never counts as an operator even when it did send the token (the spine's
`SessionInfo.Proved` is asked for). An entry is an address
(`10.0.0.7`), a name at an address (`Companion: FOH deck@10.0.0.7`), or a bare name, which names a session from this machine
only — a HELLO name is a claim, never a role. MCP and the assistant are the **assistant**; a person's
approval of an assistant's line is **approved**; fleet and sync peers are **peers**; the node itself (recovery, a panic's own
lines) is the **system**; an unpaired client only observes.

**Classes** come from the catalogue (`VerbSpec.Show`); a generic edit (`SET`, `ADD`, `REMOVE`, `MOVE`, `NUDGE`) is classed by the
id it names (`op` layout; `sf pj ms cm sc mk pn vp` mapping; `cv ly gn fx sd` content; `pl it` playlists; `md` media; `dv` device;
an unknown prefix counts as mapping, the cautious side).

**The armed table** (A runs, R refused with words, P waits for a person):

| Class | Operator | Remote | Assistant | Approved | Peer | System |
|---|---|---|---|---|---|---|
| Query | A | A | A | A | A | A |
| Transport, Perform | A (MinGo, EXPECT) | A | P | A | A | A |
| Air towards dark (BLACKOUT ON, FREEZE ON, MASTER 0) | A | A | P | A | A | A |
| Air towards light | A | A | P | R | A | A |
| Panic | A, never refused | A, never refused | P | A | A | A |
| Show: ARM, LOCK, PREFLIGHT | A | A | P | R | A | A |
| Show: DISARM, UNLOCK | A | A when DISARM-FROM PAIRED, else R | P | R | A unless DISARM-FROM DESK | A |
| Show: SET, PANIC SET | A | R | P | R | A | A |
| Content | A (live) | R | P | R | A | A |
| Playlists | PLAYLIST ADD only | R | P | R | A | A |
| Mapping, Layout | R | R | P | R | A | A |
| Media | IMPORT, FETCH | FETCH | P | R | A | A |
| Heavy, Setup | R | R | R | R | R | A |
| Device (shutter, input, freeze) | A | SHUTTER … CLOSE only | P | R | A | A |
| Device power | ON; OFF with CONFIRM | R | P | R | R | A |
| Project | SAVE, UNDO, REDO | R | P | R | R | A |
| Network | R | R | P | R | A | A |
| Fleet | FAILBACK with CONFIRM | FAILOVER only | P | R | A | A |
| Admin (credentials) | A | R | R | R | R | A |

`SHOW SET REMOTE-VIEW-ONLY ON` makes an armed remote read-only, and an approved assistant's line with it (PANIC still runs). PANIC is never refused for a paired person, armed
or not, under a lock or not; from an assistant it waits for a person.

**The words** say the rule and the way out: "Not while armed: WARP NODE moves the mapping on the air. SHOW DISARM on the desk
first." Refusals are journalled and counted in `STATE.show.refused` (last minute, and the last one in words).

**The blind draft's owner** (below) gets a fourth answer for content, playlists, mapping, layout, media and its own UNDO/REDO, armed
or not: the line runs *into its draft*, off the air. Every other class keeps the table's answer.

**An assistant asks first.** MCP's tools and the assistant's `propose_commands` ask this table (`IShowDecisions`) before they run or
queue a line: a refused line is neither run nor queued (and stops the lines after it), a held line waits for a person whatever the
assistant's settings say, and a line into the assistant's own blind draft runs.

**A fleet mirror** (a hot backup copying its primary's edits) applies them through `EngineCore.MirrorEditAsync`: one undo step past
the armed show's implicit mapping and layout locks (the primary's gate already decided the edit), never past the show's own locks
or a waiting blind commit, and never reachable from a client's line.

**An assistant's armed line** is queued on this node's own proposal list (it never runs on the assistant's word) and answered
"SET waits for a person while the show is armed … — queued for a person as proposal ap3". When a person approves it, it runs as
**approved**, which gets what a remote gets and never more: an approved mapping edit is still refused while armed.

## Locks

`LOCK <id> [<why>]` locks an entity; `LOCK AREA mapping|layout|content|playlists|media|devices|network [<why>]` an area. A lock is
kept in the project (it travels with the show) but is **not an undo step**, and no undo or redo takes it back. One guard checks
every edit, undo and redo before it is published (`ProjectStore.Guard`), so no verb, binding, assistant or undo changes what a lock
holds: "Wall (surface) is locked by Desk ('show night', 20:12) — UNLOCK sf1a2b3c4d first". The areas:

| Area | What it holds |
|---|---|
| mapping | the stage (meshes, projectors, cameras, viewpoints, scans, stacks, calibration) and where each surface is (mesh, faces, transform, masks, visibility, pixel quad); each output's warp, masks, blend, projector, measured correction and the calibration it uses — everything a surface or an output holds that no other area names (the cautious side) |
| layout | each output's display, raster, refresh, mode, plan, rotation, rectangle, latency and whether it is on; outputs added or removed |
| content | canvases, generators, sends, presets; what each surface and output shows (source, opacity, effects) |
| playlists | every playlist and item |
| media | the media library |
| devices | the projector links |
| network | the link verbs (`LINK …`, `SYNC …`), held by the gate since links are not in the document |

An armed show adds `mapping` and `layout` (shown in `LOCKS` as `implicit`). A surface's or projector's own `Locked` flag (the
desk's gizmo lock) counts as an entity lock. `UNLOCK ALL` lifts every lock.

## Panic and the master

`PANIC` takes each output to its planned outcome (`PANIC SET <output>|ALL <outcome> [FADE <ms>] [SAFE <source>]`; an output with
no plan uses the show's default, black over one second):

| Outcome | The picture | The transports | `PANIC CLEAR` |
|---|---|---|---|
| BLACK | fades to black, holds | the playlists it shows pause at the fade's end | fades up; transports stay paused |
| RUN | fades to black, holds | keep running | fades up into the show as it is now |
| REWIND | fades to black, holds | at the fade's end the item that was on is cued at its in-point | fades up on its first frame |
| NEXT | fades to black, holds | at the fade's end the next item is cued | fades up on its first frame |
| FREEZE | holds the frame at once | keep running | lets go at once |
| SAFE | dips to black over half the fade, shows the planned look, up over the other half | keep running | dips back to the show |
| SHUTTER | stays | keep running; every projector linked to the output closes its shutter | opens them |
| FX-OFF | stays | keep running; `FX BYPASS ALL ON` | `FX BYPASS ALL OFF` |
| HARD | black at once | pause at once | as BLACK |

When outputs showing one playlist disagree, the most conservative transport outcome wins (HARD, BLACK, REWIND, NEXT, RUN).

**A panic's transport outcome holds whatever drives the playlist.** A step that moves a playlist's transport (BLACK, HARD, REWIND,
NEXT) first disengages the playlist's timecode chase, at the step's instant, then runs: the code rolls on and the playlist stays
where the panic put it. It is journalled (`panic black: Show stops chasing FOH: …`), the receipt's `released` names it (`done:false`
while it waits for the fade's end), and `STATE.timecode` shows the chase `disengaged by PANIC BLACK — TIMECODE ENGAGE Show ON to
follow the code again`. `PANIC CLEAR` brings the air back, not the chase; `TIMECODE ENGAGE … ON` does. RUN, FREEZE, SAFE, SHUTTER and
FX-OFF leave transports — and chases — alone. The panic asks through one hook (`ReleaseForPanic`, declared beside the panic and
answered by the timecode package), so anything else that comes to drive a playlist answers there too. A step the playlist still
refuses is never dropped: it is journalled with the refusal's words (`ok:false`), and named in the PANIC's words when the step runs
inside it (HARD, the second press).
`PANIC <outcome>` overrides every plan; `ON <output>…` names outputs; `FADE <ms>` sets the fade; `AT +<ms>|<unix ms>` lands the
panic at an instant on the show clock (its transport outcome at that fade's end, its shutter and effects lines sent when the
instant comes); `SHOW SET PANIC-LEAD <ms>` lands every panic that far ahead so a group fades on the same frames: a group's
leader sends its lead in the panic's `AT` and a member adds none of its own, so every member lands on the leader's instant.

**The second press** while any fade of the first is running is the hard stop: every panicked output black at once, every transport
it shows paused, `hard:true`. A press after every fade has settled changes nothing and says so.

**Pure functions of show time.** Every level is an `AirFade` of the leader instant — a dropped frame heals and every node agrees.
The render thread reads `AirLevels`, built on the lane once per change and allocation-free per frame: each output's gain is master
× panic level × degrade gain (`IOutputGains`), multiplied last in the output stage (`uAir`) so 0 is true black; the SAFE look rides
the pattern-override path; FREEZE holds the output's picture. The air's sound gain (`IAirLevels.AudioGain`) is master × the darkest
panic, from the same fades, so sound fades on the same frames as picture (a SAFE look has no show sound).

`MASTER <0-100> [FADE <ms>]` is the grand master over every output, picture and sound, faded on the show clock; it is runtime, not
an undo step.

SHUTTER and FX-OFF send their lines as the node itself (`system`), through the gate like any other line, and keep each line's stage
(`sent`, `scheduled`, `applied`, `refused: …`) in `STATE.air.panic.devices`; the projector's own report is the fact.

## Blind edit

`BLIND ON` gives the calling session a **draft**: a second store seeded with the live root (sharing every branch, so it costs
nothing). The show's gate sends that session's document edits there — lines on the lane through the line's store, off-lane verbs
through `EditAsync` — while the air, STATE's project and `PROJECT GET` keep the live root. One draft per node, held by a session
(its door and its id); a draft needs a lasting connection, so MCP and HTTP calls cannot open one. A desk that reconnects (a new
session, the same name) takes its draft back with `BLIND ON` once the old connection is gone — while that one is still open it
keeps the draft ("Desk holds the blind draft on another connection that is still open — it commits or discards it there"); the
takeover is journalled and raised (`show.blind` with `takenFrom`). The desk may land or drop a draft another session left. A line
the gate sent to the draft lands in the draft it was decided for or nowhere: if the draft ended or began landing meanwhile it is
refused, never applied live. While a draft is open, `PROJECT OPEN` and `PROJECT NEW` are refused (the draft is of this project).
A draft lives in the engine's memory: a restart loses it, as it loses an unsaved edit.
The show's explicit locks hold on the draft as they hold live; an armed show's implicit mapping and layout locks do not (staging
mapping while armed is what the draft is for).

**Seeing it.** `VIEW <v> SHOW editor DRAFT` is the editor's view of the draft: the render host draws it with a second renderer on
its context that is handed the draft with no outputs, so its canvases and generators are the draft's and the air's renderer never
sees the draft (the stage's meshes and warps are the live ones until the commit lands). With no draft open it shows the air. The
draft is drawn after the air's frame, between frames, into a ring of the view's own, so a draft view never moves the air's present
and a fault in it stops the draft views (journalled once), never the air. A draft view takes a view id of its own: an id that has
drawn the air is refused ("live has drawn the air — a draft view takes a view id of its own (VIEW live-draft SHOW editor DRAFT)").

**`BLIND DIFF`** lists the draft against the root it started from, entity by entity (`ProjectDiff`, Core): added, changed (each
field as the `SET` path that would make it, from and to, at most twelve and a count), removed; a field set back to what it was is
no change. A list whose kept entities stand in another order is one more change, `order:<kind>` ("playlists (order): order
["A","B"] → ["B","A"]"); landing it puts the draft's order in place and keeps an entity added live since `BLIND ON` after the one
it followed. An entity the draft adds lands after the one it follows in the draft. `conflicts` names every id the draft changed
that also changed live since `BLIND ON`.

**`BLIND COMMIT [NOW|AT …|NEXT [<pl>]|ON GO [<pl>]] [FORCE]`** lands the draft as **one undo step** at a show boundary:

| Boundary | Instant on the show clock |
|---|---|
| `NOW` (default) | now + the commit lead (`SHOW SET COMMIT-LEAD`, 34 ms) |
| `AT +<ms>` / `AT <unix ms>` | that instant (at least the commit lead ahead) |
| `NEXT [<pl>]` | the playlist's next switch (the focused playlist when none is named), read again until it lands: a GO on the playlist pins it to the GO's own switch, a clip verb moves it with the lane; refused "nothing ends on Show — use ON GO or AT" |
| `ON GO [<pl>]` | the next accepted GO, NEXT, PREV, VIDEO PLAY or RESTART (on that playlist): its own start instant |

On a sync group's member, the leader's GO on a playlist of the group reaches it as that playlist's anchor: an anchor that sets it
playing on another item than the one on the air here at its instant, or from a standstill, is a GO here — it lands an `ON GO`
commit on its instant and pins a `NEXT` commit to the switch it makes, as on the leader.

The commit is published as (root, leader instant) in the engine's snapshot, so the render thread draws the new root **from the
boundary's frame on** — the same frame on every node holding it — and the lane promotes it on the tick that reaches it. A step of
the show clock (SYNC.md) moves a waiting commit's instant with the air, so it lands neither early nor late. Exact to the frame
means the project's own fields (content, generators, canvases, outputs); the stage's tables (meshes, warps, masks, blends, lenses)
are built for the live root and follow when the lane makes the commit the document, a few ticks later. A live
change to an entity the draft also changed refuses the commit, naming it ("changed on the air since BLIND ON: Main canvas (canvas,
cv_main) — … FORCE lands the draft's version"); `FORCE` lands the draft's whole version of each such entity. While a commit waits,
the draft takes no more edits, a live edit to an entity it lands is refused ("a blind commit lands Main canvas (canvas) at
21:04:05.200 — wait for it, or BLIND DISCARD"), and any other live edit is carried into the root it publishes. A lock set after
the commit was scheduled refuses it when it lands: the draft stays, nothing staged is lost; a `LOCK` that would refuse a waiting
commit is itself refused ("a blind commit lands … — LOCK it once the commit has landed, or BLIND DISCARD first"). **An armed show
lands only an operator's commit**: a commit a remote asked is not published while the show is armed, and reaching its boundary
armed it is refused and journalled, the draft kept. An assistant's `BLIND COMMIT` is always refused: a person lands an assistant's
draft ("BLIND DIFF shows it, BLIND COMMIT on the desk lands it"). `BLIND DISCARD` drops the draft and a waiting commit, and is
refused within the commit lead of its instant ("the draft is landing now … — UNDO takes it back once it has landed");
`BLIND OFF` leaves an empty draft and refuses one that holds changes.

## Sound on the air

The on-air playlist's item (the focused playlist, the one the clip verbs drive) plays through one `AudioOut` slaved to the item's
own anchor (`AnchorTimeline`), so it follows GO, pause, seek, a rate and a panic's transport outcome as the picture does. Its gain
is the air's sound gain — master × the darkest panic level, from the same `AirFade`s as the picture — × the playlist's volume,
read at each service step's leader instant and applied from the next step (≤ one 10 ms block behind the picture). A decoder opens
on a worker and is handed over on the lane's tick; an item that cannot sound says why in `STATE.show.sound.problem`. The exe's show
part opens the machine's output (`show.airSound`: `default`, `off` or an OpenAL device's name); a node with no device plays the
null output.

## Transitions on a manual GO

A `PLAYLIST GO`, `NEXT` or `PREV` into an item that comes in with a fade or a dip keeps the item on the air through the incoming
item's window, which opens at the GO's own instant (its `AT`, else now): the outgoing item stays the lane's current one, the
incoming one is its next with the switch at the window's end, and the source provider mixes both in linear light exactly as
between scheduled items. Inside the window the incoming item is the one on the air: a further GO steps and mixes from it, and a
clip verb (`VIDEO PAUSE`, `SEEK`, …) lands on it, keeping the GO. An item that ends inside the window holds its last frame until
the switch. A playlist in a sync group cuts at the GO: its followers adopt the leader's item and anchor, which carry no window yet,
so the group cuts together.

## The GO fence

`SHOW SET MINGO <ms> [ARMED|REHEARSAL]` (300 ms armed, 0 in rehearsal by default): a `PLAYLIST GO|NEXT|PREV` or `VIDEO
PLAY|RESTART` on a playlist within that window of its last accepted GO (the lane's leader time) is refused "double GO protection:
0.20 s since the last GO on Show (0.30 s armed)". `EXPECT <item>|#<n>` on the same verbs refuses when the item that would play is
not the one the caller's key showed: "the next item on Show is now 'C', not 'B' — the key was out of date".

## Preflight

`PREFLIGHT [QUICK]` answers its id at once (accepted; `PREFLIGHT QUICK WAIT` answers with the finished report) and runs every check side by side off the lane, each under its own budget on
the node's monotonic clock — a check past its budget is a grey row "did not finish in 5 s" and the report is not complete. The
report (`PREFLIGHT REPORT [<id>]`) has a row per thing checked, each with its light, value, note and, where a line fixes it, that
line; the last twenty are kept, and written beside the journal as `preflight/<id>.json`.

The node's own checks:

| Section | Row | Light | Fix |
|---|---|---|---|
| outputs | each enabled display output: its display present (or a free one when it names none) | red when absent | `SET <op> displayKey "<present>"`, or `OUTPUT ENABLE <op> OFF` |
| media | each file the show uses (the panic plans' safe looks included): present, readable, verified | red missing or unreadable, amber unverified | `MEDIA FETCH <md>` |
| devices | each projector link: answers, on, shutter open | red unreachable or in standby, amber shutter closed | `DEVICE POWER <dv> ON`, `DEVICE SHUTTER <dv> OPEN` |
| disk | free space on the media volume | red under twice the largest file, amber under 10 GB | — |
| safety | armed; every SAFE plan's look in the show; the default plan; double-GO protection on; no panic in force; no plan for an output the show lost; no blind draft left open | grey in rehearsal (amber with EXPECT-ARMED), red a missing look or a panic in force, amber MinGo off, a stray plan or an open draft | `SHOW ARM`, `PANIC SET <op> BLACK`, `SHOW SET MINGO 300`, `PANIC CLEAR` |
| mapping | each projector an enabled output shows, through the calibration ledger: its grade and a fallback | red not solved or failed, amber C or no fallback | `PROJECTOR SOLVE <pj>`, `CALIBRATION FALLBACK <pj>`, `CALIBRATION ACCEPT <pj>` |
| clock | the show clock: role and, following, the uncertainty of the leader's time (on a follower with the sync part, its own `sync` rows judge it: one light per report) | green < 1 ms, amber < 4 ms, red beyond; amber when a playlist names a sync group this node leads or follows none | `SYNC FOLLOW <group> <leader>`, `SYNC LEAD <group>` |
| links | Patterns and Stagewatch: heard and paired | grey not linked, red linked but not heard, amber heard but not paired (its token is a secret: no line) | `LINK PATTERNS <host>`, `LINK STAGEWATCH <host>` |

Every package adds its own checks (`EngineServices.PreflightChecks`). Every row of this node's checks names this node. When the
calibration ledger's own check runs (its `calibration` rows, one per projector) the mapping check yields to it, so a projector
never has two verdicts in one report; show-ready's mapping step reads either.

**The group.** `SHOW ARM ALL`, `SHOW DISARM ALL` and `PREFLIGHT ALL` go to every member of the group this node leads through the
fleet's fan-out (`IFleetFanout`, wp2-sync): each member runs the line through its own gate as a peer. ARM and DISARM go out once,
at the leader instant this node's own half ran, and answer with this node and every member's receipt (applied with its leader
instant, refused with its words, silent); a node already in the asked
mode still brings the group to it. `SHOW DISARM ALL` is an operator's (the desk's) only: a remote that may disarm this node may not
disarm the group. `PREFLIGHT ALL` asks each member `PREFLIGHT QUICK WAIT` and merges the rows of the report each member's receipt
carries under that member's name, a member that does not answer being a red `fleet` row; a member's fix runs on the member, so it
is in the row's note (" — on FOH-B: SHOW SET MINGO 300") and never a line this node's report would run here. Every report lists
one section per node (`nodes`). The journal keeps one row per receipt, never the report it carries. Refused "no fleet fan-out on
this node" while the node fans out to none.

**Show-ready** counts five steps — outputs, media, mapping,
sync and backup (sync, clock, fleet, links), safety — a step done when none of its rows is red or amber and every check of it finished (a check past its
budget leaves its step open: what was not checked is not known to be ready); a step with no rows does not apply and counts as done.
`STATE.show.preflight` carries the sums and "4/5"; the context's `preflight` item its light and words.

## Threads

| Path | Thread | Cost |
|---|---|---|
| The gate (`ShowPolicy.Decide`) | the lane, or an off-lane verb's caller (published state only) | allocation-free when it allows |
| The lock guard | inside the store's edit | reference compares of the areas a lock holds |
| `AirLevels` | built on the lane per change; read on the render thread | one dictionary lookup per output per frame |
| Preflight | the pool, side by side | each check under its budget |
| The blind draft's diff | the lane, on each draft edit | JSON of the changed entities only |
| A commit's root | the lane, when the live root moves while it waits | one patch application |
| The pending root's choice | the render thread | one compare per frame |
| The draft view | the render thread, while one is shown | a second renderer's frame with no outputs, an editor pass ≤ 30 a second, one copy |
| The air's sound | the output's own thread (5 ms) | one gain read per service step |
