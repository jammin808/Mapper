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
