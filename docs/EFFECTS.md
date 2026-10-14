# Effects

PlayMap runs image effects in **chains**: an ordered list of **slots** on an output, a layer, a canvas, a surface or a playlist
item. Each slot is one effect **kind** with its parameters, how its result mixes over its input (`mix` 0–1 and `blend`: normal,
add, multiply, screen, difference), the scale it renders at (`scale` 0.25–1) and the show instant its time counts from. Effects
are written in **ISF** (Interactive Shader Format, v2 and v1), so a file from VDMX, Resolume or Millumin's libraries runs here
unchanged, and PlayMap's own library is ISF too.

This build draws the chains on **outputs**. Chains on layers, canvases, surfaces and playlist items are kept in the show, edited
with the same verbs and reported in STATE (`"state":"idle"`, with the words "not drawn: this build draws effects on outputs
only"); the renderer's call points for them are the next step (see *Next*).

## Where a chain runs

An output's chain runs on its picture after the picture is made and **before** the output stage (warp, mask, blend, lens,
trims, dither): an effect never sits after the warp, where it would break the blend weights and the black pedestal, and never
after the air's gain, so a fade to black is black.

What the air shows instead of the show never passes through a chain: a test pattern over an output, a panic's safe look,
blackout, a raw scan frame and the identify card. Meanwhile the chain's slots are `idle` in STATE with the reason, and they keep
their programs and buffers, so the chain draws on the first frame back. A slot that is bypassed, or at mix 0, hands its input on
**bit for bit**. An edit elsewhere in the show (another file imported, another slot changed) never restarts a slot on the air.

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

Once warm, a frame with a chain allocates nothing on the frame path.

## Next

- The renderer's call points for **layer and surface** chains (`IFxAttachPoints.Layer`/`Surface`, already implemented by the
  effects pass and tested by calling them) and for **playlist-item** chains with the item's start as their origin.
- Modulation bindings (`FX BIND` through `IModulationBus`, with the built-in `lfo:` and `ramp:` sources), the geometry-aware
  effects (`PM_POSITION`, `PM_NORMAL`, `PM_EDGE`) and the rest of the library.
- FFGL 2.x: the in-process host, the probe child and quarantine, the isolated host, the Windows GL island.
- The ES fix-up tokenizer (hoisting non-constant global initialisers, explicit int → float), history pre-roll, ISF generators and
  transitions, presets and linked looks, audition on a preview, the shader editor.
