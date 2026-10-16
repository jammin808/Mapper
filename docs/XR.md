# XR: look at an area and control it

*The smart-glasses goal: look at an area and control it. Device and API facts carry source URLs; **(verified)** means re-read from a primary source on 12 Oct 2026, **(unverified)** means it comes from the research pass and could not be re-checked. Accuracy and latency figures marked **estimate** are our judgement.*

## 1. The goal and the honest answer

**Goal:** someone wearing smart glasses looks at an area to be mapped and controls it: selects a surface, puts content on it, fixes a corner, re-aligns a projector, or makes an unmapped object a surface.

**Answer: this is buildable, and PlayMap's design suits it unusually well. It comes after the core works, not instead of it.**

- **PlayMap has no XR code.** Some prerequisites are in place: the engine runs with its real backends, the scan runner scans through a capture camera or the simulator, `CURSOR` draws a cursor on the wall through the edit overlay, and Show Mode decides each line by the caller's role (operator, remote, assistant, observer). Missing: a pick or highlight verb, roles for XR clients, and any run on a real projector.
- **The first gate:** one projector scanned, solved and showing content on a real object. Until then there is no projector-to-surface map to point at.
- **The user's literal ask includes uncalibrated areas.** "Look at an area to be mapped" can mean an area nobody has scanned yet. §5.4 designs that flow: look, say "scan this", and the projector covering it scans while the glasses or phone camera watches.
- **We found no projection-mapping or media-server vendor shipping headset or glasses look-and-control** (Oct 2026; not exhaustive). Spatial control of live stages does exist elsewhere: ETC ships phone and tablet AR in Eos through Augment3d ([ETC](https://www.etcconnect.com/About/News/Eos-gets-Augment3d-with-the-release-of-Eos-v3-0.aspx)), and Unreal Engine has VR scouting and a Remote Control web app for virtual production stages (source needed). Mapping prototypes include Spatial Mapper on Snap Spectacles ([write-up](https://rbkavin.studio/insights/building-spatial-mapper-snap-spectacles.html)) and Project Ion on visionOS ([TestFlight](https://testflight.apple.com/join/hzXHAZ4B)).
- **The design decision:** the projector is the AR display. The glasses are a pointer, a small menu and, where allowed, a posed camera. PlayMap outlines the selected surface on the real object. That feedback is exact by construction and needs no rendering on the glasses, so it works on display-less camera glasses, monocular HUDs and headsets alike.

## 2. Why PlayMap's design fits

Once a projector is calibrated, PlayMap holds a dense map: projector pixel → 3D point on the mesh → surface ID and UV. Every look-and-control interaction reduces to getting a ray or a camera pose into PlayMap's world and intersecting it with that map.

| Ingredient | PlayMap | MadMapper / Resolume / HeavyM | disguise / Pixera / Watchout |
|---|---|---|---|
| Calibrated 3D model of each projector | Built and tested on simulators (scan runner, solvers, calibration ledger); never on hardware | No (2D, or manual point matching in MadMapper) | Yes |
| Surfaces as named objects with UV-anchored masks and warps | Yes | Partly | Yes |
| Open control surface a new client can use | Verbs, STATE, MCP; the desk has no private API | Partly (Resolume REST) | Yes: APIs and variables |
| Ray-pick and highlight verbs for thin clients | Designed (§5.1); `CURSOR` built | No | Not found |
| Pose solver that registers any camera seeing projected light | Built (DLT/PnP/P3P + RANSAC) | No | Inside their calibration |
| Local segmentation for "make that a surface" | Built (classical + SAM-class when a verified model is present) | No | Not found |
| Deterministic frames, so content can act as a fiducial | Built | No | Unknown |

The high end has the world model and APIs but keeps the operator at a workstation. The prosumer tools have nothing to ray-cast against. PlayMap's specific addition is the pick-and-highlight verb set on top of the model.

## 3. Device and API landscape (October 2026)

| Device | Status and price | Optics | What a third-party app can get | Role for PlayMap |
|---|---|---|---|---|
| **Phone (Android Chrome)** | Shipping | Screen | WebXR immersive-ar, hit test, anchors, depth; raw `camera-access` ([caniuse](https://caniuse.com/wf-webxr-camera), [spec](https://immersive-web.github.io/raw-camera-access/)) | **Phase 1.** Live 6-DoF cursor plus camera |
| **Phone (iPhone Safari)** | Shipping | Screen | No WebXR AR ([xrdoctors](https://xrdoctors.pro/blog/webxr-on-ios-what-actually-works); unverified); getUserMedia works, but exposure and focus cannot be locked reliably | **Phase 1.** Freeze-frame tap mode |
| **Camera glasses without a display** (Ray-Ban Meta, Oakley Meta) | Shipping | None | Camera through a native iOS/Android phone app using Meta's Wearables Device Access Toolkit ([FAQ](https://developers.meta.com/wearables/faq/); verified via Meta docs). No 6-DoF tracking | **Phase 1b.** Head camera as the pointer: PnP against projected content gives the head pose (§5.3) |
| Other camera or HUD glasses (Mentra Live, Brilliant Labs Frame/Halo, Rokid, RayNeo X3 Pro, XREAL One Pro, Even Realities G1/G2, Vuzix Z100) | Various | Various | Not researched; camera and SDK access unknown | Check before committing |
| **Meta Ray-Ban Display + Neural Band** | $799 including the band; 600×600 display in the right lens only; about 20° FOV (FOV unverified) | Monocular HUD | Web Apps in developer preview since about May 2026: **no camera or microphone** in Web Apps; input arrives as directional navigation and activation (arrow keys and Enter, handled by the browser as spatial focus navigation); an opt-in pinch-and-drag mode sends continuous pointer events; DeviceMotion/DeviceOrientation IMU data; GPS from the paired phone; no raw EMG or custom gestures. Camera is available only through the native Device Access Toolkit ([Web Apps build](https://wearables.developer.meta.com/docs/develop/webapps/build/), [input](https://wearables.developer.meta.com/docs/develop/webapps/build/input-and-navigation), [price and display](https://glasskit.app/docs/meta-ray-ban-display-sdk-explained)) (verified) | **Parallel track.** HUD remote: cycle and confirm; pinch-and-drag for nudges |
| **Meta Quest 3 / 3S** | Shipping | Video passthrough | Passthrough Camera API on Horizon OS v74+: camera intrinsics, extrinsics, pose and precise timestamps (verified: [Meta sample](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)). Up to 1280×960 per camera, up to 30 fps, about 40–60 ms latency, Store publishing allowed since about v76 ([Meta PCA](https://developers.meta.com/vr/documentation/spatial-sdk/spatial-sdk-pca-overview/); unverified). Needs the headset-camera permission. **No eye tracking** | **Phase 2, first headset.** Cheapest device with camera, pose and intrinsics |
| **Samsung Galaxy XR (Android XR)** | Reported $1,799, Oct 2025 (unverified) | Video passthrough | `android.permission.EYE_TRACKING_FINE` ("eye gaze for selection, input, and interactions"), a runtime permission reached through OpenXR or Unity, **not** the Jetpack XR SDK. **No documented app API for raw passthrough camera frames**; only a camera-state extension. Scene understanding (planes, raycast, depth) is available (verified: [permissions](https://developer.android.com/develop/xr/permissions), [OpenXR extensions](https://developer.android.com/develop/xr/openxr/extensions)) | **Phase 2.** Gaze hover. Registration by pinch-the-dots or marker, not camera frames, until Google documents camera access |
| **XREAL Project Aura (Android XR)** | Announced at Google I/O 2025 for a 2026 launch; retail name, price and ship date unknown | Optical see-through, about 70° FOV (unverified) | Android XR stack; same camera gate as Galaxy XR; eye tracking unknown | **Phase 3.** First realistic see-through glasses target |
| **Apple Vision Pro (M5)** | From $3,499 (256 GB, US), Oct 2025 ([Apple](https://www.apple.com/newsroom/2025/10/apple-vision-pro-upgraded-with-the-powerful-m5-chip-and-comfortable-dual-knit-band/)) | Video passthrough | Eye data is never streamed to apps; gaze hover is drawn by the system, and the app gets a targeting ray or the targeted entity only when the user pinches. Main-camera access is an Enterprise API needing an entitlement and an Apple licence file, distributed in-house, not on the App Store. Scene reconstruction meshes are available with world-sensing permission (verified: [enterprise APIs](https://developer.apple.com/documentation/visionos/building-spatial-experiences-for-business-apps-with-enterprise-apis), [privacy](https://developer.apple.com/documentation/visionos/adopting-best-practices-for-privacy)) | **Phase 3.** Pinch-the-dots registration; optional enterprise camera build |
| **Android XR AI glasses** (Samsung, Warby Parker, Gentle Monster) | Fall 2026, audio and camera models first ([Android Authority](https://www.androidauthority.com/samsung-google-android-xr-glasses-warby-parker-gentle-monster-google-io-2026-3668380/); unverified) | None or small display | Jetpack Projected and Compose Glimmer ([Glimmer](https://developer.android.com/jetpack/androidx/releases/xr-glimmer)); third-party camera access unknown | **Phase 3+.** Voice + head camera + projector feedback |
| **Snap Specs (consumer)** | Reported $2,195, fall 2026 (unverified) | Optical see-through | Lenses only (TypeScript/JS); camera plus internet triggers permission alerts and an LED ([Snap](https://developers.snap.com/spectacles/permission-privacy/overview)) | Later; a separate codebase |
| Pico 4 Ultra Enterprise, Varjo XR-4, Magic Leap 2, RealWear | Various | Various | Enterprise-only or unknown camera access | Do not target now |

**The gates.**
1. **Third-party camera frames:** open on Quest (with permission); via a phone app on Ray-Ban/Oakley Meta; enterprise-only on Vision Pro; **not documented on Android XR** (Galaxy XR, Aura); absent in Ray-Ban Display Web Apps; sandboxed on Snap.
2. **Gaze:** continuous with permission on Galaxy XR (OpenXR/Unity); a ray only at the pinch on Vision Pro; none on Quest 3/3S, which use head or hand rays.

**Engines.** StereoKit is a C#/OpenXR engine for Quest and Android ARM64 ([GitHub](https://github.com/StereoKit/StereoKit)) that could run PlayMap's own `MeshRaycaster`, `CameraModel` and `PoseSolver` on the device; Android XR support upstream is unconfirmed. Vision Pro needs a Swift/RealityKit client. OpenXR spatial entities, including marker tracking, are in the 1.1 registry ([Khronos](https://www.khronos.org/blog/openxr-spatial-entities-extensions-released-for-developer-feedback)).

## 4. Prior art and its lessons

| Prior art | What it did | Lesson |
|---|---|---|
| ETC Augment3d (Eos) | Phone and tablet AR for a lighting console; the physical beam is the feedback | A phone is enough. The physical output is the best feedback. A printed marker is the always-works fallback |
| Unreal VR scouting and Remote Control | Headset and tablet control of a virtual production stage (source needed) | Spatial and tablet control of live stages is accepted practice; ours controls a real surface rather than a virtual scene |
| Spatial Mapper (Snap Spectacles) | Pinch the corners of real projector light; place shapes | The appeal is real; accuracy is limited to headset tracking. **Use the glasses to point and structured light to measure** |
| Touch Projector (CHI 2010) ([PDF](https://www.medien.ifi.lmu.de/pubdb/publications/pub/boring2010chi/boring2010chi.pdf)) | Touch a remote display through live phone video | Unstable until the frame **froze on touch** |
| Laser-pointer interaction with projector-camera systems | A long line of research uses a camera to find a pointer dot on a projected surface (sources needed) | The cheapest look-and-control needs no app at all (§5.2) |
| "Gazeify Then Voiceify" (IUI 2026) ([arXiv](https://arxiv.org/abs/2601.19281); figures not re-verified) | Look, pinch, the system describes the target, correct by voice | Reported: gaze alone right in about half of trials, voice fixing more than half of the rest. Always highlight a candidate and require a separate commit |
| Apple visionOS HIG ([buttons](https://developer.apple.com/design/human-interface-guidelines/buttons), [eyes](https://developer.apple.com/design/human-interface-guidelines/eyes)) | 60×60 pt minimum hit region; target centres at least 60 pt apart (verified) | Gaze targets are coarse; design for whole surfaces |
| Quest reconstruction projects ([QuestRealityCapture](https://github.com/t-34400/QuestRealityCapture)) | Record passthrough frames with poses and intrinsics | The data path for "headset as scan camera" is proven |
| 4Wall Vision Pro field test ([4Wall](https://www.4wall.com/blog/2024/09/04/exploring-the-future-of-production-tech-apple-vision-pro-in-lighting-and-video-design)) | Remote-desktop into Resolume's GUI | Awkward. Build native spatial clients |

## 5. Architecture

### 5.1 Core verbs

Every XR device is another client of the Show Bus, HTTP/WS and MCP. It adds a **pointer** (a ray in PlayMap's world) and sometimes a **camera** (posed frames).

| Verb | Returns or does | Round |
|---|---|---|
| `PICK RAY <origin> <dir> [t]` | `{surface, uv, projector, pixel, distance, candidates[]}`, ranked so clients can cycle | 1 |
| `PICK PIXEL <device> <x> <y> [t]` | Same, from a pixel in a registered camera frame (the rig camera, a phone or glasses) | 1 |
| `HIGHLIGHT <surface\|node> [style] [ttl] [AT t]` | Outline on the real object via `effect.outline`, timed to the predicted vblank. Refused while armed | 1 |
| `CURSOR <device> <ray>` | Continuous on-wall cursor; the same overlay pass as the desk's video cursor | 1 |
| `FIDUCIAL FLASH <projector\|all> [ms]` | ChArUco grid on calibrated surfaces for 100–300 ms; returns the projector-pixel ↔ 3D table | 3 |
| `REGISTER <device> {correspondences\|rays\|marker}` | Stores `T_world←deviceWorld`, RMS error and an anchor ID | 3 |
| `POSE <device>` stream | Timestamped head pose; also OpenTrackIO or PSN | 3 |
| Roles | `xr-observer`, `xr-rehearsal` (+ HIGHLIGHT, FIDUCIAL, content), `xr-operator` (+ warp, pin and mask edits). Armed behaviour follows docs/UX.md §7.1: operators drop to transport-only, everyone else to observer | 3 |

`PICK`, `HIGHLIGHT` and `CURSOR` give the desk its on-wall cursor and hover highlight, so they are Round 1 desk work (ROADMAP R1.12), not a side project. `CURSOR` is built, as `CURSOR <output> <x> <y>` (0–1 across the output) rather than a ray; `PICK` and `HIGHLIGHT` are not.

### 5.2 Phase 0.5: point with a laser, no device

The scan camera already in the rig sees the wall. A €10 laser pointer or a phone torch makes a bright dot. The engine finds the dot in the camera frame and calls `PICK PIXEL` with the rig camera's own calibration: no registration, no app, no Wi-Fi.
- Dot appears → the candidate surface outlines → a click on the pointer's remote button (or a dwell of 1 s, estimate) commits.
- It exercises the whole pick → highlight → cycle → commit grammar before any phone or headset code exists.
- Limits: needs a calibrated rig camera, rehearsal lighting, and the dot must be distinguishable from content (blink detection).

### 5.3 Registering a device to the rig

| Method | How | Accuracy (**estimate**) | Needs |
|---|---|---|---|
| **A. Projected fiducial + PnP (primary)** | The engine flashes ChArUco through calibrated projectors; each detected corner has a known 3D point. `PoseSolver` with RANSAC gives the camera pose, composed with the device's camera-to-head extrinsics | Roughly 1–3 cm at 4 m once timing, rolling shutter and extrinsics errors are included; better when the wearer holds still | Camera frames + intrinsics: phones, Quest, Ray-Ban/Oakley Meta via toolkit, Vision Pro enterprise |
| A′. Headset Gray code | Short Gray-code sequence decoded by the existing scan decoder; dense correspondences. Also works before projectors are calibrated | Similar; 2–4 s at 30 fps | Same |
| **A″. Content as pose (camera glasses)** | No 6-DoF tracking on the glasses: solve the head-camera pose per frame by PnP against what PlayMap knows it is projecting, or against a brief fiducial flash in rehearsal. The image centre becomes the head ray | Surface-level picking (estimate) | Camera glasses (Phase 1b) |
| **B. Pinch the numbered dots** | The engine projects 4–6 numbered dots; the user aims and pinches each; point-to-ray solve, gravity-constrained to 4 DoF | Several cm at 5 m: enough to pick surfaces, not to calibrate | Any device that gives a ray (Vision Pro without enterprise, Galaxy XR) |
| C. ICP of the headset scene mesh | Refines B, or re-acquires after relocalisation | Coarse; no published study found | Quest scene mesh, visionOS reconstruction, Android XR scene understanding |
| **D. Printed marker** | ArUco/QR marker on the rig, pinned once in PlayMap (OpenXR marker tracking, ARKit image anchor) | Good | Any device; the always-works fallback |
| E. Content-as-fiducial during shows (research) | Render what a camera at pose P should see and match it (LightGlue-class matcher with a permissively licensed extractor) | Unknown until built | Camera frames |

**Error sources that the estimates must include:** camera-to-pose time alignment, rolling shutter, motion blur, camera-to-head extrinsic error, exposure saturated by projection, and tracking loss in dark venues. Every registration reports its error as a number ("±2 cm here").

**Persistence.** Store the result as a device spatial anchor plus PlayMap's transform. Re-validate each session with one flash or one pinch. For crews, use visionOS shared coordinate spaces and Quest colocation so every wearer shares one frame ([WWDC25 318](https://developer.apple.com/videos/play/wwdc2025/318/)).

### 5.4 Interaction model

- **Selection = ray ∩ surface map.** Always show a candidate before acting. Gaze is coarse: Apple's 60 pt minimum target is roughly 2.5° (our conversion, estimate), about 44 cm on a facade 10 m away. Glasses pick whole surfaces and snapped nodes, never freehand points.
- **Disambiguation is the core of the UX.** Swipe, or say "next" or "previous", to cycle ranked candidates; each lights up on the wall. Pinch or tap commits. Nothing acts on gaze alone. Destructive verbs never appear on hover ([WWDC25](https://developer.apple.com/videos/play/wwdc2025/303/)).
- **Verb ring after commit:** Content, Next, Blackout (hold to confirm), Brightness ±, Nudge, Re-solve. The same verbs and the same object card as the desk.
- **Fine editing.** Snap to the nearest lattice node, mask vertex or pin, drawn large on the wall. Pinch-hold plus head or hand rotation drives a damped, rate-controlled nudge along the surface; on Ray-Ban Display, pinch-and-drag pointer events drive it. Edits land in UV space.
- **"Scan this" for an uncalibrated area.** The wearer looks at an unmapped area and says "scan this" (or taps). The engine finds which projector's frustum contains the ray, using the projector's rough pose from the planner or a previous solve. That projector runs a short Gray-code sequence while the glasses or phone camera captures it (method A′). Click-to-surface proposals appear on the wall, refined with "more", "less" and "done". The rig camera, if present, refines the result for precision.
- **Where no projector covers the gaze point,** the projector cannot highlight anything. The device overlay (or voice on display-less glasses) says "No projector covers that" and shows the nearest frustum edge.
- **Voice with deixis.** "Put the water loop on *this* and the one *next to it*." The assistant context gains a `focus` field: the highlighted surface, its candidates and the last 10 s of targets. Every proposal still passes `CommandGate`. Speech-to-text runs on the device or locally by default.
- **Ray-Ban Display** has no camera in Web Apps and no 6-DoF pose. Selection works by cycling candidates that the projector highlights in turn; navigation cycles, activation confirms.

### 5.5 Feedback: projector vs device

| | Projector outline on the object | Overlay on the device |
|---|---|---|
| Works with | Every device, including display-less glasses and a crew radio | Passthrough headsets and see-through glasses |
| Accuracy | Exact by construction | Depends on registration and display calibration |
| Shows the invisible | No | Yes: frusta, blend zones, shadows, pins, residuals, the next projector's placement |
| Audience-safe | No: rehearsal and blind only | Yes |
| Latency | p50 50–90 ms (estimate, §7) | One device frame (11–16 ms at 90–120 Hz) |

**We use both.** A local cursor gives instant response; the projector outline confirms ground truth. **While the show is armed, projector feedback is off**; only device overlays or the HUD remain. Bright projection can saturate passthrough cameras, so overlays use outlines, not fills.

### 5.6 Dark venues

Mapping happens in the dark, where inside-out tracking, hand tracking and passthrough degrade.
- In rehearsal, project a low-contrast tracking texture or a marker band onto non-show surfaces, and offer a `WORKLIGHT` verb.
- Show a tracking-quality chip on the device.
- When SLAM is lost, fall back automatically to camera-frame PnP (A, A″) or to cycling candidates with no pointing at all.
- Printed markers on the rig (D) work whenever there is any light on them.

### 5.7 The glasses as the scan camera

What a headset adds: **metric scale** (board-free self-calibration is up to scale; SLAM baselines fix it), **many posed views** that fill occlusions a tripod webcam misses, and **one device** for scan, registration and control.

Constraints: Quest frames are up to 1280×960 at up to 30 fps; rolling shutter means the wearer must hold still (a "hold" HUD and settle detection); whether manual exposure works on Quest passthrough cameras is **unknown** and is the first test of Phase 2. Android XR offers no documented camera frames, so Galaxy XR and Aura cannot be scan cameras today.

**Decision:** headset capture provides coarse geometry, scale, coverage and registration. Headset poses enter bundle adjustment as **soft priors**. A tripod webcam or phone stays the precision camera for final calibration and drift watch.

### 5.8 Media path

- Round 1: phone and glasses frames as JPEG over the WebSocket with an envelope `{device, t_device, t_leader, K, distortion, T_world←camera, exposure}`.
- Mid term: WHIP ingest and WHEP previews.
- Geometry (decimated mesh, surfaces, frusta, blend weights) streams as glTF over the WebSocket, so headsets render overlays locally.

## 6. Phased plan

ROADMAP IDs in brackets. Time targets are in the canonical table, docs/UX.md §5.0.

| Phase | What | Exit criterion | Depends on |
|---|---|---|---|
| **Gate** | One webcam, one projector, one box: scan → solve → surface → content | Done on real hardware with measured RMS | Round 1 |
| **0. Core verbs** (R1.12) | `PICK RAY/PIXEL`, `HIGHLIGHT`, `CURSOR` in the edit overlay pass | Desk mouse drives an on-wall cursor; `PICK` returns the right surface on the lab box | Gate, R1.5 |
| **0.5. Laser pointer** (R3.10) | Rig camera finds the dot; pick, cycle, commit | Laser pick ≤2 s per surface | Phase 0, R1.11 |
| **1. Phone, zero install** (R3.8, R3.9) | `/remote` PWA; roles and armed permissions; `FIDUCIAL FLASH`, `REGISTER`; iPhone freeze-frame tap; Android Chrome WebXR cursor with `camera-access`; beam guard | Phone pick targets met; pick accuracy logged | Phase 0, R1.9, R1.11, R3.5 |
| **1b. Camera glasses** (M29) | Ray-Ban/Oakley Meta via the Device Access Toolkit phone app: head camera as pointer (A″), temple tap to commit, voice | "Look, tap, it lights up" works in rehearsal lighting | Phase 1 |
| **2. Passthrough headset** (M10) | Quest 3/3S client (StereoKit first if it performs, else Unity/Spatial SDK): head/hand ray, pinch, overlays of frusta, blends and pins, AR placement of the next projector, posed walk-around scan. Then Galaxy XR with gaze hover and pinch-the-dots registration | Register ≤1 s on Quest; headset pick target met; headset scan fixes scale within 1% (estimate) | Phase 1, glTF stream |
| **3. Glasses and Vision Pro** (L1) | XREAL Aura, Vision Pro (pinch-the-dots; optional enterprise camera build), Android XR AI glasses | A real rehearsal run from glasses by a non-developer | Phase 2 |
| **Parallel** | Ray-Ban Display Web App HUD remote | Transport and cycle-confirm without the desk | Phase 1 |
| **4. Looks-and-controls** (L2, L5) | Content-as-fiducial during shows; drift alerts routed to whoever is looking at the surface; multi-wearer crews with shared anchors; "look and say" authoring | A drift alert raised, routed and approved without anyone at the desk | Drift watch, roles, shared anchors |

## 7. Latency budget

| Stage | Estimate |
|---|---|
| Ray sample + pinch detection on the device | 10–30 ms |
| Wi-Fi to engine | 3–15 ms; spikes of 50+ ms on busy venue Wi-Fi |
| Engine `PICK` against precomputed maps | <1 ms |
| Render on the next vblank | 0–16.7 ms at 60 Hz |
| Projector input lag | Model-dependent: roughly 15–30 ms in a low-latency mode on some models; 30–100 ms or more on install projectors doing internal warp or processing |
| **Pinch → outline on the object** | **p50 about 50–90 ms; p95 up to about 150 ms or more on busy Wi-Fi or slow projectors** |
| Quest camera frame | About 40–60 ms (unverified) |
| Fiducial registration | About 0.3–1 s including flash, capture and PnP |

Fine for select and confirm; too slow for direct dragging. Drags show a device-local cursor while projector updates stream behind it. Rays carry device timestamps, and the engine times `HIGHLIGHT` to the predicted vblank, the same clock discipline as multi-node sync. PlayMap is to read and, where the protocol allows, set projector low-latency mode, and to use measured per-output latency (`LATENCY MEASURE`, which needs a camera; R1.3, R1.11).

## 8. Risks

| Risk | Mitigation |
|---|---|
| **Latency feels sluggish** | Local cursor first, projector second. Publish measured pinch-to-light p50/p95 in STATE |
| **Gaze is often wrong** | Candidate highlight, cycling, separate commit, voice deixis bound to the highlight |
| **Platform churn** (camera APIs enterprise-only, undocumented or in preview) | Phone and laser first. Every device is a thin client on one protocol; no logic lives in a client that the engine cannot do. Android XR camera access is treated as absent until documented |
| **Dark venues** | §5.6 |
| **Registration error** | Glasses pick surfaces and snapped nodes; structured light stays the measurement; error shown as a number |
| **Physical safety** | **Passthrough headsets are never worn on truss, ladders, lifts or stage edges**; there, use a phone, camera glasses or see-through glasses with a clear view. Plan for battery life, heat and comfort over a 4-hour focus call: swap devices, keep a phone as backup. A guard-rail reminder appears when a headset session starts |
| **Privacy** (camera frames of a venue and people) | Frames are processed on the engine or device, never in the cloud by default, and are ephemeral unless saved as a scan. "Camera registration in progress" shows on the wall during capture. Respect platform rules: Quest headset-camera permission, Android XR scene-understanding consent ([Android XR](https://developer.android.com/develop/xr/openxr/extensions/XR_ANDROID_scene_meshing)), Apple's private gaze, Meta's camera LED on glasses |
| **Security** | QR pairing shown on the desk, per-device keys, TLS on the WebSocket/MCP gateway, role-scoped verbs, audit log of every XR verb |
| **Live-show safety** | Armed: permissions per docs/UX.md §7.1; fiducial flashes, highlights and headset scans refused; high-risk verbs queue at the desk; edits staged and committed at a boundary. The operator role lapses when tracking is lost or the device is removed. A client dropping off the network has no effect on playback |
| **Eye safety** | Beam guard: a ray–frustum test of each tracked head against every projector. Rehearsal: soft-mask around the head or dim that projector, warn, log. **Armed: warn the operator only; never blank a projector automatically.** Configurable per projector with a hazard distance. Complements laser hazard-distance practice; does not replace it |
| **Over-promising precision** | Never say "millimetre selection". Selection is surface-level; accuracy claims wait for measurements |

## 9. What to build first

1. **Pass the gate:** one calibrated projector on a real object (ROADMAP Round 1).
2. **Edit overlay pass** in the output stage: cursors, outlines, names, lattice nodes, TTL and source tags, stripped automatically when armed (R1.5). The desk needs it anyway. Built: outlines, handles, lattices and cursors through the output's warp, nothing while armed; not names or tags.
3. **`PICK RAY`, `PICK PIXEL`, `HIGHLIGHT`, `CURSOR`** (R1.12), tested against the scan simulator before hardware. `CURSOR` is built.
4. **Measured per-output latency** (R1.11), so highlights are timed correctly.
5. **Laser-pointer pick** with the rig camera (R3.10). The first look-and-control anyone can try.
6. **Roles and Show Mode gating** for remote clients, plus the beam guard (R3.5, R3.8). Show Mode's gate by the caller's role is built; XR roles and the beam guard are not.
7. **`FIDUCIAL FLASH` + `REGISTER`,** reusing `PoseSolver` (R3.9).
8. **Engine-served `/remote` PWA** with freeze-frame picking and QR pairing (R3.8, R3.9).
9. **Then camera glasses (Meta toolkit), then Quest 3, then Galaxy XR,** then XREAL Aura and Vision Pro. Ray-Ban Display runs in parallel as a HUD remote.
