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

## 2. The claims

| Claim | Call | Read back | Default | `MACHINE SET` word | Refused when |
|---|---|---|---|---|---|
| Process priority | `SetPriorityClass(HIGH_PRIORITY_CLASS)`; realtime never | `GetPriorityClass` | on | `PROCESS HIGH\|NORMAL` | — |
| HighQoS, every timer honoured | `SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED \| IGNORE_TIMER_RESOLUTION` in the control mask and a zero state mask | `GetProcessInformation` | on | `QOS HIGH\|AUTO` | — |
| GPU scheduling class | `D3DKMTSetProcessSchedulingPriorityClass(HIGH)` | `D3DKMTGetProcessSchedulingPriorityClass` | on | `GPU-PRIORITY NORMAL\|HIGH\|REALTIME` | REALTIME without `SeIncreaseBasePriorityPrivilege` (the installer grants it to the show account); REALTIME also asks the direct queue for `GLOBAL_REALTIME` at the next device start, whose creation fails rather than downgrades |
| MMCSS for render and present | `AvSetMmThreadCharacteristicsW("Games")`, `AvSetMmThreadPriority(AVRT_PRIORITY_HIGH)`; reverted with `AvRevertMmThreadCharacteristics` | the handle, the task's registry values (read, never written) | on | `MMCSS ON\|OFF` | the MMCSS service stopped |
| MMCSS for audio and timecode | `"Pro Audio"` (priority 23–26) | as above | on | as above | as above |
| Decode | above-normal priority; the critical CPU set (stretch) | `GetThreadPriority` | on | — | — |
| Background pools | EcoQoS: `SetThreadInformation(ThreadPowerThrottling)` with `EXECUTION_SPEED` in both masks; below-normal priority | `GetThreadInformation` | on | — | — |
| CPU sets (stretch) | `SetThreadSelectedCpuSets`: the critical pool on the highest `EfficiencyClass`, background pools on the rest; never a hard affinity | `GetThreadSelectedCpuSets` | AUTO | `CPUSETS AUTO\|OFF` | one efficiency class only (nothing to separate), said |
| Keep awake | `SetThreadExecutionState(ES_CONTINUOUS \| ES_DISPLAY_REQUIRED \| ES_SYSTEM_REQUIRED)` while an output is open | the previous state it returns | on | `AWAKE ON\|OFF` | — |
| GC while armed | `GCSettings.LatencyMode = SustainedLowLatency` on `SHOW ARM`, back on `SHOW DISARM` | `GCSettings.LatencyMode` | on | `GC SHOW\|DEFAULT` | the runtime refuses (server GC), said |

Defaults are on where the cause is documented (Windows' own throttling of an occluded or windowless process; timer requests ignored;
the display sleeping) — L10 measures every claim's effect on p99.9 frame time, and a default that measures worse changes in one
commit citing the record. A setting already at its value is refused with words ("QoS is already high"), as the doctrine asks.

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
  headless pacer is the first user: today it waits with `AutoResetEvent.WaitOne` at the default resolution, a measured cause
  waiting to be measured [map §3.1].
- Stamps stay QPC (`Stopwatch`, `MonoClock`); wall-clock stamps that cross machines use `GetSystemTimePreciseAsFileTime`, as the
  clock's sync already does.
- The timer's lateness (p99 of the last 1 000 waits) is in STATE; a p99 over 1 ms is amber in preflight.

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
    VendorTier Tier, bool DrivesOutputs, long DedicatedMb);
