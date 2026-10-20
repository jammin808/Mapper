# Design: `wp3-installer` — the installer, the supervisor and the version an installer can order

*How PlayMap will be installed once per machine, so the engine never needs to run as administrator; how a small service will keep
the engine running in the show user's session and stop one whose picture has stopped; how every build will carry a version an
installer can order; and what a show machine is told about its own setup. Package section: `PLAN.md` §3.5. Today CI publishes a
self-contained folder, with no installer, no signing, no firewall rules and no dump settings, and the version is `0.1.0` on `main`
[map §6.2–§6.3].*

**The decision in one paragraph.** This round one per-machine installer built by Inno Setup 7.1 (`PLAN.md` D6 and §9 decision 3:
the owner's choice of 19 October 2026, "Keep Inno Setup") installs to Program Files on every edition of Windows 11 and does every
machine-level action once, through the supervisor's own commands: firewall rules, WER's dump settings, the show account's
base-priority privilege, the event source, the supervisor service. It is an EXE, not an MSI: it has no automatic undo of service and
registry changes, no repair and no `msiexec` for IT, so it orders its own steps to stand in for the undo. The MSI this file designs
(§1a–§4, built by the WiX toolset compiled from its MS-RL source at a pinned tag, with no WiX binary, package or paid service) is
kept for the next round, when an MSI is revisited; everything else here holds for this round's installer as written, restated for
an EXE. M17 gave the answer: WiX v7.0.0's pinned source does not build on `windows-2025` — it needs Visual Studio's v143 C++ tools,
the runner's Visual Studio 2026 has v145 only, and adding v143 through Visual Studio's installer failed — and the source checks its
EULA gesture in three places (`wix.exe`'s command line, the SDK target's WIX7015, the `CheckLicenseAcceptance.targets` every
extension package carries), which PlayMap never passes; no patch to the source was made, and the probe that measured it is retired
(§1a). The installer carries the Agility SDK's `D3D12Core.dll` and shows a licence page that carries Microsoft's own licence for it
verbatim, answering the SDK's requirement that distributors and external end users "agree to terms that protect it and Microsoft at
least as much as this agreement"; PlayMap's own terms are the owner's to name, and none are named yet. The owner accepts Microsoft's
distribution terms, including indemnifying Microsoft (`PLAN.md` §9 decision 23). Builds are
unsigned this round and say so, with a SHA-256 list beside each. The supervisor (LocalSystem) starts `PlayMap.exe engine
--supervised` (the one run that writes the machine's pid record) in the auto-logged-on show user's session — never session 0, where
display devices are out of reach — restarts it with backoff, and stops an engine whose shown-frame count has stopped while HEALTH
says it should be moving — never during a start or a device rebuild within its measured budget. The installer's version is
MAJOR.MINOR.BUILD from CI's build number (an MSI's `ProductVersion` next round), so every numbered build upgrades the one before it;
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
the last fee-free binary line, has had no community security fixes since February 2026 [`research/installer_tools.md` §2]. So the
MSI this file designs is built by the WiX toolset compiled from its source, and next round's MSI brings back what it needs:

- A `wix-toolset` job in the installer's CI block that checks out `wixtoolset/wix` at a pinned release tag, builds the SDK and the
  Util and Firewall extensions with the runner's Visual Studio and .NET SDK, and caches the packages by the tag's commit, with a
  version suffix of PlayMap's own so they are never mistaken for nuget.org's.
- A `nuget.config` at the root that maps `WixToolset.*` to that local feed only, so a restore can never fetch an official WiX
  package. No WiX GitHub release, NuGet package or `wix` .NET tool is downloaded; no HeatWave, Developer Direct or fee; no EULA
  property; no WiX issue is filed.
- M17 measured that toolset this round: the tag built, then a test MSI with a `ServiceInstall`, a `MajorUpgrade` and a firewall rule
  installed, upgraded and uninstalled quietly on the runner, the build's minutes and the cache's size recorded. Its answer: the
  pinned v7.0.0 source does not build on `windows-2025`. Its native parts need Visual Studio's v143 C++ tools, the runner's Visual
  Studio 2026 has v145 only, and adding v143 through Visual Studio's installer failed, so the build stops at MSB8020 before any
  licence check runs. The source checks the EULA gesture in three places — `wix.exe`'s command line, the SDK target's WIX7015, and
  the `CheckLicenseAcceptance.targets` every extension package carries — and WiX's own build sets `AcceptEula` for itself. PlayMap
  accepts no WiX agreement and never passes the gesture, and no patch to the pinned source was made: a session's permission policy
  refused it (`PLAN.md` §9 decision 3). The probe is retired with that answer: this round there is no `wix-toolset` job,
  `nuget.config`, `installer/wix/` or `WixSourceTests`, and nothing of WiX is built or shipped.
- So Inno Setup 7.1 (its licence allows any use; a commercial user above US$5 000 revenue is asked, not required, to buy a licence —
  a request the owner may meet, and nothing in the build depends on it, `PLAN.md` §9 decision 3) builds this round's 64-bit
  installer, the owner's choice of 19 October 2026: every machine-level action of §3 through the supervisor's own commands
  (`install`, `uninstall`, `grant`, `revoke`, `preinstall-check`), the downgrade and armed checks in its `InitializeSetup`, §1's
  version rules restated for an EXE (its version the same MAJOR.MINOR.BUILD §1 gives an MSI). It loses MSI's transactional
  rollback of service and registry changes, repair and `msiexec`; an MSI is revisited next round.
- `THIRD-PARTY-NOTICES.md` names nothing of WiX this round; once an MSI ships, its Util and Firewall custom-action DLLs, which sit
  inside the MSI, are named as MS-RL files with a link to their tag's source.

## 2. What the MSI lays down

| Where | What |
|---|---|
| `%ProgramFiles%\PlayMap\` | the self-contained publish with ReadyToRun (`PublishReadyToRun`: the engine's first frames run precompiled code, not the JIT's first tier — M13's default with the pre-roll; the first ten minutes' p99.9 frame time recorded in L21) (`PlayMap.exe` and its assemblies, .NET 10 included); ANGLE's `av_libglesv2.dll` (the desk's Avalonia and the D3D11 path); OpenAL Soft; libvmx; the shader worker's natives (DXC's `dxcompiler.dll` alone, from Microsoft.Direct3D.DXC under its LLVM licence: its built-in validator signs DXIL, so no `dxil.dll` is laid down, `PLAN.md` §9 decision 24; glslang, SPIRV-Tools, SPIRV-Cross) |
| `…\ffmpeg\` | FFmpeg 9.0.2's LGPL shared libraries, as replaceable files (today the folder carries none and the node looks for them beside the exe [map §6.2]) — the same hash-pinned `lgpl-shared` build both CI jobs load for every decode test from this package's first item on; the GPL command-line build stays in CI only to encode test media, on the path and never beside a test host |
| `…\D3D12\` | the Agility SDK's `D3D12Core.dll` (its debug layer is left out of releases) |
| `…\Supervisor\PlayMap.Supervisor.exe` | the service |
| `%ProgramData%\PlayMap\` | `dumps\` (WER), `supervisor\` (its status file and log); the show account may write both |
| `HKLM\SOFTWARE\PlayMap` | `InstallDir`, `Version`, `ShowUser`, `Supervised` (read by the desk and by `machine.install`) |

Per user, as today: the node's home (`%LOCALAPPDATA%\PlayMap`: settings, journal, caches, the pipeline cache) in the show user's
profile.

**The licence page.** `D3D12Core.dll` is the Agility SDK's distributable code, which its licence lets PlayMap ship on its
Distribution Requirements, among them to "require distributors and external end users to agree to terms that protect it and
Microsoft at least as much as this agreement" ("it" is the distributable code) and to "indemnify, defend, and hold harmless
Microsoft from any claims, including attorneys’ fees, related to the distribution or use of your applications, except to the extent
that any claim is based solely on the unmodified distributable code", with its Distribution Restrictions beside them (`PLAN.md` §9
decision 23 quotes them whole). So the installer shows a licence page, accepted before anything is laid down, that carries
Microsoft's licence for `D3D12Core.dll` verbatim as the terms a person agrees to for it, with a section for PlayMap's own terms
that states none until the owner names them. A quiet install shows no page and lays nothing down unless its command line carries
`/ACCEPTTERMS=yes`. The owner accepts Microsoft's distribution terms, the indemnity included. No third party's terms but
`D3D12Core.dll`'s are on the page, beside the section for PlayMap's own: DXC's `dxcompiler.dll` asks for no agreement, its LLVM
Release License (University of Illinois/NCSA) asking that its copyright notice, its three conditions and its disclaimer travel with
the binary, which they do in `licenses\DXC\` beside it (the package's `LICENSE-LLVM.txt`, with DXC's own `LICENSE.TXT` and
`ThirdPartyNotices.txt` for the third-party code inside it, laid down by every build and publish) and in `THIRD-PARTY-NOTICES.md`,
which reproduces the licence whole; no `dxil.dll` is laid down (§9 decision 24). `THIRD-PARTY-NOTICES.md` names the runtime and its
terms.

**Upgrade and launch.** A fixed `UpgradeCode`; `MajorUpgrade` scheduled after `InstallInitialize`, no same-version upgrade, a
downgrade refused with "PlayMap 0.2.17 is newer than this installer's 0.2.12: uninstall it first to go back". A launch condition
from a registry search of `CurrentBuildNumber` (`VersionNT` does not tell 24H2 from older builds): "PlayMap needs Windows 11 24H2
(build 26100) or later; this machine runs build 22631". No launch condition reads Smart App Control: once it enforces
(`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState` 1) it blocks the unsigned MSI itself before
Windows Installer evaluates a condition, and the installed exe, its DLLs and the supervisor with it [`research/installer_tools.md` §4],
so no word of PlayMap's is ever shown then — the release page and `docs/INSTALL.md` carry them, before the download. While it is still
evaluating (2) the MSI installs, and its UI shows one warning page — "Smart App Control is evaluating this PC: once it enforces, Windows
blocks this unsigned build and the node will not start. Turn it off in Windows Security ▸ App & browser control before the show" —
which a quiet install writes to its log. x64 only, on every edition.

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
  unknown publisher, and More info ▸ Run anyway lets it run; an unsigned build has no reputation to carry from one release to the next.
  UAC names the publisher "Unknown". Smart App Control, on by default on clean Windows 11 installs, Home included, blocks unsigned code
  with no per-app exception once it enforces: the installer, `PlayMap.exe` and the supervisor, before anything of PlayMap runs, so the
  person sees only Windows' own block (logged as event 3077 in `Microsoft-Windows-CodeIntegrity/Operational`), and a desk on another
  machine shows the node as not answering in the fleet's board. The release page and `docs/INSTALL.md` say this before the download,
  with the steps; while it evaluates, the MSI's warning page (§2) and preflight's amber row say it on the machine. A copy from a share
  or a USB stick usually carries no mark [`research/installer_tools.md` §4].
- **What still verifies a build.** A `SHA256SUMS` file of the installer and every shipped file beside each artifact and release;
  `docs/INSTALL.md` shows `Get-FileHash` against it. That is the only check this round, and it shows a download is intact, not who built
  it: the sums sit on the same page as the files, so whoever could change one could change the other, and `docs/INSTALL.md` says so.
  There is no updater, so nothing verifies an update automatically. GitHub's build-provenance attestations, which would show which
  workflow built a file, are a next-round option once their terms for this repository's plan are checked (`PLAN.md` §9 decision 2).
- **Next round.** An identity (Azure Artifact Signing, or another the owner chooses) signs `PlayMap.exe`, PlayMap's own assemblies,
  `PlayMap.Supervisor.exe`, the shipped third-party natives (Smart App Control checks every DLL as it loads) and the installer,
  timestamped, each checked by `Get-AuthenticodeSignature`; the updater verifies a build against a hash from a channel it trusts.

## 7. The show machine (`docs/INSTALL.md`)

- **The OS, in three tiers** (`PLAN.md` D4). Windows 11 IoT Enterprise LTSC 2024 for permanent show nodes (build 26100, supported to
  10 Oct 2034, no feature updates, Shell Launcher and specialized displays available); Pro or Enterprise 25H2 or later for desks and
  for nodes run with update policies; Home 25H2 or later, supported and unmanaged — no kiosk, no specialized displays, no update
  deferral, at most 128 GB of memory and one socket [`research/windows11_home.md` §1]. Build 26100 is the floor every edition shares, so
  Home or Pro 24H2, out of servicing since 13 Oct 2026, installs and runs, and machine preflight's servicing row says, amber, that it
  gets no security updates (`design/platform.md` §6). S mode (Home only) runs no installer: switch out of it first. Windows 10 is not
  supported.
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
| `InstallerTermsTests` | both (the pinned package's half on Linux) | the licence page's terms hold Microsoft's licence for `D3D12Core.dll` byte for byte from the pinned Agility SDK package, its SHA-256 the pin's, and a section for PlayMap's own terms that states none; the script shows Inno Setup's own licence page with nothing that skips it or answers it; every package whose files the publish carries is named in `THIRD-PARTY-NOTICES.md` |
| `InstallerTests` | Windows (elevated) | the MSI builds (from `installer/PlayMap.Installer.wixproj`, outside `PlayMap.sln`); with Smart App Control's state set to evaluating (a test property), a quiet install completes and its log carries the warning's words; the FFmpeg it carries hashes to CI's LGPL pin; a quiet install lays the files, the service with its recovery actions, the firewall rules, the WER values, the registry key; the previous build's MSI (built in the job with a lower build number) upgrades to this one and keeps the node's settings; a downgrade refused with words; a low build refused by the launch condition (a test property); an uninstall removes all but the node's data; with a fake engine on loopback answering armed, the install stops with its words; with a fake engine process that never answers, it stops with "does not answer"; a standard user binds 9940; the licence page holds the terms before anything is laid down; a quiet install without `/ACCEPTTERMS=yes` is refused and lays nothing down |
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
- **WiX from its source** does not build on the runner, and its source checks the fee's EULA gesture in three places that PlayMap
  never passes (M17, §1a): Inno Setup builds this round's installer (`PLAN.md` §9 decision 3), without an MSI's automatic undo,
  repair or `msiexec`, and an MSI is revisited next round. No other free installer fits the node better: NSIS needs more script and
  has no automatic rollback at all, MSIX needs a signature, Velopack and Squirrel install no services, WixSharp still runs a WiX
  toolset, Visual Studio's Installer Projects build only inside the IDE, and writing the MSI's tables directly is weeks of a
  generator PlayMap would own (`research/installer_tools.md` §3).
- **The Direct3D 12 runtime's terms** bind the owner as its distributor (`PLAN.md` §9 decision 23 quotes them): among them, end
  users agree to terms that protect the distributable code and Microsoft at least as much as the Agility SDK's licence, which the
  installer's licence page answers by carrying that licence verbatim (§2), and the owner indemnifies Microsoft against claims,
  attorneys' fees included, related to the distribution or use of PlayMap, except claims based solely on the unmodified runtime. The
  owner accepts them.
- **Unsigned builds** meet SmartScreen's prompt on every download and Smart App Control's block where it enforces (`PLAN.md` R23),
  which no word of PlayMap's can reach: the release page and `docs/INSTALL.md` say so before the download and give the steps, and in
  evaluation the MSI's warning page and preflight say it on the machine.
