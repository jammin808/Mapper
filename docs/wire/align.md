# wp2-align on the wire

Camera auto-align of flat screens (`docs/ALIGN.md`). A `<group>` is an alignment group (`al…`, or its name), a `<run>` a run
(`ar…`). Corners are 0–1 of the camera picture, top-left first and clockwise.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `ALIGN NEW` | `ALIGN NEW <name> <camera> <output>…\|ALL [CANVAS <canvas>\|NEW] [SHAPE AUTO\|FLAT]` | JSON `{id, outputs, canvas, words}`; `<camera>` a camera rig's id or name, or `sim` (the bare simulator); `ALL` every enabled output that does not render a projector's 3D picture; without `CANVAS` the canvas the outputs show, else a new one as wide as all of them; an output showing something else is put on the canvas (the words name it). The same outputs through the same camera again, an output named twice, a projector-mode output and `SHAPE CYLINDER` (next round) refused with words | review | yes |
| `ALIGN RUN` | `ALIGN RUN <group> [ONLY <output>…] [STEPS SCAN,SOLVE,COLOR\|ALL] [FIT INSIDE\|CORNERS] [COLOR OFF\|WHITE]` | JSON `{stage:"accepted", run, group, pictures, words}`; the run goes on off the lane (`STATE.align.run`); it stages, never applies. `COLOR WHITE` (or a `COLOR` step, or `ALL`) adds the colour step: every output's white and level matched by gains in linear light. `ONLY` scans the named outputs again and keeps the others' warps, from the captures beside the run the group's outputs stand on. Every run ends with the verify pass (the outputs it scanned). Refused with words: one running already (`ALIGN CANCEL` first), a scan running, the project unsaved, the camera gone, an output off or in projector mode, `FIT CORNERS` without corners, a step set without `SCAN,SOLVE` (a step alone is next round), `COLOR FULL` (next round), `ONLY` with colour, with every output, with an output not the group's, or when the outputs do not stand on one accepted run; a node without the align part | high | yes |
| `ALIGN STATUS` | `ALIGN STATUS` | `STATE.align.run` (null before the first run) | low | no |
| `ALIGN SCORE` | `ALIGN SCORE <run>\|<group>` | JSON `{run, group, state, score, unit, grade, seamPx, coverage, terms:[{code, words, value, unit, penalty, light, fix, hint}], words}`: a run by id (held by the runner, else read from its folder), or a group's staged run, else the run its outputs stand on; the terms are coverage (fix `ALIGN RUN <group> FIT INSIDE`, or `ALIGN CORNERS <group> CLEAR` when corners were clicked), seam or stack (the verify pass's misregistration; fix `ALIGN RUN <group>`) and uniformity | low | no |
| `ALIGN CANCEL` | `ALIGN CANCEL` | `cancelling <run>`; the run ends `cancelled`, the outputs the show's again; refused when none runs | low | yes |
| `ALIGN ACCEPT` | `ALIGN ACCEPT <run>\|<group>` | JSON `{group, run, applied:[{output, snapshot, fallback}], score:{value, unit, grade, rmsPx, p95Px, confidence, source, words, terms}, words}`; answered off the lane; every output's measured correction recorded in the calibration ledger (source `align`) and applied in one edit (one undo step), the correction it had kept as a result and made its fallback, its blend γ the measured one; event `align.accepted`. A run read back from its folder when the runner no longer holds it. Refused: a failed or cancelled run (its words), files missing or changed, every output standing as the run has it already | review | yes |
| `ALIGN REVERT` | `ALIGN REVERT <group>` | JSON `{group, applied:[{output, fallback, recorded}], words}`: each output back to its fallback (recorded as a new result, source `revert`); refused when none has a fallback or each stands on it | review | yes |
| `ALIGN CORNERS` | `ALIGN CORNERS <group> <x1> <y1> … <x4> <y4>`, `ALIGN CORNERS <group> CLEAR` | JSON `{group, fit, cameraCorners:[[x,y]…], words}`; corners make the group `FIT CORNERS`, `CLEAR` back to `FIT INSIDE`; a point outside 0–1, a crossed or clockwise-wrong quad, the same corners again refused | review | yes |
| `ALIGN CLEAR` | `ALIGN CLEAR <output>\|<group>` | `cleared the measured correction of …`: each output's correction taken off (a `Cleared` result in the ledger), what it had kept as its fallback; refused when none has one | review | yes |
| `DEVICE NATIVE` | `DEVICE NATIVE <device>` | JSON `{device, protocol, settings:[{name, word, value, neutral, on, verified, caveat}], unsupported, guards:[op…], fix, words}`: the projector's own geometry (`QVX:GMMI0`) and edge blend (`QVX:EDBI0`) read live over Panasonic NTCONTROL, queries only; `fix` the `DEVICE RAW` line that turns off what fights a measured correction ("" when nothing does); other protocols refused with words saying where to look | low | no |

