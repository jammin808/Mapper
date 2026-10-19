# The machine platform: what a show node claims from Windows, and what it is

Windows 11 lowers the quality of service of a process whose windows are hidden, covered or minimised, and stops honouring its timer
requests; an engine whose outputs sit behind the desk, or that has no window at all, is that process. The engine claims what it needs
once at start, by role for each of its threads, through one small seam — and every claim answers what it asked, what Windows said and
what reading it back shows. The node's identity (its build, the OS edition and build, the CPU, every GPU by LUID with its driver and
vendor tier, the runtimes it loaded) is one record in STATE that preflight compares with the driver set locked for the show. Nothing on
the machine changes silently: what the engine cannot claim as a standard user is a fact with words.

Design: `docs/round3/design/platform.md`. Wire: `docs/wire/machine.md`.

## The parts

| Where | What |
|---|---|
| `src/PlayMap.Core/Machine` | The seam (`IMachinePlatform`, `IThreadClaim`, `IDeadlineTimer`, `ThreadClaims`) and the policy, pure, proven on both CI jobs: `MachineSettings` and `LockedDriverSet` (kept in the node's own settings, never the project), `PlatformPolicy` (MACHINE SET's words, each default with its cause, the role → claims table), `CpuSetPlan` (placement), `EditionTable` and `ServicingTable` (the edition, its support tier and its end of servicing), `VendorPolicy` (the published GPU policy), `MachineRows` (the machine's preflight rows from what was read), `FirewallRules` and `PeImage` (text the platform reads, parsed). Core's `NullMachinePlatform` claims nothing and says why |
| `src/PlayMap.Platform.Windows` | `WindowsMachinePlatform`, an edge referencing Core alone: the Win32 calls by `LibraryImport`, the DXGI and NVAPI probes by its own minimal declarations, each structure pinned by a layout golden (`PlatformLayoutTests`). Every type is Windows only |
| `src/PlayMap.Engine/Machine` | `MachineParts` composes the platform (Windows' on Windows; elsewhere the null platform, "the platform seam is Windows only"), makes the process's claims in its Configure before any part starts a thread, holds keep-awake, and adds the machine preflight; `RecordedPlatform` keeps every claim made through the platform and every deadline wait's lateness for STATE; `MachineNode` keeps the settings through the network host, the settings file's one writer, and the runtimes their owners fill |

The host points `ThreadClaims` at the composed platform once at start; each thread claims its role in one line at its start
(`using var claim = ThreadClaims.Claim(ThreadRole.Decode);`) and the claim reverts at its end.

## The claims

| Claim | Call | Read back | Default | Its cause | `MACHINE SET` | Applies |
|---|---|---|---|---|---|---|
| Process priority | `SetPriorityClass(HIGH_PRIORITY_CLASS)`; realtime never | `GetPriorityClass` | HIGH | documented: the class for a process whose threads are mostly blocked and run briefly for time-critical events | `PROCESS HIGH\|NORMAL` | now |
| HighQoS, every timer request honoured | `SetProcessInformation(ProcessPowerThrottling)`, `EXECUTION_SPEED \| IGNORE_TIMER_RESOLUTION` in the control mask, a zero state mask | `GetProcessInformation` | HIGH | documented: Windows 11 lowers the QoS of an occluded, minimised or windowless process and ignores its timer requests | `QOS HIGH\|AUTO` | now |
| GPU scheduling class | `D3DKMTSetProcessSchedulingPriorityClass` | `D3DKMTGetProcessSchedulingPriorityClass` | **NORMAL** | none documented: off until the lab measures HIGH (L10) | `GPU-PRIORITY NORMAL\|HIGH\|REALTIME`; REALTIME needs `SeIncreaseBasePriorityPrivilege`, which the installer grants the show account, and is refused without it, naming it | now |
| MMCSS | render and present `"Games"`, audio and timecode `"Pro Audio"`, capture `"Capture"` (`AvSetMmThreadCharacteristicsW`, `AvSetMmThreadPriority(HIGH)`), reverted with `AvRevertMmThreadCharacteristics` | the task index | ON | documented: Windows' service for time-critical multimedia threads | `MMCSS ON\|OFF` | each thread's next start |
| Thread priority | above normal for render, present, decode, audio, timecode, control, LED and capture; normal for network servers; below normal for background pools | `GetThreadPriority` | — | the roles' table | — | — |
| CPU sets | the critical pool (render, present, decode, capture) on the highest efficiency class, background pools on the rest (`SetThreadSelectedCpuSets`, soft, never a hard affinity); one efficiency class sets nothing and says so | `GetThreadSelectedCpuSets` | AUTO | documented: CPU sets, never affinity, on hybrid processors | `CPUSETS AUTO\|OFF` | each thread's next start |
| EcoQoS for background pools | `SetThreadInformation(ThreadPowerThrottling)`, `EXECUTION_SPEED` in both masks | `GetThreadInformation` | on | documented: the explicit level for background work | — | — |
| The deadline timer | `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)`, the wait taken with the cancel handle | the waits' lateness | HIGH | documented: a high-resolution timer wakes near its due time without raising the system's timer resolution | `TIMER HIGH\|DEFAULT` | the render host's next start |
| Keep awake | a power request (`PowerCreateRequest`, display and system required) while the project has an enabled display output | whether it is held | ON | documented: the display and the system otherwise sleep on their timeouts | `AWAKE ON\|OFF` | now |

A default is on only where its cause is documented; the GPU scheduling class has none and is NORMAL. Each default changes — on or
off — in one commit citing L10's record. A setting already at its value is refused with words ("QoS is already high"), as is a word
or value MACHINE SET does not take.

Keep-awake is a power request, a handle any thread holds and clears, rather than `SetThreadExecutionState`, whose state belongs to the
thread that set it and ends with that thread: the engine has no one thread that both sees the outputs open and lives as long as they do.

### On CI

The Windows job's `PlatformCallsTests` makes every claim on the hosted runner (Windows Server 2025 Datacenter, build 26100, the Basic
Render Driver) and its wp3-platform step puts every answer in the log and the summary. There every process claim applied and read back
as set (HIGH and AUTO alike), every role's thread claims applied, MMCSS gave each of Games, Pro Audio and Capture a task, CPU sets set
nothing on its one efficiency class and said so, EcoQoS read back for background, the realtime privilege is held (the runner is
elevated) and the high-resolution timer is made. Where a later runner refuses, the test passes on a refusal with its named words.

## Time

Every deadline wait on a claimed thread goes through the platform's `IDeadlineTimer`. The headless pacer is the first user: it waited
on its wake handle's own timeout at the default timer resolution. Measured on the hosted runner before it switched (`PlatformCallsTests`,
1 000 waits of 1 ms each, run 39214709394), lateness in µs:

