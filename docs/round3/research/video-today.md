# Video input path: today, the round-3 plan, and what a capture-card pass-through needs

Read from the tree at `main` 6f24310 (round 2 closed). `[I]` marks inference; everything else is cited by file:line.

There is no `docs/round3/design/video*.md`. `docs/round3/PLAN.md:274` says "Video and desk-ux are specified here in full", so
the round-3 video package is `PLAN.md` §3.11 (lines 1036-1085), together with `design/d3d12.md` §3 (copy queue, lines 79-88)
and `design/present.md` §6 (frames in flight and the just-in-time loop, lines 84-125).

---

## 1. Today's input path, by protocol

### 1.1 Routing

- `LiveUri.TryParse` (`src/PlayMap.Core/Media/LiveVideo.cs:17-58`) maps `ndi:`, `omt:`, `spout:` and `syphon:` to their protocol.
  Any other scheme of two or more letters, or a device path (`/dev/…`, `video=…`, lines 131-133), becomes `LiveProtocol.Ffmpeg`.
  `LiveProtocol.DeckLink` exists in the enum (line 7), but no scheme maps to it: `decklink:` is read as an FFmpeg URL (lines
  27-41).
- `MediaBackend.Open` (`src/PlayMap.Engine/Host/MediaBackend.cs:147-148`) sends NDI, OMT and Spout to `VideoIoHub.Open`.
  Everything else goes to `MediaSources.Open`, which opens a `Stream` item as `new CaptureSource(new CaptureOptions { Format =
  "", Device = item.Location })` (`src/PlayMap.Media/MediaLibrary.cs:196`). The capture source is returned untracked
  (`MediaBackend.cs:172-178`; only `Track` adds to `_open`, line 312-317).
- The hub skips FFmpeg locations everywhere (`VideoIoHub.cs:231-232`, `:421-423`). So do STATE's `videoio.inputs`
  (`IsLiveInput`, `EngineCore.VideoIo.cs:972-973`) and `INPUT ADD`, which answers "read by FFmpeg when it plays"
  (`EngineCore.VideoIo.cs:130,140`).

**Consequence: a capture card (`video=…`, `/dev/video…`) is outside the hub today.** It gets no loss policy, backup, tally,
health row in `videoio.inputs`, or `decode.sources` row. `INPUT DISCOVER` takes `NDI|OMT|SPOUT|ALL` only
(`VerbCatalog.cs:351`). `CaptureDevices.List` is called nowhere in `src/`, so no verb lists capture devices.

**Likely defect (not run here):** `Format = ""` means "let FFmpeg probe (a network url)" (`CaptureSource.cs:14-15`). Lines
168-169 then pass a null input format to `MediaInput.Open`, which calls `avformat_open_input(url, null)`
(`MediaInput.cs:113-143`). FFmpeg's probe does not select dshow or v4l2 for `video=Cam Link 4K` or `/dev/video0`, so a library
item or camera rig naming a device should fail to open. The scan path is different. `CaptureCamera.OptionsFor`
(`ScanCamera.cs:58`) leaves `Format` null, which selects the platform format (`CaptureSource.cs:168`, `CaptureDevices.PlatformFormat`
`:320-321`). The fault is only on the air path (`EngineSources.LiveFor` turns a rig into a `Stream` item, `EngineSources.cs:378-389`).
No test opens a device item through `MediaSources` (only `LiveContractTests.cs:21-22` parses the names). This line is unchanged
since 05c9587.

### 1.2 NDI / OMT / Spout (the hub)

- There is one receiver per input, and every consumer holds a non-owning `LiveHandle` (`LiveHandle.cs:139-196`). A backup or
  reconnect is a pointer swap at the next acquire (`LiveHandle.cs:66-71`, `169-175`). The hub tick runs 10 Hz on its own thread
  (`VideoIoHub.cs:44-47`, `774-790`). Tally uses a 250 ms window (`:51`).
- Each receiver has its own background thread at default priority: NDI `NdiProvider.cs:296` (`recv_capture_v3`, 50 ms,
  `:243,310`), OMT `OmtProvider.cs:128` (`Receive` 50 ms, `:115,158`), Spout `SpoutProvider.cs:231` (a 2 ms step, 250 ms while
  absent, `:236-243`). Only the render thread gets a priority (`RenderHost.cs:209`, AboveNormal).
