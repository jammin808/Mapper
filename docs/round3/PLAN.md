# PlayMap round 3: the plan

*How PlayMap will become an installed, Windows-native show node on every edition of Windows 11, Home included: Direct3D 12 behind a
rendering hardware interface (RHI) cut at today's `GlDevice`, presented through DXGI's flip model with the evidence for every
claim, locked between machines where the hardware locks and honestly free everywhere else, supervised and installed per machine,
and numbered so an installer can order every build; and how its desk will give a person simple guided workflows beside the
advanced and programming tools, with every map doable by hand, the assistant in every step, and capture cards passed through to the
air at the least latency the path allows. The round is a spine in one session, then the fifteen packages of the Windows move and
five packages for the owner's requirements of 18 October 2026, in four waves; each package is one implementation session followed
by one review-and-fix session. This file says who builds what, in which order, how each is accepted, and what waits for the next
round. Read against `main` at `23f162f` (16 Oct 2026) for the Windows move and at `6f24310` (round 2 closed) for the desk and
capture work, the codebase map (`research/codebase-map.md`, cited as **[map §n]**), the direction note
(`research/windows-gpu-direction.md`, cited as **[dir §n]**, its sections counted in order) with the six notes beside it, and the
notes for the owner's answers and requirements — Windows 11 Home (`research/windows11_home.md`, **[home §n]**), the installer
without a paid service (`research/installer_tools.md`, **[inst §n]**), capture cards (`research/capture_cards.md`, **[cap §n]**),
the workflows and the desk (`research/workflows_ux.md`, **[ux §n]**) and two readings of the tree at `6f24310`
(`research/desk-today.md`, **[today §x]**; `research/video-today.md`, **[vt §n]**). The designs that go beyond this file are in
`design/`.*

*The owner's direction, in their words: "PlayMap needs to be an installed Windows application to take full advantage of direct
deep Windows features, processor features, and graphics card features including RTX and Radeon. … Portability is not an issue —
ability, ultra-low latency, reliability and flexibility is. Add version numbers to builds. Remember to consider latest gaming and
medical code, UX design, and architecture for speed, user interface, and stability."*

