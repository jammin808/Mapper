# Design: `wp3-stability` and `-2` — prove what the audience saw; rehearse losing the GPU

*How the node will prove the last rung of the doctrine's chain for pixels — observed — with a counter in the picture read back by a
process that shares nothing with the renderer; what heartbeat a supervisor can trust; how latency will be measured with a step;
what happens on a crash; and how a lost device will be rebuilt by the path that started it, rehearsed in CI. Package sections:
`PLAN.md` §3.9 (Part A) and §3.15 (Part B).*

**The decision in one paragraph.** A frozen screen is the most dangerous failure because it looks normal: medical displays carry an
advancing counter and an independent monitor reads the frame actually shown [dir §8]. PlayMap's present ledger already proves a
frame was shown according to the driver; the missing step is observed. Each watched output will carry a small code in a region the
operator chooses (overscan, a masked area, an LED processor's hidden rows); `PlayMap observer`, a process with no GPU device and no
render code, will read it from a capture of the output and give a verdict per frame. The heartbeat a supervisor trusts is the
shown-frame count, never a thread's tick. A lost device is a normal event: the reason and DRED's evidence go to the journal, every
device object is released, and the start-up path runs again — the same steps in the same order — while content, a function of
time, resumes at "now".

---

## Part A — observed (`wp3-stability`)

## 1. The bar

| Today | After |
|---|---|
| The ledger says which refresh showed each present, by the driver's statistics (DXGI) or the swap's return (GL) | plus an independent observer's reading of the pixels that left the output |
| The desk restarts a local engine that exits; a headless node has no supervisor; the engine has no heartbeat of its own [map §11.6] | HEALTH carries each output's shown and observed frame counts; the supervisor (wp3-installer) stops an engine whose counts stop |
| `LATENCY MEASURE` flashes white and times a camera | `LATENCY STEP` measures glass-to-glass with each stage's share |
| An unhandled exception is journalled; no dump is configured [map §3.4] | the journal is flushed with the build, the process ends, WER keeps a full dump (wp3-installer configures it) |

## 2. The watermark

```
one mark, 64 blocks of SIZE × SIZE px (SIZE 8 by default: 512 × 8 px), each block black or white at full code:
  [ 4 sync: W B W B ][ 24 refresh index ][ 16 leader instant, ms, low bits ][ 8 CRC-8 ][ 4 sync: B W B W ][ 8 spare: 0 ]
two marks per output: TOP (the raster's first rows) and BOTTOM (its last rows), at the same x
```

```csharp
namespace PlayMap.Core.Observe;

public readonly record struct WatermarkCode(uint Counter24, ushort LeaderMs16);

public static class AirWatermark
{
    public const int Blocks = 64;
    /// <summary>The 64 block values (0 black, 1 white) of a code.</summary>
    public static void Encode(in WatermarkCode code, Span<byte> blocks);
    /// <summary>A code from 64 block readings, or false (sync blocks missing, CRC wrong).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> blocks, out WatermarkCode code);
}
```

- **Where.** `Output.Watermark` (spine's model field): `null` (off), or the marks' place (`TOP`, `BOTTOM`, `BOTH`), their x and y and
  block size, set by `WATERMARK`. Preflight says, as a fact, that the marks show on the air at that place.
- **When.** Stamped by `WatermarkPass` last in the output stage, after the air's gain, on every frame the output presents —
  held frames included, so the counter runs while the content holds and stops only when the pipeline does. Never drawn while the
  output is black by the show's policy (blackout, a black panic outcome, a fenced node): the observer knows those from STATE.
- **What.** The counter is the refresh index on the leader's time line at the vblank the frame is drawn for — the frame's leader
  instant divided by the output's refresh period, mod 2^24 (wrap-aware) — and the instant is that leader instant in milliseconds
  (16 low bits: enough to tell one frame from the next and to see a wrong instant). Both are functions of show time, as everything on
  the air is: a hot backup drawing the same instant stamps the same mark, so its picture stays the primary's to the byte
  (`BackupRenderTests` gain a case with the watermark on), and no node counts its own frames into the picture. The counter still
  stops when the presents stop, because no new frame carries a new index; a held frame re-presented at a later vblank carries that
  vblank's index, so the counter runs while the content holds.

## 3. The observer

```
PlayMap.exe observer     a process the engine starts and watches, one JSON object a line on standard input and output;
                         no GPU device, no render code: it reads pixels from a capture and decides
```

**Sources** (`OBSERVE <output> FROM <uri>`, machine-local, kept in `NodeSettings.Observe` — this node's settings, written by its
settings writer, never the project's): a capture device in the spine's one capture grammar (`CaptureUri`,
`capture:<provider>/<device>[/<input>]`) — this wave `capture:dshow/<device>` on Windows and `capture:v4l2/<device>` on Linux through
Media's avdevice (a card on an HDMI or SDI loop-through), and once wp3-capture lands every capture provider (`capture:decklink/2`,
`capture:mf/…`), read on the CPU — an OMT or NDI receive of a loop-through converter or an LED processor's monitor output (`omt:…`,
`ndi:…`), and, for tests, an OMT send of the output itself. A stored source means the same before and after wp3-capture merges,
since both parse the one grammar. It fails closed: only `capture:`, `omt:` and `ndi:` are taken, a `capture:` provider the grammar
does not know is refused, and any other scheme, a URI carrying user information (`omt://user:secret@…`) or a query is refused with
words naming what was refused; nothing of a refused URI reaches the journal (`ObserveUriTests`).

**The engine tells it** what it watches and what STATE says, on change only:

```json
{ "watch": { "output": "op_1", "uri": "omt://127.0.0.1:6400/op_1", "place": "both", "x": 0, "size": 8,
             "raster": [3840, 2160], "rateHz": 60, "budgetMs": 34 } }
{ "air": { "op_1": "playing" } }        // playing | held | black
{ "predicted": { "op_1": [ 1234567, 89012345678 ] } }   // counter → predicted display ns, for "late"
```

**It answers** a line per verdict change and a summary each second:

```json
{ "output": "op_1", "verdict": "ok", "counter": 1234567, "leaderMs": 51234, "capturedNs": 89012362000, "lateMs": 0.4 }
{ "output": "op_1", "verdict": "stale", "words": "the counter has stood at 1234570 for 6 frames while STATE says op_1 plays" }
```

and writes the same into a memory-mapped status file (a seqlock'd record per output, as the preview ring's slots are) that the
supervisor reads, so a wedged engine is still seen.

**Verdicts** (Core, pure, on a manual clock in tests):

| Verdict | When | Light |
|---|---|---|
| `ok` | both marks decode, equal, the counter advancing one per refresh | green |
| `held` | the counter runs while STATE says the output holds its picture | green (expected), said |
| `black` | no mark while STATE says the output is black | grey (expected) |
| `stale` | the counter stops for two frames: the pipeline froze, whatever the content does | red |
| `torn` | the top and bottom marks differ in one capture | red |
| `wrong` | the mark's leader instant is off the instant the counter should carry by more than a frame | red |
| `late` | lit later than the frame's predicted display instant by more than the output's budget | amber |
| `lost` | no mark while STATE says the output shows | red |

The capture's own latency (a card's frame) is a setting of the source, measured once against a photodiode (L12) and subtracted
before `late` is judged.

