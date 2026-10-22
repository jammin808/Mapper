# Round 2: carry-over and the integrator's notes

**Round 2 is merged:** every package of [`PLAN.md`](PLAN.md) is on main. Round 3 builds on it, toward the installed,
Windows-native node on Direct3D 12 and the desk's guided workflows, with no lab this round
([`docs/round3/PLAN.md`](../round3/PLAN.md)). This file keeps what each integration found, from the round-1 integration's
carry-overs to the last fix batch's notes, oldest first. What stays open of it is in
[`docs/ROADMAP.md`](../ROADMAP.md) ("Open fixes", with "Lab only" for what only real hardware proves); the owner's calls and their
answers are here, in one place.

## The owner's calls, answered

Calls 1–11 are answered, each in the owner's words of 18 October 2026, call 4 also with their choice of 19 October 2026; call 11's
answer is a figure that follows round 3's reply to the owner's question (its §9 decision 18). The plan
([`docs/round3/PLAN.md`](../round3/PLAN.md)) records each answer as a decision taken (its §1.2 and §9) and what it changes.

1. **The show nodes' Windows edition** (the plan's D4). *Answered:* "PlayMap also needs to run on Windows 11 Home as well." Every
   Windows 11 edition from build 26100 is supported — Home, Pro, Pro for Workstations, Enterprise, Education, IoT Enterprise — with IoT
   Enterprise LTSC 2024 recommended for permanent show nodes; on Home the supervisor starts the engine in an auto-logged-on local show
   account in place of a kiosk, and preflight names each layer Home lacks.
2. **The hardware lock's hardware** (D5). *Answered:* "Use hardware lock where possible, fall back to software lock." On NVIDIA's
   professional GPUs with RTX PRO Sync, the present barrier locks outputs on Direct3D 12 and the swap group on OpenGL (AUTO's path this
   round), each output locked only by its own evidence; a capture input's phase is measured, never called locked; and every other
   output is in the software lock: the same frame, its phase measured, never a locked state.
3. **The signing identity** (§9 decision 2). *Answered:* "Leave unsigned for now." Builds and installers are unsigned and named so,
   each with a SHA-256 list; signing is next round's.
4. **The WiX maintenance fee** (§9 decision 3). *Answered:* "Do not use WIX paid services." No official WiX binary, package, fee
   or paid service is used. *Then:* "Use WIX if no cheaper alternative. Are there no other free installer packages?" There are —
   Inno Setup and NSIS — and both build EXEs; none is cheaper than WiX built from its source, which costs nothing. M17 measured that
   WiX's pinned source (v7.0.0) does not build on the Windows runner: it needs Visual Studio's v143 C++ tools, and the runner's
   Visual Studio 2026 has v145 only. That source checks the fee's EULA gesture in three places; PlayMap never passes it, and no patch
   to the source was made. *Then, on 19 October 2026:* "Keep Inno Setup (Recommended)." Inno Setup builds this round's installer, an
   EXE with no automatic undo of service and registry changes, no repair and no `msiexec` for IT, and an MSI is revisited next
   round. Inno Setup's author asks commercial users to buy a licence, a request the owner may meet.
5. **The lab: its machines and who runs it** (§9 decision 11, §7). *Answered:* "Full physical hardware testing is not available yet."
   Every lab line keeps its script and pass mark and is reported not run; every question only the lab answers stands on its "if no"
   branch, said in STATE.
6. **The round's size** (§9 decision 1). *Answered:* "All 16." The sixteen packages stay in their waves, and five more for the owner's
   requirements of the same day are in the round (*Answered:* "Yes"): twenty-one packages, about 518 hours.

The desk's held-line exemptions (the desk-2 notes, below):

7. The desk sends `VIDEO STOP` (the transport's Stop), `PANIC CLEAR` (the panic banner's Clear and its fade menu) and `MASTER` at
   every press, though a Companion key holds each to confirm (`DeskAction.SentAtOnce`). *Answered:* "Keep." The desk's menu bar,
   palette and console keep the rule.
8. A card's `APPROVE`, pressed twice on a Companion key, is pressed once on the desk. *Answered:* "Approve." It stays one press, on the
   cards and in the assistant's pane.

The other calls the notes below leave with the owner:

9. The controllers preflight rows carry no fix line: no verb pairs a client or restarts the network announcement (wp2-companion,
   in the fx merge's notes). *Answered:* "Leave rows as information for now." The rows stay as they are, with their lights (amber
   "not paired", red "not announced", grey "none") and their words, and no fix line.
10. `/api/context` sits behind the token, so Patterns reads PlayMap's context over the wire (`SUBSCRIBE CONTEXT`); opening it is
    the owner's call (wp2-fleet). *Answered:* "Keep it closed."
11. Which commands Patterns' Eye sees: the context lists the verbs that move a panic, the air, a transport or Show Mode first,
    the rest in catalogue order, and the Eye keeps the first 64 (wp2-control). *The owner asked:* "If PlayMap uses up to 128 commands,
    shouldn't Patterns use 128 too?" *The plan's answer, back to the owner* (round 3's §9 decision 18): PlayMap's context lists every
    mutating verb the node answers — 195 in the busy golden (`tests/PlayMap.Engine.Tests/Fleet/Golden/context-busy.json`), the 65th of
    them `MOVE <id> <index>`, and about 215 once round 3's rows are routed — so a cap of 128 would still drop about 87. The Show Bus
    contract (Patterns' `docs/SHOWBUS.md` §6) names no command cap; the 64 lives only in the two readers' code, Patterns'
    `src/Patterns.Core/Services/ShowContext.cs:99` (`.Take(64)`) and PlayMap's own `src/PlayMap.Core/Bus/ShowContext.cs` (`Parse`).
    *The owner answered:* "Use at least 256, but only where necessary." A cap lives only where a reader needs one: PlayMap's context lists
    every command, as today, and PlayMap's own reader needs none (its 512 KB bound already bounds it, and its commands feed no
    prompt), so round 3's integration pass makes it read every command within that bound. Changing Patterns' reader is Patterns' to do, so it is **a request to Patterns' maintainers**: where the Eye keeps a
    cap, at least 256 — which holds every command PlayMap lists — and the cap stated in `SHOWBUS.md` §6, knowing PlayMap's commands are ordered most-useful first (the panic,
    air, transport and Show Mode rows lead). Round 3's integration pass adds `ContextOrderTests`, which keep the air's verbs first
    whatever rows the round adds; until Patterns' Eye keeps at least 256 commands, or no cap, it proposes the air's verbs and not the guides', canvases' or
    capture's rows, which sort after them.

## Carry-over from the round-1 integration

From the host integration (merged 0dd62d2):
- Sync-group anchors are not carried to followers: AnchorPublished has no verb/push to followers. → fleet/framelock package must add an anchor push (Show Bus or wire SUBSCRIBE) so followers adopt the leader's playlist anchors.
- Starting to FOLLOW steps the node's single leader clock; playing playlists outside a sync group jump. → slew the clock (rate-limited offset adoption) or rebase non-group anchors on adoption.
- Lead/follow role and follow-blackout switch are not persisted across restarts. → NodeSettings.
- FETCH sends no token and pulls over plain HTTP; a token-protected peer must trust this node's address. → authenticated pulls (token header or HMAC) and HTTPS with pinning.
- STATE.sync.offsetMs now means leader-time uncertainty (half RTT); document in WIRE.md (done?) and the desk.
- MCP client count approximate (sessionless clients).
- Render host starts on a pool thread; fine for our own render thread; macOS main-thread GL would need another hook.
- Desk reads STATE air.pattern as a string; it is an object. → desk fix.

From the stage integration (merged):
- Fade and dip transitions play as cuts → implement in render (two sources crossfaded by the transport's transition window).
- A playlist nested in a playlist shows black.
- BLEND SOLVE runs on the control lane and blocks it → move off-lane with progress, apply as one edit at the end.
- Shown VIEWs never expire → view leases with heartbeat from the desk (expire after N s without renewal).
- No stings exposed by the engine → RenderHostOptions.Impulses hook exists; wire STING/IMPULSE verbs (content package).
- Display latency is one option, not per output → per-output latency (framelock/present package).
- Headless 'dropped' measured against project fps → amber on slow machines; measure against the output's own refresh target.
- Projector view: no lens distortion, grey background.

From the ops integration (merged c836c6b):
- Real capture path untested on hardware; Phone and Folder cameras refused → phonescan package adds Phone; Folder (image set) camera for offline scans.
- A capture camera that fails to open leaves its view blank with no words → words in the view/STATE.
- Scan pictures held in memory only → a scan from before a restart is refused → persist scan captures (decoded maps) in the project folder.
- Regions and smart scan refer to the first projector only → per-projector.
- Real scan without a known distance is not metric → scale from a known distance, a measured baseline, a tape measure pin, or a phone LiDAR/ARCore depth.
- No verb for assistant settings; ASK waits for the whole model turn → ASSISTANT SET …; streamed ASK replies over WS.
- StagewatchProposalSink not switched on (StagewatchLink already collects approved lines; must set CollectApproved=false) → fleet/showsafety decides.
- Risk is per verb: power-on and shutter-open are High too → per-argument risk (RiskOf(line)).
- Credentials accepted only from this machine.
- Devices extension binds UDP 4352; conflict degrades notifications only.
- Headless camera readback lands ~8 ms before the frame's present stamp → render: stamp present time after GPU finish consistently.
- FIXED on main: LINK PATTERNS/STAGEWATCH tokens and CREDENTIAL SET secrets journalled (VerbSpec.SecretFrom + VerbCatalog.Redacted).
- MCP approval shows line.Raw in proposals (a token an AI proposes would show on the approval desk) → redact display, keep the real line for execution.

From the polish pass (merged into main):
- Transitions on a manual GO/NEXT/PREV still cut (the lane would need to keep the outgoing item and sync followers agree on the window) → showsafety/framelock or playback follow-up.
- Desk has no screens for STATE solve, cameras, ai.assistant; FakeEngineLink doesn't answer ASSISTANT verbs → desk packages.
- Gate change: AI-proposed lines with secret words (LINK … token) now pass the gate marked, always wait for a person, are high-risk on MCP, and are shown redacted (was: refused outright).

Round 2 W1 notes:
- wp2-fx: chains on playlist items, layers and surfaces draw through the spine's pass hooks (IFramePass.Item, Layer,
  Surface; an item's chain starts at the item's leader start; PassFrame.Covered/Held say per output whether a pattern, safe
  look or hold is up — SPINE.md). Open: a canvas's own chain is stored but not drawn (needs a canvas call point after the
  canvas is composed).
