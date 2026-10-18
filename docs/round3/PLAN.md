# PlayMap round 3: the plan

*How PlayMap will become an installed, Windows-native show node: Direct3D 12 behind a rendering hardware interface (RHI) cut at
today's `GlDevice`, presented through DXGI's flip model with the evidence for every claim, locked between machines only where
the hardware locks, supervised and installed per machine, and numbered so an installer can order every build. The round is a
spine in one session, then fifteen packages in four waves; each package is one implementation session followed by one
review-and-fix session. This file says who builds what, in which order, how each is accepted, and what waits for the next
round. Read against `main` at `23f162f` (16 Oct 2026), the codebase map (`research/codebase-map.md`, cited as **[map §n]**) and
the direction note (`research/windows-gpu-direction.md`, cited as **[dir §n]**, its sections counted in order) with the six
notes beside it. The designs that go beyond
this file are in `design/`.*

*The owner's direction, in their words: "PlayMap needs to be an installed Windows application to take full advantage of direct
deep Windows features, processor features, and graphics card features including RTX and Radeon. … Portability is not an issue —
ability, ultra-low latency, reliability and flexibility is. Add version numbers to builds. Remember to consider latest gaming and
medical code, UX design, and architecture for speed, user interface, and stability."*

---

## 1. The goal and the decisions

### 1.1 The goal

At the end of round 3 a Windows 11 machine will run PlayMap from a signed per-machine installer. Its engine will start in the
show user's session under a supervising service and render on Direct3D 12. Each output will present through its own
flip-model swap chain, and STATE will say whether that output is in Independent Flip and how that is known. The frame will
be drawn for the instant it is predicted to light the display, from a render graph compiled when the show changes, with every
pipeline compiled before the show is armed. User effects will compile out of process and draw on the same device. Decode will
stay on the GPU, previews will reach the desk without a CPU copy, and a lost device will be rebuilt by the same path that started
it, rehearsed in CI. An independent observer will read back what the audience saw. Outputs will lock across machines on RTX PRO
Sync and say "free, phase +x ms" everywhere else. OpenGL will stay the reference backend through the round and keep the Linux
development loop alive. It will retire in the next round, once the pixel and timing evidence says the two backends agree.

What exists today, and what the move changes, is the map's subject; its twelve findings [map §1] set the shape of this plan.
The three that shape it most: GL calls sit in ten files but GL types reach fifty of the renderer's eighty-nine [map §2.1];
there is no declarative render graph, only an ordered frame with five pass hooks [map §2.3]; and device loss stops the drawing,
with no rebuild path [map §3.1].

### 1.2 Decisions taken

| # | Decision | What it means in the tree | Why | What would reopen it |
|---|---|---|---|---|
| D1 | **Direct3D 12 is the primary renderer, behind an RHI.** | The RHI's contracts will live in Core (`PlayMap.Core/Gpu`); the GL backend will move under `src/PlayMap.Render/Gl/`; the Direct3D 12 backend will be a new edge, `src/PlayMap.Render.D3D12`, referencing Core and its native libraries only. Everything a backend implements or feeds will move to Core with the contracts: the platform and presenter seams, the program table, the vblank clock and ledger, the lock driver's shape. Passes, generators and the frame code will use the RHI and name neither API. | New GPU features reach Windows through D3D12 first; it sits on DXGI's flip model and NVIDIA's present barrier; it is the currency of D3D12 Video, Spout's D3D11 bridge and the desk's GPU import; only D3D12 has PIX, DRED, Aftermath, Radeon GPU Detective and GPU-less CI on WARP [dir §1]. | Nothing in the evidence; a lab finding that D3D12 is slower than GL on a vendor tier delays AUTO there (D2), not the decision. |
| D2 | **OpenGL will stay the reference backend until pixel and timing parity, then retire.** | GL 3.3 core / ES 3.00 will stay behind the RHI with unchanged pixels, both dialects' legs in CI. `PRESENT PATH D3D12` will be explicit this round; AUTO will stay GL until a lab parity record exists for a vendor tier (§1.5, M1), and then become D3D12 on that tier in one commit that cites the record. GL, ANGLE and the D3D11 path will be deleted next round, each with the tests that test only it. | Parity must be measured before a backend is retired ("measured, then governed"); no source compares D3D12, Vulkan and GL latency on show hardware [dir §1]. | A tier whose parity never arrives keeps GL there, with words. |
| D3 | **Vulkan is not carried.** | No Vulkan backend. The user-shader front end will go through SPIR-V (D7), so a Vulkan backend would stay cheap to add. | Vulkan's Windows presentation depends on each vendor; Spout, D3D12 Video and the desk's import need a DirectX bridge anyway; present timing is new and NVIDIA-first [dir §1]. | Linux render nodes as a goal again. |
| D4 | **Windows 11 24H2 (build 26100) or later; Windows 11 IoT Enterprise LTSC 2024 recommended for show nodes.** | The installer will refuse an older build with words. Show nodes: IoT Enterprise LTSC 2024 (build 26100, supported to 10 Oct 2034, Shell Launcher and specialized displays available). Desk and programming machines: Pro or Enterprise 24H2/25H2. Windows 10 will not be supported. Linux will keep the development loop and CI's GL suites, not shipped nodes. | 24H2 clears every feature floor the round uses (GPU upload heaps at 26080+, D3D12 AV1 encode); the Agility SDK keeps D3D12 current on a frozen LTSC; Windows 10 ended on 14 Oct 2025 [dir §5]. | A shipped feature needing a later build. |
| D5 | **Hardware lock only on NVIDIA RTX PRO with RTX PRO Sync; an honest software lock everywhere else.** | The frame-lock tier will join outputs through NVAPI's D3D12 present barrier and report its sync mode and in-sync counts as the lock's evidence. AMD, GeForce and any node without a board will present free and report measured phase ("same frame, phase +6.2 ms ± 0.3 ms"); STATE will never give an output a locked state (`framelocked`, `house-locked`, `grouped`) without the barrier's own statistics, and the unused `soft-locked` will be retired. | AMD withdrew framelock in 23.Q1; GeForce never had it; network time aligns which frame is drawn, not when it scans out [dir §2]. | A vendor shipping a lock with a D3D12 entry point. |
| D6 | **A per-machine MSI; the engine runs in an auto-logged-on show user's session under a supervising service.** | WiX v7 will build one per-machine MSI. A small LocalSystem service (`PlayMap.Supervisor`) will start `PlayMap.exe engine` in the show user's session, restart it, and stop one whose shown-frame heartbeat has stopped while HEALTH says it should move (never during a start or a device rebuild within its measured budget). The engine will never run as administrator and never in session 0; the installer will do every machine-level action once (firewall, dump settings, privileges). | Session 0 cannot drive displays; MSIX virtualises the machine-level writes the node needs; Velopack's MSI adds no services [dir §5]. | None expected. |
| D7 | **One HLSL source for the product shaders; user ISF out of process.** | The 40 product programs will be written once in HLSL and compiled by DXC at build time (the GLSL stays for the GL backend until it retires). User ISF will go ISF → GLSL 4.50 → glslang relaxed (`-R`) → SPIR-V → validated → SPIRV-Cross → HLSL → DXC → DXIL in a `PlayMap shader-worker` process, so no compiler loads into the engine, and will draw on the Direct3D 12 device through a root signature of its own (wp3-shaders-2); spirv_to_dxil will stay a measured alternative behind the same stage. A shader that crashed the GPU will be quarantined. | DXC's PDBs map a GPU crash to a source line in PIX and RGD; incorrectly written GLSL crashes explicit-API renderers [dir §1]. SPIRV-Cross and DXC ship as packages today; spirv_to_dxil has no package (to be built from Mesa). | spirv_to_dxil passing more of the ISF corpus than SPIRV-Cross (shaders' stretch measures it). |
| D8 | **TerraFX for Direct3D 12 and DXGI.** | `TerraFX.Interop.Windows` (raw structs and function-pointer vtables, generated from the Windows SDK and Agility headers) in `PlayMap.Render.D3D12` only; its port of the D3D12 Memory Allocator for memory, or PlayMap's own placed-resource pools if that port lags the Agility SDK. NVAPI will stay hand-declared, as today: the board's facts in Render's existing tier, the present barrier's calls in `Render.D3D12/Sync`; `PlayMap.Platform.Windows` will carry its own few read-only DXGI and NVAPI probe declarations, each with a layout golden. | The hot path allocates nothing; raw bindings keep that true and match the house's vtable approach (`ComSlots`) without hand-writing the D3D12 surface, which is far larger than the 31 methods `ComSlots` declares [map §2.7]. A wrapper binding allocates managed objects per interface. | A binding gap that blocks a must; Vortice.Windows is the named alternative. |
| D9 | **The desk keeps Avalonia,** with GPU shared-texture previews and the consoles' safety patterns. | Previews will move from four CPU copies to a shared texture and fence per view where the desk's compositor shares the engine's adapter; the CPU ring will stay for every other case. | Avalonia 12.1 is current and imports D3D11 NT-handle textures and D3D12 fence handles; WinUI 3 would rewrite every view for one asset [dir §9]. | Avalonia's backend not importing either at run time (§1.5, M2): the ring stays, and only the GPU path waits. |
| D10 | **What the round refuses.** | DLSS frame generation (synthesised frames under their own pacing contradict content as a pure function of show time); adaptive resolution (it breaks blend seams); VRR on any output in a lock group or feeding genlocked processors; shipping changed TDR registry keys (long GPU work is split instead); `GC.Collect` at show arm (banned in `src/`; a case for it needs a measured cause and a test). | [dir §2, §4, §5, §7, §8] | A measured cause and a deterministic test. |

### 1.3 Versions: one source, every format its reader needs

`main` holds the version in one place (`Version.props`: `PlayMapVersion` 0.2.x and `PlayMapRunBase`; CI passes its run
number as `PlayMapRun` and its commit as `PlayMapCommit`, and the build number is the run less the base), stamps every assembly
of PlayMap's own source and the exe, has `BuildInfo` read it back, and names the build on the desk, in HEALTH's `build`, in the
engine's log and the journal's start row and in MCP's server info. STATE's `version` stays the plain product version, so STATE
does not move between builds. The CI artifact is `PlayMap-<FileVersion>-win-x64`. Round 3 will keep that scheme and add the
installer's rules (wp3-installer, item 1).

| Reader | Format | Example (0.2.0, CI build 17, commit `abc1234`) | Rule |
|---|---|---|---|
| People, HEALTH `build` (the context's machine item, the fleet's board), MCP's server info, the log and the journal's start row, every crash manifest | SemVer 2 with build metadata | `0.2.0+17.abc1234` (local: `0.2.0-dev+abc1234`) | The commit maps a dump to its source; build metadata never orders versions |
| Windows file version (Explorer, WER) | four parts, each ≤ 65 535 | `0.2.0.17` | `Version.props` refuses a build number past 65 535 and every part past 65 535 |
| **MSI `ProductVersion`** | `MAJOR.MINOR.BUILD`, major and minor ≤ 255, build ≤ 65 535 | `0.2.17` | Windows Installer reads three fields and ignores a fourth: under the four-part file version every CI build of 0.2.0 would read as 0.2.0 [map §6.3]. The MSI takes the build number as its third field, so every numbered build upgrades the one before it |
| Assembly version | binding only | `0.2.0.0` | As `main` has it |

The rules the installer will add:

1. **Major and minor at most 255** for every build, not only for an MSI: `Version.props` will refuse more, so no version can be
   set that a release cannot install.
2. **An MSI is made only from a numbered build.** A local build refuses to make one, with words; its MSI would read as
   `0.2.0` and could never be upgraded in order.
3. **The build number orders installs within a MAJOR.MINOR.** It is CI's run number less `PlayMapRunBase`, and the run
   number only grows within one workflow file. Anything that starts the numbering again (a raised `PlayMapRunBase`, a new
   workflow file) raises MAJOR or MINOR in the same commit, never PATCH alone, as `Version.props` already says; the release job
   compares the new MSI version with the last release's and refuses one that is not greater, naming both.
4. **PATCH lives in the SemVer and the product's name.** Add/Remove Programs shows `ProductVersion` (`0.2.17`); the product's
   name carries the SemVer ("PlayMap 0.2.0 (build 17)"), as the desk's about line does.
5. **No downgrade by installer.** `MajorUpgrade` refuses a lower version with words; going back is an uninstall and an install,
   by a person.
6. **The direction note's git height is not taken.** The branch already numbers by run; a height repeats across branches at the
   same depth and needs a full-history checkout, while a run number is unique per run. The commit in the SemVer gives the same
   traceability the height would have.

MSIX and Velopack are not used, so their version rules do not apply.

### 1.4 What CI proves, and where

