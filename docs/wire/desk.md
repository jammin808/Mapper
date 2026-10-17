# wp2-desk on the wire

The desk adds no verb and routes none: it is a client of the catalogue like Patterns, Companion and an assistant. Every
line it sends is checked by `DeskVerbRuleTests` (it parses, a catalogue row matches it, it carries no secret).

## What the desk asks

| Line | When | What it reads |
|---|---|---|
| `VERBS` | at attach and whenever the link comes back live (quiet) | the names the node runs: a control whose verb is not among them hides |
| `WHOAMI` | with `VERBS` (quiet) | `role` (the show policy's role for this desk), `session` and `transport` (STATE.show.blind names its draft's owner the same way), `paired` |
| `FX KINDS` | once the node's verbs are known (quiet) | `[{id, name, family, …}]` for Add effect ▸ |
| `MOD SOURCES` | once the node's verbs are known (quiet) | `[{source, determinism, words}]` for Modulate ▸ |
| `DISPLAYS` | when the Outputs drawer opens | unchanged from round 1 |

## STATE it reads

`show` (armed, by, since, rules, panic, locks, refused, preflight, blind: its owner's session and transport, its changes, and
while a commit waits its `when`), `fleet` (role, of, door), `air.panic`, `air.master`, `air.pattern` (an object: `{kind,
outputs}`), `air.overlay.on`, `control.learning`, `scan.state`, `align.run.state`, `calibration.task.state` (in Core's
`PatternTasks` words), `videoio.inputs`, and every block a card descriptor names. `sync.offsetMs` shows as the leader's time
± that many ms (half the round trip); `outputs[].dropped` as late presents in 10 s, with `latencyMs` (the display's lag the node
holds the output to) and `present.path`.

## The gate

Before a press the desk asks the engine's doors in the engine's order, with the engine's own Core rules and words: the router's
pairing (`WHOAMI`'s `paired`, the words `WireWords.NotPaired`); the fleet's (Core's `BackupGate` with `fleet.role` and the words
`fleet.door` carries); an open blind draft refuses `PROJECT OPEN` and `PROJECT NEW` (`BlindWords.DraftOpen`); the show policy for the
desk's role from `WHOAMI`, with this desk as the draft's owner when `show.blind` names its session and transport (its document
edits then go to its draft, refused only while the commit waits: `BlindWords.Landing`); the network area's lock
(`LockGuard.AreaWords`, the node's own words); then `LockGuard.Check` on the mirrored project with the edit a `SET`, `ADD`,
`REMOVE` or `MOVE` would make (the armed show's implicit areas left out for a draft's edit, as the draft's guard leaves them), and
for any other line the area its class edits and the entity it names first. A control the node would refuse greys with those
words; the node still decides.

## The fake engine

`FakeEngineLink` answers, with the engine's meaning and STATE shapes: `SHOW ARM/DISARM/SET/STATUS`, `PANIC` (a second press
within the fade is HARD), `PANIC CLEAR`, `LOCK`, `UNLOCK`, `LOCKS`, `MASTER`; `BLIND ON`, `BLIND DISCARD`, `BLIND COMMIT` for the
desk's own draft; `ASSISTANT STATUS/SET`, `PROPOSALS`, `APPROVE`, `REJECT`, `ASK`; `FX KINDS`, `FX ADD`, `MOD SOURCES`, `MOD BIND`,
`MOD UNBIND`, `BIND LEARN [OFF]`; `VERBS` and `WHOAMI` as the engine's JSON. Its doors are the engine's, in order and with the
same words: pairing, the fleet's door (the backup fixture's `fleet.door`), the open draft, the show policy for the desk, the
network lock, and `LockGuard` on its store. It carries a line on a live or a stale link, as the wire client does.
