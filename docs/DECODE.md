# Decode: the HAP family on the GPU, the decode-path ladder, hardware imports

Every clip decoder says the path its frames actually come by — and why not a better one — in STATE, DECODE STATUS, the context
and preflight. HAP clips play block-compressed: the CPU only undoes the second stage (Snappy, or a copy) chunk by chunk, and the GPU samples
the blocks. All seven codecs play, including the two FFmpeg has no decoder for.

| FourCC | Codec (`MediaInfo.VideoCodec`) | Texture | Planes | `hap` pass |
|---|---|---|---|---|
| `Hap1` | `hap` | BC1 (RGB DXT1) | 1 | as is |
| `Hap5` | `hap_alpha` | BC3 (RGBA DXT5) | 1 | as is |
| `HapY` | `hap_q` | scaled YCoCg in BC3 | 1 | YCoCg → RGB |
| `HapM` | `hap_q_alpha` | YCoCg BC3 + BC4 alpha | 2 | YCoCg → RGB, alpha from the BC4 plane |
| `HapA` | `hap_alpha_only` | BC4 | 1 | grey |
| `Hap7` | `hap_r` | BC7 | 1 | as is |
| `HapH` | `hap_hdr` | BC6H unsigned or signed | 1 | HDR reading, tone-mapped |

## The parts

