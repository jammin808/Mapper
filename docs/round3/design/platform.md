# Design: `wp3-platform` — what a show node claims from Windows, and what it is

*The `PlayMap.Platform.Windows` seam: the process and thread claims a real-time engine must make because Windows 11's defaults now
work against it, the high-resolution timer, keep-awake, the node's identity and capabilities as one record in STATE, and the
machine's preflight. Package section: `PLAN.md` §3.3. None of this exists in the tree today [map §4: "Absent"]; the render,
control, LED and audio threads run at `ThreadPriority.AboveNormal` and nothing else [map §1.8].*

**The decision in one paragraph.** Windows 11 lowers the quality of service of a process whose windows are hidden, covered or
minimised, and stops honouring its timer requests; an engine whose outputs sit behind the desk, or that has no window at all, is
that process. The engine will claim what it needs once at start, by role for each of its threads, through one small seam built on
`LibraryImport` — and every claim will answer what it asked, what Windows said, and what reading it back shows. The node's identity
(build, OS edition and build, CPU, every GPU by LUID with its driver, the vendor tier, the runtimes it loaded) is one record in
STATE that preflight compares with the set locked for the show. Nothing on the machine is changed silently: what the engine cannot
claim as a standard user is reported as a fact with words.

---

## 1. The seam

```csharp
namespace PlayMap.Core.Machine;

public enum ThreadRole { Render, Present, Decode, Audio, Timecode, Control, Led, Network, Background }

/// <summary>One claim: what was asked, whether Windows applied it, what reading it back shows, and why not.</summary>
public readonly record struct Claim(string What, string Asked, bool Applied, string ReadBack, string Words);

public interface IMachinePlatform : IDisposable
{
    /// <summary>"windows" or "none" (Linux, and any build without the seam).</summary>
    string Name { get; }
    string Words { get; }

    ImmutableArray<Claim> ClaimProcess(MachineSettings settings);

    /// <summary>Claims for the calling thread by role; disposing reverts them (MMCSS handles, throttling state).</summary>
    IThreadClaim ClaimThread(ThreadRole role);

    /// <summary>A deadline timer at the best resolution the platform has; waits are on the machine's monotonic clock.</summary>
    IDeadlineTimer CreateTimer();

    /// <summary>Keeps the display and the system awake while held.</summary>
    IDisposable KeepAwake();

    NodeIdentity Identity();
    CapabilitySnapshot Capabilities();
    ImmutableArray<MachineFact> Facts();
}

public interface IThreadClaim : IDisposable
{
    ImmutableArray<Claim> Claims { get; }
}

public interface IDeadlineTimer : IDisposable
{
    /// <summary>Waits until the instant (ns, the machine's monotonic clock) or the cancel handle; answers false on cancel.</summary>
    bool WaitUntil(long monoNs, WaitHandle? cancel = null);
    /// <summary>How late the last waits woke, p99 over the last 1 000 (µs).</summary>
    double LateP99Us { get; }
}
```

