# Design: `wp3-installer` — the MSI, the supervisor and the version an installer can order

*How PlayMap will be installed once per machine, so the engine never needs to run as administrator; how a small service will keep
the engine running in the show user's session and stop one whose picture has stopped; how every build will carry a version an MSI
can order; and what a show machine is told about its own setup. Package section: `PLAN.md` §3.5. Today CI publishes a
self-contained folder, with no installer, no signing, no firewall rules and no dump settings, and the version is `0.1.0` on `main`
[map §6.2–§6.3].*

**The decision in one paragraph.** One per-machine MSI, built by the WiX toolset compiled in CI from its MS-RL source at a pinned
release tag (no WiX binary, package or paid service is used; `PLAN.md` D6, M17), installs to Program Files on every edition of Windows
11 and does every machine-level action once: firewall rules, WER's dump settings, two privileges for the show account, the event
source, the supervisor service. Builds are unsigned this round and say so, with a SHA-256 list beside each. The
supervisor (LocalSystem) starts `PlayMap.exe engine` in the auto-logged-on show user's session — never session 0, where display
devices are out of reach — restarts it with backoff, and stops an engine whose shown-frame count has stopped while HEALTH says it should be moving — never
during a start or a device rebuild within its measured budget.
The MSI's `ProductVersion` is MAJOR.MINOR.BUILD from CI's build number, so every numbered build upgrades the one before it;
`main`'s file version and SemVer stay as they are.

---

## 1. The version rules

The base is `main`'s scheme (`PLAN.md` §1.3): `Version.props` holds `PlayMapVersion` (MAJOR.MINOR.PATCH) and `PlayMapRunBase`;
CI passes `PlayMapRun` (its run number) and `PlayMapCommit`, and the build number (`_PlayMapBuildNumber`, 1–65 535) is the run
less the base; the file version is MAJOR.MINOR.PATCH.BUILD; the informational version is `MAJOR.MINOR.PATCH+BUILD.SHORTSHA`;
`BuildInfo` reads it back. This package adds:

```xml
<!-- Version.props (the installer's hunk) -->
<Error Condition="$(_PlayMapVersionWellFormed) and ($([System.Version]::Parse('$(PlayMapVersion)').Major) &gt; 255 or $([System.Version]::Parse('$(PlayMapVersion)').Minor) &gt; 255)"
       Text="PlayMapVersion '$(PlayMapVersion)' has a major or minor above 255, the most an MSI's ProductVersion holds: no build may carry a version a release cannot install." />
<PropertyGroup Condition="'$(PlayMapRun)' != ''">
  <PlayMapMsiVersion>$([System.Version]::Parse('$(PlayMapVersion)').Major).$([System.Version]::Parse('$(PlayMapVersion)').Minor).$(_PlayMapBuildNumber)</PlayMapMsiVersion>
</PropertyGroup>
```

```xml
<!-- installer/PlayMap.Installer.wixproj: outside PlayMap.sln (an MSI builds on Windows only), built by the Windows job's MSI step -->
<Error Condition="'$(PlayMapMsiVersion)' == ''"
       Text="An MSI is made from a numbered build only (PlayMapRun and PlayMapCommit from CI): a local build's MSI would read as MAJOR.MINOR.0 and could not be upgraded in order." />
```

```csharp
// src/PlayMap.Core/BuildInfo.cs (the installer's hunk)
/// <summary>The MSI's ProductVersion: MAJOR.MINOR.BUILD; "" for a local build, which makes no MSI.</summary>
public string MsiVersion => IsLocal
    ? ""
    : System.Version.Parse(Version) is var v ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Build}") : "";
```

| Build | SemVer (people, HEALTH, the log) | File version | MSI |
|---|---|---|---|
| 0.2.0, build 17 | `0.2.0+17.abc1234` | `0.2.0.17` | `0.2.17` |
| 0.2.3, build 18 | `0.2.3+18.def5678` | `0.2.3.18` | `0.2.18` (upgrades 0.2.17) |
| 0.3.0, build 19 | `0.3.0+19.0123abc` | `0.3.0.19` | `0.3.19` |
| local | `0.2.0-dev+abc1234` | `0.2.0.0` | none (refused) |

