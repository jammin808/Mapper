# bench/r3-present — the present paths side by side, for M1 and M3

`PlayMap.Bench.Present.exe` answers two questions of round 3's plan (`docs/round3/PLAN.md` §1.5) on a real display, and it waits
for the lab: no machine of the lab's inventory exists this round (§7, D15), so it is built and never run.

- **M1** — is Direct3D 12 as fast as GL, on show hardware? Lab line **L1**: photon latency and missed vblanks.
- **M3** — does the Windows 11 composition swapchain take Direct3D 12 buffers and present at a target time? Lab line **L4**.

The Windows CI job builds it (the step "The lab's bench (built, not run)"), so it always compiles against the tree, and never
runs it: the hosted runner has no GPU and no display a photodiode can watch. It is Windows only and sits outside `PlayMap.sln`,
so the Linux build never builds it.

## What it does

On one display, a white square flashes for one frame in every `--flash` frames (30 by default) and is black otherwise. The square
is an eighth of the display's height, centred at the top, a twentieth of the height down — where L1 puts the photodiode. The bench
shows it through each present path in turn, `--seconds` each:

| `--path` | What presents | Its statistics |
|---|---|---|
| `d3d12` | A raw Direct3D 12 flip swap chain: a device on the adapter that drives the display, a borderless window over the whole display, `FLIP_DISCARD` with three buffers and a frame-latency waitable at one frame, each frame cleared black (the square cleared white) with no shader | DXGI's present count and frame statistics |
| `gl` | PlayMap's GL window, as the engine opens it for `PRESENT PATH GL` (AUTO's path) | the output's present clock, in the engine's words |
| `d3d11` | PlayMap's Direct3D 11 path, as the engine opens it for `PRESENT PATH DXGI`: a DXGI flip swap chain on a D3D11 device through ANGLE | the output's present clock, with DXGI's frame statistics |
| `composition` | The composition swapchain (`presentation.h`, Windows 11) asked to take Direct3D 12 buffers. Each ask is written down with its answer: `CreatePresentationFactory` given the device and then its queue, `IsPresentationSupported` (and with Independent Flip), `AddBufferFromResource` with two shareable Direct3D 12 textures. Only when every one says yes does it present: through DirectComposition on a borderless window, each present with a target time two refreshes ahead, the manager's present statuses kept | the present ids, target times and statuses |
| `all` | each of the four in that order (the default) | |

Every present writes a CSV row (`--out`, `r3-present.csv` by default). A row holds:

- the path, the frame, and whether the square was white;
- the QPC ticks just before and just after the present call;
- the path's number for the present;
- the display statistics, where the path has them.

The file's first line gives the QPC frequency, so the photodiode's trace and PresentMon's capture line up on QPC. Lines starting
with `#` say how each path started, or why it did not.

```
PlayMap.Bench.Present [--path all|d3d12|gl|d3d11|composition] [--seconds 60] [--display 0] [--flash 30] [--hz 60] [--warp]
                      [--out r3-present.csv]
```

- `--display` counts the displays as DXGI lists them: each adapter in order, then each display on it.
- `--hz` is the refresh the composition path's target times are spaced by.
- `--warp` puts the Direct3D devices on WARP. That is for trying the bench on a machine without a GPU, never for a measurement.

## Running it in the lab

1. Publish it on a Windows machine with the .NET 10 SDK:
   `dotnet publish bench/r3-present/PlayMap.Bench.Present.csproj -c Release -r win-x64 --self-contained -o r3-present`.
   The folder holds the exe, PlayMap's Render and ANGLE, and nothing of WARP.
2. Run it on each L1 machine, one display at a time:
   - Put the photodiode at the square.
   - Start PresentMon on the bench's process.
   - Run `--path all --seconds 3600` for L1's hour per path.
3. For L4, run `--path composition` on the RTX node.
4. For each path, the record names:
   - the machine, the driver and the build (the exe's version names the build);
   - the CSV;
   - the PresentMon capture;
   - the photodiode's trace.
5. L1's pass mark compares `d3d12` with `gl`. M1's second half — the engine itself on `PRESENT PATH GL` and `D3D12`, an hour
   each — is the engine's to run, not this bench's.
