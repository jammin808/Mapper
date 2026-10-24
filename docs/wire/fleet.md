# wp2-fleet on the wire

What machine health, the fleet board and the context's order for Patterns' Eye add to the wire. Every row below is routed (VERBS
marks it `"available"`). The hot-backup rows after them in the catalogue (`FLEET BACKUP`, `FAILOVER`, `MIRROR` …) are in
[backup.md](backup.md).

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `HEALTH` | `HEALTH` | This machine's health in one record: outputs, render, CPU, memory, disk, GPU, network, sync, devices, media (unknown is −1) | low | no |
| `FLEET` | `FLEET [STATUS]` (alias `EYE`, `EYE STATUS`) | Every PlayMap machine this node hears as one board in Patterns' Eye shape: lights by evidence, headline, problems | low | no |
| `FLEET FOCUS` | `FLEET FOCUS <machine\|output\|id\|label>` (alias `EYE FOCUS`) | Focuses the board on a thing and its neighbours (a view, not journalled) | low | no, paired |
| `FLEET LENS` | `FLEET LENS ALL\|OUTPUTS\|PROJECTORS\|SYNC\|BACKUP\|PROBLEMS` (alias `EYE LENS`) | Shows one kind of thing on the board | low | no, paired |
| `FLEET NEXT` | `FLEET NEXT` (alias `EYE NEXT`) | Focuses the next problem (reds, then ambers) | low | no, paired |
| `FLEET PREV` | `FLEET PREV` (alias `EYE PREV`) | Focuses the previous problem | low | no, paired |
| `FLEET RESET` | `FLEET RESET` (alias `EYE RESET`) | The board's view from before the focus | low | no, paired |

`SUBSCRIBE HEALTH` (the spine's row) opens the HEALTH topic: an open read (unpaired sessions may), the record at once, then at least
once a second and at once when any light moves (the machine's, a category's, an output's); latest wins. `SUBSCRIBE HEALTH <words>`
is refused "SUBSCRIBE HEALTH takes no words after it".

The view verbs are ephemeral: no journal row, no project revision. A move that changes nothing is refused with the reason: "already
focused on machine:9f8e7d6c", "the board already shows problems", "no problems on the board: every thing is green or unknown",
"the board is at its own view already: nothing focused since the last reset", "nothing on the board answers to 'X' — FLEET lists
every machine, output and projector". `FLEET FOCUS self` names this node's own machine (the same key on every node).

## Replies

- `HEALTH` — `{"v":1,"node","name","at","role","of","epoch","mirror","build","light","headline","outputs":[{"id","name","light","open","fps",
  "dropped","missed60s","path","shownFrames","observedFrames"}],"projectors":[{"id","name","light","words","output"}],
  "render":{"renderer","path","resets"},"gpu":{"epoch","phase","since"},
  "cpuPct","memMb","memFreeMb","diskFreeGb","gpuPct","gpuTempC","netMbps","uptimeS","sync":{"role","leader","group","uncertaintyMs",
  "freewheelS"},"devices":{"total","green","amber","red"},"media":{"items","missing"},"watchdog":{"laneMs","renderMs"},"rev",
  "categories":[{"name","light","words"}]}`. `build` is the node's whole build identity in SemVer: `0.2.0+17.abc1234` for CI's
  build 17 of commit abc1234, `0.2.0-dev+abc1234` for a local build (README, "Versions"); STATE's `version` is the product
  version alone. `shownFrames` is the frames an output's display has shown and `observedFrames` the frames its independent
  observer decoded; `gpu` is the GPU device's epoch, its phase (`starting`, `running`, `rebuilding`, `unknown`) and since when (Unix
  ms). Each is −1 (the phase `unknown`) until the node measures it, and a line from a node that sends none of them reads so. Categories,
  always all eleven in this order: outputs, render, cpu, memory, disk, gpu,
  network, sync, devices, media, watchdog. A number not measured is −1 and its category grey with words that say why ("GPU load
  and temperature are not readable on this machine"). The machine's light is the worst known category (red over amber over green;
  grey says nothing). The light table is `HealthLights` (src/PlayMap.Core/Fleet/HealthRecord.cs): CPU 80/95 %, free memory
  1.5 GB/512 MB, free disk 10/2 GB, GPU 90/98 % or 80/90 °C, network 900 Mbps (amber only), a follower's clock ±2/10 ms, the lane's
  worst tick gap 60/250 ms, the frame p95 40/100 ms; an output not open is red, one that dropped or missed a vblank amber.
- `FLEET` / `EYE STATUS` — the board in Patterns' `EyeJson` field names and order: `{"headline","worst","worstLight","worstWords",
  "counts":{"green","amber","red","grey","things","links"},"focus","lens","problems":[…],"nodes":[{"id","kind","plane","tier","label",
  "sub","light","x","y","w","h","words":[…],"page","item","wire":"EYE FOCUS <id>","visible","emphasis"}],"edges":[{"from","to","kind",
  "light","words"}],"replay":false,"replayAt":"","moment":"","machines":[{"id","name","role","light","headline","heard","self",
  "outputs":[…],"projectors":[…]}]}`. Things: `machine:<node>` (tier 1), `output:<node>:<id>` (tier 2), `projector:<node>:<id>`
  (tier 3, its device's facts folded in), all on the video plane. Links, only the Eye's verbs: machine **shows** output, output
  **drives** projector, backup machine **mirrors** primary machine, follower machine **follows** leader machine. `visible` is the
  thing under the current lens; `emphasis` is 1 / 0.55 / 0.25 by hops from the focus.
- The view verbs — `{"focus":"machine:c0ffee00","lens":"problems"}`.

## The web port

`GET /health` (and a WebSocket on `/health`: the record pushed as `SUBSCRIBE HEALTH` pushes it) and `GET /fleet` (the board) are
web modules admitted before the token gate, on purpose: they are open reads like the hello — names, counts and lights, never a
secret, a token or a file's content — and they change nothing (anything but GET or HEAD is refused 403 "the fleet's pages are
read-only …"). The `/health` WebSocket feeds at most 64 sockets at once; one more is answered 503 with words (a GET still reads
the record). A WebSocket whose Origin names another site is refused 403 (a fleet monitor sends no Origin; a browser always does), so
a stranger's page cannot hold the feeds. They sit outside `/api/…`, which the web port keeps for its own routes behind the gate
(no module serves under it).

## STATE

Top level, after `show`: `fleet` then `eye`.

```jsonc
"fleet": {"role":"solo","epoch":0,"state":"serving","of":null,"backups":[],"mirror":null,"succession":[],"alarm":null,"last":null,
          "nodes":[{"id":"9f8e7d6c","name":"FOH-B","role":"backup","light":"amber","headline":"…","heard":"live|stale|gone","build":"…","rev":12}],
          "light":"red","headline":"3 machines · 3 outputs · 1 red · 2 amber — FOH-C: gone quiet 7 s","routes":[],"routeTest":null},
