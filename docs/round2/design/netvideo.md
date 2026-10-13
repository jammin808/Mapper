# Work package `netvideo`: network video in and out — NDI® 6.3 and OMT (capture cards later)

*Design spec, round 2. Written 13 Oct 2026 against `main` at `c12b186` (`/home/user/mapper`).*

**Sources.**
- Research: `r2/research/videoio.md`, cited as **[V §n]**.
- Read here, and cited:
  - the NDI SDK 6.3 headers (MIT, fetched today from DistroAV into `scratchpad/ext/netvideo/ndi-headers/`);
  - libomtnet `7d29031` (13 Oct 2026);
  - libvmx `a1828cb`;
  - Patterns' `Patterns.Ndi` (`bcdbf28`).
- Facts measured in this container while designing are marked **[A n]** and listed in Appendix A. The probes are under `scratchpad/r2/bench/netvideo/`.

**Scope boundary.** This package owns:
- the live-input model, discovery, receive, the send model, per-output and multiview sends, and the GL pack and readback path;
- NDI and OMT.

**texshare** owns Spout and Syphon. It plugs into the same `LiveUri`, `ILiveInputProvider`, `ISendProvider` and `VideoSend` contracts defined here. Capture cards (DeckLink, AJA) come later through the same provider seam.

---

## 0. The decision in one paragraph

A live input is a library item: `MediaItem { Kind = Stream, Location = "ndi:STUDIO (Cam 1)" | "omt:HOST (Program)" | "omt://10.0.0.5:6401" }`. A new `LiveSettings` record covers pacing, loss policy, backup and tally.

Each input is received **once per node**, by a warm `ILiveSource` that the engine's `VideoIoService` owns. The renderer, scan cameras, thumbnails and the multiview all share that source. The source fills a pooled native frame ring in the sender's own packed format (UYVY, UYVA, P216 or PA16), and the GPU unpacks it. No CPU colour conversion happens.

A send is a model entity, `VideoSend`, that taps one of:
- an output's final raster;
- an output's pre-warp picture;
- a canvas;
- any source;
- a multiview.

The render thread scales the tap and packs it into the wire format in a shader. It then reads it back through a non-stalling ring whose slots stay **mapped until the send worker is done with them**, so the render thread copies nothing. The NDI and OMT encoders run on one worker thread per send, latest-wins.

OMT is libomtnet, vendored as source with three bug fixes found here, plus libvmx built per RID. It is tested end to end in CI.

NDI loads the **user-installed** runtime through MIT-transcribed headers. It is tested against a fake `libndi.so.6` built in CI. No NDI binary ships.

---

## 1. Goal and the bar

**Goal.** A mapping node that any show's video network can feed and be fed by:
- Every NDI or OMT source on the LAN appears in STATE and on the desk within a second.
- One right-click puts a source on a surface.
- One right-click sends any output, canvas or source back out.
- Every input and send says in words how it is doing.
- Losing a feed has a defined outcome.
- The licence obligations are met by construction.

### 1.1 What the best do, and how PlayMap matches or beats each point

