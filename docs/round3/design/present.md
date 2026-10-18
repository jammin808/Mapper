# Design: `wp3-present` and `wp3-framelock` — flip presenters, the predicted display time, and the lock

*How each output will reach its display through a Direct3D 12 flip-model swap chain, how the node will know and prove that the
output is in Independent Flip, how one frame's work will be driven by the instant it is predicted to light the display, and how
outputs will lock across machines where RTX PRO Sync exists and say exactly how far apart they are where it does not. Package
sections: `PLAN.md` §3.10 (present) and §3.14 (framelock). The D3D11 path this inherits is the map's §2.7; the clock, ledger and
lock machine it feeds are §2.8, §2.9 and §3.2.*

**The decision in one paragraph.** The experimental D3D11 presenter already follows the documented discipline — a borderless window
covering the display, a waitable `FLIP_DISCARD` swap chain at latency 1, frame statistics as vblank truth [map §2.7]. The Direct3D
12 presenter keeps that discipline without ANGLE, adds the evidence it never set (`IndependentFlip`, `Proven` [map §2.8]), and
moves the render loop to just-in-time: wait on the pacing output's waitable, then sample time and live inputs, then record one
frame for one predicted display instant. The lock is a strictly better tier behind the same lock machine, one per present path on
RTX PRO with RTX PRO Sync: NVIDIA's D3D12 present barrier on Direct3D 12, and the `WGL_NV_swap_group` swap group on OpenGL, which is
AUTO's path on every machine this round. Each output joins as its own client or member, and only its own evidence — the barrier's
sync mode and in-sync counts for its client, or its group's read-back, the board's sync state and the shared frame count — puts it in
a locked state (`framelocked`, `house-locked`, `grouped`). Everywhere else outputs are in a free state and STATE gives their measured
phase.
The seams all of this implements sit in Core (`IGpuPlatform` and `IOutputPresenter` in `Core/Gpu`, the clock, the ledger and the
frame block in `Core/Presentation`, the barrier's target and statistics in `Core/Sync`), so the Direct3D 12 platform, presenters and
barrier tier live in `Render.D3D12` and reference Render not at all.

---

## Part A — presenters (`wp3-present`)

## 1. What carries over

| From | Kept | Changed |
|---|---|---|
| `DxgiOutputWindow` (D3D11, ANGLE) | the popup over the monitor, `FLIP_DISCARD`, the waitable at the output's latency, `GetFrameStatistics` into the clock, the pacing output's bounded wait with the device lock let go, `DwmNudge` on its own thread, `DisplayList`, `QpcTime`, the staged start with words, the fuse | no ANGLE, no pbuffer wrap: `Record` copies the final picture into the back buffer in the frame's list |
| `VblankClock`, `PresentLedger`, `PresentClock`, `LatencyPlan`, `DelayRing` | all of it (the first four in `PlayMap.Core.Presentation` since the spine) | `GpuTimer` moves to the RHI's timestamps; `ModeEvidence` gets set |
| `PresentFuse`, `PRESENT PATH` | a start that never reached its first frames comes back as GL with words; `PRESENT RESET` clears the fuse | `D3D12` joins the words |

The D3D11 path stays selectable (`PRESENT PATH DXGI`) this round and is deleted with ANGLE next round.

## 2. The platform and the path

- `D3D12Platform : IGpuPlatform` (`Render.D3D12/Platform/`; `Name` "d3d12", `Api` "dxgi-flip", `Path` `D3D12Flip`), made on the
  adapter that drives the outputs' displays; `Device` is `d3d12.md`'s device; `Monitors()` from `DisplayList` (`EnumDisplayMonitors`
  and `QueryDisplayConfig`, with each mode's exact refresh rational). wp3-d3d12-core made it offscreen; this package gives it the
  display list and the presenters, routes `PRESENT PATH D3D12` to it, runs the Engine suites that render on it on the Windows job,
  and writes their STATE twins (`*.d3d12.json`).
- **A display on another adapter** (`d3d12.md` §2's rule): presented composed by DXGI's copy across adapters, amber with its reason
  in the output's `present.words` and in preflight; `AdapterRuleTests` (a fake display list of two adapters).
- `GpuPlatforms.Create` gains the `d3d12` word: a Direct3D 12 start that stops at a stage (`d3d12.md` §2) comes back as GL with the
  stage's words, as the DXGI path does today.
- **AUTO.** GL on every platform this round. When L1's record exists for a vendor tier, AUTO becomes D3D12 on that tier in one
  commit citing the record; until then `machine.present.why` (the existing key) says "AUTO is OpenGL: Direct3D 12 waits for its
  parity record on this GPU's tier".

## 3. The flip presenter, step by step

| # | Step | Detail |
|---|---|---|
| 1 | the window | `WS_POPUP` over the monitor's rectangle, no frame, DWM transitions off (`DWMWA_TRANSITIONS_FORCEDISABLED`), `MakeWindowAssociation(NO_ALT_ENTER \| NO_WINDOW_CHANGES)` |
| 2 | the mode | the output's `RefreshHz` must be the display's current mode (from `QueryDisplayConfig`, as a rational); a request for another is refused, naming both ("the output asks 60/1; the display runs 60000/1001"), because a mismatched rate forces DXGI to blit |
| 3 | the swap chain | `CreateSwapChainForHwnd` on the direct queue: the display's size, `B8G8R8A8_UNORM` (or §7's formats), `FLIP_DISCARD`, buffers = frames in flight + 2, `FRAME_LATENCY_WAITABLE_OBJECT`; `SetMaximumFrameLatency(framesInFlight)`; the waitable kept |
| 4 | the first frame | one wait on the waitable before the first frame (the Godot prototype's lesson [round 2's `present.md`]) |
| 5 | `Record` | the final picture copied into the current back buffer (`Copy` when size and format match, else the `present` program's draw), then the back buffer moved to `PRESENT` |
| 6 | `Present` | after the frame's submit: `Present(1, 0)` for the pacing output; `Present(1, DXGI_PRESENT_DO_NOT_WAIT)` for the others, a full queue answering `Held` (never a block, never a tear) |
| 7 | `Observe` | `GetFrameStatistics` and `IDXGISwapChainMedia::GetFrameStatisticsMedia` after every output presented; `DISJOINT` (first call, a mode change) restarts the clock's fit |

## 4. Vblank truth and the ledger

`VblankClock` fits `(SyncRefreshCount, SyncQPCTime)` pairs (QPC to ns as `(qpc / f) * 1e9 + (qpc % f) * 1e9 / f`, as today);
`PresentLedger` matches each present's count to the refresh that showed it. The stamps of §6's frame blocks come from the same
statistics: a block's `ShownNs` is filled when the statistics name its present count.

## 5. Independent Flip: hint and proof

| Evidence | Meaning | Taken from |
|---|---|---|
| `none` | nothing known yet | — |
| `hint` | DXGI says the output is not composed by the desktop compositor (`CompositionMode` `OVERLAY` in the media statistics) and the statistics are continuous: every present shown on the refresh after the waitable signalled, no composition delay | `Observe`, every frame |
| `proven` | a measured run: `PRESENT PROVE` held `hint` for its whole window (default 10 s, 2–60) with no missed vblank, no `DISJOINT`, one frame from present to display at the display's own rate | `PRESENT PROVE`, kept until the mode changes or a window overlaps |

The mode keeps PlayMap's own words, which STATE already publishes (`iflip`, `composed`, `gl`, `offscreen`, `unknown`). DXGI's media
statistics cannot tell independent flip on the primary plane from a hardware overlay plane, so `iflip` means "not composed by the
desktop compositor", and `evidenceWords` names its source ("hint from DXGI's media statistics: overlay, statistics continuous";
"proven by PRESENT PROVE at 21:04 for 10 s"). PresentMon's labels ("Hardware: Independent Flip", "Hardware Composed: Independent
Flip", "Composed: Flip") appear only where PresentMon's own reading is the source: in the lab beside STATE (L2), and in a
`presentMon` key when the stretch's ETW session (the show user in Performance Log Users) supplies it. An output that is composed is
amber in preflight with the reason DXGI's report and the statistics give ("composed: another window overlaps output 2",
"composed: the display scales the buffer").

