# Sync: one show clock, one group, measured

How PlayMap nodes share one show clock, one transport and one air, and how the leader knows how close their vblanks are. The
display side (vblank truth, the frame-lock machine and its drivers) is `docs/OUTPUT.md`; the wire's side of this is
`docs/wire/sync.md`.

## The roles, kept

A node is **solo**, **leads** a sync group, or **follows** a group's leader (`SYNC LEAD <group>`, `SYNC FOLLOW <group> <node>`).
The role, the group, the leader and the "follow Patterns' blackout" switch are kept in the node's settings (`NodeSettings.Sync`:
`role` solo|lead|follow, `group`, `leader`, `followPatternsBlackout`, `slewPpm`, `leaderTimeoutMs`) and entered again at start:

- a **lead** binds the clock-sync port at once;
- a **follow** waits until its leader is heard on the show network ("waiting to hear 0badc0de on the show network"), then
  follows it — its clock, its anchor feed and its air;
- the follow-blackout switch is on the air before the first transport opens.

The settings are written read-fresh (the file's other keys are kept). Following the same leader again is refused as a no-op
while that leader is heard and its feed is live; a leader that went quiet, or came back on another port, is followed afresh.

## The clock: slewed, stepped, held, promoted

`NetworkClock` (`src/PlayMap.Core/Time/Clocks.cs`) is the leader's time line as this node reads it — the engine's leader clock,
read lock-free by the lane and the render thread.

- **Slew.** A new estimate whose jump at now is at most **1 ms** is absorbed by a residual that decays at **200 ppm**
  (`slewPpm`): leader time stays continuous and strictly monotonic, at 1 + drift ± 200 ppm. A slewed correction moves a content
  frame's boundary by at most 1 ms (6 % of a 60 Hz frame); 200 ppm is far inside the ±0.5 % audio follows without an audible
  pitch change.
- **Step.** A larger jump (following a leader for the first time, a leader that restarted, going back to this node's own clock)
  steps. The step is taken **on the engine's control lane** and announces Δ = new − old leader time; in the same lane action the
  engine moves every playlist's anchor (and so every scheduled `AT` start), the identify deadline, the master's and the panic's
  fades and safe looks, the fades back from a panic, the panic's steps and lines still due, and a blind commit waiting for its
  instant, by +Δ: media positions and levels are continuous and nothing lands early or late. A group playlist that already took
  its leader's anchor is on the leader's time line and stays (when the step brings the clock onto that line), and so does a blind
  commit pinned to it. The render thread reads the snapshot and the clock separately, so a frame drawn in the
  microseconds between the clock's swap and the rebased snapshot can show one step; a step happens when a node starts following
  or its leader's clock jumps, not in a running show (an epoch in the snapshot would close it: a spine amendment). The journal says
  "followed 0badc0de: show clock stepped +3 h 12 min; 2 playlists kept their place"; the event `sync.stepped` carries Δ.
- **Hold.** A leader silent for `leaderTimeoutMs` (3 s): the follower free-wheels on its last estimate (drift applied). Its
  playlists keep switching their own items at the anchored instants. STATE `sync.freewheelS`, the journal ("FOH (0badc0de)
  went quiet: free-wheeling on the last estimate"), the event `sync.leader.lost`, the context's red light. The next estimate
  ends it.
- **Promote.** `NetworkClock.Promote()` freezes the mapping at now into a fixed offset: a backup that takes over continues its
  leader's time line with no jump (wp2-backup calls it), and a later `Lead()` keeps that line.
- **One line at a time.** A step waiting for the lane is replaced by a later correction, `Lead` or `Promote` (a stale step is never
  taken), and the "never backwards" floor belongs to one time line, so a reader still on the old mapping cannot hold the new line
  up. The exchange starts its estimate over when an answer's offset is further from the kept samples' best than both round trips
  and a millisecond allow (a leader that restarted on another line).
- **Pin.** `NetworkClock.Pin(leaderNs)` makes an instant "now" on one thread for synchronous work: a member runs its leader's
  air line pinned to the leader's instant, so the fades start on the same frames.

The clock-sync client keeps the time line it has until its new leader's first answer (it never steps through this node's own
clock on the way from one leader to another).

## Anchors and the group's air: the feed

A leader serves **`SUBSCRIBE ANCHORS [<node> [<output>…]]`** on its line wire. A follower's sync part dials its leader's wire
port from the hello and subscribes with its node id and output ids (so the leader's board names its outputs). The feed pushes,
in order:

- **`ANCHOR <group> <playlist> <json>`** on every transport change of a playlist in a sync group (the existing `AnchorJson`:
  index, item, state, media and leader instants, rate, in/out, loop, seq). A follower adopts each **through its own
  `PLAYLIST ANCHOR`** (session `sync`, the peer role, its own gate), for the playlist of that id in the group, or else every
  playlist of the group here when the leader has shown one playlist for the group and the group has one here (projects made apart);
  otherwise the anchor is not taken and the journal says so once. An anchor that sets the playlist playing on another item than
  the one on the air here at its instant, or from a standstill, is the leader's GO here too: a blind commit waiting for a GO, or for
  the playlist's next switch, lands on its instant as on the leader. Nothing is adopted until the follower's clock reads this
  leader's time line: lines wait for an estimate heard since the follow (or the feed's connection) began, and for any step to be
  taken.
- **`ANCHOR <group> @air {"feed","seq","line","leaderNs"}`**: the group's air. On a leader, `PANIC`, `PANIC CLEAR`, `BLACKOUT` (a toggle
  is sent as the state it left) and `MASTER` go to every member as they ran here (`HERE` keeps a line local, and so does a panic
  line that names outputs: they are this node's). A panic's `AT <unix ms>` goes as `AT +<ms>` from the line's instant, read on the
  leader's wall clock, and a panic carries the leader's `PANIC-LEAD` in its `AT` (`PANIC FADE 1000 AT +200`): a member adds no
  lead of its own, so the group fades on the leader's frames. Show's `SHOW ARM|DISARM ALL` and `PREFLIGHT ALL` use the same path
  through `IFleetFanout`, once per line, at the instant the leader's own half ran. A member runs `PANIC`, `PANIC CLEAR`,
  `BLACKOUT`, `MASTER`, `SHOW ARM`, `SHOW DISARM` and `PREFLIGHT` from its leader and refuses anything else with words. A
  `PREFLIGHT` runs beside the feed (a later air line is never held behind its checks); for `PREFLIGHT QUICK WAIT` the member's
  receipt carries its finished report, and the leader waits up to 10 s for it (a member's checks run under their own budgets). A lane
  verb runs pinned to the leader's instant: at once when that instant has come and nothing waits before it, else in arrival order
  from the lane's tick once its instant has come (the member answers `accepted` first). A `PANIC` from no later than the panic
  the member is in is that press handed again: refused as such, never a second (hard) press. Every member answers each line with `SYNC RECEIPT <seq> ACCEPTED|APPLIED|REFUSED <leader ns>
  [<words>]`; the leader journals each receipt ("PANIC FADE 1000 on Stage: applied — …") and a member silent for 2 s reads
  `silent` (one deadline for the whole group).
- **`ANCHOR <group> @lock {"apply","swapGroup","barrier","counter":{…}|null}`**: the group's frame lock. A member offers the
  leader's counter anchor to its outputs (`IFrameLockBackend.OfferAnchor`, so counts map to the same show time), joins when the
  leader applied the lock and leaves when it released it — never with its own frame lock off or once it has begun to stop, and not
  while it is armed and on air (it joins or leaves at the next blackout or once disarmed, as `SYNC APPLY` and `SYNC LEAVE` would).
  The record is taken on the member's lane, and a held one retried by the watch never acts after a newer record.

A member takes the feed's lines one at a time, in the order they came (a leader numbers and pushes each air line under one lock,
in the order its lines ran), and none before its clock reads the leader's time line; a feed that comes faster than it takes it is
dialled again. A subscriber gets the latest anchor of every playlist and the latest air line of each kind (blackout, master,
panic) first, so a member that comes back rejoins the show where it is; a member never runs the same air line twice (a leader
that restarted numbers its lines afresh under a new feed instance; a member remembers the lines it ran across follows, the
oldest forgotten first, never the latest of a kind). A subscriber that names no
node only reads (a desk watching the group) and is no member: it is never waited for.

## Vblank reports and the leader's board

Each member reports each output's vblank once a second (and at once when its lock state changes) to its leader's clock-sync port
(UDP 9942, the PMCK socket), as a 64-byte **PMVB** packet: lock state, flags, path, node id, output hash, sequence, board
count, period (ps, on the leader's time base: the measured period scaled by the estimate's drift), last vblank (leader ns),
refresh index, missed in 60 s, house sync, air latency and the sender's own clock uncertainty (half its best round trip, plus
any correction still being slewed). The responder tells them apart by their magic and refuses anything else (fail closed); a report is admitted only
from an address that asked for the time within 10 s, and the board hears it only from a member of the feed, subscribed from
that address and heard on the hello from it. The leader adds its own outputs. Reports never change the air.

**`SyncBoard`** (pure) turns them into numbers:

- the **reference** output is the leader's first, else the first heard; entries unheard for 5 s read "not heard";
- the **phase** of each output against the reference, each one's vblank nearest a common leader instant along its own period,
  wrapped into (−P/2, P/2]; its **uncertainty** is both members' clock uncertainty, so a 210 µs uncertainty is never shown as
  a 12 µs phase;
- the **next slip** at (P/2 − φ)/δ for a slower output, (φ + P/2)/|δ| for a faster one (δ the relative rate); none under
  0.01 ppm or for a locked pair. 50 ppm at 60 Hz slips every 5 min 33 s; 59.94 against 60 every 16.65 s;
- **ppm** against the nearest standard rate, the 1001 family exact (60000/1001, not 59.94);
- **mixed rate** past 500 ppm (59.94 against 60 is 1001), amber;
- the **verdict**: `same vblank` (every heard output locked, and every locked pair measured as one: phase within both
  uncertainties and 1 ms, board counts one per period apart mod 2²⁴), `free-running` (amber, naming the pair, when outputs report
  locked but measure apart; red when an output fell out of the lock; amber after 5 s of joining, or while the show plays and a slip
  is under 10 min), `mixed rate` (amber), `unknown` (nothing heard).

## STATE, context, preflight, INDEX

- **STATE `sync`** gains SPINE §9's keys: `group`, `feed` (live|connecting|lost|off), `slewing`, `steps`, `freewheelS`,
  `outputs[]` (the leader's board; a follower its own outputs), `pairs[]` (with `aNode`/`bNode`; `mixedRate` per pair),
  `verdict`, `words` (`SYNC STATUS` answers the same object). The group's light is the context item's and INDEX row's. `offsetMs` is the follower's leader-time **uncertainty** (half the best round trip),
  not the clocks' raw difference.
- **Context**: each node publishes `sync:<group>` (kind `sync`; the leader's carries the verdict, a follower's its link); the
  follower's `machine` follows it and its group playlists follow it; the leader's group follows its `machine`; a free-running
  output hears it.
- **Preflight** (section `sync`): a follower's leader heard (red with `SYNC FOLLOW <group> <leader>`), its clock uncertainty under
  ± 4 ms, its feed live (show's own `clock` row yields a follower's clock to these rows, so the clock has one light per report); a
  leader's rates (red on a mixed rate, with `SET <output> refreshHz <rate>` for its own odd output, or the member's node and the
  line to run there — output ids repeat across nodes) and its members; a solo node grey.
- **INDEX** section `sync`: the group, lit by its verdict (or the follower's link), key `SYNC STATUS`.

## Verified here, next

- **Here**, on loopback with real nodes in one process on a manual clock and synthetic vblanks: anchors, the group's air and its
  receipts, the role kept across a restart, free-wheel and the anchored switch, the step and the slew, the board and its
  verdict, preflight by injected fault, LTC chased on a leader adopted by its follower.
- **Lab**: real displays and boards (the `@lock` join acts through wp2-output's drivers).
- **Not yet**: a switch such as `BLACKOUT` has no group lead, so each member takes it when the line runs there (the leader's instant
  is its event's stamp, not its frame); a step reaches the render thread a moment before the rebased snapshot (an epoch in the
  snapshot would pair them), and the frame-lock counter's anchor is not moved by a step; the verb row a fan-out reads is written
  after the lane ran the line, so two sessions' air lines within microseconds can reach members in the other order. Each needs a
  seam outside this package (the papers note lists them).
- **Next round**: `SYNC LATENCY` and the group's air latency (`@latency`, with the per-output latency ring); succession lists;
  the frame code and the sync certificate.