| What the field does (source) | PlayMap this round | Match / beat |
|---|---|---|
| **Inputs: NDI is table stakes.** MadMapper, Resolume, HeavyM, Millumin, Pixera and disguise take NDI in. VIOSO, MadMapper Space Scanner and Watchout use NDI cameras for calibration [V §0, COMPETITIVE §3] | NDI in through the user's runtime: find, receive, HX/HX2/HX3 decode (inside the runtime), tally back, PTZ. **NDI and OMT cameras are scan cameras.** Today `CameraSourceKind.Ndi` is handed to FFmpeg, which has no NDI and so cannot open it (`ScanCamera.OptionsFor`, §5). This package fixes that | **Match**, plus a fix to a broken path |
| **OMT.** vMix 29, an OBS plugin, Nimble, Zoe and TouchDesigner (community) have it. No mapper ships OMT [V §4.1] | OMT in and out on Windows, macOS **and Linux**, with alpha (UYVA/PA16), 16-bit (P216/PA16), 1/8 preview proxies, quality voting, tally and the Discovery Server. PlayMap can **host** an OMT Discovery Server for networks without mDNS | **Beat**: first OMT-native mapper. Tested end to end in CI [A1–A3] |
| **Sends.** Resolume/MadMapper/Millumin send an output or composition over NDI or Spout. Pixera and disguise send per output [V §9.9] | **Taps** at four points per protocol, in one picker: an output's final (post-warp) raster, its pre-warp picture, a canvas, any source (a playlist, a live input, media). Plus **multiview** | **Beat**: pre-warp and source taps let PlayMap be the content server for another mapper or for IMAG. Final taps feed LED processors and recorders exactly what the projector gets |
| **Multiview.** Broadcast mixers (vMix, TriCaster) have one. Pixera and Watchout show thumbnails in their GUI only [V §9.9, U] | A **multiview send** (NDI or OMT) and the same composite as a desk `VIEW`. It shows every output and input, labels, tally borders, the input-lost or blackout or frozen state, per-tile fps, and leader timecode | **Beat**: a health wall any NDI monitor can show, drawn from leader time, so two nodes draw identical frames |
| **Loss.** Most tools show black or the last frame and log a line [V §9.5] | Loss policy per input (`hold`, `black` or `fallback:<source>`) with stale and lost thresholds. A **backup URI** takes over automatically; switching back is a person's word. Every switch goes to the journal and into STATE, the context and Companion | **Beat**: defined outcomes, in words |
| **Jitter.** Showing the latest frame causes judder when the sender's clock nearly matches the display's. NDI ships FrameSync as a time-base corrector for this [NDI `FrameSync.h`, A6] | Pacing `latest` (lowest latency, for cameras and scans), `smooth` (a timestamp jitter buffer that PlayMap owns, both protocols, pure and tested), or `locked` (the same buffer with the leader's published offset, so main and backup show **the same frame** of the same sender) | **Beat**: `locked` makes network inputs mirror-deterministic, which COMPETITIVE §5c says none of us do today |
| **Failover downstream.** Watchout keeps runners going when the controller dies. NDI has `send_set_failover` [V §9.5, header] | `SEND FAILOVER <send> PEER <node>` resolves the same send on the backup node through the Show Bus and sets NDI failover, so third-party receivers follow PlayMap's backup | **Beat**: tied to PlayMap's redundancy |
| **Licence.** NDI's SDK terms changed in Aug 2025 (6.2.1). Avolites and ETC dropped NDI [V §5.5] | No NDI binary ships. The runtime is the user's. Headers are MIT-transcribed. The ndi.video link sits beside every NDI picker. ® and the trademark sentence appear in About and the docs. A CI test fails if any NDI binary lands in the published output | **Match**, with a check that keeps it true |
| **Discovery visibility.** Pickers list names only [U] | `STATE.videoio.discovered` lists each source's protocol, host and address, when it was last seen, whether it is in the library, and **which Show Bus peer it belongs to**. A Patterns node's NDI feed shows as "Patterns · Program" | **Beat**: the network is labelled by the show, not by hostnames |
| **Health and preflight.** Competitors flag "no signal" after the fact [V §9.5] | `VIDEOIO CHECK` and the showsafety preflight run these checks: runtime present and its version, libvmx and its CPU path, mDNS daemon, IPv6/IPv4 path, port bind, send-name collisions, bandwidth against the NIC (counting OMT receivers), and each on-air input delivering for the soak | **Beat** |

### 1.2 What is deliberately not claimed

- **NDI HX/HDR sending.** The Standard SDK cannot send HX or HDR metadata [V §5.3–5.4]. PlayMap sends SDR High Bandwidth only and refuses an HDR NDI send with that reason.
- **Glass-to-glass figures for NDI.** None are measured here. The real runtime cannot be fetched in this container: docs.ndi.video is blocked and there is no Linux runtime package.

---

## 2. Scope this round

### Must

| # | Item | Acceptance criteria (checkable) |
|---|---|---|
| M1 | **Core spine.** `LiveUri`, `LiveSettings` on `MediaItem`, `VideoSend` and `Project.Sends`, `OutputKind.Omt`, `CameraSourceKind.Omt`, `Ids.Send = "sd"`, `PixelLayout.Uyvy/Uyva/P216/Pa16`, `VideoFrameLease.ArrivalNs/SenderTimestamp`, the contracts in §4.4 | (a) `ProjectJsonTests`: a schema-1 file without `live`/`sends` loads unchanged. A round trip keeps every new field. Unknown enum words are refused with the field named.<br>(b) `LiveUriTests`: `ndi:STUDIO (Cam 1)`, `ndi://10.0.0.5:5961`, `omt:VM (Program)`, `omt://h:6401`, a glob `ndi:*(Cam 1)`; `rtsp://…` is `Ffmpeg`; `ndi:` with an empty name is refused.<br>(c) Every existing test passes |
| M2 | **GPU unpack** of the four packed layouts, with alpha | GL tests on llvmpipe:<br>– UYVY, UYVA, P216 and PA16 frames made by the CPU twin (`PackReference.FromRgb`) of a 64-colour chart convert to the same RGB as the existing Yuv422P path, ±1/255 at 8-bit and ±1/1023 at 16-bit;<br>– the alpha plane reaches `Picture.a`;<br>– odd width is refused with words;<br>– the last chroma pair at the right edge is clamped (no wrap) |
| M3 | **GPU pack and the send readback ring** | GL tests:<br>– `pack` (UYVY, UYVA, P216, PA16, BGRA) of a chart read back with `SendReadbackRing` equals `PackReference.FromRgb` ±1 code, at 1920×1080 and 1280×720;<br>– a slot handed to a fake sink stays mapped and pending until `Release()`, and its bytes do not change in the meantime;<br>– with 4 slots and a sink that holds 2, the ring never blocks: requests beyond that are skipped and counted `dropped`;<br>– the render thread allocates 0 B per frame after warm-up (`FramePathAllocatedBytes`) |
| M4 | **PlayMap.Video common**: `LiveFramePool`, `LiveFrameLease`, `TimestampJitterBuffer`, `SendWorker`, `DiscoveryCache`, `BandwidthEstimator` | Pure tests:<br>– pool leases are ref-counted; a held frame is never overwritten; 0 B per frame after warm-up (`GC.GetAllocatedBytesForCurrentThread`);<br>– the jitter buffer (§3.5) holds the cadence vectors in §7;<br>– a send worker is latest-wins: an unsent older frame is released and counted;<br>– discovery merges, ages out after 15 s and annotates peers by address |
| M5 | **OMT in and out** (vendored libomtnet with the patches in §3.8; libvmx per RID) | CI here, real loopback:<br>– send → receive of UYVY, UYVA, P216, PA16 and BGRA at 1080p, with the luma error ≤ 2 codes;<br>– preview receives 1/8 size;<br>– tally from receiver to sender;<br>– `Quality` voting;<br>– an in-process Discovery Server lists the sender, and the name goes when the sender is disposed;<br>– **no avahi-daemon and no IPv6 do not crash or stall the process** (the probe aborted before the patches [A1]) |
| M6 | **NDI** through the user's runtime: loader, find, receive (capture thread), send (async ring), tally both ways, connections, failover, per-frame `ndi_color_info` | Against the fake runtime built in CI (§7.2):<br>– struct sizes and offsets equal the clang-measured layout [A4];<br>– a buffer passed to `send_send_video_async_v2` is never written or released before the next sync event (the shim checksums it);<br>– find, receive, tally, failover and connections round-trip;<br>– a missing runtime makes `STATE.videoio.ndi.state = "absent"` with the words and the ndi.link URL, and the node runs on;<br>– the audio FourCC constant is `'FLTp'` (0x70544C46), not Patterns' `'FLTP'` [A5] |
| M7 | **Engine**: `VideoIoService` (extension), the warm input pool, routing in `MediaBackend.Open`, `EngineSources` (leader-time acquire, tally marks, loss routing), `RenderHost.Sends`, stream outputs (`OutputKind.Ndi/Omt`) | Engine tests with a headless render host on Xvfb:<br>– **PlayMap → OMT → PlayMap loopback**: `SEND ADD OMT Loop output:op_1` then `INPUT ADD omt://127.0.0.1:<port>`, then `SURFACE SOURCE`. A second output shows the chart within 10 frames, ±3 codes;<br>– one receiver per input although three consumers read it;<br>– a send not watched (`OnlyWhenWatched`) costs no readback;<br>– a stream output's fps is measured against its own `RefreshHz` (carry-over) |
| M8 | **Verbs, STATE, context, MCP, NodeSettings** (§4.2–4.6) | Engine tests assert JSON fields:<br>– every verb's refusal reasons (unknown scheme, duplicate URI, runtime absent, name collision, bandwidth guard arithmetic, a reconnect of a healthy input, PTZ on a non-PTZ source);<br>– `VERBS` lists them with risks;<br>– `STATE.videoio` matches §4.5;<br>– context items and links;<br>– the desk's fake engine writes the same shape |
| M9 | **Loss policy and backup** | Fake-provider tests:<br>– `stale` at 3 periods and `lost` at `LostMs`;<br>– `hold` keeps the last picture, `black` draws black, `fallback:color:#…`/`media:…`/`generator:…` draws that;<br>– the backup takes over on `lost`, journalled once;<br>– returning to the primary needs `INPUT SWITCH`;<br>– a recovered primary (no backup) returns after `RecoverMs` of steady frames |
| M10 | **Multiview** send and `VIEW … SHOW multiview:<send>` | GL test: 4 outputs + 2 inputs → a 3×2 grid. Labels are drawn. A program-tallied tile has a 4 px red border. A lost input shows "NO SIGNAL" plus its words. A blacked-out output says "BLACK". Two renders at the same leader instant are byte-identical |
| M11 | **Scan cameras**: `LiveCamera` for `Ndi`/`Omt` rigs; `Gray8` reads UYVY and P216 | Test: an OMT loopback sender showing a gray ramp → `LiveCamera.NextAsync` gives gray8 with the correct ramp (±1) and `ArrivalNs` set. `ScanViews` no longer says "no camera" for an NDI or OMT rig |
| M12 | **Licence obligations** | – `LicenceTests` scans the publish output: no `Processing.NDI.Lib*`, `libndi*` or `*.ndi*` binaries.<br>– `THIRDPARTY-NOTICES.md` lists libomtnet, libvmx, sse2neon and the NDI headers (MIT, Vizrt NDI AB).<br>– The About box and `docs/VIDEO.md` carry "NDI® is a registered trademark of Vizrt NDI AB".<br>– Every NDI picker has an `ndi.video` link (a desk UI test finds it) |
| M13 | **Papers**: `docs/VIDEO.md` (new), WIRE (verbs, STATE.videoio), ARCHITECTURE (§2 Video row), ROADMAP R3.3 status, THIRDPARTY-NOTICES | Review |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **`smooth` pacing** for both protocols (the jitter buffer on the render path) | Simulated sender, 60 Hz with ±6 ms jitter, shown on a 60 Hz output: 0 repeats and 0 drops after 2 s (`latest` gives > 5 % repeats on the same trace). A 50 fps sender on 60 Hz: exactly one repeat every 5 frames, no double repeats |
| S2 | **`locked` pacing** across nodes (needs fleet's anchor push; until then single-node, using the local anchor) | Two engines in one process (ManualClock, NetworkClock offset), one fake sender, independent jitter per receiver: for 600 frames the chosen sender timestamp at each leader instant is identical on both nodes when D ≥ jitter, and "late" is counted when not |
| S3 | **PTZ** (`INPUT PTZ`) for NDI | Fake runtime: preset recall, store, zoom, pan/tilt and focus reach the shim with the right values. `ptz:false` refuses with words |
| S4 | **Discovery thumbnails**: `VIEW <v> SHOW discover:<uri>` (NDI lowest bandwidth / OMT preview receiver on a 10 s lease) and `THUMB input:<id>` / `discover:<uri>` for Companion | OMT loopback: the view shows the 1/8 proxy; the proxy receiver closes 10 s after the last show |
| S5 | **Failover sends**: `SEND FAILOVER … PEER <node>` | Two engines on the Show Bus loopback: B's send full name is resolved from B's STATE, and the shim records `send_set_failover` |
| S6 | **Host an OMT Discovery Server** (`VIDEOIO SET OMT-SERVE ON`) | In-process: another process with the server set as its `DiscoveryServer` lists PlayMap's sends [A3] |
| S7 | **Show Bus annotation**: discovered sources whose address belongs to a heard peer are named after it; a context link `peer → input` | Test with a fake peer at 127.0.0.1 |

### Later

- **Capture cards:**
  - DeckLink through COM interop and timecode [V §6.1];
  - AJA through a C shim.
- Audio:
  - input audio as an analysis source (audio-reactive WP);
  - embedded audio in sends.
- **OMT `CompressedOnly` recording.** Capture an input to file, for rehearsal or for a deterministic show.
- **HDR sends.** OMT P216 with `ndi_color_info`, once the final picture is 10-bit or more (present WP's `Rgb10A2`).
- WHIP/WHEP (MediaMTX sidecar) [V §7].
- Syphon (texshare, macOS engine).
- NDI Discovery Server monitoring APIs (6.3 send/recv advertisers).

---

## 3. Architecture

### 3.1 Modules (fences kept)

```
PlayMap.Core            LiveUri · LiveSettings · VideoSend · PixelLayout(+4) · ILiveSource · ILiveInputProvider ·
                        ISendProvider · ISendSink · SendFrame · LiveHealth · SendStats · ProviderStatus   (no I/O)
PlayMap.Render          VideoTextureSet (+packed/16-bit/alpha) · yuv shader (+uPacked, uAlpha) · pack shaders ·
                        SendReadbackRing · MultiviewCompositor · RenderEngine.Sends.cs (taps)
PlayMap.Video           (edge: Core only) LiveFramePool/Lease · TimestampJitterBuffer · SendWorker · DiscoveryCache ·
                        BandwidthEstimator · PackReference (CPU twin, used by tests and the fake) · LiveRouter
PlayMap.Video.Omt       (edge: Core, Video, ThirdParty.Omt; native libvmx per RID) OmtProvider · OmtSource · OmtSink ·
                        OmtFinder · OmtServerHost · OmtLog (OMTLogging → ILogger)
PlayMap.Video.Ndi       (edge: Core, Video; the runtime is loaded at run time) NdiRuntime · NdiNative · NdiProvider ·
                        NdiFinder · NdiSource · NdiSink · NdiPtz · NdiColorInfo
src/ThirdParty/libomtnet  vendored source, pinned 7d29031, analyzers off, patch list (§3.8); assembly PlayMap.ThirdParty.Omt
PlayMap.Engine          VideoIoService (IEngineExtension) · LiveInputPool (warm sources) · EngineCore.VideoIo.cs (verbs,
                        STATE, context) · Rendering/RenderHost.Sends.cs · Rendering/MultiviewTiles.cs ·
                        Scanning/LiveCamera.cs · hunks in MediaBackend, EngineSources, SourceResolver, ScanCamera
PlayMap.App             Inputs section, Source browser, Sends in the Outputs drawer, VideoIO in the Network drawer, About
```

**Why three edge assemblies.**
- `PlayMap.Video.Ndi` holds only MIT-transcribed layouts and does nothing without a runtime. That makes the licence separation visible in the tree.
- `PlayMap.Video.Omt` owns the libvmx native asset.
- texshare adds `PlayMap.Video.Spout` beside them.

The Engine references all three. Only App references Avalonia.

**The vendored assembly.**
- `src/ThirdParty/libomtnet/PlayMap.ThirdParty.Omt.csproj` sits under `src/ThirdParty/`, whose nested `Directory.Build.props`
  (created by the spine) sets `RunAnalyzers=false`, `EnableNETAnalyzers=false`, `TreatWarningsAsErrors=false` and
  `Nullable=disable`, imports nothing from the root props and adds no `BannedSymbols.txt` — upstream code, kept diffable (it
  uses `Thread.Sleep` and `DateTime.Now`, §3.8). Compiling the bench's tree under the root props instead fails with ≈ 320
  errors (CA5392, S2445, S2178, S2223, RS0030 …); `generated_code = true` silences only nullable warnings, so the fence is the
  project boundary, not an `.editorconfig` section.

PlayMap's own code never calls `Thread.Sleep`. Its interop is only `OmtSource`/`OmtSink`, which live under the analyzers.

### 3.2 Threads and data flow

```
                         ┌──────────────────────── VideoIoService (engine extension) ─────────────────────────┐
 NDI runtime ─ find ──▶  │ NdiFinder thread: find_wait_for_sources(1000) → snapshot ─┐                         │
 OMT discovery ───────▶  │ OmtFinder timer 1 s: OMTDiscovery.GetAddresses() ──────────┼─▶ DiscoveryCache ─▶ STATE │
                         │                                                            │   (peer annotate)       │
                         │ LiveInputPool: one ILiveSource per input in the project (warm)                       │
                         │   NdiSource thread: recv_capture_v3(video,meta,50ms) → copy → LiveFramePool          │
                         │   OmtSource thread: OMTReceive.Receive(Video,50) (VMX decode here) → copy → pool     │
                         │   (one receiver per input; handles lent to renderer, scan, thumbnails, multiview)   │
                         └───────────────▲───────────────────────────────────────────────▲────────────────────┘
                                         │ IVideoSource handle (non-owning)               │ ISendSink (made off-thread)
 render thread ──────────────────────────┼────────────────────────────────────────────────┼──────────────────────
   EngineSources.Live: src.Acquire(leaderNs) → lease (pool slot)                          │
   SourceResolver → VideoTextureSet: PBO upload (packed) → yuv pass (uPacked/uAlpha) → RGBA16F picture
   RenderEngine.Render (outputs) → RenderHost.Sends: for each due send:
       tap texture → scale blit → pack pass (RGBA8 or RGBA16 target) → ReadPixels into PBO slot + fence
       collect: fence passed → map slot → sink.Offer(slot)  (slot stays mapped)            │
       next frames: slot.Released? → unmap, free                                          ▼
                                                      SendWorker thread per send ("send <name>"): latest-wins
                                                        NDI: send_send_video_async_v2(frame) → release the previous slot
                                                        OMT: OMTSend.Send(frame) (VMX encode, sync) → release the slot
                                                        every 250 ms: connections, tally → STATE
```

**Rules.**
- The render thread never calls a network API or an encoder.
- Its only native calls for sends are GL calls. It may call `ILiveSource.Acquire`, which is a lock-free read of the newest-slot index.
- It never waits on a worker. A worker that is still holding slots makes the ring skip that send's frame, and the skip is counted.

### 3.3 Receive

**Opening.** `MediaBackend.Open(item)` gains one hunk before FFmpeg: `if (Live?.TryOpen(item) is { } h) return h;`.
- `LiveRouter.TryOpen` parses `item.Location` with `LiveUri.TryParse`. It answers `null` for `Ffmpeg` URLs, which keep `CaptureSource`.
- For NDI and OMT it hands back a **handle** to the warm source in `LiveInputPool`. Disposing the handle does not close the receiver.
- Scan cameras use the same pool through `LiveCamera`.

**Warm pool.**
- Every Stream item with an NDI or OMT URI and `Live.Warm = true` (the default) is opened when it enters the project. It is closed when it leaves or is set cold.
- A cold input connects on first acquire and says "connecting" until its first frame, typically ≤ 1 s [U for NDI; OMT reconnect is every 1 s, `BeginConnect`].
- Rationale: "every selection change is instant", and OMT is unicast, so each extra receiver costs a whole stream [V §4.5].

**NdiSource.**
- Create: `recv_create_v3` with these settings:
  - `source_to_connect_to = {p_ndi_name or p_url_address}`;
  - `color_format = fastest` (UYVY, or UYVA with alpha) when `Depth` is `Auto` or `Eight`; `best` (P216/PA16 from native NDI senders) when `Depth = High`;
  - `bandwidth = highest`, or `lowest` for `Proxy`;
  - `allow_video_fields = false`;
  - `p_ndi_recv_name = "PlayMap <node> <input>"`.
- Capture thread: `recv_capture_v3(recv, &video, null, &meta, 50)`.
  - On `video`: copy the planes into a pool slot, then call `recv_free_video_v2`.
  - `p_metadata` is parsed for `ndi_color_info`: `transfer="bt_2100_pq|bt_2100_hlg"` sets Pq/Hlg, `matrix="bt_2020"` sets Bt2020.
  - `timestamp` becomes `SenderTimestamp`. `NDIlib_recv_timestamp_undefined` (INT64_MAX) means "none".
  - `ArrivalNs` is `MonoClock` at return.
  - On `status_change`: re-read `recv_ptz_is_supported` and `recv_get_web_control`.
  - On `error`: state `lost`, words "the sender went away".
  - Every 1 s: `recv_get_no_connections`, plus `recv_get_performance` for total and dropped counts.
- **Why copy rather than lease the NDI buffer.**
  - A copy frees the runtime's buffer at once.
  - It normalises the stride.
  - It lets the renderer, scan and thumbnails hold frames independently.
  - It costs one memcpy per frame on the receive thread: 4.1 MB at 1080p UYVY, 8.3 MB at P216.
  - A zero-copy wrap is a measured-first optimisation for later. Doctrine: measured, then governed.
- **Why not FrameSync for video.**
  - FrameSync is one puller per receiver, and its time base is tied to that puller's call rate.
  - PlayMap's single receiver serves several consumers.
  - The `locked` mode needs sender timestamps against leader time, which FrameSync hides.
  - The timestamp jitter buffer (§3.5) covers `smooth`, is pure, and is tested here.

**OmtSource.**
- Create: `new OMTReceive(address, OMTFrameType.Video | Metadata, preferred, flags)` with:
  - `preferred = UYVYorUYVA` for `Auto`/`Eight`;
  - `UYVYorUYVAorP216orPA16` for `High`;
  - `flags = Preview` for `Proxy`.
- Thread: `Receive(Video|Metadata, 50, ref frame)`. Its `Data` is valid until the next `Receive` (`tempVideo` reuse, read in `OMTReceive.cs`), so it is copied into a pool slot.
- Field mapping:
  - `Timestamp` (100 ns) becomes `SenderTimestamp`;
  - `ColorSpace` 601 or 709 becomes the matrix;
  - `FrameMetadata` is parsed for the same `ndi_color_info` element. This is PlayMap's documented OMT HDR convention, since OMT has no BT.2020/PQ field [V §4.2].
- Tally: `SetTally(new OMTTally(preview, program))`.
- Quality: `SetSuggestedQuality`.

**LiveFramePool.**
- `Slots = Live.Pacing == Latest ? 4 : 8`. Native memory is 64-byte aligned and sized per layout, reallocated only when the size grows.
- Frame header: `Layout, Width, Height, Planes[], SenderTimestamp, ArrivalNs, Generation, Matrix, Range, Transfer, HasAlpha`.
- `Acquire(target)` is lock-free. It reads the published index, does `AddRef`, and rents a pooled `LiveFrameLease`. It allocates nothing once warm. This mirrors `FramePool`/`LeasePool` in Media, but without referencing Media.

**Plane layouts** (as the uploader reads them):

| Layout | Planes | Texture(s) | Source |
|---|---|---|---|
| `Uyvy` | 1 × (w·2 B per row) | RGBA8 at (w/2, h): texel = U Y0 V Y1 | NDI/OMT `UYVY` |
| `Uyva` | UYVY plane, then an A8 plane (w B per row) | RGBA8 (w/2, h) + R8 (w, h) | NDI `UYVA` ("UYVY followed by alpha", `structs.h` l.66) |
| `P216` | Y16 (w·2 B per row), then UV16 interleaved (w·2 B per row, h rows) | R16 (w, h) + RG16 (w/2, h) | "16bpp version of the NV16 format" (`structs.h` l.70–75) |
| `Pa16` | P216 planes, then A16 (w·2 B per row) | + R16 (w, h) | `structs.h` l.77–82 |

**State machine** (per input, evaluated on the receive thread and on each STATE read):

```
connecting ──first frame──▶ ok ──age > StaleMs (default 3 × own period)──▶ stale ──age > LostMs (1000)──▶ lost
     ▲                        ▲                                                                       │
     └──── sender reappears ──┴────────── RecoverMs (2000) of steady frames ◀──────────────────────────┘
absent: the URI names nothing discovered and no connection; failed: the provider is absent or refused (words)
```

### 3.4 Loss policy, backup and routing to the renderer

- `EngineSources.Live(id, item)` changes in two ways.
  - It calls `src.Acquire(leaderNs)` for an `ILiveSource` instead of `Acquire(0)`, so `smooth` and `locked` select by time.
  - It records `slot.DrawnFrame = _frame` and whether the draw was an output or a preview pass (`RenderHost` sets `_sources.InPreview` around `RenderPreviews` and multiview).
- `SourceResolver.Resolve` gains one arm: `case SourceKind.Live or SourceKind.Media when _provider.Route(source, _leaderNs) is { } routed`. The routed source is resolved once. Live or Stream targets are refused by validation, so routing cannot recurse. `EngineSources.Route` answers:
  - `null` while the input is ok or stale, or lost with `OnLoss = Hold`: the renderer holds the last picture;
  - `SourceRef.None` when it is lost with `Black`: black;
  - the fallback `SourceRef` when it is lost with `Fallback`: a colour, a generator or a canvas is drawn by the renderer, and `media:` goes through `MediaAt`.
- **Backup URI.**
  - On `lost`, `LiveInputPool` switches the input's handle to the backup source. The backup is warm when `Warm` is true, so the switch is a pointer swap at the next acquire.
  - It journals `"Cam 1 lost (no frame for 1.0 s) — showing its backup ndi:STUDIO2 (Cam 1)"` and sets `STATE.videoio.inputs[].using = "backup"`.
  - Switching back is `INPUT SWITCH <in> PRIMARY`, a person's word. Show doctrine: never flap on the air.

**Tally to senders.** The `VideoIoService` tick runs at 10 Hz on a timer, not on the render thread. It reads each slot's last draw frames from an `EngineSources` snapshot (`TallySnapshot`: id → (lastOutputFrame, lastPreviewFrame)):
- program = drawn into an output in the last 15 render frames **and** not `air.EffectiveBlackout`;
- preview = drawn in previews or the multiview, or (when showsafety lands) in a blind edit.

It calls `ILiveSource.SetTally` only on change. NDI: `recv_set_tally`. OMT: `SetTally`.

### 3.5 Pacing: `TimestampJitterBuffer` (pure, in PlayMap.Video)

Inputs per frame: the sender timestamp `s` (ns = 100 ns × field) and the arrival `a` (mono ns).

1. **Offset floor.**
   - `o = min over the last 2 s of (a − s)`. This is the fastest network path.
   - Drift: least squares of the per-250 ms minima over 20 s gives `ppm`, and `o(t)` is extrapolated.
   - The sender's clock rate is reported (`clockPpm`).
2. **Delay.**
   - `D = max(1.5 × median period, p95(a − s − o) + 2 ms)`, clamped to [1, 4] periods.
   - Re-estimated every second, with hysteresis: it grows at once and shrinks by ≤ 10 % per second.
3. **Select** for the render frame whose instant is `V`:
   - `smooth`: `V` = the predicted vblank in mono ns, which the engine supplies as `leaderNs` mapped to mono by `NetworkClock`.
   - Choose the newest buffered frame with `s + o + D ≤ V`.
   - If none qualifies, choose the oldest, counted "early".
   - If the choice equals the last one, count "repeated". If frames were skipped, count "dropped".
4. **`locked`**:
   - The sync leader publishes per input `A = o_leader + (leader − mono)_leader + D_leader` as `InputAnchor{uri, aNs, dNs, seq}` on fleet's anchor push.
   - Every node selects the newest frame with `s + A ≤ L`, where `L` is the frame's leader instant.
   - Nodes that receive the same sender therefore pick the same `s` for the same `L`. A node missing that frame shows the previous one and counts "late". The STATE badge stays "deterministic" only while `late == 0` over the last 10 s.
5. **Missing timestamps** (NDI undefined, or OMT 0/−1): fall back to `s := a − o_est` (the arrival line). `smooth` then still evens the cadence by `D`. `locked` is refused with words: "the sender gives no timestamps".

All of the steps above are pure functions of `(s, a, V)` sequences, so they are tested here with synthetic traces (§7).

### 3.6 Sends

**Plan.**
- On a project change, `RenderHost.Sends` builds `SendPlan[]`: explicit `Project.Sends`, plus one implicit send per enabled `OutputKind.Ndi`/`Omt` output.
- The implicit send has id = the output id, name = the output name, tap = `output:<id>`, size = the raster, fps = `RefreshHz`, and format from `Output.Stream`.
- Sinks are created and updated by `VideoIoService` on a worker, because `NDIlib_send_create` and `OMTSend` bind sockets. The plan holds `volatile ISendSink?`, and a send without a sink yet is skipped.

**Due test (leader-time grid).**
- For a send at `fps = N/D`, frame `k = floor(leaderNs × N / (D × 1e9))`. The send fires when `k > lastK`.
- Two nodes therefore send on the same grid, and a send slower than the loop decimates exactly.
- A send faster than the loop sends every frame and reports `asked 60, rendered 50`.

**Watch gate.**
- When `OnlyWhenWatched` is true (the default) and `sink.Connections == 0`, no pack or readback happens.
- `Connections` is refreshed ≤ 4 Hz by the worker: NDI `send_get_no_connections(…, 0)`, OMT `Connections`.

**Tap resolution** (`RenderEngine.Send(SendRequest)`, after `Render` in the same frame):

| Tap | Texture | Notes |
|---|---|---|
| `output:<op>` | `OutputState.Final` (8-bit, post warp/blend/mask/trim/dither) | Black with words when the output is off |
| `prewarp:<op>` | `OutputState.PreWarp` + `PreWarpRect`, newly recorded in `DrawOutput` | The projector render, the canvas rectangle or the source picture, before the output stage |
| `canvas:<cv>` | `SourceResolver.Canvas(id)` (RGBA16F) | Real high bit depth for P216 |
| `source:<ref>` | `SourceResolver.Resolve(ref, w, h, ctx)` | Any source: a playlist, a live input, a generator, media |
| `multiview` | `MultiviewCompositor.Compose(tiles, w, h)` | §3.7 |

**Scale.**
- The tap is blitted with linear filtering into the send's RGBA8 or RGBA16F scratch when the size differs.
- Aspect: `fit` (letterbox, default) or `fill`, set in `VideoSend.Fit`.

**Pack pass** (`pack` program, one source, mode uniform; target top row first as everywhere):

| Format | Target | Texel (i, row) holds | Readback |
|---|---|---|---|
| `Uyvy` | RGBA8 (w/2, h) | U, Y(2i), V, Y(2i+1). Chroma is co-sited: `[1,2,1]/4` over pixels 2i−1, 2i, 2i+1, clamped | `GL_RGBA/UNSIGNED_BYTE` → bytes `U Y0 V Y1` |
| `Uyva` | RGBA8 (w/2, h + h/2) | rows 0..h−1 as UYVY; row h+r, texel i: alpha bytes 4i..4i+3 of the 2w-byte span holding alpha rows 2r and 2r+1 | contiguous = UYVY plane, then an A8 plane of stride w |
| `P216` | RGBA16 (w/4, 2h) | band 0: Y of pixels 4i..4i+3; band 1: U, V of pair 4i, then U, V of pair 4i+2 | `GL_RGBA/UNSIGNED_SHORT` → Y16 plane, then UV16 plane, both stride 2w (exact NV16-16) |
| `Pa16` | RGBA16 (w/4, 3h) | band 2: A16 of pixels 4i..4i+3 | + an A16 plane |
| `Bgra` | RGBA8 (w, h) | the picture | `GL_BGRA` |

- Encoding is BT.709 limited range (16–235/240 at 8 bits, ×256 at 16 bits).
- `PackReference` in PlayMap.Video is the CPU twin. It is used by the tests and by the fake NDI shim's checks.
- Width must be a multiple of 4 for `Uyva`, `P216` and `Pa16`, and even for `Uyvy`. `SEND ADD` rounds down to the next valid size and says so.
- `RGBA16` (unorm) render targets are core in GL 3.3. On the ES dialect (the present WP's ANGLE path) they need `EXT_texture_norm16`, which Mesa ES has [present A1].

**`SendReadbackRing`** (Render/Video/SendReadback.cs; generalises `PixelPackRing`):
- 4 slots of PBOs with fences. `Request(target, ReadFormat, stamp, fpsN, fpsD)` is skipped when no slot is free, counted `dropped`.
- `Collect()`: for each pending slot whose fence passed, `MapBufferRange(READ)` and hand the slot as a `SendFrame` to the sink.
- **The buffer stays mapped** until the worker calls `Release()`, which is an `Interlocked` flag. Then the next `Collect()` (render thread) unmaps it and frees the slot.
- GL allows a buffer to stay mapped while other GL work proceeds, as long as that buffer is not used by GL. Another thread reading the mapped pointer is plain memory access.
- Hunk: `GlDevice.ReadIntoPackBuffer(target, buffer, ReadFormat)` gains the format and type parameter (`Bgra8`, `Rgba8`, `Rgba16`).

**Workers** (`SendWorker`: one thread per send, named `send <name>`, a latest-wins box of one frame):
- **NDI.**
  - `NDIlib_video_frame_v2_t { xres, yres, FourCC, frame_rate_N/D, picture_aspect_ratio = 0, progressive, timecode = leaderNs/100, p_data, line_stride_in_bytes = w·2 (UYVY/UYVA/P216/PA16) or w·4, p_metadata = null | "<ndi_color_info …/>", timestamp = 0 }`.
  - It then calls `send_send_video_async_v2` and **releases the previously submitted slot**, since the new async call is the sync event [`Send.h` l.75–87].
  - On disable or dispose it calls `send_send_video_v2(null)` and then releases the last slot.
  - `send_create` with `clock_video = false`, because PlayMap paces from the render loop.
- **OMT.** `OMTMediaFrame { Type = Video, Timestamp = leaderNs/100, Codec, Width, Height, Stride, Flags (Alpha|HighBitDepth), FrameRateN/D, AspectRatio = w/h, ColorSpace = BT709, Data, DataLength, FrameMetadata }`, then `OMTSend.Send(frame)`. It encodes synchronously [A2: p50 4.6–8.6 ms at 1080p on this 4-vCPU box] and the worker releases the slot at once.
- **Every 250 ms:** connections, tally (`send_get_tally(…, 0)` / `GetTally(0, ref t)`), bytes sent (OMT `GetVideoStatistics`; for NDI an estimate from frames × format), and the encode time p95.

**Rates.** Frame rate rationals: 23.976 → 24000/1001, 29.97 → 30000/1001, 59.94 → 60000/1001. Anything else → round(fps × 1000)/1000.

**Stamps.** A downstream PlayMap reading the send aligns by the leader instant: OMT `Timestamp` and NDI `timecode` are leader ns / 100.

### 3.7 Multiview (Render/Video/Multiview.cs + Engine/Rendering/MultiviewTiles.cs)

- **Tiles.**
  - `VideoSend.Tiles` (refs) or auto: every enabled output in plan order, then every live input in use, then the sends.
  - Grid: `cols = ceil(sqrt(n))`, a 16:9 cell, the picture fitted.
- **Each tile has:**
  - the picture (output final, input texture, or a send's tap);
  - a label bar: a Skia text texture cached by `(label, width)`, drawn from the existing `SkiaKit`;
  - a tally border, 4 px: red program, green preview;
  - a state overlay: `NO SIGNAL — <words>` (lost), an amber corner (stale), `BLACK` (output in blackout), `FROZEN`, `TEST PATTERN <kind>`, `OFF`;
  - fps and resolution in the corner.
- **Header bar:** the node name, the project name, leader timecode `HH:MM:SS:FF` at the project rate, and the count of green outputs. All of it comes from the frame's leader instant, so two nodes draw identical frames.
- **Cost:** one pass per tile plus text quads. Labels are re-rasterised only when they change. Allocation is 0 B per frame once warm, since the tile array is reused.
- **Desk:** `VIEW mv1 SHOW multiview:<send>` (a new `ViewKind.Multiview`) draws the same composite into a preview ring. With a missing send id, `multiview:auto` shows the auto layout.

### 3.8 OMT vendoring: pinned source and the patches found here

| # | Defect (found here) | Effect | Patch (≤ 30 lines in total; upstream PRs opened) |
|---|---|---|---|
| P1 | `OMTDiscoveryAvahi` calls `avahi_service_browser_new(client=NULL, …)` and `avahi_entry_group_new(NULL, …)` when `avahi_client_new` failed | **The process aborts** (`browser.c:583: avahi_service_browser_new: Assertion 'client' failed`) on any Linux machine without `avahi-daemon` running [A1] | Return early when `client == 0`. `RegisterAddressInternal` returns false. Discovery falls back to a server or manual addresses, and STATE says `mdns: "none (avahi-daemon not running)"` |
| P2 | `OMTSend` and `OMTMultiConnectSocket` create `InterNetworkV6` dual-stack sockets unconditionally | `SocketException 97 Address family not supported` when constructing a sender on hosts with IPv6 disabled (common on show networks) [A1] | `OMTUtils.NewTcpSocket()`: dual-stack when `Socket.OSSupportsIPv6`, else IPv4. Bind `IPAddress.Any` for v4 |
| P3 | `OMTAddress.AddAddress` stores IPv4 as v4-mapped IPv6 | With P2's IPv4 socket, `BeginConnect` throws `NotSupportedException "This protocol version is not supported"` [A1] | `MapToIPv4()` when the socket is IPv4 |
| P4 | `OMTDiscoveryServer` writes to `Console` | Noise on the engine's stdout | Route it through `OMTLogging.Write` |
| P5 | `OMTLogging` writes files under `OMT_STORAGE_PATH` | Logs land outside the node | Set `OMT_STORAGE_PATH = <NodeHome>/omt` and `OMTLogging.SetCallback(→ ILogger)` before first use (no source change) |
| P6 | `linux/OMTDiscoveryAvahi.cs:138` stops its thread with `Thread.Abort` (SYSLIB0006) | `PlatformNotSupportedException` on .NET 10 when discovery stops (found compiling the vendored tree under the repo's props) | A volatile stop flag checked by the loop, and `Join` with a timeout; `OmtDiscoveryTests` dispose discovery with no avahi-daemon |

- The patched tree is `scratchpad/r2/bench/netvideo/omt-patches.diff` (112 lines, including a diagnostic change to `OMTReceive` that is not shipped).
- **Configuration without touching the user's global OMT settings:** set `OMTSettings.GetInstance().SetString("DiscoveryServer", "omt://host:6399")` and `NetworkPortStart`/`End` **in memory, before** the first `OMTDiscovery.GetInstance()`, and never call `Save()`. Verified [A3].
- **libvmx.** CI builds it per RID with clang. The Linux x64 command is the upstream script, 7.7 s here [A1].
  - Natives: `runtimes/linux-x64/native/libvmx.so`, `linux-arm64` (NEON via sse2neon), `win-x64/native/libvmx.dll` (clang-cl), `osx-arm64`/`osx-x64` (universal dylib).
  - Loading: `NativeLibrary.SetDllImportResolver` on the ThirdParty assembly for `"libvmx"` → the RID folder beside the exe.
  - The CPU check (AVX2 or SSE4.2) goes into STATE.

### 3.9 NDI runtime loading

Search order. The first that loads wins, and the path goes into STATE:
1. `NodeSettings.VideoIo.NdiRuntimePath`.
2. `$NDI_RUNTIME_DIR_V6/Processing.NDI.Lib.x64.dll` (Windows).
3. Beside the exe. Allowed only because the user placed it there: "keep them in the app folder, never the system path" [V §5.5].
4. Standard folders: `%ProgramFiles%\NDI\NDI 6 Runtime\v6`, `/usr/local/lib/libndi.dylib`.
5. The loader name: `libndi.so.6`, `libndi.dylib`.

Binding:
- Exports are bound with `NativeLibrary.TryGetExport` into `delegate* unmanaged[Cdecl]` fields, using `[LibraryImport]`-free function pointers so there is no marshalling stub.
- **Required:** `initialize`, `destroy`, `version`, `is_supported_CPU`, `find_create_v2`, `find_destroy`, `find_wait_for_sources`, `find_get_current_sources`, `recv_create_v3`, `recv_destroy`, `recv_capture_v3` (or `_v2`), `recv_free_video_v2`, `recv_free_metadata`, `recv_set_tally`, `recv_get_no_connections`, `send_create`, `send_destroy`, `send_send_video_async_v2`, `send_send_video_v2`, `send_get_tally`, `send_get_no_connections`.
- **Optional, each gating a feature in STATE:** `send_set_failover`, `send_get_source_name`, `recv_get_performance`, `recv_ptz_*`, `recv_get_web_control`, `recv_free_string`, `recv_get_source_name`.

Initialisation:
- `NDIlib_is_supported_CPU()` false → "this CPU lacks SSE4.2".
- `NDIlib_initialize()` false → failed, with words.

Configuration:
- Before initialise, PlayMap sets `NDI_CONFIG_DIR = <NodeHome>/ndi` [V §5.6].
- It writes `ndi-config.v1.json` there from `NodeSettings.VideoIo`: `{"ndi":{"networks":{"discovery":"<servers>"},"groups":{"send":"…","recv":"…"}}}` [V-x search, see §9].
- The runtime reads it only at load, so a change says "applies at the next engine start".
- Process-wide groups and extra IPs are also passed per `find_create_v2` and `send_create` (`p_groups`, `p_extra_ips`). Those apply at once.

---

## 4. Spine needs

### 4.1 Model (`src/PlayMap.Core/Model/VideoIoModel.cs`, new). Backwards compatible; Schema stays 1

```csharp
namespace PlayMap.Core.Model;

public enum LivePacing { Latest, Smooth, Locked }
public enum LiveLoss { Hold, Black, Fallback }
public enum LiveDepth { Auto, Eight, High }

/// <summary>How a network input is received and what it does when it fails. Only Stream items with an NDI or OMT
/// (later Spout, DeckLink) URI read it; an older file without it reads <see cref="Default"/>.</summary>
public sealed record LiveSettings
{
    public static readonly LiveSettings Default = new();
    public LivePacing Pacing { get; init; } = LivePacing.Latest;  // "latest" | "smooth" | "locked"
    public bool Proxy { get; init; }                              // NDI lowest bandwidth / OMT 1/8 preview
    public LiveDepth Depth { get; init; } = LiveDepth.Auto;       // auto: 16-bit only when the sender is (OMT flag) or "high"
    public bool Warm { get; init; } = true;                       // keep connected while in the project
    public string Backup { get; init; } = "";                     // a second live URI; "" = none
    public LiveLoss OnLoss { get; init; } = LiveLoss.Hold;
    public string Fallback { get; init; } = "";                   // SourceRef text, never live/stream (validated)
    public int StaleMs { get; init; }                             // 0 = 3 periods of the input's own rate
    public int LostMs { get; init; } = 1000;                      // 100..60000
    public int RecoverMs { get; init; } = 2000;                   // steady frames before leaving a fallback
    public bool Tally { get; init; } = true;                      // tell the sender when it is on air
    public string Quality { get; init; } = "default";             // OMT suggested quality: default|low|medium|high
}

public enum SendProtocol { Ndi, Omt, Spout }                      // Spout: texshare WP
public enum SendFormat { Auto, Uyvy, Uyva, P216, Pa16, Bgra }
public enum SendFit { Fit, Fill, Stretch }

/// <summary>A picture this node sends: a tap of one point of the pipeline, packed, encoded and announced under a
/// name. Width/Height 0 = the tap's own size; Fps 0 = the tap's rate (an output's RefreshHz, else the show rate).</summary>
/// <param name="Id">"sd" + 8 hex (<see cref="Ids.Send"/>).</param>
/// <param name="Name">The source name on the network (NDI and OMT add "HOST (…)" themselves); 1–48 printable chars.</param>
/// <param name="Tap">"output:op…", "prewarp:op…", "canvas:cv…", "source:&lt;sourceref&gt;", "multiview".</param>
public sealed record VideoSend(string Id, string Name, SendProtocol Protocol, string Tap, int Width, int Height,
    double Fps, SendFormat Format, bool Enabled) : IEntity
{
    public string Quality { get; init; } = "default";            // OMT sender quality; NDI ignores (refused if set)
    public string Groups { get; init; } = "";                     // NDI groups ("" = public)
    public string Failover { get; init; } = "";                   // NDI full name | "peer:<nodeId>" | ""
    public SendFit Fit { get; init; } = SendFit.Fit;
    public bool OnlyWhenWatched { get; init; } = true;
    public ImmutableArray<string> Tiles { get; init; } = [];      // multiview: refs ("output:…", "media:…", "send:…"); [] = auto
    public string Label { get; init; } = "";                      // multiview title ("" = node name)
}

/// <summary>A stream output's encoding (OutputKind.Ndi / Omt): the output is drawn headless and leaves only as this.</summary>
public sealed record StreamOut
{
    public static readonly StreamOut Default = new();
    public SendFormat Format { get; init; } = SendFormat.Auto;    // auto: UYVY (UYVA when the output has alpha)
    public string Quality { get; init; } = "default";
    public string Groups { get; init; } = "";
    public string Failover { get; init; } = "";
    public bool OnlyWhenWatched { get; init; }                    // a stream output is a show output: always sending
}
```

**Hunks to existing model types:**
- `MediaItem`: `public LiveSettings? Live { get; init; }`. JSON `"live"`, written only when not null.
- `Project`: `public ImmutableArray<VideoSend> Sends { get; init; } = [];`. JSON `"sends"`. `AllIds()` yields send ids.
- `Output`: `public StreamOut? Stream { get; init; }`. JSON `"stream"`.
- `OutputKind`: `Omt` appended after `Virtual`. Enum words are camelCase strings, so order does not matter for the JSON.
- `CameraSourceKind`: `Omt` appended.
- `Ids`: `Send = "sd"`, a prefix not in use (checked against `Ids.cs` and the sibling specs: control `bd`/`ct`).
- `EngineCore.Edits`: `Locate` finds `VideoSend` in `Project.Sends`, and `Add` gains kind `send`.
- `Validate`:
  - name rules;
  - the tap target exists;
  - size multiple rules by format;
  - fps 1–240;
  - format allowed by protocol: NDI and OMT take UYVY/UYVA/P216/PA16/BGRA, Spout is texshare's;
  - NDI with `Quality` not "default" is refused;
  - `Failover` syntax;
  - `LiveSettings.Fallback` parses as a `SourceRef` whose kind is not Live and is not a Stream media item;
  - `Backup` parses as a live URI;
  - ranges.

**Example.**

```json
"media":[{"id":"md1a2b3c4d","name":"Cam 1","kind":"stream","location":"ndi:STUDIO (Cam 1)","contentHash":"","sizeBytes":0,
  "info":{…},"tags":["camera"],"live":{"pacing":"smooth","proxy":false,"depth":"auto","warm":true,"backup":"ndi:STUDIO2 (Cam 1)",
  "onLoss":"fallback","fallback":"media:md9f8e7d6c","staleMs":0,"lostMs":1000,"recoverMs":2000,"tally":true,"quality":"default"}}],
"sends":[{"id":"sd5e6f7a8b","name":"Main out","protocol":"ndi","tap":"output:op_1","width":0,"height":0,"fps":0,"format":"auto",
  "enabled":true,"quality":"default","groups":"","failover":"peer:0badc0de","fit":"fit","onlyWhenWatched":true,"tiles":[],"label":""}]
```

### 4.2 Verbs (`VerbCatalog`: new `VerbCategory.Video`)

| Verb | Syntax | Risk | Mutates | Reply (OK payload / refusals) |
|---|---|---|---|---|
| `INPUT LIST` | `INPUT LIST` | Low | no | `[{id,name,uri,proto,state,words,w,h,fps,using,tally,…}]` = `STATE.videoio.inputs` |
| `INPUT DISCOVER` | `INPUT DISCOVER [NDI\|OMT\|ALL] [WAIT <0-5000 ms>]` | Low | no | `{"ndi":"ready","omt":"ready","sources":[…discovered…]}`. It kicks a refresh; `WAIT` answers after that long (off-lane) |
| `INPUT ADD` | `INPUT ADD <uri> [<name>] [PACING LATEST\|SMOOTH\|LOCKED] [PROXY] [COLD]` | Review | yes | `{"id":"md…","uri":"…"}`. Refused: unknown scheme ("ndi:, omt:, or an FFmpeg URL"); "already in the library as md… (Cam 1)"; "NDI is not available here: <words> — ndi.video"; `LOCKED` on a node in no sync group |
| `INPUT FALLBACK` | `INPUT FALLBACK <input> HOLD\|BLACK\|<source>` | Review | yes | effect "Cam 1 on loss: shows Standby loop". Refused when unchanged, or when the source is live |
| `INPUT BACKUP` | `INPUT BACKUP <input> <uri>\|OFF` | Review | yes | Refused when it equals the primary |
| `INPUT SWITCH` | `INPUT SWITCH <input> PRIMARY\|BACKUP` | High | yes | Observed: `{"using":"backup","firstFrameMs":12}` after the next frame from it. Refused: "already on its backup"; "the backup has given no frame for 3.2 s"; "no backup set" |
| `INPUT RECONNECT` | `INPUT RECONNECT <input>\|ALL` | High | yes | Refused when healthy: "Cam 1 delivers 59.94 fps (12 ms old) — nothing to reconnect". Accepted → observed on the first frame |
| `INPUT PTZ` | `INPUT PTZ <input> PRESET <0-99> [SPEED <0-1>] \| STORE <0-99> \| ZOOM <0-1> \| PANTILT <-1..1> <-1..1> \| FOCUS AUTO\|<0-1>` | High | yes | The camera's own answer (`recv_ptz_*` bool): "sent", or refused: "Cam 1 is not a PTZ camera", "not connected", "OMT has no PTZ" |
| `SEND LIST` | `SEND LIST` | Low | no | = `STATE.videoio.sends` |
| `SEND ADD` | `SEND ADD NDI\|OMT <name> <tap> [<w>x<h>] [@<fps>] [UYVY\|UYVA\|P216\|PA16\|BGRA] [QUALITY LOW\|MEDIUM\|HIGH] [ALWAYS]` | Review | yes | `{"id":"sd…","fullName":"SHOWPC (Main out)","w":1920,"h":1080,"estMbps":200}`. Refused: protocol unavailable (words + link); "SHOWPC (Main out) is already on the network from 10.0.0.7"; tap target missing; **bandwidth guard**: "4 × OMT 1080p60 Medium ≈ 800 Mb/s > 700 Mb/s (70 % of eth0's 1 Gb/s)". Size rounded: effect says "1918 → 1916 (P216 needs a multiple of 4)" |
| `SEND ENABLE` | `SEND ENABLE <send> ON\|OFF` | High | yes | effect, or refused when unchanged. OFF with connections > 0 adds "2 receivers will lose this picture" to the effect. When armed (showsafety) it is refused with that reason |
| `SEND FAILOVER` | `SEND FAILOVER <send> <ndi full name>\|PEER <node>\|OFF` | Review | yes | `PEER` resolves the peer's send of the same id or name through its STATE. Refused: "OMT has no failover — receivers in PlayMap use INPUT BACKUP"; "peer B has no send named Main out" |
| `VIDEOIO STATUS` | `VIDEOIO STATUS` | Low | no | `STATE.videoio` minus the lists: runtimes, mdns, servers, bandwidth |
| `VIDEOIO CHECK` | `VIDEOIO CHECK` | Low | no | `[{"id":"videoio.ndi.runtime","light":"green","words":"NDI 6.3.2 at /usr/lib/libndi.so.6","fix":""},…]` (§4.7) |
| `VIDEOIO SET` | `VIDEOIO SET NDI ON\|OFF \| NDI-GROUPS <g> \| NDI-EXTRA-IPS <ip,…>\|OFF \| NDI-DISCOVERY <host,…>\|OFF \| OMT ON\|OFF \| OMT-DISCOVERY <host[:port]>\|OFF \| OMT-SERVE ON\|OFF \| MAX-SEND-MBPS <0-100000>` | Review | yes (NodeSettings) | effect plus `"applies":"now"\|"next start"`. Refused when unchanged |

Generic verbs keep working:
- `SET md… live.pacing "smooth"`, `SET sd… width 1280`, `REMOVE sd…`, `MEDIA REMOVE md…`.
- `SURFACE SOURCE sf… live:md…` and `OUTPUT SOURCE op… live:md…`.
- `ADD send {json}`.

`VIEW` gains `input:<id>`, `discover:<uri>` (S4) and `multiview:<send|auto>`.

**Routing.**
- `EngineCore.Router.cs` gains one arm: `_ when spec.Category == VerbCategory.Video => VideoVerb(spec, args, line, session, ct)`.
- `INPUT DISCOVER … WAIT`, `INPUT PTZ`, `INPUT SWITCH`, `INPUT RECONNECT` and `SEND FAILOVER PEER` join `OpsVerbs` (answered off the lane, with receipts as facts). Their project edits still go through the lane.
- Per-argument risk is used once the carry-over `RiskOf(line)` lands: `SEND ENABLE … ON` is Review; `OFF` stays High.

### 4.3 Contracts in Core (`src/PlayMap.Core/Media/LiveVideo.cs`, new)

```csharp
namespace PlayMap.Core.Media;

public enum LiveProtocol { Ffmpeg, Ndi, Omt, Spout, Syphon, DeckLink }

public static class LiveUri
{
    /// <summary>"ndi:FULL NAME" | "ndi://ip:port" | "omt:FULL NAME" | "omt://host:port" | "spout:NAME"; anything else with
    /// a scheme or a device path is Ffmpeg. A '*' is allowed only as the whole host part ("ndi:*(Cam 1)").</summary>
    public static bool TryParse(string? location, out LiveProtocol protocol, out string address, out bool byUrl);
    public static string Format(LiveProtocol protocol, string address);
    public static bool Matches(string pattern, string fullName);
    public static (string Host, string Name) Split(string fullName);   // "HOST (Name)"
}

public sealed record LiveEndpoint(LiveProtocol Protocol, string Uri, string FullName, string Host, string Name,
    string Address, long SeenUnixMs)
{
    public string PeerId { get; init; } = "";      // a heard Show Bus peer at that address
    public string PeerName { get; init; } = "";
    public string InLibrary { get; init; } = "";   // the media id that names it, or ""
}

public enum LiveState { Connecting, Ok, Stale, Lost, Absent, Failed }

public sealed record LiveHealth(LiveState State, string Words, int Width, int Height, double Fps, long AgeMs,
    double JitterMsP95, long Received, long Dropped, long Repeated, long Late, string Format, bool Alpha,
    TransferFunction Transfer, double Mbps, int Connections)
{
    public bool Ptz { get; init; }
    public string WebControl { get; init; } = "";
    public double DelayMs { get; init; }           // smooth/locked D
    public double ClockPpm { get; init; }
    public static readonly LiveHealth Opening = new(LiveState.Connecting, "connecting", 0, 0, 0, -1, 0, 0, 0, 0, 0, "", false, TransferFunction.Sdr, 0, 0);
}

public interface IPtz
{
    bool Supported { get; }
    bool Preset(int number, float speed);
    bool Store(int number);
    bool Zoom(float value);
    bool PanTilt(float pan, float tilt);
    bool Focus(float? value);                      // null = auto
}

/// <summary>A network picture: Acquire(mediaNs) takes the frame's leader instant (latest ignores it).</summary>
public interface ILiveSource : IVideoSource
{
    LiveProtocol Protocol { get; }
    LiveHealth Live { get; }
    IPtz? Ptz { get; }
    void SetTally(bool program, bool preview);
    void SetPacing(LivePacing pacing, LiveAnchor? anchor);   // render-side selection; anchor for Locked
}

public readonly record struct LiveAnchor(long OffsetNs, long DelayNs, long Seq);

public sealed record ProviderStatus(string State, string Version, string Path, string Words, string Link)
{
    public static readonly ProviderStatus Absent = new("absent", "", "", "", "");
}

public interface ILiveInputProvider : IDisposable
{
    LiveProtocol Protocol { get; }
    ProviderStatus Status { get; }
    IReadOnlyList<LiveEndpoint> Endpoints { get; }           // an immutable snapshot
    event Action? EndpointsChanged;
    void Refresh();
    /// <summary>Starts receiving; never blocks (frames arrive later). The caller owns the source.</summary>
    ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key);
}

public sealed record SendSpec(string Id, string Name, int Width, int Height, int FpsN, int FpsD, SendFormat Format,
    string Quality, string Groups, string Failover, bool Alpha);

public sealed record SendStats(long Sent, long Dropped, double EncodeMsP95, double Mbps, long BytesSent);

public interface ISendSink : IDisposable
{
    string FullName { get; }
    int Connections { get; }                                  // refreshed ≤ 4 Hz
    (bool Program, bool Preview) Tally { get; }
    SendStats Stats { get; }
    string Words { get; }                                     // "" while healthy
    /// <summary>Takes a packed frame; latest wins (an older unsent frame is released and counted). Never blocks.</summary>
    void Offer(SendFrame frame);
    /// <summary>Applies a changed spec: quality, failover and groups in place; a new name or format recreates the sender.</summary>
    void Update(SendSpec spec);
}

public interface ISendProvider : IDisposable
{
    LiveProtocol Protocol { get; }
    ProviderStatus Status { get; }
    /// <summary>Creates the sender (binds sockets): call off the render thread.</summary>
    ISendSink Create(SendSpec spec);
}

/// <summary>A packed picture owned by the producer until <see cref="Release"/> (any thread, once).</summary>
public abstract class SendFrame
{
    public nint Data { get; protected set; }
    public int Bytes { get; protected set; }
    public int Stride { get; protected set; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public SendFormat Format { get; protected set; }
    public long LeaderNs { get; protected set; }
    public int FpsN { get; protected set; }
    public int FpsD { get; protected set; }
    public TransferFunction Transfer { get; protected set; }
    public abstract void Release();
}
```

**Changes to `VideoFrames.cs`:**
- `PixelLayout` appends `Uyvy, Uyva, P216, Pa16`.
- `VideoFrameLease` gains `public virtual long ArrivalNs => 0;` and `public virtual long SenderTimestampNs => long.MinValue;`.
- `MediaFrameLease.ArrivalNs` becomes an `override`.

`YuvConstants.For` handles `P216`/`Pa16` (16 significant bits, `codeScale = 65535`, step 256) and `Uyvy`/`Uyva` (8 bits).

### 4.4 Engine seam (`src/PlayMap.Engine/VideoIoSeams.cs`, new; services init property)

```csharp
public interface IVideoIo
{
    ProviderStatus Ndi { get; }
    OmtStatus Omt { get; }                                   // + vmx cpu path, mdns, server
    IReadOnlyList<LiveEndpoint> Discovered { get; }
    IReadOnlyList<InputView> Inputs { get; }                 // per library input: id, uri, health, using, tally, pacing
    IReadOnlyList<SendView> Sends { get; }                   // per plan entry: id, fullName, state, stats, connections, tally
    BandwidthView Bandwidth { get; }
    void Refresh(LiveProtocol? only);
    Task<string> SwitchAsync(string inputId, bool backup, CancellationToken ct);
    Task<string> ReconnectAsync(string inputId, CancellationToken ct);
    Task<string> PtzAsync(string inputId, PtzCommand command, CancellationToken ct);
    IReadOnlyList<PreflightRow> Check();
    ISendSink? SinkFor(string sendId);                       // render thread: a volatile read
    IVideoSource? LiveHandle(MediaItem item);                // MediaBackend.Open routes here
    IScanCamera? CameraFor(CameraRig rig);                   // ScanRunner.OpenCamera composes with this
}
public sealed record PreflightRow(string Id, string Light, string Words, string Fix);
// EngineServices.VideoIo.cs: public IVideoIo? VideoIo { get; init; }
```

`VideoIoService : IEngineExtension, IVideoIo`:
- It is made in `EngineParts.AddExtensions` (its own file, `VideoIoParts.cs`).
- `Configure` sets `services with { VideoIo = this }` and installs `MediaBackend.Live = this`.
- `StartAsync` attaches to the project-change event: it rebuilds the warm pool and the send sinks on a worker and pushes STATE.

### 4.5 STATE (appended at the end of the root, after `decode` if that WP lands first)

```json
"videoio":{
 "ndi":{"state":"ready","version":"NDI SDK LINUX 6.3.2","path":"/usr/lib/libndi.so.6","words":"","link":"https://ndi.video",
        "groups":"","discovery":"","features":["failover","ptz","webcontrol"]},
 "omt":{"state":"ready","version":"1.0.0.20+playmap.1","vmx":"avx2","mdns":"none","mdnsWords":"avahi-daemon is not running — sources on other machines need a discovery server or an address",
        "server":"","serving":false,"words":""},
 "discoveredCount":7,
 "discovered":[{"uri":"ndi:STUDIO (Cam 1)","proto":"ndi","name":"Cam 1","host":"STUDIO","address":"10.0.0.5:5961",
                "seenMs":1760000000000,"peer":"","peerName":"","added":"md1a2b3c4d"}],
 "inputs":[{"id":"md1a2b3c4d","name":"Cam 1","uri":"ndi:STUDIO (Cam 1)","proto":"ndi","state":"ok","words":"",
            "w":1920,"h":1080,"fps":59.94,"ageMs":11,"jitterMsP95":2.8,"received":53110,"dropped":0,"repeated":3,"late":0,
            "format":"uyvy","alpha":false,"hdr":"sdr","pacing":"smooth","delayMs":25,"clockPpm":-3.1,"using":"primary",
            "tally":"program","ptz":true,"mbps":131.2,"warm":true,"deterministic":false,"onLoss":"fallback"}],
 "sends":[{"id":"sd5e6f7a8b","name":"Main out","proto":"ndi","fullName":"SHOWPC (Main out)","tap":"output:op_1","implicit":false,
           "w":1920,"h":1080,"fps":60,"format":"uyvy","state":"sending","words":"","enabled":true,"connections":2,
           "tally":{"program":true,"preview":false},"sent":120330,"dropped":4,"skippedUnwatched":0,"encodeMsP95":0.0,
           "mbps":182.0,"failover":"BACKUP-PC (Main out)"}],
 "bandwidth":{"nic":"eth0","linkMbps":1000,"inMbps":131.2,"outMbps":364.0,"limitMbps":700,"light":"green"}}
```

**Rules.**
- `discovered` is capped at 64 rows; `discoveredCount` is the true count, and `INPUT DISCOVER` lists all.
- An input that is not in use and is warm still reports health.
- `state` words for inputs: `connecting|ok|stale|lost|absent|failed`.
- `state` words for sends: `sending|idle` (unwatched and gated) `|off|starting|failed`.
- The output STATE row of a stream output appends `"stream":{"proto":"omt","fullName":"…","connections":1,"fps":59.8,"target":60}`. Its health light measures fps against the output's own `RefreshHz` (carry-over).
- The STATE `seq` moves when any of these change. Rates and ages are rounded so a push happens at most every 250 ms, the latest winning.

### 4.6 Context (Show Bus §6) and the Eye

**Items.**
- `videoio` (kind `videoio`):
  - red when the project names NDI inputs or sends and the runtime is absent;
  - amber when any input is stale, lost or on fallback, or any send is dropping more than 1 %;
  - green otherwise.
  - Headline example: "3 inputs ok, 2 sends to 3 receivers".
- `input:<id>` (kind `input`) for each input that is on air or not ok. Its words name the state and what it shows: "lost 4 s — showing Standby loop".
- `send:<id>` (kind `send`) with its connections and tally.

**Links.**
- `input:<id> → surface:<sf>|output:<op>` "feeds", for surfaces and outputs whose source resolves to the input.
- `output:<op> → send:<id>` "sends".
- `peer:<id> → input:<id>` "sends", when the input's address belongs to a heard peer: Patterns' NDI feed, in the shape of SHOWBUS-NODE item 4.

Patterns' Eye reads third-party contexts (SHOWBUS-NODE item 3). It draws them as a chain, `Patterns ─NDI→ Cam/Program → Wall surface → Left projector`, with each node's light.

**Show Bus hello.** One cap is added: `"video.io"`. That makes 15 of the 16 caps kept, noted in `ShowBus.cs`.

### 4.7 Checks (for showsafety's PREFLIGHT; `VIDEOIO CHECK` until then)

| Id | Green | Amber | Red |
|---|---|---|---|
| `videoio.ndi.runtime` | runtime loaded, version ≥ newest known (6.3.2, a table in code) | older than the newest known (the SDK's 30-day rule; §9) | project uses NDI and the runtime is absent or failed |
| `videoio.omt.vmx` | libvmx loaded, AVX2 | SSE4.2 path | missing |
| `videoio.mdns` | avahi, Bonjour or DnsApi working | none, but discovery servers are set | none, and inputs name hosts by name |
| `videoio.ip` | IPv6 dual stack | IPv4 only (patched path) | — |
| `videoio.ports` | OMT 6400–6600 bindable; 6399 when serving | fewer than the sends need | none |
| `videoio.names` | no send name seen from another address | — | a collision |
| `videoio.bandwidth` | estimate < 50 % of the link | < 70 % | ≥ the limit |
| `videoio.inputs` | every on-air input ok for the soak (60 s) at the expected size and rate | any repeat or drop beyond 0.5 % | any lost |
| `videoio.sends` | every enabled send has its sink; watched sends drop < 0.5 % | — | a sink failed |

### 4.8 NodeSettings (`src/PlayMap.Core/Bus/VideoIoSettings.cs`, new; one init property on `NodeSettings`)

```csharp
public sealed record VideoIoSettings
{
    public static readonly VideoIoSettings Default = new();
    public bool NdiEnabled { get; init; } = true;               // false: the runtime is never loaded
    public string NdiRuntimePath { get; init; } = "";
    public string NdiGroups { get; init; } = "";
    public ImmutableArray<string> NdiExtraIps { get; init; } = [];
    public string NdiDiscovery { get; init; } = "";             // comma list → ndi-config.v1.json (next start)
    public bool OmtEnabled { get; init; } = true;
    public string OmtDiscovery { get; init; } = "";             // "host[:6399]" (next start)
    public bool OmtServe { get; init; }                         // host a discovery server on :6399
    public int OmtPortStart { get; init; } = 6400;
    public int OmtPortEnd { get; init; } = 6600;
    public int MaxSendMbps { get; init; }                       // 0 = 70 % of the measured link
    public string Interface { get; init; } = "";                // "" = the default route's NIC
}
// NodeSettings (Pairing.cs hunk): public VideoIoSettings VideoIo { get; init; } = VideoIoSettings.Default;
```

### 4.9 Render-side records (`PlayMap.Render`)

- `SendRequest(string SendId, SendTapKind Kind, string TargetId, SourceRef? Source, int Width, int Height, SendFormat Format, SendFit Fit, int FpsN, int FpsD, SendReadbackRing Ring, MultiviewTiles? Tiles)`.
- `TextureFormat.Rgba16` (unorm, new) and `ReadFormat { Bgra8, Rgba8, Rgba16 }`.
- `ShaderLibrary` adds `"pack"` and extends `"yuv"` with uniforms `uPacked` (0 planar, 1 UYVY), `uAlpha` (0 none, 1 R8, 2 R16) and `uLastPair`.
- `Names` gains `"pack"`, so the warm-up compiles it.

---

## 5. Files

**New:**

| File | What |
|---|---|
| `src/PlayMap.Core/Model/VideoIoModel.cs` | §4.1 |
| `src/PlayMap.Core/Media/LiveVideo.cs` | §4.3 contracts, `LiveUri` |
| `src/PlayMap.Core/Bus/VideoIoSettings.cs` | §4.8 |
| `src/PlayMap.Video/PlayMap.Video.csproj` | edge (Core) |
| `src/PlayMap.Video/LiveFramePool.cs`, `LiveFrameLease.cs` | packed pool, lease (alloc-free) |
| `src/PlayMap.Video/TimestampJitterBuffer.cs` | §3.5 |
| `src/PlayMap.Video/SendWorker.cs` | latest-wins worker base, stats |
| `src/PlayMap.Video/DiscoveryCache.cs`, `BandwidthEstimator.cs`, `PackReference.cs`, `LiveRouter.cs`, `NicInfo.cs` | — |
| `src/PlayMap.Video.Omt/PlayMap.Video.Omt.csproj` + `runtimes/*/native/libvmx.*` (CI artefacts) | edge |
| `src/PlayMap.Video.Omt/OmtProvider.cs`, `OmtSource.cs`, `OmtSink.cs`, `OmtFinder.cs`, `OmtServerHost.cs`, `OmtSetup.cs` (settings, storage path, log callback, libvmx resolver) | — |
| `src/ThirdParty/libomtnet/**` (pinned `7d29031`, P1–P4 and P6; P5 is configuration), `PlayMap.ThirdParty.Omt.csproj`, `PATCHES.md`, `LICENSE.txt` | vendored |
| `build/libvmx/build.sh`, `build.cmd`, CI job `libvmx` (matrix linux-x64/arm64, win-x64, osx) | natives |
| `src/PlayMap.Video.Ndi/PlayMap.Video.Ndi.csproj` | edge |
| `src/PlayMap.Video.Ndi/NdiRuntime.cs` (search, load, exports, version), `NdiNative.cs` (structs, FourCCs incl. `FLTp`), `NdiProvider.cs`, `NdiFinder.cs`, `NdiSource.cs`, `NdiSink.cs`, `NdiPtz.cs`, `NdiColorInfo.cs`, `NdiConfigFile.cs` | — |
| `src/PlayMap.Render/Video/SendReadback.cs` | `SendReadbackRing`, `ReadbackSlot : SendFrame` |
| `src/PlayMap.Render/Video/PackPass.cs` | pack program driver |
| `src/PlayMap.Render/Video/Multiview.cs` | compositor |
| `src/PlayMap.Render/Engine/RenderEngine.Sends.cs` | taps (`RenderEngine` made `partial`) |
| `src/PlayMap.Engine/VideoIo/VideoIoService.cs`, `LiveInputPool.cs`, `VideoIoParts.cs`, `VideoIoChecks.cs` | engine side |
| `src/PlayMap.Engine/VideoIoSeams.cs`, `EngineServices.VideoIo.cs` | §4.4 |
| `src/PlayMap.Engine/EngineCore.VideoIo.cs` | verbs, `WriteVideoIoState`, context items |
| `src/PlayMap.Engine/Rendering/RenderHost.Sends.cs`, `MultiviewTiles.cs` | plan, due test, watch gate, collect |
| `src/PlayMap.Engine/Scanning/LiveCamera.cs` | `IScanCamera` over a live handle |
| `src/PlayMap.App/Views/SourceBrowser.axaml(.cs)`, `ViewModels/InputsViewModel.cs`, `ViewModels/SendsViewModel.cs` | desk |
| `tests/PlayMap.Video.Tests/` | pool, jitter, worker, discovery, PackReference, URI |
| `tests/PlayMap.Video.Omt.Tests/` | real loopback, server, tally, preview, no-avahi and IPv4 paths |
| `tests/PlayMap.Video.Ndi.Tests/` + `tests/native/fake_ndi.c` (MIT, ours; MSBuild target builds `libndi.so.6` with clang) | binding tests |
| `docs/VIDEO.md`, `THIRDPARTY-NOTICES.md` (rows) | papers |

**Touched (small hunks):**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Media/VideoFrames.cs` | 4 layouts; 2 virtual props |
| `src/PlayMap.Core/Model/MediaModel.cs`, `Project.cs`, `OutputModel.cs`, `StageModel.cs`, `Ids.cs`, `ProjectJson.cs` | init props, enum values, `AllIds`, prefix |
| `src/PlayMap.Core/Bus/Pairing.cs` | `NodeSettings.VideoIo` |
| `src/PlayMap.Core/Bus/ShowBus.cs` | cap `video.io` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | `VerbCategory.Video` + 15 rows |
| `src/PlayMap.Render/Video/VideoTextureSet.cs`, `YuvConversion.cs` | 4 layouts, alpha plane |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | `yuv` uniforms, `pack` program, `Names` |
| `src/PlayMap.Render/Gpu/GpuTypes.cs`, `GlDevice.Resources.cs` | `Rgba16`, `ReadFormat` |
| `src/PlayMap.Render/Engine/RenderEngine.cs` | `partial`; `OutputState.PreWarp/PreWarpRect` set in `DrawOutput` |
| `src/PlayMap.Render/Engine/SourceResolver.cs` | Live/Media route arm |
| `src/PlayMap.Media/Frames/MediaFrameLease.cs` | `override ArrivalNs` |
| `src/PlayMap.Engine/Host/MediaBackend.cs` | `Live` property; route in `Open` |
| `src/PlayMap.Engine/Rendering/EngineSources.cs` | leader-time acquire for `ILiveSource`, draw marks, `Route` for live loss, `LiveFor` accepts Ndi/Omt rigs |
| `src/PlayMap.Engine/Rendering/RenderHost.cs`, `RenderHost.Previews.cs`, `PreviewViews.cs` | call `Sends`; `InPreview` toggles; `ViewKind.Input/Discover/Multiview` |
| `src/PlayMap.Engine/Scanning/ScanCamera.cs`, `ScanRunner` wiring | `Gray8` UYVY/P216; `OptionsFor` no longer maps Ndi to FFmpeg |
| `src/PlayMap.Engine/EngineCore.Router.cs`, `EngineCore.Ops.cs`, `EngineCore.Edits.cs`, `StateWriter.cs`, `ContextBuilder.cs` | one arm; ops set; locate, add, validate; one call each |
| `src/PlayMap.App/...` (Play media list, Outputs drawer, Network drawer, About, `Link/FakeEngineLink.State.cs`, `State/StateSnapshot.cs`) | desk |
| `.github/workflows/build.yml` | clang for libvmx and the fake NDI; the licence scan |
| `docs/WIRE.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, `README.md` (NDI® notice) | papers |

---

## 6. Dependencies

**Work packages.**
- **zerocopy**: none blocking. This package does not use `GpuSurface`. Live leases report `DecodePath.Software` with `Device = "ndi"|"omt"` if `SourceHealth` gains those fields.
- **present**: the D3D11 path reuses the pack shaders through the ES dialect. `PresentClock` replaces the render-loop due test for send pacing when it lands. A 10-bit final (`Rgb10A2`) enables real 10-bit output-tap sends later.
- **texshare**: implements `ISendProvider`/`ILiveInputProvider` for Spout, using `VideoSend.Protocol = Spout` and `LiveUri` `spout:`.
- **showsafety**: provides `armed`. Locks: `INPUT ADD/BACKUP/FALLBACK/SWITCH`, `SEND ENABLE OFF` and `REMOVE` of a watched send are refused while armed, with the reason. Blind edits stay allowed. It also provides PREFLIGHT, which aggregates §4.7, and panic outcomes: inputs go to their fallback, and sends keep running and carry what the air shows.
- **fleet**: the anchor push carries `InputAnchor` (S2) and peer STATE for `SEND FAILOVER PEER` (S5). Each node's `videoio` context item feeds the health view.
- **companion**: INDEX gains `inputs` and `sends`; THUMB `input:` and `discover:`; EVENTS `videoio.discovered`. The module's presets are in §8.
- **control**: bindings may target the `INPUT …` and `SEND …` lines (nothing needed here).

**Third party.**

| What | Version | Licence | How |
|---|---|---|---|
| libomtnet | 1.0.0.20, `7d29031` (13 Oct 2026) | MIT | vendored source, P1–P4, upstream PRs |
| libvmx | `a1828cb` (23 Sep 2026) | MIT (sse2neon MIT) | built per RID by CI, shipped as a native asset |
| NDI SDK headers | 6.3 (DistroAV copy) | MIT for the header files only ("Copyright 2023-2026 Vizrt NDI AB") | layouts transcribed into `NdiNative.cs`, notice kept |
| NDI runtime | user-installed, 6.x (6.3.2 newest known) | NDI SDK licence (proprietary) | loaded at run time; **never shipped** |
| Patterns.Ndi | `bcdbf28` | MIT (same author) | ported ideas: runtime search, P216 handling. Not its `'FLTP'` constant [A5] |
| avahi-client (Linux) | system | LGPL, loaded at run time by libomtnet's P/Invoke | not shipped |

---

## 7. Verification here

### 7.1 Tested for real in this container

**OMT end to end on 127.0.0.1** with real libvmx built by clang:
- UYVY, UYVA, P216, PA16 and BGRA at 1080p, UYVY at 4K, and the 1/8 preview; tally, quality voting, and the Discovery Server.
- The no-avahi and IPv4-only paths. This container has both conditions, which is how P1–P3 were found [A1–A3].

**GL on llvmpipe** (Xvfb :99):
- unpack and pack shaders against `PackReference`, ±1 code;
- `SendReadbackRing` slot life;
- the multiview, byte-identical at the same leader instant;
- RGBA16 targets on GL 3.3 core.

**Engine with a headless render host:**
- PlayMap → OMT → PlayMap round trip (M7);
- one receiver per input;
- the watch gate;
- loss and backup with OMT senders started and stopped in-process;
- STATE, context and verbs;
- `LiveCamera` reading an OMT ramp.

**Pure:**
- the jitter buffer on synthetic traces: 60/60 with ±6 ms jitter, 50→60, 59.94→60 (drift 1000 ppm), bursts, gaps, missing timestamps, and two-node `locked`;
- URI parsing; validation; bandwidth arithmetic.

**Allocation:** 0 B per frame on the render thread and the receive threads after warm-up, measured with `GC.GetAllocatedBytesForCurrentThread`.

### 7.2 Simulated, and how

**NDI**, with `tests/native/fake_ndi.c`: about 450 lines, MIT, ours, built with clang by an MSBuild target into the test output as `libndi.so.6`. It exports the `NDIlib_*` functions PlayMap binds.
- A sender registry, so `find` returns in-process senders plus injected ones.
- `recv_capture_v3` returns copies of frames sent to that name, with `timestamp` = submit time in 100 ns and the frame's `p_metadata`.
- `send_send_video_async_v2` **checksums the buffer at submit and again at the next sync event**, and counts a violation if it changed (proves M6's lifetime rule).
- Tally, connections, failover and PTZ calls are recorded.
- Control exports: `fake_ndi_inject(name, fourcc, w, h, data, stride, ts100ns, meta)` for jittered external senders, and `fake_ndi_stats(…)`.
- Struct layouts are asserted against the clang-measured table [A4].

**Network jitter and drift:** synthetic `(s, a)` traces feed the jitter buffer. The OMT loopback can add delay through a test transport shim around `OmtSource`'s timestamps. The bytes are not changed.

**Two nodes:** two engines in one process with `ManualClock` and a shared fake sender.

**NIC speed:** `NicInfo` behind an interface. The container's virtual NIC reports a speed or −1, and the tests inject values.

### 7.3 Compile-only, or needs other hardware or software

- **The real NDI runtime.** Licence acceptance, and no access here: docs.ndi.video is blocked by the egress proxy, and there is no Linux runtime package. Needed for:
  - HX/HX2/HX3 receive, including the open question about the system FFmpeg ≥ 7 that DistroAV reports on Linux [V §5.3];
  - real tally with vMix or TriCaster;
  - PTZ on a real camera;
  - SpeedHQ CPU cost.

  Lab checklist in `docs/VIDEO.md`.
- **mDNS discovery** for OMT (Avahi with the daemon, Windows DnsApi, macOS dns_sd) and NDI. The avahi libraries are present here but no daemon runs, and none is installed.
- **IPv6 dual-stack OMT.** This container has no IPv6. The patched IPv4 path is the one tested.
- **Windows and macOS.** libvmx builds for win-x64 and osx are CI matrix jobs on those runners. The ES/ANGLE pack path is compile-only until the present WP.
- **Glass-to-glass latency.** The existing `LATENCY MEASURE` with a camera, in the lab.

---

## 8. Desk and Companion surface

### 8.1 Desk

**Play workspace → Media list: an "Inputs" section.** Each live tile has:
- a thumbnail (≤ 1 fps, from `VIEW input:<id>`);
- a protocol badge: `NDI®` or `OMT`;
- a state chip: green ok, amber stale, red lost, grey connecting;
- `1080p59.94`;
- a tally dot: red program, green preview;
- `↺ backup` when the backup is in use.

**The tile's right-click menu.** Each item is a `DeskAction` whose verb shows in its tooltip:
- *Show on surface ▸* (`SURFACE SOURCE <sf> live:<in>`);
- *Show on output ▸*;
- *Pacing ▸ Latest/Smooth/Locked* (`SET … live.pacing`);
- *Proxy*;
- *Keep connected*;
- *On loss ▸ Hold / Black / Fallback…* (`INPUT FALLBACK`);
- *Backup source…* (`INPUT BACKUP`);
- *Switch to backup / primary* (`INPUT SWITCH`);
- *Reconnect* (`INPUT RECONNECT`), enabled only when the input is not ok;
- *PTZ ▸ Preset 1…8 / Store…* (`INPUT PTZ`);
- *Use as scan camera* (`ADD camera {…source…}`);
- *Copy URI*;
- *Remove*.

**"Add input" opens the Source browser.**
- Discovered sources are grouped by protocol and by host.
- A host that is a Show Bus peer shows its peer name, for example "Patterns · Program".
- Hovering shows a live proxy (`VIEW discover:<uri>`, S4).
- There is a filter, "Add by address" (`omt://`, `ndi://ip:port`), and runtime lines:
  - "NDI® runtime 6.3.2 found";
  - or "NDI® runtime not installed — get it at ndi.link/NDIRedistV6 · ndi.video".
- A footer reads "NDI® is a registered trademark of Vizrt NDI AB".
- Double-click adds the source (`INPUT ADD`).
- Dragging a source onto a surface adds it and then sets the surface's source, as two lines in one undo step.

**Outputs drawer.**
- Each output tile's right-click has *Send this output ▸ NDI… / OMT… / Pre-warp…* (`SEND ADD`).
- A tile with sends shows a chip such as "→ NDI · 2 watching".
- *Add stream output ▸ NDI / OMT* adds an output of that kind.
- A **Sends** list shows, per send: the name, its full name, connections, Mb/s, a red tally lamp when a downstream switcher has it on program, dropped frames, and an enable switch (`SEND ENABLE`, High: a two-step confirm when it is watched).
- *Multiview* opens a preview: `VIEW mv SHOW multiview:auto`.

**Network drawer → Video IO.**
- Runtime versions and paths.
- mDNS state, with its words and fix.
- Discovery servers (`VIDEOIO SET …`).
- *Serve OMT discovery*.
- A NIC bar: in and out Mb/s against the link and the limit.
- *Check* (`VIDEOIO CHECK`), whose rows have fixes.

**About:** the NDI® trademark sentence and the THIRDPARTY notices.

### 8.2 Companion (with the companion WP)

**Variables.**
- Per input: `in_<id>_state`, `_fps`, `_age_ms`, `_res`, `_tally`, `_using`, `_name`.
- Per send: `sd_<id>_connections`, `_tally_program`, `_tally_preview`, `_mbps`, `_dropped`, `_state`.
- `videoio_ndi` (ready/absent), `videoio_out_mbps`.

**Feedbacks.**
- `input_state` (boolean, with a state option; colour presets green, amber, red).
- `input_on_air`.
- `input_using_backup`.
- `send_watched` (connections > 0).
- `send_on_program`: a downstream switcher took PlayMap's send.
- `videoio_bandwidth` (amber/red).
- `thumb` (value → `base64Image`) for `input:<id>`.

**Actions.**
- `input_switch` (hold-to-confirm group, High).
- `input_reconnect` (hold-to-confirm).
- `input_ptz_preset` (number, speed).
- `input_fallback`.
- `send_enable` (two presses on OFF).
- `route_input`: input × surface or output → `SURFACE SOURCE`/`OUTPUT SOURCE` (High, so confirm).

**Live auto-creation.**
- INDEX gains `inputs` (`{id,name,uri,proto,ptz,presets:[…]}`) and `sends`.
- Template preset groups are keyed by id: `in.<id>`, with a thumbnail key, a route key per surface and PTZ preset keys 1–8 when `ptz`; and `sd.<id>`, with an enable key and the tally lamp.
- A **"Discovered" group** is regenerated on the EVENTS push `videoio.discovered`. It holds one key per discovered source not yet in the library, and pressing it sends `INPUT ADD <uri>`.
- So a camera plugged into the network appears as a key within about a second, and is on air two presses later.

---

## 9. Risks and open questions

| Risk / question | Mitigation / how it is settled |
|---|---|
| **Upstream libomtnet bugs** abort the process (P1) or fail on IPv4-only hosts (P2, P3) | Patched and pinned. PRs upstream. The tests reproduce the abort conditions (no daemon, no IPv6) on every CI run. A re-vendor must keep `PATCHES.md` green |
| libomtnet allocates or blocks in places PlayMap does not control (`Thread.Sleep(1)` in `OMTClock`; per-frame objects) | Only on its own threads, never the render thread. Receive and send threads are measured. Budget: ≤ 2 KB per frame managed; a governor only if measured |
| **OMT is unicast**: each receiver costs a full stream (≈ 200 Mb/s at 1080p60 Medium) [V §4.5] | The bandwidth guard counts connections, `OnlyWhenWatched` applies, and preview proxies are used for monitors |
| OMT `Send` encodes synchronously, and the cost depends on the CPU. Here p50 is 4.6–8.6 ms at 1080p and 21 ms at 4K with both ends on 4 vCPUs [A2] | Encoding runs on a worker; latest-wins drops and counts. Preflight warns when encode p95 exceeds 80 % of the period. 4K60 OMT needs a real CPU (libvmx claims one i7 core [V §4.3]): a lab number |
| **NDI licence terms** (30-day "current SDK" rule, the Products definition, an appliance exclusion) [V §5.5] | No binaries ship. STATE shows the runtime version, and the check is amber when older than the newest known. A future PlayMap appliance needs the Advanced SDK: a decision recorded in ROADMAP. **Not legal advice; review before release** |
| `ndi-config.v1.json` key names and `NDI_CONFIG_DIR` were confirmed only from search extracts and the research [V §5.6] | Lab check with the real runtime. Until confirmed, per-call `p_groups` and `p_extra_ips` (header-verified) carry groups and extra IPs, and the discovery server is the only config-file feature |
| NDI receive `timestamp` equality across two receivers of one sender (needed for `locked`) | The header says it is SDK-generated at the moment the frame was submitted by the sending side [A6]. Lab-verify with two nodes. `locked` stays a Should |
| NDI HX decode on Linux needs a system FFmpeg ≥ 7 (DistroAV), alongside PlayMap's own FFmpeg 9 in the same process | Lab. If it clashes, NDI receive moves to a helper process: the `ILiveInputProvider` seam allows an out-of-process provider later |
| Readback on weak GPUs (PCIe) | Packing to UYVY halves the bytes [V §9.3]. The watch gate applies. One PBO ring per send with 4 slots. Per-send GPU time goes in STATE once timer queries are wired |
| Send names collide across nodes, e.g. a backup node with the same project | Default names include the node name for explicit sends ("Main out"; full name "HOST (Main out)" already differs by host). The check flags any same name from another address |
| Tally semantics: "on air" through a blacked-out output, or a frozen one | Program requires not-blackout. Frozen counts as program (the picture is on the wall) |
| Patterns' NDI audio uses FourCC `'FLTP'`, but the header defines `'FLTp'` [A5] | Not ported. Reported to the Patterns owner as a finding (read-only here) |

**Open questions.**
1. Should `smooth` be the default for inputs on surfaces, with `latest` for scan cameras? This spec keeps `latest` as the default, measured first.
2. Should a stream output default to `OnlyWhenWatched = false`? This spec says yes: an LED processor may connect late and must get frames.
3. Should the multiview carry audio meters once audio lands?

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Core spine: model, JSON, ids, validation, `LiveUri`, contracts, layouts (M1) | 5 |
| Render: unpack (4 layouts, alpha) + tests (M2) | 4 |
| Render: pack shaders, RGBA16, `SendReadbackRing` + tests (M3) | 6 |
| Render: multiview compositor + labels + tests (M10) | 5 |
| PlayMap.Video: pool, lease, worker, discovery cache, bandwidth, PackReference (M4) | 6 |
| OMT: vendoring, P1–P4, libvmx CI matrix, provider, server host, tests (M5) | 8 |
| NDI: loader, interop, finder, source, sink, colour info, config file, fake runtime in C, tests (M6) | 10 |
| Engine: service, warm pool, routing, EngineSources/SourceResolver hunks, RenderHost.Sends, stream outputs, LiveCamera (M7, M9, M11) | 10 |
| Verbs, STATE, context, checks, NodeSettings (M8) | 5 |
| Desk: inputs section, source browser, sends, network drawer, About, fake engine STATE | 7 |
| Licence test, papers (M12, M13) | 3 |
| **Must subtotal** | **69** |
| S1 smooth / S2 locked pacing (engine wiring; the algorithm is in M4) | 4 |
| S3 PTZ, S4 discovery thumbnails, S5 failover PEER, S6 OMT server, S7 peer annotation | 7 |
| **Total** | **80** |

---

## Appendix A. Verified here (13 Oct 2026, this container: 4 vCPU, no IPv6, no avahi-daemon)

**[A1] OMT from source** (`scratchpad/r2/bench/netvideo/`):
- libvmx `a1828cb` builds with the upstream clang command in 7.7 s and exports `VMX_Encode{UYVY,UYVA,P216,PA16,BGRA,…}`, `VMX_Decode*` and `VMX_DecodePreview*`.
- libomtnet `7d29031`, compiled into a net10.0 probe, failed three ways before patching:
  1. `avahi_service_browser_new: Assertion 'client' failed` aborted the process (no avahi-daemon). After guarding the browser, `avahi_entry_group_new: Assertion 'client' failed` aborted it again;
  2. `SocketException (97): Address family not supported by protocol` in the `OMTSend` constructor (no IPv6);
  3. `NotSupportedException: This protocol version is not supported` on every connect: v4-mapped addresses on an IPv4 socket.
- Patches P1–P3 (`omt-patches.diff`) fix all three.

**[A2] OMT loopback measurements** (`omtprobe.out`). Sender and receiver are in one process on 4 shared vCPUs, the synthetic ramp content compresses unusually well, and the figures are not representative of show hardware:

| Format, size | Received | `Send()` p50 / p95 ms | submit→receive p50 / p95 ms | Max luma error |
|---|---|---|---|---|
| UYVY 1080p | 180/180 | 4.6–8.6 / 8.9–18 | 10–14 / 19–26 | 1 code |
| P216 1080p | 120/120 | 5.3 / 15 | 12.6 / 24 | 2 (10-bit) |
| UYVA 1080p | 120/120 | 6.7 / 21 | 17 / 46 | 0 |
| PA16 1080p | 60/60 | 7.8 / 21 | 17 / 34 | 0 |
| UYVY 1080p preview | 120/120, delivered at **240×135** | 4.4 / 17 | 9 / 24 | — |
| UYVY 2160p | 60/60 | 21 / 52 | 45 / 297 | 1 |
| BGRA 1080p | 120/120 | 11 / 22 | 21 / 38 | 0 |

**[A3] OMT Discovery Server without mDNS** (`omtdisc.out`):
- An in-process `OMTDiscoveryServer` on :6399 was set as `DiscoveryServer` through `OMTSettings` in memory, without `Save()`.
- `GetAddresses()` listed `VM (Disc Probe)` 3 ms after the sender started.
- The upstream server prints "Connected: …" and "ADDED …" to stdout (P4).

**[A4] NDI 6.3 x64 layouts** (clang against the MIT headers; `ndilayout.out`):

| Struct | Size (B) | Offsets |
|---|---|---|
| `NDIlib_source_t` | 16 | — |
| `NDIlib_video_frame_v2_t` | 72 | `FourCC@8`, `frame_rate_N@12`, `picture_aspect_ratio@20`, `frame_format_type@24`, `timecode@32`, `p_data@40`, `line_stride@48`, `p_metadata@56`, `timestamp@64` |
| `NDIlib_audio_frame_v3_t` | 64 | — |
| `NDIlib_metadata_frame_t` | 24 | — |
| `NDIlib_tally_t` | 2 | — |
| `NDIlib_find_create_t` | 24 | — |
| `NDIlib_recv_create_v3_t` | 40 | `color_format@16`, `bandwidth@20`, `allow_video_fields@24`, `p_ndi_recv_name@32` |
| `NDIlib_send_create_t` | 24 | `clock_video@16`, `clock_audio@17` |

- FourCCs: UYVY `0x59565955`, UYVA `0x41565955`, P216 `0x36313250`, PA16 `0x36314150`, BGRA `0x41524742`, BGRX `0x58524742`, FLTp `0x70544C46`.
- Linux library name: `libndi.so.6`.

**[A5] Patterns audio FourCC.**
- `Patterns.Ndi/NdiInterop.cs:165` defines `FourCcFltp = 'F'|'L'<<8|'T'<<16|'P'<<24` (0x50544C46).
- The 6.3 header defines `NDIlib_FourCC_audio_type_FLTP = NDI_LIB_FOURCC('F','L','T','p')` (`structs.h:135`).
- PlayMap uses the header value.

**[A6] Read in the NDI 6.3 headers:**
- `recv_capture_v3` may be called on several threads at once.
- `send_send_video_async_v2`'s sync events are another async send, a sync send, a send with NULL, or destroy.
- `video_frame_v2_t.timestamp` is "a 100-nanosecond time that was the exact moment that the frame was submitted by the sending side and is generated by the SDK". `NDIlib_recv_timestamp_undefined = INT64_MAX`.
- FrameSync "transforms push sources into pull sources" and may return the same frame repeatedly.
- PTZ: `recv_ptz_is_supported`, `_zoom(0..1)`, `_pan_tilt(-1..1, -1..1)`, `_store_preset(0..99)`, `_recall_preset(n, speed 0..1)`, `_auto_focus`, `_focus(0..1)`.
- `send_set_failover(source)` and `send_get_source_name`.
- `recv_color_format_fastest` gives UYVY or UYVA. `_best` gives P216 or PA16 from native NDI.

**[A7] Engine code read on `main`** that this design plugs into:
- `EngineSources.LiveFor` accepts only `Device`/`Stream` rigs.
- `CaptureCamera.OptionsFor` maps `CameraSourceKind.Ndi` to an FFmpeg capture of the NDI name, which cannot open: FFmpeg has no NDI.
- `PixelPackRing` reads only BGRA and unmaps inside `TryCollect`.
- `RenderEngine.DrawOutput` computes the pre-warp `picture` and `src` rectangle but does not keep them.
- `SourceResolver.Resolve` routes only playlists.
- `MediaSources.Open` sends every `Stream` item to `CaptureSource`.
- The `ShowBus` caps list has 14 of 16.
