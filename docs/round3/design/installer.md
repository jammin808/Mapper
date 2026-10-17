# Design: `wp3-installer` — the MSI, the supervisor and the version an installer can order

*How PlayMap will be installed once per machine, so the engine never needs to run as administrator; how a small service will keep
the engine running in the show user's session and stop one whose picture has stopped; how every build will carry a version an MSI
can order; and what a show machine is told about its own setup. Package section: `PLAN.md` §3.5. Today CI publishes a
self-contained folder, with no installer, no signing, no firewall rules and no dump settings, and the version is `0.1.0` on `main`
[map §6.2–§6.3].*

**The decision in one paragraph.** One per-machine MSI built by WiX v7 installs to Program Files and does every machine-level action
once: firewall rules, WER's dump settings, two privileges for the show account, the event source, the supervisor service. The
supervisor (LocalSystem) starts `PlayMap.exe engine` in the auto-logged-on show user's session — never session 0, where display
devices are out of reach — restarts it with backoff, and stops an engine whose shown-frame count has not moved for three seconds.
The MSI's `ProductVersion` is MAJOR.MINOR.BUILD from CI's run number, so every numbered build upgrades the one before it; the
`versioning` branch's file version and SemVer stay as they are.

---

## 1. The version rules

The base is the `versioning` branch (`PLAN.md` §1.3): `Version.props` holds `PlayMapVersion` (MAJOR.MINOR.PATCH), CI passes
`PlayMapBuild` (its run number, 1–65 535) and `PlayMapCommit`; the file version is MAJOR.MINOR.PATCH.BUILD; the informational
version is `MAJOR.MINOR.PATCH+BUILD.SHORTSHA`; `BuildInfo` reads it back. This package adds:

```xml
<!-- Version.props (the installer's hunk) -->
<Error Condition="$(_PlayMapVersionWellFormed) and ($([System.Version]::Parse('$(PlayMapVersion)').Major) &gt; 255 or $([System.Version]::Parse('$(PlayMapVersion)').Minor) &gt; 255)"
       Text="PlayMapVersion '$(PlayMapVersion)' has a major or minor above 255, the most an MSI's ProductVersion holds: no build may carry a version a release cannot install." />
<PropertyGroup Condition="'$(PlayMapBuild)' != ''">
  <PlayMapMsiVersion>$([System.Version]::Parse('$(PlayMapVersion)').Major).$([System.Version]::Parse('$(PlayMapVersion)').Minor).$(PlayMapBuild)</PlayMapMsiVersion>
</PropertyGroup>
```

```xml
<!-- installer/PlayMap.Installer.wixproj -->
<Error Condition="'$(PlayMapMsiVersion)' == ''"
       Text="An MSI is made from a numbered build only (PlayMapBuild and PlayMapCommit from CI): a local build's MSI would read as MAJOR.MINOR.0 and could not be upgraded in order." />
```

```csharp
// src/PlayMap.Core/BuildInfo.cs (the installer's hunk)
/// <summary>The MSI's ProductVersion: MAJOR.MINOR.BUILD; "" for a local build, which makes no MSI.</summary>
public string MsiVersion => IsLocal
    ? ""
    : System.Version.Parse(Version) is var v ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Build}") : "";
```

| Build | SemVer (people, wire, STATE) | File version | MSI |
|---|---|---|---|
| 0.2.0, run 17 | `0.2.0+17.abc1234` | `0.2.0.17` | `0.2.17` |
| 0.2.3, run 18 | `0.2.3+18.def5678` | `0.2.3.18` | `0.2.18` (upgrades 0.2.17) |
| 0.3.0, run 19 | `0.3.0+19.0123abc` | `0.3.0.19` | `0.3.19` |
| local | `0.2.0-dev+abc1234` | `0.2.0.0` | none (refused) |

The release job reads the last release's MSI version (`gh release view`) and refuses an MSI that is not greater, naming both; a new
workflow file starts run numbers at 1, so the commit that adds one raises MINOR. Add/Remove Programs shows `ProductVersion`; the
product's name carries the SemVer ("PlayMap 0.2.3 (build 18)"), and `docs/INSTALL.md` explains the two numbers.

## 2. What the MSI lays down

