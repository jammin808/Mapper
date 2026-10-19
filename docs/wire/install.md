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

## Papers note

The node installs per machine with an Inno Setup installer (the WiX toolset does not build from its pinned source on the runner, and its source carries a licence check PlayMap never answers), with
the supervisor service, firewall rules, WER dumps, the event source and the show account's right done by the supervisor's commands;
unsigned builds named `-unsigned` with `SHA256SUMS`; the installer's version MAJOR.MINOR.BUILD; an install refused while armed or
while a live engine does not answer. `docs/INSTALL.md` is the person's guide.

For `docs/ROADMAP.md`'s round-3 row of wp3-installer (the integration pass writes it): a per-machine installer built by Inno Setup,
unsigned with `SHA256SUMS`, the supervisor service, its machine-level actions, the version rules, the armed check that fails closed
and STATE `machine.install`; proven on the Windows runner, not in a lab (`docs/INSTALL.md`).
