# Where PlayMap stands

*PlayMap's competitive position: the field, the matrix, and what to learn from each. Every third-party fact carries a source URL. **Estimate** marks our own judgement, not a measurement.*

**How to read the sources.** The research pass read most vendor pages through search-result summaries, because many vendor domains (madmapper.com, resolume.com, dataton.com, disguise.one, pixera.one, help.disguise.one and others) did not resolve from our build environment. A final fact check on 12 October could not re-open those domains and had no search budget left. So:

- **(verified)** means re-read from a primary source on 12 Oct 2026.
- **(unverified)** means it comes from the research pass and could not be re-checked. Confirm it before quoting it outside the team. Version numbers, release dates and prices are the facts most likely to be wrong.
- **unknown** means we found no source.

Vendor capability claims are the vendors' own. We have tested none of these products ourselves.

## 1. The short answer

**No. PlayMap is not better than MadMapper today, and it is not in the class of disguise, Pixera or Watchout.** It has never put a frame on a real projector.

Status of main:
- All ten modules are merged and tested, and `PlayMap engine [headless]` composes the real media, render, network and scan backends. The desk starts and watches this machine's engine or attaches to a node on the network; the round-two verbs run over the wire until the desk's controls for them land.
- The round-two work is built on simulators: show safety, vblank truth and the frame lock, the HAP family on the GPU, optimise to HAP on import, OSC, MIDI, Art-Net and sACN input with bindings, timecode chase, the node's side of Companion, ISF effect chains, audio analysis and modulation, generative content, pixel quads and the calibration ledger, camera auto-align, fleet health and LED pixel mapping (docs/ROADMAP.md §0).
- No real camera, projector, sync board, SAM model or second machine has been used; the GL tests run on Mesa's software renderer. Every scan, solve and device test runs against simulators or protocol fixtures.

**By design, PlayMap aims past MadMapper in 3D calibration, multi-node sync and automation. That is a hypothesis until Round 1 publishes RMS figures from real hardware** (docs/ROADMAP.md). Scope is not robustness. Our simulators do not model rolling shutter, auto-exposure, projector image processing, interreflection or subsurface scattering, and those are what break structured light in real rooms.

Five design ideas we have not found combined in one product:

