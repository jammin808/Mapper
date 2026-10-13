# Work package `zerocopy`: zero-copy hardware decode

*Design for round 2, written 13 Oct 2026 against `main` at `c12b186` (`/home/user/mapper`). Roadmap item R2.2, with
the Linux, NVIDIA and macOS routes that R2.2 leaves out.*

**Evidence marks** (as in the research reports): **[V]** I opened the source in this session; **[M]** measured on
this machine on 13 Oct 2026 (the probes and their outputs are under `scratchpad/r2/bench/`); **[U]** unverified;
**[E]** an estimate, with the arithmetic shown. Research reports: `r2/research/codecs.md` (§8 and §9.5 are the basis
of this package), `r2/research/present.md` (§5 and §10 set the Windows output architecture this package plugs into),
and `r2/research/videoio.md` (§9.2, the GPU lease this package shares with Spout).

New measurements made for this design (all on llvmpipe, Mesa 25.2.8, Xvfb :99):

| Probe | Result | Files |
|---|---|---|
| Can PlayMap's own windowing (Silk.NET.Windowing 2.23.0 on GLFW) give **EGL** contexts on X11? | **Yes.** One hint, `GlfwProvider.GLFW.Value.WindowHint(WindowHintContextApi.ContextCreationApi, ContextApi.EglContextApi)`, set before `Window.Create`, makes the hidden primary context and a shared output window both EGL contexts (`eglGetCurrentContext() != 0`, `glXGetCurrentContext() == 0`). The display has `EGL_EXT_image_dma_buf_import` and `_modifiers`. Without the hint both are GLX. Silk.NET.Windowing.Glfw never calls `glfwDefaultWindowHints`, so the hint survives [M] | `bench/eglctx/Program.cs`, `eglctx.out` |
| Does a dmabuf import per plane work, and is it exact? | **Yes, bit-exact.** NV12 imported as `DRM_FORMAT_R8` + `DRM_FORMAT_GR88` EGLImages (internal formats `GL_R8` 0x8229 / `GL_RG8` 0x822B). P010 imported as `R16` + `GR1616` (`GL_R16` / `GL_RG16`). 1080p and 4K, with and without an explicit `DRM_FORMAT_MOD_LINEAR`; 0 mismatches on read-back. The same import works inside the GLFW EGL context. The "dmabuf" is a `memfd`: llvmpipe imports by `mmap`, so the import half of zero-copy is testable in CI here [M] | `bench/proto/dmabuf_probe.c`, `dmabuf_probe.out`; `eglctx.out` |
| What does a per-frame import cost in API terms? | `eglCreateImageKHR` ×2 + `glEGLImageTargetTexture2DOES` ×2 + `eglDestroyImageKHR` ×2 = **0.013–0.018 ms per frame** at 1080p and 4K (llvmpipe; a GPU driver's kernel import is not included) [M] | `dmabuf_probe.out` |
| Does FFmpeg.AutoGen 9.0.1.1 bind the FFmpeg 9 device-level `BindFlags`? | **Yes.** `AVD3D11VADeviceContext` is 64 bytes with `BindFlags@56`, `MiscFlags@60`, matching `/opt/ffmpeg9/include/libavutil/hwcontext_d3d11va.h`. `AVDRMFrameDescriptor`, `AVVAAPIDeviceContext`, `AVCUDADeviceContext`, `AVVkFrame` and `AVVulkanDeviceContext` are **absent** [M] | `bench/autogenprobe/`, `autogen.out` |
| The C layout to hand-declare | `AVDRMObjectDescriptor` 24 B (`fd@0 size@8 format_modifier@16`); `AVDRMPlaneDescriptor` 24 B (`object_index@0 offset@8 pitch@16`); `AVDRMLayerDescriptor` 104 B (`format@0 nb_planes@4 planes@8`); `AVDRMFrameDescriptor` **528 B** (`nb_objects@0 objects@8 nb_layers@104 layers@112`) [M, clang against the FFmpeg 9 header] | `bench/proto/drmlayout.c`, `drmlayout.out` |
| FFmpeg's VAAPI→DRM map | `vaapi_map_to_drm_esh` exports with `VA_EXPORT_SURFACE_SEPARATE_LAYERS`, adds `READ_ONLY` and calls **`vaSyncSurface` first** for `AV_HWFRAME_MAP_READ`, and closes the exported fds when the mapped frame is freed [V, FFmpeg master `libavutil/hwcontext_vaapi.c`, fetched to `scratchpad/ext/ffsrc/`] | |
| FFmpeg's D3D11 device and pool | `d3d11va_device_init` fills `device_context`, `video_device`, `video_context` from `device` and installs its own mutex only when `lock` is null. The frames context ORs the device's `BindFlags`/`MiscFlags`, strips `SHADER_RESOURCE` for `DXGI_FORMAT_420_OPAQUE`, and caps the array at **64**. DXVA pools are 1 + 16 surfaces for H.264/HEVC, 1 + 8 for VP9/AV1, 1 + 2 otherwise, **plus `extra_hw_frames`**, aligned to 16 (128 for HEVC/AV1) [V, `hwcontext_d3d11va.c`, `dxva2.c`] | |
| ANGLE tokens | `EGL_D3D_TEXTURE_ANGLE 0x33A3`, `EGL_D3D11_TEXTURE_PLANE_ANGLE 0x3492`, `EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE 0x3493`, `EGL_TEXTURE_INTERNAL_FORMAT_ANGLE 0x345D`. The `EGL_ANGLE_d3d_texture_client_buffer` spec is now v12 (2026-10-09); NV12/P010/P016 need the plane attribute (0 or 1); the texture must come from the device the display was made on [V, `google/angle` `include/EGL/eglext_angle.h` and `extensions/EGL_ANGLE_d3d_texture_client_buffer.txt`] | `scratchpad/ext/angle/` |

---

## 1. Goal and the bar

**Goal.** A decoded frame never leaves the GPU on its way to the air. The renderer samples the decoder's own surface,
or one copy of it made on the GPU. On every machine, every source says which path it got and why, with measured cost.
If the zero-copy path fails at run time, the source steps down a defined ladder without a black frame.

**What the others do, and how PlayMap matches or beats each point:**

| Who | What they do (source) | PlayMap |
|---|---|---|
| **mpv** (the reference implementation most players copy) | D3D11VA default is one GPU-to-GPU copy into a shader resource. Sampling the decoder surface is an opt-in flag (`--d3d11va-zero-copy`) with a warning about edge padding and driver bugs. `d3d11va` needs a D3D11 or ANGLE context, not WGL. VAAPI → DRM PRIME → EGL images per plane. CUDA → GL via `cuMemcpy2DAsync` into a registered texture. Copy-back modes "may not help too much over software decoding" (codecs.md §8.1–8.5) | **Same defaults, and the opt-in becomes a measured proof.** PlayMap runs an A/B check per driver: decode a reference clip both ways and compare the pictures, including the padded edge. The node switches itself to direct sampling only when they match, and keeps the proof keyed to the driver version. A driver update runs it again. mpv leaves this to a user flag |
| **Watchout 7** | Converts H.264 to HEVC and ProRes to Notch LC on import, so playback is hardware decode or GPU codecs. Runners keep playing with the Asset Manager offline (codecs.md §7.1) | Matched by the hardware path here plus the HAP and optimise-on-import packages. Beaten on **evidence**: Watchout publishes no per-asset decode path. PlayMap shows `path`, `zeroCopy`, CPU ms per frame, bus MB per frame and VRAM per source, in STATE, MCP and the desk |
| **Pixera, disguise** | Steer users to GPU-friendly codecs (HAP, NotchLC) and do not document zero-copy for H.264/HEVC (codecs.md §7.2–7.3) | PlayMap plays H.264, HEVC 8/10-bit and AV1 zero-copy where the hardware decodes them, so a rental node runs a client's delivery file without a transcode. The HAP package covers the GPU codecs |
| **Linux show servers** | Most commercial servers are Windows-only; nothing found documents VAAPI zero-copy in a media server (codecs.md §8.8 summarises the platform routes) | **Zero-copy on Linux** through PlayMap's own windowing: EGL contexts from GLFW (proved here) and VAAPI dmabufs. This makes cheap Intel/AMD mini-PCs real 4K render nodes |
| **Every one of them** | Hardware-decode failure is a log line, or a black layer | **A ladder with defined outcomes:** direct → one GPU copy → copy-back → software. Each step down keeps the last picture on the wall, says why in STATE and the context, and never steps back up mid-show (show safety) |

## 2. Scope this round

### Must

| # | Item | Acceptance criteria (checkable) |
|---|---|---|
| M1 | **Core spine**: `GpuSurface`, `VideoFrameLease.Surface`, `IGpuShare`, `DecodePath`, `SourceHealth` additions | CPU leases are unchanged; every existing test passes; a lease with a surface and no planes reaches `VideoTextureSet` only through an importer; a source opened **without** a share never produces a surface lease (test: open with `Gpu = null` → `Surface == null` on every lease) |
| M2 | **Linux EGL contexts**: `RenderContext` asks GLFW for EGL on Linux (`NodeSettings.GlApi = "auto"`), falls back to GLX when the EGL context fails, and says so | GL test: `RenderContext.CreateHidden()` on Xvfb gives `eglGetCurrentContext() != 0`; an output window that shares with it is EGL too; with `GlApi = "glx"` both are GLX; an EGL failure (forced by a test seam) falls back to GLX and sets `STATE.decode.platform.words` |
| M3 | **Linux VAAPI zero-copy**: VAAPI device on the GL's own render node; frames mapped to DRM PRIME on the decode thread; per-plane EGLImages in the render thread; sampled in place by the existing `yuv` pass; GL fence before release | (a) Import test on llvmpipe with memfd-backed NV12, P010, odd sizes (1918×1078), non-zero offsets and 256-byte pitches: the converted RGB16F picture equals the CPU-upload path's **bit for bit**. (b) Lease-life test with a fake hardware source (pool of 20 surfaces): no surface returns to the decoder before its fence signals; at most 2 frames held by the renderer; `FramePathAllocatedBytes` flat after warm-up. (c) A modifier the GL cannot import (`external_only` or absent) demotes that source to copy-back on its **first** frame, with words |
| M4 | **Windows D3D11VA on the shared device** (needs the D3D11 output package, R2.1 option C): FFmpeg decodes on PlayMap's `ID3D11Device` with `BindFlags = DECODER \| SHADER_RESOURCE`, PlayMap's recursive lock, multithread protection; default one `CopySubresourceRegion` per frame into a pooled NV12/P010 texture; direct sampling of the array slice once proven | Compile-only here (§7). Lab: 4 × 4K60 HEVC 10-bit, zero `av_hwframe_transfer_data` calls (counter in STATE), decode CPU < 1 ms/frame/stream p95, no tearing at the bottom/right edge with 1080p H.264 (coded 1088) |
| M5 | **Path ladder and demotion** (§3.4): chosen at open from policy + platform + proof; demoted at run time on import failure, device loss or surface starvation; never promoted mid-show | Fake-source tests for each trigger: the next frame comes by the lower path, the renderer holds the last picture meanwhile (no black frame, asserted on read-back), `STATE.decode.sources[i].why` names the trigger, and the journal has one row |
| M6 | **Padded surfaces**: the `yuv` program takes `uCrop` (visible ÷ coded) and `uMaxUv` (last full chroma texel centre) | GL test: a 1920×1088 NV12 surface whose last 8 rows are magenta converts to a 1920×1080 picture with no magenta in the last row or column |
| M7 | **Direct-sampling proof**: A/B check per driver, persisted in `NodeHome/decode-proof.json`; `DECODE PROBE` runs it on request | Logic tests with a fake importer pair (equal → proven; one LSB in the last row differs → refused with the row named); the file is rewritten when `adapter + driver version + OS build` changes; refused on an armed node |
| M8 | **STATE, verbs, context, MCP**: `STATE.decode`, `DECODE STATUS / POLICY / PROBE`, a context item, NodeSettings | Engine tests assert the JSON fields (not words); `DECODE POLICY DIRECT` without a proof is refused with the reason; `VERBS` lists the three verbs with their risks; the desk's fake engine writes the same shape |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **NVIDIA one-copy path (CUDA → GL)** for Linux nodes and for Windows nodes still on the WGL renderer: NVDEC via FFmpeg's `cuda` hwaccel, `cuGraphicsGLRegisterImage` once per pool texture, `cuMemcpy2DAsync` device→array per plane, event before release | Compile-only + a fake `libcuda.so.1` (C, ~150 lines, built with clang in the test) that checks the call order and pitches; lab: 4K60 HEVC with no host round trip |
| S2 | **EGLImage cache per VA surface** (key: `VASurfaceID` + frames-context generation) | Only if the lab shows `eglCreateImageKHR` above 0.1 ms per plane on iris/radeonsi; the test asserts the cache empties on a frames-context change |
| S3 | **Per-item override** `MediaItem.Decode` (`auto`/`software`/`copy-back`) through generic `SET` | `SET md… decode "software"` reopens that source on its next open, never mid-show |
| S4 | **Desk**: decode chip in the media inspector, node card line, right-click "Probe decode on every node" | App tests with the fake engine |

### Later (not this round)

- **macOS**: VideoToolbox `CVPixelBufferRef` → `CVPixelBufferGetIOSurface` → `CGLTexImageIOSurface2D(ctx, GL_TEXTURE_RECTANGLE, GL_R8|GL_RG8, w, h, …, surface, plane)` on today's GL, and the Metal texture cache (codecs.md §8.6) on the macOS engine (ROADMAP L6). It waits for the main-thread GL hook (CARRYOVER: "Render host starts on a pool thread; macOS main-thread GL would need another hook").
- **Vulkan hwaccel** for ProRes on the GPU (codecs.md §8.7), exported as dmabuf (Linux) or a D3D11 shared handle.
- **WGL zero-copy on Windows** (`VideoProcessorBlt` to RGBA + `WGL_NV_DX_interop2`): only if R2.1 slips past this round (codecs.md §8.3 recommends against it).
- **Live capture sources** (MJPEG/H.264 webcams via `CaptureSource`) on the zero-copy path.
- **Surfaceless EGL for headless nodes** (no Xvfb), since the EGL path now exists.

## 3. Architecture

### 3.1 Components

```
 PlayMap.Core   GpuSurface · IGpuShare · DecodePath · VideoFrameLease.Surface · SourceHealth (+path facts)
                                     ▲ implements                      ▲ produces
 PlayMap.Render  RenderGpuShare ── IGpuImporter ─┬ DmaBufImporter (EGL, Linux)          PlayMap.Media
   RenderContext (GL API choice)                 ├ AngleD3D11Importer (Windows, R2.1)   HwDecode (device from the share,
   VideoTextureSet (import → yuv pass → fence)   ├ CudaGlImporter (should)                pool sizing, path ladder)
   ReleaseRing (fences)                          └ (CPU upload: today's path)          DrmFrames (hand-declared)
                                                                                       GpuFrameSlot (AVFrame refs)
 PlayMap.Engine  EngineSources (opens with the share; health per slot) · DecodeState (STATE.decode)
                 EngineCore.Decode (DECODE verbs) · DecodeProof (A/B, decode-proof.json)
```

Module rules hold: Media produces surfaces described only by Core types (fds, pointers, offsets) and never calls
GL or EGL; Render imports them and never calls FFmpeg (ARCHITECTURE.md §2).

### 3.2 Threads and data flow

**Linux (VAAPI, Intel/AMD; NVIDIA with `nvidia-vaapi-driver`):**

1. *Render thread, at start.* `RenderContext` makes EGL contexts. `RenderGpuShare` queries
   `eglQueryDisplayAttribEXT(dpy, EGL_DEVICE_EXT 0x322C)` → `eglQueryDeviceStringEXT(dev, EGL_DRM_RENDER_NODE_FILE_EXT 0x3377)`
   (tokens [V] Khronos `eglext.h`; `EGL_EXT_device_drm_render_node` is listed by Mesa here [M])
   for the render node (`/dev/dri/renderD128`), and `eglQueryDmaBufFormatsEXT` / `eglQueryDmaBufModifiersEXT` for
   the importable `(fourcc, modifier, external_only)` set. A software GL (`EGL_MESA_device_software`, llvmpipe)
   gives no render node: `platform.zeroCopy = "unavailable"`, "the GL is software; zero-copy needs a GPU".
2. *Pool thread, open.* `HwDecode` creates the VAAPI device on that node:
   `av_hwdevice_ctx_create(&dev, AV_HWDEVICE_TYPE_VAAPI, "/dev/dri/renderD128", null, 0)`. It sets
   `ctx->extra_hw_frames = QueueFramesHw + 3` before `avcodec_open2`.
3. *Decode thread, per frame.* `av_hwframe_map(drm, vaapi, AV_HWFRAME_MAP_READ)` (which runs `vaSyncSurface` and
   `vaExportSurfaceHandle(SEPARATE_LAYERS | READ_ONLY)`). The `AVDRMFrameDescriptor` is read through the hand-declared
   struct into `DmaBufPlane[]`. On the first frame only, `IGpuShare.CanImport(fourcc, modifier)` is checked for each
   layer; a miss demotes the source before anything reaches the renderer. The mapped `AVFrame` (which holds the
   VAAPI frame) goes into a `GpuFrameSlot` and is queued like any frame.
4. *Render thread, per frame.* `EngineSources.Acquire` hands the lease (generation remapped as today).
   `VideoTextureSet.Update` sees `lease.Surface`, asks the `DmaBufImporter` for plane textures:
   `eglCreateImageKHR(dpy, EGL_NO_CONTEXT, EGL_LINUX_DMA_BUF_EXT 0x3270, null, attrs)` with `EGL_WIDTH`,
   `EGL_HEIGHT`, `EGL_LINUX_DRM_FOURCC_EXT 0x3271`, `EGL_DMA_BUF_PLANE0_FD_EXT 0x3272`, `_OFFSET_EXT 0x3273`,
   `_PITCH_EXT 0x3274` and, when the modifier is not `DRM_FORMAT_MOD_INVALID (0x00FFFFFFFFFFFFFF)`,
   `EGL_DMA_BUF_PLANE0_MODIFIER_LO/HI_EXT 0x3443/0x3444`. Then `glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, image)` on
   two GL texture names the set owns (R8 + RG8, or R16 + RG16). The existing `yuv` program converts into the set's
   RGB16F target; the rest of the frame samples that target as today.
5. *Release.* After the `yuv` draw the set inserts `glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE)` and moves the lease
   to `ReleaseRing`. Each frame the ring polls `glClientWaitSync(fence, 0, 0)` and disposes signalled leases: the
   mapped frame is freed, the fds close, the VA surface returns to FFmpeg's pool. The EGLImages are destroyed at the
   same moment (per-frame images; S2 caches them).

**Windows (D3D11VA on PlayMap's device; needs R2.1 option C, present.md §10):**

1. R2.1 makes one `ID3D11Device` per adapter with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT | BGRA_SUPPORT`, calls
   `ID3D10Multithread::SetMultithreadProtected(TRUE)` and hands it to ANGLE (`eglCreateDeviceANGLE`). Its
   `RenderGpuShare` exposes the device pointer, the adapter LUID and a recursive `Lock`.
2. `HwDecode`: `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)`; `AVD3D11VADeviceContext.device` = the device
   (AddRef through its vtable first, since FFmpeg releases it); `BindFlags = D3D11_BIND_DECODER (0x200) |
   D3D11_BIND_SHADER_RESOURCE (0x8)`; `lock`/`unlock`/`lock_ctx` = `[UnmanagedCallersOnly]` callbacks that enter
   and exit the share's recursive lock (a `GCHandle` in `lock_ctx`); `av_hwdevice_ctx_init`. If the decoder then
   fails to make its frames (a driver that refuses `SHADER_RESOURCE` on decoder arrays), it reopens with
   `BindFlags = D3D11_BIND_DECODER` only, and the source can use only the GPU-copy path.
3. Decode thread: frames arrive as `AV_PIX_FMT_D3D11`, with `data[0] = ID3D11Texture2D*` (the array) and
   `data[1] = slice`. The slot keeps an `av_frame_ref`. The surface records the coded size from the frames context
   (`AVHWFramesContext.width/height`) and the visible size from the frame.
4. Render thread, **GPU-copy (default until proven)**: under the share's lock,
   `CopySubresourceRegion(own, 0, 0, 0, 0, array, D3D11CalcSubresource(0, slice, 1), &box{0,0,0,W',H',1})` into one of
   three pooled NV12/P010 textures (`D3D11_USAGE_DEFAULT`, `BIND_SHADER_RESOURCE`; W', H' rounded up to even). Each
   pool texture has its two plane EGLImages made **once** (`EGL_D3D_TEXTURE_ANGLE` + `EGL_D3D11_TEXTURE_PLANE_ANGLE`
   0/1). Then the `yuv` pass.
5. Render thread, **direct (once proven)**: plane EGLImages of the array slice itself
   (`EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE = slice`), cached per `(array pointer, slice, plane)` for the frames
   context's life, so a 32-slice pool makes at most 64 images once.
6. Release, **the one-device rule**: decoder, copy and draw are recorded on one immediate context, which runs them in
   order. So the lease is disposed as soon as the copy (or the `yuv` draw) has been *recorded*, not when it has
   *completed*. FFmpeg can only reuse that slice after the unref, so its next decode into it is recorded after the
   read. No fence, no extra held surfaces. The invariant in the code comment: "one device, one immediate context:
   release after record".

**Windows on today's WGL renderer, and macOS:** copy-back as today (`TransferIfHardware`), reported honestly as
`hw-copy-back` with `why = "GL on Windows: zero-copy needs the D3D11 output path (R2.1)"`. NVIDIA nodes get S1.

### 3.3 Surfaces, pools and memory

- **Queue depth on the GPU path.** `QueueFramesHw` (default 8, NodeSettings) replaces `QueueSettings.QueueFrames`
  (12) for hardware sources. At 60 fps 8 frames is 133 ms ahead, plenty for a hardware decoder that decodes 4K in a few
  ms [E].
- **Surfaces per source** = DPB (17 for H.264/HEVC, 9 for VP9/AV1, 3 otherwise [V dxva2.c]) + `QueueFramesHw` +
  3 (renderer-held 2 + one being mapped). That is 28 for HEVC, within D3D11's limit of 64 [V].
- **VRAM per source** [E]: a 4K HEVC surface is 3840 × 2176 (128-aligned) × 1.5 B = 12.5 MB in NV12, 25 MB in P010,
  so 28 surfaces are 350 MB (8-bit) or 700 MB (10-bit). 1080p H.264 (1920 × 1088) is 3.1 MB × 28 = 88 MB. GPU-copy
  adds 3 pool textures. Every source reports `vramMb`, and the preflight package sums it per node.
- **Bus traffic** [E, codecs.md §8.1]: copy-back moves 12.4 MB per 4K NV12 frame down and again up, about 746 MB/s
  each way at 60 fps. The zero-copy paths move 0 bytes over the bus. GPU-copy moves 12.4 MB inside VRAM per frame.
- **`GpuFrameSlot`**: a pooled object (no allocation per frame) holding `nint HwFrame` (an `AVFrame*` reference), the
  `GpuSurface` (its `DmaBufPlane[4]` array reused) and the timing fields `FrameSlot` has. `FrameQueue` stores the
  common base `QueuedFrame` (today's `FrameSlot` fields minus the buffer), so trimming, seeking, looping and the
  late/drop counters work unchanged.

### 3.4 The path ladder

| Rank | `path` | `zeroCopy` | `gpuCopies` | Where it exists |
|---|---|---|---|---|
| 1 | `hw-direct` | true | 0 | Linux VAAPI → EGL (always direct: mpv samples VAAPI images in place); Windows D3D11 after the proof |
| 2 | `hw-gpu-copy` | true | 1 | Windows D3D11 default; NVIDIA CUDA → GL (S1) |
| 3 | `hw-copy-back` | false | 0 | today's path on every platform |
| 4 | `software` | false | 0 | FFmpeg CPU decode |
| — | `hap-gpu` | true | 0 | set by the HAP package (CPU parses, the GPU decodes BCn) |
| — | `gpu-share` | true | 1 | set by texture-sharing inputs (Spout, Syphon: `texshare` package) |

**Choice at open:** the highest rank allowed by `NodeSettings.DecodePolicy` (`auto` | `direct` | `gpu-copy` |
`copy-back` | `software`), the item's `Decode` override (S3), whether the share supports it, and the proof
(`direct` needs one on Windows).

**Demotion at run time**, one rank or straight to `hw-copy-back`. The source keeps its queue and the renderer
holds the last picture:

| Trigger | Detected by | Goes to |
|---|---|---|
| EGL import fails (`EGL_BAD_MATCH`, `EGL_BAD_ATTRIBUTE`, `EGL_BAD_ALLOC`) | `DmaBufImporter` → `IGpuShare.ReportImportFailure(key, words)` → a volatile flag the decode thread reads before its next map | `hw-copy-back` |
| The first frame's `(fourcc, modifier)` is not importable | decode thread, before queueing | `hw-copy-back` |
| `GetDeviceRemovedReason() != S_OK`, or the share's `DeviceEpoch` moves | render host (R2.1) | the source is reopened on the new device; until then it holds |
| Starvation: the decoder waits more than 3 frame periods for a free surface, 3 times in 10 s | `HwDecode` | `hw-copy-back` (copies free surfaces immediately) |
| Direct sampling unproven but forced, and the proof later fails | `DecodeProof` | `hw-gpu-copy` for sources opened afterwards |

**No promotion mid-show.** A demoted source stays down until it is next opened: a new cue, a loop back to an item, or
`DECODE PROBE`. On an armed node (Show Mode package) nothing reopens a decoder on air.

### 3.5 The padded-surface fix (M6)

Hardware surfaces are aligned: H.264 1080p is coded 1088 high, HEVC and AV1 are aligned to 128 [V dxva2.c]. On D3D11
the plane images cover the coded size. The `yuv` program gets two uniforms:

```glsl
uniform vec2 uCrop;   // visible / coded size, (1, 1) for CPU uploads and dmabuf imports (imported at visible size)
uniform vec2 uMaxUv;  // for chroma: (visibleChroma - 0.5) / codedChroma, per axis; luma uses the same in luma texels
vec2 uvY = vTex * uCrop;              // luma
vec2 uvC = min(vTex * uCrop, uMaxUv); // chroma never interpolates into the padding
```

This removes mpv's warning about the bottom and right edges (codecs.md §8.2, item 4). Dmabuf planes are imported
at their visible size (EGL takes width and height per plane), so `uCrop = (1, 1)` there.

### 3.6 The direct-sampling proof (M7)

- **When:** at node start if `policy = auto`, the platform offers direct sampling and there is no proof for this
  driver, and only while the node is not armed (R2.11's contention rule). Also on `DECODE PROBE … DIRECT`.
- **What:** PlayMap ships two short reference clips with deliberately odd sizes, made from synthetic patterns:
  `assets/decode-probe/probe-h264-1918x1078.mp4` and `probe-hevc10-3838x2158.mp4`, about 200 kB each. The right and
  bottom edges carry saturated stripes. The proof decodes 30 frames of each, twice: once direct and once by GPU-copy.
  Both run through the same `yuv` pass into RGB16F, and are read back with `PixelPackRing`.
- **Pass:** every visible texel equal within 1/1023 per channel, **and** the last two rows and columns equal exactly.
  The proof is written to `NodeHome/decode-proof.json`:
  `{"adapter":"10de:2531","luid":"0x…","driver":"32.0.15.6614","os":"10.0.26100","codecs":{"h264":true,"hevc10":true},"at":1760360000000}`.
  If the adapter, driver version or OS build changes, the proof is void.
- **Fail:** `platform.direct = "refused"`, with the first differing row and column and the codec named.

### 3.7 Formats

| Decoder output | Linux layer fourccs (VAAPI `SEPARATE_LAYERS`) | GL plane formats | D3D11 | ANGLE plane images | `YuvConstants` layout |
|---|---|---|---|---|---|
| NV12 | `R8  ` 0x20203852 + `GR88` 0x38385247 | `GL_R8` + `GL_RG8` [M] | `DXGI_FORMAT_NV12` (103) | plane 0 → R8, plane 1 → RG8 [V spec] | `PixelLayout.Nv12` |
| P010 | `R16 ` 0x20363152 + `GR32` 0x32335247 | `GL_R16` + `GL_RG16` [M] | `DXGI_FORMAT_P010` (104) | R16, RG16 [V spec] | `PixelLayout.P010` (10 bits high, code scale 65535/64) |
| 4:2:2 / 4:4:4 hardware formats (`Y210`, `VUYX`, `XV30`) | not this round: demote to copy-back with words | — | — | `Y210`/`Y410` are sample-only images in ANGLE v12 [V] | — |

### 3.8 Performance budgets

| Step | Budget | Basis |
|---|---|---|
| Decode-thread CPU per frame, hardware path | ≤ 1 ms p95 at 4K60 (map + sync + queue) | [E]; field data has copy-back at 4–40 ms (codecs.md §8.1) |
| Render-thread import per frame | ≤ 0.1 ms (two images, two binds) | 0.015 ms measured on llvmpipe [M] |
| GPU-copy per 4K frame | ≤ 0.1 ms GPU (12.4 MB on the GPU) | [E] |
| `yuv` pass | unchanged (it runs for CPU uploads today) | — |
| Allocation | 0 bytes per frame after warm-up on decode and render threads | R2.8; asserted by the existing `FramePathAllocatedBytes` and `_threadAllocated` counters |
| Held surfaces | ≤ 2 by the renderer (Linux fences), 0 after record (Windows one-device rule) | §3.2 |

## 4. Spine needs

### 4.1 Core contracts (new file `src/PlayMap.Core/Media/GpuSurfaces.cs`)

```csharp
namespace PlayMap.Core.Media;

/// <summary>What a GPU-resident picture is, in terms both a decoder and a renderer understand.</summary>
public enum GpuApi { None, D3D11, DmaBuf, IoSurface, Cuda }

public enum GpuSurfaceFormat { Nv12, P010, Bgra8, Rgba8, Rgb10A2, Rgba16, Rgba16F, Rgba32F }

/// <summary>How a frame reaches the renderer. The hardware ladder is HwDirect → HwGpuCopy → HwCopyBack → Software
/// (§3.4); HapGpu (the HAP package) and GpuShare (texture-sharing inputs) are set by their own producers.</summary>
public enum DecodePath { HwDirect, HwGpuCopy, HapGpu, GpuShare, HwCopyBack, Software }

/// <summary>One plane of a dmabuf frame: the fd stays owned by the frame (closed when the lease ends).</summary>
public readonly record struct DmaBufPlane(int Fd, uint Fourcc, int Width, int Height, long Offset, int Pitch, ulong Modifier);

/// <summary>A picture on the GPU. Pooled by its producer and valid until the lease that carries it is disposed; a
/// persistent surface (a texture-share input's own copy) is valid until its owner's <see cref="Retire"/>.</summary>
public sealed class GpuSurface
{
    public GpuApi Api { get; set; }
    public GpuSurfaceFormat Format { get; set; }
    public int Width { get; set; }          // visible
    public int Height { get; set; }
    public int CodedWidth { get; set; }     // allocation (≥ visible)
    public int CodedHeight { get; set; }
    public nint Handle { get; set; }        // ID3D11Texture2D* | IOSurfaceRef | CUdeviceptr (plane 0)
    public int Slice { get; set; }          // array slice for D3D11 decoder arrays
    public long SurfaceId { get; set; }     // stable per allocation: an importer's cache key (VASurfaceID, texture ptr ^ slice)
    public long PoolEpoch { get; set; }     // moves when the producer's pool is rebuilt (cache invalidation)
    public DmaBufPlane[] Planes { get; } = new DmaBufPlane[4];
    public int PlaneCount { get; set; }
    public bool Persistent { get; set; }    // the same handle every frame (cache the import for the surface's life)
}

/// <summary>What the renderer can import, and the device decoders should use. Implemented by the render host;
/// handed to sources opened for the renderer (never to CPU consumers: scan cameras, thumbnails, the probe).</summary>
public interface IGpuShare
{
    GpuApi Api { get; }
    string Describe { get; }                       // "VAAPI → EGL dmabuf on /dev/dri/renderD128 (iris)"
    nint D3D11Device { get; }                      // ID3D11Device*, not AddRef'd; 0 unless Api == D3D11
    long AdapterLuid { get; }
    string? DrmRenderNode { get; }                 // "/dev/dri/renderD128" when Api == DmaBuf
    long DeviceEpoch { get; }                      // moves when the device is rebuilt (device removed)
    bool DirectProven { get; }                     // sampling decoder surfaces in place is proven on this driver
    bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier);
    void Enter();                                  // the device's recursive lock (FFmpeg's lock callbacks take it)
    void Exit();
    /// <summary>Render → source: an import failed; the source steps down before its next frame. Thread-safe.</summary>
    void ReportImportFailure(string sourceKey, string words);
    bool ImportFailed(string sourceKey, out string words);
}
```

### 4.2 Changes to existing Core types (small hunks, backward compatible)

```csharp
// src/PlayMap.Core/Media/VideoFrames.cs
public abstract class VideoFrameLease : IDisposable
{
    /// <summary>The picture on the GPU, or null for a CPU picture (Planes). A lease with a surface may have no planes.</summary>
    public virtual GpuSurface? Surface => null;
    …
}