The release job reads the last release's MSI version (`gh release view`) and refuses an MSI that is not greater, naming both; a new
workflow file starts run numbers at 1 and a raised `PlayMapRunBase` starts build numbers at 1, so the commit that does either
raises MAJOR or MINOR, never PATCH alone. Add/Remove Programs shows `ProductVersion`; the
product's name carries the SemVer ("PlayMap 0.2.3 (build 18)"), and `docs/INSTALL.md` explains the two numbers.

## 1a. The toolset, built from its source

The maintenance fee's EULA binds WiX's binary releases only and says a user may compile binaries from the source without it; WiX v5,
the last fee-free binary line, has had no community security fixes since February 2026 [`research/installer_tools.md` §2]. So:

- A `wix-toolset` job in the installer's CI block checks out `wixtoolset/wix` at a pinned v7 release tag, builds the SDK and the Util
  and Firewall extensions with the runner's Visual Studio 2026 and .NET 10 SDK, and caches the packages by the tag's commit, with a
  version suffix of PlayMap's own so they are never mistaken for nuget.org's.
- `nuget.config` (new at the root) maps `WixToolset.*` to that local feed only: a restore can never fetch an official WiX package.
  No WiX GitHub release, NuGet package or `wix` .NET tool is downloaded; no HeatWave, Developer Direct or fee; no EULA property; no
  WiX issue is filed.
- The first day is M17's time-box: the tag built, a test MSI with a `ServiceInstall`, a `MajorUpgrade` and a firewall rule installed,
  upgraded and uninstalled quietly on the runner, the build's minutes and the cache's size recorded, and whether the self-built tool
  still asks for the EULA gesture (if it does, the owner decides, `PLAN.md` §9 decision 3).
- If the toolset does not build within the day, Inno Setup 7.1 (its licence allows any use; a commercial user is asked, not required,
  to buy a licence) builds a 64-bit installer instead: every machine-level action of §3 through the supervisor's own commands
  (`install`, `uninstall`, `grant`, `revoke`, `preinstall-check`), the downgrade and armed checks in its `InitializeSetup`, §1's
  version rules restated for an EXE (ordered by the four-part file version). It loses MSI's transactional rollback of service and
  registry changes, repair and `msiexec`.
- `THIRD-PARTY-NOTICES.md` names WiX as a build tool built from its source at the tag, and its Util and Firewall custom-action DLLs,
  which sit inside the MSI, as MS-RL files with a link to that tag's source.

## 2. What the MSI lays down

