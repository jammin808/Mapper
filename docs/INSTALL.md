# Installing PlayMap on a show machine

*How a node is installed once per machine, how it keeps running, and what Windows says about an unsigned build. The design is
`docs/round3/design/installer.md`; the installer's files are in `installer/`.*

## What the installer is

PlayMap installs per machine with one installer, `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned.exe`, built by Inno Setup
7.1. The plan's MSI is built by the WiX toolset compiled from its MS-RL source (`docs/round3/PLAN.md` D6). On the Windows runner
the pinned source (v7.0.0) does not build as it stands: it asks for Visual Studio 2022's C++ tools (v143), the runner's Visual
Studio 2026 carries only its own, and adding them through Visual Studio's installer fails on the runner (M17, CI's `wix-toolset`
job). The source also carries its licence check (the SDK's WIX7015 target and `wix.exe`'s own), which PlayMap never answers with
the gesture and which no patch in this tree answers. So Inno Setup builds the installer with the same acceptance (§9 decision 3).
It runs elevated once and does every machine-level action
through the supervisor's own commands, so the engine itself never runs as administrator:

| Where | What |
|---|---|
| `C:\Program Files\PlayMap\` | the exe and the desk (a self-contained publish, ReadyToRun), libvmx beside the exe; `THIRD-PARTY-NOTICES.md`, the licence texts its files ask for beside them, and `SHA256SUMS` of every file laid down |
| `…\ffmpeg\` | FFmpeg 9.0.2's LGPL shared libraries, replaceable files (the same hash-pinned build every decode test on CI loads), with the build's `LICENSE.txt` (the LGPL version 3) and `GPL-3.0.txt` |
| `…\D3D12\` | the Agility SDK's `D3D12Core.dll` (its debug layer is left out) |
| `…\Supervisor\PlayMap.Supervisor.exe` | the supervisor |
| `%ProgramData%\PlayMap\` | `dumps\` (WER's dumps) and `supervisor\` (its status file, log and the engine's pid record); the show user may write both |
| `HKLM\SOFTWARE\PlayMap` | `InstallDir`, `Version`, `SemVer`, `ShowUser`, `Supervised`, `Signed` |

and on the machine: the `PlayMapSupervisor` service (LocalSystem, automatic, restarted after 5 s and 30 s, the count reset after a
day, non-crash failures too); inbound firewall rules for `PlayMap.exe` — TCP 9940 (web, MCP) and 9941 (line wire), UDP 9928, 9942,
9943, 6454, 5568 and 5353 — on the private and domain profiles, scoped to the local subnet (`/PUBLICFIREWALL=1` adds Public; a new
network is Public until the person sets it Private); WER LocalDumps for `PlayMap.exe` (full dumps, five kept, in
`%ProgramData%\PlayMap\dumps`); the `PlayMap` Event Log source; and `SeIncreaseBasePriorityPrivilege` for the show account. No URL
reservation is made: the node binds its own sockets.

Install quietly with `PlayMap-….exe /VERYSILENT /SUPPRESSMSGBOXES /SHOWUSER=<computer>\show /LOG=install.log`. The installer:

- refuses Windows builds older than 26100 (Windows 11 24H2), with the build it found;
- refuses an older build over a newer one ("PlayMap 0.2.17 is newer than this installer's 0.2.12: uninstall it first to go back")
  and the same build again; going back is an uninstall and an install, by a person;
- asks the running engine first, and fails closed: an engine answering armed stops the install (and the uninstall) with the
  node's words ("the show is armed on this node since 20:31 by Desk: disarm first"); an engine process that does not answer within
  5 s stops it too ("an engine is running here (pid 4120) but does not answer: stop it from the desk or with
  `PlayMap.Supervisor.exe stop`, then install again"); an engine that answers the check with an error stops it as well, quoting
  the engine's reason ("the engine (pid 4120) answered the armed check with ERR …: disarm or stop it from the desk, then install
  again"); no engine, or a disarmed one, lets it go on;
- while Smart App Control is evaluating, shows one warning page (a quiet install writes it to its log): "Smart App Control is
  evaluating this PC: once it enforces, Windows blocks this unsigned build and the node will not start. Turn it off in Windows
  Security > App & browser control before the show."

The installer goes into `C:\Program Files\PlayMap` only: its service runs as SYSTEM from there, and a folder an ordinary account
may change would let that account replace what SYSTEM runs. It shows no folder page, and a `/DIR=` elsewhere, or an earlier install
found elsewhere, is refused with words before anything changes.

**Upgrades.** Just before anything is replaced the installer asks the armed check again (the wizard may have waited on its pages);
refuses while any program other than the supervised engine runs from the folder — a desk left open, an engine run by hand ("a
program runs from C:\Program Files\PlayMap (pid 4120, PlayMap.exe): close it (the desk, or an engine run by hand), then install
again"); then stops the earlier service and its engine (a termination after 5 s: see the supervisor, below). The earlier install's machine-level actions
stay as they are, and the new build's supervisor sets each again once the files are in place, the machine marked supervised last,
once the service runs; an upgrade that names another show user takes the right and the folders' write access back from the earlier
one. An upgrade that does not reach its end — refused, cancelled, a file it could not replace — has its files rolled back by Inno
Setup and the earlier service started again, which starts the earlier engine: the machine is as it was. An install whose
machine-level setup fails (exit 3, its words in the log) leaves the files in place and the machine not supervised, so the desk
starts its own engine until the installer is run again.

**Uninstall.** After the person confirms it and before any file is removed, the supervisor's uninstall asks the armed check again,
refuses while another program runs from the folder, stops the service and its engine, and takes back every machine-level action —
the supervised mark first, then the service, the firewall rules, WER's values, the event source and the account right, each tried
whatever the others did. Any refusal, or anything it could not take back, stops the uninstall with every file still in place, its
words in the uninstall's log. The dumps and the node's own data (`%LOCALAPPDATA%\PlayMap` in the show user's profile) stay. The MSI's
transactional rollback of service and registry changes is what the Inno Setup installer gives up; the order above stands in for it.

**What the owner decides.** The Agility SDK's terms let `D3D12Core.dll` ship inside PlayMap's installer on conditions that include
its end users agreeing to terms that protect Microsoft at least as much as Microsoft's own do; the installer shows no licence page,
so how that condition is met is the owner's decision (`THIRD-PARTY-NOTICES.md`, wp3-installer). So is Inno Setup's request that a
commercial user buy a licence (`docs/round3/PLAN.md` §9 decision 3), and the WiX toolset's licence checks, which PlayMap never
answers, patches or passes.

## The two version numbers

Add/Remove Programs shows the installer's version, `MAJOR.MINOR.BUILD` (`0.2.17`), where BUILD is CI's build number; the product's
name carries the SemVer ("PlayMap 0.2.0 (build 17)"), as the desk's about line and HEALTH's `build` do. Every numbered build upgrades
the one before it. A major or minor above 255 is refused by every build; a local build makes no installer. A release's version is
greater than the last release's, or the installer job's release-order step refuses it with "raise MINOR": it reads the last
release's version from its installer's name, and stops when a release exists whose version it cannot read, or when GitHub cannot be
asked. The installer job proves each upgrade from the build before it: one build lower, or, for the first build of a raised MAJOR or
MINOR, the first build of the line below.

## The supervisor

`PlayMapSupervisor` starts `PlayMap.exe engine --supervised` in the auto-logged-on show user's console session (never session 0,
which cannot drive displays), restarts it after an exit with backoff (1 s doubling to 30 s), stops trying after five restarts in ten
minutes and says so, and stops an engine whose open outputs' shown frames have stood still for the stale time (3 s, or four of the
rebuild's first good frames) while HEALTH says the GPU runs — never while the engine reports no shown frames, never during a start or
a device rebuild within its budget (30 s while the first good frame is unmeasured on a real node), never for a slow HEALTH while
frames move. Its status (`%ProgramData%\PlayMap\supervisor\status.json`) is in STATE's `machine.install` and preflight's `install`
section. Stopping the service leaves the engine running; the next supervisor adopts it.

**Every stop is a termination after 5 s.** To stop the engine — the freeze rule's stop, `PlayMap.Supervisor.exe stop`, an upgrade's
or an uninstall's — the supervisor sets the engine's stop event (`Global\PlayMap-Engine-Stop`, which the show user may only wait on)
and terminates the engine when it has not ended 5 s later. No engine waits on that event yet (the engine's side of it is a seam of
the round's spine), so the engine is always terminated: no normal stop closes its doors in order, no recovery record is written,
and the cause is in `supervisor.log` only, not in the next engine's journal. The event stays set until the engine has gone, so an
engine that looks at it between frames will see it once it does.

The engine's pid record (`%ProgramData%\PlayMap\supervisor\engine.pid`) sits in a folder the show user may write, so it only says
which process to look at. The service adopts an engine it did not start only when that process is the installed `PlayMap.exe` run as
the engine, as the show user, in the console session, started when its record was written (a pid another process took after a power
cut fails here); any other record is removed, with the reason in `supervisor.log`. The elevated commands (`stop` with no service,
`uninstall`, an upgrade) end only the installed engine the record names, through the handle opened when it was checked, and say so
when the record names anything else. The armed check asks the engine a record names only when it is PlayMap's engine.

| Command (elevated where it changes the machine) | Does |
|---|---|
| `PlayMap.Supervisor.exe status` | the supervisor's state, restarts and last cause |
| `PlayMap.Supervisor.exe stop` | stops the engine (a termination after 5 s) and holds it until `resume` |
| `PlayMap.Supervisor.exe resume` | ends a hold or the too-many-restarts stop |
| `PlayMap.Supervisor.exe preinstall-check` | the installer's armed check |
| `PlayMap.Supervisor.exe grant <account>` / `revoke <account>` | the show account's right |
| `PlayMap.Supervisor.exe rights <account>` | the account rights an account holds, as the local security policy says |

`status`, `stop` and `resume` ask the service on its pipe (`\\.\pipe\PlayMap.Supervisor`, open to the show user and
administrators), and take an answer only from the service itself: a process running as SYSTEM or from the installed
`Supervisor\PlayMap.Supervisor.exe`. The service keeps the pipe's name its own (another program that holds the name is never served
on), answers each connection on its own within 2 s and a line of at most 64 bytes, and goes on answering whatever one client does.

On a supervised machine the desk attaches to the engine on loopback and never starts its own.

## Unsigned builds

Every build and installer is **unsigned** this round (the owner's decision, `docs/round3/PLAN.md` §9 decision 2), named `-unsigned`.
Before downloading, know what Windows does with it:

- **SmartScreen**: a downloaded installer shows "Windows protected your PC" with an unknown publisher; More info ▸ Run anyway runs
  it. Reputation never carries from one unsigned build to the next. A copy from a share or a USB stick usually carries no mark.
- **UAC** names the publisher "Unknown".
- **Smart App Control**, on by default on a clean Windows 11 install (Home included), blocks unsigned code with no per-app exception
  once it enforces: the installer, `PlayMap.exe` and the supervisor, before anything of PlayMap runs. You then see only Windows'
  own block (logged as event 3077 in `Microsoft-Windows-CodeIntegrity/Operational`), and a desk on another machine shows the node as
  not answering in the fleet's board. Turn it off in Windows Security ▸ App & browser control ▸ Smart App Control before installing.

**Checking a download.** Beside each artifact is a `SHA256SUMS` file, one line per file: its SHA-256 and its name.
In PowerShell:

```powershell
Get-FileHash .\PlayMap-0.2.0.17-win-x64-unsigned.exe -Algorithm SHA256
Select-String -Path .\SHA256SUMS -Pattern 'PlayMap-0.2.0.17-win-x64-unsigned.exe'
```

The two hashes match when the download is intact. That is the only check this round, and it shows a download is intact, not who
built it: the sums sit on the same page as the files, so whoever could change one could change the other. There is no updater, so
nothing verifies an update automatically.

**Checking an install.** The installer's artifact carries `SHA256SUMS.payload` beside its `SHA256SUMS`: the sums of every file the
installer lays down, which the install keeps as `C:\Program Files\PlayMap\SHA256SUMS`. `Get-FileHash` of an installed file matches
its line there (the uninstaller Inno Setup writes, `unins000.*`, is the one file not listed).

## The show machine

**The OS, in three tiers.** Windows 11 IoT Enterprise LTSC 2024 for permanent show nodes (build 26100, supported to 10 Oct 2034, no
feature updates, Shell Launcher and specialized displays); Pro or Enterprise 25H2 or later for desks and for nodes run with update
policies; Home 25H2 or later, supported and unmanaged (no kiosk, no specialized displays, no update deferral). Build 26100 is the
floor every edition shares, so Home or Pro 24H2, out of servicing since 13 Oct 2026, installs and runs and machine preflight says
it gets no security updates. S mode (Home only) runs no installer: switch out of it first (free and one-way). Windows 10 is not
supported.

**The GPU support tiers** are the ones `machine.identity` publishes (`docs/round3/design/platform.md`); this round no lab has
measured any of them, and each says so.

**The show account.** A local standard user, "show". Auto-logon through Sysinternals Autologon, which keeps the password as an LSA
secret — never `DefaultPassword` in plain text in the registry. Give the account to the installer (`/SHOWUSER=`). On Home, whose
setup asks for a Microsoft account (and a new one has no password), make the local "show" account after setup, with a password, and
set auto-logon to it.

**Shell Launcher** where the edition has it (IoT Enterprise, Enterprise, Education:
`Dism /online /Enable-Feature /all /FeatureName:Client-EmbeddedShellLauncher`), with the engine as the show account's shell on a
node with no desk; the supervisor still watches it. On Home and Pro the supervisor starts the engine in the show account's session
with Explorer underneath, and the desk's monitor (or a small admin display) stays the primary display, where notifications, UAC
and setup screens open.

**Updates.** On the editions with Update client policies: feature updates deferred 365 days, quality updates 30, no automatic
restart with a user logged on, a maintenance window. On Home, which honours none of them: pause updates past the show (35 days at a
time), set active hours, restart without updating from the power menu, choose the maintenance window by hand. This build writes no
update policy; preflight's `install` section says so as an information row.

**Security defaults.** Smart App Control off for an unsigned build; Memory Integrity left as it is (a capture or sync driver that
does not load under it says so); the device-encryption recovery key kept where it can be found; show media outside OneDrive
folders, whose files can be online-only placeholders; the show network set to Private, or the firewall rules given Public.

**Firmware and power.** Power on after power loss in the firmware (BIOS); the High or Ultimate Performance plan where offered, else
Balanced with the best-performance mode; USB selective suspend and PCIe link-state power management off; the display never
sleeping; the screen saver off.

**Drivers.** The GPU driver and a sync board's firmware pinned for a show (`MACHINE LOCK`), updated only in a window; on Home,
where Windows Update installs drivers with the monthly update, `MACHINE LOCK` observes and names a changed driver.

## What is proven, and where

CI's installer job (windows-2025, elevated) builds the installer and the one numbered a build before it, then: the launch condition
refuses build 22631; fake engines (run as `PlayMap.exe engine`) stop the install armed and silent, and the uninstall armed; the earlier
build installs and lays the files, each matching its line in `SHA256SUMS`, with the notices and licence texts, the service with its
recovery actions, the firewall rules on the private and domain profiles (Public too with `/PUBLICFIREWALL=1`), WER's values, the
show user's write access to `dumps\` and `supervisor\`, the show user's account right and the install's key; this build upgrades it
with Smart App Control evaluating and keeps the node's data; the earlier build is refused as a downgrade; a folder other than
Program Files, a show user Windows cannot find (exit 3, the machine left unsupervised), an upgrade while another program runs from the
install, and an upgrade that cannot replace a file (the earlier service running again) each end as this guide says; `stop` holds the
engine and `resume` starts it again, and the show user may only wait on the stop event; a standard account runs the installed engine
and serves HTTP on 9940 with no URL reservation; an uninstall straight after the service is stopped ends its engine and takes every
machine-level action back, the account right read back with `rights`; a pid record naming a process that is not the engine ends
nothing; the artifact's `SHA256SUMS` matches every file. No lab ran this round: a real IoT Enterprise LTSC node with auto-logon, the
supervisor starting the engine in session 1 on real hardware, Shell Launcher, an upgrade during a rehearsal, the unsigned installer
on clean Home and Pro, ReadyToRun's first ten minutes and a Home node (L11, L18, L21, L23) are not run.