| Wait | p50 | p90 | p99 | p99.9 | max |
|---|---|---|---|---|---|
| The wait handle's timeout (the pacer's wait before) | 12 564 | 14 867 | 15 233 | 15 554 | 15 637 |
| The high-resolution timer | 528 | 532 | 550 | 592 | 668 |

The runner is shared, so no CI test asserts a time; L10 holds the pass mark on show hardware. Stamps stay QPC (`Stopwatch`, `MonoClock`).
The platform's waits' p99 lateness is in STATE (`machine.platform.timer`), and a p99 over 1 ms is amber in preflight.

## The node's identity

`machine.identity` in STATE, read at start and cached (the Windows platform reads it again on `RefreshIdentity`):

- **Build** — PlayMap's version, its SemVer with build metadata and its file version.
- **OS** — `RtlGetVersion`'s build, `GetProductInfo`'s product type and the registry's `EditionID`, `DisplayVersion` and `UBR`,
  classified by `EditionTable`: the family (home, pro, workstation, enterprise, education, iot, server), the LTSC and IoT flags and
  the support tier (`ltsc` for IoT Enterprise LTSC, `managed` for Pro, Pro for Workstations, Enterprise and Education, `home`,
  `unsupported` for a build below 24H2 or a server, `unknown` for an edition the table does not know).
- **CPU** — its name, its logical processors by efficiency class, the widest vector .NET accelerates, AVX-512 and AVX10.
- **GPUs** — every DXGI 1.6 adapter by LUID with vendor and device ids, name, the user-mode driver's version, whether an output is
  attached and its memory; on NVIDIA the driver's version and branch from NVAPI, and RTX PRO told from RTX by
  `NvAPI_GPU_QueryWorkstationFeatureSupport(PROVIZ)`, never by the name. The vendor tier and support level follow `docs/INSTALL.md`:
  NVIDIA RTX and RTX PRO tier 1; AMD's RDNA 3 and 4 Radeons validated per driver (none is validated before the lab runs); Intel Arc
  best effort; software adapters and anything else unsupported.
- **Runtimes** — the Agility runtime, FFmpeg's build and the sync board's firmware, each filled by its owner through
  `MachineNode.FillRuntimes` (empty until then).

`machine.capabilities` carries the platform's own probes: the realtime privilege, hardware-accelerated GPU scheduling as set
(`GraphicsDrivers\HwSchMode`, recorded, never forced), whether the high-resolution timer is made, and whether the process's timer
requests are honoured. The fleet's GPU load sums the 3D engines of the adapter that drives the outputs, by its LUID, where the identity
names one.

## The driver lock

`MACHINE LOCK` keeps this node's driver set — the OS build and revision, PlayMap's build, the runtimes, every GPU by vendor, device and
name with its driver — in `NodeSettings.Machine.Locked` (never in the project, so a hot backup keeps its own). A LUID names an adapter
within one boot only, so the set never keys by one. Preflight names every move from the set as an amber row with both versions and the
fix `MACHINE LOCK`; on Home, where drivers cannot be held back, the row says the node observes the change. The set already locked again,
and `MACHINE LOCK CLEAR` with none, are refused with the reason and nothing is written.