The Linux job will stay the development loop and the gate for everything that is not Direct3D. The Windows job (pinned to
`windows-2025`: Windows Server 2025, build 26100, so the MSI's launch condition holds on it; no GPU) will gain Direct3D 12 on WARP
from NuGet (feature level 12_2, Shader Model 6.9 [dir §1]) with the Agility SDK's debug layer, and will become the hard gate for
every Direct3D 12 line from wp3-d3d12-core's merge on (§5.4). WARP's NuGet binaries are CI's only; they will never be in the MSI.
The runner is a Server SKU on the Basic Render Driver in a virtual display, so what it gives — MMCSS, the throttling read-back,
the GPU scheduling class, an HDR10 colour space, the session calls — is measured on the spine's day (M14), never assumed; where it
refuses, the test asserts the refusal's named words and the lab proves the call. The hosted runners are shared, so no CI test
asserts a time (§2.1).

| Evidence | Linux job (llvmpipe, Xvfb) | Windows job (WARP) | Lab |
|---|---|---|---|
| Core, Net, Devices, Vision, Assistant, App (headless), Media's CPU and fake-runtime tests, the Companion module | ✓ | ✓ | — |
| The GL backend: 213 pixel tests, the GL render host, ES dialect tests | ✓ (core 3.30 and the ES 3.00 leg) | ✓ (Mesa WGL) | L20 (Windows drivers) |
| Decode tests on the LGPL FFmpeg the MSI carries (the GPL command line only encodes test media) | ✓ | ✓ | — |
| The RHI's own tests: fences, layouts, draw states, the module fence | ✓ | ✓ | — |
| The render graph compiler, the frame block ring, evidence maths, observer verdicts, platform policy, supervisor policy, the version maths | ✓ (pure) | ✓ | — |
| Direct3D 12: device, debug layer and GPU-based validation with no message, every pixel scene within its row of the D3D12 tolerance table, aliasing on and off, zero warm-frame allocation | — | ✓ | real GPUs (L15) |
| HLSL through DXC, reflection against the program layouts | to be measured (M6) | ✓ | — |
| User ISF to SPIR-V (glslang, spirv-val, SPIRV-Cross) | ✓ | ✓ (on to DXIL, drawn on WARP, the pinned public corpus counted) | — |
| D3D12 flip presenters: bring-up, 120 presents, read-back, HDR swap chains | — | ✓ (statistics may be absent on WARP) | real vblank |
| Independent Flip proven, PresentMon agreeing | — | — | ✓ |
| Present barrier, RTX PRO Sync | fake NVAPI | fake NVAPI, layout goldens | ✓ |
| D3D12VA decode handoff, the copy-queue arena, Spout through a D3D11 side device | — | ✓ (fake decoder; two WARP devices, cross-API if M8 allows) | NVIDIA, AMD, Intel; Resolume, TouchDesigner |
| Forced device removal; the time to the first good frame recorded | — | ✓ (if WARP honours a forced removal, M8; else a fault seam) | a real TDR |
| Shared previews: handles duplicated into a second process and read there | — | ✓ (in a child process if M8 allows; else two devices in one process) | Avalonia's own compositor |
| `PlayMap.Platform.Windows` | policy | every call answers; read back where the Server SKU answers (M14) | p99.9 frame time with and without each claim |
| MSI build, install, upgrade, downgrade refused, uninstall, service, firewall, dump settings | — | ✓ (the runner is elevated) | IoT Enterprise LTSC 2024, auto-logon |
| Signing | — | ✓ on `main` and the signing test branch once an identity exists (§9.2); a named notice before | SmartScreen (L18) |

The development loop runs on Linux, so every Direct3D 12 slice will keep its logic pure where it can be and its Direct3D calls
thin, and will read its pushed branch's Windows job (`gh run view`) before it says it is done. A failure is read from Linux
through the job's artifacts: the spine's `workflow_dispatch` input runs one filtered Direct3D 12 leg and uploads its diff images,
the info-queue log and DRED's output, with the leg's minutes.

### 1.5 Measure first

The research could not settle these. Each has a cheap measurement, a deadline (the package whose slice it changes), and a plan for
both answers, so no slice waits on a hope.

| # | Question | Measured how | Deadline | If yes | If no (and until it is measured) |
|---|---|---|---|---|---|
| M1 | **Is D3D12 as fast as GL, through the present ledger, on show hardware?** | `bench/r3-present` (the spine's) in W1 (a flashing quad through D3D12 flip, the existing GL window and the D3D11 path on one display, photodiode at a fixed screen position plus PresentMon); then the engine itself once wp3-present merges: the same show on `PRESENT PATH GL` and `D3D12`, an hour each, ledger and photodiode, the ISF corpus count on the new road beside it | AUTO's switch, after W3 | AUTO becomes D3D12 on that vendor tier (NVIDIA RTX, RTX PRO, Radeon, Arc) in one commit citing the record | AUTO stays GL on that tier, with words in STATE naming the record; D3D12 stays explicit |
| M2 | **Does Avalonia's Windows backend import a D3D12 shared texture and a D3D12 fence at run time?** | desk-ux's bench app (`bench/r3-avalonia-import`) in W1 on a real GPU: `ICompositionGpuInterop.SupportedImageHandleTypes` and `SupportedSemaphoreTypes`, an import, a frame shown | wp3-desk-gpu's slice (W3) | The fence path as designed | The ring stays for that compositor (a shared texture the desk cannot wait on is not used); the age badge ships either way |
| M3 | **Does the Windows 11 composition swapchain take D3D12 buffers and present at a target time?** | `bench/r3-present` in W1 | wp3-present's stretch 7 | A timed-present tier for outputs whose cadence is not the display's | Not built; flip presenters with the predicted display time are the path |
| M4 | **Does the present barrier join several swap chains in one process on a Sync wall?** | The lab's two-machine RTX PRO Sync wall, two outputs each: nvpro-samples' barrier sample (no PlayMap code), then the engine | W4's entry condition: the sample's run booked before W3 ends (§9 decision 13) | A client per output's swap chain, as designed | One swap chain per machine joins the barrier (the pacing output's); the machine's other outputs report free with their phase against it, and `SYNC APPLY` says so; Mosaic stays a stretch |
| M5 | **Does Reflex work on RTX PRO drivers, and with the present barrier?** | The lab's RTX PRO node: `NvAPI_D3D_SetSleepMode` answers and its latency markers | after W3 | An optional low-latency tier on outputs that are not in a barrier | PlayMap's own just-in-time pacing (waitable, then sample, then record) is the only one; never both on one output until measured |
| M6 | Does DXC on Linux emit validated, signed DXIL? | The shaders package's first day | wp3-shaders | HLSL compiles on both jobs | HLSL compiles on the Windows job only; the Linux build embeds none and a Linux test holds the HLSL program set to the library's names |
| M7 | Does a .NET apphost load the Agility SDK's runtime (no `D3D12SDKVersion` export) through `ID3D12SDKConfiguration1::CreateDeviceFactory`? | The spine's probe step, on the runner (its WARP test and the hard gate's debug layer rest on the answer) | the spine | The MSI places `D3D12Core.dll` beside the exe and STATE names the runtime loaded | The OS runtime, named in STATE, its debug layer from Windows' Graphics Tools optional feature installed in the job; features that need a newer runtime are probed and refused with words |
| M8 | Does WARP honour `ID3D12Device5::RemoveDevice`, does it decode video, do the NuGet WARP (Direct3D 12) and the system WARP (Direct3D 11) share NT-handle textures and fences, and can a WARP allocation be duplicated into a child process? | wp3-d3d12-core's first day | wp3-stability-2, wp3-video, wp3-desk-gpu | Removal, D3D12VA open, Spout's cross-API bridge and the child-process preview run in CI | Removal through a fault seam in CI and a real one in the lab; D3D12VA's handoff tested with a fake decoder; CI shares Direct3D 12 to Direct3D 12 in one process, and the cross-API and cross-process cases move to L8 and L3 |
| M9 | Can the supervisor start the engine into the logged-on session (`WTSQueryUserToken`, `CreateProcessAsUser`)? | wp3-installer, on a lab machine with auto-logon, and as a probe on the runner (its job runs in an interactive session) | wp3-installer | As designed | A scheduled task at the show user's logon starts the engine; the service only watches and stops |
| M10 | Does `NvAPI_D3D1x_QueryFrameCount` give every node the same count with the barrier joined? | With M4 | wp3-framelock | `LockCounter` reads it | Content time from the leader's anchor at the barrier's in-sync refresh count |
| M11 | Do Spout's legacy share handles open on the D3D11 side device beside a D3D12 renderer? | wp3-video, with Resolume and TouchDesigner | wp3-video | As designed | Spout through the side device's CPU staging, said in STATE |
| M12 | Does FFmpeg 9.0.2's D3D12VA hand frames over on NVIDIA, AMD and Intel? | wp3-video, in the lab | wp3-video | Zero-copy D3D12VA | D3D11VA on a side device, copied once on the GPU, or copy-back, with the ladder's words |
| M13 | ReadyToRun and a pre-roll, or NativeAOT, for the engine's first ten minutes? | p99.9 frame time over the first ten minutes of a show on one lab machine (L21) | ReadyToRun's record with wp3-installer; NativeAOT next round | — | ReadyToRun in the MSI's publish (wp3-installer item 2) and a pre-roll (wp3-d3d12-passes-2 item 1) are the default |
| M14 | What does the hosted Windows runner give: MMCSS, the power-throttling read-back and the GPU scheduling class on the Basic Render Driver; an HDR10 colour space on its virtual display; M9's session calls? | The spine's probe step, its answers in the job's summary | the spine | Each asserted on the job by its package | Each a Server-SKU or virtual-display limit: the package's Windows test asserts the refusal's named words there, and the lab line proves the call (L10, L11, L13) |
| M15 | How many minutes does GPU-based validation add to the Direct3D 12 leg on WARP? | wp3-d3d12-core's first full run, the leg's minutes in the summary | wp3-d3d12-core | On in every run | On for the merge's changed scenes in every run and for all of them in a nightly run on `main`; the gate (§5.4) names which |

M1–M5 are the four results the direction note says decide the schedule, plus Reflex; M6–M13 are what the map found the tree
needs answered; M14–M15 are what the Windows job's own evidence rests on.

### 1.6 Done means, for the round

Round 3 is done when:

1. Every package's must is merged to `main` with both CI jobs green and the analyzers on, under §2.1's per-package rule.
2. On the Windows job, on WARP: the Direct3D 12 backend draws every scene of the pixel suites, user effects included, through the
   compiled render graph within each scene's row of the D3D12 tolerance table (every widening recorded with its measured
   difference and reason; packed video byte-exact), with the debug layer and GPU-based validation on (as M15 decided) and no
   message; aliasing on and off draw the same pixels; warm frames allocate nothing; every pipeline the armed show can reach is
   compiled before ARM answers, or ARM's answer names each one missing with preflight red; a forced device removal is rebuilt by
   the start-up path to a good frame, its time recorded; the MSI builds, installs, upgrades from the previous build, refuses a
   downgrade and uninstalls; `main`'s artifact is named with its version and signed, or named unsigned while §9 decision 2 is open.
3. On Windows with `PRESENT PATH D3D12`, a show runs end to end on the Direct3D 12 backend: outputs on flip presenters, decode by
   D3D12VA or the copy-queue arena, user ISF effects compiled by the worker, Spout through the side device, previews shared with
   the desk where its compositor allows, the observer reading an output back. AUTO is still GL until M1's record, and STATE says
   why.
4. On Linux the GL backend draws every golden byte for byte as it did at `23f162f`, and every suite that needs no Direct3D is
   green.
5. No claim without evidence: every output says its present mode and how it is known; no output without the barrier's own
   statistics is in a locked state; every platform claim says what it asked and what Windows answered; every shared preview says
   which path it took.
6. The lab checklist (§7) is written with a script and a pass mark for every manual line, M1–M5 are measured or pending with a
   date (M4 before W4), and the papers (the integration pass, §5.6) describe what shipped in the present tense.

---

## 2. Ownership rules

### 2.1 The capacity rule

| Rule | What it means |
|---|---|
| **One session, one slice** | A package is built by one implementation session, then one review-and-fix session (fixes only, no new scope). |
| **The budget** | Above its cut line (⟂) a slice is about 20–26 hours of work, after what the spine already lands. Below the line, stretch items total at most about 8 hours. |
| **Order is the contract** | Slice items are built in the listed order. The must above ⟂ merges on its own. A stretch item starts only when the must is green; an unfinished stretch item is not merged half-built: it moves to "Next round" and its catalogue rows are trimmed. |
| **Done means (a package)** | Merged to `main` with both CI jobs green and the analyzers on; its acceptance tests pass (here, on the Windows job, or marked manual with the lab line that will check it); its fixture `tests/fixtures/state/<pkg>.json` is written by one of its own tests, with a card descriptor `<pkg>.cards.json` for each new STATE block (the desk renders it, and its actions become the object's right-click entries); its catalogue block holds only rows it routes; it registers at least one `IPreflightCheck` with a fix line, tested by injecting a fault, where it owns a show resource; it contributes an INDEX section where it adds an entity a controller can act on; `docs/<AREA>.md` and `docs/wire/<pkg>.md` describe what shipped, with a papers note for the integration pass; the scripts of its lab lines (§7) are in `docs/round3/lab/`, inside its budget. |
| **The doctrine** | Attempts are not facts (sent → accepted → applied → observed; a verb that would change nothing is refused with the reason; tests assert fields and published STATE, never words alone). Content is a pure function of show time. One writer. The desk has no private API. Fail closed on the wire. Measured, then governed. |
| **Timing on CI** | The hosted runners are shared, so a CI test asserts counts, order and fields — on a manual clock, or by counting frames — and records any time it measures without a ceiling. Latency, ratio and frame-time pass marks belong to the lab's lines (§7). A CI test with a time ceiling is a defect of the test. |
| **Windows code** | Every Windows-only type carries `[SupportedOSPlatform("windows")]` (CA1416 is an error), so the solution builds and its pure tests run on Linux. Native calls are thin; the logic around them is pure and tested on both jobs. A Direct3D 12 test fails on any debug-layer message of severity warning or above, unless the test names the message and why WARP gives it. |
| **Native files and licences** | Every native binary the node loads (the Agility runtime, DXC, glslang, SPIRV-Tools, SPIRV-Cross, FFmpeg, libvmx) is pinned by hash, with a notice in `THIRD-PARTY-NOTICES.md` under the package's heading. WARP's NuGet binaries are CI's only. AMD runtimes are never bundled (their EULA); NVIDIA's Aftermath library is loaded when it sits beside the exe and shipped only if its terms allow; NVAPI is the driver's. No GPL code is copied or opened while writing the matching code. |
| **No test is skipped to get green** | A GL-only test stays while GL stays and leaves with the code it tests, in the change that removes that code, which says so. |

### 2.2 What every package owns, by convention

Each package owns, beyond the files named in its section:

- its **stub files** created by the spine for its hook word: `src/PlayMap.Engine/EngineCore.<Word>.cs`,
  `EngineServices.<Word>.cs`, `Host/EngineParts.<Word>.cs`, `Rendering/RenderHost.<Word>.cs`,
  `src/PlayMap.Core/Bus/NodeSettings.<Word>.cs` (where its word has them);
- its **catalogue block** in `VerbCatalog.cs` (between its comment line and the next blank line): it trims syntax to what it
  implements, removes rows it does not implement, may add rows inside its own block for a stretch item it reaches, and edits no
  row outside its block;
- new folders `src/PlayMap.<Project>/<Area>/**` and `tests/PlayMap.<Project>.Tests/<Area>/**` for its area;
- `tests/fixtures/state/<pkg>.json` and `<pkg>.cards.json`, `docs/<AREA>.md`, `docs/wire/<pkg>.md`, and its heading of
  `THIRD-PARTY-NOTICES.md`;
- at most one `<ItemGroup Label="wp3-<pkg>">` at the end of any `.csproj` it needs a reference in (one hunk per project);
- its own named block of `.github/workflows/build.yml` (from its `# wp3-<pkg>:` comment line to the next block), of whatever length
  its steps need, landing in §5.3's merge order: CI steps are blocks, not hunks, so the ten-line rule below does not bind them, and a
  package edits no other block. One workflow file keeps the run numbers the MSI version orders by (§1.3, rule 3).

**Continuations.** A continuation (wp3-d3d12-passes-2, wp3-stability-2, wp3-shaders-2) owns, for its wave, every file its parent
owned (the parent is merged and finished) except a file §4 gives another package in that wave, plus the files named in its section;
it keeps the parent's hook word, catalogue block and fixture. Its review session checks the parent's tests still pass unchanged.

**Ownership by wave.** A file has one owner in each wave. When a package is merged, the files it owned pass to the package §4
names for the next wave. No two packages of one wave own the same file; a package touching another's file does so only through a
hunk §4 names, landing in §5.3's merge order, at most about ten lines.

**The spine's files** after W0 (changed only through the spine amendment lane, §5.7): `Core/Model/*` (the round-3 fields and
`ProjectStore.UndoTo` land in W0), `Core/Show/ShowPolicy.cs` (`UNDO TO` as `UNDO`), `Core/Fleet/HealthRecord.cs` (the heartbeat's
reserved fields), `Core/Wire/{VerbCatalog (outside blocks), VerbSyntax, ShowClass}.cs`, `Core/BuildInfo.cs` (with the installer's hunk),
`Engine/{EngineCore, EngineCore.Router, EngineCore.Modules, StateWriter, ContextBuilder, NodeHome, Seams, Seams.Round2,
Seams.Round3}.cs`, `Engine/Host/{EngineParts, EngineHost, EngineEntry}.cs`, `Version.props` (with the installer's hunk),
`Directory.Build.props`, `BannedSymbols.txt`, `.editorconfig`, `PlayMap.sln`, `.github/workflows/build.yml` (outside the packages'
named blocks).

### 2.3 Hook words

| Word | Package | Stubs |
|---|---|---|
| `Machine` | wp3-platform | `EngineCore.Machine.cs`, `EngineServices.Machine.cs`, `NodeSettings.Machine.cs` |
| `Gpu` | wp3-d3d12-core (wp3-d3d12-passes-2 adds `EngineCore.Pipelines.cs`, wp3-stability-2 `EngineCore.Rebuild.cs`) | `EngineCore.Gpu.cs`, `RenderHost.Gpu.cs`, `NodeSettings.Gpu.cs` |
| `Plan` | wp3-d3d12-passes (wp3-d3d12-passes-2 continues it, then wp3-stability-2 in W4) | `Rendering/RenderHost.Plan.cs` |
| `Observe` | wp3-stability | `EngineCore.Observe.cs`, `EngineServices.Observe.cs`, `Host/EngineParts.Observe.cs`, `Host/EngineEntry.Observer.cs`, `NodeSettings.Observe.cs` |
| `Shaders` | wp3-shaders (wp3-shaders-2 continues it) | `Host/EngineEntry.ShaderWorker.cs`, `EngineServices.Shaders.cs` |
| `Undo` | wp3-desk-ux | `EngineCore.Undo.cs` |
| `Preview` | wp3-desk-gpu | `RenderHost.Preview.cs` (beside the existing `RenderHost.Previews.cs`), `EngineCore.Preview.cs` |

Present and framelock keep the existing Output and Sync files (`EngineCore.Output.cs`, `EngineCore.Sync.cs`,
`Presentation/*`), owned by them in their waves.

---

## 3. The packages

### 3.0 Overview

"Where" says where the session will run: **here** (this machine, under §5.2's limits) or **cloud** (its own container; its
Windows evidence comes from the pushed branch's Windows job either way). Hours are the slice's must above ⟂.

| # | Branch | Hook word | Wave | Depends on | Where | h | Round-3 headline |
|---|---|---|---|---|---|---|---|
| 0 | `wp3-spine` | — | W0 | `main` with `versioning` merged | here | 23 | Round-3 rows, Core contracts and the presenter types in Core, model fields, the seams already known (`UNDO TO` in the gate, the heartbeat's fields, the arm hook), the new projects, the WARP and ES legs in CI, the M1/M3 bench, stubs |
| 1 | `wp3-rhi` | — | W1 | spine | here | 26 | The RHI in Core with the presenter seams, the program table, user programs and the graph's transients; GL behind it with unchanged pixels; one test fixture for every backend |
| 2 | `wp3-platform` | Machine | W1 | spine | cloud | 26 | `PlayMap.Platform.Windows`: QoS, timers, MMCSS, process and GPU priority, CPU sets, keep-awake; the node's identity, support tier and capabilities; machine preflight |
| 3 | `wp3-shaders` | Shaders | W1 | spine (rhi before merge) | cloud | 25 | The 40 programs in HLSL through DXC; the shader worker for user ISF; quarantine |
| 4 | `wp3-installer` | — | W1 | spine | cloud | 26 | The MSI version rules, the LGPL FFmpeg in CI, the WiX MSI with ReadyToRun, machine-level actions, the supervisor service, a fail-closed armed check, signing |
| 5 | `wp3-desk-ux` | Undo | W1 | spine | cloud | 23 | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status rules, preview age, the M2 bench |
| 6 | `wp3-d3d12-core` | Gpu | W2 | rhi, shaders | cloud | 26 | Device, queues, fences, memory, descriptors, transients and aliasing, the exporter, DRED and Aftermath, the pipeline cache; every product pixel scene on WARP |
| 7 | `wp3-d3d12-passes` | Plan | W2 | rhi (d3d12-core before merge) | here | 24 | Every pass declares what it reads and writes; the render graph compiled on show change; both backends walk the plan |
| 8 | `wp3-stability` | Observe | W2 | rhi | here | 22 | The watermark, the independent observer, the shown-frame heartbeat, `LATENCY STEP`, the crash policy |
| 9 | `wp3-present` | (Output) | W3 | d3d12-core, stability | cloud | 25 | D3D12 flip presenters, Independent Flip evidence, one queued frame, the predicted display time, HDR10 and 10-bit, the Engine suites on D3D12 |
| 10 | `wp3-video` | (Decode) | W3 | d3d12-core | cloud | 23 | D3D12VA zero-copy, the copy-queue arena, Spout on a D3D11 side device, NDI and OMT on the copy queue, decode tiers |
| 11 | `wp3-desk-gpu` | Preview | W3 | d3d12-core, desk-ux | here | 20 | Shared-texture previews with a fence, the adapter guard, the ring kept, the desk-kill test |
| 12 | `wp3-d3d12-passes-2` | (Plan, Gpu) | W3 | d3d12-passes, d3d12-core | here | 21 | Pipelines compiled at arm and keyed by driver, bindless records, zero allocation on D3D12, GPU time per pass |
| 13 | `wp3-framelock` | (Sync) | W4 | present; M4 measured | cloud | 23 | The present barrier on RTX PRO Sync, the honest software lock, the governed fall-out, timing preconditions with ADLX's FreeSync read |
| 14 | `wp3-stability-2` | (Gpu, Observe) | W4 | present, d3d12-passes-2 | cloud | 21 | One rebuild path, rehearsed device removal, repeated loss governed, the degrade order, GPU crash evidence |
| 15 | `wp3-shaders-2` | (Shaders) | W3 | shaders, d3d12-core (d3d12-passes-2 before merge) | cloud | 22 | User ISF effects on the Direct3D 12 device, the GPU-time guard, the ISF corpus on the new road |

Sixteen sessions, about 375 hours above the cut lines. The designs that go beyond this file: `design/rhi.md` (wp3-rhi),
`design/d3d12.md` (wp3-d3d12-core), `design/graph.md` (wp3-d3d12-passes and -2), `design/shaders.md` (wp3-shaders and -2),
`design/present.md` (wp3-present and wp3-framelock), `design/platform.md`, `design/desk-gpu.md`, `design/stability.md`
(wp3-stability and -2) and `design/installer.md`. Video and desk-ux are specified here in full.

Each section: the slice in order (⟂ is the cut line; items after it are stretch), what it adds to preflight and INDEX, the files
it owns, the hunks it may add to others' files, its dependencies, its acceptance tests, what is verified here, on CI and in the
lab, and its next round.

### 3.1 `wp3-spine` — the contracts every package builds on (W0)

**Slice.**
1. The base: `main` with the `versioning` branch merged (§5.1); the spine rebases on it and checks `BuildInfo.Informational`
   reaches the hello, STATE's `build` and the journal header (adding it where the branch did not). (1 h)
2. The catalogue: a `V3` helper (`Round = 3`, answered "not available on this node yet" until its owner routes it), every row of
   the table below with its syntax, words, risk, show class and owner's block comment; `VIEW` gains `[SHARED <pid>]` and
   `PRESENT PATH` gains `D3D12` (refused with words until their owners route them); `RoutesTests` and `CatalogueTests` cover the
   rows. (3 h)
3. Contracts two or more packages share: `GpuApi.D3D12`; `PresentPath.D3D12Flip` and its word (`d3d12-flip` in STATE; on the
   wire a D3D12 flip presenter reports PMVB path 2, `dxgi-flip`, the presentation model it shares with the D3D11 path, so mixed
   builds in a group read each other); the PMVB reader learns path 3 (`dedicated`, wp3-present's stretch) and still refuses 4
   and above; `NodeIdentity`, `AdapterIdentity`, `CapabilitySnapshot`, `CapabilityProbe`, `ThreadRole`, `IThreadClaim` and the
   static `ThreadClaims` (Core/Machine: a thread in Media, Net or the engine claims its role in one line, a no-op until the engine
   points it at its platform); the engine seams of `Seams.Round3.cs` (`IMachinePlatform`, `IAirObserver`, `IGpuLifecycle`,
   `IPreviewShare`, `IShowArmHook`) with null implementations that say why. The presenter-facing types move from Render to
   `src/PlayMap.Core/Presentation` with their names and behaviour (`VblankClock`, `PresentLedger`, `PresentClock` with
   `PresentSnapshot`, `PresentMode` and `ModeEvidence`, `LatencyPlan`, `PresentPath`, `PresentProblem`, `PresentResult`), so a
   Direct3D 12 presenter in `Render.D3D12` feeds them without referencing Render (wp3-rhi moves `IGpuPlatform` and
   `IOutputPresenter` when it reshapes them). (5 h)
4. The model's round-3 fields, backward compatible (init properties with defaults): `Output.ColorBits` (8 | 10), `Output.Hdr`
   (off | hdr10 | scrgb) and `Output.NitTarget`, `Output.FramesInFlight` (0 = auto, 1, 2), `Output.Watermark` (null, or place
   and size); `ModelCompatTests` read every round-2 fixture project unchanged. (2 h)
5. The tree: `src/PlayMap.Render.D3D12` (references Core; TerraFX; every type `[SupportedOSPlatform("windows")]`; its folders as
   `design/d3d12.md` §0 lists them), `src/PlayMap.Platform.Windows` (references Core), `src/PlayMap.Supervisor` (references Core),
   their test projects, the solution's entries (the WiX project stays outside `PlayMap.sln`: an MSI builds on Windows only, so the
   Windows job's MSI step builds it); `ModuleRuleTests` (each new project references Core and its own native packages only;
   Render.D3D12 does not reference Render; only App references Avalonia); the stubs of §2.3, each empty body holding
   `// wp3-<pkg> fills this.`; the new STATE keys reserved in their place — `identity`, `capabilities`, `platform`, `locked` and
   `install` as children of the existing `machine` block, after its present keys; `render`, `observe` and `previews` at the top
   level; empty fixtures `machine.json`, `install.json`, `shaders.json`, `gpu.json`, `graph.json`, `observe.json` and
   `previews.json` with their card descriptors; `docs/wire/<pkg>.md` generated from the catalogue; one heading per package in
   `THIRD-PARTY-NOTICES.md`. (4 h)
6. CI: the Windows job pinned to `windows-2025` (Windows Server 2025, build 26100: the MSI's launch condition holds there);
   it restores `Microsoft.Direct3D.WARP` and `Microsoft.Direct3D.D3D12` (the Agility SDK, with its debug layer) and copies them
   beside the test hosts; a trait `Backend=D3D12` on Direct3D 12 tests so the job lists their results apart; a `workflow_dispatch`
   input that runs one filtered Direct3D 12 leg and uploads its diff images, the info-queue log and DRED's output as artifacts,
   with the leg's minutes in the summary, so a Direct3D 12 failure is read from Linux; a probe step that answers M7 and M14 in the
   job's summary; the job's summary names the version built. The Linux job gains the ES 3.00 leg of the Render suite
   (`PLAYMAP_GL_DIALECT=es300`, which passes today by hand), after the two tests that name a desktop core context
   (`GlCapsTests`, `ShaderConventionTests`, docs/OUTPUT.md) read the fixture's dialect, so the leg runs whole and D2's "unchanged
   pixels" covers both dialects. (3 h)
7. The seams already known, so no package takes them through the amendment lane: `ProjectStore.UndoTo(n)` and its steps' list
   (newest first, with who and when) and a hunk in `src/PlayMap.Core/Show/ShowPolicy.cs` that treats `UNDO TO` exactly as `UNDO`
   (drafted for the blind owner, allowed for an operator while armed); `HealthOutput.ShownFrames` and `ObservedFrames` (−1 meaning
   unknown) and HEALTH's `gpu` record (the device epoch, the phase — `starting`, `running`, `rebuilding` — and since when), read
   from a round-2 HEALTH line as unknown; the engine's pid record (pid, line-wire port, start time), written at start into
   `%ProgramData%\PlayMap\supervisor\engine.pid` where that folder exists, which the supervisor and the MSI's armed check read;
   `IShowArmHook` (asynchronous, cancellable, with a deadline: `SHOW ARM` runs the hooks
   registered before it answers, names in its answer what each did, cancels one that misses its deadline with words, and arms
   either way). (3 h)
8. The W1 bench for M1 and M3: `bench/r3-present` — a flashing quad through a raw Direct3D 12 flip swap chain, the existing GL
   window and the D3D11 path on one display, and the composition swapchain asked to take Direct3D 12 buffers — built on the
   Windows job, run in the lab (L1, L4). (2 h)

⟂ *(≈ 23 h)*

The round-3 rows:

| Row | Syntax | Owner | Risk | Show class |
|---|---|---|---|---|
| `MACHINE` | `MACHINE [STATUS]` | platform | Low | Query |
| `MACHINE SET` | `MACHINE SET QOS HIGH\|AUTO \| TIMER HIGH\|DEFAULT \| MMCSS ON\|OFF \| PROCESS HIGH\|NORMAL \| CPUSETS AUTO\|OFF \| GPU-PRIORITY NORMAL\|HIGH\|REALTIME \| GC SHOW\|DEFAULT \| AWAKE ON\|OFF` | platform | Review (REALTIME: High) | Setup |
| `MACHINE LOCK` | `MACHINE LOCK [CLEAR]` | platform | Review | Setup |
| `GPU` | `GPU [STATUS]` | d3d12-core | Low | Query |
| `GPU WARM` | `GPU WARM [WAIT]` | d3d12-passes-2 | Low | Perform |
| `GPU REHEARSE` | `GPU REHEARSE [REMOVE]` | stability-2 | High | Heavy |
| `PRESENT PATH` | `PRESENT PATH AUTO\|D3D12\|DXGI\|GL` (D3D12 added) | present | High | Setup |
| `PRESENT PROVE` | `PRESENT PROVE [<output>\|ALL] [FOR <seconds 2-60>]` | present | Low | Perform |
| `WATERMARK` | `WATERMARK <output>\|ALL ON\|OFF [TOP\|BOTTOM\|BOTH] [AT <x> <y>] [SIZE <px 4-32>]` | stability | Review | Layout |
| `OBSERVE` | `OBSERVE <output> FROM <capture uri> \| OBSERVE <output> OFF` | stability | Review | Setup |
| `OBSERVE STATUS` | `OBSERVE STATUS [<output>]` | stability | Low | Query |
| `LATENCY STEP` | `LATENCY STEP <output> [VIA <camera>\|OBSERVER] [STEPS <3-60>]` | stability | High | Heavy |
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | shaders | High | Setup (refused armed by the gate; never drafted: the quarantine is the node's, not the project's) |
| `UNDO LIST` | `UNDO LIST [<n 1-100>]` | desk-ux | Low | Query |
| `UNDO TO` | `UNDO TO <step>` | desk-ux | Review | Project (as `UNDO`: item 7's gate hunk) |
| `VIEW` (extended) | `… [SHARED <pid>]` | desk-gpu | as `VIEW` | as `VIEW` |

Rows whose replies or words grow, without a new row: `OUTPUT STATS` (mode, evidence), `SYNC STATUS` (barrier, phase), `HEALTH`
(shown and observed frames, the GPU's phase), `FX CHECK` (the Direct3D 12 path's stages), `DECODE STATUS` (D3D12VA), `PREFLIGHT`
(every package's rows).

**Acceptance.** `CatalogueTests` (every round-3 row: syntax parses, risk and class set, `SecretFrom` null, every alternative
passes `CommandGate.Check`; no `RiskWhen` lowers a Heavy, Air or Layout line); `RoutesTests` (round-3 rows unrouted answer "not
available on this node yet"); `ModelCompatTests`; `ModuleRuleTests`; `PmvbCodecTests` (path 3 round-trips; path 4 is refused);
`StateOrderTests` (the new keys in their place, empty: the `machine` children after its present keys); `WindowsPlatformTests` (the
new projects' types carry the attribute); `UndoGateTests` (the gate's verdicts: `UNDO TO` allowed for an operator while armed and
drafted for the blind owner, exactly as `UNDO`; `FX RELEASE` refused armed and not draftable); `UndoToStoreTests` (`UndoTo(3)` on
five edits → the root after the second, one step); `HealthCompatTests` (a round-2 HEALTH line reads the new fields as −1 and the
phase as unknown); `ArmHookTests` (a hook past its deadline is cancelled and named, and ARM arms); **every existing test passes
unchanged** (the moved presentation types keep their names; only `using` lines change); the desk builds.

**Verified here.** All but the Windows lines. **On CI (Windows).** WARP and the Agility SDK restored beside the hosts; a one-line
Direct3D 12 test creates a WARP device and says its feature level; M7's and M14's answers in the summary; the bench builds. **On
CI (Linux).** The ES 3.00 leg green. **Manual.** None (the bench's runs are L1 and L4).

### 3.2 `wp3-rhi` — the cut at `GlDevice`, with GL behind it (W1)

*Design: `design/rhi.md`.*

**Slice.**
1. The contracts in `src/PlayMap.Core/Gpu/`: the format, filter, wrap, blend, depth, cull and topology vocabulary (moved from
   `Render/Gpu/GpuTypes.cs`, same names and values); `IGpuDevice`; the resource types callers already name (`GpuTexture`,
   `RenderTarget`, `GpuBuffer`, and `GpuMesh` for today's `VertexArray`) as abstract classes; `ICommandList`
   with a `DrawBuilder` (a `ref struct` that sets a program's constants by name through its layout, binds textures by slot and
   draws); `PipelineKey` (program, blend, depth, cull, topology, target formats) and `DrawState`; `ProgramLayout` (each
   program's constants with type and offset, its texture slots); `FenceValue` (a queue's timeline and value); `IUploadRing`,
   `IReadbackRing`, `IGpuTimers`, `ITextureImporter`, `ITextureExporter`; `IPipelineCache` (look-up that never compiles, warm-up
   off the render thread). With them, everything a backend implements: `IGpuPlatform` (with `Device` in place of `Context`) and
   `IOutputPresenter` (`Record`, then `Present`) moved from `Render/Platform` with `OutputPresenterOptions` and `MonitorInfo`; the
   program table as data (`Core/Gpu/Programs/programs.layout.json`, generated once from the GLSL declarations by a tool test and
   committed, and `ProgramTable : IProgramLibrary`), the one table the GL backend, the Direct3D 12 backend and the HLSL build read;
   user programs made at run time (`CreateProgramAsync`, `ReleaseProgram`, `ProgramSource`, `ProgramMade`); the render graph's
   transient contract (`ITransientMemory`, `Alias`, `Discard`, `SetPlanManaged`), so wp3-d3d12-passes adds none in W2. (7 h)
2. The GL backend under `src/PlayMap.Render/Gl/`: `GlDevice` and its partials, `GlDialect`, `RenderContext`, the GL platforms
   (GLFW, EGL, ANGLE), the compile lane and the HAP encoder's GL use moved there; `GlBackend : IGpuDevice` with an immediate
   `GlCommandList`; fences as values over GL sync objects; the pixel-pack and unpack rings behind `IReadbackRing` and
   `IUploadRing`; timer queries behind `IGpuTimers`; the dmabuf and ANGLE importers behind `ITextureImporter`; `CreateProgramAsync`
   on the compile lane (today's compile and link, the uniforms and samplers read back into a layout); transients rented from the
   pool, `Alias` and `Discard` doing nothing. (6 h)
3. The callers: every Render file of bands 2–3 [map §2.1] through the RHI — `RenderEngine`, the passes, generators, video, LED,
   sends, overlay, HAP, effects, projection; uniforms set by name through the program's layout, allocation-free; loose state
   replaced by one `DrawState` per draw; `IGpuPlatform.Device` in place of `Context`; `IOutputPresenter.Record(list, picture)`
   then `Present()` in place of `Present(GpuTexture)`; the effect code that compiles user GLSL today (`IsfRenderer`,
   `FxChainRunner`, `EffectGenerator`) through `CreateProgramAsync`; the engine's two `GlDevice` files; `PassFrame.Device` an
   `IGpuDevice`. (9 h)
4. The tests: `BackendFixture` in place of `GlFixture` (the GL backend on a dedicated thread today; a backend list a later
   package extends), `Pixels.Read(IGpuDevice, RenderTarget)`, the 213 `gl` tests and 15 files that name `GlDevice` moved onto it;
   no golden file edited. (4 h)

⟂ *(≈ 26 h)*

5. *(stretch)* Constant ids generated per program (`Ids.Output.uAir`) in place of names in the five hottest passes.
6. *(stretch)* Each pass lists the `PipelineKey`s it can draw (wp3-d3d12-passes-2 reads the lists).

**Preflight.** None: rhi adds no show resource and no STATE block (every fixture is unchanged). **INDEX.** None.

**Owns.** `src/PlayMap.Core/Gpu/**`; `src/PlayMap.Render/**` for W1 (`Gl/**` new; `Gpu/**` emptied into `Gl/` and Core;
`Platform/` keeps `GpuPlatform.cs`'s factory and the NVAPI files); `src/PlayMap.Engine/Rendering/**` for W1;
`tests/PlayMap.Render.Tests/**`; `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` and the other rigs' platform lines.

**Hunks.** `src/PlayMap.Media/Hw/HwDecode.cs` (the share's type, if `IGpuShare` moves; at most a using line).

**Depends on.** The spine.

**Acceptance.**
- **Every existing Render and Engine test passes with its golden files unedited** — the pixel goldens byte for byte on
  llvmpipe here in both dialects (core 3.30 and the spine's ES 3.00 leg) and on Mesa WGL on the Windows job.
- `RhiFenceTests`: no type of `Silk.NET.OpenGL` and no `Gl*` type of the backend is named outside `src/PlayMap.Render/Gl/**`;
  Core's `Gpu` and `Presentation` namespaces reference no Render type; `PassFrame` carries no GL type.
- `DrawStateTests`: every `DrawState` maps to exactly one `PipelineKey`; after each draw of a scripted frame the GL state read back
  (blend, depth, cull, polygon mode, bound program) equals what the old calls set, call by call.
- `ProgramLayoutTests`: the program table lists exactly the uniforms and samplers each program's GLSL declares, by name and type;
  no two programs share a name; every `Set`/`Texture` name in Render's sources exists in its program's layout.
- `UserProgramTests`: a translated ISF source through `CreateProgramAsync` on GL → a layout equal to its uniforms and samplers; a
  compile error → the stage and the ISF line; released after its last frame; the FX pixel tests unchanged through it.
- `TransientContractTests`: on GL placements come from the pool and `Alias`, `Discard` and `SetPlanManaged` change no pixel.
- `FenceValueTests`: values grow per queue; `IsComplete` is monotonic; a fence ring of 64 wraps without losing a value.
- `AllocationTests` unchanged: a warm frame allocates nothing.
- The STATE fixtures' own tests regenerate every fixture byte for byte.

**Verified here.** All. **On CI (Windows).** The same suites on Mesa WGL. **Manual (lab).** L20: the GL backend after the cut on
NVIDIA, AMD and Intel Windows drivers — what AUTO runs all round.

**Next round.** The GL backend deleted with its tests once M1's record holds on every tier (GL-only tests leave with it, by name,
in that change); constant ids everywhere.

### 3.3 `wp3-platform` — what a show node claims from Windows (W1)

*Design: `design/platform.md`.*

**Slice.**
1. `PlayMap.Platform.Windows` (`LibraryImport` throughout): the process claims — `HIGH_PRIORITY_CLASS` (never realtime);
   power-throttling opt-out (`SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED` and
   `IGNORE_TIMER_RESOLUTION` in the control mask and a zero state mask: HighQoS and every timer request honoured); the GPU
   scheduling class (`D3DKMTSetProcessSchedulingPriorityClass`), settable and NORMAL by default until L10 measures HIGH; each read
   back where Windows can be asked. The thread claims by role: render and present join MMCSS "Games", audio and timecode "Pro Audio"
   (`AvSetMmThreadCharacteristics`, reverted on dispose); every claim answering what it asked and what Windows said; `ThreadClaims`
   pointed at the platform at start, and the threads' one-line claims (§4's hunks). Each default on cites its documented cause
   (design §2); the two without one (the GPU class, the GC's show mode) are off until L10. (6 h)
2. Time: a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) for every deadline wait on the engine's
   deadline threads — the headless pacer first (today `AutoResetEvent.WaitOne` at the default timer resolution [map §3.1]),
   measured before it switches: both waits' lateness recorded on the runner, the switch's commit citing them; QPC for every stamp,
   as today. (3 h)
3. Keep-awake: `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` while an output is open; the
   screen saver, which that call does not stop, read as a fact; the GC's `SustainedLowLatency` while the show is armed through
   `IShowArmHook`, settable (`GC SHOW`) and DEFAULT until L10 measures it. (2 h)
4. The node's identity and capabilities: OS edition, build and revision (LTSC and IoT Enterprise named), the CPU (model, cores
   by efficiency class, the vector widths .NET accelerates), every DXGI adapter by LUID with vendor, name and driver version, its
   vendor tier (RTX PRO by NVAPI's workstation-feature probe, RTX, Radeon, Arc, WARP) and the published support level (NVIDIA tier
   1, AMD validated per driver, Intel Arc best effort, as `docs/INSTALL.md` says), HAGS, the power plan, the timer resolution
   honoured — the DXGI and NVAPI probes through the platform's own minimal declarations, each structure pinned by a layout golden,
   so the project references Core alone; the FFmpeg build, the sync board's firmware and the Agility runtime filled by their owners
   through the same record; STATE `machine.identity`, `machine.capabilities`, `machine.platform`, `machine.locked`; the context's
   machine item; `MACHINE`, `MACHINE SET` (a setting already at its value is refused with words; REALTIME refused without the
   privilege, naming the privilege and that the installer grants it), `MACHINE LOCK` (keeps this node's driver set in
   `NodeSettings.Machine.Locked`, written by the node's settings writer, never in the project; the locked set again, or `CLEAR`
   with none, refused with words). (8 h)
5. Placement: CPU sets — the critical pool (render, present, decode) on the highest efficiency class, background pools elsewhere,
   by `SetThreadSelectedCpuSets` (soft; never a hard affinity), a no-op said on one efficiency class, the dual-CCD option off by
   default; decode workers above normal; background pools at EcoQoS (`SetThreadInformation(ThreadPowerThrottling)`). (4 h)
6. Machine preflight's must rows, every row a fact with words, never a silent change: the OS below build 26100 (red), the power
   plan, sleep and display timeouts, the screen saver, a pending reboot, each claim of items 1–5 that Windows refused, the timer's
   lateness, and drift from `MACHINE LOCK`'s set (amber, naming the old and new versions, fix `MACHINE LOCK`). (3 h)

⟂ *(≈ 26 h)*

7. *(stretch)* Preflight's remaining rows: USB selective suspend, PCIe link-state power management, notifications, the Windows
   Update deferral policy, HAGS as a fact.
8. *(stretch)* A GC pause histogram (EventPipe) in STATE beside the frame time — the measurement `GC SHOW`'s default waits for.

**Preflight.** Item 6's rows, section `machine`. **INDEX.** Section `machine`: the claims' lights (feedback only).

**Owns.** `src/PlayMap.Platform.Windows/**`, `src/PlayMap.Core/Machine/**` (after the spine), the `Machine` stubs,
`tests/PlayMap.Platform.Windows.Tests/**`, `tests/fixtures/state/machine.json`, `docs/PLATFORM.md`, `docs/wire/machine.md`.

**Hunks.** One `ThreadClaims.Claim(role)` line at each thread's start, after rhi: `src/PlayMap.Engine/Rendering/RenderHost.cs`
(render), `src/PlayMap.Engine/ControlLane.cs` (control), `src/PlayMap.Net/Led/LedSender.cs` (LED), `src/PlayMap.Media/Audio/
AudioOut.cs` (audio), `src/PlayMap.Engine/Timecode/LtcInput.cs` (timecode), `src/PlayMap.Media/Sources/FfmpegVideoSource.cs` and
`src/PlayMap.Media/Hap/HapWorkers.cs` (decode); `src/PlayMap.Engine/Rendering/HeadlessPacing.cs` (the timer, after rhi);
`src/PlayMap.Engine/Fleet/MachineSampler.Windows.cs` (reads the identity record). The plan thread claims `Background` in
wp3-d3d12-passes' own file.

**Depends on.** The spine.

**Acceptance.**
- `PlatformPolicyTests` (pure, both jobs): the role → claims table; a refused claim keeps its words; a setting at its value refused;
  the defaults equal design §2's table, each with its cause.
- `CpuSetTests` (pure): a fake topology of 8 performance and 16 efficiency cores → the critical pool on the 8, background on the
  16; one class → nothing set, said.
- `PlatformCallsTests` (Windows): every claim answers what it asked and what Windows said — on the runner's Server SKU and Basic
  Render Driver a refusal with its named words passes, M14 recording which; the power-throttling state read back with
  `GetProcessInformation` equals what was set where Windows answers; an MMCSS handle is taken and reverted on dispose where the
  service runs; 1 000 waits of 1 ms on today's wait and on the high-resolution timer, both distributions recorded and no ceiling
  asserted (the runner is shared; L10 holds the pass mark).
- `PlatformLayoutTests` (both): the platform's own DXGI and NVAPI probe structures against the headers' sizes and offsets.
- `IdentityTests`: the record's keys and types from a fake platform, the tier and support level per vendor; `machine.json`
  written; a locked set against a changed GPU driver → an amber row naming both versions with the fix line `MACHINE LOCK`.
- `MachineVerbTests`: `MACHINE SET GPU-PRIORITY REALTIME` without the privilege → refused naming it; armed → refused by the class;
  `MACHINE SET QOS HIGH` when it is → refused "already high"; `MACHINE LOCK` on the locked set and `MACHINE LOCK CLEAR` with none →
  refused with the reason, nothing written.
- `PreflightMachineTests`: each row fails on an injected fake fact and passes once fixed; every fix line parses.
- On Linux the null platform's words ("the platform seam is Windows only") in STATE and preflight.

**Verified here.** Policy, placement, verbs, STATE, preflight, against fakes. **On CI (Windows).** Every call answers, read back
where the Server SKU allows. **Manual (lab).** L10: p99.9 frame time with and without each claim on an NVIDIA, an AMD and an Intel
machine — the measured cause that turns each default on or off; a hybrid Intel CPU's placement; a dual-CCD X3D CPU. L22: a precise
Windows thread time — a thread's cycles at the time-stamp counter's frequency from the platform against its running time, with
Memory Integrity on and off, and the ticks' step under the node's own timer claims.

**Next round.** Core reservation, large pages for frame pools, a pre-roll measured against NativeAOT (M13).

### 3.4 `wp3-shaders` — one HLSL source; user ISF out of process (W1)

*Design: `design/shaders.md`.*

**Slice.**
1. The 40 product programs [map §2.4] in HLSL under `src/PlayMap.Render.D3D12/Shaders/`, one file per family, with the house's
   conventions (targets top row first, row-vector matrices, UV v down, geometry from `SV_VertexID`, exact reads with `Load`), one
   root signature for all (the program's constants in `b0`; a binding block in `b1` of sixteen texture and sixteen sampler
   indices into directly indexed heaps, Shader Model 6.6); compiled by DXC at build time to DXIL with PDBs and reflection,
   embedded in the assembly (on the Windows job; on Linux too if M6 says yes). (12 h)
2. `ShaderLayoutTests`: each program's DXC reflection (constant names, types, offsets; texture slots) equals its
   `ProgramLayout` in rhi's program table; before rhi merges, against a parse of the GLSL declarations; the slot headers generated
   from the table's file by an MSBuild step, so `Render.D3D12` reads data and never references Render. The integer-exact
   programs that only `Load` (`video.pack`, `video.unpack`) are marked for wp3-d3d12-core's byte-exact parity tests; the HAP
   encoder's four are compiled and reflected but not parity-tested this round (the encoder stays GL in the optimise worker). (2 h)
3. The shader worker, `PlayMap shader-worker` (its own process, started and watched by the engine as the optimise worker is, one
   JSON object a line): ISF → `IsfTranslator`'s new target (GLSL 4.50, explicit in/out, set/binding for images, ISF inputs as
   loose uniforms) → glslang relaxed (`-R`) → SPIR-V → `spirv-val` → SPIRV-Cross → HLSL (SM 6.0) → DXC → validated DXIL and its
   reflection; each failure in that stage's words, with the line mapped back through `#line` to the ISF source; natives pinned by
   hash, loaded by the worker only — the engine never loads a compiler; a source that stops the worker three times fails with
   words. (8 h)
4. Quarantine: `ShaderQuarantine` (Core: a kind's hash → reason, when, build; kept in the node's home, never in the project), a
   quarantined kind's slots drawn bypassed with words, STATE `fx.quarantine`, a red preflight row with `FX RELEASE <kind>` and
   `FX REMOVE <slot>`; `FX RELEASE` (High, class Setup: refused armed by the gate and never drafted blind; a kind not quarantined
   refused; `ALL` with none quarantined refused). The record is filled by wp3-shaders-2's GPU-time guard and wp3-stability-2's
   removal breadcrumbs; this package lands it, the bypass and the verb. `FX CHECK` reports the new path's stages. (3 h)

⟂ *(≈ 25 h)*

5. *(stretch)* spirv_to_dxil behind the same stage (built from pinned Mesa source), compared with SPIRV-Cross on the corpus.

The corpus on the new road (above wp3-shaders-2's cut line) and user effects on the device are the continuation's, §3.16.

**Preflight.** Quarantined kinds (red, section `fx`). **INDEX.** None new.

**Owns.** `src/PlayMap.Render.D3D12/Shaders/**`, `src/PlayMap.Engine/ShaderWorker/**`, the `Shaders` stubs,
`src/PlayMap.Core/Effects/{IsfTranslator, IsfTranslator.Vulkan, ShaderQuarantine}.cs`, the FX block's `FX CHECK` words and
`FX RELEASE`, `tests/fixtures/state/shaders.json` (`fx.compile`, `fx.quarantine`) and its cards, its named CI block (the pinned
compiler natives).

**Hunks.** `src/PlayMap.Render/Effects/FxChainRunner.cs` (bypass a quarantined kind, after rhi).

**Depends on.** The spine; rhi before merge (the layouts).

**Acceptance.**
- `HlslBuildTests` (Windows): every program compiles to validated DXIL with a PDB; the set equals `ShaderLibrary.Names`.
- `ShaderLayoutTests` (Windows; the GLSL side on both).
- `IsfVulkanTargetTests` (both): the built-in ISF files and the test corpus translate, compile under glslang `-R` and pass
  `spirv-val`; a translator error keeps its ISF line.
- `ShaderWorkerTests`: a good source → DXIL and a layout; a syntax error → the stage and the ISF line; a fake compiler that aborts
  → the worker restarts with backoff, the third stop fails the kind with words; the engine's process holds no compiler module.
- `QuarantineTests`: a quarantined kind's slot draws its input unchanged (± 0 codes on GL); STATE and preflight carry it; `FX
  RELEASE` armed → the gate's verdict a refusal by class and the quarantine unchanged; from the blind owner never drafted — armed
  refused, disarmed acting on the node's quarantine at once, the draft holding no quarantine either way; on a free kind, and `ALL`
  with none, refused with words; else the slot draws again.

**Verified here.** Translator, glslang, spirv-val and SPIRV-Cross (their Linux natives), the worker's protocol and supervision,
quarantine. **On CI (Windows).** DXC, reflection, the worker end to end to DXIL. **Manual.** None (pixel parity is
wp3-d3d12-core's).

**Next round.** The GLSL library deleted with GL; Shader Model 7's SPIR-V intake when it ships; user HLSL.

### 3.5 `wp3-installer` — the MSI, the supervisor and the version rules (W1)

*Design: `design/installer.md`.*

**Slice.**
1. The version rules (§1.3): `Version.props` refuses a major or minor above 255; `MsiVersion` = MAJOR.MINOR.BUILD from numbered
   builds only (a local build refuses to make an MSI, with words); `BuildInfo.MsiVersion`; the release job's check that the new
   MSI version is greater than the last release's. Both CI jobs switch their FFmpeg to the hash-pinned `lgpl-shared` build of the
   same FFmpeg 9.0.2, so every decode test runs on the libraries the MSI will carry; the GPL command-line build stays on CI's path
   only to encode test media (libx264 and libx265), never beside a test host. (3 h)
2. The MSI (WiX v7 SDK project `installer/PlayMap.Installer.wixproj`, outside `PlayMap.sln` because an MSI builds on Windows only,
   built by the Windows job's MSI step; the EULA accepted by property in CI): per machine into Program Files\PlayMap; the
   self-contained publish with ReadyToRun (`PublishReadyToRun`: M13's default with the pre-roll, the first ten minutes recorded in
   L21) harvested; FFmpeg's LGPL shared libraries (item 1's pin) and libvmx beside the exe (today neither is in the folder
   [map §6.2]); the Agility SDK runtime under `D3D12\` (its debug layer left out of releases); the shader worker's natives; a launch
   condition of Windows 11 build 26100 or later on x64 (a registry search, not `VersionNT`); `MajorUpgrade` with no same-version
   upgrade and a downgrade refused with words; the product's name carrying the SemVer. (8 h)
3. Machine-level actions, once, so the engine never runs as administrator: firewall rules for `PlayMap.exe` (TCP 9940, 9941; UDP
   9928, 9942, 9943, 6454, 5568, 5353); no URL ACL (Kestrel binds its own sockets — asserted by a standard user binding 9940);
   mDNS checked on the runner (`_playmap._tcp` browsable beside Windows' own responder on 5353; if the two collide, the node
   registers through `DnsServiceRegister` instead, behind the platform seam);
   WER LocalDumps for `PlayMap.exe` (`%ProgramData%\PlayMap\dumps`, full, five kept, the show user's write ACL); the show
   account granted `SeIncreaseBasePriorityPrivilege` (the realtime GPU queue, used only when measured) by the supervisor's
   `grant` command as a deferred custom action; the Event Log source. (5 h)
4. The supervisor (`PlayMap.Supervisor`, a LocalSystem service with recovery actions on crashes and on non-zero stops): starts
   `PlayMap.exe engine` in the auto-logged-on show user's session (`WTSQueryUserToken`, `CreateProcessAsUser`; M9), restarts it
   with backoff (1 s doubling to 30 s), and stops an engine whose shown-frame heartbeat has not advanced for the stale time while
   HEALTH's `gpu.phase` says `running` (HEALTH over loopback: the fields the spine reserves, wp3-stability fills) — the stale time
   the larger of 3 s and four times the rebuild's first-good-frame time the Windows job records, a start or a rebuild exempt up to
   that budget, and the rule inactive, said in the status file, while the count is unknown (−1) — and journals why; it stops
   trying after five restarts in ten minutes and says so in a status file the engine, the desk and the fleet read, and writes a
   manifest (build, commit, last journal sequence) beside each new WER dump; the desk attaches to a supervised engine instead of
   starting its own; the MSI's `preinstall-check` fails closed — no engine process: the install goes on; an engine answering armed:
   refused with the node's words; an engine process alive that does not answer: refused with words, never read as "not armed".
   (7 h)
5. Signing: Azure Artifact Signing on `main`'s builds once an identity exists (§9 decision 2) — the exe, PlayMap's own DLLs, the
   MSI — checked by `Get-AuthenticodeSignature` in the job; before then the step skips with a named notice in the job's summary and
   the artifact is named `-unsigned`, on `main` as on branches; the step proved on a protected test branch that holds the secret
   before it gates `main`. (3 h)

⟂ *(≈ 26 h)*

6. *(stretch)* The opt-in show-machine feature: Windows Update deferral (feature updates 365 days, quality 30, no automatic
   restart with a user logged on, active hours), notifications off, `DisableUserPresenceQos`, the show account in Performance Log
   Users (for wp3-present's ETW stretch) — written once by the MSI and reported by preflight whether written or not.
7. *(stretch)* A per-build SBOM as a CI artifact.

**Preflight.** Section `install`: not installed (a folder run, amber on a show node), the supervisor's state (restarts, stopped,
the freeze rule waiting for shown frames), auto-logon on and for which user, the show machine's policies. **INDEX.** None.

**Owns.** `installer/**`, `src/PlayMap.Supervisor/**`, `src/PlayMap.Engine/Install/**` (STATE `machine.install` from the install's
registry key and the supervisor's status file), `tests/PlayMap.App.Tests/Install/**`, `tests/fixtures/state/install.json`,
`docs/INSTALL.md` (the guidance: IoT Enterprise LTSC 2024 for show nodes, auto-logon with the LSA secret and never a plain-text
registry password, Shell Launcher, BIOS power-on after loss, the update window, the published GPU support tiers and the two
version numbers), its named CI block (§2.2).

**Hunks.** `Version.props` (the 255 rule); `src/PlayMap.Core/BuildInfo.cs` (`MsiVersion`); `src/PlayMap.App/Link/
EngineSupervisor.cs` (attach to a supervised engine; after desk-ux); the FFmpeg steps of both CI jobs (item 1).

**Depends on.** The spine (the `versioning` base).

**Acceptance.**
- `MsiVersionTests` (both): (0.2.0, build 17) → 0.2.17; (0.2.3, 18) → 0.2.18, greater than 0.2.17; a major or minor of 256
  refused; build 0 refused for an MSI; (0.2.0, 1) after a released 0.2.500 → refused "lower than the last release: raise MINOR".
- `VersionPropsTests`: a build with `PlayMapVersion` 256.0.0 fails with the words.
- `PreinstallCheckTests` (the decision pure on both; against fake engines on Windows): no engine process → go on; an engine
  answering armed → refused with its words; disarmed → go on; **an engine process alive but silent → refused**, naming its pid; a
  pid file whose process is gone → go on.
- `InstallerTests` (Windows, elevated): the MSI builds; a quiet install lays the files, registers the service with its recovery
  actions, the firewall rules and the WER values; its FFmpeg hashes to CI's LGPL pin; the previous build's MSI (built in the job
  with a lower build number) upgrades to this one and keeps the node's settings; a downgrade is refused with words; an uninstall
  removes all but the node's data; with a fake engine on loopback answering armed, the install fails with the node's words; with
  a fake engine process that never answers, it fails with "does not answer".
- `SupervisorTests` (pure, both): the backoff schedule, the heartbeat rule on a manual clock, the rule inactive while the count is
  −1, `starting` and `rebuilding` exempt within the budget and frozen past it, the five-in-ten-minutes stop, the dump manifest; on
  Windows the service installs, starts and stops on the runner.
- `SessionStartProbe` (Windows, a probe, not a gate): M9's calls on the runner, whose job runs in an interactive session; the
  answer in the summary.
- `DeskAttachTests` (in `tests/PlayMap.App.Tests/Install/`): a supervised machine's desk attaches and never starts an engine.
- `SigningTests` (`main` and the signing test branch): every signed file verifies; with no identity, the notice and the
  `-unsigned` name.

**Verified here.** Version maths, supervisor policy, the check's decision. **On CI (Windows).** The MSI's life cycle, the service,
the firewall and WER settings, the LGPL FFmpeg under every decode test, signing once an identity exists. **Manual (lab).** L11: IoT
Enterprise LTSC 2024 with auto-logon; the supervisor starting the engine in session 1 (M9); Shell Launcher; an upgrade during a
rehearsal; L18: SmartScreen; L21: the first ten minutes.

**Next round.** `MACHINE HARDEN` (the supervisor applying a hardening fix a person approved), an updater that refuses while armed,
LTS channels with pinned drivers.

### 3.6 `wp3-desk-ux` — the consoles' safety patterns, where not yet built (W1)

The desk on `main` already has the show strip, the gate desk-wide, the armed red frame, the attention bar, the Esc ladder with
its guard, Edit blind and the min-GO rule [main's `docs/DESK.md`]. This package adds only what it lacks.

**Slice.**
1. The GO guard made visible: while the node's min-GO window is open after a GO (STATE.show's last GO and MinGo), every GO control
   draws a red border that closes with the window; a GO the node refuses flashes once — a single event, never twice in 333 ms —
   with the node's words in its tooltip and the attention bar. (3 h)
2. Blind made unmistakable: while STATE.show.blind names this desk's session, the whole desk turns grey (a theme variant), every
   pane showing draft content carries the word BLIND, and the window's frame takes the blind colour (distinct from armed red);
   another desk's draft leaves this one's colours and says whose it is. (3 h)
3. The Oops history: `UNDO LIST [<n>]` (the undo steps newest first, in field words, with who and when, from the store's undo
   list — the spine's `ProjectStore` steps — and the journal; for the blind owner, the draft's own steps) and `UNDO TO <step>` (the
   step and every newer one undone as one action, so `UNDO TO` the newest is one `UNDO`; refused with LockGuard's words across a
   lock, with the range for an unknown step, and "nothing to undo" on an empty list; drafted for the blind owner and allowed for an
   operator while armed exactly as `UNDO` is, by the spine's gate hunk), engine side in `EngineCore.Undo.cs`; the desk's Oops
   panel, and "Undo to here" in each step's right-click menu. (6 h)
4. The dark room and the status rules: a dim mode (unselected chrome dimmed, the selected and the critical at full contrast) with
   a desk brightness from 20 to 100 % apart from Windows', and a daylight theme; every light paired with a shape and a word (✓ ok,
   △ warn, ✕ fault, ■ armed); ok, warn and fault at least 3:1 apart in luminance in every theme; saturated red only where the air is
   affected; no alarm faster than 2 Hz; a confirmation before closing the desk while armed ("The show keeps playing on the node").
   (6 h)
5. Preview age: each preview's age from its ring slot's stamp; older than two frames of its own rate → a "stale" badge with the
   age; frozen for 1 s → greyed with the reason (the engine quiet, the ring gone). wp3-desk-gpu reuses it on the GPU path. (3 h)
6. The M2 bench: `bench/r3-avalonia-import` — an Avalonia 12.1 window that lists `SupportedImageHandleTypes` and
   `SupportedSemaphoreTypes`, imports a Direct3D 12 shared texture and fence from a helper process and shows a frame, and records
   the exact name and signature of the compositor's timeline-semaphore update; built on the Windows job, run in the lab (L3). (2 h)

⟂ *(≈ 23 h)*

7. *(stretch)* Map anything: every control whose line can be bound highlighted; touching one starts `BIND LEARN` for its line.

**Preflight.** None (desk). **INDEX.** None.

**Owns.** `src/PlayMap.App/**` for W1, the `Undo` stub, `tests/PlayMap.App.Tests/**` for W1 (but `Install/**`, wp3-installer's
area), `bench/r3-avalonia-import/**`, `docs/DESK.md`.

**Hunks.** None (the spine lands `ProjectStore.UndoTo`, its steps' list and the gate's `UNDO TO` rule).

**Depends on.** The spine.

**Acceptance.**
- `GoGuardTests` (headless Avalonia, manual clock): GO with MinGo 500 → the border on, off at 500 ms; a refused GO flashes once.
- `BlindLookTests`: this desk's draft → the grey variant and BLIND on every draft pane; another desk's → neither, its owner named.
- `UndoListTests` (Engine): five edits → five steps newest first in field words; `UNDO TO` the third → the root after the second,
  one journal row; across a lock → refused, nothing undone; an unknown step → refused with the range; an empty list → refused
  "nothing to undo"; armed, from an operator → allowed, as `UNDO`; from the blind owner disarmed → the draft's steps listed and
  undone, the live project's revision unchanged.
- `StatusRuleTests`: every status template has a shape and a word per light; the 3:1 contrast computed from each theme's tokens;
  the red token used only by air styles; no animation period under 500 ms.
- `PreviewAgeTests`: a frozen ring → stale after two frames, grey at 1 s with the reason.
- `CloseArmedTests`; `DeskVerbRuleTests` pass with the new actions.

**Verified here.** All (headless); the bench builds on the Windows job. **Manual (lab).** L19: a formative session with operators
in a dark room, recorded in `docs/UX.md`; L3: the bench (M2).

**Next round.** A summative test of the critical tasks (GO, panic, recovering after a desk restart, finding a faulted output).

### 3.7 `wp3-d3d12-core` — the Direct3D 12 device behind the RHI (W2)

*Design: `design/d3d12.md`.*

**Slice.**
1. The device: the adapter by LUID (the outputs' displays' adapter; WARP for CI), the Agility runtime through a device factory
   (M7, answered on the spine's day; the OS runtime with words otherwise), DRED (auto-breadcrumbs, page-fault data) and NVIDIA's
   Aftermath (when its library sits beside the exe) armed before the device is made; the debug layer and GPU-based validation in
   tests; the features checked and recorded (shader model, resource binding tier, enhanced barriers, GPU upload heaps);
   `D3D12Device : IGpuDevice`; M8's and M15's first-day answers recorded. (5 h)
2. Queues and fences: a direct queue at HIGH priority (GLOBAL_REALTIME only when `MACHINE SET GPU-PRIORITY REALTIME` asks and the
   privilege is held, else refused with words — creation fails rather than downgrades), a copy queue; timeline fences as
   `FenceValue`; command allocators per frame in flight; GPU-side waits between queues. (3 h)
3. Memory and descriptors: committed and placed resources through the D3D12 Memory Allocator (TerraFX's port, or PlayMap's own
   pools); an upload ring per frame in flight (a GPU upload heap where ReBAR and build 26080 allow); a readback ring on the copy
   queue; a shader-visible CBV/SRV/UAV heap (static ranges and a per-frame ring), RTV and DSV heaps, static samplers; every
   descriptor and resource released only after the fence value of its last use completes; the render graph's primitives — transient
   memory with placed resources at a plan's offsets, the aliasing barrier, the discard — so wp3-d3d12-passes adds nothing to these
   folders; the exporter (`Share/`): shared committed textures, shared fences and their NT handles, which wp3-video's Spout bridge
   and wp3-desk-gpu's previews both build on. (7 h)
4. Commands: `D3D12CommandList : ICommandList` with automatic state tracking and barriers (per subresource) and its plan-managed
   opt-out (`SetPlanManaged`), the shared root signature, constants into the frame's ring through the program's layout, pipelines
   from the embedded DXIL through `IPipelineCache` — made on the render thread only when no warm-up made them and then counted
   (`render.pipelines.renderThreadMakes`), kept in a pipeline library keyed by adapter LUID, driver version and build; timestamps
   per pass; PIX markers and DRED breadcrumbs per pass. (6 h)
5. The Direct3D 12 leg of `BackendFixture`: each product program drawn once on WARP (`ProgramSmokeTests`, the first run of every
   HLSL program, named alone when it fails), then every Render pixel scene but the user-effect ones (wp3-shaders-2's) on WARP within
   its row of the D3D12 tolerance table — the table filled from the first run, each widening a row with the measured maximum and
   mean difference and the reason, reviewed at the gate; packed video (`video.pack`, `video.unpack`) byte-exact; an offscreen
   D3D12 platform, selected by the platform options (`PRESENT PATH D3D12`'s routing, the Engine suites on it and their STATE twins
   are wp3-present's), with one render-host scene on it; STATE `render` and `machine.present.api` "d3d12"; `GPU`. (5 h)

⟂ *(≈ 26 h)*

6. *(stretch)* An async compute queue for the particle step, with a serial fallback, kept only where measured faster.

**Preflight.** Section `gpu`: running on WARP (red on a show node), the debug layer on (amber), pipelines made on the render
thread since the last arm (amber), the Agility runtime not the one shipped (amber). **INDEX.** None.

**Owns.** `src/PlayMap.Render.D3D12/**` for W2 (every folder of `design/d3d12.md` §0 that exists in W2; `Shaders/**`, finished in
W1, passes to it too), `src/PlayMap.Core/Gpu/**` for W2, `tests/PlayMap.Render.D3D12.Tests/**`, the `Gpu` stubs,
`tests/PlayMap.Render.Tests/**` for W2 (`BackendFixture`'s Direct3D 12 leg, `PixelParityTests` and its tolerance table; other
packages' new tests sit in their own area folders), `src/PlayMap.Render/Platform/GpuPlatform.cs` (the factory) for W2,
`src/PlayMap.Engine/EngineCore.Output.cs` for W2 (the `api` word), `tests/fixtures/state/gpu.json`, `docs/wire/gpu.md`.

**Hunks.** None beyond its W2 files.

**Depends on.** rhi, shaders.

**Acceptance.**
- `D3D12DeviceTests` (Windows, WARP): the device comes up with the debug layer and GPU-based validation; DRED on; the features
  recorded; no message across the whole Direct3D 12 run.
- `ProgramSmokeTests` (Windows): every product program drawn once with its layout's constants and textures, before the suite.
- `PixelParityTests` (Windows): every Render pixel scene but the user-effect ones on WARP within its row of the tolerance table —
  the same goldens for both backends; packed video byte-exact. The HAP encoder's blocks are not in it: the encoder stays a GL user
  in the optimise worker this round.
- `TransientAliasTests` (Windows): two placed textures at one offset with an aliasing barrier and a discard draw what two
  committed ones draw; a plan-managed resource gets no automatic barrier; no debug-layer message.
- `ExporterTests` (Windows): a shared texture and fence opened by NT handle on a second WARP device in the same process; the chart
  reads back and the fence's value is seen.
- `LifetimeTests`: a texture disposed while in flight is released only after its fence completes; 1 000 frames of create and
  dispose → live counts back to their baseline; DRED's recently freed list empty at the end.
- `UploadReadbackTests`: byte-exact round trips on the copy queue; the render thread never waits on a readback (counted).
- `PipelineCacheTests`: a second start makes no pipeline (all from the library); a changed driver key → re-warmed with words; a
  render-thread make counted.
- `D3D12AllocationTests`: a warm static frame allocates nothing.
- `D3D12HostTests`: the headless render host on the offscreen D3D12 platform renders one scene; STATE says `api` d3d12 with the
  adapter.

**Verified here.** The C# builds; the pure parts (the release queue, the ring allocator, the cache key, the tolerance table's
reader). **On CI (Windows, WARP).** All the above, read from Linux through the spine's dispatch leg and its artifacts. **Manual
(lab).** L15: every pixel scene on NVIDIA, AMD and Intel drivers; an Aftermath dump of a forced fault on NVIDIA; a Radeon GPU
Detective report on AMD.

**Next round.** Enhanced barriers by default where every tier has them; work graphs where a pass gains from them.

### 3.8 `wp3-d3d12-passes` — the render graph, compiled when the show changes (W2)

*Design: `design/graph.md`.*

**Slice.**
1. Every pass declares what it reads and writes: `IFramePass.Declare(ref GraphBuilder)` beside its existing calls, for the five
   hooked passes (effects, LED, sends, overlay, the engine's `AirEnd`) and the frame's own stages (sources, canvases, surfaces,
   each output's picture, the output stage, final taps, previews); the frame's order and doctrine unchanged (blackout before
   content code, raw scan frames skip, held outputs keep their picture). (6 h)
2. The compiler (`src/PlayMap.Core/Graph`, pure): the project snapshot, the outputs and the passes' declarations → an immutable
   `FramePlan` — passes in order, unused passes culled, transient lifetimes, transients aliased by size and format within a frame,
   barriers per use, copy-queue work with its fence handshake, the plan's pipeline keys; imported resources (delay rings, ISF
   persistent buffers, the particle field's state, previews' shared textures, presenters' buffers) never aliased; compiled on the
   render host's plan thread when the project, the outputs or the air's routing change, published by volatile reference, walked
   by the render thread from the next frame. (8 h)
3. Both backends walk the plan (on GL the barriers are no-ops and transients come from the pool); on Direct3D 12 the plan's
   resources are marked plan-managed and its transients placed, aliased and discarded through d3d12-core's primitives (rhi's
   `ITransientMemory`, `Alias`, `Discard`, `SetPlanManaged`) — this package adds nothing to `Render.D3D12`; STATE `render.graph`
   (passes, transients, aliased bytes, compile ms, when). (4 h)
4. The missing-barrier check: every golden scene on WARP with aliasing on and off, under the debug layer's barrier validation —
   the same pixels and no message. (3 h)
5. Plans compiled, when the show is armed (an `IShowArmHook`, the spine's), for every state it can reach without an edit: each
   playlist item's chains and transitions, the panic outcomes' safe looks, the blind draft's view. (3 h)

⟂ *(≈ 24 h)*

6. *(stretch)* A graph view in STATE for the desk's Health drawer (the passes and their costs as a card).

**Preflight.** None new (wp3-d3d12-passes-2 adds "pipelines compiled"). **INDEX.** None.

**Owns.** `src/PlayMap.Core/Graph/**`; `src/PlayMap.Render/{Engine, Passes, Projection, Effects, Led}/**` for W2 (the passes'
`Declare` methods; but `Passes/WatermarkPass.cs`, wp3-stability's in W2); `src/PlayMap.Render/Gl/**` for W2 (the GL walk);
`src/PlayMap.Engine/Rendering/RenderHost.cs` and the `Plan` stub (`RenderHost.Plan.cs`) for W2;
`tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` for W2; `tests/fixtures/state/graph.json` and its cards.

**Hunks.** `src/PlayMap.Engine/Rendering/RenderHost.VideoIo.cs` (`AirEnd.Declare`); `src/PlayMap.Render/Passes/WatermarkPass.cs`
(its `Declare`, after stability); `tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` (its scenes' rows).

**Depends on.** rhi; d3d12-core before merge (§5.3).

**Acceptance.**
- `GraphCompilerTests` (Core, pure): two outputs × three surfaces × two chains → the plan's order equals the frame's documented
  order; an unused pass culled; transients with disjoint lifetimes share memory, overlapping never; an imported resource never
  aliased; a barrier before every read of a written resource and none twice; the same topology → the same plan hash.
- `PlanWalkTests`: every golden scene through the plan equals the immediate frame's goldens byte for byte on GL (here) and within
  tolerance on WARP (Windows).
- `AliasingParityTests` (Windows): aliasing on and off identical; no debug-layer message.
- `PlanSwapTests`: an edit mid-run → the next frame walks the new plan; the swap allocates nothing on the render thread.
- `ArmedPlansTests`: arming compiles the reachable plans through its arm hook; a transition's plan is ready before its window
  opens.

**Verified here.** The compiler, GL walking. **On CI (Windows).** D3D12 walking, aliasing, validation. **Manual (lab).** L16: GPU
time per pass on real GPUs (from -2's timings).

**Next round.** Async compute placed by the compiler where measured.

### 3.9 `wp3-stability` — prove what the audience saw (W2)

*Design: `design/stability.md` Part A (§1–§7).*

**Slice.**
1. The watermark: `AirWatermark` (Core: the refresh index on the leader's time line at the frame's vblank — the leader instant
   over the output's period, mod 2^24 — the low 16 bits of that leader instant in milliseconds and a CRC-8, as two-tone 8 × 8 px
   blocks, a mark at the raster's top and one at its bottom), stamped last in the output stage on every frame the output presents,
   held frames included; a function of show time, so a hot backup stamps the same mark and stays the primary's picture to the byte
   (`BackupRenderTests` gain the case), and still a counter that stops when presents stop; never drawn while the output is black
   (blackout, a black panic, a fenced node) — the observer reads those from STATE; per output by `WATERMARK` and
   `Output.Watermark` (`ON` as it is and `OFF` when off refused with the reason); drawn through the RHI on both backends; preflight
   says where it shows on the air. (5 h)
2. The observer: `PlayMap observer` (the same exe, its own process, with no GPU device and no render code), started and watched by
   the engine as its workers are, reading a capture source through Media (a capture device, or an OMT or NDI receive of a
   loop-through), decoding both marks of each captured frame into a verdict — ok; held (the counter runs while STATE says the
   output holds its picture: expected); stale (the counter stops for two frames: the pipeline froze, whatever the content does);
   torn (the two marks differ); wrong (the leader instant off by more than a frame); late (lit later than the predicted display
   time by more than the output's budget); lost (no mark while STATE says the output shows); black (no mark while STATE says it is
   black: expected) — on its standard output to the engine and into a status file the supervisor reads, so a wedged engine is
   still caught; STATE `outputs[].observed`, `OBSERVE` (its URI grammar fail-closed: `capture:`, `omt:` and `ndi:` only, user
   information or a query refused with words; its current source again, or `OFF` with nothing observed, refused with the reason;
   the source in `NodeSettings.Observe`, this node's), `OBSERVE STATUS`, a red preflight row, HEALTH's outputs category. (8 h)
3. The heartbeat: HEALTH's reserved `ShownFrames` (the present ledger's count, from display statistics where there are any, swap
   returns on GL) and `ObservedFrames` filled, −1 until known, and HEALTH's `gpu` phase kept `running`; the supervisor's rule reads
   them. (2 h)
4. `LATENCY STEP`: a step in the watermark's region (or, through a camera in rehearsal, the whole output) at chosen leader
   instants, observed; glass-to-glass p50 and p95 per output; refused while armed (Heavy), refused with neither an observer nor a
   camera, naming both. wp3-present adds each stage's share. (5 h)
5. The crash policy: an unhandled exception only flushes the journal (whose header carries the build) and writes a "stopping"
   STATE line, then lets the process end for WER's dump; no in-process crash reporter; dumps stay on the machine. (2 h)

⟂ *(≈ 22 h)*

6. *(stretch)* The observer reading the scan camera's view of the wall in rehearsal (larger marks).
7. *(stretch)* The air hazard file (`docs/HAZARDS.md`: each hazard to the air traced to the test that holds it, design §14).

**Preflight.** Section `observe`: an observed output stale, torn, wrong or lost (red), late (amber); a watermark showing on the
air (amber, with where). **INDEX.** Section `observe`: each observed output's light.

**Owns.** `src/PlayMap.Core/Observe/**`, `src/PlayMap.Engine/Observe/**`, the `Observe` stubs (`NodeSettings.Observe.cs` among
them), `src/PlayMap.Render/Passes/WatermarkPass.cs` for W2, `tests/fixtures/state/observe.json` and its cards, `docs/OBSERVE.md`,
`docs/wire/observe.md`.

**Hunks.** `src/PlayMap.Render/Passes/OutputStage.cs` (the watermark last); `src/PlayMap.Engine/EngineCore.Fleet.cs` (fills the
spine's `HealthOutput` counts); `src/PlayMap.Core/Presentation/PresentLedger.cs` (the shown-frame count);
`tests/PlayMap.Engine.Tests/Fleet/BackupRenderTests.cs` (a case with the watermark on).

**Depends on.** rhi.

**Acceptance.**
- `WatermarkCodecTests`: every counter near the 24-bit wrap round-trips; a flipped block fails the CRC; differing marks → torn;
  two hosts given the same leader instant stamp the same code.
- `ObserverVerdictTests` (manual clock): scripted captures → each verdict with its words; stale against held by STATE; blackout
  expected.
- `ObserverLoopbackTests` (both jobs): an output sent over OMT loopback as the capture; the observer process reads it; STATE ok;
  `FREEZE ON` → held; an injected fault (the host stops presenting new pictures on one output while STATE says it plays) → stale
  after two captured frames without an advance — counted in captured frames, never timed — preflight red, HEALTH red; the
  observer killed → restarted with backoff, said in STATE.
- `ObserveUriTests`, `ObserveVerbTests`: the grammar's refusals, nothing of a refused URI journalled; `OBSERVE` on its current
  source, `OBSERVE … OFF` with nothing observed, `WATERMARK … ON` as it is and `OFF` when off — each refused with its reason.
- `BackupRenderTests` (the new case): primary and backup with the watermark on stay byte-equal.
- `WatermarkRenderTests` (GL; WARP at the merge): the marks read back from the final raster decode to the frame's counter and
  instant; under blackout the region is black.
- `LatencyStepTests` (the OMT observer): ten steps → p50 and p95 reported; armed → refused by the class; no observer and no
  camera → refused naming both.
- `HeartbeatTests`: shown frames advance with presents, held outputs included; a stopped presenter's do not.
- `CrashPolicyTests`: an unhandled exception in a child host → the journal flushed with the build, a non-zero exit, no in-process
  dump.

**Verified here.** All, with OMT loopback on llvmpipe. **On CI (Windows).** The same. **Manual (lab).** L12: a capture card on an
HDMI loop-through of a real output; an LED processor's monitor out; a photodiode beside the observer for `LATENCY STEP`.

**Next round.** A hardware bypass on loss (LED processors' dual inputs, a relay router) driven by the observer's verdict.

### 3.10 `wp3-present` — Direct3D 12 flip presenters and the predicted display time (W3)

*Design: `design/present.md` Part A (§1–§10).*

**Slice.**
1. The platform and the presenters: `D3D12Platform : IGpuPlatform` (Core's seam; `Render.D3D12/Platform/`, d3d12-core's offscreen
   one) on the outputs' adapter, given the display list (`DisplayList`'s `EnumDisplayMonitors` and `QueryDisplayConfig`, with
   exact refresh rationals); a flip presenter per output — a borderless popup covering its monitor, `FLIP_DISCARD`, the back
   buffers at the display's mode, a frame-latency waitable with `SetMaximumFrameLatency` at the output's frames in flight, the
   refresh an enumerated mode (a request for another refused, naming both rationals), `Present(1, 0)`; `Record` copies the final
   picture into the back buffer in the frame's list; an output whose display hangs off another adapter presented composed, amber
   with its words (`design/d3d12.md` §2); the fuse (a start that never reached its first frames comes back as GL, with words);
   `PRESENT PATH D3D12` routed, AUTO still GL until M1's record, with words; the Engine suites that render run on this platform on
   the Windows job and write their `*.d3d12.json` twins. (9 h)
2. Vblank truth and the evidence: frame statistics into `VblankClock` and `PresentLedger`; Independent Flip as `hint` from DXGI's
   own report (the media statistics' composition mode) with the statistics continuous (each present shown on the refresh after
   its waitable signalled, no composition stall) and as `proven` by `PRESENT PROVE` (N seconds of continuity at the display's own
   rate, no miss, one frame from present to display), in PlayMap's own words (`iflip`, `composed`) with `evidenceWords` naming the
   source — PresentMon's labels only where PresentMon is the source (L2, the ETW stretch); `outputs[].present.mode/evidence/
   evidenceWords`; an output not in Independent Flip amber in preflight with its reason (composed: a window overlaps; the mode's
   refresh is not the display's). (5 h)
3. One queued frame and the predicted display time: frames in flight per output (1 where the output shows a live input, else 2;
   `Output.FramesInFlight`); the render loop waits on the pacing output's waitable, then samples leader time and the newest live
   frames, then records (just in time); a ring of frame blocks (`FrameBlock`, a `record struct` in `Core/Presentation`; frames in
   flight + 1: the frame's number, its target vblank, its predicted display instant, its leader instant and its stage stamps —
   sampled, recorded, submitted, presented, shown); the frame drawn for the leader instant at the vblank it will scan out on plus
   the output's display latency, one instant for every stage; the stamps into the ledger and `LATENCY STEP`'s per-stage shares. (6 h)
4. HDR10 and 10-bit: `Output.ColorBits` 10 → an `R10G10B10A2` swap chain (BT.709, gamma 2.2); `Output.Hdr` hdr10 → `R10G10B10A2`
   with `SetColorSpace1(G2084, P2020)` and the output stage's PQ encode at `NitTarget`; scrgb → `R16G16B16A16_FLOAT`; the encode and
   the 10-bit dither by hunks in the output stage after wp3-d3d12-passes-2 merges; what the display reports
   (`IDXGIOutput6::GetDesc1`) in STATE; refused with words where the display or the swap chain cannot (the hosted runner's virtual
   display among them if M14 says so). (4 h)
5. The wire: a D3D12 flip output reports PMVB path 2 (`dxgi-flip`); STATE `machine.present` keeps every key and type it
   publishes — `api` "d3d12" and `path` "d3d12-flip" are new values, AUTO's words go in `why`; the new per-output keys
   (`evidenceWords`, `framesInFlight`, `colorSpace`, `display`, `refresh`) are additive. (1 h)

⟂ *(≈ 25 h)*

6. *(stretch)* The dedicated-display presenter (specialized displays through `Windows.Devices.Display.Core`) on Enterprise, Pro for
   Workstations and IoT Enterprise, PMVB path 3; refused with the edition's words elsewhere.
7. *(stretch)* The composition swapchain as a timed-present tier, if M3 says it takes D3D12 buffers.
8. *(stretch)* Present facts from the engine's own ETW session (the show user in Performance Log Users, wp3-installer's stretch)
   for `proven` from outside the statistics, PresentMon's labels in a `presentMon` key.

**Preflight.** Present mode per output (amber when not Independent Flip, with why; amber for an output on another adapter); a
refresh mismatch (red). **INDEX.** Section `outputs`: each output's present mode (feedback only).

**Owns.** `src/PlayMap.Render.D3D12/{Present, Platform}/**`; `src/PlayMap.Core/Presentation/**` for W3; `src/PlayMap.Render/{Platform,
Timing}/**` for W3; `src/PlayMap.Engine/Rendering/{RenderHost, OutputWindows, HeadlessPacing}.cs` for W3;
`tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` for W3 (the D3D12 platform's leg); `src/PlayMap.Engine/Presentation/**`;
`src/PlayMap.Engine/EngineCore.Output.cs`; the Output block's rows; `tests/fixtures/state/output.json` and the `*.d3d12.json`
twins; `docs/OUTPUT.md`.

**Hunks.** `src/PlayMap.Render/Engine/Contracts.cs` (`FrameInputs.PredictedDisplayNs`, after d3d12-passes-2);
`src/PlayMap.Render/Passes/OutputStage.cs` and `src/PlayMap.Render.D3D12/Shaders/output.hlsl` (the PQ encode and the 10-bit
dither, after d3d12-passes-2); `src/PlayMap.Engine/Observe/LatencyStep.cs` (the stage shares).

**Depends on.** d3d12-core; stability (the step and the heartbeat).

**Acceptance.**
- `D3D12WarpPresenterTests` (Windows): the platform on WARP; 120 presents on a window; statistics read or the fuse's words; the
  back buffer read back equals the final picture.
- `D3D12EngineHostTests` (Windows): the Engine suites that render, on the D3D12 platform; their `*.d3d12.json` twins written and
  compared.
- `IndependentFlipEvidenceTests` (pure, synthetic statistics): continuity → hint; a composition stall pattern → composed with
  words; `PRESENT PROVE` over 600 continuous samples → proven; one disjoint sample → not proven, counted; an offscreen output →
  refused "no display"; `evidenceWords` names its source.
- `AdapterRuleTests` (a fake display list of two adapters): the second adapter's output presented composed, amber with its words.
- `RefreshMatchTests`: 60/1 asked of a 60000/1001 display → refused naming both.
- `FrameBlockTests` (manual clock): with one frame in flight the predicted display instant = the next vblank + the display
  latency; with two, one period later; a test pass records the leader instant every stage was given — all equal; the ring reused
  without allocation.
- `JustInTimeTests`: with a fake platform the loop samples leader time only after the waitable signals.
- `HdrOutputTests` (WARP): the HDR10 swap chain with its colour space, or, where M14 found the runner's display refuses it, the
  refusal's words; the encode on a pure test — 203 cd/m² white encodes to PQ 0.581 ± 0.001.
- `PresentPathTests`: AUTO → GL with "D3D12 waits for its parity record" in `machine.present.why`; D3D12 on Linux refused
  "Windows only".
- `PmvbCodecTests` unchanged for path 2; `output.json` regenerated with every existing key and type unchanged.

**Verified here.** Evidence maths, the block ring, the just-in-time order, path words, the adapter rule. **On CI (Windows,
WARP).** Bring-up, read-back, HDR swap chains (or their refusal), the Engine suites on D3D12. **Manual (lab).** L1: photodiode
latency against GL on the same machine (M1); L2: Independent Flip proven on NVIDIA, AMD and Intel outputs at each node's most
outputs, PresentMon agreeing; L13: HDR10 on a real display.

**Next round.** AUTO on each tier as its record lands; the ANGLE and D3D11 path deleted; a device per adapter.

### 3.11 `wp3-video` — decode and video I/O on the Direct3D 12 device (W3)

**Slice.**
1. D3D12VA through FFmpeg 9.0.2 (`AV_HWDEVICE_TYPE_D3D12VA`, declared in the pinned binding [map §2.10]): the hardware device
   made on the renderer's `ID3D12Device`; each `AVD3D12VAFrame` (texture, fence, value) waited on by the render queue on the GPU,
   never the CPU, its NV12 or P010 planes sampled in place through plane views by the `yuv` program, the frame held until the
   fence value of the frame that drew it completes; the ladder's words (`decode.sources[].path` `d3d12va-direct`); copy-back where
   the device refuses (M12). (8 h)
2. The upload arena on Direct3D 12: `IUploadArena` over an upload heap (a GPU upload heap where available); HAP blocks and
   CPU-decoded planes copied on the copy queue frames ahead with a fence handshake; the render never waits — a late copy holds the
   last frame, with words, counted. (5 h)
3. Spout through a D3D11 side device: the existing `D3D11SpoutGpu` device opens Spout's legacy share handles and copies into a
   texture the D3D12 renderer shares by NT handle through d3d12-core's exporter, synchronised by a shared fence
   (`OpenSharedFence`, `Signal`, the render queue's `Wait`); sends the reverse; no CPU copy either way (M11). (5 h)
4. NDI and OMT on the copy queue: the send stage's readback and the live inputs' uploads as copy-queue work with fence values.
   (3 h)
5. Decode tiers in the capability record: D3D12 Video's decode profiles per codec (`CheckFeatureSupport(VIDEO_DECODE_SUPPORT)`),
   so `MEDIA INSPECT` and preflight say what this GPU decodes in hardware (4:2:2 HEVC on Blackwell, AV1); NVDEC and AMF recorded as
   probed tiers. (2 h)

⟂ *(≈ 23 h)*

6. *(stretch)* NVDEC through FFmpeg's CUDA path with CUDA ↔ D3D12 external memory, for profiles D3D12 Video does not expose.
7. *(stretch)* The GPU HAP encoder on D3D12 compute (a new encoder version with its PSNR evidence).

**Preflight.** Software decode of a show item the GPU could decode (amber, with the profile); Spout through staging (amber).
**INDEX.** None new.

**Owns.** `src/PlayMap.Media/Hw/**`, `src/PlayMap.Media/Video/Spout/**`, `src/PlayMap.Render.D3D12/Video/**`,
`src/PlayMap.Render/{Video, Hap, Decode}/**`, `src/PlayMap.Engine/Decode/**`, `src/PlayMap.Engine/EngineCore.Decode.cs`,
`src/PlayMap.Core/Media/{GpuSurfaces, UploadArena}.cs`, `docs/DECODE.md`, `docs/VIDEO.md` (its Spout and GPU parts).

**Hunks.** `src/PlayMap.Render/Passes/SendStage.cs` (the copy-queue readback, after d3d12-passes-2).

**Depends on.** d3d12-core.

**Acceptance.**
- `D3d12vaWarpTests` (Windows): the hardware device opens on the renderer's WARP device, or says why (M8).
- `D3d12vaHandoffTests` (WARP, a test "decoder" writing NV12 frames on another queue with a fence): the render waits on the GPU (no
  CPU wait counted), samples in place, releases each frame after its drawing frame's fence; pixels equal the GL path's `yuv`
  result within tolerance.
- `CopyQueueArenaTests` (WARP): HAP frames up on the copy queue; the render thread never waits; a late copy → the last frame held,
  counted, with words.
- `SpoutBridgeTests` (two WARP devices): a chart round-trips byte for byte (BGRA8) both ways; no map or unmap on either side. If
  M8 finds the NuGet WARP and the system WARP do not share, CI covers Direct3D 12 to Direct3D 12 and the D3D11 side moves to L8.
- `PackUnpackTests` byte-exact on Direct3D 12.
- `VideoCapsTests`: a fake `CheckFeatureSupport` → the record's profiles; preflight's software-decode row names what the GPU lacks.

**Verified here.** The logic, against fakes. **On CI (Windows).** As above. **Manual (lab).** L7: D3D12VA on NVIDIA, AMD and Intel
with the pinned FFmpeg (M12); L8: Spout with Resolume and TouchDesigner (M11); L17: NDI with a real runtime.

**Next round.** The D3D11VA and dmabuf importers deleted with GL; NVENC sends from the GPU.

### 3.12 `wp3-desk-gpu` — previews without a CPU copy (W3)

*Design: `design/desk-gpu.md`.*

**Slice.**
1. The engine: a preview view drawn at its displayed size into three textures created shared by d3d12-core's exporter, with a
   `ready` fence the engine signals and a `released` fence the desk signals; their NT handles duplicated into the desk's process
   on `VIEW … SHARED <pid>` (loopback sessions only, and only into the process that owns the session's connection, of the
   engine's own user and session, else refused with words); the ring file kept as the view's control block (slot, fence value, size, stamp), so the desk learns
   the newest slot without a line; the engine never waits on the desk — a slot not yet released is skipped, as the ring skips a
   slot today. (7 h)
2. The desk: `GpuPreviewSource : IPreviewSource` importing each slot once through `ICompositionGpuInterop` (`ImportImage` with
   the texture's NT handle, `ImportSemaphore` with the D3D12 fence where the interop lists that type, M2), drawing into a
   `CompositionDrawingSurface` with its wait value; `IPreviewSource` answering a composition surface or a bitmap; the adapter guard
   (the compositor's LUID against the engine's adapter: different → the ring, with words in the preview's corner); a re-import
   after the desk's device is lost or the engine's device epoch moves. (8 h)
3. The ring kept for remote nodes, another adapter, the GL backend and any import failure, each said in the preview's corner;
   desk-ux's age badge on both paths. (2 h)
4. The desk-kill test: a desk killed mid-import leaves the engine as it was, by facts — no render-thread wait on the desk's fence
   (counted), the present ledger's missed and held counts unchanged, no allocation — and the view's shared textures released when
   its lease ends; the frame times are recorded, not asserted. (3 h)

⟂ *(≈ 20 h)*

5. *(stretch)* The desk's compositor put on the engine's adapter by default (`GraphicsAdapterSelectionCallback`).
6. *(stretch)* Rates from what the desk shows: the focused viewport at the display's rate, thumbnails at 15 Hz, hidden views none.

**Preflight.** None (the desk). **INDEX.** None.

**Owns.** `src/PlayMap.App/**` and `tests/PlayMap.App.Tests/**` for W3; `src/PlayMap.Engine/Rendering/{PreviewViews,
RenderHost.Previews}.cs`, the `Preview` stub; `src/PlayMap.Core/Media/PreviewRing.cs`; `src/PlayMap.Render.D3D12/Share/**` for W3;
`tests/fixtures/state/previews.json` and its cards.

**Hunks.** `src/PlayMap.Render/Engine/RenderEngine.cs` (`Preview` and `CollectPreviews` into shared targets, after
d3d12-passes-2).

**Depends on.** d3d12-core, desk-ux.

**Acceptance.**
- `PreviewShareTests` (Windows, WARP): `VIEW … SHARED <pid>` from a loopback session for a child test process → the handles open
  there on its own WARP device, the chart reads back, fence values grow; another user's or session's pid → refused; a remote
  session → refused "shared previews are local only". If M8 finds WARP's allocations do not duplicate into a child process, the
  case runs on two devices in one process and the cross-process import moves to L3.
- `GpuPreviewSourceTests` (a fake `ICompositionGpuInterop`): an adapter mismatch → the ring with words; no fence type → the ring
  with words; a lost device → one re-import.
- `DeskKillTests` (Windows): the child desk killed mid-import → zero render-thread waits on the desk's fence (counted), the present
  ledger's missed and held counts unchanged, no allocation, the view released at its lease's end; the frame times before and after
  recorded without a ceiling.
- `ViewVerbTests` extended.

**Verified here.** Logic, the ring path, refusals. **On CI (Windows).** Duplication and import into a second WARP device in a
child process. **Manual (lab).** L3: Avalonia's own compositor importing the texture and the fence on NVIDIA, AMD and Intel (M2),
and the preview's latency.

**Next round.** A DirectComposition surface hosted by the desk, if the measured latency calls for it.

### 3.13 `wp3-d3d12-passes-2` — nothing compiled, allocated or waited on during the show (W3, continues d3d12-passes)

*Design: `design/graph.md`.*

**Slice.**
1. Pipelines compiled at arm: the union of every reachable plan's pipeline keys — user effects' included, which their passes
   declare (wp3-shaders-2) — compiled off the render thread on `SHOW ARM` (its `IShowArmHook`) and on `GPU WARM`, ARM waiting for
   them up to the hook's deadline and answering once they are made; a key still missing or failed does not stop the ARM (armed is
   the safe side, and ARM is never refused for a red preflight row): ARM's answer names each one with the compile's words, and
   preflight's `pipelines` row is red; a key missing at air time holds its step (its target keeps its previous picture; a step that
   never drew passes its input through; a source with no input shows black), counted and named, never compiled on the render
   thread; the cache keyed by adapter LUID, driver version and build, warmed again after a driver change with words; the engine's
   per-frame paths pre-rolled before the node says it is ready (the pre-roll in `RenderHost.Plan.cs`, its call a hunk in
   `RenderHost.cs`). (6 h)
2. Bindless records: the stage pass's surfaces as records in a structured buffer (mesh, UVs, content, mask, blend as descriptor
   indices) drawn with a few indirect draws per output — the contract's two additions in `Core/Gpu` (`DrawBuilder.Buffer`,
   `ICommandList.DrawIndirect`) behind `GpuFeatures.Indirect`, which GL reports false and so keeps its draw per surface;
   descriptor indices freed only after their fence values complete; the render thread's work no longer grows with the surface
   count (measured). (8 h)
3. Zero allocation on Direct3D 12: the allocation fence across warm frames for every pixel scene on WARP, modulated layers
   included — their drawn numbers in a per-frame table instead of a drawn project (round 2's remaining gap, R2.8), through named
   hunks on the modulation files. (5 h)
4. GPU time per pass (`render.passes[].gpuUs`, p50 and p95) and the render thread's CPU time per frame in STATE. (2 h)

⟂ *(≈ 21 h)*

5. *(stretch)* Async compute for the particle step with a serial fallback, placed by the compiler, kept only where measured faster.

**Preflight.** Section `gpu`: pipelines compiled for the armed show (red when any is missing, fix `GPU WARM`); steps held at air
time (red, naming the keys). **INDEX.** None.

**Owns.** Its parent's files for W3 except `src/PlayMap.Engine/Rendering/RenderHost.cs` (wp3-present's in W3) and
`src/PlayMap.Render/Effects/**` (wp3-shaders-2's): `src/PlayMap.Core/Graph/**`, `src/PlayMap.Render/{Engine, Passes, Projection,
Led}/**` (`Passes/WatermarkPass.cs` included), `src/PlayMap.Render/Gl/**`, `RenderHost.Plan.cs`, `graph.json`;
`src/PlayMap.Core/Gpu/**` for W3; `src/PlayMap.Render.D3D12/{root, Device, Queues, Memory, Descriptors, Commands, Pipelines,
Shaders, Interop, Bindless}/**` for W3; `tests/PlayMap.Render.Tests/**` for W3 (but the D3D12 tolerance table, wp3-shaders-2's in
W3); `EngineCore.Pipelines.cs`.

**Hunks.** `src/PlayMap.Engine/Rendering/RenderHost.cs` (the pre-roll's call before STATE says `ready`, a few lines, first in W3's
merge order; present rebases on it); `src/PlayMap.Core/Modulation/{ModulationBus, ModulationPlan}.cs` and
`src/PlayMap.Engine/Audio/EngineCore.Mod.cs` (the drawn numbers' table in place of the drawn project; round 2's files, no owner in
W3).

**Depends on.** d3d12-passes, d3d12-core.

**Acceptance.**
- `PipelineWarmTests`: arming compiles every reachable key before ARM answers; a key forced missing → ARM's answer names it,
  `show.armed` true, preflight's row red, at air time its step held and counted with no render-thread compile; a failed key the
  same with the compile's words; a changed driver key → warmed again.
- `BindlessTests` (WARP): 1, 10 and 100 surfaces → the same pixels as the plan without records; draws per output bounded by a
  small constant; no descriptor reused before its fence; on GL `GpuFeatures.Indirect` false and the per-surface draws unchanged.
- `ZeroAllocTests` (WARP and GL): every scene, modulated layers included; `ModulationTests` unchanged.
- `PassTimingTests`; `graph.json` regenerated.

**Verified here.** GL's share of the allocation fence; the warm-up orchestration with a fake cache. **On CI (Windows).** All.
**Manual (lab).** L16: GPU time per pass and render-thread CPU time against surface count on a real GPU; a driver update
re-warming.

**Next round.** Work graphs for GPU-driven content where measured.

### 3.14 `wp3-framelock` — locked where the hardware locks, honest everywhere else (W4)

*Design: `design/present.md` Part B (§11–§18).*

**Entry condition.** M4 measured: L5's run of NVIDIA's barrier sample on the Sync wall, which needs no PlayMap code, booked before
W3 ends on the date the lab inventory gives it (§7, §9 decision 13). If the wall has not answered when W4 starts, the session builds
M4's "no" shape — one swap chain per machine in the barrier, correct whatever the answer — and a client per output waits for the
record as a stretch; the multi-output shape is never built on a hope.

**Slice.**
1. The NVIDIA present-barrier tier on RTX PRO with RTX PRO Sync: `NvAPI_D3D12_QueryPresentBarrierSupport`; a client per joined
   swap chain (`NvAPI_D3D12_CreatePresentBarrierClient`); its back buffers and fence registered
   (`NvAPI_D3D12_RegisterPresentBarrierResources`), again after every `ResizeBuffers` and rebuild; `NvAPI_JoinPresentBarrier` and
   `NvAPI_LeavePresentBarrier`; `NvAPI_QueryPresentBarrierFrameStatistics` read after the presents every frame (sync mode — not
   joined, client, system, cluster — and the present, present-in-sync, flip-in-sync and in-sync refresh counts). `IFrameLockDriver`
   reshaped for a barrier client and moved, with `FrameLockBoard`, `IPresentBarrierTarget` and `BarrierStatistics`, to
   `src/PlayMap.Core/Sync`, split from the board's facts (`IFrameLockBoardSource`, today's NVAPI tier, its declarations unchanged);
   the barrier tier in `Render.D3D12/Sync` with its own declarations of the barrier's calls, each structure pinned by
   `NvapiLayoutTests`. The lock machine's locked states need the barrier's own evidence: `grouped` for system mode, `framelocked`
   (or `house-locked` on house sync) for cluster mode, with in-sync counts advancing; `SoftLocked`, which nothing enters and no
   STATE has published, removed with a wire note, so `IsLocked` and the engine's `PresentSeams.Locked` count only those three.
   M4's answer decides the multi-output shape: a client per output's swap chain if several join; if not, one swap chain per machine
   joins (the pacing output's) and the machine's other outputs report free with their phase against it, `SYNC APPLY` saying so.
   (8 h)
2. The honest software lock: without a board or a barrier, every output is in a free state (`free-measured`, or `free-unknown`
   before its clock settles) with its measured phase against the group's reference ("same frame, phase +6.2 ms ± 0.3 ms, next slip
   in 41 min"); the lock's published keys and words kept (`state`, `words`, `reason`, `fellOut`, `rejoins`), `tier`, `mode`,
   `phaseMs` and `phaseUncertaintyMs` new, and `sync.outputs[].phaseMs`; no free output ever carries a locked state. (3 h)
3. The all-or-nothing barrier governed: an output whose in-sync count stops for three refreshes leaves the barrier and free-runs
   (`fell-out`), its reason in `lock.reason`; the rest of the wall keeps its barrier; no automatic rejoin while armed (round 2's
   rule); the 24-bit counter wrap-aware through `LockCounter`. (4 h)
4. The shared frame count: content time from `NvAPI_D3D1x_QueryFrameCount` where the driver answers it with the barrier joined
   (M10), else from the leader's anchor at the barrier's in-sync refresh count — both behind `LockCounter`'s source. (3 h)
5. The preconditions as facts: each output's timing (pixel clock, totals, refresh rational) in STATE, so a refused lock names its
   cause — timings more than ±50 ppm apart, EDIDs that differ, VRR on (NVAPI on NVIDIA; ADLX's FreeSync state on AMD, a read-only
   probe loaded from the driver's own library, never bundled, in `Platform.Windows/Vendors/`; unknown on Intel, said), not in
   full-screen Independent Flip, an output on a second adapter; members of one group on different backends or GPU drivers while a
   generator or effect is drawn on two members' outputs (amber in preflight, words in `SYNC APPLY`: their floats may round apart
   across a seam); preflight rows. (5 h)

⟂ *(≈ 23 h)*

6. *(stretch)* Synchronized Mosaic: one swap chain across a Mosaic surface, outputs as its viewports — a stretch whatever M4
   answers, since it changes the presenter model.
7. *(stretch)* Warp and blend at scanout (`NvAPI_GPU_SetScanoutWarping`, `SetScanoutIntensity`) as an RTX PRO tier behind the
   shader warp, reported applied only after the state is read back.

**Preflight.** The lock per output (a free output in a group that applied the lock: amber with its phase and reason); timing
mismatches (red); VRR unknown on a vendor (amber); mixed backends or drivers under shared content (amber). **INDEX.** Section
`sync`: the barrier's mode per output (feedback only).

**Owns.** `src/PlayMap.Render/Sync/**`, `src/PlayMap.Render.D3D12/{Sync, Present}/**` for W4 (present is merged),
`src/PlayMap.Render/Platform/Windows/NvApi*.cs`, `src/PlayMap.Core/Sync/**`, `src/PlayMap.Platform.Windows/Vendors/**` for W4,
`src/PlayMap.Engine/Presentation/**` for W4, `src/PlayMap.Engine/Rendering/OutputWindows.cs` for W4 (the Mosaic stretch),
`src/PlayMap.Engine/EngineCore.Sync.cs`, `src/PlayMap.Net/Sync/{VblankReport, SyncBoard}.cs`, `tests/fixtures/state/sync.json`,
`docs/SYNC.md` (its lock part).

**Hunks.** None (the barrier's registration on resize and rebuild sits in the presenter it now owns; stability-2's rebuild hook
lands first).

**Depends on.** present (and stability-2 in merge order); M4 measured, or its "no" shape (the entry condition above).

**Acceptance.**
- `PresentBarrierTierTests` (a fake NVAPI with the six functions scripted: pending until full screen, client, system, cluster): a
  join locks only after cluster mode (`framelocked`) or system mode (`grouped`) with in-sync counts advancing for 120 refreshes;
  pending → "not locked: the barrier is pending (a window overlaps output 2)"; after a `ResizeBuffers` the registered buffers
  equal the swap chain's.
- `NvapiLayoutTests` extended with the barrier's structures against NVIDIA's header.
- `BarrierStallTests`: one output's in-sync count stops → it leaves and free-runs within three refreshes with its reason; the others
  stay locked; armed → no rejoin.
- `SoftwareLockWordsTests`: the fields first — every output without barrier statistics has a free `lock.state` and `IsLocked`
  false; then no locked state's word (`framelocked`, `house-locked`, `grouped`) in any STATE value of a free output (the whole STATE
  scanned); free outputs carry phase words.
- `SingleClientTests`: a three-output node under M4's "no": one swap chain joins, the other two free with their phase, `SYNC APPLY`'s
  words.
- `TimingPreconditionTests`: two outputs 80 ppm apart → `SYNC APPLY` refused naming both rates and the ±50 ppm rule; an output on a
  second adapter refused naming it; two members on different backends with a shared generator → amber with the words.
- `AdlxProbeTests`: a fake ADLX: FreeSync on → the VRR row with its words; no ADLX → "unknown", said.
- `LockCounterTests` unchanged; `RebuiltBarrierTests` (merge-time, with stability-2): after a rehearsed removal the barrier's
  resources are registered again before the first present.

**Verified here.** The fake tier, the words, the counters, the preconditions. **On CI (Windows).** NVAPI loads or says "no NVIDIA
driver"; ADLX loads or says why. **Manual (lab).** L5: a two-machine RTX PRO Sync wall with two outputs each: cluster mode, in-sync
counts, a pulled sync cable, a stalled node, house sync at 59.94 Hz with a ×2 multiply (M4, M10); GeForce and Radeon nodes reporting
free with their phase.

**Next round.** EDID and custom timings owned by the presentation layer; SDI and ST 2110 presenters whose vblank truth comes from
the card; ADLX's display control (FreeSync forced off, 10-bit) where measured.

### 3.15 `wp3-stability-2` — device loss as a rehearsed event (W4, continues stability)

*Design: `design/stability.md` Part B (§8–§13).*

**Slice.**
1. One rebuild path: a removal seen at `Present`, `ExecuteCommandLists`, `GetDeviceRemovedReason` or a fence at `UINT64_MAX` → the
   reason and DRED's breadcrumbs and page-fault data journalled (`GPU INCIDENT`: the last pass completed, the first outstanding,
   the recently freed objects) → every device object released → the start-up path run again (device, queues, presenters, the
   pipeline library, imports, the arena, the plans) → frames resume at "now", content being a function of time; HEALTH's `gpu`
   phase `rebuilding` with its epoch for the supervisor while the path runs; the time to the first good frame in STATE; the same
   path on GL (context loss by robustness where the context offers it, else a fault seam); `RenderHost.FaultLimit`'s stop gone.
   (8 h)
2. Rehearsal: `GPU REHEARSE [REMOVE]` forces a removal (`ID3D12Device5::RemoveDevice`) in rehearsal, refused while armed; CI
   forces removals on WARP (M8) and asserts the facts — the incident row, STATE's removal and reason, the rebuild's steps in the
   start's order, a good frame reached — and records the time to the first good frame without a ceiling (the runner is shared):
   the budget the supervisor's exemption reads, held × 4 by L9 in the lab. (4 h)
3. Repeated loss governed: rebuilds with backoff; three in ten minutes → the GL fallback where it starts (HEALTH's phase
   `starting` while it does), else the outputs held as the show's policy says, with words — never a silent stop; a removal whose breadcrumb sits inside a user effect's pass
   quarantines that kind before the rebuild draws again. (4 h)
4. The degrade order for a missing frame: the next frame drawn for its own instant, never a catch-up → the last presented picture
   held while the render thread stalls, with the stall's cause → an observed stale or lost output red on the attention bar with
   the backup's `FAILOVER` as its fix line (the takeover's automatic trigger stays the backup's next-round item); black only where
   the show's policy says. (3 h)
5. GPU crash evidence: an Aftermath dump on a removal on NVIDIA when its library is present; the dump manifest beside it. (2 h)

⟂ *(≈ 21 h)*

6. *(stretch)* A real TDR through `dxcap -forcetdr` in the lab script, its time to a good frame published.

**Preflight.** Section `gpu`: removals since the last arm (amber with their reasons). **INDEX.** None.

**Owns.** Its parent's files, `EngineCore.Rebuild.cs`, `src/PlayMap.Engine/Rendering/{RenderHost, RenderHost.Plan}.cs` for W4,
`src/PlayMap.Render.D3D12/**` for W4 but `Sync/` and `Present/` (wp3-framelock's), `src/PlayMap.Render/Gl/**` for W4 (context
loss), `src/PlayMap.Core/Gpu/**` for W4, `tests/PlayMap.Render.Tests/**` and `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` for
W4, `tests/fixtures/state/gpu.json` for W4 (`render.removals`).

**Hunks.** `src/PlayMap.Render.D3D12/Present/**` (the presenters made again through their factory on a rebuild; framelock owns
the folder in W4 and merges after).

**Depends on.** present, d3d12-passes-2.

**Acceptance.**
- `DeviceLossRehearsalTests` (WARP): a removal during a running show (rehearsal) → `GPU INCIDENT` with DRED's breadcrumbs naming
  the pass; a good frame reached and its time recorded; STATE `render.removals` 1 with the reason; HEALTH's `gpu` phase
  `rebuilding`, then `running`; the presenters back; media time continuous — the frame after the rebuild shows the instant it
  should.
- `SupervisorRebuildTests` (with wp3-installer's merged `SupervisorCore`): a rehearsed removal under the supervisor's rule: no stop
  while HEALTH says rebuilding within its budget × 4; a rebuild held past it → the stop, with words naming the phase.
- `RebuildPathTests` (a recording fake): the rebuild calls exactly the start-up steps, in the same order.
- `RepeatedLossTests` (fake device): three in ten minutes → GL with words; GL unable to start → held by the policy, with words.
- `QuarantineOnLossTests`: a breadcrumb inside effect slot X → its kind quarantined; the rebuild draws X bypassed.
- `RehearseVerbTests`: armed refused; on GL without robustness refused "the GL context cannot be removed on purpose here".
- `DegradeOrderTests`.

**Verified here.** The policy and the path against fakes. **On CI (Windows, WARP).** Forced removals. **Manual (lab).** L9: a real
TDR on NVIDIA and AMD; a driver update during a rehearsal; an Aftermath dump read in Nsight.

**Next round.** The observer's verdict driving the hardware bypass; a safety monitor kept even simpler than the supervisor.

### 3.16 `wp3-shaders-2` — user effects on the Direct3D 12 device (W3, continues shaders)

*Design: `design/shaders.md` §11.*

**Slice.**
1. User programs on the device: on show change (and on `FX ADD`, `FX SET` of a kind) the engine sends each user kind the plan
   reaches to the shader worker once, keyed by its hash; the reply's DXIL and reflection go to `IGpuDevice.CreateProgramAsync`
   (`ProgramCode.Dxil`), whose `ProgramId` and `ProgramLayout` the effect pass draws through the builder as on GL; a kind not back
   yet draws bypassed with words, counted; user effects' root signature (the default block at `b0`, ISF's built-ins as root
   constants at `b1`, `t0`–`t15`, `s0`–`s3`) and the per-draw descriptor copies into the frame's ring (`Render.D3D12/Effects/`,
   allocation-free). (6 h)
2. ISF on the device as on GL: multi-pass with sizes, persistent buffers (imports in the plan, never aliased), imported pictures,
   `EffectGenerator`, feedback; the FX pixel scenes join `PixelParityTests`, each with its row of the tolerance table. (7 h)
3. Pipelines at arm: the effect pass declares its user programs' keys, so wp3-d3d12-passes-2's warm-up makes them before ARM
   answers. (2 h)
4. The GPU-time guard (the measured cause: a frame whose GPU time passes the output's period misses its vblank): each user slot
   bracketed by timestamps; a slot that is the cause — the frame over the period and under it without the slot's own time — on 30
   consecutive frames is bypassed and quarantined (`QuarantineReason.GpuTime`, wp3-shaders' record) with both numbers in its
   words. (3 h)
5. The ISF corpus on the new road: the Windows job clones the public ISF files at a pinned commit and runs each through the worker
   to DXIL and one draw on WARP; the count recorded in the job's summary and `fx.compile.corpus`, against 324 of 327 on GL; M1's
   AUTO record cites it. (3 h)
6. `FX CHECK` end to end on the Direct3D 12 road (the worker's stages, then the device's make), and `docs/EFFECTS.md`'s Direct3D
   12 part. (1 h)

⟂ *(≈ 22 h)*

7. *(stretch)* spirv_to_dxil behind the worker's stage (wp3-shaders' stretch, if not reached there), compared on the corpus.

**Preflight.** Section `fx`: a kind compiling on the worker (amber), a kind the guard quarantined (red, as wp3-shaders' row).
**INDEX.** None new.

**Owns.** Its parent's files for W3 (`src/PlayMap.Engine/ShaderWorker/**`, the `Shaders` stubs, `src/PlayMap.Core/Effects/{IsfTranslator,
IsfTranslator.Vulkan, ShaderQuarantine}.cs`, the FX block's rows it owns, `shaders.json`), `src/PlayMap.Render/Effects/**` for W3,
`src/PlayMap.Render.D3D12/Effects/**`, `tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` for W3, its named CI block (the
corpus).

**Hunks.** None beyond its files; its user programs' keys reach the warm-up through the plan's declarations.

**Depends on.** shaders, d3d12-core; d3d12-passes-2 before merge (§5.3).

**Acceptance.**
- `UserEffectD3D12Tests` (Windows): the FX pixel scenes on WARP within their rows; multi-pass and persistent buffers; an effect
  generator; a kind still compiling → bypassed with words, then drawn.
- `EffectRootSignatureTests` (Windows): the descriptor copies per draw allocate nothing; no debug-layer message.
- `UserPipelineWarmTests` (Windows, at the merge with d3d12-passes-2): an armed show's user programs made before ARM answers.
- `GpuTimeGuardTests` (both, a fake timer): 29 frames → nothing; 30 → quarantined with both numbers; a frame over the period
  that would be over without the slot → nothing for that slot.
- `IsfCorpusD3D12Tests` (Windows): the pinned corpus to DXIL and one draw each; the count recorded.
- `QuarantineTests` and `ShaderWorkerTests` unchanged.

**Verified here.** The guard's rule, the request's keying, the worker's protocol. **On CI (Windows, WARP).** All. **Manual
(lab).** User effects in L15's per-vendor parity; the guard's numbers on a real GPU in L16.

**Next round.** User HLSL; Shader Model 7's SPIR-V intake when it ships.

---

## 4. Shared files and their hunks

Every file has one owner in each wave (§2.2). A package touching another's file does so only through a hunk named here, landing
in §5.3's order. The files the move edits most are the map's §9 list. A folder not named for a wave keeps no owner in it, and
nothing in that wave edits it.

| File | Owner, by wave | Hunks, in merge order |
|---|---|---|
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | spine (outside blocks) | each package inside its own block only |
| `src/PlayMap.Core/Model/OutputModel.cs` | spine (W0 lands every round-3 field) | none |
| `src/PlayMap.Core/Model/ProjectStore.cs` | spine (W0: `UndoTo` and the steps' list) | none |
| `src/PlayMap.Core/Show/ShowPolicy.cs` | spine (W0: `UNDO TO` as `UNDO`) | none |
| `src/PlayMap.Core/Fleet/HealthRecord.cs` | spine (W0: `ShownFrames`, `ObservedFrames` at −1, the `gpu` record) | none |
| `src/PlayMap.Engine/EngineCore.Fleet.cs` | — (round 2's, finished) | stability (fills the counts, W2) → stability-2 (the `gpu` phase while rebuilding, W4) |
| `Version.props`, `src/PlayMap.Core/BuildInfo.cs` | spine (from the `versioning` branch) | installer (the 255 rule, `MsiVersion`; W1) |
| `src/PlayMap.Core/Gpu/**` | rhi (W1: the contracts, the presenter seams, the program table, user programs, the transient contract) → d3d12-core (W2) → d3d12-passes-2 (W3: indirect draws and structured buffers) → stability-2 (W4) | none |
| `src/PlayMap.Core/Presentation/**` | spine (W0: moved from `Render/Timing` and `Render/Platform`) → present (W3: `FrameBlock`, the evidence) | stability (the ledger's shown-frame count, W2) |
| `src/PlayMap.Core/Machine/**` | spine (W0: the records, `ThreadClaims`) → platform (W1) | none |
| `src/PlayMap.Core/Sync/**` | — (round 2's) → framelock (W4: the driver's new shape) | none |
| `src/PlayMap.Core/Media/GpuSurfaces.cs`, `UploadArena.cs` | spine (W0: `GpuApi.D3D12`) → video (W3) | none |
| `src/PlayMap.Core/Media/PreviewRing.cs` | desk-gpu (W3) | none (desk-ux reads the existing stamp) |
| `src/PlayMap.Core/Effects/IsfTranslator.cs` | shaders (W1) → shaders-2 (W3) | none |
| `src/PlayMap.Core/Modulation/{ModulationBus, ModulationPlan}.cs`, `src/PlayMap.Engine/Audio/EngineCore.Mod.cs` | — (round 2's) | d3d12-passes-2 (the drawn numbers' table, W3) |
| `src/PlayMap.Core/Observe/**`, `src/PlayMap.Engine/Observe/**` | stability (W2) → stability-2 (W4) | present (`Observe/LatencyStep.cs`: the stage shares, W3) |
| `src/PlayMap.Render/Gpu/*` → `Gl/*` | rhi (W1) | none |
| `src/PlayMap.Render/Gl/**` (with `RenderContext.cs` and the GL platforms) | rhi (W1) → d3d12-passes (W2: the GL walk) → d3d12-passes-2 (W3: GL's allocation fence) → stability-2 (W4: context loss) | none |
| `src/PlayMap.Render/Engine/{RenderEngine, FramePasses, Contracts}.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | present (`FrameInputs.PredictedDisplayNs`, W3) → desk-gpu (`Preview`, `CollectPreviews`, W3) |
| `src/PlayMap.Render/Passes/OutputStage.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | stability (the watermark last, W2, before d3d12-passes) → present (the PQ encode and the 10-bit dither, W3) |
| `src/PlayMap.Render/Passes/WatermarkPass.cs` | stability (W2) → d3d12-passes-2 (W3) | d3d12-passes (its `Declare`, W2, after stability) |
| `src/PlayMap.Render/Passes/SendStage.cs` | rhi → d3d12-passes → d3d12-passes-2 | video (the copy-queue readback, W3) |
| `src/PlayMap.Render/Effects/**` | rhi (W1) → d3d12-passes (W2) → shaders-2 (W3) | shaders (`FxChainRunner.cs`: the quarantine's bypass, W1) |
| `src/PlayMap.Render/Platform/**`, `Timing/**` | rhi (W1) → d3d12-core (`GpuPlatform.cs`, W2) → present (W3) | none |
| `src/PlayMap.Render/Platform/Windows/NvApi*.cs` | rhi (W1) → present (W3, with `Platform/**`) → framelock (W4) | none |
| `src/PlayMap.Render/Sync/**` | rhi (W1) → framelock (W4) | none |
| `src/PlayMap.Render.D3D12/**` | spine (W0: the project, empty folders) → shaders (`Shaders/`, W1) → d3d12-core (all of `design/d3d12.md` §0's W2 folders, W2) → W3: present (`Platform/`, `Present/`), video (`Video/`), desk-gpu (`Share/`), shaders-2 (`Effects/`), d3d12-passes-2 (the root, `Device/`, `Queues/`, `Memory/`, `Descriptors/`, `Commands/`, `Pipelines/`, `Shaders/`, `Interop/`, `Bindless/`) → W4: framelock (`Sync/`, `Present/`), stability-2 (every other folder) | present (`Shaders/output.hlsl`: the PQ encode, W3, after d3d12-passes-2) → stability-2 (`Present/`: presenters made again on a rebuild, W4, before framelock) |
| `src/PlayMap.Render.D3D12/PlayMap.Render.D3D12.csproj` | as its root above | one `<ItemGroup Label="wp3-<pkg>">` per package |
| `src/PlayMap.Platform.Windows/**` | spine (W0: the project) → platform (W1) → framelock (`Vendors/`, W4) | none |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | rhi (W1) → d3d12-passes (W2) → present (W3) → stability-2 (W4) | platform (the render thread's claim, W1) → d3d12-passes-2 (the pre-roll's call, W3, first in W3's order) |
| `src/PlayMap.Engine/Rendering/RenderHost.Plan.cs` | spine (W0: the `Plan` stub) → d3d12-passes (W2) → d3d12-passes-2 (W3) → stability-2 (W4) | none |
| `src/PlayMap.Engine/Rendering/HeadlessPacing.cs` | rhi (W1) → present (W3) | platform (the high-resolution timer, W1) |
| `src/PlayMap.Engine/Rendering/OutputWindows.cs` | rhi (W1) → present (W3) → framelock (W4) | none |
| `src/PlayMap.Engine/Rendering/RenderHost.VideoIo.cs` | rhi (W1) → video (W3) | d3d12-passes (`AirEnd.Declare`, W2) |
| `src/PlayMap.Engine/Rendering/{PreviewViews, RenderHost.Previews}.cs` | rhi (W1) → desk-gpu (W3) | none |
| `src/PlayMap.Engine/EngineCore.Output.cs` | d3d12-core (W2) → present (W3) | none |
| `src/PlayMap.Engine/Presentation/**` | present (W3) → framelock (W4) | none |
| `src/PlayMap.Engine/ControlLane.cs`, `src/PlayMap.Engine/Timecode/LtcInput.cs`, `src/PlayMap.Net/Led/LedSender.cs`, `src/PlayMap.Media/Audio/AudioOut.cs`, `src/PlayMap.Media/Sources/FfmpegVideoSource.cs`, `src/PlayMap.Media/Hap/HapWorkers.cs` | — (round 2's) | platform (one `ThreadClaims.Claim` line each, W1) |
| `src/PlayMap.Engine/Fleet/MachineSampler.Windows.cs` | — (round 2's) | platform (reads the identity record, W1) |
| `src/PlayMap.Net/Sync/VblankReport.cs` | spine (W0: path 3) → framelock (W4) | none |
| `src/PlayMap.Media/Hw/**`, `Video/Spout/**` | video (W3) | rhi (the share's type, W1) |
| `src/PlayMap.App/**` | desk-ux (W1) → desk-gpu (W3) | installer (`Link/EngineSupervisor.cs`: attach to a supervised engine, W1) |
| `tests/PlayMap.App.Tests/**` | desk-ux (W1; `Install/**` installer's) → desk-gpu (W3) | none |
| `tests/PlayMap.Render.Tests/**` | rhi (W1) → d3d12-core (W2) → d3d12-passes-2 (W3) → stability-2 (W4); new tests sit in their package's area folder | none |
| `tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` | d3d12-core (W2) → shaders-2 (W3) → stability-2 (W4) | d3d12-passes (its scenes' rows, W2) → d3d12-passes-2, present (their rows, W3) |
| `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` | rhi (W1) → d3d12-passes (W2: the plan) → present (W3: the D3D12 platform's leg) → stability-2 (W4) | none |
| `tests/PlayMap.Engine.Tests/Fleet/BackupRenderTests.cs` | — (round 2's) | stability (a case with the watermark on, W2) |
| `.github/workflows/build.yml` | spine | each package's own named block (§2.2), in merge order: shaders (pinned compiler natives, W1) → installer (the LGPL FFmpeg in both jobs; the MSI, install, upgrade and signing, W1) → d3d12-core (the debug-layer settings, the D3D12 leg's summary, W2) → shaders-2 (the pinned ISF corpus, W3) |
| `THIRD-PARTY-NOTICES.md` | spine (headings) | each package its heading: d3d12-core (TerraFX, the D3D12 Memory Allocator, the Agility SDK; WARP for CI only), shaders (DXC, glslang, SPIRV-Tools, SPIRV-Cross), installer (WiX as a build tool; FFmpeg's LGPL libraries and libvmx now inside the MSI), framelock (ADLX loaded from the driver, never shipped), stability-2 (Aftermath, if shipped) |
| `*.csproj` | the project's owner | one `<ItemGroup Label="wp3-<pkg>">` per package |
| `tests/fixtures/state/{machine, install, shaders, gpu, graph, observe, previews}.json` and their cards | platform, installer, shaders → shaders-2, d3d12-core → stability-2, d3d12-passes → d3d12-passes-2, stability → stability-2, desk-gpu — one writer each per wave | none |
| `tests/fixtures/state/{main, output, decode, sync}.json` | the package whose keys change: present (`output`), video (`decode`), framelock (`sync`) | Linux keeps GL words; the Windows job writes `*.d3d12.json` twins (present's leg) and compares them there |
| `integrations/companion-module-playmap/test/fixtures/*` | the integration pass | none |
| `docs/ARCHITECTURE.md`, `README.md`, the agent brief, `docs/WIRE.md`, `docs/ROADMAP.md`, `docs/COMPETITIVE.md` | the integration pass (§5.6) | none during the waves; packages write papers notes |

---

## 5. Waves, the machine, the merge order and the gate

### 5.1 Waves

| Wave | Packages | Why this grouping |
|---|---|---|
| **Base** | the `versioning` branch merged to `main` | The version every later artifact, journal and dump carries; the installer's rules build on it. |
| **W0** | spine | The rows, contracts, model fields, projects, the seams already known and the WARP and ES legs every later test reads. W1 starts on a green spine. |
| **W1** | rhi, platform, shaders, installer, desk-ux (5) | Each depends on the spine only. rhi alone touches `src/PlayMap.Render` and the render host (beyond the named hunks); the other four own new projects (`Platform.Windows`, `Render.D3D12/Shaders`, `Supervisor`, `installer/`) and the desk. The benches for M1–M3 (the spine's and desk-ux's) run in the lab beside them. |
| **W2** | d3d12-core, d3d12-passes, stability (3) | Each needs the RHI; d3d12-core also the HLSL. The graph compiler and the observer run on GL here; their Direct3D 12 acceptance runs after d3d12-core merges first. |
| **W3** | d3d12-passes-2, shaders-2, present, video, desk-gpu (5) | Each needs the Direct3D 12 device; d3d12-passes-2 also the graph; shaders-2 also the worker; desk-gpu also the desk's age badge; video and desk-gpu build on d3d12-core's merged exporter. |
| **W4** | stability-2, framelock (2) | Each needs the flip presenters; the rebuild path before the barrier that must register again in it. Framelock's entry condition is M4 (§3.14). |

**Scheduling.** Waves name dependency depth, not a barrier: a package starts when every package it depends on is merged and a
slot is free; a review takes the next free slot when its implementation ends. The critical path is spine → rhi → d3d12-core →
present → framelock; rhi is its longest link and the one the round cannot shorten (§6, R1).

### 5.2 The machine and where sessions run

**This machine** (4 cores, 15 GB of memory and no swap, about 14 GB of free disk, no GPU): round 2's rules stand (round 2's
`PLAN.md` §5.2) — at most five sessions at once (four implementations and one review), a build semaphore of 3 and a test
semaphore of 2 for the GPU suites, test hosts under `DOTNET_GCHeapHardLimit` of 1.5 GB, no build while `MemAvailable` is under
2 GB or free disk under 3 GB, one Xvfb per session, a loopback address per builder slot, `LP_NUM_THREADS=1`, one `dotnet build`
at a time per tree, a merged package's worktree removed. The new projects add little: `PlayMap.Render.D3D12` builds its C# here and
its shaders where DXC runs (M6).

**Cloud sessions** run in containers of their own, so they do not count against this machine's five. A cloud session pushes only
its own branch (`wp3-<pkg>`), never `main`; its Windows evidence is its branch's Windows job, read with `gh run view`; the merge
is done here, through §5.4's gate. Per wave, at most:

| Wave | Here | Cloud |
|---|---|---|
| W0 | spine | — |
| W1 | rhi (and reviews) | platform, shaders, installer, desk-ux |
| W2 | d3d12-passes, stability | d3d12-core |
| W3 | d3d12-passes-2, desk-gpu | present, video, shaders-2 |
| W4 | (reviews, the integration pass) | stability-2, framelock |

The packages here are the ones that run the GL pixel suites heavily or own the integration-hot render files; the cloud ones own
new projects or take their evidence from the Windows job. With this split the round runs in about seven rounds of sessions:
W0, two for W1, one each for W2–W4, and the integration pass.

### 5.3 Merge order

| Wave | Order | Why |
|---|---|---|
| W1 | **rhi → shaders → platform → desk-ux → installer** | The RHI changes Render and Core first; shaders' layout tests read its layouts and its `FxChainRunner` hunk follows it; platform's timer and claims hunks follow it; the installer's attach hunk follows desk-ux's desk. |
| W2 | **d3d12-core → stability → d3d12-passes** | The device before anyone's WARP acceptance; the watermark's `OutputStage` hunk before d3d12-passes declares the output stage. |
| W3 | **d3d12-passes-2 → shaders-2 → present → video → desk-gpu** | d3d12-passes-2 owns the frame files the others hunk, lands its pre-roll's call in `RenderHost.cs` before present takes that file, and builds the warm-up shaders-2's user programs join; present's output-stage hunks follow it; video and desk-gpu build on d3d12-core's merged exporter, in either order. |
| W4 | **stability-2 → framelock** | The rebuild path before the barrier registers again in it. |

### 5.4 The gate for every merge

On one integration worktree here, one build at a time:

1. Rebase on `main`; build the whole solution in Release with the analyzers on, the desk, `tools/PlayMap.Node` and the new projects
   included (the WiX project is outside the solution; the Windows job's MSI step builds it).
2. Every test project on Linux to its end (GL on Xvfb in both dialects, FFmpeg 9.0's LGPL libraries, the Companion module's
   `npm test`); the package's
   fixtures regenerated and equal to the committed ones; `RoutesTests`, `StateOrderTests`, `CatalogueTests`, `ContextLinkTests`,
   `ModuleRuleTests`, `RhiFenceTests`; no skipped, disabled or quarantined test.
3. The pushed branch's Windows job green, as in round 2.
4. **From wp3-d3d12-core's merge on, the Windows job's Direct3D 12 leg is a hard gate**: every `Backend=D3D12` test passes on
   WARP with the debug layer on and GPU-based validation as M15 decided (every run, or the merge's changed scenes with a nightly
   full run on `main`), the run's debug-layer message count is zero (an allowed message is named in its test, with why WARP gives
   it), and `PixelParityTests` holds every pixel scene on both backends within its row of the D3D12 tolerance table (the
   user-effect scenes from wp3-shaders-2's merge on) — a new pixel test brings both, and every new or widened row is reviewed here
   with its measured maximum and mean difference and its reason. A red Direct3D 12 leg blocks the merge exactly as a red GL test
   does; a test that is red only sometimes is a defect for the package's review session, never a waiver — and a CI test that
   asserts a time is such a defect (§2.1).
5. **From wp3-installer's merge on, the MSI leg is a hard gate**: the MSI builds, installs, upgrades from the previous build,
   refuses a downgrade and uninstalls on the runner; on `main`, once a signing identity is configured, every signed file verifies
   (before then the step's named notice and the `-unsigned` name).

A red merge is fixed by that package's review session, never by editing another package's test.

### 5.5 Tests added at merge

They need two packages; the second package's review session writes them.

| After | Test |
|---|---|
| rhi, shaders | `ShaderLayoutTests` against rhi's `ProgramLayout`s |
| d3d12-core, stability | `WatermarkRenderTests` on WARP |
| d3d12-core, d3d12-passes | `PlanWalkTests` and `AliasingParityTests` on WARP |
| platform, d3d12-core | the direct queue's priority from `MACHINE SET`; the identity's Agility field filled |
| installer, stability | `SupervisorHeartbeatTests`: the supervisor stops an engine whose shown frames stop while its phase is `running` (a fake engine on loopback), and not while the count is −1 |
| installer, stability-2 | `SupervisorRebuildTests`: a rehearsed removal under the supervisor's rule: no stop within the rebuild's budget × 4 |
| d3d12-passes-2, shaders-2 | `UserPipelineWarmTests`: an armed show's user programs made before ARM answers |
| present, stability | `LatencyStepTests` with each stage's share; `HeartbeatTests` from D3D12 frame statistics where WARP gives them |
| video, d3d12-passes-2 | the send stage's copy-queue readback through the plan; `ZeroAllocTests` with a live input |
| present, desk-gpu | shared previews while flip presenters run (WARP) |
| stability-2, shaders-2 | `QuarantineOnLossTests` (a removal inside a user effect's pass on the Direct3D 12 device) |
| stability-2, framelock | `RebuiltBarrierTests` |

### 5.6 The round's integration pass (after W4)

1. `RoutesTests` flips to **every round-3 row is routed** (true by construction: packages removed the rows they did not build).
2. `RoundThreeEndToEndTests` (Windows, WARP): a show on `PRESENT PATH D3D12` — two outputs on flip presenters, a HAP clip and an
   H.264 clip (the fake D3D12VA decoder), an ISF chain compiled by the worker and drawn on the device, a Spout send and receive
   across two devices, an OMT observer reading output 1, a shared preview in a child process (or two devices in one, as M8
   decided); `SHOW ARM` compiles every pipeline and preflight lists one section per package; a rehearsed removal mid-show
   recovers; no free output is in a locked state.
3. The papers, from each package's note: `docs/ARCHITECTURE.md` §1 (an installed node, not a portable exe), §2 (the module table
   with `Render.D3D12`, `Platform.Windows` and `Supervisor`; Render's row), §5 (the RHI, the graph, Direct3D 12, GL as the
   reference); `docs/OUTPUT.md`, `DECODE.md`, `VIDEO.md`, `EFFECTS.md`, `SYNC.md`; `README.md`'s opening and its module table; the
   agent brief's description and loop (the Windows job, WARP); `docs/WIRE.md`'s round-3 section; `docs/ROADMAP.md`'s statuses —
   the items its round-3 table says the packages move (R0.7, R1.11, R2.1, R2.2, R2.5, R2.8, R2.9, R4.2, R4.5, R4.6 in part, M20,
   M21); `docs/COMPETITIVE.md`; `THIRD-PARTY-NOTICES.md` against the MSI's contents.
4. `LicenceTests` on the MSI's contents: no NDI binary; FFmpeg's hash equals CI's LGPL pin and it reports no `--enable-gpl`; no
   WARP; no AMD runtime (ADLX is the driver's); every notice present.
5. Budgets recorded: the MSI's size, start-up to the first frame on WARP, the pipeline count of the reference show, the warm-frame
   allocation assertion with every pass registered on both backends.
6. The Companion module's fixtures regenerated from the integrated catalogue and STATE.

### 5.7 The spine amendment lane

A seam found missing mid-wave is added by a short spine commit on `main` between merges, never by a package editing a spine file;
until it lands, the package works behind its own interface in its own files.

---

## 6. Risk register

| # | Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|---|
| R1 | The RHI cut touches about fifty files in one session and sits on the critical path | High | High | Contracts first; GL wrapped, not rewritten; caller changes mechanical, names resolved through layouts (ids are stretch); one acceptance — every golden byte-identical; rhi alone owns Render in W1, so an overrun delays only its dependants, which are named | rhi |
| R2 | GL-shaped callers explode into pipeline variants compiled at first use | Medium | High | One `DrawState` per draw mapping to one key; keys enumerated by the plan; render-thread compiles counted in STATE from d3d12-core on; closed by d3d12-passes-2's warm-up at arm | d3d12-core, d3d12-passes-2 |
| R3 | A missing barrier or an early release removes the device on a real GPU ("premature release of objects in flight is one of the top causes" [dir §7]) | High | High | Automatic tracking until the plan takes over; the debug layer and GPU-based validation as a hard gate; aliasing on and off compared; releases deferred to fence values; DRED on in the field | d3d12-core, d3d12-passes |
| R4 | WARP passes but real drivers differ (timing, formats, video) | High | Medium | The lab checklist per vendor (§7, L15 and L20 for pixels on real drivers); AUTO stays GL until M1; every vendor feature probed, never assumed | present, video, framelock |
| R5 | A user shader hangs or faults the GPU under an explicit API | Medium | High | Compiled out of process; SPIR-V validated; the GPU-time guard and the corpus on the new road above wp3-shaders-2's cut line; the kind quarantined; the rebuild path | shaders, shaders-2, stability-2 |
| R6 | GLSL and HLSL drift apart while both live | Medium | Medium | Layout tests per program against one program table; pixel parity per scene on both backends as a hard gate; GL retires next round | shaders, d3d12-core |
| R7 | TerraFX or its memory-allocator port lags the Agility SDK or misses a call | Low | Medium | Raw declarations for any gap (the house's `ComSlots` way); PlayMap's own placed pools; Vortice named as the alternative | d3d12-core |
| R8 | Avalonia does not import a D3D12 fence (M2) | Medium | Low | Measured in W1; the ring stays; the age badge ships either way | desk-gpu |
| R9 | The present barrier refuses several swap chains (M4), or yields no shared frame count (M10) | Medium | High for multi-output walls | M4 measured before W4 (framelock's entry condition); until then, or if not, one swap chain per machine joins and the rest report free with their phase; Mosaic stays a stretch; content time from the anchor at the in-sync refresh | framelock |
| R10 | The Agility runtime does not load from a .NET apphost (M7) | Medium | Low | The OS runtime, named in STATE; newer features probed and refused with words | d3d12-core |
| R11 | Auto-logon and a LocalSystem supervisor widen the attack surface | Medium | High | A standard show account; its password as an LSA secret, never a plain registry value; the supervisor's pipe ACL'd to the show user and administrators; the MSI grants two named privileges to one named account; the engine never elevated; guidance in `docs/INSTALL.md` | installer |
| R12 | Wire and fixture changes break peers (Patterns, Stagewatch, Companion) | Low | Medium | D3D12 flip outputs keep PMVB path 2; new STATE keys additive (`machine.present`'s keys and types, the lock's words and `reason` kept); Windows fixtures as twins; the module's fixtures regenerated at integration; mixed builds in a group named in preflight by their versions | spine, present, framelock, integrator |
| R13 | Platform claims starve the desk or the system (a high-priority process with MMCSS threads) | Low | Medium | Each claim a setting with its read-back; realtime GPU priority off until measured; the lab's p99.9 with and without each | platform |
| R14 | Licences: FFmpeg now inside the MSI; WARP not redistributable; AMD runtimes; Aftermath's terms | Medium | High | The LGPL FFmpeg build only, as replaceable files with notices, the same pin every CI decode test loads; WARP in CI only; AMD runtimes (ADLX among them) never bundled; Aftermath loaded when present until its terms are confirmed; `LicenceTests` on the MSI | installer, integrator |
| R15 | People read "Direct3D 12" as "the default" while AUTO is still GL | Medium | Medium | STATE says why AUTO is GL; the roadmap and papers say it in the present tense; the lab checklist says what remains | integrator |
| R16 | Sixteen sessions; `main` moves meanwhile | Medium | Medium | Packages start as their dependencies merge; cloud sessions beside this machine's five; every package rebases before merge; §9 decision 1 names the deferral order | orchestrator |
| R17 | Add/Remove Programs shows `0.2.17` for 0.2.0 build 17 | High | Low | The product's name carries the SemVer; `docs/INSTALL.md` explains the two numbers | installer |
| R18 | WARP's rasteriser rounds, filters and blends unlike llvmpipe, against tolerances tuned on llvmpipe | High | Medium | A per-scene D3D12 tolerance table filled from the first WARP run, each widening with its measured difference and reason, reviewed at the gate; byte-exact only for the integer `Load` programs; L15 on real drivers | d3d12-core |
| R19 | A shared hosted runner makes a timing assertion red sometimes, blocking a hard gate | Medium | High | CI asserts counts, order and fields and records times (§2.1); pass marks live in the lab | every package |
| R20 | A sync group mixes GL and Direct3D 12 nodes, or drivers, while generated content spans a blend seam | Medium | Medium | AUTO stays GL and D3D12 is chosen per node, so the case is real; amber in preflight and words in `SYNC APPLY` (framelock item 5) | framelock |
| R21 | The supervisor stops an engine during a legitimate stall (a start, a rebuild, a GL fallback) | Medium | High | HEALTH's `gpu` phase exempts a start or a rebuild up to four times its recorded budget; the stale time tied to that measured budget; `SupervisorRebuildTests` at the merge | installer, stability-2 |
| R22 | The lab is not there when its line falls due | Medium | High | The inventory confirmed before W1 (§7, §9 decision 11); a line without its machine reported not run, never passed | owner |

---

## 7. The lab checklist

What the Windows job cannot prove. Each line's script is written by the package named, inside its budget, in `docs/round3/lab/`
before that package merges; a line's result is recorded with the machine, driver and build it ran on. The benches that answer
M1–M3 are the spine's (`bench/r3-present`) and desk-ux's (`bench/r3-avalonia-import`).

**The lab itself.** The owner confirms this inventory and who operates it before W1 (§9 decision 11); a line whose machine is
missing is said in the round's papers as not run, never as passed.

| Machine or rig | Lines | Status |
|---|---|---|
| Two RTX PRO nodes with RTX PRO Sync, four outputs each, a house-sync generator at 59.94 Hz | L1, L2, L5, L6, L9, L10, L14, L15, L21, L22 | to confirm |
| A GeForce RTX node | L1, L2, L4, L8, L16, L17 | to confirm |
| A Radeon RDNA 3 or 4 node | L1, L2, L7, L9, L10, L15, L20 | to confirm |
| An Intel Arc node; a hybrid Intel CPU; a dual-CCD X3D CPU | L1, L2, L7, L10, L15, L20 | to confirm |
| A capture card on an HDMI loop-through; an LED processor with a monitor output; a photodiode rig | L1, L12 | to confirm |
| An HDR10 display | L13 | to confirm |
| A clean Windows 11 IoT Enterprise LTSC 2024 install with auto-logon; a clean Windows 11 Pro for SmartScreen | L11, L18 | to confirm |
| Three operators and a dark room | L19 | to confirm |

| # | Check | Machine | Pass mark | Package |
|---|---|---|---|---|
| L1 | **M1**: photon latency and missed vblanks, `PRESENT PATH GL` against `D3D12`, an hour each, photodiode at the top-centre of the output | an RTX PRO node, a GeForce RTX node, a Radeon node, an Arc node | D3D12's p99 photon latency at or below GL's; its missed vblanks at or below GL's; the record cites the ISF corpus count on the Direct3D 12 road (wp3-shaders-2) | present |
| L2 | Independent Flip proven per output, PresentMon beside STATE, at each node's most outputs at once | each node of L1, every output it drives | PresentMon shows "Hardware: Independent Flip" or "Hardware Composed: Independent Flip" on each output for 10 minutes and STATE says `iflip` with `proven`; the most outputs one GPU holds in Independent Flip recorded | present |
| L3 | **M2**: Avalonia importing the D3D12 texture and fence; the cross-process and cross-API share if M8 sent it here | the desk on each vendor | a preview shown from the shared texture; the fence type listed; the preview's latency recorded | desk-gpu |
| L4 | **M3**: the composition swapchain with D3D12 buffers and a target time | an RTX node | a present at its target time, measured | present |
| L5 | **M4, M10**: the Sync wall — NVIDIA's barrier sample first (M4, before W3 ends), then the engine | two RTX PRO machines with RTX PRO Sync, two outputs each, house sync at 59.94 Hz with a ×2 multiply | several swap chains in one process join, or not (M4's answer); cluster mode with in-sync counts advancing on every joined output; a pulled sync cable frees only that machine, with its reason; the frame count shared, or the anchor's fallback measured; photodiode phase across machines within the card's specification | framelock |
| L6 | **M5**: Reflex on the RTX PRO driver | the RTX PRO node | `NvAPI_D3D_SetSleepMode` answers and its markers report | present |
| L7 | **M12**: D3D12VA through FFmpeg 9.0.2 | NVIDIA, AMD, Intel; H.264, HEVC 10-bit, AV1 at 4K60 | STATE says `d3d12va-direct`; no CPU copy counted | video |
| L8 | **M11**: Spout with Resolume and TouchDesigner; the cross-API bridge if M8 sent it here | an RTX node | both directions without a CPU copy | video |
| L9 | A real TDR (`dxcap -forcetdr`) and a driver update during a rehearsal | NVIDIA, AMD | the first good frame within the CI's recorded time × 4 (the supervisor's rebuild budget); an Aftermath dump read on NVIDIA | stability-2 |
| L10 | The platform's claims | the three vendors' nodes, a hybrid Intel CPU, a dual-CCD X3D CPU | p99.9 frame time with each claim at or below without it; each default (the GPU class, the GC's show mode among them) confirmed or changed with the record; the timer's lateness against today's wait | platform |
| L11 | The installed node: **M9**, auto-logon, Shell Launcher, an upgrade refused while armed | Windows 11 IoT Enterprise LTSC 2024 | the supervisor starts the engine in the show user's session after a cold boot; the armed refusal's words; the longest shown-frame gap outside a rebuild recorded (the supervisor's floor) | installer |
| L12 | The observer and `LATENCY STEP` | a capture card on an HDMI loop-through; an LED processor's monitor output | verdicts match injected faults; stale within two captured frames; the step's glass-to-glass within 1 ms of a photodiode's | stability |
| L13 | HDR10 output | an HDR10 display | PQ codes measured at the display within its tolerance | present |
| L14 | Two 72-hour soaks, AUTO (GL) and `PRESENT PATH D3D12`, two outputs each | the RTX PRO node | zero missed vblanks; no removal; memory flat; the longest shown-frame gap outside a rebuild recorded | integrator |
| L15 | Pixel parity and crash evidence on real drivers | NVIDIA, AMD, Intel nodes | every pixel scene within its tolerance row on each vendor's driver, user effects included; an Aftermath dump of a forced fault read in Nsight on NVIDIA; a Radeon GPU Detective report naming an HLSL line on AMD | d3d12-core |
| L16 | GPU time per pass; render-thread CPU against surface count | an RTX node | each pass's p50 and p95 recorded; with bindless records the render thread's CPU time at 100 surfaces within 10 % of 10 surfaces; the GPU-time guard's numbers on a heavy effect | d3d12-passes, -2 |
| L17 | NDI with a real runtime | an RTX node with the NDI 6 runtime | a send and a receive through the copy queue, no CPU wait counted | video |
| L18 | SmartScreen | a clean Windows 11 Pro | the signed MSI from `main` installs without a SmartScreen block | installer |
| L19 | The formative dark-room session | the desk, three operators, a dark room | each critical task (GO, panic, recovering after a desk restart, finding a faulted output) done without help, or the finding recorded in `docs/UX.md` | desk-ux |
| L20 | The GL backend after the cut, on Windows drivers | NVIDIA, AMD, Intel nodes | the pixel suites within their GL tolerances and an hour's show on AUTO (GL) with no more missed vblanks than `23f162f` on the same machine | rhi |
| L21 | **M13**: the first ten minutes | the RTX PRO node, the MSI's ReadyToRun build | p99.9 frame time over a show's first ten minutes recorded against minutes 10–20 | installer |
| L22 | A precise Windows thread time: a thread's cycles (`QueryThreadCycleTime`) at the time-stamp counter's frequency from the platform, never a rate measured against the stopwatch | the RTX PRO node with Memory Integrity on (Windows as Hyper-V's root partition), then off | a spinning thread's cycles at that frequency within 1% of its running time from ETW's context-switch, interrupt and DPC events, idle and beside a network and disk interrupt load; its ticks (`GetThreadTimes`) under the node's own timer claims (`IGNORE_TIMER_RESOLUTION`, the high-resolution timer) still in the step `ThreadTime.ResolutionNs` states; until it passes, a Windows thread time is its ticks and the survey slice's budget there is proven by the stopwatch alone (docs/FACADE.md) | platform |

---

## 8. Coverage: every line of the direction

| The direction's line | Packages | Round 3 reaches | Proved by |
|---|---|---|---|
| "an installed Windows application" | installer, platform | A signed per-machine MSI (or one named unsigned until §9 decision 2); the engine supervised in the show user's session; machine-level actions done once; the version rules an installer needs | `InstallerTests`, `SupervisorTests`, `MsiVersionTests`, `PreinstallCheckTests`; L11, L18 |
| "direct deep Windows features" | platform, present, installer | QoS and timer claims, MMCSS, process and GPU priority, keep-awake, CPU sets, flip-model presenters with HDR10 and 10-bit, WER dumps, privileges; specialized displays as stretch | `PlatformCallsTests`, `CpuSetTests`, `D3D12WarpPresenterTests`, `HdrOutputTests`; L2, L10, L13 |
| "processor features" | platform, installer | The CPU's vector widths and efficiency classes in the identity; CPU sets placing the critical pool on the highest efficiency class, EcoQoS for background pools, never a hard affinity; ReadyToRun in the MSI's publish. **Below bar this round**: hot kernels benchmarked on the show CPUs; NativeAOT against ReadyToRun (M13) | `CpuSetTests`, `PlatformPolicyTests`, `IdentityTests`; L10, L21 |
| "graphics card features including RTX" | d3d12-core, framelock, video, platform | Aftermath, RTX PRO Sync through the present barrier, NVDEC and 4:2:2 profiles probed, the RTX PRO tier detected; the published support tiers (NVIDIA RTX and RTX PRO tier 1, AMD validated per driver, Intel Arc best effort) in `docs/INSTALL.md` and `machine.identity`. **Below bar this round**: RTX Video and TrueHDR, Optical Flow, scanout warp (stretch), Windows ML with TensorRT for RTX (no inference on the air path yet), DirectStorage (no GPU-native, chunk-compressed frame format yet), Intel VPL (D3D12 Video and FFmpeg decode first) | `PresentBarrierTierTests`, `VideoCapsTests`, `IdentityTests`; L5, L6, L15 |
| "and Radeon" | d3d12-core, platform, video, framelock | DXIL with PDBs for Radeon GPU Detective, the Radeon tier detected and validated per driver, AMF probed, ADLX's FreeSync and colour-depth state read (read only, from the driver's own library) for the lock's preconditions, the honest software lock (AMD has no framelock). **Below bar this round**: ADLX display control (FreeSync forced off, 10-bit forced); AGS (its Direct3D 12 extensions need it to create the device, behind the device factory next round); Anti-Lag 2 (PlayMap's own just-in-time pacing does its job, and never two pacers on one output) | `SoftwareLockWordsTests`, `AdlxProbeTests`, `IdentityTests`; L1, L7, L15 |
| "Portability is not an issue — ability" | D1–D4, d3d12-core, video, present, shaders-2 | Windows-only nodes; Direct3D 12 with HDR10, 10-bit, GPU upload heaps, D3D12VA, bindless records, user effects on the device | §1.6 item 2 |
| "ultra-low latency" | present, d3d12-passes-2, stability | One queued frame, the waitable, just-in-time sampling, one predicted display time per frame, nothing compiled on the render thread, `LATENCY STEP` measuring it | `FrameBlockTests`, `JustInTimeTests`, `PipelineWarmTests`, `LatencyStepTests`; L1, L12 |
| "reliability" | stability, stability-2, installer, d3d12-core, shaders-2 | The independent observer, the shown-frame heartbeat, the supervisor that knows a rebuild from a freeze, DRED, one rehearsed rebuild path, quarantine, the GPU-time guard | `ObserverLoopbackTests`, `DeviceLossRehearsalTests`, `SupervisorHeartbeatTests`, `SupervisorRebuildTests`, `GpuTimeGuardTests`; L9, L14 |
| "flexibility" | rhi, platform | Two backends behind one interface; every vendor feature probed with a fallback that keeps the show correct | `RhiFenceTests`, `PixelParityTests` |
| "Add version numbers to builds" | `versioning` (base), installer | One source; SemVer, file version and MSI version each in its reader's rules; every MSI build ordered | `BuildIdentityTests` and `BuildStampTests` (on `main`), `MsiVersionTests` |
| "latest gaming code … for speed" | d3d12-passes, d3d12-passes-2, present, d3d12-core | A render graph compiled on show change, pipelines at arm keyed by driver, bindless records, zero allocation, copy queues and timeline fences, frames in flight per output | `GraphCompilerTests`, `BindlessTests`, `ZeroAllocTests` |
| "medical code … for stability" | stability, stability-2, platform | A refresh index in the picture read by an independent observer; a heartbeat that is the shown-frame count; device loss rehearsed; driver sets locked like a SOUP list; the air hazard file as stretch | `ObserverVerdictTests`, `HeartbeatTests`, `IdentityTests` |
| "UX design … user interface" | desk-ux, desk-gpu | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status by shape and word, preview age, previews without a CPU copy | `GoGuardTests`, `BlindLookTests`, `StatusRuleTests`, `PreviewShareTests`; L3, L19 |

---

## 9. Open decisions

1. **The size of the round.** As planned, sixteen sessions. If it must be smaller, defer whole packages in this order and mark
   their §8 rows "Below bar this round": desk-gpu (the ring stays), framelock (the board stays as it is, joins refused), video's
   Spout bridge and copy-queue arena (D3D12 decode by copy-back). Never rhi, d3d12-core, d3d12-passes, d3d12-passes-2, present,
   shaders-2, stability, stability-2, installer or platform: they carry the direction's own lines, and without shaders-2 a show
   with effects cannot run on Direct3D 12.
2. **Signing identity.** Azure Artifact Signing takes organisations in the USA, Canada, the EU and the UK, and individuals in the
   USA and Canada only. The owner's eligibility decides it; until then builds are unsigned and named so, and the signing step skips
   with a named notice (wp3-installer item 5).
3. **WiX's Open Source Maintenance Fee** applies to organisations above $10 000 of annual revenue; the plan assumes it is paid if
   it applies.
4. **FFmpeg inside the MSI.** The pinned Windows build must be the LGPL variant; the installer package switches both CI jobs to it
   on its first day and confirms it.
5. **AUTO's switch** per vendor tier needs the owner's sign-off on L1's record.
6. **Show nodes without the desk.** Recommended: show nodes run the engine only (Shell Launcher on IoT Enterprise) with the desk on
   another machine; a one-machine show stays supported.
7. **Aftermath's library** shipped in the MSI, or loaded where the user puts it, by its terms.
8. **Realtime GPU priority** stays off until L10 measures it.
9. **The air hazard file** (stability's stretch 7) as a standing rule for the air's code: each hazard traced to a fault-injection
   test, borrowing medical risk practice as a design tool, not as a claim of compliance.
10. **`main` has moved** past `23f162f`, the map's base, with the desk's merge and the version scheme among its commits. Neither
    touches the renderer, the render host or Media; the version scheme adds the build to the web port's MCP server info and a
    stamp check after each CI job's build (`tools/ci-stamp.ps1`), and the desk's `PreviewSource.cs` keeps the same copy path [map
    header]. The spine rebases at start; no conflict is expected outside the desk's preview files, which desk-gpu owns in W3.
11. **The lab.** The inventory of §7 and who operates it, confirmed before W1; a line without its machine is reported not run.
12. **The platform's defaults.** As planned, `PROCESS HIGH`, HighQoS, MMCSS, CPU sets, EcoQoS and keep-awake are on by their
    documented causes, and the GPU scheduling class and the GC's show mode are off until L10 measures them. The owner may prefer every
    claim off until L10, or every claim on from the start; either is one line in `design/platform.md` §2.
13. **M4's date.** The lab books NVIDIA's barrier sample on the Sync wall before W3 ends. If it has not run when W4 starts, framelock
    builds the one-swap-chain shape, which is correct whatever the answer, and a client per output waits for the record.
