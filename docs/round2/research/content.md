# Research: particles, fractals and reactive scenes — state of the art and PlayMap's expansion

Written 13 October 2026 for the round-2 work package **content**. Code read on `main` at `c12b186`
(`/home/user/mapper`) and Patterns at `/home/user/patterns-svc` (read-only). Every web fact carries its URL, the date
the page states (or "undated") and **V** (opened and read here) or **U** (not opened, or only a title/snippet; from
memory or a secondary mention — do not build on it without checking). Measurements made in this container are
marked **M** and come from `r2/bench/content/` (`Program.cs`, output in `content.out`).

---

## 1. What PlayMap has today (code, V)

| Area | What is there | Where | Limits that matter |
|---|---|---|---|
| Catalogue | `GeneratorKind(Id, Family, Name, Words, Animated, Params)`; `ParamSpec(Name, Type, Default, Min, Max, Choices, Words)` with `TryNormalise`; 5 families: `pattern` (17 kinds), `fractal` (5), `particles` (1), `reactive` (1), `effect` (3) | `src/PlayMap.Core/Content/GeneratorCatalog.cs` | Flat parameter lists, no groups, no traits (space, determinism class, cost), no bindings |
| Particles | `ParticleSim`: **CPU**, deterministic, fixed step 1/120 s on the leader grid, baseline quantum 512 steps, catch-up budget 3 M particle-updates a frame, `JoinTimeline` for late pictures, `EdgeFlux` side births; max **20 000** particles; 4 emitters (top, bottom, centre, area), 5 sprite shapes; drawn as instanced quads (`sprite` program) from a Skia atlas; sting surge channels (burst, speed, glow, swirl, gust, lift, slow, ripple, scale, hue, shake, flash) | `ParticleSim.cs`, `ParticleGenerator.cs` | No forces beyond gravity/wind/wobble/swirl; no 3D; no emitting from pictures or meshes; no collisions; CPU bound well before 1 M |
| Particle presets | 34 scenes in 8 packs, Patterns' names | `ParticlePresets.cs` | — |
| Fractals | 5 kinds (Mandelbrot, Julia, Burning ship, Newton, domain warp); float32 shaders with a CPU twin (`FractalMath`) held to them by a test; `FractalView` pure of time (breathing zoom, drifting Julia constant); loop cap 256 iterations (`ShaderIterationCap`) | `FractalMath.cs`, `FractalPresets.cs` (24 scenes), `ShaderLibrary.cs` (`FractalCommon`) | `zoom` param goes to 1e6 but float32 pixel spacing collapses near 1e4–1e5; no 3D, no IFS, no deep zoom |
| Reactive | `reactive.scene`: 6 closed-form scenes (plasma, tunnel, kaleidoscope, pulse, vortex, star warp) with a CPU twin; `ReactiveView.Level/Low/Amount` exist **but are always 0** — no audio reaches them | `ReactiveField.cs` | Not reactive yet |
| Surface-aware | `effect.sweep/outline/relight` drawn by the stage shader's `uMode` 2/3/4 from world position, normal and UV; flat-plane fallback elsewhere | `EffectGenerator.cs`, `StageRenderer.cs`, `StageFs` | `outline` uses `fwidth(n)` in screen space: the crease it finds depends on the projector's view, so two projectors can disagree on an edge |
| Stings | `EffectImpulse`, 12 `PulsePreset`s (Patterns' names), `EffectEnvelope`, `EffectImpulseChannel`; `RenderHostOptions.Impulses` hook | `EffectImpulse.cs`, `RenderHost.cs:39,467` | **No verb fires one** (CARRYOVER) |
| Safety | `FlashGuard`: ≤ 3 flashes/s, peak ≤ 0.7, audio swing ≤ 0.25 | `FlashGuard.cs` | — |
| Time | `GeneratorFrame(Seconds, ShowFrame, FrameRate, Impulse, FlashGuard)`; Seconds = leader seconds; leader clock = the leader's `Stopwatch` (`MonoClock`, ns since boot) | `RenderEngine.cs:200`, `Clocks.cs:23` | Shaders take `(float)seconds`: after one day of leader uptime the float32 step is 7.8 ms (half a 60 Hz frame); after a week 62 ms. A generator in a playlist runs on leader time, not item time (the open question in `design/timecode.md` §9.8) |
| GPU device | `TextureFormat` Rgba8, R8, Rg8, R16, Rg16, R16F, Rg16F, Rgba16F, R32F, Rg32F; one colour attachment per target; instanced draws | `Gpu/GpuTypes.cs`, `GlDevice.cs` | No RGBA32F, no MRT, no 3D textures, no timer queries |
| Transitions | `TransitionKind` Cut/Fade/Dip modelled; `PlaylistPlayback` carries Transition/TransitionNs | `PlaylistModel.cs`, `PlaybackLanes.cs` | **Drawn as cuts** (CARRYOVER); a playlist inside a playlist is black (`SourceResolver.Resolve`) |

**Patterns (V, local).** The content came from Patterns: the same particle, fractal and reactive names and ranges.
Patterns' `ReactiveOptions` has `AudioAmount` (0–1) and `AudioSource` (none / loopback / input) and the rule "the
sound moves the scene; it never drives it — a scene with no sound still moves on the show clock"
(`Patterns.Core/Model/PatternConfig.cs:899–960`). Its analyser publishes `AudioLevelFrame(Level, Low, Mid, High)`
from a 1024-sample Hann FFT, bands 20–250 / 250–2000 / 2000–8000 Hz, smoothed 30 ms attack / 250 ms release, stale
after 1 s (`Patterns.Rendering/Effects/Spectrum.cs`). Patterns also has an adaptive **quality ladder** (steps
particles and fractal iterations to 70 / 50 / 35 % after 3 s over 85 % of the frame slot; back up after 30 s clean;
`Patterns.Core/Services/QualityLadder.cs`) and fires effect pulses as `STING <n|name>` library items with
`PulseMs` 100–5000 (`ShowState.cs:1495–1545`).

---

## 2. GPU particles

### 2.1 What the best do

| Fact | Source | Date | V/U |
|---|---|---|---|
| TouchDesigner 2025 introduced **POPs** (Point Operators), "the first new operator family … in over a decade", running on the GPU as compute shaders; "particle systems … point clouds with millions of points"; 4 GB VRAM minimum, 8 GB recommended | https://derivative.ca/community-post/2025-official-update/73153 | 2025-10-29 | V |
| POPs official build 2025.31550; DMX Fixture POP / DMX Out POP send point attributes to Art-Net, sACN, KiNET | https://derivative.ca/release/202531550/73152 | 2025-10-28 | V |
| POPs: points with attributes (`P`, `N`, `Color`, `Vel`, `Age`, `PartForce`…); particles are "integrating over time"; the Particle POP integrates forces added to `PartForce`; `particlesGPU` (the old TOP-based system) is re-implemented in POPs; Trail POP makes per-particle line strips; custom GLSL POPs are compute shaders | https://docs.derivative.ca/Learning_About_POPs | 2026-07-15 | V |
| 2025.30000 release notes: Particle POP "Fixed non-deterministic cooking behavior to ensure consistent simulation results across runs"; new "Jitter Birth Position", "Jitter Births During Frame"; birth-rate fix below 1/frame | https://docs.derivative.ca/Release_Notes/2025.30000 | 2026-08-26 | V |
| Notch particles: a **Particle Root** holds a fixed pool (Max Particle Count); emitters take particles from the pool, affectors move them, renderers draw them; dead particles return to the pool; unused pool slots still cost memory | https://manual.notch.one/2026.2/en/docs/reference/nodes/particles/ | updated 2026-09-15 | V |
| Notch affectors (2026.2): Angled Motion, **Curl Noise Fluid**, Custom Shader, Depth Image Collision, Explode, Field, Flocking, Fluid FLIP / MPM / SPH, Force, Image, Kill Box, Mesh Attractor, **Mesh Distance Field**, Particle-Particle Collision, Points, Primitive, Primitive Collision, Procedural, Rigid Body Collision, Scale, Shockwave, Spline Attractor, Spring, Turbulence, Velocity, Vortex, World Collision | same page | 2026-09-15 | V |
| Notch emitters: Field, **Image** (RGBA of an image or video controls emission; motion vectors from an Optical Flow node make particles track the video), **Mesh** (vertices, surface or edges; deforming meshes supported; weight map, mask), Particle Cache ("repeatable results"), Primitive, Procedural, Screen, Spline, Trail, Video Feature, **Volume** | same page; https://manual.notch.one/2026.2/en/docs/reference/nodes/particles/emitters/image-emitter/ (updated 2025-11-11); …/emitters/mesh-emitter/ | 2025–2026 | V |
| Notch renderers: Point, Trail, Line Connection (lines between nearby particles), Geometry Connection, Dot Matrix, Gradient 2D, Soft Ball, Volume, **Render Particles To Surfaces** | particles page | 2026-09-15 | V |
| Notch **Mesh Distance Field Affector**: SDF voxel texture (width/height/depth parameters) with modes Attractor, Repel, Collision, **Follow Contours**; collision events | https://manual.notch.one/2026.2/en/docs/reference/nodes/particles/affectors/mesh-distance-field-affector/ | undated | V |
| Notch **Mesh Collision Affector**: ray-traced triangles in a BVH; animated, generated or deforming objects **not supported** (rigid only) | https://manual.notch.one/2026.1/en/docs/reference/nodes/particles/affectors/mesh-collision-affector/ | 2025-12-15 | V |
| Unreal Niagara: simulation stages, GPU emitters, neighbour grids | https://dev.epicgames.com/documentation/en-us/unreal-engine/simulation-stages-in-niagara-for-unreal-engine | — | **U** (page did not render) |
| MadMapper 6 generative content is "Materials": an ISF dialect with its own entry point, **no multi-pass**, CPU-side `GENERATORS` (time_base, damper, adsr) | `r2/research/effects.md` §1.6 (read there from docs.madmapper.com) | — | V (there) |
| Curl noise (Bridson, Hourihan, Nordenstam 2007): velocity = ∇×ψ of a noise vector potential — "exactly incompressible", respects solid boundaries by ramping ψ to zero with distance to the boundary, amplitude modulated in space | https://www.cs.ubc.ca/~rbridson/docs/bridson-siggraph2007-curlnoise.pdf | 2007 | V |

### 2.2 GL 3.3 / ES 3.0 constraints (the Windows path is ANGLE on D3D11)

| Fact | Source | Date | V/U |
|---|---|---|---|
| ANGLE support table: **OpenGL ES 3.0 complete on Direct3D 11; ES 3.1 not listed as complete for D3D11** (complete on desktop GL, GLES, Vulkan) | https://chromium.googlesource.com/angle/angle/+/HEAD/README.md | fetched 2026-10-13 | V |
| An older ANGLE doc (`ES31StatusOnD3D11.md`, at an old commit) lists compute shaders on D3D11 as "Fully implemented" | https://chromium.googlesource.com/angle/angle/+show/065c232918a953726ec4edcec365123126d446e2/doc/ES31StatusOnD3D11.md | undated | V (but superseded by the README table) |
| PlayMap's Windows path draws through ANGLE as GLSL ES 3.00 (`GlDialect.Es300`); the 24 existing programs compile with only the header changed | `r2/design/present.md` §0, [A1] | 2026-10-13 | V (there) |

**Consequence:** no compute shaders, no SSBOs, no image load/store, no atomics, no geometry shaders on the shipping
Windows path. GPU particles must be **fragment-pass state updates into float textures (MRT ping-pong)** or
**transform feedback**. Both are ES 3.0 core. Rendering into RGBA32F needs `EXT_color_buffer_float`; *blending*
into a 32-bit float target needs `EXT_float_blend`; RGBA16F is blendable with `EXT_color_buffer_half_float`/
`EXT_color_buffer_float`.

### 2.3 Measured here (M, Mesa 25.2.8 llvmpipe, 4 cores; llvmpipe is a CPU rasteriser, so only relative)

| Measurement | Result |
|---|---|
| Limits (GL 4.5 core context) | `MAX_DRAW_BUFFERS` 8, `MAX_VERTEX_TEXTURE_IMAGE_UNITS` 32, `MAX_3D_TEXTURE_SIZE` 2048 |
| ES 3.2 context (the CI stand-in for ANGLE's ES 3.0) | `EXT_color_buffer_float`, `EXT_float_blend`, `EXT_color_buffer_half_float`, `OES_texture_float_linear` all present; step, sprite, Mandelbulb and perturbation programs compile and link as `#version 300 es`; an RGBA32F + RGBA32F MRT framebuffer is complete |
| GPU particle step (fragment pass, 2 × RGBA32F MRT, curl of 3 analytic-gradient value noises, integer-hash births) | 262 144 particles: 10–24 ms a step; 1 048 576: 29–48 ms a step (llvmpipe; varies with machine load) |
| Instanced sprite draw, additive into RGBA16F at 1920×1080 | 262 144: 155–260 ms; 1 048 576: 0.67–1.4 s (llvmpipe fill rate — irrelevant for a GPU) |
| **Bounded-memory determinism** — sim A runs from step 0; sim B starts empty at step 400 (a late joiner or a seek); births on a fixed per-slot schedule `phase_i + k·life_i` from an integer hash; life 240–480 steps | After 300 common steps (< max life): 128 610 of 1 048 576 floats differ. After 600 common steps (> max life): **0 floats differ** in position and velocity — bit-identical. Same at 1 M particles (0 of 4 194 304) |

---

## 3. Fractals

| Fact | Source | Date | V/U |
|---|---|---|---|
| Mandelbulb (2009): iterate w → r⁸·(sin 8θ sin 8φ, cos 8θ, sin 8θ cos 8φ) + c in polar form; trig-free polynomial form is faster on CPUs but "not in GPUs usually, which have super fast trigonometric implementations" | https://iquilezles.org/articles/mandelbulb/ | undated (mentions 2020) | V |
| Distance estimate for Julia/Mandelbrot sets: track z′ (z′ₙ₊₁ = p·zₙ^(p−1)·z′ₙ (+1 for Mandelbrot)); d ≈ ½·|z|/|z′|·log|z|; bailout raised (e.g. 1e10–1e20) because the formula needs large |z|; enables anti-aliased boundaries in 2D and sphere tracing in 3D | https://iquilezles.org/articles/distancefractals/ | undated | V |
| Mandelbox (box fold + sphere fold + scale), KIFS (kaleidoscopic IFS), pseudo-Kleinian, quaternion Julia, Apollonian sphere-inversion fractals: standard DE fractals of the fractalforums era | general knowledge (Lowe 2010; Knighty 2010) | — | **U** |
| Enhanced (over-relaxed) sphere tracing | Keinert et al. 2014 | — | **U** |
| **Perturbation** (K. I. Martin, SuperFractalThing, 2013): one high-precision reference orbit Z; per pixel low-precision deltas zₙ₊₁ = 2Zₘzₙ + zₙ² + c; "even single precision (with 24 bits) can be used successfully"; float32 underflows below ~1e-38, double below ~1e-308; **rescaling** (z = S·w, c = S·d) or floatexp extends range | https://mathr.co.uk/blog/2021-05-14_deep_zoom_theory_and_practice.html | 2021-05-14 | V |
| **Rebasing** (Zhuoran 2021): when \|Zₘ + zₙ\| < \|zₙ\|, set zₙ ← Zₘ + zₙ and m ← 0 — glitches avoided rather than detected; one reference per critical point | https://mathr.co.uk/blog/2022-02-21_deep_zoom_theory_and_practice_again.html ; https://mathr.co.uk/web/deep-zoom.html | 2022-02-21; undated | V |
| **Bivariate linear approximation (BLA)**: z_{n+l} = A·zₙ + B·c, valid when \|z\| < R; single step A = 2Zₘ, B = 1, R from ε·\|Zₘ\| (ε the hardware precision, e.g. 2⁻²⁴); merge neighbours: A_z = A_y·A_x, B_z = A_y·B_x + B_y, R_z = max(0, min(R_x, (R_y − \|B_x\|·\|c\|)/\|A_x\|)); table of O(M) entries built by levels starting at iteration 1; lookup takes the largest valid skip, then rebases | same pages; https://mathr.co.uk/tmp/mandelbla.pdf | 2022; undated | V |
| Fractalshades (Python) uses the same rebasing + BLA scheme and a (float64, int32) extended type below 1e-308 | https://gbillotey.github.io/Fractalshades-doc/math.html | undated (v1.2.1) | V |
| **Fractal flames** (cuburn): an IFS of "xforms" (pre-affine → weighted sum of variations → post-affine), density-weighted xform choice, a colour coordinate per point; "fuse" iterations discarded before recording; 2D histogram accumulation; log-density scaling; "density estimation" = variable-width Gaussian filter in low-density cells; animation by interpolating genomes over time | https://docslib.org/doc/4442770/cuburn-a-gpu-accelerated-fractal-flame-renderer | undated | V |
| Real-time flames in a compute shader (blog) | https://wrighter.xyz/blog/2023_08_17_flame_fractals_in_comp_shader | 2023-08-17 | U (title only) |
| The flame algorithm paper (Draves & Reckase) | — | 2003/2008 | **U** (the algorithm above is taken from the cuburn text, which was read) |
| flam3, Fractorium, Kalles Fraktaler are copyleft; Shadertoy's default licence is CC BY-NC-SA 3.0 | general knowledge | — | **U** — treat all as "read the maths, copy no code" |
| AMD FidelityFX **FSR 1** (EASU upscale + RCAS sharpen) is **MIT**-licensed ("Copyright (c) 2021 Advanced Micro Devices… Permission is hereby granted, free of charge…") | https://raw.githubusercontent.com/GPUOpen-Effects/FidelityFX-FSR/master/license.txt | 2021 | V |

**Measured here (M).** Deep zoom at 1e12 (192×192, max 3000 iterations, seahorse valley −0.743643887037151 +
0.131825904205330i): **float32 GPU perturbation with rebasing** against a double reference orbit agrees with a CPU
double-precision perturbation twin on **90.0 % of pixels exactly and 92.5 % within ±1 iteration; the mean relative
iteration error over escaping pixels is 0.27 %**. The disagreements sit almost entirely in the 2000–3000 iteration
band next to the set (2754 of 28 657 pixels there; 1 of 8464 in the 1000–2000 band). **Plain float32 iteration at the
same zoom gets 0 %: the whole image is one value.** A Mandelbulb DE raymarch (128 steps, 5 iterations) at 480×270 takes
13–38 ms on llvmpipe.

---

## 4. Reactive

| Fact | Source | Date | V/U |
|---|---|---|---|
| MediaPipe **Selfie Segmentation**: two models (general 256×256×3, landscape 144×256×3, ~250 KB), MobileNetV3-like, person/background mask in 0–1, real-time on a laptop CPU; **Apache License 2.0** | https://storage.googleapis.com/mediapipe-assets/Model%20Card%20MediaPipe%20Selfie%20Segmentation.pdf | undated | V |
| ONNX exports of the selfie segmenter exist (Qualcomm AI Hub, `license: apache-2.0`, ~106 K parameters, ONNX ~0.9–1.4 ms on phone NPUs) | https://huggingface.co/qualcomm/MediaPipe-Selfie-Segmentation | undated | V |
| PlayMap.Vision already runs ONNX locally, with a licence-aware `ModelCatalog` (never offers non-commercial by default) | `src/PlayMap.Vision/Models/ModelCatalog.cs` | — | V |
| Notch Image Emitter uses an Optical Flow node's motion vectors so particles follow the video; Depth Image Collision Affector collides with depth cameras | Notch pages above | 2025–2026 | V |
| Audio analysis for reactivity is reserved for another package: sources `audio:level|bass|mid|high|onset|fft/<bin>`, `beat`, `bpm`; seams `IModulationBus`, `IAudioFrames` | `r2/design/effects.md` §3.7, §4.4 | 2026-10-13 | V |
| Audio input seam `IAudioInputs`/`IAudioCapture` (OpenAL Soft capture; FFmpeg `file:`/`alsa`/`pulse`/`dshow`) | `r2/design/timecode.md` M9, §4.5 | 2026-10-13 | V |
| Surface G-buffer: per surface, the mesh rasterised in UV space into `PM_POSITION` (RGBA32F), `PM_NORMAL` (RGBA16F), `PM_EDGE` (R16F) | `r2/design/effects.md` §3.2 | 2026-10-13 | V |

---

## 5. Design implications for PlayMap

1. **Particles at scale on GL 3.3 and ES 3.0, no compute.** State in RGBA32F textures (position+age, velocity+life,
   attachment), updated by one fragment pass into MRT and ping-ponged; drawn as instanced quads that fetch their
   particle with `texelFetch(gl_InstanceID)`. This is ES 3.0 core plus `EXT_color_buffer_float` (present on Mesa;
   ANGLE D3D11 must be confirmed on the Windows CI leg). Accumulate into **RGBA16F**, never blend into RGBA32F
   (`EXT_float_blend` is an extra requirement). 1 M particles is the default ceiling, 4 M with `quality ultra`.
2. **Determinism without integrating forever: "bounded memory".** Each slot is reborn on a fixed schedule
   `phase_i + k·life_i` from an integer hash, so the field at step n depends only on steps n − maxLife … n.
   **Measured: bit-identical after one maximum lifetime** on the same GPU and driver. Consequences:
   - a late picture, a backup node or a seek converges by prewarming one lifetime of steps (480 steps for 4 s, a
     cue-time preroll);
   - births use integer hashes (exact on every GPU), so even different vendors birth identical particles; only
     the float integration may drift, and only within one lifetime;
   - emission rate is applied as "this life is dormant" at the birth step, so a rate that is a function of show
     time (an LFO, a sting envelope, a pre-analysed track) keeps the guarantee, and a live rate is badged `live`.
3. **Beyond Notch for mapping: particles that know the stage.**
   - Emit from surfaces, fold edges and boundaries with an area- or length-weighted alias table.
   - Collide with and **follow** the real stage through a baked SDF (Notch's Follow Contours) built off-thread from
     the stage meshes. PlayMap's SDF is signed by the nearest face's normal, so open scans and facades work.
   - Particles can **stick**: snow settling on ledges.
   - Draw **in each surface's UV space** (on-surface particles: correct from every seat and seamless across
     projectors) or **from the audience viewpoint** through the existing `SurfaceMapping.Projection` (particles in
     the air, anamorphic).
   - Emit from any picture (video, live camera, canvas) weighted by luma, alpha, edges, motion or a person matte,
     with flow from the picture's gradient or optical flow.
4. **Fractals: three families, all pure functions of show time.**
   - **3D DE raymarching**: Mandelbulb, Mandelbox, KIFS, quaternion Julia, Apollonian. Render at 0.5–0.75 scale
     and upscale with FSR 1 (MIT).
   - **Deep zoom** to 1e30 with a BigInteger reference orbit on the CPU, float32 perturbation + rebasing + BLA on the
     GPU. ES 3.0 has no doubles, and the measurement shows float32 is good enough: 92.5 % within ±1, 0.27 % mean
     error, versus 0 % for plain float.
   - **Fractal flames**: a stateless chaos game each frame, seeded by the frame index, splatted additively into
     RGBA16F with log-density tone mapping.
   - **Stage space**: the fractal sits *inside* the object, seen from the audience viewpoint through the surface
     G-buffer. No competitor maps a raymarched fractal onto calibrated multi-projector geometry in the same tool.
5. **Reactive: wire what exists, then add the stage and the camera.**
   - Fill `ReactiveView.Level/Low/Amount` from `audio:*` (Patterns' rule: the sound moves the scene, never drives
     it; swing ≤ `FlashGuard.MaxAudioBrightnessSwing`).
   - Add spectrum, Voronoi and wave scenes.
   - Add camera motion energy and flow (GPU, low resolution) and a local person matte (MediaPipe selfie, Apache-2.0,
     ONNX through PlayMap.Vision).
   - Add surface-aware looks computed from mesh analysis rather than screen derivatives, so they agree across
     projectors:
     - crease and curvature;
     - geodesic "grow" from seeds (the building draws itself);
     - self-shadowed relight from a sun path, a projector or the audience;
     - gravity-flow direction for water and lava.
   - **Pre-analysed show audio** makes reactivity deterministic: a function of media time, mirrored on backups and
     chaseable by timecode. Live-input reactivity cannot do that.
6. **Time hygiene (a latent bug).** Shaders receive float32 seconds of a leader clock that counts from the leader's
   boot. Each generator needs an origin stored in the model (stamped on NEW and RETIME), item time when it sits in a
   playlist (answers timecode §9.8), double-precision time on the CPU with periodic phases wrapped there, and a STATE
   warning past 2¹⁶ s.
7. **Budgets for 4K60 on a mid GPU** (reference: RTX 4060 / RTX A2000 class, about 15 TFLOPS FP32). All of these
   are estimates (E) to measure in the lab with timer queries.

   | Content | Budget |
   |---|---|
   | 1 M particles | about 0.3 ms for 2 steps + about 0.6 ms to draw |
   | DE raymarch | at 1920×1080 internal, about 4 ms + FSR 0.3 ms |
   | Deep zoom with BLA | about 3 ms |
   | Flames, 1 M walkers × 20 iterations | about 2–3 ms |
   | Closed-form scenes | ≤ 0.3 ms |

   Each generator has a `quality` (render scale, particle share, iteration cap). Per-generator GPU ms go to STATE.
   The governor is **advise-only** by default (doctrine: measured, then governed).
8. **Presets and modulation.**
   - Every numeric parameter can be bound through the shared `ParamBinding` / `IModulationBus` (effects package).
   - Curated packs ship for each new kind; user presets live in the project.
   - **Deterministic look morphs** (`GENERATOR MORPH … OVER 8 s AT …`) are a pure function of show time, so every
     node crossfades looks identically.
   - The verbs give Companion buttons (presets, morphs with a progress ring, stings) and encoders (`SET … BY`) with
     no extra work.
9. **Licences.** Implement from the maths and the papers only. Copy nothing from flam3, Fractorium, Kalles Fraktaler
   or Shadertoy (CC BY-NC-SA by default) **(U)**. FSR 1 is MIT and may be ported with its notice. The MediaPipe
   model is Apache-2.0 and is fetched at run time by Vision's model store. Ableton Link (beat sync) is not
   MIT-compatible **(U)** and stays out.