- wp2-fx edited Assistant.Tests/CatalogueTests to read a named list of trimmed rows (TrimmedRows) — every package that trims rows must add to it.
- wp2-fx needs audio's MOD BIND route for effect bindings (stretch, next round).
- wp2-show: McpTools and AssistantTools should ask IShowDecisions (new Core seam, implemented by the engine) before running/queueing a line (companion owns McpTools). WireSession infers transport "desk" from HELLO name PlayMapDesk — any paired client claiming that name is treated as the operator (companion to harden). A backup mirroring mapping while armed (W3 backup) needs a way past the armed lock for fleet sessions. STATE.show refusal count and panic levels only update when STATE's revision moves (stale between). Pinned-count tests break as packages extend: OpsHostTests (extension count 3), CatalogueTests (209 round-2 rows; fx changed it to a TrimmedRows list; show kept BLIND rows unrouted to keep 209), VerbSyntaxTests (SHOW SET forms), EngineRouterTests (show/fx/output changed key list to a prefix check) — reconcile at merge.
- wp2-decode spine amendments: VideoFrameLease needs a virtual white level/HDR scale (today via IHapPicture in Core/Media/Hap); a clip-source interface (SetLoop, Opened) that EngineSources uses instead of type-checking FfmpegVideoSource (HAP sources don't loop natively — they wrap by seeking, holding the pre-loop frame a few ms; a failed HAP open is not retried); a shared GLSL include for PQ/HLG/tone map (hap program carries its own copy). SnappyBlock (own allocation-free decoder) is 2x slower than Snappier in Debug — measure in Release. Upload arena (stretch) not built.
- wp2-output spine amendments: headless outputs show red because their window is not open — their light should come from present.light (StateWriter/ContextBuilder); ShaderConventionTests and GlCapsTests should read GlFixture.Dialect and build.yml needs an ES-leg step (PLAYMAP_GL_DIALECT=es300, 2 spine tests filtered today); decide companion's base INDEX 'outputs' section vs output's 'present'. OML sync control not built. outputs[].dropped now = late presents in last 10 s past 0.5% budget.

## Integrator notes from the fx merge (facts for every later package)

- Every GLFW window (output windows, the effects compile lane's shared context, any future second context such as a Spout/NDI
  reader thread or a D3D11 interop window) is made through `RenderContext.MakeWindow(options, primary.EglWindows)`: it holds
  the process-wide hint gate and asks for the primary's API. A window asked across APIs, or made while another thread holds the
  EGL hint, makes `glfwCreateWindow` return NULL, and Silk.NET then calls `glfwSetWindowPos` on it, which aborts the whole
  process (Ubuntu's GLFW asserts). Regression test: `tests/PlayMap.Render.Tests/Effects/FxLaneContextTests.cs`.
- Never run `--blame-crash` with dumps on this box (a mini dump is ~3 GB and starves the disk gate); read stacks with
  `dotnet-dump analyze <dmp> -c "clrstack -all"` and delete the dump at once.
- PlayMap's own code names no licence (the owner deleted LICENSE and asked that MIT be named nowhere for PlayMap). Never
  write MIT or any licence for PlayMap's own code, built-in content or modules; never add a LICENSE file or licence metadata
  for it. Third-party notices stay. The one exception is the Companion module (decided by the owner): its source
  `package.json` "MIT" (Bitfocus requires it), its manifest "GPL-3.0-only", LICENSE with the MIT text ("Copyright (c) 2026
  jammin808") and the GPL-3.0 text for the packaged module (design/companion.md, research/companion-licence.md).
- wp2-companion (merged d3f8e9c): spine amendments wanted — SessionInfo.Channel (worked around via the client table),
  JournalRow.Transport/Session (RECORD next round), ExtensionSetup carrying the TLS identity (mDNS TXT tls/fp), a dispose hook
  for engine modules. Open for others: AssistantTools does not ask IShowDecisions (McpTools does) → assistant owner;
  ShowPolicy.RoleOf makes any client whose HELLO name is in Operators an operator (claim-based, like the old desk rule) →
  show/blind owner, harden like the desk (proof + address); the controllers preflight has no row with a fix line (no verb pairs
  a client or restarts the announcement) → the owner's call 9, answered: the rows stay information. A remote desk now needs
  Companion.DeskAddresses.
- Integrator (after companion): the web port refuses foreign Origins on /api, /ws, /mcp and, for tokenless admission, hosts
  that are not the node's (WebSettings.Hosts lists venue names). Render-thread facts go through FactsSignal (its own thread and
  a semaphore; never the thread pool from the render thread — queueing there can add a worker and allocate). Preflight runner
  reports each finished check (EngineCore.PreflightCheckFinished) for tests that move the clock.
- wp2-audio: open — ComposeAudio runs after ComposeFx (the fx pass sees Modulation null; integrator to order audio first);
  generators draw bound numbers at their base until content's renderers evaluate Generator.Bindings through the frame's
  Modulation (then change the "drawn at its base" words in EngineCore.Audio.cs and Audio/EngineCore.Mod.cs, regenerate
  audio.json; ReactiveRealBusTests audio×content); with bindings the drawn project is rebuilt every frame (allocates; content's
  zero-allocation overlay replaces it); TEMPO METER applies at once (meter into TempoAnchor when anchors are shared);
  AudioInputHub.Open holds its lock while a device opens (control calls Open/Dispose off its lane); the fallback AudioRuntime
  needs the spine dispose hook. LtcThroughCaptureHubTests: written by whichever of control/audio merges second (control).
  Source names: beat = phase in the beat, bar = bar phase (fx binds bar). WASAPI only compiled: watch the Windows CI job.
- wp2-wall: open — PROJECTOR SOLVE's "who" is the verb name (round-one router gives no session); the overlay follows corner
  pin and lattice but not the lens pre-distortion (needs the warp mesh and lens lookup in frame passes, or an Over mode in
  OutputStage); onboarding.contentAt is stamped on the lane (needs a first-content callback from the render host);
  air.overlay.cursorP95Ms not measured; wall.json rounds solver numbers to 3 decimals (watch Windows CI);
  OpsHostTests.ARealNodeWithTheOperationsPartsScansTheSimulatedCameraEndToEnd failed once under load (watch it).
- wp2-fleet: reads at /health and /fleet (web modules, Open, GET/HEAD only, nothing secret; HttpHost refuses modules under
  /api). The hello carries caps eye and show; the context's command list puts panic, air, transport and Show Mode verbs first
  (the Eye keeps the first 64). /api/context sits behind the token, so Patterns reads PlayMap's context over the wire (SUBSCRIBE
  CONTEXT); the owner's call 10 keeps it closed. No verb accepts a changed fleet pin (the preflight row
  names keys/fleet-pins.json). The Patterns-side Eye patch is prepared for Patterns' maintainers and not applied (kept outside
  this tree).
- wp2-control (merged): LTC readers open and close off the control loop through audio's capture hub ("<device>@<channel>");
  PANIC BLACK, HARD, REWIND and NEXT disengage a chase (receipt `released`), CLEAR does not re-engage. Open: after a restart
  the recovery record keeps which chases are disengaged but not that a panic did it (the words fall back to "plays by hand");
  a device whose open never returns holds the hub's lock, so ControlService.DisposeAsync and the hub's Dispose wait on it
  (needs a bounded open in audio's hub); an engine built without the audio part (tests only) has no hub, so LTC and TIMECODE
  DEVICES say so. The context's command list sorts TIMECODE CHASE, ENGAGE and INTERNAL with the transport verbs, which moves
  SYNC LEAD, SYNC FOLLOW and LINK PATTERNS out of the Eye's first 64 — which commands the Eye sees is the owner's call 11, now a
  request to Patterns (above).
- wp2-align (merged): ALIGN NEW/RUN/STATUS/CANCEL/ACCEPT/REVERT/CORNERS/CLEAR, run folders, STATE.align and outputs[].measured,
  the measured warp, blend and pedestal drawn by the output stage; AlignLedgerRoundTripTests is the wall × align test. Open: the
  phone camera hooks in at AlignRunnerOptions.OpenCamera (set in AlignExtension) once phonescan merges; SCAN START does not
  refuse while an ALIGN RUN shows patterns (align refuses the other way round); a real-camera run does not take the camera from
  the previews as a scan does (lab); PROJECT SAVE to a new folder copies neither align/<run> nor scans/, so the next edit finds
  the measured files missing; the ledger records one result per edit, so ALIGN ACCEPT calls wall's ledger directly to apply
  every output in one edit (a multi-result record would close it); ACCEPT writes the measured γ into the blend γ and a revert to
  a fallback without a correction does not restore it; wall's ScoreVocabulary still offers next-round fix lines (ALIGN RUN …
  STEPS COLOR, … COLOR WHITE); align.devices is not written (DEVICE NATIVE is next round); fx's IsfTranslatorTests leaves GL
  errors queued in the shared context (a later test that checks for none must clear them first).

## Integrator notes from the round-2 fix batch (fix-product3)

