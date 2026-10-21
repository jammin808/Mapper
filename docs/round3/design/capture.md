# Design: `wp3-capture` — capture cards passed through to the air, fast and measured

*How a capture card's picture will travel from the card to a projector's swap chain with no CPU copy where the card and the
Direct3D 12 path allow and at most one where they do not; what the latency budget is and how the node will measure it rather than
claim it; how an input's rate and phase will meet an output's refresh, measured, never called locked; which vendor SDKs it loads at
run time and on what terms; what CI proves without a card; and the lab line that proves the rest. Package section:
`PLAN.md` §3.21. Today's input path is `research/video-today.md`, cited **[vt §n]**; the card and SDK research is
`research/capture_cards.md`, cited **[cap §n]**.*

**The decision in one paragraph.** Capture cards join the video I/O hub as one more provider beside NDI, OMT and Spout, so they get a
receiver per input, the loss policy, a backup, tally, health, the context's input item and preflight for nothing. Each provider
declares the rung it reached on a ladder whose best rung, on Direct3D 12, has the card write each frame straight into memory PlayMap
owns and Direct3D 12 also maps (`OpenExistingHeapFromAddress`), so the CPU never touches a pixel; the capture thread submits one
copy-queue copy at the frame's arrival with a fence, and the render queue waits on that fence on the GPU, never the CPU. On OpenGL —
AUTO's path on every machine this round — the pass-through takes one CPU copy, an upload on the render thread and a CPU fence wait,
and STATE and preflight say so. Packed 4:2:2 stays packed to the GPU. An output marked pass-through runs one frame in flight, leaves
the latency plan's delay ring, samples late by the node's own measured render time and, outside a lock group, becomes the pacing
output. An input's phase against the output is measured and published; a card's reference status is never read as the input being
locked. Media Foundation is the universal provider (every card with a Windows driver, Windows 11 Home included) and DeckLink the
first vendor SDK; both are loaded from the user's own installation, never redistributed. STATE publishes each input's
arrival-to-shown time from the node's own stamps and, once a loop-through runs `LATENCY STEP` on the pass-through's output, its
glass-to-glass time; until then it says "unmeasured", never a number.

---

## 1. Today

- **Capture cards sit outside the hub.** NDI, OMT and Spout go to `VideoIoHub`; any other scheme, and any device name (`video=…`,
  `/dev/video…`), is read as an FFmpeg location and opened by `CaptureSource` through libavdevice (`dshow` on Windows). The hub, STATE's
  `videoio.inputs` and the decode list all skip those, so a card has no loss policy, backup, tally, health row or decode row.
  `LiveProtocol.DeckLink` exists and nothing reaches it; `CaptureDevices.List` exists and no verb calls it [vt §1.1].
- **A device item on the air path most likely does not open.** A library stream item opens with `Format = ""` (probe, for a network
  URL), and FFmpeg's probe picks neither `dshow` nor `v4l2` for a device name; the scan path, which leaves the format null and so takes
  the platform's, opens. No test opens a device item through `MediaSources` [vt §1.1]. Read from the code, not run.
- **Three CPU copies and a CPU colour conversion.** Packed 4:2:2 (UYVY, YUYV) is converted to planar on the capture thread by
  `sws_scale`, and 10-bit 4:2:2 (v210, P210) is reduced to 4:2:0 on the CPU; the frame is copied into a frame pool, then into a mapped
  pixel-unpack buffer on the render thread, then uploaded [vt §1.3, §1.4]. The GPU's packed unpack (`PackedUnpack`) is used only by the
  network inputs.
- **Latency is not measured.** STATE has an input's age, rate, received and dropped; `repeated`, `late` and jitter are always 0; the
  arrival stamp is read only by scan and align [vt §1.6].
- **Round 3's plan so far** uses a capture card only to watch an output (`OBSERVE`, `LATENCY STEP`); wp3-video moves NDI and OMT
  uploads to the copy queue; wp3-present gives an output that shows a live input one frame in flight [vt §2]. The roadmap's item for
  DeckLink is ROADMAP M13, not this plan's question M13 (ReadyToRun).

## 2. The bar