- **Copy 1 (CPU):** the protocol buffer is copied into a `LiveFramePool` slot (lock-free, refcounted, 64-byte aligned, at least
  3 slots, a frame with every slot held dropped and counted; `LiveFramePool.cs:6-13,48-53,80-102`). OMT is a row memcpy
  (`OmtProvider.cs:206-219`). NDI also weaves fields (`NdiProvider.cs:354-435`).
- Spout today goes GPU→CPU→GPU. The sender texture is `CopyResource`d into one of 3 D3D11 staging textures
  (`D3D11SpoutGpu.cs:301-310`, `SpoutReceiver.cs:29,116`). It is mapped one step later with DO_NOT_WAIT into the pool
  (`SpoutProvider.cs:291-308`), then uploaded again by the renderer.
- Arrival is stamped on the node's monotonic clock after the copy (`OmtProvider.cs:221`; `Pool.Publish(sender ts, arrival)`,
  `:228`).

### 1.3 FFmpeg capture (`CaptureSource`)

- The open runs with every buffer off: `AVFMT_FLAG_NOBUFFER|FLUSH_PACKETS`, `max_analyze_duration = 0`
  (`MediaInput.cs:132-135,154-155`), and a LowDelay decoder (`CaptureSource.cs:184`). It has one thread
  (`CaptureSource.cs:65`) and a pool of 6 frames (`:36`). Arrival is stamped when `av_read_frame` returns (`:249`). The newest
  frame wins and the older one is released and counted dropped if never shown (`:295-306`).
- A hardware-decoded camera (MJPEG/H.264) is copied back to memory: `TransferIfHardware` (`:268`).
- **A CPU colour conversion on the capture thread for the formats cards deliver.** `FrameFormats.Plan` keeps only planar YUV,
  NV12, P010, GRAY8, RGBA and BGRA (`FrameFormats.cs:52-75`). Packed 4:2:2 (UYVY422/YUYV422) falls to the generic branch and is
  `sws_scale`d to planar `Yuv422P` (`:87-88`, `FrameWriter.cs:79-108`). Any >8-bit format (v210, P210) becomes `Yuv420P10`
  (`FrameFormats.cs:83-84`), so 4:2:2 chroma is halved vertically on the CPU. The GPU already has a UYVY/P216 unpack for network
  inputs (`PackedUnpack.cs:6-11`) that this path does not use.

### 1.4 To the GPU and the air (render thread, one GL context)

1. `SourceResolver.Video` acquires each source once per frame: `LastAcquired != _frame` (`SourceResolver.cs:398-408`). Every
   consumer in a frame draws the same generation. `EngineSources.Live` → `Acquire(0)` gives "the newest frame, whatever the time
   asked" (`EngineSources.cs:391-398`; `LiveSource.cs:12,131`; contract `LiveVideo.cs:174`).
2. `VideoTextureSet.Update` skips a generation it already holds (`VideoTextureSet.cs:95`). Packed layouts go to `PackedUnpack`
   (`:101-102,181-186`); others to per-plane upload (`:103-118`).
3. **Copy 2 (CPU):** a memcpy into a mapped pixel-unpack buffer from a 3-slot ring (`PixelBuffers.cs:26-47`, `MapUnpack` +
   `GlDevice.Copy`). **Copy 3 (GPU DMA):** `glTexSubImage2D` from that PBO (`GlDevice.Resources.cs:73-81`).
4. **Colour conversion on the GPU:** `video.unpack` (UYVY as RGBA8 w/2×h, P216 as R16 + RG16, BT.601/709/2020 coefficients) into
   an RGBA16F/RGBA16 target (`PackedUnpack.cs:74-97,120-121`), or the `yuv` pass for planar layouts.
5. Then canvases → surfaces → per-output mode → output stage (warp, masks, blend, trim, air gain) → delay ring → present
   (`ARCHITECTURE.md:158-175`). Sends tap after the output stage (`VIDEO.md` "A send").

The persistent mapped upload arena exists for HAP only (`GlUploadArena.cs:59`, `VideoTextureSet.cs:135-158`). Live planes never
use it.

### 1.5 The frame-time rule, and the carve-out that already exists

- Show time is sampled once per frame: leader now + time to the pacer's next vblank + the air latency, or the lock anchor's count
  (`RenderHost.cs:598-611`). Every generator, transition, effect and clip reads it.
- **The existing carve-out:** a live input is drawn at its arrival (the newest at the moment the render thread acquires it), not
  at show time (`EngineSources.cs:397`, `LiveSource.cs:12`, `CaptureSource.cs:30-31,89-90`). `LivePacing.Smooth/Locked` are
  accepted and shown as `latest` (`LiveSource.cs:118-119,154`; `docs/wire/videoio.md:125`).
