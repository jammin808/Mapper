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
| `src/PlayMap.Core/Machine` | The seam (`IMachinePlatform` with what a platform reads back of what it uses and holds, answered by its defaults on a platform that claims nothing; `IThreadClaim`; `IDeadlineTimer`; `ThreadClaims` with each deadline thread's timer; `ShowDoor`) and the policy, pure, proven on both CI jobs: `MachineSettings` and `LockedDriverSet` (kept in the node's own settings, never the project), `PlatformPolicy` (MACHINE SET's words, each default with its cause, the role → claims table), `CpuSetPlan` (placement), `EditionTable` and `ServicingTable` (the edition, its support tier and its end of servicing), `VendorPolicy` (the published GPU policy), `MachineRows` (the machine's preflight rows from what was read), `FirewallRules` and `PeImage` (text the platform reads, parsed). Core's `NullMachinePlatform` claims nothing and says why |
| `src/PlayMap.Platform.Windows` | `WindowsMachinePlatform`, an edge referencing Core alone: the Win32 calls by `LibraryImport`, the DXGI and NVAPI probes by its own minimal declarations, each structure pinned by a layout golden (`PlatformLayoutTests`). Every type is Windows only |
| `src/PlayMap.Engine/Machine` | `MachineParts` composes the platform (Windows' on Windows; elsewhere the null platform, "the platform seam is Windows only"), makes the process's claims in its Configure before any part starts a thread, hands the platform every saved setting before anything acts on it (`IMachinePlatform.Use`) and the node's open show doors with its facts (the network host's, then the control inputs'), holds keep-awake, and adds the machine preflight; `RecordedPlatform` keeps every claim made through the platform, the kind of timer made, every deadline wait's lateness, and what the platform reads back of keep-awake and the power plan, for STATE; `MachineNode` keeps the settings through the network host, the settings file's one writer, and the runtimes their owners fill |

The host points `ThreadClaims` at the composed platform once at start; each thread claims its role in one line at its start
(`using var claim = ThreadClaims.Claim(ThreadRole.Decode);`) and the claim reverts at its end. The claim holds the thread itself, so any
thread may dispose it. A listener with no thread of its own — an LTC reader, fed by the audio hub's capture thread with every other
listener on that device — claims through a `DeliveryClaim`: on the delivering thread at its first delivery, and again when the device
reopens on another thread. Windows' platform joins a second claim on a thread already claimed to the first (the same claims, reverted
when the last holder lets go), so two LTC sources on one device never stack their priorities. The null platform attempts no thread
claim: its one claim asks nothing and is grey, and nothing is recorded for a thread.

## The claims

