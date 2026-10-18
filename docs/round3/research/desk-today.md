# The desk and the engine at main 6f24310, against the owner's new requirements

A read of the tree at `main` 6f24310 (round 2 closed). Every claim about code cites file:line. "Wire-only" means the verb runs
over the wire, HTTP, MCP, Companion or a binding but no desk control sends it. Reach numbers come from a literal scan of the
catalogue's 299 rows (`src/PlayMap.Core/Wire/VerbCatalog.cs`) against the desk's sources (fake engine excluded) and the card
descriptors. It is an estimate, not a test: 102 rows appear as a line in desk code, 85 only as a card action, and 112 in
neither. The estimate is a literal search, repeatable on any tree; its lists are not kept.

Verdicts: **CONFIRMED** means read in the code; **PLAUSIBLE** means inferred from the code and not run.

---

## (a) The four-click quad

**What exists.**
- The tool. `QuadTool` takes up to four wall-normalised clicks, then Enter draws (`src/PlayMap.App/ViewModels/Map/QuadTool.cs:8-80`).
  It is armed by Q, the tool strip button (`src/PlayMap.App/Views/MapView.axaml:95-97`), and the projector view's right-click on
  empty space, which offers "Quad tool" and "On the wall" (`src/PlayMap.App/ViewModels/MapViewModel.cs:61-62`,
  `src/PlayMap.App/Controls/ProjectorCanvas.cs:588-605`).
- The line sent. Enter sends `SURFACE QUAD <pj> x1 y1 … x4 y4 PX|RASTER`, rounded to 0.01 px (`MapViewModel.cs:576-586`,
  `:588-613`). The catalogue row is `VerbCatalog.cs:497`. The engine orders the corners and checks them convex and at least 64 px²
  (`docs/CALIBRATION.md:11-14`, `src/PlayMap.Core/Mapping/PixelQuadMath.cs:149-197`).
- Persistence. A drawn quad is a project `Surface` with `Pixel` set (`src/PlayMap.Core/Model/StageModel.cs:157-158`), so it
  stays. It is listed under Surfaces in the outliner as "quad on <projector> · <source>" (`MapViewModel.cs:955-963`) and drawn
  in that projector's view with its corners (`ProjectorCanvas.cs:161-183`). It can be selected again by a Select-tool click in
  the projector view (`ProjectorCanvas.cs:429-433`), from the outliner, or by right-clicking it (`ProjectorCanvas.cs:592-600`).
- Nudging. While a quad is selected, Tab and Shift+Tab step its corner. The arrows move it 1 wall px, Shift+arrow 10 px and
  Alt+arrow 0.1 px, as `SURFACE CORNER <sf> <c> BY dx dy PX` (`src/PlayMap.App/ViewModels/ShortcutMap.cs:72-86`,
  `MapViewModel.cs:615-634`). These keys work only while the canvas or nothing has the focus
  (`src/PlayMap.App/Views/MainWindow.axaml.cs:72-73`). The selected corner is drawn large (`ProjectorCanvas.cs:176-181`), and
  the wall shows it through `HANDLE SELECT` (`MapViewModel.cs:612,622`).

**What is partial (CONFIRMED unless marked).**
- **A half-drawn quad does not survive leaving it.** The clicks are dropped when the tool changes (`MapViewModel.cs:174-175`),
  when another projector is shown (`:349-350`, `:381-382`), when the desk leaves MAP for PLAY or SCAN
  (`src/PlayMap.App/ViewModels/MainViewModel.Map.cs:22-30` → `MapViewModel.cs:483-489`), and at Esc (`MainViewModel.cs:141`).
  A user who clicks away mid-quad starts again.
- **The tool stays Q after Enter.** `FinishQuadAsync` does not return to Select (`MapViewModel.cs:588-613`). The next click on
  the quad starts a new quad instead of selecting it (`ProjectorCanvas.cs:379-381`). The user must press S to pick a quad.
- **The guided path cannot be reopened.** The first-light guide's × closes it for good on this desk (`_dismissed`,
  `src/PlayMap.App/ViewModels/Map/FirstLightGuide.cs:38-42,70`). It shows only on the first-light path (`:11-16,70`). Nothing
  in a menu brings it back.
- **The corner resets on reselect, and the wall is not told.** Reselecting a quad sets the corner to TL (`MapViewModel.cs:327`)
  but sends no `HANDLE SELECT`; only Enter and Tab send it (`:612,622`). Deselecting sends no `HANDLE SELECT NONE` either: the
  desk never sends it (grep finds it only in `FakeEngineLink.Map.cs:482`). The wall can enlarge a different corner, or a quad
  no longer selected.
