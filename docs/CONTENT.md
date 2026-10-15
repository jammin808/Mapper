# Content: particles, fractals, reactive

PlayMap's generators draw pictures from nothing: test patterns, 2D and 3D fractals, particle fields, reactive scenes and
surface-aware effects. Every one of them is a **pure function of its own time**, so a backup node, a late joiner and a timecode
chase draw the same picture at the same instant. The wire is in `docs/wire/content.md`.

## A generator's own time

A generator counts its time from its **origin** (`originNs`, a leader instant):

- `GENERATOR NEW` stamps the origin at the leader's now — the same instant on every node that applies the edit;
- `GENERATOR RETIME <generator>|ALL [AT …]` moves it (a particle field settles anew from there);
- a generator from a file made before round 2 has origin 0: it counts from the leader's epoch, as it always did.

Its time is `(leader − origin)` computed in **double** on the CPU: a float32 of a leader time days into a show has lost its
milliseconds, and the shader is handed only the small numbers it needs. Past 2¹⁶ s (about 18 hours) of a non-periodic kind's own
time, STATE warns "time precision: re-time at a quiet moment".

**Item time.** While a playlist routes a generator, the generator reads its **item's clip time** instead: the clip's position past
its in point. It holds while the item is cued or paused, wraps when it loops, moves with a seek and a timecode chase, and starts at
zero when the item starts, as a clip does. During a fade or a dip the incoming generator reads its own item's time too. When two
playlists route one generator, the first in the show's order sets its time.

The **sync marks** (`pattern.syncmarks`) keep the leader's own grid whatever their origin: a flash on every node at the same leader
second is what they are for.

**On a stage surface.** A surface-aware effect (`effect.sweep`, `effect.outline`, `effect.relight`) shown on a surface is drawn by
the projector pass from the surface's own geometry, on the same own time it draws at anywhere else: `RETIME` moves it there too,
the shader is handed only the seconds since its origin, and STATE counts the surface's draw (`ok`, on the air, its `timeS`).

## Stings

A **sting** is one of Patterns' twelve pulse and score shapes (`STING LIST`): Explosion, Rush, Flash, Bloom (pulses) and
Shockwave, Vortex, Strobe, Supernova, Freeze, Gust, Rainbow, Quake (scores). `STING <preset> [<ms>] [AT …]` puts it in the node's
sting channel, starting at a leader instant, for 100–5000 ms (900 ms for a pulse, 2 s for a score by default). Every generator
answers the surge the same way on every node at the same instant: particles speed up, slow, lift, swirl and gust; fractals zoom,
turn and shift hue; scenes glow; the flash channel goes through the flash guard. A new sting replaces the one running;
`STING CLEAR` stops it. A sting changes no show; `STATE.content.sting` observes it while it runs.

## The GPU particle field (`particles.flow`)

Every particle's state lives in two RGBA32F textures (position, age and life; velocity, colour key and size key) and is stepped
by one fragment pass on a fixed grid of 1/120 s of the generator's time (counted in whole nanoseconds, exact days into a show), two
steps a frame at 60 Hz. Up to 1 M particles
(262 144 by default); a software renderer (llvmpipe) steps 64 k, and STATE says so.

**Births.** Slot *i* lives `life_i` steps (between `lifeMin` and `lifeMax`) and is born whenever `(n − phase_i) mod life_i = 0`;
everything about its k-th life — whether it is lit at the `rate`, where it is born, how fast and which way, its colour and size —
is drawn from integer hashes of (i, k). The schedule is integer arithmetic only, so every GPU agrees with `ParticleSchedule`, its
C# twin, slot for slot.

**Bounded memory.** A slot's state depends only on the steps since its last birth, so a field run from any step at least one
longest life back is the same field. The renderer therefore never integrates from the start of a show: on its first frame, after
a seek, a jump of more than a life, or any change of its parameters, it **re-anchors** one longest life before the time it must
show and runs forward from there (at most `30 M / count` steps a frame, 2–64), holding its last picture and saying how far it has
settled; a GPU too slow to catch up draws what it has after 30 frames, and says so. A bound number (an LFO on the rate, say) is
read at each step's own instant, so a pure source keeps the field the same at 50 or 60 frames a second. On one GPU and driver the result is bit-for-bit the field that played from the start; across vendors births are exact
and the field is the same in kind, not to the bit (STATE's time class says `bounded`, with its horizon).

**Forces**, in order: gravity and wind, a vortex, an attractor (negative repels), a sting's lift, swirl and gust; drag; then the
position moves by its velocity plus a **curl-noise flow** (the curl of a quintic value-noise potential, 1–3 octaves, drifting
with time: divergence-free, so it neither gathers nor thins the field). At the picture's edge particles pass, bounce (keeping
`restitution` of their speed), wrap or die. A non-finite state makes the slot dormant until its next birth.

