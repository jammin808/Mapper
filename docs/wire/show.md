# wp2-show on the wire

What Show Mode, locks, panic, the master, the GO fence and preflight add to the wire. Every row below is routed (VERBS marks it
`"available"`). The block's `BLIND …` rows stay in the catalogue for blind editing (wp2-blind, this package's continuation) and
answer "… is not available on this node yet" until it routes them; `SHOW SET COMMIT-LEAD` (blind commits) and the group forms
(`ALL`, without a sync fan-out) are refused with words.

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `SHOW` | `SHOW [STATUS]` | Show Mode: armed or rehearsal, by whom and since, the rules, panic, locks and the last preflight | low | no |
| `SHOW ARM` | `SHOW ARM [ALL] [<words>]` | Arms the show with no password: edits that would move the air are refused for every caller until SHOW DISARM; transport, blackout and PANIC stay live; armed survives a restart (ALL: every node of the group, refused while this node fans out to none) | review | yes |
| `SHOW DISARM` | `SHOW DISARM [ALL]` | Back to rehearsal (ALL: every node of the group, refused while this node fans out to none) | review | yes |
| `SHOW SET` | `SHOW SET MINGO <ms> [ARMED\|REHEARSAL] \| REMOTE-VIEW-ONLY ON\|OFF \| DISARM-FROM PAIRED\|DESK \| EXPECT-ARMED ON\|OFF \| COMMIT-LEAD <ms> \| PANIC-LEAD <ms>` | Changes a show rule: double-GO protection, remote clients read-only while armed, who may disarm, warn when playing unarmed, how far ahead on the show clock a panic lands (COMMIT-LEAD waits for blind commits) (kept in the project, one undo step) | review | yes |
| `PANIC` | `PANIC [BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD] [ON <output>…\|ALL] [FADE <ms 0-10000>] [AT +<ms>\|<unix ms>] [HERE]` | Takes each output to its planned outcome over its fade, picture and sound, now or at an instant on the show clock; a second PANIC during the fade is the hard stop; HERE keeps it to this node; never refused for an operator (an assistant's PANIC waits for a person) | high | yes |
| `PANIC CLEAR` | `PANIC CLEAR [ON <output>…\|ALL] [FADE <ms 0-10000>] [HERE]` | Brings the air back from a panic over a fade (transports stay as the outcome left them) | high | yes |
| `PANIC SET` | `PANIC SET <output>\|ALL BLACK\|RUN\|REWIND\|NEXT\|FREEZE\|SAFE\|SHUTTER\|FX-OFF\|HARD [FADE <ms 0-10000>] [SAFE <source>]` | Plans what PANIC does on an output, with its fade and the safe look SAFE shows (kept in the project, one undo step) | review | yes |
| `MASTER` | `MASTER <0-100> [FADE <ms 0-60000>] [HERE]` | The grand master over every output, picture and sound, faded on the show clock (not an undo step); HERE keeps it to this node | high | yes |
| `LOCK` | `LOCK <id> [<words>] \| LOCK AREA mapping\|layout\|content\|playlists\|media\|devices\|network [<words>]` | Locks an entity or an area: no verb, undo or assistant changes it; playback and reading go on (not an undo step) | review | yes |
| `UNLOCK` | `UNLOCK <id> \| UNLOCK AREA <area> \| UNLOCK ALL` | Lifts a lock (ALL: every lock this show holds) | review | yes |
| `LOCKS` | `LOCKS` | Every lock with who set it and why, and the areas an armed show adds | low | no |
| `PREFLIGHT` | `PREFLIGHT [QUICK] [ALL]` | Checks the node is ready for the show from facts: outputs on present displays, media present and readable, linked projectors reachable, free disk, the show's safety; each row carries the line that fixes it; answers the report's id (ALL: every node of the group, refused while this node fans out to none) | low | yes |
| `PREFLIGHT REPORT` | `PREFLIGHT REPORT [<id>]` | The last preflight report, or one by id | low | no |

On `PLAYLIST GO|NEXT|PREV` and `VIDEO PLAY|RESTART` (spine rows): `EXPECT <item>|#<n>` refuses the GO when the item that would play
is not the one named — "the next item on Show is now 'C', not 'B' — the key was out of date"; and the show's MinGo refuses a
second GO on the same playlist inside the window — "double GO protection: 0.20 s since the last GO on Show (0.30 s armed)".

## Replies

- `SHOW` — the `STATE.show` object below.
- `SHOW ARM` — `{"armed":true,"by":"Desk","since":<unix ms>}` (`"members":n` with `ALL` on a group's leader); refused "already armed
  by … since HH:mm".
- `SHOW DISARM` — `{"armed":false,"landed":0,"setAside":0}`; refused "not armed — the show is in rehearsal".
- `PANIC` — `{"outcome":"black","hard":false,"since":…,"outputs":[{"id","outcome","fadeMs","until"}],"devices":[{"id","stage"}],
  "released":[{"playlist","done","atLeaderNs","words"}],"notes"?}` (`released`: each chased playlist whose chase the panic's step
  disengages — `done:true` when it already has, `false` while the step waits for its fade's end; its words are in `notes` too; a
  step the playlist refuses is journalled `ok:false` and, when it ran inside this PANIC, its words are in `notes`); the
  second press inside a fade answers `"outcome":"hard","hard":true`; a press after every fade has settled is refused
  "already in panic since HH:mm:ss (black) — PANIC CLEAR brings the air back".
- `PANIC CLEAR` — `{"cleared":["op_1"],"fadeMs":1000,"inPanic":false}`; refused "not in panic — the air is the show's".
- `PANIC SET` — the plan rows `[{"output","outcome","fadeMs","safe"}]`; a SAFE plan without a look is refused with the line that
  gives one.
- `MASTER` — `{"master":0.5,"fadeMs":1000}`; refused "the master is already at 50" when it is.
- `LOCK` — `{"target":"op_1","kind":"entity"}`; `UNLOCK` — `{"unlocked":[…]}`; `LOCKS` — `[{"target","kind","by","at","words",
  "implicit"}]` with the armed show's `mapping` and `layout` as `implicit:true`.
- `PREFLIGHT` — accepted (receipt stage `accepted`) with `{"id":"pf…","depth":"quick"}`; the report is published when it is done
  (STATE, the context, the journal, the `preflight.done` event). `PREFLIGHT REPORT [<id>]` — the whole report:
  `{"id","at","depth","durationMs","light","green","amber","red","grey","ready":{"done","of","firstOpen"},"complete",
  "rows":[{"id","node","section","item","light","value","note","fix"}]}`.

## Who may run what

Show Mode's table (`ShowPolicy`, docs/SHOW.md) decides every line before its handler, after pairing; refusals are journalled
(`kind:"refused"`) and counted in `STATE.show.refused`. An assistant's line the armed table holds (MCP, the assistant) is queued
on this node's proposal list and answered "… waits for a person while the show is armed … — queued for a person as proposal
apN"; it never runs on the assistant's word. Callers outside the engine can ask first through `IShowDecisions` (Core), which the
engine implements.

## STATE

`air` gains, after today's keys: `"master":{"level":1.0,"to":1.0,"untilLeaderNs":0}`, `"panic":null|{"outcome","hard","by",
"since","outputs":[{"id","outcome","level","settled"}],"devices":[{"id","stage"}]}`.

Top-level `show` (after `cameras`):

```json
"show":{"armed":true,"by":"Desk","since":1760101234567,"words":"doors at eight",
  "rules":{"minGoMs":300,"minGoMsRehearsal":0,"remoteViewOnly":false,"disarmFrom":"paired","expectArmed":true,
           "defaultOutcome":"black","defaultFadeMs":1000,"panicLeadMs":0},
  "blind":null,
  "panic":null|{…as air.panic},
  "locks":[{"target":"content","kind":"area","by":"Desk","at":…,"words":"show night","implicit":false}],
  "refused":{"last60s":1,"last":"Companion: FOH deck — SET op_1 width 1280 — Not while armed: …"},
  "preflight":null|{"id":"pf…","at":…,"depth":"quick","light":"red","green":3,"amber":0,"red":1,"grey":2,"ready":"4/5",
                    "firstOpen":"outputs: Output 1 — this machine has no display for it","running":false},
  "minGoMs":300}
```

`blind` stays `null` until blind editing lands. The fixture is `tests/fixtures/state/show.json`, written by `ShowStateFixtureTest`.

## Cards

`tests/fixtures/state/show.cards.json`: Show, Panic, Master, Locks and Preflight. A card is `{"title","path","light","words",
"fields":[{"label","path"}],"actions":[line…]}`; `light` is a list of rules read in order — `{"if":<path>,"light":<word>}` (the
path holds a value that is not null, false or 0), `{"from":<path>}` (the path holds a light word), `{"light":<word>}` (always);
paths are relative to the card's path; `words` fills `{path}` holes.

## Context

- `show` (kind `show`, always present, first): red "PANIC black since 21:04:05 — PANIC CLEAR"; green "ARMED by Desk since 20:31";
  amber "rehearsal while playing — SHOW ARM before doors" (only with EXPECT-ARMED ON); grey "rehearsal".
- `preflight` (kind `preflight`), once a preflight ran: its worst light, "3 green · 0 amber · 1 red · show-ready 4/5 · just now".
- Links: `show` **controls** `output:<id>` (red in panic, amber under the master, else green); `link:patterns` **controls** `show`
  (no link points at `air`); `link:stagewatch` **feeds** `show`.
- Headline: "ARMED · …" armed, "PANIC black · …" in panic.

## Events

`show.armed`, `show.disarmed` (`{"by"}`), `panic` (`{"outcome","hard"}`), `panic.clear` (`{"outputs"}`), `lock` (`{"target",
"kind","on"}` or `{"targets","on":false}`), `preflight.done` (`{"id","light"}`), `refused` (`{"verb","who","words"}`, at most
one a second).

## INDEX

Section `show` (preset title "Show", one key per row sending the row's id as its line): `SHOW ARM`, `SHOW DISARM`, `PANIC`,
`PANIC <outcome>` for the default, HARD and every planned outcome, `PANIC CLEAR`, `MASTER 0`, `MASTER 100`, and `MASTER` as an
`encoder` row (the encoder sends `MASTER <0-100>`).

## Papers note

For the integration docs pass (WIRE.md, ARCHITECTURE.md, UX.md, ROADMAP.md):

- Show Mode exists: `SHOW ARM` restricts every caller (the desk included) to running the show — transport, blackout, the master
  and PANIC stay live; mapping and layout are locked implicitly; armed survives a restart (the safe side). The full table is
  docs/SHOW.md.
- Locks (`LOCK`, `LOCK AREA`) are kept in the project but are not undo steps; no edit, undo or redo moves what they hold.
- `PANIC` has nine outcomes planned per output (`PANIC SET`), fades on the show clock (picture and sound), the hard second press,
  and `PANIC CLEAR`; `MASTER` is the grand master. `air.master` and `air.panic` are new STATE keys; `uAir` carries them to the
  outputs.
- The GO fence: MinGo (300 ms armed) and `EXPECT` on GO, NEXT, PREV, PLAY and RESTART.
- `PREFLIGHT` checks outputs, media, projector links, free disk and the show's safety, each row with a fix line; every package adds
  its own checks.
- Not yet: blind editing (W2), the group forms (`ALL`) without a sync fan-out, FULL and SOAK preflight, parked edits, soft leases,
  the approval desk on Stagewatch, named users, lowering the risk of safe-direction lines.