## 6. One queued frame and the predicted display time

**Frames in flight per output.** `Output.FramesInFlight`: 0 (auto) is 1 when the output shows a live input, else 2; it sets the
swap chain's latency and enters the output's latency in the latency plan, so the delay rings still make every display light the
same picture together; a node whose outputs all carry live inputs runs at one frame. On a node with any two-frame output the plan
would delay a live output back by the frame it saved; an output marked pass-through (`OUTPUT PASSTHROUGH`, wp3-capture) leaves the
delay ring for that reason and says so (`design/capture.md` §5).

**The frame block.**

```csharp
namespace PlayMap.Core.Presentation;

/// <summary>One frame's times, from the sample to the display, all on the machine's monotonic clock (ns). A ring of
/// frames-in-flight + 1 blocks, reused; nothing allocates.</summary>
public record struct FrameBlock
{
    public long Frame;
    public long TargetVblank;       // the pacing output's refresh count the frame is for
    public long PredictedDisplayNs; // that vblank's instant + the node's air latency
    public long LeaderNs;           // leader time at PredictedDisplayNs: the one instant every stage draws for
    public long SampledNs, RecordedNs, SubmittedNs, PresentedNs, ShownNs;
}

public sealed class FrameBlockRing
{
    public FrameBlockRing(int framesInFlight);
    public ref FrameBlock Begin(long frame);
    public ref FrameBlock ByPresentCount(string outputId, uint presentCount);   // filled by Observe
}
```

