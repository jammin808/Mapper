# wp2-align on the wire

Camera auto-align of flat screens (`docs/ALIGN.md`). A `<group>` is an alignment group (`al…`, or its name), a `<run>` a run
(`ar…`). Corners are 0–1 of the camera picture, top-left first and clockwise.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `ALIGN NEW` | `ALIGN NEW <name> <camera> <output>…\|ALL [CANVAS <canvas>\|NEW] [SHAPE AUTO\|FLAT]` | JSON `{id, outputs, canvas, words}`; `<camera>` a camera rig's id or name, or `sim` (the bare simulator); `ALL` every enabled output that does not render a projector's 3D picture; without `CANVAS` the canvas the outputs show, else a new one as wide as all of them; an output showing something else is put on the canvas (the words name it). The same outputs through the same camera again, an output named twice, a projector-mode output and `SHAPE CYLINDER` (next round) refused with words | review | yes |
| `ALIGN RUN` | `ALIGN RUN <group> [STEPS SCAN,SOLVE\|ALL] [FIT INSIDE\|CORNERS]` | JSON `{stage:"accepted", run, group, pictures, words}`; the run goes on off the lane (`STATE.align.run`); it stages, never applies. Refused with words: one running already (`ALIGN CANCEL` first), a scan running, the project unsaved, the camera gone, an output off or in projector mode, `FIT CORNERS` without corners, any other step (`COLOR`, `VERIFY`, `NEUTRAL`: next round), a node without the align part | high | yes |
| `ALIGN STATUS` | `ALIGN STATUS` | `STATE.align.run` (null before the first run) | low | no |
| `ALIGN CANCEL` | `ALIGN CANCEL` | `cancelling <run>`; the run ends `cancelled`, the outputs the show's again; refused when none runs | low | yes |
| `ALIGN ACCEPT` | `ALIGN ACCEPT <run>\|<group>` | JSON `{group, run, applied:[{output, snapshot, fallback}], score:{value, unit, grade, rmsPx, p95Px, confidence, source, words, terms}, words}`; answered off the lane; every output's measured correction recorded in the calibration ledger (source `align`) and applied in one edit (one undo step), the correction it had kept as a result and made its fallback, its blend γ the measured one; event `align.accepted`. A run read back from its folder when the runner no longer holds it. Refused: a failed or cancelled run (its words), files missing or changed, every output standing as the run has it already | review | yes |
| `ALIGN REVERT` | `ALIGN REVERT <group>` | JSON `{group, applied:[{output, fallback, recorded}], words}`: each output back to its fallback (recorded as a new result, source `revert`); refused when none has a fallback or each stands on it | review | yes |
| `ALIGN CORNERS` | `ALIGN CORNERS <group> <x1> <y1> … <x4> <y4>`, `ALIGN CORNERS <group> CLEAR` | JSON `{group, fit, cameraCorners:[[x,y]…], words}`; corners make the group `FIT CORNERS`, `CLEAR` back to `FIT INSIDE`; a point outside 0–1, a crossed or clockwise-wrong quad, the same corners again refused | review | yes |
| `ALIGN CLEAR` | `ALIGN CLEAR <output>\|<group>` | `cleared the measured correction of …`: each output's correction taken off (a `Cleared` result in the ledger), what it had kept as its fallback; refused when none has one | review | yes |

Every ALIGN verb that changes something is refused while the show is armed, with the show gate's own words. `ALIGN PLAN`,
`ALIGN SCORE`, `ONLY`, the `NEUTRAL`/`COLOR`/`VERIFY` steps, `COLOR OFF|WHITE`, `SHAPE CYLINDER` and `DEVICE NATIVE` left the
catalogue (next round).

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
"outputs": [{ …, "measured": null | {"run":"ar…","residualPx":0.07,"gamma":2.2|null,"colour":false,"blend":"replace"|null,
                                     "files":"ok|loading|<file> missing — <output> runs without its measured correction",
                                     "light":"green|grey|red"} }]
```

A group is red when an output's files are missing, amber while its run runs or a run waits to be accepted, otherwise the light
of its accepted grade (grey: not measured yet). The fixture is `tests/fixtures/state/align.json`, its cards `align.cards.json`.

## Context, events, preflight, INDEX

- Context: `align:<group>` (kind `align`) with the group's light and words.
- Events: `align.accepted` `{group, run}`.
- Preflight section `align`: each output with a measured correction — red `missing` (a file missing, outside the folder or
  changed; fix `ALIGN REVERT <group>` when it has a fallback, else `ALIGN CLEAR <output>`), red `not loaded`, amber `staged`
  (`ALIGN ACCEPT <run>`), green `ok`; one green row when no output has one.
- INDEX section `align`: a row per group (light, state `none|running|staged|A 100`); keys Align (`ALIGN RUN`), Accept
  (`ALIGN ACCEPT`), Revert (`ALIGN REVERT`, hold).

## Files

A run's folder `align/<run>/` beside the project, written whole and never again: `run.json` (state, camera, fit, words, seam,
coverage, white p5/p95, the flat fit's residual, the score with its terms, each output's γ, decoded share, residuals and its
measured correction) and per output `warp-<output>.pfm` (the grid: u, v, valid per node) and `blend-<output>.pfm` (weight,
pedestal, uniformity gain at a quarter of the raster). The project names each file with its hash; a file is read only inside the
project's folder.

## Papers note

For the integration docs pass:

- `WIRE.md`: the rows above, STATE `align` and `outputs[].measured`, the context kind `align`, event `align.accepted`, preflight
  and INDEX sections `align`; the trimmed rows (`ALIGN PLAN`, `ALIGN SCORE`, `DEVICE NATIVE`, `ONLY`, colour steps, cylinders).
- `MAPPING.md`: camera auto-align of flat screens (`docs/ALIGN.md`): identify, scan, the joint lens-and-homography flat fit and its
  planarity test, rectification, the canvas target, the measured warp grid and the measured blend in light with its pedestal,
  uniformity and per-output γ; results in the calibration ledger with fallbacks.
- `ARCHITECTURE.md`: `AlignRunner` (`IAlignBackend` in `EngineServices.Align`, added by the align part with the render host as its
  display), `MeasuredLibrary` (files read off the lane and the render thread, a set published whole), `MeasuredStage` in the output
  stage (the measured grid as the first warp layer; the `output` program's measured hunk); a node composed without the align part
  refuses a run in words.
- `ROADMAP.md`: flat-screen auto-align done (simulator-verified; a real camera and projectors are a lab check); next round:
  grouped capture, curved screens, the colour match, the projectors' own processing, the shutter knock, the free-form hand-over,
  vendor audit, the warm-up gate, two cameras, camera control, `ALIGN PLAN`, the desk's Align tab.
