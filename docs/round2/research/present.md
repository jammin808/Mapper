# Smooth output on Windows: D3D11 flip-model presentation, frame statistics, framelock and genlock

*Research for PlayMap's next professional round. Written 13 October 2026 against `main` at `be9b24c` (`/home/user/mapper`; the `wp-stage` and `wp-host` branches are already merged into it, `wp-ops` is at `186375e`).*

**How to read the sources.** Every fact carries a source and a mark:

- **V** means verified. I read the primary text myself: a page fetched server-side, a raw file or header pulled from the vendor's own repository, or the page's own text returned by search.
- **V-local** means read in this machine's PlayMap, Patterns or FFmpeg 9 tree.
- **U** means unverified. It comes from a secondary source, or I could not open the primary.
- **E** means estimate: our own arithmetic or engineering judgement.

Dates are the page's own date where it shows one, otherwise "n.d.". Everything was accessed on 13 Oct 2026. Vendor capability claims are the vendors' own. Nothing here was run on Windows, a GPU, a sync card or a projector. The build machine has none of them.

---

## 0. The short answer

1. **Smoothness on Windows comes from the presentation layer, not the drawing API.** Microsoft's documented way to get reliable vblank timing, one frame of latency and composition bypass is a **DXGI flip-model swap chain** (`DXGI_SWAP_EFFECT_FLIP_DISCARD`), created **waitable** (`DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`, `SetMaximumFrameLatency(1)`). It runs in a **borderless window that exactly covers the monitor**, and it reads **`IDXGISwapChain::GetFrameStatistics`** as vblank truth (§2). PlayMap's GL path has none of these today (§1).
2. **Recommended Windows architecture (§10):**
   - **Present.** PlayMap owns one `ID3D11Device` per adapter and one flip-model waitable swap chain per output. Presentation, frame statistics, NVAPI framelock and the FFmpeg D3D11VA decoder all hang off that device.
   - **Draw, first step.** The existing GLSL renderer runs through **ANGLE on that same device**, in the GLES 3.0 dialect of today's 24 programs, and draws into the swap-chain back buffers wrapped with `EGL_ANGLE_d3d_texture_client_buffer`. This is the pattern Avalonia's `LowLatencyDxgiSwapChain` already ships, and Patterns uses it. It brings zero-copy NV12/P010 planes via `EGL_D3D11_TEXTURE_PLANE_ANGLE`. ANGLE's D3D11 format tables include BC1–BC7 and BC6H.
   - **Draw, later only if measured.** A **native D3D11 device behind the same seam**, for the output stage and video planes, if lab measurements show ANGLE's cost, a missing format or a vendor bug.
   - **No interop on the present path.** `WGL_NV_DX_interop2` stays off it (driver variance, lock cost, no NV12). Keep it only as an island for desktop-GL FFGL plugins.
3. **Framelock design (§11).** Three tiers, all behind one state machine. The rule is "never stall the air".
   - **Tier 1, NVIDIA RTX PRO Sync.** NVAPI `NvAPI_D3D1x_JoinSwapGroup` + `NvAPI_D3D1x_BindSwapBarrier` + `NvAPI_D3D1x_Present`, joined **non-blocking**, with board status from `NvAPI_GSync_*`. Under lock, content frames are picked from the **shared hardware frame counter** with explicit 24-bit wrap handling.
   - **Tier 2, Intel.** IGCL `ctlGetSetDisplayGenlock` on Windows, or Intel's MIT SW Genlock DPLL steering on Linux.
   - **Tier 3, no hardware lock.** A measured software tier: every output's refresh in ppm and vblank phase in leader time, from frame statistics and `D3DKMTGetScanLine`, exchanged on UDP 9942.
   - **On any stall,** a node drops out of the barrier and keeps playing free-running, with an alarm. Watchout 7 does the opposite: a node with invalid sync "stalls on the frame barrier and stops presenting frames" (§7).
4. **Honest ceilings without sync cards (§8).** Software can make every node pick the same frame for the same show time, which PlayMap already does. It cannot align scan-out phase on Windows. Two free-running outputs 50 ppm apart slip one whole frame every **5 min 33 s** at 60 Hz. A 59.94 Hz output beside a 60 Hz output slips one every **16.7 s**. PlayMap should measure and publish these numbers, and refuse mixed refresh rates in a sync group.

---

## 1. Where PlayMap is today (V-local)

| Fact | Where |
|---|---|
| The renderer is OpenGL 3.3 core through Silk.NET.OpenGL / Silk.NET.Windowing **2.23.0**, plus SkiaSharp 3.119.4 | `src/PlayMap.Render/PlayMap.Render.csproj` |
| One hidden primary GL context does every pass. Each output window has a **shared** context that only draws one textured triangle and swaps | `src/PlayMap.Render/Platform/OutputWindow.cs`, `RenderContext.cs` |
| Pacing: "when one thread drives several windows only one of them should wait (the pacing window, `SetSwapInterval` 1) and the others swap at once (0)". The engine re-picks the pacer when windows open or close | `OutputWindow.cs` remarks; `src/PlayMap.Engine/Rendering/OutputWindows.cs` `RepickPacer()` |
| Vblank prediction is inferred from **the instant `SwapBuffers` returned** (`LastSwapNs`). `FramePacer` takes the median of intervals within ±50 % of nominal. A dropped frame is counted as `slots - 1` | `src/PlayMap.Render/Timing/FramePacer.cs` |
| The frame is drawn for leader time = `LeaderNowNs` + time to the pacing window's next vblank + `DisplayLatencyNs` | `src/PlayMap.Engine/Rendering/RenderHost.cs` remarks |
| `MonoClock` is `Stopwatch` in nanoseconds, which is **QPC on Windows**, so DXGI's `SyncQPCTime` maps onto it with one multiply | `src/PlayMap.Core/Time/Clocks.cs` |
| Fine clock sync on UDP 9942 uses a fixed 40-byte little-endian packet: magic `PMCK`, version 1, kind (1 request, 2 reply), two zero bytes, `u32 seq`, four zero bytes, `i64 t1`, `i64 t2`, `i64 t3`. Anything else is refused | `src/PlayMap.Net/Sync/ClockSync.cs` |
| The GPU seam is a concrete facade, `GlDevice`, with about 45 methods: `CreateTexture`, `CreateRenderTarget`, `Program(name)`, uniform-by-name, `DrawFullscreen` and so on. Only `GlDevice*.cs` and `Platform/*` reference Silk.NET.OpenGL. 13 files reference `GlDevice` | `src/PlayMap.Render/Gpu/GlDevice*.cs` |
| Shaders: **24 programs, about 835 lines of GLSL `#version 330 core`**, all named in `ShaderLibrary.Names`. The library comment already says "A Direct3D backend supplies the same names in HLSL with the same uniforms" | `src/PlayMap.Render/Shaders/ShaderLibrary.cs` |
| Texture formats in use: `Rgba8, R8, Rg8, R16, Rg16, R16F, Rg16F, Rgba16F, R32F, Rg32F`. BGRA uploads and BGRA `ReadPixels`. `TimeElapsed` queries. `PolygonMode` for wireframe | `src/PlayMap.Render/Gpu/GpuTypes.cs`, `GlDevice*.cs` |
| Hardware decode tries `AV_HWDEVICE_TYPE_D3D11VA` on Windows, with **copy-back** to system memory | `src/PlayMap.Media/Ffmpeg/MediaInput.cs`; docs/COMPETITIVE.md §2 |
| Roadmap already names R2.1 "D3D11 flip-model renderer… `GetFrameStatistics` as vblank truth, verified independent flip, one present thread per adapter", R2.2 zero-copy, R2.5 10-bit, R2.7 measured sync, R2.9 HARDENING, R2.10 frame-rate handling, R4.2 "NVAPI swap groups and barriers… counter-wrap handling" | `docs/ROADMAP.md` |
| **Patterns already ships a flip-model path on Windows** through Avalonia's `Win32CompositionMode.LowLatencyDxgiSwapChain` (ANGLE). A fuse file falls back to composition when a start fails | `/home/user/patterns-svc/src/Patterns.App/Program.cs`, `Services/DirectOutputService.cs` |

**What this means.** On Windows today, PlayMap's presentation goes through each vendor's WGL swap path. It learns the vblank only from a blocking swap returning, which is what a busy thread or a composed window distorts. It cannot see whether it is in independent flip. It has no present-count-to-vblank mapping, so it cannot detect a late frame at the display. It has no hook for NVIDIA swap groups except `WGL_NV_swap_group`. The device seam is clean enough to add a backend.

---

## 2. DXGI flip model: the facts that matter

### 2.1 Swap effects and requirements

- `DXGI_SWAP_EFFECT` values: `DISCARD = 0`, `SEQUENTIAL = 1` (both bitblt), `FLIP_SEQUENTIAL = 3` (Windows 8+), `FLIP_DISCARD = 4` (Windows 10+). D3D12 supports only the flip effects. "To display HDR swapchain buffer formats and color spaces, a flip model is required." [V, learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect, n.d.]
- Flip rules: `BufferCount` 2–16; no MSAA (`SampleDesc.Count = 1`); **one flip-model swap chain per HWND**; no GDI on that HWND. Flip model gives "full-screen present statistics in windowed mode". [V, learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model, n.d.]
- After `Present`, "the back buffer needs to explicitly be re-bound to the D3D11 immediate context". After `SetFullscreenState`, call `ResizeBuffers` before `Present`. [V, learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model, ms.date 2018-05-31, updated 2026-04-15]
- `FLIP_DISCARD` additionally lets DWM "reverse compose" (draw overlays into the app buffer) while staying in direct flip. [V, same page]

### 2.2 Direct flip, independent flip, MPO, and why exclusive fullscreen is not needed

- DirectFlip engages in three ways, from least to most capable [V, same page]:
  1. The buffers match the screen and the client area covers it.
  2. Panel fitters: the client covers the screen and the buffers are within a hardware-dependent factor of it, "for example, 0.25x to 4x".
  3. MPO: a hardware overlay plane for a sub-region.
- Once direct-flipped, "the DWM can go to sleep… Your application frames are sent directly to the screen, independently, with the same efficiency as fullscreen exclusive. This is 'Independent Flip'". Query support with `IDXGIOutput6::CheckHardwareCompositionSupport`. [V, same page]
- With a waitable object in independent flip, "you can get down to 1 frame of latency on recent versions of Windows". A bug that gave a minimum of 2 frames in the Anniversary Update was fixed in the Fall Creators Update. [V, same page]
- "Flip model presents go as far as making windowed mode effectively equivalent or better when compared to the classic 'fullscreen exclusive' mode… you may want to reconsider whether your application actually needs a fullscreen exclusive mode." [V, same page]
- Microsoft's Fullscreen Optimizations run FSE games "in a highly optimized borderless windowed format". Its "telemetry indicates that performance is, on average, as good or better than FSE". When an overlay appears, "the DWM reassumes control of the display". [V, devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/, 2019-12-17]
- Patterns' direct-output note records the same practical rule: a window from another app on top "puts it back through the compositor". [V-local, `Patterns.Core/Services/DirectOutput.cs`]

