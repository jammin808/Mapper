# WP "present": the Windows D3D11 flip-model output path

*Design spec for PlayMap's round-2 professional features. Written 13 October 2026 against `main` (`c12b186`,
`/home/user/mapper`). It builds on `r2/research/present.md` (cited as **[P §n]**) and `r2/research/codecs.md`
(**[C §n]**). Facts checked on this machine while designing are in Appendix A and marked **[A]**.*

**The decision in one paragraph.** On Windows, PlayMap creates its own `ID3D11Device` and one **DXGI flip-model,
waitable swap chain per Display output**, in a borderless window that exactly covers the monitor. It reads
**`GetFrameStatistics` as vblank truth** and counts **missed vblanks from the display's side**, using a
present-ID ledger. The existing GLSL renderer draws on that same device through **ANGLE**, as an OpenGL ES 3.0
dialect of today's `GlDevice`. It draws into the wrapped back buffers. **No WGL/DX interop is used on the present
path** [P §4, §10]. The GL 3.3 core path stays for Linux, macOS, development and the test suite. The ES dialect
also runs here on Mesa (OpenGL ES 3.2, llvmpipe). All 24 programs compile and link as GLSL ES 3.00 with only the
header changed [A1], so the code the Windows path draws with is tested on every CI run. A native D3D11 hot path
stays behind the same seam. It is built only if lab measurements require it [P §10.3].

---

## 1. Goal and the bar

### 1.1 What the best competitors do

| Who | What they do about output smoothness | Source |
|---|---|---|
| Microsoft's documented route | Flip model (`FLIP_DISCARD`), a waitable object with latency 1, a borderless window covering the screen. That gives Independent Flip: "same efficiency as fullscreen exclusive" and down to 1 frame of latency. Frame statistics give vblank truth, and a present-ID → refresh algorithm finds glitches | [P §2.2–2.5] |
| Patterns (sister app) | Flip model through Avalonia's `LowLatencyDxgiSwapChain` (ANGLE). It paces on `IDXGIOutput::WaitForVBlank`, not on a waitable object or frame statistics. A **fuse file** falls back to composition when a start fails | [P §1, §5.2] |
| WATCHOUT 7 | Its docs concede "Different Windows versions and GPU vendors also implement Full Screen Optimizations (FSO) differently". The Nodes window shows NTP diff, GPU, drive and network. No per-output display-side frame counts are documented | [P §7] |
| disguise | The Output Sync Test reads `tRender`, `frameTime` and `deltaTime`, which are software timers. 10-bit output | [P §7], docs/COMPETITIVE.md |
| Unreal nDisplay | D3D11/12 with NVIDIA swap lock (policy 2) | [P §6.2, §7] |
| Godot prototype | Reached "Hardware Composed: Independent Flip" and about 1 frame of latency. It needed a wait before the first frame | [P §2.3, §4] |
| MadMapper (anecdote) | A forum thread reports staff steering Windows users to Direct3D output for VBL sync | docs/COMPETITIVE.md (U) |

### 1.2 How PlayMap matches each point

| Point | PlayMap this round |
|---|---|
| Flip model, waitable, latency 1, borderless covering the monitor | `DxgiOutputWindow`: `FLIP_DISCARD`, 3 buffers, `FRAME_LATENCY_WAITABLE_OBJECT`, `SetMaximumFrameLatency(1)`, `WS_POPUP` over the monitor rectangle, DWM transitions off, `DXGI_MWA_NO_ALT_ENTER \| NO_WINDOW_CHANGES`, and a wait before the first frame (§3.4) |
| Vblank truth | `VblankClock` fits (`SyncRefreshCount`, `SyncQPCTime`) pairs. The period is accurate to under 0.1 ppm over 60 s, and the phase predicts the next vblank (§3.6) |
| Glitch detection | `PresentLedger` implements Microsoft's present-ID → expected-refresh algorithm (§3.7) |
| 10-bit | `Output.ColorBits = 10` gives an `R10G10B10A2_UNORM` swap chain, an `Rgb10A2` final picture and a 10-bit dither. Bits requested and bits the output reports are both published (§3.9) |
| Fallback on a bad start | Patterns' fuse, with words in STATE (§3.11) |

### 1.3 How PlayMap beats them (documented weaker or absent elsewhere)

1. **Proof, not claims.** Every output publishes three things in STATE, MCP, the context (the Eye) and Companion: its flip mode, the *evidence* for that mode, and missed vblanks counted from the display's own statistics. The evidence is one of three levels:
   - `proven`: a PresentMon capture matched to this output's swap-chain address;
   - `hint`: `CompositionMode` from the media statistics, plus the measured present-to-display latency;
   - `none`.

   No competitor in [P §7] documents per-output display-side frame counts or a proven flip mode.
2. **Refresh in ppm.** Each output's real refresh is shown to six decimals with its ppm against nominal (for example 59.999823 Hz, −2.95 ppm). This feeds the framelock WP's slip forecast. WATCHOUT shows an NTP difference only.
3. **Light leaves every lens at the same show time.** Each output has its own `LatencyMs`, which is the projector's input lag. Faster outputs are delayed by whole frames to match the slowest, and the sub-frame residual is published (§3.8). Today a single node-wide latency option is the norm, and it is a carry-over gap.
4. **One device for decode, draw, present and framelock.** The D3D11 device is handed to:
   - FFmpeg, for D3D11VA zero-copy (codecs WP);
   - Spout (videoio WP);
   - NVAPI swap groups (framelock WP).

   There are no copy-backs and no interop locks [P §5.4].
5. **A GPU reset is a blink, not a dead node.** When the device is removed (TDR), the GPU side is rebuilt and the outputs come back on their own (§3.12). Today a lost context ends the render loop and leaves the node playing blind.
6. **One shader source on every platform, tested here.** The ES dialect runs the whole Render test suite on Mesa [A1, A2]. The Windows path draws with code that CI exercises on every push.

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **`GlDialect` (`Core330`, `Es300`).** It covers the shader header switch, the per-dialect format and extension table, and an alias proc loader. The loader resolves `glFoo` → `glFooEXT` → `glFooKHR` → `glFooOES` → `glFooANGLE` | All 24 programs compile and link in both dialects on Mesa (shown here [A1]). `tests/PlayMap.Render.Tests` passes with `PLAYMAP_GL_DIALECT=es` and without it. CI runs both legs |
| M2 | **Presenter seam.** `IOutputPresenter` replaces `OutputWindow` in `RenderEngine` and `OutputWindows`. `IGpuPlatform` replaces the hard-wired `RenderContext.CreateHidden`. The GL window is the first implementation, with unchanged behaviour | Every existing Render and Engine test passes unchanged. `RenderHostTests` still sees windows open and close under Xvfb |
| M3 | **`VblankClock` and `PresentLedger`.** Pure arithmetic in `PlayMap.Render.Timing`, fed by DXGI statistics on Windows and by swap-return instants on GL. `FramePacer` stays as the GL sample source | Synthetic tests:<br>• With 600 samples, 50 µs Gaussian jitter and a 20 ppm offset, the recovered ppm is within ±3 ppm (about 4σ: σ_b = σ√(12/N³) = 11.8 ns = 0.71 ppm) and the phase within 30 µs; with 3,600 samples (σ 0.05 ppm) within ±0.5 ppm. The test computes both bounds as 5σ from §3.6's formula, so no seed choice decides it.<br>• Scripted statistics give the exact `missedVblanks`, `glitches` and `repeats`.<br>• A u32 `PresentCount` and `SyncRefreshCount` wrap is handled.<br>• `DISJOINT` resets the clock, which settles after 30 samples. |
| M4 | **The DXGI flip path on Windows:**<br>• a D3D11 device on the adapter of the first Display output;<br>• ANGLE on that device;<br>• one waitable `FLIP_DISCARD` swap chain per Display output;<br>• the `present` program drawn into the wrapped back buffer;<br>• `Present1(1, DO_NOT_WAIT)`;<br>• `GetFrameStatistics` every frame;<br>• `MsgWaitForMultipleObjectsEx` pacing. | Compile-only here. On a `windows-latest` CI job on WARP (§7.3): device, ANGLE, swap chain and 600 presents, with zero faults and STATE `present.path = "dxgi-flip"`. In the lab (R0.5): 4 outputs for 60 min with `missedVblanks = 0` and PresentMon 100 % independent flip |
| M5 | **STATE, verbs, context and light:**<br>• `outputs[].present{…}` and `machine.present{…}`;<br>• the output light computed from display-side counts;<br>• `OUTPUT STATS` and `PRESENT RESET`. | `PresentStateTests`: the keys and types exist. Light rules: amber when `missed60s > 0`, red when `statsAgeMs > 1000` on dxgi. Each is asserted on the fields, not on words |
| M6 | **10-bit:**<br>• `Output.ColorBits` (8 or 10);<br>• `TextureFormat.Rgb10A2` for the final picture;<br>• the output shader's dither at 1/1023;<br>• an `R10G10B10A2_UNORM` swap chain;<br>• `bitsReported` from `IDXGIOutput6::GetDesc1().BitsPerColor`. | On Mesa, a 0–1 horizontal ramp at 1024 px rendered into a 10-bit final reads back 1024 distinct codes, monotonic. The same ramp in 8 bits gives 256. With dither on, the mean error is under half a code |
| M7 | **Fuse and `PRESENT PATH`.** A DXGI start that never reaches 120 settled frames falls back to GL next time and says why. `NodeSettings.PresentPath` is `auto`, `dxgi` or `gl` | `PresentFuseTests` (pure file logic). A host test with a fake platform that never settles shows the fault words and `machine.present.path = "gl"` after the restart |
| M8 | **Per-output latency, plus three carry-overs:**<br>• `Output.LatencyMs`;<br>• a whole-frame delay ring;<br>• the node's air latency is the largest output latency;<br>• an offscreen output's `dropped` no longer goes amber against the project rate;<br>• the scan present stamp is taken consistently after GPU completion, or from the display statistics. | `DelayRingTests` (offscreen on Mesa): outputs at 0 ms and 33.3 ms latency at 60 Hz show the same content frame 2 frames apart in the delay ring. `latencyResidualMs` is correct. A headless node on llvmpipe below 60 fps reads green "offscreen". The scan's present stamp is never earlier than the read-back that holds the frame |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Independent-flip evidence:**<br>• `IDXGISwapChainMedia::GetFrameStatisticsMedia().CompositionMode`;<br>• occlusion (`DXGI_STATUS_OCCLUDED`, a foreign foreground window on the monitor);<br>• `PRESENT PROVE`, which runs PresentMon 2.x and parses its CSV per swap-chain address. | The CSV parser passes on recorded fixture CSVs (iflip, composed, mixed, tearing) here. The live run is lab-only |
| S2 | **Device-lost recovery on both paths.** Tear down and rebuild the GPU side on the render thread | GL on Mesa: an injected loss (a test hook that disposes the context mid-loop) brings the outputs back within 2 s. Journal and STATE show `present.resets = 1`. Three losses in 60 s keep the outputs closed, with words |
| S3 | **NVIDIA stop-gap for the GL path:** `PRESENT NVLAYER ON\|OFF` sets `OGL_CPL_PREFER_DXPRESENT (0x20D690F8) = 1` on PlayMap's driver profile through NVAPI DRS, with approval [P §4] | Compile-only. The DRS sequence runs against `FakeNvApi`. Lab: PresentMon shows a flip mode on the GL path |
| S4 | **Linux vblank truth on GL:** `GLX_OML_sync_control` (`glXGetSyncValuesOML` → UST/MSC, `glXGetMscRateOML`) feeds `VblankClock` when the calls *work*. The extension string alone is not evidence: Xvfb here lists it, but both calls return False [A5] | The detector requires calls that succeed and an MSC that advances across 3 swaps. Here that is tested for real: Xvfb is refused and the fallback runs. Lab on a real X server |
| S5 | **`D3DKMTGetScanLine` sampler** (`gdi32.dll`): scan-out phase samples at a QPC instant, cross-checking `SyncQPCTime` | Compile-only. The arithmetic is tested with scripted scanlines |