public sealed record SourceHealth(bool Ready, string Status, long FramesDecoded, long FramesDropped, long FramesLate,
    double DecodeMsP95, string Decoder, bool Hardware)
{
    public DecodePath Path { get; init; } = DecodePath.Software;   // set from Hardware by the old constructor callers
    public bool ZeroCopy => Path is DecodePath.HwDirect or DecodePath.HwGpuCopy or DecodePath.HapGpu or DecodePath.GpuShare;
    public int GpuCopies { get; init; }
    public string Device { get; init; } = "";        // "vaapi", "d3d11va", "cuda", ""
    public string Why { get; init; } = "";           // why not a better path, in words
    public long BusBytesPerFrame { get; init; }
    public long VramBytes { get; init; }
    public int Surfaces { get; init; }
    public double ImportMsP95 { get; init; }         // filled by the renderer side
}
```

`RemappedLease` (`src/PlayMap.Engine/Rendering/SourceLeases.cs`) forwards `Surface`. `PinnedLease` keeps the default
(null).

### 4.3 Media options

```csharp
// src/PlayMap.Media/Ffmpeg/MediaInput.cs, DecoderOptions (init properties, defaults keep today's behaviour)
public IGpuShare? Gpu { get; init; }                        // null: copy-back as today
public DecodePath Ceiling { get; init; } = DecodePath.HwDirect;   // the policy's best allowed path
public int QueueFramesHw { get; init; } = 8;
```

`IMediaBackend` gains one default member (`src/PlayMap.Engine/Seams.cs`):
`void UseGpu(IGpuShare? share) { }`. `MediaBackend` keeps the share and passes it when `EngineSources` opens a source
for the renderer. `RenderHost` calls `request.Media.UseGpu(share)` once its context exists, and calls it with `null`
when the context goes.

### 4.4 Verbs (`VerbCatalog`, category `Media`)

| Verb | Grammar | Risk | Mutates | Reply |
|---|---|---|---|---|
| `DECODE STATUS` | `DECODE STATUS` | Low | no | `OK {…}`: the `STATE.decode` object |
| `DECODE POLICY` | `DECODE POLICY AUTO\|DIRECT\|GPU-COPY\|COPY-BACK\|SOFTWARE [FORCE]` | Review (`FORCE` is High) | yes (NodeSettings) | `OK {"stage":"applied","policy":"auto","effect":"decoders opened from now on; 3 on air keep their path"}`. Refused: `DIRECT` with no proof and no `FORCE` ("direct sampling is not proven on this driver: run DECODE PROBE DIRECT"); a policy equal to the current one ("already auto") |
| `DECODE PROBE` | `DECODE PROBE [<media>\|ALL] [DIRECT]` | Review | yes (runs GPU work, may write the proof) | `OK {"stage":"accepted","probe":"dp…"}`; the result arrives in `STATE.decode.probes` and as a pushed line `PROBE dp… {…}`. Refused on an armed node ("decode probes run in rehearsal, not on an armed node") |

Every reply carries fields; tests assert the fields. MCP gets the three as tools from the catalogue. The context's
command list includes them with their risks.

### 4.5 STATE (`"decode"`, after `"devices"`; new keys at the end of the object, WIRE.md rule)

```json
"decode":{"policy":"auto",
 "platform":{"api":"dmabuf","describe":"VAAPI → EGL dmabuf on /dev/dri/renderD128 (iris)","gl":"egl",
   "zeroCopy":"ready","direct":"proven","proofAt":1760360000000,"words":""},
 "sources":[{"key":"md1a2b3c4d","media":"md1a2b3c4d","name":"Intro","codec":"hevc","device":"vaapi","path":"hw-direct",
   "zeroCopy":true,"gpuCopies":0,"format":"p010","width":3840,"height":2160,"surfaces":28,"vramMb":700,
   "decodeMsP95":0.6,"importMsP95":0.02,"busMbPerFrame":0,"dropped":0,"late":0,"why":""}],
 "probes":[{"id":"dp…","media":"md…","state":"done","path":"hw-direct","fps":412.0,"cpuMsPerFrame":0.4,"words":""}]}
