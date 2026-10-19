# The installer

PlayMap's per-machine installer (`docs/INSTALL.md` for a person; `docs/round3/design/installer.md` for the design).

- `PlayMap.iss` — the installer, built by Inno Setup 7.1 in the MSI's place with the same acceptance (`docs/round3/PLAN.md` §9
  decision 3): the WiX toolset's pinned source carries licence checks PlayMap never answers, patches or passes (the owner decides
  them), and M17 measures whether that source builds on the runner (`wix/`, below; `docs/INSTALL.md` says what it answered). Every
  machine-level action is the supervisor's own command.
- `licence.txt` — the terms the licence page shows and the install keeps as `{app}\licence.txt`: the section for PlayMap's own
  terms (none named yet), Microsoft's licence terms for `D3D12Core.dll` byte for byte from the pinned Agility SDK package's
  `LICENSE.txt`, and pointers to the other components' notices. UTF-8 with its byte-order mark and CRLF lines, never converted on
  checkout (`.gitattributes`); `InstallerTermsTests` holds it to the pinned package.
- `build-installer.ps1` — lays out the payload (the ReadyToRun publish, `ffmpeg\` with its licence texts, `D3D12\`, libvmx and its
  licence, OpenAL Soft's licence, `THIRD-PARTY-NOTICES.md`, `licence.txt`, `Supervisor\`), writes the payload's `SHA256SUMS` of
  every file it lays down, and compiles it with the version Version.props gives a numbered build; a local build makes no installer,
  with words.
- `licenses/` — the GNU licence texts the payload carries beside the files they cover (GPL version 3 for FFmpeg's LGPL version 3,
  LGPL version 2 for OpenAL Soft).
- `sums.ps1` — the `SHA256SUMS` of an unsigned build, and the job summary's notice.
- `tests/PlayMap.Installer.Tests` — the life cycle on the Windows runner, elevated (outside `PlayMap.sln`; the installer job runs it).
- `tests/PlayMap.FakeEngine` — a stand-in engine, run as `PlayMap.exe engine`, that answers the armed check as told, writes its pid
  record and waits on the stop event: the installer's and the supervisor's Windows tests start it.
- `wix/` — M17's measurement: the WiX toolset built from its MS-RL source at the tag `wix/toolset.json` pins, into `wix/feed`,
  the only source `nuget.config` allows for `WixToolset.*`, and the probe that says whether the self-built tool asks for the gesture.