### Later (named so nothing is silently dropped)

| # | Item |
|---|---|
| L1 | One render thread per adapter, for multi-GPU nodes. This round, outputs on a second adapter are refused with words |
| L2 | Native D3D11 hot path (`present`, `output`, `yuv`, `blit`) behind an extracted `IGpuDevice`. Gated by lab measurements: ANGLE CPU over 1 ms per output per frame, a missing extension, or a vendor bug [P §10.3] |
| L3 | Composition swapchain API (target-time presents); `IDXGISwapChainMedia::SetPresentDuration` for 23.976 cadence (R2.10) [P §2.7, §11.5] |
| L4 | HDR signalling: HDR10 and scRGB (M21) |
| L5 | macOS Metal with `CAMetalDisplayLink`; Vulkan `VK_EXT_present_timing` on Linux [P §9] |
| L6 | Per-output content time for outputs whose refresh differs from the pacer's. This round they show the pacer's frame (§9) |
| L7 | macOS main-thread render host hook (carry-over) |

---

## 3. Architecture

### 3.1 Components and where they live

```
PlayMap.Render
  Gpu/GlDialect.cs            GlDialect, the per-dialect format/extension table, AliasLoader
  Gpu/GlDevice*.cs            (existing) + Dialect: shader header, formats, BGRA swizzle, timer/debug aliases
  Platform/IGpuPlatform.cs    the platform seam: device, monitors, presenters, frame wait, device share
  Platform/GlPlatform.cs      today's GLFW hidden context + OutputWindow (Core330, or Es300 via GLFW ES context)
  Platform/OutputWindow.cs    (existing) implements IOutputPresenter
  Present/IOutputPresenter.cs the presenter seam, PresentResult, PresentSnapshot
  Present/PresentClock.cs     VblankClock + PresentLedger + counters per output (render thread)
  Present/DelayRing.cs        per-output whole-frame delay (latency alignment)
  Timing/VblankClock.cs       the fit (pure)
  Timing/PresentLedger.cs     present ID → expected refresh (pure)
  Timing/PresentMonCsv.cs     PresentMon CSV → per swap chain mode fractions (pure)
  Platform/Windows/           [SupportedOSPlatform("windows")], all compile on Linux
    D3D11Adapter.cs           adapter choice, ID3D11Device, multithread protection, device-lost reason
    AngleEgl.cs               ~25 EGL entry points resolved from av_libglesv2.dll's handle, constants
    AnglePlatform.cs          IGpuPlatform on D3D11 + ANGLE (Es300)
    AngleBackBuffer.cs        back buffer ↔ GL framebuffer (EGLImage route; pbuffer route)
    DxgiOutputWindow.cs       Win32 popup + swap chain + present + statistics: IOutputPresenter
    DxgiNative.cs             the few DXGI/DWM/user32/gdi32 calls Silk.NET lacks (LibraryImport)
    NvDrs.cs                  (S3) NVAPI DRS stop-gap
    PresentMonProbe.cs        (S1) runs PresentMon, hands its CSV to PresentMonCsv
PlayMap.Engine
  Rendering/RenderHost.cs     (existing) builds its platform through IGpuPlatform; waits through it
  Rendering/RenderHost.Present.cs   IPresentBackend: STATE rows, PROVE, RESET, device-lost rebuild
  Rendering/PresentFuse.cs    the fuse file in the node home
  EngineCore.Present.cs       the verbs
PlayMap.Core
  Media/GpuDeviceShare.cs     the device handed to Media (codecs) and Spout (videoio) without Render types
```

Module fences hold:
- Render adds `Silk.NET.Direct3D11` and `Silk.NET.DXGI` 2.23.0. They are the same binding family Render already uses.
- ANGLE is loaded by file name at run time. Render takes no Avalonia package reference.
- Media receives the device as a Core record of handles (`GpuDeviceShare`). Render never sees FFmpeg.

### 3.2 The GL dialect (M1)

Measured here [A1, A2]: Mesa llvmpipe offers OpenGL ES 3.2 with `EXT_color_buffer_float`, `EXT_color_buffer_half_float`, `EXT_texture_norm16`, `OES_texture_float_linear`, BPTC/RGTC/S3TC, `EXT_disjoint_timer_query`, `EXT_read_format_bgra`, `EXT_texture_format_BGRA8888`, `OES_EGL_image(_external)` and `EXT_buffer_storage`. It does **not** offer `ANGLE_polygon_mode` or `NV_polygon_mode`. Swapping `#version 330 core` for `#version 300 es` plus precision statements compiles and links **24 of 24** programs.

| Concern | `Core330` (today) | `Es300` (ANGLE on Windows; Mesa in CI) |
|---|---|---|
| Shader header | `#version 330 core\n` | `#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\n` (`ShaderLibrary.Source(name, dialect)` swaps the first line only) |
| Function loading | `GL.GetApi(window)` | `GL.GetApi(AliasLoader.Wrap(eglGetProcAddress))`. Silk.NET 2.23's `GL.GetApi(Func<string, nint>)` exists [A3]. The alias loader tries `name`, then `name+"EXT"`, `"KHR"`, `"OES"`, `"ANGLE"` |
| BGRA upload (`UploadFormat.Bgra8`) | `GL_BGRA` into `GL_RGBA8` | Upload as `GL_RGBA`/`UNSIGNED_BYTE` and set `TEXTURE_SWIZZLE_R = BLUE`, `TEXTURE_SWIZZLE_B = RED` on that texture. This is ES 3.0 core, so no extension and no copy |
| BGRA read-back | `glReadPixels(GL_BGRA)` | Needs `EXT_read_format_bgra` (Mesa yes; ANGLE sets it [P §5.1]). Otherwise read `GL_RGBA` and swap on the CPU (counted in STATE `machine.present.words`) |
| `R16`/`Rg16` (10-bit software video) | core | Needs `EXT_texture_norm16` (`GL_R16_EXT`). Otherwise the software-decoded P010 path shifts to 8 bits and says so on the source (`videoQuality: "8-bit fallback"`). The D3D11VA planes from codecs WP do not need it |
| `Rgba16F` render target | core | Needs `EXT_color_buffer_half_float` or `EXT_color_buffer_float`. If neither is present, the projector pass falls back to `Rgba8`, with words |
| `R32F` linear sampling | core | Needs `OES_texture_float_linear`. Otherwise `NEAREST` |
| GPU timer (`GpuTimer`) | `GL_TIME_ELAPSED`, `glGetQueryObjectui64v` | `EXT_disjoint_timer_query`: same enum `0x88BF`, `glGetQueryObjectui64vEXT` through the alias. A sample is discarded when `GL_GPU_DISJOINT_EXT (0x8FBB)` reads true |
| Debug output | `glDebugMessageCallback` | `glDebugMessageCallbackKHR` through the alias (KHR_debug) |
| Wireframe (`SetWireframe`) | `glPolygonMode` | `glPolygonModeANGLE` (`GL_ANGLE_polygon_mode`) when listed. Otherwise `GpuCaches` builds a line index buffer once per mesh and the editor draws `GL_LINES`. The editor's wireframe is the only user |
| Version parse | `"4.6 …"` | `"OpenGL ES 3.0 (ANGLE …)"`. `GlDevice.GlVersion` strips the `OpenGL ES ` prefix. `GlDevice.Dialect` is published |
| 10-bit target | `GL_RGB10_A2` | `GL_RGB10_A2`, colour-renderable in ES 3.0 core |

