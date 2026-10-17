# PlayMap round 3: the plan

*How PlayMap will become an installed, Windows-native show node: Direct3D 12 behind a rendering hardware interface (RHI) cut at
today's `GlDevice`, presented through DXGI's flip model with the evidence for every claim, locked between machines only where
the hardware locks, supervised and installed per machine, and numbered so an installer can order every build. The round is a
spine in one session, then fourteen packages in four waves; each package is one implementation session followed by one
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
pipeline compiled before the show is armed. Decode will stay on the GPU, previews will reach the desk without a CPU copy, and a
lost device will be rebuilt by the same path that started it, rehearsed in CI. An independent observer will read back what
the audience saw. Outputs will lock across machines on RTX PRO Sync and say "free, phase +x ms" everywhere else. OpenGL stays the
reference backend through the round and keeps the Linux development loop alive. It retires in the next round, once the pixel
and timing evidence says the two backends agree.

What exists today, and what the move changes, is the map's subject; its twelve findings [map §1] set the shape of this plan.
The three that shape it most: GL calls sit in ten files but GL types reach fifty of the renderer's eighty-nine [map §2.1];
there is no declarative render graph, only an ordered frame with five pass hooks [map §2.3]; and device loss stops the drawing,
with no rebuild path [map §3.1].

### 1.2 Decisions taken

| # | Decision | What it means in the tree | Why | What would reopen it |
|---|---|---|---|---|
| D1 | **Direct3D 12 is the primary renderer, behind an RHI.** | The RHI's contracts live in Core (`PlayMap.Core/Gpu`); the GL backend moves under `src/PlayMap.Render/Gl/`; the Direct3D 12 backend is a new edge, `src/PlayMap.Render.D3D12`, referencing Core and its native libraries only. Passes, generators and the frame code use the RHI and name neither API. | New GPU features reach Windows through D3D12 first; it sits on DXGI's flip model and NVIDIA's present barrier; it is the currency of D3D12 Video, Spout's D3D11 bridge and the desk's GPU import; only D3D12 has PIX, DRED, Aftermath, Radeon GPU Detective and GPU-less CI on WARP [dir §1]. | Nothing in the evidence; a lab finding that D3D12 is slower than GL on a vendor tier delays AUTO there (D2), not the decision. |
| D2 | **OpenGL stays the reference backend until pixel and timing parity, then retires.** | GL 3.3 core / ES 3.00 stays behind the RHI with unchanged pixels. `PRESENT PATH D3D12` is explicit this round; AUTO stays GL until a lab parity record exists for a vendor tier (§1.5, M1), and then becomes D3D12 on that tier in one commit that cites the record. GL, ANGLE and the D3D11 path are deleted next round, each with the tests that test only it. | Parity must be measured before a backend is retired ("measured, then governed"); no source compares D3D12, Vulkan and GL latency on show hardware [dir §1]. | A tier whose parity never arrives keeps GL there, with words. |
| D3 | **Vulkan is not carried.** | No Vulkan backend. The user-shader front end goes through SPIR-V (D7), so a Vulkan backend would stay cheap to add. | Vulkan's Windows presentation depends on each vendor; Spout, D3D12 Video and the desk's import need a DirectX bridge anyway; present timing is new and NVIDIA-first [dir §1]. | Linux render nodes as a goal again. |
| D4 | **Windows 11 24H2 (build 26100) or later; Windows 11 IoT Enterprise LTSC 2024 recommended for show nodes.** | The installer refuses an older build with words. Show nodes: IoT Enterprise LTSC 2024 (build 26100, supported to 10 Oct 2034, Shell Launcher and specialized displays available). Desk and programming machines: Pro or Enterprise 24H2/25H2. Windows 10 is not supported. Linux keeps the development loop and CI's GL suites, not shipped nodes. | 24H2 clears every feature floor the round uses (GPU upload heaps at 26080+, D3D12 AV1 encode); the Agility SDK keeps D3D12 current on a frozen LTSC; Windows 10 ended on 14 Oct 2025 [dir §5]. | A shipped feature needing a later build. |
| D5 | **Hardware lock only on NVIDIA RTX PRO with RTX PRO Sync; an honest software lock everywhere else.** | The frame-lock tier joins outputs through NVAPI's D3D12 present barrier and reports its sync mode and in-sync counts as the lock's evidence. AMD, GeForce and any node without a board present free and report measured phase ("same frame, phase +6.2 ms ± 0.3 ms"); STATE never says "locked" without hardware evidence. | AMD withdrew framelock in 23.Q1; GeForce never had it; network time aligns which frame is drawn, not when it scans out [dir §2]. | A vendor shipping a lock with a D3D12 entry point. |
| D6 | **A per-machine MSI; the engine runs in an auto-logged-on show user's session under a supervising service.** | WiX v7 builds one per-machine MSI. A small LocalSystem service (`PlayMap.Supervisor`) starts `PlayMap.exe engine` in the show user's session, restarts it, and stops one whose shown-frame heartbeat has stopped. The engine never runs as administrator and never in session 0; the installer does every machine-level action once (firewall, dump settings, privileges). | Session 0 cannot drive displays; MSIX virtualises the machine-level writes the node needs; Velopack's MSI adds no services [dir §5]. | None expected. |
| D7 | **One HLSL source for the product shaders; user ISF out of process.** | The 40 product programs are written once in HLSL and compiled by DXC at build time (the GLSL stays for the GL backend until it retires). User ISF goes ISF → GLSL 4.50 → glslang relaxed (`-R`) → SPIR-V → validated → SPIRV-Cross → HLSL → DXC → DXIL in a `PlayMap shader-worker` process, so no compiler loads into the engine; spirv_to_dxil stays a measured alternative behind the same stage. A shader that crashed the GPU is quarantined. | DXC's PDBs map a GPU crash to a source line in PIX and RGD; incorrectly written GLSL crashes explicit-API renderers [dir §1]. SPIRV-Cross and DXC ship as packages today; spirv_to_dxil has no package (to be built from Mesa). | spirv_to_dxil passing more of the ISF corpus than SPIRV-Cross (shaders' stretch measures it). |
| D8 | **TerraFX for Direct3D 12 and DXGI.** | `TerraFX.Interop.Windows` (raw structs and function-pointer vtables, generated from the Windows SDK and Agility headers) in `PlayMap.Render.D3D12` only; its port of the D3D12 Memory Allocator for memory, or PlayMap's own placed-resource pools if that port lags the Agility SDK. NVAPI stays hand-declared, as today. | The hot path allocates nothing; raw bindings keep that true and match the house's vtable approach (`ComSlots`) without hand-writing the D3D12 surface, which is far larger than the 31 methods `ComSlots` declares [map §2.7]. A wrapper binding allocates managed objects per interface. | A binding gap that blocks a must; Vortice.Windows is the named alternative. |
| D9 | **The desk keeps Avalonia,** with GPU shared-texture previews and the consoles' safety patterns. | Previews move from four CPU copies to a shared texture and fence per view where the desk's compositor shares the engine's adapter; the CPU ring stays for every other case. | Avalonia 12.1 is current and imports D3D11 NT-handle textures and D3D12 fence handles; WinUI 3 would rewrite every view for one asset [dir §9]. | Avalonia's backend not importing either at run time (§1.5, M2): the ring stays, and only the GPU path waits. |
| D10 | **What the round refuses.** | DLSS frame generation (synthesised frames under their own pacing contradict content as a pure function of show time); adaptive resolution (it breaks blend seams); VRR on any output in a lock group or feeding genlocked processors; shipping changed TDR registry keys (long GPU work is split instead); `GC.Collect` at show arm (banned in `src/`; a case for it needs a measured cause and a test). | [dir §2, §4, §5, §7, §8] | A measured cause and a deterministic test. |

### 1.3 Versions: one source, every format its reader needs

The `versioning` branch will land on `main` before the spine starts (§5.1). It holds the version in one place
(`Version.props`, `PlayMapVersion` 0.2.x), stamps every assembly and the exe, has `BuildInfo` read it back, shows it on the desk
and in STATE, and names the CI artifact `PlayMap-<FileVersion>-win-x64`. Round 3 will keep that scheme and add the installer's
rules (wp3-installer, item 1).

| Reader | Format | Example (0.2.0, CI build 17, commit `abc1234`) | Rule |
|---|---|---|---|
| People, the wire's hello, STATE `build`, MCP, the journal header, every crash manifest | SemVer 2 with build metadata | `0.2.0+17.abc1234` (local: `0.2.0-dev+abc1234`) | The commit maps a dump to its source; build metadata never orders versions |
| Windows file version (Explorer, WER) | four parts, each ≤ 65 535 | `0.2.0.17` | The branch refuses a build number past 65 535 and every part past 65 535 |
| **MSI `ProductVersion`** | `MAJOR.MINOR.BUILD`, major and minor ≤ 255, build ≤ 65 535 | `0.2.17` | Windows Installer reads three fields and ignores a fourth: under the branch's four-part scheme every CI build of 0.2.0 would read as 0.2.0 [map §6.3]. The MSI takes the build number as its third field, so every numbered build upgrades the one before it |
| Assembly version | binding only | `0.2.0.0` | As the branch has it |

The rules the installer adds:

1. **Major and minor at most 255** for every build, not only for an MSI: `Version.props` will refuse more, so no version can be
   set that a release cannot install.
2. **An MSI is made only from a numbered build.** A local build refuses to make one, with words; its MSI would read as
   `0.2.0` and could never be upgraded in order.
3. **The build number orders installs within a MAJOR.MINOR.** It is CI's run number, which only grows within one workflow
   file. Anything that starts the numbering again (a new workflow file) must raise MINOR in the same commit; the release job
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

The Linux job stays the development loop and the gate for everything that is not Direct3D. The Windows job (`windows-latest`, no
GPU) gains Direct3D 12 on WARP from NuGet (feature level 12_2, Shader Model 6.9 [dir §1]) with the Agility SDK's debug layer, and
becomes the hard gate for every Direct3D 12 line from wp3-d3d12-core's merge on (§5.4). WARP's NuGet binaries are CI's only; they
are never in the MSI.

| Evidence | Linux job (llvmpipe, Xvfb) | Windows job (WARP) | Lab |
|---|---|---|---|
| Core, Net, Devices, Vision, Assistant, App (headless), Media's CPU and fake-runtime tests, the Companion module | ✓ | ✓ | — |
| The GL backend: 213 pixel tests, the GL render host, ES dialect tests | ✓ | ✓ (Mesa WGL) | — |
| The RHI's own tests: fences, layouts, draw states, the module fence | ✓ | ✓ | — |
| The render graph compiler, the frame block ring, evidence maths, observer verdicts, platform policy, supervisor policy, the version maths | ✓ (pure) | ✓ | — |
| Direct3D 12: device, debug layer and GPU-based validation with no message, every pixel scene within the GL tolerances, aliasing on and off, zero warm-frame allocation | — | ✓ | real GPUs |
| HLSL through DXC, reflection against the program layouts | to be measured (M6) | ✓ | — |
| User ISF to SPIR-V (glslang, spirv-val, SPIRV-Cross) | ✓ | ✓ (and on to DXIL) | — |
| D3D12 flip presenters: bring-up, 120 presents, read-back, HDR swap chains | — | ✓ (statistics may be absent on WARP) | real vblank |
| Independent Flip proven, PresentMon agreeing | — | — | ✓ |
| Present barrier, RTX PRO Sync | fake NVAPI | fake NVAPI, layout goldens | ✓ |
| D3D12VA decode handoff, the copy-queue arena, Spout through a D3D11 side device | — | ✓ (fake decoder, two WARP devices) | NVIDIA, AMD, Intel; Resolume, TouchDesigner |
| Forced device removal and the time to the first good frame | — | ✓ (if WARP honours a forced removal, M8) | a real TDR |
| Shared previews: handles duplicated into a second process and read there | — | ✓ | Avalonia's own compositor |
| `PlayMap.Platform.Windows` | policy | every call answers and reads back | p99.9 frame time with and without each claim |
| MSI build, install, upgrade, downgrade refused, uninstall, service, firewall, dump settings | — | ✓ (the runner is elevated) | IoT Enterprise LTSC 2024, auto-logon |
| Signing | — | ✓ on `main` | SmartScreen |

The development loop runs on Linux, so every Direct3D 12 slice keeps its logic pure where it can be and its Direct3D calls thin,
and reads its pushed branch's Windows job (`gh run view`) before it says it is done.

### 1.5 Measure first

The research could not settle these. Each has a cheap measurement, a deadline (the package whose slice it changes), and a plan for
both answers, so no slice waits on a hope.

