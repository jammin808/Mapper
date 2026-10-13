# Work package "content": particles, fractals and reactive scenes, expanded

Spec for round 2, written 13 October 2026, against `main` at `c12b186`.

- **Research:** `r2/research/content.md`, cited as **[R §n]**.
- **Measurements made here:** `r2/bench/content/` (`Program.cs`, `content.out`), cited as **[M]**.
- **Related specs**, whose seams this one shares rather than duplicates:
  - `effects.md`: `ParamBinding`, `IModulationBus`, `IAudioFrames`, `SurfaceGBuffer`, GPU timers, the compile lane.
  - `present.md`: `GlDialect.Es300`, the ANGLE path.
  - `timecode.md` §9.8: generator time in a playlist.
  - `companion.md`: generated actions and encoders.
  - `led.md`: LEDs sample sources.

**Naming.**
- A **generator** keeps its meaning: a `Generator` record of a `GeneratorKind`.
- A **sting** is an `EffectImpulse` (Patterns' "pulse").
- A **space** is where a generator draws:
  - `Picture`: a 2D picture, as today;
  - `SurfaceUv`: in a surface's UV space, from its G-buffer;
  - `Viewpoint`: from a stage viewpoint camera, then projected onto the surfaces.
- **Bounded memory** describes a simulation whose state at step *n* depends only on steps *n − L … n*, where *L* is
  its longest particle life.

---

## 1. Goal and the bar

PlayMap's content becomes a match for Notch and TouchDesigner in the work a mapping show needs. It goes beyond them
in three places where they are weak:
- **Determinism across machines.** A backup node and a timecode chase draw the same picture.
- **Content that knows the calibrated stage.** Meshes, edges, curvature and the audience viewpoint, continuous
  across projectors.
- **Running locally.** No cloud.

Everything is reachable by one verb grammar shared by the desk, Patterns, Companion and an AI.

| What the best do | How PlayMap matches or beats it |
|---|---|
| **TouchDesigner 2025 POPs.** GPU compute particles and point clouds "with millions of points"; forces into `PartForce`; Trail POP; DMX from points. A 2025.30000 fix makes the Particle POP give "consistent simulation results across runs" — on one machine [R §2.1] | **Match:** 1 M particles by default and 4 M at `quality ultra`; forces; trails (S5). LEDs come from the led package sampling any source. **Beat:** **bounded-memory determinism *across* machines and seeks**. The field is bit-identical once one lifetime has run, measured here: 0 of 4 194 304 floats differ [M]. A backup node, a late joiner and a timecode jump all converge to the same field |
| **Notch particles.** Root pool, emitters (Image with optical flow, Mesh vertices/surface/edges, Volume…), 27 affectors (Curl Noise, Mesh Distance Field with *Follow Contours*, Vortex…), renderers (Point, Trail, *Render Particles To Surfaces*). Mesh collision is rigid-only [R §2.1] | **Match:**<br>• a fixed pool with births from it;<br>• emitters: image/video/live (luma, alpha, edge, matte), surface, edge, shapes;<br>• curl, vortex, attractor, flow, drag, wind;<br>• SDF attract, repel, **follow contours** and collide, plus *stick*;<br>• on-surface rendering.<br>**Beat:** the SDF and the emitters come from the **calibrated stage** (scanned or imported), signed by face normals so **open facade scans** work. On-surface particles are drawn **in UV space**, so they are seamless across overlapping projectors and right from every seat. Particles "in the air" are drawn from the audience viewpoint through the existing projection mapping. Notch needs a separate media server for any of this |
| **MadMapper 6 Materials.** An ISF dialect, single-pass, `time_base` integration [R §2.1, effects R §1.6] | **Beat:** multi-pass GPU simulations, raymarched 3D fractals, deep zoom and flames, all a pure function of show time (no `time_base` jumps). User ISF generators come through the effects package as `isf:` kinds in the same catalogue |
| **Deep zoom tools** (Kalles Fraktaler, Fraktaler 3, Fractalshades): CPU or GPU perturbation with rebasing and BLA, offline [R §3] | **Match** the algorithm (high-precision reference orbit, rebasing, BLA) **in real time on ES 3.0 floats**. Measured: float32 perturbation is within ±1 iteration of a double twin on 92.5 % of pixels, with 0.27 % mean error; plain float scores 0 % [M]. **Beat:** zoom **journeys** that are a function of show time, so they are chaseable and mirrored, to 1e30, on a projection-mapped wall |
| **Notch / TouchDesigner raymarching** (user-built) | **Match:** five DE fractal kinds with curated presets, at render scale 0.5–1 with FSR 1 upscaling (MIT). **Beat:** **stage space**. The fractal is *carved into* the object (`solid`, view-independent) or seen *through* it from the audience viewpoint (`window`, anamorphic), across every projector of the surface |
| **Fractal flames** (cuburn/flam3, offline) [R §3] | **Match** in real time: a stateless chaos game each frame (a pure function of the frame index), log-density tone map and genome morphs. Genomes are JSON an AI can write |
| **Patterns' reactive scenes.** Audio moves the scene, never drives it; `AudioAmount`; quality ladder [R §1] | **Match:** the same six scenes, now actually fed by `audio:*`, plus spectrum, Voronoi and waves. **Beat:** reactivity from a **pre-analysed show soundtrack** (S3) is a function of media time, so it is deterministic, chaseable and mirrored |
| **Lightform-style surface effects** (PlayMap's own `effect.*` today) | **Beat:** crease, geodesic *grow* ("the building draws itself") and self-shadowed *sun* relight. They are computed from **mesh analysis**, not screen derivatives, so two projectors agree on every edge |
| **Stings** (Patterns' `STING`, 12 pulse shapes) | **Match** with verbs (a carryover gap): `STING <preset> [<ms>] [AT …]`, scheduled on the leader clock so every node surges together. They become Companion buttons automatically |
| **Look changes.** Resolume and MadMapper animate parameters per node | **Beat:** `GENERATOR MORPH … OVER 8 s AT …` is a pure function of show time: one look crossfades to another identically on every node, Companion shows the progress, and UNDO reverses it |

---

## 2. Scope this round

### Must

| # | What | Acceptance (checkable) |
|---|---|---|
| **M1** | **Content time.**<br>• `Generator.OriginNs`, stamped by `GENERATOR NEW`/`RETIME`.<br>• **Item time** for a generator a playlist routes. This answers timecode §9.8: a generator in a playlist reads its clip position.<br>• Time is double on the CPU, with periodic phases wrapped there.<br>• STATE warns past 2¹⁶ s.<br>• `GeneratorFrame` gains `LeaderNs` and `OriginNs` | `ContentTimeTests`: a generator item cued 10 s into the item draws the same pixels (llvmpipe, ≤ 1/255) as one started fresh and run to item time 10 s. A leader time of 7 days gives the same picture as 0 s + the same offset (no float32 drift). The warning appears in STATE at 65 536 s |
| **M2** | **STING verbs** (carryover): `STING <preset> [<ms>] [AT …]`, `STING CLEAR`, `STING LIST`. STATE `content.sting` | `StingVerbTests`: `STING Explosion 900 AT +100` → `EffectImpulseChannel.Current` = (Explosion, leader + 0.1 s, 0.9 s). An unknown preset or a length outside 100–5000 is refused with words. `STING CLEAR` with none is refused. The headless particle render differs from the unstung frame |
| **M3** | **GPU particle engine** and kinds **`particles.flow`** and **`particles.image`**. Up to 1 M particles by default (count up to 4 M):<br>• bounded-memory schedule;<br>• emitters: shapes, and any source picture weighted by luma, alpha, edges or matte;<br>• forces: gravity, wind, drag, curl, vortex, attractor, picture flow, sting surge;<br>• picture-edge collisions;<br>• sprites, streaks, size and colour over life;<br>• prewarm and preroll | `GpuParticleTests` (llvmpipe):<br>• **(a)** two sims, the second starting `maxLife` + 100 steps late, are **bit-identical** (as [M]);<br>• **(b)** the GPU birth schedule read back equals `ParticleSchedule` in C# for every slot;<br>• **(c)** the CPU twin of curl has \|div v\| < 1e-4 · \|v\| at 10⁴ points;<br>• **(d)** an emitter on a white disk lights no pixel outside the disk's dilation at the first step;<br>• **(e)** 0 bytes allocated per frame once warm;<br>• **(f)** every program compiles and links as GLSL 330 and ES 300 |
| **M4** | **Stage spaces and stage particles.**<br>• `GeneratorSpace.SurfaceUv`/`Viewpoint` in the resolver and `StageRenderer`.<br>• `SurfaceFields` (curvature, downhill direction, geodesic distance, fold edges, area and edge alias tables), the baked **SDF + nearest-triangle volume**.<br>• **`particles.stage`**: surface, edge and sky emitters; SDF attract, repel, follow and collide; stick; on-surface (UV) and viewpoint rendering | `StageContentTests` (llvmpipe, a 1 m cube and a 4 m plane):<br>• SDF error ≤ 1 voxel against the analytic distance;<br>• 0 of 100 000 particles end inside the cube after 10 s at restitution 0.3;<br>• two projectors overlapping a cube face give the same colour at 20 sampled world points within 2/255 (UV-space continuity);<br>• "stick" particles come to rest on the cube's top face (≥ 95 % of stuck particles within 1 cm of y = 1) |
| **M5** | **Mesh-aware surface effects:**<br>• `effect.crease` (curvature from the mesh);<br>• `effect.grow` (geodesic reveal from seeds);<br>• `effect.shadowlight` (relight with a shadow map, with the light on an orbit, a sun path, a projector or a viewpoint) | On the cube, crease lights all 12 edges in UV space within 2 texels and nothing on faces (< 1 % of face texels > 0.1). `grow` from the lowest vertex reaches the top face's far corner at exactly `distance/speed` s (±1 frame). `shadowlight` with the light behind the cube leaves the front face at ambient |
| **M6** | **Raymarched fractals:** `fractal.mandelbulb`, `fractal.mandelbox`, `fractal.kifs`, `fractal.julia4d`, `fractal.apollonian`.<br>• Render scale plus FSR 1 EASU+RCAS.<br>• `view` = `screen` \| `solid` \| `window` (stage) | `FractalDeTests`: the C# DE twin and a GPU DE probe pass agree within 1e-3 relative at 4096 points per kind. The smoke render is non-black with depth variance. The ES compile leg passes. FSR on a 2× upscale of a test card has PSNR ≥ 30 dB against the full-resolution render |
| **M7** | **Deep zoom** for `fractal.mandelbrot`, `julia` and `burningship`:<br>• high-precision `location`;<br>• BigInteger reference orbit off-thread;<br>• float32 perturbation + rebasing + BLA;<br>• zoom journeys (`zoomRate`, `zoomMax` ≤ 1e30) | `DeepZoomTests` at 1e12, 1e20 and 1e28 on 3 locations: ≥ 90 % of pixels within ±1 iteration of the CPU twin (double perturbation, or BigInteger per pixel at 64×64 for 1e20 and beyond) [M: 92.5 % at 1e12]. Plain float scores < 5 % (as a check that the test bites). The reference for 400 bits × 100 000 iterations takes < 2 s on this machine. BLA leaves the image within the same ±1 bound and cuts the shader's iteration count ≥ 5× at 1e20 |
| **M8** | **Fractal flames** `fractal.flame`:<br>• 24 variations;<br>• genome JSON (`genome`, `genomeB`, `mix`);<br>• stateless walkers per frame;<br>• log-density tone map;<br>• 16 curated genomes | `FlameTests`: the CPU twin's histogram at 256² and 200 k samples and the GPU's have χ² distance < 0.05. The same frame index renders identical pixels twice from scratch. Every curated genome draws ≥ 10 % non-black pixels |
| **M9** | **Reactive:**<br>• `reactive.scene` reads `audio:level`/`audio:bass` through the modulation bus (the new `audio` param), with the swing capped by `FlashGuard`;<br>• new kinds `reactive.spectrum`, `reactive.voronoi`, `reactive.waves` (closed form, with CPU twins) | With a fake `IAudioFrames` at a bass level of 0.8, `ReactiveView.Low` = 0.8 × `audio`. The brightness swing is ≤ 0.25 over 10 s of pink noise. The new scenes match their CPU twins within 2/255 (at 128×72). Silence still moves (frame 0 ≠ frame 60) |
| **M10** | **Presets, user presets, morphs, bindings:**<br>• `GENERATOR PRESETS/RECALL/SAVE/MORPH/BIND/UNBIND/RESET/GET`;<br>• `SET … BY`;<br>• about 110 curated presets for the new kinds | `GeneratorVerbTests`. `MorphTests`: a morph is a pure function of time (two `RenderEngine`s with different frame timing give identical pixels at the same leader ns); choices switch at u = 0.5; colours lerp in OKLab; UNDO restores the parameters and clears the morph. Every curated preset resolves and renders non-black |
| **M11** | **Drawn transitions and nested playlists** (carryover):<br>• Fade and Dip, mixing two resolved pictures by the transition window (the outgoing item keeps decoding);<br>• a playlist inside a playlist resolves to depth 4;<br>• cycles are refused at edit time | Headless render: half-way through a 1 s fade from red to blue gives (0.5, 0, 0.5) ± 1/255; a dip at 50 % is black; a 25 % dip is 50 % red. A nested playlist shows the inner item. `PLAYLIST ADD A playlist:B` when B contains A is refused, naming the loop. With FFmpeg 9, two H.264 items crossfade with no black frame (frame-by-frame luma ≥ the minimum of the two) |
| **M12** | **Cost and state:**<br>• per-generator GPU ms through timer queries;<br>• class (`pure`, `bounded`, `history`, `live`);<br>• warming and computing progress;<br>• STATE `content`;<br>• a CONTEXT item;<br>• governor `Advise` | A fake timer at 2 × budget turns the light amber after 2 s and changes no parameter. `Auto` (S9) is off by default |
| **M13** | **Desk:**<br>• the content library with live thumbnails;<br>• inspector groups with modulation dots;<br>• the sting pad;<br>• the preset bar with morph;<br>• right-click entries on surfaces, sources and generators | View-model tests: every menu entry maps to a verb in `VerbCatalog`. The preset bar recalls through `GENERATOR RECALL` |

### Should

| # | What | Acceptance |
|---|---|---|
| S1 | **Camera reactive.**<br>• Motion energy and optical flow from a Live source, on the GPU at 1/8 resolution: source `camera:<live>/motion`, plus a flow texture for `particles.image` and `reactive.fluid`.<br>• **Person matte** through PlayMap.Vision's ONNX (MediaPipe Selfie Segmentation landscape 144×256, Apache-2.0, fetched by `ModelStore`) as an emitter weight, collider or mask.<br>• `reactive.silhouette` | A video file as a "camera" through the FFmpeg live path: motion energy rises within 2 frames of a cut. A fake `IPersonMatte` drives emission only inside the matte. A real ONNX run is opt-in (`PLAYMAP_VISION_MODELS`) |
| S2 | `reactive.fluid`: stable fluids at 512×288 with 20–40 Jacobi iterations, injected by audio, motion, matte and stings. Badged `history` | Mass conservation: \|div\| after projection < 1e-3. Dye in a closed box decays only by its dissipation |
| S3 | **Pre-analysed show audio:** on import (or `MEDIA ANALYSE`), an audio track gives `AudioFeatureTrack` (100 Hz level, bass, mid, high, onset, beat estimate). It is read at the playlist's media time as `audio:show/<playlist>/…` | Two nodes give identical feature values at the same media time. A timecode chase scrubs reactions with the picture. Coordinate with the audio package |
| S4 | **Show tempo:** `TEMPO SET <bpm> [AT …]`, `TEMPO TAP` → sources `beat`, `bar`, `beat/<n>` as pure functions of leader time | An LFO bound to `beat` peaks on every node at the same leader ns |
| S5 | Particle **trails** (an 8-step ring per particle) and **plexus-lite** (neighbour lines via a coarse binning splat) | Trails are bounded memory, so the determinism test still holds |
| S6 | **Depth emitter** from a Live depth feed (16-bit depth with intrinsics): world points that particles can emit from and collide with | A synthetic depth plane emits on the plane ± 1 cm |
| S7 | **Generator preroll:** the next playlist item, when it is a generator, warms up 2 s ahead (particles prewarm one lifetime; deep zoom computes its reference) | A cue onto a 1 M-particle preset shows its steady field on the first frame (compared with a sim run from −maxLife) |
| S8 | **AI composition:**<br>• `GENERATOR NEW <kind> [<name>] WITH <json>` (parameters, bindings and inputs in one edit);<br>• `GENERATOR CHECK <json>`;<br>• flame genomes written by the assistant, through approval | MCP tool round trip: the proposal shows a summary, not the raw JSON (carryover) |
| S9 | Governor `Auto`: steps a generator's render scale 1 → 0.75 → 0.5 on a measured p95 over its budget for 3 s, and back after 30 s clean. Never changes the simulation | A deterministic test with a fake timer |
| S10 | `effect.scanline`, `effect.depth`, `effect.ao` (an AO baked on the GPU from 128 directions) | AO on a cube's inner corner < AO on a face centre |

### Later

These are left for a later round:
- Particle–particle collisions and flocking (they need a neighbour grid; no atomics on ES 3.0).
- SPH and FLIP fluids.
- Pseudo-Kleinian fractals.
- Rescaled or floatexp deep zoom past 1e30.
- Volume emitters (inside a closed mesh).
- A compute-shader path once ANGLE ships ES 3.1 on D3D11 (it does not today [R §2.2]), or through a Vulkan or native D3D11 backend (present L2).
- `effect.windows` (glass masks from the facade package).
- `effect.shatter` (Voronoi pieces with viewpoint parallax).
- Per-generator sting targeting (`STING … ON <generator>`).
- Gaussian-splat rendering of scans as content.

---

## 3. Architecture

### 3.1 Where things run

```
control lane (Engine)                       render thread (Render)                         workers
─────────────────────                       ──────────────────────                         ───────
GENERATOR/STING verbs ─► ProjectStore       FrameInputs{Project, LeaderNs, Impulse,         StageAnalysisService (Engine):
  (one writer; snapshot)                      Modulation, Audio, Camera}                      MeshFields.Analyse, SDF bake
STING ─► EffectImpulseChannel ────────────►  SourceResolver                                   (per mesh+transform+faces hash,
                                               ├ PlaylistFrame (now/prev/progress) ─► mix     cached in the node cache)
PlaybackLanes (Prev* kept for                  ├ Generator ─► GeneratorHost                  DeepZoomPlanner (Core):
  TransitionNs after a switch)                 │    t = ContentTime(leaderNs, origin)        BigInteger orbit + BLA table
                                               │    params = Morph ∘ Bindings ∘ Params      PersonMatte (Vision, S1)
                                               │    Picture   → own target
                                               │    SurfaceUv → per-surface UV target (G-buffer + fields)
                                               │    Viewpoint → viewpoint target + depth
                                               └ StageRenderer: samples UV targets; Viewpoint
                                                  sources use projection mapping from that viewpoint
```

**No new threads on the render side.** CPU bakes (the SDF, mesh fields, the reference orbit, the BLA table) run on
`Task.Run` workers. A finished result is swapped in on the render thread by reference, the way `IStageResources`
objects are swapped today: identity is the change test.

### 3.2 Time (M1)

- **`ContentTime.Seconds(long leaderNs, long originNs)`** returns `(leaderNs − originNs) / 1e9` in **double**.
- **Origin rules**, in order:
  1. the routed playlist item's clip position (`PlaylistFrame.NowItemNs`): item time, which answers timecode §9.8;
  2. `Generator.OriginNs` when it is ≠ 0;
  3. leader epoch 0, today's behaviour for old files.
- **Shaders** get `uTime` as a float of *t*, wrapped on the CPU when the kind declares a period (`GeneratorTraits.PeriodSeconds`). Phases are given as `uPhase*` computed in double: `fmod(t·rate, 1)`.
- **Precision warning.** When *t* > 2¹⁶ s and the kind is non-periodic, STATE shows `"time precision: re-time at a quiet moment"` with the verb `GENERATOR RETIME`.
- **CPU `ParticleSim`** keeps its grid. Its baseline uses *t*, not leader seconds, so item time restarts the field with the item, as a clip does.

### 3.3 GPU particle engine (M3, M4)

**State.** There are N slots, with `side = ceil(sqrt(N))` (at most 2048). Three RGBA32F textures, ping-ponged:

| Texture | `.xyz` | `.w` |
|---|---|---|
| `S0` | position (picture units, or metres for stage particles) | age, s |
| `S1` | velocity | life, s |
| `S2` (stage and stuck only) | (triangle id as float, exact < 2²⁴; bary u, v) | flags: stuck, on-surface, dormant |

- 1 M slots = 3 × 2 × 16 MB = **96 MB**; 4 M slots = 384 MB.
- Every slot counts against `ContentMemoryCapMb`. A `count` that would go over the cap is clamped, with words.

**Step** (`pstep` program, fragment, MRT, one full-screen triangle at side×side). Fixed `dt` = 1/120 s on the
generator time grid. 2 steps a frame at 60 Hz. Catch-up is capped at `StepsPerFrame = clamp(3e7 / N, 2, 64)`.

1. **Schedule** (integer only, so it is exact on every GPU):
   - `life_i = lifeMin + pcg(i ^ K1) % (lifeMax − lifeMin + 1)` in steps;
   - `phase_i = pcg(i ^ K2) % life_i`;
   - `since = (n − phase_i) mod life_i`;
   - `k = (n − phase_i − since) / life_i`.
   - `since == 0` is a birth. `ParticleSchedule` (Core) is the C# twin.
2. **Birth.**
   - `alive = u01(pcg(i·K3 ^ k·K4)) < rate(t_birth) / rateMax`. Otherwise the slot is *dormant* for this life.
   - `rate(t)` is the bound or morphed rate at the birth step. When it is pure (LFO, sting, pre-analysed audio), determinism holds. When it is live, the generator is badged.
   - The emitter samples a position and a direction from `pcg` streams keyed by (i, k).
   - **Burst pool:** the last 25 % of slots are reserved. While a sting runs, slot *j* is born at `t_sting + jitter_j` (a hash). Otherwise those slots stay dormant. This keeps Patterns' Burst channel within bounded memory.
3. **Forces**, in a fixed order, each skipped at amount 0:
   - gravity and wind;
   - **curl**: the curl of 3 decorrelated quintic value noises with analytic gradients [R §2.1, Bridson], with octaves 1–3 and a time drift;
   - vortex: an axis and a strength with r⁻¹ falloff and a core radius;
   - attractor or repeller: a point, a strength and a radius with smooth falloff;
   - **picture flow** (`particles.image`): the source's luma gradient rotated by 90° (along contours) or not (up the gradient), or the S1 optical flow texture;
   - **SDF** (stage):
     - *attract* / *repel*: a force along ∓∇d inside a band;
     - *follow*: v ← v − (v·∇d)∇d inside the band (Notch's Follow Contours);
   - drag: v·(1 − drag·dt);
   - sting surge: Speed, Slow, Lift, Swirl and Gust map to the same channels `ParticleSim` uses, so Patterns' stings feel the same.
4. **Integrate:** semi-implicit Euler.
5. **Collide.**
   - Picture: edges bounce, wrap, kill or pass.
   - Stage: when `d(p') < radius`:
     - push out along ∇d;
     - reflect the normal velocity × restitution, and the tangential velocity × (1 − friction);
     - **stick**: when |v| < `stickSpeed`, a hash decides the slot is stuck (flag; v = 0) until it dies.
6. NaN guard: a non-finite state makes the slot dormant until its next birth, the GPU form of `ParticleSim`'s rule.

**Emitters.**

| Emitter | Algorithm | Kinds |
|---|---|---|
| point, line, ring, disc, box, sphere (shell/volume), edges of the picture (top, bottom, left, right, all) | closed form from 2–3 `u01` draws | flow, stage (in metres) |
| **picture** (`source` input: media, live, canvas, playlist, generator) | A weight map is made from the source: luma, alpha, Sobel edges, motion (S1), matte (S1) or flat × threshold. It goes into an R16F texture at ≤ 512² with a full mip chain (`glGenerateMipmap`). **Hierarchical importance sampling** descends the mips: at each level, pick a quadrant by its four weights with one `u01`; log₂(512) = 9 fetches a birth. Colour is sampled from the source at birth, or every step with `colorBy = source` | image |
| **surface** | `SurfaceFields.Area`: a Vose alias table over the triangles of the chosen surfaces (CPU, O(T)), packed in RG32F (prob, alias) plus a triangle → vertex index texture. Uniform barycentrics come from √u. Position, normal and UV come from vertex textures (RGBA32F) | stage |
| **edges** | an alias table over fold (dihedral > `foldDeg`, default 30°) and boundary edges, weighted by length | stage |
| **sky** | the top face of the stage bounds, expanded by 10 %, for rain and snow | stage |

The source's own weight-map pass costs 1 + 1/3 mip at ≤ 512². A live source makes the generator `live`.

**Render.**
- **Sprites:** an instanced quad (4 vertices × N instances). The vertex shader does `texelFetch(S0, ivec2(gl_InstanceID % side, gl_InstanceID / side))` and skips dormant slots by moving them off-screen.
- **Shapes:** the existing 5-shape atlas (circle, square, star, streak, bokeh), plus `streak` velocity-aligned with length = |v|·`shutter`.
- **Over life:** size grows, shrinks, pulses or stays constant; colour over life uses 2–5 colours.
- **Colour by:** `life`, `speed`, `random` or `source`.
- **Blending:** premultiplied, or additive with `glow`.
- **Target:** RGBA16F, which is blendable [R §2.2]. The quality share draws `round(N·share)` instances and still steps all N, the `ParticleSim.ActiveCount` rule.
- **`onSurface`** (stage):
  - for each surface showing the generator, draw the particles whose triangle (S2) belongs to that surface (a triangle → surface table) into the surface's UV target;
  - size in UV = world size / (metres per UV texel), estimated from the G-buffer position derivatives at that UV;
  - the result is sampled by the projector pass through the UVs, so it is the same light on the object from every projector.
- **`viewpoint`** (stage):
  - draw sprites in world space from `Generator.Params.viewpoint` (a `Viewpoint` id) with depth tested against the stage depth rendered from that viewpoint;
  - surfaces showing the generator use projection mapping from that viewpoint (mode 1), so particles hang in the air for that seat.

**Prewarm and preroll.**
- On configure, a seek or a time jump of more than `MaxBehindSteps`, the sim re-anchors at `n0 = n − maxLife` and runs `maxLife` steps under the catch-up cap. While warming, it draws its last frame (or black on first use), and STATE shows `warming: 0.4`.
- `GENERATOR PREPARE` and S7 preroll do the warming before the generator is on air.

**Why fragment passes and not transform feedback.**
- Both are ES 3.0 core.
- Textures give random access: neighbour fetches, a debug readback and the importance-sampled emitters.
- Textures map one-to-one onto a future compute path.
- Transform feedback on ANGLE D3D11 goes through stream-out, which we have no lab data for **(U)**.

### 3.4 Stage spaces, fields and the SDF (M4, M5)

**`GeneratorTraits.Space`**:

| Space | The generator draws | What the resolver does |
|---|---|---|
| `Picture` | its own target at the consumer's size (today) | unchanged |
| `SurfaceUv` | for each surface that shows it, at `NodeSettings.SurfaceTextureSize` (default 2048², capped by the surface's UV area): `RenderSurface(generator, surface, gbuffer, fields, frame)` | `StageRenderer` samples the result through UVs (mode 0). A `SurfaceUv` generator shown on a canvas or output falls back to a flat plane (today's `EffectGenerator` rule) |
| `Viewpoint` | from a viewpoint camera, with stage depth | `StageRenderer` sets mode 1 with that viewpoint's matrices for every surface that shows it |

- **UV overlap guard.** `SurfaceGBuffer` (effects package) counts overdraw while rasterising UV space. A surface whose UVs overlap by more than 0.5 % is drawn with the **inline path** instead: the per-fragment stage shader, the way `effect.*` modes 2–4 work today. STATE says `"UVs overlap on <surface>: drawn per projector"`.
- The existing `effect.sweep/outline/relight` move to `SurfaceUv`. A pixel-equivalence test against today's mode 2–4 output holds within 2/255 on a plane.

**`SurfaceFields`** (Core, CPU, per surface; cached by mesh id + faces hash + transform). The `SurfaceGBuffer` rasterises
these as extra UV-space channels. The effects package reserves names for them; this package adds `PM_CURVATURE`,
`PM_DOWNHILL`, `PM_GEODESIC` and `PM_AO`.

| Field | Algorithm | Cost (200 k triangles, E) |
|---|---|---|
| Fold edges | the dihedral angle per shared edge; boundary edges have one triangle | 30 ms |
| Curvature | the angle-weighted mean of the incident edges' signed dihedral (a convex fold is positive), per vertex, normalised by the mean edge length | 40 ms |
| Downhill | `t = g − (g·n) n`, normalised (g = world −Y), per vertex; zero where \|t\| < 0.05 | 10 ms |
| Geodesic | Dijkstra over the edge graph from seed vertices (`seeds` = `lowest` \| `highest` \| `centre` \| a list of world points snapped to the nearest vertex), with edge lengths in metres | 150 ms |
| Alias tables | Vose over triangle areas and over fold and boundary edge lengths | 20 ms |
| AO (S10) | GPU: 128 directions, an orthographic depth map from each, accumulated into UV space | 0.3 s once |

**SDF + nearest-triangle volume** (Core `MeshDistanceFieldBaker`; Engine `StageAnalysisService` on a worker).
- **Volume:** the stage bounds padded by 10 %. The voxel size is set by `NodeSettings.SdfResolution` (default 64 on the longest axis; 128 at `quality ultra`).
- **Per voxel:** the closest point among the stage surfaces' triangles, through a BVH.
  - The query is added to `MeshRaycaster` as `Closest(Vec3d p, double maxDistance) → (triangle, point)`.
  - The distance is **signed by the nearest face's normal** (`sign(dot(p − q, n_q))`). This works for open surfaces (facades, scans), where winding or parity signs fail.
- **Upload:**
  - `R16F` distance as a 3D texture (GL 3.3 and ES 3.0 core);
  - `R32F` nearest-triangle id as a 3D texture. It gives `onSurface` particles their triangle and UV.
- **Gradient:** central differences in the shader.
- **Cost (E):** 64³ is 262 k closest-point queries ≈ 0.3–1 s on 4 cores. 128³ takes 8× as long. The result is cached in the node cache by a stage hash.
- **Rebake:** when a surface's mesh, faces or transform change. The old volume is used until the new one is swapped in, with STATE `fields: rebaking`.

**Surface-aware effects** (M5, `SurfaceUv`, closed form over fields):

| Kind | How it draws |
|---|---|
| `effect.crease` | `smoothstep(threshold, threshold + softness, |curvature|)`. Colours split convex and concave. Pulse and width are in metres (via the G-buffer's metres per texel) |
| `effect.grow` | `reveal = smoothstep(front − width, front, speed·t − geodesic)`. Line mode draws only within `width` of the front, plus fold edges already reached ("the building draws itself"). Fill mode reveals the content input (`source`) behind the front |
| `effect.shadowlight` | **light:** `orbit` (as relight), `sun` (azimuth and elevation over a day curve in *t*), `projector:<id>` or `viewpoint:<id>`. **Shading:** Lambert by the G-buffer normal × a shadow term from a 2048² depth map rendered from the light over the stage meshes once a frame (PCF 3×3), + ambient × AO (S10, else 1). The light may also multiply a `source` input ("relight the video") |

### 3.5 Raymarched fractals (M6)

**Kinds and distance estimators** (each with a C# twin in `Fractal3D` for tests):

| Kind | DE (per iteration) | Parameters |
|---|---|---|
| `fractal.mandelbulb` | polar power-*p*: `r ← |w|`, `θ ← p·acos(w.y/r)`, `φ ← p·atan(w.x, w.z)`, `w ← rᵖ·(sinθ sinφ, cosθ, sinθ cosφ) + c`, `dr ← p·r^(p−1)·dr + 1`; `DE = 0.5·log r·r/dr` [R §3] | `power` 2–16, `iterations` 3–16, `bailout`, `juliaMode`, `juliaC` xyz |
| `fractal.mandelbox` | box fold (clamp to ±`foldLimit`, reflect), sphere fold (`r² < minR²` → ×`fixedR²/minR²`; `< fixedR²` → ×`fixedR²/r²`), `w ← scale·w + c`, `dr ← |scale|·dr + 1`; `DE = |w|/|dr|` | `scale` −3…3, `foldLimit`, `minRadius`, `fixedRadius`, `iterations` |
| `fractal.kifs` | per iteration: rotate (`rotate1`), fold by plane set (`tetra` \| `octa` \| `menger` \| `sierpinski`), rotate (`rotate2`), `w ← scale·w − offset·(scale − 1)`; `DE = (|w| − 1)·scale^(−n)` | `fold`, `scale`, `offset` xyz, `rotate1`/`rotate2` angles, `iterations` |
| `fractal.julia4d` | quaternion `q ← q² + c`, `dq ← 2q·dq`; `DE = 0.5·|q|·log|q|/|dq|` on the 3D slice w = `slice` | `c` xyzw + `drift` (c moves on a pure-time Lissajous), `slice` |
| `fractal.apollonian` | repeated `w ← −1 + 2·fract(0.5·w + 0.5)` (box tile), `k = s/|w|²`, `w ← k·w`, `scale ← k·scale`; `DE = 0.25·|w.y|/scale` | `s` 1–1.5, `iterations`, `tile` |

**Marcher** (`raymarch` program, shared):
- sphere tracing with a cone-relative epsilon (`detail` px), `steps` 32–256, over-relaxation 1.2 with fallback **(U)**, and a far plane;
- normal from 4-tap tetrahedral differences;
- AO from 5 samples along the normal; soft shadow optional (`shadow`, 24 steps);
- colour from the palette by orbit trap (min |w|), with glow from the step count and fog;
- camera `orbit` from (`speed`, `distance`, `height`, `fov`), all functions of *t*.

**Output and upscaling:** RGBA16F at `scale` × the target (0.5 default at 4K, 0.75 at 1080p) → **FSR 1 EASU** (upscale) → **RCAS** (sharpen, `sharpness`). Ported from AMD's MIT-licensed `ffx_fsr1.h` GLSL path, with its notice in `THIRDPARTY-NOTICES`. Two passes.

**Stage views:**
- **`solid`** (`SurfaceUv`): the fractal is evaluated *at* each surface point `p` (world → fractal space by `placement`: centre, size, rotation). Colour comes from orbit trap and DE shading at `p`, as if the object were carved out of fractal matter. It is view-independent, so every seat and every projector agree.
- **`window`** (`SurfaceUv`): a ray from the viewpoint `e` (`viewpoint` param) through `p` is marched *into* the fractal behind the surface. The interior appears to recede behind the wall from that seat, anamorphically.

The audience viewpoint is the existing `Viewpoint` record. Projection-mapping illusions like this usually need a separate Notch-plus-media-server chain.

### 3.6 Deep zoom (M7)

- **Location.**
  - A `location` text parameter (`"re,im"` decimal, any number of digits; up to 2048 characters with `ParamSpec.MaxLength`) overrides `centerX` and `centerY`.
  - `zoom` rises to a maximum of 1e30 (old files clamp the same).
  - `iterations` rises to a maximum of 200 000 for deep views.
  - The shader loop bound is a uniform. The existing `ShaderIterationCap = 256` stays for the shallow path.
- **Path choice.** When `zoom · height < 1e4` the existing float path draws, unchanged. Above that, `DeepZoomGenerator` draws.
- **Reference orbit** (Core `ReferenceOrbit`, a worker):
  - fixed-point complex numbers on `System.Numerics.BigInteger`, with `bits = max(64, ⌈log₂(zoomMax · height)⌉ + 48)`;
  - parsed from the decimal text exactly (invariant culture);
  - iterated `Z ← Z² + C` (Burning Ship: abs folds) to `iterations` or escape;
  - stored as float pairs: `RG32F`, 4096 wide.
  - Cost (E): 400 bits × 100 000 iterations ≈ 0.1–0.3 s.
  - The reference is a pure function of (location, bits, iterations), so every node computes the identical orbit.
- **BLA table** (Core `BlaTable`, in double from the reference): single steps `A = 2Zₘ`, `B = 1`, `R = ε·|Zₘ|` (ε = 2⁻²⁴ for float32, halved for safety [R §3]). Levels merge neighbours starting at iteration 1, with `A_z = A_y·A_x`, `B_z = A_y·B_x + B_y`, `R_z = max(0, min(R_x, (R_y − |B_x|·|c|max)/|A_x|))`. Upload: `RG32F` A, `RG32F` B, `R32F` R; level offsets go in a uniform array (≤ 18 levels).
- **Per pixel** (the `deepzoom` program):
  - `c = (pixel − centre)·spacing` in float32 (valid to spacing ≈ 1e-37, i.e. zoom 1e30 at 2160 rows);
  - loop:
    1. take the largest valid BLA at reference iteration *m* (aligned, `|z| < R`);
    2. otherwise do one perturbation step `z ← 2Zₘz + z² + c`;
    3. **rebase** when `|Zₘ + z| < |z|` or *m* reaches the end of the reference: `z ← Zₘ + z`, `m ← 0` [R §3];
    4. escape at |Zₘ + z|² > 2¹⁶ (for smooth colouring).
  - The derivative is perturbed alongside, for DE colouring: an anti-aliased boundary at any zoom [R §3, iq].
- **Journeys.**
  - `zoom(t) = min(zoomMax, zoom · e^(zoomRate·t))`; the centre is fixed.
  - The reference is computed once for `zoomMax`, so a 60 s dive needs no CPU work per frame.
  - The palette offset is a pure function of *t*.
  - `deep` = `auto` \| `off` (off forces the float path for Patterns parity).
- **While the reference computes:** the float path draws the view clamped to 1e4, and STATE shows `computing 37 %`. `GENERATOR PREPARE` and S7 preroll avoid that.
- **Curated journeys** (8): Seahorse dive (−0.743643887037158704752191506114774, 0.131825904205311970493132056385139, to 1e20), Elephant dive, Mini-brot at 1e14, Spiral arm, Julia dragon deep, the Burning Ship armada at 1e12, Misiurewicz spiral, Double spiral to 1e28. The coordinates are public mathematical points; the digits above were written from memory **(U)**, so the tests must render each one and check it before it ships.

### 3.7 Fractal flames (M8)

- **Genome** (Core `FlameGenome`, JSON):
  - 2–6 xforms, each with a weight, a colour, a colour speed, a pre-affine, an optional post-affine and up to 4 weighted variations;
  - an optional final xform;
  - global rotate, scale, brightness, gamma, vibrancy and palette.
  - **24 variations from the published formulas** (maths, not code): linear, sinusoidal, spherical, swirl, horseshoe, polar, handkerchief, heart, disc, spiral, hyperbolic, diamond, ex, julia, bent, waves, fisheye, popcorn, exponential, power, cosine, rings, fan and bubble.
- **Walkers** (stateless per frame):
  - W walkers (`quality`: 256 k / 1 M / 2 M) in an RGBA32F texture (x, y, colour, xform-rng);
  - seeded from `pcg(walker ^ frameIndex·K)` so frame *f* is a pure function of *f*;
  - **K = 24** iterations a frame: 8 fuse + 16 splat. Each iteration is a fragment pass (choose an xform by a cumulative-weight lookup with a hash; apply the variations), and after the fuse each iteration also draws W `GL_POINTS` (1 px) that fetch their walker, additively into an RGBA16F accumulator (rgb × 1, a = 1).
- **Tone map** (`flametone`):
  - `L = brightness · log10(1 + a·k)/(a)`;
  - `rgb' = mix(rgb·L, (rgb/a)·pow(L, 1/γ), vibrancy)`;
  - **density estimation:** a 3-level blur in which low-density texels read coarser mips (`lod = clamp(log2(32/a), 0, 3)`) [R §3, cuburn].
- **Animation:**
  - `evolve` rotates xform affines by *t*;
  - `genomeB` + `mix` (bindable) interpolates two genomes xform by xform, with matching counts padded by zero-weight xforms;
  - `GENERATOR MORPH` drives `mix`.
- **Temporal smoothing:** `smooth` (0–0.9) is an exponential blend with the previous frame. It is contracting, so two nodes converge, but the generator is classed `history`; default 0.

### 3.8 Reactive (M9, S1–S4)

- **`reactive.scene` + audio.** A new `audio` param (0–1, default 0.6; Patterns' `AudioAmount` default):
  - `ReactiveView.Level` = bus `audio:level`;
  - `Low` = bus `audio:bass`;
  - `Amount` = `audio`.
  - The brightness modulation stays inside `FlashGuard.MaxAudioBrightnessSwing`.
  - Without an audio source the bus reads 0.5 → *neutral* (centred), and STATE says `"audio: none on this node"`.
- **New closed-form kinds** (CPU twins in `ReactiveScenes`):
  - `reactive.spectrum`: `style` = bars \| mirror \| radial \| ring \| wave; `bands` 8–128; smoothing. It reads `IAudioFrames.TrySpectrum` into an R32F 1×bins texture. With no audio it shows an *idle* motion on pure time, so a scene never looks dead (Patterns' rule).
  - `reactive.voronoi`: Worley cells with drifting seeds, edge glow and per-cell pulse. `pulse` can be bound to `audio:onset` or `beat`.
  - `reactive.waves`: interference of 1–8 sources, plus **ripples** launched by stings (deterministic) or by a bound onset (live).
- **Camera (S1).**
  - The `CameraAnalysis` render pass works on a Live source at 1/8 resolution: luma, a frame difference giving motion energy (scalar, smoothed), and a 2-level pyramidal Lucas–Kanade flow (RG16F).
  - It publishes `camera:<live>/motion` and `camera:<live>/x|y` (centroid) to the modulation bus, plus a flow texture handle for `particles.image` and `reactive.fluid`.
  - **Person matte:** the Vision `IPersonMatte` runs MediaPipe Selfie Segmentation (landscape 144×256, Apache-2.0 [R §4]) on a worker at ≤ 30 Hz through ONNX Runtime (already a Vision dependency). It hands back an R8 matte.
  - `reactive.silhouette` uses the matte for an outline glow, echo trails (history, bounded by `echo` ≤ 1 s) and a fill with any `source`.
- **Pre-analysed audio (S3).**
  - `AudioFeatureTrack` is a sidecar per media content hash in the node cache: 100 Hz frames of (level, bass, mid, high, onset strength), plus beat times from onset autocorrelation.
  - It is computed with FFmpeg decode → the same Hann/FFT analysis as Patterns' `Spectrum` (1024 window, the same bands).
  - Read at the playlist's media time, it is a pure function of show time.
- **Show tempo (S4):** `beat = frac((t − anchor)·bpm/60)`, `bar = frac(beat/beatsPerBar)`. These are pure modulation sources.

### 3.9 Presets, morphs and modulation (M10)

- **Curated presets** are C# tables per kind, like `ParticlePresets`. The existing `preset` text param semantics stay: a preset fills every parameter the operator has not set.
- **User presets** are `Project.GeneratorPresets`, written as explicit full parameter sets.
- **Morph.**
  - `GENERATOR MORPH g P OVER s AT a`:
    - writes `Params = P` (the target);
    - writes `Morph = (From: the previous effective params, StartNs: a, LengthNs: s, Curve)`.
  - **Render evaluation:** with `u = curve(clamp((t_leader − Start)/Length, 0, 1))`:

    | Parameter type | How it morphs |
    |---|---|
    | Number | lerp |
    | Integer | lerp, then round |
    | Colour | lerp in OKLab |
    | Colours | element-wise in OKLab, the shorter list cycled |
    | Choice, Bool, Text | switch at u ≥ 0.5 |
    | `seed` and `genome` | switch at u ≥ 0.5 (flames morph through `mix` instead) |

  - After the end, `Morph` is inert. It is cleared by the next edit of that generator. No timed edit is needed, so the one writer is untouched.
  - The morph is a pure function of leader time.
- **Bindings.** `Generator.Bindings` uses the effects package's `ParamBinding` and `Modulation.Evaluate`.
  - **Order:** `effective = Bindings(Morph(Params, t), bus, t)`.
  - A bound live source makes the generator `live`.
  - `SmoothSeconds` applies only to live sources, as in effects §3.7.
- **Effective values** of bound or morphing parameters go to `STATE.content.generators[].live`. They never write the project.

### 3.10 Transitions and nested playlists (M11)

- **`PlaylistPlayback`** gains `PrevItemId`, `PrevSource`, `PrevClip` and `PrevUntilLeaderNs` (init). When the control lane advances a lane past a switch with `Transition ≠ Cut`, it keeps the outgoing item there until `SwitchAt + TransitionNs`.
- **`Frame(long leaderNs) → PlaylistFrame`** gives (Now, NowItemId, NowItemNs, Prev, PrevItemId, PrevItemNs, Kind, Progress).
  - It is a pure function of the snapshot and the leader time.
  - It covers both the not-yet-advanced snapshot (`leaderNs ≥ SwitchAt`, so Now = Next and Prev = Current) and the advanced one (Prev\*).
- **The transition starts at the switch** (QLab/Millumin style). The outgoing item keeps playing; past its out point it holds its last frame.
- **`ISourceProvider`** gains two methods with `null` defaults:
  - `PlaylistFrame? PlaylistAt(SourceRef playlist, long leaderNs)`;
  - `VideoFrameLease? AcquireItem(string playlistId, string itemId, long leaderNs)`.
  - `EngineSources` keeps the outgoing item's decoder slot alive during the window.
  - **One method for two packages:** the effects package's proposed `PlaylistItemAt` is this `PlaylistAt`.
- **The resolver** resolves both sides (each with its own item time and, with effects, its own item chain) and mixes them with the `transition` program:
  - Fade: `mix(prev, now, ease(p))`;
  - Dip: `p < ½ ? prev·(1 − 2p) : now·(2p − 1)`.
  - Effects S6 adds ISF transitions through the same mixer slot.
- **Nesting:**
  - `Resolve(playlist)` recurses through routed playlists with a depth limit of 4 and a visited set. Past the limit it shows black with a fault naming the chain.
  - `PLAYLIST ADD` refuses a source that would make a cycle.

### 3.11 Cost, quality and governance (M12, S9)

- **`quality`** (every new kind): `draft` \| `show` \| `ultra`. It maps per kind to the render scale, the particle share, the walker count, the step cap and the SDF resolution. Defaults are `show`.
- **GPU timing:** `GL_TIME_ELAPSED` queries in a ring 3 deep, never waited on. This is the effects package's `GpuTimerRing`; on ES it needs `EXT_disjoint_timer_query`, present on Mesa [present A1] and, on ANGLE, **(U)**. The timing is per generator per frame.
- **STATE:** `gpuMs` and `gpuMsP95` over 2 s.
- **Budget:** `NodeSettings.ContentBudgetMs` (6.0 at 60 Hz, scaled by the output's refresh). A generator's share is its own measured cost. STATE goes amber when the sum of on-air generators' p95 is over budget for 2 s, and red over 2× budget.
- **Governor** (`ContentGovernor`):
  - `Advise` (the default): words and a light only.
  - `Auto` (S9): render scale only. It never touches a simulation's steps, so determinism classes are unchanged; the draw differs only in resolution.
- **Budgets** (E: estimates for the reference GPU, RTX 4060 / A2000 class; measured in the lab with the timer ring):

| Content at 3840×2160, 60 Hz | Internal work | Estimate |
|---|---|---|
| `particles.flow` 1 M, curl 2 octaves | 2 steps × ~0.15 ms + draw 1 M sprites avg 6 px | ~0.9 ms |
| `particles.image` 1 M + 512² weight pyramid | + 0.1 ms | ~1.0 ms |
| `particles.stage` 512 k, SDF 64³, onSurface 2 surfaces | 2 steps × 0.12 + 2 × 0.3 draw | ~0.9 ms |
| `fractal.mandelbulb` | 1920×1080 internal, 96 steps, 8 iterations + FSR | ~4 ms |
| Deep zoom at 1e20, 5000 iterations, BLA | 8.3 Mpx, ~10× skip | ~3 ms |
| `fractal.flame` 1 M walkers × 24 | 24 passes + 16 M points | ~2.5 ms |
| `reactive.*` closed form | 1 pass | ≤ 0.3 ms |
| `effect.*` in UV space, 2048² per surface | 1 pass per surface + shadow map 2048² | ≤ 0.6 ms |
| `reactive.fluid` 512×288, 30 Jacobi | ~35 passes | ~0.6 ms |

- **Measured here (M, llvmpipe — relative only):**
  - a 1 M particle step: 29–48 ms;
  - Mandelbulb at 480×270: 13–38 ms;
  - deep zoom 192² at 3000 iterations: 0.23–1.0 s.
  - These are CPU-rasterised numbers. They prove the paths work, not that they are fast.
- **CPU per generator per frame:** ≤ 30 µs and 0 bytes allocated once warm (uniform caches; pooled targets; no LINQ in the frame path).

### 3.12 Formats and safety

- **Textures:**

  | Use | Format |
  |---|---|
  | Particle state | RGBA32F (render target; needs `EXT_color_buffer_float` on ES; no blending) |
  | Accumulators | RGBA16F (blendable) |
  | SDF | R16F 3D |
  | Triangle id | R32F 3D |
  | Weight maps | R16F with mips |
  | Reference orbit, BLA | RG32F/R32F |

- **`GlDevice` additions:** `TextureFormat.Rgba32F`; 3D textures (`CreateTexture3D`, `Upload3D`); multi-attachment render targets (`CreateRenderTarget(int w, int h, ReadOnlySpan<TextureFormat> colors)` with `glDrawBuffers`); `GenerateMips`; timer queries (shared with effects); `DrawPoints(program, vao?, count)`.
- **ES 3.00 rules** for every content shader:
  - `precision highp float; precision highp int; precision highp sampler2D/3D`;
  - no `texture2D`;
  - integer hashes on `uint`;
  - loops bounded by a constant with an early `break` on a uniform (FXC-friendly unrolling, **U**: verify on the Windows leg).
- **Flashes:** stings, strobing parameters (`reactive.*` pulse, `effect.crease` pulse, the deep-zoom palette speed) and audio swings all go through `FlashGuard`. Kinds that can flash declare `MayFlash` and STATE lists them.
- **Licences** [R §5.9]:
  - everything is written from the mathematics and the papers;
  - nothing is copied from flam3, Fractorium, Kalles Fraktaler or Shadertoy;
  - FSR 1 is ported under MIT with its notice;
  - the selfie-segmentation model (Apache-2.0) is downloaded by `ModelStore` at run time and never shipped in the exe.

---

## 4. Spine needs

### 4.1 Model (Core/Model; all JSON camel-case; old files read unchanged)

```csharp
// Content.cs — Generator gains init properties (positional constructor and every call site unchanged)
public sealed record Generator(string Id, string Name, string Kind, ParamSet Params) : IEntity
{
    /// <summary>Leader ns its time counts from; 0 = the leader epoch (files before round 2).</summary>
    public long OriginNs { get; init; }                                                       // "originNs": 0
    /// <summary>Shared with FX (effects package): parameter ← source.</summary>
    public ImmutableArray<ParamBinding> Bindings { get; init; } = [];                         // "bindings": []
    /// <summary>A look crossfade in progress (inert once over).</summary>
    public ParamMorph? Morph { get; init; }                                                   // "morph": null
    /// <summary>Picture inputs: "source" (emitter / fill), "flow", "matte", "depth" → any source.</summary>
    public ImmutableSortedDictionary<string, SourceRef> Inputs { get; init; }
        = ImmutableSortedDictionary<string, SourceRef>.Empty;                                 // "inputs": {}
}

public enum MorphCurve { Linear, Ease }
public sealed record ParamMorph(ParamSet From, long StartNs, long LengthNs, MorphCurve Curve);  // target = Params

/// <summary>A look saved in the show: a kind and its full parameter set.</summary>
public sealed record GeneratorPreset(string Id, string Name, string Kind, ParamSet Params) : IEntity;

// Project.cs — init property
public ImmutableArray<GeneratorPreset> GeneratorPresets { get; init; } = [];                  // "generatorPresets": []
// ProjectSettings — init property (S4)
public ShowTempo? Tempo { get; init; }                                                        // "tempo": null
public sealed record ShowTempo(double Bpm, long AnchorNs, int BeatsPerBar);

// Ids.cs
public const string GeneratorPreset = "gp";

// PlaybackLanes.cs (Engine) — PlaylistPlayback gains init properties (M11)
public string? PrevItemId { get; init; }
public SourceRef PrevSource { get; init; } = SourceRef.None;
public ClipTransport PrevClip { get; init; } = ClipTransport.Empty;
public long PrevUntilLeaderNs { get; init; } = long.MinValue;
```

- **JSON.** `ProjectJson.Sanitize` maps a null `Bindings` to empty, a null `Inputs` to Empty and a null `GeneratorPresets` to empty. This is a small hunk with a round-trip test.
- **Ids.** `Project.AllIds()` yields `gp` ids.
- **Generic edits work at once:** `SET gn… params.count 500000`, `GET gn…`, `REMOVE gp…`.
- **Shared types.** `ParamBinding` and `BindingCurve` are defined by the effects package (`FxModel.cs`). Whichever package lands first creates them in `Core/Model/Modulation.cs`, and the other reuses them.

**Catalogue (Core/Content)** — init properties only:

```csharp
public enum GeneratorSpace { Picture, SurfaceUv, Viewpoint }
public enum TimeClass { Pure, Bounded, History, Live }          // Live/History when a bound or input source is live
public sealed record GeneratorTraits(
    GeneratorSpace Space, TimeClass Time, bool ReadsAudio, bool ReadsCamera, bool ReadsStage, bool MayFlash,
    double PeriodSeconds,     // 0 = not periodic (time precision warning applies)
    double CostMs4K,          // estimate on the reference GPU, for KINDS and the planner (E)
    double MaxLifeSeconds)    // Bounded: the convergence horizon; 0 otherwise
{ public static readonly GeneratorTraits Picture = new(GeneratorSpace.Picture, TimeClass.Pure, false, false, false, false, 0, 0.3, 0); }

// GeneratorKind gains:  public GeneratorTraits Traits { get; init; } = GeneratorTraits.Picture;
//                       public IReadOnlyList<string> Inputs { get; init; } = [];        // "source", "flow", "matte", "depth"
// ParamSpec gains:      public string Group { get; init; } = "";                        // "Emit", "Forces", "Collide", "Look", "Camera", "Quality"
//                       public int MaxLength { get; init; } = 256;                      // Text; "location" 2048, "genome" 16384
//                       public double Step { get; init; }                               // encoder step for SET … BY (0 = (Max−Min)/100)
```

**New kinds and their parameters.** Defaults are in parentheses; every number has min and max in the catalogue.
- `particles.flow`, in groups:
  - **Emit:**
    - `preset` (""), `count` (262144; 1k–4 194 304), `seed`;
    - `emitter` (`point|line|ring|disc|box|sphere|top|bottom|left|right|edges|area`), `x`, `y`, `size`, `angle`;
    - `rate` (1), `lifeMin` (2 s), `lifeMax` (4 s), `speedMin`, `speedMax`, `direction`, `spread`.
  - **Forces:** `gravity`, `wind`, `drag`, `curl` (0.6), `curlScale`, `curlSpeed`, `curlOctaves` (2), `vortex`, `vortexX`, `vortexY`, `attract`, `attractX`, `attractY`, `attractRadius`.
  - **Collide:** `edges` (`pass|bounce|wrap|kill`), `restitution`.
  - **Look:** `sprite` (`circle|square|star|streak|bokeh`), `sizeMin`, `sizeMax`, `sizeOverLife` (`constant|grow|shrink|pulse`), `colors`, `colorBy` (`life|speed|random`), `opacity`, `glow`, `shutter`, `background`.
  - **Quality:** `quality`.
- `particles.image` adds:
  - **Emit:** `weight` (`luma|alpha|edges|motion|matte|flat`), `threshold`, `invert`;
  - **Forces:** `flow` (0), `flowMode` (`along|across|optical`);
  - **Look:** `colorBy` gains `source`, and `sticky` (bool);
  - input `source`.
- `particles.stage` works in metres, with:
  - **Emit:** `emitter` (`surface|edges|sky|point|sphere|box`), `surfaces` (tag or id list, "" = all showing it), `foldDeg`;
  - **Forces:** `sdf` (`off|attract|repel|follow`), `sdfStrength`, `sdfBand`;
  - **Collide:** `collide` (bool), `friction`, `stick` (0–1), `stickSpeed`;
  - **Look:** `draw` (`onSurface|viewpoint`), `viewpoint` (id).
- `fractal.mandelbulb`, `mandelbox`, `kifs`, `julia4d`, `apollonian`: as in §3.5, plus:
  - **Camera:** `view` (`screen|solid|window`), `viewpoint`, `speed`, `distance`, `height`, `fov`, `placeX/Y/Z`, `placeSize`;
  - **Look:** `colors`, `glow`, `ao`, `fog`, `shadow`, `background`, `detail`, `steps`;
  - **Quality:** `quality`, `scale`, `sharpness`.
- `fractal.mandelbrot`, `julia` and `burningship` gain `location`, `zoomRate`, `zoomMax` and `deep`. `zoom` max 1e30; `iterations` max 200 000.
- `fractal.flame`: `preset`, `genome`, `genomeB`, `mix`, `evolve`, `rotate`, `zoom`, `brightness`, `gamma`, `vibrancy`, `colors`, `smooth`, `quality`.
- `reactive.scene` gains `audio`.
- New: `reactive.spectrum`, `reactive.voronoi` and `reactive.waves` (style, bands, speed, colours, `audio`, `pulse`), and in S1 and S2 `reactive.silhouette` and `reactive.fluid`.
- New: `effect.crease` (`threshold`, `softness`, `width`, `pulse`, `convex`, `concave` colours), `effect.grow` (`seeds`, `speed`, `width`, `mode` `lines|fill`, `color`, `loop`, input `source`) and `effect.shadowlight` (`light` `orbit|sun|projector|viewpoint`, `lightRef`, `speed`, `elevation`, `ambient`, `color`, `softness`, input `source`).

### 4.2 Verbs (`VerbCatalog`, `VerbCategory.Content`)

`<generator>` is a `gn…` id or a name. `<preset>` is a curated name or a `gp…` id. `AT +<ms>|<unix ms>` schedules on
the leader clock (the transport's rule).

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `GENERATOR KINDS` (extended) | `GENERATOR KINDS [<family>]` | Low | no | JSON as today plus `traits:{space,time,readsAudio,readsCamera,readsStage,mayFlash,costMs4K,maxLifeSeconds}`, `inputs`, `params[].group`, `params[].step`, `presets` (count) |
| `GENERATOR NEW` (extended) | `GENERATOR NEW <kind> [<name>] [PRESET <preset>] [WITH <json>]` | Low | yes | `OK gn…`, "made Particles · Ink in water"; `WITH` (S8) sets params, bindings and inputs in one edit; stamps `OriginNs` |
| `GENERATOR GET` | `GENERATOR GET <generator>` | Low | no | JSON `{id,name,kind,params,effective,bindings,inputs,morph,traits,state,live,gpuMs}` |
| `GENERATOR SET` (extended) | `GENERATOR SET <generator> <param> <value> \| … <param> BY <delta>` | Review (Low when not on air, once `RiskOf(line)` lands) | yes | as today. `BY` adds `delta·Step` (numbers) or steps a choice; coalesced per (generator, param) |
| `GENERATOR RESET` | `GENERATOR RESET <generator> [<param>\|ALL]` | Review | yes | refuses when already at defaults |
| `GENERATOR INPUT` | `GENERATOR INPUT <generator> <input> <source>\|NONE` | Review | yes | refuses an unknown input, an unknown source, or a loop ("Sparks reads canvas Main, which shows Sparks") |
| `GENERATOR PRESETS` | `GENERATOR PRESETS [<kind>\|<family>]` | Low | no | JSON `[{id?, name, kind, pack, curated}]` |
| `GENERATOR RECALL` | `GENERATOR RECALL <generator> <preset>` | Review | yes | refuses a preset of another kind (names it) and an unchanged result |
| `GENERATOR SAVE` | `GENERATOR SAVE <generator> <name>` | Low | yes | `OK gp…` |
| `GENERATOR MORPH` | `GENERATOR MORPH <generator> <preset>\|<json> [OVER <0.1–600 s>] [AT …] [CURVE LINEAR\|EASE]` | Review | yes | effect `"Sparks → Embers over 8 s from 21:04:05.300"`; refuses when the target equals the current params; default OVER 4 s |
| `GENERATOR BIND` | `GENERATOR BIND <generator> <param> <source> [AMOUNT <a>] [OFFSET <o>] [CURVE …] [SMOOTH <s>]` | Review | yes | words on whether the source exists on this node and the new time class ("now live: breaks mirror") |
| `GENERATOR UNBIND` | `GENERATOR UNBIND <generator> <param>\|ALL` | Review | yes | refuses when nothing is bound |
| `GENERATOR RETIME` | `GENERATOR RETIME <generator>\|ALL [AT …]` | Review | yes | the new origin; a particle field re-anchors (prewarm) |
| `GENERATOR PREPARE` | `GENERATOR PREPARE <generator> [AT <unix ms>]` | Low | no (runtime) | `OK accepted`. STATE `warming → ready` is the observation. Refused on a node without a render host |
| `GENERATOR CHECK` (S8) | `GENERATOR CHECK <json>` | Low | no | JSON `{ok, issues:[{param, words}], normalised}` |
| `STING` | `STING <preset> [<100–5000 ms>] [AT …]` | Review | no (air; journalled) | effect `"Explosion 900 ms at 21:04:05.300"`; refuses unknown presets with the list |
| `STING CLEAR` | `STING CLEAR` | Low | no (air) | refuses when none is running |
| `STING LIST` | `STING LIST` | Low | no | JSON `[{name, kind:"pulse\|score", channels:[…], defaultMs}]` |
| `TEMPO SET` / `TEMPO TAP` (S4) | `TEMPO SET <bpm 20–300> [<beats per bar>] [AT …]` / `TEMPO TAP` | Review | yes | the bpm and anchor; TAP averages the last 4 taps (on the control lane's clock) |
| `MEDIA ANALYSE` (S3) | `MEDIA ANALYSE <media>\|ALL` | Low | no (cache) | accepted; STATE `media[].audioFeatures: ready` |

- **Receipts:**
  - Verbs that change the model answer when the edit is **applied**.
  - `warming`, `computing` and `ready` are **observed** in STATE.
  - `STING` answers applied when the channel holds it; the surge is then visible in `STATE.content.sting.progress`.
- **Show Bus:** a Patterns pulse can reach PlayMap as `STING <name> <ms> AT <unix ms>` on the line wire. Patterns' names are the same.
- **PANIC.** The showsafety package can name the outcome `STING CLEAR` + `GENERATOR MORPH ALL calm OVER 2 s` ("content calm"). This package provides the verbs; showsafety defines the outcome.

### 4.3 STATE and CONTEXT

`StateWriter` gets one call, `ContentStateWriter.Write(w, e)`:

```json
"content": {
  "budgetMs": 6.0, "governor": "advise", "light": "green",
  "sting": { "preset": "Explosion", "atUnixMs": 1792000000300, "lengthMs": 900, "progress": 0.42 },
  "tempo": { "bpm": 124, "beat": 0.31, "bar": 2 },
  "fields": { "state": "ready|baking|stale", "sdf": "64x48x40", "surfaces": 3, "words": "" },
  "generators": [ {
      "id": "gn1a2b3c4d", "kind": "particles.stage", "name": "Snow on ledges",
      "state": "ok|warming|computing|faulted|idle", "progress": 1.0,
      "time": "bounded", "horizonS": 4.0, "space": "surfaceUv",
      "gpuMs": 0.9, "gpuMsP95": 1.1, "scale": 1.0, "count": 524288, "share": 1.0,
      "morph": { "to": "Blizzard", "progress": 0.3 },
      "live": { "rate": 0.82 },
      "onAir": true, "inputs": { "source": "live:lv0a1b2c3d" },
      "words": "", "warnings": [ "time precision: re-time at a quiet moment" ]
  } ]
}
```

- **CONTEXT** item `content`:
  - **amber** when a generator is faulted, the budget is exceeded, the fields are stale, or a `live`/`history` generator is on air while this node mirrors a backup ("breaks mirror");
  - **red** at 2× budget.
  - Headline example: "9 generators · particles 0.9 ms · Snow on ledges warming 40 %".
- **Commands:** the verbs above with their risks.

### 4.4 Seams

```csharp
// Core/Content — pure, testable
public static class ContentTime { public static double Seconds(long leaderNs, long originNs); public static double Phase(double t, double rate); }
public static class ParticleSchedule { public static (int Life, int Phase) Slot(uint index, int lifeMin, int lifeMax);
                                       public static (bool Birth, uint Life) At(uint index, long step, int lifeMin, int lifeMax); }
public static class CurlNoise { public static Vector3 Curl(Vector3 p, int octaves); }          // the shader's twin
public static class Fractal3D { public static double Distance(Fractal3DKind k, Vector3 p, in Fractal3DParams q); }
public sealed class ReferenceOrbit { public static ReferenceOrbit Compute(string location, int bits, int iterations, FractalKind k, CancellationToken ct, IProgress<double>? p); public ReadOnlySpan<float> Orbit { get; } public int Length { get; } }
public sealed class BlaTable { public static BlaTable Build(ReferenceOrbit r, double maxPixelC, double epsilon); }
public sealed record FlameGenome(...); public static class FlameVariations { public static Vector2 Apply(FlameVariation v, Vector2 p, in FlameXform x); }
public static class ParamMorphing { public static ParamSet Evaluate(GeneratorKind k, ParamSet from, ParamSet to, double u); }
public sealed record AliasTable(float[] Prob, int[] Alias) { public static AliasTable Build(ReadOnlySpan<double> weights); }

// Core/Geometry
public sealed record SurfaceFieldSet(float[] Curvature, Vector3[] Downhill, float[] Geodesic, int[] FoldEdges, AliasTable Area, AliasTable Edges);
public static class MeshFields { public static SurfaceFieldSet Analyse(MeshData mesh, ImmutableArray<int> faces, Transform3 t, FieldRequest req, CancellationToken ct); }
public sealed record MeshDistanceField(int Nx, int Ny, int Nz, Vec3d Min, double Voxel, Half[] Distance, float[] NearestTriangle);
public static class MeshDistanceFieldBaker { public static MeshDistanceField Bake(IReadOnlyList<StagePart> parts, int maxResolution, CancellationToken ct); }
// MeshRaycaster gains: public (int Triangle, Vec3d Point)? Closest(Vec3d p, double maxDistance = double.PositiveInfinity);

// Render/Engine/Contracts.cs — defaults keep every existing implementer
public interface IStageResources { …; SurfaceFieldSet? Fields(string surfaceId) => null; MeshDistanceField? DistanceField() => null; }
public interface ISourceProvider { …; PlaylistFrame? PlaylistAt(SourceRef playlist, long leaderNs) => null;
                                   VideoFrameLease? AcquireItem(string playlistId, string itemId, long leaderNs) => null; }
public readonly record struct PlaylistFrame(SourceRef Now, string? NowItemId, long NowItemNs, SourceRef Prev, string? PrevItemId,
                                            long PrevItemNs, TransitionKind Kind, float Progress);
// FrameInputs gains (init): IModulationBus Modulation (effects; default built-ins), IAudioFrames? Audio (effects), ICameraFrames? Camera (S1)
public interface ICameraFrames { bool TryMatte(string liveId, out GpuMatteHandle matte); }   // S1, filled by the Vision bridge
// GeneratorFrame gains (init): long LeaderNs; long OriginNs; IModulationBus? Modulation; IAudioFrames? Audio

// Engine/Seams.cs
public sealed record GeneratorStats(string Id, string Kind, string State, double Progress, string Time, string Space,
                                    double GpuMs, double GpuMsP95, double Scale, int Count, double Share, string Words,
                                    IReadOnlyDictionary<string, double> Live);
public interface IContentBackend
{
    IReadOnlyList<GeneratorStats> Generators();
    Task PrepareAsync(string generatorId, long atLeaderNs, CancellationToken ct);
    string FieldsState { get; }
}
// IRenderHost : …, IContentBackend   — a node without a render host answers with an empty backend that refuses PREPARE with words

// Vision (S1)
public interface IPersonMatte : IDisposable { bool TryMatte(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> matte, out int mw, out int mh); }
```

### 4.5 NodeSettings additions (init, defaults; old files read unchanged)

```csharp
public double ContentBudgetMs { get; init; } = 6.0;              // at 60 Hz; scaled by refresh
public ContentGovernor ContentGovernor { get; init; } = ContentGovernor.Advise;   // Off | Advise | Auto
public int ContentMemoryCapMb { get; init; } = 1024;
public int MaxParticles { get; init; } = 4_194_304;
public int SdfResolution { get; init; } = 64;                    // longest axis
public int SurfaceTextureSize { get; init; } = 2048;
public string PersonMatteModel { get; init; } = "mediapipe-selfie-landscape";   // S1; "" = off
```

---

## 5. Files

**New (preferred), Core:**
- `src/PlayMap.Core/Content/ContentTime.cs`, `GeneratorTraits.cs`, `ParamMorphing.cs` (with OKLab), `GeneratorPresetCatalog.cs` (curated tables for the new kinds)
- `src/PlayMap.Core/Content/Particles/ParticleSchedule.cs`, `GpuParticleSettings.cs` (Resolve for flow, image and stage), `CurlNoise.cs`, `AliasTable.cs`, `ParticlePresets2.cs`
- `src/PlayMap.Core/Content/Fractals/Fractal3D.cs`, `Fractal3DPresets.cs`, `ReferenceOrbit.cs` (BigInteger fixed point, decimal parse), `BlaTable.cs`, `DeepZoomPresets.cs`, `FlameGenome.cs`, `FlameVariations.cs`, `FlamePresets.cs`
- `src/PlayMap.Core/Content/Reactive/ReactiveScenes.cs` (spectrum, Voronoi and waves CPU twins), `AudioFeatureTrack.cs` (S3), `ShowTempo.cs` (S4)
- `src/PlayMap.Core/Geometry/MeshFields.cs`, `MeshDistanceField.cs`, `MeshClosestPoint.cs` (BVH query; or a `MeshRaycaster` partial)
- `src/PlayMap.Core/Model/GeneratorPresetModel.cs`

**New, Render:**
- `src/PlayMap.Render/Generators/Gpu/GpuParticleSystem.cs` (state, step, emit, draw), `ParticleShaders.cs`, `WeightPyramid.cs`, `ParticleGenerators.cs` (flow, image, stage)
- `src/PlayMap.Render/Generators/Stage/SurfaceSpace.cs` (UV targets per surface, fields upload), `ViewpointSpace.cs`, `StageFieldTextures.cs` (SDF and triangle-id 3D textures, alias tables), `SurfaceEffects.cs` (crease, grow, shadowlight), `ShadowMap.cs`
- `src/PlayMap.Render/Generators/Fractals/RaymarchGenerator.cs`, `RaymarchShaders.cs`, `Fsr1.cs` (+ `Fsr1Shaders.cs` with the MIT notice), `DeepZoomGenerator.cs`, `DeepZoomShaders.cs`, `FlameGenerator.cs`, `FlameShaders.cs`
- `src/PlayMap.Render/Generators/Reactive/ReactiveGenerators2.cs`, `ReactiveShaders2.cs`, `CameraAnalysis.cs` (S1), `FluidGenerator.cs` (S2)
- `src/PlayMap.Render/Engine/TransitionMixer.cs` (+ the `transition` program)
- `src/PlayMap.Render/Generators/ContentShaderRegistry.cs`: the content programs register beside `ShaderLibrary` without editing its big switch, except for one line that merges the registries

**New, Engine and App:**
- `src/PlayMap.Engine/EngineCore.Generators.cs` (the new GENERATOR verbs), `EngineCore.Stings.cs`, `Content/ContentStateWriter.cs`, `Content/StageAnalysisService.cs`, `Content/AudioAnalysisService.cs` (S3)
- `src/PlayMap.App/ViewModels/Content/ContentLibraryViewModel.cs`, `GeneratorInspectorViewModel.cs`, `StingPadViewModel.cs`, `PresetBarViewModel.cs`, and the views `Views/Content/*.axaml`

**New, Vision (S1):** `src/PlayMap.Vision/Segmentation/PersonMatte.cs`, plus a `ModelCatalog` entry.

**New, tests:**
- `tests/PlayMap.Core.Tests/Content/`: `ContentTimeTests`, `ParticleScheduleTests`, `CurlNoiseTests`, `AliasTableTests`, `Fractal3DTests`, `ReferenceOrbitTests`, `BlaTableTests`, `FlameVariationTests`, `ParamMorphingTests`, `GeneratorCatalogTests` (every new kind resolves its defaults and presets), `MeshFieldsTests`, `MeshDistanceFieldTests`
- `tests/PlayMap.Render.Tests/Content/`: `GpuParticleTests`, `StageContentTests`, `RaymarchTests`, `DeepZoomTests`, `FlameTests`, `ReactiveSceneTests`, `TransitionTests`, `EsDialectContentTests`
- `tests/PlayMap.Engine.Tests/`: `GeneratorVerbTests`, `StingVerbTests`, `ContentStateTests`, `NestedPlaylistTests`

**Touched (small hunks):**

| File | Hunk |
|---|---|
| `Core/Content/GeneratorCatalog.cs` | append the new kinds; `ParamSpec` gets `Group`, `MaxLength`, `Step` (and `TryNormalise` honours `MaxLength`); `GeneratorKind` gets `Traits` and `Inputs`; the fractal kinds' `zoom`/`iterations` maximums and new parameters |
| `Core/Model/Content.cs` | the `Generator` init properties and `ParamMorph` |
| `Core/Model/Project.cs` | `GeneratorPresets`, `ProjectSettings.Tempo` |
| `Core/Model/Ids.cs`, `ProjectJson.cs` | the `gp` id and Sanitize |
| `Core/Wire/VerbCatalog.cs` | the rows in §4.2 |
| `Core/Bus/Pairing.cs` | the `NodeSettings` init properties |
| `Core/Content/ReactiveField.cs` | `ReactiveView.Of` takes levels |
| `Core/Geometry/MeshRaycaster.cs` | `Closest` |
| `Render/Gpu/GpuTypes.cs`, `GlDevice.cs`, `GlDevice.Resources.cs` | `Rgba32F`, 3D textures, MRT targets, mips, points, timer ring |
| `Render/Generators/GeneratorRenderer.cs` | `GeneratorFrame` init fields; `Render(in frame)` also gets the effective `ParamSet` after morph and bindings |
| `Render/Generators/GeneratorHost.cs` | `Create` cases; a surface/viewpoint key; time origin; preroll; stats |
| `Render/Engine/SourceResolver.cs` | `PlaylistFrame` resolution, transition mix, nesting, generator origin |
| `Render/Projection/StageRenderer.cs` | `SurfaceUv`/`Viewpoint` sources; the old `effect.*` modes kept as the inline fallback |
| `Render/Engine/Contracts.cs` | the §4.4 defaults |
| `Engine/Playback/PlaybackLanes.cs` | the Prev\* fields and `Frame()` |
| `Engine/Rendering/EngineSources.cs` | `PlaylistAt`, `AcquireItem`, keeping the outgoing slot |
| `Engine/Rendering/StageResources.cs` | `Fields`/`DistanceField` from `StageAnalysisService` |
| `Engine/Rendering/RenderHost.cs` | `IContentBackend`; pass modulation and audio; stats |
| `Engine/EngineCore.Router.cs` | dispatch |
| `Engine/EngineCore.Edits.cs` | the `PLAYLIST ADD` cycle check |
| `Engine/StateWriter.cs` | one call |
| `Engine/ContextBuilder.cs` | the content item |
| `Engine/Seams.cs` | `IContentBackend`, `GeneratorStats` |
| `docs/` | `WIRE.md` (verbs, STATE), `ARCHITECTURE.md` (spaces, bounded memory), `ROADMAP.md`, a new `docs/CONTENT.md` |

---

## 6. Dependencies

**Work packages:**

| Package | What this package needs or gives |
|---|---|
| **effects** | `ParamBinding`, `IModulationBus` + built-in LFO and ramp sources, `IAudioFrames`, `SurfaceGBuffer` (UV-space position, normal, edge; this package adds field channels), `GpuTimerRing`, the compile lane (the raymarch and deep-zoom programs are long, so compile off-thread). **Whichever package lands first creates the shared types**, and the other reuses them. Effects S6 (ISF transitions) plugs into this package's `TransitionMixer` |
| **present** | `GlDialect.Es300`: every content program compiles in both dialects (the CI ES leg). The Windows DXGI/ANGLE leg must confirm `EXT_color_buffer_float`, `EXT_disjoint_timer_query`, point sprites and float-texture vertex fetch on ANGLE D3D11 (lab test) |
| **audio** (live analysis, if a separate package) | registers `audio:level\|bass\|mid\|high\|onset\|fft/<bin>` in the bus and implements `IAudioFrames`. Until then, reactive scenes read neutral 0.5 and say so. S3 (pre-analysed show audio) and S4 (show tempo) may move to that package; the source names stay |
| **timecode** | this package answers §9.8 (item time) and supplies drawn transitions for its later jump fades |
| **companion** | consumes VERBS and INDEX: presets, morphs and stings become generated buttons; `SET … BY` drives encoders. INDEX `generators[].params` gains `group` and `step` |
| **showsafety** | the "content calm" PANIC outcome uses `STING CLEAR` + `GENERATOR MORPH`; Show Mode locks `GENERATOR` edits but not `STING` (a show action) — showsafety decides; `RiskOf(line)` lowers risk off air |
| **led** | LEDs sample any source; content needs nothing extra |
| **facade** | stage meshes, and later glass masks for `effect.windows` |
| **phonescan** | depth feeds for S6 |
| **fleet** | `STING`/`MORPH` with `AT` to every node of a group (fan-out) |

**Third-party (all MIT-compatible; nothing new is linked at build time except as noted):**
- **AMD FidelityFX FSR 1**: MIT (2021) [R §3]. The GLSL is ported into `Fsr1Shaders.cs` with the notice in `THIRDPARTY-NOTICES.md`.
- **MediaPipe Selfie Segmentation** (S1): Apache-2.0 [R §4]. The model file is fetched at run time by Vision's `ModelStore` (an ONNX export; its SHA-256 is pinned in the catalogue), never bundled.
- **ONNX Runtime**: already a Vision dependency (MIT).
- **No code** from flam3, Fractorium, Kalles Fraktaler, mathr's programs or Shadertoy. Algorithms are written from the papers and articles cited in the research.
- **Ableton Link is not used** (licence **U**: not MIT-compatible). S4's show tempo is PlayMap's own.
- **.NET:** `System.Numerics.BigInteger`, `System.Half` and `System.Numerics.Vector3` (runtime only; Core stays runtime-only).

---

## 7. Verification here

This container is a 4-core Linux box with Mesa 25.2.8 llvmpipe on Xvfb :99, FFmpeg 9, no GPU, no Windows, no camera
and no audio device.

**Tested for real here:**
- **Core (no GL):**
  - the time maths and precision;
  - the schedule twin;
  - curl divergence;
  - alias tables (χ² against the weights);
  - DE twins;
  - the BigInteger orbit against a double orbit at shallow zoom (equal to 1e-15) and against itself at two precisions (identical leading digits);
  - BLA merge invariants (`A`/`B` composition equals direct iteration of the linear part);
  - flame variations against hand-computed values;
  - morph maths and OKLab;
  - every curated preset resolving;
  - mesh fields on cubes and planes (12 fold edges; curvature signs; geodesic distances against the analytic value);
  - the SDF against the analytic box distance;
  - verbs, STATE and CONTEXT through `EngineCore` with the bare parts.
- **GL on llvmpipe (Xvfb :99):**
  - **particle determinism, bit-exact**, already shown by the bench [M];
  - the GPU schedule against C#;
  - emitter containment;
  - SDF collisions and sticking on the cube;
  - UV-space continuity across two projectors;
  - raymarch DE probe against the twin;
  - FSR PSNR;
  - **deep zoom agreement against the CPU twin** (bench: 92.5 % within ±1 at 1e12 [M]; the tests add 1e20 and 1e28 with BigInteger per-pixel truth at 64²);
  - flame χ² and frame purity;
  - reactive twins;
  - transitions (fade and dip pixels);
  - nested playlists;
  - allocation-free frames;
  - **the ES 3.00 leg**: an ES 3.2 context on Mesa with `EXT_color_buffer_float` and `EXT_float_blend` present, where four content programs and an RGBA32F MRT already compile, link and complete [M]. Every content program joins this leg.
- **FFmpeg 9:**
  - `particles.image` from an H.264 test clip (the decoder path that exists);
  - a crossfade between two H.264 items with no black frame;
  - S3 feature extraction from an AAC/PCM track, identical twice;
  - S1 motion energy from a video file used as a "camera" (the FFmpeg live path), rising within 2 frames of a cut.

**Simulated, and how:**
- **Audio:** a fake `IAudioFrames` and bus (sine, pink noise, a 120 bpm kick pattern). A WAV through FFmpeg for S3.
- **Camera:** a video file as a Live source. The person matte is a fake `IPersonMatte` that returns an ellipse. A real ONNX run is opt-in (`PLAYMAP_VISION_MODELS` pointing at a downloaded model), because Hugging Face may not be reachable here.
- **GPU timing:** a fake timer ring drives the budget and governor tests. llvmpipe timings are reported but not asserted (relative only).
- **Multi-node determinism:** two `RenderEngine` instances in one process with different frame timings and different start times. This stands in for two machines with the same GPU and driver. Across vendors, births are exact (integer) and positions are not bit-exact; this is stated in STATE (`time: "bounded"`) and in the papers.

**Compile-only or measured elsewhere** (stand-ins named):
- **Performance budgets** (§3.11) need a real GPU, measured in the lab with the timer ring on the reference card. Here: llvmpipe relative numbers only.
- **The ANGLE D3D11 leg** (`EXT_color_buffer_float`, point sprites, FXC loop compile times for raymarching and deep zoom, timer queries) runs on the present package's `windows-latest` WARP CI job. It renders the content smoke scenes at 256² and compares them against the Mesa golden images within 4/255. WARP is a software D3D11, so it gives correctness, not speed.
- **Real cameras, real audio inputs, the real person-matte model:** opt-in, on a lab machine.

---

## 8. Desk and Companion surface

**The first five minutes** (MadMapper/HeavyM's bar):
- **Content library** (left dock, "Content"): families → kinds → **preset tiles with live thumbnails** (rendered at 160×90 by the preview path, ≤ 4 fps, paused when hidden).
- **Drag a tile** onto a surface in the 3D view, a layer, an output or a playlist. This sends `GENERATOR NEW <kind> PRESET <p>` + `SURFACE SOURCE …`, as one undo step.
- The wall shows it at once. A `Bounded` kind prewarms behind the previous picture and swaps in when ready (no empty frames).

**Inspector** (right dock):
- **Groups** come from `ParamSpec.Group` (Emit, Forces, Collide, Look, Camera, Quality), collapsible.
- **Number fields:** drag, and a scroll wheel that sends `SET … BY`.
- **Modulation dot** on every number: click → a source picker (LFO, sting, beat, audio, camera, MIDI, OSC, DMX, var) → `GENERATOR BIND`. A bound parameter shows a moving **ghost** of its live value.
- **Badges:**
  - the time class (`pure` grey, `bounded` blue with its horizon "4 s", `live` amber "breaks mirror");
  - GPU ms with a sparkline;
  - `warming 40 %` / `computing 37 %`.
- **Preset bar:**
  - curated and user presets;
  - **Recall** and **Save**;
  - **Morph to…** with a length slider and "at next bar" when a tempo exists;
  - a progress line while morphing.

**Sting pad** (Show workspace): 12 buttons in Patterns' names, a length knob (100–5000 ms), "fire at next beat" and a
`STING CLEAR` key. Each button shows the shape's channel icons. The pad lights while surging.

**Right-click** (in step with the verbs):

| On | Entries |
|---|---|
| A surface | Particles from this surface ▸ (snow settling, sparks from edges, lava down the face) · **Draw itself** (grow) · **Sun across it** (shadowlight) · Crease glow · Fractal inside ▸ (window from the audience seat, solid) |
| A media item, live input or canvas | Particles from this picture ▸ (disintegrate, embers, pixel rain) · Silhouette particles (S1, live) |
| A generator | Presets ▸ · Morph to ▸ · Save look · Retime · Prepare · Bind ▸ · Copy look / Paste look · Quality ▸ · Reset |
| A playlist item | Transition ▸ (Cut, Fade, Dip; length) |

**Companion** (generated by the companion package from VERBS and INDEX; nothing hand-written):
- **Actions:**
  - `STING` per preset;
  - `GENERATOR RECALL` per preset (one press);
  - `GENERATOR MORPH` (long press = morph over the press length);
  - `GENERATOR PREPARE`;
  - `TEMPO TAP`.
- **Feedbacks:**
  - a sting surging (progress ring);
  - morph progress on the target preset's key;
  - generator warming or computing (a ring);
  - GPU cost amber or red;
  - the time class (amber "live");
  - per-generator `audio:level` meter (when bound).
- **Variables:** `$(playmap:gen_<name>_<param>)` live values, `sting`, `bpm`, `beat`.
- **Encoders** (Stream Deck +, Studio): `GENERATOR SET … BY` on any parameter, with `Step` from the catalogue. The LCD shows the parameter name and its live value.
- **Auto-created pages:** a "Looks" page per on-air generator (its curated presets as buttons with thumbnails, from THUMB of a preset preview), and a "Stings" page.

---

## 9. Risks and open questions

**Risks:**

1. **ANGLE on D3D11:** float render targets, point sprites, and FXC compile times for long loops (raymarch, deep zoom, flames) are **unverified**.
   - *Mitigation:*
     - the WARP CI leg (present package);
     - the compile lane (off the render thread) with a program binary cache;
     - an `iterations`/`steps` uniform bound with `break`, never an unrolled constant;
     - flames can fall back to 1-px quads if point sprites are slow (×4 vertices).
2. **Cross-vendor bit-exactness.** Bounded memory makes nodes *converge*. It is bit-exact only on the same GPU and driver [M]. Different vendors give the same births and statistically the same fields, not identical pixels.
   - *Stated* in STATE and the papers. A backup node should match its primary's GPU, which is already the rental-rack norm.
3. **UV quality.** Imported meshes with overlapping or stretched UVs break UV-space drawing.
   - *Mitigation:* overlap detection → the inline per-projector fallback, with words. Auto-UV (`UvMapping.cs`) for scans. Particle size compensates for stretch through metres per texel.
4. **SDF on open meshes.** Signing by the nearest face misbehaves near thin double-sided geometry (both sides within one voxel).
   - *Mitigation:* `sdfBand` larger than a voxel, 128³ at `ultra`. STATE warns when > 5 % of voxels flip sign between neighbours.
5. **Float32 deep zoom.** A residual 7.5 % disagreement sits in the high-iteration band [M]. It is visible as fine noise in the densest filaments. Above ~1e30 the deltas underflow.
   - *Mitigation:* DE-based anti-aliasing hides most of it. `zoomMax` is capped at 1e30 this round, with words. Rescaled floats are "later".
6. **Memory.** 4 M particles cost 384 MB plus accumulators.
   - *Mitigation:* `ContentMemoryCapMb` with a clamp and words. The quality share draws fewer without losing state.
7. **Prewarm hitch.** Prewarming 480 steps of a 1 M field may take several frames even on a GPU.
   - *Mitigation:* the catch-up cap and holding the previous picture. Preroll (S7) and `PREPARE` before cues.
8. **Shader size and the uber-shader temptation.** Each kind is its own program with includes. The stage shader keeps modes 2–4 only as a fallback.
9. **Flash risk** from audio-bound brightness or pulse parameters at high amounts.
   - *Mitigation:* `FlashGuard` on every kind that declares `MayFlash`, and the audio swing cap.

**Open questions:**

1. Does live audio analysis live in its own package or in effects? This spec only consumes the bus, and offers S3 and S4 to whichever owns audio.
2. Should Show Mode (showsafety) lock `GENERATOR MORPH`/`RECALL`, or only parameter edits? Looks are often changed live on purpose, so the proposal is to allow both and lock only RESET/INPUT/NEW.
3. Patterns parity: should Patterns learn the new kind names over the Show Bus? Today they are PlayMap-only, and Patterns ignores unknown kinds.
4. Default transition placement: does the fade start at the switch (proposed, the QLab and Millumin habit) or centre on it? This needs a sentence in PLAYBACK and the desk.
5. Should a sting apply to all generators (Patterns parity, proposed) or have `ON <generator>` now?

---

## 10. Effort (agent-hours)

| Item | Hours |
|---|---|
| M1 Content time, origins, item time, precision | 5 |
| M2 STING verbs and STATE | 3 |
| GL device additions (RGBA32F, MRT, 3D textures, mips, points; the timer ring if effects has not landed) | 5 |
| M3 GPU particle engine, flow and image kinds, weight pyramid, determinism tests | 22 |
| M4 Stage spaces, SurfaceFields, SDF bake + `Closest`, `particles.stage` (onSurface, viewpoint), tests | 20 |
| M5 crease, grow, shadowlight + shadow map; moving the old `effect.*` to UV space with an equivalence test | 9 |
| M6 Raymarcher, 5 DE kinds + twins, FSR 1 port, stage views | 15 |
| M7 Deep zoom: BigInteger orbit, BLA, rebasing shader, journeys, worker, agreement tests | 13 |
| M8 Flames: genome, 24 variations, walkers, tone map, 16 genomes | 11 |
| M9 Reactive audio wiring + 3 new scenes + twins | 7 |
| M10 Presets (≈ 110 curated), user presets, morph, bindings, verbs | 11 |
| M11 Drawn transitions, Prev\* in lanes, `AcquireItem`, nesting, cycle check | 10 |
| M12 Costs, governor Advise, STATE/CONTEXT, docs | 6 |
| M13 Desk: library, inspector groups, modulation dots, sting pad, preset bar, right-click | 12 |
| **Must total** | **149** |
| S1 Camera analysis + person matte (Vision) + `reactive.silhouette` | 10 |
| S2 `reactive.fluid` | 6 |
| S3 Pre-analysed show audio | 6 |
| S4 Show tempo | 3 |
| S5 Trails and plexus-lite | 6 |
| S6 Depth emitter | 4 |
| S7 Generator preroll | 3 |
| S8 AI composition (`WITH`, `CHECK`, genome authoring) | 4 |
| S9 Governor Auto | 2 |
| S10 scanline, depth, AO bake | 5 |
| **Should total** | **49** |

---

## Appendix A — measured here (`r2/bench/content/content.out`, Mesa 25.2.8 llvmpipe, Xvfb :99)

```
GL_RENDERER=llvmpipe (LLVM 20.1.2, 256 bits) GL_VERSION=4.5 (Core Profile) Mesa 25.2.8-0ubuntu0.24.04.4
MAX_DRAW_BUFFERS=8 MAX_VERTEX_TEXTURE_IMAGE_UNITS=32 MAX_3D_TEXTURE_SIZE=2048
particles 512x512=262144: step 23.97 ms (llvmpipe), draw 1080p additive 155.0 ms; determinism: after 300 steps
  (< life max 480) 128610 floats differ, after 600 steps pos 0 / vel 0 floats differ
particles 1024x1024=1048576: step 28.59 ms, draw 1080p additive 670.6 ms; determinism: after 300 steps 513909 floats
  differ, after 600 steps pos 0 / vel 0 floats differ
mandelbulb DE raymarch 480x270 (128 steps, 5 iterations): 13.2 ms on llvmpipe
deep zoom 1e12, 192x192, max 3000, reference length 3001: float perturbation exact 90.0 % / within ±1 92.5 %;
  plain float exact 0.0 % / within ±1 0.0 % (one value); mean relative iteration error 0.27 %;
  off-by->1 pixels: 1 of 8464 in 1000–2000, 2754 of 28657 in 2000–3000, 5 of 65 interior
ES context: OpenGL ES 3.2 Mesa — EXT_color_buffer_float, EXT_float_blend, EXT_color_buffer_half_float,
  OES_texture_float_linear present; step, sprite, bulb, perturbation programs compile and link as #version 300 es;
  RGBA32F MRT framebuffer complete
```

The particle step and draw times vary run to run by about 2× with machine load (other agents share the 4 cores).
The determinism and agreement numbers did not change across three runs.
