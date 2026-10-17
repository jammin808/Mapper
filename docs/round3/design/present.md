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
frame for one predicted display instant. The lock is a strictly better tier behind the same lock machine: NVIDIA's D3D12 present
barrier on RTX PRO with RTX PRO Sync, whose own sync mode and in-sync counts are the only evidence that makes an output "locked".
Everywhere else outputs are free and STATE gives their measured phase.

---

## Part A — presenters (`wp3-present`)

## 1. What carries over

| From | Kept | Changed |
|---|---|---|
| `DxgiOutputWindow` (D3D11, ANGLE) | the popup over the monitor, `FLIP_DISCARD`, the waitable at the output's latency, `GetFrameStatistics` into the clock, the pacing output's bounded wait with the device lock let go, `DwmNudge` on its own thread, `DisplayList`, `QpcTime`, the staged start with words, the fuse | no ANGLE, no pbuffer wrap: `Record` copies the final picture into the back buffer in the frame's list |
| `VblankClock`, `PresentLedger`, `PresentClock`, `LatencyPlan`, `DelayRing` | all of it | `GpuTimer` moves to the RHI's timestamps; `ModeEvidence` gets set |
| `PresentFuse`, `PRESENT PATH` | a start that never reached its first frames comes back as GL with words; `PRESENT RESET` clears the fuse | `D3D12` joins the words |

The D3D11 path stays selectable (`PRESENT PATH DXGI`) this round and is deleted with ANGLE next round.

## 2. The platform and the path

