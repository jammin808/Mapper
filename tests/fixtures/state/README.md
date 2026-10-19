# STATE fixtures

Each package writes `<pkg>.json` (its STATE sections, from a real engine scenario, by one of its own tests) and
`<pkg>.cards.json` (one card descriptor per STATE block it adds). The desk's tests load every fixture into the fake engine
(`tests/PlayMap.App.Tests/StateFixtures.cs`), and the desk builds the descriptors into itself, so a package's STATE shows
on the desk without desk code and the desk's picture of STATE cannot drift from the engine's.

A card descriptor:

| Key | Meaning |
|---|---|
| `title` | The card's title |
| `path` | The STATE block (`a.b`, `a.0` for an index, `a[]` for one card per element) |
| `each` | `true`: one card per element of the array at `path` |
| `label` | The element field added to the title of a per-element card |
| `key` | The element field that names the entity the card is about (for the inspector), when not `id` |
| `light` | A path to a light word; `{ "path", "red": [...], "amber": [...], "green": [...] }`; or rules `[{ "if": <path>, "light" }, { "from": <path> }, { "from": <path>, "green": <word or [...]>, "red": … }, { "fixed": <light> }, { "light" }]` |
| `words` | A path to words, or a template: `{field}`, `{field?then:else}` (either side may hold `{field}`), `{count}` |
| `fields` | `{ "label", "path", "unit"? }`: the first six on the card's face, the rest under its "more" |
| `actions` | Verb lines, or `{ "label", "line" }`; `$(field)` and `{field}` are the element's (or an owner's) field, or the id of the owner reached by that key (`$(scan)` in `scan.regions.0`). A line takes every value from STATE: it carries no value only the fixture holds, and no slot of its row's syntax (`<scan>`) — a line that needs the operator's value is no button, and goes over the wire, MCP, Companion or a binding |

Paths are tried relative to the card's block first, then from the STATE root; a path starting `/` is from the root. A fixture of
several nodes (`backup.json`) is keyed by scenario, each a set of STATE sections; the desk's tests read each scenario.

A round-3 package's fixture (`machine`, `install`, `shaders`, `gpu`, `graph`, `observe`, `previews`, `showing`, `canvas`, `guides`,
`capture`) is the placeholder `{}` until the package's own test writes its sections: `RoundThreeFixtureTests` writes the placeholder
and holds that, while it stands, the package's STATE keys are absent and no card descriptor sits beside it — a descriptor file the desk
builds in describes at least one card from its fixture, so a package's descriptors arrive with the block they describe.
