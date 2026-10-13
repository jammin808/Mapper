# Work package "firstfive": the first five minutes — on-wall editing and the quad path

*Design for round 2, 13 October 2026. Research: `r2/research/lessons.md` (cited as L§n). Benches: `r2/bench/lessons/`
(`quad_tess.py`, run here). Code read: `main` at c12b186. Implements ROADMAP R1.5 (edit overlay), R1.6 (quad path),
the cursor and highlight halves of R1.12, M20's "Light a wall", and docs/UX.md §4.1 path A, §5.2 and P2/P5/P8/P13.*

This package lets someone with a laptop and one projector put a clip on a wall in under two minutes without reading
anything. They pick the projector, click four corners while looking at the wall, nudge with the arrow keys and drop a
clip. It also builds the **edit overlay**: a pass on every projector output that draws the cursor, outlines, names,
handles and a flash grid on the real surface. The calibration package draws its confidence map through it, and so will the
phone and the glasses.

---

## 1. Goal and the bar

| What the best do (L§) | PlayMap's answer | Match or beat |
|---|---|---|
| MadMapper's first guide: test pattern to focus, add a quad that **already shows the Test Card**, drag its handles in the Output view, then **arrow keys** for precision (L§1.1–1.2). Tab cycles corner points; video cursor and highlight selection sit behind three-key chords (L§1.3) | `LIGHT WALL` puts the output on the projector, shows the identify card and turns the overlay and cursor on in **one line**. Q, then four clicks **on the wall**. A new quad shows the **UV checker**. Arrows nudge 1 px, Shift 10 px, Alt 0.1 px, Tab cycles corners, Enter accepts | **Beat**: no output setup screen, no fullscreen toggle, and the cursor is on by default during mapping. Sub-pixel nudges in **wall pixels**, even through a corner-pinned output |
| HeavyM: a **crosshair on the output** lets you "draw while looking directly at the projection" (L§2.1). Illustrator grammar for drawing (L§2.2) | The **edit overlay pass**: each client's cursor in its own colour, the clicks so far with a rubber band, outlines of every surface (3D meshes projected through the solved projector, depth-tested), names as upright labels, handles with the active one enlarged, and a 2 s flash grid. All of it goes through the output's warp and lens lookup, so lines land where content lands | **Beat**: HeavyM draws a cursor; PlayMap draws the whole edit state, in 3D, for every client in the room |
| VYV projects a green cross per calibration point and a **Display Grid** that "exaggerates the blurriness" of misalignment (L§4.6) | Pins drawn on the wall with residual rings (green < 1 px, amber 1–3, red > 3); `OVERLAY GRID` shows a fine grid for 2 s through the warp | **Match**, plus residual colour on each pin |
| MadMapper "Lock Surfaces Movement" (L§1.3); Pixera locks block edits, never playback (L§6.1) | `Surface.Locked` refuses corner and node edits with the reason; the overlay draws a locked surface's handles hollow | **Match** |
| A lattice in MadMapper or HeavyM starts from the quad's corners; densifying re-interpolates | A quad's lattice is an **offset field on top of its homography**, so promoting a quad to a lattice changes **0.000 px** by construction. The bench shows a plain grid would move content by up to 307 px (two triangles) or 0.84 px (32×32) on a strong keystone (L§9.1) | **Beat**: the promised shape-keeping is exact, and content mapping is exactly projective (homogeneous UVs, error ≤ 7×10⁻¹³ px) |
| Everyone measures "first success" in marketing copy only | The engine stamps the milestones (lit, first quad, first content on a quad) in `STATE.onboarding`, and the desk shows "First map in 1:24" | **Beat**: measured, local, never sent anywhere |

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **`PixelQuad` surface kind**: `Surface.Pixel` (projector, four corners in raster-normalised units, optional lattice of offsets) and the `PixelQuadMath` it needs (corner ordering, convexity, homography, evaluation, inverse, Jacobian, lattice resample) | Unit tests: corners clicked in any of the 24 orders store TL, TR, BR, BL. A bow-tie or concave set is refused with "the corners cross — click them around the shape". `Evaluate(u,v)` at the four corners returns them to 1e-12. A rest lattice of any size gives `Evaluate` equal to the plain homography to 1e-12 at 10 000 random (u,v). `Invert(Evaluate(u,v))` round-trips to ≤ 1e-9 in UV for a 3×3 lattice with 5 % offsets |
| M2 | **Rendering pixel surfaces** in the projector pass: program `pixelsurface` with homogeneous (s,t,q) UVs; lattice quads tessellated 8×8 per cell | llvmpipe read-back on 1920×1080 with a 25 % keystone quad and `pattern.uvchecker`: the four checker corner marks land within **0.5 px** of H(u,v), and the quad's edge pixels within 1 px of the clicked corners. Mask in UV (a 0.25 × 0.25 Subtract rectangle) blacks exactly the expected region (IoU ≥ 0.98) |
| M3 | **Verbs**: `LIGHT WALL`, `SURFACE QUAD`, `SURFACE CORNER`, `SURFACE LATTICE`, `SURFACE NODE`, `HANDLE SELECT\|NEXT\|PREV\|NUDGE`, `OVERLAY`, `OVERLAY GRID`, `CURSOR` (grammar in §4.2) | Engine tests on fields: `SURFACE QUAD` answers an id and `GET` shows `pixel.topLeft` etc. 20 `SURFACE CORNER … BY 1 0` lines within the coalesce window are **one** undo step, and UNDO restores the corner exactly. `CURSOR` lines write no journal row and do not raise the STATE revision. Every verb refuses on a locked surface, an unknown id or (with a fake show gate) an armed show, each with its own words |
| M4 | **Coordinates in wall pixels.** Every coordinate a client sends is in output (post-warp) space by default, with `PX` for wall pixels and `RASTER` for raster-normalised. The engine converts with `WarpField.Invert` and the warp's local Jacobian | On an output with a 10 % corner pin: `SURFACE CORNER … BY 1 0 PX` moves the corner's **wall** position by 1.000 ± 0.01 px along x (tested with `WarpField.Evaluate`). A point outside the warp's domain is refused with its words |
| M5 | **Edit overlay pass** (`OverlayPass`): a pre-warp layer (outlines, handles, pins, grid, heat) composited through the warp mesh and lens lookup (not the blend map, masks or trim); a post-warp layer (cursors, output-warp handles, names). Off when armed, in blind edit, or on outputs not named | llvmpipe read-back: (a) a 3D box surface's projected outline pixels lie within 1 px of the CPU projection through the warp; hidden edges (behind another face) are dashed at ≤ 35 % alpha; (b) a cursor at (0.5, 0.5) on a rotated (90°) output draws its centre at the display's centre ± 1 px; (c) with a fake gate saying armed, the output is **byte-identical** to the overlay-off picture; (d) blend map at 0 on the outline's location still shows the outline |
| M6 | **Cursor board**: per-session cursors (colour, trail of clicked points), leased for 3 s, read lock-free by the render thread | A cursor not renewed for 3 s disappears from the next frame. Eight sessions draw eight crosshairs in their colours. The render thread allocates **0 bytes per frame** with two cursors moving at 60 Hz (`RenderHostStats.FramePathAllocatedBytes` flat over 600 frames) |
| M7 | **Cursor latency** measured: from the line's arrival on the lane to the frame that draws it | Headless llvmpipe at 60 Hz: p95 ≤ **2 frame periods** (33.4 ms) over 300 moves. The number goes into `STATE.overlay.cursorP95Ms` |
| M8 | **`LIGHT WALL`**: binds an output to a display, fullscreen, Projector mode with a projector (nominal throw 1.5 or the linked device's lens prior), overlay and cursor on, identify card 3 s; one undo step for the document part | With `FakeOutputBackend` listing two displays, `LIGHT WALL "EPSON PJ"` sets `op_1.displayKey`, raster and refresh from the display, `mode:"projector"`, a new `pj…` with throw 1.5, and `air.overlay.on:true`. UNDO restores the default project exactly. No display: refused with "this node sees no display — headless nodes cannot light a wall" |
| M9 | **Onboarding milestones** in `STATE.onboarding` | A scripted run (`LIGHT WALL` → `SURFACE QUAD` → 8 nudges → `MEDIA IMPORT` of the 2 s test clip → `SURFACE SOURCE`) on the real headless engine with llvmpipe and FFmpeg 9 sets `stage:"content"` and all four timestamps. The read-back shows the clip's mean colour inside the quad and black outside. The machine time is recorded (expected under 5 s) |
| M10 | **Desk**: Q quad tool (Select moves to S), four clicks with a rubber band and a live wall cursor, arrows / Shift / Alt nudges, Tab cycles, Enter, Esc, G grid, O overlay, L lattice 3×3. The first-run card "Light a wall" → display list → `LIGHT WALL` → Q armed → "Drop a clip" | Avalonia headless tests through the real input path with `FakeEngineLink`: the key and click sequence emits exactly the expected lines. Ctrl+Left still sends the transport's previous-item line in MAP. Alt+Right sends `BY 0.1 0 PX` |
| M11 | Carry-over: the desk reads `air.pattern` as an object (it is one) | The desk's `StateSnapshot` parses `{"kind":…,"outputs":[…]}`; a test feeds a real STATE line |
| M12 | STATE, context, VERBS, WIRE.md, UX.md §4.1/§5.2 updated | Catalogue tests: each new verb has syntax, risk, `Mutates`, `Ephemeral`/`Gesture` flags; `HELP SURFACE QUAD` prints the grammar |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | `HIGHLIGHT <id>… [FOR <s>] \| HIGHLIGHT OFF`: pulse outlines for N s even with the overlay off (rehearsal only), for the phone, the glasses, Companion's "find" and the assistant's "show me" | Read-back: the highlighted surface's outline alpha follows 0.5 + 0.5·sin(2π·t) of **show time**; it is gone after N s |
| S2 | `PICK PIXEL <output> <x> <y>`: which surface, UV and handle are under a wall point (quads by inversion, meshes by ray cast) | Click on a quad corner within 12 px answers its handle. A click on a box face answers that surface and the UV within 1e-3 of the mesh's |
| S3 | Snapping on the desk: other quads' corners and edges, raster edges, within 8 screen px; Ctrl suspends | Desk test: a corner dropped 5 px from another quad's corner sends that corner's exact coordinates |
| S4 | Carry-over from the stage integration: the **projector VIEW** shows black around the picture (not grey) and applies the lens lookup like the output | Read-back of `VIEW … projector:<id>` equals the output's pre-trim picture within 1 code |
| S5 | Pixel quads on the desk's 3D view as flat cards on the projector's image plane at 1 m (derived, never stored) | Desk scene test: a quad's card corners lie on the four corner rays |
| S6 | `OVERLAY … LAYERS warp`: output corner-pin and lattice handles on the wall while the Warp tool is active (post-warp) | Read-back: corner-pin handles at the pin's corners ± 1 px |

### Later

Click-to-segment in the quad tool (snap corners to the smart scan's edges and depth creases); "Lift to 3D" (turn a quad into
mesh faces once the projector is solved and a mesh exists); surface-UV lattices on 3D surfaces (R4.7 neighbourhood);
mask outlines for 3D surfaces on the wall (needs UV→mesh inversion); a transition when a surface's source changes (UX §5.2
"loops with a 0.5 s fade"); Simple/Pro switch (P14); the iPad Pencil path (UX §4.3).