**Implication.** Use engine-owned borderless popup windows exactly covering each projector's monitor. Turn DWM transitions off. Pass `DXGI_MWA_NO_ALT_ENTER`. Never put anything on top. **Do not depend on exclusive fullscreen**, but keep it as a lab-tested option for NVIDIA swap groups (§6.2).

### 2.3 Waitable swap chains

- Create with `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`. This flag cannot be added or removed by `ResizeBuffers`. [V, learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains, n.d.]
- Set latency with **`IDXGISwapChain2::SetMaximumFrameLatency`**, not `IDXGIDevice1::SetMaximumFrameLatency`. The default for waitable swap chains is **1**. Use 2 only if CPU and GPU together exceed one refresh. [V, same page]
- Get the handle with `IDXGISwapChain2::GetFrameLatencyWaitableObject`. "Wait before rendering each frame… This includes the first frame". The sample uses `WaitForSingleObjectEx(handle, 1000, true)`. [V, same page]
- Godot's prototype got "1 frame of latency (actually less than 1)" only after it waited before the first frame. [V, github.com/godotengine/godot/pull/94503, 2024-07-18]

### 2.4 `Present1`, sync interval and tearing

- With flip model and `SyncInterval = 0`, Present "presents the next frame instead of any previously queued frames, it also terminates any remaining time left on the previously queued frames". [V, DXGI_SWAP_EFFECT page]
- `DXGI_PRESENT_RESTART` discards queued presents. `DXGI_PRESENT_DO_NOT_WAIT` returns `DXGI_ERROR_WAS_STILL_DRAWING` instead of blocking. [V, github.com/MicrosoftDocs/win32/blob/docs/desktop-src/direct3ddxgi/dxgi-present.md, n.d.]
- `DXGI_PRESENT_ALLOW_TEARING` (0x200) has these conditions [V, dxgi-present.md; learn.microsoft.com/en-us/windows/win32/direct3ddxgi/variable-refresh-rate-displays, updated 2025-04-15]:
  - The swap chain was created with `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`.
  - Sync interval is 0.
  - The app is not in FSE.
  - Support is checked with `IDXGIFactory5::CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING)`.
- **For projection, tearing and VRR are wrong by default.** PlayMap presents with sync interval 1, and preflight requires VRR/G-SYNC off. NVIDIA's sync guide also says "If you use a G-SYNC display, disable G-SYNC" before enabling Frame Lock (§6.1).

### 2.5 Frame statistics: vblank truth

- `DXGI_FRAME_STATISTICS { UINT PresentCount; UINT PresentRefreshCount; UINT SyncRefreshCount; LARGE_INTEGER SyncQPCTime; LARGE_INTEGER SyncGPUTime /* reserved, 0 */; }` [V, learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_frame_statistics, n.d.]
  - `PresentCount` counts images presented to the monitor, which is "not necessarily the same as the number of times that you called" Present.
  - `PresentRefreshCount` is the vblank count at which the last image was shown.
  - `SyncRefreshCount` and `SyncQPCTime` are the vblank count and QPC time "when the scheduler last sampled the machine time".
  - "For windowed mode", counts run "since the swap chain was created".
- `GetFrameStatistics` is valid for flip model, windowed or fullscreen. It returns `DXGI_ERROR_FRAME_STATISTICS_DISJOINT` on the first call and on mode changes. `PresentRefreshCount == SyncRefreshCount` "when the app presents on every vsync". `IDXGISwapChain::GetLastPresentCount` returns the present ID of the last successful Present. [V, dxgi-flip-model page]
- Microsoft's glitch algorithm [V, dxgi-flip-model page]:
  1. Store (present ID → expected `PresentRefreshCount`).
  2. Read the statistics.
  3. If the actual refresh is later than expected, a glitch happened.
  4. Skip that many frames with `SyncInterval = 0`, or `DXGI_PRESENT_RESTART` for large glitches.
- **Caveats** [V, learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics, n.d.]:
  - "Statistics are not reliable in many multiple monitor scenarios, as well as scenarios where other fullscreen apps are running."
  - "It may be necessary to call DwmFlush before frame statistics are updated on systems that support Hardware Flip Queue."
- `IDXGISwapChainMedia` (Windows 8.1+) [V, learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/ns-dxgi1_3-dxgi_frame_statistics_media; …/nf-dxgi1_3-idxgiswapchainmedia-setpresentduration, updated 2024-02-22; …/ne-dxgi1_3-dxgi_frame_presentation_mode, updated 2024-02-22]:
  - `GetFrameStatisticsMedia(DXGI_FRAME_STATISTICS_MEDIA*)` adds `DXGI_FRAME_PRESENTATION_MODE CompositionMode` (`COMPOSED = 0`, `OVERLAY = 1`, `NONE = 2`, `COMPOSITION_FAILURE = 3`) and `UINT ApprovedPresentDuration`.
  - `SetPresentDuration(UINT Duration)` takes **hundreds of nanoseconds** and requests a custom present duration (custom refresh). "Approval should be continuously verified on a frame-by-frame basis."
  - The docs describe these for media and decode swap chains. Whether `CompositionMode` reports reliably for an ordinary RGB flip swap chain is **U**: test it in the lab.
- **Arithmetic (E).** A `UINT PresentCount` at 60 Hz wraps after 2³²/60 s ≈ 828 days. Handle wrap with unsigned subtraction anyway.

### 2.6 Other clocks for the vblank (all per-output)

- `IDXGIOutput::WaitForVBlank()` halts the thread "until the next vertical blank occurs". [V, learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-waitforvblank, n.d.] Avalonia's low-latency DXGI path paces with it (§5.2).
- `DwmGetCompositionTimingInfo` returns `DWM_TIMING_INFO` with `rateRefresh`, `qpcRefreshPeriod`, `qpcVBlank` ("the query performance counter value before the vertical blank"), `cRefresh`, `cFramesDropped`, `cFramesMissed` and others. All counters are `ULONGLONG`. [V, learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_timing_info, n.d.] This reports the compositor's clock. It is useful when composed, and for a monitor-level sanity check.
- `D3DKMTGetScanLine(D3DKMT_GETSCANLINE*)` with `{ [in] D3DKMT_HANDLE hAdapter; [in] D3DDDI_VIDEO_PRESENT_SOURCE_ID VidPnSourceId; [out] BOOLEAN InVerticalBlank; [out] UINT ScanLine; }` comes from `Gdi32.dll` (Vista+). It returns `STATUS_DEVICE_REMOVED` if the adapter stopped. [V, learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/nf-d3dkmthk-d3dkmtgetscanline and ns-d3dkmthk-_d3dkmt_getscanline, 2024-02-22]
- `D3DKMTWaitForVerticalBlankEvent(const D3DKMT_WAITFORVERTICALBLANKEVENT*)` with `{ hAdapter; hDevice /* optional */; VidPnSourceId; }`. [V, …/nf-d3dkmthk-d3dkmtwaitforverticalblankevent and ns-…, 2024-02-22] The adapter handle and VidPn source id come from `D3DKMTOpenAdapterFromLuid` / `D3DKMTOpenAdapterFromGdiDisplayName`. [U, API names from memory, not opened]
- **Use (E).** Scanline position ÷ vertical total lines × period gives the sub-millisecond phase of the scan-out at a QPC instant. Vertical total and pixel rate come from `QueryDisplayConfig` → `DISPLAYCONFIG_VIDEO_SIGNAL_INFO` [U]. This is an independent cross-check of `SyncQPCTime` that works per output even where frame statistics are "not reliable in many multiple monitor scenarios".

### 2.7 Scheduling presents for a target time

- **Hardware flip queue** (Windows 11, WDDM 3.0) [V, learn.microsoft.com/en-us/windows-hardware/drivers/display/hardware-flip-queue, n.d.]:
  - Lets the OS queue several future flips in the display controller.
  - Each flip carries a `TargetFlipTime` in QPC units: "When the clock reaches this value, the frame can be sent to the display while honoring VSync and tearing flags."
  - "Windows currently requires GPU hardware scheduling to be enabled" for it on released drivers.
  - This is a driver feature. Applications reach it through the composition swapchain or media paths, not a DXGI flag.
- **Composition swapchain API** (`IPresentationFactory`, `IPresentationManager`) [V, learn.microsoft.com/en-us/windows/win32/comp_swapchain/comp-swapchain, ms.date 2021-09-10, updated 2025-03-25]:
  - Requires Windows 11 build **10.0.22000.194**+ and WDDM 2.0. Direct scanout and iflip need WDDM 3.0.
  - Surfaces must be allocated "displayable".
  - At most **31** presentation buffers per manager.
  - Each present can carry a **target time** (system interrupt time). Presents queue and become *ready* when GPU work is done and the target time is reached.
  - The statistics queue holds "on the order of 512-1024 statistics", about 5 s worth.
  - Content binds through a DirectComposition visual.
- **Assessment (E).** This is the only Windows API that lets PlayMap say "show this buffer at this instant", which suits exact 3:2 cadence and pre-queued presents. It is newer and less proven than DXGI swap chains, and NVAPI swap groups and present barriers are defined on `IDXGISwapChain`. So it is a **later experiment**, not the first path.

### 2.8 Several outputs, one device, threads

- `IDXGIDevice1::SetMaximumFrameLatency` is **per device**. With several swap chains per device, do not set it to 1 when more than one swap chain renders every frame. "Keep the number of queued present operations less than the number of back buffers in the swap chain with the least number of back buffers". With two swap chains presenting together, "create swap chains with at least three buffers". [V, github.com/MicrosoftDocs/win32/blob/docs/desktop-src/direct3ddxgi/dxgi-multiple-swap-chains.md, n.d.] Waitable swap chains carry their own per-swap-chain latency instead of the device's. [V, reduce-latency page]
- The immediate context is single-threaded. "Your application should also use that same thread for… DXGI operations, especially… `IDXGISwapChain::Present`". It is "invalid to use an immediate context concurrently with most of the DXGI interface functions". [V, github.com/MicrosoftDocs/win32/blob/docs/desktop-src/direct3d11/overviews-direct3d-11-render-multi-thread-intro.md, n.d.]
- DXGI sends window messages synchronously, so a render thread blocked while the message-pump thread waits on it **deadlocks**. "Never block the message pump." [V, learn.microsoft.com/en-us/windows/win32/direct3darticles/dxgi-best-practices, n.d.; learn.microsoft.com/en-us/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi, n.d.]

