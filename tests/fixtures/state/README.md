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
| `light` | A path to a light word; `{ "path", "red": [...], "amber": [...], "green": [...] }`; or rules `[{ "if": <path>, "light" }, { "from": <path> }, { "light" }]` |
| `words` | A path to words, or a template: `{field}`, `{field?then:else}` (either side may hold `{field}`), `{count}` |
| `fields` | Up to six `{ "label", "path", "unit"? }` (more are not shown) |
| `actions` | Verb lines, or `{ "label", "line" }`; `$(field)` and `{field}` are the element's (or its owner's) field |

Paths are tried relative to the card's block first, then from the STATE root; a path starting `/` is from the root.
