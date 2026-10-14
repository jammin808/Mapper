# GPU codecs for PlayMap: the HAP family (incl. HAP R and HAP HDR), optimise on import, zero-copy hardware decode

Research date: **13 October 2026**. Build machine: 4-core Linux, 15 GB RAM, no GPU, Mesa 25.2.8 llvmpipe (LLVM 20.1.2) on Xvfb :99, FFmpeg `n9.0.2-27-g7e93014c5b-20261012` (libavutil 61.1.102, libavcodec 63.1.102) at `/opt/ffmpeg9`.

**Legend.** Every fact carries a source and a mark:
- **[V]** verified: I opened the source (file, page, spec, repository) in this session. "(extract)" means the text came from a search-engine extract of that page, not a full fetch.
- **[M]** measured on this machine on 13 Oct 2026 (commands and prototypes under `scratchpad/r2/bench/`).
- **[U]** unverified: the claim is from memory or from a page I could not open. Treat it as a lead.
- **[E]** estimate or engineering judgement, with the arithmetic shown.

Prototype sources written for this report (research code only, not product code):
`docs/round2/bench/proto/happarse.c` (HAP parse and Snappy timing) and `.../proto/glcheck.c` (BCn upload and readback through EGL on llvmpipe).

---

## 0. Findings on one page

1. **The HAP spec already defines HAP R and HAP HDR.** Top-level section types `0xAC/0xBC/0xCC` (BC7, fourcc `Hap7`), `0xA2/0xB2/0xC2` (BC6H unsigned) and `0xA3/0xB3/0xC3` (BC6H signed), fourcc `HapH`. BC7 joined in 2019 and BC6 in 2022 (the BC6 commit is by Derivative) [V, Vidvox/hap git log].
2. **FFmpeg cannot decode or encode either one.** FFmpeg master (13 Oct 2026) and our FFmpeg 9 build map only `Hap1 Hap5 HapY HapA HapM`. They decode on the CPU to `rgb0`/`rgba`/`gray8` [V hapdec.c, isom_tags.c; M `-decoders`]. The one public FFmpeg fork with HAP R/HDR (DemolitionStudios `hap_bc6h`) can encode them. Its decoder for them is still a `// TODO` [V].
3. **FFmpeg's container code works for both.** I gave a HAP stream a `Hap7` tag. The FFmpeg 9 mov demuxer still returns all its packets (codec "unknown", tag and size kept). The mov muxer writes `Hap7` when strict compliance is set to `unofficial` [M]. So PlayMap needs no container code of its own: libavformat reads and writes, and PlayMap decodes.
4. **Our own GPU path uses 26–45× less CPU than FFmpeg's CPU HAP path, measured here.**
   - 4K HAP Q in FFmpeg costs 53–102 ms of CPU per frame, plus 10–15 ms for PlayMap's `rgb0→rgba` swscale. That is 15–57 fps with 4 threads.
   - My parse and Snappy step costs 2.0–2.3 ms on one core. Its output is 8.3 MB of BC3 blocks, ready to upload; FFmpeg's is 33 MB of RGBA.
   - Uploaded as-is to Mesa, the blocks read back like FFmpeg's CPU decode: HAP 68 dB PSNR (max diff 2); HAP Q 54 dB (max diff 3, from FFmpeg's integer truncation) [M].
5. **This box can decode every HAP texture format.** llvmpipe's core profile exposes S3TC, RGTC and BPTC. Compressed uploads of BC1, BC3, BC4, BC7, BC6H UF16 and BC6H SF16 all work and read back [M]. GPU-HAP tests can therefore run in CI here.
6. **Encoding HAP R and HAP HDR on the CPU is slow; GPU encoders are needed.**
   - Fast CPU BC7 encoders need about 15 ms per 2048² image on a 24-core desktop CPU (Core Ultra 9 285K; bc7e "ultrafast", QuickBC7). Quality profiles need 60–700 ms [V extract, CAG 2026 paper].
   - About 4K, the published GPU numbers say this: BC6H can be encoded in real time on a GPU (GPURealTimeBC6H, MIT). A GPU BC7 compute encoder exists under MIT (DirectXTex `BC7Encode.hlsl`) [V].
   - A new CPU encoder, bc7f, picks its BC7 mode analytically and runs in bounded time (2026) [V].
7. **Watchout 7 optimises on import, with limits.**
   - Every source becomes its own playout format (WAF). H.264 goes to HEVC, ProRes to Notch LC, and the HAP family passes through.
   - It keeps the source, deduplicates storage and makes quarter-resolution HAP previews.
   - It prioritises work by asset type, and Runners keep playing with the Asset Manager offline.
   - Its weak points: it **has no HAP R encoder** (HAP R only passes through), mapping changes never re-optimise existing assets, and the work runs on one Asset Manager [V, docs.dataton.com guide].
   - Pixera transcodes to VP9, NotchLC, HAP, HAP Alpha and HAP Q, and keeps the result as a "file version" [V extract].
   - disguise has no built-in optimiser that I could find; it sends users to Adobe Media Encoder [V extract].
8. **D3D11VA zero-copy is possible with FFmpeg 9.**
   - FFmpeg 9 adds device-level `BindFlags`/`MiscFlags` to `AVD3D11VADeviceContext` (APIchanges 2026-01-26, lavu 60.24.100). FFmpeg.AutoGen 9.0.1.1 binds `AVD3D11VADeviceContext`, `AVD3D11VAFramesContext` and `AVD3D11FrameDescriptor` [V].
   - The safe default is the one mpv uses: one on-GPU copy (`CopySubresourceRegion`) from the decoder array slice into a texture that shaders can read. mpv documents that sampling the decoder surface directly "technically" breaks D3D11 rules [V mpv manual].
   - `WGL_NV_DX_interop2` lists no planar YUV support. mpv's DXVA2→GL interop converts to RGB first (`StretchRect`) [V].
   - On Linux the route is VAAPI → DRM PRIME → EGL dmabuf (`EGL_EXT_image_dma_buf_import(_modifiers)`, both present here). It needs an **EGL** context, not GLX.
   - FFmpeg 9's Vulkan hwaccel decodes H.264, HEVC, AV1, VP9 and **ProRes** (ProRes since 8.1) [V Changelog; M `-h decoder=prores`].

---

## 1. PlayMap's media path today (read from the repo)

| Fact | Source |
|---|---|
| Decoder open: `avcodec_find_decoder` with `av_hwdevice_ctx_create(&device, type, null, null, 0)`. FFmpeg creates its own device, which is **not shared with the renderer** | [V] `/home/user/mapper/src/PlayMap.Media/Ffmpeg/MediaInput.cs` (`StreamDecoder.TryHardwareCore`) |
| Device order: Windows `D3D11VA`; macOS `VIDEOTOOLBOX`; Linux `VAAPI`, then `CUDA` | [V] same file, `PlatformDevices()` |
| A hardware frame is copied to system memory with `av_hwframe_transfer_data` into NV12/P010 (`TransferIfHardware`), then uploaded again | [V] same file |
| `FrameFormats.Plan`: `rgb0`, which FFmpeg's HAP decoder outputs for `Hap1`/`HapY`, is not a pass-through layout. It is converted to RGBA with swscale (`alpha \|\| rgb … → Rgba8, Convert=RGBA`) | [V] `src/PlayMap.Media/Frames/FrameFormats.cs` |
| `PixelLayout` has no compressed layouts. `TextureFormat`/`UploadFormat` have no BC formats. Uploads use `TexSubImage2D`, optionally from a PBO | [V] `src/PlayMap.Core/Media/VideoFrames.cs`; `src/PlayMap.Render/Gpu/GpuTypes.cs`; `src/PlayMap.Render/Gpu/GlDevice.Resources.cs` (stage branch, now merged into main `be9b24c`) |
| GL context: Silk.NET.Windowing 2.23.0, `ContextAPI.OpenGL`, Core 3.3 | [V] `src/PlayMap.Render/Platform/RenderContext.cs`, `PlayMap.Render.csproj` |
| Media verbs today: `MEDIA LIST/GET/IMPORT/REMOVE/HAS/FETCH/STATUS`; content addressed by `sha256:` | [V] `src/PlayMap.Core/Wire/VerbCatalog.cs` |
| Roadmap items for this topic: R2.2 (D3D11VA zero-copy), R2.3 (HAP family on the GPU incl. HAP R/HDR), R2.4 (optimise on import), R2.11 (GPU contention policy); L7 (NotchLC GPU decode if the SDK terms allow) | [V] `docs/ROADMAP.md` |

---

## 2. The HAP format

### 2.1 Status and provenance

- Spec: `documentation/HapVideoDRAFT.md` in https://github.com/Vidvox/hap (still titled DRAFT). Repo HEAD `d847f6b`, 2024-07-25 [V, shallow then full clone].
- Spec history [V, `git log`]:
  - `4781a9f` 2016-04-04: fourcc recommendations added.
  - `c238cda` 2019-11-04: "Document four-cc for BC7/BPTC".
  - `3a50947` 2020-08-31: "Add human-readable name for BC7 Hap".
  - `ee877fd` 2022-04-29 (Malcolm Bechard, Derivative): BC6U/BC6S sections.
  - `5526371` 2022-05-05: "Hap H" renamed to **"Hap HDR"**.
  - `c7abb0c` 2022-05-06: reference code supports BC6.
- Licence of the spec and reference code: 2-clause BSD ("Free BSD"), © 2012–2013 Tom Butterworth and Vidvox LLC [V, `LICENSE`, `README.md`]. PlayMap may use it if the notice is kept.
- Test material listed in the README: Odd Dimensions, 16K, FFmpeg, TouchDesigner (1.5 GB), AVF Batch Converter, QuickTime and DirectShow sample packs on `d3omao0uy1rjjh.cloudfront.net` [V README; packs not downloaded].

### 2.2 Section header [V spec]

- The header is 4 or 8 bytes. If any of bytes 0–2 is non-zero, they hold a 24-bit little-endian size and the header is 4 bytes.
- If bytes 0–2 are all zero, bytes 4–7 hold a 32-bit little-endian size and the header is 8 bytes.
- Byte 3 is the **section type**. The size excludes the header.
- Unknown section types must be skipped when other sections give enough information. Sections at one level may come in any order.