**Emitters**: the whole picture, a point, a line, a ring, a disc, one edge or all four, or **a picture** — any source
(`GENERATOR INPUT <generator> source <source>`: a canvas, a playlist, media, a live input, another generator) where particles
are born only where it is bright (brighter than `threshold`, with its brightness as the chance). A source that would show the
field itself is refused as a loop.

**`particles.image`** is the same field made for a picture: born where its source is bright (by luma, alpha or edges, `invert`
for where it is dark), drifting along the picture's contours (`flow`; `flowMode` across moves them up its brightness instead),
and coloured by the picture where each particle was born (`colorBy` `source`: the colour is packed whole into the slot's state at
birth, 8 bits a channel). `particles.flow` reads the same picture when its emitter is `picture`.

**Drawing.** Each slot is an instanced sprite (circle, square, star, streak or bokeh) fetched from the state on the GPU, sized and
coloured over its life (by life, speed or at random through 2–5 colours), faded in and out, premultiplied or additive with `glow`.

## Raymarched fractals

`fractal.mandelbulb` (power, iterations), `fractal.mandelbox` (scale, box fold, sphere fold radii, iterations) and `fractal.kifs`
(a tetrahedron, octahedron or Menger fold with scale, offset and a twist, iterations) share one marcher: sphere tracing through the
family's distance estimator, a hit where a step is closer than the pixel's cone (`detail` pixels), shaded by a tetrahedral normal,
the step count as occlusion, the orbit trap as colour through the palette, fog with distance and a glow near the surface. The
camera orbits the origin (`speed`, `distance`, `height`, `fov`) on the generator's time. The distance estimators and the marcher are
written once in single precision in Core (`Fractal3D`) and mirrored line for line in the shader, so a test holds the GPU's depth to
the CPU twin's. `renderScale` (0.25–1) draws a smaller picture and filters it up.

## Reactive

`reactive.scene` and every fractal have **`sound`** (0–1; 0 in every project from before round 2, which draws exactly as it did).
Above 0 the generator reads the node's `audio:level` and `audio:bass` through the frame's modulation bus (the audio package's
analysis) at the frame's leader instant: the bass swings the brightness by at most `FlashGuard.MaxAudioBrightnessSwing` (0.25)
either side — reactive, never a flash — and the pulse scene's rings follow it. A node with no sound draws neutral and says
"audio: none on this node". A generator that listens is `live` in STATE: a mirror node does not hear the same.

## Bindings

A generator's bindings (`MOD BIND`, alias `GENERATOR BIND`) never write the show. Kinds that read their numbers every frame —
`particles.flow`, the fractals, `reactive.scene`, the moving patterns — evaluate them on the render thread through the frame's bus
before they draw (allocating nothing), and STATE's `live` shows each bound number's value at the last draw. The CPU particle field
(it restarts from its seed on any change), the still patterns and the surface effects draw a bound number at its base, and say so.

## Looks

A **look** is a parameter set with a name. `GENERATOR PRESETS [<kind>|<family>]` lists the curated looks — thirty for the round-2
kinds (particle flows and pictures, the three raymarched fractals, the reactive scene), and the particle presets and fractal
scenes the older kinds carry — and the looks the show keeps. `GENERATOR RECALL <generator> <look>` writes a look's parameters whole
in one undo step (a look of another kind is refused, naming it); `GENERATOR SAVE <generator> <name>` keeps the generator's look in
the show (`gp…`), written whole so it recalls the same picture whatever the catalogue's defaults become.

## Cost and preflight

Each generator's draw is timed on the GPU (timestamp queries, read three frames later, never waited on); STATE reports the last
time and the 95th percentile of the last 120. The node's content budget (`NodeSettings.Content.BudgetMs`, 6 ms at 60 Hz) is
advisory: STATE and the context go amber over it and red over twice it, and preflight names the line that brings a generator down
(half its particles, a lower render scale). Nothing is changed for you.

## Not this round

Morphs (`GENERATOR MORPH`), optical flow from a live camera, deep zoom, stage
spaces and stage particles, crease/grow/shadowlight, `julia4d` and `apollonian`, FSR upscaling, flames, camera reactive, fluid,
trails, preroll, the governor's Auto, `GENERATOR NEW … WITH`.
