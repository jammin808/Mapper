# Installing PlayMap on a show machine

*How a node is installed once per machine, how it keeps running, and what Windows says about an unsigned build. The design is
`docs/round3/design/installer.md`; the installer's files are in `installer/`.*

## What the installer is

PlayMap installs per machine with one installer, `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned.exe`, an EXE built by Inno
Setup 7.1: the owner's choice for this round. An MSI is revisited next round. Beside an MSI, this EXE gives up the automatic undo of
service and registry changes (the order under Upgrades and Uninstall stands in for it), repair, and `msiexec` for IT departments.
`docs/round3/PLAN.md` D6 names Inno Setup this round; the plan's MSI (`docs/round3/design/installer.md` §1a–§4) is kept for next
round. M17's answer on the runner is that WiX v7.0.0's pinned source does not build on windows-2025. Its native parts need Visual
Studio's v143 C++ tools; the runner's Visual Studio 2026 carries v145 only, its installer adds none of the v143 components (it exits
having added none), and the build stops at MSB8020, the v143 build tools not found, in its first minute, before any licence check
runs. The source checks its licence in three places — `wix.exe`'s command line, the SDK target's WIX7015, and the
`CheckLicenseAcceptance.targets` every extension package carries — and WiX's own build sets `AcceptEula` for itself. PlayMap never
passes that gesture and patches none of it. The installer shows a licence page (below), runs elevated once and does every
machine-level action through the supervisor's own commands, so the engine itself never runs as administrator:

