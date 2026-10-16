# wp2-resolve on the wire

Re-solving the projector that moved (`docs/CALIBRATION.md`) and align's rescan of one output, white match, verify pass, score
and double-correction guard (`docs/ALIGN.md`). wp2-resolve continues wp2-wall and wp2-align: its rows live in their catalogue
blocks and their pages (`docs/wire/wall.md`, `docs/wire/align.md`); this page says which forms, rows and keys are its own.

## Verbs

| Verb | What it adds |
|---|---|
| `CALIBRATION RESOLVE <projector>…\|ALL [CAMERA <camera>] [STAGE]` | `CAMERA`: a fresh look at only the named projectors through a calibrated camera — attribution per projector, only the movers re-solved from their dots and checked on the wall; pins follow the pose and pixel quads are re-seated; a projector that blends automatically has its blend solved again off the lane, or in a blind draft when the draft lands (`docs/wire/wall.md`); a phone rig is looked through via the phone hub; a folder camera is refused (its pictures come one per pattern, shot by hand or by a tether); a look and a scan never run together — each claims itself, then asks whether the other is under way ("a scan is running — a re-solve waits for it (SCAN CANCEL)", "a camera re-solve is looking through … — the scan waits for it (CALIBRATION CANCEL stops it)") |
| `ALIGN RUN <group> [ONLY <output>…] [STEPS SCAN,SOLVE,COLOR\|ALL] [FIT INSIDE\|CORNERS] [COLOR OFF\|WHITE]` | `ONLY`, the `COLOR` step and `COLOR WHITE`; every run ends with the verify pass and says its pictures and pattern time |
| `ALIGN SCORE <run>\|<group>` | a run's score with every term's words, value, light and fix |
| `DEVICE NATIVE <device>` | a projector's own geometry and edge blend read live (Panasonic), queries only, with the `DEVICE RAW` line that turns off what fights a measured correction |

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

## Not in the catalogue

`ALIGN PLAN` (the dry run) and `OVERLAY GRID` have no row; every row above is routed and answered.

## Next

The LENS, CODED and DENSE rungs of a camera re-solve (a mover whose pose alone does not fit), re-solving the camera when it
moved, a stored reference look, the drift monitor, lens read-back triggers and before/after on the wall; FULL colour (the
primaries matched, not only white) and curved screens; a snapshot, neutral and restore of a projector's own processing, and its
read on protocols other than Panasonic's; overlay names and pins, and the grid.
