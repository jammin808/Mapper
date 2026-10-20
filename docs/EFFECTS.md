# Effects

PlayMap runs image effects in **chains**: an ordered list of **slots** on an output, a layer, a canvas, a surface or a playlist
item. Each slot is one effect **kind** with its parameters, how its result mixes over its input (`mix` 0–1 and `blend`: normal,
add, multiply, screen, difference), the scale it renders at (`scale` 0.25–1) and the show instant its time counts from. Effects
are written in **ISF** (Interactive Shader Format, v2 and v1), so a file from VDMX, Resolume or Millumin's libraries runs here
unchanged, and PlayMap's own library is ISF too.

This build draws the chains on **outputs**, canvas **layers**, stage **surfaces** and **playlist items**. A canvas's own chain is
kept in the show, edited with the same verbs and reported in STATE (`"state":"idle"`, with the words "not drawn: this build draws
a canvas's layers' chains, not the canvas's own"); its layers' chains, and those of the outputs and surfaces that show it, draw.

Today every effect compiles and draws on OpenGL. The direction (docs/round3/PLAN.md D7, §3.4, §3.16): the renderer's own programs
are written once in HLSL and compiled by DXC at build time; an ISF effect compiles out of the engine's process, in
`PlayMap shader-worker` (ISF → GLSL 4.50 → SPIR-V → HLSL → DXIL), and draws on the Direct3D 12 device, so no compiler loads into
the engine on that path. A kind that brought the GPU down is quarantined: its slots draw bypassed, with words, until `FX RELEASE`.
OpenGL stays the reference path until parity is measured.

## Where a chain runs

Each chain runs where its owner's picture is made, and at most once a frame however many outputs show it:

- An **output**'s chain runs on its picture after the picture is made and **before** the output stage (warp, mask, blend, lens,
  trims, dither): an effect never sits after the warp, where it would break the blend weights and the black pedestal, and never
  after the air's gain, so a fade to black is black.
- A **layer**'s chain runs on the layer's picture (its source at the layer's rectangle) before anything is drawn into its canvas;
  the result is drawn at the rectangle with the layer's opacity and blend. A canvas is composed once a frame, so every output and
  surface that shows it shows the same chained layer. A chained layer is never taken as covering the canvas opaquely (an effect
  may write alpha): the background goes under it.
- A **surface**'s chain runs once a frame on its content, made at one size for the frame — the largest projector raster among
  the enabled projector outputs — and every projector samples that one picture through the surface's UVs. So an effect with
  history steps once a frame, and every projector shows the same picture, whatever their rasters.
- A **playlist item**'s chain runs on the item's picture while its playlist shows it, once a frame (at the size its first
  consumer asks for); across a fade or a dip each side runs its own item's chain before the two are mixed.

Previews of the stage drawn after the frame run no chain: they show the chained picture the frame made, or, for a surface no
projector drew that frame, its content without the chain.

What the air shows instead of the show never passes through a chain: a test pattern over an output, a panic's safe look,
blackout, a raw scan frame and the identify card. A safe look that is a canvas or a playlist is drawn as its source is, apart
from the show: its layers' and items' chains do not run on it, even in a frame where another output shows the same canvas or
playlist with them. A chain the frame did not reach is `idle` in STATE with the reason, said once the frame is wholly drawn:

| Words | Kept |
|---|---|
| "not drawn: the air is black" | yes |
| "not drawn: a test pattern or safe look covers its output" | yes |
| "not drawn: its output shows a scan frame or has no size" | yes |
| a held picture (freeze, or a FREEZE outcome on its output) keeps the slot's last state | yes |
| "not drawn: its output is off" | no |
| "not drawn: the layer is not visible", "not drawn: no output shows its canvas" | no |
| "not drawn: the surface is not visible", "not drawn: no projector shows it" | no |
| "not drawn: the quad has no source and shows its test card", "not drawn: its content is a surface-aware effect the stage draws itself" | no |
| "not drawn: its playlist is not showing it" | no |

For a layer, a surface or an item, "its output" is any output that would show it: the output's canvas or source, or its
projector's surfaces, followed through canvases' layers and playlists' items (where several such outputs are off the show, the
words are the first of black, covered, scan frame, held). Of a playlist the air keeps off, only the item it was showing when the
air took it is kept; the others say "not drawn: its playlist is not showing it". A chain no output would show is out of the
picture, whatever the air does.