`GlDeviceOptions` gains `GlDialect Dialect = GlDialect.Core330`. `RenderContext.CreateHidden` gains `GlDialect`. On Linux, `Es300` asks GLFW for `ContextAPI.OpenGLES 3.0`. Xvfb's GLX here offers `GLX_EXT_create_context_es2_profile` and an "OpenGL ES 3.2 Mesa" profile [A6], so GLFW's ES context is available under Xvfb. That is how CI runs the ES leg: `GlFixture` reads `PLAYMAP_GL_DIALECT`. If a runner's GLX refuses, the fixture makes the ES context through EGL surfaceless, the route A1 used.

### 3.3 The presenter and platform seams (M2)

`RenderEngine` today calls `OutputWindow.Present(picture)` and reads `LastSwapNs`. After this change:
- `RenderEngine.Attach(string outputId, IOutputPresenter presenter)` replaces `Attach(string, OutputWindow)`.
- Presenting goes through `IOutputPresenter.Present`.
- `OutputState.Pacer` (a `FramePacer`) becomes `OutputState.Clock` (a `PresentClock`).
- `RenderHost` gets its context, monitors and presenters from `IGpuPlatform`. It never constructs `Silk.NET.Windowing` objects itself.

Signatures are in §4.4.

### 3.4 Windows bring-up (M4): the exact sequence

All of this runs on the render thread. Every COM pointer is owned by the object that made it and released in `Dispose`, in reverse order.

1. **Adapter.**
   - `CreateDXGIFactory2(0, IID_IDXGIFactory6)`.
   - Enumerate `IDXGIAdapter1` and its `IDXGIOutput`s. Match each output's `DXGI_OUTPUT_DESC.DeviceName` (`\\.\DISPLAY2`) against the outputs' `DisplayMatch` keys. The `MonitorInfo` list on Windows carries `DeviceName`, from `EnumDisplayMonitors` + `GetMonitorInfoW`.
   - Choose the adapter of the first enabled Display output.
   - An output whose monitor sits on another adapter is refused: "Right is on a second GPU (Intel UHD); this node renders on one GPU — move its cable to the NVIDIA card" (L1).
2. **Device.**
   - `D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT, [11_1, 11_0], D3D11_SDK_VERSION, …)`. In debug builds, add `D3D11_CREATE_DEVICE_DEBUG` if the SDK layers exist, and retry without it.
   - `QueryInterface(ID3D10Multithread).SetMultithreadProtected(TRUE)`. Decode threads (codecs WP) share the device [C §8.2].
3. **ANGLE.**
   - `NativeLibrary.TryLoad("av_libglesv2", typeof(AnglePlatform).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory, out handle)`. `Avalonia.Angle.Windows.Natives 2.1.27548.20260419` ships only `runtimes/win-{x64,x86,arm64}/native/av_libglesv2.dll` (checked in the NuGet cache), with the EGL entry points in the same DLL; every `egl*` function is resolved from that handle with `NativeLibrary.GetExport`. A test compares the loader's names with the package's asset list.
   - `eglCreateDeviceANGLE(EGL_D3D11_DEVICE_ANGLE = 0x33A1, device, null)`.
   - `eglGetPlatformDisplayEXT(EGL_PLATFORM_DEVICE_EXT = 0x313F, eglDevice, null)`, then `eglInitialize`.
   - Require EGL extensions from **three lists**, naming the missing one in the refusal: the client extensions, `eglQueryString(EGL_NO_DISPLAY, EGL_EXTENSIONS)` ⊇ {`EGL_EXT_platform_base`, `EGL_EXT_platform_device`, `EGL_EXT_device_query`, `EGL_ANGLE_device_creation`, `EGL_ANGLE_device_creation_d3d11`} (checked before `eglCreateDeviceANGLE`); the device extensions, `eglQueryDeviceStringEXT(dev, EGL_EXTENSIONS)` ⊇ {`EGL_ANGLE_device_d3d11`}; the display extensions, `eglQueryString(dpy, EGL_EXTENSIONS)` ⊇ {`EGL_ANGLE_d3d_texture_client_buffer`, `EGL_KHR_surfaceless_context`}, with `EGL_ANGLE_image_d3d11_texture` optional (route A). (`EGL_ANGLE_device_d3d11` is a device extension and never appears in the display's string.)
   - **Verify ANGLE is on our device:** `eglQueryDisplayAttribEXT(EGL_DEVICE_EXT)` → `eglQueryDeviceAttribEXT(EGL_D3D11_DEVICE_ANGLE)` must equal our pointer. If not, refuse: "ANGLE made its own device".
   - `eglBindAPI(EGL_OPENGL_ES_API)`.
   - Choose a config with `EGL_RED/GREEN/BLUE/ALPHA_SIZE 8` and `EGL_SURFACE_TYPE ⊇ EGL_PBUFFER_BIT`. A second config at 10/10/10/2 serves 10-bit back buffers.
   - `eglCreateContext(dpy, cfg, EGL_NO_CONTEXT, [EGL_CONTEXT_MAJOR_VERSION 3, EGL_CONTEXT_MINOR_VERSION 0, (debug) EGL_CONTEXT_OPENGL_DEBUG EGL_TRUE, EGL_NONE])`.
   - `eglMakeCurrent(dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, ctx)`.
4. **GL.** `GL.GetApi(AliasLoader.Wrap(eglGetProcAddress))`, then `new GlDevice(gl, options with { Dialect = Es300 })` and `RenderEngine.WarmUp()`. All 24 programs are translated by ANGLE once at start. The node's renderer name becomes `"Direct3D 11 + ANGLE · NVIDIA RTX A4000 · 596.86"`.
5. **One window and swap chain per Display output** (`DxgiOutputWindow`):
   - **Window.** `RegisterClassExW("PlayMapOutput", CS_OWNDC, WndProc)`, then `CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, …, WS_POPUP | WS_VISIBLE, monitor.X, monitor.Y, monitor.W, monitor.H)` and `DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED = 3, TRUE)`. `WndProc` answers `WM_ERASEBKGND` with 1, `WM_SETCURSOR` by hiding the cursor, and `WM_CLOSE` by setting `IsClosing`. It never blocks.
   - **Swap chain.** `IDXGIFactory2::CreateSwapChainForHwnd(device, hwnd, &desc1, null, null, out sc1)`, with `desc1`:
     - `{Width, Height}` = the monitor's pixels
     - `Format = DXGI_FORMAT_B8G8R8A8_UNORM`, or `R10G10B10A2_UNORM` for 10-bit
     - `SampleDesc {1, 0}`
     - `BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT`
     - `BufferCount = 3` [P §2.8]
     - `Scaling = DXGI_SCALING_NONE`
     - `SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD (4)`
     - `AlphaMode = DXGI_ALPHA_MODE_IGNORE`
     - `Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT (0x40)`
     - No `ALLOW_TEARING`: projection never tears [P §2.4].
   - **Setup calls.**
     - `factory.MakeWindowAssociation(hwnd, DXGI_MWA_NO_WINDOW_CHANGES | DXGI_MWA_NO_ALT_ENTER)`
     - `IDXGISwapChain2::SetMaximumFrameLatency(1)` and `GetFrameLatencyWaitableObject()`
     - 10-bit only: `IDXGISwapChain3::CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709)`, then `SetColorSpace1`.
     - `sc.GetContainingOutput()` → `IDXGIOutput6::GetDesc1()` gives `BitsPerColor` and `ColorSpace`. If the containing output is not the monitor we placed it on, refuse with words.
     - The nominal refresh comes from the active path: `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` → `DISPLAYCONFIG_PATH_TARGET_INFO.refreshRate {Numerator, Denominator}` (for example 60000/1001), with `targetVideoSignalInfo.vSyncFreq` for the actual timing. (`IDXGIOutput::FindClosestMatchingMode` answers the supported mode nearest a request, not the active one.)
   - **First frame.** Wait on the waitable before it (the Godot lesson [P §2.3]).
6. **Back buffer → GL framebuffer** (`AngleBackBuffer`). In Direct3D 11 only buffer 0 is accessible for `DISCARD` and `FLIP_DISCARD`: the runtime renames buffer 0's storage on each `Present`, so `GetBuffer(0)` returns the same `ID3D11Texture2D` and a view or EGL wrapper made on it stays valid (the rotating `GetCurrentBackBufferIndex` is Direct3D 12's). Buffer 0 is wrapped **once per swap chain**, again after `ResizeBuffers` or device loss, and the cache asserts it holds exactly one entry, so a driver or OS change is caught. Two routes:
   - **Route A (preferred when `EGL_ANGLE_image_d3d11_texture` is listed):** `eglCreateImageKHR(dpy, EGL_NO_CONTEXT, EGL_D3D11_TEXTURE_ANGLE = 0x3484, tex, [EGL_NONE])` → `glEGLImageTargetTexture2DOES` → an FBO. Everything stays in one surfaceless context, with no `eglMakeCurrent` per output. Whether Avalonia's ANGLE build lists it is **U**: this is lab test 2.
   - **Route B (production-proven [P §5.2]):** `eglCreatePbufferFromClientBuffer(dpy, EGL_D3D_TEXTURE_ANGLE = 0x33A3, tex, cfg, [EGL_WIDTH w, EGL_HEIGHT h, EGL_NONE])`, then `eglMakeCurrent(dpy, pb, pb, ctx)` and draw to FBO 0. Then make the surfaceless context current again. This is Avalonia's `WrapDirect3D11Texture` pattern.
