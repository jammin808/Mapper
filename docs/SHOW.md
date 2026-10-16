# Show Mode, locks, panic and preflight

What keeps a running show safe from the people and the machines that drive it: one switch that turns a rehearsal into a show
(`SHOW ARM`), locks on what is finished, a panic that takes the air down on the show clock and brings it back, a grand master, a
fence against the double GO, and a preflight that says what is not ready with the line that fixes it. The verbs, replies and STATE
are in docs/wire/show.md.

## Show Mode

Rehearsal is today's node: every paired caller may change anything. `SHOW ARM [<why>]` arms the show, with no password: from then
on the table below decides every line, after pairing and before its handler, for every caller — the desk, Patterns, Companion,
OSC and MIDI bindings, the wire, MCP and the assistant. `SHOW DISARM` goes back to rehearsal. Neither is an undo step.

**Armed is the safe side**, so it survives a restart: the recovery record keeps who armed it, when and why, and a node that comes
back comes back armed (a panic in force comes back too, at its outcome at once).

**Roles** come from the session's transport, never from words a client sends: the desk is the **operator**; any other paired
client (wire, HTTP, WebSocket, Companion, OSC, bindings, Patterns, Stagewatch) is a **remote** — an **operator** when its HELLO
name or address is listed in the node's settings (`show.operators`); MCP and the assistant are the **assistant**; a person's
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
| Show: DISARM, UNLOCK | A | A when DISARM-FROM PAIRED, else R | P | R | A | A |
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
instant comes); `SHOW SET PANIC-LEAD <ms>` lands every panic that far ahead so a group fades on the same frames.

**The second press** while any fade of the first is running is the hard stop: every panicked output black at once, every transport
it shows paused, `hard:true`. A press after every fade has settled changes nothing and says so.

**Pure functions of show time.** Every level is an `AirFade` of the leader instant — a dropped frame heals and every node agrees.
The render thread reads `AirLevels`, built on the lane once per change and allocation-free per frame: each output's gain is master
× panic level × degrade gain (`IOutputGains`), multiplied last in the output stage (`uAir`) so 0 is true black; the SAFE look rides
the pattern-override path; FREEZE holds the output's picture. The air's sound gain (`IAirLevels.AudioGain`) is master × the darkest
panic, from the same fades, so sound fades on the same frames as picture (a SAFE look has no show sound). STATE shows each level
as it stands when read: it moves when a fade starts and once more when every fade has settled, and never on its own while nothing
fades (a node just started included, its degrade gains on the air from the start).

`MASTER <0-100> [FADE <ms>]` is the grand master over every output, picture and sound, faded on the show clock; it is runtime, not
an undo step.

SHUTTER and FX-OFF send their lines as the node itself (`system`), through the gate like any other line, and keep each line's stage
(`sent`, `scheduled`, `applied`, `refused: …`) in `STATE.air.panic.devices`; the projector's own report is the fact.

## The GO fence

`SHOW SET MINGO <ms> [ARMED|REHEARSAL]` (300 ms armed, 0 in rehearsal by default): a `PLAYLIST GO|NEXT|PREV` or `VIDEO
PLAY|RESTART` on a playlist within that window of its last accepted GO (the lane's leader time) is refused "double GO protection:
0.20 s since the last GO on Show (0.30 s armed)". `EXPECT <item>|#<n>` on the same verbs refuses when the item that would play is
not the one the caller's key showed: "the next item on Show is now 'C', not 'B' — the key was out of date".

## Preflight

`PREFLIGHT [QUICK]` answers its id at once (accepted) and runs every check side by side off the lane, each under its own budget on
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
| safety | armed; every SAFE plan's look in the show; the default plan | grey in rehearsal (amber with EXPECT-ARMED), red a missing look | `SHOW ARM`, `PANIC SET <op> BLACK` |

Every package adds its own checks (`EngineServices.PreflightChecks`). **Show-ready** counts five steps — outputs, media, mapping,
sync and backup, safety — a step done when none of its rows is red or amber and every check of it finished (a check past its
budget leaves its step open: what was not checked is not known to be ready); a step with no rows does not apply and counts as done.
`STATE.show.preflight` carries the sums and "4/5"; the context's `preflight` item its light and words.

## Threads

| Path | Thread | Cost |
|---|---|---|
| The gate (`ShowPolicy.Decide`) | the lane, or an off-lane verb's caller (published state only) | allocation-free when it allows |
| The lock guard | inside the store's edit | reference compares of the areas a lock holds |
| `AirLevels` | built on the lane per change; read on the render thread | one dictionary lookup per output per frame |
| Preflight | the pool, side by side | each check under its budget |