- **The view does not follow an outliner selection.** Picking a quad in the outliner does not switch the view to its projector;
  only projectors and outputs set the view (`MapViewModel.cs:328-331`). The arrows move a quad the view may not show.
- **Corners are not typed.** `SurfaceInspector` has no corner fields. It shows mesh-era fields that a quad does not use:
  "Surface on " with an empty mesh name (`src/PlayMap.App/ViewModels/MapInspectors.cs:267`), Position (`:290`) and Mapping
  (`:277`). Typing a corner goes over the wire (`SURFACE CORNER … TO x y`, `VerbCatalog.cs:498`).
- **Corners are not dragged.** No mouse drag exists for quad corners. Drag targets are only warp corners and nodes, pins and
  mask nodes (`ProjectorCanvas.cs:376-406,449-490`).
- **Quad lattices are wire-only.** `SURFACE LATTICE` and `SURFACE NODE` are wire-only (reach list, `VerbCatalog.cs:499-500`).
  `HANDLE NUDGE` and `HANDLE NEXT|PREV` appear only on the edit-overlay card (`tests/fixtures/state/wall.cards.json`).
- **Duplicate does not offset a quad.** Duplicate sends a copy moved 0.3 m in 3D (`MapViewModel.cs:977-979`), but a quad's
  `Transform` is unused (`StageModel.cs:157`), so the copy lands on top of the original (PLAUSIBLE).

**Pins and warp corners cannot be moved by one pixel.**
- **Pins move only by mouse drag.** `PinRow.Pixel` is read-only (`MapInspectors.cs:701-705`), the arrows act only on a selected
  quad (`MapViewModel.cs:634`), and the drag is `ProjectorCanvas.cs:388-397,512-518`.
- **Output warp corners and lattice nodes move only by drag** (`ProjectorCanvas.cs:385-386,503-511`). There is no typed field
  and no arrow nudge.
- **The projector view has no zoom.** There is no wheel or zoom handler in `ProjectorCanvas.cs`. The MAP centre column is what
  is left after 290 + 360 px side columns (`src/PlayMap.App/Views/MapView.axaml:9`). At the default 1600 px window
  (`MainWindow.axaml:11`) a 1920 px raster draws at about 2 raster px per screen px, so a one-pixel mouse move is not
  reachable (PLAUSIBLE, from the layout).

**What a user sees and clicks today.** MAP ▸ press Q ▸ click four corners in the projector view (each also shows on the wall
when On the wall is on) ▸ Enter. The quad is selected with TL large, the arrows move it 1 px, Tab steps to the next corner.
After clicking elsewhere the user returns by pressing S, then clicking the quad, or by clicking its outliner row. The corner
starts again at TL.

## (b) The media on each screen

**What exists.**
- **Surfaces show their source.** The outliner row reads "quad on P1 · <media name>" (`MapViewModel.cs:962-963,1103-1110`). The
  inspector has a Content kind and item chooser (`MapInspectors.cs:49-126,270-276`). The surface right-click has Content ▸
  (`MapViewModel.cs:986-997`).
- **PLAY output cards show the picture.** Each card is a live preview of the output's ring (`src/PlayMap.App/Views/PlayView.axaml:82-97`)
  with "1920×1080 · Canvas · 60 fps · <pattern>" (`src/PlayMap.App/ViewModels/PlayViewModel.cs:521,531-532`).

**What is missing (CONFIRMED).**
- **No output row, tile or card names what it shows.** The MAP output row is "mode · projector · display"
  (`MapViewModel.cs:1074`). The plan tile is "w×h · mode · display" (`src/PlayMap.App/ViewModels/DrawerViewModels.cs:311`). The
  PLAY card is as above.
- **STATE `outputs[]` has no source key** (`src/PlayMap.Engine/StateWriter.cs:319-351`). The source lives only in the mirrored
  project (`src/PlayMap.Core/Model/OutputModel.cs:159-162`).
- **A Source-mode output has no source chooser.** The output inspector offers a projector only in Projector mode and a canvas
  only in Canvas mode (`src/PlayMap.App/Views/InspectorView.axaml:263-269`, `MapInspectors.cs:857-870`).
  `OUTPUT SOURCE … media:` for a Source-mode output is wire-only.
