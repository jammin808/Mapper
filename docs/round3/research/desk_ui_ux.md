# Desk UI technology and UX for a Windows-only show-control / media-server desk (state as of October 2026)

Scope: which UI framework PlayMap's desk should use on Windows 11 x64 / .NET 10, how to show the engine's GPU output in the desk with zero copy and low latency, what the best show-control and creative tools teach about operator UX, how to engineer UI performance and isolation, and the human-factors rules that apply. Local repo facts are cited by file path; everything else by URL. Items marked "computed" are arithmetic from cited numbers; "Inference" sections hold judgement and recommendations.

## 1. Framework comparison for a Windows-only pro desk in 2026

### Takeaway
Stay on Avalonia 12.x. It is current (12.0 in April 2026, 12.1 in July 2026, and the desk already pins 12.1.4). Its GPU-interop API already imports D3D11 NT-handle textures, D3D12 shared fences and Vulkan NT handles, and it has a low-latency DXGI swap-chain mode. WinUI 3 is the only rival with a materially better preview path (SwapChainPanel with a cross-process swap-chain handle), but Microsoft itself says its startup and memory are still worse than UWP's. A rewrite would cost months and gain little for this desk. WPF (D3D9-era D3DImage, airspace), Dear ImGui (no accessibility) and Qt Bridges for C# (still beta) are not fit for the main desk.