**The loop, just in time.**

1. Wait on the pacing output's waitable (bounded at three periods; a timeout is counted and said).
2. Sample: now; the vblank the frame will scan out on (`VblankClock` and the frames in flight); `PredictedDisplayNs` = that
   instant + the air latency; `LeaderNs` = the leader's time at it (or the lock anchor's count when locked, as today).
3. Take, for each live input, the newest frame published by the sample instant on the machine's monotonic clock (arrival and show
   time are different clocks, and `LeaderNs` lies a display latency ahead, so the newest is always the one), and stamp its age at
   draw (sample less arrival).
4. Record the frame (the plan's walk) for `LeaderNs`: every pass, generator, transition and effect reads that one instant.
5. Submit; present each output; observe.

This is the pattern NVIDIA Reflex and AMD Anti-Lag 2 implement for games; PlayMap's own version needs neither SDK. Reflex is a
measured option for later (M5), never on an output in a barrier until measured. Stamps 2–5 and the shown stamp feed the ledger
and `LATENCY STEP`'s stage shares. While a pass-through output is on, wp3-capture adds one wait between steps 1 and 2 (its late
sample, `capture.md` §7), and the pass-through is the pacing output unless it is in a lock group, where the group's pacing output
keeps the role: under a hardware lock every member flips on the same refresh, so any member's waitable marks the same instant.

## 7. HDR10 and 10-bit

| Output's setting | Swap chain | Colour space | The output stage's encode |
|---|---|---|---|
| `ColorBits` 8 | `B8G8R8A8_UNORM` | `RGB_FULL_G22_NONE_P709` | today's |
| `ColorBits` 10 | `R10G10B10A2_UNORM` | `RGB_FULL_G22_NONE_P709` | today's, dithered to 10 bits |
| `Hdr` hdr10 | `R10G10B10A2_UNORM` | `RGB_FULL_G2084_NONE_P2020` | linear light → BT.2020 → PQ, the content's white at `NitTarget` (203 cd/m² by default) |
| `Hdr` scrgb | `R16G16B16A16_FLOAT` | `RGB_FULL_G10_NONE_P709` | linear scRGB, 1.0 = 80 cd/m² |

What the display reports (`IDXGIOutput6::GetDesc1`: bits per colour, colour space, peak luminance) goes into STATE; a setting the
display cannot show is refused with words ("output 2's display reports 8 bits and no HDR"), as is a colour space the swap chain
refuses (`CheckColorSpaceSupport`) — the hosted runner's virtual display among them, if M14 says so, in which case `HdrOutputTests`
assert the refusal's words there and the encode's numbers on a pure test, and L13 proves the display.