| Must | Proved by |
|---|---|
| A card's frame reaches the GPU with no CPU copy on rung 1, one on rung 2, and the rung is said | `CapturePathTests` count copies and name the rung; STATE `capture.rung`, `cpuCopies` |
| The render thread never waits on a capture: a copy not done is a GPU wait, a slot not free is a counted drop | `CapturePathTests` (no CPU wait counted); `CaptureSlotTests` |
| A frame that arrives between two samples is shown on the second vblank after its arrival at most, on the manual clock | `CapturePathTests` (counted in vblanks, never timed) |
| 4:2:2 stays 4:2:2 to the GPU, unpacked byte-exact | `PackedCaptureTests` (UYVY and v210 on GL and WARP) |
| A pass-through output adds no frame the path does not need | `PassThroughTests`: one frame in flight, no delay ring, the pacing output outside a lock group; `LateSampleTests` |
| The GL path says it is the slower one | `GlPassThroughTests`: one copy, the fence wait, `passThrough.path` "gl", the amber row |
| An input's phase is measured, never read from a card's reference status | `InputPhaseTests` |
| Every latency figure is measured, with its method, or "unmeasured" | `CaptureLatencyTests`; `PassThroughLatencyTests` |
| No SDK file is shipped; a missing runtime is said in words | `LicenceTests` on the installer; `CaptureProviderTests` |

**The target.** A mapped pass-through has a floor of about one input frame plus the render: a warp may read any input line to draw any
output line, so the render waits for the whole frame [cap §0, §1]. Shipping media servers publish 2 to 4 frames from capture to output
[cap §2]. PlayMap's target on `PRESENT PATH D3D12`, from the start of a frame's arrival at the card to the start of its scan-out, is
**about 2.5 output frames at p95 with a free-running source and about 1.5 with the source in phase through a shared house reference,
with the late sample (§7); about 3 and about 2 when the late sample is at 0** — §7's table adds up to these — a target to measure, not
a fact, and unmeasured until L25 runs (`PLAN.md` §7). On GL (AUTO this round) the figure is recorded beside it, with no target of its
own.

## 3. The path, from the card to the swap chain

```
card ──DMA──▶ slot k (PlayMap's memory, wrapped as a D3D12 heap)     rung 1: zero CPU copies
     └─(SDK buffer)──memcpy──▶ slot k (GPU upload heap)               rung 2: one CPU copy
capture thread: stamp arrival → ICaptureCopy: slot k → VRAM buffer, signal fence v_k → publish (k, v_k, arrival)
render thread (after the pacing output's waitable and the late sample): newest published slot → direct queue waits v_k on the GPU
            → unpack (UYVY: video.unpack; v210: video.v210) → canvas / surfaces → output stage → present
```

1. **The capture thread**, one per input, claims the `Capture` role (MMCSS's Capture task, on the critical CPU set; wp3-platform maps
   the role the spine adds). It receives the card's frame — DeckLink's `VideoInputFrameArrived`, Media Foundation's asynchronous source
   reader callback, AJA's interrupt wait and DMA, Magewell's notification — and stamps its arrival: the card's hardware timestamp mapped
   to the machine's monotonic clock where the SDK gives one (DeckLink's `GetHardwareReferenceTimestamp`, Magewell's buffered stamp), else
   the callback's own reading.
2. **The slot ring.** Four slots per input of memory PlayMap allocates (`VirtualAlloc`, aligned for the card and the heap), wrapped once
   as a Direct3D 12 heap with a placed buffer per slot. On rung 1 the card writes into the slot itself — DeckLink through its capture
   allocator provider, AJA as application-supplied DMA buffers, Magewell through its capture-to-address call [cap §8]. On rung 2 the card
   writes into its own buffer and the capture thread copies it once, with streaming stores, into the slot, a GPU upload heap where
   Resizable BAR and the Agility runtime allow (VRAM the CPU writes), else an upload heap.
