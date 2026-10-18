# Research: UX for the three workflows, desk UI patterns, colour, and real-time architecture

Researched 2026-10-18 for the round-3 plan revision. Confidence marks: **H** (read in the vendor's or standard's own
document), **M** (read in a secondary source, or an inference from several sources), **L** (unverified or extrapolated).
Facts about PlayMap's desk come from `docs/DESK.md`, `docs/UX.md` and the note `desk-today.md` beside this one.

---

## 0. Recommendations in one page

1. **Ship three guided workflows ("guides") as staged disclosure over the existing verbs**, each opened from a Guides menu,
   the empty-view right-click and the palette, each step a docked panel with Back/Next and "Open in Pro":
   - **Flat blend / double stack** (corporate AV): canvas → outputs → overlap → spanning test card → corners → blend →
     gamma and black level → colour match → lock. The steps match Millumin, Watchout, Resolume, MadMapper, Epson, Panasonic
     and Scalable (H).
   - **2D onto a 3D wall, seen from the audience**: two paths. (a) **Camera at the audience seat**: scan, and map content in
     the camera's view, the way VIOSO calibrates (H). (b) **Eyepoint**: place a viewer point with aim and field of view, auto-fit
     to the selected surfaces, the way disguise's Perspective and Mesh mappings and Millumin's Structure work (H). A flat wall
     needs neither: the four-click quad already gives an undistorted image (M, geometry).
   - **Map an object with 1–2 projectors**: rough projector placement → 6+ pins with a coverage meter → live residual →
     accept → second projector reuses the same 3D points → automatic blend on the mesh. Every tool has this shape: disguise
     QuickCal, Watchout 7, Pixera Marker, MadMapper (H).
2. **Make the quad a persistent, fully editable object.** Keep a half-drawn quad as a per-projector draft until Enter or
   Discard. Return to Select after Enter. Remember the tool and corner per workspace (Blender keeps the active tool per
   workspace and mode, H). Add typed corner fields, corner drag, a corner picker and an on-wall crosshair. Keep 1 / Shift 10 /
   Alt 0.1 px and make the big step configurable (Figma, H).
3. **Show the media on every screen, both ways round**: each surface, quad and output row carries the media's thumbnail and
   name ("via playlist X"), and each media row carries a count badge ("on 2 surfaces"), as MadMapper does (H). Pro adds a
   routing matrix.
4. **One canvas across outputs**: a pattern or generator placed on a canvas spans it. A spanning test card shows a projector
   label and resolution per output, the overlap and blend lines, and circles and diagonals across seams. Drawing the blend
   start and width on the projected image follows Panasonic Geometry Manager Pro (H).
5. **Panels: Dock.Avalonia 12.1.0.12** (MIT, published 2026-10-17, needs Avalonia ≥ 12.1.4, which PlayMap uses) (H).
   - Use **managed windows** so a floating panel stays inside the desk window and can never land on an output monitor, which
     is the air.
   - Pin/auto-hide gives collapse. Persist the layout per workspace in `desk.json`, with Reset layout.
   - Actipro Docking (commercial, Avalonia 12.1.2+) is the fallback (H).