**Supervision.** Started and watched like the optimise worker: restarted with backoff; its own stop said in STATE
(`observe.observer: restarting`).

## 4. The heartbeat

`HealthOutput`'s `ShownFrames` (the ledger's shown-frame count: from display statistics where there are any, from swap returns on
GL) and `ObservedFrames` (the observer's decoded count) are reserved by the spine with −1 meaning unknown, so the supervisor (W1)
reads a field that exists before this package fills it; HEALTH's `gpu` record (also the spine's: the device epoch, the phase —
`starting`, `running`, `rebuilding` — and since when) says when no frames are expected. This package fills both counts. HEALTH,
`/health` and the fleet carry them. The supervisor (wp3-installer) reads them over loopback every 500 ms and, as a second path, the
observer's status file; its rule, inactive while a count is −1, is in `installer.md` §5.

## 5. `LATENCY STEP`

- **The step.** For `STEPS n` (3–60, default 10) at instants half a second apart on the leader's time line, the watermark's region
  (or, `VIA <camera>` in rehearsal, the whole output) switches from its code to white for one frame.
- **The measure.** The frame block (wp3-present) gives each step's stamps — sampled, recorded, submitted, presented, shown (the
  vblank) — and the observer (or the camera) gives the instant the step was seen, on the same machine's clock or through the
  group's clock sync. Glass-to-glass is seen − sampled; each stage's share is the difference of consecutive stamps. Until
  wp3-present merges, only glass-to-glass is reported.
