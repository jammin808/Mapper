# wp2-fleet on the wire

What machine health, the fleet board and the context's order for Patterns' Eye add to the wire. Every row below is routed (VERBS
marks it `"available"`). The hot-backup rows after them in the catalogue (`FLEET BACKUP`, `FAILOVER`, `MIRROR` …) are wp2-backup's,
this package's continuation, and answer "… is not available on this node yet" until it routes them.

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

- `HEALTH` — `{"v":1,"node","name","at","role","of","epoch","build","light","headline","outputs":[{"id","name","light","open","fps",
  "dropped","missed60s","path"}],"projectors":[{"id","name","light","words","output"}],"render":{"renderer","path","resets"},
  "cpuPct","memMb","memFreeMb","diskFreeGb","gpuPct","gpuTempC","netMbps","uptimeS","sync":{"role","leader","uncertaintyMs",
  "freewheelS"},"devices":{"total","green","amber","red"},"media":{"items","missing"},"watchdog":{"laneMs","renderMs"},"rev",
  "categories":[{"name","light","words"}]}`. Categories, always all eleven in this order: outputs, render, cpu, memory, disk, gpu,
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
the record). The plan named them `/api/health` and `/api/fleet`; the web port keeps `/api/…` for its own routes behind the gate
(no module may serve under it), so they live at `/health` and `/fleet` until the web port's owner opens an `/api/<word>` door to
modules.

## STATE

Top level, after `show`: `fleet` then `eye`.

```jsonc
"fleet": {"role":"solo","epoch":0,"state":"serving","of":null,"backups":[],"mirror":null,"succession":[],"alarm":null,"last":null,
          "nodes":[{"id":"9f8e7d6c","name":"FOH-B","role":"backup","light":"amber","headline":"…","heard":"live|stale|gone","build":"…","rev":12}],
          "light":"red","headline":"3 machines · 3 outputs · 1 red · 2 amber — FOH-C: gone quiet 7 s"},
"eye":   {"headline":"…","worst":"machine:c0ffee00","worstLight":"red","worstWords":"FOH-C: gone quiet 7 s",
          "counts":{"green":2,"amber":2,"red":1,"grey":1,"things":6,"links":5},"focus":"machine:c0ffee00","lens":"problems",
          "problems":["machine:c0ffee00","machine:9f8e7d6c","output:9f8e7d6c:op_1"]}
```

`nodes` are the other machines this node hears (this node is STATE itself); `light` and `headline` are the whole fleet's, this node
included. `role`, `epoch`, `state` and `of` are read from the node's fleet settings; `backups`, `mirror`, `alarm` and `last` are
wp2-backup's and read empty here. The fixture is `tests/fixtures/state/fleet.json`, its cards `fleet.cards.json`. STATE.peers and
the context's peers carry each PlayMap peer's own light and headline from its HEALTH.

## The context (Show Bus §6) for Patterns' Eye

A `machine` item (second, after `show`): the worst of this node's HEALTH, with its role, build and render path. The items are
ordered for the Eye's twelve (Patterns draws reds, then ambers, then the given order): `show`, `machine`, the outputs (six one by
one; beyond six an `outputs` item for the green ones and every other output by itself), the projectors (each with its device's facts
folded in), the playlists that play (two or more idle ones as one `playlists` item), `mirror:*`, `sync*`, `media`, `preflight`,
`link:*`, the rest, `fault:*`; what an aggregate stands for and the folded devices follow, still in the context. An aggregate gets
`show` **controls** `outputs`.

## Hello

The caps gain `eye` and `show` (16 in all).

## INDEX

Section `eye` (preset title "Eye", row kind `machine`, one key per row sending the row's id): `FLEET NEXT`, `FLEET PREV` (lit by the
board's worst light, state the number of problems), `FLEET LENS PROBLEMS`, `FLEET LENS ALL`, `FLEET RESET`, and `FLEET FOCUS
machine:<node>` per machine with its light (`FLEET FOCUS self` for this node).

## Preflight

Section `fleet`: a row per expected machine (known before a restart, in the show's fleet plan, or heard now) — green when its health
is live, amber quiet, red gone or never heard, fix `NODES`; a row per peer's certificate — green pinned, amber not pinned yet, red
refused (changed, or not the hello's) with the pin file to edit; one grey row when the monitor is off.

## Papers note

For the integration docs pass (WIRE.md, SHOWBUS-NODE.md, ARCHITECTURE.md, ROADMAP.md):

- `HEALTH` exists (one record per machine, eleven categories, unknown −1 and grey, the light table above), with `SUBSCRIBE HEALTH`
  and `/health`; `FLEET`/`EYE` answer the board of every PlayMap machine this node hears in Patterns' `EyeJson` shape, with lenses,
  focus and the problems queue; STATE gains `fleet` and `eye`; INDEX gains `eye`; preflight gains `fleet`.
- Every node watches its peers' health over `/health` (WebSocket, TLS pinned by the hello's fingerprint, pins in
  `keys/fleet-pins.json`), at most 32, stale at 3 s and gone at 6 s; a gone machine stays on the board, red.
- The context is ordered for Patterns' Eye with `show` and `machine` first; the hello offers `eye` and `show`.
- Patterns' side (P1 "PlayMap" kind words, P2 reading every peer's context with the `context` cap, P3 picture things on the Video
  band) is prepared as a patch with tests, not applied (SHOWBUS-NODE.md should record it); a PlayMap peer's context is read over its
  line wire (`CONTEXT`, open) since `/api/context` is behind the token.
- Not yet: the metrics ring and `FLEET AT` replay, the `/fleet` page, webhooks and syslog, GPU temperatures through NVML, the hello's
  `role`/`epoch` fields, hot backup and failover (wp2-backup).
