# wp2-optimise on the wire

Optimise on import (`docs/OPTIMISE.md`). A `<media>` is a library item's id or name; a `<job>` is an `oj…` id (node-local, never in
the project); a `<version>` is an `mv…` id (in the project, on its item).

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `MEDIA OPTIMISE` | `MEDIA OPTIMISE <media>…\|ALL [TO hap\|hap-alpha\|hap-q\|hap-q-alpha] [QUALITY FAST\|SHOW] \| MEDIA OPTIMISE CANCEL\|PAUSE\|RESUME [<job>\|ALL]` | `{"stage":"accepted","jobs":[…],"skipped":[…]}`, off the control lane; CANCEL/PAUSE/RESUME `{"cancelled"\|"paused"\|"resumed":[…]}` | review (CANCEL, PAUSE, RESUME low) | yes (jobs; the version when one is made) |
| `MEDIA JOBS` | `MEDIA JOBS` | the STATE shape below with every job | low | no |
| `MEDIA VERSIONS` | `MEDIA VERSIONS <media>` | `{"media","use","playsHere","why","versions":[…]}` | low | no |
| `MEDIA USE` | `MEDIA USE <media> AUTO\|SOURCE\|<version>` | `{"stage":"applied","media","use","effect"}` | review | yes (project) |
| `MEDIA RULES` | `MEDIA RULES` | `{"mode","quality","rules":[…],"builtIn":[…]}` | low | no |
| `MEDIA RULE` | `MEDIA RULE MODE AUTO\|NEVER` | `{"mode","effect"}` | review | yes (project) |

**MEDIA OPTIMISE.** Without `TO` each item is planned by the rules as though the mode were auto (a person asked); with `TO` the named
target is made. The reply is an attempt, not a fact: `"stage":"accepted"`, one row per job queued
(`{"id":"oj…","media":"md…","to":"hap-q","key":"ok:…"}`) and one per item skipped (`{"media":"md…","why":"…"}` — "already has HAP Q
(mv…)", "already queued (oj…)", "every target node decodes it on the GPU", "only video clips are optimised this round", "not on this
node", the HDR words). The version is the fact: it lands on the item (`MEDIA VERSIONS`) as one edit when the job is done.

**Refusals**, with words: the node is armed ("optimise runs in rehearsal, not on an armed node — disarm first"; the show's gate
refuses it first as a Heavy line); no such media; `TO rechunk|strip|snappy|hap-r|hap-hdr|hap-alpha-only` ("… arrives next round");
`QUALITY ARCHIVE`; nothing to do (every item's reason, joined); no service on the node. CANCEL, PAUSE and RESUME without a job mean
ALL and refuse when nothing changes ("no job is queued or running", "no job is paused").

**MEDIA USE** refuses an unchanged use ("Clip already uses source") and a word that is none of AUTO, SOURCE or a version of the item.
Its `effect` says it takes effect at the item's next open, and how many open decoders keep playing what they opened. The generic
`SET md… use "…"` is checked the same way (auto, source or one of its versions).

**MEDIA RULE MODE** refuses ASK ("ASK, with its suggestions, arrives next round") and an unchanged mode.

## STATE

At the end of `media`, `null` on a node without the optimise part:

```json
"media":{ …, "optimise":{"mode":"auto","governor":"run|trickle|paused|waiting","words":"a show plays: one stream, GPU ≤ 15 %",
  "running":1,"queued":0,"failed":0,
  "worker":{"pid":4242,"gl":"4.5 (Core Profile) Mesa … · llvmpipe …","encoder":"gl-frag/1 · llvmpipe …","restarts":0},
  "jobs":[{"id":"oj…","media":"md…","name":"Clip","to":"hap-q","state":"queued|encoding|verifying|done|failed|cancelled",
           "framesDone":1200,"frames":1800,"fps":31.5,"etaS":19.0,"attempts":0,"paused":false,"version":"","psnrAvg":0,"words":""}]}}
```

`jobs` holds at most 16 rows: the running ones, the queue in order, then the latest finished. `MEDIA JOBS` has them all. A done
job's `version` is its `mv…` and its `words` say the audio's fate and the quality ("aac copied; 44.1 dB average, 43.9 dB lowest").

## Context

The item `optimise`: red when a job of an item the show plays failed (its words); green while jobs run or wait ("optimising 2 clips —
a show plays: one stream, GPU ≤ 15 %"); absent otherwise.

## Preflight

Section `optimise`: amber for each video the show plays at 4K or larger (what outputs and surfaces show, through playlists, canvases
and generators' inputs, and what enabled LED fixtures sample) that this node decodes on the CPU (what plays here is not a
HAP codec), with the fix `MEDIA OPTIMISE <media> TO hap-q`; one green row when there is none.

## Papers note

After the content and LED merges: the governor reads LED drops and late LED nodes, and "what the show plays" (preflight, the
context's red) follows generator inputs and LED fixtures.


For the integration docs pass: WIRE.md gains the six rows above (MEDIA ESTIMATE and MEDIA VERSION are trimmed: next round);
ARCHITECTURE.md §1 gains the third process role, `PlayMap optimise-worker`; PLAYBACK.md's media chapter gains versions and `use`;
ROADMAP.md R2.4 moves to "built" for the must above the cut line, with the next-round list of `docs/OPTIMISE.md`.