- **Nothing joins what is on the air.** The chain is output → canvas → layer → playlist → current item (STATE
  `playlists[].item.source`, `StateWriter.cs:282`). Nothing on the desk joins it, and no playlist or media row says where it
  is shown (`src/PlayMap.App/ViewModels/PlaylistViewModel.cs:247-265`).

**The surface Content ▸ menu is stale, and has no media (PLAUSIBLE, strong).** An outliner node's actions are built once, at
the node's first update (`if (_actions.Count == 0)`, `MapViewModel.cs:964-983`). Content ▸ enumerates the project's playlists,
generators and canvases as they were then (`:986-997`). Nodes are reused by `SyncById` (`:362-370`). A playlist or generator
added later does not appear in an existing surface's Content ▸. The menu has no Media entries; media is chosen only in the
inspector.

## (c) Simple and advanced areas

**What exists.**
- **Three workspaces:** PLAY, MAP and SCAN (`MainWindow.axaml:33-37`).
- **Four drawers:** Outputs, Inputs, Health and Network (`docs/DESK.md:35`).
- **A Simple/Pro toggle:** `MainWindow.axaml:42-45`, Ctrl+Shift+P (`ShortcutMap.cs:52`), and `DeskPrefs.IsPro` / `IsSimple`
  (`src/PlayMap.App/Infrastructure/DeskPrefs.cs:44-57`).
- **Guided areas:**
  - The first-light guide: light a wall → quad → clip (`FirstLightGuide.cs:11-16`, `MapView.axaml:72-87`).
  - The five-step scan wizard (`src/PlayMap.App/ViewModels/ScanWizardViewModel.cs:79-81,107-111`).

**What is partial or missing (CONFIRMED).**
- **Simple hides nothing.** Nothing binds `IsSimple` or `IsPro` beyond the toggle itself (grep: `MainWindow.axaml:42`,
  `MainViewModel.cs:79`, `DeskPrefs.cs:51,57`). This is confirmed by `docs/DESK.md:40` and `docs/ROADMAP.md:212`. The design
  intent is `docs/UX.md:37` (P14).
- **There is no programming or advanced area.** The effects browser and inspector, modulation, tempo, bindings and learn,
  timecode, LED, optimise, live inputs and sends, align, the facade guide and sync are wire-only on the desk, apart from cards
  and generated entries (`docs/DESK.md:53-60`, `docs/ROADMAP.md:22`).
- **The desk types no lines.** "The desk has no line typed by hand" (`docs/ROADMAP.md:22`). There is no console and no Ctrl+K
  palette (`docs/ROADMAP.md:212`).

## (d) The three workflows

### d1. Two projectors on a flat screen: an edge blend or a double stack

**Manual blend: the steps today (CONFIRMED).**
1. Ctrl+Shift+O opens the Outputs drawer. Add output ×2 makes Canvas-mode outputs with no projector
   (`DrawerViewModels.cs:233-240`).
2. Set each output's display in the inspector.
3. Set the Blend grid to 2×1 and the overlap in px, then press "Lay out the blend grid" (`src/PlayMap.App/Views/OutputsDrawer.axaml:26-37`).
   This sends four `SET`s per output, one after another — `sourceRect`, `blend.auto3D` false, `blend.edges` and `plan`
   (`DrawerViewModels.cs:284-297`) — so it is not one edit.
4. Set the curve, gamma and black level in the output inspector (`MapInspectors.cs:941-995`). Edges can be typed as "l, t, r, b"
   (`:982-995`).

**Manual blend: the gap (CONFIRMED).** Keystone or corner-pin for these outputs is not on the desk.
- MAP's Warp tool edits only `ViewOutput`, the output whose `projectorId` is the viewed projector (`MapViewModel.cs:288`,
  `ProjectorCanvas.cs:126-131,385-386`).
- A Canvas-mode output gets no projector chooser (`InspectorView.axaml:263-269`). The view says "No output drives this projector
  yet" (`MapViewModel.cs:240-242`).
- There is no blend overlap preview, no blend-zone visual and no two-projector template.

**Auto (camera).**
- `ALIGN NEW` (`VerbCatalog.cs:474`) and `ALIGN CORNERS` (`:481`) are wire-only.
- Once a group exists, the Alignment card offers `ALIGN RUN`, `ACCEPT`, `REVERT` and `CLEAR`, and the run card offers
  `ACCEPT`, `CANCEL` and `STATUS` (`tests/fixtures/state/align.cards.json`).
