# wp3-installer on the wire

*Generated from the catalogue by `tools/gen_stubs3.py`; the package owns this file and keeps it to what it ships. A row is
answered "not available on this node yet" until the package routes it, and `VERBS` marks it `"available"` once it is;
a form added to an existing row is refused by that row's handler with the forms that work, and changes nothing.*

## Verbs

| Verb | Syntax | Words | Risk | Changes the show | Package |
|---|---|---|---|---|---|
| — | — | (no rows of its own) | | | |

## Forms added to existing rows

None.

## STATE

`machine.install`: `installed`, `msi` (the installer's version, MAJOR.MINOR.BUILD), `dir`, `supervised`, `showUser`, `supervisor`
(`state` — waiting-for-session, starting, running, stopping, backoff, stopped-too-many-restarts or held — `pid`, `restarts`,
`lastCause`, `freezeRule`, `at`; null when no status file is read), `autoLogon` (`on`, `user`; never a password), `edition`, `signed`,
`light`, `words`. Read from `HKLM\SOFTWARE\PlayMap`, Winlogon's `AutoAdminLogon` and `DefaultUserName`, and the supervisor's
`status.json` (Core's `Machine/SupervisorStatus.cs`, the record the supervisor writes it with, so `supervisor` is the file's object
key for key), at most every two seconds. A folder run: `installed` false, `supervisor` null.

## Preflight

Section `install`: `install:installed` (a folder run amber on a node whose project drives an enabled output, grey elsewhere),
`install:supervisor` (red once it stopped trying, with `PlayMap.Supervisor.exe resume` in words; the freeze rule waiting for shown
frames as information), `install:autologon` (amber off on a supervised node), `install:policies` and `install:signed` (information
rows: grey, value `i`). The check is `Install/InstallPreflight.cs`, which the exe's composition carries through the spine's
`Install` hook (`Host/EngineParts.Install.cs` adds `Install/InstallParts.cs`, whose `Configure` adds the check), so `PREFLIGHT QUICK`
on the node reports the section (`InstallPartsTests`).

## Stopping the engine

The supervisor sets `Global\PlayMap-Engine-Stop` (Core's `SupervisorNames.StopEvent`) for every stop — the freeze rule's, `stop`,
an upgrade's, an uninstall's — and terminates the engine 5 s later only when it still runs. An engine started with `--supervised`
opens the event as it starts with Synchronize alone, the one right the event's ACL gives the show user, and waits on it for its life
(`EngineEntry`); once it is set, before the engine is up or after, the engine takes the same stop as Ctrl+C or SIGTERM: its doors
close in order, the recovery record is written, the journal says "the supervisor asked the engine to stop" and is flushed, its pid
record is taken back, and it exits 0. A supervised engine that finds no event (off Windows, or none made yet) runs, says so in its
log, and stops by its signals alone (`SupervisorStopTests`; the named event and its one right on the Windows job). The next
supervised engine journals the cause of the stop before it as it starts — "the supervisor started this engine after: <lastCause>
(<n> restarts in ten minutes)", a row that is not ok — when the status file names its pid and holds a cause
(`Install/InstallParts.cs`, `InstallPartsTests`).

## Papers note

The node installs per machine with an Inno Setup installer this round, the owner's choice; an MSI is revisited next round (beside an
MSI the EXE gives up the automatic undo of service and registry changes, repair and `msiexec` for IT). Its licence page shows
`installer/licence.txt` before anything is laid down, and the install keeps it as `{app}\licence.txt`: a section for PlayMap's own
terms, none named yet; the pinned Agility SDK package's `LICENSE.txt` verbatim as the terms a person agrees to for `D3D12Core.dll`,
the owner accepting Microsoft's distribution terms, the indemnity included; and pointers to the other components' notices. A quiet
install (`/SILENT` or `/VERYSILENT`) shows no page and must pass `/ACCEPTTERMS=yes`; without it the installer refuses with words and
lays nothing down. The installer and the portable folder carry `THIRD-PARTY-NOTICES.md`, `soft_oal-LICENSE.txt` and `licenses\`
(every package's own licence and notice files; `installer/notices.ps1`, which stops the build when the publish carries a package the
notices do not name, or a native library no package carries that they do not name by its file name). The portable folder carries no
file of the Agility SDK, and the Windows job checks that on every build. `LicenceTests` hold the installer's payload (the installer
job) and the portable folder (the Windows job) to DXC's compiler alone and to notices that name every package and native library
each carries. M17's
answer: WiX v7.0.0's pinned source does not build on windows-2025 (its native parts need Visual Studio's v143 C++ tools; the
runner's Visual Studio 2026 carries v145 only and its installer adds none of the v143 components; the build stops at MSB8020 before
any licence check runs), and the source checks its licence in three places (`wix.exe`'s command line, the SDK target's WIX7015, and
the `CheckLicenseAcceptance.targets` every extension package carries; WiX's own build sets `AcceptEula` for itself); PlayMap never
passes the gesture and patches none of it. With the installer: the supervisor service, the firewall rules, WER dumps, the event
source and the show account's right, each done by the supervisor's commands; unsigned builds named `-unsigned` with `SHA256SUMS`,
and the payload's own sums laid down with it; the installer's version MAJOR.MINOR.BUILD; an install, upgrade or uninstall refused
while armed or while a live engine does not answer. `docs/INSTALL.md` is the person's guide; the lab scripts L11, L18, L21 and L23
(the installer's part) are in `docs/round3/lab/`, each not run (no lab: the owner's answer 5).

For `docs/ROADMAP.md`'s round-3 row of wp3-installer (the integration pass writes it): a per-machine installer built by Inno Setup
this round, with its licence page (PlayMap's terms, none named yet, and Microsoft's for `D3D12Core.dll` verbatim), an MSI revisited
next round, unsigned with `SHA256SUMS`, the supervisor service, its machine-level actions, the version rules, the armed check that
fails closed and STATE `machine.install`; proven on the Windows runner, not in a lab (`docs/INSTALL.md`). Preflight's `install`
section is reported by the exe; the engine the supervisor started stops on its stop event as any asked stop does, and journals the
cause of the stop before it.

Lines outside this package's files that the integration pass changes, with what is true now (main's papers already name Inno
Setup's installer, its licence page and the retired WiX probe in `README.md`, `docs/ROADMAP.md`, `docs/round3/PLAN.md` and
`docs/round3/design/installer.md`):

- `README.md:193` (`PlayMap.Supervisor`: "it does nothing yet, says so on its standard error and exits 1"): the supervising service
  and its commands (`run`, `status`, `stop`, `resume`, `preinstall-check`, `install`, `uninstall`, `upgrade-stop`, `grant`,
  `revoke`, `rights`, `version-check`, `previous-build`, `session-probe`), `docs/INSTALL.md`'s table.
- `docs/ARCHITECTURE.md` §1 ("is to start `PlayMap.exe engine --supervised`", "Today the supervisor's exe does nothing and says so")
  and §2's `PlayMap.Supervisor` row ("it does nothing yet, says so on its standard error and exits 1"): the supervising service
  starts the engine in the show user's session, restarts it, stops one whose shown frames have stopped while HEALTH says they should
  move, and answers its commands, `docs/INSTALL.md`'s table.
- `README.md:345` and `docs/ROADMAP.md:28` (the Windows artifact `PlayMap-0.2.0.17-win-x64`): the portable artifact is
  `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned` with its notices and `SHA256SUMS`, and the installer job's
  `PlayMap-<…>-win-x64-unsigned-installer` holds the installer, its `SHA256SUMS` and `SHA256SUMS.payload`; Version.props refuses a
  MAJOR or MINOR above 255, since the installer's version is MAJOR.MINOR.BUILD; both test jobs and the installer use FFmpeg 9.0.2's
  LGPL shared build (`lgpl-shared`), and no GPL FFmpeg is shipped.
- `docs/ROADMAP.md` R0.7 ("Next (wp3-installer): the unsigned Inno Setup installer…") and line 356 (the round's installer row):
  built — the unsigned Inno Setup installer with `SHA256SUMS`, its licence page and a quiet install's `/ACCEPTTERMS=yes`, its
  firewall rules, the notices beside the files in it and in the portable folder, and the LGPL FFmpeg under every decode test and
  inside the installer, with its licence texts; `LicenceTests` on the installer's payload and the portable folder (DXC's compiler
  alone with its licence and notices, and the notices naming every package and native library each carries), the rest of
  `docs/round3/PLAN.md` §5.6 item 4 at the integration pass.
- The fixtures this package's STATE block changed, regenerated by their tests (`PLAYMAP_WRITE_FIXTURES=1`):
  `tests/fixtures/state/main.json`, `tests/fixtures/state/output.json`, and the Companion module's
  `integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json`, each carrying `machine.install`.

What the runner answered for the plan's two probes (§3.5 item 4), from the installer job's summary: a standard account runs the
installed engine and serves HTTP on 9940 with no URL reservation (`InstallerTests`); and for `_playmap._tcp` beside Windows' own mDNS
responder, Windows' resolver (`Resolve-DnsName -Type PTR _playmap._tcp.local`) answers "DNS name does not exist", while a DNS-SD
browse on the wire (a PTR query to 224.0.0.251:5353) is answered from the runner's address naming the instance it ran — the answer
stretch 10 is decided by.

The shader worker's natives ride in the payload's root as the engine's publish lays them down (design §2, `docs/round3/PLAN.md` §3.5
item 3): shaderc, SPIRV-Tools (the pinned build the installer job names to the publish), SPIRV-Cross, and of DXC its compiler alone,
`dxcompiler.dll`, with the package's `LICENSE-LLVM.txt` and DXC's `LICENSE.TXT` and `ThirdPartyNotices.txt` in `licenses\DXC\`
beside it; no `dxil.dll` is laid down (§9 decision 24). `build-installer.ps1` refuses a payload without one of them, and
`InstallerTests`' `AssertInstalled` holds each installed native to the worker's pin, DXC's notices to the engine's copies, and finds
no `dxil.dll`, `libdxil.so` or `LICENSE-MS.txt` in the install. `installer/notices.ps1` stops the build when the payload or the
portable folder carries a native library that no package of its `PlayMap.deps.json` lists and `THIRD-PARTY-NOTICES.md` does not name
by its file name (`dxcompiler.dll` and `SPIRV-Tools-shared.dll` are such libraries; the payload's FFmpeg libraries, `D3D12Core.dll`
and `libvmx.dll` too), or a `dxcompiler.dll` without `licenses\DXC\` beside it. `LicenceTests` (`docs/round3/PLAN.md` §5.6 item 4,
its lines on DXC and on the notices) read the payload in the installer job and the portable folder in the Windows job: no `dxil.dll`
or `libdxil.so` under any case of its name, no file of DXC's package by name or by content but `dxcompiler.dll` beside the assemblies
at the worker's pin and its `LICENSE-LLVM.txt` in `licenses\DXC\`, which holds that and DXC's `LICENSE.TXT` and
`ThirdPartyNotices.txt` and nothing else; `THIRD-PARTY-NOTICES.md` as the repository holds it, naming every package and every such
native library the folder carries, each package's own licence and notice files under `licenses\<package id>\` as the restore holds
them, and each of the worker's natives at its pin. The rest of §5.6 item 4 (no NDI, Aftermath, WARP, AMD or capture-card binary, FFmpeg
reporting no `--enable-gpl`, nothing of WiX) waits for the integration pass. The desk's own-engine path on a supervised machine runs through
desk-ux's seams of `ConnectionManager` (`DeskAttachTests`): the supervisor it makes starts nothing and answers so, Connect notes that
the supervisor keeps the engine and that the desk waits for it, and the link goes Live on loopback with no engine started.

## What waits on other packages and on the spine

Nothing waits on the spine. The seams this package asked for (§5.7) are on this branch as commits of their own, each before the fill
that uses it, and reach `main` with the merge: the `Install` hook in the engine's composition (`Host/EngineParts`, PLAN §2.3's
`Install` row), which `InstallParts` fills to register `InstallPreflight`; `EngineEntry` waiting under `--supervised` on the
supervisor's stop event with Synchronize alone and taking the normal stop when it is set, the name in Core
(`SupervisorNames.StopEvent`, which `MachinePlan.StopEvent` names); the supervisor's status record in Core beside `EnginePidRecord`
(`SupervisorStatus`; the supervisor keeps its policy's words in `SupervisorStatusWords`, and the engine reads the file through it);
and the identity rule in Core (`Core/Machine/EngineIdentity.cs`: `ProcessFacts`, `EngineExpectation`, `EngineIdentity`,
`ProcessReader`), which every reader of `engine.pid` holds a process to, `EnginePidRecord.TryWrite` included: a record keeps its place
only while it names a running PlayMap engine, so a stale record whose pid another process took is replaced. The supervisor adds the
user, session and parent it reads through the same handle (`Windows/EngineProcess.cs`).

What waits on other packages: `SupervisorHeartbeatTests` with wp3-stability's merge and `SupervisorRebuildTests` with
wp3-stability-2's (PLAN §5.5), and the rest of §5.6 item 4 at the integration pass (above).
