# wp2-fx on the wire

*Generated from the catalogue by `tools/gen_stubs.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `FX KINDS` | `FX KINDS [<family>\|<search>]` | Every effect kind (built-in, this show's ISF, FFGL plugins) with its parameters, cost and traits | low | no |
| `FX LIST` | `FX LIST <target>\|ALL` | The effect chain of a layer, canvas, surface, output or playlist item, with each slot's state | low | no |
| `FX ADD` | `FX ADD <target> <kind> [AT <index>] [<name>]` | Adds an effect to a chain; answers its slot id | review | yes |
| `FX REMOVE` | `FX REMOVE <slot>` | Removes an effect | review | yes |
| `FX MOVE` | `FX MOVE <slot> <index>` | Moves an effect in its chain | review | yes |
| `FX SET` | `FX SET <slot> <param>\|mix\|blend\|scale\|name <value> \| FX SET <slot> <param> BY <delta>` | Sets an effect's parameter, mix, blend or scale | review | yes |
| `FX BYPASS` | `FX BYPASS <slot>\|<target>\|ALL ON\|OFF\|TOGGLE` | Bypasses effects (ALL ON is the panic outcome 'effects off') | review | yes |
| `FX RESET` | `FX RESET <slot>` | Sets an effect's parameters back to their defaults | review | yes |
| `FX IMPORT` | `FX IMPORT <path>\|<json>` | Adds an ISF effect to the show (content-addressed, carried with it); answers its asset id and the compile report | review | yes |
| `FX CHECK` | `FX CHECK <asset>\|<json>` | Compiles an ISF source for GLSL 3.30 and ES 3.00 and reports its issues by line | low | no |
| `FX PLUGINS` | `FX PLUGINS [RESCAN]` | The FFGL plugins this node found, each with its trust and probe state | low | no |
| `FX TRUST` | `FX TRUST <plugin> IN-PROCESS\|OFF` | Lets an FFGL plugin run inside the render process, or not (this machine only, never from an assistant) | high | yes |

## STATE

top level `fx`; `outputs[].fx`.

## Papers note

(What shipped, for the integration docs pass.)
