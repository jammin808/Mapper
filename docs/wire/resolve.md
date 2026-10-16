# wp2-resolve on the wire

wp2-resolve continues wp2-wall and wp2-align: its rows live in their catalogue blocks and their papers
(`docs/wire/wall.md`, `docs/wire/align.md`); this file says which rows and keys are its own.

## Verbs

| Verb | What it adds |
|---|---|
| `CALIBRATION RESOLVE <projector>…\|ALL [CAMERA <camera>] [STAGE]` | `CAMERA`: a fresh look at only the named projectors through a calibrated camera — attribution per projector, only the movers re-solved from their dots and checked on the wall; pins follow the pose and pixel quads are re-seated; a projector that blends automatically has its blend solved again off the lane (`docs/wire/wall.md`) |
| `ALIGN RUN <group> [ONLY <output>…] [STEPS SCAN,SOLVE,COLOR\|ALL] [FIT INSIDE\|CORNERS] [COLOR OFF\|WHITE]` | `ONLY`, the `COLOR` step and `COLOR WHITE`; every run ends with the verify pass and says its pictures and pattern time |
| `ALIGN SCORE <run>\|<group>` | a run's score with every term's words, value, light and fix |
| `DEVICE NATIVE <device>` | a projector's own geometry and edge blend read live (Panasonic), queries only, with the `DEVICE RAW` line that turns off what fights a measured correction |

`ALIGN PLAN` (the dry run) is not in the catalogue (next round).

## STATE

- `air.overlay.cursorP95Ms`: how late a cursor move is drawn (frames from its line's arrival to the frame that first draws it,
  times the output's frame period), p95 over the last 300 moves; −1 before the first.
- `calibration.moved`: the projectors the last camera look found moved and that still stand where they were found.
- `calibration.task.state`: `capturing`, `matching`, `solving`, `verifying`, then `done`, `failed` or `cancelled`.
- `onboarding.contentAt`: stamped when the render host draws a frame of a quad's content (on a node without a render host, when
  the project first puts content on a quad an output shows).
- `align.devices`: each guarded device's last read of its own processing, with its light, words and fix.

## Context, preflight

- `device:<id>` is raised to amber with the guard's words and its `DEVICE RAW` line while the projector's own geometry or blend
  fights a measured correction on an output it shows.
- Preflight section `align`: an amber `double correction` row per such device, fixed by its `DEVICE RAW` line.

## Node settings

`wall.resolveDotSpacing` (64 projector px) and `wall.resolveFrameMs` (100: one dot pattern's hold on the wall, which a camera
re-solve's pattern time counts by).

## Papers note

For the integration docs pass:

- `WIRE.md`: the rows above (`CALIBRATION RESOLVE … CAMERA`, `ALIGN RUN … ONLY`/`COLOR`, `ALIGN SCORE`, `DEVICE NATIVE`), the
  STATE keys above, the context and preflight rows; `ALIGN PLAN` stays out of the catalogue (next round).
- `MAPPING.md`: re-solving only the projector that moved from a camera (`docs/CALIBRATION.md`: jittered dots, consensus matching,
  attribution, a pose-only solve from the stage, verification; pins and pixel quads carried along, the auto-blend solved again);
  the projector VIEW through its output's lens lookup on black; for flat screens (`docs/ALIGN.md`): `ONLY`, the white match, the
  verify pass and the score.
- `ARCHITECTURE.md`: `DotCheck`, `MotionAttribution`, `Reseat2D` and `StageSimulator` in Core; the camera re-solve in the engine
  shows its dots through the align display shooter (no scan-runner hunk was needed); `AlignVerify`, `ColourMatch` and
  `AlignCaptureFile` in Core; `Devices/Native` reads a projector's own processing; the render host measures cursor latency and
  witnesses the first content through the facts signal; `PreviewRequest.ProjectorId`.
- `ROADMAP.md`: the camera re-solve (QUICK rung: pose only), re-seating, the cursor's latency, the first map, the projector view
  with its lens, the white match, ONLY, the score with its verify pass and the double-correction guard done (simulator-verified;
  a real camera, projectors and a Panasonic's words are a lab check). Next round: the LENS, CODED and DENSE rungs, a camera re-solve
  when the camera moved, a stored reference look, the drift monitor, lens read-back triggers, FULL colour, curved screens,
  `INativeProcessing` with snapshot, neutral and restore, `ALIGN PLAN`, overlay names and the grid.