7. **Present and statistics each frame.** Draw `present` (the y-flip triangle `OutputWindow` already uses) from the output's picture into the back-buffer FBO, then:
   - `glFlush()`. ANGLE and DXGI share the device's immediate context on this thread, so D3D11 orders the draw before the flip. No fence is needed.
   - `IDXGISwapChain1::Present1(1, DXGI_PRESENT_DO_NOT_WAIT, &params0)`.
     - `S_OK` → `ledger.Record(GetLastPresentCount(), targetRefresh)`.
     - `DXGI_ERROR_WAS_STILL_DRAWING` → `held++`. The frame is not presented; the air keeps the previous picture for one refresh, and the next frame is drawn for its own vblank.
     - `DXGI_STATUS_OCCLUDED` → `occluded = true`, and the light goes amber: "something is over Left's display".
     - `DXGI_ERROR_DEVICE_REMOVED`/`RESET` → device loss (§3.12).
   - After all outputs have presented, call `GetFrameStatistics(out DXGI_FRAME_STATISTICS)` once per swap chain. Then `ledger.Observe(stats)` and `clock.AddSample(SyncRefreshCount, SyncQPCTime)`. `DXGI_ERROR_FRAME_STATISTICS_DISJOINT` calls `clock.Disjoint()`.

**Why `DO_NOT_WAIT`.** The render thread already waited on the waitable object, so a present normally has queue room. `DO_NOT_WAIT` guarantees that a driver that disagrees never blocks the thread that drives every output.

### 3.5 Pacing and threads (M4)

There is **one render thread**, as today. It owns:
- the D3D11 immediate context, the EGL context and the HWNDs;
- the windows' message pump;
- `OutputWindows`, `RenderEngine` and the previews.

DXGI sends window messages synchronously, so the thread that owns the windows must keep pumping while it waits [P §2.8].

```
loop:
  r = platform.WaitForFrame(pacer, max = 3 × pacer period)
      DXGI: MsgWaitForMultipleObjectsEx(1, &pacer.Waitable, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
            WAIT_OBJECT_0     → Ready
            WAIT_OBJECT_0 + 1 → PeekMessageW/TranslateMessage/DispatchMessageW until empty; wait again for the rest
            WAIT_TIMEOUT      → TimedOut (stalls++, journal once per minute); render anyway: never stall the air
      GL:   NotPaced (the pacing window's SwapBuffers blocks, as today)
  ready[o] = WaitForSingleObjectEx(o.Waitable, 0, FALSE) == WAIT_OBJECT_0   for every non-pacer DXGI output
  lead = pacer.Clock.NextVblankMonoNs(now) − now              (VblankClock; FramePacer on GL)
  leaderNs = engine.LeaderNowNs + lead + airLatencyNs          (§3.8)
  renderer.Render(inputs)                                      draws every Final (unchanged)
  for each attached output o (pacer first):
      if !ready[o] → o.Held++ ; continue                       (no block, no tear)
      o.Present(delayRing[o].Current)                          (§3.4 step 7)
  for each DXGI output: GetFrameStatistics → ledger/clock
  previews, scan stamps, health (unchanged)
```

- The **pacer** is the first attached Display output, as today (`RenderEngine.RepickPacer`).
- Non-pacer outputs at the same nominal rate present with `SyncInterval 1` when their queue has room. Each shows a new frame at its own vblank. Phase drift between them shows up as an occasional held or repeated frame. That is the honest "±1 refresh without lock", now counted per output. Today's `SwapInterval 0` on non-pacers can tear on GL. The flip model never tears.

### 3.6 `VblankClock`: vblank truth (M3)

Input samples are `(k, t)` pairs:
- `k` is the refresh count, unwrapped from u32 to u64 with unsigned deltas.
- `t` is the vblank instant on `MonoClock`, in ns. `SyncQPCTime` is converted as `(qpc / f) * 1_000_000_000 + (qpc % f) * 1_000_000_000 / f` with `f = Stopwatch.Frequency` (QPC on Windows [P §1]); the plain product overflows Int64 after a day of uptime at 10 MHz. A test converts qpc = 2^62.
- Only a strictly newer `SyncRefreshCount` is added, since the scheduler samples once per vblank or less.

The fit:
- **Two windows.** A **short** window of 240 samples (4 s at 60 Hz) gives phase. A **long** window of 3,600 samples (60 s) gives period and ppm. Both use running sums of centred values `(k − k₀, t − t₀)` in `double`. The base is re-centred when the window rolls past it, so precision never degrades.
- **Least squares.** `t = a + b·k`. The period is `b` (published in ps as an integer). The phase is `a`.
- **Robust pass.** Samples with |residual| > 0.25·b are dropped before the refit, and `outliers` is counted.
- **Accuracy (E).** With σₜ = 50 µs of scheduler jitter, the period's standard error is σₜ·√(12/N³). At N = 240 that is 47 ns (2.8 ppm); at N = 600, 11.8 ns (0.71 ppm); at N = 3,600, 0.8 ns (0.05 ppm). The phase predicted at the end of a 240-sample window has error ≈ 2σₜ/√N ≈ 6.5 µs.
- **Prediction.** `IndexAt(t) = floor((t − a)/b)`. `NextVblank(t) = a + b·(IndexAt(t) + 1)`. `TimeOf(k) = a + b·k`.
- **ppm.** `(b − T_nominal)/T_nominal × 10⁶`, where `T_nominal` comes from the mode's rational refresh.
- **States.**
  - `settling`: fewer than 30 samples, or just after `Disjoint()`.
  - `settled`.
  - `stale`: no new sample for 2 periods while presents are accepted. On systems with the hardware flip queue, Microsoft notes `DwmFlush` may be needed before statistics update [P §2.5]. `DwmFlush` blocks its caller until the next composition, so it is **never called on the render thread** (which drives every output): a helper thread calls it at most once a second to nudge the compositor and never gates a frame. If still stale the clock falls back to swap-return samples, and the evidence drops to `none`. A test with a fake platform fails if the render thread calls a blocking DWM function.
- **GL path.** `FramePacer`'s swap-return instants feed the same fit, with `k` counted from swap returns. The phase is then *biased* (swap return ≠ vblank), so STATE says `"phase":"approximate"`.

### 3.7 `PresentLedger`: missed vblanks from the display's side (M3)

This is Microsoft's algorithm [P §2.5], made exact and allocation-free.

**On present:**
- `id = GetLastPresentCount()`.
- `expected = clock.IndexAt(now) + 1`. With latency 1, this frame flips at the next vblank.
- Store `(id, expected)` in a 64-slot ring indexed by `id mod 64`.

**On statistics** `(PresentCount p, PresentRefreshCount r)`, meaning the image of present `p` was first shown at refresh `r`:
- `late = r − expected(p)`. If `late > 0`: `glitches++` and `missedVblanks += late`. The previous image stayed up `late` extra refreshes.
- Gaps between the previous `(p₀, r₀)` and `(p, r)`:
  - `repeats += max(0, (r − r₀) − (p − p₀))`: refreshes with no new image, whatever the cause.
  - `discarded += max(0, (p − p₀) − (r − r₀))`: presents replaced before display. This should stay 0 at sync interval 1.

**Measured latency.** `latencyFrames` is the median over the window of `r − IndexAt(presentTime)`. It is 1 in independent flip with latency 1. Composition usually adds one, which gives the second flip-mode hint (S1).

**Counters for STATE:**
- since the output opened: `presented`, `missedVblanks`, `glitches`, `repeats`, `held`, `stalls`;
- over the last 60 s: `missed60s`, from a 60-bucket ring of per-second counts;
- `statsAgeMs`.

**Wrap.** `PresentCount` is a `UINT`. At 60 Hz it wraps after about 828 days [P §2.5]. Every subtraction is unsigned, and a test drives it across the wrap.

### 3.8 Per-output latency and the delay ring (M8)