The engine composes `WindowsMachinePlatform` on Windows and `NullMachinePlatform` elsewhere (its words: "the platform seam is
Windows only"); both live in `PlayMap.Platform.Windows` and Core respectively, and the engine reaches them through
`Seams.Round3.IMachinePlatform`.

Threads started in Media, Net and the engine claim their role through one static seam in Core, because Media and Net cannot see
the engine's platform:

```csharp
namespace PlayMap.Core.Machine;

/// <summary>Where a thread claims its role. The engine points it at its platform once at start; until then, and on a node
/// without the seam, a claim is a no-op that says so. One line at each thread's start; the claim is disposed at its end.</summary>
public static class ThreadClaims
{
    public static IThreadClaim Claim(ThreadRole role);
    public static void Use(IMachinePlatform platform);   // the engine's, at start
}
```

The spine lands `ThreadRole`, `IThreadClaim` and `ThreadClaims` (Core/Machine) with the other shared records.

## 2. The claims

| Claim | Call | Read back | Default | The cause of the default | `MACHINE SET` word | Refused when |
|---|---|---|---|---|---|---|
| Process priority | `SetPriorityClass(HIGH_PRIORITY_CLASS)`; realtime never | `GetPriorityClass` | on | Microsoft's documented pattern for this class: a process whose threads are mostly blocked and run briefly for time-critical events (scheduling priorities) — the engine's deadline threads wait on the waitable and the timer | `PROCESS HIGH\|NORMAL` | — |
| HighQoS, every timer honoured | `SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED \| IGNORE_TIMER_RESOLUTION` in the control mask and a zero state mask | `GetProcessInformation` | on | documented: Windows 11 lowers the QoS of a process whose windows are occluded, minimised or absent and stops honouring its timer requests | `QOS HIGH\|AUTO` | — |
| GPU scheduling class | `D3DKMTSetProcessSchedulingPriorityClass(HIGH)` | `D3DKMTGetProcessSchedulingPriorityClass` | **NORMAL** until L10 | none documented: off until measured | `GPU-PRIORITY NORMAL\|HIGH\|REALTIME` | REALTIME without `SeIncreaseBasePriorityPrivilege` (the installer grants it to the show account); REALTIME also asks the direct queue for `GLOBAL_REALTIME` at the next device start, whose creation fails rather than downgrades |
| MMCSS for render and present | `AvSetMmThreadCharacteristicsW("Games")`, `AvSetMmThreadPriority(AVRT_PRIORITY_HIGH)`; reverted with `AvRevertMmThreadCharacteristics` | the handle, the task's registry values (read, never written) | on | documented: MMCSS is Windows' service for time-critical multimedia threads, which it places in the Media and Deadline QoS levels | `MMCSS ON\|OFF` | the MMCSS service stopped |
| MMCSS for audio and timecode | `"Pro Audio"` (priority 23–26) | as above | on | as above, the task "designed for Pro Audio" | as above | as above |
| Decode | above-normal priority, on the critical CPU set | `GetThreadPriority` | on | the critical pool (item 5) | — | — |
| Background pools | EcoQoS: `SetThreadInformation(ThreadPowerThrottling)` with `EXECUTION_SPEED` in both masks; below-normal priority | `GetThreadInformation` | on | documented: EcoQoS is the explicit level for background work, which Windows may confine to a subset of processors on a hybrid CPU | — | — |
| CPU sets | `SetThreadSelectedCpuSets`: the critical pool (render, present, decode) on the highest `EfficiencyClass`, background pools on the rest; never a hard affinity | `GetThreadSelectedCpuSets` | AUTO | documented: Intel and Microsoft advise CPU sets, never affinity, on hybrid processors; a single efficiency class leaves it a no-op | `CPUSETS AUTO\|OFF` | one efficiency class only (nothing to separate), said |
| Keep awake | `SetThreadExecutionState(ES_CONTINUOUS \| ES_DISPLAY_REQUIRED \| ES_SYSTEM_REQUIRED)` while an output is open | the previous state it returns | on | documented: the display and the system otherwise sleep on their timeouts | `AWAKE ON\|OFF` | — |
| GC while armed | `GCSettings.LatencyMode = SustainedLowLatency` on `SHOW ARM`, back on `SHOW DISARM` (through `IShowArmHook`) | `GCSettings.LatencyMode` | **DEFAULT** until L10 | Microsoft names rendering animations as its case, but it grows an uncompacted heap; the research itself asks for a pause histogram first | `GC SHOW\|DEFAULT` | the runtime refuses (server GC), said |

A default is on only where its cause is documented, as the table says; the two without one (the GPU class and the GC mode) are
built, settable and off. L10 measures every claim's effect on p99.9 frame time on each vendor's node, and a default changes — on or
off — in one commit citing that record. A setting already at its value is refused with words ("QoS is already high"), as the
doctrine asks.

## 3. Threads

| Thread | Role | Started in |
|---|---|---|
| `render` | Render | `RenderHost.TryCreate` (today `ThreadPriority.AboveNormal`, line 209) |
| the presenter's helper (`DwmNudge`) | Present | `Platform/Windows` |
| the control lane | Control | `EngineCore` |
| LED sender | Led | the LED part |
| audio capture and `AudioOut`'s feeder | Audio | Media |
| timecode readers | Timecode | the control part |
| decode workers, HAP's Snappy pool | Decode | Media |
| the plan thread (`graph.md`), thumbnails, media scans, survey import | Background | their owners |

Each calls `ClaimThread(role)` once at its start and disposes the claim at its end (one line, the hunks of `PLAN.md` §4). The thread
pool is not claimed. The optimise worker and the shader worker are processes that lower themselves already.

## 4. Time

- Every deadline wait on a claimed thread goes through `IDeadlineTimer`: `CreateWaitableTimerExW(…, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
  TIMER_ALL_ACCESS)`, the due time computed from the monotonic clock, `WaitForMultipleObjects` with the cancel handle. The
  headless pacer is the first user: today it waits with `AutoResetEvent.WaitOne` at the default resolution [map §3.1]. Measured
  first: `PlatformCallsTests` time 1 000 waits of 1 ms with today's wait and with the high-resolution timer on the runner and record
  both lateness distributions; the pacer switches in the same package, its commit citing the two numbers, and L10 confirms them
  on show hardware.
- Stamps stay QPC (`Stopwatch`, `MonoClock`); wall-clock stamps that cross machines use `GetSystemTimePreciseAsFileTime`, as the
  clock's sync already does.
- The timer's lateness (p99 of the last 1 000 waits) is in STATE; a p99 over 1 ms is amber in preflight.
- A thread's own time (`ThreadTime`) on Windows is its kernel and user time in clock ticks (`GetThreadTimes`, 15.625 ms by
  default), which bounds a span's work only for a span the thread ran in one stretch with no tick charged to an interrupt or a DPC;
  its cycles are not converted at a rate measured against the stopwatch (Windows leaves interrupt time out of them, and the rate
  moves with the machine's load). L22 measures a precise read: a thread's cycles at the time-stamp counter's frequency from the
  platform against its running time, with Memory Integrity on and off, and the ticks' step under the claims of §2 (the timer
  resolution honoured, the high-resolution timer). The read changes only with that record.

