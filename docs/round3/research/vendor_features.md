# Vendor-specific GPU features for a Windows-only media server / projection-mapping app (NVIDIA RTX & RTX PRO, AMD Radeon & Radeon PRO, Intel Arc), state as of October 2026, and how to layer them with fallbacks

Scope note: research done 2026-10-16. "Pro" = NVIDIA RTX PRO / former Quadro / RTX Ada / RTX A-series; AMD Radeon PRO. "API" column in the tables below means the graphics API the feature binds to (D3D12, Vulkan, OpenGL 4.6, or "display pipeline" = independent of the rendering API).

## 1. NVIDIA: NVAPI, the pro display stack (Warp & Blend, Mosaic, RTX PRO Sync, present barrier) and the NVIDIA SDKs — what exists, which card class, which API

### Takeaway
NVIDIA has the deepest stack for this product, but the projection-mapping-specific parts (driver-level Warp & Blend at scanout, Mosaic across GPUs, framelock/genlock, D3D12/Vulkan present barrier, EDID management, GPUDirect for Video) are RTX PRO/Quadro-only, while the media parts (NVENC/NVDEC via Video Codec SDK 13.1, RTX Video VSR/TrueHDR, Optical Flow, Aftermath, CUDA interop, OptiX, TensorRT-for-RTX) work on consumer GeForce RTX. Warp & Blend is applied by the display engine before scanout, so it is independent of whether PlayMap renders with D3D12, Vulkan or OpenGL; present barrier exists for D3D12 (NVAPI) and Vulkan (VK_NV_present_barrier), swap groups for OpenGL (WGL_NV_swap_group).

### Cited Findings