- ALIGN fits stacks on a flat screen too: every member fits the same target (`docs/ALIGN.md:51-52,90,99`).

**Double stack.**
- `STACK NEW`, `STACK ALIGN … FROM <scan>` and `STACK MATCH` are wire-only (`docs/ROADMAP.md:22`, `VerbCatalog.cs:467-469`).
- The Stack card offers `STACK LIST`, `FAILOVER` HOLD|MATCH and `REMOVE` (`tests/fixtures/state/facade.cards.json`).

### d2. A 2D film on a 3D wall, undistorted from an eye point

**What exists (CONFIRMED).**
- `SurfaceMapping.Projection`, content projected from a viewpoint (`StageModel.cs:116-124`), is rendered
  (`src/PlayMap.Render/Projection/StageRenderer.cs:467-473`).
- On the desk:
  - Outliner Viewpoints ▸ "Viewpoint from this view" sends the orbit camera's pose and FOV (`MapViewModel.cs:672-673`).
  - The surface inspector has Mapping = Projection and a Viewpoint chooser (`MapInspectors.cs:138-151,178-191`).
- A 3D blend for more projectors: `BLEND SOLVE` (`VerbCatalog.cs:206`, `src/PlayMap.Core/Mapping/BlendSolver3D.cs`) runs from
  the desk card "Solve the blend again" (`src/PlayMap.App/State/Cards/desk.cards.json:16`). Auto3D is a field on the output
  inspector (`MapInspectors.cs:941-949`).

**What is partial.**
- **The viewpoint cannot be edited.** Its inspector has only a name and facts (`MapInspectors.cs:1208-1240`). The eye's
  position, height and FOV cannot be typed. They come only from the orbit camera at the moment of adding it.
- **There is no guided path.** The user needs a mesh (an outliner primitive plane, box or cylinder, `MapViewModel.cs:655-659`, a
  scan, or `SURVEY IMPORT`, which is wire-only), a solved projector (d3), a viewpoint, the mapping set to Projection with that
  viewpoint, and a source. That is five places and no single flow.
- **MPCDI export is Core and tests only.** It has no verb (`src/PlayMap.Core/Mapping/MpcdiExport.cs:125-150`; its only caller is
  `tests/PlayMap.Core.Tests/Mapping/MpcdiExportTests.cs:85`).

### d3. 3D mapping with one or two projectors

**From pins (CONFIRMED).**
1. Add a projector (outliner +, `MapViewModel.cs:663`) or use `LIGHT WALL`.
2. Give its output Projector mode and that projector.
3. Add a primitive surface.
4. Press P, then click a surface in the projector view to add a pin (`ProjectorCanvas.cs:388-397`).
5. Drag each pin onto the real feature. Six pins enable Solve (`MapInspectors.cs:440`). Solve sends `PROJECTOR SOLVE`, also
   offered "Shift locked", "Zoom locked" and "Nothing locked" (`:442-444`), and is in the projector's menu (`MapViewModel.cs:1023`).
6. Read the score on Health ▸ Calibration.

**From a scan.**
1. SCAN wizard: camera → projectors → scan → review → apply (`ScanWizardViewModel.cs:107-111,376,383`).
2. `SCAN SURFACE` from the smart scan (`:126`).
3. `SCAN SUGGEST` and `SCAN SOLVE` appear only as card actions (reach list).

**Wire-only:** `PROJECTOR PRIOR` with a value, `SITE …` and `SURVEY IMPORT` (`docs/ROADMAP.md:22`).

## (e) One pattern, media or generator across several outputs

**What exists (CONFIRMED).**
- Canvases and layers (`src/PlayMap.Core/Model/Content.cs:146-161`).
- A Canvas-mode output shows its `SourceRect` of a canvas (`OutputModel.cs:141-142,159-162`,
  `src/PlayMap.Render/Engine/RenderEngine.cs:438-444`).
- A new project is one canvas with the test card as a layer, shown by one output (`src/PlayMap.Core/Model/Project.cs:79-88`).
- The drawer's blend grid assigns each output its canvas rectangle (`DrawerViewModels.cs:256-281`).
- Surfaces can sample a canvas region (`StageModel.cs:118-119`, `MapInspectors.cs:153-176`).
- `ALIGN NEW … CANVAS` makes one shared canvas (`src/PlayMap.Engine/Align/EngineCore.AlignVerbs.cs:168`).

