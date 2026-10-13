# Research: building facades and projector stacking

*13 October 2026. Every fact carries its URL and the date the page gives (n.d. = the page shows none). **Verified** = the
page was read this session through Exa (full fetch, or the search highlights extracted from that page — marked "hl").
**Unverified** = from memory, a secondary source, or a page that could not be opened. **Ours** = computed in this
container (scripts under `r2/bench/facade/`).*

## 1. What a facade job is, end to end (practitioners)

| Fact | Source | Status |
|---|---|---|
| Architectural mapping: 100–5,000 m² and more, 20,000–40,000+ lm projectors, 4–30+ projectors, outdoors at night, audience 20–200 m away. Arc de Triomphe: 15+ high-brightness projectors, "several days of on-site work" to calibrate | [Jazé, Complete video mapping guide](https://www.baptistejaze.com/en/blog/complete-video-mapping-guide), 2025-08-01 | Verified (fetch) |
| "A projector that moves by 2 mm causes an offset of several centimeters on the surface"; facade mapping 2–5 days install and calibration for 4–8 projectors | same | Verified (hl) |
| A sculpted facade costs "two to three nights" of manual warping; 2D (single-camera) autocalibration "fails the moment the surface has real relief"; both 2D and 3D autocal fail "when ambient light drowns the patterns"; 3D autocal needs an accurate model — "A model that is 20 cm off will calibrate confidently to the wrong surface" | [Jazé, Projector autocalibration: 2D and 3D, in the field](https://www.baptistejaze.com/en/projector-calibration/projector-autocalibration), 2026-07-09 | Verified (hl) |
| Modulo Kinetic 3D autocalibration was shown at ISE 2025 on an Arc de Triomphe model with PoE cameras; Modulo Player 2D autocal capped at 6 outputs per server | same (second-hand report of Modulo Pi) | Unverified (not read at Modulo Pi) |
| Preparation: 2D workflow (photos from each projector position, flat facade) vs 3D workflow (scan → low-poly retopology → UV unwrap → projection studies → on-site calibration); retopology because "the raw scan contains millions of polygons. Impossible to use in real time" | [Jazé, 2D vs 3D workflow](https://www.baptistejaze.com/en/blog/mapping-preparation-workflow), 2025-11-21 | Verified (hl) |
| disguise: "For building projections, it is recommended to laser scan the building. A laser scan can generate a highly accurate 3D mesh (+-5mm accuracy)"; the mesh is optimised, UV-mapped and exported as a textured OBJ | [disguise, Projector Simulation Overview](https://help.disguise.one/designer/stage-setup/projector-simulation/projector-simulation-overview), n.d. | Verified (hl) |
| disguise QuickCal: about 10–15 reference points per projector; 4 points solve pose only (IterLM, lens shift and throw ratio taken from the projector's settings); 6+ points also solve lens shift and throw ratio (Zhang); points must span depth and all four image corners; "the volume in which the projector is valid in is always limited by the extent of Reference Points"; QuickCal does not correct lens distortion (only OmniCal does) | [disguise, Quick Calibration](https://help.disguise.one/workflows/calibration-projection/quickcal-overview), n.d. | Verified (hl) |
| disguise OmniCal: a score below 1 px is "good"; each blob must be seen by ≥2 cameras; cameras ≥10° apart; a flat scene needs the Homography model or a temporary object for depth; capture "typically takes less than a minute"; a **Black Level** capture of all projectors outputting black is taken first ("fairly dark, but still allow recognition of important scene elements"); RigCheck measures blob movement in pixels to say whether a camera or surface moved; Alignment needs ≥3 points lined up in ≥2 cameras; Reshape moves mesh vertices without touching UVs | [OmniCal Overview](https://help.disguise.one/workflows/calibration-projection/omnical-overview), [Capture](https://help.disguise.one/workflows/calibration-projection/omnical-capture), [Calibration](https://help.disguise.one/workflows/calibration-projection/omnical-calibration), [Alignment](https://help.disguise.one/workflows/calibration-projection/omnical-alignment), n.d. | Verified (hl) |
| OmniCal hardware: 5 MP Allied Vision GigE cameras, global shutter (Sony Pregius), 6/8/12 mm C-mount lenses, PoE, up to 100 m Cat 6 | [STEMMER IMAGING, disguise application](https://www.stemmer-imaging.com/en/solutions/applications/entertainment), n.d. | Verified (hl) |
| VIOSO 3D models: mm units, X right, Y up, Z backward; origin "at a logical reference point"; single-sided faces, normals toward the viewer, only the projection surface in the model | [VIOSO, Custom 3D model preparation](https://docs.vioso.com/guide/custom-3d-model-preparation-for-calibrations), n.d. | Verified (hl) |
| VIOSO 3D alignment: the virtual camera's position is measured and entered (mm from the model origin), rotation estimated, then refined by eye on the camera image | [VIOSO, 3D Mapping](https://docs.vioso.com/calibration/content-mapping/3d-mapping), n.d. | Verified (hl) |
| Battersea Power Station (LEGO, Jan 2025): laser-scanned, 3D template in Cinema 4D, Mapping Matter + disguise for previz; 12 × PT-RQ35 and 10 × PT-RQ25 (4K 3-chip DLP); eight outputs each **tripled** (chimneys doubled); dark red brick so brightness was critical; towers ~30 m closer than usual; full failover server; **two Panasonic AW-UE100 4K PTZ cameras zoomed on projection edges and stacked areas to check alignment from a distance** | [Panasonic case study](https://eu.connect.panasonic.com/gb/en/case-studies/panasonic-transforms-iconic-battersea-power-station-factory-creativity-3d-lego), 2025-01-20 | Verified (hl) |
| Tokyo Metropolitan Government building (Feb 2024, permanent): 40 projectors (20 × PT-RQ50K 50,000 lm, 20 × PT-RZ34K 30,500 lm) on 13,904.956 m²; site and building surveyed to CAD, projectors on the roof; fully remote operation with daily camera monitoring | [Panasonic case study PDF](https://docs.connect.panasonic.com/projector/casestudies/events015/pdf/E_Report_tokyoprojectionmapping.pdf), n.d. | Verified (hl) |
| Takanawa (Feb 2026): 36 × 7 m curved, three-layer facade with shop lighting; Panasonic "automatic calibration" + masking; tuned for several viewing angles | [Panasonic case study PDF](https://docs.connect.panasonic.com/projector/casestudies/entertainment016/pdf/E_Report_TAKANAWA.pdf), 2026 | Verified (hl) |
| DATALAND (opened 20 June 2026): 84 double-stacked 4K Epson 20,000 lm projectors; 9 permanent GigE cameras; "each projector in a stack as a fully independent channel within the global solve. Both are warped to the same measured target geometry"; colour and luminance matched; mirrors produce "ghost detections … confidently wrong"; region masking excludes mirrors from solve **and** output; "explicit surface ownership"; one-click recalibration by museum staff | [Scalable, DATALAND](https://www.scalabledisplay.com/company/news/powering-refik-anadol-dataland-84-projector-calibration/), 2026-07-10 | Verified (hl) |
| A long-range outdoor projection system: checkerboards at the surface's distance occupy too little of the image to calibrate distortion; projected patterns are used instead; windows and light fixtures with complex reflectance cause colour distortions | [Poullis et al., VISIGRAPP 2017](https://poullis.org/assets/pubs/POULLIS_VISIGRAP2017.pdf), 2017 | Verified (hl) |

## 2. Survey sources and their accuracy

| Fact | Source | Status |
|---|---|---|
| Facade distances vs control points: Leica ScanStation C10 TLS RMS 0.0205 m; iPhone 13 Pro LiDAR 0.1694 m; GeoMax Zoom total station 0.0135 m | [Shevchenko et al., EAGE 2023](https://www.earthdoc.org/content/papers/10.3997/2214-4609.2023510102), 2023 | Verified (hl) |
| Apple LiDAR: dToF SPAD, 576 points, **5 m maximum range**; iOS apps on medium indoor/outdoor areas show errors from ~1 cm to ~10 cm; loop closure matters | [ISPRS Archives XLVIII-2-W8-2024](https://isprs-archives.copernicus.org/articles/XLVIII-2-W8-2024/431/2024/isprs-archives-XLVIII-2-W8-2024-431-2024.pdf), 2024 | Verified (hl) |
| iPhone 12 Pro Max LiDAR on a facade: 4.89 mm RMSE vs TLS 3.44 mm inside 5 m; error grows with range (2.0 mm at 0.25 m → 5.2 mm at 5 m) | [ResearchGate preprint](https://www.researchgate.net/publication/378351112_Evaluating_the_Accuracy_of_iPhone_Lidar_Sensor_for_Building_Facades_Conservation), 2024 | Verified (hl) |
| UAV (DJI Mini 3 Pro) photogrammetry vs TLS on a building, 23 features vs total station: RMSE 0.120 m UAV, 0.070 m TLS; UAV captures roofs and upper parts, TLS the base; hybrid recommended | [ISPRS Annals X-5-W4-2025](https://isprs-annals.copernicus.org/articles/X-5-W4-2025/143/2026/isprs-annals-X-5-W4-2025-143-2026.pdf), 2026 | Verified (hl) |
| RealityScan 2.0 (the renamed RealityCapture) released 17 June 2025; free under USD 1 M annual gross revenue, otherwise USD 1,250 per seat per year; imports aerial LiDAR; reads .las, .laz, .e57, .xyz, .csv, .pts; Windows, CUDA GPU required | [RealityScan news](https://www.realityscan.com/news/realityscan-20-new-release-brings-powerful-new-features-to-a-rebranded-realitycapture), 2025-06-17; [licence](https://www.realityscan.com/license); [CG Channel](https://www.cgchannel.com/2025/06/epic-games-releases-realityscan-2-0-and-realityscan-mobile-1-7/), 2025-06-18 | Verified (hl) |
| RealityScan Mobile 1.7 is free including commercial use (Android 7+, iOS 16+) | CG Channel, above | Verified (hl) |
| Metashape, Pix4D, Polycam export options (local shift on export, georeferenced OBJ) | — | Not researched this pass (unverified) |

## 3. Huge coordinates and georeferencing

| Fact | Source | Status |
|---|---|---|
| CloudCompare warns above ~10⁵ and offers a **Global Shift** (and scale): P_local = (P_global + T)·S; float32 relative precision 10⁻⁷–10⁻⁸; georeferenced coordinates (~10⁶) give "1 or 10 cm" precision; the shift is kept as metadata and restored on export when the format stores 64-bit values; by default the first point is the shift | [CloudCompare wiki, Global Shift and Scale](https://www.cloudcompare.org/doc/wiki/index.php/Global_Shift_and_Scale), n.d. | Verified (fetch) |
| float32 spacing: 6.1e-5 m at 1,000; 0.0078 m at 100,000; 0.0625 m at 600,000 (a UTM easting); **0.5 m at 5,700,000** (a UTM northing at 51° N) | numpy `spacing(float32)` | Ours |
| Earth curvature drop d²/2R: 0.8 mm at 100 m, 1.8 mm at 150 m, 78 mm at 1 km — a flat local frame is exact enough for one building | computed, R = 6,371 km | Ours |
| UTM point scale (spherical approximation) 0.9996 on the central meridian, 1.00009 at 200 km, 1.00071 at 300 km; elevation factor 0.99998 at 100 m, 0.99984 at 1,000 m — a grid-vs-ground scale error of up to ~400 ppm (4 cm over 100 m) | computed | Ours |
| PlayMap today: `MeshIO.ReadObj` parses doubles and **narrows to float before anything else** (`(float)p.X`); `MeshData` is float; LAS is read into doubles (`Vec3d`) with the header scale/offset but not rebased; `StageMeshes` loads a mesh file as stored | `src/PlayMap.Core/Geometry/MeshIO.Obj.cs:78`, `MeshIO.Points.cs:75`, `src/PlayMap.Engine/Rendering/StageMeshes.cs:117` | Ours (read on main) |
| PlayMap today: `ScanRunner.StageModel` bridges a scan into the **first imported mesh's raw file coordinates**, ignoring the surface's `Transform3` | `src/PlayMap.Engine/Scanning/ScanRunner.cs` (StageModel) | Ours (read on main) |

## 4. Formats

| Fact | Source | Status |
|---|---|---|
| libE57Format 3.4.1 released 2026-09-20 (XML parser hardened against external entities); 3.4.0 on 2026-08-21 added the Leica DIST camera-distortion extension and a check that the file size is a multiple of 1024 | [CHANGELOG](https://raw.githubusercontent.com/asmaloney/libE57Format/master/CHANGELOG.md) | Verified (fetch) |
| libE57Format licence: Boost Software License 1.0 | [LICENSE.md](https://raw.githubusercontent.com/asmaloney/libE57Format/master/LICENSE.md) | Verified (fetch) |
| E57 physical pages are 1024 bytes, 1020 logical + a 4-byte checksum, CRC-32C (polynomial 0x1EDC6F41) stored byte-swapped | `src/CheckedFile.h` lines 46–49, `src/CheckedFile.cpp` lines 114–127 of libE57Format master | Verified (fetch) |
| E57 node types include ScaledInteger (raw, minimum, maximum, scale, offset); points live in CompressedVector binary sections (bit-packed byte streams per field) | `include/E57Format.h` of libE57Format | Verified (fetch, headers only) |
| LASzip (the LAZ codec) is Apache-2.0; C API in `dll/laszip_api.h`: `laszip_create`, `laszip_open_reader`, `laszip_get_header_pointer`, `laszip_get_point_pointer`, `laszip_read_point`, `laszip_seek_point`, `laszip_close_reader`, `laszip_destroy`, `laszip_get_version`, `laszip_request_compatibility_mode` | [LASzip README](https://raw.githubusercontent.com/LASzip/LASzip/master/README.md), [COPYING.txt](https://raw.githubusercontent.com/LASzip/LASzip/master/COPYING.txt), [laszip_api.h](https://raw.githubusercontent.com/LASzip/LASzip/master/dll/laszip_api.h) | Verified (fetch) |
| laz-perf (Hobu) is Apache-2.0 | [COPYING](https://raw.githubusercontent.com/hobuinc/laz-perf/master/COPYING) | Verified (fetch) |
| LAStools is part LGPL-2.1, part closed — not usable | [LICENSE.txt mirror](https://raw.githubusercontent.com/m-schuetz/LAStools/master/LICENSE.txt) | Verified (fetch) |
| ProjNet 2.1.0 (NuGet, published 2025-08-18) is **LGPL-2.1-or-later** — excluded; laszip.net 2.2.1 (a port of LASzip 2.x) declares no licence in its nuspec — excluded | NuGet flat container / registration, fetched 2026-10-13 | Verified |
| PROJ is MIT-licensed (could be loaded at run time from the user's install) | — | Unverified this pass |
| LAS 1.4 keeps the CRS in a VLR: user "LASF_Projection", record 2112 (OGC WKT) or GeoKeys 34735 | ASPRS LAS 1.4 R15 (from memory) | Unverified |

## 5. Brightness budgets

| Fact | Source | Status |
|---|---|---|
| lux = lumens ÷ m²; required lumens = target lux × area ÷ realistic coefficient (0.60–0.70: ageing, dust, non-white content); light surfaces reflect 80–90 %, dark brick 20–30 % (2–3× more lux needed); targets outdoors at night: low light pollution 60 / 100 / 150+ lux (minimum / comfortable / optimal), urban 100 / 180 / 300+, twilight 200 / 400 / 600+; "a site that looks dark … can actually have 10–20 lux of ambient light" | [Jazé, Lumens vs lux](https://www.baptistejaze.com/en/blog/lumens-vs-lux-projection), 2025-10-11 | Verified (hl) |
| Doubling throw distance with the same lens quarters the lux | [Jazé, Facade budget](https://www.baptistejaze.com/en/blog/facade-projection-budget), 2026-01-11 | Verified (hl) |
| Outdoor night: aim for at least 60–80 lux with low light pollution, 100–150+ in brighter places | [Jazé, Projection calculator guide](https://www.baptistejaze.com/en/blog/projection-calculator-guide), 2025-12-01 | Verified (hl) |
| UK guide: 10,000–20,000 lm covers ~100–200 m² of clean dark masonry; "assume 15–25 lux of environmental light at the facade"; surface loss: lime render 10–20 %, dressed ashlar 25–40 %, rubble/deep brick 40–60 %, dark brick or slate 60–75 %; untreated glazing "patchy at night" | [Gilmour, Architectural projection art](https://graemegilmour.co.uk/architectural-projection-art), 2026-04-20 | Verified (hl) |
| HeavyM rules: dark rural 50–80 lm/m², urban with streetlights 100–150 lm/m²; also "required lumens = 3 × ambient lux × area" | [HeavyM outdoor](https://www.heavym.net/outdoor-projection-mapping-projector-how-to-choose-the-right-one-for-buildings-and-large-events/), 2026-07-17; [HeavyM throw ratio guide](https://www.heavym.net/projector-guide-throw-ratio-specs/), 2026-07-16 | Verified (hl) |
| Worked example: one 30,000 lm 4K projector at 100 m with TR 3.0 lights 33.3 × 18.75 m = 625 m² → 48 lux (34 lux after a 0.7 derate); double stack 67 lux, triple 101 lux; on brick (ρ = 0.35) the triple stack is ~11 cd/m² | computed | Ours |

## 6. Night capture at distance

| Fact | Source | Status |
|---|---|---|
| Epson Geometry Correction Assist (built-in camera) wants a darkened room, the projector warmed up 20 min, a matte white flat screen of gain 0.9–1.1, ≤40 lux on the screen, a white:black illuminance ratio ≥8:1, 100–500 inch images — i.e. it is not a facade tool | [Epson, Stacking with Geometry Correction Assist](https://files.support.epson.com/docid/cpd5/cpd59255/source/adjustments/concepts/multi_projector_eppt_stacking_assist.html), n.d. | Verified (fetch) |
| Camera geometry at 100 m: a 50 mm lens on a 6000 px APS-C sensor (3.9 µm) has f ≈ 12,800 px, 7.8 mm per pixel, a 47 m footprint; a 4K projector at TR 3 puts 8.7 mm pixels on the wall — about one projector pixel per camera pixel, so single-pixel Gray stripes are at the camera's limit and the phase-shift (Robust) mode is the right one | computed | Ours |
| Exposure for a 48 lux, ρ = 0.4 wall (L ≈ 6 cd/m²) at f/4, ISO 400: t = N²K/(L·S) ≈ 0.08 s | computed (K = 12.5) | Ours |
| Flicker-safe exposure: a multiple of LCM(projector frame period, half the mains period): 60 Hz projector with 50 Hz mains → LCM(16.7 ms, 10 ms) = 50 ms; with 60 Hz mains → 16.7 ms | computed | Ours |
| Projector–camera triangulation at 100 m with a 10 m baseline and 0.3 px matching: σz ≈ z²σ/(b·f) ≈ 2.3 cm — enough to see a model that is 20 cm off | computed | Ours |
| The decoder already compares each Gray pattern with its inverse (ambient cancels) and gates on white − black contrast | `src/PlayMap.Core/Scan/StructuredLightDecoder.cs` | Ours (read) |
| The scan runner takes a shutter-closed black frame per projector but only reports its mean brightness | `src/PlayMap.Engine/Scanning/ScanRunner.cs` PrepareDevicesAsync | Ours (read) |

## 7. Long-throw solves (ours, first-order covariance)

`r2/bench/facade/longthrow_table.py` (numpy): 4K 3840 × 2160 projector, pins with σ = 1 px, dense structured-light pairs with
σ = 0.3 px, pinhole model, square pixels; "image σ" is the worst 1-σ position error over a 7 × 5 grid on the facade and the same
grid 3 m in front of it.

| Case | σ focal | σ centre along axis | image σ on facade | image σ 3 m proud |
|---|---|---|---|---|
| 100 m, TR 3, 12 pins, flat, head-on, shift locked | unbounded | unbounded | unbounded | unbounded |
| same + 2 % focal prior | 2.00 % | 2.00 m | 2.34 px | 3.59 px |
| same, focal locked | — | 0.03 m | 2.14 px | 3.37 px |
| 100 m, TR 3, 12 pins, 2 m relief, head-on, shift locked | 3.71 % | 3.67 m | 1.46 px | 1.75 px |
| same, focal locked | — | 0.03 m | 1.20 px | 1.16 px |
| 100 m, TR 3, 12 pins, 2 m relief, 25° oblique | 0.92 % | 0.79 m | 1.35 px | 2.28 px |
| 100 m, TR 3, **20,000 dense pairs**, 2 m relief, shift free | 0.03 % | 0.03 m | 0.01 px | 0.02 px |
| 100 m, TR 3, 20,000 dense pairs, flat, head-on | unbounded | unbounded | unbounded | unbounded |
| 150 m, TR 5, 12 pins, 2 m relief | 8.70 % | 12.98 m | 1.41 px | 2.42 px |
| 150 m, TR 5, 12 pins, 2 m relief, focal locked | — | 0.04 m | 1.32 px | 1.79 px |

Readings: (1) a flat facade seen head-on cannot separate zoom from distance at all — no number of pins helps; only a lens
prior, a position prior or an oblique view does. (2) With relief, 12 pins put the projector metres off along its axis while the
picture on the wall is still within ~1.5 px — the pose is wrong but the picture is fine, which is what a score must say in
those words. (3) Dense pairs from a night scan bridged into the model shrink every number by two orders of magnitude.

## 8. Stacking

| Fact | Source | Status |
|---|---|---|
| Christie Mystique Essentials: single camera, flat screens, up to 12 projectors as a 2 × 3 array double-stacked, under 2 min per projector, works with Christie Twist on Christie 3DLP/1DLP or Pandoras Box 6+; Guardian (option) "constantly monitors … and automatically calibrates the image in real-time … even when content is playing" | [Mystique Essentials](https://www.christiedigital.com/products/warping-blending/mystique/mystique-essentials-edition/), n.d. | Verified (hl) |
| Christie: "The results will be limited by the lowest-performing projector in the array"; Mystique Lite does no stacking | [Mystique family](https://www.christiedigital.com/products/warping-blending/mystique/), n.d. | Verified (hl) |
| Christie AutoStack: 2 × 3 blended array stackable to 12 projectors; Premium: more than 12, curved up to ±70° | [AutoStack datasheet](https://www.christiedigital.com/globalassets/resources/public/christie-autostack-datasheet.pdf), n.d. | Verified (hl) |
| Christie Mystique Cinema: "sub-pixel alignment for dual projection systems", with motorised lens mount and auto-focus | [Mystique Cinema](https://www.christiedigital.com/en-gb/products/cinema/cinema-accessories/mystique-cinema/), n.d. | Verified (hl) |
| Christie patent US 9,872,003 (2018): stacked images warped to a **virtual target image** in a common (camera) space; the target can be one projector's image or anywhere between, set by a 0–1 parameter | [patent summary](https://exa.ai/library/legal/patent/lr3sl011yd0), 2018-01-16 | Verified (hl) |
| Christie Projector Blending Tool 3: blending, stacking or both, rows and columns 1–6, stacked units listed vertically; Logitech C910/C920/C922/C930 or Canon cameras; warp maps sent to the projector | [User manual 020-103144-01 Rev. 1](https://res.cloudinary.com/iwh/image/upload/q_auto,g_center/assets/1/26/Christie_LHD878-DS_Blending_Tool_3_Technical_Reference.pdf), 2019-07 | Verified (hl) |
| Epson Projector Professional Tool ≥1.31, Geometry Correction Assist: stack up to **6** projectors with the built-in camera, same panel resolution, up to 10 min, conditions in §6 | Epson page, §6 | Verified (fetch) |
| Panasonic ET-CUK10: up to 99 projectors (4 H × 4 V × **6 stacked** per camera), up to 6 cameras, Nikon D5200–D5600 with listed Nikkor zooms (VR off); adjusts geometry, edge blend, curved screen, stacking, colour and brightness, white balance, black level | [ET-CUK10 specifications](https://docs.connect.panasonic.com/projector/products/geometry/cuk10_spec/), n.d. | Verified (hl) |
| Panasonic: Geometry Manager Pro is folded into the free Visual Software Suite; **ET-UK20 and ET-CUK10 are discontinued in Q2 CY2026**; Free Grid on the projector: 2 × 2 to 17 × 17 grids by remote | [Geometry Manager Functions](https://docs.connect.panasonic.com/projector/products/geometry), n.d. | Verified (fetch) |
| VIOSO stacking: calibrate each stack level separately (one display compound per level), then "Stack compound calibrations" with "stacking mode" | [VIOSO helpdesk](https://helpdesk.vioso.com/knowledgebase/how-to-set-up-a-camera-based-calibration-for-a-stacked-projection-with-wings-vioso/), n.d. (legacy) | Verified (hl) |
| Scalable (DATALAND): sub-pixel stack registration "re-achieved from scratch every time"; "Miss by half a pixel and the stack reads as soft" | Scalable, §1 | Verified (hl) |
| What a vendor does when one projector of a stack fails | — | **No source found**: none of the pages above define it |
| Barco stacking tools | — | Not researched (unverified) |

## Design implications for PlayMap

1. **A site frame with a double-precision origin is the first fix, not a feature.** Readers must subtract the origin in double
   before narrowing to float (today OBJ narrows first: 0.5 m steps at a UTM northing). Store the CRS words and the origin with the
   project, show them, restore them on export (CloudCompare's model). Curvature is negligible below 1 km; a flat ENU-like frame
   (x east, y up, z south — the existing `ZUpToYUp` convention) is right.
2. **Do not reproject unless both sides need it.** Rebase metric projected coordinates as they are; convert only geographic
   (lat/lon/h) to the local frame (ECEF → ENU, exact) and UTM where two sources disagree. Report grid scale (UTM k × elevation)
   and check it against one tape-measured distance (UX §5.6 step 1). ProjNet is LGPL; write the few formulas ourselves; PROJ
   (MIT) at run time later.
3. **E57 and LAZ are the survey formats.** E57 is readable in managed code (1024-byte CRC-32C pages, XML, bit-packed vectors;
   libE57Format is BSL-1.0 for reference). LAZ: load LASzip (Apache-2.0) at run time through its C API. Huge clouds are voxel-thinned
   while streaming; structured E57 scans (row/column indices) mesh with the existing `GridMesher`; survey meshes are decimated
   (millions of triangles cannot be a live stage).
4. **Control points are first-class.** Total station shots (13.5 mm RMS) and TLS (20 mm) are the truth; phone LiDAR (5 m range,
   cm to dm drift) and UAV (~12 cm) are not. Pins should take their 3D point from a control point, the model should be checked
   against control points (Jazé: a 20 cm model error calibrates "confidently" wrong), and projector positions measured by total
   station become priors.
5. **Long-throw solves need priors and must say what they cannot know.** Our covariance shows zoom and distance are inseparable
   on a flat facade seen head-on and metres-uncertain with 12 pins even with relief — while the picture is still within ~1.5 px.
   The solver should take a focal prior (lens datasheet or learned lens readback) and a position prior (total station), lock focal
   automatically when the data cannot carry it, and report uncertainty in wall pixels and metres along the axis. Dense pairs from a
   night scan bridged into the model fix everything at once — make that the recommended facade path.
6. **Night scan = averaging + flicker-safe exposure + transient rejection, not "subtraction".** Pattern-vs-inverse already cancels
   steady ambient. What breaks at night is low signal (average N frames per pattern), mains flicker (exposure a multiple of the
   LCM above), passing headlights (interleave shutter-closed frames and drop changed pixels), and resolution (telephoto, PTZ 4K or
   a tethered DSLR — Battersea used AW-UE100 PTZ cameras). Shutter-closed frames earn their keep in **photometry**: white ÷ shutter
   gives the achieved contrast against ambient per pixel, independent of albedo and camera gain.
7. **Glass is a mask problem, solved once in surface UV.** Evidence: low return vs its neighbours, decodes that disagree with the
   model (mirror ghosts, as Scalable describes), model materials named or flagged as glass, optional segmentation. Masks in UV serve
   every projector and stack member, keep light out of rooms, and are excluded from the solve as well as the output.
8. **Brightness planning is pixel density squared.** `CoverageAnalysis` already measures projector pixels per metre on every
   sample; irradiance is Φ/(W·H)·density², summed over stacks, derated, turned into nits by albedo and into contrast by ambient.
   Plan against Jazé's lux thresholds and against measured ambient.
9. **Stacks: warp every member to the same measured target, not to each other.** Treat a stack as one projector in the blend,
   align each member with a residual warp fitted from dense decodes of the shared camera (target ≤0.25 px; Scalable's "half a pixel
   reads soft"), match gain per channel from the same frames, and define what happens when a member fails (no vendor does) — hold,
   or dim the rest to match, by the operator's choice. The rental-truck tools (Epson, Panasonic, Christie) stack flat screens in a
   dark room; PlayMap stacks on the building at night.
