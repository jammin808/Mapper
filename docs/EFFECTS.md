# Effects

PlayMap runs image effects in **chains**: an ordered list of **slots** on an output, a layer, a canvas, a surface or a playlist
item. Each slot is one effect **kind** with its parameters, how its result mixes over its input (`mix` 0–1 and `blend`: normal,
add, multiply, screen, difference), the scale it renders at (`scale` 0.25–1) and the show instant its time counts from. Effects
are written in **ISF** (Interactive Shader Format, v2 and v1), so a file from VDMX, Resolume or Millumin's libraries runs here
unchanged, and PlayMap's own library is ISF too.

This build draws the chains on **outputs**, canvas **layers**, stage **surfaces** and **playlist items**. A canvas's own chain is
kept in the show, edited with the same verbs and reported in STATE (`"state":"idle"`, with the words "not drawn: this build draws
a canvas's layers' chains, not the canvas's own"); its layers' chains, and those of the outputs and surfaces that show it, draw.

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
| "not drawn: a test pattern or safe look covers its output" (a layer's, surface's or item's: every output that would show the show) | yes |
| "not drawn: its output shows a scan frame or has no size" | yes |
| a held picture (freeze) keeps the slot's last state | yes |
| "not drawn: its output is off" | no |
| "not drawn: the layer is not visible", "not drawn: no output shows its canvas" | no |
| "not drawn: the surface is not visible", "not drawn: no projector shows it" | no |
| "not drawn: the quad has no source and shows its test card", "not drawn: its content is a surface-aware effect the stage draws itself" | no |
| "not drawn: its playlist is not showing it" | no |

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
- Sound inputs read silence this round (`audio` is a 0.5 grey, `audioFFT` black); the audio package brings the live textures.

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

Programs are compiled on a **second GL context that shares the render context's objects**, on a thread of its own. A slot shows
its input untouched while its program compiles, and a compile — however long — never holds a frame: the render thread takes the
finished program at its next frame. Where a second context cannot be made, the lane runs on the render thread instead, one
program a frame, and STATE says so (`fx.compile.lane: "render-thread"` with words). `FX CHECK` and `FX IMPORT` compile through
the same lane, off the control lane, with a 10 s bound.

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

## Measured, not governed

Each slot's GPU time is measured between two GPU timestamps around its passes and its mix, three frames deep so reading never
waits, and reported in STATE (`gpuMs`; on a GLES context, which has no timestamp queries, it stays 0); an output's chain cost is their sum (`outputs[].fx.costMs`) against the node's budget
(`NodeSettings.fx.budgetMs`, 4 ms): amber past it, red past twice it. Nothing acts on it.

Once warm, a frame with chains on outputs, layers, surfaces and items — and with chains that are not drawn, saying why —
allocates nothing on the frame path.

## Next

- A canvas's own chain (its layers' chains draw).
- A chain on a flat colour runs on its one-pixel picture: colour effects are right, spatial ones (blur, vignette, tile, feedback
  zoom) have nothing to work on until a one-pixel picture is drawn at its owner's size.
- Modulation bindings (`FX BIND` through `IModulationBus`, with the built-in `lfo:` and `ramp:` sources), the geometry-aware
  effects (`PM_POSITION`, `PM_NORMAL`, `PM_EDGE`) and the rest of the library.
- FFGL 2.x: the in-process host, the probe child and quarantine, the isolated host, the Windows GL island.
- The ES fix-up tokenizer (hoisting non-constant global initialisers, explicit int → float), history pre-roll, ISF generators and
  transitions, presets and linked looks, audition on a preview, the shader editor.