**Implications (E).**
- Run one render-and-present thread per adapter. It owns the immediate context and the output windows' message pump, which is already PlayMap's design.
- Each output's swap chain has 3 buffers, a waitable object and latency 1.
- The thread waits with `WaitForMultipleObjectsEx` on all output handles, with a timeout of a few refresh periods, and renders and presents whichever output is ready.

### 2.9 10-bit and HDR swap chains

- Advanced Color reached HDR displays in Windows 10 1709 and "specially provisioned SDR displays" in **Windows 11 22H2 (build 22621)**. A flip-model swap chain "makes your swap chain eligible for Advanced Color processing". [V, learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range, ms.date 2022-10-10, updated 2025-09-19]
- There are two combinations [V, same page]:
  - **Option 1:** `DXGI_FORMAT_R16G16B16A16_FLOAT` + `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709` (scRGB, the default for FP16). scRGB 1.0 is 80 nits.
  - **Option 2:** `DXGI_FORMAT_R10G10B10A2_UNORM` + `IDXGISwapChain3::SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)` (HDR10). This is only for HDR displays and needs no alpha blending.
- On an SDR desktop, DWM clips out-of-range values. `IDXGIOutput6` reports the "main" display, the one under the window centre. SDR reference white comes from `DISPLAYCONFIG_SDR_WHITE_LEVEL` / `QueryDisplayConfig`. [V, same page]
- `IDXGISwapChain3::CheckColorSpaceSupport` exists. G2084/P2020 and G10/P709 "will be displayed even if G22 P709 is in use, although out-of-gamut colors will be clipped". [V, learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiswapchain3-checkcolorspacesupport, n.d.]
- Content in R10G10B10A2_UNORM or R16G16B16A16_FLOAT "isn't clamped unless it's composed to an SDR desktop". [V, for-best-performance page]
- **For PlayMap (E).** Blend ramps need 10-bit output (ROADMAP R2.5): an R10G10B10A2 swap chain with G22/P709 on a 10-bit-capable chain, and FP16 for the internal linear pipeline. Read `IDXGIOutput6::GetDesc1().BitsPerColor` and publish it, because whether 10 bits reach the cable depends on the GPU output and EDID [U].

### 2.10 PresentMon: how to prove independent flip

- PresentMon console **v2.6.0** is the latest tag; the repo head is from 2026-10-08. Licence MIT (© 2017–2024 Intel). [V, github.com/GameTechDev/PresentMon, `LICENSE.txt`, tags]
- `PresentMode` values [V, `README-ConsoleApplication.md`]:
  - `Hardware: Legacy Flip`
  - `Hardware: Legacy Copy to front buffer`
  - `Hardware: Independent Flip`
  - `Composed: Flip`
  - `Hardware Composed: Independent Flip` (granted an overlay plane)
  - `Composed: Copy with GPU GDI`
  - `Composed: Copy with CPU GDI`
- Metrics [V, same file]: `MsBetweenPresents`, `MsInPresentAPI`, `MsBetweenDisplayChange`, `MsUntilDisplayed`, `DisplayLatency`, `DisplayedTime`, `MsAnimationError`, `AllowsTearing`, `FrameType`.
- Flags [V, same file]: `--process_id`, `--output_file`, `--timed`, `--terminate_after_timed`, `--stop_existing_session`, `--restart_as_admin`.
- DXGI does not tell an app which mode it got. The `CompositionMode` field in §2.5 is a hint at best. **Preflight should run a 10–30 s PresentMon capture of the engine's PID** and refuse to call an output "direct" unless every displayed frame was `Hardware: Independent Flip` or `Hardware Composed: Independent Flip` (E).
- Godot's maintainers used exactly this check: "A correct result should show **Hardware Composed: Independent Flip**." [V, github.com/godotengine/godot-proposals/issues/5692, n.d.]

---

## 3. .NET bindings in 2026

| Package | Latest | Published | Licence | Targets | Notes |
|---|---|---|---|---|---|
| Silk.NET.Direct3D11 / .DXGI / .Direct3D.Compilers / .Direct3D12 / .DirectComposition / .OpenGLES / .OpenGLES.Extensions.ANGLE / .SPIRV.Cross / .Shaderc | **2.23.0** | NuGet 2026-01-23; GitHub release "2.23 Winter 2025 Update" 2026-01-22 | MIT | netstandard2.0/2.1, netcoreapp3.1, net5.0 | Release notes: "Updated to Windows SDK 10.26100… Updated to latest ANGLE… The Silk.NET team are hard at work on 3.0, with 2.X updates continuing with the support of contributors and sponsors." A `v3.0.0-preview` tag dates from 2024-12-17, and `develop/3.0` is active (tracking issue 2026-06). [V, api.nuget.org; github.com/dotnet/Silk.NET/releases/tag/v2.23.0; gitlab.com/silkdotnet/silk.net/-/tags; github.com/dotnet/Silk.NET/issues/2586] |
| Silk.NET.EGL | 1.9.0 (stale) | — | MIT | — | Too old to use. ANGLE's EGL needs about 20 hand-written P/Invokes. [V, api.nuget.org] |
| Vortice.Direct3D11 / .DXGI / .D3DCompiler / .Dxc / .Direct3D12 / .DirectComposition | **3.8.3** | 2026-03-04 | MIT | net8.0, net9.0, net10.0 (+ `-windows10.0.22621` for DXGI) | Idiomatic COM wrappers (SharpGen), one main maintainer (Amer Koleci). [V, api.nuget.org registration] |
| TerraFX.Interop.Windows | **10.0.26100.6** | 2025-12-12 | MIT | net10.0 | Raw blittable Windows SDK bindings, no wrappers. [V, api.nuget.org] |
| Microsoft.Direct3D.DXC | 1.9.2609.5 | 2026-09-29 | (licence URL deprecated on NuGet; DXC is the LLVM/NCSA licence) | native | DXC emits DXIL (SM 6.x, D3D12) and SPIR-V. **It does not emit DXBC for D3D11.** Latest repo tags are `v1.10.2605.x` previews. [V, api.nuget.org; github.com/microsoft/DirectXShaderCompiler LICENSE.TXT; tags] |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | 2026-04-20 | (ANGLE: BSD-3) | native | Already in PlayMap's desk dependency graph through Avalonia 12.1.4. [V, api.nuget.org; github.com/google/angle LICENSE; V-local, `src/PlayMap.App/obj/project.assets.json`] |

**Shader toolchain options (2026).**

- **Slang** `v2026.19` is the latest tag. Licence `Apache-2.0 WITH LLVM-exception`. It emits HLSL, GLSL, SPIR-V and Metal from one source. [V, github.com/shader-slang/slang tags and LICENSE]
- **glslang** and **SPIRV-Cross** are at tag `vulkan-sdk-1.4.363.0`. SPIRV-Cross is Apache-2.0; glslang is BSD/MIT/Apache, with the bison-derived GPL-with-exception part removed ("Bison was removed long ago"). [V, github.com/KhronosGroup/{glslang,SPIRV-Cross} LICENSE files and tags]
- **FXC** (`d3dcompiler_47.dll`, `D3DCompile`) is the compiler that produces **DXBC SM 5.0 for D3D11**. It ships with Windows 8.1 and later [U, from memory], so runtime compilation on the node works. It does not run on PlayMap's Linux build machine.
- vkd3d-shader (Wine, LGPL) can reportedly compile HLSL to DXBC on Linux [U]. It would be a build-time tool only and never shipped.

**Fit for PlayMap (E).**

- PlayMap already pins Silk.NET 2.23.0. Adding `Silk.NET.Direct3D11` + `Silk.NET.DXGI` at the same version adds no new binding family, and keeps one `ComPtr<T>` style and one native-loading story.
- Vortice is the better-shaped API but adds a second interop layer. TerraFX is the leanest for the handful of `dxgi`/`dwmapi`/`gdi32` calls.
- **Choose Silk.NET 2.23.0 for D3D11/DXGI, plus hand-written `LibraryImport` for NVAPI, IGCL, D3DKMT, DWM and EGL.** Revisit only if Silk 3.0 ships with breaking ABI. Its migration proposal promises ABI compatibility for 2.x-compiled code. [V, github.com/dotnet/silk.net/blob/main/documentation/proposals/Proposal%20-%203.0%20Migration%20and%20Compatibility.md]
- **GLSL → HLSL**, if and when a native D3D11 device is built:
  - Keep GLSL 330 as the one source.
  - At build time: glslang → SPIR-V → SPIRV-Cross `--hlsl --shader-model 50` → HLSL. Emit a JSON reflection of uniform names to cbuffer offsets.
  - Compile DXBC on the Windows CI runner (ROADMAP R0.4). At run time, compile with `D3DCompile` into a hashed disk cache as a fallback.
  - The same runtime chain later translates ISF effects, which are GLSL.
  - Hand-written dual sources are tolerable for 24 programs, but they will drift. Slang is attractive but would replace the shader source language. Defer it.

---

## 4. GL drawing, DXGI presenting: `WGL_NV_DX_interop2`

**Spec facts** [V, raw.githubusercontent.com/KhronosGroup/OpenGL-Registry/main/extensions/NV/WGL_NV_DX_interop.txt and WGL_NV_DX_interop2.txt]
- `WGL_NV_DX_interop` (last modified 2010-10-11) defines `wglDXSetResourceShareHandleNV`, `wglDXOpenDeviceNV`, `wglDXRegisterObjectNV(hDevice, dxObject, name, type, access)`, `wglDXObjectAccessNV`, and `wglDXLockObjectsNV` / `wglDXUnlockObjectsNV`. Access values are `WGL_ACCESS_READ_ONLY_NV 0x0000`, `WGL_ACCESS_READ_WRITE_NV 0x0001` and `WGL_ACCESS_WRITE_DISCARD_NV 0x0002`.
- Accessing an object from GL while unlocked, or from D3D while GL holds the lock, is "undefined behavior and may result in data corruption or program termination". Lock failures are `ERROR_BUSY`, `ERROR_INVALID_DATA` and `ERROR_LOCK_FAILED`.
- `WGL_NV_DX_interop2` (rev 2, 2011-10-04, "Shipping with NVIDIA release 275 drivers, June 2011") adds D3D10/11 resources: textures 1D/2D/3D/cube/rect, renderbuffers, buffers. Resources must be `D3D11_USAGE_DEFAULT`, the `ID3D11Device` must be multithreaded ("Do not set `D3D11_CREATE_DEVICE_SINGLETHREADED`"), and the sample renders GL into a D3D11 swap chain back buffer.

