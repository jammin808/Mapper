# Research: lessons from the peers — first five minutes, calibration score and fallback, per-projector re-solve and history, locks and health, controller death

*13 October 2026. Topic key `lessons`. Sources read with Exa (`web_fetch_exa` on primary vendor pages, `web_search_exa` for
discovery; 11 calls). Each fact carries its URL, the page date where the page shows one, and **V** (verified: the page was
opened and the text read), **V-x** (verified from the search engine's extract of that page; the page itself was not
opened in full) or **U** (unverified). PlayMap code read at `main` c12b186. Benches run in this container are in
`r2/bench/lessons/` with their outputs.*

---

## 1. MadMapper — the first map is one quad, a test pattern and the arrow keys

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 1.1 | The official first guide: extend (not mirror) the displays; in the Projector tab pick the **Output Destination**; press **Show Test Pattern** and go fullscreen (Cmd+U; Cmd+T leaves). Use the pattern to focus, then move the projector so the pattern covers the object "protruding a little". | [My First Video Mapping guide (PDF)](https://madmapper.com/files/02-My%20First%20Video%20Mapping.pdf) | undated (MadMapper 5 era) | V |
| 1.2 | Then **add a quad** (first icon in the Surfaces tab); **a new quad shows the Test Card**. Move its four handles "from the Output view" with the mouse "to get as close as possible", "then use the keys of your keyboard (arrows up, down, left and right) to position each handle even more precisely". Assign media with one click; "Et voilà". | same | — | V |
| 1.3 | Keyboard: **Video Cursor** Alt+Ctrl+C, **Highlight Selection** Alt+Ctrl+S, **Highlight Background** Shift+Ctrl+B, **Show Test Pattern** Alt+Shift+T, **Snap To Objects** Alt+S, **Lock Surfaces Movement** Alt+B, Zoom to selected surface Alt+Z. "Tab is a default for cycling Next/Previous (Layers, **Corner Points**, Timeline Clips)". | [MadMapper 6 keyboard shortcuts](https://docs.madmapper.com/madmapper/6/2.-the-interface/keyboard-shortcuts) | undated (MadMapper 6) | V |
| 1.4 | Mesh warping guide: Alt-click an edge inserts a row or column (as cited in docs/UX.md §4.2). | [05-Mesh Warping.pdf](https://madmapper.com/files/05-Mesh%20Warping.pdf) | — | U (not re-opened this round) |

**What it teaches.** The first success is a *test pattern you focus with*, *a quad that already shows something*, *handles
you drag on the output view*, then *arrow keys*. Nothing asks for a 3D model, a camera or a scan. The video cursor and
"highlight selection" exist but are off by default and behind three-key chords.

## 2. HeavyM — draw while looking at the wall

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 2.1 | "When activated, the Cursor tool allows you to see the pointer of your mouse with a **crosshair on the output** … so that you can draw while looking directly at the projection." Toggled from the toolbar; colour, thickness and opacity in Preferences → Output → Cursor; white by default. | [The Cursor](https://help.heavym.net/hc/en-us/articles/4407417515026-The-Cursor) | 28 Sep 2021 | V |
| 2.2 | Draw tool Ctrl/Cmd+F: click for a straight point, click-drag for a mirrored-tangent Bézier point; click the first point or Enter closes; Esc closes and leaves Draw. **Shift** before clicking constrains to the previous point's X or Y. Edit tool Ctrl/Cmd+E (or double-click a face); midpoints appear on segments, click to add. Lasso selection of points across faces. Since 2.13, **numeric X/Y** for a selected point (origin top-left of the canvas). Four handle types (straight, mirrored, asymmetrical, disconnected); double-click toggles straight↔mirrored. | [Drawing and editing Faces](https://help.heavym.net/hc/en-us/articles/360018758119-Drawing-and-editing-Faces) | 11 Jul 2024 | V |
| 2.3 | Players hold photos, video, GIF, webcam, Syphon/Spout, NDI, DeckLink, shaders, text (2.12); "clipped" mode shows content only inside the group's faces. | [Adding and displaying multimedia content](https://help.heavym.net/hc/en-us/articles/360021537180-Adding-and-displaying-multimedia-content) | undated | V-x |
| 2.4 | HeavyM camera-based **automatic shape detection** | searched; no primary page found | — | **U** — do not cite as a HeavyM feature |

**What it teaches.** The crosshair on the projection *is* the editing surface; the desk becomes a touchpad. The drawing
grammar (click, click-drag, Enter, Esc, Shift constrain) is Illustrator's, so no one has to learn it.

## 3. disguise OmniCal — a score that explains itself, results on disk, a fallback, an API

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 3.1 | **OmniCal Score** (introduced r32.4): "The basis of the OmniCal Score is the **rounded up aggregate projector RMS error**, enhanced with **penalty terms** for any issues that were identified during the calibration … more useful for identifying success than standard RMS errors, which sometimes can be low despite unrealistic calibrated parameters." Same open-ended range as RMS: **0 best, ≤ 1 ideal**. | [OmniCal Calibration](https://help.disguise.one/workflows/calibration-projection/omnical-calibration) | last updated 1 Jul 2026 | V |
| 3.2 | Penalties "including, but not limited to": projector with high RMS; **invalid z-clipping**; result **mirrored or pointing backwards**; **non-square pixels or skewed axes**; **unrealistic focal length, lens shift or distortion coefficients**. An alert names penalties at the end; the report gives details and reasons "towards the end". | same | 1 Jul 2026 | V |
| 3.3 | RMS guidance: **< 1 px good**; > 1 px "usually point[s] to something going wrong"; **~1.5 px often still acceptable**. Report values are coloured **green → red**; "If everything is green, then the calibration was a success." Camera table and projector table each list RMS and whether pixels are square, plus an aggregate. | same | 1 Jul 2026 | V |
| 3.4 | Point-cloud visualisation modes: Observations (red 0 / blue 1 / green 2+ camera pairs), InlierScore (5 colours by outlier class), **ProjectorReprojError** (green good / yellow acceptable / red bad), per-point **labels** with the error in px ("values larger than 1px may indicate problems … especially if they appear in clusters"). | same | 1 Jul 2026 | V |
| 3.5 | Low RMS can hide bad distortion: "Insufficient blob coverage … may lead to unrealistic distortion values … even though the RMS error may be relatively low" → reduce the distortion model. Calibration Model Auto/Epipolar/**Homography** (flat or < 1 cm depth). Projector algorithm DLT (needs depth) / Zhang (planar) / Auto. Projector outliers RanSaC, inlier share default 0.8, sample size 7. | same | 1 Jul 2026 | V |
| 3.6 | **RigCheck**: re-calibration "without advanced knowledge", for "operators at fixed installations … after minor external changes". The Plan is the template; **each RigCheck makes a new Result** holding its own settings and data; the Plan is not modified. | [OmniCal RigCheck](https://help.disguise.one/workflows/calibration-projection/omnical-rigcheck) | undated | V-x |
| 3.7 | **Fallback Result** (r32.4): "revert the OmniCal calibration state to a previous RigCheck Result … when a RigCheck failed or produced suboptimal quality". The fallback "should typically be set to the most recent RigCheck Result with the best visual quality, as determined by an OmniCal operator after a visual inspection"; it "must be fully calibrated and aligned for all projectors, with no errors". One button: **Revert to Fallback**. | same | undated | V-x |
| 3.8 | RigCheck data lives on disk in `internal/opticalresult/<Result name>/` (settings, captured images, blob results, calibration, alignment, report). **Delete Old Results** keeps "in use" results: the current, the fallback, and an **"Additional Results to keep"** list; **"Days to keep Results"** is configurable. `acceptresult` adds the current Result to the keep list. | same | undated | V-x |
| 3.9 | "If **only projectors have moved** since the previous calibration this is **a single click**. If **cameras or projection surfaces** have moved then an operator will need to adjust the previous alignment using newly captured camera images." | [OmniCal Overview](https://help.disguise.one/workflows/calibration-projection/omnical-overview) | undated | V-x |
| 3.10 | **Blob movement detection** (RigCheck only) compares blob centres between the Plan and the RigCheck and notifies over thresholds. Causes listed: camera moved (adjust alignment per camera), surface moved (adjust per surface), projector moved ("OmniCal will automatically update all projector calibrations … **In case 2D warps or manual blend masks are used, these will likely need adjusting**"). | [OmniCal Capture](https://help.disguise.one/workflows/calibration-projection/omnical-capture) | undated | V-x |
| 3.11 | **API** (Designer r32.4+): `POST /api/session/omnical/rigcheck/executeplan`, `GET …/checkresult` → `{"result":{"status":"Green|Amber|Red","feedback":"…","omniCalScore":0.2,"cameraRmsError":0.2009,"projectorRmsError":0.1840}}` (10000 as the sentinel while running or failed), `POST …/acceptresult` ("accepts the current … Result as the new Fallback"), `POST …/revertresult`, `GET …/currentresult`, `GET …/currentfallbackresult`, `GET …/results`, `GET …/resultsforplan`, `POST …/deleteoldresults`. Advice: "compare … **only the omniCalScore, since RMS error values may fluctuate even under near identical conditions**". QuickAlign adjustments are not in the API. | [OmniCal — Designer API](https://developer.disguise.one/api/session/omnical/) | undated | V |
| 3.12 | Example feedback strings: "OmniCal successful. Capture and Calibration are good; **please check alignment visually**. OmniCal Score is excellent. Aggregate Camera error is excellent. Aggregate Projector error is excellent." / "Incomplete capture." | same | — | V |

**What it teaches.** (a) One comparable number whose basis is RMS but which **cannot be fooled by a low RMS with absurd
optics**. (b) The number's reasons are listed — but in a report window at the end, by category, not on the wall and not
with a one-click fix. (c) Results are immutable folders; the fallback is a pointer the operator moves after a visual
check; retention keeps current, fallback and an explicit keep-list. (d) Only "projectors moved" is one click; a moved
camera or surface needs an expert. (e) The vendor itself says 2D warps and manual masks may need redoing after a
projector move.

## 4. VYV Photon — per-projector re-solve, a history, live monitoring

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 4.1 | SLAC (Structured Light Automatic Calibration): calibrated projectors show an error indicator; **"should be below 0.5"**; above it, recalibrate. "It is possible to **select individual projectors and leave those that are well-calibrated untouched**" (right-click → Auto-Calibrate (Full)), adjusting light intensity and camera exposure per projector. Validation: **Display Cross** projects crosses on the reflective markers; "the centers of the crosses should land on [the] reflective points". | [Automatic Calibration and Real Time Alignment Guide](https://www.vyv.ca/en/docs/calibration/automatic-calibration-and-real-time-alignment-guide/) | undated | V-x |
| 4.2 | **Real Time Alignment** keeps calibration while projectors move, if the paired camera sees enough markers ("Copernics"); the camera entry shows a flickering "pos error". | same | undated | V-x |
| 4.3 | Release 11.5: RT Calib integrated as a step of the SLAC workflow; the RT Calib UI shows **calibration error, pose error, fps, residual and camera count**; a reset-pose button; simulation mode where the **camera prior** stays as the physical camera so calibration error is the distance between prior and solved camera. | [Release Notes](https://www.vyv.ca/en/docs/release-notes/) | 11.5, date not shown | V |
| 4.4 | "**Each calibration is now saved (up to the last 100)**, which allows comparing and investigating calibrations over time." Also: **NVZ SLAC** "imperceptible automatic alignment" with a **continuous alignment mode** while the show runs. | [Notes de version (FR page, English text)](https://www.vyv.ca/fr/docs/release-notes/) | version not shown | V-x |
| 4.5 | **Calibration Lock**: "`Controls Locked` will lock Calibration for all Displays … impossible to add new Calibration Points, manually move Displays … or attempt Auto-Calibration." Motivation: "Accidentally clicking in the Calibration Tools Window Preview could void a perfect Calibration if it isn't locked." | [Calibration Lock](https://www.vyv.ca/en/docs/calibration/calibration-lock/) | undated | V |
| 4.6 | Manual calibration draws a **green cross on the projection** for each calibration point and moves it live; a **Display Grid** (key D) is projected because it "exaggerates the blurriness generated by a bad calibration" in overlaps; stacked projectors should **share calibration points**; overlaps ≈ 15 % of a display. | [Manual 3D Calibration](https://www.vyv.ca/en/docs/calibration/manual-3d-calibration/), [Convergence and Seamless Projection](https://www.vyv.ca/en/docs/calibration/convergence-and-seamless-projection/) | undated | V-x |
| 4.7 | docs/COMPETITIVE.md also cites a VYV "reference gate" (a new result applied only within a gate). | docs/COMPETITIVE.md §3 | — | **U** (not found on the pages read) |

## 5. Scalable Display Manager — 30 seconds per projector, only the one that moved, kept backups

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 5.1 | "When a **single projector** shifts due to temperature change, lamp replacement, or physical disturbance, **a 30-second recalibration** restores sub-pixel accuracy **without affecting any other channel**." Recalibration "can be triggered manually, on a **scheduled** basis, or via **API** integration with show control systems." SDM runs on a separate calibration PC: "Recalibrate while the system stands ready." | [Scalable Display Manager](https://www.scalabledisplay.com/products/scalable-display-manager/) | undated | V-x |
| 5.2 | FAQ: recalibration "roughly 30 to 60 seconds per projector … a single button press and requires no specialist knowledge"; "The software creates an **automatic backup at the end of each calibration and retains up to ten**." | [FAQs](https://www.scalabledisplay.com/company/faqs/) | undated | V-x |
| 5.3 | Dome page: "only that projector needs recalibration. SDM keeps all other channels intact and re-measures only the affected unit"; **scheduled automatic recalibration** between shows or overnight; a **Logitech C920**-class webcam "is sufficient for most planetarium and moderate-scale dome applications"; the camera need not be levelled. | [Dome projection](https://www.scalabledisplay.com/dome-projection/) | 15 Mar 2025 | V-x |
| 5.4 | Scalable Commander CLI: `SDMSCommandLine.exe -host "192.168.6.123" -s "DefaultSystem" rg` recalibrates geometry; "Reload Mesh Files" hot-reloads warp/blend in the integrated player. | [Scalable Commander User Manual](https://knowledgebase.scalabledisplay.com/en_US/Software-User-Manuals/scalable-commander-user-manual) | undated | V-x |
| 5.5 | 7thSense's guide to SDM: changing input parameters only needs **Update Calibration**; "If any projector has been moved, a full recalibration based on new camera images will be required." | [7thSense Delta 2.8 guide](https://portal.7thsense.one/user-guides/M723-delta2_8-user-guide/aa_scalable_display_manager.html) | undated | V-x |

## 6. Pixera — locks that never stop playback

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 6.1 | 26.1 **Clip / Timeline / Layer Lock**: "can also be locked **during playback without affecting the playback functionality**, it should only lock the compositing features." Locked timelines stay **inspectable** ("all effects, keyframes, and parameters can be opened and reviewed, but not edited"). Locked timeline: no creating/deleting layers, cues, clips, no layer/clip settings; locked layer: no keyframes, no inner compositing; locked clip: not selectable or editable, **right-click** toggles, and it "still reacts to all layer and timeline adjustments". Multi-user: lock status propagates **when the first user pushes the timeline**. | [PIXERA 26.1 Overview and Changelog](https://help.pixera.one/en_US/pixera-261-overview-changelog) | 26.1 (release date not shown) | V-x |
| 6.2 | Health: the Web UI (System Information → Launch Web UI) shows how PIXERA and the RX engines run; log meanings include "display sync timed out" (render engine shuts down after three minutes if it persists), "FATAL DISPLAY SYNC DEADLOCKED" (reopens hung displays after 30 s), a watchdog after three minutes. | [PIXERA Logs and Their Meanings](https://help.pixera.one/troubleshooting/pixera-logs-and-their-meanings) | undated | V-x |
| 6.3 | A per-machine **health dashboard** in Pixera Hub | not found this round | — | **U** |

## 7. Watchout 7 — the show survives the controller

| # | Fact | Source | Date | |
|---|---|---|---|---|
| 7.1 | Services: **Producer** (authoring), **Director** (holds show state, synchronises Runners, serves NTP, hosts the **Operative** for OSC/Art-Net/HTTP), **Runner** (renders), **Asset Manager** (stores, optimises, distributes). "The Director keeps running when the Producer disconnects." | [Architectural Differences WATCHOUT 6 vs 7](https://docs.dataton.com/watchout-7-new/watchout/getting-started/architectural-differences-watchout-6-vs-7.html) | undated | V-x |
| 7.2 | Resilience: "**Runners continue playback if the Director is lost** — once a Runner has received the show data, it keeps rendering"; "**Renderers maintain output if the Runner disconnects** — … hold their last state, avoiding black screens"; the process manager restarts crashed services. | same, and [Architecture](https://docs.dataton.com/watchout-7/.architecture.html) | undated | V-x |
| 7.3 | **No offline mode**: "When Producer is connected to a Director, the system is always live … Use the **Blind Edit Warning** and timeline lock states (especially **Hands Off** mode) to protect running timelines." | [Going Online](https://docs.dataton.com/watchout-7-new/watchout/playback/going-online.html) | undated | V-x |
| 7.4 | Display servers report "download progress, playback health, GPU utilization, drive space, and network throughput"; **Startup Action** per node: No Show / **Last Show** / Specific Show, for unattended boot. A Runner failure: "the show continues on all other outputs". | [Display Servers](https://docs.dataton.com/watchout-7-new/watchout/network-setup/display-servers.html), [Running Your Show](https://docs.dataton.com/watchout-7-new/watchout/playback/running-your-show.html) | undated | V-x |

---

## 8. Where PlayMap stands (code and papers read on main)

**Papers.** docs/UX.md already commits to the right principles: P2 (operator looks at the wall → an *edit overlay pass*),
P5 (micro-gestures: Shift ×10, Ctrl ÷10, Tab), P7 (every solve and scan a named result with a score, reasons and one-click
revert; confidence map projectable), P8 (snapping with an escape), P13 (first success in minutes), P15 (object card).
The canonical time table (§5.0) sets **First light ≤ 3 min new / ≤ 60 s returning**, **Map a box, no camera ≤ 3 min
first time / ≤ 90 s regular**, and **Re-align after a bump ≤ 60 s with ≤ 10 s of patterns and no mask rework**. §5.3
storyboards the bump. ROADMAP R1.4 (explainable score: RMS + penalties for decoded %, focus, coverage, overlap < 20 %,
lens-lock violation, each with a fix; confidence map on the object), R1.5 (edit overlay: video cursor, selected-surface
outline, lattice and mask on the wall, names, flash grid; stripped when armed), R1.6 (4-click quad, arrow nudges, promote
to lattice keeping its shape), R1.2 (named immutable snapshots with a fallback; per-projector re-solve leaving masks and
lattices) and R4.8 (`CALIB PLAN/CHECK/RESOLVE/ACCEPT/REVERT`, reference gate, last 100 results) are all unbuilt.

**Code.**
- The render output path (`RenderEngine.DrawOutput` → `OutputStage.Draw`) draws content, then the 2D warp, mask,
  blend, lens lookup and trim into `st.Final`; the only thing drawn over it is the identify card. There is a `lines`
  program (1-px GL lines) used by the editor view only. **No overlay pass exists.**
- Output warps are per output (`OutputWarp`: corner pin + `WarpLattice`, normalised output units); `WarpField` already
  evaluates content→output **and inverts** output→content (`WarpField.Invert`), and `WarpMeshBuilder` tessellates.
  A surface is always a mesh (`Surface.MeshId`); **there is no 2D "quad on a projector" surface**, so the MadMapper
  path today is the output's single corner pin.
- The desk has `ProjectorCanvas` (drags corner-pin and lattice handles with `SET … warp.pin.*` and `WARP NODE`), a
  shortcut map where **Q is the Select tool** and **Ctrl+Left/Right are transport**, and `DeskAction` for one vocabulary.
- `PROJECTOR SOLVE` writes `SolveResidualPx` and words; **no snapshot, no score terms, no fallback, no history**.
  `SCAN APPLY` overwrites every unlocked solved projector. `ScanSolver` already supports a **bridged** solve (calibrated
  camera + stage mesh → only the projectors scanned are solved), and `SCAN START <camera> [<projector>…]` scans a subset.
- `ScanQuality.Assess` gives a 0–100 decode score with grade, worst 3×3 region and advice: a model for "words + fix".
- **`PlayMap.Vision/Drift/DriftWatch`** is a sound but **unwired** component (no reference outside Vision): phase
  correlation of Hann-windowed patches on a 4×3 grid of a fixed camera, a noise-aware weighting, sub-pixel parabola
  peak, median over confident regions, hysteresis (stable < 0.6 px, drifting > 1.25 px, bumped > 4 px, camera pixels)
  and 2-frame confirmation; it says "too little texture to judge" honestly. It measures **camera-image shift between two
  pictures of the same thing**, so alone it cannot tell a moved camera from a moved projector, and it needs the same
  picture in reference and live (a pattern, or static content). The FFT allocates per frame.
- Device links read **lens positions** (`LensPositions`: shift H/V, zoom, focus) where the protocol allows — a cause
  signal no camera-only competitor has.

## 9. Benches run here (numpy, seeded; `r2/bench/lessons/`)

**9.1 Drawing a 4-click quad (`quad_tess.py`).** A quad's content must follow the homography of the unit square. Drawn as
an N×N grid of plain (affine-UV) triangles, the content lands this far from where the homography puts it (projector px,
1920×1080):

| Keystone | 1×1 (two triangles) | 8×8 | 16×16 | 32×32 |
|---|---|---|---|---|
| mild (10 % top inset) | 113 | 2.4 | 0.61 | 0.15 |
| strong (25 % top inset) | 307 | 11.8 | 3.2 | 0.84 |
| picture frame, two corners pulled | 131 | 3.4 | 0.88 | 0.23 |
| side wall at a grazing angle | 180 | 8.2 | 2.3 | 0.61 |

With **homogeneous (s, t, q) texture coordinates** per vertex (q = 1/w of the homography, divided in the fragment
shader) two triangles are exact: max error **≤ 7×10⁻¹³ px** in every case. → Draw pixel quads with homogeneous UVs, not
a dense grid; a grid is only needed for a lattice's offsets.

**9.2 Re-solving only the bumped projector from a dot check (`blobcheck.py`).** Room corner (two walls and a floor), a fixed
calibrated camera (1920×1080), a projector 1920×1080 at throw 1.5, dots on a 64 px or 96 px grid, centroid noise σ
0.15–0.3 camera px, pose-only Gauss–Newton (optics locked), dot identities known:

| Grid, noise | Bump | Wall error before (p95) | After (rms / p95) |
|---|---|---|---|
| 64 px, 0.15 | small knock (0.25°, 4 mm) | 13.6 px | 0.065 / 0.10 px |
| 64 px, 0.15 | bump (0.8°, 2 cm) | 40.3 px | 0.036 / 0.05 px |
| 64 px, 0.15 | hard bump (1.5°, 5 cm) | 127 px | 0.046 / 0.08 px |
| 64 px, 0.30 | bump | 40.3 px | 0.078 / 0.12 px |
| 96 px, 0.30 | bump | 40.3 px | 0.17 / 0.27 px |

Attribution with two projectors and one camera (σ 0.2): **only P2 bumped** → P1's dots stay within 0.24 camera px of
prediction, a per-projector fit explains P2 (0.65 px) and a camera-only fit fails (6.7 px). **Camera bumped** → every
projector's dots move (~20 px), per-projector fits are poor (2–4.5 px: the geometry is wrong), a camera-only fit explains
all (0.29 px). **Camera and P1 bumped** → both single hypotheses fail; solving the camera from the projector that agrees
(P2) gives 0.29 px and 1.2 mm, then P1 re-solved through it reaches **0.29 px p95** (from 27 px).
*Limits:* the simulator has no blur, rolling shutter, projector processing or interreflection, and dot identities were
given. A real check must establish identities: a dot moving more than half the spacing aliases on a regular grid
(the "hard bump" moved 127 px). → jittered (non-periodic) dot positions plus consensus matching, with a short binary
ID code as the escalation.

---

## 10. Design implications for PlayMap

### 10.1 On-wall editing and a two-minute first map (work package **firstfive**)

1. **An edit overlay pass on every projector output, drawn after the output stage**, in two layers:
   *pre-warp* geometry (surface outlines, quad and lattice handles, pins, flash grid, confidence heat) rendered at the
   output raster and composited **through the same warp mesh and lens lookup** (so lines land where content lands);
   *post-warp* items (the cursor crosshairs, output corner-pin/lattice handles, names as upright labels) drawn in output
   pixels. Never through the blend map or masks (an outline must stay visible in a blend's dark edge). Stripped while
   armed, in blind edit, and on outputs not named. Beats HeavyM (cursor only) and MadMapper (cursor and highlight behind
   chords): one switch shows cursor, outlines, names, handles and the active handle, per client colour.
2. **A 2D "pixel quad" surface kind** bound to a projector's raster: four corners as a homography plus optional lattice
   offsets *on top of* the homography, so "promote to lattice" keeps the shape **exactly** (zero offsets = the quad)
   — the bench shows a plain grid would not. Drawn with homogeneous UVs (exact). Masks stay in UV and work unchanged.
3. **The operator speaks in wall pixels.** Clicks and nudges arrive in output (post-warp) coordinates; the engine
   inverts the output warp (`WarpField.Invert` exists). Arrow 1 px, Shift 10 px, Alt 0.1 px (Ctrl+Arrow is transport on
   this desk), Tab cycles corners (MadMapper's own convention), Enter accepts, Esc cancels.
4. **A first quad shows something at once** (MadMapper: the Test Card) — PlayMap's `pattern.uvchecker` — so the
   operator sees keystone and orientation while nudging.
5. **One composite verb for the first light** (`LIGHT WALL <display>`): output fullscreen on that display, a projector
   with nominal optics (or the lens readback), Projector mode, overlay and cursor on, identify card for 3 s — one undo
   step, the same line for the desk, Companion and an AI. Then `SURFACE QUAD`, nudges, `SURFACE SOURCE`. The engine
   stamps the onboarding milestones in STATE so "first map in 1:24" is measured, not claimed.
6. **Cursor latency is a budget**: CURSOR lines are ephemeral (not journalled, not undoable), coalesced to the newest
   per session, and the render thread reads them from the air snapshot at the next frame: ≤ 2 vblanks plus projector lag
   (UX §6), measured in a test.

### 10.2 An explainable calibration score and saved fallbacks (work package **calibops**)

1. **Score = inlier RMS rounded up to 0.1 px + penalties in pixel-equivalents**, 0 best, ≤ 1.0 grade A, ≤ 1.5 B, ≤ 4 C,
   else F — the OmniCal scale, so a disguise user reads it at once, and the companion package's grade rule. Penalties
   cover OmniCal's list (mirrored/backwards, z-clipping, non-square pixels, unrealistic focal/shift/distortion) **plus**
   what PlayMap can measure that OmniCal does not show: outlier pins named, point spread, depth spread (zoom↔distance),
   covariance-predicted wall error, lens moved since the solve (device readback), mesh changed since the solve, decode
   quality and focus, **seam misregistration with each overlapping neighbour**. **Every term carries words, a place
   ("pins 4, 7", "top-left quarter", "seam P1|P2") and a fix that is a verb line** the desk, Companion or the assistant
   can run — disguise lists reasons in a report; PlayMap puts them on the wall with a button.
2. **A confidence map per projector** (predicted error per 32 px cell: measured residuals where there are pairs,
   covariance propagation elsewhere, "unmeasured" hatched), shown in the desk and **projected onto the object** through
   the overlay with numbered problem areas that match the score's terms.
3. **Immutable named snapshots** (`CalibrationSnapshot`, id `cs…`) for every solve, scan apply, re-solve, revert and
   import: optics, pose, locks, pins, score, provenance (scan, parent, who, lens readback, mesh hash). The projector
   points at its current snapshot and a **fallback** snapshot. `ACCEPT` moves the fallback (disguise `acceptresult`),
   `FALLBACK` reverts (`revertresult`), `RECALL` applies any snapshot; all are undoable edits. Retention like disguise's:
   current, fallback, pinned, plus the last N per projector (default 20; VYV keeps 100, Scalable 10).
4. **Compare scores, not RMS** (disguise's own advice): the gate for applying a re-solve automatically is score-based
   (≤ max(1.5, 1.25 × the fallback's score)); a worse result is kept as a snapshot but staged, never applied silently.

### 10.3 History and re-solving only the projector that moved (work package **calibops**)

1. **A check that takes about a second per projector**: a jittered dot pattern and a black frame through the scan
   runner and the fixed rig camera, dots matched to their prediction (pose × mesh × camera) by consensus; a coded
   escalation when the move exceeds the capture range. Bench: pose-only re-solve from ~500 dots reaches **≤ 0.12 px p95**
   on the wall (simulation) from bumps of 14–127 px.
2. **Attribute the move before fixing it**: nothing moved / projector k moved / camera moved / camera and some
   projectors moved / the surface moved — by comparing a camera-only fit with per-projector fits (bench: the hypotheses
   separate by an order of magnitude). PlayMap then **re-solves the camera from the projectors that agree** — the case
   disguise sends to an expert — and only then the moved projectors. A surface move stays a human decision (rescan).
3. **Keep masks, lattices and quads.** Masks live in surface UV, so a pose re-solve leaves them; 3D lattices never move.
   For **pixel quads and output warps** (projector-raster objects that a pose cannot carry) the check's displacement
   field re-seats them: each corner moves to the projector pixel that now lights the wall spot it used to light (camera
   fixed: new map⁻¹ ∘ reference map). disguise says these "will likely need adjusting"; PlayMap moves them for you and
   shows the residual.
4. **History per projector** with what moved (Δmm, Δdeg, Δwall px between consecutive snapshots), the score trend and
   the cause (check, lens readback, operator); 100 entries reachable, 20 kept by default.
5. **Triggers**: an explicit or Companion-pressed CHECK; a lens-position change read from the projector (cause known
   without a camera); DriftWatch on the rig camera's unlit regions for "the camera moved"; a scheduler (when that package
   lands) for between-show checks. **Never automatic correction while armed**: detect and propose; a person approves
   (ROADMAP §6 patent note).

### 10.4 Locks, health and controller death (other packages; recorded here)

- Locks block **edits**, never playback or inspection (Pixera 26.1, VYV Calibration Lock); a locked projector's
  calibration refuses RESOLVE/RECALL with the reason; the lock is a model field so it reaches every client at once
  (Pixera propagates only on push).
- The node, not the desk, holds the show (Watchout: Runners keep playing without the Director; Producer is only UI).
  PlayMap's engine/desk split already has this shape; the fleet package must keep followers playing their last anchors
  when the leader is lost and boot to the last show.
