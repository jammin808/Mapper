# The installer

PlayMap's per-machine installer (`docs/INSTALL.md` for a person; `docs/round3/design/installer.md` for the design).

- `PlayMap.iss` — the installer, built by Inno Setup 7.1: the WiX toolset built from its source still asks for its licence
  gesture, which PlayMap never passes, and no patch to the pinned source answers it in this tree, so Inno Setup takes the MSI's
  place with the same acceptance (`docs/round3/PLAN.md` §9 decision 3). Every machine-level action is the supervisor's own command.
- `build-installer.ps1` — lays out the payload (the ReadyToRun publish, `ffmpeg\`, `D3D12\`, libvmx, `Supervisor\`) and compiles
  it with the version Version.props gives a numbered build; a local build makes no installer, with words.
- `sums.ps1` — the `SHA256SUMS` of an unsigned build, and the job summary's notice.
- `tests/PlayMap.Installer.Tests` — the life cycle on the Windows runner, elevated (outside `PlayMap.sln`; the installer job runs it).
- `wix/` — M17's measurement: the WiX toolset built from its MS-RL source at the tag `wix/toolset.json` pins, into `wix/feed`,
  the only source `nuget.config` allows for `WixToolset.*`, and the probe that says whether the self-built tool asks for the gesture.
