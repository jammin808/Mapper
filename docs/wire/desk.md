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

`show` (armed, by, since, rules, panic, locks, refused, preflight), `fleet` (role, of, door), `air.panic`, `air.master`,
`air.pattern` (an object: `{kind, outputs}`), `air.overlay.on`, `control.learning`, `videoio.inputs`, and every block a card
descriptor names. `sync.offsetMs` shows as the leader's time ± that many ms (half the round trip); `outputs[].dropped` as late
presents in 10 s, with `latencyMs` (the display's lag the node holds the output to) and `present.path`.

## The gate

Before a press the desk asks the engine's doors in the engine's order, with the engine's own Core rules: the fleet's (Core's
`BackupGate` with `fleet.role` and the words `fleet.door` carries), then the show policy for the desk's role from `WHOAMI`, then the
locks in `show.locks` with `LockGuard`'s words. A control the node would refuse greys with those words; the node still decides.

## The fake engine

`FakeEngineLink` answers, with the engine's meaning and STATE shapes: `SHOW ARM/DISARM/SET/STATUS`, `PANIC` (a second press
within the fade is HARD), `PANIC CLEAR`, `LOCK`, `UNLOCK`, `LOCKS`, `MASTER`; `ASSISTANT STATUS/SET`, `PROPOSALS`, `APPROVE`,
`REJECT`, `ASK`; `FX KINDS`, `FX ADD`, `MOD SOURCES`, `MOD BIND`, `MOD UNBIND`, `BIND LEARN [OFF]`; `VERBS` as the engine's JSON
rows. With a `fleet.door` in its STATE (the backup fixture) it refuses what the fleet's door refuses, with those words; while armed it
refuses with the Core show policy's words for the desk, as the engine's gate does.
