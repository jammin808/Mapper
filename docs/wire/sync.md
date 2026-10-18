# wp2-sync on the wire

The group's clock, anchors, air and vblank verdict, network side. The design is `docs/SYNC.md`.

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `SYNC LEAD` | `SYNC LEAD <group>` | This node leads a sync group (kept for the next start) | review | yes |
| `SYNC FOLLOW` | `SYNC FOLLOW <group> <node>` | This node follows a sync group's leader (kept for the next start) | review | yes |
| `SYNC STATUS` | `SYNC STATUS` | The clock, the group, the feed and the board's verdict | low | no |
| `SYNC RECEIPT` | `SYNC RECEIPT <seq> ACCEPTED\|APPLIED\|REFUSED <leader ns> [<words>]` | A group member's answer to an air line its leader fanned out | low | no |
| `SYNC LATENCY` | `SYNC LATENCY AUTO\|<ms 0-500>` | The group's air latency (AUTO: the largest output latency any member reports) | review | yes |
| `SUBSCRIBE ANCHORS` | `SUBSCRIBE ANCHORS [<node> [<output>…]]` | The group's feed (a topic of SUBSCRIBE) | low | no |

With the sync part, the round-one rows `SYNC LEAD`, `SYNC FOLLOW` and `LINK PATTERNS` are answered by it:

- **`SYNC LEAD <group>`** (off the lane) answers once this node leads: `leading show: followers take this node's clock on UDP
  9942`. Refused: a group word that is not letters, digits, `-` and `_` (up to 32); "already leading show"; the clock-sync port
  could not listen.
- **`SYNC FOLLOW <group> <node>`** (by id or name, off the lane) answers once the exchange runs and the feed is dialled.
  Refused: no such PlayMap node heard; the node itself; a quiet node; "already following FOH (0badc0de) for show" while that
  leader is heard and its feed live.
- **`LINK PATTERNS FOLLOW-BLACKOUT ON|OFF`** is kept in `NodeSettings.Sync.followPatternsBlackout`.
- **`SYNC LATENCY AUTO|<ms 0-500>`** (on the lane, show class layout) sets the leader's project `settings.syncLatencyMs` (−1 =
  AUTO), one undoable edit: `the group's air latency is 80 ms; show's members take it with the feed's next line`, and, below a
  display latency the group reports, `… — below the 50 ms display latency 0badc0de reports: that node's outputs stay at their own
  and light later than the rest`. In an armed show it is staged: `staged in the blind draft: the group's air latency is 80 ms —
  carried to the members when the blind draft lands`. Refused: the syntax, anything but a finite 0–500 ms or AUTO, a value set
  already ("the group's air latency is 80 ms already"), a follower ("this node follows 0badc0de in show: the group's air latency
  is its leader's — SYNC LATENCY there"). A solo node keeps it "for when this node leads a group".
- **`SYNC RECEIPT`** is accepted only from the session whose subscription the air line went to, while the leader waits for it;
  anything else: "no air line 7 waits for a receipt from this session". Ephemeral (not journalled).
- **`SUBSCRIBE ANCHORS`** is open like every subscription (anchors are transport facts already in STATE; no secret rides the
  feed). Its words are this subscriber's node id and output ids (letters, digits, `-`, `_`; up to 64 outputs): a subscriber
  that names a node is a **member** (waited for, heard on the board); one that names none only reads. A node is named only by
  itself: the session is paired, or the show network hears that PlayMap node saying hello from the session's address (else
  "SUBSCRIBE ANCHORS 9f8e7d6c: no PlayMap node 9f8e7d6c is heard from 10.0.0.66 — …"). A member that comes back from its own
  address replaces its old subscription. Anything else is refused with the syntax. A member sends
  `SUBSCRIBE ANCHORS 9f8e7d6c op_1 op_2`.

## The feed's lines (leader → member, in order)