- **Core** (`src/PlayMap.Core/Media/Hap`): `HapCodecs` (fourcc ↔ codec ↔ texture ↔ layout, the specification's section-type table),
  `HapFrame` (parses a frame in place — headers, the decode-instructions container, chunk tables — and refuses every malformed case
  with stable words; nothing allocates), `HapFrameWriter` and `HapChunking` (frames the parser reads byte-exact), `HapBudget` (the
  layer budget and the read-ahead caps), `HapHdr` (HAP HDR's rule chain and the nclx reader).
- **Media** (`src/PlayMap.Media/Hap`): `HapVideoSource` — a read thread demuxes into a ring of parsed packets, at most
  `decode.hapReadAheadMs` (250) of frames and at most `decode.hapReadAheadMb` (192) of bytes; a decode thread drops a frame wholly
  before the play head without decompressing it, fans the frame's chunks out to the process's worker pool (`HapWorkers`,
  `decode.hapWorkers`, 0 = cores − 1) and works chunks itself while it waits. Frame times are the packets' own (frame = f(time)),
  the loop reads across its end, every HAP frame is a keyframe so a seek reads from the frame at or before the target. A refused
  or corrupt frame is counted with its words and the last good frame holds; ten in a row put the words in the status.
  `SnappyBlock` is the second stage. `HapProbe` proves a clip decodable the way it plays; `HapInspect` reads a clip for MEDIA
  INSPECT; `HapThroughput` measures the second stage on a synthetic 4K HAP Q frame.
- **Render**: `GlDevice.Decode.cs` makes and fills compressed textures (from client memory or a ring of pixel-unpack buffers) and
  refuses a family the context lacks with words; what the context samples comes from `GlCaps` (its version and extensions), never
  from the driver's compressed-format list. `VideoTextureSet` uploads HAP planes at the coded size (whole blocks) and runs the
  `hap` program (`ShaderLibrary.Decode.cs`) into an RGBA16F picture at the visible size; `texelFetch` at each fragment reads exact
  texels, so the padding of an odd size is never shown.
- **Engine**: `MediaBackend` routes HAP items to `HapVideoSource` for the families the renderer samples (the render host says
  them on its first frame), to FFmpeg's CPU decoder for the five it reads when the GPU lacks a family (the source's `why` says
  so), and makes readiness refuse HAP R and HAP HDR on a GPU without BPTC ("HAP R needs BPTC textures, which this GPU's GL does
  not offer — optimise to HAP Q for this node"). A HAP source opened before the renderer said what it samples stops with those
  words (its status, STATE's red light) when the renderer turns out to lack its family, rather than reaching it as frames it
  would draw black. MEDIA INSPECT, STATE.decode, the context's `decode` item and the preflight check
  are in `docs/wire/decode.md`.

## The decode-path ladder

A clip FFmpeg decodes opens on the highest rung the policy (`decode.policy`, `DECODE POLICY`), the renderer's device and the proof
allow, and only ever steps down while it is open (`DecodeLadder`, Core, pure):

| Rank | `path` | Frames reach the renderer as | Where |
|---|---|---|---|
| 1 | `hw-direct` | the decoder's own surface, sampled in place | Linux: VAAPI on the renderer's render node, imported as dmabuf EGLImages; Windows: D3D11VA once direct sampling is proven or forced |
| 2 | `hw-gpu-copy` | one copy on the GPU | Windows: D3D11VA, the default until proven |
| 3 | `hw-copy-back` | a hardware frame copied to memory and uploaded | any hardware device that is not the renderer's |
| 4 | `software` | a CPU frame uploaded | no device, or `decode.policy software` |

`hap-gpu` (HAP) and `gpu-share` (texture-sharing inputs) are set by their own producers. A live input (NDI, OMT, Spout) is not on
the ladder: its frames come from its receiver, which no FFmpeg decoder of this node makes.

- **At open.** VAAPI surfaces are sampled in place or not at all (no one-copy rung): `gpu-copy` on Linux means copy-back. D3D11
  takes one GPU copy until direct sampling is proven on the driver (the proof harness is next round) or `DECODE POLICY DIRECT
  FORCE`. A renderer whose device imports nothing (GLX, WGL, or a software GL) opens every clip on copy-back with the renderer's
  words ("the GL is software (llvmpipe): zero-copy needs a GPU", "the render context is GLX: dmabuf imports need an EGL context",
  "the render context is WGL: zero-copy on Windows needs the Direct3D 11 output path (PRESENT PATH DXGI)").
- **At run time** (`SurfaceLadder`, one per decoder) a source steps down — keeping its queue, while the renderer holds the last
  picture — when the renderer could not import a frame (it reports the failure to the share against the source's key; the decode
  thread reads it before its next map), when the renderer's device changes under it (its context was rebuilt: it steps to
  copy-back), when the first frame's planes are not a (fourcc, modifier) the display imports, when, while playing, it waits more
  than three frame periods for a free surface three times in ten seconds (`StarvationWatch`; a pause or a seek is no wait), or
  when the device hands the stream back to software. Each step is one journal row (`decode`, "Intro: hw-direct → hw-copy-back — …") and moves STATE. A source steps back up
  only when it is opened again: the share forgets its import failure at the open (`IGpuShare.Forget`, on every share), so the new
  decoder starts from the top of the ladder.
- **The path a source reports is its latest frame's**: the path it published by (a copied-back frame says `hw-copy-back`, a
  frame the device handed back says `software`), never only the rung it opened on.

## Hardware imports

- **Linux, VAAPI → GL by dmabuf.** `DmaBufShare` (Render) is the renderer's device on an EGL context that imports dmabufs: the render
  node from `EGL_EXT_device_query`/`EGL_EXT_device_drm_render_node`, the importable (fourcc, modifier) pairs from
  `eglQueryDmaBufFormatsEXT`/`eglQueryDmaBufModifiersEXT`. `StreamDecoder` makes the VAAPI device on that node (the libva guard
  holds: a machine whose libva lacks what this FFmpeg calls skips it with words), with `extra_hw_frames` = the queue + 3. The decode
  thread maps each frame to DRM PRIME (`av_hwframe_map`, read access) into the slot's own AVFrame; `DrmFrames` reads the
  hand-declared descriptor (its layout pinned by `DrmLayoutTests`) into NV12 (R8 + GR88) or P010 (R16 + GR32) planes. The slot holds
  the mapping (and so the VA surface) until every hold on its `GpuSurface` is released. `DmaBufImporter` makes one EGLImage per plane,
  binds it as the storage of a texture it owns and the `yuv` pass samples it; the surface is held behind a fence and let go once the
  GPU passed it — two slots, so the renderer never holds more than two surfaces, and when both are in flight the last picture holds
  rather than the render thread waiting.
- **Windows, D3D11VA on the renderer's device.** `HwDecode.D3d11vaOnShare` makes FFmpeg's D3D11VA device on the share's
  `ID3D11Device` (AddRef'd for FFmpeg, BindFlags DECODER | SHADER_RESOURCE, lock callbacks into the share's recursive lock), and
  only on a device made with video support and multithread protection (otherwise the share's words say which is missing and clips
  copy back). The lock is the device's (`GpuDeviceLock`, `docs/OUTPUT.md`): the render loop holds it around each frame's ANGLE work,
  so a decoder's begin–submit–end bracket and the renderer's drawing never interleave on the one immediate context; a frame waits
  for a bracket at most 4 ms. `AngleImporter` makes its plane images through the DXGI platform's own ANGLE loader on the display
  made on the share's device (never a libEGL found by name), inside the render loop's hold. It copies the decoder's array slice
  into one of three pooled NV12/P010 textures (`CopySubresourceRegion`) whose plane images (`EGL_ANGLE_image_d3d11_texture` with
  the plane and array-slice attributes) are made once, or, when the source is on the direct rung (`GpuSurface.Direct`), imports
  the slice itself, its images cached for that decoder's pool only; one device and one immediate context, so a frame is released
  as soon as its read is recorded.
- **The GL API on Linux.** A desktop context on X11 is GLX unless `decode.glApi` is `egl`, which makes it (and its output windows)
  on EGL: the display then imports dmabufs, `decode.platform.gl` says `egl`, and zero-copy is ready on a GPU whose EGL device names
  a render node. The DXGI platform's binding reads `angle`.
- **Padded surfaces.** The `yuv` program crops: `uCrop` is visible ÷ coded size and `uMaxUv` the last visible chroma texel's
  centre, so a 1920×1088 coded frame shows no padding row and chroma never filters into it. Dmabuf planes import at their visible
  size (no crop); D3D11 plane images cover the coded size.

## The upload arena

Each render context has one: a persistently mapped, coherent pixel-unpack buffer (`decode.uploadArenaMb`, 256 MB; buffer storage
from desktop 4.4, `ARB_buffer_storage` or ES `EXT_buffer_storage`, else none and the words say so). A HAP source's decode thread
rents a block (`ArenaAllocator`: aligned first-fit ranges, coalesced, at most 64 out, allocation-free) and writes the frame's
second stage straight into it; the renderer uploads its blocks from the buffer at the block's offset, with no copy of its own. A
returned block is free again only after a fence the GPU has passed (`GlUploadArena.EndFrame`, once a frame). When the context goes,
sources are told first and return once none is writing into the arena; their queued frames in it are let go and made again; the
arena's epoch moves, so a late block is ignored and a frame in a gone arena is never read. A HAP source is handed the arena under
the same lock that lists it as open, so one opened while the context goes never writes into an arena it was not told about. STATE says `staging: persistent` and the
arena's size and free megabytes.

## HAP HDR

The specification carries no colour metadata, so the reading is explicit and STATE says which rule applied: the item's override
(`color.transfer`, `color.whiteNits`), then the container's `colr` nclx transfer (8 linear, 16 PQ, 18 HLG), then PlayMap's tag
`playmap_white_nits` (the stream's metadata, else the file's: QuickTime keeps a custom tag only for the whole file, written
with `movflags use_metadata_tags`), then the default — linear light with 1.0 = 203 nits (BT.2408 reference white). Values below zero
(signed BC6H) are clamped. The pass tone-maps with the yuv program's own curve.

## Measured here

On this container (4 cores, llvmpipe, Debug build, a shared and loaded machine), recorded by the `Category=Perf` tests:
- the second stage of a 4K HAP Q frame on one core, both decoders compiled optimised, on FFmpeg-encoded 16-chunk frames (a
  machine at load 6–10): a flat test pattern (0.73 MB stored) 1.1–1.8 ms here against 1.4–1.8 ms with Snappier; a Mandelbrot
  (3.2 MB stored, about 790 000 elements a frame) 5.8–7.2 ms against 5.3–6.3 ms; noise (8.1 MB stored, nearly all literal)
  1.9–2.5 ms against 2.2–2.3 ms. The Debug build of the tests reads 9–35 ms;
- a 4K HAP Q clip of 16 chunks plays 30 frames in 0.1–0.3 s through the source (stage p95 7–24 ms with every core shared);
- every family uploads and samples on llvmpipe's core 3.3 context; whole frames match FFmpeg's CPU decode within the
  per-family bounds (HAP and HAP Alpha max diff ≤ 2 and PSNR ≥ 60 dB, HAP Q ≤ 4 and ≥ 50 dB).

**Why not Snappier at run time.** Snappier 1.3.1's `Snappy.Decompress(ReadOnlySpan<byte>, Span<byte>)` makes a decompressor
object per call (80 bytes, measured), so a frame path through it allocates per chunk. The second stage is a decoder written from
the format description that allocates nothing; Snappier stays in the tests as the reference it is checked against byte for byte
and timed beside.

## Verified here, and what is not

- HAP, HAP Alpha and HAP Q against FFmpeg 9's own encoder and its CPU decoder; HAP Q Alpha, Alpha-Only, HAP R and HAP HDR against
  frames assembled here (`HapFrameWriter`, blocks from BCnEncoder.NET, muxed with the codec's fourcc), on Mesa's S3TC, RGTC and BPTC.
- Interop with HAP R and HAP HDR files from other encoders is lab work: no such files or encoders exist here.
- The ladder's rules, every demotion with its words, and the path in STATE equal to the path the frames came by.
- The dmabuf import half on llvmpipe: memfd-backed NV12 and P010 planes (odd sizes, offsets, 256-byte pitches) imported per plane
  convert to the CPU upload's picture bit for bit; holds, the two-surface limit, a failed import that keeps the last picture.
  `EGL_MESA_image_dma_buf_export` is absent on this display, so the export round trip says so and runs where Mesa offers it.
- Padded surfaces (a 1920×1088 frame with a magenta padding band), the upload arena (uploads, fences, epochs, allocation-free).
- The device lock (`GpuDeviceLockTests`; `DeviceLockHostTests` runs the render host on a GL platform wearing a shared device, on
  the machine's own context: surfaceless EGL on Linux, WGL on Windows):
  recursion, exclusion, the bounded take, letting go for a wait and taking every hold back, no allocation once warm; the loop
  drawing inside the lock, never waiting past its bound while a sharer holds it, and a sharer taking it bracket by bracket.
- `decode.glApi egl` on Xvfb: the desktop context and its output windows on EGL, the display's dmabuf share, `decode.platform.gl`
  reading `egl` (`GlApiTests`, `DecodeGlApiTests`; Linux only, as the dmabuf import tests are).
- **Compile-only**: VAAPI decode and its DRM mapping (no VA driver, no `/dev/dri`), D3D11VA and the ANGLE importer (a
  `Category=Windows` test opens the D3D11VA device on a WARP device on CI, or says why — WARP has no video decoder and the runner
  has no FFmpeg). Fallback for both: copy-back or software, with the words why. **Lab**: Intel/AMD VAAPI, NVIDIA/Intel D3D11VA.

## Next round

- The direct-sampling proof (`DECODE PROBE`, `NodeHome/decode-proof.json`), the EGLImage cache per VA surface, the per-item decode
  override, a measured `importMsP95` (0 this round), promoting a clip opened before the render context said what it imports (it
  stays on copy-back, honestly reported, until it is opened again), raw HAP straight from disk into the arena, CUDA → GL;
  `decode.glApi auto` choosing EGL for a desktop context on Linux (it is GLX unless `egl` is asked).
- `EngineSources` drives loops and open-failure only for `FfmpegVideoSource`: a HAP source opens without a loop of its own, so a
  clip that ends holds its last frame and a looping item (a library item shown directly, a looping playlist clip) wraps through a
  seek — the frame before the loop point holds for the seek's few milliseconds instead of the loop being gapless — and a HAP
  source that could not open is not retried. Needs a spine amendment (below).
- Interop fixtures of HAP R/HDR from other encoders (lab), direct sampling of BC textures with no `hap` pass, sRGB block formats,
  the desk's codec and decode-path chips.
- MEDIA INSPECT's results kept in `NodeHome/inspect/` across restarts (kept in memory this round) and `ON <node>`.
- A faster second stage on element-dense frames (a table-driven tag decode, as Snappier's): it trails Snappier by up to a
  fifth there.

## Spine amendments asked for

- `GpuSurface` carries `SourceKey`, `Owner` (`IGpuSurfaceOwner`) and `Retain`/`Release`: a renderer holds a decoder surface past
  the lease that carried it until its read completed (made here, in `GpuSurfaces.cs`).

- `VideoFrameLease` gains `virtual double WhiteNits` (or an HDR scale): the HAP HDR white level rides `IHapPicture` (Core, this
  package) until then.
- An interface for a clip source's loop and open result (`SetLoop`, `Opened`) that `EngineSources` reads instead of the
  `FfmpegVideoSource` type, so HAP sources loop gaplessly with a playlist's in and out points and retry when they could not open.
- The shared GLSL include (`pqToLinear`, `hlgToLinear`, `toneMap`): the `hap` program carries its own copy until `ShaderLibrary.cs`
  can hold one for both programs.