| Claim | Call | Read back | Default | Its cause | `MACHINE SET` | Applies |
|---|---|---|---|---|---|---|
| Process priority | `SetPriorityClass(HIGH_PRIORITY_CLASS)`; realtime never | `GetPriorityClass` | HIGH | documented: the class for a process whose threads are mostly blocked and run briefly for time-critical events | `PROCESS HIGH\|NORMAL` | now |
| HighQoS, every timer request honoured | `SetProcessInformation(ProcessPowerThrottling)`, `EXECUTION_SPEED \| IGNORE_TIMER_RESOLUTION` in the control mask, a zero state mask | `GetProcessInformation` | HIGH | documented: Windows 11 lowers the QoS of an occluded, minimised or windowless process and ignores its timer requests | `QOS HIGH\|AUTO` | now |
| GPU scheduling class | `D3DKMTSetProcessSchedulingPriorityClass` | `D3DKMTGetProcessSchedulingPriorityClass` | **NORMAL** | none documented: off until the lab measures HIGH (L10) | `GPU-PRIORITY NORMAL\|HIGH\|REALTIME`; REALTIME needs `SeIncreaseBasePriorityPrivilege` and is refused without it, in words that name it and say the installer grants it to the show account (wp3-installer's grant); REALTIME's `GLOBAL_REALTIME` direct queue is wp3-d3d12-core's (plan §5.5) and is not asked on this build, whose engine opens no Direct3D 12 device | now |
| MMCSS | render and present `"Games"`, audio and timecode `"Pro Audio"`, capture `"Capture"` (`AvSetMmThreadCharacteristicsW`, `AvSetMmThreadPriority(HIGH)`), reverted with `AvRevertMmThreadCharacteristics` | the task index | ON | documented: Windows' service for time-critical multimedia threads | `MMCSS ON\|OFF` | each thread's next start |
| Thread priority | above normal for render, present, decode, audio, timecode, control, LED and capture; normal for network servers; below normal for background pools | `GetThreadPriority` | — | the roles' table | — | — |
| CPU sets | the critical pool (render, present, decode, capture) on the highest efficiency class, background pools on the rest (`SetThreadSelectedCpuSets`, soft, never a hard affinity); one efficiency class sets nothing and says so | `GetThreadSelectedCpuSets` | AUTO | documented: CPU sets, never affinity, on hybrid processors | `CPUSETS AUTO\|OFF\|ONE-CACHE`; ONE-CACHE confines the critical pool to the highest class's first last-level cache (a dual-CCD CPU's first CCD), the background on every other set, and has no documented cause: off until L10 step 6 measures it | each thread's next start |
| EcoQoS for background pools | `SetThreadInformation(ThreadPowerThrottling)`, `EXECUTION_SPEED` in both masks | `GetThreadInformation` | on | documented: the explicit level for background work | — | — |
| The deadline timer | `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)`, the wait taken with the cancel handle | the waits' lateness | HIGH | documented: a high-resolution timer wakes near its due time without raising the system's timer resolution | `TIMER HIGH\|DEFAULT` | each deadline thread's next start |
| The GC while armed | `GCSettings.LatencyMode = SustainedLowLatency` as the show arms, the mode it found put back at disarm (the arm hook `gc`) | `GCSettings.LatencyMode` | **DEFAULT** | none documented: Microsoft names animation as its case, but it grows an uncompacted heap; the collector's pauses are counted into a histogram in STATE beside the frame time, the measurement this default waits for (L10) | `GC DEFAULT\|SHOW` | the show's next arm |
| Keep awake | a power request (`PowerCreateRequest`, display and system required) while the project has an enabled display output | whether it is held | ON | documented: the display and the system otherwise sleep on their timeouts | `AWAKE ON\|OFF` | now |

A default is on only where its cause is documented; the GPU scheduling class, the GC's show mode and ONE-CACHE have none and are off. Each default changes — on or
off — in one commit citing L10's record. A setting already at its value is refused with words ("QoS is already high"), as is a word
or value MACHINE SET does not take.

Keep-awake is a power request, a handle any thread holds and clears, rather than `SetThreadExecutionState`, whose state belongs to the
thread that set it and ends with that thread: the engine has no one thread that both sees the outputs open and lives as long as they do.
While it is asked it is a claim (`process.awake`, asked `on`): applied while the request is in force, refused with Windows' words when
`PowerCreateRequest` or `PowerSetRequest` refuses. STATE's `awake` is that read-back, never what was wanted: on a node that is not
Windows nothing is held, and it reads false.

Every saved MACHINE SET reaches the platform before the machine part acts on it: keep-awake is taken or let go under the new AWAKE, the
next thread claimed follows MMCSS and CPUSETS (placed afresh), the next deadline timer follows TIMER, and the machine's facts follow
AWAKE; the process's claims (QOS, PROCESS, GPU-PRIORITY) are made again at once.

### On CI

The Windows job's `PlatformCallsTests` makes every claim on the hosted runner (Windows Server 2025 Datacenter, build 26100, the Basic
Render Driver) and its wp3-platform step puts every answer in the log and the summary. There every process claim applied and read back
as set (HIGH and AUTO alike), every role's thread claims applied, MMCSS gave each of Games, Pro Audio and Capture a task, CPU sets set
nothing on its one efficiency class and said so, EcoQoS read back for background, the realtime privilege is held (the runner is
elevated) and the high-resolution timer is made. Where a later runner refuses, the test passes on a refusal with its named words.

## Time