- **Input.** `Output.LatencyMs` (JSON `latencyMs`, default 0 = unknown) is the projector's input lag. An operator sets it, or `LATENCY MEASURE` measures it (wired in the framelock WP).
- **Air latency.** `L_air = max(LatencyMs over enabled outputs on this node)`. The framelock WP may raise it to the group's maximum, so every node in a sync group targets the same light instant. The frame is drawn for `leaderNs = now + lead + L_air`.
- **Delay ring.** Output `o` presents the final picture drawn `d_o = round((L_air − L_o)/T)` frames ago. `DelayRing` holds `d_o + 1` render targets of the output's format and swaps pointers only. The frame path stays allocation-free. The output stage draws into the ring's head instead of `Final`, and `Final` aliases the newest entry.
- **Memory.** At most `d ≤ 6` frames: more is refused with words, because a latency difference above 100 ms at 60 Hz is a configuration error. A 4K RGBA8 ring of 6 is 6 × 33 MB.
- **Residual.** `latencyResidualMs = (L_air − L_o) − d_o·T` is published; |residual| ≤ T/2.
- **Previews.** Output previews show the picture presented this frame (the ring's tail), so what the desk shows is what went to the projector.
- **Carry-over: offscreen dropped.** An output with no presenter (offscreen) reports `paced: "offscreen"`. Its light is green when it renders. A headless node on llvmpipe at 34 fps no longer reads amber. Outputs that feed a stream (videoio WP) are judged against their stream's rate, which is that package's measure.
- **Carry-over: scan present stamp.** `ScanFrames.AfterFrame` receives `Func<string, PresentStamp?>`:
  - **DXGI:** the stamp is `clock.TimeOf(PresentRefreshCount)` once the ledger has seen that present displayed (1–2 frames later). The scan waits for it.
  - **GL window:** the swap return after `Finish()`.
  - **Offscreen:** the instant a fence inserted right after that output's draw is seen signalled.

  No read-back can then land before its stamp.

### 3.9 10-bit (M6)

- `TextureFormat.Rgb10A2` → `GL_RGB10_A2` / `GL_RGBA` / `GL_UNSIGNED_INT_2_10_10_10_REV`. It is renderable in both dialects.
- `RenderEngine.EnsureFinal` uses `Rgb10A2` when `output.ColorBits == 10`.
- Output shader hunk (`OutputFs`):
  - add `uniform float uCodes;`
  - replace `(uTone.w / 255.0)` with `(uTone.w / uCodes)`
  - `OutputStage.Draw` sets `uCodes` to 255 or 1023.
- **Read-back.** `ReadOutput` (BGRA8) still works for 10-bit by quantising. `ReadOutput10(outputId, Span<uint>)` is added for tests and the 10-bit ramp check.
- **Windows.** The swap chain uses `R10G10B10A2_UNORM` with G22/P709 [P §2.9]. ANGLE wraps it (supported since v4 of the client-buffer extension [P §5.1]). STATE publishes `bits` (requested) and `bitsReported` (`DXGI_OUTPUT_DESC1.BitsPerColor`). If `bitsReported < bits`, the light is amber: "10-bit asked, the output reports 8 — check the cable and the GPU's output colour depth".
- **GL windows on Linux.** A 10-bit default framebuffer needs a depth-30 X visual. This is *should*, lab-tested. Xvfb here runs at 24.

### 3.10 Independent-flip evidence (S1) and the mode

`present.mode` is one of:

| Value | Meaning |
|---|---|
| `iflip` | Hardware: Independent Flip |
| `iflip-overlay` | Hardware Composed: Independent Flip |
| `composed` | Composed: Flip |
| `composed-copy` | Composed: Copy (GDI paths) |
| `gl` | The driver's own GL path, mode unknown |
| `offscreen` | No presenter |
| `unknown` | — |

`present.evidence` is one of:

| Value | Where it comes from |
|---|---|
| `proven` | `PRESENT PROVE` in the last 24 h, matched by **swap-chain address**. PresentMon's CSV has a `SwapChainAddress` column, and the node knows each `IDXGISwapChain*` value [U: column names checked against PresentMon 2.6's README at implementation, parser keyed by header]. It holds while the output, its monitor and the window are unchanged. Any change drops it to `hint` |
| `hint` | `IDXGISwapChainMedia::GetFrameStatisticsMedia().CompositionMode`. `OVERLAY = 1` or `NONE = 2` → not composed; `COMPOSED = 0` → composed. Whether this reports reliably for RGB swap chains is **U** [P §2.5]. Corroborated by `latencyFrames` (1 means iflip-like, 2 or more means composed-like) |
| `none` | — |

**Negative evidence, continuous:**
- `DXGI_STATUS_OCCLUDED` from Present.
- `GetForegroundWindow()` on our monitor and not ours. This is checked once a second, never in the frame path.

**`PRESENT PROVE`** runs PresentMon 2.x (MIT). It looks for the exe in `NodeSettings.PresentMonPath`, then beside PlayMap's exe, then on `PATH`. The command line is:

```
PresentMon.exe --process_id <pid> --output_file <tmp.csv> --timed <s> --terminate_after_timed --stop_existing_session --no_console_stats
```

ETW needs admin or the *Performance Log Users* group. When it is refused, the answer is the exact words and the fix. `PresentMonCsv` groups rows by `SwapChainAddress` and computes each `PresentMode` fraction. An output is `proven iflip` when ≥ 99.5 % of its displayed frames are `Hardware: Independent Flip` or `Hardware Composed: Independent Flip` [P §2.10].

### 3.11 The fuse and the path choice (M7)

- **Choosing the path.** `NodeSettings.PresentPath`:
  - `auto` means **GL on every platform in round 2**; it becomes "DXGI on Windows when the device and ANGLE start" only after the WARP CI job and a lab run on a real GPU pass (a path that presents steadily but wrongly — black, the wrong monitor, the wrong colour space, frozen — is not caught by the fuse);
  - `dxgi` (explicit) is reported in STATE as `"experimental":true` this round;
  - `dxgi` means DXGI or fail with words;
  - `gl` means GL.
- **The fuse.** Before starting the DXGI platform, write `<node home>/playmap.present.fuse` = `{"path":"dxgi","attempt":n,"at":unixMs}`. Delete it once every Display output has reached 120 settled frames.
- **On start:** if the fuse exists with `attempt ≥ 1`, start GL. Raise fault `present` with: "the DXGI path did not reach its first frames last start (n attempts, at 21:04); running OpenGL. PRESENT PATH DXGI tries it again." This is Patterns' fuse [P §1, §10.5].
- **Other platforms.** On Linux and macOS, `auto` is GL. The words say so once in `machine.present.why`.

### 3.12 Device loss (S2)

**Triggers:**
- DXGI: `DXGI_ERROR_DEVICE_REMOVED`/`RESET` from `Present1` or `GetFrameStatistics`, with the reason from `ID3D11Device::GetDeviceRemovedReason`.
- ANGLE: `EGL_CONTEXT_LOST`.
- GL: `GL_CONTEXT_LOST` (KHR_robustness, when present), or the existing 30-fault limit.

**Rebuild on the render thread:**
1. Mark the outputs black in STATE.
2. Dispose the renderer, presenters and platform: everything GPU, in reverse order. HWNDs are kept when the platform allows.
3. Rebuild the platform, then `WarmUp`, then the presenters.
4. Resume.

The stage resources and sources are re-offered: identity caches clear, so meshes and warps upload again.

**Words and limits:**
- STATE `machine.present.resets++` and a journal row: "the GPU reset (DXGI_ERROR_DEVICE_REMOVED: 0x887A0006 hung); outputs back in 1.4 s".
- Three losses in 60 s stop the rebuilds. The outputs stay closed with the reason, and the engine plays on. Today's rule is kept as the last resort.
- **Budget (E):** rebuild ≤ 2 s, of which shader translation is ≤ 300 ms.

### 3.13 The device handed to others (codecs, videoio, framelock)

`IGpuPlatform.Share` returns a Core record (§4.4). `RenderHost` hands it, once the device exists, to:
- `request.Media as IGpuDeviceSink` (the codecs WP implements the sink and binds `AVD3D11VADeviceContext.device`, `lock`/`unlock` [C §8.2]);
- any videoio sink.

`DxgiOutputWindow` exposes its `IDXGISwapChain*` and the device to the framelock WP through `IFrameLockTarget` (§4.4). The framelock driver can then replace `Present1` with `NvAPI_D3D1x_Present`.

### 3.14 Performance budgets (E unless measured)

| Budget | Value | How it is held |
|---|---|---|
| CPU per output per frame on the DXGI present path (Present1 + statistics + ledger) | ≤ 0.3 ms | `RenderHostStats` adds `presentMs`. Lab soak |
| ANGLE translation overhead per output per frame | ≤ 1 ms CPU | The gate for native D3D11 (L2) [P §10.3] |
| GPU present pass | ≤ 0.2 ms at 1080p, ≤ 0.6 ms at 4K | `GpuTimer` around `present` |
| Frame path allocation once warm | 0 bytes (existing invariant) | `FramePathAllocatedBytes` flat on both paths. Statistics structs are fields; COM calls go through Silk `ComPtr` with no boxing |
| Present-to-scan-out in iflip | 1 refresh | `latencyFrames` |
| Delay ring | ≤ 6 frames per output | Refused above |
| Start | DXGI device + ANGLE + 24 programs ≤ 1.5 s | Journalled at start |

---

## 4. Spine needs

### 4.1 Model (Core): backwards compatible

These are added as **trailing optional parameters** on `Output`:

```csharp
public sealed record Output(
    string Id, string Name, OutputKind Kind, string DisplayKey, int Width, int Height, double RefreshHz,
    OutputMode Mode, string? ProjectorId, SourceRef Source, RectD SourceRect, OutputWarp Warp, BlendSettings Blend,
    ColorTrim Color, ImmutableArray<Mask> Masks, RectD Plan, int Rotation, bool Enabled,
    /// <summary>Bits per colour channel to the display: 8, or 10 (a 10-bit final picture and a 10-bit swap chain).</summary>
    int ColorBits = 8,
    /// <summary>The display's input lag (light leaves the lens this long after the vertical blank), ms; 0 = unknown.
    /// The node's frame is drawn for its slowest output; faster outputs wait whole frames.</summary>
    double LatencyMs = 0) : IEntity;
```

- JSON: `"colorBits": 8`, `"latencyMs": 0`. A missing field reads as the parameter default (System.Text.Json honours constructor defaults), so old files read unchanged.
- `SET op_1 colorBits 10` and `SET op_1 latencyMs 33` work through `ProjectJson.SetPath`.
- Edit validation in `EngineCore.Edits.cs` `Validate(Project, object)`, beside the existing raster and rotation checks: `ColorBits ∈ {8, 10}`, `LatencyMs ∈ [0, 500]`. Anything else is refused with words.

### 4.2 `NodeSettings` (machine-local, trailing optional parameters)

```csharp
string PresentPath = "auto",   // auto | dxgi | gl: the path the render host starts its outputs on
string PresentMonPath = "",    // "" = beside the exe, then PATH
```

The fuse is a file in the node home, not a setting.

