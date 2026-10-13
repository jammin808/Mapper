# UX: a smooth, intuitive, efficient desk

*Interaction design and workflows for PlayMap, as of 12 Oct 2026. Every third-party fact has a source URL. Third-party UI details come from vendor documentation read during research; the final check could not re-open most vendor domains, so treat them as reported. Time targets are **targets** that we will measure; none has been measured. **Estimate** marks our judgement.*

## 1. Where we start from

The desk is merged: the window, the PLAY | MAP | SCAN workspaces, the Outputs and Network drawers, view models, a demo engine (`FakeEngineLink`) and drag tests through the real input path. It has never talked to a real engine; that integration is in progress. Nothing in this paper has been tried on a real wall.

Three foundations are worth keeping:

1. **Calibration lives on surfaces, separate from playlists.** Forum threads report the opposite pain in MadMapper: operators updating every cue after a calibration change ([forum](https://forum.garagecube.com/viewtopic.php?f=20&t=36243)) and venue mask fixes not carrying across 40 scenes ([forum](https://forum.garagecube.com/viewtopic.php?f=20&t=36417)). Both reports are unverified, but the design is right regardless.
2. **Masks and warps are stored in surface UV space.** When only a projector moves, they survive a re-solve by construction. When the mesh changes, they need retargeting, which does not exist yet (ROADMAP R4.7). The UI must never promise the second case before it is built.
3. **One verb vocabulary.** `DeskAction` drives the toolbar button, the right-click item and the shortcut, and its tooltip names the verb. This is the Blender operator / Figma Actions pattern.

The peers teach one thing above all: **smoothness comes from three sources, not from the number of features.**
- **A tight feedback loop:** edit on the wall, re-solve live, draw overlays within a frame.
- **Visible quality:** scores, residuals, coverage, drawn where the problem is.
- **Fearless recovery:** undo, fallback results, blind edit, Show Mode.

## 2. Principles, with evidence

| # | Principle | Evidence | What it means in PlayMap |
|---|---|---|---|
| P1 | **Latency is a feature. Budget it per interaction.** | On direct touch, users notice drag latency at about 11 ms and tap latency at about 69 ms; on indirect input, about 55 ms and 96 ms ([Deber et al., CHI 2015](https://dl.acm.org/doi/10.1145/2702123.2702300)). With a ~1 ms prototype, the average user noticed drag latency at about 6 ms, the most sensitive at about 2 ms ([Ng et al., UIST 2012](https://dl.acm.org/doi/10.1145/2380116.2380174)). These are touch studies; applying them to mouse, pen and glasses is our inference | The desk draws handles, lattice lines, mask paths and snap guides itself on the next UI frame. The engine re-renders behind them. See §6 |
| P2 | **The operator looks at the wall, not the laptop.** | HeavyM's cursor lets you "draw while looking directly at the projection" ([HeavyM](https://help.heavym.net/hc/en-us/articles/4407417515026-The-Cursor)); MadMapper's video cursor ([shortcuts](https://docs.madmapper.com/madmapper/6/2.-the-interface/keyboard-shortcuts)); Watchout draws the warp mesh on the surface ([Stage](https://docs.dataton.com/watchout-7-new/watchout/the-interface/the-stage-window.html)) | An **edit overlay pass** in the output stage. It is also the display for the smart-glasses goal (docs/XR.md) |
| P3 | **Do first, then adjust. No modal dialogs.** | Blender runs each operator with defaults, then offers Adjust Last Operation generated from the operator's properties ([manual](https://docs.blender.org/manual/en/5.2/interface/undo_redo.html)) | A **last-action panel** generated from the verb's typed parameters. Changing a field re-runs the verb as one undo step |
| P4 | **One vocabulary, reachable many ways.** | Blender F3 search shows each item's menu path ([manual](https://docs.blender.org/manual/en/latest/interface/operators.html)); Figma's actions menu ([Figma](https://help.figma.com/hc/en-us/articles/23570416033943-Use-the-actions-menu-in-Figma-Design)); users objected when Figma ranked by recency ([forum](https://forum.figma.com/archive-21/new-quick-actions-menu-21400)) | Ctrl+K palette over the verb catalogue with deterministic ranking. Each item shows its shortcut and wire verb. Text that matches no verb becomes a staged assistant proposal (§10) |
| P5 | **Consistent micro-gestures everywhere.** | Blender 5.0 and Pixera 25.2 added F2 rename ([Blender](https://developer.blender.org/docs/release_notes/5.0/user_interface/), [Pixera](https://help.pixera.one/pixera-252-overview-changelog)) | Double-click a value to type. Right-click to reset or bind. Shift = coarse ×10, Ctrl = fine ÷10. Alt-drag duplicates. F2 renames. Holding Ctrl suspends snapping |
| P6 | **Separate editing from running.** | QLab Show Mode locks editing and keeps transport live, with no password ([QLab](https://qlab.app/docs/v5/fundamentals/workspace/)); Mitti's panic outcomes ([Mitti](https://imimot.com/help/mitti/getting-started/playback)) | Show Mode, blind edit, a panic menu and locks. See §7 |
| P7 | **Make quality visible where it is, and make reverting trivial.** | OmniCal colours points by quality and keeps a fallback result ([calibration](https://help.disguise.one/workflows/calibration-projection/omnical-calibration)) | Every solve and scan is a named result with a score, its reasons and a one-click revert. The confidence map can be projected onto the object itself (§4.8) |
| P8 | **Snapping helps, and always has an escape.** | Watchout: Ctrl disables snapping ([Stage](https://docs.dataton.com/watchout-7/stage/stage.html)) | Snap to vertices, edges, scan creases and whole pixels. Ctrl suspends snapping |
| P9 | **Click-to-segment has a shared grammar. Use it.** | Resolve Magic Mask (+/− clicks) ([walkthrough](https://jayaretv.com/color/davinci-resolve-ai-magic-mask-2-explained/)); Premiere Object Mask hover preview ([Adobe](https://helpx.adobe.com/premiere/desktop/add-video-effects/work-with-masks/object-masking.html)) | Click-to-surface: hover preview, click adds, Alt-click subtracts, visible click list, Enter is done |
| P10 | **AI does the work, stays inspectable and never overrides the human.** | Users want both "do it for me" and "show me how" ([Adobe Research](https://research.adobe.com/news/do-it-for-me-or-show-me-how-the-research-behind-photoshops-ai-assistant/)); Mapping Matter's AI does not overwrite values a user typed ([disguise](https://help.disguise.one/mapping-matter/projectors)) | Staged proposals with a readable diff and a "show me" mode (§10) |
| P11 | **A web remote and a Companion module are expected.** | Resolume's web server and REST API ([blog](https://resolume.com/blog/21137)); Bitfocus Companion ([releases](https://github.com/bitfocus/companion/releases/tag/v4.0.0)) | `/remote` PWA and a Companion module generated from the verbs (§8) |
| P12 | **Dark rooms and accessibility.** | An ETC operator asked for a dark mode that keeps cues visible ([ETC forum](https://community.etcconnect.com/control_consoles/eos-family-consoles/f/eos-family/60986/brightness-settings)); red light disturbs dark adaptation least ([Mantiuk et al.](https://dl.acm.org/doi/pdf/10.1145/1620993.1621005)); Ableton 12 screen reader support ([manual](https://www.ableton.com/en/live-manual/12/accessibility-and-keyboard-navigation/)) | Day, dark and night-red themes; a UI brightness slider; Avalonia automation names taken from verb names |
| P13 | **First success in minutes.** | MadMapper's first guide is one quad, a test pattern and arrow nudges ([guide](https://madmapper.com/files/02-My%20First%20Video%20Mapping.pdf)) | A first-run "Light a wall" path (§9) |
| P14 | **Simple and Pro are presentations, not products.** | HeavyM's speed for simple jobs vs the depth 3D artists need | One key switches Simple (quad, drop, effect) and Pro (full inspector). Same verbs, same project |
| P15 | **Learn an object once, use it everywhere.** | — (our design) | One **object card** for a surface or projector (name, content, health, score, three most-used verbs) in the desk inspector, the phone, the glasses HUD and as Companion variables |

## 3. Information architecture

### 3.1 Keep the workspaces, add an object spine

PLAY | MAP | SCAN follow the engineering pipeline, but operators think in objects: "this surface", "projector 2", "the lobby playlist". So **selection is global**. Select a surface in MAP, switch to PLAY, and the inspector still shows it: its source, its playlist and the projectors that light it. Selection also flows to the wall (the selected surface is outlined during rehearsal) and to remote clients.

```
┌ Mode frame (colour = rehearsal / on-wall overlays / ARMED) ─────────────────────────────┐
│ [PLAY | MAP | SCAN]  [Simple|Pro]  Ctrl+K palette  On-wall-now chips  Readiness ● PANIC │
├──────────────┬────────────────────────────────────────────────┬─────────────────────────┤
│ Outliner     │ Workspace viewport                             │ Inspector               │
│ (surfaces,   │  PLAY: playlist lanes / grid view              │  (object card on top;   │
│  projectors, │  MAP: 3D view ⇄ projector view (Tab), tools    │   tabs: Content,        │
│  outputs,    │  SCAN: live guided capture                     │   Geometry, Blend,      │
│  playlists,  │                                                │   Bindings)             │
│  media, gens)│ Last-action panel (bottom-left, transient)     │                         │
├──────────────┴────────────────────────────────────────────────┴─────────────────────────┤
│ Drawers: Outputs (tiles, identify, layout) · Network (fleet / readiness / sync) · Bindings │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 Always-visible elements

- **Mode frame.** A full-width frame colour: neutral = rehearsal; amber = patterns or overlays are on the wall (scan, identify, edit overlay); red = ARMED. Watchout colours its top bar in display-edit mode ([Stage](https://docs.dataton.com/watchout-7-new/watchout/the-interface/the-stage-window.html)); we colour the whole frame so the mode cannot be missed.
- **On-wall-now chip per output.** What the audience sees on that output: show content, test pattern override, blackout or freeze, from the engine's `AirState` precedence.
- **Readiness light.** Green, amber or red across all nodes and projectors. Click it to open the fleet board.
- **Presence.** Each connected client (desk, phone, glasses, Companion, assistant) has a colour. Its cursor and selection are drawn in that colour on every desk and, in rehearsal, on the wall. Whoever is about to change the wall is visible in the room.
- **PANIC.** A dedicated button and key that never opens a dialog.

### 3.3 Where geometry lives, said out loud

Playlists show no mapping fields. The surface inspector shows a "Mapping is shared by every playlist" badge. After a per-projector re-solve the desk says "Projector 2 re-solved. Masks and lattices unchanged: they live on the surfaces." After a rescan that changed the mesh, it shows what retargeting did, surface by surface, with a residual heat map (once R4.7 exists). Before R4.7, a mesh-changing rescan warns: "Hand edits on these 3 surfaces may need checking."

### 3.4 The verb-first contract

A desk feature is not done until it has a verb with typed parameters, a STATE key, a model field and a `DeskAction`. A CI test fails if any button, menu item or shortcut maps to a verb that is not in the catalogue. The catalogue has 78 verbs and does not yet cover pin warp, masks and the pen, projector power/shutter/lens, click-to-surface, coverage, audio, live inputs or output layout.

Typed parameters then generate, at no extra cost: the last-action panel, the palette entry, the "Bind…" target, the Companion action, the MCP tool and the integrator command sheet.

### 3.5 Concurrency and optimistic UI

The desk draws edits before the engine confirms them. When the engine rejects or changes an edit:
- the handle animates back over 150 ms to the engine's value;
- a reason chip appears at the pointer: "locked: show armed", "Ana is editing this surface", "outside lens limits";
- the same rule applies on the phone and the glasses.

Edits to one surface from two clients take a **soft lock**: the first editor holds it for 5 s after their last change (estimate). Others see the holder's colour and can take over with a deliberate action. The journal records who changed what.

## 4. Interaction designs

### 4.1 Surfaces: two paths

**Path A: no camera (the MadMapper path).** Many club and corporate gigs have no time to scan (estimate), so this path must be as fast as HeavyM.
- Press Q, then click 4 corners while looking at the wall; the projector draws the crosshair.
- Arrow keys nudge 1 px, Shift+Arrow 10 px, Tab cycles corners.
- A UV checker fills the quad live.
- The last-action panel offers "Convert to 3×3 lattice" and "Add mask". Promoting a quad to a lattice keeps its shape exactly.

**Path B: click-to-surface on the smart scan.** The smart scan is the camera image resampled into projector space.
- **Hover** outlines the candidate region on the desk and as a dim outline on the wall.
- **Click** adds and **Alt-click** subtracts. The click list stays visible and editable.
- Edges snap to depth and normal creases from the scan, which separate same-colour surfaces the image alone cannot.
- **Enter** accepts and produces a surface with UVs. The last-action panel offers simplify, feather and UV mode (planar, mesh or projector view).
- **Near term** uses the classical segmenter and a pinned SAM 2.1-tiny ONNX export for clicks (the export must keep the previous-mask input that +/− refinement needs). **Typing** ("all windows") needs a text-prompted model such as SAM 3, a verified export and a licence review; it is mid-term.
- Budget: the decoder answers in ≤100 ms after a click on the desk GPU (estimate). The encoder runs once per scan.

Both paths end the same way. Drag media from the bin onto the surface in the viewport, onto its name in the outliner, or onto its label on the wall preview. Hover highlighting shows which surface will receive it.

### 4.2 Warp lattices (stored in surface UV)

- **Sparse first.** A lattice starts at 3×3. Alt-click an edge to insert a row or column ([MadMapper mesh guide](https://madmapper.com/files/05-Mesh%20Warping.pdf)). Right-click a line to remove it.
- **Resolution changes keep the shape.**
- **Selection.** Click a point, drag a marquee, double-click an edge to select its row or column. Ctrl+Arrow moves between junctions.
- **Soft selection.** The mouse wheel during a drag sets the falloff radius, drawn as a ring on the desk and on the wall.
- **Pin fix.** A thin-plate-spline pin makes a local correction without densifying the grid. The maths exists in the mapping module.
- **Affine lock** stops lattice edits from moving the whole surface.
- **On the wall,** the lattice is drawn in a high-contrast colour with the selected point larger. A "flash grid" key shows a fine test grid for 2 s.
- **Controller dials.** Stream Deck+ dials, X-keys or MIDI encoders nudge the selected node, feather or blend gamma, with detents and acceleration, so a tech can align while watching the wall without touching the laptop.
- **After a re-solve,** a residual heat map colours each hand-moved point by how far it now disagrees with the solve.

### 4.3 Pen masks (Bezier, in UV)

Follow the Illustrator and HeavyM convention ([HeavyM](https://help.heavym.net/hc/en-us/articles/360018758119-Drawing-and-editing-Faces)): click for a corner, click-drag for a smooth point, Alt breaks handles, Enter or clicking the first point closes, Esc ends.

The pen magnetises to scan edges and depth discontinuities; Ctrl suppresses it. "Mask from selection" turns any click-to-surface result into a Bezier path (the vision module's mask-to-path exists). Feather and expand are drag handles on the canvas. Several points can be selected at once. **On an iPad with Apple Pencil,** the `/remote` page draws the stroke on the frozen smart scan and the projector echoes it live; Pencil precision beats both a mouse at the wall and glasses.

### 4.4 Pins with live residuals (pose solve)

- **Split view.** The model on the left, with pins snapping to vertices, edges and creases. The projector output on the right; the operator places each pin with the video cursor while looking at the wall.
- **The solve re-runs on every drag** once 4 or more pins exist. Each pin shows a ring coloured by its residual: green under 1 px, amber 1–3 px, red over 3 px (estimate, to tune on hardware). Global RMS is shown.
- **RANSAC talks in words:** "Pin 4 disagrees by 14 px. Move it or disable it?"
- **Guidance.** A shaded overlay marks poorly constrained regions and prompts "add a pin here, off this plane" until 6 or more pins are well spread. disguise recommends 10–15 per projector for QuickCal ([QuickCal](https://help.disguise.one/workflows/calibration-projection/quickcal-ref-points)).
- **Lens locks** (shift, zoom) sit next to the solve, with values read back from the projector over its control protocol. Measured shift beats spec-sheet shift ([Watchout guide](https://dataton.com/watchout-users-guide/displays-and-projectors/3d-mapping-projector?token=35)).
- **Results.** Every accepted solve is named ("Pose 2, RMS 0.8 px"). Revert is one click.

### 4.5 Blend tuning

- **Show what was computed before asking for tweaks.** One key each toggles the visibility, incidence and distance weight maps, on the 3D view and on the wall.
- **Check modes,** one key each: W full white, G 50% grey, K black (to see the pedestal), I colour ID per projector, 1–9 solo a projector ([ProjectionTools manual](https://download.projectiontools.com/releases/ProjectionTools/6.1/documentation/ProjectionTools_6.1.1.pdf)).
- **Uniformity vs brightness slider** with a live readout, for example "brightest to darkest 1.18:1, peak 78%".
- **Local brush** in surface UV for gain and black level, with a draggable before/after split line across the wall.
- **During interaction** the engine computes only the blend being edited; the full solve runs on release.
- **Low-confidence regions** can switch to "winner takes pixel" with a narrow feather, which hides misregistration better than a wide blend ([VRST 2025](https://cgvr.cs.uni-bremen.de/papers/vrst25/projection_vrst25.pdf)).
- **Colour across projectors** (different makers, lamp ages, laser phosphors) is a mid-term workflow with a colorimeter (ROADMAP M22). Until then the desk shows a per-projector white-point warning when the camera sees a mismatch.

### 4.6 Playlists (no cue stack, by design)

- **Lanes.** Each playlist is a column. Enter plays the selected item, arrows walk the list, and "select next on launch" lets one key step through a show ([Ableton](https://www.ableton.com/en/live-manual/12/launching-clips/)).
- **Glyphs** on each row show item behaviour: loop ×N, hold last frame, fade in/out, go to, launch quantisation (none / beat / bar / timecode) ([Mitti](https://imimot.com/help/mitti/cues/)).
- **Load vs Take.** Selected and playing are shown separately. "Load to preview" puts an item on a desk-only monitor.
- **Countdown colours:** amber at 30 s, red at 10 s.
- **Item actions.** Verb lines fire at an item's start or end: shutter, OSC out, another playlist's GO.
- **Non-deterministic badge** on items fed by live inputs, web pages or audio analysis: "breaks mirror backup". Fixes offered: "capture to HAP" (not for YouTube) or "feed both nodes from one router".
- **Grid view (optional).** N playlists as columns; a column GO fires an `AT`-synchronised GO on every playlist.
- **TC-locked playlists.** Items carry a timecode in-point and keyframed `SET` tracks. No timeline engine (docs/COMPETITIVE.md §8).
- **Transitions.** Fade and Dip are drawn when an item ends into the next: the incoming item overlaps the last T of the outgoing one, mixed in linear light (a dip goes through black at the middle). A GO, NEXT or PREV taken by hand cuts.

### 4.7 Multi-output planning

- **Planner in MAP, offline and local.** A projector and lens library (throw, shift, lumens, short-throw). Drag projectors in 3D. Heat maps for lux, pixel density, stretch, shadows, overlap and acuity at a viewer position. Coverage % and overlap % read-outs; PDF and CSV export. disguise Mapping Matter has comparable overlays but needs a connection ([requirements](https://help.disguise.one/mapping-matter/system-requirements)). Coverage analysis exists in the mapping module.
- **Calibration simulation** runs the scan simulator on the planned rig before arriving. Warn when camera overlap is below about 20%, where calibration error rises sharply in one study ([Waterloo](https://openjournals.uwaterloo.ca/index.php/vsl/article/download/6363/6231/27493)).
- **Physical identification.** Each output flashes its name and colour on its projector; a multi-view shows all outputs.
- **Drag to assign.** Drag a planned projector onto an output tile. Resolution, refresh and bit-depth mismatches against the EDID are flagged inline.

### 4.8 Scanning: one guided screen, not a wizard

One live screen, modelled on Apple Object Capture's dial and live warnings ([WWDC23](https://developer.apple.com/videos/play/wwdc2023/10191/)):

1. **Camera probe on plug-in.** Chips: manual exposure yes/no, focus lock yes/no, white-balance lock yes/no, format (raw or MJPEG), and banding risk with this projector. The probe recommends Fast, Robust or "use a different camera". Many UVC webcams expose no manual exposure on Windows and compress to MJPEG under USB bandwidth limits; browsers cannot reliably lock exposure or focus, especially iOS Safari (estimate, to confirm per device). The tested camera list (ROADMAP R1.13) backs this.
2. **Projector settings card.** Dynamic contrast and auto iris off, eco or laser dimming off, frame interpolation off, auto keystone and auto-fit off, a fixed picture mode, internal geometry reset. Set over the control protocol where possible; otherwise a checklist with the menu path for that model.
3. **Pre-flight runs itself,** one chip per check: exposure locked from an HDR probe frame, **exposure set to a multiple of the projector refresh period** (DLP colour wheels and PWM dimming band otherwise), latency measured from white flashes, ambient light, focus score.
4. **Capture.** A dial with an ETA per projector. Live chips: "camera moved", "too bright", "projector 2 contributes no light". The scan pauses when a check fails.
5. **Review.** Coverage and confidence overlay plus one score with its reasons. Every red area links to a fix ("raise exposure", "add a view here"). **Press P to project the confidence map onto the object,** with numbered problem areas, so the operator sees on the wall where the scan is weak.
6. **Re-scan only what changed.** "Re-solve projector 2" replays that projector's plan.
7. **Phone as camera.** The engine serves the `/scan` page. Expect it to need Robust mode because of exposure control (estimate).

### 4.9 When structured light fails: per-material fallbacks

| Material | Failure | Fallback offered |
|---|---|---|
| Scrim, gauze, fabric | Light passes through; mixed returns from behind | Scan with a backing flat, or quad/lattice by hand on the camera image |
| Glass, windows | No return, or reflections of other surfaces | Mask out automatically (low confidence); fit the frame from surrounding pins |
| Black, dark paint | Low modulation | Robust mode (more frames, longer exposure); else pins on the model |
| Glossy | Specular highlights, interreflection | Difficult mode (complementary codes, high-frequency phase); camera off-axis |
| Retroreflective | Saturation | Lower projector output for the scan; HDR exposure bracket |
| Outdoor facade at distance | Ambient city light, low signal | Night scan; model plus pins (§5.6) |

After a partial scan, every surface with low confidence keeps a manual path: pins, quad or lattice on the frozen camera image. The scan never leaves a surface without a way to finish it.

### 4.10 Bindings (MIDI, OSC, DMX learn)

Right-click any value, choose **Bind…**, then move a controller. A monitor shows the incoming message. Range, curve and soft takeover appear in the last-action panel. A bound control gets a corner badge. Feedback goes back to motor faders and LEDs, rate-limited to 50 Hz ([Companion v5 notes](https://github.com/bitfocus/companion/blob/main/docs/user-guide/9_whatsnew/v5-0-0.md)).

### 4.11 Media management

Bin search and tags; thumbnails with scrub; missing or offline media relink by hash; a per-asset version column showing which version each node holds and whether it is hardware-decodable there; proxies for the desk when the original is a heavy codec.

### 4.12 Working with other people's tools

- **"Bring my setup" on first run.** Import Resolume Advanced Output XML (slices, warps, masks) and MPCDI so a switching user sees their own venue on minute one. MadMapper project import is mid-term because the format is undocumented.
- **"Send to designer."** One verb exports per-surface UV templates (PNG, PSD, SVG) with surface names, pixel sizes and safe areas. Renders that come back named by surface ID are assigned automatically.

## 5. Signature workflows and time targets

### 5.0 The canonical time-target table

**This is the one table. docs/ROADMAP.md and docs/XR.md refer to it.** All times are **targets**, measured with telemetry from the start event to the stop event. "Tested webcam" means a model on the R1.13 list with manual exposure.

| Workflow | Target | Start → stop | Hardware and conditions | User | Benchmark |
|---|---|---|---|---|---|
| First light | ≤3 min new, ≤60 s returning | App launch → content looping on a projector | Laptop, one projector, signed build | New user | MadMapper first guide |
| Map a box, no camera (3 faces) | ≤3 min first time, ≤90 s regular | Q pressed → three faces with content | One projector | Regular | MadMapper and HeavyM: unmeasured (estimate 5–20 min first time) |
| **Map a box with one projector and a webcam** | **≤3 min from plugging in; ≤2 min from pressing Scan** | Camera plugged in → content on three faces | Tested webcam, office light, matte box | Regular | Unmeasured. Vendor claims for AI entrants range from seconds to minutes (unverified) |
| Known object from a model with pins | ≤4 min with 6–8 pins; ≤90 s with scan-bridged registration | Model loaded → accepted solve | One projector | 3D artist | MadMapper manual point matching |
| Two-projector blend on a curved wall | ≤10 min from rigged projectors; ≤2 min manual touch-up | Scan pressed → accepted blend | Tested webcam, two projectors | Regular | Manual 2D soft edge |
| Re-align after a bump | ≤60 s, ≤10 s of patterns, no mask rework | Re-align pressed → accepted pose | Rig camera still in place; projector moved, mesh unchanged | Tech | OmniCal one-click recal, Atlas |
| Late media swap | ≤30 s operator time | File dropped → staged for next boundary | Media already transferred | Operator | Watchout "Create Version" |
| Preflight, 4 nodes and 8 outputs | ≤90 s | PREFLIGHT pressed → board complete | Wired show network | Tech | Pixera Hub dashboards |
| Bind a MIDI control | ≤3 s per binding | Bind… → bound | Any class-compliant controller | Operator | MadMapper/Resolume learn |
| Add a follower node and verify sync | ≤60 s excluding media transfer | Node seen → sync certificate | Wired network | Tech | Watchout spare by name |
| Assistant builds a look | ≤15 s to staged preview | Prompt sent → diff shown | Claude or local model | Any | — |
| Laser-pointer pick | ≤2 s per surface | Dot appears → surface outlined | Rig camera, calibrated projector | Any | — |
| Phone pick | ≤20 s to first surface, ≤5 s each after | Page opened → first confirmed surface | Phone on venue Wi-Fi | Any | ETC Augment3d |
| Headset pick and assign | ≤8 s per surface | Ray on surface → content assigned | Quest 3, rehearsal lighting | Regular | — |

### 5.1 Map a box with one projector and a webcam (the headline)

**Pattern budget (estimate).** Fast mode on a 1920×1080 projector: Gray code on both axes with inverses (about 2 × 11 × 2 = 44 frames, fewer with coarse-to-fine) plus 4-step phase on both axes (8) plus white and black (2), about 54 frames. With 2–4 frames of projector–camera latency and settle per pattern at 30 fps, that is 5–8 s of capture per projector, rising to 20–40 s in Robust mode or with longer exposures. This must be measured in Round 1.

**Storyboard** (desk | wall | error state):

1. Plug in the webcam aimed at the box. Desk: camera tile with live thumbnail and capability chips. Wall: unchanged. Error: "No manual exposure: Robust mode, or use another camera". (10 s)
2. Press **Scan**. Desk: projector settings card and pre-flight chips. Wall: geometry reset, then latency flashes. Error: a red chip with its fix; Scan waits. (15 s)
3. Patterns play. Desk: dial with ETA. Wall: patterns, then "scanning" text. Error: "camera moved" pauses and offers restart. (10–40 s, estimate)
4. Review. Desk: "Score 86, Good. Top face low confidence: raise exposure or accept." Wall: confidence map on request (P). (5 s)
5. Hover the projector-view image; the faces glow on the real box. Click three faces, Alt-click the table, Enter. Error: a low-confidence face offers "finish by hand". (15 s)
6. Drag three clips onto the faces, or tell the assistant "fill the box with the nebula loop" and approve the diff. (10–20 s)

Total ≤2 min from step 2. Everything stays local. **We film this storyboard as the Round 1 acceptance test**, alongside the telemetry.

### 5.2 First light (new user, laptop, one projector)

1. Launch a signed build. A first-run card offers "Light a wall" and "Bring my setup".
2. If Windows Firewall asks, the desk explains in one sentence why (the engine and desk talk over the network) and that "Private networks only" is fine.
3. Displays are listed by EDID name. Click the projector: it goes fullscreen with `pattern.identify`.
4. Choose "Draw 4 corners" or "Scan with this camera".
5. With 4 corners: click, nudge, done. Drop any clip; it loops with a 0.5 s fade.
6. Only after the first frame: "Make this a show machine?" offers the hardening report (power plan, notifications, updates).

### 5.3 Re-align after a bump

1. **Near term:** the operator sees the drift and selects the projector. **Mid term:** drift watch raises an amber chip, "Projector 2 drifted about 6 px", with a camera thumbnail.
2. Click **Re-align P2**, press it on a Stream Deck, or ask the assistant. If the show is armed, the desk offers to wait for a boundary or to fade P2 first.
3. A sparse check runs on P2 only. The pose delta is staged with its score, and a before/after split sweeps across the wall.
4. Accept. Masks and lattices are unchanged because only the pose moved. A heat map flags any hand point that now fights the solve. Revert is one click.

### 5.4 Run a corporate show

1. **Preflight.** One board for every node and projector: hardware decode proven at show resolution, media hashes present, sync lock and clock offset, shutters, lamp hours, output signal, non-deterministic sources. Each red item links to its fix verb.
2. **SHOW ARM.** The frame turns red. Permissions change per §7.1. The edit overlay is stripped.
3. **Operate.** Stagewatch's VT link or a Stream Deck fires `PLAYLIST GO`. Loaded and Playing are shown separately, with countdown colours.
4. **Blind-edit** a late clip swap off-air; it commits at the next item boundary.
5. **Trouble.** PANIC, then "Fade to black and hold", "Fade and rewind" or "Fade and load next".
6. **DISARM.** The journal records who fired what and when. None of it went into undo.

### 5.5 Run PlayMap from Patterns and Stagewatch

1. PlayMap announces itself on the Show Bus. In Patterns it appears as a node with its playlists and outputs; in Stagewatch as a VT source.
2. A Patterns cue's GO sends the same `PLAYLIST GO` verb the desk sends. Patterns' Interactive devices drive PlayMap parameters by the same dotted paths as a MIDI binding.
3. Stagewatch's VT link shows the running item's remaining time on the stage manager's timer, with the same amber and red thresholds as the desk.
4. When the assistant proposes a change and a Stagewatch approval desk is on the bus, the proposal appears there for approval; the PlayMap desk shows it as "waiting for Stagewatch".
5. The PlayMap desk can stay closed during the show. Its role is setup and rescue.

### 5.6 Map a building facade (stacked projectors)

1. Import the facade model (photogrammetry or LiDAR). Check scale against one tape-measured dimension.
2. In the planner, place long-throw projectors; plan **stacking** (two or three projectors per area for brightness).
3. On site at night: per projector, place 8–15 pins on window corners and cornices with the video cursor; lens shift and zoom locked from readback.
4. Optional night scan where signal allows; glass is masked out automatically by low confidence.
5. **Stack alignment:** project a fine grid from each stacked projector in turn, measure misregistration with the camera, and apply a per-projector lattice correction until the residual is sub-pixel (target ≤0.5 px, estimate).
6. Blend between stacks with the 3D auto-blend; check with W, G and K modes.

This is the most common commercial mapping job, and the one where long throw, ambient light and reflective glass break naive scanning. It is a mid-term workflow (ROADMAP M23).

## 6. Latency budgets

| Interaction | Budget | Basis |
|---|---|---|
| Handle, lattice line or snap guide under the pointer (desk) | ≤1 UI frame (8 ms at 120 Hz, 16 ms at 60 Hz) | Drag latency noticed at about 6–11 ms on touch (P1) |
| Projector output reflects a drag | ≤2 output vblanks + projector input lag | The operator watches the wall |
| Click or tap to visible state change | ≤50 ms | Tap latency noticed at about 69 ms on touch |
| Verb acknowledged over the wire | ≤100 ms | Classic responsiveness bound |
| Click-to-surface after a click | ≤100 ms (decoder only, estimate) | SAM encoder/decoder split |
| GO to first new frame | ≤1 output frame + projector input lag | Pre-scheduled lane switch |
| Phone or glasses pinch to outline on the wall | p50 50–90 ms, p95 up to ~150 ms on busy Wi-Fi (estimate) | Wi-Fi + next vblank + projector input lag. Projector lag is model-dependent: roughly 15–30 ms in a low-latency mode on some models, 30–100 ms or more on install projectors doing internal warp (estimate) |
| Over 1 s | Progress bar with ETA | |
| Over 10 s (scan, solve, registration, transcode) | Background and cancellable | |

How to meet them:
- The desk owns optimistic overlays. The `DeskSession` 60 Hz throttle per key applies to the wire, never to the handle under the pointer.
- The engine renders only the edited surface's warp and blend during a drag.
- p95 for each budget goes into STATE next to missed vblanks, so regressions show in CI and in the field.

## 7. Show safety

### 7.1 Who may do what, armed or not

This table is the single definition; docs/XR.md uses it.

| Role | Rehearsal | Armed |
|---|---|---|
| Desk operator (local) | Everything | Transport, blackout, PANIC, blind edit; mapping and layout locked |
| Remote operator (`/remote`, Companion, glasses with operator role) | Transport, content, highlight, edits as granted | **Transport-only** (GO, NEXT, blackout); no highlights or patterns on the wall |
| Observer (any paired client) | STATE, PICK, local overlays | Same |
| Assistant | Proposals; low-risk verbs per policy | Proposals queue only |

### 7.2 Rules

| Rule | Detail | Source of the idea |
|---|---|---|
| **Show Mode (`SHOW ARM` / `DISARM`)** | Locks mapping and layout. Transport, blackout and Esc stay live. Close and quit ask for confirmation. **No password** | [QLab](https://qlab.app/docs/v5/fundamentals/workspace/): "a safety mechanism… not a security mechanism" |
| **Blind edit** | Edit an off-air copy, see a readable diff, commit at the next item boundary with `AT`, or discard | Watchout and Millumin (reported) |
| **Panic with defined outcomes** | Fade to black and hold; fade and rewind; fade and load next | [Mitti](https://imimot.com/help/mitti/getting-started/playback) |
| **Locks** | Per playlist, surface or layer; block edits, not playback or inspection | [Pixera 26.1](https://help.pixera.one/en_US/pixera-261-overview-changelog) |
| **Live actions are not in undo** | Transport, blackout, shutter, identify and failover go to the journal; undo covers document edits | Pixera 25.2 fixed engine start/stop creating undo entries ([25.2](https://help.pixera.one/pixera-252-overview-changelog)) |
| **Risk depends on the caller** | The operator's own GO at the desk is never confirmed; AI and remote clients pass the gate | docs/COMPETITIVE.md §8 |
| **Keys** | GO, PANIC and BLACKOUT on dedicated keys that never open dialogs. Space plays/pauses only in PLAY; in viewports Space-drag pans | [disguise r32.2 notes](https://www.disguise.one/sites/default/files/2025-12/Designer_r32_2_Release_Notes.pdf) |
| **Upstream changes are announced** | "Output signal changed; the projector may rescale" | A projector's auto-scaling confused a Resolume user ([forum](https://resolume.com/forum/viewtopic.php?t=10954)) |
| **No heavy compute on an armed node** | Segmentation, depth, drift matching, local LLMs, BC7/BC6H transcodes and scan decoding run on the desk machine or an idle node. On a show node they run at low priority only in rehearsal, and are refused while armed | GPU contention with playback (estimate) |
| **Beam guard** | With a tracked head in a projector's frustum: in rehearsal, soft-mask the area around the head or dim that projector, warn and log. **While armed, warn the operator only; never blank a projector automatically mid-show.** Configurable per projector. It complements laser hazard-distance practice and does not replace it | Our design |
| **Destructive verbs never appear on hover or gaze** | Hold or a second confirm | [Apple WWDC25](https://developer.apple.com/videos/play/wwdc2025/303/) |
| **Updates** | An armed node never updates; show machines never auto-update | Our design |

## 8. Touch, tablet and remote

- **`/remote` PWA,** served by the engine and paired by a QR code on the desk. Role pages:
  - **Operator:** GO, NEXT and countdowns.
  - **Tech:** outputs, projectors, health, object cards.
  - **Kiosk:** one "Start show" button with a scheduled lockout.
- **Dark-room show strip.** A minimal night-red view: the focused playlists' current and next items, countdowns, readiness and PANIC, at about 5% brightness. Also on the phone the operator pockets.
- **Touch rules:** 44 pt minimum targets; no hover-only affordances; long-press for right-click; drag a value to scrub, double-tap to type.
- **Point and tap.** The phone camera view freezes on touch-down so the user taps a surface on a still frame; Touch Projector found freezing essential for stability ([CHI 2010](https://www.medien.ifi.lmu.de/pubdb/publications/pub/boring2010chi/boring2010chi.pdf)). The projector outlines what was tapped (docs/XR.md Phase 1).
- **iPad and Pencil** for masks and pens on the frozen smart scan (§4.3).
- **Companion module** generated from `/api/verbs` and STATE: variables per playlist (current, next, elapsed, remaining), countdown-colour feedbacks, presets per playlist, a connection-status variable, double-press for high-risk verbs. The Resolume module notes it cannot tell whether the OSC port is connected ([repo](https://github.com/bitfocus/companion-module-resolume-arena)); ours reports connection state.

## 9. Onboarding

- **"Light a wall"** first-run path (§5.2), with telemetry on launch-to-first-frame.
- **"Bring my setup"** importers (§4.12).
- **Three demo projects** in demo mode, built on the scan simulator: a box, a two-projector blend, a VT playback show.
- **Embedded offline help.** Every `DeskAction` links to its help entry. A "What's new" window after updates.
- **The palette teaches.** Search shows menu path and shortcut, so experts and newcomers share one map.
- **Collect and Save** puts the project and its content-addressed media in one folder; Merge Project combines two.
- **Signed builds** and one explained firewall prompt; hardening offered after the first frame, never before.
- **Localisation.** Strings externalised from day one; verb names stay English on the wire.

## 10. AI in the loop

- **Staged by default.** The assistant returns a diff ("12 surfaces change source; 4 outline effects; `PLAYLIST GO windows AT 20:00`"). Lines can be switched off individually. Approval applies everything as one undo step.
- **Wall diff.** In rehearsal, a wipe between before and after on the real surface, with changed surfaces pulsing in the assistant's presence colour. While armed, the diff is shown on the desk preview or a headset overlay only. A journal scrubber replays what changed, when, and who did it.
- **One box.** Text in the Ctrl+K palette that matches no verb becomes a staged assistant proposal.
- **"Show me" mode.** The assistant highlights the verb, field or palette entry instead of executing it.
- **Never overwrite typed values.** Suggestions fill only fields the user has not set.
- **Risk gate.** Low and review verbs follow the approval policy. High verbs wait for a human. While armed the assistant only queues proposals.
- **Untrusted input.** File names, metadata, web pages and remote-writable STATE fields are data in the assistant's context, never instructions.
- **Voice and deixis** ("put this on that") arrive with the XR work.

## 11. Anti-patterns to avoid

1. Calibration stored in content or cues.
2. Blends that break under ordinary operations: rotating a slice reportedly killed Resolume's soft edge ([forum](https://resolume.com/forum/viewtopic.php?t=18227)). Test 2D edge cases even though our blend is 3D.
3. Invisible upstream changes (§7.2).
4. Mode confusion. If there are modes, colour the whole frame.
5. Palettes ranked by recency.
6. One-way project upgrades.
7. Cloud-only tools.
8. Destructive controls revealed by gaze or hover.
9. Magic-word unlocks: disguise unlocks parts of its UI with a typed word ([shortcuts](https://help.disguise.one/designer/getting-started/designer-keyboard-shortcuts)). Power must be discoverable.
10. Mirroring the desk into a headset. Remote-desktoping into Resolume from a Vision Pro was awkward ([4Wall](https://www.4wall.com/blog/2024/09/04/exploring-the-future-of-production-tech-apple-vision-pro-in-lighting-and-video-design)). Build thin clients on the verbs.
11. A private desk API.
12. Promising rework-free rescans before retargeting exists.
13. Silent CPU fallback, silent model downloads, or silent heavy compute on a show node.

## 12. How we will know it is smooth

- Opt-in telemetry against §5.0 and §6: launch to first frame, scan to surfaces, drag p95, verb ack p95, missed vblanks.
- A monthly usability session with three people new to PlayMap (a VJ, a corporate AV tech, a 3D artist). Each maps a box without help; record the time and every hesitation.
- The filmed §5.1 storyboard re-run every release.
- A public benchmark: lab scan datasets and simulator scenes, so anyone can check our accuracy claims.
- A field log of every "I didn't know it did that" and "where did my mask go". Each becomes a design bug.
