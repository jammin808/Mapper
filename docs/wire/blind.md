# wp2-blind on the wire

What blind edit, preflight's breadth, the group's forms and sound on the air add to wp2-show's block (this package continues it:
the rows are in show's block of the catalogue). Every row below is routed (VERBS marks it `"available"`).

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `BLIND` | `BLIND [STATUS]` | The blind draft: whose, how many changes, when it commits | low | no |
| `BLIND ON` | `BLIND ON` | Blind editing for this session: its edits go to a draft, off the air, until BLIND COMMIT | low | yes |
| `BLIND OFF` | `BLIND OFF` | Leaves blind editing (refused while the draft holds changes: COMMIT or DISCARD them) | low | yes |
| `BLIND DIFF` | `BLIND DIFF` | What the draft changes, entity by entity, in field words, with conflicts | low | no |
| `BLIND COMMIT` | `BLIND COMMIT [NOW\|NEXT [<playlist>]\|ON GO [<playlist>]\|AT +<ms>\|<unix ms>] [FORCE]` | Lands the draft on the air as one undo step at a show boundary, frame-exact; FORCE overrides the conflicts it names | high | yes |
| `BLIND DISCARD` | `BLIND DISCARD` | Drops the draft | review | yes |
| `SHOW SET COMMIT-LEAD` | `SHOW SET COMMIT-LEAD <ms 0-2000>` | How far ahead on the show clock a blind commit lands at the least (34 ms) | review | yes |
| `SHOW ARM ALL`, `SHOW DISARM ALL` | `SHOW ARM [ALL] [<words>]`, `SHOW DISARM [ALL]` | Every node of the group this node leads, answered with each member's receipt | review | yes |
| `PREFLIGHT ALL`, `PREFLIGHT WAIT` | `PREFLIGHT [QUICK] [ALL\|WAIT]` | ALL: every node of the group, one section per node; WAIT: answers with the finished report | low | yes |
| `VIEW … DRAFT` (spine row) | `VIEW <view> SHOW editor DRAFT` | The editor's view of the blind draft (the air when no draft is open); a size cannot be given with DRAFT this round | low | no |

**The gate.** The draft's owner's lines of class content, playlists, mapping, layout or media, and its UNDO/REDO, run into the draft
(`ShowVerdict.BlindOnly`), armed or not; while a commit waits they are refused "the blind draft lands at … — no more edits to it;
BLIND DISCARD cancels the commit". Armed, a remote may not open a draft, and a remote's commit is not landed while armed; an
assistant may open and read one, and its `BLIND COMMIT` is refused (a person lands it). MCP and HTTP calls cannot open a draft (it
needs a lasting connection). A GO (`PLAYLIST GO|NEXT|PREV`, `VIDEO PLAY|RESTART`) lands a waiting `ON GO` commit at the GO's own
instant; a `PLAYLIST GO|NEXT|PREV` pins a waiting `NEXT` commit to the switch it makes (on a group's member, the leader's GO in
the playlist's anchor does both). `SHOW DISARM ALL` is the desk's only.

## Replies

- `BLIND`, `BLIND ON` — the `STATE.show.blind` object below; `BLIND ON` refused "already blind — 1 change staged since HH:mm",
  "Desk holds the blind draft (1 change staged) — one draft per node: it commits or discards it first"; a desk with the owner's name
  on a new session takes the draft back once the old connection is gone ("Desk holds the blind draft on another connection that is
  still open — it commits or discards it there"); "a blind draft needs a lasting connection — BLIND ON from the desk, the wire or a
  WebSocket" (MCP, HTTP).
- `BLIND DIFF` — `{"base":<revision>,"owner":"Desk","changes":[{"id":"cv_main","kind":"canvas","label":"Main canvas","op":"changed",
  "fields":[{"field":"background","from":"\"#000000\"","to":"\"#FF0000\"","words":"background \"#000000\" → \"#FF0000\""}],
  "more"?:n,"words":["Main canvas (canvas): background \"#000000\" → \"#FF0000\""]}],"conflicts":["cv_main"]}`. `op` is `added`,
  `changed` or `removed`; the show-wide values have the ids `name`, `settings`, `show` (rules and panic plans, never locks),
  `audio`, `tempo`, `optimise`, `site`, `leds`, `control`, `timecode`, `fleet`; a list's order is `order:<kind>` (kind `order`,
  label the list, one field `order` with the kept entities' names before and after).
- `BLIND COMMIT` — accepted (`applied:false`): `{"boundary":"now|at|next|on go","playlist":"pl…"|null,"effectiveLeaderNs":<ns>|null,
  "changes":n,"by":"Desk","forced":false}`; then the journal row "blind commit: n changes landed (next) by Desk" and the event
  `show.commit` at the instant. Refused: "not blind — BLIND ON starts a draft", "the draft changes nothing — BLIND OFF leaves",
  "nothing ends on Show — use ON GO or AT", "too close to the boundary — …", "changed on the air since BLIND ON: Main canvas (canvas,
  cv_main) — BLIND DIFF shows the draft; FORCE lands the draft's version", a lock's words, "an assistant's blind draft is landed
  by a person: BLIND DIFF shows it, BLIND COMMIT on the desk lands it".
- `BLIND DISCARD` — `{"discarded":n,"commitCancelled":true|false}`; refused within the commit lead of a waiting commit's instant
  ("the draft is landing now (…) — UNDO takes it back once it has landed").
- `SHOW ARM ALL`, `SHOW DISARM ALL` — `{"armed":…,"by":…,"since":…,"members":n,"nodes":[{"node":"<id>","name":"FOH-A",
  "stage":"applied|already|refused|timeout|…","appliedLeaderNs"?:<ns>,"words":"…"}]}`, this node first.
- `PREFLIGHT ALL` — accepted `{"id":"pf…","depth":"quick","members":n}`; the report (`PREFLIGHT REPORT`) holds every node's rows (each
  row's `node` is its node's name), a `fleet:<node>` row per member (green answered, grey answered incomplete, red silent or
  refused), and `nodes:[{"node","light","green","amber","red","grey","ready"}]`. `PREFLIGHT QUICK WAIT` answers the report itself.
