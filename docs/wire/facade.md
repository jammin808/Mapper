# wp2-facade on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `SITE` | `SITE` | The site frame: coordinate system, grid origin, rotation, scale and mains frequency | low | no |
| `SITE ORIGIN` | `SITE ORIGIN <e> <n> <h> [CRS <code>\|LOCAL] [ROTATE <deg>]` | Sets the site frame; the whole stage moves with it in one edit | review | yes |
| `SITE MAINS` | `SITE MAINS 50\|60` | The venue's mains frequency | low | yes |
| `SITE POINTS` | `SITE POINTS <path> [ORDER PENZD\|PNEZD\|ENH\|NEH] [CRS <code>] [UNITS m\|cm\|mm\|ft\|usft]` | Imports surveyed control points; answers their ids and the lines it skipped | review | yes |
| `SITE CHECK` | `SITE CHECK [<surface>\|<survey>]` | Measures the model against the control points: each point's distance and the fit | low | no |
| `SURVEY IMPORT` | `SURVEY IMPORT <path> [CRS <code>\|LOCAL] [UNITS m\|cm\|mm\|ft\|usft] [UP Z\|Y] [VOXEL <m>] [DECIMATE <triangles>\|OFF] [NAME <name>]` | Imports a surveyed model or point cloud (OBJ, PLY, glTF, LAS) into the site frame, off the control lane; answers its id | review | yes |
| `SURVEY LIST` | `SURVEY LIST` | Imported surveys with provenance and state | low | no |
| `SITE GUIDE` | `SITE GUIDE` | The facade workflow as steps (survey, control points, pins, priors and solve, stacks, blend, score), each with its state and the line that does it | low | no |
| `PROJECTOR PRIOR` | `PROJECTOR PRIOR <projector> THROW <ratio> [±<pct>] \| PROJECTOR PRIOR <projector> AT <control> [±<m>] \| PROJECTOR PRIOR <projector> AT <x> <y> <z> [±<m>] \| PROJECTOR PRIOR <projector> CLEAR` | What a solve may assume about a projector's lens and position | review | yes |
| `PLAN LIGHT` | `PLAN LIGHT [<surface>\|ALL] [AMBIENT <lux>] [ALBEDO <0-1>] [TARGET <lux>] [DERATE <0-1>]` | Predicted illuminance, luminance, contrast and pixel pitch per surface from the projectors as placed | low | no |
| `STACK NEW` | `STACK NEW <name> <projector> <projector> [<projector>…] [MASTER <projector>]` | Groups projectors lighting one picture (the blend treats them as one); answers its id | review | yes |
| `STACK ALIGN` | `STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]` | Fits each member's measured residual to the master's picture from a shared scan | review | yes |
| `STACK MATCH` | `STACK MATCH <stack> FROM <scan>` | Matches the members' colour gains from the scan's white frames | review | yes |
| `STACK FAILOVER` | `STACK FAILOVER <stack> HOLD\|MATCH` | What the stack does when a member fails: hold the rest, or dim its blend group to match | review | yes |
| `STACK LIST` | `STACK LIST` | Stacks with members, residual, gains and degraded members | low | no |

## STATE

`stage.site`, `controls`, `surveys`, `stacks`; `stage.projectors[].uncertainty`, `priors`, `stack`.

## Papers note

(What shipped, for the integration docs pass.)