### 4.3 Verbs (VerbCatalog, `VerbCategory.Output`)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `OUTPUT STATS` | `OUTPUT STATS [<output>\|ALL]` | Low | no | JSON array of present rows (§4.5) |
| `PRESENT RESET` | `PRESENT RESET [<output>\|ALL]` | Low | yes | `OK` with effect "zeroed Left's counters (was 3 missed vblanks, 2 glitches)". The old totals go to the journal. Refused when nothing is counted: "nothing to reset" |
| `PRESENT PROVE` | `PRESENT PROVE [<output>\|ALL] [<seconds 5-120, default 20>]` | Low | yes (runs a process) | Off-lane like the ops verbs. The final reply is the observation: `[{"output":"op_1","mode":"iflip","observed":0.998,"frames":1198,"evidence":"proven","words":"…"}]`. Refused with the reason when PresentMon is not found, ETW is refused, or the path is GL |
| `PRESENT PATH` | `PRESENT PATH AUTO\|DXGI\|GL` | High | yes | `OK` "stored; applies at the render host's next start (now dxgi)". Refused while armed. Refused when the value equals the stored one ("already dxgi") |
| `PRESENT NVLAYER` (S3) | `PRESENT NVLAYER ON\|OFF` | High | yes | Off-lane. The applied answer reads the DRS setting back (observed). Refused on non-NVIDIA, on the DXGI path ("not needed: this node presents through DXGI"), and while armed |

- `PRESENT PROVE`, `PRESENT PATH` and `PRESENT NVLAYER` join the ops-verb set (`EngineCore.Ops.cs` `OpsVerbs`). They are answered off the lane.
- Latency and bits use the generic `SET` (Review), so the desk, Patterns and an AI use the same words.
- Risk depends on the caller as in docs/UX.md §7: an AI's `PRESENT PATH` is a proposal.

### 4.4 Seams (C# signatures)

```csharp
// PlayMap.Render.Gpu
public enum GlDialect { Core330, Es300 }
public sealed record GlDeviceOptions(bool CheckErrors, bool DebugOutput, GlDialect Dialect = GlDialect.Core330);
public static class AliasLoader { public static Func<string, nint> Wrap(Func<string, nint> getProcAddress); }

// PlayMap.Render.Present
public enum PresentPath { Offscreen, GlWindow, DxgiFlip }
public enum PresentMode { Unknown, Gl, Composed, ComposedCopy, IndependentFlip, IndependentFlipOverlay, Offscreen }
public enum ModeEvidence { None, Hint, Proven }
public readonly record struct PresentResult(bool Queued, long PresentId, long TargetRefresh, PresentProblem Problem);
public enum PresentProblem { None, Held, Occluded, DeviceLost, Failed }

/// <summary>Where one output's finished picture goes: a window on a display that presents on its vertical blank.
/// Render thread only; Present never blocks (a full queue answers Held).</summary>
public interface IOutputPresenter : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool IsClosing { get; }
    PresentPath Path { get; }
    /// <summary>The OS wait handle that signals when this output's queue has room (0 when the path has none).</summary>
    nint FrameWaitHandle { get; }
    /// <summary>The vblank truth, the ledger and the counters.</summary>
    PresentClock Clock { get; }
    PresentResult Present(GpuTexture picture);
    PresentResult PresentBlack();
    /// <summary>Reads display-side statistics into Clock (after every output presented this frame).</summary>
    void Observe();
}

public readonly record struct PresentSnapshot(
    PresentPath Path, PresentMode Mode, ModeEvidence Evidence, int BitsRequested, int BitsReported,
    double RefreshHz, double NominalHz, double Ppm, bool Settled, bool PhaseApproximate,
    long Presented, long MissedVblanks, long Missed60s, long Glitches, long Repeats, long Held, long Stalls,
    int LatencyFrames, int DelayFrames, double LatencyResidualMs, double StatsAgeMs, long LastVblankMonoNs,
    bool Occluded, string Words);

// PlayMap.Render.Platform
public sealed record OutputPresenterOptions(string Title, MonitorInfo? Monitor, bool Borderless, int Width, int Height,
    int X, int Y, int ColorBits);
public enum FrameWait { Ready, TimedOut, NotPaced }
public interface IGpuPlatform : IDisposable
{
    string Name { get; }
    RenderContext Context { get; }
    IReadOnlyList<MonitorInfo> Monitors();
    IOutputPresenter CreatePresenter(OutputPresenterOptions options);
    FrameWait WaitForFrame(IOutputPresenter? pacer, TimeSpan max);
    void PumpEvents();
    GpuDeviceShare? Share { get; }
    bool DeviceLost(out string reason);
}
public static class GpuPlatforms
{
    /// <summary>The platform a node starts on: DXGI on Windows when asked or auto and it starts, else GL; the reason
    /// when it fell back.</summary>
    public static IGpuPlatform Create(PresentChoice choice, GlDeviceOptions device, out string why);
}
public sealed record PresentChoice(string Path /* auto|dxgi|gl */, bool FuseBlown);

/// <summary>What the framelock driver needs of a DXGI output (framelock WP).</summary>
public interface IFrameLockTarget
{
    nint D3DDevice { get; }
    nint SwapChain { get; }
    /// <summary>Replaces the plain Present1 for this output (NvAPI_D3D1x_Present while joined); null restores it.</summary>
    Func<uint, uint, int>? PresentOverride { get; set; }
}

// PlayMap.Core.Media (Core, no Render types)
public enum GpuApi { None, D3D11, OpenGl }
public interface IGpuDeviceLock { void Enter(); void Exit(); }
public sealed record GpuDeviceShare(GpuApi Api, nint Device, long AdapterLuid, IGpuDeviceLock Lock);
public interface IGpuDeviceSink { void Attach(GpuDeviceShare? share); }   // codecs/videoio implement

// PlayMap.Engine (Seams.cs, additive; IOutputBackend unchanged)
public sealed record OutputPresentView(string OutputId, string Path, string Mode, string Evidence, int Bits,
    int BitsReported, double RefreshHz, double Ppm, bool Settled, long MissedVblanks, long Missed60s, long Glitches,
    long Held, int LatencyFrames, int DelayFrames, double LatencyResidualMs, double StatsAgeMs, bool Occluded, string Words);
public sealed record MachinePresentView(string Api, string Adapter, string Driver, string Angle, string Path,
    string Why, long Resets, double PresentMsP95);
public sealed record PresentProof(string OutputId, string Mode, double Observed, int Frames, string Words);
public interface IPresentBackend
{
    IReadOnlyList<OutputPresentView> Present();
    MachinePresentView Machine();
    Task<IReadOnlyList<PresentProof>> ProveAsync(IReadOnlyList<string> outputIds, TimeSpan length, CancellationToken ct);
    int Reset(string? outputId);
}
```

- `RenderHost` implements `IPresentBackend`. `StateWriter` and `ContextBuilder` test `Services.Outputs is IPresentBackend`, so fakes and `NullBackends` need no change.
- `RenderHostRequest` is unchanged. The fuse lives in `request.Home`, and `PresentPath` is in `request.Settings`.

### 4.5 STATE and context keys

`outputs[]` gains these keys (existing keys unchanged):

```json
{"id":"op_1", "...":"...", "colorBits":10, "latencyMs":33.0,
 "present":{"path":"dxgi-flip","mode":"iflip","evidence":"proven","bits":10,"bitsReported":10,
   "refreshHz":59.999823,"nominalHz":60,"ppm":-2.95,"settled":true,"phase":"exact",
   "presented":215990,"missedVblanks":0,"missed60s":0,"glitches":0,"repeats":0,"held":0,"stalls":0,
   "latencyFrames":1,"delayFrames":0,"latencyResidualMs":0.0,"statsAgeMs":16.7,"occluded":false,
   "words":"59.999823 Hz · independent flip (PresentMon) · 0 missed"}}
```

`machine` gains `"present":{"api":"d3d11+angle","adapter":"NVIDIA RTX A4000","driver":"596.86","angle":"2.1.27548","path":"dxgi","why":"","resets":0,"presentMsP95":0.21}`.

**The output light** (`StateWriter.WriteOutput`, `ContextBuilder`) is the first rule that applies:

| Light | When |
|---|---|
| grey | disabled or virtual |
| red | not open; or `statsAgeMs > 1000` on dxgi; or `mode = composed` with `evidence = proven` |
| amber | `missed60s > 0`; or `occluded`; or `bitsReported < bits`; or (dxgi and settled and `evidence = none` after 60 s); or `delayFrames` refused |
| green | everything else |

Offscreen outputs are green when rendering (M8).

**The context item** `output:<id>` takes the present words as its state, for example "59.999823 Hz · independent flip · 0 missed". Patterns' Eye shows it as is. `machine` faults (fuse, path fallback, device resets) are red items `present`.

### 4.6 MCP

The verbs are MCP tools through the catalog, unchanged. The STATE resource carries the rows. `OUTPUT STATS` is the narrow read an assistant uses to explain a judder: "Left missed 3 vblanks in the last minute while the media decode queue ran dry".

---

## 5. Files

### New files