- Done: the desk opens each preview at the ring the `VIEW … SHOW` reply names (`{"view","ring","seq"}`) or STATE
  `machine.views[].ring`, the newer of the two (a STATE below the reply's `seq` changes nothing of the view), when it is the linked
  node's ring for the view in this machine's preview folder; it opens it again when that name changes, the old reader disposed
  first (also off screen), reads no other file, maps no file that is not a whole ring, and shows the stand-in with the words why
  while no ring is named, the name is not the node's ring here, or the named one does not open; a new link is asked for every view
  at once (`PreviewNames`, `RingPreviewSource`, `PreviewRing.IsGenerationOf`; `DeskPreviewTests`). The demo engine lists
  `machine.views` with no ring.
- `hevc10.mp4` is the one test clip a GPL encoder makes (libx265 at test time, never committed): no LGPL-compatible encoder in
  this FFmpeg writes 10-bit HEVC. The owner decided to keep it. No LGPL-compatible encoder here writes H.264 with B-frames
  either (OpenH264 makes none); the reordered-stream tests play MPEG-4 part 2 and Kvazaar's HEVC.
- Done: the node's settings file has one writer, the network host. Every read-modify-write of it — `PersistSettings`,
  `KeepSettings` (present's `SavePresent` and decode's policy write, all or nothing), the pairing token, the links — reads the file
  fresh and writes it durably under the host's one lock, so two edits of different sections landing together both stay; no other
  code calls `NodeHome.SaveSettings`, only the node's start and the host call `LoadSettings` (which may make or put right the file),
  and every other reader calls `ReadSettings`, which never writes (`SettingsGateTests`). A write that landed without its folder's
  flush is kept, with a `settings-flush` fault.
- PROJECT SAVE flushes on the control lane: 0.7–3.7 ms median (20 KB–2 MB, ext4 on virtio here), p95 under 5 ms. Measured,
  not governed; a disk under heavy writes (a media pull) may stall it longer. The chunk map of a pull is not flushed (a hint the
  file's hash checks).
- Seen once each: `DeskIdentityTests.ANewTokenUnpairsAClientInTheTableAtOnce` (Net, local; code this batch did not touch; five
  reruns pass) and `HostNetworkTests.ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine` (Windows CI run 259: the
  Patterns host read back from the settings file empty — most likely a read of the file beside the write's rename, which Windows
  refuses; the settings gate above closes that race in the process), and
  `PromoteTests.AFollowerPromotedLeadsOnTheSameTimeLineAndTheOtherFollowsIt` (Linux CI runs 252 and 265: the other follower stays
  free-wheeling with the anchor feed lost; it passes in every other run and locally). The second and third are settled in
  fix-tests4 (its notes, below); the first stays a watch.

## Integrator notes from the desk-2 merge (int-desk2)

- Done: the blind chip's draft view (`VIEW editor-draft SHOW editor DRAFT`) is asked through the views' SHOW path, its reply's ring
  and seq taken as every view's; hidden, its ring is let go. MAP draws it only while the node names the draft as this desk's: on a
  new link, until WHOAMI says which session is the desk's, MAP draws the air's view and the desk keeps its wish; the draft view is
  asked once the node names the draft the desk's, and the wish goes once it says the draft is another's or there is none
  (`DeskPreviewTests`). The demo engine shows a draft view as a node that draws does — with no draft (the air), the
  desk's or another session's — and refuses one on a view id that has drawn the air in the node's words (`BlindChipTests`).
- Done: STATE.sync carries `light`, its last key: the light the node reads with its verdict (the context item's); the readiness
  light's sync part is it, so the board's two ambers its other keys do not carry (a locked pair off one vblank among free-running
  outputs, a join over 5 s) light the strip too; INDEX's sync row reads the same light; a node with no `sync.light` is still read
  from its keys (`SyncLightTests`, `PreflightBoardTests`). sync.json and sync.cards.json moved; main.json and the Companion module's fixtures and lines did not (their
  nodes run no sync part).
- Done: a held action's menu entry is pressed twice (3 s of the desk's clock, `HeldPress`), armed with the line its label names (a
  line a push moved in between arms it afresh); every line whose catalogue row presses hold is held wherever the desk sends it from a
  button or a menu — its own controls, a card's line, a generated entry — except `VIDEO STOP`, `PANIC CLEAR` and `MASTER`, which the
  desk sends at every press with the air's controls (`DeskAction.SentAtOnce`; `HeldMenuTests`, over every action the desk offers);
  a menu entry shows its words as they are (no access keys).
- Done: the views' leases (`VIEW <view> KEEP`) are renewed on the desk's clock, as the overlay's are (`KeepViewsEveryMs`).
- Decided (the owner's calls 7 and 8): the transport's Stop, the panic banner's Clear and fade menu and the master are sent at every
  press though a Companion key holds them; a card's `APPROVE` (pressed twice on a key) is pressed once.
- Open: the throttle (drags, fields, a view's camera) sends what it coalesced in its last 16 ms to the link the desk holds when it
  flushes, so a line queued just before the desk attaches to another node goes to that node (seen as a `VIEW editor-draft CAMERA` in
  `DeskPreviewTests`); it should let go of what it held for the link it was queued on.
- Open (next round): CALIBRATION HISTORY marks a row `current` by its id, while RECALL and FALLBACK refuse by the target's geometry
  ("<target> stands as <name> (<cs>) already", `CalibrationLedger.Matches`): just after a Recall the target stands as the kept result
  it came from too, and the desk offers Recall on that row and the node refuses it. The history should name the rows the target
  stands as (a `standsAs` per row) so the desk greys them with the node's words; the desk does not repeat the node's comparison over
  its own mirror.
- The HoldButton's ring times its hold on the machine's stopwatch; the menus' second press and the leases run on the desk's injected
  clock.

## Integrator notes from the round-2 fix batch (fix-tests3)

- Every `Parallel.For` and `Parallel.ForEach` under `src/` passes `Compute.Options` or `Compute.With`, and the solves, scan
  decodes and simulators of the scan, align, resolve, blend, mapping, site and stack work run through `Compute.RunAsync`: Core's
  compute threads, one fewer than the processors beside a loop's calling thread, never the shared thread pool the lanes, sockets
  and timers need. `ComputeTests` scans `src/` and refuses a loop on the pool, PLINQ and `Parallel.Invoke`, and holds every file's
  pool work to a reviewed count (`EveryJobHandedToThePoolWasReviewedAsNoCpuJob`, below). Awaited I/O stays on the pool.
- Every test that measures allocation does it through `tests/Shared/Allocations.cs` (`Start`/`Since` for the measuring thread's own
  window, `Begin` before reading a counter another thread keeps), linked into the Core, Render, Media, Net and Engine suites; the
  allocation tests run in Release as well as Debug, since a closure an optimised build makes at a method's entry shows only there.
- The waits a test reads are on clocks it owns: a cued camera's patience on `ScanRunnerOptions.Time`, a live receiver's state on
  its provider's clock (`NdiFakeRuntimeTests` steps it a 60 Hz period a frame, so the default stale and lost times hold), a
  source's opens on the queue `EngineSources` is given. The optimise worker's priority is `OptimiseOptions.WorkerPriority` (the test
  rig runs it at normal); latency flashes are paced by `FlashPhases`, each held until the camera has sent the pictures that measure
  it; a leader's anchor is adopted in the lane action that applies it; the mDNS responder under test binds a port of its own.
- Fixed at this merge: the null sound output's lost frames (Windows CI run 286, Media
  `AudioTests.TheNullOutputConsumesOnTheClock`, "the null output stalled at 19958333 ns"; run 282, 249958333 ns), a clock step
  between two reads of one service step. A service step reads the output's clock once and asks the timeline about that instant
  (`IAudioTimeline.At`); the null output's playhead is a function of its clock and carries the part of a frame a step leaves, so
  no step loses or invents a frame (`AudioTests.AClockStepInsideAServiceStepLosesNoFrame`,
  `AStepBetweenFramesCarriesThePartOfAFrame`). A device's thread runs the same step and reads its position right before the step's
  instant (`ADeviceThatPlaysOnWhileAStepAsksTheTimelineIsComparedAtItsOwnInstant`, on the loopback);
  `TheMachinesOutputReadsItsClockOncePerServiceStep` runs on the machine's device where OpenAL Soft opens one and on the null
  output elsewhere, as on both CI jobs. The air's sound runs on the local clock of the node's network clock; `StartAirAudio`
  refuses an output on another.
- The sound output's further rules, each held by a test: a device's audio clock is read with its context current; a paused null
  output holds at the paused media time; a clock read before the last step never runs the null playhead back; a step whose frames
  span more than the hard-resync window seeks past them; sound plays 0.5× to 2× with the correction's room beyond either end and
  holds silent at any other rate, `STATE.show.sound.problem` saying so (`AudioTests`,
  `AirAudioTests.AnItemAtARateSoundDoesNotPlayIsSilentAndSaysWhy`).
- Its open list and the CPU jobs it left on the shared pool are taken up in fix-tests4: what that batch closes and what stays
  open are in its notes (below).

## Integrator notes from the round-2 fix batch (fix-tests4)

- Done: a reader never makes the replace of a file that holds the node's state fail. Every reader of one opens it through
  `DurableFile.OpenRead`, `ReadAllText` or `ReadAllBytes` (shared for write and delete), and so do the tests that read a node's files
  and the STATE fixtures' writers and readers
  (`MainStateFixtureTests.EveryFixtureWriterReplacesItsFileWhole` finds a fixture writer that writes in place). The media, versions,
  models and meshes moved in through `MoveIntoPlace` are read shared for reading only (the hashers, a pull's verify, `MeshIO`): on
  Windows a reader holding one open can make its replace fail, which then says so. Windows replaces with
  a POSIX-semantics rename (`FileRenameInfoEx`) flushed through the same handle, the same rename without the read-only flag where the
  system does not know it (before 1809), and `MoveFileEx` where the file system takes no POSIX rename (`DurableFileTests`,
  `SettingsReaderTests.AReaderHoldingTheSettingsOpenNeverLosesTheWritersChange`, Category=Windows). That closes the read of the
  settings beside their rename that the Patterns link's Windows failure (run 259) points at; `HostNetworkTests`' Patterns link
  asserts that no settings write failed.
- Done: stopping the render host waits, 5 s at most (`EngineSources.DefaultCloseWait`), for every source's open and close and
  journals each that did not close (`EngineSourcesTests.WhenDisposeReturnsEverySourceHasClosed`,
  `ACloseThatHangsIsSaidAndTheStopGoesOn`, `SourceReleaseTests`). The Spout test reads a frame once its staging copy's event query
  says the copy is done (`SpoutReceiver.PendingCopied`), never after a count of tries; the provider's read never waits (a copy not
  done yet is read at a later step). The stage's tables say which blend inputs their blend answers
  (`StageResources.BlendInUseFor`; `ReseatTests` waits for the landed draft's). A line session joins the session table before it
  runs, its registrations one at a time, and it reads whether it said hello under its register lock
  (`LineWebSocketTests.ANewTokenReachesASessionStillInItsFirstStretch`). `PromoteTests` waits until each follower knows the other in
  the group before it stops the leader (the follower left free-wheeling in Linux CI runs 252 and 265).
- Done, off fix-tests3's open list: a leader's receipt and report waits run on the sync part's clock (`SyncNodeOptions.Time`;
  `AnchorFeed.Heard` says when a member's receipt is taken), so `GroupAirTests` and `ArmAllTests` move a manual clock only once the
  answer and its deadline exist; route deadlines run on the fleet's clock (`FleetTime`, `RouteTests`); a scan's end is published
  once its runner has let go of its slot, and only while it is the latest run started
  (`ScanEngineTests.TheNextScanStartedTheMomentOneEndsIsNotRefusedAsRunning`); the phone camera's 5 s and 10 s waits run on the
  hub's clock (`PhoneAlignTests`, `ResolveCameraTests`); the survey import's lane slice is the work the lane thread did in it, its
  own processor time on Linux and macOS, read in the step the CPU clock states (`ThreadTime`, `ThreadTimeTests`); Windows is
  open below.
- Done, off the CPU jobs fix-tests3 left on the pool: the stage's automatic blend, thumbnails' conversion and
  encode, `MEDIA INSPECT`'s and the decode measure's HAP decodes, the scan views' still and scan pictures, a probe's first-frame
  decode, the site preflight check and ONNX inference with its pixel work run on the compute threads (`Compute.RunAsync`;
  `await Compute.Hop()` moves an async method's CPU part there). What the air waits for is never queued there, behind a solve that
  holds every compute thread: the still decode stays on the pool (`StillBesideComputeTests`), and the stage's table builder runs on a
  thread of its own, its loops on it and the compute threads that are free (`StageTablesBesideComputeTests`). `ComputeTests.EveryJobHandedToThePoolWasReviewedAsNoCpuJob`
  holds every product file's pool work to a reviewed count, so pool work a file adds fails it until it is reviewed.
- Done: an optimise job's pause is a fact when its worker holds it. The worker says when it holds the running job between frames
  and when it goes on (its `held` lines); STATE `media.optimise.jobs[].held` is that fact (`paused` is the hold asked), and the
  journal says "paused at frame n" and "resumed at frame n" then. `MEDIA OPTIMISE PAUSE` of the running job, and `RESUME` of the job
  its worker holds, answer accepted with the job in `pending` until the worker acts; a queued job's change, and a pause of a job its
  worker holds already, is applied at once (`OptimiseJobTests.APauseIsAppliedOnlyOnceTheWorkerHolds`; the test seam
  `OptimiseOptions.HoldFirstWorkerAt` holds the first worker mid-job). A job's partial file is gone before the job reads as over.
  The chain: the catalogue's `MEDIA OPTIMISE` row says a running job's pause or resume is accepted, pending, until its worker holds
  or goes on (VERBS, HELP, MCP's `list_verbs`, the assistant's prompt, the context's commands and the Companion module's `verbs.json`
  read it); docs/WIRE.md names it among the accepted receipts and docs/wire/optimise.md gives the reply and the keys; `optimise.json`
  carries `held`, and `main.json` and `output.json` hold `"optimise": null` (their nodes run no optimise part). The desk shows a job
  through the generic card (`optimise.cards.json`: clip, target, state, frames, ETA, PSNR — neither `paused` nor `held`), whose Pause,
  Resume and Cancel send their lines and show only a refusal; the context counts running and queued jobs. A Companion key that sends
  a running job's pause or resume (the generated `v_media_optimise` action or `raw`) stays pending until STATE says each job in the
  reply's `pending` is held (a pause, or the job no longer encodes) or no longer held (a resume), never on `paused` or a progress
  line alone (`test/optimise-pending.test.ts`; docs/COMPANION.md). A pause is applied at once only while the worker holds the job and
  the knobs last sent hold it too, and is journalled at the frame the worker held at; one that comes while knobs that let the job go
  are on their way, or a resume while knobs that hold are, waits for the worker's next held line; STATE shows a hold once the
  journal has its row (`OptimiseHoldTests`).
- Done: a preflight check that returned is its own rows, however its return meets the runner's look. The runner read a running
  check's task twice, once for "finished well" and once for "finished", so a check that returned between the two reads was a grey
  "could not check: cancelled" row and its report incomplete; a member's incomplete report made its `fleet:<node>` row grey in the
  group's report. That was `FanoutTests.PreflightAllListsOneSectionPerNodeAndASilentMemberIsRed`'s failure at line 165 (`fleet:nb`
  not green), twice on the Windows job (wp3-shaders c8b993e3, wp3-desk-ux b3fea9c9). A loop of the test on four cores hit it 10
  times in 17 000 runs on Linux, each time with a grey `<section>:check` row of the member's, and holding the runner between its
  two reads hit it every time. The runner now reads how a check ended only once it has seen it end, and reads the clock before it
  looks, so a check it cuts had not finished when its budget ran out.
  `PreflightTests.ACheckThatReturnsIsItsOwnRowsHoweverItsReturnMeetsTheRunnersLook` failed all six of its runs against the old
  runner (three in Debug, three in Release) and passes against the new;
  `PreflightTests.ACheckThatReturnsIsItsOwnRowsAndOneThatThrowsOrCancelsItselfIsAGreyRowWithItsWords` holds the rows of a check
  that throws or cancels itself. The fan-out test names a member's unfinished rows before it reads the member's fleet row.
- Done: the control loop's tick that forgets an OSC sender not heard for 10 minutes moves STATE once it has. The tick published
  its counts first and forgot the sender after, and the counts' pace (four times a second of the node's clock) held the drop back
  until that clock moved; STATE reads the count when it is written, so a STATE written between the two counted the sender under
  the key that held it. On a manual clock frozen after one jump, nothing moved the key again.
  `OscServerTests.ASenderNotHeardForTenMinutesNoLongerCounts`' one Windows failure at its line 94 ("the sender forgotten", at
  wp3-desk-ux b083d74e) is consistent with that order: its poll landing while the loop thread was off the processor between the
  publish and the forgetting. A 20 ms stall of the loop there failed the test at that line in two of three warm runs on Linux, and
  a STATE written there failed it in every warm run; no run of the old order failed it unforced, so that failure stays watched
  (below). The tick now forgets quiet senders before it reads the counts, and a tick that forgot one publishes whatever the pace.
  `OscServerTests.TheTickThatForgetsASenderMovesStateAfterForgettingIt` (a reader writing STATE the moment it moves, and a count
  the pace holds at the jump) failed all six of its runs against the old order (three in Debug, three in Release) and passes
  against the new; a wait on the senders fails with STATE's counts, the service's own senders and when each was last heard, the
  node's clocks and the loop's ticks.
- Done: a backup's mirror whose primary has said nothing past the stale mark is followed again with the primary's next line on the
  same connection. The backup cleared its subscription on every move of the mirror's link but to live, the stale one included, and
  only a new connection's `SUBSCRIBE MIRROR` set it again; a quiet spell ends with the next line on the same connection (the link
  is live again and does not dial), so the mirror read `lost` ("FOH-A's mirror is not subscribed") for as long as that connection
  lasted, and the watch's follow and repulls stopped with it, while the primary's lines kept landing. Now a subscription lives as
  long as its connection: a link that goes down or dials again takes it, a stale one keeps it, and the mirror reads `lost` while
  its link is quiet ("FOH-A has said nothing on its mirror for over 6 s"), down or unsubscribed. Preflight's mirror row offers
  `FLEET BACKUP <primary>` only while no subscription stands; a quiet link's stands, the verb refuses it ("already a hot backup of
  FOH-A"), and the row offers no line then
  (`BackupMirrorLinkTests.AQuietMirrorIsOfferedNoLineInPreflightAndADroppedOneTheLineThatJoinsItAnew`). A link quiet before its
  connection's subscription is taken holds none: the mirror reads `lost` "FOH-A's mirror is not subscribed" then and is offered
  the line, and the node's log says it is not subscribed
  (`BackupMirrorLinkTests.AMirrorQuietBeforeItsNewConnectionIsSubscribedSaysItIsNotSubscribedAndIsOfferedTheLineThatJoinsItAnew`
  holds the new connection's greeting before it subscribes while the clock passes the stale mark; against the old words it failed
  three runs of three with the quiet words). The mirror's state, words and line are read from one look at the link, and a link
  that reads down or dialling holds no subscription whether or not the node's listener has cleared it yet: a preflight run between
  the link's move to down and that listener read `lost` "down" with no line (a listener ahead of the node's holding the link's
  thread 300 ms on that move failed the dropped row's assertion three runs of three; it passes now). FLEET BACKUP's refusal, the
  row's line and the watch's follow and repulls read that one predicate. A primary lights a backup whose mirror is lost red alike
  in STATE, on INDEX's key for it and in preflight's row for it; the key and the row were amber
  (`BackupMirrorLinkTests.APrimaryLightsABackupWhoseMirrorIsLostRedInStateOnItsKeyAndInPreflightAlike`). The link counted that
  silence (stale at 6 s, dropped at 12 s) on the process's clock, while the rig counts on hand-moved clocks the fleet monitor's,
  the pulls' and the sync part's watch of its leader's clock; it counts it on the node's clock now (`WireClientOptions.Clock`,
  which `MirrorLink` takes from the node). Every other link keeps the process's clock, the sync part's anchor follower among them
  (`AnchorFollower`, on `WireClient`'s default options): it counts its leader's silence in real time, its feed reads `connecting`
  while that link is stale, and an otherwise identical mirror reads `joining` then.
  `BackupPreflightTests.TheBackupsFilesAndTheRoutesAreCheckedWithTheLineThatFixesThem`' one Windows failure at its line 95
  (wp3-installer c92e3c2a, run 39653186869, the test 22 s: INDEX's `FAILOVER` red `lost` where it waited amber `different`, after
  the backup's preflight had read the mirror `different`) is consistent with the mirror link's real-time silence, not proven: its
  message gave only `("red", "lost")`, which fits the link still stale at INDEX's read (which only the count on the node's clock
  covers), a subscription cleared by a stale spell that had ended (the latch, fixed), and a dropped connection not yet subscribed
  again alike. Between the two reads the test runs
  two route reads to a closed loopback port, whose 2 s bound is on the rig's fleet clock, which does not move, so each ends only
  when the refused connect does, which on Linux takes under a millisecond here; that the Windows run stalled 6 s or more there is
  not shown. On Linux (Debug), the test process stopped by a signal for 8 s at that point failed the unchanged test at that line,
  `("red", "lost")` with "FOH-A's mirror is not subscribed", in three runs of three, as did the thread pool held 8 s (256 blocking
  items on its global queue) followed by 3 s of real time, three of three; held 3 s, it passed twice. The stopped process passes
  three of three now, and three of three with the subscription's fix alone and the link on the process's clock.
  `BackupMirrorLinkTests.AMirrorQuietPastTheStaleMarkIsFollowedAgainWithItsNextLineAndADroppedOneSubscribesAnew` holds the
  backup's reader on a mirror line while the backup's clock moves past the stale mark, lets it go, then drops the connection; it
  failed three runs of three against the old handler (`lost`, "not subscribed", the link live on its first connection) and passes
  against the new. `LinkTests.ALinkCountsItsPeersSilenceOnItsClockAndIsLiveAgainWithTheNextLineOnTheSameConnection` shows that
  real time is not counted and the link's clock is. The preflight test's two reads of the mirror name what the backup saw: the
  mirror's state and words, the link's status, connection and silence on the node's clock, and when the fleet monitor last heard
  the primary.
- Done: `LinkTests.ThePatternsLinkGoesStaleOnSilenceDropsAndRedialsAndQueuesNothingWhileDown` counts the desk's silence on a
  hand-moved clock (`PatternsLinkOptions.Clock`, which `PatternsLink` hands its link; the engine's Patterns link keeps the
  process's clock). Stale at 400 ms and dropped at 900 ms of real time, with a ping every 2 s, its link went stale and dropped on its
  own about a second into every connection whether the desk answered or not (live at 25 ms, stale at 499 ms, down at 1014 ms, live
  again at 1935 ms), and each of the test's polls raced a window of a few hundred milliseconds. Its one failure in 18 loaded Linux
  Net-suite runs on fix-backup-preflight (about 5 s, its line not kept), and its one in 9 full Net runs on fix-longpoll-win at a
  load of about 7 to 9 on four cores (its last send, after the redial, not answered ok), are consistent with that, not proven.
  With the default clock
  the link counts as it did before `WireClient` took a clock (`MonoClock` is `Stopwatch`'s counter in nanoseconds), so the hazard
  is older than that change. Beside a thread pool held for 9 s in the same test process, the unchanged test failed three runs of
  five with 8 items of 500 ms every 600 ms and three of the four that finished with 16 of 250 ms every 300 ms, at its waits for live
  and paired and for stale and at its last send. The new test (stale and dropped as the clock moves, a line sent at the move to down
  refused at once, the moves recorded) passed eight of eight beside the first hold, eight of eight beside the second held for 4 s,
  and five of six held for 9 s, the sixth's first 5 s wait ending live and paired before the first ping's answer was read (the pool
  held longer than the wait, not a window missed). Held harder (64 items of 400 ms every 700 ms) both fail, the new one because the
  link does not connect within its first wait. The same pattern (a short real-time stale or drop mark and a poll for a passing status) is not used elsewhere
  in the Net and Engine suites: `LinkTests.TheEngineLinkPairsMirrorsStateAndProjectAndRedials` waits for down with no server
  listening, which stays; `LinkTests.ALinkCountsItsPeersSilenceOnItsClockAndIsLiveAgainWithTheNextLineOnTheSameConnection` now
  waits for its first ping's answer before its peer falls silent, so no answer read after its clock moves starts the silence
  again; `BackupMirrorLinkTests`' wait for its dropped row read one look at the link apart from another, settled above.
- Done: a media pull is served and written in 1 MiB blocks, a chunk's body that falls silent is named, a pull says where it
  stands, and a fetch's journal rows are bounded. What was seen: the Windows failure (build 400 at fix-fanout-win 529b7d94, run
  39578502602, job 119149948163) is `HostNetworkTests.FetchPullsAFileByItsHashFromAPeerAndVerifiesIt` waiting 20 s for the pulled
  file (line 76 at 529b7d94) in a 45 s test of a 10 min 10 s Engine suite. Its message was the fetcher's journal, four rows: the
  node up, the item added, the `MEDIA FETCH` reply ("verb: fetching 1 file") and "media: fetching 1 file from Stage left", which is
  `NetworkHost.FetchAsync`'s answer, journaled by the verb's continuation as the pull starts, before the peer's list is answered.
  At 529b7d94 every end the pull's catches give (a list that could not be read, which there ended "no heard node holds it"; a
  chunk's tries run out; a request refused) journals a refused row, so the rows show that the pull had not ended that way within
  the 20 s. They do not show where it stood (the list, the body, the hashing and landing) or whether it had faulted outside its
  catches; the cause is not shown. Against the Windows job at wp3-desk-ux b1ac137 that morning (job 119007771945), suite by suite:
  build 2 min 35 s against 2 min 52 s, App 1 min 40 s against 1 min 50 s (241 tests against 235), Core 58 s against 1 min 7 s,
  Engine 10 min 10 s against 8 min 10 s (1 167 tests against 1 164), Net 14 s against 10 s (the same 194 tests), Render 1 min 29 s
  against 1 min 27 s, and Assistant, Devices, Media, Platform.Windows, Render.D3D12, Supervisor and Vision the same to the second.
  That job itself ended failed: its App suite failed one test (`CalibrationDeskTests.TheScoreCardsFixSendsEachTermsLine`, "Failed:
  1, Passed: 234"), so its 1 min 50 s holds a failing test. So the Engine suite was longer by two minutes, Net by 4 s (40 %) and
  Render by 2 s, while the build, App and Core were shorter; one pair of runs says nothing of the runners' spread, and where the
  Engine suite's two minutes went is not known.
  What was measured, on Linux only: the holder wrote a file to the socket 64 KiB at a time (the framework's file copy) and the
  fetcher read and wrote 64 KiB at a time, and across that test's 3 MiB body, in an otherwise idle process, the thread pool's
  completed count rose by 454, 461 and 488 in three probe runs (not kept in the tree). 5aaec503's message gives about 70 with 1 MiB
  blocks; no record of that run was kept. A socket read returns what has arrived and Windows completes socket reads its own way,
  so these counts are not Windows', and nothing here shows that the pool's queue is where the Windows run's time went. Beside one
  synthetic load in the test process (48 CPU-bound pool items of 50 ms each, each queued again as it ended, on four cores) the
  unchanged test failed at that wait in 3 of 4 runs and, with 1 MiB blocks, passed 4 of 4 (8 runs in all; one-sided Fisher p about
  0.07), and one probe of each timed the body at 19.7 s and 3.6 s. Lighter loads are reported tried before 5aaec503 (CPU hogs
  pinned to the cores, 8 to 24 spinning threads, 16 blocked pool threads, beside the preflight flood test) without making it
  fail; how many runs each had was not recorded, and they were not run again. The blocks are kept, as fewer pool turns for a body
  on Linux; that they answer the Windows failure is not shown.
  The holder answers `/api/media/<hash>` and the backup's `/mirror/media/<hash>` through `MediaFileResponse` (one byte range, the
  hash its strong ETag, 416 past the end, the whole file for anything else), and the fetcher gathers each chunk's body into 1 MiB
  blocks, each written at once (`MediaFileResponseTests`,
  `HttpHostTests.AMediaFileAnswersOneRangeItsHashAsItsTagAndTheWholeFileForAnythingElse`).
  The pull also said nothing until it ended: its bytes moved only when a whole 8 MiB chunk was in; a chunk's body had no deadline
  once its headers came, so a peer that sent 64 KiB and then nothing held the pull (`pulling` at 0 bytes, the journal silent)
  until the node stopped, and every later `MEDIA FETCH` of the file was refused "already on its way"; a failed try was logged at
  Debug only; a list that named a file twice faulted the pull outside its catches, leaving it `queued`; and a list that could not
  be read made its peer offer nothing, so its files ended "no heard node holds it". Now a chunk's body that sends nothing for
  `MediaTransferOptions.Quiet` (10 s, a chosen bound, not a measured one) on the node's clock fails that try with words ("sent
  nothing for 10 s, 65536 of 3145745 bytes in"). Only the time a read of the body waits is counted: the bandwidth bucket's waits
  and the disk's writes come between reads (a test moves the clock while the bucket holds the pull; none moves it while the disk
  writes). The list and each answer's headers wait the HTTP client's own 60 s, as before, and a list that never comes is a
  `TimeoutException` that says so. A pull says its bytes as it starts (the chunks a resume keeps), as each block lands with bytes
  no try of its chunk had, at each chunk's end and once every byte is in, with no pace: the 250 ms pace 6729107e kept had no
  measured cause and no test, and is gone. Its bytes step back only when it moves on to another peer, whose pull starts from the
  chunks the part file keeps whole. A chunk has three tries; a pull journals its first failed try at each peer ("clip from Stage
  left: chunk 1 of 1 from <addr>: … — trying again (2 of 3)") and its end, and the tries between are its row's `note` and the
  log's. A fetch journals at most 4 of its pulls' rows (`FetchJournal`) and, when it had more, one row at its end that counts how it
  ended; the rest go to the node's log. Those five rows are a quarter of the context's recent rows (`ContextBuilder.RecentRows`,
  20), the only window of the journal a reader outside the node takes (MCP's `get_context`, Patterns, Stagewatch and the
  assistant; no desk code reads the journal's rows). f9758fa3 left about two rows a file (one a file when the list failed), so a
  fetch of about ten files from a peer that refuses every chunk, or about twenty whose list fails, pushed every other row out of
  the context's window; its message presented that as fixed, and its 48-file test read the journal's 200 rows kept in memory,
  which no reader outside the node takes. 970fbe79's bound, 20 rows and one that counts the rest ("a tenth of the journal's 200
  recent rows, the ones the context and the desk read"), still let one fetch fill the context's whole window, and its flood tests
  read the 200 too. A peer's list is asked once a fetch, its rows taken first-wins, and when it could not
  be read each file no other peer gives ends with its words; a pull that fails any way but the stop's own cancelling ends "failed:
  <words>" with its refused row. STATE's transfer row carries `at`, `stage` (`asking`, `receiving`, `verifying`), `since` and `note`
  (docs/WIRE.md); the desk's Network drawer shows each row's stage and, beneath it, the last failed try's words; PLAY, the
  assistant's account of STATE and the Companion module count only the rows on their way as running copies (each counted a failed
  row as running, and PLAY marked its item fetching), and PLAY says how many could not be copied.
  All of this is the node's own pull, which `MEDIA FETCH` runs only on a node without the fleet's part, such as the bare node
  `HostRig` starts unless told otherwise. The exe composes the fleet's part, and there `MEDIA FETCH` runs `BackupNode.FetchMedia`
  (the peer's proof, `/mirror/media`), which writes no transfer row, says no failed try, has a bucket with no rate (no throttle
  while a show plays) and lets a second fetch of a file pull it beside the first; the papers had said the node's own pull's rules as
  the product's. Now they say which pull each rule is, the fleet's pull counts a body's silence on the node's clock (its transfer
  counted it on the process's own clock before) and takes the node's pull options, and its journal rows are bounded the same way;
  the rest is an open fix (below and docs/ROADMAP.md). A backup's pull of the show's own files beside its folder
  (`MirrorPuller.ShowFileAsync`) reads its body through the same quiet on the node's clock (`BodySilence`), and a file that falls
  silent is refused with its words and asked for again by the watch; an answer other than success is said by its status alone
  ("FOH-A answered 404"), its body never read. 74cf79f2's message said a mirror pull's silent body was counted on the node's clock;
  that held for content-addressed files only: the show's own files were read with no bound, so a primary that fell silent mid-file
  held that mirror run, and the show-files gate with it, with nothing in the read to end it but the node's stop
  (`MirrorPullerTests.AShowFilesSilenceIsCountedOnTheNodesClock` still waited after 30 s against 36de2773 in Debug). When the watch
  asked for the files again, that run was the watch's own turn, so it held the watch too, which a55c36a0's message and this entry
  left out; and a55c36a0 still read a refusal's body with no bound, so a primary that answered a show file with an error's headers
  and then sent nothing held the pull the same way
  (`MirrorPullerTests.AShowFilesRefusalIsSaidByItsStatusAloneAndItsSilentBodyHoldsNothing`, with `tests/Shared/FakeMediaPeer.cs`
  as committed beside it, waited its 30 s against 5a67a5b8's src in Debug, two runs of two). The watch still waits up to the quiet
  for each missing show file (open, below).
  Tests: `tests/Shared/FakeMediaPeer.cs` is a peer's two media routes with the faults a real node does not make on purpose. A
  body's silence is counted on a hand-moved clock in
  `MediaTransferTests.AChunkWhosePeerFallsSilentIsTriedAgainAndEachTryIsSaidWithItsWords`,
  `HostNetworkTests.AFetchFromAPeerThatFallsSilentSaysSoAndEndsWithItsWords` and
  `MirrorPullerTests.AFilesSilenceIsCountedOnTheNodesClock`, so their outcomes do not hang on the machine's speed;
  `MediaTransferTests.APeerSilentLongerThanTheQuietInRealTimeIsNotCutWhileTheTransfersClockStandsStill` (named
  `APeersSilenceIsCountedOnTheTransfersClockAndOnlyWhileABodyIsRead` in the commits that report it, a name that claimed the list
  and the headers too) never moves the clock, so it shows only that real time is not counted;
  `AListAndAnAnswersHeadersAreNotCountedSilentHoweverFarTheTransfersClockMoves` moves the clock ten quiets while the peer holds the
  list's answer and then the file's, and the list comes and the file lands with no try cut (a variant that counts the list's wait
  on the transfer's clock under the quiet fails it, two runs of two, as does one that counts an answer's headers' wait, two of two,
  and the never-moving test passes both);
  `APullHeldByTheBandwidthBucketIsNotCountedSilentHoweverFarTheClockMoves` moves the clock ten quiets while the bucket holds the
  pull, and a variant that counts the bucket's wait fails it. `APullsBytesNeverStepBackWhenATryStartsItsChunkAgain` ends each
  body in order after 1 MiB and a byte, with no clock, so each try lands its block whatever the load (its clock-driven form
  could cut a try before its block landed); a variant that starts a chunk's bytes again at each try fails it.
  `APullsBytesReachItsListenerInTheOrderTheyLandedHoweverLongTheListenerTakes` holds the listener on the first word that carries
  bytes until another word comes or 2 s pass. From 74cf79f2 it held the pull's first word, which the pull now says as it starts,
  before any chunk is asked for, so its wait always ran out with no word beside it and a variant that raises a block's word outside
  the pull's gate passed it (three runs of three); that variant fails it now (three of three). Their commits report
  these failing against the tree before them and they pass now: `MediaTransferTests`'
  `APeerSilentLongerThanTheQuietInRealTimeIsNotCutWhileTheTransfersClockStandsStill`,
  `APeerThatNeverAnswersItsListIsGivenUpOnTheClientsTimeoutWithWordsThatSaySo` and
  `APullsBytesReachItsListenerInTheOrderTheyLandedHoweverLongTheListenerTakes`; `HostNetworkTests`'
  `AFetchFromAPeerThatFallsSilentSaysSoAndEndsWithItsWords` (two try rows before) and
  `AFetchOfSeveralFilesFromAPeerWhoseListFailsEndsEachWithTheListsWords`; and
  `DrawerTests.TheNetworkDrawerSaysWhereEachTransferStandsAndItsLastTrysWords`. Their commits report these failing against
  1e1567e9 and they pass now: `MediaTransferTests.APullSaysHowFarItIsAsItsBlocksLandAndThatItVerifiesOnceEveryByteIsIn` (only its
  first block said there) and `AnInterruptedPullResumesFromItsChunkMapAndVerifies` (its first word came once a chunk's bytes had
  landed); `HostNetworkTests.AFetchThatMovesOnToAnotherPeerKeepsTheChunksItHasAndSaysThemAtOnce` (0 bytes at the second peer);
  `DrawerTests.PlayCountsAPullOnItsWayAsACopyAndOneThatFailedAsNone`; and
  `ExplainAndRecipeTests.ExplainCountsOnlyTheTransfersOnTheirWayAndSaysOnesThatFailed`. The same commits (74cf79f2, 970fbe79,
  85d2c1bb) report more runs against 1e1567e9 of tests that, as committed, cannot compile there; the runs were of variants
  neither kept nor described, so how those tests fare against 1e1567e9 is not shown, only that they pass now:
  `MirrorPullerTests.AFilesSilenceIsCountedOnTheNodesClock` passes `pulls:`, which 1e1567e9's `MirrorPuller` does not take
  ("still waiting after 30 s" reported); `HostNetworkTests`'
  `AFetchOfManyFilesFromAPeerThatRefusesEveryChunkJournalsEachPullsFirstFailedTryAndItsEndUpToItsBound` and
  `AFetchOfMoreFilesThanTheJournalHoldsFromAPeerWhoseListFailsLeavesTheNodesOwnRows` and
  `BackupTests.AFetchOfMoreFilesThanTheJournalHoldsWithTheFleetsPartLeavesTheNodesOwnRows` read `FetchJournal.Most`, and
  `FetchJournal` is new in 970fbe79 ("no row counting the rest", "the recent rows starting at the 51st file's" reported); the
  Companion module's variables test imports `movingTransfers`, new in 85d2c1bb, so against the module before it the file fails at
  its import and cannot give the "3 transfers and 0.17" reported; and
  `MediaTransferTests.APullHeldByTheBandwidthBucketIsNotCountedSilentHoweverFarTheClockMoves` reads `TokenBucket.Waiting`, new in
  74cf79f2, whose message says it passes against 1e1567e9 too. The context reads e969871d added to the 250-file tests, written
  with members 404e605c has, failed against 404e605c as committed, in Debug (the context's 20 rows began at the second file's
  refused row, the opening verb gone). Of the five fault tests 5aaec503 named as failing against the pull before it, four use
  members that pull lacks (`Quiet`, `Trouble`, `BlockBytes`, the stage, the rig's pulls), so as committed they could not run
  against it, and the variants its message reports are not kept; the fifth,
  `HostNetworkTests.AFetchFromAPeerThatListsTheFileTwiceStillLandsIt`, uses none of them and runs against that pull as kept. Run
  so (the test, its two helpers and `tests/Shared/FakeMediaPeer.cs` as at a55c36a0, against 529b7d94's src, in Debug), it failed
  two runs of two: after 20 s the transfer was `queued` at 0 bytes with no peer, and the journal ended at "media: fetching 1 file
  from Twice". 5aaec503's message reports it failing there, its transfer "queued" for good. The test 5aaec503 committed is the one
  kept (lines 130 to 143 of its `HostNetworkTests.cs` are the same text), so what is unrecorded is 5aaec503's run, not the test, and
  the run above, two of two failing with the transfer `queued` at 0 bytes, is consistent with its report. The progress variant it
  names, written without the pace's option, met 5aaec503's default 250 ms pace against the new pull, which said a block only when
  it landed 250 ms or more after the last word, so whether it could pass there hung on how far apart the blocks landed (the pace is
  gone now). The fetch test's wait fails with the transfer's row, the time since the fetch, the thread pool's counts and both
  nodes' journals and log tails, so a recurrence names its stage.
- Done: a long-poll counts its limit on the web port's clock (`HttpHostOptions.Time`, the system's in a node), so
  `HttpHostTests.TheLongPollWaitsForTheNextChangeAndGivesUpAtItsLimit` moves that clock by hand and no longer races its own pace.
  What was seen: its one failure on the Windows job (run 417, job 119488722915, wp3-installer e03befea, which touches neither
  `src/PlayMap.Net` nor its tests; the test passed there in run 418 and on Linux in both) was "Assert.False() Failure, Expected:
  False, Actual: True" at line 163, `Assert.False(waiting.IsCompleted)` after `await Task.Delay(150)` against a 600 ms
  `LongPollMax`, then line 170 (the method's end, where the host is disposed), in 756 ms, 7 s into a 14 s Net run of 194 tests.
  That is all the log gives: it does not say whether the poll had answered or faulted, or when. Read against the code, the handler
  answers a `since` poll only with a newer STATE or, once the limit has passed since it took the request, the one it holds. It
  takes the change signal's next task before it reads STATE, so a change between the two ends the wait; a wake that brings
  nothing newer, or a timer that fires early, waits on for what is left; the signal swaps its task before completing the old one,
  so no waiter holds a task no later change completes; its clock is the process's own monotonic one. Nothing there ends a poll
  early, and the tests below hold it. So the poll could have answered before the check only if the check ran 600 ms or more
  after the handler took the request, over 450 ms after its 150 ms delay was due. On Linux, where the host's start completes at
  once, the check runs on a thread xunit starts for it when the delay's timer fires (the test's first await that does not
  complete at once resumes through xunit's context, which runs it on a long-running thread of its own); the poll's limit, its
  answer and the client's read of it run on the thread pool. The log does not tell a late check from a request that faulted
  inside the 150 ms: either fails at line 163 with these words and frames. Nor does the 756 ms, which ends at the check and holds
  the host's start and stop beside the test's own waits: it fits a late check only if start and stop took about 150 ms or less
  together on the runner, and a faulted request whenever they took up to about 0.6 s. The log measures neither (it times no
  passing Net test). On Linux they have taken from about 20 ms (a warm run inside the Net suite) to about 0.4 s (a host's first
  start in a test run, at a load of 5 to 9 on four cores), and a runner loaded enough to hold the check that long could slow them
  too. Consistent with the check's thread running late on the loaded runner, not proven, and a request that faulted is not ruled
  out.
  Forced on Linux, the test unchanged: with every new thread's start held 700 ms (strace's delay injection on `clone`) under the
  default algorithm, it failed at line 163 with the same words and the same second frame in 3 of 15 runs and at line 165 in 4
  (the poll had answered seq 1 at its limit just after the check passed and before the publish: the same race a step later),
  and passed 8; a copy of its steps traced under the same hold saw the handler give up 605 ms after it began to wait and the
  client hold that answer 538 ms before the check ran, on xunit's thread. With xunit's aggressive algorithm on one thread and a
  test beside it that holds that thread 900 ms at a time, it failed at line 163 with the same words in 6 of 6 runs (holds of
  700 ms did not: the request reached the handler about 120 ms after it was sent, so the check still came first). Those runs show
  that a held thread makes the unchanged test fail this way, not that one did on the runner: under the holds a run took seconds.
  Now the test asks with a `since` older than STATE (answered at once, no wait made), then with STATE's own seq: it holds that
  the poll waits on the clock (`ManualTime.Made` and `Waiting`), that a wake with nothing newer (`FakeEndpoint.Touch`) leaves it
  waiting, that the next change answers it, and, with nothing newer, that it answers the one it holds when the clock has moved
  the limit since it took the request, a wake on the way counting toward the limit, and not a tick before. It passed 6 of 6 runs
  under each hold above. Two local mutations of the handler, not kept, fail it: a wake that restarts the limit (at its last
  answer) and a wake that answers whatever it holds (at the first wake).
  A wake that brought nothing newer with less than a millisecond of the limit left did not wait out that part: the runtime's timed
  wait counts whole milliseconds, dropping a part of one, and ends at once on less than one, so the handler threw, caught and read
  STATE again on its pool thread until the limit had passed (under a millisecond on the system's clock; without end on a clock the
  test moves, its first wait's timer still standing, as the loop ran inside that wait's completion). It never answered early, so
  it is not this failure's cause. `ChangeSignal.WaitAsync` now waits a part of a millisecond as a whole one, so its wait is never
  shorter than asked; `ALongPollWithUnderAMillisecondOfItsLimitLeftWaitsThatOutOnTheClock` (no second wait was made: "1 waits
  made, 1 standing") and `AWaitForAChangeEndsNoSoonerThanItsTimeThoughThatIsNotWholeMilliseconds` (a 0.5 ms wait had ended at its
  call) failed before it and pass with it. `OnTheSystemClockALongPollWithNothingNewerAnswersNoSoonerThanItsLimit` runs a poll on
  the clock a node counts on (`HttpHostOptions.Time` unset, `TimeProvider.System`), with a 50 ms limit, and reads only that it
  answered no sooner than that after it was asked, which no pace of the threads can break. A poll the web port's own stop cut
  (the host's dispose cancels it before Kestrel stops) left its handler without a word, so its client had a 200 with an empty body,
  no STATE in it; it is answered 503 with the reason now, and a poll its client gave up on is answered nothing
  (`ALongPollTheWebPortsStopCutsIsAnsweredThatItStopsNotWithAnEmptyState`: `(OK, null)` before, `ServiceUnavailable` and JSON
  after). The hand-moved clock is one file the Engine, App and Net suites share (`tests/Shared/ManualTime.cs`) instead of three
  copies.
  The same race, the test's own pace against a short real limit, was in three more Net tests and is gone from them, the first two
  not forced: `AFrameMayOvertakeTheReceiptOfTheCommandThatMovedIt` made the frame come first by holding the line's answer 300 ms
  of real time, so a sender that woke later than that let the receipt come first; the answer is now held until the test has the
  frame (`FakeEndpoint.HoldReplyUntil`). `AnchorFeedLineTests` waited 100 and 200 ms of real time for receipts the test hands in
  its next statements, so a test thread held that long read them silent (the one-deadline test's one failure beside ten busy loops,
  open below, fits that); their waits now run on a manual clock (`SendAirAsync`'s
  `time`) moved once the receipts are in, and the one-deadline test holds that twenty members make one timer and end at its one
  move instead of reading 1.5 s of real time. `StagewatchLinkTests.AStuckBeatShowsAQuestionMarkAndARedirectIsRaised` read the
  countdown's words 1.1 s of real time after the frame, and "12:29" holds only from 1 s to 2 s after it, so a test thread resumed
  0.9 s late read "12:28". It failed so here at line 171 (`Expected: "12:29"`, `Actual: "12:28"`) in 2 of 9 full Net runs at a
  load of about 7 to 9 on four cores, and in 5 of 5 runs with its process stopped 2.5 s and run 0.3 s in turn; the link now counts
  on a `ManualClock` the test moves 1.1 s, and the test passed 6 of 6 under the same stops. `LinkTests.ThePatternsLinkPairsTracksTheClipAndFollowsTheBlackout`
  read the link's counted clip 300 ms of real time after its first frame and asked for 62250 to 64000 ms, then 10000 to 11000 ms
  after a new seq landed, so a test thread held 1.7 s at the first read, or 1 s at the second, failed it, and one held past the
  link's 3 s `VideoStaleAfter` read no clip at all. With its process stopped 2.5 s and run 0.3 s in turn it failed 3 of 3 at its
  first read (twice `link.Video` null, once `Actual: 64572.55`). The link counts its clip and the desk's silence on a
  `ManualClock` the test moves now (the `PatternsLink` constructor's clock and `PatternsLinkOptions.Clock`): 62000 ms at the first
  frame, 62300 after a 300 ms move, the same 62300 after a frame 120 ms behind it, 10000 at the new seq; it passed 6 of 6 under
  the same stops. `HelloTests.TwoNodesHearEachOtherAndAgeOutOnTheClock`
  gave the hellos its stopped node had already sent 150 ms of real time to land before it moved the peer table's clock, so a
  listener held longer would take one in after the move as a fresh hello and the sweep would read the peer live, not stale. It now
  waits until the listener has counted every hello that node sent (`HelloService.Heard` at least the peer's `Sent`), and `Heard`
  counts a hello once the peer table has taken it in and told its listeners, where it had counted it before the table saw it
  (`AHelloIsCountedHeardOnlyOnceThePeerTableHasTakenItIn` read `(1, 1)` with the table's listener held before the change, `(1, 0)`
  after). The old wait was not seen failing; every receive held 250 ms did not make it fail, as the stopped node had sent a
  single hello by then.
- Done: the stage's automatic blend counts its quiet time on the clock its options give (`StageResourceOptions.Time`; the render
  host keeps the system's), and its tests move that clock.
  `StageResourcesTests.TheAutomaticBlendWaitsForAQuietStageAndKeepsItsLastGoodMaps` read the tables for no blend just after its
  first settle, and for the last good map just after its second, while the blender counted its 150 ms of real time from the
  builder's signal on threads of its own, so a test whose next step ran later than the quiet time and a solve read the solved map.
  It failed so on the Windows job (run 425, at fix-nodehost-win 39caa04c, which does not touch the stage) at its line 75
  (`Assert.Null()`, `BlendMap { OutputId = op_left, Width = 480, Height = 270 … }`), consistent with its continuation starting a few
  hundred milliseconds late on the loaded runner, not proven. With the test's own continuations held 3 s each (a context of one
  thread that runs what is posted to it no sooner than 3 s later, the stage's threads free), the unchanged test failed 3 of 3 at
  that line with those words, and passed 3 of 3 held 0 s; `ABlendTheVerbSolvedIsTakenAsItIsWithoutSolvingAgain`'s count of no solves
  held only while the test outran its 30 s quiet time, and held 31 s it failed 3 of 3 at its line 107 (`Expected: 0`, `Actual: 1`).
  The first test now moves the clock once the blender waits on it, a tick short of the quiet time (the wait stands, nothing is
  solved) and then the last tick (the wait ends), and the second never moves it; both passed 3 of 3 held as above, and the first
  fails 2 of 2, timed out waiting for the blender's wait, against a blender that counts on the system's clock.
- Done, off the open list: `PanicTests.RewindCuesTheItemAtItsInPointAtTheFadesEnd` reads the panic's journal row once a tick of the
  lane has seen the fade's end (`ShowRig.Ticked`: the show's tick instant is written after its panic steps have run), not at its
  first sight of the cue. `PANIC REWIND` cues its item through `PlaybackLanes.Cue`, which publishes the lane as Cued and runs its
  listeners before `RunStep` journals the row ("panic … cued A"), so a reader that saw Cued could read the journal without it; the
  row says the cue happened, so it is written after it. Holding the lane 300 ms in a last listener of the playback once it shows the
  cue failed the unchanged test 3 of 3 at its line 102 (the journal holding only the two verb rows); the test now asserts Cued after
  that tick, not at some point, and passed 3 of 3 under the same hold (and with it 3 s). Its failures, in one Engine run of
  int-tests4's gate and once in a loaded Linux gate whose message was lost, are consistent with that order, not proven. The same
  read was in `ViewVerbTests.AViewNotRenewedForFifteenSecondsIsHiddenAndItsRingLetGo`, which read the expiry's journal row the
  moment the view's ring file was gone: the views delete it and run their listeners before the lane's tick journals the row. A
  300 ms hold there failed it 3 of 3 at its line 213; it now takes a lane turn after the file goes (the tick is a lane item, so a
  later one runs after it), and passed 3 of 3 under the hold.
- Done: `ProjectorViewTests.TheProjectorViewIsTheOutputsPictureBeforeItsTrimAndBlackAround` reads the output, and shows the view,
  only once the stage's tables for the project are published with every mesh tried (`StageResources.WhenSettledAsync`, as
  `RenderHostTests` and `FxHostTests` wait). The wall's mesh and the output's lens lookup are built on the stage builder's own
  thread, which the render thread's first frame wakes and never waits for, so a picture drawn before the tables land lights nothing;
  the test read the output two frames after the host started, which on a loaded machine can come before them. Holding the builder
  1.5 s at its first wake failed the unchanged test 3 of 3 at its line 61 (`middle (0, 0, 0)`: the output and the view both black),
  and holding it until the output's first read failed it 2 of 2, once at its line 55 (`Actual: 68448` pixels more than one code off,
  worst 192 codes: the view drawn with the wall, the output without) and once at line 61; with the wait it passed 3 of 3 held 1.5 s
  and 1 of 1 held 3 s, every read made with the settled tables in use. Its one failure in a loaded Linux gate (its message lost) is
  consistent with that race, not proven.
- Done: `ThreadTimeTests.AThreadsTimeCountsItsWorkNotItsWait` asks the thread's measure for half of the time its spins ran, not half
  of their stopwatch span. A spin bounded by the stopwatch holds every turn the machine gives other threads and every stop of the
  process, which a thread's CPU clock rightly leaves out, so a thread given under half a processor read under half its span: in a
  loaded Linux gate (load 29 to 32 on four cores) "the 125 ms spin: spun 127.56 ms of the stopwatch, counted 61.49 ms", and once
  more in three reruns there, consistent with that, not proven. With its test host stopped 15 ms in every 25 the unchanged test
  failed 3 of 3 at its first 10 ms (counted 0.93 to 5.77 ms of 16.12 to 20.83 ms; its 125 ms spins counted 43.71 to 48.80 ms), and
  pinned to one processor beside one busy loop 3 of 3 (its 125 ms spins counted 43.98 to 52.52 ms of 127.96 to 130.00 ms). A spin
  now runs until it has run its span, summed from the stopwatch's steps between its reads under 20 µs (a longer step is time it was
  kept from running), within 10 s, and the lower bound is half of that; the upper bound stays 1.1 times the stopwatch span, which a
  stall only lengthens (on Windows a tick wider: clock interrupts come a tick apart). Under the same stops and pin it passed 6 of 6
  ("spun 319.57 ms, ran 125.00 ms, counted 130.16 ms"), and a measure that counts two fifths of the CPU clock fails it 2 of 2 ("the
  first 10 ms: ran 10.00 ms of 11.15 ms of the stopwatch, counted 4.46 ms").
- Open: with the fleet's part, which the exe composes, `MEDIA FETCH` runs `BackupNode.FetchMedia`, which writes no `media.transfers`
  row (so the desk's Network drawer and PLAY show none of its pulls), says no failed try while it tries again (its words reach the
  journal only as a file's end), makes its own unlimited bandwidth bucket for each pull (`MirrorPuller`), so it is not throttled
  while a show plays, and checks only that a file is not here, so a second `MEDIA FETCH` of a file pulls it beside the first. Running
  the fleet's pulls through the node's own pull's rows, try rule, bucket and on-its-way check closes it.
- Open: a backup's own pulls journal with no bound. Each mirrored revision's pull of the show's media and their versions
  (`BackupNode.PullFilesAsync`, run once per revision) journals a row per file it pulls, a refused one for each the primary cannot
  serve. The pull of the files the show names beside its folder (`PullShowFilesAsync`) journals a refused row for each that does
  not land, every time the watch asks for them again: about every 2 s for a file the primary refuses (a show that names a file the
  primary does not hold, say), and, for files whose bodies fall silent, once a pass that waits the quiet for each such file and
  then the watch's 2 s (about every 12 s when one file is silent). The context's window is the journal's 20 recent rows
  (`ContextBuilder.RecentRows`), so at the watch's pace one refused show file fills it in about 40 s and pushes every other row
  out; the journal coalesces no rows. No flood test covers these rows (a fetch's are bounded by `FetchJournal`, with flood tests
  that read the context). A bound per pass, as a fetch's, with a flood test that reads the context, closes it.
- Open: a backup pulls the files the show names beside its folder inline in its 100 ms watch (`WatchAsync` → `RepullAsync` →
  `RepullShowFilesAsync` → `PullShowFilesAsync` → `MirrorPuller.ShowFileAsync`), and a repull of the show from the watch
  (`MIRROR PROJECT`, which pulls those files before it applies the show) runs in the same turn. So a primary whose show file falls
  silent holds the watch, and with it the fence of a node that took the show, the follow of a group's new leader and the mirror
  state's publish, up to the quiet (10 s) for each missing show file every time it asks for them again, and an answer whose headers
  never come for the client's 60 s. Pulling the show files off the watch's own turn, with a test of the watch's pace while a show
  file is silent, closes it.
- Open, not reproduced or not explained (none is changed to pass): `ClockSlewTests`' 3 h 12 min step (its 597.5 s matches
  `pl_local`'s clamped anchor, so the failure is probably read wrongly; not reproduced); the one `FanoutTests` failure in a gate
  run (its test and line were not recorded, so the runner's misread above is its most likely cause, not a proven one);
  `FleetMonitorTests`' 10 s wait for the peer to read live (the test now waits until the headline and the context match the record
  the monitor holds; the 10 s "peer not live" is not reproduced); `OscServerTests.ASenderNotHeardForTenMinutesNoLongerCounts`' one
  Windows failure (consistent with the control tick's order fixed above, not proven; its wait now names what it saw);
  `BackupPreflightTests.TheBackupsFilesAndTheRoutesAreCheckedWithTheLineThatFixesThem`' one Windows read of the mirror as lost
  (consistent with the mirror link's real-time silence above — a stale spell, or its latch, both now on the node's clock or fixed
  — not proven: its `("red", "lost")` fits a dropped connection too; its reads now name what the backup saw);
  `LinkTests.ThePatternsLinkGoesStaleOnSilenceDropsAndRedialsAndQueuesNothingWhileDown`' two loaded Linux failures (one on
  fix-backup-preflight; one on fix-longpoll-win, its send at its line 231 then not answered ok once the link was live again, which
  of the link's refusals it was the assertion did not say; consistent with its polls racing the link's own stale-and-drop cycle in
  real time, which the test no longer counts on, not proven);
  `AnchorFeedLineTests.EveryMemberHasTheSameOneDeadlineHoweverManyThereAre`' one failure beside ten busy loops on Linux (twenty
  members silent where nineteen were due; consistent with its 100 ms real-time deadline passing before the test handed in its one
  member's answer after the send, not proven; its deadlines now run on a manual clock moved once the receipts are in, above);
  `HostNetworkTests.FetchPullsAFileByItsHashFromAPeerAndVerifiesIt`' one Windows wait for the pulled file (its cause not shown,
  above; its wait now names the transfer's stage); `HostNetworkTests.ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine`'s
  wait for the link's first clip in STATE (one failure on the batch's baseline);
  `HttpHostTests.TheLongPollWaitsForTheNextChangeAndGivesUpAtItsLimit`' one Windows long-poll answered before its check
  (consistent with the check's thread running late, above, not proven, a request that faulted not ruled out; the poll's limit now
  runs on a clock the test moves); `StageResourcesTests.TheAutomaticBlendWaitsForAQuietStageAndKeepsItsLastGoodMaps`' one Windows
  read of a solved blend just after its first settle (consistent with its continuation running later than the blend's real-time
  quiet time, above, not proven; the quiet time now runs on a clock the test moves);
  `PanicTests.RewindCuesTheItemAtItsInPointAtTheFadesEnd`' two gate failures (int-tests4's, and a loaded Linux gate's whose message
  was lost; consistent with its read of the journal racing the panic step, above, which the test now waits out, not proven);
  `ProjectorViewTests.TheProjectorViewIsTheOutputsPictureBeforeItsTrimAndBlackAround`' one loaded Linux failure (its message lost;
  consistent with its read racing the stage's builder, above, which the test now waits for, not proven);
  `ThreadTimeTests.AThreadsTimeCountsItsWorkNotItsWait`' loaded Linux failures (consistent with a thread given under half a
  processor, above, which its lower bound no longer asks of the machine, not proven).
- Open: a pause asked while the governor's let-go of the job is on its way waits, as it should, for the worker's next held line; but
  when the pause's knobs reach the worker before its encode thread has woken for the let-go, that thread never wakes (its hold waits
  on a `ManualResetEventSlim`, and a reset before the waiter runs leaves it waiting), so it says neither that it goes on nor that it
  holds, and the pause stays pending with no "paused at frame" row while STATE says held (the fact). A worker that says every let-go
  it takes, even one taken back before its thread ran, closes it.
- Open on Windows: the survey slice's budget is proven there only by the stopwatch. `ThreadTime` reads the lane thread's kernel and
  user time (`GetThreadTimes`), which Windows charges in whole clock ticks (`ResolutionNs` the system's clock increment from
  `GetSystemTimeAdjustment`, 15.625 ms by default); it is never precise there, and `Basis` says so and why the thread's cycles are
  not converted. Each slice is timed by the stopwatch too, and the survey view carries `LaneMaxMs` (the longest slice by the lane
  thread's time), `LaneResolutionMs` (that clock's step), `LaneMaxWallMs` (the longest by the stopwatch, which never reads below the
  work done in it), `LaneMaxBoundMs` (the longest by each slice's lesser reading, its thread time plus the step or its stopwatch
  time), `LaneMaxPauseMs` (the most the collections paused the process within a slice) and `LaneSlices` (`SurveyJobViewTests`).
  `SurveyImportTests` (4 × 10 ms) and `SurveyImportPerfTests` (10 ms) hold `LaneMaxBoundMs` to their budgets and fail with every
  reading, the step, the pause and `Basis`, and log what a pass stands on. On Linux the CPU clock proves both. On Windows the
  stopwatch proves a slice it reads within the budget, and a tick read bounds only a slice the lane thread ran in one stretch with
  no tick charged to an interrupt or a DPC: a slice broken into k stretches reads within k ticks of its work, and a tick that finds
  the processor in an interrupt handler or a DPC is charged to the processor. So a slice the stopwatch reads over 40 ms that passes
  the regular test on its ticks plus a tick is shown, not proven, and the twin's 10 ms, shorter than a tick, rests on the stopwatch
  alone: a stall inside a slice (a collection, a page brought in, another thread's turn) fails it, with its readings and the
  collections' pause. Both tests run in `SurveyPerfCollection` (`DisableParallelization`), so both run alone, after the parallel
  tests; the twin differs from the regular test in asking no SITE and holding no 2 M-triangle original. Here (Linux, Debug, warm),
  a slice holds 0.4 to 1.4 ms of the lane thread's time and 0.6 to 1.8 ms by the stopwatch, and, where measured, no collection
  paused the process inside one.
  `ThreadTimeTests` take the measure from their own reading of the platform: on Windows the ticks, not precise, each read allowed
  the tick it states; a thread blocked 50 ms counts at most one tick, so the stopwatch is refused; a spinning thread counts at most
  1.1 times its stopwatch span plus a tick, and a spin that ran eight ticks (125 ms) at least half of what it ran less a tick (three
  ticks), so a measure that charges a spin nothing, or only its kernel time, is refused; a spin runs until it has run its span,
  summed from the stopwatch's steps between its reads under 20 µs, so a turn the machine gave another thread, or a stop of the
  process, is in neither what it ran nor the lower bound (above). The node converts no thread's cycles. The Windows job runs
  the tick measure and passes `ThreadTimeTests` and both slice tests (CI run 312, 8ef15a2); a passing test's readings and `Basis`
  are in that run's test-results-windows trx, which this machine's proxy does not fetch, so what each slice read there is not yet
  recorded. The tick half proves a slice when the lab item below gives a precise clock. The evidence so far, from the
  windows-latest runner (a Hyper-V guest, Windows 10.0.26100 x64, 4 vCPUs, the invariant-TSC bit visible): run 306, which timed
  the slice by the stopwatch, failed the regular test at 42.62 ms against its 40 ms ceiling while the twin passed, in the same
  serial collection; fdef0b3's tick read then passed the regular test, but a tick read cannot verify the twin's 10 ms (it passed run
  307 only because no slice was charged a tick). The thread's cycles (`QueryThreadCycleTime`) converted at a rate measured once,
  from 16 spins of about a millisecond against the stopwatch, failed in turn: in run 308 the freshness rule refused the clock in
  Core's process (an equal back-to-back pair now and then) while the Engine's process used it; a probe (run 309, alone) found
  back-to-back reads about 260 cycles apart, the count current at each read, 1 ms spins at 2.57 to 2.596 cycles a nanosecond, and 0
  to 77 of 2000 pairs equal by thread; in run 310 the agreement gate ("only 2 of 16 spins … within 1% of the fastest") refused it
  in both processes; and in run 311, without that gate, `ThreadTimeTests.AThreadsTimeCountsItsWorkNotItsWait` failed: the
  calibration's fastest spin counted 2.153 cycles a nanosecond (0.46455 ns a cycle, 8 of 16 spins agreeing), and a later spin in
  the same process counted 2.439 ("spun 10.00 ms of the stopwatch, counted 11.33 ms"; blocked 50.0 ms, counted 37.7 µs). On that
  guest a thread's cycles a stopwatch nanosecond moved by 13% within one process (2.153 to 2.439) with the machine's load (Windows
  leaves interrupt time out of a thread's cycles, and a guest's interrupt and host load vary), so no rate measured once converts
  them to within the 10% the tests and a 10 ms budget need. Counting a slice's stretches would not close it: a thread's context
  switches are read from the system's process list (`NtQuerySystemInformation`), which lists every thread on the machine, too heavy
  a read for the lane, and a tick an interrupt or a DPC takes is lost inside one stretch.
- Open, lab: a precise Windows thread time needs the time-stamp counter's frequency from the platform, not a rate measured against
  the stopwatch. It is measured in the lab, with a thread's cycles at that frequency against its work, on bare metal and on a
  Windows 11 node with Memory Integrity on (Windows then runs as Hyper-V's root partition), and the ticks' step checked under the
  node's own timer claims: round 3's platform package (wp3-platform, L22 in docs/round3/PLAN.md §7; docs/ROADMAP.md).
- Open: a node's first survey import holds the lane longer than the 10 ms slice. Its slices compile the lane's import path: in a
  Debug build here they hold 12 to 22 ms of the lane thread's time (12.48 ms, and 14.49 ms by the stopwatch, run alone in a fresh
  process; 22.22 ms on a loaded machine), and an import after it about 1 ms. `SurveyImportPerfTests` measures an import after a
  small one has compiled the path, and logs the small one's slices; the regular test holds a first import to its 40 ms ceiling.
  Release is not measured cold; ReadyToRun or a pre-roll (round 3's M13) bears on it.
- Open on Windows: whether the Windows job's Spout test takes the real WARP round trip or its fallback is not verified.
- Of the desk-2 notes: the desk sends `VIDEO STOP`, `PANIC CLEAR` and `MASTER` at every press and presses a card's `APPROVE` once,
  as the owner decided (calls 7 and 8); still open, the throttle sends what it coalesced to the link it holds when it flushes, across
  a link change, and CALIBRATION HISTORY names no `standsAs` per row.
- Commit 00477fa carries the line sessions' change (the table joined before the session runs, the registrations one at a time)
  under the reseat test's subject; 085deb3, with the same subject, holds the reseat test alone and does not compile without 00477fa
  (`StageResources.BlendInUseFor`), so a bisect skips it.
