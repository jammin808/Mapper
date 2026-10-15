# wp2-multiview on the wire

wp2-multiview is Core only: the multi-view solver, dense per-projector depth, the merged mesh and coverage
(`src/PlayMap.Core/Scan/MultiView/`, described in `docs/SCAN-MULTIVIEW.md`). It routes no verb and adds no STATE.

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| — | — | (no rows of its own) | | |

`SCAN SOLVE`, `SCAN VIEWS` and `SCAN DROP` are wp2-multiscan's rows; they call `MultiViewCalibration.Solve`, `Influence`
and `DropView`.

## STATE

None.

## Papers note

For `MAPPING.md` (scanning): a scan can have many views. Each view is solved with every other, anchored on the projectors
(a projector pixel names the same surface point from every view), so several projectors on curved surfaces come out in one
frame with the camera's optics; each projector gets a dense depth map from every view that saw it; the projectors' meshes
join into one stage mesh with each surface once; coverage says how much of each projector's light the views saw; regions
are found per projector and joined across projectors, so a surface two projectors light is one region listing both. A view that
disagrees with the rest is found by its influence (the residual without it) and dropped warm, keeping the frame. Holding
projectors keeps them bit for bit and fixes the frame (two held make it metric). Measured on the simulator: projectors within
1.5 mm, 0.008° and 0.05 % throw from three phone views with an 8 % wrong focal prior; dense surface 0.27 mm RMS.
For `ARCHITECTURE.md`: `Core/Scan/MultiView` (pure computation, no engine hooks).