**What is partial (CONFIRMED).**
- **`PATTERN` does not span outputs.** It is drawn per output at each output's own raster (`ResolvePlain(…, output.Width,
  output.Height)`, `RenderEngine.cs:427-430`). `PATTERN … ON ALL` therefore shows N separate patterns, not one spanning grid.
  Only a pattern generator placed as a canvas layer spans.
- **The desk has no canvas or layer editor.** `ADD canvas` and `ADD layer` are generic wire edits (`VerbCatalog.cs:223`). A
  canvas's size, layers and placement cannot be edited on the desk. A canvas is chosen only in the output and surface
  inspectors.

## (f) Right-click menus

**What has one.**
- Outliner rows of every kind (`MapView.axaml:29`).
- In the projector view: a quad or a surface, and empty space (`ProjectorCanvas.cs:587-605`).
- 3D view objects (`src/PlayMap.App/Controls/MapViewport.cs:273-288`).
- Plan tiles (`src/PlayMap.App/Controls/PlanCanvas.cs:131-141`).
- PLAY: media, playlists, items and output cards (`PlayView.axaml:32,53,83,107,145`).
- The show strip's Ready, Blind, ARM and PANIC (`src/PlayMap.App/Views/Show/ShowStripView.axaml:11-30`) and Blackout
  (`MainWindow.axaml:76`).
- Cards (`src/PlayMap.App/Views/Cards/CardView.axaml:17`), score cards and history dots, and the fleet and preflight boards.
- The inspector toolbar (`InspectorView.axaml:343`).

**What has none:** empty 3D space (`MapViewport.cs:278-279` returns) and empty plan space (`PlanCanvas.cs:129-130`).

**How they are made.** The menus are hand-built per object with catalogue lines. Generated entries (Add effect ▸, Modulate ▸,
Bind ▸, Optimise ▸, Use input ▸) are built from the node's own answers (FX KINDS, MOD SOURCES) and STATE
(`src/PlayMap.App/ViewModels/Generated/ObjectMenusModule.cs:10-16`). They are not generated from the catalogue as a whole. Only
about a third of the catalogue rows appear as a desk line (reach estimate). Rows in neither the desk nor a card include:
- `PROJECT OPEN`; the desk has no project-open picker (a grep finds only `PROJECT GET`, `NEW` and `SAVE`);
- every `DEVICE` verb (power, shutter, lens, input);
- `INPUT ADD`, `SEND ADD`, `TIMECODE`, `LED NEW` and `LED IMPORT`;
- `STACK NEW`, `ALIGN`, `ALIGN NEW` and `ALIGN CORNERS`;
- `SURFACE LATTICE` and `SURFACE NODE`;
- `CREDENTIAL`.

## (g) Panels

**Fixed today (CONFIRMED).**
- MAP is a fixed grid of 290 | * | 360 (`MapView.axaml:9`) and PLAY of 300 | * | 340 (`PlayView.axaml:8`).
- The project has no GridSplitter, no docking library and no Expander but the card's "more" (`CardView.axaml:26`). Its
  packages are Avalonia, Desktop, Fluent and Inter only (`src/PlayMap.App/PlayMap.App.csproj:15-20`).
- One drawer opens at a time, at a fixed width (660, 560, 600, 560: `src/PlayMap.App/ViewModels/DrawerHostViewModel.cs:84-87`).
  ⤢ makes it take the whole workspace (`src/PlayMap.App/Views/DrawerHost.axaml:24`).
- No layout is persisted. `desk.json` holds only Pro and Esc-is-panic (`DeskPrefs.cs:6-8,44-59`).

**Not draggable, not dockable, not collapsible.** Round 3's `wp3-desk-ux` adds none of this (`docs/round3/PLAN.md:684-742`).

## (h) Colour

**The palette.**
- Dark neutrals, one blue accent `#3D9BFF`, danger red, and status lights in green, amber, red and grey
  (`src/PlayMap.App/Theme/Tokens.axaml:3-21`).
- Projector colours come from the model, alternating `#FFB74D` and `#4FC3F7` (`MapViewModel.cs:685`). The surface default is
  `#CE93D8` (`:715`).
- Pins are coloured by residual: good under 1 px, fair under 3 px (`MapInspectors.cs:731`, `ProjectorCanvas.cs:337`).
- The mode frame is red while armed and amber while anything but the show is on a wall (`MainWindow.axaml:202-204`).

**No colour for workspaces or stages.** PLAY, MAP and SCAN share one accent. The blind frame's violet is not built
(`docs/DESK.md:42`). Round 3 plans the blind grey variant and a shape and a word on every light (`docs/round3/PLAN.md:694-704`),
not a colour per workflow stage.

