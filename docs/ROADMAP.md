# Roadmap

*A prioritised roadmap for PlayMap. Effort and impact are **estimates**. Evidence for each item is in docs/COMPETITIVE.md, docs/UX.md and docs/XR.md. Time targets live in one place: docs/UX.md §5.0.*

## 0. Where we are, and the rule that governs everything

**Round 3 is the round under way.** Its spine is on main, and its packages build on it in four waves: the installed,
Windows-native node on Direct3D 12 and the desk's guided workflows (Round 3, below; docs/round3/PLAN.md). Today PlayMap is one
portable exe that renders through OpenGL.

**Round 2 is merged.** Every package of its plan (docs/round2/PLAN.md) is on main, built and tested on CI's Linux and Windows
jobs. What it does is below and in each item's **Built** note; what only real hardware or a real show can prove is under **Lab
only**; the fixes it leaves open are under **Open fixes**; its integrator's notes, with the owner's calls and their answers in one place, are in
docs/round2/CARRYOVER.md.

**Merged and tested:**
- The ten modules — assistant, calibration, network, mapping maths, media, scanning, projector control, render, vision and the desk.
- The engine host: `PlayMap engine [headless]` composes the real media, render, network and scan backends; the scan runner, projector control and the assistant run inside it. The desk starts and watches this machine's engine, or attaches to a node on the network over the line wire; its demo engine stays for offline use.
- The round-2 work, built on one spine (the model, the catalogue's rows, the engine's hooks, topics and STATE keys): show safety — Show Mode, locks, panic, the master, the GO fence and preflight, with blind edit (one draft per node, diffed in field words, landed as one undo step at a show boundary), sound on the air and fades and dips on a GO by hand (docs/SHOW.md); the sync group — the leader clock slewed, stepped and held, anchors and the group's air carried to followers with each member's receipt, the group's frame lock, vblank reports and the leader's board with its verdict, the group's air latency (`SYNC LATENCY`, stated or the largest display latency its nodes report) written through every member's outputs, and `SHOW ARM|DISARM ALL` and `PREFLIGHT ALL` across the group (docs/SYNC.md); outputs — the presenter seam, vblank truth and the frame lock, the Direct3D 11 flip-model path on Windows as an explicit, experimental choice (`PRESENT PATH DXGI`; AUTO is OpenGL) with its fuse, and per-output latency held to the slowest display with the node's air latency (docs/OUTPUT.md); the HAP family on the GPU, written straight into the render context's upload arena, and the decode-path ladder — every decoder saying the path its frames come by, VAAPI frames imported by dmabuf and D3D11VA on the renderer's own device under its one lock where the renderer can take them, else copy-back or software with the words why (docs/DECODE.md); OSC, MIDI, Art-Net and sACN input with bindings (docs/CONTROL.md) and timecode chase (docs/TIMECODE.md); Companion — the node's side and the Companion 5.1 module, its actions, feedbacks and presets built from the node's verbs, STATE and every section of its index over the paired line wire, a risky line's key held or pressed twice (docs/COMPANION.md); ISF effect chains (docs/EFFECTS.md); audio analysis, the beat clock and the modulation bus (docs/AUDIO.md); content — the GPU particle field, raymarched fractals, reactive scenes, stings and looks (docs/CONTENT.md); the wall — pixel quads, the edit overlay with each cursor's latency measured to the frame that draws it, the first map witnessed by the render host, the projector's view through its output's lens, the score, the calibration ledger, and re-solving only the projector that moved, from its pins or from a calibrated camera's look at its dots, its pins and pixel quads re-seated and an automatic blend solved again (docs/CALIBRATION.md); camera auto-align of flat screens, with each output's white and level matched, one output rescanned onto the canvas the others show, a verify pass and a score on every run, and a guard that names a projector whose own geometry or edge blend fights the measured correction (docs/ALIGN.md); fleet health and the board, and a hot backup that mirrors its primary's show, files, clock, transports and air to the frame, takes the air over by epoch with a press (`FAILOVER`), the old primary fencing itself, hands the show back (`FAILBACK CONFIRM`) and lets a group's follower take the lead (`FLEET PROMOTE`) (docs/FLEET.md); LED pixel mapping (docs/LED.md); video in and out over OMT, NDI and Spout (Windows) — live inputs received once, with tally, backups and a loss policy, and sends packed on the GPU that follow the air (docs/VIDEO.md); optimise on import — HAP versions of a clip made by the show's rules with PlayMap's own GPU encoder in a worker process, kept beside the source (docs/OPTIMISE.md); facades — a site frame that keeps millimetres in a survey's coordinate system, survey import of OBJ, PLY, glTF, LAS, E57 and XYZ off the control lane (decimated or thinned, one undo step), total-station control points that check the model and give pins their points, long-throw solves with lens and position priors that lock the zoom from the lens when the pins cannot carry it (only a lock the lens put there asked again), sites named by their EPSG system with geodesy from the published series, projector stacks aligned to their master's picture from one camera place and colour-matched through the ledger, blended as one projector, the rest of the building dimmed to a stack's remaining light when a member fails, the light plan and the facade guide (docs/FACADE.md, docs/MAPPING.md); and scans from many places — views added `INTO` a scan from a capture camera, a phone or a folder a tethered camera or a person shoots into, every view of every projector solved together (holding the projectors that did not move, dropping a view that disagrees, carried by a move of the site frame), a depth map per projector, one stage mesh and regions joined across projectors, the scan's views and solve kept on disk and read back after a restart, and its measured regions suggested as surfaces that only a person's `APPROVE` makes (docs/SCAN-MULTIVIEW.md, docs/PHONESCAN.md, docs/wire/multiscan.md); and the desk's controls for that work — a show strip (the Ready light, ARM with no password, PANIC that never opens a dialog, Blackout), every control greyed before the press with the node's own words, door by door (pairing, the fleet, the show's gate with the desk's own blind draft, the locks by what an edit changes), Lock for the show in every object's menu, an attention bar, four drawers, a card for every package's STATE from its descriptor, Add effect, Modulate, Bind, Optimise and Use input entries built from the node's own answers, and previews read from the ring the node names for each view, and from no other file (docs/DESK.md, docs/WIRE.md "Previews").

That work spans items from Rounds 1–4 and the mid term; a **Built** note on an item below says what main has of it, and a **Lab
only** note what waits for real hardware. Camera auto-align and the content package have no item of their own.

**On the desk:** views of its own for this work — the readiness light (the worst of the show's preflight, the fleet and the sync group, the group's part the node's own `sync.light`), the blind chip and its panel (diff, commit now or at the next item, discard, the draft drawn in MAP through the views' own path), Health ▸ Fleet (machine cards with stale and gone colours, the backup's FAILOVER held to confirm), Health ▸ Preflight (a Fix per row), Health ▸ Calibration (score cards with a Fix per term, the history strip's Recall), MAP's quad tool with corner nudges in wall pixels and the overlay on the wall, and the first-light guide (docs/DESK.md). A line a Companion key holds to confirm is held on every desk button that sends it and pressed twice in any menu (3 s of the desk's clock), the transport's Stop, the panic banner's Clear and the master excepted (sent at every press); the views' and the overlay's leases are renewed on the desk's clock.

**Not on the desk:** wire-only — the effects browser and inspector, modulation sockets, tempo, bindings and learn, timecode, LED fixture placement and patching, optimise jobs, live inputs and sends, the phone scan path, align, the facade guide, the sync and framelock tab, the present path chips (`PRESENT PATH`), the per-output panic plans (`PANIC SET`), Companion decks and the content library: their STATE shows on the desk's cards, their verbs run from the cards, the generated right-click entries and over the wire, HTTP, MCP and bindings. The blind draft's ON GO and AT commits run over the wire. The desk has no console yet, so the facade's lines that take the operator's values (`SITE ORIGIN`, `SITE POINTS`, `SURVEY IMPORT`, a `PROJECTOR PRIOR` with a value, `STACK NEW`, `STACK ALIGN`, `STACK MATCH`) and `PLAN LIGHT`, which have no desk control, go over the wire, HTTP, MCP, Companion and bindings; a stack's card holds its list, its failover and its REMOVE. A card's `APPROVE`, pressed twice on a Companion key, is pressed once on the desk (the owner's call). A Recall the node refuses because the target stands as that result already (just after it was recalled) is offered: CALIBRATION HISTORY does not say which result a target stands as. The desk has no confirmation before closing while armed, no PANIC pulse and no master level bar; Simple hides nothing; the PLAY bin is not a panel of its own; a drag the gate refuses still moves on screen while its steps are not sent. The direction is a typed console beside the menu bar and the right-click menus, with every command and feature reached from the menus and never only by typing (wp3-desk-shell, `MenuCoverageTests`).

**CI:** every push builds the whole solution in Release, the desk included, with the analyzers on and runs every test project to its end on Linux (Mesa llvmpipe under Xvfb, FFmpeg 9.0.2 from one build pinned by its hash, no libva and no system OpenAL, as on a bare machine) and on Windows (OpenGL 3.3 from a hash-pinned Mesa llvmpipe beside every test host, since the runner has no GPU; the same FFmpeg build; no audio endpoint; the Windows-only render and media code on WARP: the DXGI presenter, Spout's round trip, the D3D11VA device). The native test doubles — the fake NDI runtime and libvmx, which OMT loads — are compiled on both jobs (on Windows with the runner's LLVM clang), and a double that does not build fails its tests with the compiler's words instead of skipping them (`PLAYMAP_NATIVE_REQUIRED`). The tests' media are made at test time with LGPL-compatible encoders (R0.7). A test whose subject exists on one platform only carries that platform's category (`Category=Linux`, `Category=Windows`) and is skipped on the other with the words (EGL, GLX, dmabufs and VA-API on Linux; DXGI, Spout and D3D11VA on Windows). After the .NET suites the Linux job runs the Companion module's type check, lint and tests — its end-to-end test against the job's own engine — and Companion's module check. Each project's result is counted on its own, so one failure hides no other; the Windows job also publishes the portable exe, as the artifact `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64`. Every CI build is numbered from its run number, less `PlayMapRunBase`, and its commit (README, "Versions"); each job reads the stamp back from what it built straight after the build (and the Windows job again from the published exe's version resource), refuses one that is not this run's, and writes it to its summary (`tools/ci-stamp.ps1`).