Every ALIGN verb that changes something is refused while the show is armed, with the show gate's own words. `ALIGN PLAN`, the
`NEUTRAL` step, `COLOR FULL` and `SHAPE CYLINDER` are not in the catalogue (next round).

## STATE

```jsonc
"align": {
  "run": null | {"id":"ar…","group":"al…","state":"running|staged|accepted|failed|cancelled","light":"amber|green|red|grey",
                 "step":"identify|scan|solve|stage","progress":0.42,"etaS":12.5|-1,"words":"…",
                 "outputs":[{"id":"op…","name":"…","step":"…","progress":1,"light":"green","words":"γ 2.20, 100 % decoded, warp 0.07 px"}],
                 "score":null|{"value":100,"unit":"points","grade":"A"}},
  "groups": [{"id":"al…","name":"Wall","shape":"auto|flat","camera":"cm…|sim","outputs":["op…"],"canvas":"cv…",
              "fit":"inside|corners","corners":false,"lastRun":"ar…"|"","staged":"ar…"|"","score":100|null,"grade":"A"|"",
              "light":"green|amber|red|grey","words":"…"}]
},
"align": { …, "devices": [{"id":"dv…","outputs":["op…"],"read":true,"geometry":true,"blend":false,"at":1760…,
                          "light":"amber|green","words":"P2's own geometry is on while PlayMap corrects op2 — two warps fight",
                          "fix":"DEVICE RAW dv… VXX:GMMI0=+00000"|""}] },
"outputs": [{ …, "measured": null | {"run":"ar…","residualPx":0.07,"gamma":2.2|null,"colour":false,"blend":"replace"|null,
                                     "files":"ok|loading|<file> missing — <output> runs without its measured correction",
                                     "light":"green|grey|red"} }]
```

`devices` lists every device linked to an output with a measured correction (by the link's output, or by the projector the
output shows) once its own processing has been read: in step with the device's poll, at once after an accept. A group is red when
an output's files are missing, amber while its run runs or a run waits to be accepted, otherwise the light
of its accepted grade (grey: not measured yet). The fixture is `tests/fixtures/state/align.json`, its cards `align.cards.json`.

## Context, events, preflight, INDEX

- Context: `align:<group>` (kind `align`) with the group's light and words; a `device:<id>` item is raised to amber (never lowered
  from red) with the guard's words and fix while that projector's own geometry or blend fights a measured correction.
- Events: `align.accepted` `{group, run}`.
- Preflight section `align`: each output with a measured correction — red `missing` (a file missing, outside the folder or
  changed; fix `ALIGN REVERT <group>` when it has a fallback, else `ALIGN CLEAR <output>`), red `not loaded`, amber `staged`
  (`ALIGN ACCEPT <run>`), green `ok`; amber `double correction` per device whose own processing fights a correction (fix its
  `DEVICE RAW` line); one green row when nothing applies.
- INDEX section `align`: a row per group (light, state `none|running|staged|A 100`); keys Align (`ALIGN RUN`), Accept
  (`ALIGN ACCEPT`), Revert (`ALIGN REVERT`, hold).

## Files

A run's folder `align/<run>/` beside the project, written whole and never again: `run.json` (state, camera, fit, words, seam,
coverage, white p5/p95, the flat fit's residual, the score with its terms, each output's γ, decoded share, residuals and its
measured correction) and per output `warp-<output>.pfm` (the grid: u, v, valid per node) and `blend-<output>.pfm` (weight,
pedestal, uniformity gain at a quarter of the raster), `capture-<output>.bin` (what the camera saw of it: the decode, footprint,
white picture and photometry, deflated) and `black.bin` (the camera's black picture), which a later `ONLY` run keeps for the
outputs it does not scan again. The project names each warp and blend file with its hash; a file is read only inside the
project's folder.

## Papers note

See `docs/wire/resolve.md` for what wp2-resolve added (ONLY, COLOR WHITE, the verify pass, ALIGN SCORE, DEVICE NATIVE and the
double-correction guard, the pictures and pattern time of a run).
