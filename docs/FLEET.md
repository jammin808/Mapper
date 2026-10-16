# The fleet: health, the board and PlayMap on Patterns' Eye

Every PlayMap machine knows how it is (HEALTH), watches every other PlayMap machine it hears (the fleet monitor), and draws them all
as one board in the shape of Patterns' God's Eye (FLEET / EYE). Its context is ordered so that Patterns' Eye, which draws twelve
things of each app, draws the ones that matter. Lights are evidence: what was not measured is grey, never green. The wire's
verbs, replies and STATE are in [wire/fleet.md](wire/fleet.md).

## Health

`HealthRecord` (Core, pure) is one machine at one instant: its outputs (open, fps, dropped, missed vblanks, present path), its
projectors (with the device's power, shutter and input folded in), the renderer and its resets, CPU, this process's memory and the
machine's free memory, the media disk's free space, the GPU's load and temperature, network traffic, uptime, the clock sync's role
and uncertainty, the devices by light, the media files and how many are missing, and the watchdog: the control lane's widest tick
gap and the render's p95 frame time. Every number not measured is −1.

Eleven categories, always present, in this order — outputs, render, cpu, memory, disk, gpu, network, sync, devices, media, watchdog
— each with a light and words. The light table (`HealthLights`):

| Category | Amber | Red | Grey (unknown) |
|---|---|---|---|
| outputs | an output dropped frames or missed a vblank in the last minute | an output is not open | no outputs on, or not measured yet |
| render | the renderer reset | — | no renderer |
| cpu | ≥ 80 % | ≥ 95 % | not sampled |
| memory | under 1.5 GB free | under 512 MB free | not sampled |
| disk | under 10 GB free | under 2 GB free | not sampled |
| gpu | ≥ 90 % or ≥ 80 °C | ≥ 98 % or ≥ 90 °C | not readable on this machine (with the reason) |
| network | ≥ 900 Mbps | — | not measured |
| sync | a follower ± 2 ms, or free-wheeling | a follower ± 10 ms | not in a sync group |
| devices | a projector amber | a projector red | none linked |
| media | files missing | — | no media |
| watchdog | lane gap ≥ 60 ms or frame p95 ≥ 40 ms | lane gap ≥ 250 ms or frame p95 ≥ 100 ms | not timed |

The machine's light is the worst known category (red over amber over green; grey says nothing). Its headline is facts with no number
that moves while all is well: "FOH-A · primary · all green · 2 unknown", "FOH-B · backup · 1 amber: outputs — Left: 4 dropped, 1
missed in a minute".

The numbers come from the **machine sampler**, on its own one-second timer (never the control lane, never the render thread):
Linux reads `/proc/stat`, `/proc/meminfo`, `/proc/net/dev`, `/proc/uptime`, the home volume's free space, and the GPU from
`/sys/class/drm/card*/device` (`gpu_busy_percent`, a hwmon `temp1_input`) where the driver gives it; Windows reads system times,
`GlobalMemoryStatusEx`, `GetTickCount64`, the interfaces' bytes and the PDH counter `\GPU Engine(*engtype_3D)\Utilization Percentage`
(compile-only here; its test runs on the Windows CI job). NVIDIA's numbers on Linux and every GPU temperature on Windows need NVML
(next round), so they read grey with that reason. The lane's gap is measured on the engine's clock from the lane's second second on
(its first is the node starting). A node without the fleet's part (a bare engine) reads every machine number −1.

HEALTH is pushed (`SUBSCRIBE HEALTH`, the `/health` WebSocket) at once, once a second, and at once when any light moves — a clock
that runs only while someone watches. The `/health` WebSocket feeds at most 64 sockets at once (a monitor holds one per peer); one more
is refused with 503 and words, and a GET still reads the record; a socket a page from another site opens is refused.

The fleet's part also watches this machine's own lights once a second on the fleet's clock: when a category's, an output's or the
machine's light moves, the engine's facts move, so STATE (`fleet`, `eye`), the context's `machine` item and HEALTH's watchers say it
within the second with nothing else moving. Numbers that wander inside one light move nothing. Both clocks stop with the node.

## The fleet monitor

For every live PlayMap peer the hello hears (at most 32 subscriptions at once; a peer that comes back waits for a place like a new
one), the monitor opens the peer's `/health` WebSocket: `wss://` when the hello
offers TLS, the certificate checked against the pin (and the hello's fingerprint) and pinned on the first record that is the peer's
own; `ws://` when it offers none. A peer appears on the board when its first record arrives; its liveness is the silence since its
last record on the monitor's clock (an injected `TimeProvider`): stale at 3 s, gone at 6 s. A gone machine stays on the board, red
"gone quiet 12 s", until it is heard again — never removed silently. A subscription that ends is dialled again after 0.5 s, doubling
to 8 s while nothing is heard. A changed certificate is refused and shown red with words until the operator decides (the pins are
`keys/fleet-pins.json` beside the node's own): removing the machine's line pins its new certificate on its next record — the file
is read again whenever it changes, with no restart, and a pin set afterwards never writes the removed line back. The monitor keeps at most 64 machines (twice its subscriptions, at least 64): when it is
full, a machine it dialled and never heard (on no board) is let go to make room; a machine the board shows is never let go. The board changes (and STATE moves) only when what it shows moved — a light, a
headline, a liveness — never for a record that says the same.

## The board

`FleetGraph` (Core, pure) is Patterns' Eye method applied to the fleet: a `machine:<node>` per machine, an `output:<node>:<id>` per
output it shows, a `projector:<node>:<id>` per projector an output drives; backups **mirror** their primary, followers **follow**
their leader. A machine's things go grey with "not heard for 7 s" when it is not live. One row per machine in (role, name) order,
columns in signal order; the same facts give the same ids, order and places. The headline ("3 machines · 4 outputs · 1 red · 2 amber
— FOH-C: gone quiet 7 s"), the counts and the problems queue (reds, then ambers; machines before their things) are computed once.
Lenses: ALL, OUTPUTS, PROJECTORS, SYNC, BACKUP, PROBLEMS (the problems and what they touch); the focus dims things by hops (1, 0.55,
0.25). `FLEET` answers it in Patterns' `EyeJson` field names, so a reader written for Patterns' Eye reads PlayMap's.

## PlayMap on Patterns' Eye

Patterns draws another app's context as a card with up to twelve of its things — the reds, the ambers, then in the order given.
PlayMap gives them in this order: `show`, `machine`, the outputs (beyond six, the green ones as one `outputs` item), the projectors
with their devices folded in, the playlists that play (idle ones as one `playlists` item), mirrors, sync, media, preflight, links,
the rest, faults; what an aggregate stands for follows, still there for an assistant. Its links are only the Eye's eleven verbs.

Patterns draws PlayMap as a grey app heard on the network. Three changes on Patterns' side make PlayMap's things appear lit
(prepared as a patch with tests against Patterns' `ShowBusView` and `EyeGraph`, built and run in a scratch copy of Patterns, not
applied — Patterns' maintainers take it or not): **P1** its kind reads "PlayMap"; **P2** the context of every live peer
whose hello offers the `context` cap is read (a PlayMap peer's over its line wire, `CONTEXT` being an open read there, since
`/api/context` is behind the node's token) and drawn with the peer; **P3** another app's outputs, projectors, surfaces, playlists and
media sit on the Video band. PlayMap's own `PatternsEyeContractTests` read its context with a copy of Patterns' `ShowContext.Parse`
and the show-app section of `EyeGraph.Build` and check its things are drawn lit with its own lights.

## Preflight and INDEX

Preflight's `fleet` section: every expected machine heard (known before a restart, in the show's fleet plan, or heard now), every
pin valid. INDEX's `eye` section: NEXT and PREV PROBLEM, the PROBLEMS and ALL lenses, RESET, and a key per machine with its light.

## Threads

The sampler, the light watch and the HEALTH clock run on timers of their own; the monitor's subscriptions on the thread pool; the board is built from
published state on whichever thread asks (FLEET, STATE, INDEX), never on the lane; the lane only stamps its tick (one lock, no
allocation).