3. **The copy at arrival.** The capture thread submits, through the capture contract in `Core/Media/LiveVideo.cs` (`ICaptureCopy`,
   implemented in `Render.D3D12/Capture` and on GL in `Render/Video`, so Media references Core alone), the slot's bytes into a VRAM
   buffer — a buffer-to-buffer copy, so no texture copy's 256-byte row-pitch rule applies to 720p or 2K v210 rows (3 456 and 5 504
   bytes) — signals the input's fence with the slot's value and publishes the slot (its sequence, fence value and arrival) with one
   volatile write. A live frame has no "frames ahead": the copy goes at arrival, not when a render asks (wp3-video's file-playback
   arena keeps its own rule).
4. **The render's take.** At its sample instant the render takes the newest slot published by then, on the machine's monotonic clock,
   makes the direct queue wait on that slot's fence value on the GPU, and stamps the input's age at draw (sample less arrival). Every
   surface and output in a frame draws that one acquire. A slot is not written again until the fence of the last frame that drew it has
   completed; with every slot held, the capture thread drops the frame and counts it — it never waits, and neither does the render.
5. **Unpack.** UYVY and YUYV through the existing `video.unpack`; v210 (10-bit 4:2:2, six pixels in sixteen bytes) through a new program,
   `video.v210`, in HLSL and GLSL, read from the buffer (a byte-address read in HLSL, a buffer texture in GLSL); P210 as two planes
   through `yuv`. Nothing is converted on the CPU and 4:2:2's chroma is kept.
6. **The output.** The picture is a source like any: canvases, surfaces, the output stage (warp, masks, blend, trims, the air's gain) and
   the swap chain. A pass-through output (§5) presents it on the next refresh.

**On the GL reference backend** — AUTO's path on every machine this round — the pass-through has a slower shape, and says so:

- the slot is a persistent-mapped pixel-unpack buffer, written by the capture thread at arrival: rung 2's one CPU copy, whatever the
  card offers (GL has no wrapped heap);
- the upload is a `glTexSubImage2D` from that buffer on the render thread at the sample (GL has no copy queue), its time inside the
  render's;
- one frame in flight: while a pass-through is on, the render thread waits, before it samples, on the GL fence of the frame before
  the last (`glClientWaitSync`, bounded at one period, a timeout counted), so the driver queues at most one frame — GL's stand-in for
  the frame-latency waitable, which only the Direct3D 12 swap chain has (`present.md` §6);
- Media Foundation hands system-memory samples there (no D3D11 texture to share): rung 2;
- STATE `outputs[].passThrough.path` "gl" and `capture.rung` say it, and preflight is amber: "op_3's pass-through runs on OpenGL: one
  CPU copy, the upload on the render thread and a CPU fence wait — the fast path is `PRESENT PATH D3D12` (proven on WARP only this
  round)".

## 4. Providers and the rungs

| Rung | What moves the frame | CPU copies | Who reaches it |
|---|---|---|---|
| 1 `card-dma` | the card writes into PlayMap's slot, wrapped as a D3D12 heap; one copy-queue copy to VRAM | 0 | DeckLink (allocator provider), AJA (app buffers), Magewell (capture to address) on Direct3D 12 — once M16 (WARP) and L25 (a real card) say the wrapped heap takes a card's DMA |
| 2 `upload-copy` | one CPU copy into a GPU upload heap, an upload heap or, on GL, a mapped buffer; one GPU copy | 1 | every vendor SDK where rung 1 is refused; Media Foundation where its samples are system memory; every input on GL |
| 3 `mf-shared` | Media Foundation's D3D11 texture copied on the GPU into a texture shared by NT handle with a shared fence (wp3-video's Spout bridge) | PlayMap's 0; Media Foundation's own unknown | a Media Foundation source whose samples are `IMFDXGIBuffer`s, on Direct3D 12 |
| 4 `cpu` | today's FFmpeg `dshow` path into the frame pool | 3 and a conversion | a `video=` library item or a `capture:dshow/…` source, kept for what nothing else opens, said amber |

Media Foundation's rung is per input, by what its samples are: with a D3D11 device manager set, a source that hands `IMFDXGIBuffer`s
takes rung 3, one that hands system memory (most USB devices) rung 2. A UVC device that sends MJPEG is decoded by Media Foundation's
own MJPEG decoder, named in STATE (`capture.decode: "mf-mjpeg"`), its time inside the input's figure. Media Foundation's own upload of a
system-memory sample, where it makes one, is invisible to PlayMap's counters, so `cpuCopies` on rung 3 reads "PlayMap 0; Media
Foundation's own unknown", never 0 alone.