- `D3D12Platform : IGpuPlatform` (`Name` "d3d12", `Api` "dxgi-flip", `Path` `D3D12Flip`), made on the adapter that drives the
  outputs' displays; `Device` is `d3d12.md`'s device; `Monitors()` from `DisplayList` (`EnumDisplayMonitors` and
  `QueryDisplayConfig`, with each mode's exact refresh rational).
- `GpuPlatforms.Create` gains the `d3d12` word: a Direct3D 12 start that stops at a stage (`d3d12.md` §2) comes back as GL with the
  stage's words, as the DXGI path does today.
- **AUTO.** GL on every platform this round. When L1's record exists for a vendor tier, AUTO becomes D3D12 on that tier in one
  commit citing the record; until then `machine.present.words` says "AUTO is OpenGL: Direct3D 12 waits for its parity record on
  this GPU's tier".

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
| `hint` | DXGI says the output is on an overlay (`CompositionMode` `OVERLAY` in the media statistics) and the statistics are continuous: every present shown on the refresh after the waitable signalled, no composition delay | `Observe`, every frame |
| `proven` | a measured run: `PRESENT PROVE` held `hint` for its whole window (default 10 s, 2–60) with no missed vblank, no `DISJOINT`, one frame from present to display at the display's own rate | `PRESENT PROVE`, kept until the mode changes or a window overlaps |

The mode's words are PresentMon's, so the lab's PresentMon run reads beside STATE (L2): "Hardware: Independent Flip", "Hardware
Composed: Independent Flip" (an overlay plane), "Composed: Flip". An output that is composed is amber in preflight with the reason
DXGI's report and the statistics give ("composed: another window overlaps output 2", "composed: the display scales the buffer").
The stretch's ETW session (the show user in Performance Log Users) brings PresentMon's own present-mode reading inside the engine.

## 6. One queued frame and the predicted display time

**Frames in flight per output.** `Output.FramesInFlight`: 0 (auto) is 1 when the output shows a live input, else 2; it sets the
swap chain's latency and enters the output's latency in the latency plan, so the delay rings still make every display light the
same picture together; a node whose outputs all carry live inputs runs at one frame.

**The frame block.**

```csharp
namespace PlayMap.Render.Timing;

/// <summary>One frame's times, from the sample to the display, all on the machine's monotonic clock (ns). A ring of
/// frames-in-flight + 1 blocks, reused; nothing allocates.</summary>
public struct FrameBlock
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
3. Take the newest live frame at or before `LeaderNs` for each live input.
4. Record the frame (the plan's walk) for `LeaderNs`: every pass, generator, transition and effect reads that one instant.
5. Submit; present each output; observe.

This is the pattern NVIDIA Reflex and AMD Anti-Lag 2 implement for games; PlayMap's own version needs neither SDK. Reflex is a
measured option for later (M5), never on an output in a barrier until measured. Stamps 2–5 and the shown stamp feed the ledger
and `LATENCY STEP`'s stage shares.

## 7. HDR10 and 10-bit

| Output's setting | Swap chain | Colour space | The output stage's encode |
|---|---|---|---|
| `ColorBits` 8 | `B8G8R8A8_UNORM` | `RGB_FULL_G22_NONE_P709` | today's |
| `ColorBits` 10 | `R10G10B10A2_UNORM` | `RGB_FULL_G22_NONE_P709` | today's, dithered to 10 bits |
| `Hdr` hdr10 | `R10G10B10A2_UNORM` | `RGB_FULL_G2084_NONE_P2020` | linear light → BT.2020 → PQ, the content's white at `NitTarget` (203 cd/m² by default) |
| `Hdr` scrgb | `R16G16B16A16_FLOAT` | `RGB_FULL_G10_NONE_P709` | linear scRGB, 1.0 = 80 cd/m² |

What the display reports (`IDXGIOutput6::GetDesc1`: bits per colour, colour space, peak luminance) goes into STATE; a setting the
display cannot show is refused with words ("output 2's display reports 8 bits and no HDR").

## 8. Facts, words and the wire

```json
"outputs": [ { "id": "op_1", "present": {
  "path": "d3d12-flip", "mode": "Hardware: Independent Flip", "evidence": "proven", "evidenceWords": "proven 21:04 for 10 s",
  "framesInFlight": 1, "colorSpace": "hdr10", "display": { "bits": 10, "hdr": true, "maxNits": 1000 },
  "refresh": "60000/1001", "presented": 1234567, "missed60s": 0 } } ],
"machine": { "present": { "api": "d3d12", "path": "d3d12-flip", "asked": "d3d12", "kept": "d3d12", "fuse": "", "words": "" } }
```

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

The lock machine (`FrameLockMachine`, eight states, each locked state needing evidence), `LockCounter` (24-bit, wrap-aware, the
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

**The driver's new shape.**

```csharp
namespace PlayMap.Render.Sync;

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

public interface IFrameLockDriver : IDisposable
{
    string Name { get; }
    string Words { get; }
    FrameLockBoard? Board();
    bool CanBarrier { get; }
    bool Join(string outputId, IPresentBarrierTarget target, out string words);
    void Leave(string outputId);
    bool TryStatistics(string outputId, out BarrierStatistics statistics);
    /// <summary>The frame count every locked node shares (24 bits), where the driver gives one (M10).</summary>
    bool TryFrameCount(string outputId, out uint count24);
}
```

**Evidence.** The lock machine moves an output to a locked state only when its statistics say `System` (outputs of one machine
locked together) or `Cluster` (locked across machines through the Sync card), and `PresentInSyncCount` and `FlipInSyncCount` have
advanced one per refresh for 120 refreshes. `Client` or `NotJoined` after a join is "pending": the barrier engages only full
screen, with no borders, no desktop scaling and no taskbar composition — the same conditions as Independent Flip — so the words
name which one fails ("not locked: the barrier is pending — output 2 is composed: a window overlaps it").

## 13. The honest software lock

Without a board or a barrier, every output is free. STATE gives its measured phase against the group's reference from the leader's
board, with its uncertainty and the next slip ("same frame, phase +6.2 ms ± 0.3 ms, next slip in 41 min"), and never the word
"locked": `SoftwareLockWordsTests` scan the whole of STATE for it. The content is still the same frame on every node (frame =
f(leader time at vblank)); only scan-out phase differs, and only hardware moves it.

## 14. The barrier governed

A barrier is all-or-nothing: a node that stalls stops the wall. An output whose `PresentInSyncCount` stops advancing for three
refreshes while it keeps presenting (or whose waitable times out) leaves the barrier and free-runs; its cause goes to the journal
and to `outputs[].lock.cause`; the rest of the wall keeps its barrier. While armed nothing rejoins on its own (round 2's rule);
disarmed, an output rejoins after 10 s at an idle boundary.

## 15. The shared frame count

Locked content time comes from a count every node agrees on. Where the driver answers `NvAPI_D3D1x_QueryFrameCount` with the barrier
joined (M10), `LockCounter` reads it, wrap-aware; otherwise from the leader's anchor at the barrier's in-sync refresh count. Both
sit behind `LockCounter`'s source, so the lock machine does not know which.

## 16. Preconditions as facts

| Fact | Source | Refusal it names |
|---|---|---|
| each output's timing: pixel rate, totals, refresh rational | `QueryDisplayConfig` | "op_2 runs 59.9400 Hz, op_1 60.0000 Hz: 100 ppm apart, more than the ±50 ppm a lock takes" |
| each output's EDID hash | the display's registry entry | "op_1 and op_3 show the same mode from different EDIDs" |
| VRR on the display | NVAPI's display control | "G-SYNC is on for op_2: turn it off before the lock" |
| Independent Flip | §5 | "op_2 is composed" |
| members' rates | PMVB's period | "the member stage-left runs 50 Hz" (the board's `mixed rate`) |

## 17. Stretch

- **Synchronized Mosaic** (the must if M4 says several swap chains do not join): one swap chain over the Mosaic surface, PlayMap's
  outputs as its viewports, one client in the barrier.
- **Warp and blend at scanout** (`NvAPI_GPU_SetScanoutWarping`, `SetScanoutIntensity`) behind PlayMap's shader warp, reported
  applied only after the state is read back (a GeForce driver reportedly answered success and did nothing).

## 18. Facts in STATE

```json
"outputs": [ { "id": "op_1", "lock": {
  "state": "locked", "tier": "present-barrier", "mode": "cluster", "presentInSync": 98012, "flipInSync": 98012,
  "phaseMs": 0.0, "cause": "" } },
  { "id": "op_2", "lock": { "state": "free", "tier": "none", "phaseMs": 6.2, "phaseUncertaintyMs": 0.3,
  "words": "same frame, phase +6.2 ms ± 0.3 ms, next slip in 41 min" } } ]
```

## 19. Tests

| Test | Package | Job | Holds |
|---|---|---|---|
| `D3D12WarpPresenterTests` | present | Windows | the platform on WARP; 120 presents; statistics or the fuse's words; the back buffer reads back as the picture |
| `IndependentFlipEvidenceTests` | present | both | synthetic statistics → hint, composed, proven, not proven with the count; offscreen refused |
| `RefreshMatchTests` | present | both | a mismatched rate refused, both rationals named |
| `FrameBlockTests` | present | both | predicted display instants for one and two frames in flight; every stage given the same leader instant; the ring allocation-free |
| `JustInTimeTests` | present | both | leader time sampled only after the waitable signals |
| `HdrOutputTests` | present | Windows | HDR10 swap chain and colour space; 203 cd/m² → PQ 0.581 ± 0.001 |
| `PresentPathTests` | present | both | AUTO's words; D3D12 on Linux refused |
| `PresentBarrierTierTests` | framelock | both | a scripted fake NVAPI: pending until full screen, client, cluster; locked only on cluster with in-sync counts advancing for 120 refreshes; registration matches the swap chain after a resize |
| `BarrierStallTests` | framelock | both | a stalled output leaves within three refreshes, cause recorded, the others stay; no rejoin armed |
| `SoftwareLockWordsTests` | framelock | both | free outputs carry phase words; "locked" appears in no STATE value of a free output |
| `TimingPreconditionTests` | framelock | both | 80 ppm apart → `SYNC APPLY` refused naming both rates and the rule |
| `NvapiLayoutTests` | framelock | both | the barrier's structures and ids against NVIDIA's header |

## 20. Risks

- **WARP gives no real vblank** and may give no statistics: CI proves bring-up, recording and read-back; the lab proves the mode and
  the timing (L1, L2).
- **The barrier and several swap chains** (M4) — Mosaic is the fallback shape, planned as a stretch that becomes the must.
- **Reflex and the barrier together** are unknown; never combined until measured.
- **HDR's independent flip** on mixed SDR and HDR monitors is undocumented; the evidence says what each output got.
