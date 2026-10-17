# Carry-over from round-1 integration (fold into round-2 packages)

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
  a client or restarts the announcement) → owner decision. A remote desk now needs Companion.DeskAddresses.
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
  (the Eye keeps the first 64). Open for the owner: /api/context sits behind the token, so Patterns reads PlayMap's context
  over the wire (SUBSCRIBE CONTEXT); opening it is the owner's call. No verb accepts a changed fleet pin (the preflight row
  names keys/fleet-pins.json). The Patterns-side Eye patch is prepared for Patterns' maintainers and not applied (kept outside
  this tree).
- wp2-control (merged): LTC readers open and close off the control loop through audio's capture hub ("<device>@<channel>");
  PANIC BLACK, HARD, REWIND and NEXT disengage a chase (receipt `released`), CLEAR does not re-engage. Open: after a restart
  the recovery record keeps which chases are disengaged but not that a panic did it (the words fall back to "plays by hand");
  a device whose open never returns holds the hub's lock, so ControlService.DisposeAsync and the hub's Dispose wait on it
  (needs a bounded open in audio's hub); an engine built without the audio part (tests only) has no hub, so LTC and TIMECODE
  DEVICES say so. The context's command list sorts TIMECODE CHASE, ENGAGE and INTERNAL with the transport verbs, which moves
  SYNC LEAD, SYNC FOLLOW and LINK PATTERNS out of the Eye's first 64 — which 64 the Eye sees is the owner's call.
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
  free-wheeling with the anchor feed lost; it passes in every other run and locally). Watch all three.