1. **Structured-light calibration with commodity cameras and no per-output licence.** High-end autocal mostly comes with vendor camera kits (disguise OmniCal, Christie Mystique, Modulo's PoE-camera module) or per-channel licences (VIOSO). That is not universal: VIOSO accepts NDI cameras, MadMapper's Space Scanner takes a DSLR, webcam or NDI camera, and Panasonic, Epson and Christie sell camera-based geometry and blend for their own projectors (§3b). Our difference is the combination: free, any camera, curved surfaces, projectors from any maker.
2. **Masks and warps stored in surface UV space, separate from playlists.** A fix made at the venue applies to every playlist. When only a projector moves, masks and warps survive by construction. When the mesh itself changes, survival needs the retargeting work in ROADMAP R4.7.
3. **Each frame is a pure function of leader show time.** This makes timecode chase clean, and it makes a backup node a live mirror **for deterministic sources** (files, generators, `SET`s scheduled with `AT`). Live inputs, web pages, audio analysis and unscheduled edits break frame identity between nodes (§5c).
4. **One verb vocabulary, a full machine-readable STATE, and MCP with risk-gated approvals.** The desk has no private API.
5. **Ray-pick and highlight verbs over a calibrated projector-to-surface map, offered to thin clients.** disguise and Pixera also hold a 3D world model and expose APIs, so the world model alone is not unique. What we have not seen is that model offered as "send a ray, get back the surface, and see it light up on the object". That is the primitive phone and glasses control needs (docs/XR.md).

**What "frame-accurate sync" means here.** Every node picks the same frame index for a given show time. Without genlock, the moment that frame reaches the projector can differ by up to one refresh between nodes. That ±1 refresh figure is a design target and has not been measured. Presenting on the same vblank needs framelock (ROADMAP R4.2).

**Where PlayMap is behind, and stays behind until it is built:**
- **Output smoothness:** OpenGL 3.3 core or ES 3.00 and one render thread; each output measures its vblanks and presents. No D3D11 flip-model; the frame-lock machine is built, but its NVAPI tier is compile-only and joins no swap group.
- **Codecs:** the HAP family decodes on the GPU and clips optimise to HAP on import, but hardware decode of other codecs copies frames back to system memory.
- **Video IO:** no NDI, Spout, OMT or capture-card SDKs.
- **Control:** OSC, MIDI, Art-Net and sACN input with bindings, and timecode chase, are built; no Companion module yet (the node's side is built), no MIDI out or OSCQuery.
- **Show floor:** Show Mode, locks, panic, blind edit, preflight and fleet health are built; no redundancy.
- **Creative breadth:** ISF effect chains, audio reactivity and LED pixel mapping are built; no FFGL, laser output or export.
- **Adoption:** no importers from other tools, no signed installer, no reference hardware list, no support offer, and one maintainer.

## 2. What PlayMap is today

| Area | Merged and tested (simulators only) | In progress | Designed only | Absent |
|---|---|---|---|---|
| Playback core | Playlists, gapless lanes, `ClipTransport`, `TimelineAnchor`, frame selection, `AT` starts, sync groups (the leader clock, anchors and the group's air carried to followers, the vblank board), timecode chase, blind edit, sound on the air | — | Web/YouTube (enum only), Stagewatch timer source | Cue stack and timeline (by design), reverse play |
| Media | FFmpeg 9 probe, hashing, HW decode with copy-back (never run on a GPU), frame-accurate seek, stills, sequences, avdevice capture, sound input, the HAP family on the GPU, optimise to HAP on import with PlayMap's own GPU encoder, an OpenAL audio output that plays the on-air item's sound | OMT and NDI in and out | D3D11VA zero-copy | Spout, Syphon, HDR out, ASIO |
| Render | GL 3.3 core or ES 3.00 projector pass; output stage (warp, mask, blend, black level, trim, dither); vblank clock and present ledger per output; ISF effect chains; the edit overlay; every catalogued generator; Fade and Dip on scheduled switches and on a GO by hand; the blind draft's editor view | — | D3D11 flip-model | Vulkan, Metal |
| Mapping maths | Lattices, TPS pin warp, Bezier tools, mask raster, 3D auto-blend, intensity compensation, coverage analysis, MPCDI export package, pixel quads, LED pixel mapping | — | Rescan retargeting | Mesh deform, planner UI |
| Scan and calibration | Gray code + phase decoder, smart scan, quality score, meshing, `PoseSolver`, self-calibration, ICP, bridged registration, mesh and cloud IO, scan simulator, the scan runner, the calibration score and ledger, camera auto-align of flat screens, the multi-view solve (Core only) | The phone as a scan camera | — | Any real-camera run |
| Vision | Segmenter router (classical, plus SAM when a verified model is present), depth previs, mask-to-path, pin suggester, drift watch; click-to-surface and suggested regions by verb (`SCAN SURFACE`) | — | Verbs for depth previs, pin suggestions and drift watch | Model files: catalogue checksums are not yet verified, so built-in models refuse to load |
| Devices | PJLink 1/2, Christie, Panasonic, Barco Pulse and Epson drivers written from public protocol code; NEC and Sony gated as unverified; projector verbs (`DEVICE …`), the link from a projector to its device, geometry reset before each scan | — | — | Low-latency mode |
| Network | Show Bus hello, wire server, HTTP/WS, MCP, TLS with certificate pinning, content-addressed media transfer, fine clock sync, the sync group's anchor feed and vblank reports, OSC, MIDI and Art-Net/sACN input, the LED sender, mDNS, the node's side of Companion, the fleet monitor | The Companion module | Show Bus 1.3 proposals | — |
| AI | Assistant (a hosted or a local model), `CommandGate`, proposals, recipes | — | Voice, deixis | — |
| Desk | Window, PLAY / MAP / SCAN, drawers, demo engine, view models, drag tests; the real engine, local or on the network | Controls for the round-two verbs | — | Web remote, roles, XR; onboarding beyond `LIGHT WALL` |

The verb catalogue has 294 rows; a node answers the 104 first-round rows and the 148 round-two rows the packages route (`VERBS` marks each). `SCAN START` and `SCAN APPLY` run on a capture camera or the scan simulator, and `BLEND SOLVE` solves on a worker; `LATENCY MEASURE` refuses: its camera path is not built. `PROJECTOR SOLVE` is wired to the solver but has only simulated input.

## 3. The field in October 2026

### 3a. Main competitors

| Product | Version and model | Price | Sources | Status |
|---|---|---|---|---|
| MadMapper | 6.x. v6 added timelines, montage tracks and a new UI (beta Dec 2024). A 6.1.3 release in Jun 2026 and a 6.2 beta were reported | About €399–479 perpetual, or about €39–44 a month; free mode is watermarked | [CDM on v6](https://cdm.link/madmapper-timelines-clips-v6/), [forum 6.1.3](https://forum.garagecube.com/viewtopic.php?f=20&t=36456), [pricing](https://madmapper.com/madmapper/software) | v6 timelines from CDM; point release and price unverified |
| Resolume Arena | 7.x. Clip grid plus Advanced Output (slices, warp, edge blend). A 7.28.1 release on 6 Oct 2026 was reported | Perpetual; reported at €799 (the research pass also saw €899) | [download](https://resolume.com/download/) | Unverified |
| HeavyM | 2.x. Draw shapes, then add effects | Reports conflict, from about $149 a year to $599 | [update history](https://www.heavym.net/update-history/) | Unverified |
| Millumin | 5.x, macOS only. Cue and timeline hybrid with a dashboard | Reported $499 | [changelog](https://help.millumin.com/docs/general/change-log/) | Unverified |
| TouchDesigner + Moonshine | Node graph. Moonshine is a collaborative browser mapping front-end, in beta | TD Commercial reported at $600; Moonshine reported at $199 / $399 | [Derivative licensing](https://derivative.ca/UserGuide/Licensing), [Moonshine](https://moonshine.software/) | Unverified |
| disguise | r34.x reported. Timeline and tracks, 3D stage, OmniCal | Designer Starter free; Pro $159 a month or $1,500 a year. Software-only X-series tiers on your own hardware were reported; their prices are unknown | [Starter vs Pro](https://disguise.one/products/designer-starter-vs-pro), [X Series](https://www.disguise.one/en/products/x-series) | Unverified |
| Pixera | 26.x (26.1 changelog found; a 26.2 changelog is also listed). Timelines plus cue list | USB dongle. Free Creator licence with 4 watermarked outputs. Camera autocal through an embedded VIOSO module needs a separate Autocal licence | [licences](https://pixera.helpjuice.com/pixera-licences-), [VIOSO in Pixera](https://docs.vioso.com/guide/mediaservers/avstumpfl), [26.1](https://help.pixera.one/pixera-261-overview-changelog) | Unverified |
| Watchout | 7.x (7.8.4 in Sep 2026 reported). Producer, Director and Runners; timelines and cues | €3,200 per server licence; Producer free | [cost FAQ](https://www.dataton.com/downloads/faq-watchout-7/what-does-watchout-7-cost) | Unverified |
| Smode | R15.x. Real-time compositor; imports VIOSO calibrations | Dongle; price unknown | [changelog](https://doc.smode.io/changelog/) | Unverified |
| Modulo Kinetic | V7 (May 2026 reported; vendor says V7 files are not backward compatible). Timelines plus node graph | Unknown | [Modulo Pi V7](https://www.modulo-pi.com/modulokinetic-version7/) | Unverified |
| VIOSO / Scalable | VIOSO 7.0.4 (May 2026): web recalibration panel, NDI calibration workflow, camera-positioning workflow. Scalable Atlas: one camera images each projector, imports CAD or artist meshes, blends across shadows, one-click or scheduled recalibration, MPCDI export | VIOSO licensed per projector channel | [VIOSO changelog](https://docs.vioso.com/changelog), [VIOSO licensing](https://wp.helpdesk.vioso.com/faq/licensing), [Atlas](https://www.scalabledisplay.com/products/scalable-atlas/) | Unverified |
| 2026 AI-first entrants | Panasonic + Illumibot IllumiBeast (phone camera and natural-language prompts). Appotronics AIMapper (InfoComm 2026 Best of Show). Artemis from Summit Technology Lab, a UC Irvine spinout (push-button camera mapping). Ghost Arcade (phone-projected stripes) | Mostly unknown | [IllumiBeast](https://www.projectorcentral.com/panasonic-illumibot-cybertruck.htm), [AIMapper](https://en.prnasia.com/releases/global/appotronics-aimapper-wins-projectorcentral-best-of-show-winner-at-infocomm-2026-537817.shtml), [Artemis](https://www.calit2.uci.edu/2026/02/19/10741), [Ghost Arcade](https://ghostarcade.live/blog/ghost-arcade-2-0-17-auto-map-phone-projector) | Unverified. Specific timings (Ghost Arcade "about 15 s per projector", Illumibot "allow up to 10 minutes") and AIMapper's cloud processing could not be re-checked |

### 3b. Also on riders, in rooms, and free

These are real alternatives a buyer will weigh. Most were not researched in depth. Rows without a source say so.

| Product | What matters for PlayMap | Source |
|---|---|---|
| **Projector-maker autocal** | Panasonic Geometry Manager Pro is free, with paid kits: ET-UK20 for 3D mapping and masking (up to 64 projectors) and ET-CUK10 for camera-based geometry, blend, colour and brightness. Epson sells camera-assisted edge blend and stacking for some models (source needed). Christie Mystique uses pre-calibrated 5 MP GigE camera kits to compute warp and blend for Christie Twist; its Guardian feature corrects alignment while content plays, on Boxer and Crimson projectors only. **For corporate AV these are the real low-end competitor: the rental projector already has them.** | [Panasonic](https://docs.connect.panasonic.com/projector/products/geometry), [Christie brochure](https://christiedigital.com/globalassets/resources/public/projection-solution-brochure.pdf) |
| Scalable Desktop | Up to 16 projectors on one PC, automatic warp, blend and colour, scheduled recalibration | [Scalable](https://www.scalabledisplay.com/products/scalable-desktop/) |
| Christie Pandoras Box (+ Widget Designer) | Common rider media server with its own control tool | Not researched |
| Green Hippo Hippotizer, 7thSense, Ventuz, Avolites Ai, Arkaos MediaMaster, Isadora | Rider and theatre alternatives | Not researched |
| LightAct 4 | Proprietary camera-based projector calibration plus a 3D workflow | [LightAct](https://lightact-systems.com/) |
| VYV Photon | Keeps a calibration history and applies a new result only within a reference gate | [VYV guide](https://www.vyv.ca/en/docs/calibration/automatic-calibration-and-real-time-alignment-guide/) (unverified) |
| QLab 5 | The most common "mapper" in theatre, through video surfaces with geometry and masks. Exact mapping features not re-verified | [QLab docs](https://qlab.app/docs/v5/fundamentals/workspace/) |
| Unreal nDisplay | Mesh projection policy, MPCDI, EasyBlend and VIOSO policies. Powerful, engineering-heavy, no built-in camera calibration | [Epic](https://dev.epicgames.com/documentation/en-us/unreal-engine/projection-policies-in-ndisplay-in-unreal-engine) |
| **Open source** | MapMap: Qt5, GPLv3, unmaintained since 2022. VPT 8: free, Max-based, last major release 2018. SAT Splash (multi-projector camera calibration and blending) and ofxPiMapper (openFrameworks, Raspberry Pi) are the closest in features and possible sources of test scenes (sources needed) | [MapMap](https://github.com/mapmapteam/mapmap), [VPT on CDM](https://cdm.link/video-projection-tool-for-mac-windows-projection-mapping-more-powerful-adds-mixing/) |

## 4. Competitive matrix

**Key.** "PlayMap built" means merged and tested on simulators, **never on hardware**. "PlayMap designed" means in the architecture and papers, not yet built. Competitor cells are vendor claims from the sources in §3 and below, and all are (unverified) unless marked otherwise.

### 4a. Mapping, calibration, playback, sync

| | Mapping | Calibration | Playback and codecs | Sync and scale |
|---|---|---|---|---|
| **PlayMap built** | Catmull-Rom/Bezier lattices, TPS pin warp, Bezier masks in UV, 3D auto-blend maths, black level, intensity compensation, pixel quads, click-to-surface | Decoder, smart scan, PnP/DLT/P3P + RANSAC, self-calibration, ICP, bridged registration; scan runner with projector geometry reset; explainable score and calibration ledger; camera auto-align of flat screens; multi-view solve in Core | FFmpeg 9 decode, HW decode with copy-back, frame-accurate seek, gapless loops; GPU HAP incl. HAP R/HDR; optimise to HAP on import; GL 3.3 or ES 3.00 output stage with vblank truth | Frame = f(leader show time); ns UDP clock; content-addressed media; frame-lock machine (NVAPI tier compile-only). Never run on two machines |
| **PlayMap designed** | Masks and warps survive rescans | Phone autocal; drift watch | D3D11VA zero-copy, D3D11 flip-model | ±1 refresh without genlock (unmeasured); NVAPI swap groups once outputs present through DXGI |
| **MadMapper 6** | Quads, masks, Bezier mesh warp, soft edge | Manual point matching on an imported model, about 6 points (dates from v2.0). Space Scanner (5.3+) captures what the projector sees with structured light through a DSLR, webcam or NDI camera, mainly as a mask or background aid ([guide PDF](https://madmapper.com/files/13-Space%20Scanner%20Guide-1.pdf)). Automatic 3D multi-projector blend: unknown | FFmpeg plus HAP | Follows timecode, MIDI and OSC; Ableton Link reported. No built-in automatic failover found (source needed) |
| **Resolume Arena 7** | 2D slices, mesh warp, masks, edge blend. Staff: "Resolume works in 2D currently" ([forum](https://www.resolume.com/forum/viewtopic.php?p=79682)) | None built in | DXV3, HAP, H.264, ProRes; 10-bit reported | Timecode and OSC |
| **HeavyM 2** | 2D shapes, Bezier, effects; "not suited for complex 3D mapping" ([HeavyM](https://www.heavym.net/heavym2/)) | None | Common codecs | Single machine |
| **Millumin 5** | Layer and output mesh warp, masks; OBJ/DAE structure import for one viewpoint ([Millumin](https://millumin.com/)) | None | HAP and ProRes | Multi-machine in beta (reported) |
| **TouchDesigner + Moonshine** | kantanMapper, Stoner; Moonshine 3D mesh warp | camSchnappr: drag 6+ points into OpenCV `calibrateCamera` ([docs](https://derivative.ca/UserGuide/Palette%3AcamSchnappr)) | HAP family, NotchLC | Manual in TD; Moonshine failover reported |
| **disguise** | Mesh screens, Mesh Deform | OmniCal: Plan, RigCheck, Capture, Calibrate, Align, Mesh Deform; one-click recalibration if only projectors moved ([overview](https://help.disguise.one/workflows/calibration-projection/omnical-overview)). QuickCal: at least 6 reference points, 10–15 per projector recommended, mesh accurate to about ±5 mm on buildings ([QuickCal](https://help.disguise.one/workflows/calibration-projection/quickcal-ref-points)) | HAP, NotchLC, ProRes; 10-bit; ST 2110 | d3Net Director/Actor/Understudy; framelock. Understudy needs matching disguise hardware, per disguise docs |
| **Pixera 26** | Gizmo, warp engine, multi-view | Marker calibration ([Pixera](https://pixera.helpjuice.com/mapping-/marker-calibration-workflow)); camera autocal via a separately licensed VIOSO module | HAP incl. HAP R, NotchLC | Hub framelock and backup |
| **Watchout 7** | Displays, warp with 4 handles per junction ([warp](https://docs.dataton.com/guide/watchout/devices/warp-geometry.html)), masks | Point-correspondence 3D solve; NDI camera calibration streams and an HTTP API for external calibration ([docs](https://docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/display-calibration.html)); MPCDI import, 2D media profile only | HAP family, NotchLC, ProRes; optimise on import reported | Director and Runners, NVIDIA sync groups, spare by node name |
| **Smode R15** | Content maps, 3D | Locator calibrator: place locators on the model, match them in projector view, solve pose, throw and shift ([doc](https://smode.io/doc/ref/project/video-mapping/video-projector-calibrator.htm)); imports VIOSO | NotchLC, HAP | Cluster |
| **Modulo Kinetic** | 3D study with lens database | Autocalib 3D solves position, rotation, aspect, throw and shift from patterns ([manual](https://support.modulo-pi.com/modulo-kinetic-manual/v6/en/topic/calibrate-a-video-projector-in-the-3d-space-en)); PoE-camera module announced at ISE 2025 ([Modulo Pi](https://www.modulo-pi.com/showcase/ise2025-projector-3dautocalibration/)) | Unknown | Cluster |
| **VIOSO 7 / Atlas** | Auto warp and blend; 3D model workflows | Camera autocal; per-channel re-solve (Atlas); NDI calibration (VIOSO) | n/a (calibration layer) | n/a |
| **AI-first entrants** | Surfaces from a phone or camera scan | Phone or camera capture; cloud processing reported for AIMapper | Unknown | Artemis: scheduled recalibration reported |

### 4b. Control, UX, AI, price

| | Control and integration | UX | AI | Price and lock-in |
|---|---|---|---|---|
| **PlayMap built** | Wire, HTTP/WS, MCP, Show Bus, Patterns and Stagewatch links; OSC, MIDI, Art-Net and sACN input with bindings; timecode chase; the node's side of Companion; Art-Net, sACN and DDP LED output; projector control | The desk on the real engine, local or over the network; round-two verbs over the wire until the desk's controls land | Risk-gated assistant (a hosted or a local model), recipes; never used on a live show | Single exe, no dongle |
| **PlayMap designed** | Every parameter a dotted path; desk uses the same verbs | PLAY / MAP / SCAN; right-click mirrors verbs | Click-to-surface, depth previs, drift watch | Same |
| **MadMapper** | MIDI, OSC, DMX/Art-Net/sACN, LED and laser output, Syphon/Spout/NDI ([features](https://madmapper.com/madmapper/features)); OSCQuery, Ableton Link and MTC/LTC not re-checked | Surface-first; on-wall video cursor | Unknown | See §3. Projector control reported as a paid add-on; MiniMad hardware player (price unknown) |
| **Resolume Arena** | OSC, MIDI, DMX, REST, Companion module | Clip grid instrument | Local MCP servers and segmentation/depth features reported in 7.26–7.28 blog posts ([7.26](https://resolume.com/blog/34484)) | Perpetual |
| **HeavyM** | NDI, Spout, DMX reported | Fastest drawing loop | Generative content reported | Low |
| **Millumin** | MIDI, OSC, DMX, timecode, scripting | Theatre-friendly; Silent Edition | None found | Mac only |
| **TouchDesigner + Moonshine** | Everything (TD); browser co-editing (Moonshine) | Steep (TD); friendly (Moonshine) | Community MCP servers ([forum](https://forum.derivative.ca/t/minddesigner-tdmcp-an-mcp-server-that-builds-real-touchdesigner-networks-from-plain-language/993195)) | Tiers |
| **disguise** | APIs, OSC, timecode | Powerful and dense | "Ask AId3n" workflow assistant ([USITT](https://www.usitt.org/news/disguise-unveils-major-update-its-ai-assistant-ask-aid3n)) | Hardware or subscription |
| **Pixera** | Control modules, Art-Net/sACN | Locks, savable layouts, gizmo | None found (not exhaustive) | Dongle |
| **Watchout 7** | Variables over OSC, Art-Net, HTTP, MIDI | Blind editing reported | None found (not exhaustive) | Per server |
| **VIOSO / Atlas** | Web recalibration panel | Expert tool | None | Per output |
| **AI-first entrants** | Mostly closed | Point, prompt, play | Generative content | Cloud or hardware tie-in reported |

## 5. Is PlayMap better than MadMapper or the high end?

### 5a. Today

No, on every axis that counts on a job. A tool that has never played a frame through a real projector cannot be compared on smoothness, setup time or reliability.

The output path is a specific risk. On Windows, the documented route to reliable vblank timing and independent flip is a DXGI flip-model swap chain with frame statistics. OpenGL presentation goes through each driver's own path (estimate, our engineering judgement). PlayMap presents through OpenGL 3.3 core or ES 3.00, with one render thread for every output; each output keeps a vblank clock and a present ledger, so a missed vblank is counted, not guessed. A MadMapper forum thread reports staff steering Windows users towards Direct3D output for VBL sync, and users moving a stuttering four-projector HAP blend to Watchout ([forum](https://forum.garagecube.com/viewtopic.php?f=20&t=36303); unverified, and one thread is an anecdote, not a measurement). We do not need that thread to make the decision: D3D11 lands before any pro release (§8).

### 5b. By design, per user

| User | Better than MadMapper by design? | Better than disguise / Pixera / Watchout by design? | What decides it |
|---|---|---|---|
| **3D mapping artist** | **Likely, if the scan works on real materials.** MadMapper's 3D calibration is manual point matching. PlayMap has structured light, self-calibration, ICP to LiDAR, 3D auto-blend and UV-anchored work in code, tested only on simulators | **Narrower in scope until mid-term.** They have field hardening, multi-camera solves and mesh deform. PlayMap's joint multi-view solve is in Core with no verb yet (ROADMAP M3) and mesh deform is R4.7 | Time to a good image on a tested webcam; robustness on bad materials; a mesh-deform and export round trip |
| **Corporate AV tech** | **Yes, once it runs:** multi-node sync, content distribution, projector control, STATE | **Not until** redundancy and framelock exist (Show Mode, preflight and the node's side of Companion are built). Then cost wins. Projectors' own autocal also competes here (§3b) | Mirror backup, a pre-show health page, re-align after a bump in ≤60 s |
| **Permanent install / museum** | Yes: scheduler, unattended re-check and drift watch are planned | Close to OmniCal recal and Atlas with calibration as a service (R4.8, built in large part as `CALIBRATION` and `ALIGN`) | Nightly check with a score gate, boot to show, fleet view |
| **VJ / club** | **No.** MadMapper, Resolume and HeavyM win on effects, audio, MIDI learn, LED/DMX and live ergonomics (PlayMap has ISF chains, audio modulation, `BIND LEARN` and LED output, but not their depth or the instrument layer) | n/a | Interop first (Spout, OMT, NDI, OSC/MIDI), then the instrument layer |
| **Touring / broadcast programmer** | n/a | **No** until framelock, 10-bit output, ST 2110 and redundancy exist (timecode chase is built) | LTC chase as a fitted anchor; NVAPI swap groups; mirror failover |
| **AI / automation integrator** | Possibly: we found no MCP or AI layer in MadMapper | Possibly: disguise has an AI assistant; we found none in Watchout or Pixera; Resolume reportedly ships local MCP. **PlayMap's risk gate has never gated a live show** | Safe, staged, risk-gated actions proven in rehearsal |

**Bottom line.** By design, PlayMap should beat MadMapper for 3D artists, corporate AV and installs, and lose to it for VJs. It reaches the high end only on calibration accessibility, recovery without rework and automation, and only after the show-floor work in Rounds 2–4. Until Round 1 runs on hardware, every "better" in this table is a hypothesis. Our public message should say exactly that.

### 5c. Design claims that need care

| Claim | Where it holds | Where it does not |
|---|---|---|
| Backup node is a live mirror | Files, generators, `SET`s scheduled with `AT`, all on both nodes | Live capture, NDI/Spout/OMT inputs (they arrive at different times), web pages, audio analysis, stateful or feedback generators, unscheduled operator edits. These need a "non-deterministic source" badge and a fix ("capture to HAP", or feed both nodes from one router) |
| Failover is seamless | The backup's frames are identical for deterministic sources | Switch time is set by the projector or router re-locking: often 0.5–3 s unless the projector has a seamless backup-input feature (estimate). A Videohub switch is not frame-synchronised |
| Masks survive a rescan | When only a projector moved: by construction (masks live in UV) | When the mesh changed: needs retargeting (R4.7) |
| Glasses pick surfaces precisely | Whole surfaces and snapped nodes | Freehand points. Pointer error, not map accuracy, is the limit (docs/XR.md) |
| A box mapped in ≤2 minutes | A target with a pattern budget (docs/UX.md §5.1) | Not measured. Depends on a camera with manual exposure |

## 6. The market moved in 2026: what that means for us

1. **"Phone scan, surfaces appear" is now a product category.** IllumiBeast, AIMapper, Artemis and Ghost Arcade all launched or updated in 2026 (sources in §3a; details unverified). PlayMap's thesis is right but no longer alone. We must win on three things: fully local, curved surfaces, and multiple projectors from any maker.
2. **MCP is not a moat.** Community MCP servers exist for TouchDesigner, and Resolume reportedly ships local MCP. An MCP server is easy to copy. Our edge is safe action on a live show: risk gates, staging, Show Mode. That edge is only real once it has run a rehearsal.
3. **Calibration is becoming an operational service at the high end.** disguise reportedly added a RigCheck API, out-of-focus camera detection and an OmniCal Score in r32.4 ([disguise news](https://www.disguise.one/kr/node/7691); unverified). VYV keeps a calibration history and applies a new result only within a reference gate ([VYV](https://www.vyv.ca/en/docs/calibration/automatic-calibration-and-real-time-alignment-guide/); unverified). Scalable demos deliberately break and re-run calibration on the show floor ([SVC](https://www.svconline.com/the-wire/scalable-display-technologies-to-showcase-automated-projection-mapping-on-large-scale-mount-rushmore-display-at-infocomm-2026)).
4. **The high end is reaching down.** disguise Designer Pro is $1,500 a year, and software-only X-series tiers were reported (§3a; unverified). Our price advantage is real, but it may be against a cheaper disguise than before.
5. **NDI is useful but proprietary.** Its SDK has its own licence terms. Avolites reportedly removed NDI from Titan, Prism and Ai in Dec 2025 citing licensing changes ([Avolites](https://www.avolites.com/avolites-release-titan-v19-important-information/); unverified). We keep NDI optional either way (§8).
6. **Spatial control of live stages exists, but not for projection mapping.** ETC ships phone and tablet AR in Eos through Augment3d ([ETC](https://www.etcconnect.com/About/News/Eos-gets-Augment3d-with-the-release-of-Eos-v3-0.aspx)). Unreal Engine has VR scouting and a Remote Control web app for virtual production stages (source needed). We found no projection-mapping or media-server vendor shipping headset or glasses look-and-control (as of Oct 2026; not exhaustive). The nearest mapping prior art is prototypes: Spatial Mapper on Snap Spectacles ([write-up](https://rbkavin.studio/insights/building-spatial-mapper-snap-spectacles.html)) and Project Ion on visionOS ([TestFlight](https://testflight.apple.com/join/hzXHAZ4B)). See docs/XR.md.

## 7. What to learn from each competitor

| From | Lesson | PlayMap action |
|---|---|---|
| **MadMapper** | Surface-first speed: draw a quad, drop media, nudge corners while watching the wall with the video cursor ([shortcuts](https://docs.madmapper.com/madmapper/6/2.-the-interface/keyboard-shortcuts)) | Keep a 4-click quad path that needs no camera; draw editing on the projector output |
| MadMapper forum | A thread reports venue mask fixes not carrying across 40 scenes ([forum](https://forum.garagecube.com/viewtopic.php?f=20&t=36417); unverified) | Mapping is shared by every playlist, and the UI says so |
| MadMapper 6 | Some v5 projects reportedly would not open ([review](https://zunayed.com/madmapper-6-whats-new/); unverified) | Versioned project format with migrations; never a one-way upgrade |
| MadMapper | MiniMad and per-output export serve small installs ([MiniMad](https://madmapper.com/minimad); price and sync limits unknown) | Deterministic `EXPORT OUTPUT`; appliance node profile |
| MadMapper | Projector control is reportedly a paid add-on | Projector control ships free, as verbs |
| **Resolume** | The clip grid is the VJ instrument; many rigs pair "Resolume performs, MadMapper maps" over NDI or Syphon ([VJ Academy](https://vjacademy.info/learn/resolume-vs-madmapper)) | Spout, OMT and NDI in, so PlayMap can be the mapper behind Resolume; an optional grid view over playlists; import Resolume Advanced Output XML |
| Resolume | MCP is reportedly used mostly for building rather than during live shows ([blog](https://resolume.com/blog/34484); unverified) | Staged, risk-gated AI edits that are safe live |
| **HeavyM** | The cursor is drawn on the output, so you "draw while looking directly at the projection" ([HeavyM](https://help.heavym.net/hc/en-us/articles/4407417515026-The-Cursor)) | On-wall edit overlay first |
| **Millumin** | Silent Edition: edit without the audience seeing | Blind edit for humans and AI alike |
| **TouchDesigner / Moonshine** | Browser co-editing, 3D mesh warp, blends from projector pose, PJLink vitals ([Moonshine](https://moonshine.software/)) | Use Moonshine's comparison table as a checklist |
| **disguise** | A calibration workflow with a plan, a score, a fallback result and an API; out-of-focus camera detection was among the reported r32.4 additions | Calibration as a service: plan, result, score with reasons, fallback, API, camera preflight |
| disguise | Per disguise docs, Director failover limits editing and Understudy needs matching hardware ([failover](https://help.disguise.one/designer/networking/failover); unverified) | Copy the routing and API; keep live editing working after a failover |
| **Pixera** | Locks that still allow playback; warps that keep their shape when the point count changes ([26.1](https://help.pixera.one/pixera-261-overview-changelog); unverified) | Match these micro-interactions |
| Pixera Hub | Fleet health with EDID and framelock state | Network drawer becomes a fleet view |
| **Watchout 7** | Runners keep playing if the Director dies; spare by node name; optimise on import (reported) | Followers keep playing on leader loss (a design goal, untested); optimise-on-import to HAP keyed by hash (built for HAP and HAP Q: docs/OPTIMISE.md) |
| Any fixed-width frame counter | A 24-bit frame counter at 60 fps wraps every 3 d 5 h 40 m (arithmetic) | Handle counter wrap explicitly; soak for weeks, not hours |
| **Smode / disguise / Watchout** | OpenTrackIO and PSN inputs are standard | One tracking input path, also used for glasses pose |
| **Modulo / Hippotizer / 7thSense** | Non-backward-compatible files and matched builds hurt fleets | Capability negotiation on the wire; N-1 interop in CI |
| **VYV Photon** | Calibration history and a reference gate | Drift watch proposes; the gate decides; history kept |
| **Scalable Atlas** | Keep the surface model; re-solve only the channel that moved | Per-projector re-solve is the default recal path |
| **Panasonic / Epson / Christie autocal** | The projector in the rental truck may already self-align | Read, reset or push projector internal geometry; never fight it silently. Pushing our warp into projector warp engines is a mid-term item (ROADMAP M27) |
| **Lightform** | Its final release removed cloud-dependent features, including login, when the company closed ([Lightform](https://lightform.com/?p=3093)) | Local-first, and say so |
| **ETC Augment3d** | A phone was enough; the physical output was the feedback | Phone first; the projector is the AR display |
| **Open source (MapMap, Splash)** | An unmaintained project is a dead project | Sustainability plan (ROADMAP §5): second maintainer, docs, paid support option |

## 8. Where the critic lenses disagreed, and what we decided

| Question | Positions | Decision and why |
|---|---|---|
| Who do we serve first? | VJ lens: creative breadth. Show-programmer and systems lenses: show-floor discipline | **3D mapping artists, corporate AV and installs first.** Our structural strengths matter most there. VJs are served early through **interop** (Spout, OMT, NDI, OSC/MIDI learn) so PlayMap can be the mapper behind Resolume or TouchDesigner. The instrument layer (ISF, FFGL, audio, Link, pixel mapping) is mid-term; ISF chains, audio modulation and LED pixel mapping are built already, FFGL and Link are not |
| When does D3D11 land? | Systems: v1 critical path. Others: after first light | **First light on GL 3.3; D3D11 flip-model before anything is called a pro release.** D3D11 is needed for smooth multi-output playback, zero-copy decode, native BC6H/BC7 and NVAPI framelock |
| Timeline or not? | VJ and UX: no. Show programmer: touring needs timecode-sectioned shows | **No timeline engine.** TC-locked playlists (items with TC in-points), per-item keyframed `SET` tracks, per-item behaviours and actions. Patterns or QLab owns GO in theatre |
| When does XR start? | XR lens: 2027. UX lens: a phone in weeks | **`PICK`, `HIGHLIGHT` and `CURSOR` in Round 1**, because the desk's on-wall cursor needs them. Registration, roles, the phone app and a laser-pointer pick follow in Round 3, once camera ingest and measured latency exist. Headsets mid-term; glasses with displays long-term. "A phone in weeks" ignored the dependency chain |
| Risk gating on the operator's own GO | Catalogue marks transport High | **Risk depends on the caller.** The operator at the desk never confirms GO; the AI and remote clients are gated |
| Network video default | Everyone wants NDI; OMT is MIT and .NET | **Spout and OMT always available; NDI as an optional plugin through the user-installed runtime.** No single default: an output offers whichever the receiver speaks. Most senders today speak NDI, so we do not push OMT as "the" default |
| HDR | Bundled with 10-bit | **Split.** 10-bit output and a linear FP16 pipeline in Round 2, because blend ramps band at 8-bit. HDR signalling (PQ/HLG) mid-term and demand-driven: most projectors gain little from it (estimate) |
| macOS | VJ lens: half the scene uses Macs | **A macOS desk driving a Windows node mid-term; a macOS engine long-term** |
| Scope | All lenses agree | **Freeze new module scope until the vertical slice runs on real projectors** |

## 9. Codec and IO plan

Rounds and IDs refer to docs/ROADMAP.md. The professional baseline is the HAP family, NotchLC, ProRes and H.264/HEVC, plus NDI. Vendor performance tables (for example [disguise VX 4+ performance](https://help.disguise.one/hardware/performance/vx4plus-performance); figures not re-verified) consistently show GPU-decoded codecs carrying many times more 4K layers than ProRes, so GPU codecs must be the default show path.

| Format or IO | When | Plan | Notes and sources |
|---|---|---|---|
| **HAP, HAP Alpha, HAP Q, HAP Q Alpha** | Round 2 (R2.3); built | Own parser, chunked Snappy, direct BC1/BC3/BC4 upload; YCoCg conversion in the shader | FFmpeg master maps only `Hap1`, `Hap5`, `HapY`, `HapA` and `HapM`, and decodes to CPU pixels (verified: [isom_tags.c](https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavformat/isom_tags.c), [hapdec.c](https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavcodec/hapdec.c)). We parse ourselves to keep compressed textures on the GPU. Spec: [Vidvox/hap](https://github.com/Vidvox/hap) |
| **HAP R (`Hap7`, BC7), HAP HDR (`HapH`, BC6H)** | Round 2 (R2.3); built on GL with the BPTC check | Decode ourselves; BPTC check on GL; CPU fallback decoder (not built: a GPU without BPTC refuses them) | FFmpeg master has no tag mapping or decoder for either; its HAP decoder accepts only DXT1, DXT5, YCoCg-DXT5 and RGTC1 (verified: [hap.h](https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavcodec/hap.h)) |
| **Optimise on import** | Round 2 (R2.4); built for HAP, HAP Alpha, HAP Q and HAP Q Alpha | Background transcode to HAP Q or HAP R, keyed by (source hash, codec, params), synced like any asset, source kept. BC7/BC6H encoding is slow on CPU (estimate), so use a GPU encoder. PlayMap's own GPU encoder makes the BC1/BC3/BC4 families in a worker process; HAP R, HAP HDR and syncing versions between nodes are next (docs/OPTIMISE.md) | Watchout reportedly optimises on import ([formats](https://docs.dataton.com/watchout-7/am/formats_and_codecs.html); unverified) |
| **H.264 / HEVC 8–10-bit / AV1** | Exists (copy-back); zero-copy in Round 2 (R2.2) | Preflight proves hardware decode per node | AV1 hardware decode: NVIDIA RTX 30 and later, AMD RDNA2 and later except Navi 24 (RX 6500 XT / 6400), Intel Xe and later, Apple M3 and later ([HotHardware](https://hothardware.com/news/av1-codec-support-and-importance-explained); not re-verified) |
| **HEVC 4:2:2 10-bit** | Round 2 | Try hwaccel; fall back to CPU with a pre-show warning | Hardware decode on Intel Gen11 / Xe and later, NVIDIA Blackwell, and Apple Silicon; AMD and Qualcomm unknown ([NVIDIA](https://developer.nvidia.com/blog/nvidia-video-codec-sdk-13-0-powered-by-nvidia-blackwell/); Intel and Apple not re-verified) |
| **ProRes, DNxHR, CineForm, APV, DXV** | Round 2 (CPU); long term (GPU) | FFmpeg CPU decode plus an "optimise?" prompt; Vulkan decode once GPU interop exists | FFmpeg 9 Vulkan codecs ([9to5linux](https://9to5linux.com/ffmpeg-9-0-lei-open-source-multimedia-framework-officially-released); unverified). Skip ProRes RAW |
| **NotchLC** | Exists via FFmpeg (CPU); GPU long term | FFmpeg has had an LGPL CPU decoder since 2020, so NotchLC plays today with a layer-budget warning | Verified: [notchlc.c](https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavcodec/notchlc.c). A Notch SDK is needed only for GPU decode or encoding; its licence terms are unconfirmed ([NotchLC](https://notchlc.notch.one/)) |
| **Image sequences and stills** | Exists; new formats in Round 2 | EXR, DPX, PNG, TIFF exist; add AVIF, JPEG XL, animated WebP, HEIF | Keep OpenEXR current for security fixes ([OpenEXR news](https://openexr.com/en/latest/news.html)) |
| **Frame-rate handling** | Round 2 (R2.10) | Per-output refresh choice; cadence report for 23.976/25/29.97/50 fps content on 60 Hz outputs; VRR/G-Sync forced off; mixed refresh rates refused inside one sync group | Our design rule (estimate) |
| **Linear FP16 pipeline, 10-bit outputs** | Round 2 (R2.5) | R10G10B10A2 outputs; linear FP16 internal | Blend ramps need it |
| **HDR signalling (PQ/HLG, scRGB, HDR10 metadata)** | Mid term (M21) | Per-output transfer and nit target | Demand-driven |
| **Equirect, cubemap, domemaster** | Mid term (M14) | Sampler modes of projection-from-viewpoint | Opens the dome market |
| **Spout** | Round 3 (R3.3) | In and out | Current release 2.007.018, 10 Oct 2026 (verified: [releases](https://github.com/leadedge/Spout2/releases)) |
| **OMT** | Round 3 (R3.3) | In and out | MIT; libomtnet is a .NET Standard 2.0 library (verified: [libomtnet](https://github.com/openmediatransport/libomtnet)) |
| **NDI 6.3.x** | Round 3 (R3.3) | Optional plugin through the user-installed runtime; out per output plus multiview; NDI PTZ cameras as scan cameras | The NDI SDK is on the 6.3 line; DistroAV moved to 6.3.0.3 in early 2026 (verified: [DistroAV PR](https://github.com/DistroAV/DistroAV/pull/1398)). Exact current patch unknown |
| **Phone and glasses camera ingest** | Round 1 (R1.9) JPEG over WebSocket; mid term (M13) WHIP/WHEP | Frame envelope with device time, intrinsics and pose | Media path for docs/XR.md |
| **SRT / RIST** | Mid term (M13) | As FFmpeg input URLs | [SRT](https://github.com/Haivision/srt/releases) |
| **Capture cards** | Exists (Media Foundation / avdevice); DeckLink mid term (M13); AJA long term | DeckLink through its COM API, never a nonfree FFmpeg build | AJA's [libajantv2](https://github.com/aja-video/libajantv2) is MIT |
| **Gaussian splats** | Mid term (M14) import as previs; long term playable | PLY, SPZ, glTF | SPZ is MIT ([SPZ](https://github.com/nianticlabs/spz)); Khronos `KHR_gaussian_splatting` ratification reported ([radiancefields](https://radiancefields.com/platforms/the-khronos-group); unverified) |
| **Web pages** | Long term (L10) | CEF as a loose-sync source, excluded from sync groups, sandboxed (§10) | A browser's compositor clock is not our vblank grid |
| **YouTube** | Long term, after legal review | Embed only, player unmodified; **no capture or download** | YouTube's developer policies restrict modifying or obscuring the player and downloading content; warping an embed onto a surface may itself conflict ([policies](https://developers.google.com/youtube/terms/developer-policies); not re-verified) |
| **ST 2110 / IPMX / NMOS / PTP** | Long term (L4) | NMOS IS-04/05 first, then 2110 via Rivermax or Deltacast; PTP-disciplined show time | Keep clock source and IO abstract now |
| **VVC, AV2, LCEVC, Dante AV video** | Skip; watch | — | AV2 has no hardware decode yet ([AOMedia](https://aomedia.org/press%20releases/Alliance-for-Open-Media-Releases-AV2-Codec/)) |

**Rule for every format:** a node never falls back to CPU decode silently. Preflight opens the real decoder at show resolution on each node and reports hardware or CPU, layer headroom and the asset version it will play.

## 10. Legal, licensing and security risks

| Risk | What we do |
|---|---|
| **FFmpeg LGPL inside a single portable exe** | Ship FFmpeg as separate LGPL shared libraries the user can replace; no `--enable-nonfree` and no GPL-only components; licence notices and a source offer in the package |
| **H.264 / HEVC patents** | Prefer OS and GPU decoders. Get legal advice before shipping any software encoder for these codecs |
| **Model licences** | SAM 3 is released under Meta's SAM License ([arXiv 2511.16719](https://arxiv.org/pdf/2511.16719)); review it before redistributing weights. Check the licence of every ONNX export we pin, not only the original model. LightGlue is usually paired with SuperPoint weights, which are commonly licensed for non-commercial use only; use a feature extractor with a permissive licence (for example ALIKED or DISK) for drift watch (licences to confirm). YOLO-World is GPL-3 ([comparison](https://arxiv.org/pdf/2601.22164)) |
| **Calibration patents** | Christie holds patents on the structured-light pipeline ([US10574956](https://patents.google.com/patent/US10574956)), coarse-mesh model correction ([US10403028](https://patents.google.com/patent/US10403028)) and drift correction from reduced pattern sets ([US20200092524](https://patents.justia.com/patent/20200092524)). Domeprojection (now VIOSO) describes its camera auto-alignment as patented ([product](https://static.vioso.com/calibration-software/projectiontools/)). Scalable and others likely hold more. **We need a freedom-to-operate review before a commercial release.** "Detect and propose, never auto-apply" is a product choice, not a legal conclusion |
| **YouTube terms** | See §9 |
| **Prompt injection** | Media file names, metadata, web-page sources and STATE fields that remote clients can write all reach the assistant's context. They are data, never instructions. High-risk verbs always wait for a human. While armed, the assistant can only queue proposals |
| **Web content on a show node** | CEF runs sandboxed in its own process, with no file or device access, and is off by default |
| **Exposed HTTP / MCP on venue networks** | TLS and pairing exist, and every verb that changes something needs pairing. MCP is served by default; the posture to ship is MCP off until enabled, with a firewall profile offered on first run (ROADMAP R3.14) |
| **Unsigned binaries** | An unsigned exe with embedded FFmpeg and ONNX Runtime triggers SmartScreen and antivirus false positives, and corporate IT will block it. Code signing is a Round 0 item. Show machines never auto-update, and an armed node never updates |

## 11. Confidence

- **High:** the PlayMap status in §1–2 (read from the repository); the FFmpeg HAP and NotchLC facts, Spout, OMT and the NDI 6.3 line (verified 12 Oct).
- **Medium:** vendor capability descriptions taken from vendor documentation through search summaries, such as OmniCal's workflow, Watchout's calibration API, VIOSO licensing and Panasonic's kits.
- **Low:** every competitor version number, release date and price in §3 (unverified), the 2026 AI entrants' timings and cloud use, and all operator sentiment from forum threads.
- **PlayMap's own figures:** "±1 refresh without genlock", scan times and every time target in docs/UX.md are **unmeasured design targets**.

A follow-up session with search budget and access to the vendor domains should re-check §3 before any of it is used externally.