**Lab only** (built in code and tested on CI, not yet proven on real hardware or a real show). No lab exists yet (the rule
below), so none of these has run; each is reported not run, never passed. The tests run on Mesa llvmpipe
(under Xvfb on Linux, through WGL on Windows), on the scan, align and stage simulators and on a scripted frame-lock board; OMT runs
for real over loopback, and NDI against a fake runtime the tests compile, on Linux and on Windows; the Companion module runs in
Companion's own host layer against a fake node and the real engine; the NVAPI tier, WASAPI, WinMM, the DXGI flip path, Spout and
D3D11VA are compiled on Linux and run by the Windows CI job, which has no GPU (WARP); VAAPI decode compiles here (no VA driver) and
its dmabuf import half runs on Mesa; a sync group's nodes run in one process on loopback, on a manual clock with synthetic vblanks.
The lab checks still to do:
- GPUs and Windows: a DXGI start, its frame statistics and independent flip on NVIDIA, AMD and Intel GPUs; D3D11VA and VAAPI
  decode imported on real GPUs (and the device lock under a decoder's load); Spout with Resolume and TouchDesigner; HAP layer
  counts per GPU; per-output latency on displays of different lag; a precise Windows thread time — the time-stamp counter's
  frequency from the platform, never a rate measured against the stopwatch, and a thread's cycles at it against its work — on bare
  metal and on a Windows 11 node with Memory Integrity on (Windows then runs as Hyper-V's root partition), with round 3's platform
  package (L22; until then a Windows thread time is its clock ticks, and the survey slice's budget there is proven by the stopwatch
  and shown, not proven, by the ticks, docs/FACADE.md).
- Runtimes and SDKs loaded at run time: a real NDI runtime (the user's own install); the NVAPI tier on a sync board.
- Devices: a real projector, and a Panasonic's answers to the double-correction guard's reads; a real camera scan (from one place
  or many, through a capture camera, a phone or a tethered camera's folder), a camera re-solve and a colour match through a real
  camera; real phones; real LED nodes; the Companion module in a running Companion with a deck.
- Files from others: HAP R and HAP HDR files from other encoders; scanner E57 and LAS files and a total station's export.
- Shows: a projector solved at 100 m; a sync group across machines, and its air latency across nodes whose displays differ; a power
  cut just after a takeover (the epoch kept on disk); a second machine (the video checklist in docs/VIDEO.md among them).

**Open fixes** (the detail of each but the last two is in docs/round2/CARRYOVER.md):
- An optimise pause whose knobs reach the worker before its encode thread has woken for the governor's let-go stays pending, with
  no "paused at frame" row, while STATE says held: the thread's hold waits on an event that a reset before it ran leaves unset. A
  worker that says every let-go it takes closes it.
- `PROJECT SAVE` to a new folder writes the project file alone: the alignment runs and scans kept beside the project stay in the
  old folder, and the next edit finds their files missing.
- `SCAN START` is not refused while an `ALIGN RUN` shows patterns (`ALIGN RUN` refuses a running scan).
- A sound input opens under the capture hub's lock, so a device whose open never returns holds every other open and the hub's
  stop; the open needs a bound.
- No verb accepts a changed fleet pin (preflight's row names `keys/fleet-pins.json`).
- A canvas's own effect chain is stored and not drawn, and STATE says so; its layers' chains draw.
- The desk's throttle sends what it coalesced to the link it holds when it flushes, so a line queued just before the desk attaches
  to another node goes to that node.
- CALIBRATION HISTORY names no `standsAs` per row, so the desk offers a Recall the node refuses.
- On Windows the survey import's lane slice is proven by the stopwatch alone: a tick read bounds a slice the lane thread ran in one
  stretch with no tick charged to an interrupt, so a slice the stopwatch reads over the budget passes on its ticks unproven, and
  the Perf twin's 10 ms fails on a stall inside a slice; the Windows job runs the tick measure and passes both slice tests. A
  precise Windows thread time (L22) closes it.
- A node's first survey import holds the lane 12 to 22 ms in its slices in a Debug build (their first-call compilation), past the
  10 ms slice; Release is not measured cold.
- Whether the Windows job's Spout test takes the real WARP round trip or its fallback is not verified.
- `PANIC REWIND` publishes its lane as Cued before it writes the panic's journal row, so a reader that sees Cued can read the
  journal without it; `PanicTests.RewindCuesTheItemAtItsInPointAtTheFadesEnd` reads it at once and failed one gate run on that.
- Watched, not reproduced or not explained (none is changed to pass): `ClockSlewTests`' 3 h 12 min step, one `FanoutTests`
  failure, `FleetMonitorTests`' 10 s wait for the peer to read live, `HostNetworkTests`' wait for the Patterns link's first clip,
  and `DeskIdentityTests.ANewTokenUnpairsAClientInTheTableAtOnce`.
- The NDI runtime's loader and OMT's libvmx loader end with a search of the system's loader by bare name, which on Windows takes
  in the current directory and `PATH` (`NdiRuntime.Candidates` and `Load`; `OmtSetup`'s resolver, in its `Load`). Loading every
  candidate by its absolute path, as FFmpeg's loader does, closes it.
- `HapSourceTests.NothingAllocatesPerFrameOnceWarm` failed once in Release and has not failed since; its cause is not
  named. Finding the cause and making the test deterministic closes it.

**Next:** round 3's packages (below). On the spine they build the Windows-native node of docs/round3/PLAN.md, the desk's
guided workflows and capture-card pass-through. Every decision the plan put to the owner is answered (its §9), and the round
carries twenty-one packages.

**The rule:** real hardware is the proof, and nothing claims a hardware fact it has not measured. No lab exists this round
(docs/round3/PLAN.md D15), so round 3 goes ahead on what CI, WARP and the simulators prove. Every lab line keeps its script and
pass mark and is reported not run, never passed (the plan's §7). A lab, once it exists, runs those scripts as they stand.

| Effort | Duration |
|---|---|
| S | under 1 week |
| M | 1–4 weeks |
| L | 1–3 months |
| XL | over 3 months |

| Impact | Meaning |
|---|---|
| ★★★ | Blocks real shows, or is the headline differentiator |
| ★★ | Expected by pros |
| ★ | Nice to have |

## Round 3 — Windows-native (the round under way)

*The third build round (`docs/round3/`), numbered apart from this roadmap's Rounds 0–4 below, whose own Round 3 is "Plays with
others". Its plan of record is `docs/round3/PLAN.md`.*

**Decided** (docs/round3/PLAN.md §1.2 and §9, with the owner's answers of 18 October 2026; docs/round2/CARRYOVER.md keeps the
calls with their answers): Direct3D 12 becomes the primary renderer behind an RHI, OpenGL stays the reference backend until pixel
and timing parity, and Vulkan is not carried (D1–D3); a supervising service starts the engine in the show user's session, never as
administrator or in session 0 (D6); every Windows 11 edition from build 26100, Windows 11 Home included and supported unmanaged, with
IoT Enterprise LTSC 2024 recommended for permanent show nodes; the hardware lock where the hardware allows it (on NVIDIA's professional
GPUs with RTX PRO Sync, the present barrier on Direct3D 12 and the swap group on OpenGL) and the software lock everywhere else; builds
unsigned this round, each with a SHA-256 list; the installer from the WiX toolset built from its source, so no paid WiX service is
used; no lab yet, so every lab line is reported not run and nothing claims a hardware fact it has not measured; and all sixteen
packages. The owner's requirements of the same day — the four-click quad that stays and drags, every corner and pin nudged by one
pixel, what each screen shows, simple workflows beside advanced and programming areas, one canvas across outputs, menus and right-click
menus for every feature, draggable and collapsible panels, colour for areas and stages, every mapping step by hand, the assistant in
every step, and fast capture-card pass-through — are designed in docs/round3/design/desk-workflows.md and docs/round3/design/capture.md
and planned as small items inside the sixteen packages and as five more packages, twenty-one packages and about 518 hours in all.
The answers also keep the self-built WiX toolset, which no free installer undercuts, with Inno Setup as the
fallback; load NVIDIA's Aftermath library from wherever the user puts it, never shipped; let a still of a view go to an assistant model
off the machine once a person turns it on; accept the DXGI path's amber with no hardware tier; and keep the typed console, with every
command and feature reachable from the menus and right-click menus (§9). The request to Patterns that its Eye keep at least 256 of a
node's commands where it keeps a cap, and that the Show Bus contract state the cap, is in docs/round2/CARRYOVER.md (call 11).

**Today.** PlayMap is one portable exe. It renders through OpenGL 3.3 core or ES 3.00 on GLFW windows, EGL or a surfaceless context;
on Windows the Direct3D 11 flip model through ANGLE is an explicit, experimental path (`PRESENT PATH DXGI`; AUTO is OpenGL) that no
evidence yet marks as in Independent Flip. The NVAPI frame-lock tier reads a sync board and joins no output. A lost device stops the
drawing after 30 faulting frames while the engine plays on. The engine claims nothing from Windows beyond above-normal thread
priority. Previews reach the desk through four CPU copies. CI publishes a self-contained folder that nothing installs, signs or
supervises; every build carries its version, build number and commit from one source (`Version.props`; README, Versions). On the
desk a half-drawn quad is dropped when the person looks away, only a quad's corner nudges, no output says what it shows, Simple
hides nothing, the panels are fixed columns and about a third of the catalogue's rows are sent by a desk control of their own; a
capture card is read through FFmpeg, outside the video I/O hub, with three CPU copies (docs/round3/research/desk-today.md and
video-today.md).

**Built: the spine (W0, `wp3-spine`).** The spine holds the contracts every package of the round builds on. Every lab line
still waits: nothing in it claims a hardware fact.

- **Catalogue.** Round 3's 28 rows are in the catalogue, one block per package, and so are the new forms of earlier rows. Each
  answers "not available on this node yet" until its package routes it (docs/WIRE.md, Round 3). The Show Bus hello names the
  build the node runs.
- **Core.** The presentation types (`VblankClock`, `PresentLedger`, `PresentClock`, `LatencyPlan`, `PresentPath` with
  `D3D12Flip`) live in Core, so a Direct3D 12 presenter feeds them without Render. Core also holds `GpuApi.D3D12`, PMVB's path
  3, the node's identity and capability records, thread claims that do nothing until a platform takes them, and the one
  `capture:` URI grammar. A refused capture URI is never journalled.
- **Engine seams.** The air observer, the GPU's lifecycle, a shared preview, the machine platform and an arm hook each have a null
  implementation that says why it does nothing, and every node's services carry each of them, so a package's extension sets its
  own; the host points thread claims at the node's platform once at start.
- **Model.** Outputs carry colour bits, HDR and nit target, frames in flight, a watermark and pass-through. Canvases carry a
  span, viewpoints a fit, and the project its guides. A blend's γ above 0 is a person's or the current calibration result's (`Output.HandGamma` tells them apart); 0 draws the
  measured γ. Older files
  read unchanged but for γ, and the load note names each output moved.
- **Seams packages need.** `UNDO TO` with its store and gate, HEALTH's shown and observed frames and GPU phase (−1 and unknown
  until filled), the engine's pid record for the supervisor, and arm hooks with deadlines that `SHOW ARM` names in its answer.
- **Tree.** The Direct3D 12, Windows platform and supervisor projects hold their folders and placeholders. Each hook word has
  its stubs. Round 3's STATE keys have their places, absent until written. Each package has a placeholder fixture and a wire
  page.
- **CI.** The Windows job runs on `windows-2025`. It has WARP and the Agility SDK pinned beside the test hosts. The
  `Backend=D3D12` tests are a leg listed apart, with a dispatch input that runs one filtered leg and uploads its evidence.
  The one Direct3D 12 test makes a WARP device through TerraFX and says its feature level. The runner's answers to M7 and M14
  are in the job's summary. The Linux job runs the Render suite whole in ES 3.00 too.
- **Bench.** `bench/r3-present`, the bench for M1 and M3, is built on the Windows job and waits for the lab (L1, L4).

**Next** (the plan's packages build these in four waves on the spine — the fifteen of the Windows move and the five for the
owner's requirements; GL stays the reference backend through the round and retires once the pixel and timing evidence agrees; at
the round's end the integration pass updates the statuses of the items in the last column):

| Item | Package | Moves |
|---|---|---|
| A rendering hardware interface cut at `GlDevice`, GL behind it with unchanged pixels | wp3-rhi | — |
| Direct3D 12 behind it: the device, queues, fences, memory, DRED and Aftermath, every pixel scene on WARP in CI | wp3-d3d12-core | — |
| The render graph compiled on show change; pipelines compiled at arm and keyed by driver; bindless records; zero allocation on a warm frame, modulated layers included | wp3-d3d12-passes, -2 | R2.8 |
| One HLSL source for PlayMap's programs; user ISF through SPIR-V in a shader worker, a crashing shader quarantined | wp3-shaders | — |
| User ISF effects drawn on the Direct3D 12 device; a GPU-time guard per effect slot; the public ISF corpus counted on the new road | wp3-shaders-2 | — |
| Flip-model presenters on Direct3D 12, Independent Flip proven per output, one queued frame, the frame drawn for its predicted display instant, HDR10 and 10-bit | wp3-present | R2.1, R2.5, M21 |
| The present barrier on Direct3D 12 and the swap group on OpenGL where the hardware allows them (RTX PRO Sync), each output locked by its own evidence; every other output in the software lock, reporting measured phase, never in a locked state | wp3-framelock | R4.2 |
| D3D12VA decode sampled in place, the copy-queue upload arena, Spout through a D3D11 side device | wp3-video | R2.2 |
| The Windows platform seam: QoS, timers, MMCSS, process and GPU priority, keep-awake, the GC's latency mode while armed; the node's identity and drivers in STATE; machine preflight with driver drift | wp3-platform | R2.8, R2.9 |
| A watermark read back by an independent observer; the shown-frame heartbeat; `LATENCY STEP`; one rebuild path for a lost device, rehearsed in CI | wp3-stability, -2 | R1.11 |
| Shared-texture previews into the desk; the desk's UI-stall watchdog and push budget | wp3-desk-gpu | — |
| The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status by shape and word, preview age | wp3-desk-ux | M20 (the night theme) |
| A per-machine MSI on every Windows 11 edition, built by the WiX toolset compiled from its source, unsigned with a SHA-256 list, with a supervising service, the engine in an auto-logged-on show user's session (never as administrator, never in session 0, every machine-level action done once by the installer), the MSI's version rules, an install refused while the show is armed | wp3-installer | R0.7, R4.5 (boot to show), R4.6 (in part: never updating an armed node) |
| The four-click quad that stays when the person looks away, its corners dragged, typed and nudged, a pin typed and nudged | wp3-desk-ux | — |
| Docked, floating and collapsible panes; a menu bar, a palette and a typed console, with every command and feature reached from the menu bar and right-click menus, never only by typing; right-click menus generated from the catalogue; colour for status, identity and stage, never the only cue; the assistant's pane | wp3-desk-shell | R3.11 (the palette) |
| Every corner, pin and warp handle dragged, typed and nudged by one pixel; what each screen shows; the advanced panes (effects chains, modulation and bindings, an area pane per STATE block) | wp3-desk-map | — |
| One canvas across outputs — side by side, stacked, or a double-stacked blend (`CANVAS SPAN`, a pattern drawn across it); every automatic result a layer under the hand's | wp3-canvas | — |
| Guided workflows — two projectors blended or stacked on a flat screen, a film mapped onto a wall so it reads undistorted from the audience, an object with one or two projectors — by hand, automatically or with the assistant; Simple that hides | wp3-workflows | R3.11 (Simple/Pro) |
| Capture cards in the video I/O hub (Media Foundation, DeckLink), no CPU copy where the card and Direct3D 12 allow, the pass-through output with its late sample, the input's phase and its latency measured | wp3-capture | M13 (in part: DeckLink) |

What only real hardware can show — Direct3D 12 against GL on the present ledger, Independent Flip with PresentMon beside STATE, a
Sync wall, real TDRs, a Home node, two projectors on a screen, a capture card's loop-through — is the plan's lab checklist
(`docs/round3/PLAN.md` §7). No lab exists yet, so every line will be reported not run, and AUTO will stay OpenGL on every machine; it
becomes Direct3D 12 on a GPU tier only with that tier's parity record.

## 1. How we chose the order

1. **First light, then smoothness, then interop, then show-floor discipline.** Nothing else matters if the wall stutters.
2. **Serve 3D mapping artists, corporate AV and installs first.** Serve VJs early through interop so PlayMap can be the mapper behind Resolume or TouchDesigner; build the instrument layer mid-term.
3. **Every feature ships as verbs:** a verb with typed parameters, a STATE key, a model field and a `DeskAction`, enforced in CI (`DeskVerbRuleTests`). Each is reached from the menu bar and, where it names an object, from that object's right-click menu, never only by typing (`MenuCoverageTests`, from wp3-desk-shell on).
4. **Measure before we claim.** Publish scan time, RMS, sync offset, layer counts and latency per release. Until then, every "better than" is a hypothesis.

Decisions where the lenses disagreed are in docs/COMPETITIVE.md §8. In short: first light on GL 3.3, then Direct3D 12 behind an RHI as the primary renderer, OpenGL the reference until parity, Vulkan not carried; no timeline engine; `PICK`/`HIGHLIGHT`/`CURSOR` in Round 1 and the phone in Round 3; risk gating depends on the caller; Spout and OMT always available, NDI as a plugin; 10-bit and a linear FP16 pipeline first, 10-bit and HDR10 on the Direct3D 12 presenters in round 3, HLG demand-driven; show nodes on Windows 11 only, a macOS desk driving a Windows node mid-term; no scope freeze.

## 2. Near term: Rounds 0–4

### Round 0: First light (gate)

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R0.1 | Implement the engine seams with the real media, render, network and scan backends; `Program.Main` runs the desk and `engine [headless]`. **Built.** | ★★★ | L | — |
| R0.2 | Merge the desk; the desk talks to the real engine over `EngineLinkClient`; optimistic edits roll back with a reason chip when the engine refuses (docs/UX.md §3.5). **Built:** the desk, on the real engine over `EngineLinkClient`; not the reason-chip roll-back | ★★★ | M | R0.1 |
| R0.3 | One output window fullscreen, one corner-pinned quad, an H.264 clip and a generator, driven by verbs. **Built in code:** `LIGHT WALL`, `SURFACE QUAD`, FFmpeg clips and generators by verbs; not yet run on a projector | ★★★ | M | R0.1 |
| R0.4 | Self-hosted Windows CI runner with NVIDIA and AMD GPUs and FFmpeg; nightly soak counts missed vblanks. **Built in part:** the hosted Windows job runs every test project on WARP and Mesa llvmpipe with the pinned FFmpeg, and builds the native test doubles (the fake NDI runtime, libvmx) with the runner's clang, so the NDI and OMT suites run there as on Linux; no GPU runner and no soak | ★★★ | M | R0.1 |
| R0.5 | Hardware lab: three projectors from different makers (one DLP, one 3LCD or LCoS, one short-throw; at least one install model with internal warp); two nodes (RTX PRO with a sync card, GeForce); two UVC webcams (one with manual exposure, one without); an iPhone and an Android phone; a white box, a curved flat, a textured sculpture; material samples (scrim, glossy, black, glass). **Not yet:** no lab exists (docs/round3/PLAN.md D15; the rule in §0) | ★★★ | M + purchase | — |
| R0.6 | Missed vblanks, present mode and p95 frame time per output in STATE. **Built:** `outputs[].present` (missed vblanks, `missed60s`, the late share), `outputs[].frameMsP95`, `machine.present` (path and dialect), `OUTPUT STATS`, `PRESENT RESET` | ★★ | S | R0.3 |
| R0.7 | Release builds, unsigned this round and named `-unsigned`, each with a `SHA256SUMS` file; a SmartScreen and antivirus false-positive check; the firewall rules written once by the installer; FFmpeg as the LGPL build's replaceable libraries, with notices. Signing is next round's, with an identity the owner chooses (docs/round3/PLAN.md §9 decision 2). **Built in part:** the tests' media are made with LGPL-compatible encoders (OpenH264, Kvazaar, FFmpeg's own MPEG-4 part 2 and FFV1, ProRes, HAP, PCM, AAC), checked for any GPL encoder's mark; the one exception is a 10-bit HEVC clip made by libx265 at test time and never committed, kept by the owner's decision, since no LGPL-compatible encoder here writes 10-bit HEVC. Next (wp3-installer): the unsigned MSI with its `SHA256SUMS`, its firewall rules, and the LGPL FFmpeg under every decode test and inside the MSI; `LicenceTests` on the MSI at the round's integration pass. The SmartScreen and antivirus check waits for a lab (SmartScreen is the plan's L18) | ★★★ | S | R0.1 |

**Exit:** a fresh Windows PC plays a looped clip on a projector from the real desk for 24 hours with the missed-vblank count published (it need not be zero yet).

### Round 1: First real map

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R1.1 | Scan runner (`IScanBackend`): patterns and capture together, frame-ID tags, settle detection, exposure as a multiple of the refresh period; `SCAN START/APPLY` work. **Built:** `SCAN START/APPLY/SURFACE` with frame tags, the settle detector, a shutter-closed black frame and a latency stage, on a capture camera or the simulator | ★★★ | L | R0 |
| R1.2 | `PROJECTOR SOLVE` and `BLEND SOLVE` on real input; results as named immutable snapshots with a fallback; per-projector re-solve that leaves masks and lattices untouched (projector-moved case). **Built in code:** the calibration ledger (immutable snapshots, a fallback) and `CALIBRATION RESOLVE`, from the pins as they stand or, with `CAMERA`, from a calibrated camera's look at only the named projectors: jittered dots matched against where the project says they land, a moved camera told from a moved projector, only the movers' poses solved again with their optics kept (the QUICK rung) and checked on the wall, applied within the gate or staged, their pins and pixel quads re-seated and an automatic blend solved again; masks, 3D surfaces and output warps untouched. In the stage simulator, of three projectors the one knocked 0.8° and 2 cm is found moved and re-solved alone, within 0.3 px p95 of the truth on the wall, in five frames (0.5 s of patterns) (docs/CALIBRATION.md). **Lab only:** a re-solve through a real camera. Next: the LENS, CODED and DENSE rungs (a mover whose pose alone does not fit), re-solving the camera when it moved, a stored reference look, the drift monitor, lens read-back triggers and before/after on the wall | ★★★ | M | R1.1 |
| R1.3 | Projector verbs: `PROJECTOR POWER/SHUTTER/INPUT/LENS/RESETGEOM`; model link Projector → device; geometry reset before every scan; read and set low-latency mode where exposed; verify NEC and Sony drivers. **Built** as `DEVICE POWER/SHUTTER/INPUT/FREEZE/LENS/RAW/PREPARE/IDENTIFY`, `DEVICE ADD … PROJECTOR`, the scan runner readying each projector, and `DEVICE NATIVE`, which reads a Panasonic's own geometry and edge blend (queries only) for the double-correction guard; not the low-latency mode, nor a snapshot, neutral and restore of a projector's own processing. **Lab only:** every protocol against a real projector, a Panasonic's answers to the guard's reads, and the NEC and Sony checks | ★★★ | M | R0.5 |
| R1.4 | Explainable calibration score: RMS plus penalties (decoded %, focus, coverage, overlap under 20%, lens-lock violation), each with a fix link; confidence map projectable onto the object. **Built:** one score with terms, fix lines and A/B/C/F grades in STATE (docs/CALIBRATION.md); the confidence map is a preview view, not projected | ★★★ | M | R1.2 |
| R1.5 | Edit overlay pass: video cursor, selected-surface outline, lattice and mask on the wall, surface names, flash grid; stripped when armed. **Built:** outlines, handles, lattices and cursors through the output's warp, nothing while armed (`OVERLAY`, `CURSOR`); not masks, surface names, pins or the flash grid | ★★★ | M | R0.3 |
| R1.6 | 2D quad path (4 clicks, arrow nudges, promote to lattice keeping its shape). **Built:** `SURFACE QUAD`, `HANDLE`, `SURFACE LATTICE` | ★★★ | S | R1.5 |
| R1.7 | Click-to-surface verbs (`SURFACE FROM-POINT`) on the classical segmenter plus a pinned SAM 2.1-tiny ONNX export (with the previous-mask input) and checksum; licence of the export checked. Text select (`FROM-TEXT`) is mid-term (M30). **Built in part:** `SCAN SURFACE <scan> x y` and `REGION` on the classical segmenter and an optional ONNX model, `ON <projector>` for what one projector lights of a region, and `SCAN SUGGEST`, which offers a scan's measured regions as surfaces, each a proposal only a person approves; no `SURFACE FROM-POINT` row | ★★★ | M | R1.1 |
| R1.8 | Verb-first contract and CI check; verbs for pin warp, masks and pen, coverage, output layout, live inputs, audio volume, vision (select, suggest). **Built in part:** typed parameters on every row, `VERBS` marking each available or not, the live inputs' verbs (`INPUT …`, docs/wire/videoio.md); every line the desk can send is checked against the catalogue in CI (`DeskVerbRuleTests`); no check that every verb has a `DeskAction` | ★★★ | M | R0.2 |
| R1.9 | Engine-served `/scan` phone camera page (JPEG over WebSocket, frame envelope with time, intrinsics and pose). **Built:** the node serves the page on its own certificate under `/scan`; a one-time `PHONE INVITE`; lock-step capture paced by what the phone has seen; the phone as an align camera; a phone's (or any camera's) further views `INTO` a scan, solved together with the rest, and a folder camera for a tethered camera or a person shooting into a folder; the closed browser loop runs the real page in Chromium against a node on Xvfb (docs/PHONESCAN.md). **Lab only:** real phones. Next: coverage shown on the wall (`SCAN COVERAGE … SHOW`), metric scale from a tape or a phone's depth, and the desk's phone path | ★★ | M | R1.1 |
| R1.10 | Render Fade and Dip transitions in linear light. **Built:** scheduled switches mix across the incoming item's window, and so does a `PLAYLIST GO`, `NEXT` or `PREV` by hand, its window opening at the GO's instant; a playlist in a sync group cuts at the GO. Next: the window carried in the leader's anchor, so a group fades together | ★★ | S | R0.3 |
| R1.11 | `LATENCY MEASURE` with the scan camera; per-output latency stored and fed into frame selection. **Built in part:** each output's input lag (`latencyMs`) gives the node's air latency, which frame selection draws for, and holds the faster outputs back whole frames in a delay ring (docs/OUTPUT.md), and a sync group raises every member's air latency to the group's (R2.7); not the camera's measure | ★★ | S | R1.1 |
| R1.12 | `PICK RAY`, `PICK PIXEL`, `HIGHLIGHT`, `CURSOR` verbs (desk on-wall cursor and hover; XR Phase 0). **Built:** `CURSOR`, put on the wall's cursor board as its line arrives (off the control lane), with its latency counted from that arrival to the frame that draws it and published as the p95 over the last 300 moves (`STATE.air.overlay.cursorP95Ms`; two cursors moving 300 times are drawn within two frames at p95 in the tests, with nothing allocated). Next: `PICK RAY`, `PICK PIXEL` and `HIGHLIGHT` | ★★★ | S | R1.5, R1.2 |
| R1.13 | Scan preflight: camera capability probe (manual exposure, focus and white-balance lock, MJPEG, banding risk) and projector-settings card (dynamic contrast, iris, eco, interpolation, auto keystone); a tested camera list published | ★★★ | M | R1.1, R1.3 |
| R1.14 | Per-material fallbacks: every low-confidence surface after a partial scan keeps a manual path (pins, quad, lattice on the frozen camera image); Robust and Difficult scan modes exposed | ★★ | S | R1.1 |

**Exit:** "map a box with one projector and a webcam" (docs/UX.md §5.0) is timed on a tested webcam in an office-lit room with the RMS published, and the §5.1 storyboard is filmed.

### Round 2: Smooth playback

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R2.1 | Flip-model presenters: one swap chain per output, waitable object, frame statistics as vblank truth, verified independent flip, one present thread per adapter. Round 3 builds them on Direct3D 12 (wp3-present); the Direct3D 11 path through ANGLE below is their experimental forerunner and is deleted next round with GL's other paths. GL 3.3 stays the reference backend, with the Linux development loop, until the pixel and timing evidence agrees, then retires (docs/round3/PLAN.md D2). **Built in part:** the presenter seam, vblank truth per output (`VblankClock`, `PresentLedger`), the OpenGL ES 3.00 dialect and the flip-model path (experimental, `PRESENT PATH DXGI`; AUTO is GL): ANGLE on PlayMap's own device, one waitable FLIP_DISCARD swap chain per display output, frame statistics as vblank truth, the present fuse (a start that never reached its first frames runs OpenGL until `PRESENT RESET` or `PRESENT PATH DXGI`), per-output latency and the device's one lock shared with decoders (docs/OUTPUT.md), run on the Windows CI's WARP; not verified independent flip or a present thread per adapter. **Lab only:** a DXGI start, its frame statistics and independent flip on NVIDIA, AMD and Intel GPUs, and per-output latency on displays of different lag | ★★★ | L | R0 |
| R2.2 | Zero-copy decode: hardware-decoded frames sampled in place on the renderer's own device (D3D12VA on the Direct3D 12 device, NV12 and P010 planes through plane views); "zero-copy yes/no" per source in STATE. **Built in part** (docs/DECODE.md): the decode-path ladder with each source's path and why in STATE, the context and preflight; Linux VAAPI → GL by dmabuf (the import half proven on Mesa); D3D11VA on the renderer's device under its lock with one GPU copy a frame, imported through ANGLE (compile-only here; its device opens on the Windows CI's WARP or says why); `DECODE STATUS` and `DECODE POLICY`. **Lab only:** D3D11VA and VAAPI frames imported on real GPUs, and the device lock under a decoder's load. Next (wp3-video): D3D12VA decode sampled in place and the copy-queue upload arena | ★★★ | M | R2.1 |
| R2.3 | HAP family on the GPU: own parser, chunked Snappy, BC1/BC3/BC4/BC7/BC6H upload; HAP R and HAP HDR, which FFmpeg does not decode. **Built** on the GL path with a BPTC check, each frame's second stage written straight into the render context's persistently mapped upload arena (docs/DECODE.md). **Lab only:** HAP R and HAP HDR files from other encoders, and the layers each real GPU sustains | ★★★ | M | R2.1 (a GL path with a BPTC check can come earlier) |
| R2.4 | Optimise on import: background transcode to HAP Q / HAP R keyed by (hash, codec, params), synced like any asset, source kept; GPU BC7 encoder; runs on the desk or an idle node. **Built** for this node: HAP, HAP Alpha, HAP Q and HAP Q Alpha from PlayMap's own GPU encoder in a worker process, planned by the show's rules (`MEDIA OPTIMISE`; on import under `MEDIA RULE MODE AUTO`), keyed by source hash and recipe, verified by PSNR, kept as the item's versions beside the source and played from its next open (docs/OPTIMISE.md). **Lab only:** the encoder on a real GPU, and its trickle beside a show playing on one. Next: HAP R and HAP HDR (BC7, BC6H), each node fetching its own pick, the farm over idle peers, `ASK` with suggestions, the lossless rewriter, estimates and removing versions | ★★★ | M | R2.3, R2.11 |
| R2.5 | Linear FP16 pipeline; 10-bit outputs (R10G10B10A2). HDR signalling is separate (M21) | ★★ | M | R2.1 |
| R2.6 | `PREFLIGHT` verb: open every asset's decoder per node, disk and upload tests, 60 s presentation soak, EDID check, non-deterministic sources listed; per-node stream budget in STATE/MCP. **Built in part:** `PREFLIGHT [QUICK] [WAIT]` and `PREFLIGHT REPORT` with each package's section and a fix line per row, mapping read through the calibration ledger, the show clock, the links and the show's safety; `PREFLIGHT ALL` merges every member of a sync group, one section per node (docs/SHOW.md); not the soak, the upload tests, the EDID check or the stream budget. **Lab only:** `PREFLIGHT ALL` across machines on a show network | ★★★ | M | R2.3 |
| R2.7 | Measured sync: `pattern.syncmarks` plus a camera give inter-node offset p50/p99 and a "sync certificate" in the show report. **Built in part:** the software measure — each member reports its outputs' vblanks to the leader (PMVB on the clock-sync port), whose board gives each output's phase against a reference with its uncertainty, the next slip, the rate in ppm, mixed rates and one verdict, in STATE, the context, preflight and INDEX (docs/SYNC.md); and `SYNC LATENCY`, the group's air latency set by the leader — stated, or AUTO the largest display latency the leader and its members report, a quiet member keeping its place and a new lead waiting for its members' reports — carried on the feed and written through each node's per-output latency rings (`IPresentBackend.SetGroupAirLatency`); not the camera's sync marks, the offset p50/p99 or the certificate. **Lab only:** the board and the group's air latency across machines, on real vblanks from displays that differ. Next: succession lists, the frame code and the sync certificate | ★★★ | M | R1.11, two nodes |
| R2.8 | .NET determinism: zero allocations per frame (test-enforced), GC latency mode during shows, ReadyToRun warm-up, GC pauses correlated with missed vblanks. **Built in part:** tests hold a warm frame to zero allocations; the control lane's per-tick and per-step methods make no closure at their entry (an IL audit in either build) and the air's sound step allocates nothing in Release; modulated layers, surfaces and effect slots still allocate a drawn project each frame, and per-edit checks and some inputs' steps (sACN, the timecode readers) allocate by their nature | ★★ | S | R0.6 |
| R2.9 | `HARDENING` report: power plan, updates, notifications, driver allow-list, HAGS/MPO, independent flip, TdrDelay, disk; offered after the first frame; a fix applied only on a person's approval. TdrDelay is reported, never changed: long GPU work is split instead (docs/round3/PLAN.md D10). On Home no Windows Update policy is written (D4). Round 3's machine preflight (wp3-platform) reads the power plan, a pending reboot, driver drift from `MACHINE LOCK`'s set and the edition's limits as facts with words, and each output's Independent Flip is named by wp3-present; the supervisor applying an approved fix (`MACHINE HARDEN`) is next round's | ★★ | M | R2.1 |
| R2.10 | Frame-rate handling: per-output refresh choice, cadence report for 23.976/25/29.97/50 fps content, VRR/G-Sync off, mixed refresh rates refused within a sync group. **Built in part:** `SYNC APPLY` refuses a node's outputs at mixed rates, and a sync group's board and preflight name a mixed rate across its nodes (red in preflight, with the `SET <output> refreshHz` line that fixes it, to run on the node that owns the output); no cadence report or VRR check. **Lab only:** a group's mixed rates read from real displays across machines | ★★ | M | R2.1 |
| R2.11 | GPU contention policy: inference, transcode and scan decoding prefer the desk or an idle node, run at low priority on a show node in rehearsal, and are refused on an armed node. **Built for transcode:** the optimise worker runs at a lowered CPU priority, trickles while a show plays (a rest after each frame keeps its work to 15 % of the time), pauses after a dropped frame and waits while armed, and `MEDIA OPTIMISE` is refused on an armed node. The node's CPU loops and its heaviest jobs run on Core's compute threads (`Compute`), one fewer than the processors beside a loop's calling thread, never on the shared thread pool the lanes, sockets and timers need: every data-parallel loop under `src/` (a source scan in the Core suite holds them to it, and holds every file's other pool work to a reviewed count); and, through `Compute.RunAsync`, the solves, scan decodes and simulators of the scan, align, resolve, blend, mapping, site and stack work, the stage's automatic blend, thumbnails' conversion and encode, `MEDIA INSPECT`'s and the decode measure's HAP decodes, the scan views' pictures, a probe's first frame, the site preflight check and ONNX inference. What the air waits for is never queued behind a solve that holds every compute thread: still decodes stay on the pool, and the stage's table builder runs on a thread of its own, its loops on it and the compute threads that are free. Not inference or scan decoding at a lowered priority, nor moving work to the desk or an idle node. **Lab only:** the trickle and the pause after a dropped frame beside a real show's load | ★★ | S | R0.1 |

**Exit:** published layer counts per GPU for HAP, HAP Q, H.264 and ProRes; a 72-hour soak across two outputs with zero missed vblanks; a measured two-node sync offset.

### Round 3: Plays with others

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R3.1 | Bindings layer: input → verb or `SET` path with range, curve and soft takeover; right-click **Bind…** learn; feedback capped at 50 Hz; encoder support for fine geometry. **Built** (`BIND`, docs/CONTROL.md), with the desk's right-click **Bind ▸** (`BIND LEARN` on each of the object's own actions; Esc ends a learn) | ★★★ | M | R1.8 |
| R3.2 | OSC in/out with a verb-mirroring address space and OSCQuery; MIDI 1.0; Art-Net/sACN input. **Built:** OSC in and feedback out, MIDI 1.0 in, Art-Net and sACN in; not OSCQuery or MIDI out | ★★★ | M | R3.1 |
| R3.3 | Spout in/out; OMT in/out; NDI 6.3.x as an optional plugin through the user-installed runtime; NDI/OMT out per output plus multiview; NDI PTZ cameras as scan cameras. **Built** (docs/VIDEO.md): OMT and NDI in and out — live inputs received once with health, tally, a backup and a loss policy; sends of an output, a canvas or a source packed on the GPU on the show clock's grid, following the air; Spout in and out on Windows (receive in Spout's order, sends fed by the send stage; run on the Windows CI's WARP; docs/VIDEO.md); not multiview sends, prewarp taps, Spout's GPU send stage or PTZ scan cameras. **Lab only:** a real NDI runtime (the user's own install, loaded at run time; the tests compile a fake one), Spout with Resolume and TouchDesigner on a real GPU, and the video checklist on a second machine (docs/VIDEO.md) | ★★★ | M | R2.1 |
| R3.4 | Companion module generated from `/api/verbs` and STATE, with connection status. **Built** (docs/COMPANION.md): the node's side and the module (`integrations/companion-module-playmap`); not the typed recorder, or Live slots that press projectors, surfaces and faults. **Lab only:** the module in a running Companion with a deck (the tests run it in Companion's own host layer against a fake node and the real engine) | ★★ | S | R1.8 |
| R3.5 | Show Mode (`SHOW ARM`), blind edit with commit at a boundary, panic outcomes, locks, live actions out of undo, risk gating by caller, armed permission table (docs/UX.md §7.1). **Built** (docs/SHOW.md): blind edit is one draft per node, seen in the editor's draft view, diffed in field words with its conflicts named, and landed by `BLIND COMMIT NOW/AT/NEXT/ON GO` as one undo step at a show boundary; a sync group's leader carries `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER` to every member, their fades running from the leader's instant, each member answering with a receipt, and `SHOW ARM/DISARM ALL` to the whole group. Next: a draft per session, soft edit leases, parked edits, `BLIND ON ALL`, a draft view at a chosen size, the draft's own stage meshes in its view and on the commit's frame, a commit that prerolls a clip the draft adds, a draft that survives a restart; a group lead for `BLACKOUT` so members switch on the leader's frame | ★★★ | M | R0.2 |
| R3.6 | Playlist ergonomics: per-item behaviours, on-start/on-end verb actions, load vs take, countdown colours, non-deterministic badge, optional grid view | ★★ | M | R1.10 |
| R3.7 | Timecode chase as a fitted anchor (LTC on audio in, MTC, Art-Net TC, Stagewatch LTC over the Show Bus), explicit freewheel and offset sign, TC-locked playlists, per-item keyframed `SET` tracks. **Built** (docs/TIMECODE.md) but Stagewatch LTC over the Show Bus and the keyframed tracks | ★★★ | M | R3.2 |
| R3.8 | `/remote` PWA: role pages, QR pairing, roles and armed permissions, dark-room show strip, object cards | ★★★ | M | R3.5 |
| R3.9 | `FIDUCIAL FLASH`, `REGISTER`, phone point-and-tap (iPhone freeze-frame, Android WebXR), beam guard | ★★★ | M | R3.8, R1.9, R1.11, R1.12 |
| R3.10 | Laser-pointer pick through the rig camera (XR Phase 0.5) | ★★ | S | R1.12, R1.11 |
| R3.11 | Ctrl+K palette (natural language falls through to a staged assistant proposal), last-action panel generated from verb parameters, universal micro-gestures, Simple/Pro switch. **Built in part:** Simple/Pro is a desk preference (Ctrl+Shift+P) that hides nothing yet | ★★ | M | R1.8 |
| R3.12 | "Bring my setup": import Resolume Advanced Output XML and MPCDI | ★★ | M | R1.8 |
| R3.13 | "Send to designer": per-surface UV templates (PNG/PSD/SVG) with names, sizes and safe areas; re-import by surface ID | ★★ | S | R1.7 |
| R3.14 | Network and AI security posture: pairing required for remote verbs, MCP off until enabled, firewall profile, assistant context treats names, metadata and web content as data. **Built in part:** pairing for every verb that changes something, foreign Origins and Hosts refused on the web port, secrets kept out of lines and the journal; MCP is on by default | ★★★ | S | R3.5 |

**Exit:** PlayMap maps a Resolume Spout feed onto scanned surfaces, is driven from an APC40 and a Stream Deck, chases LTC, and a phone and a laser pointer each select a surface by pointing at it.

### Round 4: Show-floor grade

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| R4.1 | Mirror redundancy **for deterministic sources**: the backup renders the identical frame; fenced leader election; frame-health auto-takeover with a timeout; `FAILOVER` verb; routing by projector input or router; no automatic switch-back mid-show; live editing continues after failover. Non-deterministic sources are flagged in preflight. **Built for a manual takeover** (docs/FLEET.md): the hot backup draws the primary's frame to the byte, `FAILOVER` takes the show by epoch (kept on disk, the rename flushed, before the backup serves) and the old primary fences itself (one that comes back after the takeover sends nothing before it does), routes switch a projector's input with its read-back, `FAILBACK CONFIRM` swaps the two, only the node with the show sends LED data, video and sound; automatic takeover on silence and a succession list are next round, and WARM and COLD backups, frame keys and a router step after them. **Lab only:** the drill on real projectors and a second machine, and a power cut just after a takeover | ★★★ | L | R2.7, R1.3 |
| R4.2 | Hardware frame lock where the hardware allows it; a per-output chip (house-locked, framelocked or grouped, else free with its tier "software" and its measured phase); counter-wrap handling; sync-card firmware caveats in preflight. **Built in part:** the per-output lock machine, `SYNC APPLY/LEAVE/REJOIN`, wrap-safe counters, a sync group's members joining and leaving the leader's lock with its counter anchor through the anchor feed; the NVAPI tier is compile-only and locks no output yet. Next (wp3-framelock): on professional GPUs with RTX PRO Sync, NVAPI's present barrier on Direct3D 12 and the `WGL_NV_swap_group` swap group on OpenGL, each output locked only by its own evidence. The DXGI path (`PRESENT PATH DXGI`) has no hardware tier: a node with a board there is to be amber, with `PRESENT PATH D3D12` or `GL` as the fix; every other output is to run the software lock and report its measured phase, never a locked state (docs/round3/PLAN.md D5). **Lab only:** a sync board | ★★★ | M | R2.1 |
| R4.3 | Fleet view in the Network drawer with 24 h history; `/metrics`, syslog, webhooks; one-click diagnostics bundle. **Built in part:** `HEALTH`, the fleet board (`FLEET`) and the `/health` and `/fleet` feeds on the web port (docs/FLEET.md); not the drawer, the history, `/metrics`, syslog, webhooks or the bundle | ★★ | M | R2.6 |
| R4.4 | Audio device seam (WASAPI exclusive, ASIO), per-playlist channel matrix, LTC out, audio clock as master. **Built in part:** the input side, one capture per input shared by the analyser and the LTC reader (docs/AUDIO.md); the air's sound — the on-air item through one OpenAL Soft output slaved to its anchor, its gain the master × the darkest panic × the playlist's volume, on the picture's show clock (docs/SHOW.md); not WASAPI exclusive, ASIO, the channel matrix, LTC out or the audio clock as master | ★★ | M | R3.7 |
| R4.5 | Scheduler (time of day, holidays, sunset-relative, evaluated by the leader), projector power sequencing with warm-up, boot-to-show service | ★★ | S | R1.3 |
| R4.6 | Versioned wire (capabilities in HELLO) and project format with migrations; N-1 interop in CI; LTS show-safe channel with pinned FFmpeg and a driver matrix; never auto-update an armed node | ★★★ | M | — |
| R4.7 | Rescan retargeting when the mesh changes: NRICP mesh deform with crease stiffness; deviation and lattice-residual heat maps | ★★★ | L | R1.4 |
| R4.8 | Calibration as a service: `CALIB PLAN/CHECK/RESOLVE/ACCEPT/REVERT` on the Show Bus and MCP; reference gate; history of the last 100 results. **Built in large part** as `CALIBRATION` and `ALIGN` on the wire and MCP with a gate; the history keeps 20 results beyond the current, fallback, staged and pinned ones; `CALIBRATION RESOLVE … CAMERA` checks a re-solved pose on the wall, and `ALIGN SCORE` answers a run's score with every term, its seams measured by the run's verify pass. Next: `ALIGN PLAN` (the dry run) | ★★★ | M | R1.4, R4.7 |
| R4.9 | Reference hardware list (GPUs, capture cards, mini-PCs, cameras, projectors known to work) and a venue network guide (wired show network, multicast/IGMP, VLANs, Wi-Fi limits for phones and glasses); mDNS discovery. **Built:** the node announces itself as `_playmap._tcp`; not the list or the guide | ★★ | S | R2.6 |

**Exit:** a failover drill shows frame-identical output on the backup (deterministic sources, checked by camera) and a measured end-to-end switch time. On projectors with a seamless backup input the target is no visible gap; otherwise the target is the projector's own re-lock time, which we report rather than hide. A projector is knocked and re-aligned within the UX §5.0 target with no mask rework. A night-time unattended check runs.

## 3. Mid term (after Round 4)

| ID | Item | Group | Impact | Effort | Depends on |
|---|---|---|---|---|---|
| M1 | Offline planner: projector and lens library; lux, density, stretch, shadow and acuity heat maps; calibration simulation; PDF/CSV. **Built in part:** the light plan (`PLAN LIGHT`, docs/FACADE.md) — each surface's lux at p5/p50/p95, nits, contrast against an ambient, pixel pitch and the lumens short of a target from the projectors as placed, stacked members adding; not the library, the heat maps, the simulation or the exports | Differentiator | ★★★ | L | R4.8 |
| M2 | Content-based drift watch: render the predicted camera view, match it (permissively licensed extractor plus LightGlue-class matcher), per-projector residual, human-approved re-solve | Differentiator | ★★★ | L | R4.8 |
| M3 | Multi-camera capture and parallel multi-projector scanning (footprint probe, non-overlapping projectors at once), seam-aware bundle adjustment. **Built in part:** the joint solve of many views of many projectors, dense depth per projector, the merged mesh and regions across projectors, driven by `SCAN START … INTO`, `SCAN SOLVE [HOLD]`, `SCAN VIEWS` and `SCAN DROP` (docs/SCAN-MULTIVIEW.md); views are taken one after another through one camera, not by many cameras at once, and projectors are scanned one at a time. **Lab only:** a real camera's scan from many places | Parity with high end | ★★★ | L | R1.1 |
| M4 | Art-Net 4/sACN output and pixel mapping sampled in linear light. **Built:** fixtures on canvases, outputs, surfaces and sources sampled on the GPU in linear light and sent as Art-Net 4, sACN or DDP with the latch (docs/LED.md); not world sampling, GDTF/MVR import or `LED DISCOVER`. **Lab only:** real LED nodes | Parity | ★★ | L | R3.2 |
| M5 | LED pixels auto-located by camera (blink-coded, decoded like structured light) | Differentiator | ★★ | L | M4, R1.1 |
| M6 | Audio analysis (FFT, onset, BPM) as a modulation source broadcast with show time; Ableton Link; launch quantisation. **Built:** analysis, onsets, tempo detection, the beat clock and the modulation bus on one node (docs/AUDIO.md); not the broadcast, Link or quantisation | Parity (VJ) | ★★ | M | R4.4 |
| M7 | ISF 2.0 loader as generator and filter; per-surface effect stack; a curated shader library. **Built:** ISF filters in chains on outputs, layers, surfaces and playlist items, ten built in (docs/EFFECTS.md); not ISF generators | Parity (VJ) | ★★ | L | R3.1 |
| M8 | Geometry-aware effects as live instruments (outline chase, relight, sweep driven by beat and MIDI) | Differentiator | ★★ | M | M6, M7 |
| M9 | Export: mesh, UVs and projector cameras to glTF/FBX/USD; UV-stable re-import; finished MPCDI; deterministic `EXPORT OUTPUT` | Parity (3D artists) | ★★★ | M | R4.7 |
| M10 | Quest 3/3S client (Passthrough Camera API), posed headset scan for scale; then Galaxy XR with gaze hover and pinch-the-dots | Differentiator | ★★★ | L | R3.9 |
| M11 | Spatial show-health overlay anchored to surfaces (observer mode, safe while armed) | Differentiator | ★★ | M | M10 |
| M12 | Voice input with deixis bound to the highlighted surface | Differentiator | ★★ | M | R3.9 |
| M13 | DeckLink via COM API, SRT/RIST inputs, WHEP previews, WHIP camera ingest | Parity | ★★ | M | R3.3 |
| M14 | Equirect, cubemap and domemaster sampler modes; Gaussian-splat import as previs | Differentiator | ★★ | M | R2.1 |
| M15 | Defocus-aware blend weights; winner-takes-pixel for low-confidence regions; radiometric bake and gamut overlay | Differentiator | ★★ | M | R1.2 |
| M16 | macOS desk driving a Windows node | Parity | ★★ | M | R4.6 |
| M17 | Multi-desk roles, per-entity soft locks, presence colours, "who changed this" in the journal | Parity | ★★ | L | R3.5 |
| M18 | OpenTrackIO and PSN inputs; tracked props late-latched and predicted to vblank | Differentiator | ★★ | M | R2.1 |
| M19 | Appliance node profile (fanless mini-PC, headless boot-to-show) | Parity | ★ | S | R4.5 |
| M20 | Onboarding: "Light a wall", three demo projects, embedded offline help, night-red theme. **Built in part:** `LIGHT WALL` and `STATE.onboarding`, whose first content is stamped when the render host draws a frame of a quad's content; the scripted first map — `LIGHT WALL`, `SURFACE QUAD`, eight nudges, a clip imported and set on the quad — runs on a headless node with its read-back showing the clip inside the quad, against a 5 s bar of machine time. Next: the demo projects, offline help and the night-red theme | Parity | ★★ | S | R1.6 |
| M21 | HDR signalling: PQ/HLG, scRGB, per-output nit target, HDR10 metadata | Parity | ★ | M | R2.5 |
| M22 | Colour matching across projectors: colorimeter or spectro support, white-point and gamut matching, OCIO. **Built in part:** the white match of camera auto-align (`ALIGN RUN … COLOR WHITE`): each output's primaries measured through the camera, a common white at the largest level every output reaches, and per-output gains in linear light (a camera that sets its own exposure refuses colour with words, and the run's geometry is staged without it); in the simulator a 6 500 K output at full level and a 7 500 K one at 0.8 land within Δu′v′ 0.003 and 3 % of each other (docs/ALIGN.md). **Lab only:** a match through a real camera. Next: FULL colour (the primaries matched, not only white), a colour step on its own, a colorimeter or spectro, and OCIO | Parity (install) | ★★ | L | R1.4 |
| M23 | Facade workflow: long-throw pins at 50–150 m, night scanning, projector stacking with sub-pixel alignment (docs/UX.md §5.6). **Built in part** (docs/FACADE.md, docs/MAPPING.md): the site frame (`SITE`, `SITE ORIGIN`, `SITE MAINS`), every reader rebasing in double, so an OBJ at E 512 345 / N 5 401 234 lands within a tenth of a millimetre; `SURVEY IMPORT` of OBJ, PLY, glTF, LAS (streamed in chunks) and E57 (read in managed code, its pages' CRCs checked) off the control lane, a 2 M-triangle model decimated to 300 k within its reported deviation in one undo step; control points (`SITE POINTS`) that pins and position priors bind to, checked against the model (`SITE CHECK`, preflight's `site` row); lens and position priors (`PROJECTOR PRIOR`) as residual rows, the zoom locked from the lens when the pins cannot carry it, and every solve's uncertainty on the wall in STATE and the score; scans bridged into the stage as placed, a scan's views carried by a move of the site frame, and a folder or tethered camera as the scan camera; geodesy (Krüger's transverse Mercator, UTM, ECEF and ENU within 0.5–1 mm of published points), sites named by their EPSG system from a LAS's GeoKeys or WKT; projector stacks (`STACK NEW`, `STACK ALIGN` p95 1.77 → 0.008 px on the simulator and 0.009 px under a corner pin on the master, a fit that would fold the picture refused, `STACK MATCH` Δu′v′ 0.0107 → < 0.0001, `STACK FAILOVER`), the light plan (`PLAN LIGHT`) and the facade guide (`SITE GUIDE`). **Lab only:** scanner E57 and LAS files, a total station's export and a projector solved at 100 m. Next: LAZ, the night capture profile, measured photometry, glass masks and a camera solved from its own pins | Parity (commercial) | ★★★ | L | R1.2, M1 |
| M24 | MadMapper project import (format undocumented) | Adoption | ★★ | L | R3.12 |
| M25 | FFGL plugin host | Parity (VJ) | ★★ | M | M7 |
| M26 | Laser/ILDA output; LED processors (Brompton, Novastar, Megapixel) in mixed rooms, including genlock with processors | Parity | ★ | L | R4.2 |
| M27 | Push warp and blend into projector internal engines (Panasonic, Epson, Christie, Barco) for no-PC playback and redundancy | Differentiator | ★★ | L | R1.3 |
| M28 | People-aware projection: mask presenters' faces in keynotes (camera/depth segmentation, a generalised beam guard); people tracking for museum interactives | Parity | ★★ | M | R1.7, M18 |
| M29 | Camera-glasses client: Ray-Ban/Oakley Meta via the Device Access Toolkit; head camera as pointer, temple tap, voice | Differentiator | ★★★ | M | R3.9 |
| M30 | Text-prompted click-to-surface (`SURFACE FROM-TEXT`) with a verified SAM 3-class export after licence review | Differentiator | ★★ | M | R1.7 |
| M31 | Localisation of the desk and help | Adoption | ★ | M | R0.2 |
| M32 | Public benchmark: lab scan datasets and simulator scenes so anyone can verify accuracy claims | Credibility | ★★ | M | R1.4 |

## 4. Long term and moonshots

| ID | Item | Impact | Effort | Depends on |
|---|---|---|---|---|
| L1 | **Looks-and-controls with glasses:** XREAL Aura, Vision Pro (pinch-the-dots; enterprise camera build), Android XR AI glasses; Ray-Ban Display HUD remote | ★★★ | XL | M10, M29 |
| L2 | Content-as-fiducial registration: worn cameras stay registered during shows with no flashes | ★★★ | XL | M2, M10 |
| L3 | Imperceptible continuous alignment from complementary-pair embeds at 120 Hz and above; research track | ★★ | XL | M2 |
| L4 | ST 2110 / IPMX via Rivermax or Deltacast, NMOS IS-04/05, PTP-disciplined show time | ★★ | XL | R4.2 |
| L5 | Multi-wearer crews with shared anchors; drift alerts routed to whoever is looking | ★★ | L | L1 |
| L7 | Render-engine integration: frame-numbered texture sharing with the sync anchor for Unreal, Notch and TouchDesigner; NotchLC GPU decode if the SDK terms allow | ★★ | L | R3.3 |
| L8 | Geometry-conditioned AI content: scan, depth and edge maps as conditioning to a pluggable generator, local by default | ★ | L | M9 |
| L9 | Live collaborative editing in a browser (Moonshine-class) | ★★ | L | M17 |
| L10 | Splats as playable content; web pages as a loose-sync source class with "capture to HAP" (never for YouTube) | ★ | L | M14 |

## 5. Non-technical track: sustainability and adoption

These are the biggest adoption risks and no code fixes them. Rental houses and integrators buy support, training and confidence, not just software.

| Item | Why | When |
|---|---|---|
| Second maintainer and a documented release process | A single-maintainer project has a bus factor of one; MapMap shows what unmaintained looks like | Before the first pro release |
| Documentation site with workflows, the hardware list and the network guide | Bring-your-own-hardware needs a buyer's guide | Round 4 |
| Support offer (paid SLA option), training and a certification path | What rental houses and integrators actually buy | After Round 4 |
| Funding model | Pays for the lab, signing certificates and support | Decide before Round 4 |
| Freedom-to-operate review of calibration patents (Christie, Scalable, VIOSO/domeprojection and others) and model licences | "Detect and propose" is a product choice, not a legal conclusion | Before a commercial release |
| Community: forum, example projects, public benchmark (M32) | Trust comes from reproducible numbers | Ongoing |

## 6. Skip or watch

- AV2, VVC, LCEVC, ProRes RAW and 4D splats: no hardware, or a camera/OTT concern (docs/COMPETITIVE.md §9).
- Dante AV video natively: ingest through capture cards instead.
- A full timeline engine: replaced by TC-locked playlists and per-item keyframes.
- L6, a macOS engine (Metal, VideoToolbox zero-copy, Syphon): show nodes run on Windows only (docs/round3/PLAN.md D4, §8.1). A
  macOS desk driving a Windows node stays (M16).
- Per-frame neural compensation on show nodes: lab-grade and single-projector ([CGF survey](https://onlinelibrary.wiley.com/doi/10.1111/cgf.70288)).
- Automatic content-based correction without human approval. Christie holds patents in this area, including drift correction from reduced pattern sets ([US20200092524](https://patents.justia.com/patent/20200092524)). We detect and propose, and a human approves; whether that is clear of any patent needs the review in §5.

## 7. Summary by group

**Parity gaps that block real shows (must-have):** R0 integration and the release build (R0.1–R0.7; unsigned this round, named so, each with its SHA-256 list); scan runner and solves (R1.1, R1.2); projector verbs (R1.3); scan preflight and fallbacks (R1.13, R1.14); flip-model presenters on Direct3D 12 (R2.1); GPU HAP (R2.3); preflight (R2.6); frame-rate handling (R2.10); Show Mode (R3.5); bindings and OSC/MIDI (R3.1, R3.2); Spout/OMT/NDI (R3.3); timecode chase (R3.7); security posture (R3.14); redundancy (R4.1); framelock (R4.2); format versioning (R4.6); multi-camera capture (M3) for large rigs; the facade workflow (M23) for commercial mapping. Main has R0.1, R1.1, R1.2, R1.3, R2.3, R3.1, R3.2, R3.3, R3.5 and R3.7 in code (each but what its row names), and R0.4 (the hosted Windows job, no GPU runner), R0.7 (the tests' media, not the unsigned MSI), R2.1, R2.6, R2.10, R3.14, R4.1 (a manual takeover), R4.2 and M3 in part, and M23 in part (the site frame, survey import of PLY, OBJ, glTF, LAS and E57 with decimation and thinning, control points, long-throw priors, geodesy, stacks, the light plan and the facade guide; not LAZ, the night profile, photometry or glass masks); what they lack is proof on real hardware.

**Differentiators where PlayMap can lead:** commodity-camera calibration with an explainable score (R1.4, R4.8); the edit overlay drawn on the wall (R1.5); pick-and-highlight verbs (R1.12); click-to-surface (R1.7, M30); optimise to HAP R/HDR (R2.4); measured sync and latency (R2.7, R1.11); laser and phone pointing (R3.10, R3.9); importers and designer templates (R3.12, R3.13); live-mirror redundancy for deterministic content (R4.1); rework-free recalibration (R1.2, R4.7); drift watch (M2); LED auto-locate (M5); geometry-aware instruments (M8); staged AI on a live show (R3.5 + assistant); camera glasses and headsets (M29, M10). Main has R1.4, R1.5 and R4.8 in large part, R1.2's rework-free re-solve of a moved projector from a camera (its pixel quads re-seated; not R4.7's retargeting to a changed mesh), R2.4 in part (HAP and HAP Q, not HAP R or HDR), R2.7 in part (the vblank board and the group's air latency, not the camera's certificate), R1.11 in part (each output's input lag held to the slowest display, not the camera's measure), R4.1 for a manual takeover, and camera auto-align of flat screens, with its white match, beside them.

**Moonshots:** L1–L3. Glasses as the way to run a mapped show, and a show that keeps itself aligned.

## 8. Measures of done, per release

Each is measured against the canonical targets in docs/UX.md §5.0 and published:
- Time to first light; time to map a box with a tested webcam.
- RMS per solve on the lab objects; the public benchmark (M32) once it exists.
- Two-node sync offset p50/p99.
- Layer counts per GPU and codec.
- 72-hour soak missed vblanks.
- Failover: frame identity and end-to-end switch time.
- Bump-to-realigned time.
- Laser, phone and pinch-to-outline latency p50/p95.

Published numbers count as features. They are what a show programmer asks for first.