| Where | What |
|---|---|
| `%ProgramFiles%\PlayMap\` | the self-contained publish (`PlayMap.exe` and its assemblies, .NET 10 included); ANGLE's `av_libglesv2.dll` (the desk's Avalonia and the D3D11 path); OpenAL Soft; libvmx; the shader worker's natives (DXC's `dxcompiler.dll` and `dxil.dll`, glslang, SPIRV-Tools, SPIRV-Cross) |
| `…\ffmpeg\` | FFmpeg 9.0.2's LGPL shared libraries, as replaceable files (today the folder carries none and the node looks for them beside the exe [map §6.2]) |
| `…\D3D12\` | the Agility SDK's `D3D12Core.dll` (its debug layer is left out of releases) |
| `…\Supervisor\PlayMap.Supervisor.exe` | the service |
| `%ProgramData%\PlayMap\` | `dumps\` (WER), `supervisor\` (its status file and log); the show account may write both |
| `HKLM\SOFTWARE\PlayMap` | `InstallDir`, `Version`, `ShowUser`, `Supervised` (read by the desk and by `machine.install`) |

Per user, as today: the node's home (`%LOCALAPPDATA%\PlayMap`: settings, journal, caches, the pipeline cache) in the show user's
profile.

**Upgrade and launch.** A fixed `UpgradeCode`; `MajorUpgrade` scheduled after `InstallInitialize`, no same-version upgrade, a
downgrade refused with "PlayMap 0.2.17 is newer than this installer's 0.2.12: uninstall it first to go back". A launch condition
from a registry search of `CurrentBuildNumber` (`VersionNT` does not tell 24H2 from older builds): "PlayMap needs Windows 11 24H2
(build 26100) or later; this machine runs build 22631". x64 only.

## 3. Machine-level actions

| Action | How | Undone on uninstall |
|---|---|---|
| Firewall rules for `PlayMap.exe` | WiX's firewall extension: TCP 9940 (web, MCP), 9941 (line wire); UDP 9928 (hello), 9942 (clock, PMVB), 9943 (OSC), 6454 (Art-Net), 5568 (sACN), 5353 (mDNS); private and domain profiles; the scope a property, the local subnet by default | yes |
| No URL ACL | Kestrel binds its sockets itself; `InstallerTests` start the node as a standard user and bind 9940 on every interface | — |
| mDNS | checked on the runner: `_playmap._tcp` browsable beside Windows' own responder on 5353; if the two collide, the node registers through `DnsServiceRegister` behind the platform seam | — |
| WER LocalDumps | `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe`: `DumpFolder` `%ProgramData%\PlayMap\dumps`, `DumpType` 2 (full), `DumpCount` 5 | yes |
| Privileges for the show account | `SeIncreaseBasePriorityPrivilege` (a realtime GPU queue, used only when measured) by the supervisor's `grant <account>` (`LsaAddAccountRights`) as a deferred custom action; nothing else | yes (`revoke`) |
| Performance Log Users | the show account added (wp3-present's stretch reads its own present events) | yes |
| Event source | `PlayMap` in the Application log | yes |
| The service | `PlayMapSupervisor`, LocalSystem, automatic; recovery: restart after 5 s, restart after 30 s, then none, reset after a day, also on non-crash failures | yes |

The installer neither creates the show account nor stores its password; `docs/INSTALL.md` says how (§7).

## 4. The armed check

Before `InstallValidate` (immediate, so nothing is replaced yet), the MSI runs `PlayMap.Supervisor.exe preinstall-check`: it asks the
local node on loopback (`SHOW` on its line wire, the port from the show user's node settings) whether the show is armed. Armed → the
check sets `PLAYMAP_ARMED_WORDS` and a conditional error action stops the install or the uninstall with the node's words ("the show
is armed on this node since 20:31 by Desk: disarm first"). No answer (no engine running) → the install goes on. On a disarmed
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

public readonly record struct SupervisorFacts(bool ShowSessionActive, bool EngineAlive, bool HealthAnswered, long ShownFrames,
    bool OutputsOpen, ObserverStatus Observer, bool StopRequested);

public enum SupervisorAction { None, Start, RequestStop, Terminate, WriteStatus, WriteDumpManifest }
```