---

## 3. Architecture

### 3.1 Data flow

```
 desk / Companion / phone / AI                engine (control lane)                     render thread (one per node)
 ───────────────────────────                  ─────────────────────                     ────────────────────────────
 mouse move on ProjectorCanvas ─CURSOR (≤60 Hz)─▶ CursorBoard.Set(session,…)  (lock-free, no journal, no STATE rev) ─┐
 Q + clicks ───────────────────SURFACE QUAD ────▶ PixelQuadMath → Store.Apply (one undo step)   ── ProjectSnapshot ──┤
 arrows ─────────────────────── SURFACE CORNER … BY (coalesced) ▶ Store.Apply(coalesceKey)                         ├─▶ Frame():
 O / G / L ──────────────────── OVERLAY / OVERLAY GRID / SURFACE LATTICE ▶ SetAir(a ⇒ a.Overlay …)  ── AirState ────┤   FrameInputs.Overlay
 first-run card ─────────────── LIGHT WALL ─────▶ one edit + SetAir(overlay, identify)                              │   = (OverlayState, CursorBoard.Snapshot,
                                                                                                                    │      Armed, LeaderNs)
                                                                                                                    └─▶ RenderEngine.DrawOutput
                                                                                                                         ├ projector pass (3D surfaces, then pixel quads)
                                                                                                                         ├ OutputStage.Draw (warp, mask, blend, lens, trim)
                                                                                                                         ├ OverlayPass.Draw  ◀── OverlayGeometry cache
                                                                                                                         └ identify card
```