| Provider | Scheme (the spine's `CaptureUri`) | Loaded from | Order |
|---|---|---|---|
| Media Foundation | `capture:mf/<device>` | Windows (every edition) | must |
| Blackmagic DeckLink | `capture:decklink/<device>[/<input>]` | the user's Desktop Video installation (COM) | must |
| AJA | `capture:aja/<device>` | a small C-interface shim built from AJA's MIT library, the driver the user's | stretch |
| Magewell | `capture:magewell/<device>` | `LibMWCapture.dll` from System32, where Magewell's installer puts it | stretch |
| FFmpeg avdevice | `capture:dshow/<device>` (Windows), `capture:v4l2/<device>` (Linux); a library item's `video=<name>` | the shipped LGPL FFmpeg | kept, rung 4 |

The grammar is the spine's (`Core/Media/CaptureUri.cs`), the one wp3-stability's observer already parses for its `dshow` and `v4l2`
sources in W2, so a stored observer source means the same before and after capture merges. It fails closed: an unknown provider, user
information, a query or a device that is not there is refused with words, and a `capture:` URI is never read as an FFmpeg location
but through its provider. A provider whose runtime is absent answers "DeckLink's driver (Desktop Video) is not installed on this
machine" and the input is amber; nothing tries another path in silence. `INPUT DISCOVER CAPTURE` lists each provider's devices, their
inputs, every mode (size, rate as a rational, pixel format) and whether a reference input is present. A provider says its rung per input,
and a rung below what the card offers is a grey fact with its reason.

**Not this round:** NVIDIA GPUDirect for Video and AMD DirectGMA. They save about a millisecond per 4K frame, not frames; they are for
professional GPUs only, gated by the vendor, written for OpenGL, D3D11 or CUDA with no Direct3D 12 entry point found, and NVIDIA's
supported list stops at the RTX A-series [cap §5]. Measured, then governed: they are revisited only if a loop-through measurement shows
the copy is the bottleneck. Deltacast, Datapath and Bluefish444 wait until an owner asks and the vendor's terms are known (Media
Foundation reaches their cards meanwhile).

## 5. The pass-through output