A chain the air keeps off it keeps its programs and buffers, so it draws on the first frame back. A chain whose owner is out of
the picture lets its buffers and renderers go once unused for 120 frames (its programs stay on the lane a while longer), so a
playlist of many items with effects holds only the ones on the air. A slot that is bypassed, or at mix 0, hands its input on
**bit for bit** — a chain with no slot drawing leaves its owner's picture exactly as it was without one. An edit elsewhere in the
show (another file imported, another slot changed) never restarts a slot on the air.

## ISF support

- Every key of ISF v2: `INPUTS` (event, bool, long with `VALUES`/`LABELS`, float, point2D, color, image, audio, audioFFT),
  `PASSES` (`TARGET`, `PERSISTENT`, `FLOAT`, `WIDTH`/`HEIGHT` expressions), `IMPORTED`, `CATEGORIES`, `CREDIT`, `DESCRIPTION`,
  `VSN`, `ISFVSN`; v1's `PERSISTENT_BUFFERS` (folded into the passes that draw into them), `vv_FragNormCoord` and
  `vv_vertShaderInit`.
- `IMG_PIXEL`, `IMG_NORM_PIXEL` (with or without a bias), `IMG_THIS_PIXEL`, both spellings of `IMG_THIS_NORM_PIXEL`, `IMG_SIZE`;
  the built-ins `PASSINDEX`, `RENDERSIZE`, `TIME`, `TIMEDELTA`, `DATE`, `FRAMEINDEX`, `isf_FragNormCoord`.
- `WIDTH`/`HEIGHT` expressions are evaluated by PlayMap's own evaluator: numbers, `$WIDTH`, `$HEIGHT`, `$<input>`, `+ - * / % ^`,
  parentheses, `floor ceil round trunc min max pow sqrt abs log exp`. A result that is not a positive finite number is 1, and the
  file's check says so.
- A custom vertex file (`.vs`) gets its own prelude (`varying` → `out`, `attribute` → `in`, `texture2D` → `texture`) and an
  `isf_vertShaderInit()` that draws the full-screen triangle and sets `isf_FragNormCoord`.
- Translated sources carry no `#version` and no precision line: the GPU device writes its dialect's header (GLSL 3.30 core here;
  GLSL ES 3.00 on an ES context, where the translation also renames a shader's own definitions of built-in function names, which
  ES forbids). A file's own `#version` and precision lines are dropped in place, so line numbers hold.
- Compile errors come back at **the file's own lines**: the body follows `#line 1 1`, and the logs of Mesa, NVIDIA and ANGLE are
  read back to `{line, column, words}`.
- Measured here: **324 of the 327** public ISF files compile and link at GLSL 3.30 core on Mesa (the three that do not have errors
  in their own GLSL: an `int` returned from a `float` function, an `int`/`float` comparison, an `int` given to a `uint`).
- Orientation: PlayMap's pictures are stored top row first; ISF draws bottom-left first. Every picture handed to ISF carries a
  flip flag, ISF's own buffers none, and the mix pass turns the result the right way up.
- Imported pictures (`IMPORTED`) travel with the show inside the asset (PNG or JPEG) and are decoded on the compile lane, at
  most 4096 × 4096 (the size is read from the file's header first). `FX IMPORT` of a path takes pictures only from inside the
  file's own folder.
- Sound inputs read silence (`audio` is a 0.5 grey, `audioFFT` black): the frame carries the sound (docs/AUDIO.md), and the live
  textures are next.

## Time, and what is frame-exact

An effect's time is a pure function of the leader's show time: `TIME = (leader − origin) / 1e9` with the origin stamped on the
slot by `FX ADD`, `FRAMEINDEX = floor(TIME × rate)`, `TIMEDELTA` the show's frame time (0 on frame 0), never a measured delta. Two
nodes at the same leader instant hand an effect the same numbers, so every effect without persistent buffers draws the same
frame on every node.

A playlist item's effects count from the **item's start**: `FX ADD` on an item leaves the slot's origin 0 (the owner's), and the
item's origin is the leader instant its in point plays on its clip's anchor — the same on every node, so `TIME` is 0 when the
item starts everywhere. A seek, a pause or a rate change moves it with the item's own picture; while the item is cued and has not
started, its `TIME` is 0.

An effect with **persistent buffers** (a feedback trail, an echo) integrates from frame to frame: two renderers that start
together at the same instants draw the same frames, but a renderer that starts later does not catch up. Such a slot says so in
STATE and FX LIST: `"deterministic": false, "history": true` with the words "integrates frame to frame: not frame-exact across
nodes", and the context lists it under "breaks mirror backup".

When `TIME` leaves the range a float holds to a millisecond (past 65 536 s, or before the origin) a drawing slot's words say so.

## The compile lane