*The owner's answers to the round's decisions (§9) and their requirements of 18 October 2026, in their words: "PlayMap also needs to
run on Windows 11 Home as well." "Use hardware lock where possible, fall back to software lock." "Leave unsigned for now." "Do not use
WIX paid services." "Full physical hardware testing is not available yet." "All 16." And: "The simple four click quad: · Needs to stay
for a user to come back to if clicked away from in the menu. · Each corner/pin Needs to be editable and single pixel nudge-able. The
media allocated to a screen should be shown in the UI. It needs simple workflows UI areas, as well as areas advanced techniques and
programming areas. For example: · A two projector bland [blend] or double stacked projector blend on a standard flat screen. This is
standard corporate AV work and should offer an intuitive workflow and interface. · A single map of a 2d visual (like a film or web
video mapped onto a 3d surface like a house wall. So it can be viewed as intended without distortion. (Can be blended to more
projectors) · A simple workflow option to 3d map surfaces with 1 or 2 projectors. Multiple outputs can use the same pattern, media or
generator to span a combined screen canvas. All features can be accessible from menu items or clever, intuitive, interactive right
click menus. Video capture cards should be ultra fast in their pass through signal flow. Remember to consider Games design UI/UX for
professional and intuitive interfaces and UI. Keep in mind gaming and medical code architecture, for speed, ultra-low latency and any
database or server use. UI panels should be draggable and collapsible. Consider subtle uses of colour in the UI/UX to enhance
cognition and awareness of areas and workflow stages." And: "corners can also be dragged in the four click quad. All mapping can also
be manual, from initial setup to adjusting the auto scan and setup. Remember - Ai integration throughout."* §8.2 maps each sentence
to the section that answers it.

---

## 1. The goal and the decisions

### 1.1 The goal

At the end of round 3 a Windows 11 machine — Home, Pro or IoT Enterprise, build 26100 or later — will run PlayMap from a
per-machine installer, unsigned this round and named so, with a SHA-256 list beside every build. Its engine will start in the
show user's session under a supervising service and render on Direct3D 12. Each output will present through its own
flip-model swap chain, and STATE will say whether that output is in Independent Flip and how that is known. The frame will
be drawn for the instant it is predicted to light the display, from a render graph compiled when the show changes, with every
pipeline compiled before the show is armed. User effects will compile out of process and draw on the same device. Decode will
stay on the GPU, previews will reach the desk without a CPU copy, and a lost device will be rebuilt by the same path that started
it, rehearsed in CI. An independent observer will read back what the audience saw. Outputs will lock across machines where the
hardware locks — the present barrier on RTX PRO with RTX PRO Sync — and say "software lock: same frame, phase +x ms" everywhere
else. OpenGL will stay the reference backend through the round and keep the Linux development loop alive. It will retire in the
next round, once the pixel and timing evidence says the two backends agree.

The desk will open on simple guided workflows — two projectors blended or stacked on a flat screen, a film on a building seen from
the audience, an object mapped with one or two projectors — each a list of steps the node computes, done automatically, by hand or
by the assistant on a person's approval, beside panes that dock and collapse, a menu bar, a palette, a console and right-click
menus generated from the catalogue. Every mapping step will be doable by hand, and every automatic result will be a layer the hand's
edits sit on, kept through a re-run. The four-click quad will stay put, every corner and pin will move by one pixel, every screen
will say what it shows, and one pattern, picture or generator will span several outputs as one canvas. A capture card will pass
through to an output with no CPU copy where the card allows, its arrival-to-shown time published.

No lab exists this round (the owner's answer 5): everything the Windows job and the simulators can prove is proven there, and every
claim that only hardware can prove is reported not run, never passed (§7).

What exists today, and what the move changes, is the map's subject; its twelve findings [map §1] set the shape of this plan.
The three that shape it most: GL calls sit in ten files but GL types reach fifty of the renderer's eighty-nine [map §2.1];
there is no declarative render graph, only an ordered frame with five pass hooks [map §2.3]; and device loss stops the drawing,
with no rebuild path [map §3.1].

### 1.2 Decisions taken

The owner answered the round's decisions on 18 October 2026 (§9 records each answer and what it changes). D4, D5 and D6 carry the
answers on the edition, the lock, signing and the installer; D11–D16 are what the answers and the owner's new requirements add.

| # | Decision | What it means in the tree | Why | What would reopen it |
|---|---|---|---|---|
| D1 | **Direct3D 12 is the primary renderer, behind an RHI.** | The RHI's contracts will live in Core (`PlayMap.Core/Gpu`); the GL backend will move under `src/PlayMap.Render/Gl/`; the Direct3D 12 backend will be a new edge, `src/PlayMap.Render.D3D12`, referencing Core and its native libraries only. Everything a backend implements or feeds will move to Core with the contracts: the platform and presenter seams, the program table, the vblank clock and ledger, the lock driver's shape. Passes, generators and the frame code will use the RHI and name neither API. | New GPU features reach Windows through D3D12 first; it sits on DXGI's flip model and NVIDIA's present barrier; it is the currency of D3D12 Video, Spout's D3D11 bridge and the desk's GPU import; only D3D12 has PIX, DRED, Aftermath, Radeon GPU Detective and GPU-less CI on WARP [dir §1]. | Nothing in the evidence; a lab finding that D3D12 is slower than GL on a vendor tier delays AUTO there (D2), not the decision. |
| D2 | **OpenGL will stay the reference backend until pixel and timing parity, then retire.** | GL 3.3 core / ES 3.00 will stay behind the RHI with unchanged pixels, both dialects' legs in CI. `PRESENT PATH D3D12` will be explicit this round; AUTO will stay GL until a lab parity record exists for a vendor tier (§1.5, M1), and then become D3D12 on that tier in one commit that cites the record. GL, ANGLE and the D3D11 path will be deleted next round, each with the tests that test only it. | Parity must be measured before a backend is retired ("measured, then governed"); no source compares D3D12, Vulkan and GL latency on show hardware [dir §1]. | A tier whose parity never arrives keeps GL there, with words. |
| D3 | **Vulkan is not carried.** | No Vulkan backend. The user-shader front end will go through SPIR-V (D7), so a Vulkan backend would stay cheap to add. | Vulkan's Windows presentation depends on each vendor; Spout, D3D12 Video and the desk's import need a DirectX bridge anyway; present timing is new and NVIDIA-first [dir §1]. | Linux render nodes as a goal again. |
| D4 | **Windows 11 24H2 (build 26100) or later on every edition — Home, Pro, Pro for Workstations, Enterprise, Education and IoT Enterprise; IoT Enterprise LTSC 2024 recommended for permanent show nodes.** (The owner's answer 1.) | The installer will refuse an older build with words; S mode (Home only) runs no installer at all, so `docs/INSTALL.md` and the download page say "switch out of S mode first". Three published tiers in `docs/INSTALL.md` and `machine.identity`: **IoT Enterprise LTSC 2024** for permanent show nodes (Shell Launcher, specialized displays, update deferral; supported to 10 Oct 2034); **Pro or Enterprise 25H2+** for desks and for nodes run with update policies; **Home 25H2+**, supported and unmanaged. PlayMap depends on no edition-only feature: on Home a node runs the engine in an auto-logged-on local standard "show" account under the supervisor (D6) instead of Shell Launcher or Assigned Access, with Explorer as the shell; its outputs are desktop monitors (no specialized displays), so the desk's monitor stays the primary display; PlayMap writes no Windows Update policy on Home (pause and active hours are the person's, and `MACHINE LOCK` observes a driver that changed rather than claiming to stop it). Preflight names each layer an edition lacks as an information row (the owner's call 9's pattern) and four as hard rows: Smart App Control enforcing while builds are unsigned (the MSI's launch condition refuses with the words before anything runs), a projector output that is the primary display, show media in an online-only OneDrive placeholder, and the show network on the Public firewall profile without a rule. Windows 10 will not be supported. Linux will keep the development loop and CI's GL suites, not shipped nodes. | 24H2 clears every feature floor the round uses (GPU upload heaps at 26080+, D3D12 AV1 encode) [dir §5]. Home runs every API the engine and desk use — Direct3D 12, DXGI flip and Independent Flip, MMCSS, QoS, high-resolution timers, services, Task Scheduler, the MSI, WER — and lacks the management layers: Assigned Access, Shell Launcher, specialized displays, the Update client policies, the policy editors [home §1]. Vendor features are gated by GPU class, not edition [home §11]. Home 24H2 left servicing on 13 Oct 2026, so a Home node runs 25H2 or 26H2 and moves on Microsoft's schedule [home §2]; the Agility SDK keeps D3D12 current on a frozen LTSC. | A shipped feature needing a later build or an edition-only feature. |
| D5 | **Hardware lock where the hardware allows it; the honest software lock everywhere else.** (The owner's answer 2.) | The frame-lock tier will join outputs through NVAPI's D3D12 present barrier wherever the outputs' adapter answers `NvAPI_D3D12_QueryPresentBarrierSupport` — NVIDIA's professional GPUs (RTX PRO Blackwell, RTX Ada, RTX A-series, Quadro RTX) with an RTX PRO Sync board — and report its sync mode and in-sync counts as the lock's evidence; a capture card's reference input (DeckLink's genlock) locks that input to house sync where the card has one, read from the card (wp3-capture); a genlocked SDI output is next round. Every other output — AMD, GeForce, Intel, any node without a board — presents free with its measured phase: `lock.tier` "software" and the words "software lock: same frame, phase +6.2 ms ± 0.3 ms". STATE will never give an output a locked state (`framelocked`, `house-locked`, `grouped`) without the barrier's own statistics, and the unused `soft-locked` state will be retired ("software" is a tier, not a state). With no lab this round, the barrier tier is built in M4's "no" shape (one swap chain per machine), proven against a fake NVAPI, and says "built, not proven on hardware". | AMD withdrew framelock in 23.Q1 and its S400 module is no longer sold; GeForce never had it; network time aligns which frame is drawn, not when it scans out [dir §2, cap §5.3]. | A vendor shipping a lock with a D3D12 entry point. |
| D6 | **A per-machine MSI built by the WiX toolset compiled from its source in CI, unsigned this round; the engine runs in an auto-logged-on show user's session under a supervising service.** (The owner's answers 3 and 4.) | A cached CI job builds the WiX toolset (v7) from its MS-RL source at a pinned release tag into a local package feed; `nuget.config` maps `WixToolset.*` to that feed only, so no official WiX binary — release, NuGet package or .NET tool — is ever restored, and nothing of FireGiant's paid offerings (HeatWave, Developer Direct, the maintenance fee) is used; no WiX issue is filed. The MSI's Util and Firewall custom-action DLLs are named with MS-RL and their source tag in `THIRD-PARTY-NOTICES.md`. If the toolset cannot be built on the runner within its one-day time-box (M17), Inno Setup 7.1 (a free EXE installer) builds the installer, every machine-level action in the supervisor's own commands. Builds are unsigned and named `-unsigned`, each artifact and release with a `SHA256SUMS` file. A small LocalSystem service (`PlayMap.Supervisor`) will start `PlayMap.exe engine` in the show user's session, restart it, and stop one whose shown-frame heartbeat has stopped while HEALTH says it should move (never during a start or a device rebuild within its measured budget). The engine will never run as administrator and never in session 0; the installer will do every machine-level action once (firewall, dump settings, privileges). | The WiX maintenance fee's EULA binds only WiX's binary releases and says a user may compile binaries from the source without it; WiX v5, the last fee-free binary line, has had no security fixes since February 2026 [inst §2]. An MSI keeps transactional rollback, services, `MajorUpgrade` and `msiexec` for IT [inst §3]. Session 0 cannot drive displays; MSIX virtualises the machine-level writes the node needs; Velopack's MSI adds no services [dir §5]. | The toolset's spike failing (→ Inno Setup); the owner choosing to sign (§9 decision 2). |
| D7 | **One HLSL source for the product shaders; user ISF out of process.** | The 40 product programs will be written once in HLSL and compiled by DXC at build time (the GLSL stays for the GL backend until it retires). User ISF will go ISF → GLSL 4.50 → glslang relaxed (`-R`) → SPIR-V → validated → SPIRV-Cross → HLSL → DXC → DXIL in a `PlayMap shader-worker` process, so no compiler loads into the engine, and will draw on the Direct3D 12 device through a root signature of its own (wp3-shaders-2); spirv_to_dxil will stay a measured alternative behind the same stage. A shader that crashed the GPU will be quarantined. | DXC's PDBs map a GPU crash to a source line in PIX and RGD; incorrectly written GLSL crashes explicit-API renderers [dir §1]. SPIRV-Cross and DXC ship as packages today; spirv_to_dxil has no package (to be built from Mesa). | spirv_to_dxil passing more of the ISF corpus than SPIRV-Cross (shaders' stretch measures it). |
| D8 | **TerraFX for Direct3D 12 and DXGI.** | `TerraFX.Interop.Windows` (raw structs and function-pointer vtables, generated from the Windows SDK and Agility headers) in `PlayMap.Render.D3D12` only; its port of the D3D12 Memory Allocator for memory, or PlayMap's own placed-resource pools if that port lags the Agility SDK. NVAPI will stay hand-declared, as today: the board's facts in Render's existing tier, the present barrier's calls in `Render.D3D12/Sync`; `PlayMap.Platform.Windows` will carry its own few read-only DXGI and NVAPI probe declarations, each with a layout golden. | The hot path allocates nothing; raw bindings keep that true and match the house's vtable approach (`ComSlots`) without hand-writing the D3D12 surface, which is far larger than the 31 methods `ComSlots` declares [map §2.7]. A wrapper binding allocates managed objects per interface. | A binding gap that blocks a must; Vortice.Windows is the named alternative. |
| D9 | **The desk keeps Avalonia,** with GPU shared-texture previews and the consoles' safety patterns. | Previews will move from four CPU copies to a shared texture and fence per view where the desk's compositor shares the engine's adapter; the CPU ring will stay for every other case. | Avalonia 12.1 is current and imports D3D11 NT-handle textures and D3D12 fence handles; WinUI 3 would rewrite every view for one asset [dir §9]. | Avalonia's backend not importing either at run time (§1.5, M2): the ring stays, and only the GPU path waits. |
| D10 | **What the round refuses.** | DLSS frame generation (synthesised frames under their own pacing contradict content as a pure function of show time); adaptive resolution (it breaks blend seams); VRR on any output in a lock group or feeding genlocked processors; shipping changed TDR registry keys (long GPU work is split instead); `GC.Collect` at show arm (banned in `src/`; a case for it needs a measured cause and a test). | [dir §2, §4, §5, §7, §8] | A measured cause and a deterministic test. |
| D11 | **The desk's shell: panes that dock, float inside the desk window and collapse; one action table reached from the menu bar, the Ctrl+K palette and right-click menus generated from the catalogue; a console for typed lines.** | Dock for Avalonia (`Dock.Avalonia`, MIT, its Avalonia-12 line), pinned exactly and wrapped behind a desk seam, with managed floating windows only, so no pane can land on an output; layouts per workspace and per guide in the user's `desk.json`. Every catalogue row the desk may send is reachable from a right-click menu of the kind it names, or from the bar or the palette, and every right-click entry is also in the bar or the palette (`MenuCoverageTests`). The console types any line through the desk's gate, a secret-bearing row refused; the desk's rule "no line typed by hand" gives way to the owner's "programming areas". The held rules stay, `VIDEO STOP`, `PANIC CLEAR` and `MASTER` sent at every press (the owner's call 7). | The owner's requirements (draggable, collapsible panels; every feature from a menu or an interactive right-click menu; programming areas); Microsoft's rule that no command lives only in a context menu; Avalonia ships no docking control [ux §3.1–§3.2]. | Dock not running on the desk's Avalonia (M18 → the desk's own splitters and rails, no floating). |
| D12 | **Every mapping step can be done by hand, and every automatic result is a layer a hand edit sits on or a value it records having written.** | A re-run replaces its own layer only and lists the hand edits it kept; over a value typed by hand since, a projector solve stages and every other step refuses unless `KEEP` or `REPLACE`; `HAND` and `HAND CLEAR` say and clear the hand layer; every edit through `ProjectStore`, one undo step each (`design/desk-workflows.md` §3). Align's measured γ moves from `Blend.Gamma`, a field a person types, into the measured layer. | The owner: "All mapping can also be manual, from initial setup to adjusting the auto scan and setup"; Mapping Matter does not overwrite a value the user typed [`docs/UX.md` P10]; one writer. | None expected. |
| D13 | **The assistant is in every workflow through the verbs, STATE and context the desk uses: guides are engine-side step lists, and the assistant proposes a step's lines and carries them out only on a person's approval.** | `GUIDE` answers each guide's steps (state, words, facts, the automatic, hand and proposed lines) from pure planners in `Core/Workflows`, `SITE GUIDE`'s shape; STATE `guides` and the context's guide items; `plan_recipe guide_step`; the desk's assistant pane with Show me, Approve and Reject; "Ask about this…" on every object; a person's Check refused from an assistant. Every new row and STATE block reaches MCP, the prompt and the context with no package code. | The owner: "Ai integration throughout"; the assistant changes the show only through the gate today and its proposals wait for a person [`docs/UX.md` §10]. | None expected. |
| D14 | **Capture cards join the video I/O hub as providers loaded at run time and never redistributed; a pass-through output runs one frame in flight outside the delay ring; every latency figure is measured or "unmeasured".** | Media Foundation and DeckLink in the must, AJA and Magewell as stretch; the rungs from card DMA into a wrapped D3D12 heap (no CPU copy) to one upload copy; packed 4:2:2 unpacked on the GPU; `OUTPUT PASSTHROUGH`; `LATENCY LOOP` (`design/capture.md`). GPUDirect for Video and DirectGMA wait for a measured cause. | The owner: "Video capture cards should be ultra fast in their pass through signal flow"; a mapped pass-through's floor is one input frame plus the render [cap §0]. | A loop-through measurement showing the copy is the bottleneck. |
| D15 | **No lab this round.** (The owner's answer 5.) | Every lab line (§7) stays with its script and pass mark and is reported not run, never passed; every question only the lab answers (M1–M5, M9's proof, M10–M13, M20) stands on its "if no" branch all round, said in STATE and the papers; AUTO stays OpenGL on every machine; nothing claims a hardware fact it has not measured. | "Measured, then governed": WARP and the simulators prove logic and pixels, not vblanks, drivers or light. | A lab booked (§9 decision 11). |
| D16 | **Colour carries three separate meanings — status, identity, stage — and none is the only cue.** | Status (red, amber, green, grey with an **i** for information) reserved for its job; identity in muted colour-blind-safe hues with a number badge, never a status hue (today's projector orange goes); stage as 2–4 % low-chroma tints; tested from each theme's tokens (`design/desk-workflows.md` §11). | ISA-101 and IEC 60601-1-8 reserve alarm colours; colour alone fails colour-blind operators [ux §4]. | None expected. |

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
| The WiX toolset built from its source at a pinned tag, the local feed the only source of `WixToolset.*` | — | ✓ (cached by the tag's commit; M17) | — |
| MSI build, install, upgrade, downgrade refused, uninstall, service, firewall, dump settings | — | ✓ (the runner is elevated) | IoT Enterprise LTSC 2024, Home 26H2, auto-logon (L11, L23) |
| Unsigned builds: the `-unsigned` name, `SHA256SUMS` beside each artifact | — | ✓ (a named notice in the summary) | SmartScreen and Smart App Control on clean Home and Pro (L18) |
| Windows editions: the Home branches, the edition's rows, Smart App Control's launch condition | ✓ (fake editions) | ✓ (fake editions; the Server SKU's own answers) | a Home node (L23) |
| The desk: panes and layouts, menus against the catalogue, the palette and console, the quad's draft, every handle typed and nudged, what each screen shows, colour tokens, the guides' rails | ✓ (headless Avalonia) | ✓ | three operators (L19), the real rigs (L24) |
| The guides, `CANVAS SPAN`, the hand layers, the assistant through a scripted provider, MCP's rows | ✓ (the align and stage simulators) | ✓ | a two-projector blend and stack (L24) |
| A spanning pattern and the blend's layers on both backends | ✓ (GL) | ✓ (WARP) | L24 |
| Capture providers against fakes; the pass-through output; the wrapped heap and the packed unpacks | ✓ (fakes, GL) | ✓ (WARP: M16; a Media Foundation virtual camera if M19 allows) | a capture card on a loop-through (L25) |

The development loop runs on Linux, so every Direct3D 12 slice will keep its logic pure where it can be and its Direct3D calls
thin, and will read its pushed branch's Windows job (`gh run view`) before it says it is done. A failure is read from Linux
through the job's artifacts: the spine's `workflow_dispatch` input runs one filtered Direct3D 12 leg and uploads its diff images,
the info-queue log and DRED's output, with the leg's minutes.

### 1.5 Measure first

The research could not settle these. Each has a cheap measurement, a deadline (the package whose slice it changes), and a plan for
both answers, so no slice waits on a hope. With no lab this round (D15), every question only the lab answers — M1, M2, M3, M4, M5,
M9's proof, M10, M11, M12, M13 and M20 — stands on its "if no" column all round, and STATE and the papers say so; the CI questions
(M6–M8, M14–M19) are answered on their packages' first days as planned.

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
| M16 | Does WARP accept `OpenExistingHeapFromAddress` over a `VirtualAlloc` ring, placed buffers on it, and a copy-queue copy and a compute unpack from it, byte-exact for UYVY and v210? | wp3-capture's first day (`WrappedHeapTests`) | wp3-capture | Rung 1 (card DMA into the wrapped heap) built, proven on WARP, waiting for a real card's DMA (M20) | Rung 2 (one upload copy) is the best rung on every card, the refusal's words recorded |
| M17 | Does the WiX toolset build from its MS-RL source at a pinned tag on `windows-2025` within a one-day time-box, and does the self-built tool still ask for the maintenance fee's EULA gesture? | wp3-installer's first day: build the tag, then a test MSI with a service, a `MajorUpgrade` and a firewall rule installed, upgraded and uninstalled on the runner; build time and cache size recorded | wp3-installer | The self-built toolset in a cached job and a local feed (D6) | Inno Setup 7.1, every machine-level action in the supervisor's commands, §1.3's rules restated for an EXE; if the self-built tool asks for the gesture, the owner decides (§9 decision 3) |
| M18 | Does Dock for Avalonia (its 12.1 line) run on the desk's Avalonia 12.1.4 headless, keep floating panes inside the window, and round-trip a layout through its source-generated serializer? | wp3-desk-shell's first day | wp3-desk-shell | Dock behind the desk's seam (D11) | The desk's own splitters and collapsible rails behind the same seam, no floating panes |
| M19 | Does the hosted runner let a test create a Media Foundation virtual camera (`MFCreateVirtualCamera`) that the capture provider opens? | wp3-capture's first day | wp3-capture | `MfProviderTests` on a real MF source in CI | The MF provider against a fake source-reader seam; the real MF path is L25's |
| M20 | Does a real capture card DMA into pages Direct3D 12 has wrapped, without fault or tearing, and how fast does the GPU read them? | L25 (lab) | after wp3-capture | Rung 1 on that card | Rung 2 on that card, said |

M1–M5 are the four results the direction note says decide the schedule, plus Reflex; M6–M13 are what the map found the tree
needs answered; M14–M15 are what the Windows job's own evidence rests on; M16–M20 are what the owner's requirements and answers
added.

### 1.6 Done means, for the round

Round 3 is done when:

1. Every package's must is merged to `main` with both CI jobs green and the analyzers on, under §2.1's per-package rule.
2. On the Windows job, on WARP: the Direct3D 12 backend draws every scene of the pixel suites, user effects included, through the
   compiled render graph within each scene's row of the D3D12 tolerance table (every widening recorded with its measured
   difference and reason; packed video byte-exact), with the debug layer and GPU-based validation on (as M15 decided) and no
   message; aliasing on and off draw the same pixels; warm frames allocate nothing; every pipeline the armed show can reach is
   compiled before ARM answers, or ARM's answer names each one missing with preflight red; a forced device removal is rebuilt by
   the start-up path to a good frame, its time recorded; the installer — the MSI from the WiX toolset built from its source, or
   Inno Setup's if M17 said no — builds, installs, upgrades from the previous build, refuses a downgrade and uninstalls; `main`'s
   artifact is named with its version and `-unsigned`, with its `SHA256SUMS`.
3. On Windows with `PRESENT PATH D3D12`, a show runs end to end on the Direct3D 12 backend: outputs on flip presenters, decode by
   D3D12VA or the copy-queue arena, user ISF effects compiled by the worker, Spout through the side device, previews shared with
   the desk where its compositor allows, the observer reading an output back. AUTO is still GL until M1's record, and STATE says
   why.
4. On Linux the GL backend draws every golden byte for byte as it did at `23f162f`, and every suite that needs no Direct3D is
   green.
5. No claim without evidence: every output says its present mode and how it is known; no output without the barrier's own
   statistics is in a locked state; every platform claim says what it asked and what Windows answered; every shared preview says
   which path it took.
6. The lab checklist (§7) is written with a script and a pass mark for every manual line; with no lab (D15) every line is reported
   not run, never passed, every question only the lab answers stands on its "if no" branch with STATE and the papers saying so, and
   no paper claims a hardware fact it has not measured; the papers (the integration pass, §5.6) describe what shipped in the present
   tense.
7. On the desk, headless on both jobs: the three guides run end to end on the align and stage simulators — every step done by hand,
   every automatic step's result adjusted by hand and a re-run keeping that edit — the quad's draft survives leaving it, every handle
   is dragged, typed and nudged by one pixel, every view of an output says what it shows, every catalogue row the desk may send is
   reachable from a menu or a right-click (`MenuCoverageTests`), layouts persist per user, and the colour tokens keep their rules;
   through a scripted provider the assistant reaches every guide step and carries it out only on a person's approval.
8. A capture input through a fake provider passes through on one frame in flight with no CPU wait and its stamps exact; WARP unpacks
   UYVY and v210 byte-exact from the wrapped heap, or M16's words say why the upload copy is the best rung; every glass-to-glass
   figure in STATE is "unmeasured" until a loop-through has run.

---

## 2. Ownership rules

### 2.1 The capacity rule

| Rule | What it means |
|---|---|
| **One session, one slice** | A package is built by one implementation session, then one review-and-fix session (fixes only, no new scope). |
| **The budget** | Above its cut line (⟂) a slice is about 20–26 hours of work, after what the spine already lands. Below the line, stretch items total at most about 8 hours. |
| **Order is the contract** | Slice items are built in the listed order. The must above ⟂ merges on its own. A stretch item starts only when the must is green; an unfinished stretch item is not merged half-built: it moves to "Next round" and its catalogue rows are trimmed. |
| **Done means (a package)** | Merged to `main` with both CI jobs green and the analyzers on; its acceptance tests pass (here, on the Windows job, or marked manual with the lab line that will check it); its fixture `tests/fixtures/state/<pkg>.json` is written by one of its own tests, with a card descriptor `<pkg>.cards.json` for each new STATE block (the desk renders it, and its actions become the object's right-click entries); its catalogue block holds only rows it routes; it registers at least one `IPreflightCheck` with a fix line, tested by injecting a fault, where it owns a show resource; it contributes an INDEX section where it adds an entity a controller can act on; `docs/<AREA>.md` and `docs/wire/<pkg>.md` describe what shipped, with a papers note for the integration pass; the scripts of its lab lines (§7) are in `docs/round3/lab/`, inside its budget. With no lab (D15), "marked manual" means the line is reported not run and the package's papers say what CI and WARP proved and what waits. |
| **AI reach** | Every verb a package adds is a catalogue row, so VERBS, HELP, MCP's `list_verbs` and `run_command`, the assistant's prompt and `propose_commands`, the context's commands and the Companion module's `verbs.json` carry it with no code of the package's own (`CatalogueTests`, `PromptTests`); every STATE block it adds reaches `get_state` and MCP's state resource. A package that adds a step a person takes on the desk or in a guide also adds an assistant test: the Assistant suite's `ScriptedProvider` proposes that step's lines, the gate's verdict is the class's (queued while armed, a high-risk line waiting for a person), and the result is read back from STATE. |
| **The desk's doors** | Every desk control a package adds names its catalogue line, is in a right-click menu and in the menu bar or the palette (`MenuCoverageTests`, from wp3-desk-shell's merge on), and is greyed with the node's words where the gate refuses it. |
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
| `Handles` | wp3-desk-map | `EngineCore.Handles.cs`, `EngineCore.Showing.cs` |
| `Canvas` | wp3-canvas | `EngineCore.Canvas.cs`, `Rendering/RenderHost.Canvas.cs` |
| `Guide` | wp3-workflows | `EngineCore.Guide.cs`, `EngineServices.Guide.cs` |
| `Capture` | wp3-capture | `EngineCore.Capture.cs`, `EngineServices.Capture.cs`, `Host/EngineParts.Capture.cs`, `NodeSettings.Capture.cs`, `Rendering/RenderHost.Capture.cs` |

wp3-desk-shell is the desk's alone and has no engine stub.

Present and framelock keep the existing Output and Sync files (`EngineCore.Output.cs`, `EngineCore.Sync.cs`,
`Presentation/*`), owned by them in their waves.

---

## 3. The packages

### 3.0 Overview

"Where" says where the session will run: **here** (this machine, under §5.2's limits) or **cloud** (its own container; its
Windows evidence comes from the pushed branch's Windows job either way). Hours are the slice's must above ⟂.

| # | Branch | Hook word | Wave | Depends on | Where | h | Round-3 headline |
|---|---|---|---|---|---|---|---|
| 0 | `wp3-spine` | — | W0 | `main` with `versioning` merged | here | 26 | Round-3 rows (the new packages' too), Core contracts and the presenter types in Core, model fields, the seams already known (`UNDO TO` in the gate, the heartbeat's fields, the arm hook), the new projects, the WARP and ES legs in CI, the M1/M3 bench, stubs, the edition family |
| 1 | `wp3-rhi` | — | W1 | spine | here | 26 | The RHI in Core with the presenter seams, the program table, user programs and the graph's transients; GL behind it with unchanged pixels; one test fixture for every backend |
| 2 | `wp3-platform` | Machine | W1 | spine | cloud | 27 | `PlayMap.Platform.Windows`: QoS, timers, MMCSS, process and GPU priority, CPU sets, keep-awake; the node's identity, edition, support tier and capabilities; machine preflight with Home's rows |
| 3 | `wp3-shaders` | Shaders | W1 | spine (rhi before merge) | cloud | 25 | The 40 programs in HLSL through DXC; the shader worker for user ISF; quarantine |
| 4 | `wp3-installer` | — | W1 | spine | cloud | 26 | The MSI version rules, the LGPL FFmpeg in CI, the WiX toolset built from its source and the MSI with ReadyToRun, machine-level actions, the supervisor service, a fail-closed armed check, unsigned builds with their SHA-256 list, the editions' guidance |
| 5 | `wp3-desk-ux` | Undo | W1 | spine | cloud | 26 | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status rules, preview age, the M2 bench, the four-click quad that stays and drags |
| 6 | `wp3-d3d12-core` | Gpu | W2 | rhi, shaders | cloud | 26 | Device, queues, fences, memory, descriptors, transients and aliasing, the exporter, DRED and Aftermath, the pipeline cache; every product pixel scene on WARP |
| 7 | `wp3-d3d12-passes` | Plan | W2 | rhi (d3d12-core before merge) | here | 24.5 | Every pass declares what it reads and writes; the render graph compiled on show change; both backends walk the plan; a live input's import marked late-latched |
| 8 | `wp3-stability` | Observe | W2 | rhi | here | 23 | The watermark, the independent observer, the shown-frame heartbeat, `LATENCY STEP`, the crash policy, the journal's drops counted |
| 9 | `wp3-present` | (Output) | W3 | d3d12-core, stability | cloud | 25 | D3D12 flip presenters, Independent Flip evidence, one queued frame, the predicted display time, HDR10 and 10-bit, the Engine suites on D3D12 |
| 10 | `wp3-video` | (Decode) | W3 | d3d12-core | cloud | 24 | D3D12VA zero-copy, the copy-queue arena, Spout on a D3D11 side device, NDI and OMT on the copy queue at arrival, decode tiers |
| 11 | `wp3-desk-gpu` | Preview | W3 | d3d12-core, desk-ux, desk-shell | here | 20 | Shared-texture previews with a fence, the adapter guard, the ring kept, the desk-kill test |
| 12 | `wp3-d3d12-passes-2` | (Plan, Gpu) | W3 | d3d12-passes, d3d12-core | here | 21 | Pipelines compiled at arm and keyed by driver, bindless records, zero allocation on D3D12, GPU time per pass |
| 13 | `wp3-framelock` | (Sync) | W4 | present; M4's "no" shape (no lab) | cloud | 23 | The present barrier where the hardware allows it, the honest software lock, the governed fall-out, timing preconditions with ADLX's FreeSync read |
| 14 | `wp3-stability-2` | (Gpu, Observe) | W4 | present, d3d12-passes-2 | cloud | 21 | One rebuild path, rehearsed device removal, repeated loss governed, the degrade order, GPU crash evidence |
| 15 | `wp3-shaders-2` | (Shaders) | W3 | shaders, d3d12-core (d3d12-passes-2 before merge) | cloud | 22 | User ISF effects on the Direct3D 12 device, the GPU-time guard, the ISF corpus on the new road |
| 16 | `wp3-desk-shell` | — | W2 | desk-ux | cloud | 26 | Docked, floating and collapsible panes; the menu bar, the palette and the console from one action table; right-click menus from the catalogue; colour in three channels; the assistant's pane |
| 17 | `wp3-desk-map` | Handles | W3 | desk-shell, d3d12-passes-2 (before merge) | here | 20 | Every handle selected, dragged, typed and nudged by one pixel (`PIN MOVE`, `WARP CORNER`, the PX forms); the projector view's zoom and loupe; what each screen shows |
| 18 | `wp3-canvas` | Canvas | W4 | d3d12-passes-2, present | here | 15 | `CANVAS SPAN` and `CANVAS SOURCE`; one pattern drawn across a canvas; every output's automatic results a layer under the hand's (`HAND`, align's γ out of the hand's field) |
| 19 | `wp3-workflows` | Guide | W4 | desk-map, canvas (before merge) | cloud | 23 | The three guides and `GUIDE`; poses and scans keeping hand edits; `VIEWPOINT FIT`, `SURFACE MAP`; the Workflows home and Simple that hides; the assistant in every step |
| 20 | `wp3-capture` | Capture | W4 | video, present (framelock before merge) | cloud | 23 | Capture cards in the video I/O hub (Media Foundation, DeckLink), the wrapped-heap and upload rungs, packed 4:2:2 on the GPU, the pass-through output, the frame rate and lock, latency measured |

Twenty-one sessions, about 490 hours above the cut lines: the sixteen packages the owner kept ("All 16"), about 386 hours with what
their answers change in them, and five packages — 16 to 20 — that this revision of the plan proposes for the owner's requirements of
18 October 2026, about 107 hours. The owner kept sixteen before those requirements were given; the five are the plan's proposal, and
§9 decision 1 says how they go to the next round if the owner keeps the round at sixteen. The designs that go beyond this file:
`design/rhi.md` (wp3-rhi), `design/d3d12.md` (wp3-d3d12-core), `design/graph.md` (wp3-d3d12-passes and -2), `design/shaders.md`
(wp3-shaders and -2), `design/present.md` (wp3-present and wp3-framelock), `design/platform.md`, `design/desk-gpu.md`,
`design/stability.md` (wp3-stability and -2), `design/installer.md`, `design/desk-workflows.md` (desk-ux's item 7, wp3-desk-shell,
wp3-desk-map, wp3-canvas and wp3-workflows) and `design/capture.md` (wp3-capture). Video and the rest of desk-ux are specified here in
full.

Each section: the slice in order (⟂ is the cut line; items after it are stretch), what it adds to preflight and INDEX, the files
it owns, the hunks it may add to others' files, its dependencies, its acceptance tests, what is verified here, on CI and in the
lab, and its next round.

### 3.1 `wp3-spine` — the contracts every package builds on (W0)

**Slice.**
1. The base: `main` with the `versioning` branch merged (§5.1); the spine rebases on it and checks `BuildInfo.Informational`
   reaches the hello, STATE's `build` and the journal header (adding it where the branch did not). (1 h)
2. The catalogue: a `V3` helper (`Round = 3`, answered "not available on this node yet" until its owner routes it), every row of
   the table below with its syntax, words, risk, show class and owner's block comment; `VIEW` gains `[SHARED <pid>]` and
   `PRESENT PATH` gains `D3D12` (refused with words until their owners route them); the extended rows of the five packages the
   owner's requirements add (`PATTERN … ON <canvas>`, `WARP NODE … PX`, `HANDLE SELECT` of every handle, `INPUT DISCOVER CAPTURE`,
   `INPUT ADD`'s `MODE` and `FORMAT`) the same way; `RoutesTests` and `CatalogueTests` cover the rows. (4 h)
3. Contracts two or more packages share: `GpuApi.D3D12`; `PresentPath.D3D12Flip` and its word (`d3d12-flip` in STATE; on the
   wire a D3D12 flip presenter reports PMVB path 2, `dxgi-flip`, the presentation model it shares with the D3D11 path, so mixed
   builds in a group read each other); the PMVB reader learns path 3 (`dedicated`, wp3-present's stretch) and still refuses 4
   and above; `NodeIdentity`, `AdapterIdentity`, `CapabilitySnapshot`, `CapabilityProbe`, `ThreadRole`, `IThreadClaim` and the
   static `ThreadClaims` (Core/Machine: a thread in Media, Net or the engine claims its role in one line, a no-op until the engine
   points it at its platform), with `ThreadRole.Capture` for capture threads and `OsIdentity`'s edition family (`home`, `pro`,
   `workstation`, `enterprise`, `education`, `iot`) beside its LTSC and IoT flags; the engine seams of `Seams.Round3.cs` (`IMachinePlatform`, `IAirObserver`, `IGpuLifecycle`,
   `IPreviewShare`, `IShowArmHook`) with null implementations that say why. The presenter-facing types move from Render to
   `src/PlayMap.Core/Presentation` with their names and behaviour (`VblankClock`, `PresentLedger`, `PresentClock` with
   `PresentSnapshot`, `PresentMode` and `ModeEvidence`, `LatencyPlan`, `PresentPath`, `PresentProblem`, `PresentResult`), so a
   Direct3D 12 presenter in `Render.D3D12` feeds them without referencing Render (wp3-rhi moves `IGpuPlatform` and
   `IOutputPresenter` when it reshapes them). (5 h)
4. The model's round-3 fields, backward compatible (init properties with defaults): `Output.ColorBits` (8 | 10), `Output.Hdr`
   (off | hdr10 | scrgb) and `Output.NitTarget`, `Output.FramesInFlight` (0 = auto, 1, 2), `Output.Watermark` (null, or place
   and size); for the owner's requirements `Output.PassThrough`, `MeasuredCorrection.Gamma` (the measured γ align writes, drawn
   under the hand's γ), `Canvas.Span` (the outputs, layout, overlap and what a span wrote) and the calibration source `Hand`;
   `ModelCompatTests` read every round-2 fixture project unchanged. (3 h)
5. The tree: `src/PlayMap.Render.D3D12` (references Core; TerraFX; every type `[SupportedOSPlatform("windows")]`; its folders as
   `design/d3d12.md` §0 lists them), `src/PlayMap.Platform.Windows` (references Core), `src/PlayMap.Supervisor` (references Core),
   their test projects, the solution's entries (the WiX project stays outside `PlayMap.sln`: an MSI builds on Windows only, so the
   Windows job's MSI step builds it); `ModuleRuleTests` (each new project references Core and its own native packages only;
   Render.D3D12 does not reference Render; only App references Avalonia); the stubs of §2.3, each empty body holding
   `// wp3-<pkg> fills this.`; the new STATE keys reserved in their place — `identity`, `capabilities`, `platform`, `locked` and
   `install` as children of the existing `machine` block, after its present keys; `render`, `observe` and `previews` at the top
   level; for the owner's requirements `guides` at the top level, `outputs[].showing`, `outputs[].hand`, `outputs[].passThrough`,
   `media[].shownOn`, `videoio.inputs[].capture` and `.latency` and `air.overlay.handleP95Ms` after their blocks' present keys, and
   the context's `guide` item kind; empty fixtures `machine.json`, `install.json`, `shaders.json`, `gpu.json`, `graph.json`,
   `observe.json`, `previews.json`, `showing.json`, `canvas.json`, `guides.json` and `capture.json` with their card descriptors; `docs/wire/<pkg>.md` generated from the catalogue; one heading per package in
   `THIRD-PARTY-NOTICES.md`. (5 h)
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

⟂ *(≈ 26 h)*

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
| `PIN MOVE` | `PIN MOVE <pin> TO\|BY <x> <y> [PX]` | desk-map | Review | Target (a gesture) |
| `WARP CORNER` | `WARP CORNER <output> TL\|TR\|BR\|BL TO\|BY <x> <y> [PX\|NORM]` | desk-map | Review | Layout (a gesture) |
| `WARP NODE` (extended) | `… \| WARP NODE <output> <c> <r> TO\|BY <x> <y> PX` | desk-map | as `WARP NODE` | as `WARP NODE` |
| `HANDLE SELECT` (extended) | `… \| HANDLE SELECT <pin> \| HANDLE SELECT <output> WARP TL\|TR\|BR\|BL\|<c>,<r> \| HANDLE SELECT <output> MASK <i> <node>` | desk-map | as `HANDLE SELECT` | as `HANDLE SELECT` |
| `CANVAS SPAN` | `CANVAS SPAN <canvas>\|NEW <output> <output>… [SIDE <c>x<r>\|STACK] [OVERLAP <px>\|<pct>%] [KEEP\|REPLACE]` | canvas | Review | Layout |
| `CANVAS SOURCE` | `CANVAS SOURCE <canvas> <source> [FIT\|FILL\|STRETCH]` | canvas | High | Content |
| `PATTERN` (extended) | `PATTERN <kind>\|OFF [ON <output>\|<canvas>\|ALL]` | canvas | as `PATTERN` | as `PATTERN` |
| `HAND` | `HAND <output>\|<projector>\|<surface>` | canvas | Low | Query |
| `HAND CLEAR` | `HAND CLEAR <output> WARP\|BLEND\|COLOR\|ALL` | canvas | Review | Layout |
| `GUIDE` | `GUIDE LIST \| GUIDE FLAT\|WALL\|OBJECT\|FACADE\|FIRST [<subject>]` | workflows | Low | Query |
| `GUIDE CHECK` | `GUIDE <kind> <subject> CHECK\|UNCHECK <step>` (a person's mark: refused from an assistant's session) | workflows | Review | Project |
| `VIEWPOINT FIT` | `VIEWPOINT FIT <viewpoint> <surface>… [MARGIN <pct>] [FOV]` | workflows | Review | Target |
| `SURFACE MAP` | `SURFACE MAP <surface> UV \| CANVAS <canvas> [<x> <y> <w> <h>] \| PROJECTION FROM <viewpoint>` | workflows | Review | Content |
| `INPUT DISCOVER` (extended) | `INPUT DISCOVER [NDI\|OMT\|SPOUT\|CAPTURE\|ALL] [WAIT <0-5000 ms>]` | capture | Low | Query |
| `INPUT ADD` (extended) | `… [MODE <w>x<h>@<rate>] [FORMAT UYVY\|V210\|NV12\|AUTO]`, `capture:` URIs | capture | as `INPUT ADD` | as `INPUT ADD` |
| `OUTPUT PASSTHROUGH` | `OUTPUT PASSTHROUGH <output> ON\|OFF` | capture | Review | Setup |
| `LATENCY LOOP` | `LATENCY LOOP <output> VIA <input> [STEPS <3-60>]` | capture | High | Heavy |

Rows whose replies or words grow, without a new row: `OUTPUT STATS` (mode, evidence), `SYNC STATUS` (barrier, phase), `HEALTH`
(shown and observed frames, the GPU's phase), `FX CHECK` (the Direct3D 12 path's stages), `DECODE STATUS` (D3D12VA), `PREFLIGHT`
(every package's rows), `SITE GUIDE` (the FACADE guide's steps, the same list), `ALIGN ACCEPT`, `STACK ALIGN`, `STACK MATCH`,
`BLEND SOLVE` and `SCAN APPLY` (the hand edits each kept), `PROJECTOR SOLVE` and `CALIBRATION RESOLVE` (staged over a hand result).

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
   screen saver, which that call does not stop, read as a fact; the capture role (`ThreadRole.Capture`) claimed as MMCSS's Capture
   task on the critical pool, for wp3-capture's threads. (1 h)
4. The node's identity and capabilities: OS edition, its family and build and revision (Home, Pro, Pro for Workstations,
   Enterprise, Education, IoT Enterprise and LTSC named, from `GetProductInfo` and `EditionID`), the support tier the edition gives
   (`docs/INSTALL.md`'s three), the CPU (model, cores
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
   plan (on a machine that offers only Balanced, its power mode and the settings that matter, never "not High Performance"), sleep
   and display timeouts, the screen saver, a pending reboot, each claim of items 1–5 that Windows refused, the timer's lateness, and
   drift from `MACHINE LOCK`'s set (amber, naming the old and new versions, fix `MACHINE LOCK`; on Home, where drivers cannot be held
   back, it observes the change and says so). The edition's rows (D4): each layer the edition lacks an information row in grey with
   **i** ("updates cannot be deferred on Home: pause them past the show in Settings ▸ Windows Update"; "no kiosk on Home"; "outputs
   are desktop monitors here: keep the desk's monitor primary"), and the hard rows — Smart App Control enforcing while the build is
   unsigned (red, read from `CI\Policy\VerifiedAndReputablePolicyState`; evaluating: amber, "it may start blocking this unsigned
   build"), a projector output that is the primary display (amber), the show adapter on the Public firewall profile with no rule
   for it (amber). (4 h)

⟂ *(≈ 27 h)*

7. *(stretch)* Preflight's remaining rows: USB selective suspend, PCIe link-state power management, notifications, the Windows
   Update deferral policy (on the editions that have it), HAGS, Memory Integrity, Game Mode and each media volume's encryption as
   facts.
8. *(stretch)* The GC's `SustainedLowLatency` while the show is armed through `IShowArmHook`, settable (`GC SHOW`) and DEFAULT, with
   a GC pause histogram (EventPipe) in STATE beside the frame time — the measurement that default waits for (L10; with no lab it
   stays DEFAULT all round).

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
- `IdentityTests`: the record's keys and types from a fake platform, the tier and support level per vendor; every edition family
  from a fake `GetProductInfo` and `EditionID` (`Core`, `CoreSingleLanguage`, `Professional`, `ProfessionalWorkstation`,
  `Enterprise`, `EnterpriseS`, `Education`, `IoTEnterpriseS`) and its support tier; `machine.json` written; a locked set against a
  changed GPU driver → an amber row naming both versions with the fix line `MACHINE LOCK`.
- `EditionRowTests`: on a fake Home the edition's information rows with their Settings paths and no Update policy written; Smart
  App Control enforcing on an unsigned build → red, evaluating → amber; a projector output that is the primary display → amber; the
  show adapter on Public with no rule → amber; on a fake IoT Enterprise LTSC none of Home's rows.
- `MachineVerbTests`: `MACHINE SET GPU-PRIORITY REALTIME` without the privilege → refused naming it; armed → refused by the class;
  `MACHINE SET QOS HIGH` when it is → refused "already high"; `MACHINE LOCK` on the locked set and `MACHINE LOCK CLEAR` with none →
  refused with the reason, nothing written.
- `PreflightMachineTests`: each row fails on an injected fake fact and passes once fixed; every fix line parses.
- On Linux the null platform's words ("the platform seam is Windows only") in STATE and preflight.

**Verified here.** Policy, placement, verbs, STATE, preflight, against fakes. **On CI (Windows).** Every call answers, read back
where the Server SKU allows. **Manual (lab).** L10: p99.9 frame time with and without each claim on an NVIDIA, an AMD and an Intel
machine — the measured cause that turns each default on or off; a hybrid Intel CPU's placement; a dual-CCD X3D CPU. L22: a precise
Windows thread time — a thread's cycles at the time-stamp counter's frequency from the platform against its running time, with
Memory Integrity on and off, and the ticks' step under the node's own timer claims. L23: the edition's rows and the claims on a clean
Windows 11 Home 26H2 node. With no lab (D15) all three are reported not run; the defaults with no documented cause stay off.

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
2. The WiX toolset from its source (D6, M17): a `wix-toolset` job in the installer's CI block checks out WiX's MS-RL source at a
   pinned release tag (v7), builds the SDK and the Util and Firewall extensions, and caches them by the tag's commit with a version
   suffix of PlayMap's own; `nuget.config` maps `WixToolset.*` to that local feed only, so a restore can never fetch an official
   package; no WiX release, NuGet package, .NET tool, HeatWave or Developer Direct is used, no EULA property is set and no WiX
   issue is filed. The first day's time-box is M17: the tag built, a test MSI with a service, a `MajorUpgrade` and a firewall rule
   installed, upgraded and uninstalled on the runner, the build's minutes and the cache's size recorded; if it fails, Inno Setup 7.1
   (§1.5) takes items 3–4's place with the same acceptance, restated for an EXE. (3 h)
3. The MSI (the SDK project `installer/PlayMap.Installer.wixproj`, outside `PlayMap.sln` because an MSI builds on Windows only,
   built by the Windows job's MSI step against item 2's feed): per machine into Program Files\PlayMap; the self-contained publish
   with ReadyToRun (`PublishReadyToRun`: M13's default with the pre-roll, the first ten minutes recorded in L21) harvested; FFmpeg's
   LGPL shared libraries (item 1's pin) and libvmx beside the exe (today neither is in the folder [map §6.2]); the Agility SDK
   runtime under `D3D12\` (its debug layer left out of releases); the shader worker's natives; launch conditions of Windows 11 build
   26100 or later on x64 (a registry search, not `VersionNT`) and of Smart App Control not enforcing while the build is unsigned
   (`CI\Policy\VerifiedAndReputablePolicyState` 1 → "Smart App Control blocks unsigned programs and this build is unsigned: turn it
   off in Windows Security ▸ App & browser control, or wait for a signed build"); `MajorUpgrade` with no same-version upgrade and a
   downgrade refused with words; the product's name carrying the SemVer. (8 h)
4. Machine-level actions, once, so the engine never runs as administrator: firewall rules for `PlayMap.exe` (TCP 9940, 9941; UDP
   9928, 9942, 9943, 6454, 5568, 5353) on the private and domain profiles, the Public profile a property the person sets (a new
   network is Public by default [home §12]); no URL ACL (Kestrel binds its own sockets — asserted by a standard user binding 9940);
   mDNS checked on the runner (`_playmap._tcp` browsable beside Windows' own responder on 5353, the answer in the summary); WER
   LocalDumps for `PlayMap.exe` (`%ProgramData%\PlayMap\dumps`, full, five kept, the show user's write ACL); the show account granted
   `SeIncreaseBasePriorityPrivilege` (the realtime GPU queue, used only when measured) by the supervisor's `grant` command
   (`LsaAddAccountRights`, as on every edition) as a deferred custom action; the Event Log source. (4.5 h)
5. The supervisor (`PlayMap.Supervisor`, a LocalSystem service with recovery actions on crashes and on non-zero stops): starts
   `PlayMap.exe engine` in the auto-logged-on show user's session (`WTSQueryUserToken`, `CreateProcessAsUser`; M9 — the same on
   every edition, and on Home the only kiosk there is), restarts it with backoff (1 s doubling to 30 s), and stops an engine whose
   shown-frame heartbeat has not advanced for the stale time while HEALTH's `gpu.phase` says `running` (HEALTH over loopback: the
   fields the spine reserves, wp3-stability fills) — the stale time the larger of 3 s and four times the rebuild's first-good-frame
   time the Windows job records, a start or a rebuild exempt up to that budget, and the rule inactive, said in the status file,
   while the count is unknown (−1) — and journals why; it stops trying after five restarts in ten minutes and says so in a status
   file the engine, the desk and the fleet read; the desk attaches to a supervised engine instead of starting its own; the
   installer's `preinstall-check` fails closed — no engine process: the install goes on; an engine answering armed: refused with the
   node's words; an engine process alive that does not answer: refused with words, never read as "not armed". (6 h)
6. Unsigned builds (D6, the owner's answer 3): the artifact and every release named `-unsigned`, with a named notice in the job's
   summary ("unsigned: the owner's decision — PLAN.md §9 decision 2"); a `SHA256SUMS` file of every shipped file and the installer
   beside each artifact and release, which `docs/INSTALL.md` says how to check (`Get-FileHash`) — the only integrity a person has
   this round, and nothing verifies an update automatically, since there is no updater; `docs/INSTALL.md` shows what Windows says to
   an unsigned download (SmartScreen's "Windows protected your PC — unknown publisher", More info ▸ Run anyway; UAC's "Unknown
   publisher"; Smart App Control's block with no exception) and the steps. (1 h)
7. The editions in `docs/INSTALL.md` (D4): the three tiers; on Home, a local standard "show" account made after setup (Home's setup
   asks for a Microsoft account, and a new one has no password), auto-logon to it with the password as an LSA secret, never a
   plain-text registry value, no Shell Launcher and the supervisor in its place, updates paused past the show and active hours set
   by the person, Smart App Control off for an unsigned build, the device-encryption recovery key kept, media outside OneDrive
   folders, the show network set to Private; S mode switched out of first. (1 h)

⟂ *(≈ 26.5 h)*

8. *(stretch)* The opt-in show-machine feature: on the editions with Update client policies, Windows Update deferral (feature
   updates 365 days, quality 30, no automatic restart with a user logged on, active hours); on Home only what Home honours
   (notifications off for the show user, Do Not Disturb rules, the "finish setting up" screens off, `DisableUserPresenceQos`), no
   Update policy at all; the show account in Performance Log Users (for wp3-present's ETW stretch) — each value written once by the
   installer, read back, and reported by preflight as written or not honoured on this edition.
9. *(stretch)* A manifest (build, commit, last journal sequence) beside each new WER dump.
10. *(stretch)* The node registering through `DnsServiceRegister` behind the platform seam, if item 4's check finds the two mDNS
    responders collide.
11. *(stretch)* A per-build SBOM as a CI artifact.

**Preflight.** Section `install`: not installed (a folder run, amber on a show node), the supervisor's state (restarts, stopped,
the freeze rule waiting for shown frames), auto-logon on and for which user, the show machine's policies (on Home "not on this
edition", an information row), the build unsigned (grey, **i**). **INDEX.** None.

**Owns.** `installer/**`, `src/PlayMap.Supervisor/**`, `src/PlayMap.Engine/Install/**` (STATE `machine.install` from the install's
registry key and the supervisor's status file), `tests/PlayMap.App.Tests/Install/**`, `tests/fixtures/state/install.json`,
`docs/INSTALL.md` (the guidance: the three tiers, IoT Enterprise LTSC 2024 for permanent show nodes, Home's path, auto-logon with
the LSA secret and never a plain-text registry password, Shell Launcher where the edition has it, BIOS power-on after loss, the
update window, the unsigned prompts and `SHA256SUMS`, the published GPU support tiers and the two version numbers), `nuget.config`'s
`WixToolset.*` mapping, its named CI block (§2.2) with the `wix-toolset` job.

**Hunks.** `Version.props` (the 255 rule); `src/PlayMap.Core/BuildInfo.cs` (`MsiVersion`); `src/PlayMap.App/Link/
EngineSupervisor.cs` (attach to a supervised engine; after desk-ux); the FFmpeg steps of both CI jobs (item 1).

**Depends on.** The spine (the `versioning` base).

**Acceptance.**
- `MsiVersionTests` (both): (0.2.0, build 17) → 0.2.17; (0.2.3, 18) → 0.2.18, greater than 0.2.17; a major or minor of 256
  refused; build 0 refused for an MSI; (0.2.0, 1) after a released 0.2.500 → refused "lower than the last release: raise MINOR".
- `VersionPropsTests`: a build with `PlayMapVersion` 256.0.0 fails with the words.
- `WixSourceTests` (Windows): the restored `WixToolset.Sdk` and extensions come from the local feed, their hashes the cached build's;
  no package source but the feed serves `WixToolset.*`.
- `PreinstallCheckTests` (the decision pure on both; against fake engines on Windows): no engine process → go on; an engine
  answering armed → refused with its words; disarmed → go on; **an engine process alive but silent → refused**, naming its pid; a
  pid file whose process is gone → go on.
- `InstallerTests` (Windows, elevated): the MSI builds; a quiet install lays the files, registers the service with its recovery
  actions, the firewall rules and the WER values; its FFmpeg hashes to CI's LGPL pin; the previous build's MSI (built in the job
  with a lower build number) upgrades to this one and keeps the node's settings; a downgrade is refused with words; an uninstall
  removes all but the node's data; with a fake engine on loopback answering armed, the install fails with the node's words; with
  a fake engine process that never answers, it fails with "does not answer"; with the Smart App Control state set to enforcing (a
  test property standing in for the registry value), the launch condition refuses with its words.
- `SupervisorTests` (pure, both): the backoff schedule, the heartbeat rule on a manual clock, the rule inactive while the count is
  −1, `starting` and `rebuilding` exempt within the budget and frozen past it, the five-in-ten-minutes stop; on Windows the service
  installs, starts and stops on the runner.
- `SessionStartProbe` (Windows, a probe, not a gate): M9's calls on the runner, whose job runs in an interactive session; the
  answer in the summary.
- `DeskAttachTests` (in `tests/PlayMap.App.Tests/Install/`): a supervised machine's desk attaches and never starts an engine.
- `UnsignedArtifactTests` (Windows): the notice in the summary, the `-unsigned` name, and `SHA256SUMS` matching every file it
  lists.

**Verified here.** Version maths, supervisor policy, the check's decision. **On CI (Windows).** The toolset built from its source,
the MSI's life cycle, the service, the firewall and WER settings, the LGPL FFmpeg under every decode test, the unsigned artifact and
its sums. **Manual (lab).** L11: IoT Enterprise LTSC 2024 with auto-logon; the supervisor starting the engine in session 1 (M9);
Shell Launcher; an upgrade during a rehearsal; L18: the unsigned installer on clean Home and Pro; L21: the first ten minutes; L23: a
Home node. With no lab (D15) each is reported not run: M9 stands on the runner's probe with the scheduled task kept as its fallback,
the stale time's floor is "unmeasured on a real node", and ReadyToRun is the default without L21's record.

**Next round.** Signing (Azure Artifact Signing, or another identity the owner chooses: the exe, PlayMap's own DLLs, the shipped
third-party natives and the installer, checked by `Get-AuthenticodeSignature`), `MACHINE HARDEN` (the supervisor applying a
hardening fix a person approved), an updater that refuses while armed and verifies a build against a hash from a channel it trusts,
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
7. The four-click quad that stays (the owner's requirement; `design/desk-workflows.md` §6.1): an unfinished quad kept per
   projector on the desk until Enter or a Discard (a tool change, another projector, leaving MAP and a push keep it; a "Quad 2/4 ·
   Resume · Discard" chip in the view and the outliner); Select after Enter, with the new quad selected; reselecting restores its
   last corner and sends `HANDLE SELECT`, deselecting sends `HANDLE SELECT NONE`; an outliner pick shows the quad's projector; the
   quad's corners dragged in the projector view (`SURFACE CORNER … TO … RASTER` through the throttle, a burst one undo step); the
   first-light guide reopened from the view's right-click and the help overlay. (3 h)

⟂ *(≈ 26 h)*

8. *(stretch)* Map anything: every control whose line can be bound highlighted; touching one starts `BIND LEARN` for its line.

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
- `QuadDraftTests`, `QuadSelectTests`, `QuadCornerDragTests` (`design/desk-workflows.md` §6.4): the draft survives a tool change,
  another projector, PLAY and back, and is in no line before Enter; Enter sends one `SURFACE QUAD` and leaves Select; reselect and
  deselect send their `HANDLE SELECT`; a drag sends at most one `SURFACE CORNER … TO` per 16 ms window of the desk's clock.

**Verified here.** All (headless); the bench builds on the Windows job. **Manual (lab).** L19: a formative session with operators
in a dark room, recorded in `docs/UX.md`; L3: the bench (M2). With no lab (D15) both are reported not run; the bench is built and
waits for a GPU.

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
6. A live input's import declared late-latched in the graph: its texture is bound by the slot the render takes at its sample, the
   plan compiled once whatever slot that is, so wp3-capture's newest-slot take adds no plan change. (0.5 h)

⟂ *(≈ 24.5 h)*

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
- `LateLatchTests`: a live import whose slot changes between two frames walks the same plan hash; its barrier stands before the read.

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
6. The journal's drops counted: the bounded channel that drops its oldest row under a burst (4 096 rows) counts each drop, says the
   count and the first dropped sequence in STATE (`journal.dropped`) and writes one row saying how many it lost once the burst ends —
   an audit trail that says where it has a gap, as a medical device's does. (1 h)

⟂ *(≈ 23 h)*

6. *(stretch)* The observer reading the scan camera's view of the wall in rehearsal (larger marks).
7. *(stretch)* The air hazard file (`docs/HAZARDS.md`: each hazard to the air traced to the test that holds it, design §14).

**Preflight.** Section `observe`: an observed output stale, torn, wrong or lost (red), late (amber); a watermark showing on the
air (amber, with where). **INDEX.** Section `observe`: each observed output's light.

**Owns.** `src/PlayMap.Core/Observe/**`, `src/PlayMap.Engine/Observe/**`, the `Observe` stubs (`NodeSettings.Observe.cs` among
them), `src/PlayMap.Render/Passes/WatermarkPass.cs` for W2, `tests/fixtures/state/observe.json` and its cards, `docs/OBSERVE.md`,
`docs/wire/observe.md`.

**Hunks.** `src/PlayMap.Render/Passes/OutputStage.cs` (the watermark last); `src/PlayMap.Engine/EngineCore.Fleet.cs` (fills the
spine's `HealthOutput` counts); `src/PlayMap.Core/Presentation/PresentLedger.cs` (the shown-frame count);
`tests/PlayMap.Engine.Tests/Fleet/BackupRenderTests.cs` (a case with the watermark on); `src/PlayMap.Engine/Journal.cs` (the drop
count, item 6).

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
- `JournalDropTests`: a burst past the channel's bound → `journal.dropped` equals the rows lost, the gap row names the first
  sequence, nothing blocks the writer.

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
   Workstations and IoT Enterprise, PMVB path 3; refused with the edition's words elsewhere (Home and Pro: "this edition keeps every
   display on the desktop").
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
outputs, PresentMon agreeing; L13: HDR10 on a real display. With no lab (D15) each is reported not run: `proven` is never reached
outside the lab, and AUTO stays GL on every machine all round, its words in `machine.present.why` ("D3D12 waits for its parity
record: no lab has run one").

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
4. NDI and OMT on the copy queue: the send stage's readback as copy-queue work with fence values; a live input's upload submitted
   by its receive thread at the frame's arrival with a fence value kept in the frame's slot, and the render queue waiting on it on the
   GPU — never the arena's "frames ahead" rule, which is for files and would hold a live picture a frame (`design/capture.md` §3);
   each live input's arrival-to-submit and submit-to-sample counted in STATE. (4 h)
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
- `SpoutBridgeTests` (two WARP devices): a chart round-trips byte for byte (BGRA8) both ways; no map or unmap on either side. If
  M8 finds the NuGet WARP and the system WARP do not share, CI covers Direct3D 12 to Direct3D 12 and the D3D11 side moves to L8.
- `PackUnpackTests` byte-exact on Direct3D 12.
- `LiveUploadTests` (WARP): an OMT loopback input's upload submitted at arrival, the render's GPU wait on its fence, no CPU wait
  counted; its arrival-to-submit and submit-to-sample in STATE.
- `VideoCapsTests`: a fake `CheckFeatureSupport` → the record's profiles; preflight's software-decode row names what the GPU lacks.

**Verified here.** The logic, against fakes. **On CI (Windows).** As above. **Manual (lab).** L7: D3D12VA on NVIDIA, AMD and Intel
with the pinned FFmpeg (M12); L8: Spout with Resolume and TouchDesigner (M11); L17: NDI with a real runtime. With no lab (D15) each
is reported not run: real decode stands on the ladder's words ("not measured on this GPU"), Spout's legacy handles on M11's "if no".

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

**Owns.** The desk's preview files for W3 — `src/PlayMap.App/Controls/{PreviewSource, PreviewSurface}.cs`,
`src/PlayMap.App/State/PreviewNames.cs`, the new `src/PlayMap.App/Previews/**` (`GpuPreviewSource`, `IPreviewSource`) and
`tests/PlayMap.App.Tests/{DeskPreviewTests.cs, Previews/**}` (wp3-desk-map owns the rest of the desk in W3); `src/PlayMap.Engine/Rendering/{PreviewViews,
RenderHost.Previews}.cs`, the `Preview` stub; `src/PlayMap.Core/Media/PreviewRing.cs`; `src/PlayMap.Render.D3D12/Share/**` for W3;
`tests/fixtures/state/previews.json` and its cards.

**Hunks.** `src/PlayMap.Render/Engine/RenderEngine.cs` (`Preview` and `CollectPreviews` into shared targets, after
d3d12-passes-2).

**Depends on.** d3d12-core, desk-ux, desk-shell (the panes its previews sit in).

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

**Entry condition.** M4 is answered only by L5's run of NVIDIA's barrier sample on a Sync wall, and no lab exists this round (D15,
the owner's answer 5), so the session builds M4's "no" shape — one swap chain per machine in the barrier, correct whatever the
answer — and a client per output waits for the record as a stretch; the multi-output shape is never built on a hope. The tier is
proven against the fake NVAPI and says so ("built, not proven on hardware") until L5 runs.

**Slice.**
1. The hardware lock where the hardware allows it (D5, the owner's answer 2) — NVIDIA's present-barrier tier, taken wherever the
   outputs' adapter answers `NvAPI_D3D12_QueryPresentBarrierSupport` (a professional GPU with an RTX PRO Sync board): a client per joined
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
2. The honest software lock everywhere else: without a board or a barrier, every output is in a free state (`free-measured`, or
   `free-unknown` before its clock settles) with its measured phase against the group's reference ("software lock: same frame, phase
   +6.2 ms ± 0.3 ms, next slip in 41 min"), `lock.tier` "software" — a tier, never a state; the lock's published keys and words kept
   (`state`, `words`, `reason`, `fellOut`, `rejoins`), `tier`, `mode`, `phaseMs` and `phaseUncertaintyMs` new, and
   `sync.outputs[].phaseMs`; no free output ever carries a locked state. (3 h)
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
  false and `lock.tier` "software"; then no locked state's word (`framelocked`, `house-locked`, `grouped`) in any STATE value of a
  free output (the whole STATE scanned); free outputs carry the software lock's phase words.
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
free with their phase. With no lab (D15) L5 is reported not run, and no output of a real machine is ever said to be locked by it.

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

### 3.17 `wp3-desk-shell` — panes, menus, the palette and the console, colour, the assistant's pane (W2)

*Design: `design/desk-workflows.md` §2, §4.3, §9, §10, §11.*

**Slice.**
1. Panes (D11, M18 on the first day): Dock for Avalonia, its Avalonia-12 line pinned to one exact release at least two weeks old,
   behind the desk seam `IDeskLayout`; the outliner, the inspector, the media bin, the cards, the drawers, the assistant, the console
   and the journal as panes — dragged, split, pinned to collapse into a rail that keeps the pane's title and worst light, floated
   only inside the desk's window (managed floating windows, so no pane reaches an output); the show strip, the attention bar, PANIC,
   Blackout and the mode frame fixed; a layout per workspace in the user's `desk.json` with a version, Reset layout, an unreadable
   layout the default with a toast; today's columns the default layouts. (8 h)
2. One action table, three doors: the menu bar (File, Edit, View, Map, Output, Show, Guides, Window, Help) and the Ctrl+K palette
   (deterministic ranking, each entry's shortcut, menu path and line; text that matches no verb becomes `ASK`) from the same
   `DeskAction`s as the right-click menus, with the gate's door words and the held rules — `VIDEO STOP`, `PANIC CLEAR` and `MASTER`
   sent at every press (the owner's call 7); the console pane: typed lines with completion from `VerbSyntax`, each reply under its
   line, the history, through the desk's gate, a secret-bearing row refused and nothing of it kept. (7 h)
3. Right-click menus from the catalogue: an entry for every row whose syntax names the object's kind, its other values filled from
   the object, a submenu or the palette; the default first, at most 15 at the top level, an entry that never applies hidden and one
   refused now greyed with the node's words; a filter field, inline scrub-and-type rows for numbers, recent entries, Copy line and
   "Ask about this…" on every object; menus on empty 3D and plan space; `MenuCoverageTests`. (5 h)
4. Colour (D16): the identity tokens (sky blue, reddish purple, pale grey, with the number badge) for new projectors, outputs and
   clients, replacing today's projector orange for new ones; the stage tints (Setup, MAP, SCAN) on tabs, pane headers and guide
   steps; information grey with **i**; `AreaColourTests` over every theme desk-ux made. (3 h)
5. The assistant's pane (D13): the conversation (`ASK`), the proposals as rows with Show me (the control that sends the same line
   rung), Approve (`APPROVE <id>`, one press, the owner's call 8), Reject and Approve step (a step's low and review lines approved in
   batch order, each high-risk line on its own press); the "AI · n" pill opens it. (3 h)

⟂ *(≈ 26 h)*

6. *(stretch)* A held-key radial menu in MAP (at most eight entries).
7. *(stretch)* Native floating panes, opt-in, onto a monitor no output uses.

**Preflight.** None (the desk). **INDEX.** None.

**Owns.** `src/PlayMap.App/**` and `tests/PlayMap.App.Tests/**` for W2 (but `Install/**`, wp3-installer's area), `docs/DESK.md` for
W2; the Dock package's reference in the App project (one `<ItemGroup Label="wp3-desk-shell">`).

**Hunks.** None.

**Depends on.** desk-ux (the themes and status rules the colour channels join; the quad's draft).

**Acceptance.** `DockLayoutTests`, `FixedPartsTests`, `CollapsedLightTests`, `MenuCoverageTests`, `ConsoleTests`, `AreaColourTests`,
`AssistantPaneTests`, `AskAboutThisTests` (`design/desk-workflows.md` §2, §4.6, §9.4, §10, §11); `PaletteTests`: every action
reachable, ranked the same for the same query, a held row held but for the owner's three, a secret-bearing row refused;
`DeskVerbRuleTests` and every existing App test pass unchanged.

**Verified here.** All (headless). **On CI.** The same on both jobs. **Manual (lab).** L19 (the operators' session, with the panes and
the palette), reported not run while no lab exists (D15).

**Next round.** Layouts per role; the panes on a second desk monitor once native floating is measured.

### 3.18 `wp3-desk-map` — every handle by one pixel, and what each screen shows (W3)

*Design: `design/desk-workflows.md` §6.2–§6.4, §7.*

**Slice.**
1. The engine's handles: `PIN MOVE <pin> TO|BY <x> <y> [PX]`, `WARP CORNER <output> <c> TO|BY <x> <y> [PX|NORM]`, `WARP NODE … TO|BY …
   PX`, `HANDLE SELECT` of pins, warp corners, warp lattice nodes and mask nodes, `HANDLE NUDGE` moving the selected handle of any kind
   — so an encoder, a Companion dial or the assistant nudges a pin as it nudges a corner; a move that changes nothing refused with the
   words, a locked entity with LockGuard's; a burst of one handle one undo step; `air.overlay.handleP95Ms` (frames from a handle's line
   to the frame that draws it). (4 h)
2. The desk's handles: every handle dragged, selected (restored per object), typed (x, y in the inspector, the quad's corners among
   them, replacing the mesh fields a quad does not use) and nudged (arrows 1 px, Shift a desk preference of 2–100 defaulting to 10, Alt
   0.1; Tab steps); the status bar's mode, handle, step and unit; the overlay's selected-handle crosshair and the pins on the wall
   (`OverlayHandles`, one call from `OverlayPass`). (5 h)
3. Precision: the projector view's zoom (the wheel, 1:1, Fit) and an 8× loupe at the pointer, so one raster pixel is one move. (3 h)
4. What each screen shows: STATE `outputs[].showing` resolved engine-side at each revision (the chain to the media now playing, and
   `over` when the air covers it) and `media[].shownOn`, in the context's output items; on the desk every view of an output (PLAY's
   cards, the plan's tiles, the outliner, the projector view, the inspector's chain), media and playlist badges ("on 2 surfaces · 1
   output"), a Source-mode output's source picker, a surface's Content ▸ made again on every push with media entries; an amber
   preflight row for an enabled output that shows nothing. (6 h)
5. `showing.json` and its cards; `docs/wire/handles.md`; the papers notes. (2 h)

⟂ *(≈ 20 h)*

6. *(stretch)* Duplicate offsetting a quad by a visible step (today a copy lands on its original [today §a]).
7. *(stretch)* Gamepad selection and nudging (Watchout's mapping [ux §2]).

**Preflight.** An enabled output showing nothing (amber, section `outputs`). **INDEX.** None new (the outputs' section gains its
`showing` words).

**Owns.** `src/PlayMap.App/**` and `tests/PlayMap.App.Tests/**` for W3 but wp3-desk-gpu's preview files (§3.12); the `Handles`
stubs; `src/PlayMap.Engine/Wall/**` for W3; `src/PlayMap.Render/Passes/OverlayHandles.cs` (new); `tests/fixtures/state/showing.json`
and its cards; `docs/wire/handles.md`.

**Hunks.** `src/PlayMap.Render/Passes/OverlayPass.cs` (the call to `OverlayHandles`, after d3d12-passes-2).

**Depends on.** desk-shell; d3d12-passes-2 before merge (§5.3).

**Acceptance.** `HandleVerbTests`, `HandleSelectTests`, `HandleEditTests`, `OverlayHandleTests`, `LoupeTests`, `ShowingTests`,
`ShowingDeskTests`, `ContentMenuTests`, `NothingShownRowTests` (`design/desk-workflows.md` §6.4, §7); `HandleLatencyTests` (Render):
300 handle moves drawn within two frames at p95, counted in frames, no byte allocated; `AssistantHandleTests` (Assistant): a scripted
provider proposes `PIN MOVE … BY 1 0 PX` and `HANDLE NUDGE`, the gate's verdict by the class (queued while armed), the pin's pixel in
STATE afterwards; `MenuCoverageTests` with the new rows.

**Verified here.** All (headless; the overlay on llvmpipe). **On CI (Windows).** The same; the overlay on WARP. **Manual (lab).** L24
(the handles on a real wall), reported not run while no lab exists.

**Next round.** Snapping a pin to a mesh's vertex; `PICK PIXEL`.

### 3.19 `wp3-canvas` — one canvas across outputs, and every automatic result a layer under the hand's (W4)

*Design: `design/desk-workflows.md` §3, §8.*

**Slice.**
1. `CANVAS SPAN <canvas>|NEW <output> <output>… [SIDE <c>x<r>|STACK] [OVERLAP <px>|<pct>%] [KEEP|REPLACE]` as one edit — the canvas's
   size from the outputs less the overlaps, each output in Canvas mode with its rectangle, its inner edges and its plan rectangle,
   `STACK` the whole canvas each with no edge — recording what it wrote (`Canvas.Span`), a re-span over a rectangle or edge changed
   by hand since refused naming each unless `KEEP` or `REPLACE`; `CANVAS SOURCE <canvas> <source> [FIT|FILL|STRETCH]` (the canvas's
   base layer, one line). (4 h)
2. `PATTERN <kind>|OFF ON <canvas>`: the pattern drawn once at the canvas's size, over the canvas wherever it is shown, with the span
   card — each output's label, number and resolution in its identity colour, its rectangle's border, the overlap's start and width,
   circles and diagonals across the seams — through the RHI on both backends. (5 h)
3. The outputs' automatic results as layers under the hand's (D12): align's measured γ into `Measured.Gamma`, drawn before the hand's
   γ (today `ALIGN ACCEPT` writes it into `Blend.Gamma`, a field a person types); `ALIGN ACCEPT`, `STACK ALIGN`, `STACK MATCH` and
   `BLEND SOLVE` replacing only their layer and listing each hand edit they kept with its clearing line; `HAND <output>|<projector>|
   <surface>` and `HAND CLEAR <output> WARP|BLEND|COLOR|ALL`; STATE `outputs[].hand`. (4 h)
4. `canvas.json` and its cards, `docs/CANVAS.md`, `docs/wire/canvas.md`, `docs/ALIGN.md`'s γ words, the papers notes. (2 h)

⟂ *(≈ 15 h)*

5. *(stretch)* Content as a camera sees it (`SurfaceMapping.Camera`, from a scan taken at the audience's seat [ux §1.2]).
6. *(stretch)* MPCDI export as a verb (`MpcdiExport` is in Core with no verb today [today §d2]).

**Preflight.** A span's output whose rectangle a hand moved since (grey fact, its `KEEP` and `REPLACE` lines). **INDEX.** None.

**Owns.** `src/PlayMap.Core/Canvas/**` (the span planner, pure), `src/PlayMap.Engine/Canvas/**`, the `Canvas` stubs,
`src/PlayMap.Render/Engine/**` for W4, `src/PlayMap.Render/Passes/{OutputStage, MeasuredStage}.cs` for W4, `src/PlayMap.Core/Align/**`
and `src/PlayMap.Engine/Align/**` for W4, `tests/PlayMap.Render.Tests/Canvas/**`, `tests/fixtures/state/canvas.json` and its cards,
`docs/CANVAS.md`, `docs/wire/canvas.md`.

**Hunks.** `tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` (its scenes' rows, after stability-2).

**Depends on.** d3d12-passes-2, present (the render and output-stage files they finish in W3).

**Acceptance.** `CanvasSpanTests`, `SpanKeepTests`, `SpanPatternTests`, `CanvasSourceTests`, `HandOverAutoTests`, `HandClearTests`
(`design/desk-workflows.md` §3.3, §8); `AssistantSpanTests` (Assistant): a scripted provider proposes `CANVAS SPAN NEW op_1 op_2 SIDE
2x1 OVERLAP 15%` and `PATTERN grid ON <canvas>`, the review line by the setting and the high line waiting for a person, the canvas in
the project afterwards; every Render and Engine golden unchanged.

**Verified here.** All (GL on llvmpipe). **On CI (Windows).** The span pattern on WARP within its tolerance row. **Manual (lab).** L24,
reported not run while no lab exists.

**Next round.** Curved screens' spans (cylinder, dome) with align's curved fit.

### 3.20 `wp3-workflows` — the three guides, by hand, automatically, or with the assistant (W4)

*Design: `design/desk-workflows.md` §2–§5.*

**Slice.**
1. Guides as data (D13): pure planners in `Core/Workflows` — FLAT (§5.1 of the design), WALL (§5.2), OBJECT (§5.3), with FACADE
   (`SiteGuide`, behind the same contract and its old row) and FIRST (the first light) — each step's state, words, facts (the pins'
   coverage meter among them), automatic, hand and proposed lines; `GUIDE LIST`, `GUIDE <kind> [<subject>]`, `GUIDE … CHECK|UNCHECK
   <step>` (a person's mark, refused from an assistant's session); STATE `guides`; the context's guide items. (6 h)
2. Poses, scans and viewpoints keeping the hand's edits (D12): a typed or nudged pose a ledger result of source `hand`, and a solve or
   re-solve over one staged, naming it; `SCAN APPLY` keeping every surface's content, tags, masks and hand transforms, a surface whose
   faces are gone kept hidden and named; `VIEWPOINT FIT <viewpoint> <surface>… [MARGIN <pct>] [FOV]` (aim and field of view from the
   eye) and `SURFACE MAP <surface> UV|CANVAS …|PROJECTION FROM <viewpoint>`. (3 h)
3. The desk's guides: the Workflows home with a card per guide and its state; a guide as a docked step rail beside the live view and
   the step's inspector, each step's Do it, By hand, Ask, Explain and Check, re-entered from the object's right-click; the
   viewpoint's inspector (typed eye, aim, field of view) and Look through. (7 h)
4. Simple hides, Pro shows: Simple, the Workflows home's landing, hides Pro fields and panes and never an air-safety control; menus
   the same in both. (2 h)
5. The assistant in the guides: `plan_recipe guide_step`; the prompt's workflow rule; Explain from the step's facts and words
   (`StateExplainer` with the assistant off); `AssistantGuideTests` for every step of the three guides. (3 h)
6. `guides.json` and its cards, `docs/WORKFLOWS.md`, `docs/wire/guide.md`, `docs/MAPPING.md`'s hand rule, the papers notes. (2 h)

⟂ *(≈ 23 h)*

7. *(stretch)* A still of a view for an assistant model that reads pictures, off by default, named in the assistant's settings.
8. *(stretch)* Suggested pin points from `PlayMap.Vision`'s pin suggestions, offered as proposals.
9. *(stretch)* The 2 × 2 stack-and-blend and n × m presets of the flat guide.

**Preflight.** A begun guide with a failed step (amber, the step's words, its line as the fix where one exists). **INDEX.** Section
`guides`: each guide's next step (its line an action).

**Owns.** `src/PlayMap.Core/Workflows/**`, `src/PlayMap.Core/Site/SiteGuide.cs` for W4, `src/PlayMap.Core/Calibration/**` for W4,
`src/PlayMap.Core/Mapping/ViewpointFit.cs` (new), `src/PlayMap.Engine/Workflows/**`, the `Guide` stubs, `src/PlayMap.Engine/Wall/**`
and `src/PlayMap.Engine/Scanning/**` for W4, `src/PlayMap.App/**` and `tests/PlayMap.App.Tests/**` for W4, `src/PlayMap.Assistant/**`
and `tests/PlayMap.Assistant.Tests/**` for W4, `tests/PlayMap.Net.Tests/Workflows/**`, `tests/fixtures/state/guides.json` and its
cards, `docs/WORKFLOWS.md`, `docs/wire/guide.md`.

**Hunks.** None beyond its files.

**Depends on.** desk-map (and desk-shell before it); canvas before merge (§5.3).

**Acceptance.** `GuidePlannerTests`, `GuideVerbTests`, `FlatGuideTests`, `WallGuideTests`, `ObjectGuideTests`, `HandPoseTests`,
`ScanKeepsHandTests`, `ManualSetupTests`, `GuideRailTests`, `SimpleProTests`, `WorkflowsHomeTests`, `AssistantGuideTests`,
`McpGuideTests` (`design/desk-workflows.md` §2, §3.3, §4.6, §5); `ViewpointFitTests`: a viewpoint and two surfaces → aim at their
centre and the field of view that frames them with the margin, a typed field of view kept without `FOV`; `SurfaceMapTests`: each
form sets the mapping, again with the same refused; `SiteGuideTests` unchanged through `GUIDE FACADE`.

**Verified here.** All (headless, the align and stage simulators). **On CI.** The same on both jobs. **Manual (lab).** L24 (the flat
blend and stack on two projectors), L19 (the guides' critical tasks with operators), reported not run while no lab exists.

**Next round.** A curved-screen guide; the facade guide on the desk's rail.

### 3.21 `wp3-capture` — capture cards passed through to the air (W4)

*Design: `design/capture.md`.*

**Slice.**
1. The seam and the hub: capture providers in the video I/O hub (a receiver per input, the loss policy, backup, tally, health,
   preflight); `capture:` schemes failing closed, never read as FFmpeg locations; `INPUT DISCOVER CAPTURE` with devices and modes;
   `INPUT ADD`'s `MODE` and `FORMAT`; the `video=` path kept as rung 4 and opened with the platform's input format (today's
   `Format = ""` case first, as a failing test [vt §1.1]); packed 4:2:2 (UYVY, YUYV, v210, P210) kept packed and unpacked on the GPU,
   `video.v210` new in GLSL and HLSL. (5 h)
2. Media Foundation (rung 3, M19): the source reader with `MF_LOW_LATENCY` on a D3D11 device manager, its texture shared into
   Direct3D 12 by NT handle and shared fence through wp3-video's bridge. (5 h)
3. DeckLink (rungs 1 and 2): COM interop declared from the SDK's Include IDL with Blackmagic's notice kept; the capture allocator
   provider handing the card PlayMap's slots; every interface checked against an older driver; the hardware timestamp mapped to the
   monotonic clock; the genlock status read. (5 h)
4. The GPU side and the pass-through output: the slot ring wrapped as a D3D12 heap (M16), the copy at arrival with a fence, the
   render's GPU wait, slots released after their last drawing frame's fence, drops counted; `OUTPUT PASSTHROUGH` (one frame in flight,
   out of the latency plan's delay ring, the pacing output; refused in a lock group without a shared reference); repeats and lateness
   counted; the rate-mismatch row. (5 h)
5. Measured, then governed: STATE `videoio.inputs[].capture` and `.latency` from the stamps (arrival to shown, p50 and p95, "in-node");
   `LATENCY LOOP <output> VIA <input>` (glass to glass through a loop-through, p50, p95, p99, the method named), "unmeasured" until it
   runs; `capture.json` and its cards, `docs/VIDEO.md`'s capture part, `docs/wire/capture.md`, L25's script. (3 h)

⟂ *(≈ 23 h)*

6. *(stretch)* AJA through a C-interface shim built from its MIT library (rungs 1 and 2).
7. *(stretch)* Magewell from `LibMWCapture.dll` in System32, its low-latency mode (rungs 1 and 2).
8. *(stretch)* DeckLink's reference timing offset as a setting.

**Preflight.** Section `videoio`: a capture runtime missing, an input's rate not its pass-through output's refresh, a pass-through in
a lock group without a shared reference, rung 4 in use, a capture driver that did not load (each amber, with its words). **INDEX.**
Section `inputs` gains the capture inputs (tally and health, as live inputs).

**Owns.** `src/PlayMap.Media/Capture/**` (new), `src/PlayMap.Media/Sources/CaptureSource.cs`, `src/PlayMap.Media/Frames/**` and
`src/PlayMap.Media/Video/LiveSource.cs` for W4, `src/PlayMap.Core/Media/LiveVideo.cs` for W4, `src/PlayMap.Core/Presentation/LatencyPlan.cs`
for W4, `src/PlayMap.Engine/VideoIo/**` and `src/PlayMap.Engine/EngineCore.VideoIo.cs` for W4, `src/PlayMap.Render/Video/**` for W4,
`src/PlayMap.Render.D3D12/Capture/**` (new, outside stability-2's W4 folders), the `Capture` stubs, `tests/fixtures/state/capture.json`
and its cards, `docs/VIDEO.md` (its capture part), `docs/wire/capture.md`, its heading of `THIRD-PARTY-NOTICES.md` (the DeckLink IDL's
notice; AJA's MIT notice if the stretch lands).

**Hunks.** `src/PlayMap.Render.D3D12/Shaders/video.v210.hlsl` (a new file in stability-2's W4 folder, after stability-2);
`src/PlayMap.Engine/Rendering/RenderHost.cs` (the pacing output while a pass-through is on, after stability-2);
`tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` (v210's byte-exact row).

**Depends on.** video, present; framelock before merge (the lock-group rule reads its state).

**Acceptance.** `CaptureProviderTests`, `CapturePathTests`, `CaptureSlotTests`, `WrappedHeapTests`, `PackedCaptureTests`,
`MfProviderTests`, `DeckLinkInteropTests`, `PassThroughTests`, `RateMatchTests`, `CaptureLatencyTests`, `LatencyLoopTests`,
`DeviceItemOpenTests`, `ZeroAllocTests` with a capture input (`design/capture.md` §11); `AssistantCaptureTests` (Assistant): a
scripted provider proposes `INPUT ADD capture:decklink/1`, `SURFACE SOURCE <sf> live:<in>` and `OUTPUT PASSTHROUGH <op> ON`, each by
its class, queued while armed, the input in STATE afterwards.

**Verified here.** The providers against fakes, the slots, the rate maths, the pass-through, on GL. **On CI (Windows, WARP).** The
wrapped heap (M16), the packed unpacks, the MF provider (M19), the pass-through on WARP's presenters. **Manual (lab).** L25, reported
not run while no lab exists; STATE says "unmeasured".

**Next round.** Genlocked SDI and ST 2110 outputs (their vblank from the card), AJA and Magewell if their stretch is not reached,
GPUDirect for Video where a loop-through shows the copy matters, Deltacast, Datapath and Bluefish444 under their vendors' terms.

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
| `src/PlayMap.Core/Presentation/**` | spine (W0: moved from `Render/Timing` and `Render/Platform`) → present (W3: `FrameBlock`, the evidence) → capture (`LatencyPlan.cs` only, W4: a pass-through out of the delay ring) | stability (the ledger's shown-frame count, W2) |
| `src/PlayMap.Core/Machine/**` | spine (W0: the records, `ThreadClaims`) → platform (W1) | none |
| `src/PlayMap.Core/Sync/**` | — (round 2's) → framelock (W4: the driver's new shape) | none |
| `src/PlayMap.Core/Media/GpuSurfaces.cs`, `UploadArena.cs` | spine (W0: `GpuApi.D3D12`) → video (W3) | none |
| `src/PlayMap.Core/Media/PreviewRing.cs` | desk-gpu (W3) | none (desk-ux reads the existing stamp) |
| `src/PlayMap.Core/Effects/IsfTranslator.cs` | shaders (W1) → shaders-2 (W3) | none |
| `src/PlayMap.Core/Modulation/{ModulationBus, ModulationPlan}.cs`, `src/PlayMap.Engine/Audio/EngineCore.Mod.cs` | — (round 2's) | d3d12-passes-2 (the drawn numbers' table, W3) |
| `src/PlayMap.Core/Observe/**`, `src/PlayMap.Engine/Observe/**` | stability (W2) → stability-2 (W4) | present (`Observe/LatencyStep.cs`: the stage shares, W3) |
| `src/PlayMap.Render/Gpu/*` → `Gl/*` | rhi (W1) | none |
| `src/PlayMap.Render/Gl/**` (with `RenderContext.cs` and the GL platforms) | rhi (W1) → d3d12-passes (W2: the GL walk) → d3d12-passes-2 (W3: GL's allocation fence) → stability-2 (W4: context loss) | none |
| `src/PlayMap.Render/Engine/{RenderEngine, FramePasses, Contracts}.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) → canvas (W4: a pattern across a canvas) | present (`FrameInputs.PredictedDisplayNs`, W3) → desk-gpu (`Preview`, `CollectPreviews`, W3) |
| `src/PlayMap.Render/Passes/OutputStage.cs`, `MeasuredStage.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) → canvas (W4: the measured γ under the hand's) | stability (the watermark last, W2, before d3d12-passes) → present (the PQ encode and the 10-bit dither, W3) |
| `src/PlayMap.Render/Passes/OverlayPass.cs` | rhi (W1) → d3d12-passes (W2) → d3d12-passes-2 (W3) | desk-map (the call to its `OverlayHandles.cs`, W3, after d3d12-passes-2) |
| `src/PlayMap.Render/Passes/WatermarkPass.cs` | stability (W2) → d3d12-passes-2 (W3) | d3d12-passes (its `Declare`, W2, after stability) |
| `src/PlayMap.Render/Passes/SendStage.cs` | rhi → d3d12-passes → d3d12-passes-2 | video (the copy-queue readback, W3) |
| `src/PlayMap.Render/Effects/**` | rhi (W1) → d3d12-passes (W2) → shaders-2 (W3) | shaders (`FxChainRunner.cs`: the quarantine's bypass, W1) |
| `src/PlayMap.Render/Platform/**`, `Timing/**` | rhi (W1) → d3d12-core (`GpuPlatform.cs`, W2) → present (W3) | none |
| `src/PlayMap.Render/Platform/Windows/NvApi*.cs` | rhi (W1) → present (W3, with `Platform/**`) → framelock (W4) | none |
| `src/PlayMap.Render/Sync/**` | rhi (W1) → framelock (W4) | none |
| `src/PlayMap.Render.D3D12/**` | spine (W0: the project, empty folders) → shaders (`Shaders/`, W1) → d3d12-core (all of `design/d3d12.md` §0's W2 folders, W2) → W3: present (`Platform/`, `Present/`), video (`Video/`), desk-gpu (`Share/`), shaders-2 (`Effects/`), d3d12-passes-2 (the root, `Device/`, `Queues/`, `Memory/`, `Descriptors/`, `Commands/`, `Pipelines/`, `Shaders/`, `Interop/`, `Bindless/`) → W4: framelock (`Sync/`, `Present/`), capture (`Capture/`, new), stability-2 (every other folder) | present (`Shaders/output.hlsl`: the PQ encode, W3, after d3d12-passes-2) → stability-2 (`Present/`: presenters made again on a rebuild, W4, before framelock) → capture (`Shaders/video.v210.hlsl`, a new file, W4, after stability-2) |
| `src/PlayMap.Render.D3D12/PlayMap.Render.D3D12.csproj` | as its root above | one `<ItemGroup Label="wp3-<pkg>">` per package |
| `src/PlayMap.Platform.Windows/**` | spine (W0: the project) → platform (W1) → framelock (`Vendors/`, W4) | none |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | rhi (W1) → d3d12-passes (W2) → present (W3) → stability-2 (W4) | platform (the render thread's claim, W1) → d3d12-passes-2 (the pre-roll's call, W3, first in W3's order) → capture (the pacing output while a pass-through is on, W4, after stability-2) |
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
| `src/PlayMap.Render/{Video, Hap, Decode}/**` | rhi (W1) → video (W3) → capture (`Video/**` only, W4: `video.v210`) | none |
| `src/PlayMap.Media/{Capture/**, Sources/CaptureSource.cs, Frames/**, Video/LiveSource.cs}`, `src/PlayMap.Core/Media/LiveVideo.cs`, `src/PlayMap.Engine/VideoIo/**`, `src/PlayMap.Engine/EngineCore.VideoIo.cs` | — (round 2's) → capture (W4) | video (the live inputs' copy at arrival, W3, in its own files) |
| `src/PlayMap.App/**` | desk-ux (W1) → desk-shell (W2) → W3: desk-gpu (its preview files, §3.12) and desk-map (the rest) → workflows (W4) | installer (`Link/EngineSupervisor.cs`: attach to a supervised engine, W1) |
| `tests/PlayMap.App.Tests/**` | desk-ux (W1; `Install/**` installer's) → desk-shell (W2) → W3: desk-gpu (`DeskPreviewTests.cs`, `Previews/**`) and desk-map (the rest) → workflows (W4) | none |
| `docs/DESK.md` | desk-ux (W1) → desk-shell (W2) → desk-map (W3) → workflows (W4) | none |
| `src/PlayMap.Engine/Wall/**` | — (round 2's) → desk-map (W3: the handles) → workflows (W4: hand results in the ledger) | none |
| `src/PlayMap.Core/Calibration/**`, `src/PlayMap.Engine/Scanning/**`, `src/PlayMap.Core/Site/SiteGuide.cs` | — (round 2's) → workflows (W4) | none |
| `src/PlayMap.Core/Align/**`, `src/PlayMap.Engine/Align/**` | — (round 2's) → canvas (W4: the measured γ) | none |
| `src/PlayMap.Assistant/**`, `tests/PlayMap.Assistant.Tests/**` | — (round 2's) → workflows (W4: `guide_step`, the prompt's rule) | the packages' `Assistant…Tests` sit in their own area folders (`tests/PlayMap.Assistant.Tests/<Area>/**`) |
| `src/PlayMap.Engine/Journal.cs` | — (round 2's) | stability (the drop count, W2) |
| `tests/PlayMap.Render.Tests/**` | rhi (W1) → d3d12-core (W2) → d3d12-passes-2 (W3) → stability-2 (W4); new tests sit in their package's area folder | none |
| `tests/PlayMap.Render.Tests/Parity/D3D12Tolerances.cs` | d3d12-core (W2) → shaders-2 (W3) → stability-2 (W4) | d3d12-passes (its scenes' rows, W2) → d3d12-passes-2, present (their rows, W3) → capture, canvas (their rows, W4) |
| `tests/PlayMap.Engine.Tests/Rendering/HostRig.cs` | rhi (W1) → d3d12-passes (W2: the plan) → present (W3: the D3D12 platform's leg) → stability-2 (W4) | none |
| `tests/PlayMap.Engine.Tests/Fleet/BackupRenderTests.cs` | — (round 2's) | stability (a case with the watermark on, W2) |
| `.github/workflows/build.yml` | spine | each package's own named block (§2.2), in merge order: shaders (pinned compiler natives, W1) → installer (the LGPL FFmpeg in both jobs; the WiX toolset built from its source; the MSI, install, upgrade, the unsigned name and `SHA256SUMS`, W1) → d3d12-core (the debug-layer settings, the D3D12 leg's summary, W2) → shaders-2 (the pinned ISF corpus, W3) |
| `nuget.config` (new at the root) | installer (W1: nuget.org for every package but `WixToolset.*`, mapped to the local feed only) | none |
| `THIRD-PARTY-NOTICES.md` | spine (headings) | each package its heading: d3d12-core (TerraFX, the D3D12 Memory Allocator, the Agility SDK; WARP for CI only), shaders (DXC, glslang, SPIRV-Tools, SPIRV-Cross), installer (WiX as a build tool, built from its MS-RL source at the pinned tag, and its Util and Firewall custom-action DLLs inside the MSI, MS-RL with a link to that tag; FFmpeg's LGPL libraries and libvmx now inside the MSI), framelock (ADLX loaded from the driver, never shipped), stability-2 (Aftermath, if shipped), desk-shell (Dock for Avalonia), capture (the DeckLink SDK's Include IDL notice; AJA's MIT notice if its stretch lands; nothing of Magewell) |
| `*.csproj` | the project's owner | one `<ItemGroup Label="wp3-<pkg>">` per package |
| `tests/fixtures/state/{machine, install, shaders, gpu, graph, observe, previews, showing, canvas, guides, capture}.json` and their cards | platform, installer, shaders → shaders-2, d3d12-core → stability-2, d3d12-passes → d3d12-passes-2, stability → stability-2, desk-gpu, desk-map, canvas, workflows, capture — one writer each per wave | none |
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
| **W1** | rhi, platform, shaders, installer, desk-ux (5) | Each depends on the spine only. rhi alone touches `src/PlayMap.Render` and the render host (beyond the named hunks); the other four own new projects (`Platform.Windows`, `Render.D3D12/Shaders`, `Supervisor`, `installer/`) and the desk. The benches for M1–M3 (the spine's and desk-ux's) are built beside them and wait for a lab (D15). |
| **W2** | d3d12-core, d3d12-passes, stability, desk-shell (4) | Each needs the RHI but desk-shell; d3d12-core also the HLSL. The graph compiler and the observer run on GL here; their Direct3D 12 acceptance runs after d3d12-core merges first. desk-shell takes the desk while no other package of the wave owns it. |
| **W3** | d3d12-passes-2, shaders-2, present, video, desk-gpu, desk-map (6) | Each needs the Direct3D 12 device but desk-map; d3d12-passes-2 also the graph; shaders-2 also the worker; desk-gpu also the desk's age badge and panes; video and desk-gpu build on d3d12-core's merged exporter; desk-map needs the shell's panes and menus, and splits the desk with desk-gpu by file (§4). |
| **W4** | stability-2, framelock, canvas, capture, workflows (5) | stability-2 and framelock need the flip presenters, the rebuild path before the barrier that must register again in it, and framelock builds M4's "no" shape (§3.14); canvas needs the render and output-stage files finished in W3; capture needs video's live uploads and present's frames in flight; workflows needs desk-map's handles and canvas's span. |

**Scheduling.** Waves name dependency depth, not a barrier: a package starts when every package it depends on is merged and a
slot is free; a review takes the next free slot when its implementation ends. The critical path is spine → rhi → d3d12-core →
present → framelock; rhi is its longest link and the one the round cannot shorten (§6, R1). The desk's chain (desk-ux → desk-shell →
desk-map → workflows) runs beside it, one link per wave, and adds no wave.

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
| W2 | d3d12-passes, stability | d3d12-core, desk-shell |
| W3 | d3d12-passes-2, desk-gpu, desk-map | present, video, shaders-2 |
| W4 | canvas (and reviews, the integration pass) | stability-2, framelock, capture, workflows |

The packages here are the ones that run the GL pixel suites heavily or own the integration-hot render files; the cloud ones own
new projects, the desk's headless suites or take their evidence from the Windows job. With this split the round runs in about
seven rounds of sessions — W0, two for W1, one each for W2–W4, and the integration pass — with W3 at this machine's four
implementations (d3d12-passes-2, desk-gpu, desk-map) plus one review.

### 5.3 Merge order

| Wave | Order | Why |
|---|---|---|
| W1 | **rhi → shaders → platform → desk-ux → installer** | The RHI changes Render and Core first; shaders' layout tests read its layouts and its `FxChainRunner` hunk follows it; platform's timer and claims hunks follow it; the installer's attach hunk follows desk-ux's desk. |
| W2 | **d3d12-core → stability → d3d12-passes**, desk-shell in any slot | The device before anyone's WARP acceptance; the watermark's `OutputStage` hunk before d3d12-passes declares the output stage; desk-shell touches only the desk. |
| W3 | **d3d12-passes-2 → shaders-2 → present → video → desk-gpu → desk-map** | d3d12-passes-2 owns the frame files the others hunk, lands its pre-roll's call in `RenderHost.cs` before present takes that file, and builds the warm-up shaders-2's user programs join; present's output-stage hunks follow it; video and desk-gpu build on d3d12-core's merged exporter, in either order; desk-map's overlay hunk follows d3d12-passes-2, and it merges last so its desk files rebase on desk-gpu's previews. |
| W4 | **stability-2 → framelock → capture → canvas → workflows** | The rebuild path before the barrier registers again in it; capture's lock-group rule reads framelock's state and its `RenderHost.cs` and shader hunks follow stability-2; canvas's tolerance rows follow stability-2's table; workflows' flat guide uses canvas's span. |

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
5. **From wp3-installer's merge on, the installer leg is a hard gate**: the toolset built from its source (or Inno Setup, M17)
   makes the installer, which builds, installs, upgrades from the previous build, refuses a downgrade and uninstalls on the runner;
   the artifact carries the `-unsigned` name, the notice and its `SHA256SUMS`, which match its files.
6. **From wp3-desk-shell's merge on, `MenuCoverageTests` is a hard gate**: a row the desk may send that no menu reaches fails the
   merge that adds it.

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
| desk-map, desk-gpu | `ShowingDeskTests` on a PLAY card whose picture is a shared preview |
| capture, framelock | `PassThroughTests`' lock-group refusal against the real lock machine |
| canvas, workflows | `FlatGuideTests` end to end through `CANVAS SPAN` and the span card |
| workflows, every package that added a desk step | `AssistantGuideTests` reaching each step; `MenuCoverageTests` over the integrated catalogue |
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
   WARP; no AMD runtime (ADLX is the driver's); no DeckLink, AJA or Magewell binary and no FFmpeg built with DeckLink; WiX's Util and
   Firewall custom-action DLLs named with their MS-RL notice and source tag; every notice present.
5. Budgets recorded: the MSI's size, start-up to the first frame on WARP, the pipeline count of the reference show, the warm-frame
   allocation assertion with every pass registered on both backends.
6. The Companion module's fixtures regenerated from the integrated catalogue and STATE.
7. `ContextOrderTests` (Core): whatever rows the round adds, the context lists every panic, air, transport and Show Mode row before
   any other, so a peer that keeps only the first commands — Patterns keeps 64 today — keeps the air's verbs (`docs/round2/
   CARRYOVER.md`, the owner's call 11).
8. The papers for the owner's requirements: `docs/DESK.md` (the shell, the guides, the console, the colours), `docs/MAPPING.md` and
   `docs/CALIBRATION.md` (the hand rule), `docs/VIDEO.md` (capture), `docs/INSTALL.md` (the editions, unsigned builds), `docs/UX.md`
   (the conditions of its first-light target: an unsigned build's prompts), `docs/COMPETITIVE.md` (the guides against the field).

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
| R9 | The present barrier refuses several swap chains (M4), or yields no shared frame count (M10) | Medium | High for multi-output walls | With no lab M4 is not measured, so one swap chain per machine joins and the rest report free with their phase; Mosaic stays a stretch; content time from the anchor at the in-sync refresh | framelock |
| R10 | The Agility runtime does not load from a .NET apphost (M7) | Medium | Low | The OS runtime, named in STATE; newer features probed and refused with words | d3d12-core |
| R11 | Auto-logon and a LocalSystem supervisor widen the attack surface | Medium | High | A standard show account; its password as an LSA secret, never a plain registry value; the supervisor's pipe ACL'd to the show user and administrators; the MSI grants two named privileges to one named account; the engine never elevated; guidance in `docs/INSTALL.md` | installer |
| R12 | Wire and fixture changes break peers (Patterns, Stagewatch, Companion) | Low | Medium | D3D12 flip outputs keep PMVB path 2; new STATE keys additive (`machine.present`'s keys and types, the lock's words and `reason` kept); Windows fixtures as twins; the module's fixtures regenerated at integration; mixed builds in a group named in preflight by their versions | spine, present, framelock, integrator |
| R13 | Platform claims starve the desk or the system (a high-priority process with MMCSS threads) | Low | Medium | Each claim a setting with its read-back; realtime GPU priority off until measured; the lab's p99.9 with and without each | platform |
| R14 | Licences: FFmpeg now inside the MSI; WARP not redistributable; AMD runtimes; Aftermath's terms | Medium | High | The LGPL FFmpeg build only, as replaceable files with notices, the same pin every CI decode test loads; WARP in CI only; AMD runtimes (ADLX among them) never bundled; Aftermath loaded when present until its terms are confirmed; `LicenceTests` on the MSI | installer, integrator |
| R15 | People read "Direct3D 12" as "the default" while AUTO is still GL | Medium | Medium | STATE says why AUTO is GL; the roadmap and papers say it in the present tense; the lab checklist says what remains | integrator |
| R16 | Twenty-one sessions (the sixteen and the five for the owner's requirements); `main` moves meanwhile | Medium | Medium | Packages start as their dependencies merge; cloud sessions beside this machine's five; every package rebases before merge; §9 decision 1 names the deferral order of the five | orchestrator |
| R17 | Add/Remove Programs shows `0.2.17` for 0.2.0 build 17 | High | Low | The product's name carries the SemVer; `docs/INSTALL.md` explains the two numbers | installer |
| R18 | WARP's rasteriser rounds, filters and blends unlike llvmpipe, against tolerances tuned on llvmpipe | High | Medium | A per-scene D3D12 tolerance table filled from the first WARP run, each widening with its measured difference and reason, reviewed at the gate; byte-exact only for the integer `Load` programs; L15 on real drivers | d3d12-core |
| R19 | A shared hosted runner makes a timing assertion red sometimes, blocking a hard gate | Medium | High | CI asserts counts, order and fields and records times (§2.1); pass marks live in the lab | every package |
| R20 | A sync group mixes GL and Direct3D 12 nodes, or drivers, while generated content spans a blend seam | Medium | Medium | AUTO stays GL and D3D12 is chosen per node, so the case is real; amber in preflight and words in `SYNC APPLY` (framelock item 5) | framelock |
| R21 | The supervisor stops an engine during a legitimate stall (a start, a rebuild, a GL fallback) | Medium | High | HEALTH's `gpu` phase exempts a start or a rebuild up to four times its recorded budget; the stale time tied to that measured budget; `SupervisorRebuildTests` at the merge | installer, stability-2 |
| R22 | The lab is not there when its line falls due | Certain (the owner's answer 5) | High | Every line reported not run, never passed (D15); every M the lab alone answers on its "if no" branch, said in STATE; AUTO GL on every machine; nothing claims a hardware fact it has not measured; the scripts written, so a lab booked later runs them as they stand | owner |
| R23 | Smart App Control blocks an unsigned PlayMap on a clean Windows 11 machine, Home or Pro, with no per-app exception, and blocks the shipped unsigned natives even once PlayMap's own files are signed | High | High | The MSI's launch condition refuses with the words before anything runs; preflight's red row; `docs/INSTALL.md`'s steps; signing next round covers the shipped natives too | installer, platform |
| R24 | A Home node moves under the show: feature updates on Microsoft's schedule, drivers in the monthly update, no deferral | High | Medium | No Update policy written on Home (an attempt is not a fact); pause and active hours in the guide; `MACHINE LOCK` observes and names a changed driver; preflight's pending-reboot row | platform, installer |
| R25 | The WiX toolset does not build from its source on the runner within its time-box, or still asks for the fee's EULA gesture | Medium | Medium | M17 on the installer's first day; Inno Setup 7.1 as the measured fallback with every machine-level action in the supervisor's commands; the gesture, if asked, the owner's call (§9 decision 3) | installer |
| R26 | Dock for Avalonia has essentially one maintainer and frequent releases | Medium | Medium | One exact release pinned, at least two weeks old; behind the desk's seam; M18 on the first day; the desk's own splitters and rails as the fallback | desk-shell |
| R27 | Two layers (automatic under, hand over) confuse a person, or a re-run surprises them | Medium | High | `HAND` says both in words; every re-run's reply and staged card list the hand edits it kept; values typed by hand are never overwritten without `REPLACE` or an accept; `HandOverAutoTests` | canvas, workflows |
| R28 | Rung 1 (a card's DMA into a wrapped D3D12 heap) does not hold on a real card | Medium | Low | M16 on WARP, M20 in the lab; rung 2 (one upload copy) is designed and tested as the fallback, the rung said in STATE | capture |
| R29 | The desk's work grows past its five packages (menus generated for 300 rows, guides, panes) | Medium | Medium | Each package at its cut line with stretch below it; `MenuCoverageTests` names any row it lets off; the guides reuse `SiteGuide`'s contract | desk-shell, desk-map, workflows |

---

## 7. The lab checklist

What the Windows job cannot prove. Each line's script is written by the package named, inside its budget, in `docs/round3/lab/`
before that package merges; a line's result is recorded with the machine, driver and build it ran on. The benches that answer
M1–M3 are the spine's (`bench/r3-present`) and desk-ux's (`bench/r3-avalonia-import`).

**The lab itself.** The owner's answer (§9 decision 11): "Full physical hardware testing is not available yet." No machine of this
inventory exists this round, so every line below is reported not run, with its reason ("no lab: the owner's answer 5"), never as
passed (D15). Each line keeps its script and pass mark, written by its package inside its budget, so a lab booked later runs it as it
stands; until then each package's papers say what CI and WARP proved and what waits for the line.

| Machine or rig | Lines | Status |
|---|---|---|
| Two RTX PRO nodes with RTX PRO Sync, four outputs each, a house-sync generator at 59.94 Hz | L1, L2, L5, L6, L9, L10, L14, L15, L21, L22 | not available |
| A GeForce RTX node | L1, L2, L4, L8, L16, L17 | not available |
| A Radeon RDNA 3 or 4 node | L1, L2, L7, L9, L10, L15, L20 | not available |
| An Intel Arc node; a hybrid Intel CPU; a dual-CCD X3D CPU | L1, L2, L7, L10, L15, L20 | not available |
| A capture card on an HDMI loop-through; an LED processor with a monitor output; a photodiode rig | L1, L12 | not available |
| An HDR10 display | L13 | not available |
| A clean Windows 11 IoT Enterprise LTSC 2024 install with auto-logon; clean Windows 11 Home and Pro installs | L11, L18, L23 | not available |
| Three operators and a dark room | L19 | not available |
| Two projectors on a flat screen; a camera on a tripod | L24 | not available |
| A DeckLink card and a USB capture device on an RTX node and a Radeon node, a loop-through cable, house sync | L25 | not available |

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
| L18 | The unsigned installer as a person meets it | clean Windows 11 Home and Pro | downloaded through a browser: SmartScreen's "unknown publisher" prompt and More info ▸ Run anyway as `docs/INSTALL.md` shows them; UAC's "Unknown publisher"; with Smart App Control enforcing, the launch condition's words before anything runs; off, the install completes and `SHA256SUMS` matches | installer |
| L19 | The formative dark-room session | the desk, three operators, a dark room | each critical task (GO, panic, recovering after a desk restart, finding a faulted output) done without help, or the finding recorded in `docs/UX.md` | desk-ux |
| L20 | The GL backend after the cut, on Windows drivers | NVIDIA, AMD, Intel nodes | the pixel suites within their GL tolerances and an hour's show on AUTO (GL) with no more missed vblanks than `23f162f` on the same machine | rhi |
| L21 | **M13**: the first ten minutes | the RTX PRO node, the MSI's ReadyToRun build | p99.9 frame time over a show's first ten minutes recorded against minutes 10–20 | installer |
| L22 | A precise Windows thread time: a thread's cycles (`QueryThreadCycleTime`) at the time-stamp counter's frequency from the platform, never a rate measured against the stopwatch | the RTX PRO node with Memory Integrity on (Windows as Hyper-V's root partition), then off | a spinning thread's cycles at that frequency within 1% of its running time from ETW's context-switch, interrupt and DPC events, idle and beside a network and disk interrupt load; its ticks (`GetThreadTimes`) under the node's own timer claims (`IGNORE_TIMER_RESOLUTION`, the high-resolution timer) still in the step `ThreadTime.ResolutionNs` states; until it passes, a Windows thread time is its ticks and the survey slice's budget there is proven by the stopwatch alone (docs/FACADE.md) | platform |
| L23 | A Windows 11 Home node | a clean Home 26H2 install as its setup leaves it (a Microsoft account, Smart App Control evaluating, device encryption on, OneDrive folder backup on) | the local show account and auto-logon by `docs/INSTALL.md`; the supervisor starts the engine in its session after a cold boot (M9); preflight's edition rows and hard rows each as their facts say; an hour's show with L1's present and decode measures beside a Pro machine's; Memory Integrity on and off, and the media volume encrypted and not, each with its frame times; what the air shows during a UAC prompt; a projector's power cycle with and without EDID emulation | platform, installer |
| L24 | The flat-screen guide on real projectors | two projectors on a flat screen, side by side and then stacked; a camera on a tripod | the flat guide by hand to a blended, content-carrying canvas within 10 minutes, then again with `ALIGN`; a hand corner nudge kept through a re-run of the align; the stack's misregistration at or below 0.5 output px p95 after alignment; the wall and the object guides on a facade model with two projectors | workflows, canvas, desk-map |
| L25 | A capture card's pass-through (`design/capture.md` §12) | a DeckLink card and a USB capture device on an RTX node and a Radeon node; a loop-through; a photodiode; house sync into RTX PRO Sync and the card | the rung each reached (M20); zero CPU copies on rung 1; loop-through glass-to-glass at or below 2 output frames at p95 unlocked and about 1.5 locked; the card's reference read as locked; one repeat about every 16.7 s at 59.94 → 60; Media Foundation's figure beside the native path's; the photodiode's beside the loop's | capture |

---

## 8. Coverage

### 8.1 Every line of the direction

| The direction's line | Packages | Round 3 reaches | Proved by |
|---|---|---|---|
| "an installed Windows application" | installer, platform | A per-machine MSI on every Windows 11 edition, unsigned and named so this round with its SHA-256 list; the engine supervised in the show user's session; machine-level actions done once; the version rules an installer needs | `InstallerTests`, `SupervisorTests`, `MsiVersionTests`, `PreinstallCheckTests`, `UnsignedArtifactTests`; L11, L18, L23 |
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
| "UX design … user interface" | desk-ux, desk-gpu, desk-shell, desk-map, workflows | The GO guard, blind in grey, the Oops history, dark-room and daylight themes, status by shape and word, preview age, previews without a CPU copy; the guides, docked panes, generated menus, the palette and console, colour in three channels, every handle by one pixel | `GoGuardTests`, `BlindLookTests`, `StatusRuleTests`, `PreviewShareTests`, `MenuCoverageTests`, `AreaColourTests`; L3, L19, L24 |

### 8.2 The owner's answers and requirements of 18 October 2026

| The owner's words | Where it is answered | Packages | Proved by |
|---|---|---|---|
| "PlayMap also needs to run on Windows 11 Home as well." | D4; §3.3 items 4 and 6; §3.5 items 3, 7 and 8; §6 R23, R24; §7 L23; `design/installer.md` §7, `design/platform.md` §5–§6 | platform, installer, spine | `IdentityTests`, `EditionRowTests`, `InstallerTests`; L23 |
| "Use hardware lock where possible, fall back to software lock." | D5; §3.14 items 1–2 and its entry condition; `design/present.md` §13, §18; `design/capture.md` §6 | framelock, capture | `PresentBarrierTierTests`, `SoftwareLockWordsTests`, `SingleClientTests`, `PassThroughTests`; L5, L25 |
| "Leave unsigned for now" | D6; §1.6 item 2; §3.5 item 6; §5.4 item 5; §7 L18; §9 decision 2; `design/installer.md` §6 | installer | `UnsignedArtifactTests`; L18 |
| "Do not use WIX paid services." | D6; §1.5 M17; §3.5 item 2; §4 (`nuget.config`, the notices); §6 R25; §9 decision 3; `design/installer.md`'s decision and §1a | installer | `WixSourceTests`, `InstallerTests`, `LicenceTests` |
| "Full physical hardware testing is not available yet." | D15; §1.5; §1.6 item 6; §2.1; §6 R22; §7; every package's "Manual (lab)" | every package | every line reported not run |
| "All 16" | §3.0; §5.1; §9 decision 1 (the sixteen kept in their waves, the five new packages the plan's proposal) | all | — |
| The four-click quad "needs to stay for a user to come back to if clicked away from in the menu" | §3.6 item 7; `design/desk-workflows.md` §6.1 | desk-ux | `QuadDraftTests`, `QuadSelectTests` |
| "Each corner/pin Needs to be editable and single pixel nudge-able." | §3.18 items 1–3; `design/desk-workflows.md` §6.2–§6.4 | desk-map | `HandleVerbTests`, `HandleEditTests`, `LoupeTests` |
| "corners can also be dragged in the four click quad." | §3.6 item 7; §3.18 item 2; `design/desk-workflows.md` §6.1–§6.2 | desk-ux, desk-map | `QuadCornerDragTests`, `HandleEditTests` |
| "The media allocated to a screen should be shown in the UI." | §3.18 item 4; `design/desk-workflows.md` §7 | desk-map | `ShowingTests`, `ShowingDeskTests`, `ContentMenuTests` |
| "It needs simple workflows UI areas, as well as areas advanced techniques and programming areas." | D11; §3.17 item 2 (the console); §3.20 items 3–4; `design/desk-workflows.md` §2 | desk-shell, workflows | `SimpleProTests`, `WorkflowsHomeTests`, `ConsoleTests` |
| "A two projector bland [blend] or double stacked projector blend on a standard flat screen … intuitive workflow and interface." | §3.19 items 1–2; §3.20; `design/desk-workflows.md` §5.1, §8 | canvas, workflows, desk-map | `CanvasSpanTests`, `SpanPatternTests`, `FlatGuideTests`; L24 |
| "A single map of a 2d visual … onto a 3d surface like a house wall … viewed as intended without distortion. (Can be blended to more projectors)" | §3.20 items 1–3; `design/desk-workflows.md` §5.2 | workflows | `WallGuideTests`, `ViewpointFitTests`, `SurfaceMapTests` |
| "A simple workflow option to 3d map surfaces with 1 or 2 projectors." | §3.20 items 1–3; `design/desk-workflows.md` §5.3 | workflows | `ObjectGuideTests` |
| "Multiple outputs can use the same pattern, media or generator to span a combined screen canvas." | §3.19 items 1–2; `design/desk-workflows.md` §8 | canvas | `CanvasSpanTests`, `SpanPatternTests`, `CanvasSourceTests` |
| "All features can be accessible from menu items or clever, intuitive, interactive right click menus." | D11; §3.17 items 2–3; §5.4 item 6; `design/desk-workflows.md` §9 | desk-shell | `MenuCoverageTests`, `PaletteTests` |
| "Video capture cards should be ultra fast in their pass through signal flow." | D14; §3.11 item 4; §3.21; `design/capture.md` | capture, video, d3d12-passes | `CapturePathTests`, `WrappedHeapTests`, `PassThroughTests`, `LiveUploadTests`; L25 |
| "Remember to consider Games design UI/UX for professional and intuitive interfaces and UI." | `design/desk-workflows.md` §6.3, §9.3, §12 | desk-shell, desk-map, workflows | `PushBudgetTests`, `ToolMemoryTests`, `HandleLatencyTests` |
| "Keep in mind gaming and medical code architecture, for speed, ultra-low latency and any database or server use." | §3.9 item 6; `design/desk-workflows.md` §12–§13; `design/capture.md` §3, §7 | stability, desk-shell, capture | `JournalDropTests`, `UiStallTests`, `CaptureLatencyTests` |
| "UI panels should be draggable and collapsible." | D11; §3.17 item 1; `design/desk-workflows.md` §10 | desk-shell | `DockLayoutTests`, `CollapsedLightTests` |
| "Consider subtle uses of colour in the UI/UX to enhance cognition and awareness of areas and workflow stages." | D16; §3.17 item 4; `design/desk-workflows.md` §11 | desk-shell | `AreaColourTests` |
| "All mapping can also be manual, from initial setup to adjusting the auto scan and setup." | D12; §3.19 item 3; §3.20 item 2; `design/desk-workflows.md` §3 | canvas, workflows, desk-map | `HandOverAutoTests`, `HandPoseTests`, `ScanKeepsHandTests`, `ManualSetupTests` |
| "Remember - Ai integration throughout." | D13; §2.1 (AI reach); §3.17 item 5; §3.20 items 1 and 5; every new package's `Assistant…Tests`; `design/desk-workflows.md` §4 | every new package | `AssistantGuideTests`, `McpGuideTests`, `AssistantPaneTests`, `AssistantHandleTests`, `AssistantSpanTests`, `AssistantCaptureTests` |
| Calls 7–11 (round 2's) | §9 decisions 14–18; `docs/round2/CARRYOVER.md` | — | — |

---

## 9. Decisions: the owner's answers, and what stays open

The owner answered on 18 October 2026. Each decision below says the answer, in the owner's words where they gave them, and what it
changes; the calls round 2 left with the owner (`docs/round2/CARRYOVER.md`, calls 7–11) are decisions 14–18.

1. **The size of the round.** *Answered: "All 16."* The sixteen packages stay in their waves (§5.1) and none is deferred, so the old
   deferral order no longer applies. The owner's requirements of the same day came after that answer: this revision proposes five
   packages for them (desk-shell, desk-map, canvas, workflows, capture; §3.17–§3.21, about 107 hours, twenty-one sessions in all) and
   puts the smallest requirements inside the sixteen (desk-ux's quad, video's live upload at arrival, stability's journal drops,
   d3d12-passes' late latch). *Open:* whether the round carries the five. If the owner keeps it at sixteen, the five go to the next
   round as a block, deferred in this order — workflows (it builds on the other four), canvas, capture, desk-map, desk-shell — with
   their §8.2 rows marked "Below bar this round"; desk-ux's quad item stays in the sixteen either way.
2. **Signing identity.** *Answered: "Leave unsigned for now."* Builds and installers are unsigned and named `-unsigned`, each with a
   `SHA256SUMS` file (§3.5 item 6); the signing step, the protected signing branch and Azure Artifact Signing move to the next round,
   and signing's three hours shrink to the one the unsigned item takes. A person meets SmartScreen's "unknown publisher" prompt and UAC's "Unknown
   publisher"; Smart App Control, on by default on clean Windows 11 installs, blocks unsigned code with no exception, so the MSI's
   launch condition refuses with the words while it enforces [inst §4]. Nothing verifies an update automatically this round: there is
   no updater, and the person checks the published hashes. When an identity exists it signs the shipped third-party natives too, or
   Smart App Control still blocks them.
3. **WiX's Open Source Maintenance Fee.** *Answered: "Do not use WIX paid services."* No fee, no FireGiant service and no official WiX
   binary: the toolset is built from its MS-RL source at a pinned tag in CI (D6, §3.5 item 2), which the fee's EULA does not bind
   [inst §2]; no WiX issue is filed. *Open, only if M17 finds it:* if the self-built toolset still asks for the EULA gesture, the owner
   chooses between passing it (the agreement does not apply to self-compiled binaries) and the Inno Setup fallback.
4. **FFmpeg inside the MSI.** The pinned Windows build is the LGPL variant; the installer package switches both CI jobs to it on its
   first day and confirms it. Unchanged.
5. **AUTO's switch** per vendor tier needs the owner's sign-off on L1's record. With no lab (decision 11), AUTO stays OpenGL on every
   machine all round, said in STATE; `PRESENT PATH D3D12` is the explicit choice.
6. **Show nodes without the desk.** Recommended: show nodes run the engine only with the desk on another machine — on IoT Enterprise
   with Shell Launcher, on Home and Pro under the supervisor with Explorer underneath (D4) — and a one-machine show stays supported,
   the desk's monitor kept primary.
7. **Aftermath's library** shipped in the MSI, or loaded where the user puts it, by its terms. Open.
8. **Realtime GPU priority** stays off until L10 measures it — all round, with no lab.
9. **The air hazard file** (stability's stretch 7) as a standing rule for the air's code: each hazard traced to a fault-injection
   test, borrowing medical risk practice as a design tool, not as a claim of compliance; `design/desk-workflows.md` §12 lists the
   desk's hazard-related use scenarios beside it.
10. **`main` has moved** past `23f162f`, the map's base, to `6f24310` (round 2 closed), with the desk's merges and the version scheme
    among its commits. They touch neither the renderer nor Media's decode; the version scheme adds the build to the web port's MCP
    server info and a stamp check after each CI job's build (`tools/ci-stamp.ps1`), and the desk's `PreviewSource.cs` keeps the same
    copy path [map header]. The spine rebases at start; the desk's packages read `research/desk-today.md`, made at `6f24310`.
11. **The lab.** *Answered: "Full physical hardware testing is not available yet."* D15: every lab line stays, with its script and pass
    mark, and is reported not run; every question only the lab answers stands on its "if no" branch; each package's done says what CI
    and WARP proved; nothing claims a hardware fact it has not measured; round done item 6 is restated (§1.6). A lab booked later runs
    the scripts as they stand.
12. **The platform's defaults.** As planned, `PROCESS HIGH`, HighQoS, MMCSS, CPU sets, EcoQoS and keep-awake are on by their
    documented causes; the GPU scheduling class is off until L10 measures it, and the GC's show mode is now platform's stretch beside
    the pause histogram it waits for — both stay off all round with no lab. The owner may prefer every claim off, or every claim on;
    either is one line in `design/platform.md` §2.
13. **M4's date.** No lab books the barrier sample, so framelock builds the one-swap-chain shape, correct whatever the answer, and a
    client per output waits for the record (§3.14). Closed.
14. **The desk sends `VIDEO STOP`, `PANIC CLEAR` and `MASTER` at every press** though a Companion key holds each (call 7). *Answered:
    "Keep."* The menu bar, the palette, the console and the generated menus keep the rule (§3.17 item 2).
15. **A card's `APPROVE`, pressed twice on a Companion key, is pressed once on the desk** (call 8). *Answered: "Approve."* It stays one
    press, on the cards and in the assistant's pane (§3.17 item 5); `docs/DESK.md` states it as the rule.
16. **The controllers preflight rows carry no fix line** (call 9). *Answered: "Leave rows as information for now."* They stay
    information rows; the edition's rows on Home follow the same pattern (D4).
17. **`/api/context` sits behind the token** (call 10). *Answered: "Keep it closed."* Patterns reads PlayMap's context over the wire
    (`SUBSCRIBE CONTEXT`), as today.
18. **Which commands Patterns' Eye sees** (call 11). *The owner asked: "If PlayMap uses up to 128 commands, shouldn't Patterns use 128
    too?"* PlayMap's context lists every mutating verb with no cap, the panic, air, transport and Show Mode rows first (195 in the
    busy golden); the Show Bus contract keeps 64 — Patterns' reader takes the first 64, and PlayMap's own reader of a peer's context
    keeps the contract's 64. Changing that is Patterns' to do, and the Patterns tree is not this repository's: the request — raise the
    contract's command cap to at least 128, or read every command — is recorded for Patterns' maintainers in `docs/round2/CARRYOVER.md`;
    PlayMap's reader follows the contract once it changes, and `ContextOrderTests` (§5.6 item 7) keeps the air's verbs first whatever
    the cap.
19. **A picture for the assistant** (workflows' stretch 7). A still of a view sent to an assistant model that reads pictures is off by
    default; whether one may ever go to a provider off the machine is the owner's call.
20. **Dock for Avalonia** (D11) has essentially one maintainer; the plan accepts it pinned and behind a seam. Actipro Docking, the
    commercial alternative, is used only by the owner's decision.
