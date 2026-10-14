# wp2-align on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `ALIGN NEW` | `ALIGN NEW <name> <camera> <output>…\|ALL [CANVAS <canvas>\|NEW] [SHAPE AUTO\|FLAT\|CYLINDER]` | Outputs aligned together through one camera onto one canvas; answers its id | review | yes |
| `ALIGN PLAN` | `ALIGN PLAN <group>` | A dry run: camera, outputs, devices and their own processing, steps and time; nothing reaches the wall or a projector | low | no |
| `ALIGN RUN` | `ALIGN RUN <group> [ONLY <output>…] [STEPS NEUTRAL,SCAN,SOLVE,COLOR,VERIFY\|ALL] [FIT INSIDE\|CORNERS] [COLOR OFF\|WHITE]` | Measures the outputs through the camera and stages a correction (warp, blend, colour) with its score; nothing changes on the air until ALIGN ACCEPT | high | yes |
| `ALIGN STATUS` | `ALIGN STATUS` | The running or last run | low | no |
| `ALIGN CANCEL` | `ALIGN CANCEL` | Stops a run and restores what it changed on the projectors | low | yes |
| `ALIGN SCORE` | `ALIGN SCORE <run>\|<group>` | A run's score and every term with its words and fix | low | no |
| `ALIGN ACCEPT` | `ALIGN ACCEPT <run>` | Applies a staged run to its outputs as one edit; the previous corrections become the fallbacks | review | yes |
| `ALIGN REVERT` | `ALIGN REVERT <group>` | Back to each output's fallback correction | review | yes |
| `ALIGN CORNERS` | `ALIGN CORNERS <group> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> \| ALIGN CORNERS <group> CLEAR` | The screen's corners in the camera picture (0–1), for an exact canvas | review | yes |
| `ALIGN CLEAR` | `ALIGN CLEAR <output>\|<group>` | Removes measured corrections from outputs | review | yes |
| `DEVICE NATIVE` | `DEVICE NATIVE <device>` | A projector's own geometry and edge blend, read live (Panasonic this round; others answer with words), for the double-correction guard | low | no |

## STATE

top level `align`; `outputs[].measured`.

## Papers note

(What shipped, for the integration docs pass.)