### Cited Findings
**Avalonia (current choice)**
- The PlayMap desk references Avalonia, Avalonia.Desktop and Avalonia.Themes.Fluent at version 12.1.4 — [PlayMap.App.csproj](/home/user/mapper/src/PlayMap.App/PlayMap.App.csproj)
- Avalonia 12 announced 2026-04-07. It targets .NET 10 and SkiaSharp 3.0, removed Direct2D1, deleted BinaryFormatter use and enabled warnings-as-errors. On Windows it stops DXGI from intercepting Alt+Enter and PrintScreen and adds a WinForms message filter for hybrid apps — [Avalonia blog: Avalonia 12](https://avaloniaui.net/blog/avalonia-12)
- Direct2D1 backend removed in 12: "The Skia backend is now the recommended rendering backend for all usages" — [Avalonia 12 breaking changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)
- 12.0 changelog includes "Composition system SP1" (#20497, 2026-02-06), "Use Silk.NET in GpuInterop sample", "Fixes accumulated glitches in GpuInterop demo" and a Vulkan-backend RenderTarget fix (2026-03-20) — [Avalonia 12.0.0 release](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.0); [compare 11.3.13...12.0.0](https://github.com/AvaloniaUI/Avalonia/compare/11.3.13...12.0.0)
- Avalonia 12.1 (2026-07-08) changes:
  - On Windows, render frequency now matches the monitor refresh rate for every composition mode other than `WinUIComposition`, which stays the default (#21643).
  - Multiple dirty-rect clipping is off by default, because on discrete GPUs it is slower than overdraw.
  - Stencil buffers are on by default.
  - Bitmaps from pixels are "several times faster" (#21675).
  - The compositor's serialization was rewritten and allocates far less, so GC pauses drop (#21366).
  - Per-visual AABB hit-testing is up to "several hundredfold" faster.
  - Windows GPU surfaces without transparency reduce iGPU load (#20757).
  - Source: [Avalonia blog: What's new in 12.1](https://avaloniaui.net/blog/release-12-1)
- 12.1.x adds "A bit better handling of gpu context loss" (#21387). Later fixes: Vulkan image layout in GPU interop (#21787), `CompositionInterop.IsLost` properly wired (#21824), and `ServerCompositionDrawingSurface` no longer orphaning GPU snapshots after Dispose (#21876) — [Avalonia 12.1.0 release](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.0); [Avalonia releases](https://github.com/AvaloniaUI/Avalonia/releases)
- How Avalonia renders on Windows:
  - Skia runs on Direct3D 11 through ANGLE (`AngleEgl`, the default), with an automatic software fallback. `Wgl` and `Vulkan` are optional rendering modes.
  - Composition modes:
    - `WinUIComposition` (default): Windows 10 build 17134 or later; acrylic and high refresh rate.
    - `DirectComposition`.
    - `LowLatencyDxgiSwapChain`: feature level 11_3, Windows 8.1 or later. "Renders through a low-latency DXGI swap chain for reduced input latency, without the transparency and blur of WinUIComposition."
    - `RedirectionSurface`: compatibility fallback.
  - The first three composition modes require `AngleEgl`.
  - Source: [Avalonia docs: Windows](https://docs.avaloniaui.net/docs/platform-specific-guides/windows); [Win32PlatformOptions.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs)
- `Win32PlatformOptions.GraphicsAdapterSelectionCallback` lets the app choose the adapter when the compositor creates its D3D11 device (AngleEgl) — [Avalonia API: Win32PlatformOptions](https://api-docs.avaloniaui.net/docs/T_Avalonia_Win32PlatformOptions)
- Licensing:
  - The Avalonia core stays MIT ("not changing now, and it's not changing in the future").
  - Pro tooling is commercial: the new Dev Tools, the VS extension, Parcel, and the forked TreeDataGrid.
  - DataGrid is deprecated.
  - Avalonia committed to a free read-only "Table" control in v12.
  - Sources: [Avalonia blog, 2025-10-10](https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia); [GitHub discussion #19804](https://github.com/AvaloniaUI/Avalonia/discussions/19804)
- The TreeDataGrid docs say "This control is available as part of Avalonia Pro or higher" and that it uses virtualised rendering — [TreeDataGrid docs](https://docs.avaloniaui.net/controls/data-display/structured-data/treedatagrid/); [TreeDataGrid product page](https://avaloniaui.net/tree-data-grid)

**WinUI 3 / Windows App SDK**
- Windows App SDK 2.0 (2.0.1) shipped 2026-04-29. It is the first major version since 1.0 (November 2021) and moves to SemVer. Additions:
  - `SystemBackdropElement`
  - custom XAML conditionals
  - popup anchoring
  - Storage Pickers updates
  - Source: [WindowsAppSDK v2.0.1 release](https://github.com/microsoft/WindowsAppSDK/releases/tag/v2.0.1)
- As of 2026-10-14: latest stable 2.5.1 (2026-09-16), latest experimental 2.5.4-experimental (2026-09-29; experimental TableView and Chart controls, inking). 2.4.0 added inking to experimental and mouse/touchpad haptics. WinUI Gallery 3.0 shipped 2026-10-13 — [MS Learn: What's new for developers](https://learn.microsoft.com/en-us/windows/apps/whats-new/whats-new-for-developers)
- Servicing (May 2026 snapshot of the release-channels page): 2.0 "Current" until 2027-04-29; 1.8 "Maintenance" until 2026-09-09; 1.7 and earlier out of support. Stable majors come no more than every six months — [Windows App SDK release channels](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/apps/windows-app-sdk/release-channels.md). That page lists 2.1.3 as the latest stable in May 2026; the what's-new page lists 2.5.1 in October 2026. The two are snapshots at different dates, not a conflict.
- Microsoft: "As of Windows App SDK 2.0, launch speeds, RAM usage, and installation size of WinUI apps are larger/slower than seen in UWP. We're actively working to improve this." — [MS Learn: what's supported when migrating from UWP](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/what-is-supported)
- WinUI team, 2026-05-11:
  - Launch work benchmarked on File Explorer and Notepad: 41% fewer allocations, 63% fewer transient allocations, 25% less time in WinUI code.
  - Many changes are opt-in via XamlChangeIds, possibly opt-out "as early as 3.0 or potentially in 4.0+".
  - Microsoft calls WinUI its "strategic commitment… the native framework going forward" and is moving core Windows experiences to it.
  - Commenters say WinUI 3 "still feels quite heavy compared to UWP".
  - Source: [microsoft-ui-xaml discussion #11096](https://github.com/microsoft/microsoft-ui-xaml/discussions/11096)
- The WinAppSDK 2.0 release notes list the opt-in perf changes, for example faster ResourceDictionary hashing and deferred style setters, "registering XAML's D3D device-removed event during background device creation" — [WinAppSDK 2.0 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- Open-sourcing: a four-phase plan was announced in August 2025 with no end date. The repo README still says "We're not yet ready to accept code contributions", although the binaries now build locally — [Windows Central](https://www.windowscentral.com/microsoft/windows-11/microsoft-confirms-windows-app-sdk-winui-open-source); [microsoft-ui-xaml README](https://github.com/microsoft/Microsoft-UI-XAML)
- SwapChainPanel:
  - Hosts DXGI swap chains anywhere in the XAML tree, and more than one can exist.
  - The app QIs `ISwapChainPanelNative` and calls `SetSwapChain`. The swap chain must be created "for composition" and can come from a D3D11 device or a D3D12 command queue.
  - Rendering can run on its own thread.
  - Sources: [ISwapChainPanelNative](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.xaml.media.dxinterop/nn-windows-ui-xaml-media-dxinterop-iswapchainpanelnative); [SwapChainPanel class](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel?view=windows-app-sdk-1.7); [WindowsAppSDK discussion #3264](https://github.com/microsoft/WindowsAppSDK/discussions/3264)
- `ISwapChainPanelNative2::SetSwapChainHandle` "enables scenarios where a swap chain is created in one process and needs to be passed to another process". It is built on `DCompositionCreateSurfaceHandle` and `IDXGIFactoryMedia::CreateSwapChainForCompositionSurfaceHandle` — [ISwapChainPanelNative2::SetSwapChainHandle](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.xaml.media.dxinterop/nf-windows-ui-xaml-media-dxinterop-iswapchainpanelnative2-setswapchainhandle)
- A Microsoft-authored framework comparison (pre-release "Reactor" repo): the WinUI 3 composition layer gives an "independent animation thread at 60fps even when UI thread is blocked". WPF has "higher memory (40-80MB), slower startup (1-3s cold)" — [microsoft-ui-reactor compare overview](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/research/compare/overview.md)

**WPF on .NET 10**
- .NET 10 shipped November 2025. WPF changes: performance work (fonts, dynamic resources, XAML parsing), Fluent style "still in progress" (more controls styled, HighContrast crash fixes), a unified WPF/WinForms clipboard API, and over 4,000 new unit tests — [MS Learn: What's new in WPF for .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)
- `D3DImage`:
  - Hosts Direct3D 9 surfaces. "Changes to the back buffer are copied forward to the front buffer when you call the Unlock method."
  - The front buffer can become unavailable (screen lock, full-screen exclusive apps, user switching).
  - Nothing shows under software rendering (e.g. Remote Desktop) unless software fallback is enabled.
  - Source: [D3DImage class](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage?view=windowsdesktop-10.0)
- Microsoft's D3DImage performance guidance:
  - "Copying the surface and flushing the command buffer can be costly operations. As the number of D3DImage instances increases, more flushing occurs… use D3DImage sparingly."
  - It needs `D3DCREATE_MULTITHREADED`.
  - Cross-adapter display is "very expensive".
  - If a D3DImage straddles monitors on different adapters, "there is no way to improve performance".
  - Sources: [Performance considerations for D3D9 and WPF interop](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/performance-considerations-for-direct3d9-and-wpf-interoperability); [WPF and Direct3D9 interop](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-direct3d9-interoperation)
- D3D11 in WPF goes through Microsoft's WPFDXInterop `D3D11Image`, a `D3DImage` subclass that uses a surface-queue helper. It ships as NuGet 0.9.0-beta and its requirements still list Visual Studio 2015 and the legacy DirectX SDK. Treat it as **legacy/unmaintained** — [microsoft/WPFDXInterop](https://github.com/Microsoft/WPFDXInterop); [D3D11Image.h](https://github.com/microsoft/WPFDXInterop/blob/master/src/Microsoft.Wpf.Interop.DirectX/D3D11Image.h)
- Airspace: "Each pixel within the window belongs to exactly one HWND." Rendering WPF over a DirectX HWND region "is disallowed", and so is alpha-blending between technologies — [MS Learn: Technology regions overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/technology-regions-overview)

**Startup and footprint measurements (all of empty or simple apps; anecdotal)**
- Empty-app launch-to-Loaded times on Win11 x64, release builds, 10 runs with min and max dropped:

  | Framework | Launch to Loaded (ms) |
  |---|---|
  | WinForms 9 | 231 |
  | WinUI 1.7 | 308 |
  | WPF 9 | 524 |
  | Avalonia 11.3.0 | 757 |
  | Uno 6.1 | 776 |
  | MAUI 9 | 919 |

  The author: "for cross-platform… hardly anything in it"; "I wouldn't base a decision… on these" — [mrlacey/WinAppLaunchCompare](https://github.com/mrlacey/WinAppLaunchCompare)
- The Avalonia team replied that its `Loaded` fires after the first composition commit, later than WPF's. So the comparison is biased against Avalonia, and they planned to align this in 12.0 — [LinkedIn thread (Avalonia team reply quoted)](https://www.linkedin.com/posts/mrlacey_dotnet-maui-avaloniaui-activity-7352020216559595520-Lj_j)
- In-process time to window: WinUI 3 + NativeAOT 0.09–0.18 s, WPF on .NET 8 0.50–0.68 s, native C++ ~0 s. Single VM, May 2025 — [zenn.dev](https://zenn.dev/suusanex/articles/f642d91df412c9?locale=en)
- A single-author line-of-business benchmark (April 2026): WinUI 3 cold start ~1.4 s vs WPF ~1.8 s, with WinUI 3 using 15–20% less memory — [CTCO blog](https://www.ctco.blog/posts/winui-vs-wpf-2026-practical-comparison/)

**Dear ImGui**
- "Designed to… empower programmers to create content creation tools and visualization / debug tools (as opposed to UI for the average end-user)… full internationalization… and accessibility features are not supported." Used by Tracy, ImHex, RemedyBG — [ocornut/imgui README](https://github.com/ocornut/imgui?tab=readme-ov-file)
- Multi-viewports (OS windows over several monitors) and docking live in the `docking` branch, described as "well maintained… safe and recommended" — [Multi-viewports wiki](https://github.com/ocornut/imgui/wiki/multi-viewports)
- v1.92.0 added per-viewport framebuffer scale and `WM_DPICHANGED` handling. Multi-DPI still "will scale fonts but NOT style padding/spacings/thicknesses yet" — [imgui v1.92.0](https://github.com/ocornut/imgui/releases/tag/v1.92.0); [imgui FAQ](https://github.com/ocornut/imgui/blob/master/docs/FAQ.md)
- Making an immediate-mode GUI accessible is possible through AccessKit, but "can't be made accessible all that easily" because Dear ImGui keeps no widget state. This is a community forum, low authority — [Ziggit thread](https://ziggit.dev/t/accessible-immediate-mode-guis/12210)

**Qt 6**
- Qt 6.10 released 2025-10-07, adding flex-box layout for Qt Quick, `QRangeModel`, and Lottie/SVG animation — [Qt 6.10 released](https://www.qt.io/blog/qt-6.10-released)
- The Qt Quick RHI supports D3D11, D3D12, Vulkan, Metal and OpenGL, with D3D11 the Windows default. Qt 6.10 added adapter enumeration on D3D11/12/Vulkan — [Qt Quick 3D requirements](https://doc.qt.io/qt-6.10/qtquick3d-requirements.html); [What's new in Qt 6.10](https://doc.qt.io/qt-6/whatsnew610.html)
- Qt Bridge for C# is at **beta 0.3.0** (2026-06-23); the next step is a "Technology Preview". Templates default to net8.0, and the Windows package bundles a minimal Qt Quick runtime — [Qt blog: C# Bridge 0.3.0 Beta](https://www.qt.io/blog/qt-bridges-csharp-0.3.0-beta-released); [qt/qtbridge-csharp](https://github.com/qt/qtbridge-csharp); [Qt Bridges page](https://www.qt.io/development/qt-framework/qt-bridges)

### Inferences
- **Recommendation: keep Avalonia 12.x as the desk framework.**
  - Avalonia's interop surface (ICompositionGpuInterop, section 2) already covers what a D3D12 or Vulkan engine needs.
  - `LowLatencyDxgiSwapChain` is available if measured input latency demands it.
  - The core is MIT and the release cadence is active (12.0 April, 12.1 July 2026).
  - The desk already ships on 12.1.4.
- Avalonia's real costs:
  - The commercial TreeDataGrid: use the MIT legacy fork, write virtualised lists yourself, or use the promised free `Table`.
  - GPU interop is newer and less battle-tested than SwapChainPanel. Several interop fixes landed in 2026 (#21787, #21824, #21876).
- **WinUI 3** is the only credible alternative. Its unique asset is the zero-copy cross-process path (`SetSwapChainHandle` / composition surface handles), where the engine presents straight into the desk's tree with no import step.
  - Against it: Microsoft states its launch and RAM are still worse than UWP's, and its best perf fixes are opt-in.
  - A move means rewriting every view, style and control template. Avalonia XAML is WPF-like but not WinUI-compatible.
  - This is a re-platforming, not a port. **Estimate:** a large fraction of the desk's UI code would be rewritten. No source quantifies this.
- **WPF is ruled out for a GPU desk:**
  - D3DImage is D3D9-based and copies on every Unlock.
  - The D3D11 bridge is an unmaintained 2015 beta.
  - Airspace forbids overlays on hosted DX content.
- **Dear ImGui is ruled out for the operator desk:** it has no accessibility and multi-DPI padding scaling is unsolved. It is acceptable for engine-side developer overlays, such as a stats HUD in the engine window.
- **Qt via C#** is beta in 2026 and would add a second runtime and a C++ toolchain. Revisit after Technology Preview.
- **A native C++ shell** would discard the .NET 10 single-exe model and the shared Core types.
- If the parallel renderer decision lands on D3D12 and preview latency proves unacceptable even with Avalonia's import path, consider a **targeted hybrid** before any rewrite: one engine-owned HWND or DirectComposition visual for the main viewport. Measure first.

### Gaps
- No independent, recent (2026) benchmark of Avalonia 12 vs WinUI 3 (WinAppSDK 2.x) startup, memory or input latency on the same app. The cited ones are on Avalonia 11.3 / WinUI 1.7 / empty apps.
- No source quantifies Avalonia's Windows UI Automation (screen-reader) coverage against WinUI 3 and WPF. The blog mentions accessibility-role fixes only for macOS.
- No 2026 version or roadmap source was fetched for WPF beyond .NET 10. Whether .NET 11 (due November 2026) brings WPF changes is unverified.

## 2. Zero-copy, low-latency previews of engine output in a separate UI process

### Takeaway
Replace the CPU pixel ring with GPU-shared textures for local engines:
- The engine renders each preview into a small ring of shared textures. It signals a shared fence (D3D12) or a keyed mutex (D3D11).
- The desk imports each texture once through Avalonia's `ICompositionGpuInterop` (`D3D11TextureNtHandle` plus `Direct3D12FenceNtHandle` or KeyedMutex sync).

The current path makes about four full-frame copies per view per frame; this path makes none. Keep the ring as the fallback when the desk and engine sit on different adapters, and for remote nodes. DirectComposition/SwapChainPanel surface handles are the even lower-latency option, but only if the desk ever hosts a WinUI or DComp tree.

### Cited Findings
**Current PlayMap path**
- Previews are rendered to targets in the engine and "read back asynchronously (PBO ring) into the preview shared memory" — [docs/ARCHITECTURE.md §8](/home/user/mapper/docs/ARCHITECTURE.md)
- The preview ring is a file-backed memory map with three BGRA slots and a per-slot seqlock. The writer never waits for the reader — [PreviewRing.cs](/home/user/mapper/src/PlayMap.Core/Media/PreviewRing.cs)
- The desk reader copies the newest slot into a `byte[]`, then copies it row by row into an Avalonia `WriteableBitmap` with `Marshal.Copy`. It polls the ring and re-probes it at most once a second — [PreviewSource.cs](/home/user/mapper/src/PlayMap.App/Controls/PreviewSource.cs)
- `IPreviewSource` / `PreviewFeeds.Resolve` is already a seam: the integrator can substitute other feeds, such as remote JPEG — [PreviewSource.cs](/home/user/mapper/src/PlayMap.App/Controls/PreviewSource.cs)
- Measured cost of a CPU staging readback (SpoutDX source comments): "One texture — approx 7–12 msec at 1920x1080; Two textures — approx 2.5–3.5 msec at 1920x1080" — [SpoutDX.cpp](https://github.com/leadedge/SpoutCam/blob/dc6eec6e67802fa8c123be53d6972845e3f148d3/SpoutDX/source/SpoutDX.cpp)
- Spout's GPU path is quoted at ~0.45 ms for 4K60, against ~5–7 ms for the CPU fallback at 4K. This comes from an AI-generated wiki of the repo and is unverified secondary material — [DeepWiki: Spout2 architecture](https://deepwiki.com/leadedge/Spout2/2-architecture)
- Avalonia 12.1 made creating bitmaps from pixels "several times faster" (#21675) — [Avalonia 12.1 blog](https://avaloniaui.net/blog/release-12-1)

**Avalonia GPU interop (the import side)**
- `ICompositionGpuInterop` members:
  - `ImportImage(IPlatformHandle, PlatformGraphicsExternalImageProperties)`
  - `ImportSemaphore`
  - `GetSynchronizationCapabilities`
  - `SupportedImageHandleTypes` and `SupportedSemaphoreTypes`
  - `DeviceLuid` / `DeviceUuid` ("The LUID of the graphics adapter used by the compositor")
  - `IsLost`
  - Source: [Avalonia docs: ICompositionGpuInterop](https://docs.avaloniaui.net/api/avalonia/rendering/composition/icompositiongpuinterop)
- Image handle types:
  - `D3D11TextureGlobalSharedHandle` (legacy `GetSharedHandle`; "does not own the reference")
  - `D3D11TextureNtHandle` (`IDXGIResource1::CreateSharedHandle`, `D3D11_RESOURCE_MISC_SHARED_NTHANDLE`)
  - `VulkanOpaqueNtHandle` / `VulkanOpaqueKmtHandle`
  - `DmaBufFileDescriptor`
  - `IOSurfaceRef`
- Semaphore types include `Direct3D12FenceNtHandle` ("A DXGI NT handle returned by ID3D12Device::CreateSharedHandle or ID3D11Fence::CreateSharedHandle") and `VulkanOpaqueNtHandle`
- Source for both lists: [PlatformGraphicsExternalMemory.cs (master)](https://github.com/AvaloniaUI/Avalonia/blob/main/src/Avalonia.Base/Platform/PlatformGraphicsExternalMemory.cs)
- Sync capabilities are `Semaphores`, `KeyedMutex` ("Image must be created with D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX") or `Automatic`. A `CompositionDrawingSurface` is updated with a keyed-mutex acquire/release key pair. The maintainers advised: "For Windows you need to provide a DXGI-compatible texture handle and IDXGIKeyedMutex… Alternatively on Windows/Linux you can enable vulkan backend and provide vulkan texture and vulkan semaphore" — [Avalonia spec issue #9925](https://github.com/AvaloniaUI/Avalonia/issues/9925)
- The official D3D11 sample checks for `D3D11TextureGlobalSharedHandle` support, creates its own D3D11 device on adapter 0, and renders through a swap chain over `CompositionDrawingSurface` — [D3D11DemoControl.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/samples/GpuInterop/D3DDemo/D3D11DemoControl.cs)
- Vulkan import bug (fixed by PR #21787): Avalonia transitioned imported images from `VK_IMAGE_LAYOUT_UNDEFINED`, which on some drivers lost the imported data. A new `ImageLayout` property tells it the real layout. The PR does not implement `VK_QUEUE_FAMILY_EXTERNAL` ownership transfer — [Avalonia PR #21787](https://github.com/AvaloniaUI/Avalonia/pull/21787)
- Composition modes `WinUIComposition`, `DirectComposition` and `LowLatencyDxgiSwapChain` all require the `AngleEgl` (D3D11) rendering mode — [Avalonia docs: Windows](https://docs.avaloniaui.net/docs/platform-specific-guides/windows)

**D3D12 / D3D11 sharing rules (the export side)**
- `ID3D12Device::CreateSharedHandle` creates NT handles for an `ID3D12Heap`, `ID3D12Resource` or `ID3D12Fence`. "For Direct3D 11 and Direct3D 12 interop… a shared fence is opened in DirectX 11 with ID3D11Device5::OpenSharedFence, and a shared resource is opened with ID3D11Device::OpenSharedResource1" — [ID3D12Device::CreateSharedHandle](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-createsharedhandle); [ID3D11Device5::OpenSharedFence](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nf-d3d11_4-id3d11device5-opensharedfence)
- D3D11 fences must be created with `D3D11_FENCE_FLAG_SHARED` (or `…_CROSS_ADAPTER`) before a shared handle can be made — [ID3D11Fence::CreateSharedHandle](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11fence-createsharedhandle)
- Cross-adapter shared resources "are only supported in system memory" and need cross-adapter fences — [MS Learn: D3D12 shared heaps](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps)
- Legacy D3D11 `OpenSharedResource` sharing covers only "2D non-mipmapped textures", and "If a shared texture is updated on one device ID3D11DeviceContext::Flush must be called on that device" — [ID3D11Device::OpenSharedResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-opensharedresource)

**Industry precedent: Spout (Resolume, TouchDesigner and others on Windows)**
- All Spout sharing ultimately uses D3D11 shared textures:
  - The sender copies into the shared texture, calls `Flush()`, signals a new frame under a named mutex, then releases it.
  - Receivers check a frame-count semaphore, so they copy only when a new frame exists.
  - An "auto switch adapter" option recreates the receiver device on the sender's adapter.
  - Sources: [SpoutDX.cpp](https://github.com/leadedge/SpoutCam/blob/dc6eec6e67802fa8c123be53d6972845e3f148d3/SpoutDX/source/SpoutDX.cpp); [Spout2 Tutorial07](https://github.com/leadedge/Spout2/blob/master/SPOUTSDK/SpoutDirectX/SpoutDX/Tutorial07/Tutorial07.cpp)

**DirectComposition / cross-process swap chains**
- "DirectComposition… enables two different applications to share windowless swap chains across process boundaries… the swap chain is created in one process and is used with DirectComposition in a second process" — [MS Docs: DirectComposition bitmap surfaces](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/directcomp/bitmap-surfaces.md)
- How it is wired:
  - `DCompositionCreateSurfaceHandle` creates the handle.
  - `IDXGIFactoryMedia::CreateSwapChainForCompositionSurfaceHandle` binds a swap chain to it.
  - The desk-side tree binds it with `ICompositorInterop::CreateCompositionSurfaceForHandle` (Windows.UI.Composition) or `IDCompositionDevice::CreateSurfaceFromHandle`.
  - The composition swapchain API (`IPresentationManager`) offers "presentation with independent flip".
  - Sources: [DCompositionCreateSurfaceHandle](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositioncreatesurfacehandle); [CreateSwapChainForCompositionSurfaceHandle](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgifactorymedia-createswapchainforcompositionsurfacehandle); [Composition swapchain examples](https://learn.microsoft.com/en-us/windows/win32/comp_swapchain/comp-swapchain-examples)

**Windows.Graphics.Capture**
- It delivers `Direct3D11CaptureFrame` surfaces from a frame pool. The frame size is fixed at pool creation, and content larger than the pool is clipped. `FrameArrived` should not do heavy work on the UI thread — [MS Learn: Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- Capture rate follows the DWM ("If we detect no changes, we may decide not to render a new frame"; more aggressive in Windows 11). Capturing on a different adapter from the DWM forces system-memory transfers. Over RDP it runs at ~30 Hz — [Win32CaptureSample issue #45 (Microsoft engineer replies)](https://github.com/robmikh/Win32CaptureSample/issues/45)
- WebRTC uses a single-buffer pool "to reduce latency" — [WebRTC wgc_capture_session.cc](https://webrtc.googlesource.com/src/+/8bd0f97fe6873f2c1b80d220b66fdebd30eb4f65/modules/desktop_capture/win/wgc_capture_session.cc)
- Capture start-up took over 10 s under high CPU load — [Windows.UI.Composition-Win32-Samples issue #135](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/issues/135)

### Inferences
- **Cost of today's ring (computed):**
  - A 1920×1080 BGRA frame is 8,294,400 B; at 60 fps that is about 498 MB/s per copy pass.
  - The path makes four passes: GPU→ring (PBO readback), ring→`byte[]`, `byte[]`→`WriteableBitmap`, and Avalonia's bitmap→GPU upload.
  - That is about 2 GB/s of memory traffic per full-HD view at 60 fps, and about 8 GB/s per 4K60 view.
  - Each pass adds latency, at least one UI frame in the worst case for the poll alone.
  - The PBO readback is asynchronous, so it does not stall the air. But it consumes PCIe bandwidth and GPU copy-engine time that the show also needs.
- **Recommended preview transport, D3D12 engine:**
  - Per preview view, the engine allocates 2–3 committed textures (BGRA8, single mip) with `D3D12_HEAP_FLAG_SHARED`, plus one shared `ID3D12Fence`.
  - After rendering slot *n*, it signals fence value *v*.
  - It duplicates the NT handles into the desk process; the wire carries only handle values and the monotonically increasing fence value.
  - The desk imports each slot once with `ImportImage(D3D11TextureNtHandle)` and the fence with `ImportSemaphore(Direct3D12FenceNtHandle)`. It then updates the `CompositionDrawingSurface` with wait/signal values.
  - The engine never waits on the desk. It skips a slot the desk still holds, the same "writer never waits" rule as today's ring.
- **D3D11 engine (or interim):** use `D3D11_RESOURCE_MISC_SHARED_NTHANDLE | …_SHARED_KEYEDMUTEX` and the KeyedMutex sync capability. This is the path Avalonia's maintainers document and Spout's model proves.
- **Vulkan engine:**
  - Avalonia's default Windows compositor is D3D11 via ANGLE, and its preferred composition modes require ANGLE.
  - Prefer that the engine imports D3D-created shared textures (Vulkan supports D3D11/D3D12 texture handle types), or exports opaque NT handles only if the desk runs Avalonia's `Vulkan` rendering mode.
  - **Unverified:** whether ANGLE can import `VulkanOpaqueNtHandle` memory. This is a risk to test early (see Gaps).
- **Adapter guard (fail closed):**
  - Compare `ICompositionGpuInterop.DeviceLuid` with the engine's adapter LUID.
  - If they differ (hybrid laptops, or a desk window on a monitor driven by another GPU), refuse the GPU path with a stated reason and fall back to the CPU ring. Cross-adapter sharing is system-memory only anyway.
  - Use `GraphicsAdapterSelectionCallback` to put the desk's compositor on the engine's adapter by default.
  - Watch `IsLost` (wired in 12.1.x) and re-import after a device reset.
- **Keep the CPU ring** for remote nodes (JPEG feeds), for cross-adapter cases and as a tested fallback. Make the GPU path a new `IPreviewSource` implementation behind the existing seam.
- **Downscale thumbnails on the engine's GPU** to the size actually displayed. Render only views the desk reports as visible. This cuts GPU cost regardless of transport.
- **DirectComposition / SwapChainPanel handles** give true "present into another process's tree" with independent flip, the lowest-latency design. Avalonia exposes no public API to host a foreign DComp surface handle, so this path means a WinUI host or a custom child visual. Reserve it for a measured need.
- **Windows.Graphics.Capture is not suitable** for engine previews:
  - It captures DWM-composited windows, not render targets.
  - Its rate follows DWM change detection.
  - It is slow under load and pays cross-adapter penalties.
  - It can still serve as a last-resort "see the projector output window" diagnostic.

### Gaps
- No source confirms that Avalonia's ANGLE (D3D11) backend lists `Direct3D12FenceNtHandle` in `SupportedSemaphoreTypes` at run time on Windows 11. The constant exists in the API. Query it on target hardware.
- No published latency numbers for Avalonia `ImportImage` or `CompositionDrawingSurface` updates (frames of delay from engine signal to photon).
- No benchmark of D3D12 shared textures opened in ANGLE's D3D11 device versus a SwapChainPanel composition swap chain.
- Spout's GPU-path figure (0.45 ms at 4K) comes from an AI-generated wiki and was not verified against Spout's own documentation.

## 3. UX of leading show-control and pro creative tools

### Takeaway
The best tools share five patterns:
- **An unmistakable, unremappable stop.** QLab: Esc fades, double-Esc hard-stops.
- **GO protection.** QLab's minimum GO interval shows a red border, and a refused GO flashes.
- **Visibly different safe-edit modes.** Eos turns the Blind background grey and puts "Blind" on every display; MA3 colours the preview encoder bar orange and flashes binocular icons on previewed objects.
- **A history-based undo.** MA3's Oops list. Confirmations are kept for big, rare operations.
- **"Map anything" hardware control with feedback.** Resolume and Millumin highlight every mappable control; Companion turns Stream Decks into show surfaces.

Vendors' own release notes show what operators hate: UI hangs while editing, slow response when unfocused, and black flashes on timeline jumps.

### Cited Findings
**QLab 5 (go/panic patterns)**
- Double-GO protection:
  - A minimum time between GOs is configurable. While it is active "a red border will appear around the button". A GO received during it makes the area "flash red to indicate that the command was received, but disregarded".
  - It covers mouse, keyboard, and workspace MIDI/MSC/OSC GO.
  - "Require a 'key up' before re-arming GO" guards against bad keyboards and KVMs.
  - Source: [QLab 5: Workspace settings](https://qlab.app/docs/v5/fundamentals/workspace-settings/)
- Panic:
  - A panic fades over a set duration; a second panic during the fade stops everything at once.
  - "A single press of the escape key is always assigned to panic, and a double press of the escape key is always assigned to hard stop… in an emergency, any person familiar with QLab can confidently use the escape key to stop all playback from any QLab system."
  - Sources: [QLab 5: Workspace settings](https://qlab.app/docs/v5/fundamentals/workspace-settings/); [QLab 5 keyboard shortcuts](https://qlab.app/docs/v5/general/keyboard-shortcuts/) ("This shortcut cannot be changed")
- Edit Mode vs Show Mode (⇧⌘[ / ⇧⌘]); single-key cue editing shortcuts (N, Q, T, D…); a Warnings panel (⌘B) — [QLab 5 keyboard shortcuts](https://qlab.app/docs/v5/general/keyboard-shortcuts/)
- OSC exposes `/doubleGoWindowRemaining` and `/showMode`. Discrete actions do not reply unless `/alwaysReply` is set — [QLab OSC dictionary](https://qlab.app/docs/v5/scripting/osc-dictionary-v5/)
- Show-control broadcast emits GO, panicAll, stopAll and playhead events for other systems — [QLab show control broadcast](https://reference.qlab.app/docs/v5/networking/show-control-broadcast/)

**grandMA3 (undo, blind, preview, lock, dark-room theme)**
- `Oops` undoes the last operation. Press-and-hold opens the Oops menu, a chronological history from which "a coherent amount of actions" can be undone at once.
- "Oops Confirmation" can be Always, Main (for store/move/delete) or Never; the default is Never.
- Sources: [MA3 Keys](https://help.malighting.com/grandMA3/2.4/HTML/keys.html); [MA3 Oops menu](https://help.malighting.com/grandMA3/2.3/HTML/ws_oops_overlay.html)
- Blind hides programmer values from the output. Clear works in three stages: deselect, then deactivate, then clear. Holding Clear for more than a second clears all — [MA3 The Programmer](https://help.malighting.com/grandMA3/2.5/HTML/operate_programmer.html)
- Preview mode:
  - Uses a separate preview programmer, an orange preview encoder bar, and flashing binoculars on previewed objects.
  - The manual warns that "Storing or updating sequences or cues in preview can affect live output".
  - All preview actions appear in the command-line history.
  - Source: [MA3 Preview](https://help.malighting.com/grandMA3/2.5/HTML/preview.html)
- Desk lock is toggled with F9 or Pause. Unlocking needs taps on all four screen corners in turn — [MA3 Desk lock](https://help2.malighting.com/Page/grandMA3/Desk_lock/en/1.3)
- Theme v2.5: "optimized for use in dark control room environments… non-selected UI elements… displayed with reduced intensity, while selected UI elements are clearly highlighted". A legacy theme is kept for reverting — [MA3 2.5 features](https://help.malighting.com/grandMA3/2.5/HTML/rn_features-2-5.html)
- A separate "defaultDAYLIGHT" theme serves bright conditions. Screen-brightness and desk-light faders exist, globally and per screen — [MA3 Color Theme](https://help.malighting.com/grandMA3/2.4/HTML/ws_colors_color_theme.html); [MA3 Desk Lights & Color Theme](https://help2.malighting.com/Page/grandMA3/Desk_Light/en/1.4)

**ETC Eos**
- Blind is unmistakable: "the screen background turns gray, the word 'Blind' is in front of the command line and in the top left corner of each display". Blind edits store immediately without Record ("extremely fast, but… use caution. Undo if necessary") — [Eos Family L3 Intermediate](https://www.etcconnect.com/uploadedFiles/Main_Site/Documents/Public/Video_Tutorial/Eos_Family_L3_Intermediate_v3.2.pdf)
- Go and Stop/Back:
  - Stop/Back halts a running cue mid-transition; pressing it again goes to the previous cue; Go then resumes.
  - Setup offers Hi-Contrast Displays, Spacebar [Go] enable, and Record/Delete confirm options.
  - Source: [Eos/Gio Level 1 workbook](https://www.etcconnect.com/uploadedFiles/Main_Site/Documents/Public/Video_Tutorial/EosGio_L1_Workbook_v2.3.0_revA.pdf)

**TouchDesigner, Resolume, Millumin, Pixera, Watchout, disguise**
- TouchDesigner Perform Mode:
  - Only one Window COMP is rendered and the network editor is closed. F1/Esc toggle the mode.
  - "Start in Perform Mode" avoids the memory the editor needs.
  - Full-screen exclusive mode gives "stutter-free playback". Otherwise "the Windows Desktop Compositor may not always show all of the frames".
  - Source: [Derivative: Perform Mode](https://docs.derivative.ca/Perform_Mode)
- Resolume shortcut mode:
  - "Everything that has changed color can have a shortcut assigned to it."
  - Keyboard, MIDI, OSC and (in Arena) DMX are supported.
  - Each shortcut has a target: by position, this clip, or the selection.
  - MIDI feedback goes per device, and a MIDI monitor helps debugging.
  - Sources: [Resolume keyboard shortcuts](https://resolume.com/support/en/keyboard-shortcuts); [Resolume MIDI shortcuts](https://resolume.com/support/midi-shortcuts)
- Millumin Interactions:
  - Cmd+M highlights mappable elements in green.
  - An interaction is input → transformer → action, with feedback to motorized faders.
  - Interaction groups can be enabled or scoped.
  - The Dashboard's columns are "moments of your show", and "Silent Edition" edits without the audience noticing.
  - Sources: [Millumin Interactions](https://help.millumin.com/docs/connect/interactions/); [Millumin Dashboard](https://help.millumin.com/docs/playback/dashboard/)
- Pixera's Control tab holds Modules and Actions. A Live tab shows every control system on the network, and keyboard shortcuts can run Actions — [Pixera Control overview](https://help.pixera.one/control-basic-overview)
- Watchout 7 splits Producer, Director, Asset Manager and Runner. "Producer and Runner on the same node share GPU and SSD… check that frames are not dropped before going live, and move Producer to its own node if they are". For unattended shows the Producer node is removed — [WATCHOUT 7: Welcome](https://docs.dataton.com/guide/watchout/getting-started/welcome-to-watchout-7.html)
- disguise r30 release notes:
  - "Designer no longer respond[s] to transport commands slowly when out of focus"
  - "Performance has generally been improved when modifying objects (no more small hangs while editing!)"
  - "Large list views will no longer have input boxes that can detach and float around the GUI"
  - Content now loads before the playhead jumps, avoiding "flashes to black"
  - New GUI scale setting
  - UI warnings for invalid understudy (backup) configurations
  - Source: [disguise r30 release notes](https://help.disguise.one/designer/release-notes/r30)
- disguise Designer has a Notifications tab for "missing files or device errors" — [disguise Designer UI](https://help.disguise.one/designer/ui/designer-user-interface)
- Research for the redesign of disguise's Mapping Matter (designer's portfolio, a secondary source): even experienced users "frequently clicked around to find essential features"; "I just want everything in one place" — [Joe Brazao case study](https://www.joebrazaoux.com/mapping-matter2-0)
- Practitioner advice on choosing between tools is to test "the widest canvas, busiest cue… and expected recovery action… the operator's ability to diagnose a fault" — [Plug and Pixel](https://plugandpixel.com/insights/comparison-of-audiovisual-software/)

**Hardware controllers**
- Bitfocus Companion turns Stream Decks into control surfaces. It offers button feedback from devices, stacked or delayed actions, web buttons, and triggers over OSC, TCP, UDP, HTTP, WebSocket and Art-Net — [Bitfocus Companion](https://bitfocus.io/companion)
- The Eos Companion module ships Go and Stop/Back presets that show the pending cue and intensity on the button — [Bitfocus: ETC Eos](https://bitfocus.io/connections/etc-eos)
- QLab documents a first-party Stream Deck plugin — [QLab 5 manual index](https://qlab.app/docs/v5/)

### Inferences
- **Panic and GO.** PlayMap already has PANIC and MinGo verbs (round 2). Adopt QLab's visible contract:
  - A red border on GO while the minimum-interval guard is active.
  - A flash when a GO is refused. This doubles as the doctrine's "refused with the reason" made visible.
  - One fixed, unremappable panic key, e.g. Esc = fade panic and double Esc = hard black, documented as identical on every node.
  - Panic in the right-click menu and on the show strip, plus a Companion button.
- **Blind/draft must be impossible to mistake for live.** Copy Eos and MA3: a whole-desk tint or background change plus the word "BLIND" on every pane showing draft content. Never rely on a small badge.
- **Undo as a visible history.** Expose an Oops-like history of applied edits (from the journal), with multi-step undo from a chosen point. Ask for confirmation only on irreversible, out-of-the-ordinary actions; see section 5.
- **Show Mode / Perform Mode.** Offer a desk mode that:
  - locks editing verbs (the engine already gates them; the desk should also hide editors);
  - stops rendering heavy editor views;
  - keeps only the show strip, status cards and output previews.
- **Desk lock** (MA3-style) for unattended moments.
- **Map anything.** A mapping mode that highlights every control bound to a verb, as Resolume and Millumin do. Then key, MIDI, OSC or a Companion button is assigned by touching the control. Feedback (button colour, motor faders) comes from observed state, not attempts.
- **Status at a glance.** One persistent notifications/warnings pane (disguise Notifications, QLab Warnings) fed by preflight, plus per-card health. Never a modal for a non-blocking fault.
- **Separate authoring load from playback.** This is Watchout's guidance. PlayMap's desk/engine process split already does it on one machine. Document that on a single-GPU machine heavy desk viewports compete with the air, and give the desk a "preview budget" governor tied to measured engine frame slack.

### Gaps
- No primary forum threads were retrieved on operator complaints about Pixera, Watchout 7, Millumin or Notch UIs. The disguise pain points come from its own release notes, not user surveys.
- No source on Unreal nDisplay/Switchboard or Notch operator UX was retrieved.
- No published study of multi-monitor desk layouts in media servers was found. Layout advice above is inference.

## 4. UI performance engineering and stability

### Takeaway
Budget the desk to a frame: 16.7 ms at 60 Hz, 6.9 ms at 144 Hz. Coalesce high-rate STATE pushes to at most one apply per frame. Virtualise every list. Never copy pixels on the UI thread. Measure click-to-photon with PresentMon 2.x rather than guessing.

Avalonia 12.1's compositor (fewer allocations, faster hit-testing, refresh-rate-matched rendering) and its `LowLatencyDxgiSwapChain` mode are the levers available without changing framework. The engine/desk process split, which Watchout and TouchDesigner's Perform Mode also embody, is the stability backbone: a desk crash must never touch the air.

### Cited Findings
- Perceived latency from the repo's own UX paper, both touch studies:
  - Users notice drag latency at about 11 ms and tap latency at about 69 ms on direct touch; about 55 ms and 96 ms on indirect input — [Deber et al., CHI 2015](https://dl.acm.org/doi/10.1145/2702123.2702300)
  - The most sensitive users noticed drag latency at about 2 ms — [Ng et al., UIST 2012](https://dl.acm.org/doi/10.1145/2380116.2380174)
  - Both as summarised in [docs/UX.md](/home/user/mapper/docs/UX.md)
- Avalonia 12.1 compositor work:
  - Render-data serialization rewritten, "greatly reducing memory allocations and, consequently, garbage collector pauses, leading to smoother frame times" (#21366)
  - Hit-testing with many visuals sped up "several hundredfold in extreme cases" (#21310)
  - Multiple dirty-rect clipping off by default on discrete GPUs
  - Source: [Avalonia 12.1 blog](https://avaloniaui.net/blog/release-12-1)
- Avalonia `LowLatencyDxgiSwapChain` is "only recommended if low input latency is desirable, and there is no need for the transparency and styling / blurring" — [Win32PlatformOptions.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs)
- WinUI 3 composition runs independent animations at 60 fps even when the UI thread is blocked — [microsoft-ui-reactor compare overview](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/research/compare/overview.md)
- PresentMon:
  - 2.2.0 added a click-to-photon metric for mouse-button events (the old metric became "all-input-to-photon"). Its ETW event latency fell from 1000 ms to ~30 ms. Input landing on dropped frames is attributed to the next displayed frame — [PresentMon v2.2.0](https://github.com/GameTechDev/PresentMon/releases/tag/v2.2.0)
  - 2.3.0 (2024-12-17) added "Instrumented Latency" and Animation Time — [PresentMon v2.3.0](https://github.com/GameTechDev/PresentMon/releases/tag/v2.3.0)
  - 2.4.0 added flash injection with a rainbow bar to identify frames (for photodiode or camera checks), headless CLI capture, and beta `MsPCLatency` — [PresentMon v2.4.0](https://github.com/GameTechDev/PresentMon/releases/tag/v2.4.0)
  - Metric definitions: `MsClickToPhotonLatency`, `MsPCLatency`, `MsAnimationError` — [PresentMon capture app README](https://github.com/GameTechDev/PresentMon/blob/main/README-CaptureApplication.md)
- NVIDIA defines PC Latency as input→frame start, plus frame start→Present, plus Present→displayed, measured with ETW markers. In windowed mode dropped frames must be accounted for — [NVIDIA: Understanding and Measuring PC Latency](https://developer.nvidia.com/blog/understanding-and-measuring-pc-latency/)
- Virtualisation:
  - Avalonia's TreeDataGrid uses virtualised rendering ("Ten thousand rows… virtualized") but is now commercial (Pro tier).
  - The original MIT version stays on GitHub.
  - A free read-only `Table` control was committed for v12.
  - Sources: [TreeDataGrid page](https://avaloniaui.net/tree-data-grid); [Avalonia blog 2025-10-10](https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia)
- Receivers should copy only when the producer has a new frame. Spout's receivers check a frame-count semaphore before copying — [SpoutDX.cpp](https://github.com/leadedge/SpoutCam/blob/dc6eec6e67802fa8c123be53d6972845e3f148d3/SpoutDX/source/SpoutDX.cpp)
- Process isolation in peers:
  - Watchout separates Producer from Runner and recommends a separate node if frames drop — [WATCHOUT 7](https://docs.dataton.com/guide/watchout/getting-started/welcome-to-watchout-7.html)
  - TouchDesigner Perform Mode closes the editor to save memory and GPU — [Derivative: Perform Mode](https://docs.derivative.ca/Perform_Mode)
  - disguise fixed edit-time hangs and transport lag when out of focus in r30 — [disguise r30](https://help.disguise.one/designer/release-notes/r30)
- Windows.Graphics.Capture's `FrameArrived` should not do heavy UI-thread work, and `CreateFreeThreaded` raises it on a worker thread — [MS Learn: Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture); [Direct3D11CaptureFramePool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool?view=winrt-28000)

### Inferences
- **Frame budget.** Target UI-thread work under ~8 ms per frame at 60 Hz so that layout, render and composition fit in 16.7 ms; at 144 Hz the whole frame is 6.9 ms (computed). Treat any UI-thread task over one frame as a defect; log it with a stopwatch histogram, not `DateTime.Now`, which is banned in `src/`.
- **High-rate STATE pushes:**
  - Reduce the wire stream into an immutable snapshot off the UI thread.
  - Post at most one apply per UI frame, at Render priority, dropping intermediate snapshots ("latest wins").
  - Diff to change only properties whose values changed, so bound text and layout invalidate only where needed.
  - Never replace whole `ObservableCollection`s; patch them.
  - Throttle text that changes every frame (timecode, fps) to the display refresh. Draw it in a fixed-width, measure-free element to avoid layout thrash.
- **Many live thumbnails:**
  - Use GPU-imported images (section 2); one `CompositionDrawingSurface` per visible thumbnail.
  - Have the engine render previews at displayed size and at a reduced rate (e.g. 15–30 fps) for small thumbnails, and full rate only for the focused viewport.
  - Stop requesting frames for off-screen or collapsed views.
- **Virtualise every list and tree** (cues, media, surfaces, journal): the legacy MIT TreeDataGrid, a virtualising ItemsControl, or the v12 `Table`.
- **Measure, then govern** (doctrine). Use:
  - PresentMon click-to-photon on the desk window;
  - flash injection to check a preview frame's display age;
  - a desk-side "preview age" readout (engine stamp vs present time).
  Only then consider `LowLatencyDxgiSwapChain`, which trades acrylic and transparency for latency.
- **Isolation:**
  - The desk must hold no lock, handle or GPU resource whose loss stalls the engine. Shared textures are owned by the engine, and the engine never waits on desk fences.
  - A desk crash must leave the air untouched, and a restarted desk re-attaches by reading STATE.
  - Add a deterministic test: kill the desk mid-import and assert that engine frame cadence and the present ledger are unchanged.

### Gaps
- No Avalonia-specific published guidance was fetched on compiled bindings or layout-invalidation costs for high-frequency updates. The recommendations above are general practice, not sourced.
- No measured input-to-photon numbers exist for Avalonia 12 in `WinUIComposition` vs `LowLatencyDxgiSwapChain` modes.

## 5. Accessibility and human factors for show-critical operation

### Takeaway
Treat the desk like safety-related equipment:
- Follow the IEC 62366-1 process: a use specification, hazard-related use scenarios (wrong output armed, panic not reached, blind mistaken for live), formative tests, then summative tests with real operators in dark rooms.
- Never encode status by colour alone (WCAG 1.4.1).
- Never flash more than three times a second (WCAG 2.3.1).
- Prefer undo plus specific, rare confirmations over routine "Are you sure?" (NN/g).
- Use a dark-room theme where unselected UI is dimmed and selection and alarms stand out (grandMA3 2.5).

### Cited Findings
- WCAG 2.3.1: nothing may flash "more than three times in any one second period" unless below the general and red flash thresholds. People "are even more sensitive to red flashing". The threshold area is 341×256 CSS px, about a 10° visual field — [W3C: Understanding SC 2.3.1](https://www.w3.org/WAI/WCAG22/Understanding/three-flashes-or-below-threshold.html)
- WCAG 1.4.1: colour may not be "the only visual means of conveying information". A hue difference plus at least 3:1 relative-luminance contrast counts as a second cue. But "knowing whether an outline is green for valid or red for invalid" still needs another indicator — [W3C: Understanding SC 1.4.1](https://www.w3.org/WAI/WCAG22/Understanding/use-of-color.html)
- NN/g guidelines on confirmation dialogs:
  - Confirm only serious or irreversible actions, never routine ones ("cry wolf").
  - Be specific about consequences.
  - Give no default Yes.
  - For the most dangerous actions, require a non-standard action, such as typing a word or (Don Norman) a second person.
  - "Do go to great lengths to provide undo."
  - Source: [NN/g: Confirmation dialogs](https://www.nngroup.com/articles/confirmation-dialog/) (last reviewed 2026-08-07)
- Overused confirmations "can actually increase" errors as users rush. Undo makes experimenting safe — [NN/g: Preventing user errors](https://www.nngroup.com/articles/user-mistakes/)
- Keep consequential options away from benign ones and give them redundant visual signals. "It's okay to leverage Fitts' Law and make it a little harder to select the consequential option" — [NN/g: Consequential options close to benign](https://www.nngroup.com/articles/proximity-consequential-options/)
- In the Hawaii false missile alert, a poor confirmation screen and no undo path contributed. "Whoever is authorized to take a certain action ought also to be authorized to undo that action". "The answer is to redesign the system to be less error-prone", not to blame the operator — [NN/g: Hawaiian missile alert](https://www.nngroup.com/articles/error-prevention/)
- Modal dialogs are for preventing critical or irreversible errors. Do not interrupt high-stakes flows with them unnecessarily — [NN/g: Modal and nonmodal dialogs](https://www.nngroup.com/articles/modal-nonmodal-dialog/)
- IEC 62366-1:2015+AMD1:2020 sets out a usability engineering process tied to risk management. Its clauses: use specification (5.1); UI characteristics related to safety (5.2); hazards (5.3); hazard-related use scenarios (5.4); selecting scenarios for summative evaluation by severity (5.5); UI specification (5.6); evaluation plan (5.7); design with formative evaluation (5.8); summative evaluation (5.9) — [IEC 62366-1 preview (ToC)](https://www.technickenormy.cz/publicdoc/iec_previews/778164.pdf); [ISO catalogue entry](https://www.iso.org/standard/63179.html)
- The 2020 amendment requires root-cause analysis of every use error and use difficulty that can lead to a hazardous situation. It recognises "close calls" — [EN 62366-1/A1:2020 summary](https://standards.iteh.ai/catalog/standards/clc/b39333c5-8a2b-4f1c-8a6e-51d8681994c5/en-62366-1-2015-a1-2020)
- In IEC 62366-1 practice:
  - There is "no universal numerical pass rate"; conclusions are risk-based.
  - Formative and summative studies must be separate.
  - A use error "is not automatically a user failure" and may reveal "an ambiguous control, poor feedback… a misleading display".
  - Source: [MedTechLearning: IEC 62366-1](https://medtechlearning.com/learning/iec-62366-1-usability-engineering.html)
- FDA human-factors guidance:
  - Identify "critical tasks" by severity of potential harm, using FMEA or fault-tree analysis.
  - Validation testing uses representative users, all critical tasks, the final UI and realistic conditions.
  - Rare but hazardous scenarios need special care.
  - Source: [FDA: Applying Human Factors and Usability Engineering to Medical Devices](https://www.fda.gov/files/medical%20devices/published/Applying-Human-Factors-and-Usability-Engineering-to-Medical-Devices---Guidance-for-Industry-and-Food-and-Drug-Administration-Staff.pdf)
- Dark-room practice in consoles:
  - grandMA3 2.5 dims non-selected elements and highlights selected ones for "dark control room environments" ([MA3 2.5 features](https://help.malighting.com/grandMA3/2.5/HTML/rn_features-2-5.html)).
  - MA3 has screen-brightness and desk-light faders ([MA3 Desk Lights](https://help2.malighting.com/Page/grandMA3/Desk_Light/en/1.4)).
  - Eos offers "Hi-Contrast Displays" and adjustable LCD and backlit-button brightness ([Eos L1 workbook](https://www.etcconnect.com/uploadedFiles/Main_Site/Documents/Public/Video_Tutorial/EosGio_L1_Workbook_v2.3.0_revA.pdf)).
- Dear ImGui states that accessibility is not supported — [imgui README](https://github.com/ocornut/imgui?tab=readme-ov-file)

### Inferences
- **Status colour system:**
  - Pair every state colour with a shape or icon and a word, e.g. ARMED filled square plus label, FAULT triangle, OK check.
  - Make ok, warn and fault differ in luminance by at least 3:1, so they stay distinguishable to colour-blind operators and in monochrome photos.
  - Keep pure saturated red for one meaning only: "the air is affected now" (panic or fault). Never use it for decoration.
- **Flashing limits:**
  - Alarm blinking stays at or below 2 Hz (under the 3/s ceiling), and small.
  - No full-panel flashes.
  - QLab's "flash red once on a refused GO" is acceptable as a single event.
  - Preview thumbnails of strobing content are content, not UI. But offer "dim previews" for operators with photosensitivity.
- **Readable at distance in the dark:**
  - Large tabular figures for timecode and the next cue.
  - High contrast for selected and critical items, dimmed chrome otherwise (MA3 model).
  - A desk-wide brightness control independent of the Windows setting.
  - A daylight theme for outdoor or front-of-house use.
  - Point sizes are not sourced here (see Gaps).
- **Error prevention hierarchy** (ISO 14971 / IEC 62366 order applied to shows):
  1. Inherently safe design: one-writer engine, gated verbs, blind drafts, minimum GO interval.
  2. Protective measures: undo from the journal, armed/blind state visible everywhere, fixed panic key.
  3. Information: specific confirmations only for irreversible actions on the air, such as wiping a calibration or deleting media that is in use.
  - For the rarest, most dangerous actions, require a non-standard confirm (type the output name, or a second operator).
- **Apply an IEC 62366-1-style process** without claiming compliance:
  - Write a use specification: operators, dark FOH or booth, gloves, multi-monitor, time pressure.
  - List hazard-related use scenarios: GO on the wrong output, blind mistaken for live, panic not found, stale preview trusted as live, desk frozen while engine fine.
  - Run formative sessions in a darkened room.
  - Run a summative test of the critical tasks (GO, panic, recover after desk restart, identify a faulted output) with representative operators before 1.0. Analyse close calls as well as errors.
- **Staleness is a human-factors hazard.** Every preview shows its age; a frame older than about 2 frames gets a "stale" badge, and frozen previews grey out with the reason. This applies "attempts are not facts" to pixels.

### Gaps
- No authoritative source was retrieved for minimum type sizes at viewing distance (e.g. ISO 9241-303 or control-room standards ISO 11064), or for colour-vision-deficiency prevalence. Both are left unsourced deliberately.
- No published usability study of show-control or media-server software (beyond vendor manuals) was found.
- No source covers touch-screen use with gloves in dark booths.