"eye":   {"headline":"…","worst":"machine:c0ffee00","worstLight":"red","worstWords":"FOH-C: gone quiet 7 s",
          "counts":{"green":2,"amber":2,"red":1,"grey":1,"things":6,"links":5},"focus":"machine:c0ffee00","lens":"problems",
          "problems":["machine:c0ffee00","machine:9f8e7d6c","output:9f8e7d6c:op_1"]}
```

`nodes` are the other machines this node hears (this node is STATE itself); `light` and `headline` are the whole fleet's, this node
included. `role`, `epoch`, `state` and `of` are the node's place in a hot-backup pair as it keeps it; `backups`, `mirror`, `alarm` and `last` belong
to the hot backup and are described in [backup.md](backup.md). The fixture is `tests/fixtures/state/fleet.json`, its cards
`fleet.cards.json`. STATE.peers and the context's peers carry each PlayMap peer's own light and headline from its HEALTH.

## The context (Show Bus §6) for Patterns' Eye

A `machine` item (second, after `show`): the worst of this node's HEALTH, with its role, build (HEALTH's `build`: "solo · build
0.2.0+17.abc1234 · …") and render path. The items are
ordered for the Eye's twelve (Patterns draws reds, then ambers, then the given order): `show`, `machine`, the outputs (six one by
one; beyond six an `outputs` item for the green ones and every other output by itself), the projectors (each with its device's facts
folded in), the playlists that play (two or more idle ones as one `playlists` item), `mirror:*`, `sync*`, `media`, `preflight`,
`link:*`, the rest, `fault:*`; what an aggregate stands for and the folded devices follow, still in the context. An aggregate gets
`show` **controls** `outputs`.

## Hello

The caps include `eye` and `show` (16 in all).

## INDEX

Section `eye` (preset title "Eye", row kind `machine`, one key per row sending the row's id): `FLEET NEXT`, `FLEET PREV` (lit by the
board's worst light, state the number of problems), `FLEET LENS PROBLEMS`, `FLEET LENS ALL`, `FLEET RESET`, and `FLEET FOCUS
machine:<node>` per machine with its light (`FLEET FOCUS self` for this node).

## Preflight

Section `fleet`: a row per expected machine (known before a restart, in the show's fleet plan, or heard now) — green when its health
is live, amber quiet, red gone or never heard, fix `NODES`; a row per peer's certificate — green pinned, amber not pinned yet, red
refused (changed, or not the hello's) with the pin file to edit; one grey row when the monitor is off.

## Next

The metrics ring and `FLEET AT` replay, the `/fleet` page, webhooks and syslog, GPU temperatures through NVML, the hello's
`role`/`epoch` fields. Patterns' side (P1 the "PlayMap" kind's words,
P2 reading every peer's context with the `context` cap, P3 picture things on the Video band) is a patch with tests, not applied
(docs/FLEET.md).