- A live input is the backup mirror's one non-deterministic part (`docs/FLEET.md:120-121`; `COMPETITIVE.md:28,160`).
- Faster outputs are held back by the delay ring so every display lights the same picture together (`LatencyPlan.cs:9-13,
  35-58`; at most 6 frames, `:16`). On a mixed node this also holds a live picture back on the faster outputs.

### 1.6 Latency: what is measured, what is claimed

| What | Where | Measured? |
|---|---|---|
| `videoio.inputs[].ageMs` (now − newest arrival) | `LiveSource.cs:97-99`; `wire/videoio.md:107` | yes, the input's age only |
| `fps` (a smoothed period, ⅛ weight) | `LiveSource.cs:176-183` | yes |
| `received`, `dropped` (protocol + pool) | `LiveSource.cs:107-108` | yes |
| `repeated`, `late`, jitter p95, `delayMs`, `clockPpm` | `LiveHealth` fields (`LiveVideo.cs:150-158`) | **always 0**: `LiveSource.cs:107` passes 0 for jitter, repeated and late; the transfer is always `Sdr` (`:109`) |
| arrival → shown (the app's share) | the `CaptureSource` summary claims "the renderer measures … from there" (`CaptureSource.cs:31-33`) | **no reader**: `ArrivalNs` is read only by scans and align (`ScanRunner.cs:915-977`, `DisplayShooter.cs:51-131`) |
| glass-to-glass | `LATENCY MEASURE <camera>` (`VerbCatalog.cs:218`); round-3 `LATENCY STEP` (`stability.md` §5) | content steps only; neither measures capture → light |
| lab claim | "STATE shows `ok`, ~1 frame of age" (`VIDEO.md` lab item 1) | lab, unrun |

---

## 2. The round-3 video package as planned

`wp3-video` (`PLAN.md:1036-1085`, W3, about 23 h, depends on d3d12-core):

1. D3D12VA through FFmpeg 9.0.2 on the renderer's device. The render queue waits on each frame's fence on the GPU, never the
   CPU, and samples NV12/P010 planes in place. Copy-back where M12 says no (`:1038-1043`; M12 `:144`; L7 `:1655`).
2. `IUploadArena` over an upload heap, or a GPU upload heap where ReBAR and the build allow (`d3d12.md:52,119`). HAP blocks and
   CPU-decoded planes are copied on the copy queue *frames ahead*. "A late copy holds the last frame, with words, counted"
   (`PLAN.md:1044-1046`). AMD's guidance is "copy several frames early, double-buffered" (`d3d12.md:86-88`).
3. Spout through a D3D11 side device: the existing `D3D11SpoutGpu` opens the legacy handles and copies into an NT-handle texture
   shared with D3D12, synchronised by a shared fence, with no CPU copy (`PLAN.md:1047-1049`; M11 `:143`; L8 `:1656`). The
   fallback is staging, amber in preflight (`:1061`).
4. "NDI and OMT on the copy queue: … the live inputs' uploads as copy-queue work with fence values" (`:1050`).
5. Decode tiers from `CheckFeatureSupport(VIDEO_DECODE_SUPPORT)` (`:1052-1053`). Stretch: NVDEC through CUDA↔D3D12, and HAP
   encode on compute (`:1058-1059`).

`wp3-present` adds the following (`PLAN.md:971-978`; `present.md:84-125`):

- `Output.FramesInFlight`: auto is 1 where the output shows a live input, else 2.
- The just-in-time loop: wait on the pacing waitable → sample → "take the newest live frame at or before `LeaderNs`" → record →
  submit and present.
- `FrameBlock` stamps: sampled, recorded, submitted, presented and shown.

Platform pins the "critical pool (render, present, decode)" (`PLAN.md:478`). Its thread table has no row for the live receive
or capture threads (`platform.md:105-117`).

**What the plan does not cover:** capture cards. The only capture device in round 3 is the stability observer's `capture:`
source (`stability.md:74`; L12 `PLAN.md:1660`). DeckLink is ROADMAP M13 (`ROADMAP.md:251`). PLAN's own "M13" is a different
question, ReadyToRun (`PLAN.md:145`), and the shared label invites confusion. `VIDEO.md`'s "Next round" lists "capture cards".

### 2.1 Plan gaps to fix in the papers

- **Two clocks in one comparison.** `present.md:119` says "the newest live frame at or before `LeaderNs`". `LeaderNs` is leader
  show time at the *predicted display* instant (`present.md:101-102`). Arrival is the node's monotonic clock
  (`LiveFramePool.cs:29`). The comparison crosses clocks, and because `LeaderNs` lies a display latency in the future it always
  picks the newest frame. It should read "the newest live frame published by `SampledNs`" (monotonic), with `SampledNs −
  ArrivalNs` stamped as the input's age at draw.
- **FIF 1 is undone on mixed nodes.** FIF 1 for a live output enters the latency plan (`present.md:86-88`). With any 2-frame
  output on the node, `L_air` is the larger value, and the live output is delayed back by `d = round((L_air − L_o)/T)` = 1
  (`LatencyPlan.cs:39-49`). The gain is real only when every output carries a live input, as `present.md:88` admits. A
  pass-through has to state which outputs it leaves out of the plan.
- **The copy-queue arena's "frames ahead / a late copy holds the last frame" is a file-playback rule.** A live frame has no
  "ahead". If its upload has not completed at sample, holding the last frame adds a frame. For live, the receive thread should
  submit the copy at arrival with a fence value kept in the slot. The render picks the newest slot and makes the direct queue
  `Wait` on that value on the GPU (no CPU wait), falling back to the previous slot only when that copy has not even been
  submitted [I].
- `LiveHealth.Repeated/Late/JitterMsP95` are reserved and always 0. STATE's `repeated` key promises a count it does not make
  (`wire/videoio.md:107`).

---

## 3. What an ultra-fast capture-card pass-through needs here

### 3.1 A provider in the hub, not an FFmpeg URL

- A `capture` (or per-SDK `decklink:`) protocol that registers like Spout as an `ILiveInputProvider` (`LiveVideo.cs:195-205`).
  It inherits one receiver per input, the loss policy, the backup, tally, `videoio.inputs` health and preflight. `LiveUri` needs
  the scheme, and `LiveProtocol.DeckLink` exists already but is unreachable. Unknown schemes must stop being read as FFmpeg URLs
  for these names (fail closed).
- Windows 11 Home has DirectShow and Media Foundation. libavdevice's `dshow` is CPU-only. Media Foundation's source reader with
  a DXGI device manager gives D3D11 textures [I]; they would reach D3D12 the way the planned Spout bridge does (NT handle and a
  shared fence, `PLAN.md:1047-1049`). DeckLink and AJA are SDK paths, loaded at run time, never redistributed
  (`round2/research/videoio.md:23-24,553-597`). An FFmpeg built with `--enable-decklink` is nonfree and must not ship
  (`videoio.md:574-579`).
- Discovery: `INPUT DISCOVER CAPTURE` (`CaptureDevices.List` exists, `CaptureSource.cs:338-371`, unused). It must list each
  device's modes (`Formats`, `:376-415`).

### 3.2 Lowest-latency path, card DMA → swap chain (the rungs, best first) [I]

1. **GPUDirect for Video** (card DMA straight into VRAM). This needs a pro NVIDIA GPU and the card vendor's SDK
   (`vendor_features.md:72-76,292`). Tier 2: probe, never assume.
2. **Zero CPU copy.** The card DMAs into app-owned buffers (DeckLink's allocator provider, `videoio.md:584-586`; AJA DMA into
   app buffers). Those buffers are the upload heap itself: D3D12 heap over existing memory, `OpenExistingHeapFromAddress`
   [I, to be proven on WARP]. One copy-queue DMA to a VRAM texture follows, submitted on the capture thread at arrival with a
   fence. The direct queue waits on it on the GPU, then the UYVY/v210 unpack pass, which v210 needs as a new program.
3. **One CPU copy.** The SDK buffer is memcpy'd into a GPU upload heap (ReBAR, `d3d12.md:119`) or an upload heap, then the
   copy queue. This is today's pool shape, minus the render thread's second memcpy (`PixelBuffers.cs:36-44`).
4. **Today.** dshow → `sws_scale` → pool → PBO memcpy → `glTexSubImage2D` (§1.3-1.4): three CPU touches and a CPU colour
   convert.

Also needed:

- Keep packed 4:2:2 packed. Add UYVY422/YUYV422/v210/P210 to the plan as packed layouts unpacked on the GPU
  (`FrameFormats.cs:52-89`), and never `sws_scale` on the capture thread.
- The capture thread claims a critical role (MMCSS "Capture" exists, `windows_platform.md:31`). The platform table needs the row
  (`platform.md:105-117`).
- The output: FIF 1, the delay ring 0 for the pass-through output, and the pass-through output as the pacing output (one render
  loop paces all outputs off one waitable, `present.md:116`). Its picture still goes through the output stage (warp, blend),
  because mapping needs it.

### 3.3 Frame-rate matching and genlock

- With no common reference, the card and the GPU scanout drift. 59.94 in, 60.000 out gives one repeat every
  1/(60 − 59.94) ≈ 16.7 s [I]. 50 in, 60 out judders.
- Software options:
  - Measure the input's exact period: `_periodNs` already smooths it (`LiveSource.cs:176-183`), but STATE shows no rational.
  - Give a preflight row when a pass-through's rate is not the output's refresh rational. Refresh is an enumerated mode with
    exact rationals (`PLAN.md:957-961`), so offering `OUTPUT … REFRESH` at the input's rational is the honest fix.
  - Count every repeat and drop: fill `Repeated`/`Late` (`LiveSource.cs:107`).
- Hardware option (the owner's answer 2): house sync into the RTX PRO Sync BNC and into the card's reference input, so both
  clocks share one reference (`framelock_presentation.md:239,243`). DeckLink reports `bmdReferenceLocked` (`:233`). That
  status belongs in STATE as a fact, never assumed.
- VRR (present at arrival) is refused only in lock groups or into genlocked processors (`PLAN.md:52`). For one standalone
  pass-through output it is a measured option (`framelock_presentation.md:144,157`). It is a carve-out from vblank-driven
  frames (§4).

### 3.4 Measured, then governed

- Add a STATE fact per input: arrival → sampled → shown, p50/p95 (from `FrameBlock` stamps, `present.md:96-107`).
- Add a `LATENCY STEP VIA <input>` variant. The step goes through the card on a loop-through, and the observer or the card times
  it.
- CI: a fake capture provider on a manual clock (lavfi already stands in for a camera, `tests/PlayMap.Media.Tests/
  CaptureTests.cs:6-21`). Assert that no CPU wait is counted, that one copy is counted, and that an exact arrival→shown is stamped.
- Lab: a capture-card row (owner's answer 5: no hardware yet).

---

## 4. Rules a pass-through must keep, or carve out knowingly

**Keep:**

- **The air wins.** Blackout, freeze, panic, master, a safe look, a covered or held output, and a raw scan frame all apply to a
  pass-through output exactly as to any other (`VIDEO.md` "What the air shows"; `RenderHost.cs:612-624`). No path skips the
  output stage.
- **One acquire per source per frame**, so every surface and output in a frame draws one generation (`SourceResolver.cs:398-404`).
- **Never block the render thread.** Pool drops are counted (`LiveFramePool.cs:100`), and GPU waits are fence waits
  (`PLAN.md:1039-1040`).
- **Nothing allocated per frame once warm** (`LiveSource.cs:13`; `ZeroAllocTests` with a live input, `PLAN.md:1566`).
- **Attempts are not facts.** Pass-through on is accepted → applied (FIF 1, ring 0, pacing) → observed (measured arrival→shown
  in STATE). A verb that changes nothing is refused with the reason.
- **The desk has no private API.** Any pass-through switch is a catalogue verb with STATE, context and MCP, and a right-click
  entry.
- **Fail closed on the wire.** New schemes are explicit, and an SDK that is missing refuses with words (as NDI does, `VIDEO.md`
  "NDI").
- **SDKs are loaded at run time, never redistributed.** No GPL/LGPL code is copied, and there is no nonfree FFmpeg.
- **One writer.** Pass-through settings go through `ProjectStore` (output) or the node's settings writer (device facts).
- **Tally.** A pass-through drawn into the air is on program (`LiveHandle.cs:127-132`).

**Carve out, stated in the papers:**

- **Drawn at arrival, not at show time.** This already holds for every live input (`EngineSources.cs:397`). Everything layered
  on the pass-through (effects, transitions, generators) stays a function of `LeaderNs`.
- **"Every display lights the same picture together."** A pass-through output leaves the latency plan's delay ring
  (`LatencyPlan.cs:9-13`) and says so in STATE. Otherwise FIF 1 is undone on mixed nodes (§2.1).
- **The backup mirror.** A pass-through is non-deterministic between nodes (`FLEET.md:120-121`). The fix is one router feeding
  both, or a shared house reference.
- **Frames on the vblank grid.** Only if VRR or present-on-arrival is adopted for a standalone output, and only after a measured
  cause and a deterministic test (D10's spirit, `PLAN.md:52`).
- **The lock group.** A pass-through output in a lock group keeps the group's FIF and air latency. The fast lane is refused there
  with words, unless the card and the Sync card share house reference.
