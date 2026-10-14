# WP "framelock": framelock, genlock and measured multi-node sync

*Design spec for PlayMap's round-2 professional features. Written 13 October 2026 against `main` (`c12b186`,
`/home/user/mapper`). It builds on `r2/research/present.md` (cited as **[P §n]**) and on the "present" design
(`r2/design/present.md`, cited as **[D-present §n]**). It also folds in the carry-over gaps in its area from
`r2/CARRYOVER.md` (**[CO]**). Facts checked on this machine while designing are in Appendix A (**[A]**).*

**The decision in one paragraph.** One state machine runs every output's sync state, from `house-locked` down to
`free-unknown`, and its rule is **never stall the air**. Four tiers sit under it:

- **NVIDIA RTX PRO Sync**, through NVAPI D3D1x swap groups and barriers, joined non-blocking.
- **Intel IGCL genlock**, which is optional.
- **GLX_NV_swap_group** on Linux.
- **A measured software tier** for everything else. Each output's real refresh (in ppm) and its vblank phase in
  leader time are reported on UDP 9942. The leader turns them into per-pair phase in µs and a "next slip in
  4 min 12 s" forecast. Mixed 59.94/60 rates are refused.

Under hardware lock, content frames are chosen from the **sync board's shared frame counter**. The counter is
24-bit, wraps are handled modulo 2²⁴, it is re-anchored every 60 s at a named count, and it is **never reset in a
show**. Sync is not taken on trust. **A camera, or a phone's slow-motion clip, reads a frame code** that every
output draws as a pure function of show time. That produces a **sync certificate**: frame-identity offsets plus
sub-frame phase, observed in light. The carry-over gaps in this area are fixed here too:

- the leader's playlist anchors now reach the followers;
- following no longer makes non-group playlists jump;
- the node's sync role survives a restart.

---

## 1. Goal and the bar

### 1.1 What the best competitors do

| Who | With sync hardware | Without | Health and UX | Source |
|---|---|---|---|---|
| **disguise** | A sync card per server takes genlock. **"Apply Genlock" configures all machines with one button**, and "a machine does not need to be running Designer". The same resolution, refresh and timing are required, with matching EDIDs. Genlock can be derived from PTP | Apply Framelock | Feed View and a Network Status Widget with Genlock and Framelock icons. The Output Sync Test compares `tRender`, `frameTime` and `deltaTime` across machines (software timers) | [P §7] |
| **Pixera** | Quadro Sync II, configured in the NVIDIA Control Panel. The same EDID on all outputs; the engine must be restarted; "if the setup changes… the sync must be reactivated" | — | — | [P §7] |
| **WATCHOUT 7** | Hardware Sync Groups use the board's frame counter as the timing source. **It is 24-bit and reset just before overflow, giving a ~4-frame glitch about every 3 d 5 h.** **"A node whose NVIDIA sync is not valid stalls on the frame barrier and stops presenting frames."** | NTP, "typically within a millisecond", with a resync above ~100 ms. "NTP… does not align GPU output" | The Nodes window shows NTP Diff | [P §7] |
| **Unreal nDisplay** | NVIDIA swap lock (DX11/12) | TCP barriers every frame. **On a barrier timeout "all nodes shut themselves down"** (4.27) | — | [P §7] |
| **Resolume** | Points to GPU-level sync | "Not necessarily frame-sync" | — | [P §7] |

### 1.2 How PlayMap matches each point

