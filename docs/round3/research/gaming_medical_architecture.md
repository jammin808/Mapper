# Game-engine and medical-device architecture lessons for a Windows-only live-show renderer (2026)

Scope note: the sources are dated where it matters. "Seminal" marks older talks that later practice still rests on: Naughty Dog 2015, Ubisoft 2015, Valve 2015/2016, Frostbite 2017, Overwatch 2017, DOOM Eternal 2020. Vendor product pages and marketing are labelled as claims, not evidence. PlayMap's existing design is cited from the local tree (`docs/OUTPUT.md`, `docs/ARCHITECTURE.md`, `docs/SHOW.md`) so the inferences can say what is new and what is already there.

## Q1. Render graphs / frame graphs (Frostbite FrameGraph, Unreal RDG, Unity SRP Render Graph)

### Takeaway
Every major engine now builds a declarative graph of passes and resources. It compiles the graph: unused passes are culled, resource lifetimes are computed, transient memory is aliased, barriers and cross-queue fences are placed, and API use is checked by a validation layer. It then executes it. Correctness (barriers, lifetimes) moves out of hand-written per-feature code and into one compiler that can be tested. For PlayMap, which already has a GL render graph, the graph becomes the main correctness and diagnostics surface on D3D12/Vulkan.

### Cited Findings
- (Seminal, 2017.) Frostbite's FrameGraph splits rendering into three phases: setup, where passes declare the resources they read, write and create; compile; and execute. The graph is "built from scratch every frame". Persistent resources such as TAA history and the backbuffer are *imported*. — [Frostbite FrameGraph slides, GDC 2017](https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495); [GDC Vault abstract](https://gdcvault.com/play/1024612/FrameGraph-Extensible-Rendering-Architecture-in)
- Frostbite's compile phase culls unreferenced resources and passes, computes lifetimes, and allocates with a "simple greedy" algorithm that acquires memory right before first use and releases it after last use. Lifetimes are extended for async compute. Bind flags are derived from usage. — [Frostbite slides](https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495)
- In Frostbite, async compute is opt-in per pass. The sync point is the first use of the output on the other queue. The slides warn that it "can hurt performance if misused", and that overlap "does not always improve perf. In fact, it may sometimes hurt it." — [Frostbite slides](https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495)
- Frostbite on memory aliasing: "Must be very careful". Metadata state (FMASK/CMASK/DCC) must be valid, the first operation on an aliased resource must be DiscardResource or Clear, and the lifetimes of pipelined compute and pixel-shader work must be accounted for. — [Frostbite slides](https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495)
- The graph refactor cut Frostbite's WorldRenderer "from 15K to 5K SLOC". — [Frostbite slides (mirror)](https://en.ppt-online.org/1673409)
- In the talk's Q&A, Frostbite said it "very consciously" limited graph lifetime to exactly one frame. Multi-frame resources live outside the transient system because cross-frame aliasing "leads to vastly increased fragmentation". — [GDC 2017 talk recording](https://www.youtube.com/watch?v=1Sb3s7Xie4M)
- Unreal RDG (UE 5.8 docs) automates async compute fences, transient allocation with aliasing, split barriers, parallel command-list recording, culling of unused resources and passes, validation of API usage and dependencies, and visualisation in RDG Insights. — [Unreal RDG docs](https://dev.epicgames.com/documentation/unreal-engine/render-dependency-graph-in-unreal-engine?lang=en-US)
- RDG's validation layer is on by default in Debug and Development builds and compiled out of Test and Shipping. RDG also has debug switches: `r.RDG.ClobberResources` force-initialises resources, `r.RDG.Debug.ExtendResourceLifetimes` disables aliasing, and "Immediate Mode" disables all graph optimisations for bisecting. — [Unreal RDG docs](https://dev.epicgames.com/documentation/unreal-engine/render-dependency-graph-in-unreal-engine?lang=en-US)
- Unity's render graph (Unity 6, URP/SRP Core) avoids allocating unused resources and removes passes whose output the frame does not use. It reuses memory, synchronises the compute and graphics queues automatically, and allocates a resource just before the first pass that writes it and frees it after the last pass that reads it. Internal resources cannot cross frames unless they are imported. — [Unity URP render graph intro](https://docs.unity3d.com/Manual/urp/render-graph-introduction.html); [SRP Core render graph](https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@17.3/manual/render-graph-system.html)
- (2017, opinion/design.) The Machinery's render graph put CPU work such as culling into the same graph, so read/write declarations drive CPU parallelism too. "Root" passes, whose output is requested from outside, are never culled. — [Our Machinery blog](https://ruby0x1.github.io/machinery_blog_archive/post/high-level-rendering-using-render-graphs/index.html)

### Inferences
- PlayMap's frame topology (outputs × surfaces × warp/blend/colour/LED/send passes) changes only when the configuration changes, not every frame. The cheapest fit to the doctrine is "compile on change, execute every frame". Compile the graph on the lane when outputs or effects change, allocating freely there, and publish an immutable compiled plan (pass order, barrier lists, aliasing offsets, queue fences). The render thread only walks the plan, which keeps "allocation-free once warm" without paying Frostbite's per-frame rebuild.
- Frostbite's one-frame rule maps directly onto PlayMap's delay rings and history buffers: they must be *imported* (persistent) resources, never aliased transients.
- RDG's switches are cheap test fixtures for correctness. Run the golden-image tests twice, once with aliasing and once with lifetimes extended (and clobbered). A difference proves a missing barrier or lifetime bug. This applies "tests assert fields, not words" to the GPU.
- Managed-code mapping: graph compile is plain CPU logic and fits well in C#. Execution is a loop of pre-resolved native calls; D3D12/Vulkan calls through C# function pointers are feasible, but measure the call overhead (see Gaps). Aliasing on D3D12 needs placed resources in heaps, which is native API surface reachable through interop.

### Gaps
- No primary source found with Frostbite's exact memory savings from aliasing. The slides say "huge memory savings" and show a 720p layout, but no number was captured.
- No measured data on the CPU cost of executing a render graph from managed code.

## Q2. Job systems and task graphs (Naughty Dog fibers, Unreal Tasks/TaskGraph, Unity Jobs/Burst, enkiTS)

### Takeaway
The industry pattern is a fixed pool of workers, usually one per core, scheduling small jobs with explicit dependencies. There is no blocking wait inside jobs, and no allocation on the scheduling path. A "frame-centric" design lets the CPU, render and GPU stages each work on a different frame, each holding its own per-frame data block. Serialised access to a shared resource goes through a *pipe* or *lane* rather than a lock, which is PlayMap's "one writer" by another name.

### Cited Findings
- (Seminal, 2015.) Naughty Dog's PS4 job system for The Last of Us Remastered at 60 fps: 6 worker threads, each locked to a CPU core; 160 fibers (128 with 64 KiB stacks, 32 with 512 KiB); 3 priority queues; no job stealing. Atomic counters are the only way to synchronise. A waiting job parks its fiber and the worker picks up another job. The game ran about 800–1000 jobs per frame. — [Gyrling, GDC 2015 slides](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)
- Naughty Dog runs I/O as system threads "implemented like interrupt handlers": they read data and post a new job. Workers are locked to cores to "avoid context switches and unwanted core switching". — [Gyrling GDC 2015](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)
- Naughty Dog's frame-centric design: game logic and render logic run at the same time on *different frames*. A frame is "a piece of data… NOT a length of time". Each frame has a `FrameParams` block (frame number, delta time, skinning matrices, and per-stage start/end timestamps for Game, Render, GPU and Flip). 16 FrameParams rotate. `HasFrameCompleted(frameNumber)` controls memory lifetime. — [Gyrling GDC 2015](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)
- Naughty Dog's tagged heap is a block allocator of 2 MiB blocks. Each block is owned by a tag, there is no per-pointer `Free`, and all blocks of a tag are freed together. With one block per worker thread, "no locks required for 99.9% of memory allocations". — [Gyrling GDC 2015](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)
- Naughty Dog's stated costs: OS mutexes and condition variables cannot be used because fibers migrate between threads, so atomic spin locks are used "almost everywhere". — [Gyrling GDC 2015](https://media.gdcvault.com/gdc2015/presentations/Gyrling_Christian_Parallelizing_The_Naughty.pdf)
- The UE5 Tasks System builds DAGs through prerequisites. "Nested tasks" are completion dependencies. "Pipes" run piped tasks strictly one after another, which replaces a mutex: "pipes are lightweight… thousands of pipes without a significant drop in performance". Waiting inside tasks "should be avoided". — [UE Tasks System docs](https://dev.epicgames.com/documentation/en-us/unreal-engine/tasks-systems-in-unreal-engine)
- enkiTS (open source, zlib) is built for "zero allocations during scheduling": all task and scheduler allocations can be made at initialisation. It supports pinned tasks that run on a given thread, and priorities. Each thread has its own pipe, written at the front by its owner and stolen from at the end by others, which keeps recently written data hot in L1. — [enkiTS devlog](https://www.enkisoftware.com/devlogpost-20150822-1-Implementing-a-lightweight-task-scheduler)
- Unity's C# Job System has a safety system that "detects all potential race conditions". Jobs get copies of blittable data. Burst-compiled jobs cannot touch managed objects. If you schedule two jobs that write the same NativeArray without a dependency, the safety checks throw. TempJob allocations not disposed within 4 frames raise an error. — [Unity job system overview](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-overview.html); [Unity EntitiesSamples jobs doc](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/EntitiesSamples/Docs/jobs.md)
- Unity's tutorial example (illustrative, not a benchmark): moving an N² nearest-target loop into a job took it from about 340 ms to about 30 ms, before Burst and parallel-for. — [Unity Learn job system tutorial](https://learn.unity.com/course/basics-dots-jobs-entities/tutorial/get-started-with-unitys-job-system?version=6.0)

### Inferences
- PlayMap's "lane" is a UE Pipe, and its immutable snapshot is Naughty Dog's FrameParams. The adoption is to make the per-frame data block explicit and ring-buffered, with N slots equal to frames in flight + 1. Each slot carries the frame's show time, its target vblank and stage timestamps (lane publish, render start, submit, GPU done, shown), and is recycled when the GPU fence for that frame signals. Those timestamps feed the present ledger directly.
- The tagged heap translates to a per-frame native arena: `NativeMemory.AlignedAlloc` blocks, reset when the frame's fence completes. Transient per-frame CPU data such as constant uploads and draw lists never touches the GC heap.
- In .NET, do not put render-critical work on the shared `ThreadPool`, which other code can starve or flood. Use a small pool of dedicated worker threads pinned to cores (enkiTS-style, pre-allocated job structs, no closures). Use Unity's model, blittable structs and explicit dependencies, as the C# job ABI. A debug-only write-conflict checker, the cheap part of Unity's safety system, could be a test-only feature.
- Fibers are not practical in .NET: there is no supported fiber scheduler, and the GC must walk stacks. Use dependency DAGs plus continuation jobs instead of blocking waits.

### Gaps
- No source quantified the .NET ThreadPool's jitter against dedicated pinned threads for frame-critical work.

## Q3. Frame pacing, N frames in flight, latency-oriented (just-in-time) submission

### Takeaway
Throughput-oriented queuing, with three or more frames buffered, adds 2–4 frames of latency and hides misses. The 2020–2026 state of the art sleeps *before* sampling input or time (NVIDIA Reflex, AMD Anti-Lag 2), keeps frames in flight at 1–2, uses a waitable swap chain, and drives simulation and content from a *predicted display time* (OpenXR `predictedDisplayTime`, Vulkan `VK_EXT_present_timing`). It then checks with present statistics which refresh actually showed each frame. PlayMap's "frame = f(leader time at vblank)" is exactly this model.

### Cited Findings
- NVIDIA Reflex Low Latency mode "aligns game engine work to complete just-in-time for rendering, eliminating the GPU render queue and reducing CPU back pressure in GPU-bound scenarios". Latency markers break latency down into input, simulation, render submission, driver, render queue and GPU render. — [Reflex SDK page](https://developer.nvidia.com/performance-rendering-tools/reflex); [NVIDIA Reflex launch article, 2020](https://www.nvidia.com/en-us/geforce/news/reflex-low-latency-platform/)
- Reflex "doesn't disable [the render queue]… it just empties it": the CPU is paced so it cannot run ahead and submits "just in time for the GPU to start working without any idle gaps". The API normally lets the CPU run 2 frames ahead. — [NVIDIA Reflex article](https://www.nvidia.com/en-us/geforce/news/reflex-low-latency-platform/)
- PC Latency Stats emit ETW events (`SIMULATION_START`, `RENDERSUBMIT_START/END`, `PRESENT_START/END`, `PC_LATENCY_PING`). FrameView/PresentMon combine these into per-frame input-to-frame-start, frame-start-to-present and present-to-displayed latency, and attribute pings that fall on dropped frames to the next displayed frame. — [NVIDIA blog, May 2023](https://developer.nvidia.com/blog/understanding-and-measuring-pc-latency/)
- The Reflex QA checklist requires Reflex On to cost no more than 4% FPS, and markers to be sent whether or not low-latency mode is on. — [NVIDIA Streamline Reflex guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideReflex.md)
- `VK_NV_low_latency2` returns a per-present timing report with these timestamps: input sample, sim start/end, render submit start/end, present start/end, driver start/end, OS render queue start/end, and GPU render start/end. — [VK_NV_low_latency2 (binding docs mirroring the spec)](https://hackage.haskell.org/package/vulkan-3.26/docs/Vulkan-Extensions-VK_NV_low_latency2.html)
- AMD Anti-Lag 2 (2024 SDK, D3D11/12) inserts its delay "just before the user controls… are sampled". AMD's own latency monitor reads native latency of "3, 4 or 5" GPU frames, and 1.0–2.0 frames with Anti-Lag 2 active. — [AMD GPUOpen Anti-Lag 2 integration](https://gpuopen.com/learn/integrating-amd-radeon-anti-lag-2-sdk-in-your-game/); [AntiLag2-SDK README](https://github.com/GPUOpen-LibrariesAndSDKs/AntiLag2-SDK)
- DXGI waitable swap chains (`DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`) default to a per-swap-chain latency of 1. Microsoft advises latency 2 only when CPU and GPU each take less than a frame but together take more. The render loop waits on `GetFrameLatencyWaitableObject` before starting each frame. — [Microsoft: reduce latency with DXGI 1.3](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains)
- Without the waitable flag, DXGI queues up to 3 frames and blocks on the 4th `Present`. NVIDIA recommends 1–2 more swap chain buffers than the frames you intend to queue, with max frame latency set accordingly, "rather than relying on the OS to block… at an unexpected time". — [NVIDIA Advanced API Performance: Swap Chains, Dec 2023](https://developer.nvidia.com/blog/advanced-api-performance-swap-chains/)
- Flip-model present statistics (`GetFrameStatistics`, PresentCount, PresentRefreshCount) let an app detect a glitch: actual PresentRefreshCount later than expected. It can then recover by presenting with SyncInterval 0 for the missed frames, or with `DXGI_PRESENT_RESTART` to drop all queued presents. — [Microsoft DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model)
- Intel's D3D12 flip-model sample defines minimum latency as vsync on, 2 buffers, 1 frame, waitable latency 1. "If the application misses vsync, the frame rate will immediately drop to ½ refresh. CPU and GPU operate serially." — [Intel D3D12 flip model sample](https://www.intel.com/content/www/us/en/developer/articles/code-sample/sample-application-for-direct3d-12-flip-model-swap-chains.html)
- The Khronos Vulkan tutorial recommends 2 frames in flight; "with three or more… the CPU could get ahead of the GPU, adding frames of latency". — [Vulkan tutorial: frames in flight](https://docs.vulkan.org/tutorial/latest/03_Drawing_a_triangle/03_Drawing/03_Frames_in_flight.html)
- `VK_EXT_present_timing` (Khronos, Dec 2025) adds timing feedback for past presents and scheduling at an absolute or relative target time. A `NEAREST_REFRESH_CYCLE` flag absorbs small errors. Apps are told to "regularly rebase" target times on feedback to correct drift. NVIDIA developer drivers had it at announcement, Mesa and Android 17 "coming soon". — [Khronos blog, Dec 2025](https://www.khronos.org/blog/vk-ext-present-timing-the-journey-to-state-of-the-art-frame-pacing-in-vulkan); [VkPresentTimingInfoEXT refpage](https://github.khronos.org/Vulkan-Site/refpages/latest/refpages/source/VkPresentTimingInfoEXT.html)
- The Khronos present-timing sample lists production uses: compare ready time and display time for latency; detect target time earlier than display time by more than a refresh (a missed deadline); compare ready and dequeued times for headroom; look for irregular display-time spacing (micro-stutter). It reports FRR, VRR and adaptive-refresh modes through `refreshDuration` and `refreshInterval`. — [Vulkan swapchain present timing sample](https://docs.vulkan.org/samples/latest/samples/api/swapchain_present_timing/README.html)
- OpenXR's `xrWaitFrame` throttles the frame loop and returns a monotonically increasing `predictedDisplayTime`. "The engine simulation should advance based on the display time. Every stage in the engine pipeline should use the exact same display time for one particular application-generated frame." — [OpenXR xrWaitFrame spec](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrWaitFrame.html)
- The OpenXR Guide says to advance simulation by the delta between successive `predictedDisplayTime`s, not by measured CPU frame delta. It defines "late latch": re-sampling the pose right before queueing GPU work. — [Khronos OpenXR-Guide, frame submission](https://github.com/KhronosGroup/OpenXR-Guide/blob/main/chapters/frame_submission.md)
- (Seminal, Valve 2015.) "Running start": begin submitting 2 ms before vsync, based on measured 1.5–2.0 ms GPU bubbles. Valve calls this "a solid 1.5–2.0 ms GPU perf gain". SteamVR's separate process spins on `IDXGIOutput::WaitForVBlank()` to give apps time-since-vsync and a frame counter. A GPU query after clearing the back buffer keeps a late frame on the right side of vsync for the next frame's prediction. — [Vlachos, Advanced VR Rendering, GDC 2015](https://media.steampowered.com/apps/valve/2015/Alex_Vlachos_Advanced_VR_Rendering_GDC2015.pdf)

### Inferences
- PlayMap already uses a waitable `FLIP_DISCARD` swap chain at frame latency 1 and a present ledger that records the refresh each present was meant for and the one that showed it ([docs/OUTPUT.md](docs/OUTPUT.md)). The D3D12/Vulkan move should keep both and add:
  1. A per-frame predicted display time computed from present feedback (DXGI frame statistics, or `VK_EXT_present_timing` where available), passed unchanged through every stage. This is OpenXR's rule, and it is PlayMap's frame = f(leader time at vblank).
  2. Reflex-style markers emitted as ETW events, so PresentMon/FrameView can measure the node from outside.
- Frames in flight should be a per-output policy, not a global constant:
  - 1 for outputs carrying live inputs (IMAG camera, performer tracking, timecode-chased cues), where latency is visible;
  - 2 for heavy generative content, where throughput matters and content is a pure function of time, so extra latency is invisible if the predicted display time is honoured.
- Reflex and Anti-Lag 2 sleep before *input sampling*. In PlayMap "input" means leader time plus live-input frames. Sleep, then read the clock and the newest captured frame, then record.

### Gaps
- No source measured DXGI frame-statistics accuracy on multi-output Mosaic or frame-locked configurations.
- Whether `VK_EXT_present_timing` is on AMD Windows drivers as of October 2026 is unverified.

## Q4. Multi-queue use (graphics / async compute / copy)

### Takeaway
Use the copy (DMA) queue for system-to-VRAM uploads started several frames ahead, and async compute only for passes measured to overlap well. Always keep a serial path and a per-pass on/off switch, because async compute can lose performance. Synchronise cross-queue work in a fixed handshake so pairing is deterministic: for a show renderer, repeatable timing beats peak throughput.

### Cited Findings
- (2016.) AMD: D3D12 and Vulkan expose copy, compute and direct/graphics queues. GCN has a single geometry front end, so more than one direct queue gains nothing, and AMD had "not seen significant performance benefits from using more than one compute queue". "Manually specifying the tasks to run in parallel is more efficient than trying to automate this process." — [AMD GPUOpen, Concurrent execution with async queues](https://gpuopen.com/learn/concurrent-execution-asynchronous-queues/)
- AMD's GPUView example: a copy queue upload that waited on the previous frame gave no gain until the upload data was double-buffered, which "saved almost 10% of the total frame time". AMD also says async compute "can reduce performance when not used optimally" and should be easy to enable or disable per task. — [AMD GPUOpen async queues](https://gpuopen.com/learn/concurrent-execution-asynchronous-queues/)
- (GDC 2017.) AMD: "If copying from system memory to local, use the copy queue". Start copies "several frames" before they are needed, and do not use the copy queue for local-to-local copies, except background defragmentation at about 1% bandwidth per frame. `MakeResident` is synchronous and can fail, and oversubscribing VRAM "can cause sharp fluctuations in performance" ("try testing with two instances of your title"). — [AMD, D3D12 & Vulkan Done Right, GDC 2017](https://gpuopen.com/download/GDC2017-D3D12-And-Vulkan-Done-Right.pdf)
- AMD's async compute deep dive compares two synchronisation models. "Fire-and-forget" gives non-deterministic pairing of graphics and compute work. "Handshake" gives deterministic pairing at some cost. The advice: "Synchronize for determinism (as well as correctness)", "Always profile", "Maintain non-async paths". — [AMD GDC 2017 Async Compute Deep Dive](https://gpuopen.com/download/GDC2017-Asynchronous-Compute-Deep-Dive.pdf)
- On D3D12 one queue of each kind is guaranteed, "but no scheduling guarantees are given". — [AMD, D3D12/Vulkan lessons learned](https://gpuopen.com/download/d3d12_vulkan_lessons_learned.pdf)

### Inferences
- PlayMap mapping:
  - Copy queue: video frame uploads when the decoder output is not already in VRAM, still-image and mesh loads, and LED/send read-back staging.
  - Async compute: candidates are HAP/BC decode, the GPU HAP encoder, warp-mesh generation and scan processing. Each must be measured on both NVIDIA and AMD against a serial path and kept behind a per-pass switch that the governor and tests can flip.
  - Prefer handshake synchronisation (fixed fence points per frame), which makes timing repeatable across nodes.
- "Test with two instances" maps to running two engine processes on one GPU in CI soak tests to expose residency and budget behaviour.

### Gaps
- No 2024–2026 measurements of async compute gains on RTX 40/50 or RDNA 3/4 for compositing-type workloads were found.

## Q5. PSO caching and shader precompilation (avoiding hitches)

### Takeaway
On D3D12 and Vulkan, a first-use pipeline compile can cost tens of milliseconds, often more than 100 ms. The fix comes in layers: know or derive the full set of PSOs ahead of time, compile them before show time, and never compile on the render thread. If a PSO is not ready, skip the draw or use a precompiled fallback rather than stall. Driver updates empty the driver's cache. In 2025–2026 Microsoft added Advanced Shader Delivery (SODB to PSDB) for D3D12. Vulkan offers pipeline libraries, shader objects and explicit pipeline binaries.

### Cited Findings
- Epic (Feb 2025): when the driver sees the full state only at draw time, compilation "can take tens of milliseconds or more for a single draw command". D3D12 and Vulkan PSOs allow early compilation. UE 5.2 replaced the "Bundled PSO Cache" (recorded from play-tests) with *PSO precaching*, which derives possible PSOs at load time from materials, mesh type and global settings. — [Epic tech blog, Game engines and shader stuttering, 2025](https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem)
- Epic: Fortnite Battle Royale compiles about 30,000 PSOs per match and uses about 10,000, out of a combination space of millions. A late object "can either wait for their PSOs to be ready… or use a default material". "Fortnite takes around 20-30 seconds longer to load… when the driver cache is empty". "The cache is cleared when a new driver is installed." — [PC Gamer quoting Epic, Feb 2025](https://www.pcgamer.com/hardware/graphics-cards/epic-talks-shop-about-stuttering-in-games-that-use-its-unreal-engine-and-offers-solutions-to-the-problem/); [Epic tech blog](https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem)
- Epic's residual gaps: as of UE 5.5, global graphics shaders are not precached and can cause "rare one-time hitches". Fetching from the driver cache can still micro-stutter. Epic advises testing with `-clearPSODriverCache` and `r.PSOPrecache.Validation=2`. — [Epic tech blog](https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem)
- UE docs give three policies for a PSO that is not ready: delay proxy creation (the default, which skips the draw), substitute the default material, or block and hitch. A runtime compile counts as a "hitch" above 20 ms by default. One Insights capture showed 5–10 ms misses and one 117 ms hitch. A compile pool at 75% of hardware threads "might be a lot" during gameplay. — [UE PSO Precaching docs](https://dev.epicgames.com/documentation/en-us/unreal-engine/pso-precaching-for-unreal-engine)
- Microsoft introduced Advanced Shader Delivery in August 2025. Apps produce a State Object Database (SODB, a SQLite file of PSO/state-object descriptions plus DXIL). IHV offline compilers turn it into a Precompiled Shader Database (PSDB) that a storefront delivers per driver, and Windows updates it after driver updates. Microsoft claims "up to 10x faster" load on the ROG Xbox Ally (vendor claim). — [DirectX blog: Introducing ASD](https://devblogs.microsoft.com/directx/introducing-advanced-shader-delivery/); [Agility SDK 1.618, Sept 2025](https://devblogs.microsoft.com/directx/agility-sdk-1-618/)
- Microsoft's account of explicit PSO caching: "not everybody did it, and nobody told the driver… drivers all implicitly cached shaders anyway", so app caches duplicated driver caches. ASD's first phase feeds the driver's implicit cache. — [Agility SDK 1.618 blog](https://devblogs.microsoft.com/directx/agility-sdk-1-618/)
- ASD requirements, from the sample: Agility SDK 1.619.5 or newer, Windows 11 24H2 (26100.1000) or newer, and an ASD-capable GPU and driver. A cache hit rate of 90% or more is "a good result", verified with D3D12CacheListener (ETW). Registration through `d3dconfig` lets developers test locally. — [D3D12AdvancedShaderDelivery sample readme](https://github.com/microsoft/DirectX-Graphics-Samples/blob/master/Samples/Desktop/D3D12AdvancedShaderDelivery/readme.md); [SODB spec](https://microsoft.github.io/DirectX-Specs/d3d/StateObjectDatabase.html)
- Vulkan, from Valve's 2022 Source 2 integration: `VK_EXT_graphics_pipeline_library` lets the vertex and fragment stages compile from SPIR-V plus layout early, with a fast link at draw time (`graphicsPipelineLibraryFastLinking` is true on NVIDIA, AMD and Intel desktop). A link-time-optimised pipeline then builds on a background thread and is swapped in when ready. — [Khronos blog (Valve), 2022](https://www.khronos.org/blog/reducing-draw-time-hitching-with-vk-ext-graphics-pipeline-library)
- `VK_KHR_pipeline_binary` (Aug 2024) gives explicit, keyed pipeline binaries for app-managed caches. The sample says to store vendor ID, device ID, driver version and the key, and to treat binaries as invalid across drivers. `VK_EXT_shader_object` (2023) drops monolithic pipelines and sets performance bars, for example draw CPU cost no more than 150% of fully static pipelines. — [Khronos: explicit pipeline caching, 2024](https://www.khronos.org/blog/bringing-explicit-pipeline-caching-control-to-vulkan); [Vulkan-Samples pipeline_binary](https://github.com/KhronosGroup/Vulkan-Samples/tree/main/samples/extensions/pipeline_binary); [Khronos: Vulkan without pipelines, 2023](https://www.khronos.org/blog/you-can-use-vulkan-without-pipelines-today)
- (2020, third-party analysis.) DOOM Eternal is said to have "~500 pipeline states" in total, using a few large uber-shaders. — [Simon Coenen, DOOM Eternal graphics study](https://simoncoenen.com/blog/programming/graphics/DoomEternalStudy)

### Inferences
- PlayMap's PSO space is closed and small compared with games: a fixed pass library, plus ISF effects and generators chosen by the operator. Uber-shaders with root or push constants (id's approach) keep it in the hundreds. Every PSO in the show file can be listed at load or show-arm time, so a preflight check can assert "all PSOs for the armed show are compiled". This fits the existing Preflight in `docs/SHOW.md` and the doctrine's "measured, then governed".
- A pipeline that is not ready must never block the render thread. Use UE's default-material policy (show a held or neutral layer) and record it in STATE and the journal as a fact.
- Driver version is SOUP (see Q12). Key the app's pipeline cache by adapter LUID, vendor, device and driver version, and re-warm automatically when the driver changes.
- ASD is store-oriented. For an installed pro app the reachable part is the SODB tooling, plus local PSDB registration if Microsoft makes it available outside storefronts. Its long-term availability to non-store apps is an open question.
- Managed-code mapping: PSO creation is a native driver call made from a background C# thread. The PSO key and hash table is easy in C#, and the compile itself is native whatever the language.

### Gaps
- Whether ASD/PSDB registration is supported for non-store (MSI/MSIX) professional apps in production was not confirmed.
- No measured D3D12 numbers on how fast drivers fetch from their own cache for small PSO sets.

## Q6. Bindless and GPU-driven rendering

### Takeaway
GPU-driven pipelines hand culling, LOD and draw-argument generation to compute and issue one indirect draw for many objects. Bindless descriptor tables make that possible across materials. The reported effects are one to two orders of magnitude fewer draw calls and milliseconds of CPU and GPU saved. For a projection renderer, the main gain is a CPU-light render thread that does not scale with surface count.

### Cited Findings
- (Seminal, 2015.) Ubisoft/RedLynx GPU-driven pipeline for Assassin's Creed Unity: "1-2 orders of magnitude less drawcalls". The torture test (250,000 moving objects, 1 GB of mesh data) drew with only two `DrawInstancedIndirect` calls in a 2.3 ms GPU total. Two-phase occlusion culling uses the previous frame's depth pyramid. — [Aaltonen & Haar, SIGGRAPH 2015](https://advances.realtimerendering.com/s2015/aaltonenhaar%5Fsiggraph2015%5Fcombined%5Ffinal%5Ffooter%5F220dpi.pdf)
- (2020.) DOOM Eternal (id Tech 7): GPU triangle culling removes about 70% of submitted triangles; of a 3M budget after CPU culling, about 1M reach the rasteriser. Geometry merging draws up to 256 meshes sharing a PSO per indirect call, which "relies on completely bindless GPU pipeline". id reports "up to 5ms GPU savings in dense scenes… Similar CPU savings", a light/decal culling budget under 500 µs, and only 3 indexable resource layouts in the whole game. — [Gneiting, Rendering the Hellscape of DOOM Eternal, SIGGRAPH 2020](https://advances.realtimerendering.com/s2020/RenderingDoomEternal.pdf)
- (Practitioner opinion.) Tardif calls bindless "the most important advancement to graphics APIs delivered in the last decade". He notes that DirectX 12 Ultimate compliance guarantees it, and that ExecuteIndirect with index-buffer rebinding shipped at 60 fps or more (he cites Halo Infinite). — [Alex Tardif, Bindless programming](https://alextardif.com/BindlessProgramming.html)

### Inferences
- PlayMap's per-frame CPU work scales with outputs × surfaces × layers. With bindless (SM 6.6 `ResourceDescriptorHeap`, or Vulkan descriptor indexing) plus a GPU buffer of per-surface draw records, the render thread records a fixed handful of commands per output, whatever the show size. That directly serves "allocation-free and predictable once warm".
- Bindless also makes "content is a pure function of show time" cheap: the per-frame update becomes one constant (time) plus a small ring of changed records, written by the lane snapshot.
- Risk: a stale or uninitialised descriptor index is a top cause of GPU page faults (see Q9, DRED). Bindless needs descriptor lifetimes tied to frame fences (free-list release after N frames) and a debug validation path.

### Gaps
- No public projection-mapping or media-server source quantifies GPU-driven gains for compositing workloads, which have few objects and many large textures.

## Q7. Streaming textures and video without hitches

### Takeaway
Hitch-free streaming has four parts:
- **Asynchronous I/O and decode off the render thread.** DirectStorage and copy queues upload; decode happens on worker threads or the GPU.
- **Bounded staging budgets.**
- **A render thread that never waits.** It takes the newest ready frame and keeps the last valid one.
- **Fence-ordered handoff.**

GPU decompression (GDeflate) lifts I/O ceilings but brings its own failure mode: corrupt data causes a TDR.

### Cited Findings
- Intel's run of Microsoft's DirectStorage BulkLoad demo (9.14 GB):
  - uncompressed: 1.34 s, 6.80 GB/s, 1.43% CPU;
  - GPU decompression: 0.52 s, 17.85 GB/s, 1.15% CPU;
  - CPU decompression: 1.20 s, 7.58 GB/s, 100% CPU.

  A sampler-feedback streaming demo served 350 GB of 16k textures from a 128 MB staging buffer and about 230 MB of VRAM. — [Intel GameDev 2023, Maximum DirectStorage](https://game.intel.com/wp-content/uploads/2024/02/GameDev2023_DirectStorage_v1.pdf)
- NVIDIA: GDeflate splits data into 64 KB tiles for parallel GPU decode. The default 32 MB DirectStorage staging buffer "isn't sufficient", so start at 128 MB. Do I/O and resource creation on their own thread, separate from shader creation. — [NVIDIA: GDeflate for DirectStorage, 2022](https://developer.nvidia.com/blog/accelerating-load-times-for-directx-games-and-apps-with-gdeflate-for-directstorage/)
- DirectStorage 1.3 (July 2025) adds `EnqueueRequests`, which batches requests and synchronises with a D3D12 fence. Release note: on GPU decompression, corrupt GDeflate data does not fail cleanly "because the driver will TDR when it encounters corrupted compressed data". — [DirectX blog, DirectStorage 1.3](https://devblogs.microsoft.com/directx/directstorage-1-3-is-now-available/)
- RAD's Bink 2 decodes "4K video frames in less than one millisecond with GPU assist on modern PCs" (Epic, 2021). The SDK needs only two YUV12 buffers plus a one-second data buffer, can "decompress directly into the textures", and does I/O on a background thread. GPU compute decoding is quoted at 2–3× faster than CPU-only on Windows. These are vendor figures. — [Epic blog, Bink in UE, 2021](https://www.unrealengine.com/blog/bink-video-and-bink-audio-now-available-in-unreal-engine-for-free); [RAD Bink SDK](https://www.radgametools.com/binksdk.htm)
- An open-source C# real-time visuals tool (TiXL) shows the managed pattern. FFmpeg decode and conversion run on a worker thread "so they never block the render thread". The render thread uploads only the most recent frame, "the last-valid texture is retained, so a not-yet-ready frame never blanks output", and requests are latest-wins. A zero-copy path hands the GPU decoder surface across under a lock. The offline export path blocks for frame-exactness. — [TiXL VideoPlaybackController.cs](https://github.com/tixl3d/tixl/blob/main/Video/VideoPlaybackController.cs)
- AMD's copy queue guidance (start copies several frames early; double-buffer uploads) appears in Q4. — [AMD GDC 2017](https://gpuopen.com/download/GDC2017-D3D12-And-Vulkan-Done-Right.pdf)

### Inferences
- For video in PlayMap:
  - The decode lane runs ahead of predicted display time by a fixed number of frames, sized from the measured GOP and seek depth.
  - Decoded surfaces live in a fenced ring.
  - The render thread selects the frame for its predicted display time, so frame choice is a pure function of time, and never waits.
  - When a frame is missing, it holds the last valid frame and records a "late decode" fact. It never shows black or garbage.
- The TDR-on-corrupt-GDeflate caveat matters for an installed pro app. Validate media hashes at ingest (MEDIA FETCH) before any GPU decompression path touches the data, and keep a CPU-decode fallback.
- In managed code, I/O and decode orchestration is fine in C# (DirectStorage via COM interop; FFmpeg is already native). The render thread must touch only fences and handles.

### Gaps
- No source compared D3D12 Video decode with NVDEC/AMF for multi-stream 4K/8K ProRes/HAP playback timing jitter. ProRes and HAP are not hardware-decoded anyway.

## Q8. Deterministic simulation, replay, and multi-node determinism

### Takeaway
Games make simulation deterministic through a fixed timestep, explicit inputs, and isolating stateful systems. They use that for rollback, replay and bug reproduction. Multi-node display clusters (nDisplay) need *identical* simulation on every node, with barriers and timeouts at frame start and end. Screen-space and temporal effects (auto-exposure, bloom, reflections) break determinism across tiles.

### Cited Findings
- (2017.) Overwatch's simulation runs on fixed 16 ms command frames (7 ms in tournament mode). It is ECS: about 46 client systems and 103 component types, with only 3 systems (movement, weapon, state script) responsible for netcode. Deterministic movement lets the client roll back to server state and replay buffered inputs. — [GDC Vault, Overwatch Gameplay Architecture and Netcode](https://gdcvault.com/play/1024001); [Edgegap summary of the talk](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback)
- Ford's warning: a fixed simulation that cannot keep up enters "a death spiral" (each late frame forces more ticks). — [GDC 2017 talk recording](https://www.youtube.com/watch?v=W3aieHjyNvw)
- Overwatch's replay system serves both players (kill cams, highlights) and engineering: "replays prove invaluable in reproducing bugs". — [GDC Vault, Replay Technology in Overwatch](https://www.gdcvault.com/play/1024053/Replay-Technology-in-Overwatch-Kill)
- In the nDisplay whitepaper, cluster nodes "effectively execute identical gameplay and physics simulation to the Primary PC". The primary distributes inputs, tracking and events, "ensuring all PCs receive and acknowledge the same inputs… at the same time". Auto exposure, bloom, planar reflections and some shader effects "are not supported"; they can be used but cause visible discontinuities. — [nDisplay whitepaper](https://cdn2.unrealengine.com/Unreal+Engine%2Fndisplay-whitepaper-final-updates%2FnDisplay_Whitepaper_FINAL-f87f7ae569861e42d965e4bffd1ee412ab49b238.pdf)
- nDisplay's game and render threads are synchronised by barriers that "cannot be disabled". Configurable timeouts cover game start, frame start, frame end and render sync. A node that cannot connect after N retries quits, and if any node misses the game-start barrier "all instances in the cluster will shut down". — [nDisplay synchronization (UE 4.27)](https://docs.unrealengine.com/4.27/en-US/WorkingWithMedia/IntegratingMedia/nDisplay/Synchronization/); [nDisplay ports and timeouts](https://docs.unrealengine.com/4.27/en-US/WorkingWithMedia/IntegratingMedia/nDisplay/Ports/)

### Inferences
- PlayMap's "content = f(show time)" is stronger than Overwatch's model, because there is no integrated state. The lesson is to *police* it:
  - Ban frame-delta integration in generators and effects. Temporal effects (feedback, trails, particle state) must be seeded and reconstructible from time, or declared "node-local, non-deterministic" in STATE.
  - Give every random source a seed from show time and surface ID (Pcg32 already does this).
  - Add a golden test: render frame N on two engines and compare hashes.
- Like nDisplay, flag screen-space and temporal effects that span outputs (bloom, auto-exposure, feedback) as cross-output discontinuity risks in preflight.
- Replay for bug reproduction: the journal plus the recovery record plus a recorded leader-time track is enough to re-render any frame offline. That is the game industry's "replay proves bugs" in PlayMap terms.

### Gaps
- No public detail found on how disguise or Notch guarantee determinism of generative content across RenderStream cluster nodes.

## Q9. Telemetry and in-field crash capture (DRED, Aftermath, GPU dumps, Crashpad/Sentry, PresentMon)

### Takeaway
GPU crashes (device removed/TDR) are only diagnosable if breadcrumbs, page-fault attribution and dumps are armed *before* the failure. CPU crashes are best captured out of process. Frame-pacing telemetry is available from ETW without touching the renderer. A show product should ship all three, gated so that no secrets enter dumps.

### Cited Findings
- DRED auto-breadcrumbs use `WriteBufferImmediate` to write a progress counter after every render op (draw, dispatch, copy, resolve…) into system memory that survives device removal. DRED 1.1 adds GPU page-fault reporting that matches the faulting virtual address against live and recently freed objects. DRED must be enabled before the device is created. — [Microsoft: Use DRED](https://learn.microsoft.com/en-us/windows/win32/direct3d12/use-dred)
- DRED tracks "the 65,000 most recently freed objects". "Premature release of objects in-flight is one of the top causes for unexpected device removals". Since Windows 10 1903, user-mode device-removed events go to Windows Error Reporting, and Microsoft may temporarily turn DRED on for customer configurations with many failures. — [DRED spec](https://microsoft.github.io/DirectX-Specs/d3d/DeviceRemovedExtendedData.html); [Microsoft: Use DRED](https://learn.microsoft.com/en-us/windows/win32/direct3d12/use-dred)
- Microsoft's DRED WinDbg extension reads `!d3ddred` from a heap dump to name the command list, the queue, the last completed op and the outstanding ops ("the first outstanding operation is the strongest suspect"), and the freed resource whose name matched the faulting address. — [DirectX blog, DRED debugger extension, 2019](https://devblogs.microsoft.com/directx/debugger-extension-for-dred/)
- The D3D12 GPU dump spec describes runtime-generated dumps on device error, in development and retail, with no setting needed. Begin and end callbacks let the app add blobs (2 MB limit total). A new `GetDeviceErrorCode` distinguishes hung from page-fault errors. Status: spec with preview-stage notes. — [DirectX-Specs: D3D12 GPU dump files](https://microsoft.github.io/DirectX-Specs/d3d/D3D12GpuDumps.html)
- NVIDIA Nsight Aftermath produces GPU crash dumps for D3D12 and Vulkan. With resource tracking it identifies resources at faulting addresses, and it can map shader microcode to DXIL/SPIR-V lines using debug-info files (embedded in R615+ drivers). — [Nsight Aftermath user guide](https://docs.nvidia.com/nsight-aftermath/UserGuide/gpu-crash-dump-setting-up.html)
- Vulkan device-lost is "sticky" and requires recreating the logical device. The `VK_KHR_device_fault` proposal extends `VK_EXT_device_fault` to report even recoverable faults, with blocking queries and a separate vendor-binary retrieval API (proposal status). — [Vulkan proposal VK_KHR_device_fault](https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_device_fault.html)
- Sentry Native compares crash backends. Crashpad and `native` run out of process and upload at crash time. They handle Windows fast-fail, `abort()` and heap corruption, and the handler "is not affected by any corruptions that led to a crash". In-process breakpad and inproc upload on the next run, and inproc captures only the crashing thread. — [Sentry Native backend tradeoffs](https://docs.sentry.io/platforms/native/advanced-usage/backend-tradeoffs/); [Sentry Native backends](https://docs.sentry.io/platforms/native/configuration/backends/)
- Sentry notes that minidumps "may contain sensitive information… environment variables… passwords" and are not scrubbed when stored as attachments. — [Sentry Unreal crash reporter docs](https://docs.sentry.io/platforms/unreal/configuration/crash-reporter/crash-reporter-client/)
- PresentMon (open source, ETW-based) reports per-frame CPU, GPU and display durations and latencies across D3D, OpenGL and Vulkan: DisplayLatency, DisplayedTime, dropped frames, present mode, click-to-photon. v2.2 cut ETW event latency from 1000 ms to about 30 ms. — [PresentMon repo](https://github.com/GameTechDev/PresentMon/); [PresentMon console README](https://raw.githubusercontent.com/GameTechDev/PresentMon/main/README-ConsoleApplication.md); [PresentMon v2.2.0 release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.2.0)

### Inferences
- Ship DRED on by default. The breadcrumb cost is in the command stream; measure it, but a show renderer with tens of passes is unlikely to notice. Also ship custom per-pass breadcrumbs and pass names written into the journal. On device removal, write a "GPU incident" record (breadcrumbs, page-fault matches, adapter, driver) to the journal and STATE before recreating the device.
- Integrate Aftermath on NVIDIA, opt-in or always-on for touring rigs, and the AMD equivalent (Radeon GPU Detective). Neither was researched in depth here; see Gaps.
- CPU crash capture should be out of process (Crashpad-style). It fits PlayMap's two-process design: the desk or a small supervisor can hold the handler. "Secrets ride in no line, no journal row" extends to dumps: use filtered or reduced-memory minidumps and never keep full-heap dumps as attachments without scrubbing.
- PresentMon and ETW give an *external* observer of frame delivery that is independent of the engine's own present ledger. Run it as a cross-check in soak tests, and possibly as a field "second opinion" through the PresentMon service API. That echoes the medical principle of an independent monitor (Q12).

### Gaps
- AMD Radeon GPU Detective and Vulkan `VK_EXT_device_fault` driver support on Windows AMD was not researched.
- .NET-specific crash dump collection (createdump / `DOTNET_DbgEnableMiniDump`, WER LocalDumps) was not fetched. Managed exception capture alongside native minidumps is therefore unverified here.
- Retail availability of D3D12 GPU dump files is unclear: the spec mentions preview behaviour.

## Q10. Stability practices: device-lost recovery, watchdogs, degraded modes

### Takeaway
Treat device removal as a normal event with one rebuild path that is also the startup path. Test it with forced TDRs. Make watchdogs observe the *work itself* (a per-frame heartbeat), not a timer that any thread can reset. Prefer graceful degradation (hold the last frame, reproject or resubmit, lower resolution) to visible failure. Valve and Meta treat these as last-resort safety nets, not ways to hit frame rate.

### Cited Findings
- Microsoft: on `DXGI_ERROR_DEVICE_REMOVED` or `DEVICE_RESET`, release every device resource and the swap chain, recreate the device, then notify components to rebuild. Microsoft suggests a single code path "similar, if not identical" to startup initialisation. `GetDeviceRemovedReason` distinguishes a driver upgrade or physical removal, which is "normal", from `DEVICE_RESET`, where repeated occurrences suggest invalid commands. `dxcap -forcetdr` forces a TDR for testing. — [Microsoft: Handle device removed scenarios](https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios); [Microsoft: Lost devices](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/lost-devices)
- WDDM TDR: the OS preempts, then resets the GPU, purges video memory and restores the desktop. "Some legacy DirectX applications might just render black at the end of this recovery". Well-written apps release and recreate the device. — [Microsoft: WDDM Timeout Detection and Recovery](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery)
- (2016.) Valve: reprojection "should be thought of as a last-resort safety net… DO NOT rely on reprojection to maintain framerate". Make the render thread autonomous: "If CPU isn't ready with a new frame… render thread resubmits last frame's GPU workload with updated HMD poses". — [Vlachos, Advanced VR Rendering Performance, GDC 2016](https://media.steampowered.com/apps/valve/2016/Alex_Vlachos_Advanced_VR_Rendering_Performance_GDC2016.pdf)
- Valve's Adaptive Quality rules:
  - target 70–90% GPU utilisation;
  - drop 2 levels when the last frame finished after 90% of the frame budget;
  - raise 1 level only after 3 frames below 70%;
  - predict with linear extrapolation;
  - keep a "10% idle rule" so other processes and the desktop get GPU time.

  You "can't toggle visual features like specular… shadows" mid-run, but you can adjust resolution and MSAA. — [Vlachos GDC 2016](https://media.steampowered.com/apps/valve/2016/Alex_Vlachos_Advanced_VR_Rendering_Performance_GDC2016.pdf)
- Valve, quoting Oculus (Antonov 2015): "ATW should run at a fixed fraction of the game frame rate… 90Hz… or… 45Hz… Rendering at an intermediate rate, such as 65Hz… is a worse artifact." — [Vlachos GDC 2016](https://media.steampowered.com/apps/valve/2016/Alex_Vlachos_Advanced_VR_Rendering_Performance_GDC2016.pdf)
- Meta's compositor "always has the most recently submitted frame available". On a miss it re-displays the previous frame with rotational TimeWarp, and the display refresh rate never changes. A "Stale" counter reports re-displayed frames per second; steady non-zero values mean inconsistent delivery, which users see as judder. — [Meta: missed frames](https://developers.meta.com/vr/documentation/native/android/os-missed-frames/)
- In medical safety architecture: "hardware thread monitors avoid the problems with software reset watchdog timers by looking for 'heartbeats'… indicating thread liveness", because "watchdog timers too often have been reset inappropriately". — [AAMI BI&T 2017, Principles of explicitly designed medical device safety architecture](https://array.aami.org/doi/full/10.2345/0899-8205-51.5.380)

### Inferences
- PlayMap already restarts the engine from a watchdog with a recovery record, rebuilds on device loss, and has a never-stall barrier watchdog ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/OUTPUT.md](docs/OUTPUT.md)). The lessons sharpen these:
  1. The watchdog heartbeat should be the *present ledger's shown-frame counter advancing*, not a thread's tick. A thread can tick while the picture is frozen.
  2. Device-removed rebuild should be exercised in CI with a forced TDR on Windows runners (`dxcap -forcetdr`), with assertions on time-to-first-good-frame and the STATE fields.
  3. Degraded modes in order of preference: resubmit the last frame's GPU work at the new time (Valve's autonomous render thread, valid because content = f(time)); then hold the last shown frame; then switch to backup. Never show black unless the show policy says so.
- Adaptive quality is risky for projection: a resolution drop on a projector is visible and breaks blend seams. If adopted, restrict it to generative layers behind a show-policy switch and use Valve's asymmetric hysteresis. Fixed-fraction degradation (60 to 30 for a heavy layer) beats free-running rates.
- A "safe mode" (start with effects off, minimal outputs) is a common pattern but unsourced here; see Gaps.

### Gaps
- No primary source was found for game "safe mode after crash" practices. The default Windows TDR delay is not sourced.

## Q11. What Unreal nDisplay and disguise/Notch borrow from games

### Takeaway
Both wrap a game engine's real-time renderer in a cluster layer: a primary that distributes time, input and events; deterministic per-node simulation; software barriers; and hardware framelock and genlock (NVIDIA Quadro/RTX PRO Sync swap barriers) for tear-free simultaneous swaps. disguise's RenderStream drives third-party engines with timestamped frame requests and returns textures over RDMA networks.

### Cited Findings
- nDisplay: the primary PC manages and dispatches inputs, tracking data and cluster events "in a synchronous manner", and manages timing. Cluster nodes render extra frustums and are "frame-locked and genlocked". — [nDisplay whitepaper](https://cdn2.unrealengine.com/Unreal+Engine%2Fndisplay-whitepaper-final-updates%2FnDisplay_Whitepaper_FINAL-f87f7ae569861e42d965e4bffd1ee412ab49b238.pdf)
- Epic on sync topologies:
  - framelock-only (daisy chain) allows NVIDIA swap groups and barriers;
  - genlock-only "has not proven to be 100% reliable because there is no accurate control over frame presentation";
  - genlock plus framelock with NVIDIA barriers is "the most reliable";
  - without hardware sync, "expect at least a one-frame hardware delay".

  — [nDisplay synchronization docs](https://docs.unrealengine.com/4.27/en-US/WorkingWithMedia/IntegratingMedia/nDisplay/Synchronization/)
- NVIDIA RTX PRO Sync (the rebranded Quadro Sync II) has one timing server per cluster. Display timings must be within ±50 ppm. The swap-barrier signal travels over the RJ45 framelock chain, which is not TCP/IP. Framelock is enabled last, after Mosaic and other settings. — [NVIDIA RTX PRO Sync user guide](https://images.nvidia.com/aem-dam/Solutions/design-visualization/quadro-product-literature/nvidia-rtx-pro-sync-user-guide.pdf)
- disguise RenderStream: Designer sends timestamped "frame request" messages with metadata, and render engines (Unreal, Unity, Notch, TouchDesigner) answer with finished textures. The API includes "special tools… to support clustered rendering in-sync". — [disguise: Extending RenderStream](https://help.disguise.one/workflows/renderstream/extending-renderstream)
- RenderStream remote mode uses 25/100 GbE Mellanox ConnectX-5/6 NICs (Rivermax), RoCE v2 lossless, IGMP snooping and fast-leave, and needs genlock when fragments go out on multiple actor machines. Local mode renders on the output server "which minimises interaction delay". — [disguise RenderStream networking](https://help.disguise.one/designer/networking/renderstream-networking); [RenderStream overview](https://help.disguise.one/workflows/renderstream/renderstream-overview)
- Notch over RenderStream can split a block across render nodes only if it exposes a camera; otherwise content is duplicated. — [disguise: RenderStream–Notch](https://help.disguise.one/workflows/renderstream/notch/renderstream-notch)

### Inferences
- RenderStream's "timestamped frame request, then texture response" is the industry form of PlayMap's frame = f(leader time). It argues for a PlayMap *render-request* seam: a member renders "frame for leader time T, view V" and returns a texture or stream. That would let PlayMap host or be hosted by other engines.
- The nDisplay timeouts and the "all instances shut down" policy are a fail-closed rule for cluster *startup*. During a running show the opposite is wanted: degrade per node and keep the rest on air. PlayMap's existing barrier "falls out" rule ([docs/OUTPUT.md](docs/OUTPUT.md)) is the better show policy and is worth stating as a deliberate difference.

### Gaps
- No public technical detail on disguise's internal render loop, frame pacing or frozen-frame detection.
- No public architecture document for Notch's runtime.

## Q12. Medical software lifecycle and risk practice (IEC 62304, ISO 14971, IEC 62366-1, FDA): what to borrow

### Takeaway
Borrow the *mechanisms*, not the paperwork:
- **Risk-based rigour.** Classify by worst-case harm, assume software failure probability = 1, and credit only *external* controls.
- **Designed and verified segregation.**
- **A SOUP list** with exact versions and anomaly monitoring.
- **Hazard-to-test traceability.**
- **Risk-control priority order.** Design, then protective measures, then warnings.
- **Verification of implementation and effectiveness.**
- **Use-error analysis with formative and summative evaluation.**
- **An independent safety subsystem** that detects, notifies, mitigates and records.
- **An SBOM, threat model and patch plan.**

### Cited Findings
- IEC 62304 Amendment 1 (2015): a software system is Class A if it cannot contribute to a hazardous situation, or if the risk is acceptable after controls *external* to the software. Otherwise it is Class B (non-serious injury possible) or Class C (death or serious injury possible). "The probability of a software failure shall be assumed to be 1". Internal watchdogs or range checks do not lower the class. — [Kelsey Quality IEC 62304 §4.3 crosswalk](https://kelseyqms.com/crosswalk/iec-62304/req-iec62304-4-3); [CASRAI IEC 62304 guide](https://www.casrai.org/guides/iec-62304)
- A system takes the class of its highest-class item unless segregation is designed, documented and verified. The segregation requirement (clause 5.3.5) is itself Class C. — [CASRAI IEC 62304 guide](https://www.casrai.org/guides/iec-62304)
- The notified bodies' FAQ (Team-NB): segregation can be functional, logical or physical. Verification should show that non-safety items cannot corrupt safety data, control flow or the execution environment, and may include "creating a stress situation". One risk control may cover several causes, since per-cause controls "may lead to complex and… less safe software". — [Team-NB EN 62304 FAQ](https://www.team-nb.org/wp-content/uploads/2015/05/documents2013/FAQ_62304_Final_130804.pdf)
- IEC 62304's four SOUP obligations:
  1. identification (title, manufacturer, unique version), for all classes;
  2. functional and performance requirements relied on;
  3. required hardware and software environment;
  4. evaluation of published anomaly lists for hazardous sequences.

  A maintenance plan keeps monitoring new anomalies, and internal code without records is also SOUP. — [CASRAI IEC 62304 guide](https://www.casrai.org/guides/iec-62304); [Kelsey IEC 62304 lifecycle guide](https://kelseyqms.com/guides/iec-62304-software-lifecycle)
- IEC 62304 Edition 2 status, October 2026: not published. About 1,500 comments on the 2025 CD led to a second CD, and the IEC forecasts publication in October 2028. The draft replaces classes A/B/C with two "process rigor levels": Level I is about Class A plus architecture and integration requirements; Level II absorbs B and C at about C's rigour. Scope widens to "health software", AI/ML planning is added, and cybersecurity is delegated to IEC 81001-5-1. — [MD Regulatory, IEC 62304 guide, 2026](https://mdregulatory.com/iec-62304/); [Quickbird Medical, June 2026](https://quickbirdmedical.com/en/news/iec-62304-edition-2-august-2026/); [IEC SC62A Ed. 2 design specification](https://assets.iec.ch/public/sc62a/IEC%2062304%20Ed.%202.0%20Design%20Specification.pdf)
- ISO 14971:2019 covers risk management over the whole life cycle, including data and systems security and usability, and requires criteria for acceptability (without setting levels). The 2019 edition adds benefit-risk analysis and stronger production and post-production feedback. — [ISO 14971:2019](https://www.iso.org/cms/live/live/en/sites/isoorg/contents/data/standard/07/27/72704.html); [ISO 14971:2019 preview text](https://cdn.standards.iteh.ai/samples/72704/121784023c8a4101a1940070b83a55e2/ISO-14971-2019.pdf)
- ISO 14971 clause 7.1 sets the risk control option order: inherently safe design, then protective measures (including alarms), then information for safety. FMEA (IEC 60812:2018) is bottom-up and FTA (IEC 61025) top-down. FMEA "rarely" works alone because it misses combinations, which need FTA, HAZOP or STPA. STPA names four kinds of unsafe control action: not provided; provided; too early, too late or out of order; applied too long or stopped too soon. — [spilma, risk management guide](https://www.spilma.com/en/guides/risk-management-iso-14971-iec-31010-fmea-fta)
- Verification has two halves: *implementation* (was the control built as specified) and *effectiveness* (does it reduce the risk in the scenario). Test boundary, fault and abnormal conditions, "not only nominal operation", and confirm the control adds no new hazards, for example nuisance alarms or a protective stop that is not itself harmless. — [MedTechLearning, medical device risk management](https://medtechlearning.com/learning/medical-device-risk-management.html)
- FDA (risk management slides): use PHA early, FTA top-down, FMEA bottom-up, and "more than one risk management technique". — [FDA CDRH: Risk management activities within a quality system](https://www.fda.gov/media/163915/download)
- IEC 62366-1:2015+A1:2020 process (clauses 5.1–5.10):
  - use specification;
  - user-interface characteristics related to safety and potential use errors;
  - hazards;
  - hazard-related use scenarios, with selection for summative evaluation;
  - UI specification;
  - evaluation plan;
  - formative evaluation during design;
  - summative evaluation on the production-equivalent UI;
  - "user interface of unknown provenance".

  — [IEC 62366-1+A1 preview TOC](https://www.technickenormy.cz/publicdoc/iec_previews/778164.pdf); [ISO page for IEC 62366-1](https://www.iso.org/standard/63179.html)
- In summative evaluation, scenarios are chosen by risk: "evaluating everything is infeasible and evaluating favourites is indefensible" (consultancy opinion). — [SPEQ on IEC 62366-1](https://www.speqgx.com/standards/iec-62366-1)
- FDA June 2023 premarket software guidance:
  - Basic vs Enhanced documentation, with Enhanced where failure could pose a probable risk of death or serious injury, "assessed prior to implementation of risk control measures";
  - traceability from hazard ID to risk control, SRS, SDS and unit, integration and system test IDs;
  - unresolved anomalies deferred only with a risk assessment;
  - for Enhanced, unit and integration protocols with objective pass/fail.

  — [FDA guidance PDF](https://www.fda.gov/media/153781/download); [Innolitics annotated guidance](https://innolitics.com/articles/fda-guidance-content-of-premarket-submissions-for-device-software-functions/)
- FDA reviewers check traceability "via other software documentation… we want to see, at a minimum, test cases to requirement". — [FDA webinar transcript, July 2023](https://www.fda.gov/media/170714/download)
- FDA cybersecurity guidance (current version February 2026, superseding June 27 2025):
  - secure product development framework;
  - threat modelling over the full system and life cycle;
  - risk assessed by *exploitability* rather than probability;
  - a machine-readable SBOM to NTIA minimum elements, plus support level and end-of-support date per component;
  - assessment of known vulnerabilities, including CISA KEV;
  - four architecture views: global system, multi-patient harm, updateability/patchability, security use cases.

  Section 524B requires cyber-device makers to plan post-market vulnerability monitoring and to patch on a regular cycle, with critical fixes out of cycle. — [FDA cybersecurity guidance page](https://www.fda.gov/regulatory-information/search-fda-guidance-documents/cybersecurity-medical-devices-quality-management-system-considerations-and-content-premarket); [guidance PDF](https://www.fda.gov/media/119933/download); [FDA webinar slides](https://www.fda.gov/media/173516/download)
- Explicit safety architecture (AAMI BI&T, 2017): split the design into an *operational* subsystem and a *safety* subsystem that detects faults and use errors, notifies, mitigates by forcing a known safe state, and records. The safety subsystem runs on separate resources so operational faults cannot propagate into it, and keeps a nonvolatile hardware fault log because "a malfunction may prevent execution of threads". — [AAMI BI&T 2017](https://array.aami.org/doi/full/10.2345/0899-8205-51.5.380)
- The safety system should be "as simple as possible… functionally independent… well-defined interfaces… fully verifiable", often a separate microcontroller or FPGA with a windowed watchdog over the main processor. Without isolation, "all functions… must be treated at the highest level of criticality". — [MDDI, Medical device safety system design, 2012](https://www.mddionline.com/design-engineering/medical-device-safety-system-design-a-systematic-approach)
- IEC 60601-1 requires "single fault safe": a first fault can occur at any time; if it is not detected, a second independent fault must be assumed. Hardware faults are random; software faults are systematic, so redundancy must be *diverse* (different hardware, toolchain, SOUP or team) or it duplicates the bug. A single channel with a watchdog or test function is the simpler option. — [Johner Institute: functional safety, 2023](https://blog.johner-institute.com/systems-engineering/functional-safety/)
- Common architectures: redundant with voting (fault-tolerant), control plus protective (fail-safe, "a large number of therapeutic medical devices"), single (inherently safe). Protection types are CPP, CP and C. — [Sagentia via Today's Medical Developments, 2011](https://www.todaysmedicaldevelopments.com/article/tmd-0911-functional-safety/)

### Inferences
- **Classify PlayMap by "harm to the air".** Assume software failure probability 1 and credit only external controls (backup node, LED-processor input failover, projector "no signal → black or logo", a changeover router):
  - Rigour Level II (C-like): render, present, sync, show gate, PANIC/BLACKOUT paths.
  - Level I: desk UI, assistant, scan tools, provided the engine is independent of the desk, which "the desk has no private API" already makes true.
  - This is a design tool, not a certification.
- **Air hazard analysis (FMEA plus FTA plus STPA-style unsafe control actions).** Top events:
  - black on air;
  - frozen frame;
  - tear;
  - wrong content or cue;
  - wrong geometry or blend (visible seam);
  - output desync across surfaces;
  - colour or brightness jump;
  - photosensitive flash (a real audience-harm hazard);
  - projector shutter, lamp or laser misfire;
  - stale timecode chase;
  - crash and restart.

  For each: detection (present ledger, watermark loopback, camera), control (backup, hold, fade, interlock), and a test ID. PlayMap's verbs and STATE already give the "control action" vocabulary STPA needs (GO too early or late, BLACKOUT not provided).
- **Implementation and effectiveness.** PlayMap tests already assert fields. Add effectiveness tests that inject faults (forced TDR, killed decode lane, network partition, SDI loss) and assert that the air stayed within policy, not merely that the code ran.
- **SOUP list.** GPU drivers, FFmpeg, ANGLE (while it remains), Avalonia, Silk.NET/Vortice/TerraFX, ONNX runtime, NIC drivers and firmware, LED processor firmware. Record exact versions, the behaviour relied on, an anomaly review, and a "show-locked" driver set per tour. The PSO cache key (Q5) is the same version identity.
- **Usability (62366).** Hazard-related use scenarios for operators: GO on the wrong cue, edits landing live instead of blind, PANIC misfire, an unlabelled intentional freeze mistaken for a failure, or the reverse. Run formative evaluation with operators and summative evaluation on the release candidate for the top scenarios.
- **Cyber.** Shows run on networks (Show Bus, MCP, web). Generate a CycloneDX SBOM per build, write a threat model, and keep an updateability view. This also lines up with "fail closed on the wire".

### Gaps
- No evidence was found of entertainment-technology vendors formally applying 62304/14971. The mapping is an inference.
- The ISO 14971 sources used are summaries and previews; the full standard text was not read.

## Q13. Medical imaging display pipelines: GSDF calibration, latency budgets, redundancy, frozen/torn frame detection

### Takeaway
Medical display practice contributes three things:
- **Calibrate to a standard transfer function in measured luminance**, including ambient light (DICOM GSDF), so pictures look the same across displays.
- **Treat latency as a hard human-performance budget.** Surgical hand-eye tasks degrade measurably at about 100 ms, and FPGA pipelines hit under one frame.
- **Treat a frozen display as the most dangerous failure because it looks normal.** Patents and practice use a changing secure pattern or counter checked downstream, alerting or blanking when it is stale.

### Cited Findings
- DICOM PS3.14 GSDF covers 0.05 to 4000 cd/m², spanning 1023 just-noticeable differences derived from Barten's model of human contrast sensitivity, and "explicitly includes the effects of the diffused ambient Illuminance". It is defined by interpolating those levels (log-luminance deviation of 0.3% at most). — [DICOM PS3.14 ch. 7](https://dicom.nema.org/medical/dicom/current/output/chtml/part14/chapter_7.html)
- GSDF's aims are similar grayscale rendition across displays of different luminance and efficient use of display levels. Levels that are "perceptually indistinguishable… are wasted. If they are too far apart, the observer may see contours". Digitisation "is limited to 8 or 10 bits". Perceptual linearisation is a means, "not considered an objective by itself". — [DICOM PS3.14 Annex A](https://dicom.nema.org/medical/dicom/current/output/chtml/part14/chapter_A.html)
- Peer-reviewed latency results:
  - Kumcu et al. 2016: 29 participants (15 trainees, 14 laparoscopic surgeons) at six latencies under 200 ms showed "significant deterioration in performance and user experience at 105 ms added latency", and "surgeons were more negatively affected". — [Kumcu et al., Int J Med Robot 2016](https://doi.org/10.1002/rcs.1758)
  - Nankaku et al. 2022: in a dynamic environment, delays of 100 ms or less "may be acceptable", but performance already differed significantly between 0 and 70 ms. — [Nankaku et al., PLOS One 2022](https://journals.plos.org/plosone/article?id=10.1371%2Fjournal.pone.0274328)
  - Takahashi et al. 2022 (hinotori): 30 and 50 ms rated feasible; at 100 and 150 ms "surgery was not feasible for the most part". — [Takahashi et al., Asian J Endosc Surg 2022](https://doi.org/10.1111/ases.13150)
  - Nakauchi et al. 2022: a threshold under 125 ms, with an actual end-to-end of 27 ms (25 ms local processing plus 2 ms network). — [Nakauchi et al., Langenbecks Arch Surg 2022](https://doi.org/10.1007/s00423-022-02710-6)
  - (Older.) Anvari et al. 2005: tasks were completable up to 500 ms, with rising errors and time. — [Anvari et al., Comput Aided Surg 2005](https://www.tandfonline.com/doi/pdf/10.3109/10929080500228654)
- Engineering practice (vendor sources):
  - Endoscope CCUs use FPGAs so that "the video stream is processed only by high-performance logic", and surgical systems may "avoid using compression altogether due to the inherent lag". — [Xilinx WP391](https://docs.amd.com/api/khub/documents/NeMcOgT9e_pYU73Aj_wnTg/content)
  - A Tier-1 endoscopy 4K FPGA pipeline with a "<1 frame" latency requirement. — [Neurealm case study](https://www.neurealm.com/case-studies/video-pipeline-implementation-for-an-endoscopy-device/)
  - Enclustra claims "maximum image latency allowed for endoscopic applications… between 50 and 150 milliseconds". — [Enclustra](https://www.enclustra.com/en/projects/low-latency-4k-medical-endoscopy/)
- Measured software pipelines:
  - NVIDIA Clara Holoscan endoscopy: a GStreamer pipeline added 146 ms and the GXF graph pipeline with AI added 51 ms, both over a 74 ms camera-to-monitor baseline. — [NVIDIA Clara Holoscan endoscopy whitepaper](https://developer.download.nvidia.com/CLARA/Endoscopy-Whitepaper-Clara-Holoscan.pdf)
  - RidgeRun's glass-to-glass test (photographing a millisecond clock and the live output together) measured 35.95–68.37 ms. Exclusive display versus a running display manager was 41.61 vs 49.62 ms; power profile and clocks also mattered. — [RidgeRun, Holoscan Sensor Bridge glass-to-glass, 2024](https://www.ridgerun.com/post/glass-to-glass-assessment-of-the-holoscan-sensor-bridge-on-a-nvidia-agx-orin)
- Frozen-frame detection, US Patent 10,383,581: each vital-sign frame carries a secure pattern (time or sequence counter) that is updated over time. A monitoring device reads the frame actually in the display buffer and checks the pattern is current; if not, it alerts or blanks the screen, optionally restarting the display "like a watchdog". The patent states that a frozen display "represents a critical situation, since… the operator… will not notice this situation immediately". — [US 10,383,581](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/10383581)
- US Patent 11,462,140: a colour or light sensor at the display reads displayed test images, an alert fires on mismatch, and backlight drive current is logged as telemetry for predictive service. — [US 11,462,140](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11462140)
- Field reality: an FDA MAUDE report (2024) of an operating-room patient monitor where "it stops and the screen is frozen", requiring reboot. — [FDA MAUDE report 19221498](https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfmaude/detail.cfm?mdrfoi__id=19221498&pc=MHX)
- A clinical guide (vendor blog) calls a frozen display "arguably the most dangerous display problem because it is deceptive", detected by waveforms that stop scrolling or a stopped on-screen clock. — [Med-Linket troubleshooting guide, 2026](https://med-linket-corp.com/blogs/news/monitor-display-problems-blank-flickering-frozen)
- Monitors label an *intentional* freeze: "When the waveforms on the screen are frozen, the FREEZE prompt will appear". — [Contec CMS8000 service manual](https://www.scribd.com/document/362534289/Monitor-Contec-CMS8000-Service-Manual-v1-0)

### Inferences
- **Frozen and torn frame detection for the air.** PlayMap's present ledger proves a present was *shown* according to the driver. The medical lesson is to prove what was *displayed* by observing it downstream:
  - **Watermark.** Encode a per-frame counter or show-time code into pixels: a blanked-edge row, an overscan or masked region, an LED-tile region outside the canvas, or an alpha-free corner on outputs with a capture loop.
  - **Independent observer.** Have it read the code. Options: an SDI/HDMI capture card on a loop-through, the LED processor's monitoring output, or the scan camera (the "smart scan" machinery). It flags *stale* (counter not advancing), *torn* (two counter values in one captured frame), *wrong* (counter ≠ expected for the leader time) and *late* (frames behind).
  - **Fact model.** That moves PlayMap from "applied" to "observed" in its own doctrine: sent → accepted → applied → observed.
- **Label intentional states.** FREEZE and BLACKOUT must be distinct, visible states in STATE and on the desk, so the observer and the operator can tell a deliberate hold from a fault. The monitor-manual "FREEZE prompt" pattern applies directly.
- **Deterministic colour.** GSDF's approach of a single standard curve, measured luminance, ambient included and calibrated per display maps to calibrating every projector and LED processor to a declared reference (for example BT.709/2.4 or ST 2084 for HDR LED) with measured gains. That is already in PlayMap's calibration ledger direction. Prefer 10-bit output to avoid contouring in dark gradients, which GSDF's "too far apart → contours" reasoning predicts.
- **Latency budget for live elements.** For IMAG, performer tracking and camera-driven effects, about 100 ms glass-to-glass is the edge of human hand-eye tolerance by the surgical evidence. Lip-sync and show-specific budgets may be tighter. Publish a measured glass-to-glass figure per output using RidgeRun's two-clock photo method, and note that exclusive/independent flip matters (about 8 ms in their measurement).

### Gaps
- AAPM TG18/TG270 display QA specifics (constancy test intervals, luminance tolerances) were not fetched.
- No primary source was found on redundant dual video paths in surgical towers, such as automatic switch to a secondary display path. Only general safety-architecture sources were found.
- No source describes surgical systems using visible heartbeat overlays on the surgical feed itself. The evidence found is the vital-sign patent and monitor practice.

## Q14. Hard real-time lessons from adjacent fields (broadcast playout, flight simulators, VR compositors)

### Takeaway
- **Broadcast** runs mirrored main and backup chains with automatic changeover on detected black, freeze, silence or loss, with hysteresis to avoid flapping, and with a hardware bypass so the failsafe works even when the monitor's own computer is dead.
- **Flight simulation** regulates end-to-end *transport delay* (about 100–120 ms) and measures it with a step input through every module, using automated tests rerun at each evaluation.
- **VR compositors** never miss vblank. They drive the frame loop from predicted display time and redisplay or reproject the last good frame rather than show nothing.

### Cited Findings
- Broadcast changeover (vendor product docs): it detects "Signal loss, Video black frames, Video freeze (still image), Audio loss, Audio silence…" and uses "configurable hysteresis to prevent unnecessary switching". With a hardware pass-through SDI card, "the primary signal continues to flow uninterrupted even if the changeover server is powered off". — [SkyLark SL NEO Changeover](https://skylark.tv/products/software-new/transmission-telecom-operators/sl-neo-changeover/)
- Imagine Versio Redundancy (vendor) keeps up to five channels mirrored and synchronised. It fails over automatically on health criteria, checks and corrects schedule "running points" across the stack, can select a backup from a pool, and merges as-run logs across failover (A before, B after). — [Imagine Communications Versio Redundancy](https://imaginecommunications.com/make-tv/products/playout-and-channel-origination/aviator-orchestrator/versio-redundancy/)
- Vendor claims (marketing, unverified):
  - "sub-40ms" automatic failover, configurable debounce timers, and "isolated dual network architecture… preventing split-brain". — [Kashtrix HA controller](https://kashtrix.com/broadcast-playout/ha-controller)
  - hot-standby switching "< 1 frame". — [Point Media Tech Kaster Playout](https://point.tv/en/product/playout/)
- Flight simulation:
  - FAA's 2022 Part 60 final rule adopted ICAO 9625 Edition 4 transport-delay tolerances: 100 ms for instrument and motion response, 120 ms for visual response. — [FAA final rule, 14 CFR Part 60, 2022](https://www.faa.gov/sites/faa.gov/files/2022-11/Final%20Rule%20%2014%20CFR%20part%2060-Flight%20Simulation%20Training%20Device.pdf)
  - Older FAA helicopter simulator standards: 100 ms for Levels C/D, 150 ms for Level B. — [FAA AC appendix](https://www.faa.gov/documentLibrary/media/Advisory_Circular/Appx_1-2.pdf)
  - Helicopter FTDs: 150 ms or less. — [14 CFR Part 60 Appendix D](https://www.ecfr.gov/current/title-14/chapter-I/subchapter-D/part-60/appendix-Appendix%20D%20to%20Part%2060)
- FAA transport-delay test: "measure all the delay encountered by a step signal migrating from the pilot's control through all the simulation software modules in the correct order, using a handshaking protocol, finally through the normal output interfaces". The test mode "shall not alter the flow of information". — [FAA AC appendix](https://www.faa.gov/documentLibrary/media/Advisory_Circular/Appx_1-2.pdf); [Part 60 Appendix D](https://www.ecfr.gov/current/title-14/chapter-I/subchapter-D/part-60/appendix-Appendix%20D%20to%20Part%2060)
- Part 60 says a driver program that automatically runs the objective tests "is encouraged for all simulators and required for Level C and Level D". Results record date, time, conditions and tolerances against validation data. — [14 CFR Part 60 Appendix A](https://www.ecfr.gov/current/title-14/chapter-I/subchapter-D/part-60/appendix-Appendix%20A%20to%20Part%2060)
- VR compositors (see Q3 and Q10): `xrWaitFrame` paces the app with predicted display time ([OpenXR spec](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrWaitFrame.html)); Meta's compositor always outputs a frame, reprojected if stale ([Meta](https://developers.meta.com/vr/documentation/native/android/os-missed-frames/)); Valve treats reprojection as a last-resort safety net ([Valve 2016](https://media.steampowered.com/apps/valve/2016/Alex_Vlachos_Advanced_VR_Rendering_Performance_GDC2016.pdf)).
- Meta's Application SpaceWarp lets apps intentionally render at half rate with motion vectors and depth, while the compositor synthesises intermediate frames. Meta claims "up to 70% more GPU headroom" (vendor claim). — [Meta: missed frames / AppSW](https://developers.meta.com/vr/documentation/native/android/os-missed-frames/); [Meta AppSW](https://developers.meta.com/horizon/documentation/spatial-sdk/os-app-spacewarp/)

### Inferences
- PlayMap already has backup roles (failover/failback, a silent backup, fenced black; see [docs/SYNC.md](docs/SYNC.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)). Broadcast adds:
  1. Changeover triggered by *observed-signal* faults (black, freeze, loss), with hysteresis and debounce in STATE.
  2. A hardware bypass recommendation: LED processor dual-input failover, or an SDI changeover router with relay bypass, so the failsafe does not depend on a working PC.
  3. As-run logs merged across failover in the journal.
- Flight-sim transport-delay testing maps to a built-in "step test" verb. Inject a step (a full-field colour change or timecode jump) at the leader and measure, through the observer (camera or capture), when each output shows it. Run it automatically as part of preflight and recurrent show checks, like the Part 60 driver program. This is the "observed" fact for latency.
- A compositor that "always presents something" is the right model for PlayMap's present stage. The present step should never be skipped for want of content: if the frame for time T is late, present the hold or resubmitted frame for T at vblank and log a miss.

### Gaps
- SMPTE ST 2022-7 seamless protection switching and ST 2110 redundancy specifics were not fetched.
- No independent (non-vendor) measurements of playout failover times were found.

## Q15. Feasibility in C#/.NET: what is managed, what must be native

### Takeaway
.NET can host a frame-critical renderer if the render thread is allocation-free once warm and GC behaviour is configured deliberately: workstation concurrent GC, `SustainedLowLatency` during the show, explicit collection only at safe points, and optional no-GC regions with budget callbacks. NativeAOT removes JIT warm-up but not the GC. Device, driver, PSO compile, DirectStorage, DRED/Aftermath and crash handlers are native APIs reached through interop. The out-of-process crash handler and safety monitor should be separate native or minimal processes.

### Cited Findings
- `GCLatencyMode.SustainedLowLatency` suppresses *foreground* gen-2 collections (only gen 0/1 and background gen 2 run). It can stay on for long periods but "results in a larger managed heap… does not compact… higher fragmentation". `LowLatency` suppresses gen 2 entirely, only for short periods and only with workstation GC. Full blocking collections can still happen under memory pressure. — [Microsoft .NET GC latency modes](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency); [GCLatencyMode enum](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gclatencymode)
- `GC.TryStartNoGCRegion(totalSize, …)` commits the budget up front and fails if it cannot. It performs a full blocking collection before entering unless that is disallowed. Exceeding the budget silently ends the region. A callback API (`RegisterNoGCRegionCallback`) reports budget use. — [Microsoft TryStartNoGCRegion (net-10.0)](https://learn.microsoft.com/en-us/dotnet/api/system.gc.trystartnogcregion?view=net-10.0); [TinyFFR: avoiding GC stutter](https://tinyffr.dev/reference/avoiding_gc_stutter/); [dotnet/runtime issue 11733 (fixed)](https://github.com/dotnet/runtime/issues/11733)
- Microsoft GDK guidance on NativeAOT for games: "NativeAOT does not remove the garbage collector… It removes the JIT. Frame-time spikes caused by Gen0 collections are still yours to solve". The guidance:
  - prefer structs for per-frame data;
  - use `ArrayPool`/`MemoryPool`;
  - pre-size collections at load and never grow them mid-frame;
  - consider `SustainedLowLatency` during gameplay and force collection on load screens;
  - workstation GC is often better than server GC;
  - measure with PIX.

  — [Microsoft Learn: NativeAOT for gaming (GDK)](https://learn.microsoft.com/en-us/gaming/gdk/docs/gdk-dev/pc-dev/tutorials/get-started-with-custom-engine/native-aot-for-gaming?view=gdk-2604)
- A .NET real-time 3D library (TinyFFR) recommends zero or near-zero per-frame allocations, server GC off and concurrent GC on. It notes that `TryStartNoGCRegion` commits its whole budget immediately. — [TinyFFR: avoiding GC stutter](https://tinyffr.dev/reference/avoiding_gc_stutter/)
- Unity's model for managed engines: Burst-compiled jobs cannot access managed objects, and data passes as blittable copies or unmanaged containers that "are not garbage collected and so induce no garbage collection overhead". — [Unity job system overview](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-overview.html); [Unity EntitiesSamples jobs doc](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/EntitiesSamples/Docs/jobs.md)
- Out-of-process crash handling captures crashes, such as heap corruption and fast-fail, that in-process handlers miss. — [Sentry Native backend tradeoffs](https://docs.sentry.io/platforms/native/advanced-usage/backend-tradeoffs/)

### Inferences
- **Managed and fine:** render graph compile, the job scheduler (dedicated threads), frame pacing and prediction logic, present ledger, watermark encode and decode logic, the observer's analysis, hazard and preflight tables, the PSO key table, SBOM generation, and telemetry export (ETW via `EventSource`).
- **Managed but disciplined:** the render thread's per-frame loop. Allowed: structs, `stackalloc`/`Span`, native arenas, pre-sized collections, unmanaged function pointers for COM vtables. Forbidden: closures, LINQ, boxing, strings, logging allocations on the hot path. Gate it with an allocation-counter test (`GC.GetAllocatedBytesForCurrentThread` before and after N warm frames equals 0) as a fence in CI, in the spirit of the analyzer fences in `.editorconfig`/`BannedSymbols.txt`.
- **Native by nature, called through interop:** device and queue creation, PSO compile, DirectStorage, DRED/Aftermath setup, swap chains and present statistics. **Native separate processes:** the crash handler (Crashpad-style), and ideally the independent air observer and safety monitor. Keeping the monitor minimal and outside the engine process is the medical "separate execution environment" rule.
- **GC policy for a show node:**
  - workstation concurrent GC;
  - `SustainedLowLatency` while armed;
  - induce a collection at show-arm and after loads, only at safe points;
  - optionally a no-GC region during critical cues with a budget callback that journals any overrun.

  The house ban on `GC.Collect` in `src/` conflicts with the "collect at load" advice. Any exception needs a measured cause and a deterministic test, per "measured, then governed".

### Gaps
- No source measured end-to-end D3D12 call overhead from C# (function-pointer COM interop through Vortice, TerraFX or Silk.NET) against C++ for a render loop of about 1–5k calls per frame.
- No source quantified .NET 9/10 gen-0 pause times for an allocation-free main loop with background allocation on other threads (decode, network).

## Q16. Synthesis: what a live-show renderer should adopt (ranked by effect on the air)

### Takeaway
The highest-value transfers are the ones that make failures *observable and bounded*:
1. Prove the displayed frame downstream (watermark plus an independent observer), and alarm, hold or switch on stale.
2. Drive everything from a predicted display time with 1–2 frames in flight, and verify with present feedback.
3. Never compile, allocate or wait on the render thread (PSO precompile with preflight, compiled render plan, decode lanes with latest-wins).
4. Treat device loss and crashes as rehearsed events with captured evidence (DRED, out-of-process dumps, forced-TDR CI).
5. Run a hazard-driven engineering file: air FMEA/FTA/STPA, SOUP and driver locks, traceability to fault-injection tests, usability scenarios, SBOM and threat model.

### Cited Findings
- Each item above rests on the cited findings in Q1–Q15:
  - observation: [US 10,383,581](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/10383581), [AAMI BI&T 2017](https://array.aami.org/doi/full/10.2345/0899-8205-51.5.380);
  - predicted time: [OpenXR xrWaitFrame](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrWaitFrame.html), [VK_EXT_present_timing](https://www.khronos.org/blog/vk-ext-present-timing-the-journey-to-state-of-the-art-frame-pacing-in-vulkan), [DXGI flip model statistics](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model);
  - no compile on the render thread: [Epic PSO blog](https://www.unrealengine.com/en-US/tech-blog/game-engines-and-shader-stuttering-unreal-engines-solution-to-the-problem);
  - device loss: [Microsoft device removed](https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios), [DRED](https://learn.microsoft.com/en-us/windows/win32/direct3d12/use-dred);
  - process: [IEC 62304 guide](https://www.casrai.org/guides/iec-62304), [FDA software guidance](https://www.fda.gov/media/153781/download), [FDA cybersecurity guidance](https://www.fda.gov/regulatory-information/search-fda-guidance-documents/cybersecurity-medical-devices-quality-management-system-considerations-and-content-premarket).

### Inferences
Mapping table: lesson, then technique, then where it lands in PlayMap, then managed (M) or native (N).

| # | Lesson (source field) | Concrete technique | PlayMap landing | M/N |
|---|---|---|---|---|
| 1 | Frozen display is the deadliest failure (medical) | Per-frame counter/time code in pixels plus an independent observer (capture loop, LED monitor out, camera); stale, torn, wrong and late become facts | Present ledger extended from "shown" to "observed"; STATE `outputs[].observed`; journal | M logic, N capture |
| 2 | Independent safety subsystem (medical, broadcast) | Minimal monitor process with separate resources; heartbeat = shown-frame counter; forces safe state per show policy (hold, backup, black); hardware bypass where possible | Watchdog becomes air watchdog; backup role; LED/router failover | N or minimal M process |
| 3 | Predicted display time (VR, Vulkan, DXGI) | Time per frame from present feedback; same time through every stage; rebase on feedback | frame = f(leader time at vblank), made explicit | M (N API calls) |
| 4 | JIT submission, frames in flight 1–2 (Reflex, Anti-Lag 2, DXGI) | Waitable swap chain; sleep, then sample time and live inputs, then record; per-output frames-in-flight policy | Presenter per output | M |
| 5 | Never compile on the render thread (Epic, Microsoft ASD, Vulkan) | Enumerate all PSOs at show-arm; preflight "all compiled"; skip or hold if missing; cache keyed by driver version | Preflight, fixture tests | N compile, M orchestration |
| 6 | Compiled render graph (Frostbite, RDG, Unity) | Compile on config change; immutable plan; validation layer and lifetime-extension/clobber test modes | Existing render graph, ported | M |
| 7 | Frame-centric jobs and arenas (Naughty Dog, UE Pipes, enkiTS) | Ring of per-frame blocks with stage timestamps; fence-released native arenas; dedicated pinned workers; zero-alloc scheduling | Lanes = pipes; snapshot = FrameParams | M |
| 8 | Decode lanes, latest-wins, hold last valid (Bink, TiXL, DirectStorage) | Decode ahead to predicted time; fenced surface ring; render never waits; hash media before GPU decompression | Playback lanes, MEDIA FETCH | M plus N codecs |
| 9 | Device loss is normal (Microsoft, Vulkan) | Single rebuild path; forced-TDR CI; time-to-good-frame asserted | Device-lost rebuild (exists) | N API, M flow |
| 10 | GPU and CPU crash evidence (DRED, Aftermath, Crashpad) | DRED on by default; pass breadcrumbs; out-of-process minidumps with secret filtering | Journal "GPU incident"; desk or supervisor holds the handler | N |
| 11 | Transport-delay step test (flight sim) | Automated step injection and measured glass-to-glass per output, rerun at preflight | New verb or preflight check; STATE latency fact | M plus N capture |
| 12 | Hazard file, SOUP, traceability (62304, 14971, FDA) | Air FMEA/FTA/STPA; class-like rigour by worst-case air harm; SOUP list with driver and firmware locks; hazard → requirement → test IDs; fault-injection effectiveness tests | docs plus tests | process |
| 13 | Use-error engineering (62366) | Hazard-related operator scenarios; formative and summative evaluation; label intentional FREEZE/BLACKOUT | Desk, right-click menus, STATE | process |
| 14 | Cyber (FDA 524B) | Per-build SBOM; threat model; updateability view; patch cadence | Release pipeline | process |
| 15 | Deterministic colour (DICOM GSDF) | Declared reference transfer; measured per-display calibration including ambient; 10-bit output | Calibration ledger | M plus measurement |
| 16 | Bindless/GPU-driven (Ubisoft, id) | Per-surface records in GPU buffers; few indirect draws per output; fence-tied descriptor lifetimes | Render backend | N API, M records |
| 17 | Async compute only where measured (AMD) | Per-pass switch; handshake sync; serial fallback | Governor plus tests | M |
| 18 | Graceful degradation, not adaptive surprises (Valve, Meta) | Resubmit last frame at new time, then hold, then backup; fixed-fraction degradation for heavy generative layers only | Show policy | M |

### Gaps
- No source evaluated these medical practices in entertainment technology. The ranking is a judgement of effect on the audience-visible output, not measured evidence.