```
ANCHOR show pl_show {"index":0,"itemId":"it_a","state":"Playing","mediaNs":0,"leaderNs":1007300000000,"rate":1,"running":true,"inNs":0,"outNs":10000000000,"loop":false,"seq":1}
ANCHOR show @air {"feed":"5c1e9a07","seq":3,"line":"PANIC FADE 1000","leaderNs":1007300000000}
ANCHOR show @lock {"apply":true,"swapGroup":1,"barrier":1,"counter":{"count24":1193046,"leaderNs":987654321000,"periodPs":16666666667,"effectiveAt24":1193166,"seq":5}}
ANCHOR show @latency {"ms":50}
```

`@latency` is the group's air latency (ms, 0–500): the stated one, or AUTO the largest of the leader's own outputs' and each
member output's PMVB report (an output the member named keeps its place while quiet, one it enabled since counts while heard, a
member that leaves drops out; a lead waits up to five seconds for members and their first reports, and a joining member's first
report is waited for as long). It goes whenever it moves, 0 included, and to a member that joins as the latest; a member writes it
through `IPresentBackend.SetGroupAirLatency` (anything else is not taken) and keeps it until the next feed it follows carries one.

`feed` is new per leader process and `seq` counts within it: a member runs each (feed, seq) once. A member answers each `@air`
line on the same connection:

```
@s7 SYNC RECEIPT 3 APPLIED 1007300000000 PANIC as planned on 1 output by FOH (sync leader)
```