### 2.3 Top-level section types [V spec; reference `hap.c` constants]

Type byte = `(second_stage << 4) | texture`. The second-stage nibble: `0xA` none, `0xB` Snappy, `0xC` "consult decode instructions" (chunked).

| Texture nibble | Content | None | Snappy | Chunked | Block bytes | B/px |
|---|---|---|---|---|---|---|
| `0xB` | RGB, DXT1/BC1 | `0xAB` | `0xBB` | `0xCB` | 8 | 0.5 |
| `0xE` | RGBA, DXT5/BC3 | `0xAE` | `0xBE` | `0xCE` | 16 | 1 |
| `0xF` | Scaled YCoCg in DXT5/BC3 | `0xAF` | `0xBF` | `0xCF` | 16 | 1 |
| `0xC` | RGBA, **BC7** | `0xAC` | `0xBC` | `0xCC` | 16 | 1 |
| `0x1` | Alpha, RGTC1/BC4 | `0xA1` | `0xB1` | `0xC1` | 8 | 0.5 |
| `0x2` | RGB unsigned float, **BC6U** | `0xA2` | `0xB2` | `0xC2` | 16 | 1 |
| `0x3` | RGB signed float, **BC6S** | `0xA3` | `0xB3` | `0xC3` | 16 | 1 |
| — | Multiple images | `0x0D` | | | | |

The reference code's enum values are the GL internal formats: `HapTextureFormat_RGB_DXT1 = 0x83F0`, `RGBA_DXT5 = 0x83F3`, `YCoCg_DXT5 = 0x01`, `A_RGTC1 = 0x8DBB`, `RGBA_BPTC_UNORM = 0x8E8C`, `RGB_BPTC_UNSIGNED_FLOAT = 0x8E8F`, `RGB_BPTC_SIGNED_FLOAT = 0x8E8E` [V `source/hap.h`].

### 2.4 Multiple images (`0x0D`) [V spec]

- The section holds one or two top-level image sections. With two, their dimensions must match.
- The only permitted pair is **Scaled YCoCg DXT5 + RGTC1/BC4 alpha**, which is HAP Q Alpha (`HapM`).
- Each inner section is decoded as if it stood alone, and the components are combined: RGB from YCoCg, A from BC4.

### 2.5 Decode instructions and chunking [V spec; V reference `hap.c` lines 280–300, 440–475, 640–840]

- **Chunked top-level section (`0xC?`).** Its data is one Decode Instructions Container (type `0x01`), *immediately followed* by the frame data.
- **Inside the container:**
  - `0x02` Chunk Second-Stage Compressor Table: one byte per chunk, `0x0A` uncompressed or `0x0B` Snappy.
  - `0x03` Chunk Size Table: u32 LE per chunk, the compressed size.
  - `0x04` Chunk Offset Table: u32 LE per chunk, offset from the start of the frame data. Optional; without it, offsets are the running sum of sizes.
  - All tables have the same entry count, and the entry count is the chunk count. A compressor table requires a size table.
- **Output placement.** Each chunk decompresses independently. The reference decoder reads each chunk's uncompressed length from its Snappy preamble (`snappy_uncompressed_length`) and places chunks contiguously in order (`running_uncompressed_chunk_size`). So every chunk's output offset is known before any decompression, and all chunks can be decompressed in parallel straight into the final buffer.
- **Reference encoder.** It splits on block boundaries: 8 bytes for DXT1/BC4, 16 otherwise. It reduces the chunk count until `block_count % chunk_count == 0`, then stores a chunk raw (`0x0A`) when Snappy does not shrink it.
- **Chunk counts in practice.** FFmpeg's encoder takes `-chunks 1..64` [M `-h encoder=hap`]. TouchDesigner always writes chunked HAP with 12 chunks, and advises chunks ≥ physical cores [V extract, https://docs.derivative.ca/Hap].

### 2.6 FourCCs and names [V spec]

| Texture(s) | Name | FourCC |
|---|---|---|
| DXT1/BC1 | Hap | `Hap1` |
| DXT5/BC3 RGBA | Hap Alpha | `Hap5` |
| Scaled YCoCg DXT5 | Hap Q | `HapY` |
| Scaled YCoCg DXT5 + RGTC1 alpha | Hap Q Alpha | `HapM` |
| RGTC1/BC4 | Hap Alpha-Only | `HapA` |
| BPTC/BC7 UNORM | **Hap R** | **`Hap7`** |
| BPTC/BC6U and BC6S | **Hap HDR** | **`HapH`** |