| Where | What |
|---|---|
| `C:\Program Files\PlayMap\` | the exe and the desk (a self-contained publish, ReadyToRun), libvmx beside the exe, and the shader worker's natives (shaderc, SPIRV-Tools, SPIRV-Cross, and of DXC its compiler alone, `dxcompiler.dll`); `licence.txt` (the terms the licence page shows), `THIRD-PARTY-NOTICES.md`, the licence texts its files ask for beside them, `licenses\` (the licence and notice files of the packages the publish comes from, the .NET runtime's among them, and DXC's licence and notices in `licenses\DXC\`), and `SHA256SUMS` of every file laid down |
| `…\ffmpeg\` | FFmpeg 9.0.2's LGPL shared libraries, replaceable files (the same hash-pinned build every decode test on CI loads), with the build's `LICENSE.txt` (the LGPL version 3) and `GPL-3.0.txt` |
| `…\D3D12\` | the Agility SDK's `D3D12Core.dll`, under Microsoft's terms the licence page shows (its debug layer and `d3dconfig.exe` are left out) |
| `…\Supervisor\PlayMap.Supervisor.exe` | the supervisor |
| `%ProgramData%\PlayMap\` | `dumps\` (WER's dumps) and `supervisor\` (its status file, log and the engine's pid record); the show user may write both |
| `HKLM\SOFTWARE\PlayMap` | `InstallDir`, `Version`, `SemVer`, `ShowUser`, `Supervised`, `Signed` |

and on the machine: the `PlayMapSupervisor` service (LocalSystem, automatic, restarted after 5 s and 30 s, the count reset after a
day, non-crash failures too); inbound firewall rules for `PlayMap.exe` — TCP 9940 (web, MCP) and 9941 (line wire), UDP 9928, 9942,
9943, 6454, 5568 and 5353 — on the private and domain profiles, scoped to the local subnet (`/PUBLICFIREWALL=1` adds Public; a new
network is Public until the person sets it Private); WER LocalDumps for `PlayMap.exe` (full dumps, five kept, in
`%ProgramData%\PlayMap\dumps`); the `PlayMap` Event Log source; and `SeIncreaseBasePriorityPrivilege` for the show account. No URL
reservation is made: the node binds its own sockets.

**The licence page.** Before anything is laid down the installer shows its terms, which a person accepts to go on: a section for
PlayMap's own terms, which the owner has not named yet and which states none; Microsoft's licence terms for the DirectX Agility SDK,
copied verbatim from the pinned `Microsoft.Direct3D.D3D12` package's `LICENSE.txt`, as the terms the person agrees to for
`D3D12Core.dll`; and where the other components' notices and licence texts are in the install. The install keeps the terms as
`C:\Program Files\PlayMap\licence.txt`. A quiet install (`/SILENT` or `/VERYSILENT`) shows no page, so it must pass
`/ACCEPTTERMS=yes`: without it the installer refuses with words and lays nothing down.

Install quietly with `PlayMap-….exe /VERYSILENT /SUPPRESSMSGBOXES /ACCEPTTERMS=yes /SHOWUSER=<computer>\show /LOG=install.log`;
read `licence.txt`'s terms first (an install with its page shows them). The installer:

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
words in the uninstall's log. The dumps and the node's own data (`%LOCALAPPDATA%\PlayMap` in the show user's profile) stay. The
order above stands in for the automatic undo of service and registry changes an MSI would give.

**What the owner decides.** The Agility SDK's DISTRIBUTABLE CODE section lets `D3D12Core.dll` ship inside PlayMap's installer on the
requirements it lists, among them to "require distributors and external end users to agree to terms that protect it and Microsoft at
least as much as this agreement" and to "indemnify, defend, and hold harmless Microsoft from any claims, including attorneys’ fees,
related to the distribution or use of your applications, except to the extent that any claim is based solely on the unmodified
distributable code". The owner ships it: the licence page shows Microsoft's terms verbatim as the terms a person agrees to for
`D3D12Core.dll`, and the owner accepts Microsoft's distribution terms, the indemnity included. PlayMap's own terms are the owner's
to name; until they are, the licence page says none are stated. Inno Setup's request that a commercial user buy a licence is the
owner's to meet too (`docs/round3/PLAN.md` §9 decision 3).

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
frames move. Its status (`%ProgramData%\PlayMap\supervisor\status.json`) is in STATE's `machine.install`. Preflight's `install`
section reads it too, but the exe does not show that section yet: its registration waits on the round's spine (an install hook in
the engine's composition), so today STATE alone carries the supervisor's status. Stopping the service leaves the engine running; the next supervisor adopts it.

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

On a supervised machine the desk attaches to the engine on loopback and never starts its own: when the engine does not answer,
Connect ▸ This machine says "This machine's engine is kept by its supervisor — the desk attaches to it." and "Waiting for this
machine's engine…", and the desk is on the engine as soon as it answers (`PlayMap.Supervisor.exe status` says what the service
holds).

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

**The portable folder.** The Windows job's portable artifact, `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned`, is the
exe's publish with its `SHA256SUMS`. Beside the files it carries the notices they ask for, as the installer lays them down:
`THIRD-PARTY-NOTICES.md`, `soft_oal-LICENSE.txt` beside OpenAL Soft's `soft_oal.dll`, and `licenses\` with the licence and notice
files of the packages the publish comes from (ANGLE's among them); the job stops when the publish carries a package
`THIRD-PARTY-NOTICES.md` does not name. It carries no file of the Agility SDK (no `D3D12Core.dll`, `d3d12SDKLayers.dll` or
`d3dconfig.exe`), so no licence page and no Microsoft terms come with it; the job lists those files on every build, says in its
summary that there are none, and stops if one appears. It carries no FFmpeg either: its engine loads the shared libraries a person
puts in place.

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

**The GPU support tiers.** NVIDIA RTX and RTX PRO: tier 1, validated. AMD RDNA 3 and RDNA 4: tier 2, validated per driver, so a
driver outside the validated list reads amber. Intel Arc: best effort, and said so. Any other GPU: unsupported, and said so. This
round no lab has measured any of them, so no tier is validated yet and each says that too. Once wp3-platform lands,
`machine.identity.adapters[].tier` carries each adapter's tier (`docs/round3/design/platform.md`).

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
update policy; preflight's `install` section has an information row that says so, which the exe shows once that section is
registered (above, the supervisor).

**Security defaults.** Smart App Control off for an unsigned build; Memory Integrity left as it is (a capture or sync driver that
does not load under it says so); the device-encryption recovery key kept where it can be found; show media outside OneDrive
folders, whose files can be online-only placeholders; the show network set to Private, or the firewall rules given Public.

**Firmware and power.** Power on after power loss in the firmware (BIOS); the High or Ultimate Performance plan where offered, else
Balanced with the best-performance mode; USB selective suspend and PCIe link-state power management off; the display never
sleeping; the screen saver off.

**Drivers.** The GPU driver and a sync board's firmware pinned for a show (`MACHINE LOCK`), updated only in a window; on Home,
where Windows Update installs drivers with the monthly update, `MACHINE LOCK` observes and names a changed driver.

## What is proven, and where

CI's installer job (windows-2025, elevated) builds the installer and the one numbered a build before it, then: the licence page,
read back from the built installer, holds the terms — their heading first, Microsoft's DISTRIBUTABLE CODE section on it, decoded as
UTF-8 — before Inno Setup lays anything down, and the install keeps them byte for byte; a quiet install without `/ACCEPTTERMS=yes`
(`/VERYSILENT`, `/SILENT`, or another value) is refused with words and lays nothing down; the launch condition refuses build 22631;
fake engines (run as `PlayMap.exe engine`) stop the install armed and silent, and the uninstall armed; the earlier build installs
and lays the files, each matching its line in `SHA256SUMS`, with the notices and licence texts (the runtime's, SkiaSharp's and ONNX
Runtime's third-party notices under `licenses\` among them), the shader worker's natives each at the worker's pin with DXC's licence
and notices in `licenses\DXC\` and no `dxil.dll`, `libdxil.so` or `LICENSE-MS.txt` anywhere, the service with its recovery actions,
the firewall rules on the private and domain profiles (Public too with `/PUBLICFIREWALL=1`), WER's values, the show user's write
access to `dumps\` and `supervisor\`, the show user's account right and the install's key; this build upgrades it with Smart App
Control evaluating and keeps the node's data; the earlier build is refused as a downgrade; a folder other than Program Files, a show
user Windows cannot find (exit 3, the machine left unsupervised), an upgrade while another program runs from the install, and an
upgrade that cannot replace a file (the earlier service running again) each end as this guide says; `stop` holds the engine and
`resume` starts it again, and the show user may only wait on the stop event; a standard account runs the installed engine and serves
HTTP on 9940 with no URL reservation; an uninstall straight after the service is stopped ends its engine and takes every
machine-level action back, the account right read back with `rights`; a pid record naming a process that is not the engine ends
nothing; the artifact's `SHA256SUMS` matches every file. On Linux, `InstallerTermsTests` holds `installer/licence.txt` to the pinned
Agility SDK package, its SHA-256 the pin's: Microsoft's `LICENSE.txt` whole, and its DISTRIBUTABLE CODE section byte for byte; on
both jobs it finds nothing in `PlayMap.iss` that skips the licence page or answers it (no `ShouldSkipPage`, no change to its
choices, no licence file named but [Setup]'s), and every package the publish's restore carries named in `THIRD-PARTY-NOTICES.md`.
The Windows job finds no Agility SDK file in the portable folder, and lays the notices into it. No lab ran this round: a real IoT
Enterprise LTSC node with auto-logon, the supervisor starting the engine in session 1 on real hardware, Shell Launcher, an upgrade
during a rehearsal, the unsigned installer on clean Home and Pro, ReadyToRun's first ten minutes and a Home node (L11, L18, L21,
L23; their scripts are in `docs/round3/lab/`) are not run.