Every deadline thread waits on the platform's `IDeadlineTimer`, made on that thread (`ThreadClaims.CreateTimer`: the LED sender's and
each audio feeder's as the thread starts, the headless pacer's at its first wait) and disposed at its end: the headless pacer (until
its next frame's deadline, its wake handle the cancel), the LED sender (until a millisecond before its next instant, woken early by
whatever hands it work, spinning the last millisecond) and each audio output's feeder (5 ms between service steps, its stop the
cancel). Their instants are on the machine's monotonic clock, which the platform's timers wait on; a thread on any other clock (a
test's hand-moved one) waits on the runtime's own wait on that clock, and the audio feeder never reads its output's clock to wait. The
two waits measured on the hosted runner (`PlatformCallsTests`, 1 000 waits of 1 ms each, run 39214709394), lateness in µs:

| Wait | p50 | p90 | p99 | p99.9 | max |
|---|---|---|---|---|---|
| A wait handle's timeout (the runtime's wait, TIMER DEFAULT) | 12 564 | 14 867 | 15 233 | 15 554 | 15 637 |
| The high-resolution timer | 528 | 532 | 550 | 592 | 668 |

The runner is shared, so no CI test asserts a time; L10 holds the pass mark on show hardware. Stamps stay QPC (`Stopwatch`, `MonoClock`).
The p99 lateness of every deadline thread's waits (the last 1 000) is in STATE (`machine.platform.timer`), with the kind of the last
timer made (`mode`: `high`, `default`, or `none` before any; never the setting, which applies from the next timer made), and a p99 over
1 ms is amber in preflight.