| Concern | Rule |
|---|---|
| Where the engine runs | the active console session (`WTSGetActiveConsoleSessionId`) when its user is `ShowUser`; `WTSQueryUserToken`, `CreateEnvironmentBlock`, `CreateProcessAsUser` on `winsta0\default` (M9; a scheduled task at the show user's logon is the fallback, the service then only watching) |
| Restart | after an exit: 1 s, doubling to 30 s; five restarts in ten minutes → `StoppedTooManyRestarts`, said in the status file, cleared by `PlayMap.Supervisor.exe resume` or the service's restart |
| The heartbeat | every 500 ms: HEALTH over loopback (each output's `ShownFrames`, wp3-stability) and, as a second path, the observer's status file |
| Stopping a frozen engine | open outputs whose shown-frame count has not moved for 3 s, or HEALTH silent for 10 s with no shown frames → set the engine's stop event (`Global\PlayMap-Engine-Stop`, its ACL the show user's), wait 5 s for the normal stop (doors closed in reverse, the recovery record written), then terminate; the cause in the supervisor's log and the next engine's journal |
| What it never does | stop an engine for a slow HEALTH alone while frames move; run as the engine's parent in a way that ends the engine when the service ends (a supervisor restart re-adopts the running engine by its pid file) |
| Dumps | a watcher on `dumps\`: beside each new dump a manifest — build, commit, engine pid, exit code, the journal's last sequence, when |
| Its status | `%ProgramData%\PlayMap\supervisor\status.json` (replaced atomically: state, engine pid, restarts, last cause, when), read by the engine into `machine.install` and by the desk |
| Its pipe | `\\.\pipe\PlayMap.Supervisor` (the show user and administrators): `status` and `resume` only this round |

**The desk.** `Link/EngineSupervisor.cs` reads `HKLM\SOFTWARE\PlayMap\Supervised`: on a supervised machine the desk attaches to the
local engine over loopback and never starts its own; while the engine is being started it shows the supervisor's state from the
status file.

## 6. Signing

Azure Artifact Signing on `main`'s builds (its CI action, the key in an HSM, the account's eligibility per `PLAN.md` §9 decision 2):
`PlayMap.exe`, PlayMap's own assemblies, `PlayMap.Supervisor.exe` and the MSI, timestamped. A job step checks every one with
`Get-AuthenticodeSignature` (all `Valid`) and fails otherwise. Branch builds are unsigned and their artifact is named so
(`PlayMap-0.2.17-win-x64-unsigned.msi`). One identity signs every channel, so SmartScreen's reputation accrues to it.

## 7. The show machine (`docs/INSTALL.md`)

- **The OS.** Windows 11 IoT Enterprise LTSC 2024 for show nodes (build 26100, supported to 10 Oct 2034, no feature updates, Shell
  Launcher and specialized displays available); Pro or Enterprise 24H2/25H2 for desks. Windows 10 is not supported.
- **The show account.** A standard user; auto-logon through Sysinternals Autologon, which keeps the password as an LSA secret —
  never `DefaultPassword` in plain text in the registry; `ShowUser` given to the MSI.
- **Shell Launcher** on IoT Enterprise (`Dism /online /Enable-Feature /all /FeatureName:Client-EmbeddedShellLauncher`) with the
  engine as the show account's shell, for a node with no desk; the supervisor still watches it.
- **Updates.** Feature updates deferred 365 days, quality updates 30, no automatic restart with a user logged on, a maintenance
  window; a pending reboot is a preflight fact (wp3-platform). The stretch's opt-in feature writes these policies once; the guide
  says how to set them by hand.
- **Firmware and power.** Power on after power loss in the firmware; the High or Ultimate Performance plan; USB selective suspend
  and PCIe link-state power management off; the display never sleeping; the screen saver off. Each is a preflight fact.
- **Drivers.** The GPU driver and the sync board's firmware pinned for a show (`MACHINE LOCK`), updated only in a window.

## 8. Facts in STATE and preflight

```json
"machine": { "install": { "installed": true, "msi": "0.2.17", "dir": "C:\\Program Files\\PlayMap", "supervised": true,
  "supervisor": { "state": "running", "restarts": 0, "lastCause": "", "at": 1760600000000 },
  "autoLogon": { "on": true, "user": "show" }, "policies": { "updatesDeferred": true } } }
```

Preflight's `install` section: a folder run on a node that looks like a show node (amber: "not installed: nothing restarts the
engine if it stops"); the supervisor stopped after too many restarts (red, fix `PlayMap.Supervisor.exe resume` in words); auto-logon
off on a supervised node (amber); updates not deferred (amber).

## 9. Tests

| Test | Job | Holds |
|---|---|---|
| `MsiVersionTests` | both | §1's table; a major or minor of 256 refused; build 0 refused; a lower version than the last release refused with "raise MINOR" |
| `VersionPropsTests` | both | a build of 256.0.0 fails with the words; a numbered build sets `PlayMapMsiVersion` |
| `InstallerTests` | Windows (elevated) | the MSI builds; a quiet install lays the files, the service with its recovery actions, the firewall rules, the WER values, the registry key; the previous build's MSI (built in the job with a lower build number) upgrades to this one and keeps the node's settings; a downgrade refused with words; a low build refused by the launch condition (a test property); an uninstall removes all but the node's data; with a fake engine on loopback answering armed, the install stops with its words; a standard user binds 9940 |
| `SupervisorTests` | both | `SupervisorCore` on a manual clock: the backoff, the frozen-engine rule (and not on a slow HEALTH while frames move), the five-in-ten-minutes stop, re-adoption, the dump manifest; on Windows the service installs, starts and stops |
| `SupervisorHeartbeatTests` | both (at the merge with stability) | a fake engine on loopback whose shown frames stop → the stop event, then termination after the grace |
| `DeskAttachTests` | both | a supervised machine's desk attaches and never starts an engine |
| `SigningTests` | Windows, `main` only | every signed file verifies |

## 10. Risks

- **Auto-logon and a LocalSystem service** widen the attack surface (`PLAN.md` R11): a standard show account, its password as an LSA
  secret, the pipe's ACL, two named privileges for one named account, the engine never elevated.
- **Launching into a session** (M9) depends on the session being there: the supervisor waits for it, says so, and the scheduled task
  is the measured fallback.
- **FFmpeg inside the MSI** must be the LGPL build (`PLAN.md` §9 decision 4); `LicenceTests` check the MSI's contents.
- **WiX's maintenance fee** applies to organisations over the threshold (`PLAN.md` §9 decision 3).
