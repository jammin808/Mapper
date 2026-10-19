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
`status.json`, at most every two seconds. A folder run: `installed` false, `supervisor` null.

## Preflight

Section `install`: `install:installed` (a folder run amber on a node whose project drives an enabled output, grey elsewhere),
`install:supervisor` (red once it stopped trying, with `PlayMap.Supervisor.exe resume` in words; the freeze rule waiting for shown
frames as information), `install:autologon` (amber off on a supervised node), `install:policies` and `install:signed` (information
rows: grey, value `i`). The check is `Install/InstallPreflight.cs`; the exe's composition registers it once the spine's
`AddInstallParts` hook lands (until then tests register it through the services).

## Stopping the engine

The supervisor sets `Global\PlayMap-Engine-Stop` and terminates the engine 5 s later when it has not ended. No engine opens the
event yet (`EngineEntry` waiting on it under `--supervised`, with Synchronize alone, is the spine's seam), so every stop — the
freeze rule's, `stop`, an upgrade's, an uninstall's — is a termination after 5 s, with no normal stop, no recovery record, and its
cause in `supervisor.log` alone.

## Papers note

The node installs per machine with an Inno Setup installer this round, the owner's choice; an MSI is revisited next round (beside an
MSI the EXE gives up the automatic undo of service and registry changes, repair and `msiexec` for IT). Its licence page shows
`installer/licence.txt` before anything is laid down, and the install keeps it as `{app}\licence.txt`: a section for PlayMap's own
terms, none named yet; the pinned Agility SDK package's `LICENSE.txt` verbatim as the terms a person agrees to for `D3D12Core.dll`,
the owner accepting Microsoft's distribution terms, the indemnity included; and pointers to the other components' notices. A quiet
install (`/SILENT` or `/VERYSILENT`) shows no page and must pass `/ACCEPTTERMS=yes`; without it the installer refuses with words and
lays nothing down. The installer and the portable folder carry `THIRD-PARTY-NOTICES.md`, `soft_oal-LICENSE.txt` and `licenses\`
(every package's own licence and notice files; `installer/notices.ps1`, which stops the build when the publish carries a package the
notices do not name). The portable folder carries no file of the Agility SDK, and the Windows job checks that on every build. M17's
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
section is built and tested, and shown by the exe once the spine's install hook lands (below).

Lines outside this package's files that the integration pass changes, with what is true now (main's papers already name Inno
Setup's installer, its licence page and the retired WiX probe in `README.md`, `docs/ROADMAP.md`, `docs/round3/PLAN.md` and
`docs/round3/design/installer.md`):

- `README.md:173` (`PlayMap.Supervisor`: "it does nothing yet, says so on its standard error and exits 1"): the supervising service
  and its commands (`run`, `status`, `stop`, `resume`, `preinstall-check`, `install`, `uninstall`, `upgrade-stop`, `grant`,
  `revoke`, `rights`, `version-check`, `previous-build`, `session-probe`), `docs/INSTALL.md`'s table.
- `README.md:290` and `docs/ROADMAP.md:28` (the Windows artifact `PlayMap-0.2.0.17-win-x64`): the portable artifact is
  `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned` with its notices and `SHA256SUMS`, and the installer job's
  `PlayMap-<…>-win-x64-unsigned-installer` holds the installer, its `SHA256SUMS` and `SHA256SUMS.payload`; Version.props refuses a
  MAJOR or MINOR above 255, since the installer's version is MAJOR.MINOR.BUILD; both test jobs and the installer use FFmpeg 9.0.2's
  LGPL shared build (`lgpl-shared`), and no GPL FFmpeg is shipped.
- `docs/ROADMAP.md` R0.7 ("Next (wp3-installer): the unsigned Inno Setup installer…") and row 190 (the round's installer row):
  built — the unsigned Inno Setup installer with `SHA256SUMS`, its licence page and a quiet install's `/ACCEPTTERMS=yes`, its
  firewall rules, the notices beside the files in it and in the portable folder, and the LGPL FFmpeg under every decode test and
  inside the installer, with its licence texts; `LicenceTests` on the installer at the integration pass.
- The fixtures this package's STATE block changed, regenerated by their tests (`PLAYMAP_WRITE_FIXTURES=1`):
  `tests/fixtures/state/main.json`, `tests/fixtures/state/output.json`, and the Companion module's
  `integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json`, each now carrying `machine.install`.

What the runner answered for the plan's two probes (§3.5 item 4), from the installer job's summary: a standard account runs the
installed engine and serves HTTP on 9940 with no URL reservation (`InstallerTests`); and for `_playmap._tcp` beside Windows' own mDNS
responder, Windows' resolver (`Resolve-DnsName -Type PTR _playmap._tcp.local`) answers "DNS name does not exist", while a DNS-SD
browse on the wire (a PTR query to 224.0.0.251:5353) is answered from the runner's address naming the instance it ran — the answer
stretch 10 is decided by.

What waits on other packages and on the spine:

- The shader worker's natives (DXC's `dxcompiler.dll` and `dxil.dll`, glslang, SPIRV-Tools, SPIRV-Cross; design §2, §3.5 item 3)
  are not in the payload: they are wp3-shaders' and not on main yet. At the rebase onto a main that carries them, the payload takes
  their hash-pinned files into its root, `InstallerTests`' `AssertInstalled` asserts `dxcompiler.dll` and `dxil.dll` (and the others
  shaders ships), and the notices name them.
- Seams of the spine (§5.7), requested: an `AddInstallParts` hook in the engine's composition (`Host/EngineParts`), which this
  package fills to register `InstallPreflight`, with a test that composes the engine as the host does and finds `install:*` rows in
  `PREFLIGHT QUICK`; `EngineEntry` opening `Global\PlayMap-Engine-Stop` with Synchronize alone under `--supervised`, running the
  normal shutdown when it is set, and the next supervised engine journalling the status file's `lastCause` at start; the
  supervisor's status record in Core beside `EnginePidRecord`, and `EnginePidRecord.TryWrite`'s liveness read held to the same
  identity rule (`Install/EngineIdentity.cs`).
- From desk-ux, as an extension of §4's installer hunk: a one-line injection point in `ConnectionManager` (an `EngineSupervisor`
  factory or its supervised predicate), so a test runs `ConnectOwnAsync` on a supervised machine against an engine live on loopback
  and finds the link Live with no engine started.