| File | Notes |
|---|---|
| `src/PlayMap.Render/Gpu/GlDialect.cs` | — |
| `src/PlayMap.Render/Platform/IGpuPlatform.cs`, `GlPlatform.cs` | — |
| `src/PlayMap.Render/Present/IOutputPresenter.cs`, `PresentClock.cs`, `DelayRing.cs`, `PresentTypes.cs` | — |
| `src/PlayMap.Render/Timing/VblankClock.cs`, `PresentLedger.cs`, `PresentMonCsv.cs` | — |
| `src/PlayMap.Render/Platform/Windows/D3D11Adapter.cs`, `AngleEgl.cs`, `AnglePlatform.cs`, `AngleBackBuffer.cs`, `DxgiOutputWindow.cs`, `DxgiNative.cs`, `PresentMonProbe.cs`, `NvDrs.cs` | Every type is `[SupportedOSPlatform("windows")]`. Entry points are guarded by `OperatingSystem.IsWindows()`, so CA1416 holds the fence |
| `src/PlayMap.Core/Media/GpuDeviceShare.cs` | — |
| `src/PlayMap.Engine/Rendering/RenderHost.Present.cs`, `PresentFuse.cs` | — |
| `src/PlayMap.Engine/EngineCore.Present.cs` | — |
| `tests/PlayMap.Render.Tests/VblankClockTests.cs`, `PresentLedgerTests.cs`, `DialectTests.cs`, `TenBitTests.cs`, `DelayRingTests.cs`, `PresentMonCsvTests.cs` (+ `Fixtures/presentmon-*.csv`) | — |
| `tests/PlayMap.Engine.Tests/Rendering/PresentStateTests.cs`, `PresentFuseTests.cs`, `DeviceLossTests.cs` | — |
| `tests/PlayMap.Render.Windows.Tests/` | A project built everywhere and run by the Windows job (§7.3). Its tests assert Windows behaviour and are not skipped on Linux: the Linux job does not run this project |
| `.github/workflows/build.yml` | Adds the `es` leg (env `PLAYMAP_GL_DIALECT=es`) and a `windows-latest` job (§7.3) |

### Existing files touched (small hunks)

| File | Change |
|---|---|
| `src/PlayMap.Render/PlayMap.Render.csproj` | Two `PackageReference`s (`Silk.NET.Direct3D11`, `Silk.NET.DXGI` 2.23.0) |
| `src/PlayMap.Render/Gpu/GlDevice.cs`, `GlDevice.Resources.cs`, `GpuTypes.cs` | Dialect branches. `TextureFormat.Rgb10A2`. BGRA swizzle. Version parse |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | `Source(name, dialect)`. `uCodes` in `OutputFs` |
| `src/PlayMap.Render/Platform/RenderContext.cs` | Construct from an existing `GL` + `GlDevice` (the EGL case). `Window` becomes `GlPlatform`-internal |
| `src/PlayMap.Render/Platform/OutputWindow.cs` | Implements `IOutputPresenter` |
| `src/PlayMap.Render/Engine/RenderEngine.cs` | `Attach(IOutputPresenter)`, `PresentClock`, delay ring, `Rgb10A2` final, `ReadOutput10` |
| `src/PlayMap.Render/Passes/OutputStage.cs` | `uCodes` |
| `src/PlayMap.Render/Timing/FramePacer.cs` | Becomes a `VblankClock` sample source. `OutputStats` gains fields |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | Platform creation through `GpuPlatforms.Create`; `WaitForFrame`; air latency; device-loss hook |
| `src/PlayMap.Engine/Rendering/OutputWindows.cs` | Presenters, not `OutputWindow` |
| `src/PlayMap.Engine/Rendering/ScanFrames.cs` | `PresentStamp` |
| `src/PlayMap.Engine/Seams.cs` | The three records and `IPresentBackend` |
| `src/PlayMap.Engine/StateWriter.cs` | `present` row; `machine.present` |
| `src/PlayMap.Engine/ContextBuilder.cs` | Light rule; words |
| `src/PlayMap.Engine/EngineCore.Router.cs` | Four dispatch lines |
| `src/PlayMap.Engine/EngineCore.Ops.cs` | `OpsVerbs` set |
| `src/PlayMap.Core/Model/OutputModel.cs` | Two trailing parameters |
| `src/PlayMap.Engine/EngineCore.Edits.cs` | `Validate`: two rules |
| `src/PlayMap.Core/Bus/Pairing.cs` | `NodeSettings`, two trailing parameters |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | Five verbs |
| `docs/WIRE.md`, `docs/ARCHITECTURE.md` §5, `docs/ROADMAP.md` (R2.1/R2.5 status), `docs/COMPETITIVE.md` (output row) | — |
| `src/PlayMap.App` | Outputs drawer chip, inspector fields and right-click items (§8). The desk reads STATE only |

---

## 6. Dependencies

**Work packages.**
- **codecs** consumes `GpuDeviceShare` and `IGpuDeviceSink` for D3D11VA zero-copy and HAP BC uploads. On ANGLE, BC formats come through ANGLE's D3D11 format tables [P §5.1, C §8.2].
- **videoio** consumes the device for Spout shared textures, and the present clock for send pacing [videoio research §9].
- **framelock** consumes `IFrameLockTarget`, `PresentClock` and the present rows, and owns the group air latency and `LATENCY MEASURE`.
- **showsafety** (Show Mode, preflight, HARDENING):
  - this WP reads `armed` to refuse `PRESENT PATH` and `NVLAYER`;
  - preflight calls `PRESENT PROVE` and reads `missedVblanks` during its soak.
- **fleet** shows `machine.present` per node.
- **companion** reads the variables (§8).

None of these blocks this WP. It lands first.

**Third party** (MIT-compatible; restrictive runtimes loaded at run time):

| What | Version | Licence | How |
|---|---|---|---|
| Silk.NET.Direct3D11, Silk.NET.DXGI | 2.23.0 (NuGet 2026-01-23) | MIT | PackageReference in Render (same family as Silk.NET.OpenGL 2.23.0 already used) [P §3] |
| ANGLE (`av_libglesv2.dll`, EGL and GLES in one file) | Avalonia.Angle.Windows.Natives 2.1.27548.20260419 | BSD-3 | Already shipped beside the exe by the desk's Avalonia 12 graph. Loaded by name at run time (`NativeLibrary.TryLoad`); no new package |
| PresentMon console | 2.6.0 | MIT | A separate process, user-installed or placed beside the exe; never linked |
| NVAPI (`nvapi64.dll`) | Driver's | Driver | Loaded at run time (S3). IDs from the MIT NVAPI SDK header `nvapi_interface.h` [A4] |
| `d3d11.dll`, `dxgi.dll`, `dwmapi.dll`, `user32.dll`, `gdi32.dll` | Windows | OS | — |

Not used: `WGL_NV_DX_interop2` on the present path [P §4]; Vortice and TerraFX (TerraFX is a fallback only if a Silk 2.23 binding of `IDXGISwapChainMedia` or `IDXGIOutput6` proves incomplete).

---

## 7. Verification here (honest)

### 7.1 Tested for real in this container

- **ES dialect on Mesa.**
  - All 24 programs compile and link as ES 3.00 [A1]. This becomes `DialectTests.EveryProgramCompilesInEsAndCore`.
  - The whole `PlayMap.Render.Tests` suite runs under `PLAYMAP_GL_DIALECT=es` through GLFW ES contexts on Xvfb. That includes pixel-exact output-stage tests, generators, video upload, BGRA via swizzle, `R16` via norm16, timer queries via `EXT_disjoint_timer_query`, and the line-index wireframe fallback, since Mesa lacks polygon_mode [A2].
- **`VblankClock`, `PresentLedger`, `PresentMonCsv`.** Pure, with synthetic and fixture data: jitter, ppm, outliers, wrap, disjoint, stale, late presents, discarded presents.
- **10-bit final, dither and `ReadOutput10`.** On Mesa (M6 acceptance).
- **Delay ring and air latency.** Offscreen outputs on Mesa through `RenderHost`: the content frame offset between two outputs is read back by `ReadOutputAsync` with the `pattern.syncmarks` frame bar.
- **Presenter seam refactor.** The existing `RenderHostTests` and `NodeRenderTests` under Xvfb with GL windows.
- **Fuse logic, path choice and STATE/context rows.** With `FakeGpuPlatform`, a platform whose presenters are scripted `IOutputPresenter`s producing chosen `PresentSnapshot`s: iflip, composed, missed vblanks, stale, occluded. The light rules are asserted on fields.
- **Device-loss rebuild on GL.** A test hook (`RenderHost.TestLoseDevice()`, internal) disposes the context between frames. The host rebuilds, the outputs reopen under Xvfb, and the counters and journal rows are asserted.
- **The scan stamp ordering.** `OpsRenderScanTests` is extended: `stamp ≥ readback time` for offscreen and windowed outputs.

### 7.2 Simulated (fakes standing in)

| Fake | Stands in for |
|---|---|
| `FakeGpuPlatform` / `FakePresenter` | The DXGI path's behaviour in the engine: STATE, light, fuse, verbs |
| `ScriptedFrameStatistics` | `DXGI_FRAME_STATISTICS` sequences recorded in the shape Microsoft documents, including first-call `DISJOINT` |
| PresentMon CSV fixtures | Hand-written from the documented column set [P §2.10]. **The exact 2.6 column names are U until checked on Windows**, and the parser is keyed by header names |

### 7.3 Compile-only here, and where it is really tested

**Compile-only here:** everything under `Platform/Windows/` (D3D11, DXGI, ANGLE EGL, Win32, DWM, NVAPI DRS, PresentMon launch). It compiles on Linux because Silk.NET's D3D11 and DXGI bindings are netstandard and the P/Invokes are declarations.

**A `windows-latest` GitHub job.** The runner has no GPU, so it uses `D3D_DRIVER_TYPE_WARP` with `PLAYMAP_D3D_WARP=1`. It:
- builds;
- runs `PlayMap.Render.Windows.Tests`: create the device, start ANGLE on it, verify the device identity, create a 1280×720 flip swap chain on a window, then 600 presents.

It asserts:
- zero faults and zero held presents;
- the ledger matches its own presents;
- STATE reads `path = "dxgi-flip"`.