| Where | What |
|---|---|
| `%ProgramFiles%\PlayMap\` | the self-contained publish with ReadyToRun (`PublishReadyToRun`: the engine's first frames run precompiled code, not the JIT's first tier — M13's default with the pre-roll; the first ten minutes' p99.9 frame time recorded in L21) (`PlayMap.exe` and its assemblies, .NET 10 included); ANGLE's `av_libglesv2.dll` (the desk's Avalonia and the D3D11 path); OpenAL Soft; libvmx; the shader worker's natives (DXC's `dxcompiler.dll` and `dxil.dll`, glslang, SPIRV-Tools, SPIRV-Cross) |
| `…\ffmpeg\` | FFmpeg 9.0.2's LGPL shared libraries, as replaceable files (today the folder carries none and the node looks for them beside the exe [map §6.2]) — the same hash-pinned `lgpl-shared` build both CI jobs load for every decode test from this package's first item on; the GPL command-line build stays in CI only to encode test media, on the path and never beside a test host |
| `…\D3D12\` | the Agility SDK's `D3D12Core.dll` (its debug layer is left out of releases) |
| `…\Supervisor\PlayMap.Supervisor.exe` | the service |
| `%ProgramData%\PlayMap\` | `dumps\` (WER), `supervisor\` (its status file and log); the show account may write both |
| `HKLM\SOFTWARE\PlayMap` | `InstallDir`, `Version`, `ShowUser`, `Supervised` (read by the desk and by `machine.install`) |

Per user, as today: the node's home (`%LOCALAPPDATA%\PlayMap`: settings, journal, caches, the pipeline cache) in the show user's
profile.

**Upgrade and launch.** A fixed `UpgradeCode`; `MajorUpgrade` scheduled after `InstallInitialize`, no same-version upgrade, a
downgrade refused with "PlayMap 0.2.17 is newer than this installer's 0.2.12: uninstall it first to go back". A launch condition
from a registry search of `CurrentBuildNumber` (`VersionNT` does not tell 24H2 from older builds): "PlayMap needs Windows 11 24H2
(build 26100) or later; this machine runs build 22631". A second from Smart App Control's state
(`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState`): 1, enforcing, while the build is unsigned →
"Smart App Control blocks unsigned programs and this build is unsigned: turn it off in Windows Security ▸ App & browser control, or
wait for a signed build" — before any file is laid, since the installed exe, its DLLs and the supervisor would all be blocked. x64
only, on every edition.

## 3. Machine-level actions

| Action | How | Undone on uninstall |
|---|---|---|
| Firewall rules for `PlayMap.exe` | WiX's firewall extension (built from source, §1a): TCP 9940 (web, MCP), 9941 (line wire); UDP 9928 (hello), 9942 (clock, PMVB), 9943 (OSC), 6454 (Art-Net), 5568 (sACN), 5353 (mDNS); private and domain profiles, the Public profile a property the person sets (Windows puts a new network on Public); the scope a property, the local subnet by default | yes |
| No URL ACL | Kestrel binds its sockets itself; `InstallerTests` start the node as a standard user and bind 9940 on every interface | — |
| mDNS | checked on the runner: `_playmap._tcp` browsable beside Windows' own responder on 5353, the answer in the summary; if the two collide, the node registers through `DnsServiceRegister` behind the platform seam (the package's stretch) | — |
| WER LocalDumps | `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe`: `DumpFolder` `%ProgramData%\PlayMap\dumps`, `DumpType` 2 (full), `DumpCount` 5 | yes |
| Privileges for the show account | `SeIncreaseBasePriorityPrivilege` (a realtime GPU queue, used only when measured) by the supervisor's `grant <account>` (`LsaAddAccountRights`) as a deferred custom action; nothing else | yes (`revoke`) |
| Event source | `PlayMap` in the Application log | yes |
| The service | `PlayMapSupervisor`, LocalSystem, automatic; recovery: restart after 5 s, restart after 30 s, then none, reset after a day, also on non-crash failures | yes |

The installer neither creates the show account nor stores its password; `docs/INSTALL.md` says how (§7).

## 4. The armed check

Before `InstallValidate` (immediate, so nothing is replaced yet), the MSI runs `PlayMap.Supervisor.exe preinstall-check`. It fails
closed:

1. It looks for a live engine: the supervisor's pid file (`%ProgramData%\PlayMap\supervisor\engine.pid`, with the port the engine
   listens on, written by the engine at start) and any running process whose image is the installed `PlayMap.exe` with the
   `engine` argument.
2. **No engine process** → the install goes on.
3. An engine that answers on loopback (`SHOW` on its line wire, at the port the pid file names) → armed: the check sets
   `PLAYMAP_ARMED_WORDS` and a conditional error action stops the install or the uninstall with the node's words ("the show is armed
   on this node since 20:31 by Desk: disarm first"); disarmed: the install goes on (below).
4. **An engine process that does not answer** within 5 s (wedged on its control lane, or listening elsewhere) → refused with words
   ("an engine is running here (pid 4120) but does not answer: stop it from the desk or with `PlayMap.Supervisor.exe stop`, then
   install again"). Silence is never read as "not armed". On a disarmed
upgrade, the supervisor stops the engine through its normal stop (§5) before files are replaced, and starts the new one after.

## 5. The supervisor

```csharp
namespace PlayMap.Supervisor;

public sealed record SupervisorPolicy(TimeSpan FirstBackoff, TimeSpan MaxBackoff, TimeSpan HeartbeatStale, TimeSpan HealthSilent,
    int MaxRestarts, TimeSpan RestartWindow, TimeSpan StopGrace)
{
    public static readonly SupervisorPolicy Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), 5, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
}

public enum SupervisorState { WaitingForSession, Starting, Running, Stopping, Backoff, StoppedTooManyRestarts, Updating }