## 5. The node's identity and capabilities

```csharp
namespace PlayMap.Core.Machine;

public sealed record NodeIdentity(
    BuildIdentity Build,                         // BuildInfo: SemVer, file version, MSI version, commit
    OsIdentity Os,                               // edition, version, build, revision, LTSC, IoT
    CpuIdentity Cpu,                             // model, logical processors by efficiency class, vector widths accelerated
    ImmutableArray<AdapterIdentity> Adapters,
    RuntimeIdentity Runtimes);                   // the Agility runtime loaded, FFmpeg's build, the sync board's firmware

public sealed record OsIdentity(string Edition, string Version, int Build, int Revision, bool Ltsc, bool Iot);
public sealed record CpuIdentity(string Model, int Logical, ImmutableArray<int> ByEfficiencyClass, int VectorBits, bool Avx512,
    bool Avx10);
public sealed record AdapterIdentity(long Luid, int VendorId, int DeviceId, string Name, string Driver, string Branch,
    VendorTier Tier, SupportLevel Support, bool DrivesOutputs, long DedicatedMb);
public enum VendorTier { NvidiaRtxPro, NvidiaRtx, Radeon, IntelArc, Other, Software }
public enum SupportLevel { Tier1, ValidatedPerDriver, BestEffort, Unsupported }
public sealed record RuntimeIdentity(string Agility, string Ffmpeg, string SyncFirmware);

/// <summary>Every probe's answer: supported, why not, and the driver it was asked of. Immutable; a new snapshot after a device
/// rebuild or a driver change.</summary>
public sealed record CapabilitySnapshot(long AtUnixMs, ImmutableArray<CapabilityProbe> Probes);
public readonly record struct CapabilityProbe(string Feature, long AdapterLuid, bool Supported, string Words, string Driver);
```

