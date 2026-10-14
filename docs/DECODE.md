# Decode: the HAP family on the GPU

HAP clips play block-compressed: the CPU only undoes the second stage (Snappy, or a copy) chunk by chunk, and the GPU samples
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
  not offer — optimise to HAP Q for this node"). MEDIA INSPECT, STATE.decode, the context's `decode` item and the preflight check
  are in `docs/wire/decode.md`.

## HAP HDR

The specification carries no colour metadata, so the reading is explicit and STATE says which rule applied: the item's override
(`color.transfer`, `color.whiteNits`), then the container's `colr` nclx transfer (8 linear, 16 PQ, 18 HLG), then PlayMap's stream
tag `playmap_white_nits`, then the default — linear light with 1.0 = 203 nits (BT.2408 reference white). Values below zero
(signed BC6H) are clamped. The pass tone-maps with the yuv program's own curve.

## Measured here

On this container (4 cores, llvmpipe, Debug build, a shared and loaded machine), recorded by the `Category=Perf` tests:
- the second stage of a 4K HAP Q frame on one core: 9–35 ms with the decoder here, 6–14 ms with Snappier (Release), depending on
  the machine's load;
- a 4K HAP Q clip of 16 chunks plays 30 frames in 0.1–0.3 s through the source (stage p95 7–24 ms with every core shared);
- every family uploads and samples on llvmpipe's core 3.3 context; whole frames match FFmpeg's CPU decode within hap.md M4's
  bounds (HAP and HAP Alpha max diff ≤ 2 and PSNR ≥ 60 dB, HAP Q ≤ 4 and ≥ 50 dB).

**Why not Snappier at run time.** Snappier 1.3.1's `Snappy.Decompress(ReadOnlySpan<byte>, Span<byte>)` makes a decompressor
object per call (80 bytes, measured), so a frame path through it allocates per chunk. The second stage is a decoder written from
the format description that allocates nothing; Snappier stays in the tests as the reference it is checked against byte for byte
and timed beside.

## Verified here, and what is not

- HAP, HAP Alpha and HAP Q against FFmpeg 9's own encoder and its CPU decoder; HAP Q Alpha, Alpha-Only, HAP R and HAP HDR against
  frames assembled here (`HapFrameWriter`, blocks from BCnEncoder.NET, muxed with the codec's fourcc), on Mesa's S3TC, RGTC and BPTC.
- Interop with HAP R and HAP HDR files from other encoders is lab work: no such files or encoders exist here.

## Next round

- The persistently mapped upload arena (`UploadArena`, `LeaseRetention`): this round's stretch item, not started (wp2-zerocopy's
  item 4 picks it up); `decode.uploadArenaMb` is read by nothing yet and STATE says `staging: copy`.
- `EngineSources` drives loops and open-failure only for `FfmpegVideoSource`: a HAP source opens with its loop on (a library item
  shown directly is gapless; a playlist clip with in and out points loops through a seek), and a HAP source that could not open
  is not retried. Needs a spine amendment (below).
- Interop fixtures of HAP R/HDR from other encoders (lab), direct sampling of BC textures with no `hap` pass, raw HAP straight from
  disk into the arena, sRGB block formats, the desk's codec chips.
- MEDIA INSPECT's results kept in `NodeHome/inspect/` across restarts (kept in memory this round) and `ON <node>`.
- A faster second stage (wider copies, or Snappier once it decodes without allocating).

## Spine amendments asked for

- `VideoFrameLease` gains `virtual double WhiteNits` (or an HDR scale): the HAP HDR white level rides `IHapPicture` (Core, this
  package) until then.
- An interface for a clip source's loop and open result (`SetLoop`, `Opened`) that `EngineSources` reads instead of the
  `FfmpegVideoSource` type, so HAP sources loop gaplessly with a playlist's in and out points and retry when they could not open.
- The shared GLSL include (`pqToLinear`, `hlgToLinear`, `toneMap`): the `hap` program carries its own copy until `ShaderLibrary.cs`
  can hold one for both programs.
