# wp2-led on the wire

LED pixel mapping (`docs/LED.md`). A `<fixture>` is a fixture's id (`lf…`) or name; a `<node>` is an LED node's id (`ln…`) or name;
`#<tag>` names the fixtures carrying that tag. `<universe>[.<address>]` is an Art-Net Port-Address (0–32767) or an sACN universe
(1–63999) and a slot (1–512, default 1); a DDP device has no universes and is patched at `0.<byte>`.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `LED LIST` | `LED LIST` | JSON `{output, nodes:[{id,name,protocol,host,state,universes,fixtures,why}], fixtures:[{id,name,type,space,pixels,patch}], pixels, universes, conflicts:[…], mbps, refused}` | low | no |
| `LED NEW` | `LED NEW LINE\|GRID\|RING <count>[x<rows>] [ON canvas:<id>\|output:<id>\|surface:<id>\|source:<ref>] [TYPE <type>] [<name>]` | the fixture id; effect `added fixture lf… <name> (170 px on canvas cv…, unpatched)`. With no ON: the first canvas, else the first output. A line runs across the middle of the space, a grid fills it, a ring sits in its centre | low | yes |
| `LED IMPORT` | `LED IMPORT <path> [AS CSV] [ON canvas:<id>\|output:<id>\|surface:<id>\|source:<ref>] [SCALE <x>] [NODE <node>]` | JSON `{fixtures, types, nodes, pixels, ids, warnings}`: a CSV pixel list on this machine (MADRIX/Capture columns: Fixture or Display Name, Pixel or Index, X, Y, Z, Universe, Channel, DMX Address, Type), one points fixture per name in pixel order, positions × SCALE, patched from its first pixel's address to NODE (or the rig's only node); one undo step; errors name the line and column. Off the lane | review | yes |
| `LED PATCH` | `LED PATCH <fixture> <node> <universe>[.<address>] [FORCE]` | the range (`PixLite 3.1–3.510`); refused when it would overlap another fixture (both named, with the slots) or push the rig past the bandwidth guard, unless `FORCE`; a 16-bit type on a WLED device is refused | review | yes |
| `LED AUTOPATCH` | `LED AUTOPATCH [<fixture>\|#<tag>\|ALL] [NODE <node>…] [FROM <universe>[.<address>]] [EACH ON\|OFF]` | JSON `{universes:"0–25", pixels, fixtures, conflicts}`; one undo step; "already patched so" when nothing would change. Default FROM: the first node's first universe, or (for part of the rig) after what the rest of the rig uses on it | review | yes |
| `LED OUTPUT` | `LED OUTPUT ON\|OFF [<node>\|ALL] [HOLD]` | no node or ALL: the machine master (`NodeSettings.led.output`, written to the node's settings file; off on a fresh node). OFF sends one black frame (colour and dimmer slots 0, constants kept), sACN adds three terminations; HOLD stops without them. With a node: its `enabled` (an edit). ON needs a renderer ("no renderer on this node — LED output needs the GPU") | high | yes |
| `LED TEST` | `LED TEST CHASE\|ORDER\|UNIVERSE\|FIXTURE\|GRADIENT\|WALK\|WHITE\|FULL\|BLACK\|OFF [ON <node>\|<fixture>\|#<tag>\|ALL]` | the pattern; live (journalled, kept in the recovery record, never an undo step) | high | yes |
| `LED LOCATE` | `LED LOCATE <fixture> [<seconds 1-60>]` | blinks the fixture at 2 Hz, white ↔ its editor colour (10 s by default) | review | yes |
| `LED LEVEL` | `LED LEVEL <0-100> [FADE <ms>]`, `LED LEVEL BY <delta> [FADE <ms>]` | the rig's master level, faded on the show clock; live | high | yes |
| `LED MONITOR` | `LED MONITOR <node> <universe>` | JSON `{node, universe, leaderMs, slots:"<1024 hex>", fixtures:[{id,from,to}]}` — the bytes last **sent** (a DDP device's 512-byte block) | low | no |

**Refusals** leave the show unchanged: an unknown fixture, node, type, space or tag; an address out of range; a unit (head, pixel,
tail) larger than the node's universe; a fixture running past the protocol's last universe with no next node; a value that would
change nothing ("already patched so", "the LED level is already 50", "no LED test is up", "PixLite is already off").

**Generic edits.** `ADD lednode|fixturetype|fixture <json>` (a node starts off), `SET <id> <field> <value>` (`patch.universe`,
`host`, `rateHz`, `latencyMs`, `sacnPriority` …), `REMOVE`, `MOVE` (rig order is auto-patch order). A node's host is an IP address.

## STATE

`led` (end of STATE):

```json
"led": {"output": true, "level": 1.0, "fixtures": 12, "pixels": 4320, "universes": 26, "mbps": 38.4,
        "test": null | {"pattern": "chase", "on": "ALL"}, "locate": null | {"fixture": "lf…", "leftMs": 4200},
        "lagMs": 0.4, "lateCollects": 0,
        "nodes": [{"id": "ln…", "name": "PixLite A", "protocol": "artnet|sacn|ddp", "host": "10.0.0.21",
                   "state": "off|disabled|idle|sending|late|unreachable|refused", "fps": 44.0, "universes": 8, "lateMs": 0.2,
                   "seen": null | {"name": "PixLite16", "refreshHz": 44, "ageS": 1.2}, "why": ""}],
        "conflicts": [{"node": "ln…", "universe": 3, "from": 1, "to": 48, "fixtures": ["lf…", "lf…"]}],
        "warnings": ["Halo is a world fixture — world sampling is next round, black for now"]}
```

A node is `sending` only once frames have left for it; `late` when the 95th percentile of its lateness over two seconds passes one
frame at 60 Hz; `unreachable` when a send failed in the last two seconds or a node that answered ArtPoll has been silent 10 s;
`refused` when the bandwidth guard refuses the rig. `lagMs` is the worst node's lateness; `lateCollects` counts GPU read-backs
taken a frame late. Fixture: `tests/fixtures/state/led.json`, cards `led.cards.json`.

## Context, INDEX, preflight, hello

- Context: one item `led:<node>` (kind `led`): red unreachable or refused while this machine sends, amber late or a test or locate
  up, green sending, grey otherwise; `output:<id>` **feeds** `led:<node>` when a fixture on the node samples that output.
- INDEX section `led`: a row per node; preset keys On, Off, Test (chase on the node), Level ± (`LED LEVEL BY ±5`).
- Preflight section `led`: every patched Art-Net node on this machine answered ArtPoll (red with `LED OUTPUT OFF <node>` when it
  did not); overlaps red with `LED AUTOPATCH ALL`; the guard's refusal red; output off with a patched rig amber with
  `LED OUTPUT ON`. sACN and DDP receivers do not answer: grey.
- Hello caps: none of its own. The hello keeps the sixteen a strict reader keeps; a peer learns of LED output from `STATE.led`
  and the context's `led:<node>` items.

## Papers note

Shipped this round: Canvas, Output, Surface (UV and canvas-region mappings) and Source sampling, point and area, in linear light on
the GPU; colour orders, RGBW, 16-bit, gains, gammas, brightness, dimmer and constant channels; Art-Net 4 (on the shared socket),
sACN (unicast or multicast, sync universe, terminations) and DDP (WLED 8-bit) output decimated on the leader grid and latched;
patch, auto-patch, overlaps, the bandwidth guard; tests, locate, level, blackout/freeze/air, STATE, context, INDEX, preflight.
`LED IMPORT AS CSV` shipped (stretch); MVR and GDTF import and `LED DISCOVER` are next round (`LED DISCOVER` is not routed).