| Point | PlayMap |
|---|---|
| One-button apply across machines (disguise) | `SYNC APPLY` on the group's leader. Each follower joins through its own policy, because the lock intent rides the anchor stream it already follows (§3.8). The reply waits for the *observed* lock on every node and answers per node and output |
| Per-node and per-output status (disguise, Pixera) | Chips `house-locked`, `framelocked`, `grouped`, `soft-locked`, `free-measured` and `free-unknown`, plus `joining` and `fell-out`. They appear in STATE, the context (Patterns' Eye), Companion and the desk |
| A display change drops the lock (Pixera) | A mode, EDID or monitor change on a locked output makes it leave, says so, and offers `SYNC REJOIN`. The lock is never silently lost |
| Counter as the timing source (WATCHOUT) | Under lock, frame = f(anchor, (count − count₀) mod 2²⁴) (§3.5) |

### 1.3 How PlayMap beats them

1. **Never stall the air.** WATCHOUT's documented behaviour is a stall, and nDisplay's is a cluster shutdown. PlayMap instead:
   - joins non-blocking;
   - waits on each output's waitable object with a 3-refresh timeout;
   - on a stall, drops that output out of the barrier and keeps presenting free-run, with a red alarm;
   - **never rejoins on its own while armed** (§3.4).
2. **No counter-reset glitch.** PlayMap never resets the counter. It uses modulo-2²⁴ deltas and re-anchors every 60 s at a count named in advance, so every node switches anchor on the same vblank (§3.5).
3. **Numbers instead of hope without sync cards.** Per output it publishes:
   - the measured refresh to six decimals, and its ppm against the leader's clock;
   - its vblank phase in µs against the group's reference output, with the uncertainty;
   - **when the pair will next slip a frame**.

   Mixed 59.94/60 is refused in a group. WATCHOUT offers only "within a millisecond" of clock (§3.6).
4. **Sync observed in light, not read from timers.** `SYNC CERTIFY` takes a live camera or **a phone slow-motion clip dropped on the desk** and reads a frame code from every output in view. It writes a **sync certificate** with:
   - frame-identity offsets (p50/p99, worst pair);
   - sub-frame phase with a standard error;
   - its agreement with the reported numbers (§3.7).

   disguise's Output Sync Test compares software timers.
5. **Plain words from the board.** For example:
   - "the framelock cable on Stage left is plugged into a network switch" (`RJ45_Ethernet`);
   - "house sync arrives at 59.94 Hz but the outputs run at 60.00 Hz";
   - "sync firmware 3.6 on a Blackwell card has known house-sync issues with driver 596.86 [P §6.1]";
   - "Left is composed, not flipped: a window is over its display".
6. **Light leaves every lens in the group at the same show time.** The group's air latency is the slowest projector's (present's delay ring per output, and the group maximum here). `LATENCY MEASURE` finally runs (§3.9).
7. **Cheap fleets get real numbers today, and a path to real lock.** IGCL genlock on Intel (S2) is something no competitor in [P §7] offers card-free.

---

## 2. Scope this round

### Must

| # | Item | Acceptance (checkable) |
|---|---|---|
| F1 | **Anchors reach the followers, and the role is kept** [CO]:<br>• `SUBSCRIBE ANCHORS` on the leader's wire, and `ANCHOR <group> <json>` push lines;<br>• a follower adopts them through its own `PLAYLIST ANCHOR`;<br>• `NodeSettings` keeps `SyncRole`, `SyncGroup`, `SyncLeader` and `FollowPatternsBlackout`, restored at start. | Two real nodes in one process (the existing `HostRig`): the leader's `PLAYLIST GO` on a group playlist makes the follower's playlist the same item and anchor, read in the follower's STATE, within 200 ms. After restarting the follower it is a follower again (STATE `sync.role`) and follows the same leader. The follow-blackout switch survives a restart |
| F2 | **Following never makes a non-group playlist jump** [CO]:<br>• `NetworkClock` slews corrections under 1 ms at 200 ppm or less;<br>• it steps larger ones and raises `Stepped(Δ)`;<br>• the engine rebases every non-group anchor and every scheduled `AT` by +Δ on the lane. | A node plays a non-group playlist, then `SYNC FOLLOW`s a leader whose clock is 3 h 12 min ahead. The playlist's media position is continuous within 1 ms across the step. Leader time read in a tight loop never runs backwards, and never faster than 1 + 200 ppm + drift during a slew |
| F3 | **Vblank reports `PMVB` on UDP 9942** (fail closed), and the leader aggregates them:<br>• per-output ppm and phase;<br>• per-pair phase, uncertainty and next slip;<br>• the group verdict;<br>• mixed-rate detection.<br>STATE `sync.outputs[]`, `sync.pairs[]` and `sync.verdict`; context items for the Eye. | Codec: round trip, and every malformed byte refused. A 100k-packet Pcg32 fuzz accepts none. Aggregation against closed form on synthetic nodes: phase within 1 µs, slip time within 0.1 %. A 59.94 + 60 group reads `mixedRate:true` and amber. Two in-process nodes over loopback UDP list both nodes' outputs in the leader's `sync.outputs` |
| F4 | **The lock machine:**<br>• `FrameLockMachine` (pure, Core);<br>• the NVAPI tier (`NvFrameLock`, Windows, compile-only);<br>• the never-stall watchdog;<br>• frame-count content selection, 24-bit wrap and 60 s re-anchor;<br>• no auto-rejoin while armed. | At least 25 scripted transitions with `FakeFrameLockDriver`: join to lock after 120 good refreshes; a stall at 3T leaves the barrier and the output keeps presenting; a board-sync loss debounced over 3T; no rejoin while armed; auto-rejoin after 10 s at an idle boundary while disarmed; a cable into a switch is refused with words. Counter maths over 4 simulated days at 120 Hz. Anchor switch at a named count, with identical content time on 3 simulated nodes for every vblank across 2 wraps |
| F5 | **Verbs:** `SYNC APPLY`, `SYNC LEAVE`, `SYNC REJOIN`, `SYNC MARKS`, `SYNC CERTIFY`, `SYNC REPORT` and `SYNC LATENCY`. `SYNC STATUS` is extended | The router tests assert every refusal by its reason field: armed, follower, no board, mixed rate, marks not up |
| F6 | **The frame code and the certificate:**<br>• `pattern.synccode` (generator and shader);<br>• `SyncCodeReader` (Core, pure);<br>• `SyncCertifier` (Core, pure);<br>• a live camera (`IScanCamera`) or a clip file (Media). | The code renders on llvmpipe and decodes exactly from `ReadOutputAsync` pictures. A simulated camera (homography, blur σ = 1.5 px, noise σ = 6, gamma 2.2, two-frame exposure mixing, rolling shutter) recovers injected offsets of 0, +1, −1 and +2 frames exactly. The phase is within 3 standard errors over 600 samples. A camera locked to the display rate is detected and its phase refused |
| F7 | **Group air latency and `LATENCY MEASURE`:**<br>• the leader publishes the group's largest air latency; each node uses max(local, group);<br>• `LATENCY MEASURE` flashes an output raw, times the camera's view of it (`LatencyEstimator`, already in Core) and writes `Output.LatencyMs` as one edit. | The simulated camera with an injected 50 ms output delay measures 50 ms ± one camera frame. Two outputs measured 17 and 50 ms give the present WP's delay ring 2 frames at 60 Hz. The group latency in the follower's STATE equals the leader's maximum |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Rolling-shutter phase refinement.** When two codes share sensor rows, the row at which each code changes within one camera frame gives the phase directly. Line time is calibrated from consecutive transitions of one output | Synthetic rolling-shutter images: phase within 100 µs at a 20 ms readout |
| S2 | **Intel IGCL genlock tier** (`ctlGetSetDisplayGenlock`: GET_TOPOLOGY, VALIDATE, ENABLE, DISABLE; `ctlGetVblankTimestamp`) → `soft-locked` | Compile-only. The state machine is driven by `FakeFrameLockDriver` with the IGCL fact shape |
| S3 | **Linux NVIDIA on the GL path:** `glXJoinSwapGroupNV`, `glXBindSwapBarrierNV`, `glXQueryFrameCountNV` | Run-time detection is tested. Mesa lacks it, so the fallback is what runs. Lab |
| S4 | **Preflight sync checks exported for showsafety's preflight:** mixed rates; mode timing equality (`QueryDisplayConfig` → `DISPLAYCONFIG_VIDEO_SIGNAL_INFO`); board firmware against a known-issues table (`sync-known-issues.json`, data); a cable into a switch; VRR/G-SYNC state (NVAPI DRS) | The table loader and rules are pure and tested here. The probes are compile-only |
| S5 | **A board configuration helper.** `NvAPI_GSync_SetSyncStateSettings` **needs administrator rights** [A2], so the engine never calls it. A separate `PlayMap sync-board master\|slave\|off` entry, run once elevated by the operator (UAC on that machine), configures the board | Compile-only. The argument and plan logic are tested |

### Later

| # | Item |
|---|---|
| L1 | D3D12 present barrier (`NvAPI_D3D12_*PresentBarrier*`, `NvAPI_QueryPresentBarrierFrameStatistics`) through D3D11On12 or a D3D12 path [P §6.2] |
| L2 | Intel SW Genlock (MIT, Linux, DPLL steering, needs root) as a separate privileged helper [P §6.4] |
| L3 | AMD S400 status (legacy) [P §6.3] |
| L4 | `VK_EXT_present_timing` on Linux; PTP-disciplined show time (L4 on the roadmap) |
| L5 | A photodiode probe: a USB microcontroller sampling two photodiodes at ≥ 20 kHz, line protocol `PD <t_us> <ch> <edge>`, as the highest-grade certificate source |
| L6 | Static rate trim through vendor custom timings [P §8, point 4] |

---

## 3. Architecture

### 3.1 Components

```
PlayMap.Core
  Sync/FrameLockMachine.cs   per-output lock states, transitions, alarms (pure)
  Sync/LockCounter.cs        24-bit unwrap, anchors, content time at a count (pure)
  Sync/VblankReport.cs       the PMVB packet codec (fail closed)
  Sync/SyncBoard.cs          the leader's table of reports → per-output and per-pair numbers, verdict (pure)
  Sync/SyncCode.cs           frame-code layout, Gray code, parity (shared by the generator and the reader)
  Sync/SyncCodeReader.cs     gray8 camera picture → decoded bands (pure)
  Sync/SyncCertifier.cs      decoded samples → certificate (pure)
  Time/ClockOffsetEstimator.cs (existing) NetworkClock: slew/step state, Stepped event
  Content/GeneratorCatalog.cs  + pattern.synccode
PlayMap.Render
  Generators/ShaderGenerators.cs + SyncCodeGenerator;  Shaders/ShaderLibrary.cs + "synccode" (25th program)
  Sync/IFrameLockDriver.cs   the driver seam
  Sync/NoFrameLock.cs        the software tier (always available)
  Platform/Windows/NvApi.cs        nvapi_QueryInterface + function pointers (IDs [A1])
  Platform/Windows/NvFrameLock.cs  IFrameLockDriver over NvApi + IFrameLockTarget [D-present §4.4]
  Platform/Windows/IgclGenlock.cs  (S2)
  Platform/Linux/GlxSwapGroup.cs   (S3)
PlayMap.Net
  Sync/ClockSync.cs          (existing) the responder demultiplexes PMVB to an IVblankSink
  Sync/VblankReporter.cs     follower → leader, 1 Hz per output and on change
  Sync/AnchorFeed.cs         leader: latest ANCHOR per group, pushed to ANCHORS subscribers
  Links/AnchorFollower.cs    follower: subscribes to its leader's ANCHORS over the wire
PlayMap.Engine
  EngineCore.FrameLock.cs    the verbs; rebase on Stepped; @lock application
  Rendering/RenderHost.Lock.cs  lock driver on the render thread; content time under lock; watchdog
  Sync/SyncCertify.cs        camera/clip runner → SyncCertifier → certificate file + STATE
  Sync/LatencyMeasure.cs     LATENCY MEASURE over IScanDisplay + IScanCamera + LatencyEstimator
```

The module fences hold:
- The machine, the counter maths, the codec, the board, the code and the certifier are pure Core.
- NVAPI and IGCL live in Render's Windows platform folder, beside the swap chains they act on.
- The Net code moves bytes. The engine decides.

### 3.2 Per-output lock states

| State (wire word) | Meaning | Evidence that must be present (attempts are not facts) |
|---|---|---|
| `house-locked` | Board genlocked to house sync | `NV_GSYNC_STATUS_PARAMS.bHouseSync = 1`; `houseSyncIncoming` = nominal ± 0.5 %; `NV_GSYNC_STATUS.bIsSynced = 1`; barrier bound; frame count +1 per refresh for 120 refreshes |
| `framelocked` | Board framelock across nodes | `bIsSynced = 1`, `bIsSyncSignalAvailable = 1`, barrier bound, the count advancing. The leader confirms equal counts across members (§3.5) |
| `grouped` | Swap group inside one node, no barrier | `NvAPI_D3D1x_QuerySwapGroup` → group ≠ 0, barrier = 0, the count advancing |
| `soft-locked` | IGCL genlock (S2) | `ctl_genlock_args_t.IsGenlockEnabled` and the vblank timestamps aligned |
| `free-measured` | Free-run on the DXGI flip path with settled statistics | present WP: `settled`, mode iflip with evidence ≥ hint [D-present §3.6, §3.10] |
| `free-unknown` | Composed, GL, offscreen or not settled | Shown grey or red. Certificates are still allowed, but "reported" numbers are marked approximate |
| `joining` | Join requested, evidence accumulating | — (≤ 5 s, then success or `fell-out`) |
| `fell-out` | Was locked; the lock broke; presenting free-run | The reason is kept: stall, board unsynced, count stalled, device busy, display changed |

### 3.3 `FrameLockMachine` (pure, Core)

**Inputs: facts.** They arrive per output once a frame from the render thread, and board facts once a second from a monitor thread:

```csharp
public readonly record struct LockFacts(
    long MonoNs, double PeriodNs, bool Settled, bool IndependentFlip,
    bool BoardPresent, bool BoardSynced, bool SignalAvailable, bool HouseSync, double HouseSyncHz, bool CableIntoSwitch,
    bool Joined, bool BarrierBound, uint SwapGroup, bool CountValid, uint Count24,
    bool WaitTimedOut, bool PresentDeviceBusy, bool DisplayChanged, bool Armed, bool AtBoundary,
    LockIntent Intent);
public readonly record struct LockIntent(bool Apply, uint SwapGroup, uint Barrier, long Seq);
```

**Outputs:** the state, the actions (`Join`, `Leave`, `Bind`, `Unbind`, `UsePresentOverride`, `RestorePresent`), alarms with words, and the counters (`fellOut`, `rejoins`, `lockedSince`).

**Rules.**

1. **Start a join.** `Intent.Apply` and preconditions OK (board present, synced or able to sync, no cable in a switch, not armed — or armed and blacked out) → `Join(group, blocking: false)`, `Bind(barrier)`, `UsePresentOverride`, state `joining`.
2. **Confirm the lock.** `joining` + 120 consecutive refreshes with `Count24` advancing by exactly 1 mod 2²⁴, `BoardSynced` and `BarrierBound` → `framelocked` (or `house-locked` when `HouseSync`).
3. **Fall out.** Any locked state + one of the following → `Unbind`, `Leave` (non-blocking), `RestorePresent`, state `fell-out`, and a **red alarm** with the reason:
   - `WaitTimedOut` (the waitable did not signal within 3T);
   - the count did not advance for ≥ 3T;
   - `BoardSynced` false for ≥ 3T;
   - `PresentDeviceBusy`;
   - `DisplayChanged`.

   The output keeps presenting free-run on the same vblank pacing. The present WP's waits already never exceed 3T [D-present §3.5].
4. **Rejoin.**
   - `fell-out` + `Armed` → stays. **Never rejoins on its own while armed.**
   - `fell-out` + disarmed + board synced ≥ 10 s + `AtBoundary` (blackout, or no playlist playing) → rejoin (`joining`).
   - Otherwise only `SYNC REJOIN` rejoins.
5. **Release.** `Intent.Apply` false → `Unbind`, `Leave`, `RestorePresent`, and the state becomes `free-measured` or `free-unknown`.

A transition table test covers every edge. The machine has no clock of its own: time comes from `MonoNs` in the facts, so tests are deterministic.

### 3.4 The NVIDIA tier (`NvFrameLock`, Windows; compile-only here)

**Loading.**
- `NativeLibrary.Load("nvapi64.dll")`.
- `nvapi_QueryInterface(uint id) → nint`. It is exported from `nvapi64.dll`, and calls are `__cdecl` (the x64 convention).
- Function pointers are cast to `delegate* unmanaged[Cdecl]<…>`. The interface IDs come from the MIT header `nvapi_interface.h` [A1].
- Struct versions follow `MAKE_NVAPI_VERSION(T, v) = sizeof(T) | (v << 16)` [A2]:

| Struct | Size (x64) | Version |
|---|---|---|
| `NV_GSYNC_STATUS` | 16 bytes | 0x00010010 |
| `NV_GSYNC_STATUS_PARAMS_V2` | 36 bytes | 0x00020024 |
| `NV_GSYNC_CAPABILITIES_V3` | 28 bytes | 0x0003001C |
| `NV_GSYNC_GPU` | 40 bytes (with pointer padding) | 0x00010028 |

A `sizeof` test asserts each managed struct's size against these numbers.

**Board facts** (a monitor thread, 1 Hz; NVAPI GSync calls are not tied to the render thread):
- `NvAPI_GSync_EnumSyncDevices` → handles.
- `NvAPI_GSync_QueryCapabilities` → `boardId`, named from the R615 `nvapi.h`: 856 → "Quadro G-Sync (P358)", 8288 → "Quadro Sync (P2060)", 8289 → "Quadro Sync II (P2061)". Any other id is shown as "sync board 0x<id>" with its capabilities treated as unknown; the RTX PRO Sync board's id is not in that header and is recorded in lab test 7 and added to `sync-known-issues.json`. Firmware = `revision.extendedRevision`. NVAPI documents these as the FPGA major and minor revision [A2]. Their mapping to NVIDIA's "v3.06" naming is **U** (lab test 7).
- `NvAPI_GSync_GetTopology` → GPUs (`isSynced`) and displays (`syncState` UNSYNCED / SLAVE / MASTER).
- `NvAPI_GSync_GetSyncStatus(handle, gpu)` → `bIsSynced`, `bIsSyncSignalAvailable`.
- `NvAPI_GSync_GetStatusParameters` → `refreshRate`, `RJ45_IO[2]`, `RJ45_Ethernet[2]` ("Connected to ethernet hub? [ERRONEOUSLY CONNECTED!]" [A2]), `houseSyncIncoming`, `bHouseSync`.

**Swap groups** (render thread; they act on the present WP's device and swap chain through `IFrameLockTarget` [D-present §4.4]):
- `NvAPI_D3D1x_QueryMaxSwapGroup(dev, &maxGroups, &maxBarriers)`. A result of 0 means no swap groups (GeForce): the tier is unavailable, with words.
- `NvAPI_D3D1x_JoinSwapGroup(dev, swapChain, group, blocking: FALSE)`. Per NVAPI, `blocking = false` means "the Present operation returns immediately" [A2].
- `NvAPI_D3D1x_BindSwapBarrier(dev, group, barrier)`. A barrier of 0 releases it.
- While joined, `IFrameLockTarget.PresentOverride = (interval, flags) => NvAPI_D3D1x_Present(dev, swapChain, interval, flags)`. `NVAPI_DEVICE_BUSY` maps to `PresentDeviceBusy`.
- `NvAPI_D3D1x_QueryFrameCount(dev, &count)` every frame, after the waitable wait. If it measures over 100 µs on a node, it is read every 4th frame and interpolated. `NvAPI_D3D1x_ResetFrameCount` is **never called** in a show. It is only reachable through the S5 helper with the board idle.

**Board configuration.** `SetSyncStateSettings` "will require Administrator privileges" [A2], so the engine never configures the board. When the topology shows no MASTER display, `SYNC APPLY` refuses with the exact fix:

> the sync board on Stage left is not configured: once, as administrator, run `configureSync.exe enable server source=VSYNC display=0,0` on the timing server and `… enable client` on the others (or Frame Lock in the NVIDIA Control Panel), then `SYNC APPLY` again

The order is G-SYNC off, then Mosaic, then Frame Lock last [P §6.1]. The S5 helper automates this for operators who accept a UAC prompt.

**Windowed flip versus exclusive fullscreen.** NVAPI is silent on whether D3D11 swap groups engage with a windowed flip swap chain in independent flip [P §6.2]. `NodeSettings.LockExclusiveFullscreen` (default false) switches the presenter to `SetFullscreenState(TRUE, output)` + `ResizeBuffers` for locked outputs. That is a lab-validated option (lab test 1). With it, losing focus can drop the lock, and the machine reports `fell-out: display changed`.

### 3.5 Content time under lock: the counter (`LockCounter`, pure)

- **Unwrap.** Each node keeps `c64` by adding `(c24_now − c24_prev) mod 2²⁴` per read. A delta over 2²³ is impossible within one frame and is treated as a fault, not a wrap.
- **Width check.** If the driver ever returns a value ≥ 2²⁴, the node switches to 32-bit arithmetic for that board and journals it. The width is **U** for the D3D API: WATCHOUT documents 24 bits for the board [P §7].
- **Anchor.** The leader publishes `{count24: c₀, leaderNs: t₀, periodPs: P, effectiveAt24: cₑ, seq}` on the anchor feed (§3.8):
  - `t₀` is the leader's fitted vblank time for count `c₀` (present's `VblankClock` on the leader's pacer [D-present §3.6]);
  - `P` comes from the leader's 60 s window.
- **Content time.** Each node uses, from count `cₑ` on:

  `t(c) = t₀ + ((c − c₀) mod 2²⁴) × P`

  Every node computes **identical integers** for the same vblank, so every node picks the same content frame: `FrameInputs.LeaderNs = t(c_now + 1) + L_air` replaces `LeaderNowNs + lead + L_air` [D-present §3.5].
- **Re-anchoring** every 60 s:
  - The leader publishes a new anchor with `effectiveAt24 = c_now + 120` (2 s at 60 Hz).
  - Every node switches at that count, on the same vblank.
  - The step between the old and new mapping is the period error over 60 s: at 0.05 ppm that is 3 µs, far below a frame.
  - The counter is never reset. WATCHOUT's scheduled ≈4-frame reset glitch cannot happen [P §7].
- **Stale anchor.** If `(c − c₀) mod 2²⁴ > 2²³` (≈ 38.8 h at 60 Hz) and no anchor arrived, the node falls back to clock-based time and raises an amber alarm, "lock anchor stale".
- **Wrap periods** (2²⁴ refreshes): 3 d 5 h 40 m at 60 Hz, 3 d 21 h 12 m at 50 Hz, 1 d 14 h 50 m at 120 Hz [P §11.2]. The tests cross two wraps.
- **Membership check.** Each node reports `count24` and `lastVblankLeaderNs` (PMVB). The leader checks that

  `count_i − count_L − round((V_i − V_L)/P) ≡ 0 (mod 2²⁴)`

  for every member. A member that disagrees is "not on the same lock chain" and stays `joining`, then `fell-out` with words.

### 3.6 The measured software tier: PMVB reports and the leader's board

**Packet.** `PMVB` v1: 64 bytes, little-endian, UDP to the leader's sync port (9942, the `PMCK` socket). The existing `PMCK` 40-byte packet is unchanged. The responder demultiplexes by magic and length and refuses anything else, as today [P §1].

```
off size field
 0   4   magic "PMVB" (0x42564D50 read LE)
 4   1   version = 1
 5   1   state  0 free-unknown, 1 free-measured, 2 grouped, 3 soft-locked, 4 framelocked, 5 house-locked,
                6 fell-out, 7 joining                       (> 7 refused)
 6   1   flags  bit0 iflip evidence ≥ hint, bit1 composed, bit2 statistics settled, bit3 barrier bound,
                bit4 house sync present, bit5 armed, bit6 board present, bit7 board synced
 7   1   path   0 offscreen, 1 gl, 2 dxgi-flip               (> 2 refused)
 8   4   u32 nodeId (the 8-hex node id as a number)
12   4   u32 outputIdHash (FNV-1a 32 of the output id, UTF-8)
16   4   u32 seq (per node; older or equal than the last seen for (node, output) refused)
20   4   u32 boardFrameCount (low 24 bits significant; the high byte must be zero)
24   8   i64 periodPs           (1 000 000 000 … 50 000 000 000, i.e. 20–1000 Hz; else refused)
32   8   i64 lastVblankLeaderNs (the fitted vblank at refreshIndex, on the leader's clock)
40   8   u64 refreshIndex
48   4   u32 missed60s
52   4   u32 houseSyncMilliHz (0 if none)
56   4   i32 airLatencyUs (0 … 500 000)
60   4   u32 reserved, must be zero
```

**Who may send.**
- A follower's `VblankReporter` sends each Display output's report at 1 Hz, and at once when the state changes.
- The leader accepts PMVB only from addresses that sent a PMCK request in the last 10 s, **and** whose Show Bus hello carries the same node id.
- Reports never change the air. They only inform the numbers and the verdict.
- The leader adds its own outputs locally.

**`SyncBoard` (pure, Core): the leader's aggregation.**
- **Entries.** Keyed by `(nodeId, outputIdHash)`. Expired after 5 s, after which they read grey "not heard".
- **Reference output R.** The leader's first Display output; otherwise the first reporter.
- **Phase of o against R.** `φ_o = wrap((V_o − V_R) mod P_R)` into (−P_R/2, P_R/2]. `V_o` is extrapolated to a common leader instant along its own period.
- **Uncertainty.** `u_o = clockUncertainty_o (half RTT) + fit σ`. Both are published, so a 210 µs uncertainty is never shown as a 12 µs phase.
- **Relative rate.** `δ_o = (P_o − P_R)/P_R`.
- **Next pair slip.**
  - If δ > 0: `(P/2 − φ)/δ`.
  - If δ < 0: `(φ + P/2)/|δ|`.
  - Null when |δ| < 0.01 ppm or the pair is locked.
- **ppm of o against the leader clock.** `(P_o − P_nominal)/P_nominal`.
- **Next content slip of o.** One output repeating or skipping a content frame against show time: with `θ = (V_o + L_air) mod T_c` (`T_c` = the content frame period), it is `θ/|δ_o,c|` toward the boundary the drift approaches. These slips are inherent to free-running displays [P §8]. Publishing their forecast is the honest part.
- **Mixed rate.** `|P_o/P_R − 1| > 500 ppm` (for example 59.94 against 60 is 1001 ppm) → `mixedRate`, amber, and `SYNC APPLY` refuses.
- **Verdict**, one word plus a sentence:

| Verdict | When |
|---|---|
| `same vblank` | Every member locked and counts agree |
| `free-running` | Worst pair phase, worst uncertainty and the soonest slip are given |
| `mixed rates` | — |
| `partial` | Some members locked |
| `not measured` | — |

**Arithmetic from [P §8] that the tests check.** 50 ppm at 60 Hz slips once every 5 min 33 s, and 59.94 against 60 Hz once every 16.65 s.

### 3.7 The frame code and the sync certificate

**`pattern.synccode`** is a new generator. It is catalogued as a *technical pattern*, like the scan's structured light: its flicker is confined to ≤ 10 % of the raster, and `SYNC MARKS` is High and refused while armed. Each output draws, as a pure function of show time:

- A **band** of 28 cells across the middle 80 % of the raster's width, with cell height = cell width. Each band is a **data row** with its **complement row** directly below. The pair repeats **6 times** down the band, for redundancy and rolling-shutter sampling. Cells left to right:

| Cells | Content |
|---|---|
| 0–2 | start guard `W B W` |
| 3–6 | node slot (4 bits, MSB first; assigned by the leader in `@lock.marks`) |
| 7–10 | output number on its node (4 bits, from the generator context) |
| 11–22 | frame code: **12-bit Gray code** of `n = GeneratorFrame.Frame mod 4096`. `Frame = floor(leaderSeconds × fps)`, already computed per frame [A3], so all nodes in sync show the same `n` |
| 23 | even parity over cells 3–10 |
| 24 | even parity over cells 11–22 |
| 25–27 | stop guard `W W B` |

- A thin white outline around the band, so cameras can auto-expose. The rest of the raster is black.

`AirState` gains `PatternParams` (a `ParamSet`), and the `PATTERN` grammar takes `[<param>=<value>…]`. `SYNC MARKS ON` sends `PATTERN pattern.synccode slot=<n>` on each node through its own lane. The `synccode` shader is the 25th program and is compiled in both dialects [D-present §3.2].

**`SyncCodeReader`** (pure, Core: a gray8 picture to bands):
1. Every 2nd row: local-mean threshold (a 64-px box), run lengths, then a search for the start guard with the cell width estimated from its runs.
2. Sample 28 cell centres by linear interpolation between the start and stop guards. This absorbs the perspective along the band.
3. Find the complement row at +1 cell height. Each bit is `data > complement`, with a margin of 8 levels. This is differential, so it is robust to blend ramps and ambient light.
4. Check the parities, decode the Gray code, and collect `(slot, output, n, row, confidence)`.
5. Cell width must be ≥ 6 camera px.

**`SyncCertifier`** (pure, Core: samples to a certificate):
- For each camera frame, take every decoded band. For each pair of outputs (a, b) seen in the same frame: `Δ = (n_a − n_b)` centred mod 4096.
- Over N frames, report:
  - `sameFrame = P(Δ = 0)`, `plus1 = P(Δ = +1)`, `minus1 = P(Δ = −1)`, and `gross = P(|Δ| ≥ 2)`;
  - the **sub-frame phase** `φ̂ = (plus1 − minus1) × T_c`, with standard error `T_c·√(f(1−f)/N)`. If camera instants are spread uniformly over the content period, the chance that two walls show different frames equals their phase over the period.
- **Uniformity check.** If `plus1` and `minus1` are both 0 while samples spread over fewer than 3 distinct code steps per second, or the camera's rate is within 0.1 % of an integer ratio of the content rate (from frame timestamps), then the camera is locked to the display. The phase is **refused** ("the camera is locked to the display rate — film at 240 fps or 59.94"), and only frame identity is certified.
- **Rolling-shutter refinement (S1).** For a pair whose bands share sensor rows, the transition rows of `n` within one image give each wall's flip instant on the sensor's row clock. The row-time scale is calibrated from the same wall's successive transitions.
- **Certificate verdicts:**

| Verdict | When |
|---|---|
| `locked` | All pairs \|φ̂\| ≤ 100 µs and `plus1 + minus1 < 1 %` |
| `frame-accurate` | `gross = 0`, worst \|φ̂\| < T_c/2 |
| `±1 refresh` | `gross = 0` with frame differences present |
| `out of sync` | `gross > 1 %` |

- **Reported against observed.** The certificate also records the PMVB-predicted phase for each pair and the difference, which validates the reported numbers.

**Sources.**
- **Live:** an `IScanCamera` (a capture device, a stream or the phonescan WP's phone camera), N frames at ≥ 60 fps.
- **Clip:** `SYNC CERTIFY FILE <path>` opens the clip through `IMediaBackend.Open`. A phone slow-motion clip at 240 fps works well. Frame instants come from the clip's PTS.

**Certificate file:** `<project folder>/sync/<id>.json`. It is also journalled, and its summary is in STATE:

```json
{"v":1,"id":"sc_7f3a91c2","at":1760101234000,"group":"show","method":"camera|clip","source":"camera:cm1",
 "frames":1200,"contentFps":60,"cameraFps":239.76,"exposureMs":1.0,
 "outputs":[{"slot":0,"number":1,"node":"1a2b3c4d","output":"op_1","name":"Left","seen":1187}],
 "pairs":[{"a":"op_1","b":"op_2","sameFrame":0.981,"plus1":0.019,"minus1":0.0,"gross":0.0,
   "phaseUs":317,"phaseSeUs":92,"reportedPhaseUs":295,"agreementUs":22}],
 "verdict":"frame-accurate",
 "words":"Every pair showed the same frame in 98 % of 1200 samples; worst pair Left–Right 0.32 ms ± 0.09 ms apart (reported 0.30 ms); never 2 frames apart."}
```

### 3.8 Anchors to followers, the lock intent and the group latency (F1, F7)

**Leader side (`AnchorFeed`, Net).**
- `EngineCore.AnchorPublished(playlistId, json)` already fires on the lane. `NetworkHost` maps playlist → `SyncGroup` and keeps the **latest anchor per group**.
- Subscribers get `ANCHOR <group> <json>` push lines. On subscribe they get every current anchor first.
- The pseudo-group `@lock` carries the group's lock record, re-sent on change and every 60 s:

```json
{"group":"show","seq":12,"apply":true,"swapGroup":1,"barrier":1,"latencyMs":33.0,
 "counter":{"count24":1234567,"leaderNs":123456789000000,"periodPs":16666712000,"effectiveAt24":1234687},
 "marks":{"on":false,"slots":{"1a2b3c4d":0,"9f8e7d6c":1}}}
```

**Grammar.** `SUBSCRIBE STATE|CONTEXT|PROJECT|ANCHORS|OFF`. `ANCHORS` pushes only `ANCHOR` lines, latest per group. It is open like every subscription: anchors are transport facts already in STATE, and no secret rides them. An unknown subscription word is refused, as today.

**Follower side (`AnchorFollower`, Net).**
- On `SYNC FOLLOW`, and on start when the role is follow, it opens a `WireClient` to the leader's wire port from its hello. That is the same client the Patterns link uses, with TLS pinning when the leader requires HTTPS.
- It sends `HELLO <name>` and `SUBSCRIBE ANCHORS`.
- For each `ANCHOR <group> <json>`, the engine (not Net) runs `PLAYLIST ANCHOR <pl> <json>` through `HandleAsync` with the host's internal session, for every local playlist whose `SyncGroup == group`. The follower adopts the anchor through **its own verb**: no remote mutation, and the journal says "adopted the leader's anchor for show".
- `@lock` is applied by `EngineCore.FrameLock` according to `NodeSettings.FrameLock` (`auto` joins, `off` stays free-run measured).
- Reconnects use exponential backoff from 0.5 s to 8 s. STATE `sync.feed` shows `live`, `connecting` or `lost`, with the reason.

**Group latency.**
- Each node's air latency is the largest `Output.LatencyMs` of its outputs [D-present §3.8], and it is reported in PMVB.
- `ProjectSettings.SyncLatencyMs` (−1 = automatic) sets the group's value: automatic is the maximum the leader hears, otherwise the stated value.
- Each node uses `max(local, group)`. Every lens in the group then emits a show instant's light at the same moment.

**Persisted role [CO].** `SYNC LEAD`, `SYNC FOLLOW` and `LINK PATTERNS FOLLOW-BLACKOUT` write `NodeSettings` through `INetworkControl.PersistAsync`. That method is new; `NetworkHost.Persist` already exists privately. On start, `NetworkHost` re-enters the role:
- a lead binds at once;
- a follow waits until the leader is heard on Show Bus, showing "waiting to hear FOH-1 (9f8e7d6c)" in `sync.words`, then follows.

### 3.9 Clock adoption without jumps (F2) [CO]

`NetworkClock` today swaps a new estimator on every sample, and `Follow` steps from local time to leader time. Two changes fix that:

- **Slew.** The clock holds an immutable `ClockState(estimator, residualNs, residualAtLocalNs)`, swapped atomically (one volatile reference, so the render thread still reads it lock-free). The applied offset is `estimatorOffset(t) + residual(t)`, where the residual decays linearly to 0 at **200 ppm**. When a new estimator arrives, the jump `j` at now is computed:
  - **|j| ≤ 1 ms:** the residual absorbs it, so time stays continuous. A 1 ms correction takes 5 s.
  - **|j| > 1 ms, or `Follow`/`Lead`:** a step. The residual becomes 0, and `Stepped(Δ)` is raised with Δ = new − old leader time at now.

  Leader time is strictly monotonic, at a rate of 1 + drift ± 200 ppm.
- **Rebase on the lane.** `EngineCore` handles `Stepped(Δ)` with **one** lane action:
  - every playlist **not** in a sync group, and every group playlist until its first adopted anchor, gets `TimelineAnchor.LeaderNs += Δ`;
  - scheduled `AT` starts are rebased the same way;
  - `AirState.IdentifyUntilLeaderNs` is rebased;
  - the journal says "followed 9f8e7d6c: show clock stepped +3 h 12 min; 2 playlists kept their place".

  Media positions are continuous, and content stays a pure function of (the new) show time.
- **Why these values.** A slewed correction of at most 1 ms moves a content-frame boundary by at most 1 ms, which is 6 % of a 60 Hz frame and below the clock uncertainty most shows already carry. Anything larger is stepped and rebased, so it is exact. 200 ppm is far inside the ±0.5 % audio slaving range (docs/ARCHITECTURE §4), so audio follows without audible pitch change. Both are constants with this rationale in the code, and a deterministic test covers each (doctrine: measured, then governed). The measured cause is carry-over 2.

### 3.10 `LATENCY MEASURE` (F7)

`LATENCY MEASURE <camera> [<output>|ALL]`:
1. For each output in turn, show raw black and white frames alternately through `IScanDisplay.ShowAsync`: 6 changes, 400 ms apart.
2. Stamp each change with the present WP's truthful present time [D-present §3.8].
3. Feed each camera frame's mean brightness, from the region where that output was seen, to `LatencyEstimator` (Core, existing).

The estimate is glass-to-capture, so it includes the camera's own latency `C`:
- `CameraRig.LatencyMs` is a new trailing parameter, default 0 = unknown. When the camera's latency is known it is subtracted.
- When it is unknown, the values are stored **relative to the fastest output measured**. Only differences matter for wall-to-wall sync. The words say "absolute latency includes the camera's; audio alignment may be off by it".

Each result is one `SET <output> latencyMs <v>` edit, which is undoable and journalled.

### 3.11 Threads and budgets

| Where | What | Budget |
|---|---|---|
| Render thread | `QueryFrameCount`, the machine step, content time under lock, the watchdog, present through the override | ≤ 0.1 ms per output per frame (E); 0 allocation (structs, function pointers) |
| Monitor thread (1 Hz) | NVAPI GSync status, topology, capabilities | ≤ 5 ms per poll |
| Net threads | PMVB receive and send, anchor feed | ≤ 64 B × outputs × 1 Hz per follower |
| Lane | Rebase on step, `@lock` application, verbs | One lane action each |
| Worker (off-lane) | `SYNC CERTIFY` decode and analysis | ≤ 4 ms per 1080p camera frame on 4 cores (E) (reader on every 2nd row); 1200 frames ≈ 5 s. Refused while armed on a show node (docs/UX.md §7.2: no heavy compute on an armed node); runs on the desk or an idle node by `FROM <node>` later |

---

## 4. Spine needs

### 4.1 Model (Core), backwards compatible (trailing optional parameters)

```csharp
public sealed record ProjectSettings(string FrameRate, TransitionKind DefaultTransition, long DefaultTransitionNs, bool FlashGuard,
    /// <summary>The sync group's air latency, ms: −1 = the largest any member reports.</summary>
    double SyncLatencyMs = -1);

public sealed record CameraRig(string Id, string Name, CameraSourceKind SourceKind, string Source, Intrinsics Intrinsics,
    LensDistortion Lens, Pose Pose, bool Calibrated,
    /// <summary>The camera's own capture latency, ms (0 = unknown): subtracted from a glass-to-capture measurement.</summary>
    double LatencyMs = 0) : IEntity;
```

The JSON names are `syncLatencyMs` and `latencyMs`. Old files read as the defaults.

`AirState` (engine, not the document) gains `ParamSet PatternParams` (default `ParamSet.Empty`). `RenderHost.PatternProject` passes it to the pattern generator.

### 4.2 `NodeSettings` (trailing optional parameters)

```csharp
string SyncRole = "solo",            // solo | lead | follow — re-entered at start
string SyncGroup = "",
string SyncLeader = "",              // the node id followed
bool FollowPatternsBlackout = true,  // carried over from AirState, persisted
string FrameLock = "auto",           // auto: join the hardware lock when the group's leader applies it; off: free-run, measured
int SwapGroup = 1,
int SwapBarrier = 1,
bool LockExclusiveFullscreen = false // lab option (§3.4)
```

### 4.3 Verbs (`VerbCategory.Network`; the existing `SYNC LEAD`/`FOLLOW`/`STATUS` stay)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `SYNC STATUS` | `SYNC STATUS` | Low | no | The extended `sync` JSON (§4.5) |
| `SYNC APPLY` | `SYNC APPLY [<group>]` | High | yes | Off-lane. First `accepted`: "applying framelock to 3 nodes, 7 outputs". Then within 10 s, applied with per-node and per-output observed states. Refused with reasons on a follower ("the leader applies: SYNC APPLY on FOH-1"), while armed, on mixed rates, with no board on a member, with the board not configured (the exact fix), or with a cable in a switch |
| `SYNC LEAVE` | `SYNC LEAVE [<output>\|ALL]` | High | yes | On the leader with no argument: the whole group leaves (`@lock.apply=false`). Otherwise local outputs leave. The receipt is applied when free-run is observed |
| `SYNC REJOIN` | `SYNC REJOIN [<output>\|ALL]` | High | yes | Refused while armed **unless** the air is blacked out. "nothing to rejoin" when no output fell out |
| `SYNC MARKS` | `SYNC MARKS ON\|OFF` | High | yes | Puts `pattern.synccode` on every Display output of the group, with slots assigned. Refused while armed. On is refused when the flash guard says the code band exceeds 10 % of an output's raster |
| `SYNC CERTIFY` | `SYNC CERTIFY <camera> [<seconds 5-120, default 10>] \| SYNC CERTIFY FILE <path>` | Review | yes (writes a certificate) | Off-lane. The certificate JSON (§3.7). Refused when the marks are not up (live camera), when no band is decoded ("no frame code seen — is the camera looking at the walls?"), or while armed on this node |
| `SYNC REPORT` | `SYNC REPORT [<certificate id>]` | Low | no | That certificate, or the latest. "none yet" is a refusal with words |
| `SYNC LATENCY` | `SYNC LATENCY AUTO\|<ms 0-500>` | Review | yes | Sets `ProjectSettings.SyncLatencyMs`, one edit |
| `LATENCY MEASURE` | `LATENCY MEASURE <camera> [<output>\|ALL]` (exists; implemented now) | High | yes | Off-lane. `[{"output":"op_1","latencyMs":48.2,"spreadMs":2.1,"relative":true}]`, plus the edits |

- `SUBSCRIBE` grammar: `SUBSCRIBE STATE|CONTEXT|PROJECT|ANCHORS|OFF` (catalog syntax updated; `WireWords.Subscribe` updated).
- `PATTERN` grammar: `PATTERN <kind>|OFF [ON <output>|ALL] [<param>=<value>…]`.
- `SYNC APPLY`, `SYNC CERTIFY`, `LATENCY MEASURE` and `SYNC LEAVE`/`REJOIN` (which wait for observation) join the ops-verb set, answered off the lane.
- Per-argument risk [CO] is not needed here: every verb above has one risk.

### 4.4 Seams (C# signatures)

```csharp
// PlayMap.Render.Sync
public sealed record FrameLockBoard(string Kind, int BoardId, string Firmware, bool Synced, bool SignalAvailable,
    bool HouseSync, double HouseSyncHz, double RefreshHz, ImmutableArray<bool> CableIntoSwitch,
    ImmutableArray<string> RjDirection, bool Configured, string Driver);

/// <summary>A framelock tier: board facts (any thread, ≤ 1 Hz) and swap-group control (render thread, on the
/// presenter it acts on). Join never blocks a present; every call answers what it did or why not.</summary>
public interface IFrameLockDriver : IDisposable
{
    string Name { get; }                               // "nvapi" | "igcl" | "glx-nv" | "none"
    FrameLockBoard? Board();                           // null: no board
    int MaxSwapGroups { get; }                         // 0: not supported (GeForce, Mesa)
    bool Join(IOutputPresenter output, uint group, uint barrier, out string words);
    void Leave(IOutputPresenter output);
    bool TryFrameCount(IOutputPresenter output, out uint count24);
}

// PlayMap.Core.Sync
public enum LockState { FreeUnknown, FreeMeasured, Grouped, SoftLocked, Framelocked, HouseLocked, FellOut, Joining }
public sealed class FrameLockMachine
{
    public LockState State { get; }
    public string Words { get; }
    public LockActions Step(in LockFacts facts);      // pure transition; actions for the caller to perform
}
[Flags] public enum LockActions { None = 0, Join = 1, Bind = 2, Unbind = 4, Leave = 8, OverridePresent = 16, RestorePresent = 32, Alarm = 64 }

public readonly record struct LockAnchor(uint Count24, long LeaderNs, long PeriodPs, uint EffectiveAt24, long Seq);
public sealed class LockCounter
{
    public void Read(uint count24);                    // unwrap
    public void Offer(LockAnchor anchor);              // takes effect at EffectiveAt24
    public bool TryLeaderNsAt(uint count24, out long leaderNs);   // false: no anchor, or stale (> 2^23)
}

public readonly record struct VblankReport(LockState State, byte Flags, byte Path, uint NodeId, uint OutputIdHash, uint Seq,
    uint BoardFrameCount, long PeriodPs, long LastVblankLeaderNs, ulong RefreshIndex, uint Missed60s,
    uint HouseSyncMilliHz, int AirLatencyUs)
{
    public const int Size = 64;
    public void Write(Span<byte> b);
    public static bool TryRead(ReadOnlySpan<byte> b, out VblankReport report);
}

public sealed class SyncBoard
{
    public void Offer(in VblankReport r, long receivedMonoNs, double clockUncertaintyNs);
    public SyncPicture Picture(long nowLeaderNs);     // outputs, pairs, verdict, words (immutable)
}

public sealed record SyncCodeBand(int Slot, int Number, int Code, int Row, double Confidence);
public static class SyncCodeReader { public static int Read(ReadOnlySpan<byte> gray, int width, int height, List<SyncCodeBand> into); }
public sealed class SyncCertifier
{
    public void Add(long frameNs, IReadOnlyList<SyncCodeBand> bands);
    public SyncCertificate Finish(double contentFps, SyncPicture? reported);
}

// PlayMap.Net
public interface IVblankSink { void Offer(in VblankReport report, IPAddress from); }   // the responder hands PMVB here

// PlayMap.Engine (Seams.cs, additive)
public sealed record LockView(string State, string Words, string Driver, FrameLockBoardView? Board, long FellOut, long Rejoins);
public sealed record FrameLockBoardView(string Kind, int Id, string Firmware, bool Synced, bool Signal, bool HouseSync,
    double HouseSyncHz, bool CableIntoSwitch, bool Configured);
public interface IFrameLockBackend          // RenderHost implements it
{
    IReadOnlyDictionary<string, LockView> Locks();      // by output id
    void Intent(LockIntent intent);                      // from @lock or a verb
    Task<IReadOnlyList<(string OutputId, string State, string Words)>> ObserveAsync(TimeSpan within, CancellationToken ct);
    LockAnchor? LocalAnchor();                           // the leader's (count, vblank) pair for @lock
}
public interface INetworkControl            // existing; additions
{
    Task PersistAsync(Func<NodeSettings, NodeSettings> edit);
    SyncPicture? SyncPicture();                          // the leader's board; null on a follower or solo
    void PublishLock(string group, string lockJson);     // @lock on the anchor feed
}
```

`SyncView` (existing record) is unchanged. The new rows are written by `StateWriter.WriteSync` from `INetworkControl.SyncPicture()` and `IFrameLockBackend`.

### 4.5 STATE, context and the Eye

`sync` (existing keys unchanged; new keys added):

```json
"sync":{"role":"leader","leader":"9f8e7d6c","offsetMs":0,"rttMs":0,"driftPpm":0,"groups":["show"],
  "group":"show","feed":"live","latencyMs":33.0,"slewing":false,"steps":1,
  "lock":{"state":"framelocked","driver":"nvapi","words":"framelocked: Quadro Sync II (P2061) fw 3.6, barrier 1",
    "board":{"kind":"nvidia","id":8289,"firmware":"3.6","synced":true,"signal":true,"houseSync":false,"houseSyncHz":0,
      "cableIntoSwitch":false,"configured":true},"fellOut":0,"rejoins":0},
  "outputs":[{"node":"9f8e7d6c","output":"op_1","name":"Left","state":"framelocked","path":"dxgi-flip",
    "refreshHz":59.999823,"ppm":-2.95,"phaseUs":0,"uncertaintyUs":0,"nextSlipS":null,"missed60s":0,"frameCount":1234567,"heard":true}],
  "pairs":[{"a":"op_1","b":"op_7","phaseUs":12,"uncertaintyUs":210,"nextSlipS":null,"mixedRate":false}],
  "verdict":"same vblank","words":"3 nodes, 7 outputs on one vblank; last certificate frame-accurate (0.3 ms worst).",
  "certificate":{"id":"sc_7f3a91c2","at":1760101234000,"verdict":"frame-accurate","worstPhaseUs":317,"method":"camera"}}
```

- A follower shows its own outputs only, plus `"leaderVerdict"` from the `@lock` record. The aggregated board is the leader's.

**Faults.** `fell-out` adds a STATE fault `{"part":"sync","words":"Left fell out of the lock at 21:14:03 (barrier stalled 3 frames) — presenting free; SYNC REJOIN when ready"}` and a journal row (`fault`).

**Output light** (present WP's rules plus these):
- red on `fell-out`;
- amber on `joining` beyond 5 s, on `mixedRate`, or on a free-run pair whose next slip is under 10 min during a playing show.

**Context (the Eye) [P §11.1].**
- The leader publishes an item `sync:<group>` (kind `sync`, label "Sync show"), lit by the verdict, with words like "3 nodes · framelocked · worst 12 µs".
- Each node publishes an item `sync:<group>:<nodeId>` with its own state, linked `"member"` to `sync:<group>`.
- Each output item `output:<id>` is linked to `sync:<group>:<nodeId>` with kind `locked` or `free` and the output's light.

Patterns' Eye merges items by id across nodes, so the hub `sync:<group>` appears once. Its links fan out to every node and output, lit by their state.

### 4.6 MCP

The verbs are tools through the catalog. An assistant reads `SYNC STATUS` and `SYNC REPORT` freely. `SYNC APPLY`, `LEAVE`, `REJOIN` and `MARKS` are High and are proposals only. `SYNC CERTIFY` is Review.

---

## 5. Files

### New files

| File | Notes |
|---|---|
| `src/PlayMap.Core/Sync/FrameLockMachine.cs`, `LockCounter.cs`, `VblankReport.cs`, `SyncBoard.cs`, `SyncCode.cs`, `SyncCodeReader.cs`, `SyncCertifier.cs` | — |
| `src/PlayMap.Render/Sync/IFrameLockDriver.cs`, `NoFrameLock.cs` | — |
| `src/PlayMap.Render/Generators/SyncCodeGenerator.cs` | Or a class in `ShaderGenerators.cs` beside `SyncMarksGenerator` |
| `src/PlayMap.Render/Platform/Windows/NvApi.cs`, `NvFrameLock.cs`, `IgclGenlock.cs` (S2) | — |
| `src/PlayMap.Render/Platform/Linux/GlxSwapGroup.cs` (S3) | — |
| `src/PlayMap.Net/Sync/VblankReporter.cs`, `AnchorFeed.cs` | — |
| `src/PlayMap.Net/Links/AnchorFollower.cs` | — |
| `src/PlayMap.Engine/EngineCore.FrameLock.cs` | — |
| `src/PlayMap.Engine/Rendering/RenderHost.Lock.cs` | — |
| `src/PlayMap.Engine/Sync/SyncCertify.cs`, `LatencyMeasure.cs` | — |
| `src/PlayMap.Engine/Sync/sync-known-issues.json` (S4) | Embedded data: board firmware × GPU family × driver → words, with source links [P §6.1] |
| `tests/PlayMap.Core.Tests/FrameLockMachineTests.cs`, `LockCounterTests.cs`, `VblankReportTests.cs`, `SyncBoardTests.cs`, `SyncCodeTests.cs`, `SyncCertifierTests.cs`, `NetworkClockSlewTests.cs` | — |
| `tests/PlayMap.Core.Tests/Sim/SyncCameraSim.cs` | The simulated camera: walls as homographies, blur, noise, gamma, exposure window, rolling shutter, camera rate and jitter. Pure; Pcg32 seeded |
| `tests/PlayMap.Render.Tests/SyncCodeRenderTests.cs` | Render the code on llvmpipe in both dialects; decode from read-back |
| `tests/PlayMap.Net.Tests/AnchorFeedTests.cs`, `VblankSocketTests.cs` | — |
| `tests/PlayMap.Engine.Tests/Host/SyncGroupHostTests.cs` | Two nodes: anchors, persisted role, step+rebase, PMVB over loopback, `SYNC STATUS` aggregation, `SYNC MARKS` + `SYNC CERTIFY FILE` on a clip |
| `tests/PlayMap.Engine.Tests/Rendering/FrameLockHostTests.cs` | `FakeFrameLockDriver` through `RenderHost`: content time under a scripted counter, watchdog, fall-out keeps presenting |
| `tests/PlayMap.Engine.Tests/Rendering/LatencyMeasureTests.cs` | — |

### Existing files touched (small hunks)

| File | Change |
|---|---|
| `src/PlayMap.Core/Time/ClockOffsetEstimator.cs` | `NetworkClock`: `ClockState`, slew, `Stepped` |
| `src/PlayMap.Core/Model/Project.cs` | `ProjectSettings.SyncLatencyMs` |
| `src/PlayMap.Core/Model/StageModel.cs` | `CameraRig.LatencyMs` |
| `src/PlayMap.Core/Bus/Pairing.cs` | `NodeSettings`: eight trailing parameters |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | Seven verbs; `SUBSCRIBE` and `PATTERN` syntax |
| `src/PlayMap.Core/Content/GeneratorCatalog.cs` | `pattern.synccode` |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | `synccode` program; `Names` |
| `src/PlayMap.Render/Generators/GeneratorHost.cs` | One case |
| `src/PlayMap.Net/Sync/ClockSync.cs` | Responder demultiplexes PMVB to `IVblankSink`; the PMCK reader is unchanged |
| `src/PlayMap.Net/Wire/WireSession.cs`, `src/PlayMap.Net/Commands/WireWords.cs` | `SUBSCRIBE ANCHORS`, `PushKind.Anchors` |
| `src/PlayMap.Engine/Host/NetworkHost.cs` | Anchor feed and follower wiring; role persistence and re-entry; `PersistAsync`; `SyncPicture` |
| `src/PlayMap.Engine/EngineCore.Sync.cs` | Rebase on `Stepped`; `PatternParams` |
| `src/PlayMap.Engine/EngineCore.cs` | `AirState.PatternParams` |
| `src/PlayMap.Engine/EngineCore.Router.cs` | Dispatch lines; `PATTERN` params; `FOLLOW-BLACKOUT` persists |
| `src/PlayMap.Engine/EngineCore.Ops.cs` | `OpsVerbs` |
| `src/PlayMap.Engine/StateWriter.cs` | `WriteSync` |
| `src/PlayMap.Engine/ContextBuilder.cs` | `sync:*` items and links |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | Content time under lock; facts; driver lifetime |
| `docs/WIRE.md` (`sync`, `SUBSCRIBE ANCHORS`, `PMVB`), `docs/SHOWBUS-NODE.md` (sync items for the Eye), `docs/ARCHITECTURE.md` §4 (slew and rebase, lock tiers), `docs/ROADMAP.md` (R2.7, R4.2 status), `docs/COMPETITIVE.md` (sync rows) | — |
| `src/PlayMap.App` | The Network drawer's Sync tab (§8); the desk fix that `air.pattern` is an object [CO] lands with the `PatternParams` change |

---

## 6. Dependencies

**Work packages.**
- **present (hard).**
  - The presenter seam; `IFrameLockTarget` for `NvAPI_D3D1x_Present`.
  - `VblankClock`, for the period, phase and `TimeOf(k)` behind PMVB and the counter anchor.
  - The delay ring and `Output.LatencyMs`.
  - Truthful present stamps for `LATENCY MEASURE`.

  The software tier, anchors, slew and rebase, the certificate and the state machine do not wait for present's Windows path: they run on the GL path's samples, marked approximate.
- **showsafety:**
  - the `armed` flag for refusals and the no-auto-rejoin rule;
  - preflight calls the S4 checks;
  - Panic and blackout are the boundaries for automatic rejoin.
- **phonescan:** a phone as a live certify camera (optional); the clip path needs nothing from it.
- **fleet:** its health view consumes `sync.outputs` and `sync.pairs`, and the `sync:*` context items.
- **companion:** variables and feedbacks (§8).
- **codecs:** none.

**Third party.** No new package.

| What | Licence | How |
|---|---|---|
| NVAPI (`nvapi64.dll`) | Interface IDs and struct layouts from the **MIT** NVAPI SDK headers (`nvapi_interface.h`, `nvapi.h`, R615, read 13 Oct 2026 [A1, A2]). No header text is copied, only numbers and layouts, which a test asserts | The NVIDIA driver's DLL, loaded at run time |
| IGCL (`ControlLib.dll`) | Intel's header is under Intel's own licence, so the P/Invoke is written from the published API docs and no header is copied [P §6.4] | Intel driver, loaded at run time (S2) |
| `configureSync.exe` / NVIDIA Control Panel | — | Named in words, never run by the engine |
| FFmpeg 9 | (existing) | Only for the clip path through Media |

---

## 7. Verification here (honest)

### 7.1 Real in this container

- **`FrameLockMachine`.** Scripted facts covering every transition in §3.3, including:
  - armed: no auto-rejoin, and `SYNC REJOIN` allowed only when blacked out;
  - debounce at 3T;
  - a display change;
  - device busy;
  - the cable-in-switch refusal.
- **`LockCounter`.**
  - Unwrap across two simulated wraps at 50, 60 and 120 Hz (4 days).
  - Anchor switch at `effectiveAt24` on 3 simulated nodes: identical `t(c)` for every vblank.
  - Staleness at 2²³.
  - The 32-bit switch when a value ≥ 2²⁴ appears.
- **PMVB codec and socket.**
  - A round trip.
  - Each structural byte mutated is refused.
  - A fuzz of 100k Pcg32 packets accepts none.
  - Loopback UDP: the leader's responder demultiplexes PMVB and PMCK on one socket, and refuses PMVB from an address that never sent PMCK.
- **`SyncBoard`.** Synthetic nodes with ppm offsets, phases and jitter: phase, uncertainty, the pair-slip time against closed form (50 ppm → 333 s at 60 Hz; 59.94/60 → 16.65 s), mixed rate, and expiry.
- **`NetworkClock` slew and step.**
  - A monotonic, bounded rate under 10⁶ random estimator swaps (Pcg32).
  - `Stepped` raised exactly when |j| > 1 ms.
- **Two real nodes in one process** (`SyncGroupHostTests`, the existing `HostRig`):
  - `SYNC LEAD` and `SYNC FOLLOW`;
  - `SUBSCRIBE ANCHORS` adoption within 200 ms;
  - a non-group playlist continuous within 1 ms across the follow step;
  - the role persisted and re-entered after a restart;
  - PMVB from the follower's offscreen outputs reaching the leader's `sync.outputs`, honestly `free-unknown` and `path: "offscreen"`.
- **Frame code.**
  - It renders on llvmpipe in both GL dialects, and `SyncCodeReader` decodes the exact `(slot, number, code)` from `ReadOutputAsync`.
  - `SyncCameraSim` composes two or three outputs' code pictures (from `SyncCode` layout maths, rendered on the CPU) with injected per-output frame offsets and sub-frame phases. The certifier recovers:
    - frame offsets exactly;
    - phase within 3 SE at N = 600;
    - the locked-camera case refused.
- **Clip path.** The test encodes a 4 s, 240 fps, 1080p H.264 clip of simulated camera frames through FFmpeg 9 (libx264 is in the build here). `SYNC CERTIFY FILE` on it gives the same verdict. It runs where the FFmpeg tests run (`PLAYMAP_FFMPEG_DIR`).
- **`LATENCY MEASURE`.** With the engine's simulated camera (`Scanning/ScanSimulation.cs`'s rig, with an injected output delay): within one camera frame; the relative mode and the `CameraRig.LatencyMs` subtraction.
- **Content time under a lock in `RenderHost`** with `FakeFrameLockDriver`: the frames drawn equal `t(c+1) + L_air` exactly, and a fall-out keeps presenting (frames advance, the alarm is raised, the counters move).

### 7.2 Simulated (fakes)

| Fake | Stands in for |
|---|---|
| `FakeFrameLockDriver` | NVAPI, IGCL and GLX tiers: board facts, join and bind results, frame counter, device busy, scripted |
| `SyncCameraSim` | A real camera: geometry, optics, sensor timing |
| Offscreen presenters on Mesa | Display outputs. Their vblank clock comes from the loop pacer, so phase is approximate, and the tests assert that the STATE says so |

### 7.3 Compile-only here; where it is really tested

**Compile-only:** `NvApi.cs` and `NvFrameLock.cs` (P/Invokes and function pointers), `IgclGenlock.cs`, `GlxSwapGroup.cs`, the S5 helper and the S4 probes. A `sizeof` test runs here on the managed NVAPI structs against the header-derived sizes [A2].

**The hardware lab (R0.5)** carries the claims [P §12]:
- **Test 1.** Non-blocking swap groups on windowed flip in iflip and in FSE; behaviour when a node is killed (the watchdog must keep the survivors presenting).
- **Test 4.** `VblankClock` against a photodiode.
- **Test 7.** Firmware 3.04/3.06 × Blackwell × house sync.
- **A certificate** from a real 240 fps phone clip of two walls, compared with a photodiode.

Until then, docs/COMPETITIVE.md keeps "±1 refresh without genlock (unmeasured)" and adds "framelock designed, not measured".

---

## 8. Desk and Companion surface

### Desk (Network drawer → **Sync** tab; reads STATE; uses the verbs)

- **Group card.**
  - The verdict word and sentence, large: "same vblank" or "free-running · worst 6.1 ms · next slip 4:12".
  - The latency line: "air latency 33 ms (Right's projector)".
  - The last certificate's verdict, with a **Certify** button.
- **Per node.** A row with the role, feed state, driver and board block: board `Quadro Sync II (P2061) fw 3.6` (an unknown id reads `sync board 0x…`), `house sync —`, and a warning glyph for "cable in a switch".
- **Per output.** A chip (`LOCK`, `HOUSE`, `GROUP`, `FREE ✓`, `FREE ?`, `FELL OUT`) and a phase bar: µs against the reference, with the uncertainty as a whisker. Free-run outputs show a countdown to the next slip.
- **Buttons:**
  - **Apply** (High: the desk confirms; an AI proposes);
  - **Leave**, **Rejoin**;
  - **Marks**;
  - **Certify** (a camera picker, or **drop a phone clip** onto the tab, which sends `SYNC CERTIFY FILE`);
  - **Measure latency**.
- **Right-click:**
  - an output: Leave lock · Rejoin lock · Measure latency · Show frame code on this output (`PATTERN pattern.synccode ON <output>`);
  - a node: Follow this node · Lead from here · Open its STATE.
- **Fell-out alarm.** A red banner on the always-visible strip, "Left fell out of the lock — presenting free", with **Rejoin** greyed while armed (and why).
- **Certificate history.** A list of the project's certificates, each openable as the words plus a pair table.

### Companion (the companion WP's module)

- **Variables:**
  - `sync_lock` (the local lock state), `sync_verdict`, `sync_worst_phase_us`;
  - `sync_next_slip` ("4:12", or empty);
  - `sync_cert` (the last verdict), `sync_latency_ms`;
  - `out_<n>_lock` per output bank.
- **Feedbacks:**
  - `sync_lock_is {state}` and `sync_fell_out` (boolean: a red flash style);
  - `sync_phase {pair?}` (value, for a gauge).
- **Actions:** `SYNC APPLY`, `SYNC REJOIN`, `SYNC MARKS ON/OFF`, `SYNC CERTIFY <camera>`. Gated by `show.allowed` (showsafety); `REJOIN` is shown live only when allowed.
- **Presets:**
  - a **SYNC** key lit by the verdict, reading "LOCK" or "FREE 6.1ms";
  - a **REJOIN** key that turns red and lights only after a fall-out;
  - a fleet template key per node, with its lock chip (the fleet WP renders the page).

---

## 9. Risks and open questions

| Risk or question | Mitigation |
|---|---|
| D3D11 swap groups may need exclusive fullscreen, not windowed flip (NVAPI is silent) [P §6.2] | `LockExclusiveFullscreen` lab option; lab test 1 first. The software tier carries the show meanwhile |
| `blocking = FALSE` may still hold a present when a barrier member dies | The watchdog's 3T timeout on the *waitable* (not on Present) catches it. Lab test 1 kills a node mid-show |
| Board configuration needs administrator rights [A2] | The engine never configures. Exact words, and an opt-in elevated helper (S5) |
| RTX PRO Sync firmware 3.04/3.06 field reports [P §6.1] | The known-issues table in preflight. Firmware is published in STATE |
| The frame-count width for the D3D API is unverified (WATCHOUT documents 24-bit for the board) | Run-time width detection, and 32-bit arithmetic when observed |
| PMVB is unauthenticated UDP and could be spoofed on the show network | It never changes the air. Accepted only from PMCK-active addresses whose hello id matches. The same trust as PMCK today; HMAC with the pairing token is a later hardening if fleet asks |
| A camera locked to the display biases phase | Detected and refused, with words to film at 240 fps or 59.94 |
| The flicker of the code band (photosensitivity) | ≤ 10 % of the raster; High risk; refused while armed; the words warn "use with no audience". Scan patterns set the precedent |
| Step-and-rebase versus Patterns' clip clock | The Patterns link counts on its own anchors; rebasing applies to PlayMap's playlists only. A test follows a leader while the Patterns link is live |
| Open: does `QueryFrameCount` cost enough to matter at 240 Hz? | Measured per node, with the every-4th-frame fallback |
| Open: IGCL genlock's physical requirements and supported SKUs [P §6.4] | S2 stays compile-only until a lab Intel Arc machine exists |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Anchor feed and follower, `SUBSCRIBE ANCHORS`, persisted role and re-entry, `PersistAsync` | 8 |
| `NetworkClock` slew and step, the lane rebase, tests | 6 |
| PMVB codec, the responder demux, reporter, `SyncBoard`, STATE and context | 10 |
| `FrameLockMachine`, `LockCounter`, the `RenderHost` lock hooks, watchdog, tests | 10 |
| NVAPI layer and `NvFrameLock` (compile-only), struct size tests, known-issues data | 6 |
| Verbs, router, ops-verb wiring, refusals, journal | 6 |
| `pattern.synccode` (catalog, generator, shader), `PatternParams` | 4 |
| `SyncCodeReader`, `SyncCertifier`, `SyncCameraSim`, live and clip runners, certificate files | 14 |
| `LATENCY MEASURE` and group latency | 5 |
| IGCL (S2) and GLX swap group (S3), compile-only | 5 |
| Desk Sync tab and menus; papers | 6 |
| **Total** | **80** |

---

## Appendix A. Facts checked on this machine while designing (13 Oct 2026)

| # | Fact | How |
|---|---|---|
| A1 | NVAPI interface IDs from the MIT header `nvapi_interface.h` (NVIDIA/nvapi `main`; SPDX MIT, © 2019–2026 NVIDIA). See the table after this one | Fetched and grepped into `scratchpad/ext/nvapi-src/` |
| A2 | From `nvapi.h` (same repository):<br>• `MAKE_NVAPI_VERSION(typeName, ver) = sizeof(typeName) \| (ver << 16)`<br>• `NV_GSYNC_STATUS {version, bIsSynced, bIsStereoSynced, bIsSyncSignalAvailable}`<br>• `NV_GSYNC_STATUS_PARAMS_V2 {version, refreshRate, RJ45_IO[2], RJ45_Ethernet[2] /* "Connected to ethernet hub? [ERRONEOUSLY CONNECTED!]" */, houseSyncIncoming, bHouseSync, bInternalSlave:1}`<br>• `NV_GSYNC_CAPABILITIES_V3 {version, boardId, revision /* FPGA major */, capFlags, extendedRevision /* FPGA minor */, bIsMulDivSupported:1, maxMulDivValue}`<br>• `NVAPI_GSYNC_DISPLAY_SYNC_STATE {UNSYNCED 0, SLAVE 1, MASTER 2}`<br>• `NvAPI_D3D1x_JoinSwapGroup(pDevice, pSwapChain, group, blocking)`: "false means the Present operation returns immediately"<br>• `NvAPI_D3D1x_Present` "returns NVAPI_DEVICE_BUSY on DXGI_ERROR_DEVICE_RESET/REMOVED, DXGI_STATUS_OCCLUDED"<br>• **`NvAPI_GSync_SetSyncStateSettings` returns `NVAPI_INVALID_USER_PRIVILEGE`: "The application will require Administrator privileges"**, and displays not listed are un-synchronised | Read in the fetched header |
| A3 | `RenderEngine` already computes `GeneratorFrame.Frame = floor(max(0, seconds) × rate + 1e-6)` from the frame's leader seconds. `SyncMarks` and `pattern.syncmarks` exist (a flash on a 2 s grid plus a phase bar), so the frame code reuses an existing pure function of show time | `src/PlayMap.Render/Engine/RenderEngine.cs`, `src/PlayMap.Core/Content/FlashGuard.cs` |
| A4 | Carry-over facts confirmed in code:<br>• `AnchorPublished` has no subscriber that sends to followers (`EngineCore.Sync.cs`; `NetworkHost` does not subscribe).<br>• `NetworkClock.Follow` swaps the estimator, with no slew.<br>• `NodeSettings` has no sync role.<br>• `SUBSCRIBE` takes only `STATE\|CONTEXT\|PROJECT\|OFF` (`WireSession.cs`, `VerbCatalog.cs`). | Read `main` |

**The NVAPI interface IDs (A1):**

| Group | Function | ID |
|---|---|---|
| Core | `NvAPI_Initialize` | 0x0150e828 |
| | `NvAPI_Unload` | 0xd22bdd7e |
| | `NvAPI_GetErrorMessage` | 0x6c2d048c |
| | `NvAPI_SYS_GetDriverAndBranchVersion` | 0x2926aaad |
| | `NvAPI_EnumPhysicalGPUs` | 0xe5ac921f |
| | `NvAPI_GPU_GetFullName` | 0xceee8e9f |
| | `NvAPI_DISP_GetDisplayIdByDisplayName` | 0xae457190 |
| | `NvAPI_Mosaic_GetCurrentTopo` | 0xec32944e |
| GSync | `NvAPI_GSync_EnumSyncDevices` | 0xd9639601 |
| | `NvAPI_GSync_QueryCapabilities` | 0x44a3f1d1 |
| | `NvAPI_GSync_GetTopology` | 0x4562bc38 |
| | `NvAPI_GSync_SetSyncStateSettings` | 0x60acdfdd |
| | `NvAPI_GSync_GetControlParameters` | 0x16de1c6a |
| | `NvAPI_GSync_SetControlParameters` | 0x8bbff88b |
| | `NvAPI_GSync_AdjustSyncDelay` | 0x2d11ff51 |
| | `NvAPI_GSync_GetSyncStatus` | 0xf1f5b434 |
| | `NvAPI_GSync_GetStatusParameters` | 0x70d404ec |
| D3D1x | `NvAPI_D3D1x_Present` | 0x03b845a1 |
| | `NvAPI_D3D1x_QueryFrameCount` | 0x9152e055 |
| | `NvAPI_D3D1x_ResetFrameCount` | 0xfbbb031a |
| | `NvAPI_D3D1x_QueryMaxSwapGroup` | 0x9bb9d68f |
| | `NvAPI_D3D1x_QuerySwapGroup` | 0x407f67aa |
| | `NvAPI_D3D1x_JoinSwapGroup` | 0x14610cd7 |
| | `NvAPI_D3D1x_BindSwapBarrier` | 0x9de8c729 |
| Later (L1) | `NvAPI_D3D12_QueryPresentBarrierSupport` | 0xa15faef7 |
| | `NvAPI_JoinPresentBarrier` | 0x17f6bf82 |
| | `NvAPI_QueryPresentBarrierFrameStatistics` | 0x61b844a1 |