**Vendor support and who uses it**
- Godot's proposal: "despite having NV in its name, this extension is also available on Intel and AMD drivers… It does work on my Intel integrated graphics driver." [V, github.com/godotengine/godot-proposals/issues/10242, 2024-07-18]
- mpv's `dxinterop` context is "(experimental) Win32, using WGL for rendering and Direct3D 9Ex for presentation. Works on Nvidia and AMD. Newer Intel chips with the latest drivers may also work". mpv's **`d3d11va` hwdec "requires `--gpu-context=d3d11` or `--gpu-context=angle`"**, not WGL. [V, mpv.net's copy of mpv option text; github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst]
- Spout's SDK is built around "GL/DX interop", with a compatibility test (`IsGLDXavailable`, `CreateInterop`, `LockInteropObject`). [V, raw.githubusercontent.com/leadedge/Spout2/master/SPOUTSDK/SpoutGL/SpoutGL.h; latest tag v2.005]

**Measured pitfalls in public prototypes**
- **Godot PR #94503 (RFC, 2024-07)** [V]:
  - The prototype reached "Hardware Composed: Independent Flip" and about 1 frame of latency.
  - A maintainer measured the direct-to-shared-buffer strategy dropping max FPS from **1846 to 660**. An intermediate buffer reached about **1200**, with "serious desktop responsiveness issues with V-Sync disabled". The "main culprit is `wglDXLockObjectsNV` and `wglDXUnlockObjectsNV`".
  - On Intel, "`wglDXUnlockObjectsNV` seems to wait for all the previous OpenGL commands to complete".
  - The PR is not merged.
- **nlguillemot/OpenGL-on-DXGI (n.d.).** On NVIDIA, `FLIP_DISCARD` "seems to only work for the swap chain's first buffer… resulting in flickering". The author suggests copying instead of wrapping swap-chain buffers. [V]
- **pmttavara/OpenGL-on-DXGI1.3 (n.d.).** GL blit → shared staging renderbuffer → D3D blit with Y-mirror is "2 copies of overhead". "There's no easy way to know when you're in Independent Flip vs. Flip". [V]

**A no-code alternative, NVIDIA only**
- The NVIDIA control panel setting "Vulkan/OpenGL present method" offers `Auto`, `Prefer Native` and `Prefer layered on DXGI Swapchain`. The DXGI-layered method "is compatible with features such as `VK_KHR_display` and Multiplane overlay… can add minor memory and performance overhead". The default is Auto. [V, community.khronos.org/t/vulkan-present-mode-preferred-native-vs-layered-on-dxgi-swapchain/112055, 2025-08-24]
- It arrived with driver 526.47. [V, forums.x-plane.org/…/276396…, 2022-10-28]
- NVAPI's DRS settings expose it as `OGL_CPL_PREFER_DXPRESENT_ID = 0x20D690F8`, with values `PREFER_DISABLED 0`, `PREFER_ENABLED 1` and `AUTO 2` (the default). An app can set it on its own profile with `NvAPI_DRS_CreateSession` / `LoadSettings` / `FindApplicationByName` / `CreateProfile` / `CreateApplication` / `SetSetting` / `SaveSettings`. [V, NVAPI R615 `NvApiDriverSettings.h`, `nvapi.h`]
- This gives a GL app flip-model presentation without frame statistics or a waitable object. **It is a useful emergency mitigation for today's GL path on NVIDIA nodes**, applied as a HARDENING item (ROADMAP R2.9) on approval.

**Verdict (E).**
- Interop keeps every GL line and gets a DXGI swap chain.
- Against it:
  - It puts two driver stacks (WGL ICD + D3D11) on the critical path of every frame.
  - Lock and unlock serialise GPU work, with vendor-dependent cost.
  - NV12 decode surfaces cannot be shared into GL. mpv refuses d3d11va with WGL, so zero-copy needs a D3D11 `VideoProcessorBlt` NV12→RGBA copy first.
  - Behaviour on AMD and Intel is anecdotal.
- Keep it **off the present path**. Keep it as an island where desktop GL is unavoidable: **FFGL plugins** are desktop-GL (the FFGL master "replaced glload by glew, enabling OpenGL 4.6 extensions to be used inside plugins"). [V, raw.githubusercontent.com/resolume/ffgl/master/README.md]

---

## 5. ANGLE on PlayMap's own D3D11 device

### 5.1 What ANGLE gives on D3D11

- Its D3D11 backend is **complete for OpenGL ES 2.0 and 3.0**. It has no ES 3.1 on D3D11. ES 3.1 and 3.2 are complete on Vulkan. ANGLE "is used as the default WebGL backend for both Google Chrome and Mozilla Firefox on Windows". Licence BSD-3. [V, raw.githubusercontent.com/google/angle/main/README.md and LICENSE]
- **ANGLE's own HWND window surfaces use the blt model** (`DXGI_SWAP_EFFECT_SEQUENTIAL`, `BufferCount = 1`; or `DISCARD`). It uses flip (`FLIP_SEQUENTIAL`, 2 buffers via `CreateSwapChainForComposition`) only with DirectComposition. [V, raw.githubusercontent.com/google/angle/main/src/libANGLE/renderer/d3d/d3d11/win32/NativeWindow11Win32.cpp] So **PlayMap must own the swap chain** and let ANGLE draw into its buffers.
- **`EGL_ANGLE_d3d_texture_client_buffer`** (v11, 2022-08-23) [V, raw.githubusercontent.com/google/angle/main/extensions/EGL_ANGLE_d3d_texture_client_buffer.txt]:
  - `EGL_D3D_TEXTURE_ANGLE 0x33A3` wraps an `ID3D11Texture2D` as a pbuffer or an EGLImage.
  - v4 (2019-04-15) added `DXGI_FORMAT_R10G10B10A2_UNORM`.
  - v7 (2021-01-12) added **NV12/P010/P016 planes via `EGL_D3D11_TEXTURE_PLANE_ANGLE`**.
  - v8 added texture-array slices via `EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE`.
- `EGL_ANGLE_device_creation` + `EGL_ANGLE_device_creation_d3d11` provide `eglCreateDeviceANGLE(EGL_D3D11_DEVICE_ANGLE, device, …)`, so ANGLE renders on **a device PlayMap created**. `EGL_ANGLE_device_d3d11` queries it back. [V, raw files in google/angle/extensions]
- `EGL_CHROMIUM_sync_control` (`eglGetSyncValuesCHROMIUM` → UST/MSC/SBC) and `EGL_ANGLE_sync_control_rate` exist. These are secondary once PlayMap reads DXGI statistics directly. [V, same directory]
- ANGLE's D3D11 caps code sets `readFormatBgraEXT`, `pixelBufferObjectNV`, `mapBufferRangeEXT`, **`disjointTimerQueryEXT`**, **`polygonModeANGLE`**, `floatBlendEXT`, `textureStorageEXT` and others to true. Its D3D11 format table maps `BC1…BC5`, **`BC6H_RGB_UFLOAT/SFLOAT`**, **`BC7_RGBA_UNORM(_SRGB)`**, `R16_UNORM`, `R16G16_UNORM` and `GL_RGBA16_EXT`. [V, …/d3d11/renderer11_utils.cpp; …/d3d11/texture_format_data.json; …/texture_format_map.json, main] Whether `GL_EXT_texture_compression_bptc`, `rgtc` and `norm16` are *exposed* at run time on a given device is **U**: check in the lab with `glGetString(GL_EXTENSIONS)`.

### 5.2 The same pattern in production in .NET

- Avalonia's `DxgiRenderTarget` [V, raw.githubusercontent.com/AvaloniaUI/Avalonia/master/src/Windows/Avalonia.Win32/DirectX/DxgiRenderTarget.cs and DxgiConnection.cs]:
  - Creates `DXGI_SWAP_EFFECT_FLIP_DISCARD`, `BufferCount 2`, `DXGI_FORMAT_B8G8R8A8_UNORM`, `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`, with `MakeWindowAssociation(NO_ALT_ENTER | NO_PRINT_SCREEN)`.
  - Wraps back buffer 0 for ANGLE with `WrapDirect3D11Texture`.
  - Calls `Present(0, 0)`, paced by `IDXGIOutput::WaitForVBlank()` on a separate connection thread.
- Patterns enables it per show with a crash fuse. [V-local]
- That design paces on the output's vblank wait, not a waitable object or frame statistics. **PlayMap should copy the wrapping and keep its own pacing** (§2.3, §2.5).

### 5.3 Cost of moving PlayMap's shaders to ES (E, from §1)

- Change the `#version 330 core` header to `#version 300 es` and add `precision highp float;` in fragment shaders. `gl_VertexID`, `texture()` and integer bit operations are ES 3.0.
- Wireframe `PolygonMode` → `GL_ANGLE_polygon_mode` (ANGLE source sets it).
- 16-bit UNORM textures (`R16`, `Rg16`) need `EXT_texture_norm16` (format table present, exposure U). With ANGLE, P010 video arrives as planes anyway.
- `R32F`/`Rg32F` sampling must be `NEAREST` unless `OES_texture_float_linear` is present.
- Rendering to `Rgba16F` needs `EXT_color_buffer_half_float` or `EXT_color_buffer_float` (U on ANGLE D3D11).
- `ReadPixels(BGRA)` is covered by `EXT_read_format_bgra`.
- Mesa's GLES on the Linux CI can run the ES variants [U for llvmpipe's exact ES version], so the shader suite stays testable here.

### 5.4 One device for decode, draw, present and framelock (E)

- FFmpeg's `AVD3D11VADeviceContext` carries `ID3D11Device *device`, `device_context`, and recursive `lock`/`unlock` callbacks "protect[ing] accesses to device_context". Device-level `BindFlags` and `MiscFlags` apply to every frames context. Upstream notes that `D3D11_BIND_SHADER_RESOURCE` "is removed for AV_PIX_FMT_YUV420P / DXGI_FORMAT_420_OPAQUE". Frames are `ID3D11Texture2D*` in `AVFrame.data[0]` plus an array index in `data[1]`. [V-local, `/opt/ffmpeg9/include/libavutil/hwcontext_d3d11va.h`; V, raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavutil/hwcontext_d3d11va.h]
- Give FFmpeg PlayMap's device with `BindFlags = D3D11_BIND_DECODER | D3D11_BIND_SHADER_RESOURCE`. Turn on `ID3D10Multithread::SetMultithreadProtected(TRUE)`. Supply lock callbacks bound to the same mutex the render thread takes around ANGLE and DXGI calls. Then import the NV12/P010 array slice into ANGLE through `EGL_D3D11_TEXTURE_PLANE_ANGLE` + `EGL_D3D11_TEXTURE_ARRAY_SLICE_ANGLE`. That is true zero-copy.
- Device sharing between decoder and renderer is mpv's `d3d11va` arrangement [U for mpv's exact locking].
- NVAPI swap groups take the same `ID3D11Device` and PlayMap's `IDXGISwapChain` (§6.2).