## Machine preflight

Section `machine`, every row a fact with words, never a silent change; a fix line only where a verb fixes it:

| Row | Light | Words / fix |
|---|---|---|
| Windows build below 26100 | red | "Windows 11 24H2 or later is required (this is build 22631)" |
| The edition | grey | "Windows 11 Home 26H2 · build 26300.7019 · supported, unmanaged" |
| A layer the edition lacks (Home) | grey, value **i** | updates cannot be deferred (pause them in Settings ▸ Windows Update); no kiosk; outputs are desktop monitors (keep the desk's monitor primary); Remote Desktop cannot reach this node. PlayMap writes no Update policy on Home |
| Smart App Control evaluating on an unsigned build | amber | "turn Smart App Control off before the show: once it enforces, Windows blocks this unsigned build and this node will not start"; enforcing has no row, since the unsigned engine that would raise it cannot start |
| The OS version past its end of servicing | amber | "Windows 11 Home 24H2 left servicing on 13 Oct 2026: this node gets no security updates — move to 25H2 or later"; a version the build's servicing table (dated 19 Oct 2026) does not name is grey, "servicing unknown" |
| An enabled display output on the primary display | amber | "notifications, UAC and setup screens open on Stage left: make the desk's monitor primary" |
| A connected network on Public with no rule letting PlayMap in there | amber | the network and the Settings path to make it Private |
| The power plan | amber unless High or Ultimate Performance | on a machine that offers Balanced only, its power mode and the settings that matter, never "not High Performance" |
| Sleep or display timeout on mains | amber | the timeout; with keep-awake off, the fix `MACHINE SET AWAKE ON` |
| The screen saver | amber when on | "keeping the display awake does not stop the screen saver" |
| A reboot pending | amber | from Windows Update's `RebootRequired`, servicing's `RebootPending`, `PendingFileRenameOperations` |
| A claim Windows refused | amber | the claim's words |
| The timer's p99 lateness over 1 ms | amber | the measured value |
| Drift from MACHINE LOCK's set | amber | "NVIDIA RTX PRO 4000 Blackwell driver 582.16 → 591.44 since the driver set was locked" — fix `MACHINE LOCK` |

A reading Windows does not give is grey "unknown" with the words where it was read, never a guess. On a node that is not Windows the
section is one grey row: "the platform seam is Windows only".

## Verbs, STATE, the context, INDEX

`MACHINE [STATUS]`, `MACHINE SET`, `MACHINE LOCK [CLEAR]` (`docs/wire/machine.md`). Both changes are Setup-class: refused while the
show is armed. STATE's `machine` block carries `identity`, `capabilities`, `platform` (the platform's name and words, its light, every
claim with its light, the settings, the timer, whether keep-awake is held) and `locked` (null, or the set and its drift). The context's
`machine:platform` item on a Windows node carries the build, the OS line, the tier and the drift count. INDEX's `machine` section lists
each claim lit by its answer, as feedback.

## What CI proves, and what waits for the lab

| Evidence | Where |
|---|---|
| The policy, placement, the edition and servicing rows, the GPU policy, the drift, the verbs, STATE and preflight, against fakes | both jobs (`PlatformPolicyTests`, `CpuSetTests`, `IdentityTests`, `EditionRowTests`, `PreflightMachineRowTests`, `MachineVerbTests`, `PreflightMachineTests`, `MachineStateFixtureTest`) |
| The probe structures' sizes and offsets, the vtable slots | both jobs (`PlatformLayoutTests`) |
| Every claim answers on Windows; read-backs; MMCSS taken and reverted; the two waits measured | the Windows job (`PlatformCallsTests`), its answers in the summary |
| L10: p99.9 frame time with and without each claim on NVIDIA, AMD and Intel nodes, a hybrid Intel CPU, a dual-CCD X3D CPU | **not run** (no lab this round): `docs/round3/lab/L10-platform-claims.md` |
| L22: a precise Windows thread time | **not run**: `docs/round3/lab/L22-thread-time.md` |
| L23: the edition's rows and the claims on a clean Home node | **not run**: `docs/round3/lab/L23-home-node.md` |

No hardware fact here was measured on show hardware: the defaults with no documented cause stay off, and every number above is the
shared runner's.

## Next round

Core reservation, large pages for frame pools, a pre-roll measured against NativeAOT (M13); the preflight rows the stretch names (USB
selective suspend, PCIe link-state power management, notifications, the Update deferral policy, HAGS, Memory Integrity, Game Mode and
each media volume's encryption); the GC's show mode while armed with its pause histogram (`GC SHOW`), which waits for a measured cause.
