# Research: projectors' built-in camera alignment (the rental-truck competition)

*Topic "projcal", 13 October 2026. All pages were read on 13 Oct 2026 through a server-side fetch or search (Exa). "Verified
(fetch)" means the full page or document was read; "verified (hl)" means the cited sentence was read in a search extract from the
vendor page; "unverified" means it was not read and is not relied on. "n.d." means the page carries no date; where a document has a
revision or a "valid as of" line, that is given. Vendor command tables mirrored in public repositories were read locally
(`scratchpad/ext/ntc/`, converted with `pdftotext`).*

## 0. Summary in one paragraph

No major brand ships a *projector-only* auto-alignment that a third party can start over the network. Panasonic, Christie, Barco,
Sharp NEC and Sony do camera alignment in **Windows PC software** with a vendor-listed camera and **their own projectors only**;
Epson is the one brand with a camera **in or on the projector** (built-in on EB-L1000/L1500/L12000Q/L20000/L25000/L30000 families,
the attachable ELPEC01 "PixAlign" camera on EB-PU/PQ series) and menu-driven "Simple Stacking"/"Simple Blending" of two projectors
without a PC. The built-ins are flat-screen first (Epson also concave/convex, Christie Pro Venue ±70°, Panasonic curved/360° with
several cameras), want a dark room and a matte white screen, take minutes (Epson ≤4 min for a 2-stack, ≤9 min for a 6-stack;
Christie and Panasonic "<2 min per projector"), and keep the projectors' internal warp engines as the place the result lives.
Third parties can reset and partly read those internal settings (Panasonic publishes geometry, blend, colour-matching and uniformity
words, including a *measured* colour-matching mode whose measured and target xyY values can be written over the network); none
publishes a command that *runs* the camera alignment. Scalable Display (now inside Barco's tool) and Christie Mystique are the
reference for accuracy and for "re-calibrate in one click".

## 1. Panasonic

| Fact | Source (date) | Status |
|---|---|---|
| Auto Screen Adjustment is PC software: Geometry Manager Pro (GMP) + the "Auto Screen Adjustment plug-in" + a licence (ET-CUK10 on the projector, or ET-CUK10P on the PC); a camera on the PC by USB. "Test patterns or grids are projected … corrected"; the AUTO ADJUST button opens it | [GMP Ver.6.0 manual PDF](https://ap.pnd.panasonic.com/sites/default/files/media/document/2026-03/GeometryManagerPro_Ver.6.0_EN.pdf) (uploaded 2026-03) | Verified (hl) |
| ET-CUK10: automatically adjusts **geometry, edge blending, curved screen, stacking, colour & brightness, white balance, black level**; max 99 projectors = 4 H × 4 V × **6 stacked per camera**, max 6 cameras; LAN; GMP ≥ 2.0 (≥3.3 in the catalogue) | [ET-CUK10 spec](https://docs.connect.panasonic.com/projector/products/geometry/cuk10_spec/) n.d.; [ET-CUK10 catalogue PDF](https://docs.connect.panasonic.com/projector/download/catalog/pdf/ET-CUK10.pdf) n.d. | Verified (hl) |
| Supported projectors: Panasonic only (PT-RQ50K … PT-MZ10KL list; ★ pre-activated on RQ50K/RQ35K/RZ34K/RQ25K/RZ24K/RQ18K/RZ17K/REQ/REZ/MZ…KL) | ET-CUK10 spec (above) | Verified (hl) |
| Cameras: Nikon D5200/D5300/D5500/D5600 (+D7500 with GMP ≥ 6.1.10 and plug-in ≥ 5.1.10); listed Nikkor zooms with **VR off**; "special-purpose lens such as … fish-eye are not compatible" | ET-CUK10 spec; [Projector Feature Guide](https://docs.connect.panasonic.com/projector/feature_guide/) n.d. | Verified (hl) |
| "Available for various screen shapes, such as flat, convex, concave, portrait and S-shape"; "Calibration and adjustment based on the projector's internal image processor achieves minimal image degradation"; measures "colour, brightness, black level, uniformity and alignment" | [ET-CUK10 EU page](https://eu.connect.panasonic.com/global/en/projectors/et-cuk10cuk10p) n.d. | Verified (hl) |
| **Visual Software Suite 2.0** (free, Windows 10/11, i5 4-core, 16 GB) folds GMP in: "Auto geometry correction, blending, black level adjustment, colour matching (performed at the projector level only), and content splitting"; "Click to define the image projection area"; cameras Nikon D5200/D5300/D5500/D5600/D7500/**Z50/Z5** by USB or **IDS GV-5890CP-C-HQ** (PoE GigE) | [VSS 2.0 spec file](https://eu.connect.panasonic.com/sites/default/files/media/document/2025-10/Visual%20Software%20Suite_spec_en.pdf) "as of September 2025"; [VSS page](https://docs.connect.panasonic.com/projector/products/vss/) n.d. | Verified (fetch) |
| VSS cameras-to-projectors table: 1 camera → 1–4 projectors per row/column (and up to 6 stacked); 2 → 3–7; 3 → 4–10; 4 → 5–13; 5 → 6–16; 6 → 7–19. A **360° curved screen needs ≥ 5 projectors and ≥ 3 cameras** | VSS 2.0 spec file | Verified (fetch) |
| Stacks and 360° screens are not supported when the ET-FMP50 media processor does the warping | VSS 2.0 spec file, note 1 | Verified (fetch) |
| ET-UK20 and ET-CUK10 discontinued Q2 CY2026; GMP folded into VSS | [Geometry Manager page](https://docs.connect.panasonic.com/projector/products/geometry) n.d. (via r2/research/facade.md §8) | Verified (fetch, earlier session) |
| Smart Projector Control (iOS/Android): control of up to 64 projectors, NFC, lens, patterns; the phone camera is used **only for Auto Focus** (yellow frame on the screen, projector shows a focus pattern) — no phone-camera geometry or blend | [Smart Projector Control page](https://docs.connect.panasonic.com/projector/products/smartpjcontrol/) n.d.; [JP page](https://connect.panasonic.com/jp-ja/products-services/projector/lineup/smartpjcontrol) n.d. | Verified (hl) |
| On-projector Geometric Adjustment / Free Grid (2×2 … 17×17 by remote), three saved geometry memories | Feature Guide; [PT-RZ990 page](https://docs.connect.panasonic.com/projector/products/rz990/) n.d. | Verified (hl) |
| Time per run | — | **Not published** in what was read (catalogue: "3 quick, easy steps") |

**What a third party can do over NTCONTROL (TCP 1024)** — from Panasonic's own PT-RZ570 control-command table (2019-06), mirrored in
[peschuster/ntcontrol-connection `doc/com_rz570.pdf`](https://github.com/peschuster/ntcontrol-connection) and read locally.
Verified for the RZ570 series; other models answer ERR1 if they lack a word (model-dependent, as PlayMap already marks Panasonic):

| Setting | Control | Query | Neutral value |
|---|---|---|---|
| Geometry (keystone/curved/PC-1..3/corner) | `VXX:GMMI0=+0000n` (0 off, 1 keystone, 2 curved, 3–5 PC-1..3, 10 corner) | `QVX:GMMI0` | `+00000` |
| Edge blending | `VXX:EDBI0=…`, sides `VGU/VGB/VGL/VGR:0/1`, widths `VXX:EUWI0/EBWI0/ELWI0/ERWI0` | `QVX:EDBI0` … | `+00000` |
| Edge-blend black (non-overlapped level, border level and widths, overlapped black per side) | `VJI:www.rrr.ggg.bbb`, `VXX:EBII1..6`, `VJO:`, `VJU/VJB/VJL/VJR`, `VXX:EBBI4..7`, `VXX:EBBS0..3` | matching `Q…` | off / 0 |
| Edge-blend **auto test pattern** | `VXX:EATI1=+00001` | `QVX:EATI1` | `+00000` |
| **Colour matching** off / 3 colours / 7 colours / **measured** | `VXX:CMAI0=+00000/1/2/4` | `QVX:CMAI0` | `+00000` |
| Colour matching 3-colour values | `VMR/VMG/VMB:rrrr,gggg,bbbb` (0–2048), `VMW:` gain | `QMR/QMG/QMB/QMW` | — |
| **Measured mode: measured data** black/R/G/B/W as **Y,x,y** | `VXX:CMMS0..4=YYYYY,xxxx,yyyy` (Y 0–65535, x,y 0001–0999 = ×1000) | `QVX:CMMS0..4` | — |
| **Measured mode: target data** R/G/B/C/M/Y/W as Y,x,y | `VXX:CMTS0..6=…` | `QVX:CMTS0..6` | — |
| Auto test pattern for colour matching | `VXX:CATI0/CATI1/CATI3=+00001` | — | `+00000` |
| Uniformity PC correction | `VXX:UFMI1=+00000/1`; per-point `E$W:c,±vvv,±hhh,nn` | `QVX:UFMI1`, `E$R:` | `+00000` |
| White balance high/low R,G,B | `VHR/VHG/VHB:000…`, `VOR/VOG/VOB:` | `QHR…` | as found |
| Dynamic contrast / iris | `OAI:0/1` | `QAI` | `0` |
| Daylight View | `VXX:DLVI0=+0000n` | `QVX:DLVI0` | `+00000` |
| Custom masking | `VXX:MSKI1=+00000/1` | `QVX:MSKI1` | `+00000` |
| Digital zoom H/V/both, interlock | `OZH/OZV/OZO:nnn`, `OZS:0` | `QZH…` | 50 / 0 |
| Frame response (latency) | `VXX:FDYI0=+00000/1` (normal/fast) | `QVX:FDYI0` | fast for alignment |
| Light output | `VXX:LOPI2=+000nn` | `QVX:LOPI2` | as found (do not change mid-run) |
| Lens calibration | `VXX:LNSI0=+00001` (execute all) | — | — |
| Screen Adjustment key | `OSA` (remote-key emulation: opens the menu) | — | — |

There is **no NTCONTROL word that starts Auto Screen Adjustment**: it runs in the PC software. Geometry Manager Pro writes its grid
warps through an unpublished binary NTCONTROL variant (docs/PROJECTORS.md §2).

## 2. Epson

| Fact | Source (date) | Status |
|---|---|---|
| **Built-in camera** on EB-L1000U/L1100U/L1105U/L1200U/L1300U/L1405U, EB-L1500U/L1505U, EB-L1510S/L1515S/L1710S/L1715S, EB-L1500UH/L1505UH/L1750U/L1755U, EB-L12000Q/L12002Q/L20000U/L20002U, EB-L25000U/L30000U/L30002U; **EB-PU series and UST lenses need the external ELPEC01** | [Epson EU FAQ KA-01058](https://www.epson.eu/en_EU/faq/KA-01058) n.d.; "The projector has two cameras" (error 0203) | Verified (fetch) |
| ELPEC01 "PixAlign": CMOS 1/2.9", **2 MP, 15 fps**, USB-powered 5 V < 1.5 W; mounts on the projector or the UST lens "without the need for an angle of view adjustment" | [EB-PU2220 Specifications Rev.D](https://download3.ebz.epson.net/dsc/f/03/00/16/38/51/29988ab0685a6c557e302ebec43bc6877b90e0ff/Specifications_EBPU2220series_EN_r103.pdf) n.d.; [Epson advanced tools](https://epson.com/advanced-projector-installation-tools) n.d. | Verified (fetch) |
| Camera functions on EB-PU2220 with ELPEC01: **Blending** (flat/concave/convex), **Stacking** (flat/concave/convex), **Colour calibration** (flat/concave/convex), **Screen matching** (flat/concave), **Remote camera access**; none with ELPLX02/ELPLX02W or the ELPLL08 long-throw lens | Specifications Rev.D, "External Camera Correspondence Table" | Verified (fetch) |
| Camera-assist curved correction only on "a part of an exact circle"; minimum R/L per lens (e.g. ELPLM15 concave 0.27, convex 0.52 horizontally) | Specifications Rev.D §2.5.2 / §2.6 | Verified (fetch) |
| **Simple Stacking** (menu, no PC): **2 projectors**, LAN cable between them, DHCP on, the operator shapes the master with Quick Corner, then "Start Auto Adjustment"; remaining gaps by Point Correction (grid points movable 0.5–600 px); **up to ~4 min** | [Simple Stacking task](https://files.support.epson.com/docid/cpd6/cpd65465/EN/Adjustments/Tasks/multi_projection_stacking_menu.html) n.d.; [Firmware 4.10 notes](https://download3.ebz.epson.net/dsc/f/03/00/13/62/60/7ce339715bcec6d2a50b7352c211df19c52a2e8e/FirmwareUpdateInfo_v410_L20002Useries_r1.pdf) n.d. | Verified (hl) |
| Conditions (camera correction / simple stacking): camera on each projector; **wait 2 min** after power-on; **matte white flat even** screen; projector **squarely facing** the screen; white:black illuminance ratio **≥ 8:1** (screen ≤ 40 lux → size bands, e.g. 20–24.9 klm: 80–400 in); **same native resolution** on all; Front or Front/Ceiling | Firmware 4.10 notes; [Conditions page](https://files.support.epson.com/docid/cpd6/cpd65465/EN/Menus/Reference/conditions_simple_stacking.html) n.d. | Verified (hl) |
| Stacking with Epson Projector Professional Tool (EPPT) "Geometry Correction Assist": **up to 6** projectors, layout set on the Edit Group screen, "**up to approximately 9 minutes**"; gaps fixed by Point Correction | [EPPT stacking task](https://files.support.epson.com/docid/cpd6/cpd65465/EN/Adjustments/Tasks/multi_projection_stacking_protool_camera.html) n.d. | Verified (hl) |
| Tiling Assist: "Combine **up to 15 projectors** and up to 300-inch screen per projector"; Screen Matching "adjusts colour inconsistencies when using multiple projectors"; "Select Pro Series projectors can perform [stacking] without a PC or router" | [Epson advanced tools](https://epson.com/advanced-projector-installation-tools) n.d. | Verified (fetch) |
| **Colour Calibration** (single projector, ageing): flat, matte white, 80–200 in, projector parallel to the surface; **~1 min 30 s**; recommended **30 min** after power-on (troubleshooting says **20 min**); room darkened; geometry correction off or small; resets Colour Uniformity and Colour Matching to defaults; Undo/Redo; **can be scheduled on the projector** (Extended → Schedule Settings, up to four weeks); errors 0201/0202/0203 | [Epson EU FAQ KA-01058](https://www.epson.eu/en_EU/faq/KA-01058) n.d. | Verified (fetch) |
| Multi-Projection menu: "adjust the tint, brightness, black level, and blend curve of multiple projectors so that they match better"; auto adjustment only with a camera and Colour Calibration on | [Multi-Projection menu](https://files.support.epson.com/docid/cpd6/cpd65465/EN/Menus/Reference/menu_multi_projection_setting_ebl890e.html) n.d. | Verified (hl) |
| **Epson Web API**: `GET http(s)://<ip>/api/v01/control/escvp21?cmd=<CMD>+<p1>[+<p2>]` executes **ESC/VP21** lines; Digest auth user **EPSONWEB** with the Web Control password; enabled in Network → Projector Control → Web API (or the Developer's Menu "Enable API"); works "on or in standby when ready to communicate"; content-playback API `/api/v01/contentmgr/…` | [Web API Specifications Ver.1.1.0](https://download3.ebz.epson.net/dsc/f/03/00/12/82/04/c6e7ed44d7553969835eab058494d2d818493921/WebAPI_Specifications_EN_Ver1.1.0.pdf) n.d.; [Web API page](https://files.support.epson.com/docid/cpd6/cpd65465/EN/Monitoring/Concepts/web_api.html) n.d. | Verified (hl) |
| Network menu has **Remote Camera Access**; Web Control "Checking the Projected Image from a Remote Location" (a photo through the built-in/external camera) | EB-PQ2216 User's Guide TOC ([PDF](https://download.epson-europe.com/pub/download/6621/epson662172eu.pdf)); Epson advanced tools | Verified (hl) — the HTTP endpoint for the photo was **not** read (unverified) |
| ESC/VP21 words for the camera functions (start stacking/blending/screen matching/colour calibration) | The ESC/VP21 list pages that were fetched list only power/source/mute in the extract | **Unverified**: no published trigger was read |
| EB-PU2220 also has Art-Net and sACN control and PJLink | Specifications Rev.D TOC §3.4–3.5 | Verified (fetch) |

## 3. Christie

| Fact | Source (date) | Status |
|---|---|---|
| Mystique: "automated camera-based alignment and recalibration"; integrates with **Christie Twist**-enabled 3DLP/1DLP projectors (Inspire, GS (some), Korus, Jazz, HS, Crimson, M 4K RGB, Griffyn, Sapphire 4K40-RGBH, Eclipse, D4K40-RGB) **or Pandoras Box 6+** "regardless of the projector" ("blend quality … cannot be guaranteed for non-Christie projectors") | [Mystique family](https://www.christiedigital.com/products/warping-blending/mystique/) n.d.; [brochure](https://preproduction.christiedigital.com/globalassets/resources/public/christie-mystique-brochure.pdf) n.d. | Verified (hl) |
| **Mystique Lite** (free): Logitech C920/C920s (brochure adds C922 and "Christie Intelligent Camera"); **up to 3 Christie projectors in a horizontal array, flat**, warp and blend only, **no stacking** | same | Verified (hl) |
| **Essentials**: single camera, flat, **up to 12 projectors (2 high × 3 wide, double-stacked)**, "<2 min per projector"; colour camera background compensation (Mystique ≥ 2.5, M 4K RGB/Griffyn/Sapphire/4K40-RGB/Boxer 4K30/Mirage 304K); 5 MP CCD PoE cameras with 5–16 mm lenses, pre-calibrated pairs; USB licence dongle | [Essentials](https://www.christiedigital.com/products/warping-blending/mystique/mystique-essentials-edition/) n.d. | Verified (hl) |
| **Pro Venue**: flat and curved **±70°**, unlimited projectors if one camera sees all; marked-surface alignment (rink lines); "recognizes small changes in the position of the projectors, camera and even the projection surface and automatically resets the alignment" | [Pro Venue](https://www.christiedigital.com/products/warping-blending/mystique/Mystique-pro-venue-edition/) n.d. | Verified (hl) |
| **Premium / LSE**: multi-camera, domes, custom smooth screens, imported screen model; LSE does markerless 3D projection mapping and corrects drift | brochure | Verified (hl) |
| **Guardian** (option): "constantly monitors … automatically calibrates the image in real-time … even when content is playing"; Griffyn, Crimson, D4K40-RGB, Boxer 4K30, Mirage 304K; not with Pandoras Box | brochure; [Griffyn FAQ](https://www.christiedigital.com/globalassets/resources/public/griffyn-faq.pdf) n.d. | Verified (hl) |
| Electronic **black-level blending** (LSE; add-on for Pro Venue/Premium) | brochure | Verified (hl) |
| "The results will be limited by the lowest-performing projector in the array" | Mystique family | Verified (hl) |
| AutoStack (older): 2 × 3 blended array stacked to 12, "under two minutes per projector", 3/5 MP cameras with 3.5–12 mm lenses; Premium curved ±70° | [AutoStack datasheet](https://www.christiedigital.com/globalassets/resources/public/christie-autostack-datasheet.pdf) n.d. | Verified (hl) |
| Griffyn: built-in Twist edge-blends and stacks **up to 6** images on 2D or 3D surfaces; Twist Premium 16 projectors / 87 points; Twist Pro unlimited / 1500 points; Christie Conductor monitors up to 256 projectors | Griffyn FAQ | Verified (hl) |
| A built-in camera inside a Christie projector | — | **None found** |
| A network trigger for Mystique recalibration (API, show-control command) | — | **Unverified** (not found) |

## 4. Barco

| Fact | Source (date) | Status |
|---|---|---|
| **Barco Auto-alignment** launched 28 Jan 2026, co-developed with **Scalable Display Technologies**: camera-guided warp, edge blend and realignment "in just a few clicks"; uses the **onboard warping and blending of Pulse OS** (no media server) | [press release](https://www.barco.com/en/news/press-releases/2026/auto-alignment-scalable) 2026-01-28; [product page](https://www.barco.com/en/product/barco-auto-alignment) n.d. | Verified (fetch) |
| Supported: QDX-4K45/N4K35/N4K45/4K35/N4K25/W35/W45 (and RGB), UDM-4K30/W30/4K15/W22/W15/4K22/W19, I600-4K8/4K15/4K10 | [Auto Alignment Tool](https://www.barco.com/en/support/software/R3306296) n.d. | Verified (hl) |
| User guide v02 (4 Aug 2026, SW 1.0.7): "a basic environment consisting of a host computer and **up to 4 Barco Pulse projectors**" | [user guide page](https://www.barco.com/en/support/docs/R5920262) | Verified (fetch) |
| Projector Toolset for Pulse (Java SE 8u401) — scheduler, lens calibration, PJLink, custom EDID | [Projector Toolset guide](https://www.barco.com/en/support/docs/r5906795) n.d. | Verified (hl) |
| Scalable: "Barco, Hitachi, NEC, and Ricoh embed Scalable's algorithms in their projector firmware as licensed OEM technology, enabling single-camera calibration without an external SDM workstation"; SDM is hardware-agnostic, mixes models, recalibrates on a schedule "or triggered by show control systems"; webcam (C920) to GigE | [Scalable warp-and-blend guide](https://www.scalabledisplay.com/projector-warp-and-blend/) 2025-01-15 | Verified (hl) — the firmware claim is the vendor's own |
| Pulse JSON-RPC properties for warp/blend upload | docs/PROJECTORS.md §3 | Not read (unverified) |

## 5. Sharp NEC

| Fact | Source (date) | Status |
|---|---|---|
| **ProAssist**: PC software over the network; Edge Blending, Uniformity, Gamma Tone, Geometric Correction (cylinder, sphere), and **Auto Adjust** with a webcam (**Logitech C922N only**) that sets **white balance, black level, gamma tone and uniformity** for **two projectors only** ("not possible … for three or more"); edge blending must be set first; results stored in projector memory 1; Undo | [ProAssist page](https://sharp-displays.jp.sharp/dl/en/pj_soft/proassist.html) n.d.; [ProAssist manual v4](https://sharp-displays.jp.sharp/support/webdl/dl_service/data/ProAssist/ProAssist_User_manual_EN_Ver4.pdf) n.d. | Verified (hl) |
| Auto Adjust models: NP-PA703UL/803UL/733UL+/804UL/1004UL/1505UL/1705UL, NP-PX803UL/1004UL (table) | ProAssist page | Verified (hl) |
| **Multi Screen Tool**: web camera photographs the images; creates geometric-correction and edge-blend data for **stacking** (master or auto: largest rectangle in the overlap) and **tiling 2–4** projectors; "**Only flat screens** are supported", screens up to 10 m; 4K tiling 2 × 2; lens memory not supported; data saved as a GCXS file | [MST page](https://www.sharp-nec-displays.com/dl/en/pj_soft/mst.html) n.d.; [MST manual](https://sharp-displays.jp.sharp/support/webdl/dl_service/data/mst/MST_manual_ENG_8.pdf) n.d. | Verified (hl) |
| Geometric Correction Tool 4: manual cylinder/sphere/corner-wall correction from a PC | [GCT4](https://www.sharp-nec-displays.com/dl/en/pj_soft/gct4.html) n.d. | Verified (hl) |
| Scalable in NEC firmware | Scalable guide (vendor claim) | Unverified for which models |

## 6. Sony and Optoma

| Fact | Source (date) | Status |
|---|---|---|
| Sony **Projector Calibration Tool DSMA-PC1** v1.0.0.2 for **VPL-GTZ380**: "calibrate colour uniformity and colour matching using a connected computer"; cameras Sony α5100/α6100/α6400 | [Sony support](https://www.sony.com/electronics/support/software/00274980) release 2022-01-20 | Verified (fetch) |
| Sony camera geometry/blend alignment | — | None found |
| Optoma camera-assisted alignment | — | **None found** in one search (unverified absence) |

## 7. Reference points outside the projector makers

| Fact | Source (date) | Status |
|---|---|---|
| Scalable Atlas: automatic camera-based mapping and blending on any object, scheduled and one-click recalibration, MPCDI; ≥ 15 % overlap (25 % for wide viewing or rear projection); projector settings: gamma 2.2, 6500 K, full range, **dynamic black off, overscan off, keystone off** | [Atlas manual](https://knowledgebase.scalabledisplay.com/en_US/Software-User-Manuals/scalable-atlas-user-manual) n.d. | Verified (hl) |
| disguise OmniCal: capture "typically takes less than a minute"; sub-pixel; point cloud + projector intrinsics; one-click recalibration when projectors move | [OmniCal](https://www.disguise.one/en/products/omnical) n.d.; [Prolight](https://prolight.com.pl/en/product/omnical.html) n.d. | Verified (hl) |
| Modulo Kinetic V7 3D auto-calibration: a simulation mode that runs the calibration on the 3D scene with no captures; checkerboard + inverse to associate cameras with projectors; parallel capture needs 10 GbE | [Modulo Kinetic manual](https://support.modulo-pi.com/modulo-kinetic-manual/v7/en/topic/3d-autocalibration-guide) n.d. | Verified (hl) |
| Christie patent US 9,872,003: stacked images warped to a virtual target between the members | r2/research/facade.md §8 | Verified (hl, earlier) |

## 8. The comparison PlayMap must beat

| | Where it runs | Camera | Brands | Shapes | Stacks | Colour | Time (vendor) | Network trigger |
|---|---|---|---|---|---|---|---|---|
| Panasonic VSS 2.0 / GMP + CUK10 | Windows PC | Nikon DSLR/Z, IDS GigE | Panasonic | flat, convex, concave, S, 360° (≥3 cams) | 6 per camera | colour, brightness, WB, black | n/p | none published |
| Epson camera assist | in projector (2-stack, 2-blend) or EPPT on PC | built-in / ELPEC01 2 MP | Epson, same resolution | flat (+ arc concave/convex) | 2 (menu), 6 (EPPT) | calibration, screen matching | ≤4 min (2-stack), ≤9 min (6), 1.5 min colour | Web API carries ESC/VP21; camera-function words not read |
| Christie Mystique | Windows PC | webcam (Lite), 5 MP GigE | Christie Twist (or Pandoras Box) | flat → ±70° → dome/3D by edition | 2×3×2 (Essentials) | background comp., black level | <2 min/projector | not found |
| Barco Auto-alignment | PC | (not read) | Barco Pulse | "curved or irregular" | yes ("stacked multi-channel") | not stated | not stated | not found |
| Sharp NEC ProAssist / MST | Windows PC | C922N only | NEC | flat (MST) | MST stacking | WB, black, gamma, uniformity — 2 projectors | not stated | not found |
| Sony DSMA-PC1 | PC | α5100/6100/6400 | VPL-GTZ380 | — | — | uniformity, matching | — | — |

## Design implications for PlayMap

**(a) Interop — trigger and supervise, reset before PlayMap's own.**

1. **Snapshot, then neutralise, then restore on request.** Before any PlayMap alignment, read and keep every internal setting a
   protocol publishes, then set it neutral; never destroy a rental house's internal alignment silently. Panasonic is fully
   verified for this from its own table (RZ570, model-dependent): `GMMI0`, `EDBI0` + sides, `CMAI0`, `UFMI1`, `OAI`, `DLVI0`,
   `MSKI1`, `OZH/OZV/OZO/OZS`, `FDYI0` (fast). Christie's `WRP`/`WPS` stays gated (docs/PROJECTORS.md). Epson and Barco have no
   published neutral words in what was read → the plan says in words what the operator must switch off (Epson: Geometry Correction
   Off, Edge Blending Off, Screen Matching / Colour Matching reset; Barco: warp/blend off in Projector Toolset).
2. **Double-correction guard.** Scalable and Epson both warn that internal keystone under an external warp loses sharpness and
   breaks the camera's measure. When PlayMap's measured correction is applied to an output whose projector reports internal
   geometry or blend on, the context goes amber and says which.
3. **No vendor run can be started from the network** in anything read (Panasonic, Christie, Barco and NEC run it in PC software;
   Epson's camera functions are menu or EPPT; the ESC/VP21 words were not found). So "trigger" is honest only as: PlayMap can
   *start* a vendor alignment where a verified word exists (none today — the verb exists and refuses with this reason), and PlayMap
   can always **audit** what a built-in left on the wall with its own camera (seam misregistration, stack convergence, white
   uniformity, colour difference) and score it. A failed or poor built-in result then has a one-press fallback: neutralise and run
   PlayMap's.
4. **Push into the projector where it is published.** Panasonic's *measured* colour-matching mode takes measured and target xyY
   (`CMMS0..4`, `CMTS0..6`, `CMAI0=+00004`) — PlayMap's camera measurement can be written there so the match runs in the
   projector's 10/12-bit processor (Panasonic's own selling point: "minimal image degradation"). White-balance gains
   (`VHR/VHG/VHB`) are already a verified capability. Units of `Y` need one session on a real projector → gated until tested.
5. **Epson's Web API** (`/api/v01/control/escvp21`, Digest `EPSONWEB`) is a second transport for the same ESC/VP21 words — useful
   when EPPT holds TCP 3629. Its "Remote Camera Access" photo could later make an Epson's own camera a PlayMap scan/verify camera
   (endpoint unverified).
6. **Respect the vendors' own preconditions as measured checks**, not timers: Epson asks for 2 min (stacking) and 20–30 min
   (colour) of warm-up and ≥ 8:1 white:black on the screen; PlayMap measures light-output drift and contrast and gates the colour
   pass on them.

**(b) How PlayMap wins.**

1. **Any brand mix, any resolution.** Every vendor tool is single-brand (Christie's only escape is Pandoras Box; Epson requires
   the same native resolution). PlayMap's correction lives in its own output stage, so the warp, blend and colour match are the
   same for any projector, and the measurement only needs the projectors' light.
2. **Curved and 3D.** The built-ins are flat first (Epson: arcs of an exact circle; NEC MST: flat only; Christie needs the
   expensive editions for domes and 3D). PlayMap already self-calibrates projectors and the camera and triangulates the screen:
   fit plane, cylinder or sphere, or hand a freeform object to the 3D mapping path — one flow.
3. **Faster from the truck.** Vendors: 1.5–9 min, Christie "<2 min per projector". PlayMap: discover → identify by optical
   footprint and shutter knock → neutralise → scan non-overlapping projectors **in parallel** → solve → colour → verify. Target
   ≤ 90 s for four projectors on a flat screen (estimate, to be measured).
4. **Colour and brightness across brands**: a 3×3 matrix per projector in linear light to the common gamut and white, per-pixel
   uniformity, measured black-level blending — free, where Christie sells black-level blending as an add-on and NEC matches only
   two projectors.
5. **A fallback when the built-in fails, and when PlayMap fails**: snapshot/restore of the projector's own settings, every run
   staged with an explained score, the previous result kept as the fallback, a history of runs.
6. **Local and safe**: no licence dongle, no cloud, any UVC/phone/NDI camera; every step is a verb with its risk (shutters and
   patterns are High; accepting a result is Review), so an assistant can plan and score but a person presses Run and Accept.