---

## 6. Framelock and genlock hardware

### 6.1 NVIDIA RTX PRO Sync (formerly Quadro Sync II)

- "The NVIDIA RTX PRO Sync card is a rebranding of the Quadro Sync II card that was aligned with the launch of the NVIDIA RTX PRO Blackwell cards." The user guide is v08 (2025-04-16, "Rebranding… New features highlighted, including Variable Refresh Rate"), then v09 (2026-03-10, "Updated references to Sync Utility"). [V, images.nvidia.com/aem-dam/Solutions/design-visualization/quadro-product-literature/nvidia-rtx-pro-sync-user-guide.pdf]
- Capabilities [V, same guide; nvidia.com/en-us/design-visualization/solutions/rtx-pro-sync/, n.d.]:
  - Up to **4 matching GPUs per card** and **2 cards per system** (8 GPUs, cards joined by CON0), with **32 displays per system**.
  - External sync up to **1000 Hz**. Sync multiply and divide from firmware 2.18+.
  - BNC **TTL sync output** with a short duty cycle (about **200 ns** at 60 Hz).
  - Display timings must be within **±50 ppm**.
  - Nodes daisy-chain with CAT-5 RJ45, "Do not use Ethernet hubs or switches… the card will not operate if it is connected to networking equipment".
  - Setup order: G-SYNC off, stereo, Mosaic, then Frame Lock last.
  - Windows CLI: `configureSync.exe` (Sync Utility), e.g. `configureSync.exe enable server source=VSYNC display=0,0 display=1,0`. Linux: `nvidia-settings -a [dpy:N]/FramelockDisplayConfig=2` (server) or `=1` (client), then `[gpu:0]/FramelockEnable=1`.
- Compatible GPUs [V, product page]: RTX PRO 6000/5000/4500/4000 Blackwell; RTX 6000/5000/4500/4000(SFF) Ada; RTX A6000/A5500/A5000/A4500/A4000; A40; L40/L40S; Quadro RTX 8000/6000/5000/4000; GV100. **GeForce is not supported.** Watchout's docs state the same (§7).
- **Field risk, 2026.** Forum reports that RTX PRO Sync firmware **v3.04/v3.06** fails to configure a house-sync timing server, while v2.02 works on Ada. Blackwell forces v3.04+. An NVIDIA staff reply says it works in their lab and suggests a frequency mismatch (for example a 60 Hz mosaic against 59.94 Hz house sync) and driver r595 596.86. [V, forums.developer.nvidia.com/t/359018 (2026-01-29) and /362499 (2026-03-05)] **Preflight should report sync firmware and driver versions against a known-issues list.**

### 6.2 NVAPI (headers and stub libraries MIT, SDK R615)

[V, github.com/NVIDIA/nvapi at commit 2026-09-18; `License.txt` SPDX MIT; `docs/R615-*`]

**Swap groups (D3D10/11/12)**
- `NvAPI_D3D1x_QueryMaxSwapGroup(IUnknown* pDevice, NvU32* pMaxGroups, NvU32* pMaxBarriers)`
- `NvAPI_D3D1x_QuerySwapGroup(IUnknown* pDevice, IDXGISwapChain* pSwapChain, NvU32* pSwapGroup, NvU32* pSwapBarrier)`
- `NvAPI_D3D1x_JoinSwapGroup(IUnknown* pDevice, IDXGISwapChain* pSwapChain, NvU32 group /*0 = leave*/, BOOL blocking)`. With `blocking`, a present "should return immediately or block until all members of the SwapGroup are ready and the presentation was actually executed".
- `NvAPI_D3D1x_BindSwapBarrier(IUnknown* pDevice, NvU32 group, NvU32 barrier /*0 = release*/)`
- `NvAPI_D3D1x_Present(IUnknown* pDevice, IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags)` is the present wrapper that "notifies the D3D driver of the SwapChain used… thus allowing the D3D driver to apply SwapGroup and SwapBarrier functionality". It returns `NVAPI_DEVICE_BUSY` on device reset or removal, or `DXGI_STATUS_OCCLUDED`.
- `NvAPI_D3D1x_QueryFrameCount(IUnknown* pDevice, NvU32* pFrameCount)` reads "the universal framecounter of the Quadro-Sync master device". `NvAPI_D3D1x_ResetFrameCount(IUnknown* pDevice)` resets it.

**Present barrier (R470+, D3D12 only)**
- `NvAPI_D3D12_QueryPresentBarrierSupport`
- `NvAPI_D3D12_CreatePresentBarrierClient(ID3D12Device*, IDXGISwapChain*, NvPresentBarrierClientHandle*)`
- `NvAPI_D3D12_RegisterPresentBarrierResources(client, ID3D12Fence*, ID3D12Resource**, NvU32)`. The fence is signalled only by the driver and must be monotonic. Re-register after `ResizeBuffers`.
- `NvAPI_JoinPresentBarrier(client, NV_JOIN_PRESENT_BARRIER_PARAMS{dwVersion = NV_JOIN_PRESENT_BARRIER_PARAMS_VER1})`, `NvAPI_LeavePresentBarrier`, `NvAPI_DestroyPresentBarrierClient`
- `NvAPI_QueryPresentBarrierFrameStatistics` → `NV_PRESENT_BARRIER_FRAME_STATISTICS { dwVersion; SyncMode; PresentCount; PresentInSyncCount; FlipInSyncCount; RefreshCount; }`. `SyncMode` is one of:
  - `PRESENT_BARRIER_NOT_JOINED 0`
  - `PRESENT_BARRIER_SYNC_CLIENT 1`, which happens "if the back buffers of this client are composited instead of being flipped out to screen"
  - `PRESENT_BARRIER_SYNC_SYSTEM 2`
  - `PRESENT_BARRIER_SYNC_CLUSTER 3`
- `NvAPI_D3D12_CreateDDisplayPresentBarrierClient` (R510) covers DisplaySource.
- NVIDIA's blog says the present barrier "is only effective when an application is in a full-screen state with no window borders, as well as no desktop scaling or taskbar composition". Otherwise it "reverts to a pending state". In-system sync needs the same GPU and timing, a Quadro Sync board, or Mosaic. [V, developer.nvidia.com/blog/synchronizing-present-calls-between-applications-on-distributed-systems-with-directx-12/, 2022-07-26]

**Sync board status and control (G-Sync API)**
- `NvAPI_GSync_EnumSyncDevices`
- `NvAPI_GSync_QueryCapabilities` → `NV_GSYNC_CAPABILITIES_V3 { boardId (P358 = 856, P2060 = 8288, P2061 = 8289), revision, capFlags, extendedRevision, bIsMulDivSupported:1, maxMulDivValue }`
- `NvAPI_GSync_GetTopology`
- `NvAPI_GSync_GetSyncStatus(hDevice, hPhysicalGpu, NV_GSYNC_STATUS{ bIsSynced, bIsStereoSynced, bIsSyncSignalAvailable })`
- `NvAPI_GSync_GetStatusParameters` → `NV_GSYNC_STATUS_PARAMS_V2 { refreshRate, RJ45_IO[2], RJ45_Ethernet[2] /* "Connected to ethernet hub? [ERRONEOUSLY CONNECTED!]" */, houseSyncIncoming /*Hz*/, bHouseSync, bInternalSlave:1 }`
- `NvAPI_GSync_Get/SetControlParameters` → `NV_GSYNC_CONTROL_PARAMS_V2 { polarity, vmode, interval, source (VSYNC/HOUSESYNC), interlaceMode, syncSourceIsOutput, syncSkew, startupDelay, multiplyDivideMode, multiplyDivideValue }`
- `NvAPI_GSync_SetSyncStateSettings`, `NvAPI_GSync_AdjustSyncDelay`

**OpenGL equivalents**
- `WGL_NV_swap_group` ("Shipping since 2003 on Quadro GPUs with framelock support"): `wglJoinSwapGroupNV(HDC, GLuint group)`, `wglBindSwapBarrierNV(GLuint group, GLuint barrier)`, `wglQuerySwapGroupNV`, `wglQueryMaxSwapGroupsNV`, `wglQueryFrameCountNV`, `wglResetFrameCountNV`. Issues note: "An example for a state that does typically not restrict swap group usage is the use of one fullscreen sized window per windows desktop." [V, Khronos registry raw WGL_NV_swap_group.txt, rev 1.0 2009-09-08]
- `GLX_NV_swap_group` is the Linux counterpart. [V, raw GLX_NV_swap_group.txt; download.nvidia.com/XFree86/Linux-x86_64/610.43.02/README/framelock.html]

**Usage proof**
- Unreal nDisplay's `swap_sync_policy=2` is "NVIDIA swap lock (only for NVIDIA cards rendering with DirectX11 and DirectX12)", with `sync_group` / `sync_barrier` defaulting to 1. [V, docs.unrealengine.com/4.27/en-US/WorkingWithMedia/IntegratingMedia/nDisplay/Configuration/] So D3D11 swap groups work in a shipping product.
- **Unknown:** whether D3D11 swap groups engage with a windowed flip-model swap chain in independent flip, or need FSE. NVAPI says nothing. The WGL spec and NVIDIA's blog both point to "fullscreen-sized, uncomposed". This is the first lab test (§12).

### 6.3 AMD

- AMD FirePro **S400** provides "Output lock… and 3D swap lock", up to 4 GPUs per system, BNC house sync (blackburst, tri-level, TTL, SDI), "Framelock supported in OpenGL". The original list covers FirePro W9000/W8000/W7000/V7900. [V, bhphotovideo.com/lit_files/210883.pdf; alldatasheet copy marked ©2013]
- A reseller lists S400 compatibility up to **Radeon PRO VII, WX 9100, WX 8200, WX 7100**. [V, shop.schneider-digital.com/…/amd-firepro-s400-g-sync-option-card, n.d.]
- The RDNA3 **Radeon PRO W7800** datasheet lists no framelock or genlock among its supported technologies. [V, amd.com/content/dam/amd/en/documents/products/graphics/workstation/radeon-pro-w7800-datasheet.pdf, n.d.]
- disguise documents an AMD genlock workflow for VX 4/4+. [V, help.disguise.one/designer/configuration/genlock-configuration]
- I found no public AMD application API (AGS or ADL) for swap barriers. **Status 2026: legacy hardware only; treat AMD as "free-run, measured".** [U, absence of evidence]

