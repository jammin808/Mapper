# Work package `optimise`: optimise on import

*Design for round 2, written 13 Oct 2026 against `main` at `c12b186` (`/home/user/mapper`). Roadmap item R2.4 (with
R2.11's contention rule for transcode work).*

**Evidence marks:**
- **[V]** I opened the source in this session, or the cited research report did.
- **[M]** Measured on this machine on 13 Oct 2026.
- **[U]** Unverified.
- **[E]** An estimate, with the arithmetic shown.

**Sources:**
- `r2/research/codecs.md`: §7 (what Watchout, Pixera, disguise, TouchDesigner and Resolume do) and §9.3–9.4 (the design implications).
- The **hap** package (`r2/design/hap.md`), which supplies the encoder (`IHapBlockEncoder`, `HapPacker`, `HapMuxer`) and the lossless rewriter.
- hap.md's new measurements, cited here as **hap N1–N7**. The key ones:
  - GPU fragment encoders reach FFmpeg-class quality (hap N5).
  - Pure-managed BC7 takes 30–63 s per 1080p frame (hap N6).
  - A Snappy pass takes 10–17 ms per 4K frame on one core (hap N7).

The package rests on three measured facts:
1. **The encode belongs on the GPU.** On a CPU rasteriser (llvmpipe, 4 cores) the GL 3.3 encoders run HAP Q at 35–56 ms per 4K frame. They match FFmpeg's CPU encoder within −0.2 to +0.4 dB on natural content.
2. **CPU BC7 in managed code is out of the question** (hap N6). HAP R at "Show" quality is a GPU compute job and, on one GPU, an overnight one (hap §9 risk 4). So the work must spread across machines.
3. **HAP Q costs 0.7–8.2 MB per 4K frame** (hap N1). Derived files are large, so disk space and transfer are planned, not discovered.

---

## 1. Goal and the bar

**Goal.** A clip the operator drops in becomes the version each node plays best, in the background, without touching the show. The work runs:
- on a GPU codec when that pays (HAP, HAP Q, HAP Q Alpha, HAP R, HAP HDR);
- losslessly when the file only needs re-chunking or a different second stage;
- not at all when the node already plays the source well (hardware-decoded H.264, HEVC or AV1 on the zero-copy path).

Every choice is a rule the operator can read. Every job shows progress, an ETA and a quality score that explains itself. Sources are never touched. Versions travel to nodes by content hash, like media.

**What the others do, and how PlayMap matches or beats each point:**

| Who / what | What they do (codecs.md §7) | PlayMap |
|---|---|---|
| **Watchout 7: one playout format** | "The Runner plays only WAF". H.264 and MPEG-2 become HEVC; ProRes becomes Notch LC; the HAP family passes through; stills become uncompressed raw | **Rules on properties and node capabilities**, not on the codec name alone (§3.3). An H.264 clip that every target node decodes in hardware stays as it is, with no transcode, because zerocopy plays it on the GPU. A 4K ProRes clip becomes HAP Q. HDR becomes HAP HDR, and alpha becomes HAP Q Alpha or HAP R |
| **Watchout: changing the mapping** | "Mapping changes apply to future optimizations only". To change an asset, delete and re-add it, or make a new version by hand | A rule change marks the derived versions it no longer matches as **stale**. One verb re-optimises them, with an estimate first: `MEDIA ESTIMATE STALE` → "14 clips, 3 min, 6.2 GB" (S2) |
| **Watchout: no HAP R encoder** | "WATCHOUT has no HAP R encoder, so a HAP R asset reaches a Runner only by pass-through" | **Encodes HAP R and HAP HDR** (hap M7). Fast on any GPU; Show quality on compute GPUs (hap S1) |
| **Watchout: one Asset Manager does the work** | Runners download up to 8 files in parallel. Encoding happens on the Asset Manager alone | Any node, or the desk's own engine, runs jobs in an **isolated worker process**. **Segments** of intra-only output make every job resumable, and S1 spreads the segments of one job over every idle node's GPU |
| **Watchout: keeps the source; dedupe** | "The original source is retained"; identical data is stored once | The same, and stronger in two ways. The derived file's key is `sha256(source hash, canonical recipe, encoder version)`, so the same work is never done twice **anywhere**: a node asks its peers (`MEDIA DERIVED <key>`) before it encodes. Each node fetches **only the version it will play** (§3.7), not every derivative |
| **Watchout: priority** | Image > Other > Audio > Video > Image Sequence: "quick assets become available first" | The same type order, then **what the show needs soonest**. Items cued next in any playlist go first. Items on air on this node go last and run at trickle, because their source is being read from this disk |
| **Watchout: status** | Icons, a progress bar (indeterminate when unknown), a job-queue summary, a red name on failure | Progress per frame and per segment, an **ETA** from the measured rate, **resume after crash or power loss** at segment granularity, and errors in plain words, in STATE, MCP, the desk and Companion |
| **Watchout: previews** | Quarter-size HAP previews | Posters, plus a **scrub strip** (one thumbnail a second), built during the encode (S3). Posters feed Companion keys through `IPosterStore` |
| **Watchout: resilience** | "The Asset Manager node only needs to be online when you edit the show or add content… During playback the Runners hold local copies" | The same: every node holds verified local copies. A node that dies mid-job resumes from its last finished segment when it returns, and nothing on air ever waits for a job |
| **Pixera** | A Content Transcoding tool to VP9, NotchLC, HAP, HAP Alpha and HAP Q; a "Batch Transcoding folder"; the result is **added as a file version** (codecs.md §7.2) | Versions, as in Pixera, plus **HAP Q Alpha, HAP R and HAP HDR**, and rules instead of a manual right-click per file. A watch folder comes in S4 |
| **disguise** | No built-in optimiser: it points users to Adobe Media Encoder (codecs.md §7.3) | Built in |
| **TouchDesigner** | Encodes every HAP flavour. The HAP R GPU encoder can trip the Windows 2 s TDR. HAP HDR is "not real time". It always writes 12 chunks (codecs.md §7.4) | Tiled GPU dispatches bounded to ≤ 50 ms (hap §3.8). The chunk count comes from the target nodes' cores. Raw chunks where Snappy saves under 10 %, so decode costs less CPU at no disk cost (hap N7) |
| **Every one of them** | No quality report. Work runs inside the playback application | **A quality score that explains itself**: PSNR on one sampled frame per second, the worst frame with its time and a poster, and "plays N layers at 4K60 on node X" from that node's own facts. **Never disturbs playback**: a separate process, refused on an armed node, trickle while a show plays, an automatic pause on a measured dropped frame, and a disk token bucket (§3.5) |

## 2. Scope this round

### Must

| # | Item | Acceptance criteria (checkable) |
|---|---|---|
| M1 | **Model** (Core, backward compatible):<br>• `MediaItem.Versions` (`MediaVersion`) and `MediaItem.Use` (`auto` / `source` / a version id);<br>• `Project.Optimise` (`OptimiseSettings`: mode, rules, default quality, target nodes);<br>• the id prefix `mv` | A round-1 project file loads with `versions: []`, `use: "auto"` and the default optimise settings. It round-trips byte-identical except for the new keys. `GET md…` shows the versions. `SET md… use "source"` is refused when it is already so ("already source") |
| M2 | **Planner and recipes** (Core, pure):<br>• `OptimisePlanner.Decide(item, settings, targets)` → `Keep` / `Lossless` / `Encode` / `Ask` / `Never`, with the recipe and the rule's words;<br>• `Recipe` canonical form and `Recipe.Key(sourceHash, encoderVersion)`;<br>• `VersionChoice.Pick(item, node facts, files present)` | Table tests, one row per default rule (§3.3) on probe facts. The table includes H.264 1080p with hardware decode on every target → `Keep`, and the same with a CPU-only node in the targets → `Encode hap-q`. The recipe key is stable across runs and machines (golden value). `VersionChoice` picks a HAP R version only on a node whose `BlockFormats` has `Bc7`, else the next one, else the source, and says why |
| M3 | **Job service** (engine extension `optimise`):<br>• a queue persisted in `NodeHome/optimise/jobs.json`;<br>• priorities (§3.4);<br>• a state machine (`queued → planning → encoding → assembling → verifying → done \| failed \| cancelled`, with `paused` beside each running state);<br>• verbs, STATE, journal, context | Engine tests with a fake worker:<br>• `MEDIA OPTIMISE md… TO hap-q` answers `{"stage":"accepted","jobs":[…]}`. STATE shows the job, then `done`. The project gains a version and the journal has one row.<br>• An engine restart mid-job reloads the queue and resumes.<br>• `MEDIA OPTIMISE` on an armed node is refused with the reason (fake `IShowGate`).<br>• A second identical request is answered "already has hap-q (mv…)" |
| M4 | **The worker process** `PlayMap optimise-worker`:<br>• its own hidden GL context (the hap encoder), FFmpeg decode (`FrameReader`), `HapPacker` and segments;<br>• JSON-lines protocol on stdin/stdout;<br>• lowered OS CPU and I/O priority;<br>• restarted by the supervisor | Process tests on this box (Xvfb, llvmpipe, FFmpeg 9):<br>• a 2 s 1080p libx264 clip becomes a HAP Q `.mov`. FFmpeg decodes it, and our source plays it.<br>• Killing the worker (SIGKILL) mid-job: it restarts within 2 s and the job resumes at the first unfinished segment. The final file equals a run with no kill, segment by segment (byte-equal segment payloads).<br>• A clip that crashes the worker twice on the same segment fails the job with words that name the frame range |
| M5 | **Segments, assembly, verify, record:**<br>• segments of `SegmentFrames` (600) aligned to source keyframes;<br>• `.hseg` files with checksums;<br>• assembly into `.mov` with audio copied;<br>• verify: parse every frame, GPU-decode sampled frames, PSNR vs source;<br>• the hash goes to the media store (`AddVerified`) and a version into the project | • The output's frame count and pts equal the source's (VFR included: a test clip with irregular pts).<br>• The audio stream's start and duration are equal within 1 ms (an AAC + HAP Q check through `ffprobe`).<br>• The quality report has `psnrAvg`, `psnrMin`, `sampled` and `worstAtMs`. On the 2 s `testsrc2` clip HAP Q gives `psnrAvg ≥ 43 dB` (hap N5) |
| M6 | **Playing versions:**<br>• `MediaBackend` resolves an item to the version `VersionChoice` picks on this node (`use: auto`), or the one named;<br>• a change takes effect at the item's next open, never mid-air;<br>• STATE says which version plays and why | Engine test: an item with a HAP Q version present on disk opens `HapVideoSource` on that file. With `use: "source"` it opens the source. A switch while on air leaves the open decoder alone until the next cue (asserted on `EngineSources`' open count) |
| M7 | **Sync and dedupe:**<br>• `MEDIA FETCH` pulls, for each item, the file this node will play (its pick), with `SOURCES` / `EVERYTHING` options;<br>• `MEDIA DERIVED <key>` answers a peer's hash for a key;<br>• a job first asks heard peers and fetches instead of encoding | Two engines on loopback, using the existing `NetworkHost` and media transfer:<br>• A makes the HAP Q version. B's `MEDIA OPTIMISE` on the same item fetches it from A by hash (no encode: the worker is never started on B), and records the version with A's quality.<br>• `MEDIA FETCH ALL` on a third node pulls only the version it will play, not the source |
| M8 | **Governor** ("measured, then governed"): a pure state machine `Run` / `Trickle` / `Paused` / `Waiting(armed)` driven by:<br>• armed;<br>• whether anything plays;<br>• output dropped and late deltas;<br>• source late deltas;<br>• disk free.<br>The service applies it to the worker: stop, start, the trickle knobs, and the disk token bucket | Deterministic tests with a fake clock and fake health:<br>• a dropped frame while a job runs pauses it for `PauseAfterDropS` (60 s), with one journal row naming the output;<br>• `IsAnimating` → Trickle (1 stream, GPU duty ≤ 15 %, disk ≤ 50 MB/s);<br>• armed → Waiting ("optimise waits: the show is armed");<br>• free disk below estimate + 10 GB → the job fails before it starts, with the numbers.<br>On this box, the worker's measured GPU duty at trickle (from its `GpuTimer` samples) stays ≤ 15 % ± 3 over a 30 s run |
| M9 | **Rules modes and the import hook:**<br>• `MEDIA IMPORT` runs the planner when the probe lands;<br>• `auto` queues jobs;<br>• `ask` records a **suggestion** (STATE, MEDIA LIST, the context's command list for an AI);<br>• `never` does nothing;<br>• `MEDIA ESTIMATE` gives time and size before any work | • An import of a 4K ProRes clip in `ask` mode puts it in `media.optimise.suggested`, with the words "ProRes 4444 decodes on the CPU here: HAP Q plays it on the GPU (est 2 min, 5.1 GB)".<br>• `MEDIA OPTIMISE SUGGESTED` queues exactly those.<br>• In `auto` mode the same import queues a job.<br>• `MEDIA RULES MODE NEVER` makes the next import queue and suggest nothing |
| M10 | **Surfaces:**<br>• STATE `media.optimise`; per-item `use`, `versions` and `optimise` in `MEDIA LIST`;<br>• the context item `optimise`;<br>• the MCP tools (generated from the catalogue);<br>• the desk jobs drawer and media badges;<br>• `IPosterStore` (posters made during the first planning pass) for the companion package | Engine tests assert JSON fields. App tests with the fake engine. `THUMB media:md…` (companion) is served from the poster store without a decode |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Farm**: one job's segments over idle peers. Peer-to-peer `OPTIMISE WORK <json>` (Review): source by hash, recipe, frame range. The segment is served back by its hash over the existing HTTP media port. The coordinator assembles | Two engines on loopback each run a worker. A 60-segment job finishes with segments made on both nodes. Each segment's checksum is verified on arrival. Killing B's worker reassigns B's unfinished segments to A |
| S2 | **Stale on rule change** + `MEDIA ESTIMATE STALE` + `MEDIA OPTIMISE STALE` | Changing a rule's target from `hap-q` to `hap-r` marks the matching versions stale (STATE `stale: n`). The estimate lists them, and the verb queues them |
| S3 | **Scrub strip** (1 thumbnail per second, 160 px JPEG, stored by content hash). The desk's scrub bar and Companion use it | App test: the strip loads with no decode |
| S4 | **Watch folder** (`NodeSettings.OptimiseWatch`): files that land there are imported and planned under the project's rules | A file copied in is imported, and its job queued in `auto` mode |
| S5 | **Hardware long-GOP targets** for disk- or space-limited rigs: `TO hevc-intra`, `hevc-shortgop`, `h264-shortgop` through FFmpeg's hardware encoders (`hevc_nvenc`, `hevc_qsv`, `hevc_amf`, `hevc_vaapi`) when the node has one | Compile-only here (no hardware encoder). A fake through `libx264`/`libx265` checks the plumbing in tests only, since PlayMap never selects a GPL software encoder itself |
| S6 | **VMAF** in the quality report where the FFmpeg build has `libvmaf` (this one does, codecs.md §9.3) | `vmaf` key present on this box |

### Later

- NotchLC as a target (the SDK licence, codecs.md §6).
- ProRes as a target (no LGPL-safe encoder of our own).
- AV1 intra.
- Re-optimising automatically when a node's facts change (a new GPU without BPTC joins). Today that surfaces as a suggestion.
- Job ownership handed between nodes when the coordinator leaves (needs fleet's project distribution).

## 3. Architecture

### 3.1 Components

```
 PlayMap.Core   Model: MediaVersion · MediaItem.Versions/Use · OptimiseSettings · OptimiseRule · OptimiseMatch
                Optimise: Recipe (canonical, key) · OptimisePlanner · VersionChoice · Governor · SegmentPlan · Estimates
                (uses Media/Hap/HapEncoding from the hap package)

 PlayMap.Media  Transcode/FrameReader (decode every frame in order, any layout) · Transcode/SegmentFile (.hseg)
                Transcode/Assembler (segments + audio → .mov via HapMuxer) · Hap/HapPacker · Hap/HapRewriter (hap)

 PlayMap.Render Encode/GlHapEncoder (hap) — the worker's own context

 PlayMap.Engine Optimise/OptimiseService (extension: queue, planner hook, governor loop, verbs facade, STATE)
                Optimise/OptimiseSupervisor (worker process life, protocol)   Optimise/JobStore (jobs, derived index, rates)
                Optimise/OptimiseWorker (runs inside "PlayMap optimise-worker": GL context + pipeline)
                Optimise/PosterStore (IPosterStore)   EngineCore.Optimise (verbs)   Host/OptimiseWorkerEntry
```

Module rules hold:
- The planner, recipes, version choice and governor are pure Core: table-tested, with no clocks of their own.
- Media decodes and muxes; Render encodes; Engine joins them.
- The worker is the same exe in a third role. The App's `Program.Main` dispatches `optimise-worker` as it does `engine` today.

### 3.2 Processes, threads and data flow

```
 engine process                                           optimise-worker process (BelowNormal CPU, idle/low I/O)
 ┌─────────────────────────────────────┐   stdin: JSON    ┌──────────────────────────────────────────────────────────┐
 │ OptimiseService (control lane posts)│ ───────────────► │ protocol thread                                          │
 │  queue · planner · governor loop    │  run/pause/knobs │ decode thread: FrameReader (FFmpeg; hw decode allowed     │
 │ OptimiseSupervisor                  │ ◄─────────────── │   only when the governor says Run) → leases (YUV/RGBA)   │
 │  spawn, watch, restart, kill        │  stdout: events  │ GL thread (own hidden context, GL ≥ 3.3 / ES 3.0):        │
 │ JobStore: jobs.json, derived.json,  │  progress/seg/   │   VideoTextureSet (yuv → RGBA16F) → GlHapEncoder tiles   │
 │   rates.json                        │  done/failed     │   → BlockReadback (integer PBO ring)                     │
 └─────────────────────────────────────┘                  │ pack pool (cores−1, or 1 at trickle): HapPacker per frame│
        │ Store.Apply (one writer)                        │ writer: SegmentFile .hseg (fsync per segment, checksum)  │
        ▼                                                 │ assembler: segments + audio → .mov (HapMuxer)            │
   Project.Media[i].Versions += …                         │ verifier: parse all, GPU-decode sampled, PSNR vs source  │
                                                          └──────────────────────────────────────────────────────────┘
```

**Why a separate process:**
- Isolation: an encoder fault, an FFmpeg crash on a bad file, a GL driver reset in the encoder's context or an out-of-memory never reaches the engine.
- Clean resource control: OS CPU priority, I/O priority, and a GL context the render context never shares.
- Kill-to-cancel: a job stops instantly however deep it is inside a native call.

This mirrors the desk/engine split (ARCHITECTURE.md §1). The worker is started on the first job and exits after `OptimiseIdleExitS` (120 s) with nothing queued, so a show node with no jobs carries no extra process.

**Protocol** (one JSON object per line; secrets never ride it, as nothing needs one):
- **Engine → worker:**
  - `{"t":"run","job":{…},"segments":[3,4,5]}`
  - `{"t":"knobs","mode":"trickle","gpuDuty":0.15,"cpuThreads":1,"diskMbps":50}`
  - `{"t":"pause"}`, `{"t":"resume"}`, `{"t":"stop"}`
- **Worker → engine:**
  - `{"t":"hello","pid":4242,"gl":"OpenGL 4.6 · NVIDIA RTX A4000","encoder":"hapenc/1","blocks":31,"compute":true}`
  - `{"t":"progress","job":"oj…","frames":5400,"of":18000,"fps":51.2,"gpuDuty":0.14}`
  - `{"t":"segment","job":"oj…","seg":9,"sha256":"…","bytes":…}`
  - `{"t":"done","job":"oj…","path":"…","sha256":"…","bytes":…,"info":{…},"quality":{…}}`
  - `{"t":"failed","job":"oj…","seg":12,"words":"…"}`
- The worker's `blocks` field is a `BlockFormats` value, `compute` says whether a compute context exists, and `gpuDuty` is measured with `GpuTimer` over 1 s.

**One frame through the worker:**
1. **Decode.** `FrameReader` (Media) wraps `StreamDecoder` and `FrameWriter`, the existing pieces. It decodes every frame in order and hands `VideoFrameLease`s with a pooled queue of 3.
   - Hardware decode follows the zerocopy ladder, but always **copy-back** in the worker. The worker's GL context is not the render context, and copy-back is cheap next to encoding.
   - Hardware decode is only allowed in `Run`. In `Trickle` the worker decodes in software, so the playback decoders keep the hardware decode engine.
2. **Upload and convert.** The worker's `VideoTextureSet` uploads the planes and runs `yuv` into RGBA16F. For HAP HDR it uses `uOutput = 1` (linear, 1.0 = 203 nits; hap §3.8 step 1). Sources with alpha arrive as `Rgba8` or `RgbaHalf`.
3. **Encode.** `GlHapEncoder` (hap M7) draws scissored tiles. The governor's GPU duty is enforced here: after each tile the worker sleeps `tileGpuMs × (1/duty − 1)`, measured by `GpuTimer`. At duty 0.15 a 4 ms tile is followed by a 23 ms rest.
4. **Read back.** `BlockReadback` keeps 3 frames in flight.
5. **Pack.** `HapPacker` plans chunks with `targets' cores`, runs the Snappy pass per chunk with raw-when-not-worth-it, and writes the frame (hap §3.8 steps 5–6).
6. **Write.** The frame is appended to the current `.hseg`. Each finished segment is fsynced and its sha256 reported.

### 3.3 The rules: planner and defaults

`OptimisePlanner.Decide(item, settings, targets)` is pure. Its inputs:
- the probe (`MediaInfo`, plus the facts it needs: bit depth, transfer, alpha, GOP class, chunk stats for HAP);
- the settings;
- `TargetFacts` per target node.

`TargetFacts` comes from the zerocopy package's STATE (`decode` capabilities), hap's `blocks`, cores, and preflight's disk MB/s when known. With no peer facts, it falls back to this node alone. The first rule that matches wins. Each rule has its own mode (`inherit` / `auto` / `ask` / `never`). The project's mode `never` overrides everything; that is the kill switch.

**Default rules** (written into new projects; older projects read them as the default):

| # | Name | When | To | Mode | Why (the words the operator sees) |
|---|---|---|---|---|---|
| 1 | `hap-lossless` | codec is HAP-family **and** (`chunks < 4` and width ≥ 1920) | `rechunk` (lossless, hap M8) | auto | "1 chunk: decodes on one core; re-chunking changes no pixel" |
| 2 | `hap-keep` | codec is HAP-family | `keep` | — | "already a GPU codec" |
| 3 | `hdr` | transfer PQ or HLG, or float/EXR, or bit depth ≥ 12 with a wide gamut | `hap-hdr` | ask | "HDR source: HAP HDR keeps the range on the GPU (BC6H)" |
| 4 | `alpha` | has alpha (ProRes 4444, QuickTime Animation, PNG/TIFF sequence with alpha, VP9 alpha) | `hap-q-alpha` | ask | "alpha: HAP Q Alpha has the best measured quality per byte here; HAP R if you choose it" (hap N5: YCoCg 43.3 dB vs BC7 mode 6 40.1 dB on graphics) |
| 5 | `cpu-intra` | codec in {prores, dnxhd, cfhd, mjpeg, qtrle, png, tiff, ffv1, notchlc, dxv, apv} | `hap-q` | ask (auto at ≥ 3840 wide) | "<codec> decodes on the CPU here: HAP Q plays on the GPU" |
| 6 | `long-gop-cpu` | codec in {h264, hevc, av1, vp9, mpeg2video} **and** some target node decodes it in software (`TargetFacts.HwCodecs`) | `hap-q` | auto | "node B decodes this HEVC on the CPU" |
| 7 | `long-gop-hw` | codec in {h264, hevc, av1, vp9} | `keep` | — | "every target node decodes it on the GPU (zero-copy)" |
| 8 | `sequence` | kind = image sequence (no alpha) | `hap-q` | ask | "thousands of files → one file; faster disk reads" |
| 9 | `stills` | kind = image | `keep` | — | "a still uploads once" |
| 10 | `fallback` | anything | `keep` | — | "no rule asks for a change" |

`OptimiseMatch` fields:
- `codecs`, `kinds`, `alpha`, `hdr`, `minBitDepth`, `minWidth`;
- `cpuDecode` (true = some target decodes it on the CPU), `maxChunks`;
- `tags` (the item's `Tags`: an operator can tag `#keep` or `#hap-r` and add a rule for it).

A rule's `to` is one of:
- `keep`;
- `rechunk`, `strip`, `snappy`;
- `hap`, `hap-alpha`, `hap-q`, `hap-q-alpha`, `hap-alpha-only`, `hap-r`, `hap-hdr`;
- S5: `hevc-intra`, `hevc-shortgop`, `h264-shortgop`.

**Recipe.** `Recipe(To, Quality, Chunks, SecondStage, RawBelowGain, Size, AlphaFrom, HdrWhiteNits, HdrSigned, Audio)`:
- Its canonical text is compact JSON with sorted keys and invariant-culture numbers.
- The key is `"ok:" + hex(sha256("playmap-optimise/1\n" + sourceHash + "\n" + canonical + "\n" + encoderVersion))[..32]`. The encoder version is the **algorithm** version, `hapenc/1`, not the device, so every GPU's result counts as the same work.
- GPUs may differ in the last bit of a block. The version records the bytes actually made (`ContentHash`), and the first version recorded wins.

**Stale (S2).** A version is stale when `Decide` for its item, under the current rules and targets, yields a different recipe key from the version's own. This is computed, not stored, so it can never be out of date.

### 3.4 Jobs, priorities and segments

**Job record** (`JobStore`, `NodeHome/optimise/jobs.json`, written atomically by temp file + rename on every state change):

```json
{"id":"oj3f9a1c2d","media":"md1a2b3c4d","name":"Facade loop","source":"sha256:…","key":"ok:…","recipe":{…},
 "state":"encoding","priority":2,"createdAt":1760360000000,"frames":18000,"segments":[{"n":0,"from":0,"to":600,"state":"done","sha256":"…"},…],
 "node":"a1b2c3d4","attempts":0,"words":""}
```

**Priority** (lower runs first):
1. **Kind**, as Watchout orders it: image 0, other 1, audio 2, video 3, sequence 4.
2. **Show need**: an item cued next in any playlist −2; an item that is on air on this node +2 (it runs, but only at trickle).
3. **Size**: smaller first within a class ("quick assets become available first").

Ties go to the oldest job.

**Segments** (`SegmentPlan`, pure):
- Nominal length is `SegmentFrames` (600, 10 s at 60 fps).
- For long-GOP sources, a boundary is the first keyframe at or after `k × SegmentFrames`, read from the demuxer index (`avformat_index_get_entry` flags `AVINDEX_KEYFRAME`). Decoding a segment starts at its keyframe, so no frame outside the segment is ever encoded.
- Segment *n* writes `NodeHome/optimise/work/<key>/seg-00n.hseg`. The format: magic `PMHS`, version 1, then repeated `[u32 bytes][i64 pts][i64 duration][payload]`. A sidecar `seg-00n.done` holds the sha256 over the payloads.
- Resume skips finished segments whose checksum still matches.
- **Assembly** writes `NodeHome/media/<sha256>.mov`:
  - the video packets from the segments in order (pts unchanged);
  - the source's audio stream-copied with its original timestamps when the mov muxer takes the codec, else decoded to PCM s24le;
  - the `colr` atom and metadata.
  - The work folder is deleted after the version is recorded. `OptimiseKeepWorkGb` caps leftovers.

**Verify** (in the worker, before `done`):
1. Every packet must pass `HapFrame.TryParse`.
2. One frame per second (max 120) is GPU-decoded through the `hap` pass, together with the matching source frame through `yuv`. Both are read back as RGBA8, and PSNR is computed on the CPU over RGB (Y-weighted average reported too).
3. The report gives `psnrAvg`, `psnrMin` and `worstAtMs`, plus a poster of the worst frame (S3 shows it on the desk).
4. A job whose `psnrMin` falls below `MinPsnr` (default 30 dB) is **not failed**. It is recorded with the words "lowest 28.4 dB at 1:02.4 — check that frame", and the planner never picks a version marked `review` automatically, because `VersionChoice` skips it until `MEDIA USE` names it. Attempts are not facts: the version is a candidate until a person or rule accepts it.

### 3.5 Never disturbs playback: the governor

`Governor` is a pure state machine stepped by the service once a second. All its thresholds are `NodeSettings`.

**Inputs:**
- `armed` (showsafety's `IShowGate.Armed`; absent = false);
- `animating` (`EngineCore.IsAnimating`);
- the sum of `OutputHealth.Dropped` and of on-air sources' `FramesLate`, as deltas;
- `diskFreeBytes` for the media store;
- the job's estimate.

| State | When | What the worker does | Words in STATE |
|---|---|---|---|
| `Waiting` | armed | stopped (process kept if alive, no work) | "optimise waits: the show is armed" |
| `Paused` | a drop or late delta > 0 while a job ran, for `PauseAfterDropS` (60) after the last one; or paused by verb | no work | "paused for 60 s: op_1 dropped a frame while it ran" (journalled once per pause) |
| `Trickle` | animating, no recent drop | 1 stream, `cpuThreads = 1`, GPU duty ≤ `TrickleGpuDuty` (0.15), disk ≤ `TrickleDiskMbps` (50), software decode | "a show plays: one stream, GPU ≤ 15 %" |
| `Run` | idle, no recent drop, not armed | full: hardware decode allowed, `cpuThreads = cores − 1`, duty 1.0, disk unthrottled | "" |
| (refused at start) | disk free < estimate + `MinFreeGb` (10) | job fails before it starts | "needs 6.2 GB + 10 GB margin; 9.1 GB free on /media" |

**Hysteresis:**
- `Run → Trickle` happens on the first animating second.
- `Trickle → Run` needs 10 s idle.
- `Paused` ends only after a full quiet window.

**Why these knobs.** CARRYOVER's measured-cause rule: each one is tied to a signal PlayMap already measures (output drops, source lateness). The governor is "measured, then governed" with a deterministic test per row.

**Worker-side enforcement:**
- **CPU priority.** `Process.PriorityClass = BelowNormal` on Windows. On Linux, `setpriority(PRIO_PROCESS, 0, 10)` (nice 10) via P/Invoke.
- **I/O priority.** Linux: `ioprio_set(IOPRIO_WHO_PROCESS, 0, IOPRIO_PRIO_VALUE(IOPRIO_CLASS_IDLE, 0))`. Windows: `SetFileInformationByHandle(FileIoPriorityHintInfo, IoPriorityHintLow)` on its read handles [U on the effect through FFmpeg's own handles; the token bucket is the guaranteed limit].
- **Disk rate.** A token bucket (the `TokenBucket` class from `PlayMap.Net.Media`, moved to Core.Time) on demux reads and segment writes.
- **GPU.** `EGL_IMG_context_priority` (`EGL_CONTEXT_PRIORITY_LOW_IMG`) would lower the worker's context priority where a driver offers it, but GLFW cannot pass that attribute, so it is a later item (an EGL-made worker context) [U on vendor support]. The duty cycle is the control that always works. A GL context in another process is scheduled by the OS GPU scheduler (WDDM, the DRM scheduler), and the duty cycle bounds its share either way.

### 3.6 Versions in the project and at play time

- **Recording.** When a job is done, the service posts to the control lane, and the control lane runs:

  ```csharp
  Store.Apply($"optimised {name} → {to}", p => p with {
      Media = p.Media.Update(id, m => m with { Versions = m.Versions.Add(version) }) });
  ```

  The version's file is already indexed (`MediaBackend.AddVerified`), and the derived index (`NodeHome/optimise/derived.json`: key → hash, size, info, quality) is updated. There is one writer: the engine's `ProjectStore`.
- **Choosing** (`VersionChoice.Pick`, pure). With `use: auto`, the node takes the versions it can play, in this order:
  1. formats in `BlockFormats`;
  2. not marked `review`;
  3. the file present and verified here, or fetchable.

  It prefers HAP-family over the source. Among HAP versions it takes the one the planner's current recipe names, then the highest measured quality. If nothing qualifies, it plays the source. `use: <id>` forces that version: if this node cannot play it, readiness says why ("mv… is HAP R; this GPU's GL has no BPTC").
- **Opening.** `MediaBackend.Open(item)` resolves through `Pick`. `EngineSources` keys its slots by item. A version change takes effect when the slot next opens:
  - the next cue;
  - a loop back to the item;
  - a retry.

  An item on air keeps its decoder, so frames never jump mid-air. That is also zerocopy's no-promotion rule.

### 3.7 Sync to nodes

- **Versions travel with the project.** They are a field on `MediaItem`. Where fleet distributes the project, every node learns of them.
- **`MEDIA FETCH <media>|ALL [FROM <node>] [PLAYS|SOURCES|EVERYTHING]`.**
  - The default `PLAYS` pulls, for each item, the file `VersionChoice` picks on **this** node. The engine passes `NetworkHost.FetchAsync` a `MediaItem` whose `ContentHash` and `SizeBytes` are the version's, so the transfer code, its verification and its show-time bandwidth limit are reused as they are.
  - `SOURCES` pulls sources, as today. `EVERYTHING` pulls both.
- **Dedupe across nodes.** Before a job encodes, the service asks each heard PlayMap peer `MEDIA DERIVED <key>`, with a 2 s budget. The first peer that answers with a hash wins: the job becomes a fetch, and the version is recorded locally with the peer's quality report (from its derived index). If no peer answers, the job encodes.

### 3.8 Estimates

`Estimates` is pure:
- **Time** = frames ÷ rate.
  - Rate comes from `NodeHome/optimise/rates.json`, an exponentially weighted rate per `(to, quality, pixel class ≤1080p/≤2160p/≤4320p, decode hw|sw)`, updated after each job.
  - Before the first job of a class, a 30-frame calibration burst runs in the worker (at most 3 s).
  - During a job, the ETA uses the job's own 10 s rate.
- **Size** = frames × predicted bytes per frame. The fraction is learned per (to, source codec class). The first estimate is fixed: HAP Q ≤ 1 B/px before Snappy; 0.35 B/px assumed for typical footage at its first estimate [E: codecs.md §4B clean 0.70 MB vs noisy 8.2 MB per 4K frame bound the range 0.08–0.99 B/px].
- **Layer budget.** "Plays N layers at 4K60 on node X" from hap's `HapBudget` (hap S6) with node X's facts.

### 3.9 Performance budgets

| What | Budget | Basis |
|---|---|---|
| Effect on playback | 0 dropped frames attributable to a job. A drop pauses jobs within 1 s, and the drop is journalled | M8 |
| Engine CPU for the service | ≤ 0.2 % of one core (1 Hz governor step, JSON lines) | [E] |
| Worker at Run, 4K HAP Q, mid-range GPU | ≥ 60 fps; the bound is the source decode and Snappy, not BCn | hap §3.10 [E]; Snappy ~4 ms per 4K frame on 4 cores (hap N7) |
| Worker at Run on this box (llvmpipe) | 1080p HAP Q ≈ 25–40 fps [E: hap N5 ÷ 4 for 1080p + 1–3 ms libx264 decode + Snappy] | measured in M4 tests and published as the CI rate |
| HAP R Show (S1), 4K | ~0.5 s per frame on one GPU [E: CAG26 NVTT fast 257 ms per 2048² × 2]; **a farm job** | hap §9 risk 4 |
| Resume loss after a crash | ≤ one segment (10 s of source) | M4 |
| Disk at trickle | ≤ 50 MB/s | M8 |

## 4. Spine needs

### 4.1 Model (Core; `src/PlayMap.Core/Model/MediaModel.cs`, `Project.cs`, new `OptimiseModel.cs`)

```csharp
// MediaItem record body (init properties; old files read as empty/auto)
public ImmutableArray<MediaVersion> Versions { get; init; } = [];
/// <summary>"auto" (the best version this node can play, else the source), "source", or a version id.</summary>
public string Use { get; init; } = "auto";

/// <summary>A derived file of a library item: same content, another codec. Content-addressed like media; the source is
/// never changed.</summary>
public sealed record MediaVersion(
    string Id,                 // "mv…"
    string To,                 // "hap-q", "hap-r", "rechunk", …
    string Recipe,             // canonical recipe JSON
    string Key,                // "ok:" + 32 hex
    string ContentHash,        // "sha256:…" of the derived file
    long SizeBytes,
    MediaInfo Info,
    VersionQuality Quality,
    string Encoder,            // "hapenc/1 · gl-frag · NVIDIA RTX A4000"
    string MadeOn,             // node id
    long CreatedUnixMs) : IEntity
{
    /// <summary>Recorded, but below the quality floor: never picked by "auto" until a person names it.</summary>
    public bool Review { get; init; }
}

public sealed record VersionQuality(double PsnrAvg, double PsnrMin, int Sampled, long WorstAtMs)
{
    public double? Vmaf { get; init; }                                  // S6
    public static readonly VersionQuality None = new(0, 0, 0, 0);
}

// Project record body
public OptimiseSettings Optimise { get; init; } = OptimiseSettings.Default;

public enum OptimiseMode { Auto, Ask, Never }
public enum RuleMode { Inherit, Auto, Ask, Never }

public sealed record OptimiseSettings(OptimiseMode Mode, ImmutableArray<OptimiseRule> Rules, EncodeQuality Quality,
    ImmutableArray<string> Targets)        // node ids the show plays on; empty = every heard PlayMap node
{
    public static readonly OptimiseSettings Default = new(OptimiseMode.Ask, OptimiseRules.Defaults, EncodeQuality.Show, []);
}

public sealed record OptimiseRule(string Name, OptimiseMatch When, string To, RuleMode Mode)
{
    public EncodeQuality? Quality { get; init; }
}

public sealed record OptimiseMatch
{
    public ImmutableArray<string> Codecs { get; init; } = [];
    public ImmutableArray<MediaKind> Kinds { get; init; } = [];
    public bool? Alpha { get; init; }
    public bool? Hdr { get; init; }
    public int MinBitDepth { get; init; }
    public int MinWidth { get; init; }
    public bool? CpuDecode { get; init; }
    public int MaxChunks { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
}
```

`Ids.MediaVersion = "mv"`. Jobs (`oj…`) are node-local and never part of the project. JSON follows the model's camelCase. Here is a version in a project file:

```json
{"id":"mv7c1d2e3f","to":"hap-q","recipe":"{\"audio\":\"copy\",\"chunks\":16,\"quality\":\"show\",\"rawBelow\":0.1,\"secondStage\":\"auto\",\"to\":\"hap-q\"}",
 "key":"ok:5b0e…","contentHash":"sha256:…","sizeBytes":5123456789,"info":{…},"quality":{"psnrAvg":44.1,"psnrMin":39.8,"sampled":120,"worstAtMs":62400},
 "encoder":"hapenc/1 · gl-frag · NVIDIA RTX A4000","madeOn":"a1b2c3d4","createdUnixMs":1760360000000,"review":false}
```

### 4.2 Core logic (new, pure: `src/PlayMap.Core/Optimise/*.cs`)

```csharp
public sealed record TargetFacts(string NodeId, BlockFormats Blocks, ImmutableArray<string> HwCodecs, int Cores, double DiskMbps, bool Compute);
public sealed record ProbeFacts(MediaInfo Info, MediaKind Kind, int BitDepth, string Transfer, bool LongGop, int HapChunks);
public enum Decision { Keep, Lossless, Encode, Ask, Never }
public sealed record PlanResult(Decision Decision, Recipe? Recipe, string Rule, string Words);

public static class OptimisePlanner
{
    public static PlanResult Decide(ProbeFacts probe, ImmutableArray<string> tags, OptimiseSettings settings, IReadOnlyList<TargetFacts> targets);
    public static bool IsStale(MediaVersion v, PlanResult now, string sourceHash, string encoderVersion);
}
public sealed record Recipe(string To, EncodeQuality Quality, int Chunks, SecondStagePolicy SecondStage, double RawBelowGain,
    string Size, string AlphaFrom, double HdrWhiteNits, bool HdrSigned, string Audio)
{
    public string Canonical();                                         // sorted keys, invariant culture
    public string Key(string sourceHash, string encoderVersion);       // "ok:" + 32 hex
    public HapEncodeSettings ToHap();
}
public static class VersionChoice
{
    public static (MediaVersion? Version, string Why) Pick(MediaItem item, TargetFacts here, Func<string, bool> present);
}
public enum GovernorState { Run, Trickle, Paused, Waiting }
public sealed record GovernorInputs(bool Armed, bool Animating, long DroppedTotal, long LateTotal, long NowNs);
public sealed record GovernorKnobs(GovernorState State, int CpuThreads, double GpuDuty, int DiskMbps, bool HwDecode, string Words);
public sealed class Governor   // pure: no clock of its own; Step(inputs) → knobs
{
    public Governor(GovernorSettings settings);
    public GovernorKnobs Step(in GovernorInputs inputs);
}
public static class SegmentPlan { public static int Plan(long frames, int segmentFrames, ReadOnlySpan<long> keyframes, Span<(long From, long To)> segments); }
public static class Estimates { public static (double EtaS, long Bytes) Of(ProbeFacts probe, Recipe recipe, RateTable rates); }
```

### 4.3 Verbs (`VerbCatalog`, category `Media`)

| Verb | Grammar | Risk | Mutates | Reply (fields; tests assert them) |
|---|---|---|---|---|
| `MEDIA OPTIMISE` | `MEDIA OPTIMISE <media>…\|ALL\|SUGGESTED\|STALE [TO <target>] [QUALITY FAST\|SHOW\|ARCHIVE] [FARM] [FIRST]` | Review | yes (jobs) | `OK {"stage":"accepted","jobs":[{"id":"oj…","media":"md…","to":"hap-q","etaS":184,"fetchFrom":null}],"skipped":[{"media":"md…","why":"already has hap-q (mv…)"}],"etaS":184,"bytes":5100000000}`. **Refused:** armed ("optimise runs in rehearsal, not on an armed node"); nothing to do ("every item named already has that version"); `TO hap-r QUALITY SHOW` with no compute worker ("Show-quality HAP R needs a GL 4.3 or ES 3.1 GPU; this node's worker has GL 3.3 — use QUALITY FAST or FARM"); not enough disk (the numbers) |
| `MEDIA OPTIMISE CANCEL` | `MEDIA OPTIMISE CANCEL <job>\|ALL` | Low | yes | `OK {"cancelled":["oj…"]}`. Refused when nothing matches |
| `MEDIA OPTIMISE PAUSE` / `RESUME` | `MEDIA OPTIMISE PAUSE\|RESUME [<job>\|ALL]` | Low | yes | `OK {"paused":["oj…"]}`. Refused when already in that state |
| `MEDIA JOBS` | `MEDIA JOBS` | Low | no | `OK {"governor":{…},"jobs":[…]}` (the STATE shape, every job) |
| `MEDIA ESTIMATE` | `MEDIA ESTIMATE <media>…\|ALL\|SUGGESTED\|STALE [TO <target>] [QUALITY …]` | Low | no | `OK {"items":[{"media":"md…","decision":"encode","to":"hap-q","rule":"cpu-intra","words":"…","frames":18000,"etaS":184,"bytes":5100000000,"layers4k60":9}],"etaS":184,"bytes":5100000000}` |
| `MEDIA VERSIONS` | `MEDIA VERSIONS <media>` | Low | no | `OK {"media":"md…","use":"auto","playsHere":"mv…","why":"HAP Q, present, 44.1 dB","versions":[…],"stale":[]}` |
| `MEDIA USE` | `MEDIA USE <media> AUTO\|SOURCE\|<version>` | Review | yes (project) | `OK {"stage":"applied","media":"md…","use":"mv…","effect":"takes effect when md… next opens; it is on air on 1 output and keeps its decoder"}`. Refused when unchanged |
| `MEDIA VERSION REMOVE` | `MEDIA VERSION REMOVE <version> [FILES]` | Review | yes | `OK {"removed":"mv…","files":1}`. `FILES` also deletes this node's derived file if no other item names that hash |
| `MEDIA PRUNE` | `MEDIA PRUNE [DRY]` | Review (`DRY` runs as a query) | yes | `OK {"files":12,"bytes":61000000000,"dry":true}`: derived files in this node's store that no item of the open project names |
| `MEDIA DERIVED` | `MEDIA DERIVED <key>` | Low | no | `OK {"key":"ok:…","hash":"sha256:…","bytes":…,"quality":{…},"info":{…}}` or `OK no` |
| `MEDIA RULES` | `MEDIA RULES` | Low | no | `OK {"mode":"ask","quality":"show","targets":[],"rules":[…]}` |
| `MEDIA RULE ADD` | `MEDIA RULE ADD <json> [AT <n>]` | Review | yes (project) | `OK {"rules":11,"at":3,"stale":2}` (S2 fills `stale`). JSON as `OptimiseRule`; unknown fields refused (fail closed) |
| `MEDIA RULE REMOVE` | `MEDIA RULE REMOVE <n>\|<name>` | Review | yes | `OK {"rules":10}` |
| `MEDIA RULES MODE` | `MEDIA RULES MODE AUTO\|ASK\|NEVER` | Review | yes | `OK {"mode":"never"}`. Refused when unchanged |
| `MEDIA FETCH` (changed) | `MEDIA FETCH <media>\|ALL [FROM <node>] [PLAYS\|SOURCES\|EVERYTHING]` | Low | yes | as today; `PLAYS` is the default |
| `OPTIMISE WORK` (S1) | `OPTIMISE WORK <json>` | Review | yes | peer-to-peer: `{"job","source","recipe","segments":[…]}` → `OK {"accepted":[…]}`. Segments are fetched back by hash |

MCP gets every verb from the catalogue, with its risk. The context's command list carries `MEDIA OPTIMISE SUGGESTED` (Review) whenever suggestions exist, so an AI may propose it and a person approves.

### 4.4 STATE and context

Inside `STATE.media`, new keys at the end:

```json
"media":{"items":24,"missing":0,"ready":true,"transfers":[…],
 "optimise":{"mode":"ask","governor":"trickle","words":"a show plays: one stream, GPU ≤ 15 %",
   "running":1,"queued":3,"paused":0,"failed":0,"suggested":4,"stale":0,"etaS":412,"bytesQueued":21000000000,
   "worker":{"pid":4242,"gl":"OpenGL 4.6 · NVIDIA RTX A4000","encoder":"hapenc/1","compute":true,"restarts":0,"gpuDuty":0.14},
   "jobs":[{"id":"oj3f9a1c2d","media":"md1a2b3c4d","name":"Facade loop","to":"hap-q","quality":"show","state":"encoding",
            "framesDone":5400,"frames":18000,"fps":51.2,"etaS":246,"segmentsDone":9,"segments":30,"fetchFrom":null,"words":""}]}}
```

- `jobs` holds at most 16 rows: the running ones, then the queue head. `MEDIA JOBS` has them all.
- `MEDIA LIST` rows gain `use`, `versions` (count), `playsHere` (version id or `"source"`) and `optimise`. The `optimise` field is one of `""`, `"suggested"`, `"queued"`, `"encoding 30 %"`, `"fetching"`, `"optimised"`, `"stale"`, `"review"` or `"failed"`.
- **Context item `optimise`:**
  - **info** while jobs run: "optimising 3 clips, 7 min left".
  - **amber** when an item in a playlist is suggested or stale **and** some target decodes it on the CPU at ≥ 4K: "2 clips in the show still decode on the CPU: optimise them (est 6 min)". This comes with the command `MEDIA OPTIMISE SUGGESTED`.
  - **red** when a job for an item in the show failed: its words.
- **Events**, when the companion package's `EVENT` channel lands: `optimise.queued`, `optimise.done {media, version, quality}`, `optimise.failed {media, words}`, `optimise.suggested {media, to, etaS}`.

### 4.5 Seams

```csharp
// PlayMap.Engine/Optimise — the service as the verbs and other packages see it (EngineServices init property in its own file)
public interface IOptimiseService
{
    OptimiseView View { get; }                                             // STATE.media.optimise
    Task<CommandReply> OptimiseAsync(IReadOnlyList<MediaItem> items, OptimiseRequest request, CancellationToken ct);
    bool Cancel(string jobOrAll); bool Pause(string jobOrAll); bool Resume(string jobOrAll);
    IReadOnlyList<SuggestionView> Suggested { get; }
    (string Hash, long Bytes, VersionQuality Quality, MediaInfo Info)? Derived(string key);
    void OnImported(MediaItem item);                                       // the MEDIA IMPORT hook (control lane)
}
// EngineServices.Optimise.cs: public IOptimiseService? Optimise { get; init; }

// Companion's poster seam, implemented here (companion.md §4.4)
public interface IPosterStore
{
    bool TryGet(string contentHash, int maxWidth, int maxHeight, out ReadOnlyMemory<byte> jpeg, out int width, out int height);
}

// PlayMap.Media/Transcode — sequential decode for the worker
public sealed class FrameReader : IDisposable
{
    public FrameReader(string path, DecoderOptions decoder, long fromFrame = 0);
    public MediaInfo Info { get; }
    public IReadOnlyList<long> KeyframeIndex();                            // frame numbers with AVINDEX_KEYFRAME
    public bool TryRead(out VideoFrameLease? lease, out long frame);      // false at end; leases pooled (3)
}
```

**Showsafety's `IShowGate`** (companion.md §4.4) is read if present: `Armed`. Otherwise the node counts as in rehearsal.

`MediaBackend` gains `Pick(item)` internally (through `VersionChoice`), and `Resolve(item)` returns the picked file. `IMediaBackend.Readiness` words name the version ("plays mv… (HAP Q)").

### 4.6 NodeSettings (init properties with defaults, `src/PlayMap.Core/Bus/Pairing.cs`)

```csharp
public string OptimiseRole { get; init; } = "auto";      // auto (run jobs) | never (this node never encodes; it may still fetch)
public int OptimiseSegmentFrames { get; init; } = 600;
public double OptimiseTrickleGpuDuty { get; init; } = 0.15;
public int OptimiseTrickleDiskMbps { get; init; } = 50;
public int OptimisePauseAfterDropS { get; init; } = 60;
public int OptimiseMinFreeGb { get; init; } = 10;
public double OptimiseMinPsnr { get; init; } = 30;
public int OptimiseIdleExitS { get; init; } = 120;
public int OptimiseKeepWorkGb { get; init; } = 20;
public string OptimiseWatch { get; init; } = "";          // S4: a folder, "" = none
```

## 5. Files

**New**

| File | What |
|---|---|
| `src/PlayMap.Core/Model/OptimiseModel.cs` | `MediaVersion`, `VersionQuality`, `OptimiseSettings`, `OptimiseRule`, `OptimiseMatch`, modes, `OptimiseRules.Defaults` |
| `src/PlayMap.Core/Optimise/OptimisePlanner.cs`, `Recipe.cs`, `VersionChoice.cs`, `Governor.cs`, `SegmentPlan.cs`, `Estimates.cs`, `Psnr.cs` | §4.2 (pure) |
| `src/PlayMap.Core/Time/TokenBucket.cs` | moved from `PlayMap.Net/Media/MediaTransfer.cs` (unchanged; Net references it) |
| `src/PlayMap.Media/Transcode/FrameReader.cs`, `SegmentFile.cs`, `Assembler.cs` | decode, `.hseg`, assembly through `HapMuxer` |
| `src/PlayMap.Engine/Optimise/OptimiseService.cs` | extension: queue, planner hook, governor loop, dedupe, recording |
| `src/PlayMap.Engine/Optimise/OptimiseSupervisor.cs`, `WorkerProtocol.cs` | process life, JSON lines |
| `src/PlayMap.Engine/Optimise/OptimiseWorker.cs` | inside the worker: GL context (`RenderContext.CreateHidden` with `PreferredVersion`), pipeline, verify |
| `src/PlayMap.Engine/Optimise/JobStore.cs` | `jobs.json`, `derived.json`, `rates.json` (atomic writes) |
| `src/PlayMap.Engine/Optimise/PosterStore.cs` | `IPosterStore` (posters at planning, JPEG 320 px via the worker or one decode on the engine's pool while not armed) |
| `src/PlayMap.Engine/EngineCore.Optimise.cs` | the verbs |
| `src/PlayMap.Engine/EngineServices.Optimise.cs` | `IOptimiseService? Optimise` |
| `src/PlayMap.Engine/Host/OptimiseWorkerEntry.cs` | `PlayMap optimise-worker --home <folder> --parent <pid>` |
| `src/PlayMap.Engine/Host/EngineParts.Optimise.cs` | adds the `optimise` extension |
| tests: `tests/PlayMap.Core.Tests/Optimise/PlannerTests.cs`, `RecipeTests.cs`, `VersionChoiceTests.cs`, `GovernorTests.cs`, `SegmentPlanTests.cs`, `EstimateTests.cs`, `ModelCompatTests.cs`; `tests/PlayMap.Media.Tests/Transcode/FrameReaderTests.cs`, `SegmentFileTests.cs`, `AssemblerTests.cs`; `tests/PlayMap.Engine.Tests/Optimise/OptimiseServiceTests.cs`, `WorkerProcessTests.cs`, `DedupeTests.cs`, `VersionPlayTests.cs`, `OptimiseStateTests.cs` | §7 |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/MediaModel.cs`, `Project.cs`, `Ids.cs` | `Versions`, `Use`; `Optimise`; `mv` |
| `src/PlayMap.Core/Bus/Pairing.cs` | NodeSettings init properties |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | the rows in §4.3 |
| `src/PlayMap.Net/Media/MediaTransfer.cs` | `TokenBucket` moved to Core (a `using` change) |
| `src/PlayMap.Engine/EngineCore.Content.cs` | `MediaImport` calls `Optimise?.OnImported(item)` after the probe lands. `MediaFetch` gets `PLAYS`/`SOURCES`/`EVERYTHING` and the version items. `MediaListJson` gets the new row keys |
| `src/PlayMap.Engine/EngineCore.Router.cs` | the verb arms |
| `src/PlayMap.Engine/Host/MediaBackend.cs` | `Resolve` through `VersionChoice`; readiness words name the version |
| `src/PlayMap.Engine/Host/EngineParts.cs` | `AddExtensions` gets the optimise extension (or the side's own partial file) |
| `src/PlayMap.Engine/StateWriter.cs` | one call writes `media.optimise` at the end of `media` |
| `src/PlayMap.Engine/ContextBuilder.cs` | the `optimise` item |
| `src/PlayMap.App/Program.cs` | `optimise-worker` dispatch beside `engine` |
| `src/PlayMap.App/…` | media badges, the Optimise and Use menus, the jobs drawer, the ask banner, version compare (§8); `Link/FakeEngineLink.State.cs` |
| `docs/WIRE.md`, `docs/PLAYBACK.md` (optimise chapter), `docs/ARCHITECTURE.md` (§1 processes: the worker), `docs/ROADMAP.md` (R2.4) | papers |

## 6. Dependencies

**Work packages.**
- **hap (R2.3): hard dependency.** It supplies `IHapBlockEncoder`/`GlHapEncoder`, `HapPacker`, `HapMuxer`, `HapRewriter` (`rechunk`/`strip`/`snappy`), `HapFrame` (verify), `HapVideoSource` (playing versions), `BlockFormats`, `HapBudget`, and the `yuv` `uOutput` mode for HDR.
- **zerocopy (R2.2).** `TargetFacts.HwCodecs` comes from `STATE.decode` and its `DECODE PROBE` results. Without it, the planner uses this node's `StreamDecoder` hardware config list, honest but local.
- **showsafety (R2.6, R3.5).** `IShowGate.Armed` (Waiting). Preflight's disk MB/s (for `TargetFacts.DiskMbps`), and its layer budget reads versions.
- **fleet.** Distributes the project, so versions reach every node's project. Its health view shows each node's `media.optimise`. Until it lands, nodes dedupe and fetch by key (M7).
- **companion.** Consumes `IPosterStore`, `media.optimise` and the events (§8).
- **present.** Not needed: the worker uses GL (or ES on ANGLE through present's dialect) in its own process.

**Third party.** There are no new packages beyond hap's (Snappier). FFmpeg 9 is loaded at run time:
- demux and decode (existing);
- `mov` mux (existing libraries, used by hap's `HapMuxer`);
- PCM audio encode for codecs the mov muxer refuses (`pcm_s24le`, LGPL);
- S5 only: FFmpeg's hardware encoders (`hevc_nvenc`, `hevc_qsv`, `hevc_amf`, `hevc_vaapi`; LGPL wrappers over vendor runtimes from the user's driver).

PlayMap never selects `libx264`/`libx265` (GPL) on its own; tests may use them to make sources. VMAF (S6) is through the user's FFmpeg `libvmaf` filter, where present. **No FFmpeg code is copied.**

## 7. Verification here

| Area | Real on this machine | Simulated, and how | Compile-only or lab |
|---|---|---|---|
| Model, planner, recipes, version choice, governor, segments, estimates | **Yes**: pure table tests; golden recipe keys; a round-1 project round trip | node facts as literals | — |
| Worker pipeline (M4, M5) | **Yes**: Xvfb :99 + llvmpipe GL + FFmpeg 9. The sources are made by FFmpeg at test time:<br>• libx264 1080p 2 s → HAP Q;<br>• prores_ks 4444 with alpha → HAP Q Alpha and HAP R (Fast);<br>• libx265 Main10 PQ → HAP HDR;<br>• an EXR sequence → HAP HDR;<br>• a PNG sequence → HAP Q;<br>• qtrle with alpha → HAP Q Alpha;<br>• FFmpeg 1-chunk HAP → `rechunk`.<br>FFmpeg decodes every `Hap1`/`5`/`Y`/`M`/`A` output. Our source decodes all of them. `ffprobe` checks tags, pts and the audio offset | — | GPU throughput, multi-GPU machines, ANGLE contexts (lab) |
| Crash and resume | **Yes**: the real worker process, killed with SIGKILL between segments and inside one; byte-equal segment payloads after resume | — | power loss (fsync semantics are the OS's) |
| Governor effect | **Yes** for the logic (fake clock and health). Measured trickle duty on llvmpipe via the worker's `GpuTimer` | output drops injected through a fake `IOutputBackend` | **Real contention** (encode on one GPU while 4 outputs play) is a lab test with present's ledger: 30 min of 4×4K60 playback + a 4K HAP Q job at Run, then at Trickle; target 0 drops at Trickle |
| Dedupe and sync (M7) | **Yes**: two or three engines on loopback, using the existing `NetworkHost` and media transfer; `MEDIA DERIVED`; `MEDIA FETCH PLAYS` | — | a real show network (lab) |
| Farm (S1) | **Yes** on loopback (two engines, two workers) | — | a real multi-GPU farm (lab) |
| I/O and CPU priority | Linux nice and ioprio calls are checked by reading `/proc/<pid>/stat` and `ioprio_get` | — | Windows priority hints (compile-only) |
| Hardware encoders (S5) | — | `libx264`/`libx265` stand in for plumbing tests only | NVENC, QSV, AMF and VAAPI (lab) |
| STATE, verbs, context, MCP, desk | **Yes**: engine tests (fields, not words); app tests with the fake engine | — | — |

**Honest summary:**
- **Runs for real here:** the whole optimise pipeline on a CPU rasteriser with FFmpeg 9 (decode, GPU-program encode, pack, segments, assembly, verify, record, dedupe, fetch, crash and resume).
- **Lab only:** encode speed and the interaction with real playback on a shared GPU. These are the numbers that matter for "never disturbs playback". The governor's logic is proven here; its real-world thresholds are confirmed by the lab drill.

## 8. Desk and Companion surface

- **Media panel:**
  - Each item carries a badge: `HAP Q ✓` (plays here), `optimising 42 % · 3 min`, `suggested`, `stale`, `review` (amber), or `failed` (red, with the reason on hover).
  - **Right-click → Optimise ▸**:
    - "As the rules say" (`MEDIA OPTIMISE md…`);
    - "HAP Q" / "HAP Q Alpha" / "HAP R" / "HAP HDR" / "Re-chunk (lossless)", each with its estimate from `MEDIA ESTIMATE` in the menu text;
    - "Cancel".
  - **Use ▸**: Auto / Source / each version with its quality, from `MEDIA USE`.
  - "Versions…" opens the version list with the quality report and the worst frame's poster.
  - **Compare (A/B)**: two VIEWs side by side, source and version at the same media time, with a wipe slider. The desk asks for two views through the existing `VIEW` verb, so it uses no private API.
- **After an import in `ask` mode**, a non-modal banner: "2 clips would play better as HAP Q (est 3 min, 4.2 GB)" with three buttons:
  - **Optimise** (`MEDIA OPTIMISE SUGGESTED`);
  - **Not now**;
  - **Never in this project** (`MEDIA RULES MODE NEVER`).
- **Jobs drawer:**
  - One row per job: name, target, progress bar, fps, ETA, node and state.
  - Buttons: Pause/Resume/Cancel per row, and "Pause all".
  - Header line: the governor's words ("a show plays: one stream, GPU ≤ 15 %").
  - A finished job shows its quality and a **Use it** button when its version is `review`.
- **Rules editor** in project settings: the rule table (drag to reorder), the mode switch, the default quality and the target nodes. Each edit is a `MEDIA RULE …` line.
- **Network drawer, node card:** "Optimise: 2 running · 51 fps · worker GL 4.6 · compute".
- **Companion** (the companion package's module, generated from verbs and STATE):
  - variables `optimise_state`, `optimise_running`, `optimise_queued`, `optimise_eta`, `optimise_suggested` and `optimise_failed`;
  - feedbacks: "Optimising" (blue, with a progress bar from `framesDone/frames`), "Optimise waiting: armed" (dark) and "Optimise failed" (red);
  - actions: `MEDIA OPTIMISE SUGGESTED` (Review: hold to fire), `MEDIA OPTIMISE PAUSE ALL` and `RESUME ALL` (Low, instant).
  - **Live auto-created keys** (the companion package's live page): one key per running job, with its poster from `IPosterStore`, a progress ring and the ETA. Pressing it pauses or resumes that job. The key disappears when the job is done, and the layout freezes while armed (companion.md §3).

## 9. Risks and open questions

1. **GPU contention on show nodes.** A shared GPU is the real risk. The worker process, the duty cycle, the drop-triggered pause and `OptimiseRole = never` for pure playback nodes are the mitigations. The lab drill (§7) sets the default trickle duty. Until it has run, the default role for a node with display outputs is `auto`, but the governor holds it at **Trickle at most** while any output is open and a show plays.
2. **Disk space.** HAP Q is 8–20× the size of a typical H.264 delivery file [E: 0.35 B/px HAP Q vs 0.02–0.04 B/px H.264 at 4K60 50–100 Mb/s]. Estimates come first. Jobs refuse with numbers below `MinFreeGb`. `MEDIA PRUNE` removes what nothing names.
3. **HAP R expectations.** Fast HAP R (mode 6) is valid but no better than HAP Q on opaque content (hap N5). So defaults never pick HAP R for opaque content, and Show-quality HAP R is a farm job until S1 is measured on a GPU.
4. **Determinism across GPUs.** Keys dedupe by algorithm, not bytes. Two nodes racing on the same key can make two files. The first recorded wins, and the other is pruned. The race window is narrowed by asking peers first.
5. **Long-GOP segment edges.** Open-GOP streams, and decoders that need preroll before a keyframe. The segment decodes from its keyframe and keeps frames by pts within its range. Tests cover x264 open-GOP (`-x264opts open_gop=1`).
6. **Audio edit lists.** A mov `elst` with a start offset must survive. The assembler copies `start_time` and the test checks A/V offset parity.
7. **Project growth.** Each version is about 600 bytes of JSON, which is negligible.
8. **Open:**
   - Should derived files live beside the project (portable shows) instead of in the node's store? Today's media store keeps them content-addressed and shareable. A "collect show" export (fleet) can copy them.
   - Is `EGL_IMG_context_priority` offered by NVIDIA's EGL on Linux [U]?

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| M1 model + compatibility tests | 3 |
| M2 planner, recipes, version choice, default rules, table tests | 6 |
| M3 job service, job store, priorities, verbs, journal | 7 |
| M4 worker process, protocol, supervisor, priorities, `FrameReader`, the GL pipeline | 10 |
| M5 segments, assembly with audio, verify and PSNR, record | 6 |
| M6 playing versions (`MediaBackend`, readiness, no mid-air switch) | 3 |
| M7 dedupe (`MEDIA DERIVED`), `MEDIA FETCH PLAYS`, loopback tests | 4 |
| M8 governor + service loop + lab drill script | 4 |
| M9 import hook, modes, suggestions, `MEDIA ESTIMATE` | 3 |
| M10 STATE, context, poster store, desk drawer and badges, papers | 6 |
| **Must total** | **52** |
| S1 farm | 8 |
| S2 stale; S3 scrub strip; S4 watch folder; S5 hardware targets (compile-only); S6 VMAF | 9 |
| **With should** | **69** |