- Errors on a contested store: a live edit to an entity a waiting commit lands — "a blind commit lands Main canvas (canvas) at
  21:04:05.200 — wait for it, or BLIND DISCARD"; a `LOCK` that would refuse a waiting commit — "a blind commit lands … — LOCK it
  once the commit has landed, or BLIND DISCARD first"; `PROJECT OPEN|NEW` while a draft is open; a line decided for a draft that
  ended or began landing before it ran — "the blind draft this line was for has ended — nothing changed".
- `VIEW <v> SHOW editor DRAFT` on a view id that has drawn the air — "<v> has drawn the air — a draft view takes a view id of its
  own (VIEW <v>-draft SHOW editor DRAFT)".

## STATE

`show.blind`: `null | {"on":true,"owner":"Desk","session":"d1","transport":"desk","since":<unix ms>,"base":<revision>,"changes":n,
"commit":null|{"when","boundary","playlist","effectiveLeaderNs"|null,"changes","by","forced"}}`. `session` and `transport` are the
owner's as WHOAMI says them, so a client knows the draft is its own; `when` is the boundary in the words an owner's edit is refused
with while the commit waits ("with the next GO on Show", "at 20:31:05.120").
`show.rules.commitLeadMs` (34). `show.sound`: `null | {"output":"device|loopback|null","status":"…","item":"it…"|null,
"gain":0.5,"problem":""}`. Fixture: tests/fixtures/state/show.json (cards: show.cards.json, "Blind" and "Sound").

**Events.** `show.blind` (`{"on":true,"owner"}`, `{"on":true,"owner","takenFrom"}` on a takeover, `{"on":false}`), `show.commit` (`{"changes","by","boundary"}`, at the commit's
instant).

## Preflight rows

`mapping:<pj>` (red not solved: `PROJECTOR SOLVE <pj>`; red failed with a fallback: `CALIBRATION FALLBACK <pj>`; amber C: `PROJECTOR
SOLVE`; amber no fallback: `CALIBRATION ACCEPT <pj>`), `clock:sync` (`SYNC LEAD <group>`, `SYNC FOLLOW <group> <leader>`),
`links:patterns`, `links:stagewatch` (`LINK PATTERNS <host>`, `LINK STAGEWATCH <host>`), `safety:mingo` (`SHOW SET MINGO 300`),
`safety:in-panic` (`PANIC CLEAR`), `safety:plan:<op>`, `safety:blind`, `fleet:<node>`. A member's rows carry its fix in the note
(" — on FOH-B: SHOW SET MINGO 300"), never as this report's `fix`. The mapping rows are absent where the calibration ledger's own
`calibration` rows run. Show-ready's "sync and backup" step reads `sync`, `clock`, `fleet` and `links`.

## INDEX

Section `show` gains `BLIND COMMIT NEXT` and `BLIND DISCARD` (kind `blind`; amber with "n staged" or "landing next" while a draft is
open, grey "no draft").

## Papers note

For the integration docs pass (WIRE.md, ARCHITECTURE.md, UX.md, PLAYBACK.md, ROADMAP.md):

- Blind edit exists: one draft per node, visible in the editor's draft view, diffed in field words, committed as one undo step at
  NOW, AT, the next switch or the next GO — frame-exact, because the commit is a (root, leader instant) pair the render thread picks
  per frame. Conflicts are named, never merged; FORCE lands the draft's version.
- Preflight reads mapping (through the calibration ledger), the show clock, the links and more of the show's safety; the group's
  forms (ARM, DISARM, PREFLIGHT ALL) answer with each member's receipt and one report section per node.
- Sound on the air: the on-air item's audio plays slaved to its anchor, faded by master and panic on the same show clock as the
  picture (`show.airSound` picks the device).
- Fades and dips now play on a manual GO, NEXT and PREV (the carry-over is closed for a single node; a playlist in a sync group cuts
  at the GO until wp2-sync's anchor feed carries the window).
- An operators entry is an address rule plus a token proved on the connection (a HELLO name alone is a claim; a session from a
  trusted address never counts until the session carries `Proved`); the assistant's
  tools ask Show Mode before running or queueing, as MCP's do; `MirrorEditAsync` is W3 backup's way past the armed show's implicit
  locks.
- Not yet: several drafts (one per session), soft edit leases, parked edits, `BLIND ON ALL`, a draft view at a chosen size, the
  draft's own stage meshes in its view or on the commit's frame, a commit that prerolls a clip the draft adds before its boundary,
  a draft that survives a restart, a panic's outcome on followers' sound beyond the group's air.