| # | Question | Measured how | Deadline | If yes | If no (and until it is measured) |
|---|---|---|---|---|---|
| M1 | **Is D3D12 as fast as GL, through the present ledger, on show hardware?** | `bench/r3-present` in W1 (a flashing quad through D3D12 flip, the existing GL window and the D3D11 path on one display, photodiode at a fixed screen position plus PresentMon); then the engine itself once wp3-present merges: the same show on `PRESENT PATH GL` and `D3D12`, an hour each, ledger and photodiode | AUTO's switch, after W3 | AUTO becomes D3D12 on that vendor tier (NVIDIA RTX, RTX PRO, Radeon, Arc) in one commit citing the record | AUTO stays GL on that tier, with words in STATE naming the record; D3D12 stays explicit |
| M2 | **Does Avalonia's Windows backend import a D3D12 shared texture and a D3D12 fence at run time?** | A bench app in W1 on a real GPU: `ICompositionGpuInterop.SupportedImageHandleTypes` and `SupportedSemaphoreTypes`, an import, a frame shown | wp3-desk-gpu's slice (W3) | The fence path as designed | Only the texture is shared and the desk polls the ring's control block for completion, or the ring stays; the age badge ships either way |
| M3 | **Does the Windows 11 composition swapchain take D3D12 buffers and present at a target time?** | A bench in W1 | wp3-present's stretch 7 | A timed-present tier for outputs whose cadence is not the display's | Not built; flip presenters with the predicted display time are the path |
| M4 | **Does the present barrier join several swap chains in one process on a Sync wall?** | The lab's two-machine RTX PRO Sync wall, two outputs each, nvpro-samples' barrier sample then the engine | wp3-framelock's slice (W4) | A client per output's swap chain, as designed | Multi-output nodes lock through one swap chain over a Synchronized Mosaic surface (framelock's stretch 6 becomes its must) |
| M5 | **Does Reflex work on RTX PRO drivers, and with the present barrier?** | The lab's RTX PRO node: `NvAPI_D3D_SetSleepMode` answers and its latency markers | after W3 | An optional low-latency tier on outputs that are not in a barrier | PlayMap's own just-in-time pacing (waitable, then sample, then record) is the only one; never both on one output until measured |
| M6 | Does DXC on Linux emit validated, signed DXIL? | The shaders package's first day | wp3-shaders | HLSL compiles on both jobs | HLSL compiles on the Windows job only; the Linux build embeds none and a Linux test holds the HLSL program set to the library's names |
| M7 | Does a .NET apphost load the Agility SDK's runtime (no `D3D12SDKVersion` export) through `ID3D12SDKConfiguration1::CreateDeviceFactory`? | wp3-d3d12-core's first day, on the runner | wp3-d3d12-core | The MSI places `D3D12Core.dll` beside the exe and STATE names the runtime loaded | The OS runtime, named in STATE; features that need a newer runtime are probed and refused with words |
| M8 | Does WARP honour `ID3D12Device5::RemoveDevice`, and does it decode video? | wp3-d3d12-core's first day | wp3-stability-2, wp3-video | Removal and D3D12VA open run in CI | Removal through a fault seam in CI and a real one in the lab; D3D12VA's handoff tested with a fake decoder |
| M9 | Can the supervisor start the engine into the logged-on session (`WTSQueryUserToken`, `CreateProcessAsUser`)? | wp3-installer, on a lab machine with auto-logon | wp3-installer | As designed | A scheduled task at the show user's logon starts the engine; the service only watches and stops |
| M10 | Does `NvAPI_D3D1x_QueryFrameCount` give every node the same count with the barrier joined? | With M4 | wp3-framelock | `LockCounter` reads it | Content time from the leader's anchor at the barrier's in-sync refresh count |
| M11 | Do Spout's legacy share handles open on the D3D11 side device beside a D3D12 renderer? | wp3-video, with Resolume and TouchDesigner | wp3-video | As designed | Spout through the side device's CPU staging, said in STATE |
| M12 | Does FFmpeg 9.0.2's D3D12VA hand frames over on NVIDIA, AMD and Intel? | wp3-video, in the lab | wp3-video | Zero-copy D3D12VA | D3D11VA on a side device, copied once on the GPU, or copy-back, with the ladder's words |
| M13 | ReadyToRun and a pre-roll, or NativeAOT, for the engine's first ten minutes? | p99.9 frame time over the first ten minutes of a show on one lab machine | next round | — | ReadyToRun and a pre-roll (wp3-d3d12-passes-2 item 1) |

M1–M5 are the four results the direction note says decide the schedule, plus Reflex; M6–M13 are what the map found the tree
needs answered.

### 1.6 Done means, for the round

Round 3 is done when:

1. Every package's must is merged to `main` with both CI jobs green and the analyzers on, under §2.1's per-package rule.
2. On the Windows job, on WARP: the Direct3D 12 backend draws every scene of the pixel suites through the compiled render graph
   within the GL tolerances (the byte-exact ones byte-exact), with the debug layer and GPU-based validation on and no message;
   aliasing on and off draw the same pixels; warm frames allocate nothing; every pipeline the armed show can reach is compiled
   before ARM answers; a forced device removal is rebuilt by the start-up path to a good frame within its recorded budget; the
   MSI builds, installs, upgrades from the previous build, refuses a downgrade and uninstalls; `main`'s artifact is signed and
   named with its version.
3. On Windows with `PRESENT PATH D3D12`, a show runs end to end on the Direct3D 12 backend: outputs on flip presenters, decode by
   D3D12VA or the copy-queue arena, Spout through the side device, previews shared with the desk where its compositor allows,
   the observer reading an output back. AUTO is still GL until M1's record, and STATE says why.
4. On Linux the GL backend draws every golden byte for byte as it did at `23f162f`, and every suite that needs no Direct3D is
   green.
5. No claim without evidence: every output says its present mode and how it is known; no output without hardware evidence is
   called "locked"; every platform claim says what it asked and what Windows answered; every shared preview says which path it
   took.
6. The lab checklist (§7) is written with a script and a pass mark for every manual line, M1–M5 are measured or pending with a
   date, and the papers (the integration pass, §5.6) describe what shipped in the present tense.

---

## 2. Ownership rules

### 2.1 The capacity rule

| Rule | What it means |
|---|---|
| **One session, one slice** | A package is built by one implementation session, then one review-and-fix session (fixes only, no new scope). |
| **The budget** | Above its cut line (⟂) a slice is about 20–26 hours of work, after what the spine already lands. Below the line, stretch items total at most about 8 hours. |
| **Order is the contract** | Slice items are built in the listed order. The must above ⟂ merges on its own. A stretch item starts only when the must is green; an unfinished stretch item is not merged half-built: it moves to "Next round" and its catalogue rows are trimmed. |
| **Done means (a package)** | Merged to `main` with both CI jobs green and the analyzers on; its acceptance tests pass (here, on the Windows job, or marked manual with the lab line that will check it); its fixture `tests/fixtures/state/<pkg>.json` is written by one of its own tests, with a card descriptor `<pkg>.cards.json` for each new STATE block (the desk renders it, and its actions become the object's right-click entries); its catalogue block holds only rows it routes; it registers at least one `IPreflightCheck` with a fix line, tested by injecting a fault, where it owns a show resource; it contributes an INDEX section where it adds an entity a controller can act on; `docs/<AREA>.md` and `docs/wire/<pkg>.md` describe what shipped, with a papers note for the integration pass. |
| **The doctrine** | Attempts are not facts (sent → accepted → applied → observed; a verb that would change nothing is refused with the reason; tests assert fields and published STATE, never words alone). Content is a pure function of show time. One writer. The desk has no private API. Fail closed on the wire. Measured, then governed. |
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
- at most one `<ItemGroup Label="wp3-<pkg>">` at the end of any `.csproj` it needs a reference in (one hunk per project).

**Continuations.** A continuation (wp3-d3d12-passes-2, wp3-stability-2) owns, for its wave, every file its parent owned (the
parent is merged and finished) plus the files named in its section; it keeps the parent's hook word, catalogue block and fixture.
Its review session checks the parent's tests still pass unchanged.

**Ownership by wave.** A file has one owner in each wave. When a package is merged, the files it owned pass to the package §4
names for the next wave. No two packages of one wave own the same file; a package touching another's file does so only through a
hunk §4 names, landing in §5.3's merge order, at most about ten lines.

**The spine's files** after W0 (changed only through the spine amendment lane, §5.7): `Core/Model/*` (the round-3 fields land in
W0), `Core/Wire/{VerbCatalog (outside blocks), VerbSyntax, ShowClass}.cs`, `Core/BuildInfo.cs` (with the installer's hunk),
`Engine/{EngineCore, EngineCore.Router, EngineCore.Modules, StateWriter, ContextBuilder, NodeHome, Seams, Seams.Round2,
Seams.Round3}.cs`, `Engine/Host/{EngineParts, EngineHost, EngineEntry}.cs`, `Version.props` (with the installer's hunk),
`Directory.Build.props`, `BannedSymbols.txt`, `.editorconfig`, `PlayMap.sln`, `.github/workflows/build.yml` (each package's CI
steps are hunks, §4).

### 2.3 Hook words

| Word | Package | Stubs |
|---|---|---|
| `Machine` | wp3-platform | `EngineCore.Machine.cs`, `EngineServices.Machine.cs`, `NodeSettings.Machine.cs` |
| `Gpu` | wp3-d3d12-core (wp3-d3d12-passes-2 adds `EngineCore.Pipelines.cs`, wp3-stability-2 `EngineCore.Rebuild.cs`) | `EngineCore.Gpu.cs`, `RenderHost.Gpu.cs`, `NodeSettings.Gpu.cs` |
| `Observe` | wp3-stability | `EngineCore.Observe.cs`, `EngineServices.Observe.cs`, `Host/EngineParts.Observe.cs`, `Host/EngineEntry.Observer.cs` |
| `Shaders` | wp3-shaders | `Host/EngineEntry.ShaderWorker.cs`, `EngineServices.Shaders.cs` |
| `Undo` | wp3-desk-ux | `EngineCore.Undo.cs` |
| `Preview` | wp3-desk-gpu | `RenderHost.Preview.cs` (beside the existing `RenderHost.Previews.cs`) |

Present and framelock keep the existing Output and Sync files (`EngineCore.Output.cs`, `EngineCore.Sync.cs`,
`Presentation/*`), owned by them in their waves.

---

## 3. The packages

### 3.0 Overview

"Where" says where the session will run: **here** (this machine, under §5.2's limits) or **cloud** (its own container; its
Windows evidence comes from the pushed branch's Windows job either way). Hours are the slice's must above ⟂.

| # | Branch | Hook word | Wave | Depends on | Where | h | Round-3 headline |
|---|---|---|---|---|---|---|---|
| 0 | `wp3-spine` | — | W0 | `main` with `versioning` merged | here | 16 | Round-3 rows, Core contracts, model fields, the new projects, the WARP leg in CI, stubs |
| 1 | `wp3-rhi` | — | W1 | spine | here | 25 | The RHI in Core; GL behind it with unchanged pixels; one test fixture for every backend |
| 2 | `wp3-platform` | Machine | W1 | spine | cloud | 26 | `PlayMap.Platform.Windows`: QoS, timers, MMCSS, process and GPU priority, keep-awake; the node's identity and capabilities; machine preflight |
| 3 | `wp3-shaders` | Shaders | W1 | spine (rhi before merge) | cloud | 25 | The 40 programs in HLSL through DXC; the shader worker for user ISF; quarantine |
| 4 | `wp3-installer` | — | W1 | spine | cloud | 24 | The MSI version rules, the WiX MSI, machine-level actions, the supervisor service, signing |
| 5 | `wp3-desk-ux` | Undo | W1 | spine | cloud | 21 | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status rules, preview age |
| 6 | `wp3-d3d12-core` | Gpu | W2 | rhi, shaders | cloud | 25 | Device, queues, fences, memory, descriptors, DRED and Aftermath, the pipeline cache; every pixel scene on WARP |
| 7 | `wp3-d3d12-passes` | — | W2 | rhi (d3d12-core before merge) | here | 24 | Every pass declares what it reads and writes; the render graph compiled on show change; both backends walk the plan |
| 8 | `wp3-stability` | Observe | W2 | rhi | here | 22 | The watermark, the independent observer, the shown-frame heartbeat, `LATENCY STEP`, the crash policy |
| 9 | `wp3-present` | (Output) | W3 | d3d12-core, stability | cloud | 23 | D3D12 flip presenters, Independent Flip evidence, one queued frame, the predicted display time, HDR10 and 10-bit |
| 10 | `wp3-video` | (Decode) | W3 | d3d12-core | cloud | 24 | D3D12VA zero-copy, the copy-queue arena, Spout on a D3D11 side device, NDI and OMT on the copy queue, decode tiers |
| 11 | `wp3-desk-gpu` | Preview | W3 | d3d12-core, desk-ux | here | 20 | Shared-texture previews with a fence, the adapter guard, the ring kept, the desk-kill test |
| 12 | `wp3-d3d12-passes-2` | (Gpu) | W3 | d3d12-passes, d3d12-core | here | 21 | Pipelines compiled at arm and keyed by driver, bindless records, zero allocation on D3D12, GPU time per pass |
| 13 | `wp3-framelock` | (Sync) | W4 | present | cloud | 21 | The present barrier on RTX PRO Sync, the honest software lock, the governed fall-out, timing preconditions |
| 14 | `wp3-stability-2` | (Gpu) | W4 | present, d3d12-passes-2 | cloud | 21 | One rebuild path, rehearsed device removal, repeated loss governed, the degrade order, GPU crash evidence |

Fifteen sessions, about 340 hours above the cut lines. The designs that go beyond this file: `design/rhi.md` (wp3-rhi),
`design/d3d12.md` (wp3-d3d12-core), `design/graph.md` (wp3-d3d12-passes and -2), `design/shaders.md`, `design/present.md`
(wp3-present and wp3-framelock), `design/platform.md`, `design/desk-gpu.md`, `design/stability.md` (wp3-stability and -2) and
`design/installer.md`. Video and desk-ux are specified here in full.

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
3. Core contracts two or more packages share: `GpuApi.D3D12`; `PresentPath.D3D12Flip` and its word (`d3d12-flip` in STATE; on the
   wire a D3D12 flip presenter reports PMVB path 2, `dxgi-flip`, the presentation model it shares with the D3D11 path, so mixed
   builds in a group read each other); the PMVB reader learns path 3 (`dedicated`, wp3-present's stretch) and still refuses 4
   and above; `NodeIdentity`, `AdapterIdentity`, `CapabilitySnapshot` and `CapabilityProbe` records (Core/Machine); the engine
   seams of `Seams.Round3.cs` (`IMachinePlatform`, `IAirObserver`, `IGpuLifecycle`, `IPreviewShare`) with null implementations
   that say why. (4 h)
4. The model's round-3 fields, backward compatible (init properties with defaults): `Output.ColorBits` (8 | 10), `Output.Hdr`
   (off | hdr10 | scrgb) and `Output.NitTarget`, `Output.FramesInFlight` (0 = auto, 1, 2), `Output.Watermark` (null, or place
   and size); `ModelCompatTests` read every round-2 fixture project unchanged. (2 h)
5. The tree: `src/PlayMap.Render.D3D12` (references Core; TerraFX; every type `[SupportedOSPlatform("windows")]`),
   `src/PlayMap.Platform.Windows` (references Core), `src/PlayMap.Supervisor` (references Core), their test projects, the
   solution's entries; `ModuleRuleTests` (each new project references Core and its own native packages only; Render.D3D12 does not
   reference Render; only App references Avalonia); the stubs of §2.3, each empty body holding `// wp3-<pkg> fills this.`; empty
   fixtures `machine.json`, `gpu.json`, `observe.json` with their card descriptors; `docs/wire/<pkg>.md` generated from the
   catalogue; one heading per package in `THIRD-PARTY-NOTICES.md`. (4 h)
6. CI: the Windows job restores `Microsoft.Direct3D.WARP` and `Microsoft.Direct3D.D3D12` (the Agility SDK, with its debug layer)
   and copies them beside the test hosts; a trait `Backend=D3D12` on Direct3D 12 tests so the job lists their results apart;
   the job's summary names the version built. (2 h)

⟂ *(≈ 16 h)*

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
| `FX RELEASE` | `FX RELEASE <kind>\|ALL` | shaders | High | Content |
| `UNDO LIST` | `UNDO LIST [<n 1-100>]` | desk-ux | Low | Query |
| `UNDO TO` | `UNDO TO <step>` | desk-ux | Review | Project |
| `VIEW` (extended) | `… [SHARED <pid>]` | desk-gpu | as `VIEW` | as `VIEW` |

Rows whose replies or words grow, without a new row: `OUTPUT STATS` (mode, evidence), `SYNC STATUS` (barrier, phase), `HEALTH`
(shown and observed frames), `FX CHECK` (the Direct3D 12 path's stages), `DECODE STATUS` (D3D12VA), `PREFLIGHT` (every package's
rows).

**Acceptance.** `CatalogueTests` (every round-3 row: syntax parses, risk and class set, `SecretFrom` null, every alternative
passes `CommandGate.Check`; no `RiskWhen` lowers a Heavy, Air or Layout line); `RoutesTests` (round-3 rows unrouted answer "not
available on this node yet"); `ModelCompatTests`; `ModuleRuleTests`; `PmvbCodecTests` (path 3 round-trips; path 4 is refused);
`StateOrderTests` (the new top-level keys in their place, empty); `WindowsPlatformTests` (the new projects' types carry the
attribute); **every existing test passes unchanged**; the desk builds.

**Verified here.** All. **On CI (Windows).** WARP and the Agility SDK restored beside the hosts; a one-line Direct3D 12 test
creates a WARP device and says its feature level. **Manual.** None.

### 3.2 `wp3-rhi` — the cut at `GlDevice`, with GL behind it (W1)

*Design: `design/rhi.md`.*

**Slice.**
1. The contracts in `src/PlayMap.Core/Gpu/`: the format, filter, wrap, blend, depth, cull and topology vocabulary (moved from
   `Render/Gpu/GpuTypes.cs`, same names and values); `IGpuDevice`, `ITexture`, `IRenderTarget`, `IGpuBuffer`; `ICommandList`
   with a `DrawBuilder` (a `ref struct` that sets a program's constants by name through its layout, binds textures by slot and
   draws); `PipelineKey` (program, blend, depth, cull, topology, target formats) and `DrawState`; `ProgramLayout` (each
   program's constants with type and offset, its texture slots); `FenceValue` (a queue's timeline and value); `IUploadRing`,
   `IReadbackRing`, `IGpuTimers`, `ITextureImporter`, `ITextureExporter`; `IPipelineCache` (look-up that never compiles, warm-up
   off the render thread). (6 h)
2. The GL backend under `src/PlayMap.Render/Gl/`: `GlDevice` and its partials, `GlDialect`, `RenderContext`, the compile lane and
   the HAP encoder's GL use moved there; `GlBackend : IGpuDevice` with an immediate `GlCommandList`; fences as values over GL sync
   objects; the pixel-pack and unpack rings behind `IReadbackRing` and `IUploadRing`; timer queries behind `IGpuTimers`; the
   dmabuf and ANGLE importers behind `ITextureImporter`. (6 h)
3. The callers: every Render file of bands 2–3 [map §2.1] through the RHI — `RenderEngine`, the passes, generators, video, LED,
   sends, overlay, HAP, effects, projection; uniforms set by name through the program's layout, allocation-free; loose state
   replaced by one `DrawState` per draw; `IGpuPlatform.Device` in place of `Context`; `IOutputPresenter.Record(list, picture)`
   then `Present()` in place of `Present(GpuTexture)`; the engine's two `GlDevice` files; `PassFrame.Device` an `IGpuDevice`.
   (9 h)
4. The tests: `BackendFixture` in place of `GlFixture` (the GL backend on a dedicated thread today; a backend list a later
   package extends), `Pixels.Read(IGpuDevice, IRenderTarget)`, the 213 `gl` tests and 15 files that name `GlDevice` moved onto it;
   no golden file edited. (4 h)

⟂ *(≈ 25 h)*

5. *(stretch)* Constant ids generated per program (`Ids.Output.uAir`) in place of names in the five hottest passes.
6. *(stretch)* Each pass lists the `PipelineKey`s it can draw (wp3-d3d12-passes-2 reads the lists).

**Preflight.** None: rhi adds no show resource and no STATE block (every fixture is unchanged). **INDEX.** None.

**Owns.** `src/PlayMap.Core/Gpu/**`; `src/PlayMap.Render/**` for W1 (`Gl/**` new; `Gpu/**` emptied into `Gl/` and Core);
`src/PlayMap.Engine/Rendering/**` for W1; `tests/PlayMap.Render.Tests/**`; `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs`
and the other rigs' platform lines.

**Hunks.** `src/PlayMap.Media/Hw/HwDecode.cs` (the share's type, if `IGpuShare` moves; at most a using line).

**Depends on.** The spine.

**Acceptance.**
- **Every existing Render and Engine test passes with its golden files unedited** — the pixel goldens byte for byte on
  llvmpipe here and on Mesa WGL on the Windows job.
- `RhiFenceTests`: no type of `Silk.NET.OpenGL` and no `Gl*` type of the backend is named outside `src/PlayMap.Render/Gl/**`;
  Core's `Gpu` namespace references no Render type; `PassFrame` carries no GL type.
- `DrawStateTests`: every `DrawState` maps to exactly one `PipelineKey`; after each draw of a scripted frame the GL state read back
  (blend, depth, cull, polygon mode, bound program) equals what the old calls set, call by call.
- `ProgramLayoutTests`: every program's layout lists exactly the uniforms and samplers its GLSL declares, by name and type; no
  two programs share a name.
- `FenceValueTests`: values grow per queue; `IsComplete` is monotonic; a fence ring of 64 wraps without losing a value.
- `AllocationTests` unchanged: a warm frame allocates nothing.
- `RenderStateFixtureTests`: every STATE fixture regenerated byte for byte.

**Verified here.** All. **On CI (Windows).** The same suites on Mesa WGL. **Manual.** None.

**Next round.** The GL backend deleted with its tests once M1's record holds on every tier (GL-only tests leave with it, by name,
in that change); constant ids everywhere.

### 3.3 `wp3-platform` — what a show node claims from Windows (W1)

*Design: `design/platform.md`.*

**Slice.**
1. `PlayMap.Platform.Windows` (`LibraryImport` throughout): the process claims — `HIGH_PRIORITY_CLASS` (never realtime);
   power-throttling opt-out (`SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED` and
   `IGNORE_TIMER_RESOLUTION` in the control mask and a zero state mask: HighQoS and every timer request honoured); the GPU
   scheduling class (`D3DKMTSetProcessSchedulingPriorityClass` HIGH); each read back where Windows can be asked. The thread
   claims by role: render and present join MMCSS "Games", audio and timecode "Pro Audio" (`AvSetMmThreadCharacteristics`, reverted
   on dispose); decode at above normal; background pools at EcoQoS (`SetThreadInformation(ThreadPowerThrottling)`); every claim
   answering what it asked and what Windows said. (8 h)
2. Time: a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) for every deadline wait on the engine's
   deadline threads — the headless pacer first (today `AutoResetEvent.WaitOne` at the default timer resolution [map §3.1]); QPC
   for every stamp, as today. (3 h)
3. Keep-awake: `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` while an output is open;
   the screen saver, which that call does not stop, read as a fact; the GC's `SustainedLowLatency` while the show is armed. (2 h)
4. The node's identity and capabilities: OS edition, build and revision (LTSC and IoT Enterprise named), the CPU (model, cores
   by efficiency class, the vector widths .NET accelerates), every DXGI adapter by LUID with vendor, name and driver version, its
   vendor tier (RTX PRO by NVAPI's workstation-feature probe, RTX, Radeon, Arc, WARP), HAGS, the power plan, the timer
   resolution honoured; the FFmpeg build, the sync board's firmware and the Agility runtime filled by their owners through the
   same record; STATE `machine.identity`, `machine.capabilities`, `machine.platform`; the context's machine item; `MACHINE`,
   `MACHINE SET` (a setting already at its value is refused with words; REALTIME refused without the privilege, naming the
   privilege and that the installer grants it), `MACHINE LOCK` (keeps this machine's driver set as the show's). (8 h)
5. Machine preflight, every row a fact with words, never a silent change: the OS below build 26100 (red), the power plan, USB
   selective suspend, PCIe link-state power management, sleep and display timeouts, the screen saver, notifications, a pending
   reboot, the Windows Update deferral policy, HAGS, each claim of items 1–3 that Windows refused, and drift from `MACHINE LOCK`'s
   set (amber, naming the old and new versions, fix `MACHINE LOCK`). (5 h)

⟂ *(≈ 26 h)*

6. *(stretch)* CPU sets: the critical pool (render, present, decode) on the highest efficiency class, background pools elsewhere,
   by `SetThreadSelectedCpuSets` (soft; never a hard affinity); the dual-CCD option off by default.
7. *(stretch)* A GC pause histogram (EventPipe) in STATE beside the frame time.

**Preflight.** Item 5's rows, section `machine`. **INDEX.** Section `machine`: the claims' lights (feedback only).

**Owns.** `src/PlayMap.Platform.Windows/**`, `src/PlayMap.Core/Machine/**`, the `Machine` stubs, `tests/PlayMap.Platform.Windows.Tests/**`,
`docs/PLATFORM.md`, `docs/wire/machine.md`.

**Hunks.** `src/PlayMap.Engine/Rendering/HeadlessPacing.cs` (the timer, after rhi); `RenderHost.cs` and the LED, audio and
timecode threads' start (one `Claim(role)` line each, after rhi); `src/PlayMap.Engine/Fleet/MachineSampler.Windows.cs` (reads
the identity record).

**Depends on.** The spine.

**Acceptance.**
- `PlatformPolicyTests` (pure, both jobs): the role → claims table; a fake topology of 8 performance and 16 efficiency cores →
  the critical pool on the 8; a refused claim keeps its words; a setting at its value refused.
- `PlatformCallsTests` (Windows): every claim answers; the power-throttling state read back with `GetProcessInformation` equals
  what was set; an MMCSS handle is taken and reverted on dispose; 1 000 waits of 1 ms on the high-resolution timer — p99 recorded,
  a 4× ceiling asserted.
- `IdentityTests`: the record's keys and types from a fake platform; `machine.json` written; a locked set against a changed GPU
  driver → an amber row naming both versions with the fix line `MACHINE LOCK`.
- `MachineVerbTests`: `MACHINE SET GPU-PRIORITY REALTIME` without the privilege → refused naming it; armed → refused by the class;
  `MACHINE SET QOS HIGH` when it is → refused "already high".
- `PreflightMachineTests`: each row fails on an injected fake fact and passes once fixed; every fix line parses.
- On Linux the null platform's words ("the platform seam is Windows only") in STATE and preflight.

**Verified here.** Policy, verbs, STATE, preflight, against fakes. **On CI (Windows).** Every call answers and reads back.
**Manual (lab).** p99.9 frame time with and without each claim on an NVIDIA, an AMD and an Intel machine — the measured cause for
each default; a hybrid Intel CPU's placement; a dual-CCD X3D CPU.

**Next round.** Core reservation, large pages for frame pools, a pre-roll measured against NativeAOT (M13).

### 3.4 `wp3-shaders` — one HLSL source; user ISF out of process (W1)

*Design: `design/shaders.md`.*

**Slice.**
1. The 40 product programs [map §2.4] in HLSL under `src/PlayMap.Render.D3D12/Shaders/`, one file per family, with the house's
   conventions (targets top row first, row-vector matrices, UV v down, geometry from `SV_VertexID`, exact reads with `Load`), one
   root signature for all (the program's constants in `b0`, sixteen texture slots, four static samplers); compiled by DXC at
   build time to DXIL with PDBs and reflection, embedded in the assembly (on the Windows job; on Linux too if M6 says yes). (12 h)
2. `ShaderLayoutTests`: each program's DXC reflection (constant names, types, offsets; texture slots) equals its
   `ProgramLayout`; before rhi merges, against a parse of the GLSL declarations. The integer-exact programs (`video.pack`,
   `hapenc.*`) are marked for wp3-d3d12-core's byte-exact parity tests. (2 h)
3. The shader worker, `PlayMap shader-worker` (its own process, started and watched by the engine as the optimise worker is, one
   JSON object a line): ISF → `IsfTranslator`'s new target (GLSL 4.50, explicit in/out, set/binding for images, ISF inputs as
   loose uniforms) → glslang relaxed (`-R`) → SPIR-V → `spirv-val` → SPIRV-Cross → HLSL (SM 6.0) → DXC → validated DXIL and its
   reflection; each failure in that stage's words, with the line mapped back through `#line` to the ISF source; natives pinned by
   hash, loaded by the worker only — the engine never loads a compiler; a source that stops the worker three times fails with
   words. (8 h)
4. Quarantine: `ShaderQuarantine` (Core: a kind's hash → reason, when, build; kept in the node's home), a quarantined kind's slots
   drawn bypassed with words, STATE `fx.quarantine`, a red preflight row with `FX RELEASE <kind>` and `FX REMOVE <slot>`;
   `FX RELEASE` (High; refused armed; a kind not quarantined refused). The record is filled by wp3-d3d12-core's GPU-time guard and
   wp3-stability-2's removal breadcrumbs; this package lands it, the bypass and the verb. `FX CHECK` reports the new path's
   stages. (3 h)

⟂ *(≈ 25 h)*

5. *(stretch)* spirv_to_dxil behind the same stage (built from pinned Mesa source), compared with SPIRV-Cross on the corpus.
6. *(stretch)* The opt-in ISF corpus through the new path, its count recorded against today's 324 of 327.

**Preflight.** Quarantined kinds (red, section `fx`). **INDEX.** None new.

**Owns.** `src/PlayMap.Render.D3D12/Shaders/**`, `src/PlayMap.Engine/ShaderWorker/**`, the `Shaders` stubs,
`src/PlayMap.Core/Effects/{IsfTranslator.Vulkan, ShaderQuarantine}.cs`, the FX block's `FX CHECK` words and `FX RELEASE`.

**Hunks.** `src/PlayMap.Render/Effects/FxChainRunner.cs` (bypass a quarantined kind, after rhi); `src/PlayMap.Core/Effects/
IsfTranslator.cs` (the target switch).

**Depends on.** The spine; rhi before merge (the layouts).

**Acceptance.**
- `HlslBuildTests` (Windows): every program compiles to validated DXIL with a PDB; the set equals `ShaderLibrary.Names`.
- `ShaderLayoutTests` (Windows; the GLSL side on both).
- `IsfVulkanTargetTests` (both): the built-in ISF files and the test corpus translate, compile under glslang `-R` and pass
  `spirv-val`; a translator error keeps its ISF line.
- `ShaderWorkerTests`: a good source → DXIL and a layout; a syntax error → the stage and the ISF line; a fake compiler that aborts
  → the worker restarts with backoff, the third stop fails the kind with words; the engine's process holds no compiler module.
- `QuarantineTests`: a quarantined kind's slot draws its input unchanged (± 0 codes on GL); STATE and preflight carry it; `FX
  RELEASE` armed refused, on a free kind refused, else the slot draws again.

**Verified here.** Translator, glslang, spirv-val and SPIRV-Cross (their Linux natives), the worker's protocol and supervision,
quarantine. **On CI (Windows).** DXC, reflection, the worker end to end to DXIL. **Manual.** None (pixel parity is
wp3-d3d12-core's).

**Next round.** The GLSL library deleted with GL; Shader Model 7's SPIR-V intake when it ships; user HLSL.

### 3.5 `wp3-installer` — the MSI, the supervisor and the version rules (W1)

*Design: `design/installer.md`.*

**Slice.**
1. The version rules (§1.3): `Version.props` refuses a major or minor above 255; `MsiVersion` = MAJOR.MINOR.BUILD from numbered
   builds only (a local build refuses to make an MSI, with words); `BuildInfo.MsiVersion`; the release job's check that the new
   MSI version is greater than the last release's. (3 h)
2. The MSI (WiX v7 SDK project `installer/PlayMap.Installer.wixproj`, the EULA accepted by property in CI): per machine into
   Program Files\PlayMap; the self-contained publish harvested; FFmpeg's LGPL shared libraries and libvmx beside the exe (today
   neither is in the folder [map §6.2]); the Agility SDK runtime under `D3D12\` (its debug layer left out of releases); the shader
   worker's natives; a launch condition of Windows 11 build 26100 or later on x64 (a registry search, not `VersionNT`);
   `MajorUpgrade` with no same-version upgrade and a downgrade refused with words; the product's name carrying the SemVer. (7 h)
3. Machine-level actions, once, so the engine never runs as administrator: firewall rules for `PlayMap.exe` (TCP 9940, 9941; UDP
   9928, 9942, 9943, 6454, 5568, 5353); no URL ACL (Kestrel binds its own sockets — asserted by a standard user binding 9940);
   mDNS checked on the runner (`_playmap._tcp` browsable beside Windows' own responder on 5353; if the two collide, the node
   registers through `DnsServiceRegister` instead, behind the platform seam);
   WER LocalDumps for `PlayMap.exe` (`%ProgramData%\PlayMap\dumps`, full, five kept, the show user's write ACL); the show
   account granted `SeIncreaseBasePriorityPrivilege` (the realtime GPU queue, used only when measured) by the supervisor's
   `grant` command as a deferred custom action; the Event Log source. (5 h)
4. The supervisor (`PlayMap.Supervisor`, a LocalSystem service with recovery actions on crashes and on non-zero stops): starts
   `PlayMap.exe engine` in the auto-logged-on show user's session (`WTSQueryUserToken`, `CreateProcessAsUser`; M9), restarts it
   with backoff (1 s doubling to 30 s), stops an engine whose shown-frame heartbeat (HEALTH over loopback, wp3-stability) has not
   advanced for 3 s and journals why, stops trying after five restarts in ten minutes and says so in a status file the engine,
   the desk and the fleet read, and writes a manifest (build, commit, last journal sequence) beside each new WER dump; the desk
   attaches to a supervised engine instead of starting its own; the MSI's `preinstall-check` refuses an upgrade or an uninstall
   while the node's show is armed, with the node's words. (7 h)
5. Signing: Azure Artifact Signing on `main`'s builds (the exe, PlayMap's own DLLs, the MSI), checked by
   `Get-AuthenticodeSignature` in the job; branch builds unsigned and named so. (2 h)

⟂ *(≈ 24 h)*

6. *(stretch)* The opt-in show-machine feature: Windows Update deferral (feature updates 365 days, quality 30, no automatic
   restart with a user logged on, active hours), notifications off, `DisableUserPresenceQos` — written once by the MSI and
   reported by preflight whether written or not.
7. *(stretch)* A per-build SBOM as a CI artifact.

**Preflight.** Section `install`: not installed (a folder run, amber on a show node), the supervisor's state (restarts, stopped),
auto-logon on and for which user, the show machine's policies. **INDEX.** None.

**Owns.** `installer/**`, `src/PlayMap.Supervisor/**`, `docs/INSTALL.md` (the guidance: IoT Enterprise LTSC 2024 for show nodes,
auto-logon with the LSA secret and never a plain-text registry password, Shell Launcher, BIOS power-on after loss, the
update window), the CI's installer and signing steps.

**Hunks.** `Version.props` (the 255 rule); `src/PlayMap.Core/BuildInfo.cs` (`MsiVersion`); `src/PlayMap.App/Link/
EngineSupervisor.cs` (attach to a supervised engine; after desk-ux); `.github/workflows/build.yml` (the MSI, install, upgrade and
signing steps).

**Depends on.** The spine (the `versioning` base).

**Acceptance.**
- `MsiVersionTests` (both): (0.2.0, build 17) → 0.2.17; (0.2.3, 18) → 0.2.18, greater than 0.2.17; a major or minor of 256
  refused; build 0 refused for an MSI; (0.2.0, 1) after a released 0.2.500 → refused "lower than the last release: raise MINOR".
- `VersionPropsTests`: a build with `PlayMapVersion` 256.0.0 fails with the words.
- `InstallerTests` (Windows, elevated): the MSI builds; a quiet install lays the files, registers the service with its recovery
  actions, the firewall rules and the WER values; the previous build's MSI (built in the job with a lower build number) upgrades to
  this one and keeps the node's settings; a downgrade is refused with words; an uninstall removes all but the node's data; with a
  fake engine on loopback answering armed, the install fails with the node's words.
- `SupervisorTests` (pure, both): the backoff schedule, the heartbeat rule on a manual clock, the five-in-ten-minutes stop, the
  dump manifest; on Windows the service installs, starts and stops on the runner.
- `SigningTests` (`main` only): every signed file verifies.

**Verified here.** Version maths, supervisor policy. **On CI (Windows).** The MSI's life cycle, the service, the firewall and
WER settings, signing on `main`. **Manual (lab).** IoT Enterprise LTSC 2024 with auto-logon; the supervisor starting the engine in
session 1 (M9); Shell Launcher; an upgrade during a rehearsal; SmartScreen.

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
   list and the journal) and `UNDO TO <step>` (several steps as one action, refused with LockGuard's words across a lock and by the
   gate as `UNDO` is), engine side in `EngineCore.Undo.cs`; the desk's Oops panel, and "Undo to here" in each step's right-click
   menu. (6 h)
4. The dark room and the status rules: a dim mode (unselected chrome dimmed, the selected and the critical at full contrast) with
   a desk brightness from 20 to 100 % apart from Windows', and a daylight theme; every light paired with a shape and a word (✓ ok,
   △ warn, ✕ fault, ■ armed); ok, warn and fault at least 3:1 apart in luminance in every theme; saturated red only where the air is
   affected; no alarm faster than 2 Hz; a confirmation before closing the desk while armed ("The show keeps playing on the node").
   (6 h)
5. Preview age: each preview's age from its ring slot's stamp; older than two frames of its own rate → a "stale" badge with the
   age; frozen for 1 s → greyed with the reason (the engine quiet, the ring gone). wp3-desk-gpu reuses it on the GPU path. (3 h)

⟂ *(≈ 21 h)*

6. *(stretch)* Map anything: every control whose line can be bound highlighted; touching one starts `BIND LEARN` for its line.

**Preflight.** None (desk). **INDEX.** None.

**Owns.** `src/PlayMap.App/**` for W1, the `Undo` stub, `tests/PlayMap.App.Tests/**`, `docs/DESK.md`.

**Hunks.** `src/PlayMap.Core/Model/ProjectStore.cs` (`UndoTo(n)` and the steps' list, through the spine amendment lane).

**Depends on.** The spine.

**Acceptance.**
- `GoGuardTests` (headless Avalonia, manual clock): GO with MinGo 500 → the border on, off at 500 ms; a refused GO flashes once.
- `BlindLookTests`: this desk's draft → the grey variant and BLIND on every draft pane; another desk's → neither, its owner named.
- `UndoListTests` (Engine): five edits → five steps newest first in field words; `UNDO TO` the third → the root after the second,
  one journal row; across a lock → refused, nothing undone; an unknown step → refused with the range.
- `StatusRuleTests`: every status template has a shape and a word per light; the 3:1 contrast computed from each theme's tokens;
  the red token used only by air styles; no animation period under 500 ms.
- `PreviewAgeTests`: a frozen ring → stale after two frames, grey at 1 s with the reason.
- `CloseArmedTests`; `DeskVerbRuleTests` pass with the new actions.

**Verified here.** All (headless). **Manual (lab).** A formative session with operators in a dark room, recorded in `docs/UX.md`.

**Next round.** A summative test of the critical tasks (GO, panic, recovering after a desk restart, finding a faulted output).

### 3.7 `wp3-d3d12-core` — the Direct3D 12 device behind the RHI (W2)

*Design: `design/d3d12.md`.*

**Slice.**
1. The device: the adapter by LUID (the outputs' displays' adapter; WARP for CI), the Agility runtime through a device factory
   (M7; the OS runtime with words otherwise), DRED (auto-breadcrumbs, page-fault data) and NVIDIA's Aftermath (when its library sits
   beside the exe) armed before the device is made; the debug layer and GPU-based validation in tests; the features checked and
   recorded (shader model, resource binding tier, enhanced barriers, GPU upload heaps); `D3D12Device : IGpuDevice`. (5 h)
2. Queues and fences: a direct queue at HIGH priority (GLOBAL_REALTIME only when `MACHINE SET GPU-PRIORITY REALTIME` asks and the
   privilege is held, else refused with words — creation fails rather than downgrades), a copy queue; timeline fences as
   `FenceValue`; command allocators per frame in flight; GPU-side waits between queues. (3 h)
3. Memory and descriptors: committed and placed resources through the D3D12 Memory Allocator (TerraFX's port, or PlayMap's own
   pools); an upload ring per frame in flight (a GPU upload heap where ReBAR and build 26080 allow); a readback ring on the copy
   queue; a shader-visible CBV/SRV/UAV heap (static ranges and a per-frame ring), RTV and DSV heaps, static samplers; every
   descriptor and resource released only after the fence value of its last use completes. (6 h)
4. Commands: `D3D12CommandList : ICommandList` with automatic state tracking and barriers (per subresource; resources the render
   graph manages opt out in wp3-d3d12-passes), the shared root signature, constants into the frame's ring through the program's
   layout, pipelines from the embedded DXIL through `IPipelineCache` — made on the render thread only when no warm-up made them
   and then counted (`render.pipelines.renderThreadCompiles`), kept in a pipeline library keyed by adapter LUID, driver version
   and build; timestamps per pass; PIX markers and DRED breadcrumbs per pass. (6 h)
5. The Direct3D 12 leg of `BackendFixture`: every Render pixel scene on WARP within its GL tolerance (the byte-exact ones — packed
   video, HAP encoder blocks — byte-exact); an offscreen D3D12 platform for the render host (`PRESENT PATH D3D12` on a node with
   no outputs), so the Engine suites that render run on it on the Windows job; STATE `render` and `machine.present.api` "d3d12";
   `GPU`. (5 h)

⟂ *(≈ 25 h)*

6. *(stretch)* The GPU-time guard: a user effect slot over its budget for 30 frames bypassed and quarantined (wp3-shaders' record).
7. *(stretch)* An async compute queue for the particle step, with a serial fallback, kept only where measured faster.

**Preflight.** Section `gpu`: running on WARP (red on a show node), the debug layer on (amber), pipelines made on the render
thread since the last arm (amber), the Agility runtime not the one shipped (amber). **INDEX.** None.

**Owns.** `src/PlayMap.Render.D3D12/**` (but `Shaders/**`, finished in W1, passes to it too), `tests/PlayMap.Render.D3D12.Tests/**`,
the `Gpu` stubs, `tests/PlayMap.Render.Tests/BackendFixture.cs` for W2, `src/PlayMap.Render/Platform/GpuPlatform.cs` for W2,
`src/PlayMap.Engine/EngineCore.Output.cs` for W2 (the `api` word), `docs/wire/gpu.md`.

**Hunks.** None beyond its W2 files.

**Depends on.** rhi, shaders.

**Acceptance.**
- `D3D12DeviceTests` (Windows, WARP): the device comes up with the debug layer and GPU-based validation; DRED on; the features
  recorded; no message across the whole Direct3D 12 run.
- `PixelParityTests` (Windows): every Render pixel scene on WARP within the tolerance its GL test holds — the same goldens for both
  backends.
- `LifetimeTests`: a texture disposed while in flight is released only after its fence completes; 1 000 frames of create and
  dispose → live counts back to their baseline; DRED's recently freed list empty at the end.
- `UploadReadbackTests`: byte-exact round trips on the copy queue; the render thread never waits on a readback (counted).
- `PipelineCacheTests`: a second start makes no pipeline (all from the library); a changed driver key → re-warmed with words; a
  render-thread make counted.
- `D3D12AllocationTests`: a warm static frame allocates nothing.
- `D3D12HostTests`: the headless render host on D3D12 renders; STATE says `api` d3d12 with the adapter.

**Verified here.** The C# builds; the pure parts (the release queue, the ring allocator, the cache key). **On CI (Windows,
WARP).** All the above. **Manual (lab).** NVIDIA, AMD and Intel devices; an Aftermath dump of a forced fault on NVIDIA; a Radeon
GPU Detective report on AMD.

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
3. Both backends walk the plan (on GL the barriers are no-ops and transients come from the pool); on Direct3D 12 the automatic
   tracking is off for the plan's resources; STATE `render.graph` (passes, transients, aliased bytes, compile ms, when). (4 h)
4. The missing-barrier check: every golden scene on WARP with aliasing on and off, under the debug layer's barrier validation —
   the same pixels and no message. (3 h)
5. Plans compiled, when the show is armed, for every state it can reach without an edit: each playlist item's chains and
   transitions, the panic outcomes' safe looks, the blind draft's view. (3 h)

⟂ *(≈ 24 h)*

6. *(stretch)* A graph view in STATE for the desk's Health drawer (the passes and their costs as a card).

**Preflight.** None new (wp3-d3d12-passes-2 adds "pipelines compiled"). **INDEX.** None.

**Owns.** `src/PlayMap.Core/Graph/**`; `src/PlayMap.Render/{Engine, Passes, Projection, Effects, Led}/**` for W2 (the passes'
`Declare` methods); `src/PlayMap.Engine/Rendering/RenderHost.cs` and the `RenderHost.Plan.cs` stub for W2.

**Hunks.** `src/PlayMap.Engine/Rendering/RenderHost.VideoIo.cs` (`AirEnd.Declare`).

**Depends on.** rhi; d3d12-core before merge (§5.3).

**Acceptance.**
- `GraphCompilerTests` (Core, pure): two outputs × three surfaces × two chains → the plan's order equals the frame's documented
  order; an unused pass culled; transients with disjoint lifetimes share memory, overlapping never; an imported resource never
  aliased; a barrier before every read of a written resource and none twice; the same topology → the same plan hash.
- `PlanWalkTests`: every golden scene through the plan equals the immediate frame's goldens byte for byte on GL (here) and within
  tolerance on WARP (Windows).
- `AliasingParityTests` (Windows): aliasing on and off identical; no debug-layer message.
- `PlanSwapTests`: an edit mid-run → the next frame walks the new plan; the swap allocates nothing on the render thread.
- `ArmedPlansTests`: arming compiles the reachable plans; a transition's plan is ready before its window opens.

**Verified here.** The compiler, GL walking. **On CI (Windows).** D3D12 walking, aliasing, validation. **Manual (lab).** GPU time
per pass on real GPUs (from -2's timings).

**Next round.** Async compute placed by the compiler where measured.

### 3.9 `wp3-stability` — prove what the audience saw (W2)

*Design: `design/stability.md` Part A (§1–§7).*

**Slice.**
1. The watermark: `AirWatermark` (Core: a 24-bit frame counter, the low 16 bits of the frame's leader instant in milliseconds and
   a CRC-8, as two-tone 8 × 8 px blocks, a mark at the raster's top and one at its bottom), stamped last in the output stage on
   every frame the output presents, held frames included; never drawn while the output is black (blackout, a black panic, a
   fenced node) — the observer reads those from STATE; per output by `WATERMARK` and `Output.Watermark`; drawn through the RHI on
   both backends; preflight says where it shows on the air. (5 h)
2. The observer: `PlayMap observer` (the same exe, its own process, with no GPU device and no render code), started and watched by
   the engine as its workers are, reading a capture source through Media (a capture device, or an OMT or NDI receive of a
   loop-through), decoding both marks of each captured frame into a verdict — ok; held (the counter runs while STATE says the
   output holds its picture: expected); stale (the counter stops for two frames: the pipeline froze, whatever the content does);
   torn (the two marks differ); wrong (the leader instant off by more than a frame); late (lit later than the predicted display
   time by more than the output's budget); lost (no mark while STATE says the output shows); black (no mark while STATE says it is
   black: expected) — on its standard output to the engine and into a status file the supervisor reads, so a wedged engine is
   still caught; STATE `outputs[].observed`, `OBSERVE`, `OBSERVE STATUS`, a red preflight row, HEALTH's outputs category. (8 h)
3. The heartbeat: HEALTH carries each output's shown-frame count (the present ledger's, from display statistics where there are
   any, swap returns on GL) and observed-frame count; the supervisor's rule reads them. (2 h)
4. `LATENCY STEP`: a step in the watermark's region (or, through a camera in rehearsal, the whole output) at chosen leader
   instants, observed; glass-to-glass p50 and p95 per output; refused while armed (Heavy), refused with neither an observer nor a
   camera, naming both. wp3-present adds each stage's share. (5 h)
5. The crash policy: an unhandled exception only flushes the journal (whose header carries the build) and writes a "stopping"
   STATE line, then lets the process end for WER's dump; no in-process crash reporter; dumps stay on the machine. (2 h)

⟂ *(≈ 22 h)*

6. *(stretch)* The observer reading the scan camera's view of the wall in rehearsal (larger marks).
7. *(stretch)* The air hazard file (`docs/HAZARDS.md`: each hazard to the air traced to the test that holds it, design §5).

**Preflight.** Section `observe`: an observed output stale, torn, wrong, late or lost (red); a watermark showing on the air
(amber, with where). **INDEX.** Section `observe`: each observed output's light.

**Owns.** `src/PlayMap.Core/Observe/**`, `src/PlayMap.Engine/Observe/**`, the `Observe` stubs, `src/PlayMap.Render/Passes/
WatermarkPass.cs`, `docs/OBSERVE.md`, `docs/wire/observe.md`.

**Hunks.** `src/PlayMap.Render/Passes/OutputStage.cs` (the watermark last); `src/PlayMap.Core/Fleet/HealthRecord.cs` and
`src/PlayMap.Engine/EngineCore.Fleet.cs` (the shown and observed counts in `HealthOutput`).

**Depends on.** rhi.

**Acceptance.**
- `WatermarkCodecTests`: every counter near the 24-bit wrap round-trips; a flipped block fails the CRC; differing marks → torn.
- `ObserverVerdictTests` (manual clock): scripted captures → each verdict with its words; stale against held by STATE; blackout
  expected.
- `ObserverLoopbackTests` (both jobs): an output sent over OMT loopback as the capture; the observer process reads it; STATE ok;
  `FREEZE ON` → held; an injected fault (the host stops presenting new pictures on one output while STATE says it plays) → stale
  within two frames, preflight red, HEALTH red; the observer killed → restarted with backoff, said in STATE.
- `WatermarkRenderTests` (GL; WARP at the merge): the marks read back from the final raster decode to the frame's counter and
  instant; under blackout the region is black.
- `LatencyStepTests` (the OMT observer): ten steps → p50 and p95 reported; armed → refused by the class; no observer and no
  camera → refused naming both.
- `HeartbeatTests`: shown frames advance with presents, held outputs included; a stopped presenter's do not.
- `CrashPolicyTests`: an unhandled exception in a child host → the journal flushed with the build, a non-zero exit, no in-process
  dump.

**Verified here.** All, with OMT loopback on llvmpipe. **On CI (Windows).** The same. **Manual (lab).** A capture card on an HDMI
loop-through of a real output; an LED processor's monitor out; a photodiode beside the observer for `LATENCY STEP`.

**Next round.** A hardware bypass on loss (LED processors' dual inputs, a relay router) driven by the observer's verdict.

### 3.10 `wp3-present` — Direct3D 12 flip presenters and the predicted display time (W3)

*Design: `design/present.md` Part A (§1–§10).*

**Slice.**
1. `D3D12Platform : IGpuPlatform` on the outputs' adapter; the display list (`DisplayList`'s `EnumDisplayMonitors` and
   `QueryDisplayConfig`, with exact refresh rationals); a flip presenter per output — a borderless popup covering its monitor,
   `FLIP_DISCARD`, the back buffers at the display's mode, a frame-latency waitable with `SetMaximumFrameLatency` at the output's
   frames in flight, the refresh an enumerated mode (a request for another refused, naming both rationals), `Present(1, 0)`;
   `Record` copies the final picture into the back buffer in the frame's list; the fuse (a start that never reached its first
   frames comes back as GL, with words); `PRESENT PATH D3D12` routed, AUTO still GL until M1's record, with words. (7 h)
2. Vblank truth and the evidence: frame statistics into `VblankClock` and `PresentLedger`; Independent Flip as `hint` from
   statistics continuity (each present shown on the refresh it was queued for, no composition stall) and as `proven` by
   `PRESENT PROVE` (N seconds of continuity at the display's own rate, no miss, one frame from present to display), in PresentMon's
   words; `outputs[].present.mode/evidence/evidenceWords`; an output not in Independent Flip amber in preflight with its reason
   (composed: a window overlaps; the mode's refresh is not the display's). (5 h)
3. One queued frame and the predicted display time: frames in flight per output (1 where the output shows a live input, else 2;
   `Output.FramesInFlight`); the render loop waits on the pacing output's waitable, then samples leader time and the newest live
   frames, then records (just in time); a ring of frame blocks (frames in flight + 1: the frame's number, its target vblank, its
   predicted display instant, its leader instant and its stage stamps — sampled, recorded, submitted, presented, shown); the frame
   drawn for the leader instant at the vblank it will scan out on plus the output's display latency, one instant for every stage;
   the stamps into the ledger and `LATENCY STEP`'s per-stage shares. (6 h)
4. HDR10 and 10-bit: `Output.ColorBits` 10 → an `R10G10B10A2` swap chain (BT.709, gamma 2.2); `Output.Hdr` hdr10 → `R10G10B10A2`
   with `SetColorSpace1(G2084, P2020)` and the output stage's PQ encode at `NitTarget`; scrgb → `R16G16B16A16_FLOAT`; what the
   display reports (`IDXGIOutput6::GetDesc1`) in STATE; refused with words where the display cannot. (4 h)
5. The wire: a D3D12 flip output reports PMVB path 2 (`dxgi-flip`) and STATE `machine.present.api` "d3d12". (1 h)

⟂ *(≈ 23 h)*

6. *(stretch)* The dedicated-display presenter (specialized displays through `Windows.Devices.Display.Core`) on Enterprise, Pro for
   Workstations and IoT Enterprise, PMVB path 3; refused with the edition's words elsewhere.
7. *(stretch)* The composition swapchain as a timed-present tier, if M3 says it takes D3D12 buffers.
8. *(stretch)* Present facts from the engine's own ETW session (the show user in Performance Log Users) for `proven` from outside
   the statistics.

**Preflight.** Present mode per output (amber when not Independent Flip, with why); a refresh mismatch (red). **INDEX.** Section
`outputs`: each output's present mode (feedback only).

**Owns.** `src/PlayMap.Render.D3D12/Present/**`; `src/PlayMap.Render/{Platform, Timing}/**` for W3;
`src/PlayMap.Engine/Rendering/{RenderHost, OutputWindows, HeadlessPacing}.cs` for W3; `src/PlayMap.Engine/Presentation/**`;
`src/PlayMap.Engine/EngineCore.Output.cs`; the Output block's rows; `docs/OUTPUT.md`.

**Hunks.** `src/PlayMap.Render/Engine/Contracts.cs` (`FrameInputs.PredictedDisplayNs`, after d3d12-passes-2);
`src/PlayMap.Engine/Observe/LatencyStep.cs` (the stage shares).

**Depends on.** d3d12-core; stability (the step and the heartbeat).

**Acceptance.**
- `D3D12WarpPresenterTests` (Windows): the platform on WARP; 120 presents on a window; statistics read or the fuse's words; the
  back buffer read back equals the final picture.
- `IndependentFlipEvidenceTests` (pure, synthetic statistics): continuity → hint; a composition stall pattern → composed with
  words; `PRESENT PROVE` over 600 continuous samples → proven; one disjoint sample → not proven, counted; an offscreen output →
  refused "no display".
- `RefreshMatchTests`: 60/1 asked of a 60000/1001 display → refused naming both.
- `FrameBlockTests` (manual clock): with one frame in flight the predicted display instant = the next vblank + the display
  latency; with two, one period later; a test pass records the leader instant every stage was given — all equal; the ring reused
  without allocation.
- `JustInTimeTests`: with a fake platform the loop samples leader time only after the waitable signals.
- `HdrOutputTests` (WARP): the HDR10 swap chain with its colour space; 203 cd/m² white encodes to PQ 0.581 ± 0.001.
- `PresentPathTests`: AUTO → GL with "D3D12 waits for its parity record"; D3D12 on Linux refused "Windows only".
- `PmvbCodecTests` unchanged for path 2; `output.json` regenerated; `output.d3d12.json` written by the Windows job.

**Verified here.** Evidence maths, the block ring, the just-in-time order, path words. **On CI (Windows, WARP).** Bring-up,
read-back, HDR swap chains. **Manual (lab).** Independent Flip proven on NVIDIA, AMD and Intel outputs with PresentMon agreeing;
photodiode latency against GL on the same machine (M1); HDR10 on a real display.

**Next round.** AUTO on each tier as its record lands; the ANGLE and D3D11 path deleted.

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
   texture the D3D12 renderer shares by NT handle, synchronised by a shared fence (`OpenSharedFence`, `Signal`, the render queue's
   `Wait`); sends the reverse; no CPU copy either way (M11). (6 h)
4. NDI and OMT on the copy queue: the send stage's readback and the live inputs' uploads as copy-queue work with fence values.
   (3 h)
5. Decode tiers in the capability record: D3D12 Video's decode profiles per codec (`CheckFeatureSupport(VIDEO_DECODE_SUPPORT)`),
   so `MEDIA INSPECT` and preflight say what this GPU decodes in hardware (4:2:2 HEVC on Blackwell, AV1); NVDEC and AMF recorded as
   probed tiers. (2 h)

⟂ *(≈ 24 h)*

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
- `SpoutBridgeTests` (two WARP devices): a chart round-trips byte for byte (BGRA8) both ways; no map or unmap on either side.
- `PackUnpackTests` byte-exact on Direct3D 12.
- `VideoCapsTests`: a fake `CheckFeatureSupport` → the record's profiles; preflight's software-decode row names what the GPU lacks.

**Verified here.** The logic, against fakes. **On CI (Windows).** As above. **Manual (lab).** D3D12VA on NVIDIA, AMD and Intel with
the pinned FFmpeg (M12); Spout with Resolume and TouchDesigner (M11); NDI with a real runtime.

**Next round.** The D3D11VA and dmabuf importers deleted with GL; NVENC sends from the GPU.

### 3.12 `wp3-desk-gpu` — previews without a CPU copy (W3)

*Design: `design/desk-gpu.md`.*

**Slice.**
1. The engine: a preview view drawn at its displayed size into two or three textures created shared, with one shared fence; their
   NT handles duplicated into the desk's process on `VIEW … SHARED <pid>` (loopback sessions only, and only into a process of the
   engine's own user and session, else refused with words); the ring file kept as the view's control block (slot, fence value,
   size, stamp), so the desk learns the newest slot without a line; the engine never waits on the desk — a slot the desk still
   holds is skipped, as the ring does today. (7 h)
2. The desk: `GpuPreviewSource : IPreviewSource` importing each slot once through `ICompositionGpuInterop` (`ImportImage` with
   the texture's NT handle, `ImportSemaphore` with the D3D12 fence where the interop lists that type, M2), drawing into a
   `CompositionDrawingSurface` with its wait value; `IPreviewSource` answering a composition surface or a bitmap; the adapter guard
   (the compositor's LUID against the engine's adapter: different → the ring, with words in the preview's corner); a re-import
   after the desk's device is lost or the engine's device epoch moves. (8 h)
3. The ring kept for remote nodes, another adapter, the GL backend and any import failure, each said in the preview's corner;
   desk-ux's age badge on both paths. (2 h)
4. The desk-kill test: a desk killed mid-import leaves the engine's frame cadence, present ledger and allocation count as they
   were; the view's shared textures released when its lease ends. (3 h)

⟂ *(≈ 20 h)*

5. *(stretch)* The desk's compositor put on the engine's adapter by default (`GraphicsAdapterSelectionCallback`).
6. *(stretch)* Rates from what the desk shows: the focused viewport at the display's rate, thumbnails at 15 Hz, hidden views none.

**Preflight.** None (the desk). **INDEX.** None.

**Owns.** `src/PlayMap.App/Controls/Preview*.cs` and the desk for W3; `src/PlayMap.Engine/Rendering/{PreviewViews,
RenderHost.Previews}.cs`, the `Preview` stub; `src/PlayMap.Core/Media/PreviewRing.cs`; `src/PlayMap.Render.D3D12/Share/**`.

**Hunks.** `src/PlayMap.Render/Engine/RenderEngine.cs` (`Preview` and `CollectPreviews` into shared targets, after
d3d12-passes-2).

**Depends on.** d3d12-core, desk-ux.

**Acceptance.**
- `PreviewShareTests` (Windows, WARP): `VIEW … SHARED <pid>` from a loopback session for a child test process → the handles open
  there on its own WARP device, the chart reads back, fence values grow; another user's or session's pid → refused; a remote
  session → refused "shared previews are local only".
- `GpuPreviewSourceTests` (a fake `ICompositionGpuInterop`): an adapter mismatch → the ring with words; no fence type → the ring
  with words; a lost device → one re-import.
- `DeskKillTests` (Windows): the child desk killed mid-import → the engine's present ledger and p99 frame time over the next 10 s
  within 5 % of the 10 s before, no allocation, the view released at its lease's end.
- `ViewVerbTests` extended.

**Verified here.** Logic, the ring path, refusals. **On CI (Windows).** Duplication and import into a second WARP device in a
child process. **Manual (lab).** Avalonia's own compositor importing the texture and the fence on NVIDIA, AMD and Intel (M2), and
the preview's latency.

**Next round.** A DirectComposition surface hosted by the desk, if the measured latency calls for it.

### 3.13 `wp3-d3d12-passes-2` — nothing compiled, allocated or waited on during the show (W3, continues d3d12-passes)

*Design: `design/graph.md`.*

**Slice.**
1. Pipelines compiled at arm: the union of every reachable plan's pipeline keys compiled off the render thread on `SHOW ARM` and
   on `GPU WARM`, ARM answering only once they are (or with the count still missing); a key missing at air time holds its pass
   (the pass draws its previous picture, or nothing) and is recorded, never compiled on the render thread; the cache keyed by
   adapter LUID, driver version and build, warmed again after a driver change with words; the engine's per-frame paths pre-rolled
   before the node says it is ready. (6 h)
2. Bindless records: the stage pass's surfaces as records in a structured buffer (mesh, UVs, content, mask, blend as descriptor
   indices) drawn with a few indirect draws per output; descriptor indices freed only after their fence values complete; the
   render thread's work no longer grows with the surface count (measured). (8 h)
3. Zero allocation on Direct3D 12: the allocation fence across warm frames for every pixel scene on WARP, modulated layers
   included — their drawn project built without allocating (round 2's remaining gap, R2.8). (5 h)
4. GPU time per pass (`render.passes[].gpuUs`, p50 and p95) and the render thread's CPU time per frame in STATE. (2 h)

⟂ *(≈ 21 h)*

5. *(stretch)* Async compute for the particle step with a serial fallback, kept only where measured faster.

**Preflight.** Section `gpu`: pipelines compiled for the armed show (red when any is missing, fix `GPU WARM`). **INDEX.** None.

**Owns.** Its parent's files for W3 (`src/PlayMap.Core/Graph/**`, `src/PlayMap.Render/{Engine, Passes, Projection, Effects,
Led}/**`), `src/PlayMap.Render.D3D12/{Device, Commands, Pipelines, Bindless}/**` for W3, `EngineCore.Pipelines.cs`, the
stage programs' HLSL.

**Hunks.** None beyond its files.

**Depends on.** d3d12-passes, d3d12-core.

**Acceptance.**
- `PipelineWarmTests`: arming compiles every reachable key before ARM answers; a key forced missing → its pass holds, STATE counts
  it, no render-thread compile; a changed driver key → warmed again.
- `BindlessTests` (WARP): 1, 10 and 100 surfaces → the same pixels as the plan without records; draws per output bounded by a
  small constant; no descriptor reused before its fence.
- `ZeroAllocTests` (WARP and GL): every scene, modulated layers included.
- `PassTimingTests`; `gpu.json` regenerated.

**Verified here.** GL's share of the allocation fence; the warm-up orchestration with a fake cache. **On CI (Windows).** All.
**Manual (lab).** Render-thread CPU time against surface count on a real GPU; a driver update re-warming.

**Next round.** Work graphs for GPU-driven content where measured.

### 3.14 `wp3-framelock` — locked where the hardware locks, honest everywhere else (W4)

*Design: `design/present.md` Part B (§11–§18).*

**Slice.**
1. The NVIDIA present-barrier tier on RTX PRO with RTX PRO Sync: `NvAPI_D3D12_QueryPresentBarrierSupport`; a client per output's
   swap chain (`NvAPI_D3D12_CreatePresentBarrierClient`); its back buffers and fence registered
   (`NvAPI_D3D12_RegisterPresentBarrierResources`), again after every `ResizeBuffers` and rebuild; `NvAPI_JoinPresentBarrier` and
   `NvAPI_LeavePresentBarrier`; `NvAPI_QueryPresentBarrierFrameStatistics` read after the presents every frame (sync mode — not
   joined, client, system, cluster — and the present, present-in-sync, flip-in-sync and in-sync refresh counts);
   `IFrameLockDriver` reshaped for a barrier client; the lock machine's locked states needing the barrier's own evidence (system or
   cluster mode, in-sync counts advancing). (8 h)
2. The honest software lock: without a board or a barrier, every output says "free" with its measured phase against the group's
   reference ("same frame, phase +6.2 ms ± 0.3 ms, next slip in 41 min"); `outputs[].lock.phaseMs`, `sync.outputs[].phaseMs`; the
   word "locked" never said of a free output. (3 h)
3. The all-or-nothing barrier governed: an output whose in-sync count stops for three refreshes leaves the barrier and free-runs,
   its cause recorded; the rest of the wall keeps its barrier; no automatic rejoin while armed (round 2's rule); the 24-bit counter
   wrap-aware through `LockCounter`. (4 h)
4. The shared frame count: content time from `NvAPI_D3D1x_QueryFrameCount` where the driver answers it with the barrier joined
   (M10), else from the leader's anchor at the barrier's in-sync refresh count — both behind `LockCounter`'s source. (3 h)
5. The preconditions as facts: each output's timing (pixel clock, totals, refresh rational) in STATE, so a refused lock names its
   cause — timings more than ±50 ppm apart, EDIDs that differ, VRR on, not in full-screen Independent Flip; preflight rows. (3 h)

⟂ *(≈ 21 h)*

6. *(stretch)* Synchronized Mosaic: one swap chain across a Mosaic surface, outputs as its viewports — the must if M4 says several
   swap chains do not join.
7. *(stretch)* Warp and blend at scanout (`NvAPI_GPU_SetScanoutWarping`, `SetScanoutIntensity`) as an RTX PRO tier behind the
   shader warp, reported applied only after the state is read back.

**Preflight.** The lock per output (a free output in a group that applied the lock: amber with its phase and cause); timing
mismatches (red). **INDEX.** Section `sync`: the barrier's mode per output (feedback only).

**Owns.** `src/PlayMap.Render/Sync/**`, `src/PlayMap.Render.D3D12/{Sync, Present}/**` for W4 (present is merged),
`src/PlayMap.Render/Platform/Windows/NvApi*.cs`, `src/PlayMap.Core/Sync/**`, `src/PlayMap.Engine/Presentation/**` for W4,
`src/PlayMap.Engine/EngineCore.Sync.cs`, `src/PlayMap.Net/Sync/{VblankReport, SyncBoard}.cs`, `docs/SYNC.md` (its lock part).

**Hunks.** None (the barrier's registration on resize and rebuild sits in the presenter it now owns; stability-2's rebuild hook
lands first).

**Depends on.** present (and stability-2 in merge order).

**Acceptance.**
- `PresentBarrierTierTests` (a fake NVAPI with the six functions scripted: pending until full screen, client, cluster): a join
  locks only after cluster mode with in-sync counts advancing for 120 refreshes; pending → "not locked: the barrier is pending (a
  window overlaps output 2)"; after a `ResizeBuffers` the registered buffers equal the swap chain's.
- `NvapiLayoutTests` extended with the barrier's structures against NVIDIA's header.
- `BarrierStallTests`: one output's in-sync count stops → it leaves and free-runs within three refreshes with its cause; the others
  stay locked; armed → no rejoin.
- `SoftwareLockWordsTests`: no board → free with phase; the word "locked" in no STATE value of a free output (the whole STATE
  scanned).
- `TimingPreconditionTests`: two outputs 80 ppm apart → `SYNC APPLY` refused naming both rates and the ±50 ppm rule.
- `LockCounterTests` unchanged; `RebuiltBarrierTests` (merge-time, with stability-2): after a rehearsed removal the barrier's
  resources are registered again before the first present.

**Verified here.** The fake tier, the words, the counters. **On CI (Windows).** NVAPI loads or says "no NVIDIA driver".
**Manual (lab).** A two-machine RTX PRO Sync wall with two outputs each: cluster mode, in-sync counts, a pulled sync cable, a
stalled node, house sync at 59.94 Hz with a ×2 multiply (M4, M10); GeForce and Radeon nodes reporting free with their phase.

**Next round.** EDID and custom timings owned by the presentation layer; SDI and ST 2110 presenters whose vblank truth comes from
the card.

### 3.15 `wp3-stability-2` — device loss as a rehearsed event (W4, continues stability)

*Design: `design/stability.md` Part B (§8–§13).*

**Slice.**
1. One rebuild path: a removal seen at `Present`, `ExecuteCommandLists`, `GetDeviceRemovedReason` or a fence at `UINT64_MAX` → the
   reason and DRED's breadcrumbs and page-fault data journalled (`GPU INCIDENT`: the last pass completed, the first outstanding,
   the recently freed objects) → every device object released → the start-up path run again (device, queues, presenters, the
   pipeline library, imports, the arena, the plans) → frames resume at "now", content being a function of time; the time to the
   first good frame in STATE; the same path on GL (context loss by robustness where the context offers it, else a fault seam);
   `RenderHost.FaultLimit`'s stop gone. (8 h)
2. Rehearsal: `GPU REHEARSE [REMOVE]` forces a removal (`ID3D12Device5::RemoveDevice`) in rehearsal, refused while armed; CI
   forces removals on WARP (M8) and asserts the STATE fields and the time to the first good frame (recorded; a 4× ceiling). (4 h)
3. Repeated loss governed: rebuilds with backoff; three in ten minutes → the GL fallback where it starts, else the outputs held as
   the show's policy says, with words — never a silent stop; a removal whose breadcrumb sits inside a user effect's pass
   quarantines that kind before the rebuild draws again. (4 h)
4. The degrade order for a missing frame: the next frame drawn for its own instant, never a catch-up → the last presented picture
   held while the render thread stalls, with the stall's cause → an observed stale or lost output red on the attention bar with
   the backup's `FAILOVER` as its fix line (the takeover's automatic trigger stays the backup's next-round item); black only where
   the show's policy says. (3 h)
5. GPU crash evidence: an Aftermath dump on a removal on NVIDIA when its library is present; the dump manifest beside it. (2 h)

⟂ *(≈ 21 h)*

6. *(stretch)* A real TDR through `dxcap -forcetdr` in the lab script, its time to a good frame published.

**Preflight.** Section `gpu`: removals since the last arm (amber with their reasons). **INDEX.** None.

**Owns.** Its parent's files, `EngineCore.Rebuild.cs`, `src/PlayMap.Engine/Rendering/RenderHost.cs` for W4,
`src/PlayMap.Render.D3D12/{Device, Rebuild}/**` for W4.

**Hunks.** `src/PlayMap.Render.D3D12/Present/**` (the presenters made again through their factory on a rebuild; framelock owns
the folder in W4 and merges after).

**Depends on.** present, d3d12-passes-2.

**Acceptance.**
- `DeviceLossRehearsalTests` (WARP): a removal during a running show (rehearsal) → `GPU INCIDENT` with DRED's breadcrumbs naming
  the pass; the first good frame within the recorded budget; STATE `render.removals` 1 with the reason; the presenters back;
  media time continuous — the frame after the rebuild shows the instant it should.
- `RebuildPathTests` (a recording fake): the rebuild calls exactly the start-up steps, in the same order.
- `RepeatedLossTests` (fake device): three in ten minutes → GL with words; GL unable to start → held by the policy, with words.
- `QuarantineOnLossTests`: a breadcrumb inside effect slot X → its kind quarantined; the rebuild draws X bypassed.
- `RehearseVerbTests`: armed refused; on GL without robustness refused "the GL context cannot be removed on purpose here".
- `DegradeOrderTests`.

**Verified here.** The policy and the path against fakes. **On CI (Windows, WARP).** Forced removals. **Manual (lab).** A real
TDR on NVIDIA and AMD; a driver update during a rehearsal; an Aftermath dump read in Nsight.

**Next round.** The observer's verdict driving the hardware bypass; a safety monitor kept even simpler than the supervisor.

---

## 4. Shared files and their hunks

Every file has one owner in each wave (§2.2). A package touching another's file does so only through a hunk named here, landing
in §5.3's order. The files the move edits most are the map's §9 list.

| File | Owner, by wave | Hunks, in merge order |
|---|---|---|
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | spine (outside blocks) | each package inside its own block only |
| `src/PlayMap.Core/Model/OutputModel.cs` | spine (W0 lands every round-3 field) | none |
| `src/PlayMap.Core/Model/ProjectStore.cs` | spine | desk-ux (`UndoTo` and the steps' list, W1, through the amendment lane) |
| `Version.props`, `src/PlayMap.Core/BuildInfo.cs` | spine (from the `versioning` branch) | installer (the 255 rule, `MsiVersion`; W1) |
| `src/PlayMap.Core/Media/GpuSurfaces.cs`, `UploadArena.cs` | spine (W0: `GpuApi.D3D12`) → video (W3) | none |
| `src/PlayMap.Core/Media/PreviewRing.cs` | desk-gpu (W3) | none (desk-ux reads the existing stamp) |
| `src/PlayMap.Core/Effects/IsfTranslator.cs` | shaders (W1) | none |
| `src/PlayMap.Core/Fleet/HealthRecord.cs`, `src/PlayMap.Engine/EngineCore.Fleet.cs` | — (round 2's, finished) | stability (shown and observed counts, W2) |
| `src/PlayMap.Render/Gpu/*` → `Gl/*` | rhi (W1) | none |
| `src/PlayMap.Render/Engine/{RenderEngine, FramePasses, Contracts}.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | present (`FrameInputs.PredictedDisplayNs`, W3) → desk-gpu (`Preview`, `CollectPreviews`, W3) |
| `src/PlayMap.Render/Passes/OutputStage.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | stability (the watermark last, W2, before d3d12-passes) |
| `src/PlayMap.Render/Passes/SendStage.cs` | rhi → d3d12-passes → d3d12-passes-2 | video (the copy-queue readback, W3) |
| `src/PlayMap.Render/Effects/FxChainRunner.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | shaders (the quarantine's bypass, W1) |
| `src/PlayMap.Render/Platform/**`, `Timing/**` | rhi (W1) → d3d12-core (`GpuPlatform.cs`, W2) → present (W3) | none |
| `src/PlayMap.Render.D3D12/**` | spine (empty) → shaders (`Shaders/`, W1) → d3d12-core (all, W2) → W3: present (`Present/`), video (`Video/`), desk-gpu (`Share/`), d3d12-passes-2 (`Device/`, `Commands/`, `Pipelines/`, `Bindless/`, `Shaders/`) → W4: framelock (`Sync/`, `Present/`), stability-2 (`Device/`, `Rebuild/`) | stability-2 (`Present/`: presenters made again on a rebuild, W4, before framelock) |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | rhi (W1) → d3d12-passes (W2) → present (W3) → stability-2 (W4) | platform (thread claims, W1) |
| `src/PlayMap.Engine/Rendering/HeadlessPacing.cs` | rhi (W1) → present (W3) | platform (the high-resolution timer, W1) |
| `src/PlayMap.Engine/Rendering/RenderHost.VideoIo.cs` | rhi (W1) → video (W3) | d3d12-passes (`AirEnd.Declare`, W2) |
| `src/PlayMap.Engine/Rendering/{PreviewViews, RenderHost.Previews}.cs` | rhi (W1) → desk-gpu (W3) | none |
| `src/PlayMap.Engine/EngineCore.Output.cs` | d3d12-core (W2) → present (W3) | none |
| `src/PlayMap.Engine/Presentation/**` | present (W3) → framelock (W4) | none |
| `src/PlayMap.Engine/Fleet/MachineSampler.Windows.cs` | — (round 2's) | platform (reads the identity record, W1) |
| `src/PlayMap.Net/Sync/VblankReport.cs` | spine (W0: path 3) → framelock (W4) | none |
| `src/PlayMap.Media/Hw/**`, `Video/Spout/**` | video (W3) | rhi (the share's type, W1) |
| `src/PlayMap.App/**` | desk-ux (W1) → desk-gpu (W3) | installer (`Link/EngineSupervisor.cs`: attach to a supervised engine, W1) |
| `.github/workflows/build.yml` | spine | shaders (pinned compiler natives, W1) → installer (MSI, install, upgrade, signing, W1) → d3d12-core (the debug-layer settings and the D3D12 leg's summary, W2) |
| `THIRD-PARTY-NOTICES.md` | spine (headings) | each package its heading: d3d12-core (TerraFX, the D3D12 Memory Allocator, the Agility SDK; WARP for CI only), shaders (DXC, glslang, SPIRV-Tools, SPIRV-Cross), installer (WiX as a build tool; FFmpeg's LGPL libraries and libvmx now inside the MSI), stability-2 (Aftermath, if shipped) |
| `*.csproj` | the project's owner | one `<ItemGroup Label="wp3-<pkg>">` per package |
| `tests/fixtures/state/{main, output, decode, sync}.json` | the package whose keys change: present (`output`), video (`decode`), framelock (`sync`) | Linux keeps GL words; the Windows job writes `*.d3d12.json` twins and compares them there |
| `integrations/companion-module-playmap/test/fixtures/*` | the integration pass | none |
| `docs/ARCHITECTURE.md`, `README.md`, the agent brief, `docs/WIRE.md`, `docs/ROADMAP.md`, `docs/COMPETITIVE.md` | the integration pass (§5.6) | none during the waves; packages write papers notes |

---

## 5. Waves, the machine, the merge order and the gate

### 5.1 Waves

| Wave | Packages | Why this grouping |
|---|---|---|
| **Base** | the `versioning` branch merged to `main` | The version every later artifact, journal and dump carries; the installer's rules build on it. |
| **W0** | spine | The rows, contracts, model fields, projects and the WARP leg every later test reads. W1 starts on a green spine. |
| **W1** | rhi, platform, shaders, installer, desk-ux (5) | Each depends on the spine only. rhi alone touches `src/PlayMap.Render` and the render host; the other four own new projects (`Platform.Windows`, `Render.D3D12/Shaders`, `Supervisor`, `installer/`) and the desk. The labs' benches for M1–M3 run beside them. |
| **W2** | d3d12-core, d3d12-passes, stability (3) | Each needs the RHI; d3d12-core also the HLSL. The graph compiler and the observer run on GL here; their Direct3D 12 acceptance runs after d3d12-core merges first. |
| **W3** | d3d12-passes-2, present, video, desk-gpu (4) | Each needs the Direct3D 12 device; d3d12-passes-2 also the graph; desk-gpu also the desk's age badge. |
| **W4** | stability-2, framelock (2) | Each needs the flip presenters; the rebuild path before the barrier that must register again in it. |

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
| W3 | d3d12-passes-2, desk-gpu | present, video |
| W4 | (reviews, the integration pass) | stability-2, framelock |

The packages here are the ones that run the GL pixel suites heavily or own the integration-hot render files; the cloud ones own
new projects or take their evidence from the Windows job. With this split the round runs in about seven rounds of sessions:
W0, two for W1, one each for W2–W4, and the integration pass.

### 5.3 Merge order

| Wave | Order | Why |
|---|---|---|
| W1 | **rhi → shaders → platform → desk-ux → installer** | The RHI changes Render and Core first; shaders' layout tests read its layouts and its `FxChainRunner` hunk follows it; platform's timer and claims hunks follow it; the installer's attach hunk follows desk-ux's desk. |
| W2 | **d3d12-core → stability → d3d12-passes** | The device before anyone's WARP acceptance; the watermark's `OutputStage` hunk before d3d12-passes declares the output stage. |
| W3 | **d3d12-passes-2 → present → video → desk-gpu** | d3d12-passes-2 owns the frame files the other three hunk. |
| W4 | **stability-2 → framelock** | The rebuild path before the barrier registers again in it. |

### 5.4 The gate for every merge

On one integration worktree here, one build at a time:

1. Rebase on `main`; build the whole solution in Release with the analyzers on, the desk, `tools/PlayMap.Node` and the new projects
   included.
2. Every test project on Linux to its end (GL on Xvfb, FFmpeg 9.0's libraries, the Companion module's `npm test`); the package's
   fixtures regenerated and equal to the committed ones; `RoutesTests`, `StateOrderTests`, `CatalogueTests`, `ContextLinkTests`,
   `ModuleRuleTests`, `RhiFenceTests`; no skipped, disabled or quarantined test.
3. The pushed branch's Windows job green, as in round 2.
4. **From wp3-d3d12-core's merge on, the Windows job's Direct3D 12 leg is a hard gate**: every `Backend=D3D12` test passes on
   WARP with the debug layer and GPU-based validation on, the run's debug-layer message count is zero (an allowed message is named
   in its test, with why WARP gives it), and `PixelParityTests` holds every pixel scene on both backends — a new pixel test brings
   both. A red Direct3D 12 leg blocks the merge exactly as a red GL test does; a test that is red only sometimes is a defect for the
   package's review session, never a waiver.
5. **From wp3-installer's merge on, the MSI leg is a hard gate**: the MSI builds, installs, upgrades from the previous build,
   refuses a downgrade and uninstalls on the runner; on `main`, every signed file verifies.

A red merge is fixed by that package's review session, never by editing another package's test.

### 5.5 Tests added at merge

They need two packages; the second package's review session writes them.

| After | Test |
|---|---|
| rhi, shaders | `ShaderLayoutTests` against rhi's `ProgramLayout`s |
| d3d12-core, stability | `WatermarkRenderTests` on WARP |
| d3d12-core, d3d12-passes | `PlanWalkTests` and `AliasingParityTests` on WARP |
| platform, d3d12-core | the direct queue's priority from `MACHINE SET`; the identity's Agility field filled |
| installer, stability | `SupervisorHeartbeatTests`: the supervisor stops an engine whose shown frames stop (a fake engine on loopback) |
| present, stability | `LatencyStepTests` with each stage's share; `HeartbeatTests` from D3D12 frame statistics where WARP gives them |
| video, d3d12-passes-2 | the send stage's copy-queue readback through the plan; `ZeroAllocTests` with a live input |
| present, desk-gpu | shared previews while flip presenters run (WARP) |
| stability-2, shaders | `QuarantineOnLossTests` |
| stability-2, framelock | `RebuiltBarrierTests` |

### 5.6 The round's integration pass (after W4)

1. `RoutesTests` flips to **every round-3 row is routed** (true by construction: packages removed the rows they did not build).
2. `RoundThreeEndToEndTests` (Windows, WARP): a show on `PRESENT PATH D3D12` — two outputs on flip presenters, a HAP clip and an
   H.264 clip (the fake D3D12VA decoder), an ISF chain compiled by the worker, a Spout send and receive across two devices, an OMT
   observer reading output 1, a shared preview in a child process; `SHOW ARM` compiles every pipeline and preflight lists one
   section per package; a rehearsed removal mid-show recovers; no STATE value calls a free output locked.
3. The papers, from each package's note: `docs/ARCHITECTURE.md` §1 (an installed node, not a portable exe), §2 (the module table
   with `Render.D3D12`, `Platform.Windows` and `Supervisor`; Render's row), §5 (the RHI, the graph, Direct3D 12, GL as the
   reference); `docs/OUTPUT.md`, `DECODE.md`, `VIDEO.md`, `EFFECTS.md`, `SYNC.md`; `README.md`'s opening and its module table; the
   agent brief's description and loop (the Windows job, WARP); `docs/WIRE.md`'s round-3 section; `docs/ROADMAP.md`'s statuses
   (R0.7, R2.1, R2.5, R2.8, R2.9, R4.2, R4.6); `docs/COMPETITIVE.md`; `THIRD-PARTY-NOTICES.md` against the MSI's contents.
4. `LicenceTests` on the MSI's contents: no NDI binary; FFmpeg reports no `--enable-gpl`; no WARP; no AMD runtime; every notice
   present.
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
| R4 | WARP passes but real drivers differ (timing, formats, video) | High | Medium | The lab checklist per vendor (§7); AUTO stays GL until M1; every vendor feature probed, never assumed | present, video, framelock |
| R5 | A user shader hangs or faults the GPU under an explicit API | Medium | High | Compiled out of process; SPIR-V validated; a GPU-time guard; the kind quarantined; the rebuild path | shaders, stability-2 |
| R6 | GLSL and HLSL drift apart while both live | Medium | Medium | Layout tests per program; pixel parity per scene on both backends as a hard gate; GL retires next round | shaders, d3d12-core |
| R7 | TerraFX or its memory-allocator port lags the Agility SDK or misses a call | Low | Medium | Raw declarations for any gap (the house's `ComSlots` way); PlayMap's own placed pools; Vortice named as the alternative | d3d12-core |
| R8 | Avalonia does not import a D3D12 fence (M2) | Medium | Low | Measured in W1; the ring stays; the age badge ships either way | desk-gpu |
| R9 | The present barrier refuses several swap chains (M4), or yields no shared frame count (M10) | Medium | High for multi-output walls | Measured on the lab wall before W4; Mosaic as the multi-output tier; content time from the anchor at the in-sync refresh | framelock |
| R10 | The Agility runtime does not load from a .NET apphost (M7) | Medium | Low | The OS runtime, named in STATE; newer features probed and refused with words | d3d12-core |
| R11 | Auto-logon and a LocalSystem supervisor widen the attack surface | Medium | High | A standard show account; its password as an LSA secret, never a plain registry value; the supervisor's pipe ACL'd to the show user and administrators; the MSI grants two named privileges to one named account; the engine never elevated; guidance in `docs/INSTALL.md` | installer |
| R12 | Wire and fixture changes break peers (Patterns, Stagewatch, Companion) | Low | Medium | D3D12 flip outputs keep PMVB path 2; new STATE words additive; Windows fixtures as twins; the module's fixtures regenerated at integration; mixed builds in a group named in preflight by their versions | spine, present, integrator |
| R13 | Platform claims starve the desk or the system (a high-priority process with MMCSS threads) | Low | Medium | Each claim a setting with its read-back; realtime GPU priority off until measured; the lab's p99.9 with and without each | platform |
| R14 | Licences: FFmpeg now inside the MSI; WARP not redistributable; AMD runtimes; Aftermath's terms | Medium | High | The LGPL FFmpeg build only, as replaceable files with notices; WARP in CI only; AMD runtimes never bundled; Aftermath loaded when present until its terms are confirmed; `LicenceTests` on the MSI | installer, integrator |
| R15 | People read "Direct3D 12" as "the default" while AUTO is still GL | Medium | Medium | STATE says why AUTO is GL; the roadmap and papers say it in the present tense; the lab checklist says what remains | integrator |
| R16 | Fifteen sessions; `main` moves meanwhile | Medium | Medium | Packages start as their dependencies merge; cloud sessions beside this machine's five; every package rebases before merge; §9 decision 1 names the deferral order | orchestrator |
| R17 | Add/Remove Programs shows `0.2.17` for 0.2.0 build 17 | High | Low | The product's name carries the SemVer; `docs/INSTALL.md` explains the two numbers | installer |

---

## 7. The lab checklist

What the Windows job cannot prove. Each line will have its script in `docs/round3/lab/` before its package merges; a line's result
is recorded with the machine, driver and build it ran on.

| # | Check | Machine | Pass mark | Package |
|---|---|---|---|---|
| L1 | **M1**: photon latency and missed vblanks, `PRESENT PATH GL` against `D3D12`, an hour each, photodiode at the top-centre of the output | an RTX PRO node, a GeForce RTX node, a Radeon node, an Arc node | D3D12's p99 photon latency at or below GL's; its missed vblanks at or below GL's | present |
| L2 | Independent Flip proven per output, PresentMon beside STATE | each node of L1, two outputs | PresentMon shows "Hardware: Independent Flip" on each output for 10 minutes and STATE says `proven` | present |
| L3 | **M2**: Avalonia importing the D3D12 texture and fence | the desk on each vendor | a preview shown from the shared texture; the fence type listed | desk-gpu |
| L4 | **M3**: the composition swapchain with D3D12 buffers and a target time | an RTX node | a present at its target time, measured | present |
| L5 | **M4, M10**: the Sync wall | two RTX PRO machines with RTX PRO Sync, two outputs each, house sync at 59.94 Hz with a ×2 multiply | cluster mode with in-sync counts advancing on all four outputs; a pulled sync cable frees only that machine, with its cause; the frame count shared, or the anchor's fallback measured; photodiode phase across machines within the card's specification | framelock |
| L6 | **M5**: Reflex on the RTX PRO driver | the RTX PRO node | `NvAPI_D3D_SetSleepMode` answers and its markers report | present |
| L7 | **M12**: D3D12VA through FFmpeg 9.0.2 | NVIDIA, AMD, Intel; H.264, HEVC 10-bit, AV1 at 4K60 | STATE says `d3d12va-direct`; no CPU copy counted | video |
| L8 | **M11**: Spout with Resolume and TouchDesigner | an RTX node | both directions without a CPU copy | video |
| L9 | A real TDR (`dxcap -forcetdr`) and a driver update during a rehearsal | NVIDIA, AMD | the first good frame within the CI's recorded budget × 4; an Aftermath dump read on NVIDIA | stability-2 |
| L10 | The platform's claims | the three vendors' nodes, a hybrid Intel CPU, a dual-CCD X3D CPU | p99.9 frame time with each claim at or below without it; the defaults confirmed or changed with the record | platform |
| L11 | The installed node: **M9**, auto-logon, Shell Launcher, an upgrade refused while armed | Windows 11 IoT Enterprise LTSC 2024 | the supervisor starts the engine in the show user's session after a cold boot; the armed refusal's words | installer |
| L12 | The observer and `LATENCY STEP` | a capture card on an HDMI loop-through; an LED processor's monitor output | verdicts match injected faults; the step's glass-to-glass within 1 ms of a photodiode's | stability |
| L13 | HDR10 output | an HDR10 display | PQ codes measured at the display within its tolerance | present |
| L14 | A 72-hour soak on Direct3D 12, two outputs | the RTX PRO node | zero missed vblanks; no removal; memory flat | integrator |

---

## 8. Coverage: every line of the direction

| The direction's line | Packages | Round 3 reaches | Proved by |
|---|---|---|---|
| "an installed Windows application" | installer, platform | A signed per-machine MSI; the engine supervised in the show user's session; machine-level actions done once; the version rules an installer needs | `InstallerTests`, `SupervisorTests`, `MsiVersionTests`; L11 |
| "direct deep Windows features" | platform, present, installer | QoS and timer claims, MMCSS, process and GPU priority, keep-awake, flip-model presenters with HDR10 and 10-bit, WER dumps, privileges; specialized displays as stretch | `PlatformCallsTests`, `D3D12WarpPresenterTests`, `HdrOutputTests`; L2, L10 |
| "processor features" | platform | The CPU's vector widths and efficiency classes in the identity; EcoQoS background pools; CPU sets as stretch. **Below bar this round**: hot kernels benchmarked on the show CPUs, ReadyToRun against NativeAOT (M13) | `PlatformPolicyTests`, `IdentityTests` |
| "graphics card features including RTX" | d3d12-core, framelock, video, platform | Aftermath, RTX PRO Sync through the present barrier, NVDEC and 4:2:2 profiles probed, the RTX PRO tier detected. **Below bar this round**: RTX Video and TrueHDR, Optical Flow, scanout warp (stretch) | `PresentBarrierTierTests`, `VideoCapsTests`; L5, L6 |
| "and Radeon" | d3d12-core, platform, video, framelock | DXIL with PDBs for Radeon GPU Detective, the Radeon tier detected, AMF probed, the honest software lock (AMD has no framelock). **Below bar this round**: ADLX display control (FreeSync off, 10-bit forced) | `SoftwareLockWordsTests`, `IdentityTests`; L1, L7 |
| "Portability is not an issue — ability" | D1–D4, d3d12-core, video, present | Windows-only nodes; Direct3D 12 with HDR10, 10-bit, GPU upload heaps, D3D12VA, bindless records | §1.6 item 2 |
| "ultra-low latency" | present, d3d12-passes-2, stability | One queued frame, the waitable, just-in-time sampling, one predicted display time per frame, nothing compiled on the render thread, `LATENCY STEP` measuring it | `FrameBlockTests`, `JustInTimeTests`, `PipelineWarmTests`, `LatencyStepTests`; L1, L12 |
| "reliability" | stability, stability-2, installer, d3d12-core | The independent observer, the shown-frame heartbeat, the supervisor, DRED, one rehearsed rebuild path, quarantine | `ObserverLoopbackTests`, `DeviceLossRehearsalTests`, `SupervisorHeartbeatTests`; L9, L14 |
| "flexibility" | rhi, platform | Two backends behind one interface; every vendor feature probed with a fallback that keeps the show correct | `RhiFenceTests`, `PixelParityTests` |
| "Add version numbers to builds" | `versioning` (base), installer | One source; SemVer, file version and MSI version each in its reader's rules; every MSI build ordered | the branch's `BuildIdentityTests`, `MsiVersionTests` |
| "latest gaming code … for speed" | d3d12-passes, d3d12-passes-2, present, d3d12-core | A render graph compiled on show change, pipelines at arm keyed by driver, bindless records, zero allocation, copy queues and timeline fences, frames in flight per output | `GraphCompilerTests`, `BindlessTests`, `ZeroAllocTests` |
| "medical code … for stability" | stability, stability-2, platform | A counter in the picture read by an independent observer; a heartbeat that is the shown-frame count; device loss rehearsed; driver sets locked like a SOUP list; the air hazard file as stretch | `ObserverVerdictTests`, `HeartbeatTests`, `IdentityTests` |
| "UX design … user interface" | desk-ux, desk-gpu | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status by shape and word, preview age, previews without a CPU copy | `GoGuardTests`, `BlindLookTests`, `StatusRuleTests`, `PreviewShareTests`; L3 |

---

## 9. Open decisions

1. **The size of the round.** As planned, fifteen sessions. If it must be smaller, defer whole packages in this order and mark
   their §8 rows "Below bar this round": desk-gpu (the ring stays), framelock (the board stays as it is, joins refused), video's
   Spout bridge and copy-queue arena (D3D12 decode by copy-back). Never rhi, d3d12-core, d3d12-passes, d3d12-passes-2, present,
   stability, stability-2, installer or platform: they carry the direction's own lines.
2. **Signing identity.** Azure Artifact Signing takes organisations in the USA, Canada, the EU and the UK, and individuals in the
   USA and Canada only. The owner's eligibility decides it; until then builds are unsigned and named so.
3. **WiX's Open Source Maintenance Fee** applies to organisations above $10 000 of annual revenue; the plan assumes it is paid if
   it applies.
4. **FFmpeg inside the MSI.** The pinned Windows build must be the LGPL variant; the installer package confirms it on its first day.
5. **AUTO's switch** per vendor tier needs the owner's sign-off on L1's record.
6. **Show nodes without the desk.** Recommended: show nodes run the engine only (Shell Launcher on IoT Enterprise) with the desk on
   another machine; a one-machine show stays supported.
7. **Aftermath's library** shipped in the MSI, or loaded where the user puts it, by its terms.
8. **Realtime GPU priority** stays off until L10 measures it.
9. **The air hazard file** (stability's stretch 7) as a standing rule for the air's code: each hazard traced to a fault-injection
   test, borrowing medical risk practice as a design tool, not as a claim of compliance.
10. **`main` has moved** 36 commits past `23f162f`, the desk's merge among them; none touches the renderer, the render host, CI,
    Media or Net, and the desk's `PreviewSource.cs` keeps the same copy path [map header]. The spine rebases at start; no conflict
    is expected outside the desk's preview files, which desk-gpu owns in W3.