| Field | Read from |
|---|---|
| OS | `RtlGetVersion`; `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` (`EditionID` — `IoTEnterpriseS` is IoT Enterprise LTSC, `EnterpriseS` Enterprise LTSC — `DisplayVersion`, `UBR`) |
| CPU | the processor's name string; `GetSystemCpuSetInformation` (efficiency classes); `Vector512.IsHardwareAccelerated`, `Avx512F.IsSupported`, `Avx10v1.IsSupported` |
| GPUs | DXGI 1.6 `EnumAdapterByGpuPreference` (LUID, vendor and device ids, name, memory); the driver's user-mode version (`CheckInterfaceSupport`); on NVIDIA `NvAPI_SYS_GetDriverAndBranchVersion` and `NvAPI_GPU_QueryWorkstationFeatureSupport(PROVIZ)` (RTX PRO). These are read-only probes with their own minimal declarations in `PlayMap.Platform.Windows/Interop/` — `CreateDXGIFactory2` and the three vtable slots used (`IDXGIFactory6::EnumAdapterByGpuPreference`, `IDXGIAdapter1::GetDesc1`, `CheckInterfaceSupport`), NVAPI's `nvapi_QueryInterface` and the two calls' ids — each structure pinned by a layout golden (`PlatformLayoutTests`), so the platform project references Core alone and never TerraFX or Render |
| Vendor tier | the policy published in `docs/INSTALL.md` and carried in `machine.identity.adapters[].tier` with `support`: NVIDIA RTX and RTX PRO tier 1 (validated); AMD RDNA 3 and 4 tier 2 (validated per driver: a driver outside the validated list is amber); Intel Arc best effort (said); anything else unsupported, said |
| HAGS | `D3DKMTQueryAdapterInfo` (the WDDM 2.7 caps' scheduling flag) — recorded, never forced |
| Runtimes | the Agility runtime from wp3-d3d12-core, FFmpeg's version and configuration from Media, the board's firmware from the frame-lock tier, each filled by its owner |

The identity is read at start, after a device change and after a rebuild; the capability snapshot by each owner's probes (the
platform's own: HAGS, ReBAR's GPU upload heaps through the device, timer resolution honoured). Vendor libraries bind to adapters by
LUID, never by ordinal. Within one boot a LUID names an adapter; across boots it does not, so `MACHINE LOCK` keys adapters by vendor
id, device id and name.

## 6. Machine preflight

