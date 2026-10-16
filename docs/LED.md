# LED pixel mapping

PlayMap drives LED fixtures from the same pictures the projectors show: a strip laid on a canvas, an output, a surface or a source
samples it every frame, in linear light, and the bytes leave as Art-Net 4, sACN (E1.31) or DDP, latched with the projection.
The design is `docs/round2/design/led.md`; the wire is `docs/wire/led.md`.

## The rig

`Project.leds` holds **nodes** (`ln…`: an Art-Net node, an sACN receiver, a DDP/WLED device; a node starts off), **fixture
types** (`lt…`: channels before, per and after the pixels; the built-in ones are `builtin:rgb|grb|brg|rgbw|grbw|rgb16|rgbw16|l|
dimmer+rgb` and are never stored) and **fixtures** (`lf…`: a layout in a space, a type, a patch, sampling and colour). Older
project files read an empty rig. The rig travels with the show; whether **this machine** transmits is machine-local
(`NodeSettings.led.output`, off on a fresh node): opening a venue's show on a laptop never lights the venue.

## Spaces and layouts

| Space | Positions | Samples |
|---|---|---|
| canvas | canvas pixels, x = 3 is the fourth column's centre | the canvas as it is composed |
| output | output pixels | the output's final picture (after warp, blend and trims) |
| surface | surface UV 0–1, v down | the surface's source through its UV region or canvas region (a viewpoint-projected surface is black this round) |
| source | 0–1 | the source resolved at 512 × 512 (a canvas at its own size) |
| world | metres | black this round (next round: nearest surface or a viewpoint), with a warning |

Layouts: a line (first and last LED on the ends), a grid (from any corner, rows or columns first, snaked), a ring (clockwise from
angle 0), or points. Placement moves, turns (about z) and scales it; Reverse flips the address order.

## The GPU passes

Every frame, after every output is drawn and before the presents (`IFramePass.FrameEnd`): **pass 1** samples each group's
picture (one draw per canvas, output or source in use) into an RGBA32F target, one texel per pixel — point sampling reads the
texel under the pixel, area sampling averages nine bilinear taps over half the pitch, each tap decoded to linear light with the
fixture's content gamma. **Pass 2** makes every DMX slot: blackout zeroes colour and dimmer slots (constants are kept, a node may
opt out with `followBlackout`), a test pattern or the locate blink replaces the show's colour (in code values), the show's colour
is scaled by brightness, the rig's level and the air (an output fixture follows its output's master × panic gain, any other the
darkest output's), white is extracted for RGBW fixtures, gains apply, the output gamma encodes and the value is quantised to 8 or
16 bits. The rows are read back through pixel-pack buffers behind fences, never waiting; a frozen frame keeps its colours. A frame
allocates nothing once warm. `LedReference` is the CPU twin of both passes: the read-back bytes equal it exactly for point
sampling (llvmpipe), within one code for area sampling.

## Patching

One rule places every fixture from its start: the head, each pixel and the tail never straddle a universe — a unit that does not fit
in what is left of the node's universe (`universeChannels`, 510 by default) starts the next universe at slot 1; a DDP device is one
buffer. `LED AUTOPATCH` lays fixtures end to end in rig order, moving to the next node past the protocol's last universe;
`EACH ON` starts every fixture on a fresh universe. Two fixtures on the same slot are an **overlap**: reported with both, the first
keeps the slot, and a patch that would make one is refused unless `FORCE`. The **bandwidth guard** (`NodeSettings.led.maxMbps`,
200) estimates every packet with its headers (Art-Net 18 + slots, sACN 126 + slots, DDP 10 + 1440 a packet, plus 42 bytes below
UDP) at each node's rate, and refuses a rig over it with the arithmetic — nothing is sent.

## Sending

One send thread is the only writer of LED sockets (Art-Net goes through the node's one Art-Net socket on UDP 6454, shared with
control input). Per node: a frame is sent when floor(leader seconds × rate) changes (Art-Net and sACN 44 Hz, DDP 60 Hz by default),
so two machines decimate the same frames; it leaves at the light's monotonic instant less the node's latency, every universe back to
back, then the latch — ArtSync to the node, the sACN synchronization packet when a sync universe is set, the DDP push flag on the
device's last packet. A universe not sent for 900 ms is sent again with its last payload. Art-Net nodes are polled (directed) every
3 s while output runs; their ArtPollReply fills `seen`. WLED devices get 8-bit DDP only.

## When the air is not the show

| Moment | What the LEDs do | Why |
|---|---|---|
| Blackout | One black frame each frame (colour and dimmer slots 0, constants kept); no picture is resolved or sampled while every fixture follows blackout | Blackout wins before any pass; a node set not to follow blackout keeps sampling |
| Freeze, or a panic FREEZE on any output | The colours hold | As the outputs hold |
| A panic SAFE look on any output | Fixtures that follow no single output go dark; an output's own fixtures sample its final picture (the safe look) | The rig has no safe look of its own: dark is the safe choice |
| A scan or an align run (an output on a raw frame) | That output's fixtures go **dark**, and its picture is not sampled; the rest of the rig plays | The output shows a measurement pattern, not the show; holding would leave a stale look up for the run's length, and LED light near the surface would spoil the camera's capture |
| `LED OUTPUT OFF` (machine or node) | One black frame, latched, then silence; sACN adds three terminations; `HOLD` skips both. No sampling while this machine sends nothing | The air rules |
| The node stops (shutdown) | The same as `LED OUTPUT OFF`: every node still sending gets one black frame and, on sACN, three terminations, before the sockets close | A stopped node must not leave the rig holding its last look; Art-Net nodes hold DMX indefinitely |
| The node is a hot backup (docs/FLEET.md), or a primary that has started with hot backups and not heard them yet | Silence, with no black frame, and nothing sampled; STATE says `standby`. FAILOVER (or hearing its backups) makes it send from its next frame | The rig's universes are the primary's: a second sender would fight its picture |
| The node is fenced (another node took its show) | One black frame, latched (sACN: three terminations), then silence; STATE says `held` | Nothing it last sent stays lit, and it never fights the new primary |

Values STATE shows that move on their own (a node's state words, frames a second, lateness, how long ago it answered, the
locate's time left) are written afresh on STATE's next read when they move, with nothing playing, without moving its revision.

## Import

`LED IMPORT <path> AS CSV` reads a pixel list in MADRIX/Capture-style columns (Fixture or Display Name, Pixel or Index, X, Y, Z,
Universe, Channel or Address, DMX Address as `U.A` or absolute, Type or Mode; comma, semicolon or tab; invariant culture): one
points fixture per name, in pixel order, scaled by `SCALE`, patched from its first pixel's address to the node named. The whole
file is one undo step; an error names its line and column.

## Manual checks (lab)

Real PixLite/Falcon/ENTTEC nodes with ArtSync; WLED on an ESP32 by DDP; sACN through IGMP-snooping switches; jumbo frames; the
send thread's timer on Windows; ArtPollReply offsets against a capture from a real node.

## Next round

GDTF and MVR import; `LED DISCOVER` (ArtPoll broadcast, WLED by mDNS); world sampling (nearest surface, viewpoint); the power
cap; camera auto-locate; redundancy; sACN discovery; temporal dither; the desk's LED tool, patch editor, DMX monitor and previs;
ArtSync as a directed broadcast (sent to each node's address this round); off-lane plan builds for very large rigs; surface chains
(a surface's effects are not on its LED pixels this round).