Each pass of an effect is a program the device makes (`IGpuDevice.CreateProgramAsync`, the translated source in): the GL backend
compiles and links it on a **second GL context that shares the render context's objects**, on a thread of its own, and reads its
uniforms and samplers back into the program's layout, so a draw sets only the names its program holds. A slot shows its input
untouched while its program compiles, and a compile — however long — never holds a frame: the render thread takes the finished
program at its next frame. A program that does not compile answers its stage and the ISF line of the error. Where a second
context cannot be made, the lane runs on the render thread instead, one program a frame, and STATE says so
(`fx.compile.lane: "render-thread"` with words). A program no slot has drawn for 600 frames is released, and the device lets it
go once the last frame that drew it has completed. `FX CHECK` and `FX IMPORT` compile through the same lane, off the control
lane, with a 10 s bound, and release what they made.

On the Direct3D 12 device the direction is that nothing compiles during the show (docs/round3/PLAN.md §3.13, §3.16): `SHOW ARM`
waits, up to its deadline, for every pipeline the armed show can reach, ISF kinds' included, made from the shader worker's DXIL; a
pipeline still missing then holds its step, named, and is never compiled on the render thread.

## The built-in library

Ten effects written for PlayMap as ISF files, embedded in the renderer, each with a pixel test at 64 × 36:

| Kind | Family | What it does |
|---|---|---|
| `fx.invert` | colour | each colour to its opposite; `amount` mixes toward the negative |
| `fx.levels` | colour | input black and white, gamma, output black and white |
| `fx.hsl` | colour | turns the hue, scales the saturation, lifts or lowers the lightness |
| `fx.threshold` | colour | white above the luma level, black below, with a soft edge |
| `fx.blur` | blur | Gaussian in two passes (a temporary buffer between them), radius in pixels |
| `fx.vignette` | look | darkens toward the corners |
| `fx.pixelate` | look | square cells of one colour each |
| `fx.mirror` | geometry | folds left onto right, top onto bottom, or both |
| `fx.tile` | geometry | the picture repeated across and down |
| `fx.feedback` | time | trails: a persistent float buffer faded by `decay` (history: not frame-exact across nodes) |

A show's own ISF files are kinds `isf:<asset id>` beside them (`FX IMPORT`).

## Bindings

A slot's `mix` and its kind's numbers move with the sound, the beat clock or an LFO through `MOD BIND` (alias `FX BIND`): each
frame the drawn project carries the slot's bound numbers at their live values while the show keeps the base (docs/AUDIO.md).

## Measured, not governed

Each slot's GPU time is measured between two GPU timestamps around its passes and its mix, three frames deep so reading never
waits, and reported in STATE (`gpuMs`; on a GLES context with no timestamp counter, `EXT_disjoint_timer_query` absent or
reporting no bits, it stays 0); an output's chain cost is their sum (`outputs[].fx.costMs`) against the node's budget
(`NodeSettings.fx.budgetMs`, 4 ms): amber past it, red past twice it. Nothing acts on it today. The direction on the Direct3D 12
device is a GPU-time guard with a measured cause (docs/round3/PLAN.md §3.16 item 4): a user slot that is the cause of its output
missing vblanks — the frame's GPU time over the output's period, and under it without the slot's own time — on 30 frames in a row is
bypassed and quarantined, with both numbers in its words.

Once warm, a frame with chains on outputs, layers, surfaces and items — and with chains that are not drawn, saying why —
allocates nothing on the frame path while no layer, surface or effect slot is modulated; a frame with a modulated one builds its
drawn project afresh (docs/ROADMAP.md R2.8).

## Next

- A canvas's own chain (its layers' chains draw).
- A chain on a flat colour runs on its one-pixel picture: colour effects are right, spatial ones (blur, vignette, tile, feedback
  zoom) have nothing to work on until a one-pixel picture is drawn at its owner's size.
- Live `audio` and `audioFFT` textures, the geometry-aware effects (`PM_POSITION`, `PM_NORMAL`, `PM_EDGE`) and the rest of the
  library.
- FFGL 2.x plugins (docs/ROADMAP.md M25) are not in round 3's plan. They are OpenGL code, and OpenGL retires once Direct3D 12
  reaches parity (docs/round3/PLAN.md D2), so a host waits for a design against the Direct3D 12 device.
- The ES fix-up tokenizer (hoisting non-constant global initialisers, explicit int → float), history pre-roll, ISF generators and
  transitions, presets and linked looks, audition on a preview.
- On the desk, round 3's direction (docs/round3/PLAN.md §3.18 item 5): the effects chain editor, an advanced pane, with ISF import
  and its compile report by line. No ISF source editor is planned.
