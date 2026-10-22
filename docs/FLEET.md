# The fleet: health, the board, PlayMap on Patterns' Eye, and the hot backup

Every PlayMap machine knows how it is (HEALTH), watches every other PlayMap machine it hears (the fleet monitor), and draws them all
as one board in the shape of Patterns' God's Eye (FLEET / EYE). Its context is ordered so that Patterns' Eye, which draws twelve
things of each app, draws the ones that matter. Lights are evidence: what was not measured is grey, never green. A machine can stand
by as a **hot backup** of another, rendering the same frames, and take the show over by epoch; a follower can take the lead of its
sync group when its leader dies. The wire's verbs, replies and STATE are in [wire/fleet.md](wire/fleet.md) and
[wire/backup.md](wire/backup.md).

## Health

`HealthRecord` (Core, pure) is one machine at one instant: its outputs (open, fps, dropped, missed vblanks, present path), its
projectors (with the device's power, shutter and input folded in), the renderer and its resets, CPU, this process's memory and the
machine's free memory, the media disk's free space, the GPU's load and temperature, network traffic, uptime, the clock sync's role
and uncertainty, the devices by light, the media files and how many are missing, and the watchdog: the control lane's widest tick
gap and the render's p95 frame time. Every number not measured is −1. It names the build the node runs (`build`, SemVer with
CI's build number and the commit). It has places for each output's frames shown (by the present ledger's count) and frames observed
off the air by an independent reader, and for the GPU device's epoch and phase (`starting`, `running`, `rebuilding`); each reads −1
(the phase `unknown`) until round 3's stability work fills it (docs/round3/PLAN.md §3.9). The direction (docs/round3/PLAN.md D6):
a supervising service, `PlayMap.Supervisor`, starts the engine in the show user's session and restarts it, and stops an engine whose
shown frames stop while its GPU phase is `running` — never during a start or a device rebuild, and never while the count is −1. The
engine never runs as administrator or in session 0.

Eleven categories, always present, in this order — outputs, render, cpu, memory, disk, gpu, network, sync, devices, media, watchdog
— each with a light and words. The light table (`HealthLights`):

| Category | Amber | Red | Grey (unknown) |
|---|---|---|---|
| outputs | an output dropped frames or missed a vblank in the last minute | an output is not open | no outputs on, or not measured yet |
| render | the renderer reset | — | no renderer |
| cpu | ≥ 80 % | ≥ 95 % | not sampled |
| memory | under 1.5 GB free | under 512 MB free | not sampled |
| disk | under 10 GB free | under 2 GB free | not sampled |
| gpu | ≥ 90 % or ≥ 80 °C | ≥ 98 % or ≥ 90 °C | not readable on this machine (with the reason) |
| network | ≥ 900 Mbps | — | not measured |
| sync | a follower ± 2 ms, or free-wheeling | a follower ± 10 ms | not in a sync group |
| devices | a projector amber | a projector red | none linked |
| media | files missing | — | no media |
| watchdog | lane gap ≥ 60 ms or frame p95 ≥ 40 ms | lane gap ≥ 250 ms or frame p95 ≥ 100 ms | not timed |

The machine's light is the worst known category (red over amber over green; grey says nothing). Its headline is facts with no number
that moves while all is well: "FOH-A · primary · all green · 2 unknown", "FOH-B · backup · 1 amber: outputs — Left: 4 dropped, 1
missed in a minute".

The numbers come from the **machine sampler**, on its own one-second timer (never the control lane, never the render thread; a
tick that finds the last reading still being read takes none, so a reading that stalls holds one pool thread, never one a second):
Linux reads `/proc/stat`, `/proc/meminfo`, `/proc/net/dev`, `/proc/uptime`, the home volume's free space, and the GPU from
`/sys/class/drm/card*/device` (`gpu_busy_percent`, a hwmon `temp1_input`) where the driver gives it; Windows reads system times,
`GlobalMemoryStatusEx`, `GetTickCount64`, the interfaces' bytes and the PDH counter `\GPU Engine(*engtype_3D)\Utilization Percentage`
(compile-only here; its test runs on the Windows CI job). NVIDIA's numbers on a Linux development machine and every GPU temperature
on Windows need NVML, which PlayMap does not load and round 3 does not plan: on Linux the GPU reads grey with that reason, and on
Windows the light is the load's alone, the temperature −1. The lane's gap is measured on the engine's clock from the lane's second
second on (its first is the node starting). A node without the fleet's part (a bare engine) reads every machine number −1.

HEALTH is pushed (`SUBSCRIBE HEALTH`, the `/health` WebSocket) at once, once a second, and at once when any light moves — a clock
that runs only while someone watches. The `/health` WebSocket feeds at most 64 sockets at once (a monitor holds one per peer); one more
is refused with 503 and words, and a GET still reads the record; a socket a page from another site opens is refused.

The fleet's part also watches this machine's own lights once a second on the fleet's clock: when a category's, an output's or the
machine's light moves, the engine's facts move, so STATE (`fleet`, `eye`), the context's `machine` item and HEALTH's watchers say it
within the second with nothing else moving. Numbers that wander inside one light move nothing. Both clocks stop with the node.

## The fleet monitor

For every live PlayMap peer the hello hears (at most 32 subscriptions at once; a peer that comes back waits for a place like a new
one), the monitor opens the peer's `/health` WebSocket: `wss://` when the hello
offers TLS, the certificate checked against the pin (and the hello's fingerprint) and pinned on the first record that is the peer's
own; `ws://` when it offers none. A peer appears on the board when its first record arrives; its liveness is the silence since its
last record on the monitor's clock (an injected `TimeProvider`): stale at 3 s, gone at 6 s. A gone machine stays on the board, red
"gone quiet 12 s", until it is heard again — never removed silently. A subscription that ends is dialled again after 0.5 s, doubling
to 8 s while nothing is heard. A changed certificate is refused and shown red with words until the operator decides (the pins are
`keys/fleet-pins.json` beside the node's own): removing the machine's line pins its new certificate on its next record — the file
is read again whenever it changes, with no restart, and a pin set afterwards never writes the removed line back. The monitor keeps at most 64 machines (twice its subscriptions, at least 64): when it is
full, a machine it dialled and never heard (on no board) is let go to make room; a machine the board shows is never let go. The board changes (and STATE moves) only when what it shows moved — a light, a
headline, a liveness — or a peer's place in a hot-backup pair or a sync group (its epoch, its mirror's state, its group), never for a
record that says the same.

## The board

`FleetGraph` (Core, pure) is Patterns' Eye method applied to the fleet: a `machine:<node>` per machine, an `output:<node>:<id>` per
output it shows, a `projector:<node>:<id>` per projector an output drives; backups **mirror** their primary, followers **follow**
their leader. A machine's things go grey with "not heard for 7 s" when it is not live. One row per machine in (role, name) order,
columns in signal order; the same facts give the same ids, order and places. The headline ("3 machines · 4 outputs · 1 red · 2 amber
— FOH-C: gone quiet 7 s"), the counts and the problems queue (reds, then ambers; machines before their things) are computed once.
Lenses: ALL, OUTPUTS, PROJECTORS, SYNC, BACKUP, PROBLEMS (the problems and what they touch); the focus dims things by hops (1, 0.55,
0.25). `FLEET` answers it in Patterns' `EyeJson` field names, so a reader written for Patterns' Eye reads PlayMap's.

## PlayMap on Patterns' Eye

Patterns draws another app's context as a card with up to twelve of its things — the reds, the ambers, then in the order given.
PlayMap gives them in this order: `show`, `machine`, the outputs (beyond six, the green ones as one `outputs` item), the projectors
with their devices folded in, the playlists that play (idle ones as one `playlists` item), mirrors, sync, media, preflight, links,
the rest, faults; what an aggregate stands for follows, still there for an assistant. Its links are only the Eye's eleven verbs.

Patterns draws PlayMap as a grey app heard on the network. Three changes on Patterns' side make PlayMap's things appear lit
(prepared as a patch with tests against Patterns' `ShowBusView` and `EyeGraph`, built and run in a scratch copy of Patterns, not
applied — Patterns' maintainers take it or not): **P1** its kind reads "PlayMap"; **P2** the context of every live peer
whose hello offers the `context` cap is read (a PlayMap peer's over its line wire, `CONTEXT` being an open read there, since
`/api/context` is behind the node's token) and drawn with the peer; **P3** another app's outputs, projectors, surfaces, playlists and
media sit on the Video band. PlayMap's own `PatternsEyeContractTests` read its context with a copy of Patterns' `ShowContext.Parse`
and the show-app section of `EyeGraph.Build` and check its things are drawn lit with its own lights.

Patterns' Eye keeps the first 64 of a context's commands today, so PlayMap lists the panic, air, transport and Show Mode rows first;
the request to Patterns for a cap of at least 256 is [SHOWBUS-NODE.md](SHOWBUS-NODE.md) §5 item 7. PlayMap's own context reader
(`Core/Bus/ShowContext.Parse`, which reads Patterns' context over `LINK PATTERNS`) also keeps 64 commands today; the direction is no
count cap there, since its 512 KB bound already bounds what it holds (docs/round3/PLAN.md §5.6 item 7).

## The hot backup

A frame is a pure function of the show, its playlists' anchors, the air and the leader instant. A backup takes each of those from its
primary, each with the leader instant it takes effect, and follows the primary's clock, so it draws the same frame for the same
instant — to the byte (`BackupRenderTests`: two render hosts on llvmpipe, playing a clip's HAP Q version, under a blackout and through a
panic fade). The output stage's dither is seeded by the show frame, so it does not break that.

**Joining.** `FLEET BACKUP <primary> [HOT]` on the machine that stands by. The primary must lead a sync group (its clock is the
backup's) and hear its peers' HEALTH (its fleet monitor on: what it hears there is its fence); the backup proves the primary's
pairing token, kept in its credential store as `node.<primary id>` (`CREDENTIAL SET node.0badc0de <token>`), and subscribes to the
primary's **mirror** (`SUBSCRIBE MIRROR <node>`, paired only):

- the show: the primary announces each revision; the backup pulls the show from `/mirror/project` and applies it as one mirror edit
  (past the implicit locks an armed show adds; the primary's own gate already decided the edit);
- the files: every media file and every optimised version the show names that the backup does not hold, pulled from
  `/mirror/media/<sha256>` by hash, in ranges, verified before they land — so the backup plays the version the primary plays;
- the files the show names beside its folder — a mesh read from a file (a survey's or a scan's), each output's measured warp and
  blend map, a survey's thinned cloud — pulled by key from `/mirror/file/<kind>/<id>` into the backup's folder for them (its own
  project's, else `mirror/<primary id>` in the node's home), each checked against the hash the show names, before the show that
  names them is applied: the backup's stage loads the primary's mesh from its first frame, and a file that lands later is read at
  once. A file the primary refuses is refused here by the answer's status alone, its body never read ("FOH-A answered 404"); a file
  whose body sends nothing for 10 s on the node's clock (the pulls' quiet) is not kept, its refused row saying so ("sent nothing for
  10 s, 65536 of 300000 bytes in"); the watch asks for either again, within its own turn (an open fix, docs/ROADMAP.md);
- the clock, the group's anchors and the group's air: the backup follows the primary's sync group (`SYNC FOLLOW`, docs/SYNC.md);
- every other playlist's anchor, and the air lines the group's feed does not carry (`FREEZE`, `PATTERN`, a panic or blackout `ON`
  outputs or `HERE`), through the backup's own `MIRROR` verb (session "fleet"), each at the primary's leader instant.

A live input (NDI, OMT, a stream) is received from the network, never fetched (`MEDIA FETCH` says so; the frame it draws is the
mirror's one non-deterministic part). A blind draft on the primary never reaches the backup; its landing is a revision of the show
like any edit. A playlist that chases timecode chases on the primary only: the backup's chase moves nothing, and the primary's chase
moves the anchors the mirror brings.

Every pull carries the **pairing proof** — the HMAC of the token over a fresh nonce, the scheme and the request
(`X-PlayMap-Nonce`, `X-PlayMap-Proof`) — never the token itself; the primary takes each nonce once, within a minute of its own clock, and a
proof made for HTTPS is refused over plain HTTP, and a request with a proof that names an Origin (a web page's) is refused whatever it
carries. From another machine a pull goes over HTTPS only (the backup refuses to send one in clear,
and refuses a hello that drops a certificate it has pinned), checked against the fingerprint the primary's hello advertises and pinned
in the fleet's pin file after the first proved answer; a changed certificate is refused. `MEDIA FETCH <media> VERSION <version> [FROM <node>]` pulls one version the same way on any node.
With the fleet's part, which the exe composes, `MEDIA FETCH <media>|ALL [FROM <node>]` pulls each missing file the same way, by its
hash, from the node named or else the first heard that holds it (the primary this node backs up first). These pulls say each file's
end in the journal, at most 4 rows a fetch and one that counts the rest, and write no `media.transfers` row (docs/WIRE.md); a body
that falls silent for 10 s on the node's clock fails that try.

The backup is **identical** when the primary's show is here at its revision, every file it names is here, the backup's clock reads
the primary's time line and the group's feed is live; `joining`, `resyncing` (a pull under way), `different` (a file could not be
pulled) and `lost` (the mirror is down or not subscribed, or its primary has said nothing on it for over 6 s; the last show and air
hold) say otherwise. The mirror's link counts the primary's silence on the backup's own clock, as its pulls count a body's: a
subscription lives as long as its connection, so a mirror quiet past 6 s is followed again with the primary's next line on that
connection, and one quiet past 12 s is dropped and dialled again, and the new connection subscribes anew. The backup keeps the
highest epoch its primary is heard to hold (the mirror's hello, its HEALTH) in its settings, so a takeover after the backup
restarted, its primary silent by then, still commits above it. A client's edit on a backup is refused with the way out ("FOH-B
mirrors FOH-A — send it to FOH-A (10.0.0.21), or FAILOVER to make this node the show", the words STATE.fleet.door carries, so the
desk greys what the node would refuse before the press); queries, panic, Show Mode, the node's own administration, a fetch of a
file by hash and the fleet's verbs stay; no blind draft is begun or landed on a backup, and `FLEET BACKUP` waits while the node
holds one. A pull that fails is asked again from the watch while the primary's revision is ahead or a file the show names is missing. A
follower whose clock asks a port its leader no longer answers on (the leader's hello names another since it led again) follows
afresh. A node that has begun to stop starts no pull, no join and no takeover step, and its mirror and pulls end with it.

**What goes out beside the outputs.** An LED rig's nodes and universes are shared by every machine that drives them, a send is
received by its name, and the sound reaches the room's one system — none of them switched by a projector's input — so only the node
that has the show puts them out. A hot backup's LED sender stops with no black frame (the primary's frames are the rig's), its sends
are not made, though the mirrored show has both, and its sound is silent — STATE says `standby` for each LED node and send, green,
with why, and `show.sound.gain` 0; `FLEET BACKUP` turns a node silent before the primary's show lands. FAILOVER makes the new
primary send from its next frame, on the show clock's grid, and sound from its next service step. A fenced node's outputs are held
black; its LED sends one black frame (sACN terminates its universes) and stops, its sends go dark with one black frame and close
(`held`), and its sound is silent; `FAILBACK` swaps the two nodes again (`BackupNetworkAirTests`). A primary that starts with hot
backups (a restart, a crash) keeps all three silent until it has heard each backup, none saying it took the show, or its own clock
has given them six seconds (when the board calls a machine gone): a backup that took the show while it was away fences it first, so
it never sent at all; its outputs open as before (`BackupRestartTests`). With an LED strip and a send configured the backup's
picture stays the primary's to the byte: both read the output's final pixels, and the dither is the show frame's.

**Taking over.** `FAILOVER` on the backup:

1. commits the next **epoch** (one past every epoch it holds or hears) and keeps it, with the role, in the node's settings before it
   serves (`NetworkHost.PersistSettings`, the settings file's one writer; the file reaches the disk before it is renamed into place
   and the rename before the call returns — `DurableFile`, as for every file that holds state for the node's next start — so a
   power cut just after the takeover keeps the epoch);
2. freezes its clock's mapping into its own time line (`NetworkClock.Promote`) and leads the group: show time continues with no step;
3. switches every **route** (`FLEET ROUTE <output> DEVICE <device> <input> <backup input>`, kept in the show) through its own
   `DEVICE INPUT`, each with the projector's receipt — observed when the projector reads its new input back; a projector that does not
   answer within 2 s (on the fleet's clock, as `FLEET ROUTE TEST`'s reads are) is said ("switch it by hand") and the takeover goes on;
4. journals the trail ("takeover tk… of FOH-A by FOH-B (pressed) — epoch 1 committed → leading show on the same time line → P1 32
   observed → serving → complete in 40 ms"), raises `fleet.takeover`, and serves: editing goes on.

A takeover is refused on a node that has begun to stop (it would switch the projectors to itself and go), and while another node is
heard serving the primary's show — a primary's other backup took it ("FOH-B took FOH-A's show already (epoch 1) — FLEET BACKUP FOH-B
to mirror it"): two primaries of one show never come of two presses.

The old primary hears the new epoch in the backup's HEALTH (role primary, taken from it) and **fences itself** — only from a node
that joined its mirror (proved by its token; the node keeps that list) and is heard over its pinned certificate, so no other
machine's word can black it out (`FLEET FENCE`, node to node): its outputs are held black from the next frame (a gain of 0
multiplied into the air, composed over any other degrade gain — a stack's MATCH among them — so nothing lights a fenced node), its clients' edits are refused, its clock follows the new primary, the role is kept across a restart, and
it never takes the show back by itself. `FLEET BACKUP <new primary>` there makes it the new primary's backup; `FAILBACK CONFIRM` on
the new primary then hands the show back (the original takes it under the next epoch, its routes switch to its own input, and only
while its own mirror is identical — else it refuses, journalled, and the new primary serves on) and the two swap roles: the node
that handed the show back joins the original's mirror once the original's HEALTH says it leads its group. `FLEET SOLO` ends a
backup's or a fence's role.

**Controller death.** A sync group's leader that dies leaves its followers free-wheeling on their last estimate: their playlists switch
items at the instants they computed, and the context says "leader gone — FLEET PROMOTE on the node that should lead". `FLEET PROMOTE`
on a follower commits the next epoch, promotes its clock and leads the group; a follower whose leader is silent hears the new leader's
epoch and group in its HEALTH and follows it, its leader time never running backwards and continuous within 1 ms against the running
clock (`PromoteTests`); a leader that comes back and hears a member of its group lead under a higher epoch steps down and follows it.
Only a member of the group the node has known, heard over its pinned certificate, is followed this way. On a hot backup whose primary
went silent the context says "primary gone — FAILOVER on this backup to take its show". Air lines of the primary still waiting for their
instants when FAILOVER is pressed land as planned. Automatic takeover on silence and
a succession list are not built: a takeover and a promotion are a person's press.

## Preflight and INDEX

Preflight's `fleet` section: every expected machine heard (known before a restart, in the show's fleet plan, or heard now), every
pin valid. Its `backup` section: on a backup, its mirror identical and every file and optimised version the show names here (the
line that fetches a missing one), and every file beside the show (named; the mirror asks for it again itself); on a primary, each
backup identical; on a node that holds the show, every route reachable (`FLEET ROUTE TEST`); a fenced node red with `FLEET BACKUP
<new primary>`. A backup's lost mirror carries `FLEET BACKUP <primary>` while no subscription stands (its connection ended, or a
connection, live or quiet, whose subscription is not taken), and no line while its link is only quiet with its subscription
standing: the verb refuses it, and the primary's next line follows the mirror again (or, past 12 s, the link dials again); the row's
state, words and line are read from one look at the link. INDEX's `eye` section: NEXT and PREV PROBLEM, the PROBLEMS and ALL lenses,
RESET, and a key per machine with its light; its `fleet` section: FAILOVER on a backup lit by its mirror, FAILBACK CONFIRM on a node
that took a show over, FLEET PROMOTE on a follower (red while its leader is silent), and a key per backup of a primary. A mirror is
lit alike wherever it is shown, on the backup or on its primary (STATE, the context, INDEX, preflight): green identical, red lost,
amber otherwise.

## Threads

The node's settings file has one writer, the network host (`NetworkHost.PersistSettings`, and `KeepSettings` for a change that is
refused when it cannot be written): the sync role, the fleet's role and epoch, the links and the pairing token, the LED, control,
video and audio settings, the present path and the decode policy each go through it, read fresh from the file and written under its
one lock, so none loses another's. Only the node's start and the network host load the file (a load makes it on a first run and puts
a damaged one right); every other reader — the desk's pairing-token read, the render host's and the outputs' present settings —
reads it as it is (`NodeHome.ReadSettings`) and never writes it. A write whose rename landed but whose folder the disk did not flush
is the file's content and is kept, the change taken; the node shows a `settings-flush` fault until a later write flushes whole.

The sampler, the light watch and the HEALTH clock run on timers of their own; the backup's watch (its fence, its group's new leader,
its mirror's state) on a task of its own; the mirror's lines are taken one at a time beside the lane and run through the engine's own
verbs; the primary's air lines wait for their instant in a queue the lane's tick runs; the monitor's subscriptions on the thread pool; the board is built from
published state on whichever thread asks (FLEET, STATE, INDEX), never on the lane; the lane only stamps its tick (one lock, no
allocation).