Each row is a fact with its light and its words; a fix line is given only where a verb fixes it, otherwise the words say what a
person does (and wp3-installer's stretch adds the show-machine policies that write some of them once, at install). The rows marked
*(stretch)* follow the must's rows, traded for the CPU sets above the cut line.

| Row | Light | Words / fix |
|---|---|---|
| OS build below 26100 | red | "Windows 11 24H2 or later is required (this is build 22631)" |
| OS edition | grey (fact) | "Windows 11 IoT Enterprise LTSC 2024 · build 26100.4061" |
| Power plan not High or Ultimate Performance | amber | the plan's name and how to set it |
| USB selective suspend on; PCIe link-state power management on *(stretch)* | amber | the setting's name |
| Display or sleep timeout set | amber | "the display sleeps after 10 min on mains" — fix `MACHINE SET AWAKE ON` when it is off |
| Screen saver on | amber | "keeping the display awake does not stop the screen saver" |
| Notifications on *(stretch)* | amber | the setting |
| A reboot pending | amber | from `RebootRequired`, `RebootPending`, `PendingFileRenameOperations` |
| Windows Update not deferred *(stretch)* | amber | the policy values read |
| HAGS *(stretch)* | grey (fact) | on or off; never changed |
| A claim of §2 refused | amber | the claim's words |
| The timer's p99 lateness over 1 ms | amber | the measured value |
| Drift from `MACHINE LOCK`'s set | amber | "GPU driver 582.16 → 591.44 since the driver set was locked" — fix `MACHINE LOCK` |
| Auto-logon | grey on a desk machine, amber on an installed node without it | from `Winlogon`'s `AutoAdminLogon` and `DefaultUserName` (the password is never read) |

## 7. Facts in STATE

`identity`, `capabilities`, `platform`, `locked` and (wp3-installer's) `install` are children of the existing `machine` block,
after its present keys (`renderer`, `memMb`, `gcPauseMsTotal`, `present`, `gl`); the spine reserves all five there.

```json
"machine": {
  "identity": {
    "build": { "version": "0.2.0", "informational": "0.2.0+17.abc1234", "fileVersion": "0.2.0.17", "msi": "0.2.17" },
    "os": { "edition": "IoTEnterpriseS", "version": "24H2", "build": 26100, "revision": 4061, "ltsc": true, "iot": true },
    "cpu": { "model": "AMD Ryzen 9 9950X3D", "logical": 32, "byEfficiencyClass": [32], "vectorBits": 512 },
    "adapters": [ { "luid": "0x…", "vendorId": 4318, "name": "NVIDIA RTX PRO 4000 Blackwell", "driver": "582.16",
                    "tier": "nvidiaRtxPro", "drivesOutputs": true } ],
    "runtimes": { "agility": "1.619.2 (app)", "ffmpeg": "n9.0.2", "syncFirmware": "3.06" }
  },
  "capabilities": [ { "feature": "presentBarrier", "luid": "0x…", "supported": true, "words": "", "driver": "582.16" } ],
  "platform": {
    "claims": [ { "what": "process.qos", "asked": "high", "applied": true, "readBack": "high", "words": "" } ],
    "timer": { "lateP99Us": 180 }, "awake": true, "gc": "sustainedLowLatency", "hags": true, "powerPlan": "Ultimate Performance"
  },
  "locked": { "at": 1760600000000, "drift": [] }
}
```

The context's machine item carries the build, the OS line, the tier and the drift count; MCP's resources carry the whole block.

## 8. Verbs

| Row | Risk / class | Does |
|---|---|---|
| `MACHINE [STATUS]` | Low / Query | the identity, the claims and the drift, in words |
| `MACHINE SET <word> <value>` | Review (REALTIME: High) / Setup | changes one claim's setting (`NodeSettings.Machine`) and applies it now where Windows allows, else at the next start, saying which; refused at its value, armed by the class, REALTIME without the privilege |
| `MACHINE LOCK [CLEAR]` | Review / Setup | keeps this node's driver set (adapters by vendor, device and name; Agility; FFmpeg; sync firmware; OS build; PlayMap's build) as the one its shows run on; preflight then names any drift. The set is this node's, in `NodeSettings.Machine.Locked`, written by the node's settings writer (one writer) — never in the project, so a hot backup keeps its own and never reports the primary's drivers as drift. `MACHINE LOCK` when the set equals the one locked is refused ("this machine's drivers are already the locked set"); `CLEAR` with no lock refused ("no driver set is locked") |

## 9. Tests

| Test | Job | Holds |
|---|---|---|
| `PlatformPolicyTests` | both | the role → claims table; a fake 8 + 16 core topology → the critical pool on the 8; a refused claim keeps its words; a setting at its value refused |
| `PlatformCallsTests` | Windows | every claim answers what it asked and what Windows said (on the runner's Server SKU and Basic Render Driver, a refusal with its named words passes — M14 records which); the throttling state read back equals what was set where Windows answers; MMCSS taken and reverted where the service runs; 1 000 waits of 1 ms on today's wait and on the timer: both lateness distributions recorded, no ceiling (a shared runner), the order of the calls and the claims' fields asserted |
| `PlatformLayoutTests` | both | the platform's own DXGI and NVAPI probe structures against the headers' sizes and offsets |
| `CpuSetTests` | both | a fake topology of 8 performance and 16 efficiency cores → the critical pool on the 8, background on the 16; one class → nothing set, said |
| `IdentityTests` | both | keys and types from a fake platform; `machine.json`; a changed driver against the lock → the amber row with both versions and `MACHINE LOCK` |
| `MachineVerbTests` | both | REALTIME without the privilege; armed; at its value; `MACHINE LOCK` on the locked set and `CLEAR` with none, each refused with its reason |
| `PreflightMachineTests` | both | each row fails on an injected fake fact and passes once fixed; fix lines parse |
| `NullPlatformTests` | Linux | the words in STATE and preflight; no claim attempted |

## 10. Risks

- **Claims that hurt** (`PLAN.md` R13): each is a setting with a read-back; L10 measures them; realtime GPU priority stays off until
  measured.
- **Reading what Windows does not document as an API** (power settings by GUID, policy registry values): each row says where it
  read and answers "unknown" with words when a value is absent, never a guess.
- **A dual-CCD X3D CPU** parks a CCD only for a game; PlayMap is not one. The CPU sets can confine the critical pool to one L3
  domain, an option off by default until L10 measures it.

## 11. AMD's display state (`Vendors/`, wp3-framelock's in W4)

A read-only ADLX probe — FreeSync on or off and the colour depth per display — loaded from the driver's own `amdadlx64.dll`, never
bundled (ADLX ships with the driver under AMD's own terms), fills the capability snapshot for the lock's VRR precondition on AMD
(`present.md` §16). It changes nothing on the display; forcing FreeSync off and 10-bit stays below the bar this round. It lives in
`src/PlayMap.Platform.Windows/Vendors/`, owned by wp3-framelock in W4.