The PQ encode and the 10-bit dither sit in the output stage, which wp3-d3d12-passes-2 owns in W3: this package adds them by hunks
after it merges (`OutputStage.cs`, the encode's constants; `Shaders/output.hlsl`, the encode). The GL output program is unchanged:
GL presents SDR only.

## 8. Facts, words and the wire

```json
"outputs": [ { "id": "op_1", "present": {
  "path": "d3d12-flip", "mode": "iflip", "evidence": "proven",
  "evidenceWords": "proven by PRESENT PROVE at 21:04 for 10 s", "framesInFlight": 1, "colorSpace": "hdr10",
  "display": { "bits": 10, "hdr": true, "maxNits": 1000 }, "refresh": "60000/1001", "presented": 1234567, "missed60s": 0 } } ],
"machine": { "present": { "api": "d3d12", "path": "d3d12-flip", "asked": "d3d12", "kept": false,
  "fuse": { "state": "clear", "attempt": 0, "at": 0 }, "why": "" } }
```

Every key the output's and the machine's `present` blocks publish today keeps its name and its type (`kept` a boolean, `fuse` an
object, `why` the words, `refreshHz` beside the new `refresh` rational); the new values are `api` "d3d12" and `path` "d3d12-flip",
and the new keys are `evidenceWords`, `framesInFlight`, `colorSpace`, `display` and `refresh` — additive, as R12 asks.

On the wire a D3D12 flip output reports PMVB path 2 (`dxgi-flip`), the presentation model it shares with the D3D11 path, so a group
mixing builds reads every report; path 3 (`dedicated`, the stretch) is read by this round's builds and refused by older ones
(fail closed), and preflight names a group whose members run different builds. Preflight: a composed output (amber, with why); a
refresh mismatch (red, with the mode to set).

## 9. Verbs

| Row | Risk / class | Does |
|---|---|---|
| `PRESENT PATH AUTO\|D3D12\|DXGI\|GL` | High / Setup | where outputs present from the render host's next start; D3D12 on Linux refused "Windows only"; asking the path the node runs refused "already" |
| `PRESENT PROVE [<output>\|ALL] [FOR <seconds 2-60>]` | Low / Perform | measures §5's proof and answers each output's mode and evidence; an offscreen output refused "no display"; a GL window refused "OpenGL presents report no composition mode" |

## 10. Stretch: dedicated displays and timed presents

- **Dedicated displays.** On Enterprise, Pro for Workstations and IoT Enterprise, a display the person removed from the desktop
  (Settings' toggle, or the display's own EDID block — never an EDID overridden in software) is acquired through
  `Windows.Devices.Display.Core` (`DisplayManager.Create(EnforceSourceOwnership)`, `TryAcquireTarget`, a scanout source, presents
  scheduled on it): no cursor, no taskbar, no window can cover it. PMVB path 3. Refused elsewhere with the edition's words.
- **The composition swapchain** (if M3 says it takes D3D12 buffers): presents at a target time for outputs whose content cadence is
  not the display's.

---

## Part B — the lock (`wp3-framelock`)

## 11. What carries over

The lock machine (`FrameLockMachine`, eight states, each locked state needing evidence; its wire words `free-unknown`,
`free-measured`, `grouped`, `soft-locked`, `framelocked`, `house-locked`, `fell-out`, `joining`, and its `reason` key with
`LockReasons`' words), `LockCounter` (24-bit, wrap-aware, the
60 s re-anchor), `SYNC APPLY|LEAVE|REJOIN` with their refusals, the board's facts from `NvFrameLock` (id, firmware, synced, signal,
house sync, a cable into a switch), PMVB and the leader's `SyncBoard` verdicts (`same vblank`, `free-running`, `mixed rate`,
`unknown`) [map §2.9, §3.2]. Today `Join` refuses every output and `TryFrameCount` answers false.