- **The answer.** `op_1: glass-to-glass p50 31.2 ms, p95 33.9 ms (10 steps) — record 0.6, submit 0.3, to vblank 15.1, scan-out and
  display 14.7, capture 0.5`. Refused while armed (it puts a step on the air); refused with neither an observer nor a camera,
  naming both.

## 6. The crash policy

- `AppDomain.UnhandledException` flushes the journal (whose header carries `BuildInfo.Informational`) and writes a "stopping" STATE
  line, then lets the process end; there is no in-process crash reporter, because WER's LocalDumps does not support an application
  that does its own.
- WER (configured by the MSI) keeps a full dump in `%ProgramData%\PlayMap\dumps`, five at most; the supervisor writes a manifest
  beside each new dump (build, commit, the last journal sequence). Full dumps stay on the machine; what leaves it is a triage dump
  (paths and secrets stripped), next round's diagnostics bundle.

## 7. Facts and verbs, Part A

```json
"outputs": [ { "id": "op_1", "observed": { "verdict": "ok", "source": "capture:dshow/Magewell USB Capture HDMI 4K+",
               "counter": 1234567, "ageMs": 16, "lateMs": 0.4, "words": "" } } ],
"observe": { "observer": "running", "restarts": 0, "steps": { "op_1": { "p50Ms": 31.2, "p95Ms": 33.9, "at": 1760600000000 } } }
```

| Row | Risk / class | Does |
|---|---|---|
| `WATERMARK <output>\|ALL ON\|OFF [TOP\|BOTTOM\|BOTH] [AT <x> <y>] [SIZE <px 4-32>]` | Review / Layout | sets `Output.Watermark` (one undo step); armed refused by the class; `ON` with the place and size it has, refused "already"; `OFF` when it is off, refused "the watermark is already off on op_1" |
| `OBSERVE <output> FROM <capture uri> \| OBSERVE <output> OFF` | Review / Setup | starts or stops watching; answers once the first verdict is in (or why none came in 5 s); `FROM` the source it already watches refused "already watching"; `OFF` with nothing observed refused "op_1 is not observed"; a URI outside the grammar refused (§3) |
| `OBSERVE STATUS [<output>]` | Low / Query | the verdicts and their words |
| `LATENCY STEP <output> [VIA <camera>\|OBSERVER] [STEPS <3-60>]` | High / Heavy | §5 |

Preflight's `observe` section: an observed output stale, torn, wrong or lost (red), late (amber), a watermark showing on the air
(amber, with where). INDEX's `observe` section: each observed output's light.

---

## Part B — the rehearsed loss (`wp3-stability-2`)

## 8. Detecting a loss, and its evidence

- **Where it shows.** `Present` or `ResizeBuffers` answering `DXGI_ERROR_DEVICE_REMOVED` or `DEVICE_RESET`; `ExecuteCommandLists`
  failing; a fence's completed value reading `UINT64_MAX`; on GL, `glGetGraphicsResetStatus` where the context has robustness.
  Today the loop journals a fault and stops after 30 faulting frames in a row (`RenderHost.FaultLimit`) [map §3.1]; that stop goes.
- **The incident.** Before anything is released: `GetDeviceRemovedReason`; DRED's auto-breadcrumbs (`GetAutoBreadcrumbsOutput1`:
  each command list's operations with the pass markers of `d3d12.md` §9, so the last pass the GPU completed and the first it did
  not are named); DRED's page-fault output (the faulting address, the allocations alive and recently freed there, by name); an
  Aftermath dump on NVIDIA when its library is present. One journal row:

```
GPU INCIDENT epoch 3 · DXGI_ERROR_DEVICE_HUNG · driver 582.16 · build 0.2.0+17.abc1234 ·
last completed "fx op_2 #3 Wobble Pro" · first outstanding "output op_2" · page fault none · aftermath dumps\…-3.nv-gpudmp
```

## 9. One rebuild path