Frame statistics on WARP or the Basic Display Adapter may be `DISJOINT` or unreliable [U], so the job asserts no flip mode and no ppm. Whether a hosted runner's session allows a visible window is **U**. If it does not, the job runs the same suite with an off-screen composition swap chain (`CreateSwapChainForComposition`) and says so in its name.

**The hardware lab (R0.5) carries the claims:**
- `[P §12]` test 2: the ANGLE extension dump on NVIDIA, AMD and Intel, and route A versus route B;
- test 3: 4 outputs for 60 min, 100 % iflip by PresentMon, `missedVblanks = 0`;
- test 4: `VblankClock` ppm against a photodiode within 2 ppm;
- the 72 h soak (the R2 exit).

Until those run, docs/COMPETITIVE.md keeps saying "designed, not measured".

---

## 8. Desk and Companion surface

### Desk (reads STATE; no private API)

- **Outputs drawer tile.** One chip per output: `FLIP ✓ 59.999 Hz · 0` (green), `FLIP? · 2 missed` (amber, evidence none or hint), `COMPOSED` (red when proven, amber when hinted), `GL` (grey on Linux; amber on Windows with the fallback's reason as its tooltip). The tile's light is the STATE light.
- **Output inspector, Display section:**
  - **Bits** (8 / 10). Sends `SET <id> colorBits 10` and shows `bitsReported` beside it.
  - **Latency (ms)**. Sends `SET <id> latencyMs`. A **Measure** button sends `LATENCY MEASURE` (framelock WP). The delay frames and residual are shown in small type.
  - **Prove flip**: `PRESENT PROVE <id>`, with a 20 s progress bar and the result in words.
- **Right-click on an output tile** (same verbs): Prove flip mode · Reset counters · Identify · Measure latency · Set 10-bit / 8-bit.
- **Network drawer, machine row:** `D3D11 + ANGLE · RTX A4000 · 596.86 · DXGI` or `OpenGL 4.6 · Mesa`. The fuse fault is shown in red with a **Try DXGI again** button (`PRESENT PATH DXGI`, High: confirmation on the desk, a proposal for an AI).
- **The always-visible strip** gets the worst output's missed-vblank count over the last 60 s. It is hidden at 0.

### Companion (the companion WP's module reads STATE)

- **Variables:** `out_<n>_present` (mode word), `out_<n>_hz` (six decimals), `out_<n>_missed` (`missed60s`), `out_<n>_bits`, and `node_present_path`.
- **Feedbacks:**
  - `output_flip_is {output, mode}` (boolean);
  - `output_missed {output}` (value, gauge);
  - `output_light_is` (exists), whose amber now includes missed vblanks.
- **Actions:** `PRESENT PROVE`, `PRESENT RESET`. Show-mode gating comes from STATE's `show.allowed` (showsafety WP).
- **Preset:** a "FLIP" key per output, lit by its light, reading `59.999 Hz · 0`. A press runs `PRESENT PROVE` for it.

---

## 9. Risks and open questions

| Risk or question | Mitigation |
|---|---|
| ANGLE in Avalonia's build may lack route A (`EGL_ANGLE_image_d3d11_texture`) or device creation | Route B is Avalonia's own production path. Device creation is what Avalonia's D3D11 display uses [P §5.2]. Lab test 2 decides. Without either, `auto` falls back to GL with words |
| Frame statistics "are not reliable in many multiple monitor scenarios" [P §2.5] | Stale detection, a `DwmFlush` nudge, and fallback to swap samples (evidence drops to none). S5's scanline sampler cross-checks. The lab test with 4 outputs on one adapter answers it |
| Silk.NET 2.23 bindings for `IDXGISwapChainMedia`, `IDXGIOutput6`, `IDXGIFactory6` may be incomplete | Hand-declared COM vtable calls for the few missing methods (`DxgiNative.cs`), or TerraFX for those only. Layout and slot goldens: `Unsafe.SizeOf` and field offsets of every hand-declared struct and the vtable index of every hand-declared method, against the Windows SDK headers (mingw-w64's when apt has them, else copied once with the citation); a fake `IGpuPlatform` failing at each start stage tests the fuse and the GL fallback |
| Another app's window, a notification or the Windows "Game bar" breaks iflip mid-show | Occlusion and foreground checks turn the light amber at once. HARDENING (showsafety) turns notifications off. PresentMon proof in preflight |
| Mixed refresh rates on one node: one pacer, so outputs at other rates show the pacer's frame and judder | Counted and published (`held`, `repeats`). Words on the tile: "50 Hz output paced by a 60 Hz output". Per-output content time is L6. Preflight (showsafety) refuses mixed rates within a sync group (framelock) |
| A second GPU's outputs are refused this round | Words name the cable fix. L1 adds a thread per adapter |
| ANGLE CPU cost on many outputs | `presentMs` and frame CPU published. The native D3D11 gate (L2) is a measured decision |
| `EXT_texture_norm16` missing on some ANGLE devices | The software P010 path drops to 8 bits with words. D3D11VA planes (codecs) are unaffected |
| A hosted Windows runner may not allow windows | The composition swap chain variant; the self-hosted GPU runner (R0.4) |
| Open: `CompositionMode` for RGB flip swap chains | The lab answers it. Until then it is a `hint`, never `proven` |
| Open: does `WS_EX_TOPMOST` interact with MPO or overlay assignment on some drivers? | Lab: compare PresentMon with and without topmost. NodeSettings can turn it off later if measured |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| GL dialect, alias loader, ES CI leg, format fallbacks, wireframe fallback | 7 |
| Presenter and platform seams, refactor of `RenderEngine`, `OutputWindows` and `RenderHost` | 6 |
| `VblankClock`, `PresentLedger`, `PresentClock`, tests | 8 |
| Delay ring, air latency, the three carry-overs (offscreen light, scan stamps) | 5 |
| 10-bit (format, shader, read-back, tests) | 4 |
| STATE, context, verbs, router, MCP, light rules, tests | 7 |
| Windows: D3D11 adapter, ANGLE EGL, back buffers, `DxgiOutputWindow`, pacing, statistics (compile-only) | 16 |
| Windows CI job on WARP and its test project | 4 |
| Fuse, path choice, device-loss rebuild (+ GL test hook) | 6 |
| PresentMon probe and CSV parser (S1); NV DRS stop-gap (S3); OML on Linux (S4) | 6 |
| Desk chips, inspector, menus; papers (WIRE, ARCHITECTURE, ROADMAP, COMPETITIVE) | 5 |
| **Total** | **74** |

---

## Appendix A. Facts checked on this machine while designing (13 Oct 2026)

| # | Fact | How |
|---|---|---|
| A1 | All **24 of 24** programs in `ShaderLibrary` compile and link under OpenGL ES 3.2 (Mesa 25.2.8, llvmpipe) with `#version 300 es` + `precision highp float/int/sampler2D` replacing `#version 330 core`. All 24 also compile under GL 3.3 core | Extracted each program's sources from `src/PlayMap.Render/Shaders/ShaderLibrary.cs` (composed exactly as `Source(name)` does) and compiled and linked them through EGL surfaceless contexts. Probe sources: `scratchpad/ext/present-probe/extract.py`, `escompile.c` |
| A2 | Mesa ES 3.2 here exposes:<br>• `EXT_color_buffer_float` / `_half_float`<br>• `EXT_texture_norm16`<br>• `OES_texture_float_linear`<br>• `EXT_texture_compression_bptc` / `rgtc` / `s3tc`<br>• `EXT_disjoint_timer_query`<br>• `EXT_read_format_bgra`<br>• `EXT_texture_format_BGRA8888`<br>• `OES_EGL_image(_external)`<br>• `EXT_buffer_storage`<br>• `EXT_float_blend`<br><br>It does **not** expose `ANGLE_polygon_mode`, `NV_polygon_mode` or `EXT_YUV_target` | `scratchpad/ext/present-probe/esprobe.c` |
| A3 | Silk.NET.OpenGL 2.23.0 has `GL.GetApi(Func<string, IntPtr>)`, so `GlDevice`'s binding loads from ANGLE's `eglGetProcAddress` | The package's XML docs in the local NuGet cache |
| A4 | NVAPI interface IDs (MIT, `nvapi_interface.h`, NVIDIA/nvapi main):<br>• `NvAPI_Initialize 0x0150e828`<br>• `NvAPI_DRS_CreateSession 0x0694d52e`<br>• `NvAPI_DRS_LoadSettings 0x375dbd6b`<br>• `NvAPI_DRS_FindApplicationByName 0xeee566b2`<br>• `NvAPI_DRS_CreateProfile 0xcc176068`<br>• `NvAPI_DRS_CreateApplication 0x4347a9de`<br>• `NvAPI_DRS_SetSetting 0x577dd202`<br>• `NvAPI_DRS_GetSetting 0x73bf8338`<br>• `NvAPI_DRS_SaveSettings 0xfcbc7e14`<br>• `NvAPI_DRS_DestroySession 0xdad9cff8` | Fetched and read the header. The framelock WP's IDs are in its spec |
| A5 | Xvfb `:99` here **lists** `GLX_OML_sync_control` and `GLX_SGI_video_sync`, but `glXGetMscRateOML` and `glXGetSyncValuesOML` return False with zero UST/MSC/SBC across 6 swaps. Detection must be by working calls, not by the extension string | `scratchpad/ext/present-probe/omlprobe.c` |
| A6 | Xvfb's GLX here offers `GLX_EXT_create_context_es2_profile` / `es_profile`. glxinfo reports "OpenGL ES profile version string: OpenGL ES 3.2 Mesa 25.2.8" beside "4.5 (Core Profile)" | `glxinfo` on `:99` |
