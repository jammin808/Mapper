# bench/r3-probe — what the hosted Windows runner gives (M7, M14)

`PlayMap.Bench.Probe.exe` asks the Windows CI runner the questions of round 3's plan (`docs/round3/PLAN.md` §1.5) that only the
runner can answer. The Windows job's step "The runner's answers (M7, M14)" builds and runs it and writes the answers in the job's
summary. Every question is asked and answered. A *no* is a fact for the package that owns the question, never the probe's failure:
the step fails only when the probe writes no answers at all.

| # | What it asks | Whose answer it is |
|---|---|---|
| M7 | Does a .NET apphost (this exe, which exports no `D3D12SDKVersion`) load the Agility SDK's runtime from the `D3D12` folder beside it through `ID3D12SDKConfiguration1::CreateDeviceFactory`? It asks with the Direct3D 12 test's own code (`tests/PlayMap.Render.D3D12.Tests/WarpDevice.cs`), so the probe and the test cannot ask differently, and it says which `D3D12Core.dll` and `d3d10warp.dll` the process loaded | wp3-d3d12-core, wp3-installer |
| M14 | MMCSS for a render thread (`Games`) and an audio thread (`Pro Audio`), raised to high and reverted | wp3-platform |
| M14 | The power-throttling state (HighQoS, every timer honoured), set and read back | wp3-platform |
| M14 | The GPU scheduling class raised to high and read back | wp3-platform |
| M14 | Each adapter and display, and each display's colour space now: is one in HDR10? | wp3-present |
| M14 | A flip swap chain on WARP: may it present in HDR10? | wp3-present |
| M14 (M9) | This process's session and the console session; that session's user token (`WTSQueryUserToken`); a process started there with it (`CreateProcessAsUser`). The job asks again as LocalSystem, through a scheduled task, which is how the supervisor asks | wp3-installer |

```
PlayMap.Bench.Probe --out answers.md [--sdk 619] [--agility <folder holding D3D12Core.dll>]
PlayMap.Bench.Probe --session --out session.md
```

- `--sdk` is the Agility SDK version: the job reads `D3D12_SDK_VERSION` from the pinned package's `d3d12.h`.
- `--agility` defaults to the `D3D12` folder beside the exe.
- `--session` asks M9's calls alone, for the run as LocalSystem.

It exits 0 once its answers are written, 2 when its command line is wrong. It is Windows only and outside `PlayMap.sln`, so the
Linux build never builds it.