/// <summary>Pure: given the time and what it observed, what the supervisor does next. Tested on a manual clock.</summary>
public sealed class SupervisorCore
{
    public SupervisorCore(SupervisorPolicy policy);
    public SupervisorState State { get; }
    public SupervisorAction Step(long nowNs, in SupervisorFacts facts);
}

/// <summary>What the supervisor saw this step. <c>ShownFrames</c> −1 is unknown (an engine from before wp3-stability fills it);
/// <c>GpuPhase</c> and its start come from HEALTH's <c>gpu</c> record, which the spine reserves.</summary>
public readonly record struct SupervisorFacts(bool ShowSessionActive, bool EngineAlive, bool HealthAnswered, long ShownFrames,
    bool OutputsOpen, ObserverStatus Observer, bool StopRequested, GpuPhase GpuPhase, long GpuPhaseSinceNs, long GpuEpoch);

public enum GpuPhase { Unknown, Starting, Running, Rebuilding }

public enum SupervisorAction { None, Start, RequestStop, Terminate, WriteStatus, WriteDumpManifest }
```

| Concern | Rule |
|---|---|
| Where the engine runs | the active console session (`WTSGetActiveConsoleSessionId`) when its user is `ShowUser`; `WTSQueryUserToken`, `CreateEnvironmentBlock`, `CreateProcessAsUser` on `winsta0\default` (M9; a scheduled task at the show user's logon is the fallback, the service then only watching) |
| Restart | after an exit: 1 s, doubling to 30 s; five restarts in ten minutes → `StoppedTooManyRestarts`, said in the status file, cleared by `PlayMap.Supervisor.exe resume` or the service's restart |
| The heartbeat | every 500 ms: HEALTH over loopback (each output's `ShownFrames`, wp3-stability) and, as a second path, the observer's status file |
| Stopping a frozen engine | only while HEALTH's `gpu.phase` is `running`: open outputs whose shown-frame count has not moved for the stale time, or HEALTH silent for 10 s with no shown frames → set the engine's stop event (`Global\PlayMap-Engine-Stop`, its ACL the show user's), wait 5 s for the normal stop (doors closed in reverse, the recovery record written), then terminate; the cause in the supervisor's log and the next engine's journal |
| The stale time | the larger of 3 s (180 refreshes at 60 Hz, past any single held frame the degrade order allows) and four times the first-good-frame time `DeviceLossRehearsalTests` record on the Windows job (the multiple L9 allows a real TDR); the longest shown-frame gap outside a rebuild in L11's and L14's runs is recorded beside it, and a gap that comes close changes the floor in one commit citing the record |
| A start or a rebuild | `starting` or `rebuilding` is exempt up to the same four-times budget from its `since`; past it the engine is treated as frozen, with words naming the phase; a new epoch resets the count |
| An unknown count | `ShownFrames` −1 (an engine without wp3-stability's heartbeat): the freeze rule is inactive and the status file says so ("the engine reports no shown frames: the freeze rule waits for them"); restarts on exit still apply |
| What it never does | stop an engine for a slow HEALTH alone while frames move; run as the engine's parent in a way that ends the engine when the service ends (a supervisor restart re-adopts the running engine by its pid file) |
| Dumps *(stretch)* | a watcher on `dumps\`: beside each new dump a manifest — build, commit, engine pid, exit code, the journal's last sequence, when |
| Its status | `%ProgramData%\PlayMap\supervisor\status.json` (replaced atomically: state, engine pid, restarts, last cause, when), read by the engine into `machine.install` and by the desk |
| Its pipe | `\\.\pipe\PlayMap.Supervisor` (the show user and administrators): `status` and `resume` only this round |

**The desk.** `Link/EngineSupervisor.cs` reads `HKLM\SOFTWARE\PlayMap\Supervised`: on a supervised machine the desk attaches to the
local engine over loopback and never starts its own; while the engine is being started it shows the supervisor's state from the
status file.

## 6. Unsigned builds

The owner's answer: "Leave unsigned for now" (`PLAN.md` §9 decision 2). Every build and release is unsigned and named so
(`PlayMap-0.2.17-win-x64-unsigned.msi`), with a named notice in the job's summary ("unsigned: the owner's decision — PLAN.md §9
decision 2"), on `main` as on a branch.

- **What a person sees.** A downloaded installer carries the Mark of the Web: SmartScreen shows "Windows protected your PC" with an
  unknown publisher, and More info ▸ Run anyway lets it run; an unsigned build has no reputation to carry from one release to the
  next. UAC names the publisher "Unknown". Smart App Control, on by default on clean Windows 11 installs, Home included, blocks unsigned
  code with no per-app exception, so the launch condition (§2) refuses while it enforces. A copy from a share or a USB stick usually
  carries no mark [`research/installer_tools.md` §4].
- **What still verifies a build.** A `SHA256SUMS` file of the installer and every shipped file beside each artifact and release;
  `docs/INSTALL.md` shows `Get-FileHash` against it. That is the only integrity check this round: there is no updater, so nothing
  verifies an update automatically.
- **Next round.** An identity (Azure Artifact Signing, or another the owner chooses) signs `PlayMap.exe`, PlayMap's own assemblies,
  `PlayMap.Supervisor.exe`, the shipped third-party natives (Smart App Control checks every DLL as it loads) and the installer,
  timestamped, each checked by `Get-AuthenticodeSignature`; the updater verifies a build against a hash from a channel it trusts.

## 7. The show machine (`docs/INSTALL.md`)

- **The OS, in three tiers** (`PLAN.md` D4). Windows 11 IoT Enterprise LTSC 2024 for permanent show nodes (build 26100, supported to
  10 Oct 2034, no feature updates, Shell Launcher and specialized displays available); Pro or Enterprise 25H2 or later for desks and
  for nodes run with update policies; Home 25H2 or later, supported and unmanaged — no kiosk, no specialized displays, no update
  deferral, at most 128 GB of memory and one socket [`research/windows11_home.md` §1]. S mode (Home only) runs no installer: switch
  out of it first. Windows 10 is not supported.
- **The show account.** A standard user; auto-logon through Sysinternals Autologon, which keeps the password as an LSA secret —
  never `DefaultPassword` in plain text in the registry; `ShowUser` given to the MSI. On Home, whose setup asks for a Microsoft
  account (and a new one has no password), a local standard "show" account is made after setup, with a password, and auto-logon is
  set to it.
- **Shell Launcher** where the edition has it (IoT Enterprise, Enterprise, Education: `Dism /online /Enable-Feature /all
  /FeatureName:Client-EmbeddedShellLauncher`) with the engine as the show account's shell, for a node with no desk; the supervisor
  still watches it. On Home and Pro the supervisor starts the engine in the show account's session with Explorer underneath, and the
  desk's monitor (or a small admin display) stays the primary display, since notifications, UAC and setup screens open there.
- **Updates.** On the editions with Update client policies: feature updates deferred 365 days, quality updates 30, no automatic
  restart with a user logged on, a maintenance window. On Home, which honours none of those policies: pause updates past the show
  (35 days at a time, extendable), set active hours, restart without updating from the power menu, and choose the maintenance window
  by hand. A pending reboot is a preflight fact (wp3-platform). The stretch's opt-in feature writes what the edition honours once and
  reads it back; the guide says how to set the rest by hand.
- **Security defaults.** Smart App Control off for an unsigned build; Memory Integrity left as it is (an incompatible capture or sync
  driver will not load under it, and the node says so); the device-encryption recovery key kept where it can be found; media outside
  OneDrive folders, whose files can be online-only placeholders; the show network set to Private, or the firewall rules given Public.
- **Firmware and power.** Power on after power loss in the firmware; the High or Ultimate Performance plan where offered, else
  Balanced with the best-performance mode (Modern Standby machines offer Balanced only); USB selective suspend and PCIe link-state
  power management off; the display never sleeping; the screen saver off. Each is a preflight fact.
- **Drivers.** The GPU driver and the sync board's firmware pinned for a show (`MACHINE LOCK`), updated only in a window; on Home,
  where Windows Update installs drivers with the monthly update, `MACHINE LOCK` observes and names a changed driver.

## 8. Facts in STATE and preflight

```json
"machine": { "install": { "installed": true, "msi": "0.2.17", "dir": "C:\\Program Files\\PlayMap", "supervised": true,
  "supervisor": { "state": "running", "restarts": 0, "lastCause": "", "at": 1760600000000 },
  "autoLogon": { "on": true, "user": "show" }, "policies": { "updatesDeferred": true }, "signed": false } }
