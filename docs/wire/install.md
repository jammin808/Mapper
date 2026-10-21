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
`lastCause`, `freezeRule`, `at`; null when no status file is read), `autoLogon` (`on`, `user`; never a password), `signed`, `light`
(red once the supervisor stopped trying; amber on a supervised node whose auto-logon is off, whose supervisor wrote no status, or
whose supervisor waits — for the session, a person's resume or the backoff — or is stopping the engine; grey for a folder run; else
green: each supervisor state lit as the Supervisor card lights it, and as `install:supervisor` lights it too but for one case —
running or starting while the freeze rule waits for shown frames, which STATE and the card light green and preflight reports as a grey
information row, the usual state until an engine fills HEALTH's shown frames), `words`. The Windows edition is
`machine.identity.os`'s, the machine platform's, and not repeated here. Read from `HKLM\SOFTWARE\PlayMap`, Winlogon's
`AutoAdminLogon` and `DefaultUserName`, and the supervisor's `status.json` in `%ProgramData%\PlayMap\supervisor` (Core's
`Machine/SupervisorStatus.cs`, the record the supervisor writes it with, so `supervisor` is the file's object key for key), at most
every two seconds. A folder run: `installed` false, `supervisor` null.

## Preflight

Section `install`: `install:installed` (a folder run amber on a node whose project drives an enabled output, grey elsewhere),
`install:supervisor` (red once it stopped trying, with `PlayMap.Supervisor.exe resume` in words; amber while it waits or stops the
engine; the freeze rule waiting for shown frames, or on a node with no open output for the GPU's phase, as information),
`install:autologon` (amber off on a supervised node), `install:policies` and `install:signed` (information rows: grey, value `i`;
`install:policies` says this build writes no Windows Update policy and names machine preflight's row that says the rest, from the
edition the machine platform reads — `edition.updates` on Home, `updates` on every other edition — never telling a node whose
updates are held to defer them). The check is `Install/InstallPreflight.cs`, which the exe's composition carries through the spine's
`Install` hook (`Host/EngineParts.Install.cs` adds `Install/InstallParts.cs`, whose `Configure` adds the check), so `PREFLIGHT QUICK`
on the node reports the section (`InstallPartsTests`).

## Stopping the engine

The supervisor sets `Global\PlayMap-Engine-Stop` (Core's `SupervisorNames.StopEvent`) for every stop — the freeze rule's, `stop`,
an upgrade's, an uninstall's — and terminates the engine 5 s later only when it still runs. An engine started with `--supervised`
opens the event as it starts with Synchronize alone, the one right the event's ACL gives the show user (on an install that names no
show user, INTERACTIVE: every signed-in user), and waits on it for its life (`EngineEntry`); once it is set the engine takes the same
stop as Ctrl+C or SIGTERM — during its start, which it then ends, or once it is up: its doors close in order, the recovery record is
written, the journal says "the supervisor asked the engine to stop" and is flushed, its pid record is taken back, and it exits 0. A
supervised engine that finds no event (off Windows, or none made yet) runs, says so in its log, and stops by its signals alone
(`SupervisorStopTests`; the named event and its one right on the Windows job). The next supervised engine journals the cause of the
stop before it as it starts — "the supervisor started this engine after: <lastCause> (<n> restarts in ten minutes)", a row that is not
ok — when the status file names its pid and holds a cause (`Install/InstallParts.cs`, `InstallPartsTests`). The supervisor starts the
engine suspended and lets it run once the status naming it is written, so the status the engine reads as it starts is that one, or
once that write has failed, which `supervisor.log` records — a write that could not be made (a full disk, a locked file) leaves the
engine neither suspended for good nor without a trace.

## Papers note

The node installs per machine with an Inno Setup installer this round, the owner's choice; an MSI is revisited next round (beside an
MSI the EXE gives up the automatic undo of service and registry changes, repair and `msiexec` for IT). Its licence page shows
`installer/licence.txt` before anything is laid down, and the install keeps it as `{app}\licence.txt`: a section for PlayMap's own
terms, none named yet; the pinned Agility SDK package's `LICENSE.txt` verbatim as the terms a person agrees to for `D3D12Core.dll`,
the owner accepting Microsoft's distribution terms, the indemnity included; and pointers to the other components' notices. A quiet
install (`/SILENT` or `/VERYSILENT`) shows no page and must pass `/ACCEPTTERMS=yes`; without it the installer refuses with words and
lays nothing down. The installer and the portable folder carry `THIRD-PARTY-NOTICES.md`, `soft_oal-LICENSE.txt` and `licenses\`
(every package's own licence and notice files, in the installer's payload those of the packages inside the supervisor's single-file
exe too, read from its restore; `installer/notices.ps1`, which stops the build when the publish or the supervisor carries a package
the notices do not name, or a native library no package carries that they do not name by its file name in backticks — a file the
notices name only to say it is not shipped is written there without them). The portable folder carries no
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

`docs/ROADMAP.md`'s round-3 row of wp3-installer is built ("Built: the installer, the supervisor and the version rules"): a
per-machine installer built by Inno Setup this round, with its licence page (PlayMap's terms, none named yet, and Microsoft's for
`D3D12Core.dll` verbatim), an MSI revisited next round, unsigned with `SHA256SUMS`, the supervisor service, its machine-level
actions, the version rules, the armed check that fails closed and STATE `machine.install`; proven on the Windows runner, not in a lab
(L11, L18, L21 and L23's install part not run; `docs/INSTALL.md`). Preflight's `install` section is reported by the exe; the engine
the supervisor started stops on its stop event as any asked stop does, and journals the cause of the stop before it. The machine's
folders keep SYSTEM's files out of the show user's reach: `%ProgramData%\PlayMap` is owned by administrators with its own access
(SYSTEM and administrators change it, every account reads it), the supervisor writes its status file and log in `supervisor\`,
which has no rule of its own, and the show user writes `dumps\` and the engine's folder `supervisor\engine\` alone, whose pid
record the supervisor only reads; the installer's script makes none of them (no `[Dirs]`), so the supervisor's install makes each,
the PlayMap folder first, and refuses a folder that is a link before it makes anything inside it; it removes from `supervisor\` what another account
placed there (a link itself, a file, an empty folder) and refuses, deleting nothing inside it, a folder another account owns that
holds anything, and the uninstall takes the show user's write access back, the folders left.

The papers this merge carries (plan §5.4 item 7), each sentence checked against the merged code: `README.md`'s Next paragraph (the
installer and the supervisor present, the package built and proven on the runner alone), the module table's `PlayMap.Supervisor`
row (the service and its commands, `docs/INSTALL.md`'s table), its App row (a supervised machine's desk starts no engine) and Engine
row (the install part), its papers list (`docs/INSTALL.md`), its CI (the installer job beside the Linux and Windows jobs, FFmpeg
9.0.2's LGPL shared build under every decode test, the portable folder's notices, `SHA256SUMS` and `LicenceTests`) and its Versions
(the 255 rule; the artifacts `PlayMap-0.2.0.17-win-x64-unsigned` and `PlayMap-0.2.0.17-win-x64-unsigned-installer`);
`docs/ARCHITECTURE.md` §1 (the supervisor starts, restarts and stops the engine, the freeze rule waiting for HEALTH's shown frames;
the installer makes the supervisor's folder and the engine's inside it; a supervised machine's desk starts no engine and says the
supervisor's status) and §2's Supervisor, Engine and App rows; the agent brief (the supervisor beside the exe, three CI jobs, the
installer's tests outside `PlayMap.sln`, the notices every shipped package and native library needs, and its "Where things live" row
for the installer, the supervisor and `machine.install`); `docs/WIRE.md`'s round-3 section (`machine.install` and preflight's
`install` section written); `docs/ROADMAP.md` (the CI paragraph, the lab's list, the Today paragraph, the spine's tree, "Built: the
installer, the supervisor and the version rules", the package's row, R0.7, R4.5 and R4.6 in part, and the summary's R0.7);
`docs/COMPETITIVE.md` (adoption, where PlayMap is going, an Install row, the FFmpeg, runtime, firewall and unsigned risks);
`docs/RESEARCH.md` §8; `docs/round3/README.md` (the installer and supervisor present, the package built, its notes and lab scripts,
the version rules); `docs/UX.md` §5.2 (a folder run beside the installed node); `docs/wire/machine.md` (its two facts for this merge,
carried). `docs/INSTALL.md` carries the support tiers `machine.identity` reports (`os.tier`, each adapter's `tier` and `support`,
which no preflight row reads yet) and how `GPU-PRIORITY REALTIME`'s `SeIncreaseBasePriorityPrivilege` reaches an installed node:
granted only to the account `/SHOWUSER=` names, held from that account's next sign-in, `machine.capabilities`' `realtimePrivilege` and
REALTIME's refusal words saying whether the engine holds it. `docs/round3/lab/L23-home-node.md` names the installer's paper and script
by their paths, and both L23 scripts keep Smart App Control evaluating until the platform's amber row is recorded; L10's REALTIME run
names the install's show user. The fixtures this package's STATE block changed are regenerated by their tests
(`PLAYMAP_WRITE_FIXTURES=1`): `tests/fixtures/state/main.json`, `tests/fixtures/state/output.json`, and the Companion module's
`integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json`, each carrying `machine.install` (a folder
run's: the rigs that write them read no install key of the machine they run on), and `tests/fixtures/state/install.json` with its
cards.

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
them, and each of the worker's natives at its pin. The Windows job uploads the portable folder's LicenceTests results on their own,
pass or fail. The rest of §5.6 item 4 (no NDI, Aftermath, WARP, AMD or capture-card binary, FFmpeg reporting no `--enable-gpl`,
nothing of WiX, and every notice present) waits for the integration pass (below). The desk's own-engine path on a supervised machine
runs through desk-ux's seams of `ConnectionManager` (`DeskAttachTests`): Connect's sentence says it attaches to the engine the
supervisor keeps; the supervisor it makes starts nothing and answers so; Connect notes that the supervisor keeps the engine, what
the supervisor's status file says (its state, the last cause, `PlayMap.Supervisor.exe resume` where the supervisor holds the engine
or has stopped trying, and when the file was written) and that the desk waits for it; the link goes Live on loopback with no engine
started, and a Connect that gets no answer fails with the supervisor's words.

## What waits on other packages and on the spine

Nothing waits on the spine. The seams this package asked for (§5.7) are on this branch as commits of their own, each before the fill
that uses it, and reach `main` with the merge: the `Install` hook in the engine's composition (`Host/EngineParts`, PLAN §2.3's
`Install` row), which `InstallParts` fills to register `InstallPreflight`; `EngineEntry` waiting under `--supervised` on the
supervisor's stop event with Synchronize alone and taking the normal stop when it is set, the name in Core
(`SupervisorNames.StopEvent`, which `MachinePlan.StopEvent` names); the supervisor's status record in Core beside `EnginePidRecord`
(`SupervisorStatus`; the supervisor keeps its policy's words in `SupervisorStatusWords`, and the engine reads the file through it);
the identity rule in Core (`Core/Machine/EngineIdentity.cs`: `ProcessFacts`, `EngineExpectation`, `EngineIdentity`,
`ProcessReader`), which every reader of `engine.pid` holds a process to, `EnginePidRecord.TryWrite` included (each reads the file through
`EnginePidRecord.Named`, which refuses one longer than `MaxBytes` by its length before reading any of it): a record keeps its place
only while it names a running PlayMap engine, so a stale record whose pid another process took is replaced, its window counted from
the record's stamp, the host's start; the two folders in Core, the supervisor's (`SupervisorStatus.MachineFolder`, its status file
and log) and the engine's inside it (`EnginePidRecord.MachineFolder`, the pid record alone), and `EngineEntry.StatusFolderFor`
beside the record's folder; a stop asked while the engine starts journalled through the engine the host has made
(`EngineHost.Made`); and desk-ux's `ConnectionManager` taking whether the machine is supervised and the supervisor's status file as
seams (`SupervisorStatus.ReadThisMachine`), with Connect's supervised sentence. The supervisor adds the user, session and parent it
reads through the same handle (`Windows/EngineProcess.cs`).

What waits on other packages: `SupervisorHeartbeatTests` with wp3-stability's merge and `SupervisorRebuildTests` with
wp3-stability-2's (PLAN §5.5), and the rest of §5.6 item 4 at the integration pass (above). Of "every notice present" that pass lays
down what the installer does not yet carry: the notices of the libraries FFmpeg-Builds links statically into the shipped FFmpeg DLLs
(dav1d, libvpx, Opus, libxml2, HarfBuzz and the others `THIRD-PARTY-NOTICES.md` names under wp3-installer by their licences alone:
each one's copyright lines and licence text, the BSD conditions and disclaimer among them), and glslang's BSD-3-Clause notice inside
shaderc's library, which the installer ships too; until then `THIRD-PARTY-NOTICES.md` names them and says their texts are not laid
down.