`ACCEPTED` first when the line's instant is still to come or a line before it still waits (or when the line only started, as
`PREFLIGHT` without `WAIT` does); `APPLIED` with
the leader instant the line took effect there — the leader's instant, or the member's own now when the line reached it later (a
fade still runs as if from the leader's instant; a switch such as BLACKOUT lands when it runs); `REFUSED` with the member's words
when its gate or the line refuses — a `PANIC` from no later than the panic it is in is "already in this panic since …" (the same
press handed again). A member's answer for a line due after the leader stopped waiting reaches the leader as a
late receipt (journalled "BLACKOUT ON on Stage: applied at its instant"). A `PREFLIGHT` runs beside the feed, so a later air line
is never held behind a member's checks; for `PREFLIGHT QUICK WAIT` the words are the member's finished report as compact JSON
(one word to the end of the line; a report longer than one line says so in words instead), and the leader waits up to 10 s for it:

```
@s9 SYNC RECEIPT 5 APPLIED 1009300000000 {"id":"pf3c9e1a7b","depth":"quick","rows":[…],"nodes":[…],…}
```

## IFleetFanout (for wp2-blind and wp2-backup)

`EngineServices.Fanout` (the sync part) — `Members`: the node ids subscribed as members while this node leads (empty otherwise);
`SendAsync(line, atLeaderNs, ct)`: pushes `ANCHOR <group> @air {…}` to every subscriber and answers one `FanoutReceipt(Node, Name,
Stage, AppliedLeaderNs, Words)` per member — `applied`, `refused`, `accepted` (its instant is still to come) or `silent` (no
answer within 2 s, or 10 s for a report: one deadline for every member of the send); an applied receipt's `Words` is the member's
report for `PREFLIGHT … WAIT`, else what the line did there. The leader's own `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER` are
already fanned out by the sync part when they run (from the journal's verb rows, and each line of a binding's sweep, which the
journal keeps as one row; `HERE` and a panic line naming outputs stay local; a panic's `AT <unix ms>` goes as `AT +<ms>`, with the
leader's `PANIC-LEAD` added, and a member adds no lead of its own): a package must not send them through `SendAsync` again.

## PMVB (member → leader, UDP, the clock-sync port)

64 bytes, little-endian: `"PMVB"`, version 1, lock state (0–7, `LockState`), flags (settled, board…), path (0 offscreen, 1 gl,
2 dxgi-flip, 3 dedicated; 4 and above are not a report; a Direct3D 12 flip output reports 2, as a D3D11 one does: the presentation
model both share), node id (u32, its eight hex), output id hash (FNV-1a 32 of the UTF-8 id), sequence, board count (24 bits), period
(ps on the leader's time base, 1 000 000 000 … 50 000 000 000), last vblank (leader ns, ≥ 0), refresh index, missed in 60 s (≤ 60 000), house sync
(milli-Hz), air latency (µs, 0 … 500 000), the sender's clock uncertainty (µs, ≤ 1 000 000: half its best round trip plus any correction still being slewed). Any other length, magic, version or
an out-of-range field is dropped and counted (`Refused` on the responder).

## STATE

```jsonc
"sync": {"role":"leader","leader":"0badc0de","offsetMs":0,"rttMs":0,"driftPpm":0,"groups":["show"],
  "group":"show","feed":"live|connecting|lost|off","slewing":false,"steps":0,"freewheelS":0,
  "outputs":[{"node":"9f8e7d6c","output":"op_1","state":"free-measured","path":"gl","refreshHz":59.999,"ppm":-16.67,
              "phaseUs":3.21,"uncertaintyUs":0,"nextSlipS":499.8,"missed60s":0,"frameCount":0,"heard":true}],
  "pairs":[{"a":"op_1","aNode":"0badc0de","b":"op_1","bNode":"9f8e7d6c","phaseUs":3.21,"uncertaintyUs":0,"nextSlipS":499.8,"mixedRate":false}],
  "verdict":"same vblank|free-running|mixed rate|unknown","words":"leading show with 1 member; …","groupLatencyMs":50,
  …wp2-output's "latencyMs" and "lock", the fleet's "epoch",
  "light":"green|amber|red|grey"}
```

`light` is STATE.sync's last key: the group's light read with the verdict, the same as the context item `sync:<group>` and the
INDEX row. On a leader it is the board's: grey with nothing heard, red while a heard output fell out of the lock, amber for mixed
rates, for outputs that report locked but measure apart (a locked pair off one vblank, among free-running outputs too), for an
output joining the lock for over 5 s, and for a free-running pair that slips within 10 min while the show plays, green otherwise.
On a follower (whose verdict is always `unknown`, the leader's to give) it is red while its clock free-wheels or the feed is lost,
green while the feed is live, amber otherwise. A node in no group, or configured into one it has not joined, is grey. A reader
lights the group from `light`; the other keys say why (SyncLightTests).

The fixture is `tests/fixtures/state/sync.json` (SYNC STATUS on a leader), its cards `sync.cards.json`.

**Events**: `sync.stepped {deltaNs}`, `sync.leader.lost {leader}`, `sync.mixedrate {words}`.

## Papers note

For the integration docs pass (`WIRE.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `UX.md`):

- WIRE.md: the rows above (SYNC RECEIPT added in this block), the ANCHORS topic with its words, the four feed lines, PMVB on the clock-sync port, the STATE keys and events. **`sync.offsetMs` means the follower's
  leader-time uncertainty (half the best exchange's round trip), not the raw difference between two machines' clocks** (the
  round-one carry-over): a 5 s gap between two machines' monotonic clocks reads `offsetMs` ≈ 0.05 on a switch.
- ARCHITECTURE.md: the leader clock slews (≤ 1 ms at 200 ppm) and steps on the lane with a rebase of every anchor; anchors and
  the group's air reach members through their own verbs, pinned to the leader's instant; the board's measured software tier.
- ROADMAP.md: carry-overs closed — anchors carried to followers and the role kept (AnchorFeedTests), following never makes a
  playlist jump (ClockSlewTests), the group's air latency carried to every member (SyncLatencyTests). Next: succession, the frame
  code and certificate.
- UX.md: the desk's Sync tab reads `sync` (cards in `sync.cards.json`); its follow/lead buttons send `SYNC LEAD|FOLLOW`.
- The catalogue gate (`tests/PlayMap.Assistant.Tests/CatalogueTests.cs`) gained `AddedRows` beside `TrimmedRows` to admit
  `SYNC RECEIPT`, the protocol row item 4's receipts need.

Seams outside this package that would close what SYNC.md lists as not yet (spine amendments):

- The verb row journalled inside the lane action (or a lane-side "line ran" hook), so the fan-out numbers air lines in lane order
  and reads the lane's instant.
- A step epoch (or the clock mapping) in the published snapshot, so a frame never pairs a stepped clock with an unrebased snapshot;
  the frame-lock counter's anchor moved by a step.
- A group lead for switches (`BLACKOUT`): the leader takes it at now + lead and sends that instant, so members land on its frame.