### 6.4 Intel

- **IGCL** (Intel Graphics Control Library, header © 2025, Intel-licensed, so not to be copied into MIT code; write P/Invoke from the published docs) [V, raw.githubusercontent.com/intel/drivers.gpu.control-library/master/include/igcl_api.h; intel.github.io/drivers.gpu.control-library/api.html]:
  - `ctlGetSetDisplayGenlock(hDevices, ctl_genlock_args_t*, count, hFailureDeviceAdapter*)` with operations `CTL_GENLOCK_OPERATION_GET_TIMING_DETAILS 0`, `VALIDATE 1`, `ENABLE 2`, `DISABLE 3`, `GET_TOPOLOGY 4`.
  - Topology `{ NumGenlockDisplays, IsPrimaryGenlockSystem, CommonTargetMode, pGenlockDisplayInfo[{hDisplayOutput, IsPrimary}], pGenlockModeList }`, plus `IsGenlockEnabled` and `IsGenlockPossible`.
  - `ctlGetVblankTimestamp(hDisplayOutput, ctl_vblank_ts_args_t{ NumOfTargets, VblankTS[] /* µs */ })`.
  - The sample's modes are "primary: system providing ref clock and ref sync" and "secondary: … referring ref clock and ref sync from primary", on DP or HDMI.
  - The physical link and the supported SKUs are **U**.
- **SW Genlock** (intel-retail/software-vsync-modulation-sample, README © 2025, MIT) [V, github.com/intel-retail/software-vsync-modulation-sample and docs/swgenlock.md]:
  - Linux only. It modulates the display **DPLL via MMIO** to align vblanks across systems with "microsecond-level accuracy".
  - Clock: PTP recommended, Chrony "tens of microseconds".
  - Defaults: 100 µs drift before reprogramming, 0.01 % PLL shift.
  - "pipelock" mode serves multiple pipes on one system.
  - Hardware vblank timestamps ("Hammock Harbor", `-H`) on Panther Lake onwards.
  - Platforms: Tiger Lake through Panther Lake, and Arc B-series.
  - Needs root. The demo uses unencrypted sockets.

---

## 7. How the competition uses framelock, and what it does without it

| Product | With hardware sync | Without it | Health and UX |
|---|---|---|---|
| **disguise** | A sync card per server takes genlock from a house clock. Framelock aligns outputs within a server. "Apply Genlock" configures **all machines with one button**, and "a machine does not need to be running Designer". Conditions: same resolution, refresh and timing mode on all heads; matching EDIDs. IP-VFC can derive genlock from **PTP** (ST 2110) and feed the GPU sync card (TTL on NVIDIA; tri-level on AMD) | "When genlock is not present… the Apply Genlock button is replaced with an Apply Framelock button" (Solo has no genlock) | Feed View shows "Genlock: Present (unused)", then a green per-VFC icon. The Network Status Widget has Network, Genlock and Framelock icons; "Transport control status… must be 'In sync'". The Output Sync Test reads `tRender`, `frameTime`, `deltaTime` across machines. [V, help.disguise.one/designer/configuration/genlock-configuration; …/networking/output-sync-test; …/hardware/ip-vfc/ip-vfc-genlock; …/feed-outputs/head-configuration] |
| **Pixera** | NVIDIA Quadro Sync II (Quadro/RTX only), configured in the NVIDIA Control Panel. "For syncing outputs on the internal timing it is mandatory to use the same EDID on all outputs." "The render engine has to be restarted after the settings are applied" | — | "The sync will only be activated for the active display setup. If the setup changes… the sync must be reactivated." [V, help.pixera.one/graphic-cards/synchronize-outputs-genlock-framelock-setup] |
| **WATCHOUT 7** | **Hardware Sync Groups** (Show Properties): an NVIDIA sync board in every node, daisy-chained, timing server auto-detected. "A sync group switches the timing source from NTP to the frame counter on the sync boards." "The sync boards' frame counter is 24-bit, so it wraps about every 3 days at 60 FPS (3 days 5 hours). WATCHOUT resets the counter just before it overflows, which causes a brief one-time glitch of about 4 frames at 60 FPS." **"A node whose NVIDIA sync is not valid stalls on the frame barrier and stops presenting frames."** | NTP via `w32time`, with the Director node as time source, "typically within a millisecond". A resync is forced when the offset exceeds about 100 ms. "NTP… does not align GPU output… a fraction of a frame apart… Different Windows versions and GPU vendors also implement Full Screen Optimizations (FSO) differently". GeForce: "Software sync only". AMD: no hardware genlock in WATCHOUT | The Nodes window shows NTP Diff, GPU, drive and network. SDI output genlocks via a Deltacast card's reference input. The SDI page mentions **OpenGL compute shaders** for RGB→YUV and warp/blend compositing, so a pro server can render in GL and still be smooth [V, docs.dataton.com/guide/watchout/working-with-shows/show-properties.html; …/network-setup/time-synchronization.html; …/getting-started/system-requirements.html; docs.dataton.com/watchout-7-new/watchout/displays-and-outputs/sdi-output.html] |
| **Resolume Arena** | No framelock feature. It points to GPU-level sync ("Eyefinity, Mosaic" or "Quadro Sync"). "Even on a single GPU, outputs are not necessarily frame-sync" | SMPTE or OSC playhead sync between machines "may not necessarily result in frame synced combined output… there will be noticeable difference between frames" | — [V, resolume.com/support/en/lots-of-outputs; resolume.com/forum/viewtopic.php?t=13660 (2016)] |
| **Unreal nDisplay** (reference) | `swap_sync_policy=2`, NVIDIA swap lock, DX11 and DX12 | Policy 1 is software swap sync with VSync=1. TCP barriers per frame, with `barrier_wait_timeout` 5000 ms. On timeout "the state of the cluster state is determined to be invalid, and all nodes shut themselves down" (4.27). 2025–26 forum: "nDisplay's cluster manager does network barrier waits each frame… cost grows with node count" | [V, docs.unrealengine.com/4.27/…/nDisplay/Configuration/ and /Ports/; forums.unrealengine.com/t/…/2728981 (2026-06-16); …/2699850 (2025-11-12)] |

**Lessons (E).**
1. Everyone at the high end uses NVIDIA sync boards. AMD is legacy.
2. The UX bar is disguise's one-button cross-machine apply with per-machine status, plus Pixera's warning that a display change drops sync.
3. The failure modes to beat:
   - WATCHOUT's stall on a bad node and its scheduled 4-frame counter-reset glitch.
   - nDisplay's lockstep barriers that shut the cluster down.
   - Resolume's honest absence of any sync.

---

## 8. Without sync cards: software multi-node alignment

**Two different problems.**

**(a) Frame identity: the same content frame for the same show time.** PlayMap already makes the frame a pure function of leader time at the predicted vblank plus measured display latency [V-local, ARCHITECTURE §4]. With clock sync at tens to hundreds of µs (an estimate in docs/RESEARCH.md), nodes agree except when a content-frame boundary falls inside the clock-error window.

**(b) Scan-out phase: when each display actually starts the frame.** This is set by each GPU's pixel clock. WATCHOUT states it plainly: NTP "does not align GPU output. Each node's GPU scans out on its own clock". [V] No Windows user-mode API steers the pixel clock live. Vendor custom-timing APIs change modes, which means a blank [U].

**Arithmetic (E).**