Threads: the control lane owns the document and the air; the `CursorBoard` is written by the lane (from the `CURSOR`
handler) and read by the render thread through a `volatile` immutable array swap. Nothing new runs on any other thread.

### 3.2 Pixel quads: the maths (`PlayMap.Core/Mapping/PixelQuadMath.cs`)

**Storage.** Corners `TopLeft, TopRight, BottomRight, BottomLeft` are in **raster-normalised** units of the projector's output
raster (x ÷ width, y ÷ height; (0, 0) is the raster's top-left corner, the convention of `CornerPin` and output masks).
The lattice (`WarpLattice`, the existing record) holds offsets in the same units.

**Position of content point (u, v) ∈ [0,1]²:**

```
H        = QuadHomography.TryUnitSquareToQuad(TL, TR, BR, BL)         (exists: closed form, row-major 3×3)
p(u, v)  = Apply(H, (u, v)) + L(u, v)
L(u, v)  = LatticeSurface(lattice).Evaluate(u, v) − (u, v)            (zero for a rest lattice: Catmull-Rom, Bézier
                                                                       and linear reproduce a regular grid)
```

So a quad promoted to a lattice has `L ≡ 0`: **the shape is kept exactly**. A lattice node dragged to wall point w gets
offset `o_ij = raster(w) − Apply(H, rest_ij)`; since every interpolation in `LatticeSurface` passes through its nodes,
the node lands exactly there. Moving a corner of a latticed quad moves `H`, and the whole lattice rides on it (the
"affine lock" of UX §4.2 for free).

**Ordering clicks.** Centroid c of the four points; sort by `atan2(y − c.y, x − c.x)` ascending (clockwise on a y-down
raster); rotate the list so the first is the point minimising x + y (top-left). Then `QuadHomography.IsConvex` must hold
(strictly convex, consistent turn) and the area (shoelace) must be ≥ 64 px²; otherwise the line is refused with the
reason.

**Inverse** (for `PICK PIXEL` and for drawing UV-space items): (u, v)₀ = H⁻¹(x); then Newton on p(u, v) − x with the
Jacobian `∂p = ∂H + ∂L` (`LatticeSurface.EvaluateWithDerivatives`), at most 8 steps, converged at 1e-10.

**Resizing a lattice** (`SURFACE LATTICE … 5x5` on a 3×3): new offsets = the old field L sampled at the new rest nodes.
The shape change is reported as the maximum |p_new − p_old| over a 64×64 sample of (u, v), in wall pixels (0 for a rest
lattice; small, non-zero for a bent one under Catmull-Rom, because new nodes change the tangents).

**Wall ↔ raster.** `WarpField field = new(output.Warp)` (cached per `Output` reference). Wall-normalised w → raster r =
`field.Invert(w.X, w.Y)` (null → refused: "that point is outside the output's warped picture"). A wall-pixel delta d at
raster point r → raster delta `J⁻¹·(d.x/W, d.y/H)`, with `J = field.Jacobian(r)` (exists). With an identity warp both are
the identity.

**Drawing** (`PixelQuadGeometry.Build(PixelQuad, width, height)` → interleaved `float x, y, s, t, q`):
- No lattice: the four corners, two triangles, `q_i = 1 / w_i` where `w_i` is the homography's third row at (u_i, v_i),
  and `(s, t) = (u_i·q_i, v_i·q_i)`. The fragment shader takes `uv = vec2(s, t) / q`. The bench (L§9.1) shows this is
  exact (≤ 7×10⁻¹³ px) where a 32×32 affine grid is up to 0.84 px wrong.
- With a lattice: a grid of `(C−1)·8 + 1 × (R−1)·8 + 1` vertices at p(u, v), each with its own homogeneous (s, t, q) from
  H, so only the lattice's own smooth offsets are interpolated linearly within each sub-cell.
- Cached per surface by the `PixelQuad` reference and the raster size; built on the render thread on change (target
  ≤ 0.2 ms for a 9×9 lattice: 4 225 vertices; estimate, measured by a test).

**Render.** `StageRenderer.RenderProjector` draws the 3D surfaces, then `DrawPixelSurfaces`: every visible surface whose
`Pixel.ProjectorId` is this projector, in surface-list order, depth test off, opacity blending as for stage surfaces.
Program `pixelsurface`: the vertex shader maps raster pixels to NDC (y mirrored, as for every target drawn top row first);
the fragment shader shares the stage program's content sampling (mode, region, the surface mask texture in UV, opacity)
through a common GLSL snippet in `ShaderLibrary`. A surface-aware effect sees position (u, v, 0) and normal (0, 0, 1).
Pixel quads draw **over** 3D surfaces where they overlap (2D is a layer on top, as in every 2D mapper). Auto-blend,
coverage and MPCDI ignore them: they belong to one projector.

### 3.3 The edit overlay (`PlayMap.Render/Overlay/`)

**State.** `OverlayState` (in the air: which outputs, which layers, the active handle, the grid until when, the heat map's
projector) changes at human speed and is part of STATE. Cursors are not: they live on the `CursorBoard`.

**Two layers per output** (only for outputs the overlay covers):

1. **Pre-warp layer**: an RGBA8 target at the output raster, cleared to transparent, sharing the **depth attachment** of
   the projector pass (`st.Projector`), so 3D outlines are depth-tested against what the projector actually lit.
   - 3D outlines: `SurfaceOutline.Edges(mesh, faces)` gives boundary edges (used by one triangle of the set) and crease
     edges (dihedral > 35° between two triangles of the set), capped at 20 000 segments per surface (longest kept),
     cached by (mesh content hash, faces hash). Drawn with program `overlay.lines` as **instanced screen-space quads**
     (core profile lines are 1 px): per instance `vec3 a, b; vec4 colour; float widthPx; float dash`; the vertex shader
     transforms both ends with the projector's view-projection (the stage's own `ViewProjection`), offsets by ±width/2
     along the screen-space normal, and biases depth by 2e-5 (NDC) toward the eye. Pass 1: `LEQUAL`, full alpha. Pass 2:
     `GREATER`, 35 % alpha, dashed every 6 px on `gl_FragCoord` (hidden edges). The fragment shader antialiases with
     `smoothstep` over the last pixel of width.
   - Pixel quads: the outline is p(u, v) along the four edges (16 segments per edge with a lattice, 1 without), in
     raster pixels, depth test off; lattice lines and nodes; corner handles 11 px squares, nodes 9 px circles; the
     active handle 17 px with a white ring whose alpha pulses at 1 Hz from **show time** (content stays f(show time)).
   - Pins (`OverlayLayers.Pins`, the selected projector's): a 9 px cross at `pin.Pixel`, with a ring coloured by the
     pin's residual when the calibration package has measured it (green < 1 px, amber 1–3, red > 3).
   - Flash grid (`OVERLAY GRID`): 1 px lines every 32 raster px at 60 % white, 2 px every 128 px, until
     `GridUntilLeaderNs`.
   - Heat (`OVERLAY HEAT`, filled by the calibration package): a float texture of predicted error per cell, drawn
     through a colour ramp (green ≤ 0.5 px, yellow ≤ 1, orange ≤ 2, red > 2, hatched grey = unmeasured) at 45 % alpha,
     plus numbered labels at the problem areas.
2. **Composite**: `OutputStage.Draw` with the pre-warp layer as the picture, **the same warp mesh and lens lookup**,
   no mask, no blend map, a neutral trim, no dither, and a new `Over` flag (premultiplied "over" onto `st.Final` instead
   of replacing it). The output shader gains `uniform int uOver;`: when 1 it writes the premultiplied texel and skips the
   mask, blend and trim. The lines therefore bend exactly as the content does under a corner pin or lattice, and are
   never dimmed by a blend edge.
3. **Post-warp layer**, drawn straight into `st.Final` in output pixels, turned by the output's quarter-turns as
   `OutputStage` turns the picture:
   - Cursors: a crosshair (arms 24 px, gap 6 px, 3 px wide, in the session's colour with a 1 px dark outline); the trail
     (the corners clicked so far) as 7 px dots joined by a 2 px line closing to the live cursor (the rubber band); the
     client's name in 18 px text when two or more cursors are up.
   - Names: an upright label (Inter SemiBold 28 px from a glyph atlas built once with Skia from `HouseFonts`) on a
     60 % black pill at `field.Evaluate(anchor)`. The anchor is the quad's centre p(0.5, 0.5), or the projected centroid
     of a 3D surface's faces when it falls in the raster.
   - Output-warp handles (S6) when the Warp tool is active.

**Visibility rules.** No overlay at all when `FrameInputs.Armed` (the show-safety package's gate; without it, false), in
blind edit, or on an output the overlay does not name. `PATTERN` does not hide it: outlines over the grid pattern are
the point.

**Caching and budget.** `OverlayGeometry` is rebuilt on the render thread only when its key changes: (project revision,
`OverlayState` reference, projector pose, output raster and warp reference). Target ≤ 3 ms for 100 surfaces on a
50 k-triangle scan mesh (edge extraction runs once per mesh); a llvmpipe test measures it and guards the number. The pre-warp layer is redrawn
only when the key or the pulse phase changes. Cursors are redrawn each frame from pooled vertex arrays (≤ 8 cursors ×
(4 arms + trail) quads). Per-frame GPU cost on a desktop GPU is an estimate: ≤ 0.3 ms at 1080p (one composite and one
small draw). The frame path allocates nothing once warm.

### 3.4 Cursors (`PlayMap.Engine/Overlay/CursorBoard.cs`)

`CursorBoard` holds `ImmutableArray<WallCursor>` behind a `volatile` field. `Set(session, output, at, trail, colour,
expiresLeaderNs)` replaces that session's entry (newest wins; at most 8 sessions, oldest dropped) and swaps the array.
The lane writes; the render thread reads once per frame and skips expired entries by **leader time** (3 s lease; the desk
renews at its send rate, so a closed laptop's cursor vanishes on its own). The board has its own revision, which is
**not** the STATE revision: cursor traffic never triggers a STATE push. STATE shows only the count and the measured
latency.

**Latency path and budget** (UX §6: "projector output reflects a drag ≤ 2 output vblanks + projector input lag"):
desk throttle (≤ 16.7 ms at 60 Hz, sent on the leading edge) → WS line (≪ 1 ms on loopback, ≤ 2 ms wired) → lane
(≪ 1 ms) → next frame's read (0–1 frame) → present (1 frame) → projector lag (not ours). Each `WallCursor` carries the
lane's receipt time; the render thread stamps the first frame that draws it and feeds a 300-sample ring whose p95 is
`STATE.overlay.cursorP95Ms`.

### 3.5 `LIGHT WALL`: one line to first light

Handler (control lane), one `Store.Apply("light wall …")`:
1. Resolve the display (`IOutputBackend.Displays()`) by key, EDID name (case-insensitive), or `#n` in list order.
2. Choose the output: one already on that display, else the first enabled output with an empty `DisplayKey`, else a new
   `op…`. Set `Kind = Display`, `DisplayKey`, `Width/Height/RefreshHz` from the display, `Enabled = true`, `Mode =
   Projector`.
3. Choose the projector: the output's, else a new `pj…` named "Projector n" with
   `Intrinsics.FromThrowRatio(w, h, throw)`, where throw is the linked device's `LensPrior.ThrowRatio`, else the `THROW`
   argument, else 1.5. Pose `Pose.LookAt((0, 1.5, 4), (0, 1.5, 0), +Y)`, distortion none, colour from the palette.
4. Air (not undoable): overlay on for that output with `Cursor | Outlines | Names | Handles`; identify card until
   leader + 3 s (the existing `IdentifyUntilLeaderNs`).
5. Onboarding: `LitAt` if unset.
The reply's JSON carries the next step: `{"output":"op_1","projector":"pj…","display":"…","raster":"1920x1080@60",
"next":"SURFACE QUAD pj… <x1> <y1> … (click four corners on the wall)"}`.

### 3.6 Nudges, coalescing and the journal

`SURFACE CORNER`, `SURFACE NODE` and `HANDLE NUDGE` apply with coalesce key `"pq:<surface>:<handle>"`, so a burst of
arrow presses inside `ProjectStore.CoalesceWindowMs` (750 ms) is one undo step. They are **Gesture** verbs: no
journal row per line, and one row per coalesced run when the window closes ("Quad 1 TL moved 12.0 px"). This is the
control package's `Gesture` notion, shared rather than duplicated. `CURSOR`, `HANDLE SELECT|NEXT|PREV` are
**Ephemeral**: no journal, no undo, and no STATE revision for `CURSOR`.

### 3.7 Onboarding milestones

`OnboardingClock` (engine) records the leader-wall times of the first `LIGHT WALL` (`litAt`), the first pixel quad
(`quadAt`) and the first frame with a media or playlist source on a visible pixel quad of an enabled output
(`contentAt`, set by the render host when it draws it). `startedAt` is the node's start, or `PROJECT NEW`. Nothing leaves
the node. The desk turns the numbers into "First map in 1:24" and offers "Make this a show machine?" (the hardening
report, R2.9) only after `contentAt`.

---

## 4. Spine needs

### 4.1 Model (Core, all init-only, backwards compatible; an older file reads them as null/empty)

```csharp
// src/PlayMap.Core/Model/StageModel.cs (append) — or a new file PixelQuad.cs in the same namespace
/// <summary>A surface drawn in one projector's raster: four corners (raster-normalised, x ÷ width, y ÷ height; (0,0) the
/// raster's top-left corner) make a homography of the unit square, and an optional lattice adds offsets on top of it
/// (zero offsets are the plain quad). Lit by that projector alone; a projector solve never moves it.</summary>
public sealed record PixelQuad(
    string ProjectorId,
    Vec2d TopLeft,
    Vec2d TopRight,
    Vec2d BottomRight,
    Vec2d BottomLeft,
    WarpLattice? Lattice);

public sealed record Surface(...)   // existing positional record, unchanged
{
    /// <summary>Set: the surface is a quad in a projector's raster (MeshId "", Transform and Faces unused).</summary>
    public PixelQuad? Pixel { get; init; }          // json "pixel", default null
}
```

JSON: `"pixel":{"projectorId":"pj3f…","topLeft":{"x":0.112,"y":0.081},"topRight":{…},"bottomRight":{…},
"bottomLeft":{…},"lattice":null}`. Validation (in `ADD`/`SET` and the new verbs): the projector exists; `MeshId` is ""
when `Pixel` is set; the corners are strictly convex. `REMOVE pj…` with pixel quads on it is refused: "3 quads are drawn
in Projector 1's picture (Quad 1, Quad 2, Quad 3) — remove them or move them (SET <quad> pixel.projectorId …) first".

```csharp
// src/PlayMap.Core/Model/Overlay.cs (new) — air-level, not in the project file
[Flags] public enum OverlayLayers { None = 0, Cursor = 1, Outlines = 2, Names = 4, Handles = 8, Lattice = 16, Pins = 32, Warp = 64, Heat = 128,
    Edit = Cursor | Outlines | Names | Handles | Lattice, All = 255 }
public enum HandleKind { Corner, Node }
public readonly record struct OverlayHandle(string SurfaceId, HandleKind Kind, int Column, int Row);  // Corner: Column 0..3 (TL,TR,BR,BL), Row 0
public sealed record OverlayState(bool On, ImmutableArray<string> Outputs, OverlayLayers Layers, OverlayHandle? Handle,
    long GridUntilLeaderNs, ImmutableArray<string> GridOutputs, string? HeatProjectorId, ImmutableArray<string> Highlight, long HighlightUntilLeaderNs)
{ public static readonly OverlayState Off = new(false, [], OverlayLayers.Edit, null, 0, [], null, [], 0); }
public sealed record WallCursor(string Session, string Name, string OutputId, Vec2d At, ImmutableArray<Vec2d> Trail, string Color, long ReceivedMonoNs, long ExpiresLeaderNs);
```

`AirState` (Engine) gains `public OverlayState Overlay { get; init; } = OverlayState.Off;`. `FrameInputs` (Render) gains
`OverlayState Overlay`, `ImmutableArray<WallCursor> Cursors`, `bool Armed`. `OutputStageInputs` gains `bool Over`.

`VerbSpec` gains `public bool Ephemeral { get; init; }` and `public bool Gesture { get; init; }` (or uses the control
package's `Gesture` if it lands first; one definition, not two). `EngineCore.IsJournalled` becomes
`m.Spec.Mutates && !m.Spec.Ephemeral && !m.Spec.Gesture`.

### 4.2 Verbs (category `Mapping` unless noted; all need pairing; all refused while armed, with the gate's words)

| Verb | Grammar | Risk | Flags | Reply (JSON payload) |
|---|---|---|---|---|
| `LIGHT WALL` | `LIGHT WALL <display>\|#<n>\|<output> [AS <name>] [THROW <ratio>]` | High (an output opens) | — | `{"output","projector","display","raster","next"}` |
| `SURFACE QUAD` | `SURFACE QUAD <projector>\|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX\|RASTER] [AS <name>] [SOURCE <source>]` | Review | — | `{"id","projector","corners":{"tl":[x,y],…} (raster px),"areaPx","words"}` |
| `SURFACE CORNER` | `SURFACE CORNER <surface> TL\|TR\|BR\|BL\|0-3 TO <x> <y> [PX\|RASTER] \| BY <dx> <dy> [PX\|RASTER]` (TO defaults to wall-normalised, BY to wall px) | Review | Gesture | `{"id","corner","raster":[x,y],"wall":[x,y]}` |
| `SURFACE LATTICE` | `SURFACE LATTICE <surface> <c>x<r>\|OFF [LINEAR\|SMOOTH\|BEZIER]` (2–65) | Review | — | `{"id","lattice":"3x3","shapeChangePx":0.000}` |
| `SURFACE NODE` | `SURFACE NODE <surface> <col> <row> TO\|BY <x> <y> [PX\|RASTER]` | Review | Gesture | `{"id","node":[c,r],"raster":[x,y]}` |
| `HANDLE SELECT` | `HANDLE SELECT <surface> TL\|TR\|BR\|BL\|<col>,<row> \| HANDLE SELECT NONE` | Low | Ephemeral | `{"handle":{…}}` |
| `HANDLE NEXT` / `HANDLE PREV` | `HANDLE NEXT` | Low | Ephemeral | the new handle |
| `HANDLE NUDGE` | `HANDLE NUDGE <dx> <dy> [PX\|RASTER]` (the active handle; for encoders and Companion dials) | Review | Gesture | as `SURFACE CORNER`/`NODE` |
| `OVERLAY` | `OVERLAY ON\|OFF\|TOGGLE [ON <output>\|ALL] [LAYERS <a,b,…>\|EDIT\|ALL]` (category Output) | Review | — | `{"on","outputs","layers"}` |
| `OVERLAY GRID` | `OVERLAY GRID [<output>\|ALL] [<seconds 0.5–30, default 2>]` | Review | — | `{"until"}` |
| `OVERLAY HEAT` | `OVERLAY HEAT <projector>\|OFF` (the map comes from the calibration package; refused with words when none exists) | Review | — | `{"projector","cells","measured"}` |
| `CURSOR` | `CURSOR <output> <x> <y> [TRAIL <x> <y> …] [COLOR #rrggbb] \| CURSOR OFF` (wall-normalised) | Low | Ephemeral | none (silent `OK`) |
| `HIGHLIGHT` (S1) | `HIGHLIGHT <id>… [FOR <s>] \| HIGHLIGHT OFF` | Low | Ephemeral | `{"ids","until"}` |
| `PICK PIXEL` (S2) | `PICK PIXEL <output> <x> <y> [PX]` | Low | query | `{"surface","kind":"quad\|mesh","uv":[u,v],"raster":[x,y],"handle":{…}\|null}` |

Refusal words are concrete: "Quad 1 is locked — unlock it to move its corners", "the corners cross — click them around
the shape", "that point is outside Output 1's warped picture", "Output 1 is not in Projector mode — LIGHT WALL or SET
op_1 mode projector", "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)".

The generic verbs cover the rest: `SURFACE SOURCE` (exists), `SET sf… pixel.projectorId`, `SET sf… locked true`, `ADD
mask sf… {…}` (UV masks on quads work unchanged), `REMOVE`.

### 4.3 STATE and context

Added at the end of `air` (WIRE.md's rule):

```json
"overlay": {"on":true,"outputs":["op_1"],"layers":["cursor","outlines","names","handles","lattice"],
            "handle":{"surface":"sf…","kind":"corner","index":2},"grid":false,"heat":null,"highlight":[],
            "cursors":2,"cursorP95Ms":24.1}
```

Top level, after the last key:

```json
"onboarding": {"stage":"none|lit|quad|content","startedAt":1760…,"litAt":…,"quadAt":…,"contentAt":…,"seconds":84.2}
```

`stage.projectors[]` rows gain `"quads": n`. Context: an item `overlay` (kind `overlay`, light **amber**, words "edit
overlay on Output 1, Output 2") whenever the overlay is up. The desk colours its mode frame amber from it (UX §3.2).

### 4.4 Seams

```csharp
// PlayMap.Engine — read by the overlay and every mapping verb; the show-safety package implements it
public interface IShowGate { bool Armed { get; } bool BlindEdit { get; } string? WhyLocked(string verb); }
// Until that package lands: EngineServices.ShowGate defaults to a gate that is never armed.

// PlayMap.Render — the overlay as the engine hands it over each frame (no allocation: a value)
public readonly record struct OverlayFrame(OverlayState State, ImmutableArray<WallCursor> Cursors, bool Armed)
{ public bool Covers(string outputId) => !Armed && State.On && (State.Outputs.IsDefaultOrEmpty || State.Outputs.Contains(outputId)); }

// PlayMap.Render — heat cells supplied by the calibration package (null: none)
public interface IOverlayHeat { HeatGrid? For(string projectorId); }
public sealed record HeatGrid(int Columns, int Rows, float[] ErrorPx, byte[] State, ImmutableArray<(int Id, Vec2d Raster)> Labels);
```

### 4.5 NodeSettings and ProjectSettings

`NodeSettings.CursorLeaseMs` (3000) and `NodeSettings.OverlayMaxCursors` (8). No project settings: the overlay is not part
of the show.

---

## 5. Files

**New**
- `src/PlayMap.Core/Model/Overlay.cs`: `OverlayLayers`, `OverlayState`, `OverlayHandle`, `WallCursor`
- `src/PlayMap.Core/Mapping/PixelQuadMath.cs`: order, convexity, H, p(u,v), inverse, Jacobian, resample, wall↔raster
- `src/PlayMap.Core/Mapping/PixelQuadGeometry.cs`: tessellation with homogeneous UVs
- `src/PlayMap.Core/Mapping/SurfaceOutline.cs`: boundary and crease edges, cached by hash
- `src/PlayMap.Engine/EngineCore.Overlay.cs`: `LIGHT WALL`, `SURFACE QUAD/CORNER/LATTICE/NODE`, `HANDLE *`, `OVERLAY *`,
  `CURSOR`, `HIGHLIGHT`, `PICK PIXEL` handlers
- `src/PlayMap.Engine/Overlay/CursorBoard.cs`, `src/PlayMap.Engine/Overlay/OnboardingClock.cs`
- `src/PlayMap.Render/Overlay/OverlayPass.cs`, `OverlayGeometry.cs`, `GlyphAtlas.cs`
- `src/PlayMap.App/ViewModels/QuadTool.cs` (the click state machine and nudges), `src/PlayMap.App/ViewModels/FirstRunViewModel.cs`,
  `src/PlayMap.App/Views/FirstRunCard.axaml(.cs)`
- Tests: `tests/PlayMap.Core.Tests/Mapping/PixelQuadMathTests.cs`, `SurfaceOutlineTests.cs`;
  `tests/PlayMap.Render.Tests/PixelSurfaceTests.cs`, `OverlayPassTests.cs`;
  `tests/PlayMap.Engine.Tests/OverlayVerbTests.cs`, `LightWallTests.cs`, `CursorLatencyTests.cs`, `FirstMapScriptTests.cs`;
  `tests/PlayMap.App.Tests/QuadToolTests.cs`, `FirstRunTests.cs`

**Touched (small hunks)**
- `src/PlayMap.Core/Model/StageModel.cs`: `Surface.Pixel` init property (3 lines)
- `src/PlayMap.Core/Wire/VerbCatalog.cs`: rows for §4.2; `Ephemeral`, `Gesture` on `VerbSpec`
- `src/PlayMap.Engine/EngineCore.cs`: `AirState.Overlay`; `IsJournalled`; the `CursorBoard` field
- `src/PlayMap.Engine/EngineCore.Router.cs`: dispatch rows
- `src/PlayMap.Engine/EngineCore.Edits.cs`: pixel-quad validation in `ADD`/`SET`; the `REMOVE pj…` refusal
- `src/PlayMap.Engine/StateWriter.cs`: `air.overlay`, `onboarding`, `stage.projectors[].quads`
- `src/PlayMap.Engine/ContextBuilder.cs`: the `overlay` item
- `src/PlayMap.Engine/Rendering/RenderHost.cs` (`Frame`): fill `FrameInputs.Overlay/Cursors/Armed`; latency stamps; `contentAt`
- `src/PlayMap.Render/Engine/Contracts.cs`: `FrameInputs` fields, `OverlayFrame`, `IOverlayHeat`
- `src/PlayMap.Render/Engine/RenderEngine.cs` (`DrawOutput`): one call to `OverlayPass.Draw` before the identify card
- `src/PlayMap.Render/Passes/OutputStage.cs`: `Over` (blend state and `uOver`)
- `src/PlayMap.Render/Projection/StageRenderer.cs`: `DrawPixelSurfaces` after `DrawSurfaces` in `RenderProjector`
- `src/PlayMap.Render/Shaders/ShaderLibrary.cs`: `pixelsurface`, `overlay.lines`, `overlay.text`; `uOver` in `OutputFs`
- `src/PlayMap.App/ViewModels/ShortcutMap.cs`, `MapViewModel.cs`, `Controls/ProjectorCanvas.cs`, `State/StateSnapshot.cs` (air.pattern object, overlay, onboarding)
- Papers: `docs/WIRE.md` (verbs, STATE keys), `docs/UX.md` §4.1 and §5.2 (what is built, the measured numbers), `docs/ROADMAP.md` (R1.5, R1.6 status), `docs/ARCHITECTURE.md` (the overlay pass in the output path)

---

## 6. Dependencies

**Work packages**
- **show safety**: `IShowGate` (armed, blind edit). Until it lands, a gate that is never armed. The overlay and every
  verb here call it, so arming strips the overlay the day that package merges.
- **companion**: `SELECT` (S3) and `STATE.selection`. The overlay outlines the selection in white; if companion lands
  later, `HANDLE SELECT` sets the selection too. The Companion module is generated from `VERBS`, so the new verbs
  appear as actions with no work here (§8).
- **control**: the `Gesture` flag (one definition) and encoder bindings to `HANDLE NUDGE`.
- **calibops**: fills `IOverlayHeat` and pin residuals; consumes `OVERLAY HEAT`; re-seats pixel quads after a projector moves.
- **present (D3D11)**: its output path must call the same overlay composite (a texture and a warp mesh). It is
  compile-only there; the GL path here is the reference.

**Third party**: none new. Skia (already in Render) builds the glyph atlas from the bundled Inter (OFL, already shipped).

---

## 7. Verification here

| What | How | Real or simulated |
|---|---|---|
| Pixel-quad maths, ordering, convexity, lattice exactness, wall↔raster through warps | Core unit tests (above) | Real |
| Pixel surfaces and overlay drawing | `PlayMap.Render.Tests` on **Mesa llvmpipe under Xvfb :99**: read back `ReadOutputAsync` and compare pixels with CPU-computed positions (`WarpField.Evaluate`, projection) | Real GL 3.3, software rasteriser |
| Armed strips the overlay | Fake `IShowGate`; byte equality of read-backs | Real |
| Zero allocation per frame | `RenderHostStats.FramePathAllocatedBytes` flat over 600 headless frames with two moving cursors | Real |
| Cursor latency | Headless render host paced at 60 Hz by its timer; lane receipt → frame stamp | Real for our part; **projector lag and vsync jitter on real displays are not measured here** |
| `LIGHT WALL` | `FakeOutputBackend` with two displays; fields asserted | Simulated displays (no projector here) |
| First map end to end | Real headless engine, llvmpipe, FFmpeg 9 at `/opt/ffmpeg9` decoding a generated 2 s clip; script of verbs; read-back | Real engine; the human two-minute target is **not** measured here (usability sessions, UX §12) |
| Desk tool | Avalonia headless input tests with `FakeEngineLink` | Real desk code, fake engine |
| Windows fullscreen on a real projector, multi-monitor display keys | Not here | Lab (R0.5) |

Nothing in this package is compile-only on Linux. The D3D11 overlay composite belongs to the present package.

---

## 8. Desk and Companion surface

**First-run card** (shown when the project is the default and has no projector): "Light a wall" | "Bring my setup".
"Light a wall" lists displays by EDID name with a live thumbnail. A click sends `LIGHT WALL "<name>"` and the projector
shows its identify card and the cursor. A chip says "Click the four corners of your wall — on the wall" and the Quad
tool is armed. After the quad: "Drop a clip on it" (the media bin opens; a drop sends `MEDIA IMPORT` then `SURFACE
SOURCE`). Then "First map in 1:24 · Make this a show machine?".

**MAP keys** (ShortcutMap, `InMap`): Q quad tool · S select (was Q) · Arrows 1 px · Shift+Arrows 10 px · **Alt+Arrows
0.1 px** (Ctrl+Left/Right stay transport) · Tab / Shift+Tab next/previous handle · Enter finish or accept · Esc cancel
the draft, then deselect · G flash grid (2 s) · O overlay on/off · C wall cursor on/off · L promote to 3×3 lattice ·
Shift+L lattice off. Each key's `DeskAction` tooltip shows its line.

**ProjectorCanvas**: during placement the desk draws the clicked points and the rubber band itself, on the next UI frame
(P1). It sends `CURSOR … TRAIL …` so the wall shows the same thing. On the fourth click it sends `SURFACE QUAD`. Handles
drag with optimistic drawing and roll back with a reason chip on refusal (UX §3.5). Snapping (S3) shows a guide.

**Right-click on a quad** (the same `DeskAction`s as the inspector): Source ▸ · Promote to lattice ▸ 3×3 / 5×5 / 9×9 ·
Lattice off · Add mask · Flash grid · Rename (F2) · Lock · Duplicate · Move to projector ▸ · Remove.

**Companion** (from `VERBS` through the companion package's generator; no hand work):
- Actions: Overlay on/off/toggle, Flash grid, Handle next/prev, **Nudge handle X/Y** (Stream Deck+ dials: rotate =
  `HANDLE NUDGE ±1 0`, with acceleration; press = `HANDLE NEXT`; a shift page sends ±10 and ±0.1), `LIGHT WALL` per
  display (a preset per display row in `DISPLAYS`), Highlight surface.
- Variables: `overlay_on`, `handle` ("Quad 1 · TL"), `onboarding_seconds`, `cursors`.
- Feedbacks: overlay on → amber key; active handle's surface colour on the nudge keys.

**AI**: `SURFACE QUAD` and the nudges are Review: an assistant proposes a quad from "the poster on the left wall" (with a
camera and the vision package later); the wall diff shows the proposed outline through `HIGHLIGHT` before approval.

---

## 9. Risks and open questions

| Risk | Mitigation |
|---|---|
| Outlines on a noisy scan mesh are a hairball (thousands of crease edges) | Crease threshold 35°, 20 000-segment cap per surface, longest first. Layer toggles. A test on the simulator's scan mesh counts segments |
| Legibility on bright content | Dark halo on every line and label; a "dim content under the overlay" option (content × 0.6 while the overlay is up, rehearsal only) — **open**: on by default? |
| Wall-pixel nudges through a strongly bent lattice warp are approximate (local Jacobian) | Exact for TO; BY is first-order, re-evaluated per press; tests bound the error at 10 % corner pin |
| The cursor's 60 Hz line traffic over Wi-Fi from a phone | Ephemeral, newest wins, and the lease hides a stalled client. The phone package may move cursors to a binary WS frame later |
| Projector input lag (30–100 ms on install projectors doing internal warp) makes the on-wall cursor feel slow | Out of our hands. The desk keeps its own cursor at UI rate, and docs/PROJECTORS.md's low-latency-mode setting is offered on `LIGHT WALL` when the device link exposes it |
| A 2D quad bound to a projector does not follow a later rescan | By design (L§10.3). The calibration package re-seats quads when the projector moves; "Lift to 3D" is later |
| Key changes (Q was Select) surprise existing testers | The help overlay (F1) lists them; S and Esc both reach Select |
| **Open**: should a new quad default to the UV checker or to the test card? | UV checker: it shows orientation and keystone; MadMapper's test card shows the output's identity, which the identify card already does |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| `PixelQuadMath`, geometry, outline extraction + tests | 7 |
| Model, JSON, validation, catalogue rows, flags | 3 |
| Engine verbs, cursor board, onboarding, STATE/context | 7 |
| Render: `pixelsurface`, overlay pass (lines, text atlas, composite `Over`, post layer), caching | 12 |
| Render and engine tests on llvmpipe (incl. latency, allocation, first-map script) | 7 |
| Desk: quad tool, keys, first-run card, menus, StateSnapshot fixes + tests | 10 |
| Papers | 2 |
| **Total** | **48** |