```csharp
namespace PlayMap.Engine.Rendering;

/// <summary>What a device's start and its rebuild both run, in this order: the rebuild is the start, on a new epoch.</summary>
public enum GpuStartStep { Runtime, Diagnostics, Adapter, Device, Queues, Heaps, Pipelines, Rings, Presenters, Imports, Arena, Plans }

/// <summary>Told before the device's objects are released and after the new ones exist (presenters, decoders' hardware devices,
/// the copy-queue arena, Spout's bridge, the previews' shares, the barrier's registration).</summary>
public interface IGpuLifecycle
{
    void Releasing(long epoch, in GpuIncident incident);
    void Rebuilt(long epoch, IGpuDevice device);
}
```

- The render host releases every device object in the reverse of the start's order, then runs `GpuStartStep` from `Adapter` (the
  runtime and the diagnostics stay armed) on epoch + 1. Decoders reopen on the new device through the decode ladder; previews are
  shared again and the desk re-imports by epoch; the barrier registers its buffers again (wp3-framelock).
- Frames resume at "now": the next frame's leader instant is the time it is drawn for, as at start; delay rings start again, the
  observer sees a short `lost` and then `ok`. Nothing replays and nothing catches up.
- The time from the incident to the first frame presented (and observed, where an observer runs) is measured and kept in STATE.

## 10. Rehearsal

`GPU REHEARSE [REMOVE]` (High, Heavy) forces a removal (`ID3D12Device5::RemoveDevice`) and answers when the first good frame is
back, with the time; refused while armed ("a rehearsed loss blacks the outputs for its length: rehearse disarmed"); on GL without
robustness, refused "the GL context cannot be removed on purpose here". CI forces removals on WARP (M8; a fault seam where WARP does
not honour it) and asserts the facts — the incident row, `render.removals` with its reason, the rebuild's steps in the start's order,
a good frame reached, the presenters back, the frame after the rebuild drawn for its own instant — and records the time to the
first good frame without a ceiling, since the hosted runner is shared. That recorded number is the rebuild budget the supervisor's
exemption reads (`installer.md` §5) and L9 holds a real TDR to it × 4 in the lab.

**During the rebuild** HEALTH's `gpu.phase` is `rebuilding` with its epoch and since when, so the supervisor does not take a
rebuild's stopped frames for a frozen engine; the phase goes back to `running` at the first good frame.

## 11. Repeated loss and quarantine

| Loss within ten minutes | What happens |
|---|---|
| first | rebuild at once |
| second | rebuild after 1 s |
| third | the GL fallback through the fuse, where GL starts; else the outputs held as the show's policy says, with words — never a silent stop |

A loss whose last completed breadcrumb sits inside a user effect's pass quarantines that effect's kind (`shaders.md` §6) before
the rebuild draws again, so the same shader does not take the GPU down twice; the rebuild then draws its slots bypassed.

## 12. The degrade order for a missing frame

1. A late frame: the next frame is drawn for its own instant, never a catch-up (as today: a dropped frame heals itself).
2. A stalled render thread: the presenters show their last picture (no present is queued) and STATE says held, with the stall's
   cause; the shown-frame count stops, so the supervisor's rule applies.
3. An observed `stale` or `lost` output: red on the attention bar and in preflight, with the hot backup's `FAILOVER` as its fix line
   where the node has one. The takeover's automatic trigger stays the backup's next-round item.
4. Black only where the show's policy says (a panic outcome, a fence).

## 13. Facts and verbs, Part B

```json
"render": { "removals": { "count": 1, "last": { "epoch": 3, "reason": "DXGI_ERROR_DEVICE_HUNG", "pass": "fx op_2 #3",
            "recoveredMs": 412, "at": 1760600000000, "quarantined": "Wobble Pro" } } }
```

