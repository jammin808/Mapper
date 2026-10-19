# The installer

PlayMap's per-machine installer (`docs/INSTALL.md` for a person; `docs/round3/design/installer.md` for the design).

- `PlayMap.iss` — the installer, an EXE built by Inno Setup 7.1: the owner's choice for this round, with an MSI revisited next round
  (`docs/INSTALL.md` says why, with M17's answer). Its licence page shows `licence.txt`, and every machine-level action is the
  supervisor's own command.
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
