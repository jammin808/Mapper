# Work package `hap`: the HAP family on the GPU, including HAP R and HAP HDR, and the HAP encoder

*Design for round 2, written 13 Oct 2026 against `main` at `c12b186` (`/home/user/mapper`). Roadmap item R2.3, plus
the encoder half of R2.4 (the `optimise` package drives it).*

**Evidence marks** (as in the research reports):
- **[V]**: I opened the source in this session, or the cited research report did.
- **[M]**: measured on this machine on 13 Oct 2026. This box has 4 Xeon cores at 2.8 GHz, Mesa 25.2.8 llvmpipe on EGL surfaceless, and FFmpeg 9 at `/opt/ffmpeg9`.
- **[U]**: unverified.
- **[E]**: an estimate, with the arithmetic shown.

The basis is `r2/research/codecs.md`: §2 (the format), §4 (FFmpeg's CPU path measured), §5 (encoder costs), §9.1–9.3 (design implications) and §9.7–9.8 (tests, licences).

**New measurements made for this design.** The sources are under `scratchpad/r2/bench/`: `hapnet/src/Program.cs` (managed, .NET 10) and `proto/gpuhap.c` (C, EGL). Test clips are in `hapnet/clips/`, made by FFmpeg 9 from `testsrc2`, `testsrc2` + noise and `mandelbrot`.

| # | Probe | Result | Output |
|---|---|---|---|
| N1 | **Managed second stage.** Snappier 1.3.1 (BSD-3) decompressing real FFmpeg-made HAP packets, demuxed by FFmpeg.AutoGen, into one native buffer. Median of 90 frames | 4K HAP Q, 16 chunks, clean: **2.83 ms** on 1 thread, **2.01 ms** on 4 (4.1 GB/s out).<br>4K HAP Q, 8 chunks, noisy (8.2 MB in): **4.53 / 4.19 ms**.<br>4K HAP, 1 chunk: 1.63 ms.<br>4K HAP, noisy, raw chunks: 1.10 / 0.92 ms.<br>1080p HAP Alpha, 4 chunks: 1.48 ms.<br>The native libsnappy figure for the clean 4K HAP Q clip is 1.99 ms (codecs.md §4B), so managed code costs about 1.4× on one core | `hapnet/decode.out` |
| N2 | **memcpy bandwidth** of this box: 8.29 MB native to native | **2.97 ms (2.79 GB/s)**. A copy of a 4K BC3 frame is as expensive as decompressing it | `hapnet/memcpy.out` |
| N3 | **Upload arena.** A pixel-unpack buffer made with `glBufferStorage(MAP_WRITE\|PERSISTENT\|COHERENT)` and mapped once. The context was requested as 3.3 core; Mesa returned 4.5 core and lists `GL_ARB_buffer_storage`. A worker pthread writes a real 4K HAP Q payload. The GL thread then calls `glCompressedTexSubImage2D` from the buffer offset and reads back with `glGetCompressedTexImage` | **0 of 12 frames differ** (bit-exact across threads).<br>GL thread: upload + fence **2.71–2.82 ms** per 4K frame. llvmpipe copies on the CPU; a GPU driver DMAs.<br>Today's path (orphan + map + memcpy + upload) on the same frame: **7.13–8.47 ms** | `proto/gpuhap.out` (A1, A2) |
| N4 | **Padded BC texture.** A 1918×1078 picture held as a 1920×1080 BC3 texture | no GL error | A3 |
| N5 | **GPU encoders as `#version 330 core` fragment shaders** (no compute, integer targets only: they run on any GL 3.3 or ES 3.0 context) on llvmpipe, which spreads the work over the box's 4 CPU cores. One output texel per 4×4 block goes into an `RG32UI`/`RGBA32UI` target; the blocks are uploaded back as BCn and read back. PSNR in RGB. Each pair is our shader vs **FFmpeg 9's CPU `hap` encoder on the same 4K source**:<br>• **BC1** (PCA axis, one least-squares refine): 36–54 ms per 4K frame. Clean **38.65 vs 39.00 dB**; mandelbrot **31.28 vs 31.50**; noisy **33.67 vs 33.54**.<br>• **HAP Q** (scaled YCoCg in BC3; Y as an 8-level block): 35–56 ms. Clean **43.34 vs 46.41**; mandelbrot **34.33 vs 34.21**; noisy **38.18 vs 37.81**.<br>• **HAP R, BC7 mode 6 only**: 90–171 ms. Clean 40.05, mandelbrot 32.25, noisy 34.70 dB; alpha max diff 1; every block decodes in Mesa's BPTC decoder | Even on a CPU rasteriser, the shader encoders run near FFmpeg's CPU encoder, which takes 41.7 ms CPU per 4K HAP Q frame (codecs.md §4D). Their quality is FFmpeg's on natural content. On flat graphics HAP Q is 3.1 dB below FFmpeg, because the prototype does not refine Y endpoints. HAP R mode 6 is valid but is **not** better than HAP Q on opaque content | `proto/gpuhap.out` (B, B2, B3), `hapnet/ffmpeg_psnr.out` |
| N6 | **CPU encoders in C#**:<br>• a scalar bounding-box BC1/YCoCg prototype;<br>• BCnEncoder.NET 2.3.0 (MIT), with 4 tasks | • Scalar: 159–631 ms per 4K frame, BC1 at 27.7–29.7 dB. Rejected.<br>• BCnEncoder.NET per **1080p** frame: BC1 Fast 112–186 ms, BC3 Fast 139–392 ms. **BC7 Fast 30.4–45.5 s; Balanced 28.7–63.1 s.**<br>• BCnEncoder.NET's CPU *decode* of a 4K frame: BC1 188 ms, BC3 200 ms, BC7 427 ms | `hapnet/encode.out`, `bcnnet.out`, `bcdec.out` |
| N7 | **Snappier compression** of one 4K HAP Q payload, 16 chunks, 1 thread | clean: 15.3 ms, output 8 % of input. Noisy: 10.1 ms, output 98 % (raw is the better choice). Mandelbrot: 17.3 ms, output 40 % | `hapnet/encode.out` |

What these numbers settle:
1. **The managed decode path is fast enough.** It is 2–4.5 ms per 4K frame; FFmpeg's CPU HAP Q decode costs 53–102 ms.
2. **On a bandwidth-poor machine, the copy into the PBO is the next cost** (N2, N3). The upload arena removes that copy from the render thread.
3. **The encoder is a GPU program.** It runs on every node with a GL 3.3 or ES 3.0 context, and on llvmpipe in CI.
4. **No pure-managed BCn code has a place on the video path.** It survives only as test oracles, and even there the GL readback is better.

---

## 1. Goal and the bar

**Goal.** Every HAP file plays from disk to the air without its pixels touching the CPU. The CPU parses the file and undoes Snappy into memory the GPU reads. The GPU decodes the BCn blocks. This holds for all seven fourccs, including **HAP R** and **HAP HDR**, which FFmpeg cannot decode. The same package writes all seven formats, so `optimise`, effect bakes and captures can produce them.

**What the others do, and how PlayMap matches or beats each point:**

| Who | What they do (source) | PlayMap |
|---|---|---|
| **FFmpeg-based players**<br>(today's PlayMap; Millumin, MadMapper, Resolume [U] on their HAP R status) | FFmpeg decodes `Hap1/5/Y/A/M` on the **CPU**. 4K HAP Q costs 53–102 ms CPU per frame, plus 10–15 ms of swscale in PlayMap. FFmpeg has **no HAP R or HAP HDR** decoder (codecs.md §0.2, §4A) | **26–45× less CPU** per frame (N1 vs §4A), and 4× fewer bytes to the GPU (8.3 vs 33.2 MB at 4K) |
| **Watchout 7** | Ingests HAP R by pass-through only: "WATCHOUT has no HAP R encoder". HAP HDR is not listed (codecs.md §7.1) | Decodes **and** encodes HAP R and HAP HDR (M7) |
| **disguise** | HAP, HAP Alpha and HAP-Q. **No HAP-Q Alpha**; HAP R and HDR are not listed (codecs.md §2.11, §7.3) | All seven, HAP Q Alpha included |
| **Pixera** | HAP R decode "as of 1.8". Its 1.8 changelog says "HAP decoding set to single thread usage due to deadlock". 26.1 adjusts odd sizes when transcoding (codecs.md §2.11, §7.2) | Chunk-parallel decode on a pool shared by all sources, with no lock between chunks (§3.2). Odd sizes play as they are, by a crop with no re-encode (§3.6) |
| **TouchDesigner** | Decodes and encodes HAP R and HAP HDR. Its HAP R GPU encoder "can exceed the Windows 2 s TDR" at high resolution. HAP HDR is "not real time". It always writes 12 chunks (codecs.md §2.11, §5) | **Tiled encode dispatches**, each kept under a measured 50 ms budget, so no TDR (§3.8). The chunk count comes from the target nodes' cores. Snappy is used only where it pays (§3.8 step 5) |
| **VDMX6, KlakHap** | VDMX6 decodes HAP R on Metal. KlakHap does HAP, HAP Alpha and HAP Q only (codecs.md §2.11) | Windows (D3D11 through ANGLE), Linux and every GL 3.3 or ES 3.0 device that offers the formats |
| **Every one of them** | A broken frame is a log line or a black layer. HAP HDR colour is a hidden guess | **Defined outcomes:**<br>• a refused frame keeps the last good picture and is counted, with the reason, in STATE;<br>• HAP HDR's interpretation is explicit, overridable and shown (§3.7);<br>• **per-source evidence** in STATE, MCP and the desk: format, chunks, second stage, CPU ms per frame, upload MB per frame and the staging path |

**Where PlayMap is ahead of everyone named above:**
1. **Workers decompress straight into GPU-visible memory** through a persistently mapped arena (N3). No player documents this.
2. **One-click lossless fixes.** A 1-chunk file is re-chunked, Snappy is stripped or added, and the pixels never change (M8).
3. **The layer budget is computed and published** per node and per asset (M9, S6), with the disk, not the decoder, as the honest limit (codecs.md §9.1).

## 2. Scope this round

### Must

| # | Item | Acceptance criteria (checkable) |
|---|---|---|
| M1 | **Core parser and writer** (pure, no native code): `HapFrame.TryParse`, `HapFrameWriter`, `HapChunking`, `SnappyLength`, `HapCodecs` (fourcc ↔ texture ↔ layout) | **Conformance tests** on hand-built frames:<br>• every type byte (`0xAB…0xC3`, `0x0D`);<br>• the 8-byte header form;<br>• a missing offset table;<br>• mixed raw and Snappy chunks;<br>• a `0x0D` pair;<br>• an unknown inner section, which is **skipped**;<br>• truncated, oversized and inconsistent tables, each **refused with its `HapRefusal`**;<br>• a GDeflate `0xD0` chunk, refused with "non-standard".<br>Writer→parser round trip for every texture. Zero allocations per parse (`GC.GetAllocatedBytesForCurrentThread` flat over 1,000 parses) |
| M2 | **`HapVideoSource : IVideoSource`** in Media:<br>• a read thread with time- and byte-bounded read-ahead;<br>• a decode thread;<br>• the shared chunk pool `HapWorkers`;<br>• `FramePool`/`FrameQueue` reused with **compressed `PixelLayout`s**;<br>• seek, loop and rate as `FfmpegVideoSource` does them | **Interop:** FFmpeg encodes `hap`, `hap_alpha` and `hap_q` with `-chunks 1, 4, 8, 16, 64` and `-compressor none\|snappy`. For each, the source's BCn planes equal, byte for byte, an independent decompression of the same packets through the system `libsnappy.so.1` (P/Invoke in the test only; the research prototype `happarse` used it).<br>**Timing on this box:** the decode thread spends ≤ 6 ms p95 per 4K HAP Q frame (N1: 2–4.5 ms).<br>**Allocation:** `DecodeThreadAllocatedBytes` stays flat after warm-up.<br>**Seek:** to any frame lands in ≤ 1 frame period plus one read. A loop crosses its point with no empty queue (the existing `VideoSourceTests` cases run against the HAP source) |
| M3 | **Probe and open routing:**<br>• `MediaInfoReader` names the HAP codecs (`hap`, `hap_alpha`, `hap_q`, `hap_q_alpha`, `hap_alpha_only`, `hap_r`, `hap_hdr`) from `codec_id == HAP` or tag `Hap7`/`HapH`;<br>• `MediaProbe` proves "decodable" by parsing and decompressing the first frame;<br>• `MediaSources.Open` picks `HapVideoSource`. `NodeSettings.HapDecode = "ffmpeg"` forces today's path (`Hap1/5/Y/A/M` only) | • M7's own `Hap7` file probes as `hap_r` with `decodable: true`; today its probe says "decoder not available: none".<br>• The research's re-tagged clip (`Hap7` tag over BC1 sections) is refused with `NotThisCodec`: "frame 0 holds BC1 blocks but the file says HAP R".<br>• `HapDecode = "ffmpeg"` with a `Hap7` item: readiness `decodable: false`, with the words "HAP R needs PlayMap's GPU decoder (HapDecode is ffmpeg)" |
| M4 | **Render:**<br>• compressed `TextureFormat`s;<br>• **capability by extension string**, never by `GL_COMPRESSED_TEXTURE_FORMATS` (codecs.md §4C), per dialect (Core330 / Es300 from the present package);<br>• the `VideoTextureSet` compressed branch;<br>• the `hap` program (§3.5): HAP Q YCoCg, the HAP Q Alpha pair, alpha-only, HDR, and the crop by `texelFetch` at the visible size | **GL tests on llvmpipe**, readback vs FFmpeg's CPU decode of the same frame:<br>• HAP and HAP Alpha: PSNR ≥ 60 dB, max diff ≤ 2;<br>• HAP Q: ≥ 50 dB, max diff ≤ 4 (observed 68 / 2 and 54.4 / 3, codecs.md §4C);<br>• HAP Q Alpha (made by M7; FFmpeg decodes `HapM`): RGB as HAP Q, alpha max diff ≤ 1;<br>• a 1918×1078 HAP file reads back 1918×1078 with no padding texel visible (a magenta edge band in the padding).<br>`GlCaps` (the spine's record) on llvmpipe reads all five families in both dialects |
| M5 | **Upload arena** (§3.4):<br>• one persistently mapped `PIXEL_UNPACK_BUFFER` per render context (`NodeSettings.UploadArenaMb`, default 256);<br>• slots are rented by HAP sources' `FramePool`s;<br>• leases are retained until a GL fence passes (`LeaseRetention`, shared with zerocopy).<br>Without `ARB/EXT_buffer_storage`, the copy path runs (today's `PixelUnpackRing`, compressed variant) and says so | • Cross-thread test as N3: 120 frames from 4 producer threads, every uploaded texture equal to its block bytes.<br>• **Reuse safety:** the arena never re-rents a slot whose fence has not signalled. Proved with a fake fence that stays unsignalled: the source starves (counted) and never overwrites.<br>• GL-thread time per 4K frame with the arena is less than half the copy path's on this box (N3: 2.8 vs 7.1–8.5 ms).<br>• `STATE.decode.platform.blocks.staging` reads `"persistent"`, or `"copy"` with the words |
| M6 | **HAP HDR interpretation** (§3.7):<br>• the default is **linear, 1.0 = 203 nits** (SDR reference white);<br>• `colr` nclx is read when present;<br>• `MediaItem.Color` overrides it (transfer, white nits);<br>• STATE shows the interpretation in use | A BC6H UF16 frame of value 1.0 renders as the same code value as an SDR white clip. A frame of 4.0 (812 nits) renders tone-mapped exactly as the `yuv` PQ path maps 812 nits (the shared `toneMap` function, CPU twin test). `SET md… color.whiteNits 100` changes it; the journal row and STATE show `hdr: "linear · white 100 nits (item)"` |
| M7 | **HAP encoder:**<br>• `IHapBlockEncoder` (Core seam) and `GlHapEncoder` (Render). GL 3.3 / ES 3.0 fragment programs: BC1, BC3 RGBA, YCoCg-BC3, BC4, **BC7 "fast"** (mode 6) and **BC6H "fast"** (mode 11; GPURealTimeBC6H's fast path, ported);<br>• **tiled** dispatch;<br>• integer-PBO readback;<br>• `HapPacker` (chunk plan, Snappier compress, raw-when-not-worth-it);<br>• `HapMuxer` (libavformat: `Hap7`/`HapH` via `FF_COMPLIANCE_UNOFFICIAL`, `colr`, audio copy) | **Interop:** FFmpeg 9 decodes PlayMap-made `Hap1`, `Hap5`, `HapY`, **`HapM`** and `HapA`. PSNR vs source is within 0.5 dB of FFmpeg's own encoder on natural content (N5 shows −0.2 to +0.4 dB). On the `testsrc2` graphics clip HAP Q must reach ≥ 45 dB; that bar needs the Y-endpoint refine.<br>**HAP R/HDR:** PlayMap-made `Hap7`/`HapH` files demux as tag `Hap7`/`HapH` (`ffprobe`). They decode on llvmpipe to exactly the encoder's own blocks (byte-equal planes). BC6H readback is within RMSLE 0.01 of the float source.<br>**TDR safety:** no single draw exceeds the tile budget. A fake `GpuTimer` reporting 80 ms per tile halves the tile size. |
| M8 | **Lossless rewriter** `HapRewriter`: re-chunk to N, strip Snappy, add Snappy (raw when not worth it), keep every other stream and the metadata | The pixels are equal: the planes of every frame before and after are byte-equal. Re-chunking a 1-chunk 4K file to 16 drops decode p95 on this box (N1: 1.63 → about 0.5 ms × chunks/cores) [E]. FFmpeg decodes the rewritten file |
| M9 | **Facts, verb and papers:**<br>• `SourceHealth.Hap` (`HapFacts`);<br>• `STATE.decode.sources[].hap` and `STATE.decode.platform.blocks`;<br>• `MEDIA INSPECT <media> [ON <node>]` (Low): format, chunk table summary, second-stage mix, MB/s at the clip's fps, odd size, layer budget, and fixes as verb lines;<br>• `docs/PLAYBACK.md` gets a HAP chapter | Engine tests assert JSON fields (not words). A 1-chunk 4K file's `MEDIA INSPECT` lists the fix `MEDIA OPTIMISE <m> TO rechunk`. `VERBS` lists `MEDIA INSPECT` as Low/query. The desk's fake engine writes the same shape |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **"Show" quality encoders** on compute contexts (GL 4.3+, ES 3.1):<br>• **BC7, all 8 modes with partition search**, ported from DirectXTex `BC7Encode.hlsl` (MIT);<br>• **BC6H quality**, ported from GPURealTimeBC6H (MIT/PD) | Bytes decode in Mesa's BPTC decoder. PSNR ≥ HAP Q's on the N5 set (the HAP R promise: Vidvox reports +5 dB with ATE, codecs.md §2.12). Speed on a GPU is a **lab** number. On llvmpipe (GL 4.5, compute present) the test checks correctness only |
| S2 | **Direct sampling** of BC1, BC3 and BC7 textures with no `hap` pass, when the size is a multiple of 4 and the mode is plain RGB(A). It saves the RGBA16F target: 66 MB of VRAM per 4K source | GL test: a picture equal to the pass's, and `STATE.decode.sources[].hap.pass = false` |
| S3 | **Raw HAP straight from disk into the arena.** For frames whose chunks are all raw (TouchDesigner's "no secondary compression", or our `strip` rewrite), the read thread `pread`s the texture bytes directly into the arena slot. The offset comes from the mov index (`avformat_index_get_entry`) plus the parsed header length. The CPU never touches the pixels | Byte-equal planes vs the packet path. The read thread's CPU per 4K frame is under 0.3 ms [E]. A 4K raw HAP Q file on this box plays at its fps with decode CPU about 0 |
| S4 | **sRGB block formats** (`*_SRGB` for HAP, HAP Alpha and HAP R) once R2.5's linear pipeline lands. Never for HAP Q (codecs.md §2.8) | Linear-pipeline tests |
| S5 | **Desk:**<br>• the codec chip and facts in the media inspector;<br>• warnings with fix buttons;<br>• right-click "Re-chunk for this rig", "Strip Snappy (disk-fast nodes)" and "Inspect on every node" | App tests with the fake engine |
| S6 | **Layer budget per node** (`HapBudget`): CPU from the measured `stageMsP95`, disk MB/s from preflight, upload from the measured upload ms, and VRAM | Pure-function table tests. `MEDIA INSPECT … ON <node>` uses that node's facts |

### Later (named so nothing is silently dropped)

- A native D3D11 upload (`UpdateSubresource` / a staging ring), when present's L2 replaces ANGLE on the hot path.
- A **map-ahead ring** for ES contexts without `EXT_buffer_storage`, which is likely ANGLE-on-D3D11 [U]. The GL thread maps N slots ahead with `MAP_UNSYNCHRONIZED`, workers fill them, and the GL thread unmaps and uploads.
- `io_uring` / `O_DIRECT` / unbuffered overlapped reads.
- NotchLC GPU decode: licence (codecs.md §6).
- DXV3 (proprietary).
- GDeflate second stage: non-standard, refused with words.

## 3. Architecture

### 3.1 Components

```
 PlayMap.Core   Media/Hap: HapCodecs · HapFrame (parse) · HapFrameWriter · HapChunking · SnappyLength · HapBudget
                Media/Hap/HapEncoding: HapTarget · EncodeQuality · HapEncodeSettings · IHapBlockEncoder · HapBlocks
                Media: PixelLayout (+8 compressed) · UploadBlock · IUploadArena · BlockFormats · VideoFrameLease.Staged
                Model: MediaItem.Color (ColorOverride)
                       ▲ implements                                      ▲ implements
 PlayMap.Render  GlDevice.Compressed (BC textures, persistent buffers; GlCaps)   PlayMap.Media  Hap/HapVideoSource
                 Video/UploadArena (IUploadArena)  Video/LeaseRetention (fences)                  Hap/HapReader (I/O)
                 Video/VideoTextureSet (+compressed branch)  Video/HapPass ("hap")                Hap/HapWorkers (chunks)
                 Encode/GlHapEncoder (IHapBlockEncoder) · Encode/EncoderPrograms                  Hap/HapSecondStage (Snappier)
                 Encode/BlockReadback (integer PBO ring)                                         Hap/HapPacker · HapMuxer
                                                                                                 Hap/HapRewriter · HapProbe
 PlayMap.Engine  Rendering/RenderHost (makes the arena, hands it and the caps to Media) · Rendering/HapFacts (STATE rows)
                 EngineCore.Hap (MEDIA INSPECT)
```

Module rules hold:
- Core holds the format and the contracts and has no native code.
- Media owns the files: demux, Snappy, muxing. It never calls GL.
- Render owns textures and shaders, and never calls FFmpeg.
- The encoder seam lets the `optimise` runner, which lives in Engine, join Media's decode and mux to Render's block encoder.

### 3.2 Threads and data flow (decode)

```
 disk ─► [read thread, one per source]                      [decode thread, one per source]          [render thread]
          MediaInput (libavformat)                           rent FrameSlot (arena block or native)   Acquire(mediaNs)
          av_read_frame → AVPacket ref (no copy)             fan the frame's chunks out to HapWorkers ├ staged: glCompressedTexSubImage2D
          HapFrame.TryParse (validates, chunk table)  ─ring─►  (and works chunks itself while waiting)  │   from the arena PBO offset; fence;
          read-ahead: ≤ HapReadAheadMs and                   Snappier.Decompress / memcpy per chunk   │   LeaseRetention holds the lease
            ≤ HapReadAheadMb of packets                      all chunks done → FrameQueue.Publish    ├ native: PixelUnpackRing copy → upload
                                                                                                     └ `hap` pass → RGBA16F Picture
```

1. **Read thread** (`hap read <file>`):
   - Opens `MediaInput`, `KeepOnly(video)`, and reads packets.
   - Each packet is parsed into a pooled `HapFrame`, which costs nothing: it only checks lengths and tables. A refused packet becomes a "refused" ring entry carrying its reason, so the decode thread can count it at its time.
   - The ring holds **AVPacket references** (`av_packet_move_ref`). There is no managed copy, and the decode stage reads `packet->data` directly.
   - Read-ahead stops at `HapReadAheadMs` (250) or `HapReadAheadMb` (192), whichever comes first. At 4K60 noisy HAP Q (8.2 MB per frame) the time cap binds first, at 15 frames (≈ 123 MB); the byte cap binds only above 12.8 MB per frame at 60 fps (8K, or HAP R/HDR at 4K and above).
   - On a seek it flushes the ring and calls `avformat_seek_file(fmt, s, ts, ts, ts, AVSEEK_FLAG_ANY)`. Every HAP frame is a keyframe, so the first packet read is the target's.
2. **Decode thread** (`hap decode <file>`):
   - Its loop has the shape of `FfmpegVideoSource.Loop` (`TakeSeek`, `WantMore`, `HasRoom`, loop restart). The shared timing helpers move into `Frames/ClipClock.cs` with behaviour unchanged (`Snap`, the loop-iteration maths, stale-frame rules).
   - It takes the next ring entry. A frame wholly before the play head is **dropped without decompressing**. That is the cheapest possible catch-up: no decoder state to keep warm.
   - Otherwise it rents a slot sized by `FrameLayout.For(layout, w, h)` and posts the frame's chunks to `HapWorkers`.
   - It helps by taking chunks off its own frame while waiting, then publishes. Frames publish in order, because the thread finishes one frame before taking the next.
3. **`HapWorkers`** is one per process:
   - `NodeSettings.HapWorkers` threads (0 = cores − 1, at least 1), named `hap worker n`, normal priority. The optimise worker runs in its own process (optimise §3.2), so it never competes in this pool.
   - The queue is a bounded MPMC ring of `ChunkJob` structs: `(byte* src, int srcLen, byte* dst, int dstLen, HapCompressor c, FrameCountdown* done)`. There is no allocation per job.
   - A failed chunk (Snappy error, or a length different from the preamble) marks the frame corrupt.
4. **Corrupt frame.** The slot goes back unpublished. The source counts it (`HapFacts.Corrupt`, `SourceHealth.FramesDropped`) and records the reason. The renderer holds the last picture, as it does for any source that answers nothing new. After 10 corrupt frames in a row, the status reads "corrupt: <reason> (frames 1201–1210)". Playback carries on at the next good frame.
5. **Render thread.** `VideoTextureSet.Update(lease)`:
   - **Staged lease** (its slot lives in the arena): bind the arena's buffer and call `glCompressedTexSubImage2D(..., (void*)offset)` per plane. Then run the `hap` pass, insert one fence and hand the lease to `LeaseRetention`.
   - **Native lease**: `PixelUnpackRing.UploadCompressed` (map an orphaned buffer, memcpy, upload), then the pass. The lease is disposed at once, as today.
   - `LeaseRetention.Collect()` runs once per frame. It disposes leases whose fences have signalled, so their slots return to the pool.

**Why one decode thread per source plus a shared pool.** Measured here (N1), chunk parallelism helps only modestly on this box (2.83 → 2.01 ms), because memory bandwidth is low (N2). A show machine has 3–6× the bandwidth [E]. A per-source thread keeps frames ordered and seeks simple. The pool lets four 4K sources share every core without each holding four threads of its own.

### 3.3 The parser (Core, `HapFrame`)

```
TryParse(packet, codedW, codedH, expected HapCodec) → bool, HapRefusal
  header(p): 3-byte LE size + type; if size == 0 → 4-byte LE size at +4 (header 8)  [V spec]
  top type: 0x0D → up to two inner image sections (HapM: 0xxF + 0xx1, sizes must match) [V spec]
            else  → one image section
  image section type = (stage << 4) | texture
     stage 0xA: one raw chunk        stage 0xB: one Snappy chunk
     stage 0xC: Decode Instructions Container (0x01) then frame data
        0x02 compressor table (u8 per chunk: 0x0A raw | 0x0B snappy | other → refused, 0xD0 named "GDeflate")
        0x03 size table (u32 LE)      0x04 offset table (u32 LE, optional: else running sum)
        unknown sub-sections skipped; counts equal across tables; ≤ 256 chunks
  per chunk: source range inside the section; target length = Snappy preamble varint (SnappyLength), or source
             length for raw; target offsets are the running sum (reference decoder) [V hap.c]
  totals:   Σ target = ⌈w/4⌉·⌈h/4⌉·blockBytes(texture)  (FFmpeg's own check) [V hapdec.c]
  texture vs fourcc: Hap1→0xB, Hap5→0xE, HapY→0xF, HapM→0x0D{0xF,0x1}, HapA→0x1, Hap7→0xC, HapH→0x2|0x3
```

Refusals are an enum with stable words. Examples:
- `Truncated` → "frame 812 is 3 bytes shorter than its header says".
- `SizeMismatch` → "frame 812 decodes to 8,294,000 bytes; 3840×2160 BC3 needs 8,294,400".
- `NotThisCodec` → "frame 812 holds BC7 blocks but the file says HAP Q".
- `NonStandardCompressor` → "frame 812 uses compressor 0xD0 (GDeflate), which is not part of the HAP spec".

`HapFrame` keeps fixed arrays (2 textures, 512 chunk entries) and refills them per parse, so a parse allocates nothing.

### 3.4 Staging memory: the upload arena

- **What it is.** At render start, `UploadArena` makes one buffer with `glBufferStorage(GL_PIXEL_UNPACK_BUFFER, UploadArenaMb, null, MAP_WRITE_BIT | MAP_PERSISTENT_BIT | MAP_COHERENT_BIT)` and maps it once with the same flags.
  - The extension is `GL_ARB_buffer_storage` on Core330, or GL ≥ 4.4. It is `GL_EXT_buffer_storage` on Es300; Mesa ES 3.2 lists it [V present.md §3.2]. ANGLE on D3D11 is [U].
  - The pointer stays valid for the context's life. Writes from any thread are visible to GL commands issued after a release-ordered publish. N3 proves this bit-exact on llvmpipe.
- **Allocator.** A first-fit free list of 256-byte-aligned blocks behind one `Lock`. Renting and returning are O(blocks), with at most 64 blocks live. `TryRent` answers false when the arena is full.
- **Who rents.** A HAP source's `FramePool` gets an `IFrameMemory` adapter over the arena:
  - At its first `Prepare`, each slot rents one block of `FrameLayout.TotalBytes` and keeps it for the source's life. On `FreeBuffer` the block goes back.
  - The slot records `Staged = UploadBlock(arena, offset, bytes, ptr)`. `MediaFrameLease.Staged` and `RemappedLease.Staged` forward it.
  - A slot that cannot rent uses native memory. The source then reports `staging: "copy (arena full)"` and still plays.
- **Reuse safety.** A staged lease is never disposed by the resolver: it goes to `LeaseRetention` with a fence. Only when `glClientWaitSync(fence, 0, 0)` reports signalled does the lease dispose, so the slot returns to the pool and the decode thread may write into it again. Code comment: "a staged slot returns only after the GPU read it".
- **Sizing.** A 4K HAP Q source uses `HapQueueFrames` (4) + 2 retained + 1 being written = 7 slots × 8.3 MB = **58 MB**. A HAP Q Alpha source uses 87 MB. The 256 MB default carries four 4K HAP Q layers fully staged. `UploadArenaMb` raises it, and `STATE.decode.platform.blocks.arenaFreeMb` shows the headroom.
- **Device or context loss.** `Epoch` moves. Sources holding blocks of an old epoch reopen on their next open, the same rule as zerocopy's `DeviceEpoch`. Until then they use native memory.

### 3.5 The `hap` pass (render, one draw per new frame)

Every HAP layout passes through one fragment program into the set's RGBA16F target at the **visible** size. S2 removes the pass where nothing needs transforming.

```glsl
// ShaderLibrary "hap" = (FullscreenVs, HapFs)
uniform sampler2D uTex0;      // BC1/BC3/BC7/BC6H, or YCoCg-BC3 (HAP Q)
uniform sampler2D uTex1;      // BC4 alpha plane (HAP Q Alpha)
uniform int   uMode;          // 0 rgba · 1 ycocg · 2 ycocg + alpha plane · 3 alpha-only (grey) · 4 hdr
uniform vec2  uHdr;           // x: scale to SDR white (1.0 = 203 nits), y: transfer (0 linear, 1 PQ, 2 HLG)
out vec4 oColor;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);                 // target = visible size: padding texels are never read
    vec4 t = texelFetch(uTex0, p, 0);                 // exact texel: no filtering across YCoCg blocks
    if (uMode == 1 || uMode == 2) {
        float s  = t.b * (255.0 / 8.0) + 1.0;         // scale stored as (s-1)<<3, s ∈ {1,2,4}  [V texturedsp.c maths]
        float co = (t.r - 0.50196078) / s, cg = (t.g - 0.50196078) / s, y = t.a;
        vec3 rgb = vec3(y + co - cg, y + cg, y - co - cg);
        float a  = uMode == 2 ? texelFetch(uTex1, p, 0).r : 1.0;
        oColor = vec4(clamp(rgb, 0.0, 1.0), a);
    } else if (uMode == 3) {
        oColor = vec4(t.rrr, 1.0);                     // as FFmpeg: HapA → gray8
    } else if (uMode == 4) {
        vec3 lin = uHdr.y == 1.0 ? pqToLinear(max(t.rgb, 0.0)) / 203.0
                 : uHdr.y == 2.0 ? hlgToLinear(max(t.rgb, 0.0)) / 203.0
                 : max(t.rgb, 0.0) * uHdr.x;
        oColor = vec4(pow(clamp(toneMap(lin * 203.0), 0.0, 1.0), vec3(1.0 / 2.4)), 1.0);  // the yuv path's own curve
    } else {
        oColor = t;                                    // HAP, HAP Alpha, HAP R: straight alpha [U on the spec's word]
    }
}
```

- `pqToLinear`, `hlgToLinear` and `toneMap` move from `YuvFs` into a shared GLSL include, so both programs use one copy and the CPU twin tests hold both.
- Orientation: blocks upload with row 0 at the top, and the target is top-row-first by the renderer's convention. So `texelFetch(gl_FragCoord)` is exact, with no flip.
- The pass costs one fullscreen draw at 4K per new frame. The GPU cost is about 0.1–0.3 ms on a show GPU [E]. A repeated generation is skipped, as today.

### 3.6 Formats

| FourCC | Name | Section type(s) | `PixelLayout` (new) | Block B | Core330 internal format, extension | Es300 (ANGLE / Mesa ES) | DXGI (for present's L2) | `hap` mode |
|---|---|---|---|---|---|---|---|---|
| `Hap1` | HAP | `0xAB/BB/CB` | `Bc1` | 8 | `0x83F0` `COMPRESSED_RGB_S3TC_DXT1_EXT`, `GL_EXT_texture_compression_s3tc` | same enum: `EXT_texture_compression_s3tc`, or `EXT_texture_compression_dxt1` [U on ANGLE] | `BC1_UNORM` (71) | 0 |
| `Hap5` | HAP Alpha | `0xAE/BE/CE` | `Bc3` | 16 | `0x83F3` `COMPRESSED_RGBA_S3TC_DXT5_EXT` | `EXT_texture_compression_s3tc`, or `ANGLE_texture_compression_dxt5` [U] | `BC3_UNORM` (77) | 0 |
| `HapY` | HAP Q | `0xAF/BF/CF` | `Bc3YCoCg` | 16 | `0x83F3` | as above | `BC3_UNORM` | 1 |
| `HapM` | HAP Q Alpha | `0x0D`{`0x?F`, `0x?1`} | `Bc3YCoCgBc4` (2 planes) | 16 + 8 | `0x83F3` + `0x8DBB` `COMPRESSED_RED_RGTC1` (core 3.0) | + `EXT_texture_compression_rgtc` | `BC3` + `BC4_UNORM` (80) | 2 |
| `HapA` | HAP Alpha-Only | `0xA1/B1/C1` | `Bc4` | 8 | `0x8DBB` | `EXT_texture_compression_rgtc` | `BC4_UNORM` | 3 |
| `Hap7` | **HAP R** | `0xAC/BC/CC` | `Bc7` | 16 | `0x8E8C` `COMPRESSED_RGBA_BPTC_UNORM`, `ARB_texture_compression_bptc` or GL ≥ 4.2 | `EXT_texture_compression_bptc` | `BC7_UNORM` (98) | 0 |
| `HapH` | **HAP HDR** | `0xA2/B2/C2` (unsigned) or `0xA3/B3/C3` (signed) | `Bc6hUfloat` / `Bc6hSfloat` | 16 | `0x8E8F` / `0x8E8E` `COMPRESSED_RGB_BPTC_(UN)SIGNED_FLOAT` | `EXT_texture_compression_bptc` | `BC6H_UF16` (95) / `BC6H_SF16` (96) | 4 |

Sources:
- the section types, fourccs and GL and DXGI values are from codecs.md §2.3, §2.6 and §2.7 [V];
- on llvmpipe, all five families upload and read back in a 3.3 core context (codecs.md §4C [M]), and the ES 3.2 profile lists BPTC, RGTC and S3TC (present.md §3.2 [M]);
- ANGLE-on-D3D11's exposure of each family is [U]: lab test 1 (§7).

**`FrameLayout` for compressed layouts.** Plane `Width` and `Height` count **4×4 blocks**: ⌈w/4⌉ and ⌈h/4⌉. `RowBytes = blocksW × blockBytes`. **Stride = RowBytes**, with no 64-byte alignment, because HAP's chunks fill a plane contiguously and `glCompressedTexSubImage2D` takes tight rows. The texture is allocated at the coded size, 4·⌈w/4⌉ × 4·⌈h/4⌉. The `hap` target is the visible size, so odd sizes need no re-encode.

**Capability (`GlCaps`, the spine's record, `SPINE.md` §6.4; read once per context).** It holds `Dxt1` and `Dxt5` (S3TC gives both; `EXT_texture_compression_dxt1` only DXT1; `ANGLE_texture_compression_dxt5` DXT5), `Rgtc`, `Bptc`, `Norm16` and the float flags, read with `GL_NUM_EXTENSIONS` and a `glGetStringi` loop per dialect as in the table. The desktop version stands in for RGTC (3.0) and BPTC (4.2); on ES they need `EXT_texture_compression_rgtc`/`_bptc`. The render engine turns it into the `BlockFormats` flags (`Bc1` from Dxt1, `Bc3` from Dxt5, `Bc4`, `Bc7`, `Bc6h`, `PersistentStaging`). The render host passes the caps to Media as `BlockFormats` flags (`IMediaBackend.UseRenderer`). A HAP item whose format the node lacks gets:
- **BC1/BC3/BC4 families**: the FFmpeg CPU path (today's `FfmpegVideoSource`), with `why = "this GPU's GL lacks S3TC; FFmpeg decodes on the CPU"`;
- **BC7/BC6H**: readiness `decodable: false`, with words and the fix: "HAP R needs BPTC textures, which this GPU's GL does not offer — optimise to HAP Q for this node". N6 shows a CPU BC7 decode is 427 ms per 4K frame, so there is no CPU route worth offering.

### 3.7 HAP HDR: an explicit interpretation

The spec carries no colour metadata (codecs.md §2.10). PlayMap decides in this order, and STATE always says which rule applied:
1. **`MediaItem.Color`** (the operator's override): `transfer` = `linear` | `pq` | `hlg`, and `whiteNits`.
2. **The container's `colr` nclx**: `AVCodecParameters.color_trc`. `AVCOL_TRC_LINEAR` (8) means linear. `SMPTE2084` and `ARIB_STD_B67` mean PQ- or HLG-encoded floats, which TouchDesigner users sometimes write [U].
3. **PlayMap's metadata tag** `playmap_white_nits`, which PlayMap writes into the stream metadata of every HAP HDR file it creates.
4. **The default:** linear, with 1.0 = 203 nits, the BT.2408 reference white. TouchDesigner documents HAP HDR as values that "can go outside the 0–1 range" (codecs.md §2.10). That means 1.0 is SDR white.

Signed BC6H (`0x?3`) values below 0 are clamped to 0 in the pass. Wide-gamut negative values are a later item with R2.5.

### 3.8 The encoder

**Seam (Core).**
- `IHapBlockEncoder.EncodeAsync(picture, target, quality)` turns one picture into `HapBlocks`: pooled native planes, one or two.
- Render implements it as `GlHapEncoder(GlDevice)`, so one encoder works on any context:
  - the **optimise worker's own context** (optimise §3.2);
  - the **render context** for effect bakes and captures;
  - **llvmpipe** in CI.

**Per frame, on the encoder's GL thread:**
1. **Picture in.** The picture lease (YUV planes, RGBA or half floats) goes through the existing `VideoTextureSet`, which converts YUV to RGBA16F with the `yuv` program.
   - For the HDR target, the set runs with `OutputTransfer.LinearScene`, a new `yuv` uniform `uOutput = 1`. The pass then writes linear light with 1.0 = 203 nits instead of tone-mapping. It is the inverse of §3.7's default.
   - An `RgbaHalf` source, such as EXR, is already linear.
2. **Program.** `hapenc.bc1` / `.bc3` / `.ycocg` / `.bc4` / `.bc7fast` / `.bc6hfast` draws into an integer target of ⌈w/4⌉ × ⌈h/4⌉ texels: `RG32UI` for 8-byte blocks, `RGBA32UI` for 16-byte ones. Each fragment fetches its 16 texels with `texelFetch`, clamped at the edge, so padding blocks repeat the edge.
   - HAP Q Alpha runs `.ycocg` and `.bc4` into two targets.
   - The **algorithms** are the ones N5 measured: PCA by four power-iteration steps; extremes along the axis; one least-squares refinement on the four-entry (or 16-entry) palette; nearest-entry indices; the endpoint swap that keeps the 4-colour mode (`idx ^= 0x55555555`).
   - **YCoCg:** the scale is chosen per block from {1, 2, 4} by the CoCg extent, Co goes in R5 and Cg in G6, the scale in B5 as `s−1`, and Y in an 8-level alpha block. **Before M7 closes**, the Y endpoints get the same least-squares refinement. N5 shows −3.1 dB on flat graphics without it.
   - **BC7 fast:** mode 6, one subset, RGBA 7-bit endpoints plus a p-bit chosen per endpoint by error, 4-bit indices with the anchor fix-up. **BC6H fast:** mode 11, from GPURealTimeBC6H's fast path.
3. **Tiles.** Each draw covers a scissor tile of at most `T × T` blocks, where T starts at 256 (a 1024² pixel region), followed by `glFlush`. A `GpuTimer` query per tile adapts T so a tile stays at or under 50 ms of GPU time. A TDR on Windows is 2 s by default. This bounds every submission whatever the encoder's cost per block.
4. **Readback.** `BlockReadback` is a 3-slot ring: `glReadPixels(…, GL_RG_INTEGER | GL_RGBA_INTEGER, GL_UNSIGNED_INT, offset)` into pack buffers, a fence each, mapped once signalled. This mirrors `PixelPackRing`, but with integer formats and no BGRA. The bytes are BCn blocks in row order: plane bytes, little-endian per `uint`.
5. **Second stage (`HapPacker`, CPU, the worker pool).**
   - `HapChunking.Plan(blocks, blockBytes, wanted)` takes the largest count ≤ `wanted` that divides the block count, as the Vidvox reference encoder does. If none ≥ `wanted/2` exists, it uses near-equal chunks on block boundaries.
   - `wanted` is `HapEncodeSettings.Chunks`. 0 means auto: 16 at ≥ 3840 wide, 8 at ≥ 1920, 4 below. `optimise` passes the target nodes' core counts.
   - Each chunk is compressed with Snappier `Snappy.Compress`. If the result saves less than **`RawBelowGain`** (default 10 %), the chunk is stored raw (`0x0A`). The reference stores raw only when Snappy does not shrink the chunk at all; we drop compression where it barely helps. N7's noisy clip (98 %) becomes raw, which saves decode CPU at no disk cost.
   - Policies: `SecondStage.Auto` (per-chunk choice), `Snappy` and `None` (all raw: TouchDesigner's "no secondary compression").
6. **Frame writer and muxer.**
   - `HapFrameWriter` writes the decode-instructions container, with the offset table always present, and then the chunks.
   - `HapMuxer` (Media, libavformat):
     - `avformat_alloc_output_context2(…, "mov", path)` and one video stream with `codec_id = AV_CODEC_ID_HAP` and `codec_tag = MKTAG('H','a','p','7')` (and so on);
     - the frame rate and time base copied from the source;
     - `color_*` fields for `colr`; HAP HDR also gets the `playmap_white_nits` metadata;
     - `fmt->strict_std_compliance = FF_COMPLIANCE_UNOFFICIAL` for `Hap7`/`HapH`, which codecs.md §3.4 [M] proved necessary and sufficient;
     - audio stream-copied when the mov muxer takes the codec (AAC, PCM, ALAC). Otherwise it is decoded and written as PCM s24le through FFmpeg's encoder.
   - The muxer writes packets with `av_interleaved_write_frame` and the source's pts/dts. HAP is intra-only, so dts = pts.

**Encoder quality ladder** (the names the operator sees):

| Quality | HAP / HAP Alpha / HAP Q / HAP Q Alpha / Alpha-Only | HAP R | HAP HDR | Where it runs |
|---|---|---|---|---|
| **Fast** | the M7 fragment encoders (N5: FFmpeg-class quality) | BC7 mode 6 (N5: valid; ≈ HAP Q on opaque content) | BC6H mode 11 | any GL 3.3 / ES 3.0 context, llvmpipe included |
| **Show** | the Fast encoders plus 2 refinement passes and a cluster-fit index pass [E: +0.3–0.8 dB] | **S1**: BC7 modes 0–7 with partition search (DirectXTex port) | **S1**: GPURealTimeBC6H quality | GL ≥ 4.3 or ES 3.1 compute contexts; refused with words elsewhere |
| **Archive** | Show plus exhaustive endpoint search | S1 with every partition and rotation tried | S1 quality | same, hours per minute of 4K; meant for farm jobs |

### 3.9 The lossless rewriter (`HapRewriter`, Media)

The rewriter remuxes a HAP file with libavformat. For each packet it parses with `HapFrame`, undoes the second stage into a pooled buffer, then re-plans the chunks and re-applies the second-stage policy through `HapPacker`, and writes the packet. Audio and other streams are copied with `-c copy` semantics, and so are metadata and `colr`.

Operations:
- `rechunk <n|auto>`;
- `strip` (all raw);
- `snappy` (auto, per chunk);
- `retag`: only for the known mislabel, a `Hap5` file whose sections are `0xF` (YCoCg). That is a refusal today, and the rewriter can fix the tag when the operator asks.

The pixels are untouched: the test asserts byte-equal planes per frame. On this box it runs at disk speed with about 2–15 ms CPU per 4K frame (N1 + N7) [E].

### 3.10 Performance budgets

| Step | Budget | Basis |
|---|---|---|
| Read thread CPU per frame | ≤ 0.3 ms (parse + packet ref) | parse is table checks [E]; `av_read_frame` copy is I/O-bound |
| Decode stage per 4K HAP Q frame, p95 | ≤ 6 ms here; ≤ 3 ms on a show machine | N1: 2.0–4.5 ms here [M]; native 2.0–2.3 ms (§4B) [M] |
| GL thread per new frame, arena | ≤ 0.3 ms on a GPU [E]; ≤ 3.5 ms on llvmpipe [M N3] | one compressed sub-image call and one fence. llvmpipe copies on the CPU |
| GL thread per new frame, copy path | + one memcpy of the plane: 2.97 ms here [M N2]; about 0.6–1 ms at 8–14 GB/s [E] | — |
| `hap` pass, GPU | ≤ 0.3 ms at 4K [E] | one draw |
| Bytes to the GPU per 4K frame | 4.15 MB (HAP, HAP R/HDR at 1 B/px: 8.3 MB), 12.4 MB (HAP Q Alpha) | codecs.md §2.7 |
| Allocation | 0 bytes per frame on read, decode and render threads after warm-up | `DecodeThreadAllocatedBytes`, `FramePathAllocatedBytes` |
| Seek to any frame | ≤ one frame period + one read | intra-only |
| Encoder, Fast, 4K, GPU | ≤ 3 ms per frame for HAP Q on a mid-range GPU [E]: llvmpipe on 4 cores takes 35–56 ms (N5), and a GPU has 20–50× llvmpipe's shader throughput [E] | lab |
| Encoder second stage, 4K HAP Q | ≤ 5 ms wall on 4 cores (16 chunks; N7: 15 ms on 1 core) | [M]/[E] |

## 4. Spine needs

### 4.1 Core (new files `src/PlayMap.Core/Media/Hap/*.cs`, `src/PlayMap.Core/Media/UploadArena.cs`)

```csharp
namespace PlayMap.Core.Media.Hap;

/// <summary>The texture nibble of a HAP section type (codecs.md §2.3). Values are the spec's.</summary>
public enum HapTexture : byte { Bc4Alpha = 0x01, Bc6hUnsigned = 0x02, Bc6hSigned = 0x03, Bc1Rgb = 0x0B, Bc7Rgba = 0x0C, Bc3Rgba = 0x0E, Bc3YCoCg = 0x0F }

public enum HapCompressor : byte { None = 0x0A, Snappy = 0x0B }

/// <summary>The seven HAP codecs by fourcc.</summary>
public enum HapCodec { Hap, HapAlpha, HapQ, HapQAlpha, HapAlphaOnly, HapR, HapHdr }

public static class HapCodecs
{
    public static bool TryFromTag(uint fourcc, out HapCodec codec);         // 'Hap1','Hap5','HapY','HapM','HapA','Hap7','HapH'
    public static uint Tag(HapCodec codec);
    public static string Name(HapCodec codec);                              // "hap_q_alpha" (MediaInfo.VideoCodec)
    public static int BlockBytes(HapTexture t);                             // 8 for BC1/BC4, 16 otherwise
    public static PixelLayout Layout(HapCodec codec, HapTexture first);     // HapH → Bc6hUfloat | Bc6hSfloat by section
    public static bool Allowed(HapCodec codec, HapTexture t, int index);   // fourcc ↔ section check
}

public readonly record struct HapChunk(int SourceOffset, int SourceLength, HapCompressor Compressor, int TargetOffset, int TargetLength);
public readonly record struct HapTextureInfo(HapTexture Kind, int ChunkStart, int ChunkCount, int Bytes);

public enum HapRefusal { None, Truncated, BadHeader, UnknownTexture, NotThisCodec, ChunkTables, ChunkOutOfBounds,
    SizeMismatch, TooManyChunks, NonStandardCompressor, BadSnappyLength, Empty }

/// <summary>One parsed frame. Owned by one thread and refilled by each parse: no allocation per frame.</summary>
public sealed class HapFrame
{
    public const int MaxChunks = 256;
    public int TextureCount { get; }
    public HapTextureInfo Texture(int i);
    public ReadOnlySpan<HapChunk> Chunks(int texture);
    public int RawChunks { get; }                      // for STATE's "second stage" mix
    public bool TryParse(ReadOnlySpan<byte> packet, int width, int height, HapCodec expected, out HapRefusal refusal, out int detail);
    public static string Words(HapRefusal refusal, long frame, int detail);
}

public static class SnappyLength { public static bool TryRead(ReadOnlySpan<byte> chunk, out uint length, out int preambleBytes); }

public readonly record struct HapChunkOut(int Offset, int Length, HapCompressor Compressor);
public static class HapChunking
{
    /// <summary>Chunk boundaries on block boundaries: the largest count ≤ wanted dividing the block count (the reference
    /// encoder's rule), else near-equal chunks.</summary>
    public static int Plan(int blocks, int blockBytes, int wanted, Span<HapChunkOut> uncompressed);
}
public static class HapFrameWriter
{
    public static int Measure(int textures, ReadOnlySpan<int> chunkCounts, ReadOnlySpan<HapChunkOut> chunks);
    public static int Write(Span<byte> frame, HapTexture first, ReadOnlySpan<HapChunkOut> chunks1, ReadOnlySpan<byte> data1,
        HapTexture? second = null, ReadOnlySpan<HapChunkOut> chunks2 = default, ReadOnlySpan<byte> data2 = default);
}

// --- encoding (HapEncoding.cs) ---
public enum HapTarget { Hap, HapAlpha, HapQ, HapQAlpha, HapAlphaOnly, HapR, HapHdr }
public enum EncodeQuality { Fast, Show, Archive }
public enum SecondStagePolicy { Auto, Snappy, None }
public sealed record HapEncodeSettings(HapTarget Target, EncodeQuality Quality)
{
    public int Chunks { get; init; }                         // 0 = auto (16 ≥ 3840 wide, 8 ≥ 1920, else 4)
    public SecondStagePolicy SecondStage { get; init; } = SecondStagePolicy.Auto;
    public double RawBelowGain { get; init; } = 0.10;        // store a chunk raw when Snappy saves less than this
    public string AlphaFrom { get; init; } = "alpha";        // HapAlphaOnly: "alpha" | "luma"
    public bool HdrSigned { get; init; }                     // HapHdr: BC6H SF16 instead of UF16
    public double HdrWhiteNits { get; init; } = 203;
}
/// <summary>Blocks of one picture: one plane, or two for HAP Q Alpha. Pooled; dispose returns the buffers.</summary>
public sealed class HapBlocks : IDisposable
{
    public int Planes { get; }
    public HapTexture Kind(int plane);
    public ReadOnlySpan<byte> Plane(int plane);
    public int BlocksWide { get; } public int BlocksHigh { get; }
    public void Dispose();
}
public interface IHapBlockEncoder : IAsyncDisposable
{
    /// <summary>"gl-frag/1 · llvmpipe (LLVM 20.1.2)": goes into optimise's recipe key and the quality report.</summary>
    string Id { get; }
    bool Supports(HapTarget target, EncodeQuality quality, out string why);
    ValueTask<HapBlocks> EncodeAsync(VideoFrameLease picture, HapEncodeSettings settings, CancellationToken ct);
}
```

```csharp
namespace PlayMap.Core.Media;   // UploadArena.cs

/// <summary>Block-compressed texture families the renderer can sample on this node.</summary>
[Flags] public enum BlockFormats { None = 0, Bc1 = 1, Bc3 = 2, Bc4 = 4, Bc7 = 8, Bc6h = 16, PersistentStaging = 32 }

/// <summary>A block of GPU-visible host memory (a persistently mapped pixel-unpack buffer) a producer writes a
/// picture into from any thread; the renderer uploads straight from it. Valid until returned.</summary>
public readonly record struct UploadBlock(int Arena, long Offset, long Bytes, nint Pointer, long Epoch)
{
    public bool IsEmpty => Bytes == 0;
}

public interface IUploadArena
{
    long Epoch { get; }                    // moves when the arena is rebuilt; blocks of older epochs are void
    long CapacityBytes { get; }
    long FreeBytes { get; }
    bool TryRent(long bytes, out UploadBlock block);   // thread-safe; false when full
    void Return(in UploadBlock block);                 // thread-safe; the caller's lease has passed its fence
}
```

**Hunks in existing Core files** (additive, so files and wire stay compatible):

```csharp
// VideoFrames.cs — appended at the END of PixelLayout (numeric values of existing members unchanged)
    /// <summary>Block-compressed (HAP). Plane Width/Height count 4×4 blocks; Stride = blocks × block bytes (tight).</summary>
    Bc1, Bc3, Bc3YCoCg, Bc3YCoCgBc4, Bc4, Bc7, Bc6hUfloat, Bc6hSfloat,

// VideoFrameLease
    /// <summary>Where the planes live when they were written into the renderer's upload arena (empty otherwise).
    /// A staged lease is disposed only after the GPU has read it.</summary>
    public virtual UploadBlock Staged => default;

// SourceHealth (record body; init properties, the positional shape is unchanged)
    public HapFacts? Hap { get; init; }

/// <summary>What a HAP source measured (STATE.decode.sources[].hap).</summary>
public sealed record HapFacts(string Codec, string Texture, int Chunks, double RawShare, string SecondStage,
    double StageMsP95, long UploadBytes, string Staging, long Corrupt, string LastRefusal, string Hdr);
```

```csharp
// Model/MediaModel.cs — MediaItem record body (init property; old files read as Auto)
    /// <summary>How to read this item's colour when the file's own tags are missing or wrong (HAP HDR has none).</summary>
    public ColorOverride Color { get; init; } = ColorOverride.Auto;

public sealed record ColorOverride(string Transfer, double WhiteNits, string Range, string Matrix)
{
    public static readonly ColorOverride Auto = new("auto", 0, "auto", "auto");   // 0 = the rule chain in §3.7
}
```

### 4.2 Media options and the engine seam

```csharp
// PlayMap.Media/MediaLibrary.cs — MediaSourceOptions
public HapSourceOptions Hap { get; init; } = HapSourceOptions.Default;

public sealed record HapSourceOptions
{
    public bool Enabled { get; init; } = true;                 // NodeSettings.HapDecode == "gpu"
    public IUploadArena? Arena { get; init; }
    public BlockFormats Formats { get; init; } = BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;  // until the renderer says
    public int QueueFrames { get; init; } = 4;
    public int ReadAheadMs { get; init; } = 250;
    public int ReadAheadMb { get; init; } = 192;
    public static readonly HapSourceOptions Default = new();
}

// PlayMap.Engine/Seams.cs — IMediaBackend (default method: fakes and older backends compile unchanged)
    /// <summary>What the renderer offers decoders: the block formats it samples and its upload arena (null = none).
    /// Called by the render host when its context exists, and with (None, null) when it goes.</summary>
    void UseRenderer(BlockFormats formats, IUploadArena? arena) { }
```

### 4.3 Render

```csharp
// Gpu/GpuTypes.cs — TextureFormat, appended
    Bc1, Bc3, Bc4, Bc7, Bc6hUfloat, Bc6hSfloat,

// Gpu/GlDevice.Compressed.cs (new partial)
public BlockFormats BlockFormats { get; }                                                // from GlDevice.Caps, read once per dialect
public GpuTexture CreateCompressedTexture(int width, int height, TextureFormat format); // width/height rounded up to 4
public void UploadCompressed(GpuTexture texture, nint blocks, int bytes);               // glCompressedTexSubImage2D
internal void UploadCompressedFromUnpackBuffer(GpuTexture texture, uint buffer, nint offset, int bytes);
internal (uint Buffer, nint Pointer) CreatePersistentUnpackBuffer(long bytes);          // null pointer = not offered

// Video/UploadArena.cs     public sealed class UploadArena : IUploadArena, IDisposable   (render thread creates/destroys)
// Video/LeaseRetention.cs  public sealed class LeaseRetention : IDisposable { void Retain(VideoFrameLease l); void Collect(); }
// Video/VideoTextureSet.cs Update(lease) → bool; new: bool ReleaseOrRetain(VideoFrameLease lease)   // resolver calls it
// Encode/GlHapEncoder.cs   public sealed class GlHapEncoder : IHapBlockEncoder   (ctor: GlDevice; render-thread affine)
// GlDeviceOptions          public int PreferredVersion { get; init; } = 33;   // the encoder asks 46/45/43 for compute (S1)
```

`ShaderLibrary` gains the programs `hap`, `hapenc.bc1`, `hapenc.bc3`, `hapenc.ycocg`, `hapenc.bc4`, `hapenc.bc7fast` and `hapenc.bc6hfast` (S1 adds the compute sources). It also gains the shared include `ColorFns` (`pqToLinear`, `hlgToLinear`, `toneMap`). `yuv` gains `uOutput` (0 display SDR, as today; 1 linear scene, for HDR encode).

### 4.4 Verbs (`VerbCatalog`, category `Media`)

| Verb | Grammar | Risk | Mutates | Reply |
|---|---|---|---|---|
| `MEDIA INSPECT` | `MEDIA INSPECT <media> [ON <node>]` | Low | no | `OK {"media":"md…","codec":"hap_q","texture":"ycocg-bc3","width":3840,"height":2160,"oddSize":false,"frames":18000,"chunks":{"min":1,"max":1,"typical":1},"secondStage":{"snappy":0.96,"raw":0.04},"mbPerFrame":{"avg":0.71,"max":1.9},"mbPerSecond":42.6,"refused":0,"hdr":null,"budget":{"node":"a1b2c3d4","layers4k60":12,"limit":"upload","words":"upload 6.0 GB/s ÷ 498 MB/s per layer (8.3 MB of BC3 at 60 fps) = 12; disk 1.9 GB/s ÷ 114 MB/s at peak = 16; cores 4 × 16.7 ms ÷ 4.5 ms = 14"},"fixes":[{"words":"1 chunk: each layer's frame decodes on one core (4.5 ms of its 16.7 ms)","line":"MEDIA OPTIMISE md… TO rechunk"}]}`. The budget is min(disk ÷ peak MB/s per layer, cores × frame time ÷ decode ms per layer, upload ÷ texture MB/s per layer), and `limit` names the term that binds (`HapBudgetTests` asserts the arithmetic). `ON <node>` asks that peer, whose own facts make the budget. The inspection reads every packet header (a disk pass, cached by content hash in `NodeHome/inspect/`), so it is refused while armed |

The generic `SET md… color.transfer "pq"` and `SET md… color.whiteNits 100` edit the override (an existing verb, Review as SET is). The encoder has no verb of its own: `optimise` owns `MEDIA OPTIMISE … TO hap|hap-alpha|hap-q|hap-q-alpha|hap-alpha-only|hap-r|hap-hdr|rechunk|strip|snappy`.

### 4.5 STATE and context

Inside zerocopy's `STATE.decode`, with new keys at the end of each object. If zerocopy has not landed, this package lands that skeleton, `{"platform":{…},"sources":[…]}`, with just these keys:

```json
"decode":{ …,
 "platform":{ …, "blocks":{"bc1":true,"bc3":true,"bc4":true,"bc7":true,"bc6h":true,"staging":"persistent",
   "arenaMb":256,"arenaFreeMb":140,"words":""}},
 "sources":[{"key":"md1a2b3c4d","media":"md1a2b3c4d","name":"Facade loop","codec":"hap_q","path":"hap-gpu","zeroCopy":true,
   "decodeMsP95":2.1,"dropped":0,"late":0,"why":"",
   "hap":{"texture":"ycocg-bc3","chunks":16,"rawShare":0.0,"secondStage":"snappy","stageMsP95":2.1,"uploadMb":8.29,
          "staging":"persistent","pass":true,"corrupt":0,"lastRefusal":"","hdr":null}}]}
```

- `hap.hdr`: for HAP HDR, `"linear · white 203 nits (default)"`, `"pq (colr)"` or `"linear · white 100 nits (item)"`.
- `platform.blocks.staging`: `persistent` | `copy` | `none` (no renderer).
- **Context** (Show Bus §6): the zerocopy `decode` item covers HAP sources. It is **red** when an on-air HAP source refuses frames (with the first refusal's words) or when an item in the show is HAP R or HAP HDR on a node without BPTC. It is **amber** when an on-air source runs on the copy path at ≥ 4K: "arena full: raise UploadArenaMb or play fewer 4K layers".

### 4.6 NodeSettings (init properties with defaults, in `src/PlayMap.Core/Bus/Pairing.cs`)

```csharp
public string HapDecode { get; init; } = "gpu";      // gpu | ffmpeg (today's path, Hap1/5/Y/A/M only)
public int HapWorkers { get; init; }                  // 0 = cores − 1
public int HapQueueFrames { get; init; } = 4;
public int HapReadAheadMs { get; init; } = 250;
public int HapReadAheadMb { get; init; } = 192;
public int UploadArenaMb { get; init; } = 256;        // 0 = no arena (copy path)
```

## 5. Files

**New**

| File | What |
|---|---|
| `src/PlayMap.Core/Media/Hap/HapFormat.cs` | `HapTexture`, `HapCompressor`, `HapCodec`, `HapCodecs` |
| `src/PlayMap.Core/Media/Hap/HapFrame.cs` | the parser, `HapRefusal` and words, `SnappyLength` |
| `src/PlayMap.Core/Media/Hap/HapFrameWriter.cs` | writer and `HapChunking` |
| `src/PlayMap.Core/Media/Hap/HapEncoding.cs` | `HapTarget`, `EncodeQuality`, `HapEncodeSettings`, `HapBlocks`, `IHapBlockEncoder` |
| `src/PlayMap.Core/Media/Hap/HapBudget.cs` | S6 layer budget (pure) |
| `src/PlayMap.Core/Media/UploadArena.cs` | `BlockFormats`, `UploadBlock`, `IUploadArena` |
| `src/PlayMap.Media/Hap/HapVideoSource.cs` | the source (§3.2) |
| `src/PlayMap.Media/Hap/HapReader.cs` | read thread, packet ring |
| `src/PlayMap.Media/Hap/HapWorkers.cs` | the shared chunk pool (`ChunkJob`, countdown) |
| `src/PlayMap.Media/Hap/HapSecondStage.cs` | Snappier decompress and compress, raw policy |
| `src/PlayMap.Media/Hap/HapPacker.cs` | chunk plan + second stage + frame writer for the encoder |
| `src/PlayMap.Media/Hap/HapMuxer.cs` | libavformat writer (`Hap7`/`HapH` unofficial tags, `colr`, metadata, audio copy or PCM) |
| `src/PlayMap.Media/Hap/HapRewriter.cs` | M8 |
| `src/PlayMap.Media/Hap/HapProbe.cs` | codec names, first-frame proof, inspect facts |
| `src/PlayMap.Media/Frames/ClipClock.cs` | `Snap`, loop iteration and stale-frame rules shared with `FfmpegVideoSource` (moved, unchanged) |
| `src/PlayMap.Render/Gpu/GlDevice.Compressed.cs` | BC textures, `BlockFormats` from `GlCaps`, persistent buffers |
| `src/PlayMap.Render/Video/UploadArena.cs` | `IUploadArena` over a persistent PBO |
| `src/PlayMap.Render/Video/LeaseRetention.cs` | fence ring (shared with zerocopy: whichever package lands first adds it) |
| `src/PlayMap.Render/Video/HapPass.cs` | mode, uniforms, the `hap` draw |
| `src/PlayMap.Render/Encode/GlHapEncoder.cs`, `EncoderPrograms.cs`, `BlockReadback.cs` | M7 (S1 adds `Bc7ComputeEncoder.cs`, `Bc6hComputeEncoder.cs`) |
| `src/PlayMap.Engine/Rendering/HapFacts.cs` | the STATE rows and context words |
| `src/PlayMap.Engine/EngineCore.Hap.cs` | `MEDIA INSPECT` |
| tests: `tests/PlayMap.Core.Tests/Hap/HapParserTests.cs`, `HapWriterTests.cs`, `HapChunkingTests.cs`, `HapBudgetTests.cs`; `tests/PlayMap.Media.Tests/Hap/HapSourceTests.cs`, `HapInteropTests.cs`, `HapMuxerTests.cs`, `HapRewriterTests.cs`; `tests/PlayMap.Render.Tests/Hap/HapUploadTests.cs`, `UploadArenaTests.cs`, `HapEncoderTests.cs`, `HapPassTests.cs`; `tests/PlayMap.Engine.Tests/HapStateTests.cs`, `MediaInspectTests.cs` | §7 |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Media/VideoFrames.cs` | eight `PixelLayout` members at the end; `Staged`; `SourceHealth.Hap`; `HapFacts` |
| `src/PlayMap.Core/Model/MediaModel.cs` | `MediaItem.Color`, `ColorOverride` |
| `src/PlayMap.Core/Bus/Pairing.cs` | six NodeSettings init properties |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | the `MEDIA INSPECT` row |
| `src/PlayMap.Media/Frames/FramePool.cs` | `FrameLayout` compressed geometry (tight strides); `IFrameMemory` hook in `FrameSlot.Prepare`/`FreeBuffer`; `Staged` on the slot |
| `src/PlayMap.Media/Frames/MediaFrameLease.cs` | `Staged` override |
| `src/PlayMap.Media/MediaLibrary.cs` | `MediaSources.Open` routes HAP items (by `Info.VideoCodec`) to `HapVideoSource`; `MediaSourceOptions.Hap` |
| `src/PlayMap.Media/MediaProbe.cs`, `Ffmpeg/MediaInfoReader.cs` | HAP names; first-frame proof by parse plus second stage for HAP streams |
| `src/PlayMap.Media/Sources/FfmpegVideoSource.cs` | uses `ClipClock` (behaviour unchanged) |
| `src/PlayMap.Media/PlayMap.Media.csproj` | `Snappier` 1.3.1 |
| `src/PlayMap.Render/Gpu/GpuTypes.cs`, `GlDevice.cs` | compressed `TextureFormat`s; caps read at construction; `PreferredVersion` |
| `src/PlayMap.Render/Gpu/PixelBuffers.cs` | `PixelUnpackRing.UploadCompressed` |
| `src/PlayMap.Render/Video/VideoTextureSet.cs` | compressed branch, `hap` pass, staged upload, `ReleaseOrRetain` |
| `src/PlayMap.Render/Engine/SourceResolver.cs` | `using var lease` → `set.ReleaseOrRetain(lease)` (shared with zerocopy) |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | `hap`, `hapenc.*`, `ColorFns` include, `yuv` `uOutput` |
| `src/PlayMap.Engine/Seams.cs` | `IMediaBackend.UseRenderer` default method |
| `src/PlayMap.Engine/Host/MediaBackend.cs` | keep formats and arena; pass `HapSourceOptions`; readiness words for missing formats |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | make `UploadArena` and `LeaseRetention`; `UseRenderer(caps, arena)` |
| `src/PlayMap.Engine/Rendering/SourceLeases.cs` | `RemappedLease` forwards `Staged` |
| `src/PlayMap.Engine/StateWriter.cs` (or zerocopy's `DecodeState`) | the `hap` and `blocks` keys |
| `src/PlayMap.Engine/EngineCore.Router.cs` | `"MEDIA INSPECT" => MediaInspect(args, ct)` |
| `src/PlayMap.Engine/ContextBuilder.cs` | words in the `decode` item |
| `src/PlayMap.App/…` media inspector view and view model; `Link/FakeEngineLink.State.cs` | S5 |
| `docs/PLAYBACK.md` (HAP chapter), `docs/WIRE.md` (`MEDIA INSPECT`, STATE keys), `docs/ARCHITECTURE.md` (§2 Media row), `docs/ROADMAP.md` (R2.3 status) | papers |

## 6. Dependencies

**Work packages.**
- **zerocopy (R2.2).** Shares `DecodePath.HapGpu`, the `STATE.decode` object, `SourceHealth` init properties, `LeaseRetention` and the resolver change. Either package can land first. The second one reuses the first one's pieces, and the spine landing (task 10) merges them.
- **present (R2.1).** The `Es300` dialect for ANGLE. The BC formats on ANGLE come from its D3D11 format tables [U]: lab test 1. HAP needs no D3D11 code until present's L2.
- **optimise (R2.4).** The consumer of M7 and M8. It owns the `MEDIA OPTIMISE` verbs and the jobs.
- **effects.** Bakes a chain to HAP: `GlHapEncoder` on the render context, `HapPacker` and `HapMuxer`.
- **showsafety (R2.6/R3.5).** Reads `MEDIA INSPECT` budgets and `STATE.decode.platform.blocks` for preflight. Its `armed` flag refuses inspection disk passes.
- **companion.** Shows the decode chip variables (§8).

**Third party.**

| What | Version | Licence | How |
|---|---|---|---|
| Snappier | 1.3.1 (2026-04-28) | BSD-3-Clause [V NuGet] | NuGet, PlayMap.Media only. `Snappy.Decompress(ReadOnlySpan, Span)`, `GetUncompressedLength`, `Compress` |
| FFmpeg.AutoGen | 9.0.1.1 (in tree) | LGPL-3.0 bindings (already accepted) | demux and mux; `AVFormatContext.strict_std_compliance` |
| FFmpeg 9 shared libraries | user's or shipped as replaceable LGPL libs (R0.7) | LGPL/GPL by build | loaded at run time as today. **No FFmpeg code copied**; `hapdec.c` and `texturedsp*.c` were read only to learn the maths (codecs.md §9.8) |
| Silk.NET.OpenGL | 2.23.0 (in tree) | MIT | `CompressedTexSubImage2D`, `BufferStorage`, `MapBufferRange` |
| Vidvox HAP spec and reference | HEAD `d847f6b` | BSD-2 | the format; the notice is kept in `THIRD-PARTY-NOTICES.md` |
| DirectXTex `BC7Encode.hlsl`, `BC6HEncode.hlsl` (S1) | May 2026 release | MIT | ported to GLSL compute; notice kept |
| GPURealTimeBC6H (M7 fast, S1 quality) | master | MIT / public domain | ported; notice kept |
| BCnEncoder.NET | — | MIT | **not used.** N6 shows it is 100–1000× too slow for video. The GL readback is the test oracle |

## 7. Verification here

| Area | Real on this machine | Simulated, and how | Compile-only or lab |
|---|---|---|---|
| Parser and writer (M1) | **Yes**: conformance suite, round trips, zero-allocation check | — | — |
| Second stage and source (M2) | **Yes**: FFmpeg 9 makes the clips (`hap`, `hap_alpha`, `hap_q`; chunks 1–64; both compressors). Planes are byte-compared with the native-libsnappy prototype's dump. Timing and allocation asserts. Seek, loop and rate via the existing `VideoSourceTests` cases | corrupt packets made by flipping bytes and truncating | — |
| GL decode (M4) | **Yes**: llvmpipe on Xvfb :99 (and EGL surfaceless) supports S3TC, RGTC and BPTC in core [M]. Readback vs FFmpeg's CPU decode with the M4 thresholds. The ES leg runs the same tests with `PLAYMAP_GL_DIALECT=es` (present's CI leg) | — | ANGLE-on-D3D11 format exposure (lab test 1) |
| Arena (M5) | **Yes**: N3 is the prototype. Multi-producer bit-exact test, starvation test with a never-signalled fake fence, GL-thread timing ratio | — | real DMA timing on NVIDIA, AMD and Intel (lab) |
| HAP R and HAP HDR | **Yes**: blocks made by M7's BC7 and BC6H fragment encoders, muxed `Hap7`/`HapH` with `strict unofficial`, demuxed, decoded on llvmpipe and compared byte-for-byte with the encoder's planes. BC6H readback by RMSLE vs the float source | no third-party HAP R/HDR file is available here. The research's re-tagged `Hap7` clip only proves the demux | **Interop with TouchDesigner, Pixera and VDMX** files and players: lab (§9 risk 3) |
| Encoder (M7) | **Yes**: N5's programs on llvmpipe; FFmpeg 9 decodes PlayMap's `Hap1`/`Hap5`/`HapY`/**`HapM`**/`HapA`. PSNR targets vs FFmpeg's encoder. Tile adaptation with a fake timer | — | GPU speed (lab); S1 compute encoders run on llvmpipe's GL 4.5 for correctness only |
| Rewriter (M8) | **Yes**: byte-equal planes; FFmpeg decodes the output | — | — |
| STATE, verb, context (M9) | **Yes**: engine tests with a fake media backend; desk tests with the fake engine | — | — |

**Honest summary.** The whole decode and encode path runs for real here, on a CPU rasteriser:
- parse, Snappy and upload;
- the pass;
- the encoders, mux and remux;
- the arena's cross-thread visibility.

Two things are not real here:
- **GPU timings** (DMA, encoder throughput on real shader cores), which are lab numbers;
- **ANGLE's format exposure on Windows**, which is compile-only here.

Third-party HAP R and HAP HDR interop is a lab item, because no such files or encoders exist in this container.

## 8. Desk and Companion surface

- **Media inspector**, "On this node":
  - the chip `HAP Q · GPU` (or `HAP R · GPU`, `HAP HDR · GPU`, `HAP · CPU (FFmpeg)` in amber, or `HAP R · cannot play here` in red);
  - a facts line: "16 chunks · Snappy 96 % · 0.7 MB per frame · 43 MB/s · decode 2.1 ms · arena";
  - for HAP HDR, the interpretation with an **Edit** button that sets `color.*` (`SET md… color.whiteNits …`).
- **Warnings with fix buttons**, each a verb line:
  - "1 chunk: decodes on one core" → **Re-chunk** (`MEDIA OPTIMISE md… TO rechunk`);
  - "raw chunks on a slow disk" → **Add Snappy**;
  - "HAP R needs BPTC; node B lacks it" → **Make a HAP Q version for node B** (`MEDIA OPTIMISE md… TO hap-q ON nodeB`).
- **Right-click on a media item:**
  - "Inspect on this node" / "Inspect on every node" (`MEDIA INSPECT … ON …`);
  - "Re-chunk for this rig";
  - "Strip Snappy (disk-fast nodes)";
  - "Colour: auto / linear / PQ / white nits…".
- **Network drawer, node card:** "Blocks: BC1 BC3 BC4 BC7 BC6H · arena 140 of 256 MB free".
- **Companion** (the companion package's module, generated from verbs and STATE):
  - variables `hap_sources`, `hap_corrupt` and `arena_free_mb`;
  - a feedback "A HAP clip on air refuses frames" (red);
  - an action `MEDIA INSPECT` (Low, instant press).
  - The module's poster key for a HAP item uses the optimise package's `IPosterStore`.

## 9. Risks and open questions

1. **ANGLE on D3D11 may not list BPTC or `EXT_buffer_storage`** [U].
   - BPTC missing: HAP R and HAP HDR are refused on Windows until present's L2 (native D3D11, where `BC7_UNORM` is guaranteed at feature level 11_0 [V Microsoft docs via codecs.md §2.7]). This is the biggest risk to the "HAP R on Windows" claim, and lab test 1 settles it first.
   - Buffer storage missing: the copy path runs (measured cost N2).
2. **The disk, not the decoder, caps layer counts** (codecs.md §9.1). The budget in `MEDIA INSPECT` says so, and S3 (direct raw reads) is the lever for raw files.
3. **HAP R and HAP HDR interop.**
   - Third-party files may use chunk counts, compressors or colour conventions we have not seen. The parser refuses with words rather than guessing.
   - Lab: decode TouchDesigner (12 chunks), Pixera and AVF Batch Exporter files, and have TouchDesigner and Pixera open PlayMap's files.
4. **Quality of the Fast HAP R encoder.** Mode 6 is valid but not better than HAP Q on opaque content (N5). So `optimise`'s defaults send opaque content to HAP Q and alpha content to HAP Q Alpha or HAP R. "Show"-quality HAP R needs S1, measured on a GPU before it is promised. CAG26 has NVTT's fast GPU BC7 at 257 ms per 2048² (codecs.md §5), so expect seconds per 4K frame, which makes it a farm job.
5. **Straight vs premultiplied alpha in HAP Alpha and HAP R** [U on the spec's wording]. Default straight, as FFmpeg decodes it. An item-level override can follow if a known producer premultiplies.
6. **Persistent coherent mapping on some drivers is slow to read.** We only write, so write-combined memory is what we want [E]. Lab check on Intel iGPUs, where "coherent" may mean cached.
7. **Open:**
   - Does Silk.NET 2.23's `GL.BufferStorage` overload take a `null` data pointer cleanly? The C prototype did.
   - Do FFmpeg 9's mov muxer defaults write `colr` from `codecpar` without `movflags +write_colr`? (Check in `HapMuxerTests`.)

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| M1 parser, writer, chunking, refusal words, conformance suite | 6 |
| M2 source, reader, workers, `ClipClock` extraction, `FrameLayout` compressed, interop tests | 10 |
| M3 probe and routing, readiness words | 2.5 |
| M4 compressed textures, caps per dialect, `VideoTextureSet` branch, `hap` pass, GL tests | 6 |
| M5 arena, `LeaseRetention`, `IFrameMemory`, resolver change, tests | 5 |
| M6 HDR interpretation, `ColorOverride`, shared colour include, CPU twin | 2.5 |
| M7 encoder programs (5 fragment encoders + Y refine), tiles, readback, packer, muxer, interop tests | 12 |
| M8 rewriter | 3 |
| M9 STATE, `MEDIA INSPECT`, context, papers | 4 |
| **Must total** | **51** |
| S1 compute BC7 and BC6H ports | 10 |
| S2 direct sampling; S3 direct raw reads; S4 sRGB (with R2.5); S5 desk; S6 budget | 9 |
| **With should** | **70** |
