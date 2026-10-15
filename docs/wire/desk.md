# wp2-desk on the wire

The desk adds no verb and routes none: it is a client of the catalogue like Patterns, Companion and an assistant. Every
line it sends is checked by `DeskVerbRuleTests` (it parses, a catalogue row matches it, it carries no secret).

## What the desk asks

| Line | When | What it reads |
|---|---|---|
| `VERBS` | once at attach (quiet) | the names the node runs: a control whose verb is not among them hides |
| `FX KINDS` | once the node's verbs are known (quiet) | `[{id, name, family, …}]` for Add effect ▸ |
| `MOD SOURCES` | once the node's verbs are known (quiet) | `[{source, determinism, words}]` for Modulate ▸ |
| `DISPLAYS` | when the Outputs drawer opens | unchanged from round 1 |

## STATE it reads

`show` (armed, by, since, rules, panic, locks, refused, preflight), `air.panic`, `air.master`, `air.pattern` (an object:
`{kind, outputs}`), `air.overlay.on`, `control.learning`, `videoio.inputs`, and every block a card descriptor names.

## The fake engine

`FakeEngineLink` answers, with the engine's meaning and STATE shapes: `SHOW ARM/DISARM/SET/STATUS`, `PANIC` (a second press
within the fade is HARD), `PANIC CLEAR`, `LOCK`, `UNLOCK`, `LOCKS`, `MASTER`; `ASSISTANT STATUS/SET`, `PROPOSALS`, `APPROVE`,
`REJECT`, `ASK`; `FX KINDS`, `FX ADD`, `MOD SOURCES`, `MOD BIND`, `MOD UNBIND`, `BIND LEARN [OFF]`; `VERBS` as the engine's JSON
rows. While armed it refuses with the Core show policy's words for the desk, as the engine's gate does.