public enum VendorTier { NvidiaRtxPro, NvidiaRtx, Radeon, IntelArc, Other, Software }
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
| GPUs | DXGI 1.6 `EnumAdapterByGpuPreference` (LUID, vendor and device ids, name, memory); the driver's user-mode version (`CheckInterfaceSupport`); on NVIDIA `NvAPI_SYS_GetDriverAndBranchVersion` and `NvAPI_GPU_QueryWorkstationFeatureSupport(PROVIZ)` (RTX PRO) |
| HAGS | `D3DKMTQueryAdapterInfo` (the WDDM 2.7 caps' scheduling flag) — recorded, never forced |
| Runtimes | the Agility runtime from wp3-d3d12-core, FFmpeg's version and configuration from Media, the board's firmware from the frame-lock tier, each filled by its owner |

The identity is read at start, after a device change and after a rebuild; the capability snapshot by each owner's probes (the
platform's own: HAGS, ReBAR's GPU upload heaps through the device, timer resolution honoured). Vendor libraries bind to adapters by
LUID, never by ordinal. Within one boot a LUID names an adapter; across boots it does not, so `MACHINE LOCK` keys adapters by vendor
id, device id and name.

## 6. Machine preflight

Each row is a fact with its light and its words; a fix line is given only where a verb fixes it, otherwise the words say what a
person does (and wp3-installer's stretch adds the show-machine policies that write some of them once, at install).

| Row | Light | Words / fix |
|---|---|---|
| OS build below 26100 | red | "Windows 11 24H2 or later is required (this is build 22631)" |
| OS edition | grey (fact) | "Windows 11 IoT Enterprise LTSC 2024 · build 26100.4061" |
| Power plan not High or Ultimate Performance | amber | the plan's name and how to set it |
| USB selective suspend on; PCIe link-state power management on | amber | the setting's name |
| Display or sleep timeout set | amber | "the display sleeps after 10 min on mains" — fix `MACHINE SET AWAKE ON` when it is off |
| Screen saver on | amber | "keeping the display awake does not stop the screen saver" |
| Notifications on | amber | the setting |
| A reboot pending | amber | from `RebootRequired`, `RebootPending`, `PendingFileRenameOperations` |
| Windows Update not deferred | amber | the policy values read |
| HAGS | grey (fact) | on or off; never changed |
| A claim of §2 refused | amber | the claim's words |
| The timer's p99 lateness over 1 ms | amber | the measured value |
| Drift from `MACHINE LOCK`'s set | amber | "GPU driver 582.16 → 591.44 since the show was locked" — fix `MACHINE LOCK` |
| Auto-logon | grey on a desk machine, amber on an installed node without it | from `Winlogon`'s `AutoAdminLogon` and `DefaultUserName` (the password is never read) |

## 7. Facts in STATE

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
| `MACHINE LOCK [CLEAR]` | Review / Setup | keeps this machine's driver set (adapters by vendor, device and name; Agility; FFmpeg; sync firmware; OS build; PlayMap's build) as the show's; preflight then names any drift |

## 9. Tests

| Test | Job | Holds |
|---|---|---|
| `PlatformPolicyTests` | both | the role → claims table; a fake 8 + 16 core topology → the critical pool on the 8; a refused claim keeps its words; a setting at its value refused |
| `PlatformCallsTests` | Windows | every claim answers; the throttling state read back equals what was set; MMCSS taken and reverted; 1 000 waits of 1 ms on the timer: p99 recorded, a 4× ceiling asserted |
| `IdentityTests` | both | keys and types from a fake platform; `machine.json`; a changed driver against the lock → the amber row with both versions and `MACHINE LOCK` |
| `MachineVerbTests` | both | REALTIME without the privilege; armed; at its value |
| `PreflightMachineTests` | both | each row fails on an injected fake fact and passes once fixed; fix lines parse |
| `NullPlatformTests` | Linux | the words in STATE and preflight; no claim attempted |

## 10. Risks

- **Claims that hurt** (`PLAN.md` R13): each is a setting with a read-back; L10 measures them; realtime GPU priority stays off until
  measured.
- **Reading what Windows does not document as an API** (power settings by GUID, policy registry values): each row says where it
  read and answers "unknown" with words when a value is absent, never a guess.
- **A dual-CCD X3D CPU** parks a CCD only for a game; PlayMap is not one. The CPU-set stretch can confine the critical pool to one
  L3 domain, off by default until L10 measures it.