```

Preflight's `install` section: a folder run on a node that looks like a show node (amber: "not installed: nothing restarts the
engine if it stops"); the supervisor stopped after too many restarts (red, fix `PlayMap.Supervisor.exe resume` in words); auto-logon
off on a supervised node (amber); updates not deferred (amber, on the editions that can defer them; on Home an information row, "this
edition cannot defer updates"); the build unsigned (grey, **i**).

## 9. Tests

| Test | Job | Holds |
|---|---|---|
| `MsiVersionTests` | both | §1's table; a major or minor of 256 refused; build 0 refused; a lower version than the last release refused with "raise MINOR" |
| `PreinstallCheckTests` | both (the decision pure; on Windows against fake engines) | no engine process → go on; an engine answering armed → refused with its words; disarmed → go on; **an engine process alive but silent → refused**, naming its pid; a pid file whose process is gone → go on |
| `VersionPropsTests` | both | a build of 256.0.0 fails with the words; a numbered build sets `PlayMapMsiVersion` |
| `WixSourceTests` | Windows | the restored `WixToolset.Sdk` and extensions come from the local feed, their hashes the cached build's; no other source serves `WixToolset.*` |
| `InstallerTests` | Windows (elevated) | the MSI builds (from `installer/PlayMap.Installer.wixproj`, outside `PlayMap.sln`); with Smart App Control's state set to enforcing (a test property), the launch condition refuses with its words; the FFmpeg it carries hashes to CI's LGPL pin; a quiet install lays the files, the service with its recovery actions, the firewall rules, the WER values, the registry key; the previous build's MSI (built in the job with a lower build number) upgrades to this one and keeps the node's settings; a downgrade refused with words; a low build refused by the launch condition (a test property); an uninstall removes all but the node's data; with a fake engine on loopback answering armed, the install stops with its words; with a fake engine process that never answers, it stops with "does not answer"; a standard user binds 9940 |
| `SupervisorTests` | both | `SupervisorCore` on a manual clock: the backoff, the frozen-engine rule (and not on a slow HEALTH while frames move), the rule inactive while `ShownFrames` is −1, `starting` and `rebuilding` exempt within the budget and frozen past it, the five-in-ten-minutes stop, re-adoption, the dump manifest (with its stretch); on Windows the service installs, starts and stops |
| `SessionStartProbe` | Windows (a probe, not a gate) | M9's calls on the runner, whose job runs in an interactive session: `WTSGetActiveConsoleSessionId`, `WTSQueryUserToken` and `CreateProcessAsUser` of a child that reports its session id; the answer in the job's summary, L11 the proof |
| `SupervisorHeartbeatTests` | both (at the merge with stability) | a fake engine on loopback whose shown frames stop → the stop event, then termination after the grace |
| `DeskAttachTests` | both | a supervised machine's desk attaches and never starts an engine (in `tests/PlayMap.App.Tests/Install/`, this package's area) |
| `UnsignedArtifactTests` | Windows | the step's notice, the `-unsigned` name, and `SHA256SUMS` matching every file it lists |

## 10. Risks

- **Auto-logon and a LocalSystem service** widen the attack surface (`PLAN.md` R11): a standard show account, its password as an LSA
  secret, the pipe's ACL, two named privileges for one named account, the engine never elevated.
- **Launching into a session** (M9) depends on the session being there: the supervisor waits for it, says so, and the scheduled task
  is the measured fallback.
- **FFmpeg inside the MSI** must be the LGPL build (`PLAN.md` §9 decision 4): both CI jobs load the same pinned `lgpl-shared` build
  for every decode test, so the tests run on the libraries the MSI carries, and `LicenceTests` check the MSI's copy against that
  hash.
- **WiX from its source** may not build on the runner within its day, or may still ask for the fee's EULA gesture (M17): Inno Setup
  is the measured fallback, the gesture the owner's call (`PLAN.md` §9 decision 3).
- **Unsigned builds** meet SmartScreen's prompt on every download and Smart App Control's block where it enforces (`PLAN.md` R23):
  the launch condition says so before anything is laid, and `docs/INSTALL.md` gives the steps.