Vidvox also calls HAP R "HAP 7A" because it is BC7 with alpha [V, https://vdmx.vidvox.net/blog/vdmx6, 2024-09-12; https://vdmx.vidvox.net/blog/hap-r-benchmarks, 2025-08-07].

### 2.7 Texture formats → GPU API enums

| HAP texture | GL internal format (value) | GL availability | DXGI (value) |
|---|---|---|---|
| BC1 | `GL_COMPRESSED_RGB_S3TC_DXT1_EXT` (0x83F0); sRGB variant via `EXT_texture_sRGB` | `GL_EXT_texture_compression_s3tc` (never core) | `DXGI_FORMAT_BC1_UNORM` (71) / `_SRGB` (72) |
| BC3 | `GL_COMPRESSED_RGBA_S3TC_DXT5_EXT` (0x83F3) | same | `DXGI_FORMAT_BC3_UNORM` (77) / `_SRGB` (78) |
| BC4 | `GL_COMPRESSED_RED_RGTC1` (0x8DBB) | core since GL 3.0 [U on version; widely documented] | `DXGI_FORMAT_BC4_UNORM` (80) |
| BC7 | `GL_COMPRESSED_RGBA_BPTC_UNORM` (0x8E8C), sRGB 0x8E8D | `ARB_texture_compression_bptc`, core in GL 4.2 [U on version]; written against GL 3.2 [V spec text] | `DXGI_FORMAT_BC7_UNORM` (98) / `_SRGB` (99) |
| BC6H unsigned | `GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT` (0x8E8F) | same | `DXGI_FORMAT_BC6H_UF16` (95) |
| BC6H signed | `GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT` (0x8E8E) | same | `DXGI_FORMAT_BC6H_SF16` (96) |

Sources:
- GL enum values: [V `ARB_texture_compression_bptc.txt` and `EXT_texture_compression_s3tc.txt` from KhronosGroup/OpenGL-Registry; 0x8DBB from Vidvox `hap.h`].
- DXGI values: [V https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format].
- BC6H facts: 16-byte blocks, 14 modes, UF16 has no sign bit, alpha always 1.0, decode must be bit-accurate, no INF/NaN (SF16 can hold −INF) [V https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc6h-format].

Frame sizes (block payload before Snappy) [E, arithmetic: ⌈w/4⌉·⌈h/4⌉·block bytes]:

| | 1920×1080 | 3840×2160 | 4K at 60 fps |
|---|---|---|---|
| BC1 / BC4 (HAP, HAP Alpha-Only) | 1,036,800 B | 4,147,200 B | 249 MB/s |
| BC3 / BC7 / BC6H (HAP Alpha, HAP Q, HAP R, HAP HDR) | 2,073,600 B | 8,294,400 B | 498 MB/s |
| BC3 + BC4 (HAP Q Alpha) | 3,110,400 B | 12,441,600 B | 746 MB/s |
| For comparison: RGBA8 / NV12 / P010 / RGBA16F | 8.29 / 3.11 / 6.22 / 16.6 MB | 33.2 / 12.4 / 24.9 / 66.4 MB | RGBA8 1.99 GB/s |

### 2.8 Scaled YCoCg (HAP Q)

- Reference paper: J.M.P. van Waveren and I. Castaño, *Real-Time YCoCg-DXT Compression*, NVIDIA, September 2007 (cited by the spec) [V spec; paper not opened].
- Channel layout in the DXT5 texel: R = Co, G = Cg, B = scale, A = Y.
- FFmpeg's CPU maths (`libavcodec/texturedsp.c`, `ycocg2rgba(…, scaled=1)`): `s = (b >> 3) + 1; co = (r − 128) / s; cg = (g − 128) / s; R = y + co − cg; G = y + cg; B = y − co − cg` in **integer** arithmetic, so it truncates [V].
- The fragment-shader equivalent in my prototype: `co = R − 0.50196078; cg = G − 0.50196078; scale = B·(255/8) + 1; co /= scale; cg /= scale; rgb = (Y+co−cg, Y+cg, Y−co−cg)`. Against FFmpeg's decode of the same frame: PSNR 54.4 dB, max diff 3 code values [M]. The float path is the more accurate one.
- HAP Q cannot use an sRGB texture format, because YCoCg values are not sRGB-encoded. Linearise after the conversion.
- HAP, HAP Alpha and HAP R can use the `*_SRGB` formats, so the hardware linearises before filtering. That is relevant to R2.5's linear pipeline [E].

### 2.9 Snappy

- Format [V https://raw.githubusercontent.com/google/snappy/main/format_description.txt, last revised 2011-10-05]:
  - Preamble: the uncompressed length as a little-endian varint (≤ 2³²−1).
  - Elements are tagged by the low 2 bits: `00` literal (length−1 in the upper 6 bits up to 60, else 1–4 extra bytes); `01` copy with 1-byte offset (length 4..11, 11-bit offset); `10` copy with 2-byte offset (length 1..64); `11` copy with 4-byte offset.
  - The format is simple enough to write a bounded decoder for (about 150 lines).
- .NET options:
  - **Snappier 1.3.1** (published 2026-04-28; BSD-3-Clause; pure C#; targets net8.0, netstandard2.0, net472) [V NuGet nuspec and registration].
    - API: `Snappy.GetUncompressedLength(ReadOnlySpan<byte>)`, `Snappy.Decompress(ReadOnlySpan<byte>, Span<byte>)` and `Snappy.TryDecompress(…, out int)` [V `Snappier/Snappy.cs`].
    - The README claims "near C++ level performance" on .NET 6+ through `System.Runtime.Intrinsics` [V README; not benchmarked here].
  - IronSnappy 1.3.1 (Apache-2.0, a port of the Go implementation) [V nuspec].
- Native libsnappy 1.1.10 decompression rates on this box (one core, in memory) [M, `happarse`]:
  - Compressible content (testsrc2): **4.2 GB/s** of output (8.29 MB in 1.99 ms).
  - Raw chunks: memcpy speed.
  - Partly compressible data (noisy RGB plus flat alpha): **0.71 GB/s** (8.29 MB in 11.7 ms).

### 2.10 What the spec leaves open (PlayMap must decide)

- **No colour metadata.** The spec says nothing about primaries, transfer or range. Only the container's `colr` atom can carry them [V spec, absence].
- **HAP HDR scaling is ambiguous in practice.**
  - The Demolition FFmpeg fork turns 8-bit RGBA input into BC6H by dividing by 255, or by a user `hap_h_normalization_factor`.
  - It also has a TODO about whether FFmpeg applies gamma to EXR input [V `hapenc.c` in DemolitionStudios/FFmpeg `hap_bc6h`].
  - TouchDesigner: "16-bit floating point data. Intended to be used for HDR content, since the values can go outside the 0-1 range… not suitable for… point cloud positions" [V extract, https://docs.derivative.ca/Hap].
  - A Derivative staff member, 2022-06-14: HAP HDR stores signed float for tone-mapped HDR colour [V extract, https://forum.derivative.ca/t/hap-hdr-encoding-outside-of-touch-designer/269307].
- **Odd dimensions.**
  - Blocks cover ⌈w/4⌉×⌈h/4⌉, and FFmpeg's decoder aligns `coded_width/height` to 4 [V hapdec.c].
  - FFmpeg's encoder refuses sizes that are not multiples of 4 [V hapenc.c].
  - D3D requires BC textures to be multiples of 4 in every dimension [V https://learn.microsoft.com/en-us/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-block-compression].
  - Pixera 26.1 added "Automatically adjust odd resolution when transcoding to HAP" [V extract, https://help.pixera.one/changelogs-version-overviews/pixera-261-overview-changelog].
- **No checksum per frame or per chunk.** A corrupt Snappy stream is caught only by the decompressor's bounds checks.
- **Non-standard second-stage compressor.** The Demolition fork defines `HAP_COMP_GDEFLATE = 0xD0` (GPU-decompressible deflate); it is not in the spec [V fork `hap.h`]. Its author: "GDeflate isn't a part of Hap specification" [V extract, https://github.com/vidvox/hap/issues/47, 2023].

### 2.11 Who supports HAP R and HAP HDR (as of Oct 2026)

| Product | HAP R | HAP HDR | Source |
|---|---|---|---|
| VDMX6 (Metal) | decode, Sept 2024 | — | [V] https://vdmx.vidvox.net/blog/vdmx6 (2024-09-12) |
| AVF Batch Exporter / HapInAVFoundation (macOS) | encode with the Apple Texture Encoder (ATE) | — | [V] HAP R benchmarks post (2025-08-07) |
| TouchDesigner | decode and encode (GPU; may exceed the Windows 2 s TDR at high resolution) | decode and encode (not in real time, per staff) | [V extract] https://docs.derivative.ca/Hap; forum 2022-06-14 |
| Pixera | decode "as of version 1.8.xx" | not listed | [V extract] https://help.pixera.one/managing-resources/supported-file-formats |
| Watchout 7 | ingest and play out by pass-through only ("WATCHOUT has no HAP R encoder") | not listed | [V] https://docs.dataton.com/guide/watchout/assets-asset-manager/formats-codecs.html |
| Jokyo HAP Encoder (Adobe plug-in) | encode (reported as the first) | — | [V extract] https://vvvv.org/blog/2022/news-in-hap-world/ (2022-04-20) |
| Demolition Media HAP Player (vvvv), Shutter Encoder fork | decode; encode via their FFmpeg fork | encode (fork) | [V extract] vvvv blog; issue #47 |
| FFmpeg master / FFmpeg 9 | no | no | [V] hapdec.c, isom_tags.c; [M] |
| KlakHap (Unity) | no (HAP, HAP Alpha, HAP Q only) | no | [V] https://raw.githubusercontent.com/keijiro/KlakHap/master/README.md |
| disguise | not listed (HAP, HAP Alpha, HAP-Q; **no HAP-Q Alpha**) | — | [V extract] https://help.disguise.one/designer/content-management/video-codecs/hap-hapq-codec |

### 2.12 Quality data for HAP R

Vidvox, 7 Aug 2025, https://vdmx.vidvox.net/blog/hap-r-benchmarks [V]. Encoder: HapInAVFoundation with ATE. "Kb/Frame" is kilobits; the noise row at 16589 Kb ≈ 1 B/px at 1080p.

| Clip | HAP 1.0 PSNR | HAP Q PSNR | HAP R 1.0 PSNR | HAP Q Kb/f | HAP R 1.0 Kb/f |
|---|---|---|---|---|---|
| Colour gradient | 49.59 | 42.45 | 51.37 | 2660.5 | 2897.3 |
| RGB noise | 29.64 | 31.47 | 31.99 | 16589.1 | 16589.1 |
| Fireplace (camera) | 46.60 | 42.28 | 50.61 | 13104.1 | 14061.6 |
| Big Buck Bunny (CG) | 45.02 | 44.68 | 48.95 | 10075.2 | 10314.3 |

The post also reports:
- HAP R averages **+5 dB** PSNR over HAP Q and +7 dB over HAP.
- HAP R at 100 % is "on par with ProRes 422", with file sizes about those of HAP Q.
- HAP R always carries alpha.
- ATE shows no quality variation below 99 %, and its 100 % setting is "much slower".

---

## 3. What FFmpeg 9 offers

### 3.1 This build [M]

`ffmpeg -encoders/-decoders | grep -iE "hap|notch|dxv"`:
- `hap` encoder `V.S..D` (slice threads). Options: `-format hap(11)|hap_alpha(14)|hap_q(15)`, `-chunks 1..64`, `-compressor none(160)|snappy(176)`. Input `rgba` only.
- `hap` decoder `VFS..D` (frame and slice threads).
- `dxv` encoder (format `dxt1` only) and `dxv` decoder.
- `notchlc` decoder `VF...D` (frame threads). There is **no** NotchLC encoder.

`-h decoder=…`, "Supported hardware devices", in this Linux build:
- h264, hevc, av1, vp9: `cuda vaapi vulkan`.
- prores, prores_raw, ffv1, apv: `vulkan`.
- hap, dxv, notchlc: none.

Hardware acceleration methods: `cuda vaapi qsv drm opencl vulkan amf`. A Windows build would add d3d11va/d3d12va/dxva2 [U for the Windows list; not checked here].

Configure includes `--enable-libsnappy --enable-gpl --enable-version3`. **This build is GPL**, so PlayMap must keep loading FFmpeg at run time from the user's install (as the brief says) and must copy none of its code.

### 3.2 HAP decoder and encoder internals (FFmpeg master, read 13 Oct 2026)

- `libavcodec/hap.h`:
  - `enum HapTextureFormat { HAP_FMT_RGBDXT1=0x0B, HAP_FMT_RGBADXT5=0x0E, HAP_FMT_YCOCGDXT5=0x0F, HAP_FMT_RGTC1=0x01 }`.
  - `HapCompressor { NONE=0xA0, SNAPPY=0xB0, COMPLEX=0xC0 }`.
  - Section types `0x01..0x04`.
  - No BC7 or BC6 values [V].
- `libavcodec/hapdec.c`:
  - Tags `Hap1→AV_PIX_FMT_RGB0`, `Hap5→RGBA`, `HapY→RGB0`, `HapA→GRAY8`, `HapM→RGBA` (two textures). Any other tag returns `AVERROR_DECODER_NOT_FOUND`.
  - Second-stage decode runs per chunk through `execute2`. Blocks are then decoded on the CPU through `ff_texturedsp_exec_decompress_threads`.
  - Raw contiguous texture data is used in place (`hap_can_use_tex_in_place`).
  - Frames are refused when `tex_size != (coded_w/4)*(coded_h/4)*tex_ratio` [V].
- `libavcodec/hapenc.c`:
  - Requires `width % 4 == 0 && height % 4 == 0`.
  - Compresses each chunk with libsnappy `snappy_compress`, falling back to raw when it does not shrink [V].
  - The texture encoder `texturedspenc.c` is "Based on public domain code by Fabian Giesen, Sean Barrett and Yann Collet" (stb_dxt-class, fast) [V].
- `libavformat/isom_tags.c`:
  - `AV_CODEC_ID_HAP` ← `Hap1, Hap5, HapY, HapA, HapM`.
  - `AV_CODEC_ID_DXV` ← `DXD3, DXDI`.
  - `AV_CODEC_ID_NOTCHLC` ← `nclc` [V].
- FFmpeg also has `hapqa_extract_bsf` (keeps the RGB or the alpha texture of HAP Q Alpha) [V extract, ffmpeg-devel 2024-01].

### 3.3 The one public FFmpeg fork with HAP R and HAP HDR

DemolitionStudios/FFmpeg, branch `hap_bc6h` [V files via raw.githubusercontent.com; V commit list via https://github.com/DemolitionStudios/FFmpeg/commits/hap_bc6h; last commit 2026-04-27].
- Adds tags `Hap7`/`HapH` and `HAP_FMT_BPTC=0x0C, HAP_FMT_BPTC_FU=0x02, HAP_FMT_BPTC_FS=0x03`.
- **The decoder has `// TODO` for both**, so it has no CPU decode.
- The encoder uses **bc7e (ISPC)** with presets `ultrafast/fast/slow`, and **GPURealTimeBC6H** for BC6H.
- It defines a non-standard `HAP_COMP_GDEFLATE 0xD0`.
- Vidvox hoped in Aug 2025 that it "will hopefully be part of the standard release in the next few months". FFmpeg master still lacks it on 13 Oct 2026 [V].

### 3.4 Container experiments [M]

- I rewrote the `stsd` fourcc of a 10-frame `Hap1` file to `Hap7`. `ffprobe` reports `codec_name=unknown codec_tag_string=Hap7 width=1920 height=1080 nb_read_packets=10`. **libavformat demuxes HAP R/HDR packets for us.**
- `ffmpeg -c copy -tag:v Hap7 out.mov` fails ("Tag Hap7 incompatible with output codec id '186'"). With **`-strict unofficial`** it writes `Hap7`. In the API: `AVFormatContext.strict_std_compliance = FF_COMPLIANCE_UNOFFICIAL` and `codecpar->codec_tag = MKTAG('H','a','p','7')`.
- `-c copy` of the unknown-codec stream into a new `.mov` also works, with a probe warning.

### 3.5 DXV and NotchLC in FFmpeg [V]

- `dxv.c` decodes:
  - `DXT1`.
  - `DXT5`: the alpha is premultiplied, so it is decoded as DXT4.
  - `YCG6`: YCoCg 4:2:0 stored as RGTC1 planes, output YUV420P.
  - `YG10`: YCG6 with alpha.
  - The encoder writes DXT1 only. DXV3 is Resolume's proprietary format.
- `notchlc.c` (LGPL-2.1+, © 2020 Paul B Mahol):
  - Magic `NLC1`, then u32 uncompressed size and u32 format (`0` LZF, `1` LZ4, `2` uncompressed).
  - Output `AV_PIX_FMT_YUVA444P12`. CPU only, frame-threaded.

---

## 4. Measurements on this machine (13 Oct 2026) [M]

The content is synthetic: `testsrc2`, either clean (very compressible) or with `noise=alls=12:allf=t` (barely compressible). Real footage sits between the two. Timings are wall time ÷ frames unless marked CPU.

**A. FFmpeg 9 CPU HAP decode** (`ffmpeg -benchmark -threads N -i X -f null -`)

| Clip | 1 thread | 4 threads | Note |
|---|---|---|---|
| 1080p HAP (noisy) | 5.7 ms | 3.1 ms | |
| 1080p HAP Q (noisy) | 24.4 ms | 18.7 ms | |
| 1080p HAP Q (clean) | 14.4 ms | 4.2 ms | |
| 4K HAP (noisy, 8 chunks) | 21.5 ms | 15.9 ms | |
| 4K HAP Q (noisy) | 101.8 ms | **68.1 ms (14.7 fps)** | |
| 4K HAP Q (clean, 16 chunks) | 56.0 ms | 17.6 ms wall, **52.8 ms CPU** | |
| 4K HAP Alpha (noisy) | 40.1 ms | 26.0 ms | |
| + `-pix_fmt rgba` (PlayMap's swscale step) at 4K | | +10.5 ms (HAP Q) to +14.6 ms (HAP) CPU per frame | |

**B. My HAP parse plus Snappy into a BCn buffer** (`proto/happarse`, libsnappy, OpenMP over chunks, packet already in memory)

| Clip | Chunks | In → out per frame | 1 thread | 4 threads |
|---|---|---|---|---|
| 1080p HAP (clean) | 8 | 0.10 → 1.04 MB | 0.26 ms | 0.08 ms |
| 1080p HAP Q (clean) | 8 | 0.19 → 2.07 MB | 0.47 ms | 0.15 ms |
| 4K HAP Q (clean) | 16 | 0.70 → 8.29 MB | **1.99 ms** | 0.50 ms |
| 4K HAP (clean) | **1** | 0.36 → 4.15 MB | 1.08 ms | 1.37 ms (one chunk: no parallelism) |
| 4K HAP (noisy, raw chunks) | 8 | 4.14 → 4.15 MB | 0.73 ms | 0.36 ms |
| 4K HAP Q (noisy) | 8 | 8.20 → 8.29 MB | **2.26 ms** | 0.95 ms |
| 4K HAP Alpha (noisy RGB, flat alpha) | 8 | 4.92 → 8.29 MB | 11.7 ms | 3.4 ms |

At 4K HAP Q the GPU path uses **26–45× less CPU per frame** than FFmpeg (52.8–101.8 ms vs 2.0–2.3 ms), before counting the 10–15 ms swscale that the GPU path also removes. It sends **4× less data** to the GPU (8.3 MB vs 33.2 MB).

**C. BCn on llvmpipe** (`proto/glcheck`: EGL surfaceless, GL 3.3 core context; Mesa compresses an RGBA or float test image, the blocks are read with `glGetCompressedTexImage`, re-uploaded with `glCompressedTexImage2D`, and read back with `glGetTexImage`)

| Format | Upload OK | Readback quality (Mesa's encoder; test only) |
|---|---|---|
| BC1 0x83F0 | yes, no GL error | 35.97 dB |
| BC3 0x83F3 | yes | 37.33 dB |
| BC4 0x8DBB | yes | 99 dB (gradient) |
| BC7 0x8E8C | yes | 29.78 dB. Mesa's BPTC encoder plus a pathological saw-tooth alpha; it is not a quality reference |
| BC6H UF16 0x8E8F / SF16 0x8E8E | yes | RMSLE 0.0040 / 0.0108 |
| **HAP frame (`Hap1`) blocks as-is** vs FFmpeg's RGBA | yes | **68.02 dB, max diff 2** |
| **HAP Q frame blocks as-is**, plus the float YCoCg maths, vs FFmpeg | yes | **54.44 dB, max diff 3** |

Notes on the GL results:
- `GL_NUM_COMPRESSED_TEXTURE_FORMATS` = 12 on llvmpipe (FXT1, three S3TC formats, ETC2/EAC). RGTC and BPTC are **not** listed, yet they work. Check capability through extension strings, never through this enum.
- glxinfo, core profile: `GL_EXT_texture_compression_s3tc`, `GL_ARB_texture_compression_rgtc`, `GL_ARB_texture_compression_bptc`, `GL_ARB_compressed_texture_pixel_storage`, `GL_ARB_buffer_storage`, `GL_EXT_memory_object_fd`, `GL_KHR_texture_compression_astc_ldr`.
- eglinfo: `EGL_EXT_image_dma_buf_import`, `EGL_EXT_image_dma_buf_import_modifiers`, `EGL_MESA_platform_surfaceless`.

**D. FFmpeg's HAP encoder** (4K testsrc2, 4 threads, generation cost subtracted)

| Variant | Cost per 4K frame | Notes |
|---|---|---|
| HAP | ≈ 15.5 ms CPU, ≈ 11.8 ms wall | |
| HAP Q | ≈ 41.7 ms CPU, ≈ 16.4 ms wall | near real time at 4K60 on 4 cores |
| PSNR on testsrc2 1080p | HAP 38.4 dB, HAP Q 46.5 dB | noisy source: 25.6 / 25.8 dB (chroma noise) |
| Mesa's BPTC encoder, 1080p | 37 ms | test-only quality |

---

## 5. BC7 and BC6H encode cost (for optimise on import and capture to HAP)

| Encoder | Licence | Platform | Published speed | Source |
|---|---|---|---|---|
| bc7e.ispc (Binomial) | Apache-2.0 | CPU, ISPC SSE2…AVX2; caller does the threading | 2–3× faster than ispc_texcomp at equal quality, ~8× faster than its "slow"; profiles `ultrafast…veryslow` | [V extract] https://github.com/BinomialLLC/bc7e; [V] LICENSE |
| bc7e scalar C++ port | Apache-2.0 [U] | CPU, no ISPC | — | [V] note in https://raw.githubusercontent.com/richgel999/bc7enc_rdo/master/README.md |
| **bc7f** (2026) | open source [U on exact licence] | CPU, "strictly bounded O(1)", every BC7 mode | "significantly faster than bc7e.ispc Level 1" | [V] https://richg42.blogspot.com/2026/01/bc7f-new-real-time-analytical-bc7.html |
| Encoders in one table (2048² RGBA, ms) | — | Intel Core Ultra 9 285K | QuickBC7 **14.4**; bc7e u0 **14.8**, u6 59.5; ISPC ufast 18.1, vfast 60.0, fast 107.3, basic 208.2, slow 712.1; NVTT fast (GPU) 257.1; NVTT highest (CPU) 40,936 | [V extract] https://nahjaeho.github.io/papers/CAG26.pdf (Computers & Graphics 2026) |
| etcpak BC7 | BSD-3 [U] | CPU | 7950X MT **225 Mpx/s**; i7-1185G7 MT 21.3; M3 Max MT 65.8 | [V extract] README of a fork, https://github.com/marklove5102/etcpak |
| ispc_texcomp (Intel) | MIT [V text] | CPU ISPC | (above) | [V] https://raw.githubusercontent.com/GameTechDev/ISPCTextureCompressor/master/readme.md |
| BCnEncoder.NET 2.3.0 (2026-03-05) | MIT OR Unlicense | pure C# (netstandard2.0/2.1) | BC1–BC7 encode and decode incl. BC6H; **no published speed** (expect the slowest option) | [V] NuGet nuspec, README |
| GPURealTimeBC6H (K. Narkowicz) | MIT or public domain | GPU HLSL compute | 256×256×6 cube: **Fast 0.02 ms, Quality 0.528 ms** on Quadro P4000; used in shipped AA/AAA games | [V] README, LICENSE |
| DirectXTex `BC7Encode.hlsl`, `BC6HEncode.hlsl` (May 7 2026 release) | MIT | GPU (DirectCompute) | — | [V] files exist; README |
| Betsy | [U] | GPU GLSL compute | BC1, BC3, BC4, BC5, BC6H UF (port of GPURealTimeBC6H), ETC; BC7 not listed | [V] https://raw.githubusercontent.com/darksylinc/betsy/master/README.md |
| TouchDesigner HAP R encoder | proprietary | GPU | can exceed the Windows 2 s TDR at high resolution | [V extract] https://docs.derivative.ca/Hap |

Per-frame estimates [E] (scaled by pixel count; 4K = 8.29 Mpx = 1.98 × 2048²):
- **CPU BC7, fastest class** (bc7e ultrafast, QuickBC7): about 29 ms per 4K frame on a 24-core desktop (Core Ultra 9 285K); about 4× that on this 4-core box. That is near real time only on big CPUs, at reduced quality.
- **CPU BC7, quality class** (bc7e basic/slow-like, ISPC basic): 0.4–1.4 s per 4K frame. A 10-minute 4K60 clip (36,000 frames) takes **4–14 hours**.
- **GPU BC6H**: about 0.4 ms (Fast) to 11 ms (Quality) per 4K frame on a 2017 workstation GPU. Real time is feasible.
- **GPU BC1/BC3 YCoCg** (van Waveren-class): real time. FFmpeg's CPU HAP Q encoder already reaches about 60 fps at 4K on 4 cores [M], at stb_dxt quality.

---

## 6. NotchLC

| Fact | Source |
|---|---|
| 12-bit luma, 8-bit U and V, 8-bit alpha (28 bpp plus alpha); "equivalent of 10bit accuracy" | [V] https://manual.notch.one/2026.2/en/docs/reference/notchlc/ (page updated 22 May 2025) |
| Ratios 4:1 to 8:1; at Optimal it is "~30 % larger than HAP Q"; five quality levels (Good, Very Good, Excellent, Optimal, Best); maximum 16384² ("DX11") | same |
| Decoders: 7thSense, disguise ≥ r16, Pixera ≥ 1.8 (also encodes), Watchout ≥ 7 (also encodes), TouchDesigner ≥ 2021 (also encodes), Smode, Ventuz, Modulo Pi, Pandoras Box and others | same |
| **A commercial SDK for integrators** ("contact our sales team"); free Adobe CC plug-ins | same |
| NotchLC Processor CLI: `-quality 0..1000` bands; `-cpu` is "x10 – x100" slower than GPU | [V extract] https://manual.notch.one/2026.1/en/docs/reference/notchlc/advanced-pipeline-tools/notchlc-processor/ |
| Watchout: 12-bit luma and 8-bit chroma, "about 1:4 to 1:5", LZ4 second pass, default output for ProRes | [V] Dataton formats page |
| FFmpeg: LGPL CPU decoder only (`NLC1`, LZF/LZ4/raw, YUVA444P12) | [V] notchlc.c |
| Public bitstream spec | none found [U] |

GPU decode looks feasible: an LZ4 pass on the CPU, then decode of 4×4 blocks with variable bit widths per block in a compute or fragment shader [E]. But the only public description of the format is FFmpeg's LGPL code. A shader written from it is a derivative-work risk unless the work is done clean-room or under the Notch SDK. Keep FFmpeg's CPU decode, loaded at run time, with a layer-budget warning, as in `docs/COMPETITIVE.md`.

---

## 7. Optimise on import: what the others do

### 7.1 Watchout 7 (current user guide) [V]

Sources: https://docs.dataton.com/guide/watchout/assets-asset-manager/asset-manager.html and `/formats-codecs.html` (undated, read 13 Oct 2026).

- **One playout format, WAF (WATCHOUT Asset Format).**
  - "The Runner plays only WAF."
  - Called "optimization, not transcoding": most video is repackaged without re-encoding.
  - Only codecs the Runner cannot play are re-encoded, and "WATCHOUT matches the source quality".
- **Pipeline:**
  - Upload into Asset Manager storage, organised by asset UUID, with tracked progress.
  - Optimize, which also encodes a preview.
  - Store: identical data is kept once and **the original source is retained**.
  - Distribute: Runners download, up to **8 in parallel**.
  - The state then reads `Ok`.
- **Default mapping:**
  - H.264, MPEG-2 → HEVC.
  - HEVC → HEVC "re-encoded to short-GOP (no B-frames)".
  - ProRes 422 family and 4444 opaque → Notch LC; ProRes 4444 alpha → Notch LC Alpha.
  - QuickTime Animation → Notch LC (or Notch LC Alpha).
  - HAP family and Notch LC pass through.
  - Still images → uncompressed Raw.
  - Raw formats include 10-bit RGB, 11-bit YUV 4:2:0 and 16-bit RGBA.
- **Overrides:**
  - Per row in Asset Manager Settings; a coloured arrow marks a non-default row.
  - "Mapping changes apply to future optimizations only".
  - To change an existing asset: delete and re-add it, or "create a new version with the chosen output codec".
- **"WATCHOUT has no HAP R encoder, so a HAP R asset reaches a Runner only by pass-through."**
- **Priority:** Image > Other (models, fonts) > Audio > Video > Image Sequence. "Quick assets become available first."
- **Previews:** about ¼ width × ¼ height, encoded as HAP or HAP Alpha.
- **Status reporting:**
  - Icons for new, queued, uploading, optimizing and dynamic assets.
  - A progress bar, indeterminate when the percentage is unknown.
  - A job-queue summary (syncing, optimizing, uploading, pending).
  - Failed assets show a **red name**, with the error at the top of Properties.
  - Filters include *Preparing* and *Failed*.
- **Inputs and licensing:**
  - Asset Watcher (watched folders) and a web UI on port 3023.
  - Demo mode limits optimisation to HAP, HAP Alpha and 8-bit images. Everything else needs a licence.
- **Resilience:** "The Asset Manager node only needs to be online when you edit the show or add content… During playback the Runners hold local copies."
- **Best practice:** "Pre-optimize before show day… Large shows can take hours."
- **Documentation drift:** an older page (https://docs.dataton.com/watchout-7-new/…) describes HAP as the default target and chunks hashed with Blake3 [V extract]. The current guide describes WAF and Notch LC/HEVC defaults. Dataton has changed the design during Watchout 7's life.

### 7.2 Pixera [V extract]

- **Content Transcoding tool**, https://help.pixera.one/managing-resources/content-transcoding:
  - Targets: VP9, NotchLC, HAP, HAP Alpha, HAP Q, PNG/TIFF/DPX. Presets, crop and resize.
  - Entry points: a right-click Transcode menu on a file or folder, and a "Batch Transcoding folder" that transcodes on drop and then stores the result in the RX-Cache and auto-imports it.
  - The output is saved next to the original with the preset as suffix, and **is added as a file version** of the source.
- NotchLC encodes on the GPU (per the Notch Processor note) [V extract].
- **Changelog 1.8:**
  - "Frame threading activated for NotchLC and Hap".
  - Also "HAP decoding set to single thread usage due to deadlock".
  - "Clients continue playback if connection is lost" (https://help.pixera.one/changelogs-version-overviews/1215930-changelog-1-8).
- **Changelog 26.1:** odd resolutions are auto-adjusted when transcoding to HAP.

### 7.3 disguise [V extract]

- Designer plays HAP, HAP Alpha, HAP-Q (**not HAP-Q Alpha**), DXV v2, NotchLC and ProRes.
- For transcoding it recommends "standard rendering applications such as Adobe Media Encoder or the freeware MPEG Streamclip". No built-in optimiser was found.
- Sources: https://help.disguise.one/designer/content-management/video-codecs/transcoding-software, `/hap-hapq-codec`, `/video-codec-overview`.

### 7.4 TouchDesigner [V extract]

- Movie File Out encodes HAP, HAP Q, HAP R, HAP HDR and NotchLC.
- It always writes 12 chunks.
- "Hap Secondary Compression" can be switched off to write raw BC data: no CPU decompression, and less memory bandwidth with "High Performance Read".
- Source: https://docs.derivative.ca/Hap; Movie File Out TOP page dated 2026-05-05.

### 7.5 Resolume

DXV3 is Resolume's GPU codec. FFmpeg decodes it, but FFmpeg's encoder writes DXT1 only (§3.5). Whether Resolume's own transcoder (Alley) optimises on import was not checked [U].

### 7.6 What a best-in-class import pipeline does (synthesis) [E, derived from 7.1–7.5]

1. **Rules, not a single target.** Map by source codec, alpha, bit depth, HDR transfer, resolution and frame rate, *and by the node's measured capabilities*: hardware decode support, disk MB/s, VRAM.
2. **The source is always kept.** Derived assets are versions, never replacements.
3. **Content-addressed derivation.** The key is (source hash, recipe hash, encoder build), so the same work is never done twice, anywhere.
4. **Lossless operations first.** Pass-through, re-chunking, stripping or adding the second-stage compressor and padding odd sizes all change bytes but not pixels.
5. **Background and parallel work.** Jobs run in priority order (stills first, like Watchout), and can be cancelled and resumed.
6. **Distributed work across idle machines.** Intra-only codecs (HAP, NotchLC) allow **segment-parallel** encoding: any frame range is independent.
7. **A quality score that explains itself** (PSNR/SSIM/VMAF on sampled frames) and a **budget check**: will this play N layers on that node?
8. **Visible progress and errors in plain words** on the desk, over the wire and through MCP.
9. **Never on an armed show node** (PlayMap R2.11).

---

## 8. Zero-copy hardware decode with FFmpeg 9

### 8.1 What copy-back costs

- **PlayMap's path today** (§1): the GPU decodes, `av_hwframe_transfer_data` copies the frame into system memory (staging map plus copy), and the renderer uploads it again (PBO). The data crosses the bus twice and the CPU touches it at least once.
- **Traffic per 4K60 stream** [E]:
  - NV12 is 12.4 MB per frame, so about **746 MB/s down + 746 MB/s up**.
  - P010 (10-bit HDR) doubles that to 1.49 GB/s each way.
  - The CPU copy out of uncached or write-combined staging memory is the expensive part.
- **Field reports:**
  - FFmpeg 5.1.2, VAAPI, Intel Baytrail: `av_hwframe_transfer_data` took ~20 ms at 1080p and ~40 ms at 3840×1714, most of it in `vaMapBuffer` [V extract, https://www.mail-archive.com/libav-user@ffmpeg.org/msg13087.html and msg13089, 2022-10].
  - NVDEC, 4K60, 2026: a host round trip of the luma plane added about 4 ms at p99, which on its own still held 60 fps. Adding CPU work on top broke it [V extract, https://ramwise.dev/blog/realtime-gpu-video-processing/, 2026-08-31].
  - mpv: copy-back modes "are often less efficient than the direct modes, and may not help too much over software decoding if you are short on CPU resources" [V, mpv `DOCS/man/options.rst`].

### 8.2 D3D11VA with PlayMap's own `ID3D11Device` (Windows; needs R2.1 D3D11 backend)

FFmpeg API [V `libavutil/hwcontext_d3d11va.h` and `.c`, master and the installed FFmpeg 9 header; APIchanges]:

- `AVD3D11VADeviceContext { ID3D11Device *device; ID3D11DeviceContext *device_context; ID3D11VideoDevice *video_device; ID3D11VideoContext *video_context; void (*lock)(void*); void (*unlock)(void*); void *lock_ctx; UINT BindFlags; UINT MiscFlags; }`.
  - Only `device` is mandatory. FFmpeg releases the interfaces when the context is freed.
  - The lock "must be recursive" and protects `device_context`/`video_context` calls.
  - **`BindFlags`/`MiscFlags` at device level** were added **2026-01-26, lavu 60.24.100**, so they are present in FFmpeg 9.
  - The dictionary keys `SHADER`, `UAV`, `RTV` and `SHARED` passed to `av_hwdevice_ctx_create` set them.
  - `D3D11_BIND_SHADER_RESOURCE` is dropped for `DXGI_FORMAT_420_OPAQUE`.
- `AVD3D11VAFramesContext { ID3D11Texture2D *texture; UINT BindFlags; UINT MiscFlags; AVD3D11FrameDescriptor *texture_infos; }`.
  - "Decoding requires a single array texture."
  - `MAX_ARRAY_SIZE 64`: "Driver specification limits ArraySize to 64 for decoder-bound resources".
- A decoded `AVFrame` (`AV_PIX_FMT_D3D11`) holds `data[0] = ID3D11Texture2D*` and `data[1] = (intptr_t)array index`.
- Format map: `DXGI_FORMAT_NV12 ↔ AV_PIX_FMT_NV12`, `DXGI_FORMAT_P010 ↔ AV_PIX_FMT_P010`.
- FFmpeg.AutoGen 9.0.1.1 (NuGet) contains the type names `AVD3D11VADeviceContext`, `AVD3D11VAFramesContext` and `AVD3D11FrameDescriptor`, and the functions `av_hwdevice_ctx_alloc`, `av_hwdevice_ctx_init` and `av_hwframe_map` [V, metadata strings in `lib/net8.0/FFmpeg.AutoGen.dll`].
  - Whether its `AVD3D11VADeviceContext` already has the 2026 `BindFlags` fields is **unverified**. Check `sizeof` against the C header before use.
  - It does **not** contain `AVDRMFrameDescriptor`, `AVVAAPIDeviceContext`, `AVCUDADeviceContext` or `AVVkFrame`; declare those by hand.

Recipe [E, from the API above and mpv's implementation]:
1. Create the decode device on the render adapter. Call `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)` and set `device` (AddRef first). Set `BindFlags = D3D11_BIND_DECODER | D3D11_BIND_SHADER_RESOURCE`. Set `lock`/`unlock` to the renderer's recursive lock, or enable `ID3D10Multithread::SetMultithreadProtected`. Then call `av_hwdevice_ctx_init` and `ctx->hw_device_ctx = av_buffer_ref(dev)`.
2. Size the frame pool: decoder DPB + render queue depth + 2, and ≤ 64 slices.
3. **Default ("1-copy"):** `CopySubresourceRegion(dst, 0, 0, 0, 0, frameTex, D3D11CalcSubresource(0, index, 1), &box)` into a pooled NV12/P010 texture with `D3D11_BIND_SHADER_RESOURCE`. Then two SRVs:
   - NV12: `DXGI_FORMAT_R8_UNORM` (Y) and `DXGI_FORMAT_R8G8_UNORM` (UV, half size).
   - P010: `R16_UNORM` and `R16G16_UNORM`.

   [V view formats: https://docs.dndocs.com/n/SharpDX.DXGI/4.2.0/api/SharpDX.DXGI.Format.html (a mirror of the DXGI_FORMAT notes); Microsoft's `NV12VideoTexture.cpp` sample; ANGLE's `formatutils11.cpp`].

   This is exactly mpv's default path: "the video image will be copied (GPU-to-GPU) from the decoder surface to a shader resource" [V mpv options.rst]. The copy stays on the GPU: about 12 MB per 4K frame at hundreds of GB/s, which is negligible [E].
4. **Opt-in "zero-copy":** SRVs straight on the decoder array slice (`D3D11_SRV_DIMENSION_TEXTURE2DARRAY`, `FirstArraySlice = index`, `ArraySize = 1`). mpv warns: "can cause the image to be sampled incorrectly on the bottom and right edges due to padding, and may invoke driver bugs, since Direct3D 11 technically does not allow sampling from a decoder surface (though most drivers support it)" [V mpv options.rst, `--d3d11va-zero-copy`].
5. In both modes, **hold the `AVFrame` reference until the GPU has finished with it**: an `ID3D11Query` of type `D3D11_QUERY_EVENT`, or a fence. Crop UV to `width/coded_width` because of padding.

### 8.3 GL on Windows before D3D11 lands

- **`WGL_NV_DX_interop2`** (rev. 2, 2011-10-04) registers `ID3D11Texture2D`/`Buffer` for GL use [V https://raw.githubusercontent.com/KhronosGroup/OpenGL-Registry/main/extensions/NV/WGL_NV_DX_interop2.txt].
  - Restrictions: `D3D11_USAGE_DEFAULT`, WDDM, "must be multithreaded".
  - **No planar YUV formats are defined.**
  - mpv's DXVA2→GL interop first converts to RGB with `IDirect3DDevice9Ex_StretchRect` into an `X8R8G8B8` shared surface [V mpv `hwdec_dxva2gldx.c`].
  - So with GL output on Windows, a decoded frame must be converted to RGB first, for example with `ID3D11VideoContext::VideoProcessorBlt` into an RGBA/R10G10B10A2 texture [U on the API choice; standard D3D11 video API]. Then register that texture through interop2 and lock and unlock it each frame.
- **`EXT_external_objects_win32`** (2023-08-29) imports `HANDLE_TYPE_D3D11_IMAGE_EXT (0x958B)` and `_KMT_EXT (0x958C)` as GL memory objects [V spec]. Its vendor coverage on Windows GL drivers was not verified [U].
- **Recommendation:** do not invest in GL-on-Windows zero-copy. Ship hardware decode with copy-back on GL, and do zero-copy on the D3D11 backend (R2.1 → R2.2).

### 8.4 Linux: VAAPI → DRM PRIME → EGL dmabuf

- FFmpeg path:
  - `av_hwframe_map(drm, vaapi, AV_HWFRAME_MAP_READ)` gives `AV_PIX_FMT_DRM_PRIME` with an `AVDRMFrameDescriptor`:
    - `nb_objects` and `objects[4]` (`{int fd; size_t size; uint64_t format_modifier}`);
    - `nb_layers` and `layers[4]` (`{uint32_t format; int nb_planes; planes[4] {int object_index; ptrdiff_t offset; ptrdiff_t pitch}}`) [V `hwcontext_drm.h`].
  - Or `vaExportSurfaceHandle(..., VA_EXPORT_SURFACE_READ_ONLY | VA_EXPORT_SURFACE_SEPARATE_LAYERS, …)` followed by `vaSyncSurface`, which is what mpv does [V mpv `hwdec_vaapi.c`].
- GL import:
  - `eglCreateImageKHR(dpy, EGL_NO_CONTEXT, EGL_LINUX_DMA_BUF_EXT (0x3270), NULL, attribs)` once per plane: `DRM_FORMAT_R8` for Y and `DRM_FORMAT_GR88` for UV (mpv).
  - Then `glEGLImageTargetTexture2DOES`.
  - Modifiers go in `EGL_DMA_BUF_PLANEn_MODIFIER_LO/HI_EXT (0x3443…0x344A)`, after checking with `eglQueryDmaBufFormatsEXT`/`eglQueryDmaBufModifiersEXT` (`external_only` flag) [V `EGL_EXT_image_dma_buf_import_modifiers.txt` v5 2019-03-12; mpv `dmabuf_interop_gl.c`].
  - The colour hints `EGL_YUV_COLOR_SPACE_HINT_EXT` and `EGL_SAMPLE_RANGE_HINT_EXT` exist but apply only to single-image YUV import [V `EGL_EXT_image_dma_buf_import.txt`].
- **Requirement:** the GL context must come from **EGL**. PlayMap uses Silk.NET.Windowing, which on X11 defaults to GLX via GLFW [U]. Check whether Silk.NET lets the GLFW EGL context API be chosen, or create our own EGL context for output windows [U].
- Mesa here exposes both dmabuf extensions [M eglinfo].

### 8.5 NVIDIA: NVDEC/CUDA → GL

- mpv registers the GL texture with `cuGraphicsGLRegisterImage(..., CU_GRAPHICS_REGISTER_FLAGS_WRITE_DISCARD)`, maps it, and gets a `CUarray` with `cuGraphicsSubResourceGetMappedArray`.
- Each frame is then `cuMemcpy2DAsync` from `CU_MEMORYTYPE_DEVICE` (the frame) to `CU_MEMORYTYPE_ARRAY` (the texture), followed by `cuStreamSynchronize` [V mpv `hwdec_cuda_gl.c`, `hwdec_cuda.c`].
- That is one device-to-device copy with no bus crossing. NVIDIA's own forum measured host↔device bandwidth at about 21 GB/s on a 3090 once timed correctly [V extract, https://forums.developer.nvidia.com/t/video-codec-sdk-copytodeviceframe-data-rate-only-1-8gb-s/192315, 2021].
- FFmpeg.AutoGen does not bind `AVCUDADeviceContext`; declare it by hand [V metadata].

### 8.6 macOS: VideoToolbox

- mpv's libplacebo path wraps `CVPixelBuffer` (IOSurface-backed) as Metal textures with `CVMetalTextureCacheCreate` and `CVMetalTextureCacheCreateTextureFromImage` [V mpv `hwdec_vt_pl.m`].
- The GL route (`CGLTexImageIOSurface2D` onto rectangle textures) is legacy, since GL is deprecated on macOS [U; not opened].
- This fits roadmap item L6 (Metal engine).

### 8.7 Vulkan video and ProRes on the GPU

- FFmpeg 9 in this build: the `vulkan` hwaccel covers h264, hevc, av1, vp9, **prores**, prores_raw, ffv1 and apv [M].
- Changelog: "ProRes Vulkan hwaccel" is in **8.1**; "ProRes RAW VideoToolbox hwaccel" and "APV Vulkan hwaccel" are in 9.0 [V https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/Changelog].
- Frames stay in `VkImage` (`AVVkFrame`, not bound by AutoGen). Using them from GL or D3D11 needs `VK_KHR_external_memory_win32/fd` plus `GL_EXT_memory_object` or a D3D11 shared handle.
- ProRes compute decode performance is unmeasured [U]. It could let PlayMap play ProRes on the GPU, *without* the transcode that Watchout performs.

### 8.8 Summary per platform [E]

| Platform | Default (ship first) | Next | Notes |
|---|---|---|---|
| Windows, D3D11 backend | D3D11VA on PlayMap's device, one GPU-side `CopySubresourceRegion`, NV12/P010 SRVs | opt-in zero-copy after preflight proves it on this driver | `BindFlags` device field (FFmpeg ≥ lavu 60.24) |
| Windows, GL backend (interim) | copy-back (today) | VideoProcessorBlt to RGBA + `WGL_NV_DX_interop2` | interop2 has no NV12 |
| Linux, Intel/AMD | VAAPI copy-back (today) | VAAPI → DRM PRIME → EGL dmabuf (R8 + GR88 layers) | needs an EGL context |
| Linux/Windows, NVIDIA | CUDA copy-back (today) | NVDEC → `cuMemcpy2DAsync` into a GL texture | one on-GPU copy |
| macOS | VT copy-back | CVPixelBuffer → Metal texture cache | L6 |
| Any Vulkan GPU | — | Vulkan hwaccel incl. ProRes | measure first |

---

## 9. Design implications for PlayMap

What follows is what would make PlayMap comparable to, or better than, Watchout, Pixera, disguise and TouchDesigner on GPU codecs. Items are ordered by dependency. [E] unless a fact is cited above.

### 9.1 A HAP GPU source that beats FFmpeg's CPU path (R2.3)

**Data path (no RGBA anywhere):**
1. libavformat demuxes as today. For `codec_id == AV_CODEC_ID_HAP`, or `codec_id == NONE` with tag `Hap7`/`HapH`, the engine opens a **`HapVideoSource`** instead of `StreamDecoder`, so FFmpeg's `hap` decoder is never opened. The demuxer is proven for `Hap7` (§3.4).
2. **Parse in Core.** `PlayMap.Core/Media/Hap/HapFrame.cs` is pure and has no native code: section headers (3- or 4+4-byte), the type nibbles, `0x0D` multi-image, and the `0x01` container with tables `0x02/0x03/0x04`.
   - Validate everything before touching memory: sizes in bounds; chunk count equal across tables; total uncompressed size = ⌈w/4⌉·⌈h/4⌉·block bytes, which is FFmpeg's own consistency check; texture nibble consistent with the fourcc.
   - Refuse a bad frame with the reason, keep the last good frame and count it in `SourceStats` (doctrine: *attempts are not facts*).
3. **Second stage.**
   - Read each chunk's Snappy preamble first (§2.5), so every output offset is known.
   - Then decompress all chunks **in parallel directly into a persistently mapped upload buffer**: GL `ARB_buffer_storage` with `MAP_PERSISTENT|MAP_COHERENT`, or a D3D11 staging-texture ring. No intermediate `byte[]`.
   - Use Snappier's `Snappy.Decompress(ReadOnlySpan<byte>, Span<byte>)` (BSD-3), or a small bounded decoder of our own written from `format_description.txt`.
   - Raw chunks (`0x0A`, or TouchDesigner's "no secondary compression") are copied straight from the file read buffer.
4. **Upload.**
   - GL: `glCompressedTexSubImage2D` from the PBO offset, into a texture of ⌈w/4⌉·4 × ⌈h/4⌉·4, sampled with UV scaled by `w/coded_w`.
   - D3D11: `CopySubresourceRegion` from staging into a `DXGI_FORMAT_BC*` texture.
   - **Formats:** `0xB` BC1, `0xE` BC3, `0xF` BC3 plus the YCoCg shader, `0x1` BC4, `0xC` BC7, `0x2` BC6H UF16, `0x3` BC6H SF16; HAP Q Alpha = BC3 + BC4 sampled together.
   - Use `_SRGB` variants for HAP, HAP Alpha and HAP R once R2.5's linear pipeline lands. Never use them for HAP Q, which is linearised after the YCoCg step.
5. **Capability check by extension string, not by `GL_COMPRESSED_TEXTURE_FORMATS`** (§4C): `GL_EXT_texture_compression_s3tc`, `GL_ARB_texture_compression_rgtc`, `GL_ARB_texture_compression_bptc`.
   - Fallback when a format is missing: CPU block decode to RGBA. BC1/BC3/BC4 decoders are short; BC7/BC6H could come from BCnEncoder.NET (MIT/Unlicense) or our own code. **Never from FFmpeg** (LGPL).
6. **Prefetch by show time.** HAP frames are independent, and *content is a pure function of show time*, so the reader knows exactly which byte range it needs next. Issue large aligned asynchronous reads (`FILE_FLAG_NO_BUFFERING` + overlapped on Windows, `O_DIRECT`/io_uring on Linux [U for the io_uring choice]) N frames ahead. Seek is free: any frame, no GOP.

**Types to add:**
- `PixelLayout.Bc1, Bc3, Bc3YCoCg, Bc4, Bc3YCoCgBc4 (HAP Q Alpha), Bc7, Bc6hU, Bc6hS`.
- `TextureFormat` compressed members.
- `GlDevice.CreateCompressedTexture(...)` and `UploadCompressedFromUnpackBuffer(...)`.
- `GpuTexture.ContentScale` for the padding crop.
- STATE per source: `decode: "hap-gpu"` with `format`, `chunks`, `secondStage`, `cpuMsPerFrame`, `uploadBytesPerFrame`.

**Targets** (from §4, at 4K HAP Q, before any GPU work):
- Second stage ≤ 2.5 ms on one core per layer-frame, against FFmpeg's 53–102 ms plus 10–15 ms swscale.
- Upload 8.3 MB instead of 33.2 MB.
- At about 2 ms per layer-frame, a 4-core node has CPU room for roughly 30 layers at 4K60. **Disk becomes the limit**: about 500 MB/s per raw 4K60 HAP Q layer, so an NVMe at about 3–7 GB/s carries 6–14 layers. That is why Watchout says "The read speed of the SSD usually is" the bottleneck [V].
- Publish this as the "layer budget" in preflight (R2.6).

**Lossless operations the source can offer on import**, which no competitor offers as one-click operations [E]:
- **Re-chunk**: a 1-chunk HAP file, common from older encoders, gets N ≥ cores chunks. Decompress, split on block boundaries, recompress; the pixels are unchanged. §4B shows the cost of one chunk: 1.37 ms on 4 threads vs 0.50 ms with 16 chunks.
- **Strip or add Snappy** per node: strip on a CPU-starved 8K node, add on a disk-starved node.
- **Pad odd dimensions** without re-encoding: append edge blocks and record the crop.

### 9.2 HAP R and HAP HDR as the differentiator

- **Decode them both on day one.** The parser is the same and only the GL/DXGI enum changes. That puts PlayMap ahead of FFmpeg-based players (Millumin, MadMapper, Resolume [U on their HAP R status]), KlakHap, disguise (not listed) and Watchout for HAP HDR.
- **An explicit HDR interpretation for HAP HDR.** The spec has no colour metadata (§2.10). Read the mov `colr` (nclx) atom when present. Otherwise default to *linear, 1.0 = SDR reference white*, with a per-asset override: transfer, scale or nits. Show the interpretation in STATE and on the desk, so it is never a hidden guess. Write `colr` on every HAP HDR file PlayMap creates.
- **Write HAP R and HAP HDR files through libavformat**, with `codec_tag = 'Hap7'/'HapH'` and `strict_std_compliance = FF_COMPLIANCE_UNOFFICIAL` (§3.4). Interop target: TouchDesigner, Pixera (HAP R), VDMX, Watchout (pass-through).

### 9.3 Encoders (the engine of optimise on import and capture to HAP)

- **GPU first.**
  - BC1, and BC3 with YCoCg (HAP, HAP Alpha, HAP Q): fragment-shader or compute encoders in the van Waveren/Castaño style. They are real-time class, and FFmpeg's CPU encoder already does about 60 fps at 4K on 4 cores [M].
  - **BC6H** (HAP HDR): port GPURealTimeBC6H (MIT; Betsy already has a GLSL port).
  - **BC7** (HAP R): port DirectXTex `BC7Encode.hlsl` (MIT) to D3D11 CS 5.0, and to GL 4.3 compute where available. On GL 3.3, which has no compute, encode on the D3D11 path or the CPU.
  - **Split every dispatch into tiles** to stay well under the Windows 2 s TDR. TouchDesigner documents this failure for HAP R [V].
- **CPU fallback.**
  - bc7e (Apache-2.0) via its scalar C++ port, or bc7f. Built as a native library for us; it is not FFmpeg.
  - BCnEncoder.NET (MIT) as a last-resort pure-managed path, with no speed promise.
  - FFmpeg's `hap` encoder (loaded at run time) is fine for quick HAP and HAP Q proxies and previews.
- **Quality settings in plain words**: *Fast*, *Show*, *Archive*. Each maps to an encoder profile and is reported with its measured PSNR/SSIM. VMAF is available because the build has `--enable-libvmaf` [M configure line].

### 9.4 Optimise on import that beats Watchout's (R2.4)

| Watchout 7 does | PlayMap should do |
|---|---|
| A fixed codec mapping per source codec; changes apply only to future imports | **Rules on properties and node capabilities**: codec, alpha, bit depth/HDR, resolution, fps, target node's preflight (hardware decode support, disk MB/s, VRAM). A rule change marks affected derivatives *stale* and offers "re-optimise 14 assets (est. 3 min)" |
| HAP R by pass-through only; no HAP HDR | **Encode HAP R and HAP HDR** on the GPU; offer them for HDR or alpha content |
| One Asset Manager does all optimisation | **Segment-parallel, distributed jobs**: HAP and NotchLC are intra-only, so split a clip into frame ranges, encode on every idle node and GPU (desk, spare nodes), concatenate. Refused on armed nodes, low priority in rehearsal (R2.11) |
| Re-encodes or passes through | **Lossless first**: pass-through with validation, re-chunk, pad odd sizes, add or strip Snappy, all without re-encoding (§9.1) |
| Content-addressed storage, source kept | Same, keyed `sha256(source) + recipe hash + encoder build`. The derivative is a *version* of the source (as in Pixera). `MEDIA FETCH` already moves things by hash, so derivatives sync like any asset |
| Progress bar, red name on failure | Progress, **ETA**, frames done, and **resume after crash** at segment granularity. Errors in plain words, in STATE, MCP and the desk; each job is cancellable |
| No quality report | **A quality score that explains itself**: PSNR/SSIM (and VMAF) on sampled frames vs the source, file MB/s at show fps, and "plays N layers on node X" from that node's disk benchmark |
| Previews at quarter resolution in HAP | Same for desk previews, plus a **scrub strip** (thumbnails at every second, built during the encode) |
| ProRes → NotchLC (licensed SDK) | ProRes plays as-is where the FFmpeg Vulkan ProRes hwaccel (8.1+) proves itself in preflight; otherwise → HAP Q or HAP R. NotchLC sources pass through, decoded on the CPU by FFmpeg, with a layer-budget warning |
| H.264 → short-GOP HEVC | H.264/HEVC stay as they are when the node decodes them in hardware and they meet a seek-latency budget; otherwise → HAP Q. "Short-GOP HEVC" can be an optional rule |

Proposed wire verbs, consistent with `VerbCatalog.cs`. These are proposals and are not in the catalogue yet:
- `MEDIA OPTIMISE <media>|ALL [AS <profile>] [ON <node>|IDLE]` (Review risk).
- `MEDIA OPTIMISE CANCEL <job>`.
- `MEDIA VERSIONS <media>`.
- `MEDIA USE <media> <version>` (Review).
- `MEDIA RULES` / `MEDIA RULE SET <json>` (Review).
- `MEDIA STATUS` grows a `jobs[]` array with `{id, media, recipe, node, state, framesDone, framesTotal, etaS, error}`.

### 9.5 Zero-copy decode (R2.2)

- **Windows D3D11:** PlayMap's own device goes into `AVD3D11VADeviceContext.device`, with device-level `BindFlags = DECODER|SHADER_RESOURCE` and the renderer's recursive lock.
  - Default: one GPU-side `CopySubresourceRegion` into a pooled NV12/P010 SRV texture, with views `R8/R8G8` (NV12) or `R16/R16G16` (P010).
  - Direct sampling of the decoder slice is opt-in and only after a per-driver preflight check that compares against the 1-copy result.
  - Keep each frame alive until an event query or fence reports the GPU finished with it.
- **Linux:** VAAPI to DRM PRIME to EGL images per plane, once output windows run on EGL contexts. Keep CUDA copy-back as the NVIDIA fallback until the CUDA↔GL copy path is written.
- **STATE per source:** `decodePath: "hap-gpu" | "hw-zero-copy" | "hw-gpu-copy" | "hw-copy-back" | "software"`, with the measured `cpuMsPerFrame` and `busBytesPerFrame`. This is the "zero-copy yes/no" asked for in R2.2.
- **Do not build GL-on-Windows zero-copy** (no NV12 in interop2). It waits for the D3D11 backend.

### 9.6 Preflight, desk and MCP (R2.6)

- For every asset on every node, preflight checks:
  - the decode path it will get;
  - frames per second decoded in a 2-second burst;
  - compressed-texture formats available on the node;
  - disk MB/s sustained at the asset's bitrate;
  - VRAM;
  - and from those, a **layer budget** per codec ("this node: 9 × 4K60 HAP Q, 2 × 4K60 H.264").
- Assets that FFmpeg can only decode on the CPU (NotchLC, ProRes without Vulkan, DXV) are flagged with an "optimise?" fix.
- HAP files with one chunk, or with odd dimensions, are flagged with the lossless fix.

### 9.7 Tests that run on this build machine

The headless test suite needs only llvmpipe and FFmpeg 9 [M]:
- **Spec conformance of the Core parser.** Hand-built frames for every type byte, the 8-byte header form, missing offset table, raw-plus-Snappy mixed chunks, `0x0D` pairs, truncated and oversized tables (each must be refused with a reason), unknown inner sections (must be skipped).
- **Cross-check against FFmpeg.** FFmpeg encodes `hap`, `hap_alpha` and `hap_q` with `-chunks 1..64` and both compressors. PlayMap decodes on llvmpipe and reads back; FFmpeg decodes on the CPU. Assert PSNR ≥ 60 dB and max diff ≤ 2 for HAP/HAP Alpha, and PSNR ≥ 50 dB and max diff ≤ 4 for HAP Q (the observed 68 dB/2 and 54 dB/3).
- **HAP R and HAP HDR round trips.** Mesa compresses with `glTexImage2D` to `BPTC` internal formats; the test wraps the blocks in a `0xBC`/`0xB2` HAP frame, muxes with tag `Hap7`/`HapH` through libavformat (`strict unofficial`), then demuxes, decodes on the GPU and reads back. Assert bit-exact equality with Mesa's own decode.
- **Allocation test:** no allocation per frame (R2.8) on the HAP path.

### 9.8 Licence hygiene

| Can be used or ported into PlayMap (keep notices) | Reference only, do not copy |
|---|---|
| Vidvox HAP spec and reference (BSD-2); Snappier (BSD-3) or our own Snappy; GPURealTimeBC6H (MIT/PD); DirectXTex shaders (MIT); BCnEncoder.NET (MIT/Unlicense); bc7e (Apache-2.0); stb_dxt-class algorithms (public domain) | FFmpeg `hapdec.c`, `texturedsp*.c`, `notchlc.c`, `dxv.c` (LGPL; this build is GPL); mpv (GPL/LGPL); the Demolition FFmpeg fork; NotchLC internals (needs a clean room or the Notch SDK); Betsy until its licence is checked [U] |

### 9.9 Open questions and risks

- How long does the BC7 GPU encode take per 4K frame on a mid-range show GPU, after porting DirectXTex `BC7Encode.hlsl`? Measure before promising "HAP R on import" timings.
- Does Silk.NET.Windowing let us pick an EGL context on X11/Wayland [U]? If not, Linux zero-copy needs our own EGL window path.
- Which drivers break direct sampling of D3D11 decoder slices? Build the preflight A/B check before enabling it anywhere.
- Is a binding missing from FFmpeg.AutoGen 9.0.1.1 (`AVD3D11VADeviceContext.BindFlags` layout, DRM/VAAPI/CUDA structs)? Verify `sizeof` in a test.
- HAP HDR colour semantics are not standardised. Coordinate with Vidvox and Derivative on a `colr` convention, and document PlayMap's default.
- Disk, not CPU, will cap HAP layer counts. The prefetcher and an honest per-node disk benchmark matter as much as the decoder.

---

### Source index (all read 13 Oct 2026 unless marked)

- Vidvox HAP spec and reference: https://github.com/Vidvox/hap (`documentation/HapVideoDRAFT.md`, `source/hap.c`, `source/hap.h`, `LICENSE`, `README.md`) [V]
- Vidvox posts: https://vdmx.vidvox.net/blog/hap-r-benchmarks (2025-08-07) [V]; https://vdmx.vidvox.net/blog/vdmx6 (2024-09-12) [V extract]
- FFmpeg master: `libavcodec/hapdec.c`, `hap.h`, `hap.c`, `hapenc.c`, `texturedsp.c`, `texturedspenc.c`, `notchlc.c`, `dxv.c`, `libavformat/isom_tags.c`, `libavutil/hwcontext_d3d11va.h/.c`, `hwcontext_drm.h`, `doc/APIchanges`, `Changelog` via raw.githubusercontent.com/FFmpeg/FFmpeg/master [V]
- DemolitionStudios/FFmpeg `hap_bc6h`: `libavcodec/hapdec.c`, `hap.h`, `hapenc.c`, `libavformat/isom_tags.c` [V]; commit list [V]; https://github.com/vidvox/hap/issues/47 [V extract]
- Snappy: https://raw.githubusercontent.com/google/snappy/main/format_description.txt [V]; Snappier README and `Snappy.cs` [V]; NuGet index and nuspec for snappier, bcnencoder.net, ironsnappy, k4os.compression.lz4, ffmpeg.autogen, silk.net.direct3d11 (2.23.0), vortice.direct3d11 (3.8.3) [V]
- Khronos: `WGL_NV_DX_interop2.txt`, `EXT_external_objects_win32.txt`, `ARB_texture_compression_bptc.txt`, `EXT_texture_compression_s3tc.txt`, `EGL_EXT_image_dma_buf_import(_modifiers).txt` [V]
- Microsoft: BC6H format; Block Compression (D3D10); DXGI_FORMAT [V]
- mpv master: `DOCS/man/options.rst`, `video/out/d3d11/hwdec_d3d11va.c`, `video/out/opengl/hwdec_dxva2gldx.c`, `hwdec_d3d11egl.c`, `video/out/hwdec/hwdec_vaapi.c`, `dmabuf_interop_gl.c`, `hwdec_cuda.c`, `hwdec_cuda_gl.c`, `hwdec_vt_pl.m` [V]
- Dataton Watchout 7 guide: `/guide/watchout/assets-asset-manager/asset-manager.html`, `/formats-codecs.html` [V]; `/watchout-7-new/…` pages [V extract]; https://knowledge.dataton.com/knowledge/video-encoding-best-practices-for-watchout [V extract]
- Pixera: content-transcoding, supported-file-formats, changelog 1.8, 26.1 [V extract]
- disguise: transcoding-software, hap-hapq-codec, video-codec-overview, supported-file-formats [V extract]
- Derivative: https://docs.derivative.ca/Hap, Movie File Out TOP (2026-05-05), forum 2022-06-14 [V extract]
- Notch: manual 2026.2 NotchLC (updated 2025-05-22) [V]; NotchLC Processor 2026.1 [V extract]
- Encoders: bc7enc_rdo README [V]; BinomialLLC/bc7e LICENSE [V], README [V extract]; bc7f post [V]; GPURealTimeBC6H README and LICENSE [V]; DirectXTex README and shader files [V]; Betsy README [V]; ISPCTextureCompressor readme [V]; CAG26 paper [V extract]; etcpak fork README [V extract]; BCnEncoder.NET README [V]
- KlakHap README [V]; vvvv blog 2022-04-20 [V extract]
- Copy-back field data: libav-user 2022-10 [V extract]; ramwise.dev 2026-08-31 [V extract]; NVIDIA forum 2021 [V extract]