`GPU REHEARSE` as §10; `GPU` (wp3-d3d12-core's row) adds the removals. Preflight's `gpu` section: removals since the last arm (amber,
with their reasons).

---

## 14. The air hazard file (stretch)

Medical practice classifies software by the harm it can do and traces each hazard to a test that shows the control works; it does
not trust software to be right (IEC 62304 assumes its failure probability is 1) [dir §8]. PlayMap borrows the mechanism, not a
compliance claim: `docs/HAZARDS.md` lists each hazard to the air and the fault-injection test that holds it.

| Hazard to the air | Control | Test |
|---|---|---|
| frozen picture | watermark, observer, shown-frame heartbeat | `ObserverLoopbackTests`, `SupervisorHeartbeatTests` |
| black | blackout only by policy; the rebuild path | `DeviceLossRehearsalTests`, `PanicRenderTests` |
| torn within an output | flip model, Independent Flip proven | `IndependentFlipEvidenceTests`; L2 |
| torn between outputs or machines | the barrier where hardware exists; phase said elsewhere | `PresentBarrierTierTests`, `SoftwareLockWordsTests` |
| wrong cue | min GO, EXPECT, receipts | round 2's `MinGoTests` |
| a visible seam or brightness jump | blend in linear light, the stack's degrade gain | round 2's `StackRenderTests` |
| desync | anchors, the clock's slew, the board's verdict | round 2's `AnchorFeedTests`, `SyncBoardTests` |
| a photosensitive flash | FlashGuard; desk alarms ≤ 2 Hz | `ContentCoreTests` (FlashGuard), `StatusRuleTests` |
| a crash | WER dump, supervisor restart, the recovery record | `CrashPolicyTests`, `SupervisorTests` |

## 15. Tests

| Test | Package | Job | Holds |
|---|---|---|---|
| `WatermarkCodecTests` | stability | both | round trips near the 24-bit wrap; a flipped block fails the CRC; differing marks → torn; the index is the leader instant over the period, the same on two hosts for the same instant |
| `ObserverVerdictTests` | stability | both | scripted captures → every verdict with its words; held against stale; black expected |
| `ObserverLoopbackTests` | stability | both | the observer process on an OMT send of the output; FREEZE → held; an injected presenter stall → stale after two captured frames without an advance (counted in captured frames, never timed), preflight and HEALTH red; the observer killed → restarted |
| `ObserveUriTests` | stability | both | `capture:dshow/…`, `capture:v4l2/…`, `omt:`, `ndi:` taken; an unknown capture provider, another scheme, user information or a query refused with words; nothing of a refused URI journalled |
| `ServerFloodTests` | stability | both | the Engine host rig on a manual-clock pacer: 10 000 line-wire lines, 1 000 MCP calls and 1 000 HTTP STATE reads from test threads during 600 ticks → 600 frames rendered and 600 presents counted, no tick missed, each server's overflow refused with its words and counted; counts only |
| `ObserveVerbTests` | stability | both | `OBSERVE … FROM` its current source, `OBSERVE … OFF` with nothing observed, `WATERMARK … ON` as it is and `OFF` when off: each refused with its reason, nothing changed |
| `WatermarkRenderTests` | stability | both (WARP at merge) | the marks read back from the final raster decode to the frame's counter and instant; black under blackout |
| `LatencyStepTests` | stability | both | ten steps → p50 and p95 (with stage shares after wp3-present); armed refused; no observer and no camera refused |
| `HeartbeatTests` | stability | both | shown frames advance with presents, held outputs included; a stopped presenter's do not |
| `CrashPolicyTests` | stability | both | a child host's unhandled exception → the journal flushed with the build, a non-zero exit, no in-process dump |
| `DeviceLossRehearsalTests` | stability-2 | Windows | a removal mid-show (rehearsal) → the incident with DRED's pass; a good frame reached and its time recorded (no ceiling on the hosted runner); STATE's removal; HEALTH's `gpu.phase` rebuilding, then running; media time continuous |
| `SupervisorRebuildTests` | stability-2 (at the merge with wp3-installer's supervisor) | Windows | a rehearsed removal under the supervisor's rule on a fake service: no stop while HEALTH says rebuilding within its budget × 4; a rebuild past it → the stop, with words |
| `RebuildPathTests` | stability-2 | both | a recording fake: the rebuild runs the start's steps in the start's order |
| `RepeatedLossTests` | stability-2 | both | three in ten minutes → GL with words; GL failing → held by policy |
| `QuarantineOnLossTests` | stability-2 | both | a breadcrumb in slot X → its kind quarantined, X drawn bypassed after the rebuild |
| `RehearseVerbTests`, `DegradeOrderTests` | stability-2 | both | the refusals; the order of §12 |

## 16. Risks

- **A watermark on the air.** It is visible where it is drawn; the operator chooses the place, preflight says it as a fact, and it is
  off by default. An LED processor's hidden rows or a projector's overscan are the intended places.
- **The observer's capture path** adds its own latency and can fail on its own; its failures say so (`lost` with the capture's
  words) and never stop the air.
- **WARP and forced removal** (M8): a fault seam carries CI where WARP does not remove on request; the lab's real TDR is L9.