```

- `platform.zeroCopy`: `ready` | `unavailable` | `probing`; `platform.direct`: `proven` | `unproven` | `refused` |
  `n/a`; `platform.gl`: `egl` | `glx` | `wgl` | `angle`.
- `sources` lists open decoders for the renderer (EngineSources' item, media and live slots), at most 64 rows.
- Facts that change (path, why, platform) call `EngineCore.FactsChanged()`. Rates ride along when STATE is written
  anyway (it is written while anything animates).

**Context (Show Bus §6):** one item `decode` with light green (every on-air source is zero-copy, HAP, or ≤ 1080p
software), amber (a ≥ 4K source on air on `hw-copy-back` or `software`), or red (a source failed to decode). Its
words: "2 clips decode on the CPU: Intro (4K HEVC), Loop B (ProRes)".

### 4.6 NodeSettings (init properties with defaults on the positional record)

```csharp
public string GlApi { get; init; } = "auto";          // auto | egl | glx (Linux); ignored elsewhere
public string DecodePolicy { get; init; } = "auto";   // auto | direct | gpu-copy | copy-back | software
public string DecodeDevice { get; init; } = "";       // "" = platform order; vaapi | cuda | d3d11va | videotoolbox
public int DecodeQueueFramesHw { get; init; } = 8;
```

The proof lives in its own file, `NodeHome/decode-proof.json`, like `hash-cache.json`, so it never rides in a
settings line.

### 4.7 Render seams (inside PlayMap.Render)

```csharp
/// <summary>Turns a lease's GPU surface into plane textures for the yuv pass (or a picture texture for RGB
/// surfaces). Render thread only.</summary>
internal interface IGpuImporter : IDisposable
{
    GpuApi Api { get; }
    /// <summary>Plane textures for the surface; false (with words) when it cannot import it.</summary>
    bool TryImport(GpuSurface surface, Span<GpuTexture?> planes, out string words);
    /// <summary>After the frame's last read of the surface was recorded: keep the lease until it is safe to release.</summary>
    void Retain(VideoFrameLease lease);
    /// <summary>Releases leases whose reads have completed (called once per frame).</summary>
    void Collect();
}
```

`GlDevice` gains `internal GpuTexture AdoptExternal(uint glName, int w, int h, TextureFormat f)` (a texture whose
storage an EGLImage provides; `Dispose` deletes the name only). `RenderContext` gains `GlApi` and `Share`
(`IGpuShare?`).

## 5. Files

**New**

| File | What |
|---|---|
| `src/PlayMap.Core/Media/GpuSurfaces.cs` | §4.1 |
| `src/PlayMap.Media/Ffmpeg/HwDecode.cs` | device from the share (VAAPI on the render node; D3D11 adoption with lock callbacks and BindFlags; CUDA in S1); `extra_hw_frames`; the open-time ladder; starvation watch |
| `src/PlayMap.Media/Ffmpeg/DrmFrames.cs` | hand-declared `AVDRMFrameDescriptor` and friends (`[StructLayout(LayoutKind.Sequential)]`, sizes asserted against §0's golden: 24/24/104/528) and `ToPlanes(GpuSurface)` |
| `src/PlayMap.Media/Frames/GpuFrameSlot.cs` | pooled GPU slot and its pool |
| `src/PlayMap.Render/Platform/GlContextApi.cs` | the GLFW EGL hint, GLX fallback, which API the context really is (`eglGetCurrentContext`) |
| `src/PlayMap.Render/Platform/EglNative.cs` | `LibraryImport("libEGL.so.1")` for `eglGetCurrentDisplay`, `eglGetCurrentContext`, `eglQueryString`, `eglGetProcAddress`; function pointers for `eglCreateImageKHR`, `eglDestroyImageKHR`, `eglQueryDmaBufFormatsEXT`, `eglQueryDmaBufModifiersEXT`, `eglQueryDisplayAttribEXT`, `eglQueryDeviceStringEXT`, and `glEGLImageTargetTexture2DOES` (from the GL context's `GetProcAddress`; present in Mesa's core profile [M]) |
| `src/PlayMap.Render/Platform/RenderGpuShare.cs` | `IGpuShare` for EGL now and ANGLE after R2.1 |
| `src/PlayMap.Render/Video/DmaBufImporter.cs` | §3.2 Linux steps 4–5 and the fence ring |
| `src/PlayMap.Render/Video/AngleD3D11Importer.cs` | §3.2 Windows steps 4–6 (`[SupportedOSPlatform("windows")]`; compiled everywhere, used only with an ANGLE context) |
| `src/PlayMap.Render/Video/CudaGlImporter.cs` | S1 |
| `src/PlayMap.Engine/Rendering/DecodeState.cs` | `STATE.decode` writer, context item |
| `src/PlayMap.Engine/EngineCore.Decode.cs` | `DECODE STATUS / POLICY / PROBE` |
| `src/PlayMap.Engine/Rendering/DecodeProof.cs` | §3.6, `decode-proof.json` |
| `assets/decode-probe/*.mp4` | two reference clips (synthetic, project-owned) |
| tests: `tests/PlayMap.Render.Tests/DmaBufImportTests.cs`, `EglContextTests.cs`, `YuvCropTests.cs`; `tests/PlayMap.Media.Tests/DrmLayoutTests.cs`, `HwLadderTests.cs`, `GpuSlotTests.cs`; `tests/PlayMap.Engine.Tests/DecodeStateTests.cs`, `DecodeVerbTests.cs`, `DecodeProofTests.cs` | §7 |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Media/VideoFrames.cs` | `Surface` on the lease; `SourceHealth` init properties |
| `src/PlayMap.Core/Bus/Pairing.cs` | four NodeSettings init properties |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | three `DECODE` rows |
| `src/PlayMap.Media/Ffmpeg/MediaInput.cs` | `DecoderOptions` properties; `StreamDecoder.Open` delegates device setup to `HwDecode` when `Gpu` is set |
| `src/PlayMap.Media/Sources/FfmpegVideoSource.cs` | `PublishPending`: map to a GPU slot instead of `TransferIfHardware` when the source's path is zero-copy; check `ImportFailed` before each map |
| `src/PlayMap.Media/Frames/FramePool.cs`, `FrameQueue.cs`, `MediaFrameLease.cs` | the `QueuedFrame` base; `Surface` on the lease |
| `src/PlayMap.Media/MediaLibrary.cs` | `MediaSourceOptions.Video.Decoder.Gpu` passed through |
| `src/PlayMap.Render/Platform/RenderContext.cs` | GL API choice before `Window.Create`; `Share` |
| `src/PlayMap.Render/Video/VideoTextureSet.cs` | `Update`: a surface goes to the importer, then the `yuv` pass; `Retain` instead of the caller's dispose |
| `src/PlayMap.Render/Engine/SourceResolver.cs` | a surface lease is handed to the set and not disposed by `using` |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | `yuv`: `uCrop`, `uMaxUv` |
| `src/PlayMap.Render/Gpu/GlDevice.Resources.cs` | `AdoptExternal` |
| `src/PlayMap.Engine/Seams.cs` | `IMediaBackend.UseGpu` default method |
| `src/PlayMap.Engine/Host/MediaBackend.cs` | keep the share; pass it on renderer opens |
| `src/PlayMap.Engine/Rendering/EngineSources.cs`, `SourceLeases.cs`, `RenderHost.cs` | share in, health out, `Surface` forwarded, `UseGpu` call |
| `src/PlayMap.Engine/StateWriter.cs` | one call: `DecodeState.Write(w, e)` after `WriteOpsState` |
| `src/PlayMap.Engine/ContextBuilder.cs` | the `decode` item |
| `src/PlayMap.App/State/StateSnapshot.cs`, media inspector view and view model, `Link/FakeEngineLink.State.cs` | the decode chip and the fake's STATE |
| `docs/WIRE.md`, `docs/PLAYBACK.md`, `docs/ARCHITECTURE.md` (§2 Media row), `docs/ROADMAP.md` (R2.2 status) | papers |

## 6. Dependencies

**Work packages.**
- **R2.1, the D3D11 output path (option C, ANGLE on PlayMap's device).** M4 needs its device (created with
  `VIDEO_SUPPORT`, multithread-protected), its ANGLE display and its device-loss epoch. Linux (M2, M3) does not wait
  for it.
- **HAP (R2.3)** reuses `DecodePath.HapGpu`, the `SourceHealth` facts and `STATE.decode.sources`.
- **texshare** reuses `GpuSurface`, `IGpuShare`, `IGpuImporter` and the ANGLE importer for RGBA surfaces.
- **Preflight (R2.6)** reads `DECODE PROBE` results and `vramMb`. **Show Mode (R3.5)** supplies "armed", which
  refuses the probe and blocks reopening. **Companion** exposes the variables in §8.

**Third party** (all already in the tree, or loaded from the user's machine at run time):

| What | Version | Licence | How |
|---|---|---|---|
| FFmpeg.AutoGen | 9.0.1.1 (in tree) | LGPL-3.0 bindings, already accepted for the tree | D3D11 structs bound [M]; DRM structs hand-declared |
| FFmpeg 9 shared libraries | user's or shipped as replaceable LGPL libs (R0.7) | LGPL/GPL by build | loaded at run time as today |
| Silk.NET.Windowing / GLFW | 2.23.0 (in tree) | MIT | the EGL hint [M] |
| libEGL (Mesa or vendor) | system | MIT (Mesa) / vendor | `LibraryImport` at run time |
| libva + VA driver | system | MIT (libva, intel-media-driver), Mesa (radeonsi) | loaded by FFmpeg |
| nvidia-vaapi-driver (optional) | system | MIT | loaded by libva when the user installed it |
| CUDA driver API (`libcuda.so.1`, `nvcuda.dll`), S1 | user's NVIDIA driver | proprietary, loaded at run time only | `LibraryImport`, about 12 functions |
| ANGLE | through R2.1 | BSD-3 | R2.1 ships it |

No GPL or LGPL code is copied. mpv (GPL/LGPL) and FFmpeg's internals were read only to learn the APIs' behaviour.

## 7. Verification here

| Area | Real on this machine | Simulated, and how | Compile-only (needs Windows, a GPU or hardware) |
|---|---|---|---|
| EGL contexts through GLFW (M2) | **Yes**: Xvfb :99 + Mesa EGL, proved by the probe [M]; the test asserts the context API | GLX fallback forced through a test seam (`GlContextApi` told to fail EGL) | NVIDIA's EGL on X11 [U]; Wayland |
| Dmabuf import → `yuv` pass (M3) | **Yes**: memfd frames are importable by llvmpipe [M]. Tests compare the converted picture with the CPU path bit for bit, for NV12, P010, odd sizes, offsets, pitches and the LINEAR modifier | — | Real VAAPI surfaces (tiled modifiers, kernel imports) need an Intel/AMD GPU: lab item |
| FFmpeg VAAPI map | — | A `FakeHwFrames` producer writes NV12/P010 into memfds and fills an `AVDRMFrameDescriptor`-shaped struct exactly as `vaapi_map_to_drm_esh` does [V], so `DrmFrames.ToPlanes` and the slot and lease life run for real | `av_hwdevice_ctx_create(VAAPI)` and `av_hwframe_map` (no `/dev/dri` here) |
| Struct layouts | **Yes**: `DrmLayoutTests` asserts `Marshal.SizeOf` and `OffsetOf` against the clang-measured golden; `AVD3D11VADeviceContext` offsets asserted against the probe's | — | — |
| Lease and fence life, pool sizing, demotion, no-black-frame | **Yes** on llvmpipe with the fake producer (real GL fences) | starvation and import failure injected by the fake | — |
| Crop and edge (M6) | **Yes**: a 1920×1088 CPU upload with a magenta band stands in for a padded surface (same uniforms) | — | ANGLE plane images of real padded slices |
| Proof logic (M7) | **Yes**: a pair of fake importers (same texture vs one-LSB edge change) through the real compare and file write | — | The real A/B on a D3D11 driver |
| D3D11VA shared device, GPU-copy, direct, one-device release rule (M4) | — | — | **Compile-only**: needs Windows, a GPU with a video decoder and R2.1's ANGLE device. WARP has no video decoder, so even a Windows CI job without a GPU cannot run it [E]. Lab test: present.md §12 #5 |
| CUDA → GL (S1) | — | fake `libcuda.so.1` checks call order and pitches | real NVDEC |
| STATE, verbs, context, MCP, desk | **Yes** (engine tests with a fake media backend reporting each path; app tests with the fake engine) | — | — |

Honest summary: on this box the whole Linux import half and every piece of logic runs for real. The FFmpeg hardware
half (VAAPI, D3D11VA, CUDA) is only exercised through fakes that reproduce FFmpeg's documented outputs. The Windows
path is compile-only until R2.1 and the lab exist.

## 8. Desk and Companion surface

- **Media inspector**, row "On this node": a chip `GPU · zero-copy`, `GPU · 1 copy`, `GPU · copy-back` (amber at
  ≥ 4K), `CPU` (amber at ≥ 4K), or `HAP · GPU`. Hover shows the device, surfaces, VRAM and decode ms. If `why` is
  set it is the chip's second line ("tiling 0x0100000000000002 does not import here").
- **Right-click on a media item:** "Probe decode on this node" (`DECODE PROBE <media>`), "Probe on every node" (the
  same line to each peer), "Decode in software" / "Back to automatic" (`SET <media> decode …`, S3).
- **Network drawer, node card:** "Zero-copy: VAAPI → EGL · direct proven" or "Zero-copy: unavailable — GL is
  software", with the policy. Right-click: "Run the decode proof" (`DECODE PROBE ALL DIRECT`), "Policy…"
  (`DECODE POLICY`).
- **Readiness:** the `decode` context item feeds the top bar's readiness dot, with its words.
- **Companion** (module generated from verbs and STATE, R3.4; the Companion package owns the module): variables
  `decode_policy`, `decode_zero_copy` (`ready`/`unavailable`), `decode_cpu_sources` (count of on-air sources on
  copy-back or software); a feedback "Any on-air clip decodes on the CPU" (amber background); actions
  `DECODE PROBE ALL` and `DECODE POLICY …` with their risk shown on the button.

## 9. Risks and open questions

1. **R2.1 slips.** Then Windows keeps copy-back. Mitigation: S1 gives NVIDIA nodes a one-GPU-copy path on the WGL
   renderer. VideoProcessorBlt + `WGL_NV_DX_interop2` stays "later" (codecs.md §8.3).
2. **Tiled modifiers that GL cannot sample as `GL_TEXTURE_2D`** (`external_only = true`). Mitigation: checked on the
   first frame, then demote. Lab test on Intel Arc/Xe2 (4-tiled + CCS) and AMD RDNA3 [U].
3. **Multi-GPU Linux.** The VAAPI device must be the GL's render node, which is why it is chosen by EGL device query
   rather than "renderD128". Hybrid laptops are out of scope.
4. **NVIDIA proprietary on Linux.** VAAPI exists only through `nvidia-vaapi-driver`. Its dmabuf export is used by
   Firefox [U]. Without it, S1 (CUDA) is the route. GLFW EGL on NVIDIA X11 is [U]; the GLX fallback covers it.
5. **Direct sampling driver bugs** (mpv's warning). Mitigation: the proof per driver version, never on by default
   without it.
6. **VRAM.** 700 MB per 4K 10-bit HEVC source. Mitigation: `vramMb` in STATE, preflight sums, and
   `DecodeQueueFramesHw` lowers the pool if needed.
7. **Implicit sync across VAAPI and GL.** Handled by fences rather than relying on kernel implicit sync; the cost is
   ≤ 2 held surfaces.
8. **FFmpeg API drift.** The hand-declared DRM structs are guarded by the size and offset tests; an FFmpeg 10 change
   fails CI instead of corrupting memory.
9. **Open:** is `eglCreateImageKHR` from a dmabuf cheap enough per frame on iris/radeonsi (S2 decides by measurement)?
   Does ANGLE expose R16/RG16 sampling of P010 planes on every vendor (present.md §12 test 2)?

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| M1 spine (Core types, lease, health, RemappedLease, IMediaBackend.UseGpu) | 4 |
| M2 EGL context choice + fallback + tests | 3 |
| M3 VAAPI path: HwDecode (Linux), DrmFrames, GpuFrameSlot/QueuedFrame refactor, DmaBufImporter, fences, fake producer, tests | 12 |
| M4 D3D11VA shared device + ANGLE importer (compile-only, written to the verified APIs) | 7 |
| M5 ladder + demotion + starvation watch + tests | 4 |
| M6 crop uniforms + test | 1.5 |
| M7 proof (clips, harness on the render thread, file, tests) | 5 |
| M8 STATE, verbs, context, MCP, papers | 5 |
| **Must total** | **41.5** |
| S1 CUDA → GL + fake libcuda | 6 |
| S2 cache (if measured), S3 override, S4 desk | 5 |
| **With should** | **52.5** |
