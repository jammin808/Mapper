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
  clock. In the desk's headless tests the ring's timer runs only when a test yields to the dispatcher (a pump runs the dispatcher's
  jobs, not its timers), and no test that holds a button yields while it holds, so none races the hold's 600 ms now: with the press
  pumped 650 ms late (a delay in a scratch copy) `DeskSpineTests`' hold test and the tests that hold through `DeskHarness.Hold`
  (`BlindChipTests`, `OnWallTests`) passed 3 of 3, and a scratch test that then yielded 100 ms saw the press complete on its own. A
  test that awaits while a press is held would race it; the desk's clock for the ring closes that.

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
  holds the new connection's reader on the primary's answer to its proof and its greeting before it subscribes, so nothing more is
  heard on it while the clock passes the stale mark; against the old words it failed three runs of three with the quiet words).
  The mirror's state, words and line are read from one look at the link, and a link
  that reads down or dialling holds no subscription whether or not the node's listener has cleared it yet: a preflight run between
  the link's move to down and that listener read `lost` "down" with no line (a listener ahead of the node's holding the link's
  thread 300 ms on that move failed the dropped row's assertion three runs of three; it passes now). FLEET BACKUP's refusal, the
  row's line and the watch's follow and repulls read that one predicate. A primary lights a backup whose mirror is lost red alike
  in STATE, on INDEX's key for it and in preflight's row for it; the key and the row were amber
  (`BackupMirrorLinkTests.APrimaryLightsABackupWhoseMirrorIsLostRedInStateOnItsKeyAndInPreflightAlike`). The link counted that
  silence (stale at 6 s, dropped at 12 s) on the process's clock, while the rig counts on hand-moved clocks the fleet monitor's,
  the pulls' and the sync part's watch of its leader's clock; it counts it on the node's clock now (`WireClientOptions.Clock`,
  which `MirrorLink` takes from the node). The engine's Patterns link and the sync part's anchor follower (`AnchorFollower`) count
  on the node's clock too (below); a follower's feed reads `connecting` while its anchor link is stale, and an otherwise identical
  mirror reads `joining` then.
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
  hand-moved clock (`PatternsLinkOptions.Clock`, which `PatternsLink` hands its link; the engine's Patterns link is given the
  node's, below). Stale at 400 ms and dropped at 900 ms of real time, with a ping every 2 s, its link went stale and dropped on its
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
- Done: a node writes STATE and CONTEXT before its doors open and says how long that took, a slow STATE names the part it went in,
  and a line the node host test is not answered names its stage; the cause of
  `NodeHostTests.TheNodeHostRunsAnEngineThatAnswersPing`' one Windows failure is open. What the job shows (fix-backup-preflight
  5ae2c481, run 39676825957, job 119488748563; the test 16 s, 91 s into an 8 min 22 s Engine suite of 1 175 tests): its line 44,
  the `@s STATE` ask, timed out, "no line within 10000 ms" from `LineClient.ReadAsync` (`HostRig.cs` 216, through 228 and 243),
  and its `PING` at line 43 was answered; nothing of the node (its log is not printed, and the job's test results were not
  fetched). So the node answered `PING` and wrote no STATE line within 10 s, and why is not shown. The branch's changes
  (`WireClient`'s clock, `MirrorLink`, `BackupNode`'s mirror) are not on that path: a solo node dials no link and
  `BackupNode.Mirror` answers null off a backup.
  5f6436d8 read it as STATE's first build (every package's writer run for the first time) paid on the reader's line, and its
  papers called that cause fixed; as measured, that build does not reach 10 s. On Linux (Debug, the node tool, llvmpipe) the first
  write took 253 to 268 ms, its thread working 248 to 260 ms of it (work, not a wait), 609 to 629 ms with the node on one core, and
  2 031 ms on one core beside four spinning loops (its thread working about 300 ms of it, the rest waiting for the processor), the
  node then up at 11.5 s; runs at d205f792 (unloaded, and pinned to one core beside 4 and 12 spinning loops: up at 11.0 and 23.3 s,
  the first STATE 2 074 and 4 607 ms) put the first STATE at 0.13 to 0.33 of the start's time. The CI node
  answered `PING` within about 6 s of its launch (16 s less the 10 s wait), which predicts a first build of 1 to 2 s. A wait of
  10 s or more that only STATE meets on that runner (a lock; a native or Windows-only call on STATE's path, such as Spout's memory
  share read from the registry on every write there, `StateVideoIo` → `WriteSpout` → `SpoutProvider.Status` →
  `Win32SpoutKernel.MemoryShare`, which no Linux forcing reaches), the node's process frozen or starved, or the test host's own read
  starved are not ruled out. 5f6436d8's forced runs stand: the first build held 12 s failed the unchanged test at line 44 with those
  words and that stack, its `PING` answered; the control lane, the render thread's first frame and every machine reading held
  (under the reader's lock, as the Windows reader holds its lock around PDH's collect) left STATE under 2 s.
  The host now writes STATE and CONTEXT on its start's own thread after the render host and the extensions start and before the
  network host opens the wire, the web port and the rest (`EngineHost.WriteFactsOnce`). 5f6436d8 wrote them after the doors and
  before the "is up" row, so a reader that dialled the moment the wire listened (the supervisor adopting from the pid record, a desk
  that redials) still paid the build under STATE's gate: probed as the log said the wire listened, that STATE took 284 to 349 ms
  then and 15 to 17 ms now, five runs each. A review's re-run (eight starts of the node tool, llvmpipe on one thread) put the first
  write at 268 to 323 ms, and a client of its own that sent `@s STATE` the moment the log said the wire listened was answered in 37
  to 46 ms (three runs): some hundreds of milliseconds and some tens, the spread the machine's. The "is up" row and log line say how
  long the write took, its slowest part and how much of it its thread worked (`EngineHost.FirstFacts`); every STATE write is timed
  part by part on the engine's clock (`StateParts`: STATE's top-level parts, each package by its hook word) beside its thread's own
  processor time, and a write after the first of 1 s or more that is the longest since the first is journalled as an ok row
  ("STATE took … ms (… ms in videoio, its thread working … ms) to write, the longest since its first"). Journalled first as not ok,
  that row reached the context's recent rows as "refused: STATE took …" (`ContextBuilder` then prefixed every row not ok so), a refusal
  that never happened, and the first write, which the "is up" row already says, was journalled too whenever it took a second (the
  review's start pinned to one core beside eight spinning loops: 2 713 ms) and was the measure every later write had to pass, so a
  slow reader's STATE after a slower first build went unjournalled. A writer
  that faults there is journalled and logged once under its own name (STATE or CONTEXT); 5f6436d8 kept it as a standing host fault,
  which STATE's faults and the context's red items went on claiming after STATE wrote cleanly, and named a CONTEXT fault STATE's.
  The test reads the node's output and errors as they come (a redirected pipe nobody reads fills, on Windows at the system's
  default of a few KiB, and holds a node thread that writes to it; the headless node writes about 0.7 KiB there on Linux, ALSA's
  lines, and what it writes on the runner is not shown), asserts STATE's id and name are those its fresh folder's settings hold
  (the `PING`'s stamp is a constant other tests share, so its echo shows no node in particular; the port, read from that node's
  own log in its fresh temp folder, does), and when a line is not answered within 10 s says whether it was answered within 50 s
  more and when, whether a second connection's `PING` (with the node's read and write stamps) and `CONTEXT` were answered, whether
  the process lives, and the ends of its journal, log and output. Forced on Linux (a hook kept out of the tree holding each STATE
  after the first in its videoio part): held 12 s, it said "answered 12.0 s after it was asked", the second `PING` in 0 ms and
  `CONTEXT` in 2 ms, and the journal's "STATE took 12010 ms (12001 ms in videoio, its thread working 10 ms) to write, the longest
  yet" (the row's words then); held 120 s, "not answered within 60 s" with that `PING` and `CONTEXT` answered and no such row (a
  write stuck in STATE in a live node, its part not named: the node's thread stacks would name it); the node stopped (SIGSTOP) at
  that write, "a second connection's PING not answered within 10 s; the node runs" (a frozen process). `HostLifecycleTests`'
  `TheHostWritesStateBeforeItsWireListensAndSaysHowLongItTook` (the write after the doors: the wire had listened on 44363 at
  STATE's first write), `ANodeWhoseStateFaultsAsItStartsIsUpAndSaysWhy` (the fault kept standing: the host's faults not empty)
  and `ASlowStateWriteIsJournalledWithThePartItWentIn` (a part's proposal count moves the node's clock inside STATE's "ai" part:
  the start's write two minutes, said in the "is up" row and journalled nowhere else, then a minute, half a minute and two minutes,
  the minute and the two minutes journalled, ok, each naming "ai", the minute in the context's recent rows as written; it failed
  with no slow row before 4e18f961, on the start's row under 4e18f961's rule, and with the row journalled not ok on its ok) each
  fail with only its behaviour reverted and pass now, the last also three runs of three pinned to one core beside eight spinning
  loops;
  `AStateWriteAfterTheFirstIsJournalledFromASecondWhenItIsTheLongestSince` runs the rule on a manual clock only STATE's writes move
  (a first of 5 s not journalled, 999 ms not, 1 000 ms journalled, 1 000 and 999 ms again not, 1 500 ms journalled), so the bound is
  pinned at a second exactly (with it set to 0 the 999 ms write is journalled and the test fails);
  `ANodeWhoseContextFaultsAsItStartsIsUpAndSaysWhy` holds the CONTEXT half (a part whose faults fault once, on the thread that has
  just written STATE's "ai" part, so only the start's CONTEXT faults: its row is CONTEXT's, before "is up", which says STATE's time
  and "CONTEXT not written"; no fault stays; it fails with that fault named STATE's); 5f6436d8's
  `TheHostSaysItIsUpOnlyOnceItsStateHasBeenWritten` passes as before. The Windows job passed the node host test at 39caa04c (run
  39727116245), its one run there since; one pass shows nothing of the cause.
  In the same review, `MachineSampler`'s 1 Hz timer took a reading on every tick with no guard while the Windows reader holds its
  lock around PDH's collect, so a stalled reading parked one more pool thread each second without bound; a tick now takes none while
  a reading is in flight (`MachineSamplerTests.TicksWhileAReadingStallsTakeNoneOfTheirOwn`: without the guard its second advance
  parked in the reader and its 10 s wait ran out; now one reading in all until the held one returns). It is not on STATE's path.
- Done, off the open list: the context's recent rows say what each row is. A row that is not ok is prefixed by its kind
  (`ContextBuilder.Build`: `r.Ok ? r.Text : r.Kind + ": " + r.Text`), so a refusal still reads "refused: …", a fault "fault: …", a
  sync warning "sync: … went quiet: free-wheeling …", a decode or media word "decode: …" or "media: …" and a claim Windows refused
  "machine: … was refused: …"; an ok row reads as written. Every row that was not ok read "refused: " before, so to MCP's
  get_context, Patterns, Stagewatch and the assistant a fault or a warning was a refusal of a line (and a claim Windows refused said
  "refused: " twice). `ContextRecentTests.ARowThatIsNotOkSaysItsKindAndAnOkRowReadsAsWritten` reads a fault, a sync warning, a
  machine claim refused, a refusal and two ok rows (a verb's and a sync row) in the context's window: against the old prefix it
  failed 3 runs of 3 (every row that is not ok "refused: "), and it passes 3 of 3 now.
  `VerbRowTests.AnImportsOwnRowsComeAfterTheRowOfTheVerbThatBeganIt` reads its media word as "media: …" (3 of 3 failed against the
  old prefix); the fetches' rows that `VerbRowTests`, `HostNetworkTests` and `BackupTests` read there are refused rows and read as
  before, their expectations now made from each row's kind.
- Done: a verb's own journal row comes before every row of the work its handler leaves running. The dispatcher writes a verb's row
  once its handler has answered, on the thread that awaited the answer (for a lane verb, a pool thread after the lane's turn), and
  work the handler had begun ran beside that write. `MEDIA FETCH`'s handler began `NetworkHost.FetchAsync`, which yields to the pool
  and starts the pulls, so a peer whose list is refused at once could end the whole fetch (its first four refused rows and the row
  that counts the rest) before the verb's row was written: the journal and the context said the fetch ended before the verb that
  began it. That is `HostNetworkTests.AFetchOfMoreFilesThanTheJournalHoldsFromAPeerWhoseListFailsLeavesTheNodesOwnRows`' one Windows
  failure, at its line 231 (run 420, job 119506902541, at fix-backup-preflight 93f05df9, which touches none of this code; run 419,
  green on both jobs, passed it on Windows, and it passes on Linux): the context's last row was "fetching 250 files", the verb's own
  row, where the test reads the fetch's end row, which it had waited for and found. So the log shows the verb's row written after the
  end of every pull, 1 min 34 s into an Engine suite of 8 min 19 s (1 172 passed, 1 failed), the test itself taking 1 s. It does not
  show why the dispatcher's write came so late: a busy thread pool on the runner is consistent with it, not proven. On Linux, holding
  the verb's row 3 s before the dispatcher writes it (a local edit, not kept) failed the unchanged test at line 231 with the same
  words; holds of 0, 5, 15, 40, 55, 70 and 85 ms passed it three runs of three each and 100 ms failed it three of three (Debug, one
  machine), so there the pulls ended between 85 and 100 ms after the handler answered. Now a handler hands the work it leaves running
  to `EngineCore.AfterRow` on the lane (`VerbCall.AfterRow` off it, `ForLine` for a lane part of a line answered off it), and the
  dispatcher begins it once it has written the line's row, whatever that row says, or at once when the line writes none, before the
  line is answered; with the same 3 s hold the test passes three runs of three. The fetch's own accepted row was the same order one
  level down: `NetworkHost.FetchAsync` began the pulls and only then answered what it had queued, which `MEDIA FETCH` journalled as
  the `media` row "fetching 250 files from Dead list" once the call had answered, so a stall of that one thread between the two
  statements put that row after the fetch's end row. The Windows run's last row was the verb's own ("fetching 250 files"), so there
  it was the dispatcher's write that came late, not this one. The review that found it held the thread there 80 to 140 ms on Linux
  and failed the unchanged test at line 231 with "fetching 250 files from Dead list" as the last row (reported, not rerun here);
  `HostNetworkTests.AFetchsRowsFollowTheVerbAndWhatWasQueuedWhenItsPeerEndsEveryPullInsideTheFetch` ends every pull inside the call
  and, with that order put back, finds the edge's row after the end row three runs of three. Now the edge journals what it queued
  before it begins the pulls (`INetworkControl.FetchAsync`'s contract), and `MEDIA FETCH` journals only a fetch that could not
  start. The sweep of the work a line leaves running that journals, each now begun, or journalled, behind the line's row: `MEDIA
  FETCH` (the node's own pull and the fleet's, `BackupNode.Fetch`), `LINK PATTERNS`, `LINK STAGEWATCH` and a node's own `SYNC
  LEAD|FOLLOW` without the sync part (the change's end row, journalled inside the handler when the edge answered at once), `LINK
  PATTERNS FOLLOW-BLACKOUT` with the sync part (the switch's write beside the lane, a fault row when it fails), `SCAN START` (the
  runner's run and the scan's end), `SURVEY IMPORT` (the worker: a file that cannot be read ends the import with a refusal at its
  first read, so the worker begins behind the row and a stop waits for it from the answer on), `MEDIA IMPORT` (the import itself,
  whose own row says a file will not play here or FFmpeg is missing, and how it ends), `BLEND SOLVE` and the blend solved again by
  `CALIBRATION ACCEPT|FALLBACK|RESOLVE`, `PANIC` (its shutter and effect lines, each a line of its own with its own row, sent from
  the panic's dispatcher with no further hop, and from a pool thread when begun on the lane; so the panic is answered once its
  dispatcher has begun each line, one after another, up to that line's first wait: the show's gate, the projector link's lookup
  and as much of its command as runs before it waits, or an effect line's post to the lane. That is work in the panic's answer
  that a pool hop per line would take out of it, at the cost of a hop before each shutter; the air change is made before it, in
  the panic's own turn, and the group's fan-out goes with the panic's row), `PREFLIGHT` without `WAIT` (its run),
  `MEDIA OPTIMISE` (its jobs stay queued, handed to no worker, until the row: a job whose item is not on the node ends failed at its
  first step, which the service's loop could take before the row), `BIND LEARN` (the learnt and stopped rows wait for its row; the
  learn is armed before the answer), `SHOW ARM|DISARM` (the hooks the sync leader's line runs behind its answer, and a hook any arm
  leaves running past its deadline, whose fault is journalled when it ends), `MIRROR PROJECT` (the pulls of the files the mirrored
  show names that are not here), `MIRROR HANDBACK` (the takeover) and an assistant's line the armed show holds for a person (its
  offer to an approval desk, whose refusal names the proposal the line's own refusal gave its id: `ProposalQueue.Add`, then
  `OfferAsync` behind the row). `PROJECTOR SOLVE` and `SCAN SURFACE` leave nothing running (their solves and edits are awaited
  inside the handler); the operations dispatcher hands each its own `VerbCall` and takes it after the row, as the other dispatchers
  do. Rows a handler writes while it answers (a panic's playlists, a reconnect's words, `FAILBACK`'s, a `PREFLIGHT WAIT`'s run, the
  lines a `CONTROL INJECT` fires, the keep-awake hold Windows refuses, which `MACHINE SET AWAKE ON`'s save and an edit that
  enables a display output each journal as they ask for it) come before its row, as they always did, in a fixed order.
  Every dispatcher begins the held work
  in a `finally` after the row, so a row that throws, or a test's hold on it, loses none of it. Parts that see a verb's change on a
  turn of their own are an open fix (below). Tests: each makes the work end as early as it can, inside the edge's own call or with
  the line's row held back (`EngineCore.RowHeldForTests`, which each dispatcher but a member's air line's awaits just before the
  row, inside the `try` of that `finally`, told whether the line holds work behind it) until
  the work's first row is in, and each of these failed three runs of three with the order before it put back (a local edit, not
  kept) and passes now: `VerbRowTests`' `AFetchsRowComesBeforeEveryRowOfItsPullsWhenTheyAllEndBeforeTheFetchIsAnswered` (its edge
  journals what it queued, as the contract now says), `AnImportsOwnRowsComeAfterTheRowOfTheVerbThatBeganIt`,
  `ASurveyThatCannotBeReadIsRefusedAfterTheRowOfTheImportThatBeganIt`, `APreflightsRunIsJournalledAfterTheRowOfTheVerbThatStartedIt`
  (unforced at first, it failed 3 of 13 runs against the product before the fix; held, it fails every run),
  `ABlendSolveIsJournalledAfterTheRowOfTheLineThatAskedForIt`, `AHookLeftRunningPastItsDeadlineFaultsAfterTheRowOfTheArmThatLeftIt`,
  `TheSyncLeadersArmRunsItsHooksBehindItsRow`, `APanicsOwnLinesAreJournalledAfterThePanicsRow` and
  `AProposalADeskDoesNotTakeIsSaidAfterTheRefusalThatNamesIt`; the `HostNetworkTests` test above;
  `BackupTests.AMirroredShowsFilePullsAreJournalledAfterTheRowOfTheMirrorLineThatBeganThem`;
  `SettingsWriterTests.AFollowSwitchTheFileCannotKeepIsSaidAfterTheRowOfTheLineThatSetIt`.
  `VerbRowTests.ALinksRowComesBeforeTheRowThatSaysHowTheChangeEndedWhenItEndsAtOnce` failed three runs of three against the product
  before the fix and passes now; `WorkHandedToALineIsHeldUntilItsDispatcherTakesItAndBegunAtOnceAfter` holds `VerbCall`'s hand-over.
  A package's lane handler's `VerbCall` goes to the line's dispatcher with the lane's turn, and the dispatcher takes what was
  handed over through it once it has written the row, so work the handler left running that hands its row over through the call
  after the handler has answered still follows the row: `WorkALaneHandlerLeftRunningHandsOverThroughItsCallAfterTheAnswerStillFollowsTheLinesRow`
  failed three runs of three when the router took the call's work at the handler's answer (the work's row first) and passes now.
  No handler does that today. `VerbCall` is a class, one per line, so no copy of it holds work its dispatcher never takes.
  `WorkALaneHandlerHandsOverThroughItsCallFollowsTheLinesRowOnEveryLaneDispatcher` runs such a line from the wire, from a binding
  and as a member's air line, each with a start its handler hands over through the call while it answers and one handed over after
  the answer, at the last moment before the dispatcher takes the call (in the row's hold; on an air line, which has no hold, as its
  row is written): both follow the row, in that order. With the binding's or the air line's dispatcher not taking the call (its
  line's own held work begun in place of `TakeWork()`) those legs lose the call's work; with `HoldsWork` blind to the call the row's
  hold is told the wire's and the binding's lines hold none; with the router's old take every leg puts the late start before the
  row or before the earlier one. Each leg named failed with its order put back (a local edit, not kept: one run for each dropped
  part, three of three for the old take), and all three legs pass three runs of three.
  These failed three runs of three with the order before them put back (a local edit, not kept) and pass now, each with the line's
  row held until the work's first row is in: `VerbRowTests.AScansEndIsJournalledAfterTheRowOfTheStartThatBeganIt` (a scan backend
  whose scan has ended when it answers), `AnOptimiseJobThatCannotRunIsJournalledAfterTheRowOfTheVerbThatQueuedIt` (a margin of free
  disk no disk holds: the job fails at its first step, before any worker starts) and
  `ALearnsRowIsJournalledAfterTheRowOfTheBindLearnThatArmedIt` (the control moves just before the row is written: the binding is
  made then, its row after); `BackupTests.AFleetFetchsRowsFollowTheRowOfTheVerbThatBeganThem` and
  `AHandBacksTakeoverIsJournalledAfterTheRowOfTheMirrorLineThatAskedForIt` (the takeover's `SYNC LEAD` and its own row);
  `ResolveTests.AReSolvesBlendIsJournalledAfterTheRowOfTheResolveThatSolvedIt` (the blend a `CALIBRATION RESOLVE` solves again, its
  lane part through `ForLine`). `LINK STAGEWATCH`, and `SYNC LEAD|FOLLOW` on a node without the sync part, take the link test's
  path (with the sync part, which the exe composes, `SYNC LEAD|FOLLOW` await their change inside the handler and say it in their
  own row; the leader's feed a follow then reads is open, below); `CALIBRATION ACCEPT|FALLBACK`
  solve the blend in their line's own turn on the lane, as `BLEND SOLVE` does; the scan runner's own row (a camera latency below
  zero, journalled by its run, which begins behind the row) has no forcing test. The 250-file tests, the node's own and the fleet's
  (`BackupTests.AFetchOfMoreFilesThanTheJournalHoldsWithTheFleetsPartLeavesTheNodesOwnRows`), read the fetch's end row as the last.
  The runs first reported as three of three kept a log of one run, cut off at its last test, for the first twelve forcing tests
  above and none for the six after them. Run again at b93f06ff with every one of those orders put back at once (each start begun
  at once where it is handed over, the seams kept) and the link change's too, the eighteen forcing tests and
  `ALinksRowComesBeforeTheRowThatSaysHowTheChangeEndedWhenItEndsAtOnce` failed three runs of three, each with the work's row ahead
  of the line's (the mirrored show's pull and the hand-back's takeover before their `MIRROR` rows), and all nineteen passed three
  runs of three as committed.
- Open: a part that sees a verb's change on a turn of its own journals what it sees then, which can be before the verb's own row,
  since the dispatcher writes a lane verb's row once the lane's turn is over, on another thread. A lane tick that runs first (a
  timecode chase's first step, a blind commit landing at its instant, the sound input the tick opens) and a part on a thread of its
  own (the video I/O hub's word of a reconnected input's first frame, the sync node's rows of a leader's feed after `SYNC FOLLOW`, a
  backup's watch and its mirror link's own lines after `FLEET BACKUP`, a Stagewatch operator's word on the link after `LINK
  STAGEWATCH`, a projector link set up again when an edit moved the project, the optimise service's automatic queue when an edit
  lands a hashed item, the render host's frame lock falling out after `SYNC APPLY`) can
  each write first. None is measured and no test covers them. Writing a lane verb's row in the lane's own turn would order the ticks
  behind it; the other parts would need to hold a line's rows until its row.
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
  out. This reading replaces a step in 300b36ad's message, which brought the fix: that message read the 756 ms on Linux's start
  and stop, and so put the check about 0.7 s in.
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
  On a node's own clock (`TimeProvider.System`) the fix changes three things a client or a reader sees. Every wait for a change,
  the long-poll's and the WebSocket sender's, is whole milliseconds rounded up, so a poll with nothing newer ends its wait up to a
  millisecond later than it did and never spins through the last part of one; the sender's waits are its `BeatEvery` (2 s in a
  node) or 1 s, whole milliseconds, so its beats are as they were. A poll the web port's own stop cuts is answered 503, not an
  empty 200. `HelloService.Heard`, which only tests read, counts a hello after the peer table has taken it in.
- Done: the stage's automatic blend counts its quiet time on the clock its options give (`StageResourceOptions.Time`; the render
  host keeps the system's), and its tests move that clock.
  `StageResourcesTests.TheAutomaticBlendWaitsForAQuietStageAndKeepsItsLastGoodMaps` read the tables for no blend just after its
  first settle, and for the last good map just after its second, while the blender counted its 150 ms of real time from the
  builder's signal on threads of its own, so a solve could reach those tables in two ways: the test's next step running later than
  the quiet time and a solve, or the builder running late between its signal to the blender and the tables it then builds, whose
  last assembly takes in whatever the blender has solved by then. It failed so on the Windows job (run 425, at fix-nodehost-win
  39caa04c, which does not touch the stage) at its line 75 (`Assert.Null()`, `BlendMap { OutputId = op_left, Width = 480,
  Height = 270 … }`), consistent with either on the loaded runner, not proven, and its words do not tell the two apart. Each,
  forced, failed the unchanged test 3 of 3 at that line with those words: the test's own continuations held 3 s each (a context of
  one thread that runs what is posted to it no sooner than 3 s later, the stage's threads free; held 0 s it passed 3 of 3), and the
  builder held 5 s just after its signal to the blender (the test's continuations free).
  `ABlendTheVerbSolvedIsTakenAsItIsWithoutSolvingAgain`'s count of no solves held only while the test outran its 30 s quiet time,
  and held 31 s it failed 3 of 3 at its line 107 (`Expected: 0`, `Actual: 1`). The first test now moves the clock once the blender
  waits on it, a tick short of the quiet time (the wait stands, nothing is solved) and then the last tick (the wait ends); it passed
  3 of 3 under each hold above, and fails 2 of 2, timed out waiting for the blender's wait, against a blender that counts on the
  system's clock. The second moves the clock through its quiet time once the offered blend is in use and, when the blender has
  ended that pass (`StageResources.BlendPasses`, counted once what a quiet time led to is done), asks for no solve and the offered
  map still up: the blender's taking inputs already solved or offered as solved was not run by the test before, whose quiet time
  never ran out. With that check taken out of the blender it fails 3 of 3 at its line 134 (`Expected: 0`, `Actual: 1`), where the
  test before the change and a form of it that never moved its clock passed 3 of 3; it passed 3 of 3 with its continuations held
  31 s and 3 of 3 with the builder held 5 s.
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
- Done, its cause not found: the survey import tests' ready checks fail with the import's own words and the test host's heap against
  its limit (`SurveyImportTests.AssertReady`; `SurveyJobViewTests.AnImportThatIsNotReadyFailsWithItsWordsAndTheHeap`).
  `SurveyImportPerfTests.TheImportNeverHoldsTheLaneMoreThanTenMillisecondsInASlice` failed once in a loaded Linux gate (load up to
  about 23) and once earlier under the full suite; the gate keeps only a test's [FAIL] line, so which assertion failed is not known.
  Its slice check reads the lane thread's CPU clock on Linux, which a preemption or a stop does not move: with the test host stopped
  10 ms in every 13 for the whole run it passed (the longest slice 1.88 ms of the lane thread's time, 25.79 ms by the stopwatch).
  Its ready check fails when the import is refused for memory: under the gate's 1.5 GB heap limit (`DOTNET_GCHeapHardLimit`) the
  2 M-triangle import needs some 0.75 to 1 GB beside what the host holds, and with 800 MB held live in the test host by a startup
  hook the unchanged test failed 2 of 2 at its line 485 with no reason (`Expected: "ready"`, `Actual: "failed"`), as the full-suite
  failure kept from 2026-10-19 reads (600 MB held, it passes); it now fails 2 of 2 with "the import … is failed, not ready: North
  front was not imported: the file is too large for this machine's memory — thin or split it first; the managed heap 1456 MB
  allocated now, 1456 MB and 1494 MB committed at its last collection, of the 1536 MB this process may use" (what is allocated is
  read at the check, the heap's size and commit as the last collection left them). A full Engine run here had 31 MB live when the
  test began, so what would hold some 0.7 GB in the gate's host is not found, and its failure stays watched (below). `ThreadTime`'s
  summary says what its reads count: never a stop the thread waited out (another thread's collection, a page read in from disk), but
  a collection the thread runs itself and the kernel's work on its own page faults, which a slice's 10 ms therefore holds.
- Done: every lock the air's threads take beside another thread frame by frame makes its wait event as it is made, and each of
  those threads has its own wait record before its first frame, so no first wait allocates on a show path. A `Lock` makes its wait
  event (an `AutoResetEvent` and its `SafeWaitHandle`, 56 bytes, or 280 when a collection has dropped the runtime's cache of the
  handle's type) on the first thread that has to wait for it, and a thread makes its wait record (160 bytes on Linux) on its own
  first wait. The render host's gates were made bare, so the frame that first met a reader holding one paid for it, at whatever
  frame that came. Two gates every frame takes inside its counted span are read from other threads: the frame-time gate, under which
  `Stats` and the present view copied and sorted the frame-time rings, and the health gate, under which `Health`, `Present` and
  `Machine` build their lists for the fleet sampler and STATE while each frame writes every output's numbers.
  `DeviceLockHostTests.TheLoopDrawsInsideTheDeviceLockAndNeverWaitsPastItsBound`' one Windows failure at its line 234 (main
  2724ccd1, run 427, job 119670930217: `Expected: 212744`, `Actual: 213024`, 280 bytes on the warm frame path across its 100 ms
  hold and 30 frames; the same commit passed in runs 424 and 430 and on Linux) is consistent with either, not proven: the hold's
  sharer reads `Stats` as it starts and ends, STATE reads the health gate meanwhile, and a reader preempted inside either makes the
  frame's write its first wait. On Linux a scratch build that kept the window's sharer read inside the frame-time gate for 20 ms
  failed the unchanged test at that line by exactly 280 bytes in four runs of four, and a dump of the render thread stopped just
  after that write held the event, its handle and the two cache objects (24 + 32 + 168 + 56 bytes); the device lock's own timed-out
  takes allocate nothing after its first.
  Core's `LockWaits` (moved from Media, whose decode threads' queue, pool and counters already used it) prepares, as each is made:
  the render host's five gates (frame times, health, frame waiters, views, draft views), the frame lock host's, the sources' busy
  gate and lease pool's, the device lock, the upload arena's ranges and the GL arena's own lock, the sting channel every frame reads
  its sting from, the LED frame pool and the LED sender's gate, a sound output's gate and the process-wide OpenAL context gate, a
  capture input's lock, frame pool, lease pool and counters, a still's lock and lease pool, a live input's lease pool, a sender's
  offer and stats gates, a HAP clip's packet ring and arena gate and the shared HAP workers' job ring, the composed platform's
  record of every deadline wait's lateness and of the claims (`RecordedPlatform`, on every platform), and on Windows the platform's
  own lateness window. A thread that claims its role (render, decode, sound, LED, control) makes its wait record with the claim
  (`ThreadClaims.Claim`); HAP's read and decode threads, an image sequence's and a capture input's make theirs as they start, and a
  send worker's first act is a wait. `Stats` and the present view hold the frame-time gate only for the copy and sort on their own
  stack. Preparing waits, where the lock is made, for one timed take of each lock (a millisecond, up to a timer tick on Windows): off
  the frame for every lock but the GL upload arena's two, which the render thread makes with the context's first frame with an
  engine (and again after a lost context), never on a warm frame.
  `RenderHostLockTests.AReaderHoldingTheFrameGateAcrossAFrameCostsTheFramePathNothing` takes that gate between two frames and holds
  it until the next frame waits on it: against the old host it failed six runs of six by 280 bytes (`Expected: 213360`, `Actual:
  213640`). `AWriterHoldingTheStingChannelAcrossAFrameCostsTheFramePathNothing` does the same with the sting channel, which the frame
  reaches as it composes, once its sources have begun it: against the old channel it failed three runs of three by 280 bytes
  (`Expected: 213872`, `Actual: 214152`). `EveryLockTheRenderThreadTakesBesideOtherThreadsHasItsWaitEventBeforeItsFirstFrame` reads
  each such lock's first wait, the sting channel's among them, through `tests/Shared/LockWaitProbe.cs` (a prepared waiter thread,
  the lock held by the test): nine runs of nine failed against the old host, 280 bytes on six to eight of its eight locks, and one
  run of three against the old channel; a lock the render thread takes every frame can read 0 on old code, as the render thread can
  be the one that waits first during the probe's hold. `LockWaitsTests` (the device lock, its first timed-out take behind a sharer,
  the arena's ranges, the LED pool, the sting channel), `LedSenderTests`, Media's `PerformanceTests` (a sound output; the capture
  input's, still's, live input's and HAP locks, 280 bytes on each of ten), `SendSinkTests.ASendersLocksAreMadeWithTheirWaitEvents`,
  `DeadlineThreadTests.TheLatenessRecordEveryDeadlineWaitWritesIsMadeWithItsWaitEvent` and `GlUploadArenaTests` failed three runs
  of three each against the old code at 280 bytes a lock; `LockWaitsTests.AClaimedThreadsFirstWaitOnAPreparedLockAllocatesNothing`
  failed three runs of three at 160 bytes, the thread's record; on Windows `LatenessTests` reads the lateness window (not run here).
  All pass now. Read and left: the stage resources', stage meshes' and timer view's locks, which the frame path does not take (the
  timer view's only as its poller starts); the runtime deadline timer's, which only its own thread takes; a preview sink's, which
  the render thread takes drawing a draft view between frames, outside the counted span; the GL share's and the dmabuf importer's
  books of import failures, which a frame writes only when an import has failed; the NVIDIA frame lock driver's, which only its
  once-a-second read of the board and its dispose take; and a live input's tally, set by the video hub's thread. The render thread can still wait a moment
  behind a reader holding the health gate or the frame lock host's: that wait allocates nothing.
- Done: the mirror test that moves the backup's clock before a new connection subscribes hears nothing on that connection meanwhile.
  `BackupMirrorLinkTests.AMirrorQuietBeforeItsNewConnectionIsSubscribedSaysItIsNotSubscribedAndIsOfferedTheLineThatJoinsItAnew`
  holds the new connection's reader on the primary's answer to its proof (`MirrorLink.PairingChanged`, which hands on the link's
  pairing moves) as well as its greeting before it subscribes, and moves the clock past the stale mark once. Holding the greeting
  alone left the reader free: the mirror link says HELLO, which makes it a STATE listener on the primary, and the primary pushes
  STATE to every listener 200 ms of real time after any change (`WireServerOptions.PushThrottle`): after the new connection's own
  HELLO, and after each move of the clock, which moves the primary's peer table and fleet monitor on the same hand-moved clock. The
  link drops those pushes but its reader counts each as heard (rightly: the primary spoke), and its beat looks every 250 ms of real
  time, so a move went stale only if a look fell between it and the next push. The test moved the clock at most twice, and when both
  moves were absorbed it read no stale move and line 172 compared against `FirstOrDefault`'s default, `(Connecting, null, null,
  null)`; the link had neither read Connecting nor dropped. That is the test's one Windows failure (branch fix-fetchrow-win
  d3df7fb5, run 431, job 119794922607, after 7 s, on a branch that touches neither the backup nor its links), whose actual is
  exactly that default: consistent with a push landing before the beat's look on the loaded runner, not proven. On Linux the
  unchanged test, its process stopped for 100 ms from 205 ms after the redial, failed at line 172 with that tuple in six runs of
  eight, and passed three of three with the same stop from 400 ms; a traced copy made while diagnosing it, not run again since, saw
  the pushes land 222 ms after the first move and 209 ms after the second, each before the beat's next look. The test now passes
  eight runs of eight under the 205 ms stop, four of four with it pinned to one CPU as well, and one each under stops of 150 ms from
  0, 40, 120, 250 and 300 ms; the class passes. It keeps every assertion, its wait for the stale move now for `Stale` alone. No
  product behaviour changes; that the mirror link and the anchor follower are STATE listeners that drop every STATE line is an
  efficiency question, open below.
- Done: a follower that takes its clock from its group's new leader (`BackupNode.RetargetAsync`) raises STATE's push once the epoch
  is the node's and writes the row of the follow after that, with `Persist`'s words when the settings file could not keep the
  epoch, as the fleet's other changes carry them; `PromoteTests` waits for that row before it reads it.
  `PromoteTests.AFollowerPromotedLeadsOnTheSameTimeLineAndTheOtherFollowsIt` waited for follower 2's STATE to show the new leader,
  a live feed, no hold and epoch 1, then read its journal once for "leads show now (epoch 1)". The follow publishes the epoch
  (which `STATE.sync.epoch` reads), then writes the settings file durably (read fresh, flushed, renamed into place), and only then
  writes the row, which says whether the epoch was kept: a reader can see the epoch while the file is still being written, and read
  the journal before the row. It failed so on the Windows job (run 439, job 120079075197, fix-win-batch2 4f38560e, which does not
  touch the fleet) at its line 113 (`Assert.Contains()`, `Not found: "leads show now (epoch 1)"`), every assertion before it
  passing, consistent with a slow settings write or the watch's thread held between the epoch and the row on the loaded runner, not
  proven: the log times neither. With the row held 1 s before it is written (the test unchanged, the hold in a copy of the journal)
  the unchanged test failed 5 of 5 at that line with those words, and passed 2 of 2 unheld; every settings write held 250 ms did not
  make it fail in 4 runs (the test's own steps then wait longer). The test now waits for the row, as `BackupTests` waits for the
  fence's, and keeps every assertion; it passed 3 of 3 under the same hold against the old product and against the new.
  The follow also dropped `Persist`'s words, so a follower whose settings file refused the write said nothing of it, and it raised
  no push, so STATE showed the old epoch until another fact moved (0.3 to 0.96 s in traces while the test read it every 20 ms).
  `AFollowerWhoseSettingsCannotKeepItsNewLeadersEpochSaysSoInTheRowOfItsFollow`, its settings refusing every write, asks the row for
  " — not kept for the next start (the node's settings could not be written: disk full)" and not ok, STATE read as the row is
  written for the new leader and epoch 1, a push raised between the epoch's move and the row, and the file still at epoch 0. It
  failed 3 of 3 on the words against the old product; with the words carried and no push, 2 of 5 on the push (in the other three
  another fact's push fell in that window; the watch now counts only the push of the row's own writer, below, and so fails every
  run without it); with both, it passed 3 of 3 under the hold and 3 of 3 without.
- Done: a step-down, a backup's kept epoch, a primary's role and a hand-back say when the settings file could not keep them, once
  STATE holds them. A leader that steps down to a member leading its group under a higher epoch (`StepDownAsync`) did as the follow
  above did: it dropped `Persist`'s words and raised no push; it now pushes STATE, then writes its row with the words, not ok when
  the epoch was not kept. A backup that takes the higher epoch its primary is heard to hold (`KeepPrimaryEpoch`), a node a backup
  joins becoming its primary with that backup (`OnMembersChanged`) and a node that handed its show back taking the epoch it was taken
  back under (the hand-back in `LookAsync`) dropped the words too, so a node whose file could not keep them would, after a restart,
  take over under an epoch that fences no one, go out without waiting to hear its backups, or be fenced by none of them, having said
  nothing. The first two now say so in a fault row and the hand-back in its own row, each after STATE moved and was pushed (the
  epoch's move raised none before). With the settings refusing every write,
  `ALeaderWhoseSettingsCannotKeepTheEpochItStepsDownUnderSaysSoInTheRowOfItsStepDown`,
  `ABackupWhoseSettingsCannotKeepItsPrimarysEpochSaysSo`, `APrimaryWhoseSettingsCannotKeepItsRoleSaysSo` and
  `ANodeWhoseSettingsCannotKeepTheEpochOfItsHandBackSaysSoInItsRow` ask each row for its words, not ok, STATE read as it is written
  for what it reports, a push raised before it, and the file as it was; against the product before this change each failed 2 of 2
  (the step-down's and the hand-back's rows without the words, no row at all for the other two), and with it each passed 3 of 3.
  Their watch (`RowWatch`, as the follow's above) counts only a push the row's own writer raised: the fact moves and its row is
  written on one thread with no wait between, and a push another thread raised in that span, for a fact of its own, let a missing
  push pass. Counting any push, the product built without the follow's, the step-down's, the kept epoch's and the hand-back's pushes
  failed their push assertions 3, 2, 2 and 1 of 3 runs; counting the writer's, each failed 3 of 3, and with the pushes each passed 3
  of 3.
- Done: `BackupNode.Persist` pushes STATE once the fleet's settings are the node's, before it answers, so every change of them writes
  its row, or answers its verb, beside a STATE that holds it (the follow's, the step-down's and the kept epoch's own pushes, above,
  are now that one). FLEET FENCE and the takeover wrote theirs before any push of the role and epoch they set: a new role's only push
  was the network air's (`MoveAir`), which moves before the settings are the node's so that STATE never names a role whose air has
  not moved, and STATE is cached on its revision, so a reader holding the fence's row, or the takeover's fault row for an epoch the
  file could not keep, could read the role and epoch from before (the fence's next push came after `LeadsHeardAsync` and a
  `SYNC FOLLOW`, the takeover's after its routes). `AFailoverWhoseSettingsCannotBeKeptOnEitherNodeSaysSoInEachRowOnceStateHoldsIt`,
  both nodes' files refusing every write, asks the backup's fault row and the old primary's fence row for their words, not ok, STATE
  read as each is written for the role, the node it names and epoch 1, a push by the row's writer before it, and each file as it was;
  against the product before this change it failed 3 of 3 at the takeover's push, and with it it passed 3 of 3.
- Done, not seen failing: a backup that hears its primary's epoch in the hello of its mirror and in the primary's HEALTH at once
  (`KeepPrimaryEpoch`, from the mirror's line and from the watch) takes it once and says once that its settings file could not keep
  it. The epoch was read outside the settings' lock and taken inside it, so both callers could pass the read and both write the fault
  row (before the fleet-words change above, only a second write of the same epoch). It is now read and taken in one step under the
  lock. `ABackupThatHearsItsPrimarysEpochOnItsMirrorAndInItsHealthSaysOnceThatItCannotKeepIt`, a backup restarted under epoch 2 whose
  primary holds 5, its file refusing every write, waits for the row and for its mirror to be identical (its hello read; the watch has
  read the primary's HEALTH many times by then) and asks for one row, STATE at epoch 5 and the file at 2. With the first caller past
  the read held until a second passed it (in a scratch copy) it failed 3 of 3 against the two steps, with two rows, and passed
  unheld; with one step it passed 3 of 3 under the same hold (the second caller waits on the lock, then finds the epoch taken).
- Done, not seen failing: `ClockSlewTests.AWaitingBlindCommitMovesWithTheStepSoItLandsNeitherEarlyNorLate` reads the landing's
  journal row once the lane's tick that landed the commit is over (`Ticked`, as `BlindTests` reads it), not at its first sight of
  the landed show. A commit lands on the lane's tick: the store publishes the show and runs its listeners, and then the row ("blind
  commit: 1 change landed (at) by Desk") says it landed, so it rightly follows; a reader that saw the show could read the journal
  before the row. With the row held 1 s before the journal took it (in a copy of the journal), the unchanged test failed 3 of 3 at
  its line 102 with the row not found, and passed unheld; it passed 3 of 3 under the hold now.
- Done, not seen failing: `MemberCommitTests.AMembersCommitWaitingForAGoLandsWithTheLeadersGoAndANextCommitWithItsEarlyNext` reads
  its landing row ("blind commit: 1 change landed (on go) by Desk") once the member's lane tick that landed the commit is over, for
  the reason above. Held 1 s before the journal took the row, the unchanged test failed 3 of 3 at its line 40, and passed unheld; it
  passed 3 of 3 under the hold now.
- Done, not seen failing: a follower whose leader goes quiet says it free-wheels in STATE (its words, pushed) before its row and its
  `sync.leader.lost` event, and that it follows again before the row that its leader is heard again (`SyncNode.WatchLeader`). It
  wrote the row and raised the event first, so `AnchorFeedTests.AFollowerWhoseLeaderDiesFreeWheelsAndSwitchesItsNextItemAtTheAnchoredInstant`,
  which waits for the row and then reads STATE's words, could read the follow's words: with the lane held 1 s just after the row,
  the unchanged test failed 3 of 3 at its line 107 (`"following FOH (0badc0de) for show — …"`), and passed unheld. The test now also
  reads STATE's words as the row is written; against the old order that failed 3 of 3 with no hold (the words were set after the
  row, on the same thread), and with the new order it passed 3 of 3 held and 3 of 3 unheld.
- Done, not seen failing: `AnchorFeedTests.ARestartedFollowerFollowsItsLeaderAgainRejoinsTheShowWhereItIsAndKeepsItsSwitch` waits
  for the rejoin's row ("… (kept from before the restart)") before it reads it. The rejoin (`SyncNode.RestoreAsync`) follows through
  the network host, which names the role STATE reads, starts its feed, and then writes the row; the role, the clock and the live
  feed the test waited for come first. Held 1 s before the journal took the row, the unchanged test failed 3 of 3 at its line 156,
  and passed unheld; it passed 3 of 3 under the hold now.
- Done, not seen failing: `BackupRestartTests.APrimaryWhoseBackupIsNotHeardGoesOutOnceItsClockHasWaitedForIt` waits for the row
  ("… did not hear its hot backup …") before it reads it. `BackupNode.Hear` moves the node's network air under the settings' lock
  (live, the LED sender's master, the sends reconciled, a push) and writes the row after the lock; the air and the LED, which the
  test waited for, come first. Held 1 s before the journal took the row, the unchanged test failed 3 of 3 at its line 128, and
  passed unheld; it passed 3 of 3 under the hold now.
- Done: `ScanEngineTests.AScanIsRefusedWithoutACameraAndCancelGivesTheOutputsBack` holds the simulated scan at its first pattern
  until its cancel is in. A simulated scan reads "scanning" only while its patterns render, the FAST plan's 34 back to back in one
  work item on a compute thread (`ScanRunner.CaptureSimulated`; 0.3 to 0.7 s here in Debug), and goes on to its decode, its solve
  and "done" with no wait; the test sampled the stage every 20 ms of `Task.Delay`, which needs the runtime's timer thread and then a
  pool thread, so a sampler not run in that window never saw the stage and read "decoding" to "done" until its 30 s ran out. It
  failed so on the Windows job (run 432, at fix-fetchrow-win ae9ca8d9, which does not touch the scan) at its line 420
  (`Assert.True() Failure`, after 30 s), consistent with that, not proven: the assertion names no stage. With the test host's
  ".NET Timer" thread alone stopped 5 s once the rig was built (the scan's threads running on; the whole scan ran in that time) the
  unchanged test failed 3 of 3 at that line. It now hears the stage on the scan's own thread (`Progressed`, raised once the step is
  stored) and holds the scan there until `SCAN CANCEL` is answered, every assertion kept; it passed 3 of 3 under the same stop
  (the scan held at its first pattern) and 3 of 3 without it.
- Done: the hot-backup tests' meeting (`BackupRig.MeetAsync`) hears each peer at the web port it serves, and moves the fleet's
  clock while a node lacks a peer's health and its fleet monitor waits to dial again. A fleet monitor whose subscription to a peer
  ends without a record (refused, cut, or answered by another machine under the peer's id) dials again once its backoff (0.5 s
  doubling to 8 s, spread by up to a fifth) has run out on the fleet's clock, and until then takes no newer hello of that peer, one
  at another port included (`FleetMonitor.Consider` returns while the peer's feed runs, its wait included); the rigs' nodes run
  the fleet on a `ManualTime` that no hot-backup test moves, so one such end left the peer unheard for good and the meeting's wait
  for its health ran out with "0badc0de has no health of 9f8e7d6c"; the meeting's first wait took any hello under the peer's id
  that named a web port. `BackupRenderTests.TheBackupPlaysTheSameVersionAndItsPictureIsTheSameBytesAtTheSameInstants` failed so
  on the Windows job (run 433, at fix-fetchrow-win ae9ca8d9, which does not touch the backup), from its line 141 after 22 s,
  consistent with that, not proven: no node log or monitor words survive in the job's output, so what ended the first subscription
  there is not known (a stray hello under the peer's id, which the rigs' shared ids and hello sockets allow, below, or a dial
  refused or cut). With a hello under the backup's id that names a closed web port sent to the test host's UDP ports in their first
  3 s (before the meeting), the unchanged test failed 3 of 3 with those words, as did, 3 of 3 against the meeting as it was, two
  new tests (`BackupRigTests`: the same stray hello sent in the process, and a first subscription that ends unheard). The monitor
  now counts the subscriptions waiting out their backoff (`FleetMonitor.Redialling`, each counted once its wait's timer stands) and
  says what it last said of a machine it keeps, heard or not (`WordsOf`); the meeting moves every node's fleet clock 50 ms at each
  20 ms look while the node lacks the peer's health and its monitor waits to dial any machine again (`FleetWait.UntilAsync`; no look
  moves it once the health is held or while that monitor waits to dial none, but a step moves every node's clock, so it also moves a
  dial another node has under way, whose connect timeout runs on that clock, and ages the records the nodes hold), and a meeting
  that fails names the hello's address and port, the peer's own port and the monitor's counts and words. The new tests passed 3 of
  3, the render test 3 of 3 with the stray hello, and the 54 Fleet tests all passed.
- Done: `FleetMonitorTests.APeerAppearsWhenItsHealthArrivesAndGoesStaleThenGoneOnTheMonitorsClock` moves A's fleet clock while A
  lacks B live and A's fleet monitor waits to dial again, through the hot-backup meeting's wait (`FleetWait.UntilAsync`). Its nodes
  run the fleet on a `ManualTime` the test did not move before B read live, so a first subscription to B that ended without a
  record (refused or cut) left A's monitor waiting out its backoff for good, and the 10 s wait for B live ran out at its line 52
  with only the journal in its message. With a hello under B's id (read from B's settings file as B started) naming a closed web
  port, sent to the test host's UDP ports for 0.4 s before the two met, the unchanged test failed 3 of 3 at that line after 11 s.
  The wait's steps are the monitor's sweep (250 ms): the sweep ages liveness every 250 ms from the clock's zero, so a record heard
  between two sweeps would be read stale or gone up to 250 ms after the test's advances cross 3 s and 6 s (with 50 ms steps the
  test failed 2 runs of 3 so, at its stale read); heard at a sweep's instant, as on the unmoved clock, it is read at them. Every
  assertion is kept: A's clock reads exactly what the wait moved it (zero when the first subscription is heard), and a wait that
  fails names what A's hello and monitor hold of B. Under the same stray hello it passed 3 of 3 (A's clock moved 8 s on a probed
  run), and 3 of 3 without it (0 s); the 54 Fleet tests all passed. The watched "peer not live" below is consistent with this, not
  proven: the failing runs kept only the journal, and in this rig, whose node ids are random, a stray hello under B's id is not the
  likely way in; a dial refused or cut is.
- Done: `OptimiseJobTests.CancelStopsTheRunningJobAndLeavesNoPartialFile` holds its worker after the first frame
  (`OptimiseRig.HeldAtFirstFrame`), so its cancel lands mid-job. The job's partial file is on disk only while the clip encodes and
  until the service takes the end in (a few seconds here), and the test looked for it every 50 ms before it cancelled, so a test
  that looked only after that waited out its 4 minutes and failed. With the test's continuation 15 s late once the job had begun
  (a delay in a scratch copy) the unchanged test failed 3 of 3 ("the partial file never came", the version made); held, it passed
  3 of 3 under the same delay and 3 of 3 without it. No failure of it has been seen.
- Done: an optimise stop counts its grace on the clock its options give (`OptimiseOptions.Time`; a node keeps the system's), and
  `OptimiseWorkerEndTests.AStopKillsTheWorkerItAskedToEndNotTheOneThatTookAJobMeanwhile` moves that clock once it has seen the kill a
  job's arrival makes. Its stop waited 500 ms of real time before killing the worker it asked to end, while the test started a
  fresh worker (a process start, much slower on Windows) and asked for that arrival's kill alone; a start slower than the grace
  put the stop's kill first. With 700 ms between the stop and the fresh start (a delay in a scratch copy) the unchanged test
  failed 3 of 3 at its line 159 (both kills already in); it passed 3 of 3 under the same delay and 3 of 3 without it. No failure
  of it has been seen.
- Done: the line wire's STATE and PROJECT push throttle counts its 200 ms window on the clock the wire's options give
  (`WireServerOptions.Time`, through `TrailingThrottle`; a node's wire and its web port's line sessions keep the system's), and
  `WireServerTests.PushesAreThrottledLatestWinsAndNeverRepeated` moves that clock once the window stands. Its fifty changes had to
  fall inside two real-time windows, and the skip it counted was read 600 ms after the touches without waiting for the throttle
  that writes it. With the publishing loop held 250 ms twice (a delay in a scratch copy) the unchanged test failed 3 of 3 at its
  line 247 (three pushes); it now reads the one push the window makes, and waits for the skip, and passed 3 of 3 under the same
  delays and 3 of 3 without them. No failure of it has been seen.
- Done: `ClockSyncTests`' convergence waits for the leader's count of its answers (`ClockSyncResponder.Answered`) rather than reading
  it once the follower holds eight samples: the responder counts an answer once its send has returned, which can be after the
  follower has taken that answer in. With the count landing 300 ms after each send (a scratch copy of the responder) the unchanged
  tests failed 3 of 3 at its line 36; waiting, they passed 3 of 3 under it and 3 of 3 without it. No failure of them has been seen.
- Done: `HostNetworkTests.ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine` runs its node on a clock the test holds
  (`HostRig`'s mono a `ManualClock`). The link trusts a running clip for 3 s of the node's clock after the desk's last STATE
  (`PatternsLinkOptions.VideoStaleAfter`, counted on the node's own clock) and the fake desk sends its STATE once, at connect, so a
  test that read STATE more than 3 s after that read no clip. With the test's read held 3.5 s once the link was live and paired (a
  delay in a scratch copy) the unchanged test failed 3 of 3 at its line 346; on the held clock it passed 3 of 3 under the same delay
  and 3 of 3 without it.
- Done: the Stagewatch approval desk's pull ends on its stop whatever the call it has in flight then ends on, and letting the desk
  go throws nothing. The sink's pull (`StagewatchProposalSink`) took a refused, reset or unreadable desk in only while its stop was
  not taken (`… && !ct.IsCancellationRequested`) and ended only on a cancel, so a failure raised once the stop was taken faulted the
  pull, and its dispose threw that failure at whoever let the desk go: the assistant's change of desk or its own dispose, and with
  them the operations services' dispose (`OpsServices`); the sink's client and stop were then never disposed, and a second dispose
  threw. The stop is taken with `CancelAsync`, which marks it at once and runs its callbacks on the pool after it returns; a
  request's client sees the stop only through the token it linked to it, one of those callbacks, and hands a failure raised before
  then on as itself (an `HttpRequestException`), not as a cancel. `StagewatchSinkTests.ADeskThatDoesNotAnswerLeavesTheProposalOnTheNodesOwnList`
  failed so on the Windows job (run 442, job 120200285298, at fix-win-batch4 b52c28f4, which touches neither the assistant nor the
  sink) after 2 s, its refused connect thrown from the sink's collect (its line 139) through the sink's, the assistant's, the
  operations services' and the rig's dispose to the test's `finally`, no assertion failed: consistent with the connect to the
  closed port the test names, refused only after about 2 s on Windows (at once on Linux), landing as the stop was taken, not proven.
  The exception shows only that the client handed the refusal on before the stop reached the request's token and that the pull's
  filter ran once the stop was marked: the refusal raised just after the stop was marked, or just before it with the stop marked
  before the filter ran. The held refusal below forces the first; the pull now ends quietly on either.
  With the sink's third collect held by a handler the test gives it and refused in a callback on the pull's stop, registered after
  the client linked the request's token so it runs before the stop reaches that token (a token's callbacks run newest first), the
  new `ARefusalThatLandsAsTheStopIsTakenEndsThePullAndIsNotThrownAtWhoeverLetsTheDeskGo` failed 6 of 6 against the sink as it was,
  with the Windows job's exception and its stack from the sink's collect to the test; and with a client that fails as no pull
  expects, `APullThatFailsAsNoPullExpectsEndsSaidAndIsNotThrownAtWhoeverLetsTheDeskGo` failed 3 of 3 (the pull faulted unsaid, and
  the dispose threw it). The pull now ends quietly on whatever it meets once the stop is taken; a refusal before the stop is said on
  the sink as before; a failure no pull expects ends the pull, said at once on the sink and in the journal and written with its
  trace to the engine's log (`AssistantServiceOptions.Log`, which the host's assistant part fills; with only its words kept, the
  same test's new log assertions failed 3 of 3); and the sink's dispose runs once and lets its client and stop go whatever it
  meets. The two new tests and the three before them, every assertion kept,
  passed 8 runs of 8. The unchanged test still cannot be made to fail on Linux on its own, where the refusal is immediate. A
  change of desk while the old desk's call was refused so (`AssistantService.UseApprovalDeskAsync`) threw the refusal at whoever
  changed the desk once the change was made, and skipped telling STATE; with the same held refusal,
  `AChangeOfDeskWhileTheOldDesksCallIsRefusedAsItsStopIsTakenCompletes` failed 3 of 3 against the sink as it was, the refusal thrown
  from the change, and passes now (5 runs of 5, with the sink's other tests): the new desk is the one read, the old names nothing new.
  A callback on the pull's stop that threw (its registrant's fault, which the stop's `CancelAsync` carries once every callback has
  run) still made the sink's dispose throw before it waited for the pull, letting the client go under a pull still running: with the
  old desk's collect held past its stop and a callback on the request's token throwing,
  `AChangeOfDeskWhoseOldStopMeetsAThrowingCallbackCompletesOnceTheOldPullHasEnded` failed 3 of 3 (the change thrown at once, with
  the callback's exception). The dispose now writes that fault with its trace to the engine's log and waits for the pull, and the
  test passes (5 runs of 5, with the sink's other tests and `OpsServicesTests`). The node as shipped does not switch the approval
  desk on (nothing outside the tests calls `UseApprovalDeskAsync` with a desk; round 2 §9, decision 7), so every path in this entry
  is reached from the tests today, and from a node once the desk is switched on.
- Done: the operations services an engine built without the host owns (`OpsServices.Attach`: the scan runner, projector control
  and the assistant, each with its attachment) are all let go when one of them fails to stop, and what failed is thrown once they
  are (itself when one part failed, together when several did). Their dispose stopped at the first part that threw and runs once,
  so the parts attached before it were never let go: with the approval desk's dispose throwing as above, the assistant's failure
  kept projector control and the scan runner, and the test rig (`OpsRig`) then skipped its engine's dispose, leaving the engine
  running in the test host. With a part that throws, `OpsServicesTests` failed 3 of 3 against the dispose as it was (only the
  failing part and the one after it went; several failures came out as the first alone); they passed 5 runs of 5, and the rig now lets
  its engine go whatever its services' dispose meets. A node's host lets each part go on its own (`EngineHost`'s quiet stop).
- Done: a fleet monitor's dial to a peer's health ends as the monitor's stop whatever it meets once the stop is taken. The dial
  (`HealthFeedClient`) took a refused or reset dial in only while the stop was not taken (`… && !ct.IsCancellationRequested`), so a
  failure raised once it was, before the stop reached the dial's own token (the stop is taken with `CancelAsync`, as the approval
  desk's above), came out of the dial as a `WebSocketException`, which the monitor (`FleetMonitor`) writes to the engine's log as a
  feed that faulted, with its trace, on a plain stop; on Windows a dial to a peer that went away is refused about 2 s late. With the
  peer resetting the dial in a callback on the stop registered after the dial linked its token, the new
  `HealthFeedsTests.ADialThePeerFailsAsTheStopIsTakenEndsAsTheStop` failed 3 of 3 against the dial as it was (the
  `WebSocketException` over the reset); it now ends as the stop's cancel, and a dial failed before the stop still ends with its
  words. It passed 5 runs of 5 with the fleet's Net tests. No failure of it has been seen.
- Done: a backup's pulls from its primary end as the node's stop whatever they meet once the stop is taken. A file's pull
  (`BackupNode.PullOneAsync`, behind the mirror's run of pulls and `MEDIA FETCH`) and the watch's second ask for the show's files
  took a refused, reset or cut-short pull in only while the stop was not taken (`… && !ct.IsCancellationRequested`), so a failure
  raised once it was, before the stop reached the request's own token (as the approval desk's above), came out as itself: the
  mirror's run of pulls faulted ("A backup task faulted." in the engine's log, with its trace) and the watch logged "The backup watch
  faulted." on a plain stop. With the primary resetting the pull's first ask in a callback on the stop registered after the client
  linked the request's token, the new `BackupPullTests.APullThePrimaryFailsAsTheStopIsTakenEndsAsTheStop` failed 3 of 3 against the
  pull as it was (the `HttpRequestException` over the reset); it now ends as the stop's cancel, and a pull failed before the stop
  still ends with its row's words. It passed 5 runs of 5, and the 28 backup tests passed. The watch's ask has the same two lines
  and is not forced: its token is the node's own stop, which no test reaches. No failure of either has been seen.
- Done: a node's own `MEDIA FETCH` ends as the node's stop whatever its pull meets once the stop is taken. Its chain
  (`NetworkHost.PullAllAsync`, and in it the ask for a peer's media list and a file's pull) took a refused, reset or unreadable peer
  in only while the stop was not taken (`… && !ct.IsCancellationRequested` on each), so a failure raised once it was, before the stop
  reached the request's own token (as the approval desk's above), faulted the fetch, which the host writes to the engine's log as "A
  network task faulted." with its trace, on a plain stop. With the peer resetting the ask for its list in a callback on the stop
  registered after the client linked the request's token, the new `HostFetchStopTests.AFetchThePeerFailsAsTheStopIsTakenEndsAsTheStop`
  failed 3 of 3 against the fetch as it was (the `HttpRequestException` over the reset); it now ends as the stop's cancel, as the
  stop's own cancelling always did (no row, the node going), and a fetch failed before the stop still ends with the file's row and
  its words. It passed 5 runs of 5, and `HostNetworkTests` passed. No failure of it has been seen.
- Open: whether a lane's failure raised once its stop is taken is the stop's, as the done entries above make it for the
  approval desk, the fleet monitor's health dial, a backup's pulls and a node's own `MEDIA FETCH`, is the owner's to make the house
  rule. Settled once, it decides the sites the sweep behind those fixes judged low and left as they are: the Patterns link's context
  loop (`PatternsLink.ContextAsync`, its line 279: a line's write that fails as the link's stop is taken passes
  `WireClient.WriteAsync`'s filter, its line 187, and the link's dispose takes in only a cancel), `MediaTransfer.ChunkAsync` (its
  line 330: an `ObjectDisposedException` when the puller is let go mid-pull), `HelloService.SayAsync` (its line 189: a send that
  fails as the stop is taken is logged as a hello that could not be made), `PhoneSession.OnTextAsync`'s catch (its line 591),
  `OptimiseService.RecordAsync` (its line 880: a failure other than the cancel as the node stops fails the job rather than leaving
  it to be queued again) and `BackupNode.MirrorProjectAsync`'s "could not pull" words during a stop. The same filter
  (`… && !ct.IsCancellationRequested`) also stands where the sweep named nothing: `WireClient.WriteAsync` (above; inside the link,
  the connection's own catches take its escape in), `StagewatchLink.PostContextAsync` (its line 515: the beat's catch takes its
  escape in, as it takes in `ApprovalsAsync`'s at its line 555), and the model store's download (`ModelStore`, its line 460) and
  `CuedCapture.PictureOrNullAsync` (its line 349), not read for where an escape goes.
- Done: a native double's build ends with its compiler, never with the compiler's pipes (`NativeBuild.Run`), and a hung test host's
  stacks reach the CI log. The OMT tests' attribute builds libvmx with clang++ while the suite is discovered (`OmtFactAttribute`,
  `VmxBuild.Ensure`; the fake NDI runtime's attribute the same way), and the build read the compiler's output to its end and then
  waited for its exit, neither bounded; the end of a pipe comes only once every process holding its write end has let go, and a
  process the compiler starts inherits it (on Windows clang++ links through Visual Studio's link.exe, whose helpers are known to
  linger on build machines), so one that held on stalled the whole test host in discovery, where blame has no test to name. The
  Engine test host on the Windows job stalled so (run 435, at main cf9699d8, which passed on Windows in run 434 and on Linux): "A total
  of 1 test files matched" and nothing more until blame's 5 minutes ran out, no test result and no sequence file; consistent with
  that, not proven: blame's dump was off and the runner's processes cannot be seen from here. With clang++ wrapped so a helper it
  starts holds its stdout and stderr, and a fresh temp folder, the unchanged Engine host failed 3 of 3 with run 435's lines word for
  word under a 1-minute blame limit (the library built, never moved into place); the dump of one such run, read with dotnet-dump,
  stood in `NativeBuild.Run`'s read under `OmtFactAttribute`'s constructor in xunit's discovery. A build now reads the compiler's
  words while it waits for the compiler alone, up to 3 minutes (then it and every process it started are stopped, with the words
  "did not finish … and was stopped"), reads the words for 5 s more, and says a failed build's exit code; it passed 3 of 3 under the
  same wrapper. `NativeBuildTests` runs a "compiler" whose helper holds its pipes until the test lets it go (`PipeHolder`): the
  unchanged build failed 3 of 3 ("the build was still waiting for the end of the compiler's pipes", after 60 s), the new one passed
  3 of 3, with a compiler stopped at its deadline and one that fails with its words. The probe of npm's global root that the phone
  tests' attributes run in discovery, the Net suite's node facts and the Engine suite's browser facts alike (`ToolProbe`, shared by
  both), read its tool's output to its end before its 30 s bound could apply (the Engine suite's with no bound at all, once per
  test); it now ends with the tool, asked once per suite (its test failed 3 of 3 against the old probe's logic, passed 3 of 3). With
  npm standing in for one whose helper holds its output 10 minutes and `PLAYMAP_E2E=1`, the Engine host with the old browser fact
  stalled in discovery 3 of 3 until a 1-minute blame limit stopped it ("The active test run was aborted"), and with the shared probe
  skipped its two browser tests in 8 s 3 of 3. CI now stops a hung test host with a
  mini dump the runtime writes (`--blame-hang-dump-type mini`; on Windows `VSTEST_DUMP_FORCENETDUMP`, since the collector's own
  Windows dumper writes thread information alone, which no managed stack walk reads), and on a failed job prints every managed
  thread's stack from each dump (dotnet-dump 10.0.750501, the package held to its SHA-256), so a stall before the first test is
  named as a test's is. Shown on Linux only: the Linux step's reading of a stalled discovery's dump stood in the build under the
  attribute's constructor. On Windows the dump through `VSTEST_DUMP_FORCENETDUMP`, the Windows stacks step and the Windows forms of
  `NativeBuildTests` and `ToolProbeTests` (a batch file whose helper is started with `start /b`, `ping.exe`) have not yet run (no
  Windows runner was at hand): the Windows job's next run is their first.
- Done: the phone scan rig's hub runs on a clock the test holds (`PhoneScanRig.PhoneTime`), the phone bridge test's scan waits for
  each answer on a held clock too, and the hub counts a hello's wait on its own clock, its words going before the socket is let go.
  A session ends "stopped sending: nothing for 10 s" when the node has read nothing from the phone for `QuietTimeout` on the hub's
  clock (`PhoneSession.ReceiveLoopAsync`), and the rig left that clock the system's while the fake phone in the same process pings
  every second of real time: a test host that ran neither the phone's ping nor the node's read of it for 10 s ended the session, and
  the scan's next cue failed. `PhoneBridgeTests.APhoneScanBridgesIntoTheModelAsItIsPlaced` failed so on the Windows job (run 436, at
  fix-nodehost-win f7d3645b, which does not touch the phone hub or the bridge) at its line 45 after 1 m 12 s, consistent with that,
  not proven. With the test host stopped 11 s mid-scan the unchanged test failed 4 of 5 with the same words at the same line, and
  passed 2 of 2 stopped 8 s; with the host stopped 2.6 s of every 2.75 s it failed 3 of 3 the second way the rig raced real time,
  "could not take locate frame N: it said nothing for 2 s" (the scan's `PatternTimeout`, `MaxRetakes` 2). Both clocks held, it
  passed 3 of 3 under each, and the 74 Engine tests of the phone, the folder camera and the camera resolve passed. The hello's wait (`HelloTimeout`) was a `CancelAfter` of real
  time, and a cancelled read aborts a web socket before any close can go, so a phone that said no hello never heard "no hello":
  with a 300 ms limit on the system's clock the unchanged hub closed nothing (status null) 3 of 3. The limit now runs on the hub's
  clock and, once it has run out, sends the close with its words while the read stands, letting the socket go 2 s later if the phone
  never answers (`PhoneHubOptions.Time`); the new Net tests (the held clock moved through the limit, and the system's clock) failed
  3 of 3 against the old hub and passed 3 of 3. The rig's invite test reads the invite's expiry against the hub's clock. No assertion
  of the old tests is changed; scans in the rig that keep the default real-time patience still count it so (`PhoneCameraTests`).
- Done: `GpuDeviceLockTests.ASharerWaitsForTheHolderAndABoundedTakeGivesUpWithinItsBound` lets the holder go only once the sharer's
  own wait is counted. It let go 100 ms after the sharer's thread said it had started, and its last count held from the bounded take
  before it alone (`TryEnter` counts a wait), so a sharer that reached its take after the holder had let go took the lock unopposed
  and the test passed without showing the sharer waited. With the sharer's thread 150 ms late to its take (a delay in a scratch
  copy) the unchanged test passed 3 of 3 with the lock's waits at 1, the bounded take's alone; it now waits for the sharer's wait to
  be counted, asserts the sharer took nothing while the holder held, and that the waits rose by exactly that one, and passed 3 of 3
  under the same delay. No failure of it has been seen.
- Done: `MediaTransferTests.TheBandwidthBucketHoldsItsRateAndCanBeLoweredAndLifted` asks that a bucket with no limit answer each of
  its hundred takes as it is asked (each take complete as it returns, none waiting in the bucket), not that the hundred fit in 200 ms
  of the stopwatch, which measured only the machine's pace around the loop. With the loop's thread stopped 250 ms half way (a delay
  in a scratch copy) the unchanged test failed 3 of 3 at its range; it passed 3 of 3 under it. The rate half is unchanged. No
  failure of it has been seen.
- Done: a PJLink search counts its window on the clock its socket is given (`PjLinkUdp`'s `time`; a node's is the system's), and
  `PjLinkDriverTests.ASearchFindsAClass2ProjectorOnLoopbackUdp` and `DeviceManagerTests.DiscoveryFindsAProjectorIdentifiesItAndSuggestsItsNativeDriver`
  move that clock through the window once the simulator's answer has been heard and the window's wait stands. The search collected
  answers for 400 ms of real time across two receive loops (the simulator's and its own), so an answer later than that was dropped.
  With the simulator's answer 500 ms late (a delay in a scratch copy) both unchanged tests failed 3 of 3 ("The collection was
  empty"); both passed 3 of 3 under it, and the 112 Devices tests passed. No failure of them has been seen.
- Done: the desk's toasts are aged on the session's clock (`DeskSession`'s `time`, which its throttle already ran on; the desk's is
  the system's), and `DeskLogicTests.ARefusalBecomesAToastInPlainWordsWithTheVerbThatCausedIt` moves it. A repeated refusal folds into
  the toast already up when the same words come within two seconds, which `Notify` read on the stopwatch, so a repeat the test sent
  more than 2 s after the first stacked a second toast. With the test's repeat 2.1 s late (a delay in a scratch copy) the unchanged
  test failed 3 of 3 ("The collection contained 2 items"); on the moved clock it passed 3 of 3 under the same delay, and now also
  shows a repeat past the two seconds stacking and a refusal's toast going at twice the window's age. No failure of it has been seen.
- Done: PROJECTOR SOLVE's lane tests (`ProjectorSolveLaneTests.ASolveHoldsTheLaneWithinFourTimesItsSlice` and its Perf twin) read how
  long the lane keeps a line waiting on the lane itself, from the line's post to its start (`LaneProbe`), not by the test's
  stopwatch around the whole round trip, which held the test's own continuation after the lane had answered. With that
  continuation 45 ms late each time (a delay in a scratch copy) both unchanged tests failed 3 of 3 (a median of 45.3 ms against 40
  and 10); read on the lane they passed 3 of 3 under the same delay, and the probe still reads the solve's slices (a median of about
  1 ms, a worst of 2 to 6 ms, here). A wait inside a slice and a stop of the lane thread are still in the reading. No failure of
  them has been seen.
- Done: the survey import's lane tests (`SurveyImportPerfTests.TwoMillionTrianglesInFifteenSecondsWithPingUnderFiftyMilliseconds` and
  the regular `SurveyImportTests.ATwoMillionTrianglePlyComesInAsOneEditWithinItsDeviationAndNeverHoldsTheLane`) read how long the
  lane takes to answer each SITE on the lane itself (`LaneProbe.AnsweredMsAsync`: how long an empty line posted just before SITE
  waited, and the lane's turn from that line's end to the start of one posted just behind SITE, the line before holding the lane until
  both are posted), not by the test's stopwatch around SITE's whole round trip, which held the test's own continuation. With that
  continuation 210 ms late once (a delay in a scratch copy) both unchanged tests failed 3 of 3 (a worst of 210.2 to 210.5 ms against
  the Debug build's 200 ms ceiling); read on the lane they passed 3 of 3 under the same delay (a worst of 1 to 10 ms). SITE's own turn
  is in the reading: with SITE held 300 ms on the lane (a scratch copy), a reading of the lane's wait before each SITE alone passed
  3 of 3 (a worst of 4.3 ms), and this one failed 3 of 3 (306 to 342 ms); with the test's thread stopped 210 ms between posting SITE
  and the line behind it once, and 210 ms after an answer once, it passed 3 of 3 (a worst of 5 to 20 ms). No failure of them has
  been seen.
- Done: `DeviceLockHostTests.TheLoopDrawsInsideTheDeviceLockAndNeverWaitsPastItsBound` holds the device until the loop has drawn its
  five frames inside the hold (`HoldUntilDrawn`, within the rig's patience), not for 400 ms of real time, and its decoder takes its
  brackets until the loop has drawn a frame among them, not for sixty brackets' few hundred milliseconds. Both asked a number of
  frames of a span of real time, so a render thread the machine left unrun for that span failed them. With the test host's render
  threads alone frozen 1.5 s (a cgroup freezer of their own) just before the hold the unchanged test failed 3 of 3 ("the loop drew 0
  frames while a sharer held the device for 400 ms"), and frozen as the hold began it failed 3 of 3 at the decoder's ("the loop drew
  nothing while a decoder took the device bracket by bracket"); it passed 3 of 3 under the first. The loop's take is still held to
  its bound, read on the render thread: the lock says how long its latest bounded take that gave up waited
  (`GpuDeviceLock.LastTimedOutWaitNs`, written before `TimedOut` counts it), the sharer reads it each time the count moves, and the
  shortest of the hold's takes must be within 80 ms (twenty times the 4 ms bound; a render thread left unrun only lengthens a wait,
  so the shortest is the bound's measure however the machine is paced). With the loop's take given 125 times its bound a hold
  that asks for its frames alone passed 3 of 3, and this one failed 3 of 3 (its shortest take 500.1 to 500.2 ms), as it did 3 of 3
  with `FrameBoundMs` itself 500; with the render threads frozen 1.5 s inside a take (the longest take read 1504 to 1505 ms, the
  hold 1.57 s) it passed 3 of 3, its shortest take 4.1 to 4.3 ms. No failure of it has been seen.
- Done: the engine's Patterns link and a follower's anchor link count their peer's silence on the node's clock. `NetworkHost` made
  the Patterns link's options with no clock and `SyncNode` made its `AnchorFollower` on `WireClient`'s default options, so each
  counted the desk's or the leader's silence (stale at 6 s, dropped and dialled again at 12 s) and paced its pings on the process's
  clock, while the node, and every rig that holds the node's clock, counts on the node's: of the Patterns link only the clip's trust
  did. The Patterns link now takes the node's clock (`PatternsLinkOptions.Clock`), and `AnchorFollower` takes a clock, `SyncNode`
  handing it the node's (`NetworkClock.Local`; the process's when none is given).
  `HostNetworkTests.ThePatternsLinkCountsTheDesksSilenceOnTheNodesClock` and
  `AnchorLinkClockTests.AFollowersAnchorLinkCountsItsLeadersSilenceOnTheNodesClock` hold the node's clock while the desk (the fake
  desk's `Silent`) or a leader's wire that says nothing stays quiet for 6.5 s of real time, then move it past the stale mark and
  past the drop mark: against the old links both failed 3 runs of 3 at their first check (the link `stale`, the feed `connecting`,
  6 s of real time into the silence); now the link reads live on its first connection through that real-time silence, stale (the
  feed `connecting`) on the same connection once the node's clock passes 6 s, and dials again once it passes 12 s, 3 of 3. No
  failure of either has been seen.
- Done: a node's preview rings leave no file behind it. The engine never let its views go at its stop (`EngineCore.DisposeAsync`
  left `PreviewViews` undisposed), so a view a feed draws, whose ring the views make (`PreviewSink`: a draft view's, a camera's, a
  scan's), still shown when the node stopped kept its ring's file in the preview folder, about 11 MB at the largest size; the render
  host lets its own rings go as it stops. Here that folder held 32 such files, every one the view a blind render test left shown at
  its end, under node ids the rig draws at random, so no later node of that id ever made a ring of that name to sweep it. The
  engine now lets its views go once the host has stopped the render host and the lane takes no line (a view asked after is refused,
  "the node is stopping"), and a node making its views takes away its own earlier rings that nothing holds
  (`PreviewViews.SweepLeftovers`: every generation's file of its id is opened alone first, so a ring another engine of the same id
  still writes, or a reader still maps, stays; a file named without a generation is no ring of a node's).
  `PreviewRingFileTests.ADraftViewStillShownWhenTheNodeStopsLeavesNoFileBehind` (a draft view drawn, then the rig stopped) and
  `ANodeMakingItsViewsTakesAwayItsOwnEarlierRingsThatNothingHolds` (two leftovers taken; one held open as a reader holds it, one
  named without a generation and another node's kept) failed 3 runs of 3 against the old engine (the draft view's ring left; every
  leftover kept) and pass 3 of 3.
- Done: a node's phone hub runs on the node's clocks. The phone part made the hub with neither of its clocks
  (`PhoneHubOptions.Time`, `MonoNs`), so in a node it counted on `TimeProvider.System` and the process's monotonic clock: its hello
  wait (10 s), a phone's silence (10 s), a ticket's expiry and the wrong-ticket minute ran on the system's time whatever the
  node's, and its clock exchange answered on the process's monotonic clock where presents are stamped on the node's (one clock in
  a node, two under a test's). The hub now takes the node's monotonic clock (`ExtensionSetup.Mono`) and the node's time, a
  `TimeProvider` the host carries to its extensions (`EngineHostOptions.Time`, `ExtensionSetup.Time`; unset, as in a node, it is
  the system's). `PhoneNodeClockTests.TheNodesPhoneHubRunsOnTheNodesClocks` starts a node with the phone part on a held time and
  a manual monotonic clock: an invite expires 5 minutes after the node's time, a socket that says nothing is told "no hello" once
  that time passes 10 s, and a phone's ping is answered at the node's monotonic instant. Built against the old part it failed 3
  runs of 3 (the invite's expiry the system's time), and it passes 3 of 3. The phone loop (`PhoneScanE2ETests`) runs its node's
  hub on a time it holds, so the browser's pings, paced by the real clock, never race the hub's 10 s waits.
- Done: two tests read a later step only after waiting for what writes it.
  `ScanEngineTests.ASimulatedScanRunsThroughTheVerbsAndAppliesWithinToleranceOfTheTruth` read the journal's "scan … finished" row
  having waited only for the scan to read done, and the row is posted to the lane once the run's task has ended
  (`EngineCore.Content`), so a lane that ran late left the test's `Last` with no such row; it now waits for that row (60 s) and says
  the journal's last rows if it never comes. `HostNetworkTests.ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine` read
  the node's settings file once the link was live, and `LINK PATTERNS` answers accepted and writes the settings after it starts the
  link (`NetworkHost.LinkPatternsAsync`); it now waits for the link's row, written once that change has ended, before it reads the
  file. With the row's post held 30 s and the settings' write 5 s (delays in a scratch copy) the unchanged tests failed 3 of 3 (no
  "scan " row; the kept host ""), and the changed ones passed 3 of 3 under the same delays. No failure of either has been seen.
- Done: a test that runs a child process ends with the process itself, under a deadline, never with the end of its output pipes.
  The sites read every word to its end before or after waiting for the exit, or awaited an exit with no bound, so a process the
  tool started that held its pipes (a linker's helper, npm's, cmd.exe's) held the test without end. One runner in the shared
  test files (`tests/Shared/ChildProcess.cs`) waits for the process under a deadline (3 minutes unless the caller names one), stops
  it and every process it started past that, then reads its words for at most 5 s, as the native build did; a process a test
  started itself is ended the same way (`EndAsync`, `StopAsync`, `Stop`). The native build (`NativeBuild.Run`) and the tool probe
  (`ToolProbe`) run through it, and so do the media suite's clips and ffprobe read (`TestMedia`) and its hash test's `sha256sum`
  (fed on its standard input), the render suite's HAP clips, the engine suite's clip makers (`HostRig.MakeClipAsync`, the HAP and
  decode tests', `OptimiseClips`), the node host tests, the phone loop's browser driver, the Net suite's node scripts
  (`NodeTools.RunAsync`) and the supervisor tests' helpers. `ChildProcessTests` runs a tool whose helper holds its pipes
  (`PipeHolder`) through `Run`, `RunAsync` and `EndAsync`: against the sites' old pattern (every word read to its end, then the
  exit waited for) all three failed 3 runs of 3, still waiting after 60 s, and they pass 3 of 3; beside them a tool past its
  deadline is stopped with its words, a tool's input, output, errors and exit code are its own, and one that cannot start says
  so. The tests that triage did not list follow in the next entry, and two wave-2 package files stay open (below).
- Done: the tests the runner had not yet reached run through it too, the installer's and the supervisor's among them. The installer
  tests' Windows tools (`InstallerTests.Tool`: `net.exe`, `netsh.exe`, `sc.exe`, `tasklist.exe`, `cmd.exe` and the supervisor's own
  commands) read their output to its end, then their errors, then waited for the exit with no bound (and a tool that filled its
  error pipe before its output ended could never finish); the notices script (`LicenceTests.RunNotices`), the version tests' MSBuild
  (`VersionPropsTests`) and the build stamp's builds (`BuildStampTests`) waited for the exit under a deadline of their own, then
  read the words to their end, and left a tool past that deadline running; and the processes the installer's, the supervisor's and
  the shader worker's tests start and end themselves (the fake engine, ping, the engine run as a standard user, the worker) were
  each ended by a kill of the test's own, then waited for with no bound or not at all. Each runs through the runner now, or ends there
  (`Run`, `RunAsync`, `Stop`, `StopAsync`), and the installer's own run (`InstallerTests.Wait`) is stopped with every process it
  started past its 10 minutes; the installer's tests, outside the solution, link the runner as the suites do. A tool that does not
  finish, or whose words a process it started holds open, fails its test with how it ended. Forced with a stand-in for dotnet whose
  helper holds its output pipes for 400 s after it has exited, handed to the test host through the run settings' environment:
  `VersionPropsTests.ANumberedBuildSetsTheInstallersVersion` against the old helper waited 181 to 182 s, 3 runs of 3, until its own
  deadline cancelled the read; now it ends in about 5 s, 3 of 3, its message the stand-in's words held open, and on the real dotnet it
  passes (the Core suite's build stamp tests 16, the supervisor's suite 95 with its 33 Windows tests skipped here, the shader
  worker's 12). The installer's tests build here and run only on the elevated installer job, and the supervisor's Windows tests only
  on the Windows job: neither job has run them with this change yet.
- Done: the phone tests find their tools as Windows names them. The phone loop's lookup (`LoopPhone.Find`) and the node scripts'
  (`NodeTools.Find`) each took `name.exe` alone on Windows, so npm, a batch file there (`npm.cmd`), was never found, and the node
  scripts' fallback asked for `npm.cmd.exe`; each also looked in every PATH entry, a relative one (the current directory's) too.
  One lookup in the shared test files (`ToolProbe.Find`) now takes, folder by folder, `name.exe` then `name.cmd` on Windows and
  `name` elsewhere, by its absolute path, never in an entry that is not an absolute folder, and both use it.
  `ToolProbeTests.AToolIsFoundByItsAbsolutePathAsItsExeThenItsCmdOnWindowsAndByItsNameElsewhere` reads folders of stand-in files
  Windows' way and not: against the old lookup it failed 3 runs of 3 (no `npm.cmd`), and it passes 3 of 3. The Windows job runs the
  lookup for real; until then it is shown on Linux only.
- Open: two tests' clip makers still read ffmpeg's errors to their end and then wait for its exit with no bound:
  `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` (d3d12-passes' file in wave 2) and
  `tests/PlayMap.Engine.Tests/Fleet/BackupRenderTests.cs` (stability's hunk in wave 2). Moving each to the tests' runner
  (`ChildProcess.Run`) in its package closes it.
- Done: six of the short real-time windows the sweep watched run on what the test holds, or log what they measured. The optimise
  worker's stop graces in `OptimiseWorkerEndTests` run on `OptimiseOptions.Time`, a clock the test moves: the tests that stop no
  worker assert that no grace stood, and the two that stop a running one wait for the stop's grace to stand, find the stop still
  under way, then move the clock (no assertion of theirs read the grace's passing, so no failure of the old tests could be forced).
  The clip loop plays on a hand-moved position, 5 ms a request through two seconds and three loop points
  (`VideoSourceTests.OnAHandMovedPositionTheLoopAnswersEveryRequestWithItsFrame`), its stopwatch twin a `Category=Perf` test:
  with the decoder's queue held 400 ms one second in, the stopwatch test failed 3 runs of 3 and the hand-moved one passes 3 of 3.
  `AnchorFeedTests` keeps every fact and logs the adoption's time, its 800 ms ceiling gone (900 ms inside the window failed it 3
  of 3), the design's 200 ms bar the show network's. `ClockSyncTests` logs the best round trip and holds the offset within a
  millisecond and half of it (under a leader whose clock takes 25 ms to read, the 20 ms ceiling failed 3 of 3 and the bound held 3
  of 3). A folder camera's settle runs on the clock its options give (`FolderCameraOptions.Time`, the system's on a node), and
  `FolderCameraWriteTests` moves it once the file is whole: with the file written in place in two halves 60 ms apart, the real
  20 ms settle took the half file 3 of 3 (no frame), and the test's clock waits for the whole one 3 of 3. Art-Net's timecode test
  runs on the node's clock, moved 40 ms a frame once the source has read the last: with 120 ms between each send and its read, the
  real clock read dropout 3 of 3 and the node's clock reads rolling 3 of 3. The throughput bars ride with the serial Perf step
  and the hold button with the desk's clock (`docs/ROADMAP.md`).
- Done: the three tests one loaded gate failed together (2026-10-22, the Core suite 5 min and the Engine suite 28 min 39 s long)
  no longer time the machine's load, or say what the node held. The light plan's budget (`LightPlanBudgetTests`) times the plan by
  the process's CPU time across it, its collection running alone, against 4× the 3 s budget on each core, the stopwatch's time
  logged beside it: pinned to one core beside two busy loops, the stopwatch's 12 s ceiling failed 3 runs of 3 (25.0 to 27.8 s) and
  the CPU time held 3 of 3 (10.1 to 10.9 s against 12 s, a Debug build); the Perf twin keeps the 3 s of the stopwatch. The
  self-calibration's and the 1080p decode's tests log their times and keep every other assertion, their bars (a second; faster than
  real time) in Perf twins (`SelfCalibrationTests.SelfCalibrationSolvesNoisyRoomPairsWithinASecond`,
  `PerformanceTests.A1080pH264DecodesFasterThanRealTimeInSoftware`): pinned to one core beside 8 and 12 busy loops the old bars
  failed 3 of 3 (1691 to 1759 ms; 16.4 to 18.4 fps) and the tests pass 3 of 3.
  `ClockSlewTests.AStepAfterAdoptionLeavesTheLeadersAnchorAndMovesTheRest`' checks after its step each name every lane's anchors,
  the clock's steps and the journal; its failure kept no message and has not been reproduced (`docs/ROADMAP.md`).
  `SurveyImportPerfTests`' bars are unchanged.
- Done: CI runs the `Category=Perf` tests in a step of their own after the suites, on both jobs, so a bar times its own work and
  not the suites' beside it. The suites leave them out (`Category!=Perf`; on Windows beside `Backend!=D3D12`), and the Perf step
  runs each project's, one project and one test collection at a time (`xUnit.ParallelizeTestCollections=false`), its results as
  `<project>.perf.trx`, whatever the suites said; a project that fails is named with the machine's load around its run (Linux: the
  load averages before and after, and the cores; Windows: the processors' load before and after, and the cores) and, on Windows,
  each failed test with its message. Every bar is unchanged: the survey import's 15 s, 50 ms and 10 ms slice, the light plan's
  3 s, the first map's 5 s, the HAP and 1080p decode throughput, the clip loop's two real-time seconds, the self-calibration's
  second and the projector solve's slice. The integration gate's own script is not in this tree and still runs them inside the
  suites (`docs/ROADMAP.md`).
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
- Open (efficiency, not measured as the cause of anything): a backup's mirror link (`MirrorLink`) and a follower's anchor link
  (`AnchorFollower`) say HELLO, which makes each a STATE listener on its primary or leader (`WireSession`'s HELLO), and each drops
  every STATE line it is sent: every change there (coalesced to one push per 200 ms, and at least one a second while anything
  plays; about 7.5 KB in the backup rig) crosses to every backup and follower, which reads and discards it. The pushes count as the
  peer speaking on those links, which is right. Saying `SUBSCRIBE OFF` before their own subscription (OFF also ends topics, so it
  goes first), or a HELLO that is no listener, closes it; either is a wire change with its catalogue row and papers.
- Open: a fleet monitor takes no newer hello of a peer while it waits out its backoff after a subscription ended
  (`FleetMonitor.Consider` returns while the peer's feed runs), so a peer heard again at another address, port or certificate is
  dialled up to about 9.6 s late; and the monitor's peers (`Peers()`, which STATE and the board read) leave out a machine never
  heard, so nothing a person reads says why a peer whose hello is heard has no health (`WordsOf` says it, to tests). Cutting the
  wait short when the dial changes, and a never-heard peer's words on the board, close it.
- Open: the hello shares the port it listens on with every other socket on the machine (SO_REUSEADDR, and SO_REUSEPORT on Linux)
  on the port a test asks the system for too (`HelloOptions.ListenPort` 0), so a test node's hello socket can take a port another
  live test node's hello socket holds (here, 71 of 2000 live binds to port 0 with those options, none without them; Windows not
  measured), and the hot-backup and sync rigs give their nodes the same constant ids (`BackupRig.PrimaryId` is
  `SyncRig.LeaderId`), so a rig's node can hear another rig's node under a peer's id. Sharing only the well-known port closes it;
  the hot-backup meeting now waits for the peer at the web port it serves, and names what it holds when it does not hear it.
- Open, not reproduced or not explained (none is changed to pass): `ClockSlewTests`' 3 h 12 min step (its 597.5 s matches
  `pl_local`'s clamped anchor, so the failure is probably read wrongly; not reproduced); the one `FanoutTests` failure in a gate
  run (its test and line were not recorded, so the runner's misread above is its most likely cause, not a proven one);
  `FleetMonitorTests`' 10 s wait for the peer to read live (the test now waits until the headline and the context match the record
  the monitor holds; the 10 s "peer not live" is consistent with A's monitor's first subscription to B ending unheard and its next
  dial waiting on A's fleet clock, which the test did not move before B read live, not proven; the wait now moves that clock while
  the monitor waits, and names what A holds of B, above); `OscServerTests.ASenderNotHeardForTenMinutesNoLongerCounts`' one
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
  wait for the link's first clip in STATE (one failure on the batch's baseline; consistent with the clip's 3 s of trust on the
  node's real clock running out before the test read it, not proven; the node now runs on a clock the test holds, above);
  `HttpHostTests.TheLongPollWaitsForTheNextChangeAndGivesUpAtItsLimit`' one Windows long-poll answered before its check
  (consistent with the check's thread running late, above, not proven, a request that faulted not ruled out; the poll's limit now
  runs on a clock the test moves);
  `NodeHostTests.TheNodeHostRunsAnEngineThatAnswersPing`' one Windows wait for its first STATE (its `PING` answered; its cause
  open, above: STATE's first build as measured does not reach 10 s, and a wait only STATE meets, a frozen or starved process, or a starved test host is not ruled out; an unanswered line now
  names its stage: when it came, a second connection's `PING` and `CONTEXT`, a STATE write's slowest part, the node's output; the
  Windows job passed it at 39caa04c, its one run there since). On that runner the fresh node also draws its default project's
  display output (1920 × 1080 at 60 Hz, offscreen, on Mesa's llvmpipe) in a process the machine part claims HIGH, beside a test
  host at normal priority on four cores; whether a node with no display draws an output nobody sees is the owner's call;
  `StageResourcesTests.TheAutomaticBlendWaitsForAQuietStageAndKeepsItsLastGoodMaps`' one Windows
  read of a solved blend just after its first settle (consistent with its continuation, or the stage builder between its signal to
  the blender and its tables, running later than the blend's real-time quiet time, above, not proven; the quiet time now runs on a
  clock the test moves);
  `PanicTests.RewindCuesTheItemAtItsInPointAtTheFadesEnd`' two gate failures (int-tests4's, and a loaded Linux gate's whose message
  was lost; consistent with its read of the journal racing the panic step, above, which the test now waits out, not proven);
  `ProjectorViewTests.TheProjectorViewIsTheOutputsPictureBeforeItsTrimAndBlackAround`' one loaded Linux failure (its message lost;
  consistent with its read racing the stage's builder, above, which the test now waits for, not proven);
  `ThreadTimeTests.AThreadsTimeCountsItsWorkNotItsWait`' loaded Linux failures (consistent with a thread given under half a
  processor, above, which its lower bound no longer asks of the machine, not proven);
  `PromoteTests.AFollowerPromotedLeadsOnTheSameTimeLineAndTheOtherFollowsIt`' one Windows read of the follow's row before it was
  written (consistent with a slow settings write, or the watch's thread held, between the epoch's move and the row, above, which
  the test now waits for, not proven);
  `SurveyImportPerfTests.TheImportNeverHoldsTheLaneMoreThanTenMillisecondsInASlice`' two loaded Linux failures (which assertion
  failed was not kept; an import refused for memory under the gate's heap limit fails its ready check, above, but what would hold
  that much of the heap in the gate's host is not found; its ready checks now say the import's words and the heap);
  `ScanEngineTests.AScanIsRefusedWithoutACameraAndCancelGivesTheOutputsBack`' one Windows failure at its line 420 (consistent with
  its 20 ms samples missing the scan's few hundred milliseconds of "scanning", not proven; the scan is now held there until the
  cancel is in, above); `BackupRenderTests.TheBackupPlaysTheSameVersionAndItsPictureIsTheSameBytesAtTheSameInstants`' one Windows
  wait for the backup's health from its line 141 (consistent with the primary's fleet monitor's first subscription ending unheard
  and its next dial waiting on a fleet clock the hot-backup tests never moved, not proven; the meeting now moves that clock while
  the monitor waits, and names what the node holds of its peer, above); the Engine test host's one Windows stall before its first
  test (run 435: no result and no sequence file before blame's 5 minutes; consistent with libvmx's build in the OMT tests' attribute
  waiting, while the suite was discovered, for the end of its compiler's pipes, not proven; the build now ends with its compiler,
  and the next hang prints every managed thread's stack in the job's log, above);
  `PhoneBridgeTests.APhoneScanBridgesIntoTheModelAsItIsPlaced`' one Windows "stopped sending: nothing for 10 s" at its line 45 (run
  436; consistent with the test host leaving the fake phone's pings unsent for the hub's 10 s of real time, not proven; the rig's hub
  now runs on a clock the test holds, above);
  `StagewatchSinkTests.ADeskThatDoesNotAnswerLeavesTheProposalOnTheNodesOwnList`' one Windows failure from the sink's dispose after
  2 s (consistent with its refused connect landing as the pull's stop was taken, above, not proven; the pull now ends on its stop
  whatever it meets, and its dispose throws nothing).
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