The collector's pauses are read in-process from the runtime's own GC events (EventPipe, `Microsoft-Windows-DotNETRuntime`, the GC
keyword at Informational: each pause from the execution engine's suspension to its restart) and published as
`machine.platform.gc.pauses` — the count, the p99 of the last 1 000, the longest and a histogram under 1, 2, 5, 10, 20 and 50 ms.

## The node's identity

`machine.identity` in STATE, read at start and cached (the Windows platform reads it again on `RefreshIdentity`):

- **Build** — PlayMap's version, its SemVer with build metadata and its file version.
- **OS** — `RtlGetVersion`'s build, `GetProductInfo`'s product type and the registry's `EditionID`, `DisplayVersion` and `UBR`,
  classified by `EditionTable`: the family (home, pro, workstation, enterprise, education, iot, server), the LTSC and IoT flags and
  the support tier: one of the plan's three (D4) — `ltsc` for IoT Enterprise LTSC, `managed` for Pro, Pro for Workstations, Enterprise
  and Education, `home` — or `unsupported` for a build below 24H2 or a server, `unknown` for an edition the table does not know.
- **CPU** — its name, its logical processors by efficiency class, the widest vector .NET accelerates, AVX-512 and AVX10.
- **GPUs** — every DXGI 1.6 adapter by LUID with vendor and device ids, name, the user-mode driver's version, whether an output is
  attached and its memory; on NVIDIA the driver's version and branch from NVAPI, and RTX PRO told from RTX by
  `NvAPI_GPU_QueryWorkstationFeatureSupport(PROVIZ)`, never by the name. NVAPI is the driver's (`nvapi64.dll`, never shipped), loaded
  from the system directory by its absolute path, never by a search by name. The vendor tier and support level are the plan's (D4):
  NVIDIA RTX and RTX PRO tier 1; AMD's RDNA 3 and 4 Radeons validated per driver (none is validated before the lab runs); Intel Arc
  best effort; software adapters and anything else unsupported.
- **Runtimes** — the Agility runtime, FFmpeg's build and the sync board's firmware, each filled by its owner through
  `MachineNode.FillRuntimes` (wp3-d3d12-core the Agility runtime's, Media FFmpeg's, wp3-framelock the firmware's); no owner fills one
  on this build, so each reads empty in STATE and in `MACHINE LOCK`'s set.

`machine.capabilities` carries the platform's own probes: the realtime privilege; hardware-accelerated GPU scheduling for each adapter by
its LUID with its driver, as its kernel driver answers (`D3DKMTQueryAdapterInfo`, `KMTQAITYPE_WDDM_2_7_CAPS`: supported, and on or off
in the words; recorded, never forced); whether the high-resolution timer is made; whether the process's timer requests are honoured
(only where the QoS claim asked HIGH and reads back high: a claim Windows does not read back is "set; Windows does not read it back
here"); and, only when one fails, why: DXGI's failure (`adapters`), so an empty adapter list never reads as "no GPU", and NVAPI's
(`nvapi`) where an NVIDIA adapter is present, so an RTX PRO told by its name alone says so. The probes and the active power plan are
read at start and again where the process's claims or the identity are made again, never on STATE's path; STATE carries the plan as
`machine.platform.powerPlan` (`name`, `kind`: `high`, `ultimate`, `balanced`, `saver`, `other`, `""` unread).

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
| A layer the edition lacks (Home; Pro and Pro for Workstations: the kiosk's) | grey, value **i** | updates cannot be deferred (pause them in Settings ▸ Windows Update); no kiosk; outputs are desktop monitors (keep the desk's monitor primary); Remote Desktop cannot reach this node. PlayMap writes no Update policy on Home. On Pro and Pro for Workstations: "no Shell Launcher on this edition: the supervisor starts the engine with Explorer underneath" |
| Smart App Control evaluating on an unsigned build | amber | "turn Smart App Control off before the show: once it enforces, Windows blocks this unsigned build and this node will not start"; enforcing has no row, since the unsigned engine that would raise it cannot start |
| The OS version past its end of servicing | amber | "Windows 11 Home 24H2 left servicing on 13 Oct 2026: this node gets no security updates — move to 25H2 or later"; a version the build's servicing table (dated 19 Oct 2026) does not name is grey, "servicing unknown" |
| An enabled display output on the primary display | amber | "notifications, UAC and setup screens open on Stage left: make the desk's monitor primary" |
| A network show traffic can cross on Public with no rule letting PlayMap in there (the local and the policy store's rules read, a rule's program path expanded) | amber | when every show door (the line wire, the web port, the clock sync while leading, OSC, Art-Net, sACN) is bound to an address, only the networks whose adapters carry one are read (each network's connections give its adapters, joined to the machine's interfaces by adapter GUID), and the row names the doors, the address and the adapter; when a door listens on every interface, every connected network is read and the row says which doors do, and to make the network Private or bind the node to its show adapter's address; with nothing amber, a green row says what was read |
| The power plan | amber unless High or Ultimate Performance | on a machine that offers Balanced only, its power mode and the settings that matter, never "not High Performance" |
| Sleep or display timeout on mains | amber | the timeout; with keep-awake off, the fix `MACHINE SET AWAKE ON` |
| The screen saver | amber when on (its flag set and a screen saver chosen, `SCRNSAVE.EXE` or its policy key: the flag alone reads set with "(None)") | "keeping the display awake does not stop the screen saver" |
| A reboot pending | amber | from Windows Update's `RebootRequired`, servicing's `RebootPending`, `PendingFileRenameOperations` |
| USB selective suspend on (mains) | amber | the setting and where a person turns it off |
| PCIe link-state power management saving power (mains) | amber | moderate or maximum, and where a person turns it off |
| Notifications on | amber | "a toast can open over an output" — Settings ▸ System ▸ Notifications |
| Windows Update not deferred, paused or held (every edition with the Update client policies; Home has its information row instead) | amber, green when held | the policy values read: a quality-update deferral, automatic updates off or notify-only, a pause and its end |
| Hardware-accelerated GPU scheduling (the person's `HwSchMode`), Memory Integrity (as it runs, `SystemCodeIntegrityInformation`; else its setting, unknown when absent), Game Mode | grey | on, off, unset or unknown; never changed |
| A claim Windows refused (keep-awake's among them) | amber | the claim's words |
| The timer's p99 lateness over 1 ms | amber | the measured value |
| Drift from MACHINE LOCK's set | amber | "NVIDIA RTX PRO 4000 Blackwell driver 582.16 → 591.44 since the driver set was locked" — fix `MACHINE LOCK` |

A reading Windows does not give is grey "unknown" with the words where it was read, never a guess. On a node that is not Windows the
section is the platform's grey row, "the platform seam is Windows only", and the driver lock's row (and an enabled display output on the
primary display, where there is one).

## Verbs, STATE, the context, INDEX

`MACHINE [STATUS]`, `MACHINE SET`, `MACHINE LOCK [CLEAR]` (`docs/wire/machine.md`). Both changes are Setup-class: refused while the
show is armed. STATE's `machine` block carries `identity`, `capabilities`, `platform` (the platform's name and words, its light, every
claim with its light, the settings, the timer made and its lateness, whether keep-awake is held as read back, the power plan, the GC's
setting, what it holds and its pauses) and `locked` (null, or the set and its drift). The context's
`machine:platform` item on a Windows node carries the build, the OS line, the tier and the drift count. INDEX's `machine` section lists
each claim lit by its answer, as feedback.

## What CI proves, and what waits for the lab

| Evidence | Where |
|---|---|
| The policy, placement (ONE-CACHE on a dual-CCD topology among it), the edition and servicing rows, the GPU policy, the drift, the verbs, STATE and preflight, against fakes | both jobs (`PlatformPolicyTests`, `CpuSetTests`, `IdentityTests`, `EditionRowTests`, `PreflightMachineRowTests`, `MachineVerbTests`, `PreflightMachineTests`, `MachineStateFixtureTest`) |
| Every saved setting reaches the platform before the machine part acts on it; STATE says the timer made, the hold read back and the power plan; a refused hold is an amber claim; off Windows nothing is claimed, held or recorded for a thread | both jobs (`MachineSettingsReachTests`, `PreflightMachineTests`), the machine part composed over a fake Windows platform |
| The parsers that feed hard rows (firewall rules, the PE certificate table, the screen saver's two readings) and the capability probes' decisions (DXGI's and NVAPI's words, HAGS per adapter, timer requests honoured) | both jobs (`WindowsTextTests`, `CapabilityTests`) |
| A listener fed by a capture thread claims on it, and again when the thread changes | both jobs (`DeliveryClaimTests`) |
| The machine's lines through the assistant's gate: refused while armed by the Setup class, REALTIME waiting for a person, the result read back from STATE | both jobs (`AssistantMachineTests`) |
| The probe structures' sizes and offsets, the vtable slots | both jobs (`PlatformLayoutTests`) |
| Every deadline thread waits on the timer it made: the LED sender's and the audio feeder's on a hand-moved clock, each wait's instant and answer seen; a node's LED sender, audio feeder and headless pacer make theirs through the platform | both jobs (`LedSenderTimerTests`, `AudioFeederTimerTests`, `ThreadTimerTests`, `DeadlineThreadTests`) |
| The show doors and their addresses (the network host's and the control inputs'), and the networks' row reading only the networks they are on | both jobs (`ShowDoorTests`, `MachineDoorTests`, `NetworkRowTests`); each network's adapters read on Windows (`PlatformCallsTests`) |
| Through the contract, the composed platform answers what the platform underneath reads back | both jobs (`ReadBackContractTests`, `MachineRecordTests`) |
| Every claim answers on Windows with its named words; read-backs; MMCSS taken and reverted (Windows' answer to the revert kept); a second claim on a thread joins the first; every MACHINE SET word handed to one platform reaches its next hold, timer and thread claim and its facts; the two waits measured | the Windows job (`PlatformCallsTests`), its answers in the summary |
| L10: p99.9 frame time with and without each claim on NVIDIA, AMD and Intel nodes, a hybrid Intel CPU, a dual-CCD X3D CPU | **not run** (no lab this round): `docs/round3/lab/L10-platform-claims.md` |
| L22: a precise Windows thread time | **not run**: `docs/round3/lab/L22-thread-time.md` |
| L23: the edition's rows and the claims on a clean Home node | **not run**: `docs/round3/lab/L23-home-node.md` |

No hardware fact here was measured on show hardware: the defaults with no documented cause stay off, and every number above is the
shared runner's.

## Next round

Core reservation, large pages for frame pools, a pre-roll measured against NativeAOT (M13); each media volume's encryption as a
preflight fact (BitLocker's state is read through WMI or the shell's property store, neither of which the platform declares yet).