`OUTPUT PASSTHROUGH <output> ON|OFF` marks an output that shows a live input as fast as it can (`Output.PassThrough`, the spine's field):

- one frame in flight (its swap chain's latency 1, wp3-present's waitable; on GL the fence wait of §3);
- out of the latency plan's delay ring — today the plan holds every faster output back so every display lights the same picture
  together, which on a node with any two-frame output would delay the pass-through by a frame and undo the gain [vt §2.1];
- the late sample (§7) while it is on;
- the pacing output while it is on, so the render samples just after the waitable that matters to the live picture — unless the
  output is in a lock group: there the group's pacing output keeps the role, since under a hardware lock every member flips on the
  same refresh and any member's waitable marks the same instant, and under the software lock the words say what the pass-through gives
  up ("op_3 samples on op_1's waitable in group Stage: up to its phase, +6.2 ms, later than its own").

`ON` when on and `OFF` when off are refused with the reason. STATE `outputs[].passThrough` says it is on, the input, its frames in
flight, that it has left the delay ring, its path (`d3d12` or `gl`), its late sample and its misses; preflight says, amber, that the
output leads the others by its delay ("op_3 leads the wall by 1 frame: a pass-through").

## 6. Frame rate, phase and the frame lock

- **An input's phase, measured.** The capture thread's arrival stamps against the pacing output's vblanks give each input's phase
  (the arrival's offset into the refresh, its jitter and its drift in ppm), published as `videoio.inputs[].capture.phase` with the words
  "in phase with op_1: a shared reference" when it holds within ±0.5 ms for 600 frames at a rate equal to the output's refresh, else
  "drifting 3.2 ppm against op_1: one repeat every 5 min 12 s". It is never called locked: a capture input is in phase only because its
  source shares the house reference with the outputs (house sync into the source and into RTX PRO Sync, wp3-framelock's tier), and the
  phase is the evidence of that, measured on every frame [cap §9].
- **A card's reference status** (DeckLink's genlock status: `locked`, `unlocked`, `absent`) is published as a fact about that card's
  outputs, `videoio.inputs[].capture.reference`, which next round's genlocked SDI output reads; it says nothing about the input's phase.
  The card's reference timing offset, which places an output frame against house sync, is a stretch setting for that case.
- **The honest software lock everywhere else.** The render takes the newest frame complete by its sample; repeats and drops are counted
  (`repeated`, `dropped`, `late` filled — today they read 0); the input's rate is published as a rational from the card's mode or, where
  the provider gives none, from its measured period. A pass-through whose input rate is not its output's refresh rational is amber in
  preflight with both rationals and the cost ("59.94 in, 60.00 out: one repeated frame every 16.7 s"), and the words offer the output's
  refresh at the input's exact rate, an enumerated mode (wp3-present's rule, a request for another refused naming both); one whose
  input drifts at an equal rate is amber with the drift's words.
- **Never** variable refresh on a pass-through this round: D10 refuses VRR in lock groups, and a standalone output presenting at
  arrival is a measured option for later [cap §9]. DeckLink outputs' ±127 ppm trim belongs to an SDI output, next round.

## 7. The latency budget, the late sample, and how it is measured

| Stage (60 Hz) | Budget | Measured by |
|---|---|---|
| The frame arrives on the wire | 1 frame (16.7 ms) | the card's timestamps |
| The card's buffering before the host sees it | 0 to 1 frame, by the card's mode (0 in a low-latency mode) | the hardware timestamp against the callback's reading |
| Rung 2's CPU copy (rungs 1 and 3: none) | about 2 ms for a 4K v210 frame (0.12 frame) | the capture thread's stamps |
| The copy to VRAM | ≤ 1.5 ms for a 4K v210 frame | the copy queue's timestamps |
| The wait for the sample | 0 to 1 frame with a free-running source (p95 about 0.95), about 0 in phase | sampled less arrival |
| Sample to vblank | about 1 frame sampled at the waitable; with the late sample the render's p99 plus the margin (about 0.35 frame for a 4 ms render) | the frame block's sampled and shown stamps |
| Unpack, map, output stage | ≤ 4 ms, inside the row above | GPU time per pass (wp3-d3d12-passes-2) |
| The present queue | 0 (one frame in flight) | presented to shown (the flip statistics) |
| Scan-out to the line that shows it | up to 1 frame (the target counts to its start) | — |

Summed at p95 on rung 1: 1 + 0.09 + 0.95 + 0.35 ≈ 2.4 frames with a free-running source and the late sample, 1 + 0.09 + 0.35 ≈ 1.45 in
phase; without the late sample about 3 and 2; rung 2 adds 0.12. These are §2's targets.

**The late sample** (wp3-capture item 6). Sampling straight after the waitable leaves a frame waiting about one refresh from its
sample to the vblank it scans out on. While a pass-through is on, the render instead waits — on the platform's high-resolution timer,
after the pacing output's waitable — until the predicted vblank less the p99 of the last 600 frames' sample-to-GPU-done time and a
margin of 1 ms and 10 % of the period, clamped to the period, then samples [cap §11]. The GPU-done time is the node's own, measured on
every frame (Direct3D 12 timestamp queries; GL timer queries, read a frame late), so the governor has its measured cause on the node
that runs it. A present that misses its vblank sets the offset to 0 for 10 s, counted and said ("late sample off for 10 s: op_3
missed vblank 91 204"); STATE `outputs[].passThrough.lateSampleMs` and `.lateMisses`. The whole frame samples late, so every output of
the node gives up the same margin while a pass-through is on, and the words say so. `LateSampleTests` hold it on the manual clock.

**In the node, every frame.** The frame block's stamps (wp3-present: sampled, recorded, submitted, presented, shown) plus the input's
arrival give `videoio.inputs[].latency.arrivalToShownP50Ms` and `P95Ms` with `method: "in-node"`: the time from the card handing the
frame over to the display's flip, which excludes the wire before the card and the projector after the flip, and says so.

**Glass to glass, by loop-through.** The pass-through's output is cabled back into a capture input set as its observer
(`OBSERVE <output> FROM capture:decklink/2`, wp3-stability's verb, the same grammar); `LATENCY STEP <output> VIA OBSERVER`
(wp3-stability's) steps the watermark and gives, per step, the time from the step frame's sample to its arrival back at the observing
card — the render, the present queue, the scan-out, the cable and the card's buffering. For a pass-through output the node adds, per
step frame, the age at draw of the pass-through's own input frame in that same frame (the frame block's stamp: sample less arrival), so
each step's figure runs from the input frame's arrival at its card to the output frame's arrival back at a card; the return leg's card
buffering stands in for the input's own, and the display's delay is outside both and said. Published as `latency.glassToGlass` (p50,
p95, p99) with `method: "loop-through via <input> + age at draw"`; `null`, and the words "unmeasured: no loop-through has run on this
node", until it has run. No row of capture's own: `LATENCY STEP`'s per-step instants reach capture through one hunk in
`Observe/LatencyStep.cs`, and `LATENCY STEP` keeps its refusals (armed, and neither an observer nor a camera). A photodiode adds the
projector's own delay, which no in-node method sees; L25 records it beside the loop.

## 8. What it keeps, and what it carves out

**Kept.** The air wins: blackout, freeze, panic, the master, a safe look, a held or covered output and a raw scan frame apply to a
pass-through exactly as to any output, and nothing skips the output stage. One acquire per source per frame. The render thread never
blocks on a capture; nothing allocates once warm (`ZeroAllocTests` with a capture input). Attempts are not facts: `OUTPUT PASSTHROUGH`
is accepted, applied (frames in flight, the ring, the pacing, the late sample) and then observed (the measured arrival-to-shown in
STATE). The desk has no private API: every switch is a catalogue row with STATE, the context, MCP and a right-click entry. Fail closed.
One writer: the pass-through is the project's (`ProjectStore`), the device facts the node's settings writer's. SDKs loaded at run time,
never redistributed; no nonfree FFmpeg.

**Carved out, and said.** A live input is drawn at its arrival, not at show time — already true of every live input; everything layered
on it (effects, transitions, generators) stays a function of the leader's time at the frame's vblank, the late sample changing when the
frame is sampled and not the instant it is drawn for. A pass-through output leaves "every display lights the same picture together". A
pass-through is the backup mirror's non-deterministic part, as every live input is (a router feeding both nodes, or a shared house
reference, closes it). wp3-present's loop takes "the newest live frame at or before `LeaderNs`" — a comparison of two clocks that always
picks the newest; this design states it as the newest published by the sample instant on the monotonic clock, with the age stamped
(`present.md` §6 says the same).

## 9. SDKs and their terms

| Path | Free to use | Shipped by PlayMap | Loaded how | Agreement needed |
|---|---|---|---|---|
| Media Foundation | yes (Windows) | nothing | the OS | no |
| Blackmagic DeckLink | yes (the SDK's EULA) | interop declarations translated from the SDK's Include IDL, which carries its own permissive notice (kept in `THIRD-PARTY-NOTICES.md`); no sample code (internal use only); named only as "compatible with Blackmagic Design DeckLink" | COM, from the user's Desktop Video installation | no |
| AJA NTV2 | yes (MIT) | a C-interface shim built from the MIT sources, with the MIT notice | the shim; the driver the user's | no |
| Magewell MWCapture | yes (free SDK) | nothing: runtime files are never copied into a product and never linked statically | `LibMWCapture.dll` from System32 | no, for run-time loading |
| FFmpeg with DeckLink | — | never: such a build is nonfree | — | — |
| NVIDIA GPUDirect for Video | on application | not this round | — | yes (NVIDIA and the card's vendor) |

Each provider's notice sits under capture's heading of `THIRD-PARTY-NOTICES.md`; `LicenceTests` (the integration pass) assert that
the installer holds no DeckLink or Magewell binary and no AJA driver or SDK binary — the shim compiled from AJA's MIT sources, if
the stretch lands, is allowed with its notice.

## 10. Verbs, STATE, preflight

| Row | Syntax | Risk / class |
|---|---|---|
| `INPUT DISCOVER` (extended) | `INPUT DISCOVER [NDI\|OMT\|SPOUT\|CAPTURE\|ALL] [WAIT <0-5000 ms>]` | Low / Query |
| `INPUT ADD` (extended) | `INPUT ADD <uri> [<name>] [PROXY] [COLD] [MODE <w>x<h>@<rate>] [FORMAT UYVY\|V210\|NV12\|AUTO]` — `capture:` URIs | as `INPUT ADD` |
| `OUTPUT PASSTHROUGH` | `OUTPUT PASSTHROUGH <output> ON\|OFF` | Review / Setup |

Glass to glass is `LATENCY STEP`'s (wp3-stability's row, High / Heavy), on the pass-through output with a loop-through observer.

STATE: `videoio.inputs[]` gains `capture` (`provider`, `device`, `mode`, `rate`, `format`, `decode`, `rung`, `cpuCopies`, `gpuWaits`,
`phase`, `reference`) and `latency` (`arrivalToShownP50Ms`, `arrivalToShownP95Ms`, `method`, `glassToGlass`: null or `{p50Ms, p95Ms,
p99Ms, method, at}`), and its `repeated` and `late` are filled; `outputs[].passThrough` (`on`, `input`, `framesInFlight`, `path`,
`lateSampleMs`, `lateMisses`). A capture input is a hub input, so the context carries it as every hub input is carried
(`EngineCore.VideoIo.cs`'s `input` items: an input on air, not ok or on its stand-in), and an assistant reads it and proposes
`INPUT ADD`, `SURFACE SOURCE <sf> live:<in>` and `OUTPUT PASSTHROUGH` like any line, queued while armed.

Preflight (section `videoio`): a capture runtime missing (amber, naming what to install); an input's rate not its pass-through output's
refresh (amber, both rationals); a pass-through's input drifting against its output at an equal rate (amber, the drift); a pass-through
on GL (amber, the fast path named); rung 4 in use (amber: "this input is converted on the CPU"); a capture card whose driver did not
load under Memory Integrity (amber, the device named, from the provider's discovery).

## 11. What CI proves without a card

| Test | Job | Holds |
|---|---|---|
| `CaptureProviderTests` | both | a fake provider on a manual clock (a test double of the provider seam, as NDI's fake runtime is): discovery's devices and modes; a missing runtime's words; `capture:` URIs parsed by the spine's grammar, an unknown provider refused, never handed to FFmpeg as a location |
| `CapturePathTests` | both (GL); Windows (WARP) | frames from the fake provider at 60 Hz: rung 2 counts one CPU copy and rung 1 none; no CPU wait counted on the render thread; the stamps exact (arrival, submitted, sampled, shown on the manual clock); every surface in a frame draws one acquire; every frame shown on the second vblank after its arrival at most, counted in vblanks |
| `CaptureSlotTests` | both | every slot held by in-flight frames → the next frame dropped and counted, no wait; a slot reused only after its last drawing frame's fence |
| `WrappedHeapTests` (M16) | Windows (WARP) | `OpenExistingHeapFromAddress` over a `VirtualAlloc` ring, placed buffers on it, a copy-queue buffer copy and a compute unpack from it: UYVY and v210 frames byte-exact against the CPU reference at 720p, 1080p, 2K DCI and 4K widths; or, where WARP refuses the heap, the refusal's words recorded and rung 2 the best rung on every card until L25 |
| `PackedCaptureTests` | both | UYVY, YUYV, v210 and P210 unpacked on the GPU byte-exact against the reference on GL and on WARP; no `sws_scale` on the capture thread |
| `MfProviderTests` (M19) | Windows | a Media Foundation virtual camera opened by the provider where the runner allows one; else the provider against a fake source-reader seam: `IMFDXGIBuffer` samples → rung 3, system-memory samples → rung 2, an MJPEG type → `decode: "mf-mjpeg"`, `cpuCopies`' words on rung 3; the real MF path is L25's |
| `DeckLinkInteropTests` | both | the interop declarations' layouts; fake COM objects for the input, the allocator provider and the frame: the provider asks for PlayMap's slots, checks every `QueryInterface` against an older driver, maps the hardware timestamp; the reference status published as the card's output fact |
| `PassThroughTests` | both | `OUTPUT PASSTHROUGH op ON` → one frame in flight, out of the delay ring, the pacing output; in a lock group → the group's pacing output keeps the role and the words say what it costs; `ON` when on refused |
| `GlPassThroughTests` | Linux (GL) | the pass-through on GL: one CPU copy counted, the fence wait before the sample, at most one frame queued (counted), `path: "gl"`, the amber row's words |
| `InputPhaseTests` | both | arrivals at a fixed offset into the refresh for 600 frames at an equal rate → "in phase"; a 3.2 ppm drift → "drifting" with the repeat interval; a card's reference status `locked` with a drifting input → still "drifting" |
| `RateMatchTests` | both | 59.94 (60000/1001) in, 60 out on a manual clock → one repeat per 1 001 output frames, counted; 50 in, 60 out → one repeat in every six; the preflight row's words |
| `CaptureLatencyTests` | both | arrival-to-shown p50 and p95 from the stamps; `glassToGlass` null and the words "unmeasured" until a loop has run |
| `PassThroughLatencyTests` | both | the fake capture provider standing in for the cable as the output's observer, delivering each step frame a scripted number of vblanks after its sample: ten `LATENCY STEP` steps → `glassToGlass` p50, p95 and p99 equal to the scripted sums plus each step frame's age at draw, the method's words; armed → `LATENCY STEP` refused by its class |
| `LateSampleTests` | both | a manual clock and scripted GPU-done times: the offset is the p99 of the last 600 plus the margin, clamped; a missed vblank → 0 for 10 s, counted, then back; off when no pass-through is on |
| `DeviceItemOpenTests` | both | a `video=` library item through `MediaSources` reaches the FFmpeg seam with the platform's input format (`dshow` on Windows, `v4l2` on Linux), not a probe (today's `Format = ""` case, written first, failing); the real open is L25's |
| `ZeroAllocTests` (extended) | both | a warm frame with a capture input allocates nothing |

## 12. The lab line it needs

**L25 — a capture card's pass-through** (`PLAN.md` §7): a DeckLink card and a USB capture device on an RTX node and a Radeon node, an
HDMI or SDI loop-through from an output back into the card, a photodiode on the projected image, house sync into the source and RTX PRO
Sync. Pass marks: the rung each reached (rung 1 on the DeckLink if the wrapped heap takes its DMA, else rung 2, said); zero CPU copies
counted on rung 1; on `PRESENT PATH D3D12` loop-through glass-to-glass at or below about 2.5 output frames at p95 with a free-running
source and about 1.5 in phase, with the late sample, and about 3 and 2 without it; the same on AUTO (GL), recorded beside it; the
input's phase "in phase" with the house reference shared and "drifting" without it; repeats at 59.94 → 60 one in about 16.7 s; Media
Foundation's latency on the USB device and on the DeckLink beside the native path; the photodiode's figure beside the loop's. With no
lab (the owner's answer 5), L25 is reported not run, and STATE says "unmeasured".

## 13. Risks

- **Rung 1 is unproven.** That a card's driver can lock and DMA into pages Direct3D 12 has also wrapped, and that WARP accepts the heap, is
  inference until M16 and L25 [cap §8]; `OpenExistingHeapFromAddress` is a call Microsoft describes as special-purpose and diagnostic,
  with restrictions that "may still exist" [cap §8]: rung 2 is the designed fallback, one copy, and the words say which rung ran.
- **The GL path is the default and the slower one** this round (AUTO is GL until M1's record): it says so in STATE and preflight, and
  L25 records both paths.
- **The late sample can miss.** A frame sampled too late repeats on every output of the node: the offset comes from the node's own p99
  plus a margin, and a miss turns it off for 10 s, counted.
- **Memory Integrity** (on by default on new Windows 11 machines, Home included) keeps an incompatible capture driver from loading
  [`research/windows11_home.md` §6.3]: discovery names the device the driver did not load for; PlayMap never toggles it.
- **A vendor SDK older or newer than the interop.** Every COM interface is asked for and checked; an old driver's missing interface is
  rung 2 or a refusal with words.
- **Mixed outputs on one node** lose the pass-through's frame unless it leaves the delay ring: §5 makes the trade explicit and STATE says
  it.