#### NVAPI (the base for everything NVIDIA-specific on Windows)
- The public NVAPI SDK lives on GitHub (NVIDIA/nvapi, repo created 2023-08-21); it contains "headers, interface definitions, and libraries (nvapi.lib / nvapi64.lib / nvapia64.lib / nvapia64ec.lib)"; "The actual NVAPI implementation is provided by the NVIDIA GPU driver installed on the system"; licensed MIT — [NVIDIA/nvapi README](https://github.com/NVIDIA/nvapi/blob/main/README.md); [NVIDIA/nvapi repo](https://github.com/NVIDIA/nvapi/)
- nvapi.lib and nvapi64.lib are explicitly "licensed under the following terms: ... SPDX-License-Identifier: MIT" — [NVIDIA/nvapi License.txt](https://github.com/NVIDIA/nvapi/blob/main/License.txt)
- Latest commit seen is labelled "R580-Developer SDK"; headers carry "Copyright (c) 2019-2026" and MIT SPDX — [NVIDIA/nvapi commits](https://github.com/NVIDIA/nvapi/commits/main); [NvApiDriverSettings.h](https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h)
- NvApiDriverSettings.h exposes driver-profile settings relevant to playback/pacing, e.g. `VSYNCMODE_ID`, `PRERENDERLIMIT_ID`, `VRR_MODE_ID`, `OGL_TRIPLE_BUFFER_ID`, `OGL_DEEP_COLOR_SCANOUT_ID`, `FRL_FPS_ID`, `CUDA_EXCLUDED_GPUS_ID`, `PREFERRED_PSTATE_ID` (DRS application profiles) — [NvApiDriverSettings.h](https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h)
- Detection functions in NVAPI include `NvAPI_GPU_GetGPUType`, `NvAPI_GPU_QueryWorkstationFeatureSupport`, `NvAPI_GPU_WorkstationFeatureQuery`, `NvAPI_GPU_WorkstationFeatureSetup`, `NvAPI_SYS_GetDriverAndBranchVersion` (in nvapi_lite_common.h) — [NVAPI globals index](https://docs.nvidia.com/nvapi/globals_func_n.html)
- `NVAPI_GPU_WORKSTATION_FEATURE_MASK` enumerates SWAPGROUP (0x1), STEREO (0x10), WARPING (0x100), PIXINTENSITY (0x200), GRAYSCALE (0x400), BPC10 (0x1000); `NV_GPU_WORKSTATION_FEATURE_TYPE` has `NVIDIA_RTX_VR_READY` and `PROVIZ`; the mask API "is deprecated and it is recommended to instead query NV_GPU_WORKSTATION_FEATURE_TYPE_PROVIZ support from the API NvAPI_GPU_QueryWorkstationFeatureSupport" — [NVAPI GPU group](https://docs.nvidia.com/nvapi/group__gpu.html); [NVAPI GPU group (gameworks mirror)](https://docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__gpu.html)
- `NvAPI_GetDisplayDriverVersion` is deprecated since R290: "Instead, use NvAPI_SYS_GetDriverAndBranchVersion" — [nvapi.h source](https://docs.nvidia.com/nvapi/nvapi_8h_source.html)

#### Warp and Blend at scanout (NvAPI_GPU_SetScanoutWarping / SetScanoutIntensity)
- "Warp and blend is implemented as an interface in NVAPI that programmably exposes warping and intensity adjustment features before the final scanout ... works on a single screen, multiple screens and multi-gpu configurations and are available only with NVIDIA Quadro GPUs" — [NVIDIA Warp and Blend page](https://developer.nvidia.com/warp-and-blend)
- Same page: "provides an easy way to bring this functionality to any application with minimal performance impact and no incremental latency"; up to 16 displays/projectors with Mosaic + Quadro Sync; intensity and black level per pixel. The page's dependency table is stale (Windows 7/8/10, "Quadro 1200 class or higher ... drivers 302.82 or newer") — [NVIDIA Warp and Blend page](https://developer.nvidia.com/warp-and-blend)
- Signatures: `NvAPI_GPU_SetScanoutIntensity(NvU32 displayId, NV_SCANOUT_INTENSITY_DATA*, int* pbSticky)`, `NvAPI_GPU_SetScanoutWarping(NvU32 displayId, NV_SCANOUT_WARPING_DATA*, int* piMaxNumVertices, int* pbSticky)`, `NvAPI_GPU_GetScanoutWarpingState(...)` — [NVAPI GPU group](https://docs.nvidia.com/nvapi/group__gpu.html)
- Warp mesh: vertices as triangle strip `NV_GPU_WARPING_VERTICE_FORMAT_TRIANGLESTRIP_XYUVRQ` (x,y screen; u,v texture; r,q perspective), `textureRect` = desktop rect from `NvAPI_GPU_GetScanoutConfiguration`; blend = float RGB per-pixel `blendingTexture` plus optional `offsetTexture` (black level); "sticky" flag persists across reboots — [errollw Warp-and-Blend-Quadros sample](https://github.com/errollw/Warp-and-Blend-Quadros/blob/master/WarpBlend-Quadros/WarpBlend-Quadros/WarpBlend-Quadros.cpp); [NVIDIA SIGGRAPH "Seamless Display" slides](https://www.nvidia.com/content/siggraph/Nash_Seamless_Display.pdf)
- Card support (forum, NVIDIA staff-style answer, 2023): "supported on all professional Workstation GPUs ... RTX A2000, RTX A4000, RTX A4500, RTX A5500, RTX A6000, RTX 4000SFF ADA Gen, RTX 6000 ADA Gen"; user datapoint: RTX 4070 Ti on driver 572.16 → `NVAPI_NOT_SUPPORTED`; another user reports GeForce RTX 4080 warp/blend worked on Linux with driver 550 but has no effect on 570/580 ("NVIDIA has stopped to support Warp and Blend for GeForce cards as of driver versions 570 and 580") — [NVIDIA forum: RTX Warp and Blend API](https://forums.developer.nvidia.com/t/rtx-warp-and-blend-api/249199)
- Older pro boards can also fail: Quadro K2000 on driver 472.84 returns `NVAPI_NOT_SUPPORTED` for SetScanoutWarping/Intensity/CompositionParameter — [NVIDIA forum: K2000](https://forums.developer.nvidia.com/t/does-warp-and-blend-api-support-quadro-k2000/293511)
- Products using it: VIOSO desktop embedding "requires a professional NVIDIA GPU and all outputs arranged into a Mosaic Group" — [VIOSO docs: Desktop Embedding](https://docs.vioso.com/integration/desktop-embedding); domeprojection nWarp "utilizes the NVIDIA API (NVAPI)", requires "NVIDIA RTX, A2000 or higher" — [VIOSO/domeprojection nWarp](https://www.vioso.com/simulation/software/nwarp/); Scalable Display: "We co-developed an API with Nvidia to enable driver level warping on the Quadro series", up to 16 projectors, Quadro Sync for multi-GPU — [ScalableDesktop](https://www.scalabledisplay.com/products/scalable-desktop/)
- VIOSO's 2020 table: hardware hooking "requires ... professional series ... NVIDIA Quadro (P-series and better) and AMD FirePro"; "current AMD Radeon Pro GPUs have not a clear path for supporting this" — [VIOSO legacy helpdesk: GPU setup](https://www.helpdesk.vioso.com/documentation/anyblend5-requirements/gpu-setup/)

#### Mosaic, RTX PRO Sync (ex-Quadro Sync II), swap groups, present barrier
- "The NVIDIA RTX PRO Sync card is a rebranding of the Quadro Sync II card that was aligned with the launch of the NVIDIA RTX PRO Blackwell cards. Both cards offer equivalent features"; up to 8 GPUs with two cards; external sync up to 1000 Hz; sync multiply/divide; "Enables Synchronized Mosaic"; GPUs: "Pascal-series graphics boards and later, 4000class and above"; Windows 10/11; "R440 branch and newer display drivers support ... Mosaic topologies"; SwapReady hardware signal; present barrier / swap barrier need CAT-5 between nodes — [NVIDIA RTX PRO Sync User Guide](https://images.nvidia.com/aem-dam/Solutions/design-visualization/quadro-product-literature/nvidia-rtx-pro-sync-user-guide.pdf)
- Spec: 4 GPUs per Sync card, 2 cards per system, 8 synchronized GPUs, 32 displays (also 32 with Mosaic) per system; compatible GPUs include RTX PRO 6000 (Server/Workstation/Max-Q), 5500, 5000, 4500, 4000, 4000 SFF Blackwell, RTX 6000/5000/4500/4000/4000 SFF Ada, RTX A6000/A5500/A5000/A4500/A4000, A40, L40/L40S, Quadro RTX 8000/6000/5000/4000, GV100 — [NVIDIA RTX PRO Sync page](https://www.nvidia.com/en-us/design-visualization/solutions/rtx-pro-sync/)
- Sync firmware 3.06 applies to RTX PRO Sync and Quadro Sync II — [NVIDIA Sync firmware 3.06](https://www.nvidia.com/en-us/drivers/firmware/3_06/nv-quadro-sync-windows-update-3-06-driver/); Mosaic Utility supported-product list is the same pro list (no GeForce) — [NVIDIA Mosaic Utility](https://www.nvidia.com/en-us/drivers/mosaic-utility/)
- Present barrier (D3D12): "combine swap groups and swap barriers into a single high-level interface"; functions `NvAPI_D3D12_QueryPresentBarrierSupport`, `NvAPI_D3D12_CreatePresentBarrierClient(device, swapChain, &handle)`, `NvAPI_D3D12_RegisterPresentBarrierResources(handle, fence, backBuffers, count)`, `NvAPI_JoinPresentBarrier`; only effective "in a full-screen state with no window borders, as well as no desktop scaling or taskbar composition", otherwise it reverts to a pending state; display sync via Quadro Sync or a Mosaic surface — [NVIDIA blog: present barrier DX12](https://developer.nvidia.com/blog/synchronizing-present-calls-between-applications-on-distributed-systems-with-directx-12/)
- Sample requirements: "NVIDIA RTX / Quadro series card (GeForce is NOT supported)", driver 472.12+, NVAPI R470+ — [nvpro-samples/dx12_present_barrier](https://github.com/nvpro-samples/dx12_present_barrier)
- NVIDIA forum answer (2025): "Present barrier is an updated API replacing the swap groups and barriers and available for both Vulkan and DX12"; non-blocking ("pre-present wait") behaviour is default; swap group/barrier only work fullscreen; DX11 uses NvAPI swap group + swap barrier — [NVIDIA forum: swap group vs present barrier](https://forums.developer.nvidia.com/t/quadro-sync-ii-clarification-on-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/323309/2)
- Vulkan maturity datapoint: Oct 2025 report that a second fullscreen swapchain fails to join `VK_NV_present_barrier` (2× RTX A5000, Quadro Sync II, driver 581.42) while the same wall works with OpenGL `WGL_NV_swap_group` — [NVIDIA forum: VK_NV_present_barrier](https://forums.developer.nvidia.com/t/cant-create-more-than-1-swapchain-with-vk-nv-present-barrier/349276)
- Linux driver docs list GLX_NV_swap_group as the OpenGL mechanism, with "a universal frame counter" — [NVIDIA Linux README: Frame Lock](https://download.nvidia.com/XFree86/Linux-x86_64/570.211.01/README/framelock.html)

#### Video Codec SDK (NVENC / NVDEC)
- Current SDK 13.1; 13.1 adds UHQ for iterative encoding, app-allocated CUarray as NVENC/NVDEC input, AV1 hierarchical-B, NVDEC QP/MB-type stats, seek, MV-HEVC decode view info; Jetson Thor added January 2026 — [NVIDIA Video Codec SDK page](https://developer.nvidia.com/video-codec-sdk)
- FFmpeg's nv-codec-headers "Corresponds to Video Codec SDK version 13.1.15", minimum driver 610 (Windows and Linux) — [FFmpeg/nv-codec-headers](https://github.com/FFmpeg/nv-codec-headers)
- SDK 13.0 (Jan/Feb 2025, Blackwell): encode — H.264 10-bit, interlaced and 4:2:2; HEVC 4:2:2; MV-HEVC; AV1 LTR; temporal layers; MaxCLL/mastering-display SEI; decode — 2× H.264 throughput and 8192×8192, H.264 High10/High422, HEVC 4:2:2 10/12-bit, dynamic decode surfaces — [Video Codec SDK 13.0 Read Me](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/read-me/index.html); [NVIDIA blog: SDK 13.0 Blackwell](https://developer.nvidia.com/blog/nvidia-video-codec-sdk-13-0-powered-by-nvidia-blackwell/)
- Encode matrix: H.264 4:2:2 and High10 only on Blackwell; 4:4:4 (CAVLC) on Turing+; HEVC Main10 and 4:4:4 on Turing+, HEVC 4:2:2 Blackwell only; AV1 8/10-bit 4:2:0 up to 8192×8192 on Ada and Blackwell — [NVENC Application Note 13.1](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-application-note/index.html)
- NVDEC codecs: MPEG-2, VC-1, H.264, HEVC, VP8, VP9, AV1; Blackwell adds 4:2:2 H.264/HEVC decode — [NVIDIA Video Codec SDK page](https://developer.nvidia.com/video-codec-sdk)
- Session limit: "On non-qualified GPUs, the number of concurrent encode sessions is limited to 12 per system ... combined ... on all non-qualified cards"; qualified (pro) GPUs are limited only by resources — [NVENC Application Note 13.1](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-application-note/index.html); contradicted by stale sample text in the 13.0 Read Me ("restricted to 5 sessions" / "3 sessions" on GeForce) — [Video Codec SDK 13.0 Read Me](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/read-me/index.html)
- `NvEncodeAPIGetMaxSupportedVersion` returns the max SDK version the installed driver supports (runtime capability check) — [NVENC Programming Guide 13.1](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/)
- NVENC/NVDEC "can also be accessed via low-level APIs such as DirectX and Vulkan Video" — [NVIDIA Video Codec SDK page](https://developer.nvidia.com/video-codec-sdk)
- FFmpeg 8.0 adds NVENC MV-HEVC, HEVC/H.264 4:2:2 8/10-bit, H.264 4:2:0 10-bit; NVDEC H.264 10-bit, H.264/HEVC 4:2:2 — [NVIDIA: Using FFmpeg with NVIDIA GPU](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html)

#### RTX Video SDK and the Video Effects (VFX, ex-Maxine) SDK
- RTX Video SDK = AI Video Super Resolution (upscale + compression-artifact removal) and RTX Video HDR (SDR→HDR10); "Optimized to run on Tensor-Core-powered RTX GPUs" — [NVIDIA RTX Video SDK page](https://developer.nvidia.com/rtx-video-sdk)
- "available on Windows 10 and 11 64-bit platforms. It supports DirectX 11, DirectX 12, and Vulkan, with CUDA support coming soon" (June 2024) — [NVIDIA blog: RTX Video SDK](https://developer.nvidia.com/blog/enhancing-low-resolution-sdr-video-with-the-nvidia-rtx-video-sdk/)
- VFX SDK 1.3.0 features: AI Green Screen, Background Blur, Upscale, Denoising, Relighting, Video Super Resolution, Video Frame Generation, TrueHDR; distributed via NGC; features installed per GPU compute capability 75, 86, 89, 120 (Turing/Ampere/Ada/Blackwell) and 121 (WoA); "We do not officially support ... deployment of this SDK to a datacenter or cloud environment" — [NVIDIA VFX SDK User Guide 1.3.0](https://docs.nvidia.com/maxine/vfx/1.3.0/nvidia-vfx-sdk-user-guide.pdf)
- TrueHDR: "Given an 8-bit SDR RGBA input, TrueHDR produces a packed 10-bit output in Rec.2020 primaries with the SMPTE ST 2084 (PQ) transfer function"; governed by NVIDIA Software License Agreement + Product-Specific Terms for AI Products, models under NVIDIA Open Model License; "available for commercial/non-commercial use" — [NGC TrueHDR](https://catalog.ngc.nvidia.com/orgs/nvidia/maxine/collections/nvvfxtruehdr/-)

#### Optical Flow SDK
- Optical Flow SDK 5.0 adds a Vulkan interface on Ampere+; NVOFA hardware since Turing; package includes NvOFFRUC frame-rate up-conversion library (ARGB/NV12, DirectX or CUDA) — [Optical Flow SDK release notes](https://docs.nvidia.com/video-technologies/optical-flow-sdk/release-notes/index.html); [NVOFA FRUC guide](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvfruc-programming-guide/index.html); [NVIDIA blog: OF SDK 5.0 Vulkan](https://developer.nvidia.com/blog/accelerated-motion-processing-brought-to-vulkan-with-optical-flow-sdk/)
- SDK 5.0 needs R525+; stereo-disparity mode slated for removal — [NVOFA Application Note](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvofa-application-note/index.html); [OF SDK deprecation notices](https://docs.nvidia.com/video-technologies/optical-flow-sdk/deprecation-notices/index.html)
- Community report (mpv issue): distributed FRUC binaries were not rebuilt for Blackwell (source recompiled works); FRUC's licence made an FFmpeg filter non-redistributable — [mpv issue #11060](https://github.com/mpv-player/mpv/issues/11060) (secondary source; not NVIDIA-confirmed)

#### Reflex, DLSS / Streamline
- NVIDIA RTX SDKs licence covers DLSS, Reflex, NGX, RTXGI, RTXDI, RTX Video SDK, RTX Dynamic Vibrance, NRD: apps "must be fully interoperable with ... NVIDIA" GPUs; DLSS/Reflex/NGX/RTX Video/Dynamic Vibrance may be deployed in cloud only on NVIDIA vGPU; "You are required to notify NVIDIA prior to commercial release" (DLSS, Reflex, NGX, Dynamic Vibrance); NVIDIA may push over-the-air updates and "temporarily disable the DLSS integration" if issues aren't fixed — [NVIDIA RTX SDKs License (RTXCR copy)](https://github.com/NVIDIA-RTX/RTXCR/blob/main/License.txt)
- DLSS Frame Generation via Streamline needs Windows 10 20H1+, HAGS enabled, DX12 or Vulkan 1.2+, driver 512.15+, and Reflex integrated; DLSS-G presents asynchronously through an internal "SL pacer" thread/queue — [Streamline DLSS-G integration (DeepWiki summary of NVIDIA-RTX/Streamline)](https://deepwiki.com/NVIDIA-RTX/Streamline/5.3-dlss-frame-generation-integration)

#### Nsight Aftermath (GPU crash dumps in the field)
- Current: Nsight Aftermath SDK 2026.3 (2026-07-23, R610 driver); prior 2025.5 (R590), 2025.4 (R580), 2025.1 (R570, Blackwell support) — [Aftermath release history](https://developer.nvidia.com/nsight-aftermath-history); [Aftermath getting started](https://developer.nvidia.com/nsight-aftermath/getting-started)
- Supports D3D12, DXR, D3D11 (basic), Vulkan; must call `GFSDK_Aftermath_EnableGpuCrashDumps` before creating any device; Vulkan features via `VK_NV_device_diagnostics_config`/`NV_device_diagnostic_checkpoints`; "all .dll / .so files in the lib directory" are distributable per LICENSE — [Aftermath SDK Guide](https://docs.nvidia.com/nsight-aftermath/SDK/index.html)
- GPU requirement conflict: SDK guide says "NVIDIA Turing GPU or newer"; getting-started page says "Pascal and newer" — [Aftermath SDK Guide](https://docs.nvidia.com/nsight-aftermath/SDK/index.html); [Aftermath getting started](https://developer.nvidia.com/nsight-aftermath/getting-started)

#### GPUDirect for Video, Rivermax (SDI/ST 2110 I/O)
- GPUDirect for Video is for "IO board manufacturers"; available to app developers "through 3rd Party partner SDKs" (OpenGL, DirectX, CUDA; Windows/Linux); "Select NVIDIA Quadro, Tesla, or GRID products" — [NVIDIA GPUDirect for Video](https://developer.nvidia.com/gpudirectforvideo)
- WATCHOUT 7 lists "GPUDirect (Deltacast capture/playout)" as supported on RTX PRO and not on GeForce — [WATCHOUT 7 system requirements](https://docs.dataton.com/guide/watchout/getting-started/system-requirements.html)
- Rivermax: "licensed, closed source library"; ConnectX NICs or BlueField DPUs; "For Windows OS, strict timing requirements (PTP, ST2110) necessitate the use of a BlueField card"; GPUDirect supported on Linux and Windows; ST 2110-20/-30/-31/-40/-22, 2022-7, IPMX — [Rivermax FAQ](https://developer.nvidia.com/networking/rivermax/faq)
- Free development licence valid three months, renewable; production licences purchased; Resizable BAR needed for GPUDirect RDMA — [Holoscan for Media: Rivermax](https://docs.nvidia.com/holoscan-for-media/latest/user-guide/getting-help/rivermax-st-2110.html); Windows tuning guide (Jan 2026) — [Rivermax Windows tuning](https://enterprise-support.nvidia.com/s/article/Rivermax-Windows-Performance-Tuning-Guide)

#### CUDA interop with D3D12 / Vulkan
- External-resource interop is "supported for ... Direct3D[11-12], Vulkan"; D3D12 heaps/committed resources created with `D3D12_HEAP_FLAG_SHARED` import via NT handle as `cudaExternalMemoryHandleTypeD3D12Heap` / `...D3D12Resource` (flag `cudaExternalMemoryDedicated`); Vulkan via `VK_KHR_external_memory_win32` opaque Win32 handles; semaphores via `cudaImportExternalSemaphore` (D3D12 fence, Vulkan timeline/binary semaphore, keyed mutex) — [CUDA Programming Guide: interop](https://docs.nvidia.com/cuda/cuda-programming-guide/04-special-topics/graphics-interop.html)
- Must match the physical device by UUID (or LUID on Windows) — "a device ordinal will not do"; importing an NT handle does not transfer ownership — [The CUDA Handbook §3.11](https://cudahandbook.com/book/ch3/external-memory-and-graphics)

#### OptiX (ray tracing for projection planning / light simulation)
- OptiX 9.1.0 (December 2025, needs R590+); 9.0.0 (February 2025, R570+, Blackwell, cluster/MegaGeometry, cooperative vectors) — [OptiX downloads](https://developer.nvidia.com/designworks/optix/download); [NVIDIA/optix-sdk releases](https://github.com/NVIDIA/optix-sdk/releases)
- "OptiX is free to use within any application, including commercial"; runs on GeForce, RTX/Quadro and data-centre GPUs, Turing and newer — [OptiX downloads](https://developer.nvidia.com/designworks/optix/download)
- Licence: binary-only redistribution inside your product with enforceable end-user terms; "You are required to notify NVIDIA prior to use ... in a commercial application"; no use that makes it subject to an open-source licence; headers published as NVIDIA/optix-dev — [optix-dev LICENSE](https://github.com/NVIDIA/optix-dev/blob/v9.0.0/LICENSE.txt); [OptiX 9.0 release forum post](https://forums.developer.nvidia.com/t/optix-9-0-release/322842)

#### Tensor inference
- Windows ML's NVIDIA EP `NvTensorRtRtxExecutionProvider`: GeForce RTX 30xx and above, driver 32.0.15.5585+ / CUDA 12.5 — [Windows ML supported EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers); "over 50% faster inferencing on NVIDIA RTX GPUs compared to DirectML" — [Windows blog: Windows ML GA](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices/)

### Inferences
- Catalogue (NVIDIA), card class and API:

| Feature | SDK / API | Card | Graphics API it binds to | Notes for PlayMap |
|---|---|---|---|---|
| Warp + blend + black level at scanout | NVAPI `SetScanoutWarping/Intensity` | RTX PRO only (GeForce returns NOT_SUPPORTED on current drivers) | display pipeline (any API, also desktop) | Optional "hardware warp" output path; PlayMap's own shader warp stays the reference path |
| Mosaic (one desktop across outputs/GPUs) | NVAPI Mosaic / Mosaic Utility | RTX PRO (GeForce has "Surround" only) | display pipeline | Useful for single-swapchain spanning; not required |
| Framelock / genlock | RTX PRO Sync card + driver | RTX PRO 4000-class+ | display pipeline | Read status via NVAPI GSync; set up in driver |
| Present barrier / swap group | NvAPI D3D12 present barrier; `VK_NV_present_barrier`; `WGL_NV_swap_group` | RTX PRO + Sync or Mosaic, fullscreen | D3D12 / Vulkan / OpenGL respectively | Vulkan path has field reports of multi-swapchain issues |
| EDID management | NVAPI | RTX PRO (per WATCHOUT) | n/a | |
| NVENC/NVDEC | Video Codec SDK 13.1, or FFmpeg | all RTX; GeForce encode capped 12 sessions/system | CUDA, D3D11/12 inputs, Vulkan Video | 4:2:2 decode/encode needs Blackwell (RTX 50 / RTX PRO Blackwell) |
| VSR / TrueHDR | RTX Video SDK; VFX SDK 1.3 | RTX (Tensor cores) | D3D11/D3D12/Vulkan (RTX Video SDK) | AI upscale low-res content to projector res; SDR→HDR10 |
| Optical flow / FRUC | Optical Flow SDK 5.0 | Turing+ (Vulkan Ampere+) | D3D11/D3D12/CUDA/Vulkan | Slow-motion/retime; Blackwell binary status unclear |
| GPU crash dumps | Nsight Aftermath 2026.3 | all (Turing+ per guide) | D3D12, Vulkan | Ship in field; must init before device creation |
| SDI/2110 zero-copy | GPUDirect for Video (via AJA/Deltacast/... SDKs); Rivermax | pro GPUs; ConnectX/BlueField NIC | OpenGL/DX/CUDA | Rivermax needs paid licence |
| Compute interop | CUDA external memory | all | D3D12, Vulkan | Match by LUID |
| Ray tracing | OptiX 9.1 (NVIDIA-only) vs DXR / Vulkan RT (cross-vendor) | all RTX | CUDA (OptiX) | Projection planning / light sim; notify NVIDIA |
| DLSS SR/FG, Reflex | Streamline / RTX SDKs | RTX (FG: RTX 40/50, HAGS) | D3D12/Vulkan | Low relevance: FG inserts synthesized frames and its own pacing, conflicting with "frame = f(show time)" and genlocked present |

- Because Warp & Blend lives in the display engine "before the final scanout", it is a natural *optional* last stage: PlayMap could hand the mesh + blend maps it already computes to NVAPI on RTX PRO and render unwarped, but must keep its shader warp for GeForce/AMD/Intel and as the authoritative visual (the doctrine "attempts are not facts" implies reading back state via `GetScanoutWarpingState` after setting).
- GeForce support for scanout warp appears to have been removed or never official on Windows; treat Warp & Blend as RTX PRO-only and probe at run time rather than gate on a GPU list.
- Present barrier on Vulkan seems less mature than D3D12/OpenGL in the field (one report); if Vulkan is chosen as the primary API, budget testing time on Sync hardware.

### Gaps
- No current NVIDIA document states Warp & Blend card requirements for RTX PRO Blackwell explicitly (the developer page is stale; support for RTX PRO Blackwell is inferred from the "all professional workstation GPUs" forum answer).
- Whether RTX PRO Blackwell entry cards (RTX PRO 2000/1000) support Warp & Blend, Mosaic and Sync was not confirmed (Sync list starts at RTX PRO 4000).
- RTX Video SDK current version number and whether CUDA support shipped were not found.
- "NVIDIA Broadcast SDK" naming/status in 2026 not separately researched (the VFX SDK 1.3 covers the effects).
- NVAPI GSync (framelock status/query) function list and whether all Mosaic functions are available on GeForce Surround were not verified.
- NVDEC concurrency limits on GeForce (none documented in sources found).

## 2. AMD: AGS, ADLX, AMF, FidelityFX/FSR, Anti-Lag 2, Radeon PRO sync, Radeon GPU Detective, ROCm/HIP on Windows

### Takeaway
AMD offers solid, permissively licensed media and system SDKs (AMF 1.5.3 MIT, AGS 6.3.1 MIT, FidelityFX/FSR MIT source + signed DLLs) that work on consumer Radeon, but no driver-level warp/blend API, and current-generation Radeon PRO (RDNA2/3/4) shows no evidence of framelock/genlock support — the FirePro S400 sync module only covers older FirePro/Radeon Pro WX/VII cards. AMD's hardware codecs are 4:2:0-only (no 4:2:2/4:4:4) through VCN 5.0 (RDNA4). ROCm on Windows (HIP SDK 7.2) supports RDNA3/RDNA4 but the full ROCm stack is still not on Windows.

### Cited Findings

#### AGS (AMD GPU Services)
- Latest v6.3.1 adds VS2026 binaries; 6.3 (May 2025) adds RDNA4 detection "and more robust driver version and GPU specifications detection"; 6.2 shader clock intrinsics; provides adapter string/device/revision/vendor id/ASIC family, clocks, local/shared memory, app registration for DX11/DX12, wave reduce/scan intrinsics, user debug markers, display enumeration "including information on display name ... HDR and AMD FreeSync Premium Pro capabilities"; DX11-only extras: quad list, UAV overlap, depth bounds, multi-draw indirect, multiview — [AMD GPUOpen: AGS](https://gpuopen.com/amd-gpu-services-ags-library/); [AGS v6.3.0 release](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/releases/tag/v6.3.0)
- DX12 extensions require creating the device through `agsDriverExtensionsDX12_CreateDevice()` instead of `D3D12CreateDevice()` — [AGS README](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/blob/master/README.md); [AGS DX12 docs](https://gpuopen-librariesandsdks.github.io/ags/group__dx12.html)
- AGS also provides Crossfire and Eyefinity configuration info — [AGS_SDK repo](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/)
- Licence: MIT — [AGS LICENSE.txt](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/blob/master/LICENSE.txt)

#### ADLX (display, desktop/Eyefinity, tuning, monitoring) and legacy ADL (framelock)
- ADLX Display interfaces: 3DLUT, Blanking, ColorDepth, ConnectivityExperience, CustomColor, CustomResolution, DynamicRefreshRateControl, FreeSync (+ColorAccuracy), GPUScaling, Gamma, Gamut, HDCP, IntegerScaling, PixelFormat, Resolution, ScalingMode, VSR, VariBright — [ADLX Display reference](https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/display/)
- Categories: Display, 3DGraphics (AA, FSR, AFMF, Radeon Boost...), Desktop ("Eyefinity multi-display configuration"), GPUTuning, PerformanceMonitoring, PowerTuning, MultiMedia (VSR), events, I2C, Smart Access Memory; C/C++/C#/Java/Python bindings; ships MCP servers for AI-agent control of GPU settings; "forward and backward compatible with AMD drivers" — [ADLX GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)
- "The ADLX library is installed with the driver as a .dll file" — [ADLX Programming Guide](https://gpuopen.com/manuals/adlx/programming-with-adlx/adlx-programming-guide/)
- Framelock/genlock is exposed only in legacy ADL: `ADL2_Workstation_GLSyncModuleDetect_Get`, `..._GLSyncGenlockConfiguration_Get/Set`, `..._DisplayGLSyncMode_Get/Set`, `..._GLSyncPortState_Get/Set`, `..._GLSyncSupportedTopology_Get` — [ADL Framelock/Genlock APIs](https://gpuopen-librariesandsdks.github.io/adl/group__FLGLAPI.html)
- Licence: "ADLX SDK is distributed under the license included in ADLX SDK License Agreement.pdf" (not MIT; terms not read) — [ADLX GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)

#### Radeon PRO framelock / genlock (FirePro S400) — status
- S400: up to 4 GPUs per module, house sync (analog black burst, tri-level, TTL, SDI), RJ-45 daisy chain; originally for FirePro W9000/W8000/W7000 (2012-era); "Framelock supported in OpenGL" — [Sapphire/AMD FirePro S400 datasheet](https://www.azken.com/download/SAPPHIRE_AMD_FirePro_S400_SYNC.pdf)
- Reseller compatibility list (still sold, €667): Radeon PRO VII, WX 9100, WX 8200, WX 7100, FirePro W9100/W9000/W8100/W8000/W7000/W7100, V-series — no RDNA cards listed — [Schneider Digital S400](https://shop.schneider-digital.com/en/graphics-cards/amd/firepro/amd-firepro-s400-g-sync-option-card)
- Radeon Pro VII (2020): "Frame Lock/Genlock ... (AMD FirePro S400 synchronization module required)" — [AMD press release 2020-05-13](https://www.amd.com/en/newsroom/press-releases/2020-5-13-amd-expands-professional-offerings-with-amd-radeon.html)
- AMD Software PRO Edition 22.Q3 and 22.Q4 (driver for Radeon Pro W6800/W6600/W5700...): "Framelock/Genlock and MGPU VR are not currently supported" — [PRO Edition 22.Q4 notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-PRO-WIN-22-Q4.html); [22.Q3 notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-PRO-WIN-22-Q3.html)
- Radeon PRO W7900/W7800 datasheets list Supported Technologies "Workstation, AMD Remote, Radeon Media Engine, Software PRO Edition, VR Ready Creator, Eyefinity" — no framelock/genlock listed; product pages list 10/12-bit output and 3D stereo — [W7900 datasheet](https://www.amd.com/content/dam/amd/en/documents/products/graphics/workstation/radeon-pro-w7900-datasheet.pdf); [W7800 datasheet](https://www.amd.com/content/dam/amd/en/documents/products/graphics/workstation/radeon-pro-w7800-datasheet.pdf); [W7800 product page](https://www.amd.com/en/products/graphics/workstations/radeon-pro/w7800.html)
- Media servers: disguise X Series — "NVIDIA RTX and Quadro Professional GPUs must be used where genlock is a requirement", Quadro Sync II required — [disguise X Series specs](https://help.disguise.one/disguise-x-series/disguise-x-series-specifications); disguise still documents AMD FL/GL setup for older VX 4 / VX 4+ hardware — [disguise genlock configuration](https://help.disguise.one/designer/configuration/genlock-configuration); 7thSense used S400 with Radeon Pro 18.Q2.1 — [7thSense AMD genlock guide](https://portal.7thsense.one/user-guides/MC264-display-configuration/amd_radeon_pro_synchronization_(ge.html); WATCHOUT 7: hardware genlock/framelock, EDID management and GPUDirect need NVIDIA pro GPUs; "AMD GPUs may work for basic playback" — [WATCHOUT 7 system requirements](https://docs.dataton.com/guide/watchout/getting-started/system-requirements.html)

#### AMF (Advanced Media Framework)
- AMF 1.5.3 (September 2026; needs Adrenalin 26.9.2+): color matrix for VideoConverter/VideoDecoder, initial VPE support in VideoConverter, cursor embedding in DirectCapture, rotation, FFmpeg 8.1.2; 1.5.2 needs Adrenalin 26.5.1; 1.4.36 added AV1 B-frames and HQ presets; 1.4.28 added AV1 encode and 12-bit AV1 decode; 1.4.26 PreAnalysis PAQ/TAQ, HQScaler sharpness, "low latency decoder"; 1.4.24 Direct Capture, HQScaler (bilinear/bicubic/FSR), Vulkan HEVC encoder — [AMD GPUOpen: AMF](https://gpuopen.com/advanced-media-framework/); [AMF GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/); [AMF releases](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/releases)
- AV1 encoder usages: transcoding, ultra-low-latency, low-latency, webcam, HQ, HQLL; latency modes incl. LOWEST_LATENCY; decoder `AMF_VIDEO_DECODER_LOW_LATENCY`; SmartAccess Video spreads encode/decode across multiple VCN instances (APU+dGPU) — [AMF AV1 encoder API](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/amf/doc/AMF_Video_Encode_AV1_API.md); [AMF wiki: SmartAccess Video](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/wiki/Smart-Access-Video-Primer)
- Codec matrix: "All codecs are 4:2:0"; VCN 4.0 (RDNA3) and 5.0 (RDNA4): decode AVC 8-bit 4K, HEVC 8/10-bit 8K, VP9 8/10-bit 8K, AV1 8/10/12-bit 8K; encode AVC 8-bit 4K, HEVC 8/10-bit 8K, AV1 8/10-bit 8K; "AV1 encoder supported in one VCN instance only" — [AMF wiki: GPU HW features](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/wiki/GPU-and-APU-HW-Features-and-Support)
- AMD answer on RDNA4: "RX 9xxx supports HEVC 4:2:0, 8 and 10 bit for encoding and decoding" (no 4:2:2/4:4:4) — [AMF issue #539](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/issues/539)
- Radeon PRO W7900/W7800: 2× encode/decode streams (HEVC, 4K H.264), 1× AV1 encode & decode — [W7900 datasheet](https://www.amd.com/content/dam/amd/en/documents/products/graphics/workstation/radeon-pro-w7900-datasheet.pdf)
- Licence: MIT, with notice that AMD grants no codec patent rights and that the package "uses libraries from the FFmpeg project under the LGPLv2.1" — [AMF LICENSE.txt](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/LICENSE.txt)

#### FidelityFX / FSR SDK
- FidelityFX SDK 2.0.0 (2025-08-20): FSR 4.0.2 ML upscaling + FSR 3.1.5 frame generation; must use `amd_fidelityfx_loader.dll` and `amd_fidelityfx_upscaler.dll` (signed DLLs); "FSR 4 upscaling requires an AMD Radeon RX 9000 Series GPU or better ... When running on other hardware, the AMD FidelityFX API will automatically select AMD FSR 3.1.5"; FSR 3.1.5 needs Shader Model 6.2; driver can upgrade the ML model in shipped apps; legacy effects frozen in SDK 1.1.4 — [GPUOpen: FidelityFX SDK 2.0](https://gpuopen.com/learn/amd-fidelityfx-sdk-2-0/); [FidelityFX SDK v2.0.0 release](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.0.0)
- FSR 4 requires RDNA4 "and the game using DirectX 12" — [GPUOpen: FSR 4 in 85 games](https://gpuopen.com/news/amd-fsr4-over-85-games/); later SDK releases are branded "AMD FSR SDK" carrying FSR Redstone ML features — [FidelityFX-SDK releases](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/)
- FSR 4 source was published (MIT) in the SDK 2.0 package, reportedly by accident, then pulled; forks persist — [Guru3D](https://www.guru3d.com/story/amd-accidentally-opensources-fsr-4-code-on-github/); [FSR 4.0.2 reference mirror](https://github.com/Rolaand-Jayz/FSR-4.0.2-reference) (secondary sources)

#### Anti-Lag 2
- SDK for DX11 (Adrenalin 24.6.1+), DX12 (24.7.1+), Vulkan (24.9.1+, `VK_AMD_anti_lag`); RDNA1 (RX 5000) and newer; API: `AntiLag2DX12::Initialize(&ctx, device)` returns S_OK if present, `Update(&ctx, enable, fpsLimit)` before input poll — [GPUOpen: Anti-Lag 2 integration](https://gpuopen.com/learn/integrating-amd-radeon-anti-lag-2-sdk-in-your-game/); [AntiLag2-SDK GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/AntiLag2-SDK); [Adrenalin 24.9.1 notes](https://www.amd.com/es/resources/support-articles/release-notes/RN-RAD-WIN-24-9-1.html)
- UE plugin 2.0.5 (August 2026) — [GPUOpen: Unreal Engine](https://gpuopen.com/unreal-engine/)

#### Radeon GPU Detective (RGD)
- v1.6.3 (June 2026); D3D12 and Vulkan; Windows 11; RDNA2/3/4; "Must TDR"; captured through Radeon Developer Panel; v1.5 adds HLSL source via PDBs; v1.4 hardware crash analysis; recommends AGS string markers and `SetName`/`vkSetDebugUtilsObjectNameEXT` — [GPUOpen: RGD](https://gpuopen.com/radeon-gpu-detective/); [RGD manual](https://gpuopen.com/manuals/rgd_manual/); [RGD release notes](https://github.com/GPUOpen-Tools/radeon_gpu_detective/blob/master/RGD_RELEASE_NOTES.txt)

#### ROCm / HIP on Windows
- HIP SDK for Windows 7.2 (release notes dated 2026-07-31), Windows 11; earlier 7.1.1, 6.4.2 (Win10/11, Server 2022) — [AMD HIP SDK page](https://www.amd.com/en/developer/resources/rocm-hub/hip-sdk.html); [HIP SDK 7.2 release notes](https://rocm.docs.amd.com/projects/install-on-windows/en/latest/about/releasenotes.html)
- HIP SDK 7.1.1 Windows GPUs: RX 9070/9060 (RDNA4) and RX 7900–7600 (RDNA3) supported; RDNA2 not; Radeon AI PRO R9700 and PRO W7900 in ROCm 7.2.1 Windows matrix; "Pytorch on Windows includes ROCm 7.2.1 components; however, the entire ROCm stack is not yet supported on Windows" — [HIP SDK 7.1.1 system requirements](https://rocm.docs.amd.com/projects/install-on-windows/en/docs-7.1.1/reference/system-requirements.html); [ROCm Windows support matrix](https://rocm.docs.amd.com/projects/radeon-ryzen/en/latest/docs/compatibility/compatibilityrad/windows/windows%5Fcompatibility.html)
- "Windows releases trail Linux releases"; "Not all source code required to build Windows from source is available under a permissive open source license" — [ROCm release versioning](https://rocm.docs.amd.com/projects/install-on-windows/en/latest/about/release-versioning.html)
- Windows ML AMD EPs: `MIGraphXExecutionProvider` (GPU) and `VitisAIExecutionProvider` (NPU) — [Windows ML supported EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)

### Inferences
- Catalogue (AMD):

| Feature | SDK | Card | API | PlayMap use |
|---|---|---|---|---|
| GPU/driver detect, display HDR/FreeSync caps, app registration, DX12 intrinsics | AGS 6.3.1 (MIT) | RDNA+ | D3D11/D3D12 (device must be created via AGS) | Detection + debug markers for RGD |
| Display control (FreeSync off, color depth, pixel format, custom resolution, 3D LUT, Eyefinity) | ADLX (own licence, DLL ships with driver) | Radeon & PRO | display pipeline | Force FreeSync off, 10-bit, RGB full on projector outputs |
| Framelock/genlock | ADL Workstation GLSync + S400 | only pre-RDNA FirePro/Radeon Pro WX/VII | display pipeline | Treat as legacy; don't build on it |
| HW decode/encode | AMF 1.5.3 (MIT) or FFmpeg (d3d11va/amf) | RDNA2+ | D3D11/D3D12/Vulkan surfaces | 4:2:0 only; no ProRes-class 4:2:2 HW decode |
| Upscaling | FSR 4 (RDNA4, D3D12) / FSR 3.1.5 (SM6.2) | all (FSR3) | D3D12 (+Vulkan for FSR 3) | Spatial/temporal upscale of 3D scenes only; needs motion vectors for temporal |
| Latency | Anti-Lag 2 | RDNA1+ | D3D11/D3D12/Vulkan | Low relevance (input-latency for games) |
| Crash analysis | RGD 1.6.3 | RDNA2+ | D3D12/Vulkan | Developer-side; no in-app dump SDK found |
| Compute | HIP SDK 7.2 | RDNA3/4 | (interop not verified) | Prefer D3D12/Vulkan compute or Windows ML |

- No AMD equivalent of NVIDIA scanout warp/blend appears in ADLX's interface list; PlayMap's own shader warp/blend is the only path on AMD.
- For a product whose market values genlock, AMD is viable for single-machine shows and multi-machine "software sync" (PlayMap's time-based sync), but not for hardware-locked LED/camera work in 2026.

### Gaps
- No AMD statement found that explicitly says "RDNA2/3/4 Radeon PRO do not support framelock/genlock"; conclusion is from absence in datasheets, S400 compatibility lists and the 22.Q3/Q4 note. "Radeon PRO Simple Synchronization" was not found in any source.
- ADLX licence terms (PDF) not read; HIP interop with D3D12/Vulkan external memory on Windows not verified.
- Whether AMD drivers now support HAGS on RX 7000/9000 (Puget's earlier test said AMD didn't) not verified.
- Which AMD GPUs the disguise VX 4/VX 4+ use for genlock was not verified.

## 3. Intel Arc (brief): Intel VPL, XeSS, other

### Takeaway
Intel's media stack (Intel VPL, MIT) is the broadest in codecs (adds VVC decode) and works on Arc Alchemist/Battlemage and recent iGPUs; XeSS 3 SDK gives SR on any SM6.4 GPU and frame generation on Arc and other vendors. Relevance for PlayMap is mostly as a decode backend on Intel-based nodes and iGPU-assisted decode.

### Cited Findings
- oneVPL renamed Intel Video Processing Library (Intel VPL), no longer part of oneAPI spec; successor to discontinued Media SDK; supports Iris Xe, Arc, Flex, 11th-gen+ iGPUs — [Intel VPL overview](https://www.intel.com/content/www/us/en/developer/tools/vpl/overview.html)
- libvpl dispatcher MIT; spec 2.17.0; VPL GPU runtime MIT: encoders HEVC, AVC, MPEG-2, JPEG, VP9, AV1; decoders HEVC, AVC, VP8, VP9, MPEG-2, VC1, JPEG, AV1, VVC; platforms TGL…DG2 (Alchemist), MTL/ARL, LNL, BMG (Battlemage), PTL, NVL; MPEG-2 decode removed from Nova Lake on — [intel/libvpl](https://github.com/intel/libvpl/); [Intel VPL spec](https://intel.github.io/libvpl/latest/index.html); [intel/vpl-gpu-rt](https://github.com/intel/vpl-gpu-rt?tab=readme-ov-file)
- VPL API 2.13 adds AV1 screen-content tools, alpha encoding, "AI-based frame interpolation, AI-based super resolution, and Battlemage platform" — [libvpl CHANGELOG](https://github.com/intel/libvpl/blob/main/CHANGELOG.md)
- Intel recommends FFmpeg/GStreamer "for cross-accelerator/cross-vendor development"; VPL for advanced Intel features — [Intel Media SDK → oneVPL upgrade guide](https://www.intel.com/content/www/us/en/docs/onevpl/upgrade-from-msdk/2023-1/overview.html)
- XeSS 3 SDK: XeSS-SR "on all GPUs with SM 6.4 (DP4a)"; XeSS-FG on Arc and non-Intel SM6.4 GPUs (max one generated frame on non-Intel); XeLL; XeSS-FG is DX12 with a proxy `IDXGISwapChain4`; `xefgSwapChainD3D12CreateContext` checks support; SR supports Vulkan 1.1 and DX11 (Arc only) — [intel/xess](https://github.com/intel/xess/); [XeSS-FG developer guide](https://github.com/intel/xess/blob/main/doc/xess_fg_developer_guide_english.md); [XeSS releases](https://github.com/intel/xess/releases)
- Intel support (as reported): HAGS supported on Arc B-series on Windows 11 22H2+, not on Arc A-series — [XDA, 2026-08-29](https://www.xda-developers.com/gpu-scheduling-toggle-pointless-2020-growing-list-windows-features-need/)
- Windows ML Intel EP: `OpenVINOExecutionProvider` — [Windows ML supported EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)

### Inferences
- Intel has no pro display-sync or warp products relevant here; treat Intel as "baseline tier + VPL decode".
- An Intel iGPU in a node can serve as an extra decoder (VVC, AV1) beside an NVIDIA/AMD render GPU, with cross-adapter copy cost.

### Gaps
- XeSS SDK licence text ("Other" on GitHub) not read; Arc Pro (B50/B60) display features not researched.

## 4. Cross-vendor Windows features the GPU exposes: D3D12 Video, Vulkan Video, Windows ML / DirectML, DirectStorage, HDR / 10-bit output, HAGS

### Takeaway
Windows now offers vendor-neutral paths for most media and AI needs: D3D12 Video decode/encode (AV1 encode since Windows 11 24H2) and Vulkan Video (H.264/H.265/AV1 decode+encode), Windows ML (GA Sept 2025; DirectML is in maintenance), DirectStorage 1.3/1.4-preview with GPU decompression (GDeflate; Zstd in 1.4 preview), and DXGI Advanced Color for HDR10/scRGB output. HAGS is increasingly a prerequisite (DLSS FG, hardware flip queue) but has had stability reports in capture/VR workloads.

### Cited Findings
#### D3D12 Video and Vulkan Video
- D3D12 video encoding exists since Windows 11 (H.264, HEVC); "Support for AV1 Encode is added in Windows 11, version 24H2 (WDDM 3.2)"; designed for interop between D3D12 video encode and D3D12 graphics/compute/ML — [Microsoft Learn: D3D12 video encoding](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/video-encoding-d3d12)
- Later D3D12 encode feature specs: HEVC 4:2:2/4:4:4, subregion notifications, readable reconstructed picture, stats metadata, QP map/dirty map/motion vectors, lower-resolution 2-pass, spatial adaptive QP — [DirectX-Specs: D3D12 Video Encoding](https://microsoft.github.io/DirectX-Specs/d3d/D3D12VideoEncoding.html)
- Agility SDK 1.716.0-preview (Jan 2025) new encode features; subregion notifications need Windows 11 24H2; AMD RX 7x00 listed — [DirectX blog: Agility 1.716](https://devblogs.microsoft.com/directx/agility-sdk-1-716-0-new-d3d12-video-encode-features/)
- Vulkan 1.3.302 (Nov 2024) `VK_KHR_video_encode_av1` + encode quantization map; Vulkan Video then covers decode and encode for H.264, H.265, AV1; NVIDIA and AMD drivers at launch, Intel later — [Khronos blog](https://www.khronos.org/blog/khronos-announces-vulkan-video-encode-av1-encode-quantization-map-extensions); AMD Adrenalin 24.20.25.01 adds `VK_KHR_video_encode_av1` — [AMD release notes](https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-24-20-25-01-EXPANDED-VLK-SUPPORT.html)

#### Windows ML / DirectML
- Windows ML GA 2025-09-23, in Windows App SDK 1.8.1+, Windows 11 24H2+; ONNX Runtime compatible; Windows distributes and updates ORT and EPs — [Windows blog: Windows ML GA](https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices/)
- Included EPs: CPU, DirectML (legacy); downloadable EPs: MIGraphX (AMD), NvTensorRtRtx (NVIDIA), OpenVINO (Intel), QNN (Qualcomm), VitisAI (AMD), WebGPU (experimental) — [Windows ML supported EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)
- "Support for CPU and GPU (via DirectML) is available on all supported Windows versions. Hardware-optimized execution providers ... require Windows 11 version 24H2" — [What is Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)
- Bring-your-own EPs for offline/managed machines: ~80 MB+ each, app validates ORT/EP combos — [Windows ML: bring your own EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/bring-your-own-eps)
- "DirectML is in sustained engineering ... new feature development has moved to Windows ML" — [Microsoft Learn: DirectML](https://learn.microsoft.com/en-us/windows/ai/directml/dml); "DirectML is in maintenance mode" — [microsoft/DirectML](https://github.com/microsoft/DirectML?tab=readme-ov-file)

#### DirectStorage
- 1.1: GPU decompression (GDeflate) "supported on all DirectX 12 + Shader Model 6.0 GPUs", vendor metacommands optional; GDeflate splits streams into 64 KiB tiles; staging buffers ~128 MiB to saturate IO; `DisableGpuDecompression` switch — [DirectX blog: DirectStorage 1.1](https://devblogs.microsoft.com/directx/directstorage-1-1-now-available/)
- 1.3 (2025-07-01): `EnqueueRequests`; corrupted GDeflate on GPU path "the driver will TDR" — [DirectX blog: DirectStorage 1.3](https://devblogs.microsoft.com/directx/directstorage-1-3-is-now-available/)
- 1.4 public preview (2026-03-11): Zstandard CPU+GPU decompression, open-source Zstd GPU shader (optimised for ≤256 KB chunks), Game Asset Conditioning Library, D3D12 CreatorID; BC1/3/4/5 shuffle post-processing; NuGet 1.4.0-preview1-2603.504; known issue: staging >256 MB failures, Zstd GPU fallback may TDR — [DirectX blog: DirectStorage 1.4](https://devblogs.microsoft.com/directx/directstorage-1-4-release-adds-support-for-zstandard/); [NuGet 1.4.0-preview1](https://www.nuget.org/packages/Microsoft.Direct3D.DirectStorage/1.4.0-preview1-2603.504)
- `IDStorageQueue2::GetCompressionSupport` reports which path the runtime will take — [NuGet changelog](https://www.nuget.org/packages/Microsoft.Direct3D.DirectStorage/1.4.0-preview1-2603.504)

#### HDR and 10-bit output
- Advanced Color: DWM composes in FP16 scRGB, lifting the old 8-bit windowed limit; swap chain must be flip model; Option 1 FP16 + scRGB (recommended, works for all APIs); Option 2 `R10G10B10A2_UNORM` + `SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)` for HDR10 video/fullscreen; SDR white level from `AdvancedColorInfo.SdrWhiteLevelInNits` (nominal 80 nits) — [Microsoft Learn: DirectX with Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
- Auto HDR applies to DirectX 11/12 games only — [Microsoft: Auto HDR](https://www.microsoft.com/en-us/windows/tips/auto-hdr)
- Windows has a GPU-accelerated display color pipeline (matrix + 1D LUT) programmable via MHC ICC profiles since Windows 10 2004 — [Microsoft Learn: MHC calibration pipeline](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc)
- GeForce 30-bit color in OpenGL since NVIDIA Studio driver 431.70 (2019): "support for 30-bit color across all product lines, including GeForce and TITAN" — [NVIDIA Studio driver news](https://www.nvidia.com/en-us/geforce/news/gfecnt/nsd-supercharge-your-favorite-creative-apps/); [NVIDIA KB 4847](https://nvidia.custhelp.com/app/answers/detail/a_id/4847/related/1/session/L2F2LzEvdGltZS8xNjU2OTY2ODEyL2dlbi8xNjU2OTY2ODEyL3NpZC9mVXlpT0w3V3oyWVB5YXNseThlYWxJZEhPWHo5bWs3R1hVcVpyN2o1ZVk5bzBfeDNCNm03NjVoMEVQWWdzZUpaJTdFaUI4YXdEc1gwNkZQclQlN0U5Yk50VTNoYzklN0Vsa3NKelFMbXBxWjltZkVwV2NabXdMV0RtOXNaN2clMjElMjE=)
- Radeon PRO W7800/W7900: 10-bit and 12-bit display colour output — [W7800 product page](https://www.amd.com/en/products/graphics/workstations/radeon-pro/w7800.html)

#### HAGS (hardware-accelerated GPU scheduling)
- Introduced Windows 10 May 2020 (WDDM 2.7), opt-in, OEMs encouraged to enable — [DirectX blog: HAGS](https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/)
- Required for DLSS Frame Generation (Streamline) — [Streamline DLSS-G (DeepWiki)](https://deepwiki.com/NVIDIA-RTX/Streamline/5.3-dlss-frame-generation-integration); Microsoft's basic hardware flip queue requires GPU hardware scheduling; advanced flip queue needs HAGS stage 2 (WDDM 3.2) — [XDA, 2026-08-29](https://www.xda-developers.com/gpu-scheduling-toggle-pointless-2020-growing-list-windows-features-need/)
- Puget: "little to no impact" in content-creation apps; V-Ray −5%, Blender +5%, After Effects up to +10%; reports of instability with high-VRAM apps; AMD drivers did not support it at the time of their test — [Puget Systems](https://www.pugetsystems.com/labs/articles/impact-of-hardware-accelerated-gpu-scheduling-on-content-creation-performance/)
- OBS recommends disabling HAGS as a troubleshooting step (capture issues, audio delays, DPC latency) — [Acer blog summary](https://blog.acer.com/en/discussion/4557/should-you-enable-hardware-accelerated-gpu-scheduling-in-windows-11) (secondary)

### Inferences
- For HAP/image-sequence streaming, DirectStorage helps only if PlayMap controls the on-disk format (GDeflate/Zstd-compressed chunks into D3D12 buffers/textures); HAP's own Snappy/LZ-style chunking would still be decompressed by PlayMap's code. Corrupt data → TDR on the GPU path argues for CPU validation or checksums on show media.
- DirectStorage, D3D12 Video and Windows ML all bind to D3D12; a Vulkan-primary renderer would need D3D12↔Vulkan shared-handle interop to use them (Vulkan Video is the Vulkan-native media alternative). OpenGL 4.6 has no native path to any of them.
- HDR output: projectors are mostly SDR; for LED/HDR targets use FP16 scRGB swap chains; the shader-side colour pipeline (and MHC/ICC only for monitors) is the portable path.
- HAGS should be detected and reported in STATE, not forced; recommend "on" for NVIDIA FG/flip-queue benefits but expose it as a preflight warning variable because of capture/latency reports.

### Gaps
- FFmpeg's d3d12va decode/encode coverage and quality versus vendor SDKs was not researched here.
- No primary source on how D3D12 Video encode of 4:2:2/4:4:4 maps per vendor/hardware.
- HAGS support status on AMD RDNA3/RDNA4 in 2026 not verified.

## 5. Detection and fallback: detecting vendor, card class, driver and feature support at run time; keeping the app correct without a feature

### Takeaway
Every vendor feature found exposes an explicit run-time probe (NVAPI returns `NVAPI_NOT_SUPPORTED`, `NvAPI_GPU_QueryWorkstationFeatureSupport(PROVIZ)`, `NvAPI_D3D12_QueryPresentBarrierSupport`, `NvEncodeAPIGetMaxSupportedVersion`, AGS/ADLX init and driver version, Anti-Lag 2 `Initialize` → S_OK, FSR auto-fallback to 3.1.5, XeSS-FG context creation, DirectStorage `GetCompressionSupport`, Windows ML EP catalog). The clean pattern is: enumerate DXGI adapters (LUID), bind each vendor library to the adapter by LUID, probe capabilities into an immutable capability record, and route each optional feature through a seam whose baseline implementation is PlayMap's own cross-vendor path.

### Cited Findings
- NVAPI pro probing: `NvAPI_GPU_QueryWorkstationFeatureSupport(physicalGpu, NV_GPU_WORKSTATION_FEATURE_TYPE_PROVIZ)` (recommended over the deprecated feature mask with WARPING/PIXINTENSITY/SWAPGROUP/BPC10 bits); `NvAPI_GPU_GetGPUType`; `NvAPI_SYS_GetDriverAndBranchVersion` — [NVAPI GPU group](https://docs.nvidia.com/nvapi/group__gpu.html); [NVAPI globals](https://docs.nvidia.com/nvapi/globals_func_n.html)
- Unsupported warp/blend returns `NVAPI_NOT_SUPPORTED` (GeForce RTX 4070 Ti, Quadro K2000) — [NVIDIA forum 249199](https://forums.developer.nvidia.com/t/rtx-warp-and-blend-api/249199); [NVIDIA forum 293511](https://forums.developer.nvidia.com/t/does-warp-and-blend-api-support-quadro-k2000/293511)
- Present barrier: query support per D3D12 device; may sit in a "pending" state (not synchronized) until fullscreen conditions hold; frame statistics available via the client handle — [NVIDIA blog: present barrier](https://developer.nvidia.com/blog/synchronizing-present-calls-between-applications-on-distributed-systems-with-directx-12/)
- NVENC: `NvEncodeAPIGetMaxSupportedVersion` for driver capability — [NVENC Programming Guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/)
- AGS: GPU info incl. vendor/device id, ASIC family, driver version, RDNA4 detection; DX12 extensions require device creation via `agsDriverExtensionsDX12_CreateDevice` — [GPUOpen: AGS](https://gpuopen.com/amd-gpu-services-ags-library/); [AGS README](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/blob/master/README.md)
- ADLX DLL installed with the driver, forward/backward compatible — [ADLX Programming Guide](https://gpuopen.com/manuals/adlx/programming-with-adlx/adlx-programming-guide/); [ADLX GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)
- AMF features are tied to minimum Adrenalin versions (e.g. 1.5.3 → 26.9.2) — [GPUOpen: AMF](https://gpuopen.com/advanced-media-framework/)
- Anti-Lag 2 `Initialize` returns S_OK only if present — [AntiLag2-SDK](https://github.com/GPUOpen-LibrariesAndSDKs/AntiLag2-SDK); FSR API auto-selects FSR 3.1.5 when FSR 4 hardware absent — [GPUOpen: FidelityFX SDK 2.0](https://gpuopen.com/learn/amd-fidelityfx-sdk-2-0/); XeSS-FG `xefgSwapChainD3D12CreateContext` fails on unsupported devices — [XeSS-FG guide](https://github.com/intel/xess/blob/main/doc/xess_fg_developer_guide_english.md)
- Aftermath must be enabled before any D3D12/Vulkan device is created — [Aftermath SDK Guide](https://docs.nvidia.com/nsight-aftermath/SDK/index.html)
- Cross-API device identity: match by UUID/LUID, not ordinal — [The CUDA Handbook §3.11](https://cudahandbook.com/book/ch3/external-memory-and-graphics)
- Windows ML acquires EPs dynamically; CPU and DirectML always available as fallback — [What is Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview)
- DirectStorage reports its decompression path via `GetCompressionSupport`; GPU decompression can be disabled globally — [NuGet DirectStorage](https://www.nuget.org/packages/Microsoft.Direct3D.DirectStorage/1.4.0-preview1-2603.504); [DirectStorage 1.1 blog](https://devblogs.microsoft.com/directx/directstorage-1-1-now-available/)
- Industry practice: WATCHOUT 7 recommends RTX PRO and states which features (genlock, EDID, GPUDirect) degrade on GeForce/AMD — [WATCHOUT 7 system requirements](https://docs.dataton.com/guide/watchout/getting-started/system-requirements.html); disguise: Mosaic and genlock only on NVIDIA RTX/Quadro pro — [disguise X Series specs](https://help.disguise.one/disguise-x-series/disguise-x-series-specifications)

### Inferences
- Proposed layering (three tiers), with the baseline always correct on its own:
  - **Tier 0 — baseline (any D3D12/Vulkan GPU):** PlayMap shader warp/blend/masks/colour; FFmpeg software or D3D12 Video / Vulkan Video decode; time-based software sync (frame = f(leader time)); Windows ML CPU/DirectML; FP16 scRGB output; DXGI/Vulkan enumeration.
  - **Tier 1 — vendor consumer features (probe, then enable):** NVDEC/NVENC (Video Codec SDK or FFmpeg nvdec/cuda), AMF, Intel VPL; RTX Video VSR/TrueHDR; Optical Flow FRUC; TensorRT-RTX / MIGraphX / OpenVINO EPs; Aftermath (NVIDIA) for field crash dumps; ADLX/NVAPI DRS to force display settings (FreeSync/G-SYNC off, 10-bit, full-range RGB); CUDA interop.
  - **Tier 2 — pro-only features (probe, never assume):** NVAPI scanout warp/blend; Mosaic; RTX PRO Sync framelock/genlock + present barrier/swap group; EDID management; GPUDirect for Video; Rivermax.
- Detection sequence: (1) `IDXGIFactory6::EnumAdapterByGpuPreference` → adapter LUID, PCI vendor id (NVIDIA 0x10DE, AMD 0x1002, Intel 0x8086 — standard PCI-SIG ids, not sourced above), driver UMD version; (2) per vendor, load the vendor library lazily (nvapi64.dll is in the driver; AGS is an app-shipped DLL/static lib; ADLX DLL from the driver; AMF runtime from the driver — the last is inferred from AMF's driver-version requirements); (3) map vendor handles to the DXGI LUID; (4) probe each feature with its own API and record {supported, reason, driver version} in an immutable capability snapshot that STATE and the context publish; (5) verbs that would use an unsupported feature are refused with the probe's reason (matches the "a verb that would change nothing is refused with the reason" doctrine); (6) where a feature can be set but silently not applied (present barrier pending; scanout warp on GeForce drivers that return OK but do nothing, per the Linux report), read back observed state (`GetScanoutWarpingState`, present-barrier frame stats, Sync status) before reporting "applied".
- Device/swap-chain creation is the critical seam: AGS DX12 extensions replace `D3D12CreateDevice`, Aftermath must hook before device creation, present barrier/Anti-Lag 2/XeSS-FG/DLSS-G bind to the device or wrap the swap chain. The renderer's device factory and swap-chain factory should be explicit seams with vendor decorators, chosen once at start-up from the capability snapshot.
- Safe on consumer cards: NVENC/NVDEC (mind 12-session encode cap), AMF, VPL, RTX Video SDK, VFX SDK, Optical Flow, Aftermath, CUDA interop, OptiX, Windows ML EPs, AGS, ADLX display settings, FSR/XeSS, 10-bit output (GeForce Studio/30-bit), HDR via DXGI. Not on consumer cards: scanout warp/blend, Mosaic, framelock/genlock, present barrier, EDID management, GPUDirect for Video.
- Keep vendor libraries out of Core (the house module rules): each vendor path is an edge module referencing Core and its own native library; P/Invoke surfaces are small C APIs (NVAPI uses `nvapi_QueryInterface` function-id lookup; AMF/ADLX are COM-like C++ interfaces needing C shims or vtable interop from .NET).

### Gaps
- No source consulted on .NET-specific bindings (e.g. maintained C# wrappers for NVAPI/ADLX/AMF); ADLX advertises C# bindings but their packaging was not checked.
- DXGI/Vulkan vendor-id and LUID APIs were not separately sourced in this pass (standard, but uncited here).

## 6. Licensing and redistribution: NVAPI, Video Codec SDK, RTX Video SDK, AGS, ADLX, AMF, FidelityFX, DirectStorage and others

### Takeaway
Nothing found blocks a closed-source commercial Windows app: NVAPI, AGS, AMF, Intel VPL and FSR source are MIT; NVIDIA's Video Codec SDK, RTX SDKs (RTX Video/DLSS/Reflex), OptiX, VFX SDK and Aftermath are proprietary but allow binary redistribution inside your product. The friction points are (a) NVIDIA notification-before-release clauses (DLSS/Reflex/NGX; OptiX/DesignWorks), (b) "NVIDIA GPUs only" and cloud-use restrictions in the RTX SDKs, (c) "no open-source contamination" clauses in NVIDIA/AMD proprietary terms — which matter if PlayMap itself is open-source (the proprietary SDKs then must stay optional, dynamically loaded plug-ins), (d) codec patent royalties are always the integrator's responsibility, and (e) Rivermax needs a paid production licence.

### Cited Findings
- NVAPI headers + nvapi.lib/nvapi64.lib: MIT — [NVIDIA/nvapi License.txt](https://github.com/NVIDIA/nvapi/blob/main/License.txt)
- Video Codec SDK licence: royalty-free right to "distribute the Software and derivative works thereof (in object code only) as integrated in Licensee software products solely for use with supported NVIDIA GPU hardware"; distribution chain must be bound by terms "no less restrictive"; licensee must obtain third-party codec licences (MPEG-LA etc.); grant-back of modifications — [NVIDIA Video Codec SDK License Agreement](https://developer.nvidia.com/nvidia-video-codec-sdk-license-agreement)
- The NVENC header (`nvEncodeAPI.h` in nv-codec-headers) carries an MIT-style notice "applies to this header file only" (Copyright 2010-2026 NVIDIA) — [nv-codec-headers nvEncodeAPI.h](https://github.com/FFmpeg/nv-codec-headers/blob/master/include/ffnvcodec/nvEncodeAPI.h)
- RTX SDKs licence (RTX Video SDK, DLSS, Reflex, NGX...): NVIDIA-GPU interoperability requirement; cloud use only on NVIDIA vGPU; notification before commercial release for DLSS/Reflex/NGX/Dynamic Vibrance; OTA updates; trademark use needs approval; RTX Video SDK licence questions to nvidia-rtx-video-sdk-license-questions@nvidia.com — [NVIDIA RTX SDKs License](https://github.com/NVIDIA-RTX/RTXCR/blob/main/License.txt); older (Jan 2023) version — [NVIDIA RTX SDKs License PDF](https://developer.download.nvidia.com/gameworks/NVIDIA-RTX-SDKs-License-23Jan2023.pdf)
- VFX SDK features: NVIDIA Software License Agreement + Product-Specific Terms for AI Products; models under NVIDIA Open Model License; commercial use allowed — [NGC TrueHDR](https://catalog.ngc.nvidia.com/orgs/nvidia/maxine/collections/nvvfxtruehdr/-)
- OptiX: free commercial use; binary-only redistribution; notify NVIDIA before commercial use; no open-source-licence contamination; no hosted-service use — [optix-dev LICENSE](https://github.com/NVIDIA/optix-dev/blob/v9.0.0/LICENSE.txt); [OptiX downloads](https://developer.nvidia.com/designworks/optix/download)
- Aftermath: lib DLLs distributable under the SDK LICENSE — [Aftermath SDK Guide](https://docs.nvidia.com/nsight-aftermath/SDK/index.html)
- Optical Flow FRUC: licence terms led an FFmpeg developer to conclude binaries with the filter can't be redistributed (community interpretation) — [mpv issue #11060](https://github.com/mpv-player/mpv/issues/11060)
- Rivermax: closed source, licensed; dev licence free (3 months, renewable) — [Rivermax FAQ](https://developer.nvidia.com/networking/rivermax/faq); [Holoscan for Media: Rivermax](https://docs.nvidia.com/holoscan-for-media/latest/user-guide/getting-help/rivermax-st-2110.html)
- Typical NVIDIA SDK licence (example nvCOMP): "licensed ... only for use in systems with NVIDIA GPUs"; "may not use the SDK in any manner that would cause it to become subject to an open source software license" — [NVIDIA SDK licence (nvCOMP)](https://github.com/NVIDIA/nvcomp/blob/main/LICENSE)
- AGS: MIT — [AGS LICENSE.txt](https://github.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/blob/master/LICENSE.txt)
- AMF: MIT, plus "AMD does not provide a license ... relating to ... codec technologies", and bundled FFmpeg libraries under LGPLv2.1 — [AMF LICENSE.txt](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/LICENSE.txt)
- ADLX: separate "ADLX SDK License Agreement.pdf" — [ADLX GitHub](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)
- FidelityFX/FSR: FSR 1–4.0.2 source published under MIT; SDK 2.x ships pre-built signed DLLs — [FSR 4.0.2 reference mirror (MIT text)](https://github.com/Rolaand-Jayz/FSR-4.0.2-reference); [FidelityFX SDK v2.0.0 release](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.0.0)
- AMD driver EULA forbids redistributing driver components (relevant to never bundling driver DLLs such as ADLX/AMF runtimes) — [AMD Software EULA](https://www.amd.com/en/resources/support-articles/amd-software-eula.html)
- Intel VPL dispatcher and GPU runtime: MIT — [intel/libvpl](https://github.com/intel/libvpl/); [intel/vpl-gpu-rt](https://github.com/intel/vpl-gpu-rt?tab=readme-ov-file); Intel tools licence: codec licences are the integrator's responsibility — [Intel software tools licence overview](https://cdrdv2-public.intel.com/777665/intel-software-development-tools-license%20-overview-august-2024.pdf)
- DirectStorage ships as a NuGet package (Microsoft.Direct3D.DirectStorage) — [DirectStorage SDK downloads](https://devblogs.microsoft.com/directx/directstorage-api-downloads/); GDeflate reference code promised under Apache 2.0 — [DirectStorage 1.1 blog](https://devblogs.microsoft.com/directx/directstorage-1-1-now-available/)
- Windows ML EPs are distributed/updated by Windows (no bundling needed) or bundled by the app ("bring your own EPs"), with EP licence terms in Windows ML third-party notices — [Windows ML supported EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers); [Bring your own EPs](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/bring-your-own-eps)

### Inferences
- Licence summary:

| SDK | Licence | Redistribute what | Conflicts for closed app | Conflicts for open-source app |
|---|---|---|---|---|
| NVAPI | MIT | static lib; runtime in driver | none | none |
| Video Codec SDK | NVIDIA proprietary; headers MIT | runtime in driver; header MIT | NVIDIA-GPU-only use; codec patents | use MIT nv-codec-headers only (as FFmpeg does) |
| RTX Video SDK / DLSS / Reflex | NVIDIA RTX SDKs licence | SDK DLLs | notify (DLSS/Reflex), cloud limits, NVIDIA-only | proprietary binaries must be optional plug-ins |
| VFX SDK (VSR/TrueHDR/green screen) | NVIDIA SLA + AI terms + Open Model License | runtime + models (via NGC) | no datacenter/cloud support | as above |
| OptiX | NVIDIA SDK licence | compiled into binary | notify before commercial use | no-OSS-contamination clause |
| Aftermath | NVIDIA SDK licence | lib DLLs | none significant | optional plug-in |
| Rivermax | commercial | runtime + paid licence per deployment | cost | cost |
| AGS | MIT | DLL/static lib | none | none |
| ADLX | AMD ADLX licence (not read) | DLL is in driver | check PDF | check PDF |
| AMF | MIT (+LGPL FFmpeg bits in samples) | headers; runtime in driver | codec patents | none |
| FSR / FidelityFX | MIT source; signed DLLs in 2.x | loader/upscaler DLLs | none found | none found |
| Anti-Lag 2 | not verified | header-only integration | — | — |
| Intel VPL / XeSS | MIT / Intel "Other" | dispatcher; libxess.dll | XeSS terms not read | XeSS terms not read |
| DirectStorage | Microsoft NuGet redistributable | dstorage DLLs | not read in detail | — |

- If PlayMap's source is open, the NVIDIA proprietary SDKs (Video Codec SDK headers aside, RTX Video, VFX, OptiX, Aftermath) should live in separately-distributed, dynamically loaded edge modules so the open core never "becomes subject to" or embeds them.

### Gaps
- Not read: ADLX SDK License Agreement.pdf, Anti-Lag 2 licence, XeSS licence text, DirectStorage NuGet licence text, Nsight Aftermath LICENSE file, RTX Video SDK-specific supplement beyond the RTX SDKs licence.
- Whether MPEG-LA/Access Advance royalties apply to an app that only calls OS/driver hardware decoders was not researched (all vendors disclaim it).
