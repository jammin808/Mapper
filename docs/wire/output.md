# wp2-output on the wire

Presentation from the display's side and the frame lock. The design is `docs/OUTPUT.md`.

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `OUTPUT STATS` | `OUTPUT STATS [<output>\|ALL]` | Each output's presentation from the display's side: path, measured refresh, missed vblanks, held frames, latency, lock | low | no |
| `PRESENT RESET` | `PRESENT RESET [<output>\|ALL]` | Zeroes presentation counters (the old totals go to the journal) | low | yes |
| `SYNC APPLY` | `SYNC APPLY [<group>]` | The leader joins the group's outputs to the hardware framelock and answers each output's observed lock | high | yes |
| `SYNC LEAVE` | `SYNC LEAVE [<output>\|ALL]` | Outputs leave the framelock and present free-running, measured | high | yes |
| `SYNC REJOIN` | `SYNC REJOIN [<output>\|ALL]` | Outputs that fell out of the lock join it again (armed: only while blacked out) | high | yes |

An output is named by id, 1-based number or name; none, or `ALL`, is every enabled output.

- **`OUTPUT STATS`** answers `[{"id","name","latencyMs","present":{…}|null,"lock":{…}}]`, the same objects as STATE.
- **`PRESENT RESET`** (off the lane, on the render thread) answers the totals that were there,
  `[{"id","presented","missedVblanks","held","glitches"}]`, and journals them (`present`). Refused: no render host; an unknown
  output; "nothing to reset" when the named outputs presented nothing since their counters started.
- **`SYNC APPLY`** (off the lane) joins every enabled output to the board's swap group and barrier (`NodeSettings.Present`), then
  waits up to 10 s to observe each one locked or fallen out, and answers `{"did":"applied","locked":2,"of":2,"outputs":[{"id",
  "state","reason","words"}]}`.
- **`SYNC LEAVE`** with no output (or `ALL`) releases the group's wish; with an output, holds that output out while the wish
  stands. Answers `{"did":"left",…}` once observed free.
- **`SYNC REJOIN`** asks the outputs that fell out (or were held out) to join again; answers `{"did":"rejoined",…}` once observed.

**Refusals lead with their reason word** (`<reason>: <words>`), and the last one is kept in STATE `sync.lock.refused`:

| Reason | Verbs | When |
|---|---|---|
| `no-render` | all three | the node has no render host |
| `off` | APPLY, REJOIN | this node's `NodeSettings.Present.FrameLock` is off: its outputs free-run, measured |
| `follower` | APPLY | this node follows a leader ("SYNC APPLY on <leader>") |
| `armed` | APPLY, REJOIN | the show is armed and not blacked out |
| `unknown-group` | APPLY | no playlist names that sync group |
| `no-board` | APPLY | no sync board (with the driver's words) |
| `not-configured` | APPLY | the board has no server or client display (the one-time administrator step) |
| `cable-into-switch` | APPLY | a sync cable runs into a network switch |
| `no-outputs` | APPLY | no output is on |
| `mixed-rates` | APPLY | the outputs run at different refresh rates (named) |
| `already` | APPLY | every output is in the lock already |
| `not-applied` | LEAVE, REJOIN | the lock is not applied |
| `not-in-lock` | LEAVE | the output is out of the lock already |
| `nothing-fell-out` | REJOIN | no output fell out or was held out |
| `no-output` | LEAVE, REJOIN | the named output is no output of this node |

Inside the machine an output's own refusal or fall-out reason is one of `no-board`, `not-configured`, `cable-into-switch`,
`armed`, `stall`, `count-stalled`, `board-unsynced`, `device-busy`, `display-changed`, `join-timeout`, `join-refused`
(`outputs[].lock.reason`).

## STATE

```jsonc
"outputs": [{ …, "latencyMs": 33.0,
  "present": {"path":"gl-window|offscreen","mode":"gl|offscreen","evidence":"none","refreshHz":59.999823,"nominalHz":60,
              "ppm":-2.95,"settled":true,"missedVblanks":0,"missed60s":0,"held":0,"delayFrames":0,"latencyResidualMs":0,
              "statsAgeMs":16.7,"words":"59.999823 Hz · 0 missed in 60 s","presented":215990,"late10s":0,
              "phase":"approximate|nominal|unknown","light":"green|amber|grey"} | null,
  "lock": {"state":"free-unknown|free-measured|joining|framelocked|house-locked|fell-out","words":"…","reason":"",
           "fellOut":0,"rejoins":0} }],
"sync": { …, "latencyMs": 33.0,
  "lock": {"state":"framelocked","driver":"nvapi|fake|none","board":"Quadro Sync II (P2061)"|null,"words":"…","applied":true,
           "locked":2,"fellOut":0,"rejoins":0,"time":"count|clock",
           "refused":null|{"verb":"SYNC APPLY","reason":"armed","words":"…","at":1760101234567}} },
"machine": { …, "present": {"api":"gl","adapter":"OpenGL 4.5 llvmpipe …","path":"gl|offscreen","experimental":false,"why":"",
                            "resets":0,"presentMsP95":0.21,"dialect":"core330|es300","context":"glx|egl|egl-surfaceless"} | null }
```

- `outputs[].dropped` (today's key) is now the late presents of the last 10 s **past the 0.5 % budget** (0 within it), so
  `outputs[].light` is amber only past it; the running total is `present.missedVblanks`.
- `sync.latencyMs` is `ProjectSettings.syncLatencyMs` when set (≥ 0), else the largest enabled output's `latencyMs`.
- `machine.present.presentMsP95` is the time a frame spends in present calls (the pacing window's wait for the vblank included).
- Events: `sync.fellout` `{"output","words"}` when an output falls out of the lock (with a `fault` journal row).
- Context: `sync:lock` (kind `sync`) while the lock is applied (or an output fell out), linked from each `output:<id>` with the
  Eye's verbs only — `follows` while locked, `hears` while free; `machine:present` when the platform is not what was asked.
- Preflight (section `outputs`): `outputs.present.<id>` and, while the lock is applied, `outputs.lock.<id>` — red when it fell
  out (fix `SYNC REJOIN <id>`), amber when held out (fix `SYNC REJOIN <id>`) or free after a refused join (fix `SYNC APPLY`).
- INDEX: section `present` (rows `{"id","kind":"output","label","parent","light","state"}`).
- Fixture: `tests/fixtures/state/output.json`, cards `output.cards.json` (written by `OutputFixtureTests`).

## Papers note

For the integration docs pass (`WIRE.md`, `ARCHITECTURE.md`, `ROADMAP.md`):

- WIRE.md: the five verbs above, their refusal reasons, the STATE keys, the event `sync.fellout`, and the changed meaning of
  `outputs[].dropped` (late presents past the 0.5 % budget over 10 s, against the output's own refresh — the round-1 carry-over
  "headless dropped measured against project fps" is closed).
- ARCHITECTURE.md: the render platform seam (`IGpuPlatform`, `IOutputPresenter`), the GL dialects (core 3.30, ES 3.00 through
  EGL), the present clock (vblank fit and ledger) and the frame-lock machine with its drivers; show time under the lock comes from
  the board's count through the leader's anchor.
- ROADMAP.md: shipped — presenter seam, ES dialect, vblank truth, the lock machine (NVAPI board facts compile-only); next — the
  "Next round" list of `docs/OUTPUT.md`.
- `NodeSettings.Present` (`path`, `dialect`, `frameLock`, `swapGroup`, `swapBarrier`) is read at the render host's start.