## 12. The present-barrier tier

**The calls** (their interface ids from NVIDIA's MIT-licensed NVAPI headers, pinned by `NvapiLayoutTests` as today's structures
are):

| Call | When |
|---|---|
| `NvAPI_D3D12_QueryPresentBarrierSupport(device)` | at the tier's start; false → the tier says why |
| `NvAPI_D3D12_CreatePresentBarrierClient(device, swapChain)` | per output, after its swap chain exists |
| `NvAPI_D3D12_RegisterPresentBarrierResources(client, fence, backBuffers, n)` | after creation, after every `ResizeBuffers`, after every rebuild — the barrier increments the fence; PlayMap may wait on it, never signal it |
| `NvAPI_JoinPresentBarrier(client)` / `NvAPI_LeavePresentBarrier(client)` | on `SYNC APPLY` / `SYNC LEAVE`, a stall, a rebuild |
| `NvAPI_QueryPresentBarrierFrameStatistics(client)` | every frame after the presents |

**The driver's new shape**, in Core beside the lock machine, so the tier in `Render.D3D12/Sync` implements it without Render. Today's
`IFrameLockDriver` (in `Render/Sync`, its calls taking an `IOutputPresenter`) splits in two: the board's facts, which stay
implemented by today's `NvFrameLock` over NVAPI's sync-board calls (its declarations unchanged in
`Render/Platform/Windows/NvApi*.cs`, usable whatever the backend), and the barrier, implemented by `Render.D3D12/Sync/NvPresentBarrier`
with its own declarations of the barrier's six calls and `NvAPI_D3D1x_QueryFrameCount` (NVAPI's interface ids, each structure
pinned by `NvapiLayoutTests`). The engine's `FrameLockHost` composes the two.

```csharp
namespace PlayMap.Core.Sync;

/// <summary>What a flip presenter offers a barrier tier: its device, swap chain, back buffers and the epoch they were made in
/// (a new epoch after a resize or a rebuild means "register again").</summary>
public interface IPresentBarrierTarget
{
    nint Device { get; }
    nint SwapChain { get; }
    ReadOnlySpan<nint> BackBuffers { get; }
    long BuffersEpoch { get; }
}

public enum BarrierMode { NotJoined, Client, System, Cluster }

public readonly record struct BarrierStatistics(BarrierMode Mode, uint PresentCount, uint PresentInSyncCount,
    uint FlipInSyncCount, uint RefreshCountInSync);

/// <summary>The sync board's facts (any thread, at most once a second): today's NVAPI tier, a fake, or none.</summary>
public interface IFrameLockBoardSource : IDisposable
{
    string Name { get; }
    string Words { get; }
    FrameLockBoard? Board();      // the record moves from Render/Sync to here, unchanged
}

/// <summary>The barrier (render thread, on the output it acts on).</summary>
public interface IFrameLockDriver : IDisposable
{
    string Name { get; }
    string Words { get; }
    bool CanBarrier { get; }
    bool Join(string outputId, IPresentBarrierTarget target, out string words);
    void Leave(string outputId);
    bool TryStatistics(string outputId, out BarrierStatistics statistics);
    /// <summary>The frame count every locked node shares (24 bits), where the driver gives one (M10).</summary>
    bool TryFrameCount(string outputId, out uint count24);
}
```

**Evidence.** The lock machine moves an output to a locked state only when its statistics say `System` (outputs of one machine
locked together: `grouped`) or `Cluster` (locked across machines through the Sync card: `framelocked`, or `house-locked` when the
board is on house sync), and `PresentInSyncCount` and `FlipInSyncCount` have advanced one per refresh for 120 refreshes.
`SoftLocked` — a vendor's software genlock — has no tier that enters it and no STATE has ever published it; framelock removes it, so
`FrameLockMachine.IsLocked` and the engine's `PresentSeams.Locked` count only the three states the barrier's statistics prove, and
`docs/wire/sync.md` notes the retired word. A vendor's software genlock, if one ships, lands as a free state with its phase.