6. **Colour: three channels with separate jobs.**
   - **Status** colours are reserved and saturated: red air/armed, amber attention, cyan information. This follows
     IEC 60601-1-8 and ISA-101 (H).
   - **Identity** colours are muted and drawn from the Okabe-Ito palette, one per projector or output (Watchout's colour tag,
     disguise's lineup tint, H). They never reuse a status hue. Today's `#FFB74D` projector orange collides with amber.
   - **Stage** colours are a very subtle tint per workspace or guide step (Unity's Play-mode tint, Resolve's pages, H/M).
   - Never colour alone: every light also carries a shape and a word. Flash only unacknowledged high-priority states, and never
     flash text (H).
7. **Latency architecture.**
   - Wait on the DXGI waitable object, then sample show time, render and present, with a maximum frame latency of 1 (2 only
     when the measured CPU plus GPU time exceeds the budget) (H).
   - Publish input-to-photon p50/p95 per lane, as PresentMon and Reflex define it (H).
   - No allocation on the frame path (Holzmann rule 3; the .NET SustainedLowLatency mode) (H).
   - Add a safety-architecture watchdog: detect → notify → mitigate → record, on separate resources, with a health-checked
     feed, never a blind one (AAMI) (H).
   - Keep files plus journal rather than a database on the hot path. If an index is ever needed, use SQLite WAL on its own
     lane (H).
8. **Capture pass-through on Windows 11 Home.**
   - GPUDirect for Video, Mosaic and Warp & Blend are professional-GPU features (H), so PlayMap must not depend on them.
   - Use Magewell low-latency mode with partial notification (64 lines; 1080p60 total 32 → 17.7 ms in Magewell's test) (H).
   - Upload by slice through D3D12 GPU upload heaps where ReBAR is on (Agility SDK 1.613+, Windows 11 build 26080+), otherwise
     through the copy queue. Convert YUV in a shader, keep at most one frame in flight, and publish the glass-to-glass figure
     (H/M).

---

## 1. How the tools structure the three workflows

### 1.1 Two-projector edge blend or double stack on a flat screen

| Tool | How the steps go | What a first-timer sees | Simple vs advanced |
|---|---|---|---|
| **Millumin** | Neutral keystone → place projectors with ~5–10 % overlap → Output panel: layout `2 x 1`, one projector's resolution (canvas = 1600×600 from two 800×600) → soft-edge = overlap pixels (40) → *Display test card* → adjust each output's corners until circles and lines match → colour: dim the brighter projector → smoothness. Matrix 2×2 the same, "choose one projector and adjust the others to it" ([Millumin soft-edge](https://help.millumin.com/docs/display/softedge/)) **H** | One Output popup with a layout grid and an overlap number; the canvas is then "a normal screen" | Per-output warping grid arrived in v5 beta ([forum](https://forum.millumin.com/discussion/2249/multiple-projector-blending)); canvas-level mapping and masks are the advanced layer ([mapping](https://help.millumin.com/docs/display/mapping/)) **H** |
| **Watchout 7** | Add displays on the Stage (right-click at the clicked spot, or from a node). Overlapping displays on the same tier get automatic soft edges (Device Properties ▸ Mask ▸ Automatic Softedges), with one gamma slider (0.5–1.5, default 1.0). Put displays on different tiers to stop blending. Test pattern cycle: White → Masked/Pattern → None ([edge blending](https://docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/edge-blending.html), [adding displays](https://docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/adding-displays.html)) **H** | One Stage canvas with the displays as coloured rectangles (each has a colour tag and a Lock). New physical displays start disabled until routing is confirmed | Mask-based shaping (junction points, feathered masks) when the automatic blend is not enough. Automatic blends are not available for 3D projectors or canvas displays **H** |
| **Resolume Arena** | Advanced Output: slices on the **Input Selection** stage overlap as the projectors do (≥15 % recommended), using the test card's diagonals. On the **Output Transformation** stage, warp each output by its four big corners. Enable edge blending per slice ("blends the middle automatically"). Fine-tune Gamma R/G/B, Power, Luminance. Black-level compensation on the output tab ([edge blending](https://resolume.com/article/54), [input selection](https://www.resolume.com/support/en/input-selection)) **H** | Two stages, input and output, with one screen and one slice ready-made | Slice masks, routing layers/screens to slices, presets per venue ([advanced output](https://www.resolume.com/support/en/advanced-output), [slice routing](https://resolume.com/support/en/slice-routing)) **H** |
| **MadMapper 6** | Projectors tab: enable outputs and show their test patterns → put the calibration card on a quad, duplicate it for projector 2 → overlap 10–20 % ("about four grid boxes") → align corners in the output view → select **both** quads ▸ Soft Edge ▸ **Auto Setup** → tune width and power per side. Stacking = the same mapping on both projectors at 100 % overlap ([soft-edge](https://docs.madmapper.com/madmapper/6/6.-outputs/soft-edge-blending)) **H** | A surfaces list, an input view, an output view, and a test card on every new quad ([first mapping guide](https://madmapper.com/files/02-My%20First%20Video%20Mapping.pdf)) **H** | Blends with feathered internal masks for non-perpendicular edges. The blend lives on the surface, not the projector ([soft-edge PDF](https://download.madmapper.com/softedgeressource/MadMapper-Soft-Edge.pdf), gamma default 1.8) **H** |
| **Epson Projector Professional Tool** | Group the projectors → Blending and Stacking Settings → set each projector's position → **Geometry Assist – Stacking & Blending** wizard (camera) → then Screen Matching (tint and brightness, matched to the darkest projector) and Point Correction. For stacking, layer 1 is the reference and later layers are set slightly larger. Recommended patterns: Gray Bars V/H and White ([Epson guide](https://files.support.epson.com/docid/cpd6/cpd64101/source/adjustments/tasks/multi_projector_tasks/multi_projector_blending_stacking_eppt.html), [operation guide PDF](https://download3.ebz.epson.net/dsc/f/03/00/17/68/43/5919400321dbd21409508bf898987ab70f88c035/ProjectorProfessionalTool_162_EN.pdf)) **H** | A wizard over a layout grid | Manual edge blending per edge (start, range, blend curve). Keyboard step sizes: 0–7 picks the step, Ctrl+arrow moves, Ctrl+Shift moves 0.5, Alt moves 5 **H** |
| **Panasonic Geometry Manager Pro** | Content area ▸ projection layout ▸ register devices → Blending tab: enable, choose gamma or a curve, tick the edges, set **Start** and **Width**; *Projecting markers* draws the start line and width on the projected image. Auto Screen Adjustment with a camera does geometry, blend, colour and black level ([VSS manual](https://eu-uat.connect.panasonic.com/sites/default/files/media/document/2025-09/VSS_Inst-and-Ad_OI_DPQP1600WA_E.pdf), [feature guide](https://docs.connect.panasonic.com/projector/feature_guide/index.html)) **H** | List view, main view, an Adjust menu | Camera kit and finer grids are paid upgrades **H** |
| **Scalable Display Manager** | Clients → Projectors (order, layout) → Camera → **Data Collection** (white, 5-dot and grid per projector, about 60 s each) → Screen type → Image boundary (≥6 control points per camera) → optional perspective, blend and grid tweaks → Finish ([manual](https://scalabledisplay.helpjuice.com/en_US/Software-User-Manuals/scalable-display-manager-user-manual), [tutorial](https://www.youtube.com/watch?v=OAJLBI6aktU), [TouchDesigner how-to](https://derivative.ca/UserGuide/How-to_calibrate_your_projector_with_Scalable_Displays)) **H** | A left-to-right panel sequence | Per-channel recalibration in about 30 s, scheduled recalibration ([product](https://www.scalabledisplay.com/products/scalable-display-manager/)) **M** (vendor claims) |

**The common shape (H, synthesis):** define one canvas → identify outputs → state the overlap (10–20 % default) → spanning
test card → per-projector corner alignment (perspective first, a lattice only if needed) → automatic blend from the overlap
geometry → blend gamma or curve → black-level compensation → colour and brightness match to the dimmest → lock. A stack is the
same with 100 % overlap and one projector as the reference.

### 1.2 A 2D video onto a 3D surface so it reads undistorted from a viewing position

| Tool | Mechanism | Workflow |
|---|---|---|
| **disguise** | **Perspective mapping**: content emitted from an *eyepoint* with rotation and field of view. 2D content looks right only from that point. Every camera gets a perspective mapping by default. **Mesh mapping** auto-fits the frustum to the mesh's extent ([perspective](https://help.disguise.one/designer/mapping/mapping-types/perspective-mapping), [mesh](https://help.disguise.one/designer/mapping/mapping-types/mesh-mapping), [overview](https://help.disguise.one/designer/mapping/mapping-types-overview)) **H**. A content template can be made by camera-projecting UVs from the audience position ([example](https://help.disguise.one/workflows/3d-modelling/projection-examples/perspective-mapping)) **H** | Layer ▸ Mapping ▸ new Perspective mapping ▸ assign screens ▸ set eyepoint, rotation, FOV ▸ drop content. Projectors are calibrated separately (QuickCal or OmniCal), so more projectors blend on the same screens **H** |
| **Millumin** | **Structure** (anamorphosis): import OBJ or DAE, map the mesh corners onto the real structure in output mode ("auto-place points" after 6), then adjust the camera's point of view to the audience's ([structure](https://help.millumin.com/docs/display/structure/)) **H** | Four steps: mesh → structure and camera near the final view → map 6 points → content and fine POV **H** |
| **Pixera** | Each screen has a *perspective* (virtual camera) in inner compositing. Layer Mapping effects add Planar, **Perspective** and Equirectangular projection, with an in-workspace handle (Show UI in Workspace). Eyepoint controls exist for dynamic screens ([layer mapping effects](https://help.pixera.one/layer-mapping-effects?kb_language=en_US), [dynamic screen](https://help.pixera.one/en_US/virtual-production-/dynamic-screen-workflow)) **H** | Screens tab (geometry) → Mapping tab (projectors) → Compositing (perspective or effect) **H** |
| **VIOSO** | After a camera calibration, **the camera is the default content space**: content is laid out as the camera sees it. Converting to a 3D model's UV content space is a separate, irreversible step. An eyepoint node offsets views for static-eyepoint exports ([free-mode 3D](https://docs.vioso.com/guide/workflows/mrd-freemode), [designer](https://docs.vioso.com/start/project-mode/designer), [dynamic eyepoint](https://www.helpdesk.vioso.com/documentation/vrsim-dynamic-eye-point-correction/)) **H** | Camera at the eyepoint → scan → done (camera space). Optional MRD 3D alignment and UV conversion. Warp and blend export to engines (`.vwf`, MPCDI) **H** |
| **Scalable** | Perspective panel → "generate perspective outputs": per-projector frusta for a user-defined eyepoint ([tutorial](https://www.youtube.com/watch?v=OAJLBI6aktU)) **H** | After calibration **H** |

**What this means (M, synthesis plus geometry).**
- On a **flat** wall, a projector-to-wall homography (the four-click quad) already makes the image metrically correct on the
  wall, so it reads as a flat poster from every seat. An eyepoint adds nothing there.
- The eyepoint matters for **non-planar** walls (bays, corners, cornices) and for deliberate illusions.
- The simplest robust path is VIOSO's: **put the camera where the audience is**, scan, and map content in the camera's view.
  PlayMap's scan already holds projector↔camera correspondences, and "the smart scan" is the inverse view, so this is a content
  mode rather than new calibration (M).
- The eyepoint path (disguise and Millumin) suits a mesh or scan when the camera cannot stand at the seat (L/M).
- Both blend across more projectors, because content is defined once in one space (camera or eyepoint) and every projector
  samples it. Overlap blending then happens in that shared space.

### 1.3 Simple 3D mapping with one or two projectors (point pins, mesh import)

| Tool | Points | Feedback | Notable UX |
|---|---|---|---|
| **disguise QuickCal** | Create ≥6 reference points on the mesh (6–12 usual, up to 30 on buildings). Line up 10–15 per projector. Calibration method Auto. Line up extremes first: image corners, near and far ([overview](https://help.disguise.one/workflows/calibration-projection/quickcal-overview), [points](https://help.disguise.one/workflows/calibration-projection/quickcal-ref-points)) **H** | Reprojection and RMS error over the API ([API](https://developer.disguise.one/api/session/quickcal/)) **H** | Right-click a projector ▸ config ▸ QuickCal editor. **Arrow keys move 1 px**. The output is **tinted in the projector's colour**. Output modes: Content, Wireframe, Identify (grid with the projector name), Grid, None. Points are shared across projectors ([projectors](https://help.disguise.one/workflows/calibration-projection/quickcal-projectors)) **H** |
| **Watchout 7** | **6 virtual points minimum**, "more points does not necessarily give a better result"; vertex snapping ([display calibration](https://docs.dataton.com/guide/watchout/devices/display-calibration.html), [3D mapping](https://docs.dataton.com/watchout-7/3d/mapping.html)) **H** | An **Accuracy %** | Manual calibration first, **Continuous only for fine-tuning** (it causes jumps otherwise). Select points with W/A/S/D, move them with the arrows, **or with an Xbox gamepad** (left stick selects, right stick moves). An eye button hides the UI. The four-step framing is Position real → Prepare virtual → Calibrate → Play media **H** |
| **Pixera Marker Calibration** | **≥5 markers**, as far out as possible, ideally in all corners ([marker calibration](https://help.pixera.one/mapping-/marker-calibration-workflow)) **H** | **Re-projection error** (average). The solver defaults to Levenberg–Marquardt | Needs a connected output. Markers move by mouse or **arrow keys**. A calibration toggle updates live. The projector's lens and throw must be right first **H** |
| **MadMapper** | 6 non-coplanar vertices ([forum, developer](https://forum.garagecube.com/viewtopic.php?t=9297)) **H** | **Real-time RMS error**, then *Fix Position* and *Reset Calibration* ([docs](https://docs.madmapper.com/madmapper/6/4.-surfaces/advanced-3d-and-scanning)) **H** | Users asked for a simpler "four corners of the bounding box" path ([same forum](https://forum.garagecube.com/viewtopic.php?t=9297)) **H** |

**The common shape (H, synthesis):** rough projector pose and lens first → points on the model with vertex snapping → drag each
point's output twin onto the real feature → a live error or accuracy number → solve manually, then live only for fine-tuning →
points spread in depth and to the image corners → the same 3D points reused by the second projector → wireframe, grid and
identify outputs while lining up → the projector's output tinted in its identity colour.

### 1.4 How each tool shows which media goes to which screen

- **MadMapper**: a usage-count badge on each media item ("«1» … applied once"). Surfaces display their assigned media
  ([guide](https://madmapper.com/files/02-My%20First%20Video%20Mapping.pdf)) **H**.
- **Watchout**: cues are drawn on the Stage under the displays they fall on, so placement is the assignment **H**.
- **Resolume**: each slice names its Input Source (composition, layer, screen or preview), and right-click offers Match Output
  Shape ([routing](https://resolume.com/support/en/slice-routing)) **H**.
- **disguise**: a layer's Mapping lists its screens. Default mappings are named after their screens ([perspective](https://help.disguise.one/designer/mapping/mapping-types/perspective-mapping)) **H**.

PlayMap's outliner already reads "quad on P1 · <media>". No media or playlist row says where it plays (`desk-today.md` §b).

### 1.5 One pattern, media or generator spanning outputs as one canvas

- **Watchout**: the stage is one canvas, and displays are windows on it **H**.
- **Resolume**: the composition is the canvas and slices cut it. The test card spans all slices ([forum](https://resolume.com/forum/viewtopic.php?t=11618)) **M**.
- **Millumin**: a 2×1 layout makes the canvas **H**.
- **disguise**: the TestPattern layer fills each surface it is mapped to. Its centre label shows the screen or projector name and
  resolution ([test pattern](https://help.disguise.one/designer/layers/layer-types/generative/testpattern)) **H**.
- **Panasonic**: projected markers draw the blend start and width ([VSS manual](https://eu-uat.connect.panasonic.com/sites/default/files/media/document/2025-09/VSS_Inst-and-Ad_OI_DPQP1600WA_E.pdf)) **H**.
- **NVIDIA Mosaic**: spans outputs at OS level, but only on RTX PRO, RTX and Quadro professional GPUs, and Warp & Blend likewise
  ([Mosaic](https://www.nvidia.com/en-us/technologies/nvidia-mosaic/), [Warp & Blend](https://developer.nvidia.com/warp-and-blend), [user guide](https://images.nvidia.com/aem-dam/en-zz/Solutions/design-visualization/quadro-product-literature/NVMosaic-UG.pdf)) **H**.
  A forum report says GeForce lost Warp & Blend in drivers 570/580 ([forum](https://forums.developer.nvidia.com/t/rtx-warp-and-blend-api/249199/5)) **L**.
  On Windows 11 Home and consumer GPUs, spanning and blending must be PlayMap's own, which it already is.

PlayMap today: `PATTERN … ON ALL` draws N separate patterns at each output's raster. Only a pattern generator placed as a canvas
layer spans (`desk-today.md` §e).

**Recommendation:** add a spanning form (for example `PATTERN <kind> SPAN <canvas>`, or a pattern verb whose target is a canvas)
that draws one grid across the canvas. It overlays each output's label and resolution, the overlap rectangle and the blend
start and width lines, and adds circles and diagonals across seams (Resolume's diagonals, Millumin's circles). The same canvas
then takes media or a generator, which already spans.

### 1.6 The three PlayMap guides (proposal)

Every step sends catalogue lines only (the desk has no private API). Every step's state comes from STATE. Each step can be
re-entered later from the object's right-click menu.

**A. Blend or stack on a flat screen** (target under 10 min for two projectors; estimate)
1. *What are you building?* Side by side (1×2, 2×1, n×m) · Stack (100 %) · Stack + blend. The answer creates one canvas.
2. *Outputs*: pick displays, each identified on the wall in its identity colour and number (Identify).
3. *Overlap*: a number in % or px (default 15 %, hint 10–20 %), or "measure with the camera".
4. *Spanning test card* on the canvas (§1.5).
5. *Corners*: per projector, the four-corner quad on its slice. Tab steps corners; 1 / Shift 10 / Alt 0.1 px. Optionally promote
   to a 3×3 lattice. For a stack, projector 1 is the reference and projector 2 aligns to it, with an A/B flash toggle.
6. *Blend*: automatic from the overlap geometry, then blend gamma or curve, and black-level compensation.
7. *Match*: brightness and colour per output, dimmest as reference (Epson's rule).
8. *Lock* the outputs (`LOCK`), then the content step.

**B. Video onto a building, seen from the audience**
1. *Where is the audience?* "I can put a camera there" → camera path. "I have a model or scan" → eyepoint path. "It is a flat
   wall" → quad path (guide A or the first-light quad).
2. Camera path: scan from the seat, then the content mode *As seen from the camera*, then add projectors (each scanned) and
   blend in camera space.
3. Eyepoint path: place the viewer point in the 3D view (default 1.7 m above the ground plane; estimate), auto-fit the frustum
   to the selected surfaces (disguise Mesh mapping), preview "look through the viewer", then drop the clip.

**C. Map an object with one or two projectors**
1. Import a mesh, or use the scan.
2. Rough projector placement: lens, throw and a drag in 3D. Priors exist (`int-facade`).
3. Pins: a coverage meter (four image quadrants plus depth spread) and suggested vertices. Live residual per pin, coloured by
   quality (PlayMap already uses good < 1 px, fair < 3 px). Solve manual, then live.
4. Accept (the score card).
5. Second projector: the same 3D points offered first.
6. Blend on the mesh, then content.

---

## 2. The four-click quad: persistent and editable (owner's ask)

Current desk facts (`desk-today.md` §a):
- A drawn quad is a project `Surface` and persists, and it is listed in the outliner.
- **A half-drawn quad is dropped** when the tool changes or another projector is shown.
- The tool stays Q after Enter, so the next click starts a new quad.
- The corner resets to TL on reselect, and the wall is not told.
- The view does not follow an outliner pick.
- There are no typed corner fields and no corner drag.
- Pins move only by mouse.

Precedents:
- Blender: "only one active tool per Workspace and mode. This tool is remembered" ([tool system](https://docs.blender.org/manual/en/latest/interface/tool_system.html)) **H**. Blender 2.80 tools "stay active until changing to another tool", with **Adjust Last Operation** separate from the next tool's settings ([2.80 UI notes](https://developer.blender.org/docs/release_notes/2.80/ui/)) **H**.
- Watchout: per-display Lock and colour tag **H**.
- disguise QuickCal keeps line-ups per projector configuration **H**.

Recommendations (**M**):
- **Draft per projector.** Clicks of an unfinished quad persist on the desk, per projector, until Enter or an explicit Discard
  (Esc discards only while the view has focus and shows the draft). Leaving MAP, opening a menu or switching projectors
  keeps it, and the ghost redraws on return. The draft is desk-local, so the engine's one-writer rule is untouched.
- **After Enter, return to Select** with the new quad selected (one-shot by default, like most editors). A *Keep quad tool*
  toggle supports repeated drawing.
- **Remember the selected surface and corner** per workspace. On reselect, restore the last corner and send
  `HANDLE SELECT <sf> <c>` so the wall's enlarged handle matches.
- **Outliner pick switches the view** to the quad's projector, so the arrows never move an unseen quad.
- **Corners editable four ways:**
  - drag on the projector view and on the wall cursor;
  - typed x/y per corner in the inspector, accepting arithmetic as Resolume does ("/3");
  - a TL/TR/BR/BL picker;
  - nudge keys, with the step shown in the status bar ("1 px · wall pixels").
- The same applies to **pins** (typed pixel, arrow nudge on the selected pin) and to **output warp corners and lattice nodes**
  (today drag-only).
- **Always reachable.** The quad tool appears in the MAP menu, the view's right-click (already there), the palette, Q, and the
  re-openable first-light guide (Help ▸ Guides), so a user who clicked away has a door back.
- Gamepad selection and nudge (Watchout's Xbox mapping) is a later, optional add **L**.

---

## 3. Desk UI patterns

### 3.1 Docking, draggable and collapsible panels (Avalonia 12)

**Dock (wieslawsoltes/Dock), "Dock for Avalonia"**
- MIT ([repo](https://github.com/wieslawsoltes/Dock)) **H**.
- `Dock.Avalonia` **12.1.0.12, 2026-10-17**, depends on **Avalonia ≥ 12.1.4**, targets net8.0 and net10.0, about 744k total
  downloads ([NuGet](https://www.nuget.org/packages/Dock.Avalonia/)) **H**. PlayMap's desk pins Avalonia 12.1.4.
- Avalonia-11 users must use the separate `.v11` packages, never mixed with the unsuffixed Avalonia-12 line **H**.
- About 1.5k stars and 4.7k commits, essentially one maintainer, so the bus factor is low (**M**). GitHub Releases is empty and
  packages publish frequently, several per week in October 2026, so pin an exact version.

Dock features ([docs](https://wieslawsoltes.github.io/Dock/articles/README.html)) **H**:
- document and tool panes;
- tools that **auto-hide, pin or float**;
- split layouts with proportional sizing and drag handles;
- floating windows with docking indicators;
- **managed floating windows hosted inside the main window**;
- layout persistence: JSON, XML, YAML, Protobuf, and a **System.Text.Json source-generated** serializer
  ([FAQ](https://wieslawsoltes.github.io/Dock/articles/dock-faq.html));
- control recycling and deferred content;
- docking groups, `CanDrag`/`CanDrop`/`CanFloat` per dockable, fixed sizes via Min=Max.

Managed windows "cannot be moved outside the main window or across monitors"
([managed windows](https://wieslawsoltes.github.io/Dock/articles/dock-managed-windows-guide.html)) **H**. For PlayMap that is a
feature: **a desk panel can never be dragged onto a projector output** and cover the air, or break independent flip on that
output (**M**, inference).

Alternatives:
- **Actipro Docking/MDI**: commercial Pro licence, Visual Studio-like, auto-hide, dock guides, a Ctrl+Tab switcher, layout
  persistence. v26.2.0 supports Avalonia 12.1.2+ (2026-09-24) ([product](https://www.actiprosoftware.com/products/controls/avalonia), [support matrix](https://www.actiprosoftware.com/docs/controls/avalonia/supported-technologies), [v26.2 blog](https://www.actiprosoftware.com/blog/post/26773/avalonia-controls-v2620-released)) **H**.
- The Avalonia team declined an official docking control and pointed to Actipro, Dock, NP.Ava.UniDock, ReDocking and
  Avalonia.UpDock. A user reported "uni dock doesn't work" and that UpDock has no NuGet package
  ([issue #19054](https://github.com/AvaloniaUI/Avalonia/issues/19054)) **H** (statements in the thread).

**Recommendation (M):**
- Use Dock.Avalonia with `DockSettings.UseManagedWindows = true` by default. Allow native float as an opt-in only to monitors
  that no output uses.
- Tool panes (outliner, inspector, media bin, cards, drawers) become dockables that pin to collapse.
- The layout is a desk preference in `desk.json`, per workspace and per guide, with *Reset layout*.
- The show strip, PANIC and the attention bar are **not dockable** (fixed, always visible).
- Wrap Dock behind a desk seam so Actipro can replace it.
- The serializer must be the source-generated one (trimming/AOT safety).

### 3.2 Right-click menus, radial menus and the palette

**Microsoft's context-menu rules ([Win32 guide](https://learn.microsoft.com/en-us/windows/win32/uxguide/cmd-menus)) H:**
- only commands for the clicked object;
- **never make a command available only in a context menu**;
- groups of ≤7, ≤15 items, few submenus;
- order: primary, secondary, transfer, settings, object commands (Delete, Rename, Properties);
- the default command bold and first;
- **remove rather than disable** inapplicable items, except members of sets (Cut/Copy/Paste).

The Windows app guidance adds: put *all* contextual commands in the item's menu, with Shift+F10 and the Menu key as the
keyboard context action ([contextual commanding](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/collection-commanding)) **H**.

Reconcile with PlayMap's doctrine (**M**):
- Hide entries that never apply to this kind of object.
- Show-but-disable, with the node's reason, entries that apply but are refused now (armed, locked, not paired). The desk
  already does this; it is the "related set" exception, and it tells the truth about attempts.
- Add menus where there are none: empty 3D space and empty plan space (`desk-today.md` §f).
- Make every menu entry also reachable from the menu bar or palette.

**Radial (pie) and marking menus:**
- Pie menus measured about **15 % faster with fewer errors** than linear menus in an 8-item study
  ([Callahan et al. 1988](https://www.cs.umd.edu/~ben/papers/Callahan1988empirical.pdf)) **H**.
- In real work, marks were **3.5× faster** than menu selection and experts drifted to marks
  ([Kurtenbach & Buxton 1994](https://doi.org/10.1145/191666.191759)) **H**.
- Performance degrades as items grow; it holds best at ≤8, with even counts ([Kurtenbach et al. 1993](https://doi.org/10.1207/s15327051hci0801_1)) **H**.
- Blender ships pies for orientation, pivot, snap and shading ([2.80 notes](https://developer.blender.org/docs/release_notes/2.80/ui/)) **H**.

Recommendation (**M**): keep linear right-click menus as the primary, Windows-native path. Add one optional held-key pie in MAP
(≤8 entries: quad, select, pin, lattice, mask, on the wall, identify, test card) for experts. The palette (`docs/UX.md` P4)
stays the search path.

### 3.3 Progressive and staged disclosure

NN/g:
- Progressive disclosure "defers advanced or rarely used features to a secondary screen", which improves learnability,
  efficiency and error rate.
- **Staged disclosure** (wizards) suits tasks that "divide into distinct steps that have little interaction" and fails when
  users must alternate between steps ([NN/g](https://www.nngroup.com/articles/progressive-disclosure/)) **H**.

So the guides (§1.6) are staged, but each step keeps the live view and the object's full inspector one click away
("Open in Pro"), because alignment and blend tuning alternate (**M**). PlayMap's Simple/Pro switch hides nothing yet
(`docs/DESK.md`). Simple should hide Pro fields, never air-safety controls.

### 3.4 Keyboard nudge conventions

| Tool | Small | Big | Fine | Other |
|---|---|---|---|---|
| Resolume (slices) | arrow 1 px | Shift 10 px | — | Ctrl disables snapping ([input selection](https://www.resolume.com/support/en/input-selection)) **H** |
| Figma | 1 | Shift 10 (both configurable) | — | [nudge](https://help.figma.com/hc/en-us/articles/4404575206295-Set-small-and-big-nudge-values) **H** |
| disguise QuickCal | arrow 1 px | — | — | [projectors](https://help.disguise.one/workflows/calibration-projection/quickcal-projectors) **H** |
| Watchout 7 calibration | arrows | velocity setting | — | W/A/S/D select, gamepad sticks **H** |
| Pixera markers | arrows | — | — | **H** |
| Epson EPPT | Ctrl+arrow at the chosen step (0–7 keys: 0.5…30) | Alt 5 steps | Ctrl+Shift 0.5 | i = initialise, Ctrl+Z/Y **H** |
| PlayMap quad (today) | arrow 1 px | Shift 10 | Alt 0.1 | Tab / Shift+Tab steps corners **H** (`docs/DESK.md`) |

Keep PlayMap's mapping; it matches the field. Add a configurable big step (Figma), the unit in the status bar, typed fields and
undo for nudges (Epson) (**M**).

### 3.5 Persistent tool modes and mode visibility

- Blender remembers the active tool per workspace and mode, and the status bar shows each tool's modifiers
  ([tool system](https://docs.blender.org/manual/en/latest/interface/tool_system.html)) **H**.
- Unity tints the **whole editor** in Play mode "for ease of identifying when you're in Play mode", because edits made then
  are lost ([Unity colours](https://docs.unity3d.com/Manual/preferences-colors.html)) **H**.

PlayMap's mode frame (red when armed, amber when patterns are on a wall) is the same idea. Keep it at the window edge only, so
it never reduces content contrast.

---

## 4. Colour semantics: cognition without alarm fatigue

### Standards and evidence

- **IEC 60601-1-8**, Table 2 ([2006 text](https://mdcpp.com/doc/standard/IEC60601-1-8-2006.pdf)) **H**:

  | Priority | Colour | Flashing | Duty |
  |---|---|---|---|
  | High | **red** | 1.4–2.8 Hz | 20–60 % |
  | Medium | **yellow** | 0.4–0.8 Hz | 20–60 % |
  | Low | **cyan or yellow** | constant | — |

  Further rules:
  - Text that flashes on and off "is discouraged"; flashing between normal and reverse video is acceptable.
  - The highest priority must be perceivable at 4 m, and the specific condition legible at 1 m.
  - Priority may be marked !, !!, !!!.
  - Cyan was added so low priority differs from medium by colour.
- **ISA-101 high-performance HMI** ([ISA overview PDF](https://www.isa.org/getmedia/06130a38-f7af-4b35-8c9c-2c34f25c1977/The-High-Performance-HMI-Overview-v2-01.pdf)) **H**:
  - "Color, by itself, is never used as the sole differentiator".
  - Bright colours are reserved for **abnormal** situations; normal screens are not covered in red and green.
  - Alarm colours are used for nothing else.
  - The indicator flashes while **unacknowledged** and stops after acknowledgement.
  - Redundant coding: colour, shape and text.
  - Rockwell's style guide adds: grey backgrounds, cool low-intensity colours for live data, no gradients, line weight for
    emphasis ([Rockwell PDF](https://literature.rockwellautomation.com/idc/groups/literature/documents/wp/proces-wp023_-en-p.pdf)) **H**.
- **Alarm fatigue**:
  - An estimated **85–99 %** of alarm signals need no intervention; alarm fatigue is "the most common contributing factor" in
    alarm-related sentinel events ([Joint Commission SEA 50](https://digitalassets.jointcommission.org/api/public/content/f65e5c9df2b94000a99445e0a7877007)) **H**.
  - A systematic review found **74–99 %** of monitor alarms non-actionable. Alarm delays and widened limits help
    ([Paine et al. 2016](https://pmc.ncbi.nlm.nih.gov/articles/PMC4778561/)) **H**.
- **Colour-vision deficiency**: Okabe–Ito's CUD palette is black, orange `#E69F00`, sky blue `#56B4E9`, bluish green `#009E73`,
  yellow `#F0E442`, blue `#0072B2`, vermillion `#D55E00`, reddish purple `#CC79A7`
  ([palette](https://siegal.bio.nyu.edu/color-palette/), [R's default since 4.0](https://doi.org/10.32614/rj-2023-071)) **H**.
  Common deficiencies are red-green, green-yellow and white-cyan (ISA, above) **H**. Contrast for non-text UI is WCAG 2.x
  SC 1.4.11 (3:1) and use of colour SC 1.4.1 ([W3C](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast)) **M** (not
  re-read here).

### PlayMap today

- `#3D9BFF` accent.
- Status lights green, amber, red and grey.
- Projectors alternate `#FFB74D` (amber-orange) and `#4FC3F7`. Surfaces default to `#CE93D8`.
- The red armed frame and amber on-wall frame.
- No colour for workspaces or stages (`desk-today.md` §h).

### Recommendation (M)

| Channel | Job | Palette | Rules |
|---|---|---|---|
| **Status** | the air and the node's condition | red (air at risk, PANIC, ARMED), amber (attention), cyan (information), green (ready, sparingly), grey (off or unknown) | Saturated and reserved for this job only. Every light also has a shape (●▲■) and a word. Flash only an unacknowledged red or amber item, once per event, at the IEC rates, then hold steady. Never flash text. Information is cyan and never yellow, so it cannot be mistaken for attention. Escalate only actionable conditions; quiet transient ones with a short delay (alarm-delay evidence). |
| **Identity** | which projector, output or client | Okabe–Ito sky blue, bluish green, blue and reddish purple at reduced saturation, plus a number badge (P1, P2) | Never an amber, orange, red or vermillion hue: replace `#FFB74D`. Used for outlines, pins, cameras, wall overlays and Identify. A projector's line-up overlay is tinted its identity colour (disguise). |
| **Stage** | where am I in the workflow | a 2–4 % tint or a 2 px accent on the workspace tab, panel headers and guide steps (Setup and Outputs · Map · Scan · Play and Show) | Equal luminance, low chroma. Red never marks a stage. The armed frame overrides all stage tints. Resolve's page bar is the model for stages as places ([Resolve](https://www.blackmagicdesign.com/products/davinciresolve)) **M**. |

In dark rooms, keep the night-red theme from `docs/UX.md` P12, but there red must still mean only status. The theme recolours
neutrals, not the meaning of the status colours (**M**).

---

## 5. Medical-device usability lessons that transfer

- **IEC 62366-1** ([ISO page](https://www.iso.org/standard/63179.html), [overview](https://medtechlearning.com/learning/iec-62366-1-usability-engineering.html)) **H/M**:
  - the process runs use specification → UI characteristics related to safety → **hazard-related use scenarios** → UI
    specification → **formative** then **summative** evaluation;
  - a use error is evidence of a design problem, not a user failure.
- **FDA human factors guidance** ([PDF](https://www.fda.gov/files/medical%20devices/published/Applying-Human-Factors-and-Usability-Engineering-to-Medical-Devices---Guidance-for-Industry-and-Food-and-Drug-Administration-Staff.pdf)) **H**:
  - validation tests **critical tasks** with representative users, a production-equivalent UI and realistic conditions;
  - severity, not probability, drives what must be designed out.

For PlayMap (**M**):
- Name the hazard-related use scenarios: blacking out the wrong output, editing a live surface while armed, a blend left on
  the wrong projector, a panel dropped over the air, a stale view mistaken for live.
- Give each a desk-level test asserting published state (the doctrine already tests fields, not words).
- When hardware arrives, run one formative session per guide. Physical testing is not yet available (owner, answer 5).

---

## 6. Speed and latency: lessons from game engines and medical devices

### 6.1 Frame budgets and input-to-photon

- Budgets are 16.7 ms at 60 Hz and 8.3 ms at 120 Hz.
- NVIDIA defines PC latency = input-to-frame-start + frame-start-to-present + present-to-displayed
  ([NVIDIA blog](https://developer.nvidia.com/blog/understanding-and-measuring-pc-latency/)) **H**.
- PresentMon 2.2 added a mouse-button **click-to-photon** metric
  ([release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.2.0)) **H**.
- **DXGI**: create the flip-model swap chain with `FRAME_LATENCY_WAITABLE_OBJECT` and **wait before rendering**, then sample
  input and time, render and present.
  - The default maximum latency for waitable swap chains is 1. Raise it to 2 only if CPU and GPU each fit the frame but their
    sum does not ([DXGI 1.3 latency](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains)) **H**.
  - In independent flip this reaches 1 frame of latency ([flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)) **H**.
  - `ALLOW_TEARING` is lower still but tears, which is wrong for the air **H**.

For PlayMap (**M**):
- Show time is sampled after the wait, so frame = f(leader time at that vblank). The doctrine already demands this.
- Publish p50/p95 for desk drag-to-wall, GO-to-first-frame, and capture glass-to-glass in STATE beside missed vblanks
  (`docs/UX.md` §6 asks for this).

### 6.2 Deterministic loops

Fiedler's "Fix your timestep" decouples a fixed simulation step from render rate, clamps the frame time (0.25 s) and
interpolates for display ([Gaffer on Games](https://gafferongames.com/post/fix_your_timestep/)) **H**. PlayMap's rule is
stronger: no integration at all, since content is a pure function of show time. The lesson that transfers is the **clamp**
(**M**): after a stall, never try to "catch up" by replaying; jump to f(t).

### 6.3 Memory and GC on the frame path

- Holzmann's Power of Ten (JPL; used for the Curiosity flight software) ([P10](https://spinroot.com/p10/), [paper](https://spinroot.com/gerard/pdf/P10.pdf)) **H**:
  - fixed loop bounds;
  - **no dynamic allocation after initialisation**;
  - check return values;
  - all warnings on, with analysers.
- .NET ([latency modes](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)) **H**:
  - `GCLatencyMode.SustainedLowLatency` suppresses foreground gen-2 collections for long periods, at the cost of a larger heap;
  - `GC.TryStartNoGCRegion` is for short critical paths.

PlayMap already has Release allocation tests. Extend them to every lane the air depends on (render, present, capture upload),
and set SustainedLowLatency while armed (**M**). Both are "measured, then governed": add the governor only with a measured
pause.

### 6.4 Watchdogs and safety architecture

AAMI's safety architecture partitions a device into operational and **safety subsystems**, with
([BI&T 2017](https://array.aami.org/doi/full/10.2345/0899-8205-51.5.380)) **H**:
- independence and isolation;
- separate resources, so UI faults cannot propagate;
- a uniform **detect → notify → mitigate → record** flow;
- superseding control to reach a **known safe state**.

Firmware practice adds the same lessons ([overview](https://beefed.ai/en/safety-architecture-patterns-medical-firmware), [ICS](https://www.ics.com/blog/developing-safe-medical-devices-means-following-these-principles)) **M**:
- fed by a health task only after self-checks, never fed blindly;
- an independent supervisor;
- a few fall-back modes rather than one per failure.

For PlayMap (**M**):
- The engine is the operational subsystem. A small supervisor (same exe, `PlayMap watch`, or the desk) watches the engine's
  heartbeat and present cadence.
- The render lane's watchdog feeds only when a frame was presented in budget.
- Mitigations are the existing PANIC plans: hold or black per output, plus a GPU device-removed recovery.
- Every trip is a journal row. The desk shows it in the attention bar once, and acknowledgement stops the flash.

### 6.5 Database and servers

PlayMap uses no database. It keeps the project JSON (temp + rename), a journal JSONL and settings files, with
`ProjectStore` as the single writer (`desk-today.md` §j). That matches the game/medical pattern of append-only logs and
immutable snapshots; keep it.

If an index is ever needed (a media library over thousands of files, calibration history search), use **SQLite in WAL**
([WAL](https://sqlite.org/wal.html)) **H**:
- readers do not block the writer, and there is one writer;
- `synchronous=NORMAL` skips the per-commit sync, so only checkpoints sync, and checkpoints can run on their own thread;
- it can still return SQLITE_BUSY in edge cases.

Run it on its own lane, never on render or present. HTTP, MCP and wire servers stay off the frame lanes, with bounded queues
and back-pressure (**M**).

### 6.6 Capture-card pass-through on Windows 11 Home

- **Magewell Pro Capture** ([article](https://www.magewell.com/developer/26/detail), [PDF](https://www.magewell.com/files/LowLatency%20&PartialNotification.pdf), [specs](https://www.magewell.com/tech-specs/pro-capture-hexa-cvbs)) **H**:
  - low-latency mode transfers after **64 lines**;
  - **partial notification** lets software start on part of a frame;
  - 1080p60 total latency fell from 32.0 ms to 17.7 ms in Magewell's test;
  - cards support GPUDirect for NVIDIA and DirectGMA for AMD.
- **NVIDIA GPUDirect for Video**: sub-frame transfers into GPU memory, but only on **professional** GPUs (RTX A-series, Quadro,
  Tesla) ([NVIDIA](https://developer.nvidia.com/gpudirectforvideo)) **H**. GeForce does not support GPUDirect RDMA
  ([NVIDIA forum moderator](https://forums.developer.nvidia.com/t/question-about-rdma-and-resizable-bar-on-the-rtx4090/294300)) **H**.
- **AJA with RDMA** roughly halved transfer time, which made 4K RGBA at 60 fps fit the frame
  ([NVIDIA blog](https://developer.nvidia.com/blog/supporting-low-latency-streaming-video-for-ai-powered-medical-devices-with-clara-holoscan/)) **H**.
- **D3D12 GPU upload heaps**: CPU writes straight into VRAM through Resizable BAR (`D3D12_HEAP_TYPE_GPU_UPLOAD`; check
  `OPTIONS16.GPUUploadHeapSupported`; ReBAR on in BIOS for dGPUs). Retail use needs Agility SDK 1.613+ on Windows 11 build
  26080 or later ([spec](https://microsoft.github.io/DirectX-Specs/d3d/D3D12GPUUploadHeaps.html), [blog](https://devblogs.microsoft.com/directx/agility-sdk-1-613-0/)) **H**.
  Windows 11 24H2 is build 26100, so current Home installs qualify (**M**).

Recommendation (**M**):
- Treat consumer GPUs as the baseline. The pass-through lane runs: the card's low-latency/partial callback → write slices into
  a GPU upload heap (or a pinned upload buffer plus the copy queue) → shader YUV→RGB → sample the newest complete frame (or the
  newest complete slice set, if the mapping only needs the top region) at the waitable wait.
- Keep **at most one frame queued** (newest-wins already).
- Use GPUDirect only as an optional fast path when a professional GPU is present.
- Publish glass-to-glass p50/p95 per input.
- DeckLink, AJA and Magewell SDKs load at run time and are never redistributed.

---

## 7. Gaps and uncertainties

- Not verified first-hand:
  - Christie, Barco and Epson "AutoStack" details beyond Epson's own guide;
  - HeavyM's current simple mode;
  - Lightform's status;
  - Resolve's page colours (Resolve's pages are a structure precedent, not a colour one);
  - the WCAG text (cited from standing knowledge).
- The GeForce Warp & Blend removal (drivers 570/580) is a single forum report (**L**). PlayMap does not depend on it.
- Time targets for the guides are estimates. No physical hardware testing is available yet (owner, answer 5).
- The camera-as-content-space path assumes PlayMap's scan correspondences cover the whole projector raster at the audience
  seat; occlusion at oblique seats needs a fallback (eyepoint path) (**M**).

## Sources

All links appear inline above. The main ones:
- **Watchout 7**: [edge blending](https://docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/edge-blending.html), [displays](https://docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/adding-displays.html), [calibration](https://docs.dataton.com/guide/watchout/devices/display-calibration.html).
- **Resolume**: [edge blending](https://resolume.com/article/54), [input selection](https://www.resolume.com/support/en/input-selection), [output transformation](https://www.resolume.com/support/en/output-transformation).
- **MadMapper**: [soft-edge](https://docs.madmapper.com/madmapper/6/6.-outputs/soft-edge-blending), [3D](https://docs.madmapper.com/madmapper/6/4.-surfaces/advanced-3d-and-scanning).
- **Millumin**: [soft-edge](https://help.millumin.com/docs/display/softedge/), [structure](https://help.millumin.com/docs/display/structure/).
- **Pixera**: [marker calibration](https://help.pixera.one/mapping-/marker-calibration-workflow), [layer mapping](https://help.pixera.one/layer-mapping-effects?kb_language=en_US).
- **disguise**: [QuickCal](https://help.disguise.one/workflows/calibration-projection/quickcal-overview), [perspective mapping](https://help.disguise.one/designer/mapping/mapping-types/perspective-mapping).
- **VIOSO**: [3D free mode](https://docs.vioso.com/guide/workflows/mrd-freemode).
- **Scalable**: [manual](https://scalabledisplay.helpjuice.com/en_US/Software-User-Manuals/scalable-display-manager-user-manual).
- **Projector vendors**: [Epson EPPT](https://download3.ebz.epson.net/dsc/f/03/00/17/68/43/5919400321dbd21409508bf898987ab70f88c035/ProjectorProfessionalTool_162_EN.pdf), [Panasonic VSS](https://eu-uat.connect.panasonic.com/sites/default/files/media/document/2025-09/VSS_Inst-and-Ad_OI_DPQP1600WA_E.pdf).
- **Docking**: [Dock](https://github.com/wieslawsoltes/Dock), [Dock NuGet](https://www.nuget.org/packages/Dock.Avalonia/), [Actipro](https://www.actiprosoftware.com/docs/controls/avalonia/supported-technologies).
- **Desk UI precedents**: [Blender tools](https://docs.blender.org/manual/en/latest/interface/tool_system.html), [Unity Play tint](https://docs.unity3d.com/Manual/preferences-colors.html), [MS context menus](https://learn.microsoft.com/en-us/windows/win32/uxguide/cmd-menus), [NN/g progressive disclosure](https://www.nngroup.com/articles/progressive-disclosure/), [pie menus 1988](https://www.cs.umd.edu/~ben/papers/Callahan1988empirical.pdf), [marking menus 1994](https://doi.org/10.1145/191666.191759).
- **Colour and alarms**: [IEC 60601-1-8](https://mdcpp.com/doc/standard/IEC60601-1-8-2006.pdf), [ISA-101 overview](https://www.isa.org/getmedia/06130a38-f7af-4b35-8c9c-2c34f25c1977/The-High-Performance-HMI-Overview-v2-01.pdf), [Joint Commission SEA 50](https://digitalassets.jointcommission.org/api/public/content/f65e5c9df2b94000a99445e0a7877007), [Okabe–Ito](https://siegal.bio.nyu.edu/color-palette/).
- **Medical usability and safety**: [FDA HF guidance](https://www.fda.gov/files/medical%20devices/published/Applying-Human-Factors-and-Usability-Engineering-to-Medical-Devices---Guidance-for-Industry-and-Food-and-Drug-Administration-Staff.pdf), [AAMI safety architecture](https://array.aami.org/doi/full/10.2345/0899-8205-51.5.380), [Power of Ten](https://spinroot.com/p10/).
- **Latency and runtime**: [DXGI waitable](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains), [PC latency](https://developer.nvidia.com/blog/understanding-and-measuring-pc-latency/), [.NET latency modes](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency), [SQLite WAL](https://sqlite.org/wal.html).
- **Capture and GPU**: [Magewell low latency](https://www.magewell.com/developer/26/detail), [GPUDirect for Video](https://developer.nvidia.com/gpudirectforvideo), [D3D12 GPU upload heaps](https://microsoft.github.io/DirectX-Specs/d3d/D3D12GPUUploadHeaps.html), [NVIDIA Mosaic](https://www.nvidia.com/en-us/technologies/nvidia-mosaic/).