| Mismatch | Period at 60 Hz | Slip by one whole frame every |
|---|---|---|
| 10 ppm | 16.667 ms | (1/60)/10e-6 = **27 min 47 s** |
| 50 ppm (the sync card's tolerance band) | 16.667 ms | **5 min 33 s** |
| 59.94 vs 60.00 Hz (1001 ppm) | — | **16.65 s** |

- Between slips, two free-running outputs differ by a phase φ ∈ [0, T). The content offset seen by a camera is up to one refresh: this is the documented "±1 refresh" ceiling, now with a number for how often it flips.

**What software can do on Windows (E, using the APIs in §2).**
1. **Know each output's real refresh to ppm and its vblank phase in leader time.**
   - Fit a line through (`SyncRefreshCount`, `SyncQPCTime`) pairs from `GetFrameStatistics`: the slope is the period, the intercept the phase. QPC → `MonoClock` → leader time via the existing `ClockOffsetEstimator`.
   - Cross-check with `D3DKMTGetScanLine` phase samples.
   - Fall back to `DwmGetCompositionTimingInfo` when composed.
   - Publish refresh (6 decimals), ppm against nominal, phase against the group's reference output, and **predicted time to next slip**.
2. **Make presents land on the intended vblank, and know when they did not.** Store present ID → target refresh count. Detect misses with Microsoft's algorithm (§2.5) and recover with `SyncInterval 0` skips. Select content for the refresh the present will actually hit, not merely "next".
3. **Refuse what cannot work.** Preflight refuses mixed refresh rates in a sync group (59.94 vs 60) and VRR on, and reports EDID timing mismatch. The ppm check uses measured periods, not nominal ones.
4. **Optional static rate trim (U, risky).** Vendor custom-timing APIs could nudge one output's pixel clock so a pair's relative drift drops from 50 ppm to under 5 ppm, stretching slips from minutes to hours. This is a mode change, setup-only, behind an approval. Lab item.
5. **Linux render nodes on Intel iGPUs:** a real software genlock through Intel's MIT SW Genlock (DPLL steering, µs-level, PTP or Chrony) (§6.4). This is a differentiator for cheap signage and museum fleets.

**Measuring sync (E unless noted).**
- **Camera.** PlayMap's planned `pattern.syncmarks` + camera certificate (ROADMAP R2.7) measures *frame identity* and ±1-frame errors. A 240 fps phone frame is 4.17 ms, too coarse for sub-frame phase. Rolling-shutter row timing can refine it, but only as a lab method.
- **Photodiode.** Two photodiodes on flashing patches, sampled at ≥ 20 kHz (50 µs resolution) by a USB scope or a microcontroller, give inter-output phase directly. Publish it as a "sync certificate" with p50 and p99.
- **Reference visual tests.** Intel's `framesync` changes solid colours "simultaneously on all screens at precisely the same moment" for visual verification [V, intel-retail README]. disguise's Output Sync Test compares `frameTime` readouts across machines [V].

---

## 9. Linux and macOS, briefly

**Linux KMS** [V, raw.githubusercontent.com/torvalds/linux/master/include/uapi/drm/drm.h and drm_mode.h]
- `drmModePageFlip(..., DRM_MODE_PAGE_FLIP_EVENT 0x01)` delivers `struct drm_event_vblank { struct drm_event base; __u64 user_data; __u32 tv_sec; __u32 tv_usec; __u32 sequence; __u32 crtc_id; }`. The timestamp is `CLOCK_MONOTONIC` when `DRM_CAP_TIMESTAMP_MONOTONIC (0x6)` is set. `crtc_id` is filled when `DRM_CAP_CRTC_IN_VBLANK_EVENT (0x12)` is set.
- Other flags: `DRM_MODE_PAGE_FLIP_ASYNC 0x02`, `DRM_MODE_PAGE_FLIP_TARGET_ABSOLUTE 0x4`, `…_RELATIVE 0x8`.
- `drm_crtc_queue_sequence { crtc_id; flags; u64 sequence; u64 user_data; }` produces `DRM_EVENT_CRTC_SEQUENCE (0x03)`, whose "Time stamp marks when the first pixel of the refresh cycle leaves the display engine".
- A headless engine on bare KMS would get exact flip timestamps per CRTC.

**Linux NVIDIA**
- `GLX_NV_swap_group` and `nvidia-settings` Frame Lock with RTX PRO Sync. Restrictions: client displays need "the same mode timings as the server"; Genlock is "frame-synchronized, and not necessarily pixel-synchronized". [V, NVIDIA Linux README 610.43.02, Chapter 31]

**Vulkan, all platforms**
- **`VK_EXT_present_timing`** (ratified; refpage rev 3, 2024-10-09; Khronos blog 2025-12-04) has three features: `presentTiming`, `presentAtAbsoluteTime`, `presentAtRelativeTime`.
  - `VkPresentTimingInfoEXT { flags; uint64 targetTime; timeDomainId; presentStageQueries; targetTimeDomainPresentStage }`, with flag `VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT`.
  - Feedback through `vkGetPastPresentationTimingEXT`.
  - Status: "NVIDIA developer drivers—available now; Mesa implementation—coming soon; Android 17… coming soon".
  - Feedback latency example: about 5 refresh cycles on a Pixel. [V, docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_present_timing.html; khronos.org/blog/vk-ext-present-timing-the-journey-to-state-of-the-art-frame-pacing-in-vulkan]
- This is the cross-platform future of "present at time T".

**macOS**
- `CVDisplayLink*` is "deprecated: first deprecated in macOS 15.0 – use NSView.displayLink(target:selector:), NSWindow.displayLink… or NSScreen.displayLink". [V, gitlab.com/qemu-project/qemu/-/work_items/2575, 2024-09-17, quoting the SDK header]
- The AppKit display links vend a `CADisplayLink` (macOS 14) with `timestamp`, `targetTimestamp` and `duration`. [V, philz.blog, 2025-06-01]
- `CAMetalDisplayLink` (macOS 14) gives `targetPresentationTimestamp` per update. [V, docs.rs objc2-quartz-core generated bindings; apple-docs mirror]
- OpenGL is deprecated on macOS, so a Mac engine means Metal (ROADMAP: long-term).

---

## 10. The Windows output architecture: options compared

| Criterion | **A. Native D3D11 renderer** (HLSL behind the seam) | **B. GL draws, DXGI presents via `WGL_NV_DX_interop2`** | **C. ANGLE on PlayMap's D3D11 device + own flip swap chains** |
|---|---|---|---|
| Flip model, waitable, frame statistics, iflip | Yes | Yes (Godot reached iflip) | Yes (Avalonia/Patterns pattern) |
| NVAPI swap groups (`NvAPI_D3D1x_*` need `ID3D11Device` + `IDXGISwapChain`) | Yes | Yes, on the presenting D3D11 device | Yes, on the shared device |
| D3D12 present barrier | Via D3D11On12 (possible, untested) | Hard | Hard |
| Zero-copy D3D11VA NV12/P010 | Yes (SRVs on array slices) | **No.** NV12 cannot cross; needs a `VideoProcessorBlt` → RGBA copy | **Yes**, via `EGL_D3D11_TEXTURE_PLANE_ANGLE` + `ARRAY_SLICE` |
| HAP BC1/BC3/BC4/BC7/BC6H | Native | Desktop-GL BPTC/RGTC (fine) | In ANGLE's D3D11 tables; run-time exposure U |
| 10-bit / FP16 swap chain | Native | Yes (wrap R10G10B10A2) | Yes (wrap R10G10B10A2 since v4) |
| Drivers on the critical path | D3D11 only | WGL ICD + D3D11 + interop locks | ANGLE + D3D11 |
| Vendor variance risk | Lowest (D3D11 is Windows' most-used path) [E] | **Highest** (lock cost; NVIDIA/AMD; Intel waits) [V anecdotes] | Low to medium (Chrome's default path; ES 3.0 only on D3D11) |
| New code, much of it untestable on this Linux box | **Large:** device (~1,000+ lines like `GlDevice`), cbuffer mapping, 24 HLSL programs via a generator, Windows CI for DXBC | Medium: interop, locking, present loop | **Small to medium:** EGL glue (~20 P/Invokes), header switch, format fallbacks, present loop |
| Shader sources | GLSL + generated HLSL | One (GLSL 330) | One (GLSL 330 / ES 300 header switch); Mesa GLES runs it on CI [U for llvmpipe ES level] |
| ISF effects (GLSL) | Run-time GLSL→SPIR-V→HLSL→`D3DCompile` | Native GLSL | Native GLSL ES (ANGLE translator) |
| FFGL plugins (desktop GL 4.x) | Needs a GL island + interop | Native | Needs a GL island + interop |
| Spout | Native D3D11 shared textures | Interop (Spout's own approach) | Native D3D11 shared textures on the shared device |

**Recommendation (E).**
1. **Build C first, as R2.1.**
   - PlayMap creates the `ID3D11Device` per adapter (`D3D11_CREATE_DEVICE_VIDEO_SUPPORT`, multithread-protected). It hands the device to ANGLE (`eglCreateDeviceANGLE(EGL_D3D11_DEVICE_ANGLE, …)`) and to FFmpeg (`AVD3D11VADeviceContext.device`, `BindFlags = DECODER | SHADER_RESOURCE`).
   - It creates a flip-model waitable swap chain per output and wraps each back buffer, or a per-output intermediate, as an ANGLE pbuffer.
   - It presents with `IDXGISwapChain1::Present1(1, 0)`, or `NvAPI_D3D1x_Present` when grouped.
   - This gives the presentation, statistics, framelock, zero-copy decode and BC formats PlayMap needs, with the least untestable code. It reuses an approach already in PlayMap's own stack.
2. **Extract `IGpuDevice` from `GlDevice` now**, with the same surface. Desktop-GL (Linux, development) and ANGLE-GLES (Windows) become two dialects of one implementation.
3. **Gate A (native D3D11) on measurements, not opinion.** Port the hot path natively only if one of these holds:
   - ANGLE costs more than 1 ms CPU per frame per output.
   - `EXT_texture_compression_bptc` or `EXT_color_buffer_half_float` is missing on a reference GPU.
   - A vendor bug blocks the path.

   The hot path is "present", "output", "yuv" and "blit", about 4 of the 24 programs. HLSL is generated by glslang + SPIRV-Cross with reflection, DXBC built on the Windows runner, with a `D3DCompile` cache at run time.
4. **B only as an island.** Use GL-on-DXGI interop only to host desktop-GL FFGL plugins and exchange their textures. Use NVIDIA's `OGL_CPL_PREFER_DXPRESENT` DRS setting as a stop-gap HARDENING fix for today's GL path on NVIDIA nodes until C lands.
5. **Fallback chain at run time on Windows:** C (DXGI flip, measured) → today's WGL path, flagged `present.path = "gl-wgl"` and `present.mode = "unknown"` in red. Patterns' **fuse file** idea carries over: a start that never reaches first frame falls back next time and says why [V-local].

---

## 11. Framelock design with graceful fallback

### 11.1 Per-output sync states (STATE, MCP, desk chip)

| State | Meaning | Evidence that must be present (attempts are not facts) |
|---|---|---|
| `house-locked` | Board genlocked to external house sync | `NV_GSYNC_STATUS_PARAMS.bHouseSync = 1`, `houseSyncIncoming` = nominal ± tolerance, `NV_GSYNC_STATUS.bIsSynced = 1`, swap barrier bound, `QueryFrameCount` advancing one per refresh |
| `framelocked` | Board framelock across nodes, no house sync | `bIsSynced = 1`, `bIsSyncSignalAvailable = 1`, barrier bound, frame count advancing |
| `grouped` | Swap group within one node, no barrier (single-node multi-output) | `NvAPI_D3D1x_QuerySwapGroup` returns group ≠ 0 |
| `soft-locked` | Intel IGCL genlock, or Linux SW Genlock "IN SYNC" | `ctl_genlock_args_t.IsGenlockEnabled`; SW Genlock status |
| `free-run, measured` | Flip model in iflip, frame statistics healthy | Fitted period (ppm) and phase published; PresentMon or `CompositionMode` consistent |
| `free-run, unknown` | Composed, or the GL path | Shown in red; certificates refused |

Each output also publishes:
- `present.path`, `present.mode`, `refreshHz` (measured, 6 decimals), `ppm`
- `phaseUs` against the group reference, `nextSlipS`
- `missedVblanks`, `glitches` (present-ID → refresh mismatches)
- `latencyFrames`
- `board {id, fw, houseSyncHz}`
- `inSyncCount` (from the present barrier or the frame-count delta)

### 11.2 State machine and "never stall the air"

**Join**
1. Validate:
   - every output in the group has the same measured refresh (≤ 50 ppm apart), the same mode timing and EDID, VRR off;
   - the sync board is present and its firmware is not on the known-issues list;
   - no `RJ45_Ethernet` miswire. Report this one as "the framelock cable is plugged into a network switch".
2. `NvAPI_D3D1x_JoinSwapGroup(dev, sc, g, blocking = FALSE)`, then `BindSwapBarrier(dev, g, b)`.
3. Present only through `NvAPI_D3D1x_Present`.
4. Declare the state only after N = 120 consecutive refreshes with the frame count advancing by one per present. The leader checks that every member reports the same count modulo wrap.

**Stall watchdog (render thread, no extra thread touches DXGI)**
- The thread waits on the waitable objects with a timeout of 3 × T.
- If a grouped output's waitable has not signalled within 3 T, or `QueryFrameCount` stops advancing for 3 T:
  1. Unbind the barrier (`BindSwapBarrier(dev, g, 0)`) and leave the group (`JoinSwapGroup(dev, sc, 0, FALSE)`).
  2. Continue presenting free-run on the same vblank pacing.
  3. Raise a high-priority alarm on STATE, Show Bus and MCP.
  4. **Never rejoin automatically while armed.** Rejoin at an operator verb, or at a blackout or scene boundary, because rejoining can blink.

  This is the opposite of WATCHOUT's documented stall. Whether `blocking = FALSE` behaves as described on current drivers is **U** and is lab test 1.

**Content selection under lock (deterministic, no clock error)**
- When the group is locked, all members present on the same refresh. Each node maps the board's universal frame count to leader time once, at join and at each `AT`-scheduled start, through the shared (count₀, leader-time₀) pair the leader broadcasts.
- Thereafter frame = f(anchor, (count − count₀) × T_group). This removes clock-estimate error from boundary decisions entirely.
- **Wrap:** the counter is 24-bit (WATCHOUT, §7). Compute deltas modulo 2²⁴ with unsigned arithmetic. Re-anchor every hour while idle, never by resetting the counter in a show. That avoids WATCHOUT's ≈4-frame reset glitch.
- 2²⁴ refreshes last 3 d 5 h 40 m at 60 Hz, 3 d 21 h 12 m at 50 Hz, and 1 d 14 h 50 m at 120 Hz (E).

**Without lock**
- Keep today's leader-time selection, but with the vblank instant taken from fitted frame statistics instead of swap-return times. Publish phase and next-slip.

### 11.3 Wire: a vblank report on UDP 9942 (new packet, fail-closed)

The existing `PMCK` v1 packet stays untouched. Add a second fixed-size packet that the existing reader refuses and the new reader accepts only by its own magic (E):

```
PMVB v1, 64 bytes, little-endian
off size field
 0   4   magic "PMVB" (0x42564D50 read LE)
 4   1   version = 1
 5   1   state (0 unknown, 1 free-measured, 2 grouped, 3 soft-locked, 4 framelocked, 5 house-locked)
 6   1   outputIndex (node-local)
 7   1   flags (bit0 iflip proven, bit1 composed, bit2 stats disjoint, bit3 barrier bound, bit4 house sync present)
 8   4   u32 seq
12   4   u32 boardFrameCount (24-bit significant; 0 if none)
16   8   i64 periodPs (fitted refresh period, picoseconds)
24   8   i64 lastVblankLeaderNs (leader-time of the vblank at refreshIndex)
32   8   u64 refreshIndex (SyncRefreshCount, widened)
40   8   u64 lastPresentRefresh (PresentRefreshCount of last displayed present)
48   4   u32 missedSinceLast
52   4   u32 houseSyncMilliHz (0 if none)
56   8   reserved, zero (refused if non-zero)
```

The leader computes pairwise phases and the group's slip forecast, and publishes a `sync` block in STATE and a `SYNC REPORT` resource in MCP.

### 11.4 Operator surface

- **One-button sync (disguise parity).**
  - `SYNC APPLY <group>` runs on every node: NVIDIA board configuration through `NvAPI_GSync_SetSyncStateSettings`, or `configureSync.exe` / `nvidia-settings` where the API is unavailable.
  - High risk: displays blink. Approval-gated for AI and remote callers, refused while armed.
  - `SYNC LEAVE`, `SYNC REJOIN`, `SYNC TEST` (shows `pattern.syncmarks` and collects a camera or photodiode certificate).
- **Fleet health (Pixera Hub parity).** Per node and output: chip state, board id and firmware, house-sync Hz, `inSyncCount`, ppm, phase, next slip, PresentMon mode, missed vblanks over 24 h.
- **Preflight (superior, E).**
  - Refuse mixed 59.94/60 in a group.
  - Flag VRR/G-SYNC, HAGS and MPO states.
  - Run a PresentMon iflip proof.
  - Check frame-statistics sanity (fitted ppm within ±500 of nominal, no disjoint storm).
  - Check board firmware against known issues.
  - Detect a cable plugged into a switch (`RJ45_Ethernet`).
  - Check EDID timing equality.
  - Run a 60 s soak with zero glitches.

### 11.5 Frame-rate handling hooks (R2.10)

- Prefer running projectors at a content-native mode (24/25/50/59.94/60).
- When cadence is forced (23.976 on 60 Hz), schedule 3:2 explicitly by refresh index.
- Try `IDXGISwapChainMedia::SetPresentDuration(417083)`, in 100 ns units (1001/24 ms = 417 083 × 100 ns), and honour `ApprovedPresentDuration`. On approval the display may switch to a multiple rate; it is unapproved on most desktops [U].
- Composition-swapchain target times are a later experiment (§2.7).

---

## 12. What to test first in the hardware lab (R0.5)

| # | Test | Pass criterion |
|---|---|---|
| 1 | `NvAPI_D3D1x_JoinSwapGroup(blocking = FALSE)` + `BindSwapBarrier` on a **windowed borderless flip** swap chain in iflip, on RTX PRO + RTX PRO Sync; repeat in FSE | `bIsSynced`, frame count advancing; photodiode phase ≤ 50 µs between nodes; behaviour when one node is killed (does the other stall?) |
| 2 | ANGLE on a PlayMap-created device: `eglCreateDeviceANGLE`, wrapped back buffer, `GL_EXTENSIONS` dump (bptc, rgtc, norm16, color_buffer_half_float, polygon_mode) on NVIDIA, AMD and Intel | All needed extensions present, or the fallback list is known |
| 3 | PresentMon on 4 outputs, 1 adapter, 60 min | 100 % `Hardware: Independent Flip` or `Hardware Composed: Independent Flip`; zero glitches by the present-ID algorithm |
| 4 | Frame statistics vs `D3DKMTGetScanLine` vs photodiode, on 2 nodes | Fitted ppm within 2 ppm of photodiode; phase within 100 µs |
| 5 | D3D11VA NV12 → ANGLE planes → output, 4K60 HEVC × 4 | Zero copy-back; GPU time budget published |
| 6 | Interop island (FFGL sample) on NVIDIA, AMD and Intel | Locks under 1 ms per frame at 60 Hz, or the island is NVIDIA-only |
| 7 | RTX PRO Sync firmware v3.06 + Blackwell + house sync 59.94/60 matrix | Reproduce or refute the 2026 forum reports; record in the known-issues list |

---

## 13. Design implications for PlayMap: what makes it comparable or superior

**To be comparable (parity with disguise, Pixera and WATCHOUT on Windows smoothness):**
1. **R2.1 as option C.**
   - A PlayMap-owned D3D11 device per adapter.
   - One `DXGI_SWAP_EFFECT_FLIP_DISCARD` waitable swap chain per output: 3 buffers, `SetMaximumFrameLatency(1)`, borderless window exactly covering the monitor, DWM transitions off, `DXGI_MWA_NO_ALT_ENTER`, no FSE dependency.
   - ANGLE renders today's shaders (ES 3.0 dialect) on the same device into wrapped back buffers. Desktop GL 3.3 remains for Linux and development behind an extracted `IGpuDevice`.
2. **Vblank truth from `GetFrameStatistics`.** Use `SyncQPCTime` and `SyncRefreshCount` regression for period and phase, and Microsoft's present-ID → refresh glitch detection. `FramePacer`'s swap-return estimate becomes the fallback.
3. **Framelock via NVAPI D3D1x swap groups and barriers.** Status from `NvAPI_GSync_GetSyncStatus` / `GetStatusParameters` / `QueryCapabilities`, a 24-bit-wrap-safe frame counter, and STATE chips. disguise-style one-button apply across nodes.
4. **Zero-copy D3D11VA** on the shared device: decoder `BindFlags = DECODER | SHADER_RESOURCE`, multithread protection, FFmpeg lock callbacks, ANGLE plane import. HAP BC formats on the GPU.
5. **10-bit output** with an R10G10B10A2 swap chain, and `BitsPerColor` published per output.
6. **Proof, not claims.** A PresentMon iflip proof in preflight, a missed-vblank count in STATE, and a 72 h soak (the ROADMAP R2 exit).

**To be superior (things the leaders document doing worse or not at all):**
1. **Never stall the air.** Join barriers non-blocking, watch waitable objects with a 3 × T timeout, and drop to free-run with an alarm when lock breaks. This contrasts with WATCHOUT's documented barrier stall and nDisplay's cluster shutdown.
2. **Deterministic content under lock.** Select frames from the shared hardware frame counter with explicit modulo-2²⁴ arithmetic and **no counter reset in a show**. WATCHOUT documents a ≈4-frame glitch every ≈3 d 5 h at 60 Hz.
3. **Numbers instead of hope without sync cards.** Show measured refresh in ppm, phase in µs and "this pair slips one frame every 5 min 33 s" on every output of every node, in STATE and MCP. Refuse mixed 59.94/60 in a group. Issue a camera or photodiode **sync certificate** (R2.7).
4. **Operator-grade diagnostics.** Map NVAPI fields to plain words: "framelock cable is in a network switch" (`RJ45_Ethernet`), "house sync 59.94 Hz but outputs at 60.00 Hz", "sync firmware 3.06 has known genlock issues with this driver", "output composed, not flipped: a window is on top".
5. **Cheap fleets get real lock.** On Linux Intel render nodes, integrate MIT SW Genlock (DPLL steering, µs-level, PTP or Chrony) as `soft-locked`. On Windows Intel, use IGCL genlock where `IsGenlockPossible`. No competitor in §7 offers card-free vblank alignment.
6. **One source of truth for shaders, one device for everything.** Decode, draw, present and framelock share one D3D11 device, with no copy-backs. FFGL plugins live on a bounded GL interop island, and Spout uses native D3D11 shared textures.
7. **Stop-gap now.** Until C lands, offer a HARDENING fix on NVIDIA nodes that sets `OGL_CPL_PREFER_DXPRESENT_ID (0x20D690F8) = PREFER_ENABLED (1)` on PlayMap's driver profile via NVAPI DRS, on approval. Verify with PresentMon. Fall back if not in independent flip.

**Effort and order (E):**
- S: NVAPI DRS stop-gap, PresentMon preflight, frame-statistics-based pacer.
- M: option C present path, `IGpuDevice` extraction, ES dialect.
- M: NVAPI framelock tier and sync states.
- M: zero-copy decode on the shared device.
- S: `PMVB` reports and the fleet sync view.
- Conditional L: native D3D11 hot path, only if lab tests 2, 3 or 5 fail.