## (i) Video capture cards

**The inputs.**
- OMT (vendored) and NDI (runtime loaded) receivers, and Spout on Windows (`docs/VIDEO.md:3-6`).
- Anything else that parses as an FFmpeg URL or device path (`/dev/…`, `video=…`) goes to `CaptureSource` through libavdevice
  (`src/PlayMap.Core/Media/LiveVideo.cs:9-46,131-133`, `src/PlayMap.Engine/EngineCore.VideoIo.cs:90-91`,
  `src/PlayMap.Media/MediaLibrary.cs:196`).
- `LiveProtocol.DeckLink` is an enum value only (`LiveVideo.cs:7`). Capture cards are "next round" (`docs/VIDEO.md:190-193`);
  DeckLink is roadmap item M13 (`docs/ROADMAP.md:251`). There is no AJA or Magewell SDK in `src`.

**The pass-through path.**
- `CaptureSource` reads newest-wins with the buffers off, stamping each frame's `ArrivalNs` (`src/PlayMap.Media/Sources/CaptureSource.cs:28-33`).
- NDI and OMT copy into a `LiveFramePool`, then the GPU unpacks (`docs/VIDEO.md:24-33`).

**Latency is not measured or published for inputs.**
- `ArrivalNs` is read only by scan and align (`src/PlayMap.Engine/Scanning/ScanRunner.cs:915`,
  `src/PlayMap.Engine/Align/DisplayShooter.cs:51`).
- **Stream items probably do not open on Windows (PLAUSIBLE).** They open with `Format = ""`, which probes
  (`MediaLibrary.cs:196`, `CaptureSource.cs:14-15,168-169`). DirectShow has no probe, so a `video=…` capture card added by
  `INPUT ADD` likely does not open on Windows. Not run.
- **Round 3 uses capture cards to observe outputs, not to pass video through:** `OBSERVE`, and `LATENCY STEP` glass-to-glass p50
  and p95 (`docs/round3/PLAN.md:350-352,888-905`). NDI and OMT uploads move to the copy queue (`:264,1050`).

## (j) Server and database use

There is no database: no SQLite or LiteDB anywhere in `src` (grep). The node keeps files:
- the project as JSON, written to a temp file and moved into place (`src/PlayMap.Engine/EngineCore.Edits.cs:663-679`,
  `src/PlayMap.Core/Storage/DurableFile.cs:19-31`);
- `playmap.node.json` settings, `playmap.recovery.json`, `playmap.journal.jsonl`, `media/` named by sha256, `keys/` and `models/`
  (`src/PlayMap.Engine/NodeHome.cs:39-44`, `src/PlayMap.Engine/Host/MediaBackend.cs:415`);
- the optimise queue `jobs.json`, written whole through a temp file and a rename (`src/PlayMap.Engine/Optimise/JobStore.cs:46-48`);
- credentials under DPAPI, or a mode-600 file elsewhere (`src/PlayMap.Engine/Devices/NodeCredentialStore.cs:3-14`).

`ProjectStore` is the single writer, publishing immutable snapshots (`src/PlayMap.Core/Model/ProjectStore.cs:18-30`). The HTTP
server serves `/api/state`, `/api/cmd`, `/api/context`, `/api/project`, `/api/media`, `/api/proposals`, `/api/thumb` and
`/api/verbs` (from `src/PlayMap.Net`).

**The journal drops its oldest rows silently under a burst (PLAUSIBLE).** It is a bounded channel of 4096 with `DropOldest`,
and nothing counts the drops (`src/PlayMap.Engine/Journal.cs:17-21,48`).

## The answers that touch this survey

- **7, Keep.** The desk already sends VIDEO STOP, PANIC CLEAR and MASTER at once (`docs/round2/CARRYOVER.md:21-22`,
  `docs/DESK.md:11-12`).
- **8, Approve.** A card's APPROVE is already sent on one press (`docs/round2/CARRYOVER.md:23`, `docs/DESK.md:43-44`).
- **11, 128 commands.** Patterns keeps 64 (Patterns' `src/Patterns.Core/Services/ShowContext.cs:99`). PlayMap's
  own reader of a peer's context also keeps ≤ 64 (`src/PlayMap.Core/Bus/ShowContext.cs:175,190`). No 128 limit was found in
  PlayMap's code. PlayMap writes every mutating verb (`ShowContext.cs:79-85`).