`Client` or `NotJoined` after a join is "pending": the barrier engages only full screen, with no borders, no desktop scaling and no
taskbar composition — the same conditions as Independent Flip — so the words name which one fails ("not locked: the barrier is
pending — output 2 is composed: a window overlaps it").

**A client per output, answered on the node (M4).** Every output's swap chain is its own client, joined at `SYNC APPLY` (class Setup,
refused while armed), and every client's statistics are read every frame. Whether one process may hold several clients on a Sync wall
is M4's question and no lab answers it this round, so each node answers it for itself: a client the driver does not take — refused at
creation or join, or still pending after 120 refreshes while its output is full screen and in Independent Flip — leaves the barrier and
runs free with its phase and the words ("the barrier did not take op_2's swap chain on driver 582.16: one swap chain per machine
there"). A join that stalls the clients already in sync is caught by §14's rule within three refreshes, and since joins happen only
disarmed it never reaches a show. The node keeps the driver's answer in `NodeSettings.Sync` by driver version, so the next `SYNC APPLY`
on that driver joins only the swap chains it took, and a new driver is tried again whole.

## 12a. The OpenGL swap-group tier

AUTO is OpenGL on every machine this round (D2, D15), so a node with RTX PRO Sync on the default path locks through NVIDIA's
`WGL_NV_swap_group`, shipping on Quadro since 2003 and reported to hold a wall where Vulkan's barrier would not take a second swap
chain [`research/vendor_features.md` §1; `docs/round2/research/present.md`]:

| Call | When |
|---|---|
| `wglQueryMaxSwapGroupsNV(hdc, &groups, &barriers)` | at the tier's start on the first output's context; 0 → the tier says why (no extension on this context, no board) |
| `wglJoinSwapGroupNV(hdc, 1)` | per output on `SYNC APPLY`, on the render thread with that output's context current |
| `wglBindSwapBarrierNV(1, 1)` | once the node's board is a member of a framelock group (cluster), so the group's swaps wait on the board |
| `wglQuerySwapGroupNV(hdc, &group, &barrier)` | read back after each join and bind, and once a second |
| `wglQueryFrameCountNV(hdc, &count)` | every frame: the frame count the locked nodes share |
| `wglJoinSwapGroupNV(hdc, 0)` | to leave, on `SYNC LEAVE`, a stall or a context loss |

The entry points come from `wglGetProcAddress` on the output's context; the window's device context comes from the GL presenter,
which offers it as an `ISwapGroupTarget` (`Core/Sync`), framelock's one hunk in the GL backend. Its evidence, per output: `grouped`
when the group reads back joined and the frame count advances one per refresh for 120 refreshes; `framelocked` (`house-locked` on
house sync) when the barrier also reads back bound and the board's sync state for the output's display is synced (today's `NvFrameLock`
facts: synced, signal, the display's sync state). The frame count is `LockCounter`'s source on GL, wrap-aware as on Direct3D 12. A
context without the extension (ANGLE, Mesa, a GeForce driver) has no tier, and the node with a board is amber (§13).

**The DXGI path** (`PRESENT PATH DXGI`, D3D11 through ANGLE) joins nothing this round: NVAPI's `NvAPI_D3D1x_JoinSwapGroup` would join
its swap chains, but the path retires next round with GL's other paths and the Direct3D 12 flip presenter is its successor on the same
flip model (`PLAN.md` §9 decision 21). Its `Join` keeps today's words, and a node with a board on it is amber with the fix line.



## 13. The honest software lock

Without a board, a hardware tier on its present path or a client the driver took, every output is in a free state (`free-measured`,
or `free-unknown` before its clock settles).
STATE gives its measured phase against the group's reference from the leader's board, with its uncertainty and the next slip
("software lock: same frame, phase +6.2 ms ± 0.3 ms, next slip in 41 min"), `lock.tier` "software", and never a locked state —
"software" is a tier, not a state, and the retired `soft-locked` stays retired. `SoftwareLockWordsTests` assert the fields
first — every output without barrier statistics has a free `lock.state` and `IsLocked` false — and then scan the whole of STATE for
a locked state's word (`framelocked`, `house-locked`, `grouped`) in any value of a free output; words such as "not locked: the
barrier is pending" are allowed. The content is still the same frame on every node (frame =
f(leader time at vblank)); only scan-out phase differs, and only hardware moves it. A node with a board whose outputs present on a
path with no hardware tier — DXGI, or a GL context without the swap-group extension — is amber in preflight and in `SYNC APPLY`'s
answer: "op_1 presents through DXGI, which has no hardware lock: `PRESENT PATH D3D12` or `PRESENT PATH GL`".

## 14. The barrier governed

A barrier is all-or-nothing: a node that stalls stops the wall. An output whose `PresentInSyncCount` (on GL, its shared frame count)
stops advancing for three refreshes while it keeps presenting (or whose waitable or swap times out) leaves the barrier or group and
free-runs (`fell-out`); its cause goes to the journal
and to `outputs[].lock.reason` (the existing key, with `LockReasons`' words) and `lock.words`; the rest of the wall keeps its
barrier. While armed nothing rejoins on its own (round 2's rule);
disarmed, an output rejoins after 10 s at an idle boundary.

## 15. The shared frame count

Locked content time comes from a count every node agrees on. Where the driver answers `NvAPI_D3D1x_QueryFrameCount` with the barrier
joined (M10), `LockCounter` reads it, wrap-aware; on GL it reads `wglQueryFrameCountNV`; otherwise from the leader's anchor at the
barrier's in-sync refresh count. Both
sit behind `LockCounter`'s source, so the lock machine does not know which.

## 16. Preconditions as facts

| Fact | Source | Refusal it names |
|---|---|---|
| each output's timing: pixel rate, totals, refresh rational | `QueryDisplayConfig` | "op_2 runs 59.9400 Hz, op_1 60.0000 Hz: 100 ppm apart, more than the ±50 ppm a lock takes" |
| each output's EDID hash | the display's registry entry | "op_1 and op_3 show the same mode from different EDIDs" |
| VRR on the display | NVAPI's display control on NVIDIA; ADLX's FreeSync state on AMD (read only, loaded from the driver's own `amdadlx64.dll`, never bundled; `src/PlayMap.Platform.Windows/Vendors/`); unknown on Intel, said | "G-SYNC is on for op_2: turn it off before the lock"; "FreeSync is on for op_3"; "VRR state unknown on this Intel GPU: check the driver's panel" (amber) |
| each output's adapter | `d3d12.md` §2's rule | "op_5 is on the second GPU: presented composed, it cannot join the lock" |
| members' backends and GPU drivers | each member's `machine.present.api` and its identity's driver | amber in preflight and words in `SYNC APPLY`'s answer when members draw on different backends or drivers while a generator or effect is drawn on two members' outputs: "stage-left draws on Direct3D 12, stage-right on OpenGL: generated content may differ by a float's rounding across the seam" |
| Independent Flip | §5 | "op_2 is composed" |
| members' rates | PMVB's period | "the member stage-left runs 50 Hz" (the board's `mixed rate`) |

## 17. Stretch

- **Synchronized Mosaic**: one swap chain over the Mosaic surface, PlayMap's outputs as its viewports, one client in the barrier.
  It stays a stretch whatever M4 answers, because it changes the presenter model; a driver that takes one client per machine (§12)
  is where it would matter.
- **Warp and blend at scanout** (`NvAPI_GPU_SetScanoutWarping`, `SetScanoutIntensity`) behind PlayMap's shader warp, reported
  applied only after the state is read back (a GeForce driver reportedly answered success and did nothing).

## 18. Facts in STATE

```json
"outputs": [ { "id": "op_1", "lock": {
  "state": "framelocked", "words": "framelocked: cluster, 98012 presents in sync", "reason": "", "fellOut": 0, "rejoins": 0,
  "tier": "present-barrier", "mode": "cluster", "presentInSync": 98012, "flipInSync": 98012, "phaseMs": 0.0 } },
  { "id": "op_2", "lock": { "state": "free-measured", "words": "software lock: same frame, phase +6.2 ms ± 0.3 ms, next slip in 41 min",
  "reason": "", "fellOut": 0, "rejoins": 0, "tier": "software", "phaseMs": 6.2, "phaseUncertaintyMs": 0.3 } } ]
```

The published keys and words stay (`state`, `words`, `reason`, `fellOut`, `rejoins`); `tier` (`present-barrier`, `swap-group` or
`software`), `mode`, `presentInSync`, `flipInSync` (on GL `frameCount` in their place), `phaseMs` and `phaseUncertaintyMs` are new
keys.

## 19. Tests

| Test | Package | Job | Holds |
|---|---|---|---|
| `D3D12WarpPresenterTests` | present | Windows | the platform on WARP; 120 presents; statistics or the fuse's words; the back buffer reads back as the picture |
| `IndependentFlipEvidenceTests` | present | both | synthetic statistics → hint, composed, proven, not proven with the count; offscreen refused |
| `RefreshMatchTests` | present | both | a mismatched rate refused, both rationals named |
| `FrameBlockTests` | present | both | predicted display instants for one and two frames in flight; every stage given the same leader instant; the ring allocation-free |
| `JustInTimeTests` | present | both | leader time sampled only after the waitable signals |
| `HdrOutputTests` | present | Windows | HDR10 swap chain and colour space; 203 cd/m² → PQ 0.581 ± 0.001 |
| `PresentPathTests` | present | both | AUTO's words in `machine.present.why`; D3D12 on Linux refused |
| `AdapterRuleTests` | present | both | a fake display list of two adapters: the second adapter's output presented composed, amber with its words |
| `D3D12EngineHostTests` | present | Windows | the Engine suites that render, on the D3D12 platform; their `*.d3d12.json` twins written and compared |
| `PresentBarrierTierTests` | framelock | both | a scripted fake NVAPI: pending until full screen, client, cluster; locked only on cluster with in-sync counts advancing for 120 refreshes; registration matches the swap chain after a resize |
| `BarrierStallTests` | framelock | both | a stalled output leaves within three refreshes (`fell-out`), its reason in `lock.reason`, the others stay; no rejoin armed |
| `SoftwareLockWordsTests` | framelock | both | every output without barrier statistics: `lock.state` a free state and `IsLocked` false (the fields first); then no locked state's word in any STATE value of a free output; free outputs carry phase words |
| `TimingPreconditionTests` | framelock | both | 80 ppm apart → `SYNC APPLY` refused naming both rates and the rule; an output on a second adapter refused naming it; members on different backends with a shared generator → amber and the words |
| `AdlxProbeTests` | framelock | both | a fake ADLX: FreeSync on → the VRR row with its words; no ADLX → "unknown", said |
| `ClientPerOutputTests` | framelock | both | a three-output node: a driver that takes every client → three locked, each by its own statistics; one that takes one → the other two leave within 120 refreshes, free with their phase and the words, the answer kept by driver version, the next `SYNC APPLY` joining only the one; a new driver version → all three tried again |
| `SwapGroupTierTests` | framelock | both | a fake WGL extension table: none → the tier's words and the amber row; a join read back → `grouped` after 120 refreshes of the frame count advancing; the barrier bound on a synced fake board → `framelocked`; a count that stops → `fell-out` within three refreshes; a board on DXGI → amber with the fix line |
| `NvapiLayoutTests` | framelock | both | the barrier's structures and ids against NVIDIA's header |

## 20. Risks

- **WARP gives no real vblank** and may give no statistics: CI proves bring-up, recording and read-back; the lab proves the mode and
  the timing (L1, L2).
- **The barrier and several swap chains** (M4) — each node answers it by each client's own statistics, keeps the driver's answer, and
  joins only disarmed; Mosaic stays a stretch.
- **The GL swap group on a borderless window** may not engage on a real Sync wall: locked only by its read-back, the board's state and
  the shared count, otherwise free with its words; L5 runs both paths.
- **Reflex and the barrier together** are unknown; never combined until measured.
- **HDR's independent flip** on mixed SDR and HDR monitors is undocumented; the evidence says what each output got.
