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

The node installs per machine with an Inno Setup installer in the MSI's place: the WiX toolset's pinned source carries licence
checks PlayMap never answers, patches or passes (the owner's decision), and M17 measured on windows-2025 that the source does not
build there (Visual Studio 2026's installer holds none of the v143 C++ components WiX's native parts ask for; the build stops at
MSB8020 before any licence check runs). With the installer: the supervisor service, the firewall rules, WER dumps, the event source
and the show account's right, each done by the supervisor's commands; unsigned builds named `-unsigned` with `SHA256SUMS`, and the
payload's own sums laid down with it; the installer's version MAJOR.MINOR.BUILD; an install, upgrade or uninstall refused while
armed or while a live engine does not answer. `docs/INSTALL.md` is the person's guide; the lab scripts L11, L18, L21 and L23 (the
installer's part) are in `docs/round3/lab/`, each not run (no lab: the owner's answer 5).

For `docs/ROADMAP.md`'s round-3 row of wp3-installer (the integration pass writes it): a per-machine installer built by Inno Setup,
unsigned with `SHA256SUMS`, the supervisor service, its machine-level actions, the version rules, the armed check that fails closed
and STATE `machine.install`; proven on the Windows runner, not in a lab (`docs/INSTALL.md`). Preflight's `install` section is built
and tested, and shown by the exe once the spine's install hook lands (below).

Lines outside this package's files that the integration pass changes, with what is true now:

- `README.md:144` ("It will install per machine from an MSI made by the WiX toolset…"): it installs per machine from an Inno Setup
  installer, `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned.exe`; no MSI is built (`docs/INSTALL.md`, "What the installer is").
- `README.md:172` (`PlayMap.Supervisor`: "it does nothing yet, says so on its standard error and exits 1"): the supervising service
  and its commands (`run`, `status`, `stop`, `resume`, `preinstall-check`, `install`, `uninstall`, `upgrade-stop`, `grant`,
  `revoke`, `rights`, `version-check`, `previous-build`, `session-probe`), `docs/INSTALL.md`'s table.
- `README.md:289` and `docs/ROADMAP.md:28` (the Windows artifact `PlayMap-0.2.0.17-win-x64`): the portable artifact is
  `PlayMap-<MAJOR.MINOR.PATCH>.<build>-win-x64-unsigned` with its `SHA256SUMS`, and the installer job's
  `PlayMap-<…>-win-x64-unsigned-installer` holds the installer, its `SHA256SUMS` and `SHA256SUMS.payload`; Version.props refuses a
  MAJOR or MINOR above 255, since the installer's version is MAJOR.MINOR.BUILD; both test jobs and the installer use FFmpeg 9.0.2's
  LGPL shared build (`lgpl-shared`), and no GPL FFmpeg is shipped.
- `docs/ROADMAP.md` R0.7 ("Next (wp3-installer): the unsigned MSI…") and row 187 ("built by the WiX toolset compiled from its
  source"): the unsigned Inno Setup installer with `SHA256SUMS`, its firewall rules, and the LGPL FFmpeg under every decode test and
  inside the installer, with its licence texts; `LicenceTests` on the installer at the integration pass.
- `THIRD-PARTY-NOTICES.md:7-9` ("Today no FFmpeg is shipped… Round 3's MSI will carry…"): the installer ships the LGPL build's
  replaceable shared libraries in `ffmpeg\`, with `LICENSE.txt` and `GPL-3.0.txt` (the wp3-installer heading).
- The fixtures this package's STATE block changed, regenerated by their tests (`PLAYMAP_WRITE_FIXTURES=1`):
  `tests/fixtures/state/main.json`, `tests/fixtures/state/output.json`, and the Companion module's
  `integrations/companion-module-playmap/test/fixtures/state.json` and `state-pattern.json`, each now carrying `machine.install`.

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
